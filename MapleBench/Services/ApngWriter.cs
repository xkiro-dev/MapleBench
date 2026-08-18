using System.IO;
using System.IO.Compression;

namespace MapleBench.Services;

/// <summary>
/// A self-contained APNG encoder for MapleStory sprite animations.
///
/// This is the lossless half of the pair. Where <see cref="GifWriter"/> has to
/// throw away the alpha channel and most of the colour, APNG carries 8-bit RGBA
/// straight through: a soft skill glow, an anti-aliased outline and a
/// 24-bit-colour gradient all survive a round trip byte for byte. When the
/// consumer can read APNG — every current browser, most image viewers, Discord,
/// Photoshop with a plugin — it is the format to hand them, and GIF is the
/// fallback for the places that still only take GIF.
///
/// <para>It is written by hand for the same reason the GIF encoder is:
/// <c>System.Drawing</c> has no concept of an animated PNG (its PNG encoder
/// writes one IDAT and stops), and pulling in an imaging package to emit four
/// well-specified chunk types would add a dependency to a single-exe app for
/// something the BCL's <see cref="ZLibStream"/> already does the hard part of.</para>
///
/// <para><b>The limits, stated honestly.</b> APNG loses nothing about the
/// pixels, but it is not free: every frame here is stored full-size and
/// uncomposited against its predecessor, so a 30-frame animation is roughly 30
/// times the size of one frame rather than the small fraction an
/// inter-frame-differenced encoder would achieve. That is a deliberate trade —
/// see <see cref="Write"/> for why the dispose and blend settings that make it
/// large are also the settings that make it correct. The other limit is
/// support: a decoder that does not know APNG shows the first frame only, which
/// is graceful but is still a still image.</para>
///
/// <para>The class holds no mutable state; the only static is the CRC lookup
/// table, which is computed once and never written again, so concurrent
/// exports do not interact.</para>
/// </summary>
public static class ApngWriter
{
    /// <summary>
    /// The standard PNG CRC-32 polynomial table (reflected 0xEDB88320). Built
    /// once at type initialisation and never mutated afterwards, so it is safe
    /// to read from any number of threads.
    /// </summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The eight bytes every PNG and APNG file starts with.</summary>
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Encodes <paramref name="frames"/> into a complete APNG byte stream.
    ///
    /// <para><b>Structure.</b> Signature, IHDR, acTL, then for the first frame
    /// an fcTL followed by IDAT, then for every later frame an fcTL followed by
    /// an fdAT, then IEND. The first frame lives in IDAT rather than in an fdAT
    /// because that is what makes the file backwards compatible: acTL, fcTL and
    /// fdAT all have a lowercase first letter, marking them ancillary, so a
    /// decoder that has never heard of APNG skips all three and sees an
    /// ordinary single-frame PNG of the first frame. Putting frame one in an
    /// fdAT instead would leave such a decoder with an empty image.</para>
    ///
    /// <para><b>Sequence numbers.</b> fcTL and fdAT share one counter, and it
    /// must increase by exactly one across the whole file with no gaps — so the
    /// numbering runs fcTL 0, IDAT (unnumbered), fcTL 1, fdAT 2, fcTL 3, fdAT 4
    /// and onward, not 0..n-1 over the fcTLs alone. Decoders check this
    /// strictly and reject the file outright when it is wrong, which is why the
    /// counter is threaded through rather than recomputed per frame.</para>
    ///
    /// <para><b>Dispose and blend.</b> Every frame is written full-size at
    /// (0,0) with <c>dispose_op = 1</c> (APNG_DISPOSE_OP_BACKGROUND) and
    /// <c>blend_op = 0</c> (APNG_BLEND_OP_SOURCE). Both settings exist to stop
    /// the previous frame leaking into the next one, and both matter for this
    /// art specifically. Blend SOURCE means a frame's pixels <i>replace</i> what
    /// is under them instead of alpha-compositing over it; under the alternative
    /// (OVER) a semi-transparent sprite would accumulate density with every
    /// frame it stayed still for, and a glow effect would brighten to solid over
    /// a few loops. Dispose BACKGROUND clears the canvas to transparent before
    /// the next frame, which is what stops a wide frame's silhouette (a sword
    /// swing) staying on screen behind a narrow one (the idle pose). Between
    /// them they make each frame independent, which costs the file size noted
    /// on the class and buys an animation that looks the same on the first loop
    /// and the fiftieth.</para>
    ///
    /// <para><b>Delay.</b> APNG stores delay as an exact rational,
    /// <c>delay_num / delay_den</c> seconds, so a millisecond delay is written
    /// as <c>DelayMs / 1000</c> with no rounding at all — unlike GIF, which has
    /// only centiseconds and forces
    /// <see cref="GifWriter.Write">a clamp to keep short delays from becoming
    /// zero</see>. A delay of 0 is legal here and means "as fast as the decoder
    /// can go", so a non-positive input is written as 0 rather than nudged up to
    /// 1: the format can express the intent, so it should. Delays above 65535 ms
    /// are capped at the field width.</para>
    ///
    /// <para><b>Filtering.</b> Every scanline is written with PNG filter type 0
    /// (None) and left to Deflate. A per-line Paeth/Sub/Up heuristic would
    /// shrink the output, typically by a useful margin on photographic content,
    /// but on flat-shaded sprite art with large runs of identical RGBA the
    /// Deflate stage already captures nearly all of it, and an unfiltered
    /// scanline is trivially verifiable. Correct and slightly large beats clever
    /// here.</para>
    /// </summary>
    /// <param name="width">Frame width in pixels. Must be positive.</param>
    /// <param name="height">Frame height in pixels. Must be positive.</param>
    /// <param name="frames">
    /// The frames, in order. Must not be empty: acTL's <c>num_frames</c> may
    /// not be zero, and a PNG with no IDAT is not a PNG, so there is no
    /// meaningful file to produce.
    /// </param>
    /// <param name="loop">
    /// When true, acTL's <c>num_plays</c> is 0, which APNG defines as looping
    /// forever. When false it is 1, meaning the animation runs once and holds
    /// on the last frame.
    /// </param>
    /// <returns>A complete APNG file, signature through IEND.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frames"/> is null, or a frame's pixel array is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="frames"/> is empty, a dimension is not positive, or a
    /// frame's pixel array is not exactly <c>width * height * 4</c> bytes.
    /// </exception>
    public static byte[] Write(int width, int height, IReadOnlyList<AnimationFrameImage> frames, bool loop = true)
    {
        Validate(width, height, frames);

        using var output = new MemoryStream();
        output.Write(Signature);

        WriteChunk(output, "IHDR"u8, BuildIhdr(width, height));
        WriteChunk(output, "acTL"u8, BuildActl(frames.Count, loop));

        int sequence = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            AnimationFrameImage frame = frames[i];
            WriteChunk(output, "fcTL"u8, BuildFctl(sequence++, width, height, frame.DelayMs));

            byte[] compressed = Deflate(Filter(width, height, frame.Rgba));
            if (i == 0)
            {
                WriteChunk(output, "IDAT"u8, compressed);
            }
            else
            {
                // fdAT is an IDAT whose payload is prefixed by its sequence
                // number; the number is inside the chunk data, so it is part of
                // what the CRC covers.
                byte[] payload = new byte[4 + compressed.Length];
                WriteUInt32BigEndian(payload, 0, (uint)sequence++);
                Buffer.BlockCopy(compressed, 0, payload, 4, compressed.Length);
                WriteChunk(output, "fdAT"u8, payload);
            }
        }

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>
    /// The precondition check, run to completion before a single byte is
    /// written so that a rejected call cannot leave a caller holding a
    /// partially built file. The rules match <see cref="GifWriter"/>'s exactly:
    /// a caller choosing between the two formats should not have to discover
    /// that one of them accepts input the other refuses.
    /// </summary>
    private static void Validate(int width, int height, IReadOnlyList<AnimationFrameImage> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException(
                $"A PNG needs a positive size; got {width}x{height}.",
                width <= 0 ? nameof(width) : nameof(height));
        }

        if (frames.Count == 0)
        {
            throw new ArgumentException("An APNG needs at least one frame.", nameof(frames));
        }

        long expected = (long)width * height * 4;
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] pixels = frames[i].Rgba;
            if (pixels is null)
            {
                throw new ArgumentNullException(nameof(frames), $"Frame {i} has no pixel data.");
            }

            if (pixels.LongLength != expected)
            {
                throw new ArgumentException(
                    $"Frame {i} is {pixels.LongLength} bytes but {width}x{height} RGBA needs {expected}.",
                    nameof(frames));
            }
        }
    }

    /// <summary>
    /// The image header: size, then bit depth 8 and colour type 6 (truecolour
    /// with alpha), compression 0 and filter 0 (the only values PNG defines),
    /// and interlace 0. Colour type 6 is what makes this the lossless option —
    /// it is the only PNG mode with a real per-pixel alpha channel, as opposed
    /// to type 3's single transparent palette entry, which is all GIF has.
    /// </summary>
    private static byte[] BuildIhdr(int width, int height)
    {
        byte[] data = new byte[13];
        WriteUInt32BigEndian(data, 0, (uint)width);
        WriteUInt32BigEndian(data, 4, (uint)height);
        data[8] = 8;  // Bit depth.
        data[9] = 6;  // Colour type: RGBA.
        data[10] = 0; // Compression method: Deflate.
        data[11] = 0; // Filter method: adaptive.
        data[12] = 0; // Interlace: none.
        return data;
    }

    /// <summary>
    /// The animation control chunk. It must appear before IDAT — a decoder that
    /// has already started the image data is entitled to ignore an acTL that
    /// turns up late, and would then render a still.
    /// </summary>
    private static byte[] BuildActl(int frameCount, bool loop)
    {
        byte[] data = new byte[8];
        WriteUInt32BigEndian(data, 0, (uint)frameCount);
        WriteUInt32BigEndian(data, 4, loop ? 0u : 1u);
        return data;
    }

    /// <summary>
    /// A frame control chunk. Full-size at the origin, with the dispose and
    /// blend operations explained on <see cref="Write"/>.
    /// </summary>
    private static byte[] BuildFctl(int sequenceNumber, int width, int height, int delayMs)
    {
        byte[] data = new byte[26];
        WriteUInt32BigEndian(data, 0, (uint)sequenceNumber);
        WriteUInt32BigEndian(data, 4, (uint)width);
        WriteUInt32BigEndian(data, 8, (uint)height);
        WriteUInt32BigEndian(data, 12, 0); // x_offset.
        WriteUInt32BigEndian(data, 16, 0); // y_offset.
        WriteUInt16BigEndian(data, 20, (ushort)Math.Clamp(delayMs, 0, ushort.MaxValue));
        WriteUInt16BigEndian(data, 22, 1000); // delay_den: the numerator is milliseconds.
        data[24] = 1; // dispose_op = APNG_DISPOSE_OP_BACKGROUND.
        data[25] = 0; // blend_op = APNG_BLEND_OP_SOURCE.
        return data;
    }

    /// <summary>
    /// Prefixes each scanline with its filter type byte. Filter 0 (None) means
    /// the row is stored as-is; see the filtering note on <see cref="Write"/>
    /// for why nothing cleverer is attempted.
    /// </summary>
    private static byte[] Filter(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        long size = (long)(stride + 1) * height;
        if (size > int.MaxValue)
        {
            throw new ArgumentException("Frame is too large to encode as a single PNG image.", nameof(rgba));
        }

        byte[] filtered = new byte[size];
        for (int y = 0; y < height; y++)
        {
            filtered[y * (stride + 1)] = 0;
            Buffer.BlockCopy(rgba, y * stride, filtered, (y * (stride + 1)) + 1, stride);
        }

        return filtered;
    }

    /// <summary>
    /// Compresses the filtered scanlines into the zlib stream PNG's IDAT and
    /// fdAT payloads carry. <see cref="ZLibStream"/> writes the two-byte zlib
    /// header and the trailing Adler-32 that PNG requires; a bare
    /// <see cref="DeflateStream"/> would produce a chunk that every decoder
    /// rejects, which is a mistake worth naming because the two types differ by
    /// six bytes and one letter.
    /// </summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes one PNG chunk: length, four-byte type, payload, CRC. The CRC
    /// covers the type and the payload but <i>not</i> the length, which is the
    /// detail most hand-rolled encoders get wrong and which produces a file
    /// that libpng refuses with "CRC error" while permissive viewers open it
    /// happily — so it stays broken until it reaches the one consumer that
    /// checks.
    /// </summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteUInt32BigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        uint crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteUInt32BigEndian(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    /// <summary>The CRC-32 PNG specifies, over the chunk type followed by the chunk data.</summary>
    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Builds the 256-entry table for the reflected CRC-32 polynomial PNG uses.</summary>
    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>PNG integers are big-endian, unlike GIF's little-endian fields.</summary>
    private static void WriteUInt32BigEndian(Span<byte> destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    /// <summary>PNG integers are big-endian, unlike GIF's little-endian fields.</summary>
    private static void WriteUInt16BigEndian(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }
}
