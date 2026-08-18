using System.Globalization;
using System.Security.Cryptography;
using MapleBench.Models;
using MapleLib.WzLib;

namespace MapleBench.Services.Composition;

/// <summary>
/// Reads and writes the ledger that sits beside a composed client, and turns a
/// port result into the record of one take.
///
/// Two jobs in one type because they are the same job seen from either end: the
/// only thing that may write a ledger entry is a port that actually happened,
/// and the only thing a ledger entry may say is what that port's recomputed
/// result says. Keeping the translation here means there is no second place
/// where a record could be composed from intentions instead of outcomes — which
/// is the failure the port itself already had once, reporting
/// <c>written 5, failed 0</c> for parts absent from the saved file.
/// </summary>
public sealed class CompositionLedgerStore
{
    private readonly WzSessionService _session;

    public CompositionLedgerStore(WzSessionService session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>The ledger file for a client folder.</summary>
    public static string PathFor(string clientFolder) =>
        System.IO.Path.Combine(clientFolder, CompositionSchema.LedgerFileName);

    /// <summary>
    /// The ledger beside this client, or null when there is none.
    ///
    /// Null and "a ledger with no takes" are different answers and both are
    /// real: the first means nobody has recorded anything about this client, the
    /// second means a build ran and took nothing. A caller that collapsed them
    /// would report an unexplained client as an empty composition.
    /// </summary>
    public static CompositionLedger? Read(string clientFolder)
    {
        string path = PathFor(clientFolder);
        if (!File.Exists(path))
            return null;

        return CompositionLedger.FromJson(File.ReadAllText(path));
    }

    /// <summary>Writes the ledger beside the client, replacing what was there.</summary>
    public static void Write(string clientFolder, CompositionLedger ledger)
    {
        Directory.CreateDirectory(clientFolder);
        File.WriteAllText(PathFor(clientFolder), ledger.ToJson());
    }

    /// <summary>
    /// Appends one take to the ledger beside a client, creating the file when
    /// there is none.
    ///
    /// This is what makes an interactively composed client explicable. It is a
    /// weaker promise than a build's — an appended ledger records what happened,
    /// where a built one also asserts it can happen again — and
    /// <see cref="CompositionLedger.Origin"/> keeps the two apart rather than
    /// letting a pile of one-off imports read as a reproducible composition.
    /// </summary>
    public static CompositionLedger Append(string clientFolder, LedgerTake take)
    {
        if (take == null)
            throw new ArgumentNullException(nameof(take));

        CompositionLedger ledger = Read(clientFolder) ?? new CompositionLedger
        {
            Output = clientFolder,
            Origin = "interactive",
            When = Now(),
        };

        take.Sequence = ledger.Takes.Count + 1;
        ledger.Takes.Add(take);
        Write(clientFolder, ledger);
        return ledger;
    }

    /// <summary>An ISO-8601 UTC stamp, which is the only time format this writes.</summary>
    public static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// SHA-256 of a file's bytes — the identity a manifest pins an archive by.
    ///
    /// Streamed, because these are gigabyte files and this is called once per
    /// archive per build.
    /// </summary>
    public static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// A session path — <c>f3/Cap/01002357.img</c> — as the archive-relative path
    /// a manifest and a ledger speak in: <c>Character.wz/Cap/01002357.img</c>.
    ///
    /// The file id is the thing that must not survive into a record. It is handed
    /// out in open order and means nothing in the next process, so a ledger that
    /// stored one would describe a different node tomorrow while looking
    /// identical — the exact class of silent drift a provenance record exists to
    /// rule out.
    /// </summary>
    public string? Relative(string? sessionPath)
    {
        if (string.IsNullOrEmpty(sessionPath))
            return null;

        string fileId = WzPath.FileId(sessionPath);
        if (fileId.Length == 0)
            return sessionPath;

        OpenFile? file = _session.TryGetFile(fileId);
        if (file == null)
            return sessionPath;

        string rest = sessionPath.Length > fileId.Length ? sessionPath[(fileId.Length + 1)..] : "";
        return rest.Length == 0 ? file.Name : file.Name + "/" + rest;
    }

    /// <summary>
    /// The record of one applied port: what it took, what it renamed, what it
    /// refused, and the counts recomputed at apply time.
    ///
    /// Everything here is read off <paramref name="result"/> and nothing is
    /// inferred from the request. A part is recorded as taken only when the port
    /// says it was <see cref="PortPartDto.Applied"/> — not when it said it
    /// <see cref="PortPartDto.WillWrite"/>, which is an intention.
    /// </summary>
    /// <param name="hashParts">
    /// Compute a <see cref="WzContentHasher"/> digest for each landed node. Off
    /// by default: it is a second full parse of everything that moved, which a
    /// whole-archive port cannot afford. When it is off every
    /// <see cref="LedgerPart.ContentHash"/> is null and
    /// <see cref="LedgerTake.PartsHashed"/> is false, so a reader can always tell
    /// "not computed" from "no content".
    /// </param>
    public LedgerTake Record(
        CompositionSource source,
        CompositionTake take,
        PortResultDto result,
        bool hashParts = false)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (take == null)
            throw new ArgumentNullException(nameof(take));
        if (result == null)
            throw new ArgumentNullException(nameof(result));

        PortPlanDto plan = result.Plan;

        LedgerTake record = new()
        {
            Source = source,
            Kind = take.Kind,
            Scope = take.Scope,
            Into = take.Into,
            Requested = new List<string>(take.Take),
            Options = take.Options,
            Note = take.Note,
            Written = result.Written,
            Failed = result.Failed,
            Skipped = result.Skipped,
            PartsHashed = hashParts,
            When = Now(),
            Seconds = result.Seconds,
        };

        if (plan.Blocked && plan.BlockedReason != null)
        {
            record.Refused.Add(new LedgerRefusal
            {
                Class = "blocked",
                What = take.Take.Count == 1 ? take.Take[0] : $"{take.Kind} · {take.Take.Count} selected",
                Why = plan.BlockedReason,
            });
        }

        foreach (string warning in plan.Warnings)
            record.Refused.Add(new LedgerRefusal { Class = "warning", What = take.Into, Why = warning });

        foreach (PortItemDto item in plan.Items)
        {
            foreach (PortPartDto part in item.Parts)
            {
                string? from = Relative(part.SourcePath);
                string? to = Relative(part.TargetPath);

                if (part.Applied)
                {
                    record.Took.Add(new LedgerPart
                    {
                        Kind = part.Kind,
                        From = from,
                        To = to,
                        SourceArchive = part.SourceArchive,
                        Bytes = part.Bytes,
                        ContentHash = hashParts ? HashLanded(part.TargetPath) : null,
                    });

                    // A rename is visible in the two paths and nowhere else: the
                    // port renames by landing the node under a different leaf.
                    // Reading it off the paths rather than off a flag is what
                    // keeps this record honest about what actually happened.
                    string? was = Leaf(from);
                    string? now = Leaf(to);
                    if (was != null && now != null && !string.Equals(was, now, StringComparison.Ordinal))
                    {
                        record.Renamed.Add(new LedgerRename
                        {
                            From = was,
                            To = now,
                            At = to,
                            Why = part.Reason ?? $"'{was}' is already taken in {take.Into} by something else.",
                        });
                    }
                    continue;
                }

                if (part.Error != null)
                {
                    record.Refused.Add(new LedgerRefusal
                    {
                        Class = "failed",
                        What = from ?? to ?? part.Label,
                        Why = part.Error,
                    });
                    continue;
                }

                // "Same" is not a refusal — the target already holds this exact
                // content and copying it would move bytes for no change. It is
                // recorded nowhere rather than as a loss.
                if (string.Equals(part.Status, "Same", StringComparison.Ordinal))
                    continue;

                if (part.Reason != null || !string.Equals(part.Status, "New", StringComparison.Ordinal))
                {
                    record.Refused.Add(new LedgerRefusal
                    {
                        Class = part.Status.ToLowerInvariant(),
                        What = from ?? to ?? part.Label,
                        Why = part.Reason
                              ?? $"{part.Status}: the port did not write this part and gave no reason, which "
                               + "is itself worth knowing.",
                    });
                }
            }
        }

        return record;
    }

    private string? HashLanded(string? targetPath)
    {
        if (string.IsNullOrEmpty(targetPath))
            return null;

        try
        {
            WzObject? node = _session.TryResolve(targetPath);
            return node == null ? null : WzContentHasher.Hash(node);
        }
        catch (InvalidOperationException)
        {
            // The hasher refuses rather than truncating on a self-containing or
            // pathologically deep node. That refusal is its answer, and a ledger
            // that turned it into a crash would make one bad node unrecordable.
            return null;
        }
    }

    private static string? Leaf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
