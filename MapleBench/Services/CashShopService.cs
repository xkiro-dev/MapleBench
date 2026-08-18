using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// A single Commodity.img entry.  The well-known fields are surfaced by name;
/// anything else the client version happens to store is carried in
/// <see cref="Extra"/> so it round-trips instead of being dropped.
/// </summary>
public sealed class CommodityItemDto
{
    /// <summary>Node name of the entry ("0", "1", ...), not the SN.</summary>
    public string Key { get; set; } = "";
    public string Path { get; set; } = "";

    public int Sn { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; } = 1;
    public int Price { get; set; }
    public int Bonus { get; set; }
    public int Period { get; set; }
    public int Priority { get; set; }
    public int ReqPop { get; set; }
    public int ReqLev { get; set; }
    /// <summary>0 = male, 1 = female, 2 = both.</summary>
    public int Gender { get; set; } = 2;
    public bool OnSale { get; set; }
    public int Class { get; set; }
    public int Limit { get; set; }
    public int PbCash { get; set; }
    public int PbPoint { get; set; }
    public int PbGift { get; set; }

    /// <summary>Derived, not stored: "Equipment > Hat", "Pet", ...</summary>
    public string Category { get; set; } = "";
    public string SubCategory { get; set; } = "";

    /// <summary>Resolved from String.wz when available.</summary>
    public string? ItemName { get; set; }

    public Dictionary<string, string> Extra { get; set; } = new();
}

public sealed class CommodityWriteRequest
{
    public string FileId { get; set; } = "";
    /// <summary>Omit to create a new entry.</summary>
    public string? Key { get; set; }
    public int Sn { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; } = 1;
    public int Price { get; set; }
    public int Bonus { get; set; }
    public int Period { get; set; }
    public int Priority { get; set; }
    public int ReqPop { get; set; }
    public int ReqLev { get; set; }
    public int Gender { get; set; } = 2;
    public bool OnSale { get; set; } = true;
    public int Class { get; set; }
    public int Limit { get; set; }
    public int PbCash { get; set; }
    public int PbPoint { get; set; }
    public int PbGift { get; set; }
    public Dictionary<string, string>? Extra { get; set; }
}

public sealed class CommodityBulkRequest
{
    public string FileId { get; set; } = "";
    public List<CommodityWriteRequest> Items { get; set; } = new();
}

/// <summary>
/// Purpose-built editing for Etc.wz/Commodity.img, the cash shop item table.
///
/// This is a thin projection over the generic tree: every write goes through
/// <see cref="WzEditService"/>, so cash shop edits share the same dirty tracking,
/// undo history and save pipeline as everything else.
/// </summary>
public sealed class CashShopService
{
    /// <summary>Fields the form owns; everything else is passed through as Extra.</summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "SN", "ItemId", "Count", "Price", "Bonus", "Period", "Priority",
        "ReqPOP", "ReqLEV", "Gender", "OnSale", "Class", "Limit",
        "PbCash", "PbPoint", "PbGift",
    };

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly UndoService _undo;

    public CashShopService(WzSessionService session, WzEditService edit, StringPoolService strings, UndoService undo)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _undo = undo;
    }

    /// <summary>
    /// The single source Cash Shop mode should use. An Etc archive outranks a
    /// separately opened Commodity.img so opening both representations cannot
    /// make the mode depend on open order. A loose image remains a valid source
    /// when it is the only representation available.
    /// </summary>
    public OpenFile? Source()
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Etc"))
            {
                if (FindCommodity(file) != null)
                    return file;
            }

            return _session.Files.FirstOrDefault(file =>
                file.LooseImage != null
                && file.LooseImage.Name.Equals("Commodity.img", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Finds Commodity.img in an open archive.  Accepts either Etc.wz itself or
    /// a loose Commodity.img opened on its own.
    /// </summary>
    public (WzImage Image, string Path) LocateCommodity(string fileId)
    {
        OpenFile file = _session.GetFile(fileId);

        if (file.LooseImage != null)
        {
            if (!file.LooseImage.Name.StartsWith("Commodity", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{file.LooseImage.Name}' is not Commodity.img.");
            WzSessionService.EnsureParsed(file.LooseImage);
            return (file.LooseImage, file.Id);
        }

        WzDirectory root = _session.RoleRoot(file, "Etc")
            ?? throw new InvalidOperationException(
                $"'{file.Name}' does not contain an Etc archive or IMG folder.");

        WzImage? image = root.GetImageByName("Commodity.img");
        string rootPath = _session.RoleRootPath(file, "Etc");
        string path = WzPath.Child(rootPath, "Commodity.img");
        if (image == null)
        {
            // Some layouts nest it a level deeper.
            foreach (WzDirectory sub in root.WzDirectories)
            {
                WzImage? candidate = sub.GetImageByName("Commodity.img");
                if (candidate != null)
                {
                    image = candidate;
                    path = WzPath.Child(WzPath.Child(rootPath, sub.Name), "Commodity.img");
                    break;
                }
            }
        }

        if (image == null)
            throw new InvalidOperationException(
                $"Commodity.img was not found in '{file.Name}'. Open Etc.wz, or open Commodity.img directly.");

        WzSessionService.EnsureParsed(image);
        return (image, path);
    }

    private WzImage? FindCommodity(OpenFile file)
    {
        if (file.LooseImage != null)
            return file.LooseImage.Name.Equals("Commodity.img", StringComparison.OrdinalIgnoreCase)
                ? file.LooseImage
                : null;

        WzDirectory? root = _session.RoleRoot(file, "Etc");
        if (root == null)
            return null;
        WzImage? direct = root.GetImageByName("Commodity.img");
        if (direct != null)
            return direct;
        foreach (WzDirectory sub in root.WzDirectories)
        {
            WzImage? nested = sub.GetImageByName("Commodity.img");
            if (nested != null)
                return nested;
        }
        return null;
    }

    public List<CommodityItemDto> List(string fileId, bool resolveNames)
    {
        lock (_session.Gate)
        {
            (WzImage image, string basePath) = LocateCommodity(fileId);
            List<CommodityItemDto> items = new(image.WzProperties.Count);

            foreach (WzImageProperty entry in image.WzProperties)
            {
                if (entry.WzProperties == null)
                    continue;
                items.Add(ReadEntry(entry, WzPath.Child(basePath, entry.Name ?? ""), resolveNames));
            }
            return items;
        }
    }

    private CommodityItemDto ReadEntry(WzImageProperty entry, string path, bool resolveNames)
    {
        CommodityItemDto dto = new()
        {
            Key = entry.Name ?? "",
            Path = path,
        };

        foreach (WzImageProperty field in entry.WzProperties!)
        {
            string name = field.Name ?? "";
            switch (name.ToLowerInvariant())
            {
                case "sn": dto.Sn = AsInt(field); break;
                case "itemid": dto.ItemId = AsInt(field); break;
                case "count": dto.Count = AsInt(field); break;
                case "price": dto.Price = AsInt(field); break;
                case "bonus": dto.Bonus = AsInt(field); break;
                case "period": dto.Period = AsInt(field); break;
                case "priority": dto.Priority = AsInt(field); break;
                case "reqpop": dto.ReqPop = AsInt(field); break;
                case "reqlev": dto.ReqLev = AsInt(field); break;
                case "gender": dto.Gender = AsInt(field); break;
                case "onsale": dto.OnSale = AsInt(field) != 0; break;
                case "class": dto.Class = AsInt(field); break;
                case "limit": dto.Limit = AsInt(field); break;
                case "pbcash": dto.PbCash = AsInt(field); break;
                case "pbpoint": dto.PbPoint = AsInt(field); break;
                case "pbgift": dto.PbGift = AsInt(field); break;
                default:
                    if (!KnownFields.Contains(name))
                        dto.Extra[name] = field.WzValue?.ToString() ?? "";
                    break;
            }
        }

        (dto.Category, dto.SubCategory) = ItemCategories.Classify(dto.ItemId);
        if (resolveNames)
            dto.ItemName = _strings.GetItemName(dto.ItemId);
        return dto;
    }

    private static int AsInt(WzImageProperty property)
    {
        try { return property.GetInt(); }
        catch { return 0; }
    }

    /// <summary>
    /// Lowest unused SN at or above the highest existing one, so generated
    /// serials never collide with an entry the user already has.
    /// </summary>
    public int NextSerial(string fileId)
    {
        lock (_session.Gate)
        {
            List<CommodityItemDto> items = List(fileId, resolveNames: false);
            if (items.Count == 0)
                return 10000000;
            return items.Max(i => i.Sn) + 1;
        }
    }

    public List<int> FindDuplicateSerials(string fileId)
    {
        lock (_session.Gate)
        {
            return List(fileId, resolveNames: false)
                .GroupBy(i => i.Sn)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(sn => sn)
                .ToList();
        }
    }

    public CommodityItemDto Upsert(CommodityWriteRequest request)
    {
        lock (_session.Gate)
        {
            (WzImage image, string basePath) = LocateCommodity(request.FileId);
            return Upsert(new CommodityTable(image, basePath), request);
        }
    }

    public List<CommodityItemDto> BulkUpsert(CommodityBulkRequest request)
    {
        lock (_session.Gate)
        {
            // The table is located, and the next free entry key worked out, once
            // for the whole batch.  Both were redone per row, and the key search
            // is a pass over every entry: on a 15,000 entry Commodity.img a 500
            // row paste spent 7.5 million comparisons deciding what to call the
            // rows before writing a single field.
            (WzImage image, string basePath) = LocateCommodity(request.FileId);
            CommodityTable table = new(image, basePath);

            using IDisposable batch = _undo.Batch($"Bulk add {request.Items.Count} cash shop items");
            List<CommodityItemDto> results = new(request.Items.Count);
            foreach (CommodityWriteRequest item in request.Items)
            {
                item.FileId = request.FileId;
                // Skip blank spreadsheet rows rather than writing empty entries.
                if (item.ItemId == 0 && item.Sn == 0)
                    continue;
                results.Add(Upsert(table, item));
            }
            return results;
        }
    }

    /// <summary>
    /// Writes one entry into an already-resolved table.  Callers hold
    /// <see cref="WzSessionService.Gate"/>.
    /// </summary>
    private CommodityItemDto Upsert(CommodityTable table, CommodityWriteRequest request)
    {
        // One item is ~16 property writes; without batching a single bulk
        // add would flush the whole undo history.
        using IDisposable batch = _undo.Batch(
            request.Key == null ? $"Add cash shop item {request.ItemId}" : $"Edit cash shop item {request.ItemId}");

        string key = request.Key ?? table.TakeKey();
        table.Reserve(key);
        string entryPath = WzPath.Child(table.BasePath, key);

        WzImageProperty? entry = table.Image.WzProperties.FindByName(key);
        if (entry == null)
        {
            _edit.Add(new AddNodeRequest
            {
                Path = table.BasePath,
                Type = "SubProperty",
                Name = key,
            });
            entry = table.Image.WzProperties.FindByName(key)
                ?? throw new InvalidOperationException($"Could not create entry '{key}'.");
        }

        HashSet<string> schema = table.Schema;
        foreach ((string name, int value) in EnumerateFields(request))
            WriteField(entry, entryPath, name, value, schema);

        if (request.Extra != null)
        {
            foreach ((string name, string value) in request.Extra)
            {
                if (KnownFields.Contains(name))
                    continue;
                // Client versions carry their own extra fields -- v232 stores
                // 'gameWorld' as a slash-separated string, for instance -- so
                // the stored type follows the value rather than assuming int.
                WriteExtraField(entry, entryPath, name, value);
            }
        }

        return ReadEntry(entry, entryPath, resolveNames: true);
    }

    /// <summary>
    /// Commodity.img plus the entry key handed out next, so a batch resolves the
    /// table once instead of once per row.
    /// </summary>
    private sealed class CommodityTable
    {
        private int _nextKey = -1;

        public CommodityTable(WzImage image, string basePath)
        {
            Image = image;
            BasePath = basePath;
        }

        public WzImage Image { get; }
        public string BasePath { get; }

        /// <summary>
        /// The field names this Commodity.img's own entries use.
        ///
        /// Different client versions store different columns — a v83 entry has
        /// five, a v232 entry has sixteen — and the writer used to create all
        /// sixteen unconditionally, so editing one v83 row grew eleven columns
        /// the archive had never had. The table describes its own shape; a
        /// write follows it rather than imposing one.
        ///
        /// Empty when the table has no entries yet, which is the one case where
        /// there is nothing to preserve and everything is created.
        /// </summary>
        public HashSet<string> Schema
        {
            get
            {
                if (_schema != null)
                    return _schema;

                _schema = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WzImageProperty entry in Image.WzProperties)
                {
                    if (entry.WzProperties == null || entry.WzProperties.Count == 0)
                        continue;
                    // The first populated entry is the sample. Later entries can
                    // legitimately differ, and unioning them all would let one
                    // stray row reintroduce the whole v232 column set.
                    // Not named 'field': inside a property accessor that is a
                    // contextual keyword in C# 14 and binds to the backing field.
                    foreach (WzImageProperty column in entry.WzProperties)
                    {
                        if (column.Name != null)
                            _schema.Add(column.Name);
                    }
                    break;
                }
                return _schema;
            }
        }

        private HashSet<string>? _schema;

        public string TakeKey()
        {
            if (_nextKey < 0)
                _nextKey = NextEntryKey(Image);
            return (_nextKey++).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Keeps generated keys clear of one the caller supplied, so a batch that
        /// mixes explicit keys with generated ones cannot land two rows on the
        /// same entry.
        /// </summary>
        public void Reserve(string key)
        {
            if (_nextKey >= 0 && int.TryParse(key, out int number) && number >= _nextKey)
                _nextKey = number + 1;
        }
    }

    public int Delete(string fileId, IEnumerable<string> keys)
    {
        lock (_session.Gate)
        {
            (_, string basePath) = LocateCommodity(fileId);
            return _edit.Delete(keys.Select(k => WzPath.Child(basePath, k)));
        }
    }

    /// <summary>
    /// Clones an entry and gives the copy a fresh SN — the "make one more like
    /// this" action that otherwise takes a dozen manual field edits.
    /// </summary>
    public CommodityItemDto Clone(string fileId, string key, int? newItemId)
    {
        lock (_session.Gate)
        {
            (WzImage image, string basePath) = LocateCommodity(fileId);
            WzImageProperty source = image.WzProperties.FindByName(key)
                ?? throw new KeyNotFoundException($"No cash shop entry '{key}'.");

            CommodityItemDto template = ReadEntry(source, WzPath.Child(basePath, key), false);
            CommodityWriteRequest request = new()
            {
                FileId = fileId,
                Sn = NextSerial(fileId),
                ItemId = newItemId ?? template.ItemId,
                Count = template.Count,
                Price = template.Price,
                Bonus = template.Bonus,
                Period = template.Period,
                Priority = template.Priority,
                ReqPop = template.ReqPop,
                ReqLev = template.ReqLev,
                Gender = template.Gender,
                OnSale = template.OnSale,
                Class = template.Class,
                Limit = template.Limit,
                PbCash = template.PbCash,
                PbPoint = template.PbPoint,
                PbGift = template.PbGift,
                Extra = template.Extra,
            };
            return Upsert(request);
        }
    }

    private static IEnumerable<(string Name, int Value)> EnumerateFields(CommodityWriteRequest r)
    {
        yield return ("SN", r.Sn);
        yield return ("ItemId", r.ItemId);
        yield return ("Count", r.Count);
        yield return ("Price", r.Price);
        yield return ("Bonus", r.Bonus);
        yield return ("Period", r.Period);
        yield return ("Priority", r.Priority);
        yield return ("ReqPOP", r.ReqPop);
        yield return ("ReqLEV", r.ReqLev);
        yield return ("Gender", r.Gender);
        yield return ("OnSale", r.OnSale ? 1 : 0);
        yield return ("Class", r.Class);
        yield return ("Limit", r.Limit);
        yield return ("PbCash", r.PbCash);
        yield return ("PbPoint", r.PbPoint);
        yield return ("PbGift", r.PbGift);
    }

    /// <summary>
    /// Sets one int field, creating it if the entry doesn't have it yet.  Field
    /// names are matched case-insensitively but created with the client's
    /// canonical casing.
    /// </summary>
    private void WriteField(WzImageProperty entry, string entryPath, string name, int value,
        HashSet<string> schema)
    {
        WzImageProperty? field = entry.WzProperties?.FindByName(name);
        if (field == null)
        {
            // A column this archive does not use is not created. Otherwise
            // editing the price of a v83 entry silently added Bonus, Period,
            // Priority, ReqPOP, ReqLEV, Gender, Class, Limit, PbCash, PbPoint
            // and PbGift to it — eleven fields the user never touched, on a
            // file the server may parse strictly.
            if (schema.Count > 0 && !schema.Contains(name))
                return;

            _edit.Add(new AddNodeRequest
            {
                Path = entryPath,
                Type = "Int",
                Name = name,
                Value = value.ToString(),
            });
            return;
        }
        _edit.SetValue(WzPath.Child(entryPath, field.Name ?? name), value.ToString());
    }

    /// <summary>
    /// Writes a pass-through field, keeping the type it already has and
    /// otherwise inferring Int for whole numbers and String for anything else.
    /// </summary>
    private void WriteExtraField(WzImageProperty entry, string entryPath, string name, string value)
    {
        WzImageProperty? field = entry.WzProperties?.FindByName(name);
        if (field != null)
        {
            _edit.SetValue(WzPath.Child(entryPath, field.Name ?? name), value);
            return;
        }

        string type = int.TryParse(value, out _) ? "Int" : "String";
        _edit.Add(new AddNodeRequest { Path = entryPath, Type = type, Name = name, Value = value });
    }

    /// <summary>
    /// Commodity entries are keyed by contiguous numeric names; pick the next
    /// free integer rather than count, which breaks if entries were deleted.
    /// </summary>
    private static int NextEntryKey(WzImage image)
    {
        int highest = -1;
        foreach (WzImageProperty entry in image.WzProperties)
        {
            if (int.TryParse(entry.Name, out int number) && number > highest)
                highest = number;
        }
        return highest + 1;
    }
}
