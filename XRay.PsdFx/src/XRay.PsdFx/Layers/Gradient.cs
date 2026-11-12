using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    public enum PsdGradientType
    {
        Linear,
        Radial,
        Angle,
        Reflected,
        Diamond,

        /// <summary>Stroke-only: follows the stroke band's distance field.</summary>
        ShapeBurst,
    }

    public enum PsdGradientInterpolation
    {
        Classic,
        Perceptual,
        Linear,
    }

    public enum PsdGradientNoiseModel
    {
        Rgb,
        Hsb,
        Lab,
    }

    public struct PsdGradientColorStop
    {
        public PsdGradientColorStop(float location, PsdColor color, float midpoint) : this()
        {
            Location = location;
            Color = color;
            Midpoint = midpoint;
        }

        /// <summary>Ramp position 0..1 (Lctn / 4096).</summary>
        public float Location { get; private set; }

        public PsdColor Color { get; private set; }

        /// <summary>Midpoint 0..1 (Mdpn / 100): position of the 50% mix inside the segment.</summary>
        public float Midpoint { get; private set; }

        public override string ToString()
        {
            return string.Format("{0:0.##} {1} mid {2:0.##}", Location, Color, Midpoint);
        }
    }

    public struct PsdGradientAlphaStop
    {
        public PsdGradientAlphaStop(float location, float opacity, float midpoint) : this()
        {
            Location = location;
            Opacity = opacity;
            Midpoint = midpoint;
        }

        /// <summary>Ramp position 0..1 (Lctn / 4096).</summary>
        public float Location { get; private set; }

        /// <summary>Opacity 0..1 (Opct / 100).</summary>
        public float Opacity { get; private set; }

        /// <summary>Midpoint 0..1 (Mdpn / 100).</summary>
        public float Midpoint { get; private set; }

        public override string ToString()
        {
            return string.Format("{0:0.##} a={1:0.##} mid {2:0.##}", Location, Opacity, Midpoint);
        }
    }

    /// <summary>
    /// The file properties of a noise (ClNs) gradient: roughness, seed, per-channel
    /// ranges and the color model. Evaluation follows the reference's deterministic
    /// noise field; this library exposes the settings only.
    /// </summary>
    public sealed class PsdGradientNoise
    {
        /// <summary>RndS seed.</summary>
        public uint Seed { get; internal set; }

        /// <summary>Smth, 0..4096 (Photoshop's roughness percent times 40.96).</summary>
        public int Roughness { get; internal set; }

        /// <summary>ShTr: the fourth noise channel drives opacity.</summary>
        public bool AddTransparency { get; internal set; }

        public PsdGradientNoiseModel Model { get; internal set; }

        /// <summary>Mnm  per-channel minimums, 0..100 (RGB channels, alpha last).</summary>
        public double[] Minimum { get; internal set; }

        /// <summary>Mxm  per-channel maximums, 0..100.</summary>
        public double[] Maximum { get; internal set; }

        internal PsdGradientNoise()
        {
        }

        public PsdGradientNoise(uint seed, int roughness, bool transparency, PsdGradientNoiseModel model, double[] minimum, double[] maximum)
        {
            Seed = seed;
            Roughness = roughness;
            AddTransparency = transparency;
            Model = model;
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>One noise channel (alpha last) in [0,1] at ramp position <paramref name="position"/>.</summary>
        public double Channel(int channel, double position)
        {
            return Gradient.NoiseChannel(this, channel, position);
        }

        /// <summary>The noise's color (sRGB, 0..1; the alpha channel when transparency is on) at a ramp position.</summary>
        internal ColorF Color(double position)
        {
            float r, g, b;
            Gradient.NoiseColor(this, position, out r, out g, out b);
            return new ColorF(r, g, b);
        }

        public override string ToString()
        {
            return string.Format("noise seed {0} roughness {1} {2}{3}", Seed, Roughness, Model, AddTransparency ? " +transparency" : "");
        }
    }

    /// <summary>
    /// A gradient's file properties from a Grad descriptor plus its placement keys
    /// (Type, Angl, Scl , Rvrs, Algn, Ofst). Ported from XRay.Psd's Gradient -
    /// the data model only; pixel evaluation (color models, ramps) is not included.
    /// </summary>
    public sealed class PsdGradient
    {
        public static PsdGradient FromDescriptor(Descriptor effect, double defaultAngle = 90)
        {
            var gradient = new PsdGradient();
            var grad = effect.GetObject("Grad");
            var offset = effect.GetObject("Ofst");

            var type = effect.GetEnum("Type");
            gradient.Type =
                type == "Rdl " ? PsdGradientType.Radial :
                type == "Angl" ? PsdGradientType.Angle :
                type == "Rflc" ? PsdGradientType.Reflected :
                type == "Dmnd" ? PsdGradientType.Diamond :
                type == "shapeburst" ? PsdGradientType.ShapeBurst : PsdGradientType.Linear;

            var interpolation = effect.GetEnum("gs99") ?? effect.GetEnum("gradientsInterpolationMethod");
            gradient.Interpolation =
                interpolation == "perceptual" || interpolation == "Perc" || interpolation == "Smoo" ? PsdGradientInterpolation.Perceptual :
                interpolation == "linear" || interpolation == "Lnr " ? PsdGradientInterpolation.Linear : PsdGradientInterpolation.Classic;

            gradient.AngleDegrees = (float)effect.GetNumber("Angl", defaultAngle);
            gradient.Scale = Math.Max(0.01f, (float)(effect.GetNumber("Scl ", 100) / 100));
            gradient.Reverse = effect.GetBoolean("Rvrs");
            gradient.AlignWithLayer = effect.GetBoolean("Algn", true);
            gradient.OffsetXPercent = (float)(offset != null ? offset.GetNumber("Hrzn", 0) : 0);
            gradient.OffsetYPercent = (float)(offset != null ? offset.GetNumber("Vrtc", 0) : 0);
            gradient.Smoothness = grad == null ? 1f : Clamp((float)(grad.GetNumber("Intr", 4096) / 4096), 0f, 1f);

            if (grad != null)
            {
                gradient.Name = Descriptor.Localize(grad.GetString("Nm  "));
                if (grad.GetEnum("GrdF") == "ClNs")
                {
                    gradient.Noise = ParseNoise(grad);
                }

                var colors = grad.GetList("Clrs");
                if (colors != null)
                {
                    foreach (var item in colors)
                    {
                        var stop = item.Object;
                        if (stop == null)
                        {
                            continue;
                        }

                        // Stable sort keeps coincident stops in file order.
                        var color = stop.GetColor("Clr ");
                        if (!color.HasValue)
                        {
                            var black = stop.GetEnum("Type") == "BckC";
                            color = black ? new PsdColor(255, 255, 255) : new PsdColor(0, 0, 0);
                        }

                        gradient._colorEntries.Add(new ColorEntry(
                            Clamp((float)(stop.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                            color.Value,
                            Clamp((float)(stop.GetNumber("Mdpn", 50) / 100), 0f, 1f)));
                    }
                }

                var transparency = grad.GetList("Trns");
                if (transparency != null)
                {
                    foreach (var item in transparency)
                    {
                        var stop = item.Object;
                        if (stop == null)
                        {
                            continue;
                        }

                        gradient._alphaEntries.Add(new AlphaEntry(
                            Clamp((float)(stop.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                            Clamp((float)(stop.GetNumber("Opct", 100) / 100), 0f, 1f),
                            Clamp((float)(stop.GetNumber("Mdpn", 50) / 100), 0f, 1f)));
                    }
                }
            }

            return gradient;
        }

        private static PsdGradientNoise ParseNoise(Descriptor grad)
        {
            var model = grad.GetEnum("ClrS");
            var seed = grad.GetNumber("RndS", 0);
            return new PsdGradientNoise
            {
                Seed = (uint)(seed < 0 ? 0 : seed > uint.MaxValue ? uint.MaxValue : seed),
                Roughness = (int)Clamp((float)grad.GetNumber("Smth", 2048), 0f, 4096f),
                AddTransparency = grad.GetBoolean("ShTr"),
                Model = model == "HSBl" || model == "HSB " || model == "HSBC" ? PsdGradientNoiseModel.Hsb :
                        model == "LbCl" || model == "Lab " || model == "LABC" ? PsdGradientNoiseModel.Lab : PsdGradientNoiseModel.Rgb,
                Minimum = Range(grad, "Mnm ", 0),
                Maximum = Range(grad, "Mxm ", 100),
            };
        }

        private static double[] Range(Descriptor grad, string key, double fallback)
        {
            // Photoshop stores the four channel ranges as a VlLs list of numbers.
            var result = new double[] { fallback, fallback, fallback, fallback };
            var list = grad.GetList(key);
            if (list == null)
            {
                return result;
            }

            for (var i = 0; i < result.Length && i < list.Count; i++)
            {
                var number = list[i].AsNumber();
                if (!double.IsNaN(number))
                {
                    // The noise field works in whole percents (rounded, away from zero).
                    result[i] = Clamp((float)Math.Round(number, MidpointRounding.AwayFromZero), 0f, 100f);
                }
            }

            return result;
        }

        private struct ColorEntry
        {
            public ColorEntry(float location, PsdColor color, float midpoint) : this()
            {
                Location = location;
                Color = color;
                Midpoint = midpoint;
            }

            public float Location;
            public PsdColor Color;
            public float Midpoint;
        }

        private struct AlphaEntry
        {
            public AlphaEntry(float location, float opacity, float midpoint) : this()
            {
                Location = location;
                Opacity = opacity;
                Midpoint = midpoint;
            }

            public float Location;
            public float Opacity;
            public float Midpoint;
        }

        private readonly List<ColorEntry> _colorEntries = new List<ColorEntry>();
        private readonly List<AlphaEntry> _alphaEntries = new List<AlphaEntry>();

        private PsdGradient()
        {
        }

        /// <summary>Builds a gradient model from programmatic stops (Gradient Map parsing).</summary>
        internal static PsdGradient FromStops(PsdGradientColorStop[] colorStops, PsdGradientAlphaStop[] alphaStops, float smoothness, PsdGradientInterpolation interpolation, PsdGradientNoise noise)
        {
            var gradient = new PsdGradient();
            gradient.Interpolation = interpolation;
            gradient.Smoothness = smoothness;
            gradient.Noise = noise;
            gradient.Scale = 1f;
            foreach (var stop in colorStops)
            {
                gradient._colorEntries.Add(new ColorEntry(stop.Location, stop.Color, stop.Midpoint));
            }

            foreach (var stop in alphaStops)
            {
                gradient._alphaEntries.Add(new AlphaEntry(stop.Location, stop.Opacity, stop.Midpoint));
            }

            return gradient;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

        /// <summary>The gradient's name (Nm  key of the Grad object).</summary>
        public string Name { get; private set; }

        public PsdGradientType Type { get; private set; }

        public PsdGradientInterpolation Interpolation { get; private set; }

        /// <summary>Placement angle in degrees (Angl; 90 when absent for layer effects).</summary>
        public float AngleDegrees { get; private set; }

        /// <summary>Placement scale (Scl  / 100; at least 0.01).</summary>
        public float Scale { get; private set; }

        public bool Reverse { get; private set; }

        public bool AlignWithLayer { get; private set; }

        /// <summary>Offset of the gradient center in percent of the bounds (Ofst Hrzn).</summary>
        public float OffsetXPercent { get; private set; }

        public float OffsetYPercent { get; private set; }

        /// <summary>Smoothness 0..1 (Intr / 4096).</summary>
        public float Smoothness { get; private set; }

        /// <summary>Noise settings when the gradient form is ClNs; null for stop gradients.</summary>
        public PsdGradientNoise Noise { get; private set; }

        /// <summary>Color stops sorted by location (coincident stops keep file order).</summary>
        public IReadOnlyList<PsdGradientColorStop> ColorStops
        {
            get
            {
                var stops = new List<PsdGradientColorStop>(_colorEntries.Count);
                foreach (var entry in _colorEntries)
                {
                    stops.Add(new PsdGradientColorStop(entry.Location, entry.Color, entry.Midpoint));
                }

                stops.Sort((a, b) => a.Location.CompareTo(b.Location));
                return stops;
            }
        }

        /// <summary>Transparency stops sorted by location.</summary>
        public IReadOnlyList<PsdGradientAlphaStop> AlphaStops
        {
            get
            {
                var stops = new List<PsdGradientAlphaStop>(_alphaEntries.Count);
                foreach (var entry in _alphaEntries)
                {
                    stops.Add(new PsdGradientAlphaStop(entry.Location, entry.Opacity, entry.Midpoint));
                }

                stops.Sort((a, b) => a.Location.CompareTo(b.Location));
                return stops;
            }
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(Type).Append(' ');
            if (!string.IsNullOrEmpty(Name))
            {
                builder.Append('"').Append(Name).Append("\" ");
            }

            builder.Append(string.Format("angle {0:0.#} scale {1:0.##}", AngleDegrees, Scale));
            if (Reverse)
            {
                builder.Append(" reversed");
            }

            builder.Append(' ').Append(ColorStops.Count).Append(" color stops");
            if (AlphaStops.Count > 0)
            {
                builder.Append(", ").Append(AlphaStops.Count).Append(" alpha stops");
            }

            if (Noise != null)
            {
                builder.Append(", ").Append(Noise);
            }

            return builder.ToString();
        }
    }
}
