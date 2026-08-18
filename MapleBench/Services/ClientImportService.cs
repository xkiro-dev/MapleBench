using System.Diagnostics;
using System.Text;
using MapleLib.WzLib;
using MapleLib.WzLib.MSFile;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// One archive as it exists in a split <c>Data\</c> client, before anything is
/// converted.
/// </summary>
public sealed class SplitArchiveDto
{
    public string Name { get; set; } = "";
    /// <summary>
    /// Logical archive family. For a classic client, <c>Map001.wz</c> belongs
    /// to <c>Map</c>; for a split client this is the same as <see cref="Name"/>.
    /// </summary>
    public string Family { get; set; } = "";
    /// <summary><c>split</c> for a Data directory, <c>classic</c> for one .wz file.</summary>
    public string Format { get; set; } = "split";
    /// <summary>Absolute path to the archive's own directory, e.g. "...\Data\String".</summary>
    public string Path { get; set; } = "";
    /// <summary>How many <c>Name_000.wz</c> parts, from LastWzIndex + 1. Zero is legal.</summary>
    public int Parts { get; set; }
    /// <summary>Nested archives found as sub-directories, "_Canvas" included.</summary>
    public List<string> SubArchives { get; set; } = new();
    /// <summary>How many <c>Name_00000.ms</c> packs in Data\Packs carry this archive's images.</summary>
    public int Packs { get; set; }
    /// <summary>Bytes of every file this archive would read, packs and nested archives included.</summary>
    public long SourceBytes { get; set; }
    /// <summary>False when this importer knows it cannot do the job — see <see cref="Reason"/>.</summary>
    public bool Supported { get; set; } = true;
    public string? Reason { get; set; }

    /// <summary>
    /// False when this archive is too large to exist as one classic .wz, whatever
    /// tool builds it. See <see cref="ClientImportService.WzAddressableCeiling"/>.
    /// Opening it read-only is unaffected — that is the point of the distinction.
    /// </summary>
    public bool Convertible { get; set; } = true;
    /// <summary>Why <see cref="Convertible"/> is false, in the user's words.</summary>
    public string? ConvertReason { get; set; }

    /// <summary>
    /// The same thing in four words, for somewhere a paragraph will not fit.
    ///
    /// <see cref="ConvertReason"/> is ninety words because the full explanation is
    /// worth reading once; it is the wrong text for a table cell or a tooltip, and
    /// a UI that needs the short form should not have to invent it and then drift
    /// from what the server actually decided.
    /// </summary>
    public string? ConvertSummary { get; set; }
}

/// <summary>What a folder turned out to be.</summary>
public sealed class ClientLayoutDto
{
    /// <summary>"split", "classic", or "none".</summary>
    public string Kind { get; set; } = "none";
    public string Path { get; set; } = "";
    /// <summary>The <c>Data\</c> directory of a split client; null otherwise.</summary>
    public string? DataPath { get; set; }
    public string Summary { get; set; } = "";
    public List<SplitArchiveDto> Archives { get; set; } = new();
    /// <summary>
    /// Legacy filename list for callers that only need confirmation. Import
    /// callers use <see cref="Archives"/> for both layouts.
    /// </summary>
    public List<string> ClassicArchives { get; set; } = new();
}

public sealed class ImportRequest
{
    /// <summary>The client folder or its Data directory. Either is accepted.</summary>
    public string SourceFolder { get; set; } = "";
    /// <summary>Archive name as reported by detection — "String", "Mob".</summary>
    public string Archive { get; set; } = "";
    /// <summary>Where the monolithic .wz goes. Defaults to <c>&lt;OutputFolder&gt;\&lt;Archive&gt;.wz</c>.</summary>
    public string? TargetPath { get; set; }
    /// <summary>Used when TargetPath is absent. Required in that case.</summary>
    public string? OutputFolder { get; set; }
    /// <summary>
    /// Write the 64-bit header shape (no 2-byte encryption version), which is
    /// what the classic client in this user's Steam depot uses. Default true.
    /// </summary>
    public bool? Save64Bit { get; set; }
    /// <summary>Permit replacing a file that is already at the target path.</summary>
    public bool Overwrite { get; set; }
    /// <summary>How many images to open on both sides and compare property-by-property.</summary>
    public int? SampleSize { get; set; }
}

/// <summary>
/// A split archive merged into one in-memory <see cref="WzFile"/>, together with
/// the source files whose bytes it is still reading through.
///
/// This is the shape the conversion has always built and then immediately
/// written to disk. Handing it out instead is what lets the same merge be
/// *opened* rather than converted — a 14.6 GB Skill becomes a tree you can
/// browse, search and port out of in a few seconds, with nothing written
/// anywhere. The conversion is still the right answer when someone genuinely
/// wants the whole archive as a file; it is the wrong answer, by four orders of
/// magnitude, when they want one boss out of it.
///
/// <b>Ownership is the whole contract.</b> Every image in <see cref="File"/> is
/// a re-parented object that reads its bytes through a reader owned by one of
/// <see cref="Sources"/> or <see cref="Packs"/>. Dispose those and the tree is
/// still there, still enumerable, and every image in it throws on parse. So the
/// caller either keeps this object alive for as long as it uses the tree, or it
/// has nothing. <see cref="Dispose"/> tears the two down in the only order that
/// does not double-free.
/// </summary>
public sealed class AssembledArchive : IDisposable
{
    /// <summary>The merged archive. Its root directory is named "&lt;Archive&gt;.wz".</summary>
    public required WzFile File { get; init; }
    public required string ArchiveName { get; init; }
    /// <summary>The split archive's own directory, e.g. "...\Data\Mob".</summary>
    public required string ArchiveDirectory { get; init; }
    public long SourceBytes { get; init; }
    public int Images { get; init; }
    public int Directories { get; init; }
    /// <summary>Images that came out of .ms packs, so have no source block to copy.</summary>
    public int ImagesReserialised { get; init; }
    public int SourceFilesRead { get; init; }
    public List<string> Warnings { get; init; } = new();

    internal List<WzFile> Sources { get; init; } = new();
    internal List<WzMsFile> Packs { get; init; } = new();

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The merged tree first, then the files it was reading through.
        //
        // Every image of one source .wz shares that file's single reader, so
        // disposing the tree closes those readers on the way past; disposing the
        // sources afterwards then finds them already closed, which is harmless.
        // The other order is not: WzFile.Dispose walks its own directory tree,
        // and the images that used to be in it now belong to the merged file, so
        // disposing a source first would dispose objects the merged tree still
        // lists and every later read would hit a disposed reader instead of a
        // closed one.
        try { File.Dispose(); } catch { /* a tree we cannot tear down is not worth failing a close over */ }
        foreach (WzFile source in Sources)
        {
            try { source.Dispose(); } catch { }
        }
        foreach (WzMsFile pack in Packs)
        {
            try { pack.Dispose(); } catch { }
        }
    }
}

public sealed class ImportResult
{
    public string Archive { get; set; } = "";
    public string WrittenTo { get; set; } = "";
    public string? BackupPath { get; set; }
    public long Bytes { get; set; }
    public long SourceBytes { get; set; }
    public int Images { get; set; }
    public int Directories { get; set; }
    /// <summary>Images that came out of .ms packs, so were re-serialised rather than copied.</summary>
    public int ImagesReserialised { get; set; }
    public int SourceFilesRead { get; set; }
    public double Seconds { get; set; }
    /// <summary>How many images verification opened on both sides and compared in full.</summary>
    public int Sampled { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Converts a modern split <c>Data\</c> client into the classic one-file-per-archive
/// layout.
///
/// The two layouts differ in packaging, not in content. A split client stores
/// <c>Data\String\String_000.wz</c>, <c>Data\Mob\_Canvas\_Canvas_003.wz</c> and
/// <c>Data\Packs\Mob_00002.ms</c> where a classic client stores one
/// <c>String.wz</c> and one <c>Mob.wz</c>; the WZ images inside are the same
/// bytes under the same key. So the conversion is a merge of directory trees
/// followed by one write, not a re-encode — and every image that came from a
/// .wz part is copied through byte for byte, which is both faster and safer
/// than round-tripping it.
///
/// Measured against C:\Nexon\Library\maplestory\appdata on this machine:
/// String is 1 part and 47 images; Reactor is 1 part plus a 158 MB _Canvas
/// sub-archive; Mob is 0 parts, 4 .ms packs (7,097 images in the first alone)
/// and 8 _Canvas parts totalling 13 GB.
///
/// Three facts drive the design, all of them established by reading the real
/// client rather than assumed:
///
///  1. <b>The stub <c>&lt;Name&gt;.wz</c> is not data.</b> It parses to a list of
///     zero-size directory entries and no images — Mob.wz is 192 bytes naming
///     its six sub-directories. It is the client's routing table. Merging it
///     would add empty directories that shadow the real ones, so it is skipped
///     and the sub-archives are discovered from the filesystem instead. Base.wz
///     is the proof that trusting it would be wrong: its 376 bytes decode to
///     entries naming the other archives, which are not its children at all.
///
///  2. <b>Canvas links already point where the merge puts them.</b> A pack image
///     carries <c>_outlink = "Mob/_Canvas/0100007.img/info/thumbnail"</c>, which
///     is a path from the archive root through a <c>_Canvas</c> directory. Keeping
///     <c>_Canvas</c> as a sub-directory of the output archive therefore leaves
///     every link resolving exactly as it did — and resolving inside one file
///     rather than across two. No link is rewritten, so no link can be rewritten
///     wrongly.
///
///  3. <b>Image bytes are position-independent.</b> The version hash is consumed
///     only by <c>WzDirectory.ParseDirectory</c> via <c>ReadOffset</c>; nothing
///     inside an image body reads it, and property string offsets are relative to
///     the image's own start. That is what makes the byte-copy legal across files
///     of different header shapes, and it is why the output can be written in the
///     64-bit shape the classic client uses while the source parts carry a
///     version header.
/// </summary>
public sealed class ClientImportService
{
    /// <summary>
    /// The patch version stamped on the output.
    ///
    /// Not cosmetic and not free to change: <c>WzFile.ParseMainWzDirectory</c>
    /// only brute-forces 777-786 for a file with no version header, and
    /// <c>CheckAndGetVersionHash</c> returns the hash unvalidated when the header
    /// value is 777. Writing any other number produces a file this library — and
    /// the client — would have to guess at. The user's classic client reports
    /// version 777 for every archive, which is the same statement from the other
    /// direction.
    /// </summary>
    private const short ClassicGameVersion = 777;

    /// <summary>
    /// How many images verification opens on both sides and compares property by
    /// property, when the caller does not say.
    ///
    /// Counts and names are cheap and prove the table of contents; they prove
    /// nothing about content, which is the failure this app cares about most.
    /// Sixty-four spread evenly across the archive costs well under a second for
    /// String (47 images, so all of them) and is bounded for a 25,000-image Mob.
    /// </summary>
    private const int DefaultSampleSize = 64;

    /// <summary>
    /// Ceiling on how much text one image's digest contributes to the comparison.
    ///
    /// A digest is only useful if a mismatch can be shown to a human. An image
    /// with 40,000 leaf properties would produce a diff nobody can read and a
    /// string nobody should hold, so the walk stops and the digest records that
    /// it stopped — identically on both sides, so the comparison stays valid.
    /// </summary>
    private const int MaxDigestLeaves = 400;

    /// <summary>
    /// The largest a WZ archive can be, in bytes, and the reason the biggest
    /// archives in a modern client can never become one classic file.
    ///
    /// Every image and every sub-directory in a WZ archive is found through a
    /// four-byte offset: <c>WzBinaryWriter.WriteOffset</c> encodes
    /// <c>(uint)value</c> and writes 32 bits, and <c>WzBinaryReader.ReadOffset</c>
    /// decodes 32 bits back. Nothing above 4,294,967,295 has an address, so an
    /// archive past that point cannot describe its own contents. This is the
    /// format, not this library and not this app.
    ///
    /// It was found by doing it. Converting Data\Effect produced a 7,184,260,534
    /// byte file whose header declared 2,889,293,238 — short by exactly
    /// 4,294,967,296, one full wrap — and every image in the second half read as
    /// unparseable because its recorded offset pointed 4 GB earlier. The check
    /// below exists so the next person is told in a second instead of finding out
    /// in eighty-four, or in forty minutes for Skill.
    ///
    /// The user's own classic client is the same statement from the other side:
    /// its largest archive is Map.wz at 1.68 GB, and where the content does not
    /// fit the client ships Map.wz, Map001.wz, Map002.wz and Map2.wz rather than
    /// one big file.
    /// </summary>
    public const long WzAddressableCeiling = uint.MaxValue;

    /// <summary>
    /// How close to <see cref="WzAddressableCeiling"/> an archive may come before
    /// this refuses it.
    ///
    /// Ninety-five percent, because the estimate is the source size and the output
    /// is not exactly it — .ms-sourced images are re-serialised and can grow. An
    /// archive at 4.2 GB of source that came out at 4.1 GB would be a file the
    /// game cannot load, produced by a run that reported success, and the only
    /// warning would have been that it was close.
    /// </summary>
    private const double CeilingMargin = 0.95;

    /// <summary>
    /// The archives this importer refuses outright, and why.
    ///
    /// Nothing here is a technical limit of the merge; it is a limit of what was
    /// tested. Left empty deliberately — see <see cref="ArchiveNote"/> for the
    /// ones that carry a caution instead of a refusal.
    /// </summary>
    private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<ClientImportService> _log;

    public ClientImportService(ILogger<ClientImportService> log) => _log = log;

    #region Detection

    /// <summary>
    /// Says what a folder is: a classic client, a split Data\ client, or neither,
    /// and what archives it holds.
    ///
    /// Accepts either the client root or the <c>Data</c> directory itself,
    /// because a user who has just been looking at
    /// <c>...\appdata\Data\String</c> in Explorer will paste one of the two and
    /// should not have to know which we wanted.
    /// </summary>
    public ClientLayoutDto Detect(string folder)
    {
        string full = ResolveFolder(folder);
        Stopwatch clock = Stopwatch.StartNew();
        ClientLayoutDto layout = DetectResolved(full);
        _log.LogInformation(
            "Detected {Kind} source at {Folder} in {Ms} ms",
            layout.Kind, full, clock.ElapsedMilliseconds);
        return layout;
    }

    private ClientLayoutDto DetectResolved(string full)
    {

        // A split archive directory is one holding a .ini with LastWzIndex. That
        // is the only marker the client itself uses, so it is the only one worth
        // testing -- a name list would go stale the first time Nexon adds an
        // archive.
        string? data = null;
        if (Directory.Exists(Path.Combine(full, "Data")) && HasSplitArchives(Path.Combine(full, "Data")))
            data = Path.Combine(full, "Data");
        else if (HasSplitArchives(full))
            data = full;

        if (data != null)
        {
            List<SplitArchiveDto> archives = EnumerateArchives(data);
            return new ClientLayoutDto
            {
                Kind = "split",
                Path = full,
                DataPath = data,
                Archives = archives,
                Summary = $"Split client — {archives.Count} archive{(archives.Count == 1 ? "" : "s")} " +
                          $"under {data}, {Bytes(archives.Sum(a => a.SourceBytes))} in total.",
            };
        }

        List<string> classicPaths = SafeFiles(full)
            .Where(f => Path.GetExtension(f).Equals(".wz", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<string> classic = classicPaths
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<SplitArchiveDto> classicArchives = classicPaths
            .Select(path =>
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                long sourceBytes = 0;
                try { sourceBytes = new FileInfo(path).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                return new SplitArchiveDto
                {
                    Name = stem,
                    Family = ArchiveFamilyService.FamilyOf(stem),
                    Format = "classic",
                    Path = path,
                    Parts = 1,
                    SourceBytes = sourceBytes,
                    Supported = true,
                    // Conversion describes split -> classic and is not an
                    // operation a classic source needs. Opening and porting are
                    // fully supported; this flag is deliberately unrelated.
                    Convertible = false,
                };
            })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // One ordinary archive is already a useful import source. Requiring a
        // whole client here made the unified importer reject the exact case it
        // exists for: somebody was handed Character.wz and wants one equip from
        // it. Companion archives can still be opened later from the same folder.
        if (classic.Count > 0)
        {
            return new ClientLayoutDto
            {
                Kind = "classic",
                Path = full,
                Archives = classicArchives,
                ClassicArchives = classic,
                Summary = classic.Count == 1
                    ? $"Classic WZ source — {classic[0]} in {full}."
                    : $"Classic client — {classic.Count} .wz archives in {full}.",
            };
        }

        return new ClientLayoutDto
        {
            Kind = "none",
            Path = full,
            ClassicArchives = classic,
            Summary = classic.Count > 0
                ? $"{full} holds {classic.Count} .wz file{(classic.Count == 1 ? "" : "s")} but does not look " +
                  "like a client folder. Point this at the folder that contains Data\\, or at the client itself."
                : $"{full} is neither a split client (no Data\\ directory with .ini archive folders) nor a " +
                  "classic one (no .wz files).",
        };
    }

    private static bool HasSplitArchives(string directory) =>
        SafeDirectories(directory).Any(d => ReadLastWzIndex(d) != null);

    private List<SplitArchiveDto> EnumerateArchives(string dataDirectory)
    {
        Dictionary<string, int> packs = ReadPacksIndex(dataDirectory);
        List<SplitArchiveDto> archives = new();

        foreach (string dir in SafeDirectories(dataDirectory))
        {
            string name = Path.GetFileName(dir);
            // Packs is a shared pool keyed by archive name, not an archive of its
            // own; it is folded into whichever archive its files belong to.
            if (name.Equals("Packs", StringComparison.OrdinalIgnoreCase))
                continue;

            int? lastIndex = ReadLastWzIndex(dir);
            if (lastIndex == null)
                continue;

            SplitArchiveDto archive = new()
            {
                Name = name,
                Family = name,
                Format = "split",
                Path = dir,
                Parts = lastIndex.Value + 1,
                SubArchives = SafeDirectories(dir)
                    .Where(d => ReadLastWzIndex(d) != null)
                    .Select(Path.GetFileName)
                    .Where(n => n != null).Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Packs = packs.TryGetValue(name, out int packLast) ? packLast + 1 : 0,
            };

            archive.SourceBytes = MeasureArchive(dir) + MeasurePacks(dataDirectory, name, archive.Packs);
            archive.Supported = !Unsupported.Contains(name);
            archive.ConvertReason = TooBigToConvert(archive.SourceBytes);
            archive.Convertible = archive.Supported && archive.ConvertReason == null;
            archive.ConvertSummary = archive.Convertible ? null : "too big for one .wz";

            // The free-space caution is suppressed once converting is impossible.
            // "About 13 GB. That much free space is needed on the target drive" is
            // true and useless next to "this can never be a .wz file" -- it reads
            // as though clearing some space would fix it.
            archive.Reason = !archive.Supported
                ? "Not supported by this importer yet."
                : archive.Convertible ? ArchiveNote(archive) : null;
            archives.Add(archive);
        }

        return archives.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A caution the user should read before starting, or null.
    ///
    /// The only one that exists is size: the output is written whole before it is
    /// verified, so a 13 GB Mob needs 13 GB free on the target volume plus room
    /// for the images that get re-serialised. Saying so up front is cheaper than
    /// a disk-full failure ninety minutes in.
    /// </summary>
    /// <summary>
    /// Why this archive cannot become one classic .wz, or null when it can.
    ///
    /// Measured against the source size, which the conversions run on this client
    /// show to be the output size within a fraction of a percent — Effect's
    /// 7,184,266,020 source bytes produced 7,184,260,534.
    /// </summary>
    private static string? TooBigToConvert(long sourceBytes)
    {
        if (sourceBytes < WzAddressableCeiling * CeilingMargin)
            return null;

        return $"{Bytes(sourceBytes)} cannot be one .wz file. Every image in a WZ archive is located by a " +
               "four-byte offset, so nothing past 4 GB has an address — the archive would be written in full " +
               "and then be unable to find its own second half. This is the WZ format, not a limit of this " +
               "app, and it is why the classic client ships Map.wz, Map001.wz and Map002.wz instead of one " +
               "file. Open it for reference instead: that merges the same tree in memory, takes seconds, " +
               "needs no disk, and you can port out of it.";
    }

    private static string? ArchiveNote(SplitArchiveDto archive)
    {
        const long large = 2L * 1024 * 1024 * 1024;
        if (archive.SourceBytes >= large)
        {
            return $"About {Bytes(archive.SourceBytes)}. The output is written in full before it is verified, " +
                   "so that much free space is needed on the target drive, and the import will take a while.";
        }
        return null;
    }

    /// <summary>
    /// Reads <c>LastWzIndex|N</c> out of the single .ini in an archive directory,
    /// or null when the directory is not a split archive.
    ///
    /// Returns -1 for an archive with no .wz parts at all, which is a real and
    /// common state: Mob and Skill both say <c>LastWzIndex|-1</c> because all of
    /// their image data lives in .ms packs. Treating that as "not an archive"
    /// would have silently hidden the two biggest archives in the client.
    /// </summary>
    private static int? ReadLastWzIndex(string directory)
    {
        string expected = Path.Combine(directory, Path.GetFileName(directory) + ".ini");
        string? ini = File.Exists(expected)
            ? expected
            : SafeFiles(directory).FirstOrDefault(f =>
                Path.GetExtension(f).Equals(".ini", StringComparison.OrdinalIgnoreCase));

        if (ini == null)
            return null;

        try
        {
            foreach (string line in File.ReadLines(ini))
            {
                string[] split = line.Split('|');
                if (split.Length >= 2 && split[0].Trim().Equals("LastWzIndex", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(split[1].Trim(), out int index))
                    return index;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        return null;
    }

    /// <summary>
    /// Reads <c>Data\Packs\Packs.ini</c>, whose lines are "&lt;Archive&gt;|&lt;last index&gt;"
    /// — "Mob|3" means Mob_00000.ms through Mob_00003.ms.
    /// </summary>
    private static Dictionary<string, int> ReadPacksIndex(string dataDirectory)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        string ini = Path.Combine(dataDirectory, "Packs", "Packs.ini");
        if (!File.Exists(ini))
            return result;

        try
        {
            foreach (string line in File.ReadLines(ini))
            {
                string[] split = line.Split('|');
                if (split.Length >= 2 && int.TryParse(split[1].Trim(), out int last))
                    result[split[0].Trim()] = last;
            }
        }
        catch (IOException) { /* a Packs.ini we cannot read means no packs, not a failed scan */ }
        catch (UnauthorizedAccessException) { }

        return result;
    }

    #endregion

    #region Assemble

    /// <summary>
    /// Merges one split archive into an in-memory <see cref="WzFile"/> and stops
    /// there, without writing anything.
    ///
    /// This is the first half of <see cref="Import"/>, lifted out so that opening
    /// a split archive read-only and converting it to a file are the same code up
    /// to the point where they differ. They have to be: an open that assembled the
    /// tree differently from the conversion would let a user port an entry out of
    /// something the conversion would never have produced, and neither result
    /// would be wrong in a way anyone could see.
    ///
    /// The checks that run here are the ones about the merge itself — zero images,
    /// duplicate names, images that would serialise hollow. The ones about the
    /// destination stay in <see cref="Import"/>, because there is no destination.
    /// </summary>
    /// <param name="report">(stage, done, total). Optional.</param>
    /// <param name="cancel">
    /// Checked between source files. A 14.6 GB Skill opens nine packs and nine
    /// hundred megabytes of buffers, and a user who started that by mistake should
    /// not have to wait for it to finish before they can do anything else.
    /// </param>
    public AssembledArchive Assemble(
        string sourceFolder, string archiveName,
        Action<string, long, long>? report = null, CancellationToken cancel = default)
        => Assemble(Detect(sourceFolder), archiveName, report, cancel);

    /// <summary>
    /// Assembles from a layout already detected by this operation. Passing the
    /// snapshot through the call chain avoids recursively measuring the same
    /// client again while keeping one internally consistent view of its paths,
    /// sizes and archive indexes.
    /// </summary>
    public AssembledArchive Assemble(
        ClientLayoutDto layout, string archiveName,
        Action<string, long, long>? report = null, CancellationToken cancel = default)
    {
        report ??= static (_, _, _) => { };

        if (layout.Kind != "split" || layout.DataPath == null)
            throw new InvalidOperationException(layout.Summary);

        SplitArchiveDto? archive = layout.Archives.FirstOrDefault(a =>
            a.Name.Equals(archiveName, StringComparison.OrdinalIgnoreCase));
        if (archive == null)
        {
            throw new InvalidOperationException(
                $"'{archiveName}' is not an archive in {layout.DataPath}. Available: " +
                string.Join(", ", layout.Archives.Select(a => a.Name)));
        }
        if (!archive.Supported)
            throw new InvalidOperationException($"{archive.Name}: {archive.Reason}");

        List<WzFile> sources = new();
        List<WzMsFile> packs = new();
        List<string> warnings = new();
        short gameVersion = -1;

        WzFile output = new(ClassicGameVersion, WzMapleVersion.BMS)
        {
            Name = archive.Name + ".wz",
        };
        output.WzDirectory.Name = archive.Name + ".wz";

        try
        {
            int sourceFileCount = archive.Parts + archive.Packs + CountNestedFiles(archive.Path);
            AssembleInto(output, output.WzDirectory, archive.Path, layout.DataPath, archive.Name,
                         sources, packs, warnings, report, sourceFileCount, ref gameVersion, cancel);

            report("Checking what was assembled", sourceFileCount, sourceFileCount);
            (int images, int directories, int reserialised) = Census(output.WzDirectory);
            if (images == 0)
            {
                throw new InvalidOperationException(
                    $"{archive.Name} assembled to zero images. Its parts, packs and sub-archives were all read " +
                    "and none of them contained an image — that is a bug in this importer or a client layout it " +
                    "does not understand.");
            }

            List<string> hollow = FindHollowImages(output.WzDirectory);
            if (hollow.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{archive.Name} contains images that carry neither content nor a source block to read it " +
                    "from — they would appear in the archive's index and be empty:\n" +
                    string.Join("\n", hollow.Select(h => "  - " + h)));
            }

            return new AssembledArchive
            {
                File = output,
                ArchiveName = archive.Name,
                ArchiveDirectory = archive.Path,
                SourceBytes = archive.SourceBytes,
                Images = images,
                Directories = directories,
                ImagesReserialised = reserialised,
                SourceFilesRead = sources.Count + packs.Count,
                Warnings = warnings,
                Sources = sources,
                Packs = packs,
            };
        }
        catch
        {
            // Ownership never transferred, so nothing else will close these. The
            // merged tree is left to the collector -- it holds no handles of its
            // own -- but the sources hold one file handle each and a failed open
            // of Sound would otherwise leak sixty-six of them.
            foreach (WzFile source in sources)
            {
                try { source.Dispose(); } catch { }
            }
            foreach (WzMsFile pack in packs)
            {
                try { pack.Dispose(); } catch { }
            }
            throw;
        }
    }

    #endregion

    #region Import

    /// <summary>
    /// Merges one split archive into a single classic .wz and proves the result
    /// before putting it anywhere the user will find it.
    ///
    /// The order is deliberate and matches <see cref="WzSaveService"/>: assemble,
    /// refuse anything that cannot be written, write to a scratch file, reopen
    /// and compare, and only then move it into place. Nothing that fails leaves
    /// a file at the destination, and nothing that succeeds does so on the
    /// strength of the write having not thrown.
    /// </summary>
    /// <param name="report">
    /// Called with (stage, done, total) as the import moves. Optional because the
    /// tests and any future scripted caller have nothing to report to, and a
    /// mandatory sink would mean every one of them inventing a no-op.
    /// </param>
    /// <param name="session">
    /// Optional. When given, archives already open with unsaved work are counted
    /// against the free space this conversion may use. See the precheck below.
    /// </param>
    public ImportResult Import(ImportRequest request, Action<string, long, long>? report = null,
                               CancellationToken cancel = default, WzSessionService? session = null)
    {
        Stopwatch clock = Stopwatch.StartNew();
        report ??= static (_, _, _) => { };

        ClientLayoutDto layout = Detect(request.SourceFolder);
        if (layout.Kind != "split" || layout.DataPath == null)
            throw new InvalidOperationException(layout.Summary);

        SplitArchiveDto? archive = layout.Archives.FirstOrDefault(a =>
            a.Name.Equals(request.Archive, StringComparison.OrdinalIgnoreCase));
        if (archive == null)
        {
            throw new InvalidOperationException(
                $"'{request.Archive}' is not an archive in {layout.DataPath}. Available: " +
                string.Join(", ", layout.Archives.Select(a => a.Name)));
        }
        if (!archive.Supported)
            throw new InvalidOperationException($"{archive.Name}: {archive.Reason}");

        // Before the destination is resolved, before a byte is read, and before
        // any of it is the user's time. See WzAddressableCeiling.
        if (!archive.Convertible)
            throw new InvalidOperationException($"{archive.Name}: {archive.ConvertReason}");

        string destination = ResolveDestination(request, archive.Name);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The output path has no directory.");
        Directory.CreateDirectory(directory);

        // Refuse before reading 13 GB, not after. Overwriting is allowed, but only
        // when asked for -- the destination is a client folder and the file being
        // replaced is the one the game loads.
        if (File.Exists(destination) && !request.Overwrite)
        {
            throw new InvalidOperationException(
                $"'{destination}' already exists. Tick 'replace the existing file' if that is what you want; " +
                "the current file is kept as a timestamped .bak either way.");
        }

        // Checked before anything is read, because the alternative is finding out
        // ninety minutes into a 13 GB conversion. Nothing is lost when it happens
        // -- SaveToDisk throws, the scratch file is deleted and the destination is
        // untouched -- but the time is, and the time is the expensive part.
        //
        // The source size is the estimate: the output is the same images under a
        // different wrapper, so it lands within a fraction of a percent (measured:
        // Etc's 1,534,945,770 source bytes produced 1,534,939,246). Ten percent of
        // headroom covers the .ms archives, whose images are re-serialised rather
        // than copied and so can differ by more.
        long needed = archive.SourceBytes + archive.SourceBytes / 10;

        // Plus whatever the session is already holding, when the target volume is
        // the one those archives would be saved back to.
        //
        // The estimate used to be the source size alone, which is the space the
        // conversion needs on an idle app. It is not the space it needs on the app
        // as people use it: a dirty archive open in the session is a save that has
        // not happened yet, and WzSaveService writes a full scratch copy beside the
        // target before it swaps. Convert a 9 GB Map with a 1.6 GB Character
        // waiting to be saved and the precheck says yes to a drive that cannot
        // hold both -- and the one that fails is the save, which is the one with
        // the user's work in it.
        long alreadyClaimed = ReservedBySession(session, destination);
        needed += alreadyClaimed;

        try
        {
            DriveInfo drive = new(Path.GetPathRoot(destination) ?? directory);
            if (drive.IsReady && drive.AvailableFreeSpace < needed)
            {
                string claimed = alreadyClaimed > 0
                    ? $" That includes {Bytes(alreadyClaimed)} kept clear for archives already open with " +
                      "unsaved changes, which need room to be written."
                    : "";
                throw new InvalidOperationException(
                    $"{archive.Name} needs about {Bytes(needed)} on {drive.Name} and only " +
                    $"{Bytes(drive.AvailableFreeSpace)} is free. The converted archive is written in full " +
                    $"before it is verified, so there is no way to do this in less space.{claimed}");
            }
        }
        catch (ArgumentException) { /* an unusual path we cannot measure is not a reason to refuse */ }
        catch (IOException) { }

        bool save64Bit = request.Save64Bit ?? true;

        // Assembled by the same call the read-only open uses, so the tree that
        // gets written is the tree a user would have browsed. See Assemble.
        AssembledArchive assembled = Assemble(layout, archive.Name, report, cancel);
        WzFile output = assembled.File;
        List<string> warnings = assembled.Warnings;
        int images = assembled.Images;

        try
        {
            Inventory expected = TakeInventory(output.WzDirectory);

            // A directory whose contents pass int.MaxValue cannot have its size
            // recorded in a WZ entry -- the field is a compressed int. The archive
            // is written correctly regardless (the size is saturated, and nothing
            // reading a WZ navigates by it), and verification below reopens it and
            // proves that. What cannot be proven from here is what the game does
            // with a directory bigger than any it ships: measured, the largest
            // single directory in the user's own classic client is well under
            // 2 GB, and the client splits Map.wz into Map/Map001/Map002 rather
            // than exceed it. So this is said out loud rather than assumed away.
            foreach (string oversize in OversizeDirectories(output.WzDirectory))
                warnings.Add(oversize);

            report($"Reading a sample of {images:N0} images to compare against afterwards", 0, images);
            Dictionary<string, string> sampled = Sample(
                output.WzDirectory, expected, request.SampleSize ?? DefaultSampleSize);

            // Same volume as the destination so the swap at the end is a rename.
            // The stem is unique because MapleLib's SaveToDisk derives its own
            // scratch name from the file name and drops it in the working
            // directory: two imports of the same archive would otherwise share
            // one ".String.TEMP" and truncate each other.
            string temp = Path.Combine(directory, $"mbimport-{Guid.NewGuid():N}.wz");

            report($"Writing {images:N0} images to {Path.GetFileName(destination)}", 0, images);
            try
            {
                output.SaveToDisk(temp, save64Bit, WzMapleVersion.BMS);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            long bytes = File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (bytes == 0)
            {
                TryDelete(temp);
                throw new InvalidOperationException(
                    $"{archive.Name} serialised to zero bytes. Nothing was written.");
            }

            report($"Reopening {Bytes(bytes)} and checking it against the source", images, images);
            List<string> defects;
            try
            {
                defects = Verify(temp, expected, sampled, save64Bit);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                throw new InvalidOperationException(
                    $"The converted {archive.Name}.wz could not be read back, so nothing was written to " +
                    $"'{destination}'.\n\nDetail: {ex.Message}", ex);
            }

            if (defects.Count > 0)
            {
                // Kept, not deleted. It is up to an hour of work and the user may
                // want to look at it; what it must not be is silently installed.
                string rescued = Path.Combine(directory,
                    $"{archive.Name}.rejected-{DateTime.Now:yyyyMMdd-HHmmss}.wz");
                TryMove(temp, rescued);
                throw new InvalidOperationException(
                    $"The converted {archive.Name}.wz does not match the client it came from, so nothing was " +
                    $"written to '{destination}':\n" +
                    string.Join("\n", defects.Select(d => "  - " + d)) +
                    $"\n\nThe rejected file is kept at:\n{rescued}");
            }

            string? backup = null;
            if (File.Exists(destination))
            {
                backup = $"{destination}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Replace(temp, destination, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, destination);
            }

            _log.LogInformation("Imported {Archive} from {Source} to {Destination} ({Bytes} bytes, {Images} images)",
                archive.Name, archive.Path, destination, bytes, images);

            return new ImportResult
            {
                Archive = archive.Name,
                WrittenTo = destination,
                BackupPath = backup,
                Bytes = bytes,
                SourceBytes = archive.SourceBytes,
                Images = images,
                Directories = assembled.Directories,
                ImagesReserialised = assembled.ImagesReserialised,
                SourceFilesRead = assembled.SourceFilesRead,
                Sampled = sampled.Count,
                Seconds = clock.Elapsed.TotalSeconds,
                Warnings = warnings,
            };
        }
        finally
        {
            // The sources have to outlive the write -- SaveImages reads image
            // bytes straight off their readers -- which is why this is here and
            // not around the assembly. AssembledArchive.Dispose knows the order.
            assembled.Dispose();
        }
    }

    /// <summary>
    /// Names any directory in the tree whose images total more than a WZ entry's
    /// size field can hold, with what it came to.
    ///
    /// Measured by summing block sizes rather than by asking the writer, because
    /// the writer only knows after it has written. The sum is exact for the images
    /// that are copied through and an underestimate for the ones re-serialised out
    /// of .ms packs, which is the right direction to be wrong in: it never invents
    /// a warning, it can only miss one that is marginal.
    /// </summary>
    private static IEnumerable<string> OversizeDirectories(WzDirectory root)
    {
        List<string> found = new();
        Measure(root);
        return found;

        long Measure(WzDirectory dir)
        {
            long total = dir.WzImages.Sum(i => (long)i.BlockSize);
            foreach (WzDirectory sub in dir.WzDirectories)
                total += Measure(sub);

            if (total > int.MaxValue)
            {
                found.Add(
                    $"'{StripRoot(dir.FullPath)}' holds {Bytes(total)}, which is more than the size field of a " +
                    "WZ directory entry can express. The archive is written and verified correctly — nothing " +
                    "that reads a WZ navigates by that number — but no classic client ships a directory this " +
                    "big, so whether the game accepts it is untested.");
            }
            return total;
        }
    }

    /// <summary>
    /// Bytes the session will need on the destination's volume for archives that
    /// are open with unsaved work, and so are a pending write of their own size.
    ///
    /// Only archives that live on that same volume, and only dirty ones: a clean
    /// archive is not going to be written, and one on another drive does not
    /// compete for this space. A split archive contributes nothing — it is
    /// read-only and has no file to be saved back to.
    /// </summary>
    private static long ReservedBySession(WzSessionService? session, string destination)
    {
        if (session == null)
            return 0;

        string? volume = Path.GetPathRoot(destination);
        if (string.IsNullOrEmpty(volume))
            return 0;

        long total = 0;
        foreach (OpenFile file in session.Files)
        {
            if (file.Kind == "split" || file.Detached)
                continue;
            if (!file.Dirty && file.CountDirtyImages() == 0)
                continue;
            if (!string.Equals(Path.GetPathRoot(file.FilePath), volume, StringComparison.OrdinalIgnoreCase))
                continue;

            try { total += new FileInfo(file.FilePath).Length; } catch { }
        }
        return total;
    }

    private static string ResolveDestination(ImportRequest request, string archiveName)
    {
        if (!string.IsNullOrWhiteSpace(request.TargetPath))
        {
            string target = Path.GetFullPath(request.TargetPath);
            // A folder typed into the path box is the common slip, and appending
            // the archive name is what the user meant every time.
            if (Directory.Exists(target))
                return Path.Combine(target, archiveName + ".wz");
            return target;
        }

        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new ArgumentException("No output folder was given.");

        return Path.Combine(Path.GetFullPath(request.OutputFolder), archiveName + ".wz");
    }

    #endregion

    #region Assembly

    /// <summary>
    /// Fills <paramref name="destination"/> with everything the split archive at
    /// <paramref name="archiveDirectory"/> holds: its .wz parts, its .ms packs,
    /// and each nested archive as a sub-directory.
    ///
    /// Recursive because nesting is: <c>Data\Character\Cap\_Canvas</c> is three
    /// levels of the same structure, and <c>Character\Cap</c> has its own .ini,
    /// stub and parts exactly as Character does.
    /// </summary>
    private void AssembleInto(
        WzFile output, WzDirectory destination, string archiveDirectory, string dataDirectory, string archiveName,
        List<WzFile> sources, List<WzMsFile> packs, List<string> warnings,
        Action<string, long, long> report, int totalSourceFiles, ref short gameVersion,
        CancellationToken cancel)
    {
        int lastIndex = ReadLastWzIndex(archiveDirectory) ?? -1;
        string name = Path.GetFileName(archiveDirectory);

        for (int i = 0; i <= lastIndex; i++)
        {
            // Between files, not inside one. A single part is one ParseWzFile deep
            // in MapleLib with no cancellation of its own, and the largest of them
            // reads its directory table in well under a second; Sound's 66 parts
            // and Skill's nine packs are where the waiting actually is.
            cancel.ThrowIfCancellationRequested();
            report($"Reading {name}_{i:D3}.wz", sources.Count + packs.Count, totalSourceFiles);
            string part = Path.Combine(archiveDirectory, $"{name}_{i:D3}.wz");
            if (!File.Exists(part))
            {
                // The .ini promised a part that is not there. Not a warning: the
                // archive is incomplete and importing the rest of it would produce
                // a file that looks whole and is missing a chunk.
                throw new FileNotFoundException(
                    $"'{Path.GetFileName(archiveDirectory)}.ini' says there are {lastIndex + 1} parts but " +
                    $"'{part}' does not exist. The source client is incomplete.", part);
            }

            WzFile source = OpenSource(part, ref gameVersion);
            sources.Add(source);
            Merge(destination, source.WzDirectory, warnings);
        }

        // Packs belong to the top-level archive only. An entry is named
        // "Mob/0100000.img"; the prefix is the archive, and there is no nesting
        // in it, so they land at the root of whatever archive claims them.
        if (string.Equals(archiveDirectory, Path.Combine(dataDirectory, archiveName),
                          StringComparison.OrdinalIgnoreCase))
        {
            LoadPacks(destination, dataDirectory, archiveName, packs, warnings,
                      report, sources.Count, totalSourceFiles, cancel);
        }

        foreach (string sub in SafeDirectories(archiveDirectory))
        {
            if (ReadLastWzIndex(sub) == null)
                continue;   // an ordinary folder, not a nested archive

            string subName = Path.GetFileName(sub);

            // Merged into the existing directory when a part already contributed
            // one of the same name, rather than added beside it: two directories
            // with one name in a WZ archive is a lookup that returns whichever
            // comes first, which is how half an archive goes missing without an
            // error.
            WzDirectory child = destination.GetDirectoryByName(subName)
                ?? AddChildDirectory(destination, subName, output);

            AssembleInto(output, child, sub, dataDirectory, archiveName, sources, packs, warnings,
                         report, totalSourceFiles, ref gameVersion, cancel);
        }
    }

    /// <summary>
    /// How many .wz parts the nested archives under a directory add, so the
    /// progress bar has a denominator that does not jump. Counted from the .ini
    /// files rather than by listing .wz files, so the stub archives — which are
    /// not read — are not counted as work.
    /// </summary>
    private static int CountNestedFiles(string archiveDirectory)
    {
        int total = 0;
        foreach (string sub in SafeDirectories(archiveDirectory))
        {
            int? last = ReadLastWzIndex(sub);
            if (last == null)
                continue;
            total += last.Value + 1 + CountNestedFiles(sub);
        }
        return total;
    }

    /// <summary>
    /// Creates a sub-directory that carries the output archive's key and version
    /// hash.
    ///
    /// The two-argument constructor rather than <c>new WzDirectory(name)</c>,
    /// which leaves WzIv null: <c>GenerateDataFile</c> hands a directory's own IV
    /// to the writer for every image it re-serialises inside it, so a null-IV
    /// directory would write its .ms-sourced images under a key nothing can read
    /// back — and the write itself would not fail.
    /// </summary>
    private static WzDirectory AddChildDirectory(WzDirectory parent, string name, WzFile output)
    {
        WzDirectory child = new(name, output);
        parent.AddDirectory(child);
        return child;
    }

    /// <summary>
    /// Opens one source .wz part under the key the split client actually uses.
    ///
    /// BMS is asserted rather than detected, and that is the deliberate part.
    /// <c>WzTool.DetectMapleVersion</c> guesses by counting how many characters
    /// of the decrypted names look like text, which is a coin flip on a file with
    /// no names in it — and a split client is full of those. Measured:
    /// <c>Data\Etc\WZ2Lua\WZ2Lua_000.wz</c> is 63 bytes and holds zero images,
    /// detection calls it GMS, and an importer that believed it refused to
    /// convert Etc at all. Meanwhile every part that does have content parses as
    /// BMS, and the .ms packs are BMS by construction — <c>WzMsFile.LoadAsWzFile</c>
    /// hard-codes it with the comment ".ms files are always from BMS".
    ///
    /// Being wrong in the other direction is not silent: a mis-keyed parse
    /// produces an entry count outside 0..100000 and <c>ParseDirectory</c> throws,
    /// which is what the failure branch below reports. That is the property that
    /// makes asserting safe here and unsafe in a general file-open path.
    /// </summary>
    private WzFile OpenSource(string path, ref short knownGameVersion)
    {
        // The patch version is brute-forced on the first part and then reused.
        // Every part of one client carries the same one (269 for the client
        // measured here), and the search restarts at zero each time otherwise —
        // 66 times over for Sound.
        WzFile file = new(path, knownGameVersion, WzMapleVersion.BMS);
        WzFileParseStatus status = file.ParseWzFile();

        if (status != WzFileParseStatus.Success && knownGameVersion != -1)
        {
            // A remembered version that does not fit this part is not fatal; it
            // just means the guess was archive-specific. Fall back to the search
            // rather than refusing a file that is perfectly readable.
            file.Dispose();
            file = new WzFile(path, -1, WzMapleVersion.BMS);
            status = file.ParseWzFile();
        }

        if (status != WzFileParseStatus.Success)
        {
            file.Dispose();
            WzMapleVersion guess = WzTool.DetectMapleVersion(path, out short _);
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' could not be read with the BMS key the rest of this client uses " +
                $"({status.GetErrorDescription()}). Its encryption looks like {guess}, and converting it would " +
                "mean re-encrypting every image — which this importer does not do, because its whole safety " +
                "argument is that image bytes are copied unchanged.");
        }

        knownGameVersion = file.Version;
        return file;
    }

    /// <summary>
    /// Adds the .ms packs that belong to this archive.
    ///
    /// <c>WzMsFile.LoadAsWzFile</c> returns a WzFile whose images carry neither a
    /// size nor an offset — there is no .wz for them to be offsets into — and are
    /// marked changed so the writer re-serialises them instead of copying zero
    /// bytes. That marking is load-bearing; the check in <see cref="Import"/>
    /// exists to catch its absence rather than to trust it.
    /// </summary>
    private void LoadPacks(
        WzDirectory destination, string dataDirectory, string archiveName,
        List<WzMsFile> packs, List<string> warnings,
        Action<string, long, long> report, int sourcesSoFar, int totalSourceFiles,
        CancellationToken cancel)
    {
        string packsDirectory = Path.Combine(dataDirectory, "Packs");
        if (!Directory.Exists(packsDirectory))
            return;

        if (!ReadPacksIndex(dataDirectory).TryGetValue(archiveName, out int lastIndex))
            return;

        for (int i = 0; i <= lastIndex; i++)
        {
            cancel.ThrowIfCancellationRequested();
            string path = Path.Combine(packsDirectory, $"{archiveName}_{i:D5}.ms");
            report($"Reading {archiveName}_{i:D5}.ms", sourcesSoFar + packs.Count, totalSourceFiles);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Packs.ini says {archiveName} has {lastIndex + 1} packs but '{path}' does not exist. " +
                    "The source client is incomplete.", path);
            }

            // Refuse before the allocation that would end the process, not after.
            //
            // A pack is not a lazy read. WzMsFile.LoadAsWzFile decrypts every
            // entry into its own byte[] and calls ParseImage on all of them
            // immediately, and marks each Changed because there is no source
            // block for the writer to copy. Changed is what makes the cost
            // permanent: ImageMemoryService.IsReleasable refuses a changed image,
            // correctly and for the reason at the top of that file, so none of
            // this can ever be given back while the archive is open.
            //
            // Measured against C:\Nexon\Library\maplestory\appdata on a 31.7 GB
            // machine, working set after opening one split archive and sweeping:
            //
            //   Mob    4 packs,   365 MB of .ms  ->  2,191 MB, sweep releases 0
            //   Skill  9 packs,   888 MB of .ms  ->  2,641 MB, sweep releases 0
            //
            // i.e. three to six times the pack bytes, retained for the session.
            // Two archives is ~4.8 GB before a single thumbnail is drawn, and it
            // is additive, so a large enough client will take the process out
            // with an OutOfMemoryException the user sees as the window vanishing.
            //
            // The check is a floor on free memory rather than a prediction of the
            // cost, because the two measured amplifications differ by 2x and a
            // model that wrong would refuse opens that would have worked. A floor
            // only ever says "not on this machine, right now", which is both true
            // and actionable — and it is re-tested between packs, so a client
            // that fits stops exactly where it stops instead of at the end.
            RefuseIfMemoryIsShort(archiveName, path, i, lastIndex);

            // Copied into memory rather than read from the file: WzMsFile decrypts
            // each entry out of its base stream on demand and the resulting images
            // keep readers over those buffers for the whole save, so a file handle
            // held open for the duration buys nothing and a torn read mid-import
            // would be silent.
            // Sized from the file rather than grown into: MemoryStream doubles,
            // and every discarded half is a large-object allocation that is not
            // compacted, so growing into a 139 MB pack touches ~395 MB to hold
            // 139 MB. Assembling an archive does this once per pack, and Skill is
            // nine of them.
            MemoryStream buffer;
            using (FileStream file = File.OpenRead(path))
            {
                buffer = new MemoryStream(file.Length <= int.MaxValue ? (int)file.Length : 0);
                file.CopyTo(buffer);
            }
            buffer.Position = 0;

            WzMsFile pack = new(buffer, Path.GetFileName(path), path, leaveOpen: true);
            packs.Add(pack);
            pack.ReadEntries();

            WzFile asWz = pack.LoadAsWzFile();
            Merge(destination, asWz.WzDirectory, warnings);
        }
    }

    /// <summary>
    /// Free memory below which another pack must not be read.
    ///
    /// Sized against what one more pack costs rather than against the machine: a
    /// single Mob pack is ~100 MB of .ms and was measured to land as roughly
    /// 550 MB of retained, unreleasable heap, so a gigabyte is "there is room
    /// for one more and a margin", not a round number. Below it the honest
    /// answer is that this client does not fit here today.
    /// </summary>
    private const long MinimumFreeBytesPerPack = 1024L * 1024 * 1024;

    /// <summary>
    /// How much memory the machine has left, in bytes, or -1 when that cannot be
    /// answered.
    ///
    /// A seam, not an abstraction: it exists so the refusal below can be tested
    /// at all. A guard against running out of memory that is only exercised by
    /// actually running out of memory is a guard nobody has ever seen work, and
    /// this one throws — getting its threshold or its message wrong turns a
    /// working open into a refusal.
    ///
    /// GCMemoryInfo rather than Process.WorkingSet64: the question is what the
    /// *machine* has left, not what this process has taken. TotalAvailableMemory
    /// is physical RAM (or the container limit, if there ever is one) and
    /// MemoryLoad is what is already committed across everything running, which
    /// correctly counts the other applications the user has open.
    /// </summary>
    internal Func<long> FreeMemoryBytes { get; set; } = SystemMemory.FreeBytes;

    /// <summary>
    /// Stops a split-archive open before it exhausts the machine, and says what
    /// it was doing when it stopped.
    ///
    /// Throwing is the whole point. The alternative that was actually happening
    /// is an <see cref="OutOfMemoryException"/> from somewhere inside a decrypt
    /// or a parse — or, worse, the process going away with nothing logged, which
    /// is what an allocation failure during a blocking compacting collection
    /// looks like from outside. An InvalidOperationException here becomes an
    /// ordinary 400 with a readable message through <c>Endpoints.ErrorFilter</c>,
    /// and <see cref="Assemble"/>'s catch disposes every source and pack opened
    /// so far, so nothing is left holding memory or file handles.
    /// </summary>
    private void RefuseIfMemoryIsShort(string archiveName, string packPath, int index, int lastIndex)
    {
        // "Cannot tell" is not "no room": a platform that does not report a
        // memory limit must not have its opens refused, so -1 lets it through.
        long free = FreeMemoryBytes();
        if (free < 0 || free >= MinimumFreeBytesPerPack)
            return;

        long packMB = 0;
        try { packMB = new FileInfo(packPath).Length / (1024 * 1024); }
        catch (IOException) { /* the size is for the message only */ }

        throw new InvalidOperationException(
            $"Stopped opening {archiveName} after {index} of {lastIndex + 1} packs: this machine has " +
            $"{free / (1024 * 1024)} MB of memory free and the next pack ({Path.GetFileName(packPath)}, " +
            $"{packMB} MB) needs several times that once it is decrypted and parsed. " +
            "A split archive's packs cannot be released while it is open, so closing archives you are " +
            "not using — or other applications — is what makes room. Nothing was changed.");
    }

    /// <summary>
    /// Moves everything in <paramref name="source"/> into <paramref name="destination"/>,
    /// merging directories that already exist by name and refusing to shadow an
    /// image that does.
    ///
    /// Duplicate image names are a refusal rather than a warning because the two
    /// candidates are indistinguishable from here: the parts of a split archive
    /// are disjoint slices of one namespace, so a collision means either the
    /// source is inconsistent or this importer has merged two things that were
    /// never one archive. Picking either copy would put a wrong image in a file
    /// that verifies clean.
    /// </summary>
    private static void Merge(WzDirectory destination, WzDirectory source, List<string> warnings)
    {
        foreach (WzImage image in source.WzImages.ToList())
        {
            WzImage? clash = destination.GetImageByName(image.Name);
            if (clash != null)
            {
                throw new InvalidOperationException(
                    $"Two source files both contain '{destination.FullPath}\\{image.Name}'. The importer cannot " +
                    "tell which one the client uses, so nothing was written.");
            }

            // Removed from the source before it is added, never merely added.
            //
            // Leaving it in both lists costs two things, and the second one was a
            // real bug caught by this importer's own verification against Quest:
            // WzFile.Dispose walks the source tree and disposes images the output
            // now owns, and ClearImages -- the obvious way to prevent that --
            // nulls Parent on every image in the list, including the ones just
            // re-parented. FullPath walks Parent, so 24,352 images silently lost
            // their directory prefix: the inventory recorded "1000.img" where the
            // written file said "QuestData\1000.img", and every one of them read
            // as both missing and unexpected. RemoveImage detaches and AddImage
            // re-attaches, in that order, so the parent chain is only ever right.
            source.RemoveImage(image);
            destination.AddImage(image);
        }

        foreach (WzDirectory sub in source.WzDirectories.ToList())
        {
            WzDirectory? existing = destination.GetDirectoryByName(sub.Name);
            source.RemoveDirectory(sub);

            if (existing == null)
            {
                // The sub-directory keeps the IV it was parsed with, and that is
                // correct: every source checked in OpenSource resolves to the same
                // BMS key the output is written under, so there is nothing to
                // change. AddDirectory re-parents it and re-points its wzFile;
                // SaveToDisk's SetVersionHash fixes its hash for the whole tree.
                destination.AddDirectory(sub);
                continue;
            }
            Merge(existing, sub, warnings);
        }
    }

    #endregion

    #region Verification

    private sealed class Inventory
    {
        public int ImageCount { get; set; }
        /// <summary>Path without the archive root, to block size. -1 where two images share a path.</summary>
        public Dictionary<string, int> ByPath { get; } = new(StringComparer.Ordinal);
        /// <summary>Paths whose image will be re-serialised, so whose block size is expected to move.</summary>
        public HashSet<string> Reserialised { get; } = new(StringComparer.Ordinal);
    }

    private static Inventory TakeInventory(WzDirectory root)
    {
        Inventory inventory = new();
        foreach (WzImage image in EnumerateImages(root))
        {
            inventory.ImageCount++;
            string path = StripRoot(image.FullPath);
            if (inventory.ByPath.ContainsKey(path))
            {
                inventory.ByPath[path] = -1;
                continue;
            }
            inventory.ByPath[path] = image.BlockSize;
            if (image.Changed)
                inventory.Reserialised.Add(path);
        }
        return inventory;
    }

    /// <summary>
    /// Opens an evenly spread selection of source images and records a digest of
    /// their whole property tree, to be compared against the same images read
    /// back out of the written file.
    ///
    /// Evenly spread rather than the first N, because the first N of a WZ archive
    /// are all in the first directory and a write that lost everything after the
    /// first sub-directory would sample perfectly.
    /// </summary>
    private static Dictionary<string, string> Sample(WzDirectory root, Inventory inventory, int count)
    {
        Dictionary<string, string> digests = new(StringComparer.Ordinal);
        if (count <= 0)
            return digests;

        List<WzImage> all = EnumerateImages(root).ToList();
        if (all.Count == 0)
            return digests;

        int stride = Math.Max(1, all.Count / count);
        for (int i = 0; i < all.Count && digests.Count < count; i += stride)
        {
            WzImage image = all[i];
            string path = StripRoot(image.FullPath);

            // An ambiguous path cannot be matched back to the right image on the
            // far side, so digesting it would compare two different images and
            // report a defect that is not one.
            if (inventory.ByPath.TryGetValue(path, out int size) && size == -1)
                continue;

            try
            {
                digests[path] = Digest(image);
            }
            catch (Exception ex)
            {
                // Recorded, not skipped. An image that will not open on the source
                // side is a fact about the import, and the far side will produce
                // the same text only if it fails the same way.
                digests[path] = "unreadable: " + ex.GetType().Name;
            }
        }
        return digests;
    }

    /// <summary>
    /// A stable text rendering of one image's contents: every property's path,
    /// type and scalar value, in tree order.
    ///
    /// This is what makes verification worth trusting. Comparing node counts
    /// proves the shape survived; comparing this proves the values did. Canvases
    /// contribute their dimensions and format rather than their pixels — the
    /// pixels were copied as one opaque block whose length the block-size check
    /// already covers, and decompressing every sampled sprite would cost more
    /// than the whole import.
    /// </summary>
    private static string Digest(WzImage image)
    {
        bool wasOpen = image.Parsed;
        if (!image.Parsed && !image.ParseImage())
            return "unparseable";

        StringBuilder text = new();
        int leaves = 0;
        Walk(image.WzProperties, "");

        if (!wasOpen && !image.Changed)
        {
            // Only images this pass opened, and never a changed one: UnparseImage
            // discards the property tree, and for a .ms-sourced image that tree is
            // the only copy of its contents there is.
            image.UnparseImage();
        }
        return text.ToString();

        void Walk(WzPropertyCollection? properties, string prefix)
        {
            if (properties == null || leaves >= MaxDigestLeaves)
                return;

            foreach (WzImageProperty property in properties)
            {
                if (leaves >= MaxDigestLeaves)
                {
                    text.Append("...truncated\n");
                    return;
                }
                leaves++;

                string path = prefix + "/" + property.Name;
                text.Append(path).Append('|').Append(property.PropertyType);

                if (property is WzCanvasProperty canvas)
                {
                    text.Append('|').Append(canvas.PngProperty?.Width ?? 0)
                        .Append('x').Append(canvas.PngProperty?.Height ?? 0)
                        .Append('f').Append(canvas.PngProperty?.Format ?? 0);
                }
                else if (property.WzProperties == null || property.WzProperties.Count == 0)
                {
                    text.Append('|').Append(Scalar(property));
                }
                text.Append('\n');

                Walk(property.WzProperties, path);
            }
        }
    }

    /// <summary>
    /// The invariant text of a leaf property. Invariant culture throughout: a
    /// comma-decimal desktop would otherwise render 1.5 as "1,5" on one side of a
    /// comparison and the digests would differ for no reason at all.
    /// </summary>
    private static string Scalar(WzImageProperty property) => property switch
    {
        WzIntProperty i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        WzLongProperty l => l.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        WzShortProperty s => s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        WzFloatProperty f => f.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        WzDoubleProperty d => d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        WzStringProperty s => s.Value ?? "",
        WzVectorProperty v => $"{v.X?.Value ?? 0},{v.Y?.Value ?? 0}",
        WzUOLProperty u => u.Value ?? "",
        WzBinaryProperty b => $"len{b.Length}",
        _ => "",
    };

    /// <summary>
    /// Reopens the written file and compares it against what was assembled.
    ///
    /// Four things, and the first three are the same ones <c>WzSaveService</c>'s
    /// own <c>VerifyCandidate</c> makes, for the same reasons:
    /// the header's declared extent has to equal the file length (the only check
    /// a truncated write fails), every image has to be present and no others, and
    /// an image that was copied through byte for byte has to still be the size it
    /// was. The fourth is the one this importer needs beyond those: a sample of
    /// images is opened on the far side and its full property digest compared
    /// against the source's. An archive whose directory table is perfect and
    /// whose images are empty passes the first three and fails this.
    /// </summary>
    private static List<string> Verify(
        string path, Inventory expected, Dictionary<string, string> sampled, bool save64Bit)
    {
        List<string> defects = new();
        int suppressed = 0;
        void Defect(string message)
        {
            if (defects.Count < 10) defects.Add(message);
            else suppressed++;
        }

        WzFile probe = new(path, save64Bit ? ClassicGameVersion : (short)-1, WzMapleVersion.BMS);
        try
        {
            WzFileParseStatus status = probe.ParseWzFile();
            if (status != WzFileParseStatus.Success)
                throw new InvalidOperationException(status.GetErrorDescription());

            long onDisk = new FileInfo(path).Length;
            long declared = probe.Header.FStart + (long)probe.Header.FSize;
            if (declared != onDisk)
            {
                Defect(declared > onDisk
                    ? $"The header describes {declared:N0} bytes but only {onDisk:N0} were written — truncated."
                    : $"The header describes {declared:N0} bytes but the file is {onDisk:N0}.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            List<string> unexpected = new();
            int actual = 0;

            foreach (WzImage image in EnumerateImages(probe.WzDirectory))
            {
                actual++;
                string imagePath = StripRoot(image.FullPath);

                if (!expected.ByPath.TryGetValue(imagePath, out int before))
                {
                    if (unexpected.Count < 5) unexpected.Add(imagePath);
                    continue;
                }
                seen.Add(imagePath);

                if (before == -1)
                    continue;   // two source images share this path; presence is all that is checkable

                if (!expected.Reserialised.Contains(imagePath) && image.BlockSize != before)
                {
                    Defect($"'{imagePath}' was copied through unchanged but its size moved from " +
                           $"{before:N0} to {image.BlockSize:N0} bytes.");
                }

                if (sampled.TryGetValue(imagePath, out string? sourceDigest))
                {
                    string writtenDigest;
                    try { writtenDigest = Digest(image); }
                    catch (Exception ex) { writtenDigest = "unreadable: " + ex.GetType().Name; }

                    if (!string.Equals(sourceDigest, writtenDigest, StringComparison.Ordinal))
                        Defect($"'{imagePath}' reads back differently: {FirstDifference(sourceDigest, writtenDigest)}");
                }
            }

            if (actual != expected.ImageCount)
                Defect($"The written file holds {actual:N0} images but {expected.ImageCount:N0} were assembled.");

            List<string> missing = expected.ByPath.Keys.Where(p => !seen.Contains(p)).Take(5).ToList();
            if (missing.Count > 0)
                Defect("Missing from the written file: " + string.Join(", ", missing));
            if (unexpected.Count > 0)
                Defect("In the written file but not assembled: " + string.Join(", ", unexpected));

            List<string> unsampled = sampled.Keys.Where(p => !seen.Contains(p)).ToList();
            if (unsampled.Count > 0)
                Defect($"{unsampled.Count} sampled image(s) never appeared in the written file, " +
                       $"including '{unsampled[0]}'.");

            if (suppressed > 0)
                defects.Add($"...and {suppressed:N0} more of the same kind.");

            return defects;
        }
        finally
        {
            probe.Dispose();
        }
    }

    /// <summary>
    /// The first line on which two digests disagree, so a mismatch names the
    /// property rather than dumping two property trees into a toast.
    /// </summary>
    private static string FirstDifference(string a, string b)
    {
        string[] left = a.Split('\n');
        string[] right = b.Split('\n');
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            string l = i < left.Length ? left[i] : "<nothing>";
            string r = i < right.Length ? right[i] : "<nothing>";
            if (!string.Equals(l, r, StringComparison.Ordinal))
                return $"expected '{Trim(l)}', found '{Trim(r)}'";
        }
        return "the digests differ in length only";

        static string Trim(string s) => s.Length <= 90 ? s : s[..90] + "…";
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Every image in the tree that would be written as zero bytes while still
    /// appearing in the archive's index — the shape of an archive that opens,
    /// lists everything, and holds nothing.
    ///
    /// An image that is not marked changed is written by copying
    /// <c>BlockSize</c> bytes from its source reader
    /// (<c>WzDirectory.SaveImages</c>), so one whose size is zero contributes
    /// nothing and no check downstream notices: the header extent matches the
    /// file length, every image is present in the table, and comparing a
    /// not-rewritten image's recorded size against what it was before compares 0
    /// against 0.
    ///
    /// <c>WzMsFile.LoadAsWzFile</c> produces images in exactly that shape — the
    /// <c>WzImage(string, WzBinaryReader)</c> constructor assigns neither size
    /// nor offset, because there is no .wz file for them to be offsets into —
    /// and marks them changed so they take the re-serialise branch instead. That
    /// marking is a fix in MapleLib, and this is the backstop for the day another
    /// path produces the same shape without it. It is public so it can be tested
    /// against a hand-built tree: the state it detects cannot be written to disk,
    /// which is precisely why it must be caught in memory.
    /// </summary>
    /// <param name="limit">Stops after this many, because a systemic failure produces one per image.</param>
    public static List<string> FindHollowImages(WzDirectory root, int limit = 10)
    {
        List<string> hollow = new();
        foreach (WzImage image in EnumerateImages(root))
        {
            if (!image.Changed && image.BlockSize <= 0)
                hollow.Add(image.FullPath);
            if (hollow.Count >= limit)
                break;
        }
        return hollow;
    }

    private static (int Images, int Directories, int Reserialised) Census(WzDirectory root)
    {
        int images = 0, directories = 0, reserialised = 0;
        Stack<WzDirectory> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            WzDirectory dir = pending.Pop();
            foreach (WzImage image in dir.WzImages)
            {
                images++;
                if (image.Changed) reserialised++;
            }
            foreach (WzDirectory sub in dir.WzDirectories)
            {
                directories++;
                pending.Push(sub);
            }
        }
        return (images, directories, reserialised);
    }

    private static IEnumerable<WzImage> EnumerateImages(WzDirectory root)
    {
        Stack<WzDirectory> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            WzDirectory dir = pending.Pop();
            foreach (WzImage image in dir.WzImages)
                yield return image;
            foreach (WzDirectory sub in dir.WzDirectories)
                pending.Push(sub);
        }
    }

    /// <summary>
    /// Drops the archive's own root name. The written file is a scratch file at
    /// verification time, so its root directory is called "mbimport-..." and an
    /// absolute comparison would report every image as missing.
    /// </summary>
    private static string StripRoot(string fullPath)
    {
        int separator = fullPath.IndexOf('\\');
        return separator < 0 ? fullPath : fullPath[(separator + 1)..];
    }

    private static long MeasureArchive(string directory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.wz", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private static long MeasurePacks(string dataDirectory, string archiveName, int count)
    {
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(dataDirectory, "Packs", $"{archiveName}_{i:D5}.ms");
            try { if (File.Exists(path)) total += new FileInfo(path).Length; } catch { }
        }
        return total;
    }

    private static string ResolveFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("No folder was given.");

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{path}' is not a usable folder path ({ex.Message})");
        }

        if (!Directory.Exists(full))
        {
            if (File.Exists(full))
                throw new ArgumentException($"'{full}' is a file, not a folder.");
            throw new DirectoryNotFoundException($"There is no folder at '{full}'.");
        }
        return full;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try { return Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); } catch { TryDelete(from); }
    }

    private static string Bytes(long value)
    {
        double mb = value / 1024d / 1024d;
        if (mb < 1) return $"{value / 1024d:F0} KB";
        return mb >= 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F0} MB";
    }

    #endregion
}
