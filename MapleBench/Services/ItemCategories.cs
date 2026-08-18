namespace MapleBench.Services;

/// <summary>
/// Maps MapleStory item IDs onto the categories the cash shop UI groups by.
///
/// The client derives these from the ID itself rather than from a lookup table,
/// so this is reproducible without needing any extra WZ file open.
/// </summary>
public static class ItemCategories
{
    public static readonly string[] TopLevel =
    {
        "Equipment", "Use", "Setup", "Etc", "Cash", "Pet", "Unknown",
    };

    public static (string Category, string SubCategory) Classify(int itemId)
    {
        if (itemId <= 0)
            return ("Unknown", "");

        int type = itemId / 1000000;
        return type switch
        {
            1 => ("Equipment", EquipSlot(itemId)),
            2 => ("Use", UseGroup(itemId)),
            3 => ("Setup", ""),
            4 => ("Etc", ""),
            5 => CashOrPet(itemId),
            _ => ("Unknown", ""),
        };
    }

    /// <summary>Pets share the 5xxxxxx range with cash items but sit at 500xxxx.</summary>
    private static (string, string) CashOrPet(int itemId)
    {
        int prefix = itemId / 10000;
        return prefix == 500 ? ("Pet", "") : ("Cash", "");
    }

    /// <summary>
    /// The equip slot is encoded in the first three digits.  Weapons occupy
    /// 130-170 and are named individually because "weapon" alone is useless
    /// when you are looking for a specific one.
    /// </summary>
    private static string EquipSlot(int itemId) => (itemId / 10000) switch
    {
        100 => "Hat",
        101 => "Face Accessory",
        102 => "Eye Decoration",
        103 => "Earring",
        104 => "Top",
        105 => "Overall",
        106 => "Bottom",
        107 => "Shoes",
        108 => "Glove",
        109 => "Shield",
        110 => "Cape",
        111 => "Ring",
        112 => "Pendant",
        113 => "Belt",
        114 => "Medal",
        115 => "Shoulder",
        116 => "Pocket Item",
        117 => "Badge",
        118 => "Emblem",
        119 => "Powder Keg",
        120 => "Android Heart",
        121 => "Totem",
        122 => "Totem",
        130 => "One-handed Sword",
        131 => "One-handed Axe",
        132 => "One-handed Blunt",
        133 => "Dagger",
        134 => "Katara",
        135 => "Wand",
        136 => "Staff",
        137 => "Wand",
        138 => "Staff",
        140 => "Two-handed Sword",
        141 => "Two-handed Axe",
        142 => "Two-handed Blunt",
        143 => "Spear",
        144 => "Polearm",
        145 => "Bow",
        146 => "Crossbow",
        147 => "Claw",
        148 => "Knuckle",
        149 => "Gun",
        150 => "Mace",
        151 => "Wand",
        152 => "Staff",
        153 => "Cane",
        156 => "Chain",
        157 => "Magic Arrow",
        158 => "Card",
        159 => "Orb",
        160 => "Dragon Essence",
        161 => "Soul Shooter",
        162 => "Desperado",
        163 => "Energy Sword",
        164 => "Espresso Machine",
        165 => "Whip Blade",
        166 => "Scepter",
        167 => "Whistle",
        168 => "Ritual Fan",
        169 => "Gauntlet",
        170 => "Ancient Bow",
        180 => "Mechanical Heart",
        181 => "Pet Equip",
        190 => "Mount",
        191 => "Mount",
        192 => "Mount",
        194 => "Dragon Equip",
        195 => "Mechanic Equip",
        _ => "Other",
    };

    private static string UseGroup(int itemId) => (itemId / 10000) switch
    {
        200 => "Potion",
        201 => "Potion",
        202 => "Potion",
        203 => "Potion",
        204 => "Scroll",
        205 => "Scroll",
        206 => "Arrow",
        207 => "Bullet",
        208 => "Throwing Star",
        209 => "Monster Card",
        210 => "Bait",
        211 => "Fishing",
        212 => "Consumable",
        213 => "Consumable",
        214 => "Consumable",
        _ => "Other",
    };

    /// <summary>
    /// The .img holding an item's data, e.g. 2040000 -> "0204.img".  Cash and
    /// use items are grouped in hundred-thousands; equips get one file each.
    /// </summary>
    public static string DataImageName(int itemId)
    {
        if (itemId / 1000000 == 1)
            return itemId.ToString("D8") + ".img";
        return (itemId / 10000).ToString("D4") + ".img";
    }
}
