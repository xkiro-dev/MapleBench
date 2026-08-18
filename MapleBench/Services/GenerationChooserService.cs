using System.Diagnostics;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/* ============================================================================
   GENERATION CHOOSER — the per-skill judgement the combined build could not make

   THE JUDGEMENT, AS MEASURED

   Of the 347 dangling links the donor restore can put back, 261 land beside art
   of a DIFFERENT GENERATION: a node that survived the port holds newer art for
   the same skill, and the links were written for the older art the donor still
   carries. Restoring one of those makes its canvases draw again AND makes the
   skill mix two generations. Refusing keeps the look consistent AND leaves the
   canvases silent. Neither fact settles it, and the two combined archives that
   were built (`full`, `cons`) each answer for all 261 at once.

   The user deserves to answer PER SKILL, visually. This service prepares what
   that answer needs — for each conflicted skill, the donor art that would be
   restored and the surviving newer-generation art it would sit beside, decoded
   to pixels and composed the way the client draws them — and then drives the
   two existing repairs with the chosen subset:

     restore first (DonorRestoreService, AcceptGenerationMismatchFor = choice),
     format-repair second (CanvasFormatRepairService, reading the restore's
     output), which is the recorded order: the restore carries donor subtrees
     the format detector has never examined, so the detector must see the
     composed archive. The RepairLedger chains the two passes, and the chosen
     subset is in the restore pass's notes, so the archive is explicable.

   WHAT THIS SERVICE DELIBERATELY DOES NOT DO

   - It decides nothing. A skill not in the accepted set is left dangling and
     reported by the underlying restore; there is no branch here that deletes,
     substitutes, or defaults a choice.
   - It writes nothing of its own. Every write goes through the two services
     whose safety properties are already tested — new file beside the source,
     never the source, verify on the saved-and-reopened archive, refuse without
     confirm.
   - The preview never follows a link to find pixels. A donor canvas that is a
     link will be refused by the restore; showing "what would be restored" by
     silently resolving it against some mount would preview art the restore
     will not write.

   The frame placement is AnimationService.PlaceAtSharedOrigin — the dumper's
   own composition rule (frames at a shared origin, `delay` timing) — so what
   the chooser animates is what an exported GIF of the same node would show.
   ============================================================================ */

/// <summary>One frame of a preview set. Id addresses the PNG bytes; -1 means it did not decode.</summary>
public sealed record GenChoiceFrame(
    int Id, string Name, int Width, int Height,
    int OffsetX, int OffsetY, int Delay, string Format, string Note);

/// <summary>
/// One animated (or single-image) node, composed for playback: frames at the
/// shared origin inside a Width x Height box.
/// </summary>
public sealed record GenChoiceSet(
    string Path, int Width, int Height, int TotalMs, bool Truncated,
    IReadOnlyList<GenChoiceFrame> Frames, string Note);

/// <summary>
/// One skill's conflict: what the donor would restore, and the surviving
/// newer-generation art it would land beside. One accept/reject decision.
/// </summary>
public sealed record GenChoiceGroup(
    string SkillId,
    string SkillName,
    string ImagePath,
    string TargetArchive,
    string LandsUnder,
    int Links,
    long Canvases,
    int SiblingsSame,
    int SiblingsDiffer,
    IReadOnlyList<string> Donors,
    IReadOnlyList<GenChoiceSet> DonorSets,
    IReadOnlyList<GenChoiceSet> LiveSets,
    IReadOnlyList<string> Notes);

/// <summary>What a prepare produced. Read-only work; nothing was written.</summary>
public sealed record GenChoiceReport(
    string Folder,
    IReadOnlyList<string> Donors,
    DateTimeOffset StartedUtc,
    double Seconds,
    int Dangling,
    int Restorable,
    int Conflicted,
    int Unrestorable,
    long ConflictedCanvases,
    IReadOnlyList<GenChoiceGroup> Groups,
    IReadOnlyList<string> Notes);

public sealed class GenChoiceBuildRequest
{
    public string Folder { get; set; } = "";
    public string[] Donors { get; set; } = Array.Empty<string>();
    public string? Family { get; set; } = "Skill";

    /// <summary>
    /// The accepted conflicted skills. An EMPTY array is a real answer — "none
    /// of them" — and produces the conservative shape (clean restores plus
    /// format repairs); it is not treated as "not chosen".
    /// </summary>
    public string[] AcceptedSkillIds { get; set; } = Array.Empty<string>();

    public string? MapleVersion { get; set; }
    public short GameVersion { get; set; }
    public bool Confirm { get; set; }
    public bool AcceptSeparateRepairs { get; set; }
    public string? RestoredOutput { get; set; }
    public string? Output { get; set; }
    public long MinimumArchiveBytes { get; set; } = 64 * 1024;
}

/// <summary>The two passes, chained, with the choice that drove them.</summary>
public sealed record GenChoiceBuildResult(
    string Source,
    string RestoredOutput,
    string FinalOutput,
    long FinalBytes,
    double Seconds,
    IReadOnlyList<string> AcceptedSkillIds,
    IReadOnlyList<string> RejectedSkillIds,
    DonorRestoreResult Restore,
    CanvasRepairResult? Format,
    IReadOnlyList<string> Notes,
    string InstallCommand);

public sealed class GenChoiceProgress
{
    /// <summary>idle | preparing | building | done | failed | cancelled</summary>
    public string State { get; set; } = "idle";
    public string Phase { get; set; } = "";
    public int GroupsDone { get; set; }
    public int GroupsTotal { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }
}

public sealed class GenerationChooserService
{
    private readonly WarmupService _warmup;
    private readonly object _gate = new();
    private readonly GenChoiceProgress _progress = new();
    private CancellationTokenSource? _cancel;
    /// <summary>Running clock of the in-flight run, so a poll mid-build reads real elapsed seconds.</summary>
    private Stopwatch? _clock;
    private GenChoiceReport? _report;
    private GenChoiceBuildResult? _build;

    /// <summary>The decoded preview frames, replaced whole on each prepare.</summary>
    private Dictionary<int, byte[]> _frames = new();

    /* Budget, so a client with a pathological conflict count cannot decode the
       session into the ground. Whatever fires is reported on the set it fired
       on, never silently. */
    private const int MaxFramesPerSet = 48;
    private const int MaxLiveSetsPerGroup = 8;
    private const long MaxPreviewBytes = 256L * 1024 * 1024;

    public GenerationChooserService(WarmupService warmup) => _warmup = warmup;

    public GenChoiceProgress Snapshot()
    {
        lock (_gate)
            return new GenChoiceProgress
            {
                State = _progress.State,
                Phase = _progress.Phase,
                GroupsDone = _progress.GroupsDone,
                GroupsTotal = _progress.GroupsTotal,
                Seconds = _clock?.Elapsed.TotalSeconds ?? _progress.Seconds,
                Error = _progress.Error,
            };
    }

    public GenChoiceReport? LastReport() { lock (_gate) return _report; }
    public GenChoiceBuildResult? LastBuild() { lock (_gate) return _build; }

    public byte[]? FrameBytes(int id) { lock (_gate) return _frames.TryGetValue(id, out byte[]? b) ? b : null; }

    /// <summary>
    /// Cancels this service's own run and, when that run is driving the two
    /// repair services, theirs too — their work IS this run's work then. When
    /// idle nothing is cancelled: the inner services may be serving somebody
    /// else's request, and reaching into it would be this endpoint cancelling
    /// work it does not own.
    /// </summary>
    public void Cancel(DonorRestoreService restore, CanvasFormatRepairService format)
    {
        bool running;
        lock (_gate)
        {
            running = _progress.State is "preparing" or "building";
            _cancel?.Cancel();
        }
        if (!running) return;
        try { restore.Cancel(); } catch { /* not started yet */ }
        try { format.Cancel(); } catch { /* not started yet */ }
    }

    /* ====================================================================
       PREPARE — decode both generations of every conflicted skill
       ==================================================================== */

    /// <summary>Reserves synchronously on the caller's thread, then prepares in the background.</summary>
    public GenChoiceProgress StartPrepare(DonorRestoreOptions options, DonorRestoreService restore)
    {
        CancellationTokenSource cancel = Begin("preparing");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunPrepare(cancel, options, restore); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    /// <summary>Synchronous prepare, for tests and callers that want the report in hand.</summary>
    public GenChoiceReport Prepare(DonorRestoreOptions options, DonorRestoreService restore)
        => RunPrepare(Begin("preparing"), options, restore);

    private GenChoiceReport RunPrepare(CancellationTokenSource cancel, DonorRestoreOptions options,
                                       DonorRestoreService restore)
    {
        Stopwatch clock = Stopwatch.StartNew();
        lock (_gate) _clock = clock;
        try
        {
            GenChoiceReport report = PrepareCore(options, restore, cancel.Token, clock);
            lock (_gate)
            {
                _report = report;
                _progress.State = "done";
                _progress.Phase = "";
                _progress.Seconds = clock.Elapsed.TotalSeconds;
            }
            return report;
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
            lock (_gate) { _cancel?.Dispose(); _cancel = null; _clock = null; }
        }
    }

    private GenChoiceReport PrepareCore(DonorRestoreOptions options, DonorRestoreService restore,
                                        CancellationToken token, Stopwatch clock)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;

        /* The scan is the underlying service's, so the chooser judges exactly
           the cases the restore will act on — same family key, same mount
           order, same conflict rule. Its case list must be complete: a chooser
           over a truncated list would present a choice that is not the whole
           choice. */
        options.MaxCases = Math.Max(options.MaxCases, 5000);
        lock (_gate) { _progress.Phase = "scanning the family (the restore's own scan)"; }
        DonorRestoreScan scan = restore.Scan(options);
        if (scan.Cases.Count < scan.Restorable + scan.Conflicted + scan.Unrestorable)
            throw new InvalidOperationException(
                "The scan truncated its case list, so this chooser would show fewer conflicts than the " +
                "restore would act on. Raise MaxCases.");

        List<IGrouping<string, DonorRestoreCase>> groups = scan.Cases
            .Where(c => c.Verdict == DonorRestoreVerdict.Conflicted)
            .GroupBy(c => c.SkillId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_gate) { _progress.GroupsTotal = groups.Count; _progress.Phase = "decoding both generations"; }

        List<string> notes = new()
        {
            $"{scan.Dangling:N0} dangling link(s): {scan.Restorable:N0} restore outright and are not a " +
            $"choice, {scan.Conflicted:N0} land beside art of a different generation and are, " +
            $"{scan.Unrestorable:N0} no donor holds. The choice below covers the {scan.Conflicted:N0}, " +
            $"grouped into {groups.Count:N0} skill(s) — a skill's frames are one decision.",
        };

        List<string> archives = DonorRestoreService.Discover(
            options.Folder, options.Family, options.MinimumArchiveBytes, out _);
        (WzMapleVersion version, short gameVersion, _) = DonorRestoreService.Encryption(archives, options);

        Dictionary<int, byte[]> frames = new();
        long previewBytes = 0;
        int nextId = 0;
        List<GenChoiceGroup> built = new();

        List<WzFile> family = new();
        List<WzFile> donors = new();
        WzFile? strings = null;
        WzImage? skillNames = null;
        try
        {
            foreach (string path in archives)
            {
                WzFile file = new(path, gameVersion, version);
                if (file.ParseWzFile() == WzFileParseStatus.Success) family.Add(file);
                else file.Dispose();
            }

            foreach (string donorPath in options.Donors ?? Array.Empty<string>())
            {
                if (!File.Exists(donorPath)) continue;
                WzFile donor = new(Path.GetFullPath(donorPath), gameVersion, version);
                if (donor.ParseWzFile() == WzFileParseStatus.Success) donors.Add(donor);
                else donor.Dispose();
            }

            /* Names from String.wz, when the folder holds one. Absence is a
               stated fact, not a silent column of blanks. */
            string stringsPath = Path.Combine(options.Folder, "String.wz");
            if (File.Exists(stringsPath) && new FileInfo(stringsPath).Length >= options.MinimumArchiveBytes)
            {
                strings = new WzFile(stringsPath, gameVersion, version);
                if (strings.ParseWzFile() == WzFileParseStatus.Success)
                {
                    skillNames = DonorRestoreService.FindImage(strings.WzDirectory, "Skill.img");
                    if (skillNames != null && !skillNames.Parsed && !skillNames.ParseImage()) skillNames = null;
                }
                if (skillNames == null)
                    notes.Add("String.wz is beside the family but its Skill.img could not be read, so the " +
                              "skills below carry ids without names.");
            }
            else
            {
                notes.Add("No String.wz in this folder, so the skills below carry ids without names.");
            }

            Dictionary<string, WzImage?> donorImages = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WzImage?> liveImages = new(StringComparer.OrdinalIgnoreCase);

            WzImage? DonorImage(string donorName, string imagePath)
            {
                string key = donorName + "" + imagePath;
                if (donorImages.TryGetValue(key, out WzImage? cached)) return cached;
                WzFile? donor = donors.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d.Name)
                    .Equals(donorName, StringComparison.OrdinalIgnoreCase));
                WzImage? image = donor == null ? null : DonorRestoreService.FindImage(donor.WzDirectory, imagePath);
                if (image != null && !image.Parsed && !image.ParseImage()) image = null;
                return donorImages[key] = image;
            }

            WzImage? LiveImage(string imagePath)
            {
                if (liveImages.TryGetValue(imagePath, out WzImage? cached)) return cached;
                WzImage? image = null;
                foreach (WzFile file in family)
                {
                    image = DonorRestoreService.FindImage(file.WzDirectory, imagePath);
                    if (image != null) break;
                }
                if (image != null && !image.Parsed && !image.ParseImage()) image = null;
                return liveImages[imagePath] = image;
            }

            foreach (IGrouping<string, DonorRestoreCase> group in groups)
            {
                token.ThrowIfCancellationRequested();

                List<DonorRestoreCase> cases = group
                    .OrderBy(c => c.Link, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> groupNotes = new();

                /* ---- the donor side: what would be restored -------------- */
                List<GenChoiceSet> donorSets = new();
                foreach (IGrouping<string, DonorRestoreCase> byParent in cases
                             .GroupBy(c => ParentOf(c.PropPath), StringComparer.OrdinalIgnoreCase))
                {
                    List<(string Name, WzCanvasProperty Canvas)> leafFrames = new();
                    foreach (DonorRestoreCase one in byParent)
                    {
                        WzImage? donorImage = DonorImage(one.Donor, one.ImagePath);
                        WzImageProperty? node = donorImage?.GetFromPath(one.PropPath);
                        if (node == null)
                        {
                            groupNotes.Add($"{one.Link}: the donor no longer answers for this node; " +
                                           "its preview is missing, not its restore.");
                            continue;
                        }
                        if (node is WzCanvasProperty canvas)
                        {
                            leafFrames.Add((LeafOf(one.PropPath), canvas));
                        }
                        else
                        {
                            // A link naming a container restores the whole
                            // container; its numbered canvases are the animation.
                            GenChoiceSet? set = BuildSet(
                                $"{one.ImagePath}/{one.PropPath}", Members(node),
                                frames, ref nextId, ref previewBytes, groupNotes);
                            if (set != null) donorSets.Add(set);
                        }
                    }
                    if (leafFrames.Count > 0)
                    {
                        GenChoiceSet? set = BuildSet(
                            $"{byParent.First().ImagePath}/{byParent.Key}", Sorted(leafFrames),
                            frames, ref nextId, ref previewBytes, groupNotes);
                        if (set != null) donorSets.Add(set);
                    }
                }

                /* ---- the live side: the surviving newer-generation art --- */
                List<GenChoiceSet> liveSets = new();
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                int liveSetsSkipped = 0;
                foreach (DonorRestoreCase one in cases)
                {
                    WzImage? liveImage = LiveImage(one.ImagePath);
                    if (liveImage == null) continue;
                    foreach (string sibling in one.DifferingSiblings)
                    {
                        string landsUnder = one.LandsUnder == "(image root)" ? "" : one.LandsUnder;
                        string path = landsUnder.Length == 0 ? sibling : landsUnder + "/" + sibling;
                        if (!seen.Add(one.ImagePath + "" + path)) continue;
                        if (liveSets.Count >= MaxLiveSetsPerGroup) { liveSetsSkipped++; continue; }

                        WzImageProperty? node = liveImage.GetFromPath(path);
                        if (node == null) continue;
                        GenChoiceSet? set = BuildSet(
                            $"{one.ImagePath}/{path}", Members(node),
                            frames, ref nextId, ref previewBytes, groupNotes);
                        if (set != null) liveSets.Add(set);
                    }
                }
                if (liveSetsSkipped > 0)
                    groupNotes.Add($"{liveSetsSkipped} more surviving node(s) differ and are not previewed " +
                                   $"here — the first {MaxLiveSetsPerGroup} stand for them.");

                int siblingsDiffer = cases.Max(c => c.SiblingsDiffer);
                int listed = cases.SelectMany(c => c.DifferingSiblings)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (siblingsDiffer > listed)
                    groupNotes.Add($"The scan counted {siblingsDiffer} differing sibling(s) and named the " +
                                   $"first {listed}; the preview shows what was named.");

                built.Add(new GenChoiceGroup(
                    group.Key,
                    NameOf(skillNames, group.Key),
                    cases[0].ImagePath,
                    cases[0].TargetArchive,
                    cases[0].LandsUnder,
                    cases.Count,
                    cases.Sum(c => c.Canvases),
                    cases.Max(c => c.SiblingsSame),
                    siblingsDiffer,
                    cases.Select(c => c.Donor).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    donorSets, liveSets, groupNotes));

                lock (_gate) { _progress.GroupsDone = built.Count; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            }
        }
        finally
        {
            foreach (WzFile file in family) try { file.Dispose(); } catch { /* read-only */ }
            foreach (WzFile donor in donors) try { donor.Dispose(); } catch { /* read-only */ }
            try { strings?.Dispose(); } catch { /* read-only */ }
        }

        if (previewBytes >= MaxPreviewBytes)
            notes.Add($"The preview budget ({MaxPreviewBytes / (1024 * 1024)} MB of decoded frames) was " +
                      "reached; sets after that point carry their metadata and a note instead of pixels.");

        GenChoiceReport report = new(
            options.Folder,
            (options.Donors ?? Array.Empty<string>()).Select(Path.GetFileName).Where(n => n != null)
                .Select(n => n!).ToList(),
            started, clock.Elapsed.TotalSeconds,
            scan.Dangling, scan.Restorable, scan.Conflicted, scan.Unrestorable,
            scan.ConflictedCanvases,
            built, notes);

        lock (_gate) { _frames = frames; }
        return report;
    }

    /* ====================================================================
       BUILD — the two passes, chained, driven by the choice
       ==================================================================== */

    /// <summary>Reserves synchronously on the caller's thread, then builds in the background.</summary>
    public GenChoiceProgress StartBuild(GenChoiceBuildRequest request, DonorRestoreService restore,
                                        CanvasFormatRepairService format)
    {
        Confirmed(request);
        CancellationTokenSource cancel = Begin("building");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunBuild(cancel, request, restore, format); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    /// <summary>Synchronous build, for tests.</summary>
    public GenChoiceBuildResult Build(GenChoiceBuildRequest request, DonorRestoreService restore,
                                      CanvasFormatRepairService format)
    {
        Confirmed(request);
        return RunBuild(Begin("building"), request, restore, format);
    }

    private static void Confirmed(GenChoiceBuildRequest request)
    {
        if (!request.Confirm)
            throw new InvalidOperationException(
                "A build writes two new archives the size of the source. Pass confirm=true.");
    }

    private GenChoiceBuildResult RunBuild(CancellationTokenSource cancel, GenChoiceBuildRequest request,
                                          DonorRestoreService restore, CanvasFormatRepairService format)
    {
        Stopwatch clock = Stopwatch.StartNew();
        lock (_gate) _clock = clock;
        try
        {
            GenChoiceBuildResult result = BuildCore(request, restore, format, cancel.Token, clock);
            lock (_gate)
            {
                _build = result;
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
            lock (_gate) { _cancel?.Dispose(); _cancel = null; _clock = null; }
        }
    }

    private GenChoiceBuildResult BuildCore(GenChoiceBuildRequest request, DonorRestoreService restore,
                                           CanvasFormatRepairService format, CancellationToken token,
                                           Stopwatch clock)
    {
        List<string> notes = new()
        {
            "Restore first, format-repair second — the recorded order: the restore carries whole donor " +
            "subtrees in, and that art has never been format-checked, so the detector must see the " +
            "composed archive. The RepairLedger chains the two passes; the per-skill choice is in the " +
            "restore pass's notes, so the archive says which judgement produced it.",
        };

        lock (_gate) { _progress.Phase = "pass 1 of 2 — donor restore (its own progress endpoint has the detail)"; }

        DonorRestoreOptions donorOptions = new()
        {
            Folder = request.Folder,
            Donors = request.Donors,
            Family = request.Family,
            MapleVersion = request.MapleVersion,
            GameVersion = request.GameVersion,
            MinimumArchiveBytes = request.MinimumArchiveBytes,
            MaxCases = 5000,
            Confirm = true,
            AcceptGenerationMismatchFor = request.AcceptedSkillIds ?? Array.Empty<string>(),
            AcceptSeparateRepairs = request.AcceptSeparateRepairs,
            Output = request.RestoredOutput,
        };

        DonorRestoreResult restored = restore.Apply(donorOptions);

        List<string> accepted = (request.AcceptedSkillIds ?? Array.Empty<string>())
            .Select(id => id.Trim()).Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> rejected = (restore.LastScan()?.Cases ?? Array.Empty<DonorRestoreCase>())
            .Where(c => c.Verdict == DonorRestoreVerdict.Conflicted)
            .Select(c => c.SkillId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !accepted.Contains(id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        if (string.IsNullOrEmpty(restored.Output))
        {
            notes.Add("The restore wrote nothing — no accepted skill and no clean restore — so there is " +
                      "no archive to format-check and no archive to install. The dangling links are " +
                      "exactly as the scan reported them.");
            return new GenChoiceBuildResult(
                restored.Source, "", "", 0, clock.Elapsed.TotalSeconds,
                accepted, rejected, restored, null, notes, "");
        }

        token.ThrowIfCancellationRequested();
        lock (_gate) { _progress.Phase = "pass 2 of 2 — canvas format repair over the restored archive"; }

        string dir = Path.GetDirectoryName(restored.Output) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(restored.Source);
        string final = string.IsNullOrWhiteSpace(request.Output)
            ? Path.Combine(dir, stem + ".genchoice.wz")
            : Path.GetFullPath(request.Output!);

        CanvasRepairResult formatted = format.Apply(new CanvasRepairOptions
        {
            Path = restored.Output,
            Output = final,
            Confirm = true,
            MapleVersion = request.MapleVersion,
            GameVersion = request.GameVersion,
            // The format pass READS the restore's output, so the ledger sees a
            // chain, not a conflict; the flag is passed through all the same for
            // the caller who is deliberately building a separate variant.
            AcceptSeparateRepairs = request.AcceptSeparateRepairs,
        });

        string finalOutput;
        string install;
        if (string.IsNullOrEmpty(formatted.Output))
        {
            finalOutput = restored.Output;
            install = restored.InstallCommand;
            notes.Add("The format pass examined the restored archive and found no split canvas format, " +
                      "so the restore's output IS the final archive. Its own notes above say how many " +
                      "canvases were examined to earn that zero.");
        }
        else
        {
            finalOutput = formatted.Output;
            install = formatted.InstallCommand;
            notes.Add($"{Path.GetFileName(restored.Output)} is the intermediate archive and stays on disk " +
                      "because the ledger chain runs through it. Install ONLY the final archive — each " +
                      "output is a whole copy of the source, so the last one copied wins.");
        }

        return new GenChoiceBuildResult(
            restored.Source, restored.Output, finalOutput,
            new FileInfo(finalOutput).Length, clock.Elapsed.TotalSeconds,
            accepted, rejected, restored, formatted,
            notes, install);
    }

    /* ====================================================================
       DECODING A SET
       ==================================================================== */

    /// <summary>
    /// The canvases a preview of this node should play. A canvas is itself; a
    /// container contributes its numerically-named canvas children in numeric
    /// order (the WZ animation convention), or all its canvas children when
    /// none is numbered.
    /// </summary>
    private static List<(string Name, WzCanvasProperty Canvas)> Members(WzImageProperty node)
    {
        if (node is WzCanvasProperty self) return new() { (self.Name, self) };

        List<(string, WzCanvasProperty)> canvases = new();
        foreach (WzImageProperty child in node.WzProperties ?? EmptyChildren)
            if (child is WzCanvasProperty canvas)
                canvases.Add((canvas.Name, canvas));

        List<(string, WzCanvasProperty)> numbered = canvases
            .Where(c => int.TryParse(c.Item1, out _)).ToList();
        return Sorted(numbered.Count > 0 ? numbered : canvases);
    }

    private static List<(string Name, WzCanvasProperty Canvas)> Sorted(
        List<(string Name, WzCanvasProperty Canvas)> members)
        => members
            .OrderBy(m => int.TryParse(m.Name, out int n) ? n : int.MaxValue)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static WzPropertyCollection EmptyChildren { get; } = new(null!);

    /// <summary>
    /// Decodes and composes one set. Placement is
    /// <see cref="AnimationService.PlaceAtSharedOrigin"/> — the dumper's rule —
    /// so the preview plays what an export of the node would show. A frame that
    /// will not decode keeps its slot and its delay with Id -1 and a note;
    /// dropping it would preview a smoother animation than the client has.
    /// </summary>
    private GenChoiceSet? BuildSet(string path,
                                   List<(string Name, WzCanvasProperty Canvas)> members,
                                   Dictionary<int, byte[]> frames, ref int nextId, ref long previewBytes,
                                   List<string> notes)
    {
        if (members.Count == 0) return null;

        bool truncated = members.Count > MaxFramesPerSet;
        if (truncated) members = members.Take(MaxFramesPerSet).ToList();

        List<AnimationFrameDto> dtos = new(members.Count);
        List<(AnimationFrameDto Dto, WzCanvasProperty Canvas)> pairs = new(members.Count);
        foreach ((string name, WzCanvasProperty canvas) in members)
        {
            AnimationFrameDto dto = AnimationService.DescribeCanvas(canvas, name);
            dtos.Add(dto);
            pairs.Add((dto, canvas));
        }

        (_, _, int width, int height, int totalMs) = AnimationService.PlaceAtSharedOrigin(dtos);

        List<GenChoiceFrame> outFrames = new(pairs.Count);
        int splitDecoded = 0;
        foreach ((AnimationFrameDto dto, WzCanvasProperty canvas) in pairs)
        {
            int id = -1;
            string note = "";
            string formatName = canvas.PngProperty is { } png ? ((int)png.Format).ToString() : "none";

            if (canvas[WzCanvasProperty.InlinkPropertyName] != null
                || canvas[WzCanvasProperty.OutlinkPropertyName] != null)
            {
                note = "draws via a link; the pixels live elsewhere and this preview does not follow links.";
            }
            else if (canvas.PngProperty is not WzPngProperty pixels)
            {
                note = "a canvas with no picture at all.";
            }
            else if (previewBytes >= MaxPreviewBytes)
            {
                note = "not decoded: the preview budget was already spent. The metadata is real.";
            }
            else
            {
                System.Drawing.Bitmap? bitmap = null;
                try
                {
                    bool split;
                    (bitmap, split, formatName) = DecodeForPreview(pixels, path, dto.Name);
                    if (split) splitDecoded++;
                    if (bitmap == null)
                    {
                        note = "did not decode.";
                    }
                    else
                    {
                        using MemoryStream stream = new();
                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        byte[] bytes = stream.ToArray();
                        id = nextId++;
                        frames[id] = bytes;
                        previewBytes += bytes.Length;
                    }
                }
                catch (Exception ex)
                {
                    note = $"threw while decoding: {ex.Message}";
                }
                finally { bitmap?.Dispose(); }
            }

            if (id < 0 && note.Length > 0)
                notes.Add($"{path}/{dto.Name}: {note}");

            outFrames.Add(new GenChoiceFrame(
                id, dto.Name, dto.Width, dto.Height, dto.OffsetX, dto.OffsetY, dto.Delay,
                formatName, note));
        }

        if (splitDecoded > 0)
            notes.Add($"{path}: {splitDecoded} frame(s) carry the split format field on disk and were " +
                      "previewed with the joined format — the built archive always includes the format " +
                      "pass, so this is what the final archive draws. Today's client draws them as noise.");

        return new GenChoiceSet(path, width, height, totalMs, truncated, outFrames,
            truncated ? $"only the first {MaxFramesPerSet} frames are previewed" : "");
    }

    /// <summary>
    /// Decodes one canvas for the preview, and decodes a SPLIT format the way
    /// the built archive will hold it.
    ///
    /// The 261 conflicted skills live in the same images as the 283 split
    /// canvas formats — `0008003/effect` is both the generation-conflict
    /// archetype and a split DXT5 — so the surviving newer art, decoded from
    /// the field on disk, is noise. The build this chooser drives ALWAYS runs
    /// the format pass, so the honest preview of "what the restored art would
    /// sit beside" is the repaired decode. The verdict is
    /// <see cref="CanvasFormatRepairService.Judge"/> — the repair's own
    /// arithmetic, reused rather than copied, so the preview can never call
    /// split what the repair calls genuine. The fields are put back afterwards;
    /// nothing this service opens is ever saved.
    /// </summary>
    private static (System.Drawing.Bitmap? Bitmap, bool Split, string FormatName) DecodeForPreview(
        WzPngProperty png, string path, string name)
    {
        if (png.Mag != 0)
        {
            CanvasMagCase verdict = CanvasFormatRepairService.Judge(
                "preview", $"{path}/{name}", "", name, png, (int)png.Format, png.Mag);
            if (verdict.Verdict == CanvasMagVerdict.Split)
            {
                WzPngFormat storedFormat = png.Format;
                int storedMag = png.Mag;
                try
                {
                    png.Format = (WzPngFormat)verdict.JoinedFormat;
                    png.Mag = 0;
                    return (png.GetImage(false), true, $"{verdict.JoinedFormat} (split on disk)");
                }
                finally
                {
                    png.Format = storedFormat;
                    png.Mag = storedMag;
                }
            }
        }
        return (png.GetImage(false), false, ((int)png.Format).ToString());
    }

    /* ====================================================================
       PLUMBING
       ==================================================================== */

    private static string ParentOf(string propPath)
    {
        int cut = propPath.LastIndexOf('/');
        return cut < 0 ? "" : propPath[..cut];
    }

    private static string LeafOf(string propPath)
    {
        int cut = propPath.LastIndexOf('/');
        return cut < 0 ? propPath : propPath[(cut + 1)..];
    }

    /// <summary>
    /// The skill's display name from String.wz's Skill.img, which keys rows by
    /// the UNPADDED id — `0008003` in the art tree is `8003` there. Both spellings
    /// are tried; an id with no row (or no String.wz) stays an id, visibly.
    /// </summary>
    private static string NameOf(WzImage? skillNames, string skillId)
    {
        if (skillNames == null) return "";
        foreach (string key in Keys(skillId))
        {
            try
            {
                if (skillNames.GetFromPath(key + "/name") is WzStringProperty name
                    && !string.IsNullOrEmpty(name.Value))
                    return name.Value;
            }
            catch { /* a malformed row names nothing */ }
        }
        return "";

        static IEnumerable<string> Keys(string id)
        {
            yield return id;
            string trimmed = id.TrimStart('0');
            if (trimmed.Length > 0 && trimmed != id) yield return trimmed;
        }
    }

    private CancellationTokenSource Begin(string state)
    {
        lock (_gate)
        {
            if (_progress.State is "preparing" or "building")
                throw new InvalidOperationException(
                    _progress.State == "preparing"
                        ? "A generation-chooser prepare is already running."
                        : "A generation-chooser build is already running.");
            _cancel = new CancellationTokenSource();
            _progress.State = state;
            _progress.Phase = "";
            _progress.GroupsDone = 0;
            _progress.GroupsTotal = 0;
            _progress.Seconds = 0;
            _progress.Error = null;
            return _cancel;
        }
    }
}
