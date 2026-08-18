using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

#region DTOs

/// <summary>One physical archive inside a merged family, in mount order.</summary>
public sealed class FamilyMemberDto
{
    /// <summary>Session file id — the id every real node path under this member starts with.</summary>
    public string FileId { get; set; } = "";
    /// <summary>The file as it exists on disk: "Skill001.wz".</summary>
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long Bytes { get; set; }
    public bool ReadOnly { get; set; }
    public bool Dirty { get; set; }
    /// <summary>Position in the mount order this family currently uses. 0 wins ties.</summary>
    public int Order { get; set; }
}

/// <summary>A family that is open and being browsed as one tree.</summary>
public sealed class ArchiveFamilyDto
{
    /// <summary>Family id — the first segment of every merged path, e.g. "g1".</summary>
    public string Id { get; set; } = "";
    /// <summary>The family stem: "Skill".</summary>
    public string Name { get; set; } = "";
    /// <summary>"classic" (Skill.wz + Skill001.wz) or "split-data" (Data\Skill\Skill_000.wz).</summary>
    public string Kind { get; set; } = "classic";
    public string Folder { get; set; } = "";
    public List<FamilyMemberDto> Members { get; set; } = new();

    /// <summary>
    /// Which copy of a duplicated image the merged tree shows. False — the
    /// default — means the base archive wins, which is the order a client mounts
    /// them in. See <see cref="ArchiveFamilyService.SetPrecedence"/> for why this
    /// is a switch rather than a constant.
    /// </summary>
    public bool LastWins { get; set; }

    /// <summary>How many image names exist in more than one member. -1 until scanned.</summary>
    public int ShadowedImages { get; set; } = -1;

    public string Summary { get; set; } = "";
}

/// <summary>A family found on disk, which may or may not be open yet.</summary>
public sealed class FamilyCandidateDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "classic";
    public string Folder { get; set; } = "";
    /// <summary>Member file names in mount order — "Skill.wz", "Skill001.wz", ...</summary>
    public List<string> Files { get; set; } = new();
    public long Bytes { get; set; }
    /// <summary>Family id when this is already merged in the session; null otherwise.</summary>
    public string? OpenAs { get; set; }
    /// <summary>How many members are already open on their own.</summary>
    public int MembersOpen { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>One physical copy of a shadowed image.</summary>
public sealed class ShadowCopyDto
{
    public string File { get; set; } = "";
    public string FileId { get; set; } = "";
    /// <summary>Real session path of this copy — what an edit or a save would touch.</summary>
    public string NodePath { get; set; } = "";
    /// <summary>Size of the image's block on disk. Equal sizes do not prove equal bytes.</summary>
    public int Bytes { get; set; }
    /// <summary>True for the copy the merged tree currently shows.</summary>
    public bool Wins { get; set; }
    /// <summary>
    /// First 12 hex characters of this copy's content hash, or null when the
    /// copies were not compared by content. Null is "not asked", never "same".
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// The image itself, for the content comparison. Internal so it stays out of
    /// the JSON: this is a handle the report is built from, not part of it.
    /// </summary>
    internal WzImage? Node { get; set; }
}

/// <summary>One image name that exists in more than one member of a family.</summary>
public sealed class ShadowEntryDto
{
    /// <summary>Logical path inside the archive, root excluded: "Effect/40000.img".</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ShadowCopyDto> Copies { get; set; } = new();
    /// <summary>True when the copies are known to be different images.</summary>
    public bool Differs { get; set; }

    /// <summary>
    /// What was actually established about these copies, which is not the same
    /// question as <see cref="Differs"/> and is the reason this field exists.
    ///
    /// <c>false</c> used to carry two meanings -- "these are the same image" and
    /// "these are the same SIZE and nobody read a byte of either" -- and the
    /// report sorted the second below the fold as the reassuring half. Block size
    /// is a pre-filter, not an identity: two images can agree on it and hold
    /// different pixels, and a client mounting the family shows one of them with
    /// no way to tell which.
    ///
    /// One of:
    ///   <c>different-size</c>    -- certainly different, established for free.
    ///   <c>different-content</c> -- certainly different, established by hashing.
    ///   <c>identical</c>         -- certainly the same, established by hashing.
    ///   <c>not-compared</c>      -- same size; nothing was read. NOT a clean bill.
    /// </summary>
    public string Verdict { get; set; } = "not-compared";
}

public sealed class ShadowReportDto
{
    public string FamilyId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Shadowed names found, before <c>limit</c> was applied.</summary>
    public int Total { get; set; }
    public int Differing { get; set; }
    /// <summary>
    /// Shadowed names whose copies share a block size and were never compared by
    /// content. Reported separately from <see cref="Differing"/> so that "nothing
    /// is wrong" and "nothing was examined" cannot be read as the same answer.
    /// </summary>
    public int NotCompared { get; set; }
    /// <summary>True when copies were hashed rather than only sized.</summary>
    public bool ComparedByContent { get; set; }
    public bool Truncated { get; set; }
    public int ImagesScanned { get; set; }
    public List<ShadowEntryDto> Entries { get; set; } = new();
    public string Summary { get; set; } = "";
}

#endregion

/// <summary>
/// Browses a split archive family — <c>Skill.wz + Skill001.wz + Skill002.wz +
/// Skill003.wz</c> — as one tree, without merging anything on disk or in
/// MapleLib.
///
/// <b>The merge is a view, not a data structure.</b> Every member stays a normal
/// session entry with its own <see cref="OpenFile"/>, its own dirty flags and its
/// own save path; this service only answers "what are the children of this
/// logical folder" by asking each member in turn and unioning the answers. Two
/// consequences fall out of that and both are the point:
///
/// <list type="bullet">
///   <item>Every leaf the merged tree hands back carries its member's <i>real</i>
///   session path, so an edit, a render, a search hit or a save reaches exactly
///   the physical file the node came from. Nothing downstream of here needs to
///   know a family exists.</item>
///   <item>Nothing is copied, so opening a family costs what opening the same
///   four archives costs today, and closing one leaves the archives open.</item>
/// </list>
///
/// Only merged <i>directories</i> get a synthetic path (<c>g1/Effect</c>),
/// because a directory is the only node whose contents come from several files
/// at once. The moment the walk reaches an image it is one file's image and it
/// is addressed as such.
///
/// The reason this exists rather than a "just concatenate them" helper is
/// <see cref="Shadows"/>. Members are supposed to hold disjoint slices of one
/// namespace and in practice they do not: in the v232 client measured here
/// <c>40000.img</c> is in both Skill.wz and Skill003.wz with different contents,
/// so which one a tool shows — and which one the game loads — is decided by
/// mount order alone, silently. A merged view that hid that would be worse than
/// four separate trees.
/// </summary>
public sealed class ArchiveFamilyService
{
    private readonly WzSessionService _session;
    private readonly ClientImportService _import;
    private readonly ILogger<ArchiveFamilyService> _log;

    private readonly Dictionary<string, Family> _families = new(StringComparer.Ordinal);
    private int _nextId = 1;

    public ArchiveFamilyService(WzSessionService session, ClientImportService import,
                                ILogger<ArchiveFamilyService> log)
    {
        _session = session;
        _import = import;
        _log = log;
    }

    /// <summary>
    /// Family ids start with this and session file ids start with 'f', so one
    /// character tells a merged path from a real one — which is what lets every
    /// existing endpoint keep taking a single <c>path</c> parameter.
    /// </summary>
    public const string IdPrefix = "g";

    private sealed class Family
    {
        public string Id = "";
        public string Name = "";
        public string Kind = "classic";
        public string Folder = "";
        /// <summary>Session file ids, in the order the client mounts them.</summary>
        public List<string> Members = new();
        public bool LastWins;
        public int ShadowedImages = -1;
    }

    #region Detection

    /// <summary>
    /// A numbered part of a classic split archive: three digits, always.
    ///
    /// The three is load-bearing and is not a style choice. <c>Map.wz</c>,
    /// <c>Map001.wz</c> and <c>Map002.wz</c> are one archive in three files;
    /// <c>Map2.wz</c> sitting beside them is a different archive entirely, and
    /// the same is true of <c>Mob2.wz</c> and <c>Sound2.wz</c>. A rule that
    /// stripped any trailing digits — which is what the file picker's grouping
    /// does, harmlessly, because it only draws a heading — would mount Map2 into
    /// Map here and produce a tree that does not exist in any client.
    /// </summary>
    private static readonly Regex NumberedPart =
        new(@"^(?<stem>.+?)(?<num>[0-9]{3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every family a folder holds, both shapes, whether or not anything is open.
    ///
    /// Deliberately tolerant about what it is pointed at: a client folder, that
    /// folder's <c>Data</c> directory, or one archive's own directory inside it
    /// all answer, because those are the three things a user drags onto the app
    /// and none of them is more correct than the others.
    /// </summary>
    public List<FamilyCandidateDto> Detect(string folder)
    {
        List<FamilyCandidateDto> found = new();
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (ArgumentException) { return found; }
        catch (NotSupportedException) { return found; }

        if (Directory.Exists(full))
            found.AddRange(DetectClassic(full));

        // The other shape. Detection is a scan of .ini files and costs nothing,
        // and a folder can legitimately be both — a client mid-migration has
        // classic archives beside a Data directory.
        try
        {
            ClientLayoutDto layout = _import.Detect(full);
            if (layout.Kind == "split" && layout.DataPath != null)
            {
                foreach (SplitArchiveDto archive in layout.Archives)
                {
                    if (!archive.Supported)
                        continue;

                    List<string> files = new();
                    for (int i = 0; i < archive.Parts; i++)
                        files.Add($"{archive.Name}_{i:D3}.wz");
                    foreach (string sub in archive.SubArchives)
                        files.Add(sub + "\\");
                    if (archive.Packs > 0)
                        files.Add($"{archive.Packs} .ms pack{(archive.Packs == 1 ? "" : "s")}");

                    // One part and nothing else is a single file wearing a
                    // directory; there is no family to merge.
                    if (files.Count < 2)
                        continue;

                    found.Add(new FamilyCandidateDto
                    {
                        Name = archive.Name,
                        Kind = "split-data",
                        Folder = archive.Path,
                        Files = files,
                        Bytes = archive.SourceBytes,
                        OpenAs = FindOpenSplit(archive.Path),
                        Summary = $"{archive.Name}: {archive.Parts} part"
                                + $"{(archive.Parts == 1 ? "" : "s")}"
                                + (archive.SubArchives.Count > 0
                                    ? $", {archive.SubArchives.Count} nested archive"
                                      + $"{(archive.SubArchives.Count == 1 ? "" : "s")}" : "")
                                + (archive.Packs > 0 ? $", {archive.Packs} pack"
                                      + $"{(archive.Packs == 1 ? "" : "s")}" : ""),
                    });
                }
            }
        }
        catch (IOException) { /* detection is best-effort; the classic half still stands */ }
        catch (UnauthorizedAccessException) { }

        return found;
    }

    /// <summary>
    /// The classic shape: a base <c>Name.wz</c> with at least one
    /// <c>Name001.wz</c> beside it, in the same folder.
    ///
    /// A base with no numbered sibling is not a family — it is an ordinary
    /// archive, and offering to "merge" it would be offering to do nothing.
    /// </summary>
    private IEnumerable<FamilyCandidateDto> DetectClassic(string folder)
    {
        Dictionary<string, string> bases = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<(int Number, string Path)>> parts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in SafeFiles(folder))
        {
            if (!Path.GetExtension(file).Equals(".wz", StringComparison.OrdinalIgnoreCase))
                continue;

            string stem = Path.GetFileNameWithoutExtension(file);
            Match match = NumberedPart.Match(stem);
            if (match.Success)
            {
                string family = match.Groups["stem"].Value;
                if (!parts.TryGetValue(family, out List<(int, string)>? list))
                    parts[family] = list = new List<(int, string)>();
                list.Add((int.Parse(match.Groups["num"].Value), file));
            }

            // Not an else: a hypothetical "Foo001.wz" is both a candidate part of
            // "Foo" and a candidate base of "Foo001".
            bases[stem] = file;
        }

        List<FamilyCandidateDto> result = new();
        foreach ((string name, List<(int Number, string Path)> list) in parts)
        {
            if (!bases.TryGetValue(name, out string? basePath))
                continue;   // numbered files with no base: a backup, not a family

            List<string> ordered = new() { basePath };
            ordered.AddRange(list.OrderBy(p => p.Number).Select(p => p.Path));

            long bytes = 0;
            foreach (string path in ordered)
            {
                try { bytes += new FileInfo(path).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            result.Add(new FamilyCandidateDto
            {
                Name = name,
                Kind = "classic",
                Folder = folder,
                Files = ordered.Select(Path.GetFileName).Select(n => n ?? "").ToList(),
                Bytes = bytes,
                OpenAs = FindOpenClassic(folder, name),
                MembersOpen = ordered.Count(IsOpen),
                Summary = $"{name}: {ordered.Count} files, {Bytes(bytes)}",
            });
        }
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Families that could be formed out of what is <i>already open</i>, ignoring
    /// the disk.
    ///
    /// This is the offer that costs nothing. Opening a MapleStory folder opens
    /// every archive in it, so by the time the user sees the tree the four Skill
    /// files are already parsed and sitting there as four roots — asking them to
    /// pick a folder again to merge those same four would be asking them to
    /// re-open 3 GB.
    /// </summary>
    public List<FamilyCandidateDto> Adoptable()
    {
        List<FamilyCandidateDto> result = new();
        Dictionary<(string Folder, string Name), List<OpenFile>> groups = new();

        foreach (OpenFile file in _session.Files)
        {
            if (file.Kind != "wz" || file.Detached)
                continue;

            string folder = Path.GetDirectoryName(file.FilePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(file.FilePath);
            string family = NumberedPart.Match(stem) is { Success: true } m ? m.Groups["stem"].Value : stem;

            var key = (folder, family);
            if (!groups.TryGetValue(key, out List<OpenFile>? list))
                groups[key] = list = new List<OpenFile>();
            list.Add(file);
        }

        foreach (((string folder, string name), List<OpenFile> members) in groups)
        {
            if (members.Count < 2)
                continue;
            // The base has to be one of them: three numbered parts with no
            // Skill.wz open is half a family, and merging it would present a tree
            // that is missing everything the base holds without saying so.
            if (!members.Any(f => Path.GetFileNameWithoutExtension(f.FilePath)
                                      .Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (FindOpenClassic(folder, name) != null)
                continue;   // already merged

            List<OpenFile> ordered = OrderMembers(members, name);
            result.Add(new FamilyCandidateDto
            {
                Name = name,
                Kind = "classic",
                Folder = folder,
                Files = ordered.Select(f => f.Name).ToList(),
                Bytes = 0,
                MembersOpen = ordered.Count,
                Summary = $"{name}: {ordered.Count} archives already open",
            });
        }
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Base first, then numbered parts ascending. The order a client mounts them in.</summary>
    private static List<OpenFile> OrderMembers(IEnumerable<OpenFile> members, string name)
        => members
            .Select(f =>
            {
                string stem = Path.GetFileNameWithoutExtension(f.FilePath);
                Match m = NumberedPart.Match(stem);
                bool isBase = stem.Equals(name, StringComparison.OrdinalIgnoreCase);
                return (File: f, Rank: isBase ? -1 : (m.Success ? int.Parse(m.Groups["num"].Value) : int.MaxValue));
            })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.File)
            .ToList();

    private bool IsOpen(string path)
        => _session.Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private string? FindOpenClassic(string folder, string name)
    {
        lock (_families)
        {
            return _families.Values.FirstOrDefault(
                f => f.Kind == "classic"
                  && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase))?.Id;
        }
    }

    private string? FindOpenSplit(string archiveDirectory)
        => _session.Files.FirstOrDefault(
            f => f.Kind == "split"
              && string.Equals(f.FilePath, archiveDirectory, StringComparison.OrdinalIgnoreCase))?.Id;

    #endregion

    #region Opening and closing

    /// <summary>
    /// Opens every member of a classic family and registers the merged view.
    ///
    /// Members that are already open are reused rather than re-read —
    /// <see cref="WzSessionService.Open"/> answers with the existing entry for a
    /// path it already holds, which is what makes "merge what I already have"
    /// and "merge that folder" the same code path.
    /// </summary>
    public ArchiveFamilyDto Open(string folder, string name, OpenRequest? options = null)
    {
        FamilyCandidateDto? candidate = Detect(folder)
            .FirstOrDefault(c => c.Kind == "classic"
                              && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (candidate == null)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a split archive family in {folder}. A family is a base archive with at " +
                "least one numbered part beside it — Skill.wz with Skill001.wz.");
        }

        string? already = FindOpenClassic(candidate.Folder, candidate.Name);
        if (already != null)
            return Describe(already);

        List<string> memberIds = new();
        foreach (string file in candidate.Files)
        {
            OpenFile opened = _session.Open(new OpenRequest
            {
                Path = Path.Combine(candidate.Folder, file),
                MapleVersion = options?.MapleVersion,
                Iv = options?.Iv,
                GameVersion = options?.GameVersion ?? -1,
                ReadOnly = options?.ReadOnly ?? false,
            });
            memberIds.Add(opened.Id);
        }

        return Register(candidate.Name, "classic", candidate.Folder, memberIds);
    }

    /// <summary>
    /// Merges archives that are already in the session, without touching the disk.
    /// </summary>
    public ArchiveFamilyDto Adopt(string folder, string name)
    {
        List<OpenFile> members = _session.Files
            .Where(f => f.Kind == "wz"
                     && !f.Detached
                     && string.Equals(Path.GetDirectoryName(f.FilePath) ?? "", folder,
                                      StringComparison.OrdinalIgnoreCase)
                     && FamilyOf(Path.GetFileNameWithoutExtension(f.FilePath))
                            .Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (members.Count < 2)
        {
            throw new InvalidOperationException(
                $"Only {members.Count} archive of the '{name}' family is open, so there is nothing to merge. " +
                "Open the rest of its files first.");
        }

        string? already = FindOpenClassic(folder, name);
        if (already != null)
            return Describe(already);

        return Register(name, "classic", folder,
                        OrderMembers(members, name).Select(f => f.Id).ToList());
    }

    /// <summary>"Skill003" -> "Skill"; "Skill" -> "Skill"; "Map2" -> "Map2".</summary>
    public static string FamilyOf(string stem)
        => NumberedPart.Match(stem) is { Success: true } m ? m.Groups["stem"].Value : stem;

    private ArchiveFamilyDto Register(string name, string kind, string folder, List<string> memberIds)
    {
        lock (_families)
        {
            Family family = new()
            {
                Id = IdPrefix + _nextId++,
                Name = name,
                Kind = kind,
                Folder = folder,
                Members = memberIds,
            };
            _families[family.Id] = family;
            _log.LogInformation("Merged {Name} from {Count} archives as {Id}",
                                name, memberIds.Count, family.Id);
            return ToDto(family);
        }
    }

    /// <summary>
    /// Drops the merged view. The archives stay open and stay editable — this
    /// unmerges, it does not close.
    /// </summary>
    public bool Unmerge(string familyId)
    {
        lock (_families)
            return _families.Remove(familyId);
    }

    /// <summary>The member file ids of a family, or empty when there is no such family.</summary>
    public IReadOnlyList<string> MemberIds(string familyId)
    {
        lock (_families)
            return _families.TryGetValue(familyId, out Family? family)
                ? family.Members.ToList()
                : Array.Empty<string>();
    }

    /// <summary>
    /// Flips which copy of a shadowed image the merged tree shows.
    ///
    /// This is the honest answer to a question the data cannot settle. When
    /// 40000.img is in both Skill.wz and Skill003.wz with different bytes, no
    /// amount of inspection says which one the game loads — that depends on the
    /// client's own mount order, which is not recorded anywhere in the archives.
    /// So the app does not pretend: it shows one, says which, and lets the user
    /// see the other with one click.
    /// </summary>
    public ArchiveFamilyDto SetPrecedence(string familyId, bool lastWins)
    {
        lock (_families)
        {
            Family family = Require(familyId);
            family.LastWins = lastWins;
            return ToDto(family);
        }
    }

    public List<ArchiveFamilyDto> List()
    {
        lock (_families)
        {
            Prune();
            return _families.Values.Select(ToDto).ToList();
        }
    }

    public ArchiveFamilyDto Describe(string familyId)
    {
        lock (_families)
            return ToDto(Require(familyId));
    }

    /// <summary>
    /// Forgets members that have been closed, and families left with fewer than
    /// two.
    ///
    /// Closing an archive goes through <c>DELETE /api/files/{id}</c> and knows
    /// nothing about families, so a stale member id is not an edge case, it is
    /// the normal result of closing Skill002.wz. A family of one is not wrong,
    /// it is just an ordinary archive wearing an extra root, so it goes too.
    /// Caller holds the lock.
    /// </summary>
    private void Prune()
    {
        foreach (Family family in _families.Values.ToList())
        {
            family.Members.RemoveAll(id => _session.TryGetFile(id) == null);
            if (family.Members.Count < 2)
                _families.Remove(family.Id);
        }
    }

    private Family Require(string familyId)
        => _families.TryGetValue(familyId, out Family? family)
            ? family
            : throw new KeyNotFoundException(
                $"No merged family with id '{familyId}'. It may have been unmerged, or one of its archives closed.");

    private ArchiveFamilyDto ToDto(Family family)
    {
        List<FamilyMemberDto> members = new();
        int order = 0;
        foreach (string id in family.Members)
        {
            OpenFile? file = _session.TryGetFile(id);
            if (file == null)
                continue;
            long bytes = 0;
            try { bytes = new FileInfo(file.FilePath).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            members.Add(new FamilyMemberDto
            {
                FileId = file.Id,
                Name = file.Name,
                FilePath = file.FilePath,
                Bytes = bytes,
                ReadOnly = file.ReadOnly,
                Dirty = file.Dirty || file.CountDirtyImages() > 0,
                Order = order++,
            });
        }

        return new ArchiveFamilyDto
        {
            Id = family.Id,
            Name = family.Name,
            Kind = family.Kind,
            Folder = family.Folder,
            Members = members,
            LastWins = family.LastWins,
            ShadowedImages = family.ShadowedImages,
            Summary = $"{family.Name}: {members.Count} archives merged, "
                    + (family.LastWins
                        ? $"{members.LastOrDefault()?.Name ?? "the last file"} wins duplicates"
                        : $"{members.FirstOrDefault()?.Name ?? "the base file"} wins duplicates"),
        };
    }

    #endregion

    #region Merged tree

    /// <summary>
    /// True when a path addresses the merged view rather than a physical archive.
    ///
    /// Cheap and total: the first segment is either a live family id or it is
    /// not, so a stale "g3" from a closed family answers false and falls through
    /// to the ordinary session path, which reports "no open file with id g3" —
    /// the right error rather than a different one.
    /// </summary>
    public bool IsFamilyPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string id = WzPath.FileId(path);
        if (id.Length == 0 || id[0] != IdPrefix[0])
            return false;
        lock (_families)
            return _families.ContainsKey(id);
    }

    /// <summary>
    /// One slot in a merged directory listing: a child name, and every member
    /// that has a node under it.
    /// </summary>
    private sealed class Slot
    {
        public string Name = "";
        public int Occurrence;
        public bool IsDirectory;
        public List<(string FileId, string FileName, WzObject Node, string RealPath)> Copies = new();
    }

    /// <summary>
    /// The children of a merged folder: every member's children, unioned by name,
    /// in mount order.
    ///
    /// Directories keep a merged path so the union carries on downward.
    /// Everything else — images and anything under them — is handed back with the
    /// real session path of the copy that wins, which is what keeps editing,
    /// saving, rendering and searching pointed at one physical file.
    /// </summary>
    public List<NodeDto> GetChildren(string familyPath)
    {
        string[] segments = WzPath.SplitRaw(familyPath);
        Family family;
        lock (_families)
            family = Require(segments.Length == 0 ? "" : Uri.UnescapeDataString(segments[0]));

        string suffix = string.Join("/", segments.Skip(1));
        List<string> order = OrderSnapshot(family);
        List<Slot> slots = new();
        Dictionary<string, Slot> byKey = new(StringComparer.OrdinalIgnoreCase);

        lock (_session.Gate)
        {
            foreach (string fileId in order)
            {
                OpenFile? file = _session.TryGetFile(fileId);
                if (file == null)
                    continue;

                string memberPath = suffix.Length == 0 ? fileId : fileId + "/" + suffix;
                if (_session.TryResolve(memberPath) is not WzDirectory directory)
                    continue;

                // Per member, because "the second child called delay" is only
                // meaningful within the file that holds them both. Two members
                // each holding one "delay" are one shadowed name, not two.
                Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

                foreach (WzObject child in Enumerate(directory))
                {
                    string name = child.Name ?? "";
                    seen.TryGetValue(name, out int occurrence);
                    seen[name] = occurrence + 1;

                    string key = occurrence == 0 ? name : name + "#" + occurrence;
                    if (!byKey.TryGetValue(key, out Slot? slot))
                    {
                        byKey[key] = slot = new Slot
                        {
                            Name = name,
                            Occurrence = occurrence,
                            IsDirectory = child is WzDirectory,
                        };
                        slots.Add(slot);
                    }
                    slot.Copies.Add((fileId, file.Name, child, WzPath.Child(memberPath, name, occurrence)));
                }
            }

            List<NodeDto> result = new(slots.Count);
            foreach (Slot slot in slots)
            {
                (string fileId, string fileName, WzObject node, string realPath) = slot.Copies[0];

                // A directory keeps the merged path: its contents are still the
                // union of every member's copy of it, however deep.
                string path = slot.IsDirectory
                    ? WzPath.Child(familyPath, slot.Name, slot.Occurrence)
                    : realPath;

                NodeDto dto = _session.ToDto(node, path);

                if (slot.IsDirectory && slot.Copies.Count > 1)
                {
                    // Unknown rather than wrong. The true count is the union of
                    // the copies' children, which cannot be had without listing
                    // them all; -1 is the value this DTO already uses for "you
                    // will find out when you expand it".
                    dto.ChildCount = -1;
                    dto.HasChildren = true;
                }

                dto.Source = fileName;
                if (slot.Copies.Count > 1)
                {
                    dto.Sources = slot.Copies.Select(c => c.FileName).ToList();
                    // Only a leaf collision is a shadowing. Two members both
                    // having a "Dragon" folder is the merge working.
                    dto.Shadowed = !slot.IsDirectory;
                }
                result.Add(dto);
            }
            return result;
        }
    }

    /// <summary>Directories before images, matching the order the session lists them in.</summary>
    private static IEnumerable<WzObject> Enumerate(WzDirectory directory)
    {
        foreach (WzDirectory sub in directory.WzDirectories)
            yield return sub;
        foreach (WzImage image in directory.WzImages)
            yield return image;
    }

    /// <summary>
    /// Member ids in the order duplicates are resolved: the winner first, copied.
    ///
    /// Copied rather than enumerated lazily, and taken under the families lock,
    /// because the walks below hold <see cref="WzSessionService.Gate"/> for a
    /// whole listing and the member list can move underneath them while they do:
    /// <see cref="Prune"/> removes closed members and
    /// <see cref="SetPrecedence"/> reverses the order. Enumerating a list that
    /// another request is editing is an <c>InvalidOperationException</c> in the
    /// middle of a tree listing, which is a crash rather than a wrong answer.
    /// </summary>
    private List<string> OrderSnapshot(Family family)
    {
        lock (_families)
            return family.LastWins
                ? Enumerable.Reverse(family.Members).ToList()
                : family.Members.ToList();
    }

    /// <summary>
    /// A merged node's own DTO. The family root reports as a File so the tree
    /// draws it like an archive; everything below it is a merged directory.
    /// </summary>
    public NodeDto GetNode(string familyPath)
    {
        string[] segments = WzPath.SplitRaw(familyPath);
        Family family;
        lock (_families)
            family = Require(segments.Length == 0 ? "" : Uri.UnescapeDataString(segments[0]));

        List<string> order = OrderSnapshot(family);

        if (segments.Length == 1)
        {
            return new NodeDto
            {
                Path = familyPath,
                Name = family.Name,
                Kind = NodeKind.File,
                HasChildren = true,
                ChildCount = -1,
                Source = $"{order.Count} archives",
                Sources = order.Select(id => _session.TryGetFile(id)?.Name ?? id).ToList(),
            };
        }

        string suffix = string.Join("/", segments.Skip(1));
        lock (_session.Gate)
        {
            List<string> holders = new();
            WzObject? first = null;
            foreach (string fileId in order)
            {
                OpenFile? file = _session.TryGetFile(fileId);
                if (file == null)
                    continue;
                WzObject? node = _session.TryResolve(fileId + "/" + suffix);
                if (node is not WzDirectory)
                    continue;
                first ??= node;
                holders.Add(file.Name);
            }

            if (first == null)
            {
                throw new KeyNotFoundException(
                    $"'{suffix}' is not a folder in any archive of the '{family.Name}' family.");
            }

            NodeDto dto = _session.ToDto(first, familyPath);
            if (holders.Count > 1)
            {
                dto.ChildCount = -1;
                dto.HasChildren = true;
                dto.Sources = holders;
            }
            dto.Source = holders[0];
            return dto;
        }
    }

    /// <summary>
    /// The physical file name to badge nodes under a real path with, or null
    /// when that path is not inside a merged family.
    ///
    /// The merged listing labels its own rows, but a walk that has descended
    /// into an image is being answered by the ordinary session from then on —
    /// and those rows would lose the badge exactly where the tree is deepest and
    /// the answer least obvious. One dictionary probe per listing keeps
    /// "which file is this in" on the screen all the way down.
    /// </summary>
    public string? SourceLabelFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string fileId = WzPath.FileId(path);
        if (fileId.Length == 0 || fileId[0] == IdPrefix[0])
            return null;   // a merged path; GetChildren labels those itself

        lock (_families)
        {
            if (_families.Count == 0)
                return null;
            if (!_families.Values.Any(f => f.Members.Contains(fileId)))
                return null;
        }
        return _session.TryGetFile(fileId)?.Name;
    }

    /// <summary>
    /// Where a real node path sits in the merged tree.
    ///
    /// A merged tree's rows are a mixture: folders are addressed by the family
    /// (<c>g1/Dragon</c>) and everything from an image downward by its own file
    /// (<c>f2/Dragon/2214.img/level</c>). Selecting a node found some other way —
    /// a search hit, a pin, the back button — means expanding its ancestors, and
    /// a client cannot derive that chain by splitting the path, because the point
    /// at which it stops being a family path and starts being a file path depends
    /// on where the folders end. It is one walk to answer here and guesswork
    /// anywhere else, so it is answered here.
    ///
    /// Returns the chain from the family root down to and including the node.
    /// Empty when the path belongs to no open family.
    /// </summary>
    public List<string> Locate(string realPath)
    {
        string[] segments = WzPath.SplitRaw(realPath);
        if (segments.Length == 0)
            return new List<string>();

        string fileId = Uri.UnescapeDataString(segments[0]);
        Family? family;
        lock (_families)
        {
            Prune();
            family = _families.Values.FirstOrDefault(f => f.Members.Contains(fileId));
        }
        if (family == null)
            return new List<string>();

        List<string> chain = new() { family.Id };
        lock (_session.Gate)
        {
            // Family path for as long as the node is a folder, real path from the
            // first thing that is not. There is no third case: an image and
            // everything under it lives in exactly one file.
            bool merged = true;
            string real = fileId;
            string logical = family.Id;

            for (int i = 1; i < segments.Length; i++)
            {
                real = real + "/" + segments[i];
                logical = logical + "/" + segments[i];

                if (merged && _session.TryResolve(real) is not WzDirectory)
                    merged = false;

                chain.Add(merged ? logical : real);
            }
        }
        return chain;
    }

    #endregion

    #region Shadowing

    /// <summary>
    /// Every image name that exists in more than one member of a family.
    ///
    /// This is the report the merged view exists to make possible, and the reason
    /// it is worth the trouble. Members are meant to be disjoint slices of one
    /// namespace. Measured on the v232 client in <c>C:\MapleStory\232</c> they
    /// are not: <c>40000.img</c> and <c>40004.img</c> are in both Skill.wz and
    /// Skill003.wz with different block sizes, as are 11000, 11200, 11210, 11211
    /// and 11212. Whichever copy a tool happens to read is the one the user
    /// edits — and if the game reads the other one, a skill's icon silently stays
    /// wrong no matter how many times the edit is repeated.
    ///
    /// Walks directory tables only. Images are not parsed, so this is cheap even
    /// on a Map family: the names and block sizes it reads were loaded when the
    /// archives were opened.
    /// </summary>
    public ShadowReportDto Shadows(string familyId, int limit = 200, bool compareContent = false)
    {
        Family family;
        lock (_families)
            family = Require(familyId);

        // Ordinal, not ordinal-ignore-case: a member holding "40000.IMG" beside
        // another's "40000.img" is a collision the client resolves by its own
        // rules, so the two have to land in the same bucket. The dictionary below
        // is therefore keyed case-insensitively on purpose.
        Dictionary<string, List<ShadowCopyDto>> byPath =
            new(StringComparer.OrdinalIgnoreCase);
        List<string> order = OrderSnapshot(family);
        int scanned = 0;

        lock (_session.Gate)
        {
            foreach (string fileId in order)
            {
                OpenFile? file = _session.TryGetFile(fileId);
                if (file?.WzFile?.WzDirectory == null)
                    continue;

                foreach ((string logical, WzImage image) in Walk(file.WzFile.WzDirectory))
                {
                    scanned++;
                    if (!byPath.TryGetValue(logical, out List<ShadowCopyDto>? copies))
                        byPath[logical] = copies = new List<ShadowCopyDto>();

                    copies.Add(new ShadowCopyDto
                    {
                        File = file.Name,
                        FileId = fileId,
                        NodePath = fileId + "/" + logical,
                        Bytes = image.BlockSize,
                        Wins = copies.Count == 0,
                        Node = image,
                    });
                }
            }
        }

        List<ShadowEntryDto> entries = new();
        int differing = 0;
        int notCompared = 0;
        foreach ((string logical, List<ShadowCopyDto> copies) in byPath)
        {
            if (copies.Count < 2)
                continue;

            // Block size settles it in one direction only. Different sizes are
            // certainly different images; equal sizes establish nothing at all,
            // and calling that "does not differ" is the ambiguity this method
            // used to publish as its headline number.
            string verdict = copies.Select(c => c.Bytes).Distinct().Count() > 1
                ? "different-size"
                : compareContent
                    ? CompareCopiesByContent(copies)
                    : "not-compared";

            bool differs = verdict is "different-size" or "different-content";
            if (differs)
                differing++;
            else if (verdict == "not-compared")
                notCompared++;

            entries.Add(new ShadowEntryDto
            {
                Path = logical,
                Name = logical.Split('/').Last(),
                Copies = copies,
                Differs = differs,
                Verdict = verdict,
            });
        }

        // Known-different first, then the ones nobody read, then the ones proven
        // identical. That middle band is the point of the ordering: it used to be
        // filed with "identical" as the reassuring half of the report, and it is
        // not reassuring — it is the half that was never examined.
        entries = entries
            .OrderBy(e => e.Verdict switch
            {
                "different-size" => 0,
                "different-content" => 0,
                "not-compared" => 1,
                _ => 2,
            })
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int total = entries.Count;
        bool truncated = limit > 0 && total > limit;
        if (truncated)
            entries = entries.Take(limit).ToList();

        lock (_families)
        {
            if (_families.TryGetValue(familyId, out Family? live))
                live.ShadowedImages = total;
        }

        return new ShadowReportDto
        {
            FamilyId = familyId,
            Name = family.Name,
            Total = total,
            Differing = differing,
            NotCompared = notCompared,
            ComparedByContent = compareContent,
            Truncated = truncated,
            ImagesScanned = scanned,
            Entries = entries,
            Summary = total == 0
                ? $"No image name appears in more than one archive of {family.Name}. "
                + $"{scanned:N0} images checked."
                : $"{total:N0} image name{(total == 1 ? "" : "s")} appear in more than one archive of "
                + $"{family.Name}. {differing:N0} hold different content; only one copy of each is shown in "
                + "the merged tree and the others are hidden behind it."
                + (notCompared > 0
                    ? $" {notCompared:N0} more have copies of the same size, which were not read and are "
                    + "neither known to match nor known to differ — ask for a content comparison to settle them."
                    : ""),
        };
    }

    /// <summary>
    /// Whether same-sized copies of one image name actually hold the same
    /// content, established by reading them.
    ///
    /// <see cref="WzContentHasher"/> rather than <c>WzImage.Checksum</c>, which
    /// is a running sum over the *encrypted* block: two members of a family
    /// opened under different keys disagree on it for identical content, and it
    /// is a stored-blob identity in a question that is about content. Hashing
    /// parses the image and reads its canvases, which is why this is opt-in and
    /// why it only ever runs on the copies block size could not already separate
    /// — a handful of names, not a family.
    ///
    /// A failure to read one copy returns "not-compared" and not "identical".
    /// That is the whole point: an answer nobody could compute must not look like
    /// an answer that came out clean.
    /// </summary>
    private static string CompareCopiesByContent(List<ShadowCopyDto> copies)
    {
        string? first = null;
        foreach (ShadowCopyDto copy in copies)
        {
            if (copy.Node == null)
                return "not-compared";

            try
            {
                copy.Hash = WzContentHasher.Hash(copy.Node)[..12];
            }
            catch (Exception)
            {
                // An unparseable image, a canvas that cannot produce bytes, or a
                // subtree that contains itself. All three mean the same thing
                // here: not established.
                return "not-compared";
            }

            first ??= copy.Hash;
        }

        return copies.All(c => c.Hash == first) ? "identical" : "different-content";
    }

    /// <summary>
    /// Every image below a directory, with its path relative to the archive root.
    ///
    /// The root's own name ("Skill.wz") is excluded deliberately: the whole point
    /// is to compare Skill.wz against Skill003.wz, whose roots are named
    /// differently and whose contents are meant to be the same namespace.
    /// </summary>
    private static IEnumerable<(string Path, WzImage Image)> Walk(WzDirectory root)
    {
        Stack<(WzDirectory Directory, string Prefix)> pending = new();
        pending.Push((root, ""));
        while (pending.Count > 0)
        {
            (WzDirectory directory, string prefix) = pending.Pop();
            // Escaped as WzPath escapes, so the reported path is one a caller can
            // hand straight back to /api/node — a WZ name is allowed to contain
            // '/' and '#', and an unescaped one would address a different node.
            foreach (WzImage image in directory.WzImages)
                yield return (WzPath.Child(prefix, image.Name ?? ""), image);
            foreach (WzDirectory sub in directory.WzDirectories)
                pending.Push((sub, WzPath.Child(prefix, sub.Name ?? "")));
        }
    }

    #endregion

    private static IEnumerable<string> SafeFiles(string path)
    {
        try { return Directory.EnumerateFiles(path).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string Bytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
