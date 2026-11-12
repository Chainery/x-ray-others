using System;
using System.Collections.Generic;
using XRay.PsdFx.Icc;

namespace XRay.PsdFx
{
    /// <summary>
    /// A CMYK, grayscale or duotone document's channel space, sampled both ways, for the
    /// adjustment layers Photoshop evaluates on the document's own channels (the reference's
    /// InkSpace, built by build_cmyk_ink_space / build_gray_ink_space in color_management.cpp).
    /// <para>
    /// The compositor works in sRGB, so an adjustment that runs on the inks takes the backdrop
    /// back to device values, maps each channel (<see cref="IInkMap"/>) and returns to sRGB
    /// through the same tables that converted the document's pixels. Device values use the PSD
    /// storage convention: 0 = full ink, 1 = none; gray 0 = black.
    /// </para>
    /// Every table is built once and read only, so rows can be converted concurrently.
    /// </summary>
    internal sealed class IccInkSpace
    {
        private const int RgbGridPoints = 33;

        private readonly IccSrgbTransform _transform;

        // CMYK: sRGB grid nodes (red slowest) of four PSD-convention ink values.
        private readonly float[] _rgbToInk;

        // Gray: the sRGB triple of each stored level, and the key the inverse searches.
        private readonly float[] _levelRgb;
        private readonly float[] _levelKey;
        private readonly bool _keyMonotonic;

        private IccInkSpace(IccSrgbTransform transform, float[] rgbToInk)
        {
            Channels = 4;
            _transform = transform;
            _rgbToInk = rgbToInk;
        }

        private IccInkSpace(float[] levelRgb)
        {
            Channels = 1;
            _levelRgb = levelRgb;
            _levelKey = new float[256];
            _keyMonotonic = true;
            for (var level = 0; level < 256; level++)
            {
                _levelKey[level] = Key(levelRgb[level * 3], levelRgb[(level * 3) + 1], levelRgb[(level * 3) + 2]);
                if (level > 0 && _levelKey[level] < _levelKey[level - 1])
                {
                    _keyMonotonic = false;
                }
            }
        }

        /// <summary>Device channels: 1 (gray or duotone) or 4 (CMYK).</summary>
        public int Channels { get; private set; }

        /// <summary>
        /// The ink space of a document whose adjustments Photoshop evaluates on its own
        /// channels: CMYK with a CMYK profile, grayscale with a gray profile, and duotone with
        /// a readable ink specification. Null otherwise (the adjustments keep their RGB math).
        /// </summary>
        public static IccInkSpace ForDocument(PsdDocument document)
        {
            var transform = document.ColorTransform;
            try
            {
                switch (document.ColorMode)
                {
                    case PsdColorMode.Cmyk:
                        return transform != null && transform.Channels == 4 ? ForCmyk(transform) : null;
                    case PsdColorMode.Grayscale:
                        if (transform == null || transform.Channels != 1)
                        {
                            return null;
                        }

                        return ForGray(delegate(float[][] planes)
                        {
                            return transform.Apply(planes, 8, planes[1], planes[2], planes[3], 256);
                        });
                    case PsdColorMode.Duotone:
                        {
                            var duotone = document.Colors.Duotone;
                            if (duotone == null)
                            {
                                return null;
                            }

                            return ForGray(delegate(float[][] planes)
                            {
                                var image = new PlanarImage(new PsdRect(0, 0, 256, 1));
                                if (!duotone.Apply(planes[0], 8, transform != null && transform.Channels == 1 ? transform : null, image))
                                {
                                    return false;
                                }

                                Array.Copy(image.R, planes[1], 256);
                                Array.Copy(image.G, planes[2], 256);
                                Array.Copy(image.B, planes[3], 256);
                                return true;
                            });
                        }

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is OverflowException)
                {
                    return null;
                }

                throw;
            }
        }

        /// <summary>Builds the CMYK form from a CMYK transform, or null when the profile has no usable B2A table.</summary>
        public static IccInkSpace ForCmyk(IccSrgbTransform transform)
        {
            var profile = transform.Profile;
            var lut = profile.GetBToA(1) ?? profile.GetBToA(0);
            if (transform.Channels != 4 || lut == null || lut.OutputChannels != 4)
            {
                return null;
            }

            var black = transform.Settings.BlackPointCompensation
                ? DestinationBlackPoint(profile, lut, transform.BlackPoint)
                : new IccXyz(0, 0, 0);
            var grid = new float[RgbGridPoints * RgbGridPoints * RgbGridPoints * 4];

            // One slab per red node; nodes are independent, so the slabs can run in any order.
            Parallelism.For(RgbGridPoints, RgbGridPoints * RgbGridPoints * 64, delegate(int first, int last)
            {
                var device = new double[4];
                var ink = new float[4];
                for (var red = first; red < last; red++)
                {
                    for (var green = 0; green < RgbGridPoints; green++)
                    {
                        for (var blue = 0; blue < RgbGridPoints; blue++)
                        {
                            float r = red / (float)(RgbGridPoints - 1), g = green / (float)(RgbGridPoints - 1), b = blue / (float)(RgbGridPoints - 1);
                            SrgbToDevice(profile, lut, black, r, g, b, device);
                            for (var c = 0; c < 4; c++)
                            {
                                ink[c] = (float)(1.0 - device[c]);
                            }

                            Refine(transform, r, g, b, ink);
                            var at = ((((red * RgbGridPoints) + green) * RgbGridPoints) + blue) * 4;
                            for (var c = 0; c < 4; c++)
                            {
                                grid[at + c] = ink[c];
                            }
                        }
                    }
                }
            });

            return new IccInkSpace(transform, grid);
        }

        /// <summary>
        /// One encoded sRGB color to device values (ICC convention: ink amounts, 1 = full ink)
        /// through a B2Ax table, as Little CMS's relative colorimetric transform with black
        /// point compensation computes it: sRGB to D50 XYZ, the scaling that maps zero onto the
        /// destination black point, the table's PCS encoding, the table.
        /// </summary>
        internal static void SrgbToDevice(IccProfile profile, IccLut toDevice, IccXyz black, double red, double green, double blue, double[] device)
        {
            var xyz = SrgbToPcs(red, green, blue);

            // Little CMS ComputeBlackPointCompensation with a zero source black (sRGB).
            xyz = new IccXyz(
                black.X + (xyz.X * (IccPcs.D50.X - black.X) / IccPcs.D50.X),
                black.Y + (xyz.Y * (IccPcs.D50.Y - black.Y) / IccPcs.D50.Y),
                black.Z + (xyz.Z * (IccPcs.D50.Z - black.Z) / IccPcs.D50.Z));
            var pcs = new double[3];
            var output = new double[IccLut.MaxChannels];
            IccPcs.Encode(profile.Pcs, toDevice.LabEncoding, xyz, pcs);
            toDevice.Evaluate(pcs, output);
            for (var c = 0; c < device.Length; c++)
            {
                device[c] = Compat.Clamp(output[c], 0.0, 1.0);
            }
        }

        /// <summary>
        /// Newton steps on cyan, magenta and yellow, black kept, until the document's pixel grid
        /// maps the inks back onto the target color; the result is kept only when the error
        /// shrinks (out-of-gamut targets keep the table's gamut mapping).
        /// </summary>
        private static void Refine(IccSrgbTransform transform, float tr, float tg, float tb, float[] ink)
        {
            var x = new double[] { ink[0], ink[1], ink[2] };
            var k = ink[3];
            var e0 = new double[3];
            var e1 = new double[3];
            var probe = new double[3];
            var trial = new double[3];

            Func<double[], double[], double> error = delegate(double[] v, double[] e)
            {
                float r, g, b;
                transform.ConvertCmyk((float)v[0], (float)v[1], (float)v[2], k, out r, out g, out b);
                e[0] = r - tr;
                e[1] = g - tg;
                e[2] = b - tb;
                return (e[0] * e[0]) + (e[1] * e[1]) + (e[2] * e[2]);
            };

            var start = error(x, e0);
            var current = start;
            var jacobian = new double[9];
            for (var iteration = 0; iteration < 12 && current > 1e-12; iteration++)
            {
                const double h = 1.0 / 512;
                for (var j = 0; j < 3; j++)
                {
                    Array.Copy(x, probe, 3);
                    var step = probe[j] + h <= 1 ? h : -h;
                    probe[j] += step;
                    error(probe, e1);
                    for (var i = 0; i < 3; i++)
                    {
                        jacobian[(i * 3) + j] = (e1[i] - e0[i]) / step;
                    }
                }

                double[] inverse;
                try
                {
                    inverse = IccPcs.Invert3x3(jacobian);
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                var improved = false;
                for (var scale = 1.0; scale > 0.05; scale *= 0.5)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var dx = -((inverse[i * 3] * e0[0]) + (inverse[(i * 3) + 1] * e0[1]) + (inverse[(i * 3) + 2] * e0[2]));
                        trial[i] = Compat.Clamp(x[i] + (scale * dx), 0, 1);
                    }

                    var value = error(trial, e1);
                    if (value < current)
                    {
                        Array.Copy(trial, x, 3);
                        Array.Copy(e1, e0, 3);
                        current = value;
                        improved = true;
                        break;
                    }
                }

                if (!improved)
                {
                    break;
                }
            }

            if (current < start)
            {
                ink[0] = (float)x[0];
                ink[1] = (float)x[1];
                ink[2] = (float)x[2];
            }
        }

        /// <summary>
        /// Builds the one-channel form from a converter that fills planes 1..3 with the sRGB
        /// color of the 256 stored levels given in plane 0.
        /// </summary>
        private static IccInkSpace ForGray(Func<float[][], bool> convert)
        {
            var planes = new[] { new float[256], new float[256], new float[256], new float[256] };
            for (var level = 0; level < 256; level++)
            {
                planes[0][level] = level / 255f;
            }

            if (!convert(planes))
            {
                return null;
            }

            var table = new float[256 * 3];
            for (var level = 0; level < 256; level++)
            {
                table[level * 3] = planes[1][level];
                table[(level * 3) + 1] = planes[2][level];
                table[(level * 3) + 2] = planes[3][level];
            }

            return new IccInkSpace(table);
        }

        /// <summary>
        /// Maps a row through the device channels: each sRGB color goes to its device values
        /// (PSD convention, 0..1), <paramref name="map"/> rewrites them in place, and the result
        /// returns to sRGB. One specialized loop per map type; runs of equal input colors reuse
        /// the previous mapping.
        /// </summary>
        public void Apply<TMap>(float[] r, float[] g, float[] b, TMap map)
            where TMap : struct, IInkMap
        {
            if (Channels == 1)
            {
                ApplyGray(r, g, b, map);
                return;
            }

            var count = r.Length;
            var inks = new[] { new float[count], new float[count], new float[count], new float[count] };
            var red = new float[count];
            var green = new float[count];
            var blue = new float[count];
            var ink = new float[4];
            float lastR = float.NaN, lastG = float.NaN, lastB = float.NaN;
            for (var i = 0; i < count; i++)
            {
                if (r[i] != lastR || g[i] != lastG || b[i] != lastB)
                {
                    lastR = r[i];
                    lastG = g[i];
                    lastB = b[i];
                    InkFromRgb(lastR, lastG, lastB, ink);
                    map.Map(ink);
                }

                for (var c = 0; c < 4; c++)
                {
                    inks[c][i] = ink[c];
                }
            }

            _transform.Apply(inks, 8, red, green, blue, count);
            Array.Copy(red, r, count);
            Array.Copy(green, g, count);
            Array.Copy(blue, b, count);
        }

        /// <summary>The four PSD-convention ink values (1 = no ink) of an encoded sRGB color.</summary>
        public void InkFromRgb(float red, float green, float blue, float[] ink)
        {
            const int max = RgbGridPoints - 1;
            var grid = _rgbToInk;
            int ix, iy, iz;
            float fx, fy, fz;
            Locate(red, max, out ix, out fx);
            Locate(green, max, out iy, out fy);
            Locate(blue, max, out iz, out fz);
            const int sz = 4, sy = RgbGridPoints * 4, sx = RgbGridPoints * RgbGridPoints * 4;
            var origin = (ix * sx) + (iy * sy) + (iz * sz);

            // Little CMS's six tetrahedra: walk from the origin along the fractions in decreasing order.
            int a1, a2;
            float t1, t2, t3;
            if (fx >= fy && fy >= fz)
            {
                a1 = sx;
                a2 = sx + sy;
                t1 = fx;
                t2 = fy;
                t3 = fz;
            }
            else if (fx >= fz && fz >= fy)
            {
                a1 = sx;
                a2 = sx + sz;
                t1 = fx;
                t2 = fz;
                t3 = fy;
            }
            else if (fz >= fx && fx >= fy)
            {
                a1 = sz;
                a2 = sx + sz;
                t1 = fz;
                t2 = fx;
                t3 = fy;
            }
            else if (fy >= fx && fx >= fz)
            {
                a1 = sy;
                a2 = sx + sy;
                t1 = fy;
                t2 = fx;
                t3 = fz;
            }
            else if (fy >= fz && fz >= fx)
            {
                a1 = sy;
                a2 = sy + sz;
                t1 = fy;
                t2 = fz;
                t3 = fx;
            }
            else
            {
                a1 = sz;
                a2 = sy + sz;
                t1 = fz;
                t2 = fy;
                t3 = fx;
            }

            var a3 = sx + sy + sz;
            float w0 = 1f - t1, w1 = t1 - t2, w2 = t2 - t3, w3 = t3;
            for (var c = 0; c < 4; c++)
            {
                var o = origin + c;
                ink[c] = (w0 * grid[o]) + (w1 * grid[o + a1]) + (w2 * grid[o + a2]) + (w3 * grid[o + a3]);
            }
        }

        /// <summary>
        /// The gray level (0..255, fractional between stored levels) whose converted color
        /// matches an sRGB color: the forward table read backwards with linear interpolation,
        /// so a stored level comes back exactly.
        /// </summary>
        public float LevelFromRgb(float red, float green, float blue)
        {
            var keys = _levelKey;
            var key = Key(red, green, blue);
            if (_keyMonotonic)
            {
                // First level whose key is at or above the color's, then between it and the one below.
                int low = 0, high = 255;
                while (low < high)
                {
                    var mid = (low + high) >> 1;
                    if (keys[mid] < key)
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                if (low == 0 || key >= keys[low])
                {
                    return low;
                }

                var span = keys[low] - keys[low - 1];
                return span > 0 ? low - 1 + ((key - keys[low - 1]) / span) : low;
            }

            var best = 0;
            var distance = float.MaxValue;
            for (var level = 0; level < 256; level++)
            {
                var d = Math.Abs(keys[level] - key);
                if (d < distance)
                {
                    distance = d;
                    best = level;
                }
            }

            return best;
        }

        /// <summary>The linear luminance of a stored gray level (its converted color, decoded).</summary>
        public double LevelLuminance(int level)
        {
            return IccPcs.DecodeSrgb(_levelKey[level]);
        }

        /// <summary>The stored gray level whose luminance is nearest <paramref name="luminance"/> (compared encoded).</summary>
        public int LevelFromLuminance(double luminance)
        {
            var encoded = (float)IccPcs.EncodeSrgb(luminance);
            return (int)Math.Round(LevelFromRgb(encoded, encoded, encoded));
        }

        private void ApplyGray<TMap>(float[] r, float[] g, float[] b, TMap map)
            where TMap : struct, IInkMap
        {
            var rgb = _levelRgb;
            var value = new float[1];
            for (var i = 0; i < r.Length; i++)
            {
                value[0] = LevelFromRgb(r[i], g[i], b[i]) / 255f;
                map.Map(value);
                var position = Compat.Clamp(value[0], 0f, 1f) * 255f;
                var lower = Math.Min((int)position, 254);
                var t = position - lower;
                var at = lower * 3;
                r[i] = rgb[at] + ((rgb[at + 3] - rgb[at]) * t);
                g[i] = rgb[at + 1] + ((rgb[at + 4] - rgb[at + 1]) * t);
                b[i] = rgb[at + 2] + ((rgb[at + 5] - rgb[at + 2]) * t);
            }
        }

        // A gray profile's colors are neutral, so any channel would do; duotone mixes are
        // colored, where luma keeps the search monotonic for ordinary inks.
        private static float Key(float red, float green, float blue)
        {
            return (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
        }

        private static IccXyz SrgbToPcs(double red, double green, double blue)
        {
            var r = IccPcs.DecodeSrgb(red);
            var g = IccPcs.DecodeSrgb(green);
            var b = IccPcs.DecodeSrgb(blue);
            var m = IccPcs.SrgbToXyzD50;
            return new IccXyz(
                (m[0] * r) + (m[1] * g) + (m[2] * b),
                (m[3] * r) + (m[4] * g) + (m[5] * b),
                (m[6] * r) + (m[7] * g) + (m[8] * b));
        }

        private static void Locate(float value, int max, out int index, out float fraction)
        {
            var position = (value > 0f ? (value < 1f ? value : 1f) : 0f) * max;
            index = Math.Min((int)position, max - 1);
            fraction = position - index;
        }

        /// <summary>
        /// Little CMS cmsDetectDestinationBlackPoint under relative colorimetric for a LUT-based
        /// output profile (Adobe's black point detection): a round trip of an L* ramp through
        /// the profile; when the mid range is nearly straight the input black point stands,
        /// otherwise a least-squares quadratic through the shadow gives its L*.
        /// </summary>
        internal static IccXyz DestinationBlackPoint(IccProfile profile, IccLut toDevice, IccXyz initial)
        {
            var toPcs = profile.InputLut(IccRenderingIntent.RelativeColorimetric);
            if (toPcs == null)
            {
                return initial;
            }

            double initialL, initialA, initialB;
            IccPcs.XyzToLab(initial, out initialL, out initialA, out initialB);
            var a = Compat.Clamp(initialA, -50, 50);
            var b = Compat.Clamp(initialB, -50, 50);
            var inRamp = new double[256];
            var outRamp = new double[256];
            var pcs = new double[3];
            var device = new double[IccLut.MaxChannels];
            for (var l = 0; l < 256; l++)
            {
                inRamp[l] = l * 100.0 / 255.0;
                IccPcs.Encode(profile.Pcs, toDevice.LabEncoding, IccPcs.LabToXyz(inRamp[l], a, b), pcs);
                toDevice.Evaluate(pcs, device);
                toPcs.Evaluate(device, pcs);
                double outL, outA, outB;
                IccPcs.XyzToLab(IccPcs.Decode(profile.Pcs, toPcs.LabEncoding, pcs), out outL, out outA, out outB);
                outRamp[l] = outL;
            }

            for (var l = 254; l > 0; l--)
            {
                outRamp[l] = Math.Min(outRamp[l], outRamp[l + 1]);
            }

            if (!(outRamp[0] < outRamp[255]))
            {
                return new IccXyz(0, 0, 0);
            }

            var minL = outRamp[0];
            var maxL = outRamp[255];
            var straight = true;
            for (var l = 0; l < 256; l++)
            {
                if (!(inRamp[l] <= minL + (0.2 * (maxL - minL)) || Math.Abs(inRamp[l] - outRamp[l]) < 4.0))
                {
                    straight = false;
                }
            }

            if (straight)
            {
                return IccPcs.LabToXyz(initialL, initialA, initialB);
            }

            // Shadow points between 10% and 50% of the normalized round trip.
            var xs = new List<double>();
            var ys = new List<double>();
            for (var l = 0; l < 256; l++)
            {
                var y = (outRamp[l] - minL) / (maxL - minL);
                if (y >= 0.1 && y < 0.5)
                {
                    xs.Add(inRamp[l]);
                    ys.Add(y);
                }
            }

            if (xs.Count < 3)
            {
                return new IccXyz(0, 0, 0);
            }

            var vertex = Math.Max(QuadraticRoot(xs, ys), 0);
            return IccPcs.LabToXyz(vertex, initialA, initialB);
        }

        /// <summary>Little CMS RootOfLeastSquaresFitQuadraticCurve.</summary>
        private static double QuadraticRoot(List<double> x, List<double> y)
        {
            var n = x.Count;
            if (n < 4)
            {
                return 0;
            }

            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, syx = 0, syx2 = 0;
            for (var i = 0; i < n; i++)
            {
                var xn = x[i];
                var yn = y[i];
                sx += xn;
                sx2 += xn * xn;
                sx3 += xn * xn * xn;
                sx4 += xn * xn * xn * xn;
                sy += yn;
                syx += yn * xn;
                syx2 += yn * xn * xn;
            }

            var m = new[] { (double)n, sx, sx2, sx, sx2, sx3, sx2, sx3, sx4 };
            double[] inverse;
            try
            {
                inverse = IccPcs.Invert3x3(m);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }

            var c = (inverse[0] * sy) + (inverse[1] * syx) + (inverse[2] * syx2);
            var bb = (inverse[3] * sy) + (inverse[4] * syx) + (inverse[5] * syx2);
            var aa = (inverse[6] * sy) + (inverse[7] * syx) + (inverse[8] * syx2);
            if (Math.Abs(aa) < 1.0e-10)
            {
                return 0;
            }

            var d = (bb * bb) - (4.0 * aa * c);
            return d <= 0 ? 0 : Math.Max(0, Math.Min(50, (-bb + Math.Sqrt(d)) / (2.0 * aa)));
        }
    }

    /// <summary>
    /// Rewrites one pixel's device values in place: one gray value or four inks, PSD
    /// convention on 0..1 (0 = full ink or black, 1 = no ink or white).
    /// </summary>
    internal interface IInkMap
    {
        void Map(float[] channels);
    }
}
