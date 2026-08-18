using System.Globalization;
using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>How an NPC field should be presented and validated.</summary>
public enum NpcFieldKind
{
    /// <summary>A number.</summary>
    Int,

    /// <summary>A 0/1 the client treats as a boolean — render a checkbox.</summary>
    Flag,

    /// <summary>Free text.</summary>
    Text,
}

/// <summary>One field of an NPC's <c>info</c> node, described well enough to render.</summary>
/// <param name="Key">The raw WZ property name. This is what gets written.</param>
/// <param name="Label">What a person calls it.</param>
/// <param name="Group">Section heading on the card.</param>
/// <param name="Unit">"px", "mesos" — appended after the input. Null for a bare number.</param>
/// <param name="Hint">One line of help, shown where there is room.</param>
public sealed record NpcFieldSpec(
    string Key,
    string Label,
    string Group,
    NpcFieldKind Kind = NpcFieldKind.Int,
    string? Unit = null,
    string? Hint = null);

/// <summary>
/// The names, groupings and labels for an NPC's <c>info</c> node.
///
/// Every entry here was read off a real client rather than assumed: the counts
/// in the comments are from a full sweep of all 10,742 images in a v232 Npc.wz,
/// which is also why the list is short. An NPC carries far less than a mob —
/// <c>hideName</c> (3,219 NPCs), <c>script</c> (3,057), <c>speak</c> (2,446) and
/// the four dialogue-box offsets account for most of what exists, and a
/// stat block like a mob's appears exactly once in the entire archive.
///
/// Like <see cref="MobFieldCatalog"/> this is descriptive, not prescriptive: an
/// NPC may carry fields that are not here (they surface under "Other") and may
/// be missing every one that is. Nothing is written unless the user edits it.
/// </summary>
public static class NpcFieldCatalog
{
    /// <summary>Section order on the card. Anything uncatalogued lands in "Other".</summary>
    public static readonly string[] GroupOrder =
    {
        "Identity", "Services", "Dialogue", "Behaviour", "Display", "Position", "Other",
    };

    private static readonly NpcFieldSpec[] All =
    {
        // --- Identity -------------------------------------------------------
        new("link",               "Links to",                "Identity", NpcFieldKind.Text,
            Hint: "This NPC's animations come from the linked image. Edits to art here will not take effect."),
        new("name",               "Name override",           "Identity", NpcFieldKind.Text,
            Hint: "Rare. The displayed name normally comes from String.wz, not from here."),
        new("quest",              "Quest script",            "Identity", NpcFieldKind.Text),
        new("imitate",            "Imitates NPC",            "Identity"),
        new("componentNPC",       "Component NPC",           "Identity", NpcFieldKind.Flag),

        // --- Services (what a player can actually do with this NPC) ---------
        new("shop",               "Opens a shop",            "Services", NpcFieldKind.Flag,
            Hint: "The stock itself lives in the server's shop table, not in Npc.wz."),
        new("scriptNpcShop",      "Shop opened by script",   "Services", NpcFieldKind.Flag),
        new("noNpcShop",          "Never opens a shop",      "Services", NpcFieldKind.Flag),
        new("noDiscountPrice",    "No discounts",            "Services", NpcFieldKind.Flag),
        new("trunkPut",           "Storage deposit fee",     "Services", NpcFieldKind.Int, "mesos"),
        new("trunkGet",           "Storage withdrawal fee",  "Services", NpcFieldKind.Int, "mesos"),
        new("storebank",          "Storage keeper",          "Services", NpcFieldKind.Flag),
        new("parcel",             "Parcel service",          "Services", NpcFieldKind.Flag,
            Hint: "Duey — sends items between characters."),
        new("cash",               "Cash shop",               "Services", NpcFieldKind.Flag),
        new("guildRank",          "Guild ranking",           "Services", NpcFieldKind.Flag),
        new("rpsGame",            "Rock-paper-scissors",     "Services", NpcFieldKind.Flag),

        // --- Dialogue -------------------------------------------------------
        new("script",             "Script bindings",         "Dialogue", NpcFieldKind.Text,
            Hint: "A group: script/0/script names the handler the server runs."),
        new("speak",              "Chat lines",              "Dialogue", NpcFieldKind.Text,
            Hint: "A group of keys ('n0', 'd1') into String.wz — the text itself is edited there."),
        new("scriptDelay",        "Script delay",            "Dialogue", NpcFieldKind.Int, "s"),
        new("talkMouseOnly",      "Click to talk only",      "Dialogue", NpcFieldKind.Flag),
        new("distanceForSelect",  "Click distance",          "Dialogue", NpcFieldKind.Int, "px"),
        new("sayFlip",            "Flip while speaking",     "Dialogue", NpcFieldKind.Flag),
        new("partnerSpeechQuest", "Partner speech quest",    "Dialogue", NpcFieldKind.Flag),

        // --- Behaviour ------------------------------------------------------
        new("forceMove",          "Wanders",                 "Behaviour", NpcFieldKind.Flag),
        new("speed",              "Move speed",              "Behaviour"),
        new("hide",               "Hidden",                  "Behaviour", NpcFieldKind.Flag,
            Hint: "The NPC is spawned but not drawn."),
        new("float",              "Floats",                  "Behaviour", NpcFieldKind.Flag),
        new("hideQuestBalloon",   "Hide quest balloon",      "Behaviour", NpcFieldKind.Flag),
        new("notRecommend",       "Not recommended",         "Behaviour", NpcFieldKind.Flag),

        // --- Display --------------------------------------------------------
        new("hideName",           "Hide name tag",           "Display", NpcFieldKind.Flag,
            Hint: "The most common field an NPC carries — 3,219 of 10,742 in a v232 client."),
        new("dcMark",             "Show marker",             "Display", NpcFieldKind.Flag),
        new("questIconPreStart",  "Quest icon (available)",  "Display"),
        new("questIconPerform",   "Quest icon (in progress)", "Display"),
        new("questIconPreComplete", "Quest icon (ready)",    "Display"),
        new("questIconComplete",  "Quest icon (done)",       "Display"),
        new("illustration",       "Illustration",            "Display", NpcFieldKind.Text),
        new("illustration2",      "Illustration (large)",    "Display", NpcFieldKind.Text),
        new("default",            "Default frame",           "Display", NpcFieldKind.Text),
        new("MapleTV",            "MapleTV screen",          "Display", NpcFieldKind.Flag),

        // --- Position -------------------------------------------------------
        new("dcLeft",             "Dialogue box left",       "Position", NpcFieldKind.Int, "px"),
        new("dcRight",            "Dialogue box right",      "Position", NpcFieldKind.Int, "px"),
        new("dcTop",              "Dialogue box top",        "Position", NpcFieldKind.Int, "px"),
        new("dcBottom",           "Dialogue box bottom",     "Position", NpcFieldKind.Int, "px"),
        new("forcedZPage",        "Forced Z page",           "Position"),
        new("forcedZMass",        "Forced Z mass",           "Position"),
        new("quarterViewYOffset", "Quarter-view Y offset",   "Position", NpcFieldKind.Int, "px"),
        new("MapleTVmsgX",        "TV message X",            "Position", NpcFieldKind.Int, "px"),
        new("MapleTVmsgY",        "TV message Y",            "Position", NpcFieldKind.Int, "px"),
        new("MapleTVadX",         "TV advert X",             "Position", NpcFieldKind.Int, "px"),
        new("MapleTVadY",         "TV advert Y",             "Position", NpcFieldKind.Int, "px"),
    };

    private static readonly Dictionary<string, NpcFieldSpec> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<NpcFieldSpec> Fields => All;

    public static NpcFieldSpec? Find(string key) =>
        ByKey.TryGetValue(key, out NpcFieldSpec? spec) ? spec : null;

    /// <summary>
    /// Where an uncatalogued key should appear. Everything unknown is still
    /// shown — this client has 90-odd distinct info keys, a dozen of which
    /// appear once, and those are exactly what someone editing an unusual
    /// client most needs to see.
    /// </summary>
    public static NpcFieldSpec Unknown(string key) =>
        new(key, key, "Other", NpcFieldKind.Text);

    /// <summary>The catalog's index for a group, for stable ordering.</summary>
    public static int GroupRank(string group)
    {
        int index = Array.IndexOf(GroupOrder, group);
        return index < 0 ? GroupOrder.Length : index;
    }
}

/// <summary>
/// Presents Npc.wz as NPCs rather than as a property tree.
///
/// Like <see cref="MobService"/>, this is a projection and not a second editor:
/// every write goes through <see cref="WzEditService"/>, so NPC edits share one
/// dirty state, one undo history and one save pipeline with everything else.
///
/// The thing that makes NPCs different from mobs is that half of what a person
/// means by "the NPC" is not in Npc.wz at all. The name, the job line under it
/// and every word it says live in String.wz/Npc.img — <c>info/speak</c> holds
/// only the *keys* ("n0", "d1"). So the card reads both archives and says which
/// one each value comes from, and the writes into String.wz go through
/// <see cref="StringEditService"/> rather than being open-coded here.
/// </summary>
public sealed class NpcService
{
    /// <summary>
    /// A browse list is for finding an NPC, not for reading a client end to
    /// end. A v232 Npc.wz holds 10,742 images; this is a backstop against an
    /// unusual one, and the UI is told when it bites.
    /// </summary>
    private const int MaxRows = 20000;

    /// <summary>
    /// The key includes a caller-supplied fileId, so without a bound a client
    /// could grow this without limit by asking for ids that do not exist. Small,
    /// because the realistic key count is one per open Npc archive plus the
    /// all-archives key.
    /// </summary>
    private const int MaxCachedLists = 16;

    /// <summary>
    /// NPC images parsed per gate hold while the list is built.
    ///
    /// An NPC image is far lighter than a mob's — measured at 0.3 ms across a
    /// v232 Npc.wz's 10,742 — so the chunk is bigger for the same ~75 ms of gate
    /// time per hold. See <see cref="WzSessionService.TryRunChunked"/>.
    /// </summary>
    private const int ChunkSize = 256;

    /// <summary>Restarts before the last pass takes the gate outright; see MobService.</summary>
    private const int MaxChunkedAttempts = 3;

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly StringEditService _stringEdit;
    private readonly UndoService _undo;

    /// <summary>
    /// The last browse list, and the tree generation it was taken from.
    ///
    /// Building it means parsing every NPC image: measured at 4.2-4.5s for a
    /// v232 Npc.wz (10,742 images, a 1.0 GB archive) cold, and 7-14ms warm.
    /// That happens under the session gate, so paying it on every
    /// keystroke-triggered refresh would make the mode unusable.
    /// <see cref="WzSessionService.Generation"/> already ticks on every
    /// structural change, which is exactly the staleness test this needs — and
    /// every write ticks it, because each one drops the resolution cache.
    /// </summary>
    private readonly Dictionary<string, (int Structure, int Value, int Names, NpcListDto List)> _listCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One list builder per cache key. The idle warm-up and a user opening NPCs
    /// can otherwise parse all 10,742 images at the same time, doubling both
    /// work and gate contention. Kept separate from the session gate because a
    /// build deliberately releases that gate between chunks.
    /// </summary>
    private readonly Dictionary<string, object> _buildLocks = new(StringComparer.Ordinal);

    public NpcService(
        WzSessionService session,
        WzEditService edit,
        StringPoolService strings,
        StringEditService stringEdit,
        UndoService undo)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _stringEdit = stringEdit;
        _undo = undo;
    }

    /// <summary>Whether any archive that could hold NPCs is open.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_session.Gate)
                return NpcArchives(null).Count > 0;
        }
    }

    #region Browse

    public NpcListDto List(
        string? fileId, bool resolveNames, CancellationToken cancel = default,
        bool allowExclusiveFallback = true)
    {
        string key = $"{fileId ?? "*"}|{resolveNames}";

        // Built before the gate is taken, because it takes the gate itself and
        // caches against the same generation. Reading it per row would be a
        // String.wz lookup 10,742 times over; this is one walk, already done for
        // the string browser.
        Dictionary<int, string> funcs = resolveNames && _stringEdit.IsAvailable
            ? _stringEdit.TextIndex("npc", "func")
            : new Dictionary<int, string>();
        if (resolveNames)
            _strings.Warm(cancel, allowExclusiveFallback);
        // Warm cache: no build lock needed.
        lock (_session.Gate)
        {
            if (TryServeCached(key, resolveNames, funcs, out NpcListDto? hit))
                return hit;
        }

        lock (BuildLockFor(key))
        {
            // A caller ahead of us most likely filled it while we waited.
            lock (_session.Gate)
            {
                if (TryServeCached(key, resolveNames, funcs, out NpcListDto? cached))
                    return cached;
            }

            for (int attempt = 0; ; attempt++)
            {
                NpcListDto? built = TryBuild(fileId, resolveNames, funcs,
                                             interleave: !allowExclusiveFallback || attempt < MaxChunkedAttempts,
                                             cancel, out int builtAtGeneration);
                if (built == null)
                    continue;   // the tree moved under the build; nothing partial is kept

                lock (_session.Gate)
                {
                    // Only stamped if nothing landed between the build finishing and
                    // this line; see MobService.List for the reasoning.
                    if (_session.Generation == builtAtGeneration)
                    {
                        if (_listCache.Count >= MaxCachedLists)
                            _listCache.Clear();
                        (int structure, int value, _) = _session.ValueChanges();
                        _listCache[key] = (structure, value, resolveNames ? _strings.Revision : 0, built);
                    }
                }
                return built;
            }
        }
    }

    private object BuildLockFor(string key)
    {
        lock (_buildLocks)
        {
            if (_buildLocks.TryGetValue(key, out object? gate))
                return gate;

            _buildLocks[key] = gate = new object();
            return gate;
        }
    }

    /// <summary>
    /// Serves the cached list when it is still true, re-reading the rows a value
    /// edit has changed. False means rebuild.
    ///
    /// The same shape as <c>MobService.TryServeCached</c>, and safe for the same
    /// reason: the list survives a value edit only because every image named by
    /// <see cref="WzSessionService.ValueChanges"/> is read again from the live
    /// tree before it goes out. Without that half the pair is a fast wrong
    /// answer. Caller must hold the gate.
    /// </summary>
    private bool TryServeCached(
        string key, bool resolveNames, Dictionary<int, string> funcs, out NpcListDto? list)
    {
        list = null;
        if (!_listCache.TryGetValue(key, out (int Structure, int Value, int Names, NpcListDto List) cached))
            return false;

        // Only for a list that carries names. A list rendered as bare ids does
        // not read the pool at all, and making it rebuild when the pool moves
        // would be a 2,742-image re-summarise for a column it does not have.
        int names = resolveNames ? _strings.Revision : 0;

        // A row's name comes out of String.wz, which is a different archive from
        // the rows, so no amount of re-reading the rows can refresh it: the
        // touched path is "f2/Mob.img/100100/name" and the rows are
        // "f1/0100100.img", so OwnerOf rightly matches neither, TryPatch skips
        // it and returns true, and the line below then stamps the pre-edit name
        // as current -- permanently, because a value generation is only consumed
        // once. Renaming a mob in the Strings section left the grid showing the
        // old name until something structural happened to it.
        //
        // Rebuilt rather than patched, because the pool cannot say which ids
        // moved. It costs a full rebuild per name edit, which is the right price
        // for the guarantee: a name edit is a deliberate, occasional act, and
        // 2,742 images re-summarised is the thing this cache makes cheap.
        if (cached.Names != names)
            return false;

        (int structure, int value, IReadOnlyCollection<string> touched) = _session.ValueChanges();
        if (cached.Structure != structure)
            return false;
        if (cached.Value != value && !TryPatch(cached.List, touched, resolveNames, funcs))
            return false;

        // Dirty flags move without the tree changing shape, so they are
        // the one thing refreshed on a cache hit.
        RefreshDirty(cached.List);
        _listCache[key] = (structure, value, names, cached.List);
        list = cached.List;
        return true;
    }

    /// <summary>
    /// Re-summarises the rows owning the touched paths. False if any of them
    /// cannot be re-read, which the caller turns into a full rebuild.
    /// </summary>
    private bool TryPatch(
        NpcListDto list, IReadOnlyCollection<string> touched, bool resolveNames, Dictionary<int, string> funcs)
    {
        if (touched.Count == 0)
            return true;

        Dictionary<string, int> index = new(list.Npcs.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Npcs.Count; i++)
            index[list.Npcs[i].Path] = i;

        bool any = false;
        foreach (string path in touched)
        {
            int row = WzSessionService.OwnerOf(index, path);
            if (row < 0)
                continue;   // an edit to something this list does not show

            string rowPath = list.Npcs[row].Path;
            if (_session.Resolve(rowPath) is not WzImage image)
                return false;
            if (!TryNpcId(image.Name, out int npcId))
                return false;

            try
            {
                WzSessionService.EnsureParsed(image);
                list.Npcs[row] = Summarise(image, rowPath, npcId, resolveNames, funcs);
                any = true;
            }
            catch
            {
                return false;
            }
        }

        if (any)
            list.Stats = Summarise(list.Npcs);
        return true;
    }

    /// <summary>
    /// One build pass, in gate-releasing chunks. Null when the tree changed
    /// while it ran — see <see cref="WzSessionService.TryRunChunked"/> for why
    /// a half-and-half list is worse than a slow one.
    /// </summary>
    private NpcListDto? TryBuild(
        string? fileId, bool resolveNames, Dictionary<int, string> funcs,
        bool interleave, CancellationToken cancel, out int generation)
    {
        NpcListDto result = new();
        List<NpcSummaryDto> npcs = result.Npcs;

        List<(WzImage Image, string Path)> work = new();
        lock (_session.Gate)
        {
            generation = _session.Generation;
            foreach (OpenFile file in NpcArchives(fileId))
            {
                if (file.LooseImage != null)
                {
                    work.Add((file.LooseImage, file.Id));
                    continue;
                }
                WzDirectory? root = _session.RoleRoot(file, "Npc");
                if (root == null)
                    continue;
                work.AddRange(EnumerateNpcImages(root, _session.RoleRootPath(file, "Npc")));
            }
        }

        bool complete = _session.TryRunChunked(generation, work, item =>
        {
            if (npcs.Count >= MaxRows)
            {
                result.Truncated = true;
                return;
            }
            WzImage image = _session.MaterializeImage(item.Image);
            if (!TryNpcId(image.Name, out int npcId))
                return;
            npcs.Add(Summarise(image, item.Path, npcId, resolveNames, funcs));
        }, ChunkSize, interleave, cancel);

        if (!complete)
            return null;

        result.NamesAvailable = _strings.IsAvailable;
        result.Stats = Summarise(npcs);
        return result;
    }

    /// <summary>
    /// One page of the list, so the first screen can paint without serialising
    /// all 10,742 rows (1.8 MB of JSON on a v232 client). The list itself is
    /// still built and cached whole; see <see cref="MobService.Page"/>.
    /// </summary>
    public (NpcListDto Page, int Total) Page(
        string? fileId, bool resolveNames, int offset, int limit, CancellationToken cancel = default)
    {
        NpcListDto all = List(fileId, resolveNames, cancel);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxRows);

        return (new NpcListDto
        {
            Npcs = all.Npcs.Skip(offset).Take(limit).ToList(),
            Stats = all.Stats,
            Truncated = all.Truncated,
            NamesAvailable = all.NamesAvailable,
        }, all.Npcs.Count);
    }

    /// <summary>
    /// Re-reads just the dirty flags of a cached list. Editing an NPC's
    /// <c>hideName</c> does not change the tree's shape, so the cached rows stay
    /// valid — but whether the image is now unsaved has changed, and that is
    /// what the browse grid marks.
    /// </summary>
    private void RefreshDirty(NpcListDto list)
    {
        foreach (NpcSummaryDto npc in list.Npcs)
        {
            try
            {
                if (_session.Resolve(npc.Path) is WzImage image)
                    npc.Dirty = image.Changed;
            }
            catch
            {
                // A row whose path no longer resolves. Close() takes the same
                // gate, so this is not a file closing under us -- it is a node a
                // structural edit removed, which also ticks Generation, so the
                // very next call rebuilds the list from scratch. Leaving the
                // stale flag for those few milliseconds is better than failing
                // the request.
            }
        }
    }

    private NpcSummaryDto Summarise(
        WzImage image, string path, int npcId, bool resolveNames, Dictionary<int, string> funcs)
    {
        NpcSummaryDto dto = new()
        {
            Path = path,
            NpcId = npcId,
            Dirty = image.Changed,
            Name = resolveNames ? _strings.GetNpcName(npcId) : null,
            Func = resolveNames && funcs.TryGetValue(npcId, out string? func) ? func : null,
        };

        // Parsed, deliberately, even though it is the expensive part: measured
        // at 4.2s for a v232 Npc.wz's 10,742 images. Listing them unparsed was
        // worse than slow -- every row read "not a shop, no script, no name",
        // which is indistinguishable from a client where that is true. The cost
        // is paid once per generation and cached; see _listCache.
        WzSessionService.EnsureParsed(image);

        WzImageProperty? info = image.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
        if (info == null)
            return dto;

        dto.LinkTarget = ReadText(info, "link");
        dto.HideName = ReadFlag(info, "hideName") == true;
        dto.IsShop = ReadFlag(info, "shop") == true || ReadFlag(info, "scriptNpcShop") == true;
        dto.IsTrunk = Child(info, "trunkPut") != null || Child(info, "trunkGet") != null;
        dto.IsStorage = ReadFlag(info, "storebank") == true;

        WzImageProperty? speak = Child(info, "speak");
        // Null, not 0: an NPC with no speak node does not have zero chat lines,
        // it has none defined, and a column of zeros reads as data that failed
        // to load.
        dto.SpeakLines = speak?.WzProperties?.Count;

        dto.Script = FirstScript(info);
        dto.HasScript = dto.Script != null || Child(info, "script") != null;
        return dto;
    }

    private static NpcStatsDto Summarise(List<NpcSummaryDto> npcs)
    {
        NpcStatsDto stats = new() { Total = npcs.Count };

        foreach (NpcSummaryDto npc in npcs)
        {
            if (!string.IsNullOrEmpty(npc.Name)) stats.Named++;
            if (npc.HasScript) stats.Scripted++;
            if (npc.IsShop) stats.Shops++;
            if (npc.IsTrunk || npc.IsStorage) stats.Storages++;
            if (npc.LinkTarget != null) stats.Linked++;
            if (npc.HideName) stats.Hidden++;
        }
        return stats;
    }

    #endregion

    #region Detail

    public NpcDetailDto Detail(string path)
    {
        NpcDetailDto dto;
        int npcId;

        lock (_session.Gate)
        {
            WzImage image = ResolveNpcImage(path);
            WzSessionService.EnsureParsed(image);

            TryNpcId(image.Name, out npcId);
            dto = new NpcDetailDto
            {
                Path = path,
                NpcId = npcId,
                Name = _strings.GetNpcName(npcId),
                Dirty = image.Changed,
            };

            WzImageProperty? info = image.WzProperties.FirstOrDefault(
                p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
            string infoPath = WzPath.Child(path, "info");
            dto.LinkTarget = info == null ? null : ReadText(info, "link");

            // Present fields first, keyed by what the NPC actually carries, then
            // the catalog entries it does not — the UI hides those behind a
            // toggle. An uncatalogued key is still shown: a field we have never
            // heard of is exactly what someone editing an unusual client needs.
            Dictionary<string, WzImageProperty> present = new(StringComparer.OrdinalIgnoreCase);
            if (info?.WzProperties != null)
            {
                foreach (WzImageProperty property in info.WzProperties)
                    present.TryAdd(property.Name ?? "", property);
            }

            Dictionary<string, NpcFieldGroupDto> groups = new(StringComparer.Ordinal);

            foreach (NpcFieldSpec spec in NpcFieldCatalog.Fields)
            {
                present.TryGetValue(spec.Key, out WzImageProperty? property);
                Add(groups, spec, property, infoPath);
            }

            foreach ((string key, WzImageProperty property) in present)
            {
                if (NpcFieldCatalog.Find(key) != null)
                    continue;
                Add(groups, NpcFieldCatalog.Unknown(key), property, infoPath);
            }

            dto.Groups = groups.Values
                .OrderBy(g => NpcFieldCatalog.GroupRank(g.Group))
                .ToList();

            if (info != null)
            {
                dto.Scripts = ReadScripts(info, infoPath);
                dto.Speak = ReadSpeak(info, infoPath, npcId);
            }

            if (dto.LinkTarget != null)
            {
                dto.Warnings.Add(
                    $"This NPC links to {dto.LinkTarget}. Its animations come from that image, " +
                    "so replacing art here does nothing — open the linked NPC instead.");
            }
            if (info == null)
            {
                dto.Warnings.Add(
                    "This image has no 'info' node. Editing a field will create one.");
            }
        }

        // Outside the gate: StringEditService takes it itself, and these are the
        // values that come from a different archive.
        StringEntryDto entry = _stringEdit.Entry("npc", npcId);
        dto.StringPath = entry.Present ? entry.Path : null;
        dto.Name ??= entry.Fields.TryGetValue("name", out string? name) ? name : null;
        dto.Func = entry.Fields.TryGetValue("func", out string? func) ? func : null;

        if (!_stringEdit.IsAvailable)
        {
            dto.Warnings.Add(
                "String.wz is not open, so this NPC's name and chat text cannot be shown or edited.");
        }
        else if (string.IsNullOrEmpty(dto.Name))
        {
            // Keyed on the *name*, not on whether the entry exists. A v232
            // client has 296 NPC images with no String.wz entry at all and
            // another handful whose entry is there but carries only dialogue
            // keys — both show up blank in game, and testing for the entry
            // alone silently passed the second group.
            dto.Warnings.Add(entry.Present
                ? $"String.wz has an entry for NPC {npcId} but no name in it, so it appears blank in game."
                : $"Nothing in String.wz names NPC {npcId}, so it appears blank in game.");
        }
        return dto;
    }

    private static void Add(
        Dictionary<string, NpcFieldGroupDto> groups,
        NpcFieldSpec spec,
        WzImageProperty? property,
        string infoPath)
    {
        if (!groups.TryGetValue(spec.Group, out NpcFieldGroupDto? group))
        {
            group = new NpcFieldGroupDto { Group = spec.Group };
            groups[spec.Group] = group;
        }

        // A container -- info/script, info/speak, info/illustration2 and
        // info/default are all sub-properties on a v232 NPC, and between them
        // they are on 7,000-odd of the 10,742 images -- has no scalar value.
        // Rendered as an ordinary Text field it draws an empty box that looks
        // editable, and typing into it reaches WzNodeFactory's `default:` throw.
        // Reported as a container instead: the card links to it rather than
        // pretending to edit it.
        bool isContainer = property is not null && property.WzValue is null && property.WzProperties?.Count > 0;

        group.Fields.Add(new NpcFieldDto
        {
            Key = spec.Key,
            Label = spec.Label,
            Kind = isContainer ? "Container" : spec.Kind.ToString(),
            Unit = spec.Unit,
            Hint = spec.Hint,
            Path = WzPath.Child(infoPath, spec.Key),
            WzType = property?.PropertyType.ToString(),
            Value = isContainer
                ? $"{property!.WzProperties!.Count} entries"
                : property?.WzValue?.ToString(),
            Present = property != null,
            Editable = !isContainer,
        });
    }

    /// <summary>
    /// The script bindings: <c>info/script/&lt;n&gt;/script</c> names the handler
    /// the server runs when the NPC is clicked.
    ///
    /// Read from the archive rather than assumed to be a single string: 3,044 of
    /// the 3,057 NPCs that have a <c>script</c> node hold it as a group of
    /// indexed entries, and the remaining handful hold a bare string.
    /// </summary>
    private static List<NpcScriptDto> ReadScripts(WzImageProperty info, string infoPath)
    {
        List<NpcScriptDto> scripts = new();
        WzImageProperty? node = Child(info, "script");
        if (node == null)
            return scripts;

        string scriptPath = WzPath.Child(infoPath, node.Name ?? "script");

        if (node.WzProperties == null || node.WzProperties.Count == 0)
        {
            scripts.Add(new NpcScriptDto
            {
                Index = "",
                Path = scriptPath,
                Script = node.WzValue?.ToString(),
            });
            return scripts;
        }

        foreach (WzImageProperty entry in node.WzProperties)
        {
            string entryPath = WzPath.Child(scriptPath, entry.Name ?? "");
            NpcScriptDto dto = new() { Index = entry.Name ?? "", Path = entryPath };

            if (entry.WzProperties == null || entry.WzProperties.Count == 0)
            {
                dto.Script = entry.WzValue?.ToString();
                scripts.Add(dto);
                continue;
            }

            foreach (WzImageProperty child in entry.WzProperties)
            {
                string name = child.Name ?? "";
                if (string.Equals(name, "script", StringComparison.OrdinalIgnoreCase))
                {
                    dto.Script = child.WzValue?.ToString();
                    dto.Path = WzPath.Child(entryPath, name);
                }
                else
                {
                    // questStart / questEnd and friends, kept because they are
                    // what decides whether the script fires at all.
                    dto.Extra[name] = child.WzValue?.ToString();
                }
            }
            scripts.Add(dto);
        }
        return scripts;
    }

    /// <summary>
    /// The idle chat lines, joined to their text.
    ///
    /// <c>info/speak/&lt;n&gt;</c> is a key, not a sentence: it holds "n0", and
    /// the words are at String.wz/Npc.img/&lt;id&gt;/n0. Showing the key alone is
    /// the thing that makes people conclude the chat text is not editable.
    /// </summary>
    private List<NpcSpeakLineDto> ReadSpeak(WzImageProperty info, string infoPath, int npcId)
    {
        List<NpcSpeakLineDto> lines = new();
        WzImageProperty? node = Child(info, "speak");
        if (node?.WzProperties == null)
            return lines;

        string speakPath = WzPath.Child(infoPath, node.Name ?? "speak");

        foreach (WzImageProperty entry in node.WzProperties)
        {
            string? key = entry.WzValue?.ToString();
            NpcSpeakLineDto line = new()
            {
                Index = entry.Name ?? "",
                Path = WzPath.Child(speakPath, entry.Name ?? ""),
                Key = key,
            };

            if (!string.IsNullOrEmpty(key))
            {
                (string? path, string? text) = _stringEdit.Field("npc", npcId, key);
                line.StringPath = path;
                line.Text = text;
            }
            lines.Add(line);
        }
        return lines;
    }

    #endregion

    #region Write

    public NpcDetailDto WriteFields(NpcWriteRequest request)
    {
        // A POST body that omitted "fields" arrives here with a null list, and
        // dereferencing it would 500 on what is really a no-op.
        if (request.Fields is null || request.Fields.Count == 0)
            return Detail(request.Path);

        lock (_session.Gate)
        {
            WzImage image = ResolveNpcImage(request.Path);
            WzSessionService.EnsureParsed(image);

            string infoPath = WzPath.Child(request.Path, "info");
            EnsureInfo(image, request.Path);

            // One batch, so a card's worth of edits is one Ctrl+Z rather than
            // fifteen. Same reason the cash shop batches a bulk add.
            using IDisposable batch = _undo.Batch(
                request.Fields.Count == 1
                    ? $"Edit {request.Fields[0].Key}"
                    : $"Edit {request.Fields.Count} NPC fields");

            foreach (NpcFieldWrite field in request.Fields)
            {
                string fieldPath = WzPath.Child(infoPath, field.Key);
                if (Exists(image, field.Key))
                {
                    if (IsContainer(image, field.Key))
                    {
                        throw new InvalidOperationException(
                            $"'{field.Key}' holds a group of values, not one value. " +
                            "Open it in the Explorer to edit what is inside it.");
                    }
                    _edit.SetValue(fieldPath, field.Value);
                }
                else
                {
                    // The catalog decides the type for a key it knows. For one it
                    // does not, the *value* decides -- Unknown() reports Text, so
                    // trusting the catalog would create a WzStringProperty holding
                    // "1" where every other NPC has a WzIntProperty, and the client
                    // calls GetInt on it. MobService.WriteFields and
                    // CashShopService.WriteExtraField infer the same way.
                    //
                    // This client makes the point twice over: hideName, forceMove
                    // and the four dc* offsets each appear as BOTH Int and String
                    // across the archive, so there is no single right answer from
                    // the key alone.
                    NpcFieldSpec? spec = NpcFieldCatalog.Find(field.Key);
                    bool numeric = long.TryParse(
                        field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                    string type = spec is null
                        ? (numeric ? "Int" : "String")
                        : spec.Kind == NpcFieldKind.Text ? "String" : "Int";

                    _edit.Add(new AddNodeRequest
                    {
                        Path = infoPath,
                        Name = field.Key,
                        Type = type,
                        Value = field.Value,
                    });
                }
            }
        }

        return Detail(request.Path);
    }

    public NpcBulkResultDto Bulk(NpcBulkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Field))
            throw new InvalidOperationException("No field was chosen.");

        string op = string.IsNullOrWhiteSpace(request.Op) ? "set" : request.Op.Trim();
        if (op is not ("set" or "add" or "multiply" or "percent"))
            throw new InvalidOperationException($"Unknown operation '{request.Op}'.");

        // Parsed once, invariant, before anything is touched: an arithmetic run
        // whose operand is not a number is a bad request, not 500 bad rows.
        // InvariantCulture on purpose -- WzNodeFactory parses with it too, so a
        // comma-decimal desktop must not be allowed to disagree with it here.
        double operand = 0;
        if (op != "set" && !double.TryParse(
                request.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out operand))
        {
            throw new InvalidOperationException(
                $"'{request.Value}' is not a number, so it cannot be used with '{op}'.");
        }

        NpcBulkResultDto result = new() { DryRun = request.DryRun };

        lock (_session.Gate)
        {
            // Everything is computed first, whether or not it will be written, so
            // a dry run and a real run cannot disagree about what happens.
            List<(string FieldPath, string After, NpcBulkChangeDto Row)> writes = new();

            foreach (string path in request.Paths ?? new List<string>())
            {
                NpcBulkChangeDto change = new() { Path = path };
                result.Changes.Add(change);

                WzImage image;
                try
                {
                    image = ResolveNpcImage(path);
                    WzSessionService.EnsureParsed(image);
                }
                catch (Exception ex)
                {
                    change.Skipped = true;
                    change.Reason = ex.Message;
                    continue;
                }

                TryNpcId(image.Name, out int npcId);
                change.NpcId = npcId;
                change.Name = _strings.GetNpcName(npcId);

                WzImageProperty? info = image.WzProperties.FirstOrDefault(
                    p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
                WzImageProperty? property = info?.WzProperties?.FirstOrDefault(
                    p => string.Equals(p.Name, request.Field, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    // Deliberately not created here. Bulk edit is for changing
                    // what exists; silently adding a field to a thousand NPCs
                    // because a dropdown offered it is not a thing to do by
                    // accident. The NPC card creates it, one NPC at a time.
                    change.Skipped = true;
                    change.Reason = "This NPC has no such field. Add it from the NPC card first.";
                    continue;
                }

                if (property.WzValue == null && property.WzProperties?.Count > 0)
                {
                    change.Skipped = true;
                    change.Reason = $"'{request.Field}' holds a group of values here, not one value.";
                    continue;
                }

                string? before = property.WzValue?.ToString();
                change.Before = before;

                if (op == "set")
                {
                    change.After = request.Value;
                }
                else
                {
                    if (!double.TryParse(before, NumberStyles.Any, CultureInfo.InvariantCulture, out double current))
                    {
                        change.Skipped = true;
                        change.Reason = "The current value is not a number.";
                        continue;
                    }

                    double after = op switch
                    {
                        "add" => current + operand,
                        "multiply" => current * operand,
                        _ => current * (1 + operand / 100),
                    };

                    // Rounding is a choice, not a default. Formatting is
                    // invariant for the same reason the parse above is: the
                    // desktop's culture produced "83,333333" on a comma-decimal
                    // machine, which WzNodeFactory then rejected, turning a bulk
                    // edit into a mid-batch throw on every de-DE install. "F0"
                    // rather than "R" because "R" emits scientific notation past
                    // ~1e15 and the integer parse rejects that too.
                    change.After = request.Round switch
                    {
                        "none" => after.ToString("0.##########", CultureInfo.InvariantCulture),
                        "floor" => Math.Floor(after).ToString("F0", CultureInfo.InvariantCulture),
                        _ => Math.Round(after, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture),
                    };
                }

                if (string.Equals(change.Before, change.After, StringComparison.Ordinal))
                {
                    change.Skipped = true;
                    change.Reason = "Already set to this.";
                    continue;
                }

                writes.Add((WzPath.Child(WzPath.Child(path, "info"), property.Name ?? request.Field),
                            change.After!, change));
            }

            if (!request.DryRun && writes.Count > 0)
            {
                using IDisposable batch = _undo.Batch(
                    $"{op} {request.Field} on {writes.Count} NPCs");

                // Per row, because a throw here used to abandon the loop with the
                // earlier rows already written and Applied never assigned -- the
                // request 500'd, the user concluded nothing had happened, and the
                // archive held a partial edit. WzEditService.SetValueMany sets the
                // house pattern: never let one bad row decide the batch's fate,
                // and report what actually landed.
                // See MobService.Bulk: the row rides along in the tuple instead
                // of being re-found by a linear scan per write.
                foreach ((string fieldPath, string after, NpcBulkChangeDto row) in writes)
                {
                    try
                    {
                        _edit.SetValue(fieldPath, after);
                        result.Applied++;
                    }
                    catch (Exception ex)
                    {
                        row.Skipped = true;
                        row.After = null;
                        row.Reason = ex.Message;
                    }
                }
            }
        }

        return result;
    }

    #endregion

    #region Plumbing

    /// <summary>Open archives that could hold NPCs: Npc.wz, Npc001.wz, Npc2.wz...</summary>
    private List<OpenFile> NpcArchives(string? fileId)
        => _session.SelectRoleSources("Npc", fileId);

    /// <summary>
    /// NPC images, at the archive root and one level down. Clients differ about
    /// whether they group them, and both shapes turn up in the wild — a v232
    /// Npc.wz keeps all 10,742 at the root.
    /// </summary>
    private static IEnumerable<(WzImage Image, string Path)> EnumerateNpcImages(WzDirectory root, string fileId)
    {
        foreach (WzImage image in root.WzImages)
            yield return (image, WzPath.Child(fileId, image.Name));

        foreach (WzDirectory sub in root.WzDirectories)
        {
            string subPath = WzPath.Child(fileId, sub.Name);
            foreach (WzImage image in sub.WzImages)
                yield return (image, WzPath.Child(subPath, image.Name));
        }
    }

    private WzImage ResolveNpcImage(string path)
    {
        object node = _session.Resolve(path);
        return node as WzImage
            ?? throw new InvalidOperationException($"'{path}' is not an NPC image.");
    }

    private void EnsureInfo(WzImage image, string imagePath)
    {
        if (image.WzProperties.Any(p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase)))
            return;

        _edit.Add(new AddNodeRequest { Path = imagePath, Name = "info", Type = "SubProperty" });
    }

    /// <summary>Whether an NPC's info child is a group rather than a single value.</summary>
    private static bool IsContainer(WzImage image, string key)
    {
        WzImageProperty? info = image.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
        WzImageProperty? property = info?.WzProperties?.FirstOrDefault(
            p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        return property is not null && property.WzValue is null && property.WzProperties?.Count > 0;
    }

    private static bool Exists(WzImage image, string key)
    {
        WzImageProperty? info = image.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
        return info?.WzProperties?.Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>"9000096.img" -> 9000096. Leading zeros are the norm.</summary>
    private static bool TryNpcId(string? name, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name))
            return false;

        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static WzImageProperty? Child(WzImageProperty parent, string key) =>
        parent.WzProperties?.FirstOrDefault(
            p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A 0/1 field's state, or null when the NPC does not carry it at all.
    /// Absent and zero are different things and the UI shows them differently.
    /// </summary>
    private static bool? ReadFlag(WzImageProperty info, string key)
    {
        WzImageProperty? property = Child(info, key);
        if (property == null)
            return null;
        string? text = property.WzValue?.ToString();
        // Int and String forms both occur for these keys in one client, so parse
        // the text rather than trusting the property type.
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value != 0
            : !string.IsNullOrEmpty(text);
    }

    private static string? ReadText(WzImageProperty info, string key)
    {
        string? text = Child(info, key)?.WzValue?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>The first handler name under <c>info/script</c>, or null.</summary>
    private static string? FirstScript(WzImageProperty info)
    {
        WzImageProperty? node = Child(info, "script");
        if (node == null)
            return null;

        string? direct = node.WzValue?.ToString();
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (node.WzProperties == null)
            return null;

        foreach (WzImageProperty entry in node.WzProperties)
        {
            string? text = entry.WzValue?.ToString()
                ?? entry.WzProperties?.FindByName("script")?.WzValue?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    #endregion
}
