using System.Runtime.InteropServices;

namespace MapleBench.Services;

/// <summary>
/// One answer to "how much room is left", used by every guard that refuses or
/// releases when memory runs short.
///
/// It exists because the obvious answer is wrong twice, and both were shipped:
///
///   * <c>GC.GetGCMemoryInfo()</c> describes the *last garbage collection*, so a
///     freshly started process — before any collection has happened — reports
///     zeros. A guard written on it alone silently disables itself for the whole
///     early part of a session, which is exactly when a user opens their
///     archives. Measured: a launch answered "this platform does not report a
///     memory limit" at rest and would have let every open through.
///   * <c>TotalAvailableMemoryBytes - MemoryLoadBytes</c> mixes two different
///     quantities the moment a heap limit is in force. With
///     <c>DOTNET_GCHeapHardLimit</c> set to 3 GB, the first is the 3 GB limit
///     while the second is still the *machine's* ~20 GB of committed memory, so
///     the subtraction saturates at zero and every open is refused on a machine
///     with 9 GB free. Measured, same run.
///
/// So both sources are read and the smaller is believed, because either can be
/// the binding constraint:
///
///   * the OS, via <c>GlobalMemoryStatusEx</c>. <c>ullAvailPhys</c> is what the
///     machine has left across everything running, which is the right question
///     for a desktop app sharing a workstation, and it is available immediately
///     and always.
///   * this process's own headroom under a limit, when there is one. A limit is
///     recognised by being smaller than the machine's physical memory, and the
///     headroom under it is the limit minus the heap already inside it — not
///     minus the machine's load, which is the arithmetic that broke.
///
/// -1 means neither could answer, and every caller reads that as "do not
/// assume": a platform that cannot report a limit must not have its work
/// refused. Getting that backwards makes the app unusable somewhere nobody
/// tested it.
/// </summary>
public static class SystemMemory
{
    public static long FreeBytes()
    {
        (long osFree, long totalPhysical) = MachineMemory();
        long underLimit = HeadroomUnderAnyLimit(totalPhysical);

        if (osFree < 0)
            return underLimit;      // may itself be -1
        if (underLimit < 0)
            return osFree;
        return Math.Min(osFree, underLimit);
    }

    /// <summary>
    /// How much room is left inside a GC heap limit, or -1 when there is no
    /// limit smaller than the machine.
    /// </summary>
    private static long HeadroomUnderAnyLimit(long totalPhysical)
    {
        try
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            long limit = info.TotalAvailableMemoryBytes;
            if (limit <= 0)
                return -1;

            // Equal to (or above) physical memory means no container quota and no
            // heap hard limit: this is just the machine again, and the OS reading
            // above already answered for it — better, because it accounts for
            // what every other process is holding.
            if (totalPhysical > 0 && limit >= totalPhysical)
                return -1;

            // HeapSizeBytes is 0 until the first collection, which reads as "the
            // whole limit is still free". That is the safe direction: it can only
            // fail to refuse, never refuse wrongly.
            return Math.Max(0, limit - info.HeapSizeBytes);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Free and total physical memory as the OS reports them, or (-1, -1).</summary>
    private static (long Free, long Total) MachineMemory()
    {
        try
        {
            MEMORYSTATUSEX status = new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status)
                ? ((long)status.ullAvailPhys, (long)status.ullTotalPhys)
                : (-1, -1);
        }
        catch
        {
            return (-1, -1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
