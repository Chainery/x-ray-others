using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Port of the original ColorAdjustmentTests: the adjustments the reference does not
    /// model (Channel Mixer, Black and White, Selective Color, Gradient Map, Photo Filter,
    /// Vibrance) and noise gradients. Payload bytes come from Photoshop-written files in
    /// psd-tools' test collection. Tuple returns of the original become out parameters or
    /// ColorF values here.
    /// </summary>
    [TestClass]
    public sealed class ColorAdjustmentTests
    {
        // fill_adjustments.psd (psd-tools): Photoshop's identity Channel Mixer, its Warming
        // Filter (85) Photo Filter at 25% with Preserve Luminosity, and a Vibrance of -6/+2.
        private const string IdentityMixer = "0001000000640000000000000000000000640000000000000000000000640000000000000000000000640000";
        private const string WarmingFilter = "000200071A320C802EE000000000001901000000";
        private const string VibranceMinus6 = "00000010000000010000000000006E756C6C000000020000000876696272616E63656C6F6E67FFFFFFFA00000000537472746C6F6E67000000020000";

        // gradient-map.psd and gradient-map-v3-classic.psd (psd-tools): foreground to background.
        private const string GradientMapV1 = "00010000000000190046006F0072006500670072006F0075006E006400200074006F0020004200610063006B00670072006F0075006E006400000002000000000000003200007B7A5B085B080000000000001000000000320000FFFFFFFFFFFF000000000002000000000000003200FF000010000000003200FF00021000002000001C09F81A000000010000080000030000000000000000800080008000800000000000";
        private const string GradientMapV3 = "0003000047636C73000000190046006F0072006500670072006F0075006E006400200074006F0020004200610063006B00670072006F0075006E00640000000200000000000000320000FFFF000000000000000000001000000000320000FFFFFFFFFFFF000000000002000000000000003200FF000010000000003200FF0002100000200000293A5138000000010000080000030000000000000000800080008000800000000000";

        // ---- Channel Mixer ------------------------------------------------------------------

        [TestMethod]
        public void Channel_mixer_parses_and_mixes()
        {
            var identity = Adjustments.ParseChannelMixer(Hex(IdentityMixer));
            Assert.IsNotNull(identity);
            Assert.IsFalse(identity.Monochrome);
            Assert.AreEqual(new MixerChannel(100, 0, 0, 0), identity.Red);
            Assert.AreEqual(new MixerChannel(0, 0, 100, 0), identity.Blue);
            var identityValue = Adjustments.ChannelMixerValue(12, 34, 56, identity);
            Assert.AreEqual(12, identityValue.R);
            Assert.AreEqual(34, identityValue.G);
            Assert.AreEqual(56, identityValue.B);

            var settings = new ChannelMixerSettings(false, new MixerChannel(50, 50, 0, 0), new MixerChannel(0, 100, 0, 0), new MixerChannel(0, 0, 100, 10));
            var mixed = Adjustments.ChannelMixerValue(100, 200, 50, settings);
            Assert.AreEqual(150, mixed.R);
            Assert.AreEqual(200, mixed.G);
            Assert.AreEqual(76, mixed.B);
            var overdrive = Adjustments.ChannelMixerValue(255, 0, 0, new ChannelMixerSettings(false, new MixerChannel(200, 0, 0, 0), new MixerChannel(-200, 0, 0, 0), new MixerChannel(0, 0, 0, -200)));
            Assert.AreEqual(255, overdrive.R);
            Assert.AreEqual(0, overdrive.G);
            Assert.AreEqual(0, overdrive.B);

            // A monochrome mixer stores its gray record first and writes it to every channel.
            var mono = new PsdBuilder.Writer();
            mono.U16(1);
            mono.U16(1);
            foreach (var value in new short[] { 30, 59, 11, 0, 0 })
            {
                mono.I16(value);
            }

            mono.Zeros(30);
            var image = RenderOver(new PsdColor(200, 100, 0), Block("mixr", mono.ToArray()));
            Assert.AreEqual(new PsdColor(119, 119, 119), image.GetPixel(0, 0));

            Assert.IsNull(Adjustments.ParseChannelMixer(new byte[] { 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.IsNull(Adjustments.ParseChannelMixer(new byte[] { 0, 1, 0, 0, 0, 100 }));
        }

        // ---- Black and White ----------------------------------------------------------------

        [TestMethod]
        public void Black_and_white_weights_the_two_largest_channels()
        {
            var settings = BlackWhiteSettings.Default;
            AssertBW(255, 0, 0, settings, 102, 102, 102);    // reds 40
            AssertBW(255, 255, 0, settings, 153, 153, 153);  // yellows 60
            AssertBW(0, 255, 0, settings, 102, 102, 102);    // greens 40
            AssertBW(0, 255, 255, settings, 153, 153, 153);  // cyans 60
            AssertBW(0, 0, 255, settings, 51, 51, 51);       // blues 20
            AssertBW(255, 0, 255, settings, 204, 204, 204);  // magentas 80
            AssertBW(90, 90, 90, settings, 90, 90, 90);      // neutrals keep their value
            AssertBW(255, 128, 0, settings, 128, 128, 128);  // 128 * 0.6 + 127 * 0.4

            var tinted = Adjustments.BlackWhiteValue(255, 128, 0, WithTint(settings, new PsdColor(225, 211, 179)));
            Assert.IsTrue(tinted.R > tinted.G && tinted.G > tinted.B);
            var luma = (0.3 * tinted.R) + (0.59 * tinted.G) + (0.11 * tinted.B);
            Assert.IsTrue(luma >= 127 && luma <= 129, "tinted luma " + luma);
        }

        [TestMethod]
        public void Black_and_white_reads_its_descriptor_and_renders()
        {
            var descriptor = Descriptor(delegate(PsdBuilder.Writer writer)
            {
                Long(writer, "Rd  ", 100);
                Long(writer, "Yllw", 0);
                Long(writer, "Grn ", 0);
                Long(writer, "Cyn ", 0);
                Long(writer, "Bl  ", 0);
                Long(writer, "Mgnt", 0);
                writer.DescriptorId("useTint");
                writer.Ascii("bool");
                writer.U8(0);
            }, 7);
            var settings = Adjustments.ParseBlackWhite(descriptor);
            Assert.IsNotNull(settings);
            Assert.AreEqual(100, settings.Reds);
            Assert.AreEqual(0, settings.Yellows);
            Assert.IsFalse(settings.HasTint);

            var image = RenderOver(new PsdColor(200, 50, 20), Block("blwh", descriptor));
            Assert.AreEqual(new PsdColor(170, 170, 170), image.GetPixel(0, 0)); // 20 + 30 * 0 + 150 * 1
            Assert.IsNull(Adjustments.ParseBlackWhite(new byte[] { 0, 0, 0, 16, 1 }));
        }

        // ---- Selective Color ----------------------------------------------------------------

        [TestMethod]
        public void Selective_color_is_parsed_and_modeled_but_not_rendered()
        {
            var payload = new PsdBuilder.Writer();
            payload.U16(1);
            payload.U16(0);
            payload.Zeros(8);
            payload.I16(-100); // reds: cyan -100
            payload.Zeros(6);
            payload.Zeros(8 * 8);
            var settings = Adjustments.ParseSelectiveColor(payload.ToArray());
            Assert.IsNotNull(settings);
            Assert.IsFalse(settings.Absolute);
            Assert.AreEqual(new SelectiveColorRange(-100, 0, 0, 0), settings.Ranges[0]);
            AssertSelectiveColor(200, 100, 100, settings, 222, 100, 100);
            AssertSelectiveColor(255, 0, 0, settings, 255, 0, 0); // no cyan to remove
            AssertSelectiveColor(100, 200, 100, settings, 100, 200, 100);

            var black = new SelectiveColorRange[9];
            black[0] = new SelectiveColorRange(0, 0, 0, 100);
            AssertSelectiveColor(200, 100, 100, new SelectiveColorSettings(true, black), 122, 61, 61);

            var none = new SelectiveColorSettings(false, new SelectiveColorRange[9]);
            AssertSelectiveColor(17, 128, 250, none, 17, 128, 250);

            var image = RenderOver(new PsdColor(200, 100, 100), Block("selc", payload.ToArray()));
            Assert.AreEqual(new PsdColor(200, 100, 100), image.GetPixel(0, 0));
            Assert.IsNull(Adjustments.ParseSelectiveColor(new byte[20]));
        }

        // ---- Color Lookup -------------------------------------------------------------------

        /// <summary>A 2-node lattice swapping red and blue, in .cube order (red fastest).</summary>
        private const string SwapCube =
            "# swap red and blue\n" +
            "TITLE \"swap\"\n" +
            "LUT_3D_SIZE 2\n" +
            "0 0 0\n" +
            "0 0 1\n" +
            "0 1 0\n" +
            "0 1 1\n" +
            "1 0 0\n" +
            "1 0 1\n" +
            "1 1 0\n" +
            "1 1 1\n";

        [TestMethod]
        public void Color_lookup_reads_cube_files()
        {
            var swap = Adjustments.ParseLutFile(Encoding.ASCII.GetBytes(SwapCube));
            Assert.IsNotNull(swap);
            Assert.AreEqual(2, swap.Size);
            AssertColor(swap, 1f, 0f, 0f, 0f, 0f, 1f);
            AssertColor(swap, 0.25f, 0.5f, 0.75f, 0.75f, 0.5f, 0.25f);

            // A 1D table inverts each channel; DOMAIN_MAX rescales the input.
            var invert = Adjustments.ParseLutFile(Encoding.ASCII.GetBytes("LUT_1D_SIZE 3\nDOMAIN_MAX 2 2 2\n1 1 1\n0.5 0.5 0.5\n0 0 0\n"), "LUTFormatCUBE");
            Assert.IsNotNull(invert);
            AssertColor(invert, 0.5f, 1f, 3f, 0.75f, 0.5f, 0f);

            Assert.IsNull(Adjustments.ParseLutFile(Encoding.ASCII.GetBytes("LUT_3D_SIZE 2\n0 0 0\n")));            // rows missing
            Assert.IsNull(Adjustments.ParseLutFile(Encoding.ASCII.GetBytes("LUT_3D_SIZE 2\n0 0 x\n"), "LUTFormatCUBE"));
            Assert.IsNull(Adjustments.ParseLutFile(Encoding.ASCII.GetBytes("LUT_3D_SIZE 9999\n")));
        }

        [TestMethod]
        public void Color_lookup_reads_3dl_and_csp_files()
        {
            // .3dl: input positions, then 12-bit rows with blue varying fastest.
            var rows = new StringBuilder("3DMESH\nMesh 0 12\n0 1023\n");
            for (var r = 0; r < 2; r++)
            {
                for (var g = 0; g < 2; g++)
                {
                    for (var b = 0; b < 2; b++)
                    {
                        rows.Append((b * 4095) + " " + (g * 4095) + " " + (r * 4095) + "\n");
                    }
                }
            }

            var lattice = Adjustments.ParseLutFile(Encoding.ASCII.GetBytes(rows.ToString()), "LUTFormat3DL");
            Assert.IsNotNull(lattice);
            AssertColor(lattice, 1f, 0f, 0f, 0f, 0f, 1f);
            AssertColor(lattice, 0.9f, 0.6f, 0.2f, 0.2f, 0.6f, 0.9f);

            // .csp: pre-LUTs (here doubling the input range) and a red-fastest lattice.
            var csp = new StringBuilder("CSPLUTV100\n3D\n\nBEGIN METADATA\nnote\nEND METADATA\n");
            for (var c = 0; c < 3; c++)
            {
                csp.Append("2\n0 2\n0 1\n");
            }

            csp.Append("2 2 2\n");
            foreach (var line in SwapCube.Split('\n'))
            {
                if (line.Length > 0 && char.IsDigit(line[0]))
                {
                    csp.Append(line).Append('\n');
                }
            }

            var cineSpace = Adjustments.ParseLutFile(Encoding.ASCII.GetBytes(csp.ToString()));
            Assert.IsNotNull(cineSpace);
            AssertColor(cineSpace, 1f, 0f, 0.5f, 0.25f, 0f, 0.5f);
            Assert.IsNull(Adjustments.ParseLutFile(Encoding.ASCII.GetBytes("CSPLUTV100\n3D\n2\n0 1\n")));
        }

        [TestMethod]
        public void Color_lookup_reads_device_link_and_abstract_profiles()
        {
            var link = new IccBuilder { DeviceClass = "link", ColorSpace = "RGB ", Pcs = "RGB " }
                .Add("A2B0", IccBuilder.Lut(true, 3, 3, 2, delegate(double[] node) { return new[] { 1 - node[0], 1 - node[1], 1 - node[2] }; }))
                .Build();
            var inverted = Adjustments.ColorLookupFromProfile(link);
            Assert.IsNotNull(inverted);
            AssertColor(inverted, 0.2f, 0.5f, 0.9f, 0.8f, 0.5f, 0.1f);

            // An identity Lab abstract profile leaves colors where they were.
            var abstractProfile = new IccBuilder { DeviceClass = "abst", ColorSpace = "Lab ", Pcs = "Lab " }
                .Add("A2B0", IccBuilder.Lut(true, 3, 3, 2, delegate(double[] node) { return node; }))
                .Build();
            var identity = Adjustments.ColorLookupFromProfile(abstractProfile);
            Assert.IsNotNull(identity);
            AssertColor(identity, 0.2f, 0.5f, 0.9f, 0.2f, 0.5f, 0.9f, 0.01f);

            // A display profile is not a lookup.
            Assert.IsNull(Adjustments.ColorLookupFromProfile(TestProfiles.AdobeRgb()));
        }

        [TestMethod]
        public void Color_lookup_layers_render_only_when_asked()
        {
            var descriptor = new PsdBuilder.Writer();
            descriptor.U32(16);
            descriptor.UnicodeString(string.Empty);
            descriptor.DescriptorId("null");
            descriptor.U32(5);
            descriptor.DescriptorId("lookupType");
            descriptor.Ascii("enum");
            descriptor.DescriptorId("colorLookupType");
            descriptor.DescriptorId("3DLUT");
            descriptor.DescriptorId("Nm  ");
            descriptor.Ascii("TEXT");
            descriptor.UnicodeString("swap.cube");
            descriptor.DescriptorId("Dthr");
            descriptor.Ascii("bool");
            descriptor.U8(0);
            descriptor.DescriptorId("LUTFormat");
            descriptor.Ascii("enum");
            descriptor.DescriptorId("LUTFormatType");
            descriptor.DescriptorId("LUTFormatCUBE");
            descriptor.DescriptorId("LUT3DFileData");
            descriptor.Ascii("tdta");
            descriptor.U32((uint)Encoding.ASCII.GetByteCount(SwapCube));
            descriptor.Bytes(Encoding.ASCII.GetBytes(SwapCube));

            var payload = new PsdBuilder.Writer();
            payload.U16(1);
            payload.Bytes(descriptor.ToArray());

            var table = Adjustments.ParseColorLookup(payload.ToArray());
            Assert.IsNotNull(table);
            AssertColor(table, 1f, 0f, 0f, 0f, 0f, 1f);

            var document = LoadOver(new PsdColor(200, 100, 50), Block("clrL", payload.ToArray()));
            Assert.AreEqual(new PsdColor(200, 100, 50), document.Render(new RenderOptions { Source = RenderSource.Layers }).GetPixel(0, 0));
            Assert.AreEqual(new PsdColor(50, 100, 200), document.Render(new RenderOptions { Source = RenderSource.Layers, RenderColorLookup = true }).GetPixel(0, 0));

            // psd-tools' color-lookup.psd: version 1 and an empty descriptor, so nothing to apply.
            Assert.IsNull(Adjustments.ParseColorLookup(Hex("000100000010000000010000000000006E756C6C00000000")));
        }

        // ---- Gradient Map -------------------------------------------------------------------

        [TestMethod]
        public void Gradient_map_reads_photoshop_payloads()
        {
            var first = Adjustments.ParseGradientMap(Hex(GradientMapV1));
            Assert.IsNotNull(first);
            Assert.IsFalse(first.Value.Reverse);
            var v1 = first.Value.Gradient;
            Assert.IsNull(v1.Noise);
            Assert.AreEqual(2, v1.ColorStops.Count);
            Assert.AreEqual(new PsdColor(123, 91, 91), v1.ColorStops[0].Color);
            Assert.AreEqual(PsdColor.White, v1.ColorStops[1].Color);
            Assert.AreEqual(0.5f, v1.ColorStops[1].Midpoint);
            Assert.AreEqual(1f, v1.Smoothness);

            var third = Adjustments.ParseGradientMap(Hex(GradientMapV3));
            Assert.IsNotNull(third);
            var v3 = third.Value.Gradient;
            Assert.AreEqual(PsdGradientInterpolation.Classic, v3.Interpolation);
            Assert.AreEqual(new PsdColor(255, 0, 0), v3.ColorStops[0].Color);

            var lut = Adjustments.BuildGradientMapLut(v3, false);
            Assert.AreEqual(255, lut.Red[0]);
            Assert.AreEqual(0, lut.Green[0]);
            Assert.AreEqual(0, lut.Blue[0]);
            Assert.AreEqual(255, lut.Red[255]);
            Assert.AreEqual(255, lut.Green[255]);
            Assert.AreEqual(255, lut.Blue[255]);
            Assert.AreEqual(255, lut.Red[128]);
            Assert.IsTrue(lut.Green[128] >= 120 && lut.Green[128] <= 135, "green 128 = " + lut.Green[128]);
            for (var v = 1; v < 256; v++)
            {
                Assert.IsTrue(lut.Green[v] >= lut.Green[v - 1]);
            }

            var reversed = Adjustments.BuildGradientMapLut(v3, true);
            Assert.AreEqual(255, reversed.Red[255]);
            Assert.AreEqual(0, reversed.Green[255]);
            Assert.AreEqual(0, reversed.Blue[255]);

            Assert.IsNull(Adjustments.ParseGradientMap(new byte[] { 0, 2, 0, 0 }));
            Assert.IsNull(Adjustments.ParseGradientMap(Slice(Hex(GradientMapV1), 0, 40)));
        }

        [TestMethod]
        public void Gradient_map_maps_luminosity_through_the_ramp()
        {
            var payload = Hex(GradientMapV3);
            var parsed = Adjustments.ParseGradientMap(payload);
            Assert.IsNotNull(parsed);
            var lut = Adjustments.BuildGradientMapLut(parsed.Value.Gradient, false);

            // Threshold's 30/59/11 luminosity picks the entry.
            var image = RenderOver(new PsdColor(200, 100, 0), Block("grdm", payload));
            var index = Adjustments.ThresholdLuminance(200, 100, 0);
            Assert.AreEqual(new PsdColor(lut.Red[index], lut.Green[index], lut.Blue[index]), image.GetPixel(0, 0));

            // The reverse byte flips the ramp.
            payload[2] = 1;
            image = RenderOver(new PsdColor(0, 0, 0), Block("grdm", payload));
            Assert.AreEqual(PsdColor.White, image.GetPixel(0, 0));
        }

        // ---- Photo Filter -------------------------------------------------------------------

        [TestMethod]
        public void Photo_filter_reads_lab_colors_and_matches_photoshop_on_white()
        {
            var warmingValue = Adjustments.ParsePhotoFilter(Hex(WarmingFilter));
            Assert.IsNotNull(warmingValue);
            var warming = warmingValue.Value;
            Assert.AreEqual(25, warming.Density);
            Assert.IsTrue(warming.PreserveLuminosity);
            Assert.IsTrue(warming.Red * 255 >= 233 && warming.Red * 255 <= 239); // Photoshop lists Warming Filter (85) as (236, 138, 0)
            Assert.IsTrue(warming.Green * 255 >= 130 && warming.Green * 255 <= 140);
            Assert.IsTrue(warming.Blue * 255 >= 0 && warming.Blue * 255 <= 1);

            // In fill_adjustments.psd the layer turns (217, 212, 209) into Photoshop's (230, 207, 200).
            var applied = Adjustments.PhotoFilterValue(217, 212, 209, warming);
            InRange(applied.R, 228, 232);
            InRange(applied.G, 205, 209);
            InRange(applied.B, 198, 202);

            // Version 3 stores the same Lab color as i32 hundredths.
            var v3 = new PsdBuilder.Writer();
            v3.U16(3);
            v3.I32(6706);
            v3.I32(3200);
            v3.I32(12000);
            v3.U32(25);
            v3.U8(1);
            var parsedV3Value = Adjustments.ParsePhotoFilter(v3.ToArray());
            Assert.IsNotNull(parsedV3Value);
            var parsedV3 = parsedV3Value.Value;
            Assert.AreEqual(warming.Red, parsedV3.Red);
            Assert.AreEqual(warming.Green, parsedV3.Green);
            Assert.AreEqual(warming.Blue, parsedV3.Blue);
            Assert.AreEqual(warming.Density, parsedV3.Density);
            Assert.AreEqual(warming.PreserveLuminosity, parsedV3.PreserveLuminosity);

            // Without Preserve Luminosity the filter only darkens; at density 0 it is the identity.
            var dark = Adjustments.PhotoFilterValue(200, 200, 200, WithPreserve(warming, false));
            Assert.IsTrue(dark.R <= 200 && dark.G < 200 && dark.B < 200);
            var identity = Adjustments.PhotoFilterValue(10, 20, 30, WithDensity(warming, 0, false));
            Assert.AreEqual(10, identity.R);
            Assert.AreEqual(20, identity.G);
            Assert.AreEqual(30, identity.B);
            Assert.IsNull(Adjustments.ParsePhotoFilter(new byte[] { 0, 4, 0, 0 }));
        }

        // ---- Vibrance -----------------------------------------------------------------------

        [TestMethod]
        public void Vibrance_favors_less_saturated_colors()
        {
            var parsed = Adjustments.ParseVibrance(Hex(VibranceMinus6));
            Assert.IsNotNull(parsed);
            Assert.AreEqual(new VibranceSettings(-6, 2), parsed.Value);

            var none = new VibranceSettings(0, 0);
            AssertVibrance(130, 120, 110, none, 130, 120, 110);
            var boost = new VibranceSettings(50, 0);
            AssertVibrance(90, 90, 90, boost, 90, 90, 90);

            // A dull color gains more chroma than a saturated one.
            var dull = Adjustments.VibranceValue(130, 120, 110, boost);
            var vivid = Adjustments.VibranceValue(250, 40, 20, boost);
            Assert.IsTrue((dull.R - dull.B) / 20.0 > (vivid.R - vivid.B) / 230.0);
            Assert.IsTrue(dull.R - dull.B > 25);

            var desaturate = Adjustments.VibranceValue(130, 120, 110, new VibranceSettings(0, -100));
            Assert.IsTrue(Math.Abs(desaturate.R - desaturate.B) <= 1);
        }

        [TestMethod]
        public void Photoshop_chain_with_photo_filter_and_vibrance_matches()
        {
            // psd-tools' fill_adjustments.psd stacks every adjustment kind over three effect
            // rectangles; with Photo Filter, Vibrance and Color Balance rendered the layer
            // composite is within these bounds of Photoshop's (all three left out: max 29, mean 8.4).
            var document = PsdDocument.Load(Fixtures.PsdTools("fill_adjustments.psd"));
            var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), document.GetMergedImage());
            Assert.IsTrue(diff.MaxDelta <= 33 && diff.MeanDelta <= 4.3, diff.ToString());
        }

        [TestMethod]
        public void Photoshop_noise_gradient_stroke_stays_within_its_bounds()
        {
            // psd-tools' effect-stroke-gradient.psd strokes with a noise gradient. Photoshop's random
            // sequence is unknown, so the bands differ; the deterministic noise field brought the
            // composite from max 245 / mean 14.8 (a gray ramp) to max 152 / mean 9.0.
            var document = PsdDocument.Load(Fixtures.PsdTools("effect-stroke-gradient.psd"));
            var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), document.GetMergedImage());
            Assert.IsTrue(diff.MaxDelta <= 160 && diff.MeanDelta <= 9.5, diff.ToString());
        }

        // ---- Noise gradients ----------------------------------------------------------------

        [TestMethod]
        public void Noise_gradients_are_deterministic_and_respect_their_ranges()
        {
            var noise = new PsdGradientNoise(991827, 3171, true, PsdGradientNoiseModel.Rgb, new[] { 20.0, 0.0, 0.0, 0.0 }, new[] { 40.0, 100.0, 100.0, 100.0 });
            var values = new HashSet<double>();
            for (var i = 0; i <= 64; i++)
            {
                var position = i / 64.0;
                Assert.AreEqual(noise.Channel(1, position), noise.Channel(1, position));
                Assert.IsTrue(noise.Channel(0, position) >= 0.2 && noise.Channel(0, position) <= 0.4);
                values.Add(noise.Channel(1, position));
            }

            Assert.IsTrue(values.Count > 32);
            var otherSeed = new PsdGradientNoise(5, 3171, true, PsdGradientNoiseModel.Rgb, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 100.0, 100.0, 100.0, 100.0 });
            Assert.AreNotEqual(noise.Channel(1, 0.37), otherSeed.Channel(1, 0.37));

            // Roughness 0 flattens the field to the middle of each range.
            var smooth = new PsdGradientNoise(7, 0, false, PsdGradientNoiseModel.Rgb, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 100.0, 100.0, 100.0, 100.0 });
            var flat = smooth.Color(0.3);
            Assert.AreEqual(128 / 255.0, flat.R, 1e-6);
            Assert.AreEqual(128 / 255.0, flat.G, 1e-6);
            Assert.AreEqual(128 / 255.0, flat.B, 1e-6);
            Assert.AreEqual(0.5, smooth.Channel(3, 0.9));

            // HSB and Lab models produce colors in range; transparency only with ShTr.
            var gradient = Gradient.FromStops(new PsdGradientColorStop[0], new PsdGradientAlphaStop[0], 1f, PsdGradientInterpolation.Classic, noise);
            Assert.IsTrue(gradient.Opacity(0.5f, false) >= 0f && gradient.Opacity(0.5f, false) <= 1f);
            var smoothGradient = Gradient.FromStops(new PsdGradientColorStop[0], new PsdGradientAlphaStop[0], 1f, PsdGradientInterpolation.Classic, smooth);
            Assert.AreEqual(1f, smoothGradient.Opacity(0.5f, false));
            foreach (var model in new[] { PsdGradientNoiseModel.Hsb, PsdGradientNoiseModel.Lab })
            {
                var modelNoise = new PsdGradientNoise(3, 4096, false, model, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 100.0, 100.0, 100.0, 100.0 });
                var modelColor = modelNoise.Color(0.61);
                InRange(modelColor.R, 0f, 1f);
                InRange(modelColor.G, 0f, 1f);
                InRange(modelColor.B, 0f, 1f);
            }
        }

        [TestMethod]
        public void Noise_gradient_fill_layers_render_the_noise_field_across_the_canvas()
        {
            // A GdFl layer without pixels and without Angl runs at 0 degrees: the noise varies
            // along x and is constant down each column.
            var fill = Descriptor(delegate(PsdBuilder.Writer writer)
            {
                writer.DescriptorId("Type");
                writer.Ascii("enum");
                writer.DescriptorId("GrdT");
                writer.DescriptorId("Lnr ");
                writer.DescriptorId("Grad");
                writer.Ascii("Objc");
                writer.UnicodeString("Noise");
                writer.DescriptorId("Grdn");
                writer.U32(8);
                writer.DescriptorId("GrdF");
                writer.Ascii("enum");
                writer.DescriptorId("GrdF");
                writer.DescriptorId("ClNs");
                writer.DescriptorId("ShTr");
                writer.Ascii("bool");
                writer.U8(0);
                writer.DescriptorId("VctC");
                writer.Ascii("bool");
                writer.U8(1);
                writer.DescriptorId("ClrS");
                writer.Ascii("enum");
                writer.DescriptorId("ClrS");
                writer.DescriptorId("RGBC");
                Long(writer, "RndS", 12345);
                Long(writer, "Smth", 4096);
                Range(writer, "Mnm ", 0);
                Range(writer, "Mxm ", 100);
            }, 2);
            var builder = new PsdBuilder { Width = 32, Height = 4 };
            var layer = new BuilderLayer { Name = "Noise", Rect = default(PsdRect) };
            layer.Blocks.Add(new KeyValuePair<string, byte[]>("GdFl", fill));
            builder.Layers.Add(layer);
            var image = PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

            var expected = Gradient.FromStops(
                new PsdGradientColorStop[0],
                new PsdGradientAlphaStop[0],
                1f,
                PsdGradientInterpolation.Classic,
                new PsdGradientNoise(12345, 4096, false, PsdGradientNoiseModel.Rgb, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 100.0, 100.0, 100.0, 100.0 }));
            expected.AngleDegrees = 0;
            var distinct = new HashSet<PsdColor>();
            for (var x = 0; x < 32; x++)
            {
                var color = image.GetPixel(x, 0);
                Assert.AreEqual(color, image.GetPixel(x, 3));
                distinct.Add(color);
            }

            Assert.IsTrue(distinct.Count > 8);
            var position = expected.Position(new PsdRect(0, 0, 32, 4), 5, 0, GradientSpan.CenterChord);
            float r, g, b;
            expected.Color(position, true, out r, out g, out b);
            Assert.AreEqual(new PsdColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)), image.GetPixel(5, 0));
        }

        // ---- Helpers ------------------------------------------------------------------------

        private static BlackWhiteSettings WithTint(BlackWhiteSettings settings, PsdColor tint)
        {
            return new BlackWhiteSettings(settings.Reds, settings.Yellows, settings.Greens, settings.Cyans, settings.Blues, settings.Magentas, true, tint);
        }

        private static PhotoFilterSettings WithPreserve(PhotoFilterSettings settings, bool preserve)
        {
            return new PhotoFilterSettings(settings.Red, settings.Green, settings.Blue, settings.Density, preserve);
        }

        private static PhotoFilterSettings WithDensity(PhotoFilterSettings settings, int density, bool preserve)
        {
            return new PhotoFilterSettings(settings.Red, settings.Green, settings.Blue, density, preserve);
        }

        private static KeyValuePair<string, byte[]> Block(string key, byte[] data)
        {
            return new KeyValuePair<string, byte[]>(key, data);
        }

        private static byte[] Descriptor(Action<PsdBuilder.Writer> items, int count)
        {
            var writer = new PsdBuilder.Writer();
            writer.U32(16);
            writer.UnicodeString(string.Empty);
            writer.DescriptorId("null");
            writer.U32((uint)count);
            items(writer);
            return writer.ToArray();
        }

        private static void Long(PsdBuilder.Writer writer, string key, int value)
        {
            writer.DescriptorId(key);
            writer.Ascii("long");
            writer.I32(value);
        }

        private static void Range(PsdBuilder.Writer writer, string key, int value)
        {
            writer.DescriptorId(key);
            writer.Ascii("VlLs");
            writer.U32(4);
            for (var i = 0; i < 4; i++)
            {
                writer.Ascii("doub");
                writer.F64(value - 0.0012);
            }
        }

        private static RgbaImage RenderOver(PsdColor color, params KeyValuePair<string, byte[]>[] blocks)
        {
            return LoadOver(color, blocks).Render(new RenderOptions { Source = RenderSource.Layers });
        }

        private static PsdDocument LoadOver(PsdColor color, params KeyValuePair<string, byte[]>[] blocks)
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var background = new BuilderLayer { Name = "Background", Rect = new PsdRect(0, 0, 2, 2) };
            background.Channels[-1] = PsdBuilder.Plane8(2, 2, 255);
            background.Channels[0] = PsdBuilder.Plane8(2, 2, color.R);
            background.Channels[1] = PsdBuilder.Plane8(2, 2, color.G);
            background.Channels[2] = PsdBuilder.Plane8(2, 2, color.B);
            builder.Layers.Add(background);
            var adjustment = new BuilderLayer { Name = "Adjustment", Rect = default(PsdRect) };
            adjustment.Blocks.AddRange(blocks);
            builder.Layers.Add(adjustment);
            return PsdDocument.Load(builder.Build());
        }

        private static void AssertColor(ColorLookupTable table, float r, float g, float b, float er, float eg, float eb, float tolerance = 1e-4f)
        {
            float or, og, ob;
            table.Apply(r, g, b, out or, out og, out ob);
            Assert.IsTrue(
                Math.Abs(er - or) <= tolerance && Math.Abs(eg - og) <= tolerance && Math.Abs(eb - ob) <= tolerance,
                "expected (" + er + ", " + eg + ", " + eb + "), got (" + or + ", " + og + ", " + ob + ")");
        }

        private static void AssertBW(int r, int g, int b, BlackWhiteSettings settings, int er, int eg, int eb)
        {
            var mapped = Adjustments.BlackWhiteValue(r, g, b, settings);
            Assert.AreEqual(er, mapped.R, "R");
            Assert.AreEqual(eg, mapped.G, "G");
            Assert.AreEqual(eb, mapped.B, "B");
        }

        private static void AssertSelectiveColor(int r, int g, int b, SelectiveColorSettings settings, int er, int eg, int eb)
        {
            var mapped = Adjustments.SelectiveColorValue(r, g, b, settings);
            Assert.AreEqual(er, mapped.R, "R");
            Assert.AreEqual(eg, mapped.G, "G");
            Assert.AreEqual(eb, mapped.B, "B");
        }

        private static void AssertVibrance(int r, int g, int b, VibranceSettings settings, int er, int eg, int eb)
        {
            var mapped = Adjustments.VibranceValue(r, g, b, settings);
            Assert.AreEqual(er, mapped.R, "R");
            Assert.AreEqual(eg, mapped.G, "G");
            Assert.AreEqual(eb, mapped.B, "B");
        }

        private static void InRange(int actual, int low, int high)
        {
            Assert.IsTrue(actual >= low && actual <= high, actual + " not in [" + low + ", " + high + "]");
        }

        private static void InRange(double actual, double low, double high)
        {
            Assert.IsTrue(actual >= low && actual <= high, actual + " not in [" + low + ", " + high + "]");
        }

        private static byte[] Slice(byte[] data, int start, int length)
        {
            var slice = new byte[length];
            Array.Copy(data, start, slice, 0, length);
            return slice;
        }

        private static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
