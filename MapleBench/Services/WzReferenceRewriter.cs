using System.Text;
using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>
/// "The art image <c>Skill.img</c> under family <c>Skill/Roguelike</c> is now
/// called <c>Skill~3f9a2c71.img</c>."
///
/// <paramref name="Family"/> is optional because a link is allowed to omit it —
/// <c>_Canvas/Skill.img/…</c> is a real and resolvable link — and a rename that
/// insisted on a family would leave those untouched. Give a family when two
/// different families hold an image of the same name; the rewriter refuses
/// rather than guesses when a family-less link is ambiguous.
/// </summary>
/// <param name="Image">The old image name, with or without <c>.img</c>.</param>
/// <param name="NewImage">The new name, with or without <c>.img</c>.</param>
/// <param name="Family">The family the image sits in, <c>_Canvas</c> excluded.</param>
public sealed record CanvasImageRename(string Image, string NewImage, string? Family = null)
{
    /// <summary>Both names compared without their suffix.</summary>
    public string Stem => CanvasLinkPath.StripImageSuffix(Image);

    /// <summary>The new name normalised to carry <c>.img</c>.</summary>
    public string NewStem => CanvasLinkPath.StripImageSuffix(NewImage);
}

/// <summary>
/// What a rewrite pass is being asked to change.
///
/// Only two things are renameable, and both are here: <c>_Canvas</c> art images
/// and named sets (<c>bS</c>, <c>oS</c>, <c>tS</c>). Everything else in
/// <see cref="RefKind"/> is discovered so it can be checked and reported, never
/// renamed — ids are the exe's and the server's, an <c>_inlink</c> names nothing
/// shared, and a UOL is rebased rather than renamed (see
/// <see cref="UolSite.RepointTo"/>).
/// </summary>
public sealed class ReferenceRewriteMap
{
    private readonly List<CanvasImageRename> _canvasImages = new();

    /// <summary>
    /// Keyed on <b>role and name</b>, never name alone. <c>bS = "spinOff1"</c>
    /// and <c>oS = "spinOff1"</c> are two different sets sharing one name on the
    /// real client, and a map keyed on the name would rewrite both — which is the
    /// defect that kept named-set rewriting out of this type until the role
    /// dimension existed.
    /// </summary>
    private readonly Dictionary<WzNamedRole, Dictionary<string, string>> _namedSets = new();

    /// <summary>The art-image renames, in the order they were added.</summary>
    public IReadOnlyList<CanvasImageRename> CanvasImages => _canvasImages;

    /// <summary>The named renames, each with the role it applies in.</summary>
    public IReadOnlyList<(WzNamedRole Role, string Set, string NewSet)> NamedSets =>
        _namedSets
            .SelectMany(role => role.Value.Select(pair => (role.Key, pair.Key, pair.Value)))
            .ToList();

    /// <summary>True when this map asks for nothing.</summary>
    public bool IsEmpty => _canvasImages.Count == 0 && _namedSets.Count == 0;

    public ReferenceRewriteMap AddCanvasImage(string image, string newImage, string? family = null)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("An image name is required.", nameof(image));
        if (string.IsNullOrWhiteSpace(newImage))
            throw new ArgumentException("A replacement image name is required.", nameof(newImage));

        _canvasImages.Add(new CanvasImageRename(image, newImage, family));
        return this;
    }

    public ReferenceRewriteMap AddCanvasImage(CanvasImageRename rename)
    {
        _canvasImages.Add(rename ?? throw new ArgumentNullException(nameof(rename)));
        return this;
    }

    /// <summary>
    /// "The <paramref name="role"/> set called <paramref name="set"/> is now
    /// called <paramref name="newSet"/>." The role is required, not defaulted:
    /// a rename that does not say which namespace it means is exactly the
    /// name-alone keying this signature exists to make unwritable.
    /// </summary>
    public ReferenceRewriteMap AddNamedSet(WzNamedRole role, string set, string newSet)
    {
        if (string.IsNullOrWhiteSpace(set))
            throw new ArgumentException("A set name is required.", nameof(set));
        if (string.IsNullOrWhiteSpace(newSet))
            throw new ArgumentException("A replacement set name is required.", nameof(newSet));

        if (!_namedSets.TryGetValue(role, out Dictionary<string, string>? sets))
            _namedSets[role] = sets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        sets[CanvasLinkPath.StripImageSuffix(set)] = CanvasLinkPath.StripImageSuffix(newSet);
        return this;
    }

    /// <summary>The new name for a set, in one role only. Null when this map says nothing about it.</summary>
    internal string? LookupNamedSet(WzNamedRole role, string stem) =>
        _namedSets.TryGetValue(role, out Dictionary<string, string>? sets)
        && sets.TryGetValue(stem, out string? renamed)
            ? renamed
            : null;

    /// <summary>
    /// The new name for an image, given the family the link named — which may be
    /// empty, because a link is allowed to omit it.
    ///
    /// Returns <see cref="CanvasRenameLookup.Ambiguous"/> rather than picking one
    /// when a family-less link matches two families. Guessing here rewrites a
    /// link to art that exists and is the wrong art, which is the failure that is
    /// impossible to spot afterwards: everything resolves, the picture is wrong.
    /// </summary>
    internal CanvasRenameLookup LookupCanvasImage(string imageStem, string linkFamily)
    {
        List<CanvasImageRename> byName = new();
        foreach (CanvasImageRename rename in _canvasImages)
        {
            if (string.Equals(rename.Stem, imageStem, StringComparison.OrdinalIgnoreCase))
                byName.Add(rename);
        }

        if (byName.Count == 0)
            return CanvasRenameLookup.None;

        if (!string.IsNullOrEmpty(linkFamily))
        {
            List<CanvasImageRename> byFamily = new();
            foreach (CanvasImageRename rename in byName)
            {
                if (string.IsNullOrEmpty(rename.Family)
                    || string.Equals(Normalise(rename.Family), Normalise(linkFamily), StringComparison.OrdinalIgnoreCase))
                {
                    byFamily.Add(rename);
                }
            }
            byName = byFamily;
        }

        return byName.Count switch
        {
            0 => CanvasRenameLookup.None,
            1 => CanvasRenameLookup.Found(byName[0]),
            _ => CanvasRenameLookup.Ambiguous,
        };
    }

    private static string Normalise(string family) => family.Trim('/');
}

/// <summary>The three answers a rename lookup can give, kept apart so "no" and "I cannot tell" never merge.</summary>
internal readonly struct CanvasRenameLookup
{
    private CanvasRenameLookup(CanvasImageRename? rename, bool ambiguous)
    {
        Rename = rename;
        IsAmbiguous = ambiguous;
    }

    public CanvasImageRename? Rename { get; }
    public bool IsAmbiguous { get; }
    public bool IsFound => Rename != null;

    public static CanvasRenameLookup None => new(null, false);
    public static CanvasRenameLookup Ambiguous => new(null, true);
    public static CanvasRenameLookup Found(CanvasImageRename rename) => new(rename, false);
}

/// <summary>What happened at one site.</summary>
public enum RewriteOutcome
{
    /// <summary>The map asked for nothing here.</summary>
    Unchanged,

    /// <summary>The text was changed.</summary>
    Rewritten,

    /// <summary>
    /// The map asked for a change and it was declined. Always carries a reason —
    /// a refusal the caller cannot read is the same as a silent no-op.
    /// </summary>
    Refused,
}

/// <summary>One site's outcome, with the text before and after.</summary>
public sealed record ReferenceRewrite(
    ReferenceSite Site,
    RewriteOutcome Outcome,
    string? Before,
    string? After,
    string? Reason)
{
    public RefKind Kind => Site.Kind;
    public string Path => Site.Path;
}

/// <summary>
/// Everything a pass did, including the sites it left alone. Reported rather
/// than counted, because a counter that is zero for two different reasons —
/// "nothing to do" and "declined everything" — has cost this project hours of
/// wrong diagnosis.
/// </summary>
public sealed record ReferenceRewriteReport(IReadOnlyList<ReferenceRewrite> Results)
{
    public int Visited => Results.Count;

    public int Rewritten => Count(RewriteOutcome.Rewritten);

    public int Refused => Count(RewriteOutcome.Refused);

    public int Unchanged => Count(RewriteOutcome.Unchanged);

    public IEnumerable<ReferenceRewrite> Refusals =>
        Results.Where(r => r.Outcome == RewriteOutcome.Refused);

    public IEnumerable<ReferenceRewrite> Changes =>
        Results.Where(r => r.Outcome == RewriteOutcome.Rewritten);

    private int Count(RewriteOutcome outcome)
    {
        int total = 0;
        foreach (ReferenceRewrite result in Results)
        {
            if (result.Outcome == outcome)
                total++;
        }
        return total;
    }

    /// <summary>A line per refusal, plus the totals — safe to put in front of a user.</summary>
    public string Summary()
    {
        StringBuilder text = new();
        text.Append(Rewritten).Append(" reference");
        if (Rewritten != 1)
            text.Append('s');
        text.Append(" rewritten, ").Append(Refused).Append(" refused, ")
            .Append(Unchanged).Append(" left as they were, out of ").Append(Visited).Append(" found.");

        foreach (ReferenceRewrite refusal in Refusals)
            text.Append("\n  refused ").Append(refusal.Path).Append(": ").Append(refusal.Reason);

        return text.ToString();
    }
}

/// <summary>
/// Applies a <see cref="ReferenceRewriteMap"/> to discovered reference sites.
///
/// Three properties are deliberate and each answers a bug that shipped:
///
/// <list type="bullet">
/// <item><b>One pass, after every node has landed.</b> Rewriting per node made
/// the outcome depend on the order the plan happened to list its entries.
/// <see cref="Apply(System.Collections.Generic.IEnumerable{ReferenceSite}, ReferenceRewriteMap, System.Collections.Generic.IReadOnlySet{WzObject})"/>
/// takes the sites already found and changes text only.</item>
/// <item><b>Idempotent.</b> A rename is keyed on the old name, so a second pass
/// finds the new name, matches nothing and reports every site
/// <see cref="RewriteOutcome.Unchanged"/>. Re-running a composition has to
/// converge, not layer.</item>
/// <item><b>Scoped.</b> Given the set of nodes this port placed, a site outside
/// it is <see cref="RewriteOutcome.Refused"/>, never quietly rewritten. Shared
/// art is <em>named</em>, not owned; rewriting a link that the target client
/// already had is how a port breaks entries nobody asked it to touch.</item>
/// </list>
/// </summary>
public sealed class WzReferenceRewriter
{
    private readonly IReferenceReader _reader;

    public WzReferenceRewriter() : this(new WzReferenceReader())
    {
    }

    public WzReferenceRewriter(IReferenceReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Discovers every reference under <paramref name="subtree"/> and applies the
    /// map. Discovery is materialised first so the walk cannot see its own edits.
    /// </summary>
    /// <param name="subtree">The tree to rewrite in.</param>
    /// <param name="map">What to rename.</param>
    /// <param name="incoming">
    /// The nodes this port placed. A site whose holder is not at or under one of
    /// them is refused. Pass null only when the whole tree is the port's own.
    /// </param>
    public ReferenceRewriteReport Rewrite(
        WzObject subtree,
        ReferenceRewriteMap map,
        IReadOnlySet<WzObject>? incoming = null)
    {
        if (subtree == null)
            throw new ArgumentNullException(nameof(subtree));

        List<ReferenceSite> sites = _reader.Discover(subtree).ToList();
        return Apply(sites, map, incoming);
    }

    /// <summary>Applies the map to sites that were already discovered.</summary>
    public ReferenceRewriteReport Apply(
        IEnumerable<ReferenceSite> sites,
        ReferenceRewriteMap map,
        IReadOnlySet<WzObject>? incoming = null)
    {
        if (sites == null)
            throw new ArgumentNullException(nameof(sites));
        if (map == null)
            throw new ArgumentNullException(nameof(map));

        List<ReferenceRewrite> results = new();

        foreach (ReferenceSite site in sites)
        {
            string? before = site.Read();
            (string? proposed, string? refusal) = Propose(site, before, map);

            if (refusal != null)
            {
                results.Add(new ReferenceRewrite(site, RewriteOutcome.Refused, before, before, refusal));
                continue;
            }

            if (proposed == null || string.Equals(proposed, before, StringComparison.Ordinal))
            {
                results.Add(new ReferenceRewrite(site, RewriteOutcome.Unchanged, before, before, null));
                continue;
            }

            if (incoming != null && !IsIncoming(site.Holder, incoming))
            {
                results.Add(new ReferenceRewrite(
                    site,
                    RewriteOutcome.Refused,
                    before,
                    before,
                    $"'{site.Path}' is not one of the nodes this port placed. Its link would become "
                    + $"'{proposed}', but the entry belongs to the target client and rewriting it would "
                    + "change an entry nobody asked to touch."));
                continue;
            }

            site.Write(proposed);
            results.Add(new ReferenceRewrite(site, RewriteOutcome.Rewritten, before, proposed, null));
        }

        return new ReferenceRewriteReport(results);
    }

    /// <summary>
    /// True when <paramref name="holder"/> is one of <paramref name="incoming"/>
    /// or sits under one. Membership is by reference: two nodes with the same
    /// path are not the same node, and the whole point of the check is to tell
    /// the copy apart from what was already there.
    /// </summary>
    public static bool IsIncoming(WzObject? holder, IReadOnlySet<WzObject> incoming)
    {
        while (holder != null)
        {
            if (incoming.Contains(holder))
                return true;
            holder = holder.Parent;
        }
        return false;
    }

    private static (string? Proposed, string? Refusal) Propose(
        ReferenceSite site,
        string? before,
        ReferenceRewriteMap map)
    {
        if (string.IsNullOrEmpty(before))
            return (null, null);

        return site.Kind switch
        {
            RefKind.CanvasOutlink => ProposeOutlink(before, map),
            RefKind.Uol => ProposeUol(before, map),
            RefKind.NamedSet => ProposeNamedSet(site, before, map),

            // bgm and mapMark are also role-carrying names in namespaces neither
            // client owns; the same role-keyed lookup serves them, and a rename
            // registered for one role can never leak into another.
            RefKind.ExternalName => ProposeNamedSet(site, before, map),

            // An '_inlink' is resolved against the nearest enclosing image, so it
            // never names a shared image and a rename of one cannot reach it. It
            // is discovered because a subnode copy that leaves its image behind
            // turns it into a 1x1 placeholder, but that is a validation question,
            // not a rewrite.
            RefKind.CanvasInlink => (null, null),

            // Ids are the exe's, the server's handlers' and saved characters'.
            // Nothing here may renumber one.
            RefKind.NumericId => (null, null),

            _ => (null, null),
        };
    }

    private static (string? Proposed, string? Refusal) ProposeOutlink(string before, ReferenceRewriteMap map)
    {
        if (!CanvasLinkPath.TryParse(before, out CanvasLinkPath link))
            return (null, null);

        CanvasRenameLookup lookup = map.LookupCanvasImage(link.ImageStem, link.Family);
        if (lookup.IsAmbiguous)
        {
            return (null,
                $"'{before}' names '{link.Image}' without a family, and the rename map holds that image "
                + "under more than one family. Refusing rather than choosing: both would resolve, and only "
                + "one is the right picture.");
        }
        if (!lookup.IsFound)
            return (null, null);

        if (!link.HasCanvasSegment)
        {
            return (null,
                $"'{before}' names '{link.Image}', which no segment of the path places under "
                + $"'{CanvasLinkPath.CanvasSegment}'. Only shared art under that folder is safe to rename; "
                + "an ordinary image is named by id and renaming it would break every other referrer.");
        }

        // Only the segment ending '.img' moves. The family above it and the
        // remainder below it are returned exactly as they came in.
        return (link.WithImage(lookup.Rename!.NewStem), null);
    }

    /// <summary>
    /// A UOL segment may name an art image, with or without the <c>.img</c> the
    /// resolver appends for it. Both forms are rewritten, and the form is kept:
    /// a text that said <c>BasicEff</c> still says <c>BasicEff~ab12</c>, not
    /// <c>BasicEff~ab12.img</c>.
    ///
    /// A segment without <c>.img</c> is only considered when the text names
    /// <c>_Canvas</c> somewhere. Otherwise a sub-property that happens to share
    /// an image's name would be renamed, and a UOL's segments are mostly ordinary
    /// property names.
    /// </summary>
    private static (string? Proposed, string? Refusal) ProposeUol(string before, ReferenceRewriteMap map)
    {
        string[] parts = before.Split('/');
        bool underCanvas = CanvasLinkPath.NamesCanvasFolder(before);
        bool changed = false;

        for (int i = 0; i < parts.Length; i++)
        {
            string segment = parts[i];
            if (segment.Length == 0 || segment == "..")
                continue;

            bool named = segment.EndsWith(CanvasLinkPath.ImageSuffix, StringComparison.OrdinalIgnoreCase);
            if (!named && !underCanvas)
                continue;

            string stem = CanvasLinkPath.StripImageSuffix(segment);
            CanvasRenameLookup lookup = map.LookupCanvasImage(stem, linkFamily: string.Empty);
            if (lookup.IsAmbiguous)
            {
                return (null,
                    $"'{before}' names '{segment}', and the rename map holds that image under more than "
                    + "one family. A UOL carries no family to disambiguate with, so this one has to be "
                    + "rewritten by hand.");
            }
            if (!lookup.IsFound)
                continue;

            parts[i] = named
                ? CanvasLinkPath.EnsureImageSuffix(lookup.Rename!.NewStem)
                : lookup.Rename!.NewStem;
            changed = true;
        }

        return changed ? (string.Join("/", parts), null) : (null, null);
    }

    private static (string? Proposed, string? Refusal) ProposeNamedSet(
        ReferenceSite site,
        string before,
        ReferenceRewriteMap map)
    {
        // The lookup is by role, and a site without one cannot be asked. Every
        // site this layer's own reader produces for these two kinds carries its
        // role, so hitting this is a reader that predates the role dimension —
        // said out loud rather than matched by name, which would silently
        // reintroduce the bS/oS collapse the role exists to prevent.
        if (site is not TextReferenceSite { Role: WzNamedRole role })
        {
            return map.NamedSets.Count == 0
                ? (null, null)
                : (null,
                    $"'{site.Path}' is a named reference with no role, and this map renames named sets "
                    + "by role. Without knowing which namespace the name resolves in, a rename here "
                    + "could rewrite a different set that happens to share the name.");
        }

        // A set is named without its extension in the value ("mapleIsland"), but
        // tolerate the suffixed form rather than miss it.
        string stem = CanvasLinkPath.StripImageSuffix(before);
        string? renamed = map.LookupNamedSet(role, stem);
        if (renamed == null)
            return (null, null);

        bool suffixed = before.EndsWith(CanvasLinkPath.ImageSuffix, StringComparison.OrdinalIgnoreCase);
        return (suffixed ? CanvasLinkPath.EnsureImageSuffix(renamed) : renamed, null);
    }
}
