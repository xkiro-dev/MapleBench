using System.Diagnostics;

namespace MapleBench.Services;

/// <summary>What one pressure check decided.</summary>
public sealed class MemoryPressureDto
{
    /// <summary>Free machine memory when the check ran, or -1 when unknown.</summary>
    public long FreeMB { get; set; }
    public long WorkingSetMB { get; set; }
    public bool UnderPressure { get; set; }
    public bool Acted { get; set; }
    public int ImagesReleased { get; set; }
    public long CachesDroppedMB { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Gives memory back while the app is still able to, instead of after it is not.
///
/// The hole this fills: <see cref="ImageMemoryService"/> could already release
/// the parsed property graphs that a browse leaves behind, but nothing ever
/// asked it to outside the warm-up that follows an open and one manual endpoint.
/// So a session that opened its archives, warmed once, and was then *used* —
/// browsing, searching, porting, rendering — grew from that point on with no
/// upper bound and nothing watching. Three large archives open and a couple of
/// deep browses is 3.7 GB, and the end of that road is not a slow app: it is an
/// allocation failure inside a parse or a collection, which from the outside is
/// the window disappearing with nothing written to the log.
///
/// So this polls, cheaply, and acts before the ceiling rather than at it:
///
///   * <b>free machine memory</b> is the trigger, not this process's working
///     set. The question that matters is whether the next parse has anywhere to
///     go, and that depends on everything the user is running, not on us. A
///     platform that cannot answer is left alone — "cannot tell" is not "no
///     room".
///   * the cheap half runs first. Rebuildable caches — rendered PNGs, item icons
///     — are dropped before any image is released, because they cost a redraw to
///     rebuild and nothing else.
///   * then <see cref="ImageMemoryService.Sweep"/>, which is the one that gives
///     back hundreds of megabytes, and which is emphatic about never touching an
///     image holding unsaved work. Everything that file protects is protected
///     here; this class only decides *when* to ask.
///   * if the machine is still short afterwards, that is said once, at warning
///     level, naming the number. A user whose next action is about to be refused
///     should have been told why before the refusal, not by it.
///
/// What it deliberately does not do is force a collection on a timer. The sweep
/// does that when it has released something; doing it on a schedule is a
/// stop-the-world pause charged to a session that may be perfectly healthy.
/// </summary>
public sealed class MemoryPressureService : BackgroundService
{
    /// <summary>
    /// Free machine memory below which the caches and parsed images are worth
    /// giving back.
    ///
    /// Above this there is room for the work in front of the user and a sweep
    /// would only buy the next browse a re-parse. Two gigabytes is roughly "one
    /// more large archive's worth of headroom" — far enough above
    /// <see cref="WzSessionService.MinimumFreeBytesToOpen"/> that the release
    /// happens well before an open would be refused, which is the whole point of
    /// having both.
    /// </summary>
    public const long PressureFloorBytes = 2048L * 1024 * 1024;

    /// <summary>
    /// Free memory below which the situation is reported rather than merely
    /// acted on. This is close enough to the floor an open is refused at that
    /// the user is about to notice anyway.
    /// </summary>
    public const long CriticalFloorBytes = 768L * 1024 * 1024;

    /// <summary>
    /// How often to look.
    ///
    /// The check itself is a <see cref="GC.GetGCMemoryInfo()"/> read and a
    /// working-set read, microseconds, and it takes no lock — so the interval is
    /// set by how fast memory can move, not by what the check costs. A domain
    /// build adds a gigabyte in a few seconds, so anything slower than this
    /// would routinely miss the rise it exists to catch.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ImageMemoryService _memory;
    private readonly WzSessionService _session;
    private readonly WzRenderService _render;
    private readonly IconService _icons;
    private readonly ILogger<MemoryPressureService> _log;

    /// <summary>
    /// Whether the last check was already critical, so the warning is written on
    /// the way in rather than every five seconds for as long as it lasts.
    /// </summary>
    private bool _warned;

    public MemoryPressureService(
        ImageMemoryService memory, WzSessionService session, WzRenderService render,
        IconService icons, ILogger<MemoryPressureService> log)
    {
        _memory = memory;
        _session = session;
        _render = render;
        _icons = icons;
        _log = log;
    }

    /// <summary>
    /// How much memory the machine has left, or -1 when that cannot be answered.
    /// A test seam, for the same reason the two other guards have one: a check
    /// that only runs when the machine is out of memory is a check nobody has
    /// ever seen run.
    /// </summary>
    internal Func<long> FreeMemoryBytes { get; set; } = SystemMemory.FreeBytes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Check(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// One pass. Public so a test — and the diagnostics endpoint — can run it
    /// without waiting five seconds for a timer.
    /// </summary>
    public MemoryPressureDto Check(CancellationToken cancel = default)
    {
        long free = FreeMemoryBytes();
        MemoryPressureDto report = new()
        {
            FreeMB = free < 0 ? -1 : free / (1024 * 1024),
            WorkingSetMB = ImageMemoryService.WorkingSetBytes / (1024 * 1024),
        };

        if (free < 0)
        {
            report.Reason = "This platform does not report a memory limit; nothing is assumed.";
            return report;
        }

        if (free >= PressureFloorBytes)
        {
            _warned = false;
            report.Reason = "There is room; nothing was released.";
            return report;
        }

        report.UnderPressure = true;

        // Nothing open means nothing of ours to give back, and dropping caches
        // that are already empty is not worth a log line.
        if (_session.FileCount == 0)
        {
            report.Reason = "Short of memory, but no archives are open; nothing here to release.";
            return report;
        }

        report.Acted = true;

        // Cheapest first: these cost a redraw to rebuild and hold nothing the
        // user could lose.
        long droppedBytes = _render.DropCache();
        _icons.Invalidate();
        report.CachesDroppedMB = droppedBytes / (1024 * 1024);

        SweepReportDto sweep = _memory.Sweep(cancel);
        report.ImagesReleased = sweep.Swept;

        long after = FreeMemoryBytes();
        report.FreeMB = after < 0 ? -1 : after / (1024 * 1024);
        report.WorkingSetMB = ImageMemoryService.WorkingSetBytes / (1024 * 1024);

        if (after >= 0 && after < CriticalFloorBytes)
        {
            report.Reason =
                $"This machine has {after / (1024 * 1024)} MB of memory free with " +
                $"{_session.FileCount} archives open, and {sweep.Kept} parsed images cannot be released " +
                "(they hold unsaved changes, are held by undo, or came out of a pack and have no block " +
                "on disk to re-read). Close an archive you are not using before opening another.";
            if (!_warned)
            {
                _warned = true;
                _log.LogWarning("{Reason}", report.Reason);
            }
            return report;
        }

        _warned = false;
        report.Reason =
            $"Released {sweep.Swept} parsed images and {report.CachesDroppedMB} MB of cached renders; " +
            $"{report.FreeMB} MB free.";

        // Only when something actually moved. A session sitting just under the
        // floor with nothing releasable — which is the normal state with packs
        // open — was writing this line every five seconds for as long as it
        // lasted, and a log that repeats itself is one nobody reads at the
        // moment it finally says something new. The one line that has to be
        // seen is the warning above, and it is written once per episode.
        if (sweep.Swept > 0 || report.CachesDroppedMB > 0)
        {
            _log.LogInformation(
                "Memory pressure: {Reason} (working set {WorkingSet} MB)", report.Reason, report.WorkingSetMB);
        }
        return report;
    }
}
