using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using MapleLib.Helpers;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;

namespace MapleBench.Services;

/// <summary>
/// Encodes a bitmap into a specific <see cref="WzPngFormat"/>.
///
/// This exists because MapleLib's own replace path re-detects the surface
/// format from the incoming pixels instead of keeping the one the canvas
/// already had.  Two of its choices are actively destructive:
///
///  * Bgr565 picks Format517 whenever both dimensions are multiples of 16.
///    Format517 stores a single colour per 16x16 block, so a 1024x768 map
///    background silently becomes a 64x48 mosaic.
///  * Format4098 (BC7) has a decoder but no encoder, so those canvases are
///    quietly rewritten as uncompressed Format2 and balloon in size.
///
/// Encoding through here and writing with
/// <see cref="WzPngProperty.SetCompressedBytes"/> keeps the original format
/// whenever the format is one MapleLib can write, and reports honestly when it
/// is not.
/// </summary>
public static class MbPngCodec
{
    /// <summary>Formats MapleLib can write. Everything else decodes only.</summary>
    public static bool CanEncode(WzPngFormat format) => format switch
    {
        WzPngFormat.Format1 => true,
        WzPngFormat.Format2 => true,
        WzPngFormat.Format3 => true,
        WzPngFormat.Format257 => true,
        WzPngFormat.Format513 => true,
        WzPngFormat.Format517 => true,
        WzPngFormat.Format1026 => true,
        WzPngFormat.Format2050 => true,
        _ => false,
    };

    public sealed record EncodeResult(WzPngFormat Format, byte[] Compressed, string? Warning);

    /// <summary>
    /// Encodes <paramref name="bitmap"/> into <paramref name="target"/> when
    /// possible, otherwise into the closest lossless format with an explanation.
    /// </summary>
    public static EncodeResult Encode(Bitmap bitmap, WzPngFormat target)
    {
        if (!CanEncode(target))
        {
            byte[] fallback = PixelData(bitmap, WzPngFormat.Format2);
            return new EncodeResult(
                WzPngFormat.Format2,
                Deflate(fallback),
                $"This canvas was stored as {target}, which MapleLib can read but not write. " +
                "It has been saved as uncompressed BGRA8888 instead, so the file will be larger.");
        }

        // Format517 is only meaningful on a 16-aligned grid; anything else would
        // be written with a block count that does not cover the image.
        if (target == WzPngFormat.Format517 && (bitmap.Width % 16 != 0 || bitmap.Height % 16 != 0))
        {
            byte[] alternative = PixelData(bitmap, WzPngFormat.Format513);
            return new EncodeResult(
                WzPngFormat.Format513,
                Deflate(alternative),
                $"The replacement is {bitmap.Width}x{bitmap.Height}, which is not a multiple of 16, " +
                "so the canvas was stored as full-resolution RGB565 rather than the original block format.");
        }

        // DXT3/DXT5 are 4x4 block formats, and MapleLib's encoders throw
        // ArgumentException("DXT3 compression requires width and height to be
        // multiples of 4.") rather than padding (PngUtility.cs:1046, :1118).
        // That reaches the user as a bare 400 telling them a compression
        // algorithm they never chose has a constraint, so it is caught here
        // where the acceptable sizes can be named and the replace can still go
        // through losslessly.
        if (IsBlockCompressed(target) && (bitmap.Width % 4 != 0 || bitmap.Height % 4 != 0))
        {
            byte[] alternative = PixelData(bitmap, WzPngFormat.Format2);
            return new EncodeResult(
                WzPngFormat.Format2,
                Deflate(alternative),
                $"The replacement is {bitmap.Width}x{bitmap.Height}. {target} compresses 4x4 blocks, " +
                $"so both sides must be a multiple of 4 — {Nearest(bitmap.Width)}x{Nearest(bitmap.Height)} " +
                "would keep the original format. It has been saved as uncompressed BGRA8888 instead, " +
                "so the file will be larger.");
        }

        byte[] data = PixelData(bitmap, target);
        return new EncodeResult(target, Deflate(data), null);
    }

    /// <summary>The DXT-backed formats, which only exist on a 4-aligned grid.</summary>
    private static bool IsBlockCompressed(WzPngFormat format) =>
        format is WzPngFormat.Format3 or WzPngFormat.Format1026 or WzPngFormat.Format2050;

    /// <summary>The nearest usable size, rounded up, so the message is actionable.</summary>
    private static int Nearest(int length) => Math.Max(4, (length + 3) / 4 * 4);

    /// <summary>
    /// Raw (uncompressed) pixel bytes for a format.
    ///
    /// Everything routes through MapleLib's own encoders so the byte layout
    /// matches its decoders exactly.  Format513 is the one exception: the public
    /// helper decides 513-vs-517 from the dimensions and cannot be told which we
    /// want, so that conversion is done here.
    /// </summary>
    private static byte[] PixelData(Bitmap bitmap, WzPngFormat format)
    {
        if (format == WzPngFormat.Format513)
            return EncodeRgb565(bitmap);

        (SurfaceFormat surface, bool grayscale) = format switch
        {
            WzPngFormat.Format1 => (SurfaceFormat.Bgra4444, false),
            WzPngFormat.Format2 => (SurfaceFormat.Bgra32, false),
            WzPngFormat.Format3 => (SurfaceFormat.Dxt3, true),
            WzPngFormat.Format257 => (SurfaceFormat.Bgra5551, false),
            WzPngFormat.Format517 => (SurfaceFormat.Bgr565, false),
            WzPngFormat.Format1026 => (SurfaceFormat.Dxt3, false),
            WzPngFormat.Format2050 => (SurfaceFormat.Dxt5, false),
            _ => (SurfaceFormat.Bgra32, false),
        };

        (WzPngFormat produced, byte[] data) = PngUtility.CompressImageToPngFormat(bitmap, surface, grayscale);

        // Sanity check: the helper picks 517 over 513 on its own, and we only
        // ask for Bgr565 when we actually want 517.
        if (produced != format)
        {
            throw new InvalidOperationException(
                $"Expected to encode as {format} but MapleLib produced {produced}.");
        }
        return data;
    }

    /// <summary>
    /// Full-resolution RGB565, two bytes per pixel, row-major — the layout
    /// MapleLib's Format513 decoder reads back.
    /// </summary>
    private static byte[] EncodeRgb565(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] output = new byte[width * height * 2];

        BitmapData locked = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)locked.Scan0;
                int stride = locked.Stride;
                int write = 0;

                for (int y = 0; y < height; y++)
                {
                    byte* row = scan0 + (y * stride);
                    for (int x = 0; x < width; x++)
                    {
                        // Source is BGRA in memory order.
                        byte b = row[(x * 4) + 0];
                        byte g = row[(x * 4) + 1];
                        byte r = row[(x * 4) + 2];

                        ushort packed = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                        output[write++] = (byte)(packed & 0xFF);
                        output[write++] = (byte)(packed >> 8);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
        return output;
    }

    /// <summary>
    /// zlib container plus a raw deflate stream — the exact framing MapleLib's
    /// reader expects (it skips the two header bytes before inflating).
    /// </summary>
    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream stream = new();
        stream.WriteByte(0x78);
        stream.WriteByte(0x9C);
        using (DeflateStream zip = new(stream, CompressionMode.Compress, leaveOpen: true))
        {
            zip.Write(raw, 0, raw.Length);
        }
        return stream.ToArray();
    }
}
