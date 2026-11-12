using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>How Linear/Reflected gradients map bounds onto the ramp (see GradientSpanBasis in the reference blend_math).</summary>
    internal enum GradientSpan
    {
        /// <summary>Layer-style overlays and strokes: the center chord, with the center and half ramp snapped to whole pixels.</summary>
        LayerProjection,

        /// <summary>Gradient fill layers (GdFl): the center chord, with the gradient line's ends snapped to pixel centers.</summary>
        CenterChord,
    }

    /// <summary>
    /// The document color model a Classic gradient interpolates in. Photoshop mixes stop
    /// colors in the document's own channels: ink percentages in CMYK, gray in grayscale
    /// and duotone, L*a*b* in Lab, the profile's RGB in RGB documents with a profile that
    /// is not sRGB, and linear light in 32-bit RGB. sRGB documents mix the sRGB bytes.
    /// </summary>
    internal enum GradientColorModel
    {
        Srgb,
        Ink,
        Gray,
        Lab,
        ProfileRgb,
        LinearRgb,
    }

    /// <summary>Bounds a gradient is placed over, in document pixels (fractional for vector paths).</summary>
    internal struct GradientBox
    {
        public GradientBox(float left, float top, float width, float height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public float Left;
        public float Top;
        public float Width;
        public float Height;

        public static GradientBox From(PsdRect rect)
        {
            return new GradientBox(rect.Left, rect.Top, rect.Width, rect.Height);
        }
    }
    /// <summary>
    /// A gradient placement resolved once for a region: the ramp origin, the unit axis
    /// (screen coordinates, y down) and the ramp length. Position is the per-pixel part
    /// of the reference gradient_position.
    /// </summary>
    internal struct GradientFrame
    {
        private readonly PsdGradientType _type;
        private readonly bool _reverse;
        private readonly float _originX;
        private readonly float _originY;
        private readonly float _axisX;
        private readonly float _axisY;
        private readonly float _length;
        private readonly bool _linearFromCenter;

        public GradientFrame(PsdGradientType type, bool reverse, float originX, float originY, float axisX, float axisY, float length, bool linearFromCenter)
        {
            _type = type;
            _reverse = reverse;
            _originX = originX;
            _originY = originY;
            _axisX = axisX;
            _axisY = axisY;
            _length = length;
            _linearFromCenter = linearFromCenter;
        }

        /// <summary>Ramp position (0..1) of document pixel (x, y), sampled at the pixel center.</summary>
        public float Position(int x, int y)
        {
            var dx = x + 0.5f - _originX;
            var dy = y + 0.5f - _originY;

            // The reference's rotated frame: local x along the axis, local y 90 degrees
            // clockwise from it on screen.
            var localX = (dx * _axisX) + (dy * _axisY);
            var localY = (dy * _axisX) - (dx * _axisY);
            float position;
            switch (_type)
            {
                case PsdGradientType.Radial:
                    position = (float)Math.Sqrt((localX * localX) + (localY * localY)) / _length;
                    break;
                case PsdGradientType.Angle:
                    if (localX == 0 && localY == 0)
                    {
                        position = 0.25f;
                    }
                    else
                    {
                        position = (float)Math.Atan2(localY, localX) / (2f * (float)Math.PI);
                        if (position < 0)
                        {
                            position += 1f;
                        }
                    }

                    break;
                case PsdGradientType.Reflected:
                    position = Math.Abs(localX) / _length;
                    break;
                case PsdGradientType.Diamond:
                    position = (Math.Abs(localX) + Math.Abs(localY)) / _length;
                    break;
                default:
                    position = _linearFromCenter ? 0.5f + (localX / (2f * _length)) : localX / _length;
                    break;
            }

            if (_reverse)
            {
                position = 1f - position;
            }

            return Compat.Clamp(position, 0f, 1f);
        }
    }

    /// <summary>
    /// A solid (stop-based) gradient plus its placement, with the reference's evaluation:
    /// gradient_position, gradient_color and gradient_stop_opacity in blend_math.cpp.
    /// Noise (ClNs) gradients evaluate the reference's deterministic noise field. The
    /// data model comes from Layers' PsdGradient; this engine adds the color models
    /// (document-native stop interpolation), the pixel placement and the Oklab mixing.
    /// </summary>
    internal sealed class Gradient
    {
        private readonly PsdGradientColorStop[] _colorStops;
        private readonly PsdGradientAlphaStop[] _alphaStops;
        private readonly PsdGradient _model;

        // Stop components in ColorModel units, parallel to _colorStops; the conversion
        // to sRGB; and the sampled ramps (without and with endpoint smoothing).
        private double[][] _nativeStops;
        private DocumentColors _colors;
        private float[] _ramp;
        private float[] _smoothRamp;
        private const int RampSize = 4096;

        private Gradient(PsdGradient model)
        {
            _model = model;
            _colorStops = model.ColorStops.ToArray();
            _alphaStops = model.AlphaStops.ToArray();
            _angleDegrees = model.AngleDegrees;
            _noiseOverride = model.Noise;
        }

        /// <summary>Builds an engine gradient from programmatic stops (Gradient Map parsing).</summary>
        internal static Gradient FromStops(PsdGradientColorStop[] colorStops, PsdGradientAlphaStop[] alphaStops, float smoothness, PsdGradientInterpolation interpolation, PsdGradientNoise noise)
        {
            return new Gradient(PsdGradient.FromStops(colorStops, alphaStops, smoothness, interpolation, noise));
        }
        /// <param name="effect">The effect or fill descriptor that holds Grad and the placement keys.</param>
        /// <param name="documentColors">The document's color conversion helper (null: sRGB mixing).</param>
        /// <param name="defaultAngle">
        /// Angle used when Angl is missing: 90 for layer effects, 0 for fill and stroke
        /// content (Photoshop draws psd-tools' gradient-styles.psd noise fills, which
        /// omit the key, as vertical bands).
        /// </param>
        public static Gradient FromDescriptor(Descriptor effect, DocumentColors documentColors, double defaultAngle = 90)
        {
            var model = PsdGradient.FromDescriptor(effect, defaultAngle);
            var gradient = new Gradient(model);
            var interpolation = model.Interpolation;
            var classic = interpolation == PsdGradientInterpolation.Classic && model.Noise == null;
            var colorModel = classic ? NativeModel(documentColors) : GradientColorModel.Srgb;
            if (!classic || documentColors == null)
            {
                return gradient;
            }

            // Native stop components in document color units (Classic, non-noise only).
            var grad = effect.GetObject("Grad");
            if (grad == null)
            {
                return gradient;
            }

            var colors = grad.GetList("Clrs");
            if (colors == null || colors.Count == 0)
            {
                return gradient;
            }

            // Pair every stop's native components with its ramp location, then apply the
            // same stable location sort as the model: the arrays stay parallel even when
            // the file lists stops out of order.
            var pairs = new List<KeyValuePair<float, double[]>>();
            foreach (var item in colors)
            {
                if (item.Object == null)
                {
                    continue;
                }

                var native = NativeComponents(item.Object.GetObject("Clr "), colorModel);
                if (native == null)
                {
                    return gradient;
                }

                pairs.Add(new KeyValuePair<float, double[]>(
                    Compat.Clamp((float)(item.Object.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                    native));
            }

            var stopCount = 0;
            foreach (var stop in gradient._colorStops)
            {
                stopCount++;
            }

            if (pairs.Count != stopCount)
            {
                return gradient;
            }

            gradient.ColorModel = colorModel;
            gradient._nativeStops = pairs
                .OrderBy(p => p.Key)
                .Select(p => p.Value)
                .ToArray();
            gradient._colors = documentColors;
            return gradient;
        }

        public PsdGradientType Type
        {
            get { return _model.Type; }
        }

        public bool Reverse
        {
            get { return _model.Reverse; }
        }

        /// <summary>"Align With Layer" (Algn): the gradient spans the layer's bounds rather than the canvas.</summary>
        public bool AlignWithLayer
        {
            get { return _model.AlignWithLayer; }
        }

        public PsdGradientNoise Noise
        {
            get { return _noiseOverride ?? _model.Noise; }
            internal set { _noiseOverride = value; }
        }

        private PsdGradientNoise _noiseOverride;

        /// <summary>Placement angle in degrees (Angl); mutable for programmatic gradients.</summary>
        public float AngleDegrees
        {
            get { return _angleDegrees; }
            internal set { _angleDegrees = value; }
        }

        private float _angleDegrees;

        public PsdGradientInterpolation Interpolation
        {
            get { return _model.Interpolation; }
        }

        /// <summary>Smoothness 0..1 of the gradient (Intr / 4096; 1 for gradient maps).</summary>
        public float Smoothness
        {
            get { return _model.Smoothness; }
        }

        /// <summary>Color stops sorted by location (coincident stops keep file order).</summary>
        public IReadOnlyList<PsdGradientColorStop> ColorStops
        {
            get { return _colorStops; }
        }

        public GradientColorModel ColorModel
        {
            get;
            private set;
        }

        /// <summary>Ramp position (0..1) of document pixel (x, y) for gradient placement over bounds.</summary>
        public float Position(PsdRect bounds, int x, int y, GradientSpan basis)
        {
            return Frame(GradientBox.From(bounds), basis).Position(x, y);
        }

        /// <summary>The placement over bounds, computed once so a fill loop only evaluates GradientFrame.Position per pixel.</summary>
        public GradientFrame Frame(GradientBox bounds, GradientSpan basis)
        {
            var centerX = bounds.Left + (bounds.Width * (0.5f + (_model.OffsetXPercent / 100f)));
            var centerY = bounds.Top + (bounds.Height * (0.5f + (_model.OffsetYPercent / 100f)));
            var radians = AngleDegrees * (float)Math.PI / 180f;
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            var absCos = Math.Abs(cos);
            var absSin = Math.Abs(sin);

            // Both bases span the center chord through the bounds, min(w/|cos|, h/|sin|),
            // times Scale (reference blend_math.cpp, GradientSpanBasis::CenterChord).
            var chord = Math.Max(1f, Math.Min(absCos > 1e-6f ? bounds.Width / absCos : float.PositiveInfinity, absSin > 1e-6f ? bounds.Height / absSin : float.PositiveInfinity));
            if (basis == GradientSpan.LayerProjection)
            {
                // Reference gradient_position: center snapped to a pixel center, whole-pixel half ramp.
                var halfRamp = Math.Max(1f, (float)Math.Floor(chord * _model.Scale * 0.5f));
                return new GradientFrame(Type, Reverse, (float)Math.Floor(centerX) + 0.5f, (float)Math.Floor(centerY) + 0.5f, cos, -sin, halfRamp, true);
            }

            // Gradient fill layers (GdFl): the gradient line's ends snap to pixel
            // centers; at a Scale other than 100% a Linear ramp mirrors its snapped end
            // about the snapped center instead.
            var halfX = cos * chord * _model.Scale * 0.5f;
            var halfY = -sin * chord * _model.Scale * 0.5f;
            var endX = SnapToPixel(centerX + halfX);
            var endY = SnapToPixel(centerY + halfY);
            float startX;
            float startY;
            if (Type != PsdGradientType.Linear)
            {
                startX = SnapToPixel(centerX);
                startY = SnapToPixel(centerY);
            }
            else if (Math.Abs(_model.Scale - 1f) < 1e-4f)
            {
                startX = SnapToPixel(centerX - halfX);
                startY = SnapToPixel(centerY - halfY);
            }
            else
            {
                startX = (2f * SnapToPixel(centerX)) - endX;
                startY = (2f * SnapToPixel(centerY)) - endY;
            }

            var axisX = endX - startX;
            var axisY = endY - startY;
            var length = (float)Math.Sqrt((axisX * axisX) + (axisY * axisY));
            if (length < 0.5f)
            {
                // Bounds too small for a snapped line: keep the exact geometry.
                var half = Math.Max(0.25f, chord * _model.Scale * 0.5f);
                return Type == PsdGradientType.Linear
                    ? new GradientFrame(Type, Reverse, centerX - (cos * half), centerY + (sin * half), cos, -sin, 2f * half, false)
                    : new GradientFrame(Type, Reverse, centerX, centerY, cos, -sin, half, false);
            }

            return new GradientFrame(Type, Reverse, startX, startY, axisX / length, axisY / length, length, false);
        }

        /// <summary>Floors to the containing pixel and returns its center.</summary>
        private static float SnapToPixel(float value)
        {
            return (float)Math.Floor(value + 1e-3f) + 0.5f;
        }

        // __PART3__
        /// <summary>Straight color at a ramp position, as [0,1] floats.</summary>
        public void Color(float position, bool endpointSmoothing, out float r, out float g, out float b)
        {
            var noise = Noise;
            if (noise != null)
            {
                NoiseColor(noise, position, out r, out g, out b);
                return;
            }

            if (_nativeStops != null)
            {
                if (endpointSmoothing && _smoothRamp == null)
                {
                    _smoothRamp = NativeRamp(true);
                }

                if (!endpointSmoothing && _ramp == null)
                {
                    _ramp = NativeRamp(false);
                }

                var ramp = endpointSmoothing ? _smoothRamp : _ramp;
                // NaN (from a damaged placement) maps to the first entry.
                var clamped = position > 0f ? Math.Min(position, 1f) : 0f;
                var entry = 3 * (int)Math.Round(clamped * (RampSize - 1));
                r = ramp[entry];
                g = ramp[entry + 1];
                b = ramp[entry + 2];
                return;
            }

            var stops = _colorStops;
            if (stops.Length == 0)
            {
                r = g = b = position;
                return;
            }

            if (position <= stops[0].Location)
            {
                Unit(stops[0].Color, out r, out g, out b);
                return;
            }

            var last = stops.Length - 1;
            if (position >= stops[last].Location)
            {
                Unit(stops[last].Color, out r, out g, out b);
                return;
            }

            for (var index = 1; index < stops.Length; index++)
            {
                var right = stops[index];
                var left = stops[index - 1];
                if (position > right.Location)
                {
                    continue;
                }

                var span = Math.Max(0.0001f, right.Location - left.Location);
                var t = (position - left.Location) / span;
                if (right.Midpoint != 0.5f)
                {
                    t = MidpointRemap(t, right.Midpoint);
                }

                if (_model.Interpolation == PsdGradientInterpolation.Linear)
                {
                    r = LinearMix(left.Color.R, right.Color.R, t);
                    g = LinearMix(left.Color.G, right.Color.G, t);
                    b = LinearMix(left.Color.B, right.Color.B, t);
                    return;
                }

                if (_model.Interpolation == PsdGradientInterpolation.Perceptual)
                {
                    Oklab.Mix(left.Color, right.Color, t, out r, out g, out b);
                    return;
                }

                var previous = index > 1 ? stops[index - 2].Color : left.Color;
                var next = index + 1 < stops.Length ? stops[index + 1].Color : right.Color;
                var smoothness = stops.Length > 2 || endpointSmoothing ? _model.Smoothness : 0f;
                r = Channel(previous.R, left.Color.R, right.Color.R, next.R, t, smoothness);
                g = Channel(previous.G, left.Color.G, right.Color.G, next.G, t, smoothness);
                b = Channel(previous.B, left.Color.B, right.Color.B, next.B, t, smoothness);
                return;
            }

            Unit(stops[last].Color, out r, out g, out b);
        }

        private static float Channel(byte p0, byte p1, byte p2, byte p3, float t, float smoothness)
        {
            var linear = p1 + ((p2 - p1) * t);
            var cubic = CatmullRom(p0, p1, p2, p3, t);
            return Compat.Clamp((float)Math.Round(linear + ((cubic - linear) * smoothness)), 0f, 255f) / 255f;
        }

        /// <summary>Opacity at a ramp position.</summary>
        public float Opacity(float position, bool endpointSmoothing)
        {
            var noise = Noise;
            if (noise != null)
            {
                return noise.AddTransparency ? (float)NoiseChannel(noise, 3, position) : 1f;
            }

            var stops = _alphaStops;
            if (stops.Length == 0)
            {
                return 1f;
            }

            if (position <= stops[0].Location)
            {
                return stops[0].Opacity;
            }

            var last = stops.Length - 1;
            if (position >= stops[last].Location)
            {
                return stops[last].Opacity;
            }

            for (var index = 1; index < stops.Length; index++)
            {
                var right = stops[index];
                var left = stops[index - 1];
                if (position > right.Location)
                {
                    continue;
                }

                var span = Math.Max(0.0001f, right.Location - left.Location);
                var t = (position - left.Location) / span;
                if (right.Midpoint != 0.5f)
                {
                    t = MidpointRemap(t, right.Midpoint);
                }

                if (endpointSmoothing && _model.Smoothness > 0)
                {
                    var previous = index > 1 ? stops[index - 2].Opacity : left.Opacity;
                    var next = index + 1 < stops.Length ? stops[index + 1].Opacity : right.Opacity;
                    var linear = left.Opacity + ((right.Opacity - left.Opacity) * t);
                    var cubic = CatmullRom(previous, left.Opacity, right.Opacity, next, t);
                    return Compat.Clamp(linear + ((cubic - linear) * _model.Smoothness), 0f, 1f);
                }

                return left.Opacity + ((right.Opacity - left.Opacity) * t);
            }

            return stops[last].Opacity;
        }

        // __PART4__
        /// <summary>The model a document's descriptor gradients interpolate in.</summary>
        private static GradientColorModel NativeModel(DocumentColors colors)
        {
            if (colors == null)
            {
                return GradientColorModel.Srgb;
            }

            if (colors.ColorMode == PsdColorMode.Cmyk)
            {
                return GradientColorModel.Ink;
            }

            if (colors.ColorMode == PsdColorMode.Grayscale && colors.ColorMode == PsdColorMode.Duotone)
            {
                return GradientColorModel.Gray;
            }

            if (colors.ColorMode == PsdColorMode.Lab)
            {
                return GradientColorModel.Lab;
            }

            if (colors.ColorMode == PsdColorMode.Rgb && colors.Depth == 32)
            {
                return GradientColorModel.LinearRgb;
            }

            if (colors.ColorMode == PsdColorMode.Rgb && colors.TransformFor(3) != null)
            {
                return GradientColorModel.ProfileRgb;
            }

            return GradientColorModel.Srgb;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        /// <summary>
        /// A stop color's components in model units (ink and gray as fractions, Lab and
        /// RGB as stored; 32-bit RGB is stored as linear light), or null when the color
        /// is in another model.
        /// </summary>
        private static double[] NativeComponents(Descriptor color, GradientColorModel model)
        {
            if (color == null)
            {
                return null;
            }

            double[] components;
            switch (model)
            {
                case GradientColorModel.Ink:
                    if (!color.TryGet("Cyn ", out _))
                    {
                        return null;
                    }

                    components = new[]
                    {
                        color.GetNumber("Cyn ", 0) / 100,
                        color.GetNumber("Mgnt", 0) / 100,
                        color.GetNumber("Ylw ", 0) / 100,
                        color.GetNumber("Blck", 0) / 100,
                    };
                    break;
                case GradientColorModel.Gray:
                    if (!color.TryGet("Gry ", out _))
                    {
                        return null;
                    }

                    components = new[] { 1 - (color.GetNumber("Gry ", 0) / 100) };
                    break;
                case GradientColorModel.Lab:
                    if (!color.TryGet("Lmnc", out _))
                    {
                        return null;
                    }

                    components = new[] { color.GetNumber("Lmnc", 0), color.GetNumber("A   ", 0), color.GetNumber("B   ", 0) };
                    break;
                case GradientColorModel.LinearRgb:
                case GradientColorModel.ProfileRgb:
                    if (!color.TryGet("Rd  ", out _))
                    {
                        return null;
                    }

                    components = new[] { color.GetNumber("Rd  ", 0), color.GetNumber("Grn ", 0), color.GetNumber("Bl  ", 0) };
                    break;
                default:
                    return null;
            }

            foreach (var value in components)
            {
                if (!IsFinite(value))
                {
                    return null;
                }
            }

            return components;
        }

        // __PART5__
        /// <summary>
        /// Samples the ramp in the document model: the Classic segment interpolation on
        /// unrounded stop components, then one conversion to sRGB.
        /// </summary>
        private float[] NativeRamp(bool endpointSmoothing)
        {
            var stops = _colorStops;
            var native = _nativeStops;
            var colors = _colors;
            var channels = native[0].Length;
            var smoothness = stops.Length > 2 || endpointSmoothing ? _model.Smoothness : 0f;
            var ramp = new float[3 * RampSize];
            var mix = new double[channels];
            var last = stops.Length - 1;
            for (var i = 0; i < RampSize; i++)
            {
                var position = i / (float)(RampSize - 1);
                if (position <= stops[0].Location || stops.Length == 1)
                {
                    Array.Copy(native[0], mix, channels);
                }
                else if (position >= stops[last].Location)
                {
                    Array.Copy(native[last], mix, channels);
                }
                else
                {
                    var index = 1;
                    while (index < stops.Length - 1 && position > stops[index].Location)
                    {
                        index++;
                    }

                    var left = stops[index - 1];
                    var right = stops[index];
                    var t = (position - left.Location) / Math.Max(0.0001f, right.Location - left.Location);
                    if (right.Midpoint != 0.5f)
                    {
                        t = MidpointRemap(t, right.Midpoint);
                    }

                    var previous = native[index > 1 ? index - 2 : index - 1];
                    var next = native[index + 1 < stops.Length ? index + 1 : index];
                    for (var c = 0; c < channels; c++)
                    {
                        var p1 = native[index - 1][c];
                        var p2 = native[index][c];
                        var linear = p1 + ((p2 - p1) * t);
                        var cubic = CatmullRom((float)previous[c], (float)p1, (float)p2, (float)next[c], t);
                        mix[c] = linear + ((cubic - linear) * smoothness);
                    }
                }

                ramp[3 * i] = Convert(mix, colors).R / 255f;
                ramp[(3 * i) + 1] = Convert(mix, colors).G / 255f;
                ramp[(3 * i) + 2] = Convert(mix, colors).B / 255f;
            }

            return ramp;
        }

        private PsdColor Convert(double[] mix, DocumentColors colors)
        {
            switch (ColorModel)
            {
                case GradientColorModel.Ink:
                    return colors.FromInk(mix[0], mix[1], mix[2], mix[3]);
                case GradientColorModel.Gray:
                    return colors.Duotone != null
                        ? DuotoneColor(colors.Duotone, mix[0], colors)
                        : colors.FromGray(mix[0]);
                case GradientColorModel.Lab:
                    return LabColor(mix[0], mix[1], mix[2]);
                case GradientColorModel.ProfileRgb:
                    return colors.FromRgb(mix[0], mix[1], mix[2]);
                default:
                    return colors.FromDescriptorRgb(Math.Max(0, mix[0]), Math.Max(0, mix[1]), Math.Max(0, mix[2]));
            }
        }

        // __PART6__
        private static PsdColor LabColor(double lightness, double a, double b)
        {
            double r, g, bl;
            ColorSpaces.LabToSrgb(lightness, a, b, out r, out g, out bl);
            return new PsdColor(Level(r), Level(g), Level(bl));
        }

        /// <summary>A gray value of a duotone document through its inks, like its pixels.</summary>
        private static PsdColor DuotoneColor(Duotone duotone, double gray, DocumentColors colors)
        {
            double r, g, b;
            duotone.Evaluate(Math.Round(Compat.Clamp(gray, 0.0, 1.0) * 255) / 255, colors.TransformFor(1), out r, out g, out b);
            return new PsdColor(Level(r), Level(g), Level(b));
        }

        private static byte Level(double unit)
        {
            return (byte)Compat.Clamp((int)Math.Round(unit * 255), 0, 255);
        }

        private static void Unit(PsdColor color, out float r, out float g, out float b)
        {
            r = color.R / 255f;
            g = color.G / 255f;
            b = color.B / 255f;
        }

        private static float MidpointRemap(float value, float midpoint)
        {
            value = Compat.Clamp(value, 0f, 1f);
            midpoint = Compat.Clamp(midpoint, 0.0001f, 0.9999f);
            return value <= midpoint ? 0.5f * value / midpoint : 0.5f + (0.5f * (value - midpoint) / (1f - midpoint));
        }

        private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;
            return 0.5f * ((2f * p1) + ((-p0 + p2) * t) + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
        }

        private static float LinearMix(byte a, byte b, float t)
        {
            var la = SrgbToLinear(a / 255.0);
            var lb = SrgbToLinear(b / 255.0);
            return (float)Compat.Clamp((int)Math.Round(ColorSpaces.EncodeSrgb(la + ((lb - la) * t)) * 255), 0, 255) / 255f;
        }

        internal static double SrgbToLinear(double value)
        {
            value = Compat.Clamp(value, 0.0, 1.0);
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        // __PART7__
        // ---------------------------------------------------------------------
        // Noise (ClNs) gradients: the reference's deterministic noise field,
        // ported from smooth_noise, gradient_noise_channel and the noise branches
        // of gradient_color / gradient_stop_opacity in blend_math.cpp. Photoshop's
        // own random sequence is not known; what matches is the character:
        // roughness, the per-channel ranges, the color model and transparency.
        // ---------------------------------------------------------------------

        /// <summary>gradient_noise_channel: four octaves of smoothed value noise mapped into the channel's range.</summary>
        internal static double NoiseChannel(PsdGradientNoise noise, int channel, double position)
        {
            var roughness = noise.Roughness / 4096.0;
            var total = 0.0;
            var weight = 0.0;
            var amplitude = 1.0;
            var frequency = 4.0 + (roughness * 60.0);
            for (var octave = 0; octave < 4; octave++)
            {
                total += SmoothNoise(unchecked(noise.Seed + (uint)(octave * 977)), channel, position * frequency, roughness) * amplitude;
                weight += amplitude;
                amplitude *= 0.5;
                frequency *= 2.0;
            }

            var value = total / Math.Max(0.0001, weight);
            var index = Compat.Clamp(channel, 0, 3);
            var minimum = (noise.Minimum.Length > index ? noise.Minimum[index] : 0.0) / 100.0;
            var maximum = (noise.Maximum.Length > index ? noise.Maximum[index] : 100.0) / 100.0;
            return Compat.Clamp(minimum + ((maximum - minimum) * value), 0.0, 1.0);
        }

        /// <summary>The noise branch of gradient_color, as byte-rounded unit floats.</summary>
        internal static void NoiseColor(PsdGradientNoise noise, double position, out float r, out float g, out float b)
        {
            var c0 = NoiseChannel(noise, 0, position);
            var c1 = NoiseChannel(noise, 1, position);
            var c2 = NoiseChannel(noise, 2, position);
            if (noise.Model == PsdGradientNoiseModel.Hsb)
            {
                var sector = c0 * 6.0;
                var index = (int)Math.Floor(sector) % 6;
                var fraction = sector - Math.Floor(sector);
                var p = c2 * (1.0 - c1);
                var q = c2 * (1.0 - (fraction * c1));
                var t = c2 * (1.0 - ((1.0 - fraction) * c1));
                double nr, ng, nb;
                switch (index)
                {
                    case 0: nr = c2; ng = t; nb = p; break;
                    case 1: nr = q; ng = c2; nb = p; break;
                    case 2: nr = p; ng = c2; nb = t; break;
                    case 3: nr = p; ng = q; nb = c2; break;
                    case 4: nr = t; ng = p; nb = c2; break;
                    default: nr = c2; ng = p; nb = q; break;
                }

                r = NoiseUnit(nr);
                g = NoiseUnit(ng);
                b = NoiseUnit(nb);
                return;
            }

            if (noise.Model == PsdGradientNoiseModel.Lab)
            {
                // The reference renders Lab noise through an OKLab-shaped approximation.
                Oklab.FromOklab(c0, c1 - 0.5, c2 - 0.5, out r, out g, out b);
                return;
            }

            r = NoiseUnit(c0);
            g = NoiseUnit(c1);
            b = NoiseUnit(c2);
        }

        private static ulong SplitMix64(ulong value)
        {
            unchecked
            {
                value += 0x9e3779b97f4a7c15UL;
                value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
                value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
                return value ^ (value >> 31);
            }
        }

        private static double UnitHash(ulong value)
        {
            return (SplitMix64(value) >> 11) * (1.0 / 9007199254740992.0);
        }

        /// <summary>smooth_noise: smoothstep-interpolated hashed lattice values, pulled toward 0.5 by low roughness.</summary>
        private static double SmoothNoise(uint seed, int channel, double coordinate, double roughness)
        {
            var floor = Math.Floor(coordinate);
            var cell = floor >= long.MaxValue - 1 ? long.MaxValue - 1 : (floor <= long.MinValue ? long.MinValue : (long)floor);
            var fraction = coordinate - cell;
            var smooth = fraction * fraction * (3.0 - (2.0 * fraction));
            var salt = ((ulong)seed << 1) ^ unchecked((ulong)(channel + 1) * 0xd6e8feb86659fd93UL);
            var a = UnitHash(unchecked((ulong)cell) ^ salt);
            var b = UnitHash(unchecked((ulong)(cell + 1)) ^ salt);
            var value = a + ((b - a) * smooth);
            return 0.5 + ((value - 0.5) * Compat.Clamp(roughness, 0.0, 1.0));
        }

        private static float NoiseUnit(double value)
        {
            return PsdColor.ClampByte(value * 255.0) / 255f;
        }

        // __PART8__
        /// <summary>Oklab mixing and conversion for Perceptual gradients (reference Oklab).</summary>
        internal static class Oklab
        {
            public static void Mix(PsdColor a, PsdColor b, float t, out float r, out float g, out float bl)
            {
                double al, aa, ab;
                double bl1, ba, bb;
                ToOklab(a, out al, out aa, out ab);
                ToOklab(b, out bl1, out ba, out bb);
                FromOklab(al + ((bl1 - al) * t), aa + ((ba - aa) * t), ab + ((bb - ab) * t), out r, out g, out bl);
            }

            private static void ToOklab(PsdColor color, out double l, out double a, out double b)
            {
                var r = SrgbToLinear(color.R / 255.0);
                var g = SrgbToLinear(color.G / 255.0);
                var bl = SrgbToLinear(color.B / 255.0);
                var l0 = Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * bl));
                var m0 = Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * bl));
                var s0 = Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * bl));
                l = (0.2104542553 * l0) + (0.7936177850 * m0) - (0.0040720468 * s0);
                a = (1.9779984951 * l0) - (2.4285922050 * m0) + (0.4505937099 * s0);
                b = (0.0259040371 * l0) + (0.7827717662 * m0) - (0.8086757660 * s0);
            }

            public static void FromOklab(double lightness, double a, double b, out float r, out float g, out float bl)
            {
                var l = lightness + (0.3963377774 * a) + (0.2158037573 * b);
                var m = lightness - (0.1055613458 * a) - (0.0638541728 * b);
                var s = lightness - (0.0894841775 * a) - (1.2914855480 * b);
                l = l * l * l;
                m = m * m * m;
                s = s * s * s;
                var rr = (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
                var gg = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
                var bb = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);
                r = Encode(rr);
                g = Encode(gg);
                bl = Encode(bb);
            }

            private static float Encode(double v)
            {
                return (float)Compat.Clamp((int)Math.Round(ColorSpaces.EncodeSrgb(Compat.Clamp(v, 0.0, 1.0)) * 255), 0, 255) / 255f;
            }

            private static double Cbrt(double value)
            {
                // Math.Cbrt is netcoreapp2.1+; Math.Pow(x, 1/3) differs for negatives.
                return value >= 0 ? Math.Pow(value, 1.0 / 3.0) : -Math.Pow(-value, 1.0 / 3.0);
            }
        }
    }
}