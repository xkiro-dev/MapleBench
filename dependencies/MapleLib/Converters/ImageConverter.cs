using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MapleLib.Converters
{
    /// <summary>
    /// Bitmap conversions used by the canvas path.
    ///
    /// This class used to also carry ToWpfBitmap/ToWinFormsBitmap, converting
    /// between System.Drawing.Bitmap and WPF's BitmapSource. They were the only
    /// reason MapleLib referenced WPF at all, which cost 32 MB of
    /// Presentation*/WindowsBase/System.Xaml in every build. Nothing in this
    /// repo -- MapleLib, MapleBench, the tests or the benchmarks -- ever called
    /// either one, so they were removed rather than isolated. If a WPF host
    /// ever needs them back, convert through the PNG bytes this class already
    /// produces instead of taking the framework reference again.
    /// </summary>
    public static class ImageConverter
    {
        #region Texture2D
        /// <summary>
        ///  Converts Microsoft.Xna.Framework.Graphics.Texture2D to PNG MemoryStream
        /// </summary>
        /// <param name="texture2D"></param>
        /// <returns></returns>
        public static MemoryStream Texture2DToPng(this Texture2D texture2D)
        {
            MemoryStream memoryStream = new MemoryStream();
            texture2D.SaveAsPng(memoryStream, texture2D.Width, texture2D.Height);
            memoryStream.Seek(0, SeekOrigin.Begin);

            return memoryStream;
        }

        /// <summary>
        /// Converts Microsoft.Xna.Framework.Graphics.Texture2D to JPG MemoryStream
        /// </summary>
        /// <param name="texture2D"></param>
        /// <returns></returns>
        public static MemoryStream Texture2DToJpg(this Texture2D texture2D)
        {
            MemoryStream memoryStream = new MemoryStream();
            texture2D.SaveAsJpeg(memoryStream, texture2D.Width, texture2D.Height);
            memoryStream.Seek(0, SeekOrigin.Begin);

            return memoryStream;
        }
        #endregion

        /// <summary>
        /// System.Drawing.Bitmap to System.Drawing.Image
        /// </summary>
        /// <param name="bitmap"></param>
        /// <returns></returns>
        public static System.Drawing.Image ToImage(this System.Drawing.Bitmap bitmap)
        {
            return (System.Drawing.Image)bitmap;
        }

        /// <summary>
        /// Converts System.Drawing.Bitmap to byte[]
        /// </summary>
        /// <param name="bitmap"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] BitmapToBytes(this System.Drawing.Bitmap bitmap)
        {
            BitmapData bmpdata = null;
            try
            {
                bmpdata = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
                int numbytes = bmpdata.Stride * bitmap.Height;
                byte[] bytedata = new byte[numbytes];
                IntPtr ptr = bmpdata.Scan0;

                Marshal.Copy(ptr, bytedata, 0, numbytes);

                return bytedata;
            }
            finally
            {
                if (bmpdata != null)
                    bitmap.UnlockBits(bmpdata);
            }
        }

        /// <summary>
        /// Gets the image format from a Bitmap object.
        /// </summary>
        /// <param name="bitmap"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static ImageFormat GetImageFormat(Bitmap bitmap) {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            PixelFormat pixelFormat = bitmap.PixelFormat;

            switch (pixelFormat) {
                case PixelFormat.Format1bppIndexed:
                case PixelFormat.Format4bppIndexed:
                case PixelFormat.Format8bppIndexed:
                    return ImageFormat.Bmp;

                case PixelFormat.Format16bppGrayScale:
                case PixelFormat.Format16bppRgb555:
                case PixelFormat.Format16bppRgb565:
                case PixelFormat.Format32bppRgb:
                case PixelFormat.Format32bppArgb:
                case PixelFormat.Format32bppPArgb:
                case PixelFormat.Format48bppRgb:
                case PixelFormat.Format64bppArgb:
                case PixelFormat.Format64bppPArgb:
                case PixelFormat.Format24bppRgb:
                    return ImageFormat.Png;

                default:
                    return ImageFormat.Jpeg;
            }
        }
    }
}
