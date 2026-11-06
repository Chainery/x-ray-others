using System;
using System.Collections.Generic;
using System.IO;
using XRay.PsdFx;

namespace XRay.PsdFx.Demo
{
    /// <summary>
    /// Round-trip check: writes a small PSD (one layer with a modern lfx2 style
    /// holding a noisy drop shadow, an inner shadow and an outer glow; one layer
    /// with a Photoshop 5 lrFX style), then reads it back with XRay.PsdFx and
    /// prints every effect - Noise (杂色) included.
    /// </summary>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.GetType().FullName + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            Console.ReadLine();
        }

        private static void Run(string[] args)
        {
            if (args.Length == 2 && args[0] == "--summary")
            {
                Summary(args[1]);
                return;
            }

            string path;
            if (args.Length > 0)
            {
                // An explicit path is always read-only: never overwrite a user file.
                path = args[0];
                Console.WriteLine("Reading " + path + " (" + new FileInfo(path).Length + " bytes)");
            }
            else
            {
                var psd = BuildSyntheticPsd();
                path = Path.Combine(Path.GetTempPath(), "xray-psdfx-demo.psd");
                File.WriteAllBytes(path, psd);
                Console.WriteLine("Wrote " + path + " (" + psd.Length + " bytes)");
            }

            var document = PsdDocument.FromFile(path);
            Console.WriteLine("Header: {0}x{1}, {2} channels, {3} bits, mode {4}, PSB={5}",
                document.Width, document.Height, document.Channels, document.BitsPerChannel, document.ColorMode, document.IsLargeDocument);
            Console.WriteLine("Global light: angle {0}, altitude {1}", document.GlobalLightAngle, document.GlobalLightAltitude);
            Console.WriteLine("Layers: " + document.Layers.Count);

            foreach (var layer in document.Layers)
            {
                Console.WriteLine();
                Console.WriteLine("Layer \"" + layer.Name + "\" visible=" + layer.IsVisible + " blend=" + layer.BlendMode + " blocks=[" + string.Join(",", layer.TaggedBlockKeys) + "]");
                if (layer.Style == null)
                {
                    Console.WriteLine("  no layer style");
                    continue;
                }

                Console.WriteLine("  style: visible=" + layer.Style.Visible + " legacy=" + layer.Style.IsLegacy);
                var index = 0;
                foreach (var effect in layer.Style.Effects)
                {
                    if (index > 0)
                    {
                        Console.WriteLine("  " + new string('-', 72));
                    }

                    Console.WriteLine("  effect #" + (index + 1));
                    Dump(effect, 4);
                    index++;
                }
            }

            // The raw tagged block stays reachable for byte-level comparisons.
            foreach (var layer in document.Layers)
            {
                var lfx2 = layer.GetTaggedBlock("lfx2");
                if (lfx2 == null)
                {
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine("Raw lfx2 block of \"" + layer.Name + "\": " + lfx2.Length + " bytes, first 16: " + ToHex(lfx2, 16));
            }
        }

        /// <summary>
        /// Canonical one-line-per-effect output shared with the XRay.Psd cross-check
        /// program, so the two libraries can be diffed line by line.
        /// </summary>
        private static void Summary(string path)
        {
            var document = PsdDocument.FromFile(path);
            foreach (var layer in document.Layers)
            {
                if (layer.Style == null)
                {
                    continue;
                }

                foreach (var effect in layer.Style.Effects)
                {
                    Console.WriteLine(string.Join("|", new[]
                    {
                        layer.Name,
                        effect.Kind.ToString(),
                        effect.Enabled ? "True" : "False",
                        effect.BlendMode.ToString(),
                        Num(effect.Opacity),
                        effect.Color.HasValue ? effect.Color.Value.ToString() : "null",
                        Num(effect.Size),
                        Num(effect.Distance),
                        Num(effect.Angle),
                        Num(effect.Spread),
                        effect.StrokePosition.HasValue ? effect.StrokePosition.Value.ToString() : "null",
                        effect.BevelStyle.HasValue ? effect.BevelStyle.Value.ToString() : "null",
                        effect.BevelTechnique.HasValue ? effect.BevelTechnique.Value.ToString() : "null",
                        Num(effect.Altitude),
                        Num(effect.Depth),
                        effect.ShadowBlendMode.HasValue ? effect.ShadowBlendMode.Value.ToString() : "null",
                        Num(effect.ShadowOpacity),
                        effect.ShadowColor.HasValue ? effect.ShadowColor.Value.ToString() : "null",
                    }));
                }
            }
        }

        private static string Num(float? value)
        {
            return value.HasValue ? value.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "null";
        }

        /// <summary>Prints every typed property of an effect (null = the property does not apply to this effect).</summary>
        private static void Dump(PsdEffect effect, int indent)
        {
            var prefix = new string(' ', indent);
            foreach (var property in typeof(PsdEffect).GetProperties())
            {
                object value;
                try
                {
                    value = property.GetValue(effect, null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(prefix + property.Name + " !! getter threw " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                if (value == null)
                {
                    continue;
                }

                string text;
                var type = value.GetType();
                if (type == typeof(bool))
                {
                    text = (bool)value ? "true" : "false";
                }
                else if (type == typeof(float))
                {
                    text = ((float)value).ToString("0.###");
                }
                else if (property.Name == "Descriptor")
                {
                    // Raw descriptor on its own, single line, at the end of the block.
                    text = value.ToString().Replace("\r\n", " ").Replace("\n", " ");
                    Console.WriteLine(prefix + "RawDescriptor = " + text);
                    continue;
                }
                else
                {
                    text = value.ToString();
                }

                Console.WriteLine(prefix + property.Name + " (" + type.Name + ") = " + text);
            }
        }

        private static string ToHex(byte[] data, int count)
        {
            var parts = new List<string>();
            for (var i = 0; i < count && i < data.Length; i++)
            {
                parts.Add(data[i].ToString("X2"));
            }

            return string.Join(" ", parts.ToArray());
        }

        // ---------- synthetic PSD builder ----------

        private static byte[] BuildSyntheticPsd()
        {
            var writer = new ByteWriter();
            writer.WriteAscii("8BPS");
            writer.WriteUInt16(1); // version: PSD
            writer.Skip(6); // reserved
            writer.WriteUInt16(3); // channels
            writer.WriteUInt32(8); // height
            writer.WriteUInt32(8); // width
            writer.WriteUInt16(8); // depth
            writer.WriteUInt16(3); // RGB
            writer.WriteUInt32(0); // color mode data
            writer.WriteUInt32(0); // image resources

            var layer1 = BuildLayer("ShadowLayer", BuildLfx2Effects(), null);
            var layer2 = BuildLayer("LegacyLayer", null, BuildLrFxEffects());
            var layer3 = BuildLayer("PatternStrokeLayer", BuildPatternStrokeEffects(), null);
            var records = new[] { layer1, layer2, layer3 };

            // Layer info: count + records + channel data.
            var info = new ByteWriter();
            info.WriteInt16((short)records.Length);
            foreach (var record in records)
            {
                info.WriteBytes(record);
            }

            foreach (var record in records)
            {
                // One raw channel of 8x8 pixels per layer (compression 0 + data).
                info.WriteUInt16(0);
                info.WriteBytes(new byte[64]);
            }

            var section = new ByteWriter();
            section.WriteUInt32((uint)info.Length);
            section.WriteBytes(info.ToBytes());
            section.WriteUInt32(0); // global layer mask info

            writer.WriteUInt32((uint)section.Length);
            writer.WriteBytes(section.ToBytes());
            return writer.ToBytes();
        }

        private static byte[] BuildLayer(string name, byte[] lfx2, byte[] lrFx)
        {
            var blocks = new ByteWriter();
            if (lfx2 != null)
            {
                blocks.WriteAscii("8BIM");
                blocks.WriteAscii("lfx2");
                blocks.WriteUInt32((uint)lfx2.Length);
                blocks.WriteBytes(lfx2);
            }

            if (lrFx != null)
            {
                blocks.WriteAscii("8BIM");
                blocks.WriteAscii("lrFX");
                blocks.WriteUInt32((uint)lrFx.Length);
                blocks.WriteBytes(lrFx);
            }

            var extra = new ByteWriter();
            extra.WriteUInt32(0); // mask data length
            extra.WriteUInt32(0); // blending ranges length
            extra.WritePascalString(name);
            extra.WriteBytes(blocks.ToBytes());

            var record = new ByteWriter();
            record.WriteInt32(0); // top
            record.WriteInt32(0); // left
            record.WriteInt32(8); // bottom
            record.WriteInt32(8); // right
            record.WriteUInt16(1); // channel count
            record.WriteInt16(0); // channel id (red)
            record.WriteUInt32(2 + 64); // channel data length
            record.WriteAscii("8BIM");
            record.WriteAscii("norm");
            record.WriteByte(255); // opacity
            record.WriteByte(0); // clipping
            record.WriteByte(0); // flags (visible)
            record.WriteByte(0); // filler
            record.WriteUInt32((uint)extra.Length);
            record.WriteBytes(extra.ToBytes());
            return record.ToBytes();
        }

        /// <summary>A layer whose only effect is a pattern-filled stroke (PntT = Ptrn).</summary>
        private static byte[] BuildPatternStrokeEffects()
        {
            var pattern = new DescriptorBuilder("Ptrn");
            pattern.AddString("Idnt", "pat-07");
            pattern.AddString("Nm  ", "Denim");

            var phase = new DescriptorBuilder("Pnt ");
            phase.AddDouble("Hrzn", 2);
            phase.AddDouble("Vrtc", -3);

            var d = new DescriptorBuilder("FrFX");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Nrml");
            d.AddPercent("Opct", 88);
            d.AddPixels("Sz  ", 6);
            d.AddEnum("Styl", "FStl", "CtrF");
            d.AddEnum("PntT", "frct", "Ptrn");
            d.AddObject("Ptrn", pattern);
            d.AddPercent("Scl ", 75);
            d.AddBool("lnkA", true);
            d.AddObject("phase", phase);
            d.AddAngle("Angl", 30);
            d.AddBool("overprint", true);

            var root = new DescriptorBuilder("null");
            root.AddBool("masterFXSwitch", true);
            root.AddObject("FrFX", d);

            var block = new ByteWriter();
            block.WriteUInt32(0);
            block.WriteUInt32(16);
            block.WriteBytes(root.ToBytes());
            return block.ToBytes();
        }

        private static byte[] BuildLfx2Effects()
        {
            var root = new DescriptorBuilder("null");
            root.AddBool("masterFXSwitch", true);
            root.AddObject("DrSh", DropShadow());
            root.AddObject("IrSh", InnerShadow());
            root.AddObject("OrGl", OuterGlow());
            root.AddObject("GrFl", GradientOverlay());
            root.AddObject("patternFill", PatternOverlay());
            root.AddObject("FrFX", Stroke());
            root.AddObject("ChFX", Satin());
            root.AddObject("ebbl", Bevel());

            // lfx2 layout: object-effects version u32 (0), descriptor version u32 (16), descriptor.
            var block = new ByteWriter();
            block.WriteUInt32(0);
            block.WriteUInt32(16);
            block.WriteBytes(root.ToBytes());
            return block.ToBytes();
        }

        private static DescriptorBuilder DropShadow()
        {
            var d = new DescriptorBuilder("DrSh");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Mltp");
            d.AddRgbColor("Clr ", 0, 0, 0);
            d.AddPercent("Opct", 75);
            d.AddAngle("lagl", 135);
            d.AddPixels("Dstn", 10);
            d.AddPercent("Ckmt", 0);
            d.AddPixels("blur", 12);
            d.AddPercent("Nose", 35); // the 杂色 under test
            d.AddBool("uglg", false);
            d.AddBool("layerConceals", true);
            d.AddBool("AntA", true);
            d.AddObject("TrnS", Contour("Ring", new object[][]
            {
                new object[] { 0.0, 0.0, true },
                new object[] { 128.0, 255.0, false },
                new object[] { 255.0, 255.0, false },
            }));
            return d;
        }

        /// <summary>A ShpC contour: Crv  list of CrPt points (Hrzn, Vrtc, Cnty).</summary>
        private static DescriptorBuilder Contour(string name, object[][] points)
        {
            var shape = new DescriptorBuilder("ShpC");
            shape.AddString("Nm  ", name);
            var list = new List<DescriptorBuilder>();
            foreach (var point in points)
            {
                var crpt = new DescriptorBuilder("CrPt");
                crpt.AddDouble("Hrzn", (double)point[0]);
                crpt.AddDouble("Vrtc", (double)point[1]);
                crpt.AddBool("Cnty", !(bool)point[2]);
                list.Add(crpt);
            }

            shape.AddList("Crv ", list);
            return shape;
        }

        private static DescriptorBuilder InnerShadow()
        {
            var d = new DescriptorBuilder("IrSh");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Mltp");
            d.AddRgbColor("Clr ", 60, 40, 20);
            d.AddPercent("Opct", 60);
            d.AddAngle("lagl", 135);
            d.AddPixels("Dstn", 5);
            d.AddPercent("Ckmt", 0);
            d.AddPixels("blur", 8);
            d.AddPercent("Nose", 0);
            d.AddBool("uglg", false);
            d.AddBool("AntA", false);
            return d;
        }

        private static DescriptorBuilder OuterGlow()
        {
            var d = new DescriptorBuilder("OrGl");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Scrn");
            d.AddRgbColor("Clr ", 255, 255, 190);
            d.AddPercent("Opct", 40);
            d.AddPercent("Ckmt", 0);
            d.AddPixels("blur", 16);
            d.AddPercent("Nose", 12);
            d.AddBool("AntA", false);
            d.AddEnum("GlwT", "BETE", "PrBL"); // Precise
            d.AddEnum("glwS", "IGSr", "SrcE"); // Edge source
            d.AddPercent("Inpr", 60); // Range
            return d;
        }

        private static DescriptorBuilder GradientOverlay()
        {
            var grad = new DescriptorBuilder("Grdn");
            grad.AddString("Nm  ", "Custom Fire");
            grad.AddEnum("GrdF", "GrdF", "Strt");
            grad.AddLong("Intr", 2048);

            var red = new DescriptorBuilder("Clrt");
            red.AddObject("Clr ", RgbColor(255, 40, 0));
            red.AddEnum("Type", "Clry", "UsrS");
            red.AddLong("Lctn", 0);
            red.AddLong("Mdpn", 50);

            var yellow = new DescriptorBuilder("Clrt");
            yellow.AddObject("Clr ", RgbColor(255, 200, 0));
            yellow.AddEnum("Type", "Clry", "UsrS");
            yellow.AddLong("Lctn", 4096);
            yellow.AddLong("Mdpn", 30);

            grad.AddList("Clrs", new List<DescriptorBuilder> { red, yellow });

            var alpha0 = new DescriptorBuilder("TrnS");
            alpha0.AddLong("Opct", 100);
            alpha0.AddLong("Lctn", 2048);
            alpha0.AddLong("Mdpn", 50);

            var alpha1 = new DescriptorBuilder("TrnS");
            alpha1.AddLong("Opct", 0);
            alpha1.AddLong("Lctn", 4096);
            alpha1.AddLong("Mdpn", 50);

            grad.AddList("Trns", new List<DescriptorBuilder> { alpha0, alpha1 });

            var offset = new DescriptorBuilder("Pnt ");
            offset.AddDouble("Hrzn", 10);
            offset.AddDouble("Vrtc", -5);

            var d = new DescriptorBuilder("GrFl");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Nrml");
            d.AddPercent("Opct", 80);
            d.AddEnum("Type", "GrdT", "Rdl "); // Radial
            d.AddAngle("Angl", 45);
            d.AddPercent("Scl ", 100);
            d.AddBool("Algn", true);
            d.AddBool("Rvrs", false);
            d.AddObject("Ofst", offset);
            d.AddBool("Dthr", true);
            d.AddObject("Grad", grad);
            return d;
        }

        private static DescriptorBuilder PatternOverlay()
        {
            var pattern = new DescriptorBuilder("Ptrn");
            pattern.AddString("Idnt", "pat-01");
            pattern.AddString("Nm  ", "Checker");

            var phase = new DescriptorBuilder("Pnt ");
            phase.AddDouble("Hrzn", 4);
            phase.AddDouble("Vrtc", -4);

            var d = new DescriptorBuilder("patternFill");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Nrml");
            d.AddPercent("Opct", 100);
            d.AddObject("Ptrn", pattern);
            d.AddPercent("Scl ", 200);
            d.AddBool("Algn", false);
            d.AddBool("lnkA", false); // exercise the unlinked path; PS omits the key when checked
            d.AddObject("phase", phase);
            d.AddAngle("Angl", 0);
            return d;
        }

        private static DescriptorBuilder Stroke()
        {
            var d = new DescriptorBuilder("FrFX");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Nrml");
            d.AddPercent("Opct", 100);
            d.AddPixels("Sz  ", 4);
            d.AddEnum("Styl", "FStl", "OutF");
            d.AddEnum("PntT", "frct", "SClr");
            d.AddRgbColor("Clr ", 0, 120, 255);
            d.AddBool("overprint", false);
            return d;
        }

        private static DescriptorBuilder Satin()
        {
            var d = new DescriptorBuilder("ChFX");
            d.AddBool("enab", true);
            d.AddEnum("Md  ", "BlnM", "Mltp");
            d.AddRgbColor("Clr ", 30, 30, 90);
            d.AddPercent("Opct", 50);
            d.AddAngle("lagl", 19);
            d.AddPixels("Dstn", 11);
            d.AddPixels("blur", 14);
            d.AddBool("Invr", false);
            d.AddBool("AntA", true);
            d.AddObject("MpgS", Contour("Ring", new object[][]
            {
                new object[] { 0.0, 0.0, false },
                new object[] { 128.0, 255.0, false },
                new object[] { 255.0, 255.0, false },
            }));
            return d;
        }

        private static DescriptorBuilder Bevel()
        {
            var pattern = new DescriptorBuilder("Ptrn");
            pattern.AddString("Idnt", "pat-01");
            pattern.AddString("Nm  ", "Checker");

            var phase = new DescriptorBuilder("Pnt ");
            phase.AddDouble("Hrzn", 0);
            phase.AddDouble("Vrtc", 0);

            var d = new DescriptorBuilder("ebbl");
            d.AddBool("enab", true);
            d.AddEnum("hglM", "BlnM", "Scrn");
            d.AddRgbColor("hglC", 255, 255, 255);
            d.AddPercent("hglO", 75);
            d.AddEnum("sdwM", "BlnM", "Mltp");
            d.AddRgbColor("sdwC", 0, 0, 0);
            d.AddPercent("sdwO", 75);
            d.AddEnum("bvlS", "BESl", "InrB");
            d.AddEnum("bvlT", "bvlT", "PrBL");
            d.AddBool("uglg", false);
            d.AddAngle("lagl", 120);
            d.AddAngle("Lald", 40);
            d.AddPercent("srgR", 83);
            d.AddPixels("blur", 10);
            d.AddEnum("bvlD", "BESs", "In  ");
            d.AddPixels("Sftn", 5);
            d.AddBool("useShape", true);
            d.AddObject("MpgS", Contour("Cone", new object[][]
            {
                new object[] { 0.0, 0.0, true },
                new object[] { 89.0, 0.0, true },
                new object[] { 128.0, 255.0, false },
                new object[] { 255.0, 0.0, false },
            }));
            d.AddBool("antialiasGloss", true);
            d.AddObject("TrnS", Contour("Round", new object[][]
            {
                new object[] { 0.0, 0.0, false },
                new object[] { 255.0, 255.0, false },
            }));
            d.AddPercent("Inpr", 50); // contour range
            d.AddBool("useTexture", true);
            d.AddObject("Ptrn", pattern);
            d.AddObject("phase", phase);
            d.AddPercent("Scl ", 150);
            d.AddBool("Algn", false); // the texture's single "link with layer" checkbox, unchecked
            d.AddPercent("textureDepth", 150);
            d.AddBool("InvT", true);
            d.AddBool("AntA", false);
            return d;
        }

        private static DescriptorBuilder RgbColor(byte r, byte g, byte b)
        {
            var color = new DescriptorBuilder("RGBC");
            color.AddDouble("Rd  ", r);
            color.AddDouble("Grn ", g);
            color.AddDouble("Bl  ", b);
            return color;
        }

        // ---------- Photoshop 5 lrFX block (version, count, records) ----------

        private static byte[] BuildLrFxEffects()
        {
            var block = new ByteWriter();
            block.WriteUInt16(0); // version
            block.WriteUInt16(2); // record count

            var common = new ByteWriter();
            common.WriteUInt32(0); // record version
            common.WriteByte(1); // master visible
            block.WriteAscii("8BIM");
            block.WriteAscii("cmnS");
            block.WriteUInt32((uint)common.Length);
            block.WriteBytes(common.ToBytes());

            var shadow = new ByteWriter();
            shadow.WriteUInt32(0); // record version
            shadow.WriteFixed16(12); // size
            shadow.WriteFixed16(0); // intensity
            shadow.WriteFixed16(120); // angle
            shadow.WriteFixed16(8); // distance
            WriteLegacyColor(shadow, 255, 0, 0);
            shadow.WriteAscii("8BIM");
            shadow.WriteAscii("Mltp");
            shadow.WriteByte(1); // enabled
            shadow.WriteByte(0); // use global light
            shadow.WriteByte(191); // opacity 75%
            block.WriteAscii("8BIM");
            block.WriteAscii("dsdw");
            block.WriteUInt32((uint)shadow.Length);
            block.WriteBytes(shadow.ToBytes());

            var fill = new ByteWriter();
            fill.WriteUInt32(0); // record version
            fill.WriteAscii("8BIM");
            fill.WriteAscii("Nrml");
            WriteLegacyColor(fill, 255, 200, 0);
            fill.WriteByte(255); // opacity
            fill.WriteByte(1); // enabled
            block.WriteAscii("8BIM");
            block.WriteAscii("sofi");
            block.WriteUInt32((uint)fill.Length);
            block.WriteBytes(fill.ToBytes());

            return block.ToBytes();
        }

        private static void WriteLegacyColor(ByteWriter writer, byte r, byte g, byte b)
        {
            writer.WriteUInt16(0); // RGB
            writer.WriteUInt16((ushort)(r * 257));
            writer.WriteUInt16((ushort)(g * 257));
            writer.WriteUInt16((ushort)(b * 257));
            writer.WriteUInt16(0);
        }

        // ---------- minimal big-endian byte writer ----------

        internal sealed class ByteWriter
        {
            private readonly List<byte> _bytes = new List<byte>();

            public int Length => _bytes.Count;

            public byte[] ToBytes() => _bytes.ToArray();

            public void WriteBytes(byte[] data) => _bytes.AddRange(data);

            public void WriteAscii(string text)
            {
                foreach (var c in text)
                {
                    _bytes.Add((byte)c);
                }
            }

            public void WriteByte(byte value) => _bytes.Add(value);

            public void Skip(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    _bytes.Add(0);
                }
            }

            public void WriteUInt16(ushort value)
            {
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)value);
            }

            public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

            public void WriteUInt32(uint value)
            {
                _bytes.Add((byte)(value >> 24));
                _bytes.Add((byte)(value >> 16));
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)value);
            }

            public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

            public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

            public void WriteInt64(long value)
            {
                var bits = unchecked((ulong)value);
                for (var shift = 56; shift >= 0; shift -= 8)
                {
                    _bytes.Add((byte)(bits >> shift));
                }
            }

            public void WriteFixed16(double value) => WriteInt32((int)Math.Round(value * 65536.0));

            public void WritePascalString(string text)
            {
                var bytes = System.Text.Encoding.GetEncoding(28591).GetBytes(text);
                _bytes.Add((byte)bytes.Length);
                WriteBytes(bytes);

                // Layer names are padded so the whole field is a multiple of four,
                // which is what ReadPascalString(4) expects.
                var total = 1 + bytes.Length;
                while ((total & 3) != 0)
                {
                    _bytes.Add(0);
                    total++;
                }
            }
        }

        /// <summary>Writes an Action Manager descriptor in the exact layout the reader expects.</summary>
        internal sealed class DescriptorBuilder
        {
            private readonly ByteWriter _writer = new ByteWriter();
            private readonly List<KeyValuePair<string, ByteWriter>> _values = new List<KeyValuePair<string, ByteWriter>>();
            private readonly string _classId;
            private readonly string _name;

            public DescriptorBuilder(string classId, string name = "")
            {
                _classId = classId;
                _name = name;
            }

            public void AddBool(string key, bool value)
            {
                var v = new ByteWriter();
                v.WriteAscii("bool");
                v.WriteByte(value ? (byte)1 : (byte)0);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddDouble(string key, double value)
            {
                var v = new ByteWriter();
                v.WriteAscii("doub");
                v.WriteDouble(value);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddUnit(string key, string unit, double value)
            {
                var v = new ByteWriter();
                v.WriteAscii("UntF");
                v.WriteAscii(unit);
                v.WriteDouble(value);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddPercent(string key, double value) => AddUnit(key, "#Prc", value);

            public void AddPixels(string key, double value) => AddUnit(key, "#Pxl", value);

            public void AddAngle(string key, double value) => AddUnit(key, "#Ang", value);

            public void AddString(string key, string value)
            {
                var v = new ByteWriter();
                v.WriteAscii("TEXT");
                v.WriteUnicodeString(value);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddLong(string key, int value)
            {
                var v = new ByteWriter();
                v.WriteAscii("long");
                v.WriteInt32(value);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddList(string key, List<DescriptorBuilder> items)
            {
                var v = new ByteWriter();
                v.WriteAscii("VlLs");
                v.WriteUInt32((uint)items.Count);
                foreach (var item in items)
                {
                    v.WriteAscii("Objc");
                    v.WriteBytes(item.ToBytes());
                }

                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddEnum(string key, string enumType, string enumValue)
            {
                var v = new ByteWriter();
                v.WriteAscii("enum");
                v.WriteId(enumType);
                v.WriteId(enumValue);
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddRgbColor(string key, byte r, byte g, byte b)
            {
                var color = new DescriptorBuilder("RGBC");
                color.AddDouble("Rd  ", r);
                color.AddDouble("Grn ", g);
                color.AddDouble("Bl  ", b);
                var v = new ByteWriter();
                v.WriteAscii("Objc");
                v.WriteBytes(color.ToBytes());
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public void AddObject(string key, DescriptorBuilder nested)
            {
                var v = new ByteWriter();
                v.WriteAscii("Objc");
                v.WriteBytes(nested.ToBytes());
                _values.Add(new KeyValuePair<string, ByteWriter>(key, v));
            }

            public byte[] ToBytes()
            {
                _writer.WriteUnicodeString(_name);
                _writer.WriteId(_classId);
                _writer.WriteUInt32((uint)_values.Count);
                foreach (var item in _values)
                {
                    _writer.WriteId(item.Key);
                    _writer.WriteBytes(item.Value.ToBytes());
                }

                return _writer.ToBytes();
            }
        }
    }

    internal static class DescriptorBuilderExtensions
    {
        public static void WriteUnicodeString(this Program.ByteWriter writer, string text)
        {
            writer.WriteUInt32((uint)text.Length);
            foreach (var c in text)
            {
                writer.WriteUInt16((ushort)c);
            }
        }

        /// <summary>Four-character IDs use the 4-byte-signature form, longer ones the length+bytes form.</summary>
        public static void WriteId(this Program.ByteWriter writer, string id)
        {
            if (id.Length == 4)
            {
                writer.WriteUInt32(0);
                writer.WriteAscii(id);
            }
            else
            {
                writer.WriteUInt32((uint)id.Length);
                writer.WriteAscii(id);
            }
        }
    }
}
