namespace MapleBench.Services;

/// <summary>
/// WZ node names turned into file names that Windows will actually accept,
/// without two different nodes quietly becoming one file.
///
/// <see cref="ExportService.Sanitise"/> already does the character work — it is
/// the one place that knows about <c>NUL.png</c>, about ".." as a traversal
/// segment and about Windows dropping trailing dots — and this does not
/// reimplement it. What it adds is the half that only matters once a whole
/// subtree is being written at once: **sanitising is lossy, and the loss is not
/// theoretical here.** The map corpus measured in <c>docs/map-data-model.md</c>
/// holds <c>info/speedMaxOver </c> (11 maps) alongside <c>info/speedMaxOver</c>
/// (89 maps) as two different keys with two different values, and Windows
/// cannot hold both under those names. So can <c>a:b</c> and <c>a_b</c>, once
/// the colon is rewritten.
///
/// The rule is that a collision is never resolved by overwriting and never
/// resolved silently. The second arrival gets a suffix, and the caller is told
/// which name it really wanted — because a folder of PNGs whose names no longer
/// match the WZ is only useful if something says so.
/// </summary>
internal sealed class DumpNames
{
    private readonly Dictionary<string, int> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A file name for one node inside one folder, with its extension.
    ///
    /// <paramref name="issue"/> is non-null when the name on disk is not the
    /// name in the WZ — either because a character had to be rewritten or
    /// because it collided with a sibling that got there first.
    /// </summary>
    internal string For(string nodeName, string extension, out DumpIssueDto? issue)
    {
        issue = null;
        string cleaned = ExportService.Sanitise(nodeName);

        string candidate = cleaned + extension;
        if (_used.TryAdd(candidate, 1))
        {
            // Windows-safe but not the WZ's name. Worth saying even without a
            // collision: a script that reads the dump back and looks for the key
            // it saw in the editor will not find it.
            if (!string.Equals(cleaned, nodeName, StringComparison.Ordinal))
            {
                issue = new DumpIssueDto
                {
                    Kind = "name.sanitised",
                    Reason = $"{Describe(nodeName)} cannot be a file name here; written as '{candidate}'.",
                };
            }
            return candidate;
        }

        // A stable, obvious disambiguator rather than a hash: the point is that
        // a human reading the folder sees there were two.
        int next = _used[candidate];
        string disambiguated;
        do
        {
            next++;
            disambiguated = $"{cleaned}~{next}{extension}";
        }
        while (!_used.TryAdd(disambiguated, 1));
        _used[candidate] = next;

        issue = new DumpIssueDto
        {
            Kind = "name.collision",
            Reason =
                $"{Describe(nodeName)} would land on '{candidate}', which a different node already took. " +
                $"Written as '{disambiguated}' instead — nothing was overwritten.",
        };
        return disambiguated;
    }

    /// <summary>
    /// A folder name for a container. Same rules; kept separate so the extension
    /// argument cannot be forgotten and a folder cannot collide with a file.
    /// </summary>
    internal string ForFolder(string nodeName, out DumpIssueDto? issue)
        => For(nodeName, "", out issue);

    /// <summary>
    /// A quoted name, with a plain-words warning when the difference between it
    /// and the name it collided with is whitespace nobody can see. That is not a
    /// hypothetical: <c>info/speedMaxOver </c> and <c>info/speedMaxOver</c> are
    /// two live keys with two different types, and a report that renders both as
    /// 'speedMaxOver' explains nothing.
    /// </summary>
    private static string Describe(string name)
        => name != name.Trim()
            ? $"'{name}' (note the surrounding whitespace)"
            : $"'{name}'";
}
