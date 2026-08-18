using System;
using System.IO;
using System.Security.Cryptography;
using MapleLib.MapleCryptoLib;
using System.Linq;
using System.Runtime.InteropServices;

#nullable enable

namespace MapleLib.WzLib.Util
{
    public sealed class WzMutableKey : IEquatable<WzMutableKey>
    {
        private static readonly int BatchSize = 4096;
        private readonly byte[] _iv;
        private readonly byte[] _aesUserKey;
        private byte[]? _keys;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="WzIv"></param>
        /// <param name="AesKey">The 32-byte AES UserKey (derived from 32 DWORD)</param>
        public WzMutableKey(byte[] WzIv, byte[] AesKey)
        {
            this._iv = WzIv;
            this._aesUserKey = AesKey;
        }

        public byte[] GetKeys() => _keys?.ToArray() ?? Array.Empty<byte>();

        public byte this[int index]
        {
            get
            {
                EnsureKeySize(index + 1);
                return _keys![index];
            }
        }

        /// <summary>
        /// Publishes a finished key buffer.
        ///
        /// One <see cref="WzMutableKey"/> is deliberately shared between readers:
        /// <c>WzBinaryReader.CreateReaderForSection</c> copies the field by reference
        /// into every section reader it hands out, so <see cref="_keys"/> is read
        /// concurrently. The reference assignment itself is atomic, so a reader sees
        /// either the old array or the new one -- never a torn reference -- but it
        /// must never see an array that is still being filled.
        ///
        /// The length check keeps a slower thread that started from a shorter buffer
        /// from replacing a longer one that another thread already published, which
        /// would make an in-range index go out of range. The derivation is
        /// deterministic, so both arrays hold identical bytes over their common
        /// prefix and dropping the shorter one loses nothing. It is a check-then-set
        /// and so still racy in principle; it narrows the window rather than closing
        /// it. Closing it properly means locking, which this type avoids because it
        /// sits on the hot decrypt path.
        /// </summary>
        private void Publish(byte[] newKeys)
        {
            byte[]? current = _keys;
            if (current == null || newKeys.Length >= current.Length)
                _keys = newKeys;
        }

        public void EnsureKeySize(int size)
        {
            if (_keys != null && _keys.Length >= size)
            {
                return;
            }

            size = (int)Math.Ceiling(1.0 * size / BatchSize) * BatchSize;
            byte[] newKeys = new byte[size];

            if (BitConverter.ToInt32(this._iv, 0) == 0)
            {
                // A zero IV means the key stream is all zeros, so this buffer is
                // already complete as allocated.
                Publish(newKeys);
                return;
            }

            int startIndex = 0;
            if (_keys != null)
            {
                _keys.CopyTo(newKeys, 0);
                startIndex = _keys.Length;
            }

            // NOT published here. Until 2026-08-05 `_keys = newKeys` ran at this
            // point, before the CryptoStream below wrote a single byte, so a
            // concurrent reader sharing this key could index into the new array
            // while it was still zeros and decrypt garbage -- wrong bytes, no
            // exception, nothing logged. The buffer is published only once it is
            // filled. Single-threaded behaviour is unchanged: the loop below reads
            // its feedback block from the local `newKeys`, never from the field.
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Key = _aesUserKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;   // Ensure no padding is added

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream(newKeys, startIndex, newKeys.Length - startIndex, true);
            using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);

            Span<byte> block = stackalloc byte[16];
            for (int i = startIndex; i < size; i += 16)
            {
                if (i == 0)
                {
                    for (int j = 0; j < block.Length; j++)
                        block[j] = _iv[j % 4];
                    cs.Write(block);
                }
                else
                {
                    cs.Write(newKeys.AsSpan(i - 16, 16));
                }
            }

            // The CryptoStream is flushed by its `using` before the buffer becomes
            // visible to anyone else.
            cs.FlushFinalBlock();
            Publish(newKeys);
        }

        public bool Equals(WzMutableKey? other) =>
            other != null && _iv.AsSpan().SequenceEqual(other._iv) && _aesUserKey.AsSpan().SequenceEqual(other._aesUserKey);

        public override bool Equals(object? obj) =>
            ReferenceEquals(this, obj) || (obj is WzMutableKey other && Equals(other));

        public override int GetHashCode() => HashCode.Combine(MemoryMarshal.Read<int>(_iv), MemoryMarshal.Read<int>(_aesUserKey));

        public static bool operator ==(WzMutableKey? left, WzMutableKey? right) =>
           ReferenceEquals(left, right) || (left is not null && left.Equals(right));

        public static bool operator !=(WzMutableKey? left, WzMutableKey? right) => !(left == right);
    }
}
