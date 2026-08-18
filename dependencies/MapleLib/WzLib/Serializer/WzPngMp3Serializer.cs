using MapleLib.WzLib.WzProperties;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapleLib.WzLib.Serializer
{
    /// <summary>
    /// Serialiser for PNG and audio files
    /// </summary>
    public class WzPngMp3Serializer : ProgressingWzSerializer, IWzImageSerializer, IWzObjectSerializer
    {
        //List<WzImage> imagesToUnparse = new List<WzImage>();
        private string outPath;

        public void SerializeObject(WzObject obj, string outPath)
        {
            //imagesToUnparse.Clear();
            total = 0; curr = 0;
            this.outPath = outPath;
            if (!Directory.Exists(outPath))
            {
                CreateDirSafe(ref outPath);
            }

            if (outPath.Substring(outPath.Length - 1, 1) != @"\")
                outPath += @"\";

            total = CalculateTotal(obj);

            // One guard per export. It is what stops a subtree that contains itself
            // -- AddProperty does no ancestry check, and a port clones subtrees
            // between archives -- from taking the process down mid-dump.
            ExportRecursion(obj, outPath, new WzWalk(), 0);
            /*foreach (WzImage img in imagesToUnparse)
                img.UnparseImage();
            imagesToUnparse.Clear();*/
        }

        public void SerializeFile(WzFile file, string path)
        {
            SerializeObject(file, path);
        }

        public void SerializeDirectory(WzDirectory file, string path)
        {
            SerializeObject(file, path);
        }

        public void SerializeImage(WzImage file, string path)
        {
            SerializeObject(file, path);
        }

        private int CalculateTotal(WzObject currObj)
        {
            int result = 0;
            if (currObj is WzFile file)
            {
                result += file.WzDirectory.CountImages();
            }
            else if (currObj is WzDirectory directory)
            {
                result += directory.CountImages();
            }
            return result;
        }

        /// <summary>
        /// Writes one node, then whatever <paramref name="walk"/> lets it descend into.
        ///
        /// The three rules are <see cref="WzWalk"/>'s, and they apply here for the
        /// same reason they apply to every other walk in this library: a WZ image is
        /// only a tree until a UOL is in it. What is specific to a dumper is which
        /// side of the guard each node falls on.
        ///
        /// LEAVES -- a canvas, a sound, and the picture a link points at -- are
        /// written without consulting the visited set. They cannot recurse, and a
        /// canvas that is legitimately reached twice (once at its own path, once
        /// through a link naming it) has to produce a file at BOTH, or the dump is
        /// missing a frame.
        ///
        /// CONTAINERS -- files, directories, images and sub-properties -- go through
        /// <c>Enter</c>: once each, never past the depth cap, and never into a link.
        /// A link's children belong to the node it points at and are dumped there,
        /// under the path they actually live at.
        /// </summary>
        private void ExportRecursion(WzObject currObj, string outPath, WzWalk walk, int depth)
        {
            // Leaves first, and a link is one of them: it is exported as the picture
            // it points at, under ITS OWN name. Writing the target's name instead
            // meant a frame named "1" linking to "0" produced no 1.png and a second,
            // identical 0.png -- a dump silently missing a frame, which is the one
            // failure a dumper must not have.
            if (currObj is WzCanvasProperty canvasProperty)
            {
                ExportCanvas(canvasProperty, outPath, currObj.Name);
                //curr++;
                return;
            }

            if (currObj is WzBinaryProperty binProperty)
            {
                string fileName = EscapeInvalidFilePathNames(currObj.Name);
                if (!fileName.EndsWith(binProperty.FileExtension, StringComparison.OrdinalIgnoreCase))
                    fileName += binProperty.FileExtension;
                string path = outPath + fileName;

                binProperty.SaveToFile(path);
                return;
            }

            if (currObj is WzUOLProperty uolProperty)
            {
                if (uolProperty.LinkValue is WzCanvasProperty linkedCanvas)
                {
                    ExportCanvas(linkedCanvas, outPath, uolProperty.Name);
                }
                return;
            }

            // Everything below here descends, so everything below here is guarded.
            if (!walk.Enter(currObj, depth))
                return;

            if (currObj is WzFile wzFile)
            {
                ExportRecursion(wzFile.WzDirectory, outPath, walk, depth + 1);
            }
            else if (currObj is WzDirectory directoryProperty)
            {
                outPath += EscapeInvalidFilePathNames(currObj.Name) + @"\";
                if (!Directory.Exists(outPath))
                    Directory.CreateDirectory(outPath);

                foreach (WzDirectory subdir in directoryProperty.WzDirectories)
                {
                    ExportRecursion(subdir, outPath + subdir.Name + @"\", walk, depth + 1);
                }
                foreach (WzImage subimg in directoryProperty.WzImages)
                {
                    ExportRecursion(subimg, outPath + subimg.Name + @"\", walk, depth + 1);
                }
            }
            else if (currObj is WzImage wzImage)
            {
                outPath += EscapeInvalidFilePathNames(currObj.Name) + @"\";
                if (!Directory.Exists(outPath))

                    Directory.CreateDirectory(outPath);

                bool parse = wzImage.Parsed || wzImage.Changed;
                if (!parse)
                {
                    wzImage.ParseImage();
                }
                foreach (WzImageProperty subprop in wzImage.WzProperties)
                {
                    ExportRecursion(subprop, outPath, walk, depth + 1);
                }
                if (!parse)
                {
                    wzImage.UnparseImage();
                }
                curr++;
            }
            else if (currObj is IPropertyContainer container)
            {
                outPath += EscapeInvalidFilePathNames(currObj.Name) + ".";

                foreach (WzImageProperty subprop in container.WzProperties)
                {
                    ExportRecursion(subprop, outPath, walk, depth + 1);
                }
            }
        }

        /// <summary>
        /// One PNG, named by the node that ASKED for it rather than by the node the
        /// pixels came from.
        /// </summary>
        private void ExportCanvas(WzCanvasProperty canvas, string outPath, string name)
        {
            Bitmap bmp = canvas.GetLinkedWzCanvasBitmap();
            string path = outPath + EscapeInvalidFilePathNames(name) + ".png";
            bmp.Save(path);
        }
    }
}
