using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>Value types an Action Manager descriptor can hold.</summary>
    public enum DescriptorValueType
    {
        Boolean,
        Integer,
        LargeInteger,
        Double,
        UnitFloat,
        UnitFloats,
        String,
        Enum,
        Object,
        List,
        ObjectArray,
        Class,
        Reference,
        RawData,
        Alias,
    }

    /// <summary>A value inside a <see cref="Descriptor"/>.</summary>
    public sealed class DescriptorValue
    {
        public DescriptorValueType Type { get; set; }

        public bool Boolean { get; set; }

        public long Integer { get; set; }

        public double Double { get; set; }

        /// <summary>Unit key for unit floats (#Pxl, #Prc, #Ang, #Pnt and so on).</summary>
        public string Unit { get; set; }

        public double[] Doubles { get; set; }

        public string String { get; set; }

        /// <summary>Enum type ID (for Enum values) or class ID.</summary>
        public string EnumType { get; set; }

        public string EnumValue { get; set; }

        public Descriptor Object { get; set; }

        public IReadOnlyList<DescriptorValue> List { get; set; }

        /// <summary>Raw bytes for tdta / alis / "Pth " values that need no further structure.</summary>
        public byte[] Raw { get; set; }

        /// <summary>Numeric view: integers, doubles and unit floats.</summary>
        public double AsNumber()
        {
            switch (Type)
            {
                case DescriptorValueType.Integer:
                case DescriptorValueType.LargeInteger:
                    return Integer;
                case DescriptorValueType.Double:
                case DescriptorValueType.UnitFloat:
                    return Double;
                case DescriptorValueType.Boolean:
                    return Boolean ? 1 : 0;
                default:
                    return double.NaN;
            }
        }

        public override string ToString()
        {
            switch (Type)
            {
                case DescriptorValueType.Boolean: return Boolean ? "true" : "false";
                case DescriptorValueType.Integer:
                case DescriptorValueType.LargeInteger: return Integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case DescriptorValueType.Double: return Double.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case DescriptorValueType.UnitFloat: return Double.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Unit;
                case DescriptorValueType.String: return "\"" + String + "\"";
                case DescriptorValueType.Enum: return EnumType + "." + EnumValue;
                case DescriptorValueType.Object: return Object != null ? Object.ToString() : "{}";
                case DescriptorValueType.List: return "[" + (List != null ? List.Count : 0) + " items]";
                default: return Type.ToString();
            }
        }
    }

    /// <summary>
    /// A parsed Photoshop Action Manager descriptor (Objc). Descriptors hold layer
    /// effects and most other modern PSD metadata. Keys keep file order.
    /// </summary>
    public sealed class Descriptor
    {
        /// <summary>
        /// Photoshop stores preset names as localized tokens, e.g.
        /// <c>$$$/Contours/Defaults/Cone=锥形</c>: the part before '=' is the lookup
        /// key, the part after it is the text to display. Plain strings pass through.
        /// </summary>
        internal static string Localize(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith("$$$/", StringComparison.Ordinal))
            {
                return text;
            }

            var equals = text.IndexOf('=');
            if (equals >= 0 && equals + 1 < text.Length)
            {
                return text.Substring(equals + 1);
            }

            var slash = text.LastIndexOf('/');
            return slash >= 0 && slash + 1 < text.Length ? text.Substring(slash + 1) : text;
        }

        private readonly List<KeyValuePair<string, DescriptorValue>> _items = new List<KeyValuePair<string, DescriptorValue>>();

        public string Name { get; set; }

        public string ClassId { get; set; }

        public IReadOnlyList<KeyValuePair<string, DescriptorValue>> Items => _items;

        public void Add(string key, DescriptorValue value) => _items.Add(new KeyValuePair<string, DescriptorValue>(key, value));

        public DescriptorValue this[string key]
        {
            get
            {
                DescriptorValue value;
                return TryGet(key, out value) ? value : null;
            }
        }

        public bool TryGet(string key, out DescriptorValue value)
        {
            foreach (var item in _items)
            {
                if (item.Key == key)
                {
                    value = item.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public string GetString(string key)
        {
            DescriptorValue v;
            return TryGet(key, out v) && v.Type == DescriptorValueType.String ? v.String : null;
        }

        public Descriptor GetObject(string key)
        {
            DescriptorValue v;
            return TryGet(key, out v) ? v.Object : null;
        }

        public IReadOnlyList<DescriptorValue> GetList(string key)
        {
            DescriptorValue v;
            return TryGet(key, out v) ? v.List : null;
        }

        public double GetNumber(string key, double fallback = double.NaN)
        {
            DescriptorValue v;
            if (!TryGet(key, out v))
            {
                return fallback;
            }

            var number = v.AsNumber();
            return double.IsNaN(number) ? fallback : number;
        }

        /// <summary>Returns true when the key exists and carries a numeric value.</summary>
        public bool HasNumber(string key)
        {
            DescriptorValue v;
            if (!TryGet(key, out v))
            {
                return false;
            }

            return !double.IsNaN(v.AsNumber());
        }

        public bool GetBoolean(string key, bool fallback = false)
        {
            DescriptorValue v;
            return TryGet(key, out v) && v.Type == DescriptorValueType.Boolean ? v.Boolean : fallback;
        }

        /// <summary>Returns true when the key exists and carries a boolean value.</summary>
        public bool HasBoolean(string key)
        {
            DescriptorValue v;
            return TryGet(key, out v) && v.Type == DescriptorValueType.Boolean;
        }

        public string GetEnum(string key)
        {
            DescriptorValue v;
            return TryGet(key, out v) && v.Type == DescriptorValueType.Enum ? v.EnumValue : null;
        }

        /// <summary>Reads a color object (RGB, gray, CMYK or HSB) as sRGB, or null for other models.</summary>
        public PsdColor? GetColor(string key)
        {
            var color = GetObject(key);
            return color == null ? (PsdColor?)null : ColorFromDescriptor(color);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            Append(builder, 0);
            return builder.ToString();
        }

        private void Append(StringBuilder builder, int indent)
        {
            builder.Append(ClassId).Append(" {").AppendLine();
            foreach (var item in _items)
            {
                builder.Append(' ', (indent + 1) * 2).Append(item.Key).Append(": ");
                AppendValue(builder, item.Value, indent + 1);
                builder.AppendLine();
            }

            builder.Append(' ', indent * 2).Append('}');
        }

        private static void AppendValue(StringBuilder builder, DescriptorValue value, int indent)
        {
            if ((value.Type == DescriptorValueType.Object || value.Type == DescriptorValueType.ObjectArray) && value.Object != null)
            {
                value.Object.Append(builder, indent);
            }
            else if (value.Type == DescriptorValueType.List && value.List != null)
            {
                builder.Append('[').AppendLine();
                foreach (var item in value.List)
                {
                    builder.Append(' ', (indent + 1) * 2);
                    AppendValue(builder, item, indent + 1);
                    builder.AppendLine();
                }

                builder.Append(' ', indent * 2).Append(']');
            }
            else
            {
                builder.Append(value);
            }
        }

        /// <summary>Reads a descriptor body (no version prefix).</summary>
        internal static Descriptor Read(BigEndianReader reader) => DescriptorReader.ReadDescriptor(reader, 0);

        /// <summary>Reads a u32 descriptor version (normally 16) followed by a descriptor.</summary>
        internal static Descriptor ReadVersioned(BigEndianReader reader)
        {
            var version = reader.ReadUInt32();
            if (version != 16)
            {
                throw new PsdFormatException(string.Format("Unsupported descriptor version {0}.", version));
            }

            return Read(reader);
        }

        internal static PsdColor? ColorFromDescriptor(Descriptor color)
        {
            DescriptorValue rf;
            DescriptorValue rd;
            color.TryGet("redFloat", out rf);
            var isRgb = rf != null || color.TryGet("Rd  ", out rd);
            if (isRgb)
            {
                double red, green, blue;
                if (rf != null)
                {
                    red = rf.AsNumber() * 255;
                    green = color.GetNumber("greenFloat", 0) * 255;
                    blue = color.GetNumber("blueFloat", 0) * 255;
                }
                else
                {
                    red = color.GetNumber("Rd  ", 0);
                    green = color.GetNumber("Grn ", 0);
                    blue = color.GetNumber("Bl  ", 0);
                }

                return new PsdColor(PsdColor.ClampByte(red), PsdColor.ClampByte(green), PsdColor.ClampByte(blue));
            }

            // 'Gry ' is the black percentage (100 = black) and CMYC components are ink percentages.
            DescriptorValue gray;
            if (color.TryGet("Gry ", out gray))
            {
                var g = PsdColor.ClampByte(255 - (gray.AsNumber() * 2.55));
                return new PsdColor(g, g, g);
            }

            DescriptorValue cyn;
            if (color.TryGet("Cyn ", out cyn))
            {
                var c = color.GetNumber("Cyn ", 0) / 100.0;
                var m = color.GetNumber("Mgnt", 0) / 100.0;
                var y = color.GetNumber("Ylw ", 0) / 100.0;
                var k = color.GetNumber("Blck", 0) / 100.0;
                return new PsdColor(
                    PsdColor.ClampByte(255 * (1 - c) * (1 - k)),
                    PsdColor.ClampByte(255 * (1 - m) * (1 - k)),
                    PsdColor.ClampByte(255 * (1 - y) * (1 - k)));
            }

            DescriptorValue h;
            if (color.TryGet("H   ", out h))
            {
                double hue = color.GetNumber("H   ", 0) / 360.0;
                var s = color.GetNumber("Strt", 0) / 100.0;
                var b = color.GetNumber("Brgh", 0) / 100.0;
                double r, g, bl;
                HsbToRgb(hue, s, b, out r, out g, out bl);
                return new PsdColor(PsdColor.ClampByte(r * 255), PsdColor.ClampByte(g * 255), PsdColor.ClampByte(bl * 255));
            }

            return null;
        }

        private static void HsbToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            h = (h - Math.Floor(h)) * 6;
            var i = (int)Math.Floor(h) % 6;
            var f = h - Math.Floor(h);
            var p = v * (1 - s);
            var q = v * (1 - (s * f));
            var t = v * (1 - (s * (1 - f)));
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }
    }

    internal static class DescriptorReader
    {
        // Real Photoshop data nests a handful of levels; a crafted file could nest
        // one level per dozen bytes and exhaust the stack.
        private const int MaxDepth = 64;

        public static Descriptor ReadDescriptor(BigEndianReader reader, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new PsdFormatException("Descriptor nesting is too deep.");
            }

            var name = reader.ReadUnicodeString();
            var classId = ReadId(reader);
            var descriptor = new Descriptor { Name = name, ClassId = classId };
            var count = reader.ReadUInt32();
            CheckCount(count, 8, reader);
            for (var i = 0u; i < count; i++)
            {
                var key = ReadId(reader);
                var type = reader.ReadSignature();
                descriptor.Add(key, ReadValue(reader, type, depth + 1));
            }

            return descriptor;
        }

        public static string ReadId(BigEndianReader reader)
        {
            var length = reader.ReadUInt32();
            if (length == 0)
            {
                return reader.ReadSignature();
            }

            if (length > (uint)reader.Remaining)
            {
                throw new PsdFormatException("Descriptor ID length exceeds the remaining data.");
            }

            return BigEndianReader.DecodeLatin1(reader.ReadBytes((int)length));
        }

        private static void CheckCount(uint count, int minimumItemBytes, BigEndianReader reader)
        {
            if ((ulong)count * (ulong)minimumItemBytes > (ulong)reader.Remaining)
            {
                throw new PsdFormatException("Descriptor item count exceeds the remaining data.");
            }
        }

        public static DescriptorValue ReadValue(BigEndianReader reader, string type, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new PsdFormatException("Descriptor nesting is too deep.");
            }

            switch (type)
            {
                case "bool":
                    return new DescriptorValue { Type = DescriptorValueType.Boolean, Boolean = reader.ReadByte() != 0 };
                case "long":
                    return new DescriptorValue { Type = DescriptorValueType.Integer, Integer = reader.ReadInt32() };
                case "comp":
                    return new DescriptorValue { Type = DescriptorValueType.LargeInteger, Integer = reader.ReadInt64() };
                case "doub":
                    return new DescriptorValue { Type = DescriptorValueType.Double, Double = reader.ReadDouble() };
                case "UntF":
                    {
                        var unit = reader.ReadSignature();
                        return new DescriptorValue { Type = DescriptorValueType.UnitFloat, Unit = unit, Double = reader.ReadDouble() };
                    }

                case "UnFl":
                    {
                        var unit = reader.ReadSignature();
                        var count = reader.ReadUInt32();
                        CheckCount(count, 8, reader);
                        var values = new double[count];
                        for (var i = 0; i < values.Length; i++)
                        {
                            values[i] = reader.ReadDouble();
                        }

                        return new DescriptorValue { Type = DescriptorValueType.UnitFloats, Unit = unit, Doubles = values };
                    }

                case "TEXT":
                    return new DescriptorValue { Type = DescriptorValueType.String, String = reader.ReadUnicodeString() };
                case "enum":
                    {
                        var enumType = ReadId(reader);
                        return new DescriptorValue { Type = DescriptorValueType.Enum, EnumType = enumType, EnumValue = ReadId(reader) };
                    }

                case "Objc":
                case "GlbO":
                    return new DescriptorValue { Type = DescriptorValueType.Object, Object = ReadDescriptor(reader, depth + 1) };
                case "VlLs":
                    {
                        var count = reader.ReadUInt32();
                        CheckCount(count, 4, reader);
                        var list = new List<DescriptorValue>((int)Math.Min(count, 4096));
                        for (var i = 0u; i < count; i++)
                        {
                            list.Add(ReadValue(reader, reader.ReadSignature(), depth + 1));
                        }

                        return new DescriptorValue { Type = DescriptorValueType.List, List = list };
                    }

                case "ObAr":
                    {
                        var count = reader.ReadUInt32();
                        return new DescriptorValue { Type = DescriptorValueType.ObjectArray, Integer = count, Object = ReadDescriptor(reader, depth + 1) };
                    }

                case "tdta":
                    {
                        var length = reader.ReadUInt32();
                        return new DescriptorValue { Type = DescriptorValueType.RawData, Raw = reader.ReadBytes((long)length) };
                    }

                case "alis":
                case "Pth ":
                    {
                        var length = reader.ReadUInt32();
                        return new DescriptorValue { Type = DescriptorValueType.Alias, Raw = reader.ReadBytes((long)length) };
                    }

                case "type":
                case "GlbC":
                    {
                        var className = reader.ReadUnicodeString();
                        return new DescriptorValue { Type = DescriptorValueType.Class, String = className, EnumValue = ReadId(reader) };
                    }

                case "obj ":
                    {
                        var count = reader.ReadUInt32();
                        CheckCount(count, 4, reader);
                        var items = new List<DescriptorValue>();
                        for (var i = 0u; i < count; i++)
                        {
                            var form = reader.ReadSignature();
                            reader.ReadUnicodeString(); // class name
                            ReadId(reader); // class id
                            switch (form)
                            {
                                case "prop":
                                    ReadId(reader);
                                    break;
                                case "Enmr":
                                    ReadId(reader);
                                    ReadId(reader);
                                    break;
                                case "rele":
                                case "Idnt":
                                case "indx":
                                    reader.ReadUInt32();
                                    break;
                                case "name":
                                    reader.ReadUnicodeString();
                                    break;
                                case "Clss":
                                    break;
                                default:
                                    throw new PsdFormatException(string.Format("Unsupported descriptor reference form '{0}'.", form));
                            }

                            items.Add(new DescriptorValue { Type = DescriptorValueType.String, String = form });
                        }

                        return new DescriptorValue { Type = DescriptorValueType.Reference, String = "obj ", List = items };
                    }

                default:
                    throw new PsdFormatException(string.Format("Unsupported descriptor value type '{0}'.", type));
            }
        }
    }
}
