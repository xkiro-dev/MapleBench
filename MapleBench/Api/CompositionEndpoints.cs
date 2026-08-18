using MapleBench.Services.Composition;

namespace MapleBench.Api;

/// <summary>
/// The composition builder's HTTP surface.
///
/// Shaped after <see cref="AuditEndpoints"/> and <see cref="RepairEndpoints"/>,
/// because those are the shapes the rest of the app already speaks: start
/// returns immediately with the first snapshot, progress is POLLED rather than
/// streamed (the one connection that must not hang is the one reporting on the
/// work), the result 404s while no run has finished, and a busy service answers
/// <b>409 on the caller's thread</b>. That last one is not style — the
/// Task.Run-then-snapshot shape was shipped twice in this app and both times a
/// refused request was answered 200 with the OTHER run's progress, which reads
/// exactly like acceptance.
///
/// What a build writes is a whole client, so the write-safety rules live below
/// the surface rather than in it: the output folder is explicit and never
/// defaulted; the builder refuses to write into the base or any source; the run
/// service refuses a folder holding an archive this app has mounted; and a
/// non-empty folder the tool did not itself build is refused by name.
/// </summary>
public static class CompositionEndpoints
{
    public static void MapComposition(this RouteGroupBuilder api)
    {
        // Starts a build and returns the first snapshot. Deliberately not
        // wired to the request's CancellationToken: a browser navigating away
        // must not silently abandon a build the user is waiting on. Cancelling
        // is /compose/cancel.
        api.MapPost("/compose/build", (CompositionStartRequest request, CompositionRunService compose) =>
            Started(() => compose.StartBuild(request)));

        api.MapGet("/compose/progress", (CompositionRunService compose) =>
            Results.Ok(compose.Snapshot()));

        api.MapPost("/compose/cancel", (CompositionRunService compose) =>
        {
            compose.Cancel();
            return Results.Ok(compose.Snapshot());
        });

        // The finished build — outcome, refusals, ledger, digest. 404 rather
        // than an empty shape: "no result yet" and "a build that took nothing"
        // are different answers, and a UI that renders the second for the first
        // reports a composition nobody ran.
        api.MapGet("/compose/result", (CompositionRunService compose) =>
        {
            CompositionBuildResult? result = compose.LastResult();
            return result == null
                ? Results.NotFound(new { error = "No composition build has finished in this session." })
                : Results.Ok(result);
        });

        // What a folder holds, for building a manifest: its .wz archives with
        // sizes, and whether a composition ledger already sits beside them.
        // Directory listing only; nothing is opened.
        api.MapGet("/compose/archives", (string path, CompositionRunService compose) =>
            Results.Ok(compose.ListArchives(path)));

        // The ledger beside any folder on disk — how a client built in an
        // earlier session explains itself. 404 when nothing ever recorded one.
        api.MapGet("/compose/ledger", (string path, CompositionRunService compose) =>
            Results.Ok(compose.ReadLedger(path)));
    }

    /// <summary>
    /// Starts work, turning "already running" into a 409 on this thread rather
    /// than a 200 that carries somebody else's progress. Anything else the start
    /// throws — a malformed request is an <see cref="ArgumentException"/> —
    /// falls through to the group's error filter and keeps its own status.
    /// </summary>
    private static IResult Started(Func<CompositionProgress> start)
    {
        try { return Results.Ok(start()); }
        catch (InvalidOperationException refused) { return Results.Conflict(new { error = refused.Message }); }
    }
}
