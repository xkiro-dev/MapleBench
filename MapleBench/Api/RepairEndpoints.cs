using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The canvas-format repair's HTTP surface.
///
/// Shaped after <see cref="AuditEndpoints"/> and paired with it: the auditor is
/// what finds `canvas.row_width_zero`, and this is what fixes it. It takes a
/// path rather than the session for the same reason the auditor does — what is
/// being repaired is a file, and the session holds whatever the user happened
/// to open.
///
/// The split between the two verbs is the whole safety model:
///
///   GET-shaped work  — /repair/canvas-format/scan — opens archives read-only,
///                      measures, and writes nothing. Safe to run on anything,
///                      including a live client.
///   POST /apply      — writes a NEW archive beside the source, never the
///                      source, and refuses without confirm=true. It does not
///                      install anything: it returns the command to install it,
///                      with the backup first, and the user runs that.
///
/// So there is no request this API can serve that modifies a client the user is
/// playing. That is a property of the endpoints, not a convention: /apply has
/// no argument that means "in place", and the service refuses an output path
/// equal to its input.
///
/// Every verb that starts work goes through <c>StartScan</c>/<c>StartApply</c>,
/// which RESERVE the service on the caller's thread before returning. The
/// obvious shape — <c>Task.Run(() =&gt; repair.Scan(options))</c> and hand back a
/// snapshot — was what this file did, and it is wrong: with a run already in
/// flight the refusal is thrown inside the task where nothing sees it, and the
/// 200 carries the OTHER run's progress, which reads exactly like acceptance.
/// The request is dropped and the caller then polls somebody else's work to
/// completion and believes it is its own. It now answers 409 with the reason.
/// The donor restore found this by being driven; the same bug was still here.
/// </summary>
public static class RepairEndpoints
{
    public static void MapRepair(this RouteGroupBuilder api)
    {
        // Read-only. Runs on a background thread and reports through /progress,
        // because a sweep of a 2 GB archive is tens of seconds and the one
        // connection that must not hang is the one reporting on the work.
        api.MapPost("/repair/canvas-format/scan", (CanvasRepairOptions options, CanvasFormatRepairService repair) =>
            Started(() => repair.StartScan(options)));

        api.MapPost("/repair/canvas-format/apply", (CanvasRepairOptions options, CanvasFormatRepairService repair) =>
        {
            if (!options.Confirm)
                return Results.BadRequest(new
                {
                    error = "A repair writes a new archive the size of the source. Pass confirm=true.",
                });

            return Started(() => repair.StartApply(options));
        });

        api.MapGet("/repair/canvas-format/progress", (CanvasFormatRepairService repair) =>
            Results.Ok(repair.Snapshot()));

        api.MapPost("/repair/canvas-format/cancel", (CanvasFormatRepairService repair) =>
        {
            repair.Cancel();
            return Results.Ok(repair.Snapshot());
        });

        // 404 rather than an empty scan, for the reason /audit/report does it:
        // "nothing is broken" and "nothing looked" are different answers, and a
        // UI that renders the first for the second tells the user their client
        // is clean when it is not.
        api.MapGet("/repair/canvas-format/scan", (CanvasFormatRepairService repair) =>
        {
            CanvasRepairScan? scan = repair.LastScan();
            return scan == null
                ? Results.NotFound(new { error = "No canvas-format scan has finished in this session." })
                : Results.Ok(scan);
        });

        api.MapGet("/repair/canvas-format/result", (CanvasFormatRepairService repair) =>
        {
            CanvasRepairResult? result = repair.LastResult();
            return result == null
                ? Results.NotFound(new { error = "No canvas-format repair has been applied in this session." })
                : Results.Ok(result);
        });
    }

    /// <summary>
    /// Starts work and turns a refusal into a 409 rather than into a 200 that
    /// carries somebody else's progress.
    /// </summary>
    private static IResult Started(Func<CanvasRepairProgress> start)
    {
        try { return Results.Ok(start()); }
        catch (InvalidOperationException refused) { return Results.Conflict(new { error = refused.Message }); }
    }
}
