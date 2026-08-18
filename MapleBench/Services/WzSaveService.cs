using System.Diagnostics;
using MapleLib.MapleCryptoLib;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using MapleBench.Models;

namespace MapleBench.Services;

public sealed class SaveResult
{
    public string FileId { get; set; } = "";
    public string SavedTo { get; set; } = "";
    public string? BackupPath { get; set; }
    public int ImagesRewritten { get; set; }
    public long Bytes { get; set; }
    public double Seconds { get; set; }
    /// <summary>Non-fatal things the user should know: client running, verification mismatches.</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Writes archives back to disk without ever putting the user's client files at
/// risk.
///
/// The dangerous part is that an open <see cref="WzFile"/> keeps a reader on its
/// source file and streams unmodified images straight through from it during a
/// save.  Writing directly over that file would therefore be reading and writing
/// the same bytes at once.  Every save here goes to a sibling temp file first,
/// and only once that has completed do we close the archive, move the original
/// aside as a backup, and swap the new file in.
/// </summary>
public sealed class WzSaveService
{
    /// <summary>
    /// The one prefix every scratch file this service writes carries.
    ///
    /// Both the writer (<see cref="NewTempName"/>) and the orphan sweep
    /// (<see cref="IsOurTempFile"/>) go through this constant, because the sweep
    /// deletes files: the day the two forms disagree is the day the sweep either
    /// stops working or starts eating something that is not ours.
    /// </summary>
    private const string TempPrefix = "mbsave-";

    /// <summary>
    /// The part of a scratch name that says "this is the user's own archive,
    /// moved aside mid-swap" rather than "this is output we produced".
    ///
    /// The two are worlds apart if a save is interrupted — one is a copy of
    /// something that already exists, the other is the only copy of the file the
    /// user opened — so the sweep can tell the user which it deleted.
    /// </summary>
    private const string ReplacedInfix = "replaced-";

    /// <summary>
    /// How long an <c>mbsave-*</c> file has to sit before the sweep treats it as
    /// abandoned.  A day is far longer than any save takes and far longer than
    /// any session, so nothing in flight can ever be inside the window.
    /// </summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    /// <summary>
    /// How many timestamped <c>.bak</c> files are kept beside one archive.
    ///
    /// Nothing pruned them at all before, and a backup of a 1.5 GB Map.wz costs
    /// 1.5 GB: a week of saving a client folder filled the drive the client is
    /// installed on.  Three, because what people reach for is "the one from
    /// before I broke it", which is the newest or the one before it — and
    /// keeping one would mean a single bad save destroys the only good copy.
    /// </summary>
    private const int BackupsKept = 3;

    /// <summary>
    /// How long verification will spend forcing rewritten image bodies open
    /// before it stops and reports how many it did not reach.
    ///
    /// Measured on this machine, Release, against synthetic archives built to
    /// the two shapes that matter:
    ///
    ///   canvas-heavy (Map.wz shape), 1,200 images / 939 MB — ParseWzFile
    ///     alone 1 ms; forcing every body open 0.39-0.63 ms an image, 470-760 ms
    ///     for the lot.
    ///   property-heavy (String.wz shape), 400 images / 6 MB, ~900 properties
    ///     each — 0.16 ms an image, 65 ms for the lot.
    ///
    /// The cost tracks property count, not file size: ParseImage records a
    /// canvas blob's offset and length and never decompresses it, which is why
    /// nearly a gigabyte of pixels reads back in under a second. A 2,742-image
    /// client archive therefore costs 1-2 s to open completely, and an ordinary
    /// save — a handful of dirty images — costs single-digit milliseconds and
    /// never comes near this budget.
    ///
    /// It exists for the encryption-override save, which re-serialises every
    /// image, and for an archive pathological in a way none of the above is.
    /// Twenty seconds is what <c>WzSearchService</c> already spends before it
    /// truncates, so the app has one answer to "how long is too long".
    /// </summary>
    private static readonly TimeSpan VerifyParseBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many individual discrepancies a verification failure lists before it
    /// summarises.  A systematically broken write produces one per image, and a
    /// message with 2,700 lines in it is not a message.
    /// </summary>
    private const int MaxReportedDefects = 10;

    private readonly WzSessionService _session;
    private readonly UndoService _undo;
    private readonly ILogger<WzSaveService> _log;

    /// <summary>
    /// Optional, and only for the post-save reconciliation: an interactive port
    /// reports parts written against the in-memory tree, and the save is the
    /// first moment that claim can be checked against a file. Null in tests that
    /// exercise saving alone; the DI container supplies it in the app.
    /// </summary>
    private readonly PortService? _port;

    /// <summary>Directories already swept this run, so repeat calls cost nothing.</summary>
    private readonly HashSet<string> _sweptDirectories = new(StringComparer.OrdinalIgnoreCase);

    public WzSaveService(WzSessionService session, UndoService undo, ILogger<WzSaveService> log,
        PortService? port = null)
    {
        _session = session;
        _undo = undo;
        _log = log;
        _port = port;
    }

    public SaveResult Save(SaveRequest request)
    {
        lock (_session.Gate)
        {
            OpenFile file = _session.GetFile(request.FileId);

            // A split archive has no file to be saved over: FilePath names the
            // directory the parts were merged from, so an in-place save would try
            // to write a .wz where a folder is, and a Save As would quietly write
            // a classic archive that the 4 GB offset ceiling makes impossible for
            // the big ones anyway. Conversion is a separate, verified path that
            // knows all of that -- point at it rather than half-doing it here.
            if (file.Kind == "split")
            {
                throw new InvalidOperationException(
                    $"'{file.Name}' was opened from a split client for reference and is not a file that can be " +
                    "saved. To turn it into a classic .wz, use Import — that path checks the size, writes to a " +
                    "scratch file and verifies the result before anything is put in place.");
            }

            if (file.Kind == "img-folder")
            {
                throw new InvalidOperationException(
                    $"'{file.Name}' is a mounted IMG folder, not one destination file. It stays reference-only " +
                    "because the current save transaction cannot verify and replace every changed .img as one " +
                    "all-or-nothing operation.");
            }

            // Read-only was enforced on every edit but not on the save itself, so
            // an archive locked after its last edit could still be rewritten to
            // disk by this path. It would be a no-op rewrite of clean content, but
            // it replaces the bytes of the file the user marked as reference, and
            // "reference only" should mean the file is not touched.
            if (file.ReadOnly)
            {
                throw new InvalidOperationException(
                    $"'{file.Name}' is open for reference only, so it was not written. Unlock it in the Files " +
                    "panel if you meant to change it.");
            }

            // A .ms pack is read by converting it into an in-memory WzFile, and every
            // save path below writes WZ bytes. Writing those back over the .ms would
            // produce a file the client cannot read -- and verification would not
            // catch it, because the result *is* a valid WZ archive, so reopening it
            // succeeds. MapleLib has WzMsFile.Save for this, but wiring it up needs a
            // real .ms to test the round trip against, so refuse rather than
            // silently convert. Save As to a .wz path still works.
            bool targetIsMs = !string.IsNullOrEmpty(request.TargetPath)
                && Path.GetExtension(request.TargetPath).Equals(".ms", StringComparison.OrdinalIgnoreCase);

            if ((file.Kind == "ms" && string.IsNullOrEmpty(request.TargetPath)) || targetIsMs)
            {
                throw new InvalidOperationException(
                    $"'{file.Name}' is a .ms pack, and saving one is not supported yet — writing " +
                    "it back would produce WZ data under a .ms name, which the client cannot read.\n\n" +
                    "Use \"Save a copy\" and choose a .wz path instead.");
            }

            // Whether this save re-serialises every image or only the dirty ones
            // decides how much has to be preflighted, and it is not the same
            // question as "what did the user edit" -- see the method.
            string? wholeArchiveNote = file.Kind == "img" ? null : WhyEveryImageIsRewritten(file, request);

            // Before Preflight, which is the first thing that allocates: it parses
            // images and forces every canvas in them to produce its compressed
            // bytes. Everything from here to the end of the save is inside the one
            // window where running out of memory is unrecoverable -- SaveToDisk
            // unparses the tree as it goes, so a process torn down mid-write loses
            // the edits whether or not the file on disk survives. Refusing here
            // costs the user a message; not refusing costs them the session.
            string? memory = CheckMemoryCeiling(file, everyImage: wholeArchiveNote != null);
            if (memory != null)
                throw new InvalidOperationException(memory);

            List<string> blockers = Preflight(file, everyImage: wholeArchiveNote != null);
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException(
                    "These nodes cannot be re-serialised, so nothing was written:\n" +
                    string.Join("\n", blockers.Take(20).Select(b => "  - " + b)) +
                    (blockers.Count > 20 ? $"\n  ...and {blockers.Count - 20} more." : ""));
            }

            try
            {
                return file.Kind == "img"
                    ? SaveLooseImage(file, request)
                    : SaveArchive(file, request, wholeArchiveNote);
            }
            finally
            {
                // Both paths, success or failure. The undo closures capture live
                // MapleLib objects, and a save can release or replace them — the
                // archive path definitely does, and the loose-image path is one
                // dispose-and-reopen away from doing the same. Clearing here
                // makes the invariant structural instead of a thing each new
                // save path has to remember.
                //
                // Scoped to THIS file. SaveToDisk only unparses the archive being
                // saved, so a blanket Clear() threw away the undo history of every
                // other open archive — and the headline workflow is twenty to forty
                // open at once, so saving Etc.wz silently forfeited unsaved work in
                // Map.wz and String.wz.
                _undo.ClearForFile(file.Id);
            }
        }
    }

    /// <summary>
    /// Every image in an archive, counted rather than listed.
    ///
    /// Used for the "images rewritten" figure on a save that re-serialises the
    /// whole file. Counting is deliberate: the figure has to describe the save
    /// that happened, and materialising 45,399 images to produce one integer
    /// would be paying an archive's worth of memory for it.
    /// </summary>
    private static int CountAllImages(WzDirectory? directory)
    {
        if (directory == null)
            return 0;

        int total = directory.WzImages.Count;
        foreach (WzDirectory sub in directory.WzDirectories)
            total += CountAllImages(sub);
        return total;
    }

    /// <summary>
    /// Verifies that every image due to be rewritten can actually produce bytes,
    /// before a single byte is written.
    ///
    /// This exists because <c>WzPngProperty.GetCompressedBytes</c> throws when a
    /// canvas's blob length is zero — a state that parses cleanly and only fails
    /// on write.  Placeholder canvases (the '_inlink'/'_outlink' shape) hit it.
    /// Without this check the failure lands halfway through serialising the
    /// archive, which is exactly when you least want an exception.
    /// </summary>
    /// <param name="everyImage">
    /// Inspect the whole archive rather than only the images the user changed.
    /// Some saves re-serialise everything (see
    /// <see cref="WhyEveryImageIsRewritten"/>), and this check walking only the
    /// dirty set meant a single placeholder canvas anywhere else in the file
    /// still threw mid-write — after <c>UnparseImage</c> had destroyed the
    /// in-memory tree — which is the precise failure this method exists to
    /// prevent.
    /// </param>
    public List<string> Preflight(OpenFile file, bool everyImage = false)
    {
        List<string> problems = new();

        // Every problem is reported under a heading that already names the
        // archive ("Etc.wz cannot be saved yet:"), and the failing node's own
        // name is the last thing on the line. Repeating "Etc.wz\" in front of
        // each path pushes the part that identifies the node off the end of a
        // narrow toast.
        string root = file.WzFile?.WzDirectory?.Name ?? file.Name;

        foreach (WzImage image in everyImage ? EnumerateAllImages(file) : file.EnumerateDirtyImages())
        {
            string path = TrimArchiveRoot(image.FullPath, root);
            bool wasAlreadyOpen = image.Parsed;
            try
            {
                // The return value matters. ParseImage answers false without
                // throwing for a header it does not recognise, so discarding it
                // let preflight pass an image that cannot be read -- and the
                // refusal then landed mid-write, after UnparseImage had already
                // discarded the in-memory tree. Preflight exists precisely so
                // that failure happens here, where nothing has been touched.
                if (!image.Parsed && !image.ParseImage())
                {
                    problems.Add($"{path}: this image could not be read, so it cannot be written back.");
                    continue;
                }
                // The walk's own verdict on whether it saw the whole image is
                // read, not discarded.
                //
                // WzWalk cuts a branch short on a repeat or at depth 256 and
                // records that in Stopped. Dropping the instance on the floor
                // made "problems is empty" mean two things: every canvas can
                // produce bytes, OR some of them were never asked. The second
                // reading is the dangerous one here, because an empty list is
                // what authorises the write — and the failure it lets through is
                // the one this method's own summary describes: GetCompressedBytes
                // throwing halfway through SaveToDisk, after UnparseImage has
                // already destroyed the in-memory tree. A single cyclic image
                // anywhere in the archive was enough, and on the everyImage path
                // that is 45,000 images' worth of chances.
                //
                // A stopped walk is therefore a blocker, not a note. The
                // alternative — writing anyway and warning afterwards — is the
                // shape this project keeps paying for: the message arrives after
                // the archive has changed.
                WzWalk walk = new();
                InspectContainer(image.WzProperties, path, problems, walk, 0);
                if (walk.Stopped)
                {
                    problems.Add(
                        $"{path}: this image links back into itself, so it could not be checked all the way " +
                        "through and part of it was never asked whether it can be written. Saving it could " +
                        "fail part way and take the unsaved tree with it.");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{path}: {ex.Message}");
            }
            finally
            {
                // A whole-archive pass would otherwise leave every image's
                // property graph resident at once, purely as a side effect of a
                // check that has finished with it -- measured at 29 KB an image
                // for a canvas-heavy archive and 168 KB for a property-heavy one,
                // so 80 MB to 450 MB for a 2,742-image client archive, held right
                // through the write that follows. The cost of releasing them is
                // one extra parse, which the numbers on VerifyParseBudget put at
                // well under a second for a whole archive. Only images this pass
                // opened are released, and never a changed one: UnparseImage
                // clears the properties, and on an edited image those are the
                // user's work.
                if (everyImage && !wasAlreadyOpen && !image.Changed)
                    image.UnparseImage();
            }
        }
        return problems;
    }

    /// <summary>
    /// Asks whether the machine can give this save the memory it needs, and
    /// returns the refusal to say so if it cannot.
    ///
    /// Two things are gathered here that <see cref="SaveGuards.CheckMemory"/>
    /// cannot see for itself: how many images the archive being saved holds —
    /// which is what the save-time cost scales with, not the file size — and
    /// which *other* archives are open, so the refusal can name something worth
    /// closing instead of leaving the user to guess.
    ///
    /// Both are plain walks of the directory tables that are already in memory.
    /// Nothing is parsed: <see cref="WzDirectory.WzImages"/> is populated when the
    /// archive is opened, so counting 45,399 images costs a pointer chase and no
    /// I/O. Caller must hold the session gate.
    /// </summary>
    private string? CheckMemoryCeiling(OpenFile file, bool everyImage)
    {
        // A loose .img is one image and its whole cost is already resident. There
        // is nothing here worth predicting, and a false refusal on the cheapest
        // save in the app would be pure damage.
        if (file.Kind == "img")
            return null;

        int images = 0;
        foreach (WzImage _ in EnumerateAllImages(file))
            images++;

        List<SaveGuards.OpenArchive> others = new();
        foreach (OpenFile other in _session.Files)
        {
            if (ReferenceEquals(other, file) || other.WzFile?.WzDirectory == null)
                continue;

            int count = 0;
            foreach (WzImage _ in other.EnumerateArchiveImages())
                count++;

            long bytes = 0;
            try { bytes = new FileInfo(other.FilePath).Length; }
            catch { /* the size is for the message only */ }

            others.Add(new SaveGuards.OpenArchive(other.Name, count, bytes));
        }

        return SaveGuards.CheckMemory(file.Name, images, everyImage, others);
    }

    /// <summary>
    /// Why this save will re-serialise every image in the archive instead of
    /// streaming the untouched ones through byte for byte — or null when it will
    /// not.
    ///
    /// <c>WzDirectory.GenerateDataFile</c> force-marks every image changed when
    /// the write IV differs from the one the archive is holding, or when a
    /// non-default MapleStory user key is configured. Two things follow, and
    /// both were wrong before this existed:
    ///
    ///   * <see cref="Preflight"/> has to walk the whole archive, not the dirty
    ///     images;
    ///   * every accepted-lossy conversion applies to the entire file rather
    ///     than to the images the user edited, so the user has to be told.
    ///
    /// The IV comparison mirrors <c>WzFile.SaveToDisk</c> rather than asking
    /// whether a version was chosen: re-saving a BMS archive as BMS resolves to
    /// the same key, and that save streams through as usual.
    /// </summary>
    private static string? WhyEveryImageIsRewritten(OpenFile file, SaveRequest request)
    {
        if (file.WzFile == null)
            return null;

        // Process-wide rather than per file, and it defeats the byte-copy path
        // for any save at all: the images have to be re-encrypted to be readable
        // under the new key.
        if (!MapleCryptoConstants.IsDefaultMapleStoryUserKey())
            return "a custom MapleStory user key is in use";

        WzMapleVersion saveAs = WzSessionService.ParseVersion(request.MapleVersion) ?? WzMapleVersion.UNKNOWN;
        if (saveAs == WzMapleVersion.UNKNOWN)
            return null;   // in place: SaveArchive pins the key the file was opened with

        try
        {
            // What the archive currently holds. WzFile.WzIv is internal to
            // MapleLib, but it is set from exactly these two things when the file
            // is opened, and every save reopens.
            byte[] opened = file.CustomIv ?? WzTool.GetIvByMapleVersion(file.MapleVersion);
            if (WzTool.GetIvByMapleVersion(saveAs).AsSpan().SequenceEqual(opened))
                return null;
        }
        catch
        {
            // The CUSTOM branch of GetIvByMapleVersion reads a stored app setting
            // and can fail. Not being able to tell means taking the expensive,
            // honest answer rather than the cheap, hopeful one.
        }
        return $"the archive is being re-encrypted as {saveAs}";
    }

    /// <summary>
    /// "Etc.wz\Achievement\1.img" -> "Achievement\1.img", but only when the
    /// leading segment really is this archive's root.
    ///
    /// A loose .img has no root segment at all, and a mismatch means the caller
    /// would lose context rather than gain room, so both are left untouched.
    /// </summary>
    private static string TrimArchiveRoot(string fullPath, string root)
    {
        int separator = fullPath.IndexOf('\\');
        if (separator <= 0 || separator == fullPath.Length - 1)
            return fullPath;
        return fullPath.AsSpan(0, separator).Equals(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath[(separator + 1)..]
            : fullPath;
    }

    /// <summary>
    /// Reads every canvas the writer is about to write, so a canvas that cannot be
    /// decoded is refused here rather than halfway through the file.
    ///
    /// Descends through <see cref="WzWalk"/>, which it did not before, and the
    /// omission was the same uncatchable crash that lived in PortService: a UOL
    /// hands back the children of whatever it resolves to, so a link pointing at
    /// its own parent -- reactor 2208004.img's <c>1/hit/0</c> is one, in stock v233
    /// AND v232 data -- recursed until the stack ran out and took the process with
    /// it. Here that happened during PREFLIGHT, i.e. with an archive the user had
    /// just asked to save and every other open archive's unsaved work still in
    /// memory.
    ///
    /// Skipping links costs this check nothing. A canvas reached through a UOL is
    /// some other node's, and it is inspected when the walk reaches the image that
    /// really owns it -- or it lives in an archive this save is not writing, in
    /// which case reading it was never this pass's business.
    /// </summary>
    private static void InspectContainer(
        WzPropertyCollection? properties, string path, List<string> problems, WzWalk walk, int depth)
    {
        if (properties == null)
            return;

        foreach (WzImageProperty property in properties)
        {
            string childPath = $"{path}/{property.Name}";

            if (property is WzCanvasProperty canvas)
            {
                try
                {
                    // Forces the same read the writer will perform. Cached, so
                    // this is not paid twice.
                    canvas.PngProperty?.GetCompressedBytes(true);
                }
                catch (Exception ex)
                {
                    problems.Add($"{childPath}: {ex.Message}");
                }
            }

            InspectContainer(walk.Into(property, depth), childPath, problems, walk, depth + 1);
        }
    }

    /// <param name="wholeArchiveNote">
    /// Non-null when every image is being re-serialised, and says why. See
    /// <see cref="WhyEveryImageIsRewritten"/>.
    /// </param>
    private SaveResult SaveArchive(OpenFile file, SaveRequest request, string? wholeArchiveNote)
    {
        WzFile wzFile = file.WzFile
            ?? throw new InvalidOperationException("This entry has no archive to save.");

        string destination = Path.GetFullPath(request.TargetPath ?? file.FilePath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination has no directory.");
        Directory.CreateDirectory(directory);

        // Refuse before touching anything if the destination is locked by the
        // game client: the historical failure here is that the original file is
        // moved aside, the write fails, and both copies are lost.
        // Every save sweeps the folder it is about to write into, not just the
        // first one of the session. A crashed save leaves a full-size scratch file
        // there, and the open-time sweep remembers folders it has already visited
        // -- so the leftover from a crash five minutes ago sat beside the archive
        // until the next launch. This is the one moment we are certain to be
        // looking at the right folder.
        SweepDirectory(directory);

        SaveGuards.GuardResult guard = SaveGuards.Check(
            destination, File.Exists(destination), file.FilePath,
            everyImageRewritten: wholeArchiveNote != null);
        if (!guard.CanProceed)
            throw new InvalidOperationException(guard.Blocker);

        List<string> warnings = guard.Warnings.ToList();
        if (wholeArchiveNote != null)
        {
            // Said in the result because it changes what the save costs the file,
            // not just how long it takes. The lossy conversions below are
            // documented as only biting images the user modified, and for this
            // one save that is false for all of them.
            warnings.Add(
                $"Every image in this archive was re-serialised, not only the ones you changed, because " +
                $"{wholeArchiveNote}. Conversions that normally touch edited images only therefore applied " +
                "to the whole file: list.wz-encrypted PNGs re-emitted as plain zlib, the canvas 'pages' " +
                "field zeroed, and any name that is not Latin-1 replaced. Keep the backup.");
        }

        // What this save will actually re-serialise, which is not always the
        // dirty count.
        //
        // The dirty count is the number of images the USER changed. Seven lines
        // above, this method has already established that some saves re-serialise
        // every image in the archive whatever the user touched -- that is exactly
        // what wholeArchiveNote means, and the warning it just produced says the
        // lossy conversions applied to the whole file. Reporting the dirty count
        // in that case told the user "3 images rewritten" about a save that
        // rewrote 45,399, i.e. it under-reported by four orders of magnitude in
        // precisely the case where the number is the one thing they would want to
        // know. Intent, not outcome.
        int rewritten = wholeArchiveNote != null
            ? CountAllImages(wzFile.WzDirectory)
            : file.CountDirtyImages();
        WzMapleVersion saveAs = WzSessionService.ParseVersion(request.MapleVersion) ?? WzMapleVersion.UNKNOWN;

        // Inventory taken before the archive is disposed, so the reopened file
        // can be checked against what we believed we were writing. It has to be
        // taken after Preflight, too: Preflight is what opens the dirty images,
        // and an image's property count is only knowable once something has.
        SaveInventory expected = TakeInventory(wzFile.WzDirectory, everyImageRewritten: wholeArchiveNote != null);

        // While the tree the port wrote into still exists: a claim the user has
        // since undone is not something the saved file can be expected to hold,
        // and pruning it now is what keeps the post-save check from crying wolf.
        _port?.PruneSaveClaims(file);

        string originalPath = file.FilePath;
        byte[]? iv = file.CustomIv;
        short gameVersion = wzFile.Version;

        // The encryption the file on disk is *currently* written with, kept
        // separate from the one this save is about to produce.
        //
        // Every failure path below reopens `originalPath`, and that file has not
        // been touched -- so it must be reopened with the key it was opened with.
        // Re-encrypting ("save as GMS") used to hand the recovery the NEW key, so
        // a failed re-encryption reopened an untouched BMS archive as GMS, the
        // parse failed, and the entry was marked Detached: the user was told their
        // file could not be reopened when nothing whatever was wrong with it.
        WzMapleVersion openedVersion = file.MapleVersion;
        byte[]? openedIv = file.CustomIv;

        // The encryption the output will actually carry. Re-encrypting on save
        // ("save as GMS") changes it, and verifying or reopening with the old
        // one would fail on a file that is perfectly good.
        WzMapleVersion version = saveAs == WzMapleVersion.UNKNOWN ? file.MapleVersion : saveAs;

        // The IV the output must actually be written with.
        //
        // SaveToDisk otherwise derives it from WzTool.GetIvByMapleVersion, and
        // the CUSTOM branch of that reads a stored app setting rather than the
        // key the file was opened with -- so a custom-IV archive would be
        // written under the wrong key, and only verification would catch it.
        // Re-encrypting to a named version deliberately drops the custom IV.
        byte[]? writeIv = saveAs == WzMapleVersion.UNKNOWN ? iv : null;
        if (saveAs != WzMapleVersion.UNKNOWN)
            iv = null;

        // Same volume as the destination so the final swap is a move, not a
        // cross-device copy.  The stem must be globally unique, not just the
        // full name: MapleLib's SaveToDisk derives its own scratch file from
        // Path.GetFileNameWithoutExtension and writes it to the working
        // directory, so ".Etc.wz.mbsave-<guid>" would collapse back to a shared
        // ".Etc.wz.TEMP" and two concurrent saves would truncate each other.
        string temp = Path.Combine(directory, NewTempName(".wz"));
        DateTime started = DateTime.UtcNow;

        // Everything from here on assumes THIS archive's in-memory tree is
        // forfeit: SaveToDisk unparses every image in it and rewrites their
        // offsets to new-file coordinates while the readers still point at the
        // old file. The undo closures capture those objects, so this file's
        // history dies with it -- and only this file's. Other open archives are
        // untouched by the call and keep theirs.
        try
        {
            wzFile.SaveToDisk(temp, request.Save64Bit, saveAs, writeIv);
        }
        catch
        {
            TryDelete(temp);
            _undo.ClearForFile(file.Id);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw;
        }
        _undo.ClearForFile(file.Id);

        long bytes = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (bytes == 0)
        {
            TryDelete(temp);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw new InvalidOperationException(
                "The archive serialised to zero bytes, so nothing was written. " + EditsLostNote());
        }

        // Durability before anything irreversible happens to the original.
        try
        {
            FlushToDisk(temp);
        }
        catch (Exception ex)
        {
            // Never delete a complete write because we could not confirm it is on
            // the platter -- the bytes are still every edit the user made.
            string rescued = KeepForRecovery(temp, destination);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw new InvalidOperationException(
                $"The rewritten archive could not be flushed to disk, so '{Path.GetFileName(destination)}' was " +
                $"left exactly as it was.\n\nWhat was written is kept at:\n{rescued}\n{EditsLostNote()}\n\n" +
                $"Detail: {ex.Message}", ex);
        }

        // Verify the new file BEFORE the original is moved aside. Discovering
        // that the output is unreadable is only useful while the input still
        // exists -- checking afterwards means the client folder already holds a
        // file we have just admitted we cannot parse.
        List<string> verification;
        try
        {
            verification = VerifyCandidate(temp, version, iv, gameVersion, expected, warnings);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw new InvalidOperationException(
                $"The rewritten archive could not be read back, so '{Path.GetFileName(destination)}' was left " +
                $"exactly as it was. {EditsLostNote()}\n\nDetail: {ex.Message}", ex);
        }

        // An inventory mismatch aborts, exactly as a parse failure does. These used
        // to be returned as warnings and carried through to the success path, so the
        // file was swapped in *after* we had established it was wrong and the user
        // was told "Saved and verified" with the discrepancy in a separate toast
        // underneath. If the image count moved or a path vanished, the only thing
        // verification is for has failed.
        if (verification.Count > 0)
        {
            string rescued = KeepForRecovery(temp, destination);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw new InvalidOperationException(
                $"The rewritten archive does not match what was open, so '{Path.GetFileName(destination)}' " +
                $"was left exactly as it was:\n" +
                string.Join("\n", verification.Select(v => "  - " + v)) +
                $"\n\nWhat was written is kept at:\n{rescued}\n{EditsLostNote()}");
        }

        // Only now release the reader on the original and swap.
        wzFile.Dispose();
        file.WzFile = null;

        // With our own handle gone, any remaining lock belongs to someone else
        // -- typically the running game client. Overwriting in that state is the
        // documented way to lose both the original and the edits, so stop here
        // while the new file is still just a temp file.
        string? locked = SaveGuards.CheckWritable(destination);
        if (locked != null)
        {
            // Keep the temp file. It holds every edit that was just serialised,
            // and deleting it here is what turns "close MapleStory and retry"
            // into "your afternoon is gone".
            string rescued = KeepForRecovery(temp, destination);
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw new InvalidOperationException(
                $"{locked}\n\nYour edits were written to:\n{rescued}\n" +
                "Close the other program, then rename that file over the original.");
        }

        string? backupPath = null;
        try
        {
            if (File.Exists(destination))
            {
                // Overwriting a *different* archive always leaves a backup, whatever
                // the request asked for. "Save a copy" sends Backup=false and its
                // dialog promises "nothing is overwritten without asking", so a target
                // path naming an existing file -- easy to reach by typing one in
                // browser mode, and it may be another archive that is open right now
                // -- destroyed it irreversibly and silently. The client asks for
                // confirmation now; this is the half that cannot be forgotten.
                bool overwritingAnotherFile =
                    !string.Equals(destination, Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase);

                if (request.Backup || overwritingAnotherFile)
                {
                    backupPath = MakeBackupPath(destination);
                    ReplaceFile(temp, destination, backupPath);
                }
                else
                {
                    // No backup wanted, but never delete first: the original is
                    // moved aside and only removed once the new file is in place.
                    // The name matters. This file IS the user's archive, and if
                    // the process dies in the moment between the swap and the
                    // delete it is all that is left of it -- under "Etc.wz.replacing"
                    // nothing would ever have restored, swept or explained it.
                    string displaced = Path.Combine(directory, DisplacedOriginalName(destination));
                    ReplaceFile(temp, destination, displaced);
                    TryDelete(displaced);
                }
            }
            else
            {
                File.Move(temp, destination);
            }
        }
        catch
        {
            // Put the original back so a failed swap never leaves a hole.
            if (backupPath != null && !File.Exists(destination) && File.Exists(backupPath))
                File.Move(backupPath, destination);
            if (File.Exists(temp))
            {
                string rescued = KeepForRecovery(temp, destination);
                RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
                throw new InvalidOperationException(
                    $"The file could not be replaced. Your edits were written to:\n{rescued}");
            }
            RecoverToOriginal(file, originalPath, openedVersion, openedIv, gameVersion);
            throw;
        }

        // Only once the new file is in place, so a swap that failed still has
        // every backup it had before.
        if (backupPath != null)
        {
            int pruned = PruneBackups(destination);
            if (pruned > 0)
            {
                warnings.Add(
                    $"{pruned} older backup{(pruned == 1 ? " was" : "s were")} deleted — the {BackupsKept} most " +
                    $"recent .bak files beside '{Path.GetFileName(destination)}' are kept.");
            }
        }

        Reopen(file, destination, version, iv, gameVersion);
        file.FilePath = destination;
        file.Dirty = false;

        // The session entry has to learn the encryption it was just written with.
        //
        // Reopen built the new WzFile with the right key, but nothing put it back
        // on the OpenFile, so after "save as GMS" the entry still claimed BMS with
        // whatever custom IV it was opened with. The next save of the same archive
        // then read those stale fields and disagreed with MapleLib about what it
        // was doing: WhyEveryImageIsRewritten compared GMS against GMS and
        // answered "streaming", while SaveToDisk compared the requested key
        // against the archive's own and force-marked every image changed. So the
        // inventory recorded 45,399 images as copied through untouched, all 45,399
        // were re-encrypted, and verification correctly refused the result --
        // reproduced here on a 1,568 MB Character.wz: 171 seconds of work, a
        // 1.5 GB .recovered file left beside the archive, and the in-memory edits
        // gone. Recording the truth is the whole fix.
        file.MapleVersion = version;
        file.CustomIv = iv;

        // After the reopen, so every lookup reads the saved bytes — the same
        // rule the composition build follows: verify on the file, never on the
        // tree that wrote it. This is what catches "written 5, failed 0" said
        // about parts that are not in the saved archive.
        if (_port != null)
            warnings.AddRange(_port.CheckSavedArchive(file));

        _log.LogInformation("Saved {File} ({Bytes} bytes, {Images} images rewritten)",
            destination, bytes, rewritten);

        return new SaveResult
        {
            FileId = file.Id,
            SavedTo = destination,
            BackupPath = backupPath,
            ImagesRewritten = rewritten,
            Bytes = bytes,
            Seconds = (DateTime.UtcNow - started).TotalSeconds,
            // `verification` is necessarily empty here — a non-empty one aborts the
            // save above. What this carries is the guard's warnings, whether the
            // whole archive was rewritten, how much of verification was skipped,
            // and any backups that were pruned.
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Opens the freshly written file and checks it against the pre-save
    /// inventory. Throws if it cannot be parsed at all; returns a list of
    /// discrepancies, any one of which the caller treats as fatal.
    ///
    /// What this used to be worth saying out loud: it called
    /// <c>ParseWzFile</c> and compared a list of path strings. That reads the
    /// directory table and nothing else — <c>WzImage</c> bodies are lazy — so a
    /// match proved the table of contents survived and nothing whatever about
    /// any image. Image-body corruption could still pass that check cleanly.
    ///
    /// Four checks now, cheapest first:
    ///
    ///  1. <b>The header agrees with the file length.</b> The writer sets
    ///     <c>FSize</c> to (end of the last image − <c>FStart</c>), so
    ///     <c>FStart + FSize</c> is the file size exactly. A write cut short by
    ///     a full disk shows up here and in no other check — the directory table
    ///     is written first and parses perfectly over a truncated body.
    ///  2. <b>Every expected image is present, and no others.</b>
    ///  3. <b>Images streamed through untouched still carry the block size and
    ///     checksum they had.</b> Those bytes were copied verbatim, so either
    ///     number moving is a real defect — and both are read straight from the
    ///     new directory table, which the parse above has already done.
    ///  4. <b>Images this save re-serialised are actually opened</b>, and an
    ///     image that had contents and comes back empty fails. That is the
    ///     shape of a <c>ParseImage</c> that returned false and was serialised
    ///     as an empty tree, which is otherwise a total silent loss that the
    ///     inventory matches on.
    ///
    /// Only check 4 costs anything, and it is bounded by
    /// <see cref="VerifyParseBudget"/>; whatever it could not reach is reported
    /// through <paramref name="notes"/> rather than passed over.
    /// </summary>
    /// <param name="notes">
    /// Collects things that are true but are not defects — chiefly how much of
    /// check 4 was skipped. The caller carries these to the user.
    /// </param>
    private List<string> VerifyCandidate(
        string candidatePath, WzMapleVersion version, byte[]? iv, short gameVersion,
        SaveInventory expected, List<string> notes)
    {
        WzFile probe = new(candidatePath, gameVersion, version);
        try
        {
            WzFileParseStatus status = probe.ParseWzFile(iv);
            if (status != WzFileParseStatus.Success)
                throw new InvalidOperationException(status.GetErrorDescription());

            List<string> defects = new();
            int suppressed = 0;
            void Defect(string message)
            {
                if (defects.Count < MaxReportedDefects)
                    defects.Add(message);
                else
                    suppressed++;
            }

            long onDisk = new FileInfo(candidatePath).Length;
            long declared = probe.Header.FStart + (long)probe.Header.FSize;
            if (declared != onDisk)
            {
                Defect(declared > onDisk
                    ? $"The header describes {declared:N0} bytes but only {onDisk:N0} were written — the file is truncated."
                    : $"The header describes {declared:N0} bytes but the file is {onDisk:N0} — there is data past the last image.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            List<string> unexpected = new();
            Stopwatch clock = Stopwatch.StartNew();
            int actualCount = 0, opened = 0, notReached = 0;

            foreach (WzImage image in EnumerateImages(probe.WzDirectory))
            {
                actualCount++;
                string path = StripRoot(image.FullPath);

                if (!expected.ByPath.TryGetValue(path, out ImageIdentity? before))
                {
                    if (unexpected.Count < 5)
                        unexpected.Add(path);
                    continue;
                }
                seen.Add(path);

                // Two images share this path, so there is no way to say which of
                // them this one is. Presence is all that can honestly be checked.
                if (before.Ambiguous)
                    continue;

                if (!before.Rewritten)
                {
                    if (image.BlockSize != before.BlockSize)
                    {
                        Defect($"'{path}' was copied through unchanged but its size moved from " +
                               $"{before.BlockSize:N0} to {image.BlockSize:N0} bytes.");
                    }
                    else if (image.Checksum != before.Checksum)
                    {
                        Defect($"'{path}' was copied through unchanged but its checksum moved.");
                    }
                    continue;
                }

                if (clock.Elapsed > VerifyParseBudget)
                {
                    notReached++;
                    continue;
                }

                try
                {
                    if (!image.ParseImage())
                    {
                        Defect($"'{path}' was rewritten and cannot be read back at all — " +
                               "what is in the file is not a WZ image.");
                    }
                    else if (before.PropertyCount > 0 && image.WzProperties.Count == 0)
                    {
                        Defect($"'{path}' held {before.PropertyCount} properties and was rewritten with none.");
                    }
                    opened++;
                }
                catch (Exception ex)
                {
                    Defect($"'{path}' was rewritten and cannot be read back: {ex.Message}");
                }
                finally
                {
                    // The probe is disposed in a moment, but the peak is what
                    // matters: holding every rewritten image of a large archive
                    // open at once is gigabytes, for a check already finished.
                    image.UnparseImage();
                }
            }

            if (actualCount != expected.ImageCount)
                Defect($"The saved file contains {actualCount:N0} images but {expected.ImageCount:N0} were expected.");

            List<string> missing = expected.ByPath.Keys.Where(p => !seen.Contains(p)).Take(5).ToList();
            if (missing.Count > 0)
                Defect("These images are missing from the saved file: " + string.Join(", ", missing));
            if (unexpected.Count > 0)
                Defect("These images are in the saved file but were not in the archive: " + string.Join(", ", unexpected));

            if (suppressed > 0)
                defects.Add($"...and {suppressed:N0} more of the same kind.");

            if (notReached > 0)
            {
                notes.Add(
                    $"Verification opened {opened:N0} of the {opened + notReached:N0} re-serialised images and " +
                    $"stopped at {VerifyParseBudget.TotalSeconds:F0} seconds. The other {notReached:N0} are " +
                    "present in the saved file's directory table, but their contents were not read back.");
            }
            return defects;
        }
        finally
        {
            probe.Dispose();
        }
    }

    /// <summary>
    /// Re-opens the untouched original after a failed save.  Without this the
    /// session is left holding a disposed archive and every later request
    /// fails with a null reference rather than a usable message.
    /// </summary>
    private void RecoverToOriginal(
        OpenFile file, string originalPath, WzMapleVersion version, byte[]? iv, short gameVersion)
    {
        try
        {
            file.WzFile?.Dispose();
            file.WzFile = null;
            Reopen(file, originalPath, version, iv, gameVersion);
            file.FilePath = originalPath;
            // The edits are gone -- SaveToDisk unparsed the tree -- so the file
            // is clean again relative to what is on disk.
            file.Dirty = false;
        }
        catch (Exception ex)
        {
            // The entry stays listed so the user can close it deliberately,
            // rather than every later click failing with an opaque 500.
            file.Detached = true;
            _log.LogError(ex, "Could not reopen {Path} after a failed save", originalPath);
        }
    }

    /// <summary>One image as it stood before the write.</summary>
    private sealed class ImageIdentity
    {
        /// <summary>The size of the image's block in the file it came from.</summary>
        public int BlockSize { get; init; }

        /// <summary>The directory table's checksum over that block.</summary>
        public int Checksum { get; init; }

        /// <summary>
        /// True when this save re-serialises the image rather than copying its
        /// bytes, so its size and checksum are expected to move and only its
        /// contents can be compared.
        /// </summary>
        public bool Rewritten { get; init; }

        /// <summary>
        /// How many properties the image held, or -1 when nothing had opened it.
        /// An image that had contents and comes back with none is the shape of a
        /// failed parse serialised as an empty tree — a total loss the inventory
        /// comparison happily matches on.
        /// </summary>
        public int PropertyCount { get; init; }

        /// <summary>
        /// Set when a second image shares this path. Neither side can then be
        /// matched to the right one, so both are checked for presence only.
        /// </summary>
        public bool Ambiguous { get; set; }
    }

    /// <summary>
    /// What we believed we were writing: every image in the archive, keyed by
    /// path relative to the root directory.
    ///
    /// The root name has to be dropped: the candidate file is written under a
    /// temp name, so its root directory is called "mbsave-..." rather than
    /// "Etc.wz" and an absolute comparison would report every image as missing.
    /// </summary>
    private sealed class SaveInventory
    {
        /// <summary>Total images including duplicate paths — what the count check compares.</summary>
        public int ImageCount { get; set; }

        public Dictionary<string, ImageIdentity> ByPath { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="everyImageRewritten">
    /// True when the save re-serialises the whole archive, so no image's size or
    /// checksum can be expected to survive. See <see cref="WhyEveryImageIsRewritten"/>.
    /// </param>
    private static SaveInventory TakeInventory(WzDirectory root, bool everyImageRewritten)
    {
        SaveInventory inventory = new();
        foreach (WzImage image in EnumerateImages(root))
        {
            inventory.ImageCount++;
            string path = StripRoot(image.FullPath);
            if (inventory.ByPath.TryGetValue(path, out ImageIdentity? clash))
            {
                clash.Ambiguous = true;
                continue;
            }
            inventory.ByPath[path] = new ImageIdentity
            {
                BlockSize = image.BlockSize,
                Checksum = image.Checksum,
                Rewritten = everyImageRewritten || image.Changed,
                // Reading WzProperties would parse the image, and parsing every
                // image of a client archive to take an inventory is not a thing
                // this method may do. Preflight has already opened the dirty ones,
                // which are the ones whose contents are worth comparing.
                PropertyCount = image.Parsed ? image.WzProperties.Count : -1,
            };
        }
        return inventory;
    }

    /// <summary>Every image under a directory, depth first.</summary>
    private static IEnumerable<WzImage> EnumerateImages(WzDirectory root)
    {
        Stack<WzDirectory> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            WzDirectory dir = pending.Pop();
            foreach (WzImage image in dir.WzImages)
                yield return image;
            foreach (WzDirectory sub in dir.WzDirectories)
                pending.Push(sub);
        }
    }

    /// <summary>
    /// Every image in an open file, changed or not — the counterpart to
    /// <see cref="OpenFile.EnumerateDirtyImages"/> for the saves that rewrite
    /// everything.
    /// </summary>
    private static IEnumerable<WzImage> EnumerateAllImages(OpenFile file)
    {
        if (file.LooseImage != null)
            return new[] { file.LooseImage };
        return file.WzFile?.WzDirectory == null
            ? Array.Empty<WzImage>()
            : EnumerateImages(file.WzFile.WzDirectory);
    }

    private static string StripRoot(string fullPath)
    {
        int separator = fullPath.IndexOf('\\');
        return separator < 0 ? fullPath : fullPath[(separator + 1)..];
    }

    private void Reopen(OpenFile file, string path, WzMapleVersion version, byte[]? iv, short gameVersion)
    {
        WzFile reopened = new(path, gameVersion, version);
        WzFileParseStatus status = reopened.ParseWzFile(iv);
        if (status != WzFileParseStatus.Success)
        {
            reopened.Dispose();
            throw new InvalidOperationException(
                $"The file was written to '{path}' but could not be reopened: {status.GetErrorDescription()}. " +
                "The saved file is on disk — verify it before overwriting your client.");
        }
        file.WzFile = reopened;
        file.Name = reopened.Name;

        // Every node the session handed out belongs to the archive we just threw
        // away. InvalidateResolution both drops the path cache and ticks
        // Generation, which is what the browse-list caches key on -- without it a
        // failed save could leave a mob grid showing an edited value that now
        // exists neither on disk nor in memory, and marked clean.
        _session.InvalidateResolution();
    }

    private SaveResult SaveLooseImage(OpenFile file, SaveRequest request)
    {
        WzImage image = file.LooseImage
            ?? throw new InvalidOperationException("This entry has no image to save.");

        // A loose image (including a hotfix Data.wz) has no archive directory
        // table for VerifyCandidate to inventory. Its equivalent is stronger and
        // cheaper: hash the complete parsed property tree now, then parse and hash
        // the serialized candidate before the original path is touched. The hash
        // is structural, so a serializer changing offsets or encryption bytes is
        // harmless; a missing member, bonus, canvas or value is not.
        WzContentHasher.ClearCache();
        string expectedHash = WzContentHasher.Hash(image);

        string destination = Path.GetFullPath(request.TargetPath ?? file.FilePath);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        SaveGuards.GuardResult guard = SaveGuards.Check(destination, File.Exists(destination), file.FilePath);
        if (!guard.CanProceed)
            throw new InvalidOperationException(guard.Blocker);

        WzMapleVersion version = WzSessionService.ParseVersion(request.MapleVersion) ?? file.MapleVersion;
        byte[] iv = file.CustomIv ?? WzTool.GetIvByMapleVersion(version);

        // WzImgSerializer appends ".img" unless the path already ends in exactly
        // that, so the temp name has to carry the extension or the bytes land
        // somewhere we never look at.
        string temp = Path.Combine(directory, NewTempName(".img"));
        DateTime started = DateTime.UtcNow;

        try
        {
            WzImgSerializer serializer = new(iv);
            serializer.SerializeImage(image, temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (!File.Exists(temp))
        {
            throw new InvalidOperationException(
                "The image was serialised but no output file appeared; the original was left untouched.");
        }

        long bytes = new FileInfo(temp).Length;
        if (bytes == 0)
        {
            TryDelete(temp);
            throw new InvalidOperationException(
                "The image serialised to zero bytes; the original file was left untouched.");
        }

        // Same reason as the archive path: what is about to be swapped over the
        // user's file has to be on the device, not in the page cache.
        try
        {
            FlushToDisk(temp);
        }
        catch (Exception ex)
        {
            string rescued = KeepForRecovery(temp, destination);
            throw new InvalidOperationException(
                $"The image could not be flushed to disk, so '{Path.GetFileName(destination)}' was left " +
                $"untouched.\n\nWhat was written is kept at:\n{rescued}\n\nDetail: {ex.Message}", ex);
        }

        // Same ordering as SaveArchive: validation belongs before the swap. A
        // readable non-empty file is not enough — a truncated but parseable
        // Data.wz would pass both checks and lose whichever override rows fell
        // off its tail. Compare the complete content we meant to write with the
        // complete content read from the candidate on disk.
        try
        {
            VerifyLooseCandidate(temp, iv, expectedHash);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            throw new InvalidOperationException(
                $"The rewritten image did not verify, so '{Path.GetFileName(destination)}' was left " +
                $"untouched. Detail: {ex.Message}", ex);
        }

        // The deserializer keeps a FileStream open on the source for the life
        // of the session, so saving a loose .img back over itself would be a
        // sharing violation against our own handle. Release it first, then
        // reopen from wherever the file ended up.
        bool sameFile = string.Equals(
            Path.GetFullPath(destination), Path.GetFullPath(file.FilePath), StringComparison.OrdinalIgnoreCase);
        if (sameFile)
        {
            image.Dispose();
            file.LooseImage = null;
        }

        string? locked = SaveGuards.CheckWritable(destination);
        if (locked != null)
        {
            string rescued = KeepForRecovery(temp, destination);
            ReopenLooseImage(file, version, iv);
            throw new InvalidOperationException(
                $"{locked}\n\nYour edits were written to:\n{rescued}");
        }

        string? backupPath = null;
        try
        {
            if (File.Exists(destination))
            {
                // Same rule as SaveArchive: overwriting a *different* file always
                // leaves a backup, whatever the request asked for. "Save a copy"
                // sends Backup=false, and its dialog promises nothing is overwritten
                // without asking -- this path had the fix applied to archives only,
                // so a loose .img copied onto an existing one was still destroyed
                // silently.
                bool overwritingAnotherFile = !string.Equals(
                    destination, Path.GetFullPath(file.FilePath), StringComparison.OrdinalIgnoreCase);

                if (request.Backup || overwritingAnotherFile)
                {
                    backupPath = MakeBackupPath(destination);
                    ReplaceFile(temp, destination, backupPath);
                }
                else
                {
                    string displaced = Path.Combine(directory, DisplacedOriginalName(destination));
                    ReplaceFile(temp, destination, displaced);
                    TryDelete(displaced);
                }
            }
            else
            {
                File.Move(temp, destination);
            }
        }
        catch
        {
            if (backupPath != null && !File.Exists(destination) && File.Exists(backupPath))
                File.Move(backupPath, destination);
            if (File.Exists(temp))
            {
                string rescued = KeepForRecovery(temp, destination);
                ReopenLooseImage(file, version, iv);
                throw new InvalidOperationException(
                    $"The image could not be replaced. Your edits were written to:\n{rescued}");
            }
            ReopenLooseImage(file, version, iv);
            throw;
        }

        file.FilePath = destination;
        file.Dirty = false;

        // Same rotation as the archive path, for the same reason: a .img saved
        // fifty times over an afternoon left fifty backups nobody would ever open.
        if (backupPath != null)
        {
            int pruned = PruneBackups(destination);
            if (pruned > 0)
            {
                guard.Warnings.Add(
                    $"{pruned} older backup{(pruned == 1 ? " was" : "s were")} deleted — the {BackupsKept} most " +
                    $"recent .bak files beside '{Path.GetFileName(destination)}' are kept.");
            }
        }

        // Reload from disk rather than reusing the in-memory image: the
        // serializer leaves it in a state where a second save takes a different
        // branch and writes from a stale reader.
        ReopenLooseImage(file, version, iv);

        return new SaveResult
        {
            FileId = file.Id,
            SavedTo = destination,
            BackupPath = backupPath,
            ImagesRewritten = 1,
            Bytes = bytes,
            Seconds = (DateTime.UtcNow - started).TotalSeconds,
            Warnings = guard.Warnings,
        };
    }

    /// <summary>
    /// Re-reads a loose .img from its current path after the handle was released.
    /// </summary>
    private void ReopenLooseImage(OpenFile file, WzMapleVersion version, byte[] iv)
    {
        try
        {
            WzImgDeserializer deserializer = new(false);
            WzImage reopened = deserializer.WzImageFromIMGFile(
                file.FilePath, iv, Path.GetFileName(file.FilePath), out _);
            file.LooseImage = reopened;
            file.MapleVersion = version;
            file.Detached = false;
        }
        catch (Exception ex)
        {
            file.Detached = true;
            _log.LogError(ex, "Could not reopen the image at {Path}", file.FilePath);
        }
        finally
        {
            // In the finally, not the try. The save has already succeeded and
            // the file has already been marked clean by this point, so a failed
            // reopen used to leave the worst possible combination: a clean flag,
            // a stale tree, and every cache still serving pre-save data as
            // current. Invalidating on both paths costs one re-walk and makes
            // the failure visible instead of silent.
            _session.InvalidateResolution();
        }
        _undo.ClearForFile(file.Id);
    }

    /// <summary>
    /// Reads a serialized loose-image candidate from disk and proves that its
    /// complete property tree is the one held in memory before the write.
    /// </summary>
    private static void VerifyLooseCandidate(string path, byte[] iv, string expectedHash)
    {
        WzImgDeserializer deserializer = new(false);
        WzImage candidate = deserializer.WzImageFromIMGFile(
            path, iv, Path.GetFileName(path), out _);
        try
        {
            if (!candidate.ParseImage())
                throw new InvalidOperationException("the candidate cannot be parsed as a WZ image");

            WzContentHasher.ClearCache();
            string actualHash = WzContentHasher.Hash(candidate);
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "the candidate's complete property tree differs from the edited image");
            }

        }
        finally
        {
            candidate.Dispose();
            WzContentHasher.ClearCache();
        }
    }

    /// <summary>
    /// Says plainly what a failed save costs.
    ///
    /// MapleLib's writer unparses the whole tree as it serialises, so once a
    /// save has started the in-memory edits cannot be recovered even though the
    /// file on disk is untouched. Reporting only the second half of that would
    /// be a comfortable lie.
    /// </summary>
    private static string EditsLostNote() =>
        "The file on disk is unchanged, but the edits held in memory could not be kept " +
        "and the file has been reloaded from disk.";

    /// <summary>
    /// Pushes a finished temp file out of the OS page cache and onto the device,
    /// before anything irreversible happens to the file it is going to replace.
    ///
    /// Nothing else in the pipeline does this. <c>WzFile.SaveToDisk</c> closes
    /// its FileStream, which flushes to the *cache*; <see cref="VerifyCandidate"/>
    /// then reads the file back through that same cache, so a save that verified
    /// clean proved nothing at all about what had reached the platter. Losing
    /// power in the seconds after the swap could leave the client folder holding
    /// a truncated archive with the original already gone — and the rescue for
    /// that, the .bak, is exactly what a Save As does not write.
    ///
    /// Cost, measured on this machine: 71 ms for a 150 MB archive, and
    /// 0.25-2.8 s for a 939 MB one against the 3.6 s MapleLib spends writing
    /// it. That is not overhead so much as relocated time — it is how long the
    /// bytes take to actually reach the device, which was previously spent
    /// after the swap with nothing waiting on it and nothing to fall back to.
    /// </summary>
    private static void FlushToDisk(string path)
    {
        // Write access is required for FlushFileBuffers, and Flush(true) issues
        // it against the handle whether or not this stream buffered anything.
        using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Swaps <paramref name="temp"/> over <paramref name="destination"/>, moving
    /// whatever was there to <paramref name="backup"/>.
    ///
    /// <c>File.Replace</c> wraps Win32 <c>ReplaceFile</c>, which swaps in one
    /// step and keeps the destination's ACLs — and which fails with
    /// ERROR_NOT_SUPPORTED on FAT32, exFAT and a good number of SMB shares.
    /// Without the fallback below, *every* save to an external drive or a NAS
    /// failed, each one leaving a full-size .recovered copy behind, and keeping a
    /// client on a USB disk is an ordinary thing to do.
    ///
    /// The fallback opens a window in which the destination does not exist. That
    /// window is exactly the one the backup covers: the user's file is sitting
    /// at <paramref name="backup"/> for the whole of it, the catch here puts it
    /// straight back, and the caller's catch does the same. Nothing is deleted
    /// to make room at any point. It does not carry the destination's ACLs over
    /// — the new file inherits the folder's — which on a FAT32 or exFAT volume
    /// costs nothing, because there are none to carry.
    /// </summary>
    private void ReplaceFile(string temp, string destination, string backup)
    {
        try
        {
            File.Replace(temp, destination, backup, ignoreMetadataErrors: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Could equally be a genuine lock, in which case the move below fails
            // too and the caller reports it — one path, no guessing at HRESULTs.
            _log.LogWarning(ex, "File.Replace did not work on {Path}; moving the original aside instead", destination);
        }

        // ReplaceFile can fail with the backup already created. Clearing it is
        // safe precisely because the destination is still there -- what is at
        // `backup` can only be a duplicate of a file that still exists.
        if (File.Exists(backup) && File.Exists(destination))
            TryDelete(backup);

        File.Move(destination, backup);
        try
        {
            File.Move(temp, destination);
        }
        catch
        {
            File.Move(backup, destination);   // put the user's file back before saying anything
            throw;
        }
    }

    /// <summary>
    /// Moves a successfully written temp file somewhere the user can find it,
    /// for when everything worked except the final swap.
    /// </summary>
    private string KeepForRecovery(string temp, string destination)
    {
        string rescued = $"{destination}.{Stamp()}.recovered";
        try
        {
            int counter = 2;
            while (File.Exists(rescued))
                rescued = $"{destination}.{Stamp()}-{counter++}.recovered";
            File.Move(temp, rescued);
            _log.LogWarning("Save could not complete; the written archive is at {Path}", rescued);
            return rescued;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not preserve the temp file {Temp}", temp);
            return temp;   // still better than deleting it
        }
    }

    /// <summary>
    /// The timestamp every file this service leaves beside an archive is named
    /// with: UTC, and the trailing Z says so.
    ///
    /// It was local time. In the hour that repeats at the daylight-saving
    /// fall-back that produces two backups an hour apart whose names sort the
    /// wrong way round — and the name is the only thing the user has to tell
    /// them apart, and the only thing <see cref="PruneBackups"/> would have had
    /// to decide which is oldest.
    /// </summary>
    private static string Stamp() => $"{DateTime.UtcNow:yyyyMMdd-HHmmss}Z";

    /// <summary>
    /// "Etc.wz" -> "Etc.wz.20260804-2118Z.bak", never overwriting an existing backup.
    /// </summary>
    private static string MakeBackupPath(string path)
    {
        string candidate = $"{path}.{Stamp()}.bak";
        int counter = 2;
        while (File.Exists(candidate))
            candidate = $"{path}.{Stamp()}-{counter++}.bak";
        return candidate;
    }

    /// <summary>
    /// Keeps the <see cref="BackupsKept"/> newest .bak files beside one archive
    /// and deletes the rest.
    ///
    /// Nothing pruned them before, so every backed-up save of a 1.5 GB Map.wz
    /// added another 1.5 GB to the client folder, for ever. Ordered by write
    /// time rather than by name, because names written before the switch to UTC
    /// cannot be compared with names written after it.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    private int PruneBackups(string destination)
    {
        try
        {
            string? directory = Path.GetDirectoryName(destination);
            string name = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
                return 0;

            List<FileInfo> ours = new();
            foreach (string entry in Directory.GetFiles(directory, name + ".*.bak", SearchOption.TopDirectoryOnly))
            {
                // As in the orphan sweep, the wildcard is a hint and the name is
                // the test: this deletes files in a folder full of the user's game
                // data, and "Etc.wz.before-the-event.bak" is not ours to remove.
                if (IsOurBackup(Path.GetFileName(entry), name))
                    ours.Add(new FileInfo(entry));
            }
            if (ours.Count <= BackupsKept)
                return 0;

            int removed = 0;
            foreach (FileInfo old in ours.OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsKept))
            {
                try
                {
                    long bytes = old.Length;
                    old.Delete();
                    removed++;
                    _log.LogInformation("Removed the old backup {Path} ({Bytes} bytes)", old.FullName, bytes);
                }
                catch (Exception ex)
                {
                    // Read-only, in use, or gone since the listing. Disk space is
                    // not worth failing a save that already succeeded.
                    _log.LogDebug(ex, "Could not remove the old backup {Path}", old.FullName);
                }
            }
            return removed;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not prune the backups beside {Path}", destination);
            return 0;
        }
    }

    /// <summary>
    /// True only for "&lt;archive&gt;.&lt;stamp&gt;.bak" exactly as
    /// <see cref="MakeBackupPath"/> writes it — <c>yyyyMMdd-HHmmss</c>, an
    /// optional <c>Z</c>, and an optional <c>-2</c> for a same-second collision.
    ///
    /// The Z is optional because backups written before the switch to UTC are
    /// still the user's backups and still have to rotate out. This decides what
    /// gets deleted, so anything that is not this shape belongs to someone else.
    /// </summary>
    private static bool IsOurBackup(string fileName, string archiveName)
    {
        const string suffix = ".bak";
        if (!fileName.StartsWith(archiveName + ".", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> stamp = fileName.AsSpan(
            archiveName.Length + 1, fileName.Length - archiveName.Length - 1 - suffix.Length);
        if (stamp.Length < 15)
            return false;

        for (int i = 0; i < 15; i++)
        {
            if (i == 8 ? stamp[i] != '-' : !char.IsAsciiDigit(stamp[i]))
                return false;
        }

        ReadOnlySpan<char> rest = stamp[15..];
        if (rest.Length > 0 && rest[0] == 'Z')
            rest = rest[1..];
        if (rest.Length == 0)
            return true;
        if (rest[0] != '-' || rest.Length == 1)
            return false;
        foreach (char c in rest[1..])
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup of our own temp file */ }
    }

    #region Orphan sweep

    /// <summary>
    /// "mbsave-16244-3f2a...c1.wz" — the archive this app is in the middle of
    /// writing, stamped with the id of the process writing it.
    ///
    /// The pid is what turns "is this file abandoned?" from a guess into a
    /// question with an answer. Without it the sweep can only reason about age,
    /// and it has to be generous — a save in flight must never be a candidate —
    /// so a full-size leftover from a crash five minutes ago sits in the user's
    /// client folder for a day. <c>Program.SweepAbandonedScratch</c> already
    /// solves the identical problem for the scratch folders the same way, by
    /// naming each after the process that owns it; this brings the archive-folder
    /// leftovers under the same rule.
    /// </summary>
    private static string NewTempName(string extension) =>
        $"{TempPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}{extension}";

    /// <summary>
    /// "Etc.wz" -> "mbsave-replaced-16244-3f2a...c1-Etc.wz" — the user's own
    /// archive, moved aside so its replacement can take its place.
    ///
    /// It is deleted the instant the swap completes, so this name is only ever
    /// seen if the process died in between. That is the case it is for: the file
    /// is then the only copy of what the user opened, and under the old
    /// "Etc.wz.replacing" nothing would restore it, nothing would sweep it, and
    /// nothing would tell them what it was. The name now carries the prefix the
    /// sweep looks for, says which of the two kinds of leftover it is, and ends
    /// in the archive's own file name so it says what it is without any tooling
    /// at all.
    /// </summary>
    private static string DisplacedOriginalName(string destination) =>
        $"{TempPrefix}{ReplacedInfix}{Environment.ProcessId}-{Guid.NewGuid():N}-{Path.GetFileName(destination)}";

    /// <summary>
    /// Deletes abandoned <c>mbsave-*</c> scratch files from the given folders.
    ///
    /// A save writes the whole archive to a sibling temp file before swapping it
    /// in, so a crash — or a machine losing power — mid-save leaves a full-size
    /// copy of Map.wz sitting in the user's client folder under a name that
    /// means nothing to them.  Nothing else ever cleans those up.
    ///
    /// This deletes files in a folder full of the user's game data, so it is
    /// deliberately narrow:
    ///   * only names matching exactly what <see cref="NewTempName"/> or
    ///     <see cref="DisplacedOriginalName"/> produces — the literal prefix, an
    ///     optional process id, a 32-character GUID, and a <c>.wz</c> or
    ///     <c>.img</c> extension.  A <c>.wz</c>, a <c>.bak</c>, a
    ///     <c>.recovered</c> or anything a user named themselves cannot match;
    ///   * only the top level of each folder, never a subtree;
    ///   * only files whose owner is demonstrably gone — either the process named
    ///     in the file's own name is no longer running, or (for a name written by
    ///     a build that did not carry one) the file has been untouched for
    ///     <see cref="OrphanAge"/>. A save running right now, in this process or
    ///     another copy of the app, is never a candidate under either rule;
    ///   * every failure is ignored.  Leaving an orphan behind costs disk space;
    ///     being noisy about it costs the user's attention for nothing.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public int SweepOrphanedTempFiles(IEnumerable<string> directories)
    {
        int removed = 0;
        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string full;
            try { full = Path.GetFullPath(directory); }
            catch { continue; }   // not a path we can reason about; leave it alone

            lock (_sweptDirectories)
            {
                if (!_sweptDirectories.Add(full))
                    continue;
            }
            removed += SweepDirectory(full);
        }
        return removed;
    }

    /// <summary>
    /// The folders of every file currently in the session — which is where a
    /// crashed save's leftovers can be.
    /// </summary>
    public int SweepOrphanedTempFiles() =>
        SweepOrphanedTempFiles(_session.Files
            .Select(f => Path.GetDirectoryName(f.FilePath) ?? "")
            .ToList());

    private int SweepDirectory(string directory)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFiles(directory, TempPrefix + "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not list {Directory} while looking for leftover temp files", directory);
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow - OrphanAge;
        int removed = 0;

        foreach (string entry in entries)
        {
            // The wildcard above is a hint, not the test: Windows matches 8.3
            // short names as well as long ones, so the real name is what decides.
            if (!IsOurTempFile(Path.GetFileName(entry), out int owner))
                continue;

            try
            {
                FileInfo info = new(entry);
                if (!info.Exists)
                    continue;

                // The later of the two stamps. A file copied or restored into
                // place carries an old creation time with a fresh write time,
                // and either one being recent is reason enough to keep it.
                DateTime touched = info.LastWriteTimeUtc > info.CreationTimeUtc
                    ? info.LastWriteTimeUtc
                    : info.CreationTimeUtc;

                // The name says which process was writing it, so the question is
                // answerable exactly rather than by waiting a day. This is the
                // whole point of the pid: an interrupted save leaves a full-size
                // copy of a 1.6 GB archive in the user's client folder, and under
                // the age rule alone it stayed there until tomorrow -- long past
                // the moment the user noticed it and asked what it was.
                //
                // A name with no pid was written by an older build, and the age
                // rule is all there is for it.
                // The two rules compose in one direction only: the pid can make a
                // file a candidate sooner, never later. Windows recycles process
                // ids, so a leftover whose pid has been handed to something else
                // would otherwise look alive for ever and never be swept at all.
                bool abandoned = (owner > 0 && !IsProcessAlive(owner)) || touched <= cutoff;
                if (!abandoned)
                    continue;

                long bytes = info.Length;
                bool displaced = IsDisplacedOriginal(Path.GetFileName(entry));

                // A displaced original is only stale once its replacement is
                // actually there, and until now nothing checked.
                //
                // ReplaceFile's fallback -- the branch taken on FAT32, exFAT and
                // a good number of SMB shares, i.e. every save to a USB stick or
                // a NAS -- moves the archive aside and then moves the new file
                // in, and between those two lines the destination does not
                // exist. Lose power there and what is on disk is: no Etc.wz, and
                // the user's only copy of it under this name. The pid rule then
                // makes it a candidate immediately (the writer is dead by
                // definition), so the next open or save in that folder deleted
                // the one remaining copy and logged that "its replacement is
                // already in place" -- which was the one thing nobody had asked.
                //
                // Left alone instead, and said out loud. A full-size file the
                // user has to rename back by hand is a bad afternoon; deleting
                // it is unrecoverable.
                if (displaced && DisplacedFrom(Path.GetFileName(entry)) is { } original
                    && !File.Exists(Path.Combine(directory, original)))
                {
                    _log.LogWarning(
                        "Keeping {Path} ({Bytes} bytes): it is your original '{Original}', moved aside by a save " +
                        "that did not finish, and there is no '{Original}' beside it. Rename it back to recover.",
                        entry, bytes, original, original);
                    continue;
                }

                info.Delete();
                removed++;

                // The two kinds of leftover are worth telling apart in the log:
                // one is output we produced and the other was, until an
                // interrupted save, the user's archive -- by the time it is a day
                // old its replacement has been in place all along, so it is a
                // stale copy either way, but the log should not pretend they are
                // the same thing.
                if (displaced)
                {
                    _log.LogInformation(
                        "Removed {Path} ({Bytes} bytes, last touched {Touched:u}) — the original archive that an " +
                        "interrupted save had moved aside; its replacement is already in place",
                        entry, bytes, touched);
                }
                else
                {
                    _log.LogInformation("Removed leftover save file {Path} ({Bytes} bytes, last touched {Touched:u})",
                        entry, bytes, touched);
                }
            }
            catch (Exception ex)
            {
                // In use, read-only, or gone since the listing. All fine.
                _log.LogDebug(ex, "Could not remove the leftover save file {Path}", entry);
            }
        }
        return removed;
    }

    /// <summary>
    /// True only for a name this service could itself have written — either
    /// "mbsave-&lt;pid&gt;-&lt;guid&gt;.wz" from <see cref="NewTempName"/> or
    /// "mbsave-replaced-&lt;pid&gt;-&lt;guid&gt;-Etc.wz" from
    /// <see cref="DisplacedOriginalName"/>.
    ///
    /// The pid is optional because a build before it existed wrote the same two
    /// shapes without one, and its leftovers are still the user's to clean up.
    /// There is no ambiguity between the two: a GUID in "N" form is 32 hex
    /// characters and a process id is a run of digits far shorter than that, so
    /// whichever the first segment parses as is what it is.
    ///
    /// The prefix comparison is case-sensitive on purpose: we always write
    /// lower case, so a file the user happens to have called "MVSAVE-..." is
    /// somebody else's and stays.
    /// </summary>
    /// <param name="owner">
    /// The process id in the name, or 0 when it carries none. The sweep uses it
    /// to ask whether the writer is still running rather than waiting out
    /// <see cref="OrphanAge"/>.
    /// </param>
    private static bool IsOurTempFile(string fileName, out int owner)
    {
        owner = 0;
        if (!fileName.StartsWith(TempPrefix, StringComparison.Ordinal))
            return false;

        string extension = Path.GetExtension(fileName);
        if (!extension.Equals(".wz", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".img", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The name starts with a dot-free prefix, so the extension's dot is
        // always past it and these two spans cannot overlap.
        ReadOnlySpan<char> stem = fileName.AsSpan(
            TempPrefix.Length, fileName.Length - TempPrefix.Length - extension.Length);

        bool replaced = stem.StartsWith(ReplacedInfix, StringComparison.Ordinal);
        if (replaced)
            stem = stem[ReplacedInfix.Length..];

        // An optional leading "<digits>-". Only consumed when what follows it
        // still looks right, so a name that merely begins with digits cannot
        // trick the parse into accepting a stem that is not a GUID.
        int firstDash = stem.IndexOf('-');
        if (firstDash > 0 && IsAllAsciiDigits(stem[..firstDash])
            && int.TryParse(stem[..firstDash], out int pid) && pid > 0)
        {
            owner = pid;
            stem = stem[(firstDash + 1)..];
        }

        if (replaced)
        {
            // What is left is "<guid>-<the archive's own name>". A GUID in "N"
            // form holds no dashes, so the first one is where the GUID ends and
            // the borrowed name begins.
            int dash = stem.IndexOf('-');
            if (dash < 0)
            {
                owner = 0;
                return false;
            }
            stem = stem[..dash];
        }

        if (Guid.TryParseExact(stem, "N", out _))
            return true;

        owner = 0;
        return false;
    }

    private static bool IsAllAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return text.Length > 0;
    }

    /// <summary>
    /// Whether the process that wrote a leftover is still running.
    ///
    /// The name is checked as well as the id, because Windows reuses process
    /// ids: without it, a leftover whose pid now belongs to an unrelated program
    /// looks alive and is never swept. A pid we cannot ask about at all counts as
    /// alive, which leaves the file for the age rule — the direction that keeps
    /// an in-flight save's scratch file safe.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process other = Process.GetProcessById(pid);
            using Process self = Process.GetCurrentProcess();
            return string.Equals(other.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;   // no such process: this leftover has no owner
        }
        catch
        {
            return true;    // cannot tell; leave it to the age rule
        }
    }

    /// <summary>
    /// True for the "mbsave-replaced-..." half of <see cref="IsOurTempFile"/> —
    /// the leftovers that are the user's own archive rather than our output.
    /// </summary>
    private static bool IsDisplacedOriginal(string fileName) =>
        fileName.StartsWith(TempPrefix + ReplacedInfix, StringComparison.Ordinal);

    /// <summary>
    /// The archive a displaced original was moved aside from — the trailing
    /// "Etc.wz" of "mbsave-replaced-4212-&lt;guid&gt;-Etc.wz" — or null when the
    /// name does not carry one.
    ///
    /// This is the whole reason <see cref="DisplacedOriginalName"/> ends in the
    /// archive's own file name rather than stopping at the GUID: the sweep has to
    /// be able to ask "is the file this one was replacing actually there?" before
    /// it deletes what may be the user's only copy. Read as the tail after the
    /// GUID, and the GUID's "N" form holds no dashes, so the dash that follows it
    /// is unambiguous.
    ///
    /// Null on anything unexpected, and the caller then keeps the file. Failing
    /// towards "leave it alone" is the only safe direction here.
    /// </summary>
    private static string? DisplacedFrom(string fileName)
    {
        if (!IsDisplacedOriginal(fileName))
            return null;

        ReadOnlySpan<char> rest = fileName.AsSpan(TempPrefix.Length + ReplacedInfix.Length);

        // An optional process id, then the GUID, then the borrowed name. Both
        // leading fields are dash-terminated and neither can contain a dash.
        int dash = rest.IndexOf('-');
        if (dash < 0)
            return null;
        if (IsAllAsciiDigits(rest[..dash]))
        {
            rest = rest[(dash + 1)..];
            dash = rest.IndexOf('-');
            if (dash < 0)
                return null;
        }

        if (!Guid.TryParseExact(rest[..dash], "N", out _))
            return null;

        ReadOnlySpan<char> original = rest[(dash + 1)..];
        return original.IsEmpty ? null : original.ToString();
    }

    #endregion
}
