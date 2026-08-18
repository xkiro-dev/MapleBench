using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The generation chooser's HTTP surface — the per-skill answer to the 261
/// conflicted donor restores that the all-or-nothing switch could not give.
///
///   POST /repair/genchoice/prepare — the donor restore's own scan, then both
///                     generations of every conflicted skill decoded to frames.
///                     Read-only; writes nothing.
///   GET  /repair/genchoice/report  — the groups: one accept/reject decision
///                     each, with composed frame sets for both sides.
///   GET  /repair/genchoice/frame/{id} — one decoded frame as PNG.
///   POST /repair/genchoice/build   — drives the two existing repairs with the
///                     accepted set: donor restore first, canvas-format repair
///                     over its output second (the recorded order), chained in
///                     the RepairLedger. Writes NEW archives beside the source
///                     and installs nothing; the result carries the backup-first
///                     install command as text.
///
/// Every verb that starts work reserves the service ON THIS THREAD before
/// returning, so a second request while one runs is a 409 with the reason —
/// never a 200 carrying somebody else's progress. The shape (and the bug it
/// prevents) is documented on <see cref="DonorRestoreEndpoints"/>; it was found
/// by driving these endpoints' siblings, not by reading them.
///
/// The chooser is a singleton like the two services it drives, so its busy gate
/// and progress are app-wide. DI also gives its background task the shared
/// foreground-activity lease that keeps speculative indexing out of its way.
/// </summary>
public static class GenerationChoiceEndpoints
{
    public static void MapGenerationChoice(this RouteGroupBuilder api)
    {
        api.MapPost("/repair/genchoice/prepare",
            (DonorRestoreOptions options, DonorRestoreService restore, GenerationChooserService chooser) =>
        {
            if (string.IsNullOrWhiteSpace(options.Folder))
                return Results.BadRequest(new
                {
                    error = "The chooser needs the client folder whose Skill family is being judged.",
                });
            if ((options.Donors?.Length ?? 0) == 0)
                return Results.BadRequest(new
                {
                    error = "The chooser needs at least one donor archive — without one there is no " +
                            "older generation to show, let alone to restore.",
                });

            /* Advisory pre-check on the service the prepare is about to drive.
               The chooser's own reservation is the race-free one; this catches
               the common case with a 409 now instead of a background failure a
               poll later. */
            if (restore.Busy)
                return Results.Conflict(new
                {
                    error = "A donor-restore run is already in flight, and the chooser drives that " +
                            "service. Wait for it or cancel it first.",
                });

            return Started(() => chooser.StartPrepare(options, restore));
        });

        /* One poll answers for the whole pipeline: the chooser's own state plus
           the two services it drives, because during a build the interesting
           numbers (images carried, canvases verified) are theirs. */
        api.MapGet("/repair/genchoice/progress",
            (DonorRestoreService restore, CanvasFormatRepairService format,
             GenerationChooserService chooser) =>
                Results.Ok(new
                {
                    chooser = chooser.Snapshot(),
                    restore = restore.Snapshot(),
                    format = format.Snapshot(),
                }));

        api.MapPost("/repair/genchoice/cancel",
            (DonorRestoreService restore, CanvasFormatRepairService format,
             GenerationChooserService chooser) =>
        {
            chooser.Cancel(restore, format);
            return Results.Ok(chooser.Snapshot());
        });

        // 404 rather than an empty report: "no conflicts" and "never prepared"
        // are different answers, and a UI that renders the first for the second
        // would tell the user there is nothing to choose.
        api.MapGet("/repair/genchoice/report", (GenerationChooserService chooser) =>
        {
            GenChoiceReport? report = chooser.LastReport();
            return report == null
                ? Results.NotFound(new { error = "No chooser prepare has finished in this session." })
                : Results.Ok(report);
        });

        api.MapGet("/repair/genchoice/frame/{id:int}", (int id, GenerationChooserService chooser) =>
        {
            byte[]? bytes = chooser.FrameBytes(id);
            return bytes == null
                ? Results.NotFound(new
                {
                    error = "No such frame in the last prepared report — frames are replaced whole " +
                            "when a new prepare finishes.",
                })
                : Results.File(bytes, "image/png");
        });

        api.MapPost("/repair/genchoice/build",
            (GenChoiceBuildRequest request, DonorRestoreService restore,
             CanvasFormatRepairService format, GenerationChooserService chooser) =>
        {
            if (!request.Confirm)
                return Results.BadRequest(new
                {
                    error = "A build writes two new archives the size of the source. Pass confirm=true.",
                });
            if ((request.Donors?.Length ?? 0) == 0)
                return Results.BadRequest(new
                {
                    error = "A build needs at least one donor archive to take the missing nodes from.",
                });

            if (restore.Busy || format.Busy)
                return Results.Conflict(new
                {
                    error = "One of the two repair services this build drives is already running. " +
                            "Wait for it or cancel it first.",
                });

            return Started(() => chooser.StartBuild(request, restore, format));
        });

        api.MapGet("/repair/genchoice/result", (GenerationChooserService chooser) =>
        {
            GenChoiceBuildResult? result = chooser.LastBuild();
            return result == null
                ? Results.NotFound(new { error = "No chooser build has finished in this session." })
                : Results.Ok(result);
        });
    }

    /// <summary>
    /// Starts work and turns a refusal into a 409 rather than into a 200 that
    /// carries somebody else's progress.
    /// </summary>
    private static IResult Started(Func<GenChoiceProgress> start)
    {
        try { return Results.Ok(start()); }
        catch (InvalidOperationException refused) { return Results.Conflict(new { error = refused.Message }); }
    }
}
