using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;
using static XRay.PsdFx.Tests.Support.DescriptorWriter;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Phase 4 port: mask records, group trees (lsct), fill/adjustment recognition,
    /// vector masks and the shmd metadata view.
    /// </summary>
    [TestClass]
    public sealed class LayerTests
    {
        [TestMethod]
        public void Mask_record_parses_with_density_and_feather()
        {
            var builder = new PsdBuilder();
            var layer = NewPixelLayer("masked");
            layer.MaskRect = new PsdRect(1, 2, 4, 6);
            layer.MaskDefault = 200;
            layer.MaskFlags = 0x10; // mask parameters follow
            layer.MaskTail = new[] { (byte)0x03, (byte)120 }; // density + feather flags present, density 120
            builder.Layers.Add(layer);

            var mask = PsdDocument.FromBytes(builder.Build()).Layers.Single().Mask;

            Assert.IsNotNull(mask);
            Assert.AreEqual(new PsdRect(1, 2, 4, 6), mask.Bounds);
            Assert.AreEqual(200, mask.DefaultColor);
            Assert.AreEqual(-2, mask.ChannelId);
            Assert.AreEqual(120, mask.Density);
            Assert.IsFalse(mask.FromRendering);
        }

        [TestMethod]
        public void Rendered_vector_mask_flag_routes_to_RenderedVectorMask()
        {
            var builder = new PsdBuilder();
            var layer = NewPixelLayer("shape");
            layer.MaskRect = new PsdRect(0, 0, 3, 3);
            layer.MaskFlags = 0x08; // from rendering (baked vector coverage)
            builder.Layers.Add(layer);

            var parsed = PsdDocument.FromBytes(builder.Build()).Layers.Single();

            Assert.IsNull(parsed.Mask);
            Assert.IsNotNull(parsed.RenderedVectorMask);
            Assert.IsTrue(parsed.RenderedVectorMask.FromRendering);
        }

        [TestMethod]
        public void Group_layers_form_a_tree_with_lsct()
        {
            var builder = new PsdBuilder();
            var divider = new BuilderLayer { Name = "/", SectionType = 3 };            // bounding divider
            var child = NewPixelLayer("inside");
            var folder = new BuilderLayer { Name = "group", SectionType = 1, SectionBlendKey = "mul " };
            builder.Layers.Add(divider);
            builder.Layers.Add(child);
            builder.Layers.Add(folder);

            var document = PsdDocument.FromBytes(builder.Build());

            Assert.AreEqual(1, document.RootLayers.Count);
            var group = document.RootLayers[0];
            Assert.AreEqual(PsdLayerKind.Group, group.Kind);
            Assert.AreEqual(PsdBlendMode.Multiply, group.BlendMode);
            Assert.AreEqual(1, group.Children.Count);
            Assert.AreSame(group, group.Children[0].Parent);
            Assert.AreEqual("group/inside", group.Children[0].Path);
            Assert.IsTrue(group.IsEffectivelyVisible);
            Assert.AreEqual(3, document.Layers.Count); // flat list keeps file order
        }

        [TestMethod]
        public void Fill_and_adjustment_layers_are_recognized()
        {
            var builder = new PsdBuilder();
            var fill = NewPixelLayer("fill");
            var soco = new DescriptorWriter();
            soco.Writer.U32(16);
            soco.Descriptor("solidColorLayer",
                Item("Clr ", Obj("RGBC ", Item("Rd  ", 255), Item("Grn ", 0), Item("Bl  ", 0))));
            fill.AddBlock("SoCo", soco.ToArray());
            var adjustment = NewPixelLayer("curves");
            adjustment.AddBlock("curv", new byte[] { 0, 0, 0, 1 });
            builder.Layers.Add(fill);
            builder.Layers.Add(adjustment);

            var layers = PsdDocument.FromBytes(builder.Build()).Layers;

            Assert.AreEqual(PsdLayerKind.Fill, layers[0].Kind);
            Assert.AreEqual("SoCo", layers[0].ContentKey);
            Assert.IsNotNull(layers[0].FillDescriptor);
            Assert.AreEqual(new PsdColor(255, 0, 0), layers[0].FillColor);
            Assert.AreEqual(PsdLayerKind.Adjustment, layers[1].Kind);
            Assert.AreEqual("curv", layers[1].ContentKey);
        }

        [TestMethod]
        public void Vector_mask_parses_to_subpaths_and_bounds()
        {
            var builder = new PsdBuilder();
            var layer = NewPixelLayer("shape");
            layer.AddBlock("vmsk", VectorMaskPayload(4, 4));
            builder.Layers.Add(layer);

            var path = PsdDocument.FromBytes(builder.Build()).Layers.Single().VectorMask;

            Assert.IsNotNull(path);
            Assert.AreEqual(1, path.Subpaths.Count);
            Assert.IsTrue(path.Subpaths[0].Closed);
            Assert.AreEqual(PathCombineOperation.Add, path.Subpaths[0].Operation);
            Assert.AreEqual(1, path.Subpaths[0].Knots.Count);
            Assert.AreEqual(1.0, path.Subpaths[0].Knots[0].X, 0.001);
            Assert.AreEqual(1.0, path.Subpaths[0].Knots[0].Y, 0.001);
            Assert.AreEqual(new PsdRect(1, 1, 1, 1), path.Bounds);
        }

        [TestMethod]
        public void Shmd_metadata_parses()
        {
            var builder = new PsdBuilder();
            var layer = NewPixelLayer("timed");
            var payload = new List<byte>();
            payload.AddRange(ToBigEndian(1)); // item count
            payload.AddRange(new[] { (byte)'8', (byte)'B', (byte)'I', (byte)'M' });
            payload.AddRange(new[] { (byte)'c', (byte)'u', (byte)'s', (byte)'t' });
            payload.Add(1); // copy on sheet duplication
            payload.AddRange(new byte[3]);
            var writer = new DescriptorWriter();
            writer.Writer.U32(16);
            writer.Descriptor("cust", Item("layerTime", 1234567890.0));
            var descriptor = writer.ToArray();
            payload.AddRange(ToBigEndian((uint)descriptor.Length));
            payload.AddRange(descriptor);
            layer.AddBlock("shmd", payload.ToArray());
            builder.Layers.Add(layer);

            var metadata = PsdDocument.FromBytes(builder.Build()).Layers.Single().Metadata;

            Assert.IsNotNull(metadata);
            Assert.AreEqual(1, metadata.Items.Count);
            Assert.AreEqual("cust", metadata.Items[0].Key);
            Assert.IsTrue(metadata.Items[0].CopyOnSheetDuplication);
            Assert.IsNotNull(metadata.LayerTime);
            Assert.AreEqual(1234567890.0, (metadata.LayerTime.Value - new DateTime(1970, 1, 1)).TotalSeconds, 1.0);
        }

        // ---- helpers ----

        private static IEnumerable<byte> ToBigEndian(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static BuilderLayer NewPixelLayer(string name)
        {
            var layer = new BuilderLayer { Name = name, Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 255);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
            return layer;
        }

        // vmsk payload: version u32, flags u32, then 26-byte path records. One closed
        // subpath expecting one knot, then one smooth knot anchored at (1, 1) in a
        // 4x4 document (fixed point 1.0 = 16777216).
        private static byte[] VectorMaskPayload(int width, int height)
        {
            var payload = new List<byte>();
            payload.AddRange(new byte[] { 0, 0, 0, 1 }); // version
            payload.AddRange(new byte[] { 0, 0, 0, 0 }); // flags
            // Pixel 1 of a 4 px extent = 0.25 = 4194304 in 16.16 fixed point.
            var quarter = new byte[] { 0x00, 0x40, 0x00, 0x00 };
            var one = quarter;

            var subpath = new byte[26];
            subpath[0] = 0; subpath[1] = 0;                       // selector 0: closed subpath
            subpath[2] = 0; subpath[3] = 1;                       // one knot expected
            subpath[4] = 0; subpath[5] = 1;                       // operation 1: Add
            payload.AddRange(subpath);

            var knot = new byte[26];
            knot[0] = 0; knot[1] = 1;                             // selector 1: smooth knot
            foreach (var coordinate in new[] { one, one, one, one, one, one })
            {
                // InY, InX, Y, X, OutY, OutX — all at (1, 1).
                Array.Copy(coordinate, 0, knot, 2, 4);
                Array.Copy(coordinate, 0, knot, 6, 4);
                Array.Copy(coordinate, 0, knot, 10, 4);
                Array.Copy(coordinate, 0, knot, 14, 4);
                Array.Copy(coordinate, 0, knot, 18, 4);
                Array.Copy(coordinate, 0, knot, 22, 4);
            }

            payload.AddRange(knot);
            return payload.ToArray();
        }
    }
}