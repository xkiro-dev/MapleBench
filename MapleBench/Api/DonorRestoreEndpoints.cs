using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The donor-restore repair's HTTP surface — the second thing in this app that
/// offers to fix a client, and the first that needs a second archive to do it.
///
/// It is paired with the auditor the same way the canvas-format repair is: the
/// auditor is what finds <c>outlink.unresolved</c>, and this is what puts the
/// art back. <c>POST /repair/donor-restore/from-audit</c> takes the finding list
/// straight off the last audit report, so the repair acts on the links the user
/// was shown rather than on a set it found by itself and never displayed.
///
/// The safety model is the canvas repair's, unchanged, because the same three
/// questions have the same three answers:
///
///   POST /scan      — opens archives read-only, measures, writes nothing.
///                     Safe against a live client.
///   POST /apply     — writes a NEW archive beside the source, never the source,
///                     and refuses without confirm=true. It does not install
///                     anything: it hands back the command to install it, with
///                     the backup first, and the user runs that.
///   /progress       — because a sweep of five gigabytes is tens of seconds and
///                     the one connection that must not hang is the one
///                     reporting on the work.
///
/// Every verb that starts work goes through <c>StartScan</c>/<c>StartApply</c>,
/// which RESERVE the service on this thread before returning. The obvious
/// shape — <c>Task.Run(() =&gt; restore.Scan(options))</c> and hand back a
/// snapshot — was written first and is wrong, and driving these endpoints is
/// what found it: with a run already in flight the refusal is thrown inside the
/// task where nothing sees it, and the response carries the OTHER run's
/// progress, which reads exactly like acceptance. It now answers 409 with the
/// reason.
///
/// One switch is this repair's own. <c>acceptGenerationMismatch</c> is separate
/// from <c>confirm</c> because it answers a different question: confirm asks
/// whether a caller meant to write at all, and this asks whether the reader has
/// seen that the donor's art and the live client's surviving art are different
/// generations of the same node, and has chosen the donor's anyway. Without it
/// those cases are reported and left dangling — which is a worse-looking audit
/// and a more honest client.
/// </summary>
public static class DonorRestoreEndpoints
{
    public static void MapDonorRestore(this RouteGroupBuilder api)
    {
        api.MapPost("/repair/donor-restore/scan", (DonorRestoreOptions options, DonorRestoreService restore) =>
            Started(() => restore.StartScan(options)));

        /* Driven by the audit, which is the point of the whole thing: the repair
           acts on the findings the user was shown. A 404 rather than an empty
           run when no audit has finished, for the reason /audit/report does it —
           "nothing is broken" and "nothing looked" are different answers. */
        api.MapPost("/repair/donor-restore/from-audit",
            (DonorRestoreOptions options, DonorRestoreService restore, IntegrityAuditService audit) =>
        {
            AuditReport? report = audit.Report();
            if (report == null)
                return Results.NotFound(new
                {
                    error = "No audit has finished in this session, so there are no outlink.unresolved " +
                            "findings to drive a restore from. Run the auditor first.",
                });

            string[] links = report.Findings
                .Where(f => f.Check == "outlink.unresolved" && !string.IsNullOrEmpty(f.Target))
                .Select(f => f.Target!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (links.Length == 0)
                return Results.Ok(new
                {
                    note = "The last audit reported no unresolved _outlink at all. There is nothing here " +
                           "to restore, which is a result and not a failure.",
                    snapshot = restore.Snapshot(),
                });

            /* The audit keeps at most MaxPerCheck findings per check while
               counting all of them, so a run that was truncated hands over fewer
               links than it found. Said here rather than discovered later as a
               restore that came up short. */
            long counted = report.Checks
                .Where(c => c.Id == "outlink.unresolved")
                .Select(c => c.Found)
                .FirstOrDefault();

            options.Links = links;
            if (string.IsNullOrWhiteSpace(options.Folder))
                options.Folder = report.Folder;

            try
            {
                return Results.Ok(new
                {
                    links = links.Length,
                    reportedByAudit = counted,
                    truncated = counted > links.Length,
                    folder = options.Folder,
                    snapshot = restore.StartScan(options),
                });
            }
            catch (InvalidOperationException busy)
            {
                return Results.Conflict(new { error = busy.Message, snapshot = restore.Snapshot() });
            }
        });

        api.MapPost("/repair/donor-restore/apply", (DonorRestoreOptions options, DonorRestoreService restore) =>
        {
            if (!options.Confirm)
                return Results.BadRequest(new
                {
                    error = "A restore writes a new archive the size of the source. Pass confirm=true.",
                });
            if ((options.Donors?.Length ?? 0) == 0)
                return Results.BadRequest(new
                {
                    error = "A restore needs at least one donor archive to take the missing nodes from. " +
                            "Without one there is nothing to put back — the art is absent, not mislabelled.",
                });

            return Started(() => restore.StartApply(options));
        });

        api.MapGet("/repair/donor-restore/progress", (DonorRestoreService restore) =>
            Results.Ok(restore.Snapshot()));

        api.MapPost("/repair/donor-restore/cancel", (DonorRestoreService restore) =>
        {
            restore.Cancel();
            return Results.Ok(restore.Snapshot());
        });

        api.MapGet("/repair/donor-restore/scan", (DonorRestoreService restore) =>
        {
            DonorRestoreScan? scan = restore.LastScan();
            return scan == null
                ? Results.NotFound(new { error = "No donor-restore scan has finished in this session." })
                : Results.Ok(scan);
        });

        api.MapGet("/repair/donor-restore/result", (DonorRestoreService restore) =>
        {
            DonorRestoreResult? result = restore.LastResult();
            return result == null
                ? Results.NotFound(new { error = "No donor restore has been applied in this session." })
                : Results.Ok(result);
        });

        /* The per-skill generation chooser rides the same route group: it is
           this repair's judgement surface — the 261 Conflicted cases decided
           one skill at a time instead of by one switch. */
        api.MapGenerationChoice();
    }

    /// <summary>
    /// Starts work and turns a refusal into a 409 rather than into a 200 that
    /// carries somebody else's progress.
    /// </summary>
    private static IResult Started(Func<DonorRestoreProgress> start)
    {
        try { return Results.Ok(start()); }
        catch (InvalidOperationException refused) { return Results.Conflict(new { error = refused.Message }); }
    }
}
