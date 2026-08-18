using System.Diagnostics;
using System.Globalization;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleLib.Helpers;
using Microsoft.Xna.Framework.Graphics;
using MapleBench.Models;
using MapleBench.Services.Composition;

namespace MapleBench.Services;

/// <summary>
/// One thing that has to travel with an entry but does not live beside it.
///
/// A mob's name is in String.wz/Mob.img; an item's is in String.wz/Eqp.img or
/// Consume.img or Cash.img depending on what it is. Rather than derive that from
/// the id — which is where a name ends up filed under the wrong category,
/// invisible in game and very hard to trace back — the images are listed and the
/// entry is looked for in all of them. Whichever one the SOURCE client filed it
/// under is the one the target gets, mirrored exactly.
/// </summary>
/// <param name="Kind">The part kind on the wire: "string", "sound" or "shop".</param>
/// <param name="Role">The archive role that holds it: "string", "sound" or "etc".</param>
/// <param name="Images">Images to probe, in order.</param>
/// <param name="MatchField">
/// When set, the entry is not keyed by the id — it is a row whose child of this
/// name holds the id. Etc.wz/Commodity.img is the case: its rows are named "0",
/// "1", "2" … and carry <c>ItemId</c> inside, so a cash item's listing has to be
/// found by scanning values rather than by a name lookup.
/// </param>
/// <param name="UniqueFields">
/// Child values that must not already exist in the target. A Commodity row's
/// <c>SN</c> is the serial the client buys by; two rows sharing one is a broken
/// shop, so a collision is refused rather than resolved.
/// </param>
/// <param name="NameField">
/// The child of a row that holds the display name, when it is not called
/// <c>name</c>. Measured on a v232 client: every String.wz image keys its label
/// as <c>name</c> except Map.img, whose rows are
/// <c>{ streetName = "Maple Road", mapName = "Maple Tree Hill" }</c> — so a map
/// plan read with the default showed 17,442 bare ids and no names at all.
/// </param>
/// <param name="KeyPath">
/// When set, the satellite is keyed by an id read out of the entry at this
/// slash-separated path rather than by the entry's own id. An equip's
/// <c>info/setItemID</c> is the important case: the equip is item 0100..., while
/// its row in <c>Etc.wz/SetItemInfo.img</c> is named by a small set id. Treating
/// those as the same id space either finds nothing or copies an unrelated row.
/// </param>
/// <param name="RequiredWhenReferenced">
/// Whether finding a key at <paramref name="KeyPath"/> makes this part required
/// for the entry itself to be written. Set definitions use this: copying an
/// equip while leaving its set id pointed at no definition (or at a different
/// target definition) removes the set window and changes its bonuses in game.
/// </param>
public sealed record PortSatelliteSpec(
    string Kind,
    string Role,
    string[] Images,
    string? AbsentNote,
    string? MatchField = null,
    string[]? UniqueFields = null,
    bool EveryImage = false,
    string NameField = "name",
    string? KeyPath = null,
    bool RequiredWhenReferenced = false);

/// <summary>
/// One place inside an entry that names another node it needs.
///
/// Read out of a real v232 client rather than assumed: Zakum's 8800000.img
/// carries <c>info/revive/0 = 8800001</c> (the arm it summons) and
/// <c>info/skill/0/skill = 140</c> (a mob skill), so a boss's parts and its
/// attacks are declared in the data, not left to the id-numbering convention.
/// Both are ordinary edges of the same graph walk as <c>info/link</c>.
/// </summary>
/// <param name="Path">
/// Slash-separated, with "*" matching every child at that level:
/// "info/link", "info/revive/*", "info/skill/*&#47;skill".
/// </param>
/// <param name="Role">
/// Null means the id is another entry of the same kind. Otherwise the archive
/// role that holds it, with <paramref name="Image"/> naming the image inside.
/// </param>
public sealed record PortReference(string Path, string? Role = null, string? Image = null);

/// <summary>
/// One kind of thing that can be carried from one client to another, and every
/// place a copy of it has to land.
///
/// This record is the whole reason the service below is not a mob feature, and
/// then an item feature bolted beside it. A mob is <c>Mob.wz/&lt;id&gt;.img</c>
/// plus <c>String.wz/Mob.img/&lt;id&gt;</c> plus <c>Sound.wz/Mob.img/&lt;id&gt;</c>;
/// an item is the same sentence with different nouns. Adding a kind is adding a
/// row here — no new branch in the machinery.
/// </summary>
/// <param name="ArchivePrefixes">
/// The archive families entries live in, as <see cref="WzSessionService.StripArchiveSuffix"/>
/// reports them. "Mob" matches Mob.wz, Mob001.wz and Mob2.wz. Plural because an
/// item is genuinely in two: a consumable is in Item.wz and an equip is in
/// Character.wz, and the client decides by id range.
/// </param>
/// <param name="UsesContainers">
/// True when an entry can be a property inside a shared image rather than an
/// image of its own — <c>Item.wz/Cash/0510.img/05100000</c>. It costs a parse of
/// every non-id-named image in the archive to find those, so it is opt-in: a
/// Mob.wz whose images are all id-named never pays it.
/// </param>
/// <param name="References">
/// Where inside an entry to find ids it depends on, and what they are ids of.
/// This is what turns the closure into a graph walk driven by a table rather
/// than a pile of per-kind special cases: <c>info/link</c>, <c>info/revive/*</c>
/// and <c>info/skill/*&#47;skill</c> are three edges of the same walk, and a
/// canvas <c>_outlink</c> is a fourth that needs no declaration because it
/// carries its own path.
///
/// <c>info/link</c> in particular is not an optimisation. A v232
/// <c>Mob.wz/8800100.img</c> (Chaos Zakum), <c>Npc.wz/1104103.img</c> (Eckhart)
/// and <c>Reactor.wz/2001013.img</c> all hold nothing but an <c>info</c> block
/// naming another id, so a port that copies only the requested image produces
/// something that draws nothing at all. This used to be declared in a separate
/// <c>LinkKeys</c> field that no code read, which is exactly how npc and reactor
/// came to be shipped with the link silently unfollowed.
/// </param>
/// <param name="Requirements">
/// Ids the entry names that this will <em>not</em> copy, and what they are ids
/// of. A quest's Check.img names the NPC you hand it in to and the mobs you have
/// to kill; its Act.img hands out items. Copying those would drag half a client
/// along, and not saying they are needed is how someone ends up with a quest
/// that starts and cannot be finished. So they are counted, checked against the
/// target when the archive that would hold them is open, and reported per entry.
/// </param>
/// <param name="EntryWrapper">
/// A fixed node between a container image and its entries. A skill lives at
/// <c>Skill.wz/0100.img/skill/1001000</c> — the <c>skill</c> level is not an id
/// and is not a category, it is always called that. Null for kinds whose entries
/// sit directly in the image.
/// </param>
/// <param name="EntryImages">
/// When set, the only container images that hold entries. A v232 Quest.wz keeps
/// its quests in QuestInfo.img and also holds PQuest.img, QuestDestination.img,
/// QuestExpByLevel.img and QuestPerformByDay.img, whose children are numeric
/// too — 1202, 57104, 51200, 51214. Indexing those as quests would make a
/// quest's identity depend on which image the archive happens to list first.
/// </param>
/// <param name="ContainerNameDigits">
/// The longest all-digit image name that is still a <em>container</em> rather
/// than an entry. Zero means every id-named image is an entry.
///
/// This exists because "the name parses as a number" does not tell the two
/// apart. Measured on a v232 client: <c>Item.wz/Consume/0200.img</c> and
/// <c>Item.wz/Cash/0510.img</c> are containers holding 8-digit item ids, while
/// <c>Item.wz/Pet/5000545.img</c> — 1,107 of them — is an entry image; and
/// <c>Skill.wz/1000.img</c> is a book holding <c>skill/10001005</c> while its
/// own name parses as 1000. Without this the index recorded the book as "skill
/// 1000" and never looked inside, so the target index held no skills at all and
/// every skill already in the target was previewed as New — a conflict count of
/// zero for a port that would then fail on every part.
/// </param>
/// <param name="ContainerDepth">
/// How many levels inside a container image an entry may sit at. The walk stops
/// at the first id-named node, so this only counts the category levels above it:
/// <c>String.wz/Eqp.img/Eqp/Cap/1002357</c> needs 3, and <c>Consume.img/2000000</c>
/// needs 1. Bounded per kind rather than unlimited for the reason
/// <see cref="PortKindSpec.MaxDepth"/> gives about directories.
/// </param>
/// <param name="IdsAreUniquePerImage">
/// True when an id only identifies an entry together with the image it is in.
/// String.wz is the case and the only one: <c>Npc.img/2100</c> is Sera and
/// <c>Map.img/2100</c>… is some other client's business entirely. With a single
/// id-keyed index per archive, porting one of those would report a conflict
/// against the other, and the target's copy of an unrelated node would be
/// offered up for overwriting.
/// </param>
/// <param name="MaxDepth">
/// How many directory levels below the archive root entries can be found at.
/// Mob.wz needs 1, Character.wz and Item.wz need 2 (a category folder), and
/// anything deeper is a different archive shape that should be declared rather
/// than stumbled into.
/// </param>
/// <param name="InertFlags">
/// Fields that decide whether the client will let the entry be USED, reported
/// per entry when it carries one. See <see cref="PortInertFlag"/>: a correct
/// copy of an entry the source itself marks unusable is still an entry nothing
/// happens on, and this is the only place that says so.
/// </param>
/// <param name="Animations">
/// Names the entry uses out of another archive of the same client, checked
/// against the target where that archive is open. See <see cref="PortAnimation"/>.
/// </param>
/// <param name="Named">
/// Nodes the entry names by NAME which this port carries and, where the target
/// already has something else of that name, carries under a different one — with
/// the entry's own reference rewritten to match. See <see cref="PortNamedRef"/>:
/// this is what a map's scenery, its background music and its map mark are, and
/// it is the difference between merging a map into a client and overwriting the
/// pictures every other map there was already using.
/// </param>
public sealed record PortKindSpec(
    string Kind,
    string Label,
    string Plural,
    string[] ArchivePrefixes,
    bool UsesContainers,
    PortSatelliteSpec[] Satellites,
    bool Supported,
    string? UnsupportedReason,
    PortReference[]? References = null,
    PortRequirement[]? Requirements = null,
    string? EntryWrapper = null,
    string[]? EntryImages = null,
    int ContainerNameDigits = 0,
    int ContainerDepth = 1,
    bool IdsAreUniquePerImage = false,
    int MaxDepth = 2,
    PortInertFlag[]? InertFlags = null,
    PortAnimation[]? Animations = null,
    PortNamedRef[]? Named = null);

/// <summary>
/// One place inside an entry that names an id this port will not carry, and what
/// that id is an id of.
/// </summary>
/// <param name="Part">
/// Which node to read it out of: <c>"entry"</c>, or the <see cref="PortSatelliteSpec.Kind"/>
/// of one of the satellites — a quest's conditions live in its Check.img row,
/// not in QuestInfo.img.
/// </param>
/// <param name="Path">Slash-separated with "*" for every child, as <see cref="PortReference.Path"/>.</param>
/// <param name="Needs">The kind whose archive would hold it: "npc", "item", "mob", "skill", "quest".</param>
/// <param name="Label">How to name it in the note: "hands in to NPC", "kills mob".</param>
/// <param name="When">
/// A sibling of the id that has to hold a given value for this row to apply.
///
/// A map's <c>life</c> list is the case, and it cannot be expressed any other
/// way: measured on a v232 client's Map002.wz/Map/Map0/000110000.img, every one
/// of its six spawns is <c>{ type = "n", id = "1520070" }</c> — the id alone
/// does not say whether it is a mob or an NPC, its <em>sibling</em> does. Two
/// rows on the same path, one keyed <c>type = "m"</c> and one <c>type = "n"</c>,
/// is the honest reading; a single row would check every NPC id against Mob.wz
/// and report an entire town as missing monsters.
/// </param>
/// <param name="Ignore">
/// One value at this path that means "nothing", not "id zero".
///
/// Also measured on that map: three of its four portals carry
/// <c>tm = 999999999</c>, which is how a spawn point says it goes nowhere, and
/// <c>info/forcedReturn</c> uses the same sentinel. Without this, the commonest
/// map in the client reports a portal to a map no client has ever had.
/// </param>
public sealed record PortRequirement(
    string Part,
    string Path,
    string Needs,
    string Label,
    (string Field, string Value)? When = null,
    int Ignore = 0);

/// <summary>
/// A field whose presence in an entry changes what the client will let a user DO
/// with it, rather than what it looks like.
///
/// It exists because the port's other checks all ask structural questions —
/// does this node exist, does that link resolve, does the target know this field
/// — and every one of them can pass on an entry the client will refuse to use.
/// A copy that is byte-for-byte the source's is the CORRECT copy and can still
/// be a skill nothing happens when you press, because the source's own data says
/// so. Nothing is altered on the strength of one of these: the note says what
/// the field means and leaves the decision where it belongs.
/// </summary>
/// <param name="Field">The child of the entry to look for. Only a non-zero value counts.</param>
/// <param name="Note">
/// Said after "This one ", so it reads as a sentence about the entry: "This one
/// carries 'psd = 1', which…".
/// </param>
public sealed record PortInertFlag(string Field, string Note);

/// <summary>
/// A place inside an entry that names something by NAME rather than by id, in
/// another archive of the same client.
///
/// A skill's <c>action/0</c> is the case this exists for, and it is the one
/// reference in a skill that no amount of copying can satisfy: it is not an id
/// in a Skill archive, it is the name of a character animation in Character.wz,
/// and the client resolves it before it will send the use-skill packet. Measured
/// on a real port into a v232 client: 4221017's <c>action/0 = 'cruelStab'</c>
/// exists in no <c>0000200*.img</c> that client has, so the skill arrives whole,
/// draws its icon, opens its window and does nothing at all when it is pressed —
/// while 4221052, whose <c>'HY422veilOfShadow'</c> that client does have, casts.
/// Every structural check passes on both.
///
/// It is reported and never carried. One action is not one node: the body image
/// has it, and so does every weapon, coat, hat and glove image that animates
/// with it, which is a port of Character.wz and not of a skill.
/// </summary>
/// <param name="Path">Where the names are, as <see cref="PortReference.Path"/>: "action/*".</param>
/// <param name="Archive">The family stem of the archive that holds them: "Character".</param>
/// <param name="ImagePrefix">
/// The images inside it to look in. A client keeps one body image per skin —
/// <c>00002000.img</c> to <c>00002013.img</c> — and they carry the same action
/// list, so any of them answers and all of them are asked before a name is
/// called missing.
/// </param>
/// <param name="What">How to name it in the note: "character animation".</param>
public sealed record PortAnimation(string Path, string Archive, string ImagePrefix, string What);

/// <summary>
/// A place inside an entry that names a node this port CAN carry — by name, in a
/// namespace the two clients share and neither owns.
///
/// This is what a map is made of, and it is the whole reason the map kind was
/// refused for so long. Measured on a v232 client:
/// <c>Map002.wz/Map/Map0/000110000.img</c> is 51,482 bytes and names four
/// pictures — <c>back/*/bS = "mapleIsland"</c> (Map001.wz/Back/mapleIsland.img,
/// 2,980,575 bytes), <c>0/info/tS = "grassySoil2"</c> (Map.wz/Tile, 49,700),
/// <c>0/obj/*/oS = "acc1"</c> (Map.wz/Obj, 21,732,097) and <c>"connect"</c>
/// (1,424,936). 26 MB of scenery for a 51 KB map, and it is not the map's: of
/// the 87 maps in that one folder, 666 object placements name acc1, 523 name
/// connect and 442 backgrounds name mapleIsland.
///
/// The thing that makes it tractable, and it was not obvious: two clients of one
/// game SHARE that library rather than owning rival copies of it. Measured across
/// a real pair, the 17,613 source maps make 1,931,467 art references and
/// 1,929,186 of them — 99.882% — already resolve in the target untouched;
/// 17,494 of those maps need not one byte of scenery copied; and exactly seven
/// set names, 41.6 MB in all, are absent from the target altogether. So the
/// mechanism is CHECK, and copying is the exception:
/// <list type="bullet">
/// <item>the target's set holds every piece this entry draws, identically —
/// use it, copy nothing, rewrite nothing. This is 99% of the answers;</item>
/// <item>the target has no set of that name at all — copy it under that name,
/// nothing clashes;</item>
/// <item>the target has the set and it cannot serve a piece this entry draws —
/// copy the source's under a name nothing in the target uses, and rewrite this
/// entry's reference to it. The escape hatch, not the road.</item>
/// </list>
/// Asking per PIECE rather than per image is what makes the first branch
/// reachable at all. Two builds of one object set differ SOMEWHERE far more often
/// than they differ at the twenty addresses one map uses, and an
/// is-this-whole-book-identical test therefore falls through to the escape hatch
/// constantly — copying 21.7 MB and renaming, to avoid a difference nobody
/// could see.
///
/// That third branch is safe for a measured reason rather than a hopeful one:
/// everything <em>inside</em> one of these images is addressed
/// relative to the image, never by a global index. A v232 object placement is
/// <c>{ oS = "acc1", l0 = "mapleIsland", l1 = "maple", l2 = "0" }</c> — three
/// names below the image — a tile is <c>{ u = "bsc", no = 4 }</c> and a
/// background is <c>{ bS = "mapleIsland", ani = 0, no = 3 }</c>. Rename the
/// image and rewrite <c>oS</c>/<c>tS</c>/<c>bS</c>, and every one of those still
/// resolves, because they resolve inside the copy. Nothing else in any archive
/// names these images at all.
///
/// The fourth outcome, overwriting the target's image with the source's, is the
/// one this exists to make impossible. It is what "copy the map across" does by
/// hand, and the blast radius was measured: a set is named by 35 other maps at
/// the median and 4,218 at the ninetieth percentile, and 366 sets are themselves
/// read by other sets through an <c>_outlink</c>. There is no branch here that
/// can do it.
///
/// What a renamed copy does NOT fix is also measured, and is reported rather
/// than papered over: those sets carry 8,688 absolute <c>_outlink</c>s, every one
/// naming a DIFFERENT set and none naming its own image, so a renamed copy goes
/// on reading the older build through them. Their <c>_inlink</c>s (11,695) and
/// UOLs (3,040) are image-relative and travel correctly. See StraysNote.
/// </summary>
/// <param name="Path">
/// Where the name is written inside the entry, with "*" matching every child at
/// that level, as <see cref="PortReference.Path"/>: "back/*&#47;bS",
/// "*&#47;info/tS", "*&#47;obj/*&#47;oS", "info/bgm", "info/mapMark".
/// </param>
/// <param name="Role">
/// The archive role that holds the named node — "map" for scenery and the map
/// mark, "sound" for the background music. Resolved across every archive of
/// that role the client has open, because a v232 client splits one directory
/// over several files: its Back is 318 images in Map001.wz and 148 more in
/// Map2.wz, its Obj is 211 in Map.wz and 125 in Map2.wz.
/// </param>
/// <param name="Under">
/// The fixed path inside that archive above the named node: "Back", "Obj",
/// "Tile", "MapHelper.img/mark". Empty when <paramref name="Split"/> carries it.
/// </param>
/// <param name="Image">
/// True when the value names a whole <c>.img</c> under <paramref name="Under"/>
/// — <c>"mapleIsland"</c> means <c>Back/mapleIsland.img</c>. False when it names
/// a property inside an image, as a map mark does.
/// </param>
/// <param name="Split">
/// True when the value is itself an image-and-node path. <c>info/bgm</c> is the
/// one: measured, it reads <c>"Bgm34/WoundedLeaf"</c>, which is
/// <c>Sound002.wz/Bgm34.img/WoundedLeaf</c> — one clip out of the twelve in a
/// 15,297,459-byte image, so the image is never the unit that travels.
/// </param>
/// <param name="What">How to name it: "background", "tile set", "object set".</param>
/// <param name="Nothing">
/// The one value at this path that names nothing at all rather than something
/// missing. Measured: <c>info/mapMark = "None"</c> is used by real maps and
/// <c>MapHelper.img/mark</c> has 303 children, none of them called "None" — so
/// without this the commonest sentinel in the file reads as a broken reference
/// and refuses the port.
/// </param>
/// <param name="Places">
/// Where the individual placements that draw out of this set live, relative to
/// the entry — "back/*", "*&#47;obj/*", "*&#47;tile/*".
///
/// This is what turns the question from "is the target's copy of acc1 the same
/// image" into "does the target's acc1 hold the pieces THIS map asks it for",
/// and the difference is the whole cost model. Measured across the source
/// library: 99.3% of the set names a map draws by already exist in the target
/// and 99.4% of those are address-for-address identical, so asking per address
/// answers "copy nothing" for 99.32% of maps outright. Asking per image instead
/// answers it by hashing 21,732,097 bytes, and answers it WRONG whenever the two
/// builds differ anywhere in a set at an address this map never touches — which
/// would rename and copy a 21.7 MB book to avoid a difference nobody could see.
/// </param>
/// <param name="PlaceName">
/// Where a placement says which set it draws from, relative to the placement,
/// with ".." for a level up.
///
/// Two shapes, both measured: a background and an object name their set on the
/// placement itself (<c>bS</c>, <c>oS</c>), and a tile does not — a tile is
/// <c>{ u = "bsc", no = 4 }</c> and the set is on the layer above it, at
/// <c>info/tS</c>. Without the hop, tiles could not be address-checked at all.
/// </param>
/// <param name="Address">
/// The path below the set image that one placement draws, as field names read
/// off the placement: <c>{ "l0", "l1", "l2" }</c> for an object,
/// <c>{ "u", "no" }</c> for a tile. A segment written <c>"ani?ani:back"</c> is a
/// flag: a background with <c>ani = 1</c> draws from the set's <c>ani</c>
/// branch and one with <c>ani = 0</c> from its <c>back</c> branch, and taking
/// either blindly asks about a frame the map does not use.
///
/// Null for a reference whose name IS the node — a map mark and a music clip
/// have nothing below them to address.
/// </param>
public sealed record PortNamedRef(
    string Path,
    string Role,
    string Under,
    bool Image,
    bool Split,
    string What,
    string? Nothing = null,
    string? Places = null,
    string? PlaceName = null,
    string[]? Address = null,
    WzNamedRole? SetRole = null);

/// <summary>
/// Moves content between two open clients in one undoable action.
///
/// The problem it exists for: porting something from a newer client is not one
/// copy. Copying <c>Mob.wz/8800100.img</c> on its own gives an unnamed, silent
/// mob that draws nothing, because its name is in String.wz, its sounds are in
/// Sound.wz, and on a v232 client that image is <em>only</em> an <c>info</c>
/// block pointing at <c>8800000.img</c> for every frame it renders. An item is
/// worse: <c>Item.wz/Cash/0510.img/05100000</c> is a property inside a shared
/// image, so the copy has to create that image in the target if it is missing
/// and insert into it if it is not, without disturbing the other 582 items in
/// it. People get all of this wrong because the one copy visibly succeeds.
///
/// Like every other mode here, this is a projection and not a second editor:
/// every write goes through <see cref="WzEditService"/> inside one
/// <see cref="UndoService.Batch"/>, so a port shares one dirty state, one undo
/// history and one save pipeline with everything else — and it is one Ctrl+Z.
///
/// Two house rules shape the API. The plan is computed before anything is
/// written and the apply recomputes it rather than trusting the client, so a
/// preview and a run cannot disagree. And nothing the target already holds is
/// ever replaced unless the caller asked for it in as many words: losing a
/// target's item to an import is worse than not importing.
///
/// It is not the importer. <see cref="ClientImportService"/> converts a
/// split-format archive into a classic .wz — a change of format. This moves
/// content between two archives that are already open, and changes no format at
/// all.
/// </summary>
public sealed class PortService
{
    /// <summary>
    /// Entries one <em>selection</em> port may carry, including the ones links
    /// pull in.
    ///
    /// A port is one undo entry holding one clone per node, and a v232 boss image
    /// runs to tens of megabytes — so the ceiling is about memory and about a
    /// preview a person can actually read. The whole-archive scope has its own
    /// ceiling, measured in bytes rather than entries; see
    /// <see cref="MaxArchiveBytes"/>.
    /// </summary>
    public const int MaxSelection = 60;

    /// <summary>
    /// How many entries a plan will parse to follow their links before it stops
    /// following links at all.
    ///
    /// Following a link means parsing the entry, and a parse is the expensive
    /// thing here: measured at 2.7 ms an image on a v232 client, so 2,742 mobs
    /// is roughly ten seconds — per preview, and a preview is meant to be
    /// something you press without thinking. 400 is about one second, which is
    /// a wait a preview can carry, and it covers the case this exists for: a
    /// few hundred equips out of one folder, each of which may point at art in
    /// another archive.
    ///
    /// Past it the links are skipped and <see cref="Limits"/> says so plainly,
    /// because a port that quietly stops following them produces a copy that
    /// looks complete and renders nothing.
    /// </summary>
    public const int MaxParsedEntries = 400;

    /// <summary>
    /// How much of the target to read before deciding which canvas encodings it
    /// knows. Enough to see the ones it uses in bulk; small enough that asking
    /// costs nothing next to the port itself.
    /// </summary>
    /// <summary>
    /// The canvas ceiling is a backstop against a pathological image, not the
    /// sampling rule. It used to be both, and it made the sample worthless:
    /// measured on a v232 Skill.wz, the first four root images (1100.img,
    /// 412.img, 1000.img, 1211.img) hold 4,333 canvases between them, so a
    /// 4,000-canvas cap stopped after 4 images of 234 and reported a vocabulary
    /// of {Format1, Format1026}. The archive's real vocabulary across all 234
    /// images and 118,831 canvases is Format1 (103,030), Format2050 (10,349),
    /// Format2 (4,044) and Format1026 (1,408) — so Format2050, a tenth of the
    /// client's own art, was judged unsupported and every DXT5 frame in the port
    /// was re-encoded against a verdict that was an artefact of where the walk
    /// happened to stop.
    /// </summary>
    private const int FormatSampleCanvases = 250_000;
    private const int FormatSampleImages = 200;

    /// <summary>
    /// Source bytes a whole-archive port may carry.
    ///
    /// The undo entry holds a full in-memory clone of every node it copied, and
    /// the parsed originals stay resident beside them, so the working set is
    /// roughly three times this. Measured twice on a v232 client: porting 400 mob
    /// images out of Mob001.wz (317 MB of source content) took the process from
    /// 412 MB to 1.34 GB, and porting a whole Npc.wz — 10,742 entries, 1,006 MB,
    /// just inside this cap — took it from 106 MB to 2.89 GB in 19.3 s. 1 GB is
    /// therefore about as far as this can go on a machine that is also holding two
    /// clients open, and the honest thing at the boundary is to refuse and say the
    /// number rather than to start and die halfway.
    /// </summary>
    public const long MaxArchiveBytes = 1_024L * 1024 * 1024;

    /// <summary>
    /// Entries listed in full in a plan. The rest are counted, not listed.
    ///
    /// A whole Mob.wz is 2,742 entries and four parts each; serialising that is
    /// megabytes of JSON describing a table nobody can read. The totals are the
    /// answer at this scale, and this is the sample under them.
    /// </summary>
    private const int MaxListedItems = 200;

    /// <summary>Conflicts named individually, so a count of 4,000 can still be judged.</summary>
    private const int MaxConflictSample = 100;

    /// <summary>
    /// How far <c>info/link</c> is chased. Real clients use one hop; a cycle
    /// (8800100 -> 8800000 -> 8800100) is not something the format forbids, and
    /// the visited set already stops it — this is the second belt.
    /// </summary>
    private const int MaxLinkDepth = 4;

    /// <summary>
    /// Target entries sampled to learn which <c>info</c> keys that client's own
    /// content uses.
    ///
    /// Kept small on purpose. Parsing an entry loads every frame it owns, so this
    /// is memory as much as time — <see cref="ImageMemoryService"/> exists
    /// because of it. Already-parsed entries are taken first and cost nothing;
    /// only the shortfall is parsed, at a measured 2.7 ms an image, so the worst
    /// case is about 110 ms and 40 resident images.
    /// </summary>
    private const int TargetSampleSize = 40;

    /// <summary>
    /// Nodes walked when looking for canvas links in one source entry. A v232
    /// boss image is a few thousand nodes; the cap is a backstop against a
    /// pathological one, and the plan says so when it bites rather than quietly
    /// reporting "no links".
    /// </summary>
    private const int MaxLinkWalk = 40_000;

    /// <summary>
    /// Suggested nearby ids across a whole plan. A guess list longer than a
    /// screenful stops being a hint and becomes a second archive listing.
    /// </summary>
    private const int MaxSuggestions = 60;

    /// <summary>Bounded like every other cache in the app; the key comes off the wire.</summary>
    private const int MaxCachedIndexes = 8;

    /// <summary>
    /// Nodes compared when asking whether two scenery images are the same
    /// picture. See <see cref="TreeDigest"/>: the question is being asked about a
    /// 21,732,097-byte object book and the first few hundred nodes settle it.
    /// </summary>
    private const int MaxDigestNodes = 400;

    /// <summary>
    /// Art read while asking that question, per image. A v232 Obj/acc1.img is
    /// 21,732,097 bytes and a map names four of these, so an unbounded comparison
    /// would read tens of megabytes off both clients before the preview could
    /// draw a single row.
    /// </summary>
    private const long MaxDigestBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How deep that comparison goes. A v232 object placement is four levels —
    /// <c>Obj/acc1.img/mapleIsland/maple/0/0</c> — and a background is three, so
    /// six reaches past every one of them with room to spare.
    /// </summary>
    private const int MaxDigestDepth = 6;

    /// <summary>
    /// Distinct names tried for one scenery image before the port gives up.
    ///
    /// Reaching this means a client already holds twenty different pictures under
    /// twenty spellings of one name, which is not a collision to resolve — it is
    /// a client somebody has ported into twenty times and should tidy up.
    /// </summary>
    private const int MaxDistinctNames = 20;

    /// <summary>
    /// How much of a client's folder name goes into a renamed scenery image. Long
    /// enough to say which client, short enough that the name stays a name.
    /// </summary>
    private const int MaxSlugLength = 16;

    /// <summary>
    /// Nodes the dependency closure may pull in beyond what was selected.
    ///
    /// The closure follows every reference a copied node makes — <c>info/link</c>,
    /// canvas <c>_outlink</c>, UOL — transitively and across archives, because a
    /// reference that does not resolve in the target is the "fine in the editor,
    /// broken in game" failure this whole feature exists to stop. Bounded because
    /// a closure from a busy node can reach a large fraction of a client, and the
    /// one thing that must never happen is a port that quietly stops half way and
    /// reports success. Past this it refuses and says the size; it does not
    /// truncate.
    /// </summary>
    private const int MaxClosureEntries = 400;

    /// <summary>
    /// How many hops of linked images may ride along behind one copied scenery
    /// set before the port refuses the set outright.
    ///
    /// A copied set's absolute <c>_outlink</c>s name OTHER images, and when the
    /// target cannot serve one of those identically the image it names is
    /// carried too — which is transitive, because the carried image has links of
    /// its own. Measured on a real client the real chains are one hop (an Obj
    /// set reaching into Back); three covers that with room, while a chain that
    /// outruns it is a sign the "carry" has become a copy of the library, which
    /// is the thing this design exists not to do. Past the bound the SET is
    /// refused with the chain named — a shell that draws the wrong build's art
    /// behind its own frames is not shipped quietly.
    /// </summary>
    private const int MaxCarriedLinkDepth = 3;

    /// <summary>
    /// The most bytes of linked images one copied set may drag in with it.
    ///
    /// The biggest single set measured is 21.7 MB, so this allows a set to bring
    /// a couple of full-size books and no more. Like the depth bound, hitting it
    /// refuses the set with the chain and the cost named rather than carrying
    /// part of a closure — a partially-carried closure is the shell problem
    /// moved one hop further out.
    /// </summary>
    private const long MaxCarriedLinkBytes = 64L * 1024 * 1024;

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly UndoService _undo;
    private readonly ILogger<PortService> _log;

    /// <summary>
    /// Per archive: every entry in it keyed by id, and the structural generation
    /// it was read at.
    ///
    /// Keyed on <see cref="WzSessionService.StructureGeneration"/> and not on
    /// <see cref="WzSessionService.Generation"/>, and that is a deliberate, narrow
    /// claim: this holds which nodes exist and what they are called, and a node
    /// can only appear, vanish or be renamed structurally. Editing an item's
    /// price ticks the value generation and cannot change this — which is exactly
    /// the split <c>ValueChanges()</c> exists to let a cache exploit.
    /// </summary>
    private readonly Dictionary<string, (int Structure, EntryIndex Entries)> _indexes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per archive and image prefix: every animation name its body images carry.
    /// See <see cref="AnimationsIn"/> — one entry costs a parse of a body image,
    /// so it is read once and kept until the session's shape changes.
    /// </summary>
    private readonly Dictionary<string, (int Structure, HashSet<string> Names)> _animations =
        new(StringComparer.Ordinal);

    /// <summary>Per target archive: the <c>info</c> keys its own entries use.</summary>
    /// <summary>
    /// The sampled canvas-format vocabulary per target archive, keyed on the
    /// session's structural generation — so a preview pays for the sample once,
    /// not once per keypress, and an apply that follows a preview reads the
    /// vocabulary that preview showed.
    /// </summary>
    private readonly Dictionary<string, (int Structure, HashSet<WzPngFormat> Formats)> _formatVocabulary =
        new(StringComparer.Ordinal);

    /// <summary>
    /// What applies have claimed to land, per archive file id, awaiting the save
    /// that makes the claim checkable — session path to the label the plan
    /// showed. See <see cref="CheckSavedArchive"/>.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _saveClaims =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, (int Structure, HashSet<string> Keys, int Sampled)> _targetKeys =
        new(StringComparer.Ordinal);

    public PortService(
        WzSessionService session,
        WzEditService edit,
        StringPoolService strings,
        UndoService undo,
        ILogger<PortService> log)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _undo = undo;
        _log = log;
    }

    #region Catalog

    private static readonly PortSatelliteSpec MobString =
        new("string", "string", new[] { "Mob.img" },
            "Without this the mob shows in game as its id.");

    private static readonly PortKindSpec[] AllKinds =
    {
        new("mob", "Mob", "Mobs",
            ArchivePrefixes: new[] { "Mob" },
            UsesContainers: false,
            Satellites: new[]
            {
                MobString,
                // Plural because it genuinely is: a v232 Sound.wz keeps a mob's
                // hit and death sounds in Mob.img and its boss voice lines in
                // MobVoice.img, and porting one without the other gives a silent
                // boss.
                new PortSatelliteSpec("sound", "sound", new[] { "Mob.img", "MobVoice.img" }, null,
                    EveryImage: true),
                // The two Effect.wz tables the census found for this kind, now
                // that Effect.wz has a role. Both are small and both are real:
                // ChaseEffect.img has 2 rows, 100% mob ids, and MobEff.img has
                // 4 mob-id rows among its 14 children -- it is a mixed image,
                // keyed by name for the rest, which is why the lookup is by id
                // and a miss is ordinary rather than suspicious.
                //
                // Effect.wz/EliteMobEff.img is deliberately NOT here: its 28
                // children are a dense 0..27 index, 0% mob ids. See
                // PortSatellites for the refusals and their numbers.
                new PortSatelliteSpec("chase-effect", "effect", new[] { "ChaseEffect.img" },
                    "Only two mobs in a v232 client have one, so having none is ordinary."),
                new PortSatelliteSpec("mob-effect", "effect", new[] { "MobEff.img" },
                    "Only a handful of mobs have a row there, so having none is ordinary."),
            },
            Supported: true, UnsupportedReason: null,
            References: new[]
            {
                // The art it borrows. Same family, often a different numbered
                // archive -- Mob.wz reaching into Mob001.wz is ordinary.
                new PortReference("info/link"),
                // The ids a boss summons. Measured on a v232 client: Zakum's
                // info/revive/0 is 8800001, which is its first arm. This is why
                // a boss's parts are followed rather than guessed at from the
                // id's hundred -- that fallback is still there for the bosses
                // that declare nothing, and it is still only a suggestion.
                new PortReference("info/revive/*"),
                // Its attacks. info/skill/N/skill is a mob skill id, which lives
                // in Skill.wz/MobSkill.img -- a different archive entirely, which
                // is exactly the kind of edge nobody remembers to copy by hand.
                new PortReference("info/skill/*/skill", Role: "skill", Image: "MobSkill.img"),
            }),

        new("npc", "NPC", "NPCs",
            ArchivePrefixes: new[] { "Npc" },
            UsesContainers: false,
            Satellites: new[]
            {
                new PortSatelliteSpec("string", "string", new[] { "Npc.img" },
                    "Without this the NPC shows in game as its id."),
                // Kept although the v232 client measured here has no
                // Sound.wz/Npc.img at all: other builds do, and a satellite that
                // finds nothing now says which of the two it is -- no such image
                // in this client, or no row for this id. Dropping it would make
                // an NPC that does have a voice silently lose it.
                new PortSatelliteSpec("sound", "sound", new[] { "Npc.img" }, null),
            },
            Supported: true, UnsupportedReason: null,
            References: new[]
            {
                // The same edge a mob has, and for the same reason: a v232
                // Npc.wz/1104103.img (Eckhart) is nothing but info/script and
                // info/link = 1101006. Ported on its own it is an NPC with no
                // sprite, no idle animation and no speech bubble frames.
                new PortReference("info/link"),
            }),

        // Two archives and two shapes, through one code path. A consumable is
        // Item.wz/Consume/0200.img/02000000 -- a property inside a shared image
        // -- and an equip is Character.wz/Cap/01002357.img, which is an image.
        // Nothing below branches on which: an entry is a node whose name is an
        // id, and where it lands in the target is a mirror of where it sat in the
        // source.
        //
        // The String images are listed rather than derived from the id for the
        // reason PortSatelliteSpec gives: a name written to the wrong category is
        // invisible in game and very hard to trace back, and the source client
        // has already made that decision correctly.
        new("item", "Item", "Items",
            ArchivePrefixes: new[] { "Item", "Character" },
            UsesContainers: true,
            Satellites: new[]
            {
                new PortSatelliteSpec("string", "string",
                    new[] { "Eqp.img", "Consume.img", "Ins.img", "Etc.img", "Cash.img", "Pet.img" },
                    "Without this the item has no name or description in game."),
                new PortSatelliteSpec("sound", "sound", new[] { "Item.img", "Eqp.img" }, null,
                    EveryImage: true),
                // Set membership is a real cross-id dependency, not an item-id
                // satellite. The equip points at a small set id through
                // info/setItemID; that id owns the member list, tooltip panel and
                // bonuses in Etc.wz/SetItemInfo.img. Required means the item is
                // not allowed to land without the row it names.
                new PortSatelliteSpec("set-info", "etc", new[] { "SetItemInfo.img" },
                    "An item only needs this when its info/setItemID names a set.",
                    KeyPath: "info/setItemID", RequiredWhenReferenced: true),
                // The optional visual paired with the same set id. Unlike
                // SetItemInfo, not every set has one, so an absent row is normal;
                // when a row exists the atomic-dependency rule above makes it
                // travel with every selected member that names the set.
                new PortSatelliteSpec("set-effect", "effect", new[] { "SetEff.img" },
                    "Only sets with a visual set effect have a row there, so having none is ordinary.",
                    KeyPath: "info/setItemID"),
                // A cash item that exists and is not listed here is an item
                // nobody can buy. Verified on a v232 client: Commodity.img holds
                // 9,697 rows named "0".."9696", each carrying SN, ItemId, Price
                // and the rest — so the row is found by its ItemId, not by name,
                // and SN is what the client actually purchases by.
                new PortSatelliteSpec("shop", "etc", new[] { "Commodity.img" },
                    "Only cash items are listed there, so an ordinary item having no row is normal.",
                    MatchField: "ItemId",
                    UniqueFields: new[] { "SN" }),
                // The three Effect.wz tables the census found and the role could
                // not reach until now. Measured on a v232 client: ItemEff.img
                // 1,307 rows / 95.3% item ids, PetEff.img 422 / 100%,
                // CharacterEff.img 72 / 100%. Kept as three specs rather than one
                // for the reason the String row gives the other way round: these
                // are not alternatives, an item can legitimately have a row in
                // more than one, so each is asked for on its own.
                new PortSatelliteSpec("item-effect", "effect", new[] { "ItemEff.img" },
                    "Only items with a use effect have a row there, so having none is ordinary."),
                new PortSatelliteSpec("pet-effect", "effect", new[] { "PetEff.img" },
                    "Only pets have a row there, so an ordinary item having none is normal."),
                new PortSatelliteSpec("character-effect", "effect", new[] { "CharacterEff.img" },
                    "Only items that draw an effect on the character have a row there, so having "
                    + "none is ordinary."),
            },
            Supported: true, UnsupportedReason: null,
            // Six, and measured rather than reasoned about. Every all-digit
            // image in both of item's archives was classified exhaustively on a
            // v232 client -- 46,674 of them, no sampling -- and the two halves
            // of the answer do not overlap:
            //
            //   containers  Item.wz  4 digits: 175 (Cash/Consume/Etc/Install/Special)
            //                        5 digits: 8   (Install/03010..03018)
            //                        6 digits: 10  (Install/030150..030159)
            //   entries     Item.wz      7: 1,107 (Pet)
            //               Character.wz 7: 1,280 (Familiar), 8: 44,094 (every slot)
            //
            // Nothing that is an entry is shorter than seven and nothing that is
            // a container is longer than six, so six is the unique smallest
            // lossless value: at 7 it swallows 2,388 real entries, and at the 4
            // this shipped with it cost 2,281 items that were invisible to the
            // index -- the children of Install's 5- and 6-digit books, which
            // were never opened -- plus 18 phantom "items" that are books:
            // 3010, 3011, 3012, 3013, 3014, 3016, 3017, 3018 and 30150..30159.
            //
            // The one oddity found and deliberately not special-cased:
            // Consume/0234.img parses to zero properties. An empty container is
            // still a container, and it costs nothing either way.
            ContainerNameDigits: 6),

        // Entries sit one fixed level inside the book image
        // (Skill.wz/0100.img/skill/1001000), which is what EntryWrapper is for.
        new("skill", "Skill", "Skills",
            ArchivePrefixes: new[] { "Skill" },
            UsesContainers: true,
            Satellites: new[]
            {
                new PortSatelliteSpec("string", "string", new[] { "Skill.img" },
                    "Without this the skill is nameless and has no description in game."),
                new PortSatelliteSpec("sound", "sound", new[] { "Skill.img", "SkillVoice.img" }, null,
                    EveryImage: true),
            },
            Supported: true, UnsupportedReason: null,
            EntryWrapper: "skill",
            // The id a newer client hands this skill's quickslot to. Measured on
            // a live client's 422.img: 4221017 carries changeSkill/0/skill =
            // 4241011 and quickslot = 1, and four of its neighbours do the same.
            // It is not copied — 4241011 is a fifth-job skill in another book,
            // and dragging it in would be a second port nobody asked for — but a
            // skill whose replacement the target has never heard of is one of the
            // shapes behind "it came across and does nothing".
            Requirements: new[]
            {
                new PortRequirement("entry", "changeSkill/*/skill", "skill", "is replaced in the quickslot by skill"),
            },
            // Six, and this was five until the same census that corrected item
            // found the identical fault one archive over.
            //
            // Skill.wz's own books do run 000.img to 16210.img — three to five
            // digits — which is where five came from, and it is right about that
            // archive and wrong about the family. Skill001.wz holds 44 books of
            // SIX digits: 800000-800007, 800010-800019, 800022-800031, 800100,
            // 800101 and 800110-800123, the Monster Life and familiar books,
            // with 3,220 skills between them. At five, those 3,220 skills were
            // invisible to the index and 44 phantom "skills" named 800000-800123
            // entered it — the exact failure this field was added to fix, and it
            // survived in the archive nobody re-measured.
            //
            // Raising it costs nothing, and that is a fact about the client
            // rather than a hope: no image in any Skill archive IS a skill (skill
            // ids are seven to nine digits and every one sits under a book's
            // 'skill' node), so nothing is reclassified out of the index. The
            // 136 Skill001.wz/MobSkill/*.img and 10 Dragon/22xx.img that now
            // read as containers carry no 'skill' child, and EntryWrapper makes
            // Scan skip them — which is what should happen to them anyway.
            ContainerNameDigits: 6,
            MaxDepth: 1,
            InertFlags: new[]
            {
                // The field that ends the "the port copied it perfectly and the
                // client still will not cast it" hunt.
                //
                // Measured on both ends of a real Shadower port. In the target
                // v232 client's own 422.img, every skill carrying 'psd' is a
                // passive — 4220012, 4220015, the 4220043-4220051 hyper block,
                // 4221007, 4221013 — and not one of its attacks does. In the
                // source, 4221017 (Cruel Stab) carries psd = 1 while 4221052 and
                // 4221006 do not; ported together into that client, 4221052 and
                // 4221006 cast and 4221017 sends no packet at all. The copy was
                // faithful to the last node: the source's own data is what says
                // this entry is not cast.
                new PortInertFlag("psd",
                    "carries 'psd = 1', which marks it as passive skill data: the client applies it by "
                    + "itself and never casts it, so pressing it in the skill window sends nothing. That "
                    + "flag is the source's own — the copy is faithful — and a newer build often sets it on "
                    + "a skill it has replaced with another id. Delete 'psd' from the copy afterwards if "
                    + "this one is meant to be pressed."),
            },
            // The reference a skill makes that no skill port can satisfy. See
            // PortAnimation: 'action/0' is the name of a character animation in
            // Character.wz, and the client will not send the use-skill packet
            // for one it cannot find.
            Animations: new[]
            {
                new PortAnimation("action/*", "Character", "0000200", "character animation"),
            }),

        // The kind this whole file was reshaped for, and the one that was refused
        // longest. The old refusal was right about the facts and wrong about the
        // conclusion: a map's scenery IS shared property, and that is an argument
        // for merging it rather than for not carrying it.
        //
        // Every number below was read off real clients rather than assumed.
        // Map002.wz/Map/Map0/000110000.img is 51,482 bytes; the four pictures it
        // names are 26,187,308 between them, five hundred times the map, and of
        // the 87 maps in that one folder 666 object placements name acc1, 523
        // name connect and 442 backgrounds name mapleIsland.
        //
        // The sharing cuts the other way too, which is the finding this row is
        // actually built on: across a real pair of clients, 1,929,186 of the
        // 1,931,467 art references their maps make already resolve in the TARGET,
        // and 17,494 of 17,613 maps need no scenery copied at all. So the
        // mechanism is to check what a map draws against the target and copy
        // nothing; travelling under a distinct name is the escape hatch for the
        // remainder. See PortNamedRef for the design and NamedParts for where the
        // decision is taken.
        //
        // The rest of the chain is declared here rather than left to be
        // discovered in game. 16 of those 87 maps are nothing but an 'info' block
        // naming another map — info/link, the same shape a v232 Npc.wz/1104103.img
        // has — so a link that is not followed is a literally empty room. All 87
        // carry a bgm and a mapMark. The mobs, NPCs, reactors and portal
        // destinations are ids into four other archives; they are counted and
        // checked and deliberately not copied, for the reason the quest kind
        // gives.
        new("map", "Map", "Maps",
            ArchivePrefixes: new[] { "Map" },
            UsesContainers: false,
            Satellites: new[]
            {
                // Two levels down and under one of 73 region folders —
                // String.wz/Map.img/maple/110000 — which is exactly the mirrored
                // lookup that already finds an equip's Eqp/Cap category. The
                // label is 'mapName', not 'name': see PortSatelliteSpec.NameField.
                new PortSatelliteSpec("string", "string", new[] { "Map.img" },
                    "Without this the map is nameless on the world map and in the minimap header.",
                    NameField: "mapName"),
            },
            Supported: true, UnsupportedReason: null,
            References: new[]
            {
                // Not an optimisation, and measured: 003000001.img, 003000003.img,
                // 003000007.img and 003000009.img each hold one node — info —
                // carrying link = "003000000", which is where every foothold,
                // portal, tile and object they draw actually lives. Copied on
                // their own they are a map with no floor.
                new PortReference("info/link"),
            },
            Requirements: new[]
            {
                // One path, two kinds, told apart by a sibling. See
                // PortRequirement.When for why the id alone cannot answer it.
                new PortRequirement("entry", "life/*/id", "mob", "spawns mob", When: ("type", "m")),
                new PortRequirement("entry", "life/*/id", "npc", "places NPC", When: ("type", "n")),
                new PortRequirement("entry", "reactor/*/id", "reactor", "places reactor"),
                new PortRequirement("entry", "portal/*/tm", "map", "has a portal to map", Ignore: 999999999),
                new PortRequirement("entry", "info/returnMap", "map", "returns you to map", Ignore: 999999999),
                new PortRequirement("entry", "info/forcedReturn", "map", "sends you back to map",
                                    Ignore: 999999999),
            },
            // Where a v232 client actually keeps each of these, checked and not
            // guessed: Back is Map001.wz and Map2.wz, Obj and Tile are Map.wz and
            // Map2.wz, the marks are 303 canvases in Map.wz/MapHelper.img/mark,
            // and a bgm is one clip inside one of the 67 Bgm images in Sound002.wz.
            // SetRole is the namespace each name resolves in — the dimension
            // WzReferenceRewriter renames by. bS = "spinOff1" and oS = "spinOff1"
            // are two different sets sharing one name on the real client, and
            // the role on the declaration is what makes collapsing them
            // unwritable.
            Named: new[]
            {
                new PortNamedRef("back/*/bS", "map", "Back", Image: true, Split: false, "background",
                                 Places: "back/*", PlaceName: "bS",
                                 Address: new[] { "ani?ani:back", "no" },
                                 SetRole: WzNamedRole.BackSet),
                new PortNamedRef("*/info/tS", "map", "Tile", Image: true, Split: false, "tile set",
                                 Places: "*/tile/*", PlaceName: "../../info/tS",
                                 Address: new[] { "u", "no" },
                                 SetRole: WzNamedRole.TileSet),
                new PortNamedRef("*/obj/*/oS", "map", "Obj", Image: true, Split: false, "object set",
                                 Places: "*/obj/*", PlaceName: "oS",
                                 Address: new[] { "l0", "l1", "l2" },
                                 SetRole: WzNamedRole.ObjSet),
                new PortNamedRef("info/mapMark", "map", "MapHelper.img/mark",
                                 Image: false, Split: false, "map mark", Nothing: "None",
                                 SetRole: WzNamedRole.MapMark),
                new PortNamedRef("info/bgm", "sound", "", Image: false, Split: true, "background music",
                                 SetRole: WzNamedRole.Bgm),
            },
            // Map002.wz keeps its maps at Map/Map0 … Map/Map9 — 17,442 images
            // across ten folders, of which Map9 alone holds 8,316 — so two
            // directory levels, and a third because nothing says a build cannot
            // add one.
            MaxDepth: 3),

        // One image per id at the archive root. A reactor has no name in
        // String.wz — the limits below say plainly that a reactor nobody has
        // placed on a map is a reactor nobody meets — but it does have sounds,
        // and it does borrow art.
        new("reactor", "Reactor", "Reactors",
            ArchivePrefixes: new[] { "Reactor" },
            UsesContainers: false,
            Satellites: new[]
            {
                // Measured on a v232 client: Sound.wz/Reactor.img is 17.8 MB
                // keyed by reactor id — 100000 is Reactor.wz/0100000.img's five
                // hit and break clips. This satellite was missing, so a ported
                // reactor broke in silence.
                new PortSatelliteSpec("sound", "sound", new[] { "Reactor.img" },
                    "Not every reactor makes a noise, so having no row here is ordinary."),
            },
            Supported: true, UnsupportedReason: null,
            References: new[]
            {
                // v232 Reactor.wz/2001013.img is 109 bytes: info/info, a string
                // 'action', and info/link = 2001002, which is where all of its
                // frames live. Unfollowed, the port produced an invisible reactor.
                new PortReference("info/link"),
            },
            MaxDepth: 1),

        // The only kind with genuinely nothing beside it. A morph's image holds
        // its own frames, its name is the character's buff icon rather than a
        // String.wz row, and a v232 Sound.wz has no Morph image at all — checked,
        // not assumed.
        new("morph", "Morph", "Morphs",
            ArchivePrefixes: new[] { "Morph" },
            UsesContainers: false,
            Satellites: Array.Empty<PortSatelliteSpec>(),
            Supported: true, UnsupportedReason: null,
            MaxDepth: 1),

        // A quest is four places at once -- QuestInfo.img for its description,
        // Say.img for its dialogue, Check.img for its conditions and Act.img for
        // its rewards -- all keyed by the same id inside Quest.wz. That is
        // exactly what a satellite list is, so it is one: the "entry" is the
        // QuestInfo row and the other three ride along.
        new("quest", "Quest", "Quests",
            ArchivePrefixes: new[] { "Quest" },
            UsesContainers: true,
            Satellites: new[]
            {
                new PortSatelliteSpec("quest-say", "quest", new[] { "Say.img" },
                    "The quest will have no dialogue."),
                new PortSatelliteSpec("quest-check", "quest", new[] { "Check.img" },
                    "The quest will have no start or completion conditions."),
                new PortSatelliteSpec("quest-act", "quest", new[] { "Act.img" },
                    "The quest will give no rewards."),
            },
            Supported: true, UnsupportedReason: null,
            // The reference check the old reason said this was waiting on. None
            // of these are copied — an Act.img row hands out four items and a
            // quest chain names eleven other quests, and following that would
            // pull half a client through the door. They are counted, checked
            // against the target where the archive that would hold them is open,
            // and reported per quest, which is what turns "starts and cannot be
            // finished" from a surprise into a line in the result.
            Requirements: new[]
            {
                new PortRequirement("quest-check", "*/npc", "npc", "talks to NPC"),
                new PortRequirement("quest-check", "*/item/*/id", "item", "needs item"),
                new PortRequirement("quest-check", "*/mob/*/id", "mob", "kills mob"),
                new PortRequirement("quest-check", "*/quest/*/id", "quest", "follows quest"),
                new PortRequirement("quest-check", "*/skill/*/id", "skill", "needs skill"),
                new PortRequirement("quest-act", "*/item/*/id", "item", "gives item"),
                new PortRequirement("quest-act", "*/skill/*/id", "skill", "teaches skill"),
                new PortRequirement("quest-act", "*/nextQuest", "quest", "leads to quest"),
            },
            // Quest.wz's other ten images are keyed numerically too — PQuest.img
            // has 1202, QuestDestination.img has 57104, QuestExpByLevel.img has
            // 51200 — so without this the archive's listing order would decide
            // what a quest id means.
            EntryImages: new[] { "QuestInfo.img" },
            MaxDepth: 1),

        // Porting a name on its own. It really is a plain node copy — that was
        // the old reason for refusing it — but the copy is not the hard part.
        // Where it lands is: a v232 String.wz files an equip at
        // Eqp.img/Eqp/Cap/1002357 and a consumable at Consume.img/2000000, and
        // dropping one at the wrong level in the Explorer gives a name the client
        // never reads and nothing on screen to show for it. Mirroring the source's
        // path, creating the categories it needs, and refusing when the target
        // already files that id somewhere else is worth having on its own.
        new("string", "Name", "Names",
            ArchivePrefixes: new[] { "String" },
            UsesContainers: true,
            Satellites: Array.Empty<PortSatelliteSpec>(),
            Supported: true, UnsupportedReason: null,
            // Eqp.img/Eqp/Cap/1002357 is the deepest layout in a v232 client:
            // image, then two category levels, then the id.
            ContainerDepth: 3,
            // Npc.img/2100 is Sera; Map.img/2100 is a map. Sharing one id-keyed
            // index between them would report the two as the same entry.
            IdsAreUniquePerImage: true,
            MaxDepth: 1),
    };

    public static IReadOnlyList<PortKindSpec> Kinds => AllKinds;

    private static PortKindSpec Require(string? kind)
    {
        PortKindSpec? spec = AllKinds.FirstOrDefault(
            k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (spec == null)
        {
            throw new InvalidOperationException(
                $"'{kind}' is not something this can port. Use one of: " +
                string.Join(", ", AllKinds.Select(k => k.Kind)) + ".");
        }
        if (!spec.Supported)
            throw new InvalidOperationException(spec.UnsupportedReason!);
        return spec;
    }

    #endregion

    #region Capabilities and clients

    public PortCapabilitiesDto Capabilities()
    {
        List<PortClientDto> clients = Clients();

        string? reason = null;
        bool available = true;
        if (clients.Count < 2)
        {
            available = false;
            reason = clients.Count == 0
                ? "No archives are open. A port needs two clients open at once — the one you are taking " +
                  "from and the one you are putting it in."
                : "Only one client is open. Open the second client's folder as well; a port moves content " +
                  "between two of them.";
        }
        else if (!clients.Any(c => c.AnyWritable))
        {
            available = false;
            reason = "Every open archive is reference-only, so there is nowhere to port to. " +
                     "Unlock the target client in the Files panel.";
        }

        return new PortCapabilitiesDto
        {
            Available = available,
            Reason = reason,
            MaxSelection = MaxSelection,
            MaxArchiveBytes = MaxArchiveBytes,
            Scopes = new List<string> { "selection", "archive" },
            Clients = clients,
            Kinds = AllKinds.Select(k => new PortKindDto
            {
                Kind = k.Kind,
                Label = k.Label,
                Plural = k.Plural,
                Archives = k.ArchivePrefixes.Select(p => p + ".wz").ToList(),
                Satellites = k.Satellites
                    .SelectMany(s => s.Images.Select(i => $"{Capitalise(s.Role)}.wz/{i}"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                RequiredSatellites = k.Satellites
                    .Where(s => s.RequiredWhenReferenced)
                    .SelectMany(s => s.Images.Select(i => $"{Capitalise(s.Role)}.wz/{i}"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                Supported = k.Supported,
                UnsupportedReason = k.UnsupportedReason,
            }).ToList(),
        };
    }

    private static string Capitalise(string role) => role switch
    {
        "string" => "String",
        "sound" => "Sound",
        "etc" => "Etc",
        // A quest's satellites are in its own archive rather than a shared one,
        // and a capabilities list that said "quest.wz/Say.img" named a file that
        // does not exist on any client.
        "quest" => "Quest",

        // Every other role is a kind name, and a kind name is the archive family
        // in lower case. Falling through unchanged read as "map.wz · Obj/acc1.img"
        // on every scenery row of a map port -- a file name no client has, in a
        // panel whose whole job is to say which file something lands in.
        _ => role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..],
    };

    /// <summary>
    /// The open archives, grouped into clients by folder.
    ///
    /// Folder and not name, for the reason <see cref="StringPoolService.StringArchives"/>
    /// gives at length: two clients open at once have a String.wz each with the
    /// same filename, and something has to decide which one a name comes from.
    /// The pool decided by folder, so this does too — a port that grouped
    /// differently could read one client's names while writing another's, and
    /// nothing on screen would show it.
    /// </summary>
    public List<PortClientDto> Clients()
    {
        lock (_session.Gate)
        {
            List<PortClientDto> clients = new();

            foreach (ClientGroup group in Groups())
            {
                PortClientDto dto = new()
                {
                    Key = group.Key,
                    Label = group.Label,
                    Folder = group.Folder,
                };

                HashSet<int> versions = new();
                foreach (OpenFile file in group.Files)
                {
                    List<string> kinds = KindsOf(file);
                    dto.Archives.Add(new PortArchiveDto
                    {
                        FileId = file.Id,
                        Name = file.Name,
                        Role = RoleOf(file),
                        Kinds = kinds,
                        ReadOnly = file.ReadOnly,
                        Dirty = file.Dirty || file.CountDirtyImages() > 0,
                    });
                    if (!file.ReadOnly)
                        dto.AnyWritable = true;
                    if (file.WzFile != null)
                        versions.Add(file.GameVersion);
                }

                dto.GameVersion = versions.Count == 1 ? versions.First() : 0;
                dto.MixedGameVersions = versions.Count > 1;
                clients.Add(dto);
            }

            return clients.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>One client: every open archive in one folder. Caller holds the gate.</summary>
    internal sealed record ClientGroup(string Key, string Label, string Folder, List<OpenFile> Files);

    private List<ClientGroup> Groups()
    {
        Dictionary<string, List<OpenFile>> byFolder = new(StringComparer.OrdinalIgnoreCase);

        foreach (OpenFile file in _session.Files)
        {
            // A file with no directory part cannot be grouped with anything, and a
            // detached one has no tree to read; both are simply not clients.
            string? folder = Path.GetDirectoryName(file.FilePath);
            if (string.IsNullOrEmpty(folder) || file.Detached)
                continue;

            if (!byFolder.TryGetValue(folder, out List<OpenFile>? files))
                byFolder[folder] = files = new List<OpenFile>();
            files.Add(file);
        }

        return byFolder
            .Select(pair => new ClientGroup(
                pair.Key,
                Path.GetFileName(pair.Key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    is { Length: > 0 } leaf ? leaf : pair.Key,
                pair.Key,
                pair.Value))
            .ToList();
    }

    /// <summary>
    /// The archive in the target client that matches the one this entry came out
    /// of, or the chosen target when there is no match.
    ///
    /// Matched on the family stem, so Mob001.wz answers for Mob.wz -- a client
    /// splits one family across numbered siblings and which sibling a given id
    /// sits in is that client's business, not something to preserve.
    ///
    /// When the family has more than one writable archive, the entry lands
    /// beside the target's own population of this kind, not in whichever archive
    /// happened to be first. That is a measured rule, not a taste: the real v232
    /// client keeps all 17,442 of its maps in Map002.wz, and "first writable of
    /// the family" put a ported map into Map.wz — fragmenting the family and
    /// pushing a 1.6 GB file toward the 4 GB WZ ceiling. Only when no archive of
    /// the family holds any entries of the kind does first-writable remain the
    /// answer, because then there is no population to land beside.
    ///
    /// <paramref name="disclosure"/> names the choice whenever there was one —
    /// more than one candidate — so the plan says where the entry goes and why
    /// rather than leaving it to be discovered in the saved file.
    /// </summary>
    private OpenFile ArchiveFor(
        EntryLocation entry, PortKindSpec spec, ClientGroup? targetClient, OpenFile fallback,
        out string? disclosure)
    {
        disclosure = null;
        if (targetClient == null)
            return fallback;

        string family = WzSessionService.StripArchiveSuffix(entry.File.Name);
        List<OpenFile> candidates = targetClient.Files
            .Where(f => !f.ReadOnly
                     && WzSessionService.StripArchiveSuffix(f.Name)
                            .Equals(family, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return fallback;
        if (candidates.Count == 1)
            return candidates[0];

        // The population question, asked of the same index every conflict check
        // reads, so "where the target keeps this kind" and "does the target
        // already have this id" cannot come apart.
        (OpenFile File, int Count) populated = candidates
            .Select(f => (File: f, Count: Index(f, spec).Count))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        if (populated.Count > 0)
        {
            disclosure =
                $"Lands in {populated.File.Name}: the {family} family has {candidates.Count} writable "
                + $"archives here, and that is the one {targetClient.Label} keeps its "
                + $"{spec.Plural.ToLowerInvariant()} in ({populated.Count:N0} of them).";
            return populated.File;
        }

        OpenFile first = candidates[0];
        disclosure =
            $"Lands in {first.Name}, the first writable archive of the {family} family: no archive of "
            + $"it holds any {spec.Plural.ToLowerInvariant()} yet, so there is no population to land "
            + "beside.";
        return first;
    }

    /// <summary>
    /// What part a given archive can play in a port, or null for none.
    ///
    /// Public because it is the thing a satellite declaration depends on and the
    /// only place the dependency can be checked. A satellite naming a role this
    /// cannot return does not throw and does not warn: it reports <c>Absent</c>
    /// for every entry for ever, with a generated sentence naming an archive
    /// nothing ever looked in. That is the worst failure mode available here, so
    /// the tests assert against this method rather than against a copy of its
    /// rules.
    /// </summary>
    public static string? RoleOf(OpenFile file)
    {
        string stem = WzSessionService.StripArchiveSuffix(file.Name);

        if (stem.Equals("String", StringComparison.OrdinalIgnoreCase))
            return "string";
        if (stem.Equals("Sound", StringComparison.OrdinalIgnoreCase))
            return "sound";
        // Etc.wz is not a kind's home, but it holds Commodity.img, which is what
        // makes a cash item purchasable. It gets a role so a client can be asked
        // for it the same way it is asked for its String.wz.
        if (stem.Equals("Etc", StringComparison.OrdinalIgnoreCase))
            return "etc";
        // Effect.wz, for the same reason and after the same finding. The census
        // measured five real satellites in it -- ItemEff.img (1,307 rows, 95.3%
        // item ids), PetEff.img (422, 100%), CharacterEff.img (72, 100%),
        // ChaseEffect.img (2, 100% mob ids) and the mob rows of MobEff.img (4)
        // -- and not one of them could be declared, because with no arm here
        // WithRole(client, "effect") returns an empty list and every one of them
        // would have reported Absent for every entry for ever.
        //
        // Effect.wz is not a kind and must not become one: its ~7,500 entries
        // are 200 genuinely new ones against a v232 client, its id-keyed images
        // belong to kinds that already exist, and it needs no name-keyed index.
        // A role is exactly the right amount of standing -- the same standing
        // Etc.wz has.
        if (stem.Equals("Effect", StringComparison.OrdinalIgnoreCase))
            return "effect";

        // The first kind that claims it. An archive can serve only one kind, and
        // no two kinds share a prefix.
        return KindsOf(file).FirstOrDefault();
    }

    private static List<string> KindsOf(OpenFile file)
    {
        string stem = WzSessionService.StripArchiveSuffix(file.Name);
        return AllKinds
            .Where(k => k.Supported
                     && k.ArchivePrefixes.Any(p => stem.Equals(p, StringComparison.OrdinalIgnoreCase)))
            .Select(k => k.Kind)
            .ToList();
    }

    private static List<OpenFile> WithRole(ClientGroup client, string role) =>
        client.Files.Where(f => RoleOf(f) == role).ToList();

    private static List<OpenFile> ArchivesFor(ClientGroup client, PortKindSpec spec) =>
        client.Files.Where(f => KindsOf(f).Contains(spec.Kind)).ToList();

    #endregion

    #region Plan and apply

    /// <summary>
    /// One art image the entries being ported reach through a <c>_Canvas</c>
    /// directory, and how much of the port depends on it.
    ///
    /// The unit the refusal is computed in. An art image either survives the trip
    /// or it does not, and every link naming it shares that fate — so the question
    /// is asked once per image and the damage is counted per link.
    /// </summary>
    private sealed class SplitArtUse
    {
        public SplitArtUse(string image, string example)
        {
            Image = image;
            Example = example;
        }

        /// <summary>"Skill/_Canvas/40004.img".</summary>
        public string Image { get; }

        /// <summary>One whole link, so a reader can see the shape rather than take it on trust.</summary>
        public string Example { get; }

        public int Links { get; set; }

        /// <summary>A few of the entries that need it, for naming rather than counting.</summary>
        public SortedSet<string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public PortPlanDto Plan(PortPlanRequest request) => BuildPlan(request, apply: false).Plan;

    /// <summary>One pass of the planner, and what it did if it was allowed to write.</summary>
    private sealed record PlanRun(PortPlanDto Plan, int Written, int Failed, double Seconds, bool NeedsDecision);

    public PortResultDto Apply(PortApplyRequest request)
    {
        if (!request.Confirmed)
        {
            throw new InvalidOperationException(
                "A port is previewed before it is written. Ask for the plan first, then send it back " +
                "with confirmed set.");
        }

        // The plan is rebuilt here rather than taken from the caller, and that is
        // what makes the preview honest: the client cannot hand back an edited
        // plan, and there is no second code path that could compute a different
        // answer. What keeps a *stale* preview safe is not a generation number —
        // it is that a part which has become a Conflict since the preview will
        // not be written unless Overwrite was asked for, and Overwrite is never
        // inferred.
        PlanRun run = BuildPlan(request, apply: true);
        PortPlanDto plan = run.Plan;
        int written = run.Written;

        if (written > 0)
        {
            // Unconditionally: the pool caches id -> name, and a ported String.wz
            // entry the pool has not re-read shows the target's old name (or none)
            // everywhere in the app, which reads exactly like a write that
            // silently did nothing.
            _strings.Invalidate();
        }

        PortResultDto result = new()
        {
            Plan = plan,
            NeedsDecision = run.NeedsDecision,
            Written = written,
            Failed = run.Failed,
            Seconds = run.Seconds,
            Skipped = plan.Totals.Parts - plan.Totals.WillWrite,
            UndoLabel = written > 0 ? _undo.Peek().NextUndo : null,
        };

        RecordInLedger(request, result);
        return result;
    }

    /// <summary>
    /// Appends this apply to the composition ledger beside the target client —
    /// the same record a build take writes, produced by the same
    /// <see cref="CompositionLedgerStore.Record"/>, so an interactively composed
    /// client is explicable the way a built one is: what was taken, from which
    /// source (pinned by content hash where the file on disk is what was read),
    /// where it landed, what it renamed, what it refused.
    ///
    /// Skipped when the port stopped to ask about conflicts — nothing happened —
    /// and when the target's folder does not exist on disk, because a ledger
    /// lives beside a client and a session of in-memory archives has no beside.
    /// A failure to record is reported on the result rather than thrown: the
    /// port has already happened, and un-happening it is not this method's call.
    /// </summary>
    private void RecordInLedger(PortApplyRequest request, PortResultDto result)
    {
        if (result.NeedsDecision)
            return;

        try
        {
            string? targetFolder, sourceFolder = null;
            string targetName;
            List<(string Name, string Path, bool Dirty)> sourceArchives = new();
            List<string> requestedRelative;

            CompositionLedgerStore store = new(_session);

            lock (_session.Gate)
            {
                OpenFile? target = _session.TryGetFile(request.TargetFileId);
                if (target == null)
                    return;
                targetFolder = Path.GetDirectoryName(target.FilePath);
                targetName = target.Name;

                // The source client, read off what the request named rather
                // than re-planned: the plan already refused a selection that
                // spans clients, so the first named archive's folder is the
                // client's folder.
                string? sourceFileId = string.Equals(request.Scope, "archive", StringComparison.OrdinalIgnoreCase)
                    ? request.SourceFileId
                    : request.Paths?.Select(WzPath.FileId).FirstOrDefault(id => id.Length > 0);
                OpenFile? sourceFile = sourceFileId == null ? null : _session.TryGetFile(sourceFileId);
                if (sourceFile != null)
                {
                    sourceFolder = Path.GetDirectoryName(sourceFile.FilePath);
                    if (sourceFolder != null)
                    {
                        // Every archive of the source client the plan read a part
                        // out of, for pinning. The part's SourceArchive is the
                        // family stem ("Map", "String"), so match on stems.
                        HashSet<string> families = new(
                            result.Plan.Items
                                .SelectMany(i => i.Parts)
                                .Select(p => p.SourceArchive)
                                .Where(a => !string.IsNullOrEmpty(a))
                                .Select(a => a!),
                            StringComparer.OrdinalIgnoreCase);

                        foreach (OpenFile file in _session.Files)
                        {
                            if (!string.Equals(
                                    Path.GetDirectoryName(file.FilePath), sourceFolder,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            if (families.Contains(WzSessionService.StripArchiveSuffix(file.Name)))
                                sourceArchives.Add((file.Name, file.FilePath, file.Dirty));
                        }
                    }
                }

                requestedRelative = (request.Paths ?? new List<string>())
                    .Select(p => store.Relative(p))
                    .Where(p => p != null)
                    .Select(p => p!)
                    .ToList();
                if (string.Equals(request.Scope, "archive", StringComparison.OrdinalIgnoreCase)
                    && sourceFile != null)
                {
                    requestedRelative.Add(sourceFile.Name);
                }
            }

            // A ledger sits beside a client folder; no folder on disk, nowhere
            // to keep the record. That is the in-memory-fixture case, not a real
            // port, and it is logged rather than warned because there is no
            // client whose history is losing anything.
            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            {
                _log.LogDebug(
                    "Port not recorded in a composition ledger: the target archive's folder does not exist.");
                return;
            }

            string sourceLeaf = sourceFolder is { Length: > 0 }
                ? Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : result.Plan.SourceClient;
            if (string.IsNullOrEmpty(sourceLeaf))
                sourceLeaf = result.Plan.SourceClient is { Length: > 0 } label ? label : "unknown";

            CompositionSource source = new()
            {
                Id = sourceLeaf,
                Label = result.Plan.SourceClient is { Length: > 0 } client ? client : sourceLeaf,
                Folder = sourceFolder ?? "",
            };
            foreach ((string name, string path, bool dirty) in sourceArchives
                         .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Pinned by the bytes on disk only when the bytes on disk are
                // what was read: a dirty archive's tree has departed from its
                // file, and a hash of the file would vouch for content this port
                // did not see. Null says "unpinned" — the ledger schema keeps
                // that apart from "hashed to nothing".
                string? sha = null;
                if (!dirty && File.Exists(path))
                    sha = HashFileCached(path);
                source.Archives.Add(new CompositionArchive(name, sha));
            }

            CompositionTake take = new()
            {
                From = source.Id,
                Kind = result.Plan.Kind,
                Scope = result.Plan.Scope,
                Into = targetName,
                Take = requestedRelative,
                FromArchive = string.Equals(request.Scope, "archive", StringComparison.OrdinalIgnoreCase)
                    ? result.Plan.SourceArchive
                    : null,
                Options = new CompositionOptions
                {
                    FollowLinks = request.FollowLinks,
                    IncludeArtOutlinks = request.IncludeArtOutlinks,
                    Overwrite = request.Overwrite,
                    Match = request.Match,
                    AcceptDeadCanvasLinks = request.AcceptDeadCanvasLinks,
                    AcceptMissingNames = request.AcceptMissingNames,
                },
                Note = "Interactive port from the MapleBench port dialog.",
            };
            foreach ((string name, string _, bool dirty) in sourceArchives)
            {
                if (dirty)
                {
                    take.Note += $" {name} had unsaved edits when this port read it, so its "
                               + "hash is not pinned: the file on disk is not what was read.";
                }
            }

            LedgerTake record;
            lock (_session.Gate)
                record = store.Record(source, take, result);
            CompositionLedgerStore.Append(targetFolder, record);
        }
        catch (Exception ex)
        {
            // The apply already happened; a record that failed to write must be
            // a loud fact on the result, never a crash that makes the port look
            // like it failed.
            result.Plan.Warnings.Add(
                "This port applied, but it could not be appended to the composition ledger beside the "
                + "target client: " + ex.Message + ". The client's history is now missing this port.");
            _log.LogWarning(ex, "Appending an interactive port to the composition ledger failed.");
        }
    }

    /// <summary>
    /// <see cref="CompositionLedgerStore.HashFile"/> behind a (path, size,
    /// mtime) memo, because an interactive port must not re-read a 1.4 GB
    /// archive every time somebody presses the button.
    /// </summary>
    private static string HashFileCached(string path)
    {
        FileInfo info = new(path);
        (long Length, DateTime Mtime) stamp = (info.Length, info.LastWriteTimeUtc);

        lock (FileHashes)
        {
            if (FileHashes.TryGetValue(path, out (long Length, DateTime Mtime, string Hash) known)
                && known.Length == stamp.Length && known.Mtime == stamp.Mtime)
            {
                return known.Hash;
            }
        }

        string hash = CompositionLedgerStore.HashFile(path);
        lock (FileHashes)
        {
            if (FileHashes.Count > 64)
                FileHashes.Clear();
            FileHashes[path] = (stamp.Length, stamp.Mtime, hash);
        }
        return hash;
    }

    private static readonly Dictionary<string, (long Length, DateTime Mtime, string Hash)> FileHashes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the plan, and — when <paramref name="apply"/> — writes the parts it
    /// says it will write.
    ///
    /// One method for both because the two must not be able to disagree about
    /// what happens. <see cref="MobService.Bulk"/> and
    /// <see cref="StringEditService"/>'s Apply take the same line, and it is the
    /// same reason: a preview computed by different code from the write is a
    /// preview of something else.
    /// </summary>
    private PlanRun BuildPlan(PortPlanRequest request, bool apply)
    {
        int written = 0;
        int failed = 0;
        bool needsDecision = false;
        PortKindSpec spec = Require(request.Kind);
        bool wholeArchive = string.Equals(request.Scope, "archive", StringComparison.OrdinalIgnoreCase);

        // "Everything under these folders" -- Character.wz/Cap, or Cap and Ring
        // together, which is how people think about equips. Several folders in
        // one run on purpose: two runs would mean two previews, two overwrite
        // decisions and two undo entries for what the user did as one action.
        //
        // It shares every downstream rule with the whole-archive scope, because
        // the thing that makes a scope "bulk" is the number of entries, not
        // where they came from: Cap alone is 3,331 images against a selection
        // limit of 60.
        bool folders = string.Equals(request.Scope, "folder", StringComparison.OrdinalIgnoreCase);
        bool bulk = wholeArchive || folders;
        Stopwatch clock = Stopwatch.StartNew();

        lock (_session.Gate)
        {
            PortPlanDto plan = new()
            {
                Kind = spec.Kind,
                Label = spec.Label,
                Scope = wholeArchive ? "archive" : folders ? "folder" : "selection",
                Generation = _session.StructureGeneration,
            };

            List<ClientGroup> groups = Groups();

            /* ---------------- the target ---------------- */

            if (string.IsNullOrWhiteSpace(request.TargetFileId))
                throw new InvalidOperationException("No target archive was chosen.");

            OpenFile target;
            try { target = _session.GetFile(request.TargetFileId); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException(ex.Message); }

            ClientGroup? targetClient = groups.FirstOrDefault(g => g.Files.Any(f => f.Id == target.Id));
            plan.TargetFileId = target.Id;
            plan.TargetArchive = target.Name;
            plan.TargetClient = targetClient?.Label ?? target.Name;
            plan.Limits.AddRange(Limits(spec, bulk));

            if (!KindsOf(target).Contains(spec.Kind))
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"'{target.Name}' does not hold {spec.Plural.ToLowerInvariant()}. Choose one of the " +
                    $"target client's {string.Join(" or ", spec.ArchivePrefixes.Select(p => p + ".wz"))}.";
                return Done(plan, clock, written, failed, needsDecision);
            }
            if (target.ReadOnly)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"'{target.Name}' is open for reference only, so nothing can be written to it. " +
                    "Unlock it in the Files panel if you meant to port into it.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            /* ---------------- the source ---------------- */

            List<EntryLocation> requested = new();
            OpenFile? sourceArchive = null;

            if (wholeArchive)
            {
                if (string.IsNullOrWhiteSpace(request.SourceFileId))
                    throw new InvalidOperationException("No source archive was chosen.");
                try { sourceArchive = _session.GetFile(request.SourceFileId); }
                catch (KeyNotFoundException ex) { throw new InvalidOperationException(ex.Message); }

                if (!KindsOf(sourceArchive).Contains(spec.Kind))
                {
                    plan.Blocked = true;
                    plan.BlockedReason =
                        $"'{sourceArchive.Name}' does not hold {spec.Plural.ToLowerInvariant()}.";
                    return Done(plan, clock, written, failed, needsDecision);
                }
                requested.AddRange(Index(sourceArchive, spec).All.OrderBy(e => e.Id));
            }
            else if (folders)
            {
                List<string> roots = (request.Paths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (roots.Count == 0)
                    throw new InvalidOperationException("No folders were chosen to port.");

                // The folders may span archives -- Cap and Ring are both in
                // Character.wz, but nothing says they have to be -- so the index
                // is taken per distinct archive rather than assuming one source.
                foreach (string fileId in roots.Select(WzPath.FileId).Distinct(StringComparer.Ordinal))
                {
                    OpenFile file;
                    try { file = _session.GetFile(fileId); }
                    catch (KeyNotFoundException ex) { throw new InvalidOperationException(ex.Message); }

                    if (!KindsOf(file).Contains(spec.Kind))
                    {
                        plan.Blocked = true;
                        plan.BlockedReason =
                            $"'{file.Name}' does not hold {spec.Plural.ToLowerInvariant()}.";
                        return Done(plan, clock, written, failed, needsDecision);
                    }

                    sourceArchive ??= file;
                    foreach (EntryLocation entry in Index(file, spec).All)
                    {
                        // Prefix match on a path boundary, never a bare
                        // StartsWith: "f1/Cap" would otherwise swallow
                        // "f1/Cape", which is a different equip slot entirely.
                        if (roots.Any(root => entry.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                            requested.Add(entry);
                    }
                }

                if (requested.Count == 0)
                {
                    plan.Blocked = true;
                    plan.BlockedReason = roots.Count == 1
                        ? $"Nothing under '{roots[0]}' is a {spec.Label.ToLowerInvariant()} this can port."
                        : $"Nothing under those {roots.Count} folders is a {spec.Label.ToLowerInvariant()} this can port.";
                    return Done(plan, clock, written, failed, needsDecision);
                }

                requested = requested
                    .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(e => e.Id)
                    .ToList();
            }
            else
            {
                List<string> paths = (request.Paths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (paths.Count == 0)
                    throw new InvalidOperationException("Nothing was selected to port.");
                if (paths.Count > MaxSelection)
                {
                    throw new InvalidOperationException(
                        $"{paths.Count} {spec.Plural.ToLowerInvariant()} is more than one selection port " +
                        $"carries. The limit is {MaxSelection} — a port holds a full copy of every node in " +
                        "memory until you save. Do it in batches, or use the whole-archive scope.");
                }

                foreach (string path in paths)
                {
                    EntryLocation? found = Locate(path, spec);
                    if (found == null)
                    {
                        plan.Items.Add(new PortItemDto
                        {
                            SourcePath = path,
                            Requested = true,
                            Notes = { $"'{path}' is not a {spec.Label.ToLowerInvariant()} in an open archive " +
                                      "any more, or its name is not an id." },
                        });
                        continue;
                    }
                    requested.Add(found);
                }
            }

            HashSet<string> sourceFiles = requested.Select(e => e.File.Id).ToHashSet(StringComparer.Ordinal);
            List<ClientGroup> sourceGroups = groups
                .Where(g => g.Files.Any(f => sourceFiles.Contains(f.Id)))
                .ToList();

            if (sourceGroups.Count == 0)
            {
                plan.Blocked = true;
                plan.BlockedReason = "The selected archives are no longer open.";
                return Done(plan, clock, written, failed, needsDecision);
            }
            if (sourceGroups.Count > 1)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"The selection spans {sourceGroups.Count} clients (" +
                    string.Join(", ", sourceGroups.Select(g => g.Label)) +
                    "). Port from one at a time — otherwise which client's String.wz supplies the names is " +
                    "decided by whichever archive happens to be first, which is not a decision to make by " +
                    "accident.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            ClientGroup source = sourceGroups[0];
            plan.SourceClient = source.Label;
            plan.SourceArchive = sourceArchive?.Name
                ?? string.Join(", ", requested.Select(e => e.File.Name).Distinct(StringComparer.Ordinal));

            if (targetClient != null && string.Equals(source.Key, targetClient.Key, StringComparison.OrdinalIgnoreCase))
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"The source and the target are the same client ({source.Label}). A port moves content " +
                    "between two clients; to copy inside one, use Duplicate in the Explorer.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            // Completeness starts on the source side. A closed satellite archive
            // is indistinguishable from "this entry has no row there": item
            // effects, sounds and Commodity rows are indexed externally, so the
            // entry itself cannot prove their absence. Require every family this
            // kind can use to be readable before collecting anything. The plan
            // will still copy only rows that actually belong to the selection.
            List<string> unopenedSourceFamilies = spec.Satellites
                .Select(satellite => satellite.Role)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(role => WithRole(source, role).Count == 0)
                .Select(role => Capitalise(role) + ".wz")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unopenedSourceFamilies.Count > 0)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"Open {string.Join(", ", unopenedSourceFamilies)} from {source.Label} before importing "
                    + $"these {spec.Plural.ToLowerInvariant()}. MapleBench must inspect every archive this "
                    + "kind can depend on to distinguish 'no dependency exists' from 'the dependency archive "
                    + "was never opened'. Nothing was written; after they are open, only rows actually used "
                    + "by the selection will be carried.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            /* ---------------- collect, links included ---------------- */

            EntryIndex targetIndex = Index(target, spec);
            EntryIndex? sourceIndex = null;

            // Built only when something needs it: link following and the boss-part
            // suggestions. For a whole-archive port neither applies, and for an
            // item there are no reference edges at all, so the walk is never paid
            // for.
            EntryIndex SourceIndex()
            {
                if (sourceIndex != null)
                    return sourceIndex;
                sourceIndex = new EntryIndex();
                foreach (OpenFile file in ArchivesFor(source, spec))
                    sourceIndex.TryAddAll(Index(file, spec));
                return sourceIndex;
            }

            List<PortItemDto> items = plan.Items;

            // Keyed by id for entries of the kind being ported and by path for
            // everything the closure drags in, because a dependency is not an
            // entry: a Mob/_Canvas image has no id, and keying it as 0 would make
            // every one of them the same node.
            Dictionary<string, PortItemDto> seen = new(StringComparer.OrdinalIgnoreCase);
            static string Key(EntryLocation e) => e.Id > 0 ? "id:" + e.Id : "at:" + e.Path;

            // The same image is reachable two ways: as an entry (by id) and as
            // something another entry's canvas outlinks to (by path). Without
            // this, porting Zakum listed 8800000.img twice -- once as the mob and
            // once as "art 8800001 draws from" -- and the second row would have
            // tried to copy it again.
            Dictionary<string, PortItemDto> byPath = new(StringComparer.OrdinalIgnoreCase);

            // Keyed on the resolved path rather than on the link text: the same
            // image is reached by several spellings ("Mob/8800141.img" and
            // "8800141.img" from inside Mob.wz), and WZ links can point back at
            // each other, so a text-keyed visit set both re-copies and loops.
            Dictionary<string, EntryLocation> dependencies = new(StringComparer.OrdinalIgnoreCase);
            bool overflowed = false;

            // Every art image these entries name through the split form, and how
            // hard they lean on it.
            //
            // Gathered while the entries are being walked for links anyway, so it
            // costs nothing, and gathered at PLAN time on purpose: this is what
            // the refusal below is computed from, and a refusal that could only be
            // computed on the way past the point of no return is not one.
            Dictionary<string, SplitArtUse> splitArt = new(StringComparer.OrdinalIgnoreCase);

            // Containers are shared: 583 consumables land in one 0200.img, and the
            // plan must say "creates 0200.img" once rather than 583 times.
            Dictionary<string, PortPartDto> containers = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<PortPartDto, PathStep> containerSteps = new();
            Dictionary<PortPartDto, string> renames = new();

            // Satellite rows this plan has already decided on, keyed by where
            // they land. Two jobs, both of them the same defect twice:
            //
            //   * A cash-shop row is APPENDED under a name nothing else uses, and
            //     that name was recomputed from the unmodified image on every
            //     call. Three pets planned together were all handed row 9697,
            //     DistinctParts deduplicated them on target path, and two of the
            //     three vanished while the totals said one row and each part
            //     rendered "New / will write".
            //   * A sound row that is a link at a sibling has to bring the
            //     sibling, and two skills sharing one sibling must produce one
            //     part, the way a shared container does.
            Dictionary<string, PortPartDto> satelliteRows = new(StringComparer.OrdinalIgnoreCase);

            // Parts an entry cannot safely land without. The first user-visible
            // case is an equip's SetItemInfo row: if it is missing or different,
            // the equip either has no set panel or joins whatever unrelated set
            // the target already assigned that number. Kept per item because one
            // set row is deliberately shared by every selected piece in the set.
            Dictionary<PortItemDto, List<PortPartDto>> requiredByItem =
                new(ReferenceEqualityComparer.Instance);

            // The scenery, music and marks these entries name, decided once each
            // and shared by everything that names them. See NamedParts: 666 of an
            // 87-map folder's object placements name one image, and the plan must
            // say "brings acc1" once rather than 666 times.
            Dictionary<string, PortPartDto> named = new(StringComparer.OrdinalIgnoreCase);

            // What the copies will have to say instead, where a name had to change
            // to avoid replacing something the target already had. Applied to the
            // TARGET's copies after the write; see RewriteNamed.
            Dictionary<(string Path, string Was), string> rewrites = new();

            // The images the copies reach by absolute '_outlink', decided once
            // each: left alone when the target serves the linked frames
            // identically, carried along when it cannot — and the copies' link
            // texts owed to the landed parts, materialised after the write.
            LinkCarryContext linkCarry = new();


            // Names neither client has. Collected rather than reported per entry
            // because between them they are a refusal.
            List<string> unsatisfied = new();

            // Every piece each named set is asked for, across the whole plan. A
            // set is decided once and shared, and two maps ask different things
            // of the same set -- so the decision has to be revisited when a later
            // one wants a piece the first never mentioned.
            Dictionary<string, SortedSet<string>> namedUses =
                new(StringComparer.OrdinalIgnoreCase);

            // Captured out here because Collect's own `requested` parameter is a
            // bool and shadows this list inside it.
            int requestedCount = requested.Count;

            // A kind whose entries name things by name cannot be ported in bulk,
            // and this is where that is said rather than discovered.
            //
            // Everything below MaxParsedEntries is followed exactly as it is for a
            // hand-picked selection; above it nothing is parsed at all, and for a
            // map that means its scenery, its music and its mark are never looked
            // at. The copy would land, preview clean, and be an empty room. A
            // whole v232 Map002.wz is 17,442 maps and about 280 MB, comfortably
            // inside the byte cap, so nothing else here would have stopped it.
            if (spec.Named != null && requestedCount > MaxParsedEntries)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"{requestedCount:N0} {spec.Plural.ToLowerInvariant()} is more than this can carry at "
                    + $"once. Past {MaxParsedEntries} entries nothing is parsed to see what it needs, and a "
                    + $"{spec.Label.ToLowerInvariant()} keeps almost everything it draws OUTSIDE itself — "
                    + "its scenery, its music and its mark are all names into images shared with hundreds of "
                    + "others. Carried without looking, the copies land, preview clean and draw nothing. "
                    + "Port the ones you want with the selection scope instead.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            foreach (EntryLocation entry in requested)
                Collect(entry, requested: true, pulledInBy: null, depth: 0);

            // What a pulled-in row says about itself. The edge it arrived on is
            // the useful part: "the art for 8800000" and "a part 8800000 summons"
            // are different facts, and a user checking the result needs to know
            // which.
            static string Because(PortReference edge, int from) => edge.Path switch
            {
                "info/link" => $"the art for {from}",
                "info/revive/*" => $"a part {from} summons",
                "info/skill/*/skill" => $"a skill {from} attacks with",
                _ => $"{edge.Path} of {from}",
            };

            void Collect(EntryLocation entry, bool requested, string? pulledInBy, int depth)
            {
                if (byPath.TryGetValue(entry.Path, out PortItemDto? sameNode))
                {
                    if (requested && !sameNode.Requested)
                    {
                        sameNode.Requested = true;
                        sameNode.PulledInBy = null;
                    }
                    return;
                }

                if (seen.TryGetValue(Key(entry), out PortItemDto? already))
                {
                    // Requested wins over pulled-in: the row should say the user
                    // asked for it, not that a link happened to reach it first.
                    if (requested && !already.Requested)
                    {
                        already.Requested = true;
                        already.PulledInBy = null;
                    }
                    return;
                }

                PortItemDto item = new()
                {
                    Id = entry.Id,
                    SourcePath = entry.Path,
                    Requested = requested,
                    PulledInBy = pulledInBy,
                    Name = entry.Id > 0 ? NameInSource(source, spec, entry) : entry.Relative,
                };
                seen[Key(entry)] = item;
                byPath[entry.Path] = item;
                items.Add(item);

                // Which archive of the target client this belongs in, decided by
                // the one it came out of rather than by the one the user picked.
                //
                // A kind can span archives: an item is Item.wz and an equip is
                // Character.wz, and the client tells them apart by id range. With
                // a single chosen target, porting a Cap alongside a cash item
                // sent Character.wz/Cap/01002357.img into Item.wz/Cap/ -- a path
                // no client will ever read, reported as a clean New. Mirroring
                // the source's archive family is the same rule that already
                // decides the directories below it.
                OpenFile home = ArchiveFor(entry, spec, targetClient, target, out string? archiveChoice);
                EntryIndex homeIndex =
                    ReferenceEquals(home, target) ? targetIndex : Index(home, spec);

                /* --- the containers it has to sit inside --- */
                foreach (PortPartDto container in ContainerParts(entry, home, containers, containerSteps))
                    item.Parts.Add(container);

                /* --- the entry itself --- */
                PortPartDto entryPart = EntryPart(spec, entry, home, homeIndex, requested);
                // The archive choice, on the part it decides. A family with more
                // than one writable archive means the plan took a decision the
                // user did not, and a decision taken silently is how a map ends
                // up growing the wrong 1.6 GB file.
                if (archiveChoice != null)
                    entryPart.Reason = With(archiveChoice, entryPart.Reason);
                item.Parts.Add(entryPart);

                /* --- everything that has to travel with it --- */
                // Only for real entries. A canvas image the closure pulled in has
                // no name in String.wz and no sound of its own, and asking for
                // them would fill the report with "not in the source" rows that
                // are not findings.
                if (entry.Id > 0)
                {
                    foreach (PortSatelliteSpec satellite in spec.Satellites)
                    {
                        IReadOnlyList<int> satelliteIds = SatelliteKeys(entry, satellite);
                        if (satelliteIds.Count == 0)
                            continue;

                        // EveryImage decides whether the images are alternatives
                        // or additions, and both are real. An item's name is in
                        // exactly one of Eqp/Consume/Ins/Etc/Cash/Pet, so probing
                        // all six as separate parts would put five "not in the
                        // source" rows on every row. A v232 mob, on the other
                        // hand, keeps its hit sounds in Sound.wz/Mob.img AND its
                        // boss voice lines in MobVoice.img, and taking only the
                        // first hit gives a silent boss.
                        foreach (int satelliteId in satelliteIds)
                        {
                            if (!satellite.EveryImage)
                            {
                                List<PortPartDto> parts = SatelliteParts(
                                    satellite, source, targetClient, satelliteId,
                                    renames, containers, containerSteps, satelliteRows);
                                item.Parts.AddRange(parts);
                                RememberRequired(item, satellite, parts);
                                continue;
                            }

                            foreach (string image in satellite.Images)
                            {
                                PortSatelliteSpec oneImage = satellite with { Images = new[] { image } };
                                List<PortPartDto> parts = SatelliteParts(
                                    oneImage, source, targetClient, satelliteId,
                                    renames, containers, containerSteps, satelliteRows);
                                item.Parts.AddRange(parts);
                                RememberRequired(item, oneImage, parts);
                            }
                        }
                    }
                }

                void RememberRequired(
                    PortItemDto owner, PortSatelliteSpec satellite, IEnumerable<PortPartDto> parts)
                {
                    foreach (PortPartDto required in parts.Where(p => p.Kind == satellite.Kind))
                    {
                        // A discovered satellite row is part of the selected
                        // entry's atomic import: name, sound, shop data, item
                        // effect or set definition. This includes helper rows a
                        // sound link points at, not only its first row.
                        //
                        // A derived reference such as setItemID remains required
                        // even when its source row is absent, because that absence
                        // is itself a dangling dependency in the source.
                        bool discovered = !string.IsNullOrWhiteSpace(required.SourcePath);
                        if (!discovered && !satellite.RequiredWhenReferenced)
                            continue;

                        if (!requiredByItem.TryGetValue(owner, out List<PortPartDto>? list))
                            requiredByItem[owner] = list = new List<PortPartDto>();
                        if (!list.Any(part => ReferenceEquals(part, required)))
                            list.Add(required);
                    }
                }

                // Anything that needs the entry PARSED is decided by how many
                // entries there are, not by which scope asked.
                //
                // It used to be "skip whenever the scope is the whole archive",
                // and that was the wrong axis. Parsing 2,742 mob images to scan
                // for art links is the ten-second cost MobService caches so
                // carefully, and paying it inside a preview would make the
                // preview unusable -- but a folder of 200 equips is nothing like
                // that, and skipping the links there would quietly break the one
                // promise this feature makes: tick a thing and everything it
                // needs comes with it.
                //
                // So the line is drawn at a measured entry count. Under it, links
                // and boss parts are followed as they are for a selection; over
                // it, they are skipped and Limits() says so in as many words.
                if (requestedCount > MaxParsedEntries)
                    return;

                // Below the entry's own parts and above everything that only
                // reports, because these are parts: a map's scenery is copied, and
                // the totals, the conflict counts and the undo entry all have to
                // cover it. Reachable only here because reading a name means
                // parsing the entry, which is exactly what the guard above is
                // about -- and why a kind that declares these refuses bulk outright
                // rather than quietly skipping them.
                if (entry.Id > 0)
                {
                    item.Parts.AddRange(NamedParts(
                        spec, entry, item, source, targetClient, target,
                        named, rewrites, renames, containers, containerSteps, unsatisfied,
                        namedUses, linkCarry));
                }

                NoteLinks(item, entry, spec, source, SourceIndex, plan, splitArt);
                NoteBossParts(item, entry, spec, SourceIndex, targetIndex, plan);
                NoteRequirements(item, entry, spec, source, targetClient);
                NoteInertFlags(item, entry, spec);
                NoteAnimations(item, entry, spec, targetClient);

                if (!request.FollowLinks || depth >= MaxLinkDepth)
                    return;

                // Everything the copied node reaches for, followed across
                // archives. See Dependencies() for why this is on by default.
                // The switch stays honest either way: with it off the images are
                // still named, they are simply not carried.
                foreach (EntryLocation needed in
                         Dependencies(entry, source, item, request.IncludeArtOutlinks))
                {
                    if (dependencies.ContainsKey(needed.Path))
                        continue;
                    if (seen.Count + dependencies.Count >= MaxClosureEntries)
                    {
                        overflowed = true;
                        break;
                    }
                    dependencies[needed.Path] = needed;
                    Collect(needed, requested: false,
                            pulledInBy: $"art {entry.Id} draws from", depth + 1);
                }

                // The edges the kind table declares: what it links to for art,
                // the parts it summons, the skills it attacks with. One loop,
                // because they are one kind of thing -- a reference.
                foreach ((PortReference edge, int referenced) in ReferencedIds(entry, spec))
                {
                    if (seen.Count >= MaxClosureEntries)
                    {
                        overflowed = true;
                        break;
                    }

                    if (edge.Role == null)
                    {
                        if (seen.ContainsKey("id:" + referenced))
                            continue;
                        if (SourceIndex().Get(entry.Scope, referenced) is { } sibling)
                        {
                            Collect(sibling, requested: false,
                                    pulledInBy: Because(edge, entry.Id), depth + 1);
                            continue;
                        }
                        item.Notes.Add(
                            $"{Because(edge, entry.Id)} is {referenced}, which is not in the archives you " +
                            $"have open for {source.Label}. Open that archive and port again, or the copy " +
                            "will be missing it in game.");
                        continue;
                    }

                    EntryLocation? external = ResolveReferenced(source, edge, referenced);
                    if (external == null)
                    {
                        // Two different failures, and telling them apart is the
                        // difference between "open another archive" and "this
                        // client does not work the way you think". Measured on a
                        // v232 client: its Skill.wz has no MobSkill.img at all --
                        // it holds EliteMobSkill.img and nothing else mob-shaped
                        // -- so a boss's info/skill ids do not resolve there, and
                        // reporting that as a missing entry would send someone
                        // hunting for an image that does not exist.
                        bool haveImage = FindImage(
                            source.Files.Where(f => RoleOf(f) == edge.Role
                                                 || KindsOf(f).Contains(edge.Role ?? "")).ToList(),
                            edge.Image ?? "") != null;

                        item.Notes.Add(haveImage
                            ? $"It uses {edge.Image} entry {referenced}, which is not in {source.Label}'s " +
                              $"{edge.Image}. Nothing here can carry it, so the copy will not do that in game."
                            : $"It names {edge.Image} entry {referenced}, but {source.Label} has no " +
                              $"{edge.Image} open — this client may not keep them there at all. Nothing was " +
                              "copied for it, and the ported copy will not do that in game unless the target " +
                              "already has it.");
                        continue;
                    }
                    if (dependencies.ContainsKey(external.Path))
                        continue;
                    dependencies[external.Path] = external;
                    Collect(external, requested: false, pulledInBy: Because(edge, entry.Id), depth + 1);
                }
            }

            if (overflowed)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"What you picked needs more than {MaxClosureEntries} nodes once everything it draws " +
                    "from is followed — links, art in other archives, and what those reach for in turn. " +
                    "Nothing was written. A port that stops half way through a closure is worse than one " +
                    "that does not start: the copy is there, it looks right, and the pieces it needs are " +
                    "missing. Port fewer at a time, or use the whole-archive scope if you want the lot.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            // Every satellite row that was actually discovered is part of the
            // selected entry. A derived reference such as setItemID is also a
            // prerequisite when its row is absent, because the entry explicitly
            // names it. Stop before any ordinary entry is considered for writing:
            // a partial import that reports success is the failure this workflow
            // exists to prevent.
            List<PortPartDto> unavailableRequired = requiredByItem.Values
                .SelectMany(parts => parts)
                .Distinct<PortPartDto>(ReferenceEqualityComparer.Instance)
                .Where(part => part.Status is "Absent" or "Blocked")
                .ToList();
            if (unavailableRequired.Count > 0)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    "One or more selected entries has a dependency that cannot be carried exactly: "
                    + string.Join(" ", unavailableRequired
                        .Select(part => part.Reason)
                        .Where(reason => !string.IsNullOrWhiteSpace(reason))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3))
                    + " Nothing was written. Open the named archive on the target and preview again. If it "
                    + "is already open, that client does not contain the destination image this dependency "
                    + "needs; add that image or choose the matching archive rather than accepting a partial import.";
                Truncate(plan);
                return Done(plan, clock, written, failed, needsDecision);
            }

            // A name neither client has, refused before a byte is written.
            //
            // This is the one outcome the merge cannot produce something drawable
            // from. Everywhere else a named reference ends up pointing at the
            // source's picture, the target's picture, or the source's picture
            // under a new name; here it points at nothing, and a map whose object
            // set is missing is the "empty room" this kind was refused over for
            // years.
            //
            // A door rather than a wall, and that is measured rather than
            // generous. Across all 17,442 maps of a v232 Map002.wz there are
            // 1,876 distinct names those maps draw by, and four of them are in no
            // archive that client ships: Back/coordiKing, Obj/starPlanet,
            // BgmPL2.img/Aburp and PL_Beautyroid.img/Lab. The client runs those
            // maps regardless, which settles two things at once — a missing name
            // is not the sort of failure that takes a client down, and a wall here
            // would make a handful of Nexon's own maps permanently unportable over
            // a fault they shipped with. The overwhelmingly commoner cause is
            // still an archive nobody opened, so the default is to stop and say
            // which one.
            if (unsatisfied.Count > 0)
            {
                List<string> distinct = unsatisfied
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string refusal =
                    $"{distinct.Count:N0} thing{(distinct.Count == 1 ? "" : "s")} "
                    + $"{(distinct.Count == 1 ? "this" : "these")} "
                    + $"{spec.Plural.ToLowerInvariant()} draw{(distinct.Count == 1 ? "s" : "")} by name "
                    + $"{(distinct.Count == 1 ? "is" : "are")} in neither client: "
                    + string.Join(", ", distinct.Take(8))
                    + (distinct.Count > 8 ? $" and {distinct.Count - 8} more" : "")
                    + $". {source.Label} has nothing to copy and {plan.TargetClient} has nothing to fall "
                    + "back on, so the copy would name pictures that are not there — a room with pieces of "
                    + "it missing. Almost always that means an archive is not open: on a v232 client the "
                    + "objects and tiles are in Map.wz and Map2.wz, the backgrounds in Map001.wz and "
                    + "Map2.wz, the marks in Map.wz and the music in Sound002.wz — open those on BOTH "
                    + "sides and preview again. If they are all open, this is a reference the source "
                    + "client itself does not satisfy; its own client draws that map with the piece "
                    + "missing, and you can choose to do the same.";

                if (!request.AcceptMissingNames)
                {
                    plan.Blocked = true;
                    plan.BlockedReason = refusal + " Nothing was written.";
                    plan.BlockedOverride = true;
                    plan.BlockedOverrideAccepts = "acceptMissingNames";
                    Truncate(plan);
                    return Done(plan, clock, written, failed, needsDecision);
                }

                plan.Warnings.Add("You chose to port anyway, over this: " + refusal);
            }

            /* ---------------- what an apply would do ---------------- */

            foreach (PortItemDto item in items)
            {
                foreach (PortPartDto part in item.Parts)
                {
                    part.WillWrite = part.Status switch
                    {
                        "New" => true,
                        "Conflict" => request.Overwrite,
                        _ => false,
                    };
                }
            }

            HashSet<PortPartDto> requiredParts = requiredByItem.Values
                .SelectMany(parts => parts)
                .ToHashSet<PortPartDto>(ReferenceEqualityComparer.Instance);
            Dictionary<PortPartDto, List<PortItemDto>> ownersByPart =
                new(ReferenceEqualityComparer.Instance);
            foreach (PortItemDto owner in items)
            {
                foreach (PortPartDto owned in owner.Parts.Distinct<PortPartDto>(ReferenceEqualityComparer.Instance))
                {
                    if (!ownersByPart.TryGetValue(owned, out List<PortItemDto>? owners))
                        ownersByPart[owned] = owners = new List<PortItemDto>();
                    owners.Add(owner);
                }
            }

            bool ReadyInPlan(PortItemDto owner) =>
                !requiredByItem.TryGetValue(owner, out List<PortPartDto>? prerequisites)
                || prerequisites.All(part => part.Status == "Same" || part.WillWrite);

            bool ReadyAfterWrite(PortItemDto owner) =>
                !requiredByItem.TryGetValue(owner, out List<PortPartDto>? prerequisites)
                || prerequisites.All(part => part.Status == "Same" || part.Applied);

            // A conflict in a discovered dependency is not optional for the entry
            // that owns it. With Replace off, carry neither the entry nor its
            // other parts; otherwise the UI would call a partial import complete.
            foreach (PortPartDto part in DistinctParts(items))
            {
                if (!part.WillWrite || requiredParts.Contains(part)
                    || !ownersByPart.TryGetValue(part, out List<PortItemDto>? owners)
                    || owners.Any(ReadyInPlan))
                {
                    continue;
                }

                part.WillWrite = false;
                string dependency =
                    "Not copied because one of its dependencies conflicts with the target. Tick Replace and "
                    + "preview again only if you intend to replace the target's existing dependency too.";
                part.Reason = With(dependency, part.Reason);
                if (part.Kind is "entry" or "link-entry")
                    part.Status = "Blocked";
            }

            // Two different things planned into one place, refused out loud.
            //
            // The plan writes through DistinctParts, which deduplicates on target
            // path -- correctly, because two parts aiming at one path would be one
            // node move and counting it twice is a lie about what the port does.
            // What it cannot tell on its own is whether the second part was the
            // SAME move. Measured: three cash items planned together were each
            // given the same Commodity.img row, so two of them were dropped here
            // while the totals said one row and every one of the three rendered
            // "New / will write". The allocation defect that caused it is fixed in
            // ShopPart; this is the structural guarantee that the class of it can
            // never be silent again, wherever it comes from next.
            //
            // Parts are shared by reference on purpose -- one container part
            // belongs to 583 items -- so identity is settled before paths are
            // compared, or every shared part would report a collision with itself.
            Dictionary<string, PortPartDto> claimed = new(StringComparer.OrdinalIgnoreCase);
            HashSet<PortPartDto> once = new(ReferenceEqualityComparer.Instance);
            foreach (PortPartDto part in items.SelectMany(i => i.Parts))
            {
                if (!once.Add(part) || !part.WillWrite || part.TargetPath == null)
                    continue;

                if (!claimed.TryGetValue(part.TargetPath, out PortPartDto? first))
                {
                    claimed[part.TargetPath] = part;
                    continue;
                }

                if (string.Equals(first.SourcePath, part.SourcePath, StringComparison.OrdinalIgnoreCase))
                    continue;   // the same copy, reached twice

                part.WillWrite = false;
                part.Status = "Blocked";
                part.Existing = first.SourcePath;
                part.Reason =
                    $"This and {first.SourcePath} were both planned into {part.TargetPath}, and only one of " +
                    "them can land there. Nothing is written for this one rather than one of the two being " +
                    "dropped without saying so. Port them one at a time, or make room in the target first.";
            }

            // Scenery nobody is going to draw.
            //
            // A named part is not wanted for its own sake -- it is wanted because
            // an entry names it. Port a map the target already has with the
            // overwrite left off and the map itself is a Conflict that will not be
            // written, while its scenery is all New: without this the run copies
            // 21.7 MB of object book into the client for a map that never arrives,
            // under a name nothing will ever reference. Measured on the shape of
            // the v232 folder this was written against; it is the same wasted
            // write on every re-run.
            //
            // Decided across items, not per item, because these parts are shared:
            // a tile set two maps name is written if EITHER of them lands.
            if (spec.Named != null)
            {
                HashSet<PortPartDto> drawn = new(ReferenceEqualityComparer.Instance);
                foreach (PortItemDto item in items)
                {
                    if (!item.Parts.Any(p => p.Kind is "entry" or "link-entry" && p.WillWrite))
                        continue;
                    foreach (PortPartDto part in item.Parts.Where(p => p.Kind == "named"))
                        drawn.Add(part);
                }

                foreach (PortPartDto part in items.SelectMany(i => i.Parts))
                {
                    if (part.Kind != "named" || !part.WillWrite || drawn.Contains(part))
                        continue;

                    part.WillWrite = false;
                    part.Reason =
                        $"Not copied: every {spec.Label.ToLowerInvariant()} that draws on it is one the "
                        + "target already has and this port is not replacing, so the copy would sit in the "
                        + "client with nothing naming it. "
                        + (part.Reason ?? "");
                }
            }

            plan.Totals = Total(items, requested);
            Warn(plan, spec, source, target, items, bulk);
            SampleConflicts(plan, items);

            if (bulk && plan.Totals.Bytes > MaxArchiveBytes)
            {
                plan.Blocked = true;
                plan.BlockedReason =
                    $"This archive holds about {Megabytes(plan.Totals.Bytes)} of {spec.Plural.ToLowerInvariant()}, " +
                    $"and a whole-archive port is capped at {Megabytes(MaxArchiveBytes)}. The cap is memory, " +
                    "not policy: the single undo entry holds a full in-memory clone of everything it copied " +
                    "and the parsed originals stay resident beside it, so this would need roughly " +
                    // Three, not two: a whole Npc.wz of 1,006 MB measured 2.89 GB
                    // of working set, and a preview that understates the cost is
                    // the one that gets ignored.
                    $"{Megabytes(plan.Totals.Bytes * 3)} on top of the two clients you already have open. " +
                    "Port the ones you need with the selection scope instead — refusing here is better than " +
                    "starting and dying halfway through.";
                return Done(plan, clock, written, failed, needsDecision);
            }

            /* ---------------- the shape the target can read ---------------- */

            // Both of these used to be computed inside the write, a few lines
            // after the preview had already gone back to the caller. They are what
            // decides whether the canvas links this port is about to land are ones
            // the target can follow, so a plan that could not see them could not
            // refuse on them — it could only warn, afterwards, about a client that
            // was already broken. Hoisted, and nothing below them writes.
            //
            // Still measured from the target as it WAS: the first entry copied
            // would otherwise create the very _Canvas directory the check looks
            // for and turn the answer over halfway through the run.
            List<string> landed = new();
            HashSet<string> incoming = new(
                DistinctParts(items)
                    .Where(x => x.WillWrite && x.TargetPath != null)
                    .Select(x => WzPath.Split(x.TargetPath!).LastOrDefault() ?? "")
                    .Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            bool flattenArt = !TargetUsesCanvasDirectory(target, incoming);

            // Which '_Canvas' directories were already there, read from the
            // target as it was and for the same reason everything else on this
            // line is: the first art image copied creates the directory this
            // would otherwise be asked about afterwards.
            //
            // The sweep may detach one it leaves empty, but only one this run
            // created — see the call site. An older one is left alone whatever
            // state it ends in.
            HashSet<string> canvasBefore = new(StringComparer.OrdinalIgnoreCase);
            foreach (OpenFile archive in targetClient?.Files ?? new List<OpenFile> { target })
                CollectCanvasDirectories(archive, canvasBefore);

            // Which canvas encodings the target's own art uses — read HERE, at
            // plan time, because everything it decides is knowable before a byte
            // is written and the warnings below belong on the preview.
            //
            // This was on the apply path, hoisted once, and reverted: the old
            // sampler parsed and UNPARSED target images, and unparsing an image
            // whose in-memory tree is its only content destroys it — a preview
            // mutating the thing it previews, caught by
            // Map_PortedTwice_WritesNothingTheSecondTimeAndChangesNothing.
            // TargetCanvasFormats no longer unparses anything it did not itself
            // parse cold, and the vocabulary is cached against the structural
            // generation, so the hoist is affordable as well as safe.
            //
            // Still read from the target as it stands before this port writes:
            // sampling after the port began would learn the vocabulary of the
            // art just copied in, which is the one thing it must not be
            // measured against.
            HashSet<WzPngFormat> formats = TargetFormats(target);
            if (formats.Count == 0)
            {
                // Nothing to compare against is not the same as nothing to do.
                // RetargetCanvasFormats returns 0 without looking when the
                // vocabulary is empty, which is right -- it must not guess a
                // format from no evidence -- but it is also silent, and a port
                // whose frames are all still BC7 in a DirectX 9 client draws
                // nothing while every check passes.
                plan.Warnings.Add(
                    $"No canvas in the {FormatSampleImages} images sampled from {target.Name} could be read, "
                    + "so there is no way to tell which compression formats this client understands and "
                    + "nothing will be re-encoded. Frames arrive in whatever format the source stored them in — "
                    + "on a modern source that is Format4098 (BC7, a DirectX 11 block format), which a "
                    + "client rendering through DirectX 9 loads without complaint and draws as nothing.");
            }
            else if (requestedCount <= MaxParsedEntries)
            {
                // The other half of the same fact, said before the write instead
                // of after it: how much of what this port carries is in a format
                // the target has none of. The exact re-encode/refusal split is an
                // outcome — the encoder picks a format from the pixels — but
                // "these frames are foreign here" is a plan-time fact.
                //
                // Gated the way link-following is gated: over MaxParsedEntries
                // the plan does not parse the entries, and neither does this.
                (int foreign, SortedSet<string> foreignNames) = ForeignCanvasFormats(items, formats);
                if (foreign > 0)
                {
                    plan.Warnings.Add(
                        $"{foreign:N0} {(foreign == 1 ? "frame" : "frames")} this port carries "
                        + $"{(foreign == 1 ? "is" : "are")} compressed in a format this client's own art "
                        + $"never uses ({string.Join(", ", foreignNames.Take(4))}"
                        + (foreignNames.Count > 4 ? $" and {foreignNames.Count - 4} more" : "")
                        + $"; the target's vocabulary is {string.Join(", ", formats.OrderBy(f => f))}). "
                        + "They will be re-encoded when the port is applied. Any frame the encoder cannot "
                        + "re-encode — below 64x64, or with a side that is not a multiple of four — stays "
                        + "as it arrived and will draw nothing.");
                }
            }

            // Will this art image resolve on the TARGET side once the copying is
            // done? That, and not whether the source can see it, is what the
            // reshape asks — Find() inside FlattenCanvasArt is
            // ResolveImage(targetClient, ...) — and it is the question that was
            // never asked before anything was written.
            bool ResolvesOnTarget(string image)
            {
                // A target that is not part of a client group resolves nothing at
                // all: Find() returns null without looking. That is the measured
                // 200-dead-link case, where the art travelled — 228 MB of it — and
                // sat unread beside the links that still pointed at it.
                if (targetClient == null)
                    return false;

                // Already there, which covers both the target's own art and a part
                // this port will not write because the target already holds it.
                if (ResolveImage(targetClient, image) != null)
                    return true;

                // Not there yet, but arriving: the closure pulled it in and a part
                // of this port writes it. ArchiveFor sends it to an archive of the
                // target client, so the same lookup will find it afterwards.
                return ResolveImage(source, image) is { } inSource
                    && byPath.TryGetValue(inSource.Path, out PortItemDto? carried)
                    && carried.Parts.Any(p => p.WillWrite);
            }

            // The fatal combination, refused before a byte is written.
            //
            // Two facts have to be true together. The target stores its art inline
            // (flattenArt), so every link has to be reshaped into the one-level
            // form; and the art a link names cannot be found on the target side
            // afterwards, so there is nothing to reshape it into and the link is
            // left exactly as it came. A v232 client follows an '_outlink' one
            // level and no further, which makes
            // "Skill/_Canvas/422.img/skill/4221017/hit/0/8" not a blank frame but
            // a canvas it cannot reach — and the window that tries to draw one
            // takes the client down with it. Blank icons are survivable; this is
            // the one failure in a port that is not.
            //
            // Measured, and the whole reason this is a refusal rather than a
            // warning: the same port of 4221017 left 0 dead links with the target
            // family fully open (Skill.wz + Skill001/002/003 + String.wz) and 30
            // with only Skill.wz and String.wz — every one of them on that id, in
            // hit/0/* and effect0/*. Nothing about the plan looked different.
            //
            // Only what was actually scanned can be judged: past MaxParsedEntries
            // no entry is parsed for links at all, splitArt is empty, and this
            // cannot fire. Limits() says so where the plan is read.
            List<SplitArtUse> unreachable = splitArt.Values
                .Where(use => !ResolvesOnTarget(use.Image))
                .OrderByDescending(use => use.Links)
                .ThenBy(use => use.Image, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The other direction, and the one nothing looked in: neighbours in
            // the TARGET that borrow frames from an entry this port would replace.
            // Same failure, same client, reached from the other side — see
            // OrphanedByOverwrite for the 83 measured on 4221016.
            //
            // Only for entries actually being replaced, so a port that adds
            // without overwriting cannot trip it, and only where the entries were
            // parsed at all.
            List<(string Dependant, int Links, string Key)> orphaned = new();
            if (request.Overwrite && requestedCount <= MaxParsedEntries)
            {
                // The neighbours this run is ALSO replacing are not neighbours.
                //
                // The finding is "an entry the target already has borrows frames
                // from one being replaced", and its own remedy is "port those ids
                // too so they are replaced as a set". A dependant that is itself
                // in this port has no old inlinks left to orphan — the node
                // carrying them is overwritten wholesale by the source's, which
                // brings its own. Without this the refusal fires on exactly the
                // user who took its advice: measured, porting 4221014, 4221016
                // and 4221017 together was blocked over the 83 inlinks on the
                // target's 4221016, an entry in the very same request, and
                // nothing was written.
                HashSet<string> alsoReplaced = new(
                    DistinctParts(items)
                        .Where(x => x.WillWrite && x.Kind is "entry" or "link-entry"
                                    && x.TargetPath != null)
                        .Select(x => x.TargetPath!),
                    StringComparer.OrdinalIgnoreCase);

                foreach (PortPartDto part in DistinctParts(items))
                {
                    if (part.WillWrite && part.Status == "Conflict"
                        && part.Kind is "entry" or "link-entry")
                    {
                        orphaned.AddRange(
                            OrphanedByOverwrite(part)
                                .Where(o => !alsoReplaced.Contains(o.Key)));
                    }
                }
            }

            if (flattenArt && unreachable.Count > 0)
            {
                string refusal = DeadCanvasLinkRefusal(unreachable, source, targetClient, target);

                if (!request.AcceptDeadCanvasLinks)
                {
                    plan.Blocked = true;
                    plan.BlockedReason = refusal;

                    // The one refusal here that is a judgement rather than a fact
                    // about the archives, so the one the caller may overrule. See
                    // PortPlanDto.BlockedOverride: without this the door is never
                    // drawn and someone whose target really does read the split
                    // form has no way past a verdict about a different client.
                    plan.BlockedOverride = true;
                    plan.BlockedOverrideAccepts = "acceptDeadCanvasLinks";
                    Truncate(plan);
                    return Done(plan, clock, written, failed, needsDecision);
                }

                // Overruled, and still on the record. A decision this size must
                // not become invisible the moment it is taken.
                plan.Warnings.Add(
                    "You chose to port anyway, over this: " + refusal);
            }
            else if (targetClient == null)
            {
                // Said only when the refusal above did not already say it at
                // length. Nothing named art through a _Canvas directory, so this
                // port is not in danger today — but the reshape is still blind on
                // this side, and the next port from a split client would be.
                plan.Warnings.Add(
                    $"{target.Name} is open on its own rather than as part of its client folder, so "
                    + "nothing here can see the rest of that client. Art is reshaped by looking it up "
                    + "across the whole family, so opened this way every canvas link lands exactly as "
                    + "it came — and if this client keeps its art inline, those frames are ones it "
                    + "cannot follow. Open the target through its folder, with the numbered siblings "
                    + "and String.wz, and port again.");
            }

            if (orphaned.Count > 0)
            {
                int orphanedLinks = orphaned.Sum(o => o.Links);
                string who = string.Join(", ", orphaned.Take(6).Select(o => $"{o.Dependant} ({o.Links})"));
                string dependants =
                    $"{orphanedLinks:N0} canvases belonging to {orphaned.Count:N0} entries the target "
                    + "ALREADY HAS borrow their frames from entries this port would replace, by a path that "
                    + "will not exist once the newer node is in place: "
                    + who
                    + (orphaned.Count > 6 ? $" and {orphaned.Count - 6} more" : "")
                    + ". Those are not entries you asked to touch. An '_inlink' is written as a path from "
                    + "the book's root, so a neighbour sharing a book with what you are overwriting draws "
                    + "out of it by name, and a build that reshaped that entry leaves the neighbour "
                    + "pointing at nothing — the same unreachable canvas an unfollowable '_outlink' is, "
                    + "and the same crash. Measured: porting 4221014 alone took the target's own 4221016 "
                    + "from 0 dead links to 83. Port those ids too so they are replaced as a set, or leave "
                    + "the ones they depend on alone.";

                if (!request.AcceptDeadCanvasLinks)
                {
                    plan.Blocked = true;
                    plan.BlockedReason = dependants + " Nothing was written.";
                    plan.BlockedOverride = true;
                    plan.BlockedOverrideAccepts = "acceptDeadCanvasLinks";
                    Truncate(plan);
                    return Done(plan, clock, written, failed, needsDecision);
                }

                plan.Warnings.Add("You chose to port anyway, over this: " + dependants);
            }

            /* ---------------- what Match would remove ---------------- */

            // Said BEFORE the write, and said by the plan, because it is knowable
            // by the plan.
            //
            // Match deletes entries the user never named — that is what it is
            // for — and until now the only thing that ever mentioned it was a
            // sentence appended to the result after _edit.Delete had already run.
            // Someone who ticked the box and wanted to know what it meant could
            // only find out by doing it. The set depends on which containers the
            // port writes into, and the plan already knows every path it intends
            // to land at, so there is nothing here that had to wait for the write.
            List<string> plannedRemovals = MatchRemovals(
                request, spec, target,
                DistinctParts(items)
                    .Where(p => p.WillWrite && p.Kind is "entry" or "link-entry" && p.TargetPath != null)
                    .Select(p => p.TargetPath!),
                SourceIndex);

            if (plannedRemovals.Count > 0)
            {
                DescribeRemovals(plan, plannedRemovals);

                // At the front, and phrased as the thing that happens to the
                // user's client rather than as a property of the port. An
                // instruction nobody reads is one filed among the informational
                // notes; this is the one line on the plan that describes a loss.
                plan.Warnings.Insert(0,
                    $"Match will DELETE {plannedRemovals.Count:N0} "
                    + $"{spec.Plural.ToLowerInvariant()} the target has and {plan.SourceClient} does not, "
                    + "from the containers this port writes into. They are listed under Removals. One undo "
                    + "puts them back; nothing else does.");
            }

            // The renames, said BEFORE the write rather than counted after it.
            //
            // `rewrites` is filled by NamedParts, which runs above -- so which
            // scenery travels under a new name, and what that name is, is a plan
            // decision and always was. Only the number of references rewritten
            // is an outcome, and that is what the apply reports. Saying it here
            // is what makes it a decision the user takes: a map that will draw
            // out of 'acc1_c1' instead of 'acc1' is the merge working, and it is
            // also the single most surprising thing a map port does.
            if (rewrites.Count > 0)
            {
                plan.Warnings.Add(
                    $"{rewrites.Count:N0} scenery {(rewrites.Count == 1 ? "name" : "names")} in the "
                    + $"copies will be repointed, because {plan.TargetClient} already has different "
                    + "pictures under those names and nothing here will replace one: "
                    + RenameList(rewrites)
                    + ". Every other map in the target still draws what it drew before.");
            }

            // The images the copies drag in behind their own art links — the
            // whole cost, said before the write, because the entries' sizes say
            // nothing about it: the recorded case is a 51 KB map whose scenery
            // shell drew 14 of its 61 frames out of an image that never came.
            List<LinkedImageDecision> riding = linkCarry.Decisions.Values
                .Where(d => d.Outcome == "carried")
                .OrderBy(d => d.Source.Relative, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (riding.Count > 0)
            {
                plan.Warnings.Add(
                    $"{riding.Count:N0} image{(riding.Count == 1 ? "" : "s")} the copies draw from by "
                    + $"absolute art link will ride along ({Megabytes(riding.Sum(d => d.Bytes))}): "
                    + string.Join(", ", riding.Take(8).Select(d => d.Source.Relative))
                    + (riding.Count > 8 ? $" and {riding.Count - 8} more" : "")
                    + ". The target's own copies of those names, where it has them, hold different "
                    + "pictures at the linked frames, so leaving the links alone would draw the wrong "
                    + "build's art behind the copied scenery. Links into any that land under a new name "
                    + "are rewritten in the copies; nothing the target already had is touched.");
            }

            /* ---------------- the write ---------------- */

            // The one thing allowed to interrupt a one-click port. A conflict is
            // the only discovery that can destroy something the user already had;
            // everything else a port finds is reported after the fact, because
            // making someone read a table before every routine port is how the
            // feature stops being used at all.
            needsDecision = apply
                         && !request.Overwrite
                         && request.StopOnConflict
                         && plan.Totals.Conflicts > 0;

            if (!apply || needsDecision || plan.Totals.WillWrite == 0)
            {
                Truncate(plan);
                return Done(plan, clock, written, failed, needsDecision);
            }

            // `formats` was read at plan time, above, from the target as it was —
            // both of its warnings are on the preview now, and the vocabulary the
            // apply re-encodes against is the one the preview showed.
            string label = wholeArchive
                ? $"Port {plan.Totals.Entries} {spec.Plural.ToLowerInvariant()} from {plan.SourceArchive} to {plan.TargetClient}"
                : $"Port {items.Count} {(items.Count == 1 ? spec.Label.ToLowerInvariant() : spec.Plural.ToLowerInvariant())} to {plan.TargetClient}";

            // One batch for the whole port, which is the requirement: a port is
            // many node moves across three archives and it has to come back with
            // one Ctrl+Z, not four thousand. `landed`, `incoming` and `flattenArt`
            // were read above, from the target as it was, before any of this could
            // disturb them.
            using (IDisposable batch = _undo.Batch(label))
            {
                // The same enumeration the totals were taken from, so "n written"
                // and "n to copy" cannot come apart. Write() re-checks a container
                // before creating one, so the surplus visits were no-ops rather
                // than duplicate writes and the archive was always right — but
                // each one still incremented `written`, and a result that
                // contradicts its own undo entry is not one anybody can act on.
                // Dependency containers first, then required cross-archive rows,
                // then entries and optional structure. If any required write
                // throws, none of the entry or remaining parts that depend on it
                // are allowed to land afterwards.
                foreach (PortPartDto part in DistinctParts(items)
                             .OrderBy(part => part.Kind == "container" ? 0
                                 : requiredParts.Contains(part) ? 1 : 2))
                {
                    if (!part.WillWrite)
                        continue;

                    if (!requiredParts.Contains(part)
                        && ownersByPart.TryGetValue(part, out List<PortItemDto>? owners)
                        && !owners.Any(ReadyAfterWrite))
                    {
                        part.Status = "Blocked";
                        part.Error =
                            "A required dependency could not be written, so this part was left untouched "
                            + "rather than creating an incomplete imported entry.";
                        failed++;
                        continue;
                    }
                    try
                    {
                        Write(part, request.Overwrite, containerSteps, renames, landed);
                        part.Applied = true;
                        written++;
                    }
                    catch (Exception ex)
                    {
                        // Per part, deliberately. A throw that abandoned the loop
                        // would leave the earlier parts written with nothing said
                        // about them — which is exactly the state this feature
                        // exists to stop people getting into by hand.
                        // WzEditService.SetValueMany and MobService.Bulk set the
                        // same rule: never let one bad row decide the batch's
                        // fate, and report what actually landed.
                        part.Applied = false;
                        part.Error = ex.Message;
                        failed++;
                        _log.LogWarning(ex, "Port part failed: {Label}", part.Label);
                    }
                }

                // After every part is in place, never as each one lands.
                //
                // Reshaping an entry's art means reading the art image, and the
                // art image is itself one of the parts being written. Doing it
                // per-part meant the outcome depended on the order the plan
                // happened to list them: 8881710 was written before its own
                // _Canvas image existed in the target, found nothing to inline,
                // and kept the link the whole exercise was there to remove --
                // while its three siblings, written later, rewrote correctly and
                // pointed at a host that had not been filled in. Inside the
                // batch, so it is still one undo.
                // Guarded per path. A canvas that will not decode is a real
                // state -- a link placeholder's blob is zero bytes and asking
                // for it throws -- and an escape from here abandoned the whole
                // apply after everything had already been written, with nothing
                // said about what landed. It also made which entries got
                // reshaped depend on the order the plan happened to list them,
                // which is the fault this pass was moved out of the write loop
                // to remove.
                int refusedTotal = 0;
                int removedToMatch = 0;
                int unreshapedTotal = 0;
                List<string> artFailures = new();

                // Matched to the source: everything the new build does not have,
                //
                // Runs before the art pass on purpose: a stale id is still a
                // reader of whatever art it points at, and reshaping the archive
                // around entries that are about to be deleted would decide what to
                // inline and what to share on the strength of nodes that will not
                // be there.
                //
                // Only inside containers this port wrote to, and only for ids the
                // source genuinely lacks. A container the source does not have at
                // all is never emptied -- that is a book this port knows nothing
                // about, not one the new build dropped.
                if (request.Match && request.Overwrite)
                {
                    // Recomputed from what actually landed, not taken from the
                    // plan. The plan's list is the disclosure; this is the act,
                    // and the two are computed by one method so they cannot drift
                    // apart in what they mean — but they are computed from
                    // different inputs, because "what I said I would land" and
                    // "what landed" are different facts and this project has
                    // already shipped a result that confused them.
                    List<string> stale = MatchRemovals(request, spec, target, landed, SourceIndex);

                    // And when they disagree, that is said rather than smoothed
                    // over. A user who read the plan's Removals list is entitled
                    // to know it was not the list acted on.
                    if (!stale.OrderBy(p => p, StringComparer.Ordinal)
                              .SequenceEqual(plannedRemovals.OrderBy(p => p, StringComparer.Ordinal),
                                             StringComparer.Ordinal))
                    {
                        plan.Warnings.Add(
                            $"The plan listed {plannedRemovals.Count:N0} entries for Match to remove and the "
                            + $"port removed {stale.Count:N0}. The difference is entries that did not land "
                            + "where the plan expected, so the containers this port wrote into were not "
                            + "quite the ones it predicted. What was actually removed is on this result.");

                        DescribeRemovals(plan, stale);
                    }

                    if (stale.Count > 0)
                    {
                        // Through the edit service, so one Ctrl+Z puts them back. A
                        // delete this port made is part of the port, and a user who
                        // undoes it must not be left with the additions and not the
                        // removals.
                        removedToMatch = _edit.Delete(stale);
                        _log.LogInformation(
                            "Match removed {Count} entries the source does not have.", removedToMatch);
                    }
                }

                // What this port put on the target side, as nodes.
                //
                // The art pass needs it to tell the source's art from art that was
                // already here, which is the difference between "make this book BE
                // the new build" and "point the new build's entries at the old
                // build's pictures". Resolved once, before the pass, because
                // reshaping mutates canvases but moves no nodes.
                //
                // Built only under Match. Left null for a merge, where reusing the
                // target's own art is legitimate and is the behaviour people are
                // asking for when they leave the tick off.
                //
                // Match, here and everywhere: scoped to the BOOK being ported. The
                // set exists to say which art a ported entry may LINK to. It is
                // never a licence to remove the target's own art -- 40000.img
                // serves hundreds of skills nobody asked about, and emptying it
                // would break them outside this port's undo.
                HashSet<WzObject>? portWrote = null;
                if (request.Match)
                {
                    // Identity, spelled out. WzObject overrides neither Equals nor
                    // GetHashCode today, so the default comparer already is
                    // reference equality -- but the whole point of this set is
                    // that a node is not the same as another node that spells its
                    // name the same way, and that is too close to the bug being
                    // fixed to leave resting on what a base class does not do.
                    portWrote = new HashSet<WzObject>(ReferenceEqualityComparer.Instance);
                    foreach (string path in landed)
                    {
                        if (_session.TryResolve(path) is { } wrote)
                            portWrote.Add(wrote);
                    }
                }

                // The copies pointed at the names their scenery actually landed
                // under.
                //
                // After the writing and never before it. The rewrite is applied to
                // the TARGET's copy of an entry, so it cannot run until that copy
                // exists — and doing it on the source instead would edit the
                // client this port only ever reads, every time somebody pressed
                // Preview.
                //
                // Per part rather than over `landed`, because only an entry of
                // this kind carries these references; a scenery image that landed
                // beside it does not, and asking it would walk a 21 MB object book
                // for names it has never had.
                // Through WzReferenceRewriter, not a private walk: since the
                // reference layer grew the role dimension there is exactly one
                // implementation of "rename a named set", and this is it. The
                // map is keyed on ROLE and name — AddNamedSet without a role is
                // unwritable — so bS = "spinOff1" and oS = "spinOff1" can never
                // collapse; the reader matches the property name anywhere under
                // the copy rather than one declared path shape; and the rewrite
                // is idempotent, which is what lets a second port converge
                // instead of layering.
                //
                // `placed` scopes it: a site whose holder is not one of the
                // nodes this port placed is REFUSED with a sentence, never
                // quietly rewritten. Shared scenery is named, not owned, and a
                // target-owned entry naming the same set is precisely the thing
                // this pass must not touch — the refusal is surfaced below
                // rather than swallowed, because a rewrite the plan promised
                // that did not happen is a finding, not a detail.
                int repointed = 0;
                List<string> repointRefused = new();
                if (spec.Named != null && rewrites.Count > 0)
                {
                    ReferenceRewriteMap renameMap = NamedRewriteMap(spec, rewrites);
                    HashSet<WzObject> placed = new(ReferenceEqualityComparer.Instance);
                    foreach (string path in landed)
                    {
                        if (_session.TryResolve(path) is { } placedNode)
                            placed.Add(placedNode);
                    }

                    WzReferenceRewriter rewriter = new();
                    foreach (PortPartDto part in DistinctParts(items))
                    {
                        if (!part.Applied || part.TargetPath == null
                            || part.Kind is not ("entry" or "link-entry"))
                        {
                            continue;
                        }
                        try
                        {
                            WzObject? copy = _session.TryResolve(part.TargetPath);
                            WzImage? copyOwner = OwningImage(copy);
                            if (copy == null || copyOwner == null)
                                continue;
                            WzSessionService.EnsureParsed(copyOwner);

                            ReferenceRewriteReport report = rewriter.Rewrite(copy, renameMap, placed);
                            repointed += report.Rewritten;
                            foreach (ReferenceRewrite refusal in report.Refusals)
                                repointRefused.Add($"{refusal.Path}: {refusal.Reason}");
                        }
                        catch (Exception ex)
                        {
                            // Loud, because the alternative is a copy that names
                            // the target's own picture where the plan said it
                            // would name its own — the exact silent substitution
                            // the merge exists to prevent.
                            artFailures.Add($"{part.TargetPath}: {ex.Message}");
                            _log.LogWarning(ex, "Repointing the scenery of {Path} failed", part.TargetPath);
                        }
                    }
                }
                // What the plan said would be repointed is above, before the
                // write. This is the only half of it that is an outcome: how many
                // individual references the rewrite actually touched.
                if (repointed > 0)
                {
                    plan.Warnings.Add(
                        $"{repointed:N0} scenery {(repointed == 1 ? "reference" : "references")} in the "
                        + "copies were repointed to those names.");
                }
                if (repointRefused.Count > 0)
                {
                    plan.Warnings.Add(
                        $"{repointRefused.Count:N0} scenery "
                        + $"{(repointRefused.Count == 1 ? "reference was" : "references were")} NOT "
                        + "repointed, because the node holding each one belongs to the target rather than "
                        + "to this port: "
                        + string.Join("; ", repointRefused.Take(3))
                        + (repointRefused.Count > 3 ? $" and {repointRefused.Count - 3} more" : "")
                        + ". Nothing the target already had was rewritten.");
                }

                // The copies' absolute art links, pointed at the names the
                // linked images actually landed under. After the write, because
                // the rewrite applies to the TARGET's copies; from the same
                // decisions the plan disclosed, so every copy is rewritten to
                // one final answer; and only ever inside nodes this port itself
                // placed — a part that never applied owes nothing.
                int relinked = 0;
                foreach ((PortPartDto part, List<(string Was, LinkedImageDecision Into)> owed)
                         in linkCarry.Retexts)
                {
                    if (!part.Applied || part.TargetPath == null)
                        continue;

                    Dictionary<string, string> texts = new(StringComparer.Ordinal);
                    foreach ((string was, LinkedImageDecision into) in owed)
                    {
                        if (RetextOf(was, into) is { } now)
                            texts[was] = now;
                    }
                    if (texts.Count == 0)
                        continue;

                    try
                    {
                        WzObject? copy = _session.TryResolve(part.TargetPath);
                        WzImage? owner = OwningImage(copy);
                        if (copy == null || owner == null)
                            continue;
                        WzSessionService.EnsureParsed(owner);

                        int changed = RetextOutlinks(copy, texts);
                        if (changed > 0)
                        {
                            owner.Changed = true;
                            relinked += changed;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Loud, for the same reason the repoint pass is: a copy
                        // whose links still name the target's own image draws
                        // the wrong generation's art, silently, which is the
                        // exact failure the carry exists to prevent.
                        artFailures.Add($"{part.TargetPath}: {ex.Message}");
                        _log.LogWarning(ex, "Rewriting the art links of {Path} failed", part.TargetPath);
                    }
                }
                if (relinked > 0)
                {
                    plan.Warnings.Add(
                        $"{relinked:N0} absolute art link{(relinked == 1 ? "" : "s")} in the copies now "
                        + "name the images that rode along with them.");
                }

                int blankTotal = 0;
                foreach (string path in landed)
                {
                    try
                    {
                        if (flattenArt)
                        {
                            unreshapedTotal += Flatten(
                                true, path, targetClient, out int blankHere, portWrote);
                            blankTotal += blankHere;
                        }

                        // Same gate, same blind spot: a ported skill is a property,
                        // so this re-encoded nothing for exactly the ports that most
                        // needed it.
                        WzObject? copied = _session.TryResolve(path);
                        WzImage? copiedOwner = OwningImage(copied);
                        if (copied != null && copiedOwner != null)
                        {
                            WzSessionService.EnsureParsed(copiedOwner);
                            int recoded = RetargetCanvasFormats(copied, formats, out int refused);
                            refusedTotal += refused;
                            if (recoded > 0)
                            {
                                copiedOwner.Changed = true;
                                _log.LogInformation(
                                    "Re-encoded {Count} canvases in {Path}: the target client's own art uses "
                                    + "{Formats}, and these were in a format it has none of.",
                                    recoded, path, string.Join(", ", formats));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        artFailures.Add($"{path}: {ex.Message}");
                        _log.LogWarning(ex, "Reshaping the art of {Path} failed", path);
                    }
                }

                // The split-form art the port dragged in, now that nothing reads
                // it. Reshaping rewrote every link either at a sibling that holds
                // the frame or into the entry itself, so these images have no
                // readers left -- and only the one whose name matched an entry
                // was being dropped. Measured on the modern Shadower closure this
                // pass was written for: 12 art images, 690,888,441 bytes, of which
                // two atlases are most of it -- 40004.img at 413 MB serving 16
                // canvases and 40000.img at 147 MB serving 36. (An earlier note
                // here said "226 MB across five images", which was the v232-era
                // figure and had stopped being true.)
                //
                // Size is the smaller half. A _Canvas directory left in the
                // target is what TargetUsesCanvasDirectory measures the *next*
                // time, and the ratio it compares against exists precisely
                // because a port's own leavings were being read back as "this
                // client uses the split form". Clearing up after a successful
                // reshape keeps that measurement about the client instead of
                // about what this tool did last week.
                //
                // Only when nothing was left behind. A single unreshaped link is
                // still a reader, and this is not the code that knows which
                // image it reads.
                //
                // Every archive of the client, not just the one the plan was
                // aimed at. Write() routes each part through ArchiveFor, which
                // sends an entry to whichever archive of the target client
                // already holds that family -- so a port aimed at Skill.wz can
                // land art in Skill001.wz, and sweeping only the nominated
                // target would leave exactly the images this is here to remove.
                // Asked of the archive, not inferred from a counter.
                //
                // The gate used to be "reshaping left nothing behind", read off
                // unreshapedTotal. That number is zero when every link was fixed
                // AND when the walk never ran, and the two were indistinguishable
                // from here. A skill port took the second road -- Flatten bailed on
                // a property path before looking at anything -- so the sweep read a
                // vacuous zero as success and deleted the art that every one of
                // those links still pointed at, leaving a client that crashes on
                // the skill window.
                //
                // Counting the survivors answers the question the counter was only
                // standing in for: is anything still reading this? A walk that
                // never happened now says so, because the links are still there.
                //
                // And it is the number the user is TOLD, not merely the one the
                // sweep consults. Those were two different quantities: the sweep
                // asked the archive and the warning was keyed on unreshapedTotal,
                // so every state where links survive without the reshape counting
                // them -- TryResolve handing back nothing for a landed path, a
                // throw counted into artFailures -- swept correctly and reported
                // a clean port. One scan now answers both.
                (int survivingLinks, SortedSet<string> survivingIn) =
                    SurvivingCanvasLinks(targetClient, target);

                //
                // `failed` belongs in this gate and was not in it, and that alone
                // was enough to destroy art the target already owned. A run whose
                // art part failed to write still swept: the entry inlined the
                // TARGET's older pixels -- measured 38x46 where the source frame
                // was 9x17 -- and the sweep then deleted the target's own 226 MB
                // art book, which a second mob was still reading. Reported as
                // written=2 failed=1, and the archive said it was clean.
                bool nothingReadsCanvasArt =
                    flattenArt && failed == 0 && unreshapedTotal == 0 && artFailures.Count == 0
                    && survivingLinks == 0;

                if (nothingReadsCanvasArt)
                {
                    IEnumerable<OpenFile> sweep = targetClient?.Files ?? new List<OpenFile> { target };
                    foreach (OpenFile archive in sweep)
                    {
                        // Asked again of every archive, because flattenArt was
                        // measured on the nominated target alone and a client is
                        // not obliged to be uniform: one can keep Skill.wz flat
                        // while Data\Npc puts 2.48 GB under a single _Canvas.
                        // Sweeping an archive that really does use the split form
                        // would delete its own art on nothing more than a leaf
                        // name colliding with something this port wrote -- art
                        // this run never created and cannot put back. For the
                        // nominated target the answer is already known to be
                        // false, so this costs nothing there.
                        if (TargetUsesCanvasDirectory(archive, incoming))
                            continue;

                        // Scoped to the paths this run actually wrote, not merely
                        // to the names. See DropPortedCanvasArt's `written`: on a
                        // second port of the same content the names alone would
                        // take out the target's own art, outside the undo batch.
                        (int dropped, long bytes) = DropPortedCanvasArt(
                            archive, incoming,
                            landed.ToHashSet(StringComparer.OrdinalIgnoreCase),
                            // Detach one this run created and leave any older one
                            // exactly where it is.
                            //
                            // Transfer records its undo as "put this image back
                            // into that directory", holding the directory object.
                            // Detaching an emptied _Canvas the client already had
                            // leaves those steps re-inserting into a directory
                            // that is no longer in the tree: the undo runs,
                            // reports success, and the image is invisible and
                            // cannot be saved. Measured on an overwrite=true port
                            // that otherwise reported complete success. That is
                            // what `keep` is, and it is why this is not simply
                            // "prune whatever came out empty".
                            //
                            // The one this run created has no such step -- the
                            // only undo it has is its own creation, which removes
                            // it too -- and leaving it behind is not free: the
                            // saved archive then carries a _Canvas directory this
                            // client has never had, which is the shape the whole
                            // reshape exists to keep out of it, and the
                            // acceptance harness reads it as a failed port.
                            pruneEmpty: true,
                            keep: canvasBefore);
                        if (dropped == 0)
                            continue;

                        // Nothing here is undoable, so the archive must not be
                        // able to claim it is clean. This is what SealFile is for
                        // and PortService had never called it: measured, a port
                        // that deleted a 226 MB art book came back with
                        // OpenFile.Dirty false, no dirty images and an undo entry
                        // that said nothing about it, so the loss was invisible
                        // until the client crashed.
                        _undo.SealFile(archive.Id);
                        _log.LogInformation(
                            "Dropped {Count} _Canvas images ({Bytes} bytes) from {Archive}: their frames are "
                            + "inline now, so nothing reads them.",
                            dropped, bytes, archive.Name);
                    }
                }

                // What is actually there, not what Write() reported a moment
                // before the art pass ran.
                //
                // `Applied` and `written` were set the instant a copy returned,
                // and the passes above then consume some of those very nodes --
                // an art image inlined and dropped is a part that was written and
                // is now gone. Measured: written=4, failed=0, with the _Canvas
                // container and the art image inside it both applied=true and
                // both absent from the saved file. A result that contradicts the
                // archive is worse than a smaller one.
                int consumedAfterWriting = 0;
                foreach (PortPartDto part in DistinctParts(items))
                {
                    if (!part.Applied || part.TargetPath == null)
                        continue;
                    if (_session.TryResolve(part.TargetPath) != null)
                        continue;
                    part.Applied = false;
                    part.Reason = "Copied, then consumed: its frames were inlined into the entries that "
                                + "draw them and the split-form image was removed, because this client "
                                + "cannot follow a link into a '_Canvas' directory.";
                    consumedAfterWriting++;
                    written--;
                }
                if (consumedAfterWriting > 0)
                {
                    plan.Warnings.Add(
                        $"{consumedAfterWriting:N0} of the nodes this port copied are no longer separate "
                        + "nodes: their pixels were inlined into the entries that draw them and the "
                        + "split-form art image was removed, which is the whole point of the reshape. They "
                        + "are counted out of the written total so it matches what the archive holds.");
                }

                // Said out loud, because the symptom is silent: a frame left in
                // a format the target has none of draws nothing at all, and the
                // entry around it looks perfectly correct in the editor.
                if (refusedTotal > 0)
                {
                    plan.Warnings.Add(
                        $"{refusedTotal:N0} frames are still in a compression format this client's own art "
                        + "does not use, so they will draw nothing. The encoder picks the format from the "
                        + "pixels and cannot be told which one to produce: anything below 64x64, or with a "
                        + "side that is not a multiple of four, can never become a DXT block format at all. "
                        + "Replace those frames by hand, or accept that they are blank.");
                }
                if (artFailures.Count > 0)
                {
                    // No longer "everything else in this port still landed". That
                    // was said while the entries whose reshape threw kept their
                    // links in the split form, which is precisely the thing that
                    // did not land -- and the survivor count below is the one that
                    // says whether it did.
                    plan.Warnings.Add(
                        $"The art of {artFailures.Count:N0} entries could not be reshaped: "
                        + string.Join("; ", artFailures.Take(3))
                        + ". Whatever links those entries carried are still in the shape they arrived in.");
                }

                // Frames that will simply not draw, kept separate from the ones
                // that take the client down. A link already in the one-level shape
                // naming an id the target's book does not have is a blank icon —
                // the failure this pass is not allowed to make worse by guessing
                // at a nearby id.
                if (blankTotal > 0)
                {
                    plan.Warnings.Add(
                        $"{blankTotal:N0} canvas links are in a shape this client can follow and name a frame "
                        + "that is not in the target's copy of that image — almost always a book of the same "
                        + "name from a different build, the way Skill/40000.img holds 400004104 and 400004114 "
                        + "where the newer one holds 400004666 to 400004669. Those frames draw nothing. "
                        + "Nothing was rewritten to a neighbouring id, because a wrong picture is worse than "
                        + "a missing one.");
                }

                // The loudest of the three, because it is the one that does not
                // merely draw nothing. This client stores its art inline and
                // resolves an _outlink one level only; a link left pointing into
                // a _Canvas directory is a canvas it cannot reach at all, and
                // the window that tries to draw it goes down with it. Reaching
                // here with a count above zero most often means targetClient was
                // null -- the archive being written into is not part of a client
                // group this service recognises -- which used to leave the whole
                // port in the source's shape and say nothing whatsoever.
                // Said out loud, always. A port that quietly deleted entries the
                // user never named would be the most alarming thing this tool does
                // if it were discovered afterwards rather than reported now.
                if (removedToMatch > 0)
                {
                    plan.Warnings.Add(
                        $"Matched to the source: {removedToMatch:N0} entries the source does not have "
                        + "were removed from the containers this port wrote into, so the target now "
                        + "holds what the newer build holds. One undo puts them back.");
                }

                // Read off the archive, never off a counter. `unreshapedTotal` is
                // what the reshape pass believes it left behind, and it is zero
                // both when nothing was left and when the pass never looked --
                // which is the exact shape of the bug that shipped. This is what
                // the target now says.
                // The larger of the two, never one instead of the other.
                //
                // The archive scan is the authority on what is there, and it is
                // the only one that sees links this run did not touch — but it
                // reads memory, so an entry the reshape reported and then failed
                // to keep hold of is invisible to it. The reshape's own count is
                // the authority on what it could not fix, and it is zero both when
                // there was nothing to fix and when it never looked. Taking the
                // maximum means neither blind spot can produce silence, which is
                // the failure this warning exists to prevent and has now had three
                // times.
                int deadLinks = Math.Max(survivingLinks, unreshapedTotal);
                if (deadLinks > 0)
                {
                    plan.Warnings.Add(
                        $"{deadLinks:N0} canvas links still point into a '_Canvas' directory, which this "
                        + "client does not use and cannot resolve — in "
                        + string.Join(", ", survivingIn.Take(4))
                        + (survivingIn.Count > 4 ? $" and {survivingIn.Count - 4} more" : "")
                        + ". Those frames will not draw, and the window that tries to draw one can take the "
                        + "client down. Open every archive of the target's family — Skill.wz and Skill001.wz "
                        + "and Skill002.wz and Skill003.wz, not just the one you picked — through the client "
                        + "folder, and run the port again. The art itself was left where it is, so nothing "
                        + "has to be re-copied.");
                }

                // What this apply now CLAIMS, filed per archive so the save of
                // each one can be held to it. Everything above has finished
                // consuming parts — a part inlined-and-dropped is already
                // un-Applied — so this is the final position, not the write
                // loop's optimistic one.
                foreach (PortPartDto part in DistinctParts(items))
                {
                    if (!part.Applied || part.TargetPath == null)
                        continue;
                    string fileId = WzPath.FileId(part.TargetPath);
                    if (fileId.Length == 0)
                        continue;
                    if (!_saveClaims.TryGetValue(fileId, out Dictionary<string, string>? claims))
                        _saveClaims[fileId] = claims = new Dictionary<string, string>(StringComparer.Ordinal);
                    claims[part.TargetPath] = part.Label;
                }
            }

            Truncate(plan);
            return Done(plan, clock, written, failed, needsDecision);
        }
    }

    /// <summary>
    /// Drops any claim against this archive that is no longer in the in-memory
    /// tree — an undone or hand-deleted part is not something the saved file can
    /// be expected to hold. Called by <see cref="WzSaveService"/> before it
    /// serialises, while the tree the claims were made against still exists.
    /// </summary>
    public void PruneSaveClaims(OpenFile file)
    {
        lock (_session.Gate)
        {
            if (!_saveClaims.TryGetValue(file.Id, out Dictionary<string, string>? claims))
                return;

            foreach (string path in claims.Keys.ToList())
            {
                if (_session.TryResolve(path) == null)
                    claims.Remove(path);
            }
            if (claims.Count == 0)
                _saveClaims.Remove(file.Id);
        }
    }

    /// <summary>
    /// Holds the saved archive to what the last applies reported written into it.
    ///
    /// Called by <see cref="WzSaveService"/> AFTER the save has swapped the new
    /// file in and reopened it, so every resolution here reads the saved bytes —
    /// the same rule the composition build follows ("on the file, never on the
    /// tree that wrote it"). The interactive apply used to verify only against
    /// the in-memory tree, and nothing re-reconciled after the save: a result
    /// could say "written 5, failed 0" about parts absent from the saved file,
    /// and no check anywhere would ever contradict it.
    ///
    /// Returns sentences for the caller to carry into the save's warnings — one
    /// naming anything missing, or one saying how many parts were checked and
    /// found, so "no warning" can never mean "nothing was examined".
    /// </summary>
    public List<string> CheckSavedArchive(OpenFile file)
    {
        List<string> findings = new();
        lock (_session.Gate)
        {
            if (!_saveClaims.TryGetValue(file.Id, out Dictionary<string, string>? claims)
                || claims.Count == 0)
            {
                return findings;
            }

            // One save answers for the claims once; a later save of the same
            // archive is not re-asked about ports it already vouched for.
            _saveClaims.Remove(file.Id);

            List<string> missing = new();
            foreach ((string path, string partLabel) in claims)
            {
                if (_session.TryResolve(path) == null)
                    missing.Add(partLabel + " (" + path + ")");
            }

            if (missing.Count > 0)
            {
                missing.Sort(StringComparer.Ordinal);
                findings.Add(
                    $"Checked against the saved and reopened archive: {missing.Count:N0} of the "
                    + $"{claims.Count:N0} parts the last port reported written are NOT in the saved file — "
                    + string.Join("; ", missing.Take(5))
                    + (missing.Count > 5 ? $" and {missing.Count - 5} more" : "")
                    + ". The port's result overstated what this archive now holds. Keep the backup and "
                    + "re-run the port before shipping this file.");
            }
            else
            {
                findings.Add(
                    $"Checked against the saved and reopened archive: all {claims.Count:N0} "
                    + $"{(claims.Count == 1 ? "part" : "parts")} the last port reported written "
                    + $"{(claims.Count == 1 ? "is" : "are")} present in the saved file.");
            }
        }
        return findings;
    }

    /// <summary>
    /// Who in the TARGET draws out of the entries this port is about to replace,
    /// and would stop being able to.
    ///
    /// Every link check here looked OUTWARD — does the entry being copied reach
    /// for something that will not be there. Nothing looked inward, and inward is
    /// where the damage lands: an <c>_inlink</c> is image-root-relative, so a
    /// skill sharing a book with the one being overwritten borrows its frames by
    /// path, and replacing that entry with a differently-shaped node from another
    /// build orphans every one of them.
    ///
    /// Measured, porting Shadower 4221014 into a v232 client: the target's own
    /// 4221016 carries 88 inlinks into 4221014, of which 5 still resolved
    /// afterwards and 83 did not — a whole-archive audit of Skill.wz going from
    /// 0 dead inlinks to 83, all on one skill nobody had asked the port to touch.
    /// The apply said written=5, failed=0, and named 4221016 nowhere. Given a
    /// canvas this client cannot resolve is what takes the skill window down,
    /// that is the same failure as an unreachable <c>_outlink</c>, reached from
    /// the other side.
    ///
    /// It reports rather than repairs. Re-pointing a neighbour's frames at
    /// whatever the new build put at the same path is the "wrong picture rather
    /// than a missing one" trade this service refuses everywhere else, and
    /// inlining the target's old pixels into it would silently fork art that the
    /// two skills were deliberately sharing.
    /// </summary>
    /// <returns>
    /// Per orphaned neighbour: the book-relative name to show, how many of its
    /// canvases break, and <c>Key</c> — the same node as a session path, so the
    /// caller can tell whether this run is replacing that neighbour too.
    /// </returns>
    private List<(string Dependant, int Links, string Key)> OrphanedByOverwrite(PortPartDto part)
    {
        List<(string, int, string)> found = new();
        if (part.TargetPath == null || part.SourcePath == null)
            return found;

        WzObject? replacing = _session.TryResolve(part.TargetPath);
        WzObject? replacement = _session.TryResolve(part.SourcePath);
        WzImage? book = OwningImage(replacing);

        // Only a property inside a shared image can be inlinked into: an entry
        // that IS the image takes its whole subtree with it, and nothing outside
        // can name a path inside it this way.
        if (replacing is not WzImageProperty || replacement == null || book == null)
            return found;

        try { WzSessionService.EnsureParsed(book); }
        catch { return found; }

        string mine = InsideImage(replacing);
        if (mine.Length == 0)
            return found;
        int depth = mine.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

        Dictionary<string, int> broken = new(StringComparer.OrdinalIgnoreCase);
        int walked = 0;

        // MaxLinkWalk alone never made this walk safe. It bounds how many NODES are
        // visited, and a cycle spends one frame per node -- so it permitted 40,000
        // frames where 16,099 were enough to kill the process. The guard bounds
        // DEPTH, which is the quantity the stack cares about, and declines to walk
        // into a UOL at all. That second part is a correctness fix here as much as
        // a safety one: a node reached through a link has its own real parents, so
        // InsideImage below would have reported a foreign canvas's path as if it
        // lived in this book.
        WzWalk guard = new();

        void Walk(WzPropertyCollection? properties, int depth)
        {
            if (properties == null || walked > MaxLinkWalk)
                return;
            foreach (WzImageProperty property in properties)
            {
                if (++walked > MaxLinkWalk)
                    return;

                if (property is WzCanvasProperty
                    && property[WzCanvasProperty.InlinkPropertyName] is WzStringProperty inlink
                    && inlink.Value?.StartsWith(mine + "/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Whether it survives is decided by the node that is landing,
                    // not by the one going away: the path has to exist inside the
                    // replacement too, or the borrower is left pointing at nothing.
                    string rest = inlink.Value[(mine.Length + 1)..];
                    if (Descend(replacement, rest) == null)
                    {
                        string holder = InsideImage(property);
                        string owner = string.Join("/", holder
                            .Split('/', StringSplitOptions.RemoveEmptyEntries)
                            .Take(depth));

                        // Its own frames go with it; only a neighbour is a finding.
                        if (!owner.Equals(mine, StringComparison.OrdinalIgnoreCase))
                            broken[owner] = broken.GetValueOrDefault(owner) + 1;
                    }
                }
                Walk(guard.Into(property, depth), depth + 1);
            }
        }
        Walk(book.WzProperties, 0);

        // The book as a session path, so the neighbour can be named the same way
        // a part names its target. `mine` is the path of the replaced node below
        // its image, so dropping it off the end of the part's target path leaves
        // exactly the image the neighbour also lives in — archive included, which
        // matters because two archives of one family can both hold a 422.img.
        string[] target = WzPath.Split(part.TargetPath);
        string[] bookPath = target.Length > depth ? target[..^depth] : Array.Empty<string>();

        foreach ((string owner, int links) in broken.OrderByDescending(p => p.Value))
        {
            string key = bookPath.Length == 0
                ? ""
                : WzPath.Join(bookPath
                    .Concat(owner.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray());
            found.Add(($"{book.Name}/{owner}", links, key));
        }
        return found;
    }

    /// <summary>The path of a node below the image it lives in — "skill/4221014".</summary>
    private static string InsideImage(WzObject? node)
    {
        string path = "";
        for (WzObject? at = node; at is WzImageProperty inside; at = inside.Parent)
            path = path.Length == 0 ? inside.Name : inside.Name + "/" + path;
        return path;
    }

    /// <summary>
    /// One node reached from another by a slash path, following nothing.
    ///
    /// Deliberately not WzImage.GetFromPath, which only starts at an image, and
    /// deliberately not resolving links: the question here is whether a literal
    /// path exists inside a node, which is exactly what an <c>_inlink</c> asks.
    /// </summary>
    private static WzObject? Descend(WzObject? node, string path)
    {
        foreach (string step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                WzImage image => image[step],
                WzImageProperty property => property[step],
                _ => null,
            };
            if (node == null)
                return null;
        }
        return node;
    }

    /// <summary>
    /// The refusal, in the words a person can act on.
    ///
    /// It has one job beyond saying no: name the FILES. "Open the client folder"
    /// is advice nobody can follow at four in the morning, and it is also the
    /// wrong instruction half the time — a target opened properly as a folder can
    /// still be missing the sibling that holds the icon book, because
    /// Skill/40000.img is not in Skill.wz at all, it is at the root of
    /// Skill003.wz. So each unreachable image is asked of the source: where the
    /// source can see it, the exact archive is named, and where it cannot, the
    /// family is named with its numbered siblings spelled out.
    /// </summary>
    private string DeadCanvasLinkRefusal(
        List<SplitArtUse> unreachable, ClientGroup source, ClientGroup? targetClient, OpenFile target)
    {
        int links = unreachable.Sum(u => u.Links);
        SortedSet<string> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (SplitArtUse use in unreachable)
            foreach (string name in use.Entries)
                entries.Add(name);

        // Named per side, because the fix is different. Art the source can see
        // travels, so the only thing missing is somewhere on the target for it to
        // be found again; art the source cannot see never leaves home.
        SortedSet<string> openOnSource = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> openOnTarget = new(StringComparer.OrdinalIgnoreCase);

        foreach (SplitArtUse use in unreachable)
        {
            EntryLocation? here = ResolveImage(source, use.Image);
            if (here != null)
            {
                // The exact file, which is the whole point of naming it: this is
                // where the source keeps it, and the target's copy of that same
                // archive is the one that has to be open and writable.
                openOnTarget.Add(here.File.Name);
                continue;
            }

            string family = ArchiveFamilyOf(use.Image);
            if (family.Length == 0)
                continue;

            // Not open anywhere, so which numbered sibling holds it cannot be
            // known -- and that is exactly why they are all named. Skill001.wz
            // answers to "Skill" the same way Skill.wz does.
            openOnSource.Add($"{family}.wz");
            openOnSource.Add($"{family}001.wz");
            openOnSource.Add($"{family}002.wz");
            openOnSource.Add($"{family}003.wz");
        }

        string what =
            $"{links:N0} canvas links across {entries.Count:N0} of these entries draw their frames through a "
            + $"'_Canvas' directory, and {target.Name}'s client keeps its art inline: it follows an "
            + "'_outlink' one level and no further. 'Skill/40000.img/skill/400004114/icon' is ordinary and "
            + $"'{unreachable[0].Example}' is a canvas it cannot reach at all. A blank icon is survivable; "
            + "this is not — the window that tries to draw one takes the client down with it. Measured: the "
            + "same port left 0 of these with the whole target family open and 30 with only Skill.wz and "
            + "String.wz, all of them on one skill. Nothing was written.";

        List<string> fix = new();
        if (targetClient == null)
        {
            fix.Add(
                $"Open {target.Name} through its client FOLDER rather than as a single file — opened on its "
                + "own it belongs to no client here, so the art is copied across and then cannot be found "
                + "again on the far side, whatever else is open.");
        }
        if (openOnSource.Count > 0)
        {
            fix.Add(
                $"Open {string.Join(" and ", openOnSource)} from {source.Label} (whichever of the numbered "
                + "siblings exist) — the art these links name is in none of the archives open there now, so "
                + "there is nothing to carry across.");
        }
        if (openOnTarget.Count > 0)
        {
            fix.Add(
                $"Open the target client's own {string.Join(" and ", openOnTarget)}, unlocked — the source "
                + "has the art and it will travel, but it has to land in an archive of that family on the "
                + "target and be findable there afterwards.");
        }
        if (fix.Count == 0)
        {
            fix.Add(
                "Open every archive of that family on BOTH clients — Skill.wz and Skill001.wz and "
                + "Skill002.wz and Skill003.wz, not just the one you picked.");
        }

        return what + " " + string.Join(" ", fix);
    }

    private static PlanRun Done(
        PortPlanDto plan, Stopwatch clock, int written, int failed, bool needsDecision)
    {
        plan.Seconds = clock.Elapsed.TotalSeconds;
        return new PlanRun(plan, written, failed, plan.Seconds, needsDecision);
    }

    /// <summary>
    /// Cuts the listed entries down to a sample once the totals have been taken
    /// off the full list.
    ///
    /// Done last, and never before the totals: a preview that counted only what
    /// it listed would understate a whole-archive port by an order of magnitude,
    /// which is precisely the number the user is relying on.
    /// </summary>
    private static void Truncate(PortPlanDto plan)
    {
        if (plan.Items.Count <= MaxListedItems)
            return;

        plan.ItemsTruncated = plan.Items.Count - MaxListedItems;
        plan.Items = plan.Items.Take(MaxListedItems).ToList();
    }

    /// <summary>
    /// Every part in the plan, each node move once.
    ///
    /// A container is <em>shared</em> on purpose: <see cref="ContainerParts"/>
    /// hands the same <see cref="PortPartDto"/> object to every entry that needs
    /// it, so that 583 consumables landing in one new 0200.img read as "creates
    /// 0200.img" once rather than 583 times. Every counter and the write loop then
    /// iterated items x parts, which visits that one object 583 times — which is
    /// why a measured port reported "13 nodes written" two lines above an undo
    /// entry reading "(9 changes)", and another "87" against "(76 changes)". The
    /// undo log was the honest number both times.
    ///
    /// Keyed on the target path rather than on object identity, because the target
    /// path is what a write actually touches: two distinct parts aiming at one
    /// path would be one node move as well, and counting those twice would be the
    /// same lie told a different way. A part with no target path cannot collide
    /// with anything — an Absent or Blocked satellite has nowhere to go and never
    /// writes — so every one of those is kept.
    /// </summary>
    private static IEnumerable<PortPartDto> DistinctParts(List<PortItemDto> items)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (PortItemDto item in items)
        {
            foreach (PortPartDto part in item.Parts)
            {
                if (part.TargetPath == null || seen.Add(part.TargetPath))
                    yield return part;
            }
        }
    }

    private static PortTotalsDto Total(List<PortItemDto> items, List<EntryLocation> entries)
    {
        PortTotalsDto totals = new()
        {
            Entries = items.Count,
            Bytes = entries.Sum(e => e.Bytes),
        };

        foreach (PortPartDto part in DistinctParts(items))
        {
            totals.Parts++;
            switch (part.Status)
            {
                case "New": totals.New++; break;
                case "Conflict": totals.Conflicts++; break;
                case "Same": totals.Identical++; break;
                case "Absent": totals.Absent++; break;
                default: totals.Blocked++; break;
            }
            if (part.WillWrite)
                totals.WillWrite++;

            // Content this port moves that is NOT one of the entries. A map's
            // scenery is the case: the entries add up to 51 KB and the pictures
            // they name add up to 26 MB, and a size the user is shown that leaves
            // that out is not the size of the port.
            if (part.WillWrite)
                totals.Bytes += part.Bytes;
        }

        // Counted per item and not per part, because "the target already has one
        // of these" is a fact about the entry rather than about a node move — an
        // entry whose only shared part another entry already accounted for still
        // has its own conflicting entry part, and must still be counted here.
        foreach (PortItemDto item in items)
        {
            if (item.Parts.Any(p => p.Status == "Conflict" && p.Kind is "entry" or "link-entry"))
                totals.EntriesAlreadyThere++;
        }
        return totals;
    }

    /// <summary>
    /// Names some of the entries the target already has.
    ///
    /// "4,062 conflicts" is a number, not information. A hundred of them with
    /// their names and what the target's copy currently holds is the difference
    /// between a count somebody accepts and a count somebody can judge.
    /// </summary>
    private static void SampleConflicts(PortPlanDto plan, List<PortItemDto> items)
    {
        foreach (PortItemDto item in items)
        {
            if (plan.ConflictSample.Count >= MaxConflictSample)
                return;

            PortPartDto? conflict = item.Parts.FirstOrDefault(
                p => p.Status == "Conflict" && p.Kind is "entry" or "link-entry");
            if (conflict == null)
                continue;

            plan.ConflictSample.Add(new PortConflictDto
            {
                Id = item.Id,
                Name = item.Name,
                TargetPath = conflict.TargetPath ?? "",
                Existing = conflict.Existing,
            });
        }
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    #endregion

    #region Parts

    /// <summary>
    /// The parts that create the directories and container images an entry needs
    /// before it can be inserted.
    ///
    /// The rule is a mirror, not a derivation: whatever the source client filed
    /// this under — <c>Cash/0510.img</c>, <c>Cap</c>, nothing at all — the target
    /// gets the same, with each missing level created as the same kind of node
    /// the source has there. That is what makes one code path serve an image at
    /// an archive root, an image in a category directory and a property inside a
    /// shared image, and it is also what keeps the answer traceable: the source
    /// client already decided, correctly, and the plan can say so.
    /// </summary>
    private List<PortPartDto> ContainerParts(
        EntryLocation entry, OpenFile target,
        Dictionary<string, PortPartDto> shared, Dictionary<PortPartDto, PathStep> steps)
    {
        List<PortPartDto> parts = new();
        string cursor = target.Id;

        foreach (PathStep step in entry.Steps)
        {
            string path = WzPath.Child(cursor, step.Name);
            cursor = path;

            if (_session.TryResolve(path) != null)
                continue;   // already there, nothing to say

            parts.Add(Container(
                path, step,
                $"{target.Name} · {step.Name} ({step.Type.ToLowerInvariant()})",
                $"The target has no {step.Name} here, so the port creates it. " +
                $"{entry.File.Name} keeps this {(step.Type == "Directory" ? "folder" : "image")} " +
                "at the same place, so the copy is filed where that client files it.",
                shared, steps));
        }
        return parts;
    }

    /// <summary>
    /// One container part, made once per target path and handed out thereafter.
    ///
    /// Shared across the whole plan: 583 consumables land in one 0200.img, and the
    /// preview must say "creates 0200.img" once. Listing it 583 times is the same
    /// information rendered as noise.
    ///
    /// That sharing is why nothing may count or write a plan by walking items x
    /// parts — see <see cref="DistinctParts"/>, which exists because something
    /// did.
    /// </summary>
    private static PortPartDto Container(
        string path, PathStep step, string label, string reason,
        Dictionary<string, PortPartDto> shared, Dictionary<PortPartDto, PathStep> steps)
    {
        if (shared.TryGetValue(path, out PortPartDto? existing))
            return existing;

        PortPartDto part = new()
        {
            Kind = "container",
            Label = label,
            SourcePath = null,
            TargetPath = path,
            Status = "New",
            Reason = reason,
        };
        shared[path] = part;
        steps[part] = step;
        return part;
    }

    /// <summary>
    /// The part that carries the entry itself.
    ///
    /// The conflict test is by <em>id</em> and not by filename, because the two
    /// come apart: a v232 Sound.wz spells item 02000000 exactly that way while
    /// its String.wz spells the same item 2000000, and a v232 Mob.wz pads
    /// six-digit mob ids to seven and leaves eight-digit boss ids alone. A target
    /// holding the same id under a different spelling is refused rather than
    /// written beside — two nodes for one id is a state where which one the
    /// client reads is not something this can predict.
    /// </summary>
    private PortPartDto EntryPart(
        PortKindSpec spec, EntryLocation entry, OpenFile target,
        EntryIndex targetIndex, bool requested)
    {
        string targetPath = WzPath.Child(
            entry.Steps.Aggregate(target.Id, (path, step) => WzPath.Child(path, step.Name)), entry.Name);

        PortPartDto part = new()
        {
            Kind = requested ? "entry" : "link-entry",
            Label = $"{target.Name} · {entry.Relative}",
            SourceArchive = WzSessionService.StripArchiveSuffix(target.Name),
            SourcePath = entry.Path,
            TargetPath = targetPath,
        };

        // An entry with no id is addressed by path, never by id.
        //
        // `_Canvas` images have names the id parser cannot read, so they come
        // through as id 0 — and so does anything else unparseable. Looking one
        // up by id therefore matched the FIRST id-0 node in the target, which
        // is some unrelated image entirely. Measured: porting Cap
        // `01000119.img` blocked its own artwork against
        // `Afterimage/bow.img/0`, saying "the target already has item 0", and
        // the hat landed with an empty `_Canvas` beside it and rendered
        // nothing.
        //
        // The closure already keys these by path for exactly this reason (see
        // `Key` in BuildPlan); this is the same rule applied to the conflict
        // check, which had been left behind.
        if (entry.Id <= 0)
            return PulledInPart(part, entry, targetPath);

        EntryLocation? existing = targetIndex.Get(entry.Scope, entry.Id);

        if (existing is null)
        {
            part.Status = "New";
            return part;
        }

        if (!string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            part.Status = "Blocked";
            part.Existing = existing.Path;
            part.Reason =
                $"The target already has {spec.Label.ToLowerInvariant()} {entry.Id} as '{existing.Relative}', " +
                $"and the source spells it '{entry.Relative}'. Copying it in would leave two nodes for one id " +
                "and no way to say which the client reads. Rename or delete the target's copy first.";
            return part;
        }

        part.Status = "Conflict";
        part.TargetPath = existing.Path;
        part.Existing = Describe(existing);
        part.Reason = "The target already has this one. Nothing is replaced unless you ask for it.";
        return part;
    }

    /// <summary>
    /// An image the closure pulled in — art an entry outlinks into — judged
    /// against the target BY PATH, because that is the only address it has.
    ///
    /// <see cref="ResolveImage"/> hands these back with a synthetic <c>Id = 0</c>
    /// so they cannot be confused with entries of the kind being ported, and the
    /// conflict check used to look for an id-0 node in the target index. That can
    /// never match: the target's own <c>Character.wz/Face/00020000.img</c> is
    /// indexed under its real id, 20000, and <see cref="EntryIndex"/> is keyed by
    /// scope and id, so at most one id-0 row exists in it at all. Every pulled-in
    /// image therefore previewed as <b>New</b>.
    ///
    /// What that cost is measured: 113 of 120 sampled v233 faces draw from
    /// <c>Face/00020000.img</c>. With Replace on, porting one face silently
    /// replaced the base face every one of them reads — no conflict row, no
    /// warning, reported as New. With Replace off the copy landed at
    /// <c>00020000 copy.img</c>, a name no client reads, and the port reported a
    /// failure for something it had already half done.
    ///
    /// The rule this applies instead is the same one map porting arrived at for
    /// shared scenery: <b>present, and the same picture — leave it alone;
    /// absent — copy it; present and different — say so and let the user
    /// decide.</b> There is no branch here that replaces silently.
    /// </summary>
    private PortPartDto PulledInPart(PortPartDto part, EntryLocation entry, string targetPath)
    {
        WzObject? existing = _session.TryResolve(targetPath);
        if (existing == null)
        {
            part.Status = "New";
            return part;
        }

        // Compared as a picture rather than as a name. Two builds of one art
        // image differ far more often than the frames an entry actually draws
        // do, but at this granularity the whole image IS what is being reused --
        // and because the answer authorises reuse, a truncated digest is not
        // allowed to be the last word. See SameTreeForReuse.
        if (SameTreeForReuse(entry.Node, existing))
        {
            part.Status = "Same";
            part.Existing = Describe(existing);
            part.Reason =
                "The target already has this image at the same path and it holds the same picture, so " +
                "nothing is copied and the copy draws from the target's own.";
            return part;
        }

        part.Status = "Conflict";
        part.Existing = Describe(existing);
        part.Reason =
            "The target already has this image at the same path and what is in it is different. This is " +
            "art that other entries in that client draw from — replacing it changes them too, and none of " +
            "them are part of this port. Left alone unless you ask for it, in which case the copy that " +
            "lands is the source's picture for everything that reads it.";
        return part;
    }

    /// <summary>
    /// The part that carries one satellite — a String.wz or Sound.wz entry.
    ///
    /// Both are a node keyed by id inside a shared image, and the target path is
    /// mirrored from wherever the source client filed it, category levels
    /// included: <c>String.wz/Eqp.img/Eqp/Cap/1002357</c> arrives at the same
    /// path, with <c>Eqp</c> and <c>Cap</c> created if the target lacks them. The
    /// alternative — working the category out from the id — is how a name ends up
    /// somewhere the client never looks, with nothing on screen to show for it.
    /// </summary>
    /// <summary>
    /// One satellite, with the category levels that have to exist before it can
    /// land.
    ///
    /// The levels are parts of their own rather than something the write does on
    /// the side. They used to be neither: the reason line said "the target's
    /// Eqp.img has no Eqp/Cap, so the port creates it" and nothing anywhere
    /// created it, so <see cref="Write"/> asked <c>Transfer</c> for a parent that
    /// did not exist and every equip in a slot the target has never carried came
    /// back "'Cap' not found under 'f4/Eqp.img/Eqp'" — the item copied, the name
    /// it shows in game did not, and the plan had said it would.
    ///
    /// Making them parts rather than a silent step inside <see cref="Write"/> is
    /// the same rule the entry's own containers follow: the preview names every
    /// node the apply will create, the counts cover them, and one undo takes them
    /// back out. They go through the plan-wide <paramref name="containers"/> map,
    /// so a slot shared by 200 equips is one part and one write.
    /// </summary>
    private List<PortPartDto> SatelliteParts(
        PortSatelliteSpec satellite, ClientGroup source, ClientGroup? targetClient, int id,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers, Dictionary<PortPartDto, PathStep> containerSteps,
        Dictionary<string, PortPartDto> rows)
    {
        List<PortPartDto> made = new();
        PortPartDto part = SatellitePart(
            satellite, source, targetClient, id, renames, containers, containerSteps, rows, made);
        made.Add(part);
        return made;
    }

    private PortPartDto SatellitePart(
        PortSatelliteSpec satellite, ClientGroup source, ClientGroup? targetClient, int id,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers, Dictionary<PortPartDto, PathStep> containerSteps,
        Dictionary<string, PortPartDto> rows,
        List<PortPartDto> made)
    {
        string archive = Capitalise(satellite.Role) + ".wz";
        PortPartDto part = new()
        {
            Kind = satellite.Kind,
            Label = $"{archive} · {id}",
            SourceArchive = Capitalise(satellite.Role),
        };

        (WzImageProperty Entry, string Path, string Image, IReadOnlyList<PathStep> Steps)? found =
            FindSatellite(WithRole(source, satellite.Role), satellite, id);

        if (found == null)
        {
            part.Label = $"{archive} · {string.Join(" / ", satellite.Images)}/{id}";
            part.Status = "Absent";

            // Two different absences, and telling them apart is the difference
            // between "this id has no sound" and "this client does not keep them
            // there at all". Measured on a v232 client: its Sound.wz holds Mob.img,
            // MobVoice.img, Skill.img, SkillVoice.img and Reactor.img and has no
            // Npc.img whatever, so every NPC ported from it reported "no Npc.img
            // entry for <id>" and sent people looking for a row in an image that
            // does not exist.
            List<OpenFile> archives = WithRole(source, satellite.Role);
            List<string> missing = satellite.Images
                .Where(image => FindImage(archives, image) == null)
                .ToList();

            part.Reason = missing.Count == satellite.Images.Length
                ? $"{source.Label} has no {archive}/{string.Join(" or ", satellite.Images)} open at all, so " +
                  $"there is nothing here to carry — this client may not keep them there. " +
                  (satellite.AbsentNote == null ? "" : satellite.AbsentNote + " ") +
                  "If it should have one, open that archive and preview again."
                : $"{source.Label} has no {string.Join(" or ", satellite.Images)} entry for {id}" +
                  (satellite.AbsentNote == null ? "." : ". " + satellite.AbsentNote);
            return part;
        }

        (WzImageProperty entry, string entryPath, string image, IReadOnlyList<PathStep> steps) = found.Value;
        part.SourcePath = entryPath;
        part.Label = $"{archive} · {image}/{entry.Name}";

        if (targetClient == null)
        {
            part.Status = "Blocked";
            part.Reason = "The target archive is not part of a client folder this can read.";
            return part;
        }

        List<OpenFile> targetArchives = WithRole(targetClient, satellite.Role);

        // The whole list, not the first hit, because this is a WRITE DESTINATION.
        // See FindImages: the candidates are every archive of the role holding an
        // image of that name, in an order that does not move between sessions.
        List<(WzImage Image, string Path, OpenFile File)> candidates =
            FindImages(targetArchives, image);
        (WzImage Image, string Path)? destination =
            candidates.Count == 0 ? null : (candidates[0].Image, candidates[0].Path);

        // Disclosed, because a choice was made. More than one archive of this
        // role holds an image by this name, so the row could have landed in
        // either. The port takes the first by the deterministic order FindImages
        // defines rather than by session order, and it says which one and out
        // of what. A silent pick writes a row into a sibling archive nobody
        // looked at, and the only symptom is a duplicate name across a family
        // that the client resolves by whichever it reads first.
        string? amongOthers = candidates.Count <= 1
            ? null
            : $"{targetClient.Label} has {image} in {candidates.Count:N0} archives of its {archive} "
              + $"family - {string.Join(", ", candidates.Select(x => x.File.Name))} - and this lands "
              + $"in {candidates[0].File.Name}, the first by name. Nothing writes a second copy of "
              + "that name into a sibling. If the row belongs in one of the others, move it there "
              + "afterwards.";
        if (destination == null)
        {
            // Two different absences, and only one of them is the user's to fix.
            //
            // This branch used to say "open that client's Sound.wz" for both,
            // which is unactionable in the common case: on a measured plan 47 of
            // 276 parts carried it while the target's Sound.wz was open and
            // simply had no Mob.img inside — the client does not keep mob sounds
            // there. Being told to do the thing you have already done reads as
            // the editor being broken.
            //
            // targetArchives separates them exactly and is already in hand: empty
            // means nothing of this role is open at all, non-empty means the
            // archive is open and does not contain the image. This is the same
            // distinction the source side of this method already draws above, and
            // the one the dependency-edge branch draws; only the target side was
            // missing it.
            part.Status = "Blocked";
            part.Reason = With(amongOthers, targetArchives.Count == 0
                ? $"{targetClient.Label} has no {archive} open, so there is nowhere to put this. " +
                  $"Open that client's {archive} and preview again."
                : $"{targetClient.Label}'s {archive} is open but has no {image} in it, so there is " +
                  $"nowhere to put this — this client may not keep them there. Add {image} to that " +
                  $"archive, or open the {archive} that has one, and preview again.");
            return part;
        }

        // The category levels below the image, mirrored. Missing ones become
        // container parts of their own — see SatelliteParts for what it cost when
        // they were only named in the reason — and are named in the reason too, so
        // a new category in the target's Eqp.img is something the user saw coming.
        string targetParent = destination.Value.Path;
        List<string> creates = new();
        List<(string Path, PathStep Step)> toCreate = new();
        foreach (PathStep step in steps)
        {
            targetParent = WzPath.Child(targetParent, step.Name);
            if (_session.TryResolve(targetParent) == null)
            {
                creates.Add(step.Name);
                toCreate.Add((targetParent, step));
            }
        }

        // A row keyed by a child value has no meaningful name to mirror -- the
        // target's "4211" is some other item's listing. It is matched by the id
        // inside it, and a new one is appended under a name nothing else uses.
        if (satellite.MatchField != null)
        {
            return ShopPart(part, satellite, entry, destination.Value, targetArchives, id, renames, rows);
        }

        WzImageProperty? existing = _session.TryResolve(
            WzPath.Child(targetParent, entry.Name ?? "")) as WzImageProperty;

        // Not only the mirrored path: the target may file the same id under a
        // different category. Writing beside it would leave two entries for one
        // id, and the client reads whichever it finds first.
        (WzImageProperty Entry, string Path, string Image, IReadOnlyList<PathStep> Steps)? elsewhere =
            existing == null ? FindSatellite(targetArchives, satellite, id) : null;

        if (existing == null && elsewhere != null)
        {
            part.Status = "Blocked";
            part.Existing = elsewhere.Value.Path;
            part.Reason = With(amongOthers,
                $"The target already names {id} at {elsewhere.Value.Path}, which is not where {source.Label} " +
                "files it. Copying would leave two entries for one id. Move or delete the target's entry " +
                "first, or edit it by hand.");
            return part;
        }

        part.TargetPath = WzPath.Child(targetParent, entry.Name ?? "");

        if (existing == null)
        {
            part.Status = "New";
            part.Reason = With(amongOthers, creates.Count == 0
                ? null
                : $"The target's {image} has no {string.Join("/", creates)}, so the port creates it — that is " +
                  $"where {source.Label} files this id.");

            // Only on the branch that actually writes. A Blocked or Same
            // satellite needs no category, and creating one for it would leave an
            // empty Eqp/Cap in someone's String.wz that nothing ever files under.
            foreach ((string path, PathStep step) in toCreate)
            {
                made.Add(Container(
                    path, step,
                    $"{archive} · {image}/{step.Name}",
                    $"The target's {image} has no {step.Name}, so the port creates it. " +
                    $"{source.Label} files this id under it, and a name written anywhere else is one the " +
                    "client never looks for.",
                    containers, containerSteps));
            }

            // One row, one part, however many entries in this plan need it.
            //
            // The same Sound.wz row is reachable two ways -- as the satellite of
            // the entry being ported, and as a sibling another entry's row links
            // at (see CarrySiblings) -- and two part objects aiming at one path
            // is a part that reports "will write" and never reports Applied,
            // because only one of them survives DistinctParts. Shared the way a
            // container is shared, for the same reason.
            if (rows.TryGetValue(part.TargetPath, out PortPartDto? shared))
                return shared;

            rows[part.TargetPath] = part;
            CarrySiblings(part, entry, entryPath, destination.Value, archive, image, rows, made);
            return part;
        }

        // A derived-key dependency is a definition tree, not a label row. A
        // SetItemInfo entry keeps its member list and bonuses several levels
        // down; the ordinary shallow satellite digest can say two different
        // sets are equal when their top-level child counts happen to match.
        // Compare the complete bounded tree for these dependencies. A bound hit
        // is a conflict, never permission to reuse the target's row.
        bool same;
        bool comparisonIncomplete = false;
        if (satellite.KeyPath != null)
        {
            string sourceDigest = TreeDigest(entry, out bool sourceIncomplete);
            string targetDigest = TreeDigest(existing, out bool targetIncomplete);
            comparisonIncomplete = sourceIncomplete || targetIncomplete;
            same = !comparisonIncomplete
                   && string.Equals(sourceDigest, targetDigest, StringComparison.Ordinal);
        }
        else
        {
            same = string.Equals(Digest(entry), Digest(existing), StringComparison.Ordinal);
        }

        if (same)
        {
            part.Status = "Same";
            part.Existing = Summarise(existing);
            part.Reason = With(amongOthers, satellite.Kind == "set-info"
                ? "The target already holds the same complete member list and bonus definition for this set id."
                : satellite.Role == "sound"
                    ? "The target already holds sounds with the same names, durations and formats, so there is " +
                      "nothing worth copying. The audio itself was not compared -- decompressing both sides per " +
                      "row would cost more than the whole preview."
                    : "The target already holds an entry with the same fields and the same text, so there is " +
                      "nothing to copy.");
            return part;
        }

        part.Status = "Conflict";
        part.Existing = Summarise(existing);
        part.Reason = With(amongOthers, satellite.Kind == "set-info"
            ? $"The target already uses set id {id} for different member or bonus data. Nothing is replaced "
              + "unless you tick Replace. Replacing it changes the set window and bonuses for every target "
              + "item that already points at this set id."
              + (comparisonIncomplete
                  ? " The complete definition exceeded the comparison budget, so it is treated as different "
                    + "rather than guessed identical."
                  : "")
            : "The target already has an entry for this id and it is different. "
              + "Nothing is replaced unless you ask for it."
              + (DescribeLinks(entry) is { } links ? " " + links : ""));
        CarrySiblings(part, entry, entryPath, destination.Value, archive, image, rows, made);
        return part;
    }

    /// <summary>
    /// How many sibling rows one satellite row's links may drag in before the
    /// chain is called a chain and reported instead of followed. Sound rows link
    /// one hop in practice; this is the bound that keeps "in practice" from being
    /// an assumption.
    /// </summary>
    private const int MaxSatelliteSiblings = 8;

    /// <summary>
    /// Follows a satellite row's links to its SIBLING rows, and either brings
    /// them or says the copy will be silent.
    ///
    /// A Sound.wz row is very often not a clip. It is a UOL at another row in the
    /// same image -- measured on a v233 client: <c>Skill.img</c> 28%,
    /// <c>Item.img</c> 75%, <c>Reactor.img</c> 78% -- and a UOL is
    /// parent-relative, so <c>../10001004/Use</c> names the row beside it.
    /// Nothing resolved those against the target: <see cref="Digest"/> returns a
    /// UOL's text and deliberately never walks it, so a row was compared and
    /// copied as text and what it pointed at was never asked about. Of the 111
    /// <c>Skill.img</c> rows a v233 client has and a v232 client lacks, 14 point
    /// at a sibling the target also lacks -- carried faithfully, they land as
    /// links into nothing. A silent skill, and the port reported it clean.
    ///
    /// Three outcomes, and none of them is silence: the target has the sibling --
    /// nothing to do; the source has it and the target does not -- it is carried,
    /// as ONE shared part however many rows name it; neither has it -- the link
    /// is dead in the source too, and the part says so in the words of what the
    /// player will notice.
    ///
    /// Confined to rows sitting directly in the image, which is all a link at
    /// <c>../name</c> can address. Anything reaching further out is named and not
    /// followed, rather than guessed at from a link's text.
    /// </summary>
    private static void CarrySiblings(
        PortPartDto part, WzImageProperty entry, string entryPath,
        (WzImage Image, string Path) destination, string archive, string image,
        Dictionary<string, PortPartDto> rows, List<PortPartDto> made)
    {
        if (entry.Parent is not WzImage sourceImage)
            return;

        string sourceImagePath = WzPath.Parent(entryPath) ?? entryPath;
        List<string> silent = new();
        List<string> outside = new();
        HashSet<string> handled = new(StringComparer.OrdinalIgnoreCase) { entry.Name ?? "" };
        bool capped = false;

        Queue<(WzImageProperty Row, string Path)> pending = new();
        pending.Enqueue((entry, entryPath));

        while (pending.Count > 0)
        {
            (WzImageProperty row, string rowPath) = pending.Dequeue();
            if (row.WzProperties == null)
                continue;

            foreach (WzUOLProperty uol in row.WzProperties.OfType<WzUOLProperty>())
            {
                // The TEXT, never WzValue: a UOL resolves on read, which both
                // loses the path and quietly succeeds on a link that is broken in
                // the source as well.
                string[] segments = (uol.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2 || segments[0] != "..")
                    continue;   // inside this row, so it travels with the copy

                if (segments.Skip(1).Any(segment => segment == ".."))
                {
                    outside.Add($"{row.Name}/{uol.Name} -> {uol.Value}");
                    continue;
                }

                string sibling = segments[1];
                if (!handled.Add(sibling))
                    continue;

                if (handled.Count > MaxSatelliteSiblings)
                {
                    capped = true;
                    break;
                }

                // Present in the target is the whole question: the link resolves
                // there, whatever is on the other end of it.
                if (destination.Image.WzProperties?.FindByName(sibling) != null)
                    continue;

                WzImageProperty? inSource = sourceImage.WzProperties?.FindByName(sibling);
                if (inSource == null)
                {
                    silent.Add($"{row.Name}/{uol.Name} -> {uol.Value}");
                    continue;
                }

                string siblingTarget = WzPath.Child(destination.Path, sibling);
                if (!rows.TryGetValue(siblingTarget, out PortPartDto? carried))
                {
                    carried = new PortPartDto
                    {
                        Kind = part.Kind,
                        Label = $"{archive} · {image}/{sibling}",
                        SourceArchive = part.SourceArchive,
                        SourcePath = WzPath.Child(sourceImagePath, sibling),
                        TargetPath = siblingTarget,
                        Status = "New",
                        Reason =
                            $"{image}/{row.Name} is a link at this row rather than a clip of its own, and " +
                            $"the target has no {sibling} for it to land on. Carried too, because a link " +
                            "into nothing is a copy that makes no sound and reports no error.",
                    };
                    rows[siblingTarget] = carried;
                }
                made.Add(carried);
                pending.Enqueue((inSource, WzPath.Child(sourceImagePath, sibling)));
            }

            if (capped)
                break;
        }

        void Add(string sentence) =>
            part.Reason = string.IsNullOrEmpty(part.Reason) ? sentence : part.Reason + " " + sentence;

        if (capped)
        {
            Add($"It links to more than {MaxSatelliteSiblings} sibling rows, so the chain was not followed " +
                "to the end. Treat what came with it as incomplete.");
        }

        if (silent.Count > 0)
        {
            Add($"This row is a link rather than data ({string.Join(", ", silent.Take(4))}) and what it " +
                "points at is in NEITHER client, so the copy resolves to nothing -- in game that is " +
                "silence, with nothing to report it. It is still carried, because the source client reads " +
                "it exactly the same way.");
        }

        if (outside.Count > 0)
        {
            Add($"It also links outside this image ({string.Join(", ", outside.Take(4))}). That text is " +
                "copied as it stands, so whatever it names has to exist in the target client too.");
        }
    }

    /// <summary>
    /// The cash-shop listing part.
    ///
    /// Different from every other satellite in two ways that both matter. Its
    /// rows are named by position, so a new one is appended under a name nothing
    /// else uses rather than mirrored — mirroring "4211" would land on whatever
    /// the target lists at 4211. And its <c>SN</c> is the serial the client
    /// actually purchases by, so the source's SN is carried across unchanged and
    /// a collision with a different item in the target is refused outright.
    ///
    /// Carried rather than invented: the instruction not to silently allocate an
    /// SN is respected by reusing the source's, which is deterministic, traceable
    /// and stated in the result — nothing here picks a number on the user's
    /// behalf. If that number is taken, the port says whose it is and stops.
    /// </summary>
    private PortPartDto ShopPart(
        PortPartDto part, PortSatelliteSpec satellite, WzImageProperty entry,
        (WzImage Image, string Path) destination, List<OpenFile> targetArchives, int id,
        Dictionary<PortPartDto, string> renames, Dictionary<string, PortPartDto> rows)
    {
        WzSessionService.EnsureParsed(destination.Image);

        string? sn = entry.WzProperties?.FindByName("SN")?.WzValue?.ToString();
        part.Label = $"Etc.wz · Commodity.img (SN {sn ?? "?"})";
        part.SourceArchive = "Etc";

        (WzImageProperty Entry, string Path, string Image, IReadOnlyList<PathStep> Steps)? already =
            FindSatellite(targetArchives, satellite, id);

        if (already != null)
        {
            part.TargetPath = already.Value.Path;
            part.Existing = Summarise(already.Value.Entry);
            if (string.Equals(Digest(entry), Digest(already.Value.Entry), StringComparison.Ordinal))
            {
                part.Status = "Same";
                part.Reason = "The target already lists this item in its cash shop on the same terms.";
            }
            else
            {
                part.Status = "Conflict";
                part.Reason = "The target already lists this item in its cash shop on different terms. " +
                              "Nothing is replaced unless you ask for it. Replacing it rewrites the row " +
                              $"the target lists this item at ({already.Value.Entry.Name}), and nothing else.";

                // The name the row has to land under, which is the TARGET's, not
                // the source's.
                //
                // Without this the copy went in under the name the source's own
                // Commodity.img happens to file it at -- a position in a
                // different client's table. Measured: porting 5533138 with
                // Replace on landed the row on Commodity.img/1439, an unrelated
                // equip listing, destroying it; the landing check then threw, so
                // the port reported a failure for a row it had already
                // overwritten, and the row it meant to replace was untouched.
                //
                // See Write(): a part carrying a rename is transferred WITHOUT
                // Transfer's own overwrite, precisely because the source's name
                // is not a name in this archive at all.
                if (already.Value.Entry.Name is { Length: > 0 } row)
                    renames[part] = row;
            }
            return part;
        }

        // The serial has to be free, and free of *this* item rather than merely
        // absent: two rows sharing an SN is a shop where one of the two can never
        // be bought, and nothing in the client reports it.
        if (sn != null)
        {
            foreach (WzImageProperty row in destination.Image.WzProperties)
            {
                string? otherSn = row.WzProperties?.FindByName("SN")?.WzValue?.ToString();
                if (!string.Equals(otherSn, sn, StringComparison.Ordinal))
                    continue;

                string? otherItem = row.WzProperties?.FindByName("ItemId")?.WzValue?.ToString();
                part.Status = "Blocked";
                part.Existing = $"SN {sn} is already item {otherItem}";
                part.Reason =
                    $"The source lists this item under serial {sn}, and the target already sells item " +
                    $"{otherItem} under that serial. Two rows with one SN is a shop where one of them can " +
                    "never be bought. Nothing here will pick a different serial for you — set one in Cash " +
                    "Shop mode, which allocates a free one and shows you which.";
                return part;
            }
        }

        // Appended under a name nothing else uses. Numeric, because that is what
        // every other row in the image is called.
        //
        // "Nothing else uses" now includes the rows this same plan has already
        // handed out. It did not, and the image is not written until apply, so
        // every cash item in one port was given the same number: three pets
        // planned together all landed on Commodity.img/9697, DistinctParts
        // deduplicated them on target path, and two of them disappeared -- while
        // the totals said one row and all three parts rendered "New / will
        // write". An item that exists in the client and is in nobody's shop is
        // exactly the silent loss this whole feature is arranged against.
        int next = 0;
        foreach (WzImageProperty row in destination.Image.WzProperties)
        {
            if (int.TryParse(row.Name, out int index) && index >= next)
                next = index + 1;
        }

        string name = next.ToString(CultureInfo.InvariantCulture);
        while (rows.ContainsKey(WzPath.Child(destination.Path, name)))
            name = (++next).ToString(CultureInfo.InvariantCulture);

        part.Status = "New";
        part.TargetPath = WzPath.Child(destination.Path, name);
        part.Reason =
            $"Listed at row {name} of the target's Commodity.img, keeping the source's serial {sn ?? "?"}. " +
            "Without this row the item exists in the client and cannot be bought.";
        renames[part] = name;
        rows[part.TargetPath] = part;
        return part;
    }

    #endregion

    #region Named references

    /// <summary>
    /// Nodes an entry reaches by NAME, in a namespace both clients write into and
    /// neither owns. See <see cref="PortNamedRef"/> for what that means and what
    /// it was measured on.
    ///
    /// Two rules, and they are the whole design:
    ///
    /// <b>Never replace.</b> A scenery image is used by dozens of maps the user
    /// did not ask about, so overwriting the target's <c>Obj/acc1.img</c> with a
    /// newer build's is a change to every one of them. This method has no branch
    /// that can do it, whatever the caller passes for Overwrite — the worst it
    /// will do to an existing name is leave it alone.
    ///
    /// <b>Merge, then rewrite.</b> When the target already has that name and the
    /// picture behind it is different, the source's copy lands under a name
    /// nothing in the target uses and the entry's own reference is rewritten to
    /// it. That is safe because the inside of one of these images is addressed
    /// relative to the image — <c>oS/l0/l1/l2</c>, <c>tS/u/no</c>,
    /// <c>bS/ani/no</c> — so a rename that carries the whole subtree with it
    /// breaks nothing, and nothing else in the client names these images at all.
    ///
    /// Decided once per named node and shared, because that is the shape of the
    /// problem: 666 of the 87-map folder's object placements name <c>acc1</c>,
    /// and 666 rows saying so is the same finding rendered as noise.
    /// </summary>
    /// <param name="named">
    /// The plan-wide decision cache, keyed by role and archive-relative path. Two
    /// maps naming the same tile set must get the same answer and one part.
    /// </param>
    /// <param name="rewrites">
    /// What the copies have to say instead, keyed by the reference path and the
    /// value the source wrote. Filled here at plan time and applied by
    /// <see cref="RewriteNamed"/> after the writing is done — never before, since
    /// a plan that is only previewed must leave the source untouched.
    /// </param>
    /// <param name="unsatisfied">
    /// Names neither client has. These are collected rather than reported per
    /// entry because they are a refusal: see the call site.
    /// </param>
    private List<PortPartDto> NamedParts(
        PortKindSpec spec, EntryLocation entry, PortItemDto item,
        ClientGroup source, ClientGroup? targetClient, OpenFile target,
        Dictionary<string, PortPartDto> named,
        Dictionary<(string Path, string Was), string> rewrites,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<string> unsatisfied,
        Dictionary<string, SortedSet<string>> wanted,
        LinkCarryContext linkCarry)
    {
        List<PortPartDto> parts = new();
        if (spec.Named == null)
            return parts;

        foreach (PortNamedRef edge in spec.Named)
        {
            foreach ((string value, SortedSet<string> addresses) in Uses(entry, edge))
            {
                if (value.Length == 0)
                    continue;

                // "None" is not a missing map mark, it is a map with no mark. A
                // sentinel read as a broken reference refuses ports that are fine.
                if (edge.Nothing != null
                    && value.Equals(edge.Nothing, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? inside = InsideArchive(edge, value);
                if (inside == null)
                {
                    item.Notes.Add(
                        $"Its {edge.What} is '{value}', which is not a shape this can read — a "
                        + $"{edge.What} is written as 'image/node' and this one has no node in it. "
                        + "Nothing was carried for it; check that reference by hand.");
                    continue;
                }

                string key = edge.Role + "|" + inside;

                // Every address any entry in this plan asks of this set. A
                // decision made for one map has to hold for the next: two maps
                // share a set and ask different things of it, and the second one's
                // addresses can be the ones the target's copy does not have.
                if (!wanted.TryGetValue(key, out SortedSet<string>? asked))
                    wanted[key] = asked = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                bool grew = false;
                foreach (string address in addresses)
                    grew |= asked.Add(address);

                if (named.TryGetValue(key, out PortPartDto? already))
                {
                    // Only worth re-asking when this entry brought something new
                    // AND the standing answer was "the target's own will do". A
                    // set already being copied covers every address it has.
                    if (grew && already.Status == "Same")
                    {
                        // A real list, not a discard: an answer upgraded from
                        // "the target's own will do" to a copy brings containers
                        // and linked images with it, and a part that joins no
                        // item is a part the write loop never sees.
                        List<PortPartDto> reMade = new();
                        DecideNamed(
                            already, edge, value, inside, asked, source, targetClient, target,
                            rewrites, renames, containers, containerSteps, reMade,
                            unsatisfied, linkCarry);
                        foreach (PortPartDto level in reMade)
                        {
                            if (!parts.Contains(level))
                                parts.Add(level);
                        }
                    }
                    parts.Add(already);
                    continue;
                }

                List<PortPartDto> made = new();
                PortPartDto part = new();
                DecideNamed(
                    part, edge, value, inside, asked, source, targetClient, target,
                    rewrites, renames, containers, containerSteps, made, unsatisfied, linkCarry);

                named[key] = part;

                // Container parts are shared plan-wide, so two references into
                // the same directory hand back the same object -- a map naming
                // acc1 and connect listed "creates Obj" twice on one card. The
                // counts were always right (DistinctParts keys on the target
                // path); the row was the thing that read as two writes.
                foreach (PortPartDto level in made)
                {
                    if (!parts.Contains(level))
                        parts.Add(level);
                }
                parts.Add(part);
            }
        }
        return parts;
    }

    /// <summary>
    /// "mapleIsland" -&gt; "Back/mapleIsland.img"; "Bgm34/WoundedLeaf" -&gt;
    /// "Bgm34.img/WoundedLeaf"; "MushroomVillage" -&gt; "MapHelper.img/mark/MushroomVillage".
    ///
    /// Null when the value cannot be one of those, which for a split reference
    /// means it named an image and no node inside it.
    /// </summary>
    private static string? InsideArchive(PortNamedRef edge, string value)
    {
        if (edge.Split)
        {
            string[] halves = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (halves.Length < 2)
                return null;
            return Img(halves[0]) + "/" + string.Join('/', halves.Skip(1));
        }

        string leaf = edge.Image ? Img(value) : value;
        return edge.Under.Length == 0 ? leaf : edge.Under + "/" + leaf;
    }

    /// <summary>
    /// An image name with exactly one ".img" on it, whichever way the client
    /// spelled it.
    ///
    /// Both spellings are real and neither is wrong. Measured on a v232 client,
    /// <c>info/bgm</c> reads "Bgm34/WoundedLeaf" with no suffix at all; other
    /// builds and every hand-edited archive write "Bgm34.img/WoundedLeaf". Taking
    /// one and appending blind produced "Bgm34.img.img", which resolves in
    /// neither client and read as a refusal about a clip that was sitting there
    /// the whole time.
    /// </summary>
    private static string Img(string name) =>
        name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? name : name + ".img";

    /// <summary>The reverse of <see cref="InsideArchive"/>: the value an entry must now hold.</summary>
    private static string ValueFor(PortNamedRef edge, string value, string newLeaf)
    {
        string stem = newLeaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && edge.Image
            ? newLeaf[..^4]
            : newLeaf;

        if (!edge.Split)
            return stem;

        // Only the node half is ever renamed for a split reference: the image
        // half is the target's own Bgm34.img, which this never touches.
        int slash = value.LastIndexOf('/');
        return slash < 0 ? stem : value[..(slash + 1)] + stem;
    }

    /// <summary>
    /// Which sets an entry draws from, and exactly which pieces of each.
    ///
    /// The addresses are the point, and they are what makes the common case free.
    /// Measured across a whole source library against a whole target one: 99.3% of
    /// the set names a map draws by already exist in the target, 99.4% of those
    /// are identical address for address, and 99.32% of all 17,613 maps therefore
    /// need not one byte of scenery copied. Asking "is this whole 21.7 MB book the
    /// same book" cannot see that; asking "does the target's book hold the twenty
    /// pieces this map wants" can, and answers it out of a name lookup.
    ///
    /// A set named with no placements under it comes back with no addresses at
    /// all, which asks only that the set exist. That is the honest reading: a
    /// layer carrying <c>info/tS</c> and no tiles draws nothing out of it.
    /// </summary>
    private static Dictionary<string, SortedSet<string>> Uses(EntryLocation entry, PortNamedRef edge)
    {
        Dictionary<string, SortedSet<string>> uses = new(StringComparer.Ordinal);

        SortedSet<string> For(string value)
        {
            if (!uses.TryGetValue(value, out SortedSet<string>? found))
                uses[value] = found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        // Every name the reference path finds, whether or not anything draws
        // through it. Losing one of these would lose the reference itself.
        foreach (string value in Texts(ChildrenOf(entry.Node), edge.Path.Split('/'), 0))
            For(value);

        if (edge.Places == null || edge.Address == null || edge.PlaceName == null)
            return uses;

        foreach (WzImageProperty place in Nodes(ChildrenOf(entry.Node), edge.Places.Split('/'), 0))
        {
            if (Value(Hop(place, edge.PlaceName)) is not { Length: > 0 } named)
                continue;

            string? address = AddressOf(place, edge.Address);
            if (address != null)
                For(named.Trim()).Add(address);
        }
        return uses;
    }

    /// <summary>
    /// A node's scalar text, or null when there is no node.
    ///
    /// <see cref="ScalarText"/> takes a property and this takes the result of a
    /// lookup, which is a different thing: a placement is not obliged to carry
    /// every field a reference names, and the very first real map read here had a
    /// background with no 'ani' child at all.
    /// </summary>
    private static string? Value(WzImageProperty? property) =>
        property == null ? null : ScalarText(property);

    /// <summary>The nodes at a slash path under a collection, with "*" for every child.</summary>
    private static IEnumerable<WzImageProperty> Nodes(
        WzPropertyCollection? level, string[] segments, int at)
    {
        if (level == null)
            yield break;

        bool last = at == segments.Length - 1;
        foreach (WzImageProperty property in level)
        {
            if (segments[at] != "*"
                && !string.Equals(property.Name, segments[at], StringComparison.OrdinalIgnoreCase))
                continue;

            if (last)
            {
                yield return property;
                continue;
            }
            foreach (WzImageProperty found in Nodes(property.WzProperties, segments, at + 1))
                yield return found;
        }
    }

    /// <summary>
    /// A node reached from a placement, with ".." for a level up.
    ///
    /// The hop is what a tile needs and a background does not: a background says
    /// <c>bS</c> on itself, and a tile is <c>{ u, no }</c> whose set is named two
    /// levels above it at the layer's <c>info/tS</c>.
    /// </summary>
    private static WzImageProperty? Hop(WzImageProperty from, string path)
    {
        WzObject? at = from;
        foreach (string step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (at == null)
                return null;
            at = step == ".."
                ? (at as WzImageProperty)?.Parent
                : at switch
                {
                    WzImageProperty property => property[step],
                    WzImage image => image[step],
                    _ => null,
                };
        }
        return at as WzImageProperty;
    }

    /// <summary>
    /// The path below a set image that one placement draws, or null when the
    /// placement does not say.
    ///
    /// "ani?ani:back" is the one conditional segment and it is not a flourish: a
    /// v232 background placement is <c>{ bS, ani, no }</c> and its frame is at
    /// <c>ani/&lt;no&gt;</c> when ani is set and <c>back/&lt;no&gt;</c> when it is
    /// not, so a check that guessed would ask the set for a picture the map never
    /// draws and rename a 3 MB book over the answer.
    /// </summary>
    private static string? AddressOf(WzImageProperty place, string[] fields)
    {
        List<string> segments = new(fields.Length);
        foreach (string field in fields)
        {
            int question = field.IndexOf('?');
            if (question < 0)
            {
                if (Value(place[field]) is not { Length: > 0 } plain)
                    return null;
                segments.Add(plain.Trim());
                continue;
            }

            string flag = field[..question];
            string[] both = field[(question + 1)..].Split(':');
            if (both.Length != 2)
                return null;

            // Absent reads as off. A background with no 'ani' child is not a
            // background whose branch is unknown; it is an ordinary one.
            string set = (Value(place[flag]) ?? "0").Trim();
            bool on = set.Length > 0 && set != "0";
            segments.Add(on ? both[0] : both[1]);
        }
        return segments.Count == 0 ? null : string.Join("/", segments);
    }

    /// <summary>
    /// One named node, decided: use the target's as it stands, copy under this
    /// name, copy under a different one, or refuse.
    ///
    /// Ordered so that the answer nearly every reference gets is the first one
    /// reached and the cheapest to reach. Copying is the exception here, not the
    /// mechanism: two clients of the same game share a scenery library rather
    /// than owning colliding ones, and the measured shape of that is 1,929,186 of
    /// 1,931,467 references resolving in the target with nothing copied at all.
    ///
    /// Fills <paramref name="part"/> rather than returning a new one, because a
    /// standing decision is revisited when a later entry asks the same set for a
    /// piece the first one did not.
    /// </summary>
    private void DecideNamed(
        PortPartDto part, PortNamedRef edge, string value, string inside, SortedSet<string> addresses,
        ClientGroup source, ClientGroup? targetClient, OpenFile target,
        Dictionary<(string Path, string Was), string> rewrites,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<PortPartDto> made,
        List<string> unsatisfied,
        LinkCarryContext linkCarry)
    {
        string[] segments = inside.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string what = Capitalise(edge.Role) + ".wz";

        part.Kind = "named";
        part.Label = $"{what} · {inside}";
        part.SourceArchive = Capitalise(edge.Role);
        part.SourcePath = null;
        part.TargetPath = null;
        part.Existing = null;
        part.Bytes = 0;

        if (targetClient == null)
        {
            part.Status = "Blocked";
            part.Reason = "The target archive is not part of a client folder this can read, so the "
                        + $"{edge.What} '{value}' could not be looked up in it.";
            return;
        }

        Reach? inSource = ReachNamed(WithRole(source, edge.Role), segments);
        List<OpenFile> targetArchives = WithRole(targetClient, edge.Role);
        Reach? inTarget = ReachNamed(targetArchives, segments);

        if (inSource is { Complete: true })
            part.SourcePath = inSource.Path;

        /* ---- the target already has something of that name ---- */

        if (inTarget is { Complete: true })
        {
            if (inSource is not { Complete: true })
            {
                // Not a refusal: the reference resolves in the target, so the
                // copy will draw something. It may be the wrong something, and
                // that is worth saying rather than passing over.
                part.Status = "Absent";
                part.TargetPath = inTarget.Path;
                part.Reason =
                    $"{source.Label} has no {inside} open, so its {edge.What} could not be compared or "
                    + $"carried. The target's own '{value}' is what the copy will draw. If the two builds "
                    + "changed that picture, this map will look wrong in a way nothing here can see — open "
                    + $"the source's {what} and preview again to have it checked.";
                return;
            }

            List<string> wrong = Mismatched(inSource.Node, inTarget.Node, addresses);
            if (wrong.Count == 0)
            {
                part.Status = "Same";
                part.TargetPath = inTarget.Path;
                part.Reason = addresses.Count == 0
                    ? $"The target already has this {edge.What} and nothing here draws a particular piece "
                      + "out of it, so it is used as it stands and nothing is copied."
                    : $"The target's own '{value}' holds every one of the {addresses.Count:N0} piece"
                      + (addresses.Count == 1 ? "" : "s")
                      + " drawn from it, identically, so nothing is copied and the reference is left "
                      + "alone. Two clients of one game share this library rather than owning rival "
                      + "copies of it.";
                return;
            }

            // The copy that is about to land is not self-contained: its frames
            // reach OUT of it by absolute link, and what those links name has
            // to be decided before the copy is promised — see ChaseLinks.
            LinkCarryOutcome rides = ChaseLinks(
                inSource.Node, inside, inSource.Path, edge, source, targetClient, targetArchives,
                linkCarry, renames, containers, containerSteps,
                new List<string> { inside }, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (RefuseOverLinkChain(part, rides, inSource, value))
                return;

            Distinct(
                part, edge, value, segments, inSource, targetArchives, targetClient, source,
                wrong, rewrites, renames, containers, containerSteps, made, rides.EagerMap());
            AttachLinkCarries(part, rides, edge, made, linkCarry);
            return;
        }

        /* ---- the target has nothing of that name ---- */

        if (inSource is not { Complete: true })
        {
            // Neither side has it. Everything else in this method produces a
            // target that draws something; this is the one that does not, and it
            // is the "empty room" the map kind was refused over for years.
            unsatisfied.Add(
                $"'{value}' ({edge.What}, {what}/{inside})");
            part.Status = "Blocked";
            part.Reason =
                $"Neither client has {inside}. {source.Label} has nothing to copy and {targetClient.Label} "
                + "has nothing to fall back on, so the copy would name a picture that is not there.";
            return;
        }

        LinkCarryOutcome carries = ChaseLinks(
            inSource.Node, inside, inSource.Path, edge, source, targetClient, targetArchives,
            linkCarry, renames, containers, containerSteps,
            new List<string> { inside }, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (RefuseOverLinkChain(part, carries, inSource, value))
            return;

        Place(
            part, edge, value, segments, inSource, inTarget, targetArchives, targetClient,
            newLeaf: segments[^1], rewrites, renames, containers, containerSteps, made);
        AttachLinkCarries(part, carries, edge, made, linkCarry);
    }

    /// <summary>
    /// The bound refusal: the chase behind a set outran what a port will carry,
    /// so the SET is refused — with the chain and the cost named on the plan —
    /// rather than copied as a shell that silently draws another build's art
    /// behind its own frames.
    /// </summary>
    private static bool RefuseOverLinkChain(
        PortPartDto part, LinkCarryOutcome rides, Reach inSource, string value)
    {
        if (rides.Chain == null)
            return false;

        part.Status = "Blocked";
        part.SourcePath = inSource.Path;
        part.TargetPath = null;
        part.Reason =
            "Not copied: its frames reach through absolute art links further than this port will carry, "
            + "and a copy without what they name draws another build's pictures behind its own frames — "
            + "or nothing at all. The chase stopped at: " + rides.Chain
            + (rides.Carried.Count > 0
                ? $". Before the stop it had already gathered {rides.Carried.Count:N0} "
                  + $"image{(rides.Carried.Count == 1 ? "" : "s")} ({Megabytes(rides.CarriedBytes)})"
                : "")
            + $". Port the images it draws from first, or accept what the target draws under "
            + $"'{value}' today.";
        return true;
    }

    /// <summary>
    /// Puts what a set's links drag in onto the plan: the carried images and
    /// their containers as parts, the disclosure on the set's own part, and the
    /// texts each landed copy owes — held as decisions, materialised at apply
    /// time so every copy is rewritten from the same final answer.
    /// </summary>
    private static void AttachLinkCarries(
        PortPartDto part, LinkCarryOutcome rides, PortNamedRef edge,
        List<PortPartDto> made, LinkCarryContext linkCarry)
    {
        // A set that ends up Blocked (every rename taken, or nowhere writable)
        // ships nothing, so nothing may ride along on its account either — a
        // carried image with nothing naming it is dead weight in the client.
        if (part.Status is not ("New" or "Same"))
            return;

        HashSet<LinkedImageDecision> shown = new(rides.Carried);
        foreach ((string _, LinkedImageDecision into) in rides.Rewrites)
            shown.Add(into);

        foreach (LinkedImageDecision decision in shown)
        {
            foreach (PortPartDto level in decision.Levels)
            {
                if (!made.Contains(level))
                    made.Add(level);
            }
            if (decision.Part == null)
                continue;
            if (!made.Contains(decision.Part))
                made.Add(decision.Part);
            if (decision.OwnRewrites.Count > 0)
                linkCarry.Owe(decision.Part, decision.OwnRewrites);
        }

        if (rides.Rewrites.Count > 0)
            linkCarry.Owe(part, rides.Rewrites);

        if (part.Status == "New" || rides.Carried.Count > 0)
            part.Reason = (part.Reason ?? "") + rides.Note(edge.What);
    }

    /// <summary>
    /// Which of the pieces this entry draws the target's set cannot serve:
    /// absent, or there and holding a different picture.
    ///
    /// This is the whole of the identity question, and it is asked of the pieces
    /// rather than of the book. Asking of the book is both dearer and wrong: two
    /// builds of one object set differ somewhere far more often than they differ
    /// at the twenty addresses one map uses, and a difference nobody can see is
    /// not a reason to copy 21.7 MB in under a second name.
    ///
    /// With no addresses at all the question collapses to "does the set exist",
    /// which is what a map mark and a music clip need: there the node named IS
    /// the thing, and it is compared whole.
    /// </summary>
    private static List<string> Mismatched(WzObject inSource, WzObject inTarget, SortedSet<string> addresses)
    {
        List<string> wrong = new();

        if (addresses.Count == 0)
        {
            if (!SamePicture(inSource, inTarget))
                wrong.Add("(the whole of it)");
            return wrong;
        }

        foreach (string address in addresses)
        {
            WzObject? mine = Descend(inSource, address);
            WzObject? theirs = Descend(inTarget, address);

            // Not in the source either. That is not the target's failing and
            // copying the source's book would not fix it: the map draws nothing
            // there in the client it came from either.
            if (mine == null)
                continue;

            if (theirs == null || !SamePicture(mine, theirs))
                wrong.Add(address);
        }
        return wrong;
    }

    /// <summary>
    /// Whether two readings of one address draw the same thing — asked of the
    /// PICTURE, not of where the bytes for it happen to be kept.
    ///
    /// This is the difference between a merge that works and one that ships
    /// blank backgrounds, and the case is not exotic. Measured on this pair of
    /// clients: v232 keeps <c>Back/mapleIsland.img/ani/0/0</c> as a 521x394
    /// canvas with the pixels inline, and v233 keeps the same frame as a 1x1
    /// placeholder carrying <c>_outlink = "Map/Back/adventure.img/ani/0/0"</c>,
    /// with the pixels in an image v232 has never had. Structurally those two
    /// are as different as two canvases get. They are the same picture, and both
    /// builds say so in the same breath: each carries
    /// <c>_hash = 490f9080b79e4572b0045692ca93196eee3a0b1456c4b94d634afd05b95fc5bc</c>.
    ///
    /// Compared structurally, the target's own background reads as a different
    /// one, the source's placeholder is carried in under
    /// <c>mapleIsland_&lt;client&gt;</c>, and the copy's <c>_outlink</c>s name an
    /// image the target does not have — so the map draws 14 of its 61 pieces and
    /// nothing in the plan says so. Compared by <c>_hash</c>, the answer is
    /// "the target already has this", nothing is copied at all, and the map draws
    /// out of the client's own art the way its native maps do.
    ///
    /// <c>_hash</c> is the client's own pixel digest and is what makes this cheap
    /// as well as right: the doc on <see cref="TreeDigest"/> laments that
    /// comparing pixels costs more than the whole port, and for the canvases that
    /// carry one this is that comparison, for free and exactly. Coverage measured
    /// across both clients' scenery: 98.2% of tile canvases and 85.2% of
    /// background canvases carry it.
    ///
    /// It is a hint and not an invariant, and <c>SameCanvas</c> below says
    /// exactly where it is allowed to be the last word: when one side keeps no
    /// pixels of its own, because then nothing else can compare them, and never
    /// when both do. Everywhere else identity is content
    /// (<see cref="WzContentHasher"/>) — never a name, never a block size, and
    /// never a digest the archive happens to carry that nothing in this
    /// application maintains.
    ///
    /// <c>_inlink</c> and <c>_outlink</c> are skipped for the same reason the
    /// sizes are: they say where a build filed the pixels, which is precisely
    /// what two builds are allowed to disagree about.
    /// </summary>
    private static bool SamePicture(WzObject? inSource, WzObject? inTarget)
    {
        int budget = MaxDigestNodes;
        return Same(inSource, inTarget, 0);

        bool Same(WzObject? mine, WzObject? theirs, int depth)
        {
            if (mine == null || theirs == null)
                return ReferenceEquals(mine, theirs);

            if (mine is WzImage a)
            {
                try { WzSessionService.EnsureParsed(a); }
                catch { return false; }
            }
            if (theirs is WzImage b)
            {
                try { WzSessionService.EnsureParsed(b); }
                catch { return false; }
            }

            if (mine is WzCanvasProperty left && theirs is WzCanvasProperty right && !SameCanvas(left, right))
                return false;

            // Out of budget or depth is "no answer", and the honest reading of no
            // answer here is that they match as far as anyone looked — the same
            // reading TreeDigest's "|+" gave.
            if (budget <= 0 || depth > MaxDigestDepth)
                return true;

            List<WzImageProperty> ours = Comparable(mine);
            List<WzImageProperty> yours = Comparable(theirs);
            if (ours.Count != yours.Count)
                return false;

            for (int i = 0; i < ours.Count; i++)
            {
                if (--budget <= 0)
                    return true;

                WzImageProperty one = ours[i], other = yours[i];
                if (!string.Equals(one.Name, other.Name, StringComparison.Ordinal))
                    return false;

                if (one is WzCanvasProperty or WzSubProperty || other is WzCanvasProperty or WzSubProperty)
                {
                    if (!Same(one, other, depth + 1))
                        return false;
                    continue;
                }

                if (!string.Equals(ScalarText(one) ?? "", ScalarText(other) ?? "", StringComparison.Ordinal))
                    return false;

                if (!Same(one, other, depth + 1))
                    return false;
            }
            return true;
        }

        static bool SameCanvas(WzCanvasProperty mine, WzCanvasProperty theirs)
        {
            // Read straight off the property rather than through ScalarText,
            // which dereferences what it is given and so cannot be asked about a
            // canvas that has no '_hash' — which is 1.8% of this client's tiles
            // and 14.8% of its backgrounds.
            string? ours = (mine[HashPropertyName] as WzStringProperty)?.Value;
            string? yours = (theirs[HashPropertyName] as WzStringProperty)?.Value;

            if (ours is { Length: > 0 } && yours is { Length: > 0 })
            {
                if (!string.Equals(ours, yours, StringComparison.OrdinalIgnoreCase))
                    return false;

                // The hashes agree. Whether that settles it depends on what the
                // two canvases actually hold.
                //
                // When either side keeps no pixels of its own — a 1x1 placeholder
                // over an '_inlink' or an '_outlink' — '_hash' is the ONLY thing
                // the two have in common, and it is the measured win this whole
                // method exists for: v232 keeps mapleIsland's frame inline at
                // 521x394 and v233 keeps it as a placeholder pointing into an
                // image v232 has never had, and both builds write the same
                // '_hash'. Nothing else can see that they are the same picture.
                if (Linked(mine) || Linked(theirs))
                    return true;

                // Both hold their own pixels, so the claim is checkable — and it
                // has to be checked, because '_hash' is content the archive
                // carries and NOT a digest anything here maintains. This app's
                // own canvas editor leaves it exactly as it found it, so the
                // sequence "edit a frame in MapleBench, then port into that
                // client" produces two canvases whose stale hashes agree and
                // whose pixels do not — and the port answers "the target already
                // has this", keeps the edit, and silently substitutes it for the
                // source's art. Identity is content; '_hash' is a hint that is
                // usually right and is nobody's invariant.
                return Content(mine, theirs) == true;
            }

            // No usable '_hash' on one of them, so content decides.
            //
            // Through WzContentHasher rather than a raw block comparison, and the
            // difference is not academic: GetCompressedBytes hands back the block
            // still wearing the archive's list.wz XOR layer, so the same picture
            // in two clients reads as two pictures and gets copied for nothing.
            // The hasher reads through GetCompressedBytesForExtraction, which is
            // key-independent, and it refuses rather than truncating on a tree it
            // cannot walk — so there is no depth at which "not compared" comes
            // back as "the same".
            //
            // Affordable only because it is asked per ADDRESS: the frames one map
            // actually places, tens of them, not the tens of thousands in a 21.7
            // MB object book.
            return Content(mine, theirs) == true;
        }

        /// <summary>A canvas that keeps its pixels somewhere else.</summary>
        static bool Linked(WzCanvasProperty canvas) =>
            canvas[WzCanvasProperty.InlinkPropertyName] != null
            || canvas[WzCanvasProperty.OutlinkPropertyName] != null;

        /// <summary>
        /// Content identity, or null when the hasher refused to give one.
        ///
        /// Null is kept apart from false on purpose even though both callers act
        /// on it the same way: "these differ" and "this could not be compared"
        /// are different facts, and collapsing them at the point of measurement
        /// is how a zero comes to mean two things three refactors later.
        /// </summary>
        static bool? Content(WzObject mine, WzObject theirs)
        {
            try { return WzContentHasher.ContentEquals(mine, theirs); }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }
        }

        // Ordered by name so two archives that list their children differently
        // still agree, and without the three that describe storage rather than
        // content.
        static List<WzImageProperty> Comparable(WzObject node)
        {
            List<WzImageProperty> kept = new();
            foreach (WzImageProperty child in ChildrenOf(node) ?? new WzPropertyCollection(null))
            {
                if (child.Name is HashPropertyName
                    or WzCanvasProperty.InlinkPropertyName
                    or WzCanvasProperty.OutlinkPropertyName)
                {
                    continue;
                }
                kept.Add(child);
            }
            kept.Sort(static (x, y) => string.CompareOrdinal(x.Name, y.Name));
            return kept;
        }
    }

    /// <summary>
    /// The pixel digest a modern client keeps beside a canvas. Not a MapleLib
    /// concept — it is content the archives carry, and it is the only exact
    /// picture comparison available without decoding both sides.
    /// </summary>
    private const string HashPropertyName = "_hash";

    /// <summary>
    /// How far a slash path gets inside one client's archives of a role, and
    /// where it stopped.
    /// </summary>
    /// <param name="File">The archive this reading is in.</param>
    /// <param name="Path">The session path of the deepest node that does exist.</param>
    /// <param name="Node">That node.</param>
    /// <param name="Resolved">How many segments of the path it accounts for.</param>
    /// <param name="Steps">The type of every segment that resolved, for mirroring.</param>
    private sealed record Reach(
        OpenFile File, string Path, WzObject Node, int Resolved, IReadOnlyList<PathStep> Steps, bool Complete);

    /// <summary>
    /// The best reading of one path across every archive of a role.
    ///
    /// "Best" is deepest first, then the one holding the most children, then the
    /// first offered. The tie-break is not cosmetic: a v232 client keeps 318 Back
    /// images in Map001.wz and 148 more in Map2.wz, so "which Back does a new
    /// background belong in" has a real answer and it is the one the client
    /// itself uses most.
    ///
    /// Read-only archives are included. A reference resolves against what the
    /// client can see, not against what this app may write to — deciding
    /// otherwise would report a name as free when the client already has it, and
    /// then copy a second one in beside it.
    /// </summary>
    private static Reach? ReachNamed(List<OpenFile> archives, string[] segments)
    {
        Reach? best = null;

        foreach (OpenFile file in archives)
        {
            WzDirectory? root = file.WzFile?.WzDirectory;
            if (root == null)
                continue;

            WzObject current = root;
            string path = file.Id;
            List<PathStep> steps = new();
            int resolved = 0;

            foreach (string segment in segments)
            {
                WzObject? next = Step(current, segment);
                if (next == null)
                    break;

                steps.Add(new PathStep(segment, TypeOf(next)));
                path = WzPath.Child(path, segment);
                current = next;
                resolved++;
            }

            Reach reading = new(file, path, current, resolved, steps, resolved == segments.Length);
            if (best == null
                || reading.Resolved > best.Resolved
                || (reading.Resolved == best.Resolved && Children(reading.Node) > Children(best.Node)))
            {
                best = reading;
            }
        }
        return best;
    }

    /// <summary>One step down a literal path, following nothing and creating nothing.</summary>
    private static WzObject? Step(WzObject node, string segment)
    {
        switch (node)
        {
            case WzDirectory directory:
                return (WzObject?)directory.GetDirectoryByName(segment) ?? directory.GetImageByName(segment);

            case WzImage image:
                try { WzSessionService.EnsureParsed(image); }
                catch { return null; }
                return image[segment];

            case WzImageProperty property:
                return property[segment];

            default:
                return null;
        }
    }

    private static string TypeOf(WzObject node) => node switch
    {
        WzDirectory => "Directory",
        WzImage => "Image",
        WzCanvasProperty => "Canvas",
        _ => "SubProperty",
    };

    private static int Children(WzObject node) => node switch
    {
        WzDirectory directory => directory.WzDirectories.Count + directory.WzImages.Count,
        _ => ChildrenOf(node)?.Count ?? 0,
    };

    /// <summary>
    /// The target has this name and it is a different thing, so the copy takes a
    /// name of its own.
    ///
    /// The suffix is the source client's own folder name rather than a counter,
    /// because the point of it is to be readable a year later: a target holding
    /// both <c>acc1</c> and <c>acc1_v255</c> says where the second one came from,
    /// where <c>acc1_2</c> says only that somebody once had a collision.
    ///
    /// A name already taken by an IDENTICAL copy is reused rather than skipped
    /// past, which is what makes a second port of the same map write nothing at
    /// all instead of leaving <c>acc1_v255</c>, <c>acc1_v2552</c>, <c>acc1_v2553</c>
    /// behind it.
    /// </summary>
    private void Distinct(
        PortPartDto part, PortNamedRef edge, string value, string[] segments, Reach inSource,
        List<OpenFile> targetArchives, ClientGroup targetClient, ClientGroup source,
        List<string> wrong,
        Dictionary<(string Path, string Was), string> rewrites,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<PortPartDto> made,
        IReadOnlyDictionary<string, string>? linkRetext = null)
    {
        string leaf = segments[^1];
        bool img = leaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
        string stem = img ? leaf[..^4] : leaf;
        string slug = ProvenanceSuffix(source, inSource.File, out string suffixOrigin);

        // Before coining anything: is an earlier port's copy already here,
        // under WHATEVER suffix that port derived? Asked of every `stem_…`
        // sibling by identity, so the copies the old folder-slug rule left as
        // `acc1_Data` are still this port's own work and not strangers.
        foreach ((string priorLeaf, Reach prior) in PriorCopies(targetArchives, segments, stem, img))
        {
            if (!SameTreeForReuse(inSource.Node, prior.Node, linkRetext))
                continue;

            string priorName = img ? priorLeaf[..^4] : priorLeaf;
            part.Status = "Same";
            part.TargetPath = prior.Path;
            part.Reason =
                $"The target already holds this exact {edge.What} under the name '{priorName}', which is "
                + "where an earlier port of it landed. Nothing is copied and the map is pointed at it.";
            Rewrite(rewrites, edge, value, ValueFor(edge, value, priorLeaf));
            return;
        }

        for (int attempt = 0; attempt < MaxDistinctNames; attempt++)
        {
            string candidate = stem + "_" + slug + (attempt == 0 ? "" : (attempt + 1).ToString(
                CultureInfo.InvariantCulture));
            string newLeaf = img ? candidate + ".img" : candidate;

            string[] tried = segments.ToArray();
            tried[^1] = newLeaf;
            Reach? taken = ReachNamed(targetArchives, tried);

            if (taken is { Complete: true })
            {
                // Somebody's already here. If it is this very picture it is a
                // previous port of the same thing, and reusing it is the whole
                // reason this loop compares rather than counts. Reuse, so a
                // truncated digest cannot decide it — see SameTreeForReuse.
                // "This very picture" allows for one difference: the outlink
                // texts this port itself would rewrite in the copy. Without the
                // map, a copy whose linked images rode along last time digests
                // as a stranger and every re-port lands another 21.7 MB of it.
                if (!SameTreeForReuse(inSource.Node, taken.Node, linkRetext))
                    continue;

                part.Status = "Same";
                part.TargetPath = taken.Path;
                part.Reason =
                    $"The target already holds this exact {edge.What} under the name '{candidate}', which is "
                    + "where an earlier port of it landed. Nothing is copied and the map is pointed at it.";
                Rewrite(rewrites, edge, value, ValueFor(edge, value, newLeaf));
                return;
            }

            Place(
                part, edge, value, tried, inSource, taken, targetArchives, targetClient,
                newLeaf, rewrites, renames, containers, containerSteps, made);

            if (part.Status == "New")
            {
                string missing = string.Join(", ", wrong.Take(4))
                               + (wrong.Count > 4 ? $" and {wrong.Count - 4} more" : "");

                part.Reason =
                    $"{targetClient.Label} has a {edge.What} called '{value}' too, and it cannot serve "
                    + $"{wrong.Count:N0} of the piece{(wrong.Count == 1 ? "" : "s")} this one draws out of "
                    + $"it: {missing}. Replacing the target's would change every other map already drawing "
                    + $"from it — measured on a real pair of clients, a set is named by 35 maps at the "
                    + "median and 4,218 at the ninetieth percentile — so this copy lands as "
                    + $"'{candidate}' instead and this map's own reference is rewritten to match. The "
                    + $"'_{slug}' suffix is {suffixOrigin}; the same source always derives the same "
                    + "suffix, so a later port of the same thing finds this copy and reuses it. Nothing "
                    + "the target already had is touched. "
                    + (part.Reason ?? "");
            }
            return;
        }

        part.Status = "Blocked";
        part.Reason =
            $"{MaxDistinctNames} names beginning '{stem}_{slug}' are already taken in {targetClient.Label} "
            + $"by {edge.What}s that are none of them this one. Something has ported into this client many "
            + "times over; tidy those up before adding another.";
    }

    /// <summary>
    /// One linked image, decided once for the whole plan.
    ///
    /// A copied scenery set is not self-contained: across one client's library
    /// the sets carry 8,688 absolute <c>_outlink</c>s, every one naming a
    /// DIFFERENT image. Left alone, a copy goes on reading those frames out of
    /// whatever the target keeps under those other names — the wrong build's
    /// pictures, or nothing at all. So each image a copy links into gets exactly
    /// one of four answers, cached here so two sets naming the same image agree:
    ///
    ///   * <c>identical</c> — the target serves the linked-to pieces identically;
    ///     the link is left byte for byte as it is (the common case, and the one
    ///     that keeps ports that are fine today exactly as cheap as they were).
    ///   * <c>carried</c> — absent, or present with different pictures behind
    ///     the linked-to pieces: the image rides along, under its own name when
    ///     that name is free and under a rename-on-clash name when it is not,
    ///     and every link into it is rewritten in the copies to match.
    ///   * <c>reused</c> — an earlier port already carried this exact image;
    ///     the links are pointed at that copy and nothing lands twice.
    ///   * <c>blocked</c> — the chase outran its bounds; the SET that started
    ///     the chain is refused with the chain named, because a shell that
    ///     silently draws another build's art is not a port.
    /// </summary>
    private sealed class LinkedImageDecision
    {
        public required string Key { get; init; }
        public required EntryLocation Source { get; init; }

        /// <summary>Every piece any link in this plan asks of it.</summary>
        public SortedSet<string> Asked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Outcome = "";

        /// <summary>The plan part when carried (New) or reused (Same).</summary>
        public PortPartDto? Part;

        /// <summary>Containers a carry has to create above it.</summary>
        public List<PortPartDto> Levels { get; } = new();

        /// <summary>The leaf name it lands (or already sits) under, suffix included.</summary>
        public string? Leaf;

        /// <summary>Its own outbound rewrites — applied to ITS copy after the write.</summary>
        public List<(string Was, LinkedImageDecision Into)> OwnRewrites { get; } = new();

        /// <summary>Bytes its carry adds to the port; 0 when nothing new lands.</summary>
        public long Bytes;
    }

    /// <summary>
    /// What one copied node's outbound links need: the rewrites its own copy
    /// must carry, the images that ride along because of it, and the counts the
    /// disclosure is made of. <see cref="Chain"/> is the refusal — non-null
    /// means the set must not be copied at all, and names the road that led
    /// past the bound.
    /// </summary>
    private sealed class LinkCarryOutcome
    {
        public List<(string Was, LinkedImageDecision Into)> Rewrites { get; } = new();
        public HashSet<LinkedImageDecision> Carried { get; } = new();
        public int LeftAlone;
        public int Unresolved;
        public string? Chain;

        public long CarriedBytes => Carried.Sum(d => d.Bytes);

        /// <summary>The rewrites as text, for comparisons that need them now.</summary>
        public Dictionary<string, string> EagerMap()
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            foreach ((string was, LinkedImageDecision into) in Rewrites)
            {
                if (RetextOf(was, into) is { } now)
                    map[was] = now;
            }
            return map;
        }

        /// <summary>The disclosure, appended to the set's part so the cost is on the plan.</summary>
        public string Note(string what)
        {
            List<string> said = new();
            if (Carried.Count > 0)
            {
                List<string> names = Carried.Select(d => d.Leaf ?? d.Source.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(6).ToList();
                said.Add(
                    $"{Carried.Count:N0} image{(Carried.Count == 1 ? "" : "s")} it draws from by absolute "
                    + $"link ride{(Carried.Count == 1 ? "s" : "")} along ({Megabytes(CarriedBytes)} more): "
                    + string.Join(", ", names)
                    + (Carried.Count > names.Count ? $" and {Carried.Count - names.Count} more" : "")
                    + ", and the copies' links are rewritten to the names those land under.");
            }
            if (LeftAlone > 0)
            {
                said.Add(
                    $"{LeftAlone:N0} linked image{(LeftAlone == 1 ? "" : "s")} the target already serves "
                    + "identically at the linked frames are left exactly as they are.");
            }
            if (Unresolved > 0)
            {
                said.Add(
                    $"{Unresolved:N0} linked image{(Unresolved == 1 ? " is" : "s are")} in no open source "
                    + "archive, so those links travel as they are and the target draws whatever it has "
                    + "under those names — check those frames by hand.");
            }
            return said.Count == 0 ? "" : " " + string.Join(" ", said);
        }
    }

    /// <summary>
    /// The text a link must say once its image lands under
    /// <paramref name="into"/>'s name — or null when nothing changes, which is
    /// both the carried-under-its-own-name case and the identical case.
    /// </summary>
    private static string? RetextOf(string was, LinkedImageDecision into)
    {
        if (into.Outcome is not ("carried" or "reused") || into.Leaf == null)
            return null;
        if (!CanvasLinkPath.TryParse(was, out CanvasLinkPath link))
            return null;

        string now = link.WithImage(into.Leaf);
        return string.Equals(now, was, StringComparison.Ordinal) ? null : now;
    }

    /// <summary>
    /// The plan-wide state of link carrying: one decision per linked image, and
    /// the rewrites each landed copy owes, held as references to the decisions
    /// so the text is materialised from the FINAL answer at apply time.
    /// </summary>
    private sealed class LinkCarryContext
    {
        public Dictionary<string, LinkedImageDecision> Decisions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per landed part: the outlink texts its copy must swap.</summary>
        public Dictionary<PortPartDto, List<(string Was, LinkedImageDecision Into)>> Retexts { get; } =
            new(ReferenceEqualityComparer.Instance);

        public void Owe(PortPartDto part, IEnumerable<(string Was, LinkedImageDecision Into)> rewrites)
        {
            if (!Retexts.TryGetValue(part, out List<(string, LinkedImageDecision)>? owed))
                Retexts[part] = owed = new List<(string, LinkedImageDecision)>();
            owed.AddRange(rewrites);
        }
    }

    /// <summary>
    /// Every distinct image a node's canvases <c>_outlink</c> into, with the
    /// piece each link draws and the exact texts that say so.
    /// </summary>
    private static Dictionary<string, (SortedSet<string> Pieces, SortedSet<string> Texts)> OutlinkUses(
        WzObject node)
    {
        Dictionary<string, (SortedSet<string> Pieces, SortedSet<string> Texts)> uses =
            new(StringComparer.OrdinalIgnoreCase);
        int walked = 0;
        WzWalk guard = new();

        void Walk(WzPropertyCollection? properties, int depth)
        {
            if (properties == null)
                return;
            foreach (WzImageProperty property in properties)
            {
                if (++walked > MaxLinkWalk)
                    return;
                if (property is WzCanvasProperty
                    && property[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty outlink
                    && CanvasLinkPath.TryParse(outlink.Value, out CanvasLinkPath link))
                {
                    if (!uses.TryGetValue(link.ImagePath, out (SortedSet<string> Pieces, SortedSet<string> Texts) use))
                    {
                        uses[link.ImagePath] = use = (
                            new SortedSet<string>(StringComparer.OrdinalIgnoreCase),
                            new SortedSet<string>(StringComparer.Ordinal));
                    }
                    if (link.Remainder.Length > 0)
                        use.Pieces.Add(link.Remainder);
                    use.Texts.Add(link.Text);
                }
                Walk(guard.Into(property, depth), depth + 1);
            }
        }

        if (node is WzImage image)
            WzSessionService.EnsureParsed(image);
        Walk(ChildrenOf(node), 0);
        return uses;
    }

    /// <summary>
    /// Swaps a landed copy's <c>_outlink</c> texts by exact match. The texts are
    /// replaced whole and nothing else in the canvas is touched, so a text this
    /// map does not know stays byte for byte as it arrived.
    /// </summary>
    private static int RetextOutlinks(WzObject copy, IReadOnlyDictionary<string, string> texts)
    {
        int changed = 0;
        int walked = 0;
        WzWalk guard = new();

        void Walk(WzPropertyCollection? properties, int depth)
        {
            if (properties == null)
                return;
            foreach (WzImageProperty property in properties)
            {
                if (++walked > MaxLinkWalk)
                    return;
                if (property is WzCanvasProperty
                    && property[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty outlink
                    && outlink.Value is { } was
                    && texts.TryGetValue(was, out string? now)
                    && !string.Equals(was, now, StringComparison.Ordinal))
                {
                    outlink.Value = now;
                    changed++;
                }
                Walk(guard.Into(property, depth), depth + 1);
            }
        }

        Walk(RootsOf(copy), 0);
        return changed;
    }

    /// <summary>
    /// Decides what rides along behind one copied node's outbound links. Called
    /// for the set a plan is about to copy, and by itself for each image it
    /// carries — a carried image's own links are the next hop of the same
    /// question.
    ///
    /// The identity question is asked of the linked-to PIECES, with the same
    /// justification <see cref="Mismatched"/> gives for sets: two builds of one
    /// background book differ somewhere far more often than they differ at the
    /// frames these links actually draw, and a book the target serves
    /// identically at every linked frame is left alone — links and all — so a
    /// port that is fine today costs exactly what it cost yesterday.
    /// </summary>
    private LinkCarryOutcome ChaseLinks(
        WzObject node, string ownerLabel, string ownerSourcePath, PortNamedRef edge,
        ClientGroup source, ClientGroup targetClient, List<OpenFile> targetArchives,
        LinkCarryContext ctx,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<string> chain, int depth, HashSet<string> inProgress)
    {
        LinkCarryOutcome outcome = new();

        Dictionary<string, (SortedSet<string> Pieces, SortedSet<string> Texts)> uses;
        try { uses = OutlinkUses(node); }
        catch
        {
            // A node whose links cannot even be read is not one whose links can
            // be promised. Refuse the set rather than guessing at its closure.
            outcome.Chain = string.Join(" -> ", chain) + " (its links could not be read)";
            return outcome;
        }

        foreach ((string imagePath, (SortedSet<string> pieces, SortedSet<string> texts)) in
                 uses.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
        {
            EntryLocation? inSource = ResolveImage(source, imagePath);
            if (inSource == null)
            {
                // Not in the source either: the source's own client draws those
                // frames out of whatever answers that name, and this port has
                // nothing truer to offer. Left as it is, said in the note.
                outcome.Unresolved++;
                continue;
            }

            // Its own image, by another spelling. Nothing to carry.
            if (string.Equals(inSource.Path, ownerSourcePath, StringComparison.OrdinalIgnoreCase))
                continue;

            string key = edge.Role + "|" + inSource.Relative;

            if (inProgress.Contains(key))
            {
                // Two images linking into each other. Following the loop would
                // decide each on the strength of the other's undecided answer,
                // so the chase stops and the set is refused with the loop named.
                outcome.Chain = string.Join(" -> ", chain) + $" -> {inSource.Relative} (a cycle)";
                return outcome;
            }

            LinkedImageDecision? decision = ctx.Decisions.GetValueOrDefault(key);

            // A standing "the target serves it" only stands for the pieces it
            // was asked about. A second set can link a frame the first did not,
            // and that frame can be the diverged one — so the answer is re-asked
            // of the grown set, exactly the way NamedParts re-asks DecideNamed.
            if (decision is { Outcome: "identical" } && !pieces.IsSubsetOf(decision.Asked))
            {
                ctx.Decisions.Remove(key);
                SortedSet<string> asked = decision.Asked;
                decision = null;
                pieces.UnionWith(asked);
            }

            if (decision == null)
            {
                inProgress.Add(key);
                try
                {
                    decision = DecideLinked(
                        key, inSource, pieces, edge, source, targetClient, targetArchives, ctx,
                        renames, containers, containerSteps,
                        chain.Append(inSource.Relative).ToList(), depth + 1, inProgress,
                        out string? blocked);
                    if (blocked != null)
                    {
                        outcome.Chain = blocked;
                        return outcome;
                    }
                    ctx.Decisions[key] = decision!;
                }
                finally
                {
                    inProgress.Remove(key);
                }
            }
            else
            {
                decision.Asked.UnionWith(pieces);
            }

            switch (decision!.Outcome)
            {
                case "identical":
                    outcome.LeftAlone++;
                    break;

                case "carried":
                case "reused":
                    foreach (string text in texts)
                        outcome.Rewrites.Add((text, decision));
                    if (decision.Outcome == "carried")
                    {
                        outcome.Carried.Add(decision);
                        // Everything IT carries rides on this set's ticket too:
                        // the disclosure and the byte bound are about the whole
                        // closure, not the first hop.
                        foreach (LinkedImageDecision below in ClosureOf(decision))
                            outcome.Carried.Add(below);
                    }
                    break;
            }
        }

        if (outcome.CarriedBytes > MaxCarriedLinkBytes)
        {
            outcome.Chain =
                string.Join(" -> ", chain) + " — "
                + $"{outcome.Carried.Count:N0} images, {Megabytes(outcome.CarriedBytes)}, over the "
                + $"{Megabytes(MaxCarriedLinkBytes)} a set may bring";
            return outcome;
        }
        return outcome;
    }

    /// <summary>Every carried decision reachable below one, cycles impossible by construction.</summary>
    private static IEnumerable<LinkedImageDecision> ClosureOf(LinkedImageDecision decision)
    {
        Stack<LinkedImageDecision> open = new();
        HashSet<LinkedImageDecision> seen = new() { decision };
        open.Push(decision);
        while (open.Count > 0)
        {
            foreach ((string _, LinkedImageDecision below) in open.Pop().OwnRewrites)
            {
                if (below.Outcome == "carried" && seen.Add(below))
                {
                    yield return below;
                    open.Push(below);
                }
            }
        }
    }

    /// <summary>
    /// One linked image, decided fresh: does the target serve the linked-to
    /// pieces, and when it cannot, where does the carry land.
    ///
    /// The landing rules are the set rules on purpose — its own name when free,
    /// rename-on-clash when taken, an identical earlier copy reused — because a
    /// linked image IS scenery, reached by link instead of by <c>bS</c>/<c>oS</c>.
    /// </summary>
    private LinkedImageDecision? DecideLinked(
        string key, EntryLocation inSource, SortedSet<string> pieces, PortNamedRef edge,
        ClientGroup source, ClientGroup targetClient, List<OpenFile> targetArchives,
        LinkCarryContext ctx,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<string> chain, int depth, HashSet<string> inProgress,
        out string? blocked)
    {
        blocked = null;

        LinkedImageDecision decision = new() { Key = key, Source = inSource };
        decision.Asked.UnionWith(pieces);

        string[] segments = inSource.Relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Reach? inTarget = ReachNamed(targetArchives, segments);

        /* ---- can the target serve the linked-to pieces as it stands? ---- */

        if (inTarget is { Complete: true })
        {
            List<string> wrong = Mismatched(inSource.Node, inTarget.Node, pieces);
            if (wrong.Count == 0)
            {
                decision.Outcome = "identical";
                decision.Leaf = inSource.Name;
                return decision;
            }
        }

        /* ---- it cannot; the image has to ride along ---- */

        if (depth > MaxCarriedLinkDepth)
        {
            blocked =
                string.Join(" -> ", chain)
                + $" — stopped at {MaxCarriedLinkDepth} hops of linked images";
            return null;
        }

        // Its own links first, because both of what follows needs them: the
        // copy that lands must say the rewritten texts, and recognising an
        // earlier port's copy means comparing against those texts.
        LinkCarryOutcome below = ChaseLinks(
            inSource.Node, inSource.Relative, inSource.Path, edge,
            source, targetClient, targetArchives, ctx, renames, containers, containerSteps,
            chain, depth, inProgress);
        if (below.Chain != null)
        {
            blocked = below.Chain;
            return null;
        }
        decision.OwnRewrites.AddRange(below.Rewrites);

        Dictionary<string, string> ownMap = new(StringComparer.Ordinal);
        foreach ((string was, LinkedImageDecision into) in below.Rewrites)
        {
            if (RetextOf(was, into) is { } now)
                ownMap[was] = now;
        }

        string leaf = segments[^1];
        bool img = leaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
        string stem = img ? leaf[..^4] : leaf;

        /* ---- the name is free: land under it, links untouched ---- */

        if (inTarget is not { Complete: true })
        {
            if (!PlaceLinked(decision, edge, segments, inSource, inTarget, targetArchives,
                             targetClient, leaf, renames, containers, containerSteps, chain,
                             out blocked))
            {
                return null;
            }
            return decision;
        }

        /* ---- the name is taken by a different picture: rename on clash ---- */

        string slug = ProvenanceSuffix(source, inSource.File, out _);

        // An earlier port's carry may sit under whatever suffix THAT port
        // derived — see PriorCopies. Asked by identity before any name is
        // coined, so a suffix-rule change cannot orphan it into a duplicate.
        foreach ((string priorLeaf, Reach prior) in PriorCopies(targetArchives, segments, stem, img))
        {
            if (!SameTreeForReuse(inSource.Node, prior.Node, ownMap))
                continue;

            string[] priorTried = segments.ToArray();
            priorTried[^1] = priorLeaf;
            string priorName = img ? priorLeaf[..^4] : priorLeaf;

            decision.Outcome = "reused";
            decision.Leaf = priorLeaf;
            decision.Part = new PortPartDto
            {
                Kind = "named",
                Label = $"{prior.File.Name} · {string.Join("/", priorTried)}",
                SourceArchive = Capitalise(edge.Role),
                SourcePath = inSource.Path,
                TargetPath = prior.Path,
                Status = "Same",
                Reason =
                    $"'{ownerOf(chain)}' draws frames out of {inSource.Relative} by absolute link, and "
                    + $"an earlier port already carried that exact image as '{priorName}'. Nothing is "
                    + "copied again; the links are pointed at it.",
            };
            return decision;
        }

        for (int attempt = 0; attempt < MaxDistinctNames; attempt++)
        {
            string candidate = stem + "_" + slug + (attempt == 0 ? "" : (attempt + 1).ToString(
                CultureInfo.InvariantCulture));
            string newLeaf = img ? candidate + ".img" : candidate;

            string[] tried = segments.ToArray();
            tried[^1] = newLeaf;
            Reach? taken = ReachNamed(targetArchives, tried);

            if (taken is { Complete: true })
            {
                if (!SameTreeForReuse(inSource.Node, taken.Node, ownMap))
                    continue;

                decision.Outcome = "reused";
                decision.Leaf = newLeaf;
                decision.Part = new PortPartDto
                {
                    Kind = "named",
                    Label = $"{taken.File.Name} · {string.Join("/", tried)}",
                    SourceArchive = Capitalise(edge.Role),
                    SourcePath = inSource.Path,
                    TargetPath = taken.Path,
                    Status = "Same",
                    Reason =
                        $"'{ownerOf(chain)}' draws frames out of {inSource.Relative} by absolute link, and "
                        + $"an earlier port already carried that exact image as '{candidate}'. Nothing is "
                        + "copied again; the links are pointed at it.",
                };
                return decision;
            }

            if (!PlaceLinked(decision, edge, tried, inSource, taken, targetArchives,
                             targetClient, newLeaf, renames, containers, containerSteps, chain,
                             out blocked))
            {
                return null;
            }
            return decision;
        }

        blocked =
            string.Join(" -> ", chain)
            + $" — {MaxDistinctNames} names beginning '{stem}_{slug}' are already taken by images that "
            + "are none of them this one";
        return null;

        static string ownerOf(List<string> chain) => chain.Count > 1 ? chain[^2] : chain[0];
    }

    /// <summary>
    /// Files a carried linked image into the target — <see cref="Place"/>'s
    /// rules, without the named-reference bookkeeping a linked image does not
    /// have: nothing names it by <c>bS</c>/<c>oS</c>, only link texts, and those
    /// are rewritten from the decisions at apply time.
    /// </summary>
    private bool PlaceLinked(
        LinkedImageDecision decision, PortNamedRef edge, string[] segments, EntryLocation inSource,
        Reach? inTarget, List<OpenFile> targetArchives, ClientGroup targetClient, string newLeaf,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<string> chain,
        out string? blocked)
    {
        blocked = null;

        OpenFile home;
        string cursor;
        int from;

        if (inTarget is { Resolved: > 0 })
        {
            home = inTarget.File;
            cursor = inTarget.Path;
            from = inTarget.Resolved;
        }
        else
        {
            OpenFile? writable = targetArchives.FirstOrDefault(f => !f.ReadOnly);
            if (writable == null)
            {
                blocked = string.Join(" -> ", chain)
                        + $" — no writable {Capitalise(edge.Role)}.wz to carry it into";
                return false;
            }
            home = writable;
            cursor = writable.Id;
            from = 0;
        }

        if (home.ReadOnly)
        {
            blocked = string.Join(" -> ", chain)
                    + $" — it belongs in {home.Name}, which is open for reference only";
            return false;
        }

        for (int i = from; i < segments.Length - 1; i++)
        {
            cursor = WzPath.Child(cursor, segments[i]);
            string type = i < inSource.Steps.Count ? inSource.Steps[i].Type : "SubProperty";
            decision.Levels.Add(Container(
                cursor, new PathStep(segments[i], type),
                $"{home.Name} · {segments[i]} ({type.ToLowerInvariant()})",
                $"{home.Name} has no {segments[i]} here, so the port creates it — that is where "
                + $"{inSource.File.Name} keeps the image a carried link names.",
                containers, containerSteps));
        }

        bool renamed = !string.Equals(newLeaf, inSource.Name, StringComparison.Ordinal);
        PortPartDto part = new()
        {
            Kind = "named",
            Label = $"{home.Name} · {string.Join("/", segments)}",
            SourceArchive = Capitalise(edge.Role),
            SourcePath = inSource.Path,
            TargetPath = WzPath.Child(cursor, newLeaf),
            Status = "New",
            Bytes = inSource.Node is WzImage art ? art.BlockSize : 0,
            Reason =
                $"Rides along: '{chain[0]}' reaches it through "
                + (chain.Count > 2 ? string.Join(" -> ", chain.Skip(1).SkipLast(1)) + " and " : "")
                + "an absolute art link, and the target "
                + (renamed
                    ? "holds a different picture under its name — so it lands as "
                      + $"'{CanvasLinkPath.StripImageSuffix(newLeaf)}' and every link into it is rewritten "
                      + "to match. Nothing the target already had is touched."
                    : "has no image of that name — without it those frames draw nothing at all."),
        };

        if (renamed || !string.Equals(newLeaf, inSource.Node.Name, StringComparison.Ordinal))
            renames[part] = newLeaf;

        decision.Outcome = "carried";
        decision.Leaf = newLeaf;
        decision.Part = part;
        decision.Bytes = inSource.Node is WzImage image ? image.BlockSize : 0;
        return true;
    }

    /// <summary>
    /// Files the source's node into the target at a given name, creating whatever
    /// levels above it the target lacks.
    ///
    /// The levels are parts of their own for the reason
    /// <see cref="SatelliteParts"/> gives at length: a preview that names every
    /// node an apply will create is the only one whose counts mean anything, and
    /// a level created silently is a level that came back "'Back' not found under
    /// 'f3'" at write time with the plan having promised otherwise.
    /// </summary>
    private void Place(
        PortPartDto part, PortNamedRef edge, string value, string[] segments, Reach inSource,
        Reach? inTarget, List<OpenFile> targetArchives, ClientGroup targetClient, string newLeaf,
        Dictionary<(string Path, string Was), string> rewrites,
        Dictionary<PortPartDto, string> renames,
        Dictionary<string, PortPartDto> containers,
        Dictionary<PortPartDto, PathStep> containerSteps,
        List<PortPartDto> made)
    {
        // Where to start building from. A partial reading is the right place by
        // construction; nothing resolving at all means this family has never had
        // one of these, and then the first writable archive of the role is the
        // only honest answer -- and it is named in the reason so it is a decision
        // the reader can see.
        OpenFile home;
        string cursor;
        int from;

        if (inTarget is { Resolved: > 0 })
        {
            home = inTarget.File;
            cursor = inTarget.Path;
            from = inTarget.Resolved;
        }
        else
        {
            OpenFile? writable = targetArchives.FirstOrDefault(f => !f.ReadOnly);
            if (writable == null)
            {
                part.Status = "Blocked";
                part.Reason = targetArchives.Count == 0
                    ? $"{targetClient.Label} has no {Capitalise(edge.Role)}.wz open, so there is nowhere to "
                      + $"put this {edge.What}. Open it and preview again."
                    : $"Every {Capitalise(edge.Role)}.wz {targetClient.Label} has open is reference-only, so "
                      + $"this {edge.What} cannot be written. Unlock one in the Files panel.";
                return;
            }
            home = writable;
            cursor = writable.Id;
            from = 0;
        }

        if (home.ReadOnly)
        {
            part.Status = "Blocked";
            part.Reason =
                $"This {edge.What} belongs in {home.Name}, which is open for reference only. Unlock it in "
                + "the Files panel, or open the archive of that family you meant to write to.";
            return;
        }

        // Everything above the leaf, mirrored from the source with the source's
        // own node types -- a Back is a directory, a MapHelper.img is an image and
        // a mark is a property, and guessing any of those wrong files the copy
        // where the client never looks.
        List<string> creates = new();
        for (int i = from; i < segments.Length - 1; i++)
        {
            cursor = WzPath.Child(cursor, segments[i]);
            string type = i < inSource.Steps.Count ? inSource.Steps[i].Type : "SubProperty";
            creates.Add(segments[i]);
            made.Add(Container(
                cursor, new PathStep(segments[i], type),
                $"{home.Name} · {segments[i]} ({type.ToLowerInvariant()})",
                $"{home.Name} has no {segments[i]} here, so the port creates it — that is where "
                + $"{inSource.File.Name} keeps its {edge.What}s, and one filed anywhere else is one the "
                + "client never looks for.",
                containers, containerSteps));
        }
        part.Status = "New";
        part.SourcePath = inSource.Path;
        part.TargetPath = WzPath.Child(cursor, newLeaf);
        part.Label = $"{home.Name} · {string.Join("/", segments)}";

        // Said because it is the surprise. A map is 51 KB and one of these is
        // 21.7 MB, and a plan reporting only the entries' own size would be off
        // by five hundred times.
        part.Bytes = inSource.Node is WzImage art ? art.BlockSize : 0;
        part.Reason = creates.Count == 0
            ? null
            : $"The target's {home.Name} has no {string.Join("/", creates)}, so the port creates it.";

        // Only when the name is not the source's own. Write() reads this to
        // correct a landing Transfer renamed out of the way, and asking it to
        // rename a node to the name it already has is a no-op that still costs an
        // undo step.
        if (!string.Equals(newLeaf, segments[^1], StringComparison.Ordinal)
            || !string.Equals(newLeaf, inSource.Node.Name, StringComparison.Ordinal))
        {
            renames[part] = newLeaf;
        }

        Rewrite(rewrites, edge, value, ValueFor(edge, value, newLeaf));
    }

    /// <summary>
    /// Records what the copies must say, and refuses to record two answers for
    /// one question.
    ///
    /// A rewrite is applied to every entry this port lands, so two decisions for
    /// one name would make the result depend on which map happened to be walked
    /// first. The decision cache upstream means this cannot happen today; the
    /// throw is here so it cannot start happening quietly.
    /// </summary>
    private static void Rewrite(
        Dictionary<(string Path, string Was), string> rewrites, PortNamedRef edge, string was, string now)
    {
        if (string.Equals(was, now, StringComparison.Ordinal))
            return;

        (string, string) key = (edge.Path, was);
        if (rewrites.TryGetValue(key, out string? already)
            && !string.Equals(already, now, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{was}' was going to be rewritten both to '{already}' and to '{now}'. Nothing was written.");
        }
        rewrites[key] = now;
    }

    /// <summary>A client label as a WZ name: "MapleStory 255" -&gt; "MapleStory255".</summary>
    private static string Slug(string label)
    {
        string kept = new(label.Where(char.IsLetterOrDigit).ToArray());
        if (kept.Length == 0)
            kept = "port";
        return kept.Length <= MaxSlugLength ? kept : kept[^MaxSlugLength..];
    }

    /// <summary>
    /// Folder names that say how a client is LAID OUT rather than which client
    /// it is. A split client is opened at its `Data` folder, so the folder-leaf
    /// rule stamped every renamed copy `acc1_Data` — a suffix that names the
    /// layout convention shared by every split client on the machine.
    /// </summary>
    private static readonly HashSet<string> GenericFolderWords =
        new(StringComparer.OrdinalIgnoreCase) { "data", "wz", "client", "clients", "game", "games", "bin", "files" };

    /// <summary>
    /// The provenance suffix a renamed-on-clash copy carries — something that
    /// says WHICH CLIENT it came from, a year later, which the folder leaf
    /// alone cannot (`…\v255\Data` and `…\v263\Data` both leaf to `Data`).
    ///
    /// The rule, in order, and it is stated on the plan part so the reader can
    /// check it rather than trust it:
    ///
    ///  1. The deepest segment of the source client's folder path that is not
    ///     a generic layout word — `…\v255\Data` names `v255`. At most three
    ///     segments are considered, because beyond the client's own folder and
    ///     its parents a name stops being about the client.
    ///  2. Failing that, a short tag derived from the source archive's own
    ///     bytes — `x` + the first 8 hex of its SHA-256, the same identity the
    ///     composition ledger pins sources by — so `D:\Data` still coins a
    ///     suffix that is stable across sessions and different between clients.
    ///  3. Failing even a file to hash (an in-memory fixture), the label as
    ///     before.
    ///
    /// Deterministic on purpose: the same source must coin the same name, or a
    /// re-port cannot recognise its own earlier work and lands a duplicate.
    /// Copies coined under an OLDER rule — every `_Data` already written — are
    /// still recognised, by <see cref="PriorCopies"/> asking the identity
    /// question of every `stem_…` sibling rather than only of the names this
    /// rule would coin.
    /// </summary>
    private static string ProvenanceSuffix(ClientGroup source, OpenFile? archive, out string origin)
    {
        string[] segments = source.Folder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        int looked = 0;
        for (int i = segments.Length - 1; i >= 0 && looked < 3; i--)
        {
            string segment = segments[i];
            if (segment.Contains(':'))
                break;                          // the drive; nothing above it is a name
            looked++;
            string slug = Slug(segment);
            if (slug.Length < 2)
                continue;
            if (GenericFolderWords.Contains(slug))
                continue;
            origin = i == segments.Length - 1
                ? $"the source client's own folder name ('{segment}')"
                : $"the '{segment}' folder the source client's '{segments[^1]}' folder sits in";
            return slug;
        }

        string? path = archive?.FilePath;
        if (path is { Length: > 0 } && File.Exists(path))
        {
            try
            {
                string hash = HashFileCached(path);
                origin = $"a tag derived from {archive!.Name}'s own bytes (SHA-256 {hash[..8]}…), because " +
                         "every folder above the source client is a generic layout name";
                return "x" + hash[..8];
            }
            catch (Exception)
            {
                // An unreadable file names nothing; fall through to the label.
            }
        }

        origin = $"the source client's label ('{source.Label}')";
        return Slug(source.Label);
    }

    /// <summary>
    /// Every name already sitting beside where a renamed copy would land that
    /// begins with the copy's own stem — an earlier port's work travels under
    /// whatever suffix THAT port derived (`acc1_Data` under the old folder
    /// rule, `acc1_x1f2e3d4c` after a source archive changed), so the reuse
    /// question is asked of every such sibling by IDENTITY rather than only of
    /// the names this port would coin. Without this, a suffix-rule change
    /// orphans every earlier copy into a 21.7 MB duplicate per press of the
    /// button. Content-gated by the caller through SameTreeForReuse, so a
    /// stranger that merely shares the prefix is never pointed at.
    /// </summary>
    private static IEnumerable<(string Leaf, Reach Reach)> PriorCopies(
        List<OpenFile> archives, string[] segments, string stem, bool img)
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        string prefix = stem + "_";

        foreach (OpenFile file in archives)
        {
            WzObject? current = file.WzFile?.WzDirectory;
            bool ok = current != null;
            for (int i = 0; ok && i < segments.Length - 1; i++)
            {
                current = Step(current!, segments[i]);
                ok = current != null;
            }
            if (!ok || current == null)
                continue;

            foreach (string name in ChildNames(current))
            {
                if (img != name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    continue;
                string bare = img ? name[..^4] : name;
                if (bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }

        foreach (string name in names)
        {
            string[] tried = segments.ToArray();
            tried[^1] = name;
            if (ReachNamed(archives, tried) is { Complete: true } reach)
                yield return (name, reach);
        }
    }

    /// <summary>The names of a container's children, whatever the container is.</summary>
    private static IEnumerable<string> ChildNames(WzObject node)
    {
        switch (node)
        {
            case WzDirectory directory:
                foreach (WzDirectory sub in directory.WzDirectories) yield return sub.Name;
                foreach (WzImage image in directory.WzImages) yield return image.Name;
                break;

            case WzImage image:
                bool readable = true;
                try { WzSessionService.EnsureParsed(image); } catch { readable = false; }
                if (!readable) yield break;
                foreach (WzImageProperty property in image.WzProperties) yield return property.Name;
                break;

            default:
                WzPropertyCollection? children = ChildrenOf(node);
                if (children == null) yield break;
                foreach (WzImageProperty property in children) yield return property.Name;
                break;
        }
    }

    /// <summary>
    /// Whether two nodes hold the same thing, near enough to share a name.
    ///
    /// Names, scalar values, and — this is the part that costs something and is
    /// the part that matters — every canvas's size, format and PIXELS. Walked in
    /// name order, so two archives that happen to list their children in a
    /// different order still agree.
    ///
    /// Comparing only the shape was tried and is not good enough, and the reason
    /// is the whole decision this feeds. Two builds' <c>Obj/acc1.img</c> have the
    /// same node names and the same frame sizes and different art in them; judged
    /// the same, the ported map is pointed at the TARGET's objects, which is the
    /// wrong-art failure the rename exists to prevent, arrived at by a shortcut.
    /// A picture is what these images are, so a picture is what has to be
    /// compared.
    ///
    /// Paid for by being bounded twice over: the first <see cref="MaxDigestNodes"/>
    /// nodes and the first <see cref="MaxDigestBytes"/> of art. Two builds of one
    /// tile set differ in the first screenful or not at all, and the alternative
    /// to a bound is hashing 21,732,097 bytes on both sides of every one of a
    /// map's four references.
    ///
    /// The compressed bytes are taken in their extraction form rather than as
    /// stored, because the stored form carries the archive's own key: the same
    /// picture in two clients is two different byte strings until that is undone,
    /// which would report every image as different and rename all of them.
    /// </summary>
    private static string TreeDigest(WzObject? node) => TreeDigest(node, out _);

    private static string TreeDigest(WzObject? node, out bool truncated) =>
        TreeDigest(node, out truncated, null);

    /// <param name="outlinkRetext">
    /// Texts this port itself will write into the copy's <c>_outlink</c>s, so a
    /// tree can be digested AS IT WILL LAND rather than as it sits in the
    /// source. Without it, a copy whose links were rewritten on an earlier port
    /// can never again compare equal to the source it was made from.
    /// </param>
    private static string TreeDigest(
        WzObject? node, out bool truncated, IReadOnlyDictionary<string, string>? outlinkRetext)
    {
        truncated = false;
        if (node == null)
            return "";

        if (node is WzImage image)
        {
            try { WzSessionService.EnsureParsed(image); }
            catch
            {
                // Unreadable is "no answer", and two no-answers are not each
                // other: every caller that compares digests must treat this one
                // as truncated, or two images that both refuse to parse would
                // compare identical and authorise reusing one for the other.
                truncated = true;
                return "?unreadable";
            }
        }

        System.Text.StringBuilder digest = new();
        int budget = MaxDigestNodes;
        long bytes = MaxDigestBytes;
        bool cut = false;

        void Payload(WzImageProperty property)
        {
            if (property is not WzCanvasProperty canvas)
            {
                digest.Append(ScalarText(property) ?? "");
                return;
            }

            WzPngProperty? png = canvas.PngProperty;
            digest.Append(png?.Width ?? 0).Append('x').Append(png?.Height ?? 0)
                  .Append('f').Append(png?.Format.ToString() ?? "?");

            // The client's own answer, taken in preference to computing one.
            //
            // '_hash' is a content stamp the build writes beside a canvas, and it
            // is not a rarity: measured on a real client, 105,550 of its 119,460
            // scenery canvases carry one, and 6,825 of the 6,910 a sample of
            // 4,001 maps actually draw. Reading it costs a property lookup, where
            // deciding the same question from pixels costs the compressed blob
            // off both sides. It is also the comparison the two builds themselves
            // agree on, which is a better authority than anything derived here.
            if (canvas["_hash"]?.WzValue?.ToString() is { Length: > 0 } stamp)
            {
                digest.Append('#').Append(stamp);
                return;
            }

            if (png == null)
                return;
            if (bytes <= 0)
            {
                // The art budget ran out before this canvas was fingerprinted,
                // so from here on the digest describes sizes and formats only —
                // which two different pictures can share.
                cut = true;
                return;
            }

            try
            {
                byte[]? art = png.GetCompressedBytesForExtraction(false);
                if (art == null)
                    return;

                bytes -= art.Length;
                digest.Append(':').Append(art.Length).Append(':').Append(Fingerprint(art));
            }
            catch
            {
                // A canvas that will not give up its bytes is a real state -- a
                // link placeholder's blob is empty and asking for it throws. It
                // is recorded as unreadable rather than as equal to every other
                // unreadable one, which would make two different images compare
                // the same and reuse the target's art. The marker alone cannot
                // guarantee that — every unreadable canvas writes the same ":?"
                // — so the digest is also flagged as inexact.
                digest.Append(":?");
                cut = true;
            }
        }

        void Walk(WzObject at, int depth)
        {
            WzPropertyCollection? children = ChildrenOf(at);
            if (children == null || children.Count == 0)
                return;

            if (budget <= 0 || depth > MaxDigestDepth)
            {
                // There are children here and none of them will be looked at.
                cut = true;
                return;
            }

            foreach (WzImageProperty child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (--budget <= 0)
                {
                    digest.Append("|+");
                    cut = true;
                    return;
                }

                digest.Append('|').Append(child.Name).Append('=');
                if (outlinkRetext != null
                    && child is WzStringProperty text
                    && string.Equals(
                        child.Name, WzCanvasProperty.OutlinkPropertyName, StringComparison.Ordinal)
                    && text.Value is { } was
                    && outlinkRetext.TryGetValue(was, out string? now))
                {
                    digest.Append(now);
                }
                else
                {
                    Payload(child);
                }
                Walk(child, depth + 1);
            }
        }

        // The node's own value first. Without it every leaf digests to the empty
        // string, so a bgm clip the target already has under that name compares
        // equal to the source's whatever is in either of them -- and the port
        // reuses the target's music and reports that it carried the source's.
        if (node is WzImageProperty self)
        {
            digest.Append('=');
            Payload(self);
        }

        Walk(node, 0);
        truncated = cut;
        return digest.ToString();
    }

    /// <summary>
    /// Whether an existing node may be REUSED in place of copying
    /// <paramref name="node"/> — the one question a truncated digest must never
    /// answer.
    ///
    /// <see cref="TreeDigest"/> is bounded on purpose, and for refusing a name
    /// the bound is safe: two trees whose first <see cref="MaxDigestNodes"/>
    /// nodes differ are different, full stop. Reuse is the other direction.
    /// A digest that stopped at the budget says "they match as far as anyone
    /// looked", and acting on that substituted the target's art for the
    /// source's on nothing more than the first screenful agreeing — the exact
    /// shortcut-arrived-at wrong-art failure the comparison exists to prevent.
    ///
    /// So: digests differ — not the same, cheaply. Digests agree and neither
    /// was truncated — the same, cheaply. Digests agree but either was
    /// truncated — the question is re-asked of the FULL content through
    /// <see cref="WzContentHasher"/>, which walks everything and throws rather
    /// than truncating; and a tree the hasher refuses to walk authorises
    /// nothing, because there is no honest partial answer to "is this the same
    /// content".
    /// </summary>
    private static bool SameTreeForReuse(WzObject? node, WzObject? existing) =>
        SameTreeForReuse(node, existing, null);

    /// <param name="outlinkRetext">
    /// The link texts this port would write into a fresh copy of
    /// <paramref name="node"/>. An existing copy carrying exactly those texts
    /// is this port's own earlier work and may be reused; see
    /// <see cref="TreeDigest(WzObject?, out bool, IReadOnlyDictionary{string, string}?)"/>.
    /// </param>
    private static bool SameTreeForReuse(
        WzObject? node, WzObject? existing, IReadOnlyDictionary<string, string>? outlinkRetext)
    {
        string mine = TreeDigest(node, out bool cutMine, outlinkRetext);
        string theirs = TreeDigest(existing, out bool cutTheirs);

        if (!string.Equals(mine, theirs, StringComparison.Ordinal))
            return false;
        if (!cutMine && !cutTheirs)
            return true;
        if (node == null || existing == null)
            return ReferenceEquals(node, existing);

        // The hasher walks the raw trees and cannot be handed the retext, so a
        // truncated agreement across a link rewrite has no exact re-ask. It
        // authorises nothing: the worst outcome of refusing is a duplicate
        // copy, where the worst outcome of trusting is the wrong picture.
        if (outlinkRetext is { Count: > 0 })
            return false;

        try
        {
            return WzContentHasher.ContentEquals(node, existing);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// A canvas's bytes as one number. FNV-1a, which is not a security hash and
    /// is not being asked to be one: the question is whether two pictures in two
    /// archives are the same picture, and the alternative to a hash is keeping
    /// both blobs resident to compare them.
    /// </summary>
    private static ulong Fingerprint(byte[] bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211;
        }
        return hash;
    }

    /// <summary>
    /// The port's rename decisions as a <see cref="ReferenceRewriteMap"/> — the
    /// translation from "this edge's value 'acc1' is now 'acc1_v255'" to "the
    /// <see cref="WzNamedRole.ObjSet"/> called 'acc1' is now 'acc1_v255'".
    ///
    /// The role comes off the <see cref="PortNamedRef"/> declaration itself,
    /// which is the only place that knows which namespace an edge's names
    /// resolve in. This replaced <c>RewriteNamed</c>/<c>Retext</c>, the
    /// service's private path-walking rewrite: one implementation of "rename a
    /// named set" now exists, in <see cref="WzReferenceRewriter"/>, and it was
    /// only writable once <see cref="ReferenceRewriteMap.AddNamedSet"/> demanded
    /// a role — keyed on the name alone, bS = "spinOff1" and oS = "spinOff1"
    /// would collapse into one rename that rewrites both.
    ///
    /// A named edge without a <see cref="PortNamedRef.SetRole"/> throws rather
    /// than being skipped: the plan has already promised the rename on its
    /// warnings, and a promise silently not kept is the exact failure shape
    /// this file keeps finding.
    /// </summary>
    internal static ReferenceRewriteMap NamedRewriteMap(
        PortKindSpec spec, IReadOnlyDictionary<(string Path, string Was), string> rewrites)
    {
        ReferenceRewriteMap map = new();
        foreach (((string edgePath, string was), string now) in rewrites)
        {
            PortNamedRef edge = spec.Named?.FirstOrDefault(
                    e => string.Equals(e.Path, edgePath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"A rename was recorded for '{edgePath}', which is not a named edge the "
                    + $"'{spec.Kind}' kind declares. Nothing was rewritten.");

            if (edge.SetRole is not { } role)
            {
                throw new InvalidOperationException(
                    $"The named edge '{edge.Path}' declares no WzNamedRole, so its rename "
                    + $"('{was}' -> '{now}') cannot be expressed to the reference layer without keying "
                    + "on the name alone — which would rewrite every other role's set of that name. "
                    + "Declare the role on the PortNamedRef.");
            }

            map.AddNamedSet(role, was, now);
        }
        return map;
    }

    /// <summary>A kind's catalog row, for tests that exercise the declarations themselves.</summary>
    internal static PortKindSpec KindSpecFor(string kind) => Require(kind);

    #endregion

    #region Writing

    /// <summary>
    /// Performs one part.
    ///
    /// Everything goes through <see cref="WzEditService"/> rather than touching
    /// MapleLib: <c>Transfer</c> is the one place that clones across archives,
    /// records an undo step for the node it displaces (an overwrite used to
    /// destroy the displaced node permanently and leave the archive reporting
    /// clean), and marks the right file dirty. <c>Add</c> is the one place that
    /// creates a directory with the archive's own encryption cloned onto it.
    ///
    /// Caller holds the gate and an open undo batch.
    /// </summary>
    /// <summary>
    /// Inlines the art of a just-copied image, when the target needs it inline.
    /// Returns how many canvas links were left in the split form, which is the
    /// number of frames this client will not be able to draw.
    /// </summary>
    /// <param name="portWrote">
    /// See <see cref="FlattenCanvasArt(WzObject, ClientGroup?, out int, IReadOnlySet{WzObject}?)"/>.
    /// Null for an ordinary merge; the nodes this port landed when the caller
    /// asked to match the source.
    /// </param>
    private int Flatten(
        bool flattenArt, string path, ClientGroup? client, out int blank,
        IReadOnlySet<WzObject>? portWrote = null)
    {
        blank = 0;
        if (!flattenArt)
            return 0;
        // What the port wrote is not always an image.
        //
        // A quest IS an image; a skill is a property two levels inside one, so a
        // skill port lands "421.img/skill/4211016". This asked for a WzImage and
        // returned 0 when it did not get one -- which the caller reads as
        // "nothing needed reshaping". Every skill port therefore skipped this
        // pass in silence, left its links in the split form the target cannot
        // read, and then let the drop below delete the art those links point at
        // on the strength of the same zero.
        WzObject? node = _session.TryResolve(path);
        WzImage? owner = OwningImage(node);
        if (node == null || owner == null)
            return 0;

        WzSessionService.EnsureParsed(owner);
        (int n, int left) = FlattenCanvasArt(node, client, out blank, portWrote);
        if (n > 0)
        {
            owner.Changed = true;
            _log.LogInformation(
                "Reshaped {Count} canvas links in {Path}: this client has no _Canvas of its own, so the "
                + "art is inlined once and everything sharing it points there.",
                n, path);
        }
        if (left > 0)
        {
            _log.LogWarning(
                "{Count} canvas links in {Path} still point into a _Canvas directory this client does not "
                + "use, and were not reshaped.",
                left, path);
        }
        return left;
    }

    /// <summary>
    /// Is any canvas in the target still pointing into a <c>_Canvas</c> directory?
    ///
    /// The last word before art is deleted. Everything else in that decision is
    /// a claim about what the reshape did; this is a claim about what the
    /// archive now says, and only the second one is safe to delete on.
    ///
    /// Stops at the first survivor -- the answer is a yes/no and a large archive
    /// should not be swept twice to learn it.
    /// </summary>
    internal static bool AnyCanvasLinkSurvivesForTest(ClientGroup? client, OpenFile target) =>
        SurvivingCanvasLinks(client, target).Links > 0;

    /// <summary>
    /// How many unreachable canvas links the target still holds, and which images
    /// hold them.
    ///
    /// A count rather than a yes/no, because two things need this answer and they
    /// were reading different numbers. The sweep asked the archive; the warning
    /// the user actually sees was keyed on what the reshape pass reported having
    /// left behind — and that number is zero in states where links plainly
    /// survive. TryResolve handing back nothing for a landed path, or a throw
    /// counted into <c>artFailures</c>, both leave the reshape with nothing to
    /// report and the archive full of links, and the message that did come out
    /// ("everything else in this port still landed") was untrue of exactly the
    /// entries that mattered. One scan now answers both.
    ///
    /// No longer stops at the first survivor: the count is the message, and 30
    /// dead links on one skill is a different sentence from one.
    /// </summary>
    private static (int Links, SortedSet<string> Images) SurvivingCanvasLinks(
        ClientGroup? client, OpenFile target)
    {
        IEnumerable<OpenFile> files = client?.Files ?? new List<OpenFile> { target };
        int links = 0;
        SortedSet<string> images = new(StringComparer.OrdinalIgnoreCase);

        foreach (OpenFile file in files)
        {
            WzDirectory? root = file.WzFile?.WzDirectory;
            if (root == null)
                continue;

            foreach (WzImage image in AllImages(root))
            {
                // Only what is already in memory. Parsing a 1.2 GB archive to
                // answer this would cost more than the port, and an unparsed
                // image cannot have been written by this run -- so it cannot be
                // holding a link this run created.
                //
                // Changed as well as Parsed. An image this run created is held
                // whole in memory and never went through a parse, so `Parsed` is
                // false on precisely the images most likely to be carrying the
                // links this is looking for -- which is how a scan meant to be the
                // last word before deleting art came to skip the port's own
                // output.
                if (!image.Parsed && !image.Changed)
                    continue;
                WzWalk walk = new();
                int here = CountCanvasLinks(image.WzProperties, walk, 0);

                // A walk that had to stop short is not allowed to say "nothing".
                //
                // This count is what authorises deleting the ported art -- the
                // caller deletes on zero -- and a zero meaning "the tree defeated
                // the walk" would be the same number as a zero meaning "nothing
                // reads it". That exact confusion is what the doc comment above
                // describes happening once already, with the reshape counter. So a
                // stopped walk counts as one survivor and names its image: the
                // deletion is blocked and the user is told where to look.
                if (walk.Stopped)
                    here++;

                if (here == 0)
                    continue;
                links += here;
                images.Add($"{file.Name}/{image.Name}");
            }
        }
        return (links, images);
    }

    /// <summary>
    /// Counts split-canvas outlinks under a collection.
    ///
    /// Descends through <see cref="WzWalk"/> and not through
    /// <c>property.WzProperties</c>. This walk is where the uncatchable crash
    /// lived: a UOL hands back the children of whatever it resolves to, so
    /// reactor 2208004.img's <c>1/hit/0/uol = "../0"</c> -- a link to its own
    /// parent -- made this method recurse into the same collection for ever, and
    /// 16,099 frames later the whole editor was gone with every open archive's
    /// unsaved work. Not an exception anybody could catch, no undo entry, no log
    /// line. It was reached from any port at all, because the sweep above reads
    /// every parsed image in the target client.
    /// </summary>
    private static int CountCanvasLinks(WzPropertyCollection? properties, WzWalk walk, int depth)
    {
        if (properties == null)
            return 0;

        int found = 0;
        foreach (WzImageProperty property in properties)
        {
            if (property is WzCanvasProperty
                && property[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty link
                && IsSplitCanvasLink(link.Value))
                found++;
            found += CountCanvasLinks(walk.Into(property, depth), walk, depth + 1);
        }
        return found;
    }

    private static IEnumerable<WzImage> AllImages(WzDirectory dir)
    {
        foreach (WzImage image in dir.WzImages)
            yield return image;
        foreach (WzDirectory sub in dir.WzDirectories)
            foreach (WzImage image in AllImages(sub))
                yield return image;
    }

    /// <summary>
    /// Is this <c>_outlink</c> one the target's client cannot follow?
    ///
    /// The whole feature turns on this one distinction, so it is written once
    /// rather than spelled out at each of the four places that ask. A v232
    /// client resolves an outlink ONE level: "Skill/40000.img/skill/400004114/icon"
    /// is family, image, path-inside — ordinary, and its own bosses do it
    /// (native 8930000 draws out of Mob/2700112.img). Put a directory in
    /// between — "Skill/_Canvas/422.img/skill/4221017/hit/0/8" — and there is
    /// nothing it can do with it: not a blank frame, a crash of the window that
    /// tries to draw it.
    ///
    /// Matched on the <c>_Canvas</c> segment anywhere in the path rather than on
    /// the substring "/_Canvas/", which is what this used to be. MapleLib's own
    /// packer writes the family in front (see WzPackingService: "{category}/
    /// {CANVAS_DIRECTORY_NAME}/{imagePath}/{propertyPath}") so both spellings
    /// agree in practice — but a link that begins "_Canvas/422.img/..." is the
    /// same unreachable canvas, and the substring test called it clean.
    /// </summary>
    private static bool IsSplitCanvasLink(string? link) => CanvasLinkPath.NamesCanvasFolder(link);

    /// <summary>
    /// The archive family a canvas link reaches into — the file to open.
    /// "Skill/_Canvas/40000.img" is Skill.wz, and its numbered siblings, because
    /// Skill001.wz answers to "Skill" too. Empty when the link names no family,
    /// which a link written "_Canvas/40000.img/..." genuinely does not.
    ///
    /// One spelling. There were three: <c>parts[0]</c> guarded by a length test,
    /// <c>parts[0]</c> guarded by a different length test, and a
    /// <c>FirstOrDefault</c> over every segment — so a nested family
    /// ("Map/Back/_Canvas/x.img") produced "Map" from two of them and "Map" from
    /// the third only by accident of ordering, and the answers drifted apart the
    /// moment anything changed.
    /// </summary>
    private static string ArchiveFamilyOf(string? link)
    {
        if (!CanvasLinkPath.TryParse(link, out CanvasLinkPath path))
            return "";

        IReadOnlyList<string> family = path.FamilySegments;
        return family.Count == 0 ? "" : family[0];
    }

    /// <summary>
    /// Every entry <c>Match</c> would delete from the target, given the paths
    /// this port lands at.
    ///
    /// One method for the plan's disclosure and for the apply's delete, so the
    /// two cannot come to mean different things. What they may legitimately
    /// differ on is their input — the plan passes the paths it intends to land
    /// at, the apply passes the paths that landed — and the caller says so out
    /// loud when the two answers disagree.
    ///
    /// Returns an empty list, never null, when Match is off. Empty here is only
    /// ever reached by having looked: every path out of this method is one
    /// something was compared at.
    /// </summary>
    private List<string> MatchRemovals(
        PortPlanRequest request,
        PortKindSpec spec,
        OpenFile target,
        IEnumerable<string> landing,
        Func<EntryIndex> sourceIndex)
    {
        if (!request.Match || !request.Overwrite)
            return new List<string>();

        // The containers the entries land in, taken from the paths themselves.
        //
        // Not EntryLocation.Scope, which answers a different question: it is
        // about whether an id is unique on its own, and only the "string" kind
        // says it is not. Every other kind -- skills included -- reports no scope
        // at all, so keying this on it matched nothing and the tick did nothing
        // for the one case it was asked for.
        HashSet<string> writtenInto = landing
            .Select(WzPath.Parent)
            .Where(c => c != null)
            .Select(c => c!)
            // The archive root is not a container.
            //
            // A mob sits directly in it, so "the container this port wrote into"
            // would be the whole archive, and porting one mob would delete every
            // other mob the source happens not to have. That is not matching a
            // book, it is emptying a client. Only honoured when the whole archive
            // was asked for in the first place.
            .Where(c => c.Contains('/')
                        || string.Equals(request.Scope, "archive", StringComparison.OrdinalIgnoreCase))
            // Never a _Canvas directory, which the rule above lets through
            // precisely because it does contain a slash.
            //
            // A port lands its art there, so "<archive>/_Canvas" is a container
            // this port wrote into by that reading — and the index that is then
            // compared against the source walks into it and reads "422.img" as
            // skill id 422. Everything the source has no such id for gets
            // deleted, which is the art an earlier port left for entries that
            // still link to it, deleted before the reshape has even run. Matching
            // a book means matching a book.
            .Where(c => !WzPath.Split(c).Any(
                            s => s.Equals(WzFileManager.CANVAS_DIRECTORY_NAME,
                                          StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (writtenInto.Count == 0)
            return new List<string>();

        return Index(target, spec).All
            .Where(e => WzPath.Parent(e.Path) is string c && writtenInto.Contains(c))
            .Where(e => !sourceIndex().Contains(e.Scope, e.Id))
            .Select(e => e.Path)
            // Ordinal, so the same port previewed twice lists them the same way.
            // A list whose row order wandered could not be read as a diff.
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Puts the removals on the plan, named the way the TARGET knows them, and
    /// capped the same way the item list is.
    ///
    /// The target's name and not the source's, on purpose: what a person loses is
    /// what their own client called it, and that is also the case where the two
    /// differ most — an id both builds have under different names is exactly the
    /// kind of entry a Match sweeps out.
    /// </summary>
    private void DescribeRemovals(PortPlanDto plan, List<string> removals)
    {
        plan.Removals.Clear();
        plan.RemovalsTruncated = Math.Max(0, removals.Count - MaxListedItems);

        foreach (string path in removals.Take(MaxListedItems))
        {
            WzObject? node = _session.TryResolve(path);
            string leaf = node?.Name ?? WzPath.Split(path).LastOrDefault() ?? "";
            int.TryParse(CanvasLinkPath.StripImageSuffix(leaf), out int id);

            plan.Removals.Add(new PortRemovalDto
            {
                Id = id,
                Name = _strings.NameFor(path, leaf),
                TargetPath = path,
                Container = WzPath.Parent(path) ?? "",
            });
        }
    }

    /// <summary>The image a landed node belongs to; itself when it is one.</summary>
    private static WzImage? OwningImage(WzObject? node) =>
        node as WzImage ?? (node as WzImageProperty)?.ParentImage;

    /// <summary>The properties to walk from a landed node, whichever kind it is.</summary>
    private static WzPropertyCollection? RootsOf(WzObject? node) =>
        node is WzImage image ? image.WzProperties : (node as WzImageProperty)?.WzProperties;

    /// <summary>
    /// Removes the <c>_Canvas</c> art this port copied in, once reshaping has
    /// left nothing pointing at it.
    ///
    /// Scoped to <paramref name="incoming"/> — the names this port wrote — so an
    /// art image that was already in the target when the run started is never
    /// touched, whatever reads it. Combined with the caller's condition (reshape
    /// left zero links behind, and the target was measured as not using the
    /// split form before the port began), the only possible readers were the
    /// images this run wrote, and every one of them has just been rewritten.
    ///
    /// Not recorded as an undo step, which matches the host-drop inside
    /// <see cref="FlattenCanvasArt"/>: this art arrived with the port and goes
    /// away with it, and the batch that undoes the port removes the entries that
    /// were the only reason it was here.
    /// </summary>
    /// <param name="written">
    /// The session paths this run actually wrote, when the caller knows them.
    /// Names alone are not enough on a SECOND port of the same content, and that
    /// case is reachable and unrecoverable: <see cref="TargetUsesCanvasDirectory"/>
    /// excludes <paramref name="incoming"/> from its numerator so that a port's
    /// own leavings cannot be read back as "this client uses the split form" —
    /// and on a re-port the target's <c>_Canvas</c> holds art under exactly the
    /// names being written again, so the ratio reads zero, the client is judged
    /// flat, and the sweep runs. The per-archive re-check cannot catch it, because
    /// the exclusion that stabilises the measurement is the same one that hides
    /// the art about to be deleted. This is outside the undo batch, so the answer
    /// has to be "did THIS run put that image there", which only a path can say.
    /// Null keeps the old name-only behaviour, which is what the unit tests
    /// exercise.
    /// </param>
    /// <param name="pruneEmpty">
    /// Whether to detach a <c>_Canvas</c> directory this left with nothing in it.
    /// False from the port, and see the call site for why: an undo step holds the
    /// directory it has to put an image back into, and detaching it turns a
    /// working undo into one that succeeds and restores nothing.
    /// </param>
    /// <param name="keep">
    /// Directories that must never be detached however empty they end up, given
    /// as session paths. The port passes the <c>_Canvas</c> directories that were
    /// in the client before it started: an undo step restoring an image this run
    /// overwrote holds the directory it has to put it back into, and detaching
    /// that turns a working undo into one that succeeds and restores nothing.
    /// A directory this run created itself carries no such step — the only undo
    /// it has is its own creation, whose replay removes it anyway.
    /// </param>
    internal static (int Dropped, long Bytes) DropPortedCanvasArt(
        OpenFile target, HashSet<string> incoming, HashSet<string>? written = null,
        bool pruneEmpty = true, HashSet<string>? keep = null)
    {
        WzDirectory? root = target.WzFile?.WzDirectory;
        if (root == null)
            return (0, 0);

        int dropped = 0;
        long bytes = 0;
        List<(WzDirectory Dir, string Path)> emptied = new();

        void Walk(WzDirectory dir, string path, bool underCanvas)
        {
            bool here = underCanvas || dir.Name.Equals(
                WzFileManager.CANVAS_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase);

            foreach (WzDirectory sub in dir.WzDirectories.ToList())
                Walk(sub, WzPath.Child(path, sub.Name), here);

            if (here)
            {
                foreach (WzImage image in dir.WzImages.ToList())
                {
                    if (!incoming.Contains(image.Name))
                        continue;
                    if (written != null && !written.Contains(WzPath.Child(path, image.Name)))
                        continue;
                    bytes += image.BlockSize;
                    dir.RemoveImage(image);
                    dropped++;
                }
                if (dir.WzImages.Count == 0 && dir.WzDirectories.Count == 0)
                    emptied.Add((dir, path));
            }
        }
        Walk(root, target.Id, false);

        // An empty _Canvas is still a _Canvas as far as the next port's
        // measurement is concerned, and one was found sitting in this client's
        // Mob.wz doing exactly that.
        if (pruneEmpty)
        {
            foreach ((WzDirectory dir, string path) in emptied)
            {
                if (keep != null && keep.Contains(path))
                    continue;
                if (dir.Parent is WzDirectory parent)
                    parent.RemoveDirectory(dir);
            }
        }

        if (dropped > 0)
            target.Dirty = true;

        return (dropped, bytes);
    }

    /// <summary>
    /// Every <c>_Canvas</c> directory in one archive, as session paths.
    ///
    /// Taken before a port writes anything, so the sweep afterwards can tell a
    /// directory it created itself from one the client already had.
    /// </summary>
    internal static void CollectCanvasDirectories(OpenFile file, HashSet<string> into)
    {
        WzDirectory? root = file.WzFile?.WzDirectory;
        if (root == null)
            return;

        void Walk(WzDirectory dir, string path)
        {
            if (dir.Name.Equals(WzFileManager.CANVAS_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase))
                into.Add(path);
            foreach (WzDirectory sub in dir.WzDirectories)
                Walk(sub, WzPath.Child(path, sub.Name));
        }
        Walk(root, file.Id);
    }

    /// <summary>
    /// Whether this client keeps canvas art in a <c>_Canvas</c> directory of its
    /// own, rather than inside the image that draws it.
    ///
    /// Newer clients split a mob or an equip in two: the entry keeps its
    /// properties and every frame becomes a canvas <c>_outlink</c> into
    /// <c>Mob/_Canvas/8881710.img</c>. Older ones store the pixels inline and
    /// only ever <c>_outlink</c> one level, at a sibling image --
    /// <c>Mob/2700112.img/attack2/info/hit/0</c> is ordinary in both.
    ///
    /// Copying the split form into a client that has never used it produces
    /// content that is correct by every check this service makes and still is
    /// not what the game can read. Measured on a v232 target: 2,759 images in
    /// Mob.wz, of which the only two under <c>_Canvas</c> were the two a port
    /// had just written, and the only <c>_Canvas</c> in a 45,401-image
    /// Character.wz held one imported item. Both crashed the client on use.
    ///
    /// <paramref name="incoming"/> is what this port is writing, excluded so the
    /// answer does not flip after the first run and leave the second import
    /// shaped differently from the first.
    /// </summary>
    internal static bool TargetUsesCanvasDirectory(OpenFile target, HashSet<string> incoming)
    {
        WzDirectory? root = target.WzFile?.WzDirectory;
        if (root == null)
            return true;   // nothing to go on: leave the art exactly as it came

        int canvasImages = 0, total = 0;
        void Walk(WzDirectory dir, bool underCanvas)
        {
            bool here = underCanvas || dir.Name.Equals("_Canvas", StringComparison.OrdinalIgnoreCase);
            foreach (WzImage image in dir.WzImages)
            {
                total++;
                if (here && !incoming.Contains(image.Name))
                    canvasImages++;
            }
            foreach (WzDirectory sub in dir.WzDirectories)
                Walk(sub, here);
        }
        Walk(root, false);

        // A proportion, not a count, and that is the whole point.
        //
        // Counting said "this client uses _Canvas" the moment one existed --
        // including the one the previous port had just created. The second
        // import then came out shaped differently from the first, for a reason
        // no user could see. What actually separates the two cases is scale: a
        // client built on the split form has roughly one _Canvas image per
        // entry, while what a port leaves behind is a handful. Measured on the
        // v232 target this was found on: 2 of 2,759 images in Mob.wz, and 1 of
        // 45,401 in Character.wz -- both under a tenth of one percent.
        return total > 0 && canvasImages * 20 >= total;
    }

    /// <summary>
    /// Which canvas encodings the target client's own art already uses.
    ///
    /// Sampled rather than swept: the answer only has to be a vocabulary, and
    /// parsing every image of a 1.4 GB archive to learn it would cost more than
    /// the port. Images are taken in archive order until enough canvases have
    /// been seen, which in practice is a handful of entries.
    ///
    /// This sampler runs at PLAN time, so it must not mutate what it reads, and
    /// the previous one did: it parsed every unparsed image and unparsed it
    /// again in a finally, and <c>UnparseImage</c> is not an undo — it clears
    /// the property collection outright. For a disk-backed pristine image that
    /// restores the exact prior state, but for an image whose in-memory tree is
    /// the only copy of its content — one flagged <c>Changed</c> the way this
    /// app's own edits and a just-applied port leave them, or one built in
    /// memory with no reader behind it — it destroys the content, which is how
    /// merely PREVIEWING a second port changed what the target enumerated
    /// (caught by <c>Map_PortedTwice_WritesNothingTheSecondTimeAndChangesNothing</c>).
    ///
    /// The rule now mirrors <c>MapRoundTrip.LoadVerified</c>'s state
    /// restoration: an image that is already parsed or is <c>Changed</c> is read
    /// exactly as it stands and its state is not touched; only an image this
    /// sampler itself parsed cold off a reader — where the parse is provably the
    /// only source of the properties — is unparsed again, which IS its prior
    /// state. An image that cannot be parsed contributes nothing and is left
    /// exactly as found.
    /// </summary>
    /// <summary>
    /// How many canvases among the parts this port will write are in a format
    /// outside the target's sampled vocabulary, and which formats those are.
    ///
    /// A plan-time count of the frames <see cref="RetargetCanvasFormats"/> will
    /// have to re-encode. Read off the SOURCE side, which the plan may parse and
    /// never mutates; bounded by the same walk guard and canvas ceiling the
    /// sampler uses, and never entered at all for a bulk port (see the call
    /// site's gate), where the plan does not parse entries.
    /// </summary>
    private (int Count, SortedSet<string> Formats) ForeignCanvasFormats(
        List<PortItemDto> items, HashSet<WzPngFormat> supported)
    {
        int count = 0;
        int seen = 0;
        SortedSet<string> formats = new(StringComparer.Ordinal);

        foreach (PortPartDto part in DistinctParts(items))
        {
            if (!part.WillWrite || part.SourcePath == null)
                continue;
            if (seen >= FormatSampleCanvases)
                break;

            WzObject? node = _session.TryResolve(part.SourcePath);
            if (node == null)
                continue;
            if (node is WzImage image)
            {
                try { WzSessionService.EnsureParsed(image); }
                catch { continue; }
            }

            WzWalk guard = new();
            void Scan(WzImageProperty property, int depth)
            {
                foreach (WzImageProperty child in guard.Into(property, depth) ?? new List<WzImageProperty>())
                {
                    if (child is WzCanvasProperty canvas && canvas.PngProperty is { } png)
                    {
                        seen++;
                        if (!supported.Contains(png.Format))
                        {
                            count++;
                            formats.Add(png.Format.ToString());
                        }
                    }
                    Scan(child, depth + 1);
                }
            }

            if (node is WzCanvasProperty own && own.PngProperty is { } topPng)
            {
                seen++;
                if (!supported.Contains(topPng.Format))
                {
                    count++;
                    formats.Add(topPng.Format.ToString());
                }
            }
            foreach (WzImageProperty property in ChildrenOf(node) ?? new WzPropertyCollection(null))
                Scan(property, 0);
        }
        return (count, formats);
    }

    /// <summary>The sampled vocabulary, through the generation-keyed cache.</summary>
    private HashSet<WzPngFormat> TargetFormats(OpenFile target)
    {
        int structure = _session.StructureGeneration;
        if (_formatVocabulary.TryGetValue(target.Id, out (int Structure, HashSet<WzPngFormat> Formats) cached)
            && cached.Structure == structure)
        {
            return cached.Formats;
        }

        HashSet<WzPngFormat> formats = TargetCanvasFormats(target);
        if (_formatVocabulary.Count >= MaxCachedIndexes)
            _formatVocabulary.Clear();
        _formatVocabulary[target.Id] = (structure, formats);
        return formats;
    }

    private static HashSet<WzPngFormat> TargetCanvasFormats(OpenFile target)
    {
        HashSet<WzPngFormat> seen = new();
        WzDirectory? root = target.WzFile?.WzDirectory;
        if (root == null)
            return seen;

        int canvases = 0, images = 0;
        void Walk(WzDirectory dir)
        {
            foreach (WzImage image in dir.WzImages)
            {
                if (canvases >= FormatSampleCanvases || images >= FormatSampleImages)
                    return;

                // Whether the in-memory tree holds content that no reader can
                // put back. Parsed covers an image something has opened (its
                // property objects may be referenced elsewhere); Changed covers
                // the edit-without-parse shape WzNodeFactory.MarkChanged
                // produces and a ParseImage that would short-circuit anyway.
                bool held = image.Parsed || image.Changed;
                bool parsedHere = false;
                if (!held)
                {
                    try { parsedHere = image.ParseImage(); }
                    catch
                    {
                        // No reader, or an unreadable block: nothing to learn,
                        // and — the point — nothing was disturbed. The old
                        // sampler's finally unparsed this image anyway, which
                        // for an in-memory tree was the destruction itself.
                    }
                    if (!parsedHere)
                        continue;
                }

                try
                {
                    images++;
                    // Had no bound of any kind, so the same self-referential UOL
                    // that killed the sweep killed this too -- and this one runs
                    // while merely SAMPLING a client's canvas formats. Refusing to
                    // enter a link also keeps the sample honest: a format read
                    // through a UOL is another image's, counted as this one's.
                    WzWalk guard = new();
                    void Scan(WzImageProperty property, int depth)
                    {
                        foreach (WzImageProperty child in guard.Into(property, depth) ?? new List<WzImageProperty>())
                        {
                            if (child is WzCanvasProperty canvas && canvas.PngProperty is { } png)
                            {
                                seen.Add(png.Format);
                                canvases++;
                            }
                            Scan(child, depth + 1);
                        }
                    }
                    foreach (WzImageProperty property in image.WzProperties)
                        Scan(property, 0);
                }
                catch { /* an image that will not walk tells us nothing either way */ }
                finally
                {
                    // Only what this sampler itself parsed cold. Unparsing here
                    // returns the image to exactly the state it was found in:
                    // unparsed, empty property collection, reader intact.
                    if (parsedHere)
                        image.UnparseImage();
                }
            }
            foreach (WzDirectory sub in dir.WzDirectories)
            {
                if (canvases >= FormatSampleCanvases || images >= FormatSampleImages)
                    return;
                Walk(sub);
            }
        }
        Walk(root);
        return seen;
    }

    /// <summary>
    /// Re-encodes any canvas the target client has no vocabulary for.
    ///
    /// A canvas carries its compression format, and a port copies it across
    /// unchanged -- which is right until the two clients do not share it. A
    /// v232 source stores boss art as Format4098, which is 0x1000 + 2: BC7,
    /// a DirectX 11 block format. Copied into a client that renders through
    /// DirectX 9 it cannot become a texture, so the entry loads without
    /// complaint and draws nothing. That is not a crash and not a missing node,
    /// which is what made it so hard to see: every structural check passes.
    ///
    /// Measured on the client this was found on -- 2,742 native images, 95,456
    /// canvases -- the whole vocabulary is Format1 (89,798), Format2050
    /// (3,764), Format1026 (1,893) and Format2 (1). Format4098 does not occur
    /// once. The ported boss was 1,001 canvases of nothing but Format4098.
    ///
    /// MapleLib chooses the replacement, and its repertoire is exactly the
    /// pre-BC7 set: Dxt3, Dxt5, Bgra32, Bgra5551, Bgra4444, Bgr565. It cannot
    /// pick the format that caused this.
    ///
    /// It does NOT guarantee a canvas can be encoded: the DXT encoders throw on
    /// any sprite whose sides are not multiples of four. Such a canvas is left
    /// in the format it arrived in, and if the target cannot read that format
    /// those frames draw nothing -- so the count is carried back to the caller
    /// and stated, rather than swallowed.
    ///
    /// Forcing one format instead was tried and was wrong twice over. Bgra4444
    /// is two bytes a pixel against BC7's one, which took Mob.wz to 2.12 GiB --
    /// past the 2 GiB the server can memory-map, so it would not boot. Dxt5 is
    /// one byte a pixel but refuses any canvas whose sides are not multiples of
    /// four, which most sprites are not.
    ///
    /// This is the one step in a port that is not lossless. The alternative is
    /// art that does not appear at all.
    /// </summary>
    private static int RetargetCanvasFormats(
        WzObject node, HashSet<WzPngFormat> supported, out int refused)
    {
        refused = 0;
        if (supported.Count == 0)
            return 0;

        WzPropertyCollection? roots = RootsOf(node);
        if (roots == null)
            return 0;

        // A local rather than the out parameter: the walk below is a nested
        // function and C# will not let one close over an out.
        int couldNot = 0;


        int changed = 0;

        // One node, then everything under it -- the same correction
        // FlattenCanvasArt needed, and for a worse reason here.
        //
        // This tested a property's CHILDREN, so for a landed skill entry the
        // canvases that are DIRECT children of it -- icon, iconMouseOver,
        // iconDisabled -- were never re-encoded. Those are exactly the ones the
        // reshape has just filled by SetCompressedBytes(..., pixels.Format),
        // which carries the SOURCE format across verbatim: on a v233 -> v232 port
        // that is Format4098, BC7, a DirectX 11 block format in a client that
        // renders through DirectX 9. The icons loaded, drew nothing, and `refused`
        // stayed 0 so not a word was said. The same gap one level up skipped a
        // canvas sitting directly under an image root, which is the ordinary shape
        // of a _Canvas atlas.
        //
        // Descends through WzWalk, which had no bound here at all. This pass
        // WRITES -- it re-encodes pixels and clears mag -- so following a UOL into
        // another image meant re-encoding a canvas nobody asked to touch, in an
        // archive this port may not even be writing to, and doing it again on
        // every route that reached it. Stopping at the link is the correct read of
        // "the frames this entry brought with it", and it is also what stops the
        // walk running off the stack.
        WzWalk guard = new();
        void Consider(WzImageProperty property, int depth)
        {
            {
                if (property is WzCanvasProperty canvas
                    && canvas.PngProperty is { } png
                    && !supported.Contains(png.Format))
                {
                    try
                    {
                        using System.Drawing.Bitmap? art = png.GetImage(false);
                        if (art != null)
                        {
                            // The PNG setter, not SetCompressedBytes.
                            //
                            // SetCompressedBytes stores what it is given as the
                            // stored block, which is zlib-compressed and, in a
                            // listWz archive, wrapped again. Handing it the raw
                            // output of an encoder writes uncompressed bytes into
                            // a slot that will be inflated on read: MapleLib's own
                            // reader then fails the archive with "Invalid listWz
                            // PNG block size", and that is what shipped. This
                            // setter runs the whole pipeline -- pick a format,
                            // encode, compress, wrap -- the way a canvas written
                            // from scratch would be.
                            //
                            // It picks the format itself, from Dxt3, Dxt5,
                            // Bgra32, Bgra5551, Bgra4444 and Bgr565, honouring the
                            // multiple-of-four rule the DXT encoders require. BC7
                            // is not among them, which is the whole point: it
                            // cannot reintroduce the format that started this.
                            png.PNG = art;

                            // Magnification does not survive a re-encode, and the
                            // setter does not clear it. SetCompressedBytes does
                            // (WzPngProperty, "these bytes are the whole picture at
                            // the size just given"), and this is the same
                            // situation: the bitmap handed back by GetImage is at
                            // the stored width and height with no scaling applied,
                            // so a leftover mag makes the client stride the rows
                            // wrong -- mag 8 on a 288-wide canvas gives it a row
                            // width of 1. Measured: a v232 Skill.wz holds 4,043
                            // canvases with a non-zero mag, and one 422.img in this
                            // user's own archives carries 67 of them as an artefact
                            // of an old writer bug.
                            png.Mag = 0;

                            // What came out, not what went in.
                            //
                            // The encoder picks the format from the pixels: it is
                            // a fixed function of the content, not something this
                            // can steer, and it can perfectly well land on another
                            // format the target has none of. Measured on this port
                            // with a vocabulary that (wrongly) excluded Format2050:
                            // 47 of 48 frames came back AS Format2050 and every one
                            // was counted a success, log line and all. And any
                            // canvas below 64x64 can never be DXT at all, so the
                            // six icons came back Format257 -- a format occurring
                            // zero times in 118,831 canvases of the target's own
                            // art. `refused` counted throws only, so the warning
                            // that exists for exactly this said nothing.
                            if (!supported.Contains(png.Format))
                                couldNot++;
                            else
                                changed++;
                        }
                    }
                    catch
                    {
                        // Left in the format it came in, and counted. A frame left
                        // in a format the target cannot read draws nothing -- which
                        // the caller now states rather than leaving to be found in
                        // game.
                        couldNot++;
                    }
                }
            }

            foreach (WzImageProperty child in guard.Into(property, depth)?.ToList() ?? new List<WzImageProperty>())
                Consider(child, depth + 1);
        }

        foreach (WzImageProperty property in roots.ToList())
            Consider(property, 0);

        refused = couldNot;
        return changed;
    }

    /// <summary>
    /// Rewrites a copied image's <c>_Canvas</c> links into the shape a client
    /// without a <c>_Canvas</c> directory can read.
    ///
    /// Only touched when <see cref="TargetUsesCanvasDirectory"/> says the target
    /// has no such directory of its own. For every ordinary client this does
    /// nothing at all -- the split form is smaller and is what the source
    /// intended, so it is kept wherever the target can read it.
    ///
    /// One image gets the pixels and everything else points at it. The entry
    /// whose own name matches the art image -- 8881710.img and
    /// _Canvas/8881710.img -- takes the frames inline and the now-unreferenced
    /// art image is dropped. Every other entry sharing that art keeps an
    /// <c>_outlink</c>, rewritten one level to "Mob/8881710.img/stand/0", which
    /// is exactly what this client's own bosses already do: native 8930000
    /// draws out of Mob/2700112.img.
    ///
    /// Inlining into all of them instead is what the first version did, and a
    /// boss whose four difficulty variants share one 282 MB canvas set became
    /// four private copies of it. Mob.wz went from 1.44 GB to 2.65 GB and the
    /// server could no longer start: it memory-maps the archive, and
    /// FileChannel.map cannot map 2 GiB or more. Duplicating shared art is not
    /// a size question, it is a correctness one.
    ///
    /// The link is resolved through <see cref="ResolveImage"/> rather than
    /// WzCanvasProperty.GetLinkedWzImageProperty, whose <c>_outlink</c> branch
    /// needs HaRepacker's global WzFileManager -- null here, so it falls through
    /// and hands back the canvas it was asked about. Measured: 1,001 frames all
    /// "resolving" to their own 1x1 placeholder. Flattening on that would have
    /// blanked every frame.
    ///
    /// A canvas whose art will not resolve is left exactly as it was.
    /// </summary>
    internal (int Changed, int Left) FlattenCanvasArt(WzObject node, ClientGroup? client) =>
        FlattenCanvasArt(node, client, out _, null);

    internal (int Changed, int Left) FlattenCanvasArt(WzObject node, ClientGroup? client, out int blank) =>
        FlattenCanvasArt(node, client, out blank, null);

    /// <param name="blank">
    /// Links that are already the one-level shape this client can follow, and
    /// still name a frame that is not there. Counted apart from
    /// <c>Left</c> because the two failures are not the same size: an
    /// unreachable <c>_Canvas</c> link takes the client down, and this one draws
    /// nothing. Reported, never repaired by guessing at a nearby id.
    /// </param>
    /// <param name="portWrote">
    /// The nodes this port has just written, when the caller asked to MATCH the
    /// source, or null for an ordinary merge.
    ///
    /// This is what turns "a frame exists at that path" into "the SOURCE's frame
    /// exists at that path". The one-level rewrite below points a copied canvas
    /// at art the target already holds, which is exactly right for a merge and
    /// exactly wrong for a match: the target's shared icon book is routinely an
    /// older generation of the same ids, so the rewrite lands the port on the
    /// client's OLD picture and reports success. Measured: Meso Explosion came
    /// through with the new icon and the old mouse-over, because 'icon' used an
    /// id the target lacks (inlined, right) while 'iconMouseOver' matched an id
    /// it had (rewritten, wrong art).
    ///
    /// When this is non-null a host is only accepted if the frame it answers
    /// with sits inside something this same port wrote. Everything else inlines
    /// the source's pixels. Sharing survives where it is honest -- one ported
    /// entry pointing at another ported entry's art is a host this port wrote --
    /// so the 2 GiB duplication this branch exists to prevent does not come
    /// back.
    ///
    /// THE BOUNDARY, and it is not negotiable: Match is scoped to the BOOK being
    /// ported, never to the client. The target's shared art book serves hundreds
    /// of entries this port is not touching. The rule here is "ported entries
    /// must not POINT AT it" and nothing more -- it is never read as "remove the
    /// old generation's art", which would delete frames other entries still
    /// draw, outside this port's undo batch. Nothing in this method deletes
    /// anything the port did not itself bring in (see <c>consumed</c>, which is
    /// gated on the art sitting under a <c>_Canvas</c> directory).
    /// </param>
    internal (int Changed, int Left) FlattenCanvasArt(
        WzObject node, ClientGroup? client, out int blank, IReadOnlySet<WzObject>? portWrote)
    {
        blank = 0;
        WzImage? owner = OwningImage(node);
        WzPropertyCollection? roots = RootsOf(node);
        if (owner == null || roots == null)
            return (0, 0);

        // Locals rather than the out parameter: the walk below is a set of nested
        // functions and C# will not let one close over an out.
        int changed = 0, left = 0, unresolved = 0;
        Dictionary<string, WzImage?> resolved = new(StringComparer.OrdinalIgnoreCase);
        HashSet<WzImage> consumed = new();

        WzImage? Find(string path)
        {
            // A null client used to return 0 from the top of this method, which
            // read as "nothing needed reshaping" and was indistinguishable from
            // success. It is not: the target archive simply is not part of a
            // group this service recognises, so nothing can be resolved and
            // every link stays in the split form. Fall through instead, resolve
            // nothing, and let the counting below report what was left behind.
            if (client == null)
                return null;
            if (resolved.TryGetValue(path, out WzImage? cached))
                return cached;
            WzImage? found = ResolveImage(client, path)?.Node as WzImage;
            if (found != null)
                WzSessionService.EnsureParsed(found);
            resolved[path] = found;
            return found;
        }

        // The canvas inside the art image that actually holds pixels.
        //
        // A canvas moved into a _Canvas image keeps its own '_inlink': the packer
        // clones the original whole and strips the links only off the placeholder
        // it leaves behind (WzPackingService.ReplaceCanvasWithOutlink removes
        // _inlink and _outlink from the placeholder, never from the clone). And
        // MapleLib resolves an '_inlink' against the nearest parent image, which
        // for that clone is the _Canvas image itself, so the chain is followable
        // here — but only if it is followed. Reading PngProperty straight off such
        // a canvas hands back the 1x1 transparent placeholder that stands in for
        // the pixels, and inlining THAT is a frame that draws nothing with every
        // structural check passing: the same invisible-frame failure the
        // GetCompressedBytesForExtraction comment below is about, arrived at from
        // the other end.
        //
        // Bounded and cycle-checked because nothing in the format forbids an
        // '_inlink' loop.
        //
        // Whether what it lands on carries pixels is the CALLER's question and is
        // deliberately not asked here. Folding the two together looked tidy and
        // silently disabled the one-level rewrite: reading an image for its
        // STRUCTURE — does this book hold that frame at all — is not the same as
        // reading it for its bytes, and the host branch only ever needed the
        // former.
        static WzCanvasProperty? Frame(WzImage art, string rest)
        {
            HashSet<string> hops = new(StringComparer.OrdinalIgnoreCase);
            string at = rest;
            for (int hop = 0; hop < 8; hop++)
            {
                if (!hops.Add(at) || art.GetFromPath(at) is not WzCanvasProperty found)
                    return null;
                if (found[WzCanvasProperty.InlinkPropertyName] is not WzStringProperty inlink
                    || string.IsNullOrWhiteSpace(inlink.Value))
                    return found;
                at = inlink.Value;
            }
            return null;
        }

        // Does that image hold this frame, in a shape worth pointing at?
        //
        // GetFromPath is a pure name walk: it follows no UOL, no '_inlink' and no
        // '_outlink', so "there is a WzCanvasProperty at that path" was never
        // quite the question. A packing-service placeholder IS a
        // WzCanvasProperty — a 1x1 transparent PNG with an '_outlink' beside it —
        // so if the host book is itself split, rewriting one level pointed the
        // entry at a placeholder that points straight back into _Canvas: two hops,
        // the original unreachable canvas, counted as a success. Following
        // '_inlink' on the way is what the client does; refusing a frame that is
        // itself an '_outlink' is what it cannot do.
        static bool HoldsRealFrame(WzImage image, string rest) =>
            Frame(image, rest) is { } found
            && found[WzCanvasProperty.OutlinkPropertyName] == null;

        // Did this port write the node that frame lives in?
        //
        // Asked of the frame and then of everything above it, because what the
        // port wrote is an entry or an image and what is being judged is a canvas
        // somewhere inside one. Reference identity, not paths: the set is built
        // from the very nodes Write() landed, so a name that merely spells the
        // same is not mistaken for the thing itself -- which is the whole failure
        // being fixed here, one level up.
        //
        // A null set is a merge, where reusing the target's own art is the point.
        static bool FromThisPort(WzObject? at, IReadOnlySet<WzObject>? written)
        {
            if (written == null)
                return true;
            for (WzObject? up = at; up != null; up = up.Parent)
            {
                if (written.Contains(up))
                    return true;
            }
            return false;
        }

        // Considers ONE node, then everything under it.
        //
        // This used to test a property's CHILDREN and recurse, which is the same
        // walk with its root left out — and the root is not a formality in either
        // shape this is handed. MapleLib's packer replaces any large canvas with
        // an outlink including one sitting directly in an image
        // (FindLargeCanvasPropertiesInImage runs the test on each top-level
        // property itself), so a top-level frame was never looked at; and for a
        // ported skill the node handed in IS the entry, so its own canvases were
        // the one thing never examined. The second of those was fixed by handing
        // the entry to Walk rather than iterating it, which left the first.
        //
        // Descends through WzWalk, which had no bound here at all either. Reshape
        // below REWRITES an outlink, so following a UOL meant reshaping a link that
        // belongs to another image -- and `left`, the count of dead frames this
        // returns, was counting other entries' canvases as this port's casualties.
        WzWalk guard = new();
        void Consider(WzImageProperty property, int depth)
        {
            if (property is WzCanvasProperty canvas)
            {
                if (canvas[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty link
                    && !string.IsNullOrWhiteSpace(link.Value))
                {
                    if (IsSplitCanvasLink(link.Value))
                        Reshape(canvas, link);
                    else
                        Check(link);
                }
                // An '_inlink' the copy brought with it, checked against the image
                // it has actually landed in.
                //
                // This pass read nothing but '_outlink', which is right for a
                // split source — the packer converts every inlink away, and a v264
                // split Skill.wz has none at all. It is wrong for a classic one,
                // and those are what people port FROM as often as not. An inlink
                // is image-root-relative, so copying one property out of a shared
                // book carries "skill/4221016/hit/0/0" across verbatim and it then
                // resolves against the TARGET's book: a different generation of
                // that id draws the wrong picture, and an absent one draws none.
                // Measured in a real v232 422.img: 4221014 carries 49 of these and
                // 4221016 carries 88.
                //
                // Nothing is guessed at. Where the path resolves to a real frame in
                // the book the entry landed in, it is correct by construction — the
                // usual case, because the entry's own subtree came with it. Where
                // it does not, the frame is as dead as an unreachable outlink and
                // is counted as one.
                else if (canvas[WzCanvasProperty.InlinkPropertyName] is WzStringProperty inlink
                         && !string.IsNullOrWhiteSpace(inlink.Value)
                         && !HoldsRealFrame(owner, inlink.Value))
                {
                    left++;
                }
            }

            foreach (WzImageProperty child in guard.Into(property, depth)?.ToList() ?? new List<WzImageProperty>())
                Consider(child, depth + 1);
        }

        // A link that is already one level is the right SHAPE. That is not the
        // same as it resolving.
        //
        // Every canvas link in the measured Shadower closure is the split form —
        // outlink counts of 138, 54, 511 and 699 across books 400, 420, 421 and
        // 422, with zero '_inlink' anywhere — so the moment one is rewritten to
        // "Skill/40000.img/skill/400004114/icon" it is in a shape nothing else
        // checks. A source entry can arrive carrying one of these already, and
        // the target's book of that name is routinely a different generation:
        // the same 400004666..400004669 mismatch that made the blind one-level
        // rewrite wrong in the first place. Those frames draw nothing, which is
        // survivable where a dead _Canvas link is not — so they are counted apart
        // and reported, never repaired by guessing.
        void Check(WzStringProperty link)
        {
            // Nothing can be looked up, so nothing can be said. Counting these as
            // dead when the target simply is not a client here would put a number
            // on a question that was never asked -- and the refusal that fires for
            // a null client already covers the case that matters.
            if (client == null)
                return;

            // Cut by CanvasLinkPath, like every other link in this file. The
            // hand-rolled FindIndex this replaced agreed with it on the measured
            // links and stopped agreeing on the two shapes the parser exists for.
            if (!CanvasLinkPath.TryParse(link.Value, out CanvasLinkPath path)
                || path.Remainder.Length == 0)
            {
                return;
            }

            string rest = path.Remainder;
            WzImage? at = Find(path.ImagePath);

            // Its own image is not looked up through the client at all: for a
            // ported entry that is the book it has just landed in, and it holds
            // the frame or it does not.
            if (at == null && owner.Name.Equals(path.Image, StringComparison.OrdinalIgnoreCase))
                at = owner;
            if (at == null || !HoldsRealFrame(at, rest))
                unresolved++;
        }

        void Reshape(WzCanvasProperty canvas, WzStringProperty link)
        {
            // One parser, and it is the shared one.
            //
            // `Family` is everything above the image except the _Canvas level.
            // `parts[0]` was standing in for this and is only right for the
            // two-segment shape "Skill/_Canvas/422.img". A link that begins at
            // _Canvas has no family in it at all and would have been rewritten to
            // "_Canvas/422.img/...", which is the same unreachable canvas it
            // started as, reported as a success; and a nested one —
            // "Map/Back/_Canvas/snowyDarkrock.img/back/0", which is the shape
            // WzFileManager.NormaliseWzCanvasDirectory exists to handle — would
            // have lost its Back level.
            //
            // Three refusals collapse into one condition, and each was already a
            // `left++` before: no image segment at all, nothing below the image
            // to name a frame, and no family to point the one-level form at. Left
            // for the count below rather than guessed.
            if (!CanvasLinkPath.TryParse(link.Value, out CanvasLinkPath path)
                || path.Remainder.Length == 0
                || path.Family.Length == 0)
            {
                left++;
                return;
            }

            {
                string family = path.Family;
                string artName = path.Image;
                string artImage = path.ImagePath;
                string rest = path.Remainder;

                bool iAmTheHost = owner.Name.Equals(artName, StringComparison.OrdinalIgnoreCase);

                // The frame, not just the image.
                //
                // This used to rewrite one level as soon as an image of
                // that name existed anywhere in the target -- which is a
                // different question from whether that image holds this
                // frame. Porting a v233 Shadower into a v232 client, five
                // skills drew out of _Canvas/40000.img at ids 400004666
                // to 400004669; the target's own Skill/40000.img is a
                // different generation of the same book and has 400004104
                // and 400004114 instead. Rewriting one level pointed all
                // five at ids that are not there, which is the same dead
                // canvas the split form was, reached by a shorter path.
                //
                // Checking the frame also replaces what `incoming` was
                // guarding. That existed because the reshape once ran
                // per-part, so a host written later than its sibling was
                // not findable yet and the sibling inlined a private copy.
                // The pass now runs after every part has landed, so a host
                // that is coming is a host that is already here, and
                // "does it hold this frame" answers the duplication
                // question and the correctness one at once.
                //
                // The book a link names is NOT the book the entry sits
                // in, and nothing here may assume it is. Measured on the
                // Shadower closure: 422.img's skill/4220011/mob/0 draws
                // from "Skill/_Canvas/40004.img/skill/400040000/mob/0",
                // and the four books between them reach twelve distinct
                // art images -- 421, 422, 411, 400, 412, 112, 420, 40000,
                // 40004, 2400, 320 and 122. iAmTheHost is therefore a
                // narrow statement about one of those twelve, not the
                // usual case.
                WzImage? host = iAmTheHost ? null : Find($"{family}/{artName}");

                // Whose frame is it.
                //
                // "A frame exists here" was the only question asked, and it is
                // half of one. Under Match the answer also has to be the SOURCE's
                // frame, and the only art on the target side that is the source's
                // is art this same port put there -- so a host that predates the
                // port is refused and the pixels are inlined instead. Under a
                // merge portWrote is null, FromThisPort is true by construction,
                // and this is the branch it always was.
                //
                // Refusing costs a copy of a frame the target already had a
                // version of. That is the intended trade: a private copy of the
                // right picture beats a shared link to the wrong one, and the
                // shared-art case that made this branch necessary is untouched,
                // because a host this port wrote still answers here.
                WzCanvasProperty? hostFrame = host == null ? null : Frame(host, rest);
                bool hostAnswers =
                    hostFrame != null
                    && hostFrame[WzCanvasProperty.OutlinkPropertyName] == null
                    && FromThisPort(hostFrame, portWrote);

                if (hostAnswers)
                {
                    // Someone else holds the pixels. One level, which is
                    // the only shape this client resolves.
                    link.Value = $"{family}/{artName}/{rest}";
                    owner.Changed = true;
                    changed++;
                }
                // The art image by its own segments, not by a substring cut
                // at the first ".img" in the whole link. The two agree for
                // every link measured, and they stop agreeing the moment a
                // directory or a property is named something containing
                // ".img" — at which point the cut lands mid-path and the
                // frame silently fails to resolve.
                //
                // A source frame that is itself an '_outlink' is not a source at
                // all: what PngProperty hands back for one of those is the 1x1
                // transparent placeholder the packer leaves in the slot, and
                // inlining that blanks the frame while every check passes. Left
                // for the count instead, which is the honest answer to art this
                // cannot reach.
                else if (Find(artImage) is { } art
                         && Frame(art, rest) is { } source
                         && source[WzCanvasProperty.OutlinkPropertyName] == null
                         && source.PngProperty is { } pixels
                         && canvas.PngProperty is { } slot
                         && !ReferenceEquals(source, canvas))
                {
                    // ForExtraction, not GetCompressedBytes.
                    //
                    // GetCompressedBytes hands back the block exactly as
                    // the SOURCE archive stores it, listWz XOR wrapping
                    // and all, and SetCompressedBytes then nulls the
                    // destination's reader -- so the key needed to
                    // unwrap it is gone the moment it lands. MapleLib's
                    // own WzLinkResolver.CopyCanvasData calls this out
                    // as critical for exactly that reason. What it
                    // produces is a structurally perfect node whose
                    // pixels the client cannot decode: invisible, with
                    // every check passing.
                    slot.SetCompressedBytes(
                        pixels.GetCompressedBytesForExtraction(false),
                        pixels.Width, pixels.Height, pixels.Format);

                    // Magnification is not part of the block, and
                    // SetCompressedBytes deliberately zeroes it -- it
                    // cannot know whether the caller is replacing the
                    // picture or re-encoding the same one. Here we are
                    // copying one canvas onto another, so the source's
                    // scale is part of what is being copied, and dropping
                    // it renders the frame at the wrong size rather than
                    // not at all.
                    slot.Mag = pixels.Mag;

                    canvas.WzProperties!.Remove(link);

                    // Both links, never only the one being removed.
                    // WzCanvasProperty.GetLinkedWzImageProperty tries
                    // '_inlink' FIRST and falls back to '_outlink', so a
                    // canvas left holding an '_inlink' beside the pixels
                    // just written would draw whatever that path names
                    // instead of them: the bytes right, the frame wrong.
                    // MapleLib's own WzLinkResolver.CopyCanvasData clears
                    // both for the same reason.
                    if (canvas[WzCanvasProperty.InlinkPropertyName] is { } strayInlink)
                        canvas.WzProperties!.Remove(strayInlink);

                    // Marked here rather than by the caller once the walk
                    // is over. WzImage.SaveImage writes the ORIGINAL block
                    // bytes back when Changed is false, so a throw part way
                    // through this walk used to leave an image whose links
                    // were gone and whose pixels were inline in memory, and
                    // whose saved copy was the untouched split form -- with
                    // AnyCanvasLinkSurvives reading the memory and reporting
                    // it clean.
                    owner.Changed = true;

                    // Only the host may drop the art, because only the
                    // host knows nobody else still needs it. An entry
                    // inlining as a fallback leaves it where it is.
                    if (iAmTheHost)
                        consumed.Add(art);
                    changed++;
                }
                else
                {
                    // Neither reshape was possible: the host has no such
                    // frame and the art itself would not resolve. The
                    // link stays exactly as it was, which is the right
                    // thing to do to a canvas we cannot read -- but this
                    // client cannot read the link either, so it has to
                    // be counted and said out loud rather than left to
                    // be discovered as a crash.
                    left++;
                }
            }
        }

        // The node itself is handed in whole rather than iterated. For an image
        // that means its top-level properties one at a time; for a property -- a
        // ported skill entry -- it means the entry itself, or its own canvases are
        // the one thing never looked at, which is precisely the frame that has to
        // be reshaped.
        if (node is WzImageProperty entry)
            Consider(entry, 0);
        else
            foreach (WzImageProperty property in roots.ToList())
                Consider(property, 0);

        // The art image has no readers left: this entry took its pixels and
        // everyone else was pointed here. Keeping it would leave the archive
        // carrying both copies, which is how it went over 2 GiB the first time.
        // Only a whole-image reshape may drop the art it consumed.
        //
        // This pass used to run once per image, so  saw every reader in
        // the book and dropping was safe. It now runs once per ENTRY -- that is
        // what makes it reach a skill at all -- and the entries of one book share
        // one art image. The first to inline it is its own host, consumes it and
        // deletes it, and every later entry finds nothing to inline from and
        // leaves its links in the split form. Measured: porting 4221014 and
        // 4221017 together left 30 dead links on 4221017 alone, all naming the
        // _Canvas/422.img that 4221014 had just removed.
        //
        // For an entry the art is left where it is. DropPortedCanvasArt sweeps it
        // afterwards, once every entry has had its turn, and only when nothing
        // still points into a _Canvas directory.
        foreach (WzImage art in node is WzImage ? consumed : Enumerable.Empty<WzImage>())
        {
            if (art.Parent is WzDirectory home
                && home.Name.Equals(WzFileManager.CANVAS_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase))
            {
                home.RemoveImage(art);

                // Sealed, because nothing records this and the archive would
                // otherwise report itself clean with an image missing from it. The
                // same omission on the sweep below let a port delete a 226 MB art
                // book and come back saying OpenFile.Dirty was false, no images
                // were dirty, and the undo entry covered everything.
                if (client?.Files.FirstOrDefault(f => ReferenceEquals(f.WzFile, art.WzFileParent))
                        is { } owning)
                {
                    owning.Dirty = true;
                    _undo.SealFile(owning.Id);
                }
            }
        }

        blank = unresolved;
        return (changed, left);
    }

    private void Write(
        PortPartDto part, bool overwrite,
        Dictionary<PortPartDto, PathStep> containerSteps,
        Dictionary<PortPartDto, string> renames,
        List<string> landed)
    {
        string targetPath = part.TargetPath
            ?? throw new InvalidOperationException("This part has no target to copy into.");
        string parent = WzPath.Parent(targetPath)
            ?? throw new InvalidOperationException($"'{targetPath}' has no parent to copy into.");

        if (part.Kind == "container")
        {
            // Re-checked rather than trusted: containers are shared between
            // entries, so by the time this runs an earlier part may already have
            // created it. Adding twice throws, and a throw here would be recorded
            // as a failure for a container that is present and correct.
            if (_session.TryResolve(targetPath) != null)
                return;

            PathStep step = containerSteps[part];
            _edit.Add(new AddNodeRequest { Path = parent, Name = step.Name, Type = step.Type });
            return;
        }

        string sourcePath = part.SourcePath
            ?? throw new InvalidOperationException("This part has no source to copy from.");

        // A part that has to be renamed on landing is one whose name in the
        // SOURCE means nothing here: a Commodity row is named by its position in
        // the source's own table, and a scenery image is being copied in
        // deliberately under a name the target does not use. Letting Transfer
        // overwrite on that name destroys whatever the target happens to keep
        // there -- measured: porting cash item 5533138 with Replace on wiped
        // Commodity.img/1439, an unrelated equip listing, because 1439 is what
        // the SOURCE calls the row being carried. Whatever this part is meant to
        // replace is dealt with below, by name, deliberately.
        bool renaming = renames.TryGetValue(part, out string? name);

        // The one node this part IS allowed to replace, removed by name BEFORE
        // the copy is made rather than after.
        //
        // Before, because the alternative is what this whole change is about: a
        // copy that has already landed somewhere and a throw afterwards leaves a
        // stray node behind and reports a failure for a write that half
        // happened. Delete records what it removed, and this is inside the
        // port's batch, so one undo puts the target's own row back where it was.
        //
        // Refused rather than worked around when the plan did not say a
        // replacement was happening: renaming around an occupied name is how a
        // row nobody can find gets written.
        if (renaming && _session.TryResolve(WzPath.Child(parent, name!)) != null)
        {
            if (!overwrite || part.Status != "Conflict")
            {
                throw new InvalidOperationException(
                    $"'{WzPath.Child(parent, name!)}' is already taken and this port was not asked to " +
                    "replace it, so the copy has nowhere to land. Nothing was written for this part.");
            }

            _edit.Delete(new[] { WzPath.Child(parent, name!) });
        }

        List<NodeDto> results = _edit.Transfer(new TransferRequest
        {
            Paths = new List<string> { sourcePath },
            TargetPath = parent,
            Move = false,
            // Only ever true for a part the plan called a Conflict, and only when
            // the caller asked. Transfer's other branch renames the incoming node
            // to "<name> copy" on a clash, which for a WZ id is silently useless —
            // the client looks up "02000000" and there is no such node.
            Overwrite = overwrite && !renaming,
        });

        if (results.Count == 0)
            throw new InvalidOperationException("Nothing was copied.");

        // A row that is appended rather than mirrored lands under the source's
        // own name, which is already taken here -- Transfer renames it out of the
        // way and reports where it actually went, and this puts it where the plan
        // said. Both steps are inside the batch, so it is still one Ctrl+Z.
        if (renaming)
        {
            NodeDto renamed = _edit.Rename(results[0].Path, name!);

            // Where it IS, not where it briefly was.
            //
            // This used to record the pre-rename path, which was harmless while
            // the only caller was a Commodity row nothing looks at afterwards.
            // It stopped being harmless the moment a scenery image landed this
            // way: every pass that runs after the write -- the art reshape, the
            // format retarget, Match's "what did this port write" set -- resolves
            // these paths, and a path that no longer names anything reads as a
            // node that was never written.
            landed.Add(renamed.Path);
            return;
        }

        // The landing path is checked rather than assumed. If Transfer ever
        // renamed to avoid a clash the copy would be at "<id> copy", which no
        // client will ever read, and the port would have reported success. The
        // plan is supposed to make that impossible; this is the assertion that
        // says so out loud.
        if (!string.Equals(results[0].Path, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The copy landed at '{results[0].Path}' rather than '{targetPath}', which means the target " +
                "already held that name. Nothing here will use a renamed copy — a client looks the id up by " +
                "name and would never find it.");
        }

        landed.Add(results[0].Path);
    }

    #endregion

    #region Locating

    /// <summary>One level between an archive root and an entry, and what kind of node it is.</summary>
    private sealed record PathStep(string Name, string Type);

    /// <summary>
    /// An entry, wherever it sits: an image at an archive root, an image in a
    /// category directory, or a property inside a shared image.
    /// </summary>
    /// <param name="Steps">The nodes between the archive root and this one.</param>
    /// <param name="Relative">"Cash/0510.img/05100000" — for display and for spelling comparisons.</param>
    /// <param name="Bytes">
    /// Roughly what a clone of this costs. For an image it is the block the
    /// archive stores it in; for a property it is that block shared out among the
    /// image's children, which is an estimate and is labelled as one.
    /// </param>
    /// <param name="Scope">
    /// What this id is unique within: "" for a kind whose ids are unique across
    /// the whole archive family, or the container image's name for one where they
    /// are not. See <see cref="PortKindSpec.IdsAreUniquePerImage"/>.
    /// </param>
    private sealed record EntryLocation(
        int Id,
        OpenFile File,
        string Path,
        WzObject Node,
        IReadOnlyList<PathStep> Steps,
        string Name,
        string Relative,
        long Bytes,
        string Scope = "");

    /// <summary>
    /// Every entry in one archive, keyed by scope and id.
    ///
    /// A plain <c>Dictionary&lt;int, EntryLocation&gt;</c> was the wrong shape for
    /// String.wz, where <c>Npc.img/2100</c> and <c>Map.img/2100</c> are both real
    /// and are not the same thing. Everything else uses one scope — the empty
    /// string — so nothing else pays for the distinction.
    /// </summary>
    private sealed class EntryIndex
    {
        private readonly Dictionary<string, EntryLocation> _entries = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<EntryLocation> All => _entries.Values;
        public int Count => _entries.Count;

        public void TryAdd(EntryLocation entry) => _entries.TryAdd(Key(entry.Scope, entry.Id), entry);

        public void TryAddAll(EntryIndex other)
        {
            foreach (EntryLocation entry in other.All)
                TryAdd(entry);
        }

        public EntryLocation? Get(string scope, int id) =>
            _entries.TryGetValue(Key(scope, id), out EntryLocation? found) ? found : null;

        public bool Contains(string scope, int id) => _entries.ContainsKey(Key(scope, id));

        private static string Key(string scope, int id) => scope + "#" + id;
    }

    /// <summary>
    /// Every entry in one archive, cached against the structural generation.
    ///
    /// The cost is asymmetric and deliberately so. An archive whose images are all
    /// id-named — Mob.wz, Npc.wz, Character.wz/Cap — is a pure name scan, which is
    /// microseconds and parses nothing; that is what keeps a 2,742-image Mob.wz
    /// out of the ten-second parse <see cref="MobService"/> exists to cache.
    /// Only a kind that declares <c>UsesContainers</c> pays to look inside images
    /// whose names are not ids, and only for those images.
    /// </summary>
    private EntryIndex Index(OpenFile file, PortKindSpec spec)
    {
        string key = file.Id + "|" + spec.Kind;
        int structure = _session.StructureGeneration;

        if (_indexes.TryGetValue(key, out (int Structure, EntryIndex Entries) cached)
            && cached.Structure == structure)
        {
            return cached.Entries;
        }

        EntryIndex entries = new();
        WzDirectory? root = file.WzFile?.WzDirectory;
        if (root != null)
            Descend(file, spec, root, Array.Empty<PathStep>(), file.Id, entries, 0);

        if (_indexes.Count >= MaxCachedIndexes)
            _indexes.Clear();
        _indexes[key] = (structure, entries);
        return entries;
    }

    /// <summary>
    /// Walks a directory and the ones under it, to the depth the kind declares.
    ///
    /// Depth is per kind rather than unlimited because it is a claim about the
    /// archive's shape: Mob.wz keeps its images at the root, Item.wz and
    /// Character.wz put a category folder in the way, and an archive that nests
    /// deeper than its kind says it does is one nobody has looked at yet. An
    /// unlimited walk would instead quietly index whatever it found, which is how
    /// a port ends up filing something in a place no client reads.
    /// </summary>
    private void Descend(
        OpenFile file, PortKindSpec spec, WzDirectory directory,
        PathStep[] steps, string directoryPath, EntryIndex into, int depth)
    {
        Scan(file, spec, directory, steps, directoryPath, into);

        if (depth >= spec.MaxDepth)
            return;

        foreach (WzDirectory sub in directory.WzDirectories)
        {
            Descend(file, spec, sub,
                    steps.Append(new PathStep(sub.Name ?? "", "Directory")).ToArray(),
                    WzPath.Child(directoryPath, sub.Name ?? ""), into, depth + 1);
        }
    }

    private void Scan(
        OpenFile file, PortKindSpec spec, WzDirectory directory,
        PathStep[] steps, string directoryPath, EntryIndex into)
    {
        foreach (WzImage image in directory.WzImages)
        {
            string imagePath = WzPath.Child(directoryPath, image.Name);

            // An id-named image is an entry unless the kind says a name that short
            // is one of its containers. Without that test Skill.wz/1000.img was
            // indexed as "skill 1000" and its 116 skills were never seen at all —
            // see PortKindSpec.ContainerNameDigits.
            if (TryEntryId(image.Name, out int imageId) && !IsContainerName(spec, image.Name))
            {
                into.TryAdd(new EntryLocation(
                    imageId, file, imagePath, image, steps, image.Name,
                    Relative(steps, image.Name), image.BlockSize));
                continue;
            }

            if (!spec.UsesContainers)
                continue;

            // Only the images the kind says hold its entries. Quest.wz keys ten of
            // its twelve images numerically and only one of them holds quests.
            if (spec.EntryImages != null
                && !spec.EntryImages.Contains(image.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A container: an image holding nodes whose names are ids. This is the
            // only branch that parses, and it is why UsesContainers is opt-in.
            try
            {
                WzSessionService.EnsureParsed(image);
            }
            catch (Exception ex)
            {
                // One unreadable image must not cost the whole archive its index;
                // the string pool and the string index take the same line.
                _log.LogDebug(ex, "Skipping {Image} while indexing {Archive}", image.Name, file.Name);
                continue;
            }

            PathStep[] inside = steps.Append(new PathStep(image.Name, "Image")).ToArray();
            WzPropertyCollection? level = image.WzProperties;
            string levelPath = imagePath;

            // A fixed wrapper the kind declares: Skill.wz/0100.img/skill/<id>.
            // Absent means this image is not one of that kind's containers --
            // Skill.wz also holds MobSkill.img and Recipe images, which have no
            // 'skill' level and must not be indexed as skills.
            if (spec.EntryWrapper != null)
            {
                if (level?.FindByName(spec.EntryWrapper) is not { } wrapper)
                    continue;
                inside = inside.Append(new PathStep(spec.EntryWrapper, "SubProperty")).ToArray();
                level = wrapper.WzProperties;
                levelPath = WzPath.Child(imagePath, spec.EntryWrapper);
            }

            // The scope an id inside this image is unique within. Whole-archive
            // for every kind but String.wz, and the image's own name for that one.
            string scope = spec.IdsAreUniquePerImage ? Relative(steps, image.Name) : "";

            Inside(file, spec, level, inside, levelPath, into, image.BlockSize, scope, 1);
        }
    }

    /// <summary>
    /// Indexes the entries inside one container image, through however many
    /// category levels the kind declares.
    ///
    /// The walk stops at the first id-named node, which is what keeps it from
    /// wandering: <c>Eqp.img/Eqp/Cap/1002357</c> is found three levels down and
    /// 1002357's own children — <c>name</c>, <c>desc</c> — are never looked at, so
    /// an entry that happens to have numeric children cannot fill the index with
    /// things that are not entries.
    /// </summary>
    private void Inside(
        OpenFile file, PortKindSpec spec, WzPropertyCollection? level, PathStep[] steps,
        string levelPath, EntryIndex into, long imageBytes, string scope, int depth)
    {
        if (level == null)
            return;

        int children = Math.Max(1, level.Count);

        foreach (WzImageProperty property in level)
        {
            string name = property.Name ?? "";
            if (TryEntryId(name, out int id))
            {
                into.TryAdd(new EntryLocation(
                    id, file, WzPath.Child(levelPath, name), property, steps, name,
                    Relative(steps, name), imageBytes / children, scope));
                continue;
            }

            if (depth >= spec.ContainerDepth || property.WzProperties == null)
                continue;

            Inside(file, spec, property.WzProperties,
                   steps.Append(new PathStep(name, "SubProperty")).ToArray(),
                   WzPath.Child(levelPath, name), into, imageBytes, scope, depth + 1);
        }
    }

    /// <summary>Whether an all-digit image name is one of this kind's containers rather than an entry.</summary>
    private static bool IsContainerName(PortKindSpec spec, string name)
    {
        if (spec.ContainerNameDigits <= 0)
            return false;
        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return stem.Length <= spec.ContainerNameDigits;
    }

    private static string Relative(IReadOnlyList<PathStep> steps, string name) =>
        steps.Count == 0 ? name : string.Join("/", steps.Select(s => s.Name)) + "/" + name;

    /// <summary>
    /// The entry a session path names, with the shape it sits in.
    ///
    /// Rebuilt from the path rather than looked up in the index, because a
    /// selection port must work without paying for an index of an archive it is
    /// only taking three things out of.
    /// </summary>
    private EntryLocation? Locate(string path, PortKindSpec spec)
    {
        WzObject? node = _session.TryResolve(path);
        if (node is not (WzImage or WzImageProperty))
            return null;

        string[] segments = WzPath.SplitRaw(path);
        if (segments.Length < 2)
            return null;

        string name = WzPath.ParseSegment(segments[^1]).Name;
        if (!TryEntryId(name, out int id))
            return null;

        OpenFile file;
        try { file = _session.GetFile(segments[0]); }
        catch (KeyNotFoundException) { return null; }
        if (!KindsOf(file).Contains(spec.Kind))
            return null;

        // The nodes between the archive root and the entry, typed from what is
        // actually there rather than guessed from the name -- a WZ directory may
        // itself be called something.img, so "ends with .img" is not a test.
        List<PathStep> steps = new();
        string cursor = segments[0];
        for (int i = 1; i < segments.Length - 1; i++)
        {
            cursor = cursor + "/" + segments[i];
            string stepName = WzPath.ParseSegment(segments[i]).Name;
            steps.Add(new PathStep(stepName, _session.TryResolve(cursor) switch
            {
                WzDirectory => "Directory",
                WzImage => "Image",
                _ => "SubProperty",
            }));
        }

        long bytes = node switch
        {
            WzImage image => image.BlockSize,
            _ => (node as WzImageProperty)?.ParentImage is { } parent
                ? parent.BlockSize / Math.Max(1, parent.WzProperties?.Count ?? 1)
                : 0,
        };

        return new EntryLocation(
            id, file, path, node, steps, name, Relative(steps, name), bytes, ScopeOf(spec, steps));
    }

    /// <summary>
    /// The container image an entry sits in, for the kinds whose ids are only
    /// unique inside one. Everything above the image counts, so a String.wz opened
    /// with its images in a folder would still scope correctly.
    /// </summary>
    private static string ScopeOf(PortKindSpec spec, IReadOnlyList<PathStep> steps)
    {
        if (!spec.IdsAreUniquePerImage)
            return "";

        int image = -1;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Type == "Image")
                image = i;
        }
        return image < 0 ? "" : string.Join("/", steps.Take(image + 1).Select(s => s.Name));
    }

    /// <summary>
    /// A satellite entry by id, in whichever of the spec's images holds it, with
    /// the category levels it sits under.
    ///
    /// The nesting is discovered rather than configured: <c>Consume.img</c> keys
    /// ids directly, <c>Eqp.img</c> wraps them in <c>Eqp/&lt;category&gt;</c>, and
    /// both are live in one v232 client. Two levels is enough for every layout in
    /// the wild, and going deeper would start matching things that only look like
    /// ids.
    /// </summary>
    private (WzImageProperty Entry, string Path, string Image, IReadOnlyList<PathStep> Steps)? FindSatellite(
        List<OpenFile> archives, PortSatelliteSpec satellite, int id)
    {
        foreach (string imageName in satellite.Images)
        {
            (WzImage Image, string Path)? found = FindImage(archives, imageName);
            if (found == null)
                continue;

            (WzImage image, string imagePath) = found.Value;
            try { WzSessionService.EnsureParsed(image); }
            catch { continue; }

            // A row keyed by a child value rather than by its own name. Scanned
            // rather than probed because there is nothing to probe: a v232
            // Commodity.img's rows are named "0".."9696" and the id is inside
            // them. 9,697 integer comparisons is nothing; the alternative is an
            // index that would have to be invalidated on every shop edit.
            if (satellite.MatchField != null)
            {
                foreach (WzImageProperty row in image.WzProperties)
                {
                    string? value = row.WzProperties?.FindByName(satellite.MatchField)?.WzValue?.ToString();
                    if (value != null && int.TryParse(value, out int rowId) && rowId == id)
                    {
                        return (row, WzPath.Child(imagePath, row.Name ?? ""), imageName,
                                Array.Empty<PathStep>());
                    }
                }
                continue;
            }

            if (StringEditService.FindById(image.WzProperties, id) is { } flat)
            {
                return (flat, WzPath.Child(imagePath, flat.Name ?? ""), imageName,
                        Array.Empty<PathStep>());
            }

            foreach (WzImageProperty wrapper in image.WzProperties)
            {
                if (wrapper.WzProperties == null)
                    continue;

                if (StringEditService.FindById(wrapper.WzProperties, id) is { } direct)
                {
                    return (direct,
                            WzPath.Child(WzPath.Child(imagePath, wrapper.Name ?? ""), direct.Name ?? ""),
                            imageName,
                            new[] { new PathStep(wrapper.Name ?? "", "SubProperty") });
                }

                foreach (WzImageProperty group in wrapper.WzProperties)
                {
                    if (group.WzProperties == null)
                        continue;
                    if (StringEditService.FindById(group.WzProperties, id) is not { } nested)
                        continue;

                    string groupPath = WzPath.Child(
                        WzPath.Child(imagePath, wrapper.Name ?? ""), group.Name ?? "");
                    return (nested, WzPath.Child(groupPath, nested.Name ?? ""), imageName,
                            new[]
                            {
                                new PathStep(wrapper.Name ?? "", "SubProperty"),
                                new PathStep(group.Name ?? "", "SubProperty"),
                            });
                }
            }
        }
        return null;
    }

    /// <summary>
    /// An image by name in any of the given archives, root or one level down —
    /// with its session path.
    ///
    /// The path is returned rather than derived from the image afterwards, and
    /// that is not tidiness. A session path starts at the file id and then names a
    /// node <em>inside</em> the archive, so reconstructing one from MapleLib's
    /// parent chain means deciding where the archive's root directory stops
    /// counting — the same off-by-one <see cref="StringPoolService"/>'s
    /// <c>ArchiveOf</c> documents, which silently made every lookup miss.
    /// </summary>
    private static (WzImage Image, string Path)? FindImage(List<OpenFile> files, string imageName)
    {
        List<(WzImage Image, string Path, OpenFile File)> found = FindImages(files, imageName);
        return found.Count == 0 ? null : (found[0].Image, found[0].Path);
    }

    /// <summary>
    /// EVERY archive of a role that holds an image of this name, in an order that
    /// does not depend on what the user opened first.
    ///
    /// The order is the point. This method's single-answer form decides where a
    /// satellite row is WRITTEN, and it used to take the first hit in session
    /// order — which is the order the archives happen to sit in the session's
    /// dictionary, so the same port could land the same row in a different
    /// archive tomorrow. On a v232 client the target splits Sound four ways and
    /// <c>StripArchiveSuffix</c> makes all four answer to "Sound", so all four
    /// are candidates for every sound row; today no image name is duplicated
    /// between them, which is exactly why nobody noticed, and "today's client
    /// happens not to hit it" is not a property a write destination may rest on.
    ///
    /// Sorted by archive name and then by session id: Sound.wz comes before
    /// Sound001.wz, the base archive of a family wins, and the answer is the same
    /// on every run. Within one archive the root beats a subdirectory, which was
    /// already true and stays true.
    ///
    /// The list is returned whole rather than reduced here so the write path can
    /// SAY that it chose. A choice among several is a fact about the port, and
    /// the one thing it must not be is silent.
    /// </summary>
    private static List<(WzImage Image, string Path, OpenFile File)> FindImages(
        List<OpenFile> files, string imageName)
    {
        List<(WzImage Image, string Path, OpenFile File)> found = new();

        foreach (OpenFile file in files
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            WzDirectory? root = file.WzFile?.WzDirectory;
            if (root == null)
                continue;

            WzImage? direct = root.GetImageByName(imageName);
            if (direct != null)
            {
                found.Add((direct, WzPath.Child(file.Id, direct.Name), file));
                continue;
            }

            foreach (WzDirectory sub in root.WzDirectories)
            {
                WzImage? nested = sub.GetImageByName(imageName);
                if (nested != null)
                {
                    found.Add((
                        nested,
                        WzPath.Child(WzPath.Child(file.Id, sub.Name), nested.Name),
                        file));
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The 'was' -> 'now' list a rename warning shows, capped at eight.
    ///
    /// One method for the plan's sentence and the apply's, because the two are
    /// about the same set and a reader who compares them is entitled to compare
    /// like with like.
    /// </summary>
    private static string RenameList(Dictionary<(string Path, string Was), string> rewrites) =>
        string.Join(", ", rewrites
            .Select(r => "'" + r.Key.Was + "' -> '" + r.Value + "'")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        + (rewrites.Count > 8 ? $" and {rewrites.Count - 8} more" : "");

    /// <summary>
    /// Puts a disclosure in front of a reason, keeping whichever of the two
    /// exists.
    ///
    /// The disclosure comes first deliberately: it is about where the port is
    /// writing, which the reader has to know before the sentence explaining
    /// what it found there means anything.
    /// </summary>
    private static string? With(string? disclosure, string? reason) =>
        disclosure == null ? reason
        : reason == null ? disclosure
        : disclosure + " " + reason;

    /// <summary>"0100100.img" -&gt; 100100, "05100000" -&gt; 5100000. Leading zeros are the norm.</summary>
    private static bool TryEntryId(string? name, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name))
            return false;

        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return stem.Length > 0 && int.TryParse(stem, out id);
    }

    #endregion

    #region Warnings, limits and suggestions

    /// <summary>
    /// What a port does not do, in every client, whatever the preview says.
    ///
    /// Always shown and never conditional, because the failure this guards
    /// against is a clean-looking preview reading as a complete job. Everything
    /// here is a real gap with a real in-game symptom, and each one is stated as
    /// the symptom rather than as a technicality.
    /// </summary>
    private static IEnumerable<string> Limits(PortKindSpec spec, bool bulk)
    {
        switch (spec.Kind)
        {
            case "mob":
                yield return
                    "Map spawns are not touched. A ported mob exists in the archive but appears nowhere " +
                    "until a map's 'life' list names its id — edit that in the Explorer, or it will never " +
                    "be seen in game.";
                yield return
                    "A boss's parts are not detected. Nothing in a mob image says 'these other ids are my " +
                    "arms', so nothing here can know: the ids are grouped only by convention. Any near ids " +
                    "found in the source are listed as suggestions — check them yourself.";
                yield return
                    "Mob skills are not carried. A boss's attacks reference Skill.wz/MobSkill.img by id, and " +
                    "that image is a different archive with its own numbering; copying entries out of it " +
                    "blind would overwrite the target client's own skills.";
                break;

            case "npc":
                yield return
                    "Map placements are not touched. A ported NPC exists in the archive but stands nowhere " +
                    "until a map's 'life' list names its id.";
                yield return
                    "Scripts are not carried. An NPC's behaviour lives in the server's script folder, not in " +
                    "Npc.wz, and nothing in the image can tell you which script it runs.";
                break;

            case "item":
                yield return
                    "The item's icon travels with it, because info/icon and info/iconRaw are canvases inside " +
                    "the node being copied. Anything the icon reaches by '_outlink' does not — those are " +
                    "listed per item when they are found.";
                yield return
                    "Nothing that only the server knows is carried: drop tables, shop stock, quest rewards " +
                    "and reward chances all live outside the WZ files.";
                yield return
                    "Item options are not carried. They are keyed by option id in other archives, and " +
                    "copying them blind would overwrite the target client's own. Set membership is carried " +
                    "separately through Etc.wz/SetItemInfo.img whenever the item's info/setItemID names one.";
                break;

            case "skill":
                yield return
                    "Nothing gives the skill to anyone. Which job learns it, at what level and from which " +
                    "book is the server's business, and a v232 client's own Skill.wz book listing is not " +
                    "edited here — the entry lands in the book the source filed it under and no further.";
                yield return
                    "A skill's icons are almost always an '_outlink' into the client's icon book " +
                    "(Skill.wz/000.img on a v232 client), so porting one brings that whole image along. That " +
                    "is 14.8 MB and hundreds of other skills' icons — deliberate, because the alternative is " +
                    "a skill with a blank icon, but it is not a small copy.";
                break;

            case "reactor":
                yield return
                    "Map placements are not touched. A reactor exists in the archive and appears nowhere " +
                    "until a map's 'reactor' list names its id, so a ported reactor is one nobody meets " +
                    "until you place it.";
                yield return
                    "What a reactor drops is the server's, not the client's. The 'action' string names a " +
                    "server script, and nothing here can carry or check it.";
                break;

            case "quest":
                yield return
                    "The ids a quest names are not carried — its hand-in NPC, the mobs it counts, the items " +
                    "it takes and gives, and the quests before and after it. They are listed per quest, and " +
                    "checked against the target where that can be done without parsing a whole archive. A " +
                    "quest whose NPC or reward item the target lacks starts and cannot be finished.";
                yield return
                    "The server decides whether a quest can be taken at all. Quest.wz holds the text, the " +
                    "conditions and the rewards; the script that runs it does not live here.";
                break;

            case "string":
                yield return
                    "This carries the name and nothing that is named. A String.wz row for an item the " +
                    "target does not have is a name for something that does not exist — useful when you " +
                    "are fixing up a client by hand, and not a port of the item.";
                break;

            case "map":
                yield return
                    "Scenery is checked before it is copied, and nearly always it is not copied. Two " +
                    "clients of one game share a scenery library rather than owning rival ones: measured " +
                    "across a real pair, 1,929,186 of the 1,931,467 art references their maps make already " +
                    "resolve in the target, and 17,494 of 17,613 maps need not one byte of scenery. What " +
                    "is checked is the pieces this map actually draws — this object at this address, " +
                    "this tile, this background frame — not whether two 21.7 MB books are the same " +
                    "book, which is a dearer question with a worse answer.";
                yield return
                    "Where the target's set genuinely cannot serve a piece this map draws, the copy lands " +
                    "under a name of its own and this map's reference is rewritten to it. That is the " +
                    "escape hatch, not the road: replacing the target's copy would restyle every other map " +
                    "drawing from it — a median of 35 and 4,218 at the ninetieth percentile — and " +
                    "nothing here will do that, whatever you tick. A renamed copy is also not " +
                    "self-contained: those sets carry 8,688 absolute '_outlink's naming OTHER sets, which " +
                    "are left as they are, so the copy draws part of itself out of the older build. The " +
                    "part that creates one says so.";
                yield return
                    "Nothing the map places is carried. Its 'life' list names mobs and NPCs, its 'reactor' " +
                    "list names reactors and its portals name other maps, all by id in four other " +
                    "archives — they are counted, checked against the target where that is cheap, and " +
                    "reported per map. A map whose mobs the target lacks loads and is empty.";
                yield return
                    "The world map is not touched. Map.wz/WorldMap holds 92 images saying which maps appear " +
                    "where on the in-game world map and how they join up; a ported map is reachable by its " +
                    "portals and by command, and does not appear there until you add it by hand.";
                yield return
                    "What spawns, when, and what any of it does is the server's. Map.wz says where a mob " +
                    "may stand and how often; whether one ever does is decided somewhere this cannot see.";
                yield return
                    "A map's layout may not be its own. 6,850 of one client's 17,442 maps hold nothing but " +
                    "an 'info' block naming another map, and 10.7% draw their minimap out of another map " +
                    "image; both are followed, so porting one map can bring a second. Its portals name " +
                    "further maps by id and those are checked, not carried.";
                yield return
                    "A scenery image that keeps its own frames in a '_Canvas' directory is copied whole, " +
                    "but what IT links out to is not followed the way an entry's art is. The check that " +
                    "refuses a port over links the target cannot resolve reads the entries, not the " +
                    "pictures they name, so for scenery those links are reported against the archive after " +
                    "the fact instead of refused before. If the port ends saying canvas links still point " +
                    "into a '_Canvas', that is what happened: open the rest of the source's Map family and " +
                    "run it again.";
                break;

            case "morph":
                yield return
                    "Nothing hands the morph out. A morph id is reached by an item or a skill saying so, " +
                    "and those are ported separately — until one of them names it, the frames sit in the " +
                    "archive and nobody ever turns into it.";
                break;
        }

        yield return
            "The two clients' formats are compared only by the version stamp their archives carry and by " +
            "which fields the target's own content uses. Two builds can stamp the same version and still " +
            "differ, so a clean preview here is not a promise that the client will read every field.";

        if (bulk)
        {
            yield return
                $"Past {MaxParsedEntries} entries nothing is parsed to check it: art links, icon links and " +
                "the 'takes its art from another id' check are all skipped, because they cost a full parse " +
                "of every entry — ten seconds for a Mob.wz, and that is per preview. Under that count they " +
                "are followed exactly as for a hand-picked selection, so a folder of a few hundred equips " +
                "still carries everything it points at.";
            yield return
                $"The check that refuses a port whose canvas links the target cannot follow reads those same " +
                $"parsed links, so past {MaxParsedEntries} entries it cannot fire either. At this scale the " +
                "links are reported after the fact instead, against the archive as it then stands.";
        }
    }

    /// <summary>
    /// The version-specific warnings: what is measurably different about the two
    /// clients, and honestly, what this cannot see.
    /// </summary>
    private void Warn(
        PortPlanDto plan, PortKindSpec spec, ClientGroup source, OpenFile target,
        List<PortItemDto> items, bool wholeArchive)
    {
        /* --- the archive version stamp --- */
        OpenFile? sourceArchive = source.Files.FirstOrDefault(
            f => KindsOf(f).Contains(spec.Kind) && f.WzFile != null);
        if (sourceArchive != null && target.WzFile != null)
        {
            plan.Warnings.Add(sourceArchive.GameVersion != target.GameVersion
                ? $"The two archives stamp different versions: {sourceArchive.Name} says v{sourceArchive.GameVersion} " +
                  $"and {target.Name} says v{target.GameVersion}. Everything below still copies, but a field the " +
                  "older client does not know is ignored at best and is a parse failure at worst."
                : $"Both archives stamp v{target.GameVersion}. That is the only version test there is — it says " +
                  "the files agree about a number, not that the two clients read the same fields.");
        }

        /* --- fields the target's own content never uses --- */
        (HashSet<string> knownKeys, int sampled) = TargetInfoKeys(target, spec);
        if (sampled == 0)
        {
            plan.Warnings.Add(
                $"{target.Name} has no {spec.Plural.ToLowerInvariant()} this could read, so there is no way " +
                "to tell which fields that client understands. Nothing below has been checked against it.");
            return;
        }

        // At archive scale the source side of this would be a full parse of every
        // entry, which is the cost the whole scope is arranged to avoid. A sample
        // of the ones being listed is still evidence; claiming it covered all four
        // thousand would not be.
        SortedSet<string> unknown = new(StringComparer.Ordinal);
        int inspected = 0;
        foreach (PortItemDto item in items)
        {
            if (inspected >= TargetSampleSize)
                break;
            if (_session.TryResolve(item.SourcePath) is not { } node)
                continue;
            if (wholeArchive && node is WzImage { Parsed: false })
                continue;   // parsing it here is exactly what archive scope must not do
            foreach (string key in InfoKeys(node))
                unknown.Add(key);
            inspected++;
        }
        unknown.ExceptWith(knownKeys);

        if (inspected == 0)
        {
            plan.Warnings.Add(
                $"None of the selected {spec.Plural.ToLowerInvariant()} were already parsed, so their fields " +
                $"have not been compared against {target.Name}. Use the selection scope on a few of them to " +
                "get that check.");
        }
        else if (unknown.Count > 0)
        {
            plan.Warnings.Add(
                $"Not one of the {sampled} {spec.Plural.ToLowerInvariant()} sampled in {target.Name} carries " +
                $"these fields, which {inspected} of the ones being ported do: {string.Join(", ", unknown)}. " +
                "That is the strongest sign available here that they are newer than the target client, which " +
                "will most likely ignore them. The copy is still made — deleting fields nobody asked about " +
                "would be worse.");
        }
        else
        {
            plan.Warnings.Add(
                $"Every field on the {inspected} {spec.Plural.ToLowerInvariant()} checked also appears on the " +
                $"{sampled} sampled in {target.Name}, so nothing looks newer than the target. That is a " +
                "sample, not a proof.");
        }
    }

    /// <summary>
    /// Canvas links reaching outside what is being copied.
    ///
    /// Two different failures, and they are not the same one.
    ///
    /// <c>_outlink</c> names another image, usually in a sibling archive of the
    /// same family. It is never followed automatically: doing so would silently
    /// drag another archive's images into a port the user asked to be about one
    /// thing. Naming them and leaving the decision alone is the honest half.
    ///
    /// <c>_inlink</c> names a path inside the <em>same image</em>. For a mob or an
    /// equip that is harmless, because the whole image is copied. For an item it
    /// is not: only one property is taken out of a shared container image, so an
    /// inlink into a sibling item resolves, in the target, against whatever
    /// happens to be sitting at that path — a different icon, or nothing.
    /// </summary>
    private void NoteLinks(
        PortItemDto item, EntryLocation entry, PortKindSpec spec,
        ClientGroup source, Func<EntryIndex> sourceIndex, PortPlanDto plan,
        Dictionary<string, SplitArtUse> splitArt)
    {
        SortedSet<string> outlinks = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> strayInlinks = new(StringComparer.OrdinalIgnoreCase);
        int walked = 0;
        bool capped = false;

        // An entry that is a property inside a shared image is the case where an
        // inlink can point at a sibling that is not coming along.
        bool sharesItsImage = entry.Node is WzImageProperty;

        // Where this entry sits inside its own image, which is the frame of
        // reference an '_inlink' is written in: MapleLib resolves one against the
        // nearest parent WzImage, so "skill/4221016/hit/0/0" is a path from the
        // book's root and not from the entry's.
        //
        // This was compared against `entry.Name + "/"` — the bare id — and an
        // entry that lives under a wrapper never writes that. Measured in a real
        // v232 422.img: 4221014's 49 self-references all begin "skill/4221014/"
        // and were every one of them reported as pointing at a neighbour, while
        // 4221016's 88 genuinely stray ones were reported for the wrong reason.
        // A note that cries wolf 49 times is a note nobody reads the 89th time.
        string insideImage = "";
        for (WzObject? at = entry.Node; at is WzImageProperty inside; at = inside.Parent)
            insideImage = insideImage.Length == 0 ? inside.Name : inside.Name + "/" + insideImage;
        string mine = insideImage + "/";

        // Bounds depth and refuses to enter a link, which MaxLinkWalk did neither
        // of: it counts nodes, and one frame per node let a self-referential UOL
        // build 40,000 frames where 16,099 already killed the process. Not
        // entering a link is also what this pass wanted -- the canvases under a
        // UOL are another entry's, and reporting them as this entry's outlinks
        // described the wrong node.
        WzWalk guard = new();

        void Walk(WzPropertyCollection? properties, int depth)
        {
            if (properties == null || capped)
                return;
            foreach (WzImageProperty property in properties)
            {
                if (++walked > MaxLinkWalk) { capped = true; return; }

                if (property is WzCanvasProperty canvas)
                {
                    if (canvas[WzCanvasProperty.OutlinkPropertyName] is WzStringProperty outlink
                        && !string.IsNullOrWhiteSpace(outlink.Value)
                        && OutlinkImage(outlink.Value) is { } outImage)
                    {
                        outlinks.Add(outImage);

                        // Counted per LINK and not per image, unlike everything
                        // else here. The refusal below is about how much of this
                        // port stops working, and "one image" and "699 frames out
                        // of book 422" are the same finding at opposite ends of
                        // what a person would decide differently about.
                        if (IsSplitCanvasLink(outlink.Value))
                        {
                            if (!splitArt.TryGetValue(outImage, out SplitArtUse? use))
                                splitArt[outImage] = use = new SplitArtUse(outImage, outlink.Value!);
                            use.Links++;
                            if (use.Entries.Count < 8)
                                use.Entries.Add(entry.Name);
                        }
                    }

                    if (sharesItsImage
                        && canvas[WzCanvasProperty.InlinkPropertyName] is WzStringProperty inlink
                        && !string.IsNullOrWhiteSpace(inlink.Value)
                        && !inlink.Value.StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                    {
                        strayInlinks.Add(inlink.Value);
                    }
                }
                Walk(guard.Into(property, depth), depth + 1);
            }
        }

        try
        {
            if (entry.Node is WzImage image)
            {
                WzSessionService.EnsureParsed(image);
                Walk(image.WzProperties, 0);
            }
            else if (entry.Node is WzImageProperty property)
            {
                Walk(guard.From(property), 0);
            }
        }
        catch (Exception ex)
        {
            item.Notes.Add($"Its art could not be scanned for links: {ex.Message}");
            return;
        }

        if (capped)
        {
            item.Notes.Add(
                $"This entry is more than {MaxLinkWalk:N0} nodes, so the scan for art links stopped early. " +
                "Treat the list below as incomplete.");
        }
        else if (guard.Stopped)
        {
            // A different sentence from the size one, because it is a different
            // finding: this entry contains a node that leads back to itself, which
            // is a property of the data rather than of how much of it there is.
            item.Notes.Add(
                "Part of this entry leads back into itself, so the scan for art links stopped at the " +
                "loop. Treat the list below as incomplete.");
        }

        // NoteLinks and Dependencies both walk for outlinks, and the closure now
        // acts on them -- so this only reports the ones the closure could not
        // resolve, and says so once per image.
        // Named at the top of the plan, not only per entry.
        //
        // "Its art links out to X, which is not in the archives you have open"
        // was already said against each entry -- and read by nobody, because it
        // is one line among hundreds and the port runs anyway. What the reader
        // needs is the instruction, once, where a plan is actually read: which
        // FILE to open. An art link reaches a sibling archive far more often
        // than not, and a port whose art cannot be reached lands frames the
        // client cannot draw -- which, in a client that keeps its art inline,
        // is what takes it down when the window opens.
        SortedSet<string> toOpen = new(StringComparer.OrdinalIgnoreCase);

        // Every archive these links reach, whether or not it resolves today.
        //
        // Resolving is not the question the reader needs answered. The art is
        // looked up twice -- once here against the source, and again after the
        // copy against the target client -- and the second lookup is the one that
        // decides whether a frame ends up inline or left as a link the client
        // cannot follow. A message that speaks only when the FIRST lookup fails
        // is silent for the exact case this keeps landing in: source fully open,
        // art found, copied across, and then unreachable on the far side because
        // the archive that holds it is not open there. Measured: Skill/40000.img
        // is not in Skill.wz at all, it is at the root of Skill003.wz.
        //
        // So this is said unconditionally, and names the family rather than a
        // verdict: these are the files the port reads art out of, open them on
        // both sides.
        SortedSet<string> reaches = new(StringComparer.OrdinalIgnoreCase);
        foreach (string outlink in outlinks)
        {
            if (ArchiveFamilyOf(outlink) is { Length: > 0 } family)
                reaches.Add(family);
        }

        foreach (string outlink in outlinks.Where(o => ResolveImage(source, o) == null))
        {
            // The family, which is the file to open. "Skill/_Canvas/40000.img"
            // means Skill.wz -- and its numbered siblings, because Skill001.wz
            // answers to "Skill" too, which is the same rule the resolver uses
            // to find them.
            if (ArchiveFamilyOf(outlink) is { Length: > 0 } family)
                toOpen.Add(family + ".wz");
            string stem = CanvasLinkPath.StripImageSuffix(outlink);

            if (int.TryParse(Path.GetFileName(stem), out int linkedId)
                && sourceIndex().Contains(entry.Scope, linkedId))
            {
                item.Notes.Add(
                    $"Its art links out to {outlink}, which is in {source.Label}'s own archives. It is not " +
                    "copied: add it to the selection if the target client does not already have it.");
            }
            else
            {
                // Deliberately not "that image does not exist": it almost always
                // does, in a sibling archive of the same client that simply is not
                // open. Saying the true thing -- this port cannot see it -- points
                // at the fix instead of at a phantom.
                item.Notes.Add(
                    $"Its art links out to {outlink}, which is not in the archives you have open for " +
                    $"{source.Label}. Open the archive that holds it to see whether it needs porting too; " +
                    "if the target client has no such image, those frames will draw nothing.");
            }
        }

        foreach (string inlink in strayInlinks)
        {
            item.Notes.Add(
                $"One of its canvases takes its pixels from '{inlink}', which is another entry inside the " +
                $"same {entry.Steps[^1].Name}. Only {entry.Name} is being copied, so in the target that path " +
                "resolves against whatever is there instead — port that id too, or the picture will be " +
                "wrong rather than missing.");
        }

        if (reaches.Count > 0)
        {
            string families = string.Join(" and ", reaches.Select(r => r + ".wz"));
            string reach =
                $"These draw art out of {families}. Every part of that family has to be open on "
                + "BOTH clients — the numbered siblings included, because Skill001.wz and "
                + "Skill003.wz answer to \"Skill\" the same way Skill.wz does, and the icon book "
                + "a skill points at often lives in one of them rather than in the archive you "
                + "picked. Open on the source only, the art is copied across and then cannot be "
                + "found again on the target, so every frame lands as a link this client cannot "
                + "follow — which is what takes it down when the window opens.";
            if (!plan.Warnings.Contains(reach))
                plan.Warnings.Add(reach);
        }

        if (toOpen.Count > 0)
        {
            string say =
                $"Open {string.Join(" and ", toOpen)} from {source.Label} — the numbered siblings "
                + "too, because Skill001.wz answers to \"Skill\" the same way — and port again. "
                + "Some of these entries draw their frames out of images in those archives, and "
                + "nothing open can supply them. Ported without them the frames land as links to "
                + "art that is not there: at best they draw nothing, and in a client that keeps "
                + "its art inline the window that tries to draw one can take the client down.";
            if (!plan.Warnings.Contains(say))
                plan.Warnings.Add(say);
        }

        if (outlinks.Count > 0 && !plan.Warnings.Contains(ArtLinkWarning))
            plan.Warnings.Add(ArtLinkWarning);
        if (strayInlinks.Count > 0 && !plan.Warnings.Contains(InlinkWarning))
            plan.Warnings.Add(InlinkWarning);
    }

    // Rewritten when outlinked art started travelling by default. The old text
    // said those images were "never copied automatically", which had stopped
    // being true — and a warning that describes the opposite of what the port
    // just did is worse than no warning at all.
    private const string ArtLinkWarning =
        "Some of these draw frames out of other images through a canvas '_outlink'. Those images travel too, " +
        "and each one is listed as a row of its own saying what pulled it in — an outlink usually reaches " +
        "into a sibling archive, so a port can be much larger than the entry you picked. The ones this " +
        "could not find in the archives you have open are named per entry instead.";

    private const string InlinkWarning =
        "Some of these borrow pixels from a neighbour inside the same image through '_inlink'. Copying one " +
        "item out of a shared image leaves that link pointing at whatever sits at the same path in the " +
        "target, which is a wrong picture rather than a missing one. The neighbours are named per entry.";

    /// <summary>
    /// Ids close to a boss's that the source has and the target does not.
    ///
    /// Explicitly a suggestion and never an action. Nothing in a mob image
    /// declares its parts — Zakum's arms are 8800003..8800010 purely by
    /// convention — so this groups by the id's hundred, which is the convention,
    /// and says so. Only for entries flagged as bosses, or every ordinary mob
    /// would suggest its ninety-nine neighbours.
    /// </summary>
    private void NoteBossParts(
        PortItemDto item, EntryLocation entry, PortKindSpec spec,
        Func<EntryIndex> sourceIndex, EntryIndex targetIndex,
        PortPlanDto plan)
    {
        if (spec.Kind != "mob" || entry.Node is not WzImage image || !IsBoss(image))
            return;

        // Bounded across the whole plan, not per boss. Sixty bosses times a
        // hundred-id bucket is six thousand rows of guesses, which is not a
        // suggestion list, it is a second archive listing nobody asked for.
        if (plan.Suggestions.Count >= MaxSuggestions)
            return;

        int bucket = entry.Id / 100;
        List<PortSuggestionDto> found = new();

        foreach (EntryLocation location in sourceIndex().All)
        {
            int other = location.Id;
            if (other == entry.Id || other / 100 != bucket || targetIndex.Contains(location.Scope, other))
                continue;
            if (plan.Suggestions.Any(s => s.Id == other) || found.Any(s => s.Id == other))
                continue;

            found.Add(new PortSuggestionDto
            {
                Id = other,
                Path = location.Path,
                Name = _strings.GetMobName(other),
                Reason = $"shares the first five digits of boss {entry.Id}, is in {location.File.Name}, " +
                         "and the target does not have it",
            });
        }

        if (found.Count == 0)
            return;

        plan.Suggestions.AddRange(found.OrderBy(s => s.Id).Take(MaxSuggestions - plan.Suggestions.Count));
        item.Notes.Add(
            $"{entry.Id} is a boss. {found.Count} nearby id{(found.Count == 1 ? "" : "s")} exist in the " +
            "source and not in the target — a boss's parts are usually among them, but nothing in the archive " +
            "says so, so they are listed as suggestions rather than added.");
    }

    private static bool IsBoss(WzImage image)
    {
        try { WzSessionService.EnsureParsed(image); }
        catch { return false; }

        WzImageProperty? boss = image.WzProperties?.FindByName("info")?.WzProperties?.FindByName("boss");
        return boss != null
            && long.TryParse(boss.WzValue?.ToString(), out long value)
            && value != 0;
    }

    /// <summary>
    /// The <c>info</c> keys used by a sample of the target archive's own entries.
    ///
    /// This is the only version test that looks at content rather than at a
    /// number, and it is deliberately phrased as evidence rather than as a
    /// verdict — a field missing from forty sampled entries is a strong hint that
    /// the target client is older, not a proof that it cannot read it.
    /// </summary>
    private (HashSet<string> Keys, int Sampled) TargetInfoKeys(OpenFile target, PortKindSpec spec)
    {
        int structure = _session.StructureGeneration;
        if (_targetKeys.TryGetValue(target.Id, out (int Structure, HashSet<string> Keys, int Sampled) cached)
            && cached.Structure == structure)
        {
            return (cached.Keys, cached.Sampled);
        }

        List<EntryLocation> entries = Index(target, spec).All.ToList();
        HashSet<string> keys = new(StringComparer.Ordinal);
        int sampled = 0;

        // Already-parsed first, because they are free: a session that has visited
        // Mobs mode has parsed all 2,742 and this costs nothing at all.
        foreach (EntryLocation entry in entries)
        {
            if (sampled >= TargetSampleSize)
                break;
            if (entry.Node is WzImage { Parsed: false })
                continue;
            foreach (string key in InfoKeys(entry.Node))
                keys.Add(key);
            sampled++;
        }

        // Then a stride across the rest, so the sample is not the first forty
        // entries of one id range.
        if (sampled < TargetSampleSize && entries.Count > 0)
        {
            int stride = Math.Max(1, entries.Count / Math.Max(1, TargetSampleSize - sampled));
            for (int i = 0; i < entries.Count && sampled < TargetSampleSize; i += stride)
            {
                try
                {
                    if (entries[i].Node is WzImage image)
                        WzSessionService.EnsureParsed(image);
                    foreach (string key in InfoKeys(entries[i].Node))
                        keys.Add(key);
                    sampled++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping {Entry} while sampling {Archive}",
                        entries[i].Name, target.Name);
                }
            }
        }

        if (_targetKeys.Count >= MaxCachedIndexes)
            _targetKeys.Clear();
        _targetKeys[target.Id] = (structure, keys, sampled);
        return (keys, sampled);
    }

    private static IEnumerable<string> InfoKeys(WzObject node)
    {
        WzImageProperty? info = ChildrenOf(node)?.FindByName("info");
        if (info?.WzProperties == null)
            yield break;

        foreach (WzImageProperty property in info.WzProperties)
        {
            if (!string.IsNullOrEmpty(property.Name))
                yield return property.Name;
        }
    }

    #endregion

    #region Describing

    /// <summary>
    /// A node's children, whether it is an image or a property. Every kind of
    /// entry is one or the other, and the two have separate collections.
    /// </summary>
    private static WzPropertyCollection? ChildrenOf(WzObject node) => node switch
    {
        WzImage image => image.WzProperties,
        WzImageProperty property => property.WzProperties,
        _ => null,
    };

    /// <summary>
    /// The ids an entry names through the kind table's declared reference paths.
    ///
    /// One walk for every edge the table declares, so <c>info/link</c>,
    /// <c>info/revive/*</c> and <c>info/skill/*&#47;skill</c> cost the same code.
    /// "*" matches every child at that level, which is what makes a list of parts
    /// or a list of attacks one row in the table rather than a loop somewhere.
    /// </summary>
    private static IEnumerable<(PortReference Edge, int Id)> ReferencedIds(
        EntryLocation entry, PortKindSpec spec)
    {
        if (spec.References == null)
            yield break;

        foreach (PortReference edge in spec.References)
        {
            foreach (int id in Follow(ChildrenOf(entry.Node), edge.Path.Split('/'), 0))
                yield return (edge, id);
        }
    }

    /// <summary>
    /// The key or keys a satellite row is filed under.
    ///
    /// Most satellites share the entry's own id. A derived-key satellite names
    /// the relationship explicitly — currently an equip's
    /// <c>info/setItemID</c> — and is absent from the plan when the entry names no
    /// such relationship. This distinction is what keeps every ordinary item
    /// from growing a false "SetItemInfo row missing" warning.
    /// </summary>
    private static IReadOnlyList<int> SatelliteKeys(
        EntryLocation entry, PortSatelliteSpec satellite)
    {
        if (entry.Id <= 0)
            return Array.Empty<int>();
        if (satellite.KeyPath == null)
            return new[] { entry.Id };

        if (entry.Node is WzImage image)
            WzSessionService.EnsureParsed(image);

        return Follow(ChildrenOf(entry.Node), satellite.KeyPath.Split('/'), 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>
    /// Every id at one slash-separated path under a node, with "*" matching every
    /// child at that level. Shared by the edges the closure follows and the
    /// requirements it only reports.
    /// </summary>
    /// <param name="accept">
    /// Asked of the node the id was read out of, so a row can be judged by its
    /// siblings and not only by itself. A map's <c>life/0</c> is
    /// <c>{ type = "n", id = "1520070" }</c> — the id says nothing about whether
    /// it is a mob or an NPC, and there is no other place that answer lives.
    /// </param>
    private static IEnumerable<int> Follow(
        WzPropertyCollection? level, string[] segments, int at,
        Func<WzImageProperty, bool>? accept = null)
    {
        if (level == null)
            yield break;

        bool last = at == segments.Length - 1;
        foreach (WzImageProperty property in level)
        {
            if (segments[at] != "*"
                && !string.Equals(property.Name, segments[at], StringComparison.OrdinalIgnoreCase))
                continue;

            if (!last)
            {
                foreach (int id in Follow(property.WzProperties, segments, at + 1, accept))
                    yield return id;
                continue;
            }

            if (accept != null && !accept(property))
                continue;

            // Parsed, not pattern-matched: ids are written plainly in every
            // client seen, but leading zeros are ordinary in WZ and
            // "08800001" must mean the same mob as "8800001".
            string? value = property.WzValue?.ToString();
            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out int parsed) && parsed > 0)
                yield return parsed;
        }
    }

    /// <summary>
    /// The test one requirement's <see cref="PortRequirement.When"/> asks of the
    /// node an id was read out of, or null when it asks nothing.
    /// </summary>
    private static Func<WzImageProperty, bool>? Accepts(PortRequirement requirement)
    {
        if (requirement.When is not { } when)
            return null;

        return property =>
            (property.Parent as WzImageProperty)?.WzProperties?.FindByName(when.Field)
                ?.WzValue?.ToString()?.Trim() is { } sibling
            && sibling.Equals(when.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Says what else the target client needs for this entry to work, and — where
    /// it can be checked cheaply — whether the target already has it.
    ///
    /// This is the check the quest kind was switched off waiting for. It copies
    /// nothing: a single v232 quest names up to twenty items and eleven other
    /// quests, and following that would drag half a client through the door. What
    /// it must not do is stay quiet, because a quest whose hand-in NPC does not
    /// exist in the target starts, runs, and cannot be finished — and nothing in
    /// the editor shows it.
    ///
    /// The presence check is only run against kinds whose index is a name scan
    /// (Mob.wz, Npc.wz, Reactor.wz) or is confined to one image (Quest.wz's
    /// QuestInfo.img). Items and skills are named and explicitly not checked:
    /// answering for them means parsing every container image in the target's
    /// Item.wz or Skill.wz, which on a v232 client is a full-archive parse, and a
    /// one-click port cannot spend that on a preview.
    /// </summary>
    private void NoteRequirements(
        PortItemDto item, EntryLocation entry, PortKindSpec spec,
        ClientGroup source, ClientGroup? targetClient)
    {
        if (spec.Requirements == null || entry.Id <= 0)
            return;

        // Grouped by what is needed and how to say it, so one note covers "gives
        // item 1092000, 1092001, 4000003" rather than three.
        Dictionary<(string Needs, string Label), SortedSet<int>> wanted = new();

        foreach (PortRequirement requirement in spec.Requirements)
        {
            WzPropertyCollection? level = RequirementNode(requirement, spec, entry, source);
            if (level == null)
                continue;

            foreach (int id in Follow(level, requirement.Path.Split('/'), 0, Accepts(requirement)))
            {
                // The sentinel that means "nowhere", and the entry's own id.
                //
                // Both are measured rather than defensive: three of the four
                // portals on a v232 000110000.img carry tm = 999999999, and that
                // same map's info/returnMap is 110000, which is itself. Reported
                // as requirements they would be a map that needs a map no client
                // has and a map that needs itself.
                if (id == requirement.Ignore || id == entry.Id)
                    continue;

                if (!wanted.TryGetValue((requirement.Needs, requirement.Label), out SortedSet<int>? ids))
                    wanted[(requirement.Needs, requirement.Label)] = ids = new SortedSet<int>();
                ids.Add(id);
            }
        }

        // What it costs to be wrong, in this kind's own words.
        //
        // These sentences were written for quests, which was every caller until
        // skills declared a requirement of their own, and a note on a ported
        // skill that ended "the quest will start and not be finishable" would
        // read as a bug in the tool rather than a warning about the data. The
        // quest wording is kept exactly as it was, because it is the sharpest of
        // the two and it is what the quest limits already promise.
        string consequence = spec.Kind == "quest"
            ? "the quest will start and not be finishable"
            : $"this {spec.Label.ToLowerInvariant()} will not do in the target client what it does in the "
              + "source";

        foreach (((string needs, string label), SortedSet<int> ids) in wanted.OrderBy(p => p.Key.Label))
        {
            PortKindSpec? needSpec = AllKinds.FirstOrDefault(
                k => k.Kind.Equals(needs, StringComparison.OrdinalIgnoreCase));
            string list = string.Join(", ", ids.Take(12)) + (ids.Count > 12 ? $" and {ids.Count - 12} more" : "");

            List<OpenFile> archives = targetClient == null || needSpec == null
                ? new List<OpenFile>()
                : ArchivesFor(targetClient, needSpec);

            if (needSpec == null || archives.Count == 0)
            {
                item.Notes.Add(
                    $"It {label} {list}. None of those are copied, and no {needs} archive is open for the " +
                    $"target, so whether it has them was not checked. If it does not, {consequence}.");
                continue;
            }

            // Cheap to answer or not answered at all -- see the summary above.
            if (needSpec.UsesContainers && needSpec.EntryImages == null)
            {
                item.Notes.Add(
                    $"It {label} {list}. None of those are copied, and they were not checked against the " +
                    $"target: answering would mean parsing every container image in its " +
                    $"{archives[0].Name}, which is a whole-archive parse and not something a preview should " +
                    "spend. Port them yourself if the target does not have them.");
                continue;
            }

            EntryIndex have = new();
            foreach (OpenFile archive in archives)
                have.TryAddAll(Index(archive, needSpec));

            List<int> absent = ids.Where(id => !have.Contains("", id)).ToList();
            item.Notes.Add(absent.Count == 0
                ? $"It {label} {list}, and the target already has every one of them. They are not copied."
                : $"It {label} {list}. The target is missing " +
                  string.Join(", ", absent.Take(12)) +
                  (absent.Count > 12 ? $" and {absent.Count - 12} more" : "") +
                  $" — none of these are copied, so port them separately or {consequence}.");
        }
    }

    /// <summary>
    /// Says what the entry names by NAME in another archive, and whether the
    /// target client has it.
    ///
    /// This is the check that would have ended the hunt this was written during.
    /// A skill ported into a v232 client came across node for node — no leftover
    /// children, no unreachable canvas, every field the target understands — and
    /// did nothing when it was pressed, and the only thing wrong with it was
    /// <c>action/0 = 'cruelStab'</c>: the name of a character animation that
    /// client has never had. There is no id to follow and nothing in Skill.wz is
    /// wrong, so every other check here passes and always would have.
    ///
    /// Answered only when the target client's Character.wz is open, and it says
    /// which of the two it is rather than staying quiet. Reading it costs a parse
    /// of one body image, which is why it is asked once per plan and remembered
    /// (see <see cref="_animations"/>).
    /// </summary>
    private void NoteAnimations(
        PortItemDto item, EntryLocation entry, PortKindSpec spec, ClientGroup? targetClient)
    {
        if (spec.Animations == null || entry.Id <= 0)
            return;

        foreach (PortAnimation animation in spec.Animations)
        {
            SortedSet<string> names = new(
                Texts(ChildrenOf(entry.Node), animation.Path.Split('/'), 0), StringComparer.Ordinal);
            if (names.Count == 0)
                continue;

            string list = string.Join(", ", names.Select(n => $"'{n}'"));
            List<OpenFile> archives = targetClient == null
                ? new List<OpenFile>()
                : targetClient.Files
                    .Where(f => WzSessionService.StripArchiveSuffix(f.Name)
                        .Equals(animation.Archive, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (archives.Count == 0)
            {
                item.Notes.Add(
                    $"Its {animation.What} is {list}, which is a NAME in {animation.Archive}.wz and not "
                    + "anything this copies. No such archive is open for the target, so whether that client "
                    + "has it was not checked — and a client that cannot find the animation will not send "
                    + "the packet, so the skill lands looking perfect and does nothing when it is pressed. "
                    + $"Open the target's {animation.Archive}.wz and preview again to have this answered.");
                continue;
            }

            HashSet<string> have = new(StringComparer.Ordinal);
            foreach (OpenFile archive in archives)
                have.UnionWith(AnimationsIn(archive, animation.ImagePrefix));

            List<string> absent = names.Where(n => !have.Contains(n)).ToList();
            if (have.Count == 0)
            {
                item.Notes.Add(
                    $"Its {animation.What} is {list}. Nothing could be read out of the target's "
                    + $"{archives[0].Name} images beginning '{animation.ImagePrefix}', so this was not "
                    + "answered either way rather than guessed at.");
                continue;
            }

            item.Notes.Add(absent.Count == 0
                ? $"Its {animation.What} is {list}, and the target client already has "
                  + (names.Count == 1 ? "it" : "all of them") + $" in {archives[0].Name}. Nothing to carry."
                : $"Its {animation.What} is {list}, and the target client has no "
                  + string.Join(", ", absent.Select(n => $"'{n}'"))
                  + $" in any of {archives[0].Name}'s '{animation.ImagePrefix}' images. The client resolves "
                  + "a skill's action before it will send the use-skill packet, so this one arrives whole, "
                  + "shows its icon and description, and does NOTHING when the key is pressed. This is not "
                  + "something a skill port can fix: the animation lives in the body image and in every "
                  + "weapon and armour image that moves with it. Either point the copy at an action this "
                  + "client has, or bring that animation across separately.");
        }
    }

    /// <summary>
    /// The string values at a declared path, as <see cref="Follow"/> does for
    /// ids. Separate because a name is not a number: parsing it away is exactly
    /// how an action would be lost.
    /// </summary>
    private static IEnumerable<string> Texts(WzPropertyCollection? level, string[] segments, int at)
    {
        if (level == null)
            yield break;

        bool last = at == segments.Length - 1;
        foreach (WzImageProperty property in level)
        {
            if (segments[at] != "*"
                && !string.Equals(property.Name, segments[at], StringComparison.OrdinalIgnoreCase))
                continue;

            if (!last)
            {
                foreach (string text in Texts(property.WzProperties, segments, at + 1))
                    yield return text;
                continue;
            }

            if (ScalarText(property) is { } value && !string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    /// <summary>
    /// Every animation name in one archive's body images, read once per archive
    /// and structural generation.
    ///
    /// Bounded by the prefix and by a parse guard: a body image is tens of
    /// megabytes and one of them is enough — the skins carry the same action
    /// list — but they are all read, because "absent from every one of them" is
    /// the claim the note makes and a sample cannot support it.
    /// </summary>
    private HashSet<string> AnimationsIn(OpenFile archive, string imagePrefix)
    {
        string key = archive.Id + "|" + imagePrefix;
        int structure = _session.StructureGeneration;
        if (_animations.TryGetValue(key, out (int Structure, HashSet<string> Names) cached)
            && cached.Structure == structure)
        {
            return cached.Names;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        WzDirectory? root = archive.WzFile?.WzDirectory;
        if (root != null)
        {
            foreach (WzImage image in root.WzImages)
            {
                if (image.Name?.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                try
                {
                    WzSessionService.EnsureParsed(image);
                    foreach (WzImageProperty property in image.WzProperties ?? new WzPropertyCollection(null))
                    {
                        if (!string.IsNullOrEmpty(property.Name))
                            names.Add(property.Name);
                    }
                }
                catch (Exception ex)
                {
                    // An image that will not parse is a real state and not a
                    // reason to abandon the check: the other skins still answer,
                    // and an empty answer is reported as "not read" rather than
                    // as "the client does not have it".
                    _log.LogDebug(ex, "Could not read animations from {Image}", image.Name);
                }
            }
        }

        if (_animations.Count >= MaxCachedIndexes)
            _animations.Clear();
        _animations[key] = (structure, names);
        return names;
    }

    /// <summary>
    /// Says when an entry carries a field that decides it may not be USED, as
    /// opposed to one that decides how it looks.
    ///
    /// Every other check here is structural, and the whole point of this one is
    /// that the structural checks were all passing. A ported skill can be equal
    /// to the source's node for node — no leftovers from the target's own entry,
    /// no unreachable canvas, every field the target client understands — and
    /// still be a skill nothing happens on, because the source marked it as data
    /// the client applies rather than casts. Without this the only way to find
    /// that out is to launch the game and press the key.
    ///
    /// It reports and changes nothing. Deleting a field the source set would make
    /// the copy something other than the source's entry, which is the one promise
    /// a port makes; and the flag is right for a passive, which most of the
    /// entries carrying it are.
    /// </summary>
    private static void NoteInertFlags(PortItemDto item, EntryLocation entry, PortKindSpec spec)
    {
        if (spec.InertFlags == null || entry.Id <= 0)
            return;

        WzPropertyCollection? children = ChildrenOf(entry.Node);
        if (children == null)
            return;

        foreach (PortInertFlag flag in spec.InertFlags)
        {
            // Present and meant, not merely present. A field written as 0 is the
            // client's own way of saying "not this one", and a note on every
            // entry that spells the flag out to turn it off is a note nobody
            // finishes reading.
            WzImageProperty? field = children.FindByName(flag.Field);
            if (field == null
                || !long.TryParse(ScalarText(field)?.Trim(), out long value)
                || value == 0)
            {
                continue;
            }

            item.Notes.Add("This one " + flag.Note);
        }
    }

    /// <summary>The node a requirement reads out of: the entry itself, or one of its satellites.</summary>
    private WzPropertyCollection? RequirementNode(
        PortRequirement requirement, PortKindSpec spec, EntryLocation entry, ClientGroup source)
    {
        if (requirement.Part == "entry")
            return ChildrenOf(entry.Node);

        PortSatelliteSpec? satellite = spec.Satellites.FirstOrDefault(s => s.Kind == requirement.Part);
        if (satellite == null)
            return null;

        return FindSatellite(WithRole(source, satellite.Role), satellite, entry.Id)?.Entry.WzProperties;
    }

    /// <summary>
    /// A node named by id inside a specific image of a specific archive role —
    /// <c>Skill.wz/MobSkill.img/140</c>, which is where a mob's attack actually
    /// lives.
    ///
    /// Returned as a dependency rather than as an entry (id 0), because it is not
    /// a mob: it must not appear in the mob totals, and its own String.wz and
    /// Sound.wz satellites are not a mob's.
    /// </summary>
    private EntryLocation? ResolveReferenced(ClientGroup source, PortReference edge, int id)
    {
        List<OpenFile> archives = source.Files
            .Where(f => RoleOf(f) == edge.Role || KindsOf(f).Contains(edge.Role ?? ""))
            .ToList();

        (WzImage Image, string Path)? found = FindImage(archives, edge.Image ?? "");
        if (found == null)
            return null;

        (WzImage image, string imagePath) = found.Value;
        try { WzSessionService.EnsureParsed(image); }
        catch { return null; }

        if (StringEditService.FindById(image.WzProperties, id) is not { } node)
            return null;

        OpenFile owner = archives.First(f => ReferenceEquals(f.WzFile, image.WzFileParent));
        PathStep[] steps = { new(image.Name, "Image") };
        return new EntryLocation(
            0, owner, WzPath.Child(imagePath, node.Name ?? ""), node, steps, node.Name ?? "",
            Relative(steps, node.Name ?? ""),
            image.BlockSize / Math.Max(1, image.WzProperties.Count));
    }

    /// <summary>
    /// Everything this entry reaches for that is not inside it.
    ///
    /// A copied node is not self-contained. A v232 mob draws frames out of
    /// <c>Mob/_Canvas/0100007.img</c> through a canvas <c>_outlink</c>; a UOL
    /// borrows a sound or a sprite from a neighbour; and both of those routinely
    /// cross into a different file of the same family — Mob.wz reaching into
    /// Mob001.wz is ordinary. A reference that does not resolve in the target is
    /// the exact failure this feature exists to stop: the copy looks right in the
    /// editor and draws nothing in game.
    ///
    /// So they are followed, transitively, rather than reported and left. What is
    /// <em>not</em> silent is the result: every node pulled in this way is listed
    /// as such, and anything that could not be resolved is stated loudly rather
    /// than passing as success.
    ///
    /// The visit set is the caller's, keyed on the resolved session path, because
    /// one image is reachable under several spellings and WZ links may point back
    /// at each other.
    /// </summary>
    private List<EntryLocation> Dependencies(
        EntryLocation entry, ClientGroup source, PortItemDto item, bool carry)
    {
        List<EntryLocation> found = new();
        SortedSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> seenLinks = new(StringComparer.OrdinalIgnoreCase);
        int walked = 0;
        bool capped = false;

        // See WzWalk. MaxLinkWalk counts nodes and so bounded nothing the stack
        // cares about; a link that resolves to its own parent spent one frame per
        // node and was allowed 40,000 of them. Declining to enter a UOL costs this
        // pass nothing it wanted either: a UOL is already followed below AS a link,
        // by reading its text, which is the treatment the comment there argues for.
        // Walking into its resolved children on top of that was counting another
        // entry's subtree as this one's.
        WzWalk guard = new();

        void Walk(WzPropertyCollection? properties, int depth)
        {
            if (properties == null || capped)
                return;

            foreach (WzImageProperty property in properties)
            {
                if (++walked > MaxLinkWalk) { capped = true; return; }

                string? reference = property switch
                {
                    WzCanvasProperty canvas =>
                        (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value,
                    // The link TEXT, never WzValue: WzUOLProperty.WzValue hands
                    // back the object it resolved to, so reading it both loses
                    // the path and quietly succeeds when the path is broken.
                    WzUOLProperty uol => uol.Value,
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(reference) && seenLinks.Add(reference))
                {
                    string? imagePath = OutlinkImage(reference);
                    if (imagePath != null)
                    {
                        EntryLocation? resolved = ResolveImage(source, imagePath);
                        if (resolved == null)
                        {
                            // By image, not by the full link. A v232 Zakum reaches
                            // into Mob/2600631.img from fourteen different
                            // canvases, and fourteen copies of one sentence is
                            // the same finding rendered as noise -- which is how
                            // a report stops being read.
                            unresolved.Add(imagePath);
                        }
                        else if (!string.Equals(resolved.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(resolved);
                        }
                    }
                }

                Walk(guard.Into(property, depth), depth + 1);
            }
        }

        try
        {
            if (entry.Node is WzImage image)
                WzSessionService.EnsureParsed(image);
            Walk(guard.From(entry.Node), 0);
        }
        catch (Exception ex)
        {
            item.Notes.Add($"Its references could not be read: {ex.Message}");
            return found;
        }

        if (capped)
        {
            item.Notes.Add(
                $"This entry is more than {MaxLinkWalk:N0} nodes, so the search for what it depends on " +
                "stopped early. Treat the list of what came with it as incomplete.");
        }
        else if (guard.Stopped)
        {
            item.Notes.Add(
                "Part of this entry leads back into itself, so the search for what it depends on stopped " +
                "at the loop. Treat the list of what came with it as incomplete.");
        }

        // Off means named-and-not-carried, never silently dropped: the copy would
        // land, look complete in the editor and render nothing in game, which is
        // the failure the switch defaults to on to prevent.
        if (!carry && found.Count > 0)
        {
            item.Notes.Add(
                $"It draws frames from {string.Join(", ", found.Select(f => f.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))}" +
                (found.Count > 8 ? $" and {found.Count - 8} more" : "") +
                ". You asked for outlinked art not to travel, so none of it was copied — those frames will " +
                "draw nothing unless the target client already has those images.");
            found.Clear();
        }

        foreach (string reference in unresolved)
        {
            // Loud, and phrased as the in-game symptom. This is now the main
            // remaining way a port can look successful and be broken, so it must
            // not read as a footnote.
            item.Notes.Add(
                $"It draws frames from {reference}, which is not in any archive you have open for " +
                $"{source.Label}, so it could not be brought along. Open the archive that holds it and port " +
                "again — in the target those frames will draw nothing.");
        }

        return found;
    }

    /// <summary>
    /// A WZ-internal image path — "Mob/_Canvas/0100007.img", "Mob/8800141.img" —
    /// resolved against one client's open archives.
    ///
    /// The first segment may or may not be the archive family: MapleLib writes
    /// outlinks both ways, and a numbered sibling means the family name does not
    /// match the filename either (Mob001.wz answers to "Mob").
    ///
    /// Both readings are now genuinely tried, per archive, in that order. The
    /// doc said so before and the code did not: it computed one <c>start</c> from
    /// whether the family stem matched and used only that, so an archive whose
    /// stem matches <em>and</em> which nests the family again under its own root
    /// was unreachable, and an outlink written with the numbered file name —
    /// <c>Map001/Back/…</c>, which MapleLib's own
    /// <c>WzFile.GetObjectFromPath</c> accepts — matched nothing at all, because
    /// <c>StripArchiveSuffix("Map001.wz")</c> is <c>"Map"</c>. Widening this can
    /// only find links that used to dangle; it can never lose one.
    /// </summary>
    private EntryLocation? ResolveImage(ClientGroup source, string imagePath)
    {
        string[] parts = imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        foreach (OpenFile file in source.Files)
        {
            WzDirectory? root = file.WzFile?.WzDirectory;
            if (root == null)
                continue;

            // The family stem ("Map" for Map001.wz) and the file's own stem
            // ("Map001"). Either spelling may be the leading segment.
            bool prefixed =
                WzSessionService.StripArchiveSuffix(file.Name).Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(file.Name).Equals(parts[0], StringComparison.OrdinalIgnoreCase);

            EntryLocation? found = prefixed ? Walk(file, root, 1) : null;
            found ??= Walk(file, root, 0);
            if (found != null)
                return found;
        }
        return null;

        EntryLocation? Walk(OpenFile file, WzDirectory root, int start)
        {
            if (start >= parts.Length)
                return null;

            WzObject current = root;
            List<PathStep> steps = new();

            for (int i = start; i < parts.Length; i++)
            {
                if (current is not WzDirectory directory)
                    return null;

                WzObject? next = (WzObject?)directory.GetDirectoryByName(parts[i])
                                 ?? directory.GetImageByName(parts[i]);
                if (next == null)
                    return null;

                if (i < parts.Length - 1)
                    steps.Add(new PathStep(parts[i], next is WzDirectory ? "Directory" : "Image"));
                current = next;
            }

            if (current is not WzImage target)
                return null;

            string path = steps.Aggregate(file.Id, (p, s) => WzPath.Child(p, s.Name));
            path = WzPath.Child(path, target.Name);

            // Id 0 on purpose: this is something a copied node needs, not an
            // entry of the kind being ported, and it must never be confused with
            // one in the totals or in the id-keyed visit set.
            return new EntryLocation(
                0, file, path, target, steps, target.Name,
                Relative(steps, target.Name), target.BlockSize);
        }
    }

    /// <summary>
    /// "Mob/8800141.img/die1/0" -&gt; "Mob/8800141.img". Null when there is no
    /// image in it.
    ///
    /// Through <see cref="CanvasLinkPath"/>, which is the one parser this file is
    /// allowed to cut a link with. There used to be seven spellings of "where
    /// does the image start" in here — an <c>EndsWith</c> loop, two
    /// <c>Array.FindIndex</c>es, and four different answers to "which family is
    /// this" — and they disagreed on the two shapes that are real: a nested
    /// family (<c>Skill/Roguelike/_Canvas/Skill.img</c>, whose family is not
    /// <c>parts[0]</c>) and a link written with no family at all
    /// (<c>_Canvas/Skill.img/…</c>, which every <c>"/_Canvas/"</c> substring test
    /// misses because it has no leading slash).
    /// </summary>
    private static string? OutlinkImage(string outlink) =>
        CanvasLinkPath.TryParse(outlink, out CanvasLinkPath link) ? link.ImagePath : null;

    /// <summary>
    /// The display name this entry has in the source client, or null.
    ///
    /// Two places, because two shapes are real. A mob, an NPC, an item and a
    /// skill are named in String.wz; a quest names itself, in
    /// <c>QuestInfo.img/&lt;id&gt;/name</c>. Falling back to the entry's own
    /// <c>name</c> is what stops a whole quest plan reading as forty rows of
    /// bare ids.
    /// </summary>
    private string? NameInSource(ClientGroup source, PortKindSpec spec, EntryLocation entry)
    {
        PortSatelliteSpec? names = spec.Satellites.FirstOrDefault(s => s.Kind == "string");
        if (names != null
            && FindSatellite(WithRole(source, names.Role), names, entry.Id)
                ?.Entry.WzProperties?.FindByName(names.NameField)?.WzValue?.ToString() is { Length: > 0 } named)
        {
            return named;
        }

        return ChildrenOf(entry.Node)?.FindByName("name")?.WzValue?.ToString();
    }

    /// <summary>What the target already holds, in one line, so a conflict can be judged.</summary>
    private static string Describe(EntryLocation entry) => Describe(entry.Node);

    private static string Describe(WzObject node)
    {
        try
        {
            if (node is WzImage image)
                WzSessionService.EnsureParsed(image);

            WzPropertyCollection? properties = ChildrenOf(node);
            if (properties == null)
                return "empty";

            WzImageProperty? info = properties.FindByName("info");
            if (info?.WzProperties == null)
                return $"{properties.Count} nodes";

            string?[] fields =
            {
                Field(info, "level", "level "),
                Field(info, "maxHP", "", " HP"),
                Field(info, "reqLevel", "requires level "),
                Field(info, "price", "price "),
                Field(info, "link", "links to "),
            };
            return string.Join(" · ", fields.Where(f => f != null).Append($"{properties.Count} nodes"));
        }
        catch (Exception ex)
        {
            // An entry that will not parse is exactly the one a user must not
            // overwrite blind, so the failure is reported rather than swallowed.
            return "could not be read: " + ex.Message;
        }
    }

    private static string? Field(WzImageProperty info, string key, string before, string after = "")
    {
        string? value = info.WzProperties?.FindByName(key)?.WzValue?.ToString();
        return value == null ? null : before + value + after;
    }

    private static string Summarise(WzImageProperty entry)
    {
        // A UOL is described by where it points, never by what it points at:
        // WzUOLProperty.WzProperties hands back the *target's* children, so
        // walking into one describes a different node than the one on screen.
        if (entry is WzUOLProperty)
            return ScalarText(entry)!;

        WzPropertyCollection? children = entry.WzProperties;
        if (children == null || children.Count == 0)
            return ScalarText(entry) ?? "empty";

        return string.Join(" · ", children.Take(4).Select(c =>
            ScalarText(c) is { } text ? $"{c.Name}: {Trim(text)}" : $"{c.Name} ({c.WzProperties?.Count ?? 0})"));
    }

    private static string Trim(string? text) =>
        text == null ? "" : text.Length <= 48 ? text : text[..45] + "...";

    /// <summary>
    /// A property's value as text, or null when it has none.
    ///
    /// Two traps, both of which produced wrong answers rather than ugly ones.
    /// <see cref="WzUOLProperty.WzValue"/> returns the resolved link, so every
    /// Sound.wz entry built from UOLs — which is how a v232 client shares a boss's
    /// audio — fingerprinted as the same type name, and the plan reported
    /// "already identical" for sounds the target did not have. And
    /// <see cref="WzBinaryProperty.WzValue"/> is the decompressed audio, so
    /// <c>ToString()</c> on it is the constant "System.Byte[]". Reading the header
    /// fields instead is both readable and cheap: none of them touch the data.
    /// </summary>
    private static string? ScalarText(WzImageProperty property) => property switch
    {
        WzUOLProperty uol => "-> " + uol.Value,
        WzBinaryProperty sound => $"{sound.Length} ms {sound.SoundType}" +
                                  (sound.Frequency > 0 ? $" {sound.Frequency} Hz" : ""),
        WzCanvasProperty canvas => $"{canvas.PngProperty?.Width ?? 0}x{canvas.PngProperty?.Height ?? 0}",
        _ => property.WzValue?.ToString(),
    };

    /// <summary>
    /// A shallow fingerprint of an entry: child names, and either the value or the
    /// child count.
    ///
    /// Shallow on purpose, and the UI says so in as many words. Comparing a
    /// Sound.wz entry byte for byte means decompressing two MP3s per row, and the
    /// question being asked is only "is there anything here worth copying". What
    /// it must never do is fail to <em>distinguish</em> — see
    /// <see cref="ScalarText"/> for the version of this that compared every sound
    /// as equal.
    /// </summary>
    private static string Digest(WzImageProperty entry)
    {
        if (entry is WzUOLProperty)
            return ScalarText(entry)!;   // see Summarise: never walk into a link

        WzPropertyCollection? children = entry.WzProperties;
        if (children == null)
            return ScalarText(entry) ?? "";

        return string.Join('|', children
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => ScalarText(c) is { } text
                ? $"{c.Name}={text}"
                : $"{c.Name}={{{c.WzProperties?.Count ?? 0}}}"));
    }

    /// <summary>
    /// Names the UOL children of an entry, or null when it has none.
    ///
    /// One level deep, deliberately: a Sound.wz entry is one level of named clips,
    /// and a recursive walk through UOLs would follow the links it is trying to
    /// describe.
    /// </summary>
    private static string? DescribeLinks(WzImageProperty entry)
    {
        if (entry.WzProperties == null || entry is WzUOLProperty)
            return null;

        List<string> links = entry.WzProperties
            .OfType<WzUOLProperty>()
            .Select(uol => $"{uol.Name} -> {uol.Value}")
            .Take(6)
            .ToList();

        return links.Count == 0
            ? null
            : $"{links.Count} of these are links rather than data ({string.Join(", ", links)}). " +
              "The link text is copied as it stands, so whatever it points at has to exist in the target " +
              "client too or it resolves to nothing.";
    }

    #endregion

    #region Choosing what to port

    /// <summary>
    /// Rows a picker may show at once. The whole point of a searchable list is
    /// that it is bounded where an archive listing is not — a v232 Character.wz
    /// holds 12,134 Hair images, and a UI handed all of them has to virtualise or
    /// die. Two hundred is the same ceiling the Database section uses.
    /// </summary>
    private const int MaxListedEntries = 200;

    /// <summary>
    /// How deep a String archive is walked to collect names. Measured on a v232
    /// client, the deepest is <c>Eqp.img/Eqp/Cap/1002357/name</c> — image, two
    /// category levels, the id, then the field — so four levels below the image
    /// reaches every one of them with nothing to spare.
    /// </summary>
    private const int MaxNameDepth = 4;

    /// <summary>Per source client and kind: every id it names, and the generation it was read at.</summary>
    private readonly Dictionary<string, (int Structure, Dictionary<int, string> Names)> _sourceNames =
        new(StringComparer.Ordinal);

    /// <summary>
    /// What one open archive holds that could be ported, narrowed by a query.
    ///
    /// This exists so that picking what to import can be search-first. Nobody
    /// looking for Zakum wants to scroll 2,742 numbered images, and nobody looking
    /// for a Cap wants to learn that equips are filed by four-digit category
    /// folders — they want to type a name. What makes that safe rather than
    /// convenient is that the paths come out of <see cref="Index"/>, the same
    /// index <see cref="BuildPlan"/> resolves a selection against, so a row that
    /// is offered is a row the plan can locate. A path guessed in the browser from
    /// an id — which is what database.js has to do, because /api/db/search answers
    /// with ids and no paths — can miss, and a port given a path that does not
    /// resolve reports the failure only after the user has committed to it.
    ///
    /// Nothing here writes and nothing here parses that the plan would not parse
    /// anyway: the index is cached against the structural generation, and the name
    /// map is built in one pass over the source client's String archive.
    /// </summary>
    /// <summary>
/// The archive's entries, optionally only those inside <paramref name="under"/>.
///
/// The scope exists because no rule written in the browser can tell an entry
/// from a container it sits in. Item.wz alone has entries at two depths --
/// "ItemOption.img/000001" and "Cash/0510.img/05100000" -- so depth does not
/// answer it; and "0510.img" looks exactly like the quest entry
/// "QuestData/1000.img" while being a container rather than a thing, so the
/// name does not answer it either. This index already knows, and this is how
/// it is asked.
/// </summary>
public PortEntriesDto Entries(string fileId, string? kind, string? query, int? limit, string? under = null)
    {
        lock (Gate())
        {
            OpenFile file;
            try { file = _session.GetFile(fileId); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException(ex.Message); }

            List<ClientGroup> groups = Groups();
            ClientGroup? client = groups.FirstOrDefault(g => g.Files.Any(f => f.Id == file.Id));

            List<string> kinds = KindsOf(file);
            PortEntriesDto dto = new()
            {
                FileId = file.Id,
                Archive = file.Name,
                ReadOnly = file.ReadOnly,
                Client = client?.Label ?? file.Name,
                ClientFolder = client?.Folder ?? "",
                Kinds = kinds,
                MaxArchiveBytes = MaxArchiveBytes,
                MaxSelection = MaxSelection,
            };

            // Three different answers, and flattening them into "nothing here"
            // would be the kind of silence that reads as a broken screen. An
            // archive with no kind at all is ordinary; a kind that is declared and
            // refused has a reason worth reading; a kind that is fine is the rest.
            string? wanted = kind ?? kinds.FirstOrDefault();
            PortKindSpec? spec = wanted == null
                ? null
                : AllKinds.FirstOrDefault(k => string.Equals(k.Kind, wanted, StringComparison.OrdinalIgnoreCase));

            if (spec == null)
            {
                dto.Reason =
                    $"{file.Name} holds nothing this can carry with everything it needs. What can be " +
                    "imported this way is " +
                    string.Join(", ", AllKinds.Where(k => k.Supported)
                        .SelectMany(k => k.ArchivePrefixes).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => p + ".wz")) +
                    ". You can still open this one and copy nodes by hand in the Explorer — what a port " +
                    "adds is the name, the sounds and the art a copy on its own leaves behind, and none " +
                    "of those are defined for this archive.";
                return dto;
            }

            dto.Kind = spec.Kind;
            dto.Label = spec.Label;
            dto.Plural = spec.Plural;

            if (!spec.Supported)
            {
                dto.Reason = spec.UnsupportedReason;
                return dto;
            }
            dto.Supported = true;

            // Which archives the SOURCE client would supply the satellites from.
            //
            // Checked here because nothing else checks it. port.js's setup gap
            // looks at the target — has it somewhere to put the name — and is
            // silent about whether the source has a name to give. Opening a split
            // client's Mob on its own and porting out of it produces a copy that
            // is nameless and silent for exactly that reason, reported afterwards
            // as "not in the source" rather than beforehand as "open String too".
            foreach (PortSatelliteSpec satellite in spec.Satellites)
            {
                string family = Capitalise(satellite.Role);
                if (dto.Sources.Any(s => s.Family.Equals(family, StringComparison.OrdinalIgnoreCase)))
                    continue;

                dto.Sources.Add(new PortSourceArchiveDto
                {
                    Role = satellite.Role,
                    Family = family,
                    Open = client != null && WithRole(client, satellite.Role).Count > 0,
                    // Worded as a possibility, not a promise, for everything but
                    // the name. Every item and mob has a String row, so saying the
                    // copy lands nameless without it is true. Sound and the cash
                    // shop are the exception rather than the rule -- most equips
                    // appear in neither -- and stating them the same way read as
                    // "these exist and will move", which sent someone hunting for
                    // a sound that was never in the client at all.
                    Carries = satellite.Role switch
                    {
                        "string" => "the name it shows in game — without it the copy lands nameless",
                        "sound" => "its sounds, if it has any",
                        "etc" => "its set definition and cash-shop listing, when present",
                        "effect" => "its visual and use effects, when present",
                        _ => $"whatever it has in the source's {family}",
                    },
                });
            }

            EntryIndex index = Index(file, spec);
            dto.Total = index.Count;
            dto.TotalBytes = index.All.Sum(e => e.Bytes);

            Dictionary<int, string> names = client == null
                ? new Dictionary<int, string>()
                : SourceNames(client, spec);
            dto.NamesAvailable = client != null && WithRole(client, "string").Count > 0;

            // Every writable archive of this kind, in every OTHER client, so a
            // row can say whether the id is already in the place it would go.
            //
            // Ids in MapleStory builds are added, not reassigned, so an id in
            // both clients is the same thing in both -- which makes "already
            // there" the single most useful thing to know while picking. On a
            // search for a common mob most hits exist in every build, and this
            // is what turns forty rows into the two that are new.
            //
            // Every other client rather than one chosen target: the target is
            // picked later, in the port dialog, and a row that said "new" here
            // and "already there" there would be worse than saying nothing.
            List<EntryIndex> targetIndexes = new();
            foreach (ClientGroup other in groups)
            {
                if (client != null && ReferenceEquals(other, client))
                    continue;
                foreach (OpenFile candidate in other.Files)
                {
                    if (candidate.ReadOnly || !KindsOf(candidate).Contains(spec.Kind))
                        continue;
                    targetIndexes.Add(Index(candidate, spec));
                }
            }

            // Anchored on a separator, never a bare prefix. "f1/Cap" is a string
            // prefix of "f1/Cape", and a scope that ignored that would quietly
            // hand back a second equip slot -- the same trap the folder port has
            // a test for.
            string? scope = string.IsNullOrWhiteSpace(under) ? null : under.Trim();

            // A scope may ask for more rows than the search list allows, because
            // "what is inside this" is a different question from "the first page
            // of matches" -- but it must still ASK. Defaulting a scope to
            // unbounded, which is what this did for one build, answered a tick on
            // a large node with 42,332 rows and 4.3 MB after 43 seconds of frozen
            // window. Whatever the caller can actually use, it knows; an
            // unbounded default is not a service to it.
            //
            // `Matched` is counted over everything either way, so a caller can
            // ask for few rows and still learn the true size.
            int cap = scope == null
                ? Math.Clamp(limit ?? MaxListedEntries, 1, MaxListedEntries)
                : Math.Max(1, limit ?? MaxListedEntries);
            string needle = (query ?? "").Trim();

            List<(int Rank, EntryLocation Entry)> matched = new();
            foreach (EntryLocation entry in index.All)
            {
                if (scope != null
                    && !entry.Path.Equals(scope, StringComparison.OrdinalIgnoreCase)
                    && !entry.Path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                int rank = Rank(entry, names.GetValueOrDefault(entry.Id), needle);
                if (rank >= 0)
                    matched.Add((rank, entry));
            }

            dto.Matched = matched.Count;
            dto.Truncated = matched.Count > cap;

            foreach ((int rank, EntryLocation entry) in matched
                         .OrderBy(m => m.Rank)
                         .ThenBy(m => m.Entry.Id)
                         .Take(cap))
            {
                _ = rank;
                dto.Results.Add(new PortEntryDto
                {
                    Id = entry.Id,
                    Path = entry.Path,
                    Name = names.GetValueOrDefault(entry.Id),
                    Relative = entry.Relative,
                    InTarget = targetIndexes.Any(i => i.Get(entry.Scope, entry.Id) != null),
                });
            }

            return dto;
        }
    }

    /// <summary>
    /// How well an entry answers a query: lower is better, negative is "no".
    ///
    /// Exact answers first, in both currencies. Someone who typed an id wants that
    /// id and not the 40 others it is a prefix of; someone who typed "zakum" wants
    /// Zakum before "Zakum's Arm". An empty query matches everything, which is what
    /// makes the same call serve "show me this archive" and "find me this thing".
    /// </summary>
    private static int Rank(EntryLocation entry, string? name, string needle)
    {
        if (needle.Length == 0)
            return 0;

        string id = entry.Id.ToString(CultureInfo.InvariantCulture);

        if (name != null && name.Equals(needle, StringComparison.OrdinalIgnoreCase)) return 0;
        if (id.Equals(needle, StringComparison.Ordinal)) return 1;
        // The node's own name, so a padded "08800100" finds 8800100 and a client
        // whose images are not padded still answers the same query.
        if (entry.Name.Equals(needle, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name != null && name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 2;
        if (id.StartsWith(needle, StringComparison.Ordinal)) return 3;
        if (name != null && name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 4;
        if (id.Contains(needle, StringComparison.Ordinal)) return 5;
        // Last, and only last: the path. It is how "Cap" finds every equip in
        // Character.wz/Cap, which is a real way people look for things — but a
        // folder match ranking above a name match would bury the thing itself
        // under its neighbours.
        if (entry.Relative.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 6;

        return -1;
    }

    /// <summary>
    /// Every id the source client names, for one kind, read out of that client's
    /// own String archive.
    ///
    /// Deliberately not <see cref="StringPoolService"/>, which is the obvious
    /// thing to reach for and is wrong here. The pool merges every open String
    /// archive into one index, so with two clients open it cannot say which of
    /// them named an id — and this list exists precisely to describe one of the
    /// two. A row showing a name the source does not actually have is a row that
    /// promises a name the port cannot carry.
    ///
    /// One pass rather than a lookup per entry. The per-entry route is
    /// <see cref="StringEditService.FindById"/>, which probes the plain id and
    /// then every zero-padded width up to ten; each miss is a linear scan of the
    /// image, so 2,742 mobs would be several million comparisons to answer a
    /// question one walk answers exactly.
    /// </summary>
    private Dictionary<int, string> SourceNames(ClientGroup source, PortKindSpec spec)
    {
        PortSatelliteSpec? names = spec.Satellites.FirstOrDefault(s => s.Kind == "string");
        if (names == null)
            return new Dictionary<int, string>();

        string key = source.Key + "|" + spec.Kind;
        int structure = _session.StructureGeneration;
        if (_sourceNames.TryGetValue(key, out (int Structure, Dictionary<int, string> Names) cached)
            && cached.Structure == structure)
        {
            return cached.Names;
        }

        Dictionary<int, string> map = new();
        foreach (OpenFile archive in WithRole(source, names.Role))
        {
            foreach (string imageName in names.Images)
            {
                if (FindImage(new List<OpenFile> { archive }, imageName) is not { } found)
                    continue;
                try { WzSessionService.EnsureParsed(found.Image); }
                catch (Exception ex)
                {
                    // One unreadable image must not cost the archive its names;
                    // the string pool and the entry index take the same line.
                    _log.LogDebug(ex, "Skipping {Image} while reading names from {Archive}",
                                  imageName, archive.Name);
                    continue;
                }
                Harvest(found.Image.WzProperties, map, 0, names.NameField);
            }
        }

        if (_sourceNames.Count >= MaxCachedIndexes)
            _sourceNames.Clear();
        _sourceNames[key] = (structure, map);
        return map;
    }

    /// <summary>
    /// Collects id -&gt; name from a String image, at whatever depth the client
    /// files them.
    ///
    /// Shape-blind on purpose. A v232 client files a mob flat
    /// (<c>Mob.img/8800100/name</c>) and an equip three levels down
    /// (<c>Eqp.img/Eqp/Cap/1002357/name</c>), and the rule that covers both without
    /// a table is "a node whose name is an id and which has a name child". Anything
    /// else is a category, so walk into it.
    /// </summary>
    /// <param name="field">
    /// What that child is called. Every String.wz image on a v232 client keys its
    /// label as <c>name</c> except Map.img, whose rows read
    /// <c>{ streetName = "Maple Road", mapName = "Maple Tree Hill" }</c> — so with
    /// the default a map picker showed 17,442 bare ids. See
    /// <see cref="PortSatelliteSpec.NameField"/>.
    /// </param>
    private static void Harvest(
        WzPropertyCollection? collection, Dictionary<int, string> into, int depth, string field)
    {
        if (collection == null || depth > MaxNameDepth)
            return;

        foreach (WzImageProperty property in collection)
        {
            string? propertyName = property.Name;
            if (propertyName == null)
                continue;

            if (TryEntryId(propertyName, out int id))
            {
                if (property.WzProperties?.FindByName(field)?.WzValue?.ToString() is { Length: > 0 } named)
                    into.TryAdd(id, named);
                continue;
            }

            Harvest(property.WzProperties, into, depth + 1, field);
        }
    }

    /// <summary>The session gate, named once so the region below reads as one lock.</summary>
    private object Gate() => _session.Gate;

    #endregion
}
