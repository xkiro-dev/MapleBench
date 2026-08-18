using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MapleLib.Configuration;
using MapleLib.MapleCryptoLib;
using MapleLib.PacketLib;

namespace MapleLib.WzLib.Util
{
    public class WzTool
    {
        public const int WZ_HEADER = 0x31474B50; // "PKG1" as int representation


        public static UInt32 RotateLeft(UInt32 x, byte n)
        {
            return (UInt32)(((x) << (n)) | ((x) >> (32 - (n))));
        }

        public static UInt32 RotateRight(UInt32 x, byte n)
        {
            return (UInt32)(((x) >> (n)) | ((x) << (32 - (n))));
        }

        public static int GetCompressedIntLength(int i)
        {
            if (i > 127 || i < -127)
                return 5;
            return 1;
        }

        public static int GetEncodedStringLength(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 1;

            bool unicode = false;
            int length = s.Length;

            // This threshold must match WzBinaryWriter.Write(string) and is
            // meaningless on its own -- see the long comment there for which way
            // the pair has moved and why. What matters here is only that it says
            // the same thing the writer says, for every character.
            //
            // WzDirectory.GenerateDataFile sizes the directory block from this
            // function, so a disagreement makes the block come out a different
            // length than was reserved for it and every img.Offset after it point
            // to the wrong place, with image data overlapping the directory
            // table. Nothing catches it: the archive still parses, because
            // ParseWzFile reads the table sequentially, so verify-then-swap
            // accepts the file and swaps it over the client's. It read
            // `c > 255` while the writer read `c > sbyte.MaxValue` until
            // 2026-08-05; both read `c > 255` again from 2026-08-06, when the
            // reader's single-byte decode moved to Latin-1.
            //
            // WzSaveIntegrityReviewTests.TheReservedStringLength_MatchesWhatThe
            // WriterEmits_ForEveryShapeOfString is the guard on the pair.
            foreach (char c in s) {
                if (c > byte.MaxValue) {
                    unicode = true;
                    break;
                }
            }
            int prefixLength = length > (unicode ? 126 : 127) ? 5 : 1;
            int encodedLength = unicode ? length * 2 : length;

            return prefixLength + encodedLength;
        }

        /// <summary>
        /// Predicts the number of bytes <c>WzBinaryWriter.WriteWzObjectValue</c> will
        /// emit for a directory-entry name, mirroring that writer's dedup rule: a name
        /// longer than four characters that has already been emitted in this pass is
        /// written as a five-byte back-reference instead of inline.
        /// </summary>
        /// <param name="stringCache">
        /// The names already seen in this prediction pass. It must be scoped to a
        /// single <c>WzFile.SaveToDisk</c> call and must start empty, because it is
        /// the mirror of the fresh per-writer cache the write pass uses -- the two
        /// have to make the same inline-or-reference decision for every name or the
        /// directory block comes out a different size than was reserved for it and
        /// every offset in it is wrong.
        ///
        /// This used to be a process-global <c>static Hashtable</c> on this class.
        /// Two problems, one live and one latent: entries survived a save that threw,
        /// so the next attempt predicted a back-reference for a name the writer then
        /// emitted in full (the user saw "the rewritten archive could not be read
        /// back" on every retry until they restarted the process); and two concurrent
        /// saves of different archives shared one cache and would have corrupted each
        /// other's predictions. Passing the cache in makes both impossible by
        /// construction rather than by discipline.
        /// </param>
        public static int GetWzObjectValueLength(string s, byte type, ISet<string> stringCache)
        {
            string storeName = type + "_" + s;
            if (s.Length > 4 && stringCache.Contains(storeName))
            {
                return 5;
            }
            else
            {
                stringCache.Add(storeName);
                return 1 + GetEncodedStringLength(s);
            }
        }

        /// <summary>
        /// Get WZ encryption IV from maple version 
        /// </summary>
        /// <param name="ver"></param>
        /// <param name="fallbackCustomIv">The custom bytes to use as IV</param>
        /// <returns></returns>
        public static byte[] GetIvByMapleVersion(WzMapleVersion ver)
        {
            switch (ver)
            {
                case WzMapleVersion.EMS:
                    return WzAESConstant.WZ_MSEAIV;//?
                case WzMapleVersion.GMS:
                    return WzAESConstant.WZ_GMSIV;
                case WzMapleVersion.CUSTOM: // custom WZ encryption bytes from stored app setting
                    {
                        ConfigurationManager config = new ConfigurationManager();
                        return config.GetCusomWzIVEncryption(); // fallback with BMS
                    }
                case WzMapleVersion.GENERATE: // dont fill anything with GENERATE, it is not supposed to load anything
                    return new byte[4]; 

                case WzMapleVersion.BMS:
                case WzMapleVersion.CLASSIC:
                default:
                    return WzAESConstant.WZ_BMSCLASSIC;
            }
        }

        private static int GetRecognizedCharacters(string source) {
            return source.Count(c => c >= 0x20 && c <= 0x7E);
        }

        /// <summary>
        /// Attempts to bruteforce the WzKey with a given WZ file
        /// </summary>
        /// <param name="wzPath"></param>
        /// <param name="wzIvKey"></param>
        /// <returns>The probability. Normalized to 100</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBruteforcingWzIVKey(string wzPath, byte[] wzIvKey)
        {
            using (WzFile wzf = new WzFile(wzPath, wzIvKey))
            {
                string parseErrorMessage = string.Empty;
                WzFileParseStatus parseStatus = wzf.ParseMainWzDirectory(true);
                if (parseStatus != WzFileParseStatus.Success)
                {
                    wzf.Dispose();
                    return false;
                }
                if (wzf.WzDirectory.WzImages.Count > 0)
                {
                    string wzDirName = wzf.WzDirectory.WzImages[0].Name;
                    if (wzDirName.EndsWith(".img"))
                    {
                        wzf.Dispose();
                        return true;
                    }
                }
                wzf.Dispose();
            }
            return false;
        }

        private static double GetDecryptionSuccessRate(string wzPath, WzMapleVersion encVersion, ref short? version)
        {
            WzFile wzf;
            if (version == null)
                wzf = new WzFile(wzPath, encVersion);
            else
                wzf = new WzFile(wzPath, (short)version, encVersion);

            // try/finally rather than a Dispose() on each exit: the early return below
            // used to leak. Two of the three probes DetectMapleVersion runs fail by
            // design, so every archive opened leaked two FileStreams and two parsed
            // directory trees -- and SaveGuards probes with FileShare.None, so the
            // editor went on to report the user's own archive as "open in another
            // program" with no other program running.
            try
            {
                WzFileParseStatus parseStatus = wzf.ParseWzFile();
                if (parseStatus != WzFileParseStatus.Success)
                {
                    return 0.0d;
                }

                if (version == null) version = wzf.Version;
                int recognizedChars = 0;
                int totalChars = 0;
                foreach (WzDirectory wzdir in wzf.WzDirectory.WzDirectories)
                {
                    recognizedChars += GetRecognizedCharacters(wzdir.Name);
                    totalChars += wzdir.Name.Length;
                }
                foreach (WzImage wzimg in wzf.WzDirectory.WzImages)
                {
                    recognizedChars += GetRecognizedCharacters(wzimg.Name);
                    totalChars += wzimg.Name.Length;
                }
                if (totalChars == 0)
                    return 0.0d;
                return (double)recognizedChars / (double)totalChars;
            }
            finally
            {
                wzf.Dispose();
            }
        }

        public static WzMapleVersion DetectMapleVersion(string wzFilePath, out short fileVersion)
        {
            Hashtable mapleVersionSuccessRates = new Hashtable();
            short? version = null;
            mapleVersionSuccessRates.Add(WzMapleVersion.GMS, GetDecryptionSuccessRate(wzFilePath, WzMapleVersion.GMS, ref version));
            mapleVersionSuccessRates.Add(WzMapleVersion.EMS, GetDecryptionSuccessRate(wzFilePath, WzMapleVersion.EMS, ref version));
            mapleVersionSuccessRates.Add(WzMapleVersion.BMS, GetDecryptionSuccessRate(wzFilePath, WzMapleVersion.BMS, ref version));
            // Every probe failing leaves version null. Unwrapping it threw
            // "Nullable object must have a value", which is what the user was shown
            // for a client whose encryption we simply do not handle. Report 0 and let
            // the caller fail with something that names the actual problem.
            fileVersion = version ?? 0;
            WzMapleVersion mostSuitableVersion = WzMapleVersion.GMS;
            double maxSuccessRate = 0;

            foreach (DictionaryEntry mapleVersionEntry in mapleVersionSuccessRates)
            {
                if ((double)mapleVersionEntry.Value > maxSuccessRate)
                {
                    mostSuitableVersion = (WzMapleVersion)mapleVersionEntry.Key;
                    maxSuccessRate = (double)mapleVersionEntry.Value;
                }
            }
            if (maxSuccessRate < 0.7 && File.Exists(Path.Combine(Path.GetDirectoryName(wzFilePath), "ZLZ.dll")))
                return WzMapleVersion.GETFROMZLZ;
            else return mostSuitableVersion;
        }

        public static bool IsListFile(string path)
        {
            bool result;
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                int header = reader.ReadInt32();
                result = header != WZ_HEADER;
            }
            return result;
        }

        /// <summary>
        /// Checks if the input file is Data.wz hotfix file [not to be mistaken for Data.wz for pre v4x!]
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static bool IsDataWzHotfixFile(string path)
        {
            bool result = false;
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                byte firstByte = reader.ReadByte();

                result = firstByte == WzImage.WzImageHeaderByte_WithoutOffset; // check the first byte. It should be 0x73 that represends a WzImage
            }

            return result;
        }

        private static byte[] Combine(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }
}