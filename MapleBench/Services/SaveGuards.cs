using System.Diagnostics;

namespace MapleBench.Services;

/// <summary>
/// Checks that run before a save touches the destination file.
///
/// These exist because of three failure modes documented repeatedly by users of
/// existing WZ tools:
///
///  1. Overwriting an archive while the game client holds it open could delete
///     the file outright, losing both the original and the edits.
///  2. A "file is in use" error mid-save crashed the tool with every change lost.
///  3. Edits appeared to save but silently never reached the file.
///
/// The answer to all three is the same: refuse to start a save we can't finish,
/// and verify the result rather than trusting it.
/// </summary>
public static class SaveGuards
{
    /// <summary>Process names that hold .wz files open.</summary>
    private static readonly string[] ClientProcessNames =
    {
        "MapleStory", "MapleStoryT", "MapleStory2", "maplestory",
        "HaRepacker", "HaCreator",
    };

    public sealed class GuardResult
    {
        public bool CanProceed { get; init; }
        public string? Blocker { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Pre-flight checks that are safe to run while we still hold the archive
    /// open ourselves: destination folder, free space, and whether a client is
    /// running.  The exclusive-access probe is deliberately NOT here — see
    /// <see cref="CheckWritable"/>.
    /// </summary>
    /// <param name="sourcePath">
    /// The archive being saved. Needed because "Save a copy" targets a path that
    /// does not exist yet, and sizing the space check off a non-existent file
    /// meant Save As got no space check at all.
    /// </param>
    /// <param name="everyImageRewritten">
    /// True when this save re-serialises the whole archive rather than streaming
    /// the untouched images through — see <c>WzSaveService.WhyEveryImageIsRewritten</c>.
    /// It changes the scratch requirement by a factor of four; see below.
    /// </param>
    public static GuardResult Check(
        string destination, bool destinationExists, string? sourcePath = null, bool everyImageRewritten = false)
    {
        List<string> warnings = new();

        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
            return new GuardResult { CanProceed = false, Blocker = "The destination has no folder." };

        if (!Directory.Exists(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex)
            {
                return new GuardResult
                {
                    CanProceed = false,
                    Blocker = $"Cannot create the folder '{directory}': {ex.Message}",
                };
            }
        }

        // Writing the archive needs roughly its own size again for the temp copy --
        // and again for MapleLib's own scratch file, which does NOT land next to the
        // destination. WzFile.SaveToDisk writes "<name>.TEMP" as a *relative* path,
        // so it resolves against Environment.CurrentDirectory, which Program.cs pins
        // to %TEMP%\MapleBench\<pid>. A client on D: with a full C: therefore failed
        // mid-write, after UnparseImage had already destroyed the in-memory tree,
        // which is exactly the outcome this guard exists to prevent.
        long? archiveBytes = SourceSize(destination, destinationExists, sourcePath);
        if (archiveBytes is > 0)
        {
            // What each of the two files this save creates costs, and where it
            // lands. Held as a list rather than checked one at a time because the
            // two are very often on the SAME volume -- the ordinary case is a
            // client on C: and a scratch folder in %TEMP% on C: -- and checking
            // them independently passed a save that needed the sum of both and
            // had room for neither.
            List<(string Path, long Bytes, string Role)> wanted = new()
            {
                // The destination needs room for the whole archive twice: the temp
                // we write and the original still sitting there until the swap.
                (Path.GetFullPath(destination), archiveBytes.Value * 2, "the destination"),
            };

            // How much of the archive actually passes through MapleLib's .TEMP,
            // and it is not one number.
            //
            //   * An ordinary save streams every unchanged image straight from the
            //     source file's reader and routes only *changed* ones through the
            //     scratch file (WzDirectory.SaveImages). Demanding a whole
            //     archive's worth here once blocked a one-image edit to a 2 GB
            //     Map.wz on a machine with a small system drive -- refusing a save
            //     that needed ~50 KB. A quarter, as a warning, because the true
            //     figure is not known until the write is under way.
            //
            //   * A save that re-serialises every image writes the ENTIRE archive
            //     there first. Measured on this machine: a 1,568 MB Character.wz
            //     re-encrypted to a different key put 1,567 MB in
            //     %TEMP%\MapleBench\<pid> while the 1,568 MB output was being
            //     assembled beside the archive. The old quarter-sized warning was
            //     therefore 4x short of the truth for exactly the save that needs
            //     the most, and it did not block -- so the shortfall landed
            //     mid-write, after GenerateDataFile had already unparsed the tree.
            //     That case is knowable up front, so it blocks.
            long scratchBytes = everyImageRewritten ? archiveBytes.Value : archiveBytes.Value / 4;
            wanted.Add((Path.GetFullPath(Environment.CurrentDirectory), scratchBytes, "the scratch folder"));

            foreach ((string root, long needed, string roles) in GroupByVolume(wanted))
            {
                string? shortfall = CheckVolume(root, needed, roles);
                if (shortfall == null)
                    continue;

                // Only the guess blocks nothing. When the whole archive is going
                // through the scratch folder the number is not a guess, and the
                // destination requirement never was one.
                bool advisoryOnly = !everyImageRewritten && roles == "the scratch folder";
                if (advisoryOnly)
                    warnings.Add(shortfall + " Saving many changed images at once may fail.");
                else
                    return new GuardResult { CanProceed = false, Blocker = shortfall };
            }
        }

        string? client = FindRunningClient();
        if (client != null)
        {
            warnings.Add(
                $"'{client}' is running. It may hold WZ files open, and files it has loaded will not " +
                "pick up your changes until it restarts.");
        }

        return new GuardResult { CanProceed = true, Warnings = warnings };
    }

    /// <summary>
    /// The processes holding a file open, named, via the Restart Manager.
    ///
    /// This is the API Windows itself uses for "the file is in use by..." in
    /// Explorer, and it is the only way to answer the question without a driver
    /// or an undocumented handle walk. It is read-only: a session is opened,
    /// asked what would have to be restarted to free the file, and closed.
    /// Nothing is restarted and nothing is written.
    ///
    /// Returns an empty string on any failure -- the caller then falls back to
    /// generic advice, which is no worse than what it said before.
    /// </summary>
    private static string DescribeHolders(string path)
    {
        try
        {
            int result = RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N"));
            if (result != 0)
                return string.Empty;

            try
            {
                string[] files = { path };
                if (RmRegisterResources(session, 1, files, 0, null, 0, null) != 0)
                    return string.Empty;

                uint needed = 0;
                uint count = 0;
                uint reason = 0;
                // First call sizes the array; a zero count means nothing holds it.
                int probe = RmGetList(session, out needed, ref count, null, ref reason);
                if (needed == 0)
                    return string.Empty;

                RM_PROCESS_INFO[] info = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, info, ref reason) != 0)
                    return string.Empty;

                List<string> names = new();
                for (int i = 0; i < count; i++)
                {
                    string name = info[i].strAppName;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    int pid = (int)info[i].Process.dwProcessId;

                    // Our own process is never the answer: SaveArchive disposes the
                    // archive's reader before this runs, so if we still appear here
                    // it is a different copy, and saying "MapleBench" without the pid
                    // would read as "close the window you are using".
                    string entry = pid == Environment.ProcessId
                        ? $"{name} (this window)"
                        : $"{name} (pid {pid})";
                    if (!names.Contains(entry))
                        names.Add(entry);
                }

                return names.Count switch
                {
                    0 => string.Empty,
                    1 => names[0],
                    _ => string.Join(" and ", names),
                };
            }
            finally
            {
                RmEndSession(session);
            }
        }
        catch
        {
            // Restart Manager is unavailable on some stripped installs, and a
            // diagnostic must never be the reason a save fails differently.
            return string.Empty;
        }
    }

    /// <summary>
    /// How large the archive about to be written is, best-effort. Prefers the
    /// destination when overwriting, and falls back to the archive being saved so
    /// that "Save a copy" to a new path is still checked.
    /// </summary>
    private static long? SourceSize(string destination, bool destinationExists, string? sourcePath)
    {
        try
        {
            if (destinationExists)
                return new FileInfo(destination).Length;
            if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                return new FileInfo(sourcePath).Length;
        }
        catch { /* advisory only */ }
        return null;
    }

    /// <summary>
    /// Adds up what one save wants per volume, so a destination and a scratch
    /// folder that happen to share a drive are asked for the sum rather than
    /// twice for half of it.
    ///
    /// Anything whose root cannot be worked out is dropped rather than guessed
    /// at: a UNC path or a path on a device with no drive letter has no
    /// <see cref="DriveInfo"/> to ask, and the space check has always been
    /// advisory in that case.
    /// </summary>
    private static List<(string Root, long Bytes, string Roles)> GroupByVolume(
        IEnumerable<(string Path, long Bytes, string Role)> wanted)
    {
        Dictionary<string, (long Bytes, List<string> Roles)> byRoot = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = new();

        foreach ((string path, long bytes, string role) in wanted)
        {
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { continue; }
            if (string.IsNullOrEmpty(root))
                continue;

            if (!byRoot.TryGetValue(root, out (long Bytes, List<string> Roles) entry))
            {
                entry = (0, new List<string>());
                order.Add(root);
            }
            entry.Bytes += bytes;
            if (!entry.Roles.Contains(role))
                entry.Roles.Add(role);
            byRoot[root] = entry;
        }

        return order
            .Select(root => (root, byRoot[root].Bytes, string.Join(" and ", byRoot[root].Roles)))
            .ToList();
    }

    private static string? CheckVolume(string path, long needed, string role)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;

            DriveInfo drive = new(root);
            if (drive.IsReady && drive.AvailableFreeSpace < needed)
            {
                return
                    $"Not enough free space on {drive.Name} — that is {role}. " +
                    $"Saving needs about {needed / 1024 / 1024} MB there and " +
                    $"{drive.AvailableFreeSpace / 1024 / 1024} MB is available.";
            }
        }
        catch { /* space check is advisory only */ }
        return null;
    }

    #region The memory ceiling

    /// <summary>One open archive, as the memory guard needs to describe it.</summary>
    /// <param name="Name">"Character.wz".</param>
    /// <param name="Images">How many images it holds — what its save-time cost scales with.</param>
    /// <param name="Bytes">Its size on disk, for the message only.</param>
    public readonly record struct OpenArchive(string Name, int Images, long Bytes);

    /// <summary>
    /// What a save needs before it has looked at a single image: the writer, the
    /// verification probe's reader and buffers, and enough slack for the GC not
    /// to be collecting on every allocation.
    ///
    /// Measured on this machine, Release, against the Steam depot's Character.wz
    /// (1,568 MB, 45,399 images) with the managed heap capped at 402 MB so the
    /// GC could not simply help itself: the process sat at 180 MB with ten
    /// archives open and peaked at 758 MB during a save that re-serialised all
    /// 45,399 images. Nearly all of that 578 MB delta is per-image (below); this
    /// is the part that does not scale.
    /// </summary>
    private const long SaveBaseBytes = 192L * 1024 * 1024;

    /// <summary>
    /// Per-image cost of an ordinary save, which streams the untouched images
    /// through byte for byte.
    ///
    /// What scales here is not the file size, it is the image count: the pre-save
    /// inventory holds one entry and one path string per image, and
    /// <c>VerifyCandidate</c> parses the written file's whole directory table
    /// again to compare against it. Measured: three concurrent saves of
    /// Character.wz (45,399 images), Item.wz and String.wz took the process from
    /// 240 MB to 356 MB — 2.6 KB an image. Rounded up, because the figure is a
    /// floor and refusing a save that would have just fit is a far better outcome
    /// than dying in the middle of one.
    /// </summary>
    private const long PerImageStreamingBytes = 4 * 1024;

    /// <summary>
    /// Per-image cost when every image is re-serialised instead of copied.
    ///
    /// On top of the inventory and the probe, each image is parsed, its canvases
    /// are forced to produce compressed bytes, it is serialised into a buffer and
    /// then released. Measured on the same archive: 180 MB -> 758 MB across
    /// 45,399 images, i.e. 13 KB an image, and this is the shape of save that
    /// most wants the headroom.
    /// </summary>
    private const long PerImageRewriteBytes = 16 * 1024;

    /// <summary>
    /// Headroom left over and above what the save is predicted to want.
    ///
    /// Running the machine to exactly its commit limit is not a clean failure, it
    /// is where the stall lives: Windows answers a commit request it cannot meet
    /// by growing the pagefile, which is a synchronous disk operation that blocks
    /// the allocating thread — measured on this machine during a deliberate
    /// overload, the pagefile went from 10,527 MB to 18,803 MB and machine-wide
    /// free commit dipped to 299 MB. A save that begins in that state is a save
    /// that stalls, and if the growth cannot be satisfied the allocation failure
    /// lands inside the GC, where it is not an exception the pipeline can catch.
    /// </summary>
    private const long SafetyMarginBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Refuses a save that the machine cannot give the memory for, before
    /// anything has been read, allocated or written.
    ///
    /// The free-space guard above has always asked whether the bytes will fit on
    /// the disk. Nothing asked whether they will fit in the process, and the
    /// answer depends entirely on what is already open — the headline workflow is
    /// twenty to forty archives at once, two of them 1.6 GB, plus the in-memory
    /// clones a port leaves behind.
    ///
    /// The number this compares against is <b>available commit</b>, not free
    /// physical memory, because commit is the actual allocation ceiling on
    /// Windows: paging costs speed, exceeding the commit limit costs the
    /// allocation. And an allocation that fails inside the GC is not an
    /// <c>OutOfMemoryException</c> some <c>catch</c> here can turn into a clean
    /// refusal — the runtime tears the process down where it stands. During a
    /// write is the worst moment in this application for that to happen, so the
    /// check is deliberately made where nothing has been touched yet.
    ///
    /// Available commit already nets out everything this process is holding, so
    /// "what is already open" needs no modelling: it has been subtracted
    /// before we look. <paramref name="otherOpenArchives"/> is only used to tell
    /// the user which ones are worth closing.
    ///
    /// Returns null when the save may proceed.
    /// </summary>
    public static string? CheckMemory(
        string archiveName,
        int imageCount,
        bool everyImageRewritten,
        IEnumerable<OpenArchive> otherOpenArchives)
    {
        long available = AvailableCommitBytes();
        if (available <= 0)
            return null;   // could not ask; never fail a save over a diagnostic

        long perImage = everyImageRewritten ? PerImageRewriteBytes : PerImageStreamingBytes;
        long needed = SaveBaseBytes + (long)Math.Max(0, imageCount) * perImage;
        if (available >= needed + SafetyMarginBytes)
            return null;

        // Biggest first, and by image count rather than by file size, because
        // that is what the resident cost actually tracks -- a 1.3 GB Sound.wz
        // holds far fewer images than a 1.6 GB Character.wz and costs far less to
        // hold open.
        List<OpenArchive> closeable = otherOpenArchives
            .Where(a => a.Images > 0)
            .OrderByDescending(a => a.Images)
            .Take(4)
            .ToList();

        string advice = closeable.Count > 0
            ? "\n\nClose one of these first — biggest first:\n" +
              string.Join("\n", closeable.Select(a =>
                  $"  - {a.Name} ({a.Images:N0} images, {a.Bytes / 1024 / 1024:N0} MB)"))
            : "\n\nClose other programs, or restart MapleBench and open only the archives you need.";

        return
            $"Saving '{archiveName}' needs about {needed / 1024 / 1024:N0} MB of memory and Windows has " +
            $"{available / 1024 / 1024:N0} MB left to give, so nothing was written and the file on disk is " +
            "untouched." + advice;
    }

    /// <summary>
    /// How many more bytes this process could commit right now, or 0 when the
    /// question cannot be answered.
    ///
    /// <c>ullAvailPageFile</c> is "commit limit minus committed", which is the
    /// number that decides whether an allocation succeeds. There is no managed
    /// equivalent: <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> reports
    /// physical memory or a container limit, neither of which is the ceiling a
    /// desktop Windows process actually hits.
    ///
    /// It reflects the pagefile as it is now. A system-managed pagefile can grow,
    /// so this can read low on a machine that would in fact have coped — and that
    /// is the conservative direction on purpose, because the growth is the stall
    /// this guard exists to keep a save out of.
    /// </summary>
    public static long AvailableCommitBytes()
    {
        try
        {
            MEMORYSTATUSEX status = new() { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPageFile : 0;
        }
        catch
        {
            return 0;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion

    /// <summary>
    /// Confirms nothing else holds the destination open, immediately before it
    /// is moved aside.
    ///
    /// This must be called only AFTER our own archive handle has been released:
    /// MapleLib keeps the source file open to stream unmodified images during a
    /// save, so probing earlier would always trip over ourselves.
    ///
    /// Returns null when the file can be replaced, or a message explaining who
    /// is holding it.
    /// </summary>
    public static string? CheckWritable(string destination)
    {
        if (!File.Exists(destination))
            return null;

        if (TryLockForWrite(destination, out string? error))
            return null;

        // Name the process actually holding the file, rather than guessing.
        //
        // This used to say "open in another program" and, if a process called
        // MapleStory happened to be running, blame that. Both can be wrong, and
        // the most confusing case is the one it could never name: a *second copy
        // of MapleBench* -- a stale instance, or a window left open on another
        // desktop -- holding the archive. The user is then told to close a
        // program they cannot see, about a file they can see is open in the
        // editor in front of them.
        //
        // Restart Manager answers the real question: which processes have this
        // path open right now.
        string holders = DescribeHolders(destination);
        string advice = holders.Length > 0
            ? $"\nIt is held by {holders}."
            : "\nClose any program using it and try again.";

        // A second editor is a different problem from the game being open, and
        // the difference decides what the user should do about it.
        if (holders.Contains("MapleBench", StringComparison.OrdinalIgnoreCase))
        {
            advice +=
                "\n\nThat is another copy of MapleBench, not the game. Close it and try again — " +
                "this one released its own handle before checking, so the archive is not being " +
                "held by the window you are looking at.";
        }
        else if (FindRunningClient() is string client)
        {
            advice += $"\nClose {client} and try again.";
        }

        return
            $"'{Path.GetFileName(destination)}' is open in another program, so it was left untouched." +
            advice +
            $"\n\nDetail: {error}";
    }

    /// <summary>
    /// Opens the file with no sharing to confirm nothing else holds it.  The
    /// handle is released immediately; this is a probe, not a reservation, so a
    /// race is still possible — but it converts the common case from "file
    /// destroyed" into "save refused".
    /// </summary>
    private static bool TryLockForWrite(string path, out string? error)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #region Restart Manager interop

    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, object[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    #endregion

    private static string? FindRunningClient()
    {
        try
        {
            foreach (string name in ClientProcessNames)
            {
                Process[] found = Process.GetProcessesByName(name);
                try
                {
                    if (found.Length > 0)
                        return found[0].ProcessName;
                }
                finally
                {
                    foreach (Process process in found)
                        process.Dispose();
                }
            }
        }
        catch { /* enumeration can fail under restricted rights; not fatal */ }
        return null;
    }
}
