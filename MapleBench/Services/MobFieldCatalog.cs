namespace MapleBench.Services;

/// <summary>How a mob field should be presented and validated.</summary>
public enum MobFieldKind
{
    /// <summary>A number. The overwhelming majority.</summary>
    Int,

    /// <summary>A 0/1 int the client treats as a boolean — render a checkbox.</summary>
    Flag,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>The packed elemental table, e.g. "F2I1" — fire weak, ice immune.</summary>
    Elem,
}

/// <summary>
/// One field of a mob's <c>info</c> node, described well enough to render.
/// </summary>
/// <param name="Key">The raw WZ property name. This is what gets written.</param>
/// <param name="Label">What a person calls it.</param>
/// <param name="Group">Section heading on the card.</param>
/// <param name="Unit">"ms", "%", "px" — appended after the input. Null for a bare number.</param>
/// <param name="Hint">One line of help, shown where there is room.</param>
public sealed record MobFieldSpec(
    string Key,
    string Label,
    string Group,
    MobFieldKind Kind = MobFieldKind.Int,
    string? Unit = null,
    string? Hint = null);

/// <summary>
/// The names, groupings and labels for a mob's <c>info</c> node.
///
/// This lives in C#, and deliberately not in the view, because three separate
/// consumers need the same answer: the mob card's layout, the bulk editor's field
/// picker, and validation. The reference implementation this is modelled on kept
/// its equivalent inside a React component, which is exactly why its bulk-edit
/// dialog carries a second, shorter, partly-wrong field list — including one entry
/// (<c>count</c>) that is not a Mob.wz field at all.
///
/// The catalog is descriptive, not prescriptive: a mob may carry fields that are
/// not here (they surface as "extra"), and may be missing most of the ones that
/// are. Nothing is written unless the user edits it.
///
/// Labels matter more than they look. "pushed" is the single most valuable
/// relabel in the set — it is knockback resistance, and left as "pushed" it is a
/// number people leave alone because they cannot guess what it does.
/// </summary>
public static class MobFieldCatalog
{
    /// <summary>Section order on the card. Anything uncatalogued lands in "Other".</summary>
    public static readonly string[] GroupOrder =
    {
        "Identity", "Vitals", "Combat", "Defense", "Movement",
        "Recovery", "Flags", "Rewards", "Behaviour", "Display", "Other",
    };

    private static readonly MobFieldSpec[] All =
    {
        // --- Identity -------------------------------------------------------
        new("level",              "Level",                  "Identity"),
        new("mobType",            "Mob type",               "Identity", MobFieldKind.Text),
        new("category",           "Category",               "Identity"),
        new("link",               "Links to",               "Identity", MobFieldKind.Text,
            Hint: "This mob's real data lives in the linked image. Edits here will not take effect."),
        new("summonType",         "Summon type",            "Identity"),

        // --- Vitals ---------------------------------------------------------
        new("maxHP",              "Max HP",                 "Vitals"),
        new("maxMP",              "Max MP",                 "Vitals"),
        new("hp",                 "HP",                     "Vitals"),
        new("mp",                 "MP",                     "Vitals"),
        new("exp",                "Experience",             "Vitals"),

        // --- Combat ---------------------------------------------------------
        new("PADamage",           "Physical attack",        "Combat"),
        new("MADamage",           "Magic attack",           "Combat"),
        new("acc",                "Accuracy",               "Combat"),
        new("eva",                "Evasion",                "Combat"),
        new("pushed",             "Knockback resistance",   "Combat", Hint: "Damage needed to interrupt the mob."),
        new("fixedDamage",        "Fixed damage",           "Combat",
            Hint: "Every hit deals exactly this. -1 disables it — it is a number, not a flag."),

        // --- Defense --------------------------------------------------------
        new("PDDamage",           "Physical defense",       "Defense"),
        new("MDDamage",           "Magic defense",          "Defense"),
        new("PDRate",             "Physical defense %",     "Defense", MobFieldKind.Int, "%"),
        new("MDRate",             "Magic defense %",        "Defense", MobFieldKind.Int, "%"),
        new("elemAttr",           "Elemental table",        "Defense", MobFieldKind.Elem,
            Hint: "Packed pairs, e.g. F2I1 — fire weak, ice immune."),

        // --- Movement -------------------------------------------------------
        new("speed",              "Speed",                  "Movement"),
        new("flySpeed",           "Fly speed",              "Movement"),
        new("chaseSpeed",         "Chase speed",            "Movement"),

        // --- Recovery -------------------------------------------------------
        new("hpRecovery",         "HP recovery",            "Recovery"),
        new("mpRecovery",         "MP recovery",            "Recovery"),

        // --- Flags ----------------------------------------------------------
        new("boss",               "Boss",                   "Flags", MobFieldKind.Flag),
        new("undead",             "Undead",                 "Flags", MobFieldKind.Flag),
        new("firstAttack",        "Attacks on sight",       "Flags", MobFieldKind.Flag),
        new("bodyAttack",         "Body attack",            "Flags", MobFieldKind.Flag),
        new("invincible",         "Invincible",             "Flags", MobFieldKind.Flag),
        new("notAttack",          "Cannot be attacked",     "Flags", MobFieldKind.Flag),
        new("changeableAttr",     "Changeable element",     "Flags", MobFieldKind.Flag),
        new("escort",             "Escort",                 "Flags", MobFieldKind.Flag),
        new("partyBonusMob",      "Party bonus mob",        "Flags", MobFieldKind.Flag),
        new("noFlip",             "Never flips",            "Flags", MobFieldKind.Flag),
        new("onlyNormalAttack",   "Normal attacks only",    "Flags", MobFieldKind.Flag),

        // --- Rewards --------------------------------------------------------
        new("publicReward",       "Public reward",          "Rewards", MobFieldKind.Flag),
        new("explosiveReward",    "Explosive reward",       "Rewards", MobFieldKind.Flag),
        new("dropItemPeriod",     "Drop period",            "Rewards"),
        new("rareItemDropLevel",  "Rare drop level",        "Rewards"),
        new("getCP",              "Carnival points",        "Rewards"),
        new("charismaEXP",        "Charisma EXP",           "Rewards"),

        // --- Behaviour ------------------------------------------------------
        new("removeAfter",        "Despawn after",          "Behaviour", MobFieldKind.Int, "ms"),
        new("chargeCount",        "Charge count",           "Behaviour"),
        new("mobZone",            "Mob zone",               "Behaviour"),
        new("buff",               "Buff on hit",            "Behaviour",
            Hint: "-1 when unset."),
        new("ignoreFieldOut",     "Ignores field-out",      "Behaviour", MobFieldKind.Flag),

        // --- Display --------------------------------------------------------
        new("hpTagColor",         "HP bar colour",          "Display"),
        new("hpTagBgcolor",       "HP bar background",      "Display"),
        new("hideHP",             "Hide HP bar",            "Display", MobFieldKind.Flag),
        new("hideName",           "Hide name",              "Display", MobFieldKind.Flag),
        new("speakWidth",         "Speech width",           "Display", MobFieldKind.Int, "px"),
    };

    private static readonly Dictionary<string, MobFieldSpec> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MobFieldSpec> Fields => All;

    public static MobFieldSpec? Find(string key) =>
        ByKey.TryGetValue(key, out MobFieldSpec? spec) ? spec : null;

    /// <summary>
    /// Where an uncatalogued key should appear. Everything unknown is still shown
    /// — a field this catalog has never heard of is exactly the thing a user
    /// editing an unusual client most needs to see.
    /// </summary>
    public static MobFieldSpec Unknown(string key) =>
        new(key, key, "Other", MobFieldKind.Text);

    /// <summary>The catalog's index for a group, for stable ordering.</summary>
    public static int GroupRank(string group)
    {
        int index = Array.IndexOf(GroupOrder, group);
        return index < 0 ? GroupOrder.Length : index;
    }
}
