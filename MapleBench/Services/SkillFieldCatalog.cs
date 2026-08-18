namespace MapleBench.Services;

/// <summary>How a skill field should be presented and validated.</summary>
public enum SkillFieldKind
{
    /// <summary>A number. The overwhelming majority.</summary>
    Int,

    /// <summary>A 0/1 int the client treats as a boolean — render a checkbox.</summary>
    Flag,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>An (x, y) pair — the corners of an attack box. Written "12, 34".</summary>
    Point,
}

/// <summary>
/// One field of a skill's level data, described well enough to render.
/// </summary>
/// <param name="Key">The raw WZ property name. This is what gets written.</param>
/// <param name="Label">What a person calls it.</param>
/// <param name="Group">Section heading on the card / column group in the table.</param>
/// <param name="Unit">"s", "ms", "%", "px" — appended after the value. Null for a bare number.</param>
/// <param name="Hint">One line of help, shown where there is room.</param>
public sealed record SkillFieldSpec(
    string Key,
    string Label,
    string Group,
    SkillFieldKind Kind = SkillFieldKind.Int,
    string? Unit = null,
    string? Hint = null);

/// <summary>
/// The names, groupings, labels and units for a skill's per-level fields.
///
/// Same shape and same reasoning as <see cref="MobFieldCatalog"/>: it lives in C#
/// because three consumers need the same answer — the level table's column
/// headers, the bulk editor's field picker, and write-time type inference — and a
/// copy of it inside a view component is how the reference app ended up with two
/// disagreeing field lists.
///
/// Every key here was taken from a v232 client rather than from a wiki. The
/// frequency counts in the comments are the number of skills whose <c>common</c>
/// block carries that key, out of 3,937 that have one.
///
/// The unit column is the part that earns its keep, and it is also the part the
/// documentation gets wrong. <c>time</c>, <c>subTime</c> and <c>cooltime</c> are
/// <b>seconds</b>, not milliseconds: Hyper Body (1301007) stores
/// <c>time = "80+12*x"</c> against <c>maxLevel = 10</c>, which is 200 — a
/// 200-second buff, and 200 ms of Hyper Body is not a thing. The millisecond
/// fields are the ones that say so (<c>cooltimeMS</c>) or that describe animation
/// (<c>attackDelay</c>, <c>ballDelay</c>). Labelling a seconds field "ms" would
/// have someone multiply by a thousand and wonder why the buff never ends.
///
/// The catalog is descriptive, not prescriptive: a skill may carry keys that are
/// not here (they surface under "Other" with their raw name, which is exactly what
/// someone editing a private-server client needs), and will be missing most of the
/// ones that are.
/// </summary>
public static class SkillFieldCatalog
{
    /// <summary>Section order. Anything uncatalogued lands in "Other".</summary>
    public static readonly string[] GroupOrder =
    {
        "Progression", "Damage", "Cost", "Timing", "Targeting",
        "Effects", "Stats", "Requirements", "Slots", "Other",
    };

    private static readonly SkillFieldSpec[] All =
    {
        // --- Progression ----------------------------------------------------
        new("maxLevel",        "Maximum level",          "Progression", SkillFieldKind.Int, null,
            Hint: "How many levels the common formulas are evaluated for. Only meaningful with a common block."),
        new("masterLevel",     "Master level",           "Progression", SkillFieldKind.Int, null,
            Hint: "The cap before a mastery book is used."),
        new("combatOrders",    "Combat Orders affects",  "Progression", SkillFieldKind.Flag),

        // --- Damage ---------------------------------------------------------
        new("damage",          "Damage",                 "Damage", SkillFieldKind.Int, "%",   // 1,362 skills
            Hint: "A percentage of the character's attack, not a flat number."),
        new("damagepc",        "Damage per count",       "Damage", SkillFieldKind.Int, "%"),
        new("attackCount",     "Hits per attack",        "Damage"),                            // 983
        new("bulletCount",     "Bullets fired",          "Damage"),                            // 124
        new("mobCount",        "Monsters hit",           "Damage"),                            // 1,278
        new("mastery",         "Mastery",                "Damage", SkillFieldKind.Int, "%"),   // 99
        new("pad",             "Attack power",           "Damage"),
        new("mad",             "Magic attack",           "Damage"),
        new("padX",            "Attack power bonus",     "Damage"),                            // 131
        new("madX",            "Magic attack bonus",     "Damage"),                            // 64
        new("fixdamage",       "Fixed damage",           "Damage", SkillFieldKind.Int, null,
            Hint: "Every hit deals exactly this, ignoring attack. -1 disables it."),
        new("criticaldamage",  "Critical damage",        "Damage", SkillFieldKind.Int, "%"),   // 82
        new("criticaldamageMin", "Min critical damage",  "Damage", SkillFieldKind.Int, "%"),
        new("criticaldamageMax", "Max critical damage",  "Damage", SkillFieldKind.Int, "%"),
        new("cr",              "Critical rate",          "Damage", SkillFieldKind.Int, "%"),   // 131
        new("damR",            "Damage increase",        "Damage", SkillFieldKind.Int, "%"),   // 184
        new("bdR",             "Boss damage",            "Damage", SkillFieldKind.Int, "%"),   // 66
        new("ignoreMobpdpR",   "Ignore monster DEF",     "Damage", SkillFieldKind.Int, "%"),   // 109
        new("dot",             "Damage over time",       "Damage", SkillFieldKind.Int, null,   // 106
            Hint: "Damage dealt each tick, for dotTime seconds."),
        new("dotTime",         "DoT duration",           "Damage", SkillFieldKind.Int, "s"),   // 91
        new("dotInterval",     "DoT tick interval",      "Damage", SkillFieldKind.Int, "s"),   // 88
        new("damPlus",         "Flat damage bonus",      "Damage"),                            // 47

        // --- Cost -----------------------------------------------------------
        new("mpCon",           "MP cost",                "Cost"),                              // 1,522
        new("hpCon",           "HP cost",                "Cost"),                              // 36
        new("mpRCon",          "MP cost (of max MP)",    "Cost", SkillFieldKind.Int, "%"),
        new("hpRCon",          "HP cost (of max HP)",    "Cost", SkillFieldKind.Int, "%"),     // 26
        new("forceCon",        "Force cost",             "Cost"),                              // 82
        new("bulletConsume",   "Bullets consumed",       "Cost"),                              // 16
        new("itemCon",         "Item consumed",          "Cost", SkillFieldKind.Int, null,
            Hint: "An item id, not a count — itemConNo holds the count."),
        new("itemConNo",       "Items consumed",         "Cost"),
        new("iceGageCon",      "Ice gauge cost",         "Cost"),                              // 100
        new("mpConReduce",     "MP cost reduction",      "Cost", SkillFieldKind.Int, "%"),

        // --- Timing ---------------------------------------------------------
        // Seconds unless the key says otherwise; see the class comment.
        new("time",            "Duration",               "Timing", SkillFieldKind.Int, "s"),   // 1,207
        new("subTime",         "Secondary duration",     "Timing", SkillFieldKind.Int, "s"),   // 166
        new("cooltime",        "Cooldown",               "Timing", SkillFieldKind.Int, "s"),   // 651
        new("cooltimeMS",      "Cooldown",               "Timing", SkillFieldKind.Int, "ms"),  // 27
        new("attackDelay",     "Attack delay",           "Timing", SkillFieldKind.Int, "ms"),  // 65
        new("ballDelay",       "Projectile delay",       "Timing", SkillFieldKind.Int, "ms"),  // 32
        new("cancelableTime",  "Cancelable after",       "Timing", SkillFieldKind.Int, "ms"),  // 88
        new("updatableTime",   "Refreshable after",      "Timing", SkillFieldKind.Int, "ms"),  // 64

        // --- Targeting ------------------------------------------------------
        new("lt",              "Attack box: top-left",   "Targeting", SkillFieldKind.Point, null,   // 1,309
            Hint: "Relative to the character. Stored as a point, so it is the same at every level."),
        new("rb",              "Attack box: bottom-right", "Targeting", SkillFieldKind.Point),      // 1,309
        new("lt2",             "Second box: top-left",   "Targeting", SkillFieldKind.Point),        // 71
        new("rb2",             "Second box: bottom-right", "Targeting", SkillFieldKind.Point),      // 71
        new("range",           "Range",                  "Targeting", SkillFieldKind.Int, "px"),    // 223

        // --- Effects --------------------------------------------------------
        new("prop",            "Chance to trigger",      "Effects", SkillFieldKind.Int, "%"),  // 500
        new("subProp",         "Secondary chance",       "Effects", SkillFieldKind.Int, "%"),  // 19
        new("hcProp",          "Hyper chance",           "Effects", SkillFieldKind.Int, "%"),  // 52
        new("stanceProp",      "Stance chance",          "Effects", SkillFieldKind.Int, "%"),  // 30
        new("morph",           "Morph id",               "Effects"),                           // 29
        new("speed",           "Movement speed",         "Effects", SkillFieldKind.Int, "%"),  // 43
        new("jump",            "Jump",                   "Effects", SkillFieldKind.Int, "%"),  // 7
        new("asrR",            "Abnormal status resist", "Effects", SkillFieldKind.Int, "%"),  // 71
        new("terR",            "Elemental resist",       "Effects", SkillFieldKind.Int, "%"),  // 52
        new("mhpR",            "Max HP",                 "Effects", SkillFieldKind.Int, "%"),  // 70
        new("mmpR",            "Max MP",                 "Effects", SkillFieldKind.Int, "%"),  // 36
        new("er",              "Evasion rate",           "Effects", SkillFieldKind.Int, "%"),  // 31
        new("speedMax",        "Speed cap",              "Effects", SkillFieldKind.Int, "%"),  // 38
        new("actionSpeed",     "Attack speed",           "Effects"),                           // 37
        new("psdSpeed",        "Passive speed",          "Effects", SkillFieldKind.Int, "%"),  // 54
        new("psdJump",         "Passive jump",           "Effects", SkillFieldKind.Int, "%"),  // 41
        new("expR",            "EXP gain",               "Effects", SkillFieldKind.Int, "%"),  // 16

        // --- Stats ----------------------------------------------------------
        new("pdd",             "Weapon defense",         "Stats"),
        new("mdd",             "Magic defense",          "Stats"),
        new("acc",             "Accuracy",               "Stats"),
        new("eva",             "Avoidability",           "Stats"),
        new("strX",            "STR bonus",              "Stats"),                             // 83
        new("dexX",            "DEX bonus",              "Stats"),                             // 92
        new("intX",            "INT bonus",              "Stats"),                             // 66
        new("lukX",            "LUK bonus",              "Stats"),                             // 68
        new("indiePad",        "Attack (independent)",   "Stats", SkillFieldKind.Int, null,    // 128
            Hint: "'indie' bonuses stack with every other source rather than replacing them."),
        new("indieMad",        "Magic attack (independent)", "Stats"),                         // 109
        new("indieDamR",       "Damage (independent)",   "Stats", SkillFieldKind.Int, "%"),    // 70

        // --- Requirements ---------------------------------------------------
        new("req",             "Required skills",        "Requirements", SkillFieldKind.Text, null,
            Hint: "A group of skill id -> level entries, not a single value."),
        new("reqLev",          "Required character level", "Requirements"),
        new("reqSkillLevel",   "Required skill level",   "Requirements"),
        new("reqGuildLevel",   "Required guild level",   "Requirements"),

        // --- Slots ----------------------------------------------------------
        // The generic carriers. The description string in String.wz refers to them
        // by name ("Attack Power: +#x, Magic ATT: +#y"), so what they mean is
        // per-skill and the label cannot say more than this honestly.
        new("x",               "Slot x",                 "Slots", SkillFieldKind.Int, null,    // 1,640
            Hint: "General-purpose value. The skill's description text refers to it as #x."),
        new("y",               "Slot y",                 "Slots", SkillFieldKind.Int, null,    // 866
            Hint: "General-purpose value, referred to as #y in the description."),
        new("z",               "Slot z",                 "Slots"),                             // 476
        new("u",               "Slot u",                 "Slots"),                             // 221
        new("v",               "Slot v",                 "Slots"),                             // 245
        new("w",               "Slot w",                 "Slots"),                             // 390
        new("s",               "Slot s",                 "Slots"),                             // 198
        new("q",               "Slot q",                 "Slots"),                             // 142
        new("t",               "Slot t",                 "Slots"),                             // 47

        // --- Other ----------------------------------------------------------
        new("hs",              "Description key",        "Other", SkillFieldKind.Text, null,   // 1,782 level nodes
            Hint: "Selects which description String.wz shows. Not a number."),
        new("dateExpire",      "Expires on",             "Other", SkillFieldKind.Text, null,
            Hint: "yyyyMMddHH. The skill stops working after this."),
    };

    private static readonly Dictionary<string, SkillFieldSpec> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SkillFieldSpec> Fields => All;

    public static SkillFieldSpec? Find(string key) =>
        ByKey.TryGetValue(key, out SkillFieldSpec? spec) ? spec : null;

    /// <summary>
    /// Where an uncatalogued key should appear. Everything unknown is still shown
    /// — a client that adds its own skill fields is precisely the case where
    /// hiding them would make this tool useless.
    /// </summary>
    public static SkillFieldSpec Unknown(string key) =>
        new(key, key, "Other", SkillFieldKind.Text);

    /// <summary>The catalog's index for a group, for stable ordering.</summary>
    public static int GroupRank(string group)
    {
        int index = Array.IndexOf(GroupOrder, group);
        return index < 0 ? GroupOrder.Length : index;
    }

    /// <summary>
    /// A key's position in the catalog, so columns come out in a stable, meaningful
    /// order rather than in whatever order the archive happens to store them.
    /// Uncatalogued keys sort after every known one, alphabetically among themselves.
    /// </summary>
    public static int Rank(string key)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return All.Length;
    }
}
