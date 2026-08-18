using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// A canvas <c>_outlink</c> broken into the three parts a rename has to keep
/// apart: the family above the image, the image segment itself, and everything
/// below it.
///
/// Every rule here was learned from a link that a substring test got wrong:
///
/// <list type="bullet">
/// <item>The image is the <em>first segment ending <c>.img</c></em>, and it is the
/// only segment a rename may touch. Rewriting the whole prefix is how a link
/// that pointed at one archive ends up pointing at another.</item>
/// <item><c>_Canvas</c> is matched as a <em>segment</em>, never as the substring
/// <c>"/_Canvas/"</c>. A link written without its family prefix
/// (<c>_Canvas/Skill.img/…</c>) has no leading slash and the substring test
/// misses it, so the art it names is never recognised as art.</item>
/// <item>The family is every segment above the image <em>minus</em> the
/// <c>_Canvas</c> segment — not <c>parts[0]</c>. Nested families are real:
/// <c>Skill/Roguelike/_Canvas/Skill.img</c> has the family
/// <c>Skill/Roguelike</c>, and taking <c>parts[0]</c> yields <c>Skill</c>, which
/// names a different, existing image.</item>
/// </list>
///
/// The original segments are kept verbatim so that <see cref="WithImage"/> can
/// return the input text with exactly one segment changed. Reassembling from the
/// parsed <see cref="Family"/> and <see cref="Remainder"/> would silently drop
/// the <c>_Canvas</c> segment and any empty segments the text happened to carry.
/// </summary>
public readonly struct CanvasLinkPath
{
    /// <summary>The name WZ gives the folder that holds shared art images.</summary>
    public const string CanvasSegment = "_Canvas";

    /// <summary>The extension that marks the image segment of a link.</summary>
    public const string ImageSuffix = ".img";

    private readonly string[] _segments;
    private readonly int _imageAt;

    private CanvasLinkPath(string text, string[] segments, int imageAt, bool hasCanvasSegment)
    {
        Text = text;
        _segments = segments;
        _imageAt = imageAt;
        HasCanvasSegment = hasCanvasSegment;
    }

    /// <summary>The link exactly as it was read.</summary>
    public string Text { get; }

    /// <summary>True when the text named an image; false for a default instance.</summary>
    public bool IsImageLink => _segments != null;

    /// <summary>
    /// True when a segment of the prefix is literally <c>_Canvas</c>. Only these
    /// links name shared art, and only shared art is safe to rename.
    /// </summary>
    public bool HasCanvasSegment { get; }

    /// <summary>
    /// The segments above the image with the <c>_Canvas</c> segment removed —
    /// empty for a link written without its family prefix.
    /// </summary>
    public string Family => string.Join("/", FamilySegments);

    /// <summary>The image segment, suffix included: <c>Skill.img</c>.</summary>
    public string Image => _segments == null ? string.Empty : _segments[_imageAt];

    /// <summary>The image segment without its <c>.img</c> suffix.</summary>
    public string ImageStem => StripImageSuffix(Image);

    /// <summary>
    /// Everything up to and including the image segment, exactly as it would be
    /// walked: <c>Skill/_Canvas/422.img</c>.
    ///
    /// Unlike <see cref="Family"/> this keeps the <c>_Canvas</c> segment, because
    /// this is an <em>address</em> — the thing you hand to a resolver — and the
    /// folder is part of where the image actually sits. Empty segments are
    /// dropped, as they are everywhere a segment is interpreted; use
    /// <see cref="WithImage"/> when the text has to round-trip verbatim.
    /// </summary>
    public string ImagePath
    {
        get
        {
            if (_segments == null)
                return string.Empty;

            List<string> kept = new();
            for (int i = 0; i <= _imageAt; i++)
            {
                if (_segments[i].Length != 0)
                    kept.Add(_segments[i]);
            }
            return string.Join("/", kept);
        }
    }

    /// <summary>
    /// The segments of <see cref="Family"/>, already split. The first of them is
    /// the archive family a link is looked up in.
    /// </summary>
    public IReadOnlyList<string> FamilySegments
    {
        get
        {
            if (_segments == null)
                return Array.Empty<string>();

            List<string> kept = new();
            for (int i = 0; i < _imageAt; i++)
            {
                string segment = _segments[i];
                if (segment.Length == 0)
                    continue;
                if (segment.Equals(CanvasSegment, StringComparison.OrdinalIgnoreCase))
                    continue;
                kept.Add(segment);
            }
            return kept;
        }
    }

    /// <summary>Everything below the image: <c>skill/400004114/icon</c>.</summary>
    public string Remainder
    {
        get
        {
            if (_segments == null)
                return string.Empty;

            List<string> kept = new();
            for (int i = _imageAt + 1; i < _segments.Length; i++)
            {
                if (_segments[i].Length != 0)
                    kept.Add(_segments[i]);
            }
            return string.Join("/", kept);
        }
    }

    /// <summary>
    /// The link with the image segment replaced and everything else — family,
    /// <c>_Canvas</c>, remainder, even the odd empty segment — left byte for byte
    /// as it came in.
    /// </summary>
    public string WithImage(string newImageName)
    {
        if (_segments == null)
            throw new InvalidOperationException("This link does not name an image, so there is no segment to replace.");
        if (string.IsNullOrEmpty(newImageName))
            throw new ArgumentException("A replacement image name is required.", nameof(newImageName));

        string[] replaced = (string[])_segments.Clone();
        replaced[_imageAt] = EnsureImageSuffix(newImageName);
        return string.Join("/", replaced);
    }

    /// <summary>
    /// Splits a link. Returns false when no segment ends <c>.img</c>, which is the
    /// honest answer for a link that names a property rather than an image — the
    /// caller then knows there is nothing here a rename can act on.
    /// </summary>
    public static bool TryParse(string? text, out CanvasLinkPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(text))
            return false;

        // Empty segments are kept so the rejoin round-trips the input; they are
        // skipped everywhere a segment is interpreted.
        string[] segments = text.Split('/');

        int imageAt = -1;
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].EndsWith(ImageSuffix, StringComparison.OrdinalIgnoreCase)
                && segments[i].Length > ImageSuffix.Length)
            {
                imageAt = i;
                break;
            }
        }
        if (imageAt < 0)
            return false;

        bool canvas = false;
        for (int i = 0; i < imageAt; i++)
        {
            if (segments[i].Equals(CanvasSegment, StringComparison.OrdinalIgnoreCase))
            {
                canvas = true;
                break;
            }
        }

        path = new CanvasLinkPath(text, segments, imageAt, canvas);
        return true;
    }

    /// <summary>True when any segment of the path is <c>_Canvas</c>.</summary>
    public static bool NamesCanvasFolder(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (string segment in text.Split('/'))
        {
            if (segment.Equals(CanvasSegment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary><c>Skill.img</c> and <c>Skill</c> both give <c>Skill</c>.</summary>
    public static string StripImageSuffix(string name)
        => name.EndsWith(ImageSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^ImageSuffix.Length]
            : name;

    /// <summary><c>Skill</c> and <c>Skill.img</c> both give <c>Skill.img</c>.</summary>
    public static string EnsureImageSuffix(string name)
        => name.EndsWith(ImageSuffix, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ImageSuffix;
}

/// <summary>
/// The outcome of walking a UOL's text, with the reason attached when it went
/// nowhere. A resolver that answers "null" and nothing else is what turns a
/// broken link into a silent no-op.
/// </summary>
/// <param name="Target">The node the link reaches, or null.</param>
/// <param name="Failure">Why it reached nothing; null when <paramref name="Target"/> is set.</param>
public sealed record UolResolution(WzObject? Target, string? Failure)
{
    public bool Resolved => Target != null;
}

/// <summary>
/// Walks the text of a <see cref="WzUOLProperty"/> symbolically.
///
/// This exists rather than <see cref="WzUOLProperty.LinkValue"/> because that
/// property does two things a discovery pass must not do. It <em>memoises</em>
/// into <c>linkVal</c>, a field with no invalidation hook, so merely inspecting a
/// link freezes the answer for the rest of the process; and it swallows the
/// reason it failed.
///
/// The addressing rules are copied from <c>LinkValue</c> deliberately, quirks
/// included, because the client resolves the same way and the point is to see
/// what the client will see:
///
/// <list type="bullet">
/// <item>A text that does not begin <c>..</c> is resolved from
/// <c>GetTopMostWzImage()</c>. That method starts at the grandparent, so a UOL
/// sitting directly under an image is resolved from the archive's top directory,
/// not from the image. Surprising, and load-bearing — it is what makes a
/// directory-relative text like <c>Effect/BasicEff</c> legal at all.</item>
/// <item>Otherwise the base is <c>Parent</c>, and each <c>..</c> — the first one
/// included — walks up one, crossing out of the image into the directory tree
/// when it runs off the top.</item>
/// <item>On a <see cref="WzDirectory"/> a segment that names nothing is retried
/// with <c>.img</c> appended. This is the rule that makes <c>Effect/BasicEff</c>
/// resolvable, and any reader that only treats a text as a cross-image reference
/// when it contains a literal <c>.img</c> is blind to an entire class of link.</item>
/// </list>
///
/// One deliberate departure: a directory segment is tried literally <em>before</em>
/// <c>.img</c> is appended, where <c>LinkValue</c> only ever appends. Appending
/// first makes a nested directory unreachable. Resolving more than the client
/// does is safe for discovery — an extra dependency costs bytes, a missed one
/// costs a broken client.
/// </summary>
public static class WzUolResolver
{
    /// <summary>
    /// A UOL may land on another UOL. Chains are legitimate; unbounded chains are
    /// a cycle, and the cap names itself rather than returning an empty answer.
    /// </summary>
    public const int MaxHops = 16;

    /// <summary>Resolves the link, reporting why when it reaches nothing.</summary>
    public static UolResolution Resolve(WzUOLProperty uol)
    {
        if (uol == null)
            throw new ArgumentNullException(nameof(uol));

        return Resolve(uol, 0);
    }

    /// <summary>The node the link reaches, or null. Never memoises.</summary>
    public static WzObject? Target(WzUOLProperty uol) => Resolve(uol).Target;

    private static UolResolution Resolve(WzUOLProperty uol, int hop)
    {
        string? text = uol.Value;
        if (string.IsNullOrEmpty(text))
            return new UolResolution(null, "the link text is empty");

        string[] parts = text.Split('/');

        // Mirrors LinkValue: the very first segment decides the base, and it is
        // compared against ".." before any trimming.
        WzObject? at = parts[0] != ".." ? uol.GetTopMostWzImage() : uol.Parent;
        if (at == null)
            return new UolResolution(null, "the link has no parent to resolve against");

        foreach (string part in parts)
        {
            if (part.Length == 0)
                continue;

            if (part == "..")
            {
                at = at.Parent;
                if (at == null)
                    return new UolResolution(null, $"'{text}' walks up past the top of the archive");
                continue;
            }

            UolResolution step = Step(at, part, text, hop);
            if (!step.Resolved)
                return step;
            at = step.Target;
        }

        return at == null
            ? new UolResolution(null, $"'{text}' resolved to nothing")
            : new UolResolution(at, null);
    }

    private static UolResolution Step(WzObject at, string name, string text, int hop)
    {
        // A link that lands on another link continues through it. Resolved here
        // rather than through the indexer, which would memoise every hop.
        while (at is WzUOLProperty chained)
        {
            if (hop >= MaxHops)
                return new UolResolution(null, $"'{text}' follows more than {MaxHops} links; treating it as a cycle");

            hop++;
            UolResolution through = Resolve(chained, hop);
            if (!through.Resolved)
                return through;
            at = through.Target!;
        }

        switch (at)
        {
            case WzDirectory directory:
            {
                // Literal first, then the '.img' the client appends. Both, because
                // a directory can hold a sub-directory and an image side by side.
                WzObject? found = directory[name];
                if (found == null && !name.EndsWith(CanvasLinkPath.ImageSuffix, StringComparison.OrdinalIgnoreCase))
                    found = directory[name + CanvasLinkPath.ImageSuffix];
                return found == null
                    ? new UolResolution(null, $"'{text}' names '{name}', which is not in '{directory.Name}'")
                    : new UolResolution(found, null);
            }
            case WzFile file:
            {
                WzObject? found = file.WzDirectory?[name];
                return found == null
                    ? new UolResolution(null, $"'{text}' names '{name}', which is not in '{file.Name}'")
                    : new UolResolution(found, null);
            }
            case WzImage image:
            {
                WzImageProperty? found = image[name];
                return found == null
                    ? new UolResolution(null, $"'{text}' names '{name}', which is not in '{image.Name}'")
                    : new UolResolution(found, null);
            }
            case WzImageProperty property:
            {
                WzImageProperty? found = property[name];
                return found == null
                    ? new UolResolution(null, $"'{text}' names '{name}', which is not under '{property.Name}'")
                    : new UolResolution(found, null);
            }
            default:
                return new UolResolution(null, $"'{text}' passes through '{at.Name}', which holds nothing");
        }
    }

    /// <summary>
    /// The text that, written into a UOL living under <paramref name="from"/>,
    /// reaches <paramref name="target"/>.
    ///
    /// Always produced in the <c>..</c>-relative form, because that is the only
    /// form whose meaning does not depend on <c>GetTopMostWzImage()</c> — and a
    /// UOL is parent-relative, so a copy that lands at a different depth needs
    /// its text recomputed or it silently resolves somewhere else. That
    /// recomputation is what this is for.
    ///
    /// Returns null when the two are in different trees, which is a refusal the
    /// caller must handle rather than a text it can write.
    /// </summary>
    public static string? ExpressRelative(WzObject from, WzObject target)
    {
        if (from == null)
            throw new ArgumentNullException(nameof(from));
        if (target == null)
            throw new ArgumentNullException(nameof(target));

        WzObject? parent = from.Parent;
        if (parent == null)
            return null;

        // The first ".." already moves off the parent, so a target that is a
        // sibling of 'from' is reached by going up one and naming the parent
        // again. Starting the search at one step up is what encodes that.
        WzObject? up = parent;
        int steps = 0;
        while (up != null)
        {
            up = up.Parent;
            steps++;
            if (up == null)
                break;

            List<string> down = new();
            if (Descends(up, target, down))
            {
                List<string> parts = new();
                for (int i = 0; i < steps; i++)
                    parts.Add("..");
                parts.AddRange(down);
                return string.Join("/", parts);
            }
        }

        return null;
    }

    /// <summary>
    /// Fills <paramref name="down"/> with the names leading from
    /// <paramref name="ancestor"/> to <paramref name="node"/>, or returns false
    /// when it is not an ancestor.
    /// </summary>
    private static bool Descends(WzObject ancestor, WzObject node, List<string> down)
    {
        List<string> names = new();
        WzObject? at = node;
        while (at != null)
        {
            if (ReferenceEquals(at, ancestor))
            {
                names.Reverse();
                down.AddRange(names);
                return true;
            }
            names.Add(at.Name);
            at = at.Parent;
        }
        return false;
    }
}
