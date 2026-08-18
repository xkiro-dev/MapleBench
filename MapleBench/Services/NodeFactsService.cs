using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>What one child of a browsed folder turned out to be.</summary>
public sealed class NodeFactDto
{
    /// <summary>The child's <c>info/cash</c> is present and non-zero.</summary>
    public bool Cash { get; set; }
}

/// <summary>
/// The facts for one folder's children, keyed by full child path.
///
/// Only children that have at least one fact appear in <see cref="Facts"/>. The
/// client needs the difference between "not cash" and "not looked at yet", and
/// this response covers the whole folder in one go, so absence from the map
/// means "looked at, nothing to say" the moment the response lands.
/// </summary>
public sealed class NodeFactsDto
{
    public string Path { get; set; } = "";

    /// <summary>
    /// False when the sample said this folder does not carry the flags at all —
    /// see <see cref="NodeFactsService.ProbeSize"/>. The client leaves its
    /// markers and its cash filter off rather than showing an empty one.
    /// </summary>
    public bool Applicable { get; set; }

    /// <summary>Children actually read, sample included.</summary>
    public int Scanned { get; set; }

    /// <summary>Children the folder has, whether or not they were read.</summary>
    public int Total { get; set; }

    /// <summary>The folder is larger than <see cref="NodeFactsService.MaxChildren"/>.</summary>
    public bool Truncated { get; set; }

    public Dictionary<string, NodeFactDto> Facts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Answers "which of these children are cash items" for a folder the child table
/// is already showing.
///
/// This exists because the answer cannot be had cheaply. An equip's cash flag
/// lives at <c>info/cash</c> inside the image — <c>Character.wz/Cap/01002316.img/info/cash</c>
/// — and reading it means parsing the image, exactly as <see cref="MobService"/>
/// has to. Character.wz/Cap has 3,331 of them, so this can never be folded into
/// <c>/api/inspect</c>: the child table has to paint immediately and let the
/// markers arrive afterwards, the same deal thumbnails already have.
///
/// What was verified before any of this was written, against a real v232 client,
/// sampling images at random from each folder:
///
///   Cap        29/40 cash   Coat      30/40   Longcoat 29/40 (1 with no flag)
///   Weapon      6/40        Accessory  8/40   Shoes    27/40 (1 with no flag)
///   Face       40/40        Hair      58/60   PetEquip 60/60
///
/// So the flag discriminates, and it is not a folder-level constant. The full
/// scans this service now runs confirm the samples across whole folders:
/// Cap 2,346 cash of 3,331, Weapon 1,048 of 6,441, Accessory 499 of 3,213,
/// Face 7,996 of 8,067, Hair 11,729 of 12,134. The folders that come back
/// nearly all-cash are the purely cosmetic slots, which is the expected answer
/// rather than a broken one.
///
/// Two shapes were found and both are handled. <c>cash</c> is normally an Int,
/// but one of the 60 sampled Caps stores it as the String "1". And in Item.wz
/// the items are sub-properties of a container image (<c>Item.wz/Cash/0510.img/05100000/info/cash</c>),
/// so a "child" here is any child object, not only a WzImage.
///
/// What a scan costs, measured cold on the same client (a second call is served
/// from the cache in 4-27 ms):
///
///   f1/Cap        3,331 children   5.7 s
///   f1/Face       8,067 children   8.6 s
///   f1/Weapon     6,441 children  18.0 s
///   f1/Hair      12,134 children  30.7 s
///   f2 (Mob.wz)   2,606 children   0.04 s   -- rejected by the sample
///
/// Two consequences worth knowing. The pass runs in gate-releasing chunks, so
/// it slows other requests rather than blocking them: a concurrent /api/files
/// probe during the Cap scan measured p50 161 ms and a maximum of 199 ms
/// against 16 ms idle, where an unchunked pass would have made it wait the
/// whole 5.7 s. And parsing is what costs the memory — the Cap scan took the
/// process from 107 MB to 575 MB of working set for its 3,331 parsed images.
/// That is the same bill <see cref="MobService"/> and <see cref="NpcService"/>
/// run up for their browse lists, and it is given back by the same thing:
/// <c>ImageMemoryService.SweepIfHeavy</c> on the next warm-up, or the sweep
/// button. Nothing is swept from here, because a sweep costs a compacting GC
/// and this response must not wait for one.
///
/// Nothing here writes. Every value read is read from the live tree under the
/// session gate.
/// </summary>
public sealed class NodeFactsService
{
    /// <summary>
    /// Children read before deciding whether the folder carries these flags at
    /// all, spread evenly across the folder rather than taken from the front.
    ///
    /// Without it, opening Mob.wz would parse all 2,606 of its images to
    /// discover that no mob has ever had a cash flag. Measured on a v232
    /// client: the whole request for Mob.wz's root costs 43 ms with the sample,
    /// against the 9.8 s MobService measures for parsing that same archive.
    ///
    /// The sample is spread because WZ order is arbitrary and the front of a
    /// folder is not a fair draw. 32 is chosen against the measured density:
    /// every folder that has the flag at all has it on 82-100% of its children,
    /// so the chance of 32 consecutive misses in such a folder is not a real
    /// number. The failure this CAN have is a folder where cash is genuinely
    /// rare — a handful in a thousand — which reads as "not applicable" and
    /// shows no markers. That is the pre-existing behaviour, i.e. failing
    /// towards showing nothing rather than towards showing something wrong.
    /// </summary>
    public const int ProbeSize = 32;

    /// <summary>
    /// Children read in the full pass. Character.wz/Hair has 12,134 and
    /// Weapon 6,441, so this is a backstop against something unusual rather
    /// than a limit real browsing meets; the UI is told when it bites.
    /// </summary>
    public const int MaxChildren = 20000;

    /// <summary>
    /// Children read per gate hold. Measured at 1.7 ms an image for a v232
    /// Character.wz Cap and 2.8 ms for a Weapon, so 64 is 110-180 ms of gate
    /// time per chunk — the same trade <c>MobService.ChunkSize</c> makes at the
    /// same size, and short enough that a thumbnail or a tree expansion queued
    /// behind this is served in under 200 ms rather than after the whole pass.
    /// </summary>
    private const int ChunkSize = 64;

    /// <summary>
    /// How many times a pass may be restarted by an edit landing mid-flight
    /// before it gives up on interleaving and takes the gate for the whole
    /// pass. Without the ceiling a continuously-edited session could restart
    /// for ever. Same value and same reason as <see cref="MobService"/>.
    /// </summary>
    private const int MaxChunkedAttempts = 3;

    /// <summary>
    /// Cached answers, and the generations they were true at. The key is a
    /// caller-supplied path, so this is bounded for the same reason
    /// <c>MobService._listCache</c> is: a client asking for folders that do not
    /// exist must not be able to grow it without limit.
    /// </summary>
    private const int MaxCachedFolders = 24;

    private readonly WzSessionService _session;

    /// <summary>
    /// One entry per folder: the two generations it was built at, the answer,
    /// and the ordered child paths it was built from.
    ///
    /// The scanned list is kept because a value edit is patched rather than
    /// rebuilt, and the patch has to map an edited property path back to the
    /// child that owns it — which needs every child's path, not only the ones
    /// that turned out to be cash. It is safe to keep across a value edit for
    /// the same reason the rest of the entry is: a value edit does not change
    /// which node a path names.
    /// </summary>
    private readonly Dictionary<string, (int Structure, int Value, NodeFactsDto Dto, string[] Scanned)> _cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One lock object per folder, so two callers wanting the same folder build
    /// it once instead of racing. Never held while taking the session gate in
    /// the other order; see <see cref="MobService"/>.
    /// </summary>
    private readonly Dictionary<string, object> _buildLocks = new(StringComparer.Ordinal);

    public NodeFactsService(WzSessionService session)
    {
        _session = session;
    }

    public NodeFactsDto For(string path, CancellationToken cancel = default)
    {
        // Cheap path first: a warm cache needs neither the build lock nor a build.
        lock (_session.Gate)
        {
            if (TryServeCached(path, out NodeFactsDto? hit))
                return hit!;
        }

        lock (BuildLockFor(path))
        {
            // Re-checked: whoever held the lock has very likely just filled it.
            lock (_session.Gate)
            {
                if (TryServeCached(path, out NodeFactsDto? cached))
                    return cached!;
            }

            // A chunked pass can be restarted by an edit landing mid-build.
            // After a few tries the last one takes the gate once and finishes,
            // so a session being edited continuously still gets an answer
            // rather than spinning.
            for (int attempt = 0; ; attempt++)
            {
                NodeFactsDto? built = TryBuild(path, interleave: attempt < MaxChunkedAttempts,
                                               cancel, out int generation, out string[] scanned);
                if (built == null)
                    continue;   // the tree moved under the pass; nothing partial is kept

                lock (_session.Gate)
                {
                    // Only stamped if nothing landed between the pass finishing
                    // and this line. Stamping the current numbers onto an answer
                    // built before an edit would bless pre-edit data as current,
                    // for ever; see MobService.List for the full reasoning.
                    if (_session.Generation == generation)
                    {
                        if (_cache.Count >= MaxCachedFolders)
                            _cache.Clear();
                        (int structure, int value, _) = _session.ValueChanges();
                        _cache[path] = (structure, value, built, scanned);
                    }
                }
                return built;
            }
        }
    }

    /// <summary>
    /// The build lock for one folder, created on first use. Its own tiny lock
    /// rather than the session gate, because a build takes and releases the gate
    /// dozens of times while holding this.
    /// </summary>
    private object BuildLockFor(string key)
    {
        lock (_buildLocks)
        {
            if (_buildLocks.TryGetValue(key, out object? gate))
                return gate;

            // Clearing is safe even mid-build: a lock object still held by a
            // builder stays alive in that builder's frame, and the worst a fresh
            // object costs is one duplicated build.
            if (_buildLocks.Count >= MaxCachedFolders)
                _buildLocks.Clear();

            _buildLocks[key] = gate = new object();
            return gate;
        }
    }

    /// <summary>
    /// Serves the cached answer when it is still true, re-reading the children a
    /// value edit has touched. False means it has to be built again.
    ///
    /// This is the half of the generation split that makes the split honest, and
    /// it is copied from <c>MobService.TryServeCached</c> deliberately. The
    /// answer survives a value edit — which is the point, since typing into any
    /// field in the app would otherwise throw away a three-second folder scan —
    /// but only because every path named by
    /// <see cref="WzSessionService.ValueChanges"/> is read again from the live
    /// tree first. Setting <c>info/cash</c> to 0 has to remove the marker before
    /// the next response goes out, not on the next reopen.
    ///
    /// Caller must hold the gate.
    /// </summary>
    private bool TryServeCached(string path, out NodeFactsDto? dto)
    {
        dto = null;
        if (!_cache.TryGetValue(path, out (int Structure, int Value, NodeFactsDto Dto, string[] Scanned) cached))
            return false;

        (int structure, int value, IReadOnlyCollection<string> touched) = _session.ValueChanges();
        if (cached.Structure != structure)
            return false;   // the tree moved; nothing here can be patched into truth

        if (cached.Value != value && !TryPatch(cached.Dto, cached.Scanned, touched))
            return false;   // a touched child could not be re-read; rebuild rather than guess

        _cache[path] = (structure, value, cached.Dto, cached.Scanned);
        dto = cached.Dto;
        return true;
    }

    /// <summary>
    /// Re-reads the children owning the touched paths. False when any of them
    /// cannot be re-read, which the caller turns into a full rebuild.
    ///
    /// The touched set names the edited property — <c>f1/Cap/01002316.img/info/cash</c>
    /// — so the child it belongs to is the deepest ancestor this answer was built
    /// from. Walking the ancestors beats guessing which segment is the image: a
    /// WZ directory may itself be called something.img, and guessing wrong means
    /// serving a marker the user has just turned off.
    /// </summary>
    private bool TryPatch(NodeFactsDto dto, string[] scanned, IReadOnlyCollection<string> touched)
    {
        if (touched.Count == 0)
            return true;

        Dictionary<string, int> index = new(scanned.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scanned.Length; i++)
            index[scanned[i]] = i;

        foreach (string path in touched)
        {
            int row = WzSessionService.OwnerOf(index, path);

            // An edit to something this folder does not cover is not a problem,
            // it is simply not ours.
            if (row < 0)
                continue;

            string childPath = scanned[row];
            try
            {
                bool? cash = ReadCash(_session.Resolve(childPath));
                if (cash == true)
                    dto.Facts[childPath] = new NodeFactDto { Cash = true };
                else
                    dto.Facts.Remove(childPath);
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// One pass. Returns null when the tree changed while it ran, in which case
    /// everything it built is thrown away — an answer assembled from two
    /// different states would be wrong in a way nobody could see.
    /// </summary>
    private NodeFactsDto? TryBuild(
        string path, bool interleave, CancellationToken cancel, out int generation, out string[] scanned)
    {
        scanned = Array.Empty<string>();

        List<(WzObject Child, string Path)> work = new();
        NodeFactsDto result = new() { Path = path };

        lock (_session.Gate)
        {
            generation = _session.Generation;

            // Enumerating a directory does not parse anything; enumerating an
            // image parses that one image, which is the container case
            // (Item.wz/Cash/0510.img) and is the cost of one image, not of a
            // folder.
            WzObject node = _session.Resolve(path);
            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (WzObject child in _session.EnumerateChildren(node))
            {
                string name = child.Name ?? "";
                seen.TryGetValue(name, out int occurrence);
                seen[name] = occurrence + 1;

                // The same path /api/inspect hands the client for this child, so
                // the client can look the facts up by the key it already holds.
                if (work.Count < MaxChildren)
                    work.Add((child, WzPath.Child(path, name, occurrence)));
                else
                    result.Truncated = true;
            }
            result.Total = seen.Values.Sum();
        }

        // --- the sample ---------------------------------------------------
        // Read first and on its own, so a folder that has never carried these
        // flags costs 32 images instead of all of them.
        List<(WzObject Child, string Path)> probe = Sample(work, ProbeSize);
        bool carriesCash = false;
        int probeScanned = 0;

        bool probed = _session.TryRunChunked(generation, probe, item =>
        {
            probeScanned++;
            if (ReadCash(item.Child) != null)
                carriesCash = true;
        }, ChunkSize, interleave, cancel);

        if (!probed)
            return null;

        if (!carriesCash)
        {
            result.Applicable = false;
            result.Scanned = probeScanned;
            scanned = Array.Empty<string>();
            return result;
        }

        // --- the full pass ------------------------------------------------
        // The sampled children are read a second time here rather than being
        // carried over. They are parsed now, so the second read is a dictionary
        // lookup, and the alternative is bookkeeping that could get the merge
        // wrong — which means a marker that is missing for no visible reason.
        int count = 0;
        bool complete = _session.TryRunChunked(generation, work, item =>
        {
            count++;
            if (ReadCash(item.Child) == true)
                result.Facts[item.Path] = new NodeFactDto { Cash = true };
        }, ChunkSize, interleave, cancel);

        if (!complete)
            return null;

        result.Applicable = true;
        result.Scanned = count;
        scanned = work.Select(item => item.Path).ToArray();
        return result;
    }

    /// <summary>
    /// Up to <paramref name="wanted"/> items spread evenly across the list.
    ///
    /// Evenly rather than from the front because WZ stores children in whatever
    /// order the archive was built in, and the front of Character.wz/Weapon is
    /// six hundred one-handed swords — a run that is not representative of the
    /// folder it starts.
    /// </summary>
    private static List<(WzObject Child, string Path)> Sample(
        IReadOnlyList<(WzObject Child, string Path)> work, int wanted)
    {
        if (work.Count <= wanted)
            return work.ToList();

        List<(WzObject Child, string Path)> picked = new(wanted);
        for (int i = 0; i < wanted; i++)
            picked.Add(work[(int)((long)i * work.Count / wanted)]);
        return picked;
    }

    /// <summary>
    /// The child's <c>info/cash</c>: null when it has none, otherwise whether it
    /// is set.
    ///
    /// Tri-state on purpose. The sample needs "does this folder carry the flag
    /// at all", which absence answers and a false does not.
    ///
    /// Caller must hold the gate.
    /// </summary>
    private static bool? ReadCash(WzObject child)
    {
        WzPropertyCollection? properties;
        try
        {
            if (child is WzImage image)
            {
                // The expensive line in this file, and unavoidable: the flag is
                // inside the image. Measured at 0.93 ms an image on a v232
                // Character.wz.
                WzSessionService.EnsureParsed(image);
                properties = image.WzProperties;
            }
            else if (child is WzImageProperty property)
            {
                // Already parsed: enumerating the parent image parsed it.
                properties = property.WzProperties;
            }
            else
            {
                // A subdirectory. Nothing to read, and nothing to parse to find
                // that out.
                return null;
            }
        }
        catch
        {
            // A corrupt or unreadable image. One of those must not fail the
            // whole folder's markers, and "no flag" is the honest answer for a
            // child we could not read.
            return null;
        }

        WzImageProperty? info = properties?.FindByName("info");
        WzImageProperty? cash = info?.WzProperties?.FindByName("cash");
        if (cash == null)
            return null;

        // Parsed as a number rather than compared to "1": the value is an Int in
        // almost every image, but at least one Cap in a v232 client stores it as
        // the String "1", and a client that stored 2 would still mean "cash".
        return long.TryParse(cash.WzValue?.ToString(), out long value) && value != 0;
    }
}
