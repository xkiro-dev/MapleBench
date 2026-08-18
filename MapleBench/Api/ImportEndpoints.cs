using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

public sealed class OpenSplitRequest
{
    /// <summary>The client folder or its Data directory. Either is accepted.</summary>
    public string SourceFolder { get; set; } = "";
    /// <summary>Archive name as reported by detection — "Mob".</summary>
    public string Archive { get; set; } = "";
}

public sealed class OpenClientRequest
{
    /// <summary>The client folder or its Data directory. Either is accepted.</summary>
    public string SourceFolder { get; set; } = "";
}

/// <summary>
/// The cross-client importer's HTTP surface.
///
/// Kept out of <see cref="Endpoints"/> for the same reason
/// <see cref="SkillEndpoints"/> is: one mode, one file. Wired in with
/// <c>MapImport(api)</c> alongside the other <c>Map*</c> calls.
///
/// Two ways to get at a modern client, and the order here is the order of
/// preference. <c>/import/open</c> recognizes the source layout, opens a classic
/// .wz normally or merges one split archive in memory, and puts either in the
/// session read-only. <c>/import/archive</c>
/// converts a whole archive to a classic .wz on disk, which is what somebody
/// who genuinely wants the file should use, and is measured in gigabytes and
/// minutes. Everything else here — detect, progress, cancel — serves both.
/// </summary>
public static class ImportEndpoints
{
    public static void MapImport(this RouteGroupBuilder api)
    {
        // Answers "what did I just point at" for any folder, including one that is
        // neither kind. The picker's /api/scan cannot do this: it only lists .wz
        // files sitting directly in a folder, and a split client has none — its
        // Data\String\ holds String_000.wz, which /api/scan would show as an
        // archive family called "String_000" and open as a fragment.
        api.MapGet("/import/detect", (string path, ClientImportService import) =>
            Results.Ok(import.Detect(path)));

        // Open, not convert. The response is an ordinary OpenFileDto, so the UI
        // adds it to the file list and the tree with no special case. Detection
        // decides whether this is one classic .wz or one split Data archive; the
        // port workflow after that is identical.
        api.MapPost("/import/open", async (OpenSplitRequest request, ClientImportService import,
                                           WzSessionService session, ImportProgressService progress,
                                           StringPoolService strings, IconService icons) =>
        {
            // Through the same one-at-a-time gate as a conversion. It is essential
            // for a split archive and harmless for the much cheaper classic open.
            OpenFile file = await Task.Run(() =>
                progress.RunOpen(request.Archive, cancel =>
                {
                    ClientLayoutDto layout = import.Detect(request.SourceFolder);
                    SplitArchiveDto? archive = layout.Archives.FirstOrDefault(a =>
                        a.Name.Equals(request.Archive, StringComparison.OrdinalIgnoreCase));
                    if (archive == null)
                    {
                        throw new InvalidOperationException(
                            $"'{request.Archive}' is not an archive in {layout.Path}. Detect the folder " +
                            "again and choose one of the archives it reports.");
                    }

                    if (layout.Kind == "split")
                    {
                        return session.OpenSplitArchive(layout, request.Archive, import,
                                                        progress.Report, cancel);
                    }

                    if (layout.Kind == "classic")
                    {
                        cancel.ThrowIfCancellationRequested();
                        return session.Open(new OpenRequest
                        {
                            Path = archive.Path,
                            ReadOnly = true,
                        });
                    }

                    throw new InvalidOperationException(
                        $"{layout.Summary} Choose a folder containing ordinary .wz files or a split Data client.");
                }));

            // Both pools index by archive name and both merge across open files, so
            // a newly opened client has to invalidate them or its strings and icons
            // are invisible until something else does.
            strings.Invalidate();
            icons.Invalidate();
            return Results.Ok(file.ToDto());
        });

        // Dual View is a client-to-client workspace, not an archive picker. Detect
        // the source folder once, then mount every archive under it read-only so
        // the left pane is a complete Explorer. Keeping the loop on the server is
        // important for split clients: detection walks their INIs and pack index,
        // and repeating that scan once per archive made "open the folder" pay the
        // same discovery cost dozens of times.
        api.MapPost("/import/open-client", async (OpenClientRequest request,
                                                   ClientImportService import,
                                                   WzSessionService session,
                                                   ImportProgressService progress,
                                                   StringPoolService strings,
                                                   IconService icons) =>
        {
            object result;
            try
            {
                result = await Task.Run(() => progress.RunOpen("whole client", cancel =>
                {
                    ClientLayoutDto layout = import.Detect(request.SourceFolder);
                    if (layout.Kind is not ("split" or "classic"))
                    {
                        throw new InvalidOperationException(
                            $"{layout.Summary} Choose a folder containing ordinary .wz files or a split Data client.");
                    }

                    // A source and target cannot be two names for the same client.
                    // More importantly, session.Open returns an existing file for
                    // the same path; without this guard a writable target archive
                    // could be handed back as though Dual View had opened it
                    // read-only on the source side.
                    string sourceKey = layout.Kind == "split"
                        ? layout.DataPath ?? layout.Path
                        : layout.Path;
                    bool isWritableTarget = session.Files.Any(file =>
                        !file.ReadOnly && string.Equals(
                            Path.GetDirectoryName(file.FilePath)?.TrimEnd(
                                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            sourceKey.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase));
                    if (isWritableTarget)
                    {
                        throw new InvalidOperationException(
                            "That folder is already open as Your client. Choose the other client folder for the left pane.");
                    }

                    List<OpenFileDto> opened = new();
                    List<object> failed = new();
                    int total = layout.Archives.Count;

                    for (int index = 0; index < total; index++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        SplitArchiveDto archive = layout.Archives[index];
                        progress.Report($"Opening {archive.Name}", index, total);

                        if (!archive.Supported)
                        {
                            failed.Add(new
                            {
                                name = archive.Name,
                                message = archive.Reason ?? "This archive is not supported.",
                            });
                            continue;
                        }

                        try
                        {
                            OpenFile file = layout.Kind == "split"
                                ? session.OpenSplitArchive(
                                    layout, archive.Name, import,
                                    (stage, _, _) => progress.Report($"{archive.Name}: {stage}", index, total),
                                    cancel)
                                : session.Open(new OpenRequest
                                {
                                    Path = archive.Path,
                                    ReadOnly = true,
                                });

                            // This should only be reachable for a pre-existing
                            // session entry. Never silently weaken the read-only
                            // source guarantee even if the folder changes while
                            // the operation is running.
                            if (!file.ReadOnly)
                            {
                                failed.Add(new
                                {
                                    name = archive.Name,
                                    message = "This archive is already open for editing, so it was not used as a source.",
                                });
                                continue;
                            }

                            opened.Add(file.ToDto());
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { name = archive.Name, message = ex.Message });
                        }
                    }

                    progress.Report("Finishing the source client", total, total);
                    return new
                    {
                        folder = sourceKey,
                        kind = layout.Kind,
                        total,
                        opened,
                        failed,
                    };
                },
                successMessage: "Opened the complete source client for reference.",
                cancelMessage: "Stopped opening the source client. Archives opened before the stop remain available read-only."));
            }
            finally
            {
                // A cancellation can arrive after several archives opened. The
                // partial source is still a valid read-only session shape, so
                // caches must see it even when the request itself did not finish.
                strings.Invalidate();
                icons.Invalidate();
            }

            return Results.Ok(result);
        });

        // Synchronous: the result is only meaningful once verification has passed,
        // and a caller that gets "started" back has nothing it can trust yet. The
        // recovery story for a browser that navigates away mid-import is
        // /import/progress, which now keeps the finished result rather than only
        // the fact that it finished.
        api.MapPost("/import/archive", async (ImportRequest request, ClientImportService import,
                                              ImportProgressService progress, WzSessionService session) =>
        {
            // Off the request thread. The conversion is minutes of synchronous file
            // IO for a large archive, and holding a Kestrel thread for it starves
            // every other request -- including the progress poll that exists to
            // report on this very conversion.
            //
            // Deliberately NOT passing the request's CancellationToken: a client
            // that gives up on the response should not silently abandon a 13 GB
            // write half-way. Cancelling is an explicit act — see /import/cancel.
            ImportResult result = await Task.Run(() => progress.Run(request, import, session));
            return Results.Ok(result);
        });

        // What the current or most recent import is doing. Deliberately a poll
        // rather than a stream: the UI already polls nothing else, and a
        // long-lived response through the same Kestrel that is busy writing 13 GB
        // is the one connection that must not be able to hang.
        api.MapGet("/import/progress", (ImportProgressService progress) =>
            Results.Ok(progress.Snapshot()));

        // Asks the running import to stop at the next source file. Returns what it
        // was doing rather than 204, so a UI that races a finish still learns the
        // terminal state from this call alone.
        api.MapPost("/import/cancel", (ImportProgressService progress) =>
        {
            progress.Cancel();
            return Results.Ok(progress.Snapshot());
        });
    }
}
