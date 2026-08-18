using System.Diagnostics;

namespace MapleBench.Services;

/// <summary>
/// Builds the section indexes in the background, while the user is still
/// looking at the file tree.
///
/// The cost this hides is real and measured on a v232 client (29 archives):
/// the string pool takes 2.6s, the mob list 7.5s, the skill list 8.1s and the
/// NPC list 3.3s to build the first time. Each is correctly cached against
/// <see cref="WzSessionService.Generation"/>, so it is once per generation — but
/// paid on demand it lands as a twenty-second freeze the first time the user
/// clicks Skills. Opening a client is the first moment we know all of that may
/// be wanted, but it is also when the user is most likely to start navigating.
/// The work therefore begins only after a short quiet period and every API
/// request pushes it back again.
///
/// Three properties this must have, and does:
///
///   * it never blocks the request that starts it — <c>/api/files/open-many</c>
///     returns as soon as the archives are open;
///   * it is cancellable, and is cancelled by anything that would invalidate
///     what it is building (another open, a close, a save) so it cannot sit
///     there rebuilding a tree that has moved on;
///   * it holds the session gate only in the same short chunks an interactive
///     build does, because it calls exactly the same builders. A request cancels
///     the pass before entering its endpoint, so foreground work always wins.
/// </summary>
public sealed class WarmupService : IDisposable
{
    private readonly WzSessionService _session;
    private readonly StringPoolService _strings;
    private readonly MobService _mobs;
    private readonly NpcService _npcs;
    private readonly SkillService _skills;
    private readonly ImageMemoryService _memory;
    private readonly ILogger<WarmupService> _log;

    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task? _running;
    private int _foregroundRequests;
    private bool _disposed;

    /// <summary>
    /// A deliberate pause after the last request. Long enough to cover the burst
    /// of capability and file-list calls made after opening a client, short
    /// enough that an untouched app still has warm indexes before the user has
    /// finished orienting themselves.
    /// </summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(1500);

    public WarmupService(
        WzSessionService session, StringPoolService strings, MobService mobs,
        NpcService npcs, SkillService skills, ImageMemoryService memory, ILogger<WarmupService> log)
    {
        _session = session;
        _strings = strings;
        _mobs = mobs;
        _npcs = npcs;
        _skills = skills;
        _memory = memory;
        _log = log;
    }

    /// <summary>
    /// Off with <c>--no-warmup</c>.
    ///
    /// It exists to be measured: with the warm-up on, "how long does a cold
    /// /api/skill/list take" cannot be asked, because the answer is always "it
    /// was already built". Turning it off is how the on-demand path's own
    /// numbers stay checkable.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Marks one API request as foreground work and immediately cancels any
    /// speculative pass. Counting matters: progress polls can finish while the
    /// import they report is still running, and the warm-up must wait for both.
    /// </summary>
    public void BeginForeground()
    {
        lock (_gate)
        {
            if (!Enabled || _disposed)
                return;

            _foregroundRequests++;
            CancelLocked();
        }
    }

    /// <summary>
    /// Keeps speculative indexing paused for work that outlives its HTTP
    /// response, such as a dump or audit. Dispose exactly once when the job's
    /// real task finishes; the lease is idempotent to make failure cleanup safe.
    /// </summary>
    public IDisposable HoldForeground()
    {
        BeginForeground();
        return new ForegroundLease(this);
    }

    /// <summary>Ends one foreground request and starts the idle clock at zero.</summary>
    public void EndForeground()
    {
        lock (_gate)
        {
            if (_foregroundRequests == 0)
                return;

            _foregroundRequests--;
            if (_foregroundRequests == 0 && Enabled && !_disposed)
                ScheduleLocked();
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ScheduleLocked()
    {
        CancelLocked();

        CancellationTokenSource next = new();
        Task? previous = _running;
        _cancel = next;

        // A replacement waits for the cancelled pass to leave before it
        // starts. Besides avoiding duplicate work, this makes _running a
        // chain: CancelAndWait on the newest task also waits for every older
        // pass that could still be inside a parsing chunk.
        _running = Task.Run(async () =>
        {
            if (previous != null)
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* Run owns its errors; a cancelled predecessor is done. */ }
            }

            try
            {
                await Task.Delay(IdleDelay, next.Token).ConfigureAwait(false);
                Run(next.Token);
            }
            catch (OperationCanceledException)
            {
                // A request arrived during the quiet period.
            }
        });
    }

    /// <summary>
    /// Stops the warm-up without waiting for it.
    ///
    /// Not waited on deliberately: the caller is usually a request that is about
    /// to take the session gate, and blocking it until a chunk finishes would
    /// reintroduce exactly the wait this whole exercise removes. The warm-up
    /// checks its token between chunks and drops whatever it had built.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
            CancelLocked();
    }

    private void CancelLocked()
    {
        // Cancelled but deliberately not disposed. The worker still holds the
        // token; dropping this reference lets the source be collected with it.
        _cancel?.Cancel();
        _cancel = null;
    }

    /// <summary>
    /// Cancels, then waits a bounded time for the warm-up to actually stop.
    ///
    /// For the one caller that needs more than <see cref="Cancel"/>: closing an
    /// archive. Every other caller is about to take the gate and is right not to
    /// wait, but a close *disposes* the objects the warm-up is holding, and the
    /// warm-up only notices it has been cancelled at a chunk boundary. Between
    /// those two facts is a window in which a background thread is reading a tree
    /// that no longer exists.
    ///
    /// Bounded, and it does not take the session gate: the thing being waited for
    /// needs that gate to reach its next boundary, so holding it here would be a
    /// deadlock rather than a wait. If the budget runs out the close proceeds
    /// anyway — the generation checks in the chunked walks are the second line of
    /// defence and they are the one that has to hold.
    /// </summary>
    /// <returns>True when the warm-up had finished by the time we gave up waiting.</returns>
    public bool CancelAndWait(TimeSpan budget)
    {
        Task? running;
        lock (_gate)
        {
            CancelLocked();
            running = _running;
        }

        if (running == null || running.IsCompleted)
            return true;

        try
        {
            return running.Wait(budget);
        }
        catch (AggregateException)
        {
            // Run() swallows everything itself, so this is unreachable in
            // practice; a warm-up that failed is still a warm-up that stopped.
            return true;
        }
    }

    private void Run(CancellationToken cancel)
    {
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            if (_session.FileCount == 0)
                return;

            // Names first: every list below asks for them, and building the pool
            // once here means none of them pays for it mid-build.
            Step("string pool", cancel, () =>
                _strings.Warm(cancel, allowExclusiveFallback: false));

            // Cheapest first, so the section a user is most likely to open
            // straight away is ready soonest and a cancellation part-way through
            // still leaves something warm.
            //
            // Swept between sections rather than only at the end, which is what
            // keeps the *peak* down as well as the resting figure: each list
            // build is a different archive, so releasing Npc.wz's images before
            // parsing Mob.wz's costs the next section nothing and stops the
            // three of them being resident at once. Measured on a v232 client,
            // that is the difference between peaking at 2.2 GB and peaking a
            // little over one section's worth.
            if (_npcs.IsAvailable)
            {
                Step("npc list", cancel, () =>
                    _npcs.List(null, true, cancel, allowExclusiveFallback: false));
                Step("sweep", cancel, () => _memory.SweepIfHeavy(cancel));
            }
            if (_mobs.IsAvailable)
            {
                Step("mob list", cancel, () =>
                    _mobs.List(null, true, cancel, allowExclusiveFallback: false));
                Step("sweep", cancel, () => _memory.SweepIfHeavy(cancel));
            }
            if (_skills.IsAvailable)
            {
                Step("skill list", cancel, () =>
                    _skills.List(null, null, true, cancel, allowExclusiveFallback: false));
            }

            if (cancel.IsCancellationRequested)
                return;

            // Building those three is what puts ~2 GB on the process; the DTOs
            // are what the sections actually read from, so the parsed images
            // behind them can go back. Only images with no unsaved work are
            // touched -- see ImageMemoryService, which is emphatic about it.
            SweepReportDto report = _memory.SweepIfHeavy(cancel);

            _log.LogInformation(
                "Warm-up finished in {Ms} ms; released {Swept} images, {Before} MB -> {After} MB",
                clock.ElapsedMilliseconds, report.Swept, report.WorkingSetBeforeMB, report.WorkingSetAfterMB);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("Warm-up cancelled after {Ms} ms", clock.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // A warm-up is an optimisation. Anything it cannot do, the on-demand
            // path will do again when the user asks for it, so a failure here is
            // a log line and never a failed request.
            _log.LogDebug(ex, "Warm-up stopped early");
        }
    }

    private void Step(string what, CancellationToken cancel, Action work)
    {
        if (cancel.IsCancellationRequested)
            return;

        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            work();
            _log.LogDebug("Warmed {What} in {Ms} ms", what, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not warm {What}", what);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelLocked();
        }
    }

    private sealed class ForegroundLease : IDisposable
    {
        private WarmupService? _owner;

        public ForegroundLease(WarmupService owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndForeground();
    }
}
