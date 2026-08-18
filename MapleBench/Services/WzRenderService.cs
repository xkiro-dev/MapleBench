using System.Drawing;
using System.Drawing.Imaging;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// Turns canvas and sound nodes into bytes the browser can display or play.
/// </summary>
public sealed class WzRenderService
{
    private readonly WzSessionService _session;
    private readonly ILogger<WzRenderService> _log;

    public WzRenderService(WzSessionService session, ILogger<WzRenderService> log)
    {
        _session = session;
        _log = log;
    }

    #region Encoded PNG cache

    /// <summary>
    /// Cache key -> encoded PNG, or null for "there is no art here".  Guarded by
    /// <see cref="_cacheGate"/>, which is never taken while holding the session
    /// gate the other way round.
    /// </summary>
    private readonly Dictionary<string, byte[]?> _pngCache = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();
    private long _pngCacheBytes;
    private int _pngCacheGeneration = -1;

    /// <summary>
    /// Ceilings on the encoded-PNG cache, whichever bites first.
    ///
    /// Why cache the *encoded* bytes rather than the decoded bitmaps: measured
    /// per thumbnail on a v232 client, a mob costs 7.3 ms and 0.26 MB of
    /// retained bitmap, an NPC 4.6 ms and 0.21 MB, and a skill 24.4 ms and
    /// 1.25 MB — each Skill.wz image is a whole job's book, and the search that
    /// finds one 0.4 KB icon parses all of it. With an icon on every row of the
    /// new section grids that is ~84 s of gate time and ~3.3 GB retained across
    /// the three grids. The same icons as encoded PNGs are ~11 KB each: ~30 MB
    /// for Mob.wz's 2,742 rows against 713 MB of decoded bitmaps.
    ///
    /// The bound is stated rather than implied: 20,000 entries or 96 MB. That
    /// covers every row of the mob, NPC and skill grids of a v232 client at
    /// once. Overflow clears the cache wholesale rather than evicting — the same
    /// choice <see cref="IconService"/> and the resolution cache make, for the
    /// same reason: an eviction policy here would buy a little and give
    /// somewhere new to be wrong, and re-encoding is only ever a delay.
    /// </summary>
    private const int MaxCacheEntries = 20_000;
    private const long MaxCacheBytes = 96L * 1024 * 1024;

    /// <summary>
    /// A canvas rendered to PNG, from the cache when it is there.
    ///
    /// Keyed on <see cref="WzSessionService.Generation"/> as well as the path,
    /// which is what makes it safe against an edit: replacing a canvas goes
    /// through <c>WzEditService.SetCanvasImage</c> -> <c>MarkFileDirty</c> ->
    /// <c>InvalidateResolution</c>, so the generation moves and every key
    /// minted before it is unreachable. An undo, a redo, a delete, a rename and
    /// a save-and-reopen all do the same.
    ///
    /// It is also what lets <see cref="ImageMemoryService"/> release the parsed
    /// images underneath: the icons stay instant because they are no longer
    /// stored as parsed WZ at all.
    /// </summary>
    public byte[]? RenderCanvasPngCached(string path) => Cached("c|" + path, () => RenderCanvasPng(path));

    /// <summary>
    /// Memoises a whole thumbnail lookup — including the "no art below this
    /// node" answer, which is the expensive one to re-derive.
    /// </summary>
    public byte[]? ThumbCached(string path, Func<byte[]?> produce) => Cached("t|" + path, produce);

    private byte[]? Cached(string key, Func<byte[]?> produce)
    {
        int generation = _session.Generation;

        lock (_cacheGate)
        {
            if (_pngCacheGeneration != generation)
                ResetCache(generation);
            if (_pngCache.TryGetValue(key, out byte[]? hit))
                return hit;
        }

        // Outside _cacheGate on purpose: this takes the session gate and can
        // parse a whole image, and holding the cache lock across it would
        // serialise every other row's cache hit behind one miss.
        byte[]? png = produce();

        lock (_cacheGate)
        {
            // The tree moved while we were decoding, so this answer describes a
            // state that no longer exists. Return it to the caller who asked --
            // it is what they would have got anyway -- but do not file it.
            if (_pngCacheGeneration != generation)
                return png;

            if (_pngCache.Count >= MaxCacheEntries || _pngCacheBytes >= MaxCacheBytes)
                ResetCache(generation);

            if (_pngCache.TryAdd(key, png))
                _pngCacheBytes += png?.Length ?? 0;
        }
        return png;
    }

    private void ResetCache(int generation)
    {
        _pngCache.Clear();
        _pngCacheBytes = 0;
        _pngCacheGeneration = generation;
    }

    /// <summary>
    /// Throws the rendered PNGs away and reports how many bytes that was.
    ///
    /// For <see cref="MemoryPressureService"/>: this is the cheapest 96 MB in
    /// the process to give back, because every entry is reproducible from the
    /// archive and the only cost of losing one is redrawing a thumbnail the user
    /// may never look at again.
    /// </summary>
    public long DropCache()
    {
        lock (_cacheGate)
        {
            long bytes = _pngCacheBytes;
            ResetCache(_pngCacheGeneration);
            return bytes;
        }
    }

    /// <summary>What the cache is holding, for the memory endpoint.</summary>
    public (int Entries, long Bytes) CacheStats()
    {
        lock (_cacheGate)
            return (_pngCache.Count, _pngCacheBytes);
    }

    #endregion

    /// <summary>
    /// Renders a canvas to PNG, following '_inlink'/'_outlink' so linked
    /// canvases show the sprite they point at instead of an empty box.
    /// Returns null when the node has no drawable image.
    /// </summary>
    public byte[]? RenderCanvasPng(string path)
    {
        // The gate covers the resolve and the decode, and stops there.
        //
        // Everything inside it touches MapleLib: walking the path, following an
        // '_inlink'/'_outlink' to another canvas, and inflating the pixels out of
        // the archive's shared reader. The PNG encode that used to sit in here
        // touches none of it -- GDI+ is compressing a bitmap this method owns
        // outright, because every path into ExtractBitmap ends at
        // WzPngProperty.GetImage(false), which returns either a freshly decoded
        // bitmap or a clone of the cached one. Nothing else in the process can
        // reach it, which is also why disposing it below has always been correct.
        //
        // Measured on a v232 client, 250 cold canvases out of Map.wz/Obj at the
        // five-way concurrency media.js uses: 550 ms with the encode inside the
        // gate, 5 threads taking turns at one lock; the same 250 in 205 ms with
        // it outside. The encode is most of the wall time of a thumbnail grid and
        // it is the half that has no reason to be exclusive.
        Bitmap? bitmap;
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(path);
            bitmap = ExtractBitmap(node);
        }

        if (bitmap == null)
            return null;

        // MapleLib hands back a fresh Bitmap per call; without this the
        // GDI+ handles pile up until the process runs out of them.
        using (bitmap)
        using (MemoryStream stream = new())
        {
            try
            {
                bitmap.Save(stream, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to encode canvas at {Path}", path);
                return null;
            }
            return stream.ToArray();
        }
    }

    private Bitmap? ExtractBitmap(WzObject node)
    {
        try
        {
            switch (node)
            {
                case WzCanvasProperty canvas:
                    return canvas.GetLinkedWzCanvasBitmap();
                case WzPngProperty png:
                    return png.GetImage(false);
                case WzUOLProperty uol when uol.LinkValue is WzCanvasProperty linked:
                    return linked.GetLinkedWzCanvasBitmap();
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not decode canvas '{Name}'", node.Name);
            return null;
        }
    }

    /// <summary>Raw audio bytes plus the extension MapleLib reports for them.</summary>
    public (byte[] Data, string ContentType, string Extension)? GetAudio(string path)
    {
        lock (_session.Gate)
        {
            if (_session.Resolve(path) is not WzBinaryProperty sound)
                return null;

            byte[] data = sound.GetBytes(false);
            if (data == null || data.Length == 0)
                return null;

            string extension = sound.FileExtension;
            string contentType = extension == ".wav" ? "audio/wav" : "audio/mpeg";
            return (data, contentType, extension);
        }
    }

    /// <summary>
    /// Bytes behind any binary-ish node, for the "download raw" action and for
    /// the hex inspector.
    /// </summary>
    public byte[]? GetRawBytes(string path)
    {
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(path);
            try
            {
                return node switch
                {
                    WzBinaryProperty sound => sound.GetBytes(false),
                    WzRawDataProperty raw => raw.GetBytes(),
                    WzLuaProperty lua => lua.Value,
                    WzPngProperty png => png.GetCompressedBytes(false),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not read raw bytes at {Path}", path);
                return null;
            }
        }
    }
}
