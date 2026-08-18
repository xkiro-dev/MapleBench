using System.Text;

namespace MapleBench.Services;

/// <summary>
/// Session paths look like <c>f1/Skill.wz/000.img/skill/1000/level/1/damage</c>.
///
/// The first segment is the session file id; the rest are node names.  WZ allows
/// sibling nodes to share a name, so a segment may carry a <c>#n</c> occurrence
/// suffix ("delay#2") to address the n-th duplicate.  Names that genuinely
/// contain '/' or '#' are percent-encoded by the client, so segments are
/// unescaped before use.
/// </summary>
public static class WzPath
{
    /// <summary>
    /// Segments with their escaping intact.  Resolution has to use this rather
    /// than <see cref="Split"/>: a node genuinely named "delay#2" escapes to
    /// "delay%232", and unescaping it before the occurrence suffix is read turns
    /// it back into a request for the third "delay".
    /// </summary>
    public static string[] SplitRaw(string path)
        => string.IsNullOrEmpty(path)
            ? Array.Empty<string>()
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Segments as display names: unescaped, suffix included.</summary>
    public static string[] Split(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Array.Empty<string>();

        string[] raw = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] result = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            result[i] = Uri.UnescapeDataString(raw[i]);
        return result;
    }

    public static string Join(params string[] segments)
    {
        StringBuilder sb = new();
        foreach (string segment in segments)
        {
            if (sb.Length > 0)
                sb.Append('/');
            sb.Append(Escape(segment));
        }
        return sb.ToString();
    }

    public static string Child(string parentPath, string childName, int occurrence = 0)
    {
        // Escape the name first, then append the suffix: escaping afterwards
        // would encode the '#' that makes the suffix a suffix.
        string segment = occurrence > 0 ? $"{Escape(childName)}#{occurrence}" : Escape(childName);
        return string.IsNullOrEmpty(parentPath) ? segment : parentPath + "/" + segment;
    }

    public static string? Parent(string path)
    {
        int index = path.LastIndexOf('/');
        return index <= 0 ? null : path[..index];
    }

    /// <summary>
    /// The session file id a path names — the same one <see cref="SplitRaw"/>
    /// hands to <c>WzSessionService.Resolve</c>.
    ///
    /// Built on <see cref="SplitRaw"/> and not on <c>IndexOf('/')</c>, because
    /// the two disagreed about a leading slash and that disagreement was the
    /// whole read-only guarantee. <c>"/f1/Data.img/keep"</c>: <c>IndexOf</c>
    /// returned 0, so the id was the empty string, so <c>GetFile("")</c> threw
    /// <c>KeyNotFoundException</c> — which <c>WzEditService.EnsureWritable</c>
    /// catches and treats as "no such file, nothing to check", i.e. fail-open.
    /// <c>Resolve</c> splits with <c>RemoveEmptyEntries</c> and found the node
    /// perfectly well. So one leading character turned every one of the nine
    /// guarded mutations into an unguarded one against an archive the user had
    /// opened for reference only — and <c>MarkFileDirty</c>, which looks the file
    /// up the same way, swallowed the same exception and left it unflagged.
    ///
    /// Anything deriving a file id must therefore split the way resolution
    /// splits. That is the invariant, not "trim a slash first".
    /// </summary>
    public static string FileId(string path)
    {
        string[] segments = SplitRaw(path);
        return segments.Length == 0 ? "" : Uri.UnescapeDataString(segments[0]);
    }

    /// <summary>
    /// Splits "delay#2" into ("delay", 2) and unescapes the name.  A missing
    /// suffix means the first node with that name.
    ///
    /// Takes a raw segment (from <see cref="SplitRaw"/>), because the whole
    /// point of the escaping is to keep a literal '#' in a name apart from the
    /// '#' that introduces an occurrence.
    /// </summary>
    public static (string Name, int Occurrence) ParseSegment(string segment)
    {
        int hash = segment.LastIndexOf('#');
        if (hash > 0 && int.TryParse(segment.AsSpan(hash + 1), out int occurrence) && occurrence > 0)
            return (Uri.UnescapeDataString(segment[..hash]), occurrence);
        return (Uri.UnescapeDataString(segment), 0);
    }

    /// <summary>
    /// Percent-encodes only the characters that would otherwise be read as path
    /// structure, so ordinary WZ names stay human-readable in the URL bar.
    /// </summary>
    private static string Escape(string segment)
    {
        if (segment.IndexOf('/') < 0 && segment.IndexOf('%') < 0 && segment.IndexOf('#') < 0)
            return segment;
        // '%' first, or the escapes introduced below would be re-escaped.
        return segment.Replace("%", "%25").Replace("/", "%2F").Replace("#", "%23");
    }
}
