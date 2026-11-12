using System;
using System.Linq;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>Channel Mixer: one output channel as percents of R, G, B plus a constant.</summary>
    internal struct MixerChannel
    {
        public MixerChannel(int red, int green, int blue, int constant)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Constant = constant;
        }

        public int Red;
        public int Green;
        public int Blue;
        public int Constant;
    }

    /// <summary>Channel Mixer (mixr).</summary>
    internal sealed class ChannelMixerSettings
    {
        public ChannelMixerSettings(bool monochrome, MixerChannel red, MixerChannel green, MixerChannel blue)
        {
            Monochrome = monochrome;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public bool Monochrome { get; private set; }

        public MixerChannel Red { get; private set; }

        public MixerChannel Green { get; private set; }

        public MixerChannel Blue { get; private set; }
    }

    /// <summary>Black and White (blwh): percents for the six hue ranges plus an optional tint.</summary>
    internal sealed class BlackWhiteSettings
    {
        public BlackWhiteSettings(int reds, int yellows, int greens, int cyans, int blues, int magentas, bool hasTint, PsdColor tint)
        {
            Reds = reds;
            Yellows = yellows;
            Greens = greens;
            Cyans = cyans;
            Blues = blues;
            Magentas = magentas;
            HasTint = hasTint;
            Tint = tint;
        }

        public static BlackWhiteSettings Default
        {
            get { return new BlackWhiteSettings(40, 60, 40, 60, 20, 80, false, default(PsdColor)); }
        }

        public int Reds { get; private set; }

        public int Yellows { get; private set; }

        public int Greens { get; private set; }

        public int Cyans { get; private set; }

        public int Blues { get; private set; }

        public int Magentas { get; private set; }

        public bool HasTint { get; private set; }

        public PsdColor Tint { get; private set; }
    }

    /// <summary>Selective Color (selc): CMYK corrections (-100..100) for nine color ranges.</summary>
    internal sealed class SelectiveColorSettings
    {
        public SelectiveColorSettings(bool absolute, SelectiveColorRange[] ranges)
        {
            Absolute = absolute;
            Ranges = ranges;
        }

        public bool Absolute { get; private set; }

        public SelectiveColorRange[] Ranges { get; private set; }
    }

    /// <summary>One Selective Color range correction.</summary>
    internal struct SelectiveColorRange
    {
        public SelectiveColorRange(int cyan, int magenta, int yellow, int black)
        {
            Cyan = cyan;
            Magenta = magenta;
            Yellow = yellow;
            Black = black;
        }

        public int Cyan;
        public int Magenta;
        public int Yellow;
        public int Black;

        public bool HasEffect
        {
            get { return Cyan != 0 || Magenta != 0 || Yellow != 0 || Black != 0; }
        }
    }

    /// <summary>Photo Filter (phfl): filter color, density (0..100) and Preserve Luminosity.</summary>
    internal struct PhotoFilterSettings
    {
        public PhotoFilterSettings(double red, double green, double blue, int density, bool preserveLuminosity)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Density = density;
            PreserveLuminosity = preserveLuminosity;
        }

        public double Red;
        public double Green;
        public double Blue;
        public int Density;
        public bool PreserveLuminosity;
    }

    /// <summary>Vibrance (vibA): vibrance and saturation, -100..100.</summary>
    internal struct VibranceSettings
    {
        public VibranceSettings(int vibrance, int saturation)
        {
            Vibrance = vibrance;
            Saturation = saturation;
        }

        public int Vibrance;
        public int Saturation;
    }

    /// <summary>
    /// The adjustments the reference does not model: Channel Mixer, Black and White,
    /// Selective Color, Gradient Map, Photo Filter and Vibrance.
    /// </summary>
    internal static partial class Adjustments
    {
        // ---- Parsing --------------------------------------------------------------------

        /// <summary>mixr: u16 version 1, u16 monochrome, then five-value records per output.</summary>
        internal static ChannelMixerSettings ParseChannelMixer(byte[] data)
        {
            var reader = new BigEndianReader(data);
            if (reader.Remaining < 14 || reader.ReadUInt16() != 1)
            {
                return null;
            }

            var monochrome = reader.ReadUInt16() != 0;
            var records = new MixerChannel[3];
            for (var i = 0; i < records.Length; i++)
            {
                if (reader.Remaining < 10)
                {
                    if (i == 0 || !monochrome)
                    {
                        return null;
                    }

                    records[i] = records[0];
                    continue;
                }

                var red = reader.ReadInt16();
                var green = reader.ReadInt16();
                var blue = reader.ReadInt16();
                reader.Skip(2);
                var constant = reader.ReadInt16();
                records[i] = new MixerChannel(
                    (int)Compat.Clamp((int)red, -200, 200),
                    (int)Compat.Clamp((int)green, -200, 200),
                    (int)Compat.Clamp((int)blue, -200, 200),
                    (int)Compat.Clamp((int)constant, -200, 200));
            }

            return monochrome
                ? new ChannelMixerSettings(true, records[0], records[0], records[0])
                : new ChannelMixerSettings(false, records[0], records[1], records[2]);
        }

        /// <summary>blwh: a version-16 descriptor with the six range percents, useTint and tintColor.</summary>
        internal static BlackWhiteSettings ParseBlackWhite(byte[] data, DocumentColors colors = null)
        {
            var descriptor = ReadVersionedDescriptor(data);
            if (descriptor == null)
            {
                return null;
            }

            var fallback = BlackWhiteSettings.Default;
            var tintColor = descriptor.GetColor("tintColor");
            var tint = descriptor.GetBoolean("useTint") && tintColor.HasValue;
            return new BlackWhiteSettings(
                PercentOf(descriptor, "Rd  ", fallback.Reds),
                PercentOf(descriptor, "Yllw", fallback.Yellows),
                PercentOf(descriptor, "Grn ", fallback.Greens),
                PercentOf(descriptor, "Cyn ", fallback.Cyans),
                PercentOf(descriptor, "Bl  ", fallback.Blues),
                PercentOf(descriptor, "Mgnt", fallback.Magentas),
                tint,
                tint ? tintColor.Value : default(PsdColor));
        }

        private static int PercentOf(Descriptor descriptor, string key, int fallback)
        {
            return (int)Compat.Clamp(Math.Round(descriptor.GetNumber(key, fallback)), -200, 300);
        }

        /// <summary>selc: u16 version 1, u16 method, then ten records of four i16 percents.</summary>
        internal static SelectiveColorSettings ParseSelectiveColor(byte[] data)
        {
            var reader = new BigEndianReader(data);
            if (reader.Remaining < 84 || reader.ReadUInt16() != 1)
            {
                return null;
            }

            var absolute = reader.ReadUInt16() == 1;
            reader.Skip(8);
            var ranges = new SelectiveColorRange[9];
            for (var i = 0; i < ranges.Length; i++)
            {
                ranges[i] = new SelectiveColorRange(
                    (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100),
                    (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100),
                    (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100),
                    (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100));
            }

            return new SelectiveColorSettings(absolute, ranges);
        }

        /// <summary>phfl: version 3 stores Lab i32 values times 100; version 2 a binary color.</summary>
        internal static PhotoFilterSettings? ParsePhotoFilter(byte[] data, DocumentColors colors = null)
        {
            var reader = new BigEndianReader(data);
            if (reader.Remaining < 2)
            {
                return null;
            }

            var version = reader.ReadUInt16();
            ColorF color;
            if (version == 3 && reader.Remaining >= 17)
            {
                var l = reader.ReadInt32() / 100.0;
                var a = reader.ReadInt32() / 100.0;
                var b = reader.ReadInt32() / 100.0;
                double r, g, bl;
                ColorSpaces.LabToSrgb(Compat.Clamp(l, 0, 100), Compat.Clamp(a, -128, 127), Compat.Clamp(b, -128, 127), out r, out g, out bl);
                color = new ColorF(r, g, bl);
            }
            else if (version == 2 && reader.Remaining >= 15)
            {
                var space = reader.ReadUInt16();
                var components = new ushort[4];
                for (var i = 0; i < 4; i++)
                {
                    components[i] = reader.ReadUInt16();
                }

                var binary = BinaryColor(space, components);
                if (!binary.HasValue)
                {
                    return null;
                }

                color = binary.Value;
            }
            else
            {
                return null;
            }

            var density = (int)Math.Min(reader.ReadUInt32(), 100u);
            var preserve = reader.ReadByte() != 0;
            return new PhotoFilterSettings(
                Compat.Clamp(color.R, 0, 1),
                Compat.Clamp(color.G, 0, 1),
                Compat.Clamp(color.B, 0, 1),
                density,
                preserve);
        }

        /// <summary>
        /// grdm: u16 version (1, or 3 with a four-byte interpolation key after the
        /// flags: Gcls, Perc, Lnr ), reverse and dither bytes, the gradient name, color
        /// stops (i32 location 0..4096, i32 midpoint percent, binary color, u16 stop type),
        /// transparency stops (i32, i32, u16 opacity 0..255), then expansion count,
        /// smoothness (0..4096), length, form (1 = noise), u32 seed, transparency and
        /// vector-color flags, u32 roughness, u16 color model and the four noise minimums
        /// and maximums on a 0..32768 scale.
        /// </summary>
        internal static (Gradient Gradient, bool Reverse)? ParseGradientMap(byte[] data)
        {
            try
            {
                return ReadGradientMap(new BigEndianReader(data));
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        private static (Gradient Gradient, bool Reverse)? ReadGradientMap(BigEndianReader reader)
        {
            if (reader.Remaining < 4)
            {
                return null;
            }

            var version = reader.ReadUInt16();
            if (version != 1 && version != 3)
            {
                return null;
            }

            var reverse = reader.ReadByte() != 0;
            reader.Skip(1); // dither: not modeled
            var interpolation = PsdGradientInterpolation.Classic;
            if (version == 3)
            {
                var signature = reader.ReadSignature();
                interpolation =
                    signature == "Perc" ? PsdGradientInterpolation.Perceptual :
                    signature == "Lnr " ? PsdGradientInterpolation.Linear : PsdGradientInterpolation.Classic;
            }

            reader.ReadUnicodeString();
            var colorCount = reader.ReadUInt16();
            var colors = new List<PsdGradientColorStop>(colorCount);
            var components = new ushort[4];
            for (var i = 0; i < colorCount; i++)
            {
                var location = reader.ReadInt32();
                var midpoint = reader.ReadInt32();
                var space = reader.ReadUInt16();
                for (var c = 0; c < 4; c++)
                {
                    components[c] = reader.ReadUInt16();
                }

                reader.Skip(2);
                var color = BinaryColor(space, components) ?? new ColorF(0, 0, 0);
                colors.Add(new PsdGradientColorStop(
                    Compat.Clamp(location / 4096f, 0f, 1f),
                    new PsdColor(RoundByte(color.R * 255), RoundByte(color.G * 255), RoundByte(color.B * 255)),
                    Compat.Clamp(midpoint / 100f, 0f, 1f)));
            }

            var alphaCount = reader.ReadUInt16();
            var alphas = new List<PsdGradientAlphaStop>(alphaCount);
            for (var i = 0; i < alphaCount; i++)
            {
                var location = reader.ReadInt32();
                var midpoint = reader.ReadInt32();
                var opacity = reader.ReadUInt16();
                alphas.Add(new PsdGradientAlphaStop(
                    Compat.Clamp(location / 4096f, 0f, 1f),
                    Compat.Clamp(opacity / 255f, 0f, 1f),
                    Compat.Clamp(midpoint / 100f, 0f, 1f)));
            }

            var smoothness = 4096;
            PsdGradientNoise noise = null;
            if (reader.Remaining >= 8)
            {
                reader.Skip(2);
                smoothness = Math.Min((int)reader.ReadUInt16(), 4096);
                reader.Skip(2);
                var form = reader.ReadUInt16();
                if (form == 1 && reader.Remaining >= 30)
                {
                    var seed = reader.ReadUInt32();
                    var transparency = reader.ReadUInt16() != 0;
                    reader.Skip(2);
                    var roughness = (int)Math.Min(reader.ReadUInt32(), 4096u);
                    var modelCode = reader.ReadUInt16();
                    var model = modelCode == 4 ? PsdGradientNoiseModel.Hsb
                        : modelCode == 6 ? PsdGradientNoiseModel.Lab
                        : PsdGradientNoiseModel.Rgb;
                    var minimum = new double[4];
                    var maximum = new double[4];
                    for (var c = 0; c < 4; c++)
                    {
                        minimum[c] = Math.Round(reader.ReadUInt16() * 100.0 / 32768.0);
                    }

                    for (var c = 0; c < 4; c++)
                    {
                        maximum[c] = Math.Round(reader.ReadUInt16() * 100.0 / 32768.0);
                    }

                    noise = new PsdGradientNoise(seed, roughness, transparency, model, minimum, maximum);
                }
            }

            if (noise == null && colors.Count == 0)
            {
                return null;
            }

            var gradient = Gradient.FromStops(
                colors.OrderBy(s => s.Location).ToArray(),
                alphas.OrderBy(s => s.Location).ToArray(),
                smoothness / 4096f,
                interpolation,
                noise);
            return (gradient, reverse);
        }

        /// <summary>
        /// The Gradient Map ramp: luminosity 0..255 mapped along the gradient (reversed when
        /// the layer says so), with the eased segment interpolation of gradient fill layers.
        /// </summary>
        internal static ChannelLuts BuildGradientMapLut(Gradient gradient, bool reverse)
        {
            var red = new byte[256];
            var green = new byte[256];
            var blue = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                var position = v / 255f;
                float r, g, b;
                gradient.Color(reverse ? 1f - position : position, true, out r, out g, out b);
                red[v] = ClampByte(r * 255f);
                green[v] = ClampByte(g * 255f);
                blue[v] = ClampByte(b * 255f);
            }

            return new ChannelLuts(red, green, blue);
        }

        /// <summary>vibA: a version-16 descriptor with optional vibrance and Strt integers.</summary>
        internal static VibranceSettings? ParseVibrance(byte[] data)
        {
            var descriptor = ReadVersionedDescriptor(data);
            if (descriptor == null)
            {
                return null;
            }

            var vibrance = descriptor.GetNumber("vibrance", 0);
            var saturation = descriptor.GetNumber("Strt", 0);
            return new VibranceSettings(
                (int)Compat.Clamp(Math.Round(vibrance), -100, 100),
                (int)Compat.Clamp(Math.Round(saturation), -100, 100));
        }

        /// <summary>A Photoshop binary color (u16 space and four u16 components) as sRGB in [0,1].</summary>
        internal static ColorF? BinaryColor(int space, ushort[] c)
        {
            switch (space)
            {
                case 0:
                    return new ColorF(c[0] / 65535.0, c[1] / 65535.0, c[2] / 65535.0);
                case 1:
                    return HsbToRgb(c[0] / 65536.0, c[1] / 65535.0, c[2] / 65535.0);
                case 2:
                    // Components store 65535 - ink.
                    return new ColorF(
                        c[0] / 65535.0 * (c[3] / 65535.0),
                        c[1] / 65535.0 * (c[3] / 65535.0),
                        c[2] / 65535.0 * (c[3] / 65535.0));
                case 7:
                    {
                        double r, g, b;
                        ColorSpaces.LabToSrgb(c[0] / 100.0, (short)c[1] / 100.0, (short)c[2] / 100.0, out r, out g, out b);
                        return new ColorF(Compat.Clamp(r, 0, 1), Compat.Clamp(g, 0, 1), Compat.Clamp(b, 0, 1));
                    }

                case 8:
                    {
                        // Gray components are 0..10000 of black ink.
                        var v = 1.0 - Compat.Clamp(c[0] / 10000.0, 0, 1);
                        return new ColorF(v, v, v);
                    }

                default:
                    return null;
            }
        }

        private static ColorF HsbToRgb(double h, double s, double v)
        {
            h = (h - Math.Floor(h)) * 6;
            var i = (int)Math.Floor(h) % 6;
            var f = h - Math.Floor(h);
            var p = v * (1 - s);
            var q = v * (1 - (s * f));
            var t = v * (1 - (s * (1 - f)));
            switch (i)
            {
                case 0: return new ColorF(v, t, p);
                case 1: return new ColorF(q, v, p);
                case 2: return new ColorF(p, v, t);
                case 3: return new ColorF(p, q, v);
                case 4: return new ColorF(t, p, v);
                default: return new ColorF(v, p, q);
            }
        }

        private static Descriptor ReadVersionedDescriptor(byte[] data, DocumentColors colors = null)
        {
            if (data == null || data.Length < 4)
            {
                return null;
            }

            return PsdParser.TryReadDescriptor(data, 0, colors);
        }

        // ---- Math -----------------------------------------------------------------------

        /// <summary>Channel Mixer: (R*r + G*g + B*b)/100 + constant * 2.55, rounded.</summary>
        internal static Color8 ChannelMixerValue(int red, int green, int blue, ChannelMixerSettings settings)
        {
            return new Color8(
                Mix(settings.Red, red, green, blue),
                Mix(settings.Green, red, green, blue),
                Mix(settings.Blue, red, green, blue));
        }

        private static int Mix(MixerChannel c, int r, int g, int b)
        {
            return RoundByte(((r * c.Red) + (g * c.Green) + (b * c.Blue)) / 100.0 + (c.Constant * 2.55));
        }

        /// <summary>Black and White: min + (mid - min) * secondary + (max - mid) * primary.</summary>
        internal static Color8 BlackWhiteValue(int red, int green, int blue, BlackWhiteSettings settings)
        {
            var max = Math.Max(red, Math.Max(green, blue));
            var min = Math.Min(red, Math.Min(green, blue));
            var mid = red + green + blue - max - min;
            // Ties pick any matching range: the difference it weights is zero.
            var primary = max == red ? settings.Reds : max == green ? settings.Greens : settings.Blues;
            var secondary = min == blue ? settings.Yellows : min == red ? settings.Cyans : settings.Magentas;
            var gray = min + ((mid - min) * secondary / 100.0) + ((max - mid) * primary / 100.0);
            var value = RoundByte(gray);
            if (!settings.HasTint)
            {
                return new Color8(value, value, value);
            }

            var tinted = SetLuminosity(settings.Tint.R, settings.Tint.G, settings.Tint.B, value);
            return new Color8(RoundByte(tinted.R), RoundByte(tinted.G), RoundByte(tinted.B));
        }

        /// <summary>Photoshop's non-separable SetLum/ClipColor on 0..255 values.</summary>
        internal static ColorF SetLuminosity(double red, double green, double blue, double luminosity)
        {
            var delta = luminosity - Luminosity(red, green, blue);
            var r = red + delta;
            var g = green + delta;
            var b = blue + delta;
            var l = Luminosity(r, g, b);
            var n = Math.Min(r, Math.Min(g, b));
            var x = Math.Max(r, Math.Max(g, b));
            if (n < 0 && l - n > 1e-9)
            {
                r = l + ((r - l) * l / (l - n));
                g = l + ((g - l) * l / (l - n));
                b = l + ((b - l) * l / (l - n));
            }

            if (x > 255 && x - l > 1e-9)
            {
                r = l + ((r - l) * (255 - l) / (x - l));
                g = l + ((g - l) * (255 - l) / (x - l));
                b = l + ((b - l) * (255 - l) / (x - l));
            }

            return new ColorF(r, g, b);
        }

        private static double Luminosity(double r, double g, double b)
        {
            return (0.3 * r) + (0.59 * g) + (0.11 * b);
        }

        /// <summary>Selective Color, range weights by channel order and ink amounts.</summary>
        internal static Color8 SelectiveColorValue(int red, int green, int blue, SelectiveColorSettings settings)
        {
            var rgb = new[] { red, green, blue };
            var max = Math.Max(red, Math.Max(green, blue));
            var min = Math.Min(red, Math.Min(green, blue));
            var mid = red + green + blue - max - min;
            var weights = new double[9];
            if (max != min)
            {
                // Reds 0, Yellows 1, Greens 2, Cyans 3, Blues 4, Magentas 5.
                var top = max == red ? 0 : max == green ? 1 : 2;
                var bottom = min == blue ? 2 : min == red ? 0 : 1;
                weights[top * 2] = (max - mid) / 255.0;
                weights[bottom == 2 ? 1 : bottom == 0 ? 3 : 5] = (mid - min) / 255.0;
            }

            weights[6] = min > 128 ? (min - 128) * 2 / 255.0 : 0;
            weights[7] = Math.Max(0, 255 - Math.Abs(max - 128) - Math.Abs(min - 128)) / 255.0;
            weights[8] = max < 128 ? (128 - max) * 2 / 255.0 : 0;

            var result = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var v = rgb[c] / 255.0;
                var delta = 0.0;
                for (var i = 0; i < 9; i++)
                {
                    var range = settings.Ranges[i];
                    if (weights[i] <= 0 || !range.HasEffect)
                    {
                        continue;
                    }

                    var ink = (c == 0 ? range.Cyan : c == 1 ? range.Magenta : range.Yellow) / 100.0;
                    var black = range.Black / 100.0;
                    var change = ((-1 - ink) * black) - ink;
                    if (!settings.Absolute)
                    {
                        change *= 1 - v;
                    }

                    delta += Compat.Clamp(change, -v, 1 - v) * weights[i];
                }

                result[c] = (v + delta) * 255.0;
            }

            return new Color8(RoundByte(result[0]), RoundByte(result[1]), RoundByte(result[2]));
        }

        /// <summary>Photo Filter: each channel multiplied in linear light by the filter color.</summary>
        internal static Color8 PhotoFilterValue(int red, int green, int blue, PhotoFilterSettings settings)
        {
            return new PhotoFilterMap(settings).Map(red, green, blue);
        }

        /// <summary>The linear-light filter of one channel as a 256-entry table on the 0..255 scale.</summary>
        private static double[] PhotoFilterTable(double filter, double density)
        {
            var gain = 1 - density + (density * SrgbToLinear(filter));
            var table = new double[256];
            for (var v = 0; v < 256; v++)
            {
                table[v] = 255.0 * ColorSpaces.EncodeSrgb(SrgbToLinear(v / 255.0) * gain);
            }

            return table;
        }

        /// <summary>sRGB transfer function (linear segment below 0.04045).</summary>
        internal static double SrgbToLinear(double value)
        {
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        /// <summary>Vibrance: a saturation change weighted toward less saturated colors.</summary>
        internal static Color8 VibranceValue(int red, int green, int blue, VibranceSettings settings)
        {
            double r = red;
            double g = green;
            double b = blue;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (settings.Vibrance != 0 && max > 0 && max != min)
            {
                var amount = settings.Vibrance / 100.0 * (1 - ((max - min) / max));
                r += (r - max) * amount;
                g += (g - max) * amount;
                b += (b - max) * amount;
            }

            if (settings.Saturation != 0)
            {
                var l = Luminosity(r, g, b);
                var scale = 1 + (settings.Saturation / 100.0);
                r = l + ((r - l) * scale);
                g = l + ((g - l) * scale);
                b = l + ((b - l) * scale);
            }

            return new Color8(RoundByte(r), RoundByte(g), RoundByte(b));
        }

        /// <summary>A per-pixel color map on bytes.</summary>
        internal interface IPixelMap
        {
            Color8 Map(int red, int green, int blue);
        }

        /// <summary>Runs a struct map over a row; flat areas repeat colors, so the last mapping is remembered.</summary>
        internal sealed class PixelAdjustment<TMap> : IAdjustment
            where TMap : struct, IPixelMap
        {
            private readonly TMap _map;

            public PixelAdjustment(TMap map)
            {
                _map = map;
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                var lastIn = -1;
                var lastR = 0f;
                var lastG = 0f;
                var lastB = 0f;
                for (var i = 0; i < r.Length; i++)
                {
                    var red = Byte(r[i]);
                    var green = Byte(g[i]);
                    var blue = Byte(b[i]);
                    var key = (red << 16) | (green << 8) | blue;
                    if (key != lastIn)
                    {
                        var mapped = _map.Map(red, green, blue);
                        lastR = mapped.R / 255f;
                        lastG = mapped.G / 255f;
                        lastB = mapped.B / 255f;
                        lastIn = key;
                    }

                    r[i] = lastR;
                    g[i] = lastG;
                    b[i] = lastB;
                }
            }
        }

        internal struct ChannelMixerMap : IPixelMap
        {
            private readonly ChannelMixerSettings _settings;

            public ChannelMixerMap(ChannelMixerSettings settings)
            {
                _settings = settings;
            }

            public Color8 Map(int red, int green, int blue)
            {
                return ChannelMixerValue(red, green, blue, _settings);
            }
        }

        internal struct BlackWhiteMap : IPixelMap
        {
            private readonly BlackWhiteSettings _settings;

            public BlackWhiteMap(BlackWhiteSettings settings)
            {
                _settings = settings;
            }

            public Color8 Map(int red, int green, int blue)
            {
                return BlackWhiteValue(red, green, blue, _settings);
            }
        }

        internal struct PhotoFilterMap : IPixelMap
        {
            private readonly double[] _red;
            private readonly double[] _green;
            private readonly double[] _blue;
            private readonly bool _preserve;

            public PhotoFilterMap(PhotoFilterSettings settings)
            {
                _red = PhotoFilterTable(settings.Red, settings.Density / 100.0);
                _green = PhotoFilterTable(settings.Green, settings.Density / 100.0);
                _blue = PhotoFilterTable(settings.Blue, settings.Density / 100.0);
                _preserve = settings.PreserveLuminosity;
            }

            public Color8 Map(int red, int green, int blue)
            {
                var r = _red[red];
                var g = _green[green];
                var b = _blue[blue];
                if (_preserve)
                {
                    var preserved = SetLuminosity(r, g, b, Luminosity(red, green, blue));
                    r = preserved.R;
                    g = preserved.G;
                    b = preserved.B;
                }

                return new Color8(RoundByte(r), RoundByte(g), RoundByte(b));
            }
        }

        internal struct VibranceMap : IPixelMap
        {
            private readonly VibranceSettings _settings;

            public VibranceMap(VibranceSettings settings)
            {
                _settings = settings;
            }

            public Color8 Map(int red, int green, int blue)
            {
                return VibranceValue(red, green, blue, _settings);
            }
        }

        /// <summary>Gradient Map: the pixel's 30/59/11 luminosity picks a color from a 256-entry ramp.</summary>
        internal struct GradientMapMap : IPixelMap
        {
            private readonly byte[] _red;
            private readonly byte[] _green;
            private readonly byte[] _blue;

            public GradientMapMap(ChannelLuts luts)
            {
                _red = luts.Red;
                _green = luts.Green;
                _blue = luts.Blue;
            }

            public Color8 Map(int r, int g, int b)
            {
                var index = ThresholdLuminance(r, g, b);
                return new Color8(_red[index], _green[index], _blue[index]);
            }
        }
    }
}
