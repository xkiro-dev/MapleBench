using MapleLib.WzLib.WzProperties;
using System;
using System.Collections.Generic;

namespace MapleLib.WzLib;

/// <summary>
/// The one guard every recursive walk over a WZ property tree goes through.
///
/// A WZ image looks like a tree and is not one. <c>WzUOLProperty.WzProperties</c>
/// returns the children of the node the link RESOLVES to — see WzUOLProperty.cs,
/// where <c>UOLRES</c> is switched on — so <c>property.WzProperties</c> is a tree
/// edge for every property except a link, where it is a jump to somewhere else in
/// the archive. A link that resolves to its own parent, or to any ancestor, closes
/// a loop, and a recursive walk down that loop does not come back.
///
/// What that costs is not an exception. It is a StackOverflowException, which the
/// CLR does not let anyone catch and does not let anyone log: the process is torn
/// down mid-instruction. For this editor that is every open archive's unsaved work,
/// gone, with no undo entry and nothing in the log to say why. It was reached by
/// pressing Apply on a port of anything at all, because the pass that decides
/// whether ported art may be deleted sweeps every parsed image in every archive of
/// the target client — so a quest port died in a reactor image that some earlier
/// preview had happened to parse. Reactor 2208004.img in a stock v233 client holds
/// <c>1/hit/0/uol = "../0"</c>, which resolves to the UOL's own parent. 16,099
/// frames deep, and the editor was simply gone.
///
/// Three rules, in the order they are applied:
///
///   1. A walk does not descend into a UOL at all. This is the decision, not the
///      safety net — a link's children are another node's children, and folding
///      them into this node's subtree is wrong on its own terms wherever it
///      happens to terminate: it counts a foreign image's canvases as this
///      entry's, and the passes that WRITE would re-encode or reshape pixels that
///      belong to a node nobody asked to touch. PortService already takes this
///      line where it describes a node (<c>Summarise</c>, <c>DescribeLinks</c>
///      both report a link by where it points, never by what it points at); this
///      makes it the rule for walking too. A link is still followed AS a link, by
///      reading <c>uol.Value</c>, wherever following it is the point.
///
///   2. A node is entered once. Reference equality, because two WZ properties with
///      the same name and contents are still two nodes and only identity closes a
///      loop. Nothing in MapleLib stops a subtree being added beneath one of its
///      own descendants — <c>AddProperty</c> does no ancestry check — and porting
///      clones subtrees between archives, so this is not only about links.
///
///   3. Nothing is entered past <see cref="MaxDepth"/>. Real WZ data nests a few
///      dozen levels at the outside, so a walk this deep has already met something
///      no rule above caught, and the last resort is to stop rather than to die.
///
/// <see cref="Stopped"/> reports whether 2 or 3 fired, and callers are expected to
/// treat a stopped walk as an incomplete answer rather than as a clean one — which
/// matters most where a count of zero is what authorises deleting something. Rule 1
/// does not set it: declining to walk into a link is the intended answer, not a
/// truncated one.
///
/// One instance per walk, not one per process: the visited set is what makes it
/// correct and it must not outlive the tree it was built for.
///
/// It lives in MapleLib rather than in MapleBench because MapleLib has walks of
/// its own with the same defect — <c>WzLinkResolver</c>'s fallback searches and
/// <c>WzPngMp3Serializer</c>'s export — and MapleLib cannot reference MapleBench.
/// One guard, in the lower assembly, rather than the same three rules written
/// twice and drifting: a second copy is a second place for rule 1 to be softened
/// by someone who only needed "just this one link" resolved.
/// </summary>
public sealed class WzWalk
{
    /// <summary>
    /// Deep enough that no real WZ tree comes near it, shallow enough that the
    /// frames it permits cannot exhaust a 1 MB stack. The measured crash needed
    /// 16,099.
    /// </summary>
    public const int MaxDepth = 256;

    private readonly HashSet<object> _entered = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// True once this walk has cut a branch short because of a repeat or the depth
    /// cap — i.e. once its result stopped being a complete account of the subtree.
    /// </summary>
    public bool Stopped { get; private set; }

    /// <summary>
    /// The children of <paramref name="property"/> that a walk may descend into, or
    /// null to go no further.
    ///
    /// <paramref name="depth"/> is the caller's own recursion depth, counted from
    /// whatever it called its root.
    /// </summary>
    public WzPropertyCollection? Into(WzImageProperty property, int depth)
        => Enter(property, depth) ? property.WzProperties : null;

    /// <summary>
    /// Whether a walk may descend into <paramref name="node"/>'s children at all —
    /// the same three rules, for a walk whose nodes are not all properties.
    ///
    /// A directory or an image can be neither a link nor, in practice, its own
    /// ancestor, so for those this is rules 2 and 3 only. It exists because the
    /// walks that need it most are the ITERATIVE ones: a stack or a queue does not
    /// overflow, so instead of dying they spin, and MapleBench's <c>WzSearchService</c>
    /// spun with the editor's global session gate held. An iterative walk has no
    /// natural place to hang a visited set, which is exactly why it had none.
    ///
    /// <paramref name="depth"/> is the caller's own depth, counted from whatever it
    /// called its root.
    /// </summary>
    public bool Enter(WzObject? node, int depth)
    {
        if (node == null)
            return false;

        // Rule 1. Deliberate, so it is not a truncation and does not set Stopped.
        if (node is WzUOLProperty)
            return false;

        // Rule 3 before rule 2: a walk that is already too deep should not also pay
        // to remember where it got to.
        if (depth >= MaxDepth)
        {
            Stopped = true;
            return false;
        }

        // Rule 2.
        if (!_entered.Add(node))
        {
            Stopped = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The children a walk starts from, for a root that may be an image or a
    /// property. An image cannot be a link and cannot be revisited before the walk
    /// has begun, so this is only the null-handling.
    /// </summary>
    public WzPropertyCollection? From(WzObject? root) => root switch
    {
        WzImage image => image.WzProperties,
        WzUOLProperty => null,
        WzImageProperty property => property.WzProperties,
        _ => null,
    };
}
