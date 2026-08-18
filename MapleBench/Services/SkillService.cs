using System.Globalization;
using MapleLib.WzLib;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// Presents Skill.wz as skills rather than as a property tree.
///
/// Like <see cref="MobService"/> and <see cref="CashShopService"/>, this is a
/// projection and not a second editor: every write goes through
/// <see cref="WzEditService"/>, so skill edits share one dirty state, one undo
/// history and one save pipeline with everything else. Nothing here constructs or
/// mutates a MapleLib object directly.
///
/// The thing this mode exists to do — and the reason it is not just the mob card
/// with different labels — is that a skill's per-level values are stored two
/// incompatible ways, and no WZ editor shows them together:
///
///   (a) literally, as <c>level/1/damage</c>, <c>level/2/damage</c>, ...
///   (b) as formulas over the level variable, in a <c>common</c> block:
///       <c>common/damage = "235+3*x"</c> with <c>common/maxLevel = 30</c>.
///
/// In a v232 client the split is 911 skills stored the first way and 3,937 the
/// second, with two carrying both. An editor that shows the tree shows a skill of
/// the second kind as a handful of opaque strings, and shows a skill of the first
/// kind as thirty near-identical folders. <see cref="Detail"/> renders both as one
/// table of levels down and fields across, marking every cell with where its value
/// came from — and formula-derived cells are read-only, because they are computed,
/// not stored.
///
/// That last part is also the trap this domain has, the one the quality bar says
/// to surface where it is sprung: while a <c>common</c> block exists the client
/// reads the formulas and ignores <c>level/</c> entirely, so editing
/// <c>level/1/damage</c> on such a skill changes precisely nothing and produces no
/// error. <see cref="ExpandCommon"/> is the way out — it bakes the formulas into
/// literal levels and removes the block, which is the only order in which the
/// conversion actually takes effect.
/// </summary>
public sealed class SkillService
{
    /// <summary>
    /// A browse list is for finding a skill, not for reading a client end to end.
    /// A v232 Skill.wz holds 4,846 skills across 221 books; this is a backstop
    /// against an unusual one, and the UI is told when it bites.
    /// </summary>
    private const int MaxRows = 20_000;

    /// <summary>
    /// Rows in one skill's level table. The largest <c>maxLevel</c> in a v232
    /// client is 300 (73 skills use it). A hand-edited archive could name any
    /// number, and a table with a million rows is a hung browser, not a feature.
    /// </summary>
    private const int MaxLevelRows = 1000;

    /// <summary>
    /// The key includes a caller-supplied fileId and book, so without a bound a
    /// client could grow this without limit by asking for ids that do not exist.
    /// Matches <see cref="MobService"/>'s cap and reasoning.
    /// </summary>
    private const int MaxCachedLists = 16;

    /// <summary>
    /// Skill books parsed per gate hold while the list is built.
    ///
    /// A book is the heaviest single unit in the app — one v232 Skill.wz image
    /// holds every skill of a job with all their levels and artwork, measured at
    /// ~35 ms each across 234 of them — so the chunk is small. Eight books is
    /// roughly 280 ms of gate time per hold, against 8.1s for the whole build
    /// before this. See <see cref="WzSessionService.TryRunChunked"/>.
    /// </summary>
    private const int ChunkSize = 8;

    /// <summary>Restarts before the last pass takes the gate outright; see MobService.</summary>
    private const int MaxChunkedAttempts = 3;

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly StringPoolService _strings;
    private readonly UndoService _undo;

    /// <summary>
    /// The last browse list, and the tree generation it was taken from.
    ///
    /// Building it means parsing every skill book. Measured against a v232
    /// Skill.wz (234 images, 4,846 skills, 16,698 <c>common</c> expressions
    /// validated): 7.8–8.5s the first time, 93ms on a cache hit, and 111ms when a
    /// structural edit forces a rebuild — the images stay parsed, so only the
    /// second cost is ever paid twice. Even the 93ms is almost all serialising the
    /// 1.4 MB of JSON the list becomes; the cache lookup itself is nothing.
    ///
    /// Paying the first number on every keystroke-triggered refresh would make the
    /// mode unusable, and it happens under the session gate, so it would block
    /// every other request too. <see cref="WzSessionService.Generation"/> already
    /// ticks on every structural change, which is exactly the staleness test this
    /// needs — editing a skill's damage does not change the tree's shape, so the
    /// dirty flag and the edited value are refreshed from the write response
    /// rather than by rebuilding the list.
    /// </summary>
    private readonly Dictionary<string, (int Structure, int Value, int Names, SkillListDto List)> _listCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One list builder per cache key. Without it the idle warm-up and the first
    /// Skills visit can both parse the same 234 large book images, making the
    /// foreground request slower than a cold build instead of faster.
    /// </summary>
    private readonly Dictionary<string, object> _buildLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Book id ("522") to the name String.wz gives it ("Captain"), and the
    /// generation it was read at.
    ///
    /// Read here rather than through <see cref="StringPoolService"/> on purpose:
    /// that service indexes <c>String.wz/Skill.img</c> by skill id, keyed on a
    /// <c>name</c> child, and a book entry has a <c>bookName</c> child instead —
    /// so books are invisible to it, and teaching it about them would mean
    /// editing a file this work does not own. A v232 client carries 233 book
    /// names for 221 books, so the lookup is one small dictionary.
    /// </summary>
    private Dictionary<string, string>? _bookNames;
    private int _bookNamesGeneration = -1;

    /// <summary>
    /// The book list, by fileId filter and the generation it was built at.
    ///
    /// <see cref="Books"/> parses every book image to count its skills — 234
    /// images on a v232 client — and it was doing that on every call. Measured
    /// on the real client with all 29 archives open: 6.4 seconds, every single
    /// time, including the immediate repeat. The list view asks for it on open,
    /// on every section switch back to Skills, and after every write, so a user
    /// moving between sections paid six seconds each way for a list that had not
    /// changed.
    ///
    /// Keyed on <see cref="WzSessionService.Generation"/>, which every mutation
    /// ticks through <c>MarkFileDirty</c> — value edits included, so the Dirty
    /// flags in the DTO cannot go stale either. Same contract
    /// <see cref="_listCache"/> already relies on.
    /// </summary>
    private readonly Dictionary<string, (int Structure, SkillBooksDto Books)> _booksCache =
        new(StringComparer.Ordinal);

    public SkillService(WzSessionService session, WzEditService edit, StringPoolService strings, UndoService undo)
    {
        _session = session;
        _edit = edit;
        _strings = strings;
        _undo = undo;
    }

    /// <summary>Whether any archive that could hold skills is open.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_session.Gate)
                return SkillArchives(null).Count > 0;
        }
    }

    #region Books

    /// <summary>
    /// The skill books on offer, with the count of skills in each.
    ///
    /// Images that are not books are returned too, named, with the reason. A
    /// v232 Skill.wz holds 234 images of which 221 are books; the other 13 are
    /// tables the client reads separately (Attacktype.img, MCSkill.img,
    /// Recipe_9200.img...). Dropping them silently would look like the archive
    /// was smaller than it is.
    /// </summary>
    public SkillBooksDto Books(string? fileId)
    {
        SkillBooksDto result = new();
        string cacheKey = fileId ?? "*";

        lock (_session.Gate)
        {
            int generation = _session.StructureGeneration;
            if (_booksCache.TryGetValue(cacheKey, out (int Structure, SkillBooksDto Books) cached)
                && cached.Structure == generation)
            {
                // Only the dirty flags can have moved. A book's path, id, name
                // and skill count all depend on the tree's shape, which this key
                // already covers -- so unlike the row lists there is nothing to
                // re-summarise, and a value edit costs one flag read per book
                // instead of re-parsing 234 images.
                RefreshBookDirty(cached.Books);
                return cached.Books;
            }

            EnsureBookNames();

            foreach (OpenFile file in SkillArchives(fileId))
            {
                if (file.LooseImage != null)
                {
                    WzImage materialized = file.LooseImage;
                    string bookId = Stem(materialized.Name);
                    WzImageProperty? skills;
                    try
                    {
                        WzSessionService.EnsureParsed(materialized);
                        skills = Child(materialized.WzProperties, "skill");
                    }
                    catch (Exception ex)
                    {
                        result.Ignored.Add(new SkillIgnoredDto
                        {
                            Path = file.Id,
                            Name = materialized.Name,
                            Reason = ex.Message,
                        });
                        continue;
                    }

                    if (skills?.WzProperties == null)
                    {
                        result.Ignored.Add(new SkillIgnoredDto
                        {
                            Path = file.Id,
                            Name = materialized.Name,
                            Reason = "This image has no 'skill' node, so it is not a skill book.",
                        });
                        continue;
                    }

                    result.Books.Add(new SkillBookDto
                    {
                        Path = file.Id,
                        BookId = bookId,
                        Name = BookName(bookId),
                        SkillCount = skills.WzProperties.Count,
                        Dirty = materialized.Changed,
                    });
                    continue;
                }
                WzDirectory? root = _session.RoleRoot(file, "Skill");
                if (root == null)
                    continue;

                foreach ((WzImage image, string path) in EnumerateBookImages(
                    root, _session.RoleRootPath(file, "Skill")))
                {
                    WzImage materialized = _session.MaterializeImage(image);
                    string bookId = Stem(materialized.Name);
                    WzImageProperty? skills;
                    try
                    {
                        WzSessionService.EnsureParsed(materialized);
                        skills = Child(materialized.WzProperties, "skill");
                    }
                    catch (Exception ex)
                    {
                        result.Ignored.Add(new SkillIgnoredDto
                        {
                            Path = path,
                            Name = materialized.Name,
                            Reason = ex.Message,
                        });
                        continue;
                    }

                    if (skills?.WzProperties == null)
                    {
                        result.Ignored.Add(new SkillIgnoredDto
                        {
                            Path = path,
                            Name = materialized.Name,
                            Reason = "This image has no 'skill' node, so it is not a skill book.",
                        });
                        continue;
                    }

                    result.Books.Add(new SkillBookDto
                    {
                        Path = path,
                        BookId = bookId,
                        Name = BookName(bookId),
                        SkillCount = skills.WzProperties.Count,
                        Dirty = materialized.Changed,
                    });
                }
            }

            // Inside the gate: _bookNames is only ever written under it, and
            // reading it outside would race a concurrent rebuild for no benefit.
            result.NamesAvailable = _bookNames is { Count: > 0 };

            // Stored against the structural generation the build STARTED at. If
            // anything reshaped the tree while the images were parsing, this
            // entry is already stale by its own key and the next call rebuilds —
            // which is the right way round. Storing the current number instead
            // would bless a list partly read from a tree that has since moved.
            if (_booksCache.Count >= MaxCachedLists)
                _booksCache.Clear();
            _booksCache[cacheKey] = (generation, result);
        }

        return result;
    }

    /// <summary>
    /// Book names out of <c>String.wz/Skill.img/&lt;book&gt;/bookName</c>.
    ///
    /// Cached against the session generation rather than rebuilt per call: the
    /// image holds 11,176 entries in a v232 client and this runs once per browse.
    /// Caller must hold <see cref="WzSessionService.Gate"/>.
    /// </summary>
    private void EnsureBookNames()
    {
        int generation = _session.Generation;
        if (_bookNames != null && _bookNamesGeneration == generation)
            return;

        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (OpenFile file in _session.Files)
        {
            WzDirectory? stringRoot = _session.RoleRoot(file, "String");
            if (stringRoot == null)
                continue;
            if (!file.Name.StartsWith("String", StringComparison.OrdinalIgnoreCase))
                continue;

            WzImage? image = stringRoot.WzImages.FirstOrDefault(
                i => string.Equals(i.Name, "Skill.img", StringComparison.OrdinalIgnoreCase));
            if (image == null)
                continue;

            try
            {
                WzSessionService.EnsureParsed(image);
                foreach (WzImageProperty entry in image.WzProperties)
                {
                    string? name = Child(entry.WzProperties, "bookName")?.WzValue?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && entry.Name != null)
                        names[entry.Name] = name;
                }
            }
            catch
            {
                // A String.wz that will not parse costs names, not function. The
                // rest of the mode works against ids; degrading here is the
                // documented behaviour, not a failure to report.
            }
        }

        _bookNames = names;
        _bookNamesGeneration = generation;
    }

    private string? BookName(string bookId) =>
        _bookNames != null && _bookNames.TryGetValue(bookId, out string? name) ? name : null;

    #endregion

    #region Browse

    public SkillListDto List(
        string? fileId, string? bookPath, bool resolveNames, CancellationToken cancel = default,
        bool allowExclusiveFallback = true)
    {
        string key = $"{fileId ?? "*"}|{bookPath ?? "*"}|{resolveNames}";

        // Outside the gate; see MobService.List for why the pool is warmed here
        // rather than reached for once per row.
        if (resolveNames)
            _strings.Warm(cancel, allowExclusiveFallback);
        // Warm cache: no build lock needed.
        lock (_session.Gate)
        {
            if (TryServeCached(key, resolveNames, out SkillListDto? hit))
                return hit;
        }

        lock (BuildLockFor(key))
        {
            // A caller ahead of us most likely filled it while we waited.
            lock (_session.Gate)
            {
                if (TryServeCached(key, resolveNames, out SkillListDto? cached))
                    return cached;
            }

            for (int attempt = 0; ; attempt++)
            {
                SkillListDto? built = TryBuild(fileId, bookPath, resolveNames,
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
    /// Same shape and same contract as <c>MobService.TryServeCached</c>. The
    /// rows here are skills rather than images, so an edit to
    /// <c>…/skill/1100000/common/damage</c> is owned by the row for
    /// <c>…/skill/1100000</c> — which is what walking the path's ancestors
    /// finds, without this code needing to know anything about the shape of a
    /// skill book. Caller must hold the gate.
    /// </summary>
    private bool TryServeCached(string key, bool resolveNames, out SkillListDto? list)
    {
        list = null;
        if (!_listCache.TryGetValue(key, out (int Structure, int Value, int Names, SkillListDto List) cached))
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
        if (cached.Value != value && !TryPatch(cached.List, touched, resolveNames))
            return false;

        // Dirty flags move without the tree changing shape, so they are the
        // one thing refreshed on a cache hit.
        RefreshDirty(cached.List);
        _listCache[key] = (structure, value, names, cached.List);
        list = cached.List;
        return true;
    }

    /// <summary>
    /// Re-summarises the rows owning the touched paths. False if any of them
    /// cannot be re-read, which the caller turns into a full rebuild.
    /// </summary>
    private bool TryPatch(SkillListDto list, IReadOnlyCollection<string> touched, bool resolveNames)
    {
        if (touched.Count == 0)
            return true;

        Dictionary<string, int> index = new(list.Skills.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Skills.Count; i++)
            index[list.Skills[i].Path] = i;

        bool any = false;
        foreach (string path in touched)
        {
            int row = WzSessionService.OwnerOf(index, path);
            if (row < 0)
                continue;   // an edit to something this list does not show

            SkillSummaryDto before = list.Skills[row];
            if (_session.Resolve(before.Path) is not WzImageProperty skill)
                return false;

            try
            {
                // The book columns come from the row rather than being looked up
                // again: a value edit cannot have moved a skill to a different
                // book, and re-reading the book name would mean re-walking
                // String.wz for a field that has not changed.
                list.Skills[row] = Summarise(skill, before.Path, before.BookPath, before.BookId,
                                             before.BookName, before.Dirty, resolveNames);
                any = true;
            }
            catch
            {
                return false;
            }
        }

        if (any)
            list.Stats = Summarise(list.Skills, list.Stats?.Books ?? 0);
        return true;
    }

    /// <summary>
    /// One build pass, in gate-releasing chunks. Null when the tree changed
    /// while it ran — see <see cref="WzSessionService.TryRunChunked"/>.
    /// </summary>
    private SkillListDto? TryBuild(
        string? fileId, string? bookPath, bool resolveNames,
        bool interleave, CancellationToken cancel, out int generation)
    {
        SkillListDto result = new();
        List<SkillSummaryDto> skills = result.Skills;
        HashSet<string> books = new(StringComparer.Ordinal);

        List<(WzImage Image, string Path)> work = new();
        lock (_session.Gate)
        {
            generation = _session.Generation;
            EnsureBookNames();

            foreach (OpenFile file in SkillArchives(fileId))
            {
                if (file.LooseImage != null)
                {
                    if (bookPath == null || string.Equals(file.Id, bookPath, StringComparison.OrdinalIgnoreCase))
                        work.Add((file.LooseImage, file.Id));
                    continue;
                }
                WzDirectory? root = _session.RoleRoot(file, "Skill");
                if (root == null)
                    continue;

                foreach ((WzImage image, string path) in EnumerateBookImages(
                    root, _session.RoleRootPath(file, "Skill")))
                {
                    if (bookPath != null && !string.Equals(path, bookPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    work.Add((image, path));
                }
            }
        }

        bool complete = _session.TryRunChunked(generation, work, item =>
        {
            if (skills.Count >= MaxRows)
            {
                result.Truncated = true;
                return;
            }

            WzImage image = _session.MaterializeImage(item.Image);

            // Parsed, deliberately, even though it is the expensive part:
            // measured at ~8s for a v232 Skill.wz's 234 images. Listing
            // them unparsed was worse than slow -- every row would report
            // no levels and no damage, which looks like a broken client
            // rather than like data that has not loaded. The cost is paid
            // once per generation and cached; see _listCache.
            WzSessionService.EnsureParsed(image);

            WzImageProperty? node = Child(image.WzProperties, "skill");
            if (node?.WzProperties == null)
                return;

            books.Add(item.Path);
            string bookId = Stem(image.Name);
            string? bookName = BookName(bookId);
            string skillsPath = WzPath.Child(item.Path, "skill");

            foreach (WzImageProperty skill in node.WzProperties)
            {
                if (skills.Count >= MaxRows)
                {
                    result.Truncated = true;
                    break;
                }
                skills.Add(Summarise(
                    skill, WzPath.Child(skillsPath, skill.Name ?? ""),
                    item.Path, bookId, bookName, image.Changed, resolveNames));
            }
        }, ChunkSize, interleave, cancel);

        if (!complete)
            return null;

        result.Stats = Summarise(skills, books.Count);
        return result;
    }

    /// <summary>
    /// One page of the list, so the first screen can paint without serialising
    /// all 4,846 rows (2.7 MB of JSON on a v232 client). The list itself is
    /// still built and cached whole; see <see cref="MobService.Page"/>.
    /// </summary>
    public (SkillListDto Page, int Total) Page(
        string? fileId, string? bookPath, bool resolveNames, int offset, int limit,
        CancellationToken cancel = default)
    {
        SkillListDto all = List(fileId, bookPath, resolveNames, cancel);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxRows);

        return (new SkillListDto
        {
            Skills = all.Skills.Skip(offset).Take(limit).ToList(),
            Stats = all.Stats,
            Truncated = all.Truncated,
        }, all.Skills.Count);
    }

    /// <summary>
    /// Re-reads just the dirty flags of a cached list.
    ///
    /// Editing a skill's damage does not change the tree's shape, so the
    /// generation does not tick and the cached rows stay valid — but whether the
    /// book is now unsaved has changed, and that is what the browse grid marks.
    /// Resolved once per book rather than once per row: a v232 client has 4,846
    /// skills in 221 books, so the per-row version would be twenty times the work
    /// for the same answer.
    /// </summary>
    /// <summary>
    /// Re-reads the unsaved flag on each book. Caller must hold the gate.
    ///
    /// A book that no longer resolves keeps its previous flag rather than
    /// failing the request: only a structural edit can remove one, and that
    /// ticks the key this cache is held under, so the very next call rebuilds.
    /// </summary>
    private void RefreshBookDirty(SkillBooksDto books)
    {
        foreach (SkillBookDto book in books.Books)
        {
            try
            {
                if (_session.Resolve(book.Path) is WzImage image)
                    book.Dirty = image.Changed;
            }
            catch
            {
                /* see the summary */
            }
        }
    }

    private void RefreshDirty(SkillListDto list)
    {
        Dictionary<string, bool> byBook = new(StringComparer.Ordinal);

        foreach (SkillSummaryDto skill in list.Skills)
        {
            if (!byBook.TryGetValue(skill.BookPath, out bool dirty))
            {
                try
                {
                    dirty = _session.Resolve(skill.BookPath) is WzImage image && image.Changed;
                }
                catch
                {
                    // A row whose path no longer resolves. Close() takes the same
                    // gate, so this is not a file closing under us -- it is a node
                    // a structural edit removed, which also ticks Generation, so
                    // the very next call rebuilds the list from scratch. Leaving
                    // the stale flag for those few milliseconds is better than
                    // failing the request.
                    dirty = skill.Dirty;
                }
                byBook[skill.BookPath] = dirty;
            }
            skill.Dirty = dirty;
        }
    }

    private SkillSummaryDto Summarise(
        WzImageProperty skill, string path, string bookPath, string bookId,
        string? bookName, bool dirty, bool resolveNames)
    {
        TryId(skill.Name, out int skillId);

        SkillSummaryDto dto = new()
        {
            Path = path,
            SkillId = skillId,
            BookPath = bookPath,
            BookId = bookId,
            BookName = bookName,
            Dirty = dirty,
            Name = resolveNames ? _strings.GetSkillName(skillId) : null,
        };

        WzImageProperty? common = Child(skill.WzProperties, "common");
        WzImageProperty? levels = Child(skill.WzProperties, "level");

        dto.LevelCount = levels?.WzProperties?.Count ?? 0;
        dto.Passive = Child(skill.WzProperties, "psd") != null;
        dto.Invisible = Child(skill.WzProperties, "invisible") != null;

        bool hasCommon = common?.WzProperties is { Count: > 0 };
        bool hasLevel = dto.LevelCount > 0;
        dto.Storage = (hasCommon, hasLevel) switch
        {
            (true, true) => "mixed",
            (true, false) => "formula",
            (false, true) => "explicit",
            _ => "none",
        };

        if (hasCommon)
        {
            dto.Damage = Text(common, "damage");
            dto.MpCon = Text(common, "mpCon");
            dto.Cooltime = Text(common, "cooltime");

            FormulaScope scope = ScopeFor(common, null);

            foreach (WzImageProperty entry in common!.WzProperties!)
            {
                if (string.Equals(entry.Name, "maxLevel", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.WzValue is not string expression)
                    continue;

                // Through the scope, so "damage = y*2" counts as level-varying
                // when y is. Counting it as a constant put level 1's number down
                // the whole column with nothing to say it had done so.
                if (SkillFormulaEvaluator.ReferencesLevel(expression, scope))
                    dto.FormulaFields++;

                // Validated whether or not it varies with the level. Counting only
                // the level-varying ones hid the one genuinely broken expression in
                // a v232 client -- skill 65000003's `y = "140+y"`, which references
                // itself -- because a name that is not 'x' is not a level reference,
                // so the row reported a clean skill over a value nothing can compute.
                //
                // "Bad" means will not parse. A formula that parses but reads a
                // variable the archive does not carry is not counted here: it is a
                // question for the user, and flagging 200+4*x30 as a defect in the
                // client's own data would be this editor being wrong out loud.
                if (SkillFormulaEvaluator.LooksLikeFormula(expression)
                    && !SkillFormulaEvaluator.IsValid(expression, out _))
                    dto.BadFormulas++;
            }
        }
        else if (hasLevel)
        {
            // Falls back to level 1 so the browse columns are not blank for a
            // skill stored the other way. Level 1 is the only level every skill
            // that has any certainly has.
            WzImageProperty? first = Child(levels!.WzProperties, "1");
            dto.Damage = Text(first, "damage");
            dto.MpCon = Text(first, "mpCon");
            dto.Cooltime = Text(first, "cooltime");
        }

        dto.MaxLevel = ReadMaxLevel(common, levels);
        return dto;
    }

    private static SkillStatsDto Summarise(List<SkillSummaryDto> skills, int books)
    {
        SkillStatsDto stats = new() { Total = skills.Count, Books = books };

        foreach (SkillSummaryDto skill in skills)
        {
            switch (skill.Storage)
            {
                case "formula": stats.FormulaDriven++; break;
                case "explicit": stats.ExplicitLevels++; break;
                case "mixed": stats.Mixed++; break;
            }
            stats.BadFormulas += skill.BadFormulas;
        }
        return stats;
    }

    /// <summary>
    /// How many levels the skill has, or null when nothing says.
    ///
    /// <c>common/maxLevel</c> when there is one; otherwise the highest numbered
    /// <c>level/N</c> node. Not the *count* of those nodes: a client that skips a
    /// number would then report fewer levels than its highest one, and the table
    /// would silently lose its last row. Never 0 — a skill with no levels at all
    /// reports null, which the UI renders as "—".
    /// </summary>
    private static int? ReadMaxLevel(WzImageProperty? common, WzImageProperty? levels)
    {
        string? declared = Child(common?.WzProperties, "maxLevel")?.WzValue?.ToString();
        if (int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max > 0)
            return max;

        int highest = 0;
        if (levels?.WzProperties != null)
        {
            foreach (WzImageProperty level in levels.WzProperties)
            {
                if (int.TryParse(level.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    highest = Math.Max(highest, n);
            }
        }
        return highest > 0 ? highest : null;
    }

    #endregion

    #region Detail

    /// <summary>
    /// The virtual level table: levels down, fields across, over both storage
    /// shapes at once.
    ///
    /// A cell is filled in from, in order of precedence:
    ///   1. a literal <c>level/N/key</c> node — "explicit", and editable;
    ///   2. the <c>common/key</c> expression evaluated at N — "formula" when it
    ///      varies with the level, "constant" when it does not, and read-only
    ///      either way because it is computed rather than stored;
    ///   3. nothing — "missing", with no value at all.
    ///
    /// A formula that will not parse produces "error" with the parser's message
    /// and no value. That is the whole reason the evaluator returns a nullable:
    /// a table of confident zeros over damage formulas nobody could read is worse
    /// than a table that admits which cells it does not know.
    /// </summary>
    public SkillDetailDto Detail(string path) => Detail(path, null);

    /// <inheritdoc cref="Detail(string)"/>
    /// <param name="variables">
    /// Values for free variables — names a formula uses that the block does not
    /// define, such as the <c>x30</c> in <c>"200+4*x30"</c>. The client supplies
    /// those at runtime and the archive does not record them, so the table asks
    /// for them once and then computes the whole column.
    /// </param>
    public SkillDetailDto Detail(string path, IReadOnlyDictionary<string, double>? variables)
    {
        lock (_session.Gate)
        {
            WzImageProperty skill = ResolveSkill(path);
            EnsureBookNames();

            string skillsPath = WzPath.Parent(path)
                ?? throw new InvalidOperationException($"'{path}' is not a skill.");
            string bookPath = WzPath.Parent(skillsPath) ?? skillsPath;
            string bookId = Stem(skill.ParentImage?.Name ?? "");

            TryId(skill.Name, out int skillId);

            WzImageProperty? common = Child(skill.WzProperties, "common");
            WzImageProperty? levels = Child(skill.WzProperties, "level");
            bool hasCommon = common?.WzProperties is { Count: > 0 };
            bool hasLevel = levels?.WzProperties is { Count: > 0 };

            SkillDetailDto dto = new()
            {
                Path = path,
                SkillId = skillId,
                Name = _strings.GetSkillName(skillId),
                BookPath = bookPath,
                BookId = bookId,
                BookName = BookName(bookId),
                Dirty = skill.ParentImage?.Changed ?? false,
                HasCommon = hasCommon,
                HasLevel = hasLevel,
                HasPvpCommon = Child(skill.WzProperties, "PVPcommon") != null,
                CommonPath = common == null ? null : WzPath.Child(path, common.Name ?? "common"),
                LevelPath = WzPath.Child(path, "level"),
                MaxLevel = ReadMaxLevel(common, levels),
                Storage = (hasCommon, hasLevel) switch
                {
                    (true, true) => "mixed",
                    (true, false) => "formula",
                    (false, true) => "explicit",
                    _ => "none",
                },
            };

            // Built once and shared by every column and every cell: a formula in
            // a 'common' block can read any other key of the same block, so the
            // block as a whole is the namespace, not the single entry.
            FormulaScope scope = ScopeFor(common, variables);

            dto.Warning = DescribeCommonTrap(dto);
            BuildColumns(dto, common, levels, scope);
            BuildRows(dto, path, common, levels, scope);
            BuildSkillFields(dto, skill, path);

            foreach (SkillColumnDto column in dto.Columns)
            {
                foreach (string name in column.Needs)
                {
                    if (!dto.Variables.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        dto.Variables.Add(new SkillVariableDto
                        {
                            Name = name,
                            Value = variables != null && variables.TryGetValue(name, out double v)
                                ? v.ToString("0.##########", CultureInfo.InvariantCulture)
                                : null,
                        });
                    }
                }
            }
            return dto;
        }
    }

    /// <summary>
    /// The namespace a <c>common</c> formula is evaluated in: every sibling key
    /// that holds a value, plus whatever the user has typed for the names the
    /// block does not define.
    ///
    /// Containers and vectors are left out. <c>lt</c>/<c>rb</c> are Vector
    /// properties on 1,309 v232 skills and a vector has no scalar value, so
    /// admitting one as a variable would only produce a parse failure later,
    /// further from the cause.
    /// </summary>
    private static FormulaScope ScopeFor(
        WzImageProperty? common, IReadOnlyDictionary<string, double>? supplied)
    {
        Dictionary<string, string> definitions = new(StringComparer.OrdinalIgnoreCase);

        if (common?.WzProperties != null)
        {
            foreach (WzImageProperty entry in common.WzProperties)
            {
                if (entry.Name == null || entry.WzProperties is { Count: > 0 })
                    continue;

                if (entry.WzValue is string text)
                {
                    definitions[entry.Name] = text;
                }
                else if (entry.WzValue != null)
                {
                    // Numeric leaves only -- maxLevel is an Int and is a perfectly
                    // ordinary thing for a formula to reference. Anything whose
                    // text is not a number (a Point renders as "{X=0,Y=0}") is
                    // skipped rather than admitted and failed on use.
                    string? asText = Convert.ToString(entry.WzValue, CultureInfo.InvariantCulture);
                    if (asText != null && double.TryParse(
                            asText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        definitions[entry.Name] = asText;
                }
            }
        }

        return new FormulaScope(definitions, supplied);
    }

    /// <summary>
    /// The one failure in this domain that costs an evening and produces no
    /// error, stated where the user is about to hit it.
    /// </summary>
    private static string? DescribeCommonTrap(SkillDetailDto dto)
    {
        if (!dto.HasCommon)
            return null;

        return dto.HasLevel
            ? "This skill has a 'common' block, so the client computes every level from the formulas and " +
              "ignores the level/ nodes below — even though they exist. Edit the formulas, or bake them " +
              "into explicit levels to make the level/ values the ones that count."
            : "This skill's per-level values are formulas in its 'common' block. Adding a level/1/damage " +
              "node here would change nothing in game: the client reads the formula instead. Use 'Bake " +
              "into explicit levels' to convert first.";
    }

    private static void BuildColumns(
        SkillDetailDto dto, WzImageProperty? common, WzImageProperty? levels, FormulaScope scope)
    {
        // Every key either block mentions, deduplicated case-insensitively but
        // remembering the spelling the archive used -- that spelling is what a
        // write has to match.
        Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);

        if (common?.WzProperties != null)
        {
            foreach (WzImageProperty entry in common.WzProperties)
            {
                if (entry.Name == null || string.Equals(entry.Name, "maxLevel", StringComparison.OrdinalIgnoreCase))
                    continue;   // maxLevel describes the table; it is not a column of it
                keys.TryAdd(entry.Name, entry.Name);
            }
        }
        if (levels?.WzProperties != null)
        {
            foreach (WzImageProperty level in levels.WzProperties)
            {
                if (level.WzProperties == null)
                    continue;
                foreach (WzImageProperty field in level.WzProperties)
                {
                    if (field.Name != null)
                        keys.TryAdd(field.Name, field.Name);
                }
            }
        }

        foreach (string key in keys.Values
                     .OrderBy(k => SkillFieldCatalog.GroupRank(SkillFieldCatalog.Find(k)?.Group ?? "Other"))
                     .ThenBy(SkillFieldCatalog.Rank)
                     .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            SkillFieldSpec spec = SkillFieldCatalog.Find(key) ?? SkillFieldCatalog.Unknown(key);
            WzImageProperty? formula = Child(common?.WzProperties, key);

            SkillColumnDto column = new()
            {
                Key = key,
                Label = spec.Label,
                Group = spec.Group,
                Kind = spec.Kind.ToString(),
                Unit = spec.Unit,
                Hint = spec.Hint,
                Editable = formula == null,
            };

            if (formula != null)
            {
                column.FormulaPath = WzPath.Child(
                    WzPath.Child(dto.Path, common!.Name ?? "common"), formula.Name ?? key);

                // A container -- 'variableRect' is one on a v232 client -- has no
                // scalar value and no formula. Reported as a container rather than
                // drawn as an empty editable box that reaches WzNodeFactory's
                // `default:` throw the moment someone types in it.
                if (formula.WzValue is null && formula.WzProperties?.Count > 0)
                {
                    column.Source = "container";
                    column.Formula = $"{formula.WzProperties.Count} entries";
                }
                else if (formula.WzValue is string expression)
                {
                    column.Formula = expression;
                    if (SkillFormulaEvaluator.IsValid(expression, out string? error))
                    {
                        if (SkillFormulaEvaluator.HasCycle(expression, scope))
                        {
                            // Parses, but reaches itself through the block, so it
                            // has no value at any level. Reported as the error it
                            // is rather than falling through to "constant", which
                            // printed the expression text where a number belongs.
                            column.Source = "error";
                            column.FormulaError =
                                $"'{expression}' is defined in terms of itself, so it has no value. " +
                                "The client cannot compute it either.";
                        }
                        else
                        {
                            // Free variables do not make a formula invalid, only
                            // uncomputed. Recorded on the column so the table can
                            // ask for them once instead of printing the same
                            // complaint down thirty rows.
                            column.Needs = SkillFormulaEvaluator.FreeNames(expression, scope);
                            column.Source = SkillFormulaEvaluator.ReferencesLevel(expression, scope)
                                ? "formula"
                                : column.Needs.Count > 0 ? "formula" : "constant";
                        }
                    }
                    else if (!SkillFormulaEvaluator.LooksLikeFormula(expression))
                    {
                        // Not every common entry is arithmetic -- 'action' holds an
                        // animation name. Shown as the text it is rather than
                        // reported as a broken formula it never was.
                        column.Source = "constant";
                    }
                    else
                    {
                        column.Source = "error";
                        column.FormulaError = error;
                    }
                }
                else
                {
                    // lt/rb are Vector properties inside common on 1,309 v232
                    // skills, and maxLevel is an Int. Neither is an expression, so
                    // neither is run through the parser -- they are the same value
                    // at every level.
                    column.Source = "constant";
                    column.Formula = FormatConstant(formula);
                }
            }
            else
            {
                column.Source = "explicit";
            }

            dto.Columns.Add(column);
        }
    }

    private static void BuildRows(
        SkillDetailDto dto, string skillPath, WzImageProperty? common, WzImageProperty? levels,
        FormulaScope scope)
    {
        string levelPath = WzPath.Child(skillPath, levels?.Name ?? "level");

        // The row set is the union of "levels the formulas cover" and "levels that
        // physically exist". Taking only the first would hide a level/31 node in a
        // skill whose maxLevel says 30 -- and hidden data in an editor is worse
        // than clutter.
        SortedSet<int> rows = new();
        for (int level = 1; level <= Math.Min(dto.MaxLevel ?? 0, MaxLevelRows); level++)
            rows.Add(level);

        if (levels?.WzProperties != null)
        {
            foreach (WzImageProperty node in levels.WzProperties)
            {
                if (int.TryParse(node.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    && n > 0 && rows.Count < MaxLevelRows)
                    rows.Add(n);
            }
        }

        if ((dto.MaxLevel ?? 0) > MaxLevelRows)
            dto.Truncated = true;

        foreach (int level in rows)
        {
            WzImageProperty? node = Child(levels?.WzProperties, level.ToString(CultureInfo.InvariantCulture));
            SkillLevelRowDto row = new()
            {
                Level = level,
                Path = WzPath.Child(levelPath, level.ToString(CultureInfo.InvariantCulture)),
                Present = node != null,
            };

            foreach (SkillColumnDto column in dto.Columns)
                row.Cells.Add(BuildCell(column, node, row.Path, level, scope));

            dto.Levels.Add(row);
        }
    }

    private static SkillCellDto BuildCell(
        SkillColumnDto column, WzImageProperty? levelNode, string levelPath, int level, FormulaScope scope)
    {
        SkillCellDto cell = new() { Key = column.Key };

        WzImageProperty? explicitNode = Child(levelNode?.WzProperties, column.Key);
        if (explicitNode != null)
        {
            cell.Path = WzPath.Child(levelPath, explicitNode.Name ?? column.Key);
            cell.WzType = explicitNode.PropertyType.ToString();

            if (explicitNode.WzValue is null && explicitNode.WzProperties?.Count > 0)
            {
                cell.Source = "container";
                cell.Value = $"{explicitNode.WzProperties.Count} entries";
                cell.Editable = false;
                return cell;
            }

            cell.Source = "explicit";
            // FormatConstant, not ToString: a WzVectorProperty renders itself as
            // "{X=0,Y=0}", which WzNodeFactory.ParseVector rejects -- so a cell the
            // UI hands straight back on edit would fail to save the value it was
            // shown. "0, 0" round-trips, and matches how a formula-derived point
            // is rendered in the same column.
            cell.Value = FormatConstant(explicitNode);
            // Editable even under a common block -- the node is real and a user is
            // entitled to change it -- but the detail's warning says plainly that
            // the client will not read it until the block is gone.
            cell.Editable = true;
            return cell;
        }

        switch (column.Source)
        {
            case "container":
                cell.Source = "container";
                cell.Value = column.Formula;
                cell.Editable = false;
                return cell;

            case "error":
                // No value, on purpose. See the class comment: a zero here is
                // indistinguishable from a real zero.
                cell.Source = "error";
                cell.Error = column.FormulaError;
                cell.Editable = false;
                return cell;

            case "constant":
                // Same at every level. A numeric string still goes through the
                // evaluator so "400" and " 400 " read alike; anything the parser
                // cannot reduce -- a Vector's "{X=-339,Y=-290}", say -- is shown
                // exactly as the archive stores it rather than blanked.
                cell.Source = "constant";
                cell.Value = Compute(column.Formula, level, scope, out _) ?? column.Formula;
                cell.Editable = false;
                return cell;

            case "formula":
                cell.Source = "formula";
                cell.Value = Compute(column.Formula, level, scope, out string? error);
                if (cell.Value == null)
                {
                    // A free variable is a question, not a fault: the formula is
                    // sound and only the value the client would supply is
                    // missing. Marked apart from a real parse failure so the
                    // table can offer an input instead of a red cell.
                    cell.Source = column.Needs.Count > 0 ? "needs" : "error";
                    cell.Error = error;
                }
                cell.Editable = false;
                return cell;

            default:
                cell.Source = "missing";
                cell.Path = WzPath.Child(levelPath, column.Key);
                cell.Editable = true;
                return cell;
        }
    }

    /// <summary>
    /// Evaluates one formula at one level, formatted the way the rest of the app
    /// formats numbers.
    ///
    /// "0.##########" and InvariantCulture, both deliberately. The format keeps an
    /// integral result integral (200, not 200.0000) while not throwing away the
    /// fractional part of a rate; the culture is because <c>WzNodeFactory</c>
    /// parses invariant, so a value formatted in the desktop's culture comes back
    /// as "0,35" and fails the parse on every comma-decimal machine — which is
    /// exactly how bulk edit used to break on de-DE and fr-FR installs.
    /// </summary>
    private static string? Compute(string? expression, int level, FormulaScope scope, out string? error)
    {
        double? value = SkillFormulaEvaluator.Evaluate(expression, level, scope, out error);
        return value?.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The fields that belong to the skill rather than to a level — masterLevel,
    /// weapon, elemAttr, and everything under <c>info</c>.
    /// </summary>
    private static void BuildSkillFields(SkillDetailDto dto, WzImageProperty skill, string skillPath)
    {
        Dictionary<string, SkillFieldGroupDto> groups = new(StringComparer.Ordinal);

        Collect(skill.WzProperties, skillPath);

        WzImageProperty? info = Child(skill.WzProperties, "info");
        if (info?.WzProperties != null)
            Collect(info.WzProperties, WzPath.Child(skillPath, info.Name ?? "info"));

        dto.Groups = groups.Values
            .OrderBy(g => SkillFieldCatalog.GroupRank(g.Group))
            .ToList();

        void Collect(WzPropertyCollection? properties, string parentPath)
        {
            if (properties == null)
                return;

            foreach (WzImageProperty property in properties)
            {
                string key = property.Name ?? "";
                if (key.Length == 0)
                    continue;

                // The blocks the level table already owns, and the artwork. Listing
                // 'icon' as an editable field would be a lie -- it is a canvas, and
                // replacing its pixels is the Explorer's job.
                if (key is "common" or "PVPcommon" or "level" or "info"
                        or "icon" or "iconMouseOver" or "iconDisabled")
                    continue;

                SkillFieldSpec spec = SkillFieldCatalog.Find(key) ?? SkillFieldCatalog.Unknown(key);
                if (!groups.TryGetValue(spec.Group, out SkillFieldGroupDto? group))
                {
                    group = new SkillFieldGroupDto { Group = spec.Group };
                    groups[spec.Group] = group;
                }

                bool isContainer = property.WzValue is null && property.WzProperties?.Count > 0;

                group.Fields.Add(new SkillFieldDto
                {
                    Key = key,
                    Label = spec.Label,
                    Group = spec.Group,
                    Kind = isContainer ? "Container" : spec.Kind.ToString(),
                    Unit = spec.Unit,
                    Hint = spec.Hint,
                    Path = WzPath.Child(parentPath, key),
                    WzType = property.PropertyType.ToString(),
                    Value = isContainer
                        ? $"{property.WzProperties!.Count} entries"
                        : FormatConstant(property),
                    Present = true,
                    Editable = !isContainer,
                });
            }
        }
    }

    #endregion

    #region Write

    /// <summary>
    /// Writes literal values into <c>level/N/key</c>, creating the level nodes it
    /// needs. One batch, so a screenful of edits is one Ctrl+Z.
    /// </summary>
    public SkillDetailDto WriteLevels(SkillLevelsWriteRequest request)
    {
        // A POST body with a null 'cells' deserialises to a null list, not an
        // empty one. Reaching .Count on it is an NRE and a 500 for what is really
        // "nothing to do".
        if (request.Cells is null || request.Cells.Count == 0)
            return Detail(request.Path);

        lock (_session.Gate)
        {
            WzImageProperty skill = ResolveSkill(request.Path);

            using IDisposable batch = _undo.Batch(
                request.Cells.Count == 1
                    ? $"Edit {request.Cells[0].Key} at level {request.Cells[0].Level}"
                    : $"Edit {request.Cells.Count} skill values");

            foreach (SkillCellWrite cell in request.Cells)
            {
                if (cell.Level <= 0)
                    throw new InvalidOperationException($"'{cell.Level}' is not a level number.");
                if (string.IsNullOrWhiteSpace(cell.Key))
                    throw new InvalidOperationException("A field name is required.");

                string levelPath = EnsureLevel(request.Path, cell.Level);
                WzImageProperty levelNode = ResolveProperty(levelPath);
                WzImageProperty? existing = Child(levelNode.WzProperties, cell.Key);

                if (existing != null)
                {
                    if (existing.WzValue is null && existing.WzProperties?.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"'{cell.Key}' holds a group of values, not one value. " +
                            "Open it in the Explorer to edit what is inside it.");
                    }
                    _edit.SetValue(WzPath.Child(levelPath, existing.Name ?? cell.Key), cell.Value);
                    continue;
                }

                _edit.Add(new AddNodeRequest
                {
                    Path = levelPath,
                    Name = cell.Key,
                    Type = TypeFor(skill, cell.Key, cell.Value),
                    Value = cell.Value,
                });
            }
        }

        return Detail(request.Path);
    }

    /// <summary>
    /// What WZ type a newly created level cell should be.
    ///
    /// The strongest signal is what the *same key at another level of the same
    /// skill* already is, and it is checked first: a v232 client stores
    /// <c>damage</c> as a String at some levels and an Int at others depending on
    /// the skill, so any rule that ignores the neighbours will disagree with them
    /// half the time and the client reads the field with a fixed accessor.
    ///
    /// Failing that, the catalog decides for a key it knows. For one it does not,
    /// the *value* decides — <see cref="SkillFieldCatalog.Unknown"/> reports Text,
    /// so trusting the catalog would create a WzStringProperty holding "4" where
    /// every other skill has a WzIntProperty. <c>MobService.WriteFields</c> and
    /// <c>CashShopService.WriteExtraField</c> infer the same way for the same
    /// reason.
    /// </summary>
    private static string TypeFor(WzImageProperty skill, string key, string? value)
    {
        string? neighbour = NeighbourType(skill, key);
        if (neighbour != null)
            return neighbour;

        SkillFieldSpec? spec = SkillFieldCatalog.Find(key);
        if (spec != null)
        {
            return spec.Kind switch
            {
                SkillFieldKind.Point => "Vector",
                SkillFieldKind.Text => "String",
                _ => "Int",
            };
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? "Int"
            : "String";
    }

    /// <summary>
    /// The WZ type the same key already uses at another level of this skill, or
    /// null when no level carries it. See <see cref="TypeFor"/> for why this wins.
    /// </summary>
    private static string? NeighbourType(WzImageProperty skill, string key)
    {
        WzImageProperty? levels = Child(skill.WzProperties, "level");
        if (levels?.WzProperties == null)
            return null;

        foreach (WzImageProperty level in levels.WzProperties)
        {
            WzImageProperty? sibling = Child(level.WzProperties, key);
            string? creatable = sibling == null ? null : Creatable(sibling.PropertyType);
            if (creatable != null)
                return creatable;
        }
        return null;
    }

    /// <summary>
    /// The <see cref="WzNodeFactory"/> type name for a scalar property type, or
    /// null for anything that is not a single value.
    /// </summary>
    private static string? Creatable(WzPropertyType type) => type switch
    {
        WzPropertyType.Int => "Int",
        WzPropertyType.Short => "Short",
        WzPropertyType.Long => "Long",
        WzPropertyType.Float => "Float",
        WzPropertyType.Double => "Double",
        WzPropertyType.String => "String",
        WzPropertyType.Vector => "Vector",
        WzPropertyType.UOL => "UOL",
        _ => null,
    };

    /// <summary>
    /// Adds, clones, removes or renumbers a <c>level/N</c> node.
    ///
    /// Every one of these goes through <see cref="WzEditService"/> rather than
    /// touching the collection, which is what makes them undoable in one step and
    /// keeps the resolution cache honest about the node that just appeared.
    /// </summary>
    public SkillDetailDto Level(SkillLevelRequest request)
    {
        lock (_session.Gate)
        {
            ResolveSkill(request.Path);
            string levelsPath = WzPath.Child(request.Path, "level");

            switch (request.Op?.ToLowerInvariant())
            {
                case "add":
                {
                    Require(request.Level, "level");
                    using IDisposable batch = _undo.Batch($"Add level {request.Level}");
                    EnsureLevelContainer(request.Path);
                    if (_session.TryResolve(LevelPath(levelsPath, request.Level)) != null)
                        throw new InvalidOperationException($"Level {request.Level} already exists.");

                    _edit.Add(new AddNodeRequest
                    {
                        Path = levelsPath,
                        Name = request.Level.ToString(CultureInfo.InvariantCulture),
                        Type = "SubProperty",
                    });
                    break;
                }

                case "clone":
                {
                    Require(request.From, "source level");
                    Require(request.Level, "new level");
                    string source = LevelPath(levelsPath, request.From);
                    if (_session.TryResolve(source) == null)
                        throw new InvalidOperationException($"Level {request.From} does not exist, so it cannot be copied.");
                    if (_session.TryResolve(LevelPath(levelsPath, request.Level)) != null)
                        throw new InvalidOperationException($"Level {request.Level} already exists.");

                    // Duplicate then rename, both through WzEditService, inside one
                    // batch: Duplicate names the copy "N copy" because that is what
                    // it does everywhere else in the app, and a level called
                    // "1 copy" is not a level. One undo entry covers both halves,
                    // so a Ctrl+Z cannot leave the copy behind under its interim
                    // name.
                    using IDisposable batch = _undo.Batch($"Copy level {request.From} to {request.Level}");
                    List<NodeDto> created = _edit.Duplicate(new[] { source });
                    if (created.Count == 0)
                        throw new InvalidOperationException($"Level {request.From} could not be copied.");
                    _edit.Rename(created[0].Path, request.Level.ToString(CultureInfo.InvariantCulture));
                    break;
                }

                case "remove":
                {
                    Require(request.Level, "level");
                    string target = LevelPath(levelsPath, request.Level);
                    if (_session.TryResolve(target) == null)
                        throw new InvalidOperationException($"Level {request.Level} does not exist.");

                    using IDisposable batch = _undo.Batch($"Remove level {request.Level}");
                    _edit.Delete(new[] { target });
                    break;
                }

                case "rename":
                {
                    Require(request.Level, "level");
                    Require(request.To, "new level");
                    string target = LevelPath(levelsPath, request.Level);
                    if (_session.TryResolve(target) == null)
                        throw new InvalidOperationException($"Level {request.Level} does not exist.");
                    if (_session.TryResolve(LevelPath(levelsPath, request.To)) != null)
                        throw new InvalidOperationException($"Level {request.To} already exists.");

                    using IDisposable batch = _undo.Batch($"Renumber level {request.Level} to {request.To}");
                    _edit.Rename(target, request.To.ToString(CultureInfo.InvariantCulture));
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unknown operation '{request.Op}'. Use add, clone, remove or rename.");
            }
        }

        return Detail(request.Path);
    }

    private static void Require(int level, string what)
    {
        if (level <= 0)
            throw new InvalidOperationException($"A {what} of {level} is not valid; levels start at 1.");
    }

    private static string LevelPath(string levelsPath, int level) =>
        WzPath.Child(levelsPath, level.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Bakes a skill's <c>common</c> formulas into literal <c>level/N</c> nodes.
    ///
    /// Dry run first, always: this is the one operation here that writes hundreds
    /// of nodes from one click, and rule 2 of the quality bar exists because the
    /// app this is modelled against does exactly that with no preview.
    ///
    /// It removes the <c>common</c> block afterwards by default, and that is not
    /// tidiness — it is the whole point. While the block exists the client
    /// computes from the formulas and never looks at <c>level/</c>, so a bake that
    /// left it in place would produce a perfectly correct level table that changes
    /// nothing in game. Losing <c>maxLevel</c> with it is safe: of the 911 skills
    /// a v232 client stores as literal levels, not one carries a maxLevel, so the
    /// client plainly takes the level count from the nodes themselves.
    /// </summary>
    public SkillExpandResultDto ExpandCommon(SkillExpandRequest request)
    {
        SkillExpandResultDto result = new();

        lock (_session.Gate)
        {
            WzImageProperty skill = ResolveSkill(request.Path);
            WzImageProperty? common = Child(skill.WzProperties, "common");
            if (common?.WzProperties == null || common.WzProperties.Count == 0)
                throw new InvalidOperationException("This skill has no 'common' block, so there is nothing to bake.");

            WzImageProperty? levels = Child(skill.WzProperties, "level");
            int maxLevel = request.Levels ?? ReadMaxLevel(common, levels) ?? 1;
            if (maxLevel <= 0)
                throw new InvalidOperationException("This skill declares no levels, so there is nothing to write.");
            if (maxLevel > MaxLevelRows)
            {
                result.Notes.Add($"maxLevel is {maxLevel}; only the first {MaxLevelRows} levels will be written.");
                maxLevel = MaxLevelRows;
            }

            string commonPath = WzPath.Child(request.Path, common.Name ?? "common");
            string levelsPath = WzPath.Child(request.Path, levels?.Name ?? "level");

            // The same namespace the table computes in, so a bake writes the
            // numbers the table showed. Without it "damage = 140+y" would bake
            // as unreadable while the preview beside it read fine.
            FormulaScope scope = ScopeFor(common, request.Variables);

            // Everything is computed first, whether or not it will be written, so a
            // dry run and a real run cannot disagree about what happens.
            List<(int Level, string Key, string Type, string Value, SkillExpandChangeDto Row)> writes = new();
            HashSet<int> levelsTouched = new();

            for (int level = 1; level <= maxLevel; level++)
            {
                WzImageProperty? levelNode = Child(levels?.WzProperties, level.ToString(CultureInfo.InvariantCulture));
                string levelPath = LevelPath(levelsPath, level);

                foreach (WzImageProperty entry in common.WzProperties)
                {
                    string key = entry.Name ?? "";
                    if (key.Length == 0 || string.Equals(key, "maxLevel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    SkillExpandChangeDto row = new()
                    {
                        Level = level,
                        Key = key,
                        Path = WzPath.Child(levelPath, key),
                    };
                    result.Changes.Add(row);

                    WzImageProperty? existing = Child(levelNode?.WzProperties, key);
                    row.Before = existing?.WzValue?.ToString();

                    if (entry.WzValue is null && entry.WzProperties?.Count > 0)
                    {
                        row.Skipped = true;
                        row.Reason = $"'{key}' is a group of values, not one value. Copy it in the Explorer.";
                        continue;
                    }
                    if (existing != null && !request.Overwrite)
                    {
                        row.Skipped = true;
                        row.Reason = "This level already holds a value here. Turn on Overwrite to replace it.";
                        continue;
                    }
                    if (existing != null && existing.WzValue is null && existing.WzProperties?.Count > 0)
                    {
                        row.Skipped = true;
                        row.Reason = "This level holds a group of values here; it will not be replaced by one.";
                        continue;
                    }

                    string type;
                    string value;

                    if (entry.WzValue is string expression)
                    {
                        double? computed = SkillFormulaEvaluator.Evaluate(expression, level, scope, out string? error);
                        if (computed != null)
                        {
                            value = computed.Value.ToString("0.##########", CultureInfo.InvariantCulture);
                            type = computed.Value == Math.Floor(computed.Value) ? "Int" : "Double";
                        }
                        else if (!SkillFormulaEvaluator.LooksLikeFormula(expression))
                        {
                            // Never was arithmetic -- 'action' holds an animation
                            // name. Copied across as the text it is.
                            value = expression;
                            type = "String";
                        }
                        else
                        {
                            // Never written as 0. A formula nobody could read must
                            // leave a hole the user can see, not a plausible number.
                            row.Skipped = true;
                            row.Reason = error;
                            continue;
                        }
                    }
                    else
                    {
                        // A Vector (lt/rb) or an Int -- the same at every level.
                        // Copied verbatim rather than evaluated.
                        value = FormatConstant(entry);
                        type = Creatable(entry.PropertyType) ?? "String";
                    }

                    // What the other levels already use still wins, so a bake does
                    // not introduce a type its own neighbours disagree with.
                    type = NeighbourType(skill, key) ?? type;

                    row.After = value;
                    row.WzType = type;
                    writes.Add((level, key, type, value, row));
                    levelsTouched.Add(level);
                }
            }

            if (request.DryRun)
            {
                Annotate(result, skill, request);
                return result;
            }

            using (IDisposable batch = _undo.Batch(
                       $"Bake {skill.Name} into {levelsTouched.Count} explicit levels"))
            {
                foreach ((int level, string key, string type, string value, SkillExpandChangeDto row) in writes)
                {
                    // Per row, because a throw here would abandon the loop with the
                    // earlier rows already written and Applied never assigned -- the
                    // request 500s, the user concludes nothing happened, and the
                    // archive holds a partial edit. WzEditService.SetValueMany sets
                    // the house pattern: never let one bad row decide the batch's
                    // fate, and report what actually landed.
                    try
                    {
                        string levelPath = EnsureLevel(request.Path, level);
                        WzImageProperty levelNode = ResolveProperty(levelPath);
                        WzImageProperty? existing = Child(levelNode.WzProperties, key);

                        if (existing != null)
                            _edit.SetValue(WzPath.Child(levelPath, existing.Name ?? key), value);
                        else
                            _edit.Add(new AddNodeRequest { Path = levelPath, Name = key, Type = type, Value = value });

                        result.Applied++;
                    }
                    catch (Exception ex)
                    {
                        row.Skipped = true;
                        row.After = null;
                        row.Reason = ex.Message;
                    }
                }

                if (request.RemoveCommon && result.Applied > 0)
                {
                    try
                    {
                        _edit.Delete(new[] { commonPath });
                        result.RemovedCommon = true;
                    }
                    catch (Exception ex)
                    {
                        result.Notes.Add(
                            "The level values were written, but the 'common' block could not be removed: " +
                            ex.Message + " The client will keep using the formulas until it is gone.");
                    }
                }
            }

            // Recomputed from the rows that actually landed, not from the plan.
            //
            // levelsTouched was filled during planning, before a byte moved. A run
            // where every row of level 40 threw still counted level 40 as written,
            // because nothing ever asked the write loop what it managed. The rows
            // know: a row that failed carries Skipped.
            result.LevelsWritten = writes
                .Where(w => !w.Item5.Skipped)
                .Select(w => w.Item1)
                .Distinct()
                .Count();

            Annotate(result, skill, request);
        }

        result.Detail = request.DryRun ? null : Detail(request.Path);
        return result;
    }

    /// <summary>
    /// The things the user has to know that are not per-row: what removing the
    /// block does, and what it does not touch.
    ///
    /// Every branch below that describes a completed action reads
    /// <see cref="SkillExpandResultDto.RemovedCommon"/> -- what happened -- and
    /// never <c>request.RemoveCommon</c>, what was asked for. Keying the note on
    /// the request produced a response that said "The 'common' block is gone; the
    /// client now reads the level values" in three cases where it was still there:
    /// the delete threw (and the catch had already said so, two contradicting
    /// notes in one response), every row failed so the delete was never attempted,
    /// and a dry run that was then never applied. The distinction matters more
    /// here than almost anywhere: while 'common' exists the client ignores the
    /// level values entirely, so the difference between the two notes is the
    /// difference between a bake that changed the game and one that changed
    /// nothing at all.
    /// </summary>
    private static void Annotate(
        SkillExpandResultDto result, WzImageProperty skill, SkillExpandRequest request)
    {
        if (request.DryRun)
        {
            result.Notes.Add(request.RemoveCommon
                ? "The 'common' block will be removed afterwards. Without that the client keeps reading the " +
                  "formulas and these level values would have no effect."
                : "The 'common' block is being kept, so the client will still compute every level from the " +
                  "formulas and ignore what was written below. Remove it when you are ready for the change to " +
                  "take effect.");
        }
        else if (result.RemovedCommon)
        {
            result.Notes.Add(
                "The 'common' block is gone; the client now reads the level values. maxLevel went with it, " +
                "which is how every level-based skill in a stock client is stored.");
        }
        else if (request.RemoveCommon)
        {
            // Asked for, did not happen. The catch above explains a delete that
            // threw; this covers the other route, where no row landed so the
            // delete was never reached -- which nothing said at all.
            result.Notes.Add(
                result.Applied > 0
                    ? "The 'common' block is still there, so the client keeps computing every level from the " +
                      "formulas and ignores the values just written. Nothing you can see in game has changed yet."
                    : "Nothing was written, so the 'common' block was left alone. The skill is exactly as it " +
                      "was.");
        }
        else
        {
            result.Notes.Add(
                "The 'common' block is being kept, so the client will still compute every level from the " +
                "formulas and ignore what was written below. Remove it when you are ready for the change to " +
                "take effect.");
        }

        if (Child(skill.WzProperties, "PVPcommon") != null)
        {
            result.Notes.Add(
                "This skill also has a 'PVPcommon' block, which is left alone — it only applies in PvP maps " +
                "and has its own formulas.");
        }
    }

    private static string FormatConstant(WzImageProperty property)
    {
        // A Vector's ToString is "{X=-339,Y=-290}", which WzNodeFactory.ParseVector
        // does not accept. Its X/Y are read directly instead, invariant-formatted,
        // in the "x, y" form that factory does accept.
        if (property.PropertyType == WzPropertyType.Vector)
        {
            int x = 0, y = 0;
            if (property.WzProperties != null)
            {
                foreach (WzImageProperty part in property.WzProperties)
                {
                    if (string.Equals(part.Name, "X", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(part.WzValue?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
                    else if (string.Equals(part.Name, "Y", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(part.WzValue?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
                }
            }
            return $"{x.ToString(CultureInfo.InvariantCulture)}, {y.ToString(CultureInfo.InvariantCulture)}";
        }
        return property.WzValue?.ToString() ?? "";
    }

    /// <summary>
    /// Arithmetic over one field across many skills, previewed first.
    ///
    /// Shaped exactly like <c>MobService.Bulk</c>, including the skip reasons and
    /// the rounding choices, because the two are the same operation over different
    /// nouns and a user who has learned one should not have to learn the other.
    /// The skill-specific part is <see cref="SkillBulkRequest.Level"/>: level 0
    /// means the <c>common</c> value and level N means <c>level/N</c>, and a
    /// <c>common</c> value that is an expression is refused rather than mangled.
    /// </summary>
    public SkillBulkResultDto Bulk(SkillBulkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Field))
            throw new InvalidOperationException("No field was chosen.");

        SkillBulkResultDto result = new();

        lock (_session.Gate)
        {
            // Everything is computed first, whether or not it will be written, so
            // a dry run and a real run cannot disagree about what happens.
            List<(string FieldPath, string After, SkillBulkChangeDto Row)> writes = new();

            foreach (string path in request.Paths ?? new List<string>())
            {
                SkillBulkChangeDto change = new() { Path = path };
                result.Changes.Add(change);

                WzImageProperty skill;
                try
                {
                    skill = ResolveSkill(path);
                }
                catch (Exception ex)
                {
                    change.Skipped = true;
                    change.Reason = ex.Message;
                    continue;
                }

                TryId(skill.Name, out int skillId);
                change.SkillId = skillId;
                change.Name = _strings.GetSkillName(skillId);

                WzImageProperty? container;
                string containerPath;
                if (request.Level <= 0)
                {
                    container = Child(skill.WzProperties, "common");
                    containerPath = WzPath.Child(path, container?.Name ?? "common");
                }
                else
                {
                    WzImageProperty? levels = Child(skill.WzProperties, "level");
                    string levelsPath = WzPath.Child(path, levels?.Name ?? "level");
                    string number = request.Level.ToString(CultureInfo.InvariantCulture);
                    container = Child(levels?.WzProperties, number);
                    containerPath = WzPath.Child(levelsPath, number);
                }

                if (container == null)
                {
                    change.Skipped = true;
                    change.Reason = request.Level <= 0
                        ? "This skill has no 'common' block; pick a level instead."
                        : $"This skill has no level {request.Level}.";
                    continue;
                }

                WzImageProperty? property = Child(container.WzProperties, request.Field);
                if (property == null)
                {
                    // Deliberately not created here. Bulk edit is for changing what
                    // exists; silently adding a field to a thousand skills because a
                    // dropdown offered it is not a thing to do by accident.
                    change.Skipped = true;
                    change.Reason = "This skill has no such field.";
                    continue;
                }

                if (property.WzValue is null && property.WzProperties?.Count > 0)
                {
                    change.Skipped = true;
                    change.Reason = $"'{request.Field}' holds a group of values, not one value.";
                    continue;
                }

                string? before = FormatConstant(property);
                change.Before = before;

                if (property.PropertyType == WzPropertyType.Vector)
                {
                    change.Skipped = true;
                    change.Reason = "This is a point (x, y), not a single number.";
                    continue;
                }
                if (SkillFormulaEvaluator.ReferencesLevel(before))
                {
                    // The honest refusal. Applying "+10" to "235+3*x" by string
                    // surgery would produce something that still parses and means
                    // something else at every level but one.
                    change.Skipped = true;
                    change.Reason =
                        $"'{before}' is a formula over the skill level, not a number. " +
                        "Bake it into explicit levels first, or edit the formula itself.";
                    continue;
                }
                if (!double.TryParse(before, NumberStyles.Any, CultureInfo.InvariantCulture, out double current))
                {
                    change.Skipped = true;
                    // Separated from plain text, because "140+y" and "slashStorm2"
                    // are both "not a number" and only one of them is something the
                    // user meant to bulk-edit. Telling them apart is the difference
                    // between a report they can act on and a wall of the same line.
                    change.Reason = SkillFormulaEvaluator.LooksLikeFormula(before)
                        ? $"'{before}' is a formula, not a number. Bake it into explicit levels " +
                          "first, or edit the formula itself."
                        : "The current value is not a number.";
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
                writes.Add((WzPath.Child(containerPath, property.Name ?? request.Field), formatted, change));
            }

            if (!request.DryRun && writes.Count > 0)
            {
                string where = request.Level <= 0 ? "common" : $"level {request.Level}";
                using IDisposable batch = _undo.Batch(
                    $"{request.Op} {request.Field} ({where}) on {writes.Count} skills");

                // Per row; same reason as everywhere else in this file.
                // See MobService.Bulk: the row rides along in the tuple instead
                // of being re-found by a linear scan per write.
                foreach ((string fieldPath, string after, SkillBulkChangeDto row) in writes)
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

    /// <summary>Open archives that could hold skills: Skill.wz, Skill001.wz, Skill2.wz...</summary>
    private List<OpenFile> SkillArchives(string? fileId)
        => _session.SelectRoleSources("Skill", fileId);

    /// <summary>
    /// Book images, at the archive root and one level down. A v232 Skill.wz keeps
    /// all 234 at the root, but clients differ about whether they group them and
    /// both shapes turn up in the wild — the same rule <see cref="MobService"/>
    /// uses for the same reason.
    /// </summary>
    private static IEnumerable<(WzImage Image, string Path)> EnumerateBookImages(WzDirectory root, string fileId)
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

    /// <summary>
    /// Resolves a skill path — <c>f1/522.img/skill/5221017</c> — and refuses
    /// anything that is not one, by name rather than by shape: a path that lands
    /// on <c>level/3</c> is also a WzSubProperty and would otherwise be accepted
    /// as a skill whose fields all happen to be missing.
    /// </summary>
    private WzImageProperty ResolveSkill(string path)
    {
        WzObject node = _session.Resolve(path);
        if (node is not WzImageProperty property)
            throw new InvalidOperationException($"'{path}' is not a skill.");

        string? parent = WzPath.Parent(path);
        string? parentName = parent == null ? null : WzPath.Split(parent).LastOrDefault();
        if (!string.Equals(parentName, "skill", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{path}' is not a skill. A skill lives under <book>.img/skill/<id>.");
        }

        WzImage? image = property.ParentImage;
        if (image != null)
            WzSessionService.EnsureParsed(image);
        return property;
    }

    private WzImageProperty ResolveProperty(string path)
        => _session.Resolve(path) as WzImageProperty
           ?? throw new InvalidOperationException($"'{path}' is not a property.");

    /// <summary>Creates the <c>level</c> container if the skill has none.</summary>
    private void EnsureLevelContainer(string skillPath)
    {
        string levelsPath = WzPath.Child(skillPath, "level");
        if (_session.TryResolve(levelsPath) != null)
            return;

        _edit.Add(new AddNodeRequest { Path = skillPath, Name = "level", Type = "SubProperty" });
    }

    /// <summary>
    /// Creates <c>level/N</c> if it is missing, and returns its path.
    /// Both halves go through <see cref="WzEditService"/>, so the creation is part
    /// of the caller's undo batch rather than a side effect that survives a Ctrl+Z.
    /// </summary>
    private string EnsureLevel(string skillPath, int level)
    {
        EnsureLevelContainer(skillPath);

        string levelsPath = WzPath.Child(skillPath, "level");
        string levelPath = LevelPath(levelsPath, level);
        if (_session.TryResolve(levelPath) != null)
            return levelPath;

        _edit.Add(new AddNodeRequest
        {
            Path = levelsPath,
            Name = level.ToString(CultureInfo.InvariantCulture),
            Type = "SubProperty",
        });
        return levelPath;
    }

    /// <summary>A child by name, case-insensitively. Null-safe on both sides.</summary>
    private static WzImageProperty? Child(WzPropertyCollection? properties, string name) =>
        properties?.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? Text(WzImageProperty? container, string key)
    {
        string? value = Child(container?.WzProperties, key)?.WzValue?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>"5221017" -> 5221017. Leading zeros are the norm ("0001298").</summary>
    private static bool TryId(string? name, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name))
            return false;

        ReadOnlySpan<char> stem = name.AsSpan();
        if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>"522.img" -> "522". This is the key String.wz names a book by.</summary>
    private static string Stem(string name) =>
        name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    #endregion
}
