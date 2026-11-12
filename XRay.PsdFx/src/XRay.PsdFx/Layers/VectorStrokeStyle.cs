using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>Where a vector stroke sits relative to its path.</summary>
    public enum VectorStrokeAlignment
    {
        Center = 0,
        Inside = 1,
        Outside = 2,
    }

    /// <summary>End caps of open vector strokes and dashes.</summary>
    public enum VectorStrokeCap
    {
        Butt = 0,
        Round = 1,
        Square = 2,
    }

    /// <summary>Corner joins of vector strokes.</summary>
    public enum VectorStrokeJoin
    {
        Miter = 0,
        Round = 1,
        Bevel = 2,
    }

    /// <summary>
    /// A shape layer's vector stroke and fill switch, from the <c>vstk</c> block
    /// (descriptor class <c>strokeStyle</c>). Parsing follows
    /// <c>parse_vector_stroke_block</c> in the reference reader.
    /// </summary>
    public sealed class VectorStrokeStyle
    {
        private const double MaxWidth = 100000;

        private VectorStrokeStyle()
        {
            Dashes = new List<double>();
        }

        /// <summary>Whether the stroke is drawn (strokeEnabled, default false).</summary>
        public bool Enabled { get; private set; }

        /// <summary>Whether the shape's fill content is drawn (fillEnabled, default true).</summary>
        public bool FillEnabled { get; private set; }

        /// <summary>Stroke width in pixels (point widths convert through strokeStyleResolution).</summary>
        public double Width { get; private set; }

        /// <summary>Miter limit as a ratio of the half width (Photoshop's default is 100).</summary>
        public double MiterLimit { get; private set; }

        public VectorStrokeCap Cap { get; private set; }

        public VectorStrokeJoin Join { get; private set; }

        public VectorStrokeAlignment Alignment { get; private set; }

        /// <summary>Alternating dash and gap lengths in stroke-width multiples; empty for a solid stroke.</summary>
        public IReadOnlyList<double> Dashes { get; private set; }

        /// <summary>Dash phase in stroke-width multiples.</summary>
        public double DashOffset { get; private set; }

        public PsdBlendMode BlendMode { get; private set; }

        /// <summary>Stroke opacity 0..1 (strokeStyleOpacity).</summary>
        public double Opacity { get; private set; }

        /// <summary>
        /// The stroke paint (strokeStyleContent): a solidColorLayer, gradientLayer
        /// or patternLayer object, or null when absent.
        /// </summary>
        public Descriptor Content { get; private set; }

        /// <summary>Fill block key equivalent of Content: SoCo, GdFl, PtFl, or null.</summary>
        public string ContentKey { get; private set; }

        /// <summary>Parses a <c>vstk</c> payload; returns null when it is not a <c>strokeStyle</c> descriptor.</summary>
        internal static VectorStrokeStyle Parse(byte[] data)
        {
            Descriptor descriptor;
            try
            {
                // Vector descriptor blocks start with descriptor version 16, or with a
                // block version followed by 16 (reference read_block_descriptor).
                var reader = new BigEndianReader(data);
                if (reader.Remaining < 8)
                {
                    return null;
                }

                if (reader.ReadUInt32() != 16 && reader.ReadUInt32() != 16)
                {
                    return null;
                }

                descriptor = Descriptor.Read(reader);
            }
            catch (PsdFormatException)
            {
                return null;
            }

            if (descriptor.ClassId != "strokeStyle")
            {
                return null;
            }

            var resolution = descriptor.GetNumber("strokeStyleResolution", 72);
            if (double.IsNaN(resolution) || double.IsInfinity(resolution) || resolution <= 0)
            {
                resolution = 72;
            }

            var width = Sanitize(LengthInPixels(descriptor["strokeStyleLineWidth"], resolution), 0, MaxWidth);
            var dashes = new List<double>();
            var list = descriptor.GetList("strokeStyleLineDashSet");
            if (list != null)
            {
                foreach (var entry in list)
                {
                    var value = entry.AsNumber();
                    if (!double.IsNaN(value) && !double.IsInfinity(value))
                    {
                        dashes.Add(Compat.Clamp(value, 0, MaxWidth));
                    }
                }
            }

            var offset = LengthInPixels(descriptor["strokeStyleLineDashOffset"], resolution);
            var content = descriptor.GetObject("strokeStyleContent");
            string contentKey;
            if (content == null)
            {
                contentKey = null;
            }
            else if (content.ClassId == "solidColorLayer")
            {
                contentKey = "SoCo";
            }
            else if (content.ClassId == "gradientLayer")
            {
                contentKey = "GdFl";
            }
            else if (content.ClassId == "patternLayer")
            {
                contentKey = "PtFl";
            }
            else
            {
                contentKey = null;
            }

            var mode = descriptor.GetEnum("strokeStyleBlendMode");
            var result = new VectorStrokeStyle();
            result.Enabled = descriptor.GetBoolean("strokeEnabled", false);
            result.FillEnabled = descriptor.GetBoolean("fillEnabled", true);
            result.Width = width;
            result.MiterLimit = Sanitize(descriptor.GetNumber("strokeStyleMiterLimit", 100), 1, 1e6);
            var cap = descriptor.GetEnum("strokeStyleLineCapType");
            result.Cap = cap == "strokeStyleRoundCap" ? VectorStrokeCap.Round : cap == "strokeStyleSquareCap" ? VectorStrokeCap.Square : VectorStrokeCap.Butt;
            var join = descriptor.GetEnum("strokeStyleLineJoinType");
            result.Join = join == "strokeStyleRoundJoin" ? VectorStrokeJoin.Round : join == "strokeStyleBevelJoin" ? VectorStrokeJoin.Bevel : VectorStrokeJoin.Miter;
            var alignment = descriptor.GetEnum("strokeStyleLineAlignment");
            result.Alignment = alignment == "strokeStyleAlignInside" ? VectorStrokeAlignment.Inside : alignment == "strokeStyleAlignOutside" ? VectorStrokeAlignment.Outside : VectorStrokeAlignment.Center;
            result.Dashes = dashes;
            result.DashOffset = width > 0 && !double.IsNaN(offset) && !double.IsInfinity(offset) ? Compat.Clamp(offset / width, -1e6, 1e6) : 0;
            result.BlendMode = mode != null ? BlendModeKeys.FromDescriptorEnum(mode) : PsdBlendMode.Normal;
            result.Opacity = Sanitize(descriptor.GetNumber("strokeStyleOpacity", 100), 0, 100) / 100.0;
            result.Content = contentKey == null ? null : content;
            result.ContentKey = contentKey;
            return result;
        }

        private static double Sanitize(double value, double min, double max)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) ? Compat.Clamp(value, min, max) : min;
        }

        // Point lengths scale by resolution / 72; pixel and unitless values pass through.
        private static double LengthInPixels(DescriptorValue value, double resolution)
        {
            if (value == null)
            {
                return 0;
            }

            var number = value.AsNumber();
            if (double.IsNaN(number))
            {
                return 0;
            }

            return value.Type == DescriptorValueType.UnitFloat && value.Unit == "#Pnt" ? number * resolution / 72.0 : number;
        }
    }
}