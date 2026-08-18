using System.IO;

namespace MapleBench.Services;

/// <summary>
/// One already-composed frame of an animation, handed to
/// <see cref="GifWriter"/> or <see cref="ApngWriter"/> ready to encode.
///
/// The pixel contract is deliberately the narrowest thing both encoders can
/// agree on: RGBA32, row-major, stride exactly <c>width * 4</c>, straight
/// (non-premultiplied) alpha, byte order R, G, B, A.  Nothing here crops,
/// offsets or composites — a WZ animation's frames have per-frame origins and
/// different bounding boxes, and resolving all of that is the caller's job.
/// By the time a frame arrives here it must already be the full canvas at the
/// animation's final size, or the two encoders would each have to grow their
/// own copy of the same compositing rules and drift apart.
///
/// <para><paramref name="DelayMs"/> is the time this frame is shown, in
/// milliseconds, because that is what WZ stores (the <c>delay</c> property on
/// each frame node).  Each container then expresses it as best it can: APNG
/// keeps the millisecond exactly, GIF cannot — see
/// <see cref="GifWriter.Write"/>.</para>
/// </summary>
/// <param name="Rgba">
/// The frame's pixels. Must be exactly <c>width * height * 4</c> bytes; a
/// shorter or longer array is rejected rather than padded, because a silently
/// truncated sprite sheet is far harder to notice than a failed export.
/// </param>
/// <param name="DelayMs">How long this frame is displayed, in milliseconds.</param>
public readonly record struct AnimationFrameImage(byte[] Rgba, int DelayMs);

/// <summary>
/// A self-contained GIF89a encoder for MapleStory sprite animations.
///
/// This exists because the two obvious alternatives both fail here.
/// <c>System.Drawing</c> can save a GIF, but only a single frame, and its
/// quantiser produces a palette with no transparent entry — every sprite comes
/// out on a black rectangle.  A NuGet imaging library would solve it, but this
/// project ships as a single self-hosted exe and every added dependency is one
/// more thing that has to keep working on a machine we do not control.  The
/// format is small enough to write correctly in one file, so it is written
/// here.
///
/// <para><b>What GIF cannot do, stated honestly.</b>  GIF has no alpha channel.
/// It has one palette index that means "leave whatever is underneath alone",
/// and that is the whole of its transparency.  So this encoder makes a hard
/// cut: any pixel with alpha &lt; 128 becomes fully transparent, any pixel with
/// alpha &gt;= 128 becomes fully opaque, and the intermediate values are thrown
/// away.  For the great majority of MapleStory art — mobs, NPCs, tiles, item
/// icons — this is invisible, because that art was drawn for a client that
/// blits hard-edged sprites and its alpha is already almost entirely 0 or 255.
/// For anything with a soft edge — a skill effect, a glow, a drop shadow, an
/// anti-aliased outline over a light background — it is very visible: the
/// half-transparent border pixels snap to opaque and the sprite exports with a
/// hard fringe in whatever colour the artist blended toward.  <b>APNG is the
/// lossless option</b> and <see cref="ApngWriter"/> should be preferred whenever
/// the consumer can read it; GIF is here for the places that still only accept
/// GIF.</para>
///
/// <para>Colour is the second lossy step: 8-bit indices, so 255 opaque colours
/// per frame plus the transparent index.  Frames within the palette budget are
/// reproduced exactly; frames over it are median-cut quantised.  See
/// <see cref="Write"/>.</para>
///
/// <para>The class holds no state — the only statics are a readonly CRC-style
/// lookup-free helper set — so two exports can run on two threads without
/// interfering.</para>
/// </summary>
public static class GifWriter
{
    /// <summary>Alpha at or above this counts as opaque; below it becomes the transparent index.</summary>
    private const int AlphaCutoff = 128;

    /// <summary>
    /// Palette entries available for real colour. The 256th is reserved for the
    /// transparent index, unconditionally — even for a frame with no
    /// transparent pixel — so that every frame in an animation is quantised
    /// against the same budget and one opaque frame cannot end up with subtly
    /// different colour fidelity from its neighbours.
    /// </summary>
    private const int MaxOpaqueColors = 255;

    /// <summary>
    /// Codes 0..4094 are handed out; 4095 is left unused so that the code width
    /// never has to grow past the 12 bits the format allows. giflib is
    /// conservative in the same place and for the same reason.
    /// </summary>
    private const int MaxLzwCode = 4095;

    /// <summary>
    /// Encodes <paramref name="frames"/> into a complete GIF89a byte stream.
    ///
    /// <para><b>Delay.</b> GIF stores frame delay in centiseconds, in the
    /// Graphic Control Extension, so a millisecond delay has to be divided by
    /// ten and rounded. The rounding is away-from-zero and then <b>clamped to a
    /// minimum of 1</b>, which is the important part: MapleStory delays are
    /// frequently under 10 ms (a 6-frame attack animation at 4 ms a frame is
    /// ordinary), and a naive divide writes 0. A delay of 0 does not mean "very
    /// fast" to a GIF decoder — browsers and most viewers treat 0 (and often 1)
    /// as "the author did not mean this" and substitute 10 cs, so the naive
    /// encoder turns a 24 ms animation into a 60 ms one and the sprite appears
    /// to play at a quarter speed. Clamping to 1 cs keeps it as fast as the
    /// format can honestly go and keeps the error in the direction the viewer
    /// can see and reason about. Delays are additionally capped at 65535 cs,
    /// the width of the field.</para>
    ///
    /// <para><b>Transparency.</b> Each frame gets a Graphic Control Extension
    /// with the transparent-colour flag set and disposal method 2, "restore to
    /// background". Disposal 2 is what stops silhouettes smearing: MapleStory
    /// frames routinely differ in shape (a sword swing is wider than the idle
    /// pose), and under the default disposal 0 the wide frame's pixels stay on
    /// screen underneath the narrow one and the character grows a permanent
    /// afterimage. Restoring to background between frames costs some
    /// compression — nothing is shared between frames — and buys correctness.
    /// No global colour table is written, deliberately: with one present, a
    /// handful of decoders fill the "background" with the global table's
    /// background entry rather than with transparency, and the sprite would
    /// export on a coloured card.</para>
    ///
    /// <para><b>Colour.</b> Each frame carries its own local colour table, so a
    /// frame that changes palette mid-animation (a mob flashing red on hit) is
    /// not forced through its neighbours' colours. Within a frame, if 255 or
    /// fewer distinct opaque colours are present they are all kept and the
    /// frame is reproduced <b>exactly</b>; above that a deterministic median cut
    /// reduces them to 255 and every pixel maps to its nearest surviving
    /// colour. There is no dithering, so a smooth gradient exports with visible
    /// banding rather than with noise — for pixel art that is the right trade,
    /// and for a gradient it is another reason to reach for
    /// <see cref="ApngWriter"/>.</para>
    /// </summary>
    /// <param name="width">Frame width in pixels. Must be positive.</param>
    /// <param name="height">Frame height in pixels. Must be positive.</param>
    /// <param name="frames">
    /// The frames, in order. Must not be empty — a GIF with no image blocks is
    /// a file that some viewers open as a blank rectangle and others reject, so
    /// it is refused here rather than produced.
    /// </param>
    /// <param name="loop">
    /// When true and there is more than one frame, a NETSCAPE2.0 application
    /// extension with a loop count of 0 (forever) is emitted. When false the
    /// extension is omitted entirely rather than written with a count of 1,
    /// because "no extension" is the unambiguous way to say "play once" — a
    /// count of 1 is read by some viewers as one loop *after* the first play,
    /// i.e. twice through.
    /// </param>
    /// <returns>A complete GIF89a file, header through trailer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frames"/> is null, or a frame's pixel array is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="frames"/> is empty, a dimension is not positive, or a
    /// frame's pixel array is not exactly <c>width * height * 4</c> bytes.
    /// </exception>
    public static byte[] Write(int width, int height, IReadOnlyList<AnimationFrameImage> frames, bool loop = true)
    {
        Validate(width, height, frames);

        using var output = new MemoryStream();

        // Header and Logical Screen Descriptor. Packed byte 0x70 is: no global
        // colour table (bit 7 clear), colour resolution 8 bits per channel
        // (bits 6-4 = 7), not sorted, table-size bits ignored. Background
        // colour index and pixel aspect ratio are both 0, meaning "unspecified",
        // which is what every modern decoder assumes anyway.
        output.Write("GIF89a"u8);
        WriteUInt16(output, ClampToUInt16(width));
        WriteUInt16(output, ClampToUInt16(height));
        output.WriteByte(0x70);
        output.WriteByte(0x00);
        output.WriteByte(0x00);

        if (loop && frames.Count > 1)
        {
            WriteNetscapeLoopExtension(output);
        }

        foreach (AnimationFrameImage frame in frames)
        {
            QuantisedFrame quantised = Quantise(width, height, frame.Rgba);
            WriteGraphicControlExtension(output, DelayCentiseconds(frame.DelayMs), quantised.TransparentIndex);
            WriteImageBlock(output, width, height, quantised);
        }

        output.WriteByte(0x3B); // Trailer.
        return output.ToArray();
    }

    /// <summary>
    /// The shared precondition check. Kept separate from <see cref="Write"/> so
    /// that nothing is written to the output stream before the input is known
    /// to be whole: a caller that catches the exception must not be left
    /// holding a half-built file.
    /// </summary>
    private static void Validate(int width, int height, IReadOnlyList<AnimationFrameImage> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException(
                $"A GIF needs a positive size; got {width}x{height}.",
                width <= 0 ? nameof(width) : nameof(height));
        }

        if (frames.Count == 0)
        {
            throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));
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
    /// Converts a millisecond delay to the centiseconds GIF stores, never
    /// returning 0. See the delay discussion on <see cref="Write"/> for why 0
    /// is worse than wrong here.
    /// </summary>
    private static ushort DelayCentiseconds(int delayMs)
    {
        if (delayMs <= 0)
        {
            return 1;
        }

        long centiseconds = (long)Math.Round(delayMs / 10.0, MidpointRounding.AwayFromZero);
        if (centiseconds < 1)
        {
            centiseconds = 1;
        }

        return (ushort)Math.Min(centiseconds, ushort.MaxValue);
    }

    /// <summary>
    /// The NETSCAPE2.0 application extension, the only way to say "loop
    /// forever" in a format that never specified looping. A loop count of 0
    /// means infinite; the block is a fixed 19 bytes.
    /// </summary>
    private static void WriteNetscapeLoopExtension(Stream output)
    {
        output.WriteByte(0x21); // Extension introducer.
        output.WriteByte(0xFF); // Application extension label.
        output.WriteByte(0x0B); // Block size: 8-byte identifier + 3-byte auth code.
        output.Write("NETSCAPE2.0"u8);
        output.WriteByte(0x03); // Sub-block length.
        output.WriteByte(0x01); // Sub-block id: looping.
        WriteUInt16(output, 0); // Loop count, 0 = forever.
        output.WriteByte(0x00); // Block terminator.
    }

    /// <summary>
    /// The per-frame Graphic Control Extension: delay, transparent index and
    /// disposal. Packed byte 0x09 is disposal method 2 (bits 4-2) plus the
    /// transparent-colour flag (bit 0).
    /// </summary>
    private static void WriteGraphicControlExtension(Stream output, ushort delayCentiseconds, byte transparentIndex)
    {
        output.WriteByte(0x21); // Extension introducer.
        output.WriteByte(0xF9); // Graphic control label.
        output.WriteByte(0x04); // Block size.
        output.WriteByte(0x09); // Disposal 2 | transparent colour flag.
        WriteUInt16(output, delayCentiseconds);
        output.WriteByte(transparentIndex);
        output.WriteByte(0x00); // Block terminator.
    }

    /// <summary>
    /// The Image Descriptor, the local colour table and the LZW-compressed
    /// indices. Every frame is written full-size at (0,0); no attempt is made
    /// to shrink a frame to its changed rectangle, because disposal 2 clears
    /// the canvas between frames anyway and a partial frame under disposal 2
    /// would leave the rest of the sprite blank.
    /// </summary>
    private static void WriteImageBlock(Stream output, int width, int height, QuantisedFrame frame)
    {
        int tableSize = frame.TableSize;
        int sizeField = BitsFor(tableSize) - 1;

        output.WriteByte(0x2C); // Image separator.
        WriteUInt16(output, 0); // Left.
        WriteUInt16(output, 0); // Top.
        WriteUInt16(output, ClampToUInt16(width));
        WriteUInt16(output, ClampToUInt16(height));
        output.WriteByte((byte)(0x80 | sizeField)); // Local colour table present, unsorted, not interlaced.

        // The table has to be a power of two; entries past the ones we filled
        // are written black and never referenced by any index.
        byte[] table = new byte[tableSize * 3];
        Array.Copy(frame.Palette, table, frame.Palette.Length);
        output.Write(table, 0, table.Length);

        int minCodeSize = Math.Max(2, BitsFor(tableSize));
        output.WriteByte((byte)minCodeSize);
        WriteSubBlocks(output, LzwCompress(frame.Indices, minCodeSize));
        output.WriteByte(0x00); // End of image data.
    }

    /// <summary>
    /// GIF carries variable-length data as a chain of sub-blocks, each prefixed
    /// with its own length byte and capped at 255, terminated by a zero-length
    /// block written by the caller.
    /// </summary>
    private static void WriteSubBlocks(Stream output, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(255, data.Length - offset);
            output.WriteByte((byte)chunk);
            output.Write(data, offset, chunk);
            offset += chunk;
        }
    }

    /// <summary>
    /// A frame reduced to palette indices: the table itself as RGB triples, the
    /// per-pixel indices, and which index means transparent.
    /// </summary>
    private readonly struct QuantisedFrame
    {
        public QuantisedFrame(byte[] palette, byte[] indices, byte transparentIndex, int tableSize)
        {
            Palette = palette;
            Indices = indices;
            TransparentIndex = transparentIndex;
            TableSize = tableSize;
        }

        /// <summary>RGB triples, three bytes per entry, in index order.</summary>
        public byte[] Palette { get; }

        /// <summary>One index per pixel, row-major.</summary>
        public byte[] Indices { get; }

        /// <summary>The index the Graphic Control Extension nominates as transparent.</summary>
        public byte TransparentIndex { get; }

        /// <summary>The padded, power-of-two colour table size the format requires.</summary>
        public int TableSize { get; }
    }

    /// <summary>
    /// Turns straight-alpha RGBA into palette indices.
    ///
    /// Two paths, and the split is the point. When the frame has 255 or fewer
    /// distinct opaque colours — which is nearly every MapleStory sprite, since
    /// they were authored as indexed art — every colour is kept and the mapping
    /// is exact, so a round trip through GIF changes no opaque pixel at all.
    /// Only past that budget does median cut run, and it is written to be
    /// deterministic (every tie broken by a total ordering on the packed colour
    /// value) so that the same input always produces byte-identical output;
    /// an export that differed run to run would defeat any content hashing
    /// downstream.
    ///
    /// The transparent entry is appended last, so opaque colours occupy
    /// 0..n-1 and the transparent index is n. That ordering matters for the
    /// exact path: it keeps the index of a colour independent of whether the
    /// frame happens to contain transparency.
    /// </summary>
    private static QuantisedFrame Quantise(int width, int height, byte[] rgba)
    {
        int pixelCount = width * height;

        // Histogram of opaque colours, packed as 0x00RRGGBB.
        var histogram = new Dictionary<int, int>();
        for (int p = 0; p < pixelCount; p++)
        {
            int b = p * 4;
            if (rgba[b + 3] < AlphaCutoff)
            {
                continue;
            }

            int packed = (rgba[b] << 16) | (rgba[b + 1] << 8) | rgba[b + 2];
            histogram.TryGetValue(packed, out int count);
            histogram[packed] = count + 1;
        }

        int[] distinct = new int[histogram.Count];
        int[] weights = new int[histogram.Count];
        {
            int i = 0;
            foreach (KeyValuePair<int, int> entry in histogram)
            {
                distinct[i] = entry.Key;
                weights[i] = entry.Value;
                i++;
            }

            // Sorting by packed value is what makes everything downstream
            // deterministic: Dictionary enumeration order is not contractual,
            // and median cut's split decisions depend on the input order.
            Array.Sort(distinct, weights);
        }

        int[] palette = distinct.Length <= MaxOpaqueColors
            ? distinct
            : MedianCut(distinct, weights, MaxOpaqueColors);

        byte transparentIndex = (byte)palette.Length;
        int tableSize = Math.Max(4, NextPowerOfTwo(palette.Length + 1));

        byte[] paletteBytes = new byte[palette.Length * 3];
        for (int i = 0; i < palette.Length; i++)
        {
            paletteBytes[i * 3] = (byte)(palette[i] >> 16);
            paletteBytes[i * 3 + 1] = (byte)(palette[i] >> 8);
            paletteBytes[i * 3 + 2] = (byte)palette[i];
        }

        // Exact colour -> index lookup, plus a memo for the quantised path so
        // the nearest-colour search runs once per distinct colour rather than
        // once per pixel.
        var lookup = new Dictionary<int, byte>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i]] = (byte)i;
        }

        byte[] indices = new byte[pixelCount];
        for (int p = 0; p < pixelCount; p++)
        {
            int b = p * 4;
            if (rgba[b + 3] < AlphaCutoff)
            {
                indices[p] = transparentIndex;
                continue;
            }

            int packed = (rgba[b] << 16) | (rgba[b + 1] << 8) | rgba[b + 2];
            if (!lookup.TryGetValue(packed, out byte index))
            {
                index = NearestIndex(palette, packed);
                lookup[packed] = index;
            }

            indices[p] = index;
        }

        return new QuantisedFrame(paletteBytes, indices, transparentIndex, tableSize);
    }

    /// <summary>
    /// Median cut, reducing an arbitrary colour set to <paramref name="maxColors"/>.
    ///
    /// The colours are held in one array and boxes are ranges within it, so a
    /// split is an in-place sort of a sub-range rather than an allocation. At
    /// each step the box with the largest extent along any single channel is
    /// split at its weighted median along that channel, which keeps the fine
    /// detail where the image actually spends its pixels. Every choice —
    /// which box, which channel, where to cut — falls back to the packed colour
    /// value on a tie so the result never depends on hash ordering.
    ///
    /// Chosen over octree because it degrades more gracefully on the input this
    /// actually sees: sprite sheets with a few large flat regions and one
    /// gradient, where an octree tends to spend its budget on the flat regions
    /// it could have merged.
    /// </summary>
    private static int[] MedianCut(int[] colors, int[] weights, int maxColors)
    {
        int[] values = (int[])colors.Clone();
        int[] counts = (int[])weights.Clone();

        var boxes = new List<(int Start, int Length)> { (0, values.Length) };

        while (boxes.Count < maxColors)
        {
            int chosen = -1;
            int chosenExtent = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                (int start, int length) = boxes[i];
                if (length < 2)
                {
                    continue;
                }

                int extent = LongestChannelExtent(values, start, length, out _);
                if (extent > chosenExtent)
                {
                    chosenExtent = extent;
                    chosen = i;
                }
            }

            // Every remaining box is a single colour, or every box is flat:
            // nothing left to gain from splitting.
            if (chosen < 0 || chosenExtent == 0)
            {
                break;
            }

            (int boxStart, int boxLength) = boxes[chosen];
            LongestChannelExtent(values, boxStart, boxLength, out int channel);
            SortRangeByChannel(values, counts, boxStart, boxLength, channel);

            long total = 0;
            for (int i = boxStart; i < boxStart + boxLength; i++)
            {
                total += counts[i];
            }

            long half = total / 2;
            long running = 0;
            int split = boxStart;
            while (split < boxStart + boxLength - 1 && running + counts[split] <= half)
            {
                running += counts[split];
                split++;
            }

            // Both halves must be non-empty or the loop would never terminate.
            if (split <= boxStart)
            {
                split = boxStart + 1;
            }

            boxes[chosen] = (boxStart, split - boxStart);
            boxes.Add((split, boxStart + boxLength - split));
        }

        int[] result = new int[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            result[i] = WeightedAverage(values, counts, boxes[i].Start, boxes[i].Length);
        }

        // A stable, value-based order so the palette itself is reproducible.
        Array.Sort(result);
        return result;
    }

    /// <summary>
    /// The widest spread across R, G and B within a box, and which channel it
    /// was. Ties resolve to the lowest channel number so the choice is fixed.
    /// </summary>
    private static int LongestChannelExtent(int[] values, int start, int length, out int channel)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        for (int i = start; i < start + length; i++)
        {
            int v = values[i];
            int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (b < bMin) bMin = b;
            if (b > bMax) bMax = b;
        }

        int rangeR = rMax - rMin, rangeG = gMax - gMin, rangeB = bMax - bMin;
        channel = 0;
        int best = rangeR;
        if (rangeG > best)
        {
            best = rangeG;
            channel = 1;
        }

        if (rangeB > best)
        {
            best = rangeB;
            channel = 2;
        }

        return best;
    }

    /// <summary>
    /// Sorts a box's colours by one channel, carrying their pixel counts along.
    /// The comparison falls through to the whole packed value so that colours
    /// sharing a channel value still have a defined order.
    /// </summary>
    private static void SortRangeByChannel(int[] values, int[] counts, int start, int length, int channel)
    {
        int shift = channel switch { 0 => 16, 1 => 8, _ => 0 };

        // Channel in the high bits, full colour in the low bits: one integer
        // key that orders by the split channel first and by the whole colour
        // second, so no two entries ever compare equal and the sort is a total
        // order regardless of which sort algorithm runs.
        int[] keys = new int[length];
        int[] order = new int[length];
        for (int i = 0; i < length; i++)
        {
            int v = values[start + i];
            keys[i] = (((v >> shift) & 0xFF) << 24) | (v & 0x00FFFFFF);
            order[i] = i;
        }

        Array.Sort(keys, order);

        int[] sortedValues = new int[length];
        int[] sortedCounts = new int[length];
        for (int i = 0; i < length; i++)
        {
            sortedValues[i] = values[start + order[i]];
            sortedCounts[i] = counts[start + order[i]];
        }

        Array.Copy(sortedValues, 0, values, start, length);
        Array.Copy(sortedCounts, 0, counts, start, length);
    }

    /// <summary>
    /// A box's representative colour: the pixel-count-weighted mean, which
    /// pulls the entry toward the colours that actually cover area rather than
    /// toward the outliers that merely exist.
    /// </summary>
    private static int WeightedAverage(int[] values, int[] counts, int start, int length)
    {
        long weight = 0, r = 0, g = 0, b = 0;
        for (int i = start; i < start + length; i++)
        {
            long w = counts[i];
            int v = values[i];
            weight += w;
            r += ((v >> 16) & 0xFF) * w;
            g += ((v >> 8) & 0xFF) * w;
            b += (v & 0xFF) * w;
        }

        if (weight == 0)
        {
            return values[start];
        }

        int rr = (int)((r + weight / 2) / weight);
        int gg = (int)((g + weight / 2) / weight);
        int bb = (int)((b + weight / 2) / weight);
        return (rr << 16) | (gg << 8) | bb;
    }

    /// <summary>
    /// Nearest palette entry by squared distance in RGB. Not perceptually
    /// weighted — a Lab-space match would be closer to what the eye does, but
    /// it would also make the output depend on a colour-science constant, and
    /// for indexed sprite art the difference is not visible.
    /// </summary>
    private static byte NearestIndex(int[] palette, int packed)
    {
        int r = (packed >> 16) & 0xFF, g = (packed >> 8) & 0xFF, b = packed & 0xFF;
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int c = palette[i];
            int dr = r - ((c >> 16) & 0xFF);
            int dg = g - ((c >> 8) & 0xFF);
            int db = b - (c & 0xFF);
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return (byte)best;
    }

    /// <summary>
    /// GIF's LZW: variable-width codes, LSB-first, with an explicit clear code
    /// and a dictionary that resets when it fills.
    ///
    /// The one subtle rule is when the code width grows. The decoder widens
    /// after it installs the entry that fills the current width, and because
    /// the encoder installs each entry one code <i>earlier</i> than the decoder
    /// does, the encoder must widen when the next free code passes
    /// <c>1 &lt;&lt; codeSize</c> rather than when it reaches it. Getting this
    /// off by one produces a file that looks plausible and decodes to garbage
    /// partway through the first large frame, which is exactly the kind of bug
    /// that survives a "did bytes come back?" test.
    /// </summary>
    private static byte[] LzwCompress(byte[] indices, int minCodeSize)
    {
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;

        var packer = new BitPacker();
        var dictionary = new Dictionary<int, int>();

        packer.Write(clearCode, codeSize);

        if (indices.Length == 0)
        {
            packer.Write(endCode, codeSize);
            return packer.ToArray();
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int current = indices[i];
            int key = (prefix << 8) | current;
            if (dictionary.TryGetValue(key, out int existing))
            {
                prefix = existing;
                continue;
            }

            packer.Write(prefix, codeSize);
            if (nextCode < MaxLzwCode)
            {
                dictionary[key] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                packer.Write(clearCode, codeSize);
                dictionary.Clear();
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
            }

            prefix = current;
        }

        packer.Write(prefix, codeSize);
        packer.Write(endCode, codeSize);
        return packer.ToArray();
    }

    /// <summary>
    /// Packs variable-width codes least-significant-bit first, which is GIF's
    /// convention and the opposite of PNG's. An instance is created per call,
    /// so nothing is shared between concurrent encodes.
    /// </summary>
    private sealed class BitPacker
    {
        private readonly List<byte> _bytes = new();
        private int _buffer;
        private int _bits;

        public void Write(int code, int size)
        {
            _buffer |= code << _bits;
            _bits += size;
            while (_bits >= 8)
            {
                _bytes.Add((byte)(_buffer & 0xFF));
                _buffer >>= 8;
                _bits -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_buffer & 0xFF));
                _buffer = 0;
                _bits = 0;
            }

            return _bytes.ToArray();
        }
    }

    /// <summary>Bits needed to address <paramref name="size"/> entries, minimum one.</summary>
    private static int BitsFor(int size)
    {
        int bits = 1;
        while ((1 << bits) < size)
        {
            bits++;
        }

        return bits;
    }

    /// <summary>Rounds up to a power of two, which is the only colour table size GIF accepts.</summary>
    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    /// <summary>
    /// GIF's size fields are 16 bit. A frame larger than 65535 in either
    /// direction cannot be described, and clamping keeps the header
    /// self-consistent rather than writing a wrapped value; nothing in WZ comes
    /// close to that size, so this is a guard rather than a policy.
    /// </summary>
    private static ushort ClampToUInt16(int value) => (ushort)Math.Min(value, ushort.MaxValue);

    /// <summary>GIF integers are little-endian, unlike PNG's.</summary>
    private static void WriteUInt16(Stream output, ushort value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)(value >> 8));
    }
}
