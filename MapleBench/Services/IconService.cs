using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// Finds the inventory icon for an item ID in whichever of Item.wz / Character.wz
/// happens to be open, and caches the encoded PNG.
///
/// Everything here is best-effort: a missing archive means a null result and a
/// placeholder in the UI, never an error.
/// </summary>
public sealed class IconService
{
    /// <summary>
    /// Encoded PNGs keyed by item id; a null value marks a confirmed miss so a
    /// missing icon is not re-scanned on every card render.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte[]?> _cache = new();

    /// <summary>
    /// Archive pairs already reported as ambiguous, keyed "used|ignored".
    /// Cleared with the cache so re-opening the files says it again.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _warnedPairs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Icons are small but a full Item.wz sweep would still be tens of MB, so
    /// the cache is bounded and cleared wholesale when it fills.
    /// </summary>
    private const int MaxCacheEntries = 4000;

    private readonly WzSessionService _session;
    private readonly ILogger<IconService> _log;

    public IconService(WzSessionService session, ILogger<IconService> log)
    {
        _session = session;
        _log = log;
    }

    public void Invalidate()
    {
        _cache.Clear();
        _warnedPairs.Clear();
    }

    public byte[]? GetIconPng(int itemId)
    {
        if (itemId <= 0)
            return null;
        if (_cache.TryGetValue(itemId, out byte[]? cached))
            return cached;

        byte[]? png = null;
        try
        {
            lock (_session.Gate)
            {
                WzImageProperty? icon = FindIconProperty(itemId);
                if (icon != null)
                    png = Encode(icon);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Icon lookup failed for {ItemId}", itemId);
        }

        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[itemId] = png;
        return png;
    }

    /// <summary>True when at least one archive that could hold icons is open.</summary>
    public bool HasIconSource =>
        _session.Files.Any(f =>
            f.Name.StartsWith("Item", StringComparison.OrdinalIgnoreCase) ||
            f.Name.StartsWith("Character", StringComparison.OrdinalIgnoreCase));

    private static byte[]? Encode(WzImageProperty icon)
    {
        Bitmap? bitmap = icon switch
        {
            WzCanvasProperty canvas => canvas.GetLinkedWzCanvasBitmap(),
            WzUOLProperty uol when uol.LinkValue is WzCanvasProperty linked => linked.GetLinkedWzCanvasBitmap(),
            _ => null,
        };
        if (bitmap == null)
            return null;

        using (bitmap)
        using (MemoryStream stream = new())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private WzImageProperty? FindIconProperty(int itemId)
    {
        int type = itemId / 1000000;

        // Equips live one .img per item under a slot folder in Character.wz.
        if (type == 1)
        {
            string imageName = itemId.ToString("D8") + ".img";
            WzImage? image = FindImage("Character", imageName);
            return image == null ? null : ReadIcon(image, null);
        }

        // Pets are one .img per pet, with info directly on the image.
        if (itemId / 10000 == 500)
        {
            WzImage? petImage = FindImage("Item", itemId.ToString("D7") + ".img")
                             ?? FindImage("Item", itemId + ".img");
            return petImage == null ? null : ReadIcon(petImage, null);
        }

        // Everything else is grouped: Item.wz/<group>/0204.img/02040000/info/icon
        string groupImage = (itemId / 10000).ToString("D4") + ".img";
        WzImage? grouped = FindImage("Item", groupImage);
        return grouped == null ? null : ReadIcon(grouped, itemId.ToString("D8"));
    }

    /// <summary>
    /// Reads info/icon, following the '_inlink'/'_outlink' indirection that most
    /// modern clients use for icons.
    /// </summary>
    private static WzImageProperty? ReadIcon(WzImage image, string? entryKey)
    {
        WzSessionService.EnsureParsed(image);

        WzImageProperty? scope = entryKey == null
            ? null
            : image.WzProperties.FindByName(entryKey);

        WzPropertyCollection? properties = scope?.WzProperties ?? image.WzProperties;
        if (properties == null)
            return null;

        WzImageProperty? info = properties.FindByName("info");
        WzImageProperty? icon = info?.WzProperties?.FindByName("icon")
                             ?? properties.FindByName("icon");
        return icon;
    }

    /// <summary>
    /// Finds an .img by name inside the open archives that match
    /// <paramref name="archivePrefix"/> — Item.wz, Item001.wz, Character.wz and
    /// so on all qualify.
    /// </summary>
    private WzImage? FindImage(string archivePrefix, string imageName)
    {
        foreach (OpenFile file in CandidateArchives(archivePrefix))
        {
            WzDirectory? root = _session.RoleRoot(file, archivePrefix);
            if (root == null)
                continue;

            WzImage? found = FindImage(root, imageName);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// The archives an icon lookup may read, in the order it reads them.
    ///
    /// Nothing stops two clients' Item.wz being open at once — the session
    /// dedupes by full path, not by name — and matching on the name alone then
    /// merges two unrelated clients' icons into one lookup, with whichever
    /// archive happens to come first winning.  That shows up as an icon quietly
    /// coming from the wrong client, which is close to impossible to explain
    /// from the UI.
    ///
    /// So one client at a time, by a rule that can be stated:
    ///   1. the exact stem wins the tie ("Item.wz" before "Item001.wz"), then
    ///      the archive opened earliest;
    ///   2. that archive's folder is the client for this lookup, and only
    ///      archives from the same folder join it — which keeps genuine
    ///      families (Item.wz + Item001.wz) working;
    ///   3. anything dropped is named, with both paths, so a wrong-looking icon
    ///      has an explanation in the log.
    /// </summary>
    private List<OpenFile> CandidateArchives(string archivePrefix)
    {
        List<OpenFile> matches = _session.SelectRoleSources(archivePrefix)
            .OrderBy(f => IsExactStem(f.Name, archivePrefix) ? 0 : 1)
            .ThenBy(OpenOrder)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count <= 1)
            return matches;

        OpenFile primary = matches[0];
        string? folder = Path.GetDirectoryName(primary.FilePath);
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
            WarnOnce(primary, file);
        }
        return chosen;
    }

    private static bool IsExactStem(string archiveName, string prefix) =>
        Path.GetFileNameWithoutExtension(archiveName).Equals(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Session ids are handed out in order, so "f7" opened before "f8".</summary>
    private static int OpenOrder(OpenFile file) =>
        file.Id.Length > 1 && int.TryParse(file.Id.AsSpan(1), out int order) ? order : int.MaxValue;

    /// <summary>
    /// Says once, per pair, that an archive is being ignored.  Icon lookups run
    /// per rendered card, so warning every time would bury the message it is
    /// trying to deliver.
    /// </summary>
    private void WarnOnce(OpenFile used, OpenFile ignored)
    {
        if (!_warnedPairs.TryAdd($"{used.FilePath}|{ignored.FilePath}", 0))
            return;

        _log.LogWarning(
            "Two open archives could both supply icons: reading from '{Used}' and ignoring '{Ignored}'. " +
            "Close one of them if an icon looks like it came from the wrong client.",
            used.FilePath, ignored.FilePath);
    }

    private static WzImage? FindImage(WzDirectory directory, string imageName)
    {
        WzImage? direct = directory.GetImageByName(imageName);
        if (direct != null)
            return direct;

        foreach (WzDirectory sub in directory.WzDirectories)
        {
            WzImage? found = FindImage(sub, imageName);
            if (found != null)
                return found;
        }
        return null;
    }
}
