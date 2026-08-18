using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The client integrity auditor's HTTP surface.
///
/// Kept out of <see cref="Endpoints"/> for the reason <see cref="ImportEndpoints"/>
/// is: one mode, one file. Wired in with <c>MapAudit(api)</c> beside the other
/// <c>Map*</c> calls.
///
/// It takes a FOLDER, not the session. That is the whole design, not a
/// convenience: what the auditor checks is what a client is when it is mounted
/// — a family of archives resolving links into each other — and the session
/// holds whatever the user happened to open, which is usually one archive of a
/// family and is exactly the view that hides a shadowed image. Auditing "what
/// is open" would answer a different question and answer it reassuringly.
///
/// The job shape is <see cref="ImportProgressService"/>'s, for the same reason:
/// a run over a v232 client is minutes of synchronous IO, so it goes on a
/// background thread and the UI polls. A poll, not a stream — the one
/// connection that must not hang is the one reporting on the work.
/// </summary>
public static class AuditEndpoints
{
    public static void MapAudit(this RouteGroupBuilder api)
    {
        // What the auditor would look at, before it looks at it: the archives
        // in the folder grouped into families in mount order, and the ones it
        // is skipping with the reason. Answered without opening anything, so
        // it costs nothing and lets the UI show the plan.
        api.MapGet("/audit/plan", (string path, IntegrityAuditService audit) =>
            Results.Ok(audit.Plan(path)));

        // Starts a run and returns immediately with the first progress
        // snapshot. Deliberately not passing the request's CancellationToken:
        // a browser that navigates away should not silently abandon a run the
        // user is waiting on. Cancelling is /audit/cancel.
        api.MapPost("/audit/start", (
            AuditOptions options, IntegrityAuditService audit, WarmupService warmup) =>
        {
            IDisposable activity = warmup.HoldForeground();
            _ = Task.Run(() =>
            {
                using (activity)
                {
                    try { audit.Run(options); }
                    catch { /* the snapshot carries it */ }
                }
            });
            return Results.Ok(audit.Snapshot());
        });

        api.MapGet("/audit/progress", (IntegrityAuditService audit) =>
            Results.Ok(audit.Snapshot()));

        api.MapPost("/audit/cancel", (IntegrityAuditService audit) =>
        {
            audit.Cancel();
            return Results.Ok(audit.Snapshot());
        });

        // The finished report. 404 rather than an empty one: "no findings" and
        // "no run" are different answers and a UI that renders the first for
        // the second tells the user their client is clean when nothing looked.
        api.MapGet("/audit/report", (IntegrityAuditService audit) =>
        {
            AuditReport? report = audit.Report();
            return report == null
                ? Results.NotFound(new { error = "No audit has finished in this session." })
                : Results.Ok(report);
        });
    }
}
