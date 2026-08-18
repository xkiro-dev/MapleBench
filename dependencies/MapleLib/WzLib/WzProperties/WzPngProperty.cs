
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MapleLib.Helpers;
using MapleLib.WzLib.Util;
using Microsoft.Xna.Framework.Graphics;

namespace MapleLib.WzLib.WzProperties
{
    /// <summary>
    /// A property that contains the information for a bitmap
    /// https://docs.microsoft.com/en-us/windows/win32/direct3d9/compressed-texture-resources
    /// http://www.sjbrown.co.uk/2006/01/19/dxt-compression-techniques/
    /// https://en.wikipedia.org/wiki/S3_Texture_Compression
    /// </summary>
    public class WzPngProperty : WzImageProperty
    {
        #region Fields
        private int width, height;
        private WzPngFormat format;

        /// <summary>
        /// The magnification the canvas is stored at: the client shifts the
        /// width right by this before laying out a row.
        ///
        /// Its own field because it is its own number. It used to be folded
        /// into <see cref="Format"/> on read and split back out on write, which
        /// round-tripped inside this library and described something else
        /// entirely to the game -- see WzCanvasProperty.WriteValue. Almost
        /// every canvas in a real client stores 0 here.
        /// </summary>
        private int mag;
        internal byte[] compressedImageBytes;

        /// <summary>
        /// The compressed byte count as the archive stated it, captured while
        /// parsing. -1 for a canvas that was never read from a file.
        /// </summary>
        private int storedLength = -1;
        internal Bitmap png;
        internal WzObject parent;
        //internal WzImage imgParent;
        internal bool listWzUsed = false;

        internal WzBinaryReader wzReader;
        internal long offs;
        private readonly object imageLock = new();
        #endregion

        #region Inherited Members
        public override void SetValue(object value)
        {
            if (value is Bitmap)
                PNG = (Bitmap)value;
            else compressedImageBytes = (byte[])value;
        }

        public override WzImageProperty DeepClone()
        {
            WzPngProperty clone = new()
            {
                width = this.width,
                height = this.height,
                format = this.format,
                mag = this.mag,
                listWzUsed = this.listWzUsed
            };

            // Try to copy compressed bytes directly (most efficient)
            if (compressedImageBytes != null)
            {
                clone.compressedImageBytes = (byte[])compressedImageBytes.Clone();
            }
            else if (wzReader != null)
            {
                // Try to get compressed bytes from reader
                try
                {
                    byte[] bytes = GetCompressedBytes(true);
                    if (bytes != null)
                    {
                        clone.compressedImageBytes = (byte[])bytes.Clone();
                    }
                    else
                    {
                        Debug.WriteLine($"[WzPngProperty.DeepClone] GetCompressedBytes returned null for {width}x{height} {format}");
                    }
                }
                catch (Exception ex)
                {
                    // Fall back to bitmap copy if reader fails
                    Debug.WriteLine($"[WzPngProperty.DeepClone] Reader failed, falling back to bitmap: {ex.Message}");
                    var bmp = GetImage(false);
                    if (bmp != null)
                    {
                        clone.PNG = (Bitmap)bmp.Clone();
                    }
                }
            }
            else if (png != null)
            {
                // Copy existing bitmap - this will re-compress!
                Debug.WriteLine($"[WzPngProperty.DeepClone] No compressed bytes or reader, using bitmap copy for {width}x{height}");
                clone.PNG = (Bitmap)png.Clone();
            }
            else
            {
                Debug.WriteLine($"[WzPngProperty.DeepClone] WARNING: No data available for {width}x{height} {format}");
            }

            return clone;
        }

        public override object WzValue { get { return GetImage(false); } }
        /// <summary>
        /// The parent of the object
        /// </summary>
        public override WzObject Parent { get { return parent; } internal set { parent = value; } }
        /*/// <summary>
        /// The image that this property is contained in
        /// </summary>
        public override WzImage ParentImage { get { return imgParent; } internal set { imgParent = value; } }*/
        /// <summary>
        /// The name of the property
        /// </summary>
        public override string Name { get { return "PNG"; } set { } }
        /// <summary>
        /// The WzPropertyType of the property
        /// </summary>
        public override WzPropertyType PropertyType { get { return WzPropertyType.PNG; } }
        public override void WriteValue(WzBinaryWriter writer)
        {
            throw new NotImplementedException("Cannot write a PngProperty");
        }
        /// <summary>
        /// Disposes the object
        /// </summary>
        /// <remarks>
        /// Under <c>imageLock</c>, like every other member of this class.
        ///
        /// It was the one exception, and the exception is the dangerous one:
        /// <see cref="GetImage"/> returns <c>(Bitmap)png.Clone()</c>, which is a
        /// GDI+ call on the same native bitmap handle this method hands to
        /// <c>GdipDisposeImage</c>. Freeing a GDI+ image while another thread is
        /// inside a call on it faults in gdiplus.dll — an AccessViolation, which
        /// no <c>catch</c> in this process can see and which takes the whole
        /// application with it, leaving nothing in the log to say what happened.
        /// Closing a large archive disposes tens of thousands of these at once,
        /// so the window is not theoretical.
        /// </remarks>
        public override void Dispose()
        {
            lock (imageLock)
            {
                compressedImageBytes = null;
                if (png != null)
                {
                    png.Dispose();
                    png = null;
                }
                //this.wzReader.Close(); // closes at WzFile
                this.wzReader = null;
            }
        }
        #endregion

        #region Custom Members
        /// <summary>
        /// The width of the bitmap
        /// </summary>
        /// <summary>The stored magnification. 0 for a canvas held at full size.</summary>
        public int Mag { get { return mag; } set { mag = value; } }

        public int Width { get { return width; } set { width = value; } }
        /// <summary>
        /// The height of the bitmap
        /// </summary>
        public int Height { get { return height; } set { height = value; } }
        /// <summary>
        /// The format of the bitmap
        /// </summary>
        public WzPngFormat Format
        {
            get => format;
            set => format = value;
        }

        public bool ListWzUsed
        {
            get
            {
                lock (imageLock)
                {
                    return listWzUsed;
                }
            }
            set
            {
                lock (imageLock)
                {
                    if (value != listWzUsed)
                    {
                        listWzUsed = value;
                        CompressPng(GetImage(false));
                    }
                }
            }
        }
        /// <summary>
        /// The actual bitmap
        /// </summary>
        public Bitmap PNG
        {
            set
            {
                lock (imageLock)
                {
                    if (png != null && !ReferenceEquals(png, value))
                    {
                        png.Dispose();
                    }

                    CompressPng(value);
                    png = null;
                }
            }
        }

        /// <summary>
        /// Creates a blank WzPngProperty
        /// </summary>
        public WzPngProperty() { }

        /// <summary>
        /// Sets the compressed image bytes directly, along with dimensions and format.
        /// This allows copying image data from one canvas to another without decompression/recompression.
        /// </summary>
        /// <param name="bytes">The compressed image bytes</param>
        /// <param name="width">Image width</param>
        /// <param name="height">Image height</param>
        /// <param name="format">Image format</param>
        public void SetCompressedBytes(byte[] bytes, int width, int height, WzPngFormat format)
        {
            lock (imageLock)
            {
                this.compressedImageBytes = bytes;
                this.storedLength = bytes?.Length ?? -1;
                this.width = width;
                this.height = height;
                this.format = format;
                // These bytes are the whole picture at the size just given, so
                // whatever scale the previous ones were held at does not carry.
                this.mag = 0;

                // Clear any cached bitmap since we're replacing the data
                if (this.png != null)
                {
                    this.png.Dispose();
                    this.png = null;
                }

                // Clear reader reference since we now have the data in memory
                this.wzReader = null;
            }
        }

        /// <summary>
        /// Creates a blank WzPngProperty 
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="parseNow"></param>
        internal WzPngProperty(WzBinaryReader reader, bool parseNow)
        {
            // Keep reader available during eager parse.
            // In ParseEverything mode we decode inside this constructor, before Parent is assigned.
            // list.wz-formatted PNGs require WzKey from reader, so set it upfront.
            this.wzReader = reader;

            // Read compressed bytes
            width = reader.ReadCompressedInt();
            height = reader.ReadCompressedInt();

            // Two numbers, kept as two.
            //
            // This used to be `format1 + (format2 << 8)`, which is lossless only
            // while the second one is 0. It is 0 for every canvas in an ordinary
            // client -- 0 of ~120,000 scanned in a real one -- but a scaled
            // canvas stored as (513, 4) was read as format 1537, a format no
            // client accepts, and then written back out under that name.
            format = (WzPngFormat)reader.ReadCompressedInt();
            mag = reader.ReadCompressedInt();

            reader.BaseStream.Position += 4;
            offs = reader.BaseStream.Position;
            int len = reader.ReadInt32() - 1;
            // Kept, because it was read anyway. See CompressedLength: without it,
            // a caller that only wants to know whether there are any bytes has to
            // seek back here for four it already went past, and a sweep over a
            // whole client turns into millions of random reads.
            storedLength = len;
            reader.BaseStream.Position += 1;

            lock (reader) // lock WzBinaryReader, allowing it to be loaded from multiple threads at once
            {
                if (len > 0)
                {
                    if (parseNow)
                    {
                        if (wzReader == null) // when saving the WZ file to a new encryption
                        {
                            compressedImageBytes = reader.ReadBytes(len);
                        }
                        else // when opening the Wz property
                        {
                            compressedImageBytes = wzReader.ReadBytes(len);
                        }
                        ParsePng(true);
                    }
                    else
                        reader.BaseStream.Position += len;
                }
            }
        }
        #endregion

        #region Parsing Methods
        /// <summary>
        /// How many compressed bytes this canvas has, without reading them.
        /// </summary>
        /// <remarks>
        /// Exists for callers that only need to know whether there is anything
        /// there -- an integrity audit asking "does this canvas have pixels?"
        /// across a whole client. Doing that through
        /// <see cref="GetCompressedBytes"/> reads and allocates every blob in
        /// the archive, which on a v232 client is tens of gigabytes of IO and a
        /// large-object-heap allocation per canvas, to look at one integer.
        ///
        /// Returns -1 when the length cannot be read at all -- a canvas whose
        /// bytes were set in memory rather than read from a file has no reader
        /// to ask, and a stored length of zero or less is what an archive
        /// decrypted with the wrong IV produces. Both are answers, so neither
        /// throws: a caller sweeping a million canvases should not be made to
        /// wrap each one in a try.
        /// </remarks>
        public int CompressedLength
        {
            get
            {
                lock (imageLock)
                {
                    if (compressedImageBytes != null)
                        return compressedImageBytes.Length;
                    if (storedLength >= 0)
                        return storedLength;
                    if (wzReader == null)
                        return -1;

                    // Only for a canvas whose length was not captured at parse
                    // time. Costs a seek, so it is the fallback and not the path.
                    lock (wzReader)
                    {
                        try
                        {
                            long pos = wzReader.BaseStream.Position;
                            wzReader.BaseStream.Position = offs;
                            int len = wzReader.ReadInt32() - 1;
                            wzReader.BaseStream.Position = pos;
                            return len < 0 ? -1 : len;
                        }
                        catch
                        {
                            return -1;
                        }
                    }
                }
            }
        }

        public byte[] GetCompressedBytes(bool saveInMemory)
        {
            lock (imageLock)
            {
                if (compressedImageBytes == null)
                {
                    lock (wzReader)// lock WzBinaryReader, allowing it to be loaded from multiple threads at once
                    {
                        long pos = this.wzReader.BaseStream.Position;
                        this.wzReader.BaseStream.Position = offs;
                        int len = this.wzReader.ReadInt32() - 1;
                        if (len <= 0) // possibility an image written with the wrong wzIv
                            throw new Exception("The length of the image is negative. WzPngProperty. Wrong WzIV?");

                        this.wzReader.BaseStream.Position += 1;

                        if (len > 0)
                            compressedImageBytes = this.wzReader.ReadBytes(len);
                        this.wzReader.BaseStream.Position = pos;
                    }

                    if (!saveInMemory)
                    {
                        //were removing the reference to compressedBytes, so a backup for the ret value is needed
                        byte[] returnBytes = compressedImageBytes;
                        compressedImageBytes = null;
                        return returnBytes;
                    }
                }
                return compressedImageBytes;
            }
        }

        /// <summary>
        /// Gets compressed bytes in standard zlib format, converting from listWz format if necessary.
        /// This is used for IMG filesystem extraction to ensure PNG data can be read without
        /// the original WZ encryption key.
        /// </summary>
        /// <param name="saveInMemory">Whether to cache the bytes</param>
        /// <returns>Compressed bytes in standard zlib format</returns>
        public byte[] GetCompressedBytesForExtraction(bool saveInMemory)
        {
            return ConvertListWzToStandardZlib(GetCompressedBytes(saveInMemory));
        }

        /// <summary>
        /// Picks the bytes to write into a saved archive for this canvas.
        ///
        /// The point of it is what it does *not* do. Until this existed,
        /// <c>WzCanvasProperty.WriteValue</c> called
        /// <see cref="GetCompressedBytesForExtraction"/> unconditionally, so
        /// every list.wz-encrypted canvas inside an edited image was decrypted,
        /// inflated into a buffer sized by <c>WzPngFormat.GetDecodedSize</c> and
        /// deflated again -- on a save the user made by changing one unrelated
        /// integer somewhere else in the same image. An untouched canvas has no
        /// business being re-derived: the round trip cannot improve the bytes,
        /// and it can silently ruin them, because GetDecodedSize's default arm is
        /// width * height * 4 and any canvas whose format code this build has no
        /// case for lands there. Measured on a 32x32 canvas with a 512-byte
        /// payload and an unrecognised format, the predicted size is 4096: the
        /// inflate stopped 3584 bytes short, those 3584 zero bytes were deflated
        /// back in as if they were pixels, and the archive still opened.
        ///
        /// The conversion is only ever *needed* when the destination archive is
        /// keyed differently from the source, because the listWz XOR layer is
        /// keyed by the archive's WzKey and would otherwise be unreadable where
        /// it lands. <see cref="WzMutableKey"/> compares by IV and AES user key,
        /// which is exactly that question.
        /// </summary>
        internal byte[] GetCompressedBytesForSaving(WzBinaryWriter writer)
        {
            byte[] rawBytes = GetCompressedBytes(false);
            if (rawBytes == null || rawBytes.Length < 2)
                return rawBytes;

            ushort header = (ushort)(rawBytes[0] | (rawBytes[1] << 8));
            if (IsStandardZlibHeader(header))
                return rawBytes; // already portable; nothing to re-derive

            WzMutableKey sourceKey = this.wzReader?.WzKey ?? ParentImage?.reader?.WzKey;
            if (sourceKey != null && writer?.WzKey != null && sourceKey.Equals(writer.WzKey))
                return rawBytes; // same key at both ends: the bytes travel as bytes

            return ConvertListWzToStandardZlib(rawBytes);
        }

        /// <summary>
        /// Rewrites a list.wz-encrypted payload as a plain zlib stream, so it can
        /// be read without the archive's key. Returns the input untouched when it
        /// is already plain zlib or when no key is available to decrypt it.
        /// </summary>
        private byte[] ConvertListWzToStandardZlib(byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length < 2)
                return rawBytes;

            // Check if this is listWz format (non-standard zlib header)
            ushort header = (ushort)(rawBytes[0] | (rawBytes[1] << 8));
            bool isListWzFormat = !IsStandardZlibHeader(header);

            if (!isListWzFormat)
                return rawBytes;

            // Convert listWz format to standard zlib format by XOR decrypting
            // and re-compressing the raw pixel data
            // Get the WzKey - prefer wzReader (set during parsing), fall back to ParentImage.reader
            var wzKey = this.wzReader?.WzKey ?? ParentImage?.reader?.WzKey;
            if (wzKey == null)
                return rawBytes; // Return as-is, may fail on read

            byte[] decryptedBytes = new byte[rawBytes.Length];
            int decryptedLength = DecryptListWzBlocks(rawBytes, wzKey, decryptedBytes);
            if (decryptedLength <= 2)
            {
                return rawBytes;
            }

            int uncompressedSize = GetUncompressedSize();
            byte[] decompressed = new byte[uncompressedSize];
            using (MemoryStream decryptedStream = new MemoryStream(decryptedBytes, 2, decryptedLength - 2, writable: false))
            using (DeflateStream deflate = new DeflateStream(decryptedStream, CompressionMode.Decompress))
            {
                // No try/catch around this any more. It used to swallow every
                // exception and hand back the original listWz bytes, which reads
                // as success to every caller and produced an extracted .img the
                // archive's key was still needed to open.
                ReadFully(deflate, decompressed,
                    $"a {Format} canvas of {width}x{height}");
            }

            using (MemoryStream outputStream = new MemoryStream(decompressed.Length))
            {
                outputStream.WriteByte(0x78);
                outputStream.WriteByte(0x9C);

                using (DeflateStream deflateOut = new DeflateStream(outputStream, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflateOut.Write(decompressed, 0, decompressed.Length);
                }

                return outputStream.ToArray();
            }
        }

        /// <summary>
        /// Gets the uncompressed size based on format and dimensions
        /// </summary>
        private int GetUncompressedSize()
        {
            return Format.GetDecodedSize(width, height);
        }

        /// <summary>
        /// How many bytes the blob actually inflates to, measured by inflating
        /// it rather than derived from <see cref="Format"/>.
        ///
        /// Everything else here sizes a buffer from the format and the
        /// dimensions and then fills it. That is the right thing when the
        /// format is trustworthy, and useless when the question is whether it
        /// is: a canvas whose format field is wrong inflates into a buffer of
        /// the wrong size and the lenient read hides it. This measures the
        /// payload independently, so a caller can ask which of two candidate
        /// formats the pixels are actually stored in and get an answer from the
        /// data instead of from the field under suspicion.
        ///
        /// Returns -1 when the answer cannot be measured — no bytes, no key for
        /// a listWz-encrypted blob, or a stream that will not inflate. That is
        /// a distinct answer from zero, and the caller must not treat it as a
        /// mismatch: "I could not look" is not "it does not match".
        /// </summary>
        public long InflatedLength()
        {
            byte[] raw;
            lock (imageLock)
            {
                if (compressedImageBytes == null && wzReader == null)
                    return -1;
            }

            try
            {
                raw = GetCompressedBytes(false);
            }
            catch
            {
                // A negative stored length is what an archive decrypted with the
                // wrong IV produces. It is an answer, and a sweep over a million
                // canvases should not be made to wrap each one in a try.
                return -1;
            }

            if (raw == null || raw.Length < 2)
                return -1;

            byte[] payload = raw;
            int length = raw.Length - 2;

            ushort header = (ushort)(raw[0] | (raw[1] << 8));
            if (!IsStandardZlibHeader(header))
            {
                WzMutableKey wzKey = this.wzReader?.WzKey ?? ParentImage?.reader?.WzKey;
                if (wzKey == null)
                    return -1;

                byte[] decrypted = new byte[raw.Length];
                int decryptedLength;
                try
                {
                    decryptedLength = DecryptListWzBlocks(raw, wzKey, decrypted);
                }
                catch (InvalidDataException)
                {
                    return -1;
                }
                if (decryptedLength <= 2)
                    return -1;

                payload = decrypted;
                length = decryptedLength - 2;
            }

            long total = 0;
            byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using MemoryStream stream = new(payload, 2, length, writable: false);
                using DeflateStream inflate = new(stream, CompressionMode.Decompress);
                int read;
                while ((read = inflate.Read(scratch, 0, scratch.Length)) > 0)
                    total += read;
            }
            catch (InvalidDataException)
            {
                return -1;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
            }

            return total;
        }

        public Bitmap GetImage(bool saveInMemory)
        {
            lock (imageLock)
            {
                if (png != null)
                {
                    if (saveInMemory)
                    {
                        return png;
                    }

                    return (Bitmap)png.Clone();
                }

                Bitmap decodedBitmap = DecodeBitmap(saveInMemory);
                if (saveInMemory)
                {
                    png = decodedBitmap;
                }

                return decodedBitmap;
            }
        }

        internal static byte[] Decompress(byte[] compressedBuffer, int decompressedSize)
        {
            using (MemoryStream memStream = new MemoryStream(compressedBuffer, 2, compressedBuffer.Length - 2, writable: false))
            using (DeflateStream zip = new DeflateStream(memStream, CompressionMode.Decompress))
            {
                byte[] buffer = new byte[decompressedSize];
                ReadFully(zip, buffer, $"a canvas payload of {decompressedSize} bytes");
                return buffer;
            }
        }

        internal static byte[] Compress(byte[] decompressedBuffer)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                memStream.WriteByte(0x78);
                memStream.WriteByte(0x9C);

                using (DeflateStream zip = new DeflateStream(memStream, CompressionMode.Compress, true))
                {
                    zip.Write(decompressedBuffer, 0, decompressedBuffer.Length);
                }

                return memStream.ToArray();
            }
        }

        public void ParsePng(bool saveInMemory, Texture2D texture2d = null)
        {
            lock (imageLock)
            {
                Bitmap decodedBitmap = DecodeBitmap(saveInMemory, texture2d);

                if (saveInMemory)
                {
                    png = decodedBitmap;
                }
                else if (decodedBitmap == null)
                {
                    png = null;
                }
            }
        }

        private Bitmap DecodeBitmap(bool saveInMemory, Texture2D texture2d = null)
        {
            byte[] rawBytes = GetRawImage(saveInMemory);
            if (rawBytes == null)
            {
                return null;
            }
            try
            {
                Bitmap bmp = new(width, height, Format.GetPixelFormat());
                Rectangle rect_ = new(0, 0, width, height);
                byte[] textureBytes = rawBytes;

                switch (Format)
                {
                    case WzPngFormat.Format1:
                        {
                            BitmapData bmpData = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                            PngUtility.DecompressImage_PixelDataBgra4444(rawBytes.AsSpan(), width, height, bmp, bmpData);
                            bmp.UnlockBits(bmpData);
                            break;
                        }
                    case WzPngFormat.Format2:
                        {
                            BitmapData bmpData = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                            Marshal.Copy(rawBytes, 0, bmpData.Scan0, rawBytes.Length);
                            bmp.UnlockBits(bmpData);
                            break;
                        }
                    case WzPngFormat.Format3:
                        {
                            // New format 黑白缩略图
                            // thank you Elem8100, http://forum.ragezone.com/f702/wz-png-format-decode-code-1114978/ 
                            // you'll be remembered forever <3 
                            BitmapData bmpData3 = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                            PngUtility.DecompressImageDXT3(rawBytes, width, height, bmpData3); // FullPath = "Map.wz\\Back\\blackHeaven.img\\back\\98"
                            bmp.UnlockBits(bmpData3);
                            break;
                        }
                    case WzPngFormat.Format257: // http://forum.ragezone.com/f702/wz-png-format-decode-code-1114978/index2.html#post9053713
                        {
                            BitmapData bmpData = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format16bppArgb1555);
                            // "Npc.wz\\2570101.img\\info\\illustration2\\face\\0"

                            PngUtility.CopyBmpDataWithStride(rawBytes, bmp.Width * 2, bmpData);

                            bmp.UnlockBits(bmpData);
                            break;
                        }
                    case WzPngFormat.Format513: // nexon wizet logo
                        {
                            BitmapData bmpData = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format16bppRgb565);

                            PngUtility.CopyBmpDataWithStride(rawBytes, bmp.Width * 2, bmpData);
                            bmp.UnlockBits(bmpData);
                            break;
                        }
                    case WzPngFormat.Format517:
                        {
                            BitmapData bmpData = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format16bppRgb565);

                            PngUtility.DecompressImage_PixelDataForm517(rawBytes, width, height, bmp, bmpData); // FullPath = "Map.wz\\Back\\midForest.img\\back\\0"
                            bmp.UnlockBits(bmpData);
                            break;
                        }
                    case WzPngFormat.Format1026:
                        {
                            BitmapData bmpData1026 = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                            PngUtility.DecompressImageDXT3(rawBytes, width, height, bmpData1026);
                            bmp.UnlockBits(bmpData1026);
                            break;
                        }
                    case WzPngFormat.Format2050: // new
                        {
                            BitmapData bmpData2050 = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            PngUtility.DecompressImageDXT5(rawBytes, width, height, bmpData2050);

                            bmp.UnlockBits(bmpData2050);
                            break;
                        }
                    case WzPngFormat.Format4098:
                        {
                            BitmapData bmpData4098 = bmp.LockBits(rect_, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            try
                            {
                                if (texture2d == null)
                                {
                                    PngUtility.DecompressImageBC7(rawBytes, width, height, bmpData4098);
                                }
                                else
                                {
                                    textureBytes = PngUtility.DecompressImageBC7(rawBytes, width, height);
                                    PngUtility.CopyBmpDataWithStride(textureBytes, width * 4, bmpData4098);
                                }
                            }
                            finally
                            {
                                bmp.UnlockBits(bmpData4098);
                            }
                            break;
                        }
                    default:
                        Helpers.ErrorLogger.Log(Helpers.ErrorLevel.MissingFeature, $"Unknown PNG format {Format}");
                        break;
                }
                if (bmp != null)
                {
                    if (texture2d != null)
                    {
                        Microsoft.Xna.Framework.Rectangle rect = new Microsoft.Xna.Framework.Rectangle(Microsoft.Xna.Framework.Point.Zero,
                            new Microsoft.Xna.Framework.Point(width, height));
                        texture2d.SetData(0, 0, rect, textureBytes, 0, textureBytes.Length);
                    }
                }

                return bmp;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses the raw image bytes from WZ
        /// </summary>
        /// <returns></returns>
        internal byte[] GetRawImage(bool saveInMemory)
        {
            byte[] rawImageBytes = GetCompressedBytes(saveInMemory);
            if (rawImageBytes == null || rawImageBytes.Length < 2)
            {
                return null;
            }

            ushort header = (ushort)(rawImageBytes[0] | (rawImageBytes[1] << 8));
            listWzUsed = !IsStandardZlibHeader(header);

            byte[] compressedBytes = rawImageBytes;
            int compressedLength = rawImageBytes.Length - 2;
            if (!listWzUsed)
            {
            }
            else
            {
                // Get the WzKey - prefer wzReader (set during parsing), fall back to ParentImage.reader
                var wzKey = this.wzReader?.WzKey ?? ParentImage?.reader?.WzKey;
                if (wzKey == null)
                {
                    throw new Exception("Cannot decrypt listWz format PNG - no WzKey available. " +
                        $"wzReader={this.wzReader != null}, ParentImage={ParentImage != null}");
                }

                byte[] decryptedBytes = new byte[rawImageBytes.Length];
                int decryptedLength = DecryptListWzBlocks(rawImageBytes, wzKey, decryptedBytes);
                if (decryptedLength <= 2)
                {
                    return null;
                }

                compressedBytes = decryptedBytes;
                compressedLength = decryptedLength - 2;
            }

                MemoryStream compressedStream = new MemoryStream(compressedBytes, 2, compressedLength, writable: false);
                DeflateStream zlib = new DeflateStream(compressedStream, CompressionMode.Decompress);
                int uncompressedSize = 0;
                byte[] decBuf = null;

                switch (Format)
                {
                    case WzPngFormat.Format1: // 0x1
                        {
                            uncompressedSize = width * height * 2;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format2: // 0x2
                        {
                            uncompressedSize = width * height * 4;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format3: // 0x2 + 1?
                        {
                            // New format 黑白缩略图
                            // thank you Elem8100, http://forum.ragezone.com/f702/wz-png-format-decode-code-1114978/ 
                            // you'll be remembered forever <3 

                            uncompressedSize = ((width + 3) / 4) * ((height + 3) / 4) * 16;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format257: // 0x100 + 1?
                        {
                            // http://forum.ragezone.com/f702/wz-png-format-decode-code-1114978/index2.html#post9053713
                            // "Npc.wz\\2570101.img\\info\\illustration2\\face\\0"

                            uncompressedSize = width * height * 2;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format513: // 0x200 nexon wizet logo
                        {
                            uncompressedSize = width * height * 2;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format517: // 0x200 + 5
                        {
                            uncompressedSize = width * height / 128;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format1026: // 0x400 + 2?
                        {
                            uncompressedSize = ((width + 3) / 4) * ((height + 3) / 4) * 16;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format2050: // 0x800 + 2? new
                        {
                            uncompressedSize = ((width + 3) / 4) * ((height + 3) / 4) * 16;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    case WzPngFormat.Format4098: // 0x1000 + 2, BC7
                        {
                            uncompressedSize = ((width + 3) / 4) * ((height + 3) / 4) * 16;
                            decBuf = new byte[uncompressedSize];
                            break;
                        }
                    default:
                        Helpers.ErrorLogger.Log(Helpers.ErrorLevel.MissingFeature, string.Format("Unknown PNG format {0}", Format));
                        break;
                }

                if (decBuf != null)
                {
                    using (zlib)
                    {
                        // https://learn.microsoft.com/en-us/dotnet/api/System.IO.Compression.DeflateStream.Read?view=net-8.0#system-io-compression-deflatestream-read(system-byte()-system-int32-system-int32)
                        // https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/partial-byte-reads-in-streams
                        // Deliberately lenient, unlike ReadFully. This is the
                        // display path: the result is handed to a Bitmap and
                        // never written back, so a short read costs a partly
                        // blank thumbnail and nothing else. Refusing to decode
                        // would turn images that render today into blanks, which
                        // is a worse trade for a path that cannot corrupt
                        // anything. The paths that re-encode what they read go
                        // through ReadFully and stop instead.
                        int totalRead = 0;
                        while (totalRead < decBuf.Length)
                        {
                            int bytesRead = zlib.Read(decBuf, totalRead, decBuf.Length - totalRead);
                            if (bytesRead == 0) break;
                            totalRead += bytesRead;
                        }
                        return decBuf;
                    }
                }
            return null;
        }

        private static bool IsStandardZlibHeader(ushort header)
        {
            return header == 0x9C78 || header == 0xDA78 || header == 0x0178 || header == 0x5E78;
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> completely or throws.
        ///
        /// It used to `break` on a short read and return, leaving the tail of the
        /// buffer as the zeros it was allocated with. That is harmless only if
        /// nobody looks at the tail -- and the caller that matters,
        /// <see cref="ConvertListWzToStandardZlib"/>, deflates the whole buffer
        /// straight back into the archive, so the zeros became pixel data. The
        /// buffer length comes from <c>WzPngFormat.GetDecodedSize</c>, whose
        /// default arm is width * height * 4, so any unrecognised format code
        /// over-allocates by however much it likes: 4096 against a real 512-byte
        /// payload on the 32x32 case in the tests. A short read here means the
        /// size formula and the stored data disagree, and there is no honest
        /// recovery from that -- only a loud stop before the wrong bytes are
        /// written down.
        /// </summary>
        private static void ReadFully(Stream stream, byte[] buffer, string what)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (bytesRead == 0)
                {
                    throw new InvalidDataException(
                        $"Expected {buffer.Length} bytes for {what} but the stored data ran out after " +
                        $"{totalRead}. Continuing would treat the {buffer.Length - totalRead} missing " +
                        "bytes as zero-valued pixels and write them back into the archive.");
                }
                totalRead += bytesRead;
            }
        }

        private static int DecryptListWzBlocks(byte[] source, WzMutableKey wzKey, byte[] destination)
        {
            int sourceOffset = 0;
            int destinationOffset = 0;
            while (sourceOffset < source.Length)
            {
                if (source.Length - sourceOffset < sizeof(int))
                {
                    throw new InvalidDataException("Invalid listWz PNG block header.");
                }

                int blockSize = BitConverter.ToInt32(source, sourceOffset);
                sourceOffset += sizeof(int);

                if (blockSize < 0 || blockSize > source.Length - sourceOffset)
                {
                    throw new InvalidDataException("Invalid listWz PNG block size.");
                }

                wzKey.EnsureKeySize(blockSize);
                for (int i = 0; i < blockSize; i++)
                {
                    destination[destinationOffset + i] = (byte)(source[sourceOffset + i] ^ wzKey[i]);
                }

                sourceOffset += blockSize;
                destinationOffset += blockSize;
            }

            return destinationOffset;
        }

        internal void CompressPng(Bitmap bmp)
        {
            width = bmp.Width;
            height = bmp.Height;

            // Lock the bitmap to access pixel data directly, improving performance over GetPixel
            BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels;
            try
            {
                int expectedSize = bmp.Width * bmp.Height * 4;
                pixels = new byte[expectedSize];

                // Handle stride properly - copy row by row if stride doesn't match expected width
                if (bmpData.Stride == bmp.Width * 4)
                {
                    // No padding, direct copy
                    Marshal.Copy(bmpData.Scan0, pixels, 0, expectedSize);
                }
                else
                {
                    // Copy row by row to remove stride padding
                    int rowSize = bmp.Width * 4;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        IntPtr rowPtr = bmpData.Scan0 + y * bmpData.Stride;
                        Marshal.Copy(rowPtr, pixels, y * rowSize, rowSize);
                    }
                }
            }
            finally
            {
                // Ensure the bitmap is unlocked even if an exception occurs
                bmp.UnlockBits(bmpData);
            }

            ////// Automatically detect the suitable format for each image. See UnitTest_WzFile/UnitTest_MapleLib.cs/TestImageSurfaceFormatDetection
            SurfaceFormat suggested_surfaceFormat = ImageFormatDetector.DetermineTextureFormat(pixels, bmp.Width, bmp.Height);
            //Debug.WriteLine(string.Format("Suggested SurfaceFormat: {0}", suggested_surfaceFormat.ToString()));

            ////// Optimise the image size
            // Create an EncoderParameters object to specify the PNG encoder and the desired compression level
            //EncoderParameters encoderParameters = new(1);
            //encoderParameters.Param[0] = new EncoderParameter(Encoder.Compression, (byte) CompressionLevel.Optimal);
            // Get the PNG codec information
            //ImageCodecInfo pngCodec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Png.Guid);
            // Save the compressed image
            /*Bitmap newBitmap;
            using (MemoryStream stream = new MemoryStream())
            {
                bmp.Save(stream, pngCodec, encoderParameters);

                newBitmap = new Bitmap(stream);
            }*/

            (WzPngFormat format, byte[] compressedBytes) = PngUtility.CompressImageToPngFormat(bmp, suggested_surfaceFormat);

            this.Format = format;
            this.compressedImageBytes = Compress(compressedBytes);

            if (listWzUsed)
            {
                using (MemoryStream memStream = new MemoryStream())
                {
                    using (WzBinaryWriter writer = new WzBinaryWriter(memStream, WzTool.GetIvByMapleVersion(WzMapleVersion.GMS)))
                    {
                        writer.Write(2);
                        for (int i = 0; i < 2; i++)
                        {
                            writer.Write((byte)(compressedImageBytes[i] ^ writer.WzKey[i]));
                        }
                        writer.Write(compressedImageBytes.Length - 2);
                        for (int i = 2; i < compressedImageBytes.Length; i++)
                            writer.Write((byte)(compressedImageBytes[i] ^ writer.WzKey[i - 2]));
                        compressedImageBytes = memStream.ToArray();
                    }
                }
            }
        }
        #endregion

        #region Cast Values

        public override Bitmap GetBitmap()
        {
            return GetImage(false);
        }
        #endregion
    }
}
