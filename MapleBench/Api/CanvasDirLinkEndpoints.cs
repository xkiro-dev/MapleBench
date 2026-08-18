using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The canvas-directory inline repair's HTTP surface — the third wound of the
/// same write: `_outlink`s pointing into `_Canvas` split-art directories,
/// which resolve in an editor and draw nothing in a v232 client.
///
/// Paired with the auditor the same way the other two repairs are: the auditor
/// finds <c>outlink.into_canvas_dir</c>, and this inlines the art it names.
/// <c>POST /repair/canvas-dir-link/from-audit</c> takes the finding list
/// straight off the last audit report, so the repair acts on the links the
/// user was shown rather than on a set it found by itself and never displayed.
///
/// The safety model is the canvas-format repair's, unchanged:
///
///   POST /scan  — opens archives read-only, measures, writes nothing.
///   POST /apply — writes a NEW archive beside the source, never the source,
///                 refuses without confirm=true, and installs nothing: it
///                 hands back the backup-first command and the user runs it.
///   /progress   — polled, because a sweep of gigabytes is tens of seconds.
///
/// Every verb that starts work goes through <c>StartScan</c>/<c>StartApply</c>,
/// which RESERVE the service on the caller's thread — a busy refusal answers
/// 409 with the reason, never a 200 carrying somebody else's progress.
/// </summary>
public static class CanvasDirLinkEndpoints
{
    public static void MapCanvasDirLink(this RouteGroupBuilder api)
    {
        api.MapPost("/repair/canvas-dir-link/scan", (CanvasDirLinkOptions options, CanvasDirLinkRepairService repair) =>
            Started(() => repair.StartScan(options)));

        /* Driven by the audit, which is the point: the repair acts on the
           findings the user was shown. 404 rather than an empty run when no
           audit has finished — "nothing is broken" and "nothing looked" are
           different answers. */
        api.MapPost("/repair/canvas-dir-link/from-audit",
            (CanvasDirLinkOptions options, CanvasDirLinkRepairService repair, IntegrityAuditService audit) =>
        {
            AuditReport? report = audit.Report();
            if (report == null)
                return Results.NotFound(new
                {
                    error = "No audit has finished in this session, so there are no outlink.into_canvas_dir " +
                            "findings to drive an inline from. Run the auditor first.",
                });

            string[] links = report.Findings
                .Where(f => f.Check == "outlink.into_canvas_dir" && !string.IsNullOrEmpty(f.Target))
                .Select(f => f.Target!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (links.Length == 0)
                return Results.Ok(new
                {
                    note = "The last audit reported no _outlink into a _Canvas directory at all. There is " +
                           "nothing here to inline, which is a result and not a failure.",
                    snapshot = repair.Snapshot(),
                });

            /* The audit keeps at most MaxPerCheck findings per check while
               counting all of them, so a truncated run hands over fewer links
               than it found. Said here rather than discovered later as a
               repair that came up short. */
            long counted = report.Checks
                .Where(c => c.Id == "outlink.into_canvas_dir")
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
                    snapshot = repair.StartScan(options),
                });
            }
            catch (InvalidOperationException busy)
            {
                return Results.Conflict(new { error = busy.Message, snapshot = repair.Snapshot() });
            }
        });

        api.MapPost("/repair/canvas-dir-link/apply", (CanvasDirLinkOptions options, CanvasDirLinkRepairService repair) =>
        {
            if (!options.Confirm)
                return Results.BadRequest(new
                {
                    error = "An inline writes a new archive the size of the source. Pass confirm=true.",
                });

            return Started(() => repair.StartApply(options));
        });

        api.MapGet("/repair/canvas-dir-link/progress", (CanvasDirLinkRepairService repair) =>
            Results.Ok(repair.Snapshot()));

        api.MapPost("/repair/canvas-dir-link/cancel", (CanvasDirLinkRepairService repair) =>
        {
            repair.Cancel();
            return Results.Ok(repair.Snapshot());
        });

        // 404 rather than an empty scan: "nothing is broken" and "nothing
        // looked" are different answers.
        api.MapGet("/repair/canvas-dir-link/scan", (CanvasDirLinkRepairService repair) =>
        {
            CanvasDirLinkScan? scan = repair.LastScan();
            return scan == null
                ? Results.NotFound(new { error = "No canvas-dir-link scan has finished in this session." })
                : Results.Ok(scan);
        });

        api.MapGet("/repair/canvas-dir-link/result", (CanvasDirLinkRepairService repair) =>
        {
            CanvasDirLinkResult? result = repair.LastResult();
            return result == null
                ? Results.NotFound(new { error = "No canvas-dir-link inline has been applied in this session." })
                : Results.Ok(result);
        });
    }

    /// <summary>
    /// Starts work and turns a refusal into a 409 rather than into a 200 that
    /// carries somebody else's progress.
    /// </summary>
    private static IResult Started(Func<CanvasDirLinkProgress> start)
    {
        try { return Results.Ok(start()); }
        catch (InvalidOperationException refused) { return Results.Conflict(new { error = refused.Message }); }
    }
}
