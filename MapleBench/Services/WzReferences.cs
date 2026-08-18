using System.Globalization;
using System.Reflection;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// The kinds of reference one WZ node can make to another.
///
/// They are listed apart rather than merged into "a path" because each kind is
/// resolved differently, and — more importantly — each has a different rename
/// policy. Treating them as one string is what produced links that resolved
/// against the wrong base after a copy.
/// </summary>
public enum RefKind
{
    /// <summary>
    /// A canvas <c>_outlink</c>: an archive-rooted path to shared art. The one
    /// kind a rename may rewrite, and only in the segment ending <c>.img</c>.
    /// </summary>
    CanvasOutlink,

    /// <summary>
    /// A canvas <c>_inlink</c>: a path relative to the <em>nearest enclosing
    /// image</em>. Never renamed — it names nothing shared, only a sibling frame
    /// inside its own image. It is still discovered, because a subnode copy that
    /// leaves the image behind turns it into a 1x1 placeholder while every
    /// structural check still passes.
    /// </summary>
    CanvasInlink,

    /// <summary>
    /// A <see cref="WzUOLProperty"/>: parent-relative, so its meaning depends on
    /// where the holder sits. A copy landing at a different depth resolves
    /// somewhere else without any error.
    /// </summary>
    Uol,

    /// <summary>
    /// A named set — <c>bS</c>, <c>oS</c>, <c>tS</c> — naming an image under
    /// <c>Map/Back</c>, <c>Map/Obj</c> or <c>Map/Tile</c>. Shared property: reuse
    /// when identical, copy under a derived name and rewrite when it clashes.
    /// </summary>
    NamedSet,

    /// <summary>
    /// A numeric id naming another entity (<c>info/link</c>, a portal's
    /// <c>tm</c>). Never renamed: the exe, the server handlers and saved
    /// characters all key off these.
    /// </summary>
    NumericId,

    /// <summary>
    /// A name resolved in another archive that is not a path — <c>info/bgm</c>,
    /// <c>info/mapMark</c>. Renameable only if the whole family moves with it.
    /// </summary>
    ExternalName,
}

/// <summary>
/// WHICH namespace a named reference resolves in — the dimension a bare name is
/// meaningless without.
///
/// Measured on the real client and recorded in <c>docs/wz-reference-shapes.md</c>
/// §4.1: <c>spinOff1</c> is both <c>Map.wz/Obj/spinOff1.img</c> and
/// <c>Map001.wz/Back/spinOff1.img</c> — two different images, named by the same
/// map, in the same <c>info</c> block. A rename keyed on the name alone rewrites
/// both, which is why named-set rewriting could not move into
/// <see cref="WzReferenceRewriter"/> until this type existed. The five values are
/// the five <c>PortNamedRef</c>s the <c>map</c> kind declares.
/// </summary>
public enum WzNamedRole
{
    /// <summary><c>back/*/bS</c> — a background set under a Map-family <c>Back</c> directory.</summary>
    BackSet,

    /// <summary><c>*/obj/*/oS</c> — an object set under a Map-family <c>Obj</c> directory.</summary>
    ObjSet,

    /// <summary><c>*/info/tS</c> — a tile set under a Map-family <c>Tile</c> directory.</summary>
    TileSet,

    /// <summary><c>info/mapMark</c> — a mark canvas in <c>Map/MapHelper.img/mark</c>.</summary>
    MapMark,

    /// <summary><c>info/bgm</c> — an <c>image/clip</c> pair in the Sound family.</summary>
    Bgm,
}

/// <summary>
/// One place a reference is written down, with the text and the node that holds
/// it, and no opinion about what it points at.
///
/// <see cref="Read"/> always returns the <em>raw text</em>. This is the whole
/// reason the type exists: <see cref="WzUOLProperty.WzValue"/> hands back the
/// object the link resolved to, which both loses the path and succeeds quietly
/// when the link is broken, so a pass built on it cannot tell a working link
/// from a dangling one.
/// </summary>
public abstract class ReferenceSite
{
    /// <summary>What kind of reference this is, and therefore how it may be rewritten.</summary>
    public abstract RefKind Kind { get; }

    /// <summary>
    /// The node that owns the reference — the canvas for a canvas link, the UOL
    /// itself for a UOL, the value property for the rest. This is the node a
    /// caller checks against the set it is porting, so it must be the node the
    /// port actually placed.
    /// </summary>
    public abstract WzObject Holder { get; }

    /// <summary>The reference text exactly as stored. Never a resolved value.</summary>
    public abstract string? Read();

    /// <summary>
    /// Replaces the text and leaves the archive in a state that will save it —
    /// which includes marking the owning image changed and, for a UOL, dropping
    /// the resolution memo.
    /// </summary>
    public abstract void Write(string value);

    /// <summary>Where the holder lives, for reporting.</summary>
    public string Path => Holder?.FullPath ?? string.Empty;

    public override string ToString() => $"{Kind} @ {Path} -> {Read()}";

    /// <summary>
    /// The nearest enclosing image, which is both what an <c>_inlink</c> resolves
    /// against and what has to be flagged changed for a write to reach disk.
    /// </summary>
    protected static WzImage? OwningImage(WzObject? node)
    {
        while (node != null)
        {
            if (node is WzImage image)
                return image;
            node = node.Parent;
        }
        return null;
    }

    /// <summary>
    /// A write that is not flagged never reaches the file, and the port then
    /// reports a rewrite that did not happen — the exact "silent no-op" the
    /// working rules forbid.
    /// </summary>
    protected static void MarkChanged(WzObject? node)
    {
        WzImage? image = OwningImage(node);
        if (image != null)
            image.Changed = true;
    }
}

/// <summary>
/// A canvas <c>_inlink</c> or <c>_outlink</c>. The holder is the canvas, not the
/// string child: the string is a carrier with a fixed name, and the canvas is
/// what a port places, copies and checks membership of.
/// </summary>
public sealed class CanvasLinkSite : ReferenceSite
{
    private readonly WzStringProperty _carrier;

    internal CanvasLinkSite(RefKind kind, WzObject holder, WzStringProperty carrier)
    {
        Kind = kind;
        Holder = holder;
        _carrier = carrier;
    }

    public override RefKind Kind { get; }

    public override WzObject Holder { get; }

    /// <summary>The <c>_inlink</c> / <c>_outlink</c> property itself.</summary>
    public WzStringProperty Carrier => _carrier;

    public override string? Read() => _carrier.Value;

    public override void Write(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        _carrier.Value = value;
        MarkChanged(_carrier);
    }
}

/// <summary>
/// A <see cref="WzUOLProperty"/>.
///
/// The write is the point. <c>WzUOLProperty.linkVal</c> is a memo of the node the
/// link last resolved to, and it has no invalidation hook of any kind: change
/// <c>Value</c> and every later read still returns the old target, for the life
/// of the process. Nothing else in this repository nulls it. Writing through
/// this site does.
/// </summary>
public sealed class UolSite : ReferenceSite
{
    /// <summary>
    /// <c>linkVal</c> is internal to MapleLib and MapleBench is a separate
    /// assembly. Reflection rather than a MapleLib change so this layer can ship
    /// on its own; cached, and it fails loudly rather than skipping the reset,
    /// because a quietly stale memo is exactly the bug being fixed.
    /// </summary>
    private static readonly FieldInfo? LinkValField =
        typeof(WzUOLProperty).GetField("linkVal", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly WzUOLProperty _uol;

    internal UolSite(WzUOLProperty uol)
    {
        _uol = uol;
    }

    public override RefKind Kind => RefKind.Uol;

    public override WzObject Holder => _uol;

    /// <summary>The link itself, for callers that need to resolve or rebase it.</summary>
    public WzUOLProperty Uol => _uol;

    public override string? Read() => _uol.Value;

    public override void Write(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        _uol.Value = value;
        InvalidateMemo(_uol);
        MarkChanged(_uol);
    }

    /// <summary>
    /// Where this link points today, resolved symbolically and without touching
    /// the memo. See <see cref="WzUolResolver"/> for the addressing rules.
    /// </summary>
    public UolResolution Resolve() => WzUolResolver.Resolve(_uol);

    /// <summary>
    /// Rewrites the text so it still reaches <paramref name="target"/> from where
    /// the holder sits now. This is what a copy that landed at a different depth
    /// needs; without it the link keeps its old text and resolves elsewhere.
    /// Returns false — writing nothing — when the target is in another tree.
    /// </summary>
    public bool RepointTo(WzObject target)
    {
        string? text = WzUolResolver.ExpressRelative(_uol, target);
        if (text == null)
            return false;

        Write(text);
        return true;
    }

    /// <summary>Drops the resolution memo of an arbitrary UOL.</summary>
    public static void InvalidateMemo(WzUOLProperty uol)
    {
        if (uol == null)
            throw new ArgumentNullException(nameof(uol));

        FieldInfo field = LinkValField
            ?? throw new InvalidOperationException(
                "WzUOLProperty.linkVal is gone or was renamed. It is the memo a UOL write has to clear; "
                + "without clearing it every later read of this link returns the node it used to point at. "
                + "Update UolSite before shipping this.");

        field.SetValue(uol, null);
    }
}

/// <summary>
/// A reference carried by an ordinary value property: a named set, a numeric id,
/// or a name looked up in another archive. Reads and writes the scalar's text.
/// </summary>
public sealed class TextReferenceSite : ReferenceSite
{
    private readonly WzImageProperty _carrier;

    internal TextReferenceSite(RefKind kind, WzImageProperty carrier, WzNamedRole? role = null)
    {
        Kind = kind;
        _carrier = carrier;
        Role = role;
    }

    public override RefKind Kind { get; }

    /// <summary>
    /// The namespace this name resolves in, for a <see cref="RefKind.NamedSet"/>
    /// or <see cref="RefKind.ExternalName"/> site. Null for a numeric id, which
    /// has a kind table of its own and no shared namespace.
    ///
    /// This is what keeps <c>bS = "spinOff1"</c> and <c>oS = "spinOff1"</c>
    /// apart: same text, different sets, and only the role can tell a rewrite
    /// which one it was asked about.
    /// </summary>
    public WzNamedRole? Role { get; }

    public override WzObject Holder => _carrier;

    public override string? Read() => _carrier switch
    {
        WzStringProperty text => text.Value,
        WzIntProperty number => number.Value.ToString(CultureInfo.InvariantCulture),
        WzLongProperty number => number.Value.ToString(CultureInfo.InvariantCulture),
        WzShortProperty number => number.Value.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public override void Write(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        switch (_carrier)
        {
            case WzStringProperty text:
                text.Value = value;
                break;
            case WzIntProperty number:
                number.Value = int.Parse(value, CultureInfo.InvariantCulture);
                break;
            case WzLongProperty number:
                number.Value = long.Parse(value, CultureInfo.InvariantCulture);
                break;
            case WzShortProperty number:
                number.Value = short.Parse(value, CultureInfo.InvariantCulture);
                break;
            default:
                throw new InvalidOperationException(
                    $"'{_carrier.FullPath}' is a {_carrier.PropertyType} and holds no text to rewrite.");
        }

        MarkChanged(_carrier);
    }
}

/// <summary>
/// Which property names carry a <see cref="RefKind.NamedSet"/>,
/// <see cref="RefKind.NumericId"/> or <see cref="RefKind.ExternalName"/>.
///
/// A table rather than a heuristic, and the defaults are only the names this
/// repository has an existing, measured edge for. A guessed name is worse than a
/// missing one: it makes the reader claim coverage it does not have, and the
/// rename policy then acts on a value that was never a reference.
/// </summary>
public sealed class WzReferenceReaderOptions
{
    /// <summary>
    /// <c>bS</c> background set, <c>oS</c> object set, <c>tS</c> tile set — each
    /// mapped to the namespace it resolves in, because the name alone does not
    /// carry it and the real client shares names across namespaces.
    /// </summary>
    public IReadOnlyDictionary<string, WzNamedRole> NamedSetProperties { get; init; } =
        new Dictionary<string, WzNamedRole>(StringComparer.Ordinal)
        {
            ["bS"] = WzNamedRole.BackSet,
            ["oS"] = WzNamedRole.ObjSet,
            ["tS"] = WzNamedRole.TileSet,
        };

    /// <summary><c>link</c> the map a map borrows, <c>tm</c> a portal's target map.</summary>
    public IReadOnlySet<string> NumericIdProperties { get; init; } =
        new HashSet<string>(StringComparer.Ordinal) { "link", "tm" };

    /// <summary><c>bgm</c> in the Sound family, <c>mapMark</c> in Map/MapHelper.img.</summary>
    public IReadOnlyDictionary<string, WzNamedRole> ExternalNameProperties { get; init; } =
        new Dictionary<string, WzNamedRole>(StringComparer.Ordinal)
        {
            ["bgm"] = WzNamedRole.Bgm,
            ["mapMark"] = WzNamedRole.MapMark,
        };

    /// <summary>
    /// A ceiling on nodes visited, so a cyclic or pathological tree stops instead
    /// of hanging. Reaching it throws rather than truncating: a walk that quietly
    /// returned a prefix of the references would produce a plan that looks
    /// complete and is not.
    /// </summary>
    public int MaxNodes { get; init; } = 2_000_000;
}

/// <summary>
/// Finds every reference in a subtree.
/// </summary>
public interface IReferenceReader
{
    /// <summary>
    /// Every reference at or below <paramref name="subtree"/>, in a stable
    /// depth-first order.
    /// </summary>
    IEnumerable<ReferenceSite> Discover(WzObject subtree);
}

/// <summary>
/// One walk that yields every reference kind.
///
/// One walk on purpose. The report of what a port depends on and the set of
/// nodes it copies were two traversals of the same tree looking for the same
/// links, and they disagreed — a satellite archive's rows were walked for the
/// report and not for the copy, so an icon's <c>_outlink</c> into
/// <c>Character/_Canvas</c> was named in the plan and never carried. Sharing the
/// walk makes that class of disagreement structurally impossible.
///
/// Two traversal rules matter:
///
/// <list type="bullet">
/// <item>A <see cref="WzUOLProperty"/> is never recursed into.
/// <c>WzUOLProperty.WzProperties</c> returns the children of whatever the link
/// <em>resolved to</em>, so a naive walk silently leaves the subtree it was given,
/// visits another image's nodes as if they were the port's own, and memoises the
/// link on the way past.</item>
/// <item><c>_inlink</c> and <c>_outlink</c> are recognised on any property that
/// carries them, not only on a <see cref="WzCanvasProperty"/>, and only when the
/// carrier really is a <see cref="WzStringProperty"/> — reading a link through
/// <c>GetString()</c> would resolve it.</item>
/// </list>
/// </summary>
public sealed class WzReferenceReader : IReferenceReader
{
    private readonly WzReferenceReaderOptions _options;

    public WzReferenceReader() : this(new WzReferenceReaderOptions())
    {
    }

    public WzReferenceReader(WzReferenceReaderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public IEnumerable<ReferenceSite> Discover(WzObject subtree)
    {
        if (subtree == null)
            throw new ArgumentNullException(nameof(subtree));

        int visited = 0;
        Stack<WzObject> pending = new();
        pending.Push(subtree);

        while (pending.Count > 0)
        {
            WzObject node = pending.Pop();

            if (++visited > _options.MaxNodes)
                throw new InvalidOperationException(
                    $"Reference discovery passed {_options.MaxNodes} nodes under '{subtree.FullPath}'. "
                    + "Stopping rather than returning part of the answer, which would read as a complete one.");

            foreach (ReferenceSite site in SitesAt(node))
                yield return site;

            // Reversed so the pop order is the declaration order.
            List<WzObject> children = ChildrenOf(node);
            for (int i = children.Count - 1; i >= 0; i--)
                pending.Push(children[i]);
        }
    }

    /// <summary>Only the references written down on this one node.</summary>
    public IEnumerable<ReferenceSite> SitesAt(WzObject node)
    {
        if (node is WzUOLProperty uol)
        {
            yield return new UolSite(uol);
            yield break;
        }

        if (node is WzImageProperty property)
        {
            // The carrier must be a plain string. A '_outlink' that Nexon wrote as
            // a UOL is not read through GetString(), which would resolve it and
            // hand back a value in place of the path.
            if (Child(property, WzCanvasProperty.OutlinkPropertyName) is WzStringProperty outlink)
                yield return new CanvasLinkSite(RefKind.CanvasOutlink, property, outlink);

            if (Child(property, WzCanvasProperty.InlinkPropertyName) is WzStringProperty inlink)
                yield return new CanvasLinkSite(RefKind.CanvasInlink, property, inlink);

            string? name = property.Name;
            if (name != null && IsScalar(property))
            {
                if (_options.NamedSetProperties.TryGetValue(name, out WzNamedRole setRole))
                    yield return new TextReferenceSite(RefKind.NamedSet, property, setRole);
                else if (_options.NumericIdProperties.Contains(name))
                    yield return new TextReferenceSite(RefKind.NumericId, property);
                else if (_options.ExternalNameProperties.TryGetValue(name, out WzNamedRole nameRole))
                    yield return new TextReferenceSite(RefKind.ExternalName, property, nameRole);
            }
        }
    }

    /// <summary>
    /// Children without resolving anything. A <see cref="WzUOLProperty"/> reports
    /// none; see the type remarks for why that is not an omission.
    /// </summary>
    public static List<WzObject> ChildrenOf(WzObject node)
    {
        List<WzObject> children = new();
        switch (node)
        {
            case WzUOLProperty:
                break;
            case WzFile file:
                if (file.WzDirectory != null)
                    children.Add(file.WzDirectory);
                break;
            case WzDirectory directory:
                children.AddRange(directory.WzDirectories);
                children.AddRange(directory.WzImages);
                break;
            case WzImage image:
                Add(children, image.WzProperties);
                break;
            case WzImageProperty property:
                Add(children, property.WzProperties);
                break;
        }
        return children;

        static void Add(List<WzObject> into, WzPropertyCollection? properties)
        {
            if (properties == null)
                return;
            foreach (WzImageProperty child in properties)
                into.Add(child);
        }
    }

    /// <summary>
    /// A direct child by name, read off the collection rather than the indexer so
    /// nothing resolves on the way.
    /// </summary>
    private static WzImageProperty? Child(WzImageProperty property, string name)
    {
        WzPropertyCollection? properties = property is WzUOLProperty ? null : property.WzProperties;
        if (properties == null)
            return null;

        foreach (WzImageProperty child in properties)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    /// <summary>
    /// True for the value-bearing property types a named reference can live in.
    /// A container that happens to be called <c>link</c> is not an id.
    /// </summary>
    private static bool IsScalar(WzImageProperty property) => property.PropertyType switch
    {
        WzPropertyType.String => true,
        WzPropertyType.Int => true,
        WzPropertyType.Long => true,
        WzPropertyType.Short => true,
        _ => false,
    };
}
