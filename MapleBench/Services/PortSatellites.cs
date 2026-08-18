using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// The satellite declarations, measured against a real v232 client rather than
/// reasoned about, and the one address shape the existing machinery cannot
/// express.
///
/// A satellite is data that lives outside an entry's own image but is part of
/// it: the String.wz row that names it, the Sound.wz row that makes it audible,
/// the Etc.wz row that lists it in a shop. Port an entry without them and you
/// get a nameless item or a silent skill — and every structural check passes.
///
/// This file stays separate from <see cref="PortService"/>'s kind table because
/// these arrays are measurements, with <c>docs/satellites.md</c> as their
/// supporting data. Keeping the evidence beside the declarations prevents a
/// later revision from being written from plausibility alone.
///
/// ADOPTION is one line per kind in <see cref="PortService"/>'s catalog:
/// <c>Satellites: PortSatellites.Mob,</c> and so on. Each array below is the
/// COMPLETE list for its kind — everything the catalog declares today plus what
/// the census found — so adopting one is a replacement, never an append.
///
/// The rule everything here is written to: **a key name is not a kind.** Of nine
/// candidate gaps previously treated as id-keyed, four name a table that is not
/// keyed by that id and one names an image the client does not
/// contain. Every row below carries the number that justifies it, and the ones
/// that were rejected are recorded too, so the same plausible-sounding
/// declaration is not proposed a third time.
/// </summary>
public static class PortSatellites
{
    // ------------------------------------------------------------------- mob

    /// <summary>
    /// Measured: 9,764 mobs in Mob.wz/Mob001/Mob002/Mob2.
    ///
    /// What was NOT added, and why, so it is not proposed again:
    /// <c>Skill.wz/MobSkill.img</c> DOES NOT EXIST — not "has no row", there is
    /// no such image in any archive of the Skill family. Mob skills are 136
    /// per-id IMAGES at <c>Skill001.wz/MobSkill/&lt;id&gt;.img</c>, which is the
    /// shape <see cref="PortImageSatelliteSpec"/> below exists for.
    /// <c>Effect.wz/EliteMobEff.img</c> is a dense 0..27 index, not mob ids.
    /// </summary>
    public static readonly PortSatelliteSpec[] Mob =
    {
        // NEW, and unblocked rather than newly measured: both were found by the
        // census and both were held back because RoleOf had no arm for Effect.wz.
        // It has one now, so they are declared. Measured: ChaseEffect.img 2 rows,
        // 100% mob ids; MobEff.img 4 mob-id rows among 14 children -- a mixed
        // image, name-keyed for the rest, so a miss is ordinary.
        new PortSatelliteSpec("chase-effect", "effect", new[] { "ChaseEffect.img" },
            "Only two mobs in a v232 client have one, so having none is ordinary."),
        new PortSatelliteSpec("mob-effect", "effect", new[] { "MobEff.img" },
            "Only a handful of mobs have a row there, so having none is ordinary."),

        // Declared today. Measured: 9,915 rows, 98.5% of them mob ids, and
        // 99.99% of this client's mobs have one — the densest satellite there is.
        new("string", "string", new[] { "Mob.img" },
            "Without this the mob shows in game as its id."),

        // Declared today, and plural because it genuinely is: hit and death
        // clips are in Mob.img (7,454 rows, 75.7% of mobs) and boss voice lines
        // in MobVoice.img (19 rows, 0.19%). Taking only the first hit gives a
        // silent boss.
        new PortSatelliteSpec("sound", "sound", new[] { "Mob.img", "MobVoice.img" }, null,
            EveryImage: true),

        // NEW. Measured: 9,532 rows, 100% mob ids, and 97.62% of every mob in
        // the client has one — the second-widest mob satellite in the client and
        // undeclared until now. It is what the in-game monster search reads, so
        // a mob ported without it exists and cannot be looked up.
        new PortSatelliteSpec("mob-location", "etc", new[] { "MobLocation.img" },
            "The mob will not appear in the in-game monster search."),

        // NEW. Measured: 476 rows, 100% mob ids, 4.88% of mobs — the monster
        // book card. Most mobs have no card, so an absent row is ordinary.
        new PortSatelliteSpec("monster-book", "string", new[] { "MonsterBook.img" },
            "Only mobs with a Monster Book card have a row here, so having none is ordinary."),
    };

    // ------------------------------------------------------------------- npc

    /// <summary>
    /// Measured: 10,742 NPCs in Npc.wz.
    ///
    /// The correction this array exists for: the catalog declares
    /// <c>Sound.wz/Npc.img</c>, and NO archive of the Sound family holds an
    /// image by that name. NPC voice is <c>Sound001.wz/Voice.img</c>, whose 53
    /// children include 19 all-digit names — all 19 of which are real NPC ids.
    /// The role reaches it for free: <c>StripArchiveSuffix</c> maps Sound001.wz
    /// to "sound", <c>FindImage</c> searches every archive of the role, and no
    /// image name is duplicated anywhere in the Sound family, so the "first hit
    /// in unordered session order" hazard does not bite here.
    /// </summary>
    public static readonly PortSatelliteSpec[] Npc =
    {
        // Declared today. Measured: 10,955 rows, 98% npc ids, 99.99% cover.
        new("string", "string", new[] { "Npc.img" },
            "Without this the NPC shows in game as its id."),

        // REPOINTED from the non-existent "Npc.img". Measured: 19 rows, all 19
        // real NPC ids, 0.18% of NPCs — so almost every NPC legitimately has no
        // voice, and the note has to say so or every port reads as damaged.
        new PortSatelliteSpec("sound", "sound", new[] { "Voice.img" },
            "Only 19 NPCs in a v232 client have recorded voice lines, so having none is ordinary."),

        // NEW. Measured: 10,744 rows, 100% npc ids, 100.00% cover — every single
        // NPC in the client has one. This is the widest satellite found anywhere
        // in the census, and it was undeclared.
        new PortSatelliteSpec("npc-location", "etc", new[] { "NpcLocation.img" },
            "The NPC will not appear in the in-game NPC search."),
    };

    // ------------------------------------------------------------------ item

    /// <summary>
    /// Measured: 86,535 items across Item.wz and Character.wz.
    ///
    /// What was not added as an ordinary item-id satellite, with the number that
    /// rejected it. SetItemInfo and SetEff are now declared through their real
    /// derived set key; the others remain outside this table:
    /// <list type="bullet">
    /// <item><c>Sound.wz/Android.img</c> — 107 rows named 0001..0167, 0% item
    /// ids. Keyed by android PRESET; Etc.wz/Android/ holds the same 107 as
    /// per-preset images, and an item reaches one through <c>info/android</c>.</item>
    /// <item><c>Sound.wz/ItemPot.img</c> — 2 rows, 1000 and 2000, 0% item ids.</item>
    /// </list>
    ///
    /// The Effect.wz block that used to sit at the end of that list is gone
    /// because it was fixed rather than argued with. <c>RoleOf</c> now answers
    /// "effect" for that stem, so <c>ItemEff.img</c>, <c>PetEff.img</c> and
    /// <c>CharacterEff.img</c> are declared below — see docs/satellites.md §3.1
    /// for what they were blocked on and <see cref="PortService.RoleOf"/> for
    /// the arm. <c>SetEff.img</c> is declared separately through setItemID below:
    /// measured at 260 rows in the same set-id space, not the item-id space.
    /// </summary>
    public static readonly PortSatelliteSpec[] Item =
    {
        // Measured: 672 rows in 1..999, 0% item ids. This is intentionally
        // derived from info/setItemID instead of looking up the item id in the
        // set-id namespace. Required means an equip never lands with a dangling
        // set pointer or attached to a different target definition by accident.
        new PortSatelliteSpec("set-info", "etc", new[] { "SetItemInfo.img" },
            "An item only needs this when its info/setItemID names a set.",
            KeyPath: "info/setItemID", RequiredWhenReferenced: true),

        // Optional visual effect for the same derived set id. A missing row is
        // normal; a discovered one is carried atomically by PortService.
        new PortSatelliteSpec("set-effect", "effect", new[] { "SetEff.img" },
            "Only sets with a visual set effect have a row there, so having none is ordinary.",
            KeyPath: "info/setItemID"),

        // NEW, and unblocked rather than newly measured. Measured: ItemEff.img
        // 1,307 rows / 95.3% item ids, PetEff.img 422 / 100%, CharacterEff.img
        // 72 / 100%. Three specs and not one, because unlike the String row
        // these are not alternatives -- an item may have a row in more than one.
        new PortSatelliteSpec("item-effect", "effect", new[] { "ItemEff.img" },
            "Only items with a use effect have a row there, so having none is ordinary."),
        new PortSatelliteSpec("pet-effect", "effect", new[] { "PetEff.img" },
            "Only pets have a row there, so an ordinary item having none is normal."),
        new PortSatelliteSpec("character-effect", "effect", new[] { "CharacterEff.img" },
            "Only items that draw an effect on the character have a row there, so having "
            + "none is ordinary."),

        // Declared today. Alternatives, not additions: an item's name is in
        // exactly one of these, and probing all six would put five "not in the
        // source" rows on every item. Measured cover, per image: Consume 25.9%,
        // Etc 8.4%, Cash 3.7%, Ins 2.7%, Pet 1.3%, Eqp per slot.
        new("string", "string",
            new[] { "Eqp.img", "Consume.img", "Ins.img", "Etc.img", "Cash.img", "Pet.img" },
            "Without this the item has no name or description in game."),

        // Declared today. Measured: Item.img 294 rows (97.3% item ids), Eqp.img
        // exactly ONE row in this client (1012526/ice) — kept because another
        // build's may be full, and an empty image and a missing image are
        // different findings.
        new PortSatelliteSpec("sound", "sound", new[] { "Item.img", "Eqp.img" }, null,
            EveryImage: true),

        // NEW, and the largest of the item gaps. Measured: 731 rows, 99.3% item
        // ids. 172 of those rows are UOLs into siblings of the same image, which
        // is exactly the shape CarrySiblings was built for — a pet ported
        // without them lands links into nothing.
        new PortSatelliteSpec("sound", "sound", new[] { "Pet.img" },
            "Only pets have a row here, so an ordinary item having none is normal."),

        // NEW. Measured: CashEffect.img 34 rows (94.1% item ids),
        // ConsumeEffect.img 3 rows (100%). Small, and real: both are keyed by
        // the item's own id and neither was carried.
        new PortSatelliteSpec("sound", "sound", new[] { "CashEffect.img", "ConsumeEffect.img" },
            "Only a few cash and consumable items have a use-effect sound, so having none is ordinary.",
            EveryImage: true),

        // NEW. Measured: 1,091 rows, 99.8% item ids. Without it a ported pet is
        // mute — it has a name, a picture and nothing to say.
        new PortSatelliteSpec("pet-dialog", "string", new[] { "PetDialog.img" },
            "Only pets have dialogue, so an ordinary item having none is normal."),

        // NEW. Measured: 32 rows, 100% item ids, written zero-padded to eight
        // digits (05040000) — which FindById already handles by probing every
        // padded width up to 10.
        new PortSatelliteSpec("cash-search", "string", new[] { "CashItemSearch.img" },
            "Only items listed in the cash shop's search index have a row, so having none is ordinary."),

        // Declared today. The exception to name-keying: Commodity's 9,697 rows
        // are named "0".."9696" — the row's POSITION — and the item id is inside
        // at ItemId, which is why this is scanned by MatchField and not probed.
        // Measured 0.1% "keyed" by row name, and that number being near zero is
        // the measurement working rather than failing.
        new PortSatelliteSpec("shop", "etc", new[] { "Commodity.img" },
            "Only cash items are listed there, so an ordinary item having no row is normal.",
            MatchField: "ItemId",
            UniqueFields: new[] { "SN" }),

        // NEW. Measured: 237 rows, 100% item ids — the contents of a cash
        // package, keyed by the package item's own id. A package ported without
        // it is bought and delivers nothing.
        new PortSatelliteSpec("cash-package", "etc", new[] { "CashPackage.img" },
            "Only cash packages have a row here, so an ordinary item having none is normal."),
    };

    // ----------------------------------------------------------------- skill

    /// <summary>
    /// Measured: 9,959 skills across the Skill family.
    ///
    /// NOTHING IS ADDED HERE, and that is the finding. Both gaps recorded for
    /// this kind are misreadings of a key name:
    /// <list type="bullet">
    /// <item><c>FieldSkill.img</c> — Skill001.wz's has 28 rows numbered
    /// 100000..100027 and Sound.wz's has 15 drawn from that range. 0% are skill
    /// ids. A field skill is a separate entity with its own numbering.</item>
    /// <item><c>Summon.img</c> — Effect.wz's has 105 rows and Sound.wz's 14, in
    /// the range 0..117. One of the 119 collides with a skill id by
    /// coincidence.</item>
    /// </list>
    /// <c>Effect.wz/SkillName1..4.img</c> ARE keyed by skill id, but only 31-47%
    /// of their rows resolve to a skill this client has, and they are blocked on
    /// the Effect role besides. Left out on both counts.
    /// <c>Etc.wz/NotShowRemoteSkill.img</c> (163 rows, 91.4% skill ids) is a
    /// client display blacklist, not part of the skill.
    /// </summary>
    public static readonly PortSatelliteSpec[] Skill =
    {
        // Declared today. Measured: 11,180 rows of which 9,152 are skills —
        // 91.9% of every skill in the client has a name row. The other 2,028
        // rows are MOB skill ids (120, 121, 130, 200, ...), so a count of this
        // image is not a count of skills.
        new("string", "string", new[] { "Skill.img" },
            "Without this the skill is nameless and has no description in game."),

        // Declared today. Measured: Skill.img 3,920 rows (87.6% skill ids,
        // 34.5% of skills), SkillVoice.img 553 rows (96.2%, 5.3%).
        new PortSatelliteSpec("sound", "sound", new[] { "Skill.img", "SkillVoice.img" }, null,
            EveryImage: true),
    };

    // ------------------------------------------------------------- map, etc.

    /// <summary>
    /// Measured: 17,442 maps in Map002.wz.
    /// </summary>
    public static readonly PortSatelliteSpec[] Map =
    {
        // Declared today. The label is 'mapName', not 'name'.
        new("string", "string", new[] { "Map.img" },
            "Without this the map is nameless on the world map and in the minimap header.",
            NameField: "mapName"),

        // NEW. Measured: 6,767 rows, 99.6% map ids, 38.63% of maps.
        new PortSatelliteSpec("map-object", "etc", new[] { "MapObjectInfo.img" },
            "Not every map has one, so having no row here is ordinary."),
    };

    /// <summary>
    /// Measured: 1,195 reactors. Sound.wz/Reactor.img is 445 rows, 97.5%
    /// reactor ids, 36.3% cover. Nothing else in any of the 25 archives is keyed
    /// by a reactor id, so this kind has no gap.
    /// </summary>
    public static readonly PortSatelliteSpec[] Reactor =
    {
        new("sound", "sound", new[] { "Reactor.img" },
            "Not every reactor makes a noise, so having no row here is ordinary."),
    };

    // -------------------------------------------------- map sound references

    /// <summary>
    /// The two undeclared sound references on the map kind. These are NOT
    /// satellites — they are named references, the same shape <c>info/bgm</c>
    /// is, and they belong in the map kind's <c>Named:</c> array beside it.
    ///
    /// Measured over all 17,442 maps of Map002.wz: <c>info/bgmSub</c> is carried
    /// by 351 maps, of which 154 hold an actual entry, naming 20 distinct clips;
    /// <c>info/AmbientBGM</c> is carried by 4,281 maps, of which 91 name a clip,
    /// and all 12 distinct values resolve to a real clip in the Sound family.
    ///
    /// <c>Split: true</c> is right for both, and the interesting part is the
    /// fourth shape it does NOT read. AmbientBGM is written four ways: with the
    /// suffix (<c>Ambience.img/heavyrain</c>, 76 maps), without it
    /// (<c>Ambience/wind</c>, 5), as a three-segment path
    /// (<c>SoundEff/PL_AfterLand/clear_day</c>, 1 — <c>InsideArchive</c> joins
    /// everything after the first segment, so this works), and as a bare clip
    /// name (<c>blizzard_soft</c>, 6). The bare form returns null, which
    /// PortService already reports as "not a shape this can read" rather than
    /// guessing — and that is the right outcome, even though
    /// <c>Sound.wz/Ambience.img/blizzard_soft</c> is a real 9,534 ms clip, so
    /// the client is evidently resolving a bare name inside Ambience.img.
    /// </summary>
    public static readonly PortNamedRef[] MapSound =
    {
        new("info/bgmSub/*", "sound", "", Image: false, Split: true, "secondary background music"),
        new("info/AmbientBGM", "sound", "", Image: false, Split: true, "ambient sound"),
    };

    // ------------------------------------- satellites that are images, not rows

    /// <summary>
    /// A satellite that is a whole IMAGE named <c>&lt;id&gt;.img</c> under a
    /// directory, rather than a row inside a shared image.
    ///
    /// <see cref="PortService"/>'s <c>FindSatellite</c> returns a
    /// <see cref="WzImageProperty"/>, so this address is inexpressible there —
    /// which is not a cosmetic limitation. The mob kind declares
    /// <c>PortReference("info/skill/*/skill", Role: "skill", Image:
    /// "MobSkill.img")</c>, and there is no <c>MobSkill.img</c> in any archive
    /// of the Skill family. That reference has been resolving to nothing,
    /// silently, for as long as it has existed.
    ///
    /// The shape is rare, and that was measured rather than assumed: every
    /// directory in all 25 archives whose images are id-named — 50 of them — was
    /// crossed against every kind's id set. Exactly one is a satellite. All the
    /// other fully-keyed ones ARE entry archives of an existing kind (Npc/,
    /// Mob*/, Reactor/, Morph/, Character/&lt;slot&gt;, Item/Pet,
    /// Map002/Map/MapN), and the remainder are keyed by something that is not an
    /// entry id (Etc/Android by preset, Etc/Achievement by achievement id).
    /// </summary>
    /// <param name="Kind">The part kind, as <see cref="PortSatelliteSpec.Kind"/>.</param>
    /// <param name="Role">
    /// The archive role, as <see cref="PortSatelliteSpec.Role"/> — and subject to
    /// the same trap: a role <c>RoleOf</c> cannot return makes the satellite
    /// permanently absent rather than failing loudly.
    /// </param>
    /// <param name="Directory">
    /// The directory at the archive's root that holds the images:
    /// <c>"MobSkill"</c>.
    /// </param>
    /// <param name="KeyFrom">
    /// Where in the ENTRY to read the key, slash-separated with "*" for every
    /// child, as <see cref="PortReference.Path"/> is. Null means the entry's own
    /// id.
    ///
    /// A mob skill is the case that forces this: the image is keyed by a mob
    /// skill id, and a mob reaches it through <c>info/skill/N/skill</c>. Looking
    /// it up by the mob's own id would find nothing — or, worse, find some other
    /// mob skill that happens to share the number.
    /// </param>
    /// <param name="CarriesArt">
    /// Whether the image holds canvases of its own, which decides whether
    /// copying it is a copy of text or a port of art.
    ///
    /// Measured across all 136 MobSkill images: 109 carry canvases, 16,971
    /// canvases in total, 7,892 with an <c>_inlink</c> and 4,124 with an
    /// <c>_outlink</c>, plus 574 UOLs. That is why this spec is declared and NOT
    /// yet switched on: writing one needs the art pass and the outlink
    /// resolution an entry copy gets, and a satellite that moved a 65 KB image
    /// and dropped its 4,124 outlinks would be a new silent failure rather than
    /// a fix for the old one.
    /// </param>
    public sealed record PortImageSatelliteSpec(
        string Kind,
        string Role,
        string Directory,
        string? KeyFrom,
        bool CarriesArt,
        string? AbsentNote = null);

    /// <summary>
    /// The one instance of the shape in a v232 client.
    ///
    /// Declared so the address arithmetic below has something real to be tested
    /// against, and so the finding is in code rather than only in a document.
    /// It is deliberately NOT wired into the mob kind's satellite list: see
    /// <see cref="PortImageSatelliteSpec.CarriesArt"/> for why, and
    /// docs/satellites.md §3.3 for the numbers.
    /// </summary>
    public static readonly PortImageSatelliteSpec MobSkillImage =
        new("mob-skill", "skill", "MobSkill", "info/skill/*/skill", CarriesArt: true,
            "Only mobs that declare an attack in info/skill have one.");

    /// <summary>
    /// The keys an entry names for an image satellite: the ids at
    /// <see cref="PortImageSatelliteSpec.KeyFrom"/>, or the entry's own id when
    /// that is null.
    ///
    /// Read as text and parsed, never matched on property type. The id at one of
    /// these paths may be stored as an Int or as a String and leading zeros are
    /// ordinary — a reader that pattern-matched on <c>WzIntProperty</c> would
    /// miss the string form, which is the mistake a map's <c>info/link</c>
    /// ("003000000", a WzStringProperty on 6,850 maps) already punishes
    /// elsewhere in this codebase.
    /// </summary>
    /// <remarks>
    /// <paramref name="entry"/> is a <see cref="WzObject"/> rather than a
    /// <see cref="WzImageProperty"/> because an entry is genuinely either: a mob
    /// is <c>Mob.wz/8800000.img</c>, an image, and an item is
    /// <c>Item.wz/Consume/0200.img/02000000</c>, a property inside one. Taking
    /// only the property type is how a reader ends up working for items and
    /// silently returning nothing for every mob.
    /// </remarks>
    public static IReadOnlyList<int> Keys(
        WzObject? entry, PortImageSatelliteSpec spec, int entryId)
    {
        if (spec.KeyFrom == null)
        {
            return entryId > 0 ? new[] { entryId } : Array.Empty<int>();
        }

        WzPropertyCollection? children = entry switch
        {
            WzImage image => image.WzProperties,
            WzImageProperty property => property.WzProperties,
            _ => null,
        };

        if (children == null)
        {
            return Array.Empty<int>();
        }

        string[] steps = spec.KeyFrom.Split('/');
        List<int> found = new();
        foreach (WzImageProperty child in children)
        {
            if (steps[0] == "*" || string.Equals(child.Name, steps[0], StringComparison.Ordinal))
            {
                Walk(child, steps, 1, found);
            }
        }

        return found;
    }

    private static void Walk(WzImageProperty node, string[] steps, int at, List<int> found)
    {
        if (at == steps.Length)
        {
            string? text = node.WzValue?.ToString();
            if (text != null
                && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out int id)
                && id > 0
                && !found.Contains(id))
            {
                found.Add(id);
            }

            return;
        }

        WzPropertyCollection? children = node.WzProperties;
        if (children == null)
        {
            return;
        }

        foreach (WzImageProperty child in children)
        {
            if (steps[at] == "*" || string.Equals(child.Name, steps[at], StringComparison.Ordinal))
            {
                Walk(child, steps, at + 1, found);
            }
        }
    }

    /// <summary>
    /// Where an image satellite for this key lives, across every archive of the
    /// role, or null when no archive holds one.
    ///
    /// The directory is looked for at the archive ROOT only. That is not a
    /// simplification — it is what was measured: the one real instance,
    /// <c>Skill001.wz/MobSkill</c>, is a root directory, and searching deeper
    /// would start matching directories that only look right.
    ///
    /// Zero-padding is probed the same way <c>StringEditService.FindById</c>
    /// probes it for rows: the plain decimal first, then every left-padded width
    /// up to 10. Both spellings are real in one client — mob skills are written
    /// <c>141.img</c> and reactors <c>0100000.img</c> — so probing one and
    /// giving up is how a satellite that is sitting there reads as absent.
    /// </summary>
    public static (WzImage Image, string Path)? Locate(
        IEnumerable<OpenFile> archives, PortImageSatelliteSpec spec, int key)
    {
        if (key <= 0)
        {
            return null;
        }

        string plain = key.ToString(CultureInfo.InvariantCulture);
        foreach (OpenFile file in archives)
        {
            WzDirectory? root = file.WzFile?.WzDirectory;
            if (root == null)
            {
                continue;
            }

            WzDirectory? directory = root.WzDirectories.FirstOrDefault(
                d => string.Equals(d.Name, spec.Directory, StringComparison.OrdinalIgnoreCase));
            if (directory == null)
            {
                continue;
            }

            for (int width = plain.Length; width <= 10; width++)
            {
                WzImage? image = directory.GetImageByName(plain.PadLeft(width, '0') + ".img");
                if (image == null)
                {
                    continue;
                }

                return (image, WzPath.Child(
                    WzPath.Child(file.Id, directory.Name), image.Name));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this image holds art of its own — the question that decides
    /// whether copying it is a node copy or a port.
    ///
    /// Counted rather than sampled, and never through a UOL: a link's children
    /// belong to another node, and walking one is how this codebase has produced
    /// uncatchable stack overflows. Bounded by depth for the same reason.
    /// </summary>
    public static bool CarriesArt(WzImage? image)
    {
        if (image == null)
        {
            return false;
        }

        WzSessionService.EnsureParsed(image);
        foreach (WzImageProperty property in image.WzProperties)
        {
            if (HasCanvas(property, 0))
            {
                return true;
            }
        }

        return false;
    }

    private const int MaxArtDepth = 32;

    private static bool HasCanvas(WzImageProperty property, int depth)
    {
        if (property is WzCanvasProperty)
        {
            return true;
        }

        if (depth >= MaxArtDepth || property is WzUOLProperty)
        {
            return false;
        }

        WzPropertyCollection? children = property.WzProperties;
        if (children == null)
        {
            return false;
        }

        foreach (WzImageProperty child in children)
        {
            if (HasCanvas(child, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
