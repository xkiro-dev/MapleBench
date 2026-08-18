namespace MapleBench.Services.MapModel;

/// <summary>
/// What the editor is allowed to do with a top-level node of a map image.
/// </summary>
/// <remarks>
/// The classification is not a design; it is the census in
/// <c>docs/map-data-model.md</c> §10, taken across all 17,442 v232 map images,
/// which found that <b>40 of these kinds appear in fewer than 40 maps each</b>.
/// Those 40 are exactly the class of node a schema written from memory drops
/// silently, so they are named here rather than left to be rediscovered.
///
/// The doc reports 73 kinds and this table holds 80 names; re-running the sweep
/// found <b>81</b> distinct top-level names in the archive. The three numbers
/// agree: the doc counts all eight layer names as one kind (81 − 9 layers + 1 =
/// 73) and did not see the ninth layer — see
/// <see cref="MapNodeKinds.IsLayerName"/>.
///
/// The policy governs the <i>typed</i> layer only. Fidelity does not depend on
/// it: every node, in every class including <see cref="MapNodePolicy.Unknown"/>,
/// is held in the faithful layer and written back unchanged. A kind's policy says
/// what an editing surface may offer for it — nothing more.
/// </remarks>
public enum MapNodePolicy
{
    /// <summary>
    /// A first-class editor surface: the tool may create, change and delete these.
    /// </summary>
    Edit,

    /// <summary>
    /// Show it, allow it to be moved, do not author it. These are shapes whose
    /// meaning lives in the server or in code the editor cannot read, so
    /// inventing one is a guess.
    /// </summary>
    Display,

    /// <summary>
    /// Preserve verbatim. Mostly kinds present on a handful of maps, whose
    /// internal shape has never been measured against enough examples to author
    /// against.
    /// </summary>
    Preserve,

    /// <summary>
    /// Not in the v232 census. Treated exactly like <see cref="Preserve"/>, and
    /// reported, because a kind nobody has seen is the strongest possible reason
    /// not to let a schema-driven editor touch it.
    /// </summary>
    Unknown,
}

/// <summary>
/// The measured vocabulary of top-level map nodes, and the policy for each.
/// </summary>
public static class MapNodeKinds
{
    private static readonly Dictionary<string, MapNodePolicy> Policies = Build();

    /// <summary>
    /// The policy for a top-level node name. Names are matched ordinally,
    /// because <c>BuffZone</c> is capitalised and <c>buffZone</c> is not a kind
    /// this client ships.
    /// </summary>
    public static MapNodePolicy PolicyOf(string name)
    {
        if (name == null)
            return MapNodePolicy.Unknown;

        // A layer is any numerically named top-level node, not one of eight
        // fixed names. See IsLayerName.
        if (IsLayerName(name))
            return MapNodePolicy.Edit;

        return Policies.TryGetValue(name, out MapNodePolicy policy) ? policy : MapNodePolicy.Unknown;
    }

    /// <summary>
    /// Whether a top-level node name is a layer.
    /// </summary>
    /// <remarks>
    /// <b>The layer set is not closed at 0-7, and this was found by running the
    /// round-trip over the whole client rather than by reading anything.</b>
    /// <c>docs/map-data-model.md</c> §4 states that every geometry map has
    /// exactly layers 0-7, "never added, never removed". <c>749080500.img</c>
    /// has a ninth, named <c>8</c>, holding the ordinary <c>info</c> /
    /// <c>tile</c> / <c>obj</c> that a layer holds — one map out of 17,442,
    /// which is precisely the frequency at which a fixed <c>for i in 0..7</c>
    /// gets written and never questioned. Such a loop draws that map's ninth
    /// layer not at all, and a save that rebuilds a map from layers 0-7 deletes
    /// it outright.
    ///
    /// So layers are detected, not enumerated. Every digit-named top-level node
    /// in the client is a layer — the census over all 17,442 images found
    /// digit-named top-level kinds <c>0</c> through <c>8</c> and nothing else —
    /// and a tenth would be picked up without a code change.
    /// </remarks>
    public static bool IsLayerName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (char c in name)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }

    /// <summary>Every kind the v232 census found, with its policy.</summary>
    public static IReadOnlyDictionary<string, MapNodePolicy> All => Policies;

    /// <summary>
    /// The layer names present on every one of the 10,596 geometry maps: 0-7.
    /// <b>This is the usual set, not the whole set</b> — see
    /// <see cref="IsLayerName"/> for the one map that has a ninth. Use it to
    /// know what to expect, never to decide what to read.
    /// </summary>
    public static readonly string[] UsualLayerNames = ["0", "1", "2", "3", "4", "5", "6", "7"];

    /// <summary>
    /// The rectangle-shaped zone kinds: a container of numbered entries, each
    /// carrying <c>x1 y1 x2 y2</c>. Grouped because they are one editing
    /// surface, not eleven.
    /// </summary>
    public static readonly string[] RectKinds =
    [
        "rectInfo", "swimArea", "climbArea", "crawlArea", "rapidStream",
        "swimArea_Moment", "climbArea_Moment", "fishingZone", "checkPoint", "BuffZone",
    ];

    /// <summary>
    /// The containers measured to exist with zero children in the shipping
    /// client. The node is there and it is empty; deleting it is a diff against
    /// the client, so nothing prunes one.
    /// </summary>
    /// <remarks>
    /// The data model document names four — <c>reactor</c> on 9,560 maps,
    /// <c>ladderRope</c> on 5,756, <c>life</c> on 3,601, <c>seat</c> on one, all
    /// of which the corpus sweep confirmed exactly. It found <b>five more</b>:
    /// <c>respawn</c> on 303 maps, <c>property</c> on 16, <c>onlyUseSkill</c> on
    /// 3, <c>noSkill</c> on 2 and <c>ToolTip</c> on 1.
    ///
    /// This list is documentation, not a rule the loader needs — which is the
    /// point. The faithful layer keeps every empty container whether it is listed
    /// here or not, so finding five more was a note to write rather than a bug to
    /// fix. A loader with a hard-coded "these four may be empty" would have
    /// pruned the other five.
    /// </remarks>
    public static readonly string[] LegitimatelyEmptyContainers =
    [
        "reactor", "ladderRope", "life", "seat",
        "respawn", "property", "onlyUseSkill", "noSkill", "ToolTip",
    ];

    private static Dictionary<string, MapNodePolicy> Build()
    {
        Dictionary<string, MapNodePolicy> map = new(StringComparer.Ordinal);

        foreach (string name in new[]
        {
            "info", "portal", "life", "ladderRope", "foothold", "back",
            "0", "1", "2", "3", "4", "5", "6", "7",
            "reactor", "miniMap", "ToolTip", "seat", "area", "rectInfo",
            "swimArea", "climbArea", "crawlArea", "rapidStream", "swimArea_Moment",
            "climbArea_Moment", "fishingZone", "checkPoint", "night", "clock",
            "monsterCarnival", "BuffZone", "weather", "shipObj", "pulley", "healer",
        })
        {
            map[name] = MapNodePolicy.Edit;
        }

        foreach (string name in new[]
        {
            "particle", "noSkill", "respawn", "onlyUseSkill", "monsterDefense",
            "mobTeleport", "reactorRemove", "permittedSkill",
        })
        {
            map[name] = MapNodePolicy.Display;
        }

        foreach (string name in new[]
        {
            "user", "triggersTW", "remoteCharacterEffect", "directionInfo", "MirrorFieldData",
            "replaceUI", "nodeInfo", "footprintData", "areaCtrl", "skyWhale",
            "objectVisibleLevel", "property", "enterUI", "stigma", "bonusRewards",
            "WindArea", "illuminantCluster", "mobMassacre", "flyingAreaData", "incHealRate",
            "coconut", "battleField", "snowMan", "snowBall", "publicTaggedObjectVisible",
            "extinctMO", "unusableSkillArea", "randomMobGen", "ghostPark", "pocketdrop",
            "oxQuiz", "mobKillCountExp", "defenseMob", "courtshipDance", "CaptureTheFlag",

            // A data bug, and one of the ten anomalies: Map1/103000805.img carries
            // returnMap at the image root, where only info/returnMap belongs. It is
            // listed as a kind because it is one — the client ships it, so the model
            // has to write it back exactly where it found it.
            "returnMap",
        })
        {
            map[name] = MapNodePolicy.Preserve;
        }

        return map;
    }
}
