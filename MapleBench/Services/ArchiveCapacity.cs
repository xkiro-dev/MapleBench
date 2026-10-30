using MapleLib.WzLib;
using MapleLib.WzLib.Util;

namespace MapleBench.Services;

/// <summary>Planning estimates only; the writer enforces the exact serialized limit.</summary>
internal static class ArchiveCapacity
{
    public static long Estimate(WzDirectory directory)
    {
        long bytes = 4096;
        foreach (WzImage image in directory.WzImages)
            bytes = checked(bytes + ImageBytes(image) + 256L + image.Name.Length * 2L);
        foreach (WzDirectory child in directory.WzDirectories)
            bytes = checked(bytes + Estimate(child));
        return bytes;
    }

    // .ms images have no source WZ BlockSize. Counting their serialization avoids
    // treating gigabytes of already-parsed art as zero bytes. No output buffer is
    // retained, and SaveImage records the resulting size for subsequent plans.
    public static long ImageBytes(WzImage image)
    {
        if (image.BlockSize > 0) return image.BlockSize;
        using SizeStream stream = new();
        using WzBinaryWriter writer = new(stream, WzTool.GetIvByMapleVersion(WzMapleVersion.BMS));
        image.SaveImage(writer);
        return stream.Length;
    }

    private sealed class SizeStream : Stream
    {
        private long position;
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => position; set { Check(value); position = value; } }
        private static void Check(long value)
        {
            if (value < 0 || value > int.MaxValue)
                throw new IOException("An individual WZ image exceeds its signed 2 GiB block-size field; only separate images can be split across archives.");
        }
        public override void Write(byte[] buffer, int offset, int count) => Advance(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Advance(buffer.Length);
        public override void WriteByte(byte value) => Advance(1);
        private void Advance(int count)
        {
            long next = checked(position + count);
            Check(next);
            position = next;
            length = Math.Max(length, position);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : length));
            return position;
        }
        public override void SetLength(long value) { Check(value); length = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
