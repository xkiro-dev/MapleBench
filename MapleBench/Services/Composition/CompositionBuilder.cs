using System.Diagnostics;
using MapleBench.Models;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MapleBench.Services.Composition;

/// <summary>What a build was asked to do.</summary>
public sealed class CompositionBuildRequest
{
    /// <summary>The manifest file. Either this or <see cref="Manifest"/>.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>The manifest itself, for a caller that already has one.</summary>
    public CompositionManifest? Manifest { get; set; }

    /// <summary>
    /// Where the composed client is written.
    ///
    /// Not in the manifest, deliberately. Where a composition <em>is</em> on this
    /// machine is not part of what the composition <em>is</em>, and a manifest
    /// that carried an output path would be a document you could not send to
    /// anyone.
    /// </summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>
    /// Compute a content hash for every node that lands. Off by default: it is a
    /// second full parse of everything that moved.
    /// </summary>
    public bool HashParts { get; set; }

    /// <summary>
    /// Stop and write nothing when a take is refused, rather than producing a
    /// client missing one of its contributions.
    ///
    /// On, and it should stay on. The alternative is a fourth outcome — "kept
    /// going with less" — and a composed client that is silently missing a
    /// contribution is indistinguishable from one that has it until you are in
    /// game. Turning it off is for someone who wants to see how far a build gets;
    /// the ledger then says which takes wrote nothing, at the top.
    /// </summary>
    public bool StopOnRefusal { get; set; } = true;

    /// <summary>
    /// Called as the build moves between phases, so a caller can show what a
    /// multi-minute synchronous job is doing instead of silence. Never called
    /// with fabricated percentages: the counts are of takes and archives, which
    /// are the units the build actually knows.
    /// </summary>
    public Action<CompositionBuildStep>? Progress { get; set; }

    /// <summary>
    /// Checked between steps — before each base copy, each take, each save and
    /// each verification. Cancelling mid-build discards the half-built output
    /// (a folder of archives that is neither the base nor the composition must
    /// not be left where someone could run a client out of it) and surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public CancellationToken Cancellation { get; set; } = CancellationToken.None;
}

/// <summary>Where a build is, in the units it actually counts.</summary>
public sealed record CompositionBuildStep(string Phase, string? Detail, int TakesDone, int TakesTotal);

/// <summary>The outcome of a build. Three states, and never a fourth.</summary>
public enum CompositionBuildOutcome
{
    /// <summary>Every take applied and the reopened archives hold what the ledger claims.</summary>
    Complete,

    /// <summary>Nothing was written, and <see cref="CompositionBuildResult.Refusal"/> says why.</summary>
    Refused,

    /// <summary>
    /// Output was written with a take missing, because the caller turned
    /// <see cref="CompositionBuildRequest.StopOnRefusal"/> off and asked to see
    /// how far it got. Never reachable by default.
    /// </summary>
    Partial,
}

public sealed class CompositionBuildResult
{
    public CompositionBuildOutcome Outcome { get; set; } = CompositionBuildOutcome.Refused;

    /// <summary>Why nothing was written, for <see cref="CompositionBuildOutcome.Refused"/>.</summary>
    public string? Refusal { get; set; }

    public string OutputFolder { get; set; } = "";

    /// <summary>The record of the build, also written beside the output.</summary>
    public CompositionLedger Ledger { get; set; } = new();

    /// <summary>
    /// The build's own identity: the digest of the ledger with the timings
    /// removed. Two builds of the same manifest from the same inputs must agree
    /// here, and the archives must be byte-identical. See
    /// <see cref="CompositionLedger.Digest"/>.
    /// </summary>
    public string Digest { get; set; } = "";

    public double Seconds { get; set; }
}

/// <summary>
/// Builds a composed client from a pristine base and a manifest.
///
/// <code>
///     pristine base (never opened for writing) + composition.json  ->  output client
/// </code>
///
/// The reframe this type exists to enforce: <b>a composition is a build, not a
/// sequence of edits.</b> Re-running rebuilds from the base rather than layering
/// on top of the last result, so "this Skill.wz holds three stacked ports and
/// nothing can tell them apart" becomes impossible by construction instead of by
/// discipline. Removing a contribution is deleting a take and building again.
///
/// Four properties carry the weight:
///
/// <list type="number">
/// <item><b>The base is copied, never edited.</b> Every archive of the output
/// starts life as a byte copy of the base's, and only the ones a take actually
/// wrote into are re-serialised. An archive nothing touched is still byte-equal
/// to the base afterwards, which is both the cheap path and the honest one.</item>
/// <item><b>Inputs are pinned by content.</b> Sources are identified by the
/// SHA-256 of their archives, not by where they sit, because clients get moved
/// and renamed and a manifest has to survive that. A pinned archive whose bytes
/// have changed refuses the build by name.</item>
/// <item><b>The claim is checked on the reopened files.</b> Every landing path
/// the ledger records is looked for in the saved archive read back off disk, not
/// in the tree that wrote it — the tree is what the port believed, and this
/// project has already shipped a `written 5, failed 0` for parts that were not
/// in the file.</item>
/// <item><b>Same inputs, same bytes.</b> Nothing here reads a clock, a random
/// number or a dictionary order into what gets written. The timings live in the
/// ledger, and <see cref="CompositionLedger.Digest"/> leaves them out, so two
/// builds are comparable as a single pair of strings.</item>
/// </list>
///
/// It runs on its own session. The archives a build opens are its own business
/// and must not appear in, or be affected by, whatever the user has open in the
/// app.
/// </summary>
public sealed class CompositionBuilder
{
    private readonly ILoggerFactory _loggers;

    public CompositionBuilder() : this(NullLoggerFactory.Instance)
    {
    }

    public CompositionBuilder(ILoggerFactory loggers)
    {
        _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
    }

    public CompositionBuildResult Build(CompositionBuildRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        Stopwatch clock = Stopwatch.StartNew();

        CompositionManifest manifest =
            request.Manifest
            ?? (request.ManifestPath is { Length: > 0 } path
                ? CompositionManifest.FromJson(File.ReadAllText(path))
                : throw new InvalidOperationException(
                    "A build needs a manifest: give it a file to read or a manifest to build."));

        string output = Path.GetFullPath(
            request.OutputFolder.Length > 0
                ? request.OutputFolder
                : throw new InvalidOperationException(
                    "A build needs an output folder. It is not in the manifest on purpose — where a "
                    + "composition lives on this machine is not part of what the composition is."));

        CompositionLedger ledger = new()
        {
            Name = manifest.Name,
            Output = output,
            Origin = "build",
            Base = manifest.Base,
            When = CompositionLedgerStore.Now(),
        };

        Say(request, "checking inputs", null, 0, manifest.Takes.Count);

        string? refusal = Refuse(manifest, output, ledger);
        if (refusal != null)
        {
            return new CompositionBuildResult
            {
                Outcome = CompositionBuildOutcome.Refused,
                Refusal = refusal,
                OutputFolder = output,
                Ledger = ledger,
                Digest = ledger.Digest(),
                Seconds = clock.Elapsed.TotalSeconds,
            };
        }

        // The sessions a build opens install MapleLib's static outlink hook on
        // themselves; captured here and restored on the way out, or the hook is
        // left pointing at a disposed session and every '_outlink' in the app
        // quietly renders a 1x1 placeholder until restart — the resolves-in-an-
        // editor-draws-nothing class, self-inflicted.
        Func<string, WzImage>? resolver = WzCanvasProperty.ExternalImageResolver;

        CompositionBuildResult result;
        try
        {
            Directory.CreateDirectory(output);
            foreach (CompositionArchive archive in manifest.Base.Archives)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                Say(request, "copying the base", archive.Name, 0, manifest.Takes.Count);
                File.Copy(
                    Path.Combine(manifest.Base.Folder, archive.Name),
                    Path.Combine(output, archive.Name),
                    overwrite: true);
            }

            result = Run(manifest, output, request, ledger);
        }
        catch
        {
            // A build that threw part-way — including a cancelled one — has
            // produced a folder of archives that is neither the base nor the
            // composition, and leaving it in place invites someone to run a
            // client out of it. The base is untouched, so there is nothing to
            // lose by clearing it.
            Discard(output, manifest);
            throw;
        }
        finally
        {
            WzCanvasProperty.ExternalImageResolver = resolver;
        }

        result.Seconds = clock.Elapsed.TotalSeconds;
        result.Ledger.Seconds = result.Seconds;

        if (result.Outcome == CompositionBuildOutcome.Refused)
            Discard(output, manifest);
        else
            CompositionLedgerStore.Write(output, result.Ledger);

        return result;
    }

    /// <summary>
    /// Everything that makes a build impossible, checked before a byte is
    /// copied. All of it is said at once rather than one failure per run.
    /// </summary>
    private static string? Refuse(CompositionManifest manifest, string output, CompositionLedger ledger)
    {
        List<string> problems = new();

        if (manifest.Base.Archives.Count == 0)
            problems.Add("The manifest's base lists no archives, so there is nothing to build from.");

        if (manifest.Base.Folder.Length == 0 || !Directory.Exists(manifest.Base.Folder))
        {
            problems.Add(
                $"The base client folder '{manifest.Base.Folder}' is not there. The archives are pinned by "
                + "content, so if you have moved the client, point the manifest at the new folder and the "
                + "hashes will confirm it is the same one.");
        }

        // The output must not be an input. Building on top of the base would
        // destroy the one thing the whole design rests on — a pristine client
        // that is never mutated — and it would do it silently, since the copy
        // step would simply copy each file onto itself.
        foreach (CompositionSource input in Inputs(manifest))
        {
            if (input.Folder.Length > 0 && Same(Path.GetFullPath(input.Folder), output))
            {
                problems.Add(
                    $"The output folder is '{input.Label}' itself. A build never writes into a client it "
                    + "reads from: the base has to stay pristine, or the second build is composing on top "
                    + "of the first.");
            }
        }

        problems.AddRange(Unpinned(manifest, ledger));

        if (problems.Count == 0 && !EmptyOrOurs(output, manifest))
        {
            problems.Add(
                $"'{output}' already holds files this build did not put there. A build only ever writes "
                + $"into an empty folder or one carrying its own {CompositionSchema.LedgerFileName}, "
                + "because overwriting archives whose provenance it does not know is how a client gets "
                + "destroyed by a typo.");
        }

        foreach (CompositionTake take in manifest.Takes)
        {
            if (manifest.Source(take.From) == null)
            {
                problems.Add(
                    $"A take says it comes from '{take.From}', which is not one of the manifest's sources.");
            }
            if (manifest.Base.Archive(take.Into) == null)
            {
                problems.Add(
                    $"A take lands in '{take.Into}', which the base does not list. Add it to the base's "
                    + "archives so the output has one to write into.");
            }
        }

        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    /// <summary>
    /// Checks every pinned hash and reports the archives that carry none.
    ///
    /// A mismatch is a refusal; an absent pin is a note. They are different
    /// facts: the first says this is not the client the manifest was written
    /// against, the second says nobody ever said which client it was. Recording
    /// the second in the ledger's notes is what stops an unverified build from
    /// passing itself off as a reproducible one.
    /// </summary>
    private static IEnumerable<string> Unpinned(CompositionManifest manifest, CompositionLedger ledger)
    {
        List<string> refusals = new();
        int unpinned = 0;

        foreach (CompositionSource input in Inputs(manifest))
        {
            foreach (CompositionArchive archive in input.Archives)
            {
                string file = Path.Combine(input.Folder, archive.Name);
                if (!File.Exists(file))
                {
                    refusals.Add($"{input.Label} has no {archive.Name} at '{file}'.");
                    continue;
                }

                if (archive.Sha256 is not { Length: > 0 } pinned)
                {
                    unpinned++;
                    continue;
                }

                string actual = CompositionLedgerStore.HashFile(file);
                if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    refusals.Add(
                        $"{input.Label}'s {archive.Name} is not the file this manifest was written "
                        + $"against — pinned {pinned[..12]}…, found {actual[..12]}…. A composition built "
                        + "from different inputs is a different composition.");
                }
            }
        }

        if (unpinned > 0)
        {
            ledger.Notes.Add(
                $"{unpinned} input archive{(unpinned == 1 ? " was" : "s were")} not pinned to a content "
                + "hash, so this build verified the rest and took those on trust. Pin them and the next "
                + "build can prove it read the same bytes.");
        }

        return refusals;
    }

    private static IEnumerable<CompositionSource> Inputs(CompositionManifest manifest)
    {
        yield return manifest.Base;
        foreach (CompositionSource source in manifest.Sources)
            yield return source;
    }

    private static bool Same(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the output folder is empty, absent, or one this tool built —
    /// which is the only folder a build is allowed to overwrite.
    /// </summary>
    private static bool EmptyOrOurs(string output, CompositionManifest manifest)
    {
        if (!Directory.Exists(output))
            return true;

        string[] files = Directory.GetFiles(output);
        if (files.Length == 0)
            return true;

        if (!File.Exists(CompositionLedgerStore.PathFor(output)))
            return false;

        // A folder we built before. It may hold only its ledger and archives the
        // base names; anything else is somebody's own work and is not ours to
        // overwrite.
        HashSet<string> ours = new(StringComparer.OrdinalIgnoreCase) { CompositionSchema.LedgerFileName };
        foreach (CompositionArchive archive in manifest.Base.Archives)
            ours.Add(archive.Name);

        return files.All(f => ours.Contains(Path.GetFileName(f)));
    }

    private static void Say(
        CompositionBuildRequest request, string phase, string? detail, int takesDone, int takesTotal)
        => request.Progress?.Invoke(new CompositionBuildStep(phase, detail, takesDone, takesTotal));

    private static void Discard(string output, CompositionManifest manifest)
    {
        if (!Directory.Exists(output))
            return;

        foreach (CompositionArchive archive in manifest.Base.Archives)
        {
            string file = Path.Combine(output, archive.Name);
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { /* a leaked half-build must not mask the real failure */ }
        }
    }

    private CompositionBuildResult Run(
        CompositionManifest manifest,
        string output,
        CompositionBuildRequest request,
        CompositionLedger ledger)
    {
        using WzSessionService session = new(_loggers.CreateLogger<WzSessionService>());
        UndoService undo = new();
        WzEditService edit = new(session, undo);
        StringPoolService strings = new(session, _loggers.CreateLogger<StringPoolService>());
        PortService port = new(session, edit, strings, undo, _loggers.CreateLogger<PortService>());
        WzSaveService saves = new(session, undo, _loggers.CreateLogger<WzSaveService>());
        CompositionLedgerStore store = new(session);

        // The output first, so that when two clients hold an archive of the same
        // name the one being written is the one already in the session.
        Dictionary<string, OpenFile> outputs = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompositionArchive archive in manifest.Base.Archives)
            outputs[archive.Name] = Open(session, Path.Combine(output, archive.Name), archive, readOnly: false);

        Dictionary<string, Dictionary<string, OpenFile>> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompositionSource source in manifest.Sources)
        {
            Dictionary<string, OpenFile> opened = new(StringComparer.OrdinalIgnoreCase);
            foreach (CompositionArchive archive in source.Archives)
            {
                opened[archive.Name] =
                    Open(session, Path.Combine(source.Folder, archive.Name), archive, readOnly: true);
            }
            sources[source.Id] = opened;
        }

        List<string> refusedTakes = new();

        for (int i = 0; i < manifest.Takes.Count; i++)
        {
            request.Cancellation.ThrowIfCancellationRequested();

            CompositionTake take = manifest.Takes[i];
            CompositionSource source = manifest.Source(take.From)!;
            Dictionary<string, OpenFile> opened = sources[take.From];

            Say(request, "applying takes",
                $"take {i + 1} of {manifest.Takes.Count}: {take.Kind} from {source.Label} into {take.Into}",
                i, manifest.Takes.Count);

            PortApplyRequest apply = new()
            {
                Kind = take.Kind,
                Scope = take.Scope,
                TargetFileId = outputs[take.Into].Id,
                FollowLinks = take.Options.FollowLinks,
                IncludeArtOutlinks = take.Options.IncludeArtOutlinks,
                Overwrite = take.Options.Overwrite,
                Match = take.Options.Match,
                AcceptDeadCanvasLinks = take.Options.AcceptDeadCanvasLinks,
                AcceptMissingNames = take.Options.AcceptMissingNames,

                // A build never comes back to ask. The manifest is the answer to
                // the conflict question, and it was given before the build
                // started: Overwrite says what happens to what the target already
                // holds, and every part that is skipped for want of it is written
                // into the ledger as a refusal with its reason.
                StopOnConflict = false,
                Confirmed = true,

                Paths = take.Take.Select(t => Session(opened, t)).ToList(),
                SourceFileId = take.FromArchive is { Length: > 0 } from && opened.TryGetValue(from, out OpenFile? f)
                    ? f.Id
                    : null,
            };

            PortResultDto applied = port.Apply(apply);

            // Mandatory after any write. The hasher memoises per node object, and
            // a port replaces nodes, so a stale digest would make the next
            // identity question answer for content that is no longer there.
            WzContentHasher.ClearCache();

            LedgerTake record = store.Record(Pin(source), take, applied, request.HashParts);
            record.Sequence = i + 1;
            ledger.Takes.Add(record);

            if (applied.Plan.Blocked)
            {
                refusedTakes.Add(
                    $"Take {i + 1} ({take.Kind} from {source.Label} into {take.Into}) was refused: "
                    + applied.Plan.BlockedReason);

                if (request.StopOnRefusal)
                {
                    return new CompositionBuildResult
                    {
                        Outcome = CompositionBuildOutcome.Refused,
                        Refusal = string.Join("\n", refusedTakes)
                                  + "\n\nNothing was written. A composed client missing one of its "
                                  + "contributions looks exactly like one that has it, so this is a "
                                  + "complete build or none.",
                        OutputFolder = output,
                        Ledger = ledger,
                        Digest = ledger.Digest(),
                    };
                }
            }
        }

        /* ---------------- save, then verify what was saved ---------------- */

        List<string> written = new();
        foreach (CompositionArchive archive in manifest.Base.Archives)
        {
            OpenFile file = outputs[archive.Name];
            if (!file.Dirty && file.CountDirtyImages() == 0)
                continue;

            request.Cancellation.ThrowIfCancellationRequested();
            Say(request, "saving", archive.Name, manifest.Takes.Count, manifest.Takes.Count);

            saves.Save(new SaveRequest
            {
                FileId = file.Id,
                TargetPath = null,

                // No .bak. The output folder is produced from the base every
                // time, so the previous build is not something to preserve — and
                // a stray .bak would be a file the next build refuses to
                // overwrite because it cannot tell whose it is.
                Backup = false,
            });
            written.Add(archive.Name);
        }

        WzContentHasher.ClearCache();

        // Every archive released before anything is read back. The verification
        // has to see the file, not the writer's memory of it.
        foreach (OpenFile file in outputs.Values.ToList())
            session.Close(file.Id);

        Say(request, "verifying the saved files", null, manifest.Takes.Count, manifest.Takes.Count);
        ledger.Verification = Verify(manifest, output, ledger, request);

        if (written.Count == 0)
        {
            ledger.Notes.Add(
                "No archive was changed, so the output is a byte copy of the base. That is a real "
                + "result — every take found what it wanted already there — and not a failed build.");
        }

        foreach (string refused in refusedTakes)
            ledger.Notes.Insert(0, refused);

        return new CompositionBuildResult
        {
            Outcome = refusedTakes.Count == 0
                ? CompositionBuildOutcome.Complete
                : CompositionBuildOutcome.Partial,
            OutputFolder = output,
            Ledger = ledger,
            Digest = ledger.Digest(),
        };
    }

    /// <summary>
    /// The source as the ledger records it: the same handle and label, with every
    /// archive's hash filled in whether the manifest pinned it or not.
    ///
    /// The ledger always pins. A manifest may be written by hand and left
    /// unpinned; a record of what was actually read may not be, or it could not
    /// answer the question it exists for.
    /// </summary>
    private static CompositionSource Pin(CompositionSource source)
    {
        CompositionSource pinned = new()
        {
            Id = source.Id,
            Label = source.Label,
            Folder = source.Folder,
        };

        foreach (CompositionArchive archive in source.Archives)
        {
            string file = Path.Combine(source.Folder, archive.Name);
            pinned.Archives.Add(new CompositionArchive(
                archive.Name,
                File.Exists(file) ? CompositionLedgerStore.HashFile(file) : null)
            {
                MapleVersion = archive.MapleVersion,
                GameVersion = archive.GameVersion,
            });
        }

        return pinned;
    }

    /// <summary>
    /// Reopens the saved archives and looks for every path the ledger claims
    /// something landed at.
    ///
    /// On the file, never on the tree that wrote it. That is the working rule
    /// this project paid the most for: a port once reported five parts written
    /// that were not in the saved archive, because the count came from the plan
    /// and the plan came from intentions.
    /// </summary>
    private LedgerVerification Verify(
        CompositionManifest manifest, string output, CompositionLedger ledger, CompositionBuildRequest request)
    {
        LedgerVerification verification = new() { Ran = true };

        // Grouped by archive so each file is opened once, and so an archive
        // nothing landed in is still reported with its hash and a checked count
        // of zero — which is a different statement from "not looked at".
        Dictionary<string, List<string>> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompositionArchive archive in manifest.Base.Archives)
            expected[archive.Name] = new List<string>();

        foreach (LedgerTake take in ledger.Takes)
        {
            foreach (LedgerPart part in take.Took)
            {
                if (part.To is not { Length: > 0 } landing)
                    continue;

                int slash = landing.IndexOf('/');
                if (slash <= 0)
                    continue;

                string archive = landing[..slash];
                if (expected.TryGetValue(archive, out List<string>? paths))
                    paths.Add(landing[(slash + 1)..]);
            }
        }

        using WzSessionService reader = new(_loggers.CreateLogger<WzSessionService>());

        foreach (CompositionArchive archive in manifest.Base.Archives)
        {
            Say(request, "verifying the saved files", archive.Name,
                manifest.Takes.Count, manifest.Takes.Count);

            string file = Path.Combine(output, archive.Name);
            LedgerVerifiedArchive record = new()
            {
                Name = archive.Name,
                Sha256 = CompositionLedgerStore.HashFile(file),
                Bytes = new FileInfo(file).Length,
            };

            OpenFile reopened = Open(reader, file, archive, readOnly: true);
            List<string> paths = expected[archive.Name];

            // Distinct, ordered, so the count is a count of landings and not of
            // how many times the plan happened to mention one.
            foreach (string relative in paths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                record.Checked++;
                if (reader.TryResolve(reopened.Id + "/" + relative) == null)
                    record.Missing.Add(archive.Name + "/" + relative);
            }

            verification.Archives.Add(record);
        }

        return verification;
    }

    private static OpenFile Open(
        WzSessionService session, string path, CompositionArchive archive, bool readOnly)
    {
        OpenFile file = session.Open(new OpenRequest
        {
            Path = path,
            MapleVersion = archive.MapleVersion,
            GameVersion = (short)archive.GameVersion,
        });
        file.ReadOnly = readOnly;
        return file;
    }

    /// <summary>
    /// An archive-relative path from the manifest — <c>Mob.wz/8800100.img</c> —
    /// as the session path the port takes.
    ///
    /// Each segment goes through <see cref="WzPath.Child"/>, which is what makes
    /// a name holding a slash or a hash survive the trip. The manifest is written
    /// in plain names, because it is a document for a person.
    /// </summary>
    private static string Session(IReadOnlyDictionary<string, OpenFile> opened, string relative)
    {
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidOperationException("A take lists an empty path.");

        if (!opened.TryGetValue(segments[0], out OpenFile? file))
        {
            throw new InvalidOperationException(
                $"'{relative}' names the archive '{segments[0]}', which this take's source does not list. "
                + "Add it to that source's archives in the manifest.");
        }

        string path = file.Id;
        for (int i = 1; i < segments.Length; i++)
            path = WzPath.Child(path, segments[i]);
        return path;
    }
}
