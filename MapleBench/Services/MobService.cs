using System.Globalization;
using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// Presents Mob.wz as mobs rather than as a property tree.
///
/// Like <see cref="CashShopService"/>, this is a projection and not a second
/// editor: every write goes through <see cref="WzEditService"/>, so mob edits
/// share one dirty state, one undo history and one save pipeline with everything
/// else. Nothing here constructs or mutates a MapleLib object directly.
///
/// A note on why this does not use MapleLib's own <c>MobData</c>, which parses
/// the same node into a typed model: it has no serialiser, it ORs hardcoded mob
/// ids into <c>IsBoss</c> and <c>FirstAttack</c> so the parsed value is not the
/// stored value, it reads the HP-bar colours only for bosses, it narrows values
/// to byte/short, and it silently resolves <c>info/link</c> — so for a linked mob
/// every field it reports comes from a different image than the one you would be
/// writing to. It is a fine read model and an unusable write model. Reading the
/// raw node keeps what is displayed and what is edited the same thing.
/// </summary>
public sealed class MobService
{
    /// <summary>
    /// A browse list is for finding a mob, not for reading a client end to end.
    /// Real Mob.wz files run to a few thousand images; this is a backstop against
    /// an unusual one, and the UI is told when it bites.
    /// </summary>
    private const int MaxRows = 6000;

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly UndoService _undo;

    /// <summary>
    /// The last browse list, and the tree generation it was taken from.
    ///
    /// Building it means parsing every mob image: measured at 9.8s for a v232
    /// Mob.wz (2,742 images) on a warm cache. That is once per session and it
    /// happens under the session gate, so paying it on every keystroke-triggered
    /// refresh would make the mode unusable. <see cref="WzSessionService.Generation"/>
    /// already ticks on every structural change, which is exactly the staleness
    /// test this needs — an edit to a mob's HP does not change the tree shape, so
    /// the dirty flag and the edited value are refreshed from the write response
    /// rather than by rebuilding the list.
    /// </summary>
    private readonly Dictionary<string, (int Structure, int Value, int Names, MobListDto List)> _listCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One lock object per cache key, so two callers wanting the same list build
    /// it once. See <see cref="BuildLockFor"/>. Never held while taking the
    /// session gate in the other order.
    /// </summary>
    private readonly Dictionary<string, object> _buildLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// The result cache is bounded, but these lock identities are intentionally
    /// stable for the process lifetime. Evicting an object while a builder still
    /// holds it lets the same key acquire a second object and defeats the exact
    /// single-flight guarantee this dictionary exists to provide. In normal use
    /// the key count is one per open Mob archive plus the all-archives key.
    /// </summary>
    private const int MaxCachedLists = 16;

    /// <summary>
    /// Mob images parsed per gate hold while the list is built.
    ///
    /// Measured at 2.7 ms an image on a v232 client, so 64 is roughly 175 ms of
    /// gate time per chunk — short enough that a browse or a thumbnail queued
    /// behind the build is served promptly, long enough that the lock traffic
    /// does not show up in the total.
    /// </summary>
    private const int ChunkSize = 64;

    /// <summary>
    /// How many times a build may be restarted by an edit landing mid-flight
    /// before it gives up on interleaving and takes the gate for the whole pass.
    /// Without the ceiling a continuously-edited session could restart for ever.
    /// </summary>
    private const int MaxChunkedAttempts = 3;

    public MobService(WzSessionService session, WzEditService edit, StringPoolService strings, UndoService undo)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _undo = undo;
    }

    /// <summary>Whether any archive that could hold mobs is open.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_session.Gate)
                return MobArchives(null).Count > 0;
        }
    }

    #region Browse

    public MobListDto List(
        string? fileId, bool resolveNames, CancellationToken cancel = default,
        bool allowExclusiveFallback = true)
    {
        string key = $"{fileId ?? "*"}|{resolveNames}";

        // Warmed before the gate is taken, not per row.
        //
        // Two reasons. It is the same reason NpcService pre-builds its func
        // index: the pool takes the gate itself, so asking it 2,742 times from
        // inside the gate is 2,742 nested acquisitions. And it removes a lock
        // ordering hazard — the pool's build takes its own build lock and then
        // the session gate, so a caller holding the gate and waiting on the
        // build lock could meet a builder holding the build lock and waiting on
        // the gate.
        if (resolveNames)
            _strings.Warm(cancel, allowExclusiveFallback);

        // Cheap path first: a warm cache needs neither build lock nor build.
        lock (_session.Gate)
        {
            if (TryServeCached(key, resolveNames, out MobListDto? hit))
                return hit;
        }

        // One builder per key.
        //
        // The warm-up asks for exactly the list the user is about to ask for --
        // same key, "*|True" -- and with no gate here both parsed all 2,742
        // images, interleaving 64-image chunks against each other. The work was
        // done twice and the user's request finished at roughly double the solo
        // cost, which is the opposite of what a warm-up is for.
        //
        // Taken outside the session gate, and after Warm() above, so the only
        // lock held while waiting is this one. A builder that is cancelled or
        // throws releases it on the way out and the waiter builds instead.
        lock (BuildLockFor(key))
        {
            // Re-checked: whoever held the lock has very likely just filled it.
            lock (_session.Gate)
            {
                if (TryServeCached(key, resolveNames, out MobListDto? cached))
                    return cached;
            }

            // Chunked passes can be restarted by an edit landing mid-build. After a
            // few tries the last one takes the gate once and finishes, so a session
            // being edited continuously still gets a list rather than spinning.
            for (int attempt = 0; ; attempt++)
            {
                MobListDto? built = TryBuild(fileId, resolveNames,
                                             interleave: !allowExclusiveFallback || attempt < MaxChunkedAttempts,
                                             cancel, out int builtAtGeneration);
                if (built == null)
                    continue;   // the tree moved under the build; nothing partial is kept

                lock (_session.Gate)
                {
                    // Only cached if nothing has moved since the build finished.
                    //
                    // TryBuild returning non-null means nothing moved DURING the
                    // pass -- value edits included, since they tick Generation
                    // too -- but the gate is released between there and here, so
                    // an edit can still land in the gap. Stamping the current
                    // numbers onto a list built before it would bless pre-edit
                    // data as current, for ever. This caller still gets the list
                    // it built, which was true when it was built; the next caller
                    // rebuilds.
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

    /// <summary>
    /// The build lock for one cache key, created on first use.
    ///
    /// Its own tiny lock rather than the session gate: this is taken while a
    /// build runs, and a build takes and releases the gate hundreds of times.
    /// Keyed rather than global so a list scoped to one archive does not queue
    /// behind the everything list.
    /// </summary>
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
    /// One build pass. Returns null when the tree changed while it ran, in which
    /// case everything it built is thrown away — a list assembled from two
    /// different states would be wrong in a way nobody could see.
    /// </summary>
    private MobListDto? TryBuild(
        string? fileId, bool resolveNames, bool interleave, CancellationToken cancel, out int generation)
    {
        MobListDto result = new();
        List<MobSummaryDto> mobs = result.Mobs;

        List<(WzImage Image, string Path)> work = new();
        lock (_session.Gate)
        {
            generation = _session.Generation;
            foreach (OpenFile file in MobArchives(fileId))
            {
                if (file.LooseImage != null)
                {
                    work.Add((file.LooseImage, file.Id));
                    continue;
                }
                WzDirectory? root = _session.RoleRoot(file, "Mob");
                if (root == null)
                    continue;
                work.AddRange(EnumerateMobImages(root, _session.RoleRootPath(file, "Mob")));
            }
        }

        bool complete = _session.TryRunChunked(generation, work, item =>
        {
            if (mobs.Count >= MaxRows)
            {
                result.Truncated = true;
                return;
            }
            WzImage image = _session.MaterializeImage(item.Image);
            if (!TryMobId(image.Name, out int mobId))
                return;
            mobs.Add(Summarise(image, item.Path, mobId, resolveNames));
        }, ChunkSize, interleave, cancel);

        if (!complete)
            return null;

        result.Stats = Summarise(mobs);
        return result;
    }

    /// <summary>
    /// One page of the list, so the first screen can paint without waiting for
    /// 2,742 rows to be serialised.
    ///
    /// The list itself is built (and cached) whole: the expensive part is
    /// parsing the images, and a page cannot be produced without the sort order
    /// and stats the whole list defines. What paging saves is the JSON — the mob
    /// list is 1.2 MB and the skill list 2.7 MB on a v232 client, and that is
    /// most of what a warm request costs.
    /// </summary>
    public (MobListDto Page, int Total) Page(
        string? fileId, bool resolveNames, int offset, int limit, CancellationToken cancel = default)
    {
        MobListDto all = List(fileId, resolveNames, cancel);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxRows);

        return (new MobListDto
        {
            Mobs = all.Mobs.Skip(offset).Take(limit).ToList(),
            Stats = all.Stats,
            Truncated = all.Truncated,
        }, all.Mobs.Count);
    }

    /// <summary>
    /// Serves the cached list when it is still true, patching the rows that a
    /// value edit has changed. False means it has to be rebuilt.
    ///
    /// This is the half of the generation split that makes the split honest. The
    /// cache survives a value edit — which is the whole point, and is what turns
    /// a five-second wait after every keystroke into nothing — but only because
    /// every image named by <see cref="WzSessionService.ValueChanges"/> is
    /// re-read from the live tree before the list goes out. Relaxing the key
    /// without this would serve the pre-edit HP in the grid: fast, and wrong,
    /// which is the one trade this codebase does not make.
    ///
    /// Caller must hold the gate.
    /// </summary>
    private bool TryServeCached(string key, bool resolveNames, out MobListDto? list)
    {
        list = null;
        if (!_listCache.TryGetValue(key, out (int Structure, int Value, int Names, MobListDto List) cached))
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
            return false;   // the tree moved; nothing here can be patched into truth

        if (cached.Value != value && !TryPatch(cached.List, touched, resolveNames))
            return false;   // a touched row could not be re-read; rebuild rather than guess

        // Dirty flags move without the tree changing shape, so they are the
        // one thing refreshed on every hit.
        RefreshDirty(cached.List);
        _listCache[key] = (structure, value, names, cached.List);
        list = cached.List;
        return true;
    }

    /// <summary>
    /// Re-summarises the rows for the named images. False when any of them
    /// cannot be re-read, which the caller turns into a full rebuild.
    ///
    /// The touched set is replayed whole rather than as a delta since the row
    /// was cached. Re-summarising an image twice produces the same row, so the
    /// extra work is a few images and the alternative is per-cache bookkeeping
    /// that could get the delta wrong — and getting it wrong means a stale
    /// number on screen.
    /// </summary>
    private bool TryPatch(MobListDto list, IReadOnlyCollection<string> touched, bool resolveNames)
    {
        if (touched.Count == 0)
            return true;

        Dictionary<string, int> index = new(list.Mobs.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Mobs.Count; i++)
            index[list.Mobs[i].Path] = i;

        bool any = false;
        foreach (string path in touched)
        {
            // The touched path names the edited property, so the row it belongs
            // to is the deepest ancestor this list has a row for. Walking the
            // ancestors beats trying to work out which segment is the image:
            // a WZ directory may itself be called something.img, and guessing
            // wrong means skipping the patch and serving the pre-edit number.
            int row = WzSessionService.OwnerOf(index, path);

            // An edit to something this list does not show -- an NPC, a string,
            // a mob in an archive this list is scoped away from -- is not a
            // problem, it is simply not ours.
            if (row < 0)
                continue;

            string rowPath = list.Mobs[row].Path;
            if (_session.Resolve(rowPath) is not WzImage image)
                return false;
            if (!TryMobId(image.Name, out int mobId))
                return false;

            try
            {
                WzSessionService.EnsureParsed(image);
                list.Mobs[row] = Summarise(image, rowPath, mobId, resolveNames);
                any = true;
            }
            catch
            {
                return false;
            }
        }

        // The totals are derived from the rows, so they move with them.
        if (any)
            list.Stats = Summarise(list.Mobs);
        return true;
    }

    /// <summary>
    /// Re-reads just the dirty flags of a cached list. Editing a mob's HP does not
    /// change the tree's shape, so the structural generation does not tick and the
    /// cached rows stay valid — but whether the image is now unsaved has changed,
    /// and that is what the browse grid marks.
    /// </summary>
    private void RefreshDirty(MobListDto list)
    {
        foreach (MobSummaryDto mob in list.Mobs)
        {
            try
            {
                if (_session.Resolve(mob.Path) is WzImage image)
                    mob.Dirty = image.Changed;
            }
            catch
            {
                // A row whose path no longer resolves. Close() takes the same gate,
                // so this is not a file closing under us -- it is a node that a
                // structural edit removed, which also ticks Generation, so the very
                // next call rebuilds the list from scratch. Leaving the stale flag
                // for those few milliseconds is better than failing the request.
            }
        }
    }

    private MobSummaryDto Summarise(WzImage image, string path, int mobId, bool resolveNames)
    {
        MobSummaryDto dto = new()
        {
            Path = path,
            MobId = mobId,
            Dirty = image.Changed,
            Name = resolveNames ? _strings.GetMobName(mobId) : null,
        };

        // Parsed, deliberately, even though it is the expensive part: measured at
        // 9.8s for a v232 Mob.wz's 2,742 images. Listing them unparsed was worse
        // than slow -- every row read level 0 and 0 HP, which looks like corrupt
        // data rather than like data that has not loaded. The cost is paid once
        // per generation and cached; see _listCache.
        WzSessionService.EnsureParsed(image);

        WzImageProperty? info = image.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
        if (info == null)
            return dto;

        dto.Level = (int)ReadLong(info, "level");
        dto.MaxHP = ReadLong(info, "maxHP", "hp");
        dto.MaxMP = ReadLong(info, "maxMP", "mp");
        dto.Exp = ReadLong(info, "exp");
        dto.Padamage = ReadLong(info, "PADamage");
        dto.Madamage = ReadLong(info, "MADamage");
        dto.IsBoss = ReadLong(info, "boss") != 0;
        dto.Undead = ReadLong(info, "undead") != 0;
        dto.ElemAttr = ReadText(info, "elemAttr");
        dto.LinkTarget = ReadText(info, "link");
        return dto;
    }

    private static MobStatsDto Summarise(List<MobSummaryDto> mobs)
    {
        MobStatsDto stats = new() { Total = mobs.Count };
        bool any = false;

        foreach (MobSummaryDto mob in mobs)
        {
            if (mob.IsBoss) stats.Bosses++;
            if (mob.Undead) stats.Undead++;
            if (mob.Level <= 0) continue;

            stats.MinLevel = any ? Math.Min(stats.MinLevel, mob.Level) : mob.Level;
            stats.MaxLevel = any ? Math.Max(stats.MaxLevel, mob.Level) : mob.Level;
            any = true;
        }
        return stats;
    }

    #endregion

    #region Detail

    public MobDetailDto Detail(string path)
    {
        lock (_session.Gate)
        {
            WzImage image = ResolveMobImage(path);
            WzSessionService.EnsureParsed(image);

            TryMobId(image.Name, out int mobId);
            MobDetailDto dto = new()
            {
                Path = path,
                MobId = mobId,
                Name = _strings.GetMobName(mobId),
                Dirty = image.Changed,
            };

            WzImageProperty? info = image.WzProperties.FirstOrDefault(
                p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
            string infoPath = WzPath.Child(path, "info");
            dto.LinkTarget = info == null ? null : ReadText(info, "link");

            // Present fields first, keyed by what the mob actually carries, then
            // the catalog entries it does not — the UI hides those behind a
            // toggle. An uncatalogued key is still shown: a field we have never
            // heard of is exactly what someone editing an unusual client needs.
            Dictionary<string, WzImageProperty> present = new(StringComparer.OrdinalIgnoreCase);
            if (info?.WzProperties != null)
            {
                foreach (WzImageProperty property in info.WzProperties)
                    present.TryAdd(property.Name ?? "", property);
            }

            Dictionary<string, MobFieldGroupDto> groups = new(StringComparer.Ordinal);

            foreach (MobFieldSpec spec in MobFieldCatalog.Fields)
            {
                present.TryGetValue(spec.Key, out WzImageProperty? property);
                Add(groups, spec, property, infoPath);
            }

            foreach ((string key, WzImageProperty property) in present)
            {
                if (MobFieldCatalog.Find(key) != null)
                    continue;
                Add(groups, MobFieldCatalog.Unknown(key), property, infoPath);
            }

            dto.Groups = groups.Values
                .OrderBy(g => MobFieldCatalog.GroupRank(g.Group))
                .ToList();
            return dto;
        }
    }

    private static void Add(
        Dictionary<string, MobFieldGroupDto> groups,
        MobFieldSpec spec,
        WzImageProperty? property,
        string infoPath)
    {
        if (!groups.TryGetValue(spec.Group, out MobFieldGroupDto? group))
        {
            group = new MobFieldGroupDto { Group = spec.Group };
            groups[spec.Group] = group;
        }

        // A container -- info/skill, info/revive, info/damagedElemAttr are all
        // sub-properties on a v232 mob -- has no scalar value. Rendered as an
        // ordinary Text field it drew an empty box that looked editable, and typing
        // into it reached WzNodeFactory's `default:` throw. Reported as a container
        // instead: the card links to it rather than pretending to edit it.
        bool isContainer = property is not null && property.WzValue is null && property.WzProperties?.Count > 0;

        group.Fields.Add(new MobFieldDto
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

    #endregion

    #region Write

    public MobDetailDto WriteFields(MobWriteRequest request)
    {
        if (request.Fields is null || request.Fields.Count == 0)
            return Detail(request.Path);

        lock (_session.Gate)
        {
            WzImage image = ResolveMobImage(request.Path);
            WzSessionService.EnsureParsed(image);

            string infoPath = WzPath.Child(request.Path, "info");
            EnsureInfo(image, request.Path);

            // One batch, so a card's worth of edits is one Ctrl+Z rather than
            // fifteen. Same reason the cash shop batches a bulk add.
            using IDisposable batch = _undo.Batch(
                request.Fields.Count == 1
                    ? $"Edit {request.Fields[0].Key}"
                    : $"Edit {request.Fields.Count} mob fields");

            foreach (MobFieldWrite field in request.Fields)
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
                    // trusting the catalog created a WzStringProperty holding "4"
                    // where every other mob has a WzIntProperty, and the client
                    // calls GetInt on it. CashShopService.WriteExtraField infers the
                    // same way for the same reason.
                    MobFieldSpec? spec = MobFieldCatalog.Find(field.Key);
                    string type = spec is null
                        ? (long.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                            ? "Int"
                            : "String")
                        : spec.Kind is MobFieldKind.Text or MobFieldKind.Elem ? "String" : "Int";

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

    public MobBulkResultDto Bulk(MobBulkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Field))
            throw new InvalidOperationException("No field was chosen.");

        MobBulkResultDto result = new();

        lock (_session.Gate)
        {
            // Everything is computed first, whether or not it will be written, so
            // a dry run and a real run cannot disagree about what happens.
            List<(string FieldPath, string After, MobBulkChangeDto Row)> writes = new();

            foreach (string path in request.Paths ?? new List<string>())
            {
                MobBulkChangeDto change = new() { Path = path };
                result.Changes.Add(change);

                WzImage image;
                try
                {
                    image = ResolveMobImage(path);
                    WzSessionService.EnsureParsed(image);
                }
                catch (Exception ex)
                {
                    change.Skipped = true;
                    change.Reason = ex.Message;
                    continue;
                }

                TryMobId(image.Name, out int mobId);
                change.MobId = mobId;
                change.Name = _strings.GetMobName(mobId);

                WzImageProperty? info = image.WzProperties.FirstOrDefault(
                    p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
                WzImageProperty? property = info?.WzProperties?.FirstOrDefault(
                    p => string.Equals(p.Name, request.Field, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    // Deliberately not created here. Bulk edit is for changing what
                    // exists; silently adding a field to a thousand mobs because a
                    // dropdown offered it is not a thing to do by accident.
                    change.Skipped = true;
                    change.Reason = "This mob has no such field.";
                    continue;
                }

                string? before = property.WzValue?.ToString();
                change.Before = before;

                if (!double.TryParse(before, NumberStyles.Any, CultureInfo.InvariantCulture, out double current))
                {
                    change.Skipped = true;
                    change.Reason = "The current value is not a number.";
                    continue;
                }

                double after = request.Op switch
                {
                    "add" => current + request.Value,
                    "multiply" => current * request.Value,
                    "percent" => current * (1 + request.Value / 100),
                    "set" => request.Value,
                    _ => throw new InvalidOperationException($"Unknown operation '{request.Op}'."),
                };

                // Rounding is a choice, not a default. The reference this is
                // modelled on rounded unconditionally and silently integerised
                // every float field it touched.
                // InvariantCulture on purpose: WzNodeFactory parses with
                // CultureInfo.InvariantCulture, so formatting in the desktop's
                // culture produced "83,333333" on a comma-decimal machine, which the
                // parse then rejected -- turning a bulk edit into a mid-batch throw
                // on every de-DE/fr-FR install. "F0" rather than "R" for the same
                // reason: "R" emits scientific notation past ~1e15 and the integer
                // parse rejects that too.
                string formatted = request.Round switch
                {
                    "none" => after.ToString("0.##########", CultureInfo.InvariantCulture),
                    "floor" => Math.Floor(after).ToString("F0", CultureInfo.InvariantCulture),
                    _ => Math.Round(after, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture),
                };

                change.After = formatted;
                writes.Add((WzPath.Child(WzPath.Child(path, "info"), request.Field), formatted, change));
            }

            if (!request.DryRun && writes.Count > 0)
            {
                using IDisposable batch = _undo.Batch(
                    $"{request.Op} {request.Field} on {writes.Count} mobs");

                // Per row, because a throw here used to abandon the loop with the
                // earlier rows already written and Applied never assigned -- the
                // request 500'd, the user concluded nothing had happened, and the
                // archive held a partial edit. WzEditService.SetValueMany sets the
                // house pattern: never let one bad row decide the batch's fate, and
                // report what actually landed.
                // The row travels with the write rather than being looked up
                // again: `result.Changes.First(c => c.Path == path)` inside this
                // loop is O(n^2), and a 2,742-mob bulk edit spent millions of
                // string comparisons on it while holding the gate.
                foreach ((string fieldPath, string after, MobBulkChangeDto row) in writes)
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

    /// <summary>Open archives that could hold mobs: Mob.wz, Mob001.wz, Mob2.wz...</summary>
    private List<OpenFile> MobArchives(string? fileId)
        => _session.SelectRoleSources("Mob", fileId);

    /// <summary>
    /// Mob images, at the archive root and one level down. Clients differ about
    /// whether they group them, and both shapes turn up in the wild.
    /// </summary>
    private static IEnumerable<(WzImage Image, string Path)> EnumerateMobImages(WzDirectory root, string fileId)
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

    private WzImage ResolveMobImage(string path)
    {
        object node = _session.Resolve(path);
        return node as WzImage
            ?? throw new InvalidOperationException($"'{path}' is not a mob image.");
    }

    private void EnsureInfo(WzImage image, string imagePath)
    {
        if (image.WzProperties.Any(p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase)))
            return;

        _edit.Add(new AddNodeRequest { Path = imagePath, Name = "info", Type = "SubProperty" });
    }

    /// <summary>Whether a mob's info child is a group rather than a single value.</summary>
    private static bool IsContainer(WzImage image, string key)
    {
        WzImageProperty? info = image.WzProperties.FirstOrDefault(
            p => string.Equals(p.Name, "info", StringComparison.OrdinalIgnoreCase));
        WzImageProperty? property = info?.WzProperties?.FindByName(key);
        return property is not null && property.WzValue is null && property.WzProperties?.Count > 0;
    }

    private static bool Exists(WzImage image, string key)
    {
        WzImageProperty? info = image.WzProperties.FindByName("info");
        return info?.WzProperties?.FindByName(key) is not null;
    }

    /// <summary>"0100100.img" -> 100100. Leading zeros are the norm.</summary>
    private static bool TryMobId(string? name, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name))
            return false;

        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return int.TryParse(stem, out id);
    }

    private static long ReadLong(WzImageProperty info, params string[] keys)
    {
        foreach (string key in keys)
        {
            WzImageProperty? property = info.WzProperties?.FindByName(key);
            if (property != null && long.TryParse(property.WzValue?.ToString(), out long value))
                return value;
        }
        return 0;
    }

    private static string? ReadText(WzImageProperty info, string key)
    {
        WzImageProperty? property = info.WzProperties?.FindByName(key);
        string? text = property?.WzValue?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    #endregion
}
