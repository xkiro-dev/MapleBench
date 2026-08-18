using System.Diagnostics;
using System.Security.Cryptography;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/* ============================================================================
   CANVAS-DIRECTORY LINK INLINE — art the editor can see and the client cannot

   WHAT IS WRONG

   A canvas can hold no pixels of its own and instead carry an `_outlink`
   naming, from the archive family's root, the node whose art it draws. Some of
   those links point INTO a `_Canvas` split-art directory —
   `Character/Weapon/_Canvas/01402096.img/…`, `Skill/_Canvas/50000.img/…`. The
   art is really there: an editor mounting the family resolves the path and
   draws the picture. The v232 client does not. It follows an outlink one level
   and expects art inline, so every one of these canvases draws NOTHING in
   game, in silence — the archive opens, the audit of dangling links is clean,
   and the frame is invisible. The auditor reports them as
   `outlink.into_canvas_dir`, a separate finding from `outlink.unresolved`,
   because nothing is missing: the link is simply beyond what this client can
   follow.

   THE ONLY HONEST REPAIR IS TO PUT THE ART WHERE THE CLIENT LOOKS

   This pass resolves each such link and INLINES the pixels: the linked
   picture's compressed bytes are written into the linking canvas and the
   `_outlink` is dropped. Nothing else changes — the canvas keeps its own
   origin, delay and stamp, and the `_Canvas` directory the link named keeps
   its art (other links may still name it, and deleting is never this tool's
   job).

   Resolution goes through MapleLib's identity-aware resolver
   (<see cref="WzLinkResolver"/>), never around it, because the failure modes
   it refuses are exactly the ones a repair must not guess through:

   - only the EXACT property address counts — the resolver's bulk-extraction
     fallback strategies are disabled for a write;
   - a candidate that is ITSELF a link (a shell) is never a pixel source, and
     an address answered only by shells is refused with each shell named;
   - several same-named candidates are decided by identity — the `_hash` stamp
     the client writes, then byte identity — and when nothing distinguishes
     them the resolution is REFUSED with the candidates named. A refusal that
     says why beats a silent coin flip; every refusal survives into the scan
     report and the apply result.

   THREE THINGS THIS DELIBERATELY WILL NOT DO

   1. It does not write to the archive it read. Apply produces a NEW file
      beside the source and hands back the command to install it, backup
      first. Nothing here installs anything.
   2. It does not delete a link it cannot inline. A refused or unresolvable
      link is reported and left exactly as it is — deleting it would make the
      audit green and leave the frame just as blank.
   3. It does not inline art that is itself wounded. A source canvas carrying
      a non-zero magnification is the split-format damage; inlining it would
      copy the wound. Refused, with the composing instruction: run the
      canvas-format repair over that archive first.

   Every pass records itself in <see cref="RepairLedger"/>, keyed on content,
   so it composes after the Skill family's earlier repairs instead of silently
   forking them.
   ============================================================================ */

/// <summary>What can be done about one canvas whose _outlink points into a _Canvas directory.</summary>
public enum CanvasDirLinkVerdict
{
    /// <summary>The resolver chose exactly one picture. Inlined on apply.</summary>
    Inlineable = 0,

    /// <summary>
    /// The resolver REFUSED — shells only, ambiguous candidates, or the source
    /// art is itself format-wounded. The reason names the candidates. Left
    /// alone, reported.
    /// </summary>
    Refused = 1,

    /// <summary>Nothing answers the address at all. Left alone, reported.</summary>
    Unresolvable = 2,
}

/// <summary>
/// One linking canvas, the link it carries, what the resolver found behind it,
/// and the verdict — verbose on purpose, so the caller can check the decision
/// instead of trusting it.
/// </summary>
public sealed record CanvasDirLinkCase(
    string Link,
    string Family,
    string Archive,
    string ImagePath,
    string Inside,
    string CanvasPath,
    string Stamp,
    string SourcePath,
    int SourceWidth,
    int SourceHeight,
    int SourceFormat,
    int SourceMag,
    long SourceCompressedBytes,
    string SourceHash,
    CanvasDirLinkVerdict Verdict,
    string Why);

/// <summary>How many links point under one _Canvas address, for reconciling with the audit.</summary>
public sealed record CanvasDirCount(string Under, int DistinctLinks, long Canvases);

/// <summary>What a read-only scan found. Nothing has been written.</summary>
public sealed record CanvasDirLinkScan(
    string Folder,
    DateTimeOffset StartedUtc,
    double Seconds,
    IReadOnlyList<string> Archives,
    long Images,
    long Canvases,
    long CanvasesWithOutlink,
    int DistinctLinks,
    long LinkCanvases,
    int Inlineable,
    int Refused,
    int Unresolvable,
    IReadOnlyList<string> TargetArchives,
    IReadOnlyList<CanvasDirCount> Directories,
    IReadOnlyList<CanvasDirCount> LinkImages,
    IReadOnlyList<CanvasDirLinkCase> Cases,
    IReadOnlyList<string> Notes);

/// <summary>One canvas inlined, with the identity it was checked against at both ends.</summary>
public sealed record CanvasDirLinkWrite(
    string Path,
    string Link,
    string SourcePath,
    string ExpectedHash,
    string WrittenHash,
    bool IdentityHeld,
    bool LinkGone,
    bool Decoded,
    string Note);

/// <summary>What an apply did, measured against the file it wrote and reopened.</summary>
public sealed record CanvasDirLinkResult(
    string Source,
    string Output,
    double Seconds,
    int Considered,
    int Written,
    int Refused,
    int Deferred,
    int IdentityHeld,
    int IdentityLost,
    int LinksGone,
    int Decoded,
    int FailedToDecode,
    int BystandersDecoded,
    int BystandersFailed,
    int LinksBefore,
    int LinksAfter,
    long LinkCanvasesBefore,
    long LinkCanvasesAfter,
    long SourceBytes,
    long OutputBytes,
    long CarriedBytes,
    IReadOnlyList<CanvasDirLinkWrite> Writes,
    IReadOnlyList<CanvasDirLinkCase> StillLinked,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Notes,
    string InstallCommand,
    RepairInputCheck? Input = null,
    RepairLedgerFile? Ledger = null);

public sealed class CanvasDirLinkOptions
{
    /// <summary>The client folder whose mounted archives are examined.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Archive family to examine — "Skill". Empty means every family in the folder.</summary>
    public string? Family { get; set; }

    /// <summary>
    /// The `_outlink` values the auditor reported as `outlink.into_canvas_dir`,
    /// when the caller has a report to drive this from. Empty means "find them
    /// here". Either way the archives are walked, and a disagreement between
    /// the report and this scan is said out loud rather than quietly
    /// reconciled.
    /// </summary>
    public string[]? Links { get; set; }

    /// <summary>Encryption, when known — "BMS", "GMS", "EMS". Empty means detect.</summary>
    public string? MapleVersion { get; set; }

    /// <summary>Patch version, when known. 0 means detect it with the encryption.</summary>
    public short GameVersion { get; set; }

    /// <summary>How many cases to return per verdict. The counts are always complete.</summary>
    public int MaxCases { get; set; } = 500;

    /// <summary>
    /// Which archive an apply writes a new copy of — the one HOLDING the
    /// linking canvases, since those are what an inline modifies. Empty means
    /// "the one every inlineable case sits in", and an apply refuses when they
    /// sit in more than one rather than picking.
    /// </summary>
    public string? TargetArchive { get; set; }

    /// <summary>Where an apply writes. Empty means "&lt;target&gt;.inlined.wz" beside it.</summary>
    public string? Output { get; set; }

    /// <summary>An apply refuses without this.</summary>
    public bool Confirm { get; set; }

    /// <summary>
    /// Archives smaller than this are named in the notes and never opened —
    /// the 6 KB stub archives (`Base.wz`, `Data.wz`) crash the directory
    /// parser irrecoverably. Settable because every test fixture is small, and
    /// whatever it is set to, what was skipped is reported.
    /// </summary>
    public long MinimumArchiveBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Write even though another kind of repair has already built a whole
    /// archive from this exact input. Separate from <see cref="Confirm"/>
    /// because it answers a different question: both outputs are complete
    /// copies of the source, so installing the second silently reverts the
    /// first. The composing answer is to run this pass against the other
    /// pass's output, and <see cref="RepairLedger"/> names it.
    /// </summary>
    public bool AcceptSeparateRepairs { get; set; }
}

public sealed class CanvasDirLinkProgress
{
    public string State { get; set; } = "idle";     // idle | scanning | inlining | saving | verifying | done | failed | cancelled
    public string Phase { get; set; } = "";
    public string Archive { get; set; } = "";
    public long ImagesDone { get; set; }
    public long CanvasesDone { get; set; }
    public int Found { get; set; }
    public int Inlineable { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }
}

public sealed class CanvasDirLinkRepairService
{
    private readonly WarmupService _warmup;
    private readonly object _gate = new();
    private readonly CanvasDirLinkProgress _progress = new();
    private CancellationTokenSource? _cancel;
    private CanvasDirLinkScan? _scan;
    private CanvasDirLinkResult? _result;

    public CanvasDirLinkRepairService(WarmupService warmup) => _warmup = warmup;

    public CanvasDirLinkProgress Snapshot()
    {
        lock (_gate)
            return new CanvasDirLinkProgress
            {
                State = _progress.State,
                Phase = _progress.Phase,
                Archive = _progress.Archive,
                ImagesDone = _progress.ImagesDone,
                CanvasesDone = _progress.CanvasesDone,
                Found = _progress.Found,
                Inlineable = _progress.Inlineable,
                Seconds = _progress.Seconds,
                Error = _progress.Error,
            };
    }

    /// <summary>Whether a run is in flight. Advisory: <see cref="StartScan"/> is the race-free ask.</summary>
    public bool Busy
    {
        get { lock (_gate) return _progress.State is "scanning" or "inlining" or "saving" or "verifying"; }
    }

    public CanvasDirLinkScan? LastScan() { lock (_gate) return _scan; }
    public CanvasDirLinkResult? LastResult() { lock (_gate) return _result; }
    public void Cancel() { lock (_gate) _cancel?.Cancel(); }

    /* ====================================================================
       SCAN
       ==================================================================== */

    /// <summary>
    /// Reads. Writes nothing, opens nothing for writing, and leaves every
    /// archive it touched exactly as it found it.
    /// </summary>
    public CanvasDirLinkScan Scan(CanvasDirLinkOptions options) => RunScan(Begin("scanning"), options);

    /// <summary>
    /// Reserves the service SYNCHRONOUSLY, then scans on a background thread.
    /// The reservation must happen on the caller's thread: fired into a
    /// <c>Task.Run</c>, a busy refusal is thrown where nothing sees it and the
    /// response carries the OTHER run's progress — a 200 indistinguishable
    /// from acceptance. Same fix as the canvas-format and donor-restore
    /// services, where driving the endpoints found it.
    /// </summary>
    public CanvasDirLinkProgress StartScan(CanvasDirLinkOptions options)
    {
        CancellationTokenSource cancel = Begin("scanning");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunScan(cancel, options); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    /// <summary>Reserves synchronously, then applies on a background thread. See <see cref="StartScan"/>.</summary>
    public CanvasDirLinkProgress StartApply(CanvasDirLinkOptions options)
    {
        Confirmed(options);
        CancellationTokenSource cancel = Begin("inlining");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunApply(cancel, options); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    private static void Confirmed(CanvasDirLinkOptions options)
    {
        if (!options.Confirm)
            throw new InvalidOperationException(
                "An inline writes a new archive the size of the source. Pass confirm=true.");
    }

    private CanvasDirLinkScan RunScan(CancellationTokenSource cancel, CanvasDirLinkOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        try
        {
            CanvasDirLinkScan scan = ScanCore(options, cancel.Token, clock, started);
            lock (_gate)
            {
                _scan = scan;
                _progress.State = "done";
                _progress.Phase = "";
                _progress.Seconds = clock.Elapsed.TotalSeconds;
            }
            return scan;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _progress.State = "cancelled"; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate) { _progress.State = "failed"; _progress.Error = ex.Message; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        finally
        {
            lock (_gate) { _cancel?.Dispose(); _cancel = null; }
        }
    }

    private CanvasDirLinkScan ScanCore(CanvasDirLinkOptions options, CancellationToken token,
                                       Stopwatch clock, DateTimeOffset started)
    {
        List<string> archives = DonorRestoreService.Discover(
            options.Folder, options.Family, options.MinimumArchiveBytes, out List<string> skipped);
        if (archives.Count == 0)
            throw new InvalidOperationException(
                $"No mountable .wz archive at {options.Folder}" +
                (string.IsNullOrWhiteSpace(options.Family) ? "." : $" in the {options.Family} family.") +
                (skipped.Count > 0
                    ? $" {skipped.Count} file(s) were passed over as stubs ({string.Join(", ", skipped)}); " +
                      "lower minimumArchiveBytes if one of them is real."
                    : ""));

        (WzMapleVersion version, short gameVersion, string how) = Encryption(archives, options);
        List<string> notes = new() { how };
        if (skipped.Count > 0)
            notes.Add($"{skipped.Count} file(s) under {options.MinimumArchiveBytes:N0} bytes were not opened " +
                      $"({string.Join(", ", skipped)}). Nothing in them was examined, and a link into or out " +
                      "of one of them is unchecked rather than broken.");

        List<WzFile> open = new();
        try
        {
            foreach (string path in archives)
            {
                token.ThrowIfCancellationRequested();
                WzFile file = new(path, gameVersion, version);
                WzFileParseStatus status = file.ParseWzFile();
                if (status != WzFileParseStatus.Success)
                {
                    notes.Add($"{Path.GetFileName(path)} could not be opened ({status.GetErrorDescription()}); " +
                              "nothing in it was examined.");
                    file.Dispose();
                    continue;
                }
                open.Add(file);
            }
            if (open.Count == 0)
                throw new InvalidOperationException("Every archive in the folder failed to open.");

            Census census = Walk(open, token);
            notes.Add($"{census.Canvases:N0} canvases in {census.Images:N0} images across " +
                      $"{open.Count} archive(s). {census.OutlinkCanvases:N0} carry an _outlink, and " +
                      $"{census.IntoCanvas.Count:N0} of those point into a _Canvas directory " +
                      $"({census.IntoCanvas.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} " +
                      "distinct link values) — art an editor resolves and a v232 client draws as nothing.");

            List<LinkRef> refs = census.IntoCanvas;

            /* Driven by the auditor when the caller has a report, reconciled
               out loud in both directions. */
            if (options.Links is { Length: > 0 })
            {
                HashSet<string> asked = options.Links
                    .Select(l => l.Replace('\\', '/').Trim('/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> found = refs.Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

                int askedButAbsent = asked.Count(a => !found.Contains(a));
                int foundButNotAsked = refs.Count(r => !asked.Contains(r.Value));

                notes.Add($"Driven by {asked.Count:N0} link(s) the auditor reported as outlink.into_canvas_dir. " +
                          (askedButAbsent > 0
                              ? $"{askedButAbsent:N0} of them are not carried by any canvas in this scan — the " +
                                "report is older than the client, or ran over a different set of archives. "
                              : "Every one of them was found here too. ") +
                          (foundButNotAsked > 0
                              ? $"{foundButNotAsked:N0} further such canvas(es) were found that the report does " +
                                "not name; those are NOT acted on, because the caller asked for a list."
                              : ""));

                refs = refs.Where(r => asked.Contains(r.Value)).ToList();
            }

            lock (_gate) _progress.Found = refs.Count;

            List<CanvasDirLinkCase> cases = Judge(open, refs, token);

            int inlineable = cases.Count(c => c.Verdict == CanvasDirLinkVerdict.Inlineable);
            int refused = cases.Count(c => c.Verdict == CanvasDirLinkVerdict.Refused);
            int unresolvable = cases.Count(c => c.Verdict == CanvasDirLinkVerdict.Unresolvable);
            lock (_gate) _progress.Inlineable = inlineable;

            notes.Add($"{inlineable:N0} canvas(es) can be inlined outright; {refused:N0} are refused by the " +
                      $"identity-aware resolver (each refusal names its candidates); {unresolvable:N0} resolve " +
                      "to nothing at all. Refused and unresolvable links are reported and left exactly as they " +
                      "are — deleting a link never makes a frame draw.");

            List<CanvasDirCount> directories = refs
                .GroupBy(r => DirectoryOf(r.Value), StringComparer.OrdinalIgnoreCase)
                .Select(g => new CanvasDirCount(
                    g.Key,
                    g.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count()))
                .OrderByDescending(d => d.DistinctLinks)
                .ThenBy(d => d.Under, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<CanvasDirCount> images = refs
                .GroupBy(r => ImageOf(r.Value), StringComparer.OrdinalIgnoreCase)
                .Select(g => new CanvasDirCount(
                    g.Key,
                    g.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count()))
                .OrderByDescending(d => d.DistinctLinks)
                .ThenBy(d => d.Under, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CanvasDirLinkScan(
                options.Folder, started, clock.Elapsed.TotalSeconds,
                archives.Select(a => Path.GetFileName(a) ?? "").ToList(),
                census.Images, census.Canvases, census.OutlinkCanvases,
                refs.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                refs.Count,
                inlineable, refused, unresolvable,
                cases.Where(c => c.Verdict == CanvasDirLinkVerdict.Inlineable)
                     .Select(c => c.Archive).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
                directories, images,
                cases.GroupBy(c => c.Verdict)
                     .SelectMany(g => g.Take(Math.Max(1, options.MaxCases)))
                     .OrderBy(c => (int)c.Verdict).ThenBy(c => c.CanvasPath, StringComparer.OrdinalIgnoreCase)
                     .ToList(),
                notes);
        }
        finally
        {
            foreach (WzFile file in open)
                try { file.Dispose(); } catch { /* read-only handle */ }
        }
    }

    /* ====================================================================
       APPLY
       ==================================================================== */

    /// <summary>
    /// Inlines the art behind every inlineable link in one archive, writes a
    /// NEW file, reopens it from disk, and asks in order: is the _outlink
    /// gone, are the pixels the SAME BYTES the resolver chose, and do they
    /// decode. Then re-detects over the saved family, so the finding count is
    /// measured on the file that was written, not on the tree that wrote it.
    /// </summary>
    public CanvasDirLinkResult Apply(CanvasDirLinkOptions options)
    {
        Confirmed(options);
        return RunApply(Begin("inlining"), options);
    }

    private CanvasDirLinkResult RunApply(CancellationTokenSource cancel, CanvasDirLinkOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            CanvasDirLinkResult result = ApplyCore(options, cancel.Token, clock);
            lock (_gate)
            {
                _result = result;
                _progress.State = "done";
                _progress.Phase = "";
                _progress.Seconds = clock.Elapsed.TotalSeconds;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _progress.State = "cancelled"; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate) { _progress.State = "failed"; _progress.Error = ex.Message; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        finally
        {
            lock (_gate) { _cancel?.Dispose(); _cancel = null; }
        }
    }

    private CanvasDirLinkResult ApplyCore(CanvasDirLinkOptions options, CancellationToken token, Stopwatch clock)
    {
        // The scan is the plan: run it here rather than trusting a cached one,
        // so the archive an apply writes is the archive an apply looked at.
        lock (_gate) { _progress.State = "scanning"; _progress.Phase = "finding"; }
        CanvasDirLinkScan scan = ScanCore(options, token, clock, DateTimeOffset.UtcNow);
        lock (_gate) { _scan = scan; _progress.State = "inlining"; }

        List<string> notes = new(scan.Notes);
        List<string> failures = new();

        if (scan.Cases.Count < scan.Inlineable + scan.Refused + scan.Unresolvable)
            throw new InvalidOperationException(
                "The scan truncated its case list, so an apply driven by it would write less than it " +
                "found. Raise MaxCases.");

        List<CanvasDirLinkCase> wanted = scan.Cases
            .Where(c => c.Verdict == CanvasDirLinkVerdict.Inlineable)
            .ToList();

        string[] targets = wanted.Select(c => c.Archive)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string target = options.TargetArchive ?? "";
        if (target.Length == 0)
        {
            if (targets.Length == 0)
                throw new InvalidOperationException(
                    "Nothing here can be inlined into any archive: " +
                    $"{scan.LinkCanvases:N0} canvas(es) point into _Canvas directories and every one is " +
                    "refused or unresolvable. Read the scan; there is nothing for an apply to do.");
            if (targets.Length > 1)
                throw new InvalidOperationException(
                    "The linking canvases sit in more than one archive (" + string.Join(", ", targets) + "). " +
                    "An apply writes one new archive, so name which with targetArchive and run it once per " +
                    "archive — rather than this picking one and reporting the rest as done.");
            target = targets[0];
        }

        string source = Path.GetFullPath(Path.Combine(options.Folder, target + ".wz"));
        if (!File.Exists(source))
            throw new InvalidOperationException($"{source} is not a file.");

        string output = string.IsNullOrWhiteSpace(options.Output)
            ? Path.Combine(Path.GetDirectoryName(source) ?? ".",
                           Path.GetFileNameWithoutExtension(source) + ".inlined.wz")
            : Path.GetFullPath(options.Output!);

        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The inline writes a new archive; it never edits the one it read. " +
                "Give an output path that is not the source.");

        /* What has already been done to this file, by content rather than by
           path. Reading another pass's OUTPUT composes; reading an input
           another pass already built from is refused, naming which archive to
           run against instead — see RepairLedger for the accident that is. */
        RepairInputCheck input = RepairLedger.Inspect(source, RepairLedger.CanvasDirInlinePass);
        if (input.Verdict == RepairInputVerdict.Stale)
            throw new InvalidOperationException(input.Why);
        if (input.ConflictsWith.Count > 0 && !options.AcceptSeparateRepairs)
            throw new InvalidOperationException(input.Why);
        notes.Add(input.Why);

        List<CanvasDirLinkCase> mine = wanted
            .Where(c => c.Archive.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int deferred = wanted.Count - mine.Count;
        if (mine.Count == 0)
            throw new InvalidOperationException(
                $"No inlineable case sits in {target}.wz. The scan names the archives that hold them: " +
                string.Join(", ", targets) + ".");

        string targetFamily = DonorRestoreService.Family(target);
        HashSet<string> neededFamilies = mine.Select(c => c.Family)
            .Append(targetFamily)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        (WzMapleVersion version, short gameVersion, _) = Encryption(new List<string> { source }, options);

        // Every mounted archive of the needed families, with the TARGET path
        // opened once as the instance the save writes. Candidates and linking
        // canvases must come out of one tree per archive.
        List<string> familyArchives = DonorRestoreService.Discover(
                options.Folder, null, options.MinimumArchiveBytes, out _)
            .Where(p => neededFamilies.Contains(
                DonorRestoreService.Family(Path.GetFileNameWithoutExtension(p))))
            .ToList();

        long sourceBytes = new FileInfo(source).Length;
        long carriedBytes = 0;
        List<(CanvasDirLinkCase Case, CanvasDirLinkWrite Write)> written = new();

        WzFile? file = null;
        List<WzFile> open = new();
        try
        {
            foreach (string path in familyArchives)
            {
                token.ThrowIfCancellationRequested();
                WzFile one = new(path, gameVersion, version);
                if (one.ParseWzFile() != WzFileParseStatus.Success) { one.Dispose(); continue; }
                open.Add(one);
                if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase))
                    file = one;
            }
            if (file == null)
                throw new InvalidOperationException($"{target}.wz failed to open for the apply pass.");

            Dictionary<string, WzLinkResolver> resolvers = Resolvers(open);
            HashSet<WzImage> changed = new();

            lock (_gate) { _progress.Phase = "inlining"; }

            foreach (CanvasDirLinkCase one in mine)
            {
                token.ThrowIfCancellationRequested();

                WzImage? image = DonorRestoreService.FindImage(file.WzDirectory, one.ImagePath);
                if (image == null)
                {
                    failures.Add($"{one.CanvasPath}: the image is no longer reachable in the apply pass.");
                    continue;
                }
                WzSessionService.EnsureParsed(image);

                if (image.GetFromPath(one.Inside) is not WzCanvasProperty canvas)
                {
                    failures.Add($"{one.CanvasPath}: no canvas at this path in the apply pass.");
                    continue;
                }
                if (!resolvers.TryGetValue(one.Family, out WzLinkResolver? resolver))
                {
                    failures.Add($"{one.CanvasPath}: no archive of the {one.Family} family is open, so the " +
                                 "link cannot be resolved. Nothing was written for it.");
                    continue;
                }

                if (resolver.ResolveOutlinkExactInPlace(canvas, one.CanvasPath))
                {
                    changed.Add(image);
                    carriedBytes += one.SourceCompressedBytes;
                    written.Add((one, new CanvasDirLinkWrite(
                        one.CanvasPath, one.Link, one.SourcePath, one.SourceHash, "",
                        false, false, false,
                        $"{one.SourceWidth}x{one.SourceHeight} format {one.SourceFormat}, " +
                        $"{one.SourceCompressedBytes:N0} compressed bytes inlined from {one.SourcePath}.")));
                }
                else
                {
                    failures.Add($"{one.CanvasPath}: the scan judged this inlineable and the apply's own " +
                                 "resolution did not — " +
                                 (resolver.FailedLinks.LastOrDefault() ?? "no detail recorded") +
                                 ". Nothing was written for it.");
                }
            }

            if (written.Count == 0)
            {
                notes.Add("Nothing was written. That is a result, not a no-op: " +
                          $"{scan.LinkCanvases:N0} canvas(es) point into _Canvas directories and none in " +
                          $"{target}.wz was both inlineable and successfully resolved.");
                CanvasDirLinkResult empty = new(
                    source, "", clock.Elapsed.TotalSeconds,
                    scan.LinkCanvases > int.MaxValue ? int.MaxValue : (int)scan.LinkCanvases,
                    0, scan.Refused + scan.Unresolvable, deferred,
                    0, 0, 0, 0, 0, 0, 0,
                    scan.DistinctLinks, scan.DistinctLinks, scan.LinkCanvases, scan.LinkCanvases,
                    sourceBytes, 0, 0,
                    Array.Empty<CanvasDirLinkWrite>(),
                    scan.Cases.Where(c => c.Verdict != CanvasDirLinkVerdict.Inlineable).ToList(),
                    failures, notes, "", input, input.Ledger);
                return empty;
            }

            foreach (WzImage image in changed) image.Changed = true;

            lock (_gate) { _progress.State = "saving"; _progress.Phase = "saving"; }

            string temp = output + ".partial";
            if (File.Exists(temp)) File.Delete(temp);

            /* UNKNOWN and no override IV, so only the images written into are
               re-serialised and every other image travels as the bytes it
               already was — the same decision, with the same measurement
               behind it, as the canvas-format repair and the donor restore. */
            file.SaveToDisk(temp, null, WzMapleVersion.UNKNOWN, null);

            if (File.Exists(output)) File.Delete(output);
            File.Move(temp, output);

            notes.Add($"{written.Count:N0} canvas(es) inlined across {changed.Count:N0} image(s) of " +
                      $"{target}.wz; every other image was copied across byte for byte.");
        }
        finally
        {
            foreach (WzFile one in open) try { one.Dispose(); } catch { /* read-only or closed by save */ }
        }

        /* ---- verify on the saved and reopened archive ---- */
        lock (_gate) { _progress.State = "verifying"; _progress.Phase = "decoding"; }

        (List<CanvasDirLinkWrite> verified, int bystanders, int bystandersFailed, List<string> broke) =
            Verify(output, version, gameVersion, written, token);
        failures.AddRange(broke);

        long outputBytes = new FileInfo(output).Length;
        int rewritten = written.Select(w => w.Case.ImagePath)
                               .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        notes.Add($"{sourceBytes:N0} bytes in, {outputBytes:N0} out — a delta of " +
                  $"{outputBytes - sourceBytes:+#,##0;-#,##0;0}. {carriedBytes:N0} bytes of compressed pixels " +
                  $"were inlined into {written.Count:N0} canvas(es), replacing their placeholder blocks and " +
                  $"their _outlink strings; the rest of the delta is the re-serialisation of the " +
                  $"{rewritten:N0} image(s) holding them. No other image was rewritten.");
        notes.Add($"{bystanders:N0} canvases that were NOT inlined but share a rewritten image were decoded " +
                  $"as well, and {bystandersFailed:N0} of them failed. Inlining a canvas marks its whole image " +
                  "changed and a changed image is re-serialised, so those are what this repair actually put " +
                  "at risk.");

        /* ---- re-detect on the saved family, which is the claim that matters ---- */
        lock (_gate) { _progress.Phase = "re-detecting"; }
        (int linksAfter, long linkCanvasesAfter, List<CanvasDirLinkCase> still) =
            ReDetect(options, source, output, target, targetFamily, version, gameVersion, scan, token);

        List<LinkRefLite> beforeInFamily = scan.Cases
            .Where(c => DonorRestoreService.Family(c.Archive)
                .Equals(targetFamily, StringComparison.OrdinalIgnoreCase))
            .Select(c => new LinkRefLite(c.Link, c.CanvasPath))
            .ToList();
        int linksBefore = beforeInFamily.Select(r => r.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        long linkCanvasesBefore = beforeInFamily.Count;

        notes.Add($"Re-detected over the {targetFamily} family with {Path.GetFileName(output)} mounted in " +
                  $"{target}.wz's place: {linksBefore:N0} distinct _Canvas link(s) behind " +
                  $"{linkCanvasesBefore:N0} canvas(es) become {linksAfter:N0} behind {linkCanvasesAfter:N0}. " +
                  "That is measured on the file that was written, not on the tree that wrote it. Whatever " +
                  "remains is the refused and unresolvable set, each with its reason on it.");

        int identityHeld = verified.Count(v => v.IdentityHeld);
        int linksGone = verified.Count(v => v.LinkGone);
        int decoded = verified.Count(v => v.Decoded);

        lock (_gate) { _progress.Phase = "recording"; }
        RepairLedgerFile ledger = RepairLedger.Record(
            input, RepairLedger.CanvasDirInlinePass, output, written.Count,
            written.Select(w => w.Write.Path).ToList(), notes);
        notes.Add($"{Path.GetFileName(output)} now carries a ledger of {ledger.Passes.Count} repair pass(es) — " +
                  $"{string.Join(", ", ledger.Passes.Select(p => $"{p.Pass} ({p.Changed:N0})"))} — all of them " +
                  $"from {Path.GetFileName(ledger.Origin)}. Install THIS file rather than any earlier one: " +
                  "each is a whole archive, so the last one copied wins.");

        return new CanvasDirLinkResult(
            source, output, clock.Elapsed.TotalSeconds,
            scan.LinkCanvases > int.MaxValue ? int.MaxValue : (int)scan.LinkCanvases,
            written.Count,
            scan.Refused + scan.Unresolvable,
            deferred,
            identityHeld, written.Count - identityHeld,
            linksGone,
            decoded, written.Count - decoded,
            bystanders, bystandersFailed,
            linksBefore, linksAfter, linkCanvasesBefore, linkCanvasesAfter,
            sourceBytes, outputBytes, carriedBytes,
            verified, still, failures, notes,
            InstallCommand(ledger.Origin, output), input, ledger);
    }

    /* ====================================================================
       VERIFY
       ==================================================================== */

    /// <summary>
    /// Reopens what was written and asks three questions of every inlined
    /// canvas, in order: is the _outlink gone, are the pixels the SAME BYTES
    /// the resolver chose (compared in portable form, so the two archives'
    /// keys cannot fake agreement or disagreement), and do they decode to the
    /// size they declare. Then every OTHER canvas in the rewritten images is
    /// decoded too — the blast radius, not just the repairs.
    /// </summary>
    private static (List<CanvasDirLinkWrite>, int, int, List<string>) Verify(
        string output, WzMapleVersion version, short gameVersion,
        IReadOnlyList<(CanvasDirLinkCase Case, CanvasDirLinkWrite Write)> written,
        CancellationToken token)
    {
        List<CanvasDirLinkWrite> verified = new();
        List<string> failures = new();
        int bystanders = 0, bystandersFailed = 0;

        WzFile back = new(output, gameVersion, version);
        try
        {
            WzFileParseStatus status = back.ParseWzFile();
            if (status != WzFileParseStatus.Success)
            {
                failures.Add($"The inlined archive will not reopen: {status.GetErrorDescription()}. " +
                             "Nothing in it has been verified; do not install it.");
                return (written.Select(w => w.Write).ToList(), 0, 0, failures);
            }

            foreach (IGrouping<string, (CanvasDirLinkCase Case, CanvasDirLinkWrite Write)> byImage in
                     written.GroupBy(p => p.Case.ImagePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                WzImage? image = DonorRestoreService.FindImage(back.WzDirectory, byImage.Key);
                if (image == null)
                {
                    failures.Add($"{byImage.Key} is not in the inlined archive at all.");
                    foreach ((_, CanvasDirLinkWrite w) in byImage) verified.Add(w);
                    continue;
                }
                if (!image.Parsed && !image.ParseImage())
                {
                    failures.Add($"{byImage.Key} will not parse in the inlined archive.");
                    foreach ((_, CanvasDirLinkWrite w) in byImage) verified.Add(w);
                    continue;
                }

                HashSet<string> repaired = new(StringComparer.Ordinal);

                foreach ((CanvasDirLinkCase one, CanvasDirLinkWrite write) in byImage)
                {
                    repaired.Add(one.Inside);

                    if (image.GetFromPath(one.Inside) is not WzCanvasProperty canvas
                        || canvas.PngProperty is not WzPngProperty png)
                    {
                        failures.Add($"{one.CanvasPath}: not a canvas in the inlined archive.");
                        verified.Add(write);
                        continue;
                    }

                    bool linkGone = canvas[WzCanvasProperty.OutlinkPropertyName] == null;
                    if (!linkGone)
                        failures.Add($"{one.CanvasPath}: the _outlink is still on the canvas after the inline.");

                    string writtenHash = "";
                    bool identity = false;
                    try
                    {
                        byte[]? bytes = png.GetCompressedBytesForExtraction(false);
                        if (bytes is { Length: > 0 })
                        {
                            writtenHash = HashBytes(bytes);
                            identity = writtenHash.Equals(write.ExpectedHash, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{one.CanvasPath}: the written pixels could not be read back — {ex.Message}");
                    }
                    if (!identity && writtenHash.Length > 0)
                        failures.Add($"{one.CanvasPath}: came back as different bytes than the resolver chose " +
                                     $"({writtenHash[..12]} vs {write.ExpectedHash[..12]}). Its place is right " +
                                     "and its content is not, which is exactly the substitution this repair " +
                                     "exists to avoid.");

                    bool decoded = false;
                    System.Drawing.Bitmap? bitmap = null;
                    try
                    {
                        bitmap = png.GetImage(false);
                        if (bitmap != null && bitmap.Width == png.Width && bitmap.Height == png.Height)
                            decoded = true;
                        else
                            failures.Add($"{one.CanvasPath}: does not decode to the " +
                                         $"{png.Width}x{png.Height} it declares.");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{one.CanvasPath}: threw while decoding: {ex.Message}");
                    }
                    finally { bitmap?.Dispose(); }

                    verified.Add(write with
                    {
                        WrittenHash = writtenHash,
                        IdentityHeld = identity,
                        LinkGone = linkGone,
                        Decoded = decoded,
                    });
                }

                // The bystanders: everything else in an image this repair
                // caused to be rewritten. Link canvases are skipped, not
                // failed — a canvas whose pixels come from a link is SUPPOSED
                // to have no real block of its own.
                foreach ((string where, WzCanvasProperty canvas) in Canvases(image, ""))
                {
                    token.ThrowIfCancellationRequested();
                    if (repaired.Contains(where)) continue;
                    if (canvas.PngProperty is not WzPngProperty other) continue;
                    if (canvas[WzCanvasProperty.InlinkPropertyName] != null
                        || canvas[WzCanvasProperty.OutlinkPropertyName] != null) continue;
                    if (other.CompressedLength <= 0) continue;

                    System.Drawing.Bitmap? drawn = null;
                    try
                    {
                        drawn = other.GetImage(false);
                        if (drawn != null && drawn.Width == other.Width && drawn.Height == other.Height)
                            bystanders++;
                        else
                        {
                            bystandersFailed++;
                            if (bystandersFailed <= 20)
                                failures.Add($"{byImage.Key}/{where} was not inlined but shares a rewritten " +
                                             "image, and it does not decode to the size it declares.");
                        }
                    }
                    catch (Exception ex)
                    {
                        bystandersFailed++;
                        if (bystandersFailed <= 20)
                            failures.Add($"{byImage.Key}/{where} was not inlined but shares a rewritten " +
                                         $"image, and it threw while decoding: {ex.Message}");
                    }
                    finally { drawn?.Dispose(); }
                }

                try { image.UnparseImage(); } catch { /* nothing left to free */ }
            }
        }
        finally
        {
            try { back.Dispose(); } catch { /* read-only handle */ }
        }

        return (verified, bystanders, bystandersFailed, failures);
    }

    /// <summary>
    /// Re-runs the detection over the target's family with the OUTPUT mounted
    /// in the source's place. Scoped to that family because an inline changes
    /// only the linking canvases in the one archive it wrote — the before
    /// count it is compared against is scoped identically.
    /// </summary>
    private (int, long, List<CanvasDirLinkCase>) ReDetect(
        CanvasDirLinkOptions options, string source, string output, string target, string targetFamily,
        WzMapleVersion version, short gameVersion, CanvasDirLinkScan scan, CancellationToken token)
    {
        List<string> archives = DonorRestoreService.Discover(
                options.Folder, null, options.MinimumArchiveBytes, out _)
            .Where(p => DonorRestoreService.Family(Path.GetFileNameWithoutExtension(p))
                .Equals(targetFamily, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Dictionary<string, CanvasDirLinkCase> why = scan.Cases
            .GroupBy(c => c.CanvasPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<WzFile> family = new();
        try
        {
            foreach (string path in archives)
            {
                bool swap = string.Equals(path, source, StringComparison.OrdinalIgnoreCase);
                WzFile file = new(swap ? output : path, gameVersion, version);
                if (file.ParseWzFile() != WzFileParseStatus.Success) { file.Dispose(); continue; }
                // The mount name must be the one it replaces, or the census
                // reads a family the client does not have.
                file.Name = Path.GetFileName(path);
                family.Add(file);
            }

            Census census = Walk(family, token);
            List<CanvasDirLinkCase> still = census.IntoCanvas
                .Select(r => why.TryGetValue(r.CanvasPath, out CanvasDirLinkCase? known)
                    ? known
                    : new CanvasDirLinkCase(r.Value, r.Family, r.Archive, r.ImagePath, r.Inside, r.CanvasPath,
                        "", "", 0, 0, 0, 0, 0, "", CanvasDirLinkVerdict.Unresolvable,
                        "This link was not in the scan that drove the apply — the archive changed between " +
                        "the two, or the scan was truncated."))
                .OrderBy(c => c.CanvasPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (census.IntoCanvas.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    census.IntoCanvas.Count, still);
        }
        finally
        {
            foreach (WzFile file in family) try { file.Dispose(); } catch { /* read-only */ }
        }
    }

    /* ====================================================================
       THE JUDGEMENT
       ==================================================================== */

    /// <summary>
    /// One verdict per linking canvas, through the identity-aware resolver's
    /// peek — the same choice an apply will make, made without writing, so the
    /// scan reports exactly what the apply will do.
    /// </summary>
    private List<CanvasDirLinkCase> Judge(List<WzFile> open, List<LinkRef> refs, CancellationToken token)
    {
        List<CanvasDirLinkCase> cases = new();
        Dictionary<string, WzLinkResolver> resolvers = Resolvers(open);
        Dictionary<string, WzFile> byStem = open.ToDictionary(
            f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<(string Archive, string ImagePath), LinkRef> byImage in
                 refs.GroupBy(r => (r.Archive, r.ImagePath)))
        {
            token.ThrowIfCancellationRequested();
            lock (_gate) { _progress.Phase = $"judging {byImage.Key.Archive}.wz/{byImage.Key.ImagePath}"; }

            if (!byStem.TryGetValue(byImage.Key.Archive, out WzFile? holder))
                continue;
            WzImage? image = DonorRestoreService.FindImage(holder.WzDirectory, byImage.Key.ImagePath);
            if (image == null) continue;

            bool wasParsed = image.Parsed;
            try
            {
                WzSessionService.EnsureParsed(image);

                foreach (LinkRef one in byImage)
                {
                    CanvasDirLinkCase Case(string stamp, WzCanvasProperty? src, byte[]? bytes,
                                           CanvasDirLinkVerdict verdict, string why)
                    {
                        WzPngProperty? png = src?.PngProperty;
                        return new CanvasDirLinkCase(
                            one.Value, one.Family, one.Archive, one.ImagePath, one.Inside, one.CanvasPath,
                            stamp,
                            src?.FullPath ?? "",
                            png?.Width ?? 0, png?.Height ?? 0, png == null ? 0 : (int)png.Format,
                            png?.Mag ?? 0,
                            bytes?.Length ?? 0,
                            bytes == null ? "" : HashBytes(bytes),
                            verdict, why);
                    }

                    if (image.GetFromPath(one.Inside) is not WzCanvasProperty canvas)
                    {
                        cases.Add(Case("", null, null, CanvasDirLinkVerdict.Unresolvable,
                            "The canvas the walk recorded is no longer reachable at this path."));
                        continue;
                    }

                    string stamp = (canvas["_hash"] as WzStringProperty)?.Value ?? "";

                    if (canvas[WzCanvasProperty.InlinkPropertyName] != null)
                    {
                        cases.Add(Case(stamp, null, null, CanvasDirLinkVerdict.Refused,
                            "The canvas carries an _inlink AS WELL as this _outlink, and the client reads " +
                            "the _inlink first — pixels inlined here would be shadowed by it. Left alone " +
                            "and reported: this is a different wound than a link the client cannot follow."));
                        continue;
                    }

                    if (!resolvers.TryGetValue(one.Family, out WzLinkResolver? resolver))
                    {
                        cases.Add(Case(stamp, null, null, CanvasDirLinkVerdict.Unresolvable,
                            $"The link points into the {one.Family} family and no archive of that family " +
                            "is part of this run. Not checked — include that family to resolve it."));
                        continue;
                    }

                    WzCanvasProperty? chosen = resolver.PeekOutlinkExact(canvas, out byte[]? bytes, out string? detail);
                    if (chosen == null || bytes == null)
                    {
                        cases.Add(Case(stamp, null, null,
                            resolver.LastOutlinkFailureWasRefusal
                                ? CanvasDirLinkVerdict.Refused
                                : CanvasDirLinkVerdict.Unresolvable,
                            detail ?? "the resolver recorded no reason."));
                        continue;
                    }

                    if (chosen.PngProperty is { Mag: not 0 })
                    {
                        cases.Add(Case(stamp, chosen, bytes, CanvasDirLinkVerdict.Refused,
                            $"The art this link names ({chosen.FullPath}) itself carries a non-zero " +
                            $"magnification (format {(int)chosen.PngProperty.Format}, mag " +
                            $"{chosen.PngProperty.Mag}) — the split-format wound. Inlining it would copy " +
                            "the wound. Run the canvas-format repair over that archive first, then this " +
                            "pass over its output; the ledger will chain them."));
                        continue;
                    }

                    cases.Add(Case(stamp, chosen, bytes, CanvasDirLinkVerdict.Inlineable,
                        $"The identity-aware resolver chose exactly one picture: {chosen.FullPath} " +
                        $"({chosen.PngProperty!.Width}x{chosen.PngProperty.Height}, format " +
                        $"{(int)chosen.PngProperty.Format}, {bytes.Length:N0} compressed bytes). Inlining " +
                        "it puts the art where the v232 client looks and drops the link it cannot follow."));
                }
            }
            catch (Exception ex)
            {
                foreach (LinkRef one in byImage)
                {
                    if (cases.Any(c => c.CanvasPath.Equals(one.CanvasPath, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    cases.Add(new CanvasDirLinkCase(
                        one.Value, one.Family, one.Archive, one.ImagePath, one.Inside, one.CanvasPath,
                        "", "", 0, 0, 0, 0, 0, "", CanvasDirLinkVerdict.Unresolvable,
                        $"Reading the image threw: {ex.Message}"));
                }
            }
            finally
            {
                if (!wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                {
                    try { image.UnparseImage(); } catch { /* nothing left to free */ }
                }
            }
        }

        return cases;
    }

    /// <summary>
    /// One identity-aware resolver per archive family, holding that family's
    /// mounted archives in mount order — the resolver's category IS the family.
    /// </summary>
    private static Dictionary<string, WzLinkResolver> Resolvers(List<WzFile> open)
    {
        Dictionary<string, WzLinkResolver> resolvers = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, WzFile> family in open
                     .GroupBy(f => DonorRestoreService.Family(Path.GetFileNameWithoutExtension(f.Name)),
                              StringComparer.OrdinalIgnoreCase))
        {
            WzLinkResolver resolver = new();
            resolver.SetCategoryWzFiles(
                family.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(), family.Key);
            resolvers[family.Key] = resolver;
        }
        return resolvers;
    }

    /* ====================================================================
       THE WALK
       ==================================================================== */

    private sealed record LinkRef(string Archive, string Family, string ImagePath, string Inside,
                                  string CanvasPath, string Value);

    private sealed record LinkRefLite(string Value, string CanvasPath);

    private sealed class Census
    {
        public long Images;
        public long Canvases;
        public long OutlinkCanvases;
        public readonly List<LinkRef> IntoCanvas = new();
    }

    private Census Walk(List<WzFile> open, CancellationToken token)
    {
        Census census = new();
        foreach (WzFile file in open)
        {
            string stem = Path.GetFileNameWithoutExtension(file.Name);
            lock (_gate) { _progress.Archive = stem; _progress.Phase = "reading"; }
            WalkDirectory(file.WzDirectory, stem, DonorRestoreService.Family(stem), "", census, token);
        }
        return census;
    }

    private void WalkDirectory(WzDirectory dir, string stem, string family, string prefix,
                               Census census, CancellationToken token)
    {
        foreach (WzImage image in dir.WzImages)
        {
            token.ThrowIfCancellationRequested();
            string rel = prefix.Length == 0 ? image.Name : prefix + "/" + image.Name;

            census.Images++;
            if ((census.Images & 0x1FF) == 0)
                lock (_gate) { _progress.ImagesDone = census.Images; _progress.CanvasesDone = census.Canvases; }

            bool wasParsed = image.Parsed;
            try
            {
                if (!image.Parsed && !image.Changed && !image.ParseImage()) continue;
                MapleLib.WzLib.WzWalk walk = new();
                foreach (WzImageProperty property in image.WzProperties)
                    Property(property, stem, family, rel, property.Name, census, walk, 0);
            }
            catch (Exception)
            {
                // An image that will not read is the auditor's finding, not
                // this one's. Nothing here can inline into it.
            }
            finally
            {
                if (!wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                {
                    try { image.UnparseImage(); } catch { /* nothing left to free */ }
                }
            }
        }

        foreach (WzDirectory sub in dir.WzDirectories)
            WalkDirectory(sub, stem, family, prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name,
                          census, token);
    }

    private static void Property(WzImageProperty property, string stem, string family, string imagePath,
                                 string inside, Census census, MapleLib.WzLib.WzWalk walk, int depth)
    {
        if (property is WzCanvasProperty canvas)
        {
            census.Canvases++;
            if (canvas[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty outlink
                && !string.IsNullOrEmpty(outlink.Value))
            {
                census.OutlinkCanvases++;
                string value = outlink.Value.Replace('\\', '/').Trim('/');

                // The auditor's own rule, exactly: any segment equal to
                // "_Canvas". A substring test cannot see the prefix-less form
                // (docs/wz-reference-shapes.md §1.1), and this must count the
                // same population the audit counts or the two reports fight.
                string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(s => s.Equals("_Canvas", StringComparison.OrdinalIgnoreCase)))
                {
                    string linkFamily = segments.Length > 0
                                        && !segments[0].Equals("_Canvas", StringComparison.OrdinalIgnoreCase)
                        ? segments[0]
                        : family;
                    census.IntoCanvas.Add(new LinkRef(
                        stem, linkFamily, imagePath, inside,
                        $"{stem}.wz/{imagePath}/{inside}", value));
                }
            }
        }

        WzPropertyCollection? children = walk.Into(property, depth);
        if (children == null) return;
        foreach (WzImageProperty child in children)
            Property(child, stem, family, imagePath, inside + "/" + child.Name, census, walk, depth + 1);
    }

    /* ====================================================================
       PLUMBING
       ==================================================================== */

    /// <summary>Every canvas under a node, with its path relative to the image root.</summary>
    private static IEnumerable<(string Path, WzCanvasProperty Canvas)> Canvases(WzObject node, string prefix)
    {
        MapleLib.WzLib.WzWalk walk = new();
        return Descend(node, prefix, walk, 0);

        static IEnumerable<(string, WzCanvasProperty)> Descend(
            WzObject node, string prefix, MapleLib.WzLib.WzWalk walk, int depth)
        {
            if (node is WzCanvasProperty self && prefix.Length > 0)
                yield return (prefix, self);

            WzPropertyCollection? children = node switch
            {
                WzImage image => image.WzProperties,
                WzImageProperty property => walk.Into(property, depth),
                _ => null,
            };
            if (children == null) yield break;

            foreach (WzImageProperty child in children)
            {
                string here = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                foreach ((string deeper, WzCanvasProperty found) in Descend(child, here, walk, depth + 1))
                    yield return (deeper, found);
            }
        }
    }

    /// <summary>The path above the image an into-_Canvas link names: "Character/Weapon/_Canvas".</summary>
    internal static string DirectoryOf(string value)
    {
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int imgAt = Array.FindIndex(segments, s => s.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
        return imgAt <= 0 ? value : string.Join('/', segments.Take(imgAt));
    }

    /// <summary>The image an into-_Canvas link names, family included: "Skill/_Canvas/50000.img".</summary>
    internal static string ImageOf(string value)
    {
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int imgAt = Array.FindIndex(segments, s => s.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
        return imgAt < 0 ? value : string.Join('/', segments.Take(imgAt + 1));
    }

    private static string HashBytes(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private CancellationTokenSource Begin(string state)
    {
        lock (_gate)
        {
            if (_progress.State is "scanning" or "inlining" or "saving" or "verifying")
                throw new InvalidOperationException("A canvas-directory inline is already running.");
            _cancel = new CancellationTokenSource();
            _progress.State = state;
            _progress.Phase = "";
            _progress.Archive = "";
            _progress.ImagesDone = 0;
            _progress.CanvasesDone = 0;
            _progress.Found = 0;
            _progress.Inlineable = 0;
            _progress.Seconds = 0;
            _progress.Error = null;
            return _cancel;
        }
    }

    private static (WzMapleVersion, short, string) Encryption(List<string> archives, CanvasDirLinkOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MapleVersion)
            && Enum.TryParse(options.MapleVersion, true, out WzMapleVersion told))
            return (told, options.GameVersion != 0 ? options.GameVersion : (short)-1,
                    $"Encryption {told} was supplied by the caller, not detected.");

        // Never detect from a stub: parsing a 6 KB placeholder with each
        // candidate key walks a length field that is noise and overflows the
        // stack, which cannot be caught.
        const long MinimumRealArchive = 1L << 20;
        string probe = archives.Where(a => new FileInfo(a).Length >= MinimumRealArchive)
                               .OrderBy(a => new FileInfo(a).Length)
                               .FirstOrDefault()
                       ?? archives.OrderByDescending(a => new FileInfo(a).Length).First();

        WzMapleVersion version = WzTool.DetectMapleVersion(probe, out short gameVersion);
        return (version, gameVersion,
                $"Encryption {version} and game version {gameVersion} detected from {Path.GetFileName(probe)}.");
    }

    /// <summary>
    /// The command that installs an output, backup first. The ledger's origin
    /// is the file at the head of the chain, so it is what the whole chain is
    /// installed as — naming the source instead printed a command for a file
    /// the client does not have, once repairs compose.
    /// </summary>
    private static string InstallCommand(string installAs, string output)
    {
        string name = Path.GetFileName(installAs);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        string live = @"C:\MapleStory\232\" + name;
        return
            $"Copy-Item -LiteralPath '{live}' -Destination 'C:\\MapleStory\\232\\{Path.GetFileNameWithoutExtension(name)}_beforeCanvasDirInline_{stamp}.wz'; " +
            $"if ($?) {{ Copy-Item -LiteralPath '{output}' -Destination '{live}' -Force }}";
    }
}
