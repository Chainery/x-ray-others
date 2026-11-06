using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace XRay.PsdFx
{
    /// <summary>
    /// Converts single colors read from descriptors (CMYC, Grsc, RGBC), text engine
    /// data (/FillColor types 0, 1 and 2), legacy 10-byte color records and pattern
    /// tiles to sRGB the same way the document's pixels convert, so effect and text
    /// colors keep their relationship with the converted pixels. Colors quantize to
    /// the 8-bit channel convention first (CMYK inverted, gray 0 = black), then go
    /// through the document's ICC transform (phase 10) with the same tables as an
    /// 8-bit pixel plane; without a usable profile the built-in formulas apply.
    /// </summary>
    internal sealed class DocumentColors
    {
        private readonly PsdDocument _document;

        private StrongBox<Duotone> _duotone;

        public DocumentColors(PsdDocument document)
        {
            _document = document;
        }

        /// <summary>The document's color mode.</summary>
        public PsdColorMode ColorMode
        {
            get { return _document.ColorMode; }
        }

        /// <summary>The document's bits per channel.</summary>
        public int Depth
        {
            get { return _document.Depth; }
        }

        /// <summary>The parsed duotone specification of a duotone document, or null (parsed on first use).</summary>
        public Duotone Duotone
        {
            get
            {
                return LazyInitializer.EnsureInitialized(
                    ref _duotone,
                    delegate
                    {
                        return new StrongBox<Duotone>(_document.ColorMode == PsdColorMode.Duotone ? Duotone.Parse(_document.ColorModeData) : null);
                    }).Value;
            }
        }

        /// <summary>
        /// The document's ICC transform when it converts <paramref name="channels"/>
        /// device channels (1 gray, 3 RGB, 4 CMYK), else null. Phase 10 fills it.
        /// </summary>
        public IccSrgbTransform TransformFor(int channels)
        {
            var transform = _document.ColorTransform;
            return transform != null && transform.Channels == channels ? transform : null;
        }

        /// <summary>
        /// CMYK ink fractions (0 to 1, 1 = full ink) to sRGB: through the document's
        /// CMYK profile, else the naive mix <c>R = (1 - C)(1 - K)</c>.
        /// </summary>
        public PsdColor FromInk(double cyan, double magenta, double yellow, double black)
        {
            return FromInk(cyan, magenta, yellow, black, 255);
        }

        public PsdColor FromInk(double cyan, double magenta, double yellow, double black, byte alpha)
        {
            cyan = Unit(cyan);
            magenta = Unit(magenta);
            yellow = Unit(yellow);
            black = Unit(black);
            var transform = TransformFor(4);
            if (transform != null)
            {
                var device = new[] { Inverted(cyan), Inverted(magenta), Inverted(yellow), Inverted(black) };
                var managed = Convert(transform, device, 8, alpha);
                if (managed.HasValue)
                {
                    return managed.Value;
                }
            }

            return new PsdColor(Level((1 - cyan) * (1 - black)), Level((1 - magenta) * (1 - black)), Level((1 - yellow) * (1 - black)), alpha);
        }

        /// <summary>
        /// Gray lightness (0 = black, 1 = white) to sRGB: through the gray profile of a
        /// grayscale or duotone document, else a neutral copy.
        /// </summary>
        public PsdColor FromGray(double lightness)
        {
            return FromGray(lightness, 255);
        }

        public PsdColor FromGray(double lightness, byte alpha)
        {
            var level = Level(lightness);
            var transform = TransformFor(1);
            if (transform != null)
            {
                var managed = Convert(transform, new[] { level / 255f }, 8, alpha);
                if (managed.HasValue)
                {
                    return managed.Value;
                }
            }

            return new PsdColor(level, level, level, alpha);
        }

        /// <summary>
        /// RGB components on the 0 to 255 scale, as stored by text engine data and
        /// legacy records: through the document's RGB profile, else rounded as they are.
        /// </summary>
        public PsdColor FromRgb(double red, double green, double blue)
        {
            return FromRgb(red, green, blue, 255);
        }

        public PsdColor FromRgb(double red, double green, double blue, byte alpha)
        {
            byte r = Byte(red), g = Byte(green), b = Byte(blue);
            var transform = TransformFor(3);
            if (transform != null)
            {
                var managed = Convert(transform, new[] { r / 255f, g / 255f, b / 255f }, 8, alpha);
                if (managed.HasValue)
                {
                    return managed.Value;
                }
            }

            return new PsdColor(r, g, b, alpha);
        }

        /// <summary>
        /// Descriptor RGB (Rd, Grn, Bl on 0 to 255). In 32-bit documents these are
        /// linear light like the float channels, so they convert like 32-bit pixels;
        /// otherwise as <see cref="FromRgb(double, double, double)"/>.
        /// </summary>
        public PsdColor FromDescriptorRgb(double red, double green, double blue)
        {
            if (_document.Depth != 32)
            {
                return FromRgb(red, green, blue);
            }

            var linear = new[] { Linear(red), Linear(green), Linear(blue) };
            var transform = TransformFor(3);
            if (transform != null)
            {
                var managed = Convert(transform, linear, 32, 255);
                if (managed.HasValue)
                {
                    return managed.Value;
                }
            }

            ColorSpaces.LinearToSrgb(linear);
            return new PsdColor(Level(linear[0]), Level(linear[1]), Level(linear[2]));
        }

        /// <summary>
        /// Converts device planes (PSD conventions, 8-bit levels as floats) through the
        /// document transform for their channel count. Returns false, leaving
        /// <paramref name="image"/> untouched, when that transform does not apply.
        /// </summary>
        public bool TryConvert(float[][] planes, PlanarImage image)
        {
            var transform = TransformFor(planes.Length);
            return transform != null && transform.Apply(planes, 8, image);
        }

        /// <summary>One color through the planar path (the pixel tables), or null when the transform does not manage that depth.</summary>
        private static PsdColor? Convert(IccSrgbTransform transform, float[] device, int depth, byte alpha)
        {
            var planes = new float[device.Length][];
            for (var i = 0; i < device.Length; i++)
            {
                planes[i] = new[] { device[i] };
            }

            var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
            if (!transform.Apply(planes, depth, image))
            {
                return null;
            }

            var rgb = new byte[3];
            PlanarImage.UnitFloatToBytes(new[] { image.R[0], image.G[0], image.B[0] }, rgb);
            return new PsdColor(rgb[0], rgb[1], rgb[2], alpha);
        }

        private static float Inverted(double ink)
        {
            return (float)(Math.Round((1 - ink) * 255) / 255);
        }

        private static double Unit(double value)
        {
            return IsFinite(value) ? Compat.Clamp(value, 0, 1) : 0;
        }

        private static byte Level(double unit)
        {
            return (byte)Compat.Clamp(Math.Round(Unit(unit) * 255), 0, 255);
        }

        private static byte Byte(double value)
        {
            return IsFinite(value) ? (byte)Compat.Clamp(Math.Round(value), 0, 255) : (byte)0;
        }

        private static float Linear(double value)
        {
            return IsFinite(value) ? (float)Compat.Clamp(value / 255.0, 0, 1) : 0f;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
