using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Icc;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// ICC color management wired into the document: merged and layer pixels, effect and text
    /// colors, patterns, duotone rendering and the ink space the adjustment layers run in.
    /// </summary>
    [TestClass]
    public sealed class ColorManagementDocumentTests
    {
        private const string CmykFixture = "photoshop-cmyk-style-colors.psd";

        private struct Cs
        {
            public string Key;
            public double Value;

            public Cs(string key, double value)
            {
                Key = key;
                Value = value;
            }
        }

        private struct InkSpec
        {
            public ushort Space;
            public short[] Components;
            public short[] Curve;

            public InkSpec(ushort space, short[] components, short[] curve)
            {
                Space = space;
                Components = components;
                Curve = curve;
            }
        }

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
        public void Cmyk_fixture_layers_convert_through_the_embedded_profile()
        {
            var document = Fixtures.Load(CmykFixture);
            var overlay = document.Layers.Single(l => l.Name == "Overlay").GetPixels();
            var label = document.Layers.Single(l => l.Name == "Label").GetPixels();

            Assert.IsNotNull(document.ColorTransform);

            // The reference pins (lcms2, matching Photoshop): C43 Y98 fill and C0 M100 Y100 text.
            Assert.AreEqual(new PsdColor(158, 204, 62), overlay.GetPixel(overlay.Width / 2, overlay.Height / 2));
            var text = Enumerable.Range(0, label.Width * label.Height)
                .Select(i => label.GetPixel(i % label.Width, i / label.Width))
                .First(p => p.A == 255);
            Assert.AreEqual(new PsdColor(237, 28, 36), text);

            // Photoshop saved it without Maximize Compatibility: the merged image is a white
            // placeholder, so the layer render cannot be compared with it.
            Assert.IsFalse(document.HasRealMergedImage);
            Assert.AreEqual(new PsdColor(255, 255, 255), document.GetMergedImage().GetPixel(48, 32));

            var naive = PsdDocument.Load(Fixtures.PathOf(CmykFixture), new PsdLoadOptions { ColorManagement = false });
            Assert.IsNull(naive.ColorTransform);
            var naiveOverlay = naive.Layers.Single(l => l.Name == "Overlay").GetPixels();
            Assert.AreEqual(new PsdColor(145, 255, 5), naiveOverlay.GetPixel(overlay.Width / 2, overlay.Height / 2));
        }

        private static PsdColor OverlayColor(PsdLayer layer)
        {
            var overlay = layer.Style.Effects.First(e => e.Kind == PsdEffectKind.ColorOverlay);
            return overlay.Color.Value;
        }

        [TestMethod]
        public void Cmyk_fixture_effect_and_text_colors_convert_like_the_pixels()
        {
            // psd_cmyk_document_converts_style_and_text_colors: the C42 M45 Y67 K13 'CMYC' overlay
            // lands on Photoshop's (143,123,92) and the C0 M100 Y100 K0 /Type 2 text on #ed1c24.
            var document = Fixtures.Load(CmykFixture);
            var overlayLayer = document.Layers.Single(l => l.Name == "Overlay");
            var text = document.Layers.Single(l => l.Name == "Label").Text;

            Assert.AreEqual(new PsdColor(143, 123, 92), OverlayColor(overlayLayer));
            foreach (var run in text.StyleRuns)
            {
                Assert.AreEqual(new PsdColor(237, 28, 36), run.FillColor);
            }

            Assert.AreEqual(new PsdColor(237, 28, 36), document.TextEngine.Objects.SelectMany(o => o.StyleRuns).First().FillColor);
            var bounds = overlayLayer.Bounds;
            var render = document.Render(new RenderOptions { Source = RenderSource.Layers });
            Assert.AreEqual(new PsdColor(143, 123, 92), render.GetPixel(bounds.Left + 2, bounds.Top + 2));

            // With color management off, both fall back to the naive ink mix.
            var naive = PsdDocument.Load(Fixtures.PathOf(CmykFixture), new PsdLoadOptions { ColorManagement = false });
            Assert.AreEqual(new PsdColor(129, 122, 73), OverlayColor(naive.Layers.Single(l => l.Name == "Overlay")));
            foreach (var run in naive.Layers.Single(l => l.Name == "Label").Text.StyleRuns)
            {
                Assert.AreEqual(new PsdColor(255, 0, 0), run.FillColor);
            }
        }

        /// <summary>A <c>SoCo</c> payload: descriptor version 16, then a descriptor whose <c>Clr </c> is a <paramref name="classId"/> object.</summary>
        private static byte[] SolidColorBlock(string classId, params Cs[] components)
        {
            var writer = new PsdBuilder.Writer();
            writer.U32(16);
            writer.UnicodeString(string.Empty);
            writer.DescriptorId("null");
            writer.U32(1);
            writer.DescriptorId("Clr ");
            writer.Ascii("Objc");
            writer.UnicodeString(string.Empty);
            writer.DescriptorId(classId);
            writer.U32((uint)components.Length);
            foreach (var component in components)
            {
                writer.DescriptorId(component.Key);
                writer.Ascii("doub");
                writer.F64(component.Value);
            }

            return writer.ToArray();
        }

        private static PsdDocument FillDocument(PsdColorMode mode, int depth, byte[] profile, byte[] fill, bool managed = true)
        {
            var builder = new PsdBuilder { Width = 2, Height = 1, Mode = mode, Depth = depth };
            if (profile != null)
            {
                builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, profile));
            }

            var layer = new BuilderLayer { Name = "fill", Rect = new PsdRect(0, 0, 2, 1) };
            layer.Blocks.Add(new KeyValuePair<string, byte[]>("SoCo", fill));
            builder.Layers.Add(layer);
            return PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = managed });
        }

        [TestMethod]
        public void Gray_descriptor_and_text_colors_convert_through_the_gray_profile()
        {
            // 'Gry ' 50 is 50% black: lightness 0.5, level 128, which Gray Gamma 2.2 shows as 129 (lcms).
            var fill = SolidColorBlock("Grsc", new Cs("Gry ", 50));
            var managed = FillDocument(PsdColorMode.Grayscale, 8, TestProfiles.GrayGamma22(), fill);
            var plain = FillDocument(PsdColorMode.Grayscale, 8, TestProfiles.GrayGamma22(), fill, false);

            Assert.AreEqual(new PsdColor(129, 129, 129), managed.Layers[0].FillColor);
            Assert.AreEqual(new PsdColor(128, 128, 128), plain.Layers[0].FillColor);

            // EngineData /Type 0 is [alpha, lightness]; 32 / 255 converts to 26 like the pixel test above.
            var node = EngineDataParser.Parse(Encoding.UTF8.GetBytes("<< /FillColor << /Type 0 /Values [ 1.0 .12549 ] >> >>"))["FillColor"];
            Assert.AreEqual(new PsdColor(26, 26, 26), EngineStyles.Color(node, managed.Colors));
            Assert.AreEqual(new PsdColor(32, 32, 32), EngineStyles.Color(node, plain.Colors));
        }

        [TestMethod]
        public void Cmyk_engine_and_legacy_colors_use_the_swop_profile()
        {
            var document = FillDocument(
                PsdColorMode.Cmyk,
                8,
                SwopProfile(),
                SolidColorBlock("CMYC", new Cs("Cyn ", 43), new Cs("Mgnt", 0), new Cs("Ylw ", 98), new Cs("Blck", 0)));

            // The C43 Y98 fill matches the fixture's pixel pin, so a fill layer and its pixels agree.
            Assert.AreEqual(new PsdColor(158, 204, 62), document.Layers[0].FillColor);
            var node = EngineDataParser.Parse(Encoding.UTF8.GetBytes("<< /FillColor << /Type 2 /Values [ 1.0 0.0 1.0 1.0 0.0 ] >> >>"))["FillColor"];
            Assert.AreEqual(new PsdColor(237, 28, 36), EngineStyles.Color(node, document.Colors));

            // PS 5 10-byte color, space 2: CMYK stored inverted (65535 = no ink).
            var legacy = new byte[] { 0, 2, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF };
            Assert.AreEqual(new PsdColor(237, 28, 36), LegacyText.ReadColor(new BigEndianReader(legacy), document.Colors));
            Assert.AreEqual(new PsdColor(255, 0, 0), LegacyText.ReadColor(new BigEndianReader(legacy)));
        }

        [TestMethod]
        public void Rgb_descriptor_colors_follow_the_document_profile()
        {
            var fill = SolidColorBlock("RGBC", new Cs("Rd  ", 64), new Cs("Grn ", 128), new Cs("Bl  ", 192));

            // Same conversion as the Adobe RGB pixels in the wide-gamut test below.
            AssertNear(FillDocument(PsdColorMode.Rgb, 8, TestProfiles.AdobeRgb(), fill).Layers[0].FillColor.Value, 0, 129, 196);
            Assert.AreEqual(new PsdColor(64, 128, 192), FillDocument(PsdColorMode.Rgb, 8, TestProfiles.AdobeRgb(), fill, false).Layers[0].FillColor);
            Assert.AreEqual(new PsdColor(64, 128, 192), FillDocument(PsdColorMode.Rgb, 8, null, fill).Layers[0].FillColor);
        }

        [TestMethod]
        public void Thirty_two_bit_descriptor_colors_are_linear_light()
        {
            // psd-tools' 300dpi.psb: Photoshop shows the (172, 11, 11) fill of a 32-bit document as its sRGB encoding.
            var fill = SolidColorBlock("RGBC", new Cs("Rd  ", 172), new Cs("Grn ", 11), new Cs("Bl  ", 11));
            var plain = FillDocument(PsdColorMode.Rgb, 32, null, fill).Layers[0].FillColor.Value;
            Assert.AreEqual(new PsdColor(Encoded(172), Encoded(11), Encoded(11)), plain);

            // With a profile, only the colorant matrix applies, as for 32-bit pixels: linear gray stays neutral.
            var gray = SolidColorBlock("RGBC", new Cs("Rd  ", 127.5), new Cs("Grn ", 127.5), new Cs("Bl  ", 127.5));
            AssertNear(FillDocument(PsdColorMode.Rgb, 32, TestProfiles.AdobeRgb(), gray).Layers[0].FillColor.Value, 188, 188, 188);
        }

        private static byte Encoded(double value)
        {
            return (byte)Math.Round(IccPcs.EncodeSrgb(value / 255) * 255);
        }

        /// <summary>A <c>Patt</c> block with one uncompressed 8-bit pattern of the given mode and planes.</summary>
        private static byte[] PatternBlock(int mode, int width, int height, byte[][] planes)
        {
            var vma = new PsdBuilder.Writer();
            vma.U32(0);
            vma.U32(0);
            vma.U32((uint)height);
            vma.U32((uint)width);
            vma.U32((uint)planes.Length);
            foreach (var plane in planes)
            {
                vma.U32(1);
                vma.U32((uint)(23 + plane.Length));
                vma.U32(8);
                vma.I32(0);
                vma.I32(0);
                vma.I32(height);
                vma.I32(width);
                vma.U16(8);
                vma.U8(0);
                vma.Bytes(plane);
            }

            // The two trailing slots (user mask, transparency) are not written.
            vma.U32(0);
            vma.U32(0);
            var record = new PsdBuilder.Writer();
            record.U32(1);
            record.U32((uint)mode);
            record.U16((ushort)height);
            record.U16((ushort)width);
            record.UnicodeString("tile");
            record.U8(4);
            record.Ascii("tile");
            record.U32(3);
            record.U32((uint)vma.Length);
            record.Bytes(vma.ToArray());
            var block = new PsdBuilder.Writer();
            block.U32((uint)record.Length);
            block.Bytes(record.ToArray());
            while (block.Length % 4 != 0)
            {
                block.U8(0);
            }

            return block.ToArray();
        }

        [TestMethod]
        public void Cmyk_pattern_tiles_convert_through_the_document_profile()
        {
            // Two texels, stored inverted: C43 Y98 and C0 M100 Y100.
            var inks = new[]
            {
                new byte[] { 255 - 110, 255 },
                new byte[] { 255, 0 },
                new byte[] { 255 - 250, 0 },
                new byte[] { 255, 255 },
            };
            Func<byte[], bool, PsdDocument> load = delegate(byte[] profile, bool managed)
            {
                var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Cmyk };
                if (profile != null)
                {
                    builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, profile));
                }

                builder.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Patt", PatternBlock(4, 2, 1, inks)));
                return PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = managed });
            };

            var tile = load(SwopProfile(), true).Patterns["tile"];
            CollectionAssert.AreEqual(new byte[] { 158, 204, 62, 255, 237, 28, 36, 255 }, tile.Rgba);

            // Without a profile, the naive mix of the pixel decode.
            CollectionAssert.AreEqual(new byte[] { 145, 255, 5, 255, 255, 0, 0, 255 }, load(null, true).Patterns["tile"].Rgba);
            CollectionAssert.AreEqual(new byte[] { 145, 255, 5, 255, 255, 0, 0, 255 }, load(SwopProfile(), false).Patterns["tile"].Rgba);
        }

        [TestMethod]
        public void Gray_and_rgb_pattern_tiles_follow_a_matching_document_profile()
        {
            var gray = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Grayscale };
            gray.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.GrayGamma22()));
            gray.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Patt", PatternBlock(1, 2, 1, new[] { new byte[] { 32, 128 } })));
            CollectionAssert.AreEqual(new byte[] { 26, 26, 26, 255, 129, 129, 129, 255 }, PsdDocument.Load(gray.Build()).Patterns["tile"].Rgba);

            var rgb = new PsdBuilder { Width = 1, Height = 1 };
            rgb.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.AdobeRgb()));
            rgb.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Patt", PatternBlock(3, 1, 1, new[] { new byte[] { 64 }, new byte[] { 128 }, new byte[] { 192 } })));
            var texel = PsdDocument.Load(rgb.Build()).Patterns["tile"].Rgba;
            AssertNear(new Rgb(texel[0], texel[1], texel[2]), 0, 129, 196);

            // A gray tile in an RGB document has no matching profile and stays as stored.
            var mixed = new PsdBuilder { Width = 1, Height = 1 };
            mixed.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.AdobeRgb()));
            mixed.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Patt", PatternBlock(1, 1, 1, new[] { new byte[] { 100 } })));
            CollectionAssert.AreEqual(new byte[] { 100, 100, 100, 255 }, PsdDocument.Load(mixed.Build()).Patterns["tile"].Rgba);
        }

        private static readonly short[] LinearCurve =
        {
            0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950, 1000,
        };

        /// <summary>
        /// Duotone color mode data: version 1, ink count, four 10-byte colors, four 64-byte
        /// Pascal names, four 28-byte transfer functions, dot gain, eleven overprint colors.
        /// </summary>
        private static byte[] DuotoneSpec(params InkSpec[] inks)
        {
            var writer = new PsdBuilder.Writer();
            writer.U16(1);
            writer.U16((ushort)inks.Length);
            for (var i = 0; i < 4; i++)
            {
                var space = i < inks.Length ? inks[i].Space : (ushort)0;
                var components = i < inks.Length ? inks[i].Components : new short[4];
                writer.U16(space);
                foreach (var component in components)
                {
                    writer.I16(component);
                }
            }

            for (var i = 0; i < 4; i++)
            {
                var name = new byte[64];
                name[0] = 3;
                Encoding.ASCII.GetBytes("Ink").CopyTo(name, 1);
                writer.Bytes(name);
            }

            for (var i = 0; i < 4; i++)
            {
                foreach (var point in i < inks.Length ? inks[i].Curve : LinearCurve)
                {
                    writer.I16(point);
                }

                writer.U16(0);
            }

            writer.U16(20);
            writer.Zeros(11 * 10);
            return writer.ToArray();
        }

        private static readonly InkSpec BlackInk = new InkSpec(0, new short[] { 0, 0, 0, 0 }, null);

        private static readonly InkSpec RedInk = new InkSpec(0, new short[] { -1, 0, 0, 0 }, null); // 65535, 0, 0 as RGB

        private static RgbaImage RenderDuotone(byte[] spec, byte[] gray, int depth = 8, byte[] profile = null)
        {
            var builder = new PsdBuilder { Width = gray.Length, Height = 1, Mode = PsdColorMode.Duotone, Depth = depth, ColorModeData = spec };
            if (profile != null)
            {
                builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, profile));
            }

            builder.MergedChannels.Add(depth == 8
                ? gray
                : PsdBuilder.Plane16(gray.Length, 1, (x, y) => (ushort)(gray[x] * 257)));
            return PsdDocument.Load(builder.Build()).Render();
        }

        [TestMethod]
        public void Monotone_black_duotone_renders_like_grayscale()
        {
            var gray = new byte[] { 0, 32, 128, 200, 255 };
            var spec = DuotoneSpec(new InkSpec(BlackInk.Space, BlackInk.Components, LinearCurve));

            var plain = RenderDuotone(spec, gray);
            var profiled = RenderDuotone(spec, gray, 8, TestProfiles.GrayGamma22());
            for (var x = 0; x < gray.Length; x++)
            {
                Assert.AreEqual(new PsdColor(gray[x], gray[x], gray[x]), plain.GetPixel(x, 0));
            }

            // Through the document's gray profile the ink reflectance follows Gray Gamma 2.2 (32 -> 26, 128 -> 129).
            Assert.AreEqual(new PsdColor(26, 26, 26), profiled.GetPixel(1, 0));
            Assert.AreEqual(new PsdColor(129, 129, 129), profiled.GetPixel(2, 0));
        }

        [DataTestMethod]
        [DataRow(8)]
        [DataRow(16)]
        public void Duotone_inks_multiply_in_linear_light(int depth)
        {
            var spec = DuotoneSpec(
                new InkSpec(BlackInk.Space, BlackInk.Components, LinearCurve),
                new InkSpec(RedInk.Space, RedInk.Components, LinearCurve));

            var image = RenderDuotone(spec, new byte[] { 0, 128, 255 }, depth);

            // Tone 0.498 on both inks: black leaves 21.6% linear light, red removes it from green and blue only.
            Assert.AreEqual(new PsdColor(0, 0, 0), image.GetPixel(0, 0));
            AssertNear(image.GetPixel(1, 0), 128, 61, 61);
            Assert.AreEqual(new PsdColor(255, 255, 255), image.GetPixel(2, 0));
        }

        [TestMethod]
        public void Duotone_curves_and_lab_inks_shape_the_result()
        {
            // A curve that puts 30% black ink at the 50% tone.
            var light = new short[] { 0, -1, -1, -1, -1, -1, 300, -1, -1, -1, -1, -1, 1000 };
            Assert.AreEqual(0.3, Duotone.EvaluateCurve(light, 0.5), 1e-9);
            Assert.AreEqual(
                0.3,
                Duotone.EvaluateCurve(new short[] { 0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950, 1000 }, 0.3),
                1e-9);
            var image = RenderDuotone(DuotoneSpec(new InkSpec(BlackInk.Space, BlackInk.Components, light)), new byte[] { 127 });
            // 30% black coverage reflects the luminance of gray 0.7, which encodes back to 0.7 (178.5).
            Assert.IsTrue(image.GetPixel(0, 0).R >= 177 && image.GetPixel(0, 0).R <= 179);

            // Lab inks (space 7: L in hundredths, a and b signed hundredths): L 50 neutral at full tone.
            var lab = RenderDuotone(DuotoneSpec(new InkSpec(7, new short[] { 5000, 0, 0, 0 }, LinearCurve)), new byte[] { 0 });
            AssertNear(lab.GetPixel(0, 0), 119, 119, 119);
        }

        [TestMethod]
        public void Multichannel_planes_read_as_inverted_cmy_inks()
        {
            // As the reference's convert_multichannel_planes_to_rgb: plane bytes are 255 - ink, a missing plane is no ink.
            var three = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Multichannel };
            three.MergedChannels.Add(new byte[] { 0, 255 });
            three.MergedChannels.Add(new byte[] { 255, 128 });
            three.MergedChannels.Add(new byte[] { 255, 0 });
            var two = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Multichannel };
            two.MergedChannels.Add(new byte[] { 40 });
            two.MergedChannels.Add(new byte[] { 80 });

            var image = PsdDocument.Load(three.Build()).Render();
            Assert.AreEqual(new PsdColor(0, 255, 255), image.GetPixel(0, 0));
            Assert.AreEqual(new PsdColor(255, 128, 0), image.GetPixel(1, 0));
            Assert.AreEqual(new PsdColor(40, 80, 255), PsdDocument.Load(two.Build()).Render().GetPixel(0, 0));
        }

        [TestMethod]
        public void Unreadable_duotone_specifications_fall_back_to_gray()
        {
            Assert.AreEqual(new PsdColor(90, 90, 90), RenderDuotone(new byte[] { 0, 1, 0, 1 }, new byte[] { 90 }).GetPixel(0, 0));
            var spec = DuotoneSpec(new InkSpec(BlackInk.Space, BlackInk.Components, LinearCurve));
            spec[1] = 9; // ink count out of range
            Assert.AreEqual(new PsdColor(90, 90, 90), RenderDuotone(spec, new byte[] { 90 }).GetPixel(0, 0));
        }

        [TestMethod]
        public void Srgb_profiled_fixtures_keep_their_pixels()
        {
            foreach (var name in new[] { "arrows.psd", "photoshop-posterize.psd", "photoshop-basic.psb" })
            {
                var document = Fixtures.Load(name);
                Assert.IsNotNull(document.GetImageResource(ImageResourceIds.IccProfile));
                Assert.IsNull(document.ColorTransform);

                // The embedded HP "sRGB IEC61966-2.1" is recognized as sRGB itself.
                var profile = IccProfile.Parse(document.GetImageResource(ImageResourceIds.IccProfile).Data);
                Assert.IsTrue(IccSrgbTransform.Create(profile, 3, IccTransformSettings.Default).IsSrgbEquivalent);
            }
        }

        [DataTestMethod]
        [DataRow(8)]
        [DataRow(16)]
        public void Cmyk_documents_convert_merged_and_layer_pixels(int depth)
        {
            var builder = new PsdBuilder { Width = 4, Height = 1, Depth = depth, Mode = PsdColorMode.Cmyk };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, SwopProfile()));

            // Inks per pixel: paper, K100, M100 Y100, C43 Y98 (stored inverted).
            var inks = new[]
            {
                new[] { 0, 0, 0, 0 }, new[] { 0, 0, 0, 255 }, new[] { 0, 255, 255, 0 }, new[] { 110, 0, 250, 0 },
            };
            Func<int, byte[]> plane = delegate(int channel)
            {
                return depth == 8
                    ? PsdBuilder.Plane8(4, 1, (x, y) => (byte)(255 - inks[x][channel]))
                    : PsdBuilder.Plane16(4, 1, (x, y) => (ushort)((255 - inks[x][channel]) * 257));
            };

            var layer = new BuilderLayer { Name = "inks", Rect = new PsdRect(0, 0, 4, 1) };
            layer.Channels[-1] = depth == 8 ? PsdBuilder.Plane8(4, 1, 255) : PsdBuilder.Plane16(4, 1, (x, y) => 65535);
            for (short c = 0; c < 4; c++)
            {
                layer.Channels[c] = plane(c);
                builder.MergedChannels.Add(plane(c));
            }

            builder.Layers.Add(layer);
            var document = PsdDocument.Load(builder.Build());
            var merged = document.GetMergedImage();
            var layerPixels = document.Layers[0].GetPixels();
            var expected = new[] { new Rgb(255, 255, 255), new Rgb(35, 31, 32), new Rgb(237, 28, 36), new Rgb(158, 204, 62) };
            for (var x = 0; x < 4; x++)
            {
                var color = merged.GetPixel(x, 0);
                AssertNear(new Rgb(color.R, color.G, color.B), expected[x].R, expected[x].G, expected[x].B);
                Assert.AreEqual(merged.GetPixel(x, 0), layerPixels.GetPixel(x, 0));
            }
        }

        [TestMethod]
        public void Unusable_cmyk_profile_falls_back_to_the_naive_formula()
        {
            // As in the reference's psd_imported_cmyk_icc_profile_is_not_exported_as_rgb_profile:
            // full-ink channel bytes (all zero) still decode to black.
            var builder = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Cmyk };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, new byte[] { 1, 2, 3, 4 }));
            for (var c = 0; c < 4; c++)
            {
                builder.MergedChannels.Add(new byte[] { 0 });
            }

            var document = PsdDocument.Load(builder.Build());

            Assert.IsNull(document.ColorTransform);
            Assert.AreEqual(new PsdColor(0, 0, 0), document.Render().GetPixel(0, 0));
        }

        [TestMethod]
        public void Rgb_documents_with_a_wide_gamut_profile_convert_and_can_opt_out()
        {
            var builder = new PsdBuilder { Width = 3, Height = 1 };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.AdobeRgb()));
            builder.MergedChannels.Add(new byte[] { 64, 128, 0 });
            builder.MergedChannels.Add(new byte[] { 128, 128, 255 });
            builder.MergedChannels.Add(new byte[] { 192, 128, 0 });

            var managed = PsdDocument.Load(builder.Build()).Render();
            var unmanaged = PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = false }).Render();

            AssertNear(managed.GetPixel(0, 0), 0, 129, 196);
            AssertNear(managed.GetPixel(1, 0), 129, 129, 129);
            AssertNear(managed.GetPixel(2, 0), 0, 255, 0);
            Assert.AreEqual(new PsdColor(64, 128, 192), unmanaged.GetPixel(0, 0));
            Assert.AreEqual(new PsdColor(128, 128, 128), unmanaged.GetPixel(1, 0));
        }

        [TestMethod]
        public void Indexed_palettes_convert_through_an_rgb_profile()
        {
            var palette = new byte[768];
            palette[1] = 64;
            palette[256 + 1] = 128;
            palette[512 + 1] = 192;
            var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Indexed, ColorModeData = palette };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.AdobeRgb()));
            builder.MergedChannels.Add(new byte[] { 0, 1 });

            var image = PsdDocument.Load(builder.Build()).Render();

            Assert.AreEqual(new PsdColor(0, 0, 0), image.GetPixel(0, 0));
            AssertNear(image.GetPixel(1, 0), 0, 129, 196);
        }

        [DataTestMethod]
        [DataRow(8)]
        [DataRow(16)]
        public void Grayscale_documents_convert_through_a_gray_profile(int depth)
        {
            var builder = new PsdBuilder { Width = 3, Height = 1, Depth = depth, Mode = PsdColorMode.Grayscale };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.GrayGamma22()));
            var levels = new byte[] { 32, 128, 255 };
            builder.MergedChannels.Add(depth == 8
                ? levels
                : PsdBuilder.Plane16(3, 1, (x, y) => (ushort)(levels[x] * 257)));

            var image = PsdDocument.Load(builder.Build()).Render();

            Assert.AreEqual(new PsdColor(26, 26, 26), image.GetPixel(0, 0));
            Assert.AreEqual(new PsdColor(129, 129, 129), image.GetPixel(1, 0));
            Assert.AreEqual(new PsdColor(255, 255, 255), image.GetPixel(2, 0));
        }

        [TestMethod]
        public void Dot_gain_gray_ramp_matches_photoshop()
        {
            // The reference pins its local gray-ramp-dotgain20.psd against Photoshop's Convert to
            // Profile (within 2 levels); this builds the same 256-step ramp with a Dot Gain 20% stand-in.
            var builder = new PsdBuilder { Width = 256, Height = 1, Mode = PsdColorMode.Grayscale };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.DotGain20Like()));
            var layer = new BuilderLayer { Name = "ramp", Rect = new PsdRect(0, 0, 256, 1) };
            layer.Channels[-1] = PsdBuilder.Plane8(256, 1, 255);
            layer.Channels[0] = PsdBuilder.Plane8(256, 1, (x, y) => (byte)x);
            builder.Layers.Add(layer);
            builder.MergedChannels.Add(PsdBuilder.Plane8(256, 1, (x, y) => (byte)x));
            builder.Layers[0].Blocks.Add(new KeyValuePair<string, byte[]>("SoCo", SolidColorBlock("Grsc", new Cs("Gry ", 50))));

            var document = PsdDocument.Load(builder.Build());
            var pixels = document.Layers[0].GetPixels();
            var merged = document.GetMergedImage();
            foreach (var pin in TestProfiles.DotGain20Pins)
            {
                var pixel = pixels.GetPixel(pin.Gray, 0);
                Assert.IsTrue(pixel.R >= pin.Srgb - 1 && pixel.R <= pin.Srgb + 1, string.Format("gray {0}: {1} vs {2}", pin.Gray, pixel.R, pin.Srgb));
                Assert.IsTrue(pixel.R == pixel.G && pixel.G == pixel.B);
                Assert.AreEqual(pixel, merged.GetPixel(pin.Gray, 0));
            }

            // A 50% 'Grsc' color converts like the 128 pixel: Photoshop's 20% dot gain at mid tone.
            Assert.AreEqual(pixels.GetPixel(128, 0), document.Layers[0].FillColor);
            var previous = -1;
            for (var x = 0; x < 256; x++)
            {
                Assert.IsTrue(pixels.GetPixel(x, 0).R >= previous);
                previous = pixels.GetPixel(x, 0).R;
            }
        }

        [TestMethod]
        public void Thirty_two_bit_rgb_with_a_lut_profile_uses_its_colorants()
        {
            // The Adobe RGB model sampled into an mft2 CLUT with an XYZ PCS: at 32 bits the data is
            // linear, so only the LUT's primaries matter, and they match the matrix/TRC profile.
            var lut = new IccBuilder { DeviceClass = "scnr" }
                .Add("desc", IccBuilder.Description("Test Adobe RGB CLUT"))
                .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
                .Add("A2B0", IccBuilder.Lut(true, 3, 3, 9, delegate(double[] node)
                {
                    var r = Math.Pow(node[0], 563 / 256.0);
                    var g = Math.Pow(node[1], 563 / 256.0);
                    var b = Math.Pow(node[2], 563 / 256.0);
                    var red = TestProfiles.AdobeRed;
                    var green = TestProfiles.AdobeGreen;
                    var blue = TestProfiles.AdobeBlue;
                    return new[]
                    {
                        ((red[0] * r) + (green[0] * g) + (blue[0] * b)) * 32768 / 65535,
                        ((red[1] * r) + (green[1] * g) + (blue[1] * b)) * 32768 / 65535,
                        ((red[2] * r) + (green[2] * g) + (blue[2] * b)) * 32768 / 65535,
                    };
                }))
                .Build();
            var random = new Random(11);
            var linear = new[] { new float[64], new float[64], new float[64] };
            for (var i = 0; i < 64; i++)
            {
                for (var c = 0; c < 3; c++)
                {
                    linear[c][i] = (float)random.NextDouble();
                }
            }

            var fromLut = new PlanarImage(new PsdRect(0, 0, 64, 1));
            var fromMatrix = new PlanarImage(new PsdRect(0, 0, 64, 1));
            Assert.IsTrue(Create(lut, 3).Apply(linear, 32, fromLut));
            Assert.IsTrue(Create(TestProfiles.AdobeRgb(), 3).Apply(linear, 32, fromMatrix));
            for (var i = 0; i < 64; i++)
            {
                Assert.AreEqual(fromMatrix.R[i], fromLut.R[i], 0.002f);
                Assert.AreEqual(fromMatrix.G[i], fromLut.G[i], 0.002f);
                Assert.AreEqual(fromMatrix.B[i], fromLut.B[i], 0.002f);
            }
        }

        private static IccSrgbTransform Create(byte[] profile, int channels)
        {
            var transform = IccSrgbTransform.Create(IccProfile.Parse(profile), channels, IccTransformSettings.Default);
            Assert.IsNotNull(transform);
            return transform;
        }

        [TestMethod]
        public void Thirty_two_bit_rgb_converts_linear_primaries()
        {
            // 32-bit data is linear light: only the colorant matrix applies.
            var builder = new PsdBuilder { Width = 2, Height = 1, Depth = 32 };
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.IccProfile, string.Empty, TestProfiles.AdobeRgb()));
            builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, y) => x == 0 ? 0.5f : 0f));
            builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, y) => x == 0 ? 0.5f : 1f));
            builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, y) => x == 0 ? 0.5f : 0f));

            var image = PsdDocument.Load(builder.Build()).Render();

            // Linear gray 0.5 stays neutral (sRGB 188); linear Adobe green clips red and blue.
            AssertNear(image.GetPixel(0, 0), 188, 188, 188);
            AssertNear(image.GetPixel(1, 0), 0, 255, 0);
        }

        // ---- Ink spaces (adjustment layers on the document's channels) -----------------

        [TestMethod]
        public void Gray_ink_space_reads_every_stored_level_back_exactly()
        {
            // The real Dot Gain 20% profile Photoshop embedded in psd-tools' levels_grayscale.psd.
            var document = PsdDocument.Load(Fixtures.PsdTools("adjustments/levels_grayscale.psd"));
            Assert.AreEqual("Dot Gain 20%", document.ColorTransform.Profile.Description);
            var space = document.InkSpace;
            Assert.IsNotNull(space);
            Assert.AreEqual(1, space.Channels);
            var planes = new[] { Enumerable.Range(0, 256).Select(v => v / 255f).ToArray() };
            var r = new float[256];
            var g = new float[256];
            var b = new float[256];
            Assert.IsTrue(document.ColorTransform.Apply(planes, 8, r, g, b, 256));
            for (var level = 0; level < 256; level++)
            {
                Assert.AreEqual((float)level, space.LevelFromRgb(r[level], g[level], b[level]));
            }

            // Halfway between two stored levels reads as halfway.
            Assert.AreEqual(100.5f, space.LevelFromRgb((r[100] + r[101]) / 2, (g[100] + g[101]) / 2, (b[100] + b[101]) / 2), 0.01f);

            // Photoshop's own sRGB view of a Dot Gain 20% ramp (the reference's pins).
            foreach (var pin in TestProfiles.DotGain20Pins)
            {
                Assert.IsTrue(r[pin.Gray] * 255 >= pin.Srgb - 1.5 && r[pin.Gray] * 255 <= pin.Srgb + 1.5);
            }
        }

        [TestMethod]
        public void Documents_get_an_ink_space_by_color_mode()
        {
            Assert.IsNull(Fixtures.Load("photoshop-threshold.psd").InkSpace);
            Assert.AreEqual(4, Fixtures.Load(CmykFixture).InkSpace.Channels);

            // A grayscale document without a profile reads its gray as sRGB: nothing to convert.
            var builder = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Grayscale };
            builder.MergedChannels.Add(PsdBuilder.Plane8(1, 1, 128));
            Assert.IsNull(PsdDocument.Load(builder.Build()).InkSpace);
        }

        [TestMethod]
        public void Duotone_and_gray_adjustments_run_on_the_stored_channel()
        {
            // Invert over a red-and-black duotone: the result is the ink mix of the inverted
            // gray value, not the inverted sRGB mix.
            var gray = new byte[] { 40, 128, 220 };
            var spec = DuotoneSpec(
                new InkSpec(BlackInk.Space, BlackInk.Components, LinearCurve),
                new InkSpec(RedInk.Space, RedInk.Components, LinearCurve));
            var expected = RenderDuotone(spec, gray.Select(v => (byte)(255 - v)).ToArray());
            var builder = new PsdBuilder { Width = gray.Length, Height = 1, Mode = PsdColorMode.Duotone, ColorModeData = spec };
            var background = new BuilderLayer { Name = "Background", Rect = new PsdRect(0, 0, gray.Length, 1) };
            background.Channels[-1] = PsdBuilder.Plane8(gray.Length, 1, 255);
            background.Channels[0] = gray;
            builder.Layers.Add(background);
            var invert = new BuilderLayer { Name = "Invert", Rect = default(PsdRect) };
            invert.Blocks.Add(new KeyValuePair<string, byte[]>("nvrt", new byte[0]));
            builder.Layers.Add(invert);
            builder.MergedChannels.Add(gray);

            var document = PsdDocument.Load(builder.Build());
            Assert.AreEqual(1, document.InkSpace.Channels);
            var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });
            var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
            for (var x = 0; x < gray.Length; x++)
            {
                var want = expected.GetPixel(x, 0);
                AssertNear(rendered.GetPixel(x, 0), want.R, want.G, want.B);
                Assert.AreNotEqual(255 - merged.GetPixel(x, 0).G, rendered.GetPixel(x, 0).G);
            }
        }
    }
}
