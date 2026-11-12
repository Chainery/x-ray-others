using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>Text direction of a type layer.</summary>
    public enum TextOrientation
    {
        Horizontal,
        Vertical,
    }

    /// <summary>Paragraph alignment from the text engine (<c>Justification</c>).</summary>
    public enum TextJustification
    {
        Left = 0,
        Right = 1,
        Center = 2,
        JustifyLastLeft = 3,
        JustifyLastRight = 4,
        JustifyLastCenter = 5,
        JustifyAll = 6,
    }

    /// <summary>A run of characters sharing one character style.</summary>
    public sealed class TextStyleRun
    {
        public TextStyleRun(
            int start,
            int length,
            string text,
            string fontName,
            double? fontSize,
            PsdColor? fillColor,
            bool fauxBold,
            bool fauxItalic,
            double? tracking,
            double? leading)
        {
            Start = start;
            Length = length;
            Text = text;
            FontName = fontName;
            FontSize = fontSize;
            FillColor = fillColor;
            FauxBold = fauxBold;
            FauxItalic = fauxItalic;
            Tracking = tracking;
            Leading = leading;
        }

        /// <summary>First UTF-16 unit of the run in <see cref="TextLayerInfo.RawText"/>.</summary>
        public int Start { get; private set; }

        /// <summary>Run length in UTF-16 units (Photoshop counts the paragraph separators).</summary>
        public int Length { get; private set; }

        /// <summary>The run's text, separators normalized to \n.</summary>
        public string Text { get; private set; }

        /// <summary>PostScript name of the font.</summary>
        public string FontName { get; private set; }

        /// <summary>Size in engine units (document pixels before the layer transform).</summary>
        public double? FontSize { get; private set; }

        /// <summary>Text color.</summary>
        public PsdColor? FillColor { get; private set; }

        /// <summary>Synthetic emboldening of the named face.</summary>
        public bool FauxBold { get; private set; }

        /// <summary>Synthetic slant of the named face.</summary>
        public bool FauxItalic { get; private set; }

        /// <summary>Tracking in thousandths of an em.</summary>
        public double? Tracking { get; private set; }

        /// <summary>Fixed leading in engine units, or null for auto leading.</summary>
        public double? Leading { get; private set; }

        /// <summary>Photoshop auto leading: the paragraph's <see cref="TextParagraph.AutoLeadingFraction"/> times the size.</summary>
        public bool AutoLeading { get; set; } = true;

        /// <summary>Glyph width scale (1 = 100 percent).</summary>
        public double HorizontalScale { get; set; } = 1;

        /// <summary>Glyph height scale (1 = 100 percent).</summary>
        public double VerticalScale { get; set; } = 1;

        /// <summary>Baseline shift in engine units (positive moves the glyphs up).</summary>
        public double BaselineShift { get; set; }

        /// <summary>Metrics (automatic) kerning.</summary>
        public bool AutoKerning { get; set; } = true;

        /// <summary>Manual kerning in thousandths of an em.</summary>
        public double Kerning { get; set; }

        public TextCaps Caps { get; set; }

        public TextBaselinePosition BaselinePosition { get; set; }

        public bool Underline { get; set; }

        public bool Strikethrough { get; set; }

        /// <summary>Standard ligatures.</summary>
        public bool Ligatures { get; set; } = true;

        /// <summary>Discretionary ligatures.</summary>
        public bool DiscretionaryLigatures { get; set; }

        /// <summary>Vertical type only: 1 Roman glyphs upright, 2 rotated ("Standard Vertical Roman Alignment"), 0 unspecified.</summary>
        public int BaselineDirection { get; set; }

        /// <summary>Photoshop's language index, when stored.</summary>
        public int? Language { get; set; }

        /// <summary>"No Break": the run must not be split across lines.</summary>
        public bool NoBreak { get; set; }

        /// <summary>Outline (stroke) color of the glyphs.</summary>
        public PsdColor? StrokeColor { get; set; }

        /// <summary>Whether the glyphs are filled (<c>/FillFlag</c>).</summary>
        public bool FillEnabled { get; set; } = true;

        /// <summary>Whether the glyphs are stroked (<c>/StrokeFlag</c>).</summary>
        public bool StrokeEnabled { get; set; }

        /// <summary>Glyph outline width (<c>/OutlineWidth</c>), when stored.</summary>
        public double? StrokeWidth { get; set; }

        /// <summary>Display family name, when the record stores one (Photoshop 5 <c>tySh</c> faces).</summary>
        public string FontFamily { get; set; }

        /// <summary>Display style name (<c>Bold</c>, <c>Italic</c>...), when the record stores one (Photoshop 5 <c>tySh</c> faces).</summary>
        public string FontStyleName { get; set; }
    }

    /// <summary>A paragraph with its alignment and metrics.</summary>
    public sealed class TextParagraph
    {
        public TextParagraph(int start, int length, string text, TextJustification justification)
        {
            Start = start;
            Length = length;
            Text = text;
            Justification = justification;
        }

        /// <summary>First UTF-16 unit of the paragraph in <see cref="TextLayerInfo.RawText"/>.</summary>
        public int Start { get; private set; }

        /// <summary>Length in UTF-16 units, separator included.</summary>
        public int Length { get; private set; }

        /// <summary>The paragraph text without its separator.</summary>
        public string Text { get; private set; }

        /// <summary>Alignment.</summary>
        public TextJustification Justification { get; private set; }

        /// <summary>First-line indent in engine units; negative with a positive <see cref="StartIndent"/> is a hanging indent.</summary>
        public double FirstLineIndent { get; set; }

        /// <summary>Indent at the line start (left for horizontal left-to-right text), engine units.</summary>
        public double StartIndent { get; set; }

        /// <summary>Indent at the line end, engine units.</summary>
        public double EndIndent { get; set; }

        /// <summary>Space before the paragraph, engine units.</summary>
        public double SpaceBefore { get; set; }

        /// <summary>Space after the paragraph, engine units.</summary>
        public double SpaceAfter { get; set; }

        /// <summary>Auto leading as a fraction of the font size (Photoshop default 1.2).</summary>
        public double AutoLeadingFraction { get; set; } = 1.2;

        public bool AutoHyphenate { get; set; }

        /// <summary>Paragraph base direction. Photoshop stores it only in the document's Txt2 block.</summary>
        public TextDirection Direction { get; set; }

        /// <summary>Bulleted or numbered list style (an index into the Txt2 list style set), or null for none. Txt2 only.</summary>
        public int? ListStyleIndex { get; set; }

        /// <summary>List nesting level (0 is the outermost). Txt2 only.</summary>
        public int ListTier { get; set; }

        /// <summary>A copy with direction, list style and tier replaced (the Txt2 merge).</summary>
        internal TextParagraph WithDirection(TextDirection direction, int? listStyleIndex, int listTier)
        {
            var copy = new TextParagraph(Start, Length, Text, Justification)
            {
                FirstLineIndent = FirstLineIndent,
                StartIndent = StartIndent,
                EndIndent = EndIndent,
                SpaceBefore = SpaceBefore,
                SpaceAfter = SpaceAfter,
                AutoLeadingFraction = AutoLeadingFraction,
                AutoHyphenate = AutoHyphenate,
                Direction = direction,
                ListStyleIndex = listStyleIndex,
                ListTier = listTier,
            };
            return copy;
        }
    }

    /// <summary>
    /// The type-tool data of a text layer (<c>TySh</c>, or a Photoshop 5 <c>tySh</c>):
    /// the plain text, its transform and geometry, and the character/paragraph
    /// styling decoded from EngineData. Gaps in the layer's own record are filled
    /// from the document's <c>Txt2</c> text engine block when it has the layer's object.
    /// </summary>
    public sealed class TextLayerInfo
    {
        /// <summary>Text with paragraph breaks normalized to \n.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>The text exactly as stored (Photoshop uses \r between paragraphs).</summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>Affine transform <c>xx, xy, yx, yy, tx, ty</c> from text space to document pixels.</summary>
        public double[] Transform { get; set; } = new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0 };

        public TextOrientation Orientation { get; set; }

        /// <summary>True for paragraph (box) text, false for point text.</summary>
        public bool IsParagraphText { get; set; }

        /// <summary>Point, box, type on a path, or another shape.</summary>
        public TextShapeKind ShapeKind { get; set; }

        /// <summary>
        /// The path of type on a path (<see cref="TextShapeKind.Path"/>), from the layer's Txt2 frame.
        /// Photoshop's TySh record stores such a layer as box text (<c>ShapeType 1</c>, the path's
        /// extent as <c>BoxBounds</c>); only the Txt2 frame says it follows a path.
        /// </summary>
        public TextPath Path { get; set; }

        /// <summary>The raw EngineData <c>ShapeType</c>, when stored.</summary>
        public int? ShapeType { get; set; }

        public IReadOnlyList<TextStyleRun> StyleRuns { get; set; } = new TextStyleRun[0];

        public IReadOnlyList<TextParagraph> Paragraphs { get; set; } = new TextParagraph[0];

        /// <summary>Distinct font PostScript names referenced by the style runs, in first-use order.</summary>
        public IReadOnlyList<string> Fonts { get; set; } = new string[0];

        /// <summary>The <c>TxLr</c> text descriptor.</summary>
        public Descriptor Descriptor { get; set; }

        /// <summary>The <c>warp</c> descriptor, when present.</summary>
        public Descriptor Warp { get; set; }

        /// <summary>Warp Text settings decoded from <see cref="Warp"/>; check <see cref="TextWarp.IsIdentity"/>.</summary>
        public TextWarp WarpSettings { get; set; }

        /// <summary>The parsed text-engine tree, when it decoded.</summary>
        public EngineValue EngineData { get; set; }

        /// <summary>Index of the layer's object in the document's Txt2 block (<c>TextIndex</c>).</summary>
        public int? TextIndex { get; set; }

        /// <summary>The layer's object in the document's Txt2 block, when the document has one.</summary>
        public TextEngineObject TextEngineObject { get; set; }

        /// <summary>Anti-aliasing method, when stored.</summary>
        public TextAntiAlias? AntiAlias { get; set; }

        /// <summary>The descriptor's <c>bounds</c>: the layout box in text space (for point text, the line box around the baseline).</summary>
        public TextBounds? Bounds { get; set; }

        /// <summary>The descriptor's <c>boundingBox</c>: the tight ink box in text space.</summary>
        public TextBounds? BoundingBox { get; set; }

        /// <summary>The paragraph-text frame (EngineData <c>BoxBounds</c>) in text space.</summary>
        public TextBounds? BoxBounds { get; set; }

        /// <summary>Which stored records the values came from.</summary>
        public TextDataOrigin Origin { get; set; }

        /// <summary>Normalizes Photoshop paragraph separators (\r, \r\n, ETX line breaks) to \n and trims the trailing break.</summary>
        public static string NormalizeText(string text)
        {
            if (text.Length == 0)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r')
                {
                    builder.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else if (c == '\u0003')
                {
                    builder.Append('\n');
                }
                else
                {
                    builder.Append(c);
                }
            }

            while (builder.Length > 0 && builder[builder.Length - 1] == '\n')
            {
                builder.Length--;
            }

            return builder.ToString();
        }

        /// <summary>Parses a <c>TySh</c> tagged-block payload. Returns null when it does not decode.</summary>
        public static TextLayerInfo Parse(byte[] payload)
        {
            return Parse(payload, null);
        }

        /// <summary>Parses a <c>TySh</c> payload whose colors convert like the document's pixels.</summary>
        internal static TextLayerInfo Parse(byte[] payload, DocumentColors colors)
        {
            try
            {
                var reader = new BigEndianReader(payload);
                var version = reader.ReadUInt16();
                if (version != 1)
                {
                    return null;
                }

                var transform = ReadTransform(reader);
                reader.ReadUInt16(); // text version (50)
                var text = Descriptor.ReadVersioned(reader);
                Descriptor warp = null;
                if (reader.Remaining >= 6)
                {
                    try
                    {
                        reader.ReadUInt16(); // warp version
                        warp = Descriptor.ReadVersioned(reader);
                    }
                    catch (PsdFormatException)
                    {
                        warp = null;
                    }
                }

                return FromDescriptor(text, warp, transform, colors);
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Recovers the transform and <c>TextIndex</c> from a <c>TySh</c> payload whose
        /// descriptor does not decode, so the layer can still be matched to its Txt2 object.
        /// </summary>
        internal static SalvagedTySh Salvage(byte[] payload)
        {
            double[] transform = null;
            if (payload.Length >= 50 && (payload[0] == 0) && (payload[1] == 1))
            {
                transform = new double[6];
                for (var i = 0; i < 6; i++)
                {
                    var value = ((ulong)ChannelCodec.ReadUInt32BE(payload, 2 + (i * 8)) << 32) | ChannelCodec.ReadUInt32BE(payload, 6 + (i * 8));
                    var bits = unchecked((long)value);
                    var candidate = BitConverter.Int64BitsToDouble(bits);
                    transform[i] = double.IsNaN(candidate) || double.IsInfinity(candidate) ? (i == 0 || i == 3 ? 1 : 0) : candidate;
                }
            }

            int? textIndex = null;
            var key = new[] { (byte)'T', (byte)'e', (byte)'x', (byte)'t', (byte)'I', (byte)'n', (byte)'d', (byte)'e', (byte)'x', (byte)'l', (byte)'o', (byte)'n', (byte)'g' };
            var at = IndexOf(payload, key);
            if (at >= 0 && at + key.Length + 4 <= payload.Length)
            {
                textIndex = (int)ChannelCodec.ReadUInt32BE(payload, at + key.Length);
            }

            return new SalvagedTySh(transform, textIndex);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>A copy with the given members replaced (the Txt2 merge in <see cref="TextEngineResolver"/>).</summary>
        internal TextLayerInfo With(
            string rawText = null,
            IReadOnlyList<TextStyleRun> styleRuns = null,
            IReadOnlyList<TextParagraph> paragraphs = null,
            IReadOnlyList<string> fonts = null,
            TextEngineObject textEngineObject = null,
            TextDataOrigin origin = TextDataOrigin.None,
            TextPath path = null)
        {
            return new TextLayerInfo
            {
                Text = rawText == null ? Text : NormalizeText(rawText),
                RawText = rawText ?? RawText,
                Transform = Transform,
                Orientation = Orientation,
                IsParagraphText = path == null && IsParagraphText,
                ShapeKind = path == null ? ShapeKind : TextShapeKind.Path,
                Path = path ?? Path,
                ShapeType = ShapeType,
                StyleRuns = styleRuns ?? StyleRuns,
                Paragraphs = paragraphs ?? Paragraphs,
                Fonts = fonts ?? Fonts,
                Descriptor = Descriptor,
                Warp = Warp,
                WarpSettings = WarpSettings,
                EngineData = EngineData,
                TextIndex = TextIndex,
                TextEngineObject = textEngineObject ?? TextEngineObject,
                AntiAlias = AntiAlias,
                Bounds = Bounds,
                BoundingBox = BoundingBox,
                BoxBounds = BoxBounds,
                Origin = Origin | origin,
            };
        }

        internal static double[] ReadTransform(BigEndianReader reader)
        {
            var transform = new double[6];
            for (var i = 0; i < 6; i++)
            {
                transform[i] = reader.ReadDouble();
            }

            return transform;
        }

        internal static IReadOnlyList<string> DistinctFonts(IEnumerable<TextStyleRun> runs)
        {
            var fonts = new List<string>();
            foreach (var run in runs)
            {
                if (!string.IsNullOrEmpty(run.FontName) && !fonts.Contains(run.FontName))
                {
                    fonts.Add(run.FontName);
                }
            }

            return fonts;
        }

        private static TextLayerInfo FromDescriptor(Descriptor text, Descriptor warp, double[] transform, DocumentColors colors)
        {
            var raw = text.GetString("Txt ") ?? string.Empty;
            EngineValue engine = null;
            DescriptorValue engineValue;
            if (text.TryGet("EngineData", out engineValue) && engineValue.Type == DescriptorValueType.RawData && engineValue.Raw != null)
            {
                engine = EngineDataParser.Parse(engineValue.Raw);
            }

            var origin = TextDataOrigin.TypeTool | (engine == null ? TextDataOrigin.None : TextDataOrigin.EngineData);

            // The descriptor text is authoritative; EngineData repeats it with a trailing \r.
            var editor = engine == null ? null : engine.Get("EngineDict", "Editor", "Text");
            var engineText = editor != null && editor.Kind == EngineValueKind.String ? editor.Text : null;
            if (string.IsNullOrEmpty(raw) && !string.IsNullOrEmpty(engineText))
            {
                raw = engineText;
            }

            var orientation = text.GetEnum("Ornt") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal;
            IReadOnlyList<TextStyleRun> runs = new TextStyleRun[0];
            IReadOnlyList<TextParagraph> paragraphs = new TextParagraph[0];
            int? shapeType = null;
            TextBounds? boxBounds = null;
            TextAntiAlias? antiAlias = AntiAliasFromDescriptor(text.GetEnum("AntA"));
            if (engine != null)
            {
                var source = string.IsNullOrEmpty(engineText) ? raw : engineText;
                runs = ReadStyleRuns(engine, source, colors);
                paragraphs = ReadParagraphs(engine, source);
                var shape = engine.Get("EngineDict", "Rendered", "Shapes", "Children", 0);
                shapeType = EngineStyles.Integer(shape == null ? null : shape["ShapeType"])
                    ?? EngineStyles.Integer(shape == null ? null : shape.Get("Cookie", "Photoshop", "ShapeType"));
                boxBounds = BoundsFromList(shape == null ? null : shape.Get("Cookie", "Photoshop", "BoxBounds"));
                if (antiAlias == null)
                {
                    var engineAntiAlias = EngineStyles.Integer(engine.Get("EngineDict", "AntiAlias"));
                    if (engineAntiAlias != null && engineAntiAlias >= 0 && engineAntiAlias <= 4)
                    {
                        antiAlias = (TextAntiAlias)engineAntiAlias.Value;
                    }
                }
            }

            DescriptorValue index;
            int? textIndex = null;
            if (text.TryGet("TextIndex", out index) && index.Type == DescriptorValueType.Integer && index.Integer >= int.MinValue && index.Integer <= int.MaxValue)
            {
                textIndex = (int)index.Integer;
            }

            return new TextLayerInfo
            {
                Text = NormalizeText(raw),
                RawText = raw,
                Transform = transform,
                Orientation = orientation,
                IsParagraphText = shapeType == 1,
                ShapeKind = shapeType == null || shapeType == 0
                    ? TextShapeKind.Point
                    : shapeType == 1 ? TextShapeKind.Box : TextShapeKind.Other,
                ShapeType = shapeType,
                StyleRuns = runs,
                Paragraphs = paragraphs,
                Fonts = DistinctFonts(runs),
                Descriptor = text,
                Warp = warp,
                WarpSettings = warp == null ? null : WarpFromDescriptor(warp),
                EngineData = engine,
                TextIndex = textIndex,
                AntiAlias = antiAlias,
                Bounds = BoundsFromDescriptor(text.GetObject("bounds")),
                BoundingBox = BoundsFromDescriptor(text.GetObject("boundingBox")),
                BoxBounds = boxBounds,
                Origin = origin,
            };
        }

        private static TextWarp WarpFromDescriptor(Descriptor warp)
        {
            Func<double, double> finite = delegate(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value; };
            var key = warp.GetEnum("warpStyle") ?? "warpNone";
            return new TextWarp(
                TextWarp.StyleFromKey(key),
                key,
                finite(warp.GetNumber("warpValue", 0)),
                finite(warp.GetNumber("warpPerspective", 0)),
                finite(warp.GetNumber("warpPerspectiveOther", 0)),
                warp.GetEnum("warpRotate") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal);
        }

        private static TextBounds? BoundsFromDescriptor(Descriptor bounds)
        {
            if (bounds == null)
            {
                return null;
            }

            var left = bounds.GetNumber("Left", 0);
            var top = bounds.GetNumber("Top ", 0);
            var right = bounds.GetNumber("Rght", left);
            var bottom = bounds.GetNumber("Btom", top);
            if (double.IsNaN(left) || double.IsInfinity(left) || double.IsNaN(top) || double.IsInfinity(top)
                || double.IsNaN(right) || double.IsInfinity(right) || double.IsNaN(bottom) || double.IsInfinity(bottom))
            {
                return null;
            }

            return new TextBounds(left, top, right, bottom);
        }

        private static TextBounds? BoundsFromList(EngineValue list)
        {
            if (list == null || list.Kind != EngineValueKind.List || list.Items.Count < 4)
            {
                return null;
            }

            var values = new double[4];
            for (var i = 0; i < 4; i++)
            {
                var value = EngineStyles.Number(list.Items[i]);
                if (value == null)
                {
                    return null;
                }

                values[i] = value.Value;
            }

            return new TextBounds(values[0], values[1], values[2], values[3]);
        }

        /// <summary>Descriptor <c>AntA</c> tokens; the EngineData <c>/AntiAlias</c> numbers in parentheses.</summary>
        private static TextAntiAlias? AntiAliasFromDescriptor(string value)
        {
            switch (value)
            {
                case "Anno":
                case "antiAliasNone":
                    return TextAntiAlias.None;
                case "AnCr":
                case "antiAliasCrisp":
                    return TextAntiAlias.Crisp;
                case "AnSt":
                case "antiAliasStrong":
                    return TextAntiAlias.Strong;
                case "AnSm":
                case "antiAliasSmooth":
                    return TextAntiAlias.Smooth;
                case "AnSh":
                case "antiAliasSharp":
                    return TextAntiAlias.Sharp;
                default:
                    return null;
            }
        }

        private static List<TextStyleRun> ReadStyleRuns(EngineValue engine, string text, DocumentColors colors)
        {
            var runs = new List<TextStyleRun>();
            var fontNames = new List<string>();
            var fontSet = engine.Get("ResourceDict", "FontSet") ?? engine.Get("DocumentResources", "FontSet");
            if (fontSet != null)
            {
                foreach (var font in fontSet.Items)
                {
                    var name = font["Name"];
                    fontNames.Add(name != null && name.Kind == EngineValueKind.String ? name.Text : string.Empty);
                }
            }

            EngineValue defaultStyle = null;
            var sheetIndex = EngineStyles.Integer(engine.Get("ResourceDict", "TheNormalStyleSheet"));
            if (sheetIndex != null)
            {
                defaultStyle = engine.Get("ResourceDict", "StyleSheetSet", sheetIndex.Value, "StyleSheetData");
            }

            var runArray = engine.Get("EngineDict", "StyleRun", "RunArray");
            var lengths = engine.Get("EngineDict", "StyleRun", "RunLengthArray");
            if (runArray == null || lengths == null)
            {
                return runs;
            }

            var start = 0;
            for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count && start < text.Length; i++)
            {
                var length = Math.Min(EngineStyles.Length(lengths.Items[i]), text.Length - start);
                if (length <= 0)
                {
                    continue;
                }

                var style = runArray.Items[i].Get("StyleSheet", "StyleSheetData");
                var captured = start;
                runs.Add(EngineStyles.Run(
                    captured,
                    length,
                    EngineStyles.Segment(text, captured, length),
                    key => style != null && style[key] != null ? style[key] : defaultStyle == null ? null : defaultStyle[key],
                    StyleKeys.TypeTool,
                    fontNames,
                    1,
                    colors));
                start += length;
            }

            return runs;
        }

        private static List<TextParagraph> ReadParagraphs(EngineValue engine, string text)
        {
            var paragraphs = new List<TextParagraph>();
            EngineValue defaultSheet = null;
            var sheetIndex = EngineStyles.Integer(engine.Get("ResourceDict", "TheNormalParagraphSheet"));
            if (sheetIndex != null)
            {
                defaultSheet = engine.Get("ResourceDict", "ParagraphSheetSet", sheetIndex.Value, "Properties");
            }

            var runArray = engine.Get("EngineDict", "ParagraphRun", "RunArray");
            var lengths = engine.Get("EngineDict", "ParagraphRun", "RunLengthArray");
            if (runArray == null || lengths == null)
            {
                return paragraphs;
            }

            var start = 0;
            for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count && start < text.Length; i++)
            {
                var length = Math.Min(EngineStyles.Length(lengths.Items[i]), text.Length - start);
                if (length <= 0)
                {
                    continue;
                }

                var properties = runArray.Items[i].Get("ParagraphSheet", "Properties");
                var captured = start;
                paragraphs.Add(EngineStyles.Paragraph(
                    captured,
                    length,
                    EngineStyles.Segment(text, captured, length),
                    key => properties != null && properties[key] != null ? properties[key] : defaultSheet == null ? null : defaultSheet[key],
                    ParagraphKeys.TypeTool,
                    1));
                start += length;
            }

            return paragraphs;
        }
    }

    /// <summary>A transform and TextIndex recovered from a damaged TySh record.</summary>
    public sealed class SalvagedTySh
    {
        public SalvagedTySh(double[] transform, int? textIndex)
        {
            Transform = transform;
            TextIndex = textIndex;
        }

        public double[] Transform { get; private set; }

        public int? TextIndex { get; private set; }
    }
}
