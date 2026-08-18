using System.Linq;
using System.Globalization;
using System.Text;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapModel;

/// <summary>
/// The harness that decides whether <see cref="MapDocument"/> keeps its one
/// promise: read a map, write it back, and have nothing be different.
///
/// <para><b>Why there are two comparisons and not one.</b> "Compare against the
/// original" has two possible meanings and they answer different questions.</para>
///
/// <list type="number">
/// <item><b>Structural.</b> Walk the original parsed tree and the rebuilt tree
/// side by side and compare name, <see cref="WzPropertyType"/>, value, child
/// count and sibling order at every node. This is definitive about the model,
/// and it is the comparison that can name the node that went wrong. Nothing in
/// MapleLib's serialiser can mask a failure here or cause one.</item>
///
/// <item><b>Byte.</b> Serialise both sides and compare the bytes. This is the
/// comparison that catches anything the structural walk forgot to look at —
/// including the binary payloads the model carries by reference, whose actual
/// bytes only meet the disk through the writer.</item>
/// </list>
///
/// <para><b>The byte comparison is three-way on purpose.</b> MapleLib's writer is
/// not byte-faithful to Nexon's bytes, and pretending otherwise would produce a
/// failure list full of things this model did not do. Two type bytes are aliases
/// on read and normalised on write — <c>0x0B</c> becomes <c>0x02</c> for a Short,
/// <c>0x13</c> becomes <c>0x03</c> for an Int — the string pool dedups on its own
/// policy rather than reproducing the source's, and the extended-block length is
/// rewritten exactly where the reader tolerates slack. So three byte strings are
/// produced:</para>
///
/// <list type="bullet">
/// <item><b>Disk</b> — the image's own bytes, copied straight out of the archive
/// by the unchanged-image path in <c>WzImage.SaveImage</c>.</item>
/// <item><b>Control</b> — the <i>original</i> tree put through MapleLib's writer.
/// Any difference between Control and Disk is MapleLib's, and this model cannot
/// fix it or be blamed for it.</item>
/// <item><b>Model</b> — the rebuilt tree put through the same writer.</item>
/// </list>
///
/// <para>The claim this model is entitled to make is <b>Model == Control</b>: the
/// bytes produced from the model are exactly the bytes the same writer produces
/// from the data it was given, so the model contributed no difference at all.
/// <b>Model == Disk</b> is reported alongside it as the stronger, honest number —
/// it is the one that is true only when MapleLib's writer is faithful too.</para>
/// </summary>
public static class MapRoundTrip
{
    /// <summary>
    /// How many differences to record before giving up on a single image. A map
    /// that has gone wrong structurally usually goes wrong everywhere below the
    /// first bad node, and a thousand lines of consequence hide the one line of
    /// cause.
    /// </summary>
    public const int MaxDifferencesPerImage = 25;

    private const int MaxCompareDepth = 512;

    /// <summary>
    /// Loads an image into the model, writes it back, and compares.
    /// </summary>
    /// <param name="image">
    /// A map image from an open archive. It is left parsed, and its
    /// <c>Changed</c> flag and block size are put back exactly as they were —
    /// see the remarks on the restore.
    /// </param>
    /// <param name="iv">
    /// The archive's encryption IV, used for both serialisations. Passing the
    /// wrong one does not invalidate Model-vs-Control, which cancels it out, but
    /// it does invalidate Model-vs-Disk.
    /// </param>
    /// <param name="preloaded">
    /// A load of this same image the caller has already done, so a sweep does
    /// not read every tree twice. Null to load here.
    /// </param>
    public static MapRoundTripResult Check(WzImage image, byte[] iv, MapLoadResult preloaded = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(iv);

        string name = image.Name;

        MapLoadResult load = preloaded ?? MapDocument.Load(image);
        if (!load.Ok)
            return MapRoundTripResult.Refused(name, load.Refusal, load.Reason);

        // Disk first. The unchanged-image path in SaveImage is a straight
        // block copy out of the archive, and it stops being available the moment
        // anything sets Changed -- which the control serialisation below does.
        byte[] disk = TryReadDiskBytes(image, iv);

        WzImage rebuilt;
        try
        {
            rebuilt = load.Document.Build();
        }
        catch (Exception ex)
        {
            return MapRoundTripResult.Refused(
                name, MapRefusal.ParseFailed,
                $"'{name}' loaded but could not be rebuilt: {ex.Message}");
        }

        List<MapNodeDifference> differences = [];
        CompareList(image.WzProperties, rebuilt.WzProperties, "", differences, 0);

        byte[] model = Serialize(rebuilt, iv);

        // The control goes last, because it reparents the original image's
        // top-level properties into the temporary sub-property SaveImage dumps
        // through. Everything that needed the untouched original has happened.
        //
        // And it is undone afterwards. SaveImage's serialising arm sets nothing
        // back: it leaves Changed set and overwrites BlockSize with the length it
        // just wrote. In a sweep that is harmless, but this is the same call an
        // editor makes when it opens a map to check it -- and an image flagged
        // Changed is one the next archive save re-serialises, so merely LOOKING
        // at a map would have rewritten it. Checking a thing must not modify it.
        bool wasChanged = image.Changed;
        int blockSize = image.BlockSize;
        byte[] control;
        try
        {
            control = SerializeTree(image, iv);
        }
        finally
        {
            image.Changed = wasChanged;
            image.BlockSize = blockSize;
        }

        return new MapRoundTripResult(
            name,
            differences,
            diskBytes: disk?.Length ?? -1,
            controlBytes: control.Length,
            modelBytes: model.Length,
            modelMatchesControl: Same(model, control),
            modelMatchesDisk: disk == null ? null : Same(model, disk),
            controlMatchesDisk: disk == null ? null : Same(control, disk),
            writerDivergence: disk == null ? null : DescribeFirstDifference(disk, control));
    }

    /// <summary>
    /// Loads a map <b>only if it round-trips</b>, and refuses it with the node
    /// that differs otherwise.
    /// </summary>
    /// <remarks>
    /// This is the rule the whole phase exists for, made available rather than
    /// merely asserted in a test: <b>a map the model cannot write back unchanged
    /// is a map the editor must decline to open, not one it opens and quietly
    /// damages.</b> All 17,442 images in the v232 client pass, so today this
    /// refuses nothing — which is exactly when it is worth wiring in, because the
    /// first map it ever refuses will be one nobody predicted.
    ///
    /// It costs a rebuild and two serialisations on top of the load, so it is a
    /// choice and not the default: a sweep that has already checked an archive
    /// does not need to check it again per open.
    /// </remarks>
    public static MapLoadResult LoadVerified(WzImage image, byte[] iv)
    {
        MapRoundTripResult result = Check(image, iv);
        if (!result.Loaded)
            return MapLoadResult.Refused(result.ImageName, result.Refusal, result.RefusalReason);

        if (!result.Ok)
        {
            string where = result.Differences.Count > 0
                ? result.Differences[0].ToString()
                : "the bytes differ although every node matched";
            return MapLoadResult.Refused(
                result.ImageName, MapRefusal.RoundTripFailed,
                $"'{result.ImageName}' does not survive being written back, so it is refused rather "
                + $"than opened: {where}. Editing it would save a map that differs from the one on "
                + "disk in a place nobody asked to change.");
        }

        // Re-read rather than hand back Check's document: Check's copy is fine,
        // but tying the returned document to the harness's internals is the kind
        // of coupling that later makes someone "optimise" the check away.
        return MapDocument.Load(image);
    }

    #region Serialisation

    /// <summary>
    /// The image's bytes exactly as they sit in the archive, or null when they
    /// are not available — an image built in memory has no block on disk to copy.
    /// </summary>
    private static byte[] TryReadDiskBytes(WzImage image, byte[] iv)
    {
        if (image.Changed || image.BlockSize <= 0)
            return null;

        try
        {
            return Serialize(image, iv);
        }
        catch (Exception)
        {
            // A missing or closed reader is a "cannot answer", not a failure of
            // the model. The byte columns say so by being null.
            return null;
        }
    }

    /// <summary>
    /// Puts an image's <i>tree</i> through the writer, whatever its Changed flag
    /// said. This is the control.
    /// </summary>
    private static byte[] SerializeTree(WzImage image, byte[] iv)
    {
        image.Changed = true;
        return Serialize(image, iv);
    }

    /// <summary>
    /// Serialises an image at stream position zero.
    /// </summary>
    /// <remarks>
    /// Position zero is not incidental. The writer's string pool records
    /// <i>absolute</i> stream positions, and the reader resolves a back-reference
    /// as image-offset plus the stored int — so the two only agree when the image
    /// starts at zero. Every caller in MapleLib obeys this; a harness that packed
    /// two images into one stream would produce a second image whose string
    /// offsets are garbage and whose bytes therefore "differ" for a reason that
    /// has nothing to do with the model.
    /// </remarks>
    public static byte[] Serialize(WzImage image, byte[] iv)
    {
        using MemoryStream stream = new();
        using (WzBinaryWriter writer = new(stream, iv, leaveOpen: true))
        {
            image.SaveImage(writer);
            writer.Flush();
        }
        return stream.ToArray();
    }

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>
    /// Where two byte strings first part company, and what is there. Null when
    /// they are identical.
    /// </summary>
    /// <remarks>
    /// "The bytes differ" is not a finding anyone can act on; "at offset 4,231 a
    /// 0x0B became a 0x02" is. The offset is relative to the start of the image
    /// block, which is where a WZ image's own string offsets are measured from
    /// too, so it can be read straight against a hex dump of the archive.
    /// </remarks>
    private static string DescribeFirstDifference(byte[] expected, byte[] actual)
    {
        int limit = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < limit; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"offset {i} of {expected.Length}: 0x{expected[i]:X2} -> 0x{actual[i]:X2}"
                    + $" (context {Hex(expected, i)} -> {Hex(actual, i)})";
            }
        }
        return expected.Length == actual.Length
            ? null
            : $"identical for {limit} bytes, then lengths differ: {expected.Length} vs {actual.Length}";

        static string Hex(byte[] bytes, int at)
        {
            int start = Math.Max(0, at - 4);
            int end = Math.Min(bytes.Length, at + 5);
            return string.Join(" ", Enumerable.Range(start, end - start).Select(i => bytes[i].ToString("X2")));
        }
    }

    #endregion

    #region Structural comparison

    /// <summary>
    /// Compares two images node for node, for the tests that have to show this
    /// comparison <b>failing</b> before its passing means anything.
    /// </summary>
    /// <remarks>
    /// An acceptance check nobody has watched fail is a check nobody has any
    /// reason to trust; three broken fixes have shipped green in this repository
    /// already. So the comparison is reachable on its own, given two trees the
    /// test built, and the falsifiability tests feed it a dropped node, a
    /// reordered pair, a trimmed key name, a retyped value and a pruned empty
    /// container, and require a difference for each.
    /// </remarks>
    internal static IReadOnlyList<MapNodeDifference> CompareForTest(WzImage expected, WzImage actual)
    {
        List<MapNodeDifference> differences = [];
        CompareList(expected.WzProperties, actual.WzProperties, "", differences, 0);
        return differences;
    }

    private static void CompareList(
        IList<WzImageProperty> expected,
        IList<WzImageProperty> actual,
        string path,
        List<MapNodeDifference> differences,
        int depth)
    {
        if (differences.Count >= MaxDifferencesPerImage)
            return;

        if (expected.Count != actual.Count)
        {
            Add(differences, path, MapDifferenceKind.ChildCount,
                expected.Count.ToString(CultureInfo.InvariantCulture),
                actual.Count.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (depth >= MaxCompareDepth)
        {
            Add(differences, path, MapDifferenceKind.TooDeep, MaxCompareDepth.ToString(), "gave up");
            return;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            Compare(expected[i], actual[i], Join(path, expected[i].Name, i), differences, depth + 1);
            if (differences.Count >= MaxDifferencesPerImage)
                return;
        }
    }

    private static void Compare(
        WzImageProperty expected,
        WzImageProperty actual,
        string path,
        List<MapNodeDifference> differences,
        int depth)
    {
        // Ordinal, and that is the whole point of the check: `speedMaxOver ` and
        // `speedMaxOver` are two keys carrying two values on two sets of maps,
        // and any comparison that trims or folds case reports them equal.
        if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
        {
            Add(differences, path, MapDifferenceKind.Name, Quote(expected.Name), Quote(actual.Name));
            return;
        }

        if (expected.PropertyType != actual.PropertyType || expected.GetType() != actual.GetType())
        {
            Add(differences, path, MapDifferenceKind.Type,
                Describe(expected), Describe(actual));
            return;
        }

        switch (expected)
        {
            case WzNullProperty:
                return;

            case WzShortProperty s:
                Value(differences, path, s.Value, ((WzShortProperty)actual).Value);
                return;

            case WzIntProperty i:
                Value(differences, path, i.Value, ((WzIntProperty)actual).Value);
                return;

            case WzLongProperty l:
                Value(differences, path, l.Value, ((WzLongProperty)actual).Value);
                return;

            // Bits, not value. -0.0f and +0.0f compare equal as floats and are
            // different bytes on the wire, and MapleLib's writer goes out of its
            // way to keep them apart -- so a comparison that used == would call a
            // sign flip identical.
            case WzFloatProperty f:
                {
                    int lhs = BitConverter.SingleToInt32Bits(f.Value);
                    int rhs = BitConverter.SingleToInt32Bits(((WzFloatProperty)actual).Value);
                    if (lhs != rhs)
                        Add(differences, path, MapDifferenceKind.Value, f.Value.ToString("R", CultureInfo.InvariantCulture),
                            ((WzFloatProperty)actual).Value.ToString("R", CultureInfo.InvariantCulture));
                    return;
                }

            case WzDoubleProperty d:
                {
                    long lhs = BitConverter.DoubleToInt64Bits(d.Value);
                    long rhs = BitConverter.DoubleToInt64Bits(((WzDoubleProperty)actual).Value);
                    if (lhs != rhs)
                        Add(differences, path, MapDifferenceKind.Value, d.Value.ToString("R", CultureInfo.InvariantCulture),
                            ((WzDoubleProperty)actual).Value.ToString("R", CultureInfo.InvariantCulture));
                    return;
                }

            case WzStringProperty str:
                {
                    string other = ((WzStringProperty)actual).Value;
                    if (!string.Equals(str.Value, other, StringComparison.Ordinal))
                        Add(differences, path, MapDifferenceKind.Value, Quote(str.Value), Quote(other));
                    return;
                }

            // uol.Value, never LinkValue: a UOL's target is another node's
            // subtree, and asking for it is how a comparison walks into a cycle.
            case WzUOLProperty uol:
                {
                    string other = ((WzUOLProperty)actual).Value;
                    if (!string.Equals(uol.Value, other, StringComparison.Ordinal))
                        Add(differences, path, MapDifferenceKind.Value, Quote(uol.Value), Quote(other));
                    return;
                }

            case WzVectorProperty vector:
                {
                    WzVectorProperty other = (WzVectorProperty)actual;
                    CompareVectorHalf(differences, path + "/X", vector.X, other.X);
                    CompareVectorHalf(differences, path + "/Y", vector.Y, other.Y);
                    return;
                }

            case WzCanvasProperty canvas:
                {
                    WzCanvasProperty other = (WzCanvasProperty)actual;
                    ComparePng(differences, path, canvas.PngProperty, other.PngProperty);
                    CompareList(canvas.WzProperties, other.WzProperties, path, differences, depth);
                    return;
                }

            case WzSubProperty sub:
                CompareList(sub.WzProperties, ((WzSubProperty)actual).WzProperties, path, differences, depth);
                return;

            case WzConvexProperty convex:
                CompareList(convex.WzProperties, ((WzConvexProperty)actual).WzProperties, path, differences, depth);
                return;

            default:
                // A carried payload. Identity is the honest test: the model does
                // not copy these, it re-attaches the object it was handed, so
                // anything other than the same object means the loader lost track
                // of it. Their actual bytes are proved separately, by the byte
                // comparison, which writes them out for real.
                if (!ReferenceEquals(expected, actual))
                {
                    Add(differences, path, MapDifferenceKind.Payload,
                        "the payload it was given", "a different object");
                }
                return;
        }
    }

    private static void CompareVectorHalf(
        List<MapNodeDifference> differences, string path, WzIntProperty expected, WzIntProperty actual)
    {
        if ((expected == null) != (actual == null))
        {
            Add(differences, path, MapDifferenceKind.VectorHalf,
                expected == null ? "absent" : "present", actual == null ? "absent" : "present");
            return;
        }
        if (expected != null && expected.Value != actual.Value)
            Value(differences, path, expected.Value, actual.Value);
    }

    private static void ComparePng(
        List<MapNodeDifference> differences, string path, WzPngProperty expected, WzPngProperty actual)
    {
        if (ReferenceEquals(expected, actual))
            return;

        if ((expected == null) != (actual == null))
        {
            Add(differences, path + "/PNG", MapDifferenceKind.Payload,
                expected == null ? "absent" : "present", actual == null ? "absent" : "present");
            return;
        }

        if (expected == null)
            return;

        if (expected.Width != actual.Width || expected.Height != actual.Height
            || expected.Format != actual.Format || expected.Mag != actual.Mag)
        {
            Add(differences, path + "/PNG", MapDifferenceKind.Payload,
                $"{expected.Width}x{expected.Height} format {expected.Format} mag {expected.Mag}",
                $"{actual.Width}x{actual.Height} format {actual.Format} mag {actual.Mag}");
        }
    }

    private static void Value(List<MapNodeDifference> differences, string path, long expected, long actual)
    {
        if (expected != actual)
        {
            Add(differences, path, MapDifferenceKind.Value,
                expected.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Add(
        List<MapNodeDifference> differences, string path, MapDifferenceKind kind, string expected, string actual)
    {
        if (differences.Count < MaxDifferencesPerImage)
            differences.Add(new MapNodeDifference(path.Length == 0 ? "/" : path, kind, expected, actual));
    }

    /// <summary>
    /// Builds the path for a child. The sibling index goes in alongside the name
    /// because duplicate sibling names are legal in WZ, and a report that named
    /// two nodes identically would be useless exactly where it is most needed.
    /// </summary>
    private static string Join(string path, string name, int index)
    {
        StringBuilder builder = new(path.Length + (name?.Length ?? 0) + 8);
        builder.Append(path).Append('/').Append(name ?? "<unnamed>");
        builder.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
        return builder.ToString();
    }

    private static string Describe(WzImageProperty property) =>
        $"{property.PropertyType} ({property.GetType().Name})";

    private static string Quote(string value) => value == null ? "<null>" : $"\"{value}\"";

    #endregion
}

/// <summary>What kind of thing differed between the original and the rebuild.</summary>
public enum MapDifferenceKind
{
    /// <summary>A container held a different number of children — order or count.</summary>
    ChildCount,

    /// <summary>A node's name differed, compared ordinally.</summary>
    Name,

    /// <summary>A node's <see cref="WzPropertyType"/> or concrete class differed.</summary>
    Type,

    /// <summary>A node's value differed.</summary>
    Value,

    /// <summary>One half of a Vector was present on one side and not the other.</summary>
    VectorHalf,

    /// <summary>A carried binary payload was not the object it should have been.</summary>
    Payload,

    /// <summary>The comparison hit its depth cap and stopped rather than recursing further.</summary>
    TooDeep,
}

/// <summary>One difference, with the exact node it happened at.</summary>
/// <param name="Path">
/// A <c>/</c>-separated path from the image root, each segment carrying the
/// child's sibling index in brackets so duplicate names stay distinguishable.
/// </param>
/// <param name="Kind">What sort of difference it is.</param>
/// <param name="Expected">What the original held.</param>
/// <param name="Actual">What came back out of the model.</param>
public sealed record MapNodeDifference(string Path, MapDifferenceKind Kind, string Expected, string Actual)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Path}: {Kind} — expected {Expected}, got {Actual}";
}

/// <summary>The result of round-tripping one map image.</summary>
public sealed class MapRoundTripResult
{
    internal MapRoundTripResult(
        string imageName,
        IReadOnlyList<MapNodeDifference> differences,
        int diskBytes,
        int controlBytes,
        int modelBytes,
        bool modelMatchesControl,
        bool? modelMatchesDisk,
        bool? controlMatchesDisk,
        string writerDivergence)
    {
        WriterDivergence = writerDivergence;
        ImageName = imageName;
        Differences = differences;
        DiskBytes = diskBytes;
        ControlBytes = controlBytes;
        ModelBytes = modelBytes;
        ModelMatchesControl = modelMatchesControl;
        ModelMatchesDisk = modelMatchesDisk;
        ControlMatchesDisk = controlMatchesDisk;
        Loaded = true;
    }

    private MapRoundTripResult(string imageName, MapRefusal refusal, string reason)
    {
        ImageName = imageName;
        Refusal = refusal;
        RefusalReason = reason;
        Differences = [];
        DiskBytes = -1;
    }

    internal static MapRoundTripResult Refused(string imageName, MapRefusal refusal, string reason) =>
        new(imageName, refusal, reason);

    /// <summary>The image this result concerns.</summary>
    public string ImageName { get; }

    /// <summary>Whether the map loaded at all.</summary>
    public bool Loaded { get; }

    /// <summary>Why it was refused, if it was.</summary>
    public MapRefusal Refusal { get; }

    /// <summary>The refusal's explanation, if there was one.</summary>
    public string RefusalReason { get; }

    /// <summary>
    /// Every difference found between the original tree and the rebuilt one, up
    /// to <see cref="MapRoundTrip.MaxDifferencesPerImage"/>.
    /// </summary>
    public IReadOnlyList<MapNodeDifference> Differences { get; }

    /// <summary>Whether the two trees are identical node for node.</summary>
    public bool StructurallyIdentical => Loaded && Differences.Count == 0;

    /// <summary>
    /// Whether the bytes produced from the model are exactly the bytes the same
    /// writer produces from the original tree. <b>This is the model's own
    /// claim</b>: true means the model contributed no difference whatsoever.
    /// </summary>
    public bool ModelMatchesControl { get; }

    /// <summary>
    /// Whether the bytes produced from the model are exactly the bytes in the
    /// archive. The strongest statement available, and true only when MapleLib's
    /// writer is byte-faithful for this image as well. Null when the image's own
    /// bytes could not be read.
    /// </summary>
    public bool? ModelMatchesDisk { get; }

    /// <summary>
    /// Whether the <i>original</i> tree, put through the writer, reproduces the
    /// archive's bytes. This isolates MapleLib's own serialisation from the
    /// model's fidelity: where this is false, <see cref="ModelMatchesDisk"/>
    /// cannot be true and the model is not the reason.
    /// </summary>
    public bool? ControlMatchesDisk { get; }

    /// <summary>
    /// Where MapleLib's writer first differs from the archive's own bytes for
    /// this image, or null when it does not. Not this model's difference —
    /// <see cref="ModelMatchesControl"/> is the model's own column.
    /// </summary>
    public string WriterDivergence { get; }

    /// <summary>Size of the image's block in the archive, or -1 if unavailable.</summary>
    public int DiskBytes { get; }

    /// <summary>Size of the control serialisation.</summary>
    public int ControlBytes { get; }

    /// <summary>Size of the model's serialisation.</summary>
    public int ModelBytes { get; }

    /// <summary>
    /// The pass condition: the map loaded, the trees are identical, and the
    /// model's bytes are the writer's own bytes for the same data.
    /// </summary>
    public bool Ok => Loaded && StructurallyIdentical && ModelMatchesControl;

    /// <inheritdoc/>
    public override string ToString()
    {
        if (!Loaded)
            return $"{ImageName}: REFUSED ({Refusal}) {RefusalReason}";
        if (Ok)
            return $"{ImageName}: ok ({ModelBytes} bytes, disk match {ModelMatchesDisk?.ToString() ?? "n/a"})";
        return $"{ImageName}: FAILED — {Differences.Count} difference(s)"
            + (ModelMatchesControl ? "" : ", bytes differ")
            + (Differences.Count > 0 ? $"; first at {Differences[0]}" : "");
    }
}
