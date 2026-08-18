using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapleBench.Services.Composition;

/// <summary>
/// The two schema stamps, written into every file this namespace produces.
///
/// They are versioned strings rather than an integer because the reader must be
/// able to say "this is a composition manifest from a newer build" and "this is
/// not a composition manifest at all" as two different sentences. A file that
/// merely fails to bind would read as the second when it is the first.
/// </summary>
public static class CompositionSchema
{
    public const string Manifest = "maplebench/composition@1";
    public const string Ledger = "maplebench/ledger@1";

    /// <summary>The name a manifest gets when nothing else was asked for.</summary>
    public const string ManifestFileName = "composition.json";

    /// <summary>
    /// The name a ledger gets, beside the client it describes.
    ///
    /// Beside, not inside: a WZ archive is the client's data and a ledger is a
    /// statement about where that data came from. Putting it in the archive
    /// would change the bytes the game reads, and would be lost the first time
    /// someone re-saved the archive from another tool.
    /// </summary>
    public const string LedgerFileName = "composition-ledger.json";

    /// <summary>
    /// One JSON shape for everything here: indented so it can be read and
    /// hand-edited, camelCase because that is what the rest of this app's wire
    /// shapes use, and nulls kept so an absent field and a field that was
    /// deliberately left empty do not look the same on the page.
    ///
    /// <see cref="JsonSerializerOptions.WriteIndented"/> and a fixed property
    /// order are what make the output reproducible: two builds of the same
    /// manifest must produce the same ledger text, byte for byte, apart from the
    /// timings that <see cref="CompositionLedger.Digest"/> excludes.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

#region Manifest

/// <summary>
/// One archive of a client, named and pinned by the content of the file.
///
/// <see cref="Sha256"/> is over the bytes on disk, not over the parsed tree, and
/// that is the right identity for this job: it is what makes a manifest survive
/// the client folder being moved, renamed or copied, which is the ordinary case
/// — the same v232 client lives at three paths on this machine already. It is a
/// deliberately different question from <see cref="WzContentHasher"/>, which
/// identifies a <em>node's content</em> across archives with different keys.
/// A file hash answers "is this the same file"; a content hash answers "is this
/// the same thing". A composition needs both, in those two places.
///
/// Null means the archive was never pinned. That is allowed — a manifest written
/// by hand should not have to carry hashes — but it is reported as an unpinned
/// input rather than passed over, because an unverified base is exactly how a
/// reproducible build stops being reproducible without anybody noticing.
/// </summary>
public sealed class CompositionArchive
{
    /// <summary>The file name inside the client folder: <c>Mob.wz</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the file's bytes, lowercase hex, or null when unpinned.</summary>
    public string? Sha256 { get; set; }

    /// <summary>GMS / EMS / BMS / CLASSIC / null to let the opener detect it.</summary>
    public string? MapleVersion { get; set; }

    /// <summary>-1 lets MapleLib brute-force the version stamp, as an ordinary open does.</summary>
    public int GameVersion { get; set; } = -1;

    public CompositionArchive() { }

    public CompositionArchive(string name, string? sha256 = null)
    {
        Name = name;
        Sha256 = sha256;
    }
}

/// <summary>
/// A client taking part in a composition: the pristine base, or a donor.
///
/// <see cref="Folder"/> is where it was last seen, and it is a hint, not the
/// identity — see <see cref="CompositionArchive.Sha256"/>. A build that finds
/// the folder gone says so by name; a build that finds a different client there
/// says so by hash, which is the failure that would otherwise be silent.
/// </summary>
public sealed class CompositionSource
{
    /// <summary>The handle a take names this source by: <c>v233</c>. Unique within a manifest.</summary>
    public string Id { get; set; } = "";

    /// <summary>What a person calls it. Never load-bearing.</summary>
    public string Label { get; set; } = "";

    /// <summary>The client folder as last seen.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The archives of this client the build opens. Only these; nothing is guessed.</summary>
    public List<CompositionArchive> Archives { get; set; } = new();

    /// <summary>The archive of that name, or null.</summary>
    public CompositionArchive? Archive(string name) =>
        Archives.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The port options a take carries, mirroring <c>PortPlanRequest</c>.
///
/// Spelled out here rather than referenced so a manifest is a document rather
/// than a serialised request object: someone editing one by hand must be able to
/// see every decision that was taken, including the ones that were left at their
/// default. The defaults match the port's own, so an omitted block means "the
/// ordinary, careful port".
/// </summary>
public sealed class CompositionOptions
{
    public bool FollowLinks { get; set; } = true;
    public bool IncludeArtOutlinks { get; set; } = true;
    public bool Overwrite { get; set; }
    public bool Match { get; set; }
    public bool AcceptDeadCanvasLinks { get; set; }
    public bool AcceptMissingNames { get; set; }
}

/// <summary>
/// One contribution: everything taken from one source, in one action.
///
/// A take is the unit the manifest is ordered by, and order is meaningful — a
/// later take may overwrite an earlier one, and the ledger records that it did.
/// </summary>
public sealed class CompositionTake
{
    /// <summary>The <see cref="CompositionSource.Id"/> this comes from.</summary>
    public string From { get; set; } = "";

    /// <summary>The port kind: <c>mob</c>, <c>item</c>, <c>map</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>selection | folder | archive.</summary>
    public string Scope { get; set; } = "selection";

    /// <summary>The output archive it lands in, by file name: <c>Mob.wz</c>.</summary>
    public string Into { get; set; } = "";

    /// <summary>
    /// What to take, as archive-relative paths — <c>Mob.wz/8800100.img</c>.
    ///
    /// Archive-relative and not session paths. A session path begins with a file
    /// id (<c>f3/…</c>) that is handed out in open order and means nothing in the
    /// next process; a manifest that stored one would rebuild a different
    /// composition tomorrow while looking identical.
    /// </summary>
    public List<string> Take { get; set; } = new();

    /// <summary>The source archive, for the whole-archive scope. Null otherwise.</summary>
    public string? FromArchive { get; set; }

    public CompositionOptions Options { get; set; } = new();

    /// <summary>A free-text reason this take exists. Carried into the ledger verbatim.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// A composition, declaratively: a pristine base plus an ordered list of takes.
///
/// This is the whole idea. The base is never mutated; the output is produced
/// from it; re-running rebuilds rather than layers. "Undo a contribution" is
/// "delete a take and build again", and that is a property of the shape, not of
/// anybody's discipline.
/// </summary>
public sealed class CompositionManifest
{
    public string Schema { get; set; } = CompositionSchema.Manifest;

    /// <summary>What this composition is called.</summary>
    public string Name { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>The pristine client every build starts from. Opened read-only; never written.</summary>
    [JsonPropertyName("base")]
    public CompositionSource Base { get; set; } = new();

    public List<CompositionSource> Sources { get; set; } = new();

    /// <summary>The contributions, in the order they are applied. Order is meaningful.</summary>
    public List<CompositionTake> Takes { get; set; } = new();

    public CompositionSource? Source(string id) =>
        Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public string ToJson() => JsonSerializer.Serialize(this, CompositionSchema.Json);

    /// <summary>
    /// Reads a manifest, and refuses a file that is not one rather than binding
    /// a default-valued object out of it. A manifest with no base and no takes
    /// builds a copy of nothing and reports success, which is the worst possible
    /// answer to "you pointed me at the wrong file".
    /// </summary>
    public static CompositionManifest FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The composition manifest is empty.");

        CompositionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CompositionManifest>(json, CompositionSchema.Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"This is not readable JSON: {ex.Message}", ex);
        }

        if (manifest == null)
            throw new InvalidOperationException("The composition manifest is empty.");

        if (!string.Equals(manifest.Schema, CompositionSchema.Manifest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{manifest.Schema}' is not a composition manifest this build understands. "
                + $"Expected '{CompositionSchema.Manifest}'.");
        }

        return manifest;
    }
}

#endregion

#region Ledger

/// <summary>
/// One node that moved: where it came from, where it landed, and what it is.
///
/// <see cref="ContentHash"/> is the <see cref="WzContentHasher"/> digest of what
/// landed, when the build was asked to compute one. Null means it was not
/// computed, and <see cref="LedgerTake.PartsHashed"/> says which of the two
/// reasons that is — hashing every part of a whole-archive port is a second full
/// parse of the archive, so it is a choice, and a null that could mean either
/// "no content" or "not looked at" is the ambiguity this project keeps paying
/// for.
/// </summary>
public sealed class LedgerPart
{
    /// <summary>container | entry | link-entry | string | sound | named | shop | quest-*.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Archive-relative source path: <c>Mob.wz/8800100.img</c>.</summary>
    public string? From { get; set; }

    /// <summary>Archive-relative landing path.</summary>
    public string? To { get; set; }

    /// <summary>The source archive family this came out of: <c>Mob</c>, <c>String</c>.</summary>
    public string? SourceArchive { get; set; }

    /// <summary>Source bytes this part carried, as the plan costed it.</summary>
    public long Bytes { get; set; }

    /// <summary>Structural content hash of what landed, or null. See the type remarks.</summary>
    public string? ContentHash { get; set; }
}

/// <summary>A node that landed under a name other than the source's, and why.</summary>
public sealed class LedgerRename
{
    /// <summary>The name in the source.</summary>
    public string From { get; set; } = "";

    /// <summary>The name it was given here.</summary>
    public string To { get; set; } = "";

    /// <summary>Where the renamed thing landed.</summary>
    public string? At { get; set; }

    /// <summary>The clash that forced it, in the port's own words.</summary>
    public string Why { get; set; } = "";
}

/// <summary>
/// Something the port did not carry, and the reason.
///
/// Refusals are recorded at the same rank as the things that were carried, not
/// as a footnote, because "what is missing from this client and why" is the
/// question a composition has to answer six months later.
/// </summary>
public sealed class LedgerRefusal
{
    /// <summary>refused | absent | conflict | blocked | warning | rewrite.</summary>
    public string Class { get; set; } = "";

    /// <summary>What it was about — a path, a name, or a whole take.</summary>
    public string What { get; set; } = "";

    public string Why { get; set; } = "";
}

/// <summary>What one take did, once it had been applied and the result recomputed.</summary>
public sealed class LedgerTake
{
    /// <summary>1-based position in the manifest. The order is the composition.</summary>
    public int Sequence { get; set; }

    /// <summary>The source's handle, label and folder, and the hash of every archive read.</summary>
    public CompositionSource Source { get; set; } = new();

    public string Kind { get; set; } = "";
    public string Scope { get; set; } = "selection";
    public string Into { get; set; } = "";

    /// <summary>Exactly what the manifest asked for, echoed so the record stands alone.</summary>
    public List<string> Requested { get; set; } = new();

    public CompositionOptions Options { get; set; } = new();
    public string? Note { get; set; }

    /// <summary>Every part that was written, in plan order.</summary>
    public List<LedgerPart> Took { get; set; } = new();

    /// <summary>Every part that landed under a different name.</summary>
    public List<LedgerRename> Renamed { get; set; } = new();

    /// <summary>Everything that was not carried, with its reason.</summary>
    public List<LedgerRefusal> Refused { get; set; } = new();

    public int Written { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    /// <summary>
    /// True when <see cref="LedgerPart.ContentHash"/> was computed for the parts
    /// of this take. False makes every null hash mean "not computed" rather than
    /// "no content" — the ambiguity a bare zero or null always carries.
    /// </summary>
    public bool PartsHashed { get; set; }

    /* --- excluded from Digest: two runs of the same build differ here and must --- */

    /// <summary>When this take was applied. Not part of the composition's identity.</summary>
    [JsonPropertyOrder(100)]
    public string When { get; set; } = "";

    /// <summary>How long it took. Not part of the composition's identity.</summary>
    [JsonPropertyOrder(101)]
    public double Seconds { get; set; }
}

/// <summary>What a reopened output archive was found to contain.</summary>
public sealed class LedgerVerifiedArchive
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the file as it was written. This is the build's output identity.</summary>
    public string Sha256 { get; set; } = "";

    public long Bytes { get; set; }

    /// <summary>Landing paths looked for in the reopened archive.</summary>
    public int Checked { get; set; }

    /// <summary>Landing paths the reopened archive did not have. Empty is the only good answer.</summary>
    public List<string> Missing { get; set; } = new();
}

/// <summary>
/// The verification pass, run on the saved and reopened archives.
///
/// <see cref="Ran"/> exists because <see cref="LedgerVerifiedArchive.Missing"/>
/// being empty means nothing on its own: it is empty when every part was found
/// and equally empty when nothing was looked for. A count of zero that means two
/// things is the defect class this project has spent the most hours on, so the
/// two are separate fields.
/// </summary>
public sealed class LedgerVerification
{
    public bool Ran { get; set; }

    /// <summary>Why it did not, when it did not. Never null then.</summary>
    public string? NotRunBecause { get; set; }

    public List<LedgerVerifiedArchive> Archives { get; set; } = new();

    /// <summary>Total landing paths checked across every archive.</summary>
    public int Checked => Archives.Sum(a => a.Checked);

    /// <summary>Total landing paths missing across every archive.</summary>
    public int Missing => Archives.Sum(a => a.Missing.Count);
}

/// <summary>
/// The record of what a composed client is made of, kept beside it.
///
/// A ledger is the ledger of one output folder. It is appended to by every port
/// applied into that folder — the interactive one as much as a build — so an
/// explored client and a built client are explicable by the same file. A build
/// replaces it wholesale, because a build replaces the client wholesale.
/// </summary>
public sealed class CompositionLedger
{
    public string Schema { get; set; } = CompositionSchema.Ledger;

    /// <summary>The composition's name, when it came from a manifest.</summary>
    public string Name { get; set; } = "";

    /// <summary>The folder this ledger describes.</summary>
    public string Output { get; set; } = "";

    /// <summary>
    /// How this client came to exist: <c>build</c> from a manifest, or
    /// <c>interactive</c> when ports were applied to it by hand. The two are not
    /// the same promise — only a build claims to be reproducible.
    /// </summary>
    public string Origin { get; set; } = "interactive";

    /// <summary>The pristine base, with the hash of every archive copied from it.</summary>
    [JsonPropertyName("base")]
    public CompositionSource Base { get; set; } = new();

    public List<LedgerTake> Takes { get; set; } = new();

    /// <summary>Things true of the whole build, stated before the reader reaches the takes.</summary>
    public List<string> Notes { get; set; } = new();

    public LedgerVerification Verification { get; set; } = new();

    /* --- excluded from Digest --- */

    [JsonPropertyOrder(100)]
    public string When { get; set; } = "";

    [JsonPropertyOrder(101)]
    public double Seconds { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, CompositionSchema.Json);

    public static CompositionLedger FromJson(string json)
    {
        CompositionLedger? ledger =
            JsonSerializer.Deserialize<CompositionLedger>(json, CompositionSchema.Json);

        if (ledger == null)
            throw new InvalidOperationException("The ledger file is empty.");

        if (!string.Equals(ledger.Schema, CompositionSchema.Ledger, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{ledger.Schema}' is not a composition ledger this build understands. "
                + $"Expected '{CompositionSchema.Ledger}'.");
        }

        return ledger;
    }

    /// <summary>
    /// A hash of everything in this ledger that a rebuild must reproduce — which
    /// is everything except when it happened and how long it took.
    ///
    /// This is what makes "same inputs, same output" checkable on the *record*
    /// and not only on the archives. Two builds whose WZ files match byte for
    /// byte but whose ledgers disagree about what was refused would be a build
    /// that got the right answer for shifting reasons, and that is a bug in the
    /// explanation even when it is not one in the data.
    /// </summary>
    /// <summary>
    /// The field separator inside <see cref="Digest"/>'s text. A unit separator
    /// because it cannot occur in a WZ name, a path or a reason, so no value can
    /// forge a field boundary and make two different ledgers digest alike.
    /// </summary>
    private const char Sep = (char)0x1f;

    public string Digest()
    {
        StringBuilder text = new();
        text.Append(Schema).Append(Sep).Append(Name).Append(Sep).Append(Origin).Append('\n');
        Describe(text, Base);

        foreach (string note in Notes)
            text.Append("note\u001f").Append(note).Append('\n');

        foreach (LedgerTake take in Takes)
        {
            text.Append("take\u001f").Append(take.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(take.Kind)
                .Append(Sep).Append(take.Scope)
                .Append(Sep).Append(take.Into)
                .Append(Sep).Append(take.Written.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(take.Failed.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(take.Skipped.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(take.PartsHashed ? "hashed" : "unhashed")
                .Append('\n');
            Describe(text, take.Source);

            foreach (string requested in take.Requested)
                text.Append("  want\u001f").Append(requested).Append('\n');
            foreach (LedgerPart part in take.Took)
            {
                text.Append("  took\u001f").Append(part.Kind)
                    .Append(Sep).Append(part.From).Append(Sep).Append(part.To)
                    .Append(Sep).Append(part.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(Sep).Append(part.ContentHash ?? "-")
                    .Append('\n');
            }
            foreach (LedgerRename rename in take.Renamed)
            {
                text.Append("  renamed\u001f").Append(rename.From).Append(Sep).Append(rename.To)
                    .Append(Sep).Append(rename.At).Append(Sep).Append(rename.Why).Append('\n');
            }
            foreach (LedgerRefusal refusal in take.Refused)
            {
                text.Append("  refused\u001f").Append(refusal.Class)
                    .Append(Sep).Append(refusal.What).Append(Sep).Append(refusal.Why).Append('\n');
            }
        }

        text.Append("verified\u001f").Append(Verification.Ran ? "ran" : "did-not-run")
            .Append(Sep).Append(Verification.NotRunBecause ?? "-").Append('\n');
        foreach (LedgerVerifiedArchive archive in Verification.Archives)
        {
            text.Append("  archive\u001f").Append(archive.Name)
                .Append(Sep).Append(archive.Sha256)
                .Append(Sep).Append(archive.Bytes.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(archive.Checked.ToString(CultureInfo.InvariantCulture))
                .Append(Sep).Append(archive.Missing.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            foreach (string missing in archive.Missing)
                text.Append("    missing\u001f").Append(missing).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();

        static void Describe(StringBuilder into, CompositionSource source)
        {
            // The folder is deliberately absent. A composition rebuilt from a
            // client that was moved is the same composition; saying otherwise
            // would make every one of these records machine-specific.
            into.Append("  from\u001f").Append(source.Id).Append(Sep).Append(source.Label).Append('\n');
            foreach (CompositionArchive archive in source.Archives)
            {
                into.Append("    archive\u001f").Append(archive.Name)
                    .Append(Sep).Append(archive.Sha256 ?? "unpinned").Append('\n');
            }
        }
    }
}

#endregion
