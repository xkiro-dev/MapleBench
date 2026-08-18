namespace MapleBench.Services;

/// <summary>
/// Turns a caller-supplied folder path into a full path, or fails with something
/// the user can act on.
///
/// <c>/api/browse</c> and <c>/api/scan</c> take a path typed into the picker, so
/// every kind of wrong is reachable: a stale path from a previous session, a
/// drive that is no longer plugged in, a file dropped where a folder was meant,
/// or a system folder Windows will not let us read.  Left unguarded these surface
/// as a raw framework message ("The given path's format is not supported") or —
/// worse for the reader — as a perfectly successful listing of nothing, because
/// the callers' <c>SafeEnumerate</c> swallows the access error and returns an
/// empty sequence.  The picker shows whatever comes back verbatim, so each case
/// is named here instead.
///
/// It lives in its own file, and not inside <c>Endpoints</c>, because it is a
/// rule about a string rather than an HTTP concern: the interesting half of it —
/// which paths are refused and what the refusal says — is decidable without a
/// disk, and that is what makes it testable.
/// </summary>
public static class FolderPath
{
    /// <summary>
    /// The full path <paramref name="path"/> names, or an exception naming what
    /// is wrong with it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Nothing was given, the path is not fully qualified, or the format is one
    /// Windows will not parse.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">No folder is there.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows refuses to read it.</exception>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("No folder was given.");

        // Refused rather than resolved, and this is the whole point of the guard.
        //
        // "C:foo" is not "C:\foo". Windows reads a path with a drive letter and
        // no separator as DRIVE-RELATIVE: it means "foo inside whatever folder
        // this process is currently in on drive C:", and the answer comes from
        // the process, not from what was typed. Measured in this app, whose
        // working directory is its own scratch folder: "C:foo" resolved to
        // C:\Users\...\Temp\MapleBench\26976\foo and the picker then reported
        // "There is no folder at C:\Users\...\Temp\MapleBench\26976\foo" — a
        // path the user has never seen, about a private folder that is none of
        // their business, when what they meant was a folder on C:.
        //
        // The same is true of the two other unqualified shapes: "foo" resolves
        // against the working directory and "\foo" against whichever drive the
        // process happens to be running from. All three answer a question nobody
        // asked, and all three answer it differently tomorrow. There is no
        // reading of them this app can defend, so it refuses instead of guessing
        // — the alternative, silently rewriting "C:foo" to "C:\foo", invents an
        // intent just as freely and would list a folder the user did not name.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"'{path}' is not a complete folder path, so there is no way to tell which folder it "
                + "means. Windows would read it relative to wherever this program happens to be "
                + "running from — a private folder inside your temp directory — rather than relative "
                + "to anything you typed. Give the whole path, starting with a drive and a slash "
                + @"(C:\MapleStory\232) or a network share (\\server\share), or pick the folder in "
                + "the browser.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{path}' is not a usable folder path ({ex.Message})");
        }

        if (!Directory.Exists(full))
        {
            if (File.Exists(full))
                throw new ArgumentException($"'{full}' is a file, not a folder.");
            throw new DirectoryNotFoundException(
                $"There is no folder at '{full}'. It may have been moved or renamed, " +
                "or it may be on a drive that is not connected.");
        }

        // Enumeration is lazy, so pulling a single entry is what actually
        // touches the directory -- and it is the only way to tell "this folder
        // is empty" apart from "Windows will not let you look inside it".
        try
        {
            using IEnumerator<string> probe = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            probe.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                $"Windows will not let MapleBench read '{full}'. " +
                "Pick a folder you own, such as your MapleStory install.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"'{full}' could not be read: {ex.Message}");
        }

        return full;
    }
}
