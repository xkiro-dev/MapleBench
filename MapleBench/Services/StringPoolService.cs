using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// Resolves item, skill, mob and NPC display names from String.wz.
///
/// The whole point is that "1002000" means nothing while "Wisdom Aquire Hat"
/// means everything, so this is what makes search-by-name and readable item
/// cards possible.  It degrades silently: if String.wz isn't open, callers get
/// null and the UI falls back to IDs.
/// </summary>
public sealed class StringPoolService
{
    private readonly WzSessionService _session;
    private readonly ILogger<StringPoolService> _log;

    private readonly object _cacheGate = new();

    /// <summary>Serialises the build itself; see <see cref="EnsureBuilt"/>.</summary>
    private readonly object _buildGate = new();
    private Dictionary<int, string>? _itemNames;
    private Dictionary<int, string>? _skillNames;
    private Dictionary<int, string>? _mobNames;
    private Dictionary<int, string>? _npcNames;
    private Dictionary<int, string>? _mapNames;

    /// <summary>Bumped whenever files are opened or closed so caches rebuild.</summary>
    private int _builtForFileCount = -1;

    /// <summary>
    /// Ticks every time a different set of names is published, and every time the
    /// pool is invalidated.
    ///
    /// Exists because a browse list is not made only of the rows it re-reads. A
    /// mob row carries a name out of String.wz — <c>MobService.Summarise</c> asks
    /// <see cref="GetMobName"/> for it — so the generation split's rule ("a cached
    /// list survives a value edit only because it re-reads the rows the touched
    /// set names") cannot cover it: the touched path is
    /// "f2/Mob.img/100100/name", the rows are "f1/0100100.img", and
    /// <c>WzSessionService.OwnerOf</c> rightly matches neither. <c>TryPatch</c>
    /// then skipped it, returned true, and <c>TryServeCached</c> re-stamped the
    /// cache as current — so renaming a mob in the Strings section left the grid
    /// showing the old name, permanently, until something structural happened.
    ///
    /// A counter rather than a callback because the lists are already built
    /// around comparing counters, and because a callback would have to run under
    /// somebody else's lock.
    /// </summary>
    private int _revision;

    /// <summary>
    /// Which set of names is published right now. A cache holding text this pool
    /// produced must rebuild when this moves; see <see cref="_revision"/>.
    /// </summary>
    public int Revision
    {
        get { lock (_cacheGate) return _revision; }
    }

    /// <summary>
    /// The client folder the published pool describes, or null when nothing is
    /// built. Published under <see cref="_cacheGate"/> with the dictionaries, so
    /// a reader never sees one client's folder against another's names.
    ///
    /// This is what makes <see cref="NameFor"/> answer for one client instead of
    /// for all of them; see the guard there for what it prevents. The identity is
    /// the containing folder compared case-insensitively, which is the same
    /// identity <c>PortService.Groups</c> uses to decide what a "client" is —
    /// deliberately, so the tree and the Port screen cannot disagree about which
    /// client an archive belongs to.
    /// </summary>
    private string? _builtForFolder;

    public StringPoolService(WzSessionService session, ILogger<StringPoolService> log)
    {
        _session = session;
        _log = log;
    }

    public string? GetItemName(int id)
    {
        EnsureBuilt();
        lock (_cacheGate)
            return _itemNames != null && _itemNames.TryGetValue(id, out string? name) ? name : null;
    }

    public string? GetSkillName(int id)
    {
        EnsureBuilt();
        lock (_cacheGate)
            return _skillNames != null && _skillNames.TryGetValue(id, out string? name) ? name : null;
    }

    public string? GetMobName(int id)
    {
        EnsureBuilt();
        lock (_cacheGate)
            return _mobNames != null && _mobNames.TryGetValue(id, out string? name) ? name : null;
    }

    public string? GetNpcName(int id)
    {
        EnsureBuilt();
        lock (_cacheGate)
            return _npcNames != null && _npcNames.TryGetValue(id, out string? name) ? name : null;
    }

    /// <summary>
    /// "Henesys : Henesys" for map 100000000.
    ///
    /// Map.img is not keyed consistently: most regions list the full map id, but
    /// some list it divided by 10000 (verified against a v232 client, which holds
    /// both 100000000 and 10000 as keys). So the id is tried as-is first and the
    /// divided form only as a fallback — the other order would let a short key
    /// shadow a real map whose id happens to be 10000× larger.
    /// </summary>
    public string? GetMapName(int mapId)
    {
        EnsureBuilt();
        lock (_cacheGate)
        {
            if (_mapNames == null)
                return null;
            if (_mapNames.TryGetValue(mapId, out string? direct))
                return direct;
            // The divided form is a fallback, not a guess to spread around: without
            // the exact-multiple test, every id from 100000001 to 100009999 would
            // answer with map 10000's name. A map id that is not a whole multiple
            // has no short-key reading, so it resolves to nothing rather than to
            // something plausible and wrong.
            return mapId >= 10000 && mapId % 10000 == 0
                   && _mapNames.TryGetValue(mapId / 10000, out string? divided)
                ? divided
                : null;
        }
    }

    /// <summary>
    /// Whether the built pool actually contains names. This is the blocking
    /// answer for code that is about to consume those dictionaries; lightweight
    /// capability probes use <see cref="HasSource"/> instead.
    ///
    /// This one waits for the build, where <see cref="GetMobName"/> and friends
    /// do not. The difference is which answer a wrong one produces: a name that
    /// has not loaded yet renders as its id for a moment and then corrects
    /// itself, but "names are not available" makes the UI tell the user to open
    /// a String.wz they already have open, which is a lie with an action
    /// attached. Safe to block because no caller holds the session gate over it
    /// — the accessors that do are the non-blocking ones.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            Warm();
            lock (_cacheGate)
                return _itemNames is { Count: > 0 };
        }
    }

    /// <summary>
    /// Whether the session contains a usable String archive, without parsing a
    /// single image. Capability endpoints ask this presence question so drawing
    /// the Editors menu cannot trigger the multi-second name-pool build.
    ///
    /// <see cref="IsAvailable"/> remains the stronger, blocking answer for code
    /// that is about to consume the published dictionaries.
    /// </summary>
    public bool HasSource
    {
        get
        {
            lock (_session.Gate)
            {
                return StringArchives().Count > 0;
            }
        }
    }

    /// <summary>
    /// Everything whose id or name matches <paramref name="query"/>.
    ///
    /// This is the whole substance of Database mode: the pools are already built
    /// and already keyed by id, so a search is a scan over at most a few hundred
    /// thousand short strings — fast enough to run per keystroke without an index,
    /// and an index would only have to be invalidated on every archive open.
    ///
    /// A purely numeric query matches ids by prefix as well as exactly, because
    /// "1302" is how someone looks for the sword family.
    /// </summary>
    public List<(string Kind, int Id, string Name)> Search(string query, string? kind, int limit)
    {
        // Waits, like IsAvailable and for the same reason: Database mode is
        // nothing but this pool, and "no results" for a query that has results
        // is a worse answer than a slow one. Reached from an endpoint, which
        // holds no session gate.
        Warm();

        List<(string, int, string)> hits = new();
        if (string.IsNullOrWhiteSpace(query))
            return hits;

        query = query.Trim();
        bool numeric = query.All(char.IsAsciiDigit);

        // Scored, then cut — never cut while scanning. Taking the first N matches
        // in pool order meant "Snail" returned six items and never the Snail mob,
        // because items are scanned first and there are far more of them.
        List<(string Kind, int Id, string Name, int Score)> scored = new();

        lock (_cacheGate)
        {
            Scan("item", _itemNames);
            Scan("mob", _mobNames);
            Scan("skill", _skillNames);
            Scan("npc", _npcNames);
            Scan("map", _mapNames);
        }

        // Interleave by kind so every pool that matched is represented in the
        // first screenful, best-scoring first within each.
        List<(string Kind, int Id, string Name, int Score)>[] byKind = scored
            .GroupBy(h => h.Kind)
            .Select(g => g.OrderByDescending(h => h.Score).ThenBy(h => h.Id).ToList())
            .ToArray();

        for (int round = 0; hits.Count < limit; round++)
        {
            bool anyLeft = false;
            foreach (List<(string Kind, int Id, string Name, int Score)> pool in byKind)
            {
                if (round >= pool.Count)
                    continue;
                anyLeft = true;
                if (hits.Count >= limit)
                    break;
                hits.Add((pool[round].Kind, pool[round].Id, pool[round].Name));
            }
            if (!anyLeft)
                break;
        }
        return hits;

        void Scan(string poolKind, Dictionary<int, string>? pool)
        {
            if (pool == null)
                return;
            if (!string.IsNullOrEmpty(kind) && !string.Equals(kind, poolKind, StringComparison.OrdinalIgnoreCase))
                return;

            // One buffer for the whole scan, reused per entry. Formatting into a
            // stack buffer rather than allocating matters because this runs once per
            // pool entry per keystroke over a few hundred thousand entries, and two
            // ToString() calls each made the search allocate more than it compared.
            // Hoisted out of the loop deliberately: a stackalloc inside it is not
            // freed until the method returns, so at this entry count it is a real
            // stack overflow, not a style preference (CA2014).
            Span<char> plain = stackalloc char[12];

            foreach ((int id, string name) in pool)
            {
                int score;
                if (numeric)
                {
                    if (!id.TryFormat(plain, out int written))
                        continue;
                    ReadOnlySpan<char> digits = plain[..written];

                    if (digits.SequenceEqual(query.AsSpan()))
                        score = 100;
                    else if (digits.StartsWith(query.AsSpan(), StringComparison.Ordinal)
                             || StartsWithPadded(digits, query.AsSpan()))
                        score = 60;
                    else
                        continue;
                }
                else
                {
                    if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
                        score = 100;
                    else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                        score = 70;
                    else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        score = 40;
                    else
                        continue;
                }
                scored.Add((poolKind, id, name, score));
            }
        }
    }

    /// <summary>
    /// Whether a query matches the id's zero-padded 8-digit form, without building
    /// that form. WZ ids are written with leading zeros everywhere in the client,
    /// so "01302" is how people type them.
    /// </summary>
    private static bool StartsWithPadded(ReadOnlySpan<char> digits, ReadOnlySpan<char> query)
    {
        int pad = 8 - digits.Length;
        if (pad <= 0 || query.Length > 8)
            return false;

        for (int i = 0; i < query.Length; i++)
        {
            char expected = i < pad ? '0' : digits[i - pad];
            if (query[i] != expected)
                return false;
        }
        return true;
    }

    /// <summary>
    /// What a node's name means, when it is an id. "01302000" under Character.wz
    /// resolves to "Blue Sword"; "0100100.img" under Mob.wz to "Snail".
    ///
    /// Which dictionary to ask is decided by the archive the path starts in,
    /// because the id spaces overlap — 1000 is a valid mob, item and skill id, and
    /// answering from the wrong pool is worse than answering nothing. Anything
    /// that is not a bare number, with or without a .img suffix, resolves to null.
    /// </summary>
    /// <param name="path">Session path, e.g. "f1/Mob.wz/0100100.img".</param>
    /// <param name="name">The node's own name.</param>
    public string? NameFor(string path, string? name)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            return null;

        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];

        // Leading zeros are the norm in WZ ids, so parse rather than pattern-match.
        if (stem.IsEmpty || stem.Length > 10 || !int.TryParse(stem, out int id))
            return null;

        // Names come from one client, so they are only shown on that client.
        //
        // This is the check a sceptical user runs first, and it used to fail.
        // The pool is one process-wide set of dictionaries built from a single
        // winning String archive (see StringArchives), but nothing on the read
        // side asked which client a node belonged to — so with client A's
        // String.wz open and client B's not open at all, expanding B's Mob.wz
        // showed A's names. Worse for the case this app exists for: port a mob
        // into B, and the tree would show it named, in a client that has no name
        // for it, which is exactly the evidence someone uses to believe the port
        // worked. Worse again when B *does* have a String.wz open — it is dropped
        // with only a log line, and B's nodes are then labelled with A's names,
        // which for a shared id can be a different name for the same number.
        //
        // Falling back to the id is the honest answer: the UI already renders a
        // null DisplayName as the bare node name, so an unnamed client looks like
        // one rather than like somebody else's.
        //
        // Only NameFor is scoped here, not GetMobName and friends. Those serve
        // the domain grids, which are a separate and much larger piece of work —
        // see the report. This is the tree and inspector path, which is where the
        // wrong name is actually read.
        if (!BelongsToPoolClient(path))
            return null;

        return ArchiveOf(path) switch
        {
            "mob"    => GetMobName(id),
            "npc"    => GetNpcName(id),
            "skill"  => GetSkillName(id),
            "map"    => GetMapName(id),
            // Equips live in Character.wz and everything else in Item.wz, but both
            // are named out of the same item pool.
            "item" or "character" => GetItemName(id),
            _ => null,
        };
    }

    /// <summary>
    /// The archive family a session path sits in, lowercased and stripped of any
    /// numbered-sibling suffix: "Mob001.wz" reports "mob".
    ///
    /// Taken from the open file, NOT by reading a path segment. A session path
    /// does not contain the archive's filename — <c>WzSessionService.Resolve</c>
    /// begins its walk at the archive's root *directory*, so "f1/0100100.img" has
    /// the file id and then a node already inside the archive. Parsing segment 1
    /// yielded "0100100.img" for Mob.wz and "weapon" for Character.wz, so every
    /// lookup missed; it appeared to work only for Map.wz, whose root directory
    /// happens to be called "Map".
    /// </summary>
    private string? ArchiveOf(string path)
    {
        string name;
        try { name = _session.GetFileForPath(path).Name; }
        catch (KeyNotFoundException) { return null; }

        ReadOnlySpan<char> archive = name.AsSpan();
        if (archive.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)
            || archive.EndsWith(".ms", StringComparison.OrdinalIgnoreCase))
            archive = archive[..^3];
        if (archive.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            archive = archive[..^4];

        // Trim a numbered sibling: Mob001 -> Mob, Map2 -> Map.
        int trim = archive.Length;
        while (trim > 0 && char.IsAsciiDigit(archive[trim - 1]))
            trim--;

        return trim == 0 ? null : archive[..trim].ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Whether the node at <paramref name="path"/> lives in the client the pool
    /// was built from.
    ///
    /// Fails closed on every uncertainty — an unknown file, a file with no
    /// directory part, a pool that has not published a folder yet — because the
    /// failure this exists to prevent is showing a plausible name that is not
    /// this client's, and a missing name is visibly a missing name.
    ///
    /// The comparison is a plain folder match, with no "but there is only one
    /// client open so it must be fine" allowance. A first version had one and a
    /// test caught what it meant: a Mob.wz in one folder and a String.wz in
    /// another *are* two clients by this app's own definition (the folder is
    /// exactly what <c>PortService.Groups</c> keys on), so the allowance was
    /// asking this method to overrule that definition — which is the same
    /// guess, made once instead of twice. A split client is unaffected: its
    /// Data\Mob and Data\String are two archive directories under one Data\
    /// folder, and it is the parent that is compared, so they match.
    /// </summary>
    private bool BelongsToPoolClient(string path)
    {
        string? built;
        lock (_cacheGate)
            built = _builtForFolder;
        if (built == null)
            return false;

        // "Not open" is an ordinary answer here, not an error: this runs once
        // per row of every listing, and a row below an archive that has just been
        // closed is simply a row with no name. GetFileForPath, which this
        // replaces, throws for one — an exception per row, on the hot path, to
        // say something unremarkable.
        OpenFile? file = _session.PeekFileForPath(path);
        if (file == null)
            return false;

        string? folder = Path.GetDirectoryName(file.FilePath);

        return !string.IsNullOrEmpty(folder)
               && string.Equals(folder, built, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Forces a rebuild, e.g. after String.wz is opened mid-session.</summary>
    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _builtForFileCount = -1;

            // Ticked here and not only on publish, because the caller has just
            // said the names it can see are wrong. A list that keeps serving them
            // until the rebuild finishes is serving the thing the caller
            // invalidated — and the rebuild is lazy, so "until" can be a long
            // time.
            _revision++;
        }
    }

    /// <summary>
    /// Builds the pool now, blocking until it is there.
    ///
    /// Called by the domain list builders *before* they take the session gate,
    /// for two reasons. It removes 2,742 nested gate acquisitions from a mob
    /// list build (every <see cref="GetMobName"/> asked the session for its file
    /// count), and it keeps the pool's own build off the inside of somebody
    /// else's gate hold, which is the arrangement <see cref="EnsureBuilt"/>'s
    /// non-blocking wait depends on.
    ///
    /// Must not be called while holding <see cref="WzSessionService.Gate"/>.
    /// </summary>
    public void Warm(
        CancellationToken cancel = default, bool allowExclusiveFallback = true) =>
        EnsureBuilt(wait: true, cancel, allowExclusiveFallback);

    private void EnsureBuilt() =>
        EnsureBuilt(wait: false, CancellationToken.None, allowExclusiveFallback: true);

    private void EnsureBuilt(bool wait, CancellationToken cancel, bool allowExclusiveFallback)
    {
        cancel.ThrowIfCancellationRequested();
        int fileCount = _session.FileCount;
        lock (_cacheGate)
        {
            if (_builtForFileCount == fileCount)
                return;
        }

        // One builder at a time, and the "built" flag is published only once the
        // dictionaries are assigned.
        //
        // The flag used to be set *before* the multi-second build, so every other
        // caller in that window saw "built" and read the previous client's pool --
        // or null. That was survivable while only the cash-shop screen used this;
        // NameFor put it on /api/children and /api/inspect, i.e. once per node on
        // every tree click, so the number of threads inside the window went from
        // about one to however many rows are on screen.
        //
        // An incidental caller (wait: false) no longer queues behind the build.
        // It cannot: the build releases and re-takes the session gate between
        // chunks so that browsing stays responsive while it runs, and a caller
        // that already holds that gate -- /api/children resolving a name does --
        // would then be waiting for a builder that is waiting for it. It reads
        // the published pool instead, which is the previous *complete* one or
        // none at all, never a half-built one, so the worst case is a name
        // rendering as its id for a second. Callers that genuinely need the pool
        // ask for it through Warm() from outside the gate.
        if (!Monitor.TryEnter(_buildGate, wait ? Timeout.Infinite : 0))
            return;
        try
        {
            cancel.ThrowIfCancellationRequested();
            fileCount = _session.FileCount;
            lock (_cacheGate)
            {
                if (_builtForFileCount == fileCount)
                    return;
            }
            Build(fileCount, cancel, allowExclusiveFallback);
        }
        finally
        {
            Monitor.Exit(_buildGate);
        }
    }

    /// <summary>
    /// String.wz images collected per gate hold.
    ///
    /// A v232 String.wz holds ~42 root images and the pool took 2.6s to build
    /// with the gate held throughout, which every other request waited for. The
    /// images are wildly uneven — Eqp.img and Map.img are most of it — so this
    /// does not make the holds equal, only bounded by the largest single image
    /// instead of by the whole archive.
    /// </summary>
    private const int BuildChunkSize = 2;

    /// <summary>Restarts before the last pass takes the gate outright; see MobService.</summary>
    private const int MaxChunkedAttempts = 3;

    /// <summary>
    /// Whether a String.wz root image carries names this pool publishes.
    ///
    /// Must agree with the dispatch in <see cref="TryBuild"/>: this decides what
    /// is parsed, that decides what is read out of it.
    /// </summary>
    private static readonly HashSet<string> NamedPools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skill.img", "Mob.img", "Npc.img", "Eqp.img", "Etc.img",
        "Consume.img", "Ins.img", "Cash.img", "Pet.img", "Map.img",
    };

    // Case-insensitively, and deliberately more generous than the dispatch:
    // three of those branches compare with OrdinalIgnoreCase and four with a
    // pattern, so a filter that matched exactly could exclude an image the
    // switch would have collected from. Letting one extra image through costs a
    // parse; keeping one out costs every name in that category.
    private static bool IsNamedPool(string? name) => name != null && NamedPools.Contains(name);

    private void Build(int fileCount, CancellationToken cancel, bool allowExclusiveFallback)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (TryBuild(
                fileCount,
                interleave: !allowExclusiveFallback || attempt < MaxChunkedAttempts,
                cancel))
                return;
        }
    }

    /// <summary>
    /// One build pass. False when the tree moved while it ran, in which case
    /// nothing is published: half of one client's names merged with half of the
    /// same client a moment later is not a pool anyone can reason about.
    /// </summary>
    private bool TryBuild(int fileCount, bool interleave, CancellationToken cancel)
    {
        Dictionary<int, string> items = new();
        Dictionary<int, string> skills = new();
        Dictionary<int, string> mobs = new();
        Dictionary<int, string> npcs = new();
        Dictionary<int, string> maps = new();

        List<WzImage> work = new();
        int generation;
        string? folder = null;
        lock (_session.Gate)
        {
            generation = _session.Generation;
            foreach (OpenFile file in StringArchives())
            {
                if (file.LooseImage != null)
                {
                    folder ??= Path.GetDirectoryName(file.FilePath);
                    if (IsNamedPool(file.LooseImage.Name))
                        work.Add(file.LooseImage);
                    continue;
                }
                WzDirectory? root = _session.RoleRoot(file, "String");
                if (root == null)
                    continue;
                // Every archive StringArchives returns is in one folder by
                // construction, so the first one names the client this pool
                // describes. Recorded here rather than re-derived at read time:
                // the archive can be closed while the pool it produced is still
                // published, and the pool has to keep saying whose it is.
                folder ??= Path.GetDirectoryName(file.FilePath);
                // Only the images the pass below actually reads.
                //
                // It used to take every root image and EnsureParsed each one,
                // then look at its name and ignore most of them. A v232
                // String.wz has ~42 root images and this builder collects from
                // ten; the other thirty-odd -- ToolTip.img, MonsterBook.img,
                // Familiar.img and the rest -- were parsed in full, held in
                // memory, and dropped. The filter is the same set of names the
                // switch below dispatches on, which is why it lives next to it:
                // adding a category means adding it in both places or the names
                // silently stop resolving.
                foreach (WzImage image in root.WzImages)
                {
                    if (IsNamedPool(image.Name))
                        work.Add(image);
                }
            }
        }

        bool complete = _session.TryRunChunked(generation, work, entry =>
        {
            try
            {
                WzImage image = _session.MaterializeImage(entry);
                string name = image.Name;

                if (name.Equals("Skill.img", StringComparison.OrdinalIgnoreCase))
                    CollectFlat(image, skills);
                else if (name.Equals("Mob.img", StringComparison.OrdinalIgnoreCase))
                    CollectFlat(image, mobs);
                else if (name.Equals("Npc.img", StringComparison.OrdinalIgnoreCase))
                    CollectFlat(image, npcs);
                // Eqp.img and Etc.img both wrap their entries in one
                // extra level ("Eqp/<category>/<id>", "Etc/Etc/<id>");
                // the rest list ids directly. Treating Etc.img as flat
                // meant no 4xxxxxx item ever resolved a name.
                else if (name is "Eqp.img" or "Etc.img")
                    CollectNested(image, items);
                else if (name is "Consume.img" or "Ins.img" or "Cash.img" or "Pet.img")
                    CollectFlat(image, items);
                else if (name.Equals("Map.img", StringComparison.OrdinalIgnoreCase))
                    CollectMaps(image, maps);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping {Image} while building the string pool", entry.Name);
            }
        }, BuildChunkSize, interleave, cancel);

        if (!complete)
            return false;

        lock (_cacheGate)
        {
            _itemNames = items;
            _skillNames = skills;
            _mobNames = mobs;
            _npcNames = npcs;
            _mapNames = maps;
            _builtForFolder = folder;
            // Published together: a reader holding _cacheGate either sees the
            // previous complete pool or this one, never a mix -- and never one
            // client's names against another client's folder.
            _builtForFileCount = fileCount;
            _revision++;
        }
        _log.LogInformation("String pool: {Items} items, {Skills} skills, {Mobs} mobs, {Npcs} NPCs",
            items.Count, skills.Count, mobs.Count, npcs.Count);
        return true;
    }

    /// <summary>
    /// The String archives the pool is built from, in build order.
    ///
    /// The pool is one flat dictionary per kind, so every archive that matches
    /// contributes and the last writer for an id wins.  Two clients' String.wz
    /// open at once — which the session permits, since it dedupes by full path
    /// rather than by name — therefore used to blend two clients' names with no
    /// sign of it having happened, decided by dictionary order.
    ///
    /// The rule now: the exact stem ("String.wz") outranks a numbered sibling,
    /// the earliest-opened outranks a later one, and that archive's folder is
    /// the client the pool describes.  Same-folder siblings still merge, which
    /// is what a split String001.wz needs; anything else is dropped and named,
    /// with both paths, so a wrong name has an explanation.
    /// </summary>
    /// <remarks>
    /// Internal, not private: <see cref="StringEditService"/> must resolve the
    /// same archives in the same order, or a name would be written to one and
    /// read back from another — which looks exactly like a write that silently
    /// did nothing. One implementation is the only way to keep that true.
    /// </remarks>
    internal List<OpenFile> StringArchives()
    {
        List<OpenFile> matches = _session.SelectRoleSources("String");

        if (matches.Count <= 1)
            return matches;

        string? folder = Path.GetDirectoryName(matches[0].FilePath);
        List<OpenFile> chosen = new();

        foreach (OpenFile file in matches)
        {
            bool sameFolder = string.Equals(
                Path.GetDirectoryName(file.FilePath), folder, StringComparison.OrdinalIgnoreCase);
            bool duplicateName = chosen.Any(
                c => string.Equals(c.Name, file.Name, StringComparison.OrdinalIgnoreCase));

            if (sameFolder && !duplicateName)
            {
                chosen.Add(file);
                continue;
            }

            _log.LogWarning(
                "Two open archives could both supply names: reading from '{Used}' and ignoring '{Ignored}'. " +
                "Close one of them if an item name looks like it came from the wrong client.",
                matches[0].FilePath, file.FilePath);
        }
        return chosen;
    }

    /// <summary>
    /// Map names, which sit two levels down: <c>Map.img/&lt;region&gt;/&lt;key&gt;/{mapName,streetName}</c>.
    ///
    /// The key is stored exactly as the archive writes it, which is NOT uniform —
    /// a v232 client holds both full map ids (100000000) and short ones (10000).
    /// Normalising here would collide the two, so <see cref="GetMapName"/> owns the
    /// lookup order instead.
    /// The region ("maple", "victoria", "ossyria"...) is presentation only, so it
    /// is flattened away — a map id identifies a map on its own.
    ///
    /// Both parts are kept, joined, because "Henesys" alone is ambiguous across
    /// a client with several hundred town maps and the street is what
    /// disambiguates them.
    /// </summary>
    private static void CollectMaps(WzImage image, Dictionary<int, string> into)
    {
        foreach (WzImageProperty region in image.WzProperties)
        {
            if (region.WzProperties == null)
                continue;

            foreach (WzImageProperty entry in region.WzProperties)
            {
                if (!int.TryParse(entry.Name, out int shortId))
                    continue;

                string? mapName = entry.WzProperties?
                    .FirstOrDefault(p => string.Equals(p.Name, "mapName", StringComparison.OrdinalIgnoreCase))
                    ?.WzValue?.ToString();
                string? street = entry.WzProperties?
                    .FirstOrDefault(p => string.Equals(p.Name, "streetName", StringComparison.OrdinalIgnoreCase))
                    ?.WzValue?.ToString();

                if (string.IsNullOrWhiteSpace(mapName))
                    continue;

                into[shortId] = string.IsNullOrWhiteSpace(street) ? mapName : $"{street} : {mapName}";
            }
        }
    }

    /// <summary>Flat layout: &lt;id&gt;/name — Consume.img, Etc.img, Skill.img, ...</summary>
    private static void CollectFlat(WzImage image, Dictionary<int, string> into)
    {
        foreach (WzImageProperty entry in image.WzProperties)
            TryAdd(entry, into);
    }

    /// <summary>
    /// Reads an image that wraps its entries in a category level:
    /// Eqp.img is Eqp/&lt;category&gt;/&lt;id&gt;/name, Etc.img is Etc/Etc/&lt;id&gt;/name.
    ///
    /// The wrapper is named after the image, so it is looked up by the image's
    /// own stem rather than hard-coded. Entries are also collected at both
    /// levels: some client versions place ids directly under the wrapper.
    /// </summary>
    private static void CollectNested(WzImage image, Dictionary<int, string> into)
    {
        string stem = image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
            ? image.Name[..^4]
            : image.Name;

        WzImageProperty? wrapper = image.WzProperties.FindByName(stem);
        WzPropertyCollection? categories = wrapper?.WzProperties ?? image.WzProperties;
        if (categories == null)
            return;

        foreach (WzImageProperty category in categories)
        {
            // An id sitting straight under the wrapper, rather than in a group.
            TryAdd(category, into);

            if (category.WzProperties == null)
                continue;
            foreach (WzImageProperty entry in category.WzProperties)
                TryAdd(entry, into);
        }
    }

    private static void TryAdd(WzImageProperty entry, Dictionary<int, string> into)
    {
        if (!int.TryParse(entry.Name, out int id))
            return;
        if (entry.WzProperties?.FindByName("name") is not WzStringProperty name)
            return;
        if (!string.IsNullOrEmpty(name.Value))
            into[id] = name.Value;
    }
}
