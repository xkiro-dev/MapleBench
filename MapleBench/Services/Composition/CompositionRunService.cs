using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MapleBench.Services.Composition;

/// <summary>What a caller asks a build to do, over the wire.</summary>
public sealed class CompositionStartRequest
{
    /// <summary>The manifest, structurally. The UI composes it; a person can paste one.</summary>
    public CompositionManifest? Manifest { get; set; }

    /// <summary>
    /// Where the composed client is written. Deliberately not part of the
    /// manifest — see <see cref="CompositionBuildRequest.OutputFolder"/>.
    /// </summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>Compute a content hash per landed node. A second full parse; off by default.</summary>
    public bool HashParts { get; set; }

    /// <summary>See <see cref="CompositionBuildRequest.StopOnRefusal"/>. On, and it should stay on.</summary>
    public bool StopOnRefusal { get; set; } = true;
}

/// <summary>Where the one running (or last run) build is.</summary>
public sealed class CompositionProgress
{
    /// <summary>idle | running | done | failed | cancelled.</summary>
    public string State { get; set; } = "idle";

    /// <summary>What the build is doing right now, in its own units.</summary>
    public string Phase { get; set; } = "";

    public string? Detail { get; set; }

    public int TakesDone { get; set; }
    public int TakesTotal { get; set; }

    /// <summary>The output folder of the run this progress describes.</summary>
    public string? Output { get; set; }

    public double Seconds { get; set; }
    public string? Error { get; set; }
}

/// <summary>One archive a folder holds, for the UI's folder picker.</summary>
public sealed record ComposeArchiveDto(string Name, long Bytes);

/// <summary>What a folder looks like to a composition: its archives and whether a ledger sits beside them.</summary>
public sealed record ComposeFolderDto(string Folder, bool HasLedger, List<ComposeArchiveDto> Archives);

/// <summary>
/// Owns the one composition build a process runs at a time, the way
/// <see cref="CanvasFormatRepairService"/> owns repairs: start RESERVES the
/// service synchronously on the caller's thread and the work runs behind a
/// polled snapshot.
///
/// The reservation order is the load-bearing part. The obvious shape —
/// <c>Task.Run(() =&gt; Build(...))</c> and hand back a snapshot — was shipped
/// twice elsewhere in this app and was wrong both times: with a run already in
/// flight the refusal is thrown inside the task where nothing sees it, and the
/// 200 carries the OTHER run's progress, which reads exactly like acceptance.
/// Here a second start throws <see cref="InvalidOperationException"/> on the
/// caller's thread, which the endpoint turns into a 409 with the reason.
///
/// One further refusal lives here rather than in <see cref="CompositionBuilder"/>,
/// because only this layer can see the app's session: a build may not write into
/// a folder holding an archive that is currently MOUNTED. The builder already
/// refuses its own inputs; a mounted client is an input to the <em>user's</em>
/// session, and overwriting it under a running editor corrupts both.
/// </summary>
public sealed class CompositionRunService
{
    private readonly object _gate = new();
    private readonly WzSessionService _session;
    private readonly WarmupService _warmup;
    private readonly ILoggerFactory _loggers;

    private readonly CompositionProgress _progress = new();
    private CompositionBuildResult? _result;
    private CancellationTokenSource? _cancel;

    public CompositionRunService(
        WzSessionService session, WarmupService warmup, ILoggerFactory loggers)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _warmup = warmup ?? throw new ArgumentNullException(nameof(warmup));
        _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
    }

    public CompositionProgress Snapshot()
    {
        lock (_gate)
            return new CompositionProgress
            {
                State = _progress.State,
                Phase = _progress.Phase,
                Detail = _progress.Detail,
                TakesDone = _progress.TakesDone,
                TakesTotal = _progress.TakesTotal,
                Output = _progress.Output,
                Seconds = _progress.Seconds,
                Error = _progress.Error,
            };
    }

    public void Cancel() { lock (_gate) _cancel?.Cancel(); }

    /// <summary>
    /// The finished build this session last produced, refusals included, or null
    /// when none has finished — which the endpoint answers 404, because "no
    /// result" and "an empty result" are different facts.
    /// </summary>
    public CompositionBuildResult? LastResult() { lock (_gate) return _result; }

    /// <summary>
    /// Reserves the service synchronously, then builds on a background thread.
    /// Throws <see cref="InvalidOperationException"/> — and only for this — when
    /// a build is already running; a malformed request is an
    /// <see cref="ArgumentException"/>, so the two cannot answer with each
    /// other's status code.
    /// </summary>
    public CompositionProgress StartBuild(CompositionStartRequest request)
    {
        if (request == null)
            throw new ArgumentException("The request body was empty.");
        if (request.Manifest == null)
            throw new ArgumentException("A build needs a manifest: the base, the sources and the takes.");
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new ArgumentException(
                "A build needs an output folder. It is not defaulted on purpose: a build writes a whole "
                + "client there, so where it lands has to be said, not guessed.");

        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_progress.State == "running")
            {
                throw new InvalidOperationException(
                    $"A composition build is already running ({_progress.Phase}"
                    + $"{(_progress.Detail is { Length: > 0 } d ? $" — {d}" : "")}). "
                    + "One at a time: watch /api/compose/progress, or cancel it first.");
            }

            cancel = new CancellationTokenSource();
            _cancel = cancel;
            _result = null;
            _progress.State = "running";
            _progress.Phase = "starting";
            _progress.Detail = null;
            _progress.TakesDone = 0;
            _progress.TakesTotal = request.Manifest.Takes.Count;
            _progress.Output = Path.GetFullPath(request.OutputFolder);
            _progress.Seconds = 0;
            _progress.Error = null;
        }

        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunBuild(request, cancel); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    /// <summary>
    /// The archives a folder holds, for the UI to build a manifest from. Reads
    /// the directory listing only; opens nothing.
    /// </summary>
    public ComposeFolderDto ListArchives(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A folder path is required.");

        string folder = Path.GetFullPath(path);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"There is no folder at '{folder}'.");

        List<ComposeArchiveDto> archives = Directory.GetFiles(folder, "*.wz")
            .Select(file => new ComposeArchiveDto(Path.GetFileName(file), new FileInfo(file).Length))
            .OrderBy(archive => archive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ComposeFolderDto(
            folder,
            File.Exists(CompositionLedgerStore.PathFor(folder)),
            archives);
    }

    /// <summary>
    /// The ledger beside a folder on disk — how a finished build explains itself
    /// after the process that ran it is gone.
    /// </summary>
    public CompositionLedger ReadLedger(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A folder path is required.");

        string folder = Path.GetFullPath(path);
        return CompositionLedgerStore.Read(folder)
            ?? throw new FileNotFoundException(
                $"There is no {CompositionSchema.LedgerFileName} beside '{folder}'. Nothing has recorded "
                + "how that folder's archives came to be.");
    }

    private void RunBuild(CompositionStartRequest request, CancellationTokenSource cancel)
    {
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            string? mounted = MountedClientRefusal(request.OutputFolder);
            if (mounted != null)
            {
                CompositionBuildResult refused = new()
                {
                    Outcome = CompositionBuildOutcome.Refused,
                    Refusal = mounted,
                    OutputFolder = Path.GetFullPath(request.OutputFolder),
                };
                refused.Digest = refused.Ledger.Digest();

                lock (_gate)
                {
                    _result = refused;
                    _progress.State = "done";
                    _progress.Phase = "refused";
                    _progress.Detail = mounted;
                }
                return;
            }

            CompositionBuildResult result = new CompositionBuilder(_loggers).Build(new CompositionBuildRequest
            {
                Manifest = request.Manifest,
                OutputFolder = request.OutputFolder,
                HashParts = request.HashParts,
                StopOnRefusal = request.StopOnRefusal,
                Cancellation = cancel.Token,
                Progress = step =>
                {
                    lock (_gate)
                    {
                        _progress.Phase = step.Phase;
                        _progress.Detail = step.Detail;
                        _progress.TakesDone = step.TakesDone;
                        _progress.TakesTotal = step.TakesTotal;
                        _progress.Seconds = clock.Elapsed.TotalSeconds;
                    }
                },
            });

            lock (_gate)
            {
                _result = result;
                _progress.State = "done";
                _progress.Phase = result.Outcome switch
                {
                    CompositionBuildOutcome.Complete => "complete",
                    CompositionBuildOutcome.Refused => "refused",
                    _ => "partial",
                };
                _progress.Detail = result.Outcome == CompositionBuildOutcome.Refused ? result.Refusal : null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _progress.State = "cancelled";
                _progress.Phase = "cancelled";
                _progress.Detail =
                    "The half-built output was discarded — a folder that is neither the base nor the "
                    + "composition must not be left where someone could run a client out of it. The base "
                    + "and the sources are untouched.";
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _progress.State = "failed";
                _progress.Error = ex.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _progress.Seconds = clock.Elapsed.TotalSeconds;
                if (ReferenceEquals(_cancel, cancel))
                {
                    _cancel.Dispose();
                    _cancel = null;
                }
            }
        }
    }

    /// <summary>
    /// The refusal only this layer can make: the output folder holds an archive
    /// the app has mounted right now. The build would overwrite the file under
    /// the session that is editing it.
    /// </summary>
    private string? MountedClientRefusal(string outputFolder)
    {
        string output = TrimSeparators(Path.GetFullPath(outputFolder));

        foreach (OpenFile file in _session.Files)
        {
            if (string.IsNullOrEmpty(file.FilePath))
                continue;

            string? folder;
            try { folder = Path.GetDirectoryName(Path.GetFullPath(file.FilePath)); }
            catch (Exception) { continue; }

            if (folder != null && string.Equals(TrimSeparators(folder), output, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{output}' holds {file.Name}, which is open in this app right now. A build "
                    + "replaces every archive in its output folder, and writing over a mounted client "
                    + "would corrupt both the build and whatever is being edited. Close it here first, "
                    + "or build somewhere else and copy the result in deliberately.";
            }
        }

        return null;
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
