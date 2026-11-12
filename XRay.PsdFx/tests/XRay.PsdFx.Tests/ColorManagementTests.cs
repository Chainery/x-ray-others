using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Icc;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// ICC color management: the profile parser and the device-to-sRGB transforms. Expected
    /// sRGB values come from Little CMS 2.19 (relative colorimetric with black point
    /// compensation, the reference's settings) run on the same profile bytes, and from the
    /// reference's Photoshop pins for the SWOP fixture (.reference/docs/ps-compat.md,
    /// pattern_styles_fixtures_tests.cpp).
    /// </summary>
    [TestClass]
    public sealed class ColorManagementTests
    {
        private const string CmykFixture = "photoshop-cmyk-style-colors.psd";

        private struct Rgb
        {
            public int R;
            public int G;
            public int B;

            public Rgb(int r, int g, int b)
            {
                R = r;
                G = g;
                B = b;
            }

            public override string ToString()
            {
                return "(" + R + "," + G + "," + B + ")";
            }
        }

        private static byte[] SwopProfile()
        {
            return Fixtures.Load(CmykFixture).GetImageResource(ImageResourceIds.IccProfile).Data;
        }

        private static IccSrgbTransform Transform(byte[] profile, int channels, IccTransformSettings? settings = null)
        {
            var parsed = IccProfile.Parse(profile);
            Assert.IsNotNull(parsed);
            var transform = IccSrgbTransform.Create(parsed, channels, settings ?? IccTransformSettings.Default);
            Assert.IsNotNull(transform);
            return transform;
        }

        /// <summary>Evaluates 8-bit device values (ICC convention: CMYK is ink) to rounded 8-bit sRGB.</summary>
        private static Rgb Convert(IccSrgbTransform transform, params int[] device)
        {
            var values = new double[device.Length];
            for (var i = 0; i < device.Length; i++)
            {
                values[i] = device[i] / 255.0;
            }

            double r, g, b;
            transform.Evaluate(values, out r, out g, out b);
            return new Rgb((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
        }

        private static void AssertNear(Rgb actual, int r, int g, int b, int tolerance = 1)
        {
            Assert.IsTrue(
                Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
                string.Format("expected ({0},{1},{2}) +-{3}, got {4}", r, g, b, tolerance, actual));
        }

        private static void AssertNear(PsdColor actual, int r, int g, int b, int tolerance = 1)
        {
            AssertNear(new Rgb(actual.R, actual.G, actual.B), r, g, b, tolerance);
        }

        [TestMethod]
        public void Swop_profile_header_and_tags_parse()
        {
            var profile = IccProfile.Parse(SwopProfile());

            Assert.IsNotNull(profile);
            Assert.AreEqual("U.S. Web Coated (SWOP) v2", profile.Description);
            Assert.AreEqual(2, profile.MajorVersion);
            Assert.AreEqual(1, profile.MinorVersion);
            Assert.AreEqual(IccProfile.ClassOutput, profile.DeviceClass);
            Assert.AreEqual(IccProfile.SpaceCmyk, profile.ColorSpace);
            Assert.AreEqual(IccProfile.SpaceLab, profile.Pcs);
            Assert.AreEqual(4, profile.DeviceChannels);
            Assert.AreEqual("mft2", profile.GetAToB(0).Type);
            Assert.AreEqual("mft2", profile.GetAToB(1).Type);
            Assert.AreEqual("mft1", profile.GetBToA(0).Type);
            Assert.AreEqual(IccPcsEncoding.LabV2, profile.GetAToB(1).LabEncoding);
            CollectionAssert.Contains(new List<string>(profile.TagSignatures), "gamt");
            Assert.IsNotNull(profile.MediaWhitePoint);
            Assert.IsFalse(profile.IsMatrixShaper);
        }

        [TestMethod]
        public void Matrix_trc_profile_parses_colorants_and_gamma()
        {
            var profile = IccProfile.Parse(TestProfiles.AdobeRgb());

            Assert.AreEqual("Test Adobe RGB (1998)", profile.Description);
            Assert.IsTrue(profile.IsMatrixShaper);
            Assert.AreEqual(TestProfiles.AdobeGreen[1], profile.GreenColorant.Value.Y, 0.0001);
            Assert.AreEqual(Math.Pow(0.5, 563 / 256.0), profile.RedTrc.Evaluate(0.5), 0.000001);
            Assert.IsNull(profile.GetAToB(0));
        }

        [TestMethod]
        public void V4_profile_reads_mluc_chad_and_mAB()
        {
            var profile = IccProfile.Parse(TestProfiles.AdobeRgbV4Lut());

            Assert.AreEqual(4, profile.MajorVersion);
            Assert.AreEqual("Test Adobe RGB v4 LUT", profile.Description);
            CollectionAssert.AreEqual(
                new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
                profile.ChromaticAdaptation);
            Assert.AreEqual("mAB", profile.GetAToB(0).Type);
            Assert.IsFalse(profile.IsMatrixShaper);
        }

        [TestMethod]
        public void Parametric_curves_follow_the_icc_formulas()
        {
            // Type 4 with the sRGB parameters is the sRGB decoding curve.
            var srgb = IccCurve.FromParametric(4, new[] { 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0.0, 0.0 });
            var gamma = IccCurve.FromParametric(0, new[] { 2.2 });
            var type1 = IccCurve.FromParametric(1, new[] { 2.0, 2.0, -1.0 });
            var type2 = IccCurve.FromParametric(2, new[] { 1.0, 1.0, 0.0, 0.25 });
            var type3 = IccCurve.FromParametric(3, new[] { 1.0, 1.0, 0.0, 0.5, 0.5 });
            foreach (var x in new[] { 0.0, 0.01, 0.04, 0.2, 0.5, 0.9, 1.0 })
            {
                Assert.AreEqual(IccPcs.DecodeSrgb(x), srgb.Evaluate(x), 1e-9);
                Assert.AreEqual(Math.Pow(x, 2.2), gamma.Evaluate(x), 1e-9);
                Assert.AreEqual(x >= 0.5 ? Math.Pow((2 * x) - 1, 2) : 0, type1.Evaluate(x), 1e-9);
                Assert.AreEqual(x + 0.25, type2.Evaluate(x), 1e-9);
                Assert.AreEqual(x >= 0.5 ? x : 0.5 * x, type3.Evaluate(x), 1e-9);
            }

            var table = IccCurve.FromTable(new[] { 0f, 0.25f, 1f });
            Assert.AreEqual(0.125, table.Evaluate(0.25), 0.000001);
            Assert.AreEqual(0.625, table.Evaluate(0.75), 0.000001);
        }

        [TestMethod]
        public void Unusable_profiles_are_rejected_without_throwing()
        {
            Assert.IsNull(IccProfile.Parse(new byte[] { 1, 2, 3, 4 }));
            Assert.IsNull(IccProfile.Parse(new byte[0]));
            var swop = SwopProfile();

            // A truncated profile loses its LUTs, so there is no CMYK conversion.
            var truncatedBytes = new byte[2000];
            Array.Copy(swop, truncatedBytes, 2000);
            var truncated = IccProfile.Parse(truncatedBytes);
            Assert.IsTrue(truncated == null || IccSrgbTransform.Create(truncated, 4, IccTransformSettings.Default) == null);

            // Wrong color space for the document mode.
            Assert.IsNull(IccSrgbTransform.Create(IccProfile.Parse(TestProfiles.AdobeRgb()), 4, IccTransformSettings.Default));
            Assert.IsNull(IccSrgbTransform.Create(IccProfile.Parse(swop), 3, IccTransformSettings.Default));

            // Random corruption anywhere in the header, tag table or tag data must degrade, never throw.
            var random = new Random(7);
            foreach (var source in new[] { swop, TestProfiles.AdobeRgb(), TestProfiles.AdobeRgbV4Lut(), TestProfiles.SyntheticCmyk(), TestProfiles.GrayLiftedBlack() })
            {
                for (var trial = 0; trial < 60; trial++)
                {
                    var damaged = (byte[])source.Clone();
                    for (var flips = 0; flips < 8; flips++)
                    {
                        var at = trial % 2 == 0 ? random.Next(Math.Min(damaged.Length, 400)) : random.Next(damaged.Length);
                        damaged[at] = (byte)random.Next(256);
                    }

                    var profile = IccProfile.Parse(damaged);
                    if (profile != null)
                    {
                        var transform = IccSrgbTransform.Create(profile, profile.DeviceChannels, IccTransformSettings.Default);
                        if (transform != null && (transform.Channels == 1 || transform.Channels == 3 || transform.Channels == 4))
                        {
                            double r, g, b;
                            transform.Evaluate(new double[transform.Channels], out r, out g, out b);
                        }
                    }
                }
            }
        }

        [DataTestMethod]
        [DataRow(255, 0, 0, 255, 0, 0)]
        [DataRow(0, 255, 0, 0, 255, 0)]
        [DataRow(0, 0, 255, 0, 0, 255)]
        [DataRow(128, 128, 128, 129, 129, 129)]
        [DataRow(64, 128, 192, 0, 129, 196)]
        [DataRow(10, 20, 30, 0, 12, 24)]
        [DataRow(0, 0, 0, 0, 0, 0)]
        [DataRow(255, 255, 255, 255, 255, 255)]
        public void Adobe_rgb_converts_like_lcms(int r, int g, int b, int er, int eg, int eb)
        {
            AssertNear(Convert(Transform(TestProfiles.AdobeRgb(), 3), r, g, b), er, eg, eb);
        }

        [TestMethod]
        public void Adobe_rgb_green_is_outside_the_srgb_gamut()
        {
            var transform = Transform(TestProfiles.AdobeRgb(), 3);
            double red, green, blue;
            transform.EvaluateLinear(new double[] { 0, 1, 0 }, out red, out green, out blue);

            Assert.IsTrue(red >= -0.42 && red <= -0.37, "red " + red);
            Assert.IsTrue(green >= 0.99 && green <= 1.01, "green " + green);
            Assert.IsTrue(blue >= -0.06 && blue <= -0.03, "blue " + blue);

            // Adobe RGB shares sRGB's red primary, with a higher luminance: it clips to pure red.
            transform.EvaluateLinear(new double[] { 1, 0, 0 }, out red, out green, out blue);
            Assert.IsTrue(red >= 1.3 && red <= 1.5, "red " + red);
            Assert.IsTrue(Math.Abs(green) + Math.Abs(blue) <= 0.002);
            Assert.IsFalse(transform.IsSrgbEquivalent);
        }

        [TestMethod]
        public void V4_matrix_shaper_lut_matches_the_matrix_trc_profile()
        {
            var lut = Transform(TestProfiles.AdobeRgbV4Lut(), 3);
            var matrix = Transform(TestProfiles.AdobeRgb(), 3);

            Assert.IsTrue(lut.UsesLut);
            Assert.IsTrue(lut.IsMatrixShaper);
            var random = new Random(3);
            for (var i = 0; i < 500; i++)
            {
                var device = new[] { random.NextDouble(), random.NextDouble(), random.NextDouble() };
                double r1, g1, b1, r2, g2, b2;
                lut.Evaluate(device, out r1, out g1, out b1);
                matrix.Evaluate(device, out r2, out g2, out b2);
                Assert.IsTrue(Math.Abs(r1 - r2) < 0.6 / 255 && Math.Abs(g1 - g2) < 0.6 / 255 && Math.Abs(b1 - b2) < 0.6 / 255);
            }
        }

        [DataTestMethod]
        [DataRow(0, 0)]
        [DataRow(32, 26)]
        [DataRow(64, 62)]
        [DataRow(128, 129)]
        [DataRow(192, 193)]
        [DataRow(255, 255)]
        public void Gray_gamma_profile_converts_like_lcms(int gray, int expected)
        {
            var result = Convert(Transform(TestProfiles.GrayGamma22(), 1), gray);

            Assert.AreEqual(expected, result.R);
            Assert.AreEqual(result.R, result.G);
            Assert.AreEqual(result.R, result.B);
        }

        [TestMethod]
        public void Black_point_compensation_maps_a_lifted_gray_black_to_srgb_black()
        {
            var compensated = Transform(TestProfiles.GrayLiftedBlack(), 1);
            var plain = Transform(TestProfiles.GrayLiftedBlack(), 1, new IccTransformSettings(IccRenderingIntent.RelativeColorimetric, false));

            // lcms cmsDetectBlackPoint: (0.048214, 0.050004, 0.041248).
            Assert.AreEqual(0.050004, compensated.BlackPoint.Y, 0.0001);
            Assert.AreEqual(0.048214, compensated.BlackPoint.X, 0.0001);
            AssertNear(Convert(compensated, 0), 0, 0, 0, 0);
            AssertNear(Convert(compensated, 128), 129, 129, 129);
            AssertNear(Convert(plain, 0), 63, 63, 63);
            Assert.AreEqual(0.0, plain.BlackPoint.X);
            Assert.AreEqual(0.0, plain.BlackPoint.Y);
            Assert.AreEqual(0.0, plain.BlackPoint.Z);
        }

        [TestMethod]
        public void Cmyk_lut_profile_uses_the_perceptual_black_round_trip()
        {
            var transform = Transform(TestProfiles.SyntheticCmyk(), 4);

            // lcms: (0.016742, 0.017364, 0.014323), the relative Lab of B2A0's rich black.
            Assert.AreEqual(0.016742, transform.BlackPoint.X, 0.0001);
            Assert.AreEqual(0.017364, transform.BlackPoint.Y, 0.0001);
            Assert.AreEqual(0.014323, transform.BlackPoint.Z, 0.0001);
            AssertNear(Convert(transform, 0, 0, 0, 0), 255, 255, 255);
            AssertNear(Convert(transform, 0, 0, 0, 255), 88, 88, 88);
            AssertNear(Convert(transform, 255, 255, 255, 255), 32, 0, 0);
            AssertNear(Convert(transform, 0, 255, 255, 0), 255, 143, 55);
            AssertNear(Convert(transform, 128, 128, 128, 128), 147, 114, 84);
            AssertNear(Convert(transform, 64, 32, 200, 10), 238, 220, 125);

            // Without B2A0 lcms finds no perceptual black and compensation is a no-op.
            var plain = Transform(TestProfiles.SyntheticCmyk(false), 4);
            Assert.AreEqual(0.0, plain.BlackPoint.X);
            Assert.AreEqual(0.0, plain.BlackPoint.Y);
            Assert.AreEqual(0.0, plain.BlackPoint.Z);
            AssertNear(Convert(plain, 0, 0, 0, 255), 94, 94, 94);
            AssertNear(Convert(plain, 255, 255, 255, 255), 50, 0, 0);
        }

        [DataTestMethod]
        [DataRow(0, 0, 0, 0, 255, 255, 255)]
        [DataRow(0, 0, 0, 255, 35, 31, 32)]
        [DataRow(255, 255, 255, 255, 0, 0, 0)]
        [DataRow(0, 255, 255, 0, 237, 28, 36)]
        [DataRow(110, 0, 250, 0, 158, 204, 62)]
        [DataRow(107, 115, 171, 33, 143, 123, 92)]
        [DataRow(255, 0, 0, 0, 0, 174, 239)]
        [DataRow(0, 255, 0, 0, 236, 0, 140)]
        [DataRow(0, 0, 255, 0, 255, 242, 0)]
        [DataRow(128, 128, 128, 128, 82, 74, 72)]
        [DataRow(64, 32, 200, 10, 191, 191, 90)]
        public void Swop_conversion_matches_photoshop_and_lcms(int c, int m, int y, int k, int r, int g, int b)
        {
            // Rows 2 to 6 are the reference's Photoshop pins; the rest are lcms 2.19.
            AssertNear(Convert(Transform(SwopProfile(), 4), c, m, y, k), r, g, b);
        }

        private static readonly IccTransformSettings Absolute = new IccTransformSettings(IccRenderingIntent.AbsoluteColorimetric, true);

        [DataTestMethod]
        [DataRow(0, 0, 0, 0, 225, 223, 216)]
        [DataRow(0, 0, 0, 255, 47, 45, 44)]
        [DataRow(0, 255, 255, 0, 207, 44, 45)]
        [DataRow(110, 0, 250, 0, 140, 179, 59)]
        [DataRow(255, 0, 0, 0, 0, 153, 203)]
        [DataRow(64, 32, 200, 10, 168, 168, 80)]
        public void Absolute_colorimetric_keeps_the_swop_paper_white(int c, int m, int y, int k, int r, int g, int b)
        {
            // lcms 2.19, INTENT_ABSOLUTE_COLORIMETRIC: A2B1 scaled by the media white over D50, no black point compensation.
            var transform = Transform(SwopProfile(), 4, Absolute);

            Assert.AreEqual(0.0, transform.BlackPoint.X);
            AssertNear(Convert(transform, c, m, y, k), r, g, b);
        }

        private static IccBuilder WithAdobeColorants(IccBuilder builder)
        {
            return builder
                .Add("rXYZ", IccBuilder.Xyz(TestProfiles.AdobeRed[0], TestProfiles.AdobeRed[1], TestProfiles.AdobeRed[2]))
                .Add("gXYZ", IccBuilder.Xyz(TestProfiles.AdobeGreen[0], TestProfiles.AdobeGreen[1], TestProfiles.AdobeGreen[2]))
                .Add("bXYZ", IccBuilder.Xyz(TestProfiles.AdobeBlue[0], TestProfiles.AdobeBlue[1], TestProfiles.AdobeBlue[2]))
                .Add("rTRC", IccBuilder.Gamma(563 / 256.0))
                .Add("gTRC", IccBuilder.Gamma(563 / 256.0))
                .Add("bTRC", IccBuilder.Gamma(563 / 256.0));
        }

        [TestMethod]
        public void Absolute_colorimetric_scales_by_the_media_white_like_lcms()
        {
            // A paper-white gray output profile: lcms tints even the gray axis, through the planar tables too.
            var gray = new IccBuilder { DeviceClass = "prtr", ColorSpace = "GRAY" }
                .Add("desc", IccBuilder.Description("Test Paper Gray"))
                .Add("wtpt", IccBuilder.Xyz(0.88, 0.9, 0.70))
                .Add("kTRC", IccBuilder.Gamma(2.2))
                .Build();
            var absoluteGray = Transform(gray, 1, Absolute);
            AssertNear(Convert(absoluteGray, 64), 60, 58, 57);
            AssertNear(Convert(absoluteGray, 128), 127, 122, 119);
            AssertNear(Convert(absoluteGray, 255), 250, 242, 236);
            AssertNear(Convert(Transform(gray, 1), 128), 129, 129, 129);
            var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
            Assert.IsTrue(absoluteGray.Apply(new[] { new[] { 128 / 255f } }, 8, image));
            AssertNear(
                new Rgb((int)Math.Round(image.R[0] * 255), (int)Math.Round(image.G[0] * 255), (int)Math.Round(image.B[0] * 255)),
                127, 122, 119);

            // An input-class RGB profile uses its white; a v2 display profile counts as D50 (no change).
            var input = WithAdobeColorants(new IccBuilder { DeviceClass = "scnr" }.Add("wtpt", IccBuilder.Xyz(0.9, 0.95, 0.75))).Build();
            var display = WithAdobeColorants(new IccBuilder().Add("wtpt", IccBuilder.Xyz(0.9, 0.95, 0.75))).Build();
            AssertNear(Convert(Transform(input, 3, Absolute), 255, 255, 255), 245, 251, 244);
            AssertNear(Convert(Transform(input, 3, Absolute), 64, 128, 192), 0, 127, 187);
            AssertNear(Convert(Transform(input, 3, Absolute), 128, 128, 128), 124, 127, 123);
            AssertNear(Convert(Transform(display, 3, Absolute), 64, 128, 192), 0, 129, 196);
        }

        /// <summary>A v4 profile whose only conversion is a float <c>D2B0</c>/<c>D2B1</c> pair (Little CMS reads no D2B0 fallback for relative).</summary>
        private static byte[] FloatProfile(string deviceClass, string space, string pcs, byte[] mpet)
        {
            return new IccBuilder { Version = 0x04300000, DeviceClass = deviceClass, ColorSpace = space, Pcs = pcs }
                .Add("desc", IccBuilder.Description("Test float"))
                .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
                .Add("D2B0", mpet)
                .Add("D2B1", mpet)
                .Build();
        }

        [DataTestMethod]
        [DataRow(255, 255, 255, 255, 255, 255)]
        [DataRow(64, 128, 192, 0, 129, 196)]
        [DataRow(128, 128, 128, 129, 129, 129)]
        [DataRow(10, 20, 30, 1, 12, 24)]
        [DataRow(0, 255, 0, 0, 255, 0)]
        public void Float_rgb_profile_with_formula_curves_and_a_matrix_converts_like_lcms(int r, int g, int b, int er, int eg, int eb)
        {
            // Adobe RGB as an mpet: three-segment parametric curves (0 below 0, x^2.2, then x), then a matf.
            var curve = IccBuilder.SegmentedCurve(
                new[] { 0f, 1f },
                IccBuilder.FormulaSegment(0, 1, 0, 0, 0),
                IccBuilder.FormulaSegment(0, 2.2f, 1, 0, 0),
                IccBuilder.FormulaSegment(0, 1, 1, 0, 0));
            var matrix = new[]
            {
                (float)TestProfiles.AdobeRed[0], (float)TestProfiles.AdobeGreen[0], (float)TestProfiles.AdobeBlue[0],
                (float)TestProfiles.AdobeRed[1], (float)TestProfiles.AdobeGreen[1], (float)TestProfiles.AdobeBlue[1],
                (float)TestProfiles.AdobeRed[2], (float)TestProfiles.AdobeGreen[2], (float)TestProfiles.AdobeBlue[2],
            };
            var mpet = IccBuilder.MultiProcess(
                3,
                3,
                IccBuilder.CurveSetElement(curve, curve, curve),
                IccBuilder.MatrixElement(3, 3, matrix, new float[] { 0, 0, 0 }));
            var transform = Transform(FloatProfile("mntr", "RGB ", "XYZ ", mpet), 3);

            Assert.AreEqual(
                "mpet",
                IccProfile.Parse(FloatProfile("mntr", "RGB ", "XYZ ", mpet)).InputLut(IccRenderingIntent.RelativeColorimetric).Type);
            AssertNear(Convert(transform, r, g, b), er, eg, eb);
        }

        [TestMethod]
        public void Float_cmyk_and_gray_profiles_with_sampled_curves_and_a_clut_convert_like_lcms()
        {
            // Sampled segment on (0, 1] (implicit 0 at x = 0), constant 1 above, 0 below.
            var sampled = IccBuilder.SegmentedCurve(
                new[] { 0f, 1f },
                IccBuilder.FormulaSegment(0, 1, 0, 0, 0),
                IccBuilder.SampledSegment(0.1f, 0.35f, 0.7f, 1f),
                IccBuilder.FormulaSegment(0, 1, 0, 1, 0));
            var values = new List<float>();
            for (var node = 0; node < 81; node++)
            {
                int c = node / 27, m = node / 9 % 3, y = node / 3 % 3, k = node % 3;
                double l, a, b;
                TestProfiles.PressLab(c / 2.0, m / 2.0, y / 2.0, k / 2.0, out l, out a, out b);
                values.Add((float)l);
                values.Add((float)a);
                values.Add((float)b);
            }

            var cmyk = IccBuilder.MultiProcess(
                4,
                3,
                IccBuilder.CurveSetElement(sampled, sampled, sampled, sampled),
                IccBuilder.ClutElement(4, 3, new byte[] { 3, 3, 3, 3 }, values.ToArray()));
            var press = Transform(FloatProfile("prtr", "CMYK", "Lab ", cmyk), 4);
            AssertNear(Convert(press, 0, 0, 0, 0), 255, 255, 255);
            AssertNear(Convert(press, 0, 0, 0, 255), 94, 94, 94);
            AssertNear(Convert(press, 255, 255, 255, 255), 50, 0, 0);
            AssertNear(Convert(press, 0, 255, 255, 0), 255, 146, 67);
            AssertNear(Convert(press, 128, 128, 128, 128), 181, 157, 137);
            AssertNear(Convert(press, 64, 32, 200, 10), 255, 234, 139);

            var graySampled = IccBuilder.SegmentedCurve(
                new[] { 0f, 1f },
                IccBuilder.FormulaSegment(0, 1, 0, 0, 0),
                IccBuilder.SampledSegment(0.01f, 0.05f, 0.2f, 0.5f, 1f),
                IccBuilder.FormulaSegment(0, 1, 0, 1, 0));
            var gray = Transform(
                FloatProfile(
                    "mntr",
                    "GRAY",
                    "XYZ ",
                    IccBuilder.MultiProcess(
                        1,
                        3,
                        IccBuilder.CurveSetElement(graySampled),
                        IccBuilder.MatrixElement(1, 3, new[] { 0.9642f, 1f, 0.8249f }, new float[] { 0, 0, 0 }))),
                1);
            foreach (var sample in new[] { new[] { 0, 0 }, new[] { 32, 18 }, new[] { 64, 39 }, new[] { 128, 100 }, new[] { 200, 184 }, new[] { 255, 255 } })
            {
                AssertNear(Convert(gray, sample[0]), sample[1], sample[1], sample[1]);
            }

            // Through the planar CMYK grid too, as pixels would.
            var planes = new[] { new[] { 127 / 255f }, new[] { 127 / 255f }, new[] { 127 / 255f }, new[] { 127 / 255f } };
            var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
            Assert.IsTrue(press.Apply(planes, 8, image));
            AssertNear(
                new Rgb((int)Math.Round(image.R[0] * 255), (int)Math.Round(image.G[0] * 255), (int)Math.Round(image.B[0] * 255)),
                181, 157, 137);
        }

        [TestMethod]
        public void Damaged_float_luts_are_dropped()
        {
            var curve = IccBuilder.SegmentedCurve(new float[0], IccBuilder.FormulaSegment(0, 1, 1, 0, 0));
            var good = IccBuilder.MultiProcess(
                1,
                3,
                IccBuilder.CurveSetElement(curve),
                IccBuilder.MatrixElement(1, 3, new[] { 0.9642f, 1f, 0.8249f }, new float[] { 0, 0, 0 }));
            Assert.IsNotNull(IccLut.Parse(good));

            // Truncated anywhere, or with an element whose channel count does not chain, the tag is ignored.
            for (var length = 0; length < good.Length; length++)
            {
                var truncated = new byte[length];
                Array.Copy(good, truncated, length);
                Assert.IsNull(IccLut.Parse(truncated));
            }

            var mismatched = IccBuilder.MultiProcess(
                1,
                3,
                IccBuilder.CurveSetElement(curve, curve),
                IccBuilder.MatrixElement(1, 3, new[] { 1f, 1f, 1f }, new float[] { 0, 0, 0 }));
            Assert.IsNull(IccLut.Parse(mismatched));

            // Random corruption must degrade, never throw.
            var profile = FloatProfile("mntr", "GRAY", "XYZ ", good);
            var random = new Random(5);
            for (var trial = 0; trial < 300; trial++)
            {
                var damaged = (byte[])profile.Clone();
                for (var flips = 0; flips < 4; flips++)
                {
                    damaged[random.Next(128, damaged.Length)] = (byte)random.Next(256);
                }

                var parsed = IccProfile.Parse(damaged);
                var transform = parsed == null ? null : IccSrgbTransform.Create(parsed, 1, IccTransformSettings.Default);
                if (transform != null)
                {
                    double r, g, b;
                    transform.Evaluate(new[] { 0.5 }, out r, out g, out b);
                    Assert.IsTrue(transform.Apply(new[] { new[] { 0f, 0.5f, 1f } }, 8, new PlanarImage(new PsdRect(0, 0, 3, 1))));
                }
            }
        }

        [TestMethod]
        public void Swop_black_point_matches_lcms()
        {
            var transform = Transform(SwopProfile(), 4);

            Assert.AreEqual(0.021379, transform.BlackPoint.X, 0.00001);
            Assert.AreEqual(0.022173, transform.BlackPoint.Y, 0.00001);
            Assert.AreEqual(0.018290, transform.BlackPoint.Z, 0.00001);
            Assert.IsTrue(transform.UsesLut);
        }

        /// <summary>Random SWOP inks (C, M, Y, K) and the sRGB lcms 2.19 converts them to.</summary>
        private static readonly int[][] SwopLcmsSamples =
        {
            new[] { 130, 183, 14, 238, 21, 0, 28 },
            new[] { 127, 26, 80, 57, 105, 154, 148 },
            new[] { 190, 240, 126, 194, 31, 0, 29 },
            new[] { 52, 127, 6, 110, 129, 91, 123 },
            new[] { 208, 143, 93, 199, 4, 31, 46 },
            new[] { 81, 36, 71, 227, 39, 48, 45 },
            new[] { 64, 67, 0, 2, 186, 180, 217 },
            new[] { 107, 110, 84, 85, 113, 104, 111 },
            new[] { 148, 160, 101, 104, 84, 70, 86 },
            new[] { 93, 100, 196, 152, 86, 76, 40 },
            new[] { 11, 184, 212, 84, 167, 76, 41 },
            new[] { 74, 135, 33, 169, 84, 58, 79 },
            new[] { 154, 1, 173, 33, 92, 171, 113 },
            new[] { 158, 181, 156, 246, 4, 0, 0 },
            new[] { 161, 94, 246, 241, 0, 16, 0 },
            new[] { 90, 29, 131, 11, 164, 186, 140 },
            new[] { 183, 206, 9, 214, 21, 0, 46 },
            new[] { 187, 192, 4, 231, 6, 0, 35 },
            new[] { 23, 92, 100, 60, 182, 138, 119 },
            new[] { 125, 236, 176, 181, 60, 5, 22 },
            new[] { 128, 236, 55, 188, 57, 2, 47 },
            new[] { 151, 18, 221, 46, 97, 154, 74 },
            new[] { 106, 174, 185, 75, 122, 78, 63 },
            new[] { 174, 141, 47, 159, 45, 53, 80 },
        };

        /// <summary>
        /// The planar path samples the conversion on Little CMS's own 17-node CMYK grid with its
        /// interpolation, so it tracks lcms (offline: 99.4% of 20,000 random SWOP colors
        /// byte-exact, never more than one level off) rather than the unsampled double-precision
        /// evaluation, which differs by up to 13 levels where heavy ink clips at the sRGB gamut
        /// boundary.
        /// </summary>
        [DataTestMethod]
        [DataRow(8)]
        [DataRow(16)]
        public void Planar_cmyk_conversion_matches_lcms(int depth)
        {
            var transform = Transform(SwopProfile(), 4);
            var count = SwopLcmsSamples.Length;
            var planes = new float[4][];
            for (var c = 0; c < 4; c++)
            {
                planes[c] = new float[count];
                for (var i = 0; i < count; i++)
                {
                    // PSD planes store CMYK inverted; 16-bit samples scale by 257.
                    var stored = 255 - SwopLcmsSamples[i][c];
                    planes[c][i] = depth == 8 ? stored / 255f : stored * 257 / 65535f;
                }
            }

            var image = new PlanarImage(new PsdRect(0, 0, count, 1));
            Assert.IsTrue(transform.Apply(planes, depth, image));
            for (var i = 0; i < count; i++)
            {
                image.A[i] = 1f;
            }

            var rgba = image.ToRgba(image.Bounds);
            for (var i = 0; i < count; i++)
            {
                var expected = SwopLcmsSamples[i];
                AssertNear(rgba.GetPixel(i, 0), expected[4], expected[5], expected[6]);
            }
        }

        [TestMethod]
        public void Planar_matrix_and_gray_paths_match_the_exact_conversion()
        {
            var rgb = Transform(TestProfiles.AdobeRgb(), 3);
            var gray = Transform(TestProfiles.GrayLiftedBlack(), 1);
            var count = 256 * 3;
            var planes = new[] { new float[count], new float[count], new float[count] };
            for (var i = 0; i < count; i++)
            {
                planes[0][i] = (i % 256) / 255f;
                planes[1][i] = ((i * 7) % 256) / 255f;
                planes[2][i] = ((i * 13) % 256) / 255f;
            }

            var image = new PlanarImage(new PsdRect(0, 0, count, 1));
            Assert.IsTrue(rgb.Apply(planes, 8, image));
            var grayImage = new PlanarImage(new PsdRect(0, 0, count, 1));
            Assert.IsTrue(gray.Apply(new[] { planes[0] }, 8, grayImage));
            for (var i = 0; i < count; i++)
            {
                double r, g, b;
                rgb.Evaluate(new[] { (double)planes[0][i], planes[1][i], planes[2][i] }, out r, out g, out b);
                Assert.AreEqual(r, image.R[i], 0.0005);
                Assert.AreEqual(g, image.G[i], 0.0005);
                Assert.AreEqual(b, image.B[i], 0.0005);
                double k, _, __;
                gray.Evaluate(new[] { (double)planes[0][i] }, out k, out _, out __);
                Assert.AreEqual(k, grayImage.R[i], 0.0001);
                Assert.AreEqual(grayImage.R[i], grayImage.B[i]);
            }
        }

        [TestMethod]
        public void Swop_destination_black_point_matches_lcms()
        {
            // cmsDetectDestinationBlackPoint(SWOP, relative colorimetric) through ctypes: the
            // round trip's mid range is straight, so the input black point stands.
            var transform = Transform(SwopProfile(), 4);
            var profile = transform.Profile;
            var black = IccInkSpace.DestinationBlackPoint(profile, profile.GetBToA(1), transform.BlackPoint);

            Assert.AreEqual(0.021379, black.X, 0.00001);
            Assert.AreEqual(0.022173, black.Y, 0.00001);
            Assert.AreEqual(0.018290, black.Z, 0.00001);
        }

        /// <summary>
        /// sRGB grid nodes (in 32nds) to SWOP inks: lcms 2.19's sRGB to CMYK transform (relative
        /// colorimetric, black point compensation, TYPE_CMYK_16_REV), the reference's
        /// <c>build_cmyk_ink_space</c> settings.
        /// </summary>
        [DataTestMethod]
        [DataRow(32, 32, 32, 65535, 65535, 65535, 65535)]
        [DataRow(0, 0, 0, 18171, 21178, 21565, 7613)]
        [DataRow(16, 16, 16, 32104, 36909, 36843, 60403)]
        [DataRow(32, 0, 0, 65535, 140, 0, 65535)]
        [DataRow(0, 32, 0, 24596, 65535, 0, 65535)]
        [DataRow(0, 0, 32, 8016, 15099, 65535, 65535)]
        [DataRow(25, 12, 6, 54850, 16915, 2668, 62734)]
        [DataRow(4, 8, 11, 6289, 19435, 36752, 43262)]
        [DataRow(20, 28, 5, 38308, 65535, 0, 65535)]
        public void Srgb_to_swop_inks_matches_lcms(int r, int g, int b, int c, int m, int y, int k)
        {
            var transform = Transform(SwopProfile(), 4);
            var profile = transform.Profile;
            var lut = profile.GetBToA(1);
            var black = IccInkSpace.DestinationBlackPoint(profile, lut, transform.BlackPoint);
            var device = new double[4];
            IccInkSpace.SrgbToDevice(profile, lut, black, r / 32.0, g / 32.0, b / 32.0, device);

            var expected = new[] { c, m, y, k };
            for (var channel = 0; channel < 4; channel++)
            {
                // Stored convention (65535 = no ink), within two 8-bit levels (black, the steepest corner, is 1.8 off).
                var actual = (1 - device[channel]) * 65535;
                Assert.IsTrue(
                    actual >= expected[channel] - 514 && actual <= expected[channel] + 514,
                    string.Format("channel {0}: expected {1}, got {2:F0}", channel, expected[channel], actual));
            }
        }

        [TestMethod]
        public void Swop_ink_space_inverts_the_pixel_conversion()
        {
            var transform = Transform(SwopProfile(), 4);
            var space = IccInkSpace.ForCmyk(transform);
            Assert.IsNotNull(space);
            Assert.AreEqual(4, space.Channels);

            // Inks the profile itself separates (as a Photoshop RGB to CMYK conversion would),
            // stored as bytes, converted to sRGB as pixels are, then taken back to the inks.
            var profile = transform.Profile;
            var lut = profile.GetBToA(1);
            var black = IccInkSpace.DestinationBlackPoint(profile, lut, transform.BlackPoint);
            var device = new double[4];
            var ink = new float[4];
            var worstColor = 0f;
            var worstInk = 0f;
            var samples = new[]
            {
                new[] { 0.8, 0.6, 0.4 }, new[] { 0.3, 0.7, 0.9 }, new[] { 0.5, 0.5, 0.5 },
                new[] { 0.9, 0.2, 0.3 }, new[] { 0.2, 0.25, 0.2 }, new[] { 0.95, 0.9, 0.7 },
            };
            foreach (var sample in samples)
            {
                IccInkSpace.SrgbToDevice(profile, lut, black, sample[0], sample[1], sample[2], device);
                var psd = new float[4];
                for (var c = 0; c < 4; c++)
                {
                    psd[c] = (float)Math.Round((1 - device[c]) * 255f) / 255f;
                }

                float r, g, b, r2, g2, b2;
                Assert.IsTrue(transform.ConvertCmyk(psd[0], psd[1], psd[2], psd[3], out r, out g, out b));
                space.InkFromRgb(r, g, b, ink);
                Assert.IsTrue(transform.ConvertCmyk(ink[0], ink[1], ink[2], ink[3], out r2, out g2, out b2));
                worstColor = Math.Max(worstColor, Math.Max(Math.Abs(r2 - r), Math.Max(Math.Abs(g2 - g), Math.Abs(b2 - b))));
                for (var c = 0; c < 4; c++)
                {
                    worstInk = Math.Max(worstInk, Math.Abs(ink[c] - psd[c]));
                }
            }

            Assert.IsTrue(worstColor * 255 < 1.5, string.Format("color off by {0:F2} levels", worstColor * 255));
            Assert.IsTrue(worstInk * 255 < 3, string.Format("inks off by {0:F2} levels", worstInk * 255));
        }
    }
}
