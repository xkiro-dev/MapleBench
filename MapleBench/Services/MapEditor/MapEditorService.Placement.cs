using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Placement: new nodes into the model, with the measured types and the
/// measured constraints enforced BEFORE the write.
///
/// <para>The rules, all from <c>docs/map-data-model.md</c>:</para>
/// <list type="bullet">
/// <item><b>Always-String fields stay String</b>: <c>life/*/id</c>,
/// <c>life/*/type</c>, <c>reactor/*/id</c>, <c>tile/*/u</c>, obj
/// <c>oS/l0/l1/l2/l3</c>, back <c>bS</c>, portal <c>pn/tn/script</c>.</item>
/// <item><b><c>tS</c> is per-layer.</b> Placing a tile from another set is
/// refused with the layers that DO carry that set, so the choice — move layers,
/// or re-skin this one through <see cref="SetLayerTs"/> — is made by the user
/// before anything is written, never inferred after.</item>
/// <item><b>Life anchors to a real foothold</b>: <c>fh</c> must name an
/// existing id and <c>cy</c> is computed from that foothold's own geometry.</item>
/// <item><b>Portal <c>pn</c> is unique on write</b>, except the names the
/// shipping client itself repeats (spawn points are all "sp").</item>
/// <item><b>Foothold chains</b> are written with coincident endpoints and
/// prev/next linking by construction; ids are unique-on-write across the whole
/// map (duplicates stay legal on load). Existing prev/next are never touched.</item>
/// </list>
/// </summary>
public sealed partial class MapEditorService
{
    /// <summary>Portal names the shipping client repeats within one map —
    /// measured, not assumed. Everything else is unique-on-write.</summary>
    private static readonly HashSet<string> RepeatablePortalNames =
        new(StringComparer.Ordinal) { "sp", "tp" };

    // Placement defaults, measured across all 514,109 objects and 1,208,088
    // tiles in the shipping corpus (phase-3 measurement run): obj z is 9 on 69%
    // of objects (median 9), obj zM's mode is 0 (it is a per-map running index
    // on many maps, but 0 is the modal written value), tile zM is 0 on 57%.
    private const int DefaultObjZ = 9;
    private const int DefaultObjZM = 0;
    private const int DefaultTileZM = 0;

    public MapPlaceResultDto Place(MapPlaceRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;

            MapPlaceResultDto result = request.Kind switch
            {
                "tile" => PlaceTile(doc, request),
                "obj" => PlaceObj(doc, request),
                "back" => PlaceBack(doc, request),
                "portal" => PlacePortal(doc, request),
                "life" => PlaceLife(doc, request),
                "reactor" => PlaceReactor(doc, request),
                "ladderRope" => PlaceLadderRope(doc, request),
                _ => throw new ArgumentException(
                    $"'{request.Kind}' is not something this endpoint places. It places tile, obj, "
                    + "back, portal, life, reactor and ladderRope."),
            };

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    #region Tiles and objects

    private MapPlaceResultDto PlaceTile(EditorDoc doc, MapPlaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Set) || string.IsNullOrEmpty(request.U) || request.No is not long no)
            throw new ArgumentException("Placing a tile needs set, u and no.");
        int layerNumber = request.Layer
            ?? throw new ArgumentException("Placing a tile needs a target layer — tS is per-layer.");

        WzNode layer = RequireLayer(doc, layerNumber);
        List<string> notes = new();
        List<Change> changes = new();

        // The single hardest constraint in the editor: the layer's tS decides
        // the set of every tile on it.
        WzNode? info = layer.Child("info");
        WzNode? ts = info?.Child("tS");
        if (ts != null && !string.Equals(ts.AsText(), request.Set, StringComparison.Ordinal))
        {
            int tileCount = layer.Child("tile")?.Children.Count ?? 0;
            string carriers = string.Join(", ", LayersCarrying(doc, request.Set));
            throw new InvalidOperationException(
                $"Layer {layerNumber} draws its tiles from '{ts.AsText()}', and a layer has exactly "
                + $"one tile set — that is measured, not policy (698/698 tiled layers carry a tS). "
                + $"Place this '{request.Set}' tile on a layer that carries it "
                + (carriers.Length > 0 ? $"({carriers}), " : "(none does yet — pick an empty layer), ")
                + $"or change this layer's tS knowingly: that re-skins all {tileCount} tiles already on it.");
        }

        if (ts == null)
        {
            if (!request.AdoptLayerTs)
            {
                throw new InvalidOperationException(
                    $"Layer {layerNumber} has no tile set yet. Placing this tile will write "
                    + $"info/tS = '{request.Set}' on the layer — every future tile on it will come "
                    + "from that set. Confirm to proceed (adoptLayerTs).");
            }
            if (info == null)
            {
                info = WzNode.Container("info");
                changes.Add(InsertChild(layer, 0, info));
            }
            WzNode newTs = WzNode.OfText("tS", WzPropertyType.String, request.Set);
            changes.Add(InsertChild(info, info.Children.Count, newTs));
            notes.Add($"Layer {layerNumber} had no tile set; it now carries tS = '{request.Set}'.");
        }

        // The art must resolve — a tile that does not exist in the set is a
        // refusal, not a placement that draws a pink box forever.
        lock (_session.Gate)
        {
            (WzImage Image, string Path)? set = FindSetImage("Tile", request.Set,
                new Dictionary<string, (WzImage, string)?>(StringComparer.OrdinalIgnoreCase));
            if (set?.Image.GetFromPath($"{request.U}/{no.ToString(CultureInfo.InvariantCulture)}") == null)
            {
                throw new InvalidOperationException(
                    $"Tile/{request.Set}.img/{request.U}/{no} does not resolve in the open session, "
                    + "so placing it would write a reference to nothing.");
            }
        }

        WzNode tileList = layer.Child("tile") ?? CreateContainer(layer, "tile", changes);
        WzNode tile = WzNode.Container(NextNumericName(tileList));
        tile.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        tile.Add(WzNode.Scalar("y", WzPropertyType.Int, request.Y));
        tile.Add(WzNode.Scalar("zM", WzPropertyType.Int, DefaultTileZM));
        tile.Add(WzNode.OfText("u", WzPropertyType.String, request.U));   // measured always-String
        tile.Add(WzNode.Scalar("no", WzPropertyType.Int, no));
        changes.Add(InsertChild(tileList, tileList.Children.Count, tile));

        doc.Push(Change.Group($"place tile {request.Set}/{request.U}/{no}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, tile), Notes = notes };
    }

    private MapPlaceResultDto PlaceObj(EditorDoc doc, MapPlaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Set) || request.L0 == null || request.L1 == null || request.L2 == null)
            throw new ArgumentException("Placing an object needs set (oS) and l0/l1/l2.");
        int layerNumber = request.Layer
            ?? throw new ArgumentException("Placing an object needs a target layer.");

        WzNode layer = RequireLayer(doc, layerNumber);
        List<Change> changes = new();

        lock (_session.Gate)
        {
            (WzImage Image, string Path)? set = FindSetImage("Obj", request.Set,
                new Dictionary<string, (WzImage, string)?>(StringComparer.OrdinalIgnoreCase));
            string artPath = string.Join('/', new[] { request.L0, request.L1, request.L2, request.L3 }
                .Where(s => !string.IsNullOrEmpty(s)));
            if (set?.Image.GetFromPath(artPath) == null)
            {
                throw new InvalidOperationException(
                    $"Obj/{request.Set}.img/{artPath} does not resolve in the open session, so "
                    + "placing it would write a reference to nothing. (The integrity baseline is "
                    + "not zero — 30 obj references ship broken — but this tool does not add to it.)");
            }
        }

        WzNode objList = layer.Child("obj") ?? CreateContainer(layer, "obj", changes);
        WzNode obj = WzNode.Container(NextNumericName(objList));
        obj.Add(WzNode.OfText("oS", WzPropertyType.String, request.Set));
        obj.Add(WzNode.OfText("l0", WzPropertyType.String, request.L0));
        obj.Add(WzNode.OfText("l1", WzPropertyType.String, request.L1));
        obj.Add(WzNode.OfText("l2", WzPropertyType.String, request.L2));
        if (!string.IsNullOrEmpty(request.L3))
            obj.Add(WzNode.OfText("l3", WzPropertyType.String, request.L3));
        obj.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        obj.Add(WzNode.Scalar("y", WzPropertyType.Int, request.Y));
        obj.Add(WzNode.Scalar("z", WzPropertyType.Int, DefaultObjZ));
        obj.Add(WzNode.Scalar("f", WzPropertyType.Int, request.Flip ? 1 : 0));
        obj.Add(WzNode.Scalar("zM", WzPropertyType.Int, DefaultObjZM));
        changes.Add(InsertChild(objList, objList.Children.Count, obj));

        doc.Push(Change.Group($"place obj {request.Set}/{request.L0}/{request.L1}/{request.L2}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, obj) };
    }

    private IEnumerable<string> LayersCarrying(EditorDoc doc, string set)
    {
        foreach (WzNode node in doc.Doc.Nodes)
        {
            if (!MapNodeKinds.IsLayerName(node.Name))
                continue;
            if (string.Equals(node.Descend("info/tS")?.AsText(), set, StringComparison.Ordinal))
                yield return node.Name;
        }
    }

    #endregion

    #region Backgrounds

    private MapPlaceResultDto PlaceBack(EditorDoc doc, MapPlaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Set) || request.No is not long no)
            throw new ArgumentException("Placing a background needs set (bS) and no.");
        long ani = request.Ani ?? 0;
        long type = request.BackType ?? 0;
        if (type is < 0 or > 7)
            throw new ArgumentException("Background type is 0-7 — the eight measured values.");

        lock (_session.Gate)
        {
            (WzImage Image, string Path)? set = FindSetImage("Back", request.Set,
                new Dictionary<string, (WzImage, string)?>(StringComparer.OrdinalIgnoreCase));
            string branch = ani switch { 1 => "ani", 2 => "spine", _ => "back" };
            if (set?.Image.GetFromPath($"{branch}/{no.ToString(CultureInfo.InvariantCulture)}") == null)
            {
                throw new InvalidOperationException(
                    $"Back/{request.Set}.img/{branch}/{no} does not resolve in the open session, so "
                    + "placing it would write a reference to nothing.");
            }
        }

        List<Change> changes = new();
        WzNode backList = doc.Doc.Find("back") ?? CreateTopLevel(doc, "back", changes);

        // Child order is the measured modal one — bS,front,ani,no,f,x,y,rx,ry,
        // type,cx,cy,a on 49.5% of all 184,602 shipped entries. Types 4-7 move
        // by themselves; rx/ry there are scroll speeds and 0 is "does not move"
        // — a legal, visible starting point, not a guess.
        WzNode back = WzNode.Container(NextNumericName(backList));
        back.Add(WzNode.OfText("bS", WzPropertyType.String, request.Set)); // measured always-String
        back.Add(WzNode.Scalar("front", WzPropertyType.Int, request.Front ?? 0));
        back.Add(WzNode.Scalar("ani", WzPropertyType.Int, ani));
        back.Add(WzNode.Scalar("no", WzPropertyType.Int, no));
        back.Add(WzNode.Scalar("f", WzPropertyType.Int, request.Flip ? 1 : 0));
        back.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        back.Add(WzNode.Scalar("y", WzPropertyType.Int, request.Y));
        back.Add(WzNode.Scalar("rx", WzPropertyType.Int, 0));
        back.Add(WzNode.Scalar("ry", WzPropertyType.Int, 0));
        back.Add(WzNode.Scalar("type", WzPropertyType.Int, type));
        back.Add(WzNode.Scalar("cx", WzPropertyType.Int, 0));
        back.Add(WzNode.Scalar("cy", WzPropertyType.Int, 0));
        back.Add(WzNode.Scalar("a", WzPropertyType.Int, 255));
        changes.Add(InsertChild(backList, backList.Children.Count, back));

        doc.Push(Change.Group($"place back {request.Set}/{no}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, back) };
    }

    #endregion

    #region Portals

    private MapPlaceResultDto PlacePortal(EditorDoc doc, MapPlaceRequest request)
    {
        string pn = request.Pn ?? "";
        long pt = request.Pt ?? 0;

        // pn is how scripts and tn references name a portal; a silent duplicate
        // is a portal that can never be addressed. The exceptions are the names
        // the client itself repeats (every spawn point is "sp").
        if (pn.Length > 0 && !RepeatablePortalNames.Contains(pn))
        {
            WzNode? existing = doc.Doc.Find("portal");
            if (existing != null && existing.Children.Any(
                    p => string.Equals(p.TextAt("pn"), pn, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"A portal named '{pn}' already exists in this map. Portal names are how tn "
                    + "references and scripts address them; two of them would leave one unreachable. "
                    + "(Only 'sp' and 'tp' legally repeat — the shipping client does it itself.)");
            }
        }

        List<Change> changes = new();
        WzNode portalList = doc.Doc.Find("portal") ?? CreateTopLevel(doc, "portal", changes);

        WzNode portal = WzNode.Container(NextNumericName(portalList));
        portal.Add(WzNode.OfText("pn", WzPropertyType.String, pn));      // measured always-String
        portal.Add(WzNode.Scalar("pt", WzPropertyType.Int, pt));
        portal.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        portal.Add(WzNode.Scalar("y", WzPropertyType.Int, request.Y));
        portal.Add(WzNode.Scalar("tm", WzPropertyType.Int, request.Tm ?? 999999999)); // sentinel: none
        portal.Add(WzNode.OfText("tn", WzPropertyType.String, request.Tn ?? ""));
        if (!string.IsNullOrEmpty(request.Script))
            portal.Add(WzNode.OfText("script", WzPropertyType.String, request.Script));
        changes.Add(InsertChild(portalList, portalList.Children.Count, portal));

        doc.Push(Change.Group($"place portal {pn}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, portal) };
    }

    #endregion

    #region Life and reactors

    private MapPlaceResultDto PlaceLife(EditorDoc doc, MapPlaceRequest request)
    {
        string type = request.LifeType ?? "";
        if (type is not ("m" or "n"))
            throw new ArgumentException("Life type is 'm' (mob) or 'n' (NPC).");
        if (string.IsNullOrEmpty(request.Id))
            throw new ArgumentException("Placing life needs an id.");

        WzNode? lifeRoot = doc.Doc.Find("life");
        if (lifeRoot?.Child("isCategory")?.AsInteger() == 1)
        {
            throw new InvalidOperationException(
                "This map's life list is categorised (life/isCategory) — one of 25 such maps. "
                + "Its spawns nest under category ids whose meaning lives in the server, so this "
                + "editor shows them but does not author into them.");
        }

        // The anchor: fh must reference a real foothold, and cy comes from that
        // foothold's own geometry — never from the drop point.
        (long fhId, long cy) = AnchorToFoothold(doc, request.X, request.Y, request.Fh);
        List<string> notes = new();
        if (request.Fh == null)
            notes.Add($"Anchored to foothold {fhId}; cy computed as {cy}.");

        List<Change> changes = new();
        lifeRoot ??= CreateTopLevel(doc, "life", changes);

        // The measured universal spawn shape: id/type/x/y/fh/cy/rx0/rx1 are on
        // 100% of the 92,601 ordinary entries, f on ~100%, hide on 98%, and
        // mobTime on 99.4% of mobs AND 99.6% of NPCs (modal 0; -1 = never
        // respawn). rx0/rx1 default to the modal x∓50.
        WzNode life = WzNode.Container(NextNumericName(lifeRoot));
        life.Add(WzNode.OfText("id", WzPropertyType.String, request.Id));     // measured always-String
        life.Add(WzNode.OfText("type", WzPropertyType.String, type));         // measured always-String
        life.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        life.Add(WzNode.Scalar("y", WzPropertyType.Int, cy));
        life.Add(WzNode.Scalar("fh", WzPropertyType.Int, fhId));
        life.Add(WzNode.Scalar("cy", WzPropertyType.Int, cy));
        life.Add(WzNode.Scalar("rx0", WzPropertyType.Int, request.X - 50));
        life.Add(WzNode.Scalar("rx1", WzPropertyType.Int, request.X + 50));
        life.Add(WzNode.Scalar("f", WzPropertyType.Int, request.Flip ? 1 : 0));
        life.Add(WzNode.Scalar("hide", WzPropertyType.Int, 0));
        life.Add(WzNode.Scalar("mobTime", WzPropertyType.Int, request.MobTime ?? 0));
        changes.Add(InsertChild(lifeRoot, lifeRoot.Children.Count, life));

        doc.Push(Change.Group($"place {(type == "m" ? "mob" : "npc")} {request.Id}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, life), Notes = notes };
    }

    /// <summary>
    /// Finds the foothold a spawn stands on: the requested id when given, or the
    /// nearest foothold at-or-below the drop point. Returns its id and the
    /// computed <c>cy</c> — the foothold's own y at the spawn's x.
    /// </summary>
    private static (long Id, long Cy) AnchorToFoothold(EditorDoc doc, long x, long y, long? requestedId)
    {
        WzNode? root = doc.Doc.Find("foothold");
        if (root == null)
        {
            throw new InvalidOperationException(
                "This map has no footholds, so a spawn has nothing to stand on. Draw footholds "
                + "first — life/fh must reference a real foothold id.");
        }

        (long Id, long Cy, long Distance)? best = null;
        foreach (WzNode layer in root.Children)
        {
            foreach (WzNode group in layer.Children)
            {
                foreach (WzNode fh in group.Children)
                {
                    if (!long.TryParse(fh.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
                        continue;
                    long x1 = fh.IntegerAt("x1") ?? 0, y1 = fh.IntegerAt("y1") ?? 0;
                    long x2 = fh.IntegerAt("x2") ?? 0, y2 = fh.IntegerAt("y2") ?? 0;

                    if (requestedId is long wanted)
                    {
                        if (id != wanted)
                            continue;
                        long cx = Math.Clamp(x, Math.Min(x1, x2), Math.Max(x1, x2));
                        return (id, YAt(x1, y1, x2, y2, cx));
                    }

                    // A wall (x1 == x2) is not standable; a platform below the
                    // drop point is; the nearest one wins.
                    if (x1 == x2 || x < Math.Min(x1, x2) || x > Math.Max(x1, x2))
                        continue;
                    long yAt = YAt(x1, y1, x2, y2, x);
                    if (yAt < y)
                        continue; // above the drop point (y grows downward)
                    long distance = yAt - y;
                    if (best == null || distance < best.Value.Distance)
                        best = (id, yAt, distance);
                }
            }
        }

        if (requestedId is long missing)
        {
            throw new InvalidOperationException(
                $"No foothold with id {missing} exists in this map, and life/fh must reference a "
                + "real one — a spawn on a phantom foothold falls forever.");
        }
        if (best == null)
        {
            throw new InvalidOperationException(
                "No foothold lies below this point, so the spawn has nothing to stand on. Drop it "
                + "above a platform, or pass fh explicitly.");
        }
        return (best.Value.Id, best.Value.Cy);

        static long YAt(long x1, long y1, long x2, long y2, long atX)
        {
            if (x1 == x2)
                return Math.Max(y1, y2);
            double t = (double)(atX - x1) / (x2 - x1);
            return (long)Math.Round(y1 + t * (y2 - y1), MidpointRounding.AwayFromZero);
        }
    }

    private MapPlaceResultDto PlaceReactor(EditorDoc doc, MapPlaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Id))
            throw new ArgumentException("Placing a reactor needs an id.");

        List<Change> changes = new();
        WzNode reactorList = doc.Doc.Find("reactor") ?? CreateTopLevel(doc, "reactor", changes);

        WzNode reactor = WzNode.Container(NextNumericName(reactorList));
        reactor.Add(WzNode.OfText("id", WzPropertyType.String, request.Id)); // measured always-String
        reactor.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        reactor.Add(WzNode.Scalar("y", WzPropertyType.Int, request.Y));
        reactor.Add(WzNode.Scalar("reactorTime", WzPropertyType.Int, 0));
        reactor.Add(WzNode.Scalar("f", WzPropertyType.Int, request.Flip ? 1 : 0));
        reactor.Add(WzNode.OfText("name", WzPropertyType.String, ""));
        changes.Add(InsertChild(reactorList, reactorList.Children.Count, reactor));

        doc.Push(Change.Group($"place reactor {request.Id}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, reactor) };
    }

    /// <summary>
    /// A ladder or rope. The shape is the one every sampled shipping entry
    /// carries, in its order: <c>l, uf, x, y1, y2, page</c>, all Int (some
    /// entries add a trailing <c>piece</c>; this writer does not invent one).
    /// <c>l</c>: 1 ladder, 0 rope. <c>uf</c>: 1 = can climb off the top.
    /// <c>page</c> is the layer the ladder belongs with. y1 is the TOP
    /// (smaller y — y grows downward); the two are swapped into that order
    /// rather than refused, because which end was clicked first is not a
    /// mistake.
    /// </summary>
    private MapPlaceResultDto PlaceLadderRope(EditorDoc doc, MapPlaceRequest request)
    {
        if (request.Y2 is not long y2Raw)
            throw new ArgumentException("Placing a ladder needs y (top) and y2 (bottom).");
        long y1 = Math.Min(request.Y, y2Raw);
        long y2 = Math.Max(request.Y, y2Raw);
        if (y1 == y2)
            throw new ArgumentException("A ladder needs two distinct heights — it has zero length.");
        long l = request.L ?? 1;
        if (l is not (0 or 1))
            throw new ArgumentException("l is 1 (ladder) or 0 (rope) — the two shipped values.");
        long uf = request.Uf ?? 1;
        long page = request.Layer ?? 0;

        List<Change> changes = new();
        WzNode list = doc.Doc.Find("ladderRope") ?? CreateTopLevel(doc, "ladderRope", changes);

        WzNode ladder = WzNode.Container(NextNumericName(list));
        ladder.Add(WzNode.Scalar("l", WzPropertyType.Int, l));
        ladder.Add(WzNode.Scalar("uf", WzPropertyType.Int, uf));
        ladder.Add(WzNode.Scalar("x", WzPropertyType.Int, request.X));
        ladder.Add(WzNode.Scalar("y1", WzPropertyType.Int, y1));
        ladder.Add(WzNode.Scalar("y2", WzPropertyType.Int, y2));
        ladder.Add(WzNode.Scalar("page", WzPropertyType.Int, page));
        changes.Add(InsertChild(list, list.Children.Count, ladder));

        doc.Push(Change.Group($"place {(l == 1 ? "ladder" : "rope")}", changes.ToArray()));
        return new MapPlaceResultDto { Placed = AddrOf(doc, ladder) };
    }

    #endregion

    #region Foothold authoring

    public MapPlaceResultDto AddFootholdChain(MapFootholdChainRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;

            List<(long X, long Y)> points = (request.Points ?? new List<MapPointDto>())
                .Select(p => (p.X, p.Y)).ToList();
            MapPlaceResultDto result = WriteFootholdChains(
                doc, request.Layer, new List<List<(long, long)>> { points },
                $"draw foothold chain ({points.Count} points)");

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    /// <summary>
    /// Generates footholds from a placed tile or object's own art geometry —
    /// <c>Tile/&lt;set&gt;.img/&lt;u&gt;/&lt;no&gt;/foothold</c> is a Convex of
    /// Vectors and obj frames carry them too. The art's points are offsets from
    /// the entry's (x, y); a flipped entry mirrors the x offsets.
    /// </summary>
    public MapPlaceResultDto AutoFoothold(MapAutoFootholdRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;

            WzNode entry = doc.NodeAt(request.Addr);
            if (request.Addr.Length < 3)
                throw new ArgumentException("The address does not name a tile or object.");
            WzNode layerNode = doc.Doc.Nodes[request.Addr[0]];
            if (!MapNodeKinds.IsLayerName(layerNode.Name)
                || !int.TryParse(layerNode.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int layerNumber))
                throw new ArgumentException("Auto-footholds come from a tile or object inside a layer.");
            WzNode listNode = doc.NodeAt(request.Addr[..^1]);

            long x = entry.IntegerAt("x") ?? 0;
            long y = entry.IntegerAt("y") ?? 0;
            bool flipped = (entry.IntegerAt("f") ?? 0) == 1;

            List<List<(int X, int Y)>>? artChains;
            string what;
            lock (_session.Gate)
            {
                Dictionary<string, (WzImage, string)?> cache = new(StringComparer.OrdinalIgnoreCase);
                switch (listNode.Name)
                {
                    case "tile":
                    {
                        string? set = layerNode.Descend("info/tS")?.AsText();
                        string? u = entry.TextAt("u");
                        long no = entry.IntegerAt("no") ?? 0;
                        if (set == null || u == null)
                            throw new InvalidOperationException("The tile has no resolvable set/u.");
                        what = $"Tile/{set}.img/{u}/{no}";
                        WzImageProperty? art = FindSetImage("Tile", set, cache)?.Image
                            .GetFromPath($"{u}/{no.ToString(CultureInfo.InvariantCulture)}");
                        artChains = art == null ? null : FindFootholdGeometry(art);
                        break;
                    }
                    case "obj":
                    {
                        string? oS = entry.TextAt("oS");
                        string artPath = string.Join('/', new[]
                        {
                            entry.TextAt("l0"), entry.TextAt("l1"), entry.TextAt("l2"), entry.TextAt("l3"),
                        }.Where(s => !string.IsNullOrEmpty(s)));
                        if (oS == null || artPath.Length == 0)
                            throw new InvalidOperationException("The object has no resolvable oS/l path.");
                        what = $"Obj/{oS}.img/{artPath}";
                        WzImageProperty? leaf = FindSetImage("Obj", oS, cache)?.Image.GetFromPath(artPath);
                        WzImageProperty? frame = leaf != null
                            && leaf.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL
                            ? leaf.WzProperties?.FirstOrDefault(p => p.Name == "0") ?? leaf
                            : leaf;
                        artChains = frame == null ? null : FindFootholdGeometry(frame);
                        break;
                    }
                    default:
                        throw new ArgumentException(
                            $"'{listNode.Name}' entries do not carry art geometry; tiles and objects do.");
                }
            }

            if (artChains == null || artChains.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{what} carries no foothold geometry of its own, so there is nothing to "
                    + "generate from. Draw the chain by hand instead — generating a guess here "
                    + "would be exactly the silent invention this tool refuses.");
            }

            List<List<(long X, long Y)>> worldChains = artChains
                .Select(chain => chain
                    .Select(p => (x + (flipped ? -(long)p.X : p.X), y + (long)p.Y))
                    .ToList())
                .ToList();

            MapPlaceResultDto result = WriteFootholdChains(
                doc, layerNumber, worldChains, $"auto-footholds from {what}");
            result.Notes.Insert(0,
                $"{worldChains.Count} chain{(worldChains.Count == 1 ? "" : "s")} generated from "
                + $"{what}'s own geometry{(flipped ? ", mirrored for the flip" : "")}.");

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    /// <summary>
    /// Writes vertex chains as footholds: coincident endpoints by construction,
    /// prev/next linked within each chain, ids unique-on-write across the whole
    /// map, group allocated automatically, layer scoped. Never touches any
    /// existing foothold's prev or next.
    /// </summary>
    private MapPlaceResultDto WriteFootholdChains(
        EditorDoc doc, int layerNumber, List<List<(long X, long Y)>> chains, string label)
    {
        chains = chains
            .Select(c => c.Where((p, i) => i == 0 || p != c[i - 1]).ToList()) // drop zero-length segments
            .Where(c => c.Count >= 2)
            .ToList();
        if (chains.Count == 0)
            throw new ArgumentException("A foothold chain needs at least two distinct points.");

        List<Change> changes = new();
        WzNode root = doc.Doc.Find("foothold") ?? CreateTopLevel(doc, "foothold", changes);

        string layerName = layerNumber.ToString(CultureInfo.InvariantCulture);
        WzNode? layer = root.Child(layerName);
        if (layer == null)
        {
            layer = WzNode.Container(layerName);
            changes.Add(InsertChild(root, root.Children.Count, layer));
        }

        // Unique-on-write: the next id after everything the map already uses,
        // whatever layer or group it sits in (69 shipping maps carry duplicates;
        // this tool never adds one).
        long nextId = 1;
        foreach (WzNode l in root.Children)
            foreach (WzNode g in l.Children)
                foreach (WzNode f in g.Children)
                {
                    if (long.TryParse(f.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id)
                        && id >= nextId)
                        nextId = id + 1;
                }

        WzNode group = WzNode.Container(NextNumericName(layer));
        changes.Add(InsertChild(layer, layer.Children.Count, group));

        WzNode? firstFoothold = null;
        int written = 0;
        foreach (List<(long X, long Y)> chain in chains)
        {
            for (int i = 0; i < chain.Count - 1; i++)
            {
                long id = nextId++;
                WzNode fh = WzNode.Container(id.ToString(CultureInfo.InvariantCulture));
                fh.Add(WzNode.Scalar("x1", WzPropertyType.Int, chain[i].X));
                fh.Add(WzNode.Scalar("y1", WzPropertyType.Int, chain[i].Y));
                fh.Add(WzNode.Scalar("x2", WzPropertyType.Int, chain[i + 1].X));
                fh.Add(WzNode.Scalar("y2", WzPropertyType.Int, chain[i + 1].Y));
                fh.Add(WzNode.Scalar("prev", WzPropertyType.Int, i == 0 ? 0 : id - 1));
                fh.Add(WzNode.Scalar("next", WzPropertyType.Int, i == chain.Count - 2 ? 0 : id + 1));
                group.Add(fh);
                firstFoothold ??= fh;
                written++;
            }
        }

        doc.Push(Change.Group(label, changes.ToArray()));

        MapPlaceResultDto result = new()
        {
            Placed = firstFoothold != null ? AddrOf(doc, firstFoothold) : Array.Empty<int>(),
        };
        result.Notes.Add(
            $"{written} foothold{(written == 1 ? "" : "s")} written into layer {layerNumber}, "
            + $"group {group.Name}, ids from {(nextId - written)}.");
        return result;
    }

    #endregion

    #region Layer tile set

    public MapPlaceResultDto SetLayerTs(MapLayerTsRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(request.Path);
            doc.Touched = DateTime.UtcNow;
            if (string.IsNullOrEmpty(request.Ts))
                throw new ArgumentException("set-layer-ts needs the tile set name.");

            WzNode layer = RequireLayer(doc, request.Layer);
            WzNode? info = layer.Child("info");
            WzNode? ts = info?.Child("tS");
            int tileCount = layer.Child("tile")?.Children.Count ?? 0;

            if (tileCount > 0 && !request.ConfirmReskin
                && !string.Equals(ts?.AsText(), request.Ts, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Layer {request.Layer} has {tileCount} tiles drawn from "
                    + $"'{ts?.AsText() ?? "(no set)"}'. Changing tS re-skins every one of them to "
                    + $"'{request.Ts}' — same u/no, different pictures. Confirm to proceed "
                    + "(confirmReskin); this is stated before the write because afterwards is too late.");
            }

            lock (_session.Gate)
            {
                if (FindSetImage("Tile", request.Ts,
                        new Dictionary<string, (WzImage, string)?>(StringComparer.OrdinalIgnoreCase)) == null)
                {
                    throw new InvalidOperationException(
                        $"Tile/{request.Ts}.img does not resolve in the open session, so pointing "
                        + "this layer at it would blank every tile.");
                }
            }

            MapPlaceResultDto result = new() { Placed = Array.Empty<int>() };
            if (ts != null)
            {
                Change change = ValueChange(ts, $"set layer {request.Layer} tS");
                ts.SetText(request.Ts);
                change.CaptureNew();
                doc.Push(change);
                if (tileCount > 0)
                    result.Notes.Add($"{tileCount} tiles on layer {request.Layer} now draw from '{request.Ts}'.");
            }
            else
            {
                List<Change> changes = new();
                if (info == null)
                {
                    info = WzNode.Container("info");
                    changes.Add(InsertChild(layer, 0, info));
                }
                WzNode created = WzNode.OfText("tS", WzPropertyType.String, request.Ts);
                changes.Add(InsertChild(info, info.Children.Count, created));
                doc.Push(Change.Group($"set layer {request.Layer} tS", changes.ToArray()));
                result.Notes.Add($"Layer {request.Layer} now carries tS = '{request.Ts}'.");
            }

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    #endregion

    #region Structural helpers

    private static WzNode RequireLayer(EditorDoc doc, int layerNumber)
    {
        string name = layerNumber.ToString(CultureInfo.InvariantCulture);
        foreach (WzNode node in doc.Doc.Nodes)
        {
            if (MapNodeKinds.IsLayerName(node.Name) && string.Equals(node.Name, name, StringComparison.Ordinal))
                return node;
        }
        throw new InvalidOperationException(
            $"This map has no layer {layerNumber}. Layers are read from the image, never assumed "
            + "— place onto one of the layers the map actually has.");
    }

    /// <summary>The next free numeric child name — entry lists are dense 0..n
    /// in shipping maps, and max+1 never collides even when they are not.</summary>
    private static string NextNumericName(WzNode container)
    {
        long next = 0;
        foreach (WzNode child in container.Children)
        {
            if (long.TryParse(child.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
                && value >= next)
                next = value + 1;
        }
        return next.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An applied, reversible child insertion. The child is inserted
    /// now; the Change replays or reverts it.</summary>
    private static Change InsertChild(WzNode parent, int index, WzNode child)
    {
        int at = Math.Min(index, parent.Children.Count);
        parent.Insert(at, child);
        return new Change($"add {child.Name}",
            apply: () => parent.Insert(Math.Min(at, parent.Children.Count), child),
            revert: () => parent.Remove(child));
    }

    private static WzNode CreateContainer(WzNode parent, string name, List<Change> changes)
    {
        WzNode container = WzNode.Container(name);
        changes.Add(InsertChild(parent, parent.Children.Count, container));
        return container;
    }

    /// <summary>Creates a missing top-level container (back, portal, life,
    /// reactor, foothold) at the end of the image, undoably.</summary>
    private static WzNode CreateTopLevel(EditorDoc doc, string name, List<Change> changes)
    {
        WzNode node = WzNode.Container(name);
        int at = doc.Doc.Nodes.Count;
        doc.Doc.InsertNode(at, node);
        changes.Add(new Change($"add {name}",
            apply: () => doc.Doc.InsertNode(Math.Min(at, doc.Doc.Nodes.Count), node),
            revert: () => doc.Doc.RemoveNode(node)));
        return node;
    }

    /// <summary>The index path of a node, found by identity walk from the root.</summary>
    private static int[] AddrOf(EditorDoc doc, WzNode target)
    {
        List<int> trail = new();
        for (int i = 0; i < doc.Doc.Nodes.Count; i++)
        {
            trail.Add(i);
            if (FindIn(doc.Doc.Nodes[i], target, trail))
                return trail.ToArray();
            trail.RemoveAt(trail.Count - 1);
        }
        return Array.Empty<int>();

        static bool FindIn(WzNode node, WzNode target, List<int> trail)
        {
            if (ReferenceEquals(node, target))
                return true;
            for (int i = 0; i < node.Children.Count; i++)
            {
                trail.Add(i);
                if (FindIn(node.Children[i], target, trail))
                    return true;
                trail.RemoveAt(trail.Count - 1);
            }
            return false;
        }
    }

    #endregion
}
