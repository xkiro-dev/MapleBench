using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

public static class Endpoints
{
    /// <summary>Ceiling on an uploaded canvas file. The largest sprite in a
    /// stock client is a few hundred KB; this is generous by two orders.</summary>
    private const long MaxUploadBytes = 32 * 1024 * 1024;

    /// <summary>Ceiling on either side of an uploaded canvas, in pixels.</summary>
    private const int MaxCanvasSide = 8192;

    public static void MapEndpoints(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api").AddEndpointFilter(ErrorFilter);

        api.MapGet("/about", () => Results.Ok(new
        {
            version = typeof(MapleBench.Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            license = "GPL-3.0",
        }));

        MapSession(api);
        MapTree(api);
        MapEdit(api);
        MapMedia(api);
        MapSearch(api);
        MapCashShop(api);
        MapMobs(api);
        MapReadOnly(api);
        MapDatabase(api);
        MapAssets(api);
        api.MapDomains();
        api.MapSkills();
        api.MapTransfers();
        api.MapImport();
        api.MapAudit();
        api.MapRepair();
        api.MapDonorRestore();
        api.MapCanvasDirLink();
        api.MapComposition();
        api.MapFamilies();
        api.MapFacts();
        api.MapMapEditor();
    }

    #region Map assets

    private static void MapAssets(RouteGroupBuilder api)
    {
        // Reports the per-kind counts and the archives behind them, not just an
        // "available" bit: a library unioned across Map.wz, Map001.wz and Map2.wz
        // is short in exactly the way a small client is short, so the count and
        // its sources are the only way a caller can tell the two apart.
        api.MapGet("/mapasset/capabilities", (MapAssetService assets) =>
            Results.Ok(assets.Capabilities()));

        api.MapGet("/mapasset/sets", (string kind, MapAssetService assets) =>
            Results.Ok(assets.Sets(kind)));

        // Limit is generous because a tile set is small and the browser wants the
        // whole set at once to lay out a grid; an object set with thousands of
        // frames is the case the cap exists for, and it reports when it bites.
        api.MapGet("/mapasset/entries", (string path, int? limit, MapAssetService assets) =>
            Results.Ok(assets.Entries(path, Math.Clamp(limit ?? 2000, 1, 5000))));
    }

    #endregion

    #region Database

    /// <summary>
    /// Search across everything the client names, with icons — the fastest way to
    /// answer "what is 1302000" or "where is the Blue Sword", which otherwise
    /// means knowing which archive to open and guessing a path.
    /// </summary>
    private static void MapDatabase(RouteGroupBuilder api)
    {
        api.MapGet("/db/search", (string q, string? kind, int? limit, StringPoolService strings) =>
        {
            List<(string Kind, int Id, string Name)> hits =
                strings.Search(q, kind, Math.Clamp(limit ?? 200, 1, 500));

            var results = hits.Select(hit => new
            {
                kind = hit.Kind,
                id = hit.Id,
                name = hit.Name,
                // Only items have a reliable inventory icon. Asking for one for a
                // mob would be a per-row sprite hunt, so the client falls back to a
                // kind glyph rather than to a broken image.
                icon = hit.Kind == "item" ? $"/api/cashshop/icon/{hit.Id}" : null,
            }).ToList();

            return Results.Ok(new
            {
                results,
                truncated = hits.Count >= Math.Clamp(limit ?? 200, 1, 500),
                available = strings.IsAvailable,
            });
        });

        api.MapGet("/db/capabilities", (StringPoolService strings) =>
            Results.Ok(new { available = strings.HasSource }));
    }

    #endregion

    #region Reference-only archives

    /// <summary>
    /// Marks an open archive reference-only, or releases it.
    ///
    /// Deliberately not part of the save path: this is about the *session*, not
    /// the file on disk, and it exists so that having a second client open to
    /// compare against cannot quietly become editing it.
    /// </summary>
    private static void MapReadOnly(RouteGroupBuilder api)
    {
        api.MapPost("/files/{id}/readonly", (string id, ReadOnlyRequest request, WzSessionService session) =>
        {
            lock (session.Gate)
            {
                OpenFile file = session.GetFile(id);

                if (file.Kind == "img-folder" && !request.ReadOnly)
                {
                    throw new InvalidOperationException(
                        "An IMG folder is mounted for reference only. MapleBench will not unlock a collection " +
                        "until it can verify every changed file as one all-or-nothing save.");
                }

                // Refused rather than silently dropping the edits: locking a file
                // that already has unsaved work would leave changes that cannot be
                // undone through the UI and cannot be saved either.
                if (request.ReadOnly && (file.Dirty || file.CountDirtyImages() > 0))
                {
                    throw new InvalidOperationException(
                        $"'{file.Name}' has unsaved changes, so it cannot be made reference-only. " +
                        "Save or close it first.");
                }

                file.ReadOnly = request.ReadOnly;
                return Results.Ok(file.ToDto());
            }
        });
    }

    #endregion

    #region Mobs

    private static void MapMobs(RouteGroupBuilder api)
    {
        api.MapGet("/mob/capabilities", (MobService mobs, StringPoolService strings) =>
            Results.Ok(new { available = mobs.IsAvailable, names = strings.HasSource }));

        // Paged when asked, whole when not.
        //
        // Omitting both parameters returns exactly the shape it always did, so
        // the existing client is unaffected. Supplying either adds `total`,
        // `offset` and `limit` alongside the same fields, which is what lets a
        // grid paint its first screen without waiting for 1.2 MB of JSON: the
        // rows are built and cached whole either way (the cost is parsing the
        // images, and stats and ordering need all of them), so what paging buys
        // is the serialising and the transfer, which is nearly all of a warm
        // request. /api/npc/list and /api/skill/list have the same support in
        // NpcService.Page and SkillService.Page, awaiting the same two lines in
        // DomainEndpoints.cs and SkillEndpoints.cs.
        api.MapGet("/mob/list", (string? fileId, bool? names, int? offset, int? limit,
            MobService mobs, CancellationToken cancel) =>
        {
            if (offset is null && limit is null)
                return Results.Ok(mobs.List(fileId, names ?? true, cancel));

            (MobListDto page, int total) = mobs.Page(
                fileId, names ?? true, offset ?? 0, limit ?? 200, cancel);
            return Results.Ok(new
            {
                mobs = page.Mobs,
                stats = page.Stats,
                truncated = page.Truncated,
                total,
                offset = offset ?? 0,
                limit = limit ?? 200,
            });
        });

        api.MapGet("/mob/detail", (string path, MobService mobs) =>
            Results.Ok(mobs.Detail(path)));

        api.MapPost("/mob/fields", (MobWriteRequest request, MobService mobs) =>
            Results.Ok(mobs.WriteFields(request)));

        api.MapPost("/mob/bulk", (MobBulkRequest request, MobService mobs) =>
            Results.Ok(mobs.Bulk(request)));
    }

    #endregion

    #region Cash shop

    private static void MapCashShop(RouteGroupBuilder api)
    {
        RouteGroupBuilder shop = api.MapGroup("/cashshop");

        shop.MapGet("/items", (string fileId, bool? names, CashShopService cash) =>
        {
            List<CommodityItemDto> items = cash.List(fileId, names ?? true);
            return Results.Ok(new
            {
                items,
                stats = new
                {
                    total = items.Count,
                    onSale = items.Count(i => i.OnSale),
                    averagePrice = items.Count == 0 ? 0 : (int)items.Average(i => i.Price),
                },
                categories = items.GroupBy(i => i.Category)
                    .Select(g => new
                    {
                        name = g.Key,
                        count = g.Count(),
                        subCategories = g.Where(i => i.SubCategory.Length > 0)
                            .GroupBy(i => i.SubCategory)
                            .Select(s => new { name = s.Key, count = s.Count() })
                            .OrderBy(s => s.name),
                    })
                    .OrderBy(c => c.name),
                // Derived from the list above rather than from
                // CashShopService.FindDuplicateSerials, which would re-read and
                // re-project every entry in Commodity.img — under the global
                // session lock — to answer a question this list already answers.
                duplicateSerials = items.GroupBy(i => i.Sn)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .OrderBy(sn => sn)
                    .ToList(),
            });
        });

        shop.MapGet("/next-sn", (string fileId, CashShopService cash) =>
            Results.Ok(new { sn = cash.NextSerial(fileId) }));

        shop.MapPost("/item", (CommodityWriteRequest request, CashShopService cash) =>
            Results.Ok(cash.Upsert(request)));

        shop.MapPost("/items", (CommodityBulkRequest request, CashShopService cash) =>
            Results.Ok(cash.BulkUpsert(request)));

        shop.MapPost("/clone", (CloneRequest request, CashShopService cash) =>
            Results.Ok(cash.Clone(request.FileId, request.Key, request.ItemId)));

        shop.MapPost("/delete", (DeleteItemsRequest request, CashShopService cash) =>
            Results.Ok(new { removed = cash.Delete(request.FileId, request.Keys) }));

        shop.MapGet("/icon/{itemId:int}", (int itemId, IconService icons) =>
        {
            byte[]? png = icons.GetIconPng(itemId);
            return png == null
                ? Results.NotFound(new ApiError("No icon found for this item."))
                : Results.File(png, "image/png");
        });

        shop.MapGet("/capabilities", (IconService icons, StringPoolService strings,
            CashShopService cash) =>
            Results.Ok(new
            {
                icons = icons.HasIconSource,
                names = strings.HasSource,
                source = cash.Source()?.ToDto(),
            }));
    }

    #endregion

    #region Session

    private static void MapSession(RouteGroupBuilder api)
    {
        api.MapGet("/files", (WzSessionService session) => Results.Ok(session.ListFiles()));

        api.MapPost("/files/open", (OpenRequest request, WzSessionService session,
            StringPoolService strings, IconService icons, WzSaveService save, WarmupService warmup) =>
        {
            warmup.Cancel();
            OpenFile file = session.Open(request);
            // A newly opened String.wz or Item.wz changes what these can resolve,
            // so the caches must not outlive the session shape.
            strings.Invalidate();
            icons.Invalidate();
            // Opening is the first moment the server knows which client folder
            // the user works in, so it is the earliest point a leftover temp
            // file from a crashed save can be found. Swept folders are
            // remembered, so this is a no-op after the first file in a folder.
            save.SweepOrphanedTempFiles();
            return Results.Ok(file.ToDto());
        });

        api.MapPost("/files/open-img-folder", (OpenRequest request, WzSessionService session,
            StringPoolService strings, IconService icons, WarmupService warmup) =>
        {
            warmup.Cancel();
            OpenFile file = session.OpenImgFolder(request);
            strings.Invalidate();
            icons.Invalidate();
            return Results.Ok(file.ToDto());
        });

        api.MapDelete("/files/{fileId}", (string fileId, WzSessionService session,
            StringPoolService strings, IconService icons, UndoService undo, WarmupService warmup) =>
        {
            // Before the close, not after: a warm-up part way through a list is
            // holding MapleLib objects out of the archive about to be disposed.
            //
            // And waited for, unlike everywhere else Cancel is called. A cancel
            // that returns immediately is the right answer for an open or a save,
            // which only need the warm-up to stop competing for the gate — but a
            // close destroys what the warm-up is reading, and the warm-up only
            // looks at its token between chunks. Two seconds is far more than a
            // chunk takes and far less than a user would notice; if it is not
            // enough the close goes ahead regardless and the generation checks
            // inside the chunked walks catch it.
            warmup.CancelAndWait(TimeSpan.FromSeconds(2));
            // Before Close disposes the archive: every undo entry for this file
            // captures MapleLib objects that are about to become invalid, and
            // running one afterwards would reach into a disposed tree. Only this
            // file's entries go -- the other open archives' history is untouched.
            undo.ClearForFile(fileId);
            session.Close(fileId);
            strings.Invalidate();
            icons.Invalidate();
            return Results.NoContent();
        });

        // Opening a MapleStory folder means opening 20-40 archives; doing that
        // one dialog at a time is the single most tedious thing about existing
        // WZ tools.
        api.MapPost("/files/open-many", (OpenManyRequest request, WzSessionService session,
            StringPoolService strings, IconService icons, WzSaveService save, WarmupService warmup,
            ILoggerFactory loggers) =>
        {
            ILogger log = loggers.CreateLogger("OpenMany");

            // Anything already warming describes a session that is about to
            // change shape. Stopped before the first archive rather than after
            // the last, so it is not competing for the gate with the opens.
            warmup.Cancel();
            List<object> opened = new();
            List<object> failed = new();
            List<string> warnings = new();

            foreach (string path in request.Paths.Distinct())
            {
                try
                {
                    OpenFile file = session.Open(new OpenRequest
                    {
                        Path = path,
                        MapleVersion = request.MapleVersion,
                        Iv = request.Iv,
                        GameVersion = request.GameVersion,
                    });
                    opened.Add(file.ToDto());

                    // Surfaced at open, not at save: a client whose text cannot be
                    // represented is destroyed by *any* save, including one with no
                    // edits, so the warning has to arrive before the work does.
                    string? mangled = session.DescribeMangledText(file);
                    if (mangled != null)
                    {
                        warnings.Add(mangled);
                        log.LogWarning("{Warning}", mangled);
                    }
                }
                catch (Exception ex)
                {
                    // One bad archive must not abort the whole folder.
                    log.LogWarning(ex, "Could not open {Path}", path);
                    failed.Add(new { path, name = Path.GetFileName(path), message = ex.Message });
                }
            }

            strings.Invalidate();
            icons.Invalidate();
            save.SweepOrphanedTempFiles();

            return Results.Ok(new { opened, failed, warnings });
        });

        // Everything openable in a folder, with the grouping the UI needs to
        // present "Map.wz + Map001.wz + Map002.wz" as one thing.
        api.MapGet("/scan", (string path) => Results.Ok(ScanFolder(path)));

        api.MapPost("/files/save", (SaveRequest request, WzSaveService save, WarmupService warmup) =>
        {
            // A save holds the gate for the whole write of a large archive and
            // releases and reopens the archive at the end. A warm-up running
            // alongside it would compete for the gate with the one operation in
            // the app that must not be slowed down, and would then be holding
            // objects from a tree that has been replaced.
            warmup.Cancel();
            return Results.Ok(save.Save(request));
        });

        // What the process is holding, and a way to give it back.
        //
        // Browsing three sections of a v232 client took the process from 60 MB
        // to 2,254 MB and nothing ever gave any of it back, because nothing in
        // the app called WzImage.UnparseImage outside one branch of the save
        // preflight. The sweep is safe by construction and emphatic about it --
        // see ImageMemoryService -- and it runs automatically at the end of a
        // warm-up; this is the manual handle, and the numbers the UI can show.
        api.MapGet("/session/memory", (WzSessionService session, WzRenderService render) =>
        {
            (int entries, long bytes) = render.CacheStats();
            int parsed = 0;
            int dirty = 0;
            lock (session.Gate)
            {
                foreach (OpenFile file in session.Files)
                {
                    foreach (MapleLib.WzLib.WzImage image in file.EnumerateArchiveImages())
                    {
                        if (image.Parsed) parsed++;
                        if (image.Changed) dirty++;
                    }
                }
            }
            return Results.Ok(new
            {
                workingSetMB = ImageMemoryService.WorkingSetBytes / (1024 * 1024),
                managedMB = GC.GetTotalMemory(false) / (1024 * 1024),
                parsedImages = parsed,
                dirtyImages = dirty,
                thumbCacheEntries = entries,
                thumbCacheMB = bytes / (1024 * 1024),
            });
        });

        api.MapPost("/session/sweep", (ImageMemoryService memory, CancellationToken cancel) =>
            Results.Ok(memory.Sweep(cancel)));

        // The same check the background poller runs, on demand. It exists so the
        // guard can be watched working — "does anything actually give memory
        // back before the process dies" is not a question a five-second timer
        // and a log line answer convincingly on their own.
        api.MapPost("/session/memory-check", (MemoryPressureService pressure, CancellationToken cancel) =>
            Results.Ok(pressure.Check(cancel)));

        // Lets the UI warn before the user commits, rather than at write time.
        api.MapGet("/files/{fileId}/preflight", (string fileId, WzSessionService session, WzSaveService save) =>
        {
            // Under the gate, like every other tree-touching request.
            //
            // Preflight calls ParseImage, which mutates WzImage.properties and
            // moves the archive's shared WzBinaryReader position. Running that
            // outside the lock -- concurrently with a browse, a render or a save
            // -- is a data race on the reader, and it was the one place in the app
            // that broke the single-gate rule the whole concurrency story rests
            // on. CountDirtyImages walks the same tree, so it belongs inside too.
            lock (session.Gate)
            {
                OpenFile file = session.GetFile(fileId);

                List<string> problems = save.Preflight(file);
                return Results.Ok(new { ok = problems.Count == 0, problems, dirtyImages = file.CountDirtyImages() });
            }
        });

        // Directory listing for the in-app file picker: the browser cannot show
        // a native dialog that returns a usable server-side path.
        api.MapGet("/browse", (string? path) => Results.Ok(Browse(path)));

        // Everything with unsaved changes, so the user can review before saving
        // instead of trusting a counter. See ChangeSummary for why the counter
        // it used to trust could not see half the work.
        api.MapGet("/changes", (WzSessionService session, UndoService undo) =>
            Results.Ok(new { files = ChangeSummary.ForSession(session, undo) }));
    }

    private static object Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new
            {
                path = (string?)null,
                parent = (string?)null,
                directories = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new { name = d.Name, path = d.RootDirectory.FullName })
                    .ToList(),
                files = Array.Empty<object>(),
            };
        }

        string full = FolderPath.Resolve(path);

        List<object> directories = new();
        foreach (string dir in SafeEnumerate(() => Directory.EnumerateDirectories(full)))
            directories.Add(new { name = Path.GetFileName(dir), path = dir });

        List<object> files = new();
        foreach (string file in SafeEnumerate(() => Directory.EnumerateFiles(full)))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".wz" or ".ms" or ".img"))
                continue;
            long size = 0;
            // Matches ScanFolder: a file that vanished between the listing and
            // this line, or whose metadata we cannot read, is still worth
            // showing — failing the whole listing over its size is not.
            try { size = new FileInfo(file).Length; } catch { /* unreadable; still list it */ }
            files.Add(new
            {
                name = Path.GetFileName(file),
                path = file,
                size,
            });
        }

        return new
        {
            path = full,
            parent = Path.GetDirectoryName(full),
            directories,
            files,
        };
    }

    /// <summary>Turns a session path into a download-friendly file name.</summary>
    private static string SafeFileName(string path)
    {
        // Split already unescapes; a second pass turned a name containing a
        // literal "%2F" into one containing a slash.
        string last = WzPath.Split(path).LastOrDefault() ?? "export";
        foreach (char c in Path.GetInvalidFileNameChars())
            last = last.Replace(c, '_');
        return last.Length == 0 ? "export" : last;
    }

    /// <summary>
    /// Lists every file the session can open. Archive parts are grouped by family
    /// so numbered siblings (Map.wz, Map001.wz, Map002.wz) read as one entry;
    /// standalone .img files remain individual rows.
    /// </summary>
    private static object ScanFolder(string path)
    {
        string full = FolderPath.Resolve(path);

        List<(string Path, string Name, long Size, bool LooseImage)> openable = new();
        foreach (string file in SafeEnumerate(() => Directory.EnumerateFiles(full)))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".wz" or ".ms" or ".img"))
                continue;
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* unreadable; still list it */ }
            openable.Add((file, Path.GetFileName(file), size, extension == ".img"));
        }

        // "Map001.wz" and "Map.wz" belong to the same family. A loose image is
        // not an archive part, even when its stem resembles an archive name.
        var groups = openable
            .GroupBy(a => a.LooseImage
                         ? "img:" + a.Name
                         : WzSessionService.StripArchiveSuffix(Path.GetFileNameWithoutExtension(a.Name)),
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                family = g.Key,
                kind = g.All(a => a.LooseImage) ? "img" : "archive",
                totalSize = g.Sum(a => a.Size),
                files = g.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(a => new { a.Path, a.Name, a.Size })
                         .ToList(),
            })
            .OrderBy(g => g.files[0].Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var archives = openable.Where(a => !a.LooseImage).ToList();
        bool looksLikeClient = archives.Any(a => a.Name.StartsWith("Base", StringComparison.OrdinalIgnoreCase))
                            || archives.Count >= 5;

        return new
        {
            path = full,
            parent = Path.GetDirectoryName(full),
            looksLikeClient,
            archiveCount = openable.Count,
            looseImageCount = openable.Count(a => a.LooseImage),
            totalSize = openable.Sum(a => a.Size),
            groups,
            subdirectories = SafeEnumerate(() => Directory.EnumerateDirectories(full))
                .Select(d => new { name = Path.GetFileName(d), path = d })
                .ToList(),
        };
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> source)
    {
        try { return source().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    #endregion

    #region Tree

    private static void MapTree(RouteGroupBuilder api)
    {
        // One `path` parameter for both kinds of tree, and the branch is one
        // character wide: a merged family path starts with its family id ("g1")
        // and a physical one starts with a session file id ("f1"). Everything
        // below a merged folder that is not itself a folder is handed back with
        // its real path, so this branch is only ever taken at the top of a walk
        // -- which is exactly why no other endpoint in the app needs one.
        api.MapGet("/node", (string path, WzSessionService session, ArchiveFamilyService families) =>
            Results.Ok(families.IsFamilyPath(path)
                ? families.GetNode(path)
                : WithSource(session.GetNode(path), families.SourceLabelFor(path))));

        api.MapGet("/children", (string path, WzSessionService session, ArchiveFamilyService families,
                                 StringPoolService strings) =>
            Results.Ok(WithNames(
                families.IsFamilyPath(path)
                    ? families.GetChildren(path)
                    : WithSource(session.GetChildren(path), families.SourceLabelFor(path)),
                strings)));

        // Everything needed to render a node's detail pane in one round trip.
        api.MapGet("/inspect", (string path, WzSessionService session, ArchiveFamilyService families,
                                StringPoolService strings) =>
        {
            bool merged = families.IsFamilyPath(path);
            string? source = merged ? null : families.SourceLabelFor(path);
            NodeDto node = merged ? families.GetNode(path) : WithSource(session.GetNode(path), source);
            List<NodeDto> children = node.HasChildren
                ? (merged ? families.GetChildren(path) : WithSource(session.GetChildren(path), source))
                : new List<NodeDto>();
            WithNames(children, strings);
            node.DisplayName = strings.NameFor(node.Path, node.Name);
            // No breadcrumb here: the client builds its own from the path it
            // already has (app.js renderBreadcrumb), and it needs the open-file
            // list to label the first segment, which the server cannot supply.
            return Results.Ok(new { node, children });
        });
    }

    /// <summary>
    /// Stamps the physical archive on rows below a merged family.
    ///
    /// Null everywhere else and free when nothing is merged, so this costs an
    /// ordinary session one null check per listing. See
    /// <see cref="ArchiveFamilyService.SourceLabelFor"/> for why the rows below
    /// an image need it at all.
    /// </summary>
    private static List<NodeDto> WithSource(List<NodeDto> nodes, string? source)
    {
        if (source == null)
            return nodes;
        foreach (NodeDto node in nodes)
            node.Source ??= source;
        return nodes;
    }

    private static NodeDto WithSource(NodeDto node, string? source)
    {
        if (source != null)
            node.Source ??= source;
        return node;
    }

    /// <summary>
    /// Labels id-named nodes with what they mean — "01302000" -> "Blue Sword".
    ///
    /// Done here rather than in <c>WzSessionService.ToDto</c> because the string
    /// pool is built *from* the session, so having the session ask it back would
    /// be a dependency cycle. The endpoint layer is where both are already in
    /// hand. Resolution is a dictionary hit against an already-built pool, so the
    /// cost is per-node negligible; when no String.wz is open every lookup is a
    /// null and the field is omitted from the JSON entirely.
    /// </summary>
    private static List<NodeDto> WithNames(List<NodeDto> nodes, StringPoolService strings)
    {
        if (!strings.IsAvailable)
            return nodes;

        foreach (NodeDto node in nodes)
            node.DisplayName = strings.NameFor(node.Path, node.Name);
        return nodes;
    }

    #endregion

    #region Edit

    private static void MapEdit(RouteGroupBuilder api)
    {
        api.MapPut("/node/value", (SetValueRequest request, WzEditService edit) =>
            Results.Ok(edit.SetValue(request.Path, request.Value)));

        api.MapPut("/node/values", (SetValuesRequest request, WzEditService edit) =>
        {
            (List<NodeDto> updated, List<string> failed) = edit.SetValueMany(request.Paths, request.Value);
            return Results.Ok(new { updated, failed });
        });

        // Preview and write are the same route with DryRun flipped, so the
        // before -> after table the user approves is produced by the code that
        // does the writing. Two implementations would be two chances to show one
        // number and store another.
        api.MapPost("/node/compute", (ComputeValuesRequest request, WzEditService edit) =>
            Results.Ok(edit.ComputeValues(request)));

        api.MapPut("/node/name", (RenameRequest request, WzEditService edit) =>
            Results.Ok(edit.Rename(request.Path, request.Name)));

        api.MapPost("/node", (AddNodeRequest request, WzEditService edit) => Results.Ok(edit.Add(request)));

        api.MapPost("/node/delete", (PathsRequest request, WzEditService edit) =>
            Results.Ok(new { removed = edit.Delete(request.Paths) }));

        api.MapPost("/node/duplicate", (PathsRequest request, WzEditService edit) =>
            Results.Ok(edit.Duplicate(request.Paths)));

        api.MapPost("/node/transfer", (TransferRequest request, WzEditService edit) =>
            Results.Ok(edit.Transfer(request)));

        api.MapPost("/node/reorder", (ReorderRequest request, WzEditService edit) =>
        {
            edit.Reorder(request.Path, request.Index);
            return Results.NoContent();
        });

        api.MapGet("/node/types", () => Results.Ok(WzNodeFactory.CreatableProperties));

        api.MapPost("/undo", (WzEditService edit, UndoService undo) =>
        {
            EditAction? action = edit.Undo();
            (string? nextUndo, string? nextRedo, int undoDepth, int redoDepth) = undo.Peek();
            return Results.Ok(new
            {
                applied = action?.Label,
                affected = action?.AffectedPaths ?? Array.Empty<string>(),
                nextUndo,
                nextRedo,
                undoDepth,
                redoDepth,
            });
        });

        api.MapPost("/redo", (WzEditService edit, UndoService undo) =>
        {
            EditAction? action = edit.Redo();
            (string? nextUndo, string? nextRedo, int undoDepth, int redoDepth) = undo.Peek();
            return Results.Ok(new
            {
                applied = action?.Label,
                affected = action?.AffectedPaths ?? Array.Empty<string>(),
                nextUndo,
                nextRedo,
                undoDepth,
                redoDepth,
            });
        });

        api.MapGet("/history", (UndoService undo) =>
        {
            (string? nextUndo, string? nextRedo, int undoDepth, int redoDepth) = undo.Peek();
            return Results.Ok(new { nextUndo, nextRedo, undoDepth, redoDepth });
        });
    }

    #endregion

    #region Media

    private static void MapMedia(RouteGroupBuilder api)
    {
        api.MapGet("/canvas", (string path, WzRenderService render, HttpRequest request, HttpResponse response) =>
        {
            // Cached twice over, and both halves were missing.
            //
            // Server side: the decoded bitmap and the GDI+ PNG encode happen
            // under the session gate, and every Canvas row asks for the same
            // bytes through /api/thumb as well, so the same sprite was decoded
            // and encoded twice per row. RenderCanvasPngCached keys the encoded
            // bytes on the tree generation, which a canvas replace moves.
            //
            // Client side: this endpoint set no cache header at all. media.js's
            // header comment says "/api/thumb and /api/canvas are served with a
            // long max-age" and builds every URL with a "&v=" revision stamp it
            // bumps on write precisely so that a long max-age is safe -- the
            // stamping was there, the max-age was not. This makes the comment
            // true. An unstamped request (a direct link, a download) gets an
            // hour rather than a year, because nothing guarantees it will be
            // re-minted after an edit.
            byte[]? png = render.RenderCanvasPngCached(path);
            SetMediaCacheHeaders(request, response);
            return png == null
                ? Results.NotFound(new ApiError("This node has no image."))
                : Results.File(png, "image/png", SafeFileName(path) + ".png");
        });

        api.MapGet("/audio", (string path, WzRenderService render) =>
        {
            var audio = render.GetAudio(path);
            return audio == null
                ? Results.NotFound(new ApiError("This node has no audio."))
                : Results.File(audio.Value.Data, audio.Value.ContentType,
                    SafeFileName(path) + (audio.Value.ContentType.Contains("mp3") ? ".mp3" : ".bin"));
        });

        api.MapGet("/animation", (string path, AnimationService animation) =>
            Results.Ok(animation.Describe(path)));

        // Thumbnails for every canvas under a node, so selecting a branch shows
        // its sprites without expanding down to each leaf.
        api.MapGet("/preview", (string path, int? limit, AnimationService animation) =>
        {
            List<AnimationFrameDto> canvases = animation.CollectCanvases(path, Math.Clamp(limit ?? 60, 1, 200));
            return Results.Ok(new { canvases, count = canvases.Count });
        });

        // One representative image for any node, so a list can show what is
        // inside a branch without the user opening it. Kept deliberately
        // shallow: this runs once per visible row.
        api.MapGet("/thumb", (string path, AnimationService animation, WzRenderService render,
            HttpRequest request, HttpResponse response) =>
        {
            // Memoised whole, including the miss.
            //
            // The browser cache header below stops one *browser* re-asking, but
            // it does nothing for a second tab, a reload with the cache
            // disabled, or the first paint after a rebuild -- and the work being
            // repeated is a depth-5 search that parses a whole image under the
            // session gate. Measured per thumbnail on a v232 client: 7.3 ms for
            // a mob, 4.6 ms for an NPC and 24.4 ms for a skill (a Skill.wz image
            // is a whole job's book, parsed end to end to extract one 0.4 KB
            // icon), which across every row of the three section grids is ~84 s
            // of gate time. Served from this cache it is a dictionary probe.
            //
            // It is also what makes ImageMemoryService's sweep affordable: the
            // icons live on as encoded PNGs after the parsed images behind them
            // have been released.
            byte[]? png = render.ThumbCached(path, () =>
            {
                byte[]? direct = null;
                try { direct = render.RenderCanvasPng(path); } catch { /* not a canvas */ }
                if (direct != null)
                    return direct;

                List<AnimationFrameDto> found = animation.CollectCanvases(
                    path, limit: 1, maxVisit: 400, maxDepth: 5);
                return found.Count > 0 ? render.RenderCanvasPng(found[0].Path) : null;
            });

            // Set before the null check, so "there is no art here" is cached too.
            //
            // It used to be set only on the hit path, which made every artless row
            // re-run the 400-node depth-5 search on every repaint -- and that search
            // parses a whole image under the session gate to answer a question whose
            // answer never changes. Measured on a v232 client: 16% of Mob.wz rows
            // have no art, so a 2,742-row grid re-ran ~439 of those searches on
            // every scroll pass, for ever.
            SetMediaCacheHeaders(request, response);

            if (png == null)
                return Results.NoContent();

            return Results.File(png, "image/png");
        });

        api.MapGet("/export/images", (string path, ExportService export) =>
        {
            byte[] zip = export.ExportImages(path);
            string name = SafeFileName(path) + "-images.zip";
            return Results.File(zip, "application/zip", name);
        });

        api.MapGet("/export/img", (string path, ExportService export) =>
        {
            (byte[] data, string name) = export.ExportImg(path);
            return Results.File(data, "application/octet-stream", name);
        });

        api.MapGet("/export/json", (string path, int? depth, ExportService export) =>
        {
            byte[] json = export.ExportJson(path, Math.Clamp(depth ?? 12, 1, 40));
            return Results.File(json, "application/json", SafeFileName(path) + ".json");
        });

        // Classic <imgdir> XML: what a v83 server emulator reads instead of the
        // archive. The download is named after the image rather than the path,
        // because a server's dump is keyed by that name.
        api.MapGet("/export/xml", (string path, XmlExportService xml) =>
        {
            (byte[] data, string name) = xml.ExportImage(path);
            return Results.File(data, "application/xml", name);
        });

        api.MapGet("/export/xml-zip", (string path, int? limit, XmlExportService xml, HttpResponse response) =>
        {
            (byte[] data, string name, int images, bool truncated) = xml.ExportTree(
                path, Math.Clamp(limit ?? XmlExportService.DefaultMaxImages, 1, XmlExportService.MaxImagesCeiling));

            // The ZIP's manifest.json says this too, but a browser download can
            // only see headers, and "you got 2,000 of 40,000 images" is not
            // something to find out after unzipping.
            response.Headers["X-Export-Count"] = images.ToString();
            response.Headers["X-Export-Truncated"] = truncated ? "true" : "false";
            return Results.File(data, "application/zip", name);
        });

        api.MapGet("/raw", (string path, WzRenderService render) =>
        {
            byte[]? data = render.GetRawBytes(path);
            return data == null
                ? Results.NotFound(new ApiError("This node has no raw data."))
                : Results.File(data, "application/octet-stream", SafeFileName(path) + ".bin");
        });

        api.MapPost("/canvas", async (HttpRequest http, string path, WzEditService edit) =>
        {
            if (!http.HasFormContentType)
                return Results.BadRequest(new ApiError("Upload the image as multipart/form-data."));

            IFormFile? file = (await http.ReadFormAsync()).Files.GetFile("image");
            if (file == null)
                return Results.BadRequest(new ApiError("No 'image' file was included."));

            if (file.Length == 0)
                return Results.BadRequest(new ApiError($"'{file.FileName}' is empty."));
            if (file.Length > MaxUploadBytes)
            {
                return Results.BadRequest(new ApiError(
                    $"'{file.FileName}' is {file.Length / (1024 * 1024)} MB. " +
                    $"The limit is {MaxUploadBytes / (1024 * 1024)} MB — WZ sprites are small, " +
                    "so a file this size is usually the wrong one."));
            }

            System.Drawing.Bitmap bitmap;
            try
            {
                await using Stream stream = file.OpenReadStream();
                bitmap = new System.Drawing.Bitmap(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
            {
                // GDI+ reports every decode failure as "Parameter is not valid",
                // which reads as a bug in the editor rather than a bad file.
                return Results.BadRequest(new ApiError(
                    $"'{file.FileName}' could not be read as an image. " +
                    "PNG, BMP, GIF and JPEG are supported; the file may be truncated or renamed."));
            }

            using (bitmap)
            {
                // Checked after decode because the header is what carries the
                // dimensions, but before the copy: a 20 000 x 20 000 PNG is a
                // 1.6 GB allocation, and no WZ canvas is anywhere near that.
                if (bitmap.Width > MaxCanvasSide || bitmap.Height > MaxCanvasSide)
                {
                    return Results.BadRequest(new ApiError(
                        $"'{file.FileName}' is {bitmap.Width}x{bitmap.Height}. " +
                        $"The limit is {MaxCanvasSide}x{MaxCanvasSide} per side."));
                }

                // Detach from the request stream before it is disposed.
                using System.Drawing.Bitmap owned = new(bitmap);
                return Results.Ok(edit.SetCanvasImage(path, owned));
            }
        });
    }

    /// <summary>
    /// How long the browser may reuse a rendered sprite.
    ///
    /// The URL decides, because the front end already tells us which kind it is.
    /// <c>wwwroot/js/media.js</c> mints every thumbnail and canvas URL with a
    /// <c>v=&lt;epoch&gt;.&lt;revision&gt;</c> stamp, bumps the revision of a node and
    /// all its ancestors when a sprite is replaced, and retires the whole epoch
    /// when a file is closed. A stamped URL therefore cannot outlive the art it
    /// names, and a year is safe. An unstamped one carries no such promise —
    /// somebody's saved link, a direct download — so it gets an hour, which is
    /// what /api/thumb already used for everything.
    /// </summary>
    private static void SetMediaCacheHeaders(HttpRequest request, HttpResponse response)
    {
        response.Headers.CacheControl = request.Query.ContainsKey("v")
            ? "private, max-age=31536000, immutable"
            : "private, max-age=3600";
    }

    #endregion

    #region Search

    private static void MapSearch(RouteGroupBuilder api)
    {
        // The cancellation token matters here specifically: a search holds the
        // global session lock while it runs, so one the user has navigated away
        // from is not merely wasted work -- it blocks every other request.
        api.MapPost("/search", (SearchRequest request, WzSearchService search, CancellationToken cancel) =>
        {
            (List<SearchHitDto> hits, bool truncated, int scanned) = search.Search(request, cancel);
            return Results.Ok(new { hits, truncated, scanned });
        });

        api.MapPost("/replace", (ReplaceRequest request, WzSearchService search, CancellationToken cancel) =>
        {
            // Only the scan honours cancellation; once writing starts it runs to
            // completion, because a half-applied replace is worse than a slow one.
            (List<SearchHitDto> changed, bool truncated) = search.Replace(request, cancel);
            return Results.Ok(new { changed, truncated, dryRun = request.DryRun });
        });
    }

    #endregion

    /// <summary>
    /// Turns the exceptions the services throw into the shape the UI expects,
    /// so every failure surfaces as a readable toast rather than a 500 page.
    /// </summary>
    private static async ValueTask<object?> ErrorFilter(
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
            return Results.Json(
                new ApiError("Something went wrong.", ex.Message), statusCode: 500);
        }
    }
}

public sealed class SetValuesRequest
{
    public List<string> Paths { get; set; } = new();
    public string? Value { get; set; }
}

public sealed class ReorderRequest
{
    public string Path { get; set; } = "";
    public int Index { get; set; }
}

public sealed class CloneRequest
{
    public string FileId { get; set; } = "";
    /// <summary>Node name of the entry to clone.</summary>
    public string Key { get; set; } = "";
    /// <summary>Optional replacement item id for the copy.</summary>
    public int? ItemId { get; set; }
}

public sealed class DeleteItemsRequest
{
    public string FileId { get; set; } = "";
    public List<string> Keys { get; set; } = new();
}
