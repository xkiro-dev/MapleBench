namespace MapleBench.Models;

#region Books

/// <summary>
/// One skill book — an image under Skill.wz, e.g. <c>522.img</c>, holding the
/// skills of a single job.
/// </summary>
public sealed class SkillBookDto
{
    public string Path { get; set; } = "";

    /// <summary>The image's stem: "522". This is the key String.wz names it by.</summary>
    public string BookId { get; set; } = "";

    /// <summary>"Eight-Legs Easton"'s job book is "Captain" — from String.wz's bookName.</summary>
    public string? Name { get; set; }

    public int SkillCount { get; set; }
    public bool Dirty { get; set; }
}

/// <summary>An image under Skill.wz that is not a skill book, and why.</summary>
public sealed class SkillIgnoredDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class SkillBooksDto
{
    public List<SkillBookDto> Books { get; set; } = new();

    /// <summary>
    /// Images that were not treated as books. Listed rather than dropped: a
    /// client whose skills sit somewhere unexpected should show up as a named
    /// exclusion, not as silence.
    /// </summary>
    public List<SkillIgnoredDto> Ignored { get; set; } = new();

    public bool NamesAvailable { get; set; }
}

#endregion

#region Browse

/// <summary>One row of the skill browser.</summary>
public sealed class SkillSummaryDto
{
    public string Path { get; set; } = "";
    public int SkillId { get; set; }
    public string? Name { get; set; }

    public string BookPath { get; set; } = "";
    public string BookId { get; set; } = "";
    public string? BookName { get; set; }

    /// <summary>
    /// Null when the skill declares none — never 0. A 0 in this column would read
    /// as "this skill has no levels", which is a different and wrong statement.
    /// </summary>
    public int? MaxLevel { get; set; }

    /// <summary>How many literal <c>level/N</c> nodes exist. 0 is honest here.</summary>
    public int LevelCount { get; set; }

    /// <summary>formula | explicit | mixed | none — where the per-level values live.</summary>
    public string Storage { get; set; } = "none";

    /// <summary>The raw damage expression, e.g. "235+3*x". Null when the skill has none.</summary>
    public string? Damage { get; set; }

    public string? MpCon { get; set; }
    public string? Cooltime { get; set; }

    /// <summary>How many <c>common</c> entries are level-varying expressions.</summary>
    public int FormulaFields { get; set; }

    /// <summary>How many of them this build could not parse. Non-zero is a bug to look at.</summary>
    public int BadFormulas { get; set; }

    public bool Passive { get; set; }
    public bool Invisible { get; set; }
    public bool Dirty { get; set; }
}

public sealed class SkillStatsDto
{
    public int Total { get; set; }
    public int Books { get; set; }
    public int FormulaDriven { get; set; }
    public int ExplicitLevels { get; set; }
    public int Mixed { get; set; }
    public int BadFormulas { get; set; }
}

public sealed class SkillListDto
{
    public List<SkillSummaryDto> Skills { get; set; } = new();
    public SkillStatsDto Stats { get; set; } = new();
    public bool Truncated { get; set; }
}

#endregion

#region Detail

/// <summary>
/// One column of the virtual level table — a field, joined to the catalog, with
/// a note about where its values come from.
/// </summary>
public sealed class SkillColumnDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public string Kind { get; set; } = "Int";
    public string? Unit { get; set; }
    public string? Hint { get; set; }

    /// <summary>formula | constant | explicit | mixed | container | error.</summary>
    public string Source { get; set; } = "explicit";

    /// <summary>The <c>common</c> expression behind this column, verbatim.</summary>
    public string? Formula { get; set; }

    /// <summary>Session path of <c>common/&lt;key&gt;</c>, so the formula itself can be edited.</summary>
    public string? FormulaPath { get; set; }

    /// <summary>Why the formula could not be read. Never accompanied by a value.</summary>
    public string? FormulaError { get; set; }

    /// <summary>
    /// Free variables the formula uses — names the <c>common</c> block does not
    /// define, like the <c>x30</c> in <c>"200+4*x30"</c>. Non-empty means the
    /// formula is sound but cannot produce numbers until these are supplied; it
    /// is not an error and the column is not broken.
    /// </summary>
    public IReadOnlyList<string> Needs { get; set; } = Array.Empty<string>();

    /// <summary>False for a formula-derived column: its cells are computed, not stored.</summary>
    public bool Editable { get; set; } = true;
}

/// <summary>One cell of the virtual level table.</summary>
public sealed class SkillCellDto
{
    public string Key { get; set; } = "";

    /// <summary>
    /// The value as text, invariant-formatted. Null means there is no value —
    /// the field is absent at this level, or its formula could not be read. It is
    /// never 0 standing in for "unknown".
    /// </summary>
    public string? Value { get; set; }

    /// <summary>explicit | formula | constant | container | needs | error | missing.</summary>
    public string Source { get; set; } = "missing";

    /// <summary>Session path of the stored node, when one exists.</summary>
    public string? Path { get; set; }

    public string? WzType { get; set; }
    public bool Editable { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A free variable the level table needs a value for before it can compute.
///
/// These are real: MapleStory formulas read names the archive never stores,
/// because the client knows them from elsewhere. The editor cannot invent them
/// and will not assume zero, so it asks — once, at the top of the table, rather
/// than as an error in every cell.
/// </summary>
public sealed class SkillVariableDto
{
    public string Name { get; set; } = "";

    /// <summary>What the user supplied, or null if they have not yet.</summary>
    public string? Value { get; set; }
}

public sealed class SkillLevelRowDto
{
    public int Level { get; set; }

    /// <summary>Session path of <c>level/&lt;n&gt;</c>, whether or not it exists yet.</summary>
    public string Path { get; set; } = "";

    /// <summary>False when this row exists only because a formula covers it.</summary>
    public bool Present { get; set; }

    public List<SkillCellDto> Cells { get; set; } = new();
}

/// <summary>A field that belongs to the skill as a whole, not to a level.</summary>
public sealed class SkillFieldDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public string Kind { get; set; } = "Int";
    public string? Unit { get; set; }
    public string? Hint { get; set; }
    public string Path { get; set; } = "";
    public string? WzType { get; set; }
    public string? Value { get; set; }
    public bool Present { get; set; }
    public bool Editable { get; set; } = true;
}

public sealed class SkillFieldGroupDto
{
    public string Group { get; set; } = "";
    public List<SkillFieldDto> Fields { get; set; } = new();
}

public sealed class SkillDetailDto
{
    public string Path { get; set; } = "";
    public int SkillId { get; set; }
    public string? Name { get; set; }

    public string BookPath { get; set; } = "";
    public string BookId { get; set; } = "";
    public string? BookName { get; set; }

    public bool Dirty { get; set; }

    public int? MaxLevel { get; set; }
    public string Storage { get; set; } = "none";

    public bool HasCommon { get; set; }
    public bool HasLevel { get; set; }
    public bool HasPvpCommon { get; set; }

    public string? CommonPath { get; set; }
    public string LevelPath { get; set; } = "";

    /// <summary>
    /// The trap, stated where the user is about to spring it: a skill with a
    /// <c>common</c> block ignores everything under <c>level/</c>.
    /// </summary>
    public string? Warning { get; set; }

    public List<SkillColumnDto> Columns { get; set; } = new();
    public List<SkillLevelRowDto> Levels { get; set; } = new();

    /// <summary>
    /// Free variables across every column, deduplicated. Empty for the ordinary
    /// skill; non-empty means the table is waiting on a value, not broken.
    /// </summary>
    public List<SkillVariableDto> Variables { get; set; } = new();

    /// <summary>Skill-wide fields: masterLevel, info/*, weapon, and so on.</summary>
    public List<SkillFieldGroupDto> Groups { get; set; } = new();

    /// <summary>Set when the level table hit its row cap.</summary>
    public bool Truncated { get; set; }
}

#endregion

#region Writes

public sealed class SkillCellWrite
{
    public int Level { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class SkillLevelsWriteRequest
{
    public string Path { get; set; } = "";
    public List<SkillCellWrite> Cells { get; set; } = new();
}

/// <summary>add | clone | remove | rename, over a skill's <c>level/N</c> nodes.</summary>
public sealed class SkillLevelRequest
{
    public string Path { get; set; } = "";

    /// <summary>add | clone | remove | rename</summary>
    public string Op { get; set; } = "add";

    /// <summary>The level being added, cloned to, removed, or renamed from.</summary>
    public int Level { get; set; }

    /// <summary>For clone: the level to copy.</summary>
    public int From { get; set; }

    /// <summary>For rename: the new level number.</summary>
    public int To { get; set; }
}

/// <summary>
/// Bakes a <c>common</c> block's formulas into literal <c>level/N</c> nodes.
/// <see cref="DryRun"/> is the point: it returns every cell it would create,
/// with the reason for each one it would not, and writes nothing.
/// </summary>
public sealed class SkillExpandRequest
{
    public string Path { get; set; } = "";

    /// <summary>How many levels to write. Defaults to the skill's maxLevel.</summary>
    public int? Levels { get; set; }

    /// <summary>Replace a level cell that already holds a literal value.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Delete the <c>common</c> block afterwards. On by default, because leaving
    /// it in place means the client keeps using the formulas and the whole
    /// conversion has no effect in game.
    /// </summary>
    public bool RemoveCommon { get; set; } = true;

    /// <summary>
    /// Values for free variables, same as the detail view's. A bake has to
    /// compute the same numbers the table showed, and the table cannot show them
    /// without these — so they travel with the request rather than being
    /// re-guessed here.
    /// </summary>
    public Dictionary<string, double>? Variables { get; set; }

    public bool DryRun { get; set; } = true;
}

public sealed class SkillExpandChangeDto
{
    public int Level { get; set; }
    public string Key { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? WzType { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class SkillExpandResultDto
{
    public List<SkillExpandChangeDto> Changes { get; set; } = new();
    public int Applied { get; set; }
    public int LevelsWritten { get; set; }
    public bool RemovedCommon { get; set; }

    /// <summary>Things the user needs to know that are not per-row: PVPcommon, dropped maxLevel.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>The skill as it stands afterwards. Null on a dry run.</summary>
    public SkillDetailDto? Detail { get; set; }
}

/// <summary>
/// A bulk arithmetic edit over many skills, shaped exactly like
/// <c>MobBulkRequest</c> so the two UIs and the two mental models match.
/// </summary>
public sealed class SkillBulkRequest
{
    public List<string> Paths { get; set; } = new();
    public string Field { get; set; } = "";

    /// <summary>
    /// 0 targets the skill's <c>common/&lt;field&gt;</c>; N targets
    /// <c>level/N/&lt;field&gt;</c>. There is no "all levels" — a change the user
    /// cannot see row by row is not previewable.
    /// </summary>
    public int Level { get; set; }

    /// <summary>set | add | multiply | percent</summary>
    public string Op { get; set; } = "set";

    public double Value { get; set; }

    /// <summary>nearest | floor | none. Never round a field that is not integral.</summary>
    public string Round { get; set; } = "nearest";

    public bool DryRun { get; set; } = true;
}

public sealed class SkillBulkChangeDto
{
    public string Path { get; set; } = "";
    public int SkillId { get; set; }
    public string? Name { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
}

public sealed class SkillBulkResultDto
{
    public List<SkillBulkChangeDto> Changes { get; set; } = new();
    public int Applied { get; set; }
    public bool Truncated { get; set; }
}

#endregion
