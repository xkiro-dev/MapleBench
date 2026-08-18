using MapleLib.WzLib;

namespace MapleBench.Services.MapModel;

/// <summary>
/// A whole map image, loaded so that it can be written back unchanged.
///
/// <para><b>What this is for.</b> Everything an editor does to a map stands on
/// the ability to read one and put it back byte for byte. If that is not perfect,
/// every feature built on top of it corrupts maps quietly — the map still opens,
/// the client still runs it, and something the user never touched is gone. So
/// this class has exactly one promise, and <see cref="MapRoundTrip"/> is the
/// harness that holds it to it over all 17,442 images in the live client.</para>
///
/// <para><b>The shape.</b> A document is an ordered list of
/// <see cref="WzNode"/>s — the faithful layer, which is lossless by construction
/// — plus typed views over the ones an editor needs to name. The views own no
/// data. <see cref="Nodes"/> is the document; <see cref="Info"/>,
/// <see cref="Layers"/>, <see cref="Life"/> and the rest are ways of looking at
/// it. That inversion is what makes the escape hatch total rather than
/// top-level-only: an unmodelled key three levels down inside a life record is
/// preserved by the same mechanism that preserves an unmodelled top-level kind,
/// because neither was ever lifted out of the substrate.</para>
///
/// <para><b>Order is preserved and never assumed.</b> 212 images do not start
/// with <c>info</c>. <see cref="Nodes"/> is in file order and
/// <see cref="Build"/> writes it back in that order.</para>
///
/// <para><b>A map that cannot be round-tripped is refused, not opened.</b>
/// <see cref="Load"/> returns a <see cref="MapLoadResult"/> and the failure arm
/// is a first-class outcome. Two things reach it: an image that does not parse,
/// and a walk that stopped short because of a cycle or the depth cap. The second
/// matters more than it looks — a truncated read that returned a document anyway
/// would present a partial map as a whole one, and the first save would make the
/// truncation permanent.</para>
/// </summary>
public sealed class MapDocument
{
    private MapDocument(string imageName, List<WzNode> nodes)
    {
        ImageName = imageName;
        Nodes = nodes;

        MapId = ParseMapId(imageName);

        Info = Find("info") is { } info ? new MapInfo(info) : null;
        MiniMap = Find("miniMap") is { } mini ? new MapMiniMap(mini) : null;

        Layers = BuildLayers(nodes);
        Footholds = BuildFootholds(Find("foothold"));
        Life = BuildLife(Find("life"));
        Backgrounds = Entries(Find("back"), n => new MapBackground(n));
        Portals = Entries(Find("portal"), n => new MapPortal(n));
        LadderRopes = Entries(Find("ladderRope"), n => new MapLadderRope(n));
        Reactors = Entries(Find("reactor"), n => new MapReactor(n));
        Seats = Entries(Find("seat"), n => new MapSeat(n));
        Areas = Entries(Find("area"), n => new MapArea(n));
        ToolTips = Entries(Find("ToolTip"), n => new MapToolTip(n));
        RectZones = BuildRectZones(nodes);

        Unmodelled = [.. nodes.Where(n => !IsModelled(n.Name))];
    }

    #region Identity

    /// <summary>The image's file name, e.g. <c>100000000.img</c>.</summary>
    public string ImageName { get; }

    /// <summary>
    /// The map id, or null if the name is not a nine-digit id. A map lives in
    /// <c>Map002.wz/Map/Map&lt;first digit&gt;/&lt;id padded to 9&gt;.img</c>.
    /// </summary>
    public int? MapId { get; }

    #endregion

    #region The faithful layer

    /// <summary>
    /// Every top-level node, in file order, lossless. This is the document; the
    /// typed views below are projections of it.
    /// </summary>
    public IReadOnlyList<WzNode> Nodes { get; }

    /// <summary>
    /// The top-level nodes no typed view names. Reported so the editor can say
    /// what it is carrying rather than what it understands — the census found 40
    /// kinds present on fewer than 40 maps each, and silence about them is how
    /// they get dropped.
    /// </summary>
    public IReadOnlyList<WzNode> Unmodelled { get; }

    /// <summary>
    /// Inserts a top-level node. <b>The typed views are built at load and do not
    /// observe later structural additions</b> — callers that need live structure
    /// read <see cref="Nodes"/> / <see cref="Find"/>, which every consumer in
    /// the editor does. Order is wire order; the caller chooses the position.
    /// </summary>
    public void InsertNode(int index, WzNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        List<WzNode> nodes = (List<WzNode>)Nodes;
        nodes.Insert(Math.Min(index, nodes.Count), node);
    }

    /// <summary>Removes a top-level node by identity. See <see cref="InsertNode"/>
    /// for the staleness contract.</summary>
    public bool RemoveNode(WzNode node) => ((List<WzNode>)Nodes).Remove(node);

    /// <summary>The first top-level node with this exact name, or null.</summary>
    public WzNode Find(string name)
    {
        foreach (WzNode node in Nodes)
        {
            if (string.Equals(node.Name, name, StringComparison.Ordinal))
                return node;
        }
        return null;
    }

    #endregion

    #region The typed layer

    /// <summary><c>info</c>, or null on the handful of images without one.</summary>
    public MapInfo Info { get; }

    /// <summary>
    /// The layers this map actually has, in file order.
    ///
    /// <para><b>Not an array of eight.</b> 10,596 geometry maps have exactly
    /// layers 0-7 and <c>749080500.img</c> has a ninth named <c>8</c>, which a
    /// fixed <c>for i in 0..7</c> draws not at all and a rebuild from a fixed
    /// eight deletes. A link stub usually has none. So the layers are read from
    /// the image, and <see cref="Layer"/> is the way to ask for one by
    /// number.</para>
    /// </summary>
    public IReadOnlyList<MapLayer> Layers { get; }

    /// <summary>The layer with this number, or null if the map has no such layer.</summary>
    public MapLayer Layer(int index)
    {
        foreach (MapLayer layer in Layers)
        {
            if (layer.Index == index)
                return layer;
        }
        return null;
    }

    /// <summary>
    /// Every foothold in the map, flattened out of
    /// <c>foothold/&lt;layer&gt;/&lt;group&gt;/&lt;id&gt;</c> with its layer and
    /// group kept on each entry. Not keyed by id: 69 maps carry a duplicate.
    /// </summary>
    public IReadOnlyList<MapFoothold> Footholds { get; }

    /// <summary>
    /// Every spawn, flattened across <b>both</b> life shapes — the ordinary
    /// <c>life/&lt;i&gt;</c> and the <c>life/isCategory</c> form that nests one
    /// level deeper on 25 maps.
    /// </summary>
    public IReadOnlyList<MapLife> Life { get; }

    /// <summary>Whether this map's <c>life</c> list uses the categorised shape.</summary>
    public bool LifeIsCategorised => Find("life")?.Child("isCategory")?.AsInteger() == 1;

    /// <summary><c>back/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapBackground> Backgrounds { get; }

    /// <summary><c>portal/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapPortal> Portals { get; }

    /// <summary><c>ladderRope/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapLadderRope> LadderRopes { get; }

    /// <summary><c>reactor/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapReactor> Reactors { get; }

    /// <summary><c>seat/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapSeat> Seats { get; }

    /// <summary><c>area/&lt;name&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapArea> Areas { get; }

    /// <summary><c>ToolTip/&lt;i&gt;</c> entries, in file order.</summary>
    public IReadOnlyList<MapToolTip> ToolTips { get; }

    /// <summary>
    /// Every entry of every rectangle-shaped zone kind, each carrying which kind
    /// it came from.
    /// </summary>
    public IReadOnlyList<MapRectZone> RectZones { get; }

    /// <summary><c>miniMap</c>, or null on the 939 geometry maps without one.</summary>
    public MapMiniMap MiniMap { get; }

    /// <summary>
    /// True when this map carries geometry of its own. 10,596 maps do; 6,850 are
    /// link stubs; none has neither; 4 have both.
    /// </summary>
    public bool HasGeometry => Find("foothold") != null;

    /// <summary>
    /// True when this map points at another for its content.
    /// <b>This does not mean the map is empty</b> — 87 link stubs carry portals,
    /// 5 carry life, and several other kinds appear on stubs too.
    /// </summary>
    public bool IsLinkStub => Info?.Link != null;

    #endregion

    #region Load and build

    /// <summary>
    /// Loads a parsed map image into the model, or refuses it with a reason.
    /// </summary>
    /// <param name="image">
    /// The image to read. It is parsed here if it has not been already; a parse
    /// that fails is a refusal, not an empty document — MapleLib's
    /// <c>ParseImage</c> returns false without throwing and leaves the property
    /// list empty, which is indistinguishable from a legitimately empty image
    /// unless the return value is checked.
    /// </param>
    public static MapLoadResult Load(WzImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!image.ParseImage())
        {
            return MapLoadResult.Refused(
                image.Name,
                MapRefusal.ParseFailed,
                $"'{image.Name}' could not be parsed. Its contents are not readable, so opening it " +
                "would present an empty map and saving it would make that permanent.");
        }

        List<WzNode> nodes = WzNode.ReadImage(image, out bool complete);

        if (!complete)
        {
            return MapLoadResult.Refused(
                image.Name,
                MapRefusal.WalkStopped,
                $"'{image.Name}' could not be read completely: the walk cut a branch short at a " +
                "repeated node or the depth cap, so part of the image is not in the model. A map " +
                "that cannot be round-tripped is one the editor must decline to open, not one it " +
                "opens and quietly damages.");
        }

        return MapLoadResult.Loaded(new MapDocument(image.Name, nodes));
    }

    /// <summary>
    /// Builds a fresh <see cref="WzImage"/> from the model. Nothing is shared
    /// with the image this document was loaded from except the binary payloads
    /// the model deliberately does not decode — see <see cref="WzNode"/>.
    /// </summary>
    public WzImage Build() => WzNode.BuildImage(ImageName, Nodes);

    #endregion

    #region Construction helpers

    private static readonly HashSet<string> Modelled = new(StringComparer.Ordinal)
    {
        "info", "foothold", "back", "life", "portal", "ladderRope", "reactor",
        "miniMap", "seat", "area", "ToolTip",
    };

    private static bool IsModelled(string name) =>
        Modelled.Contains(name) || MapNodeKinds.IsLayerName(name);

    private static int? ParseMapId(string imageName)
    {
        if (imageName == null)
            return null;

        ReadOnlySpan<char> stem = imageName.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];

        return int.TryParse(stem, out int id) ? id : null;
    }

    private static IReadOnlyList<T> Entries<T>(WzNode container, Func<WzNode, T> make)
    {
        if (container == null)
            return [];

        List<T> entries = new(container.Children.Count);
        foreach (WzNode child in container.Children)
            entries.Add(make(child));
        return entries;
    }

    /// <summary>
    /// Finds the layers by reading the image rather than by looking for eight
    /// known names. See <see cref="MapNodeKinds.IsLayerName"/> for the map that
    /// makes the difference.
    /// </summary>
    private static IReadOnlyList<MapLayer> BuildLayers(List<WzNode> nodes)
    {
        List<MapLayer> layers = [];
        foreach (WzNode node in nodes)
        {
            if (MapNodeKinds.IsLayerName(node.Name) && int.TryParse(node.Name, out int index))
                layers.Add(new MapLayer(node, index));
        }
        return layers;
    }

    private static IReadOnlyList<MapFoothold> BuildFootholds(WzNode root)
    {
        if (root == null)
            return [];

        List<MapFoothold> footholds = [];
        foreach (WzNode layer in root.Children)
        {
            foreach (WzNode group in layer.Children)
            {
                foreach (WzNode foothold in group.Children)
                    footholds.Add(new MapFoothold(foothold, layer.Name, group.Name));
            }
        }
        return footholds;
    }

    /// <summary>
    /// Flattens both life shapes. See <see cref="MapLife"/> for why the second
    /// one exists and what iterating only the first costs.
    /// </summary>
    private static IReadOnlyList<MapLife> BuildLife(WzNode root)
    {
        if (root == null)
            return [];

        bool categorised = root.Child("isCategory")?.AsInteger() == 1;
        List<MapLife> life = [];

        foreach (WzNode child in root.Children)
        {
            if (string.Equals(child.Name, "isCategory", StringComparison.Ordinal))
                continue;

            if (categorised)
            {
                foreach (WzNode entry in child.Children)
                    life.Add(new MapLife(entry, $"{child.Name}/{entry.Name}", child.Name));
            }
            else
            {
                life.Add(new MapLife(child, child.Name, null));
            }
        }
        return life;
    }

    private static IReadOnlyList<MapRectZone> BuildRectZones(List<WzNode> nodes)
    {
        List<MapRectZone> zones = [];
        foreach (WzNode node in nodes)
        {
            if (Array.IndexOf(MapNodeKinds.RectKinds, node.Name) < 0)
                continue;
            foreach (WzNode entry in node.Children)
                zones.Add(new MapRectZone(entry, node.Name));
        }
        return zones;
    }

    #endregion

    /// <inheritdoc/>
    public override string ToString() =>
        $"{ImageName} ({Nodes.Count} nodes, {(HasGeometry ? "geometry" : "no geometry")}" +
        $"{(IsLinkStub ? ", link stub" : "")})";
}

/// <summary>Why a map could not be loaded.</summary>
public enum MapRefusal
{
    /// <summary>Loaded successfully; no refusal.</summary>
    None,

    /// <summary>
    /// MapleLib could not parse the image. It returns false rather than throwing
    /// and leaves the property list empty, so this has to be checked or an
    /// unreadable image looks exactly like an empty one.
    /// </summary>
    ParseFailed,

    /// <summary>
    /// The guarded walk stopped short — a repeated node or the depth cap — so
    /// the model is not a complete account of the image.
    /// </summary>
    WalkStopped,

    /// <summary>
    /// The map loaded, but writing it back did not reproduce it. Only
    /// <see cref="MapRoundTrip.LoadVerified"/> reaches this, and no image in the
    /// v232 client does.
    /// </summary>
    RoundTripFailed,
}

/// <summary>
/// The outcome of loading a map: a document, or a refusal with a reason a person
/// can act on. There is no third arm, and in particular no "loaded what it could"
/// — a partially loaded map that gets saved is a damaged map.
/// </summary>
public sealed class MapLoadResult
{
    private MapLoadResult(MapDocument document, string imageName, MapRefusal refusal, string reason)
    {
        Document = document;
        ImageName = imageName;
        Refusal = refusal;
        Reason = reason;
    }

    /// <summary>The loaded map, or null when <see cref="Refusal"/> is set.</summary>
    public MapDocument Document { get; }

    /// <summary>The image this result concerns.</summary>
    public string ImageName { get; }

    /// <summary>Why the map was refused, or <see cref="MapRefusal.None"/>.</summary>
    public MapRefusal Refusal { get; }

    /// <summary>A sentence explaining the refusal, or null.</summary>
    public string Reason { get; }

    /// <summary>Whether a document came back.</summary>
    public bool Ok => Document != null;

    internal static MapLoadResult Loaded(MapDocument document) =>
        new(document, document.ImageName, MapRefusal.None, null);

    internal static MapLoadResult Refused(string imageName, MapRefusal refusal, string reason) =>
        new(null, imageName, refusal, reason);
}
