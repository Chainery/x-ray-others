using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XRay.PsdFx.Tests.Support
{
    /// <summary>
    /// Writes small ICC profiles for tests: header, tag table and the tag types the color
    /// management code reads (XYZ, curv, para, desc, mluc, sf32, mft1, mft2, mAB, mpet).
    /// Values are given as doubles and quantized like real profiles.
    /// </summary>
    internal sealed class IccBuilder
    {
        internal struct TagEntry
        {
            public string Signature;
            public byte[] Data;

            public TagEntry(string signature, byte[] data)
            {
                Signature = signature;
                Data = data;
            }
        }

        private struct PlacedTag
        {
            public string Signature;
            public int Offset;
            public int Size;

            public PlacedTag(string signature, int offset, int size)
            {
                Signature = signature;
                Offset = offset;
                Size = size;
            }
        }

        public uint Version { get; set; } = 0x02100000;

        public string DeviceClass { get; set; } = "mntr";

        public string ColorSpace { get; set; } = "RGB ";

        public string Pcs { get; set; } = "XYZ ";

        internal List<TagEntry> Tags { get; private set; } = new List<TagEntry>();

        public IccBuilder Add(string signature, byte[] data)
        {
            Tags.Add(new TagEntry(signature, data));
            return this;
        }

        public byte[] Build()
        {
            var tableSize = 4 + (Tags.Count * 12);
            var offset = 128 + tableSize;
            var body = new MemoryStream();
            var entries = new List<PlacedTag>();
            foreach (var tag in Tags)
            {
                entries.Add(new PlacedTag(tag.Signature, offset + (int)body.Length, tag.Data.Length));
                body.Write(tag.Data, 0, tag.Data.Length);
                while (body.Length % 4 != 0)
                {
                    body.WriteByte(0);
                }
            }

            var total = offset + (int)body.Length;
            var bytes = new byte[total];
            WriteU32(bytes, 0, (uint)total);
            WriteU32(bytes, 8, Version);
            Ascii(DeviceClass).CopyTo(bytes, 12);
            Ascii(ColorSpace).CopyTo(bytes, 16);
            Ascii(Pcs).CopyTo(bytes, 20);
            Ascii("acsp").CopyTo(bytes, 36);
            WriteI32(bytes, 68, Fixed(0.9642));
            WriteI32(bytes, 72, Fixed(1.0));
            WriteI32(bytes, 76, Fixed(0.8249));
            WriteU32(bytes, 128, (uint)Tags.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var at = 132 + (i * 12);
                Ascii(entry.Signature).CopyTo(bytes, at);
                WriteU32(bytes, at + 4, (uint)entry.Offset);
                WriteU32(bytes, at + 8, (uint)entry.Size);
            }

            Array.Copy(body.ToArray(), 0, bytes, offset, (int)body.Length);
            return bytes;
        }

        public static byte[] Xyz(double x, double y, double z)
        {
            var data = new byte[20];
            Ascii("XYZ ").CopyTo(data, 0);
            WriteI32(data, 8, Fixed(x));
            WriteI32(data, 12, Fixed(y));
            WriteI32(data, 16, Fixed(z));
            return data;
        }

        /// <summary>A <c>curv</c> with a single u8Fixed8 gamma.</summary>
        public static byte[] Gamma(double gamma)
        {
            var data = new byte[14];
            Ascii("curv").CopyTo(data, 0);
            WriteU32(data, 8, 1);
            WriteU16(data, 12, (ushort)Math.Round(gamma * 256));
            return data;
        }

        /// <summary>A sampled <c>curv</c> (or the identity when <paramref name="samples"/> is empty).</summary>
        public static byte[] Table(IList<double> samples)
        {
            var data = new byte[12 + (samples.Count * 2)];
            Ascii("curv").CopyTo(data, 0);
            WriteU32(data, 8, (uint)samples.Count);
            for (var i = 0; i < samples.Count; i++)
            {
                WriteU16(data, 12 + (i * 2), Word(samples[i]));
            }

            return data;
        }

        public static byte[] Parametric(int function, params double[] parameters)
        {
            var data = new byte[12 + (parameters.Length * 4)];
            Ascii("para").CopyTo(data, 0);
            WriteU16(data, 8, (ushort)function);
            for (var i = 0; i < parameters.Length; i++)
            {
                WriteI32(data, 12 + (i * 4), Fixed(parameters[i]));
            }

            return data;
        }

        /// <summary>A v2 <c>desc</c> (ASCII part only, empty Unicode and ScriptCode parts).</summary>
        public static byte[] Description(string text)
        {
            var ascii = Encoding.ASCII.GetBytes(text + "\0");
            var data = new byte[12 + ascii.Length + 4 + 4 + 3 + 67];
            Ascii("desc").CopyTo(data, 0);
            WriteU32(data, 8, (uint)ascii.Length);
            ascii.CopyTo(data, 12);
            return data;
        }

        /// <summary>A v4 <c>mluc</c> with one en-US record.</summary>
        public static byte[] MultiLocalized(string text)
        {
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            var data = new byte[28 + utf16.Length];
            Ascii("mluc").CopyTo(data, 0);
            WriteU32(data, 8, 1);
            WriteU32(data, 12, 12);
            Ascii("enUS").CopyTo(data, 16);
            WriteU32(data, 20, (uint)utf16.Length);
            WriteU32(data, 24, 28);
            utf16.CopyTo(data, 28);
            return data;
        }

        public static byte[] Sf32(params double[] values)
        {
            var data = new byte[8 + (values.Length * 4)];
            Ascii("sf32").CopyTo(data, 0);
            for (var i = 0; i < values.Length; i++)
            {
                WriteI32(data, 8 + (i * 4), Fixed(values[i]));
            }

            return data;
        }

        /// <summary>
        /// An <c>mft2</c> (16-bit) or <c>mft1</c> (8-bit) LUT with identity matrix and identity
        /// input/output tables; <paramref name="clut"/> maps a grid node (normalized inputs,
        /// first varying slowest) to normalized outputs.
        /// </summary>
        public static byte[] Lut(bool sixteenBit, int inputs, int outputs, int grid, Func<double[], double[]> clut)
        {
            var stream = new MemoryStream();
            var header = new byte[sixteenBit ? 52 : 48];
            Ascii(sixteenBit ? "mft2" : "mft1").CopyTo(header, 0);
            header[8] = (byte)inputs;
            header[9] = (byte)outputs;
            header[10] = (byte)grid;
            for (var i = 0; i < 3; i++)
            {
                WriteI32(header, 12 + (i * 16), Fixed(1.0));
            }

            var entries = sixteenBit ? 2 : 256;
            if (sixteenBit)
            {
                WriteU16(header, 48, (ushort)entries);
                WriteU16(header, 50, (ushort)entries);
            }

            stream.Write(header, 0, header.Length);
            var word = new byte[2];
            Action<double> sample = delegate(double value)
            {
                if (sixteenBit)
                {
                    WriteU16Big(word, 0, Word(value));
                    stream.Write(word, 0, 2);
                }
                else
                {
                    stream.WriteByte((byte)ClampInt(Math.Round(value * 255), 0, 255));
                }
            };

            for (var c = 0; c < inputs; c++)
            {
                for (var i = 0; i < entries; i++)
                {
                    sample(i / (double)(entries - 1));
                }
            }

            var total = (int)Math.Pow(grid, inputs);
            var node = new double[inputs];
            for (var n = 0; n < total; n++)
            {
                var rest = n;
                for (var d = inputs - 1; d >= 0; d--)
                {
                    node[d] = (rest % grid) / (double)(grid - 1);
                    rest /= grid;
                }

                var outputsAt = clut(node);
                foreach (var value in outputsAt)
                {
                    sample(value);
                }
            }

            for (var c = 0; c < outputs; c++)
            {
                for (var i = 0; i < entries; i++)
                {
                    sample(i / (double)(entries - 1));
                }
            }

            return stream.ToArray();
        }

        /// <summary>
        /// A v4 <c>mAB</c> in the matrix/shaper form (no CLUT, no A curves): M curves, a 3x3
        /// matrix with offsets on normalized XYZ, then B curves.
        /// </summary>
        public static byte[] MatrixShaperAToB(byte[][] mCurves, double[] matrix, double[] offsets, byte[][] bCurves)
        {
            var stream = new MemoryStream();
            stream.Write(new byte[32], 0, 32);
            Func<int> pad = delegate
            {
                while (stream.Length % 4 != 0)
                {
                    stream.WriteByte(0);
                }

                return (int)stream.Length;
            };

            var offsetB = pad();
            foreach (var curve in bCurves)
            {
                stream.Write(curve, 0, curve.Length);
                pad();
            }

            var offsetMatrix = pad();
            var word = new byte[4];
            foreach (var value in Concat(matrix, offsets))
            {
                WriteI32Big(word, 0, Fixed(value));
                stream.Write(word, 0, 4);
            }

            var offsetM = pad();
            foreach (var curve in mCurves)
            {
                stream.Write(curve, 0, curve.Length);
                pad();
            }

            var data = stream.ToArray();
            Ascii("mAB ").CopyTo(data, 0);
            data[8] = 3;
            data[9] = 3;
            WriteU32(data, 12, (uint)offsetB);
            WriteU32(data, 16, (uint)offsetMatrix);
            WriteU32(data, 20, (uint)offsetM);
            return data;
        }

        /// <summary>A <c>parf</c> formula segment (type 0: gamma, a, b, c; types 1 and 2: five parameters).</summary>
        public static byte[] FormulaSegment(int type, params float[] parameters)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("parf"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(type));
            body.AddRange(new byte[] { 0, 0 });
            body.AddRange(Floats(parameters));
            return body.ToArray();
        }

        /// <summary>A <c>samf</c> sampled segment (the first point is implicit).</summary>
        public static byte[] SampledSegment(params float[] samples)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("samf"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U32(samples.Length));
            body.AddRange(Floats(samples));
            return body.ToArray();
        }

        /// <summary>A <c>curf</c> segmented curve: breakpoints between the segments.</summary>
        public static byte[] SegmentedCurve(float[] breakpoints, params byte[][] segments)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("curf"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(segments.Length));
            body.AddRange(new byte[] { 0, 0 });
            body.AddRange(Floats(breakpoints));
            foreach (var segment in segments)
            {
                body.AddRange(segment);
            }

            return body.ToArray();
        }

        /// <summary>A <c>cvst</c> curve set element.</summary>
        public static byte[] CurveSetElement(params byte[][] curves)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("cvst"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(curves.Length));
            body.AddRange(U16(curves.Length));
            body.AddRange(PositionTable(12 + (8 * curves.Length), curves));
            return body.ToArray();
        }

        /// <summary>A <c>matf</c> element: an outputs x inputs matrix (row major), then the offsets.</summary>
        public static byte[] MatrixElement(int inputs, int outputs, float[] matrix, float[] offsets)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("matf"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(inputs));
            body.AddRange(U16(outputs));
            body.AddRange(Floats(matrix));
            body.AddRange(Floats(offsets));
            return body.ToArray();
        }

        /// <summary>A float <c>clut</c> element (first input varies slowest).</summary>
        public static byte[] ClutElement(int inputs, int outputs, byte[] grid, float[] values)
        {
            var points = new byte[16];
            Array.Copy(grid, points, grid.Length);
            var body = new List<byte>();
            body.AddRange(Ascii("clut"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(inputs));
            body.AddRange(U16(outputs));
            body.AddRange(points);
            body.AddRange(Floats(values));
            return body.ToArray();
        }

        /// <summary>A multiProcessElementsType (<c>mpet</c>) tag, as used by <c>D2Bx</c>.</summary>
        public static byte[] MultiProcess(int inputs, int outputs, params byte[][] elements)
        {
            var body = new List<byte>();
            body.AddRange(Ascii("mpet"));
            body.AddRange(new byte[] { 0, 0, 0, 0 });
            body.AddRange(U16(inputs));
            body.AddRange(U16(outputs));
            body.AddRange(U32(elements.Length));
            body.AddRange(PositionTable(16 + (8 * elements.Length), elements));
            return body.ToArray();
        }

        private static byte[] PositionTable(int start, byte[][] items)
        {
            var table = new List<byte>();
            var body = new List<byte>();
            foreach (var item in items)
            {
                table.AddRange(U32(start + body.Count));
                table.AddRange(U32(item.Length));
                body.AddRange(item);
                while (body.Count % 4 != 0)
                {
                    body.Add(0);
                }
            }

            table.AddRange(body);
            return table.ToArray();
        }

        private static IEnumerable<double> Concat(double[] first, double[] second)
        {
            foreach (var value in first)
            {
                yield return value;
            }

            foreach (var value in second)
            {
                yield return value;
            }
        }

        private static byte[] U16(int value)
        {
            return new[] { (byte)(value >> 8), (byte)value };
        }

        private static byte[] U32(int value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static byte[] Floats(IEnumerable<float> values)
        {
            var bytes = new List<byte>();
            foreach (var value in values)
            {
                var one = new byte[4];
                WriteSingleBig(one, 0, value);
                bytes.AddRange(one);
            }

            return bytes.ToArray();
        }

        public static int Fixed(double value)
        {
            return (int)Math.Round(value * 65536);
        }

        public static ushort Word(double value)
        {
            return (ushort)ClampInt(Math.Round(value * 65535), 0, 65535);
        }

        private static int ClampInt(double value, int min, int max)
        {
            return value < min ? min : value > max ? max : (int)value;
        }

        private static byte[] Ascii(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static void WriteU16(byte[] data, int at, ushort value)
        {
            data[at] = (byte)(value >> 8);
            data[at + 1] = (byte)value;
        }

        private static void WriteU16Big(byte[] data, int at, ushort value)
        {
            WriteU16(data, at, value);
        }

        private static void WriteU32(byte[] data, int at, uint value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static void WriteI32(byte[] data, int at, int value)
        {
            WriteU32(data, at, (uint)value);
        }

        private static void WriteI32Big(byte[] data, int at, int value)
        {
            WriteU32(data, at, (uint)value);
        }

        private static void WriteSingleBig(byte[] data, int at, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            Array.Copy(bytes, 0, data, at, 4);
        }
    }
}
