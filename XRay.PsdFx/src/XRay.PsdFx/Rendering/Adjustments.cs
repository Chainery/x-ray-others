using System;

namespace XRay.PsdFx
{
    /// <summary>A color transform applied by an adjustment layer to the backdrop.</summary>
    internal interface IAdjustment
    {
        void Apply(float[] r, float[] g, float[] b);
    }

    /// <summary>
    /// Factory for the adjustment layers the renderer supports. Every transform runs on
    /// 8-bit values, as the reference compositor does: the float backdrop is quantized to a
    /// byte, mapped through the calibrated Photoshop math, and returned as byte / 255.
    /// Payload parsing follows .reference/src/psd/psd_adjustments.cpp; the math follows
    /// .reference/src/core/adjustment_layer.cpp and .reference/docs/adjustments-calibration.md.
    /// </summary>
    /// <remarks>
    /// Beyond the reference: Color Balance (fitted to Photoshop captures), Vibrance, Black and
    /// White, Channel Mixer, Photo Filter and Gradient Map (Adjustments.Color.cs). Selective
    /// Color is parsed but not rendered. Color Lookup (Adjustments.ColorLookup.cs, off by
    /// default) and the ink-space path of CMYK, grayscale and duotone documents
    /// (Adjustments.Inks.cs through IccInkSpace) are ported.
    /// </remarks>
    internal static partial class Adjustments
    {
        /// <summary>
        /// The transform of an adjustment layer, or null when it has none the renderer models
        /// (or its payload is malformed). <paramref name="colorLookup"/> turns on Color Lookup
        /// layers (<see cref="RenderOptions.RenderColorLookup"/>, off by default).
        /// </summary>
        public static IAdjustment Create(PsdLayer layer, bool colorLookup = false)
        {
            var key = layer.ContentKey;
            if (key == null)
            {
                return null;
            }

            var data = layer.GetTaggedBlock(key) ?? new byte[0];
            try
            {
                // CMYK, gray and duotone documents evaluate the channel-wise kinds on their own channels.
                var inkSpace = layer.Document.InkSpace;
                if (inkSpace != null && RunsOnInks(key, layer.Document.ColorMode))
                {
                    return CreateInkAdjustment(layer, key, data, inkSpace);
                }

                switch (key)
                {
                    case "nvrt":
                        return new InvertAdjustment();
                    case "thrs":
                        {
                            var level = ParseThreshold(data);
                            return level.HasValue ? new ThresholdAdjustment(level.Value, layer.Document.Depth == 16) : null;
                        }

                    case "post":
                        {
                            var levels = ParsePosterize(data);
                            if (!levels.HasValue)
                            {
                                return null;
                            }

                            return layer.Document.Depth == 16
                                ? (IAdjustment)new Posterize16Adjustment(levels.Value)
                                : LutAdjustment.Uniform(PosterizeLut(levels.Value));
                        }

                    case "levl":
                        {
                            var settings = ParseLevels(data);
                            return settings != null ? LutAdjustment.From(BuildLevelsLut(ForDocument(layer, settings))) : null;
                        }

                    case "curv":
                        {
                            var curves = ParseCurves(data);
                            return curves != null ? LutAdjustment.From(BuildCurvesLut(ForDocument(layer, curves))) : null;
                        }

                    case "hue2":
                    case "hue ":
                        {
                            var hue = ParseHueSaturation(data);
                            return hue != null ? new HueSaturationAdjustment(hue) : null;
                        }

                    case "blnc":
                        {
                            var balance = ParseColorBalance(data);
                            return balance.HasValue ? LutAdjustment.From(BuildColorBalanceLut(balance.Value)) : null;
                        }

                    case "brit":
                        {
                            var bc = ResolveBrightnessContrast(layer, data);
                            return bc.HasValue ? LutAdjustment.Uniform(BuildBrightnessContrastLut(bc.Value)) : null;
                        }

                    case "expA":
                        {
                            var exposure = ParseExposure(data);
                            return exposure.HasValue ? LutAdjustment.Uniform(BuildExposureLut(exposure.Value)) : null;
                        }

                    case "mixr":
                        {
                            var mixer = ParseChannelMixer(data);
                            return mixer != null ? new PixelAdjustment<ChannelMixerMap>(new ChannelMixerMap(mixer)) : null;
                        }

                    case "blwh":
                        {
                            var blackWhite = ParseBlackWhite(data, layer.Document.Colors);
                            return blackWhite != null ? new PixelAdjustment<BlackWhiteMap>(new BlackWhiteMap(blackWhite)) : null;
                        }

                    // Selective Color stays unrendered: its model has no Photoshop capture to
                    // measure it against (docs/adjustments.md).
                    case "selc":
                        return null;
                    case "grdm":
                        {
                            var map = ParseGradientMap(data);
                            return map.HasValue ? GradientMapAdjustment(map.Value.Gradient, map.Value.Reverse) : null;
                        }

                    case "phfl":
                        {
                            var filter = ParsePhotoFilter(data, layer.Document.Colors);
                            return filter.HasValue ? new PixelAdjustment<PhotoFilterMap>(new PhotoFilterMap(filter.Value)) : null;
                        }

                    case "vibA":
                        {
                            var vibrance = ParseVibrance(data);
                            return vibrance.HasValue ? new PixelAdjustment<VibranceMap>(new VibranceMap(vibrance.Value)) : null;
                        }

                    case "clrL":
                        {
                            var lookup = colorLookup ? ParseColorLookup(data) : null;
                            return lookup != null ? new ColorLookupAdjustment(lookup) : null;
                        }

                    default:
                        return null;
                }
            }
            catch (PsdFormatException)
            {
                // A malformed payload stays unrendered instead of guessing settings.
                return null;
            }
        }

        private static IAdjustment GradientMapAdjustment(Gradient gradient, bool reverse)
        {
            return new PixelAdjustment<GradientMapMap>(new GradientMapMap(BuildGradientMapLut(gradient, reverse)));
        }

        /// <summary>
        /// One-plane documents keep their Levels record and curve in the slot RGB documents
        /// call red; the reader copies it to green and blue because the plane becomes R = G = B.
        /// </summary>
        private static bool IsOnePlane(PsdLayer layer)
        {
            var mode = layer.Document.ColorMode;
            return mode == PsdColorMode.Grayscale || mode == PsdColorMode.Duotone || mode == PsdColorMode.Bitmap || mode == PsdColorMode.Indexed;
        }

        private static LevelsSettings ForDocument(PsdLayer layer, LevelsSettings settings)
        {
            return IsOnePlane(layer) ? settings.WithRedAsAll() : settings;
        }

        private static CurvesSettings ForDocument(PsdLayer layer, CurvesSettings settings)
        {
            return IsOnePlane(layer) ? settings.WithRedAsAll() : settings;
        }

        /// <summary>The reference's clamp_byte: lround of a float, clamped to a byte.</summary>
        internal static byte ClampByte(float value)
        {
            return (byte)Compat.Clamp(Math.Round((double)value, MidpointRounding.AwayFromZero), 0, 255);
        }

        /// <summary>std::lround of a double, clamped to a byte.</summary>
        internal static byte RoundByte(double value)
        {
            return (byte)Compat.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        }

        internal static int Byte(float value)
        {
            return (int)Compat.Clamp(MathF.Round(value * 255f), 0f, 255f);
        }

        /// <summary>Quantizes a row to byte indices (round to nearest, clamped).</summary>
        private static void Quantize(float[] values, int[] bytes)
        {
            for (var i = 0; i < values.Length; i++)
            {
                bytes[i] = Byte(values[i]);
            }
        }

        private sealed class InvertAdjustment : IAdjustment
        {
            public void Apply(float[] r, float[] g, float[] b)
            {
                Invert(r);
                Invert(g);
                Invert(b);
            }

            private static void Invert(float[] values)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = 1f - values[i];
                }
            }
        }

        /// <summary>
        /// Photoshop's Threshold: the 30/59/11 luma against the level. 8-bit documents round the
        /// integer luma; 16-bit documents compare the luma at full precision.
        /// </summary>
        private sealed class ThresholdAdjustment : IAdjustment
        {
            private readonly int _level;
            private readonly bool _sixteenBit;

            public ThresholdAdjustment(int level, bool sixteenBit)
            {
                _level = level;
                _sixteenBit = sixteenBit;
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                for (var i = 0; i < r.Length; i++)
                {
                    var white = _sixteenBit
                        ? ((0.3 * r[i]) + (0.59 * g[i]) + (0.11 * b[i])) * 255.0 >= _level
                        : ThresholdLevel(Byte(r[i]), Byte(g[i]), Byte(b[i])) >= _level;
                    r[i] = white ? 1f : 0f;
                    g[i] = r[i];
                    b[i] = r[i];
                }
            }
        }

        /// <summary>The reference's threshold_luminance: integer 30/59/11 luma, truncated.</summary>
        internal static int ThresholdLuminance(int red, int green, int blue)
        {
            return ((red * 30) + (green * 59) + (blue * 11)) / 100;
        }

        /// <summary>The luma Threshold compares with its level: 30/59/11, rounded.</summary>
        internal static int ThresholdLevel(int red, int green, int blue)
        {
            return ((red * 30) + (green * 59) + (blue * 11) + 50) / 100;
        }

        /// <summary>Photoshop's Posterize: equal-width buckets mapped to the truncated ramp.</summary>
        internal static byte PosterizeValue(int value, int levels)
        {
            levels = (int)Compat.Clamp(levels, 2, 255);
            var bucket = value * levels / 256;
            return (byte)(bucket * 255 / (levels - 1));
        }

        /// <summary>Posterize at Photoshop's 16-bit precision (0..32768).</summary>
        internal static float Posterize16Value(float value, int levels)
        {
            levels = (int)Compat.Clamp(levels, 2, 255);
            var sample = Math.Round(Compat.Clamp(value, 0f, 1f) * 32768.0);
            var bucket = Math.Min(Math.Floor(sample * levels / 32768.0), levels - 1);
            return (float)(Math.Floor(bucket * 32768.0 / (levels - 1)) / 32768.0);
        }

        internal static byte[] PosterizeLut(int levels)
        {
            var lut = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                lut[v] = PosterizeValue(v, levels);
            }

            return lut;
        }

        /// <summary>Posterize of a 16-bit RGB document.</summary>
        private sealed class Posterize16Adjustment : IAdjustment
        {
            private readonly int _levels;

            public Posterize16Adjustment(int levels)
            {
                _levels = levels;
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                Posterize(r);
                Posterize(g);
                Posterize(b);
            }

            private void Posterize(float[] values)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = Posterize16Value(values[i], _levels);
                }
            }
        }

        /// <summary>A channel-separable adjustment as three exact 256-entry tables.</summary>
        internal sealed class LutAdjustment : IAdjustment
        {
            private readonly float[] _red;
            private readonly float[] _green;
            private readonly float[] _blue;

            private LutAdjustment(byte[] red, byte[] green, byte[] blue)
            {
                _red = ToUnit(red);
                _green = ReferenceEquals(green, red) ? _red : ToUnit(green);
                _blue = ReferenceEquals(blue, red) ? _red : ReferenceEquals(blue, green) ? _green : ToUnit(blue);
            }

            public static LutAdjustment Uniform(byte[] lut)
            {
                return new LutAdjustment(lut, lut, lut);
            }

            public static LutAdjustment From(ChannelLuts luts)
            {
                return new LutAdjustment(luts.Red, luts.Green, luts.Blue);
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                var indices = new int[r.Length];
                Map(r, _red, indices);
                Map(g, _green, indices);
                Map(b, _blue, indices);
            }

            private static void Map(float[] values, float[] table, int[] indices)
            {
                Quantize(values, indices);
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = table[indices[i] & 0xFF];
                }
            }

            private static float[] ToUnit(byte[] lut)
            {
                var table = new float[256];
                for (var i = 0; i < 256; i++)
                {
                    table[i] = lut[i] / 255f;
                }

                return table;
            }
        }
    }

    /// <summary>Per-channel 256-entry transfer tables.</summary>
    internal sealed class ChannelLuts
    {
        public ChannelLuts(byte[] red, byte[] green, byte[] blue)
        {
            Red = red;
            Green = green;
            Blue = blue;
        }

        public byte[] Red { get; private set; }

        public byte[] Green { get; private set; }

        public byte[] Blue { get; private set; }
    }

    /// <summary>A byte color (0..255 per channel).</summary>
    internal struct Color8
    {
        public Color8(int r, int g, int b)
        {
            R = r;
            G = g;
            B = b;
        }

        public int R;
        public int G;
        public int B;
    }

    /// <summary>A real color (0..1 per channel, or 0..255 where noted).</summary>
    internal struct ColorF
    {
        public ColorF(double r, double g, double b)
        {
            R = r;
            G = g;
            B = b;
        }

        public double R;
        public double G;
        public double B;
    }
}
