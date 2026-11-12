using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Port of the original ResourceTests core: every raw block is kept, the typed
    /// views parse on first access, and a damaged resource yields null instead of
    /// an exception.
    /// </summary>
    [TestClass]
    public sealed class ResourceTests
    {
        [TestMethod]
        public void Every_resource_block_is_kept_raw()
        {
            var document = Document(
                Res(1037, "angle", new byte[] { 0, 0, 0x2D, 0x70 }),
                Res(1005, "", ResolutionPayload(300)),
                Res(1033, "thumb", new byte[] { 1, 2 }));

            Assert.AreEqual(3, document.ImageResources.Count);
            Assert.AreEqual(1037, document.ImageResources[0].Id);
            Assert.AreEqual("angle", document.ImageResources[0].Name);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, document.ImageResources[2].Data);
            Assert.AreSame(document.ImageResources[1], document.GetImageResource(1005));
            Assert.IsNull(document.GetImageResource(1099));
        }

        [TestMethod]
        public void Resolution_resource_parses()
        {
            var resources = Resources(Res(1005, "", ResolutionPayload(300)));

            Assert.IsNotNull(resources.Resolution);
            Assert.AreEqual(300.0, resources.Resolution.HorizontalPpi, 0.001);
            Assert.AreEqual(300.0, resources.Resolution.VerticalPpi, 0.001);
            Assert.AreEqual(PsdResolutionUnit.PixelsPerInch, resources.Resolution.HorizontalDisplayUnit);
        }

        [TestMethod]
        public void Grid_and_guides_parse()
        {
            var payload = new List<byte>();
            Add(payload, U32(1));            // version
            Add(payload, I32(32 * 24));      // horizontal cycle: 24 px
            Add(payload, I32(32 * 36));      // vertical cycle: 36 px
            Add(payload, U32(1));            // one guide
            Add(payload, I32(32 * 10));      // position: 10 px
            Add(payload, new byte[] { 1 });  // horizontal

            var resources = Resources(Res(1032, "", payload.ToArray()));

            Assert.IsNotNull(resources.GridAndGuides);
            Assert.AreEqual(24.0, resources.GridAndGuides.HorizontalGridCycle, 0.001);
            Assert.AreEqual(36.0, resources.GridAndGuides.VerticalGridCycle, 0.001);
            Assert.AreEqual(1, resources.GridAndGuides.Guides.Count);
            Assert.AreEqual(10.0, resources.GridAndGuides.Guides[0].Position, 0.001);
            Assert.AreEqual(PsdGuideOrientation.Horizontal, resources.GridAndGuides.Guides[0].Orientation);
        }

        [TestMethod]
        public void Version_info_and_xmp_parse()
        {
            var resources = Resources(
                Res(1057, "", VersionInfoPayload("XRay.PsdFx", "Photoshop")),
                Res(1060, "", Encoding.UTF8.GetBytes("<x:xmpmeta>hello</x:xmpmeta>\0")));

            Assert.IsNotNull(resources.VersionInfo);
            Assert.AreEqual(1, resources.VersionInfo.Version);
            Assert.IsTrue(resources.VersionInfo.HasRealMergedData);
            Assert.AreEqual("XRay.PsdFx", resources.VersionInfo.WriterName);
            Assert.AreEqual("Photoshop", resources.VersionInfo.ReaderName);
            Assert.AreEqual("<x:xmpmeta>hello</x:xmpmeta>", resources.Xmp);
        }

        [TestMethod]
        public void Iptc_datasets_parse_in_file_order()
        {
            var text = Encoding.UTF8.GetBytes("Test");
            var payload = new List<byte>();
            Add(payload, new byte[] { 0x1C, 0x02, 0x05, (byte)(text.Length >> 8), (byte)text.Length });
            Add(payload, text);
            Add(payload, new byte[] { 0x1C, 0x02, 0x19, 0x00, 0x01, 0x41 }); // "A"

            var resources = Resources(Res(1028, "", payload.ToArray()));

            Assert.AreEqual(2, resources.Iptc.Count);
            Assert.AreEqual(2, resources.Iptc[0].Record);
            Assert.AreEqual(5, resources.Iptc[0].DataSet);
            Assert.AreEqual("Test", PsdImageResources.DecodeIptcText(resources.Iptc[0].Value));
            Assert.AreEqual("A", PsdImageResources.DecodeIptcText(resources.Iptc[1].Value));
        }

        [TestMethod]
        public void Channel_names_prefer_the_unicode_resource()
        {
            var unicode = new List<byte>();
            Add(unicode, UnicodeString("Alpha 1"));
            Add(unicode, UnicodeString("Alpha 2"));
            var pascal = new List<byte>();
            Add(pascal, new byte[] { 6 });
            Add(pascal, Encoding.ASCII.GetBytes("Pascal"));

            var resources = Resources(Res(1045, "", unicode.ToArray()), Res(1006, "", pascal.ToArray()));
            Assert.AreEqual(2, resources.ChannelNames.Count);
            Assert.AreEqual("Alpha 1", resources.ChannelNames[0]);
            Assert.AreEqual("Alpha 2", resources.ChannelNames[1]);

            var fallback = Resources(Res(1006, "", pascal.ToArray()));
            Assert.AreEqual(1, fallback.ChannelNames.Count);
            Assert.AreEqual("Pascal", fallback.ChannelNames[0]);
        }

        [TestMethod]
        public void Print_scale_and_pixel_aspect_ratio_parse()
        {
            var scale = new List<byte>();
            Add(scale, U16(1));
            Add(scale, F32(0.5f));
            Add(scale, F32(0.25f));
            Add(scale, F32(2.0f));

            var resources = Resources(Res(1062, "", scale.ToArray()), Res(1064, "", AspectPayload(1.5)));

            Assert.IsNotNull(resources.PrintScale);
            Assert.AreEqual(PsdPrintScaleStyle.SizeToFit, resources.PrintScale.Style);
            Assert.AreEqual(2.0, resources.PrintScale.Scale, 0.0001);
            Assert.AreEqual(1.5, resources.PixelAspectRatio.Value, 0.0001);
        }

        [TestMethod]
        public void Thumbnail_falls_back_to_the_legacy_resource()
        {
            var payload = new List<byte>();
            Add(payload, U32(0));   // format 0 = raw RGB
            Add(payload, U32(2));   // width
            Add(payload, U32(2));   // height
            Add(payload, U32(6));   // row bytes
            Add(payload, U32(24));  // total size
            Add(payload, U32(0));   // compressed size
            Add(payload, U16(24));  // bits per pixel
            Add(payload, U16(1));   // planes
            Add(payload, new byte[24]);

            var resources = Resources(Res(1033, "", payload.ToArray()));

            Assert.IsNotNull(resources.Thumbnail);
            Assert.AreEqual(2, resources.Thumbnail.Width);
            Assert.AreEqual(2, resources.Thumbnail.Height);
            Assert.AreEqual(24, resources.Thumbnail.BitsPerPixel);
            Assert.AreEqual(PsdThumbnailFormat.RawRgb, resources.Thumbnail.Format);
        }

        [TestMethod]
        public void Absent_or_damaged_resources_return_null_without_throwing()
        {
            var resources = Resources(Res(1005, "", new byte[] { 1, 2 }), Res(1050, "", new byte[] { 9, 9, 9, 9 }));

            Assert.IsNull(resources.Resolution);
            Assert.IsNull(resources.GridAndGuides);
            Assert.IsNull(resources.Thumbnail);
            Assert.IsNull(resources.Slices);
            Assert.IsNull(resources.LayerComps);
            Assert.IsNull(resources.PrintScale);
            Assert.IsNull(resources.VersionInfo);
            Assert.IsNull(resources.IccProfile);
            Assert.IsNull(resources.Xmp);
            Assert.AreEqual(0, resources.Iptc.Count);
        }

        [TestMethod]
        public void Layer_comps_parse_from_the_descriptor()
        {
            var writer = new DescriptorWriter();
            writer.Writer.U32(16); // layer comps carry only the descriptor version (no lfx2 header)
            writer.Descriptor("Dcmn",
                DescriptorWriter.Item("list", new object[]
                {
                    DescriptorWriter.Obj("cmp ",
                        DescriptorWriter.Item("compID", 77),
                        DescriptorWriter.Item("Nm  ", "Comp A"),
                        DescriptorWriter.Item("capturedInfo", 3)),
                }),
                DescriptorWriter.Item("lastAppliedComp", 77));

            var resources = Resources(Res(1065, "", writer.ToArray()));

            Assert.IsNotNull(resources.LayerComps);
            Assert.AreEqual(1, resources.LayerComps.Comps.Count);
            var comp = resources.LayerComps.Comps[0];
            Assert.AreEqual(77, comp.Id);
            Assert.AreEqual("Comp A", comp.Name);
            Assert.IsTrue(comp.CapturesVisibility);
            Assert.IsTrue(comp.CapturesPosition);
            Assert.IsFalse(comp.CapturesAppearance);
            Assert.AreEqual(77, resources.LayerComps.LastAppliedCompId.Value);
        }

        // ---- helpers ----

        private static PsdDocument Document(params PsdBuilder.ResourceEntry[] entries)
        {
            var builder = new PsdBuilder { Width = 2, Height = 2, Mode = PsdColorMode.Rgb };
            builder.ColorModeData = new byte[0];
            foreach (var entry in entries)
            {
                builder.Resources.Add(entry);
            }

            return PsdDocument.FromBytes(builder.Build());
        }

        private static PsdImageResources Resources(params PsdBuilder.ResourceEntry[] entries)
        {
            return Document(entries).Resources;
        }

        private static PsdBuilder.ResourceEntry Res(ushort id, string name, byte[] data)
        {
            return new PsdBuilder.ResourceEntry(id, name, data);
        }

        private static byte[] ResolutionPayload(double ppi)
        {
            var payload = new List<byte>();
            Add(payload, U32((uint)(ppi * 65536.0)));
            Add(payload, U16(1)); // pixels per inch
            Add(payload, U16(5));
            Add(payload, U32((uint)(ppi * 65536.0)));
            Add(payload, U16(1));
            Add(payload, U16(5));
            return payload.ToArray();
        }

        private static byte[] VersionInfoPayload(string writer, string reader)
        {
            var payload = new List<byte>();
            Add(payload, I32(1));
            Add(payload, new byte[] { 1 });
            Add(payload, UnicodeString(writer));
            Add(payload, UnicodeString(reader));
            Add(payload, I32(1));
            return payload.ToArray();
        }

        private static byte[] AspectPayload(double ratio)
        {
            var payload = new List<byte>();
            Add(payload, U32(1));
            var big = BitConverter.GetBytes(ratio);
            Array.Reverse(big);
            Add(payload, big);
            return payload.ToArray();
        }

        private static byte[] UnicodeString(string value)
        {
            var payload = new List<byte>();
            Add(payload, U32((uint)value.Length));
            Add(payload, Encoding.BigEndianUnicode.GetBytes(value));
            return payload.ToArray();
        }

        private static void Add(List<byte> list, byte[] bytes)
        {
            list.AddRange(bytes);
        }

        private static byte[] U32(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static byte[] U16(ushort value)
        {
            return new[] { (byte)(value >> 8), (byte)value };
        }

        private static byte[] I32(int value)
        {
            return U32(unchecked((uint)value));
        }

        private static byte[] F32(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return bytes;
        }
    }
}
