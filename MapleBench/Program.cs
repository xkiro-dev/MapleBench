using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapleBench.Api;
using MapleBench.Services;
using Microsoft.AspNetCore.StaticFiles;

namespace MapleBench;

public static class Program
{
    private const int PreferredPort = LaunchPlan.PreferredPort;

    public static void Main(string[] args)
    {
        bool allowMultiple = args.Contains("--allow-multiple");

        // Read BEFORE anything is allowed to end another copy, and used by the
        // decision that does the ending.
        //
        // The order used to be the other way round: the kill swept first and the
        // port was resolved afterwards, so a launch that had been told exactly
        // where to go still ended the copy it was told to stay away from. See
        // LaunchPlan for the two bugs that produced, both of which are now
        // decided by LaunchPlan.Contends and tested.
        int? requestedPort = LaunchPlan.ExplicitPort(args);

        // MapleLib's WzFile.SaveToDisk creates its scratch file with a *relative*
        // path, so it lands in whatever the process working directory happens to
        // be.  Point that at a private temp folder rather than the install dir,
        // which may be read-only or shared.
        string scratchRoot = Path.Combine(Path.GetTempPath(), "MapleBench");
        string scratch = LaunchPlan.ScratchFolder(Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.CurrentDirectory = scratch;

        // Both markers are published before the sweep below, not after, because
        // their whole job is to be readable by another launch — and the launch
        // most likely to be racing this one is the one starting right now.
        //
        // --allow-multiple used to mean only "I will not kill anyone", which is
        // half of what it should mean: the next ordinary launch still killed
        // *this* copy, because instances are matched by process name and have no
        // way to ask each other about flags. A marker in the scratch folder is
        // that missing channel -- it is named after the pid, so it is both
        // discoverable and self-cleaning. The port marker beside it is the same
        // idea and closes the other half: without it a copy on any port at all
        // looked exactly like a copy squatting on ours.
        if (allowMultiple)
            LaunchPlan.PublishProtection(scratch);
        LaunchPlan.PublishPort(scratch, requestedPort ?? PreferredPort);

        // Only one editor at a time on a given port. Every launch would otherwise
        // leave the previous one holding its port, its WZ file handles and its
        // memory, and they pile up invisibly across a day's work.
        if (!allowMultiple && !TerminateOtherInstances(requestedPort))
        {
            // Nothing has been started yet, so the folder this launch just made
            // is litter. Left behind, it would be swept by the next launch, but
            // only once that launch had already decided this pid was dead.
            try { Directory.Delete(scratch, true); } catch { /* best effort */ }
            return;   // an existing instance has unsaved work; leave it alone
        }

        // Shutdown deletes this process's own folder, which does nothing for a
        // copy that was hard-killed — and this app kills its predecessor on
        // every launch, so the orphans accumulate at exactly the rate you use
        // it. Runs after the kill above, so a live instance's folder is still
        // matched by a live pid.
        SweepAbandonedScratch(scratchRoot);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Serve wwwroot from next to the executable, not from the scratch dir.
            ContentRootPath = AppContext.BaseDirectory,
        });

        int port = ResolvePort(requestedPort);
        // The final answer, which may differ from the intent published above when
        // no port was named and 5100 was taken.
        LaunchPlan.PublishPort(scratch, port);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            // Enum names stay PascalCase: NodeKind values are compared against
            // literals like "Property" in the client, and they line up with the
            // WzPropertyType names sent alongside them.
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Makes a binding failure loud enough to be caught and explained.
        //
        // Minimal-API parameter binding runs *before* endpoint filters, so
        // Endpoints.ErrorFilter — which turns every service exception into a
        // readable ApiError — never sees a body that failed to deserialise. The
        // framework's own answer is "400 Bad Request, Content-Length: 0", which
        // is what /api/import/open was observed returning: reproduced here by
        // posting a Windows path with unescaped backslashes ("C:\Nexon\..."),
        // where \N is not a JSON escape. The UI showed no message because there
        // was no message, and no server log above Debug either.
        //
        // ThrowOnBadRequest turns that silent 400 into a BadHttpRequestException,
        // which ExplainBadRequests below converts back into a 400 that says what
        // was wrong with the request.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        builder.Services.AddSingleton<WzSessionService>();
        builder.Services.AddSingleton<UndoService>();
        builder.Services.AddSingleton<WzEditService>();
        builder.Services.AddSingleton<WzSaveService>();
        builder.Services.AddSingleton<WzSearchService>();
        builder.Services.AddSingleton<WzRenderService>();
        builder.Services.AddSingleton<StringPoolService>();
        builder.Services.AddSingleton<IconService>();
        builder.Services.AddSingleton<CashShopService>();
        builder.Services.AddSingleton<MobService>();
        builder.Services.AddSingleton<MapAssetService>();
        builder.Services.AddSingleton<Services.MapEditor.MapEditorService>();
        builder.Services.AddSingleton<StringEditService>();
        builder.Services.AddSingleton<NpcService>();
        builder.Services.AddSingleton<SkillService>();
        builder.Services.AddSingleton<PortService>();
        builder.Services.AddSingleton<NodeFactsService>();
        builder.Services.AddSingleton<AnimationService>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<XmlExportService>();
        builder.Services.AddSingleton<DumpService>();

        // Singleton because DumpJobService is what enforces "one dump at a
        // time", the same way ImportProgressService does for imports: a
        // per-request instance would enforce nothing, and two archive dumps at
        // once share one disk and one session gate.
        builder.Services.AddSingleton<DumpJobService>();
        builder.Services.AddSingleton<ImageMemoryService>();
        builder.Services.AddSingleton<WarmupService>();

        // Registered both ways on purpose: as a singleton so the diagnostics
        // endpoint can ask it for one pass on demand, and as a hosted service so
        // the same instance is polled on its own timer. AddHostedService alone
        // would create a second copy the endpoint could not reach.
        builder.Services.AddSingleton<MemoryPressureService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MemoryPressureService>());

        // Singletons because ImportProgressService is the thing that enforces
        // "one import at a time", and a per-request instance would enforce
        // nothing at all.
        builder.Services.AddSingleton<ClientImportService>();
        builder.Services.AddSingleton<ImportProgressService>();
        builder.Services.AddSingleton<IntegrityAuditService>();
        builder.Services.AddSingleton<CanvasFormatRepairService>();
        builder.Services.AddSingleton<DonorRestoreService>();
        builder.Services.AddSingleton<CanvasDirLinkRepairService>();
        builder.Services.AddSingleton<GenerationChooserService>();
        builder.Services.AddSingleton<ArchiveFamilyService>();

        // Singleton because CompositionRunService is what enforces "one build at
        // a time" — a build writes a whole client to disk, and two at once would
        // interleave archive writes with no owner.
        builder.Services.AddSingleton<Services.Composition.CompositionRunService>();

        // Measured on a v232 client: the 25 requests a boot makes came to
        // 695 KB, every one of them uncompressed, because nothing ever added
        // the middleware. It is worth more than that number suggests -- the
        // section lists go out through the same pipe, and the skill list alone
        // is 2.7 MB of JSON.
        builder.Services.AddResponseCompression(options =>
        {
            // The server binds 127.0.0.1 and speaks plain HTTP, so the
            // BREACH-style concerns behind the HTTPS default do not apply.
            options.EnableForHttps = false;
            options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            // The defaults cover html/css/js/json/xml/plain. Added here: the
            // icon font and SVG art the UI ships, which are text underneath.
            options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
                .Concat(new[] { "image/svg+xml", "application/manifest+json", "text/javascript" });
        });
        // Optimal, not SmallestSize. Nothing here is compressed ahead of time, so
        // the cost is paid per response on a thread that is not holding the
        // session gate; measured on /js/app.js (85 KB), Fastest gives 34 KB and
        // Optimal 25 KB for about a millisecond more, while SmallestSize costs
        // ~40 ms for another 2 KB and would be charged again on every uncached
        // response, including the 2.7 MB skill list.
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
            options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
            options => options.Level = System.IO.Compression.CompressionLevel.Optimal);

        WebApplication app = builder.Build();

        // Measurement switch; see WarmupService.Enabled.
        if (args.Contains("--no-warmup"))
            app.Services.GetRequiredService<WarmupService>().Enabled = false;

        app.Use(LocalOnly(port));
        // Indexing is speculative; an actual user action is not. Cancel before
        // the endpoint can ask for the session gate, then start the idle clock
        // only after the complete response has finished. Starting it at request
        // arrival lets a long import or search outlive the 1.5 second delay and
        // race a new warm-up while it is still doing foreground work.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }

            WarmupService warmup = context.RequestServices.GetRequiredService<WarmupService>();
            warmup.BeginForeground();
            try
            {
                await next(context);
            }
            finally
            {
                warmup.EndForeground();
            }
        });
        app.Use(ExplainBadRequests);
        app.UseResponseCompression();

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SetStaticCacheHeaders });
        app.MapEndpoints();

        // Its own group, mapped separately -- see DumpEndpoints for why a
        // file-only addition is worth four duplicated lines of error filter.
        app.MapDump();

        string url = $"http://127.0.0.1:{port}";

        // How the UI is shown:
        //   (default)     a desktop window, via WebView2
        //   --browser     the default browser instead
        //   --no-browser  headless; just serve, open nothing
        bool headless = args.Contains("--no-browser");
        bool useBrowser = args.Contains("--browser");

        // Before the host starts listening, not after.
        //
        // Bringing up WebView2 -- spawning the Chromium process tree and, on a
        // first run, building a browser profile -- is the slowest thing in a cold
        // launch, and it used to begin only once Kestrel had bound. Measured, that
        // left ~960 ms of blank grey window. Started here it overlaps host startup
        // instead of queueing behind it (measured 553 ms off the time to a usable
        // window), and the window paints its splash meanwhile.
        //
        // Only for the desktop path: in --browser or --no-browser runs this would
        // spawn Chromium and create a profile that nothing ever uses.
        if (!headless && !useBrowser)
            Desktop.DesktopShell.BeginWarmup();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine();
            Console.WriteLine($"  MapleBench is running at {url}");
            Console.WriteLine();

            if (headless)
                return;

            if (useBrowser)
            {
                OpenBrowser(url);
                return;
            }

            // The window owns the UI thread, so it runs on its own; when it
            // closes, the server goes with it.
            Thread ui = new(() =>
            {
                if (!Desktop.DesktopShell.TryRun(url, out string? failure))
                {
                    Console.WriteLine($"  Could not open the desktop window ({failure}).");
                    Console.WriteLine("  Falling back to your browser.");
                    OpenBrowser(url);
                    return;
                }
                // The window was closed: shut the service down with it.
                app.Lifetime.StopApplication();
            })
            {
                IsBackground = true,
                Name = "MapleBench UI",
            };
            ui.SetApartmentState(ApartmentState.STA);   // required by WinForms dialogs
            ui.Start();
        });

        app.Lifetime.ApplicationStopped.Register(() =>
        {
            try { Directory.Delete(scratch, true); } catch { /* best effort */ }
        });

        app.Run();
    }

    /// <summary>
    /// Makes sure a 4xx never leaves this server without saying why.
    ///
    /// Two holes it plugs, and both were reachable:
    ///
    ///   * A request body that will not deserialise. Binding happens before
    ///     endpoint filters, so <c>Endpoints.ErrorFilter</c> cannot see it.
    ///     With <c>ThrowOnBadRequest</c> set (see Main) it arrives here as a
    ///     <see cref="BadHttpRequestException"/> carrying the parser's own
    ///     message, which is the only description of the fault that exists.
    ///     Measured before this: POST /api/import/open with a Windows path whose
    ///     backslashes were not escaped answered "400, Content-Length: 0" in
    ///     1.7 ms, with nothing logged above Debug — an error the user cannot
    ///     act on and a developer cannot find.
    ///
    ///   * Anything else that sets a 4xx and writes no body — a route constraint
    ///     that did not match, a request-size limit. The catch-all below turns
    ///     those into the same <see cref="ApiError"/> shape every other failure
    ///     already uses, so the UI's one toast path covers them too.
    ///
    /// Deliberately narrow: it never touches a response that already has a body,
    /// and it never converts a 5xx (those carry a detail the ErrorFilter wrote).
    /// </summary>
    private static async Task ExplainBadRequests(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new Models.ApiError(
                "This request could not be read.", ex.Message));
            return;
        }

        // Nothing was written. ContentLength is null when the response was never
        // given a body at all, which is the case that produced the silent 400;
        // 204 and 304 are meant to be empty, so they are left alone.
        if (!context.Response.HasStarted &&
            context.Response.StatusCode is >= 400 and < 500 &&
            context.Response.ContentLength is null or 0 &&
            context.Response.ContentType is null)
        {
            await context.Response.WriteAsJsonAsync(new Models.ApiError(
                $"The request was refused ({context.Response.StatusCode}) and the server gave no reason. " +
                "This is a bug in MapleBench; please report the action that caused it."));
        }
    }

    /// <summary>
    /// How long a static asset may be reused without asking.
    ///
    /// The constraint here is not the usual one. This is a desktop app whose
    /// wwwroot is replaced on every rebuild, at the same URLs, and served to a
    /// WebView2 whose HTTP cache outlives the process — so a generous max-age on
    /// /js/app.js means a rebuilt UI running against a rebuilt server out of the
    /// browser's copy of the *old* JavaScript, with no error and no way for the
    /// user to know. That is a worse failure than a slow boot.
    ///
    /// So the rule is drawn by what the file can break rather than by how often
    /// it changes:
    ///
    ///   * Anything carrying a version in its query string is immutable for a
    ///     year. A distinct URL cannot go stale, so this is free and safe, and
    ///     it is the hook the front end needs — appending "?v=&lt;build&gt;" to
    ///     the script and stylesheet URLs in index.html would take all 24
    ///     revalidations to zero. media.js already stamps its /api/thumb and
    ///     /api/canvas URLs exactly this way, so the convention is the app's own.
    ///   * HTML, JS and CSS revalidate every time ("no-cache" means "store it,
    ///     but ask"). The ask is a conditional GET on loopback answered 304 from
    ///     the ETag ASP.NET already emits: ~200 bytes and about a millisecond,
    ///     against the 85 KB /js/app.js it replaces. This is the deliberate
    ///     choice not to trade correctness for those milliseconds.
    ///   * Images, fonts and icons get a day. A stale glyph is cosmetic, and
    ///     these are what a scrolling grid asks for repeatedly.
    /// </summary>
    private static void SetStaticCacheHeaders(StaticFileResponseContext context)
    {
        HttpResponse response = context.Context.Response;

        if (context.Context.Request.Query.ContainsKey("v"))
        {
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return;
        }

        string extension = Path.GetExtension(context.File.Name).ToLowerInvariant();
        response.Headers.CacheControl = extension switch
        {
            ".html" or ".htm" or ".js" or ".mjs" or ".css" or ".json" or ".map" => "private, no-cache",
            _ => "private, max-age=86400",
        };
    }

    /// <summary>
    /// Deletes scratch folders left behind by copies that are no longer running.
    ///
    /// Each folder is named after the process that owned it, so "is anything
    /// still using this?" is answerable exactly rather than by age. A folder
    /// whose name is not a number was not made by us and is left alone; so is
    /// one we cannot delete, which usually means someone else is using it.
    /// </summary>
    private static void SweepAbandonedScratch(string root)
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root);
        }
        catch
        {
            return;   // nothing there yet, or not readable
        }

        foreach (string folder in folders)
        {
            if (!int.TryParse(Path.GetFileName(folder), out int pid) || pid == Environment.ProcessId)
                continue;

            try
            {
                using Process owner = Process.GetProcessById(pid);
                if (!owner.HasExited)
                    continue;   // still in use
            }
            catch (ArgumentException)
            {
                // No such process: the folder is an orphan, which is the case
                // we are here for.
            }
            catch
            {
                continue;   // cannot tell; leave it
            }

            try { Directory.Delete(folder, true); }
            catch { /* best effort; it will be swept next time */ }
        }
    }

    /// <summary>
    /// Rejects anything that did not come from this machine's own UI.
    ///
    /// Binding to 127.0.0.1 keeps other machines out, but not other *pages* on
    /// this one. A web page you have open in a browser can POST to
    /// http://127.0.0.1:5100 all it likes, and DNS rebinding lets a page on
    /// evil.example re-point that hostname at loopback and then read the
    /// responses — at which point a site you visited can enumerate your WZ
    /// files, edit them and save over your client.
    ///
    /// Two checks stop it, and neither costs the real UI anything:
    ///   - the Host header must name loopback on our port. A rebinding attack
    ///     still sends "evil.example:5100", because that is what the victim's
    ///     browser typed.
    ///   - Origin, when present, must be our own. Same-origin requests from our
    ///     page omit it or send ours; a cross-site fetch cannot forge it.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> LocalOnly(int port)
    {
        string[] allowedHosts =
        {
            $"127.0.0.1:{port}",
            $"localhost:{port}",
            $"[::1]:{port}",
        };
        string[] allowedOrigins =
        {
            $"http://127.0.0.1:{port}",
            $"http://localhost:{port}",
            $"http://[::1]:{port}",
        };

        return async (context, next) =>
        {
            string host = context.Request.Host.Value ?? "";
            if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                await Reject(context, "host");
                return;
            }

            string? origin = context.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin) &&
                !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                await Reject(context, "origin");
                return;
            }

            await next(context);
        };

        static Task Reject(HttpContext context, string which)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(
                $"Refused: this editor only answers its own window (bad {which}).");
        }
    }

    /// <summary>
    /// Ends any other copy of MapleBench that is already running — unless that
    /// copy has unsaved work.
    ///
    /// A stale instance keeps its WZ files open, which is what makes a later
    /// save refuse with "the file is in use", so clearing them out matters.
    /// But a live instance holding an hour of edits looks identical from the
    /// outside, and killing it destroys that work with no way back. So we ask
    /// it first: it answers on /api/files, and a dirty answer means we step
    /// aside instead.
    /// </summary>
    /// <returns>False when an existing instance owns unsaved work and must be kept.</returns>
    private static bool TerminateOtherInstances(int? requestedPort)
    {
        Process self = Process.GetCurrentProcess();

        // By process name. A second copy would hold port 5100 and a set of WZ
        // file handles; this one would fall through to a free port, and the user
        // would end up with two editors open on the same archive — exactly what
        // this method exists to prevent.
        Process[] others;
        try
        {
            others = Process.GetProcessesByName(self.ProcessName)
                .DistinctBy(process => process.Id)
                .ToArray();
        }
        catch
        {
            return true;   // enumeration can fail under restricted rights
        }

        int stopped = 0;
        foreach (Process other in others)
        {
            using (other)
            {
                if (other.Id == self.Id)
                    continue;

                int? otherPort = LaunchPlan.PublishedPort(other.Id);
                bool protectedInstance = LaunchPlan.IsProtected(other.Id);

                if (!LaunchPlan.Contends(requestedPort, otherPort, protectedInstance))
                {
                    Console.WriteLine(protectedInstance
                        ? $"  Leaving instance {other.Id} alone (started with --allow-multiple)."
                        : $"  Leaving instance {other.Id} alone (it is on port {otherPort}, not ours).");
                    continue;
                }

                if (HasUnsavedWork(other, otherPort, out string? where))
                {
                    Console.WriteLine();
                    Console.WriteLine("  MapleBench is already running and has unsaved changes.");
                    Console.WriteLine($"  Save or discard them there first: {where}");
                    Console.WriteLine("  (Use --allow-multiple to run a second copy anyway.)");
                    Console.WriteLine();
                    return false;
                }

                try
                {
                    // Said before it happens, and said with the pid and the port.
                    //
                    // A killed process writes nothing: no exception, no shutdown
                    // line, no crash record in the event log — its log simply
                    // ends mid-session. That is indistinguishable from a crash
                    // from the victim's side, and it cost a long investigation
                    // into a "DELETE crashes the process" bug that was this
                    // line all along. The only place the truth can be recorded
                    // is here, in the process doing it.
                    Console.WriteLine($"  Ending instance {other.Id} (port {otherPort?.ToString() ?? "unknown"}); " +
                                      "it reported no unsaved work.");
                    other.Kill(entireProcessTree: true);
                    // Give the OS a moment to release the port and file handles
                    // before we try to bind and open them ourselves.
                    other.WaitForExit(4000);
                    stopped++;
                }
                catch
                {
                    // Another user's session, or already gone. Not fatal: the
                    // port picker will fall back to a free port either way.
                }
            }
        }

        if (stopped > 0)
            Console.WriteLine($"  Closed {stopped} earlier MapleBench instance{(stopped == 1 ? "" : "s")}.");
        return true;
    }

    /// <summary>
    /// Asks a running instance whether it is holding unsaved edits.
    ///
    /// Its own published port is tried first and the small default range after,
    /// because the range alone was not enough and the gap was the dangerous
    /// kind: an instance that had fallen back past 5111, or been given --port
    /// 5802, answered on a port nothing here ever knocked on, so every probe
    /// came back "nothing listening" and it was killed without ever being asked
    /// about its unsaved work.
    ///
    /// "No answer" is NOT read as "safe to replace", and the distinction is the
    /// whole point. /api/files takes the session gate, and a save holds that gate
    /// for the entire multi-minute write of a large archive — so the one moment
    /// the probe is guaranteed to time out is the one moment killing the process
    /// destroys an hour of edits. A refused connection means nothing is listening
    /// there; a timeout means something is listening and busy. Only the first is
    /// grounds to move on.
    /// </summary>
    private static bool HasUnsavedWork(Process other, int? publishedPort, out string? where)
    {
        where = null;
        // Long enough to cover an ordinary request under a briefly-held gate. A
        // save holds it far longer than any timeout worth waiting for, which is
        // what the timeout branch below is for.
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };

        foreach (int port in ProbeOrder(publishedPort))
        {
            string url = $"http://127.0.0.1:{port}";
            try
            {
                string json = client.GetStringAsync($"{url}/api/files").GetAwaiter().GetResult();
                using JsonDocument document = JsonDocument.Parse(json);

                bool dirty = document.RootElement.EnumerateArray()
                    .Any(file => file.TryGetProperty("dirty", out JsonElement d) && d.GetBoolean());
                if (dirty)
                {
                    where = url;
                    return true;
                }
                return false;   // reachable and clean: safe to replace
            }
            catch (TaskCanceledException)
            {
                // Listening but did not answer in time — almost certainly mid-save or
                // mid-search. Assume the worst and refuse to replace it.
                where = url;
                return true;
            }
            catch (HttpRequestException)
            {
                // Nothing listening on this port, or it answered with something that
                // is not our API. Keep looking.
            }
            catch (JsonException)
            {
                // Answered, but not with our file list. Not a MapleBench we understand,
                // so do not assume it is disposable either.
                where = url;
                return true;
            }
            catch
            {
                // Anything else on this port is not something we can reason about --
                // an unrelated local service answering /api/files with a JSON object
                // rather than an array used to throw InvalidOperationException out of
                // here, out of TerminateOtherInstances, and out of Main, so MapleBench
                // could not start at all. Keep looking instead.
            }
        }
        return false;
    }

    /// <summary>
    /// Which ports to knock on, published one first.
    ///
    /// The published port is asked before the default range because it is the
    /// only answer that is actually known; the range stays as the fallback for a
    /// copy from a build that did not publish one.
    /// </summary>
    private static IEnumerable<int> ProbeOrder(int? publishedPort)
    {
        if (publishedPort is int known)
            yield return known;

        for (int port = PreferredPort; port < PreferredPort + 12; port++)
        {
            if (port != publishedPort)
                yield return port;
        }
    }

    private static int ResolvePort(int? requestedPort)
    {
        // An explicitly named port is taken as given, including when something
        // else already holds it: falling back would put the app somewhere the
        // caller is not looking, and a bind failure that says so is the more
        // useful answer.
        if (requestedPort is int requested)
            return requested;

        return IsPortFree(PreferredPort) ? PreferredPort : FindFreePort();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using TcpListener listener = new(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (Could not open a browser automatically: {ex.Message})");
        }
    }
}
