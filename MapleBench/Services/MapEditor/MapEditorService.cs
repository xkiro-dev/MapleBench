using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MapleBench.Models;
using MapleBench.Services.MapModel;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// The map editor's server half: open a map into the round-trip model, serve it
/// shaped for a renderer, apply small safe edits, and save it back through the
/// same model.
///
/// <para><b>Every map goes through <see cref="MapRoundTrip.LoadVerified"/> and
/// nothing else.</b> A map the model cannot write back unchanged is refused with
/// the node that differs, never opened partially — a partial open plus a save is
/// a damaged map. All 17,442 v232 images pass today, so the refusal arm is
/// expected to fire never; it exists for the first map nobody predicted.</para>
///
/// <para><b>Edits are edits to the model, not to the archive.</b> The document
/// lives here between open and save; the session's tree is untouched until
/// <see cref="Save"/> rebuilds the image from the model and writes it through
/// <see cref="WzSaveService"/>. The write is then verified against the saved and
/// reopened archive — the model's own bytes against the archive's — because a
/// counter is not a verification.</para>
///
/// <para><b>The rules this file inherits from the measurements</b> (see
/// <c>docs/map-data-model.md</c>): layers are read from the image, never 0-7
/// (<c>749080500.img</c> has an 8); values are written through the
/// type-preserving setters (26 shapes are genuinely mixed-type); names are
/// ordinal and never trimmed (<c>speedMaxOver&#160;</c> is a different key);
/// foothold prev/next asymmetry is legal on 6,744 footholds and is never
/// "repaired" — the invariant worth keeping is coincident endpoints, and only
/// when a vertex is dragged.</para>
/// </summary>
public sealed partial class MapEditorService
{
    /// <summary>Open documents are few and heavy; past this the least recently
    /// used is dropped (its unsaved edits with it, so the cap is generous).</summary>
    private const int MaxOpenDocs = 6;

    private const int MaxUndoDepth = 200;

    private readonly WzSessionService _session;
    private readonly WzSaveService _save;
    private readonly StringPoolService _strings;
    private readonly MapAssetService _assets;
    private readonly WzRenderService _render;
    private readonly ILogger<MapEditorService> _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, EditorDoc> _docs = new(StringComparer.Ordinal);

    public MapEditorService(
        WzSessionService session, WzSaveService save, StringPoolService strings,
        MapAssetService assets, WzRenderService render,
        ILogger<MapEditorService> log)
    {
        _session = session;
        _save = save;
        _strings = strings;
        _assets = assets;
        _render = render;
        _log = log;
    }

    #region Capabilities and the picker

    public MapEditCapabilitiesDto Capabilities()
    {
        MapEditCapabilitiesDto dto = new();
        lock (_session.Gate)
        {
            foreach ((OpenFile file, WzDirectory _, string _) in MapDirectories())
            {
                if (!dto.MapArchives.Contains(file.Name))
                    dto.MapArchives.Add(file.Name);
            }
            foreach (OpenFile file in _session.SelectRoleSources("Map"))
            {
                if (file.LooseImage != null && !dto.MapArchives.Contains(file.Name))
                    dto.MapArchives.Add(file.Name);
            }
            dto.MapCount = ListMapsUnderGate(null, int.MaxValue).Total;
            dto.Available = dto.MapCount > 0;
            dto.PortalIcons = FindHelperImage() != null;
            dto.NpcSprites = FindRootImageArchives("Npc").Count > 0;
            dto.MobSprites = FindRootImageArchives("Mob").Count > 0;
        }
        // Deliberately after the gate: IsAvailable may wait for the string pool
        // build, and holding the session gate over that wait would stall every
        // other request behind a name lookup.
        dto.Names = _strings.IsAvailable;
        return dto;
    }

    /// <summary>
    /// The picker list: map id + name from String.wz. Names come from String.wz
    /// because it is the measured source — <c>info/mapName</c> exists on 20 maps
    /// and is wrong on all of them.
    /// </summary>
    public MapListDto ListMaps(string? query, int limit)
    {
        // Names come from the string pool, whose blocking build must happen
        // outside the session gate (its own documented rule). Warmed here so the
        // picker's very first listing already has names rather than ids.
        _strings.Warm();
        lock (_session.Gate)
            return ListMapsUnderGate(query, limit);
    }

    private MapListDto ListMapsUnderGate(string? query, int limit)
    {
        MapListDto dto = new();
        HashSet<int> seen = new();
        string? q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        foreach ((OpenFile file, WzDirectory mapRoot, string rootPath) in MapDirectories())
        {
            foreach (WzDirectory sub in mapRoot.WzDirectories)
            {
                // Map0..Map9; anything else at this level is not a map folder.
                if (sub.Name.Length != 4 || !sub.Name.StartsWith("Map", StringComparison.Ordinal)
                    || sub.Name[3] is < '0' or > '9')
                    continue;

                string subPath = WzPath.Child(rootPath, sub.Name);
                foreach (WzImage image in sub.WzImages)
                {
                    string stem = image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                        ? image.Name[..^4] : image.Name;
                    if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                        continue;
                    if (!seen.Add(id))
                        continue;   // mount-order winner already listed

                    string? name = _strings.GetMapName(id);
                    if (q != null
                        && !stem.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && (name == null || !name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    dto.Total++;
                    dto.Maps.Add(new MapListRowDto
                    {
                        Id = id,
                        Name = name,
                        Path = WzPath.Child(subPath, image.Name),
                        Source = file.Name,
                    });
                }
            }
        }

        // A map .img opened on its own has no directory wrapper, but it is the
        // same editable image the tree and round-trip loader already consume.
        // SelectRoleSources ensures these are used only when a WZ/IMG-folder
        // representation of Map is not already the aggregate source.
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            if (file.LooseImage == null)
                continue;
            string stem = file.LooseImage.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? file.LooseImage.Name[..^4]
                : file.LooseImage.Name;
            if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || !seen.Add(id))
                continue;

            string? name = _strings.GetMapName(id);
            if (q != null
                && !stem.Contains(q, StringComparison.OrdinalIgnoreCase)
                && (name == null || !name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                continue;

            dto.Total++;
            dto.Maps.Add(new MapListRowDto
            {
                Id = id,
                Name = name,
                Path = file.Id,
                Source = file.Name,
            });
        }

        // A numeric query usually means "take me to this map-id range". Keep
        // fuzzy contains matching for names and partially remembered ids, but
        // never let those looser matches bury ids that begin with the query.
        // Sort before applying the limit so a large client cannot fill all 200
        // visible rows with traversal-order contains matches first.
        dto.Maps.Sort((a, b) =>
        {
            int rank = MatchRank(a).CompareTo(MatchRank(b));
            return rank != 0 ? rank : a.Id.CompareTo(b.Id);
        });
        if (dto.Maps.Count > limit)
        {
            dto.Truncated = true;
            dto.Maps = dto.Maps.Take(limit).ToList();
        }
        return dto;

        int MatchRank(MapListRowDto row)
        {
            if (q == null)
                return 0;
            // The picker presents map ids as nine digits. Rank the same text the
            // user sees, so searching 10000 puts 10000xxxx first rather than a
            // short id such as 10000 that is displayed as 000010000.
            string id = row.Id.ToString("D9", CultureInfo.InvariantCulture);
            if (id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (row.Name?.StartsWith(q, StringComparison.OrdinalIgnoreCase) == true)
                return 1;
            if (id.Contains(q, StringComparison.OrdinalIgnoreCase))
                return 2;
            return 3; // remaining matches contain the query in the name
        }
    }

    /// <summary>
    /// Every <c>Map/</c> directory in every open archive, in the client's mount
    /// order — the same union-not-pick rule <see cref="MapAssetService"/> was
    /// fixed to follow, because half a library looks exactly like a small client.
    /// </summary>
    private IEnumerable<(OpenFile File, WzDirectory Directory, string Path)> MapDirectories()
    {
        List<OpenFile> selected = Ordered(_session.SelectRoleSources("Map")).ToList();
        bool found = false;
        foreach (OpenFile file in selected)
        {
            foreach ((WzDirectory directory, string path) in MapDirectories(file))
            {
                found = true;
                yield return (file, directory, path);
            }
        }
        if (found)
            yield break;

        // Map.wz can be assets-only; this client's geometry lives in Map002.wz.
        // If that sibling has not been opened yet, an already mounted extracted
        // Map folder is still a complete geometry source and must not disappear
        // merely because the asset archive won the general WZ-vs-IMG tie-break.
        // Take one fallback representation, never merge several clients.
        OpenFile? fallback = _session.Files.FirstOrDefault(file =>
            file.Kind == "img-folder"
            && selected.All(chosen => chosen.Id != file.Id)
            && _session.RoleRoot(file, "Map") != null);
        if (fallback == null)
            yield break;

        foreach ((WzDirectory directory, string path) in MapDirectories(fallback))
            yield return (fallback, directory, path);
    }

    private IEnumerable<(WzDirectory Directory, string Path)> MapDirectories(OpenFile file)
    {
        WzDirectory? root = _session.RoleRoot(file, "Map");
        if (root == null)
            yield break;

        // Some extracted clients mount the map-id directory itself (Map0..
        // Map9 directly); others preserve the Map.wz wrapper (Map/Map0..).
        // Detect the shape from the children instead of guessing from the
        // selected folder's name.
        if (root.WzDirectories.Any(IsMapIdDirectory))
        {
            yield return (root, _session.RoleRootPath(file, "Map"));
            yield break;
        }
        string rolePath = _session.RoleRootPath(file, "Map");
        foreach (WzDirectory match in root.WzDirectories)
        {
            if (string.Equals(match.Name, "Map", StringComparison.OrdinalIgnoreCase))
            {
                yield return (match, WzPath.Child(rolePath, match.Name));
            }
        }
    }

    private static bool IsMapIdDirectory(WzDirectory directory) =>
        directory.Name.Length == 4
        && directory.Name.StartsWith("Map", StringComparison.Ordinal)
        && directory.Name[3] is >= '0' and <= '9';

    private static IEnumerable<OpenFile> Ordered(IEnumerable<OpenFile> files)
        => files
            .Select((f, index) => (File: f, Index: index))
            .OrderBy(x => MountRank(x.File))
            .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Select(x => x.File);

    private static int MountRank(OpenFile file)
    {
        string stem = file.Name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)
            ? file.Name[..^3] : file.Name;
        string family = WzSessionService.StripArchiveSuffix(stem);
        if (stem.Equals(family, StringComparison.OrdinalIgnoreCase))
            return -1;
        string digits = stem[family.Length..];
        return digits.Length == 3 && int.TryParse(digits, out int number) ? number : int.MaxValue;
    }

    #endregion

    #region Open / close

    /// <summary>
    /// Opens a map through <see cref="MapRoundTrip.LoadVerified"/>, or throws
    /// with the model's refusal — the endpoint turns that into the banner. There
    /// is no partial open.
    /// </summary>
    public MapDocDto Open(string path)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(path, out EditorDoc? existing))
            {
                existing.Touched = DateTime.UtcNow;
                return BuildDto(existing);
            }

            MapLoadResult result;
            byte[] iv;
            int generation;
            lock (_session.Gate)
            {
                OpenFile file = _session.GetFileForPath(path);
                if (_session.Resolve(path) is not WzImage image)
                {
                    throw new InvalidOperationException(
                        $"'{path}' is not a map image. The map editor opens whole .img map images.");
                }
                iv = file.CustomIv ?? WzTool.GetIvByMapleVersion(file.MapleVersion);
                result = MapRoundTrip.LoadVerified(image, iv);
                generation = _session.Generation;
            }

            if (!result.Ok)
            {
                // The refusal reason is the model's own sentence; it names the
                // node that differs. Surfaced as-is.
                throw new InvalidOperationException(result.Reason ?? "The map could not be opened.");
            }

            EditorDoc doc = new(path, result.Document!, iv, generation);
            EvictIfNeeded();
            _docs[path] = doc;
            return BuildDto(doc);
        }
    }

    public MapDocDto GetDoc(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            doc.Touched = DateTime.UtcNow;
            return BuildDto(doc);
        }
    }

    public void Close(string path)
    {
        lock (_gate)
            _docs.Remove(path);
    }

    /// <summary>
    /// The editor's unsaved work, one row per open document. The topbar dirty
    /// chip reads this because map-editor edits live in these documents, not in
    /// the session tree — the session's own change summary cannot see them, and
    /// a chip that misses them says "nothing unsaved" over real work.
    /// </summary>
    public MapEditChangesDto Changes()
    {
        lock (_gate)
        {
            MapEditChangesDto dto = new();
            foreach (EditorDoc doc in _docs.Values)
            {
                dto.Docs.Add(new MapEditChangeRowDto
                {
                    Path = doc.Path,
                    ImageName = doc.Doc.ImageName,
                    Name = doc.Doc.MapId is int id ? _strings.GetMapName(id) : null,
                    UndoDepth = doc.Undo.Count,
                    Dirty = doc.Dirty,
                });
                if (doc.Dirty)
                {
                    dto.DirtyDocs++;
                    // Depth counts edits since load; after a save the clean
                    // point moves, so the distance FROM it is what is unsaved.
                    dto.EditCount += doc.CleanDepth >= 0
                        ? Math.Abs(doc.Undo.Count - doc.CleanDepth)
                        : doc.Undo.Count;
                }
            }
            return dto;
        }
    }

    private EditorDoc Require(string path)
    {
        if (!_docs.TryGetValue(path, out EditorDoc? doc))
        {
            throw new KeyNotFoundException(
                $"'{path}' is not open in the map editor. Open it first — documents do not survive " +
                "a restart or an eviction.");
        }
        return doc;
    }

    private void EvictIfNeeded()
    {
        while (_docs.Count >= MaxOpenDocs)
        {
            // Never evict a document holding unsaved edits while a clean one is
            // available; losing edits to a cache policy would be the exact kind
            // of silent loss this tool exists to end.
            EditorDoc? victim = _docs.Values.Where(d => !d.Dirty).OrderBy(d => d.Touched).FirstOrDefault()
                ?? _docs.Values.OrderBy(d => d.Touched).First();
            if (victim.Dirty)
            {
                _log.LogWarning("Map editor evicting DIRTY document {Path}; its unsaved edits are lost.",
                    victim.Path);
            }
            _docs.Remove(victim.Path);
        }
    }

    #endregion

    #region Edits

    public MapEditResultDto Edit(MapEditRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;
            MapEditOp op = request.Op;

            MapEditResultDto result = op.Kind switch
            {
                "setValue" => SetValue(doc, op),
                "move" => Move(doc, op),
                "moveFoothold" => MoveFoothold(doc, op),
                "moveLife" => MoveLife(doc, op),
                "delete" => Delete(doc, op),
                "setField" => SetField(doc, op),
                "moveMany" => MoveMany(doc, op),
                "deleteMany" => DeleteMany(doc, op),
                "duplicate" => Duplicate(doc, op),
                "insertFootholdVertex" => InsertFootholdVertex(doc, op),
                "extendFoothold" => ExtendFoothold(doc, op),
                "moveLadder" => MoveLadder(doc, op),
                "setRect" => SetRect(doc, op),
                _ => throw new ArgumentException(
                    $"'{op.Kind}' is not an edit this endpoint knows. It offers setValue, setField, " +
                    "move, moveFoothold, moveLife, delete, moveMany, deleteMany, duplicate, " +
                    "insertFootholdVertex, extendFoothold, moveLadder and setRect."),
            };

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    public MapUndoResultDto UndoRedo(string path, bool redo)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            Stack<Change> from = redo ? doc.Redo : doc.Undo;
            Stack<Change> to = redo ? doc.Undo : doc.Redo;

            string? applied = null;
            if (from.Count > 0)
            {
                Change change = from.Pop();
                if (redo) change.Apply(); else change.Revert();
                to.Push(change);
                applied = change.Label;
                // Clean means "the model equals what was loaded or last saved".
                // History survives a save, so the clean point is the undo depth
                // recorded AT the save (0 for a fresh document) — undoing past
                // it makes the document dirty in the other direction.
                doc.Dirty = doc.Undo.Count != doc.CleanDepth;
            }

            return new MapUndoResultDto
            {
                Applied = applied,
                UndoDepth = doc.Undo.Count,
                RedoDepth = doc.Redo.Count,
                Dirty = doc.Dirty,
            };
        }
    }

    private static MapEditResultDto SetValue(EditorDoc doc, MapEditOp op)
    {
        WzNode node = doc.NodeAt(op.Addr);
        if (op.Value == null)
            throw new ArgumentException("setValue needs a value.");

        // Snapshot by the type the node already has; SetText/SetNumber keep it.
        Change change = ValueChange(node, $"set {node.Name}");
        node.SetText(op.Value);
        change.CaptureNew();
        doc.Push(change);

        return new MapEditResultDto { Structural = false };
    }

    private static MapEditResultDto Move(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("move needs x and y.");

        WzNode node = doc.NodeAt(op.Addr);
        WzNode? x = node.Child("x");
        WzNode? y = node.Child("y");
        if (x == null || y == null)
        {
            throw new InvalidOperationException(
                $"'{node.Name}' has no x/y children to move. Only entries that store their position " +
                "as x and y can be dragged.");
        }

        Change cx = ValueChange(x, "");
        Change cy = ValueChange(y, "");
        x.SetNumber(nx);
        y.SetNumber(ny);
        cx.CaptureNew();
        cy.CaptureNew();
        doc.Push(Change.Group($"move {node.Name}", cx, cy));

        return new MapEditResultDto
        {
            Structural = false,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = nx, Y1 = ny } },
        };
    }

    /// <summary>
    /// Moves one endpoint of one foothold, and with it every linked endpoint
    /// that coincided with it — the invariant the data actually keeps: on all
    /// but 38 of 784,735 footholds, <c>prev</c>'s (x2,y2) equals this one's
    /// (x1,y1).
    ///
    /// <para><b>What this never does:</b> it never writes <c>prev</c> or
    /// <c>next</c>, and it never "repairs" an asymmetric pair. 6,744 footholds
    /// ship with a prev whose next is someone else — forks — and every one of
    /// them is legal.</para>
    ///
    /// <para>Propagation is a connected-component walk over the endpoints that
    /// sat at the old point <i>within the same foothold layer</i> (chains cross
    /// groups freely — 54,016 do — but cross a layer only 5 times in the whole
    /// client, so a layer is the propagation boundary), following only actual
    /// prev/next references. Two unrelated footholds that merely happen to
    /// touch the same coordinate are left alone.</para>
    /// </summary>
    private MapEditResultDto MoveFoothold(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("moveFoothold needs x and y.");
        if (op.Vertex is not (1 or 2))
            throw new ArgumentException("moveFoothold needs vertex 1 or 2.");
        if (op.Addr.Length != 4)
            throw new ArgumentException("A foothold address is foothold/layer/group/id — four segments.");

        WzNode root = doc.Doc.Nodes[op.Addr[0]];
        if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
            throw new ArgumentException("moveFoothold only moves footholds.");

        WzNode layer = root.Children[op.Addr[1]];
        WzNode moving = doc.NodeAt(op.Addr);

        (string x1, string y1) = op.Vertex == 1 ? ("x1", "y1") : ("x2", "y2");
        long? px = moving.IntegerAt(x1);
        long? py = moving.IntegerAt(y1);
        if (px == null || py == null)
        {
            throw new InvalidOperationException(
                $"Foothold '{moving.Name}' has no {x1}/{y1}, so there is no vertex to move.");
        }

        // Everyone whose endpoint sat exactly at the old point, in this layer.
        List<(WzNode Node, int Vertex)> atPoint = new();
        foreach (WzNode group in layer.Children)
        {
            foreach (WzNode fh in group.Children)
            {
                if (fh.IntegerAt("x1") == px && fh.IntegerAt("y1") == py)
                    atPoint.Add((fh, 1));
                if (fh.IntegerAt("x2") == px && fh.IntegerAt("y2") == py)
                    atPoint.Add((fh, 2));
            }
        }

        // Connected component containing the dragged foothold, linked through
        // prev/next among the members. Forks join through their shared
        // neighbour, not through each other, which is exactly what a component
        // walk gives and a direct-link test would not.
        // WzNode does not override Equals, so set membership is reference
        // identity — which is the correct identity for nodes of a tree that
        // legally repeats names and values.
        HashSet<WzNode> component = new() { moving };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((WzNode fh, int _) in atPoint)
            {
                if (component.Contains(fh))
                    continue;
                foreach (WzNode member in component)
                {
                    if (Linked(fh, member))
                    {
                        component.Add(fh);
                        grew = true;
                        break;
                    }
                }
            }
        }

        List<Change> changes = new();
        List<(WzNode Node, int Vertex)> movedEndpoints = new();
        foreach ((WzNode fh, int vertex) in atPoint)
        {
            if (!component.Contains(fh))
                continue;
            (string fx, string fy) = vertex == 1 ? ("x1", "y1") : ("x2", "y2");
            WzNode xNode = fh.Child(fx)!;
            WzNode yNode = fh.Child(fy)!;
            Change cx = ValueChange(xNode, "");
            Change cy = ValueChange(yNode, "");
            xNode.SetNumber(nx);
            yNode.SetNumber(ny);
            cx.CaptureNew();
            cy.CaptureNew();
            changes.Add(cx);
            changes.Add(cy);
            movedEndpoints.Add((fh, vertex));
        }
        doc.Push(Change.Group($"move foothold {moving.Name} vertex {op.Vertex}", changes.ToArray()));

        // Report every foothold that moved, with its full geometry, addressed.
        MapEditResultDto result = new() { Structural = false };
        foreach (WzNode fh in movedEndpoints.Select(m => m.Node).Distinct())
        {
            int[]? addr = AddrOfFoothold(doc, op.Addr[0], fh);
            if (addr == null)
                continue;
            result.Moved.Add(new MapMovedDto
            {
                Addr = addr,
                X1 = fh.IntegerAt("x1") ?? 0,
                Y1 = fh.IntegerAt("y1") ?? 0,
                X2 = fh.IntegerAt("x2") ?? 0,
                Y2 = fh.IntegerAt("y2") ?? 0,
            });
        }
        return result;
    }

    /// <summary>
    /// Moves a life spawn by re-anchoring it: the foothold under the drop point
    /// is found the way placement finds one, <c>fh</c> is rewritten to its id
    /// and <c>cy</c> (and <c>y</c>) to that foothold's own y at the new x — the
    /// y/cy pairing that made freehand dragging unsafe, kept consistent by
    /// construction instead of being silently split. <c>rx0</c>/<c>rx1</c>
    /// (the patrol range) ride along by the same x delta, keeping their width.
    ///
    /// <para>Nothing below the drop point is a refusal, not a guess — the same
    /// sentence placement uses. Fields the record does not carry are not
    /// invented; only what is there is rewritten.</para>
    /// </summary>
    private static MapEditResultDto MoveLife(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("moveLife needs x and y — the drop point.");

        WzNode life = doc.NodeAt(op.Addr);
        WzNode? x = life.Child("x");
        WzNode? y = life.Child("y");
        if (x == null || y == null)
            throw new InvalidOperationException(
                $"'{life.Name}' has no x/y children, so there is nothing to move.");

        long oldX = x.Integer;
        List<Change> changes = new();
        List<string> notes = new();

        void Write(WzNode node, long value)
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }

        // The anchor rule is placement's own: nearest standable foothold at or
        // below the drop point, cy from its geometry at the new x.
        (long fhId, long cy) = AnchorToFoothold(doc, nx, ny, null);

        Write(x, nx);
        Write(y, cy);
        if (life.Child("cy") is WzNode cyNode)
            Write(cyNode, cy);
        if (life.Child("fh") is WzNode fhNode)
        {
            if (fhNode.Integer != fhId)
                notes.Add($"Re-anchored to foothold {fhId}; cy recomputed as {cy}.");
            Write(fhNode, fhId);
        }

        long dx = nx - oldX;
        if (dx != 0)
        {
            if (life.Child("rx0") is WzNode rx0)
                Write(rx0, rx0.Integer + dx);
            if (life.Child("rx1") is WzNode rx1)
                Write(rx1, rx1.Integer + dx);
        }

        doc.Push(Change.Group($"move life {life.TextAt("id") ?? life.Name}", changes.ToArray()));

        return new MapEditResultDto
        {
            Structural = false,
            Notes = notes,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = nx, Y1 = cy } },
        };
    }

    /// <summary>Whether either foothold names the other through prev or next.
    /// Ids are node names; prev/next are stored numbers. One-directional is
    /// enough — that is what a fork looks like.</summary>
    private static bool Linked(WzNode a, WzNode b)
    {
        return Names(a.IntegerAt("prev"), b.Name) || Names(a.IntegerAt("next"), b.Name)
            || Names(b.IntegerAt("prev"), a.Name) || Names(b.IntegerAt("next"), a.Name);

        static bool Names(long? reference, string id) =>
            reference is long r && r != 0
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            && parsed == r;
    }

    private static int[]? AddrOfFoothold(EditorDoc doc, int rootIndex, WzNode fh)
    {
        WzNode root = doc.Doc.Nodes[rootIndex];
        for (int l = 0; l < root.Children.Count; l++)
        {
            WzNode layer = root.Children[l];
            for (int g = 0; g < layer.Children.Count; g++)
            {
                WzNode group = layer.Children[g];
                for (int i = 0; i < group.Children.Count; i++)
                {
                    if (ReferenceEquals(group.Children[i], fh))
                        return new[] { rootIndex, l, g, i };
                }
            }
        }
        return null;
    }

    private MapEditResultDto Delete(EditorDoc doc, MapEditOp op)
    {
        if (op.Addr.Length < 2)
        {
            throw new InvalidOperationException(
                "A top-level node is never deleted from the map editor. Deleting a whole kind — " +
                "even an empty container — is a diff against the client: reactor ships empty on " +
                "9,560 maps and pruning it changes the map.");
        }

        WzNode parent = doc.NodeAt(op.Addr[..^1]);
        int index = op.Addr[^1];
        if (index < 0 || index >= parent.Children.Count)
            throw new ArgumentException("The address names a child that is not there.");
        WzNode child = parent.Children[index];

        // A deleted subtree leaves the document but stays in the undo stack —
        // which the save's payload sweep cannot see. Pin and detach its
        // payloads NOW, while the reader they point at is still open, so
        // undoing the delete after a save restores real bytes. A failure here
        // is not fatal to the delete; it would only surface as the save's
        // honest fallback if this exact node is ever restored.
        lock (_session.Gate)
        {
            _ = PinPayloads(child, child.Name) ?? DetachPayloads(child, child.Name);
        }

        parent.Remove(child);
        doc.Push(new Change($"delete {child.Name}",
            apply: () => parent.Remove(child),
            revert: () => parent.Insert(index, child)));

        return new MapEditResultDto { Structural = true };
    }

    /// <summary>A reversible scalar write. Old value captured now, new value
    /// after the caller writes it.</summary>
    private static Change ValueChange(WzNode node, string label)
    {
        string? oldText = null;
        double oldNumber = 0;
        bool numeric;
        switch (node.Type)
        {
            case WzPropertyType.String:
            case WzPropertyType.UOL:
                oldText = node.Text;
                numeric = false;
                break;
            case WzPropertyType.Short:
            case WzPropertyType.Int:
            case WzPropertyType.Long:
                oldNumber = node.Integer;
                numeric = true;
                break;
            case WzPropertyType.Float:
                oldNumber = node.Single;
                numeric = true;
                break;
            case WzPropertyType.Double:
                oldNumber = node.Double;
                numeric = true;
                break;
            default:
                throw new InvalidOperationException(
                    $"'{node.Name}' is a {node.Type} and its value cannot be edited here. Containers, " +
                    "vectors and binary payloads are not scalar edits.");
        }

        string? newText = null;
        double newNumber = 0;
        Change change = null!;
        change = new Change(label,
            apply: () =>
            {
                if (numeric) node.SetNumber(newNumber);
                else node.SetText(newText ?? "");
            },
            revert: () =>
            {
                if (numeric) node.SetNumber(oldNumber);
                else node.SetText(oldText ?? "");
            })
        {
            OnCaptureNew = () =>
            {
                if (numeric)
                {
                    newNumber = node.Type switch
                    {
                        WzPropertyType.Float => node.Single,
                        WzPropertyType.Double => node.Double,
                        _ => node.Integer,
                    };
                }
                else
                {
                    newText = node.Text;
                }
            },
        };
        return change;
    }

    #endregion

    #region Inspector

    /// <summary>The raw view of one node and its immediate children.</summary>
    public MapNodeDto InspectNode(string path, int[] addr)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            WzNode node = doc.NodeAt(addr);
            MapNodeDto dto = Describe(node, addr);
            for (int i = 0; i < node.Children.Count; i++)
            {
                int[] childAddr = new int[addr.Length + 1];
                addr.CopyTo(childAddr, 0);
                childAddr[^1] = i;
                dto.Children.Add(Describe(node.Children[i], childAddr));
            }
            return dto;
        }
    }

    private static MapNodeDto Describe(WzNode node, int[] addr) => new()
    {
        Addr = addr,
        Name = node.Name,
        Type = node.Type.ToString(),
        Value = node.Type switch
        {
            WzPropertyType.Vector =>
                $"({(node.HasVectorX ? node.Integer.ToString(CultureInfo.InvariantCulture) : "·")}, "
                + $"{(node.HasVectorY ? node.VectorY.ToString(CultureInfo.InvariantCulture) : "·")})",
            WzPropertyType.SubProperty or WzPropertyType.Convex or WzPropertyType.Canvas => null,
            _ => node.AsText(),
        },
        HasChildren = node.IsContainer && node.Children.Count > 0,
        ChildCount = node.IsContainer ? node.Children.Count : 0,
    };

    #endregion

    #region Save

    /// <summary>
    /// Rebuilds the image from the model, writes the archive through
    /// <see cref="WzSaveService"/>, and verifies against the saved-and-reopened
    /// file: the saved image's bytes must be exactly the model's bytes.
    ///
    /// <para><b>The document — and its undo history — survives the save.</b>
    /// The old hazard was carried payloads (canvas pixels, unmodelled binaries)
    /// pointing into the reader the save replaces, so every payload is pinned
    /// into memory BEFORE the write, while the old reader is still open. Only
    /// when a payload cannot be pinned (a shape nobody has shipped yet) does
    /// the save fall back to reloading the document, and it says so.</para>
    /// </summary>
    public MapSaveResultDto Save(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);

            byte[] modelBytes;
            OpenFile file;
            string? pinFailure;
            lock (_session.Gate)
            {
                file = _session.GetFileForPath(path);
                if (file.ReadOnly)
                {
                    throw new InvalidOperationException(
                        $"'{file.Name}' is reference-only. Release it before saving map edits into it.");
                }
                RefuseLiveClient(file);

                if (_session.Generation != doc.Generation)
                {
                    throw new InvalidOperationException(
                        "The session changed since this map was opened — an archive was opened, " +
                        "closed or saved — so the document's carried pixels may no longer be " +
                        "readable. Reopen the map and redo the edit rather than saving blind.");
                }

                if (_session.Resolve(path) is not WzImage live)
                    throw new InvalidOperationException($"'{path}' no longer resolves to an image.");

                // While the old reader is still the one the payloads point at.
                pinFailure = PinPayloads(doc.Doc);

                WzImage rebuilt = doc.Doc.Build();
                // The rebuilt image shares the document's payload objects, and
                // the post-save reopen will DISPOSE the tree they ride in with
                // — nulling even pinned byte caches on those objects. Swap the
                // document onto byte-faithful clones now: the originals go
                // into the write, the clones stay here, and the second save's
                // byte-verification is the proof the clones are exact.
                if (pinFailure == null)
                    pinFailure = DetachPayloads(doc.Doc);
                modelBytes = MapRoundTrip.Serialize(rebuilt, doc.Iv);

                // The model built the image; the session's copy now becomes it.
                WzSessionService.EnsureParsed(live);
                live.WzProperties.Clear();
                foreach (WzImageProperty property in rebuilt.WzProperties)
                    live.WzProperties.Add(property);
                live.Changed = true;
            }

            SaveResult saved;
            try
            {
                saved = _save.Save(new SaveRequest { FileId = file.Id });
            }
            catch
            {
                // The save failed and the archive on disk is untouched (the save
                // service writes to a temp file first). The in-session tree,
                // however, now holds the rebuilt image; that is still the
                // document's content, so nothing is lost — but say what state
                // things are in rather than letting the next save surprise.
                _log.LogWarning("Map save failed for {Path}; the in-session image holds the edited tree.", path);
                throw;
            }

            // Verify on the saved and reopened archive, never on a counter.
            MapSaveResultDto result = new()
            {
                SavedTo = saved.SavedTo,
                BackupPath = saved.BackupPath,
                ArchiveBytes = saved.Bytes,
                Seconds = saved.Seconds,
                UnmodelledCarried = doc.Doc.Unmodelled.Count,
            };

            lock (_session.Gate)
            {
                if (_session.Resolve(path) is not WzImage reopened)
                {
                    result.Verified = false;
                    result.Differences.Add("The saved archive no longer holds this image at its path.");
                    return result;
                }
                WzSessionService.EnsureParsed(reopened);
                byte[] savedBytes = MapRoundTrip.Serialize(reopened, doc.Iv);
                result.Verified = savedBytes.AsSpan().SequenceEqual(modelBytes);

                if (!result.Verified)
                {
                    foreach (MapNodeDifference difference in
                             MapRoundTrip.CompareForTest(reopened, doc.Doc.Build()))
                        result.Differences.Add(difference.ToString());
                    if (result.Differences.Count == 0)
                        result.Differences.Add("The bytes differ although every node matches.");
                }

                if (result.Verified && pinFailure == null)
                {
                    // The document lives on: every payload is memory-backed, the
                    // saved file equals the model, and the history on both sides
                    // of this point still applies to these exact nodes.
                    doc.Generation = _session.Generation;
                    doc.MarkSaved();
                    result.HistoryKept = true;
                }
                else
                {
                    // The fallback arm: a payload that could not be pinned, or a
                    // verification failure. Reload from the saved file the way
                    // every save used to, and say what was lost and why.
                    result.HistoryKept = false;
                    result.HistoryNote = pinFailure != null
                        ? $"Undo history was cleared by this save: {pinFailure}"
                        : "Undo history was cleared because verification failed and the document " +
                          "was reloaded from the saved file.";
                    MapLoadResult reload = MapDocument.Load(reopened);
                    if (reload.Ok)
                    {
                        _docs[path] = new EditorDoc(path, reload.Document!, doc.Iv, _session.Generation);
                    }
                    else
                    {
                        _docs.Remove(path);
                        result.Differences.Add(
                            $"The saved image would not reload: {reload.Reason} The document was closed.");
                        result.Verified = false;
                    }
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Pins every carried payload in the model into memory, so the document
    /// outlives the reader it was parsed from. Returns null on success, or a
    /// sentence naming the first payload that could not be pinned — the caller
    /// then falls back to reloading the document and says so, rather than
    /// keeping a model that would throw on its next build.
    /// </summary>
    private static string? PinPayloads(MapDocument map)
    {
        foreach (WzNode root in map.Nodes)
        {
            string? failure = PinPayloads(root, root.Name);
            if (failure != null)
                return failure;
        }
        return null;
    }

    private static string? PinPayloads(WzNode node, string where)
    {
        if (node.Payload is WzImageProperty payload)
        {
            try
            {
                switch (payload)
                {
                    case WzPngProperty png:
                        // A canvas built in memory (a regenerated minimap, a
                        // fresh placement) compressed its bitmap at set time, so
                        // this returns the cached bytes without touching any
                        // reader; a parsed canvas reads its bytes NOW, while the
                        // reader is still the one it was parsed from.
                        if (png.GetCompressedBytes(saveInMemory: true) == null)
                            return $"the pixels under '{where}' could not be read into memory.";
                        break;
                    case WzBinaryProperty sound:
                        if (sound.GetBytes(saveInMemory: true) == null)
                            return $"the sound under '{where}' could not be read into memory.";
                        break;
                    case WzRawDataProperty raw:
                        if (raw.GetBytes(saveInMemory: true) == null)
                            return $"the raw data under '{where}' could not be read into memory.";
                        break;
                    case WzVideoProperty video:
                        if (video.GetBytes(saveInMemory: true) == null)
                            return $"the video under '{where}' could not be read into memory.";
                        break;
                    case WzLuaProperty:
                        break; // always memory-backed
                    default:
                        return $"'{where}' carries a {payload.GetType().Name}, a payload this " +
                            "save does not know how to pin in memory.";
                }
            }
            catch (Exception ex)
            {
                return $"pinning the payload under '{where}' failed: {ex.Message}";
            }
        }

        foreach (WzNode child in node.Children)
        {
            string? failure = PinPayloads(child, $"{where}/{child.Name}");
            if (failure != null)
                return failure;
        }
        return null;
    }

    /// <summary>Every payload swapped for its owned-bytes clone — see
    /// <see cref="WzNode.DetachPayload"/>. Null on success; on the first
    /// failure, a sentence naming the node, and the caller falls back to the
    /// reload arm.</summary>
    private static string? DetachPayloads(MapDocument map)
    {
        foreach (WzNode root in map.Nodes)
        {
            string? failure = DetachPayloads(root, root.Name);
            if (failure != null)
                return failure;
        }
        return null;
    }

    private static string? DetachPayloads(WzNode node, string where)
    {
        try
        {
            node.DetachPayload();
        }
        catch (Exception ex)
        {
            return $"cloning the payload under '{where}' failed: {ex.Message}";
        }
        foreach (WzNode child in node.Children)
        {
            string? failure = DetachPayloads(child, $"{where}/{child.Name}");
            if (failure != null)
                return failure;
        }
        return null;
    }

    /// <summary>The one folder this tool never writes: the live client.</summary>
    private static void RefuseLiveClient(OpenFile file)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(file.FilePath); }
        catch { return; }
        if (full.StartsWith(@"C:\MapleStory\232", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{file.FilePath}' is inside the live client folder, which this editor never " +
                "writes. Work on a copy — the game launcher re-downloads edited archives with no " +
                "backup, so an edit here is both dangerous and futile.");
        }
    }

    #endregion

    #region Document DTO

    private MapDocDto BuildDto(EditorDoc doc)
    {
        MapDocument map = doc.Doc;
        MapDocDto dto = new()
        {
            Path = doc.Path,
            ImageName = map.ImageName,
            MapId = map.MapId,
            Name = map.MapId is int id ? _strings.GetMapName(id) : null,
            Link = map.Info?.Link,
            HasGeometry = map.HasGeometry,
            Bgm = map.Info?.Bgm,
            MapMark = map.Info?.MapMark,
            UndoDepth = doc.Undo.Count,
            RedoDepth = doc.Redo.Count,
            Dirty = doc.Dirty,
        };

        foreach (WzNode node in map.Unmodelled)
            dto.UnmodelledNames.Add(node.Name);
        dto.UnmodelledCount = dto.UnmodelledNames.Count;

        // One pass over the top level, index in hand — addresses are index
        // paths, and sibling names legally repeat.
        Dictionary<string, ArtRef> art = new(StringComparer.Ordinal);
        for (int i = 0; i < map.Nodes.Count; i++)
        {
            WzNode node = map.Nodes[i];
            switch (node.Name)
            {
                case "back":
                    ReadBacks(dto, node, i, inLayer: false, art);
                    break;
                case "foothold":
                    ReadFootholds(dto, node, i);
                    break;
                case "ladderRope":
                    ReadLadders(dto, node, i);
                    break;
                case "portal":
                    ReadPortals(dto, node, i);
                    break;
                case "life":
                    ReadLife(dto, node, i, art);
                    break;
                case "reactor":
                    ReadReactors(dto, node, i);
                    break;
                case "miniMap":
                    dto.MiniMap = new MapMiniMapDto
                    {
                        Width = node.IntegerAt("width"),
                        Height = node.IntegerAt("height"),
                        CenterX = node.IntegerAt("centerX"),
                        CenterY = node.IntegerAt("centerY"),
                        CanvasPath = node.Child("canvas") != null
                            ? WzPath.Child(WzPath.Child(doc.Path, "miniMap"), "canvas")
                            : null,
                    };
                    break;
                default:
                    if (MapNodeKinds.IsLayerName(node.Name)
                        && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int layerIndex))
                        ReadLayer(dto, node, i, layerIndex, art);
                    else if (IsRectKind(node.Name))
                        ReadRects(dto, node, i);
                    break;
            }
        }

        // Bounds: VR when the map has one; computed from geometry for the 1,151
        // that do not. Three bounding rects exist (VR/MR/LB) and only VR is the
        // camera bound — the others are never used here.
        MapRect? vr = map.Info?.ViewBound;
        if (vr is MapRect rect)
        {
            dto.Bounds = new MapBoundsDto { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        }
        else
        {
            dto.Bounds = ComputedBounds(dto);
            dto.BoundsComputed = dto.Bounds != null;
        }

        ResolveArt(dto, art);
        return dto;
    }

    private void ReadLayer(
        MapDocDto dto, WzNode layer, int index, int layerNumber,
        Dictionary<string, ArtRef> art)
    {
        MapLayerDto layerDto = new()
        {
            Index = layerNumber,
            Addr = new[] { index },
            TS = layer.Descend("info/tS")?.AsText(),
            TSMag = layer.Descend("info/tSMag")?.AsInteger(),
        };

        for (int c = 0; c < layer.Children.Count; c++)
        {
            WzNode child = layer.Children[c];
            switch (child.Name)
            {
                case "tile":
                    for (int t = 0; t < child.Children.Count; t++)
                    {
                        WzNode tile = child.Children[t];
                        long tileZ = long.TryParse(tile.Name, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long parsedTileZ)
                            ? parsedTileZ
                            : t;
                        string? u = tile.TextAt("u");
                        string? key = layerDto.TS != null && u != null
                            ? Register(art, $"t|{layerDto.TS}|{u}|{tile.IntegerAt("no") ?? 0}",
                                new ArtRef { Kind = 't', Set = layerDto.TS, U = u, No = tile.IntegerAt("no") ?? 0 })
                            : null;
                        layerDto.Tiles.Add(new MapTileDto
                        {
                            Addr = new[] { index, c, t },
                            Z = tileZ,
                            X = tile.IntegerAt("x") ?? 0,
                            Y = tile.IntegerAt("y") ?? 0,
                            U = u,
                            No = tile.IntegerAt("no") ?? 0,
                            ZM = tile.IntegerAt("zM") ?? 0,
                            Art = key,
                        });
                    }
                    break;

                case "obj":
                    for (int o = 0; o < child.Children.Count; o++)
                    {
                        WzNode obj = child.Children[o];
                        string? oS = obj.TextAt("oS");
                        string? l0 = obj.TextAt("l0");
                        string? l1 = obj.TextAt("l1");
                        string? l2 = obj.TextAt("l2");
                        string? l3 = obj.TextAt("l3");
                        string? key = oS != null
                            ? Register(art, $"o|{oS}|{l0}|{l1}|{l2}|{l3}",
                                new ArtRef { Kind = 'o', Set = oS, L0 = l0, L1 = l1, L2 = l2, L3 = l3 })
                            : null;
                        layerDto.Objs.Add(new MapObjDto
                        {
                            Addr = new[] { index, c, o },
                            OS = oS,
                            L0 = l0,
                            L1 = l1,
                            L2 = l2,
                            L3 = l3,
                            X = obj.IntegerAt("x") ?? 0,
                            Y = obj.IntegerAt("y") ?? 0,
                            Z = obj.IntegerAt("z") ?? 0,
                            ZM = obj.IntegerAt("zM") ?? 0,
                            F = obj.IntegerAt("f") ?? 0,
                            Spine = !string.IsNullOrEmpty(obj.TextAt("spineAni")),
                            Art = key,
                        });
                    }
                    break;

                case "back":
                    // Anomaly 3: a background list inside a layer, 26 entries on
                    // 954090400.img. Rendered with the rest, marked in-layer.
                    layerDto.LayerBackCount = child.Children.Count;
                    ReadBacksFrom(dto, child, new[] { index, c }, inLayer: true, art);
                    break;
            }
        }
        dto.Layers.Add(layerDto);
    }

    private void ReadBacks(
        MapDocDto dto, WzNode container, int index, bool inLayer,
        Dictionary<string, ArtRef> art)
        => ReadBacksFrom(dto, container, new[] { index }, inLayer, art);

    private void ReadBacksFrom(
        MapDocDto dto, WzNode container, int[] baseAddr, bool inLayer,
        Dictionary<string, ArtRef> art)
    {
        for (int b = 0; b < container.Children.Count; b++)
        {
            WzNode back = container.Children[b];
            string? bS = back.TextAt("bS");
            long ani = back.IntegerAt("ani") ?? 0;
            long no = back.IntegerAt("no") ?? 0;
            string? key = !string.IsNullOrEmpty(bS)
                ? Register(art, $"b|{bS}|{ani}|{no}",
                    new ArtRef { Kind = 'b', Set = bS, Ani = ani, No = no })
                : null;

            int[] addr = new int[baseAddr.Length + 1];
            baseAddr.CopyTo(addr, 0);
            addr[^1] = b;

            dto.Backs.Add(new MapBackDto
            {
                Addr = addr,
                BS = bS,
                No = no,
                Ani = ani,
                X = back.IntegerAt("x") ?? 0,
                Y = back.IntegerAt("y") ?? 0,
                Cx = back.IntegerAt("cx") ?? 0,
                Cy = back.IntegerAt("cy") ?? 0,
                Rx = back.IntegerAt("rx") ?? 0,
                Ry = back.IntegerAt("ry") ?? 0,
                Type = back.IntegerAt("type") ?? 0,
                Front = back.IntegerAt("front") ?? 0,
                F = back.IntegerAt("f") ?? 0,
                A = back.IntegerAt("a") ?? 255,
                InLayer = inLayer,
                Spine = ani == 2 || !string.IsNullOrEmpty(back.TextAt("spineAni")),
                Art = key,
            });
        }
    }

    private static void ReadFootholds(MapDocDto dto, WzNode root, int index)
    {
        for (int l = 0; l < root.Children.Count; l++)
        {
            WzNode layer = root.Children[l];
            for (int g = 0; g < layer.Children.Count; g++)
            {
                WzNode group = layer.Children[g];
                for (int f = 0; f < group.Children.Count; f++)
                {
                    WzNode fh = group.Children[f];
                    dto.Footholds.Add(new MapFootholdDto
                    {
                        Addr = new[] { index, l, g, f },
                        Id = fh.Name,
                        Layer = layer.Name,
                        Group = group.Name,
                        X1 = fh.IntegerAt("x1") ?? 0,
                        Y1 = fh.IntegerAt("y1") ?? 0,
                        X2 = fh.IntegerAt("x2") ?? 0,
                        Y2 = fh.IntegerAt("y2") ?? 0,
                        Prev = fh.IntegerAt("prev") ?? 0,
                        Next = fh.IntegerAt("next") ?? 0,
                    });
                }
            }
        }
    }

    private static void ReadLadders(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode ladder = root.Children[i];
            dto.Ladders.Add(new MapLadderDto
            {
                Addr = new[] { index, i },
                X = ladder.IntegerAt("x") ?? 0,
                Y1 = ladder.IntegerAt("y1") ?? 0,
                Y2 = ladder.IntegerAt("y2") ?? 0,
                L = ladder.IntegerAt("l") ?? 0,
            });
        }
    }

    private static void ReadPortals(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode portal = root.Children[i];
            dto.Portals.Add(new MapPortalDto
            {
                Addr = new[] { index, i },
                Pn = portal.TextAt("pn"),
                Pt = portal.IntegerAt("pt") ?? 0,
                X = portal.IntegerAt("x") ?? 0,
                Y = portal.IntegerAt("y") ?? 0,
                Tm = portal.IntegerAt("tm") ?? 0,
                Tn = portal.TextAt("tn"),
                Script = portal.TextAt("script"),
            });
        }
    }

    private void ReadLife(
        MapDocDto dto, WzNode root, int index,
        Dictionary<string, ArtRef> art)
    {
        // Both shapes: the ordinary life/<i> and the life/<categoryId>/<i> the
        // isCategory flag switches on — 25 maps, 2,516 spawns an editor that
        // only reads the first shape writes back empty.
        bool categorised = root.Child("isCategory")?.AsInteger() == 1;

        for (int c = 0; c < root.Children.Count; c++)
        {
            WzNode child = root.Children[c];
            if (string.Equals(child.Name, "isCategory", StringComparison.Ordinal))
                continue;

            if (categorised)
            {
                for (int e = 0; e < child.Children.Count; e++)
                    dto.Life.Add(LifeDto(
                        child.Children[e], new[] { index, c, e }, child.Name, art));
            }
            else
            {
                dto.Life.Add(LifeDto(child, new[] { index, c }, null, art));
            }
        }
    }

    private MapLifeDto LifeDto(
        WzNode life, int[] addr, string? category,
        Dictionary<string, ArtRef> art)
    {
        string? lifeId = life.TextAt("id");
        string? type = life.TextAt("type");
        string? name = null;
        string? artKey = null;
        if (lifeId != null && int.TryParse(lifeId, out int numericId))
        {
            name = string.Equals(type, "n", StringComparison.OrdinalIgnoreCase)
                ? _strings.GetNpcName(numericId)
                : _strings.GetMobName(numericId);

            if (string.Equals(type, "n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "m", StringComparison.OrdinalIgnoreCase))
            {
                string normalizedType = type.ToLowerInvariant();
                artKey = Register(art, $"l|{normalizedType}|{numericId}", new ArtRef
                {
                    Kind = 'l',
                    Set = numericId.ToString(CultureInfo.InvariantCulture),
                    U = normalizedType,
                });
            }
        }
        return new MapLifeDto
        {
            Addr = addr,
            Id = lifeId,
            Type = type,
            X = life.IntegerAt("x") ?? 0,
            Y = life.IntegerAt("y") ?? 0,
            Cy = life.IntegerAt("cy") ?? 0,
            Fh = life.IntegerAt("fh") ?? 0,
            F = life.IntegerAt("f") ?? 0,
            Hide = life.IntegerAt("hide") ?? 0,
            MobTime = life.IntegerAt("mobTime") ?? 0,
            Category = category,
            Name = name,
            Art = artKey,
        };
    }

    private static void ReadReactors(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode reactor = root.Children[i];
            dto.Reactors.Add(new MapReactorDto
            {
                Addr = new[] { index, i },
                Id = reactor.TextAt("id"),
                X = reactor.IntegerAt("x") ?? 0,
                Y = reactor.IntegerAt("y") ?? 0,
                F = reactor.IntegerAt("f") ?? 0,
                ReactorName = reactor.TextAt("name"),
            });
        }
    }

    private static MapBoundsDto? ComputedBounds(MapDocDto dto)
    {
        long left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
        void Point(long x, long y)
        {
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }

        foreach (MapFootholdDto fh in dto.Footholds) { Point(fh.X1, fh.Y1); Point(fh.X2, fh.Y2); }
        foreach (MapLadderDto l in dto.Ladders) { Point(l.X, l.Y1); Point(l.X, l.Y2); }
        foreach (MapLayerDto layer in dto.Layers)
        {
            foreach (MapTileDto t in layer.Tiles) Point(t.X, t.Y);
            foreach (MapObjDto o in layer.Objs) Point(o.X, o.Y);
        }
        foreach (MapPortalDto p in dto.Portals) Point(p.X, p.Y);
        foreach (MapLifeDto l in dto.Life) Point(l.X, l.Y);

        if (left > right || top > bottom)
            return null;

        // Headroom mirrors what VR typically adds over raw geometry.
        return new MapBoundsDto
        {
            Left = (int)(left - 100),
            Top = (int)(top - 300),
            Right = (int)(right + 100),
            Bottom = (int)(bottom + 100),
        };
    }

    #endregion

    #region Art resolution

    private sealed class ArtRef
    {
        public char Kind;
        public string Set = "";
        public string? U;
        public long No;
        public string? L0, L1, L2, L3;
        public long Ani;
    }

    private static string Register(Dictionary<string, ArtRef> art, string key, ArtRef reference)
    {
        if (!art.ContainsKey(key))
            art[key] = reference;
        return key;
    }

    /// <summary>
    /// Resolves every distinct art reference to a session path plus the origin
    /// and size the renderer needs. A reference that does not resolve is
    /// reported <see cref="MapArtDto.Missing"/> rather than dropped — the
    /// integrity baseline is not zero (30 missing obj sets and 45 missing paths
    /// ship in the client), and a silent gap looks like a renderer bug.
    /// </summary>
    private void ResolveArt(MapDocDto dto, Dictionary<string, ArtRef> art)
    {
        lock (_session.Gate)
        {
            Dictionary<string, (WzImage Image, string Path)?> imageCache = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string key, ArtRef reference) in art)
            {
                MapArtDto resolved = new() { Missing = true };
                try
                {
                    resolved = reference.Kind switch
                    {
                        't' => ResolveTile(reference, imageCache),
                        'o' => ResolveObj(reference, imageCache),
                        'b' => ResolveBack(reference, imageCache),
                        'l' => ResolveLife(reference, imageCache),
                        _ => resolved,
                    };
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Art reference {Key} failed to resolve", key);
                }
                dto.Art[key] = resolved;
            }
        }
    }

    private MapArtDto ResolveTile(ArtRef reference, Dictionary<string, (WzImage, string)?> cache)
    {
        (WzImage image, string path)? set = FindSetImage("Tile", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        WzImageProperty? node = set.Value.image.GetFromPath(
            $"{reference.U}/{reference.No.ToString(CultureInfo.InvariantCulture)}");
        return Meta(node, WzPath.Child(WzPath.Child(set.Value.path, reference.U ?? ""),
            reference.No.ToString(CultureInfo.InvariantCulture)));
    }

    private MapArtDto ResolveObj(ArtRef reference, Dictionary<string, (WzImage, string)?> cache)
    {
        (WzImage image, string path)? set = FindSetImage("Obj", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        List<string> segments = new() { reference.L0 ?? "", reference.L1 ?? "", reference.L2 ?? "" };
        if (!string.IsNullOrEmpty(reference.L3))
            segments.Add(reference.L3);

        WzImageProperty? node = set.Value.image.GetFromPath(string.Join('/', segments));
        string nodePath = segments.Aggregate(set.Value.path, (parent, name) => WzPath.Child(parent, name));

        // The leaf is usually a frame list. Two or more numbered frames make an
        // animation the viewer can play; frame 0 stays the still. A leaf that
        // is itself drawable is used as it stands.
        if (node != null && node.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
        {
            MapArtDto? animated = AnimatedMeta(node, nodePath);
            if (animated != null)
                return animated;

            WzImageProperty? frame = node.WzProperties?.FirstOrDefault(p => p.Name == "0")
                ?? node.WzProperties?.FirstOrDefault();
            if (frame != null)
            {
                nodePath = WzPath.Child(nodePath, frame.Name);
                node = frame;
            }
        }
        return Meta(node, nodePath);
    }

    private MapArtDto ResolveBack(ArtRef reference, Dictionary<string, (WzImage, string)?> cache)
    {
        (WzImage image, string path)? set = FindSetImage("Back", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        // ani = 0 -> back/<no>, ani = 1 -> ani/<no> (a frame list the viewer
        // plays), ani = 2 -> spine, which this viewer does not animate — the
        // entry is badged and its still (the same pixels phase 2 drew) is
        // drawn, never a fake. Resolution order is phase 2's exactly, with
        // spine/<no> added as a LAST resort so a spine entry that resolves
        // nowhere else still shows something rather than a missing box.
        string branch = reference.Ani != 0 ? "ani" : "back";
        string no = reference.No.ToString(CultureInfo.InvariantCulture);
        WzImageProperty? node = set.Value.image.GetFromPath($"{branch}/{no}");
        string nodePath = WzPath.Child(WzPath.Child(set.Value.path, branch), no);
        if (node == null && reference.Ani != 0)
        {
            node = set.Value.image.GetFromPath($"back/{no}");
            nodePath = WzPath.Child(WzPath.Child(set.Value.path, "back"), no);
        }
        if (node == null && reference.Ani == 2)
        {
            node = set.Value.image.GetFromPath($"spine/{no}");
            nodePath = WzPath.Child(WzPath.Child(set.Value.path, "spine"), no);
        }

        if (node != null && node.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
        {
            // Spine subtrees hold texture pages, not frames; playing them as a
            // frame list would be a fake. Only ani = 1 branches animate.
            MapArtDto? animated = reference.Ani == 2 ? null : AnimatedMeta(node, nodePath);
            if (animated != null)
                return animated;

            WzImageProperty? frame = node.WzProperties?.FirstOrDefault(p => p.Name == "0")
                ?? node.WzProperties?.FirstOrDefault();
            if (frame != null)
            {
                nodePath = WzPath.Child(nodePath, frame.Name);
                node = frame;
            }
        }
        return Meta(node, nodePath);
    }

    /// <summary>
    /// Resolves the visible pose for a map life spawn. NPC and mob images are
    /// root images rather than Map/Obj-style set libraries, so they need their
    /// own lookup. Stand is preferred; uncommon actors fall back to their first
    /// drawable top-level state instead of disappearing behind a name label.
    /// </summary>
    private MapArtDto ResolveLife(
        ArtRef reference, Dictionary<string, (WzImage, string)?> cache)
    {
        string family = string.Equals(reference.U, "n", StringComparison.OrdinalIgnoreCase)
            ? "Npc"
            : "Mob";
        (WzImage image, string path)? source = FindLifeImage(family, reference.Set, cache);
        if (source == null)
            return new MapArtDto { Missing = true };

        string[] preferred = family == "Npc"
            ? new[] { "stand", "default", "move", "sit", "speak" }
            : new[] { "stand", "move", "fly", "jump" };

        foreach (string state in preferred)
        {
            WzImageProperty? node = source.Value.image.GetFromPath(state);
            MapArtDto? art = LifeStateMeta(node, WzPath.Child(source.Value.path, state));
            if (art != null)
                return art;
        }

        foreach (WzImageProperty node in source.Value.image.WzProperties)
        {
            if (string.Equals(node.Name, "info", StringComparison.OrdinalIgnoreCase))
                continue;
            MapArtDto? art = LifeStateMeta(
                node, WzPath.Child(source.Value.path, node.Name));
            if (art != null)
                return art;
        }

        return new MapArtDto { Missing = true };
    }

    private static MapArtDto? LifeStateMeta(WzImageProperty? node, string path)
    {
        if (node == null)
            return null;
        if (node.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL)
        {
            MapArtDto direct = Meta(node, path);
            return direct.Missing ? null : direct;
        }

        MapArtDto? animated = AnimatedMeta(node, path);
        if (animated != null)
            return animated;

        WzImageProperty? frame = node.WzProperties?
            .Where(p => int.TryParse(p.Name, NumberStyles.None,
                CultureInfo.InvariantCulture, out _))
            .OrderBy(p => int.Parse(p.Name, CultureInfo.InvariantCulture))
            .FirstOrDefault();
        if (frame == null)
            return null;
        MapArtDto still = Meta(frame, WzPath.Child(path, frame.Name));
        return still.Missing ? null : still;
    }

    private (WzImage Image, string Path)? FindLifeImage(
        string family, string id,
        Dictionary<string, (WzImage, string)?> cache)
    {
        string cacheKey = "life|" + family + "|" + id;
        if (cache.TryGetValue(cacheKey, out (WzImage, string)? cached))
            return cached;

        List<string> names = new() { id + ".img" };
        if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int numericId))
        {
            string padded = numericId.ToString("D7", CultureInfo.InvariantCulture) + ".img";
            if (!names.Contains(padded, StringComparer.OrdinalIgnoreCase))
                names.Add(padded);
        }

        (WzImage, string)? found = null;
        foreach ((OpenFile file, WzDirectory root) in FindRootImageArchives(family))
        {
            WzImage? image = null;
            foreach (string name in names)
            {
                image = root.GetImageByName(name);
                if (image != null)
                    break;
            }
            if (image == null)
                continue;
            WzSessionService.EnsureParsed(image);
            found = (image, WzPath.Child(_session.RoleRootPath(file, family), image.Name));
            break;
        }

        cache[cacheKey] = found;
        return found;
    }

    /// <summary>
    /// Recognises a numbered-frame container and describes it as a playable
    /// animation, composed at the shared origin by
    /// <see cref="AnimationService.PlaceAtSharedOrigin"/> — the single
    /// composition rule this app has; this caller reuses it rather than
    /// restating it. Returns null when the node holds fewer than two frames.
    /// </summary>
    private static MapArtDto? AnimatedMeta(WzImageProperty node, string nodePath)
    {
        if (node.WzProperties == null)
            return null;

        List<(int Number, WzImageProperty Frame)> frames = new();
        foreach (WzImageProperty child in node.WzProperties)
        {
            // A Spine subtree holds numbered TEXTURE PAGES beside its .atlas /
            // .json / .skel children. Playing those pages as frames would fake
            // a rig this viewer cannot run — the entry stays a badged still.
            if (child.Name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)
                || child.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || child.Name.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
                return null;

            if (int.TryParse(child.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                frames.Add((number, child));
        }
        if (frames.Count < 2)
            return null;
        frames.Sort((a, b) => a.Number.CompareTo(b.Number));

        List<AnimationFrameDto> dtos = new(frames.Count);
        List<string> paths = new(frames.Count);
        int z = 0;
        bool first = true;
        foreach ((int _, WzImageProperty frame) in frames)
        {
            // Meta already knows how to size a linking canvas (most animated
            // frames past 0 are _inlinks); reuse it per frame.
            string framePath = WzPath.Child(nodePath, frame.Name);
            MapArtDto meta = Meta(frame, framePath);
            if (meta.Missing)
                continue;
            if (first) { z = meta.Z; first = false; }
            dtos.Add(new AnimationFrameDto
            {
                Width = meta.W,
                Height = meta.H,
                OriginX = meta.Ox,
                OriginY = meta.Oy,
                Delay = FrameDelay(frame),
            });
            paths.Add(framePath);
        }
        if (dtos.Count < 2)
            return null;

        (int anchorX, int anchorY, int width, int height, int totalMs) =
            AnimationService.PlaceAtSharedOrigin(dtos);

        MapArtDto art = new()
        {
            Path = paths[0],
            W = width,
            H = height,
            Ox = anchorX,
            Oy = anchorY,
            Z = z,
            Missing = false,
            TotalMs = totalMs,
            Frames = new List<MapArtFrameDto>(dtos.Count),
        };
        for (int i = 0; i < dtos.Count; i++)
        {
            art.Frames.Add(new MapArtFrameDto
            {
                Path = paths[i],
                W = dtos[i].Width,
                H = dtos[i].Height,
                Dx = dtos[i].OffsetX,
                Dy = dtos[i].OffsetY,
                Delay = dtos[i].Delay,
            });
        }
        return art;
    }

    /// <summary>Frame duration in ms; missing or zero is the client's own
    /// 100 ms fallback — matching it keeps playback looking like the game.</summary>
    private static int FrameDelay(WzImageProperty frame)
    {
        try
        {
            WzImageProperty? resolved = frame;
            if (resolved is WzUOLProperty uol)
                resolved = uol.LinkValue as WzImageProperty;
            if (resolved is WzCanvasProperty canvas
                && canvas[WzCanvasProperty.AnimationDelayPropertyName] is { } delay)
            {
                int value = delay.GetInt();
                if (value > 0)
                    return value;
            }
        }
        catch { /* no usable delay property */ }
        return 100;
    }

    /// <summary>
    /// Finds <c>&lt;kind&gt;/&lt;set&gt;.img</c> across every open archive that
    /// carries that library, mount order deciding a name clash — the same rule
    /// the client applies. The set name is matched loosely because the client's
    /// own resolution is case-insensitive.
    /// </summary>
    private (WzImage Image, string Path)? FindSetImage(
        string kind, string set, Dictionary<string, (WzImage, string)?> cache)
    {
        string cacheKey = kind + "|" + set;
        if (cache.TryGetValue(cacheKey, out (WzImage, string)? cached))
            return cached;

        (WzImage, string)? found = null;
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            WzDirectory? root = _session.RoleRoot(file, "Map");
            if (root == null)
                continue;
            foreach (WzDirectory library in root.WzDirectories)
            {
                if (!string.Equals(library.Name, kind, StringComparison.OrdinalIgnoreCase))
                    continue;
                WzImage? image = library.GetImageByName(set + ".img");
                if (image != null)
                {
                    WzSessionService.EnsureParsed(image);
                    found = (image, WzPath.Child(
                        WzPath.Child(_session.RoleRootPath(file, "Map"), library.Name), image.Name));
                    break;
                }
            }
            if (found != null)
                break;
        }
        cache[cacheKey] = found;
        return found;
    }

    /// <summary>
    /// Size, origin and z for a drawable node. A linking canvas (its pixels are
    /// a 1x1 placeholder) borrows the linked target's size but keeps its own
    /// origin when it has one — the link carries the placement, the target the
    /// pixels. The <see cref="MapArtDto.Path"/> stays the original node's path;
    /// /api/canvas follows the link itself when rendering.
    /// </summary>
    private static MapArtDto Meta(WzImageProperty? node, string path)
    {
        WzCanvasProperty? canvas = node as WzCanvasProperty;
        if (canvas == null && node is WzUOLProperty uol && uol.LinkValue is WzCanvasProperty linkedCanvas)
            canvas = linkedCanvas;
        if (canvas == null)
            return new MapArtDto { Missing = true };

        int w = canvas.PngProperty?.Width ?? 0;
        int h = canvas.PngProperty?.Height ?? 0;
        WzVectorProperty? origin = canvas["origin"] as WzVectorProperty;
        int z = (canvas["z"] as WzIntProperty)?.Value ?? 0;

        if (w <= 1 && h <= 1)
        {
            try
            {
                if (canvas.GetLinkedWzImageProperty() is WzCanvasProperty target
                    && !ReferenceEquals(target, canvas))
                {
                    w = target.PngProperty?.Width ?? w;
                    h = target.PngProperty?.Height ?? h;
                    origin ??= target["origin"] as WzVectorProperty;
                }
            }
            catch
            {
                // A dangling link. The size stays 1x1 and the client draws its
                // missing-art marker; the reference itself is preserved.
            }
        }

        return new MapArtDto
        {
            Path = path,
            W = w,
            H = h,
            Ox = origin?.X?.Value ?? 0,
            Oy = origin?.Y?.Value ?? 0,
            Z = z,
            Missing = false,
        };
    }

    #endregion

    #region Portal icons

    /// <summary>
    /// The portal icon table, read from <c>MapHelper.img/portal/editor</c>
    /// <b>child order</b> at runtime. Measured: <c>pt</c> does not index any
    /// alphabetical or documented order — pt 6 is <c>tp</c> in 78 of 78
    /// placements and <c>tp</c> is the sixth child.
    /// </summary>
    public List<MapPortalIconDto> PortalIcons()
    {
        lock (_session.Gate)
        {
            List<MapPortalIconDto> icons = new();
            (WzImage image, string path)? helper = FindHelperImage();
            if (helper == null)
                return icons;

            WzImageProperty? editor = helper.Value.image.GetFromPath("portal/editor");
            if (editor?.WzProperties == null)
                return icons;

            string editorPath = WzPath.Child(WzPath.Child(helper.Value.path, "portal"), "editor");
            int pt = 0;
            foreach (WzImageProperty child in editor.WzProperties)
            {
                MapArtDto meta = Meta(child, WzPath.Child(editorPath, child.Name));
                icons.Add(new MapPortalIconDto
                {
                    Pt = pt++,
                    Name = child.Name,
                    Path = meta.Missing ? null : meta.Path,
                    W = meta.W,
                    H = meta.H,
                    Ox = meta.Ox,
                    Oy = meta.Oy,
                });
            }
            return icons;
        }
    }

    private (WzImage Image, string Path)? FindHelperImage()
    {
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            WzImage? image = _session.RoleRoot(file, "Map")?.GetImageByName("MapHelper.img");
            if (image != null)
            {
                WzSessionService.EnsureParsed(image);
                return (image, WzPath.Child(_session.RoleRootPath(file, "Map"), image.Name));
            }
        }
        return null;
    }

    #endregion

    #region The document record

    private sealed class EditorDoc
    {
        public EditorDoc(string path, MapDocument doc, byte[] iv, int generation)
        {
            Path = path;
            Doc = doc;
            Iv = iv;
            Generation = generation;
            Touched = DateTime.UtcNow;
        }

        public string Path { get; }
        public MapDocument Doc { get; }
        public byte[] Iv { get; }
        public int Generation { get; set; }
        public DateTime Touched { get; set; }
        public Stack<Change> Undo { get; } = new();
        public Stack<Change> Redo { get; } = new();
        public bool Dirty { get; set; }

        /// <summary>The undo depth at which the document equals the saved file.
        /// 0 for a fresh document; set to the current depth by a save (history
        /// survives it); -1 once the clean state is no longer reachable —
        /// undoing below a save point and editing a different way orphans it.</summary>
        public int CleanDepth { get; set; }

        /// <summary>Called by a verified save: the file now equals the model at
        /// this exact depth, and the history on either side of it stays usable.</summary>
        public void MarkSaved()
        {
            CleanDepth = Undo.Count;
            Dirty = false;
        }

        public void Push(Change change)
        {
            // An edit below the save point, replacing the redo path that led
            // back to it: the saved state can no longer be reached by depth.
            if (Undo.Count < CleanDepth)
                CleanDepth = -1;
            Undo.Push(change);
            Redo.Clear();
            Dirty = true;
            while (Undo.Count > MaxUndoDepth)
            {
                // Stack has no trim; rebuild without the oldest entry. Rare
                // enough (200 edits without a save) that clarity wins.
                Change[] kept = Undo.ToArray()[..MaxUndoDepth];
                Undo.Clear();
                for (int i = kept.Length - 1; i >= 0; i--)
                    Undo.Push(kept[i]);
                // The trimmed entry was the bottom of the stack; the clean
                // depth is measured from the bottom, so it moves with it.
                CleanDepth = CleanDepth <= 0 ? -1 : CleanDepth - 1;
            }
        }

        public WzNode NodeAt(int[] addr)
        {
            if (addr == null || addr.Length == 0)
                throw new ArgumentException("An empty address names nothing.");
            if (addr[0] < 0 || addr[0] >= Doc.Nodes.Count)
                throw new ArgumentException("The address is outside the document.");

            WzNode node = Doc.Nodes[addr[0]];
            for (int i = 1; i < addr.Length; i++)
            {
                if (addr[i] < 0 || addr[i] >= node.Children.Count)
                    throw new ArgumentException(
                        $"The address is stale — '{node.Name}' has {node.Children.Count} children and " +
                        $"index {addr[i]} was asked for. Re-fetch the document.");
                node = node.Children[addr[i]];
            }
            return node;
        }
    }

    /// <summary>A reversible edit. Built with its revert; the new state is
    /// captured after the caller applies it, so apply/revert replay exactly.</summary>
    private sealed class Change
    {
        private readonly Action _apply;
        private readonly Action _revert;

        public Change(string label, Action apply, Action revert)
        {
            Label = label;
            _apply = apply;
            _revert = revert;
        }

        public string Label { get; }
        public Action? OnCaptureNew { get; init; }

        public void CaptureNew() => OnCaptureNew?.Invoke();
        public void Apply() => _apply();
        public void Revert() => _revert();

        public static Change Group(string label, params Change[] parts) => new(
            label,
            apply: () => { foreach (Change part in parts) part.Apply(); },
            revert: () =>
            {
                for (int i = parts.Length - 1; i >= 0; i--)
                    parts[i].Revert();
            });
    }

    #endregion
}
