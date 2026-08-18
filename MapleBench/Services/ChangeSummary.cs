using MapleBench.Models;
using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>One image below an archive that carries unsaved edits.</summary>
public sealed class ChangedImageDto
{
    public string Name { get; set; } = "";

    /// <summary>Session path, so the UI can navigate straight to it.</summary>
    public string Path { get; set; } = "";

    /// <summary>The archive's own path to it, for display.</summary>
    public string FullPath { get; set; } = "";
}

/// <summary>
/// One pending edit that changed the SHAPE of the archive rather than the
/// contents of an image.
/// </summary>
public sealed class StructuralChangeDto
{
    /// <summary>The undo label, as the history shows it.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Where in this archive to look, so the panel can navigate to it.
    ///
    /// These are the edit's refresh targets rather than the nodes themselves,
    /// which is not a shortcut: after a delete the node is gone and its
    /// container is the only thing left to show, and that is exactly what the
    /// user needs to see.
    /// </summary>
    public string[] Paths { get; set; } = Array.Empty<string>();
}

/// <summary>Everything one open archive holds that is not on disk.</summary>
public sealed class FileChangesDto
{
    public required OpenFileDto File { get; set; }

    /// <summary>Images carrying <c>WzImage.Changed</c>, capped at <see cref="ChangeSummary.MaxRows"/>.</summary>
    public List<ChangedImageDto> Images { get; set; } = new();

    /// <summary>Pending edits no image flag reports, capped at <see cref="ChangeSummary.MaxRows"/>.</summary>
    public List<StructuralChangeDto> Nodes { get; set; } = new();

    /// <summary>
    /// How many pending edits <see cref="Nodes"/> is a capped view of.
    ///
    /// Edits, not nodes, and deliberately: an edit is what the user did and what
    /// one Ctrl+Z takes back, whereas the number of nodes behind it is not
    /// recorded anywhere that could be read without guessing — an edit's paths
    /// are its refresh targets, so two deletes from one folder share one. A
    /// batch counts as the single entry it is, and its label says how many
    /// changes it was composed from.
    /// </summary>
    public int StructuralEdits { get; set; }

    /// <summary>
    /// Everything pending on this archive: changed images plus structural edits
    /// — which is also the number of rows the panel has to show. Zero here and
    /// <c>File.Dirty</c> true is <see cref="UnlistedChanges"/>, never silence.
    /// </summary>
    public int ChangeCount { get; set; }

    /// <summary>
    /// The archive is dirty and nothing above accounts for it.
    ///
    /// Reachable two ways, both real: an edit evicted past the undo cap, and a
    /// change recorded with no undo entry at all (a canvas replace whose
    /// original pixels could not be read seals the file instead). The work is
    /// there and cannot be listed, which is a different answer from "nothing is
    /// pending" and has to read as one.
    /// </summary>
    public bool UnlistedChanges { get; set; }
}

/// <summary>
/// What an archive has that the disk does not — the answer <c>/api/changes</c>
/// serves, and the panel a user opens before closing or saving.
///
/// It exists as a service rather than inline in the endpoint because of what it
/// got wrong. Unsaved work comes in two shapes and only one of them is an image:
/// <see cref="WzImage.Changed"/> covers every edit made INSIDE an image, and
/// nothing marks an image when the change is to the tree itself — deleting an
/// image, adding or renaming a directory, moving an .img between folders.
/// <see cref="OpenFile.Dirty"/> is the flag that covers those, and its own
/// documentation says so; the endpoint nonetheless reported only the first
/// half, so a structural edit was served as <c>dirty: true</c> with
/// <c>dirtyNodeCount: 0</c> and an empty list, and the app told the user it was
/// about to lose nothing.
///
/// Zero has to mean one thing. <see cref="FileChangesDto.ChangeCount"/> is the
/// number the question is actually about and it is never zero while the archive
/// is dirty — when nothing can be listed, <see cref="FileChangesDto.UnlistedChanges"/>
/// says that instead.
/// </summary>
public static class ChangeSummary
{
    /// <summary>
    /// Ceiling on the rows served per archive. A whole-archive port marks tens
    /// of thousands of images and the panel is for reading, not for auditing —
    /// the counts beside the lists are uncapped, so a truncated list never
    /// becomes a smaller number.
    /// </summary>
    public const int MaxRows = 500;

    /// <summary>
    /// Every open archive holding unsaved work.
    ///
    /// Takes the session gate, so callers must not already hold it. The undo
    /// service takes its own lock inside this one, which is the order every
    /// other caller uses.
    /// </summary>
    public static List<FileChangesDto> ForSession(WzSessionService session, UndoService undo)
    {
        List<FileChangesDto> files = new();
        lock (session.Gate)
        {
            foreach (OpenFile file in session.Files)
            {
                FileChangesDto changes = For(file, undo);
                if (changes.ChangeCount > 0 || changes.File.Dirty)
                    files.Add(changes);
            }
        }
        return files;
    }

    /// <summary>What one archive holds that is not on disk.</summary>
    public static FileChangesDto For(OpenFile file, UndoService undo)
    {
        List<ChangedImageDto> images = file.EnumerateDirtyImages()
            .Take(MaxRows)
            .Select(image => new ChangedImageDto
            {
                Name = image.Name,
                Path = ToSessionPath(file, image.FullPath),
                FullPath = image.FullPath,
            })
            .ToList();

        // Only the edits no image flag can report. The rest are already one row
        // each in `images`, and listing them twice would trade an undercount for
        // an overcount.
        List<UndoService.PendingEdit> structural = undo.PendingEdits(file.Id)
            .Where(edit => !edit.MarkedAnImage)
            .ToList();

        // Counted rather than measured off the capped lists: both stop at
        // MaxRows and the number beside them must not stop with them.
        int changeCount = file.CountDirtyImages() + structural.Count;

        return new FileChangesDto
        {
            File = file.ToDto(),
            Images = images,
            Nodes = structural
                .Take(MaxRows)
                .Select(edit => new StructuralChangeDto { Label = edit.Label, Paths = edit.Paths })
                .ToList(),
            StructuralEdits = structural.Count,
            ChangeCount = changeCount,
            UnlistedChanges = file.Dirty && changeCount == 0,
        };
    }

    /// <summary>
    /// "Etc.wz\Achievement\1.img" -> "f1/Achievement/1.img": swaps the archive's
    /// own root name for the session file id the tree API expects.
    /// </summary>
    private static string ToSessionPath(OpenFile file, string wzFullPath)
    {
        string[] segments = wzFullPath.Split('\\');
        return segments.Length <= 1
            ? file.Id
            : file.Id + "/" + string.Join("/", segments.Skip(1));
    }
}
