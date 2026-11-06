using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Colour conversion coverage for the ported imaging layer: the sRGB transfer
    /// curve, the Lab (D50) transform, the per-mode conversions and the duotone ink
    /// mix. Values are the ones the original suite pins for these formulas.
    /// </summary>
    [TestClass]
    public sealed class ColorSpaceTests
    {
        [TestMethod]
        public void Lab_white_and_black_map_to_the_srgb_ends()
        {
            double r, g, b;
            ColorSpaces.LabToSrgb(100, 0, 0, out r, out g, out b);
            Assert.AreEqual(1.0, r, 0.001);
            Assert.AreEqual(1.0, g, 0.001);
            Assert.AreEqual(1.0, b, 0.001);

            ColorSpaces.LabToSrgb(0, 0, 0, out r, out g, out b);
            Assert.AreEqual(0.0, r, 0.001);
            Assert.AreEqual(0, g, 0.001);
            Assert.AreEqual(0, b, 0.001);
        }

        [TestMethod]
        public void Srgb_transfer_curve_matches_the_standard()
        {
            Assert.AreEqual(0.7354, ColorSpaces.EncodeSrgb(0.5), 0.0005);
            Assert.AreEqual(0.04045, ColorSpaces.EncodeSrgb(0.0031308), 0.0005);
            Assert.AreEqual(0.5, IccPcs.DecodeSrgb(0.7354), 0.0005);
            Assert.AreEqual(0.0031308, IccPcs.DecodeSrgb(0.04045), 0.0000005);
        }

        [TestMethod]
        public void Cmyk_planes_multiply_into_rgb()
        {
            // PSD CMYK samples are inverted (1 = no ink): C'=1 M'=1 Y'=1 K'=1 is white,
            // K'=0 makes everything black.
            var image = new PlanarImage(new PsdRect(0, 0, 2, 1));
            var planes = new[]
            {
                new[] { 1f, 1f },
                new[] { 1f, 0.5f },
                new[] { 1f, 0.5f },
                new[] { 1f, 0f },
            };

            ColorSpaces.ToRgb(PsdColorMode.Cmyk, 8, planes, image, null);

            Assert.AreEqual(1f, image.R[0], 0.0001);
            Assert.AreEqual(0f, image.R[1], 0.0001);
            Assert.AreEqual(0f, image.G[1], 0.0001);
        }

        [TestMethod]
        public void Gray_planes_copy_into_all_three_channels()
        {
            var image = new PlanarImage(new PsdRect(0, 0, 3, 1));
            ColorSpaces.ToRgb(PsdColorMode.Grayscale, 8, new[] { new[] { 0.2f, 0.5f, 0.8f } }, image, null);

            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(image.R[i], image.G[i]);
                Assert.AreEqual(image.R[i], image.B[i]);
            }

            Assert.AreEqual(0.5f, image.G[1], 0.0001);
        }

        [TestMethod]
        public void Indexed_planes_resolve_through_the_palette()
        {
            var image = new PlanarImage(new PsdRect(0, 0, 2, 1));
            var palette = new[] { new PsdColor(255, 0, 0), new PsdColor(0, 255, 0) };

            // Indexed planes hold the palette index divided by 255.
            ColorSpaces.ToRgb(PsdColorMode.Indexed, 8, new[] { new[] { 0f, 1f / 255f } }, image, palette);

            Assert.AreEqual(1f, image.R[0], 0.0001);
            Assert.AreEqual(0f, image.G[0], 0.0001);
            Assert.AreEqual(1f, image.G[1], 0.0001);
        }

        [TestMethod]
        public void Duotone_with_a_linear_black_ink_matches_the_gray_path()
        {
            var duotone = Duotone.Parse(Specification());
            Assert.IsNotNull(duotone);
            Assert.AreEqual(1, duotone.Inks.Length);
            Assert.AreEqual("Black", duotone.Inks[0].Name);

            double r, g, b;
            duotone.Evaluate(0.5, null, out r, out g, out b);
            Assert.AreEqual(0.5, r, 0.02);
            Assert.AreEqual(0.5, g, 0.02);
            Assert.AreEqual(0.5, b, 0.02);

            // Applied to a plane: the table indexes by gray, 0 = black, 1 = white.
            var image = new PlanarImage(new PsdRect(0, 0, 2, 1));
            Assert.IsTrue(duotone.Apply(new[] { 0f, 1f }, 8, null, image));
            Assert.AreEqual(0f, image.R[0], 0.02);
            Assert.AreEqual(1f, image.R[1], 0.02);
        }

        [TestMethod]
        public void Duotone_without_a_specification_keeps_the_gray_path()
        {
            var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
            ColorSpaces.ToRgb(PsdColorMode.Duotone, 8, new[] { new[] { 0.25f } }, image, null);
            Assert.AreEqual(0.25f, image.R[0], 0.0001);
            Assert.IsNull(Duotone.Parse(new byte[10]));
        }

        [TestMethod]
        public void Planar_images_interleave_with_the_documented_rounding()
        {
            var image = new PlanarImage(new PsdRect(0, 0, 2, 1));
            image.Fill(0.5f, 0.25f, 0f, 1f);

            var rgba = image.ToRgba(new PsdRect(0, 0, 2, 1));

            Assert.AreEqual(128, rgba.GetPixel(0, 0).R);
            Assert.AreEqual(64, rgba.GetPixel(0, 0).G);
            Assert.AreEqual(0, rgba.GetPixel(0, 0).B);
            Assert.AreEqual(255, rgba.GetPixel(0, 0).A);
            Assert.IsTrue(rgba.IsOpaque);
        }

        [TestMethod]
        public void Transparent_pixels_stay_zero_and_flatten_over_a_background()
        {
            var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
            image.Fill(1f, 0f, 0f, 0f);
            var rgba = image.ToRgba(new PsdRect(0, 0, 1, 1));

            Assert.AreEqual(0, rgba.Pixels[0]);
            Assert.AreEqual(0, rgba.Pixels[3]);

            var flat = rgba.Flatten(PsdColor.White);
            Assert.IsTrue(flat.IsOpaque);
            Assert.AreEqual(255, flat.GetPixel(0, 0).R);
            Assert.AreEqual(255, flat.GetPixel(0, 0).G);
        }

        /// <summary>A one-ink duotone: gray ink 8 (0 = black at 100%), linear 13-point curve.</summary>
        private static byte[] Specification()
        {
            var data = new byte[412];
            data[0] = 0;
            data[1] = 1; // version 1
            data[2] = 0;
            data[3] = 1; // one ink

            // Ink colour: space 8 (grayscale), level 0 = black, in the first of four slots.
            var color = 4;
            data[color + 1] = 8;
            data[color + 2] = 0;
            data[color + 3] = 0;

            // Name: Pascal string "Black" in the first 64-byte slot.
            var name = 4 + 40;
            data[name] = 5;
            var text = new byte[] { (byte)'B', (byte)'l', (byte)'a', (byte)'c', (byte)'k' };
            Array.Copy(text, 0, data, name + 1, text.Length);

            // Curves start after the four names; curve 0 is linear: 0, 83, 167 ... 1000.
            var curve = name + (4 * 64);
            for (var p = 0; p < 13; p++)
            {
                var value = (short)Math.Round(p * 1000.0 / 12.0);
                data[curve + (p * 2)] = (byte)(value >> 8);
                data[curve + (p * 2) + 1] = (byte)value;
            }

            return data;
        }
    }
}
