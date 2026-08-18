using MapleLib.WzLib;

namespace MapleBench.Services.MapModel;

/// <summary>
/// The typed layer: named accessors over nodes of the faithful layer.
///
/// <para>Every view in this file holds a <see cref="WzNode"/> and owns no data.
/// A getter reads through to the node. That is the whole design, and it is what
/// makes the model total rather than partial: a field this file does not name is
/// not lost, because it was never lifted out of the node in the first place.
/// <see cref="MapNodeView.Node"/> is public for exactly that reason — the escape
/// hatch is not a separate mechanism to remember, it is the thing every view is
/// already standing on.</para>
///
/// <para>The views read; they do not offer setters of their own. Writing goes
/// through <see cref="WzNode.SetNumber"/> and <see cref="WzNode.SetText"/>, which
/// keep the <see cref="WzPropertyType"/> that is already there — the rule the 26
/// measured mixed-type shapes make non-negotiable. Putting the write on the node
/// rather than on 40 typed properties means there is one place that rule can be
/// broken, instead of forty.</para>
///
/// <para>The views are deliberately thin. The census found 247 distinct
/// <c>info</c> keys; naming all of them here would be 247 chances to mistype a
/// key and no chance at all of being complete when the next client ships a
/// 248th.</para>
/// </summary>
public abstract class MapNodeView
{
    private protected MapNodeView(WzNode node, string key)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        Key = key;
    }

    /// <summary>
    /// The faithful node behind this view. Reading or writing here is always
    /// legitimate — it is where the data actually is.
    /// </summary>
    public WzNode Node { get; }

    /// <summary>
    /// The name this entry had in its container: an index for a numbered list,
    /// a name for a named one. Kept as text rather than as an int because
    /// WZ container keys are strings and a few of them are not numbers.
    /// </summary>
    public string Key { get; }

    private protected long? Int(string name) => Node.IntegerAt(name);

    private protected string Str(string name) => Node.TextAt(name);

    /// <inheritdoc/>
    public override string ToString() => $"{GetType().Name} '{Key}'";
}

/// <summary>
/// <c>info</c> — the map's own header. Present on essentially every map, but
/// <b>not always first</b>: 212 images start with something else, and node order
/// is preserved rather than assumed anywhere in this model.
/// </summary>
public sealed class MapInfo : MapNodeView
{
    internal MapInfo(WzNode node) : base(node, "info") { }

    /// <summary>
    /// <c>info/link</c>. 6,850 maps carry one and every target resolves. Read as
    /// text on purpose: it is a String on 6,695 maps and an <b>Int on 155</b>, so
    /// a reader that only accepts the String silently reports 155 link stubs as
    /// having no link.
    ///
    /// A link stub is not an empty map. 87 of them carry <c>portal</c>, 44
    /// <c>monsterDefense</c>, 18 <c>reactorRemove</c>, 18
    /// <c>objectVisibleLevel</c>, 9 each of <c>swimArea_Moment</c> / <c>area</c> /
    /// <c>nodeInfo</c> and 5 <c>life</c>.
    /// </summary>
    public string Link => Str("link");

    /// <summary>
    /// <c>info/bgm</c>. Two spellings ship: <c>Bgm09/DarkShadow</c> on 17,385
    /// maps and <c>Bgm00.img/Silence</c> on 49, plus 4 with no slash at all.
    /// Resolution is case-insensitive in practice, and none of the 811 tracks
    /// lives in Sound.wz or Sound001.wz — they are all in Sound002.wz and
    /// Sound2.wz.
    /// </summary>
    public string Bgm => Str("bgm");

    /// <summary>
    /// <c>info/mapMark</c>. A bare mark name with the archive and folder implied,
    /// not a path. <c>None</c> appears on 813 maps and is a sentinel, not a mark.
    /// </summary>
    public string MapMark => Str("mapMark");

    /// <summary>
    /// <c>info/fieldType</c> — Int on 6,176 maps and <b>String on 89</b>.
    /// </summary>
    public long? FieldType => Int("fieldType");

    /// <summary>
    /// <c>info/returnMap</c>. <c>999999999</c> and <c>-1</c> both mean "none";
    /// neither is a map id to be resolved or repaired.
    /// </summary>
    public long? ReturnMap => Int("returnMap");

    /// <inheritdoc cref="ReturnMap"/>
    public long? ForcedReturn => Int("forcedReturn");

    /// <summary>
    /// <c>VRLeft</c>/<c>VRTop</c>/<c>VRRight</c>/<c>VRBottom</c> — the camera and
    /// walk bound, on 16,291 maps. The remaining 1,151 need a computed fallback.
    /// <b>Not the same rectangle as <see cref="MinimapBound"/> or
    /// <see cref="LadderBound"/></b>; conflating the three is a measured hazard,
    /// not a hypothetical one.
    /// </summary>
    public MapRect? ViewBound => MapRect.Read(Node, "VRLeft", "VRTop", "VRRight", "VRBottom");

    /// <summary>
    /// <c>MRLeft</c>/<c>MRTop</c>/<c>MRRight</c>/<c>MRBottom</c> — the minimap
    /// render bound, on roughly 2,650 maps.
    /// </summary>
    public MapRect? MinimapBound => MapRect.Read(Node, "MRLeft", "MRTop", "MRRight", "MRBottom");

    /// <summary>
    /// <c>LBSide</c>/<c>LBTop</c>/<c>LBBottom</c>-family bound, on 1,332 maps.
    /// </summary>
    public MapRect? LadderBound => MapRect.Read(Node, "LBLeft", "LBTop", "LBRight", "LBBottom");

    /// <summary>
    /// <c>info/mapDesc</c>. Present on 859 maps and <b>the empty string on every
    /// one of them</b>. The real description is in
    /// <c>String.wz/Map.img/&lt;region&gt;/&lt;id&gt;/mapDesc</c>, and this field
    /// must never be shown as if it were the name of anything.
    /// </summary>
    public string MapDesc => Str("mapDesc");

    /// <summary>
    /// Every <c>info</c> key present, in file order. This is the accessor to
    /// reach for rather than adding a property here: the census counted 247
    /// distinct keys and 11 maps carry a purely numeric one
    /// (<c>info/39430 = "Bgm56/DispossessedAnger"</c>), which no named property
    /// could have anticipated.
    /// </summary>
    public IReadOnlyList<WzNode> Keys => Node.Children;
}

/// <summary>An integer rectangle read from four separately named fields.</summary>
public readonly record struct MapRect(int Left, int Top, int Right, int Bottom)
{
    internal static MapRect? Read(WzNode node, string left, string top, string right, string bottom)
    {
        long? l = node.IntegerAt(left);
        long? t = node.IntegerAt(top);
        long? r = node.IntegerAt(right);
        long? b = node.IntegerAt(bottom);
        return l.HasValue && t.HasValue && r.HasValue && b.HasValue
            ? new MapRect((int)l.Value, (int)t.Value, (int)r.Value, (int)b.Value)
            : null;
    }
}

/// <summary>
/// One of layers <c>0</c>-<c>7</c>. Every geometry map has all eight.
/// </summary>
public sealed class MapLayer : MapNodeView
{
    internal MapLayer(WzNode node, int index) : base(node, index.ToString())
    {
        Index = index;

        WzNode tiles = node.Child("tile");
        Tiles = tiles == null ? [] : [.. tiles.Children.Select(c => new MapTile(c))];

        WzNode objects = node.Child("obj");
        Objects = objects == null ? [] : [.. objects.Children.Select(c => new MapObject(c))];

        // Anomaly 3: Map9/954090400.img has a `back` list INSIDE a layer, 26
        // entries deep. It is not a shape any documentation predicts and it is
        // real, so it gets an accessor rather than an apology.
        WzNode backs = node.Child("back");
        LayerBackgrounds = backs == null ? [] : [.. backs.Children.Select(c => new MapBackground(c))];
    }

    /// <summary>0-7.</summary>
    public int Index { get; }

    /// <summary>
    /// <c>&lt;L&gt;/info/tS</c> — the tile set for <b>this whole layer</b>, a bare
    /// set name with the archive and folder implied.
    ///
    /// This is the single hardest constraint in the editor and it is measured:
    /// of 698 tiled layers examined, layers holding tiles but no <c>tS</c> number
    /// <b>zero</b>. A tile does not carry its own set. So changing this re-skins
    /// every tile on the layer, and dropping in a tile from a different set means
    /// either moving it to another layer or rewriting this field — there is no
    /// third option, and a UI that offers one is lying.
    /// </summary>
    public string TileSet => Node.Descend("info/tS")?.AsText();

    /// <summary>
    /// <c>&lt;L&gt;/info/tSMag</c> — 1 or 2. <b>Absent is not 1.</b> The
    /// distinction is preserved because the node's presence is preserved.
    /// </summary>
    public long? TileSetMagnification => Node.Descend("info/tSMag")?.AsInteger();

    /// <summary>The tiles on this layer, in file order.</summary>
    public IReadOnlyList<MapTile> Tiles { get; }

    /// <summary>
    /// The objects on this layer, in file order. 4,605 geometry maps have zero
    /// tiles and are built entirely from these and from <c>back</c>, so a
    /// renderer must not require a tile set to draw a map.
    /// </summary>
    public IReadOnlyList<MapObject> Objects { get; }

    /// <summary>
    /// Backgrounds listed inside this layer rather than at the image root. One
    /// map does this. See the constructor's remarks.
    /// </summary>
    public IReadOnlyList<MapBackground> LayerBackgrounds { get; }
}

/// <summary>
/// <c>&lt;L&gt;/tile/&lt;i&gt;</c>. 1,208,088 of them across the client, up to
/// 13,279 in a single map.
/// </summary>
public sealed class MapTile : MapNodeView
{
    internal MapTile(WzNode node) : base(node, node.Name) { }

    /// <summary>X, in map coordinates. The client's range is [-20187, 31465].</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates. The client's range is [-26000, 8187].</summary>
    public long? Y => Int("y");

    /// <summary>
    /// The tile <i>variant</i>, and it takes exactly eleven values across the
    /// whole client: <c>bsc enH0 enH1 edD edU enV1 enV0 slLU slRU slRD slLD</c>.
    /// Always a String on the wire — never write it as an Int.
    /// </summary>
    public string Variant => Str("u");

    /// <summary>The index within the variant.</summary>
    public long? No => Int("no");

    /// <summary>Z-order modifier.</summary>
    public long? ZMod => Int("zM");
}

/// <summary>
/// <c>&lt;L&gt;/obj/&lt;i&gt;</c>. 514,109 across the client, up to 1,279 in one map.
/// </summary>
public sealed class MapObject : MapNodeView
{
    internal MapObject(WzNode node) : base(node, node.Name) { }

    /// <summary>
    /// <c>oS</c> — the object set, a bare name. Always a String. The rename
    /// engine has to rewrite this field <i>and</i> the archive-rooted UOL strings
    /// in <c>shipObj</c>/<c>pulley</c>/<c>healer</c>/<c>snowBall</c>, which name
    /// the same art in the other dialect.
    /// </summary>
    public string ObjectSet => Str("oS");

    /// <summary>First path segment under the set. Always a String.</summary>
    public string L0 => Str("l0");

    /// <summary>Second path segment. Always a String.</summary>
    public string L1 => Str("l1");

    /// <summary>Third path segment. Always a String.</summary>
    public string L2 => Str("l2");

    /// <summary>
    /// A <b>fourth</b> path segment. Two maps in the whole client have one. It is
    /// anomaly 2 of ten, and it is here because an object reader that stops at
    /// <c>l2</c> resolves those two objects to the wrong art rather than to none.
    /// </summary>
    public string L3 => Str("l3");

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates.</summary>
    public long? Y => Int("y");

    /// <summary>Z-order.</summary>
    public long? Z => Int("z");

    /// <summary>Z-order modifier.</summary>
    public long? ZMod => Int("zM");

    /// <summary>Horizontal flip.</summary>
    public long? Flip => Int("f");
}

/// <summary>
/// <c>back/&lt;i&gt;</c>. 184,602 across the client, up to 133 in one map. A
/// nested <c>back/&lt;i&gt;/&lt;j&gt;</c> exists on <c>Map9/993173100.img</c>
/// (anomaly 5) and survives as children of this entry's node.
/// </summary>
public sealed class MapBackground : MapNodeView
{
    internal MapBackground(WzNode node) : base(node, node.Name) { }

    /// <summary>The background set, a bare name. Always a String.</summary>
    public string BackSet => Str("bS");

    /// <summary>The index within the set.</summary>
    public long? No => Int("no");

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates.</summary>
    public long? Y => Int("y");

    /// <summary>
    /// Type 0-7, confirmed by field correlation rather than by documentation:
    /// <c>rx != 0</c> on 100% of types 4 and 6, <c>ry != 0</c> on 100% of 5 and 7.
    /// </summary>
    public long? Type => Int("type");

    /// <summary>Whether this is an animated background.</summary>
    public long? Ani => Int("ani");

    /// <summary>Horizontal parallax rate.</summary>
    public long? Rx => Int("rx");

    /// <summary>Vertical parallax rate.</summary>
    public long? Ry => Int("ry");

    /// <summary>Drawn in front of the map rather than behind it.</summary>
    public long? Front => Int("front");
}

/// <summary>
/// <c>foothold/&lt;layer&gt;/&lt;group&gt;/&lt;id&gt;</c>. 784,735 across the
/// client, up to 5,820 in one map.
/// </summary>
/// <remarks>
/// <b>Footholds are a directed graph, not a doubly-linked list, and the
/// difference is measured.</b> On 6,744 of them (0.86%) the node named by
/// <c>prev</c> does not name this one back as its <c>next</c> — those are forks,
/// they are legal, and an editor that "repairs" the asymmetry corrupts 6,744 real
/// links. 54,016 chains cross a group boundary, so groups are cosmetic. Only 5
/// cross a layer, so layers are effectively closed: warn, do not fix. One
/// <c>prev</c> and one <c>next</c> in the whole client dangle.
///
/// The invariant that <i>is</i> worth enforcing when a vertex is dragged is the
/// one the data actually keeps: <c>prev</c>'s <c>(x2,y2)</c> equals this one's
/// <c>(x1,y1)</c> on all but 38 footholds out of 784,735.
///
/// Ids are not unique on load — 69 maps carry a duplicate — so nothing here keys
/// a dictionary by id.
/// </remarks>
public sealed class MapFoothold : MapNodeView
{
    internal MapFoothold(WzNode node, string layer, string group) : base(node, node.Name)
    {
        Layer = layer;
        Group = group;
    }

    /// <summary>The foothold layer key. Chains cross these only 5 times in the whole client.</summary>
    public string Layer { get; }

    /// <summary>The group key. Cosmetic — 54,016 chains cross a group.</summary>
    public string Group { get; }

    /// <summary>The foothold id, as it appears in the path.</summary>
    public string Id => Key;

    /// <summary>Start X.</summary>
    public long? X1 => Int("x1");

    /// <summary>Start Y.</summary>
    public long? Y1 => Int("y1");

    /// <summary>End X.</summary>
    public long? X2 => Int("x2");

    /// <summary>End Y.</summary>
    public long? Y2 => Int("y2");

    /// <summary>The id of the previous foothold, or 0 for none.</summary>
    public long? Prev => Int("prev");

    /// <summary>The id of the next foothold, or 0 for none.</summary>
    public long? Next => Int("next");

    /// <summary>Conveyor force along the foothold.</summary>
    public long? Force => Int("force");

    /// <summary>Whether a character may pass through from below.</summary>
    public long? CantThrough => Int("cantThrough");

    /// <summary>Whether a character may drop through.</summary>
    public long? ForbidFallDown => Int("forbidFallDown");
}

/// <summary>
/// A <c>life</c> entry: a mob or NPC spawn. 90,211 across the client, up to 101
/// in one map.
/// </summary>
/// <remarks>
/// <b>The list is not always one level deep.</b> When <c>life/isCategory</c> is
/// set the shape becomes <c>life/&lt;categoryId&gt;/&lt;index&gt;</c> — 25 maps,
/// 2,516 spawns. That is anomaly 1 of ten, and it is the one with teeth: a loader
/// that iterates <c>life/&lt;int&gt;</c> and reads <c>type</c> finds nothing on
/// those 25 maps, concludes the map has no spawns, and writes back an empty one.
/// <see cref="MapDocument"/> flattens both shapes into one list and records
/// <see cref="CategoryId"/> so the shape can be written back the way it was read.
///
/// A life record can also carry a numeric child — <c>life/&lt;i&gt;/0 = -1</c> on
/// 40 maps, 2,954 occurrences (anomaly 9). It is not a field this view names, and
/// it survives regardless because it is still a child of <see cref="MapNodeView.Node"/>.
/// </remarks>
public sealed class MapLife : MapNodeView
{
    internal MapLife(WzNode node, string key, string categoryId) : base(node, key)
    {
        CategoryId = categoryId;
    }

    /// <summary>
    /// The category this spawn was listed under, or null for the ordinary
    /// one-level shape. Non-null on 25 maps.
    /// </summary>
    public string CategoryId { get; }

    /// <summary>The mob or NPC id. Always a String on the wire.</summary>
    public string Id => Str("id");

    /// <summary><c>m</c> for a mob, <c>n</c> for an NPC. Always a String.</summary>
    public string Type => Str("type");

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates.</summary>
    public long? Y => Int("y");

    /// <summary>The y the spawn stands at, which is the foothold's y, not <see cref="Y"/>.</summary>
    public long? Cy => Int("cy");

    /// <summary>The foothold id this spawn stands on.</summary>
    public long? Foothold => Int("fh");

    /// <summary>Left bound of the walk range.</summary>
    public long? Rx0 => Int("rx0");

    /// <summary>Right bound of the walk range.</summary>
    public long? Rx1 => Int("rx1");

    /// <summary>
    /// Respawn delay. <b><c>-1</c> means never respawn</b> — a sentinel, not a
    /// negative duration to be clamped.
    /// </summary>
    public long? MobTime => Int("mobTime");

    /// <summary>Horizontal flip.</summary>
    public long? Flip => Int("f");

    /// <summary>Whether the spawn is hidden.</summary>
    public long? Hide => Int("hide");
}

/// <summary>
/// <c>portal/&lt;i&gt;</c>. 49,136 across the client, up to 189 in one map.
/// </summary>
public sealed class MapPortal : MapNodeView
{
    internal MapPortal(WzNode node) : base(node, node.Name) { }

    /// <summary>The portal name. Always a String.</summary>
    public string Name => Str("pn");

    /// <summary>
    /// The portal type.
    /// <b><c>pt</c> does not index the WZ icon order.</b> Portal type 6 is
    /// <c>tp</c> in all 78 measured placements, but <c>tp</c> is the sixth child
    /// of the icon folder, so reading the icon by position is off by one for
    /// every type. The table has to be read from
    /// <c>Map.wz/MapHelper.img/portal/editor</c> child order at runtime, or
    /// hard-coded from it.
    /// </summary>
    public long? PortalType => Int("pt");

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates.</summary>
    public long? Y => Int("y");

    /// <summary>
    /// The destination map id. <c>999999999</c> and <c>-1</c> both mean "none".
    /// 72 of 49,135 portal and return references in the shipping client point at
    /// 63 distinct map ids that do not exist — that is the baseline, not damage.
    /// </summary>
    public long? TargetMap => Int("tm");

    /// <summary>The destination portal's name. Always a String.</summary>
    public string TargetName => Str("tn");

    /// <summary>The server script bound to this portal. Always a String.</summary>
    public string Script => Str("script");
}

/// <summary>
/// <c>ladderRope/&lt;i&gt;</c>. 24,934 across the client, up to 104 in one map.
/// The container exists and is empty on 5,756 maps.
/// </summary>
public sealed class MapLadderRope : MapNodeView
{
    internal MapLadderRope(WzNode node) : base(node, node.Name) { }

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Top Y.</summary>
    public long? Y1 => Int("y1");

    /// <summary>Bottom Y.</summary>
    public long? Y2 => Int("y2");

    /// <summary>1 for a ladder, 0 for a rope.</summary>
    public long? IsLadder => Int("l");

    /// <summary>Whether a character may climb off the top.</summary>
    public long? UpperFoothold => Int("uf");

    /// <summary>Page/z grouping.</summary>
    public long? Page => Int("page");
}

/// <summary>
/// <c>reactor/&lt;i&gt;</c>. 8,366 across the client, up to 463 in one map, and
/// every single reactor id resolves. The container exists and is empty on 9,560
/// maps — the most common empty container in the client.
/// </summary>
public sealed class MapReactor : MapNodeView
{
    internal MapReactor(WzNode node) : base(node, node.Name) { }

    /// <summary>The reactor id. Always a String on the wire.</summary>
    public string Id => Str("id");

    /// <summary>X, in map coordinates.</summary>
    public long? X => Int("x");

    /// <summary>Y, in map coordinates.</summary>
    public long? Y => Int("y");

    /// <summary>Z-order.</summary>
    public long? Z => Int("z");

    /// <summary>Horizontal flip.</summary>
    public long? Flip => Int("f");

    /// <summary>Respawn delay.</summary>
    public long? ReactorTime => Int("reactorTime");

    /// <summary>The reactor's script name.</summary>
    public string ReactorName => Str("name");
}

/// <summary>
/// <c>miniMap</c>. 939 geometry maps have none at all.
/// </summary>
/// <remarks>
/// <c>mag</c> is <b>4 on every minimap in the client</b>, and <b>1,974 minimaps
/// are pure <c>_outlink</c>s into another map's minimap</b> — so regenerating one
/// is not a local edit, and doing it without checking who links here breaks the
/// maps that share it.
/// </remarks>
public sealed class MapMiniMap : MapNodeView
{
    internal MapMiniMap(WzNode node) : base(node, "miniMap") { }

    /// <summary>Minimap width in pixels.</summary>
    public long? Width => Int("width");

    /// <summary>Minimap height in pixels.</summary>
    public long? Height => Int("height");

    /// <summary>Map-space x of the minimap's origin.</summary>
    public long? CenterX => Int("centerX");

    /// <summary>Map-space y of the minimap's origin.</summary>
    public long? CenterY => Int("centerY");

    /// <summary>Always 4 in the shipping client.</summary>
    public long? Magnification => Int("mag");

    /// <summary>
    /// The minimap picture. Its pixels are a carried payload — this model does
    /// not decode or re-encode them.
    /// </summary>
    public WzNode Canvas => Node.Child("canvas");

    /// <summary>
    /// The archive-rooted path this minimap borrows its picture from, or null
    /// when it has its own. 1,974 minimaps are nothing but this.
    /// </summary>
    public string CanvasOutlink => Canvas?.Child("_outlink")?.AsText();
}

/// <summary>
/// A numbered entry of one of the rectangle-shaped zone kinds. See
/// <see cref="MapNodeKinds.RectKinds"/> for which those are and why they are one
/// surface rather than eleven.
/// </summary>
public sealed class MapRectZone : MapNodeView
{
    internal MapRectZone(WzNode node, string kind) : base(node, node.Name)
    {
        Kind = kind;
    }

    /// <summary>The top-level node name this entry came from.</summary>
    public string Kind { get; }

    /// <summary>The rectangle, when all four corners are present.</summary>
    public MapRect? Bounds => MapRect.Read(Node, "x1", "y1", "x2", "y2");
}

/// <summary>
/// <c>area/&lt;name&gt;</c> — a named rectangle rather than a numbered one. Nine
/// link stubs carry one, which is on its own enough to disprove "a link stub is
/// empty".
/// </summary>
public sealed class MapArea : MapNodeView
{
    internal MapArea(WzNode node) : base(node, node.Name) { }

    /// <summary>The rectangle, when all four corners are present.</summary>
    public MapRect? Bounds => MapRect.Read(Node, "x1", "y1", "x2", "y2");
}

/// <summary>
/// <c>seat/&lt;i&gt;</c> — a chair position, stored as a Vector rather than as an
/// x/y pair. One map ships an empty <c>seat</c> container.
/// </summary>
public sealed class MapSeat : MapNodeView
{
    internal MapSeat(WzNode node) : base(node, node.Name) { }

    /// <summary>The seat position, when this entry is the Vector it usually is.</summary>
    public (int X, int Y)? Position => Node.Type == WzPropertyType.Vector
        ? ((int)Node.Integer, Node.VectorY)
        : null;
}

/// <summary>
/// <c>ToolTip/&lt;i&gt;</c> — an on-map tooltip region and its text.
/// </summary>
public sealed class MapToolTip : MapNodeView
{
    internal MapToolTip(WzNode node) : base(node, node.Name) { }

    /// <summary>The region this tooltip covers, when all four corners are present.</summary>
    public MapRect? Bounds => MapRect.Read(Node, "x1", "y1", "x2", "y2");

    /// <summary>The tooltip's title.</summary>
    public string Title => Str("Title");

    /// <summary>The tooltip's body text.</summary>
    public string Desc => Str("Desc");
}
