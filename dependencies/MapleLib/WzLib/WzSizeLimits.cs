using System;
using System.IO;

namespace MapleLib.WzLib
{
    /// <summary>Unsigned PKG1 offsets, including Daruma's patched v232 seek path.</summary>
    public static class WzSizeLimits
    {
        public const long MaximumFileBytes = uint.MaxValue;
        // Leave room for directory records and changes during serialization.
        public const long PlanningFileBytes = MaximumFileBytes - 128L * 1024 * 1024;

        public static uint Advance(uint position, long length)
        {
            if (length < 0 || length > MaximumFileBytes - position)
                throw new WzCapacityException("This WZ exceeds the unsigned 4 GiB address space. " +
                    "Distribute its images across numbered WZ files before saving.");
            return (uint)(position + length);
        }
    }

    public sealed class WzCapacityException : IOException
    {
        public WzCapacityException(string message) : base(message) { }
    }
}
