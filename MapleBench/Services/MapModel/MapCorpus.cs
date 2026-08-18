using System.Diagnostics;
using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;

namespace MapleBench.Services.MapModel;

/// <summary>
/// Runs the round-trip over every map image in a real client archive.
///
/// <para>This is the acceptance test, and it is the point of the whole model. A
/// round-trip that has been proved on a handful of hand-built fixtures has been
/// proved against the author's imagination; the ten structural anomalies in
/// <c>docs/map-data-model.md</c> exist precisely because the shipping data
/// contains shapes nobody would think to write a fixture for. So the number that
/// matters is measured over all 17,442 images, and every image that does not
/// round-trip is named along with the node that differs.</para>
///
/// <para><b>The census is not decoration.</b> A sweep that reports "17,442 of
/// 17,442 passed" is indistinguishable from a sweep that examined nothing, and
/// this repository has already been bitten twice by a zero that meant two
/// different things. <see cref="MapCorpusCensus"/> counts what was actually seen
/// — how many nodes of each <see cref="WzPropertyType"/>, how many of each
/// anomaly, how many empty containers — so the pass can be checked rather than
/// believed. If the anomaly counters come back zero, the run did not touch the
/// data it claims to have proved.</para>
/// </summary>
public static class MapCorpus
{
    /// <summary>
    /// Opens an archive read-only, working out its encryption by trying each
    /// scheme until one parses.
    /// </summary>
    /// <remarks>
    /// Deliberately dumb, and deliberately not shared with the editor's own
    /// opener: the acceptance harness must not inherit a heuristic that could
    /// make it agree with the code under test for the wrong reason.
    /// </remarks>
    public static WzFile Open(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        foreach (WzMapleVersion version in new[]
                 { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS })
        {
            WzFile file = new(archivePath, -1, version);
            if (file.ParseWzFile() == WzFileParseStatus.Success)
                return file;
            file.Dispose();
        }

        throw new InvalidOperationException(
            $"'{Path.GetFileName(archivePath)}' did not parse as BMS, GMS or EMS. " +
            "Its encryption is not one this harness can detect.");
    }

    /// <summary>
    /// Every image in the archive whose name is a map id — nine digits and
    /// <c>.img</c>. Recursive, because a map's directory is
    /// <c>Map/Map&lt;first digit of id&gt;</c> and there is no reason for the
    /// harness to hard-code that.
    /// </summary>
    public static List<WzImage> MapImages(WzFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        List<WzImage> images = [];
        Collect(file.WzDirectory, images);
        images.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return images;

        static void Collect(WzDirectory directory, List<WzImage> into)
        {
            if (directory == null)
                return;

            foreach (WzImage image in directory.WzImages)
            {
                string name = image.Name;
                if (name != null && name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                    && IsMapId(name.AsSpan()[..^4]))
                {
                    into.Add(image);
                }
            }

            foreach (WzDirectory child in directory.WzDirectories)
                Collect(child, into);
        }

        static bool IsMapId(ReadOnlySpan<char> stem)
        {
            if (stem.Length != 9)
                return false;
            foreach (char c in stem)
            {
                if (c is < '0' or > '9')
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Round-trips every map in the archive and reports what happened.
    /// </summary>
    /// <param name="file">An open archive. Never the client's own copy.</param>
    /// <param name="progress">
    /// Called with (done, total) every <paramref name="progressEvery"/> images.
    /// A run of this size that says nothing for four minutes reads as a hang.
    /// </param>
    /// <param name="progressEvery">How often to report progress.</param>
    public static MapCorpusReport Run(WzFile file, Action<int, int> progress = null, int progressEvery = 500)
    {
        ArgumentNullException.ThrowIfNull(file);

        byte[] iv = WzTool.GetIvByMapleVersion(file.MapleVersion);
        List<WzImage> images = MapImages(file);

        MapCorpusReport report = new(images.Count);
        Stopwatch clock = Stopwatch.StartNew();

        for (int i = 0; i < images.Count; i++)
        {
            WzImage image = images[i];

            // The census runs off the loaded document, so it is taken before the
            // round-trip spends the image. A failed load contributes nothing to
            // the census and is counted as a refusal instead.
            MapLoadResult load = MapDocument.Load(image);
            if (load.Ok)
                report.Census.Observe(load.Document);

            // The same load is handed on rather than repeated: reading the tree
            // twice would double the walk for no answer it does not already have.
            MapRoundTripResult result = MapRoundTrip.Check(image, iv, load);
            report.Record(result);

            // Every map is parsed and rebuilt; without this the run holds all
            // 17,442 trees at once and dies of it.
            image.Changed = false;
            image.UnparseImage();

            if (progress != null && (i + 1) % progressEvery == 0)
                progress(i + 1, images.Count);
        }

        report.Duration = clock.Elapsed;
        return report;
    }
}

/// <summary>
/// What a corpus run examined and what it found. Failures are enumerated, not
/// counted: a map that does not round-trip has to be nameable, because the rule
/// is that the editor refuses to open it rather than opening it and quietly
/// damaging it.
/// </summary>
public sealed class MapCorpusReport
{
    internal MapCorpusReport(int total)
    {
        Total = total;
    }

    /// <summary>How many map images the archive holds.</summary>
    public int Total { get; }

    /// <summary>How many were examined.</summary>
    public int Examined { get; private set; }

    /// <summary>
    /// How many round-tripped exactly: identical tree, and bytes identical to
    /// what the same writer produces from the original.
    /// </summary>
    public int Passed { get; private set; }

    /// <summary>How many were refused at load. These never reach a comparison.</summary>
    public int Refused { get; private set; }

    /// <summary>
    /// How many produced bytes identical to the archive's own. Bounded above by
    /// <see cref="ControlByteIdentical"/>: where MapleLib's writer does not
    /// reproduce Nexon's bytes, nothing downstream can.
    /// </summary>
    public int ByteIdenticalToDisk { get; private set; }

    /// <summary>
    /// How many images MapleLib's own writer reproduces byte for byte from the
    /// unmodified tree. The ceiling for <see cref="ByteIdenticalToDisk"/>, and
    /// the number that separates this model's fidelity from the library's.
    /// </summary>
    public int ControlByteIdentical { get; private set; }

    /// <summary>How many images had no readable block on disk to compare against.</summary>
    public int DiskBytesUnavailable { get; private set; }

    /// <summary>Total bytes of map data examined.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Every image that did not round-trip, with its differences.</summary>
    public List<MapRoundTripResult> Failures { get; } = [];

    /// <summary>Every image refused at load, with the reason.</summary>
    public List<MapRoundTripResult> Refusals { get; } = [];

    /// <summary>
    /// Every image whose bytes MapleLib's own writer does not reproduce from the
    /// unmodified tree.
    /// </summary>
    /// <remarks>
    /// These are named rather than counted because the two numbers they separate
    /// look identical in a summary and mean opposite things. An image here is one
    /// where "the model's bytes are not the archive's bytes" is true and is
    /// <b>not this model's doing</b> — the same divergence happens to the
    /// original tree. Naming them is also the only way anyone can go and find out
    /// what the writer is doing differently, which is a real defect worth fixing
    /// in its own right.
    /// </remarks>
    public List<MapRoundTripResult> WriterDivergedFromDisk { get; } = [];

    /// <summary>What the run actually saw. See <see cref="MapCorpus"/>'s remarks.</summary>
    public MapCorpusCensus Census { get; } = new();

    /// <summary>How long the run took.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>True when every map in the archive round-tripped exactly.</summary>
    public bool Ok => Examined == Total && Passed == Total;

    internal void Record(MapRoundTripResult result)
    {
        Examined++;

        if (!result.Loaded)
        {
            Refused++;
            Refusals.Add(result);
            Failures.Add(result);
            return;
        }

        TotalBytes += result.ModelBytes;

        if (result.ModelMatchesDisk is null)
            DiskBytesUnavailable++;
        else
        {
            if (result.ModelMatchesDisk.Value)
                ByteIdenticalToDisk++;
            if (result.ControlMatchesDisk == true)
                ControlByteIdentical++;
            else if (WriterDivergedFromDisk.Count < 200)
                WriterDivergedFromDisk.Add(result);
        }

        if (result.Ok)
            Passed++;
        else
            Failures.Add(result);
    }

    /// <summary>
    /// A short, honest account of the run: what passed, what did not, and — so
    /// that the pass can be checked rather than believed — what was seen.
    /// </summary>
    public string Summarise()
    {
        System.Text.StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{Passed:N0} of {Total:N0} map images round-tripped exactly in {Duration.TotalSeconds:N1} s.");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  refused at load: {Refused:N0}   failed comparison: {Failures.Count - Refused:N0}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  bytes identical to the archive: {ByteIdenticalToDisk:N0}"
            + $"   (MapleLib's own writer reaches {ControlByteIdentical:N0}; unavailable {DiskBytesUnavailable:N0})");
        text.AppendLine(CultureInfo.InvariantCulture, $"  {TotalBytes:N0} bytes of map data examined.");
        text.Append(Census.Summarise());

        if (WriterDivergedFromDisk.Count > 0)
        {
            text.AppendLine("Images MapleLib's writer does not reproduce byte for byte "
                + "(the model matches the writer on every one of these; the difference is the library's):");
            foreach (MapRoundTripResult diverged in WriterDivergedFromDisk)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  {diverged.ImageName}  disk {diverged.DiskBytes:N0} B -> written {diverged.ControlBytes:N0} B"
                    + $"; {diverged.WriterDivergence}");
            }
        }

        if (Failures.Count > 0)
        {
            text.AppendLine("Failures:");
            foreach (MapRoundTripResult failure in Failures)
                text.AppendLine("  " + failure);
        }

        return text.ToString();
    }
}

/// <summary>
/// A tally of what a corpus run actually met, so that a clean result can be
/// checked instead of taken on trust.
/// </summary>
/// <remarks>
/// The anomaly counters are the anti-vacuity guard. Each corresponds to one of
/// the ten structural anomalies measured across the v232 client, and each has a
/// known expected magnitude — <c>life/isCategory</c> on 25 maps, <c>obj/l3</c> on
/// 2, a layer-level <c>back</c> on 1, numeric <c>info</c> keys on 11,
/// trailing-space keys on 11 and 4, root <c>returnMap</c> on 1, numeric children
/// inside life records on 40. A run that reports every map passing but zero
/// anomalies seen has not proved anything about the anomalies.
/// </remarks>
public sealed class MapCorpusCensus
{
    private readonly Dictionary<WzPropertyType, long> _types = [];
    private readonly Dictionary<string, int> _emptyContainers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _topLevelKinds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _trailingSpaceKeys = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _unknownKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _firstMapWithKind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shapeOfKind = new(StringComparer.Ordinal);

    /// <summary>Total nodes read, at every depth.</summary>
    public long Nodes { get; private set; }

    /// <summary>Maps whose first top-level node is not <c>info</c>. Measured: 212.</summary>
    public int MapsNotStartingWithInfo { get; private set; }

    /// <summary>Maps using the two-level <c>life/isCategory</c> shape. Measured: 25.</summary>
    public int LifeCategorised { get; private set; }

    /// <summary>Spawns reached only through that shape. Measured: 2,516.</summary>
    public int CategorisedSpawns { get; private set; }

    /// <summary>Maps carrying an object with a fourth path segment. Measured: 2.</summary>
    public int ObjectsWithL3 { get; private set; }

    /// <summary>Maps with a <c>back</c> list inside a layer. Measured: 1.</summary>
    public int LayerLevelBackLists { get; private set; }

    /// <summary>
    /// Maps carrying a layer outside 0-7. Measured: <b>1</b> —
    /// <c>749080500.img</c> has a layer <c>8</c>, which the data model document
    /// says cannot exist. An eleventh anomaly, found by this sweep.
    /// </summary>
    public int MapsWithAnUnusualLayer { get; private set; }

    /// <summary>Maps with a numeric child directly under a layer. Measured: 1.</summary>
    public int LayerLevelNumericChildren { get; private set; }

    /// <summary>Maps with a nested <c>back/&lt;i&gt;/&lt;j&gt;</c>. Measured: 1.</summary>
    public int NestedBackEntries { get; private set; }

    /// <summary>Maps with a purely numeric <c>info</c> key. Measured: 11.</summary>
    public int NumericInfoKeys { get; private set; }

    /// <summary>Maps carrying <c>returnMap</c> at the image root. Measured: 1.</summary>
    public int RootReturnMap { get; private set; }

    /// <summary>
    /// Occurrences of a numeric child inside a life record — <c>life/&lt;i&gt;/0
    /// = -1</c> and friends. Measured over the whole client: <b>438</b>.
    /// </summary>
    /// <remarks>
    /// The data model document says 2,954, which is the count of the
    /// <c>life/&lt;i&gt;/&lt;j&gt;</c> <i>path shape</i> — and its own mixed-type
    /// table splits that figure as 2,516 SubProperty + 438 Int. The 2,516 are the
    /// category containers of the <c>life/isCategory</c> shape, which are not
    /// stray children of a life record at all. 438 is the number an editor cares
    /// about.
    /// </remarks>
    public int NumericChildrenInLife { get; private set; }

    /// <summary>Nodes whose name has a trailing space anywhere in the tree.</summary>
    public int TrailingSpaceNames { get; private set; }

    /// <summary>The distinct trailing-space key names seen, with their paths trimmed away.</summary>
    public IReadOnlyCollection<string> TrailingSpaceKeys => _trailingSpaceKeys;

    /// <summary>Nodes read of each <see cref="WzPropertyType"/>.</summary>
    public IReadOnlyDictionary<WzPropertyType, long> TypeHistogram => _types;

    /// <summary>Empty top-level containers, by name and count.</summary>
    public IReadOnlyDictionary<string, int> EmptyContainers => _emptyContainers;

    /// <summary>Every top-level node kind seen, by name and how many maps carry it.</summary>
    public IReadOnlyDictionary<string, int> TopLevelKinds => _topLevelKinds;

    /// <summary>Top-level kinds not in the v232 census. Expected: none.</summary>
    public IReadOnlyCollection<string> UnknownKinds => _unknownKinds;

    /// <summary>
    /// The first map image each top-level kind was seen on. A kind nobody has a
    /// policy for is not actionable until someone can go and look at one, and
    /// "somewhere in 17,442 images" is not a place to look.
    /// </summary>
    public IReadOnlyDictionary<string, string> FirstMapWithKind => _firstMapWithKind;

    /// <summary>
    /// A one-line sketch of each kind's children the first time it was seen —
    /// enough to tell a ninth layer from a coincidence of naming.
    /// </summary>
    public IReadOnlyDictionary<string, string> ShapeOfKind => _shapeOfKind;

    internal void Observe(MapDocument document)
    {
        if (document.Nodes.Count > 0
            && !string.Equals(document.Nodes[0].Name, "info", StringComparison.Ordinal))
        {
            MapsNotStartingWithInfo++;
        }

        bool sawLayerBack = false;
        bool sawLayerNumeric = false;
        bool sawNestedBack = false;
        bool sawL3 = false;

        foreach (WzNode node in document.Nodes)
        {
            Bump(_topLevelKinds, node.Name);

            if (!_firstMapWithKind.ContainsKey(node.Name))
            {
                _firstMapWithKind[node.Name] = document.ImageName;
                _shapeOfKind[node.Name] =
                    $"{node.Type}[{string.Join(" ", node.Children.Take(8).Select(c => c.Name))}"
                    + (node.Children.Count > 8 ? " ..." : "") + "]";
            }

            if (MapNodeKinds.PolicyOf(node.Name) == MapNodePolicy.Unknown)
                _unknownKinds.Add(node.Name);

            if (node.IsContainer && node.Children.Count == 0)
                Bump(_emptyContainers, node.Name);

            if (string.Equals(node.Name, "returnMap", StringComparison.Ordinal))
                RootReturnMap++;

            if (string.Equals(node.Name, "info", StringComparison.Ordinal))
            {
                foreach (WzNode key in node.Children)
                {
                    if (IsAllDigits(key.Name))
                    {
                        NumericInfoKeys++;
                        break;
                    }
                }
            }

            if (string.Equals(node.Name, "back", StringComparison.Ordinal))
            {
                foreach (WzNode entry in node.Children)
                {
                    foreach (WzNode inner in entry.Children)
                    {
                        if (inner.IsContainer && IsAllDigits(inner.Name))
                        {
                            sawNestedBack = true;
                            break;
                        }
                    }
                }
            }

            if (MapNodeKinds.IsLayerName(node.Name))
            {
                foreach (WzNode child in node.Children)
                {
                    if (string.Equals(child.Name, "back", StringComparison.Ordinal))
                        sawLayerBack = true;
                    else if (IsAllDigits(child.Name))
                        sawLayerNumeric = true;
                    else if (string.Equals(child.Name, "obj", StringComparison.Ordinal))
                    {
                        foreach (WzNode entry in child.Children)
                        {
                            if (entry.Child("l3") != null)
                                sawL3 = true;
                        }
                    }
                }
            }

            Walk(node);
        }

        foreach (WzNode node in document.Nodes)
        {
            if (MapNodeKinds.IsLayerName(node.Name)
                && Array.IndexOf(MapNodeKinds.UsualLayerNames, node.Name) < 0)
            {
                MapsWithAnUnusualLayer++;
                break;
            }
        }

        if (sawLayerBack) LayerLevelBackLists++;
        if (sawLayerNumeric) LayerLevelNumericChildren++;
        if (sawNestedBack) NestedBackEntries++;
        if (sawL3) ObjectsWithL3++;

        if (document.LifeIsCategorised)
        {
            LifeCategorised++;
            CategorisedSpawns += document.Life.Count;
        }

        foreach (MapLife life in document.Life)
        {
            foreach (WzNode child in life.Node.Children)
            {
                if (IsAllDigits(child.Name))
                    NumericChildrenInLife++;
            }
        }
    }

    private void Walk(WzNode node)
    {
        Nodes++;
        _types.TryGetValue(node.Type, out long count);
        _types[node.Type] = count + 1;

        if (node.Name != null && node.Name.Length > 0 && node.Name[^1] == ' ')
        {
            TrailingSpaceNames++;
            _trailingSpaceKeys.Add(node.Name);
        }

        foreach (WzNode child in node.Children)
            Walk(child);
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int count);
        counts[key] = count + 1;
    }

    private static bool IsAllDigits(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (char c in name)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }

    /// <summary>A readable account of what the run met.</summary>
    public string Summarise()
    {
        System.Text.StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"  {Nodes:N0} nodes read.");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  types: {string.Join(", ", _types.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value:N0}"))}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  anomalies seen — life/isCategory {LifeCategorised} maps / {CategorisedSpawns:N0} spawns; "
            + $"obj/l3 {ObjectsWithL3}; layer back {LayerLevelBackLists}; layer numeric {LayerLevelNumericChildren}; "
            + $"nested back {NestedBackEntries}; numeric info key {NumericInfoKeys}; root returnMap {RootReturnMap}; "
            + $"layer outside 0-7 {MapsWithAnUnusualLayer}; "
            + $"numeric child in life {NumericChildrenInLife:N0}; trailing-space names {TrailingSpaceNames} "
            + $"({string.Join(", ", _trailingSpaceKeys.Select(k => $"'{k}'"))})");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  not starting with info: {MapsNotStartingWithInfo}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  empty containers: {string.Join(", ", _emptyContainers.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value:N0}"))}");
        string unknown = _unknownKinds.Count > 0
            ? "NOT IN THE CENSUS: " + string.Join(", ", _unknownKinds.Select(
                k => $"'{k}' on {_topLevelKinds[k]} map(s), first {_firstMapWithKind[k]} {_shapeOfKind[k]}"))
            : "all in the census";
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  top-level kinds: {_topLevelKinds.Count}; {unknown}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  every top-level kind: {string.Join(", ", _topLevelKinds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value:N0}"))}");
        return text.ToString();
    }
}
