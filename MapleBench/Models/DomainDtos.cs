namespace MapleBench.Models;

#region NPCs

/// <summary>
/// One row of the NPC browser.
///
/// Every numeric field a client may simply not carry is nullable, on purpose.
/// An NPC with no <c>trunkPut</c> is not an NPC that charges 0 mesos to store an
/// item; rendering it as 0 is the "grid of zeros reads as corrupt data" failure
/// the quality bar names, so the absent case is null and the UI shows an em dash.
/// </summary>
public sealed class NpcSummaryDto
{
    public string Path { get; set; } = "";
    public int NpcId { get; set; }

    /// <summary>From String.wz/Npc.img/&lt;id&gt;/name. Null when String.wz is not open.</summary>
    public string? Name { get; set; }

    /// <summary>The job line under the name, String.wz/Npc.img/&lt;id&gt;/func.</summary>
    public string? Func { get; set; }

    /// <summary>First <c>info/script/&lt;n&gt;/script</c> value — the server-side handler name.</summary>
    public string? Script { get; set; }

    public bool IsShop { get; set; }
    public bool IsStorage { get; set; }
    public bool IsTrunk { get; set; }
    public bool HasScript { get; set; }
    public bool HideName { get; set; }

    /// <summary>How many chat lines <c>info/speak</c> lists. Null when there is no speak node.</summary>
    public int? SpeakLines { get; set; }

    /// <summary>Set when info/link points elsewhere — this NPC is a shell.</summary>
    public string? LinkTarget { get; set; }

    public bool Dirty { get; set; }
}

public sealed class NpcStatsDto
{
    public int Total { get; set; }
    public int Named { get; set; }
    public int Scripted { get; set; }
    public int Shops { get; set; }
    public int Storages { get; set; }
    public int Linked { get; set; }
    public int Hidden { get; set; }
}

public sealed class NpcListDto
{
    public List<NpcSummaryDto> Npcs { get; set; } = new();
    public NpcStatsDto Stats { get; set; } = new();
    public bool Truncated { get; set; }

    /// <summary>False when String.wz is not open, so the UI can say why names are missing.</summary>
    public bool NamesAvailable { get; set; }
}

/// <summary>One editable field of an NPC's info node, joined to the catalog.</summary>
public sealed class NpcFieldDto
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

    /// <summary>False when this NPC has no such node. Writing one creates it.</summary>
    public bool Present { get; set; }

    /// <summary>
    /// False for a container (a group of values rather than one). The UI must not
    /// draw an input for it — writing is refused server-side either way.
    /// </summary>
    public bool Editable { get; set; } = true;
}

public sealed class NpcFieldGroupDto
{
    public string Group { get; set; } = "";
    public List<NpcFieldDto> Fields { get; set; } = new();
}

/// <summary>One entry of <c>info/script</c>: the handler the server looks up.</summary>
public sealed class NpcScriptDto
{
    /// <summary>The index node's name — "0", "1", ... </summary>
    public string Index { get; set; } = "";

    /// <summary>Session path of the script string itself, so it can be edited directly.</summary>
    public string Path { get; set; } = "";

    public string? Script { get; set; }

    /// <summary>Any siblings of <c>script</c> in the same entry — questStart, questEnd, ...</summary>
    public Dictionary<string, string?> Extra { get; set; } = new();
}

/// <summary>
/// One idle chat line.
///
/// <c>info/speak/&lt;n&gt;</c> does not hold the text — it holds a *key* ("n0")
/// into String.wz/Npc.img/&lt;id&gt;. Editing the WZ node changes which line is
/// shown; editing the text means writing the string entry, which is what
/// <see cref="StringWriteRequest"/> is for.
/// </summary>
public sealed class NpcSpeakLineDto
{
    public string Index { get; set; } = "";

    /// <summary>Session path of the speak node, i.e. of the key.</summary>
    public string Path { get; set; } = "";

    /// <summary>The key, e.g. "n0".</summary>
    public string? Key { get; set; }

    /// <summary>The resolved text from String.wz, or null when it is missing.</summary>
    public string? Text { get; set; }

    /// <summary>Session path of the string node, when String.wz is open and holds it.</summary>
    public string? StringPath { get; set; }
}

public sealed class NpcDetailDto
{
    public string Path { get; set; } = "";
    public int NpcId { get; set; }
    public string? Name { get; set; }
    public string? Func { get; set; }

    /// <summary>Session path of String.wz/Npc.img/&lt;id&gt;, when it exists.</summary>
    public string? StringPath { get; set; }

    /// <summary>Set when info/link points elsewhere — edits here do nothing.</summary>
    public string? LinkTarget { get; set; }

    public bool Dirty { get; set; }
    public List<NpcFieldGroupDto> Groups { get; set; } = new();
    public List<NpcScriptDto> Scripts { get; set; } = new();
    public List<NpcSpeakLineDto> Speak { get; set; } = new();

    /// <summary>Non-fatal things worth saying before the user edits — see rule 6.</summary>
    public List<string> Warnings { get; set; } = new();
}

public sealed class NpcFieldWrite
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class NpcWriteRequest
{
    public string Path { get; set; } = "";
    public List<NpcFieldWrite> Fields { get; set; } = new();
}

/// <summary>
/// A bulk edit over many NPCs. <see cref="DryRun"/> is the point of the whole
/// thing: it returns the same rows with before/after filled in and writes
/// nothing, so the UI can show the change before it happens.
/// </summary>
public sealed class NpcBulkRequest
{
    public List<string> Paths { get; set; } = new();
    public string Field { get; set; } = "";

    /// <summary>set | add | multiply | percent. Only "set" accepts text.</summary>
    public string Op { get; set; } = "set";

    /// <summary>The operand, as text — "set" writes it verbatim when it is not a number.</summary>
    public string Value { get; set; } = "";

    /// <summary>nearest | floor | none. Never round a field that is not integral.</summary>
    public string Round { get; set; } = "nearest";

    public bool DryRun { get; set; } = true;
}

public sealed class NpcBulkChangeDto
{
    public string Path { get; set; } = "";
    public int NpcId { get; set; }
    public string? Name { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class NpcBulkResultDto
{
    public List<NpcBulkChangeDto> Changes { get; set; } = new();
    public int Applied { get; set; }
    public bool DryRun { get; set; }
}

#endregion

#region String.wz entries

/// <summary>
/// One display-name entry, whichever of String.wz's three layouts it lives in.
/// </summary>
public sealed class StringEntryDto
{
    /// <summary>item | mob | skill | npc | map.</summary>
    public string Kind { get; set; } = "";

    public int Id { get; set; }

    /// <summary>
    /// Session path of the entry node, e.g. "f2/Consume.img/2000000". Null when
    /// the entry does not exist yet — which is precisely the case this editor is
    /// for, so it is a state to report, not an error.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>False when nothing names this id yet.</summary>
    public bool Present { get; set; }

    /// <summary>The image the entry lives in (or would live in): "Consume.img".</summary>
    public string Image { get; set; } = "";

    /// <summary>Flat | Nested | Regioned.</summary>
    public string Layout { get; set; } = "";

    /// <summary>The Eqp category or Map region holding it, when the layout has one.</summary>
    public string? Group { get; set; }

    /// <summary>
    /// Field name -&gt; current text, for the fields this kind supports. A field
    /// the entry does not carry is present here with a null value, so the UI can
    /// tell "empty" from "not offered".
    /// </summary>
    public Dictionary<string, string?> Fields { get; set; } = new();

    /// <summary>
    /// Everything else the entry holds — the dialogue keys (n0, d1, ...) on an
    /// NPC, descD on a pet. Read-only here; the Explorer edits them.
    /// </summary>
    public Dictionary<string, string?> Other { get; set; } = new();
}

public sealed class StringListDto
{
    public string Kind { get; set; } = "";
    public List<StringEntryDto> Entries { get; set; } = new();

    /// <summary>How many entries matched before the limit was applied.</summary>
    public int Matched { get; set; }

    /// <summary>How many entries this kind has in total.</summary>
    public int Total { get; set; }

    public bool Truncated { get; set; }
    public bool Available { get; set; }
}

/// <summary>
/// Writes display text for one id, creating the entry when it does not exist.
///
/// A null field is "leave it alone"; an empty string is "make it empty". They
/// are different requests and the service treats them as such.
/// </summary>
public sealed class StringWriteRequest
{
    /// <summary>item | mob | skill | npc | map.</summary>
    public string Kind { get; set; } = "";

    public int Id { get; set; }

    public string? Name { get; set; }
    public string? Desc { get; set; }

    /// <summary>NPCs only — the job line under the name.</summary>
    public string? Func { get; set; }

    /// <summary>Maps only.</summary>
    public string? MapName { get; set; }

    /// <summary>Maps only.</summary>
    public string? StreetName { get; set; }

    /// <summary>
    /// Equips only: which Eqp.img category to create the entry under. Left null,
    /// the service works it out from the archive and says which it chose.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Maps only: which Map.img region to create the entry under. Same rule as
    /// <see cref="Category"/>.
    /// </summary>
    public string? Region { get; set; }
}

public sealed class StringFieldChangeDto
{
    public string Field { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Created { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class StringWriteResultDto
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public string? Path { get; set; }

    /// <summary>True when the id had no entry at all before this call.</summary>
    public bool CreatedEntry { get; set; }

    public string Image { get; set; } = "";
    public string? Group { get; set; }

    /// <summary>How the Eqp category / Map region was decided, when one was.</summary>
    public string? GroupReason { get; set; }

    public List<StringFieldChangeDto> Changes { get; set; } = new();
    public int Applied { get; set; }

    /// <summary>The entry as it now stands, so the UI does not have to re-fetch.</summary>
    public StringEntryDto? Entry { get; set; }
}

/// <summary>One row of a bulk naming run.</summary>
public sealed class StringBulkEntry
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Desc { get; set; }
    public string? Func { get; set; }
    public string? MapName { get; set; }
    public string? StreetName { get; set; }
    public string? Category { get; set; }
    public string? Region { get; set; }
}

public sealed class StringBulkRequest
{
    public string Kind { get; set; } = "";
    public List<StringBulkEntry> Entries { get; set; } = new();
    public bool DryRun { get; set; } = true;
}

public sealed class StringBulkChangeDto
{
    public int Id { get; set; }
    public string? Path { get; set; }
    public bool CreatedEntry { get; set; }
    public string? Group { get; set; }
    public List<StringFieldChangeDto> Changes { get; set; } = new();
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class StringBulkResultDto
{
    public string Kind { get; set; } = "";
    public List<StringBulkChangeDto> Rows { get; set; } = new();

    /// <summary>Fields actually written. Zero on a dry run, always.</summary>
    public int Applied { get; set; }

    /// <summary>Entries that had to be created.</summary>
    public int Created { get; set; }

    public int Skipped { get; set; }
    public bool DryRun { get; set; }
}

#endregion
