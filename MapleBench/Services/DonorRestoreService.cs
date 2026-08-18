using System.Diagnostics;
using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/* ============================================================================
   DONOR RESTORE — putting back art a port deleted, from an archive that still
   has it

   WHAT HAPPENED

   A canvas can hold no pixels of its own and instead carry an `_outlink`: a
   string naming, from the archive family's root, the node whose art it draws.
   `Skill/000.img/skill/0008003/affected/0` is such a string. The client opens
   the Skill family, finds `000.img`, walks to `skill/0008003/affected/0` and
   draws what it finds there.

   On the user's client 347 distinct `_outlink` values name nothing at all, and
   1,698 canvases carry those 347 strings. Every one of them draws nothing, in
   silence: the archive opens, the tree looks right, the editor is happy.

   This is NOT the same wound as the split canvas format, though it was made by
   the same write. A split format is a canvas that WAS written, with a
   mislabelled field over intact pixels — repairable by arithmetic. A dangling
   outlink is a subtree that is ABSENT. No amount of arithmetic produces pixels
   that are not there. The only honest repair is to find them somewhere else.

   THE EVIDENCE THAT THEY WERE DELETED RATHER THAN NEVER WRITTEN

   `Skill.wz/000.img/skill/0008003` has no `affected` node at all. The
   pre-damage `Skill_old.wz` (19 Feb 2026, the day the client shipped) holds
   `affected/0..6` in full — precisely the seven the outlinks name. A port
   REPLACED `000.img/skill/0008003` with different-generation art (the donor's
   `effect/0` is 79x165 format 1; the live one is 12x236 DXT5), writing the new
   canvases while dropping `affected` and leaving the canvases that pointed at
   it behind as 1x1 placeholders still carrying their links.

   WHAT THIS DOES, AND THE THREE RULES IT IS WRITTEN TO

   1. **Restore by identity, not by name.** For every dangling link, the node
      the link NAMES is looked up in a donor, and what comes back is recorded by
      its <see cref="WzContentHasher"/> digest — before the write, and again on
      the saved and reopened archive. A restore is counted only when those two
      digests are equal. "A node of that name is now there" is exactly the claim
      that let a port substitute the wrong generation's art in the first place.

   2. **Where the donor and the live client disagree in generation, that is a
      decision to surface, not one to make.** The live client's `0008003` still
      holds an `effect` and an `icon`; the donor holds its own, and they are
      different art. Restoring the donor's `affected` beside the live `effect`
      puts two generations of one skill's animation in the same node. Measured
      on this client: 27 of the 33 places a restore would land have at least one
      surviving sibling whose content differs from the donor's. Those are
      reported as Conflicted and are NOT written without
      <see cref="DonorRestoreOptions.AcceptGenerationMismatch"/>. The live art
      is newer; the donor art is what the links expect; neither of those facts
      settles it, and this code is not entitled to.

   3. **A dangling link that cannot be restored is reported, never deleted.**
      Deleting the link would make the audit green and leave the skill just as
      silent — which is precisely the failure mode this codebase exists to
      remove. There is no branch here that removes anything.

   WHAT IT WILL NOT DO

   - It does not write to the archive it read. Apply produces a new file beside
     the source and hands back the command to install it, backup first. Nothing
     here installs anything.
   - It does not carry a donor node that itself carries an `_inlink` or an
     `_outlink`. That would land a fresh dangling link and report it as a
     repair. Refused and reported instead.
   - It does not invent a target. If no archive in the live family holds the
     image the link names, the restore has nowhere to go and says so, rather
     than creating an image and hoping the mount order agrees.
   ============================================================================ */

/// <summary>What can be done about one dangling link.</summary>
public enum DonorRestoreVerdict
{
    /// <summary>A donor holds the named node and nothing around it disagrees. Written.</summary>
    Restorable = 0,

    /// <summary>
    /// A donor holds it, but a surviving sibling of the place it would land is
    /// different content in the donor and in the live client — the two are
    /// different generations of the same node. Written only on an explicit
    /// acknowledgement.
    /// </summary>
    Conflicted = 1,

    /// <summary>No donor holds it, or there is nowhere in the live family to put it. Reported.</summary>
    Unrestorable = 2,

    // There is deliberately no fourth verdict. "Restorable into an archive this
    // apply is not writing" is a property of the RUN, not of the case, and it is
    // reported as DonorRestoreResult.Deferred — a case that would silently
    // change its own verdict depending on which archive was named would be the
    // ambiguous zero this codebase keeps finding.
}

/// <summary>
/// One dangling <c>_outlink</c> value, what is known about restoring it, and
/// every number that verdict rests on.
/// </summary>
public sealed record DonorRestoreCase(
    string Link,
    string Family,
    string ImagePath,
    string PropPath,
    long Canvases,
    string ExampleCanvas,
    string TargetArchive,
    string Donor,
    string DonorHash,
    string DonorShape,
    string LandsUnder,
    int SiblingsSame,
    int SiblingsDiffer,
    IReadOnlyList<string> DifferingSiblings,
    DonorRestoreVerdict Verdict,
    string Why)
{
    /// <summary>
    /// The skill this link belongs to — the unit a generation choice is made in.
    ///
    /// A skill's frames are one decision: accepting `affected/0` and rejecting
    /// `affected/1` of the same skill would mix generations INSIDE one
    /// animation, which is strictly worse than either whole choice. The id is
    /// the segment after `skill` in the property path (`skill/0008003/affected/0`
    /// -> `0008003`); a link that is not skill-shaped falls back to its image
    /// and first path segment, so it still groups with its own siblings and
    /// never silently joins somebody else's.
    /// </summary>
    public string SkillId => DonorRestoreService.SkillIdOf(ImagePath, PropPath);
}

/// <summary>A donor archive as this run read it, and how much of the damage it covers.</summary>
public sealed record DonorArchiveReport(
    string Name,
    string Path,
    long Bytes,
    DateTimeOffset ModifiedUtc,
    string Encryption,
    short GameVersion,
    int Images,
    string ParseStatus,
    int Satisfies,
    long SatisfiesCanvases,
    IReadOnlyList<string> Notes);

/// <summary>What a read-only scan found. Nothing has been written.</summary>
public sealed record DonorRestoreScan(
    string Folder,
    DateTimeOffset StartedUtc,
    double Seconds,
    IReadOnlyList<string> Archives,
    IReadOnlyList<DonorArchiveReport> Donors,
    long Images,
    long Canvases,
    long CanvasesWithOutlink,
    int DistinctOutlinks,
    int Resolved,
    int IntoCanvasDirectory,
    int OtherFamily,
    int Malformed,
    int Dangling,
    long DanglingCanvases,
    int Restorable,
    int Conflicted,
    int Unrestorable,
    long RestorableCanvases,
    long ConflictedCanvases,
    long UnrestorableCanvases,
    IReadOnlyList<string> TargetArchives,
    IReadOnlyList<DonorRestoreCase> Cases,
    IReadOnlyList<string> Notes);

/// <summary>One node put back, with the identity it had at both ends.</summary>
public sealed record DonorRestoreWrite(
    string Path,
    string Donor,
    string DonorHash,
    string WrittenHash,
    bool IdentityHeld,
    bool Decoded,
    long CanvasesFreed,
    string Note);

/// <summary>What an apply did, measured against the file it wrote and reopened.</summary>
public sealed record DonorRestoreResult(
    string Source,
    string Output,
    double Seconds,
    int Considered,
    int Written,
    int Refused,
    int Deferred,
    int IdentityHeld,
    int IdentityLost,
    int Decoded,
    int FailedToDecode,
    int BystandersDecoded,
    int BystandersFailed,
    int DanglingBefore,
    int DanglingAfter,
    long DanglingCanvasesBefore,
    long DanglingCanvasesAfter,
    long SourceBytes,
    long OutputBytes,
    long CarriedBytes,
    long CanvasesBefore,
    long CanvasesAfter,
    long ImagesBefore,
    long ImagesAfter,
    IReadOnlyList<DonorRestoreWrite> Writes,
    IReadOnlyList<DonorRestoreCase> StillDangling,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Notes,
    string InstallCommand,
    RepairInputCheck? Input = null,
    RepairLedgerFile? Ledger = null);

public sealed class DonorRestoreOptions
{
    /// <summary>The live client folder. Its mounted archives are the client this repairs.</summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// Pre-damage archives to take the missing nodes from, in preference order.
    /// A donor is consulted only for what a link NAMES; nothing is taken from
    /// one because it happens to be there.
    /// </summary>
    public string[] Donors { get; set; } = Array.Empty<string>();

    /// <summary>Archive family to examine — "Skill". Empty means every family in the folder.</summary>
    public string? Family { get; set; }

    /// <summary>
    /// The `_outlink` values the auditor reported as `outlink.unresolved`, when
    /// the caller has a report to drive this from. Empty means "find them here".
    /// Either way the family is walked, because the canvas counts and the
    /// example paths come from the walk — and a link the auditor named that this
    /// scan finds resolvable, or the other way round, is said out loud rather
    /// than quietly reconciled.
    /// </summary>
    public string[]? Links { get; set; }

    /// <summary>Encryption, when known — "BMS", "GMS", "EMS". Empty means detect.</summary>
    public string? MapleVersion { get; set; }

    /// <summary>Patch version, when known. 0 means detect it with the encryption.</summary>
    public short GameVersion { get; set; }

    /// <summary>How many cases to return per verdict. The counts are always complete.</summary>
    public int MaxCases { get; set; } = 500;

    /// <summary>
    /// Which live archive an apply writes a new copy of. Empty means "the one
    /// every restorable case lands in", and an apply refuses when they land in
    /// more than one rather than picking.
    /// </summary>
    public string? TargetArchive { get; set; }

    /// <summary>Where an apply writes. Empty means "&lt;target&gt;.restored.wz" beside it.</summary>
    public string? Output { get; set; }

    /// <summary>An apply refuses without this.</summary>
    public bool Confirm { get; set; }

    /// <summary>
    /// Archives smaller than this are named in the notes and never opened.
    ///
    /// It is a real hazard and not a tidiness rule: `Base.wz`, `Data.wz` and
    /// `TamingMob.wz` in the user's client are 6 KB placeholders, and handing
    /// one to the directory parser walks a length field that is noise until the
    /// stack is gone — an uncatchable crash that killed the process twice while
    /// the auditor was being built. It is settable because a legitimately small
    /// archive exists (every fixture in this repo's tests is one), and a caller
    /// who knows what it is pointing at should be able to say so. Whatever it is
    /// set to, what was skipped is reported rather than quietly dropped.
    /// </summary>
    public long MinimumArchiveBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Write the Conflicted cases too — the ones where the donor's node and the
    /// live client's surviving siblings are different generations of the same
    /// thing. A separate switch from <see cref="Confirm"/> because it answers a
    /// different question: Confirm asks whether a caller meant to write at all,
    /// this asks whether the reader has seen the disagreement and chosen.
    /// </summary>
    public bool AcceptGenerationMismatch { get; set; }

    /// <summary>
    /// The per-skill form of <see cref="AcceptGenerationMismatch"/>: write the
    /// Conflicted cases whose <see cref="DonorRestoreCase.SkillId"/> is in this
    /// set, and leave the rest dangling and reported.
    ///
    /// This is the switch the 261-case judgement actually wants. All-or-nothing
    /// forces the user to trade every mixed-generation skill against every
    /// dangling link at once; a set lets each skill be decided by looking at
    /// it. Non-null means the caller HAS chosen — an EMPTY set is a real answer
    /// ("none of them") and is honoured as such, which is why the null check is
    /// on the property and not on its length. The boolean, when true, still
    /// accepts everything and says so; ids that match no conflicted case are
    /// named in the notes rather than silently ignored.
    /// </summary>
    public string[]? AcceptGenerationMismatchFor { get; set; }

    /// <summary>
    /// Write even though another kind of repair has already built a whole
    /// archive from this exact input.
    ///
    /// Separate from <see cref="Confirm"/> for the reason
    /// <see cref="AcceptGenerationMismatch"/> is: it answers a different
    /// question. Both outputs are complete copies of the source, so installing
    /// the second silently reverts the first; the composing answer is to run
    /// this pass against the other pass's output, and <see cref="RepairLedger"/>
    /// names it. This switch says the caller wants a separate variant anyway and
    /// knows only one of them can be installed.
    /// </summary>
    public bool AcceptSeparateRepairs { get; set; }
}

public sealed class DonorRestoreProgress
{
    public string State { get; set; } = "idle";     // idle | scanning | restoring | saving | verifying | done | failed | cancelled
    public string Phase { get; set; } = "";
    public string Archive { get; set; } = "";
    public long ImagesDone { get; set; }
    public long CanvasesDone { get; set; }
    public int Dangling { get; set; }
    public int Restorable { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }
}

public sealed class DonorRestoreService
{
    private readonly WarmupService _warmup;
    private readonly object _gate = new();
    private readonly DonorRestoreProgress _progress = new();
    private CancellationTokenSource? _cancel;
    private DonorRestoreScan? _scan;
    private DonorRestoreResult? _result;

    /// <summary>Mounted-looking archive names: a stem and an optional trailing number.</summary>
    private static readonly Regex MountedName = new(@"^([A-Za-z]+?)(\d*)$", RegexOptions.Compiled);

    public DonorRestoreService(WarmupService warmup) => _warmup = warmup;

    public DonorRestoreProgress Snapshot()
    {
        lock (_gate)
            return new DonorRestoreProgress
            {
                State = _progress.State,
                Phase = _progress.Phase,
                Archive = _progress.Archive,
                ImagesDone = _progress.ImagesDone,
                CanvasesDone = _progress.CanvasesDone,
                Dangling = _progress.Dangling,
                Restorable = _progress.Restorable,
                Seconds = _progress.Seconds,
                Error = _progress.Error,
            };
    }

    /// <summary>Whether a run is in flight. Advisory: <see cref="StartScan"/> is the race-free ask.</summary>
    public bool Busy
    {
        get { lock (_gate) return _progress.State is "scanning" or "restoring" or "saving" or "verifying"; }
    }

    public DonorRestoreScan? LastScan() { lock (_gate) return _scan; }
    public DonorRestoreResult? LastResult() { lock (_gate) return _result; }
    public void Cancel() { lock (_gate) _cancel?.Cancel(); }

    /* ====================================================================
       SCAN
       ==================================================================== */

    /// <summary>
    /// Reads. Writes nothing, opens nothing for writing, and leaves every
    /// archive it touched exactly as it found it.
    /// </summary>
    public DonorRestoreScan Scan(DonorRestoreOptions options) => RunScan(Begin("scanning"), options);

    /// <summary>
    /// Reserves the service SYNCHRONOUSLY and then scans on a background thread.
    ///
    /// The reservation has to happen on the caller's thread. An endpoint that
    /// fires <see cref="Scan"/> into a <c>Task.Run</c> and returns a snapshot
    /// gets, when another run is already in flight, a refusal thrown inside the
    /// task where nothing sees it — and hands back the OTHER run's progress,
    /// which reads exactly like the request having been accepted. That is the
    /// silent no-op this codebase keeps finding, and it was found here by
    /// driving the endpoints rather than by reading them.
    /// </summary>
    public DonorRestoreProgress StartScan(DonorRestoreOptions options)
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

    private DonorRestoreScan RunScan(CancellationTokenSource cancel, DonorRestoreOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        try
        {
            DonorRestoreScan scan = ScanCore(options, cancel.Token, clock, started);
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

    private DonorRestoreScan ScanCore(DonorRestoreOptions options, CancellationToken token,
                                      Stopwatch clock, DateTimeOffset started)
    {
        List<string> archives = Discover(options.Folder, options.Family, options.MinimumArchiveBytes,
                                         out List<string> skipped);
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
                      $"({string.Join(", ", skipped)}). Nothing in them was examined, and a link into one of " +
                      "them is unchecked rather than broken.");

        List<WzFile> family = new();
        try
        {
            foreach (string path in archives)
            {
                WzFile file = new(path, gameVersion, version);
                WzFileParseStatus status = file.ParseWzFile();
                if (status != WzFileParseStatus.Success)
                {
                    notes.Add($"{Path.GetFileName(path)} could not be opened ({status.GetErrorDescription()}); " +
                              "nothing in it was examined, and any link into it is unchecked rather than broken.");
                    file.Dispose();
                    continue;
                }
                family.Add(file);
            }
            if (family.Count == 0)
                throw new InvalidOperationException("Every archive in the family failed to open.");

            Census census = Walk(family, token);
            notes.Add($"{census.Canvases:N0} canvases in {census.Images:N0} images. " +
                      $"{census.OutlinkCanvases:N0} of them carry an _outlink, naming " +
                      $"{census.Outlinks.Count:N0} distinct targets.");

            Resolution resolution = Resolve(family, census.Outlinks, token);
            notes.Add($"{resolution.Resolved:N0} of those targets resolve. " +
                      $"{resolution.IntoCanvasDirectory:N0} point into a _Canvas directory (a separate " +
                      "finding — the archive is not missing anything, this client just cannot follow " +
                      $"them), {resolution.OtherFamily:N0} point at a family this run did not include, " +
                      $"{resolution.Malformed:N0} name no .img at all, and {resolution.Dangling.Count:N0} " +
                      "resolve to nothing.");

            /* Driven by the auditor when the caller has a report. The two lists
               are reconciled out loud: a link the auditor called broken that
               resolves here, or one this scan finds that the auditor did not
               name, means the two disagree about the client and the reader
               should know which before anything is written. */
            List<Dangling> dangling = resolution.Dangling;
            if (options.Links is { Length: > 0 })
            {
                HashSet<string> asked = options.Links
                    .Select(l => l.Replace('\\', '/').Trim('/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> found = dangling.Select(d => d.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

                int alsoFound = dangling.Count(d => asked.Contains(d.Value));
                int askedButResolves = asked.Count(a => !found.Contains(a));
                int foundButNotAsked = dangling.Count - alsoFound;

                notes.Add($"Driven by {asked.Count:N0} link(s) the auditor reported as outlink.unresolved. " +
                          $"{alsoFound:N0} of them are dangling here too. " +
                          (askedButResolves > 0
                              ? $"{askedButResolves:N0} resolve in this scan and were dropped — the report is " +
                                "older than the client, or was run over a different set of archives. "
                              : "") +
                          (foundButNotAsked > 0
                              ? $"{foundButNotAsked:N0} further dangling links were found that the report does " +
                                "not name; those are NOT acted on here, because the caller asked for a list."
                              : ""));

                dangling = dangling.Where(d => asked.Contains(d.Value)).ToList();
            }

            lock (_gate) _progress.Dangling = dangling.Count;

            /* The donors. Opened after the damage is known, so a donor is only
               ever asked for a node some link actually names. */
            List<DonorArchiveReport> donorReports = new();
            List<DonorRestoreCase> cases;
            List<WzFile> donors = new();
            try
            {
                foreach (string donorPath in options.Donors ?? Array.Empty<string>())
                {
                    token.ThrowIfCancellationRequested();
                    string full = Path.GetFullPath(donorPath);
                    if (!File.Exists(full))
                    {
                        donorReports.Add(new DonorArchiveReport(
                            Path.GetFileName(full), full, 0, default, version.ToString(), gameVersion, 0,
                            "missing", 0, 0, new[] { "There is no file at this path. Nothing was taken from it." }));
                        continue;
                    }

                    FileInfo info = new(full);
                    WzFile donor = new(full, gameVersion, version);
                    WzFileParseStatus status = donor.ParseWzFile();
                    int images = status == WzFileParseStatus.Success ? CountImages(donor.WzDirectory) : 0;
                    List<string> donorNotes = new();

                    if (status != WzFileParseStatus.Success)
                    {
                        donorNotes.Add($"Will not open: {status.GetErrorDescription()}. " +
                                       "Nothing was taken from it — check the encryption.");
                        donor.Dispose();
                    }
                    else
                    {
                        donors.Add(donor);
                        DateTimeOffset newest = family
                            .Select(f => (DateTimeOffset)new FileInfo(f.FilePath).LastWriteTimeUtc)
                            .DefaultIfEmpty(default)
                            .Max();
                        if (info.LastWriteTimeUtc >= newest.UtcDateTime)
                            donorNotes.Add("This donor is NOT older than every archive in the live family. " +
                                           "It may not predate the damage; check before accepting anything from it.");
                    }

                    donorReports.Add(new DonorArchiveReport(
                        Path.GetFileNameWithoutExtension(full), full, info.Length,
                        info.LastWriteTimeUtc, version.ToString(), gameVersion, images,
                        status.ToString(), 0, 0, donorNotes));
                }

                cases = Judge(family, donors, dangling, token);
            }
            finally
            {
                foreach (WzFile donor in donors)
                    try { donor.Dispose(); } catch { /* read-only handle */ }
            }

            // Fill each donor's coverage in, now that the cases know who served what.
            for (int i = 0; i < donorReports.Count; i++)
            {
                DonorArchiveReport report = donorReports[i];
                List<DonorRestoreCase> served = cases
                    .Where(c => c.Donor.Equals(report.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                donorReports[i] = report with
                {
                    Satisfies = served.Count,
                    SatisfiesCanvases = served.Sum(c => c.Canvases),
                };
            }

            int restorable = cases.Count(c => c.Verdict == DonorRestoreVerdict.Restorable);
            int conflicted = cases.Count(c => c.Verdict == DonorRestoreVerdict.Conflicted);
            int unrestorable = cases.Count(c => c.Verdict == DonorRestoreVerdict.Unrestorable);
            lock (_gate) _progress.Restorable = restorable + conflicted;

            notes.AddRange(Conclusions(cases, donorReports));

            return new DonorRestoreScan(
                options.Folder, started, clock.Elapsed.TotalSeconds,
                archives.Select(a => Path.GetFileName(a) ?? "").ToList(),
                donorReports,
                census.Images, census.Canvases, census.OutlinkCanvases,
                census.Outlinks.Count, resolution.Resolved, resolution.IntoCanvasDirectory,
                resolution.OtherFamily, resolution.Malformed,
                dangling.Count, dangling.Sum(d => d.Refs.Count),
                restorable, conflicted, unrestorable,
                cases.Where(c => c.Verdict == DonorRestoreVerdict.Restorable).Sum(c => c.Canvases),
                cases.Where(c => c.Verdict == DonorRestoreVerdict.Conflicted).Sum(c => c.Canvases),
                cases.Where(c => c.Verdict == DonorRestoreVerdict.Unrestorable).Sum(c => c.Canvases),
                cases.Where(c => c.TargetArchive.Length > 0)
                     .Select(c => c.TargetArchive).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
                cases.GroupBy(c => c.Verdict)
                     .SelectMany(g => g.Take(Math.Max(1, options.MaxCases)))
                     .OrderBy(c => (int)c.Verdict).ThenBy(c => c.Link, StringComparer.OrdinalIgnoreCase)
                     .ToList(),
                notes);
        }
        finally
        {
            foreach (WzFile file in family)
                try { file.Dispose(); } catch { /* read-only handle */ }
        }
    }

    /* ====================================================================
       APPLY
       ==================================================================== */

    /// <summary>
    /// Puts the named nodes back, writes a NEW archive, reopens it from disk,
    /// and then asks the three questions that matter in order: is the node
    /// there, is it the SAME CONTENT the donor had, and do its pixels decode.
    ///
    /// The source archive is opened read-only and is not written to under any
    /// argument. There is no in-place branch to reach.
    /// </summary>
    public DonorRestoreResult Apply(DonorRestoreOptions options)
    {
        Confirmed(options);
        return RunApply(Begin("restoring"), options);
    }

    /// <summary>Reserves synchronously, then applies on a background thread. See <see cref="StartScan"/>.</summary>
    public DonorRestoreProgress StartApply(DonorRestoreOptions options)
    {
        Confirmed(options);
        CancellationTokenSource cancel = Begin("restoring");
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

    private static void Confirmed(DonorRestoreOptions options)
    {
        if (!options.Confirm)
            throw new InvalidOperationException(
                "A restore writes a new archive the size of the source. Pass confirm=true.");
    }

    private DonorRestoreResult RunApply(CancellationTokenSource cancel, DonorRestoreOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            DonorRestoreResult result = ApplyCore(options, cancel.Token, clock);
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

    private DonorRestoreResult ApplyCore(DonorRestoreOptions options, CancellationToken token, Stopwatch clock)
    {
        // The scan is the plan. Running it here rather than trusting a cached
        // one means the archive an apply writes is the archive an apply looked
        // at — the whole "report what is, not what was intended" rule.
        lock (_gate) { _progress.State = "scanning"; _progress.Phase = "finding"; }
        DonorRestoreScan scan = ScanCore(options, token, clock, DateTimeOffset.UtcNow);
        lock (_gate) { _scan = scan; _progress.State = "restoring"; }

        List<string> notes = new(scan.Notes);
        List<string> failures = new();

        List<DonorRestoreCase> wanted = scan.Cases
            .Where(c => c.Verdict is DonorRestoreVerdict.Restorable or DonorRestoreVerdict.Conflicted)
            .ToList();

        if (scan.Cases.Count > scan.Dangling)
            throw new InvalidOperationException(
                "The scan truncated its case list, so an apply driven by it would write less than it " +
                "found. Raise MaxCases.");
        if (wanted.Count + scan.Unrestorable != scan.Dangling)
            notes.Add($"MaxCases held back {scan.Dangling - scan.Cases.Count:N0} case(s) from the report. " +
                      "An apply writes only what it can list, so raise MaxCases before trusting the totals.");

        string[] targets = wanted.Select(c => c.TargetArchive)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string target = options.TargetArchive ?? "";
        if (target.Length == 0)
        {
            if (targets.Length == 0)
                throw new InvalidOperationException(
                    "Nothing here can be restored into any archive: " +
                    $"{scan.Dangling:N0} dangling link(s), {scan.Unrestorable:N0} of them with no donor. " +
                    "Read the scan; there is nothing for an apply to do.");
            if (targets.Length > 1)
                throw new InvalidOperationException(
                    "These restores land in more than one archive (" + string.Join(", ", targets) + "). " +
                    "An apply writes one new archive, so name which with targetArchive and run it once " +
                    "per archive — rather than this picking one and reporting the rest as done.");
            target = targets[0];
        }

        string source = Path.GetFullPath(Path.Combine(options.Folder, target + ".wz"));
        if (!File.Exists(source))
            throw new InvalidOperationException($"{source} is not a file.");

        string output = string.IsNullOrWhiteSpace(options.Output)
            ? Path.Combine(Path.GetDirectoryName(source) ?? ".",
                           Path.GetFileNameWithoutExtension(source) + ".restored.wz")
            : Path.GetFullPath(options.Output!);

        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The restore writes a new archive; it never edits the one it read. " +
                "Give an output path that is not the source.");

        /* What has already been done to this file, by content rather than by
           path. A ledger that does not describe these bytes, or another pass
           that has already written a whole archive from this same input, stops
           the run here — see RepairLedger for the accident that is. */
        RepairInputCheck input = RepairLedger.Inspect(source, RepairLedger.DonorRestorePass);
        if (input.Verdict == RepairInputVerdict.Stale)
            throw new InvalidOperationException(input.Why);
        if (input.ConflictsWith.Count > 0 && !options.AcceptSeparateRepairs)
            throw new InvalidOperationException(input.Why);
        notes.Add(input.Why);

        List<DonorRestoreCase> mine = wanted
            .Where(c => c.TargetArchive.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int deferred = wanted.Count - mine.Count;

        List<DonorRestoreCase> conflicted = mine
            .Where(c => c.Verdict == DonorRestoreVerdict.Conflicted).ToList();
        List<DonorRestoreCase> conflictedTaken = conflicted;
        if (conflicted.Count > 0 && !options.AcceptGenerationMismatch)
        {
            if (options.AcceptGenerationMismatchFor is { } chosen)
            {
                /* The per-skill choice. Trimmed and matched case-insensitively,
                   because these ids travel through a browser; and reconciled out
                   loud in both directions — a chosen id nothing carries, and the
                   skills that were NOT chosen — so the ledger can say afterwards
                   exactly which judgement produced this archive. */
                HashSet<string> accepted = chosen
                    .Select(id => id.Trim())
                    .Where(id => id.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                conflictedTaken = conflicted.Where(c => accepted.Contains(c.SkillId)).ToList();
                HashSet<string> conflictedIds = conflicted.Select(c => c.SkillId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<string> takenIds = conflictedTaken.Select(c => c.SkillId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> leftIds = conflicted.Where(c => !accepted.Contains(c.SkillId))
                    .Select(c => c.SkillId).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> unmatched = accepted.Where(id => !conflictedIds.Contains(id))
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

                notes.Add($"Generation choice, per skill: of {conflicted.Count:N0} conflicted restore(s) " +
                          $"across {conflictedIds.Count:N0} skill(s), the caller accepted " +
                          $"{takenIds.Count:N0} skill(s) covering {conflictedTaken.Count:N0} restore(s)" +
                          (takenIds.Count > 0 ? $" ({string.Join(", ", takenIds)})" : "") +
                          $" and left {leftIds.Count:N0} skill(s) covering " +
                          $"{conflicted.Count - conflictedTaken.Count:N0}" +
                          (leftIds.Count > 0 ? $" ({string.Join(", ", leftIds)})" : "") +
                          ". The rejected skills' links stay dangling and stay reported — rejecting a " +
                          "restore never deletes the link that wanted it.");
                if (unmatched.Count > 0)
                    notes.Add($"{unmatched.Count:N0} accepted skill id(s) match no conflicted case in this " +
                              $"run ({string.Join(", ", unmatched)}). Nothing extra was written for them — " +
                              "either the id is mistyped or the scan that produced the choice saw a " +
                              "different client than this apply does.");

                HashSet<DonorRestoreCase> keep = conflictedTaken.ToHashSet();
                mine = mine.Where(c => c.Verdict == DonorRestoreVerdict.Restorable || keep.Contains(c))
                           .ToList();
            }
            else
            {
                conflictedTaken = new List<DonorRestoreCase>();
                mine = mine.Where(c => c.Verdict == DonorRestoreVerdict.Restorable).ToList();
                notes.Add($"{conflicted.Count:N0} restore(s) were NOT written. At each of them a node that " +
                          "survived in the live client is different content in the donor — the two are " +
                          "different generations of the same thing, and putting the donor's node back beside " +
                          "the live one mixes them. The live art is newer; the donor art is what the links " +
                          "expect. Pass acceptGenerationMismatch=true to take the donor's, name the skills " +
                          "with acceptGenerationMismatchFor to choose per skill, or leave them: the links " +
                          "stay dangling and stay reported.");
            }
        }

        (WzMapleVersion version, short gameVersion, _) =
            Encryption(new List<string> { source }, options);

        List<(DonorRestoreCase Case, DonorRestoreWrite Write)> written = new();
        long sourceBytes = new FileInfo(source).Length;
        long carriedBytes = 0;

        WzFile file = new(source, gameVersion, version);
        List<WzFile> donors = new();
        try
        {
            WzFileParseStatus status = file.ParseWzFile();
            if (status != WzFileParseStatus.Success)
                throw new InvalidOperationException($"{target}.wz could not be opened: {status.GetErrorDescription()}");

            foreach (string donorPath in options.Donors ?? Array.Empty<string>())
            {
                if (!File.Exists(donorPath)) continue;
                WzFile donor = new(Path.GetFullPath(donorPath), gameVersion, version);
                if (donor.ParseWzFile() == WzFileParseStatus.Success) donors.Add(donor);
                else donor.Dispose();
            }

            HashSet<WzImage> changed = new();

            // What this run is putting back, per image, so a note about what was
            // left behind can be about what was actually left behind.
            Dictionary<string, HashSet<string>> restoring = new(StringComparer.OrdinalIgnoreCase);
            foreach (DonorRestoreCase one in mine)
            {
                if (!restoring.TryGetValue(one.ImagePath, out HashSet<string>? paths))
                    restoring[one.ImagePath] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                paths.Add(one.PropPath);
            }

            lock (_gate) { _progress.Phase = "carrying"; }

            foreach (DonorRestoreCase one in mine)
            {
                token.ThrowIfCancellationRequested();

                WzFile? donor = donors.FirstOrDefault(
                    d => Path.GetFileNameWithoutExtension(d.Name)
                          .Equals(one.Donor, StringComparison.OrdinalIgnoreCase));
                WzImage? donorImage = donor == null ? null : FindImage(donor.WzDirectory, one.ImagePath);
                WzImage? liveImage = FindImage(file.WzDirectory, one.ImagePath);

                if (donor == null || donorImage == null || liveImage == null)
                {
                    failures.Add($"{one.Link}: the donor or the target image is no longer reachable in the " +
                                 "apply pass. Nothing was written for it.");
                    continue;
                }

                if (!donorImage.Parsed && !donorImage.ParseImage())
                {
                    failures.Add($"{one.Link}: the donor's {one.ImagePath} will not parse.");
                    continue;
                }
                WzSessionService.EnsureParsed(liveImage);

                try
                {
                    string donorHash = Carry(donorImage, liveImage, one.PropPath,
                                             restoring[one.ImagePath], out string note, out long moved);
                    carriedBytes += moved;
                    changed.Add(liveImage);
                    written.Add((one, new DonorRestoreWrite(
                        $"{target}.wz/{one.ImagePath}/{one.PropPath}", one.Donor, donorHash, "",
                        false, false, one.Canvases, note)));
                }
                catch (Exception ex)
                {
                    failures.Add($"{one.Link}: {ex.Message}");
                }
            }

            if (written.Count == 0)
            {
                notes.Add("Nothing was written. That is a result, not a no-op: " +
                          $"{scan.Dangling:N0} dangling link(s) were found and none of them was both " +
                          "restorable and accepted.");
                DonorRestoreResult empty = new(
                    source, "", clock.Elapsed.TotalSeconds, scan.Dangling, 0,
                    scan.Unrestorable + conflicted.Count - conflictedTaken.Count, deferred, 0, 0, 0, 0, 0, 0,
                    scan.Dangling, scan.Dangling, scan.DanglingCanvases, scan.DanglingCanvases,
                    sourceBytes, 0, 0, scan.Canvases, scan.Canvases, scan.Images, scan.Images,
                    Array.Empty<DonorRestoreWrite>(),
                    scan.Cases.Where(c => c.Verdict != DonorRestoreVerdict.Restorable).ToList(),
                    failures, notes, "", input, input.Ledger);
                return empty;
            }

            foreach (WzImage image in changed) image.Changed = true;

            lock (_gate) { _progress.State = "saving"; _progress.Phase = "saving"; }

            string temp = output + ".partial";
            if (File.Exists(temp)) File.Delete(temp);

            /* UNKNOWN and no override IV, so only the images that were written
               into are re-serialised and every other image travels as the bytes
               it already was. Naming a version or pinning an IV marks every
               image changed, which puts lossy round-trip risk on art nobody
               touched — see the canvas-format repair for the same decision and
               the measurement behind it. */
            file.SaveToDisk(temp, null, WzMapleVersion.UNKNOWN, null);
            file.Dispose();

            if (File.Exists(output)) File.Delete(output);
            File.Move(temp, output);

            notes.Add($"{written.Count:N0} node(s) carried into {changed.Count:N0} image(s) of {target}.wz; " +
                      "every other image was copied across byte for byte.");
        }
        finally
        {
            try { file.Dispose(); } catch { /* already closed by the save */ }
            foreach (WzFile donor in donors) try { donor.Dispose(); } catch { /* read-only */ }
        }

        /* ---- verify on the saved and reopened archive ---- */
        lock (_gate) { _progress.State = "verifying"; _progress.Phase = "decoding"; }

        // The tree that was written has been mutated, and a digest cached
        // against one of its nodes is now a silently wrong "unchanged". The
        // hasher has no change notification to hook, so the contract is that a
        // caller which writes clears it.
        WzContentHasher.ClearCache();

        (List<DonorRestoreWrite> verified, int bystanders, int bystandersFailed, List<string> broke) =
            Verify(output, version, gameVersion, written, token);
        failures.AddRange(broke);

        long outputBytes = new FileInfo(output).Length;
        int rewritten = written.Select(w => w.Case.ImagePath)
                               .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        notes.Add($"{sourceBytes:N0} bytes in, {outputBytes:N0} out — a delta of " +
                  $"{outputBytes - sourceBytes:+#,##0;-#,##0;0}, of which {carriedBytes:N0} is the compressed " +
                  $"pixels of the {written.Count:N0} canvas(es) that were put back. The remaining " +
                  $"{outputBytes - sourceBytes - carriedBytes:+#,##0;-#,##0;0} is the re-serialisation of the " +
                  $"{rewritten:N0} image(s) holding them — their property tables and string pools are laid out " +
                  "afresh. No other image was rewritten; the rest travelled as the bytes they already were.");
        notes.Add($"{bystanders:N0} canvases that were NOT restored but share a rewritten image were decoded " +
                  $"as well, and {bystandersFailed:N0} of them failed. Carrying a node marks its whole image " +
                  "changed and a changed image is re-serialised, so those are what this restore actually put " +
                  "at risk.");

        /* ---- and re-resolve, which is the claim the user cares about ---- */
        lock (_gate) { _progress.Phase = "re-resolving"; }
        (int danglingAfter, long danglingCanvasesAfter, List<DonorRestoreCase> still, Census after) =
            ReResolve(options, source, output, version, gameVersion, token);

        notes.Add($"The family held {scan.Canvases:N0} canvases in {scan.Images:N0} images before and " +
                  $"{after.Canvases:N0} in {after.Images:N0} after — a difference of " +
                  $"{after.Canvases - scan.Canvases:+#,##0;-#,##0;0} canvases and " +
                  $"{after.Images - scan.Images:+#,##0;-#,##0;0} images. Anything else that had moved would " +
                  "show up here as a number that is not the count of what was put back.");

        notes.Add($"Re-scanned the family with {Path.GetFileName(output)} mounted in {target}.wz's place: " +
                  $"{scan.Dangling:N0} dangling links become {danglingAfter:N0}, and the " +
                  $"{scan.DanglingCanvases:N0} canvases behind them become {danglingCanvasesAfter:N0}. " +
                  "That is measured on the file that was written, not on the tree that wrote it.");

        int identityHeld = verified.Count(v => v.IdentityHeld);
        int decoded = verified.Count(v => v.Decoded);

        /* Record what this archive now carries, keyed on the content of what was
           read and what was written, so the next pass composes onto it instead
           of starting again from the pristine file and reverting this one. */
        lock (_gate) { _progress.Phase = "recording"; }
        RepairLedgerFile ledger = RepairLedger.Record(
            input, RepairLedger.DonorRestorePass, output, written.Count,
            written.Select(w => w.Write.Path).ToList(), notes);
        notes.Add($"{Path.GetFileName(output)} now carries a ledger of {ledger.Passes.Count} repair pass(es) — " +
                  $"{string.Join(", ", ledger.Passes.Select(p => $"{p.Pass} ({p.Changed:N0})"))} — all of them " +
                  $"from {Path.GetFileName(ledger.Origin)}. Install THIS file rather than any earlier one: " +
                  "each is a whole archive, so the last one copied wins.");

        DonorRestoreResult result = new(
            source, output, clock.Elapsed.TotalSeconds,
            scan.Dangling, written.Count,
            scan.Unrestorable + conflicted.Count - conflictedTaken.Count,
            deferred,
            identityHeld, written.Count - identityHeld,
            decoded, written.Count - decoded,
            bystanders, bystandersFailed,
            scan.Dangling, danglingAfter,
            scan.DanglingCanvases, danglingCanvasesAfter,
            sourceBytes, outputBytes, carriedBytes,
            scan.Canvases, after.Canvases, scan.Images, after.Images,
            verified, still, failures, notes,
            InstallCommand(ledger.Origin, output), input, ledger);

        return result;
    }

    /* ====================================================================
       CARRYING A NODE ACROSS
       ==================================================================== */

    /// <summary>
    /// Puts the node <paramref name="propPath"/> names in <paramref name="donorImage"/>
    /// back into <paramref name="liveImage"/> at the same path, creating whatever
    /// containers are missing on the way.
    ///
    /// Returns the donor node's content hash — the identity the write is claimed
    /// to have carried, checked again on the reopened archive.
    /// </summary>
    private static string Carry(WzImage donorImage, WzImage liveImage, string propPath,
                                IReadOnlySet<string> alsoRestoring,
                                out string note, out long carriedBytes)
    {
        carriedBytes = 0;
        WzImageProperty? donorNode = donorImage.GetFromPath(propPath)
            ?? throw new InvalidOperationException($"the donor no longer holds '{propPath}'.");

        if (liveImage.GetFromPath(propPath) is WzImageProperty already)
        {
            // Not an error and not a silent skip: if it is the same content the
            // job is already done, and if it is not, this is a different wound
            // than a missing node and must not be written over here.
            note = WzContentHasher.ContentEquals(already, donorNode)
                ? "already present in the live archive with the same content; nothing was written."
                : "already present in the live archive with DIFFERENT content; left alone.";
            throw new InvalidOperationException(
                $"'{propPath}' is already in the target — {note} This restore is for absent nodes.");
        }

        string donorHash = WzContentHasher.Hash(donorNode);

        string[] parts = propPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int created = 0;

        // Walk down, creating the containers the live image lacks. Each created
        // container takes the donor's type at that level and NONE of its
        // children: the links named the leaf, not the branch.
        object parent = liveImage;
        string here = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            here = here.Length == 0 ? parts[i] : here + "/" + parts[i];
            WzImageProperty? existing = liveImage.GetFromPath(here);
            if (existing != null) { parent = existing; continue; }

            WzImageProperty donorHere = donorImage.GetFromPath(here)
                ?? throw new InvalidOperationException(
                    $"the donor has no '{here}' either, so there is no shape to create the container from.");

            WzImageProperty made = donorHere switch
            {
                WzSubProperty => new WzSubProperty(parts[i]),
                WzConvexProperty => new WzConvexProperty(parts[i]),
                _ => throw new InvalidOperationException(
                    $"'{here}' is missing and the donor holds a {donorHere.GetType().Name} there. " +
                    "Only a container can be created empty; creating one of those would mean " +
                    "inventing its payload."),
            };
            Attach(parent, made);
            parent = made;
            created++;
        }

        WzImageProperty carried = CarryNode(donorNode);
        Attach(parent, carried);

        int canvases = 0, links = 0;
        long bytes = 0;
        CountCarried(carried, ref canvases, ref links, ref bytes);
        carriedBytes = bytes;
        /* Siblings of the restored node that the donor holds and that NO
           dangling link in this run names. Counted against the run's own set
           rather than as "everything beside it", because most of those siblings
           are being restored by their own link and reporting them as left behind
           would be false — 24 of the 25 children of one `affected/1` are named
           by the other 24 links. */
        int unnamedSiblings = 0;
        if (donorNode.Parent is WzImageProperty donorParent && donorParent.WzProperties != null)
        {
            string parentPath = parts.Length > 1 ? string.Join('/', parts[..^1]) : "";
            foreach (WzImageProperty sibling in donorParent.WzProperties)
            {
                if (string.Equals(sibling.Name, donorNode.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string path = parentPath.Length == 0 ? sibling.Name : parentPath + "/" + sibling.Name;
                if (!alsoRestoring.Contains(path)) unnamedSiblings++;
            }
        }

        note = $"{canvases} canvas(es) carried, {bytes:N0} bytes of compressed pixels" +
               (created > 0 ? $", {created} container(s) created empty on the way" : "") +
               (unnamedSiblings > 0
                   ? $"; the donor's container also holds {unnamedSiblings} sibling(s) that no dangling " +
                     "link names, and those were not carried"
                   : "") + ".";
        return donorHash;
    }

    private static void Attach(object parent, WzImageProperty child)
    {
        switch (parent)
        {
            case WzImage image: image.AddProperty(child); break;
            case WzSubProperty sub: sub.AddProperty(child); break;
            case WzConvexProperty convex: convex.AddProperty(child); break;
            case WzCanvasProperty canvas: canvas.AddProperty(child); break;
            default:
                throw new InvalidOperationException(
                    $"a {parent.GetType().Name} cannot hold a child, so there is nowhere to put " +
                    $"'{child.Name}'.");
        }
    }

    /// <summary>
    /// A deep copy of a donor node that is safe to hand to another archive's
    /// writer.
    ///
    /// The one thing this does that <c>DeepClone</c> does not: a canvas's pixels
    /// are taken through <c>GetCompressedBytesForExtraction</c>, WHILE THE DONOR
    /// STILL HAS ITS READER AND THEREFORE ITS KEY. A stored block may be wrapped
    /// in the list.wz XOR layer, which is keyed by the archive it came out of;
    /// copying it verbatim into an archive keyed differently produces a
    /// structurally perfect canvas whose pixels the client cannot decode.
    /// <c>SetCompressedBytes</c> nulls the reader, so after the copy the key is
    /// gone and the conversion can no longer be made — it has to happen here.
    /// (MapleLib's own <c>WzLinkResolver.CopyCanvasData</c> documents the same
    /// trap; a port hit it in 410d45f.)
    ///
    /// And the refusal that keeps this honest: a donor canvas that is ITSELF a
    /// link is not carried. Copying one would land a fresh `_inlink`/`_outlink`
    /// pointing at whatever the donor's neighbourhood held, count as a repair,
    /// and leave the canvas exactly as blank as it was.
    /// </summary>
    private static WzImageProperty CarryNode(WzImageProperty node)
    {
        switch (node)
        {
            case WzCanvasProperty canvas:
            {
                if (canvas[WzCanvasProperty.InlinkPropertyName] != null
                    || canvas[WzCanvasProperty.OutlinkPropertyName] != null)
                {
                    throw new InvalidOperationException(
                        $"the donor's '{canvas.Name}' is itself a link rather than art, so carrying it " +
                        "would put a new dangling link where the old one was. Refused.");
                }

                WzPngProperty? png = canvas.PngProperty
                    ?? throw new InvalidOperationException(
                        $"the donor's '{canvas.Name}' is a canvas with no picture at all.");

                byte[]? bytes = png.GetCompressedBytesForExtraction(false);
                if (bytes == null || bytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"the donor's '{canvas.Name}' has an empty pixel block, so there is nothing to " +
                        "carry back.");
                }

                WzCanvasProperty copy = new(canvas.Name);
                WzPngProperty payload = new();
                payload.SetCompressedBytes(bytes, png.Width, png.Height, png.Format);
                // SetCompressedBytes forces the magnification to zero, which is
                // right for a whole picture. If the donor really did hold this
                // canvas scaled, that number is part of the content and comes
                // back with it.
                payload.Mag = png.Mag;
                copy.PngProperty = payload;

                foreach (WzImageProperty child in canvas.WzProperties ?? Empty)
                    copy.AddProperty(CarryNode(child));
                return copy;
            }

            case WzUOLProperty uol:
                // Carried as the text it is. Never followed: a UOL is
                // parent-relative and its children belong to another node.
                return uol.DeepClone();

            case WzSubProperty sub:
            {
                WzSubProperty copy = new(sub.Name);
                foreach (WzImageProperty child in sub.WzProperties ?? Empty)
                    copy.AddProperty(CarryNode(child));
                return copy;
            }

            case WzConvexProperty convex:
            {
                WzConvexProperty copy = new(convex.Name);
                foreach (WzImageProperty child in convex.WzProperties ?? Empty)
                    copy.AddProperty(CarryNode(child));
                return copy;
            }

            default:
                // Scalars, sounds and raw payloads: DeepClone already copies the
                // bytes rather than the reader, and none of them is key-wrapped
                // the way a canvas block can be.
                return node.DeepClone();
        }
    }

    private static WzPropertyCollection Empty { get; } = new(null!);

    private static void CountCarried(WzImageProperty node, ref int canvases, ref int links, ref long bytes)
    {
        if (node is WzCanvasProperty canvas)
        {
            canvases++;
            bytes += canvas.PngProperty?.CompressedLength ?? 0;
        }
        if (node is WzUOLProperty) { links++; return; }
        foreach (WzImageProperty child in node.WzProperties ?? Empty)
            CountCarried(child, ref canvases, ref links, ref bytes);
    }

    /* ====================================================================
       VERIFY
       ==================================================================== */

    /// <summary>
    /// Reopens what was written and asks three questions of every restored node,
    /// in order: is it there, is it the same CONTENT the donor had, and do its
    /// pixels decode to the size they declare.
    ///
    /// The middle question is the one that matters and the one a name check
    /// cannot answer. A node of the right name in the right place is exactly
    /// what the port that caused this damage produced.
    ///
    /// Then every OTHER canvas in the images this restore caused to be rewritten
    /// is decoded too. Carrying a node marks its whole image changed and a
    /// changed image is re-serialised rather than copied as bytes, so the blast
    /// radius is not the nodes that were written — it is every canvas beside
    /// them. Verifying only the writes would report a clean run without looking
    /// at any of the thousands actually put at risk.
    /// </summary>
    private static (List<DonorRestoreWrite>, int, int, List<string>) Verify(
        string output, WzMapleVersion version, short gameVersion,
        IReadOnlyList<(DonorRestoreCase Case, DonorRestoreWrite Write)> written,
        CancellationToken token)
    {
        List<DonorRestoreWrite> verified = new();
        List<string> failures = new();
        int bystanders = 0, bystandersFailed = 0;

        WzFile back = new(output, gameVersion, version);
        try
        {
            WzFileParseStatus status = back.ParseWzFile();
            if (status != WzFileParseStatus.Success)
            {
                failures.Add($"The restored archive will not reopen: {status.GetErrorDescription()}. " +
                             "Nothing in it has been verified; do not install it.");
                return (written.Select(w => w.Write).ToList(), 0, 0, failures);
            }

            foreach (IGrouping<string, (DonorRestoreCase Case, DonorRestoreWrite Write)> byImage in
                     written.GroupBy(p => p.Case.ImagePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                WzImage? image = FindImage(back.WzDirectory, byImage.Key);
                if (image == null)
                {
                    failures.Add($"{byImage.Key} is not in the restored archive at all.");
                    foreach ((_, DonorRestoreWrite w) in byImage) verified.Add(w);
                    continue;
                }
                if (!image.Parsed && !image.ParseImage())
                {
                    failures.Add($"{byImage.Key} will not parse in the restored archive.");
                    foreach ((_, DonorRestoreWrite w) in byImage) verified.Add(w);
                    continue;
                }

                HashSet<string> restored = new(StringComparer.Ordinal);

                foreach ((DonorRestoreCase one, DonorRestoreWrite write) in byImage)
                {
                    restored.Add(one.PropPath);

                    if (image.GetFromPath(one.PropPath) is not WzImageProperty node)
                    {
                        failures.Add($"{one.Link}: the restored archive holds no '{one.PropPath}'.");
                        verified.Add(write);
                        continue;
                    }

                    string writtenHash;
                    try { writtenHash = WzContentHasher.Hash(node); }
                    catch (Exception ex)
                    {
                        failures.Add($"{one.Link}: the written node has no content hash — {ex.Message}");
                        verified.Add(write with { WrittenHash = "" });
                        continue;
                    }

                    bool identity = writtenHash.Equals(write.DonorHash, StringComparison.Ordinal);
                    if (!identity)
                        failures.Add($"{one.Link}: the node came back as different content than the donor's " +
                                     $"({writtenHash[..12]} vs {write.DonorHash[..12]}). Its name and place " +
                                     "are right and its content is not, which is exactly the substitution " +
                                     "this restore exists to avoid.");

                    bool decoded = true;
                    foreach ((string where, WzCanvasProperty canvas) in Canvases(node, one.PropPath))
                    {
                        if (canvas.PngProperty is not WzPngProperty png) { decoded = false; continue; }
                        System.Drawing.Bitmap? bitmap = null;
                        try
                        {
                            bitmap = png.GetImage(false);
                            if (bitmap == null || bitmap.Width != png.Width || bitmap.Height != png.Height)
                            {
                                decoded = false;
                                failures.Add($"{byImage.Key}/{where} was restored and does not decode to the " +
                                             $"{png.Width}x{png.Height} it declares.");
                            }
                        }
                        catch (Exception ex)
                        {
                            decoded = false;
                            failures.Add($"{byImage.Key}/{where} was restored and threw while decoding: {ex.Message}");
                        }
                        finally { bitmap?.Dispose(); }
                    }

                    verified.Add(write with
                    {
                        WrittenHash = writtenHash,
                        IdentityHeld = identity,
                        Decoded = decoded,
                    });
                }

                // The bystanders.
                foreach ((string where, WzCanvasProperty canvas) in Canvases(image, ""))
                {
                    token.ThrowIfCancellationRequested();
                    if (restored.Any(r => where.Equals(r, StringComparison.Ordinal)
                                          || where.StartsWith(r + "/", StringComparison.Ordinal)))
                        continue;
                    if (canvas.PngProperty is not WzPngProperty other) continue;
                    // A canvas whose pixels come from a link is SUPPOSED to have
                    // no block of its own; counting those as failures would bury
                    // a real one in thousands of false ones.
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
                                failures.Add($"{byImage.Key}/{where} was not restored but shares a rewritten " +
                                             "image, and it does not decode to the size it declares.");
                        }
                    }
                    catch (Exception ex)
                    {
                        bystandersFailed++;
                        if (bystandersFailed <= 20)
                            failures.Add($"{byImage.Key}/{where} was not restored but shares a rewritten " +
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
    /// Re-runs the whole resolution over the family with the OUTPUT mounted in
    /// the source's place.
    ///
    /// This is the number the user asked for and the only one that answers it.
    /// "347 nodes were written" is a claim about this code; "347 links became N"
    /// is a claim about the client, and it is made against files on disk.
    /// </summary>
    private (int, long, List<DonorRestoreCase>, Census) ReResolve(
        DonorRestoreOptions options, string source, string output,
        WzMapleVersion version, short gameVersion, CancellationToken token)
    {
        List<string> archives = Discover(options.Folder, options.Family, options.MinimumArchiveBytes, out _);
        List<WzFile> family = new();
        try
        {
            foreach (string path in archives)
            {
                bool swap = string.Equals(path, source, StringComparison.OrdinalIgnoreCase);
                WzFile file = new(swap ? output : path, gameVersion, version);
                if (file.ParseWzFile() != WzFileParseStatus.Success) { file.Dispose(); continue; }
                // The mount name has to be the one it replaces, or the family
                // index answers to a name the links never use.
                file.Name = Path.GetFileName(path);
                family.Add(file);
            }

            Census census = Walk(family, token);
            Resolution resolution = Resolve(family, census.Outlinks, token);

            List<DonorRestoreCase> still = resolution.Dangling
                .Select(d => new DonorRestoreCase(
                    d.Value, d.Family, d.ImagePath, d.PropPath, d.Refs.Count,
                    d.Refs.Examples.FirstOrDefault() ?? "", "", "", "", "", "", 0, 0,
                    Array.Empty<string>(), DonorRestoreVerdict.Unrestorable, d.Why))
                .OrderBy(c => c.Link, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (resolution.Dangling.Count, resolution.Dangling.Sum(d => d.Refs.Count), still, census);
        }
        finally
        {
            foreach (WzFile file in family) try { file.Dispose(); } catch { /* read-only */ }
        }
    }

    /* ====================================================================
       THE DECISION
       ==================================================================== */

    /// <summary>
    /// For each dangling link: which donor holds what it names, where it would
    /// land, and whether anything already there disagrees with the donor.
    /// </summary>
    private List<DonorRestoreCase> Judge(List<WzFile> family, List<WzFile> donors,
                                         List<Dangling> dangling, CancellationToken token)
    {
        List<DonorRestoreCase> cases = new();

        foreach (Dangling one in dangling)
        {
            token.ThrowIfCancellationRequested();

            // Where it would go: the archive the CLIENT would read the image
            // from, which is the first in mount order that holds it.
            WzFile? holder = null;
            WzImage? liveImage = null;
            foreach (WzFile file in family)
            {
                WzImage? found = FindImage(file.WzDirectory, one.ImagePath);
                if (found == null) continue;
                holder = file; liveImage = found; break;
            }

            string example = one.Refs.Examples.FirstOrDefault() ?? "";

            if (holder == null || liveImage == null)
            {
                cases.Add(new DonorRestoreCase(
                    one.Value, one.Family, one.ImagePath, one.PropPath, one.Refs.Count, example,
                    "", "", "", "", "", 0, 0, Array.Empty<string>(), DonorRestoreVerdict.Unrestorable,
                    $"No archive in the {one.Family} family holds {one.ImagePath} at all, so there is " +
                    "nowhere in this client to put the node back. Creating the image would only work if " +
                    "the mount order put it where the client looks, and this does not guess at that. " +
                    "Reported, not deleted."));
                continue;
            }

            string targetArchive = Path.GetFileNameWithoutExtension(holder.Name);

            // Which donor names it. First in the caller's order wins, and which
            // one it was is recorded — a restore whose provenance is "one of
            // these" is not provenance.
            WzFile? donor = null;
            WzImageProperty? donorNode = null;
            foreach (WzFile candidate in donors)
            {
                WzImage? donorImage = FindImage(candidate.WzDirectory, one.ImagePath);
                if (donorImage == null) continue;
                if (!donorImage.Parsed && !donorImage.ParseImage()) continue;
                if (donorImage.GetFromPath(one.PropPath) is not WzImageProperty node) continue;
                donor = candidate; donorNode = node; break;
            }

            if (donor == null || donorNode == null)
            {
                cases.Add(new DonorRestoreCase(
                    one.Value, one.Family, one.ImagePath, one.PropPath, one.Refs.Count, example,
                    targetArchive, "", "", "", "", 0, 0, Array.Empty<string>(),
                    DonorRestoreVerdict.Unrestorable,
                    donors.Count == 0
                        ? "No donor was given, so nothing was looked in. This link is unrestored, not proven lost."
                        : $"None of the {donors.Count} donor(s) holds {one.ImagePath}/{one.PropPath}. " +
                          "The art this link names is not in anything this run was pointed at. " +
                          "Reported, not deleted — deleting the link would make the audit green and leave " +
                          "the skill just as silent."));
                continue;
            }

            string donorName = Path.GetFileNameWithoutExtension(donor.Name);
            string donorHash;
            try { donorHash = WzContentHasher.Hash(donorNode); }
            catch (Exception ex)
            {
                cases.Add(new DonorRestoreCase(
                    one.Value, one.Family, one.ImagePath, one.PropPath, one.Refs.Count, example,
                    targetArchive, donorName, "", Shape(donorNode), "", 0, 0, Array.Empty<string>(),
                    DonorRestoreVerdict.Unrestorable,
                    $"The donor's node has no content hash — {ex.Message} Nothing may be decided from it, " +
                    "so it is not carried."));
                continue;
            }

            // Where it lands, and what is already there.
            WzSessionService.EnsureParsed(liveImage);
            (string landsUnder, WzObject? liveParent, WzObject? donorParent) =
                DeepestCommon(liveImage, donor, one.ImagePath, one.PropPath);

            int same = 0, differ = 0;
            List<string> differing = new();
            if (liveParent != null && donorParent != null)
            {
                Dictionary<string, WzImageProperty> liveKids = Children(liveParent);
                Dictionary<string, WzImageProperty> donorKids = Children(donorParent);
                foreach ((string name, WzImageProperty live) in liveKids)
                {
                    if (!donorKids.TryGetValue(name, out WzImageProperty? theirs)) continue;
                    bool equal;
                    try { equal = WzContentHasher.ContentEquals(live, theirs); }
                    catch { equal = false; }
                    if (equal) same++;
                    else { differ++; if (differing.Count < 6) differing.Add(name); }
                }
            }

            if (differ > 0)
            {
                cases.Add(new DonorRestoreCase(
                    one.Value, one.Family, one.ImagePath, one.PropPath, one.Refs.Count, example,
                    targetArchive, donorName, donorHash, Shape(donorNode), landsUnder,
                    same, differ, differing, DonorRestoreVerdict.Conflicted,
                    $"{donorName} holds this node, but of the {same + differ} node(s) that survived beside " +
                    $"where it lands, {differ} are different content in the donor ({string.Join(", ", differing)}" +
                    $"{(differ > differing.Count ? ", …" : "")}). The live client's art is newer; the donor's " +
                    "is what this link expects. Putting the donor's node back beside the live one mixes two " +
                    "generations of the same thing, so this is a decision, not an arithmetic repair."));
                continue;
            }

            cases.Add(new DonorRestoreCase(
                one.Value, one.Family, one.ImagePath, one.PropPath, one.Refs.Count, example,
                targetArchive, donorName, donorHash, Shape(donorNode), landsUnder,
                same, 0, Array.Empty<string>(), DonorRestoreVerdict.Restorable,
                $"{donorName} holds {one.ImagePath}/{one.PropPath} ({Shape(donorNode)}), and " +
                (same > 0
                    ? $"every one of the {same} node(s) that survived beside where it lands " +
                      "(under " + landsUnder + ") is the same content in both. "
                    : "nothing survived beside where it lands (under " + landsUnder + ") for the donor to " +
                      "disagree with — the container it goes into is one the live archive does not have. ") +
                $"Carrying it back makes {one.Refs.Count:N0} canvas(es) draw again."));
        }

        return cases;
    }

    /// <summary>
    /// The deepest ancestor of the missing node that exists in the live archive,
    /// paired with the donor's node at the same path.
    ///
    /// That is the right place to compare generations. Comparing the whole image
    /// would fire on every restore in a client three ports deep; comparing
    /// nothing would let the wrong generation's art land beside the right one
    /// without a word, which is how this damage was made.
    /// </summary>
    private static (string, WzObject?, WzObject?) DeepestCommon(
        WzImage liveImage, WzFile donor, string imagePath, string propPath)
    {
        WzImage? donorImage = FindImage(donor.WzDirectory, imagePath);
        if (donorImage == null) return ("", null, null);
        if (!donorImage.Parsed && !donorImage.ParseImage()) return ("", null, null);

        string[] parts = propPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string deepest = "";
        for (int i = 0; i < parts.Length; i++)
        {
            string candidate = deepest.Length == 0 ? parts[i] : deepest + "/" + parts[i];
            if (liveImage.GetFromPath(candidate) is WzImageProperty
                && donorImage.GetFromPath(candidate) is WzImageProperty)
                deepest = candidate;
            else break;
        }

        WzObject? live = deepest.Length == 0 ? liveImage : liveImage.GetFromPath(deepest);
        WzObject? theirs = deepest.Length == 0 ? donorImage : donorImage.GetFromPath(deepest);
        return (deepest.Length == 0 ? "(image root)" : deepest, live, theirs);
    }

    private static Dictionary<string, WzImageProperty> Children(WzObject node)
    {
        Dictionary<string, WzImageProperty> map = new(StringComparer.OrdinalIgnoreCase);
        WzPropertyCollection? kids = node switch
        {
            WzImage image => image.WzProperties,
            WzUOLProperty => null,
            WzImageProperty property => property.WzProperties,
            _ => null,
        };
        if (kids == null) return map;
        foreach (WzImageProperty child in kids) map[child.Name] = child;
        return map;
    }

    private static string Shape(WzImageProperty node) => node switch
    {
        WzCanvasProperty canvas when canvas.PngProperty is WzPngProperty png =>
            $"canvas {png.Width}x{png.Height} format {(int)png.Format}" + (png.Mag != 0 ? $" mag {png.Mag}" : ""),
        WzCanvasProperty => "canvas with no picture",
        WzSubProperty sub => $"container of {sub.WzProperties?.Count ?? 0}",
        WzUOLProperty uol => $"link to {uol.Value}",
        _ => node.GetType().Name.Replace("Wz", "").Replace("Property", "").ToLowerInvariant(),
    };

    /* ====================================================================
       THE WALK AND THE RESOLUTION
       ==================================================================== */

    private sealed class Refs
    {
        public long Count;
        public readonly List<string> Examples = new();
        public void Add(string path)
        {
            Count++;
            if (Examples.Count < 3) Examples.Add(path);
        }
    }

    private sealed class Census
    {
        public long Images;
        public long Canvases;
        public long OutlinkCanvases;
        public readonly Dictionary<string, Refs> Outlinks = new(StringComparer.OrdinalIgnoreCase);
        // family/imagePath -> mount orders holding it
        public readonly Dictionary<string, List<int>> Index = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Dangling(string Value, string Family, string ImagePath, string PropPath,
                                   Refs Refs, string Why);

    private sealed class Resolution
    {
        public int Resolved;
        public int IntoCanvasDirectory;
        public int OtherFamily;
        public int Malformed;
        public readonly List<Dangling> Dangling = new();
    }

    private Census Walk(List<WzFile> family, CancellationToken token)
    {
        Census census = new();
        for (int order = 0; order < family.Count; order++)
        {
            string stem = Path.GetFileNameWithoutExtension(family[order].Name);
            lock (_gate) { _progress.Archive = stem; _progress.Phase = "reading"; }
            WalkDirectory(family[order].WzDirectory, stem, Family(stem), order, "", census, token);
        }
        return census;
    }

    private void WalkDirectory(WzDirectory dir, string stem, string family, int order, string prefix,
                               Census census, CancellationToken token)
    {
        foreach (WzImage image in dir.WzImages)
        {
            token.ThrowIfCancellationRequested();
            string rel = prefix.Length == 0 ? image.Name : prefix + "/" + image.Name;

            string key = family + "/" + rel;
            if (!census.Index.TryGetValue(key, out List<int>? holders))
                census.Index[key] = holders = new List<int>();
            holders.Add(order);

            census.Images++;
            if ((census.Images & 0x1FF) == 0)
                lock (_gate) { _progress.ImagesDone = census.Images; _progress.CanvasesDone = census.Canvases; }

            bool wasParsed = image.Parsed;
            try
            {
                if (!image.Parsed && !image.Changed && !image.ParseImage()) continue;
                WzWalk walk = new();
                foreach (WzImageProperty property in image.WzProperties)
                    Property(property, $"{stem}.wz/{rel}", census, walk, 0);
            }
            catch (Exception)
            {
                // An image that will not read is the auditor's finding, not this
                // one's. Nothing here can restore it.
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
            WalkDirectory(sub, stem, family, order,
                          prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name, census, token);
    }

    private static void Property(WzImageProperty property, string path, Census census, WzWalk walk, int depth)
    {
        string here = path + "/" + property.Name;

        if (property is WzCanvasProperty canvas)
        {
            census.Canvases++;
            if (canvas[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty outlink
                && !string.IsNullOrEmpty(outlink.Value))
            {
                census.OutlinkCanvases++;
                string key = outlink.Value.Replace('\\', '/').Trim('/');
                if (!census.Outlinks.TryGetValue(key, out Refs? refs))
                    census.Outlinks[key] = refs = new Refs();
                refs.Add(here);
            }
        }

        WzPropertyCollection? children = walk.Into(property, depth);
        if (children == null) return;
        foreach (WzImageProperty child in children)
            Property(child, here, census, walk, depth + 1);
    }

    /// <summary>
    /// Resolves every distinct `_outlink` against the family, by the client's own
    /// rule: the first segment names the archive FAMILY, not a file, and the rest
    /// is a path from that family's root, with the first archive in mount order
    /// that holds the image winning.
    ///
    /// MapleLib's own <c>GetLinkedWzImageProperty</c> only searches the file the
    /// canvas is in, which is why a link can resolve in game and not in an editor
    /// and the other way round. This is the same rule the auditor applies, so the
    /// two agree about what is broken.
    /// </summary>
    private Resolution Resolve(List<WzFile> family, Dictionary<string, Refs> outlinks, CancellationToken token)
    {
        Resolution resolution = new();

        HashSet<string> families = family
            .Select(f => Family(Path.GetFileNameWithoutExtension(f.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<(string Prop, string Value, Refs Refs)>> byImage = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (WzFile File, string ImagePath, string Family)> where = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string value, Refs refs) in outlinks)
        {
            token.ThrowIfCancellationRequested();

            string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int imgAt = Array.FindIndex(segments, s => s.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
            if (segments.Length == 0 || imgAt < 0) { resolution.Malformed++; continue; }

            // A link into a _Canvas directory resolves for a tool and not for a
            // v232 client, which follows an outlink one level and expects the art
            // inline. A separate finding, and not this repair's: the archive is
            // not missing anything.
            if (segments.Any(s => s.Equals("_Canvas", StringComparison.OrdinalIgnoreCase)))
            {
                resolution.IntoCanvasDirectory++;
                continue;
            }

            string fam = segments[0];
            string imagePath = string.Join('/', segments.Skip(1).Take(imgAt));
            string propPath = string.Join('/', segments.Skip(imgAt + 1));

            // Not part of this run is NOT broken, and calling it broken would be
            // the fastest way to make the whole report untrustworthy.
            if (!families.Contains(fam)) { resolution.OtherFamily++; continue; }

            WzFile? holder = null;
            foreach (WzFile file in family)
            {
                if (!Family(Path.GetFileNameWithoutExtension(file.Name)).Equals(fam, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (FindImage(file.WzDirectory, imagePath) == null) continue;
                holder = file; break;
            }

            if (holder == null)
            {
                resolution.Dangling.Add(new Dangling(value, fam, imagePath, propPath, refs,
                    $"No archive in the {fam} family holds {imagePath}. The canvas has no pixels."));
                continue;
            }

            string key = holder.Name + "\u0001" + imagePath;
            where[key] = (holder, imagePath, fam);
            if (!byImage.TryGetValue(key, out List<(string, string, Refs)>? list))
                byImage[key] = list = new List<(string, string, Refs)>();
            list.Add((propPath, value, refs));
        }

        foreach ((string key, List<(string Prop, string Value, Refs Refs)> wants) in byImage)
        {
            token.ThrowIfCancellationRequested();
            (WzFile file, string imagePath, string fam) = where[key];
            WzImage? image = FindImage(file.WzDirectory, imagePath);
            if (image == null) continue;

            bool wasParsed = image.Parsed;
            try
            {
                if (!image.Parsed && !image.Changed && !image.ParseImage())
                {
                    foreach ((string prop, string value, Refs refs) in wants)
                        resolution.Dangling.Add(new Dangling(value, fam, imagePath, prop, refs,
                            $"The target image {imagePath} exists but will not parse."));
                    continue;
                }

                foreach ((string prop, string value, Refs refs) in wants)
                {
                    if (prop.Length == 0) { resolution.Resolved++; continue; }
                    if (image.GetFromPath(prop) is WzImageProperty) { resolution.Resolved++; continue; }
                    resolution.Dangling.Add(new Dangling(value, fam, imagePath, prop, refs,
                        $"{Path.GetFileNameWithoutExtension(file.Name)}.wz/{imagePath} exists but holds no " +
                        $"'{prop}'. This is the shape a link takes after its target was renamed, moved, or " +
                        "ported without it."));
                }
            }
            catch (Exception ex)
            {
                foreach ((string prop, string value, Refs refs) in wants)
                    resolution.Dangling.Add(new Dangling(value, fam, imagePath, prop, refs,
                        $"Reading the target image {imagePath} threw: {ex.Message}"));
            }
            finally
            {
                if (!wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                {
                    try { image.UnparseImage(); } catch { /* nothing left to free */ }
                }
            }
        }

        return resolution;
    }

    /* ====================================================================
       PLUMBING
       ==================================================================== */

    /// <summary>Every canvas under a node, with its path relative to <paramref name="prefix"/>.</summary>
    private static IEnumerable<(string Path, WzCanvasProperty Canvas)> Canvases(WzObject node, string prefix)
    {
        WzWalk walk = new();
        return Descend(node, prefix, walk, 0);

        static IEnumerable<(string, WzCanvasProperty)> Descend(WzObject node, string prefix, WzWalk walk, int depth)
        {
            if (node is WzCanvasProperty self && prefix.Length > 0)
                yield return (prefix, self);

            WzPropertyCollection? children = node is WzImageProperty property
                ? walk.Into(property, depth)
                : walk.Enter(node, depth) ? walk.From(node) : null;
            if (children == null) yield break;

            foreach (WzImageProperty child in children)
            {
                string here = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                foreach ((string deeper, WzCanvasProperty found) in Descend(child, here, walk, depth + 1))
                    yield return (deeper, found);
            }
        }
    }

    /// <summary>
    /// The skill id a restore path belongs to — see
    /// <see cref="DonorRestoreCase.SkillId"/> for why it exists and what the
    /// fallback protects.
    /// </summary>
    public static string SkillIdOf(string imagePath, string propPath)
    {
        string[] parts = propPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("skill", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        return imagePath + ":" + (parts.Length > 0 ? parts[0] : "");
    }

    /// <summary>The archive family a mounted name belongs to: Skill001 -> Skill.</summary>
    internal static string Family(string stem)
    {
        int end = stem.Length;
        while (end > 0 && char.IsDigit(stem[end - 1])) end--;
        return stem[..end];
    }

    private static int CountImages(WzDirectory dir)
    {
        int count = dir.WzImages.Count;
        foreach (WzDirectory sub in dir.WzDirectories) count += CountImages(sub);
        return count;
    }

    /// <summary>
    /// The image at a slash-separated path under a directory.
    ///
    /// Not <c>WzFile.GetObjectFromPath</c>: that routes through the global
    /// <c>WzFileManager</c>, which a service opening its own files has never
    /// populated, and it returns null rather than saying that is why.
    /// </summary>
    internal static WzImage? FindImage(WzDirectory? root, string path)
    {
        if (root == null) return null;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        WzDirectory? dir = root;
        for (int i = 0; i < parts.Length - 1 && dir != null; i++)
            dir = dir.GetDirectoryByName(parts[i]);
        return dir?.GetImageByName(parts[^1]);
    }

    private CancellationTokenSource Begin(string state)
    {
        lock (_gate)
        {
            if (_progress.State is "scanning" or "restoring" or "saving" or "verifying")
                throw new InvalidOperationException("A donor restore is already running.");
            _cancel = new CancellationTokenSource();
            _progress.State = state;
            _progress.Phase = "";
            _progress.Archive = "";
            _progress.ImagesDone = 0;
            _progress.CanvasesDone = 0;
            _progress.Dangling = 0;
            _progress.Restorable = 0;
            _progress.Seconds = 0;
            _progress.Error = null;
            return _cancel;
        }
    }

    /// <summary>
    /// The archives in a client folder that are MOUNTED — a bare stem with an
    /// optional trailing number, so `Skill.wz` and `Skill003.wz` are in and
    /// `Skill_old.wz`, `Skill_backup_20260312_121540.wz` and
    /// `Skilloldsuite.wz` are not. (The first two do not match the shape at all;
    /// the third matches but is its own family, which is the same answer.) That
    /// is what makes a folder full of the user's own backups safe to scan.
    /// </summary>
    internal static List<string> Discover(string folder, string? family, long minimumBytes,
                                         out List<string> skipped)
    {
        skipped = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new List<string>();

        List<string> found = new();
        foreach (string file in Directory.GetFiles(folder, "*.wz")
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (!MountedName.IsMatch(stem)) continue;
            if (!string.IsNullOrWhiteSpace(family)
                && !Family(stem).Equals(family, StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length < minimumBytes) { skipped.Add(Path.GetFileName(file)); continue; }
            found.Add(Path.GetFullPath(file));
        }
        return found;
    }

    internal static (WzMapleVersion, short, string) Encryption(List<string> archives, DonorRestoreOptions options)
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
    /// What the scan is entitled to conclude, said in the report rather than
    /// left for the reader to infer. A detector that lists only what it found
    /// cannot be checked; one that says what it examined and found clean can.
    /// </summary>
    private static IEnumerable<string> Conclusions(
        IReadOnlyList<DonorRestoreCase> cases, IReadOnlyList<DonorArchiveReport> donors)
    {
        int restorable = cases.Count(c => c.Verdict == DonorRestoreVerdict.Restorable);
        int conflicted = cases.Count(c => c.Verdict == DonorRestoreVerdict.Conflicted);
        int unrestorable = cases.Count(c => c.Verdict == DonorRestoreVerdict.Unrestorable);

        yield return $"{restorable:N0} link(s) can be restored outright, {conflicted:N0} only over a " +
                     $"generation disagreement, and {unrestorable:N0} not at all.";

        foreach (DonorArchiveReport donor in donors.Where(d => d.Satisfies > 0))
            yield return $"{donor.Name} ({donor.Bytes:N0} bytes, {donor.ModifiedUtc:yyyy-MM-dd}) holds the " +
                         $"node named by {donor.Satisfies:N0} of them, behind {donor.SatisfiesCanvases:N0} canvases.";

        foreach (DonorArchiveReport donor in donors.Where(d => d.Satisfies == 0))
            yield return $"{donor.Name} holds nothing any dangling link names. It contributed nothing.";

        if (unrestorable > 0)
            yield return $"{unrestorable:N0} link(s) name art no donor in this run holds. They are reported " +
                         "and left exactly as they are. Deleting them would make the audit green and leave " +
                         "the skill just as silent, which is the failure this tool exists to remove.";

        if (conflicted > 0)
            yield return $"The {conflicted:N0} conflicted case(s) are the interesting ones. At each, a node " +
                         "that survived the port is DIFFERENT CONTENT in the donor — the live client has " +
                         "newer art for the same skill, and the dangling links were written for the older " +
                         "art. Restoring puts the two side by side. That is a choice about which generation " +
                         "the skill should look like, and this tool does not make it.";
    }

    /// <summary>
    /// The command that installs an output, backup first.
    ///
    /// <paramref name="installAs"/> is the archive in the live client this
    /// replaces — which is NOT the source once repairs compose. A pass reading
    /// `Skill.restored.wz` still produces a `Skill.wz`, and naming the source
    /// here printed a command that backed up and overwrote a file the client
    /// does not have. The ledger's origin is the file at the head of the chain,
    /// so it is what the whole chain is going to be installed as.
    /// </summary>
    private static string InstallCommand(string installAs, string output)
    {
        string name = Path.GetFileName(installAs);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        string live = @"C:\MapleStory\232\" + name;
        return
            $"Copy-Item -LiteralPath '{live}' -Destination 'C:\\MapleStory\\232\\{Path.GetFileNameWithoutExtension(name)}_beforeDonorRestore_{stamp}.wz'; " +
            $"if ($?) {{ Copy-Item -LiteralPath '{output}' -Destination '{live}' -Force }}";
    }
}
