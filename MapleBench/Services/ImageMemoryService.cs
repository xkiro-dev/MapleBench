using System.Diagnostics;
using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>What one sweep released.</summary>
public sealed class SweepReportDto
{
    public int Swept { get; set; }
    public int Kept { get; set; }
    public int ParsedBefore { get; set; }
    public long WorkingSetBeforeMB { get; set; }
    public long WorkingSetAfterMB { get; set; }
    public long ReclaimedMB { get; set; }
    public int ElapsedMs { get; set; }
    public bool Ran { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Gives back the memory a browse costs, by releasing the parsed property graph
/// of images that are parsed and have no unsaved changes.
///
/// Why this exists, measured on a v232 client (29 archives, 21.7 GB) in one
/// process: 60 MB at rest, 177 MB with all 29 archives open, 352 MB after the
/// string pool, 1,088 MB after the mob list, 1,631 MB after the skill list,
/// 2,139 MB after the NPC list, 2,254 MB after one deep search. None of that is
/// ever given back — nothing in the app called <see cref="WzImage.UnparseImage"/>
/// outside the one place in <c>WzSaveService.Preflight</c>. Browsing three
/// sections of your own client should not cost two gigabytes.
///
/// ============================ READ THIS FIRST ============================
/// This is the one optimisation in the app that can destroy a user's work, so
/// the rule is stated once and enforced three times below:
///
///     AN IMAGE WITH <see cref="WzImage.Changed"/> SET IS NEVER RELEASED.
///
/// <see cref="WzImage.UnparseImage"/> clears the property collection. On an
/// edited image those properties *are* the edit — the archive on disk still
/// holds the original bytes. And the loss is silent, which is what makes it
/// dangerous rather than merely bad: <c>Changed</c> survives the unparse, so at
/// save time <see cref="WzImage.SaveImage"/> takes its <c>forceRead</c> path,
/// re-reads the original pre-edit block from disk, writes that, and the save
/// verifies clean and reports "Saved and verified." The user is told their work
/// was written. It was not.
///
/// This is also why MapleLib's own <c>Img/LRUCache.cs</c> and
/// <c>Img/LazyWzImageDictionary.cs</c> are not used here and must not be
/// adopted: neither of them mentions <c>Changed</c> at all — LRUCache evicts on
/// count and bytes alone, so it will evict a dirty image the moment enough
/// others are touched. LazyWzImageDictionary is worse in a second way: its
/// enumerator yields <c>(name, null)</c> for images it has not materialised, so
/// <c>OpenFile.CountDirtyImages</c> and the save path would both see zero dirty
/// images and there would be nothing to write at all.
/// =========================================================================
///
/// Two further things are protected, for the same "silent and plausible" reason:
///
///   * images the undo history holds live references into — see
///     <see cref="UndoService.ImagesInHistory"/>;
///   * images with no on-disk block to be re-read from (a newly added image),
///     which cannot be re-parsed at all: <see cref="WzImage.ParseImage"/> opens
///     with <c>lock (reader)</c> and a new image has no reader.
/// </summary>
public sealed class ImageMemoryService
{
    /// <summary>
    /// Working set above which an idle sweep is worth running.
    ///
    /// Below this the app is not costing anyone anything and a sweep would only
    /// buy the next browse a re-parse. The number sits above "all 29 archives
    /// open plus the string pool" (352 MB measured) and below "one section
    /// browsed" (1,088 MB), so it fires exactly when a section grid has been
    /// built and not before.
    /// </summary>
    public const long AutoSweepThresholdBytes = 700L * 1024 * 1024;

    /// <summary>
    /// Images released per gate hold.
    ///
    /// <see cref="WzImage.UnparseImage"/> is a property-collection clear, ~2 µs
    /// an image, so 512 of them is well under a millisecond of gate time and an
    /// interactive request queued behind a sweep never waits for the whole of it.
    /// </summary>
    private const int ChunkSize = 512;

    private readonly WzSessionService _session;
    private readonly UndoService _undo;
    private readonly ILogger<ImageMemoryService> _log;

    /// <summary>One sweep at a time; a second caller is told to come back later.</summary>
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>
    /// What the last sweep that released nothing was looking at, so an identical
    /// one does not pay for the collection again. -1 means "no such sweep yet".
    ///
    /// See the guard in <see cref="Sweep"/>: a session holding a split archive is
    /// permanently above <see cref="AutoSweepThresholdBytes"/> and permanently
    /// unable to release anything, so without this every warm-up step and every
    /// domain build pays a stop-the-world compacting gen2 for a guaranteed zero.
    /// </summary>
    private int _futileGeneration = -1;
    private int _futileParsed = -1;

    public ImageMemoryService(WzSessionService session, UndoService undo, ILogger<ImageMemoryService> log)
    {
        _session = session;
        _undo = undo;
        _log = log;
    }

    /// <summary>
    /// Runs after each chunk, with the gate released — the exact window the
    /// generation check above exists to survive.
    ///
    /// A test seam, and it is here because the alternative did not work. The
    /// hazard is another request closing an archive while this loop holds strong
    /// references to its images, and a test that has to win a race to show that
    /// is not a test: driven from a second thread, the sweep finished before the
    /// close could take the gate on every run, so the assertion passed with the
    /// guard removed. This makes the interleaving something the test chooses.
    /// </summary>
    internal Action? BetweenChunks { get; set; }

    public static long WorkingSetBytes
    {
        get
        {
            using Process self = Process.GetCurrentProcess();
            return self.WorkingSet64;
        }
    }

    /// <summary>
    /// Sweeps only when the process is actually holding a lot; used by the
    /// warm-up and by the domain builds, which is where the memory is spent.
    /// </summary>
    public SweepReportDto SweepIfHeavy(CancellationToken cancel = default)
    {
        long working = WorkingSetBytes;
        if (working < AutoSweepThresholdBytes)
        {
            return new SweepReportDto
            {
                Ran = false,
                WorkingSetBeforeMB = working / (1024 * 1024),
                WorkingSetAfterMB = working / (1024 * 1024),
                Reason = "Below the sweep threshold; nothing to give back.",
            };
        }
        return Sweep(cancel);
    }

    public SweepReportDto Sweep(CancellationToken cancel = default)
    {
        SweepReportDto report = new();
        if (!_running.Wait(0))
        {
            report.Reason = "A sweep is already running.";
            return report;
        }

        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            report.Ran = true;
            report.WorkingSetBeforeMB = WorkingSetBytes / (1024 * 1024);

            // Taken once, outside the gate, and used as a *veto* list only: an
            // entry recorded after this point covers an image the edit has just
            // marked Changed, which the per-image guard below refuses anyway.
            HashSet<WzImage> history = _undo.ImagesInHistory();

            List<WzImage> candidates = new();
            bool stoppedEarly = false;
            int treeGeneration;
            lock (_session.Gate)
            {
                treeGeneration = _session.Generation;
                foreach (OpenFile file in _session.Files)
                {
                    foreach (WzImage image in file.EnumerateArchiveImages())
                    {
                        if (image.Parsed)
                            report.ParsedBefore++;
                        if (IsReleasable(image, history))
                            candidates.Add(image);
                        else if (image.Parsed)
                            report.Kept++;
                    }
                }
            }

            for (int i = 0; i < candidates.Count && !cancel.IsCancellationRequested;)
            {
                lock (_session.Gate)
                {
                    // The candidate list is a set of strong references taken
                    // under an earlier hold, and the gate is released between
                    // chunks. If the tree has changed shape since — and a close
                    // is exactly that — those references may name images whose
                    // archive has been disposed, whose property collection is now
                    // null, and for which UnparseImage would throw with the
                    // release loop half done. The same test every other chunked
                    // walk in the app uses: if the generation moved, this pass is
                    // describing a session that no longer exists, so stop.
                    if (_session.Generation != treeGeneration)
                    {
                        stoppedEarly = true;
                        report.Reason = "The session changed while the sweep was running; it stopped early.";
                        break;
                    }

                    int end = Math.Min(i + ChunkSize, candidates.Count);
                    for (; i < end; i++)
                    {
                        WzImage image = candidates[i];

                        // Re-tested inside the gate, against the live flags,
                        // immediately before the release. The list above was
                        // built under an earlier gate hold and an edit can have
                        // landed in between — this is the check that actually
                        // protects the user's work, and it is deliberately the
                        // same expression rather than a cached decision.
                        if (!IsReleasable(image, history))
                        {
                            report.Kept++;
                            continue;
                        }

                        image.UnparseImage();
                        report.Swept++;
                    }
                }

                BetweenChunks?.Invoke();
            }

            // Nothing was released, and the last sweep over this same tree
            // released nothing either — so the collection below has already been
            // proved not to help, and it is the expensive half.
            //
            // This is not a corner case, it is the normal state of a session with
            // a split client open. Every image a .ms pack contributes is marked
            // Changed by WzMsFile.LoadAsWzFile (there is no source block to copy,
            // so the writer must re-serialise), and IsReleasable refuses a Changed
            // image — correctly, and for the reason at the top of this file. So
            // such a session sits permanently above AutoSweepThresholdBytes and
            // permanently returns Swept = 0.
            //
            // Measured on this machine against C:\Nexon\...\appdata, split Skill
            // open (602 images, 2,641 MB working set), four sweeps back to back:
            //
            //   swept 0, kept 602, reclaimed 3 MB, 142 ms
            //   swept 0, kept 602, reclaimed 0 MB, 157 ms
            //   swept 0, kept 602, reclaimed 0 MB, 141 ms
            //   swept 0, kept 602, reclaimed 0 MB, 131 ms
            //
            // and with split Mob open (11,985 images) the same futile sweep cost
            // 533 ms. Every one of those is a blocking, compacting gen2 that stops
            // every in-flight request for that long and gives back nothing. The
            // warm-up alone asks three times.
            //
            // After this guard, same machine, split Mob open (11,985 images):
            //
            //   sweep 1  swept 0, kept 11,985, reclaimed 430 MB, 484 ms   (runs)
            //   sweep 2  swept 0, kept 11,985, reclaimed   0 MB,   7 ms   (skipped)
            //   sweep 3  swept 0, kept 11,985, reclaimed   0 MB,   8 ms   (skipped)
            //   sweep 4  swept 0, kept 11,985, reclaimed   0 MB,   6 ms   (skipped)
            //
            // 484 ms -> 6-8 ms for the same zero, and the 430 MB the first one
            // was worth is still collected.
            //
            // The *first* futile sweep still pays, deliberately: it is the one
            // that proves the heap is incompressible, and it does collect the
            // garbage the work before it left behind (571 MB, measured, on the
            // sweep straight after an open). Only the identical repeat is skipped,
            // and "identical" is exact — a different tree generation or a
            // different number of parsed images means something happened since,
            // and the sweep runs in full again.
            int generation = _session.Generation;
            // A pass that stopped early proved nothing about this tree, so it
            // must not be recorded as a futile one — doing so would make the
            // next, legitimate sweep skip its collection on the strength of a
            // measurement that was never finished.
            bool futileRepeat = !stoppedEarly
                && report.Swept == 0
                && _futileGeneration == generation
                && _futileParsed == report.ParsedBefore;
            if (report.Swept == 0 && !stoppedEarly)
            {
                _futileGeneration = generation;
                _futileParsed = report.ParsedBefore;
            }
            else
            {
                _futileGeneration = -1;
                _futileParsed = -1;
            }

            if (futileRepeat)
            {
                report.WorkingSetAfterMB = report.WorkingSetBeforeMB;
                report.ElapsedMs = (int)clock.ElapsedMilliseconds;
                report.Reason =
                    $"Nothing here can be released ({report.Kept} images are changed, held by undo, or " +
                    "have no block on disk to re-read), and the last sweep found the same, so the " +
                    "collection was skipped.";
                return report;
            }

            // The point of the exercise is the working set, so the collection is
            // not optional: without it the property graphs stay in gen2 until
            // something else forces a collection, and the number the user sees
            // in Task Manager does not move. Compacting, because that is what
            // actually hands the pages back rather than leaving a sparse heap.
            //
            // Aggressive, and exactly one pass. The mode is not interchangeable
            // here: Forced collects, but Aggressive is the one that decommits,
            // and decommitting is the entire point — measured on the same
            // session, a single Forced compacting gen2 moved the working set
            // 828 MB -> 789 MB while Aggressive moved 2,210 MB -> 191 MB. What
            // was dropped is the *repetition*: the first version ran Aggressive,
            // then WaitForPendingFinalizers, then a second Forced pass, three
            // stop-the-world compactions where one does the job, and a request
            // arriving during them waited for all three (a cached /api/mob/list
            // took 5.4s to answer).
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            report.WorkingSetAfterMB = WorkingSetBytes / (1024 * 1024);
            report.ReclaimedMB = Math.Max(0, report.WorkingSetBeforeMB - report.WorkingSetAfterMB);
            report.ElapsedMs = (int)clock.ElapsedMilliseconds;

            // Measured on a v232 client (29 archives, 21.7 GB), one process:
            //
            //   after browsing NPCs, mobs and skills in one go
            //     17,262 images released, 2,210 MB -> 191 MB, 420 ms
            //   run between sections, which is what the warm-up does
            //     10,780 released, 807 MB -> 133 MB, 179 ms
            //      6,000 released, 852 MB -> 169 MB, 190 ms
            //        460 released, 731 MB -> 160 MB, 156 ms
            //
            // and with an unsaved edit in Mob.wz present, that image is reported
            // under Kept and its properties are still there afterwards — see the
            // Changed guard in IsReleasable.
            _log.LogInformation(
                "Released {Swept} parsed images ({Kept} kept), {Before} MB -> {After} MB in {Ms} ms",
                report.Swept, report.Kept, report.WorkingSetBeforeMB, report.WorkingSetAfterMB, report.ElapsedMs);
            return report;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// The whole safety rule, in one place, called both when the candidate list
    /// is built and again under the gate immediately before each release.
    /// </summary>
    private static bool IsReleasable(WzImage image, HashSet<WzImage> history)
    {
        // Nothing to give back.
        if (!image.Parsed)
            return false;

        // *** The line that protects the user's work. Do not weaken it. ***
        // Unparsing a changed image throws the edit away and the save then
        // silently writes the original bytes back and reports success.
        if (image.Changed)
            return false;

        // A redo closure captured properties inside this image; replacing them
        // would make that redo a no-op that still marks the file dirty.
        if (history.Contains(image))
            return false;

        // No on-disk block to re-read from. ParseImage locks on a reader this
        // image does not have, so releasing it would lose the contents outright
        // and throw on the next read.
        return image.BlockSize > 0 && image.Offset > 0;
    }
}
