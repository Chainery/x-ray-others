using System;
using System.Collections.Generic;
using XRay.PsdFx.Icc;

namespace XRay.PsdFx
{
    /// <summary>Settings for building an <see cref="IccSrgbTransform"/>.</summary>
    internal struct IccTransformSettings
    {
        public IccTransformSettings(IccRenderingIntent intent, bool blackPointCompensation)
        {
            Intent = intent;
            BlackPointCompensation = blackPointCompensation;
        }

        public IccRenderingIntent Intent { get; private set; }

        public bool BlackPointCompensation { get; private set; }

        /// <summary>
        /// What the reference (color_management.cpp) asks lcms2 for, and Photoshop's Convert
        /// to Profile default: relative colorimetric with black point compensation.
        /// </summary>
        public static IccTransformSettings Default
        {
            get { return new IccTransformSettings(IccRenderingIntent.RelativeColorimetric, true); }
        }
    }

    /// <summary>
    /// Converts device colors described by an ICC profile (gray, RGB or CMYK) to
    /// gamma-encoded sRGB, following Little CMS semantics without porting it: device to PCS
    /// through the intent's A2Bx LUT (falling back to A2B0) or the matrix/TRC model, PCS to
    /// XYZ (D50), black point compensation, then the Bradford-adapted sRGB matrix and
    /// transfer curve. Planar conversion uses tables built once per depth.
    /// </summary>
    internal sealed class IccSrgbTransform
    {
        private const int CmykGridPoints = 17;
        private const int RgbGridPoints = 33;
        private const int ParallelThreshold = 1 << 18;
        private const int ParallelChunk = 1 << 16;

        // v4 perceptual reference black (ICC.1:2010, Little CMS cmsPERCEPTUAL_BLACK_*).
        private static readonly IccXyz PerceptualBlack = new IccXyz(0.00336, 0.0034731, 0.00287);

        // The LUT evaluated for grids; null for gray TRC and matrix/shaper conversions.
        private IccLut _lut;

        // Matrix/shaper RGB: per-channel curves, then a 3x3 matrix and offset giving XYZ (D50).
        private readonly IccCurve[] _shaperCurves;
        private readonly double[] _shaperMatrix = new double[9];
        private readonly double[] _shaperOffset = new double[3];
        private readonly double[] _bpcScale = new[] { 1.0, 1.0, 1.0 };
        private readonly object _gate = new object();

        // Absolute colorimetric with a media white other than D50 tints even a gray TRC.
        private readonly bool _tintedGray;
        private Tables _tables8;
        private Tables _tables16;
        private Tables _tables32;

        private IccSrgbTransform(IccProfile profile, int channels, IccTransformSettings settings)
        {
            Profile = profile;
            Channels = channels;
            Settings = settings;
            _lut = profile.InputLut(settings.Intent);
            UsesLut = _lut != null;
            if (_lut == null && channels == 3)
            {
                var r = profile.RedColorant.Value;
                var g = profile.GreenColorant.Value;
                var b = profile.BlueColorant.Value;
                var colorants = new[] { r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z };
                Array.Copy(colorants, _shaperMatrix, 9);
                _shaperCurves = new[] { profile.RedTrc, profile.GreenTrc, profile.BlueTrc };
            }
            else if (_lut != null && channels == 3 && profile.Pcs == IccProfile.SpaceXyz)
            {
                IccCurve[] curves;
                double[] matrix;
                double[] offset;
                if (_lut.TryGetMatrixShaper(out curves, out matrix, out offset))
                {
                    // Evaluated exactly rather than sampled on a grid: for the Adobe RGB v4 test
                    // profile Little CMS's grid drifts up to 21 levels where colors clip at the
                    // sRGB gamut edge.
                    const double scale = 65535.0 / 32768.0;
                    for (var i = 0; i < 9; i++)
                    {
                        _shaperMatrix[i] = matrix[i] * scale;
                    }

                    for (var i = 0; i < 3; i++)
                    {
                        _shaperOffset[i] = offset[i] * scale;
                    }

                    _shaperCurves = curves;
                    _lut = null;
                }
            }

            var bpc = settings.BlackPointCompensation && settings.Intent != IccRenderingIntent.AbsoluteColorimetric;

            // Little CMS turns BPC on for v4 profiles under the perceptual and saturation intents.
            if (profile.MajorVersion >= 4
                && (settings.Intent == IccRenderingIntent.Perceptual || settings.Intent == IccRenderingIntent.Saturation))
            {
                bpc = true;
            }

            BlackPoint = bpc ? DetectBlackPoint(settings.Intent) : new IccXyz(0, 0, 0);
            if (bpc && (BlackPoint.X > 0 || BlackPoint.Y > 0 || BlackPoint.Z > 0))
            {
                // Little CMS ComputeBlackPointCompensation with a zero destination black (sRGB).
                _bpcScale[0] = IccPcs.D50.X / (IccPcs.D50.X - BlackPoint.X);
                _bpcScale[1] = IccPcs.D50.Y / (IccPcs.D50.Y - BlackPoint.Y);
                _bpcScale[2] = IccPcs.D50.Z / (IccPcs.D50.Z - BlackPoint.Z);
            }
            else
            {
                BlackPoint = new IccXyz(0, 0, 0);
            }

            if (settings.Intent == IccRenderingIntent.AbsoluteColorimetric)
            {
                // Little CMS ComputeAbsoluteIntent with a fully adapted observer: scale the
                // relative PCS by the source media white over the destination's (D50).
                var white = MediaWhite(profile);
                _bpcScale[0] *= white.X / IccPcs.D50.X;
                _bpcScale[1] *= white.Y / IccPcs.D50.Y;
                _bpcScale[2] *= white.Z / IccPcs.D50.Z;
                _tintedGray = !SameWhite(white, IccPcs.D50);
            }

            IsSrgbEquivalent = channels == 3 && CheckSrgbEquivalence();
        }

        public IccProfile Profile { get; private set; }

        /// <summary>Device channels: 1 (gray), 3 (RGB) or 4 (CMYK).</summary>
        public int Channels { get; private set; }

        public IccTransformSettings Settings { get; private set; }

        /// <summary>The source black point used for black point compensation (zero when none applies).</summary>
        public IccXyz BlackPoint { get; private set; }

        /// <summary>True when the conversion comes from an A2Bx LUT rather than the matrix/TRC tags.</summary>
        public bool UsesLut { get; private set; }

        /// <summary>True for RGB conversions that run as curves plus a matrix (matrix/TRC tags, or a CLUT-free mAB).</summary>
        public bool IsMatrixShaper
        {
            get { return _shaperCurves != null; }
        }

        /// <summary>
        /// True for RGB profiles that reproduce sRGB within one 8-bit level (the common
        /// embedded "sRGB IEC61966-2.1"); callers skip the conversion so those documents
        /// render exactly as before.
        /// </summary>
        public bool IsSrgbEquivalent { get; private set; }

        /// <summary>
        /// The transform for a document's embedded profile (resource 1039), or null when there
        /// is none, it does not fit the color mode, it cannot be used, or it is equivalent to
        /// sRGB. Gray covers grayscale and duotone; indexed palettes are RGB.
        /// </summary>
        public static IccSrgbTransform ForDocument(PsdDocument document)
        {
            int channels;
            switch (document.ColorMode)
            {
                case PsdColorMode.Rgb:
                case PsdColorMode.Indexed:
                    channels = 3;
                    break;
                case PsdColorMode.Cmyk:
                    channels = 4;
                    break;
                case PsdColorMode.Grayscale:
                case PsdColorMode.Duotone:
                    channels = 1;
                    break;
                default:
                    channels = 0;
                    break;
            }

            if (channels == 0)
            {
                return null;
            }

            var resource = document.GetImageResource(ImageResourceIds.IccProfile);
            if (resource == null)
            {
                return null;
            }

            try
            {
                var profile = IccProfile.Parse(resource.Data);
                var transform = profile == null ? null : Create(profile, channels, IccTransformSettings.Default);
                return transform == null || transform.IsSrgbEquivalent ? null : transform;
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is OverflowException)
                {
                    return null;
                }

                throw;
            }
        }

        /// <summary>
        /// Builds a transform for <paramref name="channels"/> device channels, or returns null
        /// when the profile cannot convert them (wrong color space, device link, missing tags).
        /// </summary>
        public static IccSrgbTransform Create(IccProfile profile, int channels, IccTransformSettings settings)
        {
            uint expected;
            switch (channels)
            {
                case 1:
                    expected = IccProfile.SpaceGray;
                    break;
                case 3:
                    expected = IccProfile.SpaceRgb;
                    break;
                case 4:
                    expected = IccProfile.SpaceCmyk;
                    break;
                default:
                    expected = 0;
                    break;
            }

            if (expected == 0 || profile.ColorSpace != expected)
            {
                return null;
            }

            if (profile.DeviceClass != IccProfile.ClassInput
                && profile.DeviceClass != IccProfile.ClassDisplay
                && profile.DeviceClass != IccProfile.ClassOutput
                && profile.DeviceClass != IccProfile.ClassColorSpace)
            {
                return null;
            }

            if (profile.Pcs != IccProfile.SpaceXyz && profile.Pcs != IccProfile.SpaceLab)
            {
                return null;
            }

            if (profile.InputLut(settings.Intent) == null && !(channels != 4 && profile.IsMatrixShaper))
            {
                return null;
            }

            try
            {
                return new IccSrgbTransform(profile, channels, settings);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Device values (ICC convention: CMYK is ink amount, 1 = full ink) to PCS XYZ (D50), before black point compensation.</summary>
        public IccXyz DeviceToPcs(double[] device)
        {
            return DeviceToPcs(device, _lut);
        }

        /// <summary>Device values to unclamped linear sRGB (after black point compensation).</summary>
        public void EvaluateLinear(double[] device, out double r, out double g, out double b)
        {
            var xyz = DeviceToPcs(device, _lut);
            xyz = new IccXyz(
                _bpcScale[0] * (xyz.X - BlackPoint.X),
                _bpcScale[1] * (xyz.Y - BlackPoint.Y),
                _bpcScale[2] * (xyz.Z - BlackPoint.Z));
            if (_lut == null && Channels == 1 && !_tintedGray)
            {
                // A gray TRC maps onto the neutral axis; keep it exactly neutral.
                r = xyz.Y;
                g = xyz.Y;
                b = xyz.Y;
                return;
            }

            IccPcs.XyzToLinearSrgb(xyz, out r, out g, out b);
        }

        /// <summary>One device value through the gray path, as linear sRGB.</summary>
        public IccSrgbTransformSample EvaluateLinear(double device)
        {
            double r, g, b;
            EvaluateLinear(new[] { device }, out r, out g, out b);
            return new IccSrgbTransformSample(r, g, b);
        }

        /// <summary>Device values to clamped, gamma-encoded sRGB in [0,1].</summary>
        public void Evaluate(double[] device, out double r, out double g, out double b)
        {
            double lr, lg, lb;
            EvaluateLinear(device, out lr, out lg, out lb);
            r = IccPcs.EncodeSrgb(lr);
            g = IccPcs.EncodeSrgb(lg);
            b = IccPcs.EncodeSrgb(lb);
        }

        /// <summary>
        /// Converts decoded PSD planes (PSD conventions: CMYK stored inverted, 1 = no ink) at
        /// <paramref name="depth"/> into the image's RGB planes. Returns false when this depth
        /// is not color managed (32-bit gray, LUT and CMYK).
        /// </summary>
        public bool Apply(float[][] planes, int depth, PlanarImage image)
        {
            return Apply(planes, depth, image.R, image.G, image.B, image.R.Length);
        }

        /// <summary>Converts the first <paramref name="count"/> samples of PSD-convention device planes into RGB.</summary>
        public bool Apply(float[][] planes, int depth, float[] red, float[] green, float[] blue, int count)
        {
            var tables = GetTables(depth);
            if (tables == null)
            {
                return false;
            }

            var image = new Output(red, green, blue);
            if (count < ParallelThreshold)
            {
                ApplyRange(tables, planes, image, 0, count);
            }
            else
            {
                // Pixels are independent, so chunking never changes the output.
                var chunks = (count + ParallelChunk - 1) / ParallelChunk;
                Parallelism.For(chunks, ParallelChunk, delegate(int first, int last)
                {
                    for (var chunk = first; chunk < last; chunk++)
                    {
                        ApplyRange(tables, planes, image, chunk * ParallelChunk, Math.Min(count, (chunk + 1) * ParallelChunk));
                    }
                });
            }

            return true;
        }

        /// <summary>
        /// The media white Little CMS uses for the absolute intent: the wtpt tag, D50 when it
        /// is missing, and D50 for v2 display profiles.
        /// </summary>
        private static IccXyz MediaWhite(IccProfile profile)
        {
            if (!profile.MediaWhitePoint.HasValue
                || (profile.MajorVersion < 4 && profile.DeviceClass == IccProfile.ClassDisplay))
            {
                return IccPcs.D50;
            }

            var white = profile.MediaWhitePoint.Value;
            return white.X > 0 && white.Y > 0 && white.Z > 0 && IsFinite(white.X + white.Y + white.Z) ? white : IccPcs.D50;
        }

        private static bool SameWhite(IccXyz a, IccXyz b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private IccXyz DeviceToPcs(double[] device, IccLut lut)
        {
            if (lut != null)
            {
                var pcs = new double[3];
                lut.Evaluate(device, pcs);
                return IccPcs.Decode(Profile.Pcs, lut.LabEncoding, pcs);
            }

            if (Channels == 1)
            {
                var value = Profile.GrayTrc.Evaluate(device[0]);

                // With a Lab PCS the gray curve gives L*/100 (ICC.1 gray model), otherwise luminance.
                return Profile.Pcs == IccProfile.SpaceLab
                    ? IccPcs.LabToXyz(value * 100.0, 0, 0)
                    : new IccXyz(IccPcs.D50.X * value, IccPcs.D50.Y * value, IccPcs.D50.Z * value);
            }

            var curves = _shaperCurves;
            var r = curves[0].Evaluate(device[0]);
            var g = curves[1].Evaluate(device[1]);
            var b = curves[2].Evaluate(device[2]);
            var m = _shaperMatrix;
            var o = _shaperOffset;
            return new IccXyz(
                (m[0] * r) + (m[1] * g) + (m[2] * b) + o[0],
                (m[3] * r) + (m[4] * g) + (m[5] * b) + o[1],
                (m[6] * r) + (m[7] * g) + (m[8] * b) + o[2]);
        }

        /// <summary>
        /// Little CMS cmsDetectBlackPoint for an input profile: the v4 perceptual black, the
        /// perceptual-black round trip for v2 CMYK output profiles under relative colorimetric,
        /// or the darkest colorant forced neutral.
        /// </summary>
        private IccXyz DetectBlackPoint(IccRenderingIntent intent)
        {
            if (Profile.MajorVersion >= 4
                && (intent == IccRenderingIntent.Perceptual || intent == IccRenderingIntent.Saturation))
            {
                return Profile.IsMatrixShaper
                    ? BlackFromDarkestColorant(IccRenderingIntent.RelativeColorimetric)
                    : PerceptualBlack;
            }

            if (intent == IccRenderingIntent.RelativeColorimetric
                && Profile.DeviceClass == IccProfile.ClassOutput
                && Profile.ColorSpace == IccProfile.SpaceCmyk)
            {
                return BlackFromPerceptualRoundTrip();
            }

            return BlackFromDarkestColorant(intent);
        }

        private IccXyz BlackFromDarkestColorant(IccRenderingIntent intent)
        {
            var supported = Profile.GetAToB(intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent) != null
                || (Channels != 4 && Profile.IsMatrixShaper);
            if (!supported)
            {
                return new IccXyz(0, 0, 0);
            }

            var darkest = new double[Channels];
            for (var i = 0; i < darkest.Length; i++)
            {
                darkest[i] = Channels == 4 ? 1.0 : 0.0;
            }

            double l, a, b;
            IccPcs.XyzToLab(DeviceToPcs(darkest, Profile.InputLut(intent)), out l, out a, out b);
            if (l > 50 || l < 0 || double.IsNaN(l))
            {
                l = 0;
            }

            return IccPcs.LabToXyz(l, 0, 0);
        }

        /// <summary>Lab black to the device through B2A0, back through the relative LUT, forced neutral and capped at L* 50.</summary>
        private IccXyz BlackFromPerceptualRoundTrip()
        {
            var toDevice = Profile.GetBToA(0);
            var toPcs = Profile.InputLut(IccRenderingIntent.RelativeColorimetric);
            if (toDevice == null || toPcs == null)
            {
                return new IccXyz(0, 0, 0);
            }

            var pcs = new double[3];
            var device = new double[IccLut.MaxChannels];
            IccPcs.Encode(Profile.Pcs, toDevice.LabEncoding, IccPcs.LabToXyz(0, 0, 0), pcs);
            toDevice.Evaluate(pcs, device);
            double l, a, b;
            IccPcs.XyzToLab(DeviceToPcs(device, toPcs), out l, out a, out b);
            if (double.IsNaN(l))
            {
                l = 0;
            }

            return IccPcs.LabToXyz(Math.Min(l, 50), 0, 0);
        }

        private bool CheckSrgbEquivalence()
        {
            // One 8-bit level over a 9^3 grid: the HP "sRGB IEC61966-2.1" profile maps pure green
            // to an encoded red of 0.5 levels, while gamma 2.2, Adobe RGB or Display P3 miss by
            // several levels.
            const int steps = 9;
            const double tolerance = 1.0 / 255.0;
            var device = new double[3];
            for (var r = 0; r < steps; r++)
            {
                for (var g = 0; g < steps; g++)
                {
                    for (var b = 0; b < steps; b++)
                    {
                        device[0] = r / (double)(steps - 1);
                        device[1] = g / (double)(steps - 1);
                        device[2] = b / (double)(steps - 1);
                        double outR, outG, outB;
                        Evaluate(device, out outR, out outG, out outB);
                        if (Math.Abs(outR - device[0]) > tolerance
                            || Math.Abs(outG - device[1]) > tolerance
                            || Math.Abs(outB - device[2]) > tolerance)
                        {
                            return false;
                        }
                    }
                }
            }

            // The grid misses the shadow end of the tone curves, where gamma 2.2 and sRGB differ most.
            for (var level = 1; level < 32; level++)
            {
                device[0] = level / 255.0;
                device[1] = device[0];
                device[2] = device[0];
                double outR, outG, outB;
                Evaluate(device, out outR, out outG, out outB);
                if (Math.Abs(outR - device[0]) > tolerance
                    || Math.Abs(outG - device[1]) > tolerance
                    || Math.Abs(outB - device[2]) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private Tables GetTables(int depth)
        {
            var tables = depth == 8 ? _tables8 : depth == 16 ? _tables16 : _tables32;
            if (tables == null)
            {
                lock (_gate)
                {
                    tables = depth == 8 ? _tables8 : depth == 16 ? _tables16 : _tables32;
                    if (tables == null)
                    {
                        tables = BuildTables(depth);
                        if (depth == 8)
                        {
                            _tables8 = tables;
                        }
                        else if (depth == 16)
                        {
                            _tables16 = tables;
                        }
                        else
                        {
                            _tables32 = tables;
                        }
                    }
                }
            }

            return tables.Kind == TableKind.None ? null : tables;
        }

        private Tables BuildTables(int depth)
        {
            var levels = depth == 8 ? 256 : depth == 16 ? 65536 : 0;
            var device = new double[4];
            if (_lut == null && !(Channels == 1 && _tintedGray))
            {
                if (Channels == 1)
                {
                    if (levels == 0)
                    {
                        return Tables.None;
                    }

                    var gray = new float[levels];
                    for (var i = 0; i < levels; i++)
                    {
                        device[0] = i / (double)(levels - 1);
                        gray[i] = (float)Encode(device).R;
                    }

                    return new Tables
                    {
                        Kind = TableKind.Gray,
                        MaxIndex = levels - 1,
                        Red = gray,
                    };
                }

                // Matrix/TRC RGB: linearize per channel (32-bit data is already linear), then one matrix.
                float[] red = null, green = null, blue = null;
                if (levels > 0)
                {
                    var curves = _shaperCurves;
                    red = new float[levels];
                    green = new float[levels];
                    blue = new float[levels];
                    for (var i = 0; i < levels; i++)
                    {
                        var x = i / (double)(levels - 1);
                        red[i] = (float)curves[0].Evaluate(x);
                        green[i] = (float)curves[1].Evaluate(x);
                        blue[i] = (float)curves[2].Evaluate(x);
                    }
                }

                return new Tables
                {
                    Kind = TableKind.Matrix,
                    MaxIndex = Math.Max(levels - 1, 0),
                    Red = red,
                    Green = green,
                    Blue = blue,
                    Coefficients = MatrixCoefficients(_shaperMatrix, _shaperOffset),
                };
            }

            if (depth == 32 && Channels == 3)
            {
                // 32-bit RGB holds linear light while a LUT profile describes encoded values.
                // For a LUT the closest is its colorants: the PCS of each full primary over the
                // PCS of device black, applied as a matrix to the linear data.
                var corner = new double[3];
                var black = DeviceToPcs(corner);
                var colorants = new double[9];
                for (var column = 0; column < 3; column++)
                {
                    corner[0] = 0;
                    corner[1] = 0;
                    corner[2] = 0;
                    corner[column] = 1;
                    var primary = DeviceToPcs(corner);
                    colorants[column] = primary.X - black.X;
                    colorants[3 + column] = primary.Y - black.Y;
                    colorants[6 + column] = primary.Z - black.Z;
                }

                return new Tables
                {
                    Kind = TableKind.Matrix,
                    Coefficients = MatrixCoefficients(colorants, new[] { black.X, black.Y, black.Z }),
                };
            }

            if (depth == 32 && Channels == 1)
            {
                // 32-bit gray holds linear light; a gray LUT describes encoded values.
                return Tables.None;
            }

            if (Channels == 1)
            {
                var size = levels > 0 ? levels : 4096;
                var grid = new float[size * 3];
                for (var i = 0; i < size; i++)
                {
                    device[0] = i / (double)(size - 1);
                    var sample = Encode(device);
                    grid[i * 3] = (float)sample.R;
                    grid[(i * 3) + 1] = (float)sample.G;
                    grid[(i * 3) + 2] = (float)sample.B;
                }

                return new Tables
                {
                    Kind = TableKind.GrayLut,
                    MaxIndex = size - 1,
                    GridPoints = size,
                    Grid = grid,
                };
            }

            if (Channels == 3)
            {
                return new Tables
                {
                    Kind = TableKind.Grid3,
                    MaxIndex = RgbGridPoints - 1,
                    GridPoints = RgbGridPoints,
                    Grid = SampleGrid(3, RgbGridPoints),
                };
            }

            return new Tables
            {
                Kind = TableKind.Grid4,
                MaxIndex = CmykGridPoints - 1,
                GridPoints = CmykGridPoints,
                Grid = SampleGrid(4, CmykGridPoints),
            };
        }

        private IccSrgbTransformSample Encode(double[] device)
        {
            double r, g, b;
            Evaluate(device, out r, out g, out b);
            return new IccSrgbTransformSample(r, g, b);
        }

        /// <summary>
        /// Folds black point compensation (and the absolute intent's white scaling) and the
        /// sRGB matrix into one 3x3 matrix plus offset over device-linear RGB.
        /// </summary>
        private float[] MatrixCoefficients(double[] colorants, double[] offset)
        {
            var scaled = new double[9];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    scaled[(row * 3) + column] = _bpcScale[row] * colorants[(row * 3) + column];
                }
            }

            var matrix = IccPcs.Multiply3x3(IccPcs.XyzD50ToSrgb, scaled);
            var s = IccPcs.XyzD50ToSrgb;
            var shift = new IccXyz(
                _bpcScale[0] * (offset[0] - BlackPoint.X),
                _bpcScale[1] * (offset[1] - BlackPoint.Y),
                _bpcScale[2] * (offset[2] - BlackPoint.Z));
            var coefficients = new float[12];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    coefficients[(row * 3) + column] = (float)matrix[(row * 3) + column];
                }

                coefficients[9 + row] = (float)((s[row * 3] * shift.X) + (s[(row * 3) + 1] * shift.Y) + (s[(row * 3) + 2] * shift.Z));
            }

            return coefficients;
        }

        /// <summary>
        /// Samples the whole conversion on a regular grid indexed by PSD sample values
        /// (for CMYK the PSD value is one minus the ink amount).
        /// </summary>
        private float[] SampleGrid(int dimensions, int points)
        {
            var total = 1;
            for (var d = 0; d < dimensions; d++)
            {
                total *= points;
            }

            // One slab per value of the first input; nodes are independent, so the slabs run in parallel.
            var grid = new float[total * 3];
            var slab = total / points;
            Parallelism.For(points, (long)slab * 8, delegate(int first, int last)
            {
                var device = new double[dimensions];
                var index = new int[dimensions];
                for (var head = first; head < last; head++)
                {
                    for (var node = head * slab; node < (head + 1) * slab; node++)
                    {
                        var rest = node;
                        for (var d = dimensions - 1; d >= 0; d--)
                        {
                            index[d] = rest % points;
                            rest /= points;
                        }

                        for (var d = 0; d < dimensions; d++)
                        {
                            var value = index[d] / (double)(points - 1);
                            device[d] = dimensions == 4 ? 1.0 - value : value;
                        }

                        double r, g, b;
                        Evaluate(device, out r, out g, out b);
                        grid[node * 3] = (float)r;
                        grid[(node * 3) + 1] = (float)g;
                        grid[(node * 3) + 2] = (float)b;
                    }
                }
            });

            return grid;
        }

        private static void ApplyRange(Tables tables, float[][] planes, Output image, int start, int end)
        {
            switch (tables.Kind)
            {
                case TableKind.Gray:
                    ApplyGray(tables, planes[0], image, start, end);
                    break;
                case TableKind.GrayLut:
                    ApplyGrayLut(tables, planes[0], image, start, end);
                    break;
                case TableKind.Matrix:
                    ApplyMatrix(tables, planes, image, start, end);
                    break;
                case TableKind.Grid3:
                    ApplyGrid3(tables, planes, image, start, end);
                    break;
                case TableKind.Grid4:
                    ApplyGrid4(tables, planes, image, start, end);
                    break;
            }
        }

        private static void ApplyGray(Tables tables, float[] gray, Output image, int start, int end)
        {
            var table = tables.Red;
            var scale = (float)tables.MaxIndex;
            var maxIndex = tables.MaxIndex;
            for (var i = start; i < end; i++)
            {
                var index = Compat.Clamp((int)((gray[i] * scale) + 0.5f), 0, maxIndex);
                var value = table[index];
                image.R[i] = value;
                image.G[i] = value;
                image.B[i] = value;
            }
        }

        private static void ApplyMatrix(Tables tables, float[][] planes, Output image, int start, int end)
        {
            float[] r = image.R, g = image.G, b = image.B;
            var length = end - start;
            if (tables.Red != null)
            {
                var redTable = tables.Red;
                var greenTable = tables.Green;
                var blueTable = tables.Blue;
                var scale = (float)tables.MaxIndex;
                var maxIndex = tables.MaxIndex;
                float[] sourceR = planes[0], sourceG = planes[1], sourceB = planes[2];
                for (var i = start; i < end; i++)
                {
                    r[i] = redTable[Compat.Clamp((int)((sourceR[i] * scale) + 0.5f), 0, maxIndex)];
                    g[i] = greenTable[Compat.Clamp((int)((sourceG[i] * scale) + 0.5f), 0, maxIndex)];
                    b[i] = blueTable[Compat.Clamp((int)((sourceB[i] * scale) + 0.5f), 0, maxIndex)];
                }
            }
            else
            {
                Array.Copy(planes[0], start, r, start, length);
                Array.Copy(planes[1], start, g, start, length);
                Array.Copy(planes[2], start, b, start, length);
            }

            MultiplyMatrix(tables.Coefficients, r, g, b, start, length);
            SrgbEncoder.EncodeInPlace(r, start, length);
            SrgbEncoder.EncodeInPlace(g, start, length);
            SrgbEncoder.EncodeInPlace(b, start, length);
        }

        /// <summary>Applies the 3x3 matrix plus offset to three planes in place, clamping to [0,1].</summary>
        private static void MultiplyMatrix(float[] m, float[] r, float[] g, float[] b, int start, int count)
        {
            for (var i = start; i < start + count; i++)
            {
                float vr = r[i], vg = g[i], vb = b[i];
                r[i] = Compat.Clamp((vr * m[0]) + (vg * m[1]) + (vb * m[2]) + m[9], 0f, 1f);
                g[i] = Compat.Clamp((vr * m[3]) + (vg * m[4]) + (vb * m[5]) + m[10], 0f, 1f);
                b[i] = Compat.Clamp((vr * m[6]) + (vg * m[7]) + (vb * m[8]) + m[11], 0f, 1f);
            }
        }

        /// <summary>A gray LUT profile sampled per input level (or 4096 steps), linear between entries.</summary>
        private static void ApplyGrayLut(Tables tables, float[] gray, Output image, int start, int end)
        {
            var grid = tables.Grid;
            var max = tables.MaxIndex;
            float[] r = image.R, g = image.G, b = image.B;
            for (var i = start; i < end; i++)
            {
                int index;
                float t;
                Locate(gray[i], max, out index, out t);
                var at = index * 3;
                r[i] = grid[at] + ((grid[at + 3] - grid[at]) * t);
                g[i] = grid[at + 1] + ((grid[at + 4] - grid[at + 1]) * t);
                b[i] = grid[at + 2] + ((grid[at + 5] - grid[at + 2]) * t);
            }
        }

        private static void ApplyGrid3(Tables tables, float[][] planes, Output image, int start, int end)
        {
            var grid = tables.Grid;
            var max = tables.MaxIndex;
            float[] r = image.R, g = image.G, b = image.B;
            var points = tables.GridPoints;
            int sz = 3, sy = points * 3, sx = points * points * 3;
            float[] p0 = planes[0], p1 = planes[1], p2 = planes[2];
            for (var i = start; i < end; i++)
            {
                int ix, iy, iz;
                float fx, fy, fz;
                Locate(p0[i], max, out ix, out fx);
                Locate(p1[i], max, out iy, out fy);
                Locate(p2[i], max, out iz, out fz);
                double outR, outG, outB;
                Tetrahedral(grid, (ix * sx) + (iy * sy) + (iz * sz), sx, sy, sz, fx, fy, fz, out outR, out outG, out outB);
                r[i] = (float)outR;
                g[i] = (float)outG;
                b[i] = (float)outB;
            }
        }

        /// <summary>
        /// One CMYK color (PSD convention, 1 = no ink) through the 8-bit pixel grid, exactly as
        /// Apply converts it. Returns false when this transform has no CMYK grid.
        /// </summary>
        public bool ConvertCmyk(float c, float m, float y, float k, out float red, out float green, out float blue)
        {
            var tables = GetTables(8);
            if (Channels != 4 || tables == null || tables.Kind != TableKind.Grid4)
            {
                red = green = blue = 0;
                return false;
            }

            var grid = tables.Grid;
            var max = tables.MaxIndex;
            var points = tables.GridPoints;
            int sk = 3, sy = points * 3, sm = points * points * 3, sc = points * points * points * 3;
            int ic, im, iy, ik;
            float fc, fm, fy, fk;
            Locate(c, max, out ic, out fc);
            Locate(m, max, out im, out fm);
            Locate(y, max, out iy, out fy);
            Locate(k, max, out ik, out fk);
            var origin = (ic * sc) + (im * sm) + (iy * sy) + (ik * sk);
            double r0, g0, b0;
            Tetrahedral(grid, origin, sm, sy, sk, fm, fy, fk, out r0, out g0, out b0);
            if (fc > 0)
            {
                double r1, g1, b1;
                Tetrahedral(grid, origin + sc, sm, sy, sk, fm, fy, fk, out r1, out g1, out b1);
                r0 += (r1 - r0) * fc;
                g0 += (g1 - g0) * fc;
                b0 += (b1 - b0) * fc;
            }

            red = (float)r0;
            green = (float)g0;
            blue = (float)b0;
            return true;
        }

        private static void ApplyGrid4(Tables tables, float[][] planes, Output image, int start, int end)
        {
            var grid = tables.Grid;
            var max = tables.MaxIndex;
            var points = tables.GridPoints;
            int sk = 3, sy = points * 3, sm = points * points * 3, sc = points * points * points * 3;
            float[] c = planes[0], m = planes[1], y = planes[2], k = planes[3];
            float[] r = image.R, g = image.G, b = image.B;
            for (var i = start; i < end; i++)
            {
                int ic, im, iy, ik;
                float fc, fm, fy, fk;
                Locate(c[i], max, out ic, out fc);
                Locate(m[i], max, out im, out fm);
                Locate(y[i], max, out iy, out fy);
                Locate(k[i], max, out ik, out fk);
                var origin = (ic * sc) + (im * sm) + (iy * sy) + (ik * sk);
                double r0, g0, b0;
                Tetrahedral(grid, origin, sm, sy, sk, fm, fy, fk, out r0, out g0, out b0);
                if (fc > 0)
                {
                    double r1, g1, b1;
                    Tetrahedral(grid, origin + sc, sm, sy, sk, fm, fy, fk, out r1, out g1, out b1);
                    r0 += (r1 - r0) * fc;
                    g0 += (g1 - g0) * fc;
                    b0 += (b1 - b0) * fc;
                }

                r[i] = (float)r0;
                g[i] = (float)g0;
                b[i] = (float)b0;
            }
        }

        private static void Locate(float value, int max, out int index, out float fraction)
        {
            var position = (value > 0f ? (value < 1f ? value : 1f) : 0f) * max;
            index = Math.Min((int)position, max - 1);
            fraction = position - index;
        }

        private static void Tetrahedral(float[] v, int o, int sx, int sy, int sz, float rx, float ry, float rz, out double r, out double g, out double b)
        {
            // The three corners walked from the origin along the fractional order (Little CMS's six tetrahedra).
            if (rx >= ry && ry >= rz)
            {
                Combine(v, o, sx, sx + sy, sx + sy + sz, rx, ry, rz, out r, out g, out b);
            }
            else if (rx >= rz && rz >= ry)
            {
                Combine(v, o, sx, sx + sz, sx + sy + sz, rx, rz, ry, out r, out g, out b);
            }
            else if (rz >= rx && rx >= ry)
            {
                Combine(v, o, sz, sx + sz, sx + sy + sz, rz, rx, ry, out r, out g, out b);
            }
            else if (ry >= rx && rx >= rz)
            {
                Combine(v, o, sy, sx + sy, sx + sy + sz, ry, rx, rz, out r, out g, out b);
            }
            else if (ry >= rz && rz >= rx)
            {
                Combine(v, o, sy, sy + sz, sx + sy + sz, ry, rz, rx, out r, out g, out b);
            }
            else
            {
                Combine(v, o, sz, sy + sz, sx + sy + sz, rz, ry, rx, out r, out g, out b);
            }
        }

        /// <summary>Barycentric blend along origin, origin+a1, origin+a2, origin+a3 with sorted fractions t1 &gt;= t2 &gt;= t3.</summary>
        private static void Combine(float[] v, int o, int a1, int a2, int a3, float t1, float t2, float t3, out double r, out double g, out double b)
        {
            float w0 = 1f - t1, w1 = t1 - t2, w2 = t2 - t3, w3 = t3;
            r = (w0 * v[o]) + (w1 * v[o + a1]) + (w2 * v[o + a2]) + (w3 * v[o + a3]);
            g = (w0 * v[o + 1]) + (w1 * v[o + a1 + 1]) + (w2 * v[o + a2 + 1]) + (w3 * v[o + a3 + 1]);
            b = (w0 * v[o + 2]) + (w1 * v[o + a1 + 2]) + (w2 * v[o + a2 + 2]) + (w3 * v[o + a3 + 2]);
        }

        /// <summary>The three output planes a conversion writes.</summary>
        private struct Output
        {
            public Output(float[] r, float[] g, float[] b)
            {
                R = r;
                G = g;
                B = b;
            }

            public float[] R { get; private set; }

            public float[] G { get; private set; }

            public float[] B { get; private set; }
        }

        private enum TableKind
        {
            None,
            Gray,
            GrayLut,
            Matrix,
            Grid3,
            Grid4,
        }

        /// <summary>Lookup data for one input depth.</summary>
        private sealed class Tables
        {
            private static readonly Tables Empty = new Tables();

            public static Tables None
            {
                get { return Empty; }
            }

            public TableKind Kind { get; set; }

            /// <summary>Largest table index (levels - 1) or grid index (points - 1).</summary>
            public int MaxIndex { get; set; }

            public int GridPoints { get; set; }

            public float[] Red { get; set; }

            public float[] Green { get; set; }

            public float[] Blue { get; set; }

            /// <summary>Matrix (row major) followed by the three offsets.</summary>
            public float[] Coefficients { get; set; }

            /// <summary>Interleaved RGB outputs per node.</summary>
            public float[] Grid { get; set; }
        }
    }

    /// <summary>One sample result of <see cref="IccSrgbTransform.EvaluateLinear(double)"/>.</summary>
    internal struct IccSrgbTransformSample
    {
        public IccSrgbTransformSample(double r, double g, double b)
            : this()
        {
            R = r;
            G = g;
            B = b;
        }

        public double R { get; private set; }

        public double G { get; private set; }

        public double B { get; private set; }
    }

    /// <summary>Linear-light to sRGB-encoded floats through a 4096-segment table (error under 2e-5).</summary>
    internal static class SrgbEncoder
    {
        private const int Segments = 4096;
        private static readonly float[] Table = BuildTable();

        public static void EncodeInPlace(float[] values, int start, int count)
        {
            var table = Table;
            for (var i = start; i < start + count; i++)
            {
                var position = (values[i] > 0f ? (values[i] < 1f ? values[i] : 1f) : 0f) * Segments;
                var index = Math.Min((int)position, Segments - 1);
                var t = position - index;
                values[i] = table[index] + ((table[index + 1] - table[index]) * t);
            }
        }

        private static float[] BuildTable()
        {
            var table = new float[Segments + 1];
            for (var i = 0; i <= Segments; i++)
            {
                table[i] = (float)IccPcs.EncodeSrgb(i / (double)Segments);
            }

            return table;
        }
    }
}
