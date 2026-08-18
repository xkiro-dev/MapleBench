using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using MapleBench.Models;
using MapleBench.Services.MapModel;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Minimap regeneration. Measured facts this stands on: <c>miniMap/mag</c> is
/// always 4; <b>1,974 minimaps are pure <c>_outlink</c>s to another map's
/// minimap</b>, so a canvas that other maps draw through is never overwritten
/// without the sharers being named and the overwrite confirmed; 939 geometry
/// maps ship no minimap at all, so absence is a legal starting state.
/// </summary>
public sealed partial class MapEditorService
{
    /// <summary>mag is 4 on 7,130 of 7,130 measured minimaps.</summary>
    private const int MinimapMag = 4;

    /// <summary>Canvas pixels are floor(width / 16) x floor(height / 16) on
    /// 7,660 of 7,671 measured minimaps (99.86%) — the divisor is 2^mag, not
    /// mag, and it is floor, not ceil or round.</summary>
    private const int MinimapDivisor = 16;

    /// <summary>Refuse a render bigger than this — a canvas nobody can use is
    /// not worth an out-of-memory.</summary>
    private const long MaxMinimapPixels = 4096L * 4096L;

    private static readonly string[] MinimapMetricNames =
        { "canvas", "width", "height", "centerX", "centerY", "mag" };

    #region Plan

    /// <summary>
    /// Everything the user must know BEFORE a regeneration writes: whether the
    /// current canvas is a link (regenerating detaches it), and which other maps
    /// share this map's canvas (overwriting changes what they draw). The sharer
    /// scan walks every map image in the session — measured ~10s on the full
    /// 17,442 — so it runs here, in the plan, where waiting buys certainty,
    /// never silently inside the write.
    /// </summary>
    public MapMinimapPlanDto MinimapPlan(string path)
    {
        MapDocDto dto;
        MapMinimapPlanDto plan;
        int? mapId;
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            doc.Touched = DateTime.UtcNow;
            dto = BuildDto(doc);
            mapId = doc.Doc.MapId;

            WzNode? miniMap = doc.Doc.Find("miniMap");
            WzNode? canvas = miniMap?.Child("canvas");

            plan = new MapMinimapPlanDto
            {
                HasMiniMap = miniMap != null,
                HasCanvas = canvas != null,
                Mag = MinimapMag,
            };

            if (canvas != null)
            {
                string? link = canvas.Child("_outlink")?.AsText()
                    ?? canvas.Child("_inlink")?.AsText();
                if (canvas.Type == WzPropertyType.UOL)
                    link ??= canvas.Text;
                if (link != null)
                {
                    plan.CanvasIsLink = true;
                    plan.LinkTarget = link;
                }
            }

            (long width, long height, long cx, long cy, int cw, int ch) = MinimapMetrics(dto);
            plan.Width = width;
            plan.Height = height;
            plan.CenterX = cx;
            plan.CenterY = cy;
            plan.CanvasW = cw;
            plan.CanvasH = ch;
        }

        // The sharers: maps whose miniMap/canvas outlinks INTO this map. The
        // scan parses every map image and takes the SESSION gate internally —
        // it cannot leave that gate, because parsing mutates the shared tree.
        // What it no longer holds is the EDITOR gate: the plan above read
        // everything it needed from the document, so edits, placements and
        // undo stay responsive through the scan's ~45s. (Session-gated work —
        // thumbnails, canvas renders — still queues behind it; that is the
        // scan's real cost and it is stated in the dialog.)
        if (mapId is int id)
        {
            List<string> sharers = FindMinimapSharers(path, id, out bool scanned, out int count);
            plan.SharersScanned = scanned;
            plan.Sharers = sharers;
            plan.SharerCount = count;
        }
        return plan;
    }

    /// <summary>
    /// Every map whose <c>miniMap/canvas/_outlink</c> names this map's canvas.
    /// The link dialect is an archive-rooted path with no .wz —
    /// <c>Map/Map/Map9/910023000.img/miniMap/canvas</c> — so the match is on the
    /// <c>Map&lt;d&gt;/&lt;id&gt;.img/miniMap/canvas</c> suffix, case-insensitive
    /// like the client's own resolution.
    /// </summary>
    private List<string> FindMinimapSharers(string selfPath, int mapId, out bool scanned, out int count)
    {
        string suffix = $"Map{mapId.ToString(CultureInfo.InvariantCulture)[0]}/"
            + $"{mapId.ToString(CultureInfo.InvariantCulture).PadLeft(9, '0')}.img/miniMap/canvas";
        List<string> sharers = new();
        count = 0;
        scanned = false;

        lock (_session.Gate)
        {
            foreach ((OpenFile _, WzDirectory mapRoot, string rootPath) in MapDirectories())
            {
                scanned = true;
                foreach (WzDirectory sub in mapRoot.WzDirectories)
                {
                    if (sub.Name.Length != 4 || !sub.Name.StartsWith("Map", StringComparison.Ordinal))
                        continue;
                    foreach (WzImage image in sub.WzImages)
                    {
                        string imagePath = WzPath.Child(WzPath.Child(rootPath, sub.Name), image.Name);
                        if (string.Equals(imagePath, selfPath, StringComparison.Ordinal))
                            continue;

                        bool wasParsed = image.Parsed;
                        try
                        {
                            if (!image.ParseImage())
                                continue;
                            if (image.GetFromPath("miniMap/canvas") is not WzCanvasProperty canvas)
                                continue;
                            if (canvas["_outlink"] is not WzStringProperty outlink
                                || outlink.Value == null
                                || !outlink.Value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                                continue;

                            count++;
                            if (sharers.Count < 25)
                            {
                                string stem = image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                                    ? image.Name[..^4] : image.Name;
                                string? name = int.TryParse(stem, out int sharerId)
                                    ? _strings.GetMapName(sharerId) : null;
                                sharers.Add(name != null ? $"{stem} ({name})" : stem);
                            }
                        }
                        finally
                        {
                            // Leave the session as found: unparse only what this
                            // scan parsed, and never an image holding edits.
                            if (!wasParsed && !image.Changed)
                                image.UnparseImage();
                        }
                    }
                }
            }
        }
        return sharers;
    }

    #endregion

    #region Regenerate

    public MapMinimapResultDto MinimapRegenerate(MapMinimapRegenRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;
            MapDocDto dto = BuildDto(doc);

            WzNode? oldMiniMap = doc.Doc.Find("miniMap");
            WzNode? oldCanvas = oldMiniMap?.Child("canvas");

            // Refusals BEFORE the write — the whole point of the plan step.
            if (oldCanvas != null)
            {
                bool isLink = oldCanvas.Child("_outlink") != null
                    || oldCanvas.Child("_inlink") != null
                    || oldCanvas.Type == WzPropertyType.UOL;
                if (isLink && !request.ConfirmDetachLink)
                {
                    throw new InvalidOperationException(
                        "This map's minimap canvas is a link to another map's picture — one of the "
                        + "1,974 shared minimaps. Regenerating replaces the link with this map's own "
                        + "picture. That detaches the sharing (the other map keeps its canvas); "
                        + "confirm to proceed (confirmDetachLink).");
                }
            }

            if (doc.Doc.MapId is int id && oldCanvas != null && oldCanvas.Payload != null
                && !request.ConfirmSharedOverwrite)
            {
                List<string> sharers = FindMinimapSharers(request.Path, id, out bool scanned, out int count);
                if (scanned && count > 0)
                {
                    string named = string.Join(", ", sharers.Take(10));
                    throw new InvalidOperationException(
                        $"{count} other map{(count == 1 ? "" : "s")} draw{(count == 1 ? "s" : "")} "
                        + $"this map's minimap through an _outlink: {named}"
                        + (count > sharers.Count ? $" and {count - sharers.Count} more" : "")
                        + ". Overwriting this canvas changes what every one of them shows. Confirm "
                        + "to proceed (confirmSharedOverwrite).");
                }
            }

            (long width, long height, long centerX, long centerY, int canvasW, int canvasH) =
                MinimapMetrics(dto);
            if ((long)canvasW * canvasH > MaxMinimapPixels)
            {
                throw new InvalidOperationException(
                    $"A regenerated minimap for this map would be {canvasW}x{canvasH} px — beyond "
                    + "what the client or this tool can sensibly hold. The map's bounds are the "
                    + "cause; check VR before regenerating.");
            }

            // Encoded as Format1 (BGRA4444) because that is what 7,683 of 7,683
            // measured minimap canvases use — letting the codec pick a format
            // the client has never seen on a minimap would be a guess.
            WzPngProperty png = new();
            using (Bitmap rendered = RenderMinimapBitmap(dto, centerX, centerY, canvasW, canvasH))
            {
                MbPngCodec.EncodeResult encoded = MbPngCodec.Encode(rendered, WzPngFormat.Format1);
                png.SetCompressedBytes(encoded.Compressed, rendered.Width, rendered.Height, encoded.Format);
            }

            // Build the replacement miniMap node: the six metric children are
            // written fresh; every OTHER child the old node carried is kept —
            // an unmodelled key inside miniMap is data, not noise.
            WzNode newMiniMap = WzNode.Container("miniMap");
            newMiniMap.Add(WzNode.Canvas("canvas", png));
            newMiniMap.Add(WzNode.Scalar("width", WzPropertyType.Int, width));
            newMiniMap.Add(WzNode.Scalar("height", WzPropertyType.Int, height));
            newMiniMap.Add(WzNode.Scalar("centerX", WzPropertyType.Int, centerX));
            newMiniMap.Add(WzNode.Scalar("centerY", WzPropertyType.Int, centerY));
            newMiniMap.Add(WzNode.Scalar("mag", WzPropertyType.Int, MinimapMag));
            if (oldMiniMap != null)
            {
                foreach (WzNode child in oldMiniMap.Children)
                {
                    if (!MinimapMetricNames.Contains(child.Name, StringComparer.Ordinal))
                        newMiniMap.Add(child);
                }
            }

            MapMinimapResultDto result = new()
            {
                CanvasW = canvasW,
                CanvasH = canvasH,
                Width = width,
                Height = height,
                CenterX = centerX,
                CenterY = centerY,
            };

            if (oldMiniMap != null)
            {
                // The replaced node leaves the document for the undo stack,
                // where the save's payload sweep cannot see it. Pin and detach
                // its canvas bytes now, while its reader is still open, so
                // undoing the regeneration after a save restores real pixels.
                lock (_session.Gate)
                {
                    _ = PinPayloads(oldMiniMap, "miniMap") ?? DetachPayloads(oldMiniMap, "miniMap");
                }

                int index = IndexOfTopLevel(doc, oldMiniMap);
                doc.Doc.RemoveNode(oldMiniMap);
                doc.Doc.InsertNode(index, newMiniMap);
                doc.Push(new Change("regenerate minimap",
                    apply: () =>
                    {
                        int at = IndexOfTopLevel(doc, oldMiniMap);
                        doc.Doc.RemoveNode(oldMiniMap);
                        doc.Doc.InsertNode(at, newMiniMap);
                    },
                    revert: () =>
                    {
                        int at = IndexOfTopLevel(doc, newMiniMap);
                        doc.Doc.RemoveNode(newMiniMap);
                        doc.Doc.InsertNode(at, oldMiniMap);
                    }));
                result.Notes.Add("The previous minimap is on the undo stack, exactly as it was.");
            }
            else
            {
                int at = doc.Doc.Nodes.Count;
                doc.Doc.InsertNode(at, newMiniMap);
                doc.Push(new Change("add minimap",
                    apply: () => doc.Doc.InsertNode(Math.Min(at, doc.Doc.Nodes.Count), newMiniMap),
                    revert: () => doc.Doc.RemoveNode(newMiniMap)));
                result.Notes.Add("This map had no minimap (939 geometry maps ship without one); it has one now.");
            }

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    private static int IndexOfTopLevel(EditorDoc doc, WzNode node)
    {
        for (int i = 0; i < doc.Doc.Nodes.Count; i++)
        {
            if (ReferenceEquals(doc.Doc.Nodes[i], node))
                return i;
        }
        return doc.Doc.Nodes.Count;
    }

    /// <summary>
    /// The metric set a regeneration writes, derived from the map's camera
    /// bound (VR, or the computed fallback the 1,151 VR-less maps get).
    /// Measured across 7,130 shipping minimaps: width/height/centerX/centerY
    /// are NOT a fixed function of VR — but the single most common relation
    /// (and the safe one to write) is exactly width = VR extent and centerX =
    /// -VRLeft (modal K = 0 on every one of the four), with the canvas at
    /// floor(dim / 16).
    /// </summary>
    private static (long Width, long Height, long CenterX, long CenterY, int CanvasW, int CanvasH)
        MinimapMetrics(MapDocDto dto)
    {
        MapBoundsDto bounds = dto.Bounds
            ?? throw new InvalidOperationException(
                "This map has no VR bound and no geometry to compute one from, so there is "
                + "nothing to render a minimap of.");

        long width = bounds.Right - bounds.Left;
        long height = bounds.Bottom - bounds.Top;
        long centerX = -bounds.Left;
        long centerY = -bounds.Top;
        int canvasW = (int)Math.Max(1, width / MinimapDivisor);
        int canvasH = (int)Math.Max(1, height / MinimapDivisor);
        return (width, height, centerX, centerY, canvasW, canvasH);
    }

    /// <summary>
    /// Renders tiles and objects (frame 0) at 1/16 — the measured canvas scale
    /// — into the minimap surface. Backgrounds are deliberately excluded — a
    /// minimap shows the walkable world, not the sky — and this is an art
    /// render, not a re-creation of the client's exact styling; both are stated
    /// in the UI.
    /// </summary>
    private Bitmap RenderMinimapBitmap(MapDocDto dto, long centerX, long centerY, int canvasW, int canvasH)
    {
        // Order: within each layer, objects by z (stable WZ order for ties),
        // then tiles by the art's z and their numeric WZ child name. zM groups
        // platform geometry and is deliberately not a draw key. This is the
        // same order the interactive map view draws.
        List<(long X, long Y, long F, string Art, int Mag)> items = new();
        foreach (MapLayerDto layer in dto.Layers)
        {
            int mag = (int)(layer.TSMag ?? 1);
            foreach (MapObjDto o in layer.Objs.OrderBy(o => o.Z))
            {
                if (o.Art != null)
                    items.Add((o.X, o.Y, o.F, o.Art, 1));
            }
            foreach (MapTileDto t in layer.Tiles
                         .OrderBy(t => t.Art != null && dto.Art.TryGetValue(t.Art, out MapArtDto? m) ? m.Z : 0)
                         .ThenBy(t => t.Z))
            {
                if (t.Art != null)
                    items.Add((t.X, t.Y, 0, t.Art, mag <= 0 ? 1 : mag));
            }
        }

        Bitmap surface = new(canvasW, canvasH);
        using Graphics g = Graphics.FromImage(surface);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.Clear(Color.Transparent);

        Dictionary<string, Bitmap?> artBitmaps = new(StringComparer.Ordinal);
        try
        {
            lock (_session.Gate)
            {
                foreach ((long _, long _, long _, string key, int _) in items)
                {
                    if (artBitmaps.ContainsKey(key))
                        continue;
                    Bitmap? bitmap = null;
                    if (dto.Art.TryGetValue(key, out MapArtDto? meta) && meta.Path != null)
                    {
                        try
                        {
                            bitmap = _session.Resolve(meta.Path) switch
                            {
                                WzCanvasProperty canvas => canvas.GetLinkedWzCanvasBitmap(),
                                WzUOLProperty uol when uol.LinkValue is WzCanvasProperty linked =>
                                    linked.GetLinkedWzCanvasBitmap(),
                                _ => null,
                            };
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Minimap render could not decode {Path}", meta.Path);
                        }
                    }
                    artBitmaps[key] = bitmap;
                }
            }

            float scale = 1f / MinimapDivisor;
            foreach ((long x, long y, long f, string key, int mag) in items)
            {
                if (!artBitmaps.TryGetValue(key, out Bitmap? bitmap) || bitmap == null
                    || !dto.Art.TryGetValue(key, out MapArtDto? meta))
                    continue;

                float w = bitmap.Width * mag * scale;
                float h = bitmap.Height * mag * scale;
                float ox = meta.Ox * mag * scale;
                float oy = meta.Oy * mag * scale;
                float dx = (x + centerX) * scale;
                float dy = (y + centerY) * scale;

                if (f == 1)
                {
                    g.TranslateTransform(dx + (w - ox), dy - oy);
                    g.ScaleTransform(-1, 1);
                    g.DrawImage(bitmap, 0, 0, w, h);
                    g.ResetTransform();
                }
                else
                {
                    g.DrawImage(bitmap, dx - ox, dy - oy, w, h);
                }
            }
        }
        finally
        {
            foreach (Bitmap? bitmap in artBitmaps.Values)
                bitmap?.Dispose();
        }
        return surface;
    }

    #endregion
}
