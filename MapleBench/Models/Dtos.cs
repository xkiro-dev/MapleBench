using System.Text.Json.Serialization;

namespace MapleBench.Models;

/// <summary>
/// The five things a path can point at.  Everything the UI does is expressed in
/// terms of these, so the client never needs to know about MapleLib types.
/// </summary>
public enum NodeKind
{
    File,
    Directory,
    Image,
    Property,
}

/// <summary>
/// A single node in the WZ tree, flattened for the browser.
/// </summary>
public sealed class NodeDto
{
    /// <summary>Session path: "f1/Skill.wz/000.img/skill/1000".</summary>
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// What this node's name means, when the name is an id and String.wz is open
    /// — "Blue Sword" for 01302000. Null when it cannot be resolved, which is the
    /// common case, so the UI must treat it as decoration and never as identity.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    public NodeKind Kind { get; set; }

    /// <summary>WzPropertyType name ("Int", "Canvas", ...) for properties; null otherwise.</summary>
    public string? Type { get; set; }

    /// <summary>True when the node can be expanded. Images report true before parsing.</summary>
    public bool HasChildren { get; set; }

    /// <summary>-1 when unknown (unparsed image).</summary>
    public int ChildCount { get; set; } = -1;

    /// <summary>Editable scalar rendered as text. Null for containers.</summary>
    public string? Value { get; set; }

    /// <summary>True when the value is directly editable through <c>PUT /api/node/value</c>.</summary>
    public bool Editable { get; set; }

    public bool Parsed { get; set; } = true;

    public bool Dirty { get; set; }

    /// <summary>Type-specific extras: canvas size, sound duration, UOL target, ...</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Extra { get; set; }

    /// <summary>
    /// Set when an edit succeeded but changed something the user did not ask
    /// for — most importantly a canvas whose PNG format could not be preserved.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; set; }

    /// <summary>
    /// The physical archive this node came out of — "Skill003.wz".
    ///
    /// Set only by <see cref="Services.ArchiveFamilyService"/>, where several
    /// files are shown as one tree and "which file is this in" stops being
    /// obvious from the row's ancestry. Null everywhere else, because everywhere
    /// else the first path segment already answers it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>
    /// Every physical archive that has a node at this name, winner first. Set
    /// only when there is more than one.
    ///
    /// For a directory that is the merge doing its job. For anything else it is
    /// a collision — see <see cref="Shadowed"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Sources { get; set; }

    /// <summary>
    /// This image exists in more than one archive of the family and the copies
    /// behind <see cref="Source"/> are hidden by it.
    ///
    /// Never decoration. Which copy wins is decided by mount order alone, and
    /// the client's mount order is not recorded in the archives, so an edit made
    /// to the visible copy may be an edit to the one the game does not load.
    /// </summary>
    public bool Shadowed { get; set; }
}

#region Map assets

/// <summary>One tile, object or background set — "grassySoil", "houseSW".</summary>
public sealed class MapAssetSetDto
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Real session path of the copy this row stands for — the one an edit, a
    /// render or a port would reach. Never a synthetic "family" path: the
    /// library is a union of several archives and the first segment is what says
    /// which one.
    /// </summary>
    public string Path { get; set; } = "";

    public string Kind { get; set; } = "";

    /// <summary>
    /// The physical archive this set came out of — "Map2.wz".
    ///
    /// Always set, unlike <see cref="NodeDto.Source"/>, because the library
    /// deliberately spans archives: a v232 client keeps its 336 object sets in
    /// Map.wz <i>and</i> Map2.wz, and "which file is this in" is not answerable
    /// from the set's name.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Every archive holding a set of this name, winner first. Set only when
    /// there is more than one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Sources { get; set; }

    /// <summary>
    /// More than one archive <i>of the same client</i> has a set with this name,
    /// so the copies behind <see cref="Source"/> are hidden by it.
    ///
    /// Not the same as <see cref="Sources"/> having two entries. Two clients open
    /// side by side both have a <c>Back/grassySoil.img</c> and that is the normal
    /// workflow, not damage; two archives of one client having it is the
    /// mount-order coin toss <see cref="NodeDto.Shadowed"/> describes, and the
    /// auditor found four real instances of it in the Skill family with
    /// different bytes.
    /// </summary>
    public bool Shadowed { get; set; }
}

/// <summary>What the open session can offer the map asset browser.</summary>
public sealed class MapAssetCapabilitiesDto
{
    public bool Available { get; set; }

    /// <summary>One row per library kind — Tile, Obj, Back — whether present or not.</summary>
    public List<MapAssetKindDto> Kinds { get; set; } = new();
}

/// <summary>One library kind and where it was found.</summary>
public sealed class MapAssetKindDto
{
    public string Kind { get; set; } = "";

    /// <summary>Distinct set names, after the union across archives is deduped.</summary>
    public int Sets { get; set; }

    /// <summary>Archives contributing to this kind, in mount order.</summary>
    public List<string> Archives { get; set; } = new();

    /// <summary>How many set names are shadowed within one client.</summary>
    public int Shadowed { get; set; }
}

/// <summary>One placeable piece of art within a set.</summary>
public sealed class MapAssetEntryDto
{
    /// <summary>The variant or category — "bsc", "enH0", "house/roof".</summary>
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class MapAssetEntriesDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public List<MapAssetEntryDto> Entries { get; set; } = new();
    public bool Truncated { get; set; }
}

#endregion

#region Mobs

/// <summary>One row of the mob browser.</summary>
public sealed class MobSummaryDto
{
    public string Path { get; set; } = "";
    public int MobId { get; set; }
    public string? Name { get; set; }
    public int Level { get; set; }
    public long MaxHP { get; set; }
    public long MaxMP { get; set; }
    public long Exp { get; set; }
    public long Padamage { get; set; }
    public long Madamage { get; set; }
    public bool IsBoss { get; set; }
    public bool Undead { get; set; }
    public string? ElemAttr { get; set; }

    /// <summary>Set when info/link points elsewhere — this mob is a shell.</summary>
    public string? LinkTarget { get; set; }

    public bool Dirty { get; set; }
}

public sealed class MobStatsDto
{
    public int Total { get; set; }
    public int Bosses { get; set; }
    public int Undead { get; set; }
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
}

public sealed class MobListDto
{
    public List<MobSummaryDto> Mobs { get; set; } = new();
    public MobStatsDto Stats { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>One editable field of a mob's info node, joined to the catalog.</summary>
public sealed class MobFieldDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "Int";
    public string? Unit { get; set; }
    public string? Hint { get; set; }

    /// <summary>Full session path of the property, for direct edits.</summary>
    public string Path { get; set; } = "";

    /// <summary>The WZ property type, when the node exists.</summary>
    public string? WzType { get; set; }

    public string? Value { get; set; }

    /// <summary>False when this mob has no such node. Writing one creates it.</summary>
    public bool Present { get; set; }

    /// <summary>
    /// False for a container (a group of values rather than one). The UI must not
    /// draw an input for it — writing is refused server-side either way.
    /// </summary>
    public bool Editable { get; set; } = true;
}

public sealed class MobFieldGroupDto
{
    public string Group { get; set; } = "";
    public List<MobFieldDto> Fields { get; set; } = new();
}

public sealed class MobDetailDto
{
    public string Path { get; set; } = "";
    public int MobId { get; set; }
    public string? Name { get; set; }
    public string? LinkTarget { get; set; }
    public bool Dirty { get; set; }
    public List<MobFieldGroupDto> Groups { get; set; } = new();
}

public sealed class MobFieldWrite
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class MobWriteRequest
{
    public string Path { get; set; } = "";
    public List<MobFieldWrite> Fields { get; set; } = new();
}

/// <summary>
/// A bulk arithmetic edit over many mobs. <see cref="DryRun"/> is the point of
/// the whole thing: it returns the same rows with before/after filled in and
/// writes nothing, so the UI can show the change before it happens.
/// </summary>
public sealed class MobBulkRequest
{
    public List<string> Paths { get; set; } = new();
    public string Field { get; set; } = "";

    /// <summary>set | add | multiply | percent</summary>
    public string Op { get; set; } = "set";

    public double Value { get; set; }

    /// <summary>nearest | floor | none. Never round a field that is not integral.</summary>
    public string Round { get; set; } = "nearest";

    public bool DryRun { get; set; } = true;
}

public sealed class MobBulkChangeDto
{
    public string Path { get; set; } = "";
    public int MobId { get; set; }
    public string? Name { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class MobBulkResultDto
{
    public List<MobBulkChangeDto> Changes { get; set; } = new();
    public int Applied { get; set; }
    public bool Truncated { get; set; }
}

#endregion

/// <summary>An open WZ file, loose .img, or mounted IMG folder in the session.</summary>
public sealed class OpenFileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>"wz", "img", "img-folder", "ms" or "split".</summary>
    public string Kind { get; set; } = "wz";
    public string MapleVersion { get; set; } = "";
    public short GameVersion { get; set; }
    public bool Is64Bit { get; set; }
    public bool Dirty { get; set; }
    public int DirtyNodeCount { get; set; }
    /// <summary>True when the archive was released and could not be reopened.</summary>
    public bool Detached { get; set; }

    /// <summary>Open for reference only — every edit below it is refused.</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>Toggles an open archive between editable and reference-only.</summary>
public sealed class ReadOnlyRequest
{
    public bool ReadOnly { get; set; }
}

public sealed class OpenRequest
{
    public string Path { get; set; } = "";
    /// <summary>GMS / EMS / BMS / CLASSIC / GENERATE / CUSTOM / null = auto-detect.</summary>
    public string? MapleVersion { get; set; }
    /// <summary>Explicit 4-byte IV as hex ("4D23C72B") when MapleVersion is CUSTOM.</summary>
    public string? Iv { get; set; }
    /// <summary>-1 = let MapleLib brute-force the version.</summary>
    public short GameVersion { get; set; } = -1;
    /// <summary>
    /// Open for reference only. Applied before the file enters the session, so
    /// an importer source is never briefly writable.
    /// </summary>
    public bool ReadOnly { get; set; }
}

public sealed class OpenManyRequest
{
    public List<string> Paths { get; set; } = new();
    public string? MapleVersion { get; set; }
    public string? Iv { get; set; }
    public short GameVersion { get; set; } = -1;
}

public sealed class SetValueRequest
{
    public string Path { get; set; } = "";
    public string? Value { get; set; }
}

public sealed class RenameRequest
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AddNodeRequest
{
    /// <summary>Parent container path.</summary>
    public string Path { get; set; } = "";
    /// <summary>
    /// WzPropertyType name, or "Image" / "Directory" when the parent is a WzDirectory.
    /// </summary>
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Value { get; set; }
}

public sealed class PathsRequest
{
    public List<string> Paths { get; set; } = new();
}

public sealed class TransferRequest
{
    public List<string> Paths { get; set; } = new();
    public string TargetPath { get; set; } = "";
    /// <summary>True = move (cut), false = copy.</summary>
    public bool Move { get; set; }
    /// <summary>Overwrite same-named children instead of auto-renaming.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Where among the target's existing children to land, instead of last.
    ///
    /// This exists so a drag that drops a node *between* two children of another
    /// parent is one operation rather than a transfer followed by a reorder.
    /// Two operations would be two undo entries, and a Ctrl+Z that puts the node
    /// back in the wrong place is worse than not offering the gesture.
    ///
    /// Properties only.  A <see cref="MapleLib.WzLib.WzDirectory"/> has no
    /// insert-at — <c>AddImage</c> appends — so an image transfer ignores this
    /// and lands last, which is also why nothing in the UI offers the gesture
    /// for images.  Out-of-range values are clamped, not rejected: the caller
    /// computed the index from a listing that may have moved under it, and
    /// landing at the end is the harmless failure.
    /// </summary>
    public int? Index { get; set; }
}

public sealed class SearchRequest
{
    /// <summary>Root to search under; empty = every open file.</summary>
    public string? Path { get; set; }
    public string Query { get; set; } = "";
    public bool MatchNames { get; set; } = true;
    public bool MatchValues { get; set; }
    public bool Regex { get; set; }
    public bool CaseSensitive { get; set; }
    /// <summary>Restrict to these WzPropertyType names.</summary>
    public List<string>? Types { get; set; }

    /// <summary>
    /// Hit ceiling, clamped on the way in.  The value arrives from the wire and
    /// the search runs under the global session lock, so an unclamped
    /// int.MaxValue would leave the 20-second wall clock as the only thing
    /// standing between one request and every other one.
    /// </summary>
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 1, MaxLimit);
    }

    public const int MaxLimit = 5000;
    private int _limit = 500;
}

public sealed class SearchHitDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>String.wz alias, when the node's name is an id we can resolve.</summary>
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? Value { get; set; }
    /// <summary>
    /// Whether the value can be written. Carried so a result row can offer an
    /// input rather than making the reader navigate away to find out that it
    /// cannot be edited.
    /// </summary>
    public bool Editable { get; set; }
    /// <summary>
    /// Which archive this came from, by its real name.
    ///
    /// <see cref="Context"/> deliberately omits the session file id, which left
    /// hits from three open archives looking identical -- the same `info/maxHP`
    /// in Mob.wz and in a second Mob.wz opened for comparison were one
    /// indistinguishable list.
    /// </summary>
    public string? File { get; set; }
    /// <summary>Human-readable ancestor chain for display.</summary>
    public string Context { get; set; } = "";
}

/// <summary>
/// A bulk value change expressed as an operation rather than a literal.
/// See <see cref="MapleBench.Services.ValueMath"/> for the grammar.
/// </summary>
public sealed class ComputeValuesRequest
{
    public List<string> Paths { get; set; } = new();
    public string Expression { get; set; } = "";
    /// <summary>
    /// Report what each node would become without writing anything.
    ///
    /// The preview and the write are the same call with this flipped, so the
    /// numbers shown in the confirmation are the numbers that get written --
    /// a second implementation on the client could drift, and the one thing
    /// this must never do is report success over work that did not happen.
    /// </summary>
    public bool DryRun { get; set; }
}

public sealed class ComputedValueDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    /// <summary>Why this node was left alone; null when it was (or would be) changed.</summary>
    public string? Skipped { get; set; }
}

public sealed class ComputeValuesResult
{
    /// <summary>Plain-language restatement of the expression, for the confirmation.</summary>
    public string Description { get; set; } = "";
    public List<ComputedValueDto> Results { get; set; } = new();
    public int Changed { get; set; }
    public int Skipped { get; set; }
    /// <summary>False for a dry run, and false when nothing was eligible.</summary>
    public bool Applied { get; set; }
}

public sealed class ReplaceRequest
{
    public string? Path { get; set; }
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool InNames { get; set; }
    public bool InValues { get; set; } = true;
    public bool Regex { get; set; }
    public bool CaseSensitive { get; set; }
    /// <summary>Report what would change without writing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Clamped like <see cref="SearchRequest.Limit"/>, and for the same
    /// reason. The client sends the same number for the preview and the apply.</summary>
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 1, SearchRequest.MaxLimit);
    }

    private int _limit = 5000;
}

public sealed class SaveRequest
{
    public string FileId { get; set; } = "";
    /// <summary>Null = overwrite in place.</summary>
    public string? TargetPath { get; set; }
    public bool Backup { get; set; } = true;
    /// <summary>Override the encryption used when writing.</summary>
    public string? MapleVersion { get; set; }
    public bool? Save64Bit { get; set; }
}

public sealed class ApiError
{
    public string Message { get; set; } = "";
    public string? Detail { get; set; }

    public ApiError() { }
    public ApiError(string message, string? detail = null)
    {
        Message = message;
        Detail = detail;
    }
}
