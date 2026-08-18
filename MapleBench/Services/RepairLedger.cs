using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapleBench.Services;

/* ============================================================================
   REPAIR LEDGER — what has already been done to this archive, and what else was
   built from it

   THE ACCIDENT THIS EXISTS TO STOP, MEASURED RATHER THAN IMAGINED

   Two repairs were built for the same client on the same day, each from the
   same pristine `Skill.wz` (SHA-256 ea6ed129…):

     Skill.repaired.wz   283 split canvas formats re-joined
     Skill.restored.wz   347 dangling `_outlink` targets carried back from donors

   Both are correct. Both were verified. And installing one and then the other
   silently reverts the first, because each is a WHOLE 2 GB archive written from
   the same starting point — the second copy does not merge with the first, it
   replaces it. Nothing in either repair could see the other: a repair knew its
   input path and its own counts, and nothing recorded what the input WAS.

   THE FIX IS AN IDENTITY, NOT A CONVENTION

   Every repair now hashes the file it reads and the file it writes, and records
   the chain beside the output as `<output>.repairs.json` plus a copy in a
   machine-local registry. A later repair asks three questions before it writes,
   in this order:

     1. Does this input carry a ledger, and does the ledger describe THIS file?
        Yes and yes  -> COMPOSING. The chain is carried forward and the new pass
                        is appended, so the output can say "283 formats and 347
                        restores", which is a claim no single pass could make.
        Yes and no   -> STALE. A ledger sits beside an archive it does not
                        describe: the file was replaced or edited by something
                        that did not update it. Nothing here knows what is in it,
                        so it REFUSES rather than composing onto a fiction.
     2. Is there no ledger at all? Then the input is PRISTINE as far as anything
        can tell, which is the ordinary first-repair case.
     3. Has a DIFFERENT pass already written an archive from this exact input?
        That is the accident above, and it is caught by content, not by path:
        the registry is keyed on the source hash, so it fires even when the two
        runs read two copies of the same file from two different folders. The
        answer is a refusal with the only instruction that composes them — run
        this pass against the OTHER pass's output instead.

   The same pass re-run from the same input is not a conflict. Rebuilding an
   output you deleted, or rebuilding it with different options, produces an
   archive that supersedes the earlier one rather than fighting it, and that is
   said in the notes.

   WHAT IS HASHED, AND WHY IT IS THE FILE

   <see cref="WzContentHasher"/> is the right hash for asking whether two NODES
   are the same content across archives and keys. It is the wrong hash for this
   question: it parses every image and reads every payload under the node, which
   over a 2 GB archive is a pass measured in minutes, and it deliberately ignores
   exactly what matters here — the bytes a save actually wrote. What this needs
   is "is this the same FILE the ledger was written for", and SHA-256 over the
   stream answers it in seconds and cannot be fooled by a re-save.

   NOTHING HERE INSTALLS, DELETES OR MODIFIES AN ARCHIVE. It writes small JSON
   beside an output and under LocalAppData, and it reads.
   ============================================================================ */

/// <summary>What a repair's input turned out to be.</summary>
public enum RepairInputVerdict
{
    /// <summary>No ledger anywhere names this file. An ordinary first repair.</summary>
    Pristine = 0,

    /// <summary>A ledger describes this exact file. The new pass composes onto its chain.</summary>
    Composing = 1,

    /// <summary>
    /// A ledger sits beside this file and describes a different one. Refused:
    /// composing onto a record that does not match the bytes would produce an
    /// archive claiming repairs it may not carry.
    /// </summary>
    Stale = 2,
}

/// <summary>One repair pass, as it was applied.</summary>
public sealed record RepairPassRecord(
    string Pass,
    DateTimeOffset WrittenUtc,
    string Source,
    string SourceHash,
    long SourceBytes,
    string Output,
    string OutputHash,
    long OutputBytes,
    int Changed,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Notes);

/// <summary>
/// The chain of repairs an archive carries, and the pristine file at the head of
/// it. Written as <c>&lt;archive&gt;.repairs.json</c> beside the archive.
/// </summary>
public sealed record RepairLedgerFile(
    int Version,
    string Archive,
    string ArchiveHash,
    long ArchiveBytes,
    string Origin,
    string OriginHash,
    IReadOnlyList<RepairPassRecord> Passes);

/// <summary>Another archive built from the same input by a different pass.</summary>
public sealed record RepairSibling(
    string Pass,
    string Output,
    string OutputHash,
    DateTimeOffset WrittenUtc,
    int Changed,
    bool Exists);

/// <summary>
/// What an input is, before a pass writes anything. Every field is a fact the
/// caller can check rather than a verdict it has to trust.
/// </summary>
public sealed record RepairInputCheck(
    string Path,
    string Hash,
    long Bytes,
    RepairInputVerdict Verdict,
    RepairLedgerFile? Ledger,
    IReadOnlyList<string> AlreadyApplied,
    IReadOnlyList<RepairSibling> BuiltFromTheSameInput,
    IReadOnlyList<RepairSibling> ConflictsWith,
    string Why)
{
    /// <summary>Whether a pass may proceed. False means the reason is in <see cref="Why"/>.</summary>
    [JsonIgnore]
    public bool MayProceed => Verdict != RepairInputVerdict.Stale && ConflictsWith.Count == 0;
}

/// <summary>
/// Reads and writes the repair chain. See the header for why it exists.
/// </summary>
public static class RepairLedger
{
    public const string CanvasFormatPass = "canvas-format";
    public const string DonorRestorePass = "donor-restore";
    public const string CanvasDirInlinePass = "canvas-dir-inline";

    /// <summary>The ledger format. Bumped when a reader could misread an older file.</summary>
    public const int FormatVersion = 1;

    /// <summary>The sidecar's suffix. Appended to the whole file name, so `Skill.wz` gets `Skill.wz.repairs.json`.</summary>
    public const string SidecarSuffix = ".repairs.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string? _registryRoot;

    /// <summary>
    /// Where the registry lives. Settable so a test does not write into the
    /// user's profile, and read from <c>MAPLEBENCH_REPAIR_LEDGER</c> so a
    /// machine with several clients can keep them apart.
    ///
    /// The registry is what makes the cross-directory case work. A sidecar
    /// travels with the file only while somebody copies it along; the accident
    /// this exists to stop happened between two runs in two different folders,
    /// and a lookup keyed on the source's CONTENT finds it either way.
    /// </summary>
    public static string RegistryRoot
    {
        get => _registryRoot ??=
            Environment.GetEnvironmentVariable("MAPLEBENCH_REPAIR_LEDGER") is { Length: > 0 } fromEnv
                ? fromEnv
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MapleBench", "repair-ledger");
        set => _registryRoot = value;
    }

    /* ====================================================================
       READING
       ==================================================================== */

    /// <summary>
    /// SHA-256 of a file, as 64 lowercase hex characters. Streamed, so a 2 GB
    /// archive costs a read and not a copy of itself in memory.
    /// </summary>
    public static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 1 << 20, FileOptions.SequentialScan);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>The sidecar path for an archive.</summary>
    public static string SidecarFor(string archive) => archive + SidecarSuffix;

    /// <summary>
    /// What this input is. Reads; writes nothing.
    /// </summary>
    /// <param name="path">The archive a pass is about to read.</param>
    /// <param name="pass">The pass about to run, so a re-run is told apart from a fork.</param>
    public static RepairInputCheck Inspect(string path, string pass)
    {
        string full = Path.GetFullPath(path);
        long bytes = new FileInfo(full).Length;
        string hash = HashFile(full);

        RepairLedgerFile? ledger = null;
        RepairInputVerdict verdict = RepairInputVerdict.Pristine;
        string why;

        string sidecar = SidecarFor(full);
        RepairLedgerFile? beside = Read(sidecar);

        if (beside != null && !beside.ArchiveHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            verdict = RepairInputVerdict.Stale;
            why = $"{Path.GetFileName(sidecar)} sits beside this archive and describes a different file " +
                  $"(it records {beside.ArchiveHash[..12]}…, the file on disk is {hash[..12]}…). The archive " +
                  "has been replaced or edited by something that did not update its ledger, so what has " +
                  "already been done to it is not known. Nothing was written. Delete the stale ledger if the " +
                  "archive is genuinely unrepaired, or repair the file the ledger describes.";
        }
        else if (beside != null)
        {
            ledger = beside;
            verdict = RepairInputVerdict.Composing;
            why = Composing(beside, "its ledger");
        }
        else if (ByOutput(hash) is { } found)
        {
            // The sidecar did not travel with the file — an install copies the
            // archive and leaves the JSON behind — but the registry knows this
            // content. Composing anyway is the whole point of keying on content.
            ledger = found;
            verdict = RepairInputVerdict.Composing;
            why = Composing(found, "the repair registry, which recognised its contents even though no " +
                                   "ledger travelled with the file");
        }
        else
        {
            why = "No repair has recorded an output with these contents, so this is being treated as an " +
                  "unrepaired archive. Whatever this pass writes will carry a ledger, and the next pass " +
                  "will compose onto it rather than start again.";
        }

        List<RepairSibling> fromSameInput = BySource(hash);
        List<RepairSibling> conflicts = fromSameInput
            .Where(s => !s.Pass.Equals(pass, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (conflicts.Count > 0)
        {
            string names = string.Join(", ", conflicts.Select(c => $"{c.Pass} -> {Path.GetFileName(c.Output)}"));
            why +=
                $" REFUSED: {conflicts.Count} other repair(s) have already written a whole archive from this " +
                $"exact input ({names}). Each of those is a complete copy of this archive, so installing " +
                "this pass's output as well would not add to them — it would replace one of them and " +
                "silently undo it. Run this pass against " +
                (conflicts.Count == 1
                    ? $"'{conflicts[0].Output}' instead"
                    : "the newest of those outputs instead, then the next") +
                ", which composes the repairs into one archive. Pass acceptSeparateRepairs=true only if you " +
                "want a deliberately separate variant and understand that just one of them can be installed.";
        }
        else if (fromSameInput.Count > 0)
        {
            why += $" {fromSameInput.Count} earlier run(s) of this same pass built from this input " +
                   $"({string.Join(", ", fromSameInput.Select(s => Path.GetFileName(s.Output)))}); this run " +
                   "supersedes them rather than conflicting with them.";
        }

        return new RepairInputCheck(
            full, hash, bytes, verdict, ledger,
            ledger?.Passes.Select(p => p.Pass).ToList() ?? new List<string>(),
            fromSameInput, conflicts, why);
    }

    private static string Composing(RepairLedgerFile ledger, string how)
    {
        string passes = string.Join(", ", ledger.Passes.Select(p => $"{p.Pass} ({p.Changed:N0})"));
        return $"This archive is itself the output of {ledger.Passes.Count} earlier repair pass(es) — " +
               $"{passes} — according to {how}. They started from {Path.GetFileName(ledger.Origin)} " +
               $"({ledger.OriginHash[..12]}…). This pass composes onto that chain: what it writes carries " +
               "both, and neither undoes the other.";
    }

    /* ====================================================================
       WRITING
       ==================================================================== */

    /// <summary>
    /// Records a finished pass beside <paramref name="output"/> and in the
    /// registry, and returns the chain the output now carries.
    ///
    /// Called after the file is on disk, because the hash it records is the hash
    /// of what was actually written.
    /// </summary>
    public static RepairLedgerFile Record(
        RepairInputCheck input, string pass, string output,
        int changed, IReadOnlyList<string> paths, IReadOnlyList<string> notes)
    {
        string full = Path.GetFullPath(output);
        long bytes = new FileInfo(full).Length;
        string hash = HashFile(full);

        RepairPassRecord record = new(
            pass, DateTimeOffset.UtcNow,
            input.Path, input.Hash, input.Bytes,
            full, hash, bytes,
            changed, paths, notes);

        List<RepairPassRecord> passes = new(input.Ledger?.Passes ?? Array.Empty<RepairPassRecord>()) { record };

        RepairLedgerFile ledger = new(
            FormatVersion,
            Path.GetFileName(full), hash, bytes,
            input.Ledger?.Origin ?? input.Path,
            input.Ledger?.OriginHash ?? input.Hash,
            passes);

        Write(SidecarFor(full), ledger);
        Write(Path.Combine(RegistryRoot, "by-output", hash + ".json"), ledger);
        Write(Path.Combine(RegistryRoot, "by-source", input.Hash, $"{Safe(pass)}-{hash[..16]}.json"), ledger);
        return ledger;
    }

    /* ====================================================================
       THE FILES
       ==================================================================== */

    private static RepairLedgerFile? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            RepairLedgerFile? ledger = JsonSerializer.Deserialize<RepairLedgerFile>(File.ReadAllText(path), Json);
            // A ledger with no passes cannot say anything, and a version this
            // build does not know may mean something else entirely. Both are
            // treated as absent rather than as evidence.
            return ledger is { Version: FormatVersion, Passes.Count: > 0 } ? ledger : null;
        }
        catch (Exception)
        {
            // A ledger that will not parse is not a reason to refuse a repair —
            // it is the same information as no ledger, and the pass writes a
            // fresh one over it.
            return null;
        }
    }

    private static void Write(string path, RepairLedgerFile ledger)
    {
        string folder = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, JsonSerializer.Serialize(ledger, Json));
    }

    private static RepairLedgerFile? ByOutput(string hash) =>
        Read(Path.Combine(RegistryRoot, "by-output", hash + ".json"));

    /// <summary>Everything the registry knows that was built from an input with this hash.</summary>
    private static List<RepairSibling> BySource(string hash)
    {
        List<RepairSibling> siblings = new();
        string folder = Path.Combine(RegistryRoot, "by-source", hash);
        if (!Directory.Exists(folder)) return siblings;

        foreach (string file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Read(file) is not { } ledger) continue;
            RepairPassRecord last = ledger.Passes[^1];
            if (!last.SourceHash.Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
            siblings.Add(new RepairSibling(
                last.Pass, last.Output, last.OutputHash, last.WrittenUtc, last.Changed,
                File.Exists(last.Output)));
        }
        return siblings;
    }

    private static string Safe(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
