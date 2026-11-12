using System;
using XRay.PsdFx.Icc;

namespace XRay.PsdFx
{
    /// <summary>
    /// Profile connection space helpers: the D50 white, Lab and XYZ conversions, the LUT
    /// value encodings, and the sRGB output side (Little CMS's built-in sRGB: Rec. 709
    /// primaries with a D65 white, Bradford-adapted to D50, and the IEC 61966-2.1 tone curve).
    /// </summary>
    internal static class IccPcs
    {
        /// <summary>The PCS illuminant as Little CMS defines it.</summary>
        public static readonly IccXyz D50 = new IccXyz(0.9642, 1.0, 0.8249);

        /// <summary>Linear sRGB to XYZ (D50), row major.</summary>
        public static readonly double[] SrgbToXyzD50 = BuildSrgbToXyzD50();

        /// <summary>XYZ (D50) to linear sRGB, row major.</summary>
        public static readonly double[] XyzD50ToSrgb = Invert3x3(SrgbToXyzD50);

        public static IccXyz LabToXyz(double l, double a, double b)
        {
            var fy = (l + 16.0) / 116.0;
            var fx = fy + (a / 500.0);
            var fz = fy - (b / 200.0);
            return new IccXyz(D50.X * InverseF(fx), D50.Y * InverseF(fy), D50.Z * InverseF(fz));
        }

        public static void XyzToLab(IccXyz xyz, out double l, out double a, out double b)
        {
            var fx = F(xyz.X / D50.X);
            var fy = F(xyz.Y / D50.Y);
            var fz = F(xyz.Z / D50.Z);
            l = (116.0 * fy) - 16.0;
            a = 500.0 * (fx - fy);
            b = 200.0 * (fy - fz);
        }

        /// <summary>Decodes a LUT's normalized PCS output to XYZ (D50).</summary>
        public static IccXyz Decode(uint pcs, IccPcsEncoding labEncoding, double[] v)
        {
            if (labEncoding == IccPcsEncoding.Float)
            {
                return pcs == IccProfile.SpaceXyz ? new IccXyz(v[0], v[1], v[2]) : LabToXyz(v[0], v[1], v[2]);
            }

            if (pcs == IccProfile.SpaceXyz)
            {
                const double scale = 65535.0 / 32768.0;
                return new IccXyz(v[0] * scale, v[1] * scale, v[2] * scale);
            }

            double l, a, b;
            if (labEncoding == IccPcsEncoding.LabV2)
            {
                l = v[0] * 65535.0 / 652.80;
                a = (v[1] * 65535.0 / 256.0) - 128.0;
                b = (v[2] * 65535.0 / 256.0) - 128.0;
            }
            else
            {
                l = v[0] * 100.0;
                a = (v[1] * 255.0) - 128.0;
                b = (v[2] * 255.0) - 128.0;
            }

            return LabToXyz(l, a, b);
        }

        /// <summary>Encodes an XYZ (D50) color as a LUT's normalized PCS input.</summary>
        public static void Encode(uint pcs, IccPcsEncoding labEncoding, IccXyz xyz, double[] v)
        {
            if (labEncoding == IccPcsEncoding.Float)
            {
                if (pcs == IccProfile.SpaceXyz)
                {
                    v[0] = xyz.X;
                    v[1] = xyz.Y;
                    v[2] = xyz.Z;
                }
                else
                {
                    double l, a, b;
                    XyzToLab(xyz, out l, out a, out b);
                    v[0] = l;
                    v[1] = a;
                    v[2] = b;
                }

                return;
            }

            if (pcs == IccProfile.SpaceXyz)
            {
                const double scale = 32768.0 / 65535.0;
                v[0] = Compat.Clamp(xyz.X * scale, 0, 1);
                v[1] = Compat.Clamp(xyz.Y * scale, 0, 1);
                v[2] = Compat.Clamp(xyz.Z * scale, 0, 1);
                return;
            }

            double labL, labA, labB;
            XyzToLab(xyz, out labL, out labA, out labB);
            if (labEncoding == IccPcsEncoding.LabV2)
            {
                v[0] = Compat.Clamp(labL * 652.80 / 65535.0, 0, 1);
                v[1] = Compat.Clamp((labA + 128.0) * 256.0 / 65535.0, 0, 1);
                v[2] = Compat.Clamp((labB + 128.0) * 256.0 / 65535.0, 0, 1);
            }
            else
            {
                v[0] = Compat.Clamp(labL / 100.0, 0, 1);
                v[1] = Compat.Clamp((labA + 128.0) / 255.0, 0, 1);
                v[2] = Compat.Clamp((labB + 128.0) / 255.0, 0, 1);
            }
        }

        public static void XyzToLinearSrgb(IccXyz xyz, out double r, out double g, out double b)
        {
            var m = XyzD50ToSrgb;
            r = (m[0] * xyz.X) + (m[1] * xyz.Y) + (m[2] * xyz.Z);
            g = (m[3] * xyz.X) + (m[4] * xyz.Y) + (m[5] * xyz.Z);
            b = (m[6] * xyz.X) + (m[7] * xyz.Y) + (m[8] * xyz.Z);
        }

        /// <summary>The sRGB transfer function (linear to encoded), clamped to [0,1].</summary>
        public static double EncodeSrgb(double linear)
        {
            if (!(linear > 0))
            {
                return 0;
            }

            if (linear >= 1)
            {
                return 1;
            }

            return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
        }

        /// <summary>The inverse sRGB transfer function (encoded to linear).</summary>
        public static double DecodeSrgb(double encoded)
        {
            return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }

        public static double[] Multiply3x3(double[] a, double[] b)
        {
            var result = new double[9];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    result[(row * 3) + column] = (a[row * 3] * b[column]) + (a[(row * 3) + 1] * b[3 + column]) + (a[(row * 3) + 2] * b[6 + column]);
                }
            }

            return result;
        }

        public static double[] Invert3x3(double[] m)
        {
            var a = m[0];
            var b = m[1];
            var c = m[2];
            var d = m[3];
            var e = m[4];
            var f = m[5];
            var g = m[6];
            var h = m[7];
            var i = m[8];
            var determinant = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
            if (Math.Abs(determinant) < 1e-12)
            {
                throw new InvalidOperationException("Singular matrix.");
            }

            var s = 1.0 / determinant;
            return new[]
            {
                ((e * i) - (f * h)) * s, ((c * h) - (b * i)) * s, ((b * f) - (c * e)) * s,
                ((f * g) - (d * i)) * s, ((a * i) - (c * g)) * s, ((c * d) - (a * f)) * s,
                ((d * h) - (e * g)) * s, ((b * g) - (a * h)) * s, ((a * e) - (b * d)) * s,
            };
        }

        /// <summary>
        /// Builds the colorant matrix the way Little CMS's cmsCreate_sRGBProfile does:
        /// primaries to XYZ scaled to the D65 white, then Bradford-adapted to D50.
        /// </summary>
        private static double[] BuildSrgbToXyzD50()
        {
            var xs = new[] { 0.64, 0.30, 0.15 };
            var ys = new[] { 0.33, 0.60, 0.06 };
            const double wx = 0.3127, wy = 0.3290;
            var primaries = new[]
            {
                xs[0], xs[1], xs[2],
                ys[0], ys[1], ys[2],
                1 - xs[0] - ys[0], 1 - xs[1] - ys[1], 1 - xs[2] - ys[2],
            };
            var white = new IccXyz(wx / wy, 1.0, (1 - wx - wy) / wy);
            var inverse = Invert3x3(primaries);
            var coefficients = new double[3];
            for (var row = 0; row < 3; row++)
            {
                coefficients[row] = (inverse[row * 3] * white.X) + (inverse[(row * 3) + 1] * white.Y) + (inverse[(row * 3) + 2] * white.Z);
            }

            var rgbToXyz = new double[9];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    rgbToXyz[(row * 3) + column] = primaries[(row * 3) + column] * coefficients[column];
                }
            }

            return Multiply3x3(BradfordAdaptation(white, D50), rgbToXyz);
        }

        /// <summary>The Bradford chromatic adaptation matrix from one white to another.</summary>
        public static double[] BradfordAdaptation(IccXyz from, IccXyz to)
        {
            var bradford = new[] { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
            var source = new double[3];
            var destination = new double[3];
            for (var row = 0; row < 3; row++)
            {
                source[row] = (bradford[row * 3] * from.X) + (bradford[(row * 3) + 1] * from.Y) + (bradford[(row * 3) + 2] * from.Z);
                destination[row] = (bradford[row * 3] * to.X) + (bradford[(row * 3) + 1] * to.Y) + (bradford[(row * 3) + 2] * to.Z);
            }

            var cone = new[]
            {
                destination[0] / source[0], 0, 0,
                0, destination[1] / source[1], 0,
                0, 0, destination[2] / source[2],
            };
            return Multiply3x3(Invert3x3(bradford), Multiply3x3(cone, bradford));
        }

        private static double F(double t)
        {
            const double limit = 24.0 / 116.0 * (24.0 / 116.0) * (24.0 / 116.0);
            return t <= limit ? (841.0 / 108.0 * t) + (16.0 / 116.0) : Cbrt(t);
        }

        private static double InverseF(double t)
        {
            const double limit = 24.0 / 116.0;
            return t <= limit ? 108.0 / 841.0 * (t - (16.0 / 116.0)) : t * t * t;
        }

        /// <summary>Cube root (net45 has no Math.Cbrt), keeping the sign of the input.</summary>
        private static double Cbrt(double value)
        {
            return value < 0 ? -Math.Pow(-value, 1.0 / 3.0) : Math.Pow(value, 1.0 / 3.0);
        }
    }
}
