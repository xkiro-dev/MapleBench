using System.Globalization;
using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>How a String.wz image arranges its entries.</summary>
public enum StringLayout
{
    /// <summary>&lt;Image&gt;/&lt;id&gt;/&lt;field&gt; — Consume.img, Mob.img, Npc.img, Skill.img...</summary>
    Flat,

    /// <summary>
    /// One wrapper level named after the image, then either ids directly
    /// (Etc.img/Etc/&lt;id&gt;) or a category level first
    /// (Eqp.img/Eqp/&lt;category&gt;/&lt;id&gt;). Which of the two is decided from the
    /// archive, not assumed — see <see cref="StringEditService.NestedGroups"/>.
    /// </summary>
    Nested,

    /// <summary>Map.img/&lt;region&gt;/&lt;id&gt;/{mapName,streetName}.</summary>
    Regioned,
}

/// <summary>One String.wz image and the shape its entries take.</summary>
/// <param name="Image">Image name, e.g. "Consume.img".</param>
/// <param name="Layout">How entries are nested inside it.</param>
public sealed record StringImageSpec(string Image, StringLayout Layout);

/// <summary>
/// A kind of named thing, and where its names live.
/// </summary>
/// <param name="Kind">Wire name, matching <c>/api/db/search</c>: item, mob, skill, npc, map.</param>
/// <param name="Label">What a person calls it.</param>
/// <param name="Fields">The fields this editor writes, in display order.</param>
/// <param name="Images">Every image of this kind, for browsing.</param>
public sealed record StringKindSpec(
    string Kind,
    string Label,
    string[] Fields,
    StringImageSpec[] Images);

/// <summary>
/// Reads and writes the display text in String.wz — the gap that makes a newly
/// created item show up nameless in game.
///
/// <see cref="StringPoolService"/> already knows every layout here, but only for
/// reading, and only as id -&gt; name. This is the other half: the entry's path,
/// the fields around the name, and the ability to create the entry when it does
/// not exist. The layout rules are deliberately the same ones —
/// <c>CollectFlat</c>, <c>CollectNested</c> and <c>CollectMaps</c> — because a
/// write that lands somewhere the pool does not read would produce a name the
/// editor shows and the client never does.
///
/// Like every other mode, this is a projection and not a second editor: every
/// write goes through <see cref="WzEditService"/> inside one
/// <see cref="UndoService.Batch"/>.
///
/// One thing it does NOT do, and says so: it never touches the client's own
/// UI strings (StringTable.img, ToolTipHelp.img, the EULAs). Those are not
/// keyed by id and there is no safe generic write for them; the Explorer edits
/// them by hand.
/// </summary>
public sealed class StringEditService
{
    /// <summary>
    /// Rows returned by one browse call. The kinds run to tens of thousands of
    /// entries (42,647 equips in a v232 String.wz), so the list is a search
    /// result rather than a dump, and it reports when the cap bites.
    /// </summary>
    public const int MaxRows = 500;

    /// <summary>
    /// One index per kind, and there are five of them. Bounded anyway, for the
    /// same reason every other cache in the app is: the key comes off the wire.
    /// </summary>
    private const int MaxCachedIndexes = 8;

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly UndoService _undo;
    private readonly ILogger<StringEditService> _log;

    /// <summary>
    /// The browse index per kind, and the tree generation it was taken from.
    ///
    /// Building the item index walks 42,647 equips, 22,487 consumables, 7,328
    /// etc, 4,638 setup, 3,273 cash and 1,108 pet entries — 81,481 nodes,
    /// measured at 44ms cold and 3ms warm on a v232 String.wz. The other four
    /// kinds are 9,900 mobs, 11,176 skills, 10,955 NPCs and 15,984 maps, each
    /// under 50ms. Cheap because String.wz is 17 MB and already parsed by the
    /// name pool; cached anyway because a browse runs per keystroke.
    /// <see cref="WzSessionService.Generation"/> ticks on every edit (each write
    /// invalidates path resolution), so a write drops the index and the next
    /// browse rebuilds it — which is exactly right, because a write can add an
    /// entry the index would otherwise not know about.
    /// </summary>
    private readonly Dictionary<string, (int Generation, List<IndexRow> Rows)> _indexCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One entry as the browse index knows it.
    ///
    /// The kind's two headline fields are captured during the walk rather than
    /// re-read later: a browse row wants both ("Roger" / "Rookie Instructor"),
    /// and resolving 10,955 paths a second time to get the second one costs more
    /// than the whole walk did.
    /// </summary>
    private sealed record IndexRow(int Id, string Path, string? Name, string? Second, string Image, string? Group);

    public StringEditService(
        WzSessionService session,
        WzEditService edit,
        StringPoolService strings,
        UndoService undo,
        ILogger<StringEditService> log)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _undo = undo;
        _log = log;
    }

    #region Catalog

    private static readonly StringKindSpec[] AllKinds =
    {
        new("item", "Items", new[] { "name", "desc" }, new[]
        {
            // Order matters only for reporting; a lookup probes all of them.
            new StringImageSpec("Eqp.img", StringLayout.Nested),
            new StringImageSpec("Consume.img", StringLayout.Flat),
            new StringImageSpec("Ins.img", StringLayout.Flat),
            new StringImageSpec("Etc.img", StringLayout.Nested),
            new StringImageSpec("Cash.img", StringLayout.Flat),
            new StringImageSpec("Pet.img", StringLayout.Flat),
        }),
        new("mob", "Mobs", new[] { "name" }, new[]
        {
            new StringImageSpec("Mob.img", StringLayout.Flat),
        }),
        new("skill", "Skills", new[] { "name", "desc" }, new[]
        {
            new StringImageSpec("Skill.img", StringLayout.Flat),
        }),
        new("npc", "NPCs", new[] { "name", "func" }, new[]
        {
            new StringImageSpec("Npc.img", StringLayout.Flat),
        }),
        new("map", "Maps", new[] { "mapName", "streetName" }, new[]
        {
            new StringImageSpec("Map.img", StringLayout.Regioned),
        }),
    };

    public static IReadOnlyList<StringKindSpec> Kinds => AllKinds;

    public static StringKindSpec? Find(string? kind) =>
        AllKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));

    private static StringKindSpec Require(string? kind) =>
        Find(kind) ?? throw new InvalidOperationException(
            $"'{kind}' is not something String.wz names. Use one of: " +
            string.Join(", ", AllKinds.Select(k => k.Kind)) + ".");

    /// <summary>
    /// The image a *new* entry for this id belongs in.
    ///
    /// The client derives this from the id itself, so it is reproducible without
    /// a lookup table. Equips below 1,000,000 are the exception worth naming:
    /// skins (1xxxx), faces (2xxxx/5xxxx) and hair (3xxxx/4xxxx/6xxxx) are
    /// five-digit and still live in Eqp.img — verified against a v232 client,
    /// which holds 19 skins, 8,024 faces and 12,093 hairs there.
    /// </summary>
    private static StringImageSpec ImageForNewItem(int id)
    {
        if (id <= 0)
            throw new InvalidOperationException($"{id} is not an item id.");

        if (id < 1_000_000 || id / 1_000_000 == 1)
            return new StringImageSpec("Eqp.img", StringLayout.Nested);

        return (id / 1_000_000) switch
        {
            2 => new StringImageSpec("Consume.img", StringLayout.Flat),
            3 => new StringImageSpec("Ins.img", StringLayout.Flat),
            4 => new StringImageSpec("Etc.img", StringLayout.Nested),
            5 => id / 10_000 == 500
                ? new StringImageSpec("Pet.img", StringLayout.Flat)
                : new StringImageSpec("Cash.img", StringLayout.Flat),
            _ => throw new InvalidOperationException(
                $"{id} is outside every item range String.wz names (1xxxxxx equip, 2xxxxxx use, " +
                "3xxxxxx setup, 4xxxxxx etc, 5xxxxxx cash/pet). Add the entry by hand in the Explorer."),
        };
    }

    private StringImageSpec ImageForNew(StringKindSpec spec, int id) =>
        spec.Kind == "item" ? ImageForNewItem(id) : spec.Images[0];

    /// <summary>
    /// The Eqp.img category a new equip belongs in, when the archive itself
    /// cannot say.
    ///
    /// Read off a v232 client's own distribution rather than invented: the
    /// archive is asked first (see <see cref="ResolveGroup"/>) and this is only
    /// the fallback for an id whose slot no existing entry shares. The awkward
    /// ones are real — 111xxxx is 1,057 Rings but also 36 Accessories, and
    /// 112xxxx is 320 Accessories but 2 Rings — so the majority is what is
    /// encoded here and the archive overrides it whenever it has an opinion.
    /// </summary>
    private static string? StandardEqpCategory(int id)
    {
        int slot = id / 10_000;
        return slot switch
        {
            1 => "Skin",
            2 or 5 => "Face",
            3 or 4 or 6 => "Hair",
            100 => "Cap",
            104 => "Coat",
            105 => "Longcoat",
            106 => "Pants",
            107 => "Shoes",
            108 => "Glove",
            109 => "Shield",
            110 => "Cape",
            111 => "Ring",
            >= 101 and <= 120 => "Accessory",
            >= 161 and <= 165 => "Mechanic",
            166 or 167 => "Android",
            168 => "Bits",
            171 => "ArcaneForce",
            >= 180 and <= 183 => "PetEquip",
            190 or 191 or 193 or 198 or 199 => "Taming",
            >= 194 and <= 197 => "Dragon",
            >= 121 and <= 170 => "Weapon",
            _ => null,
        };
    }

    #endregion

    #region Availability

    /// <summary>Whether any archive that could hold display names is open.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_session.Gate)
                return StringArchives().Count > 0;
        }
    }

    /// <summary>
    /// The String archives this service reads and writes, in priority order.
    ///
    /// Delegated to <see cref="StringPoolService"/> rather than reimplemented.
    /// The two must agree exactly — the pool is what the rest of the app reads
    /// names from, so writing into an archive it does not consult would leave
    /// the UI showing the old name, indistinguishable from a failed write.
    /// </summary>
    private List<OpenFile> StringArchives() => _strings.StringArchives();

    /// <summary>Every Eqp.img category the open archive actually has, for the UI's picker.</summary>
    public List<string> EqpCategories()
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in StringArchives())
            {
                if (FindImage(file, "Eqp.img") is not WzImage image)
                    continue;
                if (Wrapper(image) is not WzImageProperty wrapper || wrapper.WzProperties == null)
                    continue;

                List<string> groups = NestedGroups(wrapper);
                if (groups.Count > 0)
                    return groups;
            }
            return new List<string>();
        }
    }

    /// <summary>Every Map.img region the open archive actually has.</summary>
    public List<string> MapRegions()
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in StringArchives())
            {
                if (FindImage(file, "Map.img") is not WzImage image)
                    continue;
                List<string> regions = image.WzProperties
                    .Where(p => p.WzProperties?.Count > 0)
                    .Select(p => p.Name ?? "")
                    .Where(n => n.Length > 0)
                    .ToList();
                if (regions.Count > 0)
                    return regions;
            }
            return new List<string>();
        }
    }

    #endregion

    #region Browse

    public StringListDto List(string kind, string? query, int limit)
    {
        StringKindSpec spec = Require(kind);
        limit = Math.Clamp(limit, 1, MaxRows);

        List<IndexRow> rows = Index(spec);

        StringListDto result = new()
        {
            Kind = spec.Kind,
            Total = rows.Count,
            Available = rows.Count > 0 || IsAvailable,
        };

        // Scored, then cut — never cut while scanning, for the same reason
        // StringPoolService.Search does not: taking the first N in pool order
        // means the exact match can fall off the end of the page.
        List<(IndexRow Row, int Score)> scored = new();
        string trimmed = (query ?? "").Trim();
        bool numeric = trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit);

        foreach (IndexRow row in rows)
        {
            int score;
            if (trimmed.Length == 0)
            {
                score = 0;
            }
            else if (numeric)
            {
                string digits = row.Id.ToString(CultureInfo.InvariantCulture);
                if (digits.Equals(trimmed, StringComparison.Ordinal))
                    score = 100;
                else if (digits.StartsWith(trimmed, StringComparison.Ordinal)
                         || digits.PadLeft(8, '0').StartsWith(trimmed, StringComparison.Ordinal))
                    score = 60;
                else if (row.Name != null && row.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    score = 30;
                else
                    continue;
            }
            else
            {
                string name = row.Name ?? "";
                if (name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    score = 100;
                else if (name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                    score = 70;
                else if (name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    score = 40;
                else
                    continue;
            }
            scored.Add((row, score));
        }

        result.Matched = scored.Count;
        result.Truncated = scored.Count > limit;

        IEnumerable<IndexRow> page = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Row.Id)
            .Take(limit)
            .Select(s => s.Row);

        lock (_session.Gate)
        {
            foreach (IndexRow row in page)
            {
                StringEntryDto dto = new()
                {
                    Kind = spec.Kind,
                    Id = row.Id,
                    Path = row.Path,
                    Present = true,
                    Image = row.Image,
                    Layout = LayoutOf(spec, row.Image).ToString(),
                    Group = row.Group,
                };

                // A row whose node no longer resolves is left with its indexed
                // name rather than failing the whole page: the generation has
                // already ticked, so the very next call rebuilds the index.
                if (_session.TryResolve(row.Path) is WzImageProperty entry)
                    Fill(dto, spec, entry);
                else
                    dto.Fields[spec.Fields[0]] = row.Name;

                result.Entries.Add(dto);
            }
        }
        return result;
    }

    /// <summary>
    /// The browse index for one kind, cached against
    /// <see cref="WzSessionService.Generation"/>.
    /// </summary>
    private List<IndexRow> Index(StringKindSpec spec)
    {
        lock (_session.Gate)
        {
            // Captured inside the lock that builds, not read afterwards: the
            // build takes long enough for an edit to land, and stamping a newer
            // generation would mark a pre-edit index as current for ever.
            int builtAtGeneration = _session.Generation;

            if (_indexCache.TryGetValue(spec.Kind, out (int Generation, List<IndexRow> Rows) cached)
                && cached.Generation == builtAtGeneration)
            {
                return cached.Rows;
            }

            List<IndexRow> rows = new();
            foreach (OpenFile file in StringArchives())
            {
                foreach (StringImageSpec imageSpec in spec.Images)
                {
                    if (FindImage(file, imageSpec.Image) is not WzImage image)
                        continue;

                    string imagePath = StringImagePath(file, image);
                    try
                    {
                        Collect(rows, spec, imageSpec, image, imagePath);
                    }
                    catch (Exception ex)
                    {
                        // One unreadable image must not cost the whole kind its
                        // index; the pool takes the same line.
                        _log.LogDebug(ex, "Skipping {Image} while indexing {Kind} strings",
                            imageSpec.Image, spec.Kind);
                    }
                }
            }

            if (_indexCache.Count >= MaxCachedIndexes)
                _indexCache.Clear();
            _indexCache[spec.Kind] = (builtAtGeneration, rows);
            return rows;
        }
    }

    private void Collect(
        List<IndexRow> rows, StringKindSpec spec, StringImageSpec imageSpec,
        WzImage image, string imagePath)
    {
        WzSessionService.EnsureParsed(image);
        string field = spec.Fields[0];
        string? second = spec.Fields.Length > 1 ? spec.Fields[1] : null;

        switch (imageSpec.Layout)
        {
            case StringLayout.Flat:
                foreach (WzImageProperty entry in image.WzProperties)
                    AddRow(rows, entry, imagePath, imageSpec.Image, null, field, second);
                break;

            case StringLayout.Nested:
            {
                WzImageProperty? wrapper = Wrapper(image);
                WzPropertyCollection? level = wrapper?.WzProperties ?? image.WzProperties;
                string levelPath = wrapper == null ? imagePath : WzPath.Child(imagePath, wrapper.Name ?? "");
                if (level == null)
                    break;

                foreach (WzImageProperty child in level)
                {
                    // Both levels, exactly as StringPoolService.CollectNested
                    // does: Etc.img lists ids straight under its wrapper while
                    // Eqp.img groups them by category, and both shapes are live
                    // in the same client.
                    if (AddRow(rows, child, levelPath, imageSpec.Image, null, field, second))
                        continue;
                    if (child.WzProperties == null)
                        continue;

                    string groupPath = WzPath.Child(levelPath, child.Name ?? "");
                    foreach (WzImageProperty entry in child.WzProperties)
                        AddRow(rows, entry, groupPath, imageSpec.Image, child.Name, field, second);
                }
                break;
            }

            case StringLayout.Regioned:
                foreach (WzImageProperty region in image.WzProperties)
                {
                    if (region.WzProperties == null)
                        continue;
                    string regionPath = WzPath.Child(imagePath, region.Name ?? "");
                    foreach (WzImageProperty entry in region.WzProperties)
                        AddRow(rows, entry, regionPath, imageSpec.Image, region.Name, field, second);
                }
                break;
        }
    }

    private static bool AddRow(
        List<IndexRow> rows, WzImageProperty entry, string parentPath,
        string image, string? group, string field, string? second)
    {
        if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return false;

        WzPropertyCollection? children = entry.WzProperties;
        string? name = children?.FindByName(field)?.WzValue?.ToString();
        string? extra = second == null ? null : children?.FindByName(second)?.WzValue?.ToString();
        rows.Add(new IndexRow(id, WzPath.Child(parentPath, entry.Name ?? ""), name, extra, image, group));
        return true;
    }

    /// <summary>
    /// id -&gt; text for one of a kind's two headline fields, off the cached
    /// browse index.
    ///
    /// This is what lets the NPC browser show "Roger — Rookie Instructor" on
    /// 10,742 rows without a String.wz lookup per row: the index has already
    /// read both, and <see cref="StringPoolService"/> only keeps the name.
    /// Later entries win, matching the pool's own last-writer-wins rule.
    /// </summary>
    public Dictionary<int, string> TextIndex(string kind, string field)
    {
        StringKindSpec spec = Require(kind);
        int position = Array.FindIndex(
            spec.Fields, f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (position is < 0 or > 1)
        {
            throw new InvalidOperationException(
                $"'{field}' is not one of {spec.Label}' indexed fields " +
                $"({string.Join(", ", spec.Fields.Take(2))}).");
        }

        Dictionary<int, string> map = new();
        foreach (IndexRow row in Index(spec))
        {
            string? text = position == 0 ? row.Name : row.Second;
            if (!string.IsNullOrEmpty(text))
                map[row.Id] = text;
        }
        return map;
    }

    #endregion

    #region Read one

    /// <summary>
    /// One entry, whether or not it exists.
    ///
    /// A missing entry is not an error — it is the state this whole service
    /// exists to fix — so it comes back with <c>Present = false</c> and the
    /// image it would be created in.
    /// </summary>
    public StringEntryDto Entry(string kind, int id)
    {
        StringKindSpec spec = Require(kind);

        lock (_session.Gate)
        {
            Located? found = Locate(spec, id);
            if (found != null)
            {
                StringEntryDto dto = new()
                {
                    Kind = spec.Kind,
                    Id = id,
                    Path = found.Path,
                    Present = true,
                    Image = found.Image,
                    Layout = LayoutOf(spec, found.Image).ToString(),
                    Group = found.Group,
                };
                Fill(dto, spec, found.Entry);
                return dto;
            }

            StringImageSpec target = ImageForNew(spec, id);
            StringEntryDto missing = new()
            {
                Kind = spec.Kind,
                Id = id,
                Present = false,
                Image = target.Image,
                Layout = target.Layout.ToString(),
            };
            foreach (string f in spec.Fields)
                missing.Fields[f] = null;
            return missing;
        }
    }

    /// <summary>
    /// One field's current text and the path it lives at, for callers that need
    /// to join against String.wz — the NPC card's chat lines, for instance.
    /// Returns (null, null) when nothing names it.
    /// </summary>
    public (string? Path, string? Text) Field(string kind, int id, string field)
    {
        StringKindSpec spec = Require(kind);
        lock (_session.Gate)
        {
            Located? found = Locate(spec, id);
            WzImageProperty? property = found?.Entry.WzProperties?.FindByName(field);
            if (found == null || property == null)
                return (null, null);

            // A container has no text of its own; saying so beats returning "".
            if (property.WzValue == null && property.WzProperties?.Count > 0)
                return (WzPath.Child(found.Path, property.Name ?? field), null);

            return (WzPath.Child(found.Path, property.Name ?? field), property.WzValue?.ToString());
        }
    }

    private void Fill(StringEntryDto dto, StringKindSpec spec, WzImageProperty entry)
    {
        foreach (string field in spec.Fields)
            dto.Fields[field] = Scalar(entry.WzProperties?.FindByName(field));

        if (entry.WzProperties == null)
            return;

        foreach (WzImageProperty child in entry.WzProperties)
        {
            string name = child.Name ?? "";
            if (name.Length == 0 || spec.Fields.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            dto.Other[name] = Scalar(child);
        }
    }

    /// <summary>
    /// A property's text, or a container's shape.
    ///
    /// Never "" for a container: an NPC's <c>dialogue</c> and <c>bubble</c> are
    /// sub-properties, and rendering them as empty strings draws an editable box
    /// that cannot be written to.
    /// </summary>
    private static string? Scalar(WzImageProperty? property)
    {
        if (property == null)
            return null;
        if (property.WzValue == null && property.WzProperties?.Count > 0)
            return $"{property.WzProperties.Count} entries";
        return property.WzValue?.ToString();
    }

    #endregion

    #region Write

    public StringWriteResultDto Write(StringWriteRequest request)
    {
        StringKindSpec spec = Require(request.Kind);
        Dictionary<string, string?> wanted = Wanted(spec, request);

        StringWriteResultDto result = new()
        {
            Kind = spec.Kind,
            Id = request.Id,
        };

        lock (_session.Gate)
        {
            // One batch for the whole entry: naming a new item creates the entry
            // node and two strings, and that is one Ctrl+Z, not three.
            using (IDisposable batch = _undo.Batch(Label(spec, request.Id, wanted)))
            {
                Applied applied = Apply(spec, request.Id, wanted, request.Category, request.Region, dryRun: false);
                result.Path = applied.Path;
                result.CreatedEntry = applied.CreatedEntry;
                result.Image = applied.Image;
                result.Group = applied.Group;
                result.GroupReason = applied.GroupReason;
                result.Changes = applied.Changes;
                result.Applied = applied.Changes.Count(c => !c.Skipped);
            }
        }

        // Outside the gate, and unconditionally: the pool caches id -> name, and
        // nothing else drops it. Skipping this leaves every list, every tree
        // label and every search result showing the name the entry had before —
        // which reads exactly like a write that silently did nothing.
        _strings.Invalidate();

        result.Entry = Entry(spec.Kind, request.Id);
        return result;
    }

    public StringBulkResultDto Bulk(StringBulkRequest request)
    {
        StringKindSpec spec = Require(request.Kind);
        StringBulkResultDto result = new() { Kind = spec.Kind, DryRun = request.DryRun };

        // Null rather than empty is what a hand-written POST body sends when the
        // list was omitted; a bulk call with no rows is a no-op, not a 500.
        List<StringBulkEntry> entries = request.Entries ?? new List<StringBulkEntry>();

        lock (_session.Gate)
        {
            // A dry run takes the batch too, and disposes it having recorded
            // nothing. That keeps the two paths one code path, so the preview
            // and the apply cannot disagree about what happens.
            using IDisposable batch = _undo.Batch(
                $"Name {entries.Count} {spec.Label.ToLowerInvariant()}");

            foreach (StringBulkEntry entry in entries)
            {
                StringBulkChangeDto row = new() { Id = entry.Id };
                result.Rows.Add(row);

                // Per row, because a throw here used to abandon the loop with
                // the earlier rows already written and nothing said about it.
                // One bad id must not decide the batch's fate.
                try
                {
                    Dictionary<string, string?> wanted = Wanted(spec, entry);
                    if (wanted.Count == 0)
                    {
                        row.Skipped = true;
                        row.Reason = "No text was given for this row.";
                        result.Skipped++;
                        continue;
                    }

                    Applied applied = Apply(
                        spec, entry.Id, wanted, entry.Category, entry.Region, request.DryRun);

                    row.Path = applied.Path;
                    row.CreatedEntry = applied.CreatedEntry;
                    row.Group = applied.Group;
                    row.Changes = applied.Changes;

                    if (applied.CreatedEntry)
                        result.Created++;
                    if (!request.DryRun)
                        result.Applied += applied.Changes.Count(c => !c.Skipped);
                }
                catch (Exception ex)
                {
                    row.Skipped = true;
                    row.Reason = ex.Message;
                    result.Skipped++;
                }
            }
        }

        if (!request.DryRun && result.Applied > 0)
            _strings.Invalidate();

        return result;
    }

    /// <summary>What one write actually did, or would do.</summary>
    private sealed record Applied(
        string? Path,
        bool CreatedEntry,
        string Image,
        string? Group,
        string? GroupReason,
        List<StringFieldChangeDto> Changes);

    /// <summary>
    /// The one place a string entry is written, shared by the single write, the
    /// bulk apply and the bulk preview.
    ///
    /// <paramref name="dryRun"/> changes nothing but whether the writes are
    /// issued: the before/after it reports is computed identically either way,
    /// so a preview that says "creates the entry and sets name" cannot be
    /// followed by an apply that does something else.
    /// </summary>
    private Applied Apply(
        StringKindSpec spec, int id, Dictionary<string, string?> wanted,
        string? category, string? region, bool dryRun)
    {
        if (id <= 0)
            throw new InvalidOperationException($"{id} is not a valid id.");
        if (StringArchives().Count == 0)
            throw new InvalidOperationException("String.wz is not open, so there is nowhere to write names.");

        List<StringFieldChangeDto> changes = new();
        Located? found = Locate(spec, id);

        string image;
        string? group;
        string? groupReason = null;
        string entryPath;
        bool created = false;

        if (found != null)
        {
            image = found.Image;
            group = found.Group;
            entryPath = found.Path;
        }
        else
        {
            StringImageSpec target = ImageForNew(spec, id);
            image = target.Image;

            (string containerPath, string? chosenGroup, string? reason) =
                EnsureContainer(spec, target, id, category, region, dryRun);
            group = chosenGroup;
            groupReason = reason;

            // Plain, never zero-padded. Every id space in a v232 String.wz is
            // written plainly bar 168 of Skill.img's 11,176 keys, and the
            // reader below probes the padded spellings anyway — so a new entry
            // is created in the form the client overwhelmingly uses, and an
            // existing padded one is found and updated rather than duplicated.
            string name = id.ToString(CultureInfo.InvariantCulture);
            entryPath = WzPath.Child(containerPath, name);
            created = true;

            if (!dryRun)
                _edit.Add(new AddNodeRequest { Path = containerPath, Name = name, Type = "SubProperty" });
        }

        foreach ((string field, string? value) in wanted)
        {
            StringFieldChangeDto change = new() { Field = field, After = value };
            changes.Add(change);

            WzImageProperty? existing = created
                ? null
                : found!.Entry.WzProperties?.FindByName(field);

            if (existing != null && existing.WzValue == null && existing.WzProperties?.Count > 0)
            {
                change.Skipped = true;
                change.After = null;
                change.Reason = $"'{field}' holds a group of values here, not one string. " +
                                "Open it in the Explorer to edit what is inside it.";
                continue;
            }

            change.Before = existing?.WzValue?.ToString();
            change.Created = existing == null;

            if (!change.Created && string.Equals(change.Before, value, StringComparison.Ordinal))
            {
                change.Skipped = true;
                change.Reason = "Already set to this.";
                continue;
            }

            if (dryRun)
                continue;

            string fieldPath = WzPath.Child(entryPath, field);
            if (existing == null)
            {
                _edit.Add(new AddNodeRequest
                {
                    Path = entryPath,
                    Name = field,
                    // Always a String: every name, desc, func, mapName and
                    // streetName in a v232 String.wz is one, and a WzIntProperty
                    // holding "5" would make the client read a number where it
                    // calls GetString.
                    Type = "String",
                    Value = value ?? "",
                });
            }
            else
            {
                _edit.SetValue(fieldPath, value);
            }
        }

        return new Applied(entryPath, created, image, group, groupReason, changes);
    }

    /// <summary>
    /// The node a new entry hangs off, creating the wrapper, category or region
    /// above it when they are missing. Returns the path, the group it settled
    /// on, and why.
    /// </summary>
    private (string Path, string? Group, string? Reason) EnsureContainer(
        StringKindSpec spec, StringImageSpec target, int id,
        string? category, string? region, bool dryRun)
    {
        List<OpenFile> archives = StringArchives();
        OpenFile file = archives.FirstOrDefault(candidate => FindImage(candidate, target.Image) != null)
            ?? archives[0];
        WzImage image = FindImage(file, target.Image)
            ?? throw new InvalidOperationException(
                $"'{file.Name}' has no {target.Image}, so there is nowhere to write {spec.Label.ToLowerInvariant()} names. " +
                "Open the String.wz that belongs to this client.");

        WzSessionService.EnsureParsed(image);
        string imagePath = StringImagePath(file, image);

        switch (target.Layout)
        {
            case StringLayout.Flat:
                return (imagePath, null, null);

            case StringLayout.Nested:
            {
                WzImageProperty? wrapper = Wrapper(image);
                string wrapperName = Stem(image.Name);
                string wrapperPath = WzPath.Child(imagePath, wrapper?.Name ?? wrapperName);
                if (wrapper == null && !dryRun)
                {
                    _edit.Add(new AddNodeRequest
                    {
                        Path = imagePath, Name = wrapperName, Type = "SubProperty",
                    });
                }

                List<string> groups = wrapper == null ? new List<string>() : NestedGroups(wrapper);
                if (groups.Count == 0)
                {
                    // Etc.img's shape: ids sit straight under the wrapper.
                    return (wrapperPath, null, null);
                }

                (string chosen, string reason) = ResolveGroup(
                    "category", groups, category, wrapper!, id,
                    () => StandardEqpCategory(id),
                    new[] { 10_000 });
                return (WzPath.Child(wrapperPath, chosen), chosen, reason);
            }

            case StringLayout.Regioned:
            {
                List<string> regions = image.WzProperties
                    .Where(p => p.WzProperties != null)
                    .Select(p => p.Name ?? "")
                    .Where(n => n.Length > 0)
                    .ToList();

                if (regions.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{target.Image} has no regions, so there is nowhere to file a map name. " +
                        "Add one in the Explorer first.");
                }

                // Region first by the map's own area, then by its world, then
                // the client's own catch-all. Map ids are 9 digits and grouped
                // by area, so a neighbour is nearly always there.
                (string chosen, string reason) = ResolveGroup(
                    "region", regions, region, null, id,
                    () => regions.FirstOrDefault(r => r.Equals("etc", StringComparison.OrdinalIgnoreCase)),
                    new[] { 1_000_000, 100_000_000 },
                    image.WzProperties);
                return (WzPath.Child(imagePath, chosen), chosen, reason);
            }

            default:
                throw new InvalidOperationException($"Unhandled layout {target.Layout}.");
        }
    }

    /// <summary>
    /// Picks the group (Eqp category / Map region) a new id belongs in.
    ///
    /// The archive is asked before any table is: whichever group already holds
    /// the most ids sharing this one's prefix is where the client itself files
    /// them, and that answer stays right for a private server whose ranges do
    /// not match a stock client. A caller-supplied group always wins, and must
    /// already exist — a typo silently creating a bogus group is exactly the
    /// kind of quiet failure this mode is meant not to have.
    /// </summary>
    private static (string Group, string Reason) ResolveGroup(
        string what, List<string> groups, string? requested,
        WzImageProperty? wrapper, int id,
        Func<string?> fallback, int[] prefixes,
        WzPropertyCollection? explicitGroups = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string? exact = groups.FirstOrDefault(
                g => g.Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact == null)
            {
                throw new InvalidOperationException(
                    $"There is no {what} called '{requested}'. This archive has: {string.Join(", ", groups)}.");
            }
            return (exact, $"You chose the {exact} {what}.");
        }

        WzPropertyCollection? collection = explicitGroups ?? wrapper?.WzProperties;
        if (collection != null)
        {
            foreach (int prefix in prefixes)
            {
                int bucket = id / prefix;
                if (bucket == 0)
                    continue;

                string? best = null;
                int bestCount = 0;
                foreach (WzImageProperty group in collection)
                {
                    if (group.WzProperties == null)
                        continue;
                    int count = 0;
                    foreach (WzImageProperty entry in group.WzProperties)
                    {
                        if (int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int other)
                            && other / prefix == bucket)
                            count++;
                    }
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = group.Name;
                    }
                }
                if (best != null)
                {
                    return (best, $"{bestCount} ids in the same range are already in the {best} {what}.");
                }
            }
        }

        string? standard = fallback();
        if (standard != null && groups.Any(g => g.Equals(standard, StringComparison.OrdinalIgnoreCase)))
        {
            string exact = groups.First(g => g.Equals(standard, StringComparison.OrdinalIgnoreCase));
            return (exact, $"No id in this range exists yet, so the standard {what} for it was used.");
        }

        throw new InvalidOperationException(
            $"Nothing says which {what} id {id} belongs in. Choose one: {string.Join(", ", groups)}.");
    }

    /// <summary>
    /// Only the fields the caller actually supplied, and only those this kind
    /// has. A null field means "leave it alone"; an empty string means "make it
    /// empty", and those are different requests.
    /// </summary>
    private static Dictionary<string, string?> Wanted(StringKindSpec spec, StringWriteRequest request)
        => Wanted(spec, new StringBulkEntry
        {
            Id = request.Id,
            Name = request.Name,
            Desc = request.Desc,
            Func = request.Func,
            MapName = request.MapName,
            StreetName = request.StreetName,
        });

    private static Dictionary<string, string?> Wanted(StringKindSpec spec, StringBulkEntry entry)
    {
        Dictionary<string, string?> wanted = new(StringComparer.Ordinal);

        void Offer(string field, string? value)
        {
            if (value == null)
                return;
            if (!spec.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{spec.Label} have no '{field}'. This kind writes: {string.Join(", ", spec.Fields)}.");
            }
            wanted[field] = value;
        }

        Offer("name", entry.Name);
        Offer("desc", entry.Desc);
        Offer("func", entry.Func);
        Offer("mapName", entry.MapName);
        Offer("streetName", entry.StreetName);
        return wanted;
    }

    private static string Label(StringKindSpec spec, int id, Dictionary<string, string?> wanted)
        => wanted.Count == 1
            ? $"Set {wanted.Keys.First()} of {spec.Label.TrimEnd('s')} {id}"
            : $"Name {spec.Label.TrimEnd('s').ToLowerInvariant()} {id}";

    #endregion

    #region Plumbing

    private sealed record Located(WzImageProperty Entry, string Path, string Image, string? Group);

    /// <summary>
    /// Finds an id's entry across every image, category and region of its kind.
    ///
    /// Every container lookup is a dictionary probe (MapleLib indexes property
    /// names case-insensitively), so the worst case — a map id against 73
    /// regions in eight spellings — is a few hundred probes and no scan.
    ///
    /// The spellings matter: a v232 Skill.img writes 167 of its 11,176 keys
    /// zero-padded to seven digits, so probing only "12" would miss "0000012"
    /// and the write would create a second entry beside the real one.
    /// </summary>
    private Located? Locate(StringKindSpec spec, int id)
    {
        foreach (OpenFile file in StringArchives())
        {
            foreach (StringImageSpec imageSpec in spec.Images)
            {
                if (FindImage(file, imageSpec.Image) is not WzImage image)
                    continue;

                WzSessionService.EnsureParsed(image);
                string imagePath = StringImagePath(file, image);

                switch (imageSpec.Layout)
                {
                    case StringLayout.Flat:
                        if (FindById(image.WzProperties, id) is WzImageProperty flat)
                            return new Located(flat, WzPath.Child(imagePath, flat.Name ?? ""), imageSpec.Image, null);
                        break;

                    case StringLayout.Nested:
                    {
                        WzImageProperty? wrapper = Wrapper(image);
                        WzPropertyCollection? level = wrapper?.WzProperties ?? image.WzProperties;
                        string levelPath = wrapper == null ? imagePath : WzPath.Child(imagePath, wrapper.Name ?? "");
                        if (level == null)
                            break;

                        if (FindById(level, id) is WzImageProperty direct)
                            return new Located(direct, WzPath.Child(levelPath, direct.Name ?? ""), imageSpec.Image, null);

                        foreach (WzImageProperty group in level)
                        {
                            if (group.WzProperties == null)
                                continue;
                            if (FindById(group.WzProperties, id) is WzImageProperty nested)
                            {
                                return new Located(
                                    nested,
                                    WzPath.Child(WzPath.Child(levelPath, group.Name ?? ""), nested.Name ?? ""),
                                    imageSpec.Image, group.Name);
                            }
                        }
                        break;
                    }

                    case StringLayout.Regioned:
                        foreach (WzImageProperty region in image.WzProperties)
                        {
                            if (region.WzProperties == null)
                                continue;
                            if (FindById(region.WzProperties, id) is WzImageProperty entry)
                            {
                                return new Located(
                                    entry,
                                    WzPath.Child(WzPath.Child(imagePath, region.Name ?? ""), entry.Name ?? ""),
                                    imageSpec.Image, region.Name);
                            }
                        }
                        break;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// An id in its plain form, then zero-padded to every width a WZ id uses.
    /// </summary>
    /// <remarks>
    /// Internal, not private, for the same reason <see cref="StringArchives"/> is:
    /// <see cref="PortService"/> has to find an id's entry in exactly the same
    /// spellings, and a second implementation that disagreed would create a
    /// duplicate entry beside the real one. The padding is not hypothetical — a
    /// v232 Sound.wz spells mob 100100 as "0100100" while its boss ids are not
    /// padded at all, and a v232 Skill.img writes 167 of its 11,176 keys padded
    /// to seven digits.
    /// </remarks>
    internal static WzImageProperty? FindById(WzPropertyCollection? collection, int id)
    {
        if (collection == null)
            return null;

        string plain = id.ToString(CultureInfo.InvariantCulture);
        WzImageProperty? hit = collection.FindByName(plain);
        if (hit != null)
            return hit;

        for (int width = plain.Length + 1; width <= 10; width++)
        {
            hit = collection.FindByName(plain.PadLeft(width, '0'));
            if (hit != null)
                return hit;
        }
        return null;
    }

    /// <summary>The wrapper node named after the image: Eqp.img -&gt; "Eqp".</summary>
    private static WzImageProperty? Wrapper(WzImage image) =>
        image.WzProperties.FindByName(Stem(image.Name));

    private static string Stem(string imageName) =>
        imageName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

    /// <summary>
    /// The group names under a nested wrapper, or none when the wrapper holds
    /// ids directly. Decided from the archive rather than hardcoded per image,
    /// because both shapes are live in one client.
    /// </summary>
    private static List<string> NestedGroups(WzImageProperty wrapper)
    {
        List<string> groups = new();
        if (wrapper.WzProperties == null)
            return groups;

        foreach (WzImageProperty child in wrapper.WzProperties)
        {
            if (int.TryParse(child.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return new List<string>();   // ids directly under the wrapper
            if (child.WzProperties != null && child.Name != null)
                groups.Add(child.Name);
        }
        return groups;
    }

    private string StringImagePath(OpenFile file, WzImage image) =>
        file.LooseImage != null
            ? file.Id
            : WzPath.Child(_session.RoleRootPath(file, "String"), image.Name);

    private WzImage? FindImage(OpenFile file, string imageName)
    {
        if (file.LooseImage != null)
            return string.Equals(file.LooseImage.Name, imageName, StringComparison.OrdinalIgnoreCase)
                ? file.LooseImage
                : null;

        return _session.RoleRoot(file, "String")?.GetImageByName(imageName);
    }

    private static StringLayout LayoutOf(StringKindSpec spec, string image) =>
        spec.Images.FirstOrDefault(i => string.Equals(i.Image, image, StringComparison.OrdinalIgnoreCase))
            ?.Layout ?? StringLayout.Flat;

    #endregion
}
