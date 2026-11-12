using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;
using static XRay.PsdFx.Tests.Support.DescriptorWriter;

namespace XRay.PsdFx.Tests
{
    /// <summary>Phase 6 port: EngineData, TySh records, Txt2, layer text and extraction.</summary>
    [TestClass]
    public sealed class TextTests
    {
        [TestMethod]
        public void Engine_data_parses_dictionary_list_and_scalars()
        {
            var payload = Encoding.ASCII.GetBytes("<< /FontSize 12.5 /Flag true /Name /foo /List [ 1 (text) (>> ) ] >>");

            var root = EngineDataParser.Parse(payload);

            Assert.IsNotNull(root);
            Assert.AreEqual(12.5, root.Get("FontSize").Number);
            Assert.AreEqual(true, root.Get("Flag").Boolean);
            Assert.AreEqual("foo", root.Get("Name").Text);
            var list = root.Get("List");
            Assert.AreEqual(3, list.Items.Count);
            Assert.AreEqual(EngineValueKind.Number, list[0].Kind);
            Assert.AreEqual("text", list[1].Text);
            Assert.AreEqual(">> ", list[2].Text); // escaped close paren stays inside the string
        }

        [TestMethod]
        public void Engine_data_decodes_utf16_strings()
        {
            var body = new List<byte> { 0xFE, 0xFF, 0x4F, 0x60 }; // BOM + U+4F60
            var payload = new List<byte>(Encoding.ASCII.GetBytes("<< /Text ("));
            payload.AddRange(body);
            payload.AddRange(Encoding.ASCII.GetBytes(") >>"));

            var root = EngineDataParser.Parse(payload.ToArray());

            Assert.AreEqual("\u4F60", root.Get("Text").Text);
        }

        [TestMethod]
        public void Engine_data_bare_dictionary_reads_txt2_style_pairs()
        {
            var payload = Encoding.ASCII.GetBytes("/0 << /0 (hello\r) /1 2 >> /98 << /0 14 >>");

            var root = EngineDataParser.Parse(payload);

            Assert.AreEqual(EngineValueKind.Dictionary, root.Kind);
            Assert.AreEqual("hello", TextLayerInfo.NormalizeText(root.Get("0", "0").Text));
            Assert.AreEqual(14, root.Get("98", "0").Number);
        }

        // ---- TySh ----

        private static byte[] EngineForHello()
        {
            // One style run (24 px, half-blue fill) and one centered paragraph over "Hello\r".
            return Encoding.ASCII.GetBytes(
                "<< /EngineDict << /Editor << /Text (Hello\r) >> /AntiAlias 3 "
                + "/StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /Font 0 /FontSize 24 "
                + "/FillColor << /Type 1 /Values [ 1.0 0.0 0.0 0.5 ] >> >> >> >> ] "
                + "/RunLengthArray [ 6 ] >> "
                + "/ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification 2 >> >> >> ] "
                + "/RunLengthArray [ 6 ] >> >> "
                + "/ResourceDict << /FontSet [ << /Name (Arial-MT) >> ] "
                + "/TheNormalStyleSheet 0 /TheNormalParagraphSheet 0 >> >>");
        }

        private static byte[] TyShPayload()
        {
            var writer = new DescriptorWriter();
            writer.Writer.U16(1); // version
            foreach (var value in new[] { 1.0, 0.0, 0.0, 1.0, 10.0, 20.0 })
            {
                writer.Writer.F64(value); // transform
            }

            writer.Writer.U16(50); // text version
            writer.Writer.U32(16); // descriptor version
            writer.Descriptor("TxLr",
                Item("Txt ", "Hello\r"),
                Item("TextIndex", 0),
                Item("EngineData", new RawBytes(EngineForHello())));
            writer.Writer.U16(1); // warp version
            writer.Writer.U32(16); // descriptor version
            writer.Descriptor("warp",
                Item("warpStyle", Enum("warpStyle", "warpArc")),
                Item("warpValue", 50.0));
            return writer.ToArray();
        }

        [TestMethod]
        public void TySh_payload_parses_text_runs_and_warp()
        {
            var info = TextLayerInfo.Parse(TyShPayload());

            Assert.IsNotNull(info);
            Assert.AreEqual("Hello", info.Text);
            Assert.AreEqual("Hello\r", info.RawText);
            Assert.AreEqual(TextOrientation.Horizontal, info.Orientation);
            Assert.AreEqual(TextShapeKind.Point, info.ShapeKind);
            Assert.AreEqual(TextAntiAlias.Smooth, info.AntiAlias);
            Assert.AreEqual(0, info.TextIndex);
            Assert.AreEqual(1, info.Fonts.Count);
            Assert.AreEqual("Arial-MT", info.Fonts[0]);
            Assert.AreEqual(1, info.StyleRuns.Count);
            var run = info.StyleRuns[0];
            Assert.AreEqual(24.0, run.FontSize);
            Assert.AreEqual("Arial-MT", run.FontName);
            Assert.AreEqual(new PsdColor(0, 0, 128, 255), run.FillColor);
            Assert.AreEqual(1, info.Paragraphs.Count);
            Assert.AreEqual(TextJustification.Center, info.Paragraphs[0].Justification);
            Assert.AreEqual(TextDataOrigin.TypeTool | TextDataOrigin.EngineData, info.Origin);
            Assert.IsNotNull(info.WarpSettings);
            Assert.AreEqual(TextWarpStyle.Arc, info.WarpSettings.Style);
            Assert.AreEqual(50.0, info.WarpSettings.Value);
            Assert.IsFalse(info.WarpSettings.IsIdentity);
        }

        [TestMethod]
        public void Layer_TySh_block_becomes_a_text_layer()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var layer = new BuilderLayer { Name = "type", Rect = new PsdRect(0, 0, 2, 2) };
            layer.AddBlock("TySh", TyShPayload());
            builder.Layers.Add(layer);

            var parsed = PsdDocument.FromBytes(builder.Build()).Layers.Single();

            Assert.AreEqual(PsdLayerKind.Text, parsed.Kind);
            Assert.AreEqual("Hello", parsed.Text.Text);
        }

        // ---- Txt2 ----

        /// <summary>
        /// A minimal Txt2 block: one font, one style sheet (/0/5/0/0/0/6), one paragraph
        /// sheet (/0/6/0/0/0/5), one frame (point, horizontal) and one object with the
        /// text "Hi\r", a style run (size 11, unit flag 1 = points) and a paragraph run.
        /// </summary>
        private static byte[] Txt2Payload()
        {
            return Encoding.ASCII.GetBytes(
                "/0 << "
                + "/1 << /0 [ << /0 << /0 << /0 (Arial-MT) /2 1 >> >> >> ] >> "
                + "/5 << /0 [ << /0 << /6 << /1 11 >> >> >> ] >> "
                + "/6 << /0 [ << /0 << /5 << /0 2 >> >> >> ] >> "
                + "/8 << /0 [ << /0 << /2 << /0 0 /1 0 >> /1 [ 0 0 100 0 100 50 0 50 ] >> >> ] >> "
                + ">> "
                + "/1 << "
                + "/1 [ << /0 << "
                + "/0 (Hi\r) "
                + "/5 << /0 [ << /0 << /0 << /5 << /0 2 >> /6 0 >> >> /1 3 >> ] >> "
                + "/6 << /0 [ << /0 << /0 << /6 << /0 0 /1 11 /53 << /99 /SimplePaint /0 << /0 1 /1 [ 1.0 1.0 0.0 0.0 ] >> >> >> /5 1 >> >> /1 3 >> ] >> "
                + "/10 << /0 0 >> >> "
                + "/1 << /0 [ << /0 0 >> ] >> >> "
                + "] >> "
                + "/98 << /0 14 >>");
        }

        [TestMethod]
        public void Txt2_block_parses_object_with_styles()
        {
            var block = TextEngineBlock.Parse(Txt2Payload(), 1.5);

            Assert.IsNotNull(block);
            Assert.AreEqual(14, block.FormatVersion);
            Assert.AreEqual(1, block.Fonts.Count);
            Assert.AreEqual("Arial-MT", block.Fonts[0].PostScriptName);
            Assert.AreEqual(1, block.Frames.Count);
            Assert.AreEqual(TextShapeKind.Point, block.Frames[0].ShapeKind);
            Assert.AreEqual(1, block.Objects.Count);
            var textObject = block.Objects[0];
            Assert.AreEqual("Hi", textObject.Text);
            Assert.AreEqual(0, textObject.Index);
            Assert.AreEqual(1, textObject.StyleRuns.Count);
            var run = textObject.StyleRuns[0];
            Assert.AreEqual("Arial-MT", run.FontName);
            // Unit flag 1: 11 points x 1.5 px/pt = 16.5 document pixels.
            Assert.AreEqual(16.5, run.FontSize.Value, 0.001);
            // Txt2 color: type 1, [alpha 1, r 1, g 0, b 0] -> red.
            Assert.AreEqual(new PsdColor(255, 0, 0, 255), run.FillColor);
            Assert.AreEqual(1, textObject.Paragraphs.Count);
            Assert.AreEqual(TextJustification.Center, textObject.Paragraphs[0].Justification);
        }

        [TestMethod]
        public void Document_Txt2_fills_layer_text_gaps()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var layer = new BuilderLayer { Name = "type", Rect = new PsdRect(0, 0, 2, 2) };
            // A TySh whose descriptor text is empty but whose TextIndex points at the Txt2 object.
            var writer = new DescriptorWriter();
            writer.Writer.U16(1);
            foreach (var value in new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0 })
            {
                writer.Writer.F64(value);
            }

            writer.Writer.U16(50);
            writer.Writer.U32(16); // descriptor version
            writer.Descriptor("TxLr", Item("Txt ", ""), Item("TextIndex", 0));
            layer.AddBlock("TySh", writer.ToArray());
            builder.Layers.Add(layer);
            builder.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Txt2", Txt2Payload()));

            var parsed = PsdDocument.FromBytes(builder.Build()).Layers.Single();

            Assert.AreEqual(PsdLayerKind.Text, parsed.Kind);
            Assert.IsNotNull(parsed.Text);
            Assert.AreEqual("Hi", parsed.Text.Text);
            Assert.IsNotNull(parsed.Text.TextEngineObject);
            Assert.AreEqual(TextDataOrigin.TypeTool | TextDataOrigin.TextEngineBlock, parsed.Text.Origin);
            Assert.AreEqual(1, parsed.Text.StyleRuns.Count);
        }

        // ---- Extraction ----

        [TestMethod]
        public void Extract_text_reports_layers_txt2_and_slicing()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var type = new BuilderLayer { Name = "type layer", Rect = new PsdRect(0, 0, 2, 2) };
            type.AddBlock("TySh", TyShPayload());
            builder.Layers.Add(type);
            var pixel = NewPixelLayer();
            pixel.Name = "背景";
            builder.Layers.Add(pixel);
            builder.GlobalBlocks.Add(new KeyValuePair<string, byte[]>("Txt2", Txt2Payload()));
            var document = PsdDocument.FromBytes(builder.Build());

            var text = document.ExtractText();

            var kinds = text.Items.Select(delegate(PsdTextItem i) { return i.Kind; }).ToList();
            CollectionAssert.Contains(kinds, PsdTextKind.LayerName);
            CollectionAssert.Contains(kinds, PsdTextKind.TextLayer);
            var layerText = text.OfKind(PsdTextKind.TextLayer).Single();
            Assert.AreEqual("Hello", layerText.Text);
            Assert.AreEqual("type layer", layerText.Source);
            // Top-down panel order: the pixel layer (named 背景) comes first.
            Assert.AreEqual("背景", text.Items.First(delegate(PsdTextItem i) { return i.Kind == PsdTextKind.LayerName; }).Source);
            // The Txt2 object says "Hi" while its layer reports "Hello": reported once more.
            var engineItem = text.OfKind(PsdTextKind.TextEngineObject).Single();
            Assert.AreEqual("Hi", engineItem.Text);
            Assert.IsNotNull(engineItem.TextInfo);
            // Document-level Txt2 objects report the frame depth (0), like the original.
            Assert.AreEqual(0, engineItem.Depth);
        }

        [TestMethod]
        public void Extract_text_honors_options()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2 };
            var type = new BuilderLayer { Name = "type layer", Rect = new PsdRect(0, 0, 2, 2) };
            type.AddBlock("TySh", TyShPayload());
            builder.Layers.Add(type);
            var document = PsdDocument.FromBytes(builder.Build());

            var text = document.ExtractText(new TextExtractionOptions
            {
                IncludeLayerNames = false,
                IncludeChannelNames = false,
                IncludeTextEngineObjects = false,
            });

            Assert.AreEqual(1, text.Items.Count);
            Assert.AreEqual(PsdTextKind.TextLayer, text.Items[0].Kind);
        }

        private static BuilderLayer NewPixelLayer()
        {
            var layer = new BuilderLayer { Name = "layer", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 255);
            layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
            layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
            return layer;
        }
    }
}
