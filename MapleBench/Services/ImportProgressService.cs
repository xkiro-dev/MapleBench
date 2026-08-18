namespace MapleBench.Services;

public sealed class ImportProgressDto
{
    /// <summary>"idle", "running", "done", "cancelled", or "failed".</summary>
    public string State { get; set; } = "idle";
    public string Archive { get; set; } = "";
    /// <summary>What the import is doing right now, in the user's words.</summary>
    public string Stage { get; set; } = "";
    /// <summary>Source files opened so far, or images written — whatever the stage counts.</summary>
    public long Done { get; set; }
    /// <summary>What Done is counting towards, or 0 when it is not yet known.</summary>
    public long Total { get; set; }
    public double Seconds { get; set; }
    public string? Message { get; set; }

    /// <summary>True while a cancel has been asked for but the work has not stopped yet.</summary>
    public bool Cancelling { get; set; }

    /// <summary>
    /// The finished conversion, kept after it completes.
    ///
    /// This is the whole of the "a refresh mid-import loses the result page" fix.
    /// The conversion is minutes of work whose interesting output — where the file
    /// went, how many images, what was warned about — used to exist only in the
    /// HTTP response body. A browser that navigated away, or a laptop that slept,
    /// threw it away and left the user with a file and no idea whether it had been
    /// verified. Polling /import/progress now returns it until the next import
    /// starts.
    /// </summary>
    public ImportResult? Result { get; set; }

    /// <summary>When the terminal state was reached, so a stale result reads as stale.</summary>
    public DateTime? FinishedUtc { get; set; }
}

/// <summary>
/// Runs one import or one split-archive open at a time and reports what it is
/// doing.
///
/// Serialised on purpose. An import reads a whole archive and writes a copy of
/// it, so two at once on the same drive do not go twice as fast — they go
/// slower, and on the 13 GB archives they can fill the target volume between
/// them, which is the one failure that costs the user something they cannot get
/// back by waiting. A second request while one is running is refused with the
/// name of the archive already in flight rather than queued, because a queued
/// import that starts forty minutes later is not what anyone clicking a button
/// expects.
///
/// Opening a split archive read-only shares the gate for the same reason at a
/// smaller scale: assembling Skill holds 930 MB of pack buffers, and doing that
/// while a conversion is writing is how a 31 GB machine starts paging.
/// </summary>
public sealed class ImportProgressService
{
    private readonly object _gate = new();
    private ImportProgressDto _current = new();
    private DateTime _started;
    private bool _running;
    private CancellationTokenSource? _cancel;

    /// <summary>
    /// Runs a conversion, holding the one-at-a-time gate.
    /// </summary>
    /// <param name="session">
    /// Consulted for the free-space estimate. See <see cref="ClientImportService"/>.
    /// </param>
    public ImportResult Run(ImportRequest request, ClientImportService import, WzSessionService? session = null)
    {
        CancellationToken token = Begin(request.Archive);
        try
        {
            ImportResult result = import.Import(request, Report, token, session);
            lock (_gate)
            {
                _current = new ImportProgressDto
                {
                    State = "done",
                    Archive = result.Archive,
                    Stage = "Finished",
                    Done = result.Images,
                    Total = result.Images,
                    Seconds = result.Seconds,
                    Message = $"Wrote {result.WrittenTo}",
                    Result = result,
                    FinishedUtc = DateTime.UtcNow,
                };
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            Finish("cancelled", request.Archive,
                   "Stopped before anything was written. The destination was not touched.");
            throw new InvalidOperationException(
                $"The import of {request.Archive} was cancelled. Nothing was written.");
        }
        catch (Exception ex)
        {
            Finish("failed", request.Archive, ex.Message);
            throw;
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Runs a split-archive open under the same gate, so it reports progress and
    /// can be cancelled exactly as a conversion can.
    /// </summary>
    public T RunOpen<T>(string archive, Func<CancellationToken, T> work,
                        string? successMessage = null, string? cancelMessage = null)
    {
        CancellationToken token = Begin(archive);
        try
        {
            T result = work(token);
            Finish("done", archive, successMessage ?? $"Opened {archive} for reference.");
            return result;
        }
        catch (OperationCanceledException)
        {
            Finish("cancelled", archive, cancelMessage ?? "Stopped. Nothing was opened.");
            throw new InvalidOperationException($"Opening {archive} was cancelled.");
        }
        catch (Exception ex)
        {
            Finish("failed", archive, ex.Message);
            throw;
        }
        finally
        {
            End();
        }
    }

    private CancellationToken Begin(string archive)
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    $"'{_current.Archive}' is already in progress ({_current.Stage}). " +
                    "Wait for it to finish or stop it — two conversions at once are slower than one after the " +
                    "other, and between them they can fill the drive.");
            }
            _running = true;
            _started = DateTime.UtcNow;
            _cancel = new CancellationTokenSource();
            _current = new ImportProgressDto
            {
                State = "running",
                Archive = archive,
                Stage = "Starting",
            };
            return _cancel.Token;
        }
    }

    private void Finish(string state, string archive, string message)
    {
        lock (_gate)
        {
            _current = new ImportProgressDto
            {
                State = state,
                Archive = archive,
                Stage = state == "done" ? "Finished" : "Stopped",
                Seconds = (DateTime.UtcNow - _started).TotalSeconds,
                Message = message,
                FinishedUtc = DateTime.UtcNow,
            };
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _running = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    /// <summary>
    /// Asks the running job to stop. Safe to call when nothing is running.
    ///
    /// Cooperative, and it has to be: the work is one long synchronous call into
    /// MapleLib, so the only places it can stop are the ones this app controls —
    /// between source files during assembly. That means a cancel arriving while
    /// SaveToDisk is half-way through a 13 GB write is honoured when the write
    /// finishes, not during it. Aborting a write mid-stream is worse than
    /// finishing it: the scratch file is deleted either way and the destination is
    /// never touched, so all the user loses by waiting is time they already spent.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_running)
                return;
            _current.Cancelling = true;
            _current.Stage = "Stopping at the next file";
            try { _cancel?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Report(string stage, long done, long total)
    {
        lock (_gate)
        {
            // Only while running. A late callback from a torn-down import must not
            // overwrite the terminal state the UI is about to read, or a failure
            // would be reported as "Reading Mob_00003.ms" forever.
            if (!_running)
                return;
            _current.Stage = stage;
            _current.Done = done;
            _current.Total = total;
            _current.Seconds = (DateTime.UtcNow - _started).TotalSeconds;
        }
    }

    public ImportProgressDto Snapshot()
    {
        lock (_gate)
        {
            return new ImportProgressDto
            {
                State = _current.State,
                Archive = _current.Archive,
                Stage = _current.Stage,
                Done = _current.Done,
                Total = _current.Total,
                Seconds = _running ? (DateTime.UtcNow - _started).TotalSeconds : _current.Seconds,
                Message = _current.Message,
                Cancelling = _current.Cancelling,
                Result = _current.Result,
                FinishedUtc = _current.FinishedUtc,
            };
        }
    }
}
