using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MapleBench.Services.MapModel;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// The direct-manipulation ops: batch moves, batch deletes, duplication,
/// foothold vertex surgery, ladder and rect-zone editing, field writes, and the
/// named history — everything the canvas's selection/drag layer commits through.
///
/// <para>The rules are the same ones the single-item ops enforce, applied to
/// many things inside ONE undo group per gesture: a life spawn re-anchors and
/// never splits its y/cy pairing; a foothold endpoint drags every linked
/// coincident endpoint with it (and never rewrites prev/next asymmetry); a
/// deleted foothold's surviving neighbours have their dangling prev/next set to
/// 0 — honest unlinking on an explicit delete, never a silent "repair" of the
/// 6,744 legal forks.</para>
/// </summary>
public sealed partial class MapEditorService
{
    /// <summary>Fields whose value decides which ART an entry draws — writing
    /// one changes the doc's art map, so the client must re-fetch.</summary>
    private static readonly HashSet<string> ArtDecidingFields =
        new(StringComparer.Ordinal) { "u", "no", "oS", "l0", "l1", "l2", "l3", "bS", "ani" };

    /// <summary>The top-level kinds the doc serves as visible rect zones:
    /// <c>area</c> (named entries), <c>ToolTip</c>, and the ten numbered
    /// <see cref="MapNodeKinds.RectKinds"/>.</summary>
    private static readonly HashSet<string> RectDocKinds = BuildRectKinds();

    private static HashSet<string> BuildRectKinds()
    {
        HashSet<string> kinds = new(MapNodeKinds.RectKinds, StringComparer.Ordinal)
        {
            "area",
            "ToolTip",
        };
        return kinds;
    }

    private static bool IsRectKind(string name) => RectDocKinds.Contains(name);

    private static void ReadRects(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode child = root.Children[i];
            long? x1 = child.IntegerAt("x1");
            long? y1 = child.IntegerAt("y1");
            long? x2 = child.IntegerAt("x2");
            long? y2 = child.IntegerAt("y2");
            if (x1 == null || y1 == null || x2 == null || y2 == null)
                continue;   // not rectangle-shaped; the inspector still shows it raw
            dto.Rects.Add(new MapRectDto
            {
                Addr = new[] { index, i },
                Kind = root.Name,
                Name = child.Name,
                X1 = x1.Value,
                Y1 = y1.Value,
                X2 = x2.Value,
                Y2 = y2.Value,
            });
        }
    }

    #region History

    /// <summary>The undo/redo stacks by label, top first — so the history
    /// panel can say what Ctrl+Z is about to take back.</summary>
    public MapHistoryDto History(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            return new MapHistoryDto
            {
                Undo = doc.Undo.Select(c => c.Label).ToList(),
                Redo = doc.Redo.Select(c => c.Label).ToList(),
            };
        }
    }

    #endregion

    #region setField

    /// <summary>Writes one named scalar child of an entry — the floating panel's
    /// op. The type is preserved by the same setter setValue uses; fields whose
    /// value picks the art (tile <c>no</c>, obj <c>l2</c>…) report structural so
    /// the client re-fetches the re-resolved art map.</summary>
    private static MapEditResultDto SetField(EditorDoc doc, MapEditOp op)
    {
        if (string.IsNullOrEmpty(op.Name))
            throw new ArgumentException("setField needs the field name.");
        if (op.Value == null)
            throw new ArgumentException("setField needs a value.");

        WzNode entry = doc.NodeAt(op.Addr);
        WzNode? field = entry.Child(op.Name);
        if (field == null)
        {
            throw new InvalidOperationException(
                $"'{entry.Name}' has no '{op.Name}' child. Fields are edited, never invented — "
                + "absent is a meaning of its own in this format.");
        }

        Change change = ValueChange(field, $"set {field.Name}");
        field.SetText(op.Value);
        change.CaptureNew();
        doc.Push(change);

        return new MapEditResultDto { Structural = ArtDecidingFields.Contains(op.Name) };
    }

    #endregion

    #region moveMany

    /// <summary>
    /// Moves a whole selection by one delta, as ONE undo entry. Each target
    /// moves by its own kind's rules: plain x/y entries shift; life spawns
    /// re-anchor to the foothold under their new point (skipped with a note
    /// when nothing is below — a batch does not fail on one spawn); footholds
    /// move BOTH endpoints, dragging every linked coincident endpoint along,
    /// each physical junction moving exactly once however many selected
    /// segments share it; ladders shift x/y1/y2; rect zones shift all corners.
    /// </summary>
    private MapEditResultDto MoveMany(EditorDoc doc, MapEditOp op)
    {
        if (op.Dx is not long dx || op.Dy is not long dy)
            throw new ArgumentException("moveMany needs dx and dy.");
        if (op.Items == null || op.Items.Count == 0)
            throw new ArgumentException("moveMany needs items.");

        List<Change> changes = new();
        List<string> notes = new();
        MapEditResultDto result = new() { Structural = false };

        void Write(WzNode node, long value)
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }

        // Footholds first, all together: collect every (node, vertex) endpoint
        // the selection drags — selected segments' own endpoints plus their
        // linked coincident partners — so a junction three selected segments
        // share still moves exactly once.
        HashSet<(WzNode Node, int Vertex)> endpoints = new();
        List<(WzNode Node, int RootIndex)> footholds = new();
        foreach (MapEditTargetDto item in op.Items.Where(i => i.Kind == "foothold"))
        {
            if (item.Addr.Length != 4)
                throw new ArgumentException("A foothold address is foothold/layer/group/id — four segments.");
            WzNode root = doc.Doc.Nodes[item.Addr[0]];
            if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
                throw new ArgumentException("The foothold item does not address a foothold.");
            WzNode layer = root.Children[item.Addr[1]];
            WzNode fh = doc.NodeAt(item.Addr);
            footholds.Add((fh, item.Addr[0]));
            CollectLinkedEndpoints(layer, fh, 1, endpoints);
            CollectLinkedEndpoints(layer, fh, 2, endpoints);
        }
        HashSet<WzNode> movedFootholds = new();
        foreach ((WzNode fh, int vertex) in endpoints)
        {
            (string xa, string ya) = vertex == 1 ? ("x1", "y1") : ("x2", "y2");
            WzNode? xNode = fh.Child(xa);
            WzNode? yNode = fh.Child(ya);
            if (xNode == null || yNode == null)
                continue;
            Write(xNode, xNode.Integer + dx);
            Write(yNode, yNode.Integer + dy);
            movedFootholds.Add(fh);
        }

        int movedCount = 0;
        string? singleLabel = null;
        foreach (MapEditTargetDto item in op.Items)
        {
            WzNode node = doc.NodeAt(item.Addr);
            switch (item.Kind)
            {
                case "foothold":
                    movedCount++;
                    singleLabel = $"move foothold {node.Name}";
                    break;

                case "life":
                {
                    WzNode? x = node.Child("x");
                    WzNode? y = node.Child("y");
                    if (x == null || y == null)
                        throw new InvalidOperationException($"'{node.Name}' has no x/y to move.");
                    long oldX = x.Integer;
                    long oldCy = node.Child("cy")?.Integer ?? y.Integer;
                    long nx = oldX + dx;
                    long targetY = oldCy + dy;
                    long fhId, cy;
                    try
                    {
                        (fhId, cy) = AnchorToFoothold(doc, nx, targetY, null);
                    }
                    catch (InvalidOperationException)
                    {
                        notes.Add($"Spawn {node.TextAt("id") ?? node.Name} not moved — no foothold "
                            + "lies below its new point, and a spawn never floats.");
                        continue;
                    }
                    Write(x, nx);
                    Write(y, cy);
                    if (node.Child("cy") is WzNode cyNode)
                        Write(cyNode, cy);
                    if (node.Child("fh") is WzNode fhNode && fhNode.Integer != fhId)
                    {
                        notes.Add($"Spawn {node.TextAt("id") ?? node.Name} re-anchored to foothold {fhId}.");
                        Write(fhNode, fhId);
                    }
                    else if (node.Child("fh") is WzNode sameFh)
                    {
                        Write(sameFh, fhId);
                    }
                    if (dx != 0)
                    {
                        if (node.Child("rx0") is WzNode rx0) Write(rx0, rx0.Integer + dx);
                        if (node.Child("rx1") is WzNode rx1) Write(rx1, rx1.Integer + dx);
                    }
                    result.Moved.Add(new MapMovedDto { Addr = item.Addr, X1 = nx, Y1 = cy });
                    movedCount++;
                    singleLabel = $"move life {node.TextAt("id") ?? node.Name}";
                    break;
                }

                case "ladder":
                {
                    WzNode? x = node.Child("x");
                    WzNode? y1 = node.Child("y1");
                    WzNode? y2 = node.Child("y2");
                    if (x == null || y1 == null || y2 == null)
                        throw new InvalidOperationException($"'{node.Name}' has no x/y1/y2 to move.");
                    Write(x, x.Integer + dx);
                    Write(y1, y1.Integer + dy);
                    Write(y2, y2.Integer + dy);
                    result.Moved.Add(new MapMovedDto
                    {
                        Addr = item.Addr, X1 = x.Integer, Y1 = y1.Integer, X2 = x.Integer, Y2 = y2.Integer,
                    });
                    movedCount++;
                    singleLabel = "move ladder / rope";
                    break;
                }

                case "rect":
                {
                    WzNode? x1 = node.Child("x1");
                    WzNode? y1 = node.Child("y1");
                    WzNode? x2 = node.Child("x2");
                    WzNode? y2 = node.Child("y2");
                    if (x1 == null || y1 == null || x2 == null || y2 == null)
                        throw new InvalidOperationException($"'{node.Name}' is not rectangle-shaped.");
                    Write(x1, x1.Integer + dx);
                    Write(y1, y1.Integer + dy);
                    Write(x2, x2.Integer + dx);
                    Write(y2, y2.Integer + dy);
                    result.Moved.Add(new MapMovedDto
                    {
                        Addr = item.Addr, X1 = x1.Integer, Y1 = y1.Integer, X2 = x2.Integer, Y2 = y2.Integer,
                    });
                    movedCount++;
                    singleLabel = $"move {doc.Doc.Nodes[item.Addr[0]].Name} {node.Name}";
                    break;
                }

                default:
                {
                    WzNode? x = node.Child("x");
                    WzNode? y = node.Child("y");
                    if (x == null || y == null)
                    {
                        throw new InvalidOperationException(
                            $"'{node.Name}' has no x/y children to move. Only entries that store "
                            + "their position as x and y can be dragged.");
                    }
                    Write(x, x.Integer + dx);
                    Write(y, y.Integer + dy);
                    result.Moved.Add(new MapMovedDto { Addr = item.Addr, X1 = x.Integer, Y1 = y.Integer });
                    movedCount++;
                    singleLabel = $"move {item.Kind} {node.Name}";
                    break;
                }
            }
        }

        // Every foothold whose geometry changed, reported whole — the client
        // applies the server's truth, junction propagation included.
        foreach ((WzNode fh, int rootIndex) in footholds
                     .Concat(movedFootholds.Select(f => (f, footholds.FirstOrDefault().RootIndex)))
                     .DistinctBy(t => t.Item1))
        {
            int[]? addr = AddrOfFoothold(doc, rootIndex, fh);
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

        if (changes.Count == 0)
        {
            result.Notes = notes;
            return result;    // everything skipped: nothing to undo, say why
        }

        string label = movedCount == 1 && singleLabel != null
            ? singleLabel
            : $"move {movedCount} items";
        doc.Push(Change.Group(label, changes.ToArray()));
        result.Notes = notes;
        return result;
    }

    /// <summary>The connected-component endpoint walk MoveFoothold does, with
    /// the matches poured into a shared sink instead of moved immediately —
    /// so a batch move can dedupe junctions across many selected segments.</summary>
    private static void CollectLinkedEndpoints(
        WzNode layer, WzNode moving, int vertex, HashSet<(WzNode Node, int Vertex)> sink)
    {
        (string xa, string ya) = vertex == 1 ? ("x1", "y1") : ("x2", "y2");
        long? px = moving.IntegerAt(xa);
        long? py = moving.IntegerAt(ya);
        if (px == null || py == null)
            return;

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

        foreach ((WzNode fh, int v) in atPoint)
        {
            if (component.Contains(fh))
                sink.Add((fh, v));
        }
    }

    #endregion

    #region deleteMany

    /// <summary>
    /// Deletes a whole selection as ONE undo entry. Foothold deletes unlink
    /// honestly: every surviving foothold in the same layer whose prev or next
    /// named a deleted id has that reference set to 0 — an explicit edit, not a
    /// silent repair; forks and asymmetry that do not involve the deleted
    /// segments are untouched. Deleted subtrees have their payloads pinned so
    /// undo after a save still restores real bytes.
    /// </summary>
    private MapEditResultDto DeleteMany(EditorDoc doc, MapEditOp op)
    {
        if (op.Items == null || op.Items.Count == 0)
            throw new ArgumentException("deleteMany needs items.");

        // Resolve everything BEFORE any mutation — value writes don't shift
        // addresses, and removals happen deepest-sibling-first afterwards.
        List<(int[] Addr, string Kind, WzNode Node, WzNode Parent)> targets = new();
        foreach (MapEditTargetDto item in op.Items)
        {
            if (item.Addr.Length < 2)
            {
                throw new InvalidOperationException(
                    "A top-level node is never deleted from the map editor — even an empty "
                    + "container is part of the map (reactor ships empty on 9,560 maps).");
            }
            WzNode parent = doc.NodeAt(item.Addr[..^1]);
            int index = item.Addr[^1];
            if (index < 0 || index >= parent.Children.Count)
                throw new ArgumentException("An address names a child that is not there. Re-fetch the document.");
            targets.Add((item.Addr, item.Kind, parent.Children[index], parent));
        }

        HashSet<WzNode> doomed = new(targets.Select(t => t.Node));
        List<Change> changes = new();
        List<string> notes = new();

        // Honest unlinking, before anything is removed.
        int unlinked = 0;
        foreach ((int[] addr, string kind, WzNode node, WzNode _) in targets)
        {
            if (kind != "foothold" || addr.Length != 4)
                continue;
            WzNode root = doc.Doc.Nodes[addr[0]];
            if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
                continue;
            if (!long.TryParse(node.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
                continue;
            WzNode layer = root.Children[addr[1]];
            foreach (WzNode group in layer.Children)
            {
                foreach (WzNode fh in group.Children)
                {
                    if (doomed.Contains(fh))
                        continue;
                    if (fh.Child("prev") is WzNode prev && prev.Integer == id && id != 0)
                    {
                        Change change = ValueChange(prev, "");
                        prev.SetNumber(0);
                        change.CaptureNew();
                        changes.Add(change);
                        unlinked++;
                    }
                    if (fh.Child("next") is WzNode next && next.Integer == id && id != 0)
                    {
                        Change change = ValueChange(next, "");
                        next.SetNumber(0);
                        change.CaptureNew();
                        changes.Add(change);
                        unlinked++;
                    }
                }
            }
        }
        if (unlinked > 0)
            notes.Add($"{unlinked} surviving prev/next reference{(unlinked == 1 ? "" : "s")} unlinked (set to 0).");

        // Pin payloads while the reader is still the one they point at.
        lock (_session.Gate)
        {
            foreach ((int[] _, string _, WzNode node, WzNode _) in targets)
                _ = PinPayloads(node, node.Name) ?? DetachPayloads(node, node.Name);
        }

        // Remove deepest-last-sibling first so earlier captured indexes stay true.
        foreach ((int[] addr, string _, WzNode node, WzNode parent) in targets
                     .OrderByDescending(t => t.Addr, AddrComparer.Instance))
        {
            int index = parent.Children.ToList().IndexOf(node);
            if (index < 0)
                continue;   // already gone — the same node addressed twice
            parent.Remove(node);
            WzNode capturedParent = parent;
            WzNode capturedNode = node;
            int capturedIndex = index;
            changes.Add(new Change($"delete {node.Name}",
                apply: () => capturedParent.Remove(capturedNode),
                revert: () => capturedParent.Insert(
                    Math.Min(capturedIndex, capturedParent.Children.Count), capturedNode)));
        }

        string label = targets.Count == 1
            ? $"delete {targets[0].Kind} {targets[0].Node.Name}"
            : $"delete {targets.Count} items";
        doc.Push(Change.Group(label, changes.ToArray()));
        return new MapEditResultDto { Structural = true, Notes = notes };
    }

    private sealed class AddrComparer : IComparer<int[]>
    {
        public static readonly AddrComparer Instance = new();
        public int Compare(int[]? a, int[]? b)
        {
            if (a == null || b == null)
                return (a?.Length ?? 0).CompareTo(b?.Length ?? 0);
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0)
                    return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    #endregion

    #region duplicate

    /// <summary>
    /// Duplicates a selection at an offset, as ONE undo entry: deep clones with
    /// fresh numeric names, positions shifted, life re-anchored to the foothold
    /// under its new point (skipped with a note when nothing is below), portal
    /// pn kept unique the same way placement enforces it. Footholds are not
    /// duplicated — a copied segment with copied prev/next would point into the
    /// original chain, which is exactly the phantom link this editor refuses to
    /// write.
    /// </summary>
    private MapEditResultDto Duplicate(EditorDoc doc, MapEditOp op)
    {
        if (op.Items == null || op.Items.Count == 0)
            throw new ArgumentException("duplicate needs items.");
        long dx = op.Dx ?? 24;
        long dy = op.Dy ?? 0;

        List<Change> changes = new();
        List<string> notes = new();
        List<WzNode> clones = new();
        MapEditResultDto result = new() { Structural = true };

        foreach (MapEditTargetDto item in op.Items)
        {
            if (item.Kind == "foothold")
            {
                notes.Add("Footholds are not duplicated — a copied segment would carry prev/next "
                    + "pointing into the original chain. Draw or extend a chain instead.");
                continue;
            }
            WzNode node = doc.NodeAt(item.Addr);
            WzNode parent = doc.NodeAt(item.Addr[..^1]);
            string? payload = FindPayload(node, node.Name);
            if (payload != null)
            {
                notes.Add($"'{node.Name}' was not duplicated — it carries {payload}, which this "
                    + "op does not copy.");
                continue;
            }

            WzNode clone = CloneNode(node);
            clone.Name = long.TryParse(node.Name, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long _)
                ? NextNumericName(parent)
                : UniqueName(parent, node.Name);

            switch (item.Kind)
            {
                case "life":
                {
                    long oldX = clone.IntegerAt("x") ?? 0;
                    long oldCy = clone.IntegerAt("cy") ?? clone.IntegerAt("y") ?? 0;
                    long nx = oldX + dx;
                    long fhId, cy;
                    try
                    {
                        (fhId, cy) = AnchorToFoothold(doc, nx, oldCy + dy, null);
                    }
                    catch (InvalidOperationException)
                    {
                        notes.Add($"Spawn {clone.TextAt("id") ?? node.Name} was not duplicated — "
                            + "no foothold lies below the offset point.");
                        continue;
                    }
                    clone.Child("x")?.SetNumber(nx);
                    clone.Child("y")?.SetNumber(cy);
                    clone.Child("cy")?.SetNumber(cy);
                    clone.Child("fh")?.SetNumber(fhId);
                    if (clone.Child("rx0") is WzNode rx0) rx0.SetNumber(rx0.Integer + dx);
                    if (clone.Child("rx1") is WzNode rx1) rx1.SetNumber(rx1.Integer + dx);
                    break;
                }
                case "ladder":
                {
                    if (clone.Child("x") is WzNode lx) lx.SetNumber(lx.Integer + dx);
                    if (clone.Child("y1") is WzNode ly1) ly1.SetNumber(ly1.Integer + dy);
                    if (clone.Child("y2") is WzNode ly2) ly2.SetNumber(ly2.Integer + dy);
                    break;
                }
                case "rect":
                {
                    if (clone.Child("x1") is WzNode rx1c) rx1c.SetNumber(rx1c.Integer + dx);
                    if (clone.Child("x2") is WzNode rx2c) rx2c.SetNumber(rx2c.Integer + dx);
                    if (clone.Child("y1") is WzNode ry1c) ry1c.SetNumber(ry1c.Integer + dy);
                    if (clone.Child("y2") is WzNode ry2c) ry2c.SetNumber(ry2c.Integer + dy);
                    break;
                }
                default:
                {
                    if (clone.Child("x") is WzNode cx) cx.SetNumber(cx.Integer + dx);
                    if (clone.Child("y") is WzNode cyn) cyn.SetNumber(cyn.Integer + dy);
                    break;
                }
            }

            if (item.Kind == "portal" && clone.Child("pn") is WzNode pn
                && pn.Text is { Length: > 0 } name && !RepeatablePortalNames.Contains(name))
            {
                string unique = UniquePortalName(doc, name);
                if (!string.Equals(unique, name, StringComparison.Ordinal))
                {
                    pn.SetText(unique);
                    notes.Add($"Duplicated portal renamed pn '{name}' → '{unique}' — portal names "
                        + "address them, and two of one name leaves one unreachable.");
                }
            }

            changes.Add(InsertChild(parent, parent.Children.Count, clone));
            clones.Add(clone);
        }

        if (clones.Count == 0)
        {
            result.Structural = false;
            result.Notes = notes;
            return result;
        }

        doc.Push(Change.Group(
            clones.Count == 1 ? $"duplicate {clones[0].Name}" : $"duplicate {clones.Count} items",
            changes.ToArray()));

        foreach (WzNode clone in clones)
            result.Placed.Add(AddrOf(doc, clone));
        result.Notes = notes;
        return result;
    }

    /// <summary>A deep clone of scalar/text/vector/container nodes. Payload
    /// nodes (canvas pixels, sounds, raw data) are refused by the caller via
    /// <see cref="FindPayload"/> before this runs.</summary>
    private static WzNode CloneNode(WzNode node)
    {
        WzNode copy = node.Type switch
        {
            WzPropertyType.SubProperty or WzPropertyType.Convex
                => WzNode.Container(node.Name, node.Type),
            WzPropertyType.Vector
                => WzNode.Vector(node.Name, (int)node.Integer, node.VectorY),
            WzPropertyType.String or WzPropertyType.UOL
                => WzNode.OfText(node.Name, node.Type, node.Text ?? ""),
            WzPropertyType.Short or WzPropertyType.Int or WzPropertyType.Long
                => WzNode.Scalar(node.Name, node.Type, node.Integer),
            WzPropertyType.Float => WzNode.Scalar(node.Name, node.Type, node.Single),
            WzPropertyType.Double => WzNode.Scalar(node.Name, node.Type, node.Double),
            _ => throw new InvalidOperationException(
                $"'{node.Name}' is a {node.Type}, which duplication does not copy."),
        };
        foreach (WzNode child in node.Children)
            copy.Add(CloneNode(child));
        return copy;
    }

    /// <summary>Names the first payload-bearing node under this one, or null.</summary>
    private static string? FindPayload(WzNode node, string where)
    {
        if (node.Payload != null)
            return $"a {node.Type} payload at '{where}'";
        foreach (WzNode child in node.Children)
        {
            string? found = FindPayload(child, $"{where}/{child.Name}");
            if (found != null)
                return found;
        }
        return null;
    }

    private static string UniqueName(WzNode parent, string baseName)
    {
        for (int i = 2; ; i++)
        {
            string candidate = i == 2 ? $"{baseName}_copy" : $"{baseName}_copy{i}";
            if (parent.Child(candidate) == null)
                return candidate;
        }
    }

    private static string UniquePortalName(EditorDoc doc, string baseName)
    {
        WzNode? portals = doc.Doc.Find("portal");
        if (portals == null)
            return baseName;
        bool Taken(string name) => portals.Children.Any(
            p => string.Equals(p.TextAt("pn"), name, StringComparison.Ordinal));
        if (!Taken(baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName}{i}";
            if (!Taken(candidate))
                return candidate;
        }
    }

    #endregion

    #region Foothold vertex surgery

    /// <summary>The next foothold id unused anywhere in the map — the same
    /// unique-on-write rule chain drawing follows.</summary>
    private static long NextFootholdId(WzNode root)
    {
        long next = 1;
        foreach (WzNode layer in root.Children)
            foreach (WzNode group in layer.Children)
                foreach (WzNode fh in group.Children)
                {
                    if (long.TryParse(fh.Name, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out long id) && id >= next)
                        next = id + 1;
                }
        return next;
    }

    /// <summary>
    /// Splits a foothold segment at a point: the original keeps its first half
    /// (x1,y1)→(v), a new segment takes (v)→(x2,y2), prev/next threaded through
    /// it — including the old next's back-reference, rewritten ONLY when it
    /// actually pointed at the split segment (a fork keeps its fork).
    /// </summary>
    private MapEditResultDto InsertFootholdVertex(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long vx || op.Y is not long vy)
            throw new ArgumentException("insertFootholdVertex needs the vertex's x and y.");
        if (op.Addr.Length != 4)
            throw new ArgumentException("A foothold address is foothold/layer/group/id — four segments.");
        WzNode root = doc.Doc.Nodes[op.Addr[0]];
        if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
            throw new ArgumentException("insertFootholdVertex only splits footholds.");

        WzNode fh = doc.NodeAt(op.Addr);
        WzNode layer = root.Children[op.Addr[1]];
        WzNode group = layer.Children[op.Addr[2]];
        if (!long.TryParse(fh.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
            throw new InvalidOperationException($"Foothold '{fh.Name}' has a non-numeric id.");

        WzNode? x2 = fh.Child("x2");
        WzNode? y2 = fh.Child("y2");
        WzNode? next = fh.Child("next");
        if (x2 == null || y2 == null || fh.Child("x1") == null || fh.Child("y1") == null || next == null
            || fh.Child("prev") == null)
        {
            throw new InvalidOperationException(
                $"Foothold '{fh.Name}' does not carry the full x1/y1/x2/y2/prev/next shape, so a "
                + "split would have to invent structure. Edit it raw instead.");
        }

        long oldX2 = x2.Integer;
        long oldY2 = y2.Integer;
        long oldNext = next.Integer;
        long newId = NextFootholdId(root);

        List<Change> changes = new();
        void Write(WzNode node, long value)
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }

        Write(x2, vx);
        Write(y2, vy);
        Write(next, newId);

        WzNode created = WzNode.Container(newId.ToString(CultureInfo.InvariantCulture));
        created.Add(WzNode.Scalar("x1", WzPropertyType.Int, vx));
        created.Add(WzNode.Scalar("y1", WzPropertyType.Int, vy));
        created.Add(WzNode.Scalar("x2", WzPropertyType.Int, oldX2));
        created.Add(WzNode.Scalar("y2", WzPropertyType.Int, oldY2));
        created.Add(WzNode.Scalar("prev", WzPropertyType.Int, id));
        created.Add(WzNode.Scalar("next", WzPropertyType.Int, oldNext));

        // The old next's back-reference: rewritten only where it truly pointed
        // here. A neighbour whose prev names someone else is a fork, and forks
        // are legal — never "repaired".
        if (oldNext != 0)
        {
            foreach (WzNode g in layer.Children)
            {
                foreach (WzNode candidate in g.Children)
                {
                    if (ReferenceEquals(candidate, fh))
                        continue;
                    if (!long.TryParse(candidate.Name, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out long cid) || cid != oldNext)
                        continue;
                    if (candidate.Child("prev") is WzNode nprev && nprev.Integer == id)
                        Write(nprev, newId);
                }
            }
        }

        changes.Add(InsertChild(group, op.Addr[3] + 1, created));
        doc.Push(Change.Group($"insert foothold vertex at ({vx}, {vy})", changes.ToArray()));

        MapEditResultDto result = new() { Structural = true };
        result.Placed.Add(AddrOf(doc, created));
        return result;
    }

    /// <summary>
    /// Continues a chain from a free end: a new linked segment from the chosen
    /// endpoint to (x, y). Refused when that end already continues — extending
    /// mid-chain is a fork, and forks are authored deliberately or not at all.
    /// </summary>
    private MapEditResultDto ExtendFoothold(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("extendFoothold needs the new endpoint's x and y.");
        if (op.Vertex is not (1 or 2))
            throw new ArgumentException("extendFoothold needs vertex 1 (the x1/y1 end) or 2 (the x2/y2 end).");
        if (op.Addr.Length != 4)
            throw new ArgumentException("A foothold address is foothold/layer/group/id — four segments.");
        WzNode root = doc.Doc.Nodes[op.Addr[0]];
        if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
            throw new ArgumentException("extendFoothold only extends footholds.");

        WzNode fh = doc.NodeAt(op.Addr);
        WzNode layer = root.Children[op.Addr[1]];
        WzNode group = layer.Children[op.Addr[2]];
        if (!long.TryParse(fh.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
            throw new InvalidOperationException($"Foothold '{fh.Name}' has a non-numeric id.");

        WzNode? link = op.Vertex == 2 ? fh.Child("next") : fh.Child("prev");
        if (link == null)
            throw new InvalidOperationException($"Foothold '{fh.Name}' carries no {(op.Vertex == 2 ? "next" : "prev")}.");
        if (link.Integer != 0)
        {
            throw new InvalidOperationException(
                $"This end of foothold {fh.Name} already continues (its "
                + $"{(op.Vertex == 2 ? "next" : "prev")} is {link.Integer}). Chains extend from "
                + "free ends; a second branch here would be a fork, which is authored deliberately "
                + "or not at all.");
        }

        long ax = fh.IntegerAt(op.Vertex == 2 ? "x2" : "x1") ?? 0;
        long ay = fh.IntegerAt(op.Vertex == 2 ? "y2" : "y1") ?? 0;
        if (ax == nx && ay == ny)
            throw new ArgumentException("The new segment has zero length.");
        long newId = NextFootholdId(root);

        List<Change> changes = new();
        Change linkChange = ValueChange(link, "");
        link.SetNumber(newId);
        linkChange.CaptureNew();
        changes.Add(linkChange);

        WzNode created = WzNode.Container(newId.ToString(CultureInfo.InvariantCulture));
        if (op.Vertex == 2)
        {
            created.Add(WzNode.Scalar("x1", WzPropertyType.Int, ax));
            created.Add(WzNode.Scalar("y1", WzPropertyType.Int, ay));
            created.Add(WzNode.Scalar("x2", WzPropertyType.Int, nx));
            created.Add(WzNode.Scalar("y2", WzPropertyType.Int, ny));
            created.Add(WzNode.Scalar("prev", WzPropertyType.Int, id));
            created.Add(WzNode.Scalar("next", WzPropertyType.Int, 0));
            changes.Add(InsertChild(group, op.Addr[3] + 1, created));
        }
        else
        {
            created.Add(WzNode.Scalar("x1", WzPropertyType.Int, nx));
            created.Add(WzNode.Scalar("y1", WzPropertyType.Int, ny));
            created.Add(WzNode.Scalar("x2", WzPropertyType.Int, ax));
            created.Add(WzNode.Scalar("y2", WzPropertyType.Int, ay));
            created.Add(WzNode.Scalar("prev", WzPropertyType.Int, 0));
            created.Add(WzNode.Scalar("next", WzPropertyType.Int, id));
            changes.Add(InsertChild(group, op.Addr[3], created));
        }

        doc.Push(Change.Group($"extend foothold chain to ({nx}, {ny})", changes.ToArray()));

        MapEditResultDto result = new() { Structural = true };
        result.Placed.Add(AddrOf(doc, created));
        return result;
    }

    #endregion

    #region Ladders and rect zones

    /// <summary>Moves or re-heights a ladder/rope: x, y1, y2 written whole,
    /// ends kept top-first the way placement writes them.</summary>
    private static MapEditResultDto MoveLadder(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y1 is not long ry1 || op.Y2 is not long ry2)
            throw new ArgumentException("moveLadder needs x, y1 and y2.");
        long y1 = Math.Min(ry1, ry2);
        long y2 = Math.Max(ry1, ry2);
        if (y1 == y2)
            throw new ArgumentException("A ladder needs two distinct heights — it has zero length.");

        WzNode ladder = doc.NodeAt(op.Addr);
        WzNode? x = ladder.Child("x");
        WzNode? ny1 = ladder.Child("y1");
        WzNode? ny2 = ladder.Child("y2");
        if (x == null || ny1 == null || ny2 == null)
            throw new InvalidOperationException($"'{ladder.Name}' has no x/y1/y2 — not a ladderRope shape.");

        List<Change> changes = new();
        foreach ((WzNode node, long value) in new[] { (x, nx), (ny1, y1), (ny2, y2) })
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }
        doc.Push(Change.Group("move ladder / rope", changes.ToArray()));

        return new MapEditResultDto
        {
            Structural = false,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = nx, Y1 = y1, X2 = nx, Y2 = y2 } },
        };
    }

    /// <summary>Writes a rect zone's four corners — the resize handles' op.</summary>
    private static MapEditResultDto SetRect(EditorDoc doc, MapEditOp op)
    {
        if (op.X1 is not long x1 || op.Y1 is not long y1 || op.X2 is not long x2 || op.Y2 is not long y2)
            throw new ArgumentException("setRect needs x1, y1, x2 and y2.");
        if (op.Addr.Length < 2)
            throw new ArgumentException("setRect addresses a zone entry, not a kind.");

        WzNode entry = doc.NodeAt(op.Addr);
        WzNode? nx1 = entry.Child("x1");
        WzNode? nY1 = entry.Child("y1");
        WzNode? nx2 = entry.Child("x2");
        WzNode? nY2 = entry.Child("y2");
        if (nx1 == null || nY1 == null || nx2 == null || nY2 == null)
            throw new InvalidOperationException($"'{entry.Name}' is not rectangle-shaped (x1/y1/x2/y2).");

        string kind = doc.Doc.Nodes[op.Addr[0]].Name;
        List<Change> changes = new();
        foreach ((WzNode node, long value) in new[] { (nx1, x1), (nY1, y1), (nx2, x2), (nY2, y2) })
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }
        doc.Push(Change.Group($"resize {kind} {entry.Name}", changes.ToArray()));

        return new MapEditResultDto
        {
            Structural = false,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 } },
        };
    }

    #endregion

    #region Palette hover preview

    /// <summary>
    /// The hover flyout's data: for an animated leaf, every frame's path,
    /// composed offset and measured delay (the same
    /// <see cref="AnimationService.PlaceAtSharedOrigin"/> composition the map
    /// viewer plays); for a still, its own meta. The client fetches the frames
    /// through /api/canvas and animates the flyout — the grid keeps showing the
    /// honest static thumbnail.
    /// </summary>
    public MapArtDto PalettePreview(string path)
    {
        lock (_session.Gate)
        {
            if (_session.Resolve(path) is not WzImageProperty node)
                throw new InvalidOperationException($"'{path}' does not resolve to art.");

            if (node.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
            {
                MapArtDto? animated = AnimatedMeta(node, path);
                if (animated != null)
                    return animated;
                WzImageProperty? frame = node.WzProperties?.FirstOrDefault(p => p.Name == "0")
                    ?? node.WzProperties?.FirstOrDefault();
                if (frame != null)
                    return Meta(frame, WzPath.Child(path, frame.Name));
            }
            return Meta(node, path);
        }
    }

    #endregion
}
