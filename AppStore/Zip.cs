using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AppStore
{
    /// <summary>One file in a zip archive.</summary>
    public class ZipEntry
    {
        /// <summary>Path inside the archive, with backslashes ("bin\app.exe").</summary>
        public string Name = "";
        public int Method;              // 0 stored, 8 deflate
        public int CompressedSize, Size;
        public uint Crc;
        public int LocalHeaderOffset;
        public bool IsDirectory;
    }

    /// <summary>
    /// Reads .zip files: stored and deflate entries, no encryption, no zip64.
    /// The .NET Compact Framework has no zip or deflate support, so this
    /// carries its own inflater (RFC 1951).
    /// </summary>
    public class ZipReader
    {
        readonly byte[] zip;
        public readonly List<ZipEntry> Entries = new List<ZipEntry>();

        public ZipReader(byte[] data)
        {
            zip = data;
            ReadCentralDirectory();
        }

        void ReadCentralDirectory()
        {
            // End of central directory: signature 0x06054b50, at most 64 KB + 22 from the end.
            int eocd = -1;
            int stop = Math.Max(0, zip.Length - 22 - 65535);
            for (int i = zip.Length - 22; i >= stop; i--)
                if (U32(i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("not a zip file");

            int count = U16(eocd + 10);
            uint cdSize = U32(eocd + 12), cdOffset = U32(eocd + 16);
            if (count == 0xFFFF || cdOffset == 0xFFFFFFFF) throw new InvalidDataException("zip64 archives aren't supported");
            if ((long)cdOffset + cdSize > zip.Length) throw new InvalidDataException("zip file is truncated");

            int p = (int)cdOffset;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > zip.Length || U32(p) != 0x02014b50) throw new InvalidDataException("bad zip directory");
                int flags = U16(p + 8);
                ZipEntry e = new ZipEntry();
                e.Method = U16(p + 10);
                e.Crc = U32(p + 16);
                e.CompressedSize = (int)U32(p + 20);
                e.Size = (int)U32(p + 24);
                int nameLen = U16(p + 28), extraLen = U16(p + 30), commentLen = U16(p + 32);
                e.LocalHeaderOffset = (int)U32(p + 42);
                if (p + 46 + nameLen > zip.Length) throw new InvalidDataException("bad zip directory");
                // Names are taken as UTF-8 (flag 0x800) or plain ASCII, which is what
                // app archives use; the device has no code page 437.
                string name = Encoding.UTF8.GetString(zip, p + 46, nameLen).Replace('/', '\\');
                e.IsDirectory = name.EndsWith("\\");
                e.Name = name.TrimEnd('\\');
                if ((flags & 1) != 0) throw new InvalidDataException(e.Name + " is encrypted");
                if (e.Size < 0 || e.CompressedSize < 0) throw new InvalidDataException(e.Name + " is too big");
                if (e.Name.Length > 0) Entries.Add(e);
                p += 46 + nameLen + extraLen + commentLen;
            }
        }

        /// <summary>The entry's bytes, checked against its CRC.</summary>
        public byte[] Extract(ZipEntry e)
        {
            int p = e.LocalHeaderOffset;
            if (p < 0 || p + 30 > zip.Length || U32(p) != 0x04034b50) throw new InvalidDataException(e.Name + ": bad local header");
            int start = p + 30 + U16(p + 26) + U16(p + 28);
            if (start + (long)e.CompressedSize > zip.Length) throw new InvalidDataException(e.Name + ": data is truncated");

            byte[] data;
            if (e.Method == 0)
            {
                if (e.CompressedSize != e.Size) throw new InvalidDataException(e.Name + ": bad stored size");
                data = new byte[e.Size];
                Buffer.BlockCopy(zip, start, data, 0, e.Size);
            }
            else if (e.Method == 8)
            {
                data = Inflater.Inflate(zip, start, e.CompressedSize, e.Size);
            }
            else throw new InvalidDataException(e.Name + ": compression method " + e.Method + " isn't supported");

            if (Crc32.Compute(data, 0, data.Length) != e.Crc) throw new InvalidDataException(e.Name + ": CRC mismatch");
            return data;
        }

        /// <summary>
        /// Rejects names that would land outside the destination folder
        /// ("..\x", "\Windows\x", "C:x").
        /// </summary>
        public static bool IsSafeName(string name)
        {
            if (name.Length == 0 || name.StartsWith("\\") || name.IndexOf(':') >= 0) return false;
            foreach (string part in name.Split('\\'))
                if (part == ".." || part == "." || part.Length == 0) return false;
            return true;
        }

        int U16(int p) { return zip[p] | (zip[p + 1] << 8); }
        uint U32(int p) { return (uint)(zip[p] | (zip[p + 1] << 8) | (zip[p + 2] << 16) | (zip[p + 3] << 24)); }
    }

    public class InvalidDataException : IOException
    {
        public InvalidDataException(string message) : base(message) { }
    }

    public static class Crc32
    {
        static uint[] table;

        public static uint Compute(byte[] data, int offset, int count)
        {
            if (table == null)
            {
                uint[] t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                table = t;
            }
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }

    /// <summary>
    /// Raw deflate decoder (RFC 1951), after zlib's "puff". The output size is
    /// known from the zip directory, so the output buffer is allocated once.
    /// </summary>
    class Inflater
    {
        static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        const int MaxBits = 15;

        readonly byte[] src;
        int pos;
        readonly int end;
        int bitBuf, bitCount;
        readonly byte[] output;
        int outPos;

        class Huffman
        {
            public readonly short[] Count = new short[MaxBits + 1];
            public readonly short[] Symbol;
            public Huffman(int symbols) { Symbol = new short[symbols]; }
        }

        Inflater(byte[] source, int offset, int length, int size)
        {
            src = source;
            pos = offset;
            end = offset + length;
            output = new byte[size];
        }

        public static byte[] Inflate(byte[] source, int offset, int length, int size)
        {
            Inflater f = new Inflater(source, offset, length, size);
            f.Run();
            if (f.outPos != size) throw new InvalidDataException("deflate data is shorter than expected");
            return f.output;
        }

        int Bits(int need)
        {
            int val = bitBuf;
            while (bitCount < need)
            {
                if (pos >= end) throw new InvalidDataException("deflate data ended early");
                val |= src[pos++] << bitCount;
                bitCount += 8;
            }
            bitBuf = val >> need;
            bitCount -= need;
            return val & ((1 << need) - 1);
        }

        void Run()
        {
            int last;
            do
            {
                last = Bits(1);
                int type = Bits(2);
                if (type == 0) Stored();
                else if (type == 1) Codes(FixedLength, FixedDistance());
                else if (type == 2) Dynamic();
                else throw new InvalidDataException("bad deflate block type");
            } while (last == 0);
        }

        void Stored()
        {
            bitBuf = 0;                         // skip to the byte boundary
            bitCount = 0;
            if (pos + 4 > end) throw new InvalidDataException("deflate data ended early");
            int len = src[pos] | (src[pos + 1] << 8);
            int nlen = src[pos + 2] | (src[pos + 3] << 8);
            pos += 4;
            if (len != (~nlen & 0xFFFF)) throw new InvalidDataException("bad stored block");
            if (pos + len > end) throw new InvalidDataException("deflate data ended early");
            if (outPos + len > output.Length) throw new InvalidDataException("deflate data is longer than expected");
            Buffer.BlockCopy(src, pos, output, outPos, len);
            pos += len;
            outPos += len;
        }

        int Decode(Huffman h)
        {
            int code = 0, first = 0, index = 0;
            int buf = bitBuf, left = bitCount;
            for (int len = 1; len <= MaxBits; len++)
            {
                if (left == 0)
                {
                    if (pos >= end) throw new InvalidDataException("deflate data ended early");
                    buf = src[pos++];
                    left = 8;
                }
                code |= buf & 1;
                buf >>= 1;
                left--;
                int count = h.Count[len];
                if (code - count < first)
                {
                    bitBuf = buf;
                    bitCount = left;
                    return h.Symbol[index + (code - first)];
                }
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new InvalidDataException("bad deflate code");
        }

        // Builds a decoding table from code lengths. Returns 0 for a complete
        // code, > 0 for an incomplete one, < 0 for an over-subscribed one.
        static int Construct(Huffman h, short[] lengths, int offset, int n)
        {
            for (int len = 0; len <= MaxBits; len++) h.Count[len] = 0;
            for (int s = 0; s < n; s++) h.Count[lengths[offset + s]]++;
            if (h.Count[0] == n) return 0;

            int left = 1;
            for (int len = 1; len <= MaxBits; len++)
            {
                left <<= 1;
                left -= h.Count[len];
                if (left < 0) return left;
            }

            short[] offs = new short[MaxBits + 1];
            for (int len = 1; len < MaxBits; len++) offs[len + 1] = (short)(offs[len] + h.Count[len]);
            for (int s = 0; s < n; s++)
                if (lengths[offset + s] != 0) h.Symbol[offs[lengths[offset + s]]++] = (short)s;
            return left;
        }

        void Codes(Huffman lencode, Huffman distcode)
        {
            for (; ; )
            {
                int symbol = Decode(lencode);
                if (symbol < 256)
                {
                    if (outPos >= output.Length) throw new InvalidDataException("deflate data is longer than expected");
                    output[outPos++] = (byte)symbol;
                }
                else if (symbol == 256) return;
                else
                {
                    symbol -= 257;
                    if (symbol >= 29) throw new InvalidDataException("bad deflate length");
                    int len = LengthBase[symbol] + Bits(LengthExtra[symbol]);
                    int ds = Decode(distcode);
                    if (ds >= 30) throw new InvalidDataException("bad deflate distance");
                    int dist = DistBase[ds] + Bits(DistExtra[ds]);
                    if (dist > outPos) throw new InvalidDataException("deflate distance too far back");
                    if (outPos + len > output.Length) throw new InvalidDataException("deflate data is longer than expected");
                    int from = outPos - dist;
                    for (int i = 0; i < len; i++) output[outPos++] = output[from + i];
                }
            }
        }

        static Huffman fixedLength, fixedDistance;

        static Huffman FixedLength
        {
            get
            {
                if (fixedLength == null) BuildFixed();
                return fixedLength;
            }
        }

        static Huffman FixedDistance()
        {
            if (fixedDistance == null) BuildFixed();
            return fixedDistance;
        }

        static void BuildFixed()
        {
            short[] lengths = new short[288];
            int s = 0;
            for (; s < 144; s++) lengths[s] = 8;
            for (; s < 256; s++) lengths[s] = 9;
            for (; s < 280; s++) lengths[s] = 7;
            for (; s < 288; s++) lengths[s] = 8;
            Huffman l = new Huffman(288);
            Construct(l, lengths, 0, 288);

            short[] d = new short[30];
            for (int k = 0; k < 30; k++) d[k] = 5;
            Huffman dh = new Huffman(30);
            Construct(dh, d, 0, 30);

            fixedDistance = dh;
            fixedLength = l;
        }

        void Dynamic()
        {
            int nlen = Bits(5) + 257;
            int ndist = Bits(5) + 1;
            int ncode = Bits(4) + 4;
            if (nlen > 286 || ndist > 30) throw new InvalidDataException("bad deflate header");

            short[] lengths = new short[320];
            int index;
            for (index = 0; index < ncode; index++) lengths[CodeLengthOrder[index]] = (short)Bits(3);
            for (; index < 19; index++) lengths[CodeLengthOrder[index]] = 0;

            Huffman lencode = new Huffman(286);
            if (Construct(lencode, lengths, 0, 19) != 0) throw new InvalidDataException("bad deflate code lengths");

            index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(lencode);
                if (symbol < 16) lengths[index++] = (short)symbol;
                else
                {
                    int len = 0, repeat;
                    if (symbol == 16)
                    {
                        if (index == 0) throw new InvalidDataException("bad deflate repeat");
                        len = lengths[index - 1];
                        repeat = 3 + Bits(2);
                    }
                    else if (symbol == 17) repeat = 3 + Bits(3);
                    else repeat = 11 + Bits(7);
                    if (index + repeat > nlen + ndist) throw new InvalidDataException("bad deflate repeat");
                    while (repeat-- > 0) lengths[index++] = (short)len;
                }
            }
            if (lengths[256] == 0) throw new InvalidDataException("deflate block has no end code");

            int err = Construct(lencode, lengths, 0, nlen);
            if (err < 0 || (err > 0 && nlen - lencode.Count[0] != 1)) throw new InvalidDataException("bad deflate literal codes");

            Huffman distcode = new Huffman(30);
            err = Construct(distcode, lengths, nlen, ndist);
            if (err < 0 || (err > 0 && ndist - distcode.Count[0] != 1)) throw new InvalidDataException("bad deflate distance codes");

            Codes(lencode, distcode);
        }
    }
}
