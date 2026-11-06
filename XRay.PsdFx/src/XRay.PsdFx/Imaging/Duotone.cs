using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>One ink of a duotone specification.</summary>
    internal sealed class DuotoneInk
    {
        public DuotoneInk(string name, PsdColor color, short[] curve)
        {
            Name = name;
            Color = color;
            Curve = curve;
        }

        /// <summary>Ink name (Pascal string from the specification).</summary>
        public string Name { get; private set; }

        /// <summary>The ink at 100% as sRGB.</summary>
        public PsdColor Color { get; private set; }

        /// <summary>The 13 transfer points (ink percent times 10 at 0, 5, 10, 20 ... 90, 95, 100% tone), -1 where unset.</summary>
        public short[] Curve { get; private set; }
    }

    /// <summary>
    /// Duotone rendering from the duotone specification in the color mode data:
    /// version (1), ink count (1 to 4), four 10-byte colors, four 64-byte Pascal
    /// names, four 28-byte transfer functions (13 points, 0 to 1000 or -1, then an
    /// override flag), dot gain and overprint colors (both ignored).
    /// Each pixel's tone is <c>t = 1 - gray</c>. Ink <c>i</c> covers
    /// <c>c_i = curve_i(t)</c>, a natural cubic spline through the set points. Its
    /// reflectance is the luminance of the gray level <c>1 - c_i</c> (plain sRGB
    /// decoding without a gray profile), so a monotone black ink with a linear
    /// curve renders exactly like the grayscale path. Inks multiply as filters in
    /// linear light, <c>L = prod(1 - (1 - r_i)(1 - ink_i))</c> per sRGB channel.
    /// </summary>
    internal sealed class Duotone
    {
        private const int MinimumLength = 2 + 2 + 40 + 256 + 112;
        private static readonly double[] CurveInputs = { 0, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 0.95, 1.0 };

        private readonly object _gate = new object();
        private float[] _table8;
        private float[] _table16;

        private readonly Spline[] _curves;

        private Duotone(DuotoneInk[] inks)
        {
            Inks = inks;
            _curves = new Spline[inks.Length];
            for (var i = 0; i < inks.Length; i++)
            {
                _curves[i] = new Spline(inks[i].Curve);
            }
        }

        public DuotoneInk[] Inks { get; private set; }

        /// <summary>Parses the specification, or returns null when it is missing or not understood.</summary>
        public static Duotone Parse(byte[] data)
        {
            if (data == null || data.Length < MinimumLength)
            {
                return null;
            }

            try
            {
                var reader = new BigEndianReader(data);
                var version = reader.ReadUInt16();
                var count = reader.ReadUInt16();
                if (version != 1 || count < 1 || count > 4)
                {
                    return null;
                }

                var colors = new PsdColor[4];
                for (var i = 0; i < 4; i++)
                {
                    colors[i] = ReadInkColor(reader);
                }

                var names = new string[4];
                for (var i = 0; i < 4; i++)
                {
                    var name = reader.ReadBytes(64);
                    names[i] = BigEndianReader.DecodeLatin1(SubArray(name, 1, Math.Min((int)name[0], 63)));
                }

                var inks = new DuotoneInk[count];
                for (var i = 0; i < 4; i++)
                {
                    var curve = new short[13];
                    for (var p = 0; p < 13; p++)
                    {
                        curve[p] = reader.ReadInt16();
                    }

                    reader.ReadUInt16(); // override flag
                    if (i < count)
                    {
                        inks[i] = new DuotoneInk(names[i], colors[i], curve);
                    }
                }

                return new Duotone(inks);
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        private static byte[] SubArray(byte[] data, int start, int count)
        {
            if (start >= data.Length || count <= 0)
            {
                return new byte[0];
            }

            count = Math.Min(count, data.Length - start);
            var result = new byte[count];
            Buffer.BlockCopy(data, start, result, 0, count);
            return result;
        }

        /// <summary>
        /// Converts a gray plane (0 = black, 1 = white) to RGB through the inks.
        /// Returns false for 32-bit data, which keeps the grayscale path.
        /// </summary>
        public bool Apply(float[] gray, int depth, IccSrgbTransform grayTransform, PlanarImage image)
        {
            if (depth != 8 && depth != 16)
            {
                return false;
            }

            var table = GetTable(depth == 8 ? 256 : 65536, grayTransform);
            var max = (table.Length / 3) - 1;
            for (var i = 0; i < gray.Length; i++)
            {
                var index = Compat.Clamp((int)((gray[i] * max) + 0.5f), 0, max) * 3;
                image.R[i] = table[index];
                image.G[i] = table[index + 1];
                image.B[i] = table[index + 2];
            }

            return true;
        }

        /// <summary>The ink mix for one gray value (0 = black, 1 = white), as encoded sRGB.</summary>
        public void Evaluate(double gray, IccSrgbTransform grayTransform, out double red, out double green, out double blue)
        {
            var tone = 1 - Compat.Clamp(gray, 0, 1);
            red = 1;
            green = 1;
            blue = 1;
            for (var i = 0; i < Inks.Length; i++)
            {
                var ink = Inks[i];
                var coverage = Compat.Clamp(_curves[i].Evaluate(tone), 0, 1);
                var device = 1 - coverage;
                var reflectance = grayTransform != null ? grayTransform.EvaluateLinear(device).R : IccPcs.DecodeSrgb(device);
                var absorbed = 1 - Compat.Clamp(reflectance, 0, 1);
                red *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.R / 255.0)));
                green *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.G / 255.0)));
                blue *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.B / 255.0)));
            }

            red = IccPcs.EncodeSrgb(red);
            green = IccPcs.EncodeSrgb(green);
            blue = IccPcs.EncodeSrgb(blue);
        }

        /// <summary>
        /// Evaluates a 13-point transfer function at <paramref name="tone"/> (0 to 1): a
        /// natural cubic spline through the set points, with 0% and 100% defaulting to
        /// 0 and 100% ink when unset.
        /// </summary>
        internal static double EvaluateCurve(short[] curve, double tone)
        {
            return new Spline(curve).Evaluate(tone);
        }

        private float[] GetTable(int levels, IccSrgbTransform grayTransform)
        {
            lock (_gate)
            {
                var table = levels == 256 ? _table8 : _table16;
                if (table == null)
                {
                    table = BuildTable(levels, grayTransform);
                    if (levels == 256)
                    {
                        _table8 = table;
                    }
                    else
                    {
                        _table16 = table;
                    }
                }

                return table;
            }
        }

        private float[] BuildTable(int levels, IccSrgbTransform grayTransform)
        {
            var table = new float[levels * 3];
            for (var i = 0; i < levels; i++)
            {
                double r, g, b;
                Evaluate(i / (double)(levels - 1), grayTransform, out r, out g, out b);
                table[i * 3] = (float)r;
                table[(i * 3) + 1] = (float)g;
                table[(i * 3) + 2] = (float)b;
            }

            return table;
        }

        /// <summary>
        /// A 10-byte Photoshop color as sRGB: Lab (space 7: L 0 to 10000, a and b signed
        /// hundredths) here, the other spaces through <see cref="LegacyText.ReadColor"/>.
        /// Color-book inks (Pantone and the like) cannot be resolved without the books
        /// and render as black.
        /// </summary>
        private static PsdColor ReadInkColor(BigEndianReader reader)
        {
            var start = reader.Position;
            var space = reader.ReadUInt16();
            if (space == 7)
            {
                var l = reader.ReadUInt16() / 100.0;
                var a = reader.ReadInt16() / 100.0;
                var b = reader.ReadInt16() / 100.0;
                reader.Skip(2);
                double red, green, blue;
                ColorSpaces.LabToSrgb(l, a, b, out red, out green, out blue);
                return new PsdColor(Level(red), Level(green), Level(blue));
            }

            reader.Position = start;
            if (space == 0 || space == 1 || space == 2 || space == 8)
            {
                return LegacyText.ReadColor(reader);
            }

            reader.Skip(10);
            return PsdColor.Black;
        }

        private static byte Level(double v)
        {
            return (byte)Compat.Clamp(Math.Round(v * 255), 0, 255);
        }

        /// <summary>Natural cubic spline through a transfer function's set points (the Curves adjustment's <c>build_curve_lut</c> scheme).</summary>
        private sealed class Spline
        {
            private readonly double[] _x;
            private readonly double[] _y;
            private readonly double[] _second;

            public Spline(short[] curve)
            {
                var xs = new List<double>(13);
                var ys = new List<double>(13);
                for (var p = 0; p < 13; p++)
                {
                    if (curve[p] >= 0 && curve[p] <= 1000)
                    {
                        xs.Add(CurveInputs[p]);
                        ys.Add(curve[p] / 1000.0);
                    }
                    else if (p == 0 || p == 12)
                    {
                        xs.Add(CurveInputs[p]);
                        ys.Add(p == 0 ? 0 : 1);
                    }
                }

                _x = xs.ToArray();
                _y = ys.ToArray();
                var n = _x.Length;
                _second = new double[n];
                var work = new double[n];
                for (var i = 1; i + 1 < n; i++)
                {
                    var sigma = (_x[i] - _x[i - 1]) / (_x[i + 1] - _x[i - 1]);
                    var pivot = (sigma * _second[i - 1]) + 2.0;
                    _second[i] = (sigma - 1.0) / pivot;
                    var slopes = ((_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i])) - ((_y[i] - _y[i - 1]) / (_x[i] - _x[i - 1]));
                    work[i] = ((6.0 * slopes / (_x[i + 1] - _x[i - 1])) - (sigma * work[i - 1])) / pivot;
                }

                for (var i = n - 2; i >= 0; i--)
                {
                    _second[i] = (_second[i] * _second[i + 1]) + work[i];
                }
            }

            public double Evaluate(double t)
            {
                var upper = 1;
                while (upper < _x.Length - 1 && t > _x[upper])
                {
                    upper++;
                }

                var span = _x[upper] - _x[upper - 1];
                var a = (_x[upper] - t) / span;
                var b = (t - _x[upper - 1]) / span;
                return (a * _y[upper - 1]) + (b * _y[upper]) + (((((a * a * a) - a) * _second[upper - 1]) + (((b * b * b) - b) * _second[upper])) * span * span / 6.0);
            }
        }
    }
}
