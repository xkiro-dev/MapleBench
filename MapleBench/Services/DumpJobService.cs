using System.Diagnostics;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// A subtree — or a whole archive — written out as real folders and files, with
/// progress and a cancel that works.
///
/// The ZIP exports are the right shape for one node. They are the wrong shape
/// for an archive: a 4 GB Map.wz cannot be assembled in memory before the first
/// byte reaches the user, and a download that takes eleven minutes with no
/// progress and no way to stop is indistinguishable from a hang. So this one
/// takes a destination folder, streams to it, counts as it goes and stops when
/// asked.
///
/// What it writes, per folder:
///  * one PNG per canvas, one .mp3/.wav per sound, both under the node's own
///    name run through <see cref="DumpNames"/>;
///  * one subfolder per container;
///  * one <c>_node.json</c> sidecar holding everything a folder cannot: the
///    scalar values, each canvas's origin/delay/format, each link's text and
///    target, and — the part that is easy to miss — **the order the children are
///    in**. WZ child order is not canonical and must be preserved on write
///    (docs/map-data-model.md measured 212 map images that do not even start
///    with <c>info</c>), and a directory listing is alphabetical. Without the
///    sidecar a dump cannot be turned back into an archive.
///
/// One dump at a time, like the import and the audit, and for the same reason:
/// they are all bounded by the same disk and the same session gate, and two at
/// once are slower than one after the other while making the progress readout
/// meaningless.
/// </summary>
public sealed class DumpJobService
{
    /// <summary>
    /// Nodes one dump may write before it stops and says so. A whole v232 client
    /// is ~3.3 million canvases; this is per-dump and generous for anything
    /// short of that, and the point of it is that the stop is REPORTED — an
    /// export that quietly ends early is the failure mode this whole file exists
    /// to avoid.
    /// </summary>
    public const int DefaultMaxNodes = 400_000;
    public const int MaxNodesCeiling = 5_000_000;

    /// <summary>
    /// And a byte ceiling, because node count does not predict size: a hundred
    /// map backgrounds outweigh a hundred thousand ints.
    /// </summary>
    private const long MaxBytes = 24L * 1024 * 1024 * 1024;

    private readonly WzSessionService _session;
    private readonly WzRenderService _render;
    private readonly DumpService _dump;
    private readonly WarmupService _warmup;
    private readonly ILogger<DumpJobService> _log;

    private readonly object _gate = new();
    private DumpProgressDto _current = new();
    private bool _running;
    private CancellationTokenSource? _cancel;

    public DumpJobService(
        WzSessionService session, WzRenderService render, DumpService dump,
        WarmupService warmup, ILogger<DumpJobService> log)
    {
        _session = session;
        _render = render;
        _dump = dump;
        _warmup = warmup;
        _log = log;
    }

    #region Job control

    /// <summary>
    /// Everything that can be refused, refused before a single file is written.
    ///
    /// Deliberately all up front. "Say it before the write, not after" is the
    /// house rule this file is most exposed to: by the time a dump has run for
    /// four minutes and then finds the destination unwritable, it has already
    /// created three thousand files somewhere the user did not want them.
    /// </summary>
    public DumpPreflightDto Preflight(DumpJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return Refuse("No node was given to dump.");
        if (string.IsNullOrWhiteSpace(request.OutputDir))
            return Refuse("No destination folder was given.");

        string name;
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(request.Path);
            name = ExportService.Sanitise(node.Name ?? "dump");
        }

        string parent;
        try
        {
            parent = Path.GetFullPath(request.OutputDir);
        }
        catch (Exception ex)
        {
            return Refuse($"'{request.OutputDir}' is not a usable folder path: {ex.Message}");
        }

        if (!Directory.Exists(parent))
            return Refuse($"'{parent}' does not exist. Make it first, or pick a folder that does.");

        // A folder holding archives is somebody's client. Writing a dump into it
        // mixes thousands of loose PNGs in with the files the game loads, and the
        // user finds out by not being able to tell them apart afterwards.
        if (!request.AllowNonEmpty && Directory.EnumerateFiles(parent, "*.wz").Any())
        {
            return Refuse(
                $"'{parent}' holds .wz archives, so it looks like a client folder rather than somewhere to put " +
                "a dump. Pick an empty folder, or say explicitly that this one is intended.",
                overridable: true);
        }

        string outputPath = Path.Combine(parent, name + "-dump");
        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any()
            && !request.AllowNonEmpty)
        {
            return Refuse(
                $"'{outputPath}' already has files in it. A second dump into it would mix two exports together " +
                "and overwrite the ones whose names match. Empty it, rename it, or say explicitly that this " +
                "one is intended.",
                overridable: true);
        }

        return new DumpPreflightDto { Ok = true, OutputPath = outputPath };
    }

    /// <summary>
    /// A refusal, and whether saying "yes, I mean it" would clear it.
    ///
    /// <see cref="DumpPreflightDto.Overridable"/> is a field rather than
    /// something the caller infers from the wording. The UI has to decide
    /// whether to offer an override checkbox, and a UI that decides by matching
    /// the end of an English sentence stops offering it the day somebody
    /// improves the sentence — which is the same class of coupling as a counter
    /// that means two different things.
    /// </summary>
    private static DumpPreflightDto Refuse(string refusal, bool overridable = false)
        => new() { Ok = false, Refusal = refusal, Overridable = overridable };

    /// <summary>
    /// Starts a dump and returns the first snapshot. The work runs on a
    /// background thread; the caller polls <see cref="Snapshot"/>.
    /// </summary>
    public DumpProgressDto Start(DumpJobRequest request)
    {
        DumpPreflightDto preflight = Preflight(request);
        if (!preflight.Ok)
            throw new InvalidOperationException(preflight.Refusal);
        string outputPath = preflight.OutputPath!;

        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    $"A dump of '{_current.Node}' is already running. Wait for it or cancel it — two dumps at " +
                    "once share one disk and one archive lock, so they finish later than one after the other.");
            }

            _running = true;
            _cancel = cancel = new CancellationTokenSource();
            _current = new DumpProgressDto
            {
                State = "running",
                Stage = "Starting",
                Node = Uri.UnescapeDataString(request.Path),
                Current = Uri.UnescapeDataString(request.Path),
            };
        }

        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { Run(request, outputPath, cancel.Token); }
                catch (Exception ex) { Fail(ex); }
                finally
                {
                    lock (_gate)
                    {
                        _running = false;
                        _cancel?.Dispose();
                        _cancel = null;
                    }
                }
            }
        });

        return Snapshot();
    }

    public DumpProgressDto Snapshot()
    {
        lock (_gate)
        {
            return new DumpProgressDto
            {
                State = _current.State,
                Stage = _current.Stage,
                Node = _current.Node,
                Current = _current.Current,
                Done = _current.Done,
                Total = _current.Total,
                Seconds = _current.Seconds,
                Cancelling = _current.Cancelling,
                Message = _current.Message,
                Result = _current.Result,
                FinishedUtc = _current.FinishedUtc,
            };
        }
    }

    /// <summary>
    /// Asks the dump to stop. Cooperative: it stops between nodes, so the files
    /// already written stay written and the report says how far it got. A
    /// cancelled dump is a partial dump that knows it is one.
    /// </summary>
    public DumpProgressDto Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
            if (_running)
                _current.Cancelling = true;
        }
        return Snapshot();
    }

    public DumpResultDto? Report()
    {
        lock (_gate)
            return _current.Result;
    }

    private void Fail(Exception ex)
    {
        _log.LogWarning(ex, "Dump failed");
        lock (_gate)
        {
            _current.State = "failed";
            _current.Stage = "Failed";
            _current.Message = ex.Message;
            _current.FinishedUtc = DateTime.UtcNow;
        }
    }

    #endregion

    #region The walk

    private void Run(DumpJobRequest request, string outputPath, CancellationToken token)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Directory.CreateDirectory(outputPath);

        State state = new()
        {
            Result = _dump.NewResult(request.Path, request.Format == "png" ? "png-tree" : "tree"),
            MaxNodes = request.MaxNodes <= 0
                ? DefaultMaxNodes
                : Math.Clamp(request.MaxNodes, 1, MaxNodesCeiling),
            PngOnly = request.Format == "png",
            ResolveLinks = request.ResolveLinks,
            Token = token,
            Clock = clock,
        };
        state.Result.OutputPath = outputPath;

        try
        {
            DumpContainer(request.Path, outputPath, 0, state);
        }
        catch (OperationCanceledException)
        {
            // Not an error: the files written so far are real and the report
            // below is what says the rest was not written.
        }

        DumpResultDto result = state.Result;
        if (state.Truncated && result.TruncatedReason == null)
            result.TruncatedReason = $"Stopped at the {state.MaxNodes} node limit.";
        result.Truncated |= state.Truncated;

        bool cancelled = token.IsCancellationRequested;
        if (cancelled)
        {
            result.Truncated = true;
            result.TruncatedReason = "Cancelled — everything already written is complete, the rest is missing.";
        }

        // The report goes down last and always, including after a cancel: a
        // folder of files with no account of what is missing from it is the
        // thing this feature exists not to produce.
        TryWriteReport(outputPath, result);

        lock (_gate)
        {
            _current.State = cancelled ? "cancelled" : "done";
            _current.Stage = cancelled ? "Cancelled" : "Finished";
            _current.Seconds = clock.Elapsed.TotalSeconds;
            _current.Cancelling = false;
            _current.Result = result;
            _current.FinishedUtc = DateTime.UtcNow;
            _current.Message = Summary(result, cancelled);
        }
    }

    private static string Summary(DumpResultDto result, bool cancelled)
    {
        string head =
            $"{result.Files} file(s): {result.Canvases} picture(s), {result.Sounds} sound(s), " +
            $"{result.Nodes} node(s) recorded.";

        // Said here rather than only in the sidecars. In a Mob.wz image every
        // single canvas is typically an _inlink into _Canvas, so "45 pictures"
        // can mean "45 pictures that belong to other nodes" — and a summary that
        // does not say so is the substitution this tool refuses elsewhere,
        // arriving quietly through the exporter instead.
        if (result.LinksRecorded > 0)
        {
            head +=
                $" {result.LinksRecorded} of them are references; each one's sidecar entry says whether a " +
                "picture was written and whose pixels it is.";
        }

        if (cancelled)
            return head + " Cancelled before it finished.";
        if (result.Issues.Count > 0)
            return head + $" {result.Issues.Count} thing(s) did not export — see dump-report.json.";
        return head + " Nothing else was skipped.";
    }

    /// <summary>
    /// One container: read its children under the session gate, then write them
    /// with the gate released.
    ///
    /// The split is the whole reason this is usable. Holding the global session
    /// gate for the length of an archive dump freezes the tree, the inspector and
    /// every other request for minutes — the editor would look hung while
    /// reporting progress, which is worse than either. So the gate is taken per
    /// container to snapshot what is there, and released while the PNGs are
    /// encoded and written. What that costs is a tree that can change underneath
    /// a running dump; what it buys is a program that answers.
    /// </summary>
    private void DumpContainer(string path, string directory, int depth, State state)
    {
        state.Token.ThrowIfCancellationRequested();

        if (depth >= WzWalk.MaxDepth)
        {
            state.Add(new DumpIssueDto
            {
                Path = path,
                Kind = "budget.exhausted",
                Reason = $"Nesting reached {WzWalk.MaxDepth} levels here and the walk stopped descending.",
            });
            state.Truncated = true;
            return;
        }

        List<Child> children = ReadChildren(path, state);
        List<object> record = new(children.Count);
        DumpNames names = new();

        foreach (Child child in children)
        {
            state.Token.ThrowIfCancellationRequested();

            if (state.Result.Nodes >= state.MaxNodes)
            {
                state.Truncated = true;
                break;
            }
            state.Result.Nodes++;

            switch (child.Kind)
            {
                case ChildKind.Canvas:
                    record.Add(WriteCanvas(child, directory, names, state));
                    break;

                case ChildKind.Sound:
                    record.Add(WriteSound(child, directory, names, state));
                    break;

                case ChildKind.Link:
                    state.Result.LinksRecorded++;
                    record.Add(WriteLink(child, directory, names, state));
                    break;

                case ChildKind.Container:
                {
                    string folder = names.ForFolder(child.Name, out DumpIssueDto? issue);
                    if (issue != null)
                    {
                        issue.Path = child.Path;
                        state.Add(issue);
                    }

                    string sub = Path.Combine(directory, folder);
                    record.Add(new
                    {
                        name = child.Name,
                        kind = "container",
                        folder,
                        node = Uri.UnescapeDataString(child.Path),
                    });

                    try
                    {
                        Directory.CreateDirectory(sub);
                    }
                    catch (Exception ex)
                    {
                        state.Add(new DumpIssueDto
                        {
                            Path = child.Path,
                            Kind = "write.failed",
                            Reason = $"Could not create '{sub}': {ex.Message}",
                        });
                        break;
                    }

                    DumpContainer(child.Path, sub, depth + 1, state);
                    if (child.ReleaseImage)
                        Release(child.Path);
                    break;
                }

                default:
                    record.Add(new
                    {
                        name = child.Name,
                        kind = child.TypeName,
                        value = child.Value,
                    });
                    break;
            }

            Progress(child.Path, state);
        }

        // Written even for a folder of nothing but canvases: it is the only
        // record of the order they were in, and of the origins the PNGs cannot
        // carry.
        if (!state.PngOnly || record.Count > 0)
            TryWriteSidecar(path, directory, record, state);
    }

    /// <summary>
    /// A snapshot of one container's children, taken under the gate and used
    /// after it is released.
    ///
    /// Values are copied out rather than referenced: holding a WzImageProperty
    /// across a gate release is exactly how the memory sweep produces detached
    /// nodes, and the only thing that survives here is data.
    /// </summary>
    private List<Child> ReadChildren(string path, State state)
    {
        List<Child> children = new();

        lock (_session.Gate)
        {
            WzObject parent;
            try
            {
                parent = _session.Resolve(path);
            }
            catch (Exception ex)
            {
                state.Add(new DumpIssueDto
                {
                    Path = path,
                    Kind = "node.unsupported",
                    Reason = $"Could not be read: {ex.Message}",
                });
                return children;
            }

            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (WzObject child in _session.EnumerateChildren(parent))
            {
                string name = child.Name ?? "";
                seen.TryGetValue(name, out int occurrence);
                seen[name] = occurrence + 1;

                Child entry = new()
                {
                    Name = name,
                    Path = WzPath.Child(path, name, occurrence),
                    TypeName = child.GetType().Name,
                };

                switch (child)
                {
                    case WzCanvasProperty canvas:
                        entry.Kind = ChildKind.Canvas;
                        entry.Width = canvas.PngProperty?.Width ?? 0;
                        entry.Height = canvas.PngProperty?.Height ?? 0;
                        entry.Format = canvas.PngProperty?.Format.ToString();
                        System.Drawing.PointF origin = DumpService.SafeOrigin(canvas);
                        entry.OriginX = (int)origin.X;
                        entry.OriginY = (int)origin.Y;
                        entry.Delay = DumpService.ReadDelay(canvas);
                        if (canvas.ContainsInlinkProperty())
                        {
                            entry.LinkKind = "_inlink";
                            entry.LinkText = (canvas[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
                        }
                        else if (canvas.ContainsOutlinkProperty())
                        {
                            entry.LinkKind = "_outlink";
                            entry.LinkText = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
                        }
                        break;

                    case WzBinaryProperty sound:
                        entry.Kind = ChildKind.Sound;
                        entry.Delay = sound.Length;
                        entry.Extension = sound.FileExtension;
                        break;

                    case WzUOLProperty uol:
                        // Never descended into. WzUOLProperty.WzProperties hands
                        // back the resolved node's children, so treating it as a
                        // container writes another node's subtree under this
                        // node's name and, when it points at an ancestor, does
                        // not come back at all.
                        entry.Kind = ChildKind.Link;
                        entry.LinkKind = "uol";
                        entry.LinkText = uol.Value;
                        break;

                    case WzImage image:
                        entry.Kind = ChildKind.Container;
                        // Images this dump opens itself are released after it
                        // has finished with them; images the user already had
                        // open, or has edited, are left exactly as they were.
                        entry.ReleaseImage = !image.Parsed && !image.Changed;
                        break;

                    case WzDirectory:
                        entry.Kind = ChildKind.Container;
                        break;

                    default:
                    {
                        string? scalar = DumpService.ScalarText(child);
                        if (scalar != null)
                        {
                            entry.Kind = ChildKind.Value;
                            entry.Value = scalar;
                        }
                        else
                        {
                            entry.Kind = (child as WzImageProperty)?.WzProperties?.Count > 0
                                ? ChildKind.Container
                                : ChildKind.Value;
                            entry.Value = null;
                        }
                        break;
                    }
                }

                children.Add(entry);
            }
        }

        return children;
    }

    private object WriteCanvas(Child child, string directory, DumpNames names, State state)
    {
        bool isLink = child.LinkKind != null;

        // A linking canvas holds no pixels of its own. Writing the target's
        // pixels under this node's name is a defensible thing to do — it is what
        // a sprite dump is usually for — but it is never done silently: the file
        // is marked as resolved in the sidecar and counted as a followed link.
        if (isLink && !state.ResolveLinks)
        {
            state.Result.LinksRecorded++;
            return new
            {
                name = child.Name,
                kind = "canvas",
                node = Uri.UnescapeDataString(child.Path),
                link = new { kind = child.LinkKind, text = child.LinkText },
                file = (string?)null,
                note = "No picture was written: this canvas is a link, and following links was not asked for.",
                origin = new { x = child.OriginX, y = child.OriginY },
                delayMs = child.Delay,
            };
        }

        byte[]? png = null;
        try { png = _render.RenderCanvasPng(child.Path); }
        catch (Exception ex) { _log.LogDebug(ex, "Canvas {Path} did not decode", child.Path); }

        if (png == null)
        {
            state.Add(new DumpIssueDto
            {
                Path = child.Path,
                Kind = isLink ? "link.dangling" : "canvas.undecodable",
                Reason = isLink
                    ? $"The {child.LinkKind} '{child.LinkText}' did not resolve to anything drawable, so no " +
                      "picture was written."
                    : $"This {child.Width}x{child.Height} {child.Format} canvas did not decode, so no picture " +
                      "was written.",
            });
            return new
            {
                name = child.Name,
                kind = "canvas",
                node = Uri.UnescapeDataString(child.Path),
                file = (string?)null,
                link = isLink ? new { kind = child.LinkKind, text = child.LinkText } : null,
                note = "This canvas did not export.",
            };
        }

        string file = names.For(child.Name, ".png", out DumpIssueDto? issue);
        if (issue != null)
        {
            issue.Path = child.Path;
            state.Add(issue);
        }

        if (!TryWrite(Path.Combine(directory, file), png, child.Path, state))
            file = "";

        if (isLink)
            state.Result.LinksRecorded++;

        state.Result.Canvases++;
        return new
        {
            name = child.Name,
            kind = "canvas",
            node = Uri.UnescapeDataString(child.Path),
            file,
            width = child.Width,
            height = child.Height,
            format = child.Format,
            origin = new { x = child.OriginX, y = child.OriginY },
            delayMs = child.Delay,
            link = isLink ? (object?)new { kind = child.LinkKind, text = child.LinkText } : null,
            resolvedFromLink = isLink,
        };
    }

    private object WriteSound(Child child, string directory, DumpNames names, State state)
    {
        (byte[] Data, string ContentType, string Extension)? audio = null;
        try { audio = _render.GetAudio(child.Path); }
        catch (Exception ex) { _log.LogDebug(ex, "Sound {Path} did not read", child.Path); }

        if (audio == null)
        {
            state.Add(new DumpIssueDto
            {
                Path = child.Path,
                Kind = "sound.empty",
                Reason = "This sound node handed back no bytes, so no audio file was written.",
            });
            return new
            {
                name = child.Name,
                kind = "sound",
                node = Uri.UnescapeDataString(child.Path),
                file = (string?)null,
                note = "This sound did not export.",
            };
        }

        string file = names.For(child.Name, audio.Value.Extension, out DumpIssueDto? issue);
        if (issue != null)
        {
            issue.Path = child.Path;
            state.Add(issue);
        }

        if (!TryWrite(Path.Combine(directory, file), audio.Value.Data, child.Path, state))
            file = "";

        state.Result.Sounds++;
        return new
        {
            name = child.Name,
            kind = "sound",
            node = Uri.UnescapeDataString(child.Path),
            file,
            lengthMs = child.Delay,
        };
    }

    /// <summary>
    /// A UOL. Recorded as the reference it is; its pixels are only written when
    /// following links was asked for AND it lands on something drawable.
    /// </summary>
    private object WriteLink(Child child, string directory, DumpNames names, State state)
    {
        string? file = null;

        if (state.ResolveLinks)
        {
            byte[]? png = null;
            try { png = _render.RenderCanvasPng(child.Path); }
            catch (Exception ex) { _log.LogDebug(ex, "Link {Path} did not resolve to a picture", child.Path); }

            if (png != null)
            {
                file = names.For(child.Name, ".png", out DumpIssueDto? issue);
                if (issue != null)
                {
                    issue.Path = child.Path;
                    state.Add(issue);
                }
                if (TryWrite(Path.Combine(directory, file), png, child.Path, state))
                    state.Result.Canvases++;
                else
                    file = null;
            }
        }

        return new
        {
            name = child.Name,
            kind = "link",
            node = Uri.UnescapeDataString(child.Path),
            link = new { kind = child.LinkKind, text = child.LinkText },
            file,
            resolvedFromLink = file != null,
            note = file == null
                ? "A reference. Its target's contents are wherever the target is, not here."
                : "The picture in this file belongs to the node this link names, not to this node.",
        };
    }

    #endregion

    #region Writing

    private bool TryWrite(string file, byte[] data, string nodePath, State state)
    {
        if (state.Result.Bytes + data.Length > MaxBytes)
        {
            state.Truncated = true;
            state.Result.TruncatedReason =
                $"Stopped at the {MaxBytes / (1024 * 1024 * 1024)} GB limit for one dump.";
            throw new OperationCanceledException();
        }

        try
        {
            File.WriteAllBytes(file, data);
            state.Result.Files++;
            state.Result.Bytes += data.Length;
            return true;
        }
        catch (Exception ex)
        {
            state.Add(new DumpIssueDto
            {
                Path = nodePath,
                Kind = "write.failed",
                Reason = $"Could not write '{file}': {ex.Message}",
            });
            return false;
        }
    }

    private void TryWriteSidecar(string path, string directory, List<object> record, State state)
    {
        try
        {
            string file = Path.Combine(directory, "_node.json");
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, new
            {
                node = Uri.UnescapeDataString(path),
                note =
                    "children[] is in the archive's own order, which a folder listing loses and which WZ does " +
                    "not guarantee to be alphabetical or to start with 'info'. Anything a file cannot carry — " +
                    "a canvas's origin and delay, a link's text, a scalar's value — is here.",
                children = record,
            }, DumpService.Indented);
            state.Result.Files++;
        }
        catch (Exception ex)
        {
            state.Add(new DumpIssueDto
            {
                Path = path,
                Kind = "write.failed",
                Reason = $"Could not write the _node.json sidecar in '{directory}': {ex.Message}",
            });
        }
    }

    private void TryWriteReport(string outputPath, DumpResultDto result)
    {
        try
        {
            using FileStream stream = File.Create(Path.Combine(outputPath, "dump-report.json"));
            JsonSerializer.Serialize(stream, DumpService.Report(result), DumpService.Indented);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write the dump report to {Path}", outputPath);
        }
    }

    /// <summary>
    /// Gives back the memory of an image this dump parsed.
    ///
    /// Without it a whole-archive dump holds every image it has ever touched:
    /// the auditor measured 89,587 images and 3.3 M canvases in one client, and
    /// parsing them all at once is how earlier passes died. The resolution cache
    /// is dropped in the same gate hold, because a memoised path pointing into a
    /// property collection that has just been cleared is a node that exists and
    /// is not in the tree.
    /// </summary>
    private void Release(string path)
    {
        try
        {
            lock (_session.Gate)
            {
                if (_session.TryResolve(path) is WzImage image && image.Parsed && !image.Changed)
                {
                    _session.InvalidateResolution();
                    image.UnparseImage();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not release {Path} after dumping it", path);
        }
    }

    private void Progress(string current, State state)
    {
        lock (_gate)
        {
            _current.Current = Uri.UnescapeDataString(current);
            _current.Done = state.Result.Files;
            _current.Stage = $"{state.Result.Canvases} picture(s), {state.Result.Sounds} sound(s)";
            _current.Seconds = state.Clock.Elapsed.TotalSeconds;
        }
    }

    #endregion

    private enum ChildKind { Value, Container, Canvas, Sound, Link }

    /// <summary>One child, copied out of the tree so it can outlive the gate.</summary>
    private sealed class Child
    {
        public string Name = "";
        public string Path = "";
        public string TypeName = "";
        public ChildKind Kind;
        public string? Value;
        public int Width;
        public int Height;
        public string? Format;
        public int OriginX;
        public int OriginY;
        public int Delay;
        public string? Extension;
        public string? LinkKind;
        public string? LinkText;
        public bool ReleaseImage;
    }

    /// <summary>Per-run state. Not shared: one dump at a time, one of these.</summary>
    private sealed class State
    {
        public required DumpResultDto Result { get; init; }
        public required int MaxNodes { get; init; }
        public required bool PngOnly { get; init; }
        public required bool ResolveLinks { get; init; }
        public required CancellationToken Token { get; init; }
        public required Stopwatch Clock { get; init; }
        public bool Truncated;

        /// <summary>
        /// Issues are capped, because a client with a systematic defect produces
        /// one per node and a 300 MB report helps nobody. The COUNT is not capped
        /// — <see cref="DumpResultDto.TruncatedReason"/> and the totals still say
        /// how many there were.
        /// </summary>
        private const int MaxIssues = 2000;
        private int _issues;

        public void Add(DumpIssueDto issue)
        {
            _issues++;
            if (_issues <= MaxIssues)
            {
                Result.Issues.Add(issue);
            }
            else if (_issues == MaxIssues + 1)
            {
                Result.Issues.Add(new DumpIssueDto
                {
                    Path = "",
                    Kind = "budget.exhausted",
                    Reason = $"More than {MaxIssues} things did not export; the rest are not listed " +
                             "individually. This is a property of the client, not of the dump.",
                });
            }
        }
    }
}
