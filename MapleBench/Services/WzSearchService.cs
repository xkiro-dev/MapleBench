using System.Diagnostics;
using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// Name and value search across the open archives, plus batch find-and-replace.
///
/// Value matching has to parse each image it visits, which is the expensive part
/// of any WZ search, so results are bounded by both a hit limit and a wall-clock
/// budget and the response says when it stopped early.
/// </summary>
public sealed class WzSearchService
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The longest a search holds the session gate in one go.
    ///
    /// This is the number the rest of the app feels: while a search runs, every
    /// other request waits at most about this long plus the handoff. Fifteen
    /// milliseconds is under a frame, and the price is one stand-down per slice.
    /// </summary>
    private const int SliceMs = 15;

    /// <summary>
    /// How long a walk runs before it starts slicing at all.
    ///
    /// A stand-down is only worth its cost when there is something to stand down
    /// from. A name-only search over 29 archives visits 97,000 nodes and finishes
    /// in about a quarter of a second -- nobody is waiting long enough to notice,
    /// and slicing it from the first node cost 48% of its runtime in handoffs
    /// (235 ms to 347 ms, measured). Past this mark the walk is long enough to be
    /// a freeze, and from there it yields every <see cref="SliceMs"/>.
    /// </summary>
    private const int GraceMs = 200;

    /// <summary>
    /// Ceiling on any single regex match.
    ///
    /// The pattern comes from the user and runs while the global session lock
    /// is held, so a catastrophically backtracking one like "(a+)+$" would not
    /// merely be slow -- it would wedge every other request in the app for
    /// good. A timeout turns that into an error message.
    /// </summary>
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Builds a user-supplied pattern with a hard time limit and a readable failure.</summary>
    private static Regex BuildRegex(string pattern, bool literal, bool caseSensitive)
    {
        RegexOptions options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            return new Regex(literal ? Regex.Escape(pattern) : pattern, options, RegexBudget);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{pattern}' is not a valid regular expression: {ex.Message}");
        }
    }

    private readonly WzSessionService _session;
    private readonly WzEditService _edit;
    private readonly UndoService _undo;
    private readonly StringPoolService _strings;

    public WzSearchService(WzSessionService session, WzEditService edit, UndoService undo,
                           StringPoolService strings)
    {
        _session = session;
        _edit = edit;
        _undo = undo;
        _strings = strings;
    }

    public (List<SearchHitDto> Hits, bool Truncated, int Scanned) Search(
        SearchRequest request, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(request.Query))
            return (new List<SearchHitDto>(), false, 0);

        // Structured predicates ("name:maxHP value:>10000") are ANDed; a plain
        // query keeps the old name-OR-value behaviour exactly, so nothing that
        // used to be typed here changes meaning. Regex is deliberately left as
        // the escape hatch it was: a pattern is one needle by definition, so
        // mixing it with prefixes would only create ways to be surprised.
        SearchQuery.Parsed parsed = request.Regex
            ? new SearchQuery.Parsed { Free = request.Query }
            : SearchQuery.Parse(request.Query);

        Func<string?, bool> matchFree = BuildMatcher(parsed.Free ?? "", request.Regex, request.CaseSensitive);
        Func<string?, bool> matchName = BuildMatcher(parsed.Name ?? "", false, request.CaseSensitive);
        Func<string?, bool> matchValue = BuildMatcher(parsed.Value ?? "", false, request.CaseSensitive);

        HashSet<string>? typeFilter = request.Types is { Count: > 0 }
            ? new HashSet<string>(request.Types, StringComparer.OrdinalIgnoreCase)
            : null;

        // Read before the gate is taken, never inside it. IsAvailable blocks on
        // the pool build, and the build reads the session -- asking for it while
        // holding the gate is the deadlock this ordering prevents. NameFor
        // itself is the non-blocking accessor and is safe below.
        bool namesReady = _strings.IsAvailable;

        // Which archive a hit came from. The context line drops the file id on
        // purpose (it is a session-local "f2" and means nothing to a reader), but
        // dropping it entirely left three open archives indistinguishable in the
        // results -- so the archive's real name is carried alongside instead.
        Dictionary<string, string> fileNames = _session.Files.ToDictionary(f => f.Id, f => f.Name);

        List<SearchHitDto> hits = new();
        Stopwatch clock = Stopwatch.StartNew();
        int scanned = 0;
        bool truncated = false;

        // The tree this search describes, fixed before the first node is read.
        //
        // A search no longer holds the session gate from the first node to the
        // last -- see Walk -- so the tree it is walking can be edited underneath
        // it. Results stitched together from two different trees are the one
        // thing a slow search must not be traded for, so the generation is
        // stamped here and re-checked at every slice boundary, and a walk that
        // sees it move stops and reports itself truncated rather than carrying
        // on. Truncated is exactly what the UI already renders as "there may be
        // more"; it is not a claim to have swept the archive.
        int generation = _session.Generation;

        foreach (string rootPath in ResolveRoots(request.Path))
        {
            if (truncated)
                break;

            // Structured queries always go deep. "name:maxHP" is a question
            // about properties, and every property in the game lives inside
            // an image -- a shallow walk lists the images and never opens
            // one, so the query would confidently return nothing. The
            // 20-second budget above is what keeps that affordable.
            // A walk the guard cut short has not covered the root, so the
            // result is truncated whether or not the hit limit was reached.
            bool complete = Walk(rootPath, request.MatchValues || parsed.IsStructured, generation, node =>
            {
                // Checked per node rather than per root: a single Map.wz
                // root is minutes of work on its own.
                cancel.ThrowIfCancellationRequested();
                scanned++;
                if (hits.Count >= request.Limit || clock.Elapsed > Budget)
                {
                    truncated = true;
                    return false;
                }

                NodeDto dto = node.Dto;
                if (typeFilter != null && (dto.Type == null || !typeFilter.Contains(dto.Type)))
                    return true;

                // Resolved once per candidate and reused for both matching
                // and display, so "blue snail" can find 0100101.img -- the
                // tree has shown that alias since round one, and the search
                // was the one place you still had to know the number.
                string? alias = namesReady ? _strings.NameFor(dto.Path, dto.Name) : null;

                if (!MatchesPredicates(parsed, dto, alias, request, matchFree, matchName, matchValue))
                    return true;

                hits.Add(new SearchHitDto
                {
                    Path = dto.Path,
                    Name = dto.Name,
                    DisplayName = alias,
                    Type = dto.Type,
                    Value = dto.Value,
                    Editable = dto.Editable,
                    File = fileNames.GetValueOrDefault(WzPath.FileId(dto.Path)),
                    Context = BuildContext(dto.Path),
                });
                return true;
            });

            truncated |= !complete;
        }

        return (hits, truncated, scanned);
    }

    /// <summary>
    /// Replaces text in property values (and optionally names) beneath a root.
    /// A dry run reports the same hits without mutating anything, which is what
    /// the UI shows for confirmation before committing.
    /// </summary>
    public (List<SearchHitDto> Changed, bool Truncated) Replace(
        ReplaceRequest request, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(request.Find))
            return (new List<SearchHitDto>(), false);

        Regex pattern = BuildRegex(request.Find, literal: !request.Regex, request.CaseSensitive);

        // In literal mode the replacement has to be literal too. "$&", "$1" and
        // "$$" are substitution tokens to Regex.Replace, so a plain find-and-
        // replace of "100" -> "$100" would otherwise insert something else.
        string replacement = request.Regex ? request.Replace : request.Replace.Replace("$", "$$");

        // Same map, same reason, as Search: a replace with no Path runs over
        // every open archive at once, and its hits are what the confirmation
        // dialog shows before anything is written. Leaving File null there meant
        // the one list that has to say which archive it is about to rewrite was
        // the one list that could not.  Read before the gate, as in Search.
        Dictionary<string, string> fileNames = _session.Files.ToDictionary(f => f.Id, f => f.Name);

        List<SearchHitDto> changed = new();
        List<(string Path, string Value)> pending = new();
        List<(string Path, string Name)> renames = new();
        bool truncated = false;

        lock (_session.Gate)
        {
            foreach (string rootPath in ResolveRoots(request.Path))
            {
                if (truncated)
                    break;

                // generation: null -- do not slice. See Walk.
                bool complete = Walk(rootPath, deep: true, generation: null, visit: node =>
                {
                    // Safe to abandon: nothing has been written yet. The writes
                    // below deliberately do not check.
                    cancel.ThrowIfCancellationRequested();

                    if (changed.Count >= request.Limit)
                    {
                        truncated = true;
                        return false;
                    }

                    NodeDto dto = node.Dto;

                    if (request.InValues && dto.Editable && dto.Value != null && SafeMatch(pattern, dto.Value))
                    {
                        string replaced = pattern.Replace(dto.Value, replacement);
                        if (replaced != dto.Value)
                        {
                            changed.Add(new SearchHitDto
                            {
                                Path = dto.Path,
                                Name = dto.Name,
                                Type = dto.Type,
                                Value = $"{dto.Value}  ->  {replaced}",
                                File = fileNames.GetValueOrDefault(WzPath.FileId(dto.Path)),
                                Context = BuildContext(dto.Path),
                            });
                            pending.Add((dto.Path, replaced));
                        }
                    }

                    if (request.InNames && SafeMatch(pattern, dto.Name))
                    {
                        string replaced = pattern.Replace(dto.Name, replacement);
                        if (replaced != dto.Name && replaced.Length > 0)
                        {
                            changed.Add(new SearchHitDto
                            {
                                Path = dto.Path,
                                Name = dto.Name,
                                Type = dto.Type,
                                Value = $"name: {dto.Name}  ->  {replaced}",
                                File = fileNames.GetValueOrDefault(WzPath.FileId(dto.Path)),
                                Context = BuildContext(dto.Path),
                            });
                            renames.Add((dto.Path, replaced));
                        }
                    }
                    return true;
                });

                truncated |= !complete;
            }

            if (!request.DryRun)
            {
                // One undo entry for the whole replace. Without this each hit
                // recorded its own, and a few hundred of them pushed the entire
                // history past its depth limit — while the confirmation dialog
                // promised "this is recorded as one undo step".
                using IDisposable batch = _undo.Batch($"Replace '{request.Find}'");

                foreach ((string path, string value) in pending)
                {
                    try { _edit.SetValue(path, value); }
                    catch { /* type rejected the replacement; already reported as a hit */ }
                }
                // Renames last: they invalidate the paths collected above.
                foreach ((string path, string name) in renames.OrderByDescending(r => r.Path.Count(c => c == '/')))
                {
                    try { _edit.Rename(path, name); }
                    catch { /* name clash; skip */ }
                }
            }
        }

        return (changed, truncated);
    }

    /// <summary>
    /// Every predicate the query carries, ANDed.
    ///
    /// The three clauses are independent on purpose. `name:` alone answers
    /// "every node called maxHP", a bare `&gt;10000` alone answers "every number
    /// over 10000", and together they answer the question the tool existed to
    /// be asked and could not be: "every maxHP over 10000". The free-text clause
    /// keeps the historic OR so an unprefixed query behaves as it always did.
    /// </summary>
    private static bool MatchesPredicates(
        SearchQuery.Parsed parsed, NodeDto dto, string? alias, SearchRequest request,
        Func<string?, bool> matchFree, Func<string?, bool> matchName, Func<string?, bool> matchValue)
    {
        // The alias counts as a name. It is not a second field to the user --
        // the tree renders it on the same row as the name -- so it must not
        // need a second search to find.
        if (parsed.Name != null && !(matchName(dto.Name) || (alias != null && matchName(alias))))
            return false;

        if (parsed.Value != null && !matchValue(dto.Value))
            return false;

        if (!SearchQuery.Matches(parsed.Op, parsed.Operand, dto.Value))
            return false;

        if (parsed.Free != null)
        {
            bool free = (request.MatchNames && (matchFree(dto.Name) || (alias != null && matchFree(alias))))
                     || (request.MatchValues && matchFree(dto.Value));
            if (!free)
                return false;
        }

        // A query made only of predicates ("name:maxHP") has no free text, and
        // must not therefore match everything. IsEmpty is the only case that
        // legitimately matches nothing, and Search() returns before reaching here.
        return true;
    }

    /// <summary>A match that treats "too slow" as "no match" rather than an abort.</summary>
    private static bool SafeMatch(Regex pattern, string value)
    {
        try { return pattern.IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private List<string> ResolveRoots(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            return new List<string> { path };
        return _session.Files.Select(f => f.Id).ToList();
    }

    /// <summary>
    /// Depth-first walk yielding a DTO per node.  When <paramref name="deep"/>
    /// is false, images are listed but not parsed, so a name-only search over a
    /// whole archive stays fast.
    ///
    /// Descends through <see cref="WzWalk"/>, which it did not before, and this
    /// is the walk that had the most to lose by not doing so.  It is iterative,
    /// so a link that resolves to its own ancestor did not overflow the stack and
    /// kill the process -- it SPUN, allocating a path four characters longer on
    /// every step (<c>.../0/uol/uol/uol/...</c>) until the 20-second budget
    /// expired, and it spun holding the global session gate, so one search that
    /// happened to match nothing wedged every other request in the editor for the
    /// full 20 seconds and then reported itself as merely truncated.  Reactor
    /// 2208004.img ships exactly that link in v232 and v233.
    ///
    /// Replace walks through here too, and it WRITES.  Following a link as
    /// structure there meant rewriting values and renaming nodes in whatever
    /// image the link pointed into, under paths naming this one.
    ///
    /// Returns false when the guard cut something short, so a caller can report
    /// the result as incomplete instead of as a clean sweep.
    /// </summary>
    /// <param name="generation">
    /// The tree the caller is describing, or null to run the whole walk in one
    /// gate hold because the caller is already holding it.
    ///
    /// Non-null slices the hold: the walk takes the gate, works for at most
    /// <see cref="SliceMs"/>, releases it, stands aside for a scheduling
    /// quantum and takes it again, re-checking this number every time. That is
    /// what stops a search being a freeze. Measured on a v232 client (29
    /// archives, 4.05M nodes visited), a value search that runs out its
    /// 20-second budget used to hold the session gate for all 20 seconds --
    /// every browse, thumbnail, inspector and edit in the app waited for the
    /// whole of it, which is why the one feature you are most likely to leave
    /// running was also the one that stopped everything else.
    ///
    /// Null is <see cref="Replace"/>'s answer, and it is deliberate rather than
    /// lazy. A replace collects paths and then writes to them; releasing the
    /// gate in between would let an add, a delete or a reorder land in the gap
    /// and leave those paths naming different nodes than the ones the user
    /// confirmed. A replace that takes a moment is fine. A replace that writes
    /// somewhere else is not.
    /// </param>
    private bool Walk(string rootPath, bool deep, int? generation,
                      Func<(NodeDto Dto, WzObject Node), bool> visit)
    {
        WzObject? root = _session.TryResolve(rootPath);
        if (root == null)
            return true;

        // One guard per walk: the visited set is what makes it correct, and it
        // must not outlive the tree it was built for.
        WzWalk walk = new();
        Stack<(WzObject Node, string Path, int Depth)> stack = new();
        stack.Push((root, rootPath, 0));

        bool sliced = generation.HasValue;
        Stopwatch clock = Stopwatch.StartNew();
        long releaseAt = GraceMs;
        bool done = false;

        while (stack.Count > 0 && !done)
        {
            lock (_session.Gate)
            {
                // The tree moved between slices, so everything below this point
                // would describe a different archive than everything above it.
                // Nothing partial is kept and nothing is patched up: the caller is
                // told the walk did not cover the root.
                if (sliced && _session.Generation != generation!.Value)
                    return false;

                while (stack.Count > 0)
                {
                    (WzObject node, string path, int depth) = stack.Pop();

                    // An unparsed image is a leaf unless the caller asked to go deep.
                    if (node is WzImage image && !deep && !image.Parsed)
                        continue;

                    // A link, a node already walked, or 256 levels down: whatever is
                    // under it is not under it as tree structure, and the nodes that
                    // really are reachable are reached at their own paths anyway.
                    if (!walk.Enter(node, depth))
                        continue;

                    Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
                    List<WzObject> children;
                    try
                    {
                        children = _session.EnumerateChildren(node).ToList();
                    }
                    catch
                    {
                        // A malformed image shouldn't abort the whole search.
                        continue;
                    }

                    foreach (WzObject child in children)
                    {
                        string name = child.Name ?? "";
                        seen.TryGetValue(name, out int occurrence);
                        seen[name] = occurrence + 1;
                        string childPath = WzPath.Child(path, name, occurrence);

                        if (!visit((_session.ToDto(child, childPath), child)))
                        {
                            done = true;
                            break;
                        }

                        stack.Push((child, childPath, depth + 1));
                    }

                    // Time, not a node count, is what bounds the hold. One node is
                    // a name comparison or a whole image parsed out of the archive
                    // depending on where the walk is, so a fixed number of them per
                    // hold would be tens of microseconds in String.wz and seconds in
                    // Skill.wz -- and it is the milliseconds that every other
                    // request in the app is waiting on.
                    if (done || (sliced && clock.ElapsedMilliseconds >= releaseAt))
                        break;
                }
            }

            // Outside the gate, and a sleep rather than a yield, for the reason
            // WzSessionService.TryRunChunked gives at length: Monitor is not fair,
            // and a thread that releases and immediately re-asks wins against a
            // request that has been waiting the whole time.
            if (sliced && !done && stack.Count > 0)
            {
                Thread.Sleep(1);
                releaseAt = clock.ElapsedMilliseconds + SliceMs;
            }
        }

        return !walk.Stopped;
    }

    private static Func<string?, bool> BuildMatcher(string query, bool useRegex, bool caseSensitive)
    {
        if (useRegex)
        {
            Regex pattern = BuildRegex(query, literal: false, caseSensitive);
            return value =>
            {
                if (value == null) return false;
                // A timeout means this one value was too expensive, not that
                // the whole search should die.
                try { return pattern.IsMatch(value); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return value => value != null && value.Contains(query, comparison);
    }

    /// <summary>
    /// Ancestor chain without the file id, for display under a hit.
    ///
    /// The leading segment is dropped rather than swapped for the archive's real
    /// name, and <see cref="SearchHitDto.File"/> carries that name instead. Two
    /// reasons. The archive is an attribute of the hit, not a step in the chain,
    /// so it belongs in a field a caller can read, group and sort by -- putting
    /// it at the head of a display string means parsing it back out, which is
    /// precisely the client-side hack this replaces. And every renderer already
    /// draws File beside Context ("Etc.wz  ScrollIcon.img"), so a name at the
    /// head of the chain would print the archive twice per row.
    ///
    /// The short-path branch this replaces did NOT drop the id: a hit on a
    /// direct child of the archive root ("f1/ScrollIcon.img", two segments) fell
    /// into `string.Join(segments)` and read "f1 / ScrollIcon.img", and the file
    /// root itself read "f1". Whole archives -- Etc.wz, Sound.wz -- store their
    /// images directly under the root, so for those every single hit leaked the
    /// session id. There is nothing above a top-level image except the archive,
    /// so the honest context for one is empty, not the handle.
    /// </summary>
    private static string BuildContext(string path)
    {
        string[] segments = WzPath.Split(path);
        // Skip(1): the session file id. The last segment is the node itself,
        // which the hit already names, so it is not part of its context.
        return string.Join(" / ", segments.Skip(1).Take(Math.Max(0, segments.Length - 2)));
    }
}
