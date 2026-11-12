using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// M1 port: the merged image section decodes under every compression, and layer
    /// pixels and mask planes decode through the same channel codec.
    /// </summary>
    [TestClass]
    public sealed class PixelDecodeTests
    {
        [TestMethod]
        public void Merged_image_decodes_under_every_compression()
        {
            foreach (var compression in new[] { PsdCompression.Raw, PsdCompression.Rle, PsdCompression.Zip, PsdCompression.ZipPrediction })
            {
                var builder = RedDocument(compression);
                var pixels = PsdDocument.FromBytes(builder.Build()).GetMergedImage();
                Assert.AreEqual(255, pixels.GetPixel(0, 0).R, "R at " + compression);
                Assert.AreEqual(0, pixels.GetPixel(1, 1).G, "G at " + compression);
                Assert.AreEqual(255, pixels.GetPixel(1, 1).A, "A at " + compression);
            }
        }

        [TestMethod]
        public void Merged_image_transparency_channel_becomes_alpha()
        {
            var builder = RedDocument(PsdCompression.Raw);
            builder.MergedTransparency = true; // negative layer count in the layer info
            builder.Layers.Add(new BuilderLayer { Name = "base", Rect = new PsdRect(0, 0, 2, 2) });
            builder.MergedChannels.Add(PsdBuilder.Plane8(2, 2, 128));

            var pixels = PsdDocument.FromBytes(builder.Build()).GetMergedImage();

            Assert.AreEqual(255, pixels.GetPixel(0, 0).R);
            Assert.AreEqual(128, pixels.GetPixel(0, 0).A);
        }

        [TestMethod]
        public void Layer_pixels_decode_through_GetPixels()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var layer = new BuilderLayer { Name = "px", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 10);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 20);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 30);
            builder.Layers.Add(layer);

            var pixels = PsdDocument.FromBytes(builder.Build()).Layers.Single().GetPixels();

            Assert.IsNotNull(pixels);
            Assert.AreEqual(10, pixels.GetPixel(0, 0).R);
            Assert.AreEqual(20, pixels.GetPixel(1, 1).G);
            Assert.AreEqual(30, pixels.GetPixel(0, 1).B);
        }

        [TestMethod]
        public void Layer_mask_plane_decodes_for_its_own_rectangle()
        {
            var builder = new PsdBuilder { Width = 4, Height = 6 };
            var layer = new BuilderLayer { Name = "masked", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 255);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
            layer.MaskRect = new PsdRect(1, 2, 4, 6); // 3 x 4
            layer.Channels[-2] = PsdBuilder.Plane8(3, 4, 100);
            builder.Layers.Add(layer);

            var parsed = PsdDocument.FromBytes(builder.Build()).Layers.Single();
            var plane = parsed.DecodeMask(parsed.Mask);

            Assert.IsNotNull(plane);
            Assert.AreEqual(12, plane.Length);
            Assert.AreEqual(100 / 255.0, plane[0], 0.002);
        }

        [TestMethod]
        public void Banded_merged_decode_matches_the_whole()
        {
            var builder = RedDocument(PsdCompression.Rle);
            builder.Width = 4;
            builder.Height = 4;
            builder.MergedChannels.Clear();
            var ramp = PsdBuilder.Plane8(4, 4, (x, y) => (byte)(x * 17 + y * 53));
            builder.MergedChannels.Add(ramp);
            builder.MergedChannels.Add(PsdBuilder.Plane8(4, 4, 5));
            builder.MergedChannels.Add(PsdBuilder.Plane8(4, 4, 9));
            var document = PsdDocument.FromBytes(builder.Build());

            var whole = MergedImageDecoder.Decode(document);
            var bands = new MergedImageBands();
            var top = MergedImageDecoder.Decode(document, 0, 2, bands);
            var bottom = MergedImageDecoder.Decode(document, 2, 4, bands);

            for (var y = 0; y < 4; y++)
            {
                var part = y < 2 ? top : bottom;
                for (var x = 0; x < 4; x++)
                {
                    // RowOffset takes document coordinates for both images.
                    Assert.AreEqual(whole.R[whole.RowOffset(y, x)], part.R[part.RowOffset(y, x)], "R " + x + "," + y);
                    Assert.AreEqual(whole.B[whole.RowOffset(y, x)], part.B[part.RowOffset(y, x)], "B " + x + "," + y);
                }
            }
        }

        private static PsdBuilder RedDocument(PsdCompression compression)
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            builder.MergedCompression = compression;
            builder.MergedChannels.Add(PsdBuilder.Plane8(2, 2, 255));
            builder.MergedChannels.Add(PsdBuilder.Plane8(2, 2, 0));
            builder.MergedChannels.Add(PsdBuilder.Plane8(2, 2, 0));
            return builder;
        }
    }
}