using System;
using System.Text;

namespace XRay.PsdFx.Icc
{
    /// <summary>Big-endian readers for ICC tags, with no Span or BinaryPrimitives (net45).</summary>
    internal static class IccBytes
    {
        [ThreadStatic]
        private static byte[] _single;

        public static int U16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        public static uint U32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        public static int I32(byte[] data, int offset)
        {
            return (int)U32(data, offset);
        }

        public static double S15Fixed16(byte[] data, int offset)
        {
            return I32(data, offset) / 65536.0;
        }

        /// <summary>Reads a big-endian IEEE single without allocating per sample.</summary>
        public static double Float(byte[] data, int offset)
        {
            var buffer = _single;
            if (buffer == null)
            {
                buffer = new byte[4];
                _single = buffer;
            }

            buffer[0] = data[offset + 3];
            buffer[1] = data[offset + 2];
            buffer[2] = data[offset + 1];
            buffer[3] = data[offset];
            return BitConverter.ToSingle(buffer, 0);
        }

        public static string Ascii(byte[] data, int offset, int count)
        {
            return Encoding.ASCII.GetString(data, offset, count);
        }

        /// <summary>A copy of [offset, offset+count); empty when that range does not fit.</summary>
        public static byte[] Slice(byte[] data, long offset, long count)
        {
            if (offset < 0 || count < 0 || offset > data.Length || count > data.Length - offset)
            {
                return new byte[0];
            }

            var result = new byte[count];
            Array.Copy(data, (int)offset, result, 0, (int)count);
            return result;
        }
    }
}
