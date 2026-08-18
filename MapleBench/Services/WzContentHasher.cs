using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using NAudio.Wave;

namespace MapleBench.Services;

/// <summary>
/// A canonical, structural SHA-256 over a WZ node's content.
///
/// It answers one question the rest of the composition engine keeps asking: are
/// these two nodes the same content, whichever archive they came out of and
/// whatever they happen to be called there. Nothing in MapleLib answered it
/// before. <see cref="WzImage.Checksum"/> is a running sum of the *encrypted*
/// image block, so it changes when the archive key changes and changes again
/// when the same image is written at a different offset -- it identifies a
/// stored blob, not a piece of content, and two clients never agree on it.
///
/// Three properties are deliberate, and each one exists because its absence
/// broke something:
///
/// 1. **Children are hashed in ordinal name-sorted order.** Stored order is not
///    stable: the same content re-saved by a different build of the packer comes
///    back with siblings in another order, and hashing stored order would report
///    that as a fork. Sorting makes reordering invisible, which is correct --
///    a WZ property list is addressed by name, never by index.
/// 2. **The node's own name is excluded at the root.** This is the property that
///    makes rename idempotent: a copy landed under a derived name
///    (<c>acc1~3f9a2c71.img</c>) must hash equal to the original it was derived
///    from, or deriving the name from the hash would not converge -- rename it,
///    hash it, get a different hash, derive a different name, forever. Child
///    names *are* included, because from the parent's point of view a child's
///    name is structure: <c>info/icon</c> and <c>info/iconRaw</c> are different
///    content even when the two canvases are byte-identical.
/// 3. **Position in the tree contributes nothing.** No path, no parent, no
///    archive. That is what lets the same art be recognised in v83 and v232.
///
/// What it is NOT: a hash of the bytes a save would write. Two archives that
/// agree here still write different bytes, because the block is keyed. Use it to
/// decide identity, never to predict a file.
///
/// Cost: hashing a node parses every image under it and reads every canvas and
/// sound payload under it. That is unavoidable -- content identity means the
/// content -- but it means this is a pass, not a predicate to call in a loop.
/// The <see cref="ConditionalWeakTable{TKey, TValue}"/> cache makes a repeated
/// ask free and holds nothing alive that the session has released.
/// </summary>
public static class WzContentHasher
{
    /// <summary>
    /// Digests, keyed by the node object itself.
    ///
    /// Weak keys because a hash must never be the reason a 200 MB image stays
    /// parsed: the session drops images under memory pressure
    /// (<see cref="ImageMemoryService"/>) and this table has to let go with it.
    /// Reference identity, not path, is the right key -- a node's path changes
    /// under a rename and its content does not.
    ///
    /// The entry is only valid while the node is unmodified. There is no change
    /// notification anywhere in MapleLib to hook, so callers that write must
    /// call <see cref="ClearCache"/> afterwards; a stale digest here would be a
    /// silently wrong "unchanged", which is exactly the failure class this
    /// engine exists to remove. The whole table is dropped rather than one
    /// entry, because a write also changes the digest of every ancestor and
    /// nothing here knows who those are.
    /// </summary>
    private static ConditionalWeakTable<WzObject, byte[]> _cache = new();

    /// <summary>Length of a SHA-256 digest, in bytes.</summary>
    private const int DigestLength = 32;

    #region Public API

    /// <summary>
    /// The content hash of <paramref name="node"/> as 64 lowercase hex
    /// characters.
    ///
    /// Lowercase because these strings end up inside derived image names
    /// (<c>acc1~3f9a2c71.img</c>) and in the composition manifest, and WZ names
    /// are compared case-insensitively -- a name whose case varies with who
    /// formatted it is a name that looks different in a diff for no reason.
    /// </summary>
    public static string Hash(WzObject node) =>
        Convert.ToHexString(Digest(node)).ToLowerInvariant();

    /// <summary>
    /// The first <paramref name="characters"/> hex characters of
    /// <see cref="Hash"/>, for the <c>~hash</c> suffix of a derived name.
    ///
    /// A prefix, never a separate shorter hash, so that extending it on a
    /// collision keeps every character that was already there and stays a
    /// function of content alone.
    /// </summary>
    public static string ShortHash(WzObject node, int characters = 8)
    {
        if (characters < 1 || characters > DigestLength * 2)
        {
            throw new ArgumentOutOfRangeException(nameof(characters),
                $"A SHA-256 prefix has between 1 and {DigestLength * 2} hex characters.");
        }
        return Hash(node)[..characters];
    }

    /// <summary>
    /// The raw 32-byte digest. A fresh copy, so a caller cannot reach into the
    /// cache through it.
    /// </summary>
    public static byte[] HashBytes(WzObject node) => (byte[])Digest(node).Clone();

    /// <summary>
    /// Whether two nodes are the same content.
    ///
    /// The one call the rewrite pass should be using in place of a name
    /// comparison: "the target already has an <c>acc1.img</c>" is not a reason
    /// to reuse it, and gating on that name alone is how a port substituted the
    /// target's own older art for the art it was asked to bring.
    /// </summary>
    public static bool ContentEquals(WzObject? left, WzObject? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        return Digest(left).AsSpan().SequenceEqual(Digest(right));
    }

    /// <summary>
    /// Forgets every cached digest.
    ///
    /// Call it after any write to an open tree. Swapping the table rather than
    /// removing entries is deliberate: a write changes the digest of the node
    /// and of every ancestor up to the archive root, and this type has no way to
    /// enumerate those.
    /// </summary>
    public static void ClearCache() => _cache = new ConditionalWeakTable<WzObject, byte[]>();

    #endregion

    #region Hashing

    /// <summary>
    /// The cached digest for a node, computed on first ask.
    ///
    /// Not locked. Two threads that race here both compute the same 32 bytes --
    /// the function is pure over an unmodified tree -- so the loser of the race
    /// costs a recomputation and nothing else, which is cheaper than serialising
    /// a whole-archive walk behind one lock.
    /// </summary>
    private static byte[] Digest(WzObject node) => Digest(node, new HashWalk());

    private static byte[] Digest(WzObject node, HashWalk walk)
    {
        ArgumentNullException.ThrowIfNull(node);

        // The table is swapped by ClearCache, so read it once: a swap between
        // the lookup and the store would otherwise write into the table that was
        // just discarded, which is harmless but pointless.
        ConditionalWeakTable<WzObject, byte[]> cache = _cache;
        if (cache.TryGetValue(node, out byte[]? cached))
            return cached;

        walk.Enter(node);
        try
        {
            using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            WriteNode(sha, node, walk);
            byte[] digest = sha.GetHashAndReset();

            cache.AddOrUpdate(node, digest);
            return digest;
        }
        finally
        {
            walk.Leave(node);
        }
    }

    /// <summary>
    /// How far into the tree one <see cref="Digest"/> call has gone, and which
    /// nodes are on the chain it is currently inside.
    ///
    /// Links are already excluded (see the <see cref="WzUOLProperty"/> arm), so
    /// this is not about them. It is <see cref="WzWalk"/>'s rule 2, and rule 2's
    /// stated reason applies here word for word: nothing in MapleLib stops a
    /// subtree being added beneath one of its own descendants -- <c>AddProperty</c>
    /// does no ancestry check -- and porting clones subtrees between archives.
    /// A tree shaped like that sends this recursion down forever, and what that
    /// costs is a StackOverflowException, which .NET does not let anyone catch:
    /// the editor is gone with every open archive's unsaved work.
    ///
    /// The response is a throw and not a truncation, which is the one thing this
    /// type must not get wrong. Every other bounded walk in this codebase reports
    /// that it stopped and lets the caller decide; a hash cannot, because a
    /// partial digest is a perfectly ordinary-looking 32 bytes and
    /// <see cref="ContentEquals"/> would answer "different content" for two nodes
    /// truncated at different points, or -- far worse -- "same content" for two
    /// truncated at the same one. That answer gates whether a port reuses the
    /// target's art instead of carrying the source's. There is no honest partial
    /// answer to "is this the same content", so the only options are the whole
    /// hash or a refusal.
    ///
    /// The set holds the current CHAIN, not everything visited: a node entered
    /// and finished is removed again. Two siblings that happen to be the same
    /// object are legal and hash the same; only re-entering a node that is still
    /// being computed is a cycle.
    /// </summary>
    private sealed class HashWalk
    {
        /// <summary>The same ceiling <see cref="WzWalk.MaxDepth"/> uses, for the same reasons.</summary>
        private const int MaxDepth = WzWalk.MaxDepth;

        private readonly HashSet<object> _onPath = new(ReferenceEqualityComparer.Instance);

        internal void Enter(WzObject node)
        {
            if (_onPath.Count >= MaxDepth)
            {
                throw new InvalidOperationException(
                    $"'{Describe(node)}' is more than {MaxDepth} levels deep, which no real WZ tree is. " +
                    "Its content hash cannot be computed, so nothing may be decided from it.");
            }

            if (!_onPath.Add(node))
            {
                throw new InvalidOperationException(
                    $"'{Describe(node)}' contains itself, so it has no content hash — hashing it would " +
                    "not terminate. Nothing may be decided from a hash of this node.");
            }
        }

        internal void Leave(WzObject node) => _onPath.Remove(node);

        /// <summary>
        /// FullPath where MapleLib can build one, and the bare name otherwise.
        /// FullPath walks parents, which a tree already known to be malformed is
        /// exactly the wrong place to trust — so a failure there must not replace
        /// this message with a second, less useful exception.
        /// </summary>
        private static string Describe(WzObject node)
        {
            try { return node.FullPath ?? node.Name ?? "(unnamed)"; }
            catch { return node.Name ?? "(unnamed)"; }
        }
    }

    /// <summary>
    /// Writes one node's canonical form: a type tag, that type's payload, then
    /// its children.
    ///
    /// Every field written below is length-prefixed (see <see cref="WriteText"/>
    /// and <see cref="WriteBlock"/>) so that no two different structures can
    /// serialise to the same byte stream -- without that, a node with children
    /// named "a" and "bc" and one with children named "ab" and "c" would collide.
    /// </summary>
    private static void WriteNode(IncrementalHash sha, WzObject node, HashWalk walk)
    {
        switch (node)
        {
            // An archive's content is its root directory's content. Routing the
            // file to the same arm as the directory makes hashing a WzFile and
            // hashing its WzDirectory agree, which they should: the extra level
            // is a MapleLib bookkeeping object, not a level of the WZ tree.
            case WzFile file:
                WriteDirectory(sha, file.WzDirectory, walk);
                return;

            case WzDirectory directory:
                WriteDirectory(sha, directory, walk);
                return;

            case WzImage image:
                WriteText(sha, "wz.img");
                WzSessionService.EnsureParsed(image);
                WriteChildren(sha, image.WzProperties, walk);
                return;

            case WzImageProperty property:
                WriteProperty(sha, property, walk);
                return;

            default:
                // Nothing else exists in MapleLib today. Tagging by type name
                // rather than throwing keeps an unknown node hashable and
                // distinct, and a future type gets its own tag for free.
                WriteText(sha, "wz.unknown:" + node.GetType().Name);
                WriteChildren(sha, null, walk);
                return;
        }
    }

    private static void WriteDirectory(IncrementalHash sha, WzDirectory directory, HashWalk walk)
    {
        WriteText(sha, "wz.dir");

        List<WzObject> children = new(directory.WzDirectories.Count + directory.WzImages.Count);
        children.AddRange(directory.WzDirectories);
        children.AddRange(directory.WzImages);
        WriteChildren(sha, children, walk);
    }

    private static void WriteProperty(IncrementalHash sha, WzImageProperty property, HashWalk walk)
    {
        // Almost every property hashes its whole child list. A canvas hashing as
        // a link is the exception and says so by replacing this.
        IReadOnlyList<WzObject>? children = property.WzProperties;

        switch (property)
        {
            #region Scalars

            case WzNullProperty:
                WriteText(sha, "prop.null");
                break;

            case WzShortProperty value:
                WriteText(sha, "prop.short");
                WriteText(sha, value.Value.ToString(CultureInfo.InvariantCulture));
                break;

            case WzIntProperty value:
                WriteText(sha, "prop.int");
                WriteText(sha, value.Value.ToString(CultureInfo.InvariantCulture));
                break;

            case WzLongProperty value:
                WriteText(sha, "prop.long");
                WriteText(sha, value.Value.ToString(CultureInfo.InvariantCulture));
                break;

            // "R" round-trips: the shortest text that reads back as the same
            // bits. The default "G" format drops the last digit or two, so two
            // genuinely different floats can print the same, and the invariant
            // culture is what keeps a decimal point a decimal point on a machine
            // whose separator is a comma.
            case WzFloatProperty value:
                WriteText(sha, "prop.float");
                WriteText(sha, value.Value.ToString("R", CultureInfo.InvariantCulture));
                break;

            case WzDoubleProperty value:
                WriteText(sha, "prop.double");
                WriteText(sha, value.Value.ToString("R", CultureInfo.InvariantCulture));
                break;

            case WzStringProperty value:
                WriteText(sha, "prop.string");
                WriteText(sha, value.Value);
                break;

            // Two ints under one property. Written as its own tag rather than as
            // a pair of children because that is what it is on disk, and because
            // an 'origin' vector and an 'origin' subproperty holding x and y are
            // not the same content.
            case WzVectorProperty vector:
                WriteText(sha, "prop.vector");
                WriteText(sha, (vector.X?.Value ?? 0).ToString(CultureInfo.InvariantCulture));
                WriteText(sha, (vector.Y?.Value ?? 0).ToString(CultureInfo.InvariantCulture));
                break;

            #endregion

            #region Links

            // A UOL is its text and nothing else.
            //
            // Never WzValue, and never WzProperties or the indexer: all three go
            // through LinkValue, which *resolves* the link -- it walks the tree,
            // memoises what it lands on in WzUOLProperty.linkVal with no
            // invalidation hook, and returns null on a broken link without
            // saying so. Hashing the resolved target would mean a dangling UOL
            // hashes like an empty one (so a client with a broken link would
            // compare equal to a client with no link), a repaired link would not
            // change the hash of the node holding it, and the answer would depend
            // on whether the sibling archives happened to be open. The text is
            // the content; what it currently reaches is a fact about the tree
            // around it.
            // Returns rather than breaks: a UOL has no children of its own, and
            // asking it for WzProperties resolves the link and would hash the
            // target's children as if they were the link's.
            case WzUOLProperty uol:
                WriteText(sha, "prop.uol");
                WriteText(sha, NormaliseLink(uol.Value));
                WriteChildren(sha, null, walk);
                return;

            #endregion

            case WzCanvasProperty canvas:
                children = WriteCanvas(sha, canvas);
                break;

            case WzBinaryProperty sound:
                WriteSound(sha, sound);
                break;

            #region Opaque payloads

            // The XOR layer over a Lua payload uses a fixed key
            // (WzKeyGenerator.GenerateLuaWzKey), not the archive's, so the
            // encrypted bytes are already portable -- but decoding first makes
            // that independence a property of this code rather than a fact one
            // has to know about MapleLib.
            case WzLuaProperty lua:
                WriteText(sha, "prop.lua");
                WriteText(sha, lua.GetString());
                break;

            case WzVideoProperty video:
                WriteText(sha, "prop.video");
                WriteBlock(sha, SHA256.HashData(video.GetBytes(false) ?? []));
                break;

            case WzRawDataProperty raw:
                WriteText(sha, "prop.raw");
                WriteBlock(sha, SHA256.HashData(raw.GetBytes(false) ?? []));
                break;

            #endregion

            case WzSubProperty:
                WriteText(sha, "prop.sub");
                break;

            case WzConvexProperty:
                WriteText(sha, "prop.convex");
                break;

            // A WzPngProperty reached directly rather than through its canvas --
            // it is addressable as the child "PNG". Same payload as the canvas
            // arm writes, under its own tag so that a bare PNG and the canvas
            // wrapping it do not collide.
            case WzPngProperty png:
                WriteText(sha, "prop.png");
                WritePngPayload(sha, png);
                break;

            default:
                WriteText(sha, "prop.unknown:" + property.GetType().Name);
                break;
        }

        WriteChildren(sha, children, walk);
    }

    /// <summary>
    /// A canvas: either the art it holds, or -- when it holds no art of its own
    /// -- the link that says where the art actually is.
    ///
    /// The link case is not an optimisation, it is a hard requirement. A linked
    /// canvas is a placeholder: MapleLib's packer leaves a 1x1 transparent stand-in
    /// beside an <c>_outlink</c> (WzPackingService.ReplaceCanvasWithOutlink), and
    /// plenty of client canvases carry an <c>_inlink</c> over no block at all.
    /// Asking one of those for its bytes reads a length of zero or less and
    /// throws "The length of the image is negative. WzPngProperty. Wrong WzIV?"
    /// -- a real archive, a correct key, and an exception that reads like a
    /// decryption failure. So a canvas with a link is never asked.
    ///
    /// Hashing it as its link text is also the right answer on the merits: the
    /// placeholder block is not content, two packers' placeholders differ in
    /// encoding while meaning the same thing, and what the canvas actually
    /// contributes to the client is which art it points at. When a port rewrites
    /// an <c>_outlink</c> to a different image, the hash changes -- correctly,
    /// because that canvas now draws something else.
    ///
    /// Both link slots are written, kept apart, and a linked canvas's remaining
    /// children are returned with the link properties removed. Removing them
    /// matters more than it looks: they would otherwise be hashed a second time
    /// as ordinary string children, with their raw text, and that second copy
    /// would undo the normalisation -- <c>Map/Back/x.img</c> and
    /// <c>map\back\X.img</c> resolve to the same canvas in the client and have to
    /// hash the same here.
    /// </summary>
    /// <returns>The children still to be hashed.</returns>
    private static IReadOnlyList<WzObject>? WriteCanvas(IncrementalHash sha, WzCanvasProperty canvas)
    {
        string? inlink = LinkTextOf(canvas, WzCanvasProperty.InlinkPropertyName);
        string? outlink = LinkTextOf(canvas, WzCanvasProperty.OutlinkPropertyName);

        if (inlink == null && outlink == null)
        {
            WriteText(sha, "prop.canvas");
            WritePngPayload(sha, canvas.PngProperty);
            return canvas.WzProperties;
        }

        // Two slots, always both written, because they are not interchangeable:
        // an '_inlink' is resolved against the image the canvas sits in and an
        // '_outlink' against the whole archive, so the same text in the two
        // slots names two different pictures.
        WriteText(sha, "prop.canvas.link");
        WriteText(sha, inlink == null ? null : NormaliseLink(inlink));
        WriteText(sha, outlink == null ? null : NormaliseLink(outlink));

        List<WzObject> rest = new(canvas.WzProperties.Count);
        foreach (WzImageProperty child in canvas.WzProperties)
        {
            if (!string.Equals(child.Name, WzCanvasProperty.InlinkPropertyName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(child.Name, WzCanvasProperty.OutlinkPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                rest.Add(child);
            }
        }
        return rest;
    }

    /// <summary>
    /// The header fields that decide how a block is read, then the block.
    ///
    /// <see cref="WzPngProperty.GetCompressedBytesForExtraction"/> rather than
    /// <c>GetCompressedBytes</c>: the raw block may still be wearing the list.wz
    /// XOR layer, which is keyed by the archive's WzKey, so the same picture in
    /// two clients would give two answers. This call strips that layer and hands
    /// back standard zlib -- the same call FlattenCanvasArt uses when it moves
    /// art between archives, which is what makes "hashes equal" and "can be
    /// copied across unchanged" the same statement.
    ///
    /// <c>false</c> for saveInMemory: a hash pass over a whole archive must not
    /// leave every canvas it touched pinned in memory.
    ///
    /// The header fields are hashed alongside the bytes because they are not
    /// derivable from them: the same compressed block read as Format1 and as
    /// Format2 is two different pictures, and mag is a separate number the client
    /// shifts the width by (see WzPngProperty.Mag -- it used to be folded into
    /// the format code, which round-tripped in this library and described
    /// something else to the game).
    /// </summary>
    private static void WritePngPayload(IncrementalHash sha, WzPngProperty? png)
    {
        if (png == null)
        {
            // A canvas with no block at all and no link. Rare, and distinct from
            // a canvas holding zero bytes, so it gets its own marker.
            WriteText(sha, "png.absent");
            return;
        }

        WriteText(sha, "png");
        WriteText(sha, png.Mag.ToString(CultureInfo.InvariantCulture));
        WriteText(sha, png.Width.ToString(CultureInfo.InvariantCulture));
        WriteText(sha, png.Height.ToString(CultureInfo.InvariantCulture));
        WriteText(sha, ((int)png.Format).ToString(CultureInfo.InvariantCulture));
        WriteBlock(sha, SHA256.HashData(png.GetCompressedBytesForExtraction(false) ?? []));
    }

    /// <summary>
    /// A sound: the fields that describe how to play it, then the payload.
    ///
    /// The payload alone is not identity -- the same MP3 frames declared at two
    /// sample rates play at two speeds -- and the descriptor alone is not either,
    /// since every BGM in a client shares one. <c>GetBytes(false)</c> reads the
    /// data block straight out of the archive with no key involved, so it is
    /// portable as it stands, and false keeps it from being retained.
    /// </summary>
    private static void WriteSound(IncrementalHash sha, WzBinaryProperty sound)
    {
        WriteText(sha, "prop.sound");
        WriteText(sha, sound.Length.ToString(CultureInfo.InvariantCulture));
        WriteText(sha, ((int)sound.SoundType).ToString(CultureInfo.InvariantCulture));
        WriteText(sha, sound.Frequency.ToString(CultureInfo.InvariantCulture));

        // WaveFormat field by field rather than ToString(): NAudio's ToString is
        // display text, free to change between versions, and a hash that moves
        // when a package is upgraded is a hash that reports every archive as
        // forked.
        WaveFormat? format = sound.WavFormat;
        if (format == null)
        {
            WriteText(sha, "wav.absent");
        }
        else
        {
            WriteText(sha, "wav");
            WriteText(sha, ((int)format.Encoding).ToString(CultureInfo.InvariantCulture));
            WriteText(sha, format.Channels.ToString(CultureInfo.InvariantCulture));
            WriteText(sha, format.SampleRate.ToString(CultureInfo.InvariantCulture));
            WriteText(sha, format.BitsPerSample.ToString(CultureInfo.InvariantCulture));
            WriteText(sha, format.AverageBytesPerSecond.ToString(CultureInfo.InvariantCulture));
            WriteText(sha, format.BlockAlign.ToString(CultureInfo.InvariantCulture));
        }

        WriteBlock(sha, SHA256.HashData(sound.GetBytes(false) ?? []));
    }

    /// <summary>
    /// The children, name-sorted, each as its name followed by its own digest.
    ///
    /// Ordinal sorting, not culture-aware: a culture-aware order puts "_inlink"
    /// and "inlink" in an order that depends on the machine's locale, and a hash
    /// that varies by machine is not an identity. Ties -- WZ permits duplicate
    /// sibling names, and MapleBench's own path syntax exists because of it --
    /// break on the digest, so a pair of same-named siblings sorts the same way
    /// wherever it is met.
    /// </summary>
    private static void WriteChildren(IncrementalHash sha, IReadOnlyList<WzObject>? children, HashWalk walk)
    {
        if (children == null || children.Count == 0)
        {
            WriteInt32(sha, 0);
            return;
        }

        List<(string Name, byte[] Digest)> ordered = new(children.Count);
        foreach (WzObject child in children)
        {
            if (child != null)
                ordered.Add((child.Name ?? string.Empty, Digest(child, walk)));
        }

        ordered.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : left.Digest.AsSpan().SequenceCompareTo(right.Digest);
        });

        WriteInt32(sha, ordered.Count);
        foreach ((string name, byte[] digest) in ordered)
        {
            WriteText(sha, name);
            WriteBlock(sha, digest);
        }
    }

    #endregion

    #region Link text

    /// <summary>
    /// One named link off a canvas, or null when the canvas does not carry it.
    ///
    /// Presence of the property is the test, not whether it resolves: an
    /// unresolvable link still means this canvas contributes a link and not
    /// pixels, and resolving here would make the hash depend on which other
    /// archives happen to be open.
    ///
    /// The value is read defensively. MapleLib's own code notes it "could get
    /// nexon'd" -- a link that is not a WzStringProperty -- and a canvas whose
    /// link is some other property type is still a linked canvas that must not be
    /// asked for bytes.
    /// </summary>
    private static string? LinkTextOf(WzCanvasProperty canvas, string linkProperty)
    {
        WzImageProperty? property = canvas[linkProperty];
        if (property == null)
            return null;
        if (property is WzStringProperty text)
            return text.Value ?? string.Empty;
        // Not a string property, but present. ToString() rather than WzValue,
        // because WzValue on a UOL would resolve it.
        return property.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Link text reduced to the form two clients can be compared on.
    ///
    /// Separators first: MapleLib writes WZ paths with '/', its own FullPath with
    /// '\', and both spellings turn up in hand-edited archives. Then case, using
    /// the invariant culture, because every lookup a link goes through
    /// (WzPropertyCollection's name index, WzCanvasProperty.GetLinkedWzImageProperty,
    /// PortService's outlink matching) is OrdinalIgnoreCase -- two links that the
    /// client cannot tell apart must not hash apart. Surrounding whitespace goes
    /// because a link with a trailing space resolves identically and is otherwise
    /// invisible in a diff.
    /// </summary>
    private static string NormaliseLink(string? link)
    {
        if (string.IsNullOrEmpty(link))
            return string.Empty;
        return link.Replace('\\', '/').Trim().ToLowerInvariant();
    }

    #endregion

    #region Canonical writer

    /// <summary>
    /// A length-prefixed UTF-8 field. A null string is written as length -1, so
    /// that a null value and an empty one are distinguishable -- WZ has both, and
    /// a missing string is not an empty string.
    /// </summary>
    private static void WriteText(IncrementalHash sha, string? text)
    {
        if (text == null)
        {
            WriteInt32(sha, -1);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(text);
        WriteInt32(sha, byteCount);
        if (byteCount == 0)
            return;

        byte[] buffer = new byte[byteCount];
        Encoding.UTF8.GetBytes(text, buffer);
        sha.AppendData(buffer);
    }

    /// <summary>A length-prefixed byte field.</summary>
    private static void WriteBlock(IncrementalHash sha, byte[] block)
    {
        WriteInt32(sha, block.Length);
        sha.AppendData(block);
    }

    /// <summary>
    /// A fixed four-byte little-endian integer. Fixed width rather than a
    /// compressed int so that the framing itself can never be ambiguous.
    /// </summary>
    private static void WriteInt32(IncrementalHash sha, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        sha.AppendData(buffer);
    }

    #endregion
}
