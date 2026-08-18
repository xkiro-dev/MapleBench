using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MapleLib.Helpers;
using MapleLib.MapleCryptoLib;
using MapleLib.WzLib.WzStructure.Enums;

namespace MapleLib.WzLib.Util
{
    /// <summary>
    ///  TODO : Maybe WzBinaryReader/Writer should read and contain the hash (this is probably what's going to happen)
    /// </summary>
    public class WzBinaryWriter : BinaryWriter
    {
        #region Properties
        public WzMutableKey WzKey { get; set; }
        public uint Hash { get; set; }
        public Dictionary<string, int> StringCache { get; set; }
        public WzHeader Header { get; set; }
        public bool LeaveOpen { get; internal set; }

        /// <summary>
        /// True when the archive being written has no 2-byte encryption version
        /// header — the shape every post-KMST1132 client uses.
        ///
        /// It exists for exactly one reason: the directory-entry name
        /// back-reference (<see cref="WzDirectoryType.RetrieveStringFromOffset_2"/>)
        /// is stored one byte lower in a 64-bit archive than in a 32-bit one.
        /// <c>WzDirectory.ParseDirectory</c> has always compensated on the way in
        /// ("extraOffset = Is64BitWzFile ? 1 : 0"), and nothing compensated on the
        /// way out, so a 64-bit archive written by this library and read back by
        /// it resolved every repeated name one byte late.
        ///
        /// Measured, on the real clients on this machine: the Steam depot's
        /// Character.wz is 64-bit, holds 45,399 images of which 3 share a name
        /// with another, and every one of those names reads back correctly with
        /// the +1 in place — so the reader is right about the real format and the
        /// writer was wrong. Converting Data\Quest into one Quest.wz is what
        /// surfaced it: 24,351 of 24,352 names round-tripped and
        /// "_Canvas\QuestCategory.img" — the one name repeated from the root
        /// directory, hence the one written as a back-reference — came back as
        /// "tbrwB".
        ///
        /// The same fault applies to any 64-bit archive saved through
        /// <c>WzFile.SaveToDisk</c>, not only to imports.
        /// </summary>
        public bool Is64BitWzFile { get; set; }
        #endregion

        #region Constructors
        public WzBinaryWriter(Stream output, byte[] WzIv)
            : this(output, WzIv, false)
        {
            this.Hash = 0;
        }

        public WzBinaryWriter(Stream output, byte[] WzIv, uint Hash) : this(output, WzIv, false)
        {
            this.Hash = Hash;
        }

        public WzBinaryWriter(Stream output, byte[] WzIv, bool leaveOpen)
            : base(output)
        {
            WzKey = WzKeyGenerator.GenerateWzKey(WzIv);
            StringCache = [];
            this.LeaveOpen = leaveOpen;
        }
        #endregion

        #region Methods
        /// <summary>
        /// ?InternalSerializeString@@YAHPAGPAUIWzArchive@@EE@Z
        /// </summary>
        /// <param name="s"></param>
        /// <param name="withoutOffset">bExistID_0x73   0x73</param>
        /// <param name="withOffset">bNewID_0x1b  0x1B</param>
        public void WriteStringValue(string str, int withoutOffset, int withOffset)
        {
            // if length is > 4 and the string cache contains the string
            // writes the offset instead
            if (str.Length > 4 && StringCache.ContainsKey(str))
            {
                Write((byte)withOffset);
                Write((int)StringCache[str]);
            }
            else
            {
                Write((byte)withoutOffset);
                int sOffset = (int)this.BaseStream.Position;
                Write(str);
                if (!StringCache.ContainsKey(str))
                {
                    StringCache[str] = sOffset;
                }
            }
        }

        /// <summary>
        /// Writes the Wz object value
        /// </summary>
        /// <param name="stringObjectValue"></param>
        /// <param name="type"></param>
        /// <param name="unk_GMS230"></param>
        /// <returns>true if the Wz object value is written as an offset in the Wz file, else if not</returns>
        public bool WriteWzObjectValue(string stringObjectValue, WzDirectoryType type)
        {
            string storeName = string.Format("{0}_{1}", (byte)type, stringObjectValue);

            // if length is > 4 and the string cache contains the string
            // writes the offset instead
            if (stringObjectValue.Length > 4 && StringCache.ContainsKey(storeName))
            {
                Write((byte)WzDirectoryType.RetrieveStringFromOffset_2); // 2
                Write((int)StringCache[storeName]);

                return true;
            }
            else
            {
                // Minus one for a 64-bit archive, mirroring the "+ extraOffset" in
                // WzDirectory.ParseDirectory. See Is64BitWzFile for the measurement
                // that establishes which side of the pair was wrong.
                int sOffset = (int)(this.BaseStream.Position - Header.FStart) - (Is64BitWzFile ? 1 : 0);
                Write((byte)type);
                Write(stringObjectValue);
                if (!StringCache.ContainsKey(storeName))
                {
                    StringCache[storeName] = sOffset;
                }
            }
            return false;
        }

        public override void Write(string value)
        {
            if (value.Length == 0)
            {
                Write((byte)0);
                return;
            }
            // A character that still fits in one byte stays in the one-byte form.
            //
            // This threshold and the one in WzTool.GetEncodedStringLength are a
            // pair and must always be changed together: the first predicts how
            // many bytes a directory entry name will take and the second emits
            // them, and when they disagree the directory block is written a
            // different size than was reserved for it, every offset after it is
            // wrong, and the archive still parses -- so verify-then-swap accepts
            // it. They were last brought back into agreement on 2026-08-05.
            //
            // The value moved from sbyte.MaxValue to byte.MaxValue on 2026-08-06,
            // with WzBinaryReader's single-byte decode moving from ASCII to
            // Latin-1 in the same change, so that the pair is now byte-exact:
            // WriteAsciiString below already stores `(byte)c`, so a name read as
            // one byte per character is written back as the same bytes. Under
            // ASCII the reader turned every byte above 0x7F into '?' -- and names
            // are rewritten from memory on every save, including for images whose
            // bodies are copied through untouched, so opening such an archive and
            // saving it renamed everything in it permanently, with the inventory
            // check comparing the mangled names against themselves and finding
            // nothing wrong.
            //
            // 255 rather than 127 is what the client's own files do, counted
            // rather than reasoned: of the Steam depot's archives on this
            // machine, Character.wz holds 136,737 one-byte strings containing a
            // byte above 0x7F (out of 31,408,561), Etc.wz 6,385, Skill.wz 2,340
            // and String.wz 402 -- "Weapon-Armor Shop" with a 0xB7 middle dot is
            // one of them. Of the 6,032 two-byte strings across those four
            // archives, not one had every character inside 0x00-0xFF. So Nexon
            // splits the two forms at 0xFF, and a name holding a Latin-1
            // character belongs in the one-byte form.
            //
            // This is not merely cosmetic. It is what makes reading and writing
            // inverse: at 127 a name that arrived as one byte per character went
            // back out as two, so an archive saved with no edits was not the
            // archive that was opened.
            bool unicode = value.Any(c => c > byte.MaxValue);

            if (unicode)
            {
                WriteUnicodeString(value);
            }
            else // ASCII
            {
                WriteAsciiString(value);
            }
        }

        /// <summary>
        /// Encodes unicode string
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteUnicodeString(string value)
        {
            if (value.Length >= sbyte.MaxValue) // Bugfix - >= because if value.Length = MaxValue, MaxValue will be written and then treated as a long-length marker
            {
                Write(sbyte.MaxValue);
                Write(value.Length);
            }
            else
            {
                Write((sbyte)value.Length);
            }

            ushort mask = 0xAAAA;

            int i = 0;
            foreach (var character in value)
            {
                ushort encryptedChar = (ushort)character;
                encryptedChar ^= (ushort)((WzKey[i * 2 + 1] << 8) + WzKey[i * 2]);
                encryptedChar ^= mask;
                mask++;
                Write(encryptedChar);

                i++;
            }
        }

        /// <summary>
        /// Encodes ASCII string
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteAsciiString(string value)
        {
            if (value.Length > sbyte.MaxValue) // Note - no need for >= here because of 2's complement (MinValue == -(MaxValue + 1))
            {
                Write(sbyte.MinValue);
                Write(value.Length);
            }
            else
            {
                Write((sbyte)(-value.Length));
            }

            byte mask = 0xAA;

            int i = 0;
            foreach (char c in value)
            {
                byte encryptedChar = (byte)c;
                encryptedChar ^= WzKey[i];
                encryptedChar ^= mask;
                mask++;
                Write(encryptedChar);

                i++;
            }
        }

        public char[] EncryptString(string stringToEncrypt)
        {
           return stringToEncrypt.Select((c, i) => (char)(c ^ ((WzKey[i * 2 + 1] << 8) + WzKey[i * 2]))).ToArray();
        }

        public char[] EncryptNonUnicodeString(string stringToEncrypt)
        {
            return stringToEncrypt.Select((c, i) => (char)(c ^ WzKey[i])).ToArray();
        }

        /// <summary>
        /// Writes a raw, unencrypted, NUL-terminated byte string. Its only caller
        /// is the WZ header copyright.
        ///
        /// One byte per character, via Latin-1, and that is load-bearing twice
        /// over. <c>WzFile.ParseMainWzDirectory</c> reads this field back as a
        /// fixed count of Header.FStart - 17 bytes, so the count has to be the
        /// character count; and Header.FStart is the origin every offset in the
        /// archive is measured from, so a copyright that encodes longer than it
        /// measures pushes the header past its own declared start. This used to
        /// go out through BinaryWriter's default UTF-8, where a single 0xA9
        /// becomes two bytes -- the padding calculation that follows it in
        /// SaveToDisk only adds bytes and cannot take them away, so the file was
        /// written one byte long and the version header landed inside what the
        /// reader treats as header padding.
        /// </summary>
        public void WriteNullTerminatedString(string value)
        {
            Write(System.Text.Encoding.Latin1.GetBytes(value));
            Write((byte)0);
        }

        public void WriteCompressedInt(int value)
        {
            if (value > sbyte.MaxValue || value <= sbyte.MinValue)
            {
                Write(sbyte.MinValue);
                Write(value);
            }
            else
            {
                Write((sbyte)value);
            }
        }

        public void WriteCompressedLong(long value)
        {
            if (value > sbyte.MaxValue || value <= sbyte.MinValue)
            {
                Write(sbyte.MinValue);
                Write(value);
            }
            else
            {
                Write((sbyte)value);
            }
        }

        public void WriteOffset(long value)
        {
            uint encOffset = (uint)BaseStream.Position;
            encOffset = (encOffset - Header.FStart) ^ 0xFFFFFFFF;
            encOffset *= Hash; // could this be removed? 
            encOffset -= WzAESConstant.WZ_OffsetConstant;
            encOffset = ByteUtils.RotateLeft(encOffset, (byte)(encOffset & 0x1F));
            uint writeOffset = encOffset ^ ((uint)value - (Header.FStart * 2));
            Write(writeOffset);
        }

        public override void Close()
        {
            if (!LeaveOpen)
            {
                base.Close();
            }
        }

        #endregion
    }
}