using System;

namespace XRay.PsdFx
{
    /// <summary>One Levels record: black/white input, gamma percent, black/white output.</summary>
    internal struct LevelsRecord
    {
        public LevelsRecord(int blackInput, int whiteInput, int gammaPercent, int blackOutput, int whiteOutput)
        {
            BlackInput = blackInput;
            WhiteInput = whiteInput;
            GammaPercent = gammaPercent;
            BlackOutput = blackOutput;
            WhiteOutput = whiteOutput;
        }

        public static LevelsRecord Identity
        {
            get { return new LevelsRecord(0, 255, 100, 0, 255); }
        }

        public int BlackInput;
        public int WhiteInput;
        public int GammaPercent;
        public int BlackOutput;
        public int WhiteOutput;

        /// <summary>clamp_levels_record: the ranges the dialogs and the codec share.</summary>
        public LevelsRecord Clamped()
        {
            var blackInput = (int)Compat.Clamp(BlackInput, 0, 254);
            var blackOutput = (int)Compat.Clamp(BlackOutput, 0, 255);
            return new LevelsRecord(
                blackInput,
                (int)Compat.Clamp(WhiteInput, blackInput + 1, 255),
                (int)Compat.Clamp(GammaPercent, 10, 999),
                blackOutput,
                (int)Compat.Clamp(WhiteOutput, blackOutput, 255));
        }

        public bool HasEffect
        {
            get
            {
                var r = Clamped();
                return r.BlackInput != 0 || r.WhiteInput != 255 || r.GammaPercent != 100 || r.BlackOutput != 0 || r.WhiteOutput != 255;
            }
        }
    }

    /// <summary>Composite (master) and per-channel Levels records.</summary>
    internal sealed class LevelsSettings
    {
        public LevelsSettings(LevelsRecord master, LevelsRecord red, LevelsRecord green, LevelsRecord blue)
        {
            Master = master;
            Red = red;
            Green = green;
            Blue = blue;
            BlackInk = LevelsRecord.Identity;
        }

        public static LevelsSettings Identity
        {
            get { return new LevelsSettings(LevelsRecord.Identity, LevelsRecord.Identity, LevelsRecord.Identity, LevelsRecord.Identity); }
        }

        public LevelsRecord Master { get; private set; }

        public LevelsRecord Red { get; private set; }

        public LevelsRecord Green { get; private set; }

        public LevelsRecord Blue { get; private set; }

        /// <summary>A CMYK document's black ink record (the fifth); only the ink-space path reads it.</summary>
        public LevelsRecord BlackInk { get; set; }

        /// <summary>One-plane documents: the red record stands in for all three.</summary>
        public LevelsSettings WithRedAsAll()
        {
            var copy = new LevelsSettings(Master, Red, Red, Red) { BlackInk = BlackInk };
            return copy;
        }

        /// <summary>A copy whose red record is the given one (the ink-space black stage).</summary>
        public LevelsSettings WithRed(LevelsRecord red)
        {
            return new LevelsSettings(Master, red, Green, Blue) { BlackInk = BlackInk };
        }
    }

    /// <summary>A Curves control point, 0..255 on both axes.</summary>
    internal struct CurvePoint
    {
        public CurvePoint(int input, int output)
        {
            Input = input;
            Output = output;
        }

        public int Input;
        public int Output;
    }

    /// <summary>Composite and per-channel Curves control points.</summary>
    internal sealed class CurvesSettings
    {
        public CurvesSettings(CurvePoint[] rgb, CurvePoint[] red, CurvePoint[] green, CurvePoint[] blue)
        {
            Rgb = rgb;
            Red = red;
            Green = green;
            Blue = blue;
            BlackInk = IdentityPoints;
        }

        public static CurvePoint[] IdentityPoints
        {
            get { return new[] { new CurvePoint(0, 0), new CurvePoint(255, 255) }; }
        }

        public static CurvesSettings Identity
        {
            get { return new CurvesSettings(IdentityPoints, IdentityPoints, IdentityPoints, IdentityPoints); }
        }

        public CurvePoint[] Rgb { get; private set; }

        public CurvePoint[] Red { get; private set; }

        public CurvePoint[] Green { get; private set; }

        public CurvePoint[] Blue { get; private set; }

        /// <summary>A CMYK document's black ink curve (curve index 4).</summary>
        public CurvePoint[] BlackInk { get; set; }

        public CurvesSettings WithRedAsAll()
        {
            return new CurvesSettings(Rgb, Red, Red, Red) { BlackInk = BlackInk };
        }

        public CurvesSettings WithRed(CurvePoint[] red)
        {
            return new CurvesSettings(Rgb, red, Green, Blue) { BlackInk = BlackInk };
        }
    }

    /// <summary>One of Photoshop's six per-hue-range bands.</summary>
    internal struct HueSaturationBand
    {
        public HueSaturationBand(int outerStart, int innerStart, int innerEnd, int outerEnd, int hue, int saturation, int lightness)
        {
            OuterStart = outerStart;
            InnerStart = innerStart;
            InnerEnd = innerEnd;
            OuterEnd = outerEnd;
            Hue = hue;
            Saturation = saturation;
            Lightness = lightness;
        }

        public int OuterStart;
        public int InnerStart;
        public int InnerEnd;
        public int OuterEnd;
        public int Hue;
        public int Saturation;
        public int Lightness;

        public bool HasEffect
        {
            get { return Hue != 0 || Saturation != 0 || Lightness != 0; }
        }
    }

    /// <summary>Hue/Saturation settings.</summary>
    internal sealed class HueSaturationSettings
    {
        public HueSaturationSettings()
        {
            ColorizeSaturation = 25;
            Bands = Adjustments.DefaultHueSaturationBands();
        }

        public int Hue { get; set; }

        public int Saturation { get; set; }

        public int Lightness { get; set; }

        public bool Colorize { get; set; }

        /// <summary>0..360, the model's convention (the file stores -180..180).</summary>
        public int ColorizeHue { get; set; }

        public int ColorizeSaturation { get; set; }

        public int ColorizeLightness { get; set; }

        public HueSaturationBand[] Bands { get; set; }
    }

    /// <summary>One Color Balance tone range: Cyan-Red, Magenta-Green and Yellow-Blue (-100..100).</summary>
    internal struct ColorBalanceRange
    {
        public ColorBalanceRange(int cyanRed, int magentaGreen, int yellowBlue)
        {
            CyanRed = cyanRed;
            MagentaGreen = magentaGreen;
            YellowBlue = yellowBlue;
        }

        public int CyanRed;
        public int MagentaGreen;
        public int YellowBlue;

        public int this[int channel]
        {
            get { return channel == 0 ? CyanRed : channel == 1 ? MagentaGreen : YellowBlue; }
        }
    }

    /// <summary>Color Balance: shadows, midtones and highlights plus Preserve Luminosity.</summary>
    internal struct ColorBalanceSettings
    {
        public ColorBalanceSettings(ColorBalanceRange shadows, ColorBalanceRange midtones, ColorBalanceRange highlights, bool preserveLuminosity)
        {
            Shadows = shadows;
            Midtones = midtones;
            Highlights = highlights;
            PreserveLuminosity = preserveLuminosity;
        }

        public ColorBalanceRange Shadows;
        public ColorBalanceRange Midtones;
        public ColorBalanceRange Highlights;
        public bool PreserveLuminosity;
    }

    /// <summary>Brightness/Contrast; legacy ranges -100..100, modern brightness -150..150 and contrast -50..100.</summary>
    internal struct BrightnessContrastSettings
    {
        public BrightnessContrastSettings(int brightness, int contrast, bool useLegacy)
        {
            Brightness = brightness;
            Contrast = contrast;
            UseLegacy = useLegacy;
        }

        public int Brightness;
        public int Contrast;
        public bool UseLegacy;
    }

    /// <summary>Exposure at Photoshop's field precision: stops / 100, offset / 10000, gamma / 100.</summary>
    internal struct ExposureSettings
    {
        public ExposureSettings(int exposureHundredths, int offsetTenThousandths, int gammaHundredths)
        {
            ExposureHundredths = exposureHundredths;
            OffsetTenThousandths = offsetTenThousandths;
            GammaHundredths = gammaHundredths;
        }

        public int ExposureHundredths;
        public int OffsetTenThousandths;
        public int GammaHundredths;

        public ExposureSettings Clamped()
        {
            return new ExposureSettings(
                (int)Compat.Clamp(ExposureHundredths, -2000, 2000),
                (int)Compat.Clamp(OffsetTenThousandths, -5000, 5000),
                (int)Compat.Clamp(GammaHundredths, 1, 999));
        }
    }

    /// <summary>Adjustment payload parsers, ported from .reference/src/psd/psd_adjustments.cpp.</summary>
    internal static partial class Adjustments
    {
        private const int LevelsRecordCount = 29;

        internal static int? ParseThreshold(byte[] data)
        {
            return data.Length >= 2 ? (int?)Compat.Clamp((data[0] << 8) | data[1], 1, 255) : null;
        }

        internal static int? ParsePosterize(byte[] data)
        {
            return data.Length >= 2 ? (int?)Compat.Clamp((data[0] << 8) | data[1], 2, 255) : null;
        }

        /// <summary>
        /// levl: u16 version 2, then 29 records of five u16 (black in, white in, black out,
        /// white out, gamma percent). Records 0..3 are composite, red, green, blue; record 4
        /// is a CMYK document's black ink.
        /// </summary>
        internal static LevelsSettings ParseLevels(byte[] data)
        {
            var reader = new BigEndianReader(data);
            if (reader.ReadUInt16() != 2 || reader.Remaining < LevelsRecordCount * 10)
            {
                return null;
            }

            var records = new LevelsRecord[5];
            for (var i = 0; i < records.Length; i++)
            {
                var blackInput = reader.ReadUInt16();
                var whiteInput = reader.ReadUInt16();
                var blackOutput = reader.ReadUInt16();
                var whiteOutput = reader.ReadUInt16();
                var gamma = reader.ReadUInt16();
                records[i] = new LevelsRecord(blackInput, whiteInput, gamma, blackOutput, whiteOutput).Clamped();
            }

            var settings = new LevelsSettings(records[0], records[1], records[2], records[3]);
            settings.BlackInk = records[4];
            return settings;
        }

        /// <summary>curv: one zero byte, then the ACV body.</summary>
        internal static CurvesSettings ParseCurves(byte[] data)
        {
            if (data.Length == 0 || data[0] != 0)
            {
                return null;
            }

            var body = new byte[data.Length - 1];
            Array.Copy(data, 1, body, 0, body.Length);
            return ReadAcv(body);
        }

        /// <summary>
        /// hue2 (and the Photoshop 4 "hue " key): u16 version 2, colorize u8, pad, colorize
        /// h/s/l and master h/s/l as i16, then six band records. Payloads that stop after the
        /// 16-byte header keep the default hextants.
        /// </summary>
        internal static HueSaturationSettings ParseHueSaturation(byte[] data)
        {
            var reader = new BigEndianReader(data);
            if (reader.ReadUInt16() != 2 || reader.Remaining < 14)
            {
                return null;
            }

            var colorize = reader.ReadByte() != 0;
            reader.Skip(1);
            var colorizeHue = Wrap360(reader.ReadInt16());
            var colorizeSaturation = (int)Compat.Clamp((int)reader.ReadInt16(), 0, 100);
            var colorizeLightness = (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100);
            var hue = (int)Compat.Clamp((int)reader.ReadInt16(), -180, 180);
            var saturation = (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100);
            var lightness = (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100);
            var bands = DefaultHueSaturationBands();
            if (reader.Remaining >= 14 * bands.Length)
            {
                for (var i = 0; i < bands.Length; i++)
                {
                    bands[i] = new HueSaturationBand(
                        Wrap360(reader.ReadInt16()),
                        Wrap360(reader.ReadInt16()),
                        Wrap360(reader.ReadInt16()),
                        Wrap360(reader.ReadInt16()),
                        (int)Compat.Clamp((int)reader.ReadInt16(), -180, 180),
                        (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100),
                        (int)Compat.Clamp((int)reader.ReadInt16(), -100, 100));
                }
            }

            var settings = new HueSaturationSettings();
            settings.Colorize = colorize;
            settings.ColorizeHue = colorizeHue;
            settings.ColorizeSaturation = colorizeSaturation;
            settings.ColorizeLightness = colorizeLightness;
            settings.Hue = hue;
            settings.Saturation = saturation;
            settings.Lightness = lightness;
            settings.Bands = bands;
            return settings;
        }

        /// <summary>
        /// blnc: shadows, midtones and highlights as three i16 triples plus a Preserve
        /// Luminosity byte, 20 bytes in all. A payload cut after the midtones (12 bytes)
        /// keeps zero highlights and Preserve Luminosity off.
        /// </summary>
        internal static ColorBalanceSettings? ParseColorBalance(byte[] data)
        {
            if (data.Length < 12)
            {
                return null;
            }

            return new ColorBalanceSettings(
                BalanceRange(data, 0),
                BalanceRange(data, 6),
                BalanceRange(data, 12),
                data.Length > 18 && data[18] != 0);
        }

        private static ColorBalanceRange BalanceRange(byte[] data, int offset)
        {
            return data.Length >= offset + 6
                ? new ColorBalanceRange(ReadClampedInt16(data, offset, 100), ReadClampedInt16(data, offset + 2, 100), ReadClampedInt16(data, offset + 4, 100))
                : default(ColorBalanceRange);
        }

        /// <summary>
        /// Brightness/Contrast: a parseable CgEd descriptor is authoritative; a file with brit
        /// alone is the CS-era legacy record.
        /// </summary>
        private static BrightnessContrastSettings? ResolveBrightnessContrast(PsdLayer layer, byte[] brit)
        {
            var descriptorData = layer.GetTaggedBlock("CgEd");
            if (descriptorData != null)
            {
                var modern = ParseBrightnessContrastDescriptor(descriptorData);
                if (modern.HasValue)
                {
                    return modern;
                }
            }

            return ParseBrightnessContrastLegacy(brit);
        }

        internal static BrightnessContrastSettings? ParseBrightnessContrastLegacy(byte[] data)
        {
            return data.Length >= 4
                ? new BrightnessContrastSettings(ReadClampedInt16(data, 0, 100), ReadClampedInt16(data, 2, 100), true)
                : (BrightnessContrastSettings?)null;
        }

        /// <summary>The CgEd descriptor: u32 version 16, then Brgh and Cntr integers; useLegacy defaults to false.</summary>
        internal static BrightnessContrastSettings? ParseBrightnessContrastDescriptor(byte[] data)
        {
            if (data.Length < 4)
            {
                return null;
            }

            var descriptor = PsdParser.TryReadDescriptor(data, 0);
            if (descriptor == null)
            {
                return null;
            }

            var brightness = descriptor["Brgh"];
            var contrast = descriptor["Cntr"];
            if (brightness == null || brightness.Type != DescriptorValueType.Integer || contrast == null || contrast.Type != DescriptorValueType.Integer)
            {
                return null;
            }

            var legacyValue = descriptor["useLegacy"];
            var useLegacy = legacyValue != null && legacyValue.Type == DescriptorValueType.Boolean && legacyValue.Boolean;
            var b = (int)Compat.Clamp(brightness.Integer, int.MinValue, int.MaxValue);
            var c = (int)Compat.Clamp(contrast.Integer, int.MinValue, int.MaxValue);
            return useLegacy
                ? new BrightnessContrastSettings((int)Compat.Clamp(b, -100, 100), (int)Compat.Clamp(c, -100, 100), true)
                : new BrightnessContrastSettings((int)Compat.Clamp(b, -150, 150), (int)Compat.Clamp(c, -50, 100), false);
        }

        /// <summary>expA: u16 version 1, then float32 exposure, offset and gamma.</summary>
        internal static ExposureSettings? ParseExposure(byte[] data)
        {
            if (data.Length < 14)
            {
                return null;
            }

            var reader = new BigEndianReader(data);
            if (reader.ReadUInt16() != 1)
            {
                return null;
            }

            var exposure = reader.ReadSingle();
            var offset = reader.ReadSingle();
            var gamma = reader.ReadSingle();
            if (float.IsNaN(exposure) || float.IsInfinity(exposure) || float.IsNaN(offset) || float.IsInfinity(offset) || float.IsNaN(gamma) || float.IsInfinity(gamma))
            {
                return null;
            }

            return new ExposureSettings(Scaled(exposure, 100.0, -2000, 2000), Scaled(offset, 10000.0, -5000, 5000), Scaled(gamma, 100.0, 1, 999));
        }

        private static int Scaled(float value, double scale, int low, int high)
        {
            return (int)Math.Round(Compat.Clamp(value * scale, low, high), MidpointRounding.AwayFromZero);
        }

        internal static HueSaturationBand[] DefaultHueSaturationBands()
        {
            return new[]
            {
                new HueSaturationBand(315, 345, 15, 45, 0, 0, 0),
                new HueSaturationBand(15, 45, 75, 105, 0, 0, 0),
                new HueSaturationBand(75, 105, 135, 165, 0, 0, 0),
                new HueSaturationBand(135, 165, 195, 225, 0, 0, 0),
                new HueSaturationBand(195, 225, 255, 285, 0, 0, 0),
                new HueSaturationBand(255, 285, 315, 345, 0, 0, 0),
            };
        }

        private static int Wrap360(int value)
        {
            return ((value % 360) + 360) % 360;
        }

        private static int ReadClampedInt16(byte[] data, int offset, int limit)
        {
            return (int)Compat.Clamp((int)(short)((data[offset] << 8) | data[offset + 1]), -limit, limit);
        }
    }
}
