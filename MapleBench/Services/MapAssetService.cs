using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// The map asset library: every tile, object and background a map can be built
/// out of.
///
/// A v232 client splits these from the maps themselves, and then splits the
/// library itself across archives. Measured on <c>C:\MapleStory\232</c>:
///
/// <code>
///   Tile   265 sets   Map.wz
///   Obj    336 sets   Map.wz (211) + Map2.wz (125)
///   Back   466 sets   Map001.wz (318) + Map2.wz (148)
/// </code>
///
/// <b>That split is why this service unions and does not pick.</b> The version
/// of <c>FindLibrary</c> this replaced returned the first archive with a
/// matching root directory and stopped, so <c>sets?kind=Obj</c> answered 211 of
/// 336 and <c>kind=Back</c> answered 318 of 466 — the whole of Map2.wz was
/// invisible, including <c>PL_Beautiroyd</c>, the second most-placed object set
/// in the game at 22,722 placements. Nothing said so: a short listing looks
/// exactly like a small client.
///
/// Note that this union is deliberately <i>not</i> the same set of files as
/// <see cref="ArchiveFamilyService"/>'s family. That detector takes three-digit
/// parts only — <c>Map.wz + Map001.wz</c> — and rightly refuses to mount
/// <c>Map2.wz</c>, which is a different archive rather than a numbered part of
/// this one. But the client reads its scenery out of all of them, so the unit
/// here is "every open archive that has a <c>Back/</c>", not "every member of a
/// detected family".
///
/// The shapes differ and that is not incidental:
///   Tile: <c>Tile/&lt;set&gt;.img/&lt;variant&gt;/&lt;index&gt;</c>
///         variant is the tile's role — "bsc" (basic), "enH0"/"enH1" (horizontal
///         edge), "enV0"/"enV1" (vertical edge), "edU"/"edD" (up/down edge),
///         "slLU"/"slRU" (slopes). A map that mixes variants from different sets
///         looks wrong, which is why the browser groups by set first.
///   Obj:  <c>Obj/&lt;set&gt;.img/&lt;category&gt;/&lt;subcategory&gt;/&lt;index&gt;</c>
///         one level deeper, and the leaf is usually an animation.
///   Back: <c>Back/&lt;set&gt;.img/{back,ani}/&lt;index&gt;</c>
///         two branches, and which one a placement draws from is decided by its
///         own <c>ani</c> flag rather than by the set.
/// </summary>
public sealed class MapAssetService
{
    private readonly WzSessionService _session;

    /// <summary>
    /// The library kinds a map is built out of.
    ///
    /// <see cref="IsAvailable"/> asks about all of them. It used to ask about
    /// Tile and Obj only, so a session holding Map001.wz alone — 318 background
    /// sets — reported the browser unavailable.
    /// </summary>
    public static readonly string[] Kinds = { "Tile", "Obj", "Back" };

    /// <summary>Bounded for the same reason as MobService's list cache.</summary>
    private const int MaxCachedKinds = 8;

    /// <summary>Set listings, keyed by kind and validated against the generation.</summary>
    private readonly Dictionary<string, (int Generation, List<MapAssetSetDto> Sets)> _setCache =
        new(StringComparer.Ordinal);

    public MapAssetService(WzSessionService session)
    {
        _session = session;
    }

    public bool IsAvailable
    {
        get
        {
            lock (_session.Gate)
                return Kinds.Any(k => FindLibraries(k).Count > 0);
        }
    }

    /// <summary>
    /// What the browser can offer, per kind, and out of which archives.
    ///
    /// A single "available" bit was what let the Map2.wz gap hide: 211 sets and
    /// 336 sets are both "available". Reporting the count and the contributing
    /// archives makes a truncated library visible without anyone having to know
    /// the real number in advance.
    /// </summary>
    public MapAssetCapabilitiesDto Capabilities()
    {
        MapAssetCapabilitiesDto dto = new();
        foreach (string kind in Kinds)
        {
            List<MapAssetSetDto> sets = Sets(kind);
            dto.Kinds.Add(new MapAssetKindDto
            {
                Kind = kind,
                Sets = sets.Count,
                Archives = sets.Select(s => s.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                Shadowed = sets.Count(s => s.Shadowed),
            });
        }
        dto.Available = dto.Kinds.Any(k => k.Sets > 0);
        return dto;
    }

    /// <summary>
    /// The sets available for a kind ("Tile", "Obj" or "Back"), unioned across
    /// every open archive that carries that library and deduped by set name.
    ///
    /// <b>Dedupe, not concatenate.</b> A set is named, not owned — a map says
    /// <c>oS = "acc1"</c> and the client finds whichever <c>Obj/acc1.img</c> it
    /// mounted. So two archives offering that name are one row, not two, and the
    /// row's <see cref="MapAssetSetDto.Path"/> is the copy that wins. Which copy
    /// wins is decided by mount order and mount order is not recorded anywhere in
    /// the archives, so it is reconstructed here the same way
    /// <see cref="ArchiveFamilyService"/> reconstructs it: base first, numbered
    /// parts ascending, everything else last.
    ///
    /// The losers are not dropped silently — they are listed in
    /// <see cref="MapAssetSetDto.Sources"/>, and flagged
    /// <see cref="MapAssetSetDto.Shadowed"/> when they come from the same client,
    /// which is the case where editing the visible copy may be editing the one
    /// the game does not load.
    /// </summary>
    public List<MapAssetSetDto> Sets(string kind)
    {
        string key = $"{kind}";

        lock (_session.Gate)
        {
            if (_setCache.TryGetValue(key, out (int Generation, List<MapAssetSetDto> Sets) cached)
                && cached.Generation == _session.Generation)
                return cached.Sets;

            // Insertion-ordered so the winner stays first when a name repeats.
            Dictionary<string, MapAssetSetDto> byName = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> folderOf = new(StringComparer.OrdinalIgnoreCase);
            List<MapAssetSetDto> sets = new();

            foreach (Library library in FindLibraries(kind))
            {
                foreach (WzImage image in library.Directory.WzImages)
                {
                    string name = TrimImg(image.Name);
                    string path = WzPath.Child(library.Path, image.Name);

                    if (byName.TryGetValue(name, out MapAssetSetDto? winner))
                    {
                        // Second and later copies of a name. The row already
                        // exists; all that is added is the truth about where else
                        // it lives.
                        (winner.Sources ??= new List<string> { winner.Source }).Add(library.File.Name);
                        if (string.Equals(folderOf[name], FolderOf(library.File), StringComparison.OrdinalIgnoreCase))
                            winner.Shadowed = true;
                        continue;
                    }

                    MapAssetSetDto set = new()
                    {
                        Name = name,
                        Path = path,
                        // Deliberately not parsed here. 265 sets x parse is the
                        // difference between a browser that opens instantly and one
                        // that stalls; the count and the preview are filled in when
                        // the user opens a set.
                        Kind = kind,
                        Source = library.File.Name,
                    };
                    byName[name] = set;
                    folderOf[name] = FolderOf(library.File);
                    sets.Add(set);
                }
            }

            sets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (_setCache.Count >= MaxCachedKinds)
                _setCache.Clear();
            _setCache[key] = (_session.Generation, sets);
            return sets;
        }
    }

    /// <summary>
    /// Every placeable entry in one set, flattened.
    ///
    /// Tiles are two levels deep and objects three, but the browser wants one
    /// grid, so the group path is carried on each entry instead of nesting the
    /// result. The group is what a user picks by — "bsc" vs "enH0" is the
    /// difference between a platform's middle and its edge.
    /// </summary>
    public MapAssetEntriesDto Entries(string setPath, int limit)
    {
        MapAssetEntriesDto result = new() { Path = setPath };

        lock (_session.Gate)
        {
            if (_session.Resolve(setPath) is not WzImage image)
                throw new InvalidOperationException($"'{setPath}' is not an asset set.");

            WzSessionService.EnsureParsed(image);
            result.Name = TrimImg(image.Name);

            foreach (WzImageProperty group in image.WzProperties)
            {
                // "info" is metadata, not art.
                if (string.Equals(group.Name, "info", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (group.WzProperties == null)
                    continue;

                string groupPath = WzPath.Child(setPath, group.Name);

                foreach (WzImageProperty entry in group.WzProperties)
                {
                    if (result.Entries.Count >= limit)
                    {
                        result.Truncated = true;
                        return result;
                    }

                    string entryPath = WzPath.Child(groupPath, entry.Name);

                    // An object entry nests one level further, and its leaf is a
                    // frame list. Descending to frame 0 is what makes a thumbnail
                    // possible; anything deeper is animation the browser does not
                    // need to show.
                    if (LooksLikeContainer(entry))
                    {
                        foreach (WzImageProperty leaf in entry.WzProperties!)
                        {
                            if (result.Entries.Count >= limit)
                            {
                                result.Truncated = true;
                                return result;
                            }
                            result.Entries.Add(new MapAssetEntryDto
                            {
                                Group = $"{group.Name}/{entry.Name}",
                                Name = leaf.Name ?? "",
                                Path = WzPath.Child(entryPath, leaf.Name ?? ""),
                            });
                        }
                    }
                    else
                    {
                        result.Entries.Add(new MapAssetEntryDto
                        {
                            Group = group.Name ?? "",
                            Name = entry.Name ?? "",
                            Path = entryPath,
                        });
                    }
                }
            }
        }
        return result;
    }

    #region Plumbing

    /// <summary>One archive's contribution to a library.</summary>
    private sealed record Library(OpenFile File, WzDirectory Directory, string Path);

    /// <summary>
    /// <b>Every</b> Tile/, Obj/ or Back/ directory the session holds, in mount
    /// order — not the first one found.
    ///
    /// Checked at the root of every open archive rather than assuming Map.wz,
    /// because clients disagree about which sibling carries the library and a
    /// v232 client disagrees with itself: Obj is in Map.wz and Map2.wz, Back is
    /// in Map001.wz and Map2.wz.
    ///
    /// Returning after the first match — what this did — is not a partial answer,
    /// it is a wrong one that cannot be told apart from a right one, because the
    /// caller has nothing to compare the count against.
    /// </summary>
    private List<Library> FindLibraries(string kind)
    {
        List<Library> found = new();

        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            WzDirectory? root = _session.RoleRoot(file, "Map");
            if (root == null)
                continue;

            // Every match, not the first: an archive is free to hold the
            // directory more than once, and a listing that silently dropped the
            // second would be the same defect one level down.
            foreach (WzDirectory match in root.WzDirectories)
            {
                if (string.Equals(match.Name, kind, StringComparison.OrdinalIgnoreCase))
                    found.Add(new Library(file, match,
                        WzPath.Child(_session.RoleRootPath(file, "Map"), match.Name)));
            }
        }
        return found;
    }

    /// <summary>
    /// Session entries in the order a client mounts them: base archive first,
    /// three-digit parts ascending, everything else last.
    ///
    /// This decides which copy of a duplicated set name the browser shows, so it
    /// has to be deterministic and it has to be the client's order rather than
    /// whatever order the files were opened in. <c>Map2.wz</c> ranks last, which
    /// is the same call <see cref="ArchiveFamilyService"/> makes when it refuses
    /// to treat it as a numbered part of Map.
    /// </summary>
    private static IEnumerable<OpenFile> Ordered(IEnumerable<OpenFile> files)
        => files
            .Select((f, index) => (File: f, Index: index))
            .OrderBy(x => MountRank(x.File))
            .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            // Two clients open at once put two "Map.wz" here; session order is
            // the only thing left that distinguishes them, and it is stable.
            .ThenBy(x => x.Index)
            .Select(x => x.File);

    private static int MountRank(OpenFile file)
    {
        string stem = file.Name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)
            ? file.Name[..^3]
            : file.Name;
        string family = WzSessionService.StripArchiveSuffix(stem);

        if (stem.Equals(family, StringComparison.OrdinalIgnoreCase))
            return -1;

        string digits = stem[family.Length..];
        return digits.Length == 3 && int.TryParse(digits, out int number) ? number : int.MaxValue;
    }

    /// <summary>
    /// The client folder an archive was loaded from, which is how two archives
    /// of one client are told apart from one archive of each of two clients.
    /// Empty when the entry has no path on disk, which groups those together
    /// rather than declaring them all distinct.
    /// </summary>
    private static string FolderOf(OpenFile file)
    {
        try
        {
            return Path.GetDirectoryName(file.FilePath) ?? "";
        }
        catch (ArgumentException)
        {
            // A synthetic entry can carry a path that is not one.
            return "";
        }
    }

    /// <summary>
    /// Whether a node is a folder of frames rather than a frame itself. A canvas
    /// with pixels is placeable; a node whose children are numbered is a level of
    /// nesting to descend through.
    /// </summary>
    private static bool LooksLikeContainer(WzImageProperty entry)
    {
        if (entry.WzProperties == null || entry.WzProperties.Count == 0)
            return false;
        // A canvas carries child properties too (origin, _inlink, z), so the test
        // is whether the *node itself* is drawable, not whether it has children.
        return entry.PropertyType is not WzPropertyType.Canvas;
    }

    private static string TrimImg(string name) =>
        name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    #endregion
}
