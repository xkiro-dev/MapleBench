using System.Diagnostics;
using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.MSFile;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using MapleBench.Models;
using MapleLib.Img;

namespace MapleBench.Services;

/// <summary>
/// Owns every open WZ file for the lifetime of the process, resolves session
/// paths to MapleLib objects, and projects those objects into DTOs.
///
/// MapleLib objects are not thread-safe and lazy-parse from a shared reader, so
/// every public entry point here takes <see cref="Gate"/>.  Callers that need to
/// perform a multi-step operation atomically should take the same lock.
/// </summary>
public sealed class WzSessionService : IDisposable
{
    private readonly Dictionary<string, OpenFile> _files = new(StringComparer.Ordinal);
    private readonly ILogger<WzSessionService> _log;
    private int _nextId = 1;

    /// <summary>Guards all MapleLib object access. Public so sibling services can share it.</summary>
    public object Gate { get; } = new();

    public WzSessionService(ILogger<WzSessionService> log)
    {
        _log = log;

        // Lets canvases with an '_outlink' into a different archive (e.g. a Mob.wz
        // canvas pointing at Mob001.wz) resolve against whatever else is open.
        WzCanvasProperty.ExternalImageResolver = ResolveExternalImage;
    }

    public IReadOnlyCollection<OpenFile> Files
    {
        get { lock (Gate) return _files.Values.ToList(); }
    }

    /// <summary>
    /// How many archives are open.
    ///
    /// Separate from <see cref="Files"/> because the only thing several callers
    /// want is the count as a staleness test, and <see cref="Files"/> copies the
    /// whole dictionary to answer it — <see cref="StringPoolService.EnsureBuilt"/>
    /// asked it once per name lookup, i.e. 2,742 times to build one mob list.
    ///
    /// This does take the gate, once per call, and that was measured rather than
    /// assumed: a lock-free published snapshot of the file table was built,
    /// benchmarked against this, and thrown away. On a v232 client the ten first
    /// tree expansions after opening it came to 1,050-1,137 ms with the snapshot
    /// and 975-1,073 ms without — no difference, because what a per-row lookup
    /// waits for is a background build's chunk, not the microseconds of the
    /// acquisition itself. What the snapshot did add was an invariant somebody
    /// has to keep ("republish after every change to _files"), and it was already
    /// silently wrong for anything that registers a file another way. A cache
    /// that can quietly describe a session that has moved on is not worth zero
    /// milliseconds.
    /// </summary>
    public int FileCount
    {
        get { lock (Gate) return _files.Count; }
    }

    /// <summary>
    /// The open archive a session path belongs to, or null when there is no such
    /// file.
    ///
    /// For callers to whom "not open" is an ordinary answer — a row below a
    /// closed archive is simply not named — rather than an error worth an
    /// exception per row. <see cref="GetFile"/> remains the one that throws.
    /// </summary>
    public OpenFile? PeekFileForPath(string path)
    {
        string fileId = WzPath.FileId(path);
        lock (Gate)
            return fileId.Length > 0 && _files.TryGetValue(fileId, out OpenFile? file) ? file : null;
    }

    public List<OpenFileDto> ListFiles()
    {
        lock (Gate)
            return _files.Values.Select(f => f.ToDto()).ToList();
    }

    #region Opening

    /// <summary>
    /// Opens a .wz archive, a .ms archive, or a loose .img.  Re-opening a path
    /// that is already in the session returns the existing entry instead of
    /// loading a second copy.
    /// </summary>
    /// <summary>
    /// Warns when an archive's text could not be represented on the way in.
    ///
    /// This used to be the only defence against a much worse problem: MapleLib
    /// decoded single-byte WZ strings as ASCII, so every byte above 0x7F in a
    /// Korean, Japanese or Chinese client became '?' — and because directory and
    /// image *names* are rewritten from memory on every save, even for images
    /// whose bodies are copied through untouched, one save renamed everything in
    /// such an archive permanently, with the inventory check comparing the
    /// mangled names against themselves and finding nothing wrong.
    ///
    /// That is fixed at the source as of 2026-08-06: the reader decodes
    /// single-byte strings as Latin-1 and the writer's one-byte threshold moved
    /// to match, so those names now round-trip byte for byte. The characters are
    /// still not *correct* for a multi-byte code page — a CP949 name reads as
    /// mojibake — so this check is kept as a display-quality warning rather than
    /// a corruption warning, and it will now only fire on names that genuinely
    /// contain '?'.
    ///
    /// Returns null when the archive looks fine.
    /// </summary>
    public string? DescribeMangledText(OpenFile file)
    {
        WzDirectory? root = file.WzFile?.WzDirectory;
        if (root == null)
            return null;

        int mangled = 0;
        int inspected = 0;

        lock (Gate)
        {
            foreach (WzDirectory directory in root.WzDirectories)
            {
                inspected++;
                if (directory.Name?.Contains('?') == true)
                    mangled++;
            }
            // Root images only: this runs on every open and must stay cheap. A
            // client whose text does not survive shows it at the top level.
            foreach (WzImage image in root.WzImages)
            {
                inspected++;
                if (image.Name?.Contains('?') == true)
                    mangled++;
            }
        }

        // One '?' can be a legitimate name. A pattern of them cannot.
        if (mangled < 3 || inspected == 0)
            return null;

        return $"{file.Name}: {mangled} of {inspected} names contain '?'. Names round-trip through a " +
               "save unchanged, so nothing will be renamed, but this client's text is not Latin-1 and " +
               "will not read correctly here.";
    }

    /// <summary>
    /// Free memory below which an open is refused outright.
    ///
    /// Not a prediction of what the archive costs — the amplification between a
    /// file's size on disk and what it becomes in memory ranges from "almost
    /// nothing" (a .wz parses lazily) to six times (a .ms decrypts and parses
    /// every entry up front), so a model would be wrong in both directions. It
    /// is a floor: below it the process has no room to do the work the open
    /// leads to, and the observed failure is not a slow app but the window
    /// disappearing with nothing logged.
    /// </summary>
    internal const long MinimumFreeBytesToOpen = 384L * 1024 * 1024;

    /// <summary>
    /// The larger floor a .ms pack has to clear, because it is the one open that
    /// is not lazy.
    ///
    /// <c>WzMsFile.LoadAsWzFile</c> decrypts every entry into its own array,
    /// parses all of them, and marks each Changed — so none of it can ever be
    /// given back by <see cref="ImageMemoryService"/> while the entry is open.
    /// Same number and same reasoning as <c>ClientImportService</c>'s per-pack
    /// floor, which guards the identical code path reached the other way; that
    /// guard existed and this one did not, so opening the same pack through
    /// File -> Open walked straight past it.
    /// </summary>
    internal const long MinimumFreeBytesForPack = 1024L * 1024 * 1024;

    /// <summary>
    /// How much memory the machine has left, or -1 when that cannot be answered.
    /// A test seam; see the identical one on <c>ClientImportService</c> for why a
    /// guard that can only be exercised by exhausting the machine is a guard
    /// nobody has ever seen work.
    /// </summary>
    internal Func<long> FreeMemoryBytes { get; set; } = SystemMemory.FreeBytes;

    /// <summary>
    /// Refuses an open the machine has no room for, and says so.
    ///
    /// A refusal is the point. What it replaces is an allocation failure part
    /// way through a parse — or the process vanishing mid-collection, which
    /// leaves the user with no message, no log line, and, if they were editing,
    /// no work either.
    /// </summary>
    private void RefuseIfMemoryIsShort(string path, string extension)
    {
        // "Cannot tell" is not "no room": a platform that does not report a
        // memory limit must not have its opens refused.
        long free = FreeMemoryBytes();
        if (free < 0)
            return;

        long floor = extension == ".ms" ? MinimumFreeBytesForPack : MinimumFreeBytesToOpen;
        if (free >= floor)
            return;

        long sizeMB = 0;
        try { sizeMB = new FileInfo(path).Length / (1024 * 1024); }
        catch (IOException) { /* the size is for the message only */ }

        string cost = extension == ".ms"
            ? "a .ms pack is decrypted and parsed in full at open, and none of it can be released " +
              "while it is open"
            : "parsing it needs several times its size on disk";

        throw new InvalidOperationException(
            $"Not enough memory to open '{Path.GetFileName(path)}' ({sizeMB} MB): this machine has " +
            $"{free / (1024 * 1024)} MB free and {cost}. Close archives you are not using — or other " +
            "applications — and try again. Nothing was opened and nothing was changed.");
    }

    public OpenFile Open(OpenRequest request)
    {
        string path = Path.GetFullPath(request.Path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        lock (Gate)
        {
            OpenFile? existing = _files.Values.FirstOrDefault(
                f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (request.ReadOnly && !existing.ReadOnly)
                {
                    if (existing.Dirty || existing.CountDirtyImages() > 0)
                    {
                        throw new InvalidOperationException(
                            $"'{existing.Name}' already has unsaved changes, so it cannot be reused as a " +
                            "read-only import source. Save or close that copy first; nothing was changed.");
                    }
                    existing.ReadOnly = true;
                }
                return existing;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();

            // After the "already open" check above, so re-opening something the
            // session already holds costs nothing and can never be refused, and
            // before anything is allocated.
            RefuseIfMemoryIsShort(path, extension);

            OpenFile file = extension switch
            {
                ".ms" => OpenMsFile(path, request),
                ".img" => OpenLooseImg(path, request),
                _ => OpenWzFile(path, request),
            };

            // Import sources enter the session locked. Applying this before the
            // registration below means no request can ever observe a source
            // archive in a writable state, even briefly.
            file.ReadOnly = request.ReadOnly;

            _files[file.Id] = file;
            InvalidateResolution();
            _log.LogInformation("Opened {Name} ({Kind}) as {Id}", file.Name, file.Kind, file.Id);
            return file;
        }
    }

    /// <summary>
    /// Mounts a filesystem folder of standalone .img files as one lazy tree.
    /// The mount is reference-only: the existing save transaction protects one
    /// destination file, not a partially-written collection of independent files.
    /// </summary>
    public OpenFile OpenImgFolder(OpenRequest request)
    {
        string path = Path.GetFullPath(request.Path);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        lock (Gate)
        {
            OpenFile? existing = _files.Values.FirstOrDefault(
                f => f.Kind == "img-folder" &&
                     string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            if (!ContainsLooseImage(path))
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}' does not contain any .img files. Nothing was opened.");
            }

            DirectoryInfo selected = new(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? parent = selected.Parent?.FullName;
            if (parent == null || selected.Name.Length == 0)
                throw new InvalidOperationException("A drive root cannot be mounted as an IMG folder.");

            WzMapleVersion version = ParseVersion(request.MapleVersion) ?? WzMapleVersion.BMS;
            byte[]? customIv = ParseIv(request.Iv);
            WzMapleVersion managerVersion = customIv != null ? WzMapleVersion.CUSTOM : version;

            ImgFileSystemManager manager = new(parent, null, managerVersion, customIv);
            VirtualWzDirectory? root = manager.GetDirectory(selected.Name);
            if (root == null)
            {
                manager.Dispose();
                throw new InvalidOperationException($"'{selected.Name}' could not be mounted as an IMG folder.");
            }

            string id = NextId();
            OpenFile file = new(id, selected.Name, path, "img-folder")
            {
                FolderRoot = root,
                ImgFolderManager = manager,
                MapleVersion = managerVersion,
                CustomIv = customIv,
                ReadOnly = true,
            };

            _files[id] = file;
            InvalidateResolution();
            _log.LogInformation("Mounted IMG folder {Path} as {Id}, reference-only", path, id);
            return file;
        }
    }

    private static bool ContainsLooseImage(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                if (Directory.EnumerateFiles(current, "*.img", SearchOption.TopDirectoryOnly).Any())
                    return true;

                foreach (string child in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>
    /// Opens one archive of a modern split <c>Data\</c> client as an ordinary
    /// session entry, read-only, without converting anything.
    ///
    /// This is the cheap answer to the thing people actually want. A split
    /// archive is a directory of <c>_000.wz</c> parts, nested sub-archives and
    /// <c>.ms</c> packs; converting one to a classic <c>.wz</c> costs its full
    /// size in disk and minutes of writing, and gives you a file. Merging the
    /// same tree in memory costs seconds and no disk, and gives you the tree —
    /// after which every feature in this app that takes an open archive works
    /// against it unchanged: the node tree, search, thumbnails, the Database
    /// sections, and above all Port, which is what turns "I want that one boss"
    /// from a 13 GB conversion into ticking a row.
    ///
    /// <b>Read-only is not a default, it is the design.</b> Writing the split
    /// format back is a problem this app has no answer for — the parts, the
    /// <c>.ini</c> index, the pack container and its per-entry keys would all
    /// have to be rebuilt, and a half-right answer would corrupt the user's live
    /// client. Refusing every write means that question never has to be asked,
    /// and it costs the workflow nothing: nobody edits the client they are
    /// copying *out of*.
    /// </summary>
    /// <param name="sourceFolder">The client folder or its <c>Data</c> directory.</param>
    /// <param name="archiveName">An archive name as reported by detection — "Mob".</param>
    public OpenFile OpenSplitArchive(
        string sourceFolder, string archiveName, ClientImportService import,
        Action<string, long, long>? report = null, CancellationToken cancel = default)
        => OpenSplitArchive(import.Detect(sourceFolder), archiveName, import, report, cancel);

    /// <summary>
    /// Opens from the layout the current import operation already detected, so
    /// session registration and assembly cannot each repeat the recursive scan.
    /// </summary>
    public OpenFile OpenSplitArchive(
        ClientLayoutDto layout, string archiveName, ClientImportService import,
        Action<string, long, long>? report = null, CancellationToken cancel = default)
    {
        // Answered from the directory listing before anything is read, because the
        // work this skips is the whole open. Detection is a scan of .ini files;
        // assembling Mob is 6.7 seconds and 2.3 GB of pack buffers. Checking
        // afterwards -- which is what a file open can afford to do, since opening
        // a .wz twice is cheap -- meant a second click on an already-open Mob paid
        // all of that and then threw the result away, and the transient 2.3 GB was
        // indistinguishable from a leak while the collector caught up.
        SplitArchiveDto? known = layout.Archives.FirstOrDefault(
            a => a.Name.Equals(archiveName, StringComparison.OrdinalIgnoreCase));
        if (known != null)
        {
            lock (Gate)
            {
                OpenFile? already = _files.Values.FirstOrDefault(
                    f => string.Equals(f.FilePath, known.Path, StringComparison.OrdinalIgnoreCase));
                if (already != null)
                    return already;
            }
        }

        // Assembled outside the gate. It is seconds of file IO for a large archive
        // and the gate is the one every tree request takes, so holding it here
        // would freeze the UI for the whole open -- including the progress poll
        // that exists to report on it.
        AssembledArchive assembled = import.Assemble(layout, archiveName, report, cancel);

        try
        {
            lock (Gate)
            {
                // Checked again under the gate. The cheap check above is outside
                // it, so two opens of the same archive that start together both
                // get past it; without this one they would both register and the
                // loser's file handles and pack buffers would be unreachable.
                OpenFile? existing = _files.Values.FirstOrDefault(
                    f => string.Equals(f.FilePath, assembled.ArchiveDirectory, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    assembled.Dispose();
                    return existing;
                }

                OpenFile file = new(NextId(), assembled.ArchiveName + ".wz", assembled.ArchiveDirectory, "split")
                {
                    WzFile = assembled.File,
                    Assembled = assembled,
                    MapleVersion = WzMapleVersion.BMS,

                    // Set here rather than left to the caller. A split archive that
                    // was writable would be one whose save path silently wrote a
                    // classic .wz over a directory, and no amount of UI would make
                    // that safe.
                    ReadOnly = true,
                };

                _files[file.Id] = file;
                InvalidateResolution();
                _log.LogInformation(
                    "Opened split archive {Name} as {Id} — {Images} images from {Sources} source files, read-only",
                    file.Name, file.Id, assembled.Images, assembled.SourceFilesRead);
                return file;
            }
        }
        catch
        {
            assembled.Dispose();
            throw;
        }
    }

    private OpenFile OpenWzFile(string path, OpenRequest request)
    {
        // `IsListFile` is really a negative header test: it means "not PKG1",
        // not "proved to be List.wz". Modern clients also ship Data.wz as one
        // bare WzImage wearing a .wz extension. The old exception recognised only
        // the one header byte MapleLib happened to expect, so a valid Data.wz from
        // another layout was stopped as List.wz before its image parser was ever
        // allowed to decide.
        //
        // The filename is not trusted as proof. A non-PKG1 Data.wz is admitted
        // only through OpenLooseImg, which eagerly parses the complete image with
        // the supplied or standard IVs before an OpenFile is created. A List.wz,
        // damaged Data.wz, or arbitrary renamed file therefore still fails while
        // the session is untouched. Legacy PKG1 Data.wz continues down the normal
        // archive path.
        bool lacksPackageHeader = WzTool.IsListFile(path);
        bool namedData = Path.GetFileName(path).Equals("Data.wz", StringComparison.OrdinalIgnoreCase);
        if (lacksPackageHeader && (namedData || WzTool.IsDataWzHotfixFile(path)))
            return OpenLooseImg(path, request);

        // A List.wz has no WZ header and would fail the normal parse path with a
        // confusing error, so name it explicitly.
        if (lacksPackageHeader)
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' is a List.wz file, not a WZ archive. List files are not editable here.");

        (WzMapleVersion version, byte[]? iv, short gameVersion, bool assumed) = ResolveEncryption(path, request);

        WzFile wzFile = new(path, gameVersion, version);
        WzFileParseStatus status = wzFile.ParseWzFile(iv);

        // The assumed encryption did not fit this archive, so fall back to
        // detecting it from this file alone.
        //
        // "Assumed" means the version was not measured against *this* file: it
        // came from the caller, or from a sibling archive in the same folder
        // (see _folderVersions). A client folder is homogeneous in practice,
        // which is what makes the shortcut worth 250 ms a file — but a folder
        // someone has assembled by hand is not, and without this the first
        // mismatched archive in it would fail to open with an encryption error
        // for a file whose encryption we could have worked out.
        if (status != WzFileParseStatus.Success && assumed)
        {
            _log.LogInformation(
                "{File} did not parse as {Version}; detecting its encryption on its own.",
                Path.GetFileName(path), version);
            wzFile.Dispose();

            (version, iv, gameVersion) = DetectEncryption(path, request);
            wzFile = new WzFile(path, gameVersion, version);
            status = wzFile.ParseWzFile(iv);
        }

        if (status != WzFileParseStatus.Success)
        {
            wzFile.Dispose();
            throw new InvalidOperationException(
                $"Could not parse '{Path.GetFileName(path)}': {status.GetErrorDescription()}. " +
                "Try selecting the encryption (GMS / EMS / BMS) manually.");
        }

        // Only a version this file actually parsed with is remembered, and only
        // when the caller did not pin one — a pinned version is the caller's
        // business and must not become the folder's answer for everyone else.
        if (iv == null && ParseVersion(request.MapleVersion) is null)
            RememberFolderVersion(path, version, wzFile.Version);

        string id = NextId();
        return new OpenFile(id, wzFile.Name, path, "wz")
        {
            WzFile = wzFile,
            MapleVersion = version,
            CustomIv = iv,
        };
    }

    private OpenFile OpenMsFile(string path, OpenRequest request)
    {
        // Both the .ms buffer and the WzMsFile are done with once the archive has
        // been converted: LoadAsWzFile decrypts every entry into its own private
        // MemoryStream and hands that to the WzImage's reader, so the returned
        // WzFile never touches the stream we opened here.  Holding on to it kept
        // the entire pack resident a second time for the life of the session.
        using FileStream stream = File.OpenRead(path);

        // Sized from the file rather than grown into.
        //
        // MemoryStream doubles, and every intermediate is a large-object
        // allocation that the collector will not compact: copying a 139 MB pack
        // into a default MemoryStream allocates 1, 2, 4 ... 128 and finally
        // 256 MB, so ~395 MB is touched to hold 139 MB and the discarded halves
        // sit in the LOH until something forces a compacting collection. One
        // right-sized buffer is the same result for a third of the peak, and the
        // peak is what the process dies of.
        long length = stream.Length;
        if (length > int.MaxValue)
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' is {length / (1024 * 1024)} MB. A .ms pack has to be read " +
                "into memory whole, and a single buffer cannot exceed 2 GB.");

        using MemoryStream buffer = new((int)length);
        stream.CopyTo(buffer);
        buffer.Position = 0;

        WzFile wzFile;
        using (WzMsFile msFile = new(buffer, Path.GetFileName(path), path, leaveOpen: true))
        {
            msFile.ReadEntries();
            wzFile = msFile.LoadAsWzFile();
        }

        string id = NextId();
        return new OpenFile(id, wzFile.Name, path, "ms")
        {
            WzFile = wzFile,
            MapleVersion = wzFile.MapleVersion,
        };
    }

    private OpenFile OpenLooseImg(string path, OpenRequest request)
    {
        WzMapleVersion version = ParseVersion(request.MapleVersion) ?? WzMapleVersion.BMS;
        byte[]? customIv = ParseIv(request.Iv);
        byte[] iv = customIv ?? WzTool.GetIvByMapleVersion(version);

        WzImage? image = TryOpenLooseImg(path, iv);
        if (image == null)
        {
            // Loose .img encryption cannot be sniffed from a header, so walk the
            // three standard IVs before giving up.
            foreach (WzMapleVersion candidate in new[] { WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.BMS })
            {
                // When the first attempt used the standard IV for `version`, do
                // not repeat it. When it used a custom IV, the standard IV for
                // that same named version has not been tried and must stay in the
                // walk; skipping it made a mistyped custom IV block an otherwise
                // ordinary BMS Data.wz.
                if (customIv == null && candidate == version)
                    continue;
                image = TryOpenLooseImg(path, WzTool.GetIvByMapleVersion(candidate));
                if (image != null)
                {
                    version = candidate;
                    customIv = null;
                    break;
                }
            }
        }
        if (image == null)
            throw new InvalidOperationException(
                $"Could not decrypt '{Path.GetFileName(path)}' with the GMS, EMS or BMS keys. " +
                "Supply a custom IV if this is a private-server build.");

        string id = NextId();
        return new OpenFile(id, image.Name, path, "img")
        {
            LooseImage = image,
            MapleVersion = version,
            // A loose image has no archive header from which the key can be
            // rediscovered later. Keeping the custom IV is what makes Save write
            // it with the same bytes it successfully opened with instead of
            // silently falling back to the standard IV for `version`.
            CustomIv = customIv,
        };
    }

    /// <summary>
    /// One attempt at a loose .img with one key: the image when it decrypted,
    /// null when it did not.
    ///
    /// The parse is what decides, because the deserializer's own answer is not
    /// an answer. <c>WzImgDeserializer.WzImageFromIMGFile</c> sets its
    /// <c>successfullyParsedImage</c> out-parameter to <c>true</c> unconditionally
    /// on the <c>freeResources: false</c> path — it never parses anything, so it
    /// has nothing to report — and this is the only path MapleBench uses.
    ///
    /// What that cost, before this: the IV walk below and the "Could not decrypt"
    /// error were both unreachable. The very first attempt always "succeeded", so
    /// a GMS-encrypted .img opened with the BMS key produced a live OpenFile whose
    /// tree was silently empty, with no error and no hint that the key was wrong.
    /// The UX spec has wording written for a case that could not occur.
    ///
    /// The empty tree is the mild half. Add one property to that image and
    /// <c>WzImage.AddProperty</c> sets <c>Changed</c>; on save, <c>SaveImage</c>'s
    /// <c>forceRead</c> test is <c>properties.Count == 0</c>, which is now false,
    /// and <c>ParseImage</c> short-circuits on <c>Changed</c> and returns true
    /// without reading anything — so the file is rewritten as the one property
    /// that was added, and MapleLib's own guard against writing an empty image is
    /// stepped around. The user's .img is gone and the save reports success.
    ///
    /// Parsing eagerly here is affordable in a way it would not be for an archive:
    /// a loose .img is a single image, not a directory of tens of thousands, and
    /// this runs once per open. It does not set <c>Changed</c>, so an unedited
    /// image still saves through the copy-the-original-bytes branch exactly as
    /// before.
    /// </summary>
    private static WzImage? TryOpenLooseImg(string path, byte[] iv)
    {
        WzImgDeserializer deserializer = new(false);
        WzImage image = deserializer.WzImageFromIMGFile(path, iv, Path.GetFileName(path), out _);
        try
        {
            if (image.ParseImage())
                return image;
        }
        catch
        {
            // A wrong key reads structure out of noise, so the failure can be a
            // throw from anywhere in the property reader as easily as a false
            // return. Both mean the same thing here.
        }

        // Disposed rather than dropped. Each attempt opens a FileStream that only
        // WzImage.Dispose closes, and the walk makes up to three of them — a
        // leaked handle on a file the user is about to be told they can retry
        // with a different key is the one that then refuses to open.
        image.Dispose();
        return null;
    }

    /// <summary>
    /// The encryption the first archive of a client folder was detected with,
    /// keyed by that folder.
    ///
    /// A MapleStory folder is one client, built once, so its 29 archives share
    /// an encryption and a game version — but auto-detection re-derives that per
    /// archive, and it is the expensive half of an open: measured on a v232
    /// client, 554 ms and 538 ms per archive detecting, against 292 ms and
    /// 298 ms with the version supplied. Detecting once and reusing the answer
    /// for the folder takes ~250 ms off each of the other 28 files.
    ///
    /// It is an assumption, never a conclusion: <see cref="OpenWzFile"/> falls
    /// back to detecting from the file itself the moment the assumed version
    /// fails to parse, so a hand-assembled mixed folder still opens completely.
    /// Written under <see cref="Gate"/> like everything else here.
    /// </summary>
    private readonly Dictionary<string, (WzMapleVersion Version, short GameVersion)> _folderVersions
        = new(StringComparer.OrdinalIgnoreCase);

    private void RememberFolderVersion(string path, WzMapleVersion version, short gameVersion)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder != null && version != WzMapleVersion.UNKNOWN)
            _folderVersions[folder] = (version, gameVersion);
    }

    /// <summary>
    /// Works out which encryption to parse with.  Auto-detection is only run
    /// when the caller did not pin a version and no sibling archive in the same
    /// folder has already answered the question, because it parses the archive
    /// three times and is slow on large files.
    /// </summary>
    /// <returns>
    /// <c>Assumed</c> is true when the version was not measured against this
    /// file, so a parse failure means "try again properly" rather than "this
    /// archive is unreadable".
    /// </returns>
    private (WzMapleVersion Version, byte[]? Iv, short GameVersion, bool Assumed) ResolveEncryption(
        string path, OpenRequest request)
    {
        byte[]? iv = ParseIv(request.Iv);
        WzMapleVersion? requested = ParseVersion(request.MapleVersion);

        if (iv != null)
            return (WzMapleVersion.CUSTOM, iv, request.GameVersion, false);

        if (requested.HasValue && requested.Value != WzMapleVersion.UNKNOWN)
            return (requested.Value, null, request.GameVersion, true);

        string? folder = Path.GetDirectoryName(path);
        if (folder != null && _folderVersions.TryGetValue(folder, out (WzMapleVersion Version, short GameVersion) known))
        {
            return (known.Version, null,
                    request.GameVersion >= 0 ? request.GameVersion : known.GameVersion, true);
        }

        (WzMapleVersion detected, byte[]? detectedIv, short gameVersion) = DetectEncryption(path, request);
        return (detected, detectedIv, gameVersion, false);
    }

    private (WzMapleVersion Version, byte[]? Iv, short GameVersion) DetectEncryption(string path, OpenRequest request)
    {
        WzMapleVersion detected = WzTool.DetectMapleVersion(path, out short fileVersion);
        _log.LogInformation("Auto-detected {Version} v{FileVersion} for {File}",
            detected, fileVersion, Path.GetFileName(path));
        return (detected, null, request.GameVersion >= 0 ? request.GameVersion : fileVersion);
    }

    public static WzMapleVersion? ParseVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Enum.TryParse(name, true, out WzMapleVersion version) ? version : null;
    }

    public static byte[]? ParseIv(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        string cleaned = hex.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                            .Replace(" ", "").Replace("-", "").Replace(",", "");
        if (cleaned.Length != 8)
            throw new ArgumentException("A custom IV must be exactly 4 bytes (8 hex characters), e.g. 4D23C72B.");

        byte[] iv = new byte[4];
        for (int i = 0; i < 4; i++)
            iv[i] = byte.Parse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        return iv;
    }

    private string NextId() => "f" + _nextId++;

    public void Close(string fileId)
    {
        lock (Gate)
        {
            if (_files.Remove(fileId, out OpenFile? file))
            {
                // The invalidation is in a finally, and that is not tidiness.
                //
                // The entry is already out of _files by the time Dispose runs, so
                // a throw part way down the tear-down walk used to leave the
                // generation un-ticked with the resolution cache still holding
                // live WzObjects out of a half-disposed tree — and every
                // generation-keyed cache above it (rendered PNGs, the section
                // lists, the map asset sets) went on serving them. The close is
                // reported as a 500 while the session quietly keeps handing out
                // pieces of the archive it just tore down.
                try
                {
                    file.Dispose();
                }
                finally
                {
                    // Both for correctness — a later file must never resolve
                    // through this one's nodes — and so the cache doesn't pin a
                    // closed archive's tree in memory.
                    InvalidateResolution();
                }

                // Logged, because a close is the last thing many sessions ever
                // record. The report this came from was "the log ends at the
                // DELETE", and a line saying the close finished is what separates
                // "it died during the close" from "it was fine afterwards and
                // something else ended it" — which is what it turned out to be.
                _log.LogInformation("Closed {Name} ({Id})", file.Name, file.Id);
            }
        }
    }

    #endregion

    #region Resolution

    /// <summary>
    /// Roughly a large archive's worth of paths; past it the cache is flushed
    /// rather than grown, so a long session can't accumulate unboundedly.
    /// </summary>
    private const int MaxCachedResolutions = 25_000;

    /// <summary>Session path -> resolved node. Read and written under <see cref="Gate"/> only.</summary>
    private readonly Dictionary<string, WzObject> _resolutionCache = new(StringComparer.Ordinal);
    private int _generation;

    /// <summary>
    /// Increments on every <see cref="InvalidateResolution"/>.  Anything else
    /// caching something derived from the tree can key on this instead of
    /// inventing its own staleness test.
    /// </summary>
    public int Generation
    {
        get { lock (Gate) return _generation; }
    }

    /// <summary>
    /// Drops every memoised path resolution.
    ///
    /// A resolution is only as good as the tree it was taken from, and this
    /// service cannot see edits made through the MapleLib objects it hands out.
    /// So anything that can change which node a path names — add, rename,
    /// delete, move/transfer, reorder, undo/redo, save-and-reopen — must call
    /// this.  Over-calling costs a re-walk; under-calling costs a write landing
    /// on the wrong node, so when in doubt, call it.
    /// </summary>
    public void InvalidateResolution()
    {
        lock (Gate)
        {
            _generation++;
            _structureGeneration++;
            _valueTouched.Clear();
            _resolutionCache.Clear();
        }

        // Content digests die with the resolutions, and for the same reason: they
        // are memos about a tree that has just changed shape. WzContentHasher has
        // no change notification to hook -- nothing in MapleLib offers one -- so
        // its cache is only correct if every writer remembers to drop it, and
        // "every writer remembers" is not a property anyone can keep true. This
        // is the one funnel they all already go through.
        //
        // A stale digest is not a slow answer, it is a wrong "unchanged": it is
        // what would let an edited image compare equal to the copy it was edited
        // away from, in a report a user reads to decide which copy to delete.
        // Dropping the whole table costs a rehash of whatever is asked for next.
        WzContentHasher.ClearCache();
    }

    private int _structureGeneration;
    private int _valueGeneration;

    /// <summary>
    /// Full session paths of the properties changed by a value-only edit since
    /// the last structural change — <c>f1/0100100.img/info/maxHP</c>, not the
    /// image above it. Bounded; see <see cref="MaxValueTouched"/>.
    ///
    /// The full path and not the owning image, because working out the image
    /// from the text is a guess and a guess here is a stale grid. WZ directories
    /// can themselves be named <c>*.img</c> — a real client has them — so
    /// "the first .img segment" names the directory and "the last .img segment"
    /// names a property if a property happens to be called that. A consumer that
    /// holds rows keyed by image path finds its row by walking this path's own
    /// ancestors, which needs no rule about what an image is called.
    /// </summary>
    private readonly HashSet<string> _valueTouched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Past this many touched images, patching them one by one costs more than
    /// rebuilding, so the structural counter is ticked instead and every browse
    /// list starts fresh. A bulk edit across a whole archive lands here, which
    /// is the right answer for it.
    /// </summary>
    private const int MaxValueTouched = 256;

    /// <summary>
    /// Ticks only when the tree changes SHAPE — add, delete, rename, move,
    /// reorder, undo, redo, close, save-and-reopen.
    ///
    /// The distinction this draws is the single biggest thing standing between
    /// this app and feeling instant. Every browse list keyed itself on
    /// <see cref="Generation"/>, which also ticks when one number changes, so
    /// typing a new HP into one mob threw away the parsed summaries of all 2,742
    /// of them and the next visit to the Mobs section paid the full ten-second
    /// rebuild. Measured on a v232 client: mob list 0.02s before an edit,
    /// 5.5s immediately after one, for a change to a single field.
    ///
    /// A value edit mutates a property in place — <c>WzNodeFactory.SetValue</c>
    /// does not replace the node, so its identity, parent and name all survive.
    /// Nothing about which node a path names has changed, and nothing about any
    /// other row has changed. So a list keyed on this counter stays correct
    /// provided it re-reads the rows named by <see cref="ValueChanges"/>, which
    /// is what makes the pair safe to use and unsafe to use by halves.
    /// </summary>
    public int StructureGeneration
    {
        get { lock (Gate) return _structureGeneration; }
    }

    /// <summary>
    /// Ticks on every value-only edit. A cache holding this number and the
    /// structural one knows both "am I still about the right tree" and "have any
    /// of my rows been edited since I last looked".
    /// </summary>
    public int ValueGeneration
    {
        get { lock (Gate) return _valueGeneration; }
    }

    /// <summary>
    /// The two counters and the touched-image set, read together so a caller
    /// cannot get a torn view of them.
    ///
    /// Caller must hold <see cref="Gate"/> — this is the state a browse cache
    /// checks itself against, and reading it outside the lock would let an edit
    /// land between the counter and the set.
    /// </summary>
    public (int Structure, int Value, IReadOnlyCollection<string> Touched) ValueChanges()
    {
        return (_structureGeneration, _valueGeneration, _valueTouched.Count == 0
            ? Array.Empty<string>()
            : _valueTouched.ToArray());
    }

    /// <summary>
    /// Records that one property's value changed, without claiming the tree
    /// moved.
    ///
    /// Still ticks <see cref="Generation"/> and still clears the resolution
    /// cache: those are the conservative behaviours everything else in the app
    /// has always had, and this change is not the place to relax them. What it
    /// adds is the narrower statement — this image, and only this image, needs
    /// re-reading — which the browse lists use to patch instead of rebuild.
    ///
    /// An empty path names nothing, so it ticks the structural counter instead:
    /// an unrecognised edit costs a rebuild rather than leaving a list quietly
    /// showing the old number. Failing towards the slow answer is the only
    /// acceptable direction here.
    /// </summary>
    public void NoteValueChanged(string path)
    {
        lock (Gate)
        {
            _generation++;
            _resolutionCache.Clear();

            if (string.IsNullOrEmpty(path))
            {
                _structureGeneration++;
                _valueTouched.Clear();
                return;
            }

            _valueGeneration++;
            _valueTouched.Add(path);
            if (_valueTouched.Count > MaxValueTouched)
            {
                _structureGeneration++;
                _valueTouched.Clear();
            }
        }
    }

    /// <summary>
    /// The deepest ancestor of <paramref name="path"/> — itself included — that
    /// <paramref name="known"/> has an entry for, or -1.
    ///
    /// This is how a cache holding rows keyed by image path finds the row an
    /// edited property belongs to, without needing any rule about which segment
    /// is the image. Deepest first, so a row for a nested image wins over a row
    /// for the directory containing it.
    /// </summary>
    public static int OwnerOf(IReadOnlyDictionary<string, int> known, string path)
    {
        int end = path.Length;
        while (end > 0)
        {
            if (known.TryGetValue(path[..end], out int found))
                return found;

            int slash = path.LastIndexOf('/', end - 1);
            if (slash <= 0)
                return -1;
            end = slash;
        }
        return -1;
    }

    /// <summary>
    /// How much work a chunked build does between stand-downs; see
    /// <see cref="TryRunChunked"/>. It is both the ceiling on how long an
    /// interactive request waits behind a background build and the thing that
    /// bounds what the handoffs cost that build.
    /// </summary>
    private const int StandDownEveryMs = 12;

    /// <summary>
    /// Runs one pass of a long build as a sequence of short gate holds, so
    /// interactive requests interleave with it instead of queueing behind it.
    ///
    /// The problem this solves, measured on a v232 client: building the mob list
    /// held the gate for 7.5s, the skill list for 8.1s and the NPC list for 3.3s,
    /// and a concurrent <c>/api/files</c> probe during those saw a maximum of
    /// 7.5s, 8.1s and 3.3s respectively — i.e. the gate was held for the whole
    /// build, so every browse, render, thumbnail and edit waited for all of it.
    /// The build still costs the same; it is no longer a wall.
    ///
    /// What it must not do is return a list stitched together from two different
    /// trees, which is a worse failure than being slow. Between chunks anything
    /// can happen, so the tree's <see cref="Generation"/> is re-checked at the
    /// top of every chunk and the pass is abandoned wholesale the moment it
    /// moves — never patched up, never partially reused. Every mutation in
    /// <see cref="WzEditService"/> ticks that counter (through
    /// <c>MarkFileDirty</c> -> <see cref="InvalidateResolution"/>), so it
    /// catches value edits as well as structural ones.
    ///
    /// <paramref name="interleave"/> false runs the whole pass in one hold. That
    /// is the caller's escape hatch: a session being edited continuously could
    /// restart a chunked pass for ever, so after a few attempts it takes the
    /// gate once and finishes, which is exactly the behaviour that existed
    /// before this method.
    /// </summary>
    /// <returns>
    /// True when the pass ran start to finish against one unchanged tree; false
    /// when the generation moved and the caller must discard what it built.
    /// </returns>
    public bool TryRunChunked<TItem>(
        int generation,
        IReadOnlyList<TItem> work,
        Action<TItem> step,
        int chunkSize,
        bool interleave,
        CancellationToken cancel)
    {
        // Stand-downs are charged against the clock, not against the chunk count.
        // The chunk sizes are tuned per service for how expensive one item is,
        // and they vary by two orders of magnitude -- StringPoolService walks
        // two images at a time, ImageMemoryService five hundred -- so one
        // handoff per chunk is a rounding error for one caller and most of the
        // runtime for another. Releasing the gate stays per chunk; standing
        // aside afterwards happens at most once per StandDownEveryMs of work.
        Stopwatch clock = Stopwatch.StartNew();
        long standDownAt = 0;

        for (int i = 0; i < work.Count;)
        {
            cancel.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (_generation != generation)
                    return false;

                int end = interleave ? Math.Min(i + Math.Max(1, chunkSize), work.Count) : work.Count;
                for (; i < end; i++)
                    step(work[i]);
            }

            // Monitor is not a fair lock, and this loop releases the gate and
            // asks for it straight back with nothing in between -- so a build
            // thread can win the re-acquire over and over while a request thread
            // that has been waiting the whole time gets nothing.
            //
            // Thread.Yield() was here, and it is not enough. It offers the
            // current processor to a thread already ready to run *on that
            // processor*; on a machine with an idle core it returns false
            // immediately and the build thread sails back in. Measured on a v232
            // client during the warm-up, with the yield: one /api/children over
            // Npc.wz -- 7 ms of work once the gate is in hand -- took 4,003 ms,
            // 5,011 ms and 6,098 ms on three successive tries, and the same
            // request took 7 ms the moment the warm-up ended. The chunking was
            // real and the interleaving was not.
            //
            // Standing down for a scheduling quantum is what actually hands over:
            // a waiter that the release made runnable gets on a processor and
            // takes the gate before this thread asks again. It is charged at
            // most once per StandDownEveryMs of work rather than once per chunk,
            // because the chunk sizes are tuned for how expensive one item is and
            // not for how often it is polite to let go.
            if (interleave && i < work.Count && clock.ElapsedMilliseconds >= standDownAt)
            {
                Thread.Sleep(1);
                standDownAt = clock.ElapsedMilliseconds + StandDownEveryMs;
            }
        }

        // The last chunk's own check happened before its work, so the tree can
        // still have moved during it.
        lock (Gate)
            return _generation == generation;
    }

    public OpenFile GetFile(string fileId)
    {
        lock (Gate)
        {
            return _files.TryGetValue(fileId, out OpenFile? file)
                ? file
                : throw new KeyNotFoundException($"No open file with id '{fileId}'. It may have been closed.");
        }
    }

    public OpenFile GetFileForPath(string path) => GetFile(WzPath.FileId(path));

    /// <summary>
    /// The same lookup as <see cref="GetFile"/> for callers to whom "not open"
    /// is an ordinary answer rather than an error.
    ///
    /// <see cref="ArchiveFamilyService"/> is the caller this exists for: a merged
    /// family holds the ids of its member archives, and closing one of those goes
    /// through the normal file close, which knows nothing about families. A stale
    /// id is therefore the expected result of an ordinary action, and catching an
    /// exception per member on every listing would be using exceptions for the
    /// common path.
    /// </summary>
    public OpenFile? TryGetFile(string fileId)
    {
        lock (Gate)
            return _files.TryGetValue(fileId, out OpenFile? file) ? file : null;
    }

    /// <summary>
    /// Chooses the one representation a section editor should read for an
    /// archive role. A WZ family and an extracted IMG folder may both describe
    /// the same Mob, Npc, Skill, String, or Map data; combining them would show
    /// duplicate ids and, worse, make edits depend on open order.
    ///
    /// An explicit file id always scopes to that source. The aggregate view
    /// prefers a WZ/split family over an IMG-folder mount or separately opened
    /// loose images, and only combines siblings from one representation in one
    /// client directory. This is source de-duplication only: no node or id inside
    /// the chosen source is removed.
    /// </summary>
    public List<OpenFile> SelectRoleSources(string role, string? fileId = null)
    {
        lock (Gate)
        {
            List<OpenFile> candidates = _files.Values
                .Where(file => (RoleRoot(file, role) != null || IsLooseRoleFile(file, role))
                    && (fileId == null || file.Id == fileId))
                .ToList();

            if (fileId != null || candidates.Count <= 1)
                return candidates;

            OpenFile primary = candidates
                .OrderBy(SourceKindRank)
                .ThenBy(file => file.ReadOnly ? 1 : 0)
                .ThenBy(file => Path.GetFileNameWithoutExtension(file.Name)
                    .Equals(role, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => OpenOrder(file))
                .ThenBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                .First();

            if (primary.Kind == "img-folder")
                return new List<OpenFile> { primary };

            string? folder = Path.GetDirectoryName(primary.FilePath);
            if (primary.LooseImage != null)
            {
                return candidates
                    .Where(file => file.LooseImage != null
                        && string.Equals(Path.GetDirectoryName(file.FilePath), folder,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(OpenOrder)
                    .ToList();
            }

            return candidates
                .Where(file => file.Kind != "img-folder" && file.LooseImage == null
                    && string.Equals(Path.GetDirectoryName(file.FilePath), folder,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetFileNameWithoutExtension(file.Name)
                    .Equals(role, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(OpenOrder)
                .ToList();
        }
    }

    private static int SourceKindRank(OpenFile file) =>
        file.LooseImage != null ? 2 : file.Kind == "img-folder" ? 1 : 0;

    private static bool IsLooseRoleFile(OpenFile file, string role)
    {
        if (file.LooseImage == null)
            return false;

        DirectoryInfo? directory = Directory.GetParent(file.FilePath);
        for (int depth = 0; directory != null && depth < 4; depth++, directory = directory.Parent)
        {
            if (StripArchiveSuffix(directory.Name).Equals(role, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int OpenOrder(OpenFile file) =>
        file.Id.Length > 1 && int.TryParse(file.Id.AsSpan(1), out int order)
            ? order
            : int.MaxValue;

    /// <summary>
    /// The directory that represents one archive role inside a session entry.
    /// A mounted IMG folder may be either the role itself ("Mob") or a whole
    /// extracted client whose immediate children are roles ("Data/Mob"). WZ
    /// roots keep their existing shape and are matched by archive name only.
    /// </summary>
    public WzDirectory? RoleRoot(OpenFile file, string role)
    {
        WzDirectory? root = file.RootDirectory;
        if (root == null)
            return null;

        if (file.Name.StartsWith(role, StringComparison.OrdinalIgnoreCase))
            return root;

        if (file.Kind != "img-folder")
            return null;

        return root.GetDirectoryByName(role);
    }

    /// <summary>The session path of <see cref="RoleRoot"/>.</summary>
    public string RoleRootPath(OpenFile file, string role)
    {
        WzDirectory? roleRoot = RoleRoot(file, role);
        if (roleRoot == null)
            throw new InvalidOperationException($"'{file.Name}' does not contain {role} IMG data.");

        return ReferenceEquals(roleRoot, file.RootDirectory)
            ? file.Id
            : WzPath.Child(file.Id, roleRoot.Name);
    }

    /// <summary>
    /// Walks a session path to its MapleLib object, parsing images on the way.
    /// Throws with the failing segment named, which the UI surfaces verbatim.
    /// </summary>
    public WzObject Resolve(string path)
    {
        string[] segments = WzPath.SplitRaw(path);
        if (segments.Length == 0)
            throw new ArgumentException("Path is empty.");

        lock (Gate)
        {
            OpenFile file = GetFile(segments[0]);
            WzObject current = file.Root;

            // The walk always restarts from the live file root, so a cached
            // sub-path can never outlive the archive it came from: after a
            // save-and-reopen the root is a different object and every entry
            // below it fails its parent check.
            string prefix = segments[0];

            for (int i = 1; i < segments.Length; i++)
            {
                (string name, int occurrence) = WzPath.ParseSegment(segments[i]);
                prefix = prefix + "/" + segments[i];
                WzObject? next = ResolveCachedChild(prefix, current, name, occurrence);
                if (next == null)
                {
                    string sofar = string.Join("/", segments.Take(i));

                    // A path that runs into a link stopped for a reason the
                    // generic message would hide, and the user has somewhere to
                    // go: the node they asked for is under whatever the link
                    // names, not under the link.  Saying which link and where it
                    // points is the difference between a refusal and a dead end.
                    if (current is WzUOLProperty link)
                    {
                        throw new KeyNotFoundException(
                            $"'{sofar}' is a link to '{link.Value}', and a link has no children of its own. " +
                            $"Look for '{name}' under what the link points at — addressing it through the link " +
                            "would name one node and reach another.");
                    }

                    throw new KeyNotFoundException($"'{name}' not found under '{sofar}'.");
                }
                current = next;
            }
            return current;
        }
    }

    public WzObject? TryResolve(string path)
    {
        try { return Resolve(path); }
        catch { return null; }
    }

    /// <summary>
    /// Memoised <see cref="ResolveChild"/>, keyed on the session path of the
    /// child.  Without it every API call re-walked the archive from its root,
    /// which on a Commodity.img with 15,000 entries cost a 15,000-comparison
    /// scan per path segment, per request.
    ///
    /// A stale entry would silently point an edit at the wrong node, which is a
    /// far worse failure than the scan it replaces, so the cache never trusts
    /// itself: on doubt it misses rather than serves.
    /// </summary>
    private WzObject? ResolveCachedChild(string path, WzObject parent, string name, int occurrence)
    {
        WzObject? container = ContainerOf(parent);

        if (_resolutionCache.TryGetValue(path, out WzObject? cached))
        {
            // A hit is only honoured while the node is still attached to the
            // parent this walk just resolved and still carries this segment's
            // name.  A delete, a move, a rename, an unparse and a reopen each
            // break one of those on the node itself.  What they cannot catch is
            // another sibling coming to shadow this name, which is why the edit
            // layer must also call InvalidateResolution().
            if (ReferenceEquals(cached.Parent, container)
                && string.Equals(cached.Name, name, StringComparison.OrdinalIgnoreCase))
                return cached;

            _resolutionCache.Remove(path);
        }

        WzObject? resolved = ResolveChild(parent, name, occurrence);

        // Only unsuffixed segments are cacheable: "#n" is positional, and the
        // position of a duplicate name shifts whenever any sibling before it is
        // inserted or removed.  Misses are never cached either, so a path that
        // becomes resolvable later — a fresh add, an image that parses on the
        // way past — is picked up without anyone having to invalidate anything.
        if (resolved != null && occurrence == 0 && ReferenceEquals(resolved.Parent, container))
        {
            // Flushing wholesale on overflow only costs a re-walk; an LRU here
            // would buy nothing but a way to get the eviction wrong.
            if (_resolutionCache.Count >= MaxCachedResolutions)
                _resolutionCache.Clear();
            _resolutionCache[path] = resolved;
        }
        return resolved;
    }

    /// <summary>
    /// The object children actually hang off.  A <see cref="WzFile"/> is a shell
    /// around its root directory, and that directory is what its children name
    /// as their parent.
    /// </summary>
    private static WzObject? ContainerOf(WzObject parent)
        => parent is WzFile file ? file.WzDirectory : parent;

    private WzObject? ResolveChild(WzObject parent, string name, int occurrence)
    {
        // MapleLib keeps a case-insensitive name index on both containers, so
        // the first node with a given name is a dictionary probe rather than a
        // walk.  Every internal call site asks for occurrence 0.
        if (occurrence == 0)
            return LookupByName(parent, name);

        // Duplicates are addressed positionally and the index only knows the
        // first of each name, so those still need the ordered walk.
        int seen = 0;
        foreach (WzObject child in EnumerateChildren(parent))
        {
            if (!string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen == occurrence)
                return child;
            seen++;
        }
        return null;
    }

    /// <summary>
    /// First child with this name, in the same order
    /// <see cref="EnumerateChildren"/> yields them.
    /// </summary>
    private static WzObject? LookupByName(WzObject parent, string name)
    {
        switch (parent)
        {
            case WzFile file:
                return file.WzDirectory == null ? null : LookupByName(file.WzDirectory, name);

            case WzDirectory dir:
                // Directories before images, as enumeration has it.
                return (WzObject?)dir.GetDirectoryByName(name) ?? dir.GetImageByName(name);

            case WzImage image:
                EnsureParsed(image);
                return image.WzProperties?.FindByName(name);

            // A link has no children of its own.  See EnumerateChildren; this is
            // the same rule reached through the fast path, and it has to agree
            // with it or a path resolves differently depending on whether it
            // carries an occurrence suffix.
            case WzUOLProperty:
                return null;

            case WzImageProperty property:
                return property.WzProperties?.FindByName(name);

            default:
                return null;
        }
    }

    /// <summary>
    /// Children in tree order.  Images are parsed on demand here, which is what
    /// keeps opening a 200 MB archive instant.
    ///
    /// A link is a leaf.  <c>WzUOLProperty.WzProperties</c> returns the children
    /// of the node the link RESOLVES to (WzUOLProperty.cs, <c>UOLRES</c>), so
    /// without the arm below this accessor folded another node's subtree in under
    /// the link's own path — and this accessor is how the tree lists children, how
    /// <see cref="ResolveChild"/> addresses duplicates, and therefore how every
    /// mutation in the editor names what it is about to change.  The measured
    /// consequence was a delete: <c>frames/link/sub/pixels</c> resolved to
    /// <c>victim/sub/pixels</c>, the delete removed the second, and it was
    /// reported as one node removed at the first path.  A node the user was not
    /// looking at, gone, with an undo entry naming somewhere else.
    ///
    /// This is <see cref="WzWalk"/>'s rule 1 — "a walk does not descend into a UOL
    /// at all" — applied to the shared accessor the recursive walks were only ever
    /// one caller of.  The link itself is still a node: it resolves, it reports its
    /// type, and <c>ToDto</c> still carries its target text, which is what a
    /// "go to target" affordance needs.  What is gone is the pretence that the
    /// target's children live at the link's address.
    /// </summary>
    public IEnumerable<WzObject> EnumerateChildren(WzObject node)
    {
        switch (node)
        {
            case WzFile file:
                foreach (WzObject child in EnumerateChildren(file.WzDirectory))
                    yield return child;
                break;

            case WzDirectory dir:
                foreach (WzDirectory sub in dir.WzDirectories)
                    yield return sub;
                foreach (WzImage image in dir.WzImages)
                    yield return image;
                break;

            case WzImage image:
                EnsureParsed(image);
                foreach (WzImageProperty prop in image.WzProperties)
                    yield return prop;
                break;

            case WzUOLProperty:
                break;

            case WzImageProperty property:
                WzPropertyCollection? props = property.WzProperties;
                if (props != null)
                {
                    foreach (WzImageProperty prop in props)
                        yield return prop;
                }
                break;
        }
    }

    public static void EnsureParsed(WzImage image)
    {
        if (!image.Parsed && !image.Changed)
            image.ParseImage();
    }

    /// <summary>
    /// Turns a lightweight IMG-folder entry into the one parsed image it names.
    /// Ordinary WZ images pass through unchanged. Virtual folders intentionally
    /// expose filename-only entries from <c>WzImages</c> so listing ten thousand
    /// files cannot parse ten thousand files; domain editors call this only for
    /// the row they are actively processing.
    /// </summary>
    public WzImage MaterializeImage(WzImage image)
    {
        if (image.Parent is VirtualWzDirectory directory && !image.Parsed)
        {
            WzImage? loaded = directory.GetImageByName(image.Name);
            if (loaded == null)
            {
                throw new InvalidDataException(
                    $"The IMG file '{Path.Combine(directory.FilesystemPath, image.Name)}' could not be read.");
            }
            image = loaded;
        }

        EnsureParsed(image);
        return image;
    }

    /// <summary>
    /// Backs <see cref="WzCanvasProperty.ExternalImageResolver"/>: given an
    /// outlink image path like "Mob/8800141.img", finds that image in any open
    /// archive.
    /// </summary>
    private WzImage? ResolveExternalImage(string imagePath)
    {
        string[] parts = imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        List<OpenFile> candidates;
        lock (Gate)
            candidates = _files.Values.ToList();

        foreach (OpenFile file in candidates)
        {
            if (file.RootDirectory == null)
                continue;

            // Outlinks are written relative to the archive family ("Mob/..."),
            // which may live in Mob.wz, Mob001.wz, Mob2.wz and so on.
            WzObject current = file.RootDirectory;
            int start = string.Equals(StripArchiveSuffix(file.Name), parts[0], StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

            bool ok = true;
            for (int i = start; i < parts.Length; i++)
            {
                WzObject? next = ResolveChild(current, parts[i], 0);
                if (next == null) { ok = false; break; }
                current = next;
            }
            if (ok && current is WzImage image)
                return image;
        }
        return null;
    }

    /// <summary>"Mob001.wz" -> "Mob".</summary>
    public static string StripArchiveSuffix(string archiveName)
    {
        string name = archiveName;
        if (name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1]))
            end--;
        return name[..end];
    }

    #endregion

    #region Projection

    public NodeDto ToDto(WzObject node, string path)
    {
        NodeDto dto = new()
        {
            Path = path,
            Name = node.Name ?? "",
        };

        switch (node)
        {
            case WzDirectory dir:
                dto.Kind = NodeKind.Directory;
                dto.ChildCount = dir.WzDirectories.Count + dir.WzImages.Count;
                dto.HasChildren = dto.ChildCount > 0;
                break;

            case WzImage image:
                dto.Kind = NodeKind.Image;
                dto.Parsed = image.Parsed;
                dto.Dirty = image.Changed;
                if (image.Parsed)
                {
                    dto.ChildCount = image.WzProperties.Count;
                    dto.HasChildren = dto.ChildCount > 0;
                }
                else
                {
                    // Unknown until parsed; claim expandable so the UI shows a chevron.
                    dto.ChildCount = -1;
                    dto.HasChildren = true;
                }
                break;

            case WzImageProperty property:
                dto.Kind = NodeKind.Property;
                dto.Type = property.PropertyType.ToString();
                dto.Dirty = property.ParentImage?.Changed ?? false;
                FillPropertyValue(dto, property);
                // A link reports no children, for the reason EnumerateChildren
                // gives.  This field is what draws the chevron, so it is the
                // invitation to traverse as much as the traversal itself: reading
                // property.WzProperties here would resolve the link and count the
                // target's children as the link's.
                WzPropertyCollection? children = property is WzUOLProperty ? null : property.WzProperties;
                dto.ChildCount = children?.Count ?? 0;
                dto.HasChildren = dto.ChildCount > 0;
                break;

            default:
                dto.Kind = NodeKind.File;
                break;
        }
        return dto;
    }

    private static void FillPropertyValue(NodeDto dto, WzImageProperty property)
    {
        switch (property)
        {
            case WzIntProperty p:
                dto.Value = p.Value.ToString(CultureInfo.InvariantCulture);
                dto.Editable = true;
                break;
            case WzShortProperty p:
                dto.Value = p.Value.ToString(CultureInfo.InvariantCulture);
                dto.Editable = true;
                break;
            case WzLongProperty p:
                dto.Value = p.Value.ToString(CultureInfo.InvariantCulture);
                dto.Editable = true;
                break;
            case WzFloatProperty p:
                dto.Value = p.Value.ToString("R", CultureInfo.InvariantCulture);
                dto.Editable = true;
                break;
            case WzDoubleProperty p:
                dto.Value = p.Value.ToString("R", CultureInfo.InvariantCulture);
                dto.Editable = true;
                break;
            case WzStringProperty p:
                dto.Value = p.Value;
                dto.Editable = true;
                break;
            case WzUOLProperty p:
                dto.Value = p.Value;
                dto.Editable = true;
                dto.Extra = new Dictionary<string, object?> { ["link"] = p.Value };
                break;
            case WzVectorProperty p:
                dto.Value = $"{p.X?.Value ?? 0}, {p.Y?.Value ?? 0}";
                dto.Editable = true;
                dto.Extra = new Dictionary<string, object?>
                {
                    ["x"] = p.X?.Value ?? 0,
                    ["y"] = p.Y?.Value ?? 0,
                };
                break;
            case WzCanvasProperty p:
                dto.Extra = new Dictionary<string, object?>
                {
                    ["width"] = p.PngProperty?.Width ?? 0,
                    ["height"] = p.PngProperty?.Height ?? 0,
                    ["format"] = p.PngProperty?.Format.ToString(),
                    ["inlink"] = (p[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value,
                    ["outlink"] = (p[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value,
                };
                dto.Value = $"{p.PngProperty?.Width ?? 0} x {p.PngProperty?.Height ?? 0}";
                break;
            case WzBinaryProperty p:
                dto.Extra = new Dictionary<string, object?>
                {
                    ["lengthMs"] = p.Length,
                    ["soundType"] = p.SoundType.ToString(),
                    ["extension"] = p.FileExtension,
                };
                dto.Value = $"{p.Length} ms {p.SoundType}";
                break;
            case WzLuaProperty p:
                // Readable but not writable: the value is an encrypted blob and
                // WzNodeFactory has no case to re-encrypt edited text, so
                // claiming it is editable would just produce a failing save.
                dto.Value = p.ToString();
                dto.Editable = false;
                break;
            case WzNullProperty:
                dto.Value = null;
                break;
        }
    }

    /// <summary>
    /// Lists a node's children as DTOs, tagging duplicate names with the
    /// <c>#n</c> suffix so every returned path is unambiguous.
    /// </summary>
    public List<NodeDto> GetChildren(string path)
    {
        lock (Gate)
        {
            WzObject node = Resolve(path);
            List<NodeDto> result = new();
            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (WzObject child in EnumerateChildren(node))
            {
                string name = child.Name ?? "";
                seen.TryGetValue(name, out int occurrence);
                seen[name] = occurrence + 1;
                result.Add(ToDto(child, WzPath.Child(path, name, occurrence)));
            }
            return result;
        }
    }

    public NodeDto GetNode(string path)
    {
        lock (Gate)
            return ToDto(Resolve(path), path);
    }

    #endregion

    public void Dispose()
    {
        lock (Gate)
        {
            foreach (OpenFile file in _files.Values)
                file.Dispose();
            _files.Clear();
            _resolutionCache.Clear();
        }
    }
}
