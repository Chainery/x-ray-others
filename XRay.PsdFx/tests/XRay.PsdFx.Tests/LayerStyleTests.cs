using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;
using static XRay.PsdFx.Tests.Support.DescriptorWriter;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Port of the original LayerStyleTests. The corpus-backed cases (photoshop-lmfx-*,
    /// photoshop-global-light-*) are reproduced with synthetic documents built by
    /// <see cref="PsdBuilder"/>, so they run without test_documents; the corpus
    /// variants stay in the fixture-driven test files.
    /// </summary>
    [TestClass]
    public sealed class LayerStyleTests
    {
        [TestMethod]
        public void Master_switch_off_leaves_effects_listed_but_inactive()
        {
            var builder = new PsdBuilder();
            var layer = new BuilderLayer { Name = "styled", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 255);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
            layer.AddBlock("lfx2", ColorOverlayEffects(masterSwitch: false));
            builder.Layers.Add(layer);

            var style = PsdDocument.FromBytes(builder.Build()).Layers.Single().Style;

            Assert.IsNotNull(style);
            Assert.IsFalse(style.Visible);
            Assert.AreEqual(1, style.Effects.Count);
            var overlay = style.Effects[0];
            Assert.AreEqual(PsdEffectKind.ColorOverlay, overlay.Kind);
            Assert.IsTrue(overlay.Enabled);
            Assert.AreEqual(new PsdColor(0, 128, 255), overlay.Color);
            Assert.AreEqual(0, style.ActiveEffects.Count());
        }

        [TestMethod]
        public void Multi_instance_effects_project_in_file_order_with_their_switches()
        {
            var builder = new PsdBuilder();
            var layer = new BuilderLayer { Name = "Comic Pow", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 200);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 40);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 40);
            layer.AddBlock("lfx2", EffectsBlock(
                Item("masterFXSwitch", true),
                Item("DrSh", Obj("DrSh",
                    Item("enab", true),
                    Item("Md  ", Enum("BlnM", "mul ")),
                    Item("Clr ", Color(0, 0, 0)),
                    Item("Opct", Unit("#Prc", 90.0)),
                    Item("lagl", Unit("#Ang", 90.0)),
                    Item("Dstn", Unit("#Pxl", 7.0)),
                    Item("Ckmt", Unit("#Pxl", 0.0)),
                    Item("blur", Unit("#Pxl", 0.0)),
                    Item("Nose", Unit("#Prc", 0.0)),
                    Item("AntA", false),
                    Item("uglg", false),
                    Item("layerConceals", true))),
                Item("SoFi", Obj("SoFi",
                    Item("enab", true),
                    Item("Md  ", Enum("BlnM", "Nrml")),
                    Item("Clr ", Color(226, 6, 44)),
                    Item("Opct", Unit("#Prc", 100.0)))),
                Item("FrFX", Obj("FrFX",
                    Item("enab", true),
                    Item("Styl", Enum("FStl", "InsF")),
                    Item("PntT", Enum("FrFl", "SClr")),
                    Item("Md  ", Enum("BlnM", "Nrml")),
                    Item("Opct", Unit("#Prc", 100.0)),
                    Item("Sz  ", Unit("#Pxl", 3.0)),
                    Item("Clr ", Color(0xE2, 0x06, 0x2C)),
                    Item("overprint", false))),
                Item("FrFX", Obj("FrFX",
                    Item("enab", true),
                    Item("Styl", Enum("FStl", "OutF")),
                    Item("PntT", Enum("FrFl", "SClr")),
                    Item("Md  ", Enum("BlnM", "Nrml")),
                    Item("Opct", Unit("#Prc", 100.0)),
                    Item("Sz  ", Unit("#Pxl", 6.0)),
                    Item("Clr ", Color(255, 255, 255)),
                    Item("overprint", false))),
                Item("IrSh", Obj("IrSh",
                    Item("enab", false),
                    Item("Md  ", Enum("BlnM", "mul ")),
                    Item("Clr ", Color(0, 0, 0)),
                    Item("Opct", Unit("#Prc", 75.0)),
                    Item("lagl", Unit("#Ang", 120.0)),
                    Item("Dstn", Unit("#Pxl", 5.0)),
                    Item("Ckmt", Unit("#Pxl", 0.0)),
                    Item("blur", Unit("#Pxl", 0.0)),
                    Item("uglg", false)))));
            builder.Layers.Add(layer);

            var style = PsdDocument.FromBytes(builder.Build()).Layers.Single().Style;
            Assert.IsNotNull(style);
            Assert.IsTrue(style.Visible);
            Assert.AreEqual(5, style.Effects.Count);
            CollectionAssert.AreEqual(
                new[] { PsdEffectKind.DropShadow, PsdEffectKind.ColorOverlay, PsdEffectKind.Stroke, PsdEffectKind.Stroke, PsdEffectKind.InnerShadow },
                style.Effects.Select(e => e.Kind).ToArray());

            var shadow = style.Effects[0];
            Assert.AreEqual(true, shadow.Enabled);
            Assert.AreEqual(PsdBlendMode.Multiply, shadow.BlendMode);
            Assert.AreEqual(PsdColor.Black, shadow.Color);
            Assert.AreEqual(0.9f, shadow.Opacity, 0.001);
            Assert.AreEqual(0f, shadow.Size);
            Assert.AreEqual(7f, shadow.Distance);
            Assert.AreEqual(90f, shadow.Angle);

            var strokes = style.Effects.Where(e => e.Kind == PsdEffectKind.Stroke).ToList();
            CollectionAssert.AreEqual(new[] { PsdStrokePosition.Inside, PsdStrokePosition.Outside }, strokes.Select(s => s.StrokePosition).ToArray());
            CollectionAssert.AreEqual(new[] { 3f, 6f }, strokes.Select(s => s.Size.Value).ToArray());
            CollectionAssert.AreEqual(new[] { new PsdColor(0xE2, 0x06, 0x2C), PsdColor.White }, strokes.Select(s => s.Color.Value).ToArray());

            // Disabled effects are listed with the defaults the renderer would use.
            var innerShadow = style.Effects.Single(e => e.Kind == PsdEffectKind.InnerShadow);
            Assert.IsFalse(innerShadow.Enabled);
            Assert.AreEqual(PsdBlendMode.Multiply, innerShadow.BlendMode);
            Assert.IsNotNull(innerShadow.Distance);
            Assert.AreEqual(4, style.ActiveEffects.Count());
        }

        [TestMethod]
        public void Global_light_resolves_into_the_angle()
        {
            var builder = new PsdBuilder();
            builder.Resources.Add(new PsdBuilder.ResourceEntry(ImageResourceIds.GlobalAngle, string.Empty, BigEndian(60)));
            var layer = new BuilderLayer { Name = "styled", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 10);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 10);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 10);
            layer.AddBlock("lfx2", EffectsBlock(
                Item("masterFXSwitch", true),
                Item("DrSh", Obj("DrSh",
                    Item("enab", true),
                    Item("Md  ", Enum("BlnM", "mul ")),
                    Item("Clr ", Color(0, 0, 0)),
                    Item("Opct", Unit("#Prc", 75.0)),
                    Item("lagl", Unit("#Ang", 90.0)),
                    Item("Dstn", Unit("#Pxl", 10.0)),
                    Item("Ckmt", Unit("#Pxl", 0.0)),
                    Item("blur", Unit("#Pxl", 4.0)),
                    Item("uglg", true)))));
            builder.Layers.Add(layer);

            var document = PsdDocument.FromBytes(builder.Build());
            Assert.AreEqual(60, document.GlobalLightAngle);

            var shadow = document.Layers.Single().Style.ActiveEffects.Single();
            Assert.AreEqual(PsdEffectKind.DropShadow, shadow.Kind);
            Assert.IsTrue(shadow.Descriptor.GetBoolean("uglg"));
            Assert.AreEqual(document.GlobalLightAngle, shadow.Angle);
            Assert.AreEqual(4f, shadow.Size);
            Assert.AreEqual(10f, shadow.Distance);
        }

        [TestMethod]
        public void Layers_without_effects_have_no_style()
        {
            var builder = new PsdBuilder();
            var layer = new BuilderLayer { Name = "plain", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 128);
            builder.Layers.Add(layer);

            var document = PsdDocument.FromBytes(builder.Build());
            Assert.IsNull(document.Layers.Single().Style);
            Assert.AreEqual(0, document.AllEffects.Count());
        }

        [TestMethod]
        public void Bevel_and_overlay_details_are_exposed()
        {
            var builder = new PsdBuilder();
            var layer = new BuilderLayer { Name = "bevelled", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 80);
            layer.AddBlock("lfx2", EffectsBlock(
                Item("masterFXSwitch", true),
                Item("ebbl", Obj("ebbl",
                    Item("enab", true),
                    Item("hglM", Enum("BlnM", "scrn")),
                    Item("hglC", Color(255, 255, 255)),
                    Item("hglO", Unit("#Prc", 75.0)),
                    Item("sdwM", Enum("BlnM", "mul ")),
                    Item("sdwC", Color(0, 0, 0)),
                    Item("sdwO", Unit("#Prc", 75.0)),
                    Item("bvlT", Enum("bvlT", "SfBL")),
                    Item("bvlS", Enum("BESl", "OtrB")),
                    Item("uglg", false),
                    Item("lagl", Unit("#Ang", 120.0)),
                    Item("Lald", Unit("#Ang", 30.0)),
                    Item("srgR", Unit("#Prc", 100.0)),
                    Item("blur", Unit("#Pxl", 5.0))))));
            builder.Layers.Add(layer);

            var bevel = PsdDocument.FromBytes(builder.Build()).Layers.Single().Style.ActiveEffects.Single();

            Assert.AreEqual(PsdEffectKind.BevelEmboss, bevel.Kind);
            Assert.AreEqual(PsdBevelStyle.OuterBevel, bevel.BevelStyle);
            Assert.IsNotNull(bevel.Altitude);
            Assert.IsNotNull(bevel.Depth);
            Assert.AreEqual(PsdBlendMode.Screen, bevel.BlendMode);
            Assert.AreEqual(PsdColor.White, bevel.Color);
            Assert.AreEqual(PsdBlendMode.Multiply, bevel.ShadowBlendMode);
            Assert.AreEqual(PsdColor.Black, bevel.ShadowColor);
        }

        private static byte[] BigEndian(int value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static object Color(byte r, byte g, byte b)
        {
            return Obj("RGBC", Item("Rd  ", (double)r), Item("Grn ", (double)g), Item("Bl  ", (double)b));
        }

        private static byte[] EffectsBlock(params KeyValuePair<string, object>[] items)
        {
            return new DescriptorWriter().Header().Descriptor("null", items).ToArray();
        }

        private static byte[] ColorOverlayEffects(bool masterSwitch)
        {
            var writer = new PsdBuilder.Writer();
            writer.U32(0);
            writer.U32(16);
            writer.UnicodeString(string.Empty);
            writer.DescriptorId("null");
            writer.U32(2);
            writer.DescriptorId("masterFXSwitch");
            writer.Ascii("bool");
            writer.U8(masterSwitch ? (byte)1 : (byte)0);
            writer.DescriptorId("SoFi");
            writer.Ascii("Objc");
            writer.UnicodeString(string.Empty);
            writer.DescriptorId("SoFi");
            writer.U32(2);
            writer.DescriptorId("enab");
            writer.Ascii("bool");
            writer.U8(1);
            writer.DescriptorId("Clr ");
            writer.Ascii("Objc");
            writer.UnicodeString(string.Empty);
            writer.DescriptorId("RGBC");
            writer.U32(3);
            foreach (var pair in new[] { new KeyValuePair<string, double>("Rd  ", 0.0), new KeyValuePair<string, double>("Grn ", 128.0), new KeyValuePair<string, double>("Bl  ", 255.0) })
            {
                writer.DescriptorId(pair.Key);
                writer.Ascii("doub");
                writer.F64(pair.Value);
            }

            return writer.ToArray();
        }
    }
}
