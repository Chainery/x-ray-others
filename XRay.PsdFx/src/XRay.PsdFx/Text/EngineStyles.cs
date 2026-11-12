using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Key names of one character style sheet flavor. TySh EngineData names its
    /// keys (<c>/FontSize</c>); the document-level Txt2 block numbers them
    /// (<c>/1</c>). A null key is a property the flavor is not known to store.
    /// </summary>
    internal sealed class StyleKeys
    {
        public string Font;
        public string FontSize;
        public string FauxBold;
        public string FauxItalic;
        public string AutoLeading;
        public string Leading;
        public string HorizontalScale;
        public string VerticalScale;
        public string Tracking;
        public string BaselineShift;
        public string AutoKerning;
        public string Kerning;
        public string FontCaps;
        public string FontBaseline;
        public string Underline;
        public string Strikethrough;
        public string Ligatures;
        public string DLigatures;
        public string BaselineDirection;
        public string Language;
        public string NoBreak;
        public string FillColor;
        public string StrokeColor;
        public string FillFlag;
        public string StrokeFlag;
        public string OutlineWidth;

        public static readonly StyleKeys TypeTool = new StyleKeys
        {
            Font = "Font",
            FontSize = "FontSize",
            FauxBold = "FauxBold",
            FauxItalic = "FauxItalic",
            AutoLeading = "AutoLeading",
            Leading = "Leading",
            HorizontalScale = "HorizontalScale",
            VerticalScale = "VerticalScale",
            Tracking = "Tracking",
            BaselineShift = "BaselineShift",
            AutoKerning = "AutoKerning",
            Kerning = "Kerning",
            FontCaps = "FontCaps",
            FontBaseline = "FontBaseline",
            Underline = "Underline",
            Strikethrough = "Strikethrough",
            Ligatures = "Ligatures",
            DLigatures = "DLigatures",
            BaselineDirection = "BaselineDirection",
            Language = "Language",
            NoBreak = "NoBreak",
            FillColor = "FillColor",
            StrokeColor = "StrokeColor",
            FillFlag = "FillFlag",
            StrokeFlag = "StrokeFlag",
            OutlineWidth = "OutlineWidth",
        };

        public static readonly StyleKeys TextEngine = new StyleKeys
        {
            Font = "0",
            FontSize = "1",
            FauxBold = "2",
            FauxItalic = "3",
            AutoLeading = "4",
            Leading = "5",
            HorizontalScale = "6",
            VerticalScale = "7",
            Tracking = "8",
            BaselineShift = "9",

            // 11 is the kerning method (0 manual, 1 metrics, 2 optical); nonzero reads as auto kerning.
            AutoKerning = "11",

            // The keys below follow the Adobe text engine's style sheet order, which matches all
            // 89 values of Photoshop's default sheet (/1/2) and ag-psd's engineData2.ts. FontCaps
            // (SmallCaps 1, masks.psd "sign in") and BaselineDirection (1 and 2, the vertical
            // fixtures) are confirmed by TySh runs of the same text; FontBaseline, Strikethrough
            // (15, a position number), Underline (16, a position number), DLigatures and NoBreak
            // only by matching zero and false values (docs/text.md, "Txt2").
            FontCaps = "12",
            FontBaseline = "13",
            Strikethrough = "15",
            Underline = "16",
            Ligatures = "18",
            DLigatures = "19",
            BaselineDirection = "35",
            Language = "38",
            NoBreak = "52",
            FillColor = "53",
            StrokeColor = "54",
        };
    }

    /// <summary>Key names of one paragraph sheet flavor (see <see cref="StyleKeys"/>).</summary>
    internal sealed class ParagraphKeys
    {
        public string Justification;
        public string FirstLineIndent;
        public string StartIndent;
        public string EndIndent;
        public string SpaceBefore;
        public string SpaceAfter;
        public string AutoLeading;
        public string AutoHyphenate;
        public string Direction;
        public string ListStyle;
        public string ListTier;

        public static readonly ParagraphKeys TypeTool = new ParagraphKeys
        {
            Justification = "Justification",
            FirstLineIndent = "FirstLineIndent",
            StartIndent = "StartIndent",
            EndIndent = "EndIndent",
            SpaceBefore = "SpaceBefore",
            SpaceAfter = "SpaceAfter",
            AutoLeading = "AutoLeading",
            AutoHyphenate = "AutoHyphenate",
            Direction = "ParagraphDirection",
        };

        public static readonly ParagraphKeys TextEngine = new ParagraphKeys
        {
            Justification = "0",
            FirstLineIndent = "1",
            StartIndent = "2",
            EndIndent = "3",
            SpaceBefore = "4",
            SpaceAfter = "5",
            AutoLeading = "7",
            AutoHyphenate = "9",
            Direction = "33",
            ListStyle = "36",
            ListTier = "37",
        };
    }

    /// <summary>
    /// Builds <see cref="TextStyleRun"/> and <see cref="TextParagraph"/> values from
    /// EngineData sheets of either flavor. Lookups fall back from the run's own
    /// (often sparse) sheet to the document's normal sheet.
    /// </summary>
    internal static class EngineStyles
    {
        /// <summary>Photoshop's ceiling for a type size in pixels; larger values mark a misread record.</summary>
        public const double MaxTextSize = 8192;

        public delegate EngineValue Lookup(string key);

        public static double? Number(EngineValue value)
        {
            if (value != null && value.Kind == EngineValueKind.Number && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number))
            {
                return value.Number;
            }

            return null;
        }

        public static bool? Flag(EngineValue value)
        {
            if (value == null)
            {
                return null;
            }

            if (value.Kind == EngineValueKind.Boolean)
            {
                return value.Boolean;
            }

            if (value.Kind == EngineValueKind.Number && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number))
            {
                return value.Number != 0;
            }

            return null;
        }

        /// <summary>A finite number as an int, or null when it is missing or out of range.</summary>
        public static int? Integer(EngineValue value)
        {
            var number = Number(value);
            if (number != null && number >= int.MinValue && number <= int.MaxValue)
            {
                return (int)Math.Round(number.Value);
            }

            return null;
        }

        /// <summary>Run lengths as non-negative ints (malformed entries count as 0).</summary>
        public static int Length(EngineValue value)
        {
            return Math.Max(0, Integer(value) ?? 0);
        }

        /// <summary>
        /// Decodes a text-engine color: TySh <c>&lt;&lt; /Type t /Values [...] &gt;&gt;</c> or
        /// Txt2 <c>&lt;&lt; /99 /SimplePaint /0 &lt;&lt; /0 t /1 [...] &gt;&gt; &gt;&gt;</c>.
        /// Type 1 is [alpha, red, green, blue], 2 is [alpha, cyan, magenta, yellow, black]
        /// ink fractions, 0 is [alpha, level] with 0 black. With <paramref name="colors"/>,
        /// the values convert like the document's pixels.
        /// </summary>
        public static PsdColor? Color(EngineValue node, DocumentColors colors = null)
        {
            if (node == null || node.Kind != EngineValueKind.Dictionary)
            {
                return null;
            }

            EngineValue values;
            int type;
            var named = node["Values"];
            if (named != null && named.Kind == EngineValueKind.List)
            {
                values = named;
                type = Integer(node["Type"]) ?? 1;
            }
            else
            {
                var paint = node["0"];
                var numbered = paint == null ? null : paint["1"];
                if (paint != null && paint.Kind == EngineValueKind.Dictionary && numbered != null && numbered.Kind == EngineValueKind.List)
                {
                    values = numbered;
                    type = Integer(paint["0"]) ?? 1;
                }
                else
                {
                    return null;
                }
            }

            var v = new double[values.Items.Count];
            for (var i = 0; i < v.Length; i++)
            {
                var component = Number(values.Items[i]);
                if (component == null)
                {
                    return null;
                }

                v[i] = component.Value;
            }

            switch (type)
            {
                case 0:
                    if (v.Length < 2)
                    {
                        return null;
                    }

                    return colors != null
                        ? colors.FromGray(v[1], Unit(v[0]))
                        : (PsdColor?)new PsdColor(Unit(v[1]), Unit(v[1]), Unit(v[1]), Unit(v[0]));
                case 2:
                    if (v.Length < 5)
                    {
                        return null;
                    }

                    if (colors != null)
                    {
                        return colors.FromInk(v[1], v[2], v[3], v[4], Unit(v[0]));
                    }

                    var k = 1 - Compat.Clamp(v[4], 0, 1);
                    return new PsdColor(
                        Unit((1 - Compat.Clamp(v[1], 0, 1)) * k),
                        Unit((1 - Compat.Clamp(v[2], 0, 1)) * k),
                        Unit((1 - Compat.Clamp(v[3], 0, 1)) * k),
                        Unit(v[0]));
                default:
                    if (v.Length < 4)
                    {
                        return null;
                    }

                    // Old writers stored 0..255 components; a value above 1 marks that scale.
                    var normalized = v[1] <= 1 && v[2] <= 1 && v[3] <= 1;
                    var red = normalized ? Unit(v[1]) : ClampByte(v[1]);
                    var green = normalized ? Unit(v[2]) : ClampByte(v[2]);
                    var blue = normalized ? Unit(v[3]) : ClampByte(v[3]);
                    var alpha = Unit(Math.Min(1, v[0]));
                    return colors != null
                        ? colors.FromRgb(red / 255.0, green / 255.0, blue / 255.0, alpha)
                        : (PsdColor?)new PsdColor(red, green, blue, alpha);
            }
        }

        /// <summary>
        /// Builds one style run. <paramref name="lengthScale"/> converts the sheet's
        /// length values (size, leading, baseline shift) to document pixels; it is 1
        /// for TySh and for Txt2 runs whose unit flag says pixels.
        /// </summary>
        public static TextStyleRun Run(
            int start,
            int length,
            string segment,
            Lookup lookup,
            StyleKeys keys,
            IReadOnlyList<string> fontNames,
            double lengthScale,
            DocumentColors colors = null)
        {
            var fontName = (string)null;
            var fontIndex = Integer(Get(lookup, keys.Font));
            if (fontIndex != null && fontIndex >= 0 && fontIndex < fontNames.Count)
            {
                fontName = fontNames[fontIndex.Value];
            }

            if (string.IsNullOrEmpty(fontName))
            {
                fontName = null;
            }

            var fontSize = Scaled(lookup, keys.FontSize, lengthScale);
            if (fontSize == null || fontSize <= 0 || fontSize > MaxTextSize)
            {
                fontSize = null;
            }

            var autoLeading = Flag(Get(lookup, keys.AutoLeading)) ?? true;

            // Photoshop records a stale /Leading even for auto-leading runs; only fixed leading counts.
            var leading = autoLeading ? (double?)null : Scaled(lookup, keys.Leading, lengthScale);
            if (leading != null && leading <= 0)
            {
                leading = null;
            }

            var tracking = Number(Get(lookup, keys.Tracking));
            if (tracking != null && Math.Abs(tracking.Value) >= 10000)
            {
                tracking = null;
            }

            var caps = Integer(Get(lookup, keys.FontCaps));
            var baselinePosition = Integer(Get(lookup, keys.FontBaseline));
            var language = Integer(Get(lookup, keys.Language));
            return new TextStyleRun(
                start,
                length,
                TextLayerInfo.NormalizeText(segment),
                fontName,
                fontSize,
                Color(Get(lookup, keys.FillColor), colors),
                Flag(Get(lookup, keys.FauxBold)) ?? false,
                Flag(Get(lookup, keys.FauxItalic)) ?? false,
                tracking,
                leading)
            {
                AutoLeading = autoLeading,
                HorizontalScale = Scale(Number(Get(lookup, keys.HorizontalScale))),
                VerticalScale = Scale(Number(Get(lookup, keys.VerticalScale))),
                BaselineShift = Scaled(lookup, keys.BaselineShift, lengthScale) ?? 0,
                AutoKerning = Flag(Get(lookup, keys.AutoKerning)) ?? true,
                Kerning = Number(Get(lookup, keys.Kerning)) ?? 0,
                Caps = caps != null && caps >= 0 && caps <= 2 ? (TextCaps)caps.Value : TextCaps.Normal,
                BaselinePosition = baselinePosition != null && baselinePosition >= 0 && baselinePosition <= 2
                    ? (TextBaselinePosition)baselinePosition.Value
                    : TextBaselinePosition.Normal,
                Underline = Flag(Get(lookup, keys.Underline)) ?? false,
                Strikethrough = Flag(Get(lookup, keys.Strikethrough)) ?? false,
                Ligatures = Flag(Get(lookup, keys.Ligatures)) ?? true,
                DiscretionaryLigatures = Flag(Get(lookup, keys.DLigatures)) ?? false,
                BaselineDirection = Integer(Get(lookup, keys.BaselineDirection)) ?? 0,
                Language = language,
                NoBreak = Flag(Get(lookup, keys.NoBreak)) ?? false,
                StrokeColor = Color(Get(lookup, keys.StrokeColor), colors),
                FillEnabled = Flag(Get(lookup, keys.FillFlag)) ?? true,
                StrokeEnabled = Flag(Get(lookup, keys.StrokeFlag)) ?? false,
                StrokeWidth = Number(Get(lookup, keys.OutlineWidth)),
            };
        }

        /// <summary>Builds one paragraph run (see <see cref="Run"/> for <paramref name="lengthScale"/>).</summary>
        public static TextParagraph Paragraph(int start, int length, string segment, Lookup lookup, ParagraphKeys keys, double lengthScale)
        {
            var justification = Integer(Get(lookup, keys.Justification)) ?? 0;
            var fraction = Number(Get(lookup, keys.AutoLeading));
            var directionValue = Integer(Get(lookup, keys.Direction));
            var direction = directionValue == 0
                ? TextDirection.LeftToRight
                : directionValue == 1 ? TextDirection.RightToLeft : TextDirection.Auto;
            var listNode = Get(lookup, keys.ListStyle);
            int? listStyle = listNode != null && listNode.Kind == EngineValueKind.Number ? Integer(listNode) : null;
            return new TextParagraph(
                start,
                length,
                TextLayerInfo.NormalizeText(segment),
                (TextJustification)Compat.Clamp(justification, 0, 6))
            {
                FirstLineIndent = Scaled(lookup, keys.FirstLineIndent, lengthScale) ?? 0,
                StartIndent = Scaled(lookup, keys.StartIndent, lengthScale) ?? 0,
                EndIndent = Scaled(lookup, keys.EndIndent, lengthScale) ?? 0,
                SpaceBefore = Scaled(lookup, keys.SpaceBefore, lengthScale) ?? 0,
                SpaceAfter = Scaled(lookup, keys.SpaceAfter, lengthScale) ?? 0,
                AutoLeadingFraction = fraction != null && fraction > 0.01 && fraction < 10 ? fraction.Value : 1.2,
                AutoHyphenate = Flag(Get(lookup, keys.AutoHyphenate)) ?? false,
                Direction = direction,
                ListStyleIndex = listStyle != null && listStyle >= 0 ? listStyle : null,
                ListTier = Math.Max(0, Integer(Get(lookup, keys.ListTier)) ?? 0),
            };
        }

        /// <summary>The part of <paramref name="text"/> a run covers, clipped to the text.</summary>
        public static string Segment(string text, int start, int length)
        {
            if (start >= text.Length || length <= 0)
            {
                return string.Empty;
            }

            return text.Substring(start, Math.Min(length, text.Length - start));
        }

        private static EngineValue Get(Lookup lookup, string key)
        {
            return key == null ? null : lookup(key);
        }

        private static double? Scaled(Lookup lookup, string key, double lengthScale)
        {
            var value = Number(Get(lookup, key));
            return value == null ? (double?)null : value.Value * lengthScale;
        }

        private static double Scale(double? value)
        {
            return value != null && value > 0.01 && value < 100 ? value.Value : 1;
        }

        private static byte Unit(double value)
        {
            return (byte)Compat.Clamp(Math.Round(value * 255), 0, 255);
        }

        private static byte ClampByte(double value)
        {
            return (byte)Compat.Clamp(Math.Round(value), 0, 255);
        }
    }
}
