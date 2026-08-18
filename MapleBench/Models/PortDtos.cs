namespace MapleBench.Models;

/*
 * Wire shapes for porting content between two open clients.
 *
 * The vocabulary is deliberately kind-neutral — "entry", "part", "satellite",
 * "client" — because the only thing that is mob-specific about a mob port is the
 * catalog row naming Mob.wz, String.wz/Mob.img and Sound.wz/Mob.img. See
 * PortService.Kinds, where an item is the same sentence with different nouns.
 */

#region Clients

/// <summary>
/// One archive as the port screen sees it.
///
/// <see cref="Role"/> is what the port machinery matched it as, not a guess for
/// the user's benefit: an archive with no role cannot take part in a port, and
/// saying which ones did match is how someone works out why their Sound.wz is
/// being ignored (usually: it is not open).
/// </summary>
public sealed class PortArchiveDto
{
    public string FileId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A kind name ("mob", "item"), or "string" / "sound", or null.</summary>
    public string? Role { get; set; }

    /// <summary>Every kind this archive can hold — "item" covers Item.wz and Character.wz.</summary>
    public List<string> Kinds { get; set; } = new();

    public bool ReadOnly { get; set; }
    public bool Dirty { get; set; }
}

/// <summary>
/// A client: every open archive sitting in one folder.
///
/// The folder is the identity, not the archive name, and that is the whole
/// reason this type exists. Two clients open at once have an Item.wz each and a
/// String.wz each, with identical names — <see cref="StringPoolService"/>
/// already resolves that ambiguity by folder, and a port that resolved it any
/// other way would read one client's names while writing another's.
/// </summary>
public sealed class PortClientDto
{
    /// <summary>The folder, which is the client's identity on the wire.</summary>
    public string Key { get; set; } = "";

    /// <summary>The folder's leaf name, which is what a person calls the client.</summary>
    public string Label { get; set; } = "";

    public string Folder { get; set; } = "";

    /// <summary>
    /// The archive version stamp shared by this client's files, or 0 when they
    /// disagree. Disagreement is worth seeing: it means the folder holds files
    /// from two builds.
    /// </summary>
    public int GameVersion { get; set; }

    public bool MixedGameVersions { get; set; }

    public List<PortArchiveDto> Archives { get; set; } = new();

    /// <summary>False when every archive here is open for reference only.</summary>
    public bool AnyWritable { get; set; }
}

/// <summary>One kind of thing that can be ported, and everything it needs.</summary>
public sealed class PortKindDto
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string Plural { get; set; } = "";

    /// <summary>The archive families the entries live in: ["Item.wz", "Character.wz"].</summary>
    public List<string> Archives { get; set; } = new();

    /// <summary>Everything that travels with an entry, as "String.wz/Eqp.img" style labels.</summary>
    public List<string> Satellites { get; set; } = new();

    /// <summary>
    /// Satellite archives that must be readable on the source when an entry
    /// points into them. The UI offers to open these before planning; the server
    /// still checks the actual reference and refuses an incomplete write.
    /// </summary>
    public List<string> RequiredSatellites { get; set; } = new();

    public bool Supported { get; set; }

    /// <summary>Why not, when <see cref="Supported"/> is false. Never null then.</summary>
    public string? UnsupportedReason { get; set; }
}

public sealed class PortCapabilitiesDto
{
    /// <summary>True when at least two clients are open and one of them can be written to.</summary>
    public bool Available { get; set; }

    /// <summary>Why the port screen cannot work right now, when it cannot.</summary>
    public string? Reason { get; set; }

    public List<PortKindDto> Kinds { get; set; } = new();
    public List<PortClientDto> Clients { get; set; } = new();

    /// <summary>Ceiling on one selection port, so the preview and the undo entry stay finite.</summary>
    public int MaxSelection { get; set; }

    /// <summary>
    /// Ceiling on a whole-archive port, in bytes of source content. Reported so
    /// the UI can say what the limit is before the user runs into it.
    /// </summary>
    public long MaxArchiveBytes { get; set; }

    /// <summary>The scopes a port can run at. "selection" is the default and should stay so.</summary>
    public List<string> Scopes { get; set; } = new();
}

#endregion

#region Plan

/// <summary>
/// What one part of a port would do. A "part" is one node move: the entry
/// itself, a container it has to sit inside, an image it links to for its art,
/// its String.wz entry, its Sound.wz entry.
/// </summary>
public sealed class PortPartDto
{
    /// <summary>
    /// container | entry | link-entry | string | sound | shop | quest-* | named
    ///
    /// "named" is the one that is not a copy of the entry or of a row keyed by
    /// its id: it is something the entry reaches for BY NAME in a namespace both
    /// clients write into and neither owns — a map's background, tile set, object
    /// set, music or mark. Those are the parts that may land under a name other
    /// than the source's, and <see cref="Reason"/> says so when they do.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>"Item.wz · Cash/0510.img/05100000" — the sentence the row shows.</summary>
    public string Label { get; set; } = "";

    public string? SourcePath { get; set; }
    public string? TargetPath { get; set; }

    /// <summary>
    /// The archive family this part comes out of -- "Character", "String",
    /// "Sound", "Etc". Not a file name: a client whose archives are Sound001.wz
    /// and Sound002.wz reports "Sound" for both.
    ///
    /// Exists so the dialog can name the archives a plan actually reads from.
    /// It used to list every archive open in the source client instead, which
    /// is a different question -- opening Sound.wz to check whether an item had
    /// a sound made the header claim the sound was coming, and it read as a
    /// promise the plan never made.
    ///
    /// Empty for containers, which are structure in the archive the entry is
    /// already counted under.
    /// </summary>
    public string? SourceArchive { get; set; }

    /// <summary>
    /// New | Conflict | Same | Absent | Blocked.
    ///
    /// Conflict is the one that matters: the target already holds something at
    /// this name and the port will not touch it unless the user has explicitly
    /// asked for an overwrite.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>What the target already holds, for a Conflict — so the user can see what they would lose.</summary>
    public string? Existing { get; set; }

    /// <summary>Why this part is Absent, Blocked or Same. Always set for those.</summary>
    public string? Reason { get; set; }

    /// <summary>Whether an apply with the current options would write this part.</summary>
    public bool WillWrite { get; set; }

    /// <summary>
    /// Roughly what this part costs to carry, in source bytes. Zero when it is
    /// not worth saying — a container, a String.wz row.
    ///
    /// Set for the parts where the number is the surprise. A v232 map image is
    /// 51,482 bytes and the four pictures it names are 26,187,308, so a plan that
    /// reported only the entry's own size would understate what the port moves by
    /// a factor of five hundred.
    /// </summary>
    public long Bytes { get; set; }

    /* --- filled in by an apply, absent on a plan --- */

    public bool Applied { get; set; }

    /// <summary>What went wrong for this part alone. One bad part never decides the batch's fate.</summary>
    public string? Error { get; set; }
}

/// <summary>One thing being ported, with everything it needs.</summary>
public sealed class PortItemDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string SourcePath { get; set; } = "";

    /// <summary>False when nothing selected this — it was pulled in by a link.</summary>
    public bool Requested { get; set; }

    /// <summary>"the art for 8800100" — why an unrequested entry is in the list.</summary>
    public string? PulledInBy { get; set; }

    public List<PortPartDto> Parts { get; set; } = new();

    /// <summary>Facts about this entry the user should read before applying.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class PortTotalsDto
{
    public int Entries { get; set; }
    public int Parts { get; set; }
    public int New { get; set; }
    public int Conflicts { get; set; }
    public int Identical { get; set; }
    public int Absent { get; set; }
    public int Blocked { get; set; }

    /// <summary>How many parts an apply would actually write right now.</summary>
    public int WillWrite { get; set; }

    /// <summary>How many entries the target already has one of, at any status.</summary>
    public int EntriesAlreadyThere { get; set; }

    /// <summary>Rough source bytes this port would clone into memory. See PortService.MaxArchiveBytes.</summary>
    public long Bytes { get; set; }
}

public sealed class PortPlanDto
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>selection | archive</summary>
    public string Scope { get; set; } = "selection";

    public string SourceClient { get; set; } = "";
    public string SourceArchive { get; set; } = "";
    public string TargetClient { get; set; } = "";
    public string TargetFileId { get; set; } = "";
    public string TargetArchive { get; set; } = "";

    /// <summary>
    /// The entries, in full. Capped — see <see cref="ItemsTruncated"/>. Nobody
    /// can read four thousand rows, so the totals lead and this is the detail.
    /// </summary>
    public List<PortItemDto> Items { get; set; } = new();

    /// <summary>How many entries the plan covers but this list does not show.</summary>
    public int ItemsTruncated { get; set; }

    /// <summary>
    /// A sample of the entries the target already has, so a whole-archive port's
    /// conflict count can be judged rather than merely counted.
    /// </summary>
    public List<PortConflictDto> ConflictSample { get; set; } = new();

    /// <summary>
    /// What <see cref="PortPlanRequest.Match"/> would DELETE from the target,
    /// listed before it happens.
    ///
    /// This is the plan's most important list and it did not exist. Match removes
    /// entries the user never named — that is the whole point of it, "make this
    /// book be what the new build has" — and the only thing that ever said so was
    /// a warning appended to the *result*, after <c>_edit.Delete</c> had run. A
    /// person who ticked Match and wanted to see what it meant had no way to find
    /// out except by doing it and reading the count afterwards.
    ///
    /// The set is computable from the plan: it is every id in a container this
    /// port will write into that the source does not have. So it is computed
    /// there, and the apply recomputes it from what actually landed and says so
    /// if the two disagree.
    ///
    /// Empty when Match is off, which is the ordinary case.
    /// </summary>
    public List<PortRemovalDto> Removals { get; set; } = new();

    /// <summary>How many removals the plan found but this list does not show.</summary>
    public int RemovalsTruncated { get; set; }

    public PortTotalsDto Totals { get; set; } = new();

    /// <summary>
    /// Things that are true of this particular port and could go wrong in game —
    /// a version stamp mismatch, a field the target's own entries never carry, an
    /// art link that points outside what is being copied.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// What a port does not do, whatever the client is. Always populated, and
    /// always shown: an empty warnings list must not read as "this port is
    /// complete", because it is not.
    /// </summary>
    public List<string> Limits { get; set; } = new();

    /// <summary>Ids near a boss's that exist in the source and not the target — a suggestion, never an action.</summary>
    public List<PortSuggestionDto> Suggestions { get; set; } = new();

    /// <summary>True when nothing at all can be written and the reason is structural.</summary>
    public bool Blocked { get; set; }
    public string? BlockedReason { get; set; }

    /// <summary>
    /// True when <see cref="BlockedReason"/> is a judgement the caller is allowed
    /// to overrule, rather than a fact about the archives.
    ///
    /// Most refusals here are structural — the target archive holds a different
    /// kind, the source is the target, the closure is bigger than the ceiling —
    /// and no tick box can make those untrue. Exactly one is not: the dead canvas
    /// link check refuses on what a v232 client will do with a link it cannot
    /// follow, and someone porting into a client this service has guessed wrong
    /// about, or who is about to fix the links by hand, has a real reason to go
    /// ahead. Sending the plan back with
    /// <see cref="PortPlanRequest.AcceptDeadCanvasLinks"/> set runs the port and
    /// re-states the refusal as a warning, so the decision is recorded either way.
    ///
    /// Only the UI should read this: it is the difference between showing a dead
    /// end and showing a dead end with a door in it. A false here means there is
    /// no door, and offering one anyway would be a lie.
    /// </summary>
    public bool BlockedOverride { get; set; }

    /// <summary>
    /// Which request flag opens this particular door:
    /// "acceptDeadCanvasLinks" or "acceptMissingNames". Null when there is none.
    ///
    /// There are two overridable refusals now and they are not the same
    /// acknowledgement. One says "I accept canvas links this client cannot
    /// follow", which risks the client going down; the other says "I accept a map
    /// with a piece of its scenery missing", which does not. A UI that set both
    /// from one red button would collect consent for a thing nobody was shown,
    /// which is the failure the confirm exists to prevent — so the plan names the
    /// flag rather than leaving the caller to guess from the wording.
    /// </summary>
    public string? BlockedOverrideAccepts { get; set; }

    /// <summary>How long the plan took to build, so a slow archive scan is visible rather than mysterious.</summary>
    public double Seconds { get; set; }

    /// <summary>
    /// The session's structural generation when this plan was built. Shown so a
    /// stale preview is identifiable; it is not what makes the apply safe — see
    /// PortService.Apply.
    /// </summary>
    public int Generation { get; set; }
}

/// <summary>
/// One entry a <c>Match</c> port would DELETE from the target.
///
/// A removal is not a part: a part is something arriving, and these are things
/// leaving, which is the more alarming of the two and was the one with no row of
/// its own. Kept apart so a UI cannot show them in the same table and let a
/// person skim past a deletion thinking it was a copy.
/// </summary>
public sealed class PortRemovalDto
{
    public int Id { get; set; }

    /// <summary>The name the TARGET knows it by — what the user would miss.</summary>
    public string? Name { get; set; }

    /// <summary>The session path of the node that would be deleted.</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>The container it would be deleted from, so the blast radius is visible.</summary>
    public string Container { get; set; } = "";
}

/// <summary>One entry the target already has, named so a conflict count can be judged.</summary>
public sealed class PortConflictDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string TargetPath { get; set; } = "";

    /// <summary>What the target's copy holds right now.</summary>
    public string? Existing { get; set; }
}

/// <summary>An id the user may also want, with the reason it is being suggested.</summary>
public sealed class PortSuggestionDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
}

#endregion

#region Requests

public class PortPlanRequest
{
    public string Kind { get; set; } = "mob";

    /// <summary>
    /// selection | archive.
    ///
    /// Defaults to selection, and should stay that way: a whole-archive port can
    /// replace thousands of nodes in someone's client, and the default has to be
    /// the one whose blast radius fits on a screen.
    /// </summary>
    public string Scope { get; set; } = "selection";

    /// <summary>Session paths of the entries to port. Selection scope only.</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>The archive to take everything from. Archive scope only.</summary>
    public string? SourceFileId { get; set; }

    /// <summary>The archive the entries land in. Required.</summary>
    public string TargetFileId { get; set; } = "";

    /// <summary>Follow <c>info/link</c> and carry the linked entry too. On by default, and it should be.</summary>
    public bool FollowLinks { get; set; } = true;

    /// <summary>
    /// Also carry images a canvas <c>_outlink</c> points at.
    ///
    /// On by default. It was off, on the reasoning that an outlink reaches into
    /// a sibling archive and pulling one in silently is not this feature's
    /// decision to make. That reasoning was wrong about what the user is asking
    /// for: the whole point of a port is not having to go and fetch the pieces
    /// by hand, and an outlink is a piece the entry genuinely needs. Left off,
    /// the copy lands, looks complete in the editor, and renders nothing in
    /// game — which is the exact failure this is supposed to prevent.
    ///
    /// It stays a switch rather than becoming unconditional, so a port that
    /// would drag in half a sibling archive can still be narrowed; and what it
    /// pulled in is reported per image afterwards, never silently.
    /// </summary>
    public bool IncludeArtOutlinks { get; set; } = true;

    /// <summary>
    /// Replace what the target already holds. Off by default and never inferred.
    /// This is the single conflict decision, taken once for the whole port.
    /// </summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Make the target match the source: what it holds, and nothing else.
    ///
    /// Off by default, and only meaningful with <see cref="Overwrite"/>: on its
    /// own it would delete what it is not allowed to replace.
    ///
    /// Copying alone cannot express "this book, as the new build has it". A
    /// build removes skills as well as adding them, so a port that only writes
    /// leaves the ones the new build dropped sitting in the target beside the
    /// ones it added -- a book that never existed in either client. This deletes
    /// those, and only those: an id is removed when the source has that same
    /// container and no such id in it.
    ///
    /// Scoped to the containers the port actually wrote into. Nothing outside
    /// them is looked at, so porting one skill book cannot touch another.
    /// </summary>
    public bool Match { get; set; }

    /// <summary>
    /// Come back and ask rather than write, when the target already has some of
    /// these. On by default, which is what makes a one-click port safe: the
    /// button does the work, and the only thing that can stop it is the one
    /// discovery that could destroy something.
    /// </summary>
    public bool StopOnConflict { get; set; } = true;

    /// <summary>
    /// Write even when the canvas links this port lands are ones the target
    /// client cannot follow.
    ///
    /// Off, and it has to be off: this is the one failure in a port that is not
    /// merely wrong on screen. A v232 client stores its art inline and resolves a
    /// canvas <c>_outlink</c> one level only — "Skill/40000.img/skill/400004114/icon"
    /// is ordinary and "Skill/_Canvas/422.img/skill/4221017/hit/0/8" is a canvas it
    /// cannot reach at all. A blank icon is survivable; a dead link is the skill
    /// window taking the client down with it. Measured: with the whole Skill family
    /// open on both sides a port of 4221017 left 0 such links, and with only
    /// Skill.wz and String.wz open it left 30, every one of them on that id.
    ///
    /// So the plan refuses rather than warns, and this is the way past it — for the
    /// person who knows their target does read the split form, or who is going to
    /// rewrite the links by hand afterwards. Set, the refusal is re-stated as a
    /// warning on the result so it is still on the record.
    /// </summary>
    public bool AcceptDeadCanvasLinks { get; set; }

    /// <summary>
    /// Write even when something the entries name is in neither client.
    ///
    /// Off, because the ordinary cause is an archive nobody opened and the fix is
    /// to open it: a map whose object set is in no Map archive on either side
    /// lands as a room with nothing in it, and finding that out in game is the
    /// failure the whole plan exists to move earlier.
    ///
    /// It is a door rather than a wall because the clients themselves are not
    /// clean, and that was measured rather than assumed. Across all 17,442 maps
    /// of a v232 Map002.wz there are 1,876 distinct names those maps draw by —
    /// backgrounds, tile sets, object sets, marks and music — and four of them
    /// are in no archive that client ships: Back/coordiKing, Obj/starPlanet,
    /// BgmPL2.img/Aburp and PL_Beautyroid.img/Lab. The client runs those maps
    /// anyway. So "this reference does not resolve" cannot mean "this map is
    /// unportable", and a wall here would make a handful of Nexon's own maps
    /// impossible to move for a fault they shipped with.
    ///
    /// Set, the refusal is re-stated as a warning on the result, so the decision
    /// is on the record either way.
    /// </summary>
    public bool AcceptMissingNames { get; set; }
}

public sealed class PortApplyRequest : PortPlanRequest
{
    /// <summary>
    /// Must be true. A separate flag from <see cref="PortPlanRequest.Overwrite"/>
    /// so that a client which forgot a field cannot write anything at all.
    /// </summary>
    public bool Confirmed { get; set; }
}

public sealed class PortResultDto
{
    /// <summary>The plan as it was recomputed at apply time, with per-part results filled in.</summary>
    public PortPlanDto Plan { get; set; } = new();

    /// <summary>
    /// True when the port stopped without writing because the target already has
    /// some of these and the caller has not said what to do about it.
    ///
    /// This is the only thing allowed to interrupt a one-click port. Everything
    /// else a port can discover — a missing name, an absent sound, an art link
    /// that will not resolve — is reported afterwards, because none of it can
    /// destroy something the user already had. A conflict can.
    /// </summary>
    public bool NeedsDecision { get; set; }

    public int Written { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    /// <summary>How long the write took. Reported because a whole-archive port is not instant.</summary>
    public double Seconds { get; set; }

    /// <summary>The label of the single undo entry this port produced, or null when nothing was written.</summary>
    public string? UndoLabel { get; set; }
}

#endregion

#region Choosing what to port

/// <summary>
/// One thing in a source archive that a port could carry, as a picker shows it.
///
/// <see cref="Path"/> is the session path the plan will be given verbatim, taken
/// straight out of the same index <c>PortService.Plan</c> resolves against — so
/// what a user ticks is exactly what gets ported. That is the whole reason this
/// list is served rather than guessed at in the browser: the Database section
/// derives a path from an id by trying conventional names against the tree
/// (see database.js <c>derivePath</c>), which is a guess that can miss, and a
/// port given a path that does not resolve reports the entry as "not a mob in an
/// open archive any more" after the user has already pressed the button.
/// </summary>
public sealed class PortEntryDto
{
    public int Id { get; set; }

    /// <summary>The session path — "f3/8800100.img".</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The name this entry has in the SOURCE client's own String archive, or null.
    ///
    /// Read out of the source client's folder rather than out of the shared name
    /// pool. With two clients open the pool merges both of their String archives
    /// and cannot say which one a name came from, so a mob could show a name that
    /// only the target names — see StringPoolService. Here the client is known,
    /// so it is asked directly.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>The path below the archive root — "Cap/01002357.img" — for the ones that nest.</summary>
    public string Relative { get; set; } = "";

    /// <summary>
    /// True when the target client already holds this id.
    ///
    /// Worth surfacing because of how MapleStory builds actually change: ids are
    /// added, not reassigned, so an id present in both clients is the same thing
    /// in both. Importing it therefore gains nothing and risks overwriting
    /// something the user has already edited — and on a search for "snail",
    /// where most hits exist in every build, it is the difference between
    /// scanning forty rows and seeing the two that are new.
    ///
    /// It is a statement about the id, not about the content: this does not
    /// compare the two entries. A build that genuinely reworked an entry under
    /// the same id would read as "already there" here, which is why the UI
    /// offers it as a filter and a default rather than a refusal.
    /// </summary>
    public bool InTarget { get; set; }
}

/// <summary>
/// One archive the SOURCE client would supply a part from, and whether it is open.
///
/// A port reads external dependencies out of the source client's companion
/// archives: names from String.wz, sounds from Sound.wz, set and shop data from
/// Etc.wz, and visual effects from Effect.wz. A closed archive cannot prove that
/// the selected entry has no row there, so complete selection imports inspect
/// every declared family before planning.
/// </summary>
public sealed class PortSourceArchiveDto
{
    /// <summary>"string", "sound", "etc".</summary>
    public string Role { get; set; } = "";

    /// <summary>The archive family a user would recognise: "String".</summary>
    public string Family { get; set; } = "";

    public bool Open { get; set; }

    /// <summary>What this archive can contribute to a selected entry.</summary>
    public string Carries { get; set; } = "";
}

/// <summary>
/// What one open archive holds that could be ported, searchable by name or id.
/// </summary>
public sealed class PortEntriesDto
{
    public string FileId { get; set; } = "";
    public string Archive { get; set; } = "";
    public bool ReadOnly { get; set; }

    /// <summary>The client the archive belongs to, by its folder's leaf name.</summary>
    public string Client { get; set; } = "";
    public string ClientFolder { get; set; } = "";

    /// <summary>The port kind this archive holds, or null when it holds none.</summary>
    public string? Kind { get; set; }
    public string? Label { get; set; }
    public string? Plural { get; set; }

    /// <summary>Every kind this archive could be listed as; more than one is possible in principle.</summary>
    public List<string> Kinds { get; set; } = new();

    /// <summary>
    /// False when nothing here can be carried, with <see cref="Reason"/> saying
    /// why in the same words the port dialog would. An archive with no kind at
    /// all — Effect.wz, UI.wz — and a kind that is declared and refused —
    /// Map.wz — are different answers and both are said out loud.
    /// </summary>
    public bool Supported { get; set; }
    public string? Reason { get; set; }

    /// <summary>Entries of this kind in the archive, before the query narrowed them.</summary>
    public int Total { get; set; }

    /// <summary>
    /// What every entry in the archive weighs, so "take the whole archive" can say
    /// its own size before it is pressed rather than being refused after.
    ///
    /// Compared against <c>PortCapabilitiesDto.MaxArchiveBytes</c>: a v232 Skill.wz
    /// is 1,247 MB against a 1,024 MB cap, and the honest place to learn that is on
    /// the button, not in a plan that ran for ten seconds first.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>The cap a whole-archive port is refused above, echoed so the UI needs no second call.</summary>
    public long MaxArchiveBytes { get; set; }

    /// <summary>Entries one selection may carry — the ceiling on "take this whole folder".</summary>
    public int MaxSelection { get; set; }

    /// <summary>How many matched the query. More than <see cref="Results"/> holds when truncated.</summary>
    public int Matched { get; set; }

    public bool Truncated { get; set; }

    public List<PortEntryDto> Results { get; set; } = new();

    /// <summary>
    /// False when the source client has no String archive open, which is also why
    /// every row's name is null. Searching by name is impossible in that state and
    /// the UI has to say so rather than showing "nothing matches".
    /// </summary>
    public bool NamesAvailable { get; set; }

    /// <summary>The source-side archives this kind needs, and whether each is open.</summary>
    public List<PortSourceArchiveDto> Sources { get; set; } = new();
}

#endregion
