using System;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>
    /// Bounds-checked big-endian cursor over an in-memory PSD buffer. Every read
    /// throws <see cref="PsdFormatException"/> on truncation, so a damaged length
    /// never turns into an out-of-range slice or a huge allocation.
    /// </summary>
    internal sealed class BigEndianReader
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private readonly byte[] _data;

        public BigEndianReader(byte[] data)
        {
            _data = data;
        }

        public int Position { get; set; }

        public int Length => _data.Length;

        public int Remaining => _data.Length - Position;

        /// <summary>Validates <paramref name="count"/> and returns the offset it starts at.</summary>
        private int Take(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new PsdFormatException(string.Format("Unexpected end of data at offset {0} (needed {1} bytes, {2} left).", Position, count, Remaining));
            }

            var offset = Position;
            Position += count;
            return offset;
        }

        public byte ReadByte() => _data[Take(1)];

        public sbyte ReadSByte() => unchecked((sbyte)_data[Take(1)]);

        public ushort ReadUInt16()
        {
            var i = Take(2);
            return (ushort)((_data[i] << 8) | _data[i + 1]);
        }

        public short ReadInt16() => unchecked((short)ReadUInt16());

        public uint ReadUInt32()
        {
            var i = Take(4);
            return ((uint)_data[i] << 24) | ((uint)_data[i + 1] << 16) | ((uint)_data[i + 2] << 8) | _data[i + 3];
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadUInt64()
        {
            var i = Take(8);
            var hi = ((ulong)_data[i] << 24) | ((ulong)_data[i + 1] << 16) | ((ulong)_data[i + 2] << 8) | _data[i + 3];
            var lo = ((ulong)_data[i + 4] << 24) | ((ulong)_data[i + 5] << 16) | ((ulong)_data[i + 6] << 8) | _data[i + 7];
            return (hi << 32) | lo;
        }

        public long ReadInt64() => unchecked((long)ReadUInt64());

        public double ReadDouble() => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64()));

        /// <summary>Reads a 16.16 fixed-point number.</summary>
        public double ReadFixed16() => ReadInt32() / 65536.0;

        /// <summary>Reads a four-character code such as 8BIM or Nrml.</summary>
        public string ReadSignature() => Latin1.GetString(_data, Take(4), 4);

        public byte[] ReadBytes(int count)
        {
            var i = Take(count);
            var copy = new byte[count];
            Array.Copy(_data, i, copy, 0, count);
            return copy;
        }

        public byte[] ReadBytes(long count) => ReadBytes((int)count);

        public BigEndianReader Slice(long count) => new BigEndianReader(ReadBytes(count));

        public void Skip(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new PsdFormatException(string.Format("Cannot skip {0} bytes at offset {1} ({2} left).", count, Position, Remaining));
            }

            Position += count;
        }

        /// <summary>
        /// Reads a Pascal string (length byte plus bytes), padding the total consumed
        /// size to a multiple of <paramref name="padding"/>. Text decodes as Latin-1,
        /// which is what Photoshop writes on Windows.
        /// </summary>
        public string ReadPascalString(int padding = 2)
        {
            var start = Position;
            var length = ReadByte();
            var text = Latin1.GetString(_data, Take(length), length);
            var consumed = Position - start;
            var padded = padding <= 1 ? consumed : (consumed + padding - 1) / padding * padding;
            Skip(Math.Min(padded - consumed, Remaining));
            return text;
        }

        /// <summary>Reads a u32 code-unit count followed by UTF-16BE text.</summary>
        public string ReadUnicodeString()
        {
            var count = ReadUInt32();
            if (count > (uint)(Remaining / 2))
            {
                throw new PsdFormatException(string.Format("Unicode string length {0} exceeds the remaining data.", count));
            }

            var bytes = ReadBytes((int)count * 2);
            var text = Encoding.BigEndianUnicode.GetString(bytes);
            return text.TrimEnd('\0');
        }

        /// <summary>Reads a section length that is u64 in PSB files and u32 in PSD files.</summary>
        public long ReadLength(bool large)
        {
            var value = large ? ReadUInt64() : ReadUInt32();
            if (value > (ulong)Remaining)
            {
                throw new PsdFormatException(string.Format("Section length {0} at offset {1} exceeds the remaining {2} bytes.", value, Position, Remaining));
            }

            return (long)value;
        }

        public static string DecodeLatin1(byte[] data) => Latin1.GetString(data);
    }
}
