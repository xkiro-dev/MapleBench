using System.Diagnostics;
using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/* ============================================================================
   THE CLIENT INTEGRITY AUDITOR

   What it is for: every failure this project has spent hours on had the same
   shape -- the archive opens, the tree looks right, the editor is happy, and
   the game disagrees. The archive is not the client. The client is a *family*
   of archives mounted together, with links that cross between them, names that
   have to match names in a different archive, and a pixel decoder with a fixed
   vocabulary of formats. Every one of those is checkable on disk, before a
   client is ever launched, and none of it is checkable by looking at one node.

   Deliberately NOT a port, a save, or an edit. It opens files read-only
   (WzFile.ParseMainWzDirectory uses FileAccess.Read/FileShare.Read), writes
   nothing, and reaches no decoder: canvases are inspected through their
   header numbers and their compressed blob length, never through GetBitmap.
   An audit that could itself corrupt a client, or that needed 40 GB of
   decoded bitmaps to run, would not be run.

   Two passes, because one pass cannot answer the interesting questions:

     Pass 1 walks each archive alone. Everything local is decided here and
     then thrown away -- an image is parsed, checked, and unparsed before the
     next one, so peak memory is one image plus the directory skeletons.
     Local means: the canvas header vocabulary, zero-length blobs, _inlink
     (image-root-relative, never crosses an image), and UOLs. It also records
     the small facts the second pass needs: image names with their on-disk
     fingerprint, the id sets each archive defines, and every _outlink target
     that was seen.

     Pass 2 answers what needs the whole client at once: does an _outlink land
     on something, does a String.wz row have a data node behind it, does an
     action name exist in Character.wz, and -- the one that cost the most --
     does the same image name exist twice in one family with different bytes.

   The rule the whole thing is written to: report what is measured, and say
   plainly what was not checked. A finding that might be fine is worth less
   than nothing, because the next one that matters gets skipped with it.
   ============================================================================ */

/// <summary>Severity, worst first. The UI sorts on this and nothing else.</summary>
public enum AuditSeverity
{
    /// <summary>The client cannot be right. A crash, a missing archive, an undecodable canvas.</summary>
    Critical = 0,
    /// <summary>Something is definitely broken but survivable — a link that draws nothing.</summary>
    Error = 1,
    /// <summary>Suspicious and worth a look; a rule the client happens to tolerate.</summary>
    Warning = 2,
    /// <summary>Measured fact, no judgement.</summary>
    Info = 3,
}

/// <summary>One thing found, at one place, with the evidence that found it.</summary>
/// <param name="Check">Stable id — "outlink.unresolved". The UI groups on this.</param>
/// <param name="Path">Where it is, as a client path: "Skill.wz/40000.img/skill/40001000/effect/0".</param>
/// <param name="Detail">One sentence, in the terms the user is thinking in.</param>
/// <param name="Target">The thing referred to, when the finding is about a reference.</param>
public sealed record AuditFinding(
    string Check,
    AuditSeverity Severity,
    string Path,
    string Detail,
    string? Target = null);

/// <summary>A check that ran, what it looked at, and how many times it fired.</summary>
public sealed record AuditCheck(
    string Id,
    string Title,
    AuditSeverity Severity,
    long Examined,
    long Found,
    bool Truncated,
    string? NotChecked = null);

/// <summary>A .wz in the folder that the run did not audit, and why not.</summary>
public sealed record AuditSkipped(string Name, long Bytes, string Why);

/// <summary>One archive as the auditor read it.</summary>
public sealed record AuditArchive(
    string Name,
    string Family,
    int MountOrder,
    long Bytes,
    string ParseStatus,
    string Encryption,
    short GameVersion,
    int Images,
    long Canvases,
    double Seconds);

/// <summary>
/// One String.wz image against the archives that define what it names, as
/// counts rather than as a list of rows.
///
/// This exists because the two String checks are the loudest in the report by
/// an order of magnitude — 39,902 and 3,682 on this client — and a per-check cap
/// of 300 rows means the raw findings cannot be counted, cannot be grouped, and
/// cannot answer the only question anyone asks of them: which kind is it, and is
/// it one-sided. Counts survive the cap; rows do not.
/// </summary>
/// <param name="Compared">False when the data archive was absent, in which case the counts are zero because nothing was compared — not because nothing was wrong.</param>
public sealed record AuditStringCoverage(
    string Kind,
    string StringImage,
    bool Compared,
    string? Why,
    int Named,
    int Defined,
    int Orphans,
    int Unnamed);

public sealed record AuditReport(
    string Folder,
    DateTimeOffset StartedUtc,
    double Seconds,
    IReadOnlyList<AuditArchive> Archives,
    IReadOnlyList<AuditCheck> Checks,
    IReadOnlyList<AuditFinding> Findings,
    IReadOnlyList<AuditStringCoverage> StringCoverage,
    IReadOnlyList<AuditSkipped> Skipped,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> NotChecked);

public sealed class AuditOptions
{
    /// <summary>The client folder. Every *.wz directly in it that looks mounted.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Archive families to include; empty means all of them.</summary>
    public string[]? Families { get; set; }

    /// <summary>Per check, how many findings to keep. The count is always complete.</summary>
    public int MaxPerCheck { get; set; } = 400;

    /// <summary>Stop after this many images per archive. 0 = no limit. For a smoke run.</summary>
    public int MaxImagesPerArchive { get; set; }

    /// <summary>
    /// Encryption, when the caller already knows it — "BMS", "GMS", "EMS".
    /// Empty means detect. Worth offering because detection is a guess made by
    /// parsing with each key and scoring how printable the names come out, and
    /// on a client whose owner knows the answer a wrong guess costs a whole run.
    /// </summary>
    public string? MapleVersion { get; set; }

    /// <summary>Patch version, when known. 0 means detect it with the encryption.</summary>
    public short GameVersion { get; set; }
}

/// <summary>Live progress for a run that takes minutes.</summary>
public sealed class AuditProgress
{
    public string State { get; set; } = "idle";     // idle | running | done | failed | cancelled
    public string Phase { get; set; } = "";
    public string Archive { get; set; } = "";
    public int ArchivesDone { get; set; }
    public int ArchivesTotal { get; set; }
    public long ImagesDone { get; set; }
    public long Findings { get; set; }
    public string? Error { get; set; }
    public double Seconds { get; set; }
}

public sealed class IntegrityAuditService
{
    /* ---- the client's own canvas vocabulary -----------------------------
       Not "the formats MapleLib can decode" and not "the formats that exist":
       the nine WzPngFormat knows, which is the list every decoder in this
       repo switches on. A tenth value is not a rarity, it is a canvas the
       client will draw as garbage or refuse — WzPngProperty's own comment
       records a 32x32/512-byte canvas whose unknown format had GetDecodedSize
       predict 4096 and pad 3584 zero bytes into a re-saved archive. */
    private static readonly HashSet<int> KnownFormats =
        new(Enum.GetValues<WzPngFormat>().Select(f => (int)f));

    /* String properties whose value names something that lives somewhere else.
       Collected by name so the report can show the real vocabulary rather than
       a guess about it. Resolution rules are attached per key in pass 2, and a
       key with no rule is reported as collected-but-not-resolved. */
    private static readonly string[] ReferenceKeys = { "action", "sound", "sfx" };

    private readonly object _gate = new();
    private AuditProgress _progress = new();
    private AuditReport? _report;
    private CancellationTokenSource? _cancel;

    public AuditProgress Snapshot()
    {
        lock (_gate) return Clone(_progress);
    }

    public AuditReport? Report()
    {
        lock (_gate) return _report;
    }

    public void Cancel()
    {
        lock (_gate) _cancel?.Cancel();
    }

    /// <summary>What a run would look at, answered without opening anything.</summary>
    public object Plan(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"No folder at {folder}.");

        List<string> assumptions = new();
        List<(string Path, string Family, string Stem, int Order)> mounted =
            Discover(new AuditOptions { Folder = folder }, assumptions, out List<AuditSkipped> skipped, out _);

        return new
        {
            folder,
            // Grouped without re-sorting: the sequence Discover handed back is the
            // order the client mounts them in, and re-alphabetising it here would
            // quietly throw away the thing that was just read off disk.
            families = mounted.GroupBy(m => m.Family, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    family = g.Key,
                    archives = g.OrderBy(m => m.Order)
                        .Select(m => new { name = m.Stem + ".wz", mountOrder = m.Order, bytes = new FileInfo(m.Path).Length }),
                }),
            skipped = skipped.Select(f => new { name = f.Name, bytes = f.Bytes, why = f.Why }),
            assumptions,
        };
    }

    /// <summary>
    /// Runs an audit. One at a time: two of these on a 24 GB client would each
    /// halve the other's disk and neither would finish sooner.
    /// </summary>
    public AuditReport Run(AuditOptions options)
    {
        CancellationTokenSource cancel = new();
        lock (_gate)
        {
            if (_progress.State == "running")
                throw new InvalidOperationException("An audit is already running.");
            _cancel = cancel;
            _progress = new AuditProgress { State = "running", Phase = "opening" };
            _report = null;
        }

        try
        {
            AuditReport report = Execute(options, cancel.Token);
            lock (_gate)
            {
                _report = report;
                _progress.State = "done";
                _progress.Seconds = report.Seconds;
                _progress.Findings = report.Findings.Count;
            }
            return report;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _progress.State = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _progress.State = "failed";
                _progress.Error = ex.Message;
            }
            throw;
        }
        finally
        {
            lock (_gate) _cancel = null;
        }
    }

    /* ====================================================================
       THE RUN
       ==================================================================== */

    private sealed class Collector
    {
        public readonly Dictionary<string, List<AuditFinding>> Kept = new();
        public readonly Dictionary<string, long> Counts = new();
        public readonly Dictionary<string, long> Examined = new();
        public int Max = 400;

        public void Examine(string check, long n = 1)
        {
            Examined[check] = Examined.GetValueOrDefault(check) + n;
        }

        public void Add(AuditFinding finding)
        {
            Counts[finding.Check] = Counts.GetValueOrDefault(finding.Check) + 1;
            List<AuditFinding> kept = Kept.TryGetValue(finding.Check, out List<AuditFinding>? list)
                ? list
                : Kept[finding.Check] = new List<AuditFinding>();
            if (kept.Count < Max) kept.Add(finding);
        }

        public long Total => Counts.Values.Sum();
    }

    /// <summary>
    /// A string that names something living somewhere else, together with the
    /// archive it was written in.
    ///
    /// The archive is the part that was missing, and its absence produced the
    /// worst finding this tool has shipped: 1,046 "unknown action" rows, nearly
    /// all of them about strings that were never animation names at all. A key
    /// name alone does not say what a value means — <c>action</c> means a
    /// character animation in Skill.wz, a server script in Reactor.wz and a
    /// frame index in Item.wz — so a check that matches on the key and not on
    /// where it is written cannot be right.
    /// </summary>
    private sealed record NamedReference(string Family, string Archive, string Value, string Path);

    /// <summary>
    /// An image as the directory describes it, without parsing it.
    ///
    /// <paramref name="Family"/> is carried rather than derived from the stem.
    /// It is decided once, in <see cref="Discover"/>, against the client's own
    /// manifest — and "Map2 belongs to Map" is exactly the kind of guess that
    /// looks harmless in a helper and turns into a shadowing report about two
    /// archives the client never mounts together.
    /// </summary>
    private sealed record ImageEntry(string Archive, string Family, int MountOrder, string Path,
        int BlockSize, int Checksum);

    /// <summary>
    /// Every canvas that carried one particular _outlink value, as a few example
    /// paths and a count.
    ///
    /// Not a list of every canvas. A v232 Character.wz has millions of linked
    /// canvases sharing far fewer link values, and keeping one object per canvas
    /// took a measured run past 5 GB resident on the second archive of
    /// twenty-four. Four examples name the problem as well as four million do,
    /// and the count is the number the user actually reads.
    /// </summary>
    private sealed class OutlinkRefs
    {
        public readonly List<string> Examples = new();
        public int Count;

        public void Saw(string source)
        {
            Count++;
            if (Examples.Count < 4) Examples.Add(source);
        }
    }

    private AuditReport Execute(AuditOptions options, CancellationToken token)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        Collector found = new() { Max = Math.Max(1, options.MaxPerCheck) };
        List<string> assumptions = new();
        List<string> notChecked = new();

        List<(string Path, string Family, string Stem, int Order)> mounted =
            Discover(options, assumptions, out List<AuditSkipped> skipped, out string manifestStem);
        if (mounted.Count == 0)
            throw new InvalidOperationException($"No mountable .wz archives in {options.Folder}.");

        lock (_gate) _progress.ArchivesTotal = mounted.Count;

        /* Encryption is detected once and reused. A per-archive detection costs
           three directory parses of a 2 GB file and would answer the same thing
           29 times; a client with two encryptions in one folder is not a client.

           The archive it is detected FROM is not the smallest one. A v232 client
           keeps Base.wz, Data.wz and TamingMob.wz as a few kilobytes of stub, and
           detection works by parsing with each candidate key and scoring how
           printable the names come out — on a 6 KB stub every key scores badly on
           noise, and the reader walked off the end of one hard enough to overflow
           the stack and take the process with it. Detect from the smallest archive
           that is big enough to be a real one. */
        WzMapleVersion version;
        short gameVersion;
        if (!string.IsNullOrWhiteSpace(options.MapleVersion)
            && Enum.TryParse(options.MapleVersion, true, out WzMapleVersion told))
        {
            version = told;
            gameVersion = options.GameVersion != 0 ? options.GameVersion : (short)-1;
            assumptions.Add($"Encryption {version} was supplied by the caller, not detected.");
        }
        else
        {
            const long MinimumRealArchive = 1L << 20;   // 1 MB
            (string Path, string Family, string Stem, int Order) probe =
                mounted.Where(m => new FileInfo(m.Path).Length >= MinimumRealArchive)
                       .OrderBy(m => new FileInfo(m.Path).Length)
                       .FirstOrDefault();
            if (probe.Path == null)
                probe = mounted.OrderByDescending(m => new FileInfo(m.Path).Length).First();

            version = WzTool.DetectMapleVersion(probe.Path, out gameVersion);
            assumptions.Add($"Encryption {version} and game version {gameVersion} detected from " +
                            $"{Path.GetFileName(probe.Path)} and reused for every archive in the folder. " +
                            "Stubs under 1 MB are never used to detect from.");
        }

        List<AuditArchive> archives = new();
        List<ImageEntry> images = new();
        Dictionary<string, OutlinkRefs> outlinks = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<NamedReference>> references = new(StringComparer.OrdinalIgnoreCase);
        Facts facts = new();

        // Files stay open across both passes: pass 2 has to re-read the winning
        // copy of an outlink's target image, and re-opening a 2 GB archive to
        // read one image would cost another full directory parse. What is held
        // is the directory skeleton, not content — images are unparsed as they
        // are walked.
        List<WzFile> open = new();
        List<AuditStringCoverage> coverage = new();
        try
        {
            foreach ((string path, string family, string stem, int order) in mounted)
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _progress.Phase = "reading";
                    _progress.Archive = stem;
                }

                Stopwatch one = Stopwatch.StartNew();
                long bytes = new FileInfo(path).Length;

                /* THE GATE THAT KEEPS THE RUN ALIVE, and it is structural now
                   rather than a size threshold.

                   This client's Data.wz is 6 KB and is not an archive at all: it
                   has no PKG1 identifier. It is a bare WZ *image* — a private
                   server's patch blob, a flat list of override paths — that was
                   given a .wz name. Its header bytes read as a 4,024,959,079-byte
                   directory offset, and the directory parser walks that into a
                   string read that recurses until the stack is gone. A stack
                   overflow cannot be caught: it takes the process, and with it the
                   twenty-eight archives that were about to be audited. It killed
                   this build twice, which is why nothing reaches that parser now
                   until a forward-only, bounds-checked read of its root entry list
                   has already succeeded.

                   It used to be a 64 KB floor, and the floor was measuring the
                   wrong thing in both directions. Measured on this client: Base.wz
                   is 6.7 KB and a perfectly good archive — three images plus the
                   eighteen namespace entries this run's mount order is read from —
                   and TamingMob.wz is 2.6 KB and holds twenty-three real images.
                   Both were being reported as empty stubs and neither was audited.
                   A 5 MB file of noise, meanwhile, sailed straight past the floor
                   into the parser. Size was a proxy; "does its directory decode"
                   is the actual question, and it is decidable.

                   The residual risk is named rather than papered over: this
                   validates the ROOT entry list, so an archive with a good root
                   and a corrupt sub-directory can still reach the parser. That has
                   not been seen; the file that killed the process fails at byte
                   zero.

                   This also replaces the AuditOptions.StubCeilingBytes knob that
                   briefly existed to let the tests turn the floor off. The knob
                   was there because a fixture is a few hundred bytes and the
                   floor skipped all of them, which turned twenty-seven checks
                   into twenty-seven silent passes over a client nothing had
                   opened. A knob is the wrong answer to that: it means the suite
                   and a real client go through different gates, and the gate is
                   the thing under test. They go through this one together. */
                found.Examine("archive.stub");
                found.Examine("archive.manifest");
                RootScan? root = TryReadRoot(path, out string unreadable);
                if (root == null)
                {
                    found.Add(new AuditFinding("archive.stub", AuditSeverity.Info, stem + ".wz",
                        $"{bytes:N0} bytes, and no reading of its root directory decoded, so it was NOT " +
                        $"opened and nothing in it was audited ({unreadable}). A file shaped like this is " +
                        "what takes the process down when it reaches the directory parser."));
                    archives.Add(new AuditArchive(stem + ".wz", family, order, bytes,
                        "NotOpened", version.ToString(), gameVersion, 0, 0, one.Elapsed.TotalSeconds));
                    lock (_gate) _progress.ArchivesDone++;
                    continue;
                }

                if (stem.Equals(manifestStem, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new AuditFinding("archive.manifest", AuditSeverity.Info, stem + ".wz",
                        $"{bytes:N0} bytes, and its root is the list of {root.Namespaces.Count} other " +
                        $"archives ({string.Join(", ", root.Namespaces)}) that this client mounts. This run's " +
                        "mount order was read from it, and it was not opened: its entries stand in for " +
                        "files this run mounts on their own, so walking them would walk the same " +
                        $"namespaces again, empty. Its own {root.Images:N0} image(s) were NOT audited."));
                    archives.Add(new AuditArchive(stem + ".wz", family, order, bytes,
                        "Manifest", version.ToString(), gameVersion, 0, 0, one.Elapsed.TotalSeconds));
                    lock (_gate) _progress.ArchivesDone++;
                    continue;
                }

                Console.WriteLine($"  audit: opening {stem}.wz ({bytes / 1048576} MB)");
                WzFile file = new(path, gameVersion, version);
                WzFileParseStatus status;
                string why;
                try
                {
                    status = file.ParseWzFile();
                    why = status.GetErrorDescription();
                }
                catch (Exception ex)
                {
                    status = WzFileParseStatus.Failed_Unknown;
                    why = ex.Message;
                }

                found.Examine("archive.parse_failed");
                if (status != WzFileParseStatus.Success)
                {
                    found.Add(new AuditFinding("archive.parse_failed", AuditSeverity.Critical,
                        stem + ".wz", $"The archive could not be opened: {why}. Every check below is " +
                        "missing whatever this archive holds, including the links other archives point " +
                        "into it."));
                    file.Dispose();
                    archives.Add(new AuditArchive(stem + ".wz", family, order, bytes,
                        status.ToString(), version.ToString(), gameVersion, 0, 0, one.Elapsed.TotalSeconds));
                    lock (_gate) _progress.ArchivesDone++;
                    continue;
                }

                open.Add(file);
                ArchiveWalk walk = new(this, found, images, outlinks, references, facts,
                                       stem, family, order, options, token);
                walk.Run(file);

                archives.Add(new AuditArchive(stem + ".wz", family, order, bytes, "Success",
                    version.ToString(), gameVersion, walk.Images, walk.Canvases, one.Elapsed.TotalSeconds));

                lock (_gate)
                {
                    _progress.ArchivesDone++;
                    _progress.Findings = found.Total;
                }
            }

            token.ThrowIfCancellationRequested();
            lock (_gate) { _progress.Phase = "cross-checks"; _progress.Archive = ""; }

            DuplicateImages(images, found);
            ResolveOutlinks(outlinks, images, open, found, token);
            FormatVocabulary(facts, found, notChecked);
            CrossNames(facts, references, found, notChecked);
            coverage = StringVersusData(facts, found, notChecked);
        }
        finally
        {
            foreach (WzFile file in open)
            {
                try { file.Dispose(); } catch { /* closing a read-only handle */ }
            }
        }

        List<AuditFinding> findings = found.Kept.Values.SelectMany(v => v)
            .OrderBy(f => (int)f.Severity).ThenBy(f => f.Check, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal).ToList();

        List<AuditCheck> checks = Catalog()
            .Select(c => new AuditCheck(c.Id, c.Title, c.Severity,
                found.Examined.GetValueOrDefault(c.Id),
                found.Counts.GetValueOrDefault(c.Id),
                found.Counts.GetValueOrDefault(c.Id) > found.Kept.GetValueOrDefault(c.Id)?.Count,
                c.NotChecked))
            .ToList();

        return new AuditReport(options.Folder, started, clock.Elapsed.TotalSeconds,
            archives, checks, findings, coverage, skipped, assumptions, notChecked);
    }

    /* ====================================================================
       DISCOVERY — which files a client actually mounts, and in what order
       ==================================================================== */

    /// <summary>
    /// A numbered part of a split namespace: exactly three digits, always.
    ///
    /// The three is load-bearing. <c>Skill.wz + Skill001.wz + Skill002.wz +
    /// Skill003.wz</c> are one namespace in four files. <c>Map2.wz</c>,
    /// <c>Mob2.wz</c> and <c>Sound2.wz</c> sitting beside <c>Map.wz</c>,
    /// <c>Mob.wz</c> and <c>Sound.wz</c> are NOT: this client's own Base.wz
    /// lists Map, Map2, Mob, Mob2, Sound and Sound2 as six separate top-level
    /// namespaces. A rule that stripped any trailing digit would merge them,
    /// and every image name two of them happened to share would be reported as
    /// a shadowing bug in a family the client never mounts as one.
    /// </summary>
    private static readonly Regex NumberedPart =
        new(@"^(?<stem>.+?)(?<num>[0-9]{3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Fallback shape, used only when the client's own manifest cannot be read.</summary>
    private static readonly Regex MountedName = new(@"^([A-Za-z]+?)(\d*)$", RegexOptions.Compiled);

    /// <summary>
    /// The namespace an archive's content resolves under: the stem with its
    /// trailing digits removed, so Mob, Mob001, Mob002 and Mob2 are all Mob.
    ///
    /// Base.wz says otherwise and Base.wz is not the authority here, which is
    /// worth writing down because it is not obvious. Its root lists Mob and Mob2
    /// — and Map and Map2, and Sound and Sound2 — as separate entries, so a first
    /// reading makes them separate namespaces. The client's own data says no:
    /// canvases INSIDE Mob2.wz write <c>_outlink = "Mob/9390610.img/..."</c> and
    /// 9390610.img is in Mob2.wz. Splitting them turned 347 unresolved outlinks
    /// into 22,509, all of them Mob2 pointing at itself through the Mob name.
    /// 22,509 links written by the client are better evidence about where its
    /// content resolves than a directory listing that only says which files
    /// exist. The Base.wz entry says a file is mounted; it does not say the
    /// content lands under a namespace of the same name.
    ///
    /// What Base.wz IS the authority on is WHICH files mount and in what order,
    /// which is what <see cref="Discover"/> uses it for.
    /// </summary>
    private static string Family(string stem)
    {
        int end = stem.Length;
        while (end > 0 && char.IsAsciiDigit(stem[end - 1])) end--;
        return end == 0 ? stem : stem[..end];
    }

    /// <summary>
    /// The namespaces a client mounts, as the client itself declares them.
    /// <see cref="Namespaces"/> is empty when the manifest could not be read,
    /// and <see cref="Why"/> then says what stopped it.
    /// </summary>
    private sealed record MountManifest(IReadOnlyList<string> Namespaces, string Stem, string Source, string? Why);

    /// <summary>
    /// Reads the client's real load order out of <c>Base.wz</c>.
    ///
    /// This replaces the auditor's weakest joint. Every cross-archive finding —
    /// a shadowed image, an _outlink that resolves, which copy of a duplicate
    /// the game gets — depends on which files belong to one namespace and which
    /// of them mounts first, and until now that was inferred from the file names
    /// by a rule nobody had checked against a client.
    ///
    /// The client does not infer it. <c>Base.wz</c>'s root directory holds one
    /// zero-length type-3 entry per namespace, and those entries ARE the list. On
    /// the v232 client measured here it names eighteen, in this order: Skill, Mob,
    /// Etc, Effect, Item, Npc, Character, Quest, Map, String, Mob2, Reactor, Morph,
    /// TamingMob, Sound, Map2, Sound2, UI. Note Mob2, Map2 and Sound2 in that list
    /// beside Mob, Map and Sound: six namespaces there, not three. Note also what
    /// is NOT in it — <c>Skilloldsafe.wz</c>, <c>Skill_edited.wz</c>,
    /// <c>Character_backup_20260311_211729.wz</c> and the rest of a working
    /// folder's debris, of which a name-shaped filter eventually lets one through.
    ///
    /// Read WITHOUT the directory parser, deliberately. Handing this client's
    /// <c>Data.wz</c> to that parser takes the process down: it has no PKG1 header
    /// at all (it is a loose .img that was given a .wz name), its header bytes read
    /// as a 4,024,959,079-byte directory offset, and the reader walks off into a
    /// string read that recurses until the stack is gone — which cannot be caught.
    /// So this walks the root entry list itself, forward only, never recursing, and
    /// bounds-checks every position against the file's real length. Anything that
    /// does not add up returns a reason instead of a list, and the caller falls back
    /// to the old name-shaped guess and says in the report that it did.
    ///
    /// The encryption key is not assumed either: the read is attempted with each
    /// candidate and the one that yields plausible namespace names wins. A name
    /// here is a short identifier, so "did it decode" is decidable rather than a
    /// judgement — which is not true of the general version detection this class
    /// otherwise depends on.
    /// </summary>
    private static MountManifest ReadMountManifest(string folder)
    {
        string? path = Directory.GetFiles(folder, "*.wz")
            .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), "Base",
                                               StringComparison.OrdinalIgnoreCase));
        if (path == null)
            return new MountManifest(Array.Empty<string>(), "", "",
                "there is no Base.wz in the folder, and Base.wz is where a client's namespace list lives");

        RootScan? scan = TryReadRoot(path, out string why);
        if (scan == null)
            return new MountManifest(Array.Empty<string>(), "", "",
                "Base.wz is there but no reading of its root directory decoded (" + why + ")");

        // Three is the floor for believing this is a manifest at all. A Base.wz
        // that holds only its own images declares no namespaces, and inventing a
        // one-namespace client out of that would be worse than falling back.
        if (scan.Namespaces.Count < 3)
            return new MountManifest(Array.Empty<string>(), "", "",
                $"Base.wz decoded but declares only {scan.Namespaces.Count} namespace directories, " +
                "which is not a client's namespace list");

        return new MountManifest(scan.Namespaces, "Base", "Base.wz", null);
    }

    /// <summary>What one bounded read of an archive's root entry list saw.</summary>
    private sealed record RootScan(int Images, int Directories, IReadOnlyList<string> Namespaces);

    /// <summary>
    /// The root entry list of a WZ archive, decoded with one candidate key, read
    /// forward only and never recursing. Throws if anything is out of bounds —
    /// the caller treats a throw as "not this key" and, when every key throws, as
    /// "not an archive", never as a finding about the client.
    /// </summary>
    private static RootScan ReadRoot(string path, byte[] iv, bool versionHeader)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using WzBinaryReader reader = new(stream, iv);

        long length = stream.Length;
        if (length < 32) throw new InvalidDataException("shorter than a WZ header");
        if (reader.ReadString(4) != "PKG1") throw new InvalidDataException("no PKG1 identifier");

        reader.ReadUInt64();                        // content size, not needed here
        uint start = reader.ReadUInt32();
        if (start < 17 || start >= length)
            throw new InvalidDataException($"directory offset {start} is outside the file");

        // A 32-bit-era archive writes a two-byte version hash at the start of the
        // directory and a 64-bit one does not. Which it is cannot be read off the
        // header, so the caller tries both and keeps whichever decodes — one more
        // decidable question instead of one more assumption.
        stream.Position = versionHeader ? start + 2 : start;
        int count = reader.ReadCompressedInt();
        if (count <= 0 || count > 200_000)
            throw new InvalidDataException($"entry count {count} is not a directory");

        List<string> namespaces = new();
        int images = 0, directories = 0;
        for (int i = 0; i < count; i++)
        {
            if (stream.Position < start || stream.Position >= length)
                throw new InvalidDataException("the entry list runs past the end of the file");

            byte type = reader.ReadByte();
            string name;
            switch (type)
            {
                case 1:                              // a placeholder the format skips over
                    stream.Position += 10;
                    continue;

                case 2:
                    {
                        // The name lives once in a string table and the entry
                        // points at it. The byte at that offset is the real type.
                        int offset = reader.ReadInt32();
                        long at = start + offset;
                        if (at < start || at >= length)
                            throw new InvalidDataException("a name offset is outside the file");
                        long resume = stream.Position;
                        stream.Position = at;
                        type = reader.ReadByte();
                        if (type != 3 && type != 4)
                            throw new InvalidDataException($"entry type {type} is not a directory or an image");
                        name = reader.ReadString();
                        stream.Position = resume;
                        break;
                    }

                case 3:
                case 4:
                    name = reader.ReadString();
                    break;

                default:
                    throw new InvalidDataException($"entry type {type} is not one this format defines");
            }

            int size = reader.ReadCompressedInt();
            reader.ReadCompressedInt();              // checksum
            stream.Position += 4;                    // the entry's encoded offset

            /* The key check, and it has to be this and not "does the name look
                like an archive name".

                An earlier cut threw unless every zero-length directory decoded to
                a bare identifier, and it cost the run: Mob.wz's root holds an
                EMPTY DIRECTORY CALLED `_Canvas` — real leftover damage from an
                earlier import, recorded in this repo — so a 1.4 GB archive was
                declared unreadable and skipped, and every _outlink in the client
                that pointed into Mob then had nothing to land on. 347 unresolved
                outlinks became 40,834. A gate that decides whether to look at 1.4
                GB has to ask whether the bytes DECODED, not whether the client is
                tidy.

                Printable ASCII is what "decoded" means here: a WZ name is one, and
                a wrong key turns one into high bytes within a character or two. */
            if (name.Length is 0 or > 64 || name.Any(c => c < 0x20 || c > 0x7E))
                throw new InvalidDataException($"'{name}' did not decode as a name");

            if (type == 4) images++; else directories++;

            // A namespace is declared as an empty directory whose content is in
            // <Name>.wz. `_Canvas` is zero-length too and is not one, which is
            // why the manifest only ever takes identifier-shaped names — and why
            // whether this archive IS a manifest is decided by the caller, from
            // which file the mount order actually came out of, not from a shape.
            if (type != 3 || size != 0) continue;
            if (IsIdentifier(name)) namespaces.Add(name);
        }

        return new RootScan(images, directories, namespaces);
    }

    /// <summary>
    /// The root entry list, under whichever key and header shape decodes it.
    /// Null means no reading of this file produced a WZ directory.
    /// </summary>
    private static RootScan? TryReadRoot(string path, out string why)
    {
        List<string> tried = new();
        foreach (WzMapleVersion candidate in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS })
        {
            foreach (bool versionHeader in new[] { false, true })
            {
                try
                {
                    RootScan scan = ReadRoot(path, WzTool.GetIvByMapleVersion(candidate), versionHeader);
                    if (scan.Images + scan.Directories > 0) { why = ""; return scan; }
                    tried.Add($"{candidate}{(versionHeader ? "+hdr" : "")}: empty");
                }
                catch (Exception ex)
                {
                    tried.Add($"{candidate}{(versionHeader ? "+hdr" : "")}: {ex.Message}");
                }
            }
        }

        why = string.Join("; ", tried.Distinct());
        return null;
    }

    /// <summary>The shape of an archive family's name, which is what a manifest entry is.</summary>
    private static bool IsIdentifier(string name) =>
        name.Length is > 0 and <= 40
        && char.IsAsciiLetter(name[0])
        && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// The archives the client mounts, in mount order, plus every other .wz in
    /// the folder with the reason it was left alone.
    ///
    /// With a manifest, "what the client mounts" is read rather than guessed and
    /// the backup filter falls out for free. Without one the old name-shaped rule
    /// stands in, because a folder full of Skill_edited.wz and Character_backup_*.wz
    /// audited as though it were a client produces a duplicate-image report for
    /// every image in it — but it is then reported as an assumption, because that
    /// is what it is.
    /// </summary>
    private static List<(string Path, string Family, string Stem, int Order)> Discover(
        AuditOptions options, List<string> assumptions, out List<AuditSkipped> skipped,
        out string manifestStem)
    {
        manifestStem = "";
        string[] wanted = options.Families ?? Array.Empty<string>();
        List<(string Path, string Family, string Stem, int Order)> result = new();
        skipped = new List<AuditSkipped>();

        Dictionary<string, string> byStem = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(options.Folder, "*.wz"))
            byStem[Path.GetFileNameWithoutExtension(path)] = path;

        MountManifest manifest = ReadMountManifest(options.Folder);
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        if (manifest.Namespaces.Count > 0)
        {
            manifestStem = manifest.Stem;

            /* Mount ORDER is the manifest's own sequence, with each namespace's
               three-digit parts inserted behind it. That is the half worth having:
               Mob.wz and Mob2.wz both resolve under Mob and a rule that ordered
               them by their trailing digits gave both of them "2", so which one
               shadowed the other came down to how the file list happened to sort.
               Base.wz names Mob tenth from the end and Mob2 eighth, and that is a
               fact rather than a tie-break. */
            int mount = 0;

            // The manifest itself mounts, first and at the root: Base.wz is the
            // namespace list AND it holds zmap.img, smap.img and StandardPDD.img,
            // which are content.
            if (byStem.TryGetValue(manifest.Stem, out string? basePath))
            {
                taken.Add(manifest.Stem);
                if (wanted.Length == 0 || wanted.Contains(manifest.Stem, StringComparer.OrdinalIgnoreCase))
                    result.Add((basePath, Family(manifest.Stem), manifest.Stem, mount++));
            }

            foreach (string declared in manifest.Namespaces)
            {
                string family = Family(declared);
                bool include = wanted.Length == 0
                    || wanted.Contains(family, StringComparer.OrdinalIgnoreCase)
                    || wanted.Contains(declared, StringComparer.OrdinalIgnoreCase);

                if (byStem.TryGetValue(declared, out string? baseFile))
                {
                    taken.Add(declared);
                    if (include) result.Add((baseFile, family, declared, mount++));
                }

                for (int part = 1; part <= 999; part++)
                {
                    string stem = declared + part.ToString("000");
                    if (!byStem.TryGetValue(stem, out string? partPath)) continue;
                    taken.Add(stem);
                    if (include) result.Add((partPath, family, stem, mount++));
                }
            }

            foreach ((string stem, string path) in byStem)
            {
                if (taken.Contains(stem)) continue;
                skipped.Add(new AuditSkipped(Path.GetFileName(path), new FileInfo(path).Length,
                    "Base.wz's root directory does not name it, so the client does not mount it"));
            }

            assumptions.Add("The list of archives, and which namespace each belongs to, was READ rather " +
                            $"than assumed: {manifest.Source} names {manifest.Namespaces.Count} namespaces " +
                            $"({string.Join(", ", manifest.Namespaces)}), and mount order is the order " +
                            "they are named in, with each one's three-digit parts behind it. Map2, Mob2 and " +
                            "Sound2 are listed as separate entries there, but their CONTENT resolves under " +
                            "Map, Mob and Sound: canvases inside Mob2.wz link to 'Mob/<image>' and the image " +
                            "is in Mob2.wz. So a Base.wz entry says which file mounts, not which namespace " +
                            "its content lands in.");
            assumptions.Add("WITHIN one namespace, the unsuffixed archive is ASSUMED to mount before its " +
                            "three-digit parts. That half was not read and could not be: the client's " +
                            "file-selection code is in a packed binary and the archive-opening DLLs never " +
                            "see a file name at all. It is what a shadowing report's 'wins' column rests " +
                            "on, so it is the part to verify in game. A stock client ships no image name " +
                            "in two parts of one namespace, which means the precedence rule is untested by " +
                            "the vendor as well — every shadowed image below was introduced by an edit.");

            // Returned in the manifest's own order, not alphabetically: the
            // point of reading Base.wz is that this IS the client's load order.
            return result;
        }

        List<(string Path, string Family, string Stem, int Suffix)> guessed = new();
        foreach ((string stem, string path) in byStem)
        {
            if (!MountedName.IsMatch(stem))
            {
                skipped.Add(new AuditSkipped(Path.GetFileName(path), new FileInfo(path).Length,
                    "not named <letters><digits>.wz, so the client does not mount it"));
                continue;
            }

            string family = Family(stem);
            Match part = NumberedPart.Match(stem);
            int suffix = part.Success ? int.Parse(part.Groups["num"].Value) : 0;
            if (wanted.Length > 0 && !wanted.Contains(family, StringComparer.OrdinalIgnoreCase)) continue;
            guessed.Add((path, family, stem, suffix));
        }

        // Ordered, then numbered, so two archives that would tie on their suffix
        // still get a stable answer rather than one that depends on the order the
        // filesystem happened to list them in.
        int fallbackMount = 0;
        foreach ((string path, string family, string stem, int _) in guessed
                     .OrderBy(g => g.Family, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(g => g.Suffix)
                     .ThenBy(g => g.Stem, StringComparer.OrdinalIgnoreCase))
        {
            result.Add((path, family, stem, fallbackMount++));
        }

        assumptions.Add($"The client's own namespace list could not be read ({manifest.Why}), so which " +
                        "files mount and which namespace each belongs to is inferred from the file names: " +
                        "<Name>.wz first, then <Name>001.wz, <Name>002.wz in order. A trailing digit that " +
                        "is not three digits long is part of the name, so Map2.wz is its own namespace and " +
                        "not part of Map. This is the assumption every cross-archive finding rests on.");
        assumptions.Add("Only files named <letters><digits>.wz are treated as mounted. Backups and " +
                        "working copies (Skill_edited.wz, Character_backup_*.wz, *.wz_BAK_*) are skipped.");

        return result;
    }

    /* ====================================================================
       PASS 1 — one archive, alone
       ==================================================================== */

    /// <summary>Small id sets the cross-checks compare against each other.</summary>
    private sealed class Facts
    {
        public readonly HashSet<string> CharacterActions = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SoundPaths = new(StringComparer.OrdinalIgnoreCase);

        public readonly HashSet<int> SkillIds = new();
        public readonly HashSet<int> MobIds = new();
        public readonly HashSet<int> NpcIds = new();
        public readonly HashSet<int> MapIds = new();
        public readonly HashSet<int> ItemIds = new();

        public readonly HashSet<int> StringSkillIds = new();
        public readonly HashSet<int> StringMobIds = new();
        public readonly HashSet<int> StringNpcIds = new();
        public readonly HashSet<int> StringMapIds = new();
        public readonly HashSet<int> StringItemIds = new();

        /* The canvas format histogram, per archive. Kept because "a format the
           decoder knows" and "a format THIS client uses" are different
           questions, and only the second one catches a canvas re-encoded by a
           tool into a format that is perfectly legal and that nothing else in
           the archive is written in. Counting is all pass 1 can do; the
           judgement needs the whole archive, so it happens in pass 2. */
        public readonly Dictionary<string, Dictionary<int, long>> FormatCounts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>"&lt;stem&gt;&lt;format&gt;" -> one real path using it.</summary>
        public readonly Dictionary<string, string> FormatExample = new(StringComparer.Ordinal);

        public bool SawString, SawSkill, SawMob, SawNpc, SawMap, SawItem, SawCharacter, SawSound;
    }

    private sealed class ArchiveWalk
    {
        private readonly IntegrityAuditService _owner;
        private readonly Collector _found;
        private readonly List<ImageEntry> _images;
        private readonly Dictionary<string, OutlinkRefs> _outlinks;
        private readonly Dictionary<string, HashSet<NamedReference>> _references;
        private readonly Facts _facts;
        private readonly string _stem, _family;
        private readonly int _order;
        private readonly AuditOptions _options;
        private readonly CancellationToken _token;

        public int Images;
        public long Canvases;

        public ArchiveWalk(IntegrityAuditService owner, Collector found, List<ImageEntry> images,
            Dictionary<string, OutlinkRefs> outlinks, Dictionary<string, HashSet<NamedReference>> references,
            Facts facts, string stem, string family, int order, AuditOptions options, CancellationToken token)
        {
            _owner = owner; _found = found; _images = images; _outlinks = outlinks;
            _references = references; _facts = facts; _stem = stem; _family = family;
            _order = order; _options = options; _token = token;
        }

        public void Run(WzFile file)
        {
            switch (_family.ToLowerInvariant())
            {
                case "string": _facts.SawString = true; break;
                case "skill": _facts.SawSkill = true; break;
                case "mob": _facts.SawMob = true; break;
                case "npc": _facts.SawNpc = true; break;
                case "map": _facts.SawMap = true; break;
                case "item": _facts.SawItem = true; break;
                case "character": _facts.SawCharacter = true; break;
                case "sound": _facts.SawSound = true; break;
            }
            Walk(file.WzDirectory, "");
        }

        private void Walk(WzDirectory dir, string prefix)
        {
            foreach (WzImage image in dir.WzImages)
            {
                _token.ThrowIfCancellationRequested();
                if (_options.MaxImagesPerArchive > 0 && Images >= _options.MaxImagesPerArchive) return;

                string rel = prefix.Length == 0 ? image.Name : prefix + "/" + image.Name;
                _images.Add(new ImageEntry(_stem, _family, _order, rel, image.BlockSize, image.Checksum));
                Images++;
                if ((Images & 0x3FF) == 0)
                    lock (_owner._gate) _owner._progress.ImagesDone += 1024;

                bool wasParsed = image.Parsed;
                _found.Examine("image.parse_failed");
                try
                {
                    if (!image.Parsed && !image.Changed && !image.ParseImage())
                    {
                        _found.Add(new AuditFinding("image.parse_failed", AuditSeverity.Critical,
                            $"{_stem}.wz/{rel}",
                            "The image header is not one the reader recognises, so nothing inside it " +
                            "can be read — the client will not read it either."));
                        continue;
                    }

                    _found.Examine("image.empty");
                    if (image.WzProperties.Count == 0)
                        _found.Add(new AuditFinding("image.empty", AuditSeverity.Warning,
                            $"{_stem}.wz/{rel}", "The image parses and holds nothing at all."));

                    foreach (WzImageProperty property in image.WzProperties)
                        Property(property, image, rel, $"{_stem}.wz/{rel}");

                    Harvest(image, rel);
                }
                catch (Exception ex)
                {
                    _found.Add(new AuditFinding("image.parse_failed", AuditSeverity.Critical,
                        $"{_stem}.wz/{rel}", $"The image threw while being read: {ex.Message}"));
                }
                finally
                {
                    // The memory bound. Without it a 2 GB archive is a 2 GB heap.
                    if (!wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                    {
                        try { image.UnparseImage(); } catch { /* nothing left to free */ }
                    }
                }
            }

            foreach (WzDirectory sub in dir.WzDirectories)
                Walk(sub, prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name);
        }

        private void Property(WzImageProperty property, WzImage image, string rel, string path)
        {
            string here = path + "/" + property.Name;

            switch (property)
            {
                case WzCanvasProperty canvas:
                    Canvas(canvas, image, rel, here);
                    break;

                case WzUOLProperty uol:
                    Uol(uol, image, here);
                    return;   // never descend a UOL: WzUOLProperty compiles with
                              // UOLRES on, so its children ARE the target's children
                              // and a walker that descends follows the link.

                case WzBinaryProperty sound:
                    // WzBinaryProperty.Length is the duration in milliseconds,
                    // not the byte count — the byte count lives in an internal
                    // field this assembly cannot see, and reading GetBytes for
                    // every sound in a 3 GB Sound family to learn it would cost
                    // more than the check is worth. So this says duration, and
                    // the wording says duration: a sound that announces itself
                    // as zero milliseconds long is a name the client will try to
                    // play and hear nothing from.
                    _found.Examine("blob.empty");
                    if (sound.Length <= 0)
                        _found.Add(new AuditFinding("blob.empty", AuditSeverity.Error, here,
                            "The sound declares a duration of zero milliseconds. It occupies a name " +
                            "the client will try to play and get silence from."));
                    break;

                case WzStringProperty text:
                    Reference(text, here);
                    break;
            }

            foreach (WzImageProperty child in property.WzProperties ?? (IEnumerable<WzImageProperty>)Array.Empty<WzImageProperty>())
                Property(child, image, rel, here);
        }

        /* ---- canvases ---------------------------------------------------- */

        private void Canvas(WzCanvasProperty canvas, WzImage image, string rel, string path)
        {
            Canvases++;
            WzPngProperty? png = canvas.PngProperty;

            string? inlink = (canvas[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
            string? outlink = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;

            _found.Examine("canvas.both_links");
            if (!string.IsNullOrEmpty(inlink) && !string.IsNullOrEmpty(outlink))
                _found.Add(new AuditFinding("canvas.both_links", AuditSeverity.Warning, path,
                    "The canvas carries an _inlink and an _outlink. The client reads the _inlink and " +
                    "ignores the other, so whichever was meant, one of them is dead weight."));

            /* _inlink: image-root-relative, and it never crosses an image
               boundary. Resolvable right here, while the image is parsed. */
            if (!string.IsNullOrEmpty(inlink))
            {
                _found.Examine("inlink.unresolved");
                if (image.GetFromPath(inlink) is not WzImageProperty)
                    _found.Add(new AuditFinding("inlink.unresolved", AuditSeverity.Error, path,
                        "The _inlink names a path inside this image that is not there, so the canvas " +
                        "has no pixels to borrow and draws nothing.", inlink));
            }

            /* _outlink: family-rooted, cross-archive, deferred to pass 2. */
            if (!string.IsNullOrEmpty(outlink))
            {
                string key = outlink.Replace('\\', '/').Trim('/');
                if (!_outlinks.TryGetValue(key, out OutlinkRefs? refs))
                    _outlinks[key] = refs = new OutlinkRefs();
                refs.Saw(path);
            }

            if (png == null) return;

            int format = (int)png.Format;
            int mag = png.Mag;

            Dictionary<int, long> histogram = _facts.FormatCounts.TryGetValue(_stem, out Dictionary<int, long>? h)
                ? h : _facts.FormatCounts[_stem] = new Dictionary<int, long>();
            histogram[format] = histogram.GetValueOrDefault(format) + 1;
            _facts.FormatExample.TryAdd(_stem + "" + format, path);

            _found.Examine("canvas.format_unknown");
            if (!KnownFormats.Contains(format))
                _found.Add(new AuditFinding("canvas.format_unknown", AuditSeverity.Critical, path,
                    $"Canvas format {format} is not one of the nine the client's decoder knows " +
                    $"({string.Join(", ", KnownFormats.OrderBy(f => f))}). The usual cause is a tool " +
                    "that wrote format and mag as one packed int; the usual result is a canvas drawn " +
                    "as noise or a decode that reads past the blob.",
                    $"format={format} mag={mag} {png.Width}x{png.Height}"));

            /* The client's row stride is ceil(width >> mag) << (format & 0xFF).
               A width that shifts to zero is a row of no pixels: the canvas
               exists, has a blob, and covers nothing. */
            _found.Examine("canvas.row_width_zero");
            if (png.Width > 0 && mag > 0 && png.Width >> mag == 0)
                _found.Add(new AuditFinding("canvas.row_width_zero", AuditSeverity.Critical, path,
                    $"Width {png.Width} shifted right by mag {mag} is zero, so the client computes a " +
                    "row of no pixels for a canvas that has data.",
                    $"format={format} mag={mag} {png.Width}x{png.Height}"));
            else if (png.Height > 0 && mag > 0 && png.Height >> mag == 0)
                _found.Add(new AuditFinding("canvas.row_width_zero", AuditSeverity.Critical, path,
                    $"Height {png.Height} shifted right by mag {mag} is zero.",
                    $"format={format} mag={mag} {png.Width}x{png.Height}"));

            _found.Examine("canvas.zero_dimension");
            if (png.Width <= 0 || png.Height <= 0)
                _found.Add(new AuditFinding("canvas.zero_dimension", AuditSeverity.Error, path,
                    $"The canvas is {png.Width}x{png.Height}."));

            /* An empty blob is only a fault when nothing else is meant to supply
               the pixels. A linked canvas is SUPPOSED to be empty. */
            if (!string.IsNullOrEmpty(inlink) || !string.IsNullOrEmpty(outlink)) return;

            /* The length, not the bytes. Reading every blob to find the empty
               ones means reading the whole client -- tens of gigabytes of IO and
               one large-object allocation per canvas -- to look at an integer
               that is already on disk four bytes ahead of the data. */
            _found.Examine("canvas.empty_blob");
            int length = png.CompressedLength;
            if (length < 0)
                _found.Add(new AuditFinding("canvas.empty_blob", AuditSeverity.Critical, path,
                    "The canvas's stored data length is negative or unreadable. That is what an " +
                    "archive read with the wrong key looks like, and what a canvas written by a tool " +
                    "that miscounted looks like; either way nothing can decode it.",
                    $"format={format} mag={mag} {png.Width}x{png.Height}"));
            else if (length == 0)
                _found.Add(new AuditFinding("canvas.empty_blob", AuditSeverity.Error, path,
                    "A canvas with no link and no compressed data. Nothing supplies its pixels.",
                    $"format={format} mag={mag} {png.Width}x{png.Height}"));
        }

        /* ---- UOLs -------------------------------------------------------- */

        /// <summary>
        /// Resolves a UOL without calling <c>WzUOLProperty.LinkValue</c>, and
        /// against BOTH readings of what a UOL is relative to.
        ///
        /// Two reasons not to call LinkValue. It memoises the resolved object
        /// into the property, which pins whatever it landed on for as long as
        /// the archive is open, and an audit that walks every UOL in a client
        /// would pin a large fraction of it. And it implements one reading of
        /// the format, which the measured data says is not the client's.
        ///
        /// The disagreement, found on a real Reactor.wz: 8679015.img has
        /// 0/hit/10 = "3", 0/hit/11 = "4" … 0/hit/16 = "9". Read as relative to
        /// the UOL's own parent that is a looping animation aliasing frame 10
        /// back to frame 3, which is what a reactor does. Read as relative to
        /// the image root — which is what LinkValue does whenever the value does
        /// not start with ".." — it asks for a top-level node called "3" that is
        /// not there, and 148 perfectly good frames in one archive come out as
        /// broken links.
        ///
        /// So: try parent-relative, then image-root-relative, and only report a
        /// UOL that fails both. Reporting the difference instead of picking a
        /// side would mean an auditor that is confidently wrong about a rule the
        /// file format does not write down anywhere.
        /// </summary>
        private void Uol(WzUOLProperty uol, WzImage image, string path)
        {
            string value = uol.Value ?? "";
            _found.Examine("uol.unresolved");
            if (value.Length == 0)
            {
                _found.Add(new AuditFinding("uol.unresolved", AuditSeverity.Error, path,
                    "The UOL's value is empty, so it points at nothing.", ""));
                return;
            }

            string[] segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            (bool ok, bool escaped) fromParent = Follow(segments, uol.Parent, image);
            if (fromParent.ok) return;

            (bool ok, bool escaped) fromRoot = Follow(segments, image, image);
            if (fromRoot.ok)
            {
                _found.Examine("uol.root_relative");
                _found.Add(new AuditFinding("uol.root_relative", AuditSeverity.Info, path,
                    "The UOL only resolves when read from the image root, not from its own parent. " +
                    "Harmless in game and worth knowing: an editor that reads it the other way shows " +
                    "this node as broken.", value));
                return;
            }

            if (fromParent.escaped || fromRoot.escaped)
            {
                // Reported honestly rather than guessed at: a UOL that walks out
                // of its image lands in a directory, and what it finds there
                // depends on which archive of the family mounted first.
                _found.Examine("uol.escapes_image");
                _found.Add(new AuditFinding("uol.escapes_image", AuditSeverity.Info, path,
                    "The UOL climbs out of its own image with '..'. Not resolved: what it lands on " +
                    "depends on which archive of the family mounted first, and parsing every image " +
                    "such a link could reach would cost more than the answer is worth.", value));
                return;
            }

            _found.Add(new AuditFinding("uol.unresolved", AuditSeverity.Error, path,
                "The UOL names a path that is in this image under neither reading of a UOL — not from " +
                "its own parent and not from the image root. Every node that reads through it reads " +
                "nothing.", value));
        }

        /// <summary>Walks segments from a starting node. Returns whether it landed, and whether it left the image.</summary>
        private static (bool Ok, bool Escaped) Follow(string[] segments, WzObject? start, WzImage image)
        {
            WzObject? cursor = start;
            foreach (string segment in segments)
            {
                if (cursor == null) return (false, false);

                // "." is "here", and real clients write it: a v232
                // Character.wz/Weapon/01702257.img/49/coolingeffect is the UOL
                // "./fireburner". Looked up as a child name it finds nothing,
                // and the link was reported dangling when it resolves perfectly.
                // WzUolResolver learned this separately; this walker is
                // deliberately its own (see the comment above) and so had to
                // learn it too. A false Error costs more than a missed one: it
                // is what teaches the reader to stop reading the list.
                if (segment == ".")
                    continue;

                if (segment == "..")
                {
                    if (ReferenceEquals(cursor, image)) return (false, true);
                    cursor = cursor.Parent;
                    continue;
                }
                cursor = cursor switch
                {
                    WzImage img => img[segment],
                    WzImageProperty prop => prop[segment],
                    _ => null,
                };
            }
            return (cursor != null, false);
        }

        /* ---- named references and id harvesting -------------------------- */

        /// <summary>
        /// Collects a string that names something living somewhere else.
        ///
        /// The shape that matters, and the one an earlier version of this missed
        /// entirely: a skill's action is <c>skill/&lt;id&gt;/action/0</c>, so the
        /// string property is named <c>0</c> and it is the PARENT that is named
        /// <c>action</c>. Matching on the string's own name alone collected
        /// nothing from a real Skill.wz and reported zero unknown actions for a
        /// client with six known-bad ones — a check that examined nothing and
        /// looked exactly like a check that found nothing.
        ///
        /// So both shapes count: <c>action</c> holding a value directly, and
        /// <c>action</c> holding a list whose entries hold the values.
        /// </summary>
        private void Reference(WzStringProperty text, string path)
        {
            string? owner = ReferenceKeys.Contains(text.Name, StringComparer.OrdinalIgnoreCase)
                ? text.Name
                : ReferenceKeys.Contains(text.Parent?.Name ?? "", StringComparer.OrdinalIgnoreCase)
                    ? text.Parent!.Name
                    : null;
            if (owner == null) return;

            string value = text.Value ?? "";
            if (value.Length == 0) return;

            string key = owner.ToLowerInvariant();
            HashSet<NamedReference> set = _references.TryGetValue(key, out HashSet<NamedReference>? s)
                ? s : _references[key] = new HashSet<NamedReference>();
            // One real place per distinct value, so a failing name can be shown
            // where it is actually written rather than as a bare string.
            set.Add(new NamedReference(_family, _stem, value, path));
        }

        private static readonly Regex NumericImage = new(@"^(\d+)\.img$", RegexOptions.Compiled);

        /// <summary>
        /// The id sets and name sets the cross-checks compare. Runs while the
        /// image is parsed and keeps only integers and short strings.
        /// </summary>
        private void Harvest(WzImage image, string rel)
        {
            string family = _family.ToLowerInvariant();
            string leaf = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
            Match numeric = NumericImage.Match(leaf);

            switch (family)
            {
                case "character":
                    // The body images carry the animation names every action
                    // string has to be one of. One skin is enough; they agree.
                    if (leaf.StartsWith("0000200", StringComparison.Ordinal))
                    {
                        foreach (WzImageProperty p in image.WzProperties)
                            _facts.CharacterActions.Add(p.Name);
                    }
                    // Equips are id-named images under a slot directory.
                    if (numeric.Success && int.TryParse(numeric.Groups[1].Value, out int equip))
                        _facts.ItemIds.Add(equip);
                    break;

                case "sound":
                    // "Mob.img/8800000" — one level of named entries per image.
                    foreach (WzImageProperty p in image.WzProperties)
                        _facts.SoundPaths.Add(rel + "/" + p.Name);
                    break;

                case "skill":
                    if (image["skill"] is WzImageProperty skills)
                    {
                        foreach (WzImageProperty p in skills.WzProperties ?? (IEnumerable<WzImageProperty>)Array.Empty<WzImageProperty>())
                            if (int.TryParse(p.Name, out int id)) _facts.SkillIds.Add(id);
                    }
                    break;

                case "mob":
                    if (numeric.Success && int.TryParse(numeric.Groups[1].Value, out int mob))
                        _facts.MobIds.Add(mob);
                    break;

                case "npc":
                    if (numeric.Success && int.TryParse(numeric.Groups[1].Value, out int npc))
                        _facts.NpcIds.Add(npc);
                    break;

                case "map":
                    if (rel.StartsWith("Map/", StringComparison.OrdinalIgnoreCase)
                        && numeric.Success && int.TryParse(numeric.Groups[1].Value, out int map))
                        _facts.MapIds.Add(map);
                    break;

                case "item":
                    // Two shapes: an id-named image (Pet/5000545.img) and a bucket
                    // image whose children are the ids (Consume/0200.img/02000000).
                    if (numeric.Success && numeric.Groups[1].Value.Length >= 7
                        && int.TryParse(numeric.Groups[1].Value, out int item))
                    {
                        _facts.ItemIds.Add(item);
                    }
                    else
                    {
                        foreach (WzImageProperty p in image.WzProperties)
                            if (int.TryParse(p.Name, out int inner) && inner >= 1000000)
                                _facts.ItemIds.Add(inner);
                    }
                    break;

                case "string":
                    HarvestString(image, leaf);
                    break;
            }
        }

        private void HarvestString(WzImage image, string leaf)
        {
            void Ids(WzImageProperty? node, HashSet<int> into, int depth)
            {
                if (node == null) return;
                foreach (WzImageProperty child in node.WzProperties ?? (IEnumerable<WzImageProperty>)Array.Empty<WzImageProperty>())
                {
                    if (int.TryParse(child.Name, out int id)) into.Add(id);
                    else if (depth > 0) Ids(child, into, depth - 1);
                }
            }

            void Roots(HashSet<int> into, int depth)
            {
                foreach (WzImageProperty child in image.WzProperties)
                {
                    if (int.TryParse(child.Name, out int id)) into.Add(id);
                    else if (depth > 0) Ids(child, into, depth - 1);
                }
            }

            switch (leaf.ToLowerInvariant())
            {
                case "skill.img": Roots(_facts.StringSkillIds, 0); break;
                case "mob.img": Roots(_facts.StringMobIds, 0); break;
                case "npc.img": Roots(_facts.StringNpcIds, 0); break;
                // Map.img groups by area: Map.img/victoria/100000000.
                case "map.img": Roots(_facts.StringMapIds, 1); break;
                // Eqp.img nests Eqp/<slot>/<id>; the others are flat.
                case "eqp.img": Roots(_facts.StringItemIds, 2); break;
                case "consume.img":
                case "ins.img":
                case "etc.img":
                case "cash.img": Roots(_facts.StringItemIds, 1); break;
            }
        }
    }

    /* ====================================================================
       PASS 2 — the whole client at once
       ==================================================================== */

    /// <summary>
    /// The measured real case this exists for: 40000.img in both Skill.wz and
    /// Skill003.wz with different bytes. Both mount; the client resolves the
    /// name to whichever came first, and every edit made to the other one is
    /// invisible — which looks exactly like an edit that did not save.
    ///
    /// "Different bytes" is decided from the directory entry alone: the on-disk
    /// block size and the checksum the archive stores beside it. No image is
    /// read to answer this, so it costs nothing on a 24 GB client.
    /// </summary>
    private static void DuplicateImages(List<ImageEntry> images, Collector found)
    {
        Dictionary<string, List<ImageEntry>> byKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (ImageEntry entry in images)
        {
            string key = entry.Family + "/" + entry.Path;
            if (!byKey.TryGetValue(key, out List<ImageEntry>? list))
                byKey[key] = list = new List<ImageEntry>();
            list.Add(entry);
        }

        foreach ((string key, List<ImageEntry> group) in byKey)
        {
            if (group.Count < 2) continue;
            found.Examine("family.shadowed_image");

            List<ImageEntry> ordered = group.OrderBy(g => g.MountOrder).ToList();
            ImageEntry winner = ordered[0];
            bool differs = ordered.Any(e => e.BlockSize != winner.BlockSize || e.Checksum != winner.Checksum);

            string where = string.Join(", ", ordered.Select(e =>
                $"{e.Archive}.wz ({e.BlockSize:N0} bytes, checksum {e.Checksum})"));

            if (differs)
            {
                found.Add(new AuditFinding("family.shadowed_image", AuditSeverity.Critical, key,
                    $"The same image name exists {ordered.Count} times in the {winner.Family} " +
                    $"family with different content: {where}. The client mounts one of them and the " +
                    $"rest are unreachable — on this mount order, {winner.Archive}.wz wins. An edit " +
                    "made to a losing copy is saved, verifiable in the file, and has no effect in game.",
                    winner.Archive + ".wz"));
            }
            else
            {
                found.Examine("family.duplicate_image");
                found.Add(new AuditFinding("family.duplicate_image", AuditSeverity.Info, key,
                    $"The same image name exists {ordered.Count} times in the family with identical " +
                    $"size and checksum: {where}. Harmless, but it is the shape a shadowing bug " +
                    "arrives in, and one edit turns it into one.", winner.Archive + ".wz"));
            }
        }
    }

    /// <summary>
    /// Resolves every distinct _outlink against the whole mounted client.
    ///
    /// The client's rule: the first segment names the archive FAMILY, not a
    /// file, and the rest is a path from that family's root. So
    /// "Skill/40000.img/skill/40001000/effect/0" is looked for in Skill.wz,
    /// Skill001.wz, Skill002.wz and Skill003.wz, in mount order. MapleLib's own
    /// GetLinkedWzImageProperty only ever searches the file the canvas is in,
    /// which is why a link can resolve in game and not in an editor, and the
    /// other way round.
    ///
    /// Grouped by target image so each target is parsed once however many
    /// thousand canvases point at it.
    /// </summary>
    private void ResolveOutlinks(Dictionary<string, OutlinkRefs> outlinks, List<ImageEntry> images,
        List<WzFile> open, Collector found, CancellationToken token)
    {
        HashSet<string> audited = images.Select(e => e.Family)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // family/imagePath -> the archives holding it, in mount order
        Dictionary<string, List<ImageEntry>> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (ImageEntry entry in images)
        {
            string key = entry.Family + "/" + entry.Path;
            if (!index.TryGetValue(key, out List<ImageEntry>? list))
                index[key] = list = new List<ImageEntry>();
            list.Add(entry);
        }

        Dictionary<string, WzFile> byStem = open.ToDictionary(
            f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

        // Group by the image the link lands in, so one parse serves them all.
        Dictionary<string, List<(string PropPath, OutlinkRefs Refs, string Value)>> byImage =
            new(StringComparer.OrdinalIgnoreCase);

        foreach ((string value, OutlinkRefs refs) in outlinks)
        {
            token.ThrowIfCancellationRequested();
            found.Examine("outlink.unresolved", refs.Count);

            string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int imgAt = Array.FindIndex(segments, s => s.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
            if (segments.Length == 0 || imgAt < 0)
            {
                Report(found, "outlink.malformed", AuditSeverity.Error, refs,
                    "The _outlink does not name an .img anywhere along its path, so there is nothing " +
                    "for the client to open.", value);
                continue;
            }

            string family = segments[0];
            string imagePath = string.Join('/', segments.Skip(1).Take(imgAt));
            string propPath = string.Join('/', segments.Skip(imgAt + 1));

            // A link into a _Canvas directory resolves for a tool and not for a
            // v232 client, which follows an outlink one level and stores art
            // inline. Separate finding, because the archive is not corrupt —
            // the link is simply beyond what this client can follow.
            found.Examine("outlink.into_canvas_dir", refs.Count);
            if (segments.Any(s => s.Equals("_Canvas", StringComparison.OrdinalIgnoreCase)))
            {
                Report(found, "outlink.into_canvas_dir", AuditSeverity.Warning, refs,
                    "The _outlink points into a _Canvas directory. A v232 client follows an outlink " +
                    "one level and expects the art inline, so this resolves in an editor and draws " +
                    "nothing in game.", value);
                continue;
            }

            /* A link into a family that was not part of this run is NOT a broken
               link, and calling it one would be the single fastest way to make
               the whole report untrustworthy: run the auditor on one archive and
               every outlink in it turns red. Say what was not looked at. */
            if (!audited.Contains(family))
            {
                found.Examine("outlink.family_absent", refs.Count);
                Report(found, "outlink.family_absent", AuditSeverity.Info, refs,
                    $"Points into the {family} family, which was not part of this run. Not checked — " +
                    "include that family to resolve it.", value);
                continue;
            }

            string key = family + "/" + imagePath;
            if (!index.TryGetValue(key, out List<ImageEntry>? holders) || holders.Count == 0)
            {
                Report(found, "outlink.unresolved", AuditSeverity.Error, refs,
                    $"No archive in the {family} family holds {imagePath}. The canvas has no pixels.",
                    value);
                continue;
            }

            ImageEntry winner = holders.OrderBy(h => h.MountOrder).First();
            string target = winner.Archive + "" + winner.Path;
            if (!byImage.TryGetValue(target, out List<(string, OutlinkRefs, string)>? list))
                byImage[target] = list = new List<(string, OutlinkRefs, string)>();
            list.Add((propPath, refs, value));
        }

        int done = 0;
        foreach ((string target, List<(string PropPath, OutlinkRefs Refs, string Value)> wants) in byImage)
        {
            token.ThrowIfCancellationRequested();
            if ((++done & 0xFF) == 0)
                lock (_gate) _progress.Phase = $"outlinks {done}/{byImage.Count}";

            string[] parts = target.Split('');
            if (!byStem.TryGetValue(parts[0], out WzFile? file)) continue;

            WzImage? image = FindImage(file.WzDirectory, parts[1]);
            if (image == null) continue;

            bool wasParsed = image.Parsed;
            try
            {
                if (!image.Parsed && !image.Changed && !image.ParseImage())
                {
                    foreach ((_, OutlinkRefs refs, string value) in wants)
                        Report(found, "outlink.unresolved", AuditSeverity.Error, refs,
                            $"The target image {parts[0]}.wz/{parts[1]} exists but will not parse.", value);
                    continue;
                }

                foreach ((string propPath, OutlinkRefs refs, string value) in wants)
                {
                    if (propPath.Length == 0) continue;    // the image itself is the target
                    if (image.GetFromPath(propPath) is WzImageProperty) continue;
                    Report(found, "outlink.unresolved", AuditSeverity.Error, refs,
                        $"{parts[0]}.wz/{parts[1]} exists but holds no '{propPath}'. The canvas has " +
                        "no pixels — this is the shape a link takes after its target was renamed, " +
                        "moved or ported without it.", value);
                }
            }
            catch (Exception ex)
            {
                foreach ((_, OutlinkRefs refs, string value) in wants)
                    Report(found, "outlink.unresolved", AuditSeverity.Error, refs,
                        $"Reading the target image {parts[0]}.wz/{parts[1]} threw: {ex.Message}", value);
            }
            finally
            {
                if (!wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                {
                    try { image.UnparseImage(); } catch { }
                }
            }
        }
    }

    private static void Report(Collector found, string check, AuditSeverity severity,
        OutlinkRefs refs, string detail, string value)
    {
        string where = refs.Examples.FirstOrDefault() ?? "(unknown)";
        string suffix = refs.Count > 1 ? $" {refs.Count:N0} canvases carry this same link." : "";
        found.Add(new AuditFinding(check, severity, where, detail + suffix, value));
    }

    private static WzImage? FindImage(WzDirectory root, string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        WzDirectory? dir = root;
        for (int i = 0; i < parts.Length - 1 && dir != null; i++)
            dir = dir.GetDirectoryByName(parts[i]);
        return dir?.GetImageByName(parts[^1]);
    }

    /* ---- the archive's own format vocabulary --------------------------- */

    /// <summary>
    /// An archive is written by one packer, so its canvases are written in a
    /// handful of formats and almost nothing else. A format that is perfectly
    /// legal, that the decoder knows, and that four canvases in two million use
    /// is not a rarity — it is the fingerprint of a tool that touched those four
    /// and re-encoded them. That is exactly the edit this project keeps making
    /// and keeps failing to see, and no per-node check can find it: it is only
    /// visible against the distribution of the archive it lives in.
    ///
    /// Deliberately separate from <c>canvas.format_unknown</c> and a step below
    /// it. Unknown means the decoder has no case for it; rare means the decoder
    /// is fine and the client may still be the only thing that ever drew a
    /// canvas that way. One is a defect, the other is a lead.
    ///
    /// Two guards keep it from being noise. An archive under
    /// <see cref="VocabularyFloor"/> canvases has no distribution to speak of
    /// and is skipped and said to be skipped. And a format only counts as
    /// outside the vocabulary below <see cref="VocabularyShare"/> of the
    /// archive, so a legitimately mixed archive reports nothing.
    /// </summary>
    private const long VocabularyFloor = 500;

    /// <summary>Below this share of an archive's canvases, a format is an outlier.</summary>
    private const double VocabularyShare = 0.001;

    private static void FormatVocabulary(Facts facts, Collector found, List<string> notChecked)
    {
        bool skipped = false;

        foreach ((string stem, Dictionary<int, long> histogram) in facts.FormatCounts
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            long total = histogram.Values.Sum();
            if (total < VocabularyFloor) { skipped = true; continue; }

            // The dominant vocabulary, named in the finding so the reader can
            // see what "normal" was rather than take the verdict on trust.
            string dominant = string.Join(", ", histogram
                .Where(kv => kv.Value >= total * VocabularyShare)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ({kv.Value * 100.0 / total:0.#}%)"));

            foreach ((int format, long count) in histogram.OrderBy(kv => kv.Key))
            {
                found.Examine("canvas.format_rare");
                if (count >= total * VocabularyShare) continue;

                // Already reported, and reported worse. Saying it twice would
                // make the Critical look like it had a duplicate.
                if (!KnownFormats.Contains(format)) continue;

                string example = facts.FormatExample.GetValueOrDefault(stem + "" + format,
                    stem + ".wz (no example recorded)");
                found.Add(new AuditFinding("canvas.format_rare", AuditSeverity.Warning, example,
                    $"Format {format} is used by {count:N0} of {stem}.wz's {total:N0} canvases " +
                    $"({count * 100.0 / total:0.###}%), against a vocabulary of {dominant}. The " +
                    "decoder accepts it, so this is not a corruption — it is the signature of a " +
                    "canvas that was re-encoded by something other than whatever packed the rest " +
                    "of the archive. Worth confirming those canvases still draw.",
                    $"format={format} count={count}"));
            }
        }

        if (skipped)
            notChecked.Add($"canvas format vocabulary: archives holding fewer than {VocabularyFloor} " +
                           "canvases were skipped. A handful of canvases has no dominant format to " +
                           "be an outlier from, and calling one of six canvases 'rare' would be a " +
                           "finding invented by the arithmetic.");
    }

    /* ---- cross-archive names ------------------------------------------ */

    private static void CrossNames(Facts facts, Dictionary<string, HashSet<NamedReference>> references,
        Collector found, List<string> notChecked)
    {
        /* action: a skill's action/0 names a character animation. If Character.wz
           has no such animation the client will not send the use-skill packet —
           the skill is silently unusable, which is exactly the "looks fine,
           game disagrees" failure this tool exists for. */
        if (!facts.SawCharacter)
        {
            notChecked.Add("action names: Character.wz was not in the folder, so no action was resolved.");
        }
        else if (facts.CharacterActions.Count == 0)
        {
            notChecked.Add("action names: Character.wz held no 0000200*.img body image, so the set of " +
                           "valid animation names is unknown and nothing was reported.");
        }
        else if (references.TryGetValue("action", out HashSet<NamedReference>? actions))
        {
            /* SCOPED TO Skill.wz, and that is a correction rather than a
               narrowing for tidiness.

               Unscoped, this check returned 1,046 findings on the v232 client and
               was wrong for nearly all of them, because it matched on the key name
               and a key name does not say what a value means. A reactor's `action`
               is the name of a SERVER script (Reactor.wz/…/action = "hiddenStreet"),
               and Item.wz/Cash/0501.img/…/effect/action = '1' is a FRAME INDEX.
               Neither is a character animation, neither is checkable from the
               archives alone, and both were being reported as broken clients.

               Only a skill's action/0 names an animation Character.wz has to
               define, and that subset — 67 on this client — is the one the finding
               can defend. The rest are counted and named as not judged rather than
               dropped silently: a check that quietly stopped looking at an archive
               is the other failure this tool has already had once. */
            int elsewhere = 0;
            HashSet<string> elsewhereFamilies = new(StringComparer.OrdinalIgnoreCase);

            foreach (NamedReference reference in actions)
            {
                if (!reference.Family.Equals("Skill", StringComparison.OrdinalIgnoreCase))
                {
                    elsewhere++;
                    elsewhereFamilies.Add(reference.Family);
                    continue;
                }

                found.Examine("action.unknown");
                if (facts.CharacterActions.Contains(reference.Value)) continue;
                found.Add(new AuditFinding("action.unknown", AuditSeverity.Error, reference.Path,
                    $"'{reference.Value}' is not an animation any Character.wz body image defines. A " +
                    "client will not play it, and for a skill it will refuse the skill.", reference.Value));
            }

            if (elsewhere > 0)
                notChecked.Add($"action names: {elsewhere:N0} distinct 'action' values outside Skill.wz " +
                               $"({string.Join(", ", elsewhereFamilies.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))}) " +
                               "were collected and deliberately NOT judged. Outside Skill.wz the key does " +
                               "not mean a character animation — a reactor's action is a server script " +
                               "name and an item effect's action is a frame index — so judging them " +
                               "against Character.wz's animation list produces confident nonsense. This " +
                               "check was doing exactly that until it was scoped.");
        }

        /* sound: the same treatment for sfx/sound values, but only where the
           value is a path into Sound.wz. A bare word ("Attack") is a name inside
           an entry this pass never opened, and reporting it would be a guess. */
        if (!facts.SawSound)
        {
            notChecked.Add("sound names: Sound.wz was not in the folder.");
        }
        else
        {
            foreach (string key in new[] { "sound", "sfx" })
            {
                if (!references.TryGetValue(key, out HashSet<NamedReference>? values)) continue;
                foreach (NamedReference reference in values)
                {
                    string value = reference.Value;
                    if (!value.Contains('/')) continue;     // not a path; not ours to judge
                    found.Examine("sound.unresolved");
                    string probe = value.Replace('\\', '/').Trim('/');
                    if (facts.SoundPaths.Contains(probe)) continue;
                    if (facts.SoundPaths.Contains(probe + ".img")) continue;
                    found.Add(new AuditFinding("sound.unresolved", AuditSeverity.Warning, reference.Path,
                        $"'{value}' names no entry in Sound.wz. The node plays silence.", value));
                }
            }
            notChecked.Add("sound names: only values containing a '/' are resolved against Sound.wz. " +
                           "A bare name is relative to an entry whose owner this pass does not know, " +
                           "and guessing at it would produce more noise than findings.");
        }
    }

    /* ---- String.wz against the data ------------------------------------ */

    /// <summary>
    /// Returns the per-kind counts as well as reporting.
    ///
    /// The counts are the point of the return value. These two checks fire tens
    /// of thousands of times, the report keeps a few hundred rows of each, and a
    /// reader looking at "39,902 orphans" has no way to tell one archive's
    /// missing half from a client where every kind is a little out of step. Six
    /// rows of arithmetic answer that; 300 truncated findings do not.
    /// </summary>
    private static List<AuditStringCoverage> StringVersusData(Facts facts, Collector found,
        List<string> notChecked)
    {
        List<AuditStringCoverage> coverage = new();

        if (!facts.SawString)
        {
            notChecked.Add("String.wz cross-check: String.wz was not in the folder.");
            return coverage;
        }

        void Compare(string kind, string image, bool sawData, HashSet<int> data, HashSet<int> named)
        {
            if (!sawData)
            {
                string why = $"the {kind} data archive was not in the folder, so String.wz/{image} was " +
                             "not compared against anything";
                notChecked.Add($"{kind}: {why}.");
                // Recorded with Compared=false rather than omitted. A kind that is
                // missing from this table reads as "clean" and a zero reads as
                // "clean"; the flag is what stops both from meaning the same thing.
                coverage.Add(new AuditStringCoverage(kind, image, false, why, named.Count, 0, 0, 0));
                return;
            }

            int orphans = 0, unnamed = 0;

            foreach (int id in named)
            {
                found.Examine("string.orphan_entry");
                if (data.Contains(id)) continue;
                orphans++;
                found.Add(new AuditFinding("string.orphan_entry", AuditSeverity.Warning,
                    $"String.wz/{image}/{id}",
                    $"String.wz names {kind} {id} and no archive defines it. Anything that lists by " +
                    "name — a search, a shop, a skill window — offers a row that resolves to nothing.",
                    id.ToString()));
            }

            foreach (int id in data)
            {
                found.Examine("data.unnamed_entry");
                if (named.Contains(id)) continue;
                unnamed++;
                found.Add(new AuditFinding("data.unnamed_entry", AuditSeverity.Warning,
                    $"{kind} {id}",
                    $"The data for {kind} {id} exists and String.wz/{image} does not name it. It shows " +
                    "as blank or as its id wherever the client prints a name.", id.ToString()));
            }

            coverage.Add(new AuditStringCoverage(kind, image, true, null,
                named.Count, data.Count, orphans, unnamed));
        }

        Compare("skill", "Skill.img", facts.SawSkill, facts.SkillIds, facts.StringSkillIds);
        Compare("mob", "Mob.img", facts.SawMob, facts.MobIds, facts.StringMobIds);
        Compare("npc", "Npc.img", facts.SawNpc, facts.NpcIds, facts.StringNpcIds);
        Compare("map", "Map.img", facts.SawMap, facts.MapIds, facts.StringMapIds);
        Compare("item", "Eqp/Consume/Ins/Etc/Cash.img",
            facts.SawItem || facts.SawCharacter, facts.ItemIds, facts.StringItemIds);

        notChecked.Add("String.wz cross-check covers skills, mobs, NPCs, maps and items by id. " +
                       "Quest, Reactor, Morph and the UI strings are not compared: their String rows " +
                       "are not keyed by an id that names a data node, and a rule invented for them " +
                       "would report a client that is fine.");

        return coverage;
    }

    /* ====================================================================
       CATALOG — every check, named, so the UI can list what ran
       ==================================================================== */

    private sealed record CheckSpec(string Id, string Title, AuditSeverity Severity, string? NotChecked = null);

    private static IEnumerable<CheckSpec> Catalog() => new[]
    {
        new CheckSpec("archive.parse_failed", "Archive will not open", AuditSeverity.Critical),
        new CheckSpec("image.parse_failed", "Image will not parse", AuditSeverity.Critical),
        new CheckSpec("family.shadowed_image", "Same image name twice in a family, different bytes", AuditSeverity.Critical),
        new CheckSpec("canvas.format_unknown", "Canvas format outside the client's vocabulary", AuditSeverity.Critical),
        new CheckSpec("canvas.row_width_zero", "Canvas format/mag implies a row of no pixels", AuditSeverity.Critical),
        new CheckSpec("outlink.unresolved", "_outlink resolves to nothing", AuditSeverity.Error),
        new CheckSpec("outlink.malformed", "_outlink names no image", AuditSeverity.Error),
        new CheckSpec("inlink.unresolved", "_inlink resolves to nothing", AuditSeverity.Error),
        new CheckSpec("uol.unresolved", "UOL dangles", AuditSeverity.Error),
        new CheckSpec("canvas.empty_blob", "Canvas with no link and no pixels", AuditSeverity.Error),
        new CheckSpec("canvas.zero_dimension", "Canvas measuring zero", AuditSeverity.Error),
        new CheckSpec("blob.empty", "Sound declaring a duration of zero", AuditSeverity.Error,
            "Measured from the declared duration only. The compressed byte count is on an internal " +
            "field of WzBinaryProperty that this assembly cannot read, so a sound with a real " +
            "duration and no bytes behind it is not detected."),
        new CheckSpec("action.unknown", "Skill action naming no Character.wz animation", AuditSeverity.Error,
            "Skill.wz only, deliberately. Unscoped this fired 1,046 times on the v232 client and was " +
            "wrong nearly every time: a reactor's `action` is a server script name and an item " +
            "effect's `action` is a frame index. Neither is a character animation and neither is " +
            "decidable from the archives, so they are counted under 'not checked' instead."),
        new CheckSpec("outlink.into_canvas_dir", "_outlink into _Canvas a v232 client cannot follow", AuditSeverity.Warning),
        new CheckSpec("canvas.format_rare", "Canvas format outside its own archive's vocabulary",
            AuditSeverity.Warning,
            $"Only archives holding at least {VocabularyFloor} canvases are judged, and a format has " +
            $"to fall under {VocabularyShare:P1} of them to count as an outlier. This finds a canvas " +
            "re-encoded by a different tool; it does not prove that canvas is broken."),
        new CheckSpec("canvas.both_links", "Canvas carrying both _inlink and _outlink", AuditSeverity.Warning),
        new CheckSpec("sound.unresolved", "Sound path absent from Sound.wz", AuditSeverity.Warning),
        new CheckSpec("string.orphan_entry", "String.wz names something no archive defines", AuditSeverity.Warning),
        new CheckSpec("data.unnamed_entry", "Data with no String.wz name", AuditSeverity.Warning),
        new CheckSpec("image.empty", "Image that parses and holds nothing", AuditSeverity.Warning),
        new CheckSpec("family.duplicate_image", "Same image name twice, identical bytes", AuditSeverity.Info),
        new CheckSpec("uol.escapes_image", "UOL climbing out of its image (not resolved)", AuditSeverity.Info),
        new CheckSpec("archive.manifest", "Archive that lists other archives, so it was never opened",
            AuditSeverity.Info,
            "A manifest's own images are not audited. Reading its root is what gives this run its mount " +
            "order; opening it means following directory entries that point at nothing, which is the " +
            "crash this gate exists to avoid."),
        new CheckSpec("archive.stub", "File whose root directory does not decode, so it was never opened",
            AuditSeverity.Info,
            "The gate is the root entry list, read forward-only and bounds-checked before anything is " +
            "handed to the recursive directory parser. It validates the ROOT only: an archive with a " +
            "good root and a corrupt sub-directory would still reach the parser, and a stack overflow " +
            "there cannot be caught."),
        new CheckSpec("uol.root_relative", "UOL that only resolves from the image root", AuditSeverity.Info),
        new CheckSpec("outlink.family_absent", "_outlink into a family this run did not include", AuditSeverity.Info),
    };

    private static AuditProgress Clone(AuditProgress p) => new()
    {
        State = p.State, Phase = p.Phase, Archive = p.Archive,
        ArchivesDone = p.ArchivesDone, ArchivesTotal = p.ArchivesTotal,
        ImagesDone = p.ImagesDone, Findings = p.Findings, Error = p.Error, Seconds = p.Seconds,
    };
}
