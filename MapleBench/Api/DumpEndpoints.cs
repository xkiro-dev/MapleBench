using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The dumper's HTTP surface: "what can this node produce", the one-file
/// downloads, and the job that writes a whole subtree to disk.
///
/// Its own group rather than a call inside <see cref="Endpoints"/>, for the
/// same reason <see cref="AuditEndpoints"/> and <see cref="ImportEndpoints"/>
/// are separate files: each feature remains independently reviewable and
/// testable. The group carries its own small error filter.
///
/// The download endpoints all put what did not export into headers as well as
/// into the payload. A browser download shows headers to the page that started
/// it and nothing else — a ZIP's <c>dump-report.json</c> is only read after
/// somebody unzips it, and "3 of these 40 frames are blank" is not something to
/// find out then.
/// </summary>
public static class DumpEndpoints
{
    public static void MapDump(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api").AddEndpointFilter(DumpErrorFilter);

        // What this node IS, and therefore what it can produce. The menu asks
        // this every time it opens; it decodes nothing.
        api.MapGet("/dump/options", (string path, DumpService dump) =>
            Results.Ok(dump.Describe(path)));

        api.MapGet("/dump/canvas", (string path, string? format, bool? resolve, DumpService dump,
                                    HttpResponse response) =>
        {
            if (string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
            {
                (byte[] data, string name, DumpResultDto result) = dump.ExportCanvasRaw(path);
                Describe(response, result);
                return Results.File(data, "application/zip", name);
            }

            (byte[] png, string fileName, DumpResultDto pngResult) =
                dump.ExportCanvasPng(path, resolve ?? false);
            Describe(response, pngResult);
            return Results.File(png, "image/png", fileName);
        });

        api.MapGet("/dump/animation", (string path, string? format, DumpService dump, HttpResponse response) =>
        {
            (byte[] data, string name, string contentType, DumpResultDto result) =
                dump.ExportAnimation(path, (format ?? "gif").ToLowerInvariant());
            Describe(response, result);
            return Results.File(data, contentType, name);
        });

        api.MapGet("/dump/sound", (string path, DumpService dump, HttpResponse response) =>
        {
            (byte[] data, string name, string contentType, DumpResultDto result) = dump.ExportSound(path);
            Describe(response, result);
            return Results.File(data, contentType, name);
        });

        api.MapGet("/dump/link", (string path, DumpService dump, HttpResponse response) =>
        {
            (byte[] data, string name, DumpResultDto result) = dump.ExportLink(path);
            Describe(response, result);
            return Results.File(data, "application/json", name);
        });

        api.MapGet("/dump/csv", (string path, int? depth, DumpService dump, HttpResponse response) =>
        {
            (byte[] data, string name, DumpResultDto result) = dump.ExportCsv(path, depth ?? 4);
            Describe(response, result);
            return Results.File(data, "text/csv", name);
        });

        // ---- the disk job ----

        // What would be refused, before anything is written. Separate from
        // /dump/start so a dialog can show the refusal next to the folder the
        // user just picked instead of after they press the button.
        api.MapPost("/dump/preflight", (DumpJobRequest request, DumpJobService jobs) =>
            Results.Ok(jobs.Preflight(request)));

        // Deliberately not passing the request's CancellationToken, for the
        // reason the audit does not: a browser that navigates away must not
        // silently abandon a dump the user is watching. Cancelling is
        // /dump/cancel.
        api.MapPost("/dump/start", (DumpJobRequest request, DumpJobService jobs) =>
            Results.Ok(jobs.Start(request)));

        api.MapGet("/dump/progress", (DumpJobService jobs) => Results.Ok(jobs.Snapshot()));

        api.MapPost("/dump/cancel", (DumpJobService jobs) => Results.Ok(jobs.Cancel()));

        // 404 rather than an empty report: "nothing was skipped" and "nothing
        // has run" are different answers, and a UI that renders the first for
        // the second tells the user their dump is complete when none exists.
        api.MapGet("/dump/report", (DumpJobService jobs) =>
        {
            DumpResultDto? report = jobs.Report();
            return report == null
                ? Results.NotFound(new ApiError("No dump has finished in this session."))
                : Results.Ok(report);
        });
    }

    /// <summary>
    /// Copies the outcome into headers so a plain browser download can still
    /// tell the user that some of it did not come out.
    /// </summary>
    private static void Describe(HttpResponse response, DumpResultDto result)
    {
        response.Headers["X-Dump-Issues"] = result.Issues.Count.ToString();
        response.Headers["X-Dump-Truncated"] = result.Truncated ? "true" : "false";
        response.Headers["X-Dump-Canvases"] = result.Canvases.ToString();

        if (result.Issues.Count > 0)
        {
            // One line, ASCII-safe: header values are latin-1 and a WZ node name
            // can hold anything at all.
            string first = result.Issues[0].Reason;
            response.Headers["X-Dump-Note"] = Ascii(
                result.Issues.Count == 1 ? first : $"{first} (+{result.Issues.Count - 1} more)");
        }
    }

    private static string Ascii(string value)
    {
        Span<char> buffer = value.Length <= 400 ? stackalloc char[value.Length] : new char[400];
        int length = Math.Min(value.Length, buffer.Length);
        for (int i = 0; i < length; i++)
            buffer[i] = value[i] is >= ' ' and <= '~' ? value[i] : '?';
        return new string(buffer[..length]);
    }

    /// <summary>
    /// The same mapping <see cref="Endpoints"/> uses, kept here so this group
    /// does not depend on a private member of that file.
    /// </summary>
    private static async ValueTask<object?> DumpErrorFilter(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ApiError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiError(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new ApiError("Access denied.", ex.Message), statusCode: 403);
        }
        catch (Exception ex)
        {
            return Results.Json(new ApiError("Something went wrong.", ex.Message), statusCode: 500);
        }
    }
}
