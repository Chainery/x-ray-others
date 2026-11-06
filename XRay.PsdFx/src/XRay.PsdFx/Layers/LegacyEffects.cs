using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Photoshop 5 layer effects (lrFX), translated into the lfx2 descriptor shape
    /// that <see cref="PsdLayerStyle"/> reads. The block is a u16 version and a u16
    /// count, then records of 8BIM, a key and a u32 size whose payload starts at a
    /// u32 version (0, or 2 with trailing native colors). Values are 16.16 fixed
    /// point, opacities a 0-255 byte, colors the 10-byte legacy record.
    /// Ported from XRay.Psd's LegacyEffects.cs.
    /// </summary>
    internal static class LegacyEffects
    {
        private const int ColorBytes = 10;

        /// <summary>The translated effects, or null when the block holds none or does not decode.</summary>
        public static Descriptor ToDescriptor(byte[] data)
        {
            try
            {
                var reader = new BigEndianReader(data);
                reader.ReadUInt16(); // version
                var count = reader.ReadUInt16();
                var root = new Descriptor { ClassId = "null" };
                var visible = true;
                var any = false;
                for (var index = 0; index < count && reader.Remaining >= 12; index++)
                {
                    var signature = reader.ReadSignature();
                    var key = reader.ReadSignature();
                    if (signature != "8BIM" && signature != "8B64")
                    {
                        break;
                    }

                    var size = reader.ReadUInt32();
                    if (size > (uint)reader.Remaining)
                    {
                        break;
                    }

                    var record = reader.Slice(size);
                    if (key == "cmnS")
                    {
                        record.ReadUInt32();
                        visible = record.ReadByte() != 0;
                        continue;
                    }

                    Descriptor effect;
                    string classId;
                    if (Translate(key, record, out classId, out effect))
                    {
                        root.Add(classId, new DescriptorValue { Type = DescriptorValueType.Object, Object = effect });
                        any = true;
                    }
                }

                if (!any)
                {
                    return null;
                }

                root.Add("masterFXSwitch", new DescriptorValue { Type = DescriptorValueType.Boolean, Boolean = visible });
                return root;
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        private static bool Translate(string key, BigEndianReader r, out string classId, out Descriptor effect)
        {
            classId = null;
            effect = null;
            try
            {
                var version = r.ReadUInt32();
                switch (key)
                {
                    case "dsdw":
                    case "isdw":
                        {
                            var size = r.ReadFixed16();
                            var intensity = r.ReadFixed16();
                            var angle = r.ReadFixed16();
                            var distance = r.ReadFixed16();
                            var color = ReadColor(r);
                            var mode = Blend(r);
                            var enabled = r.ReadByte() != 0;
                            var global = r.ReadByte() != 0;
                            var opacity = r.ReadByte();
                            var native = Native(r, version);
                            if (native.HasValue)
                            {
                                color = native.Value;
                            }

                            classId = key == "dsdw" ? "DrSh" : "IrSh";
                            var d = Effect(classId, enabled, mode, color, opacity);
                            d.Add("uglg", Boolean(global));
                            d.Add("lagl", Number(Clamp(angle, -360, 360)));
                            d.Add("Dstn", Number(Clamp(distance, 0, 30000)));
                            d.Add("Ckmt", Number(0));
                            d.Add("blur", Number(Clamp(size, 0, 250)));
                            AddIntensity(d, intensity);
                            effect = d;
                            return true;
                        }

                    case "oglw":
                    case "iglw":
                        {
                            var size = r.ReadFixed16();
                            var intensity = r.ReadFixed16();
                            var color = ReadColor(r);
                            var mode = Blend(r);
                            var enabled = r.ReadByte() != 0;
                            var opacity = r.ReadByte();
                            // Inner glows of version 2 add a source byte: 1 Edge, 0 Center.
                            var edge = key != "iglw" || version < 2 || r.ReadByte() != 0;
                            var native = Native(r, version);
                            if (native.HasValue)
                            {
                                color = native.Value;
                            }

                            classId = key == "oglw" ? "OrGl" : "IrGl";
                            var d = Effect(classId, enabled, mode, color, opacity);
                            d.Add("GlwT", Enum("BETE", "SfBL"));
                            d.Add("Ckmt", Number(0));
                            d.Add("blur", Number(Clamp(size, 0, 250)));
                            d.Add("Inpr", Number(50));
                            if (key == "iglw")
                            {
                                d.Add("glwS", Enum("IGSr", edge ? "SrcE" : "SrcC"));
                            }

                            AddIntensity(d, intensity);
                            effect = d;
                            return true;
                        }

                    case "bevl":
                        {
                            var angle = r.ReadFixed16();
                            var depth = r.ReadFixed16();
                            var size = r.ReadFixed16();
                            var highlightMode = Blend(r);
                            var shadowMode = Blend(r);
                            var highlight = ReadColor(r);
                            var shadow = ReadColor(r);
                            var style = r.ReadByte();
                            var highlightOpacity = r.ReadByte();
                            var shadowOpacity = r.ReadByte();
                            var enabled = r.ReadByte() != 0;
                            var global = r.ReadByte() != 0;
                            var down = r.ReadByte() != 0;
                            if (version >= 2 && r.Remaining >= 2 * ColorBytes)
                            {
                                highlight = ReadColor(r);
                                shadow = ReadColor(r);
                            }

                            classId = "ebbl";
                            var d = new Descriptor { ClassId = "ebbl" };
                            d.Add("enab", Boolean(enabled));
                            d.Add("hglM", Enum("BlnM", highlightMode));
                            d.Add("hglC", Rgb(highlight));
                            d.Add("hglO", Number(highlightOpacity * 100.0 / 255));
                            d.Add("sdwM", Enum("BlnM", shadowMode));
                            d.Add("sdwC", Rgb(shadow));
                            d.Add("sdwO", Number(shadowOpacity * 100.0 / 255));
                            d.Add("bvlT", Enum("bvlT", "SfBL"));
                            d.Add("bvlS", Enum("BESl", style == 1 ? "OtrB" : style == 3 ? "Embs" : style == 4 ? "PlEb" : style == 5 ? "strokeEmboss" : "InrB"));
                            d.Add("uglg", Boolean(global));
                            d.Add("lagl", Number(Clamp(angle, -360, 360)));
                            d.Add("Lald", Number(30));
                            // Photoshop 5 stores the depth in pixels, at most 20.
                            var embossed = style == 3 || style == 4;
                            d.Add("srgR", Number(size > 0 ? Clamp(depth / Math.Min(size, 20) * 100, 1, 1000) : 100));
                            d.Add("blur", Number(Clamp(embossed ? size * 2 : size, 0, 250)));
                            d.Add("bvlD", Enum("BESs", down ? "Out " : "In  "));
                            effect = d;
                            return true;
                        }

                    case "sofi":
                        {
                            var mode = Blend(r);
                            var color = ReadColor(r);
                            var opacity = r.ReadByte();
                            var enabled = r.ReadByte() != 0;
                            var native = Native(r, version);
                            if (native.HasValue)
                            {
                                color = native.Value;
                            }

                            classId = "SoFi";
                            effect = Effect(classId, enabled, mode, color, opacity);
                            return true;
                        }

                    default:
                        return false;
                }
            }
            catch (PsdFormatException)
            {
                // A truncated record drops that effect only.
                return false;
            }
        }

        /// <summary>
        /// Legacy Intensity is a transfer contour, not Spread or Choke.
        /// </summary>
        private static void AddIntensity(Descriptor d, double intensity)
        {
            d.Add("AntA", Boolean(false));
            if (!(intensity > 0))
            {
                return;
            }

            var knee = 255 / (1 + (Math.Min(intensity, 1000) / 100));
            var curve = new List<DescriptorValue>
            {
                Object(Point(0, 0)),
                Object(Point(knee, 255)),
                Object(Point(255, 255)),
            };
            var shape = new Descriptor { ClassId = "ShpC" };
            shape.Add("Nm  ", new DescriptorValue { Type = DescriptorValueType.String, String = "Intensity" });
            shape.Add("Crv ", new DescriptorValue { Type = DescriptorValueType.List, List = curve });
            d.Add("TrnS", Object(shape));
        }

        private static Descriptor Point(double x, double y)
        {
            var point = new Descriptor { ClassId = "CrPt" };
            point.Add("Hrzn", Number(x));
            point.Add("Vrtc", Number(y));
            point.Add("Cnty", Boolean(false));
            return point;
        }

        private static Descriptor Effect(string classId, bool enabled, string mode, PsdColor color, byte opacity)
        {
            var d = new Descriptor { ClassId = classId };
            d.Add("enab", Boolean(enabled));
            d.Add("Md  ", Enum("BlnM", mode));
            d.Add("Clr ", Rgb(color));
            d.Add("Opct", Number(opacity * 100.0 / 255));
            return d;
        }

        /// <summary>
        /// Photoshop's 10-byte color: u16 space and four u16 components. 0 RGB, 1 HSB,
        /// 2 CMYK stored inverted (65535 is no ink), 8 grayscale on 0..10000.
        /// </summary>
        internal static PsdColor ReadColor(BigEndianReader reader)
        {
            var space = reader.ReadUInt16();
            var c0 = reader.ReadUInt16();
            var c1 = reader.ReadUInt16();
            var c2 = reader.ReadUInt16();
            var c3 = reader.ReadUInt16();
            switch (space)
            {
                case 1:
                    {
                        var hue = c0 * 360.0 / 65535.0;
                        var saturation = c1 / 65535.0;
                        var brightness = c2 / 65535.0;
                        double r, g, b;
                        HsbToRgb(hue, saturation, brightness, out r, out g, out b);
                        return new PsdColor(PsdColor.ClampByte(r * 255), PsdColor.ClampByte(g * 255), PsdColor.ClampByte(b * 255));
                    }

                case 2:
                    {
                        var cyan = (65535 - c0) / 65535.0;
                        var magenta = (65535 - c1) / 65535.0;
                        var yellow = (65535 - c2) / 65535.0;
                        var black = (65535 - c3) / 65535.0;
                        return new PsdColor(
                            PsdColor.ClampByte(255 * (1 - cyan) * (1 - black)),
                            PsdColor.ClampByte(255 * (1 - magenta) * (1 - black)),
                            PsdColor.ClampByte(255 * (1 - yellow) * (1 - black)));
                    }

                case 8:
                    {
                        var gray = PsdColor.ClampByte(c0 / 10000.0 * 255);
                        return new PsdColor(gray, gray, gray);
                    }

                default:
                    return new PsdColor((byte)(c0 / 257), (byte)(c1 / 257), (byte)(c2 / 257));
            }
        }

        private static void HsbToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            var position = (h / 60.0) % 6.0;
            var sector = (int)Math.Floor(position);
            var fraction = position - sector;
            var p = v * (1 - s);
            var q = v * (1 - (s * fraction));
            var t = v * (1 - (s * (1 - fraction)));
            switch (sector)
            {
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                case 5: r = v; g = p; b = q; break;
                default: r = v; g = t; b = p; break;
            }
        }

        /// <summary>The version 2 native color, which supersedes the first one.</summary>
        private static PsdColor? Native(BigEndianReader r, uint version)
        {
            return version >= 2 && r.Remaining >= ColorBytes ? ReadColor(r) : (PsdColor?)null;
        }

        private static string Blend(BigEndianReader r)
        {
            r.ReadSignature();
            return r.ReadSignature();
        }

        private static DescriptorValue Rgb(PsdColor color)
        {
            var d = new Descriptor { ClassId = "RGBC" };
            d.Add("Rd  ", Number(color.R));
            d.Add("Grn ", Number(color.G));
            d.Add("Bl  ", Number(color.B));
            return Object(d);
        }

        private static DescriptorValue Object(Descriptor d)
        {
            return new DescriptorValue { Type = DescriptorValueType.Object, Object = d };
        }

        private static DescriptorValue Number(double value)
        {
            return new DescriptorValue { Type = DescriptorValueType.Double, Double = value };
        }

        private static DescriptorValue Boolean(bool value)
        {
            return new DescriptorValue { Type = DescriptorValueType.Boolean, Boolean = value };
        }

        private static DescriptorValue Enum(string type, string value)
        {
            return new DescriptorValue { Type = DescriptorValueType.Enum, EnumType = type, EnumValue = value };
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
