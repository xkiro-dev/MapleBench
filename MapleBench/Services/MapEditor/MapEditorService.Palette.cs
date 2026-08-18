using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MapleBench.Models;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// The asset palette: what a map can be built out of, served in placement terms
/// (a tile is a <c>u</c>+<c>no</c> under a set, an object is <c>l0/l1/l2</c>,
/// a background is <c>no</c>+<c>ani</c>) rather than as raw tree paths.
///
/// <para><b>Sets come from <see cref="MapAssetService"/></b>, which unions every
/// open archive that carries the library — the measured split is Obj across
/// Map.wz + Map2.wz (336 sets) and Back across Map001.wz + Map2.wz (466), and a
/// first-match lookup silently loses half of each.</para>
///
/// <para><b>Thumbnails are keyed on the canvas's own <c>_hash</c></b> — content
/// identity, which dedupes the 10,258 linked canvases in Obj for free and
/// survives generation bumps (an edit elsewhere does not evict a picture whose
/// pixels did not change). Two tiers: an LRU in memory and PNG files on disk.
/// <see cref="WzRenderService"/>'s own cache flushes wholesale and is generation
/// -keyed, so it is deliberately not the only layer here — it still serves the
/// canvases that carry no hash (11% measured), where content identity is not
/// available and generation identity is the honest fallback.</para>
///
/// <para>Deliberately absent, both measured: no sprite atlases (0.90-1.05x the
/// bytes of individual PNGs — saves nothing) and no downscaling (a 64px tile
/// thumbnail measured BIGGER than the native tile, 5,204 vs 2,431 bytes, as
/// well as blurrier). Art is served native-size.</para>
/// </summary>
public sealed partial class MapEditorService
{
    #region Sets and entries

    /// <summary>Representative thumbnails per set, resolved once — keyed on the
    /// set's session path, read and written under the session gate.</summary>
    private readonly Dictionary<string, string?> _setThumbPaths = new(StringComparer.Ordinal);

    public List<MapPaletteSetDto> PaletteSets(string kind)
    {
        if (!MapAssetService.Kinds.Contains(kind, StringComparer.Ordinal))
            throw new ArgumentException($"'{kind}' is not a palette kind. Tile, Obj and Back are.");

        List<MapAssetSetDto> sets = _assets.Sets(kind);
        List<MapPaletteSetDto> result = new(sets.Count);
        lock (_session.Gate)
        {
            foreach (MapAssetSetDto set in sets)
            {
                string cacheKey = kind + "|" + set.Path;
                if (!_setThumbPaths.TryGetValue(cacheKey, out string? thumb))
                {
                    try
                    {
                        thumb = RepresentativeThumb(kind, set.Path);
                    }
                    catch (Exception)
                    {
                        thumb = null; // a set that cannot parse still lists, sans picture
                    }
                    _setThumbPaths[cacheKey] = thumb;
                }
                result.Add(new MapPaletteSetDto
                {
                    Name = set.Name,
                    Path = set.Path,
                    Source = set.Source,
                    ThumbPath = thumb,
                });
            }
        }
        return result;
    }

    /// <summary>The first drawable entry of a set — what the set looks like in
    /// the palette grid before it is opened. Caller holds the session gate.</summary>
    private string? RepresentativeThumb(string kind, string setPath)
    {
        if (_session.Resolve(setPath) is not WzImage image)
            return null;
        WzSessionService.EnsureParsed(image);

        switch (kind)
        {
            case "Tile":
                foreach (WzImageProperty group in image.WzProperties)
                {
                    if (string.Equals(group.Name, "info", StringComparison.OrdinalIgnoreCase)
                        || group.WzProperties == null)
                        continue;
                    foreach (WzImageProperty entry in group.WzProperties)
                    {
                        if (!long.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            continue;
                        string path = WzPath.Child(WzPath.Child(setPath, group.Name), entry.Name);
                        MapArtDto meta = Meta(entry, path);
                        if (!meta.Missing)
                            return meta.Path;
                    }
                }
                return null;

            case "Obj":
                foreach (WzImageProperty l0 in image.WzProperties)
                {
                    if (string.Equals(l0.Name, "info", StringComparison.OrdinalIgnoreCase)
                        || l0.WzProperties == null)
                        continue;
                    foreach (WzImageProperty l1 in l0.WzProperties)
                    {
                        if (l1.WzProperties == null)
                            continue;
                        foreach (WzImageProperty l2 in l1.WzProperties)
                        {
                            if (!IsFrameList(l2))
                                continue;
                            string path = WzPath.Child(WzPath.Child(WzPath.Child(
                                setPath, l0.Name), l1.Name), l2.Name);
                            WzImageProperty? frame =
                                l2.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL
                                    ? l2
                                    : l2.WzProperties?.FirstOrDefault(p => p.Name == "0")
                                        ?? l2.WzProperties?.FirstOrDefault();
                            string framePath = frame != null && !ReferenceEquals(frame, l2)
                                ? WzPath.Child(path, frame.Name) : path;
                            MapArtDto meta = Meta(frame, framePath);
                            if (!meta.Missing)
                                return meta.Path;
                        }
                    }
                }
                return null;

            case "Back":
                foreach (string branch in new[] { "back", "ani" })
                {
                    WzImageProperty? container = image.WzProperties.FirstOrDefault(
                        p => string.Equals(p.Name, branch, StringComparison.OrdinalIgnoreCase));
                    if (container?.WzProperties == null)
                        continue;
                    foreach (WzImageProperty entry in container.WzProperties)
                    {
                        if (!long.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            continue;
                        string path = WzPath.Child(WzPath.Child(setPath, container.Name), entry.Name);
                        WzImageProperty? frame = entry;
                        string framePath = path;
                        if (entry.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
                        {
                            frame = entry.WzProperties?.FirstOrDefault(p => p.Name == "0")
                                ?? entry.WzProperties?.FirstOrDefault();
                            if (frame == null)
                                continue;
                            framePath = WzPath.Child(path, frame.Name);
                        }
                        MapArtDto meta = Meta(frame, framePath);
                        if (!meta.Missing)
                            return meta.Path;
                    }
                }
                return null;

            default:
                return null;
        }
    }

    public MapPaletteEntriesDto PaletteEntries(string kind, string setPath, int limit)
    {
        MapPaletteEntriesDto result = new() { Kind = kind, Path = setPath };
        lock (_session.Gate)
        {
            if (_session.Resolve(setPath) is not WzImage image)
                throw new InvalidOperationException($"'{setPath}' is not an asset set image.");
            WzSessionService.EnsureParsed(image);
            result.Set = image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? image.Name[..^4] : image.Name;

            switch (kind)
            {
                case "Tile":
                    ReadTileEntries(image, setPath, result, limit);
                    break;
                case "Obj":
                    ReadObjEntries(image, setPath, result, limit);
                    break;
                case "Back":
                    ReadBackEntries(image, setPath, result, limit);
                    break;
                default:
                    throw new ArgumentException($"'{kind}' is not a palette kind.");
            }
        }
        return result;
    }

    /// <summary>Tile/&lt;set&gt;.img/&lt;u&gt;/&lt;no&gt; — u is one of the 11
    /// measured variants, no is the index, and the canvas may carry its own
    /// foothold Convex (the auto-foothold mechanism).</summary>
    private static void ReadTileEntries(WzImage image, string setPath, MapPaletteEntriesDto result, int limit)
    {
        foreach (WzImageProperty group in image.WzProperties)
        {
            if (string.Equals(group.Name, "info", StringComparison.OrdinalIgnoreCase)
                || group.WzProperties == null)
                continue;
            string groupPath = WzPath.Child(setPath, group.Name);
            foreach (WzImageProperty entry in group.WzProperties)
            {
                if (!long.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long no))
                    continue;
                result.Total++;
                if (result.Entries.Count >= limit) { result.Truncated = true; continue; }

                string path = WzPath.Child(groupPath, entry.Name);
                MapArtDto meta = Meta(entry, path);
                result.Entries.Add(new MapPaletteEntryDto
                {
                    U = group.Name,
                    No = no,
                    ThumbPath = meta.Missing ? null : meta.Path,
                    W = meta.W,
                    H = meta.H,
                    Ox = meta.Ox,
                    Oy = meta.Oy,
                    Frames = 1,
                    HasFoothold = FindFootholdGeometry(entry) != null,
                });
            }
        }
    }

    /// <summary>Obj/&lt;set&gt;.img/&lt;l0&gt;/&lt;l1&gt;/&lt;l2&gt;[/&lt;l3&gt;]
    /// — the leaf is a frame list; frame 0 is the thumbnail and the placement
    /// preview.</summary>
    private static void ReadObjEntries(WzImage image, string setPath, MapPaletteEntriesDto result, int limit)
    {
        foreach (WzImageProperty l0 in image.WzProperties)
        {
            if (string.Equals(l0.Name, "info", StringComparison.OrdinalIgnoreCase)
                || l0.WzProperties == null)
                continue;
            foreach (WzImageProperty l1 in l0.WzProperties)
            {
                if (l1.WzProperties == null)
                    continue;
                foreach (WzImageProperty l2 in l1.WzProperties)
                {
                    // Usually l2 is the entry (a frame list). Two maps use a
                    // fourth segment; when the l2 node's children are themselves
                    // containers of frames, descend one more level as l3.
                    if (IsFrameList(l2))
                    {
                        AddObjEntry(result, limit, setPath, l0.Name, l1.Name, l2.Name, null, l2);
                    }
                    else if (l2.WzProperties != null)
                    {
                        foreach (WzImageProperty l3 in l2.WzProperties)
                        {
                            if (IsFrameList(l3))
                                AddObjEntry(result, limit, setPath, l0.Name, l1.Name, l2.Name, l3.Name, l3);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Whether a node is a frame list — numbered drawable children (or
    /// is itself drawable). Distinguishes an obj entry from an l3 level.</summary>
    private static bool IsFrameList(WzImageProperty node)
    {
        if (node.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL)
            return true;
        if (node.WzProperties == null || node.WzProperties.Count == 0)
            return false;
        WzImageProperty first = node.WzProperties.FirstOrDefault(p => p.Name == "0")
            ?? node.WzProperties[0];
        return first.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL;
    }

    private static void AddObjEntry(
        MapPaletteEntriesDto result, int limit, string setPath,
        string l0, string l1, string l2, string? l3, WzImageProperty leaf)
    {
        result.Total++;
        if (result.Entries.Count >= limit) { result.Truncated = true; return; }

        WzImageProperty? frame = leaf.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL
            ? leaf
            : leaf.WzProperties?.FirstOrDefault(p => p.Name == "0") ?? leaf.WzProperties?.FirstOrDefault();
        int frames = leaf.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL
            ? 1
            : leaf.WzProperties?.Count(p => p.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL) ?? 0;

        string path = setPath;
        foreach (string segment in new[] { l0, l1, l2, l3 }.Where(s => s != null))
            path = WzPath.Child(path, segment!);
        string framePath = frame != null && !ReferenceEquals(frame, leaf)
            ? WzPath.Child(path, frame.Name) : path;

        MapArtDto meta = Meta(frame, framePath);
        result.Entries.Add(new MapPaletteEntryDto
        {
            L0 = l0,
            L1 = l1,
            L2 = l2,
            L3 = l3,
            ThumbPath = meta.Missing ? null : meta.Path,
            W = meta.W,
            H = meta.H,
            Ox = meta.Ox,
            Oy = meta.Oy,
            Frames = Math.Max(frames, 1),
            HasFoothold = frame != null && FindFootholdGeometry(frame) != null,
        });
    }

    /// <summary>Back/&lt;set&gt;.img/{back,ani,spine}/&lt;no&gt; — which branch a
    /// placement draws from is its own <c>ani</c> flag (0/1/2), not the set's.</summary>
    private static void ReadBackEntries(WzImage image, string setPath, MapPaletteEntriesDto result, int limit)
    {
        foreach ((string branch, long ani) in new[] { ("back", 0L), ("ani", 1L), ("spine", 2L) })
        {
            WzImageProperty? container = image.WzProperties.FirstOrDefault(
                p => string.Equals(p.Name, branch, StringComparison.OrdinalIgnoreCase));
            if (container?.WzProperties == null)
                continue;
            string branchPath = WzPath.Child(setPath, container.Name);
            foreach (WzImageProperty entry in container.WzProperties)
            {
                if (!long.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long no))
                    continue;
                result.Total++;
                if (result.Entries.Count >= limit) { result.Truncated = true; continue; }

                string path = WzPath.Child(branchPath, entry.Name);
                WzImageProperty? frame = entry;
                string framePath = path;
                if (entry.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
                {
                    frame = entry.WzProperties?.FirstOrDefault(p => p.Name == "0")
                        ?? entry.WzProperties?.FirstOrDefault();
                    if (frame != null)
                        framePath = WzPath.Child(path, frame.Name);
                }
                MapArtDto meta = Meta(frame, framePath);
                result.Entries.Add(new MapPaletteEntryDto
                {
                    No = no,
                    Ani = ani,
                    ThumbPath = meta.Missing ? null : meta.Path,
                    W = meta.W,
                    H = meta.H,
                    Ox = meta.Ox,
                    Oy = meta.Oy,
                    Frames = entry.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL
                        ? 1
                        : Math.Max(1, entry.WzProperties?.Count(
                            p => p.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL) ?? 1),
                });
            }
        }
    }

    #endregion

    #region Life and reactor palettes

    public MapLifePaletteDto LifePalette(string? query, string type, int limit)
    {
        bool npc = string.Equals(type, "n", StringComparison.OrdinalIgnoreCase);
        MapLifePaletteDto dto = new() { NamesAvailable = _strings.IsAvailable };

        List<(string Kind, int Id, string Name)> rows =
            _strings.Search(query ?? "", npc ? "npc" : "mob", limit + 1);
        if (rows.Count > limit)
        {
            dto.Truncated = true;
            rows = rows.Take(limit).ToList();
        }

        lock (_session.Gate)
        {
            // Every archive of the family, not the first: a v232 client keeps
            // Mossy Snail in Mob.wz and Throwback Snail in Mob001.wz, and a
            // first-match lookup renders half the palette as "no picture".
            List<(OpenFile File, WzDirectory Root)> libraries =
                FindRootImageArchives(npc ? "Npc" : "Mob");
            dto.IconsAvailable = libraries.Count > 0;
            foreach ((string _, int id, string name) in rows)
            {
                string? iconPath = null;
                string imageName = id.ToString("D7", CultureInfo.InvariantCulture) + ".img";
                foreach ((OpenFile file, WzDirectory root) in libraries)
                {
                    WzImage? image = root.GetImageByName(imageName);
                    if (image != null)
                    {
                        iconPath = WzPath.Join(
                            _session.RoleRootPath(file, npc ? "Npc" : "Mob"),
                            image.Name, "stand", "0");
                        break;
                    }
                }
                dto.Rows.Add(new MapLifeRowDto
                {
                    Id = id,
                    Type = npc ? "n" : "m",
                    Name = name,
                    IconPath = iconPath,
                });
            }
        }
        return dto;
    }

    public MapReactorPaletteDto ReactorPalette(string? query, int limit)
    {
        MapReactorPaletteDto dto = new();
        lock (_session.Gate)
        {
            (OpenFile File, WzDirectory Root)? library = FindRootImageArchive("Reactor");
            if (library == null)
            {
                dto.Reason = "Reactor.wz is not open. The reactor palette lists reactors out of it; "
                    + "open it (a copy) to place reactors by picture rather than by typed id.";
                return dto;
            }
            dto.Available = true;
            string? q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            foreach (WzImage image in library.Value.Root.WzImages)
            {
                string stem = image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                    ? image.Name[..^4] : image.Name;
                if (q != null && !stem.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (dto.Rows.Count >= limit) { dto.Truncated = true; break; }
                dto.Rows.Add(new MapReactorRowDto
                {
                    Id = stem.TrimStart('0').Length == 0 ? "0" : stem.TrimStart('0'),
                    IconPath = WzPath.Join(library.Value.File.Id, image.Name, "0", "0"),
                });
            }
        }
        return dto;
    }

    /// <summary>The first open archive of a family whose root holds images
    /// directly (Mob.wz, Npc.wz, Reactor.wz are all this shape).</summary>
    private (OpenFile File, WzDirectory Root)? FindRootImageArchive(string family)
    {
        List<(OpenFile File, WzDirectory Root)> all = FindRootImageArchives(family);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Every open archive of the family, in session order — the
    /// family's images are split across Mob.wz / Mob001.wz / Mob2.wz and a
    /// lookup that stops at the first archive loses the rest.</summary>
    private List<(OpenFile File, WzDirectory Root)> FindRootImageArchives(string family)
    {
        List<(OpenFile, WzDirectory)> result = new();
        foreach (OpenFile file in Ordered(_session.SelectRoleSources(family)))
        {
            WzDirectory? root = _session.RoleRoot(file, family);
            if (root != null && root.WzImages.Count > 0)
                result.Add((file, root));
        }
        return result;
    }

    #endregion

    #region Thumbnails — content-addressed, two tiers

    private const int ThumbMemoryMaxEntries = 8_000;
    private const long ThumbMemoryMaxBytes = 48L * 1024 * 1024;

    private readonly object _thumbGate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Png)>> _thumbIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[] Png)> _thumbOrder = new();
    private long _thumbBytes;
    private string? _thumbDiskDir;
    private bool _thumbDiskFailed;

    /// <summary>
    /// A palette thumbnail: the canvas's pixels as PNG, plus the content hash it
    /// was cached under (null when the canvas carries no <c>_hash</c> and the
    /// generation-keyed render cache served it instead).
    /// </summary>
    public (byte[] Png, string? Hash)? Thumb(string path)
    {
        // Step 1, under the session gate: find the canvas and read its own
        // _hash. The hash of a linking canvas matches the linked pixels — that
        // is what makes content addressing dedupe the 10,258 links for free.
        string? hash = null;
        lock (_session.Gate)
        {
            WzObject? node = _session.Resolve(path);
            WzCanvasProperty? canvas = node as WzCanvasProperty;
            if (canvas == null && node is WzUOLProperty uol && uol.LinkValue is WzCanvasProperty linked)
                canvas = linked;
            if (canvas?["_hash"] is WzStringProperty hashProperty
                && !string.IsNullOrWhiteSpace(hashProperty.Value))
            {
                hash = hashProperty.Value;
            }
        }

        if (hash != null)
        {
            byte[]? cached = ThumbFromCache(hash);
            if (cached != null)
                return (cached, hash);
        }

        // Miss: render. The PNG encode is outside the session gate inside
        // RenderCanvasPng, so parallel palette loads do not queue on one lock.
        byte[]? png = hash != null
            ? _render.RenderCanvasPng(path)
            : _render.RenderCanvasPngCached(path);
        if (png == null)
            return null;

        if (hash != null)
            ThumbStore(hash, png);
        return (png, hash);
    }

    private byte[]? ThumbFromCache(string hash)
    {
        lock (_thumbGate)
        {
            if (_thumbIndex.TryGetValue(hash, out LinkedListNode<(string Key, byte[] Png)>? node))
            {
                _thumbOrder.Remove(node);
                _thumbOrder.AddFirst(node);
                return node.Value.Png;
            }
        }

        // Disk tier — survives restarts and the memory LRU, keyed by content.
        string? file = ThumbDiskPath(hash);
        if (file != null && File.Exists(file))
        {
            try
            {
                byte[] png = File.ReadAllBytes(file);
                ThumbStoreMemory(hash, png);
                return png;
            }
            catch (IOException) { /* a corrupt or locked file re-renders */ }
        }
        return null;
    }

    private void ThumbStore(string hash, byte[] png)
    {
        ThumbStoreMemory(hash, png);
        string? file = ThumbDiskPath(hash);
        if (file == null)
            return;
        try
        {
            if (!File.Exists(file))
            {
                string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, png);
                try { File.Move(temp, file); }
                catch (IOException) { File.Delete(temp); } // another request won the race
            }
        }
        catch (Exception)
        {
            _thumbDiskFailed = true; // memory tier still works; stop retrying disk
        }
    }

    private void ThumbStoreMemory(string hash, byte[] png)
    {
        lock (_thumbGate)
        {
            if (_thumbIndex.ContainsKey(hash))
                return;
            LinkedListNode<(string, byte[])> node = _thumbOrder.AddFirst((hash, png));
            _thumbIndex[hash] = node;
            _thumbBytes += png.Length;
            while (_thumbIndex.Count > ThumbMemoryMaxEntries || _thumbBytes > ThumbMemoryMaxBytes)
            {
                LinkedListNode<(string Key, byte[] Png)>? last = _thumbOrder.Last;
                if (last == null)
                    break;
                _thumbOrder.RemoveLast();
                _thumbIndex.Remove(last.Value.Key);
                _thumbBytes -= last.Value.Png.Length;
            }
        }
    }

    private string? ThumbDiskPath(string hash)
    {
        if (_thumbDiskFailed)
            return null;
        if (_thumbDiskDir == null)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MapleBench", "mapthumbs");
                Directory.CreateDirectory(dir);
                _thumbDiskDir = dir;
            }
            catch (Exception)
            {
                _thumbDiskFailed = true;
                return null;
            }
        }

        // A _hash is not guaranteed to be a file name; sanitise without folding
        // two distinct hashes together (bad characters become their code point).
        System.Text.StringBuilder name = new(hash.Length + 8);
        foreach (char c in hash)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                name.Append(c);
            else
                name.Append('%').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        if (name.Length > 120)
            name.Length = 120;
        return Path.Combine(_thumbDiskDir, name + ".png");
    }

    #endregion

    #region Foothold geometry on art

    /// <summary>
    /// The foothold geometry a piece of art carries, as vertex chains, or null.
    /// <c>Tile/&lt;set&gt;.img/&lt;u&gt;/&lt;no&gt;/foothold</c> is a Convex of
    /// Vectors; obj frames carry either a single Convex or a container of
    /// numbered Convexes. This is the measured mechanism footholds are
    /// auto-generated from.
    /// </summary>
    private static List<List<(int X, int Y)>>? FindFootholdGeometry(WzImageProperty artNode)
    {
        WzImageProperty? holder = artNode;
        if (holder is WzUOLProperty uol)
            holder = uol.LinkValue as WzImageProperty;
        if (holder?.WzProperties == null)
            return null;

        WzImageProperty? foothold = holder.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "foothold", StringComparison.OrdinalIgnoreCase));
        if (foothold == null)
            return null;

        List<List<(int X, int Y)>> chains = new();
        if (foothold is WzConvexProperty convex)
        {
            List<(int, int)>? chain = ReadVectorChain(convex);
            if (chain is { Count: >= 2 })
                chains.Add(chain);
        }
        else if (foothold.WzProperties != null)
        {
            foreach (WzImageProperty child in foothold.WzProperties)
            {
                if (child is WzConvexProperty inner)
                {
                    List<(int, int)>? chain = ReadVectorChain(inner);
                    if (chain is { Count: >= 2 })
                        chains.Add(chain);
                }
            }
        }
        return chains.Count > 0 ? chains : null;
    }

    private static List<(int X, int Y)>? ReadVectorChain(WzConvexProperty convex)
    {
        if (convex.WzProperties == null)
            return null;
        List<(int, int)> points = new();
        foreach (WzImageProperty child in convex.WzProperties)
        {
            if (child is WzVectorProperty vector)
                points.Add((vector.X?.Value ?? 0, vector.Y?.Value ?? 0));
        }
        return points;
    }

    #endregion
}
