using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapModel;

/// <summary>
/// The faithful layer: a lossless, order-preserving, type-preserving mirror of a
/// WZ property subtree, held apart from MapleLib's own objects.
///
/// This exists because of a measurement, not a preference. The v232 client's
/// 17,442 map images contain 73 distinct top-level node kinds, 247 distinct
/// <c>info</c> keys and 7,238 distinct path shapes, and ten of those shapes are
/// structural anomalies that no schema written from memory would have predicted:
/// a <c>life</c> list that is two levels deep when <c>life/isCategory</c> is set,
/// a background list nested inside a layer, numeric keys under <c>info</c>,
/// <c>returnMap</c> sitting at the image root where only <c>info/returnMap</c>
/// belongs, and — the one that defeats every "tidy the keys up on load" helper
/// ever written — <c>info/speedMaxOver&#160;</c> with a trailing space, which is a
/// different key from <c>info/speedMaxOver</c> and carries a different value on a
/// different set of maps.
///
/// The usual answer to that is an escape hatch bolted to the side of a typed
/// model: model what you know, keep the rest as an opaque blob. That answer is
/// too weak here, because the unknown is not confined to whole top-level nodes.
/// It is a stray numeric child *inside* a life record, an <c>l3</c> segment
/// *inside* an object, a fourth level *inside* a back entry. An escape hatch at
/// the top level does not catch any of those.
///
/// So the relationship is inverted. <b>The faithful layer is the substrate and
/// the typed layer is a view over it.</b> <see cref="MapDocument"/> and the
/// records in <c>MapNodes.cs</c> hold no data of their own; they hold a
/// <see cref="WzNode"/> and read through it. Anything the typed layer does not
/// name is not "handled by the escape hatch" — it was never taken out of the
/// substrate in the first place, at any depth, and comes back out unchanged
/// because nothing ever touched it.
///
/// <para><b>Two fidelity tiers, and the boundary between them is stated rather
/// than implied.</b></para>
///
/// <list type="number">
/// <item><b>Reconstructed.</b> Null, Short, Int, Long, Float, Double, String,
/// UOL, Vector, SubProperty, Convex, and a Canvas's child properties. The name,
/// the <see cref="WzPropertyType"/> and the value are copied into this class's
/// own storage, and <see cref="Build"/> creates brand-new MapleLib objects from
/// that storage. Nothing is shared with the tree that was read, which is what
/// makes the round-trip test non-vacuous: a test that handed the original
/// objects back would pass without proving anything.</item>
///
/// <item><b>Carried.</b> A canvas's pixels (<see cref="WzPngProperty"/>), and
/// whole Sound / RawData / Video / Lua properties. These are binary payloads this
/// model deliberately does not decode, so the original object is carried by
/// reference and re-attached on <see cref="Build"/>. Byte-identical by
/// construction rather than by care — there is no encode step in which to lose
/// anything. <see cref="Payload"/> is the only place a MapleLib object survives
/// inside the model, and <see cref="Fidelity"/> says so per node.</item>
/// </list>
///
/// <para><b>Type, not just value.</b> 26 shapes in the shipping client are
/// genuinely mixed-type — <c>info/link</c> is a String on 6,695 maps and an Int
/// on 155, <c>replaceUI/&lt;name&gt;</c> has 25 Longs among 215 Ints,
/// <c>shipObj/y</c> has 4 Shorts among 7 Ints, and one single
/// <c>&lt;L&gt;/obj/&lt;i&gt;/x</c> out of 514,109 is a String. The client reads
/// leniently and MapleLib writes whatever it is given, so a model that stores
/// "the value" and picks a type on write silently retypes those. <see cref="Type"/>
/// is read from the data and written back unchanged; the typed layer's setters go
/// through <see cref="SetNumber"/>, which keeps the type that is already
/// there.</para>
///
/// <para><b>Names are compared ordinally, always.</b> MapleLib's own
/// <see cref="WzPropertyCollection"/> indexes children case-insensitively, which
/// is right for a browser and wrong for a model whose job is to give back what it
/// was given. <see cref="Child"/> is ordinal and exact. <see cref="ChildLoose"/>
/// exists for lookups that genuinely want the client's lenient behaviour, and
/// says so in its name.</para>
///
/// <para><b>Duplicate names are legal and are preserved.</b> 69 maps carry
/// duplicate foothold ids; WZ has no uniqueness rule and neither does this. The
/// children are a <see cref="List{T}"/>, in file order, and <see cref="Build"/>
/// adds them through <see cref="WzPropertyCollection.Add"/> rather than
/// <c>AddProperty</c> precisely because <see cref="WzImage.AddProperty"/> throws
/// on a name it already holds.</para>
/// </summary>
public sealed class WzNode
{
    private static readonly IReadOnlyList<WzNode> NoChildren = Array.Empty<WzNode>();

    private List<WzNode> _children;

    private WzNode(string name, WzPropertyType type)
    {
        Name = name;
        Type = type;
    }

    #region Shape

    /// <summary>
    /// The property's name exactly as it was read — trailing spaces included.
    /// Eleven maps carry <c>info/speedMaxOver&#160;</c> and four carry
    /// <c>info/canPartyStatChangeIgnoreParty&#160;</c>; both are distinct keys
    /// from their untrimmed namesakes and both are destroyed by a
    /// <c>Trim()</c> anywhere on the load path.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The shape this node was read as. Written back unchanged.
    /// <see cref="WzPropertyType.Raw"/> is ambiguous in MapleLib — both
    /// <see cref="WzRawDataProperty"/> and <see cref="WzVideoProperty"/> report
    /// it — which is one of the reasons those two are carried whole rather than
    /// reconstructed from an enum value.
    /// </summary>
    public WzPropertyType Type { get; }

    /// <summary>
    /// Whether this node's bytes are rebuilt from this model's own storage or
    /// carried through untouched. See the class remarks.
    /// </summary>
    public WzNodeFidelity Fidelity => Payload != null && Type != WzPropertyType.Canvas
        ? WzNodeFidelity.Carried
        : WzNodeFidelity.Reconstructed;

    /// <summary>
    /// The original MapleLib object for a payload this model does not decode: a
    /// canvas's <see cref="WzPngProperty"/>, or a whole Sound / RawData / Video /
    /// Lua property. Null for everything else.
    /// </summary>
    internal WzImageProperty Payload { get; private set; }

    /// <summary>
    /// Swaps the payload for a byte-faithful clone that owns its bytes, so the
    /// node no longer shares an object with any MapleLib tree. A save hands the
    /// current payload to the session tree it writes (which the post-save reopen
    /// then DISPOSES — nulling even in-memory byte caches on that object); a
    /// document that means to outlive its own save calls this after building,
    /// keeping the clone while the original rides into the write. The caller
    /// pins bytes into memory first: with the compressed bytes cached, every
    /// payload type's DeepClone copies them verbatim and never re-encodes.
    /// </summary>
    internal void DetachPayload()
    {
        if (Payload != null)
            Payload = Payload.DeepClone();
    }

    /// <summary>
    /// Children in file order. Empty is a real answer and not the same as
    /// absent: <c>reactor</c> exists with zero children on 9,560 maps,
    /// <c>ladderRope</c> on 5,756, <c>life</c> on 3,601 and <c>seat</c> on one.
    /// Pruning an empty container is a diff against the client, so nothing here
    /// prunes one.
    /// </summary>
    public IReadOnlyList<WzNode> Children => (IReadOnlyList<WzNode>)_children ?? NoChildren;

    /// <summary>
    /// True for the kinds that hold a child list on the wire, whether or not the
    /// list is currently empty.
    /// </summary>
    public bool IsContainer => _children != null;

    #endregion

    #region Values

    /// <summary>Short, Int and Long all land here; also a Vector's X.</summary>
    public long Integer { get; private set; }

    /// <summary>A Vector's Y. Meaningless for every other type.</summary>
    public int VectorY { get; private set; }

    /// <summary>
    /// True when a Vector actually carried an X (respectively Y) sub-property.
    /// MapleLib produces half-populated vectors of its own accord — its
    /// <c>DeepClone</c> copies each half only if present — so "X but no Y" is a
    /// shape that has to survive a round-trip rather than be normalised away.
    /// </summary>
    public bool HasVectorX { get; private set; }

    /// <inheritdoc cref="HasVectorX"/>
    public bool HasVectorY { get; private set; }

    /// <summary>Float only. Kept apart from <see cref="Double"/> so a float's
    /// exact bit pattern — including <c>-0.0f</c>, whose sign bit MapleLib's
    /// writer takes deliberate care over — never travels through a double.</summary>
    public float Single { get; private set; }

    /// <summary>Double only.</summary>
    public double Double { get; private set; }

    /// <summary>String and UOL. A UOL's text is stored raw and is never
    /// resolved; a resolved UOL is another node's subtree, not this one's.</summary>
    public string Text { get; private set; }

    #endregion

    #region Reading

    /// <summary>
    /// Reads an image's top-level properties into the faithful layer.
    /// </summary>
    /// <param name="image">A parsed image. The caller parses; this does not, so
    /// that a parse failure is the caller's error to report rather than a silent
    /// empty document.</param>
    /// <param name="complete">
    /// False when the walk cut a branch short — a cycle through a UOL, a repeat,
    /// or the depth cap. A false here means the model is <i>not</i> a complete
    /// account of the image, and <see cref="MapDocument.Load"/> refuses the map
    /// rather than returning a truncated one. A map the editor cannot round-trip
    /// is a map it must decline to open, not one it opens and quietly damages.
    /// </param>
    public static List<WzNode> ReadImage(WzImage image, out bool complete)
    {
        ArgumentNullException.ThrowIfNull(image);

        WzWalk walk = new();
        List<WzNode> nodes = new();

        WzPropertyCollection roots = walk.From(image);
        if (roots != null)
        {
            foreach (WzImageProperty property in roots)
                nodes.Add(Read(property, walk, 1));
        }

        complete = !walk.Stopped;
        return nodes;
    }

    /// <summary>
    /// Reads one property and, for a container, everything under it.
    /// </summary>
    /// <remarks>
    /// The descent goes through <see cref="WzWalk"/> rather than straight at
    /// <c>WzProperties</c>. A WZ image is not a tree: <c>WzUOLProperty</c>'s
    /// children are the children of whatever it resolves to, so a link pointing
    /// at an ancestor closes a loop that a plain recursion follows until the
    /// stack is gone — and a StackOverflowException cannot be caught, so the
    /// process dies with every open archive's unsaved work still in it. UOLs are
    /// handled here before the walk is ever consulted, by storing the link text
    /// and descending no further, which is also the only correct reading: a
    /// link's target belongs to the node it points at.
    /// </remarks>
    private static WzNode Read(WzImageProperty property, WzWalk walk, int depth)
    {
        string name = property.Name;

        switch (property)
        {
            case WzNullProperty:
                return new WzNode(name, WzPropertyType.Null);

            case WzShortProperty s:
                return new WzNode(name, WzPropertyType.Short) { Integer = s.Value };

            case WzIntProperty i:
                return new WzNode(name, WzPropertyType.Int) { Integer = i.Value };

            case WzLongProperty l:
                return new WzNode(name, WzPropertyType.Long) { Integer = l.Value };

            case WzFloatProperty f:
                return new WzNode(name, WzPropertyType.Float) { Single = f.Value };

            case WzDoubleProperty d:
                return new WzNode(name, WzPropertyType.Double) { Double = d.Value };

            case WzStringProperty str:
                return new WzNode(name, WzPropertyType.String) { Text = str.Value };

            // Before the walk, and deliberately: the link text is the value, and
            // whatever it resolves to is somebody else's subtree.
            case WzUOLProperty uol:
                return new WzNode(name, WzPropertyType.UOL) { Text = uol.Value };

            // X and Y are fields on the property, not entries in WzProperties —
            // which returns null for a vector — so a generic child walk cannot
            // see them and this has to read them by hand.
            case WzVectorProperty vector:
                {
                    WzNode node = new(name, WzPropertyType.Vector);
                    if (vector.X != null)
                    {
                        node.Integer = vector.X.Value;
                        node.HasVectorX = true;
                    }
                    if (vector.Y != null)
                    {
                        node.VectorY = vector.Y.Value;
                        node.HasVectorY = true;
                    }
                    return node;
                }

            case WzSubProperty sub:
                return ReadContainer(sub, name, WzPropertyType.SubProperty, walk, depth);

            case WzConvexProperty convex:
                return ReadContainer(convex, name, WzPropertyType.Convex, walk, depth);

            // The children are modelled — origin, _inlink, _outlink, _hash and
            // the rest are ordinary properties an editor has every reason to
            // read. The pixels are carried: this model has no business
            // re-encoding a DXT block it never looked at, and MapleLib's own
            // PNG DeepClone falls back to a bitmap copy when it cannot reach the
            // compressed bytes, which is a re-encode wearing a copy's clothes.
            case WzCanvasProperty canvas:
                {
                    WzNode node = ReadContainer(canvas, name, WzPropertyType.Canvas, walk, depth);
                    node.Payload = canvas.PngProperty;
                    return node;
                }

            // Sound, RawData, Video, Lua, and anything a future MapleLib adds.
            // Carried whole. The enum cannot tell RawData from Video, so the
            // object itself is the record of what this was.
            default:
                return new WzNode(name, property.PropertyType) { Payload = property };
        }
    }

    private static WzNode ReadContainer(
        WzImageProperty container, string name, WzPropertyType type, WzWalk walk, int depth)
    {
        WzNode node = new(name, type) { _children = new List<WzNode>() };

        WzPropertyCollection children = walk.Into(container, depth);
        if (children == null)
            return node; // The walk stopped; walk.Stopped now says so and the load is refused.

        node._children.Capacity = children.Count;
        foreach (WzImageProperty child in children)
            node._children.Add(Read(child, walk, depth + 1));

        return node;
    }

    #endregion

    #region Writing

    /// <summary>
    /// Builds a fresh MapleLib property from this node. Nothing is shared with
    /// the tree this node was read from except a <see cref="Payload"/>.
    /// </summary>
    public WzImageProperty Build()
    {
        switch (Type)
        {
            case WzPropertyType.Null:
                return new WzNullProperty(Name);

            case WzPropertyType.Short:
                return new WzShortProperty(Name, (short)Integer);

            case WzPropertyType.Int:
                return new WzIntProperty(Name, (int)Integer);

            case WzPropertyType.Long:
                return new WzLongProperty(Name, Integer);

            case WzPropertyType.Float:
                return new WzFloatProperty(Name, Single);

            case WzPropertyType.Double:
                return new WzDoubleProperty(Name, Double);

            case WzPropertyType.String:
                return new WzStringProperty(Name, Text);

            case WzPropertyType.UOL:
                return new WzUOLProperty(Name, Text);

            case WzPropertyType.Vector:
                {
                    // Not the (string,int,int) constructor: it names the halves
                    // "" where the parser names them "X" and "Y", and it leaves
                    // their Parent unset. The names do not reach the wire, but a
                    // model that reproduces the parser's shape is one fewer thing
                    // to remember later.
                    WzVectorProperty vector = new(Name);
                    if (HasVectorX)
                        vector.X = new WzIntProperty("X", (int)Integer);
                    if (HasVectorY)
                        vector.Y = new WzIntProperty("Y", VectorY);
                    return vector;
                }

            case WzPropertyType.SubProperty:
                {
                    WzSubProperty sub = new(Name);
                    BuildChildrenInto(sub.WzProperties);
                    return sub;
                }

            case WzPropertyType.Convex:
                {
                    WzConvexProperty convex = new(Name);
                    BuildChildrenInto(convex.WzProperties);
                    return convex;
                }

            case WzPropertyType.Canvas:
                {
                    WzCanvasProperty canvas = new(Name);
                    BuildChildrenInto(canvas.WzProperties);
                    canvas.PngProperty = (WzPngProperty)Payload;
                    return canvas;
                }

            default:
                // Carried. Renaming is the one edit the model can make to a
                // payload without decoding it, so it is applied and nothing else
                // is.
                if (Payload == null)
                    throw new InvalidOperationException(
                        $"Node '{Name}' is of carried type {Type} but holds no payload, so there is " +
                        "nothing to write. This is a bug in the loader, not in the data.");
                Payload.Name = Name;
                return Payload;
        }
    }

    /// <summary>
    /// Builds a whole image. The properties go in through the collection rather
    /// than <see cref="WzImage.AddProperty"/>, which refuses a name it already
    /// holds — WZ does not, and a map with two children of the same name at the
    /// top level would otherwise throw on the way out.
    /// </summary>
    public static WzImage BuildImage(string name, IEnumerable<WzNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        WzImage image = new(name);
        image.MarkWzImageAsParsed();
        foreach (WzNode node in nodes)
            image.WzProperties.Add(node.Build());
        return image;
    }

    private void BuildChildrenInto(WzPropertyCollection target)
    {
        if (_children == null)
            return;

        target.Capacity = _children.Count;
        foreach (WzNode child in _children)
            target.Add(child.Build());
    }

    #endregion

    #region Lookup

    /// <summary>
    /// The first child with exactly this name, compared ordinally. Ordinal
    /// because <c>info/speedMaxOver&#160;</c> and <c>info/speedMaxOver</c> are
    /// two different keys and a model that conflates them cannot write both back.
    /// </summary>
    public WzNode Child(string name)
    {
        if (_children == null)
            return null;

        for (int i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Name, name, StringComparison.Ordinal))
                return _children[i];
        }
        return null;
    }

    /// <summary>
    /// The first child matching case-insensitively — the client's own lookup
    /// behaviour, and MapleLib's. Named apart from <see cref="Child"/> so that
    /// choosing it is visible at the call site rather than inherited by accident.
    /// </summary>
    public WzNode ChildLoose(string name)
    {
        if (_children == null)
            return null;

        for (int i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return _children[i];
        }
        return null;
    }

    /// <summary>Every child with this exact name. Duplicates are legal.</summary>
    public IEnumerable<WzNode> ChildrenNamed(string name)
    {
        if (_children == null)
            yield break;

        foreach (WzNode child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
                yield return child;
        }
    }

    /// <summary>
    /// Follows a <c>/</c>-separated path of ordinal-exact child names. Empty
    /// segments are skipped so a leading or doubled separator is not an error.
    /// </summary>
    public WzNode Descend(string path)
    {
        if (string.IsNullOrEmpty(path))
            return this;

        WzNode current = this;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0)
                continue;
            current = current?.Child(segment);
            if (current == null)
                return null;
        }
        return current;
    }

    #endregion

    #region Reading values across types

    /// <summary>
    /// This node's value as a number, whatever shape it was stored in —
    /// including a String that holds digits, which is how <c>life/*/id</c>,
    /// <c>reactor/*/id</c> and 1,929 of the 2,527 <c>obj/*/yOffset</c> values are
    /// stored. Null when the node holds nothing numeric.
    /// </summary>
    public double? AsNumber() => Type switch
    {
        WzPropertyType.Short or WzPropertyType.Int or WzPropertyType.Long => Integer,
        WzPropertyType.Float => Single,
        WzPropertyType.Double => Double,
        WzPropertyType.String => double.TryParse(
            Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : null,
        _ => null,
    };

    /// <summary>
    /// This node's value as a whole number, or null. Rounds a Float or Double
    /// rather than truncating, and reads a numeric String.
    /// </summary>
    public long? AsInteger()
    {
        double? number = AsNumber();
        return number.HasValue ? (long)Math.Round(number.Value, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>
    /// This node's value as text. A String or UOL gives its text; a number gives
    /// its digits; anything else gives null. <c>info/link</c> is a String on
    /// 6,695 maps and an Int on 155, and a reader that only accepts the String
    /// loses 155 link targets.
    /// </summary>
    public string AsText() => Type switch
    {
        WzPropertyType.String or WzPropertyType.UOL => Text,
        WzPropertyType.Short or WzPropertyType.Int or WzPropertyType.Long =>
            Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        WzPropertyType.Float =>
            Single.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        WzPropertyType.Double =>
            Double.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The named child's value as a whole number, or null.</summary>
    public long? IntegerAt(string name) => Child(name)?.AsInteger();

    /// <summary>The named child's value as text, or null.</summary>
    public string TextAt(string name) => Child(name)?.AsText();

    #endregion

    #region Writing values

    /// <summary>
    /// Replaces this node's value with a number, <b>keeping the type it already
    /// has</b>. A field stored as a String stays a String; a Short stays a Short.
    ///
    /// This is the rule the mixed-type census makes non-negotiable. 4 of the 11
    /// <c>shipObj/y</c> values are Shorts, 25 of the 240 <c>replaceUI</c> values
    /// are Longs, 16 of the 2,669 <c>info/limitUI</c> values are Longs, and one
    /// single <c>obj/x</c> out of 514,109 is a String. An editor that reads all
    /// of those as numbers and writes them all as Int does not lose a value — it
    /// loses the type, which is the same map coming back subtly different for
    /// every field it touched.
    /// </summary>
    public void SetNumber(double value)
    {
        switch (Type)
        {
            case WzPropertyType.Short:
                Integer = (short)Math.Round(value, MidpointRounding.AwayFromZero);
                break;
            case WzPropertyType.Int:
                Integer = (int)Math.Round(value, MidpointRounding.AwayFromZero);
                break;
            case WzPropertyType.Long:
                Integer = (long)Math.Round(value, MidpointRounding.AwayFromZero);
                break;
            case WzPropertyType.Float:
                Single = (float)value;
                break;
            case WzPropertyType.Double:
                Double = value;
                break;
            case WzPropertyType.String:
                Text = value == Math.Floor(value) && !double.IsInfinity(value)
                    ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                throw new InvalidOperationException(
                    $"'{Name}' is a {Type} and cannot hold a number. Retyping it would change what " +
                    "the client reads; create the node with the type you want instead.");
        }
    }

    /// <summary>
    /// Replaces this node's text, keeping its type. A numeric node takes the
    /// parsed number; a String or UOL takes the text as it stands.
    /// </summary>
    public void SetText(string value)
    {
        switch (Type)
        {
            case WzPropertyType.String:
            case WzPropertyType.UOL:
                Text = value;
                break;
            default:
                if (!double.TryParse(
                        value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double number))
                {
                    throw new InvalidOperationException(
                        $"'{Name}' is a {Type} and '{value}' is not a number, so writing it here " +
                        "would have to retype the node.");
                }
                SetNumber(number);
                break;
        }
    }

    #endregion

    #region Construction

    /// <summary>Creates a value node of the given shape.</summary>
    public static WzNode Scalar(string name, WzPropertyType type, double value)
    {
        WzNode node = new(name, type);
        if (type != WzPropertyType.Null)
            node.SetNumber(value);
        return node;
    }

    /// <summary>Creates a String or UOL node.</summary>
    public static WzNode OfText(string name, WzPropertyType type, string value)
    {
        if (type != WzPropertyType.String && type != WzPropertyType.UOL)
            throw new ArgumentException($"{type} does not hold text.", nameof(type));
        return new WzNode(name, type) { Text = value };
    }

    /// <summary>Creates an empty container. Empty is a legitimate final state.</summary>
    public static WzNode Container(string name, WzPropertyType type = WzPropertyType.SubProperty)
    {
        if (type != WzPropertyType.SubProperty && type != WzPropertyType.Convex && type != WzPropertyType.Canvas)
            throw new ArgumentException($"{type} does not hold children.", nameof(type));
        return new WzNode(name, type) { _children = new List<WzNode>() };
    }

    /// <summary>
    /// Creates a canvas node carrying the given pixel payload. The payload is
    /// carried by reference exactly like a canvas read from an archive — the
    /// model does not decode it, so there is no encode step to lose anything in.
    /// </summary>
    public static WzNode Canvas(string name, WzPngProperty payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new WzNode(name, WzPropertyType.Canvas)
        {
            _children = new List<WzNode>(),
            Payload = payload,
        };
    }

    /// <summary>Creates a Vector.</summary>
    public static WzNode Vector(string name, int x, int y) =>
        new(name, WzPropertyType.Vector) { Integer = x, VectorY = y, HasVectorX = true, HasVectorY = true };

    /// <summary>
    /// Appends a child. Order is the wire order; 212 map images do not begin
    /// with <c>info</c> and nothing here sorts anything.
    /// </summary>
    public WzNode Add(WzNode child)
    {
        if (_children == null)
            throw new InvalidOperationException($"'{Name}' is a {Type} and does not hold children.");
        _children.Add(child ?? throw new ArgumentNullException(nameof(child)));
        return this;
    }

    /// <summary>Inserts a child at a position, preserving the rest of the order.</summary>
    public WzNode Insert(int index, WzNode child)
    {
        if (_children == null)
            throw new InvalidOperationException($"'{Name}' is a {Type} and does not hold children.");
        _children.Insert(index, child ?? throw new ArgumentNullException(nameof(child)));
        return this;
    }

    /// <summary>Removes a child by identity. Returns whether it was there.</summary>
    public bool Remove(WzNode child) => _children != null && _children.Remove(child);

    #endregion

    /// <inheritdoc/>
    public override string ToString() => IsContainer
        ? $"{Name} ({Type}, {Children.Count} children)"
        : $"{Name} ({Type}) = {AsText() ?? "<payload>"}";
}

/// <summary>
/// Whether a node's bytes are rebuilt from the model or carried through it.
/// See <see cref="WzNode"/>'s remarks.
/// </summary>
public enum WzNodeFidelity
{
    /// <summary>Name, type and value are held by the model and written afresh.</summary>
    Reconstructed,

    /// <summary>
    /// A binary payload the model does not decode, carried by reference and
    /// re-attached unchanged.
    /// </summary>
    Carried,
}
