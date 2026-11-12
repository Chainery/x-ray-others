using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>A font entry of the Txt2 font set (<c>/0/1/0[i]</c>, class <c>CoolTypeFont</c>).</summary>
    public sealed class TextEngineFont
    {
        public TextEngineFont(string postScriptName, int fontType, string version)
        {
            PostScriptName = postScriptName;
            FontType = fontType;
            Version = version;
        }

        /// <summary>PostScript name (<c>/0</c>).</summary>
        public string PostScriptName { get; private set; }

        /// <summary>Font type (<c>/2</c>): 0 for Photoshop's defaults (Myriad, AdobeInvisFont), 1 for ordinary fonts.</summary>
        public int FontType { get; private set; }

        /// <summary>The font version string (<c>/5</c>), when stored.</summary>
        public string Version { get; private set; }
    }

    /// <summary>A text frame of the Txt2 frame set (<c>/0/8/0[i]</c>): the shape a text object is laid out in.</summary>
    public sealed class TextEngineFrame
    {
        public TextEngineFrame(int index, TextShapeKind shapeKind, TextOrientation orientation, IReadOnlyList<double> points, TextBounds? bounds)
        {
            Index = index;
            ShapeKind = shapeKind;
            Orientation = orientation;
            Points = points;
            Bounds = bounds;
        }

        /// <summary>Position in the frame set.</summary>
        public int Index { get; private set; }

        /// <summary>Settings key <c>/0</c>: absent or 0 point, 1 box, 2 path.</summary>
        public TextShapeKind ShapeKind { get; private set; }

        /// <summary>Settings key <c>/1</c>: 2 is vertical.</summary>
        public TextOrientation Orientation { get; private set; }

        /// <summary>The frame's Bezier control points as x, y pairs in text space, four per cubic segment (a box stores each side as a straight segment).</summary>
        public IReadOnlyList<double> Points { get; private set; }

        /// <summary>The extent of <see cref="Points"/>, when the frame has a path.</summary>
        public TextBounds? Bounds { get; private set; }

        /// <summary>The decoded path of a type-on-a-path frame (<see cref="TextShapeKind.Path"/>); null for other frames.</summary>
        public TextPath Path { get; set; }
    }

    /// <summary>
    /// One text object of the Txt2 block (<c>/1/1[i]</c>), addressed by a type
    /// layer's <c>TextIndex</c>. Lengths are converted to document pixels (the
    /// TySh engine units) using the unit flag each run stores.
    /// </summary>
    public sealed class TextEngineObject
    {
        /// <summary>Position in the object list; the <c>TextIndex</c> a type layer stores.</summary>
        public int Index { get; set; }

        /// <summary>Text with paragraph breaks normalized to \n.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>The engine text as stored (\r separators and a trailing \r).</summary>
        public string RawText { get; set; } = string.Empty;

        public IReadOnlyList<TextStyleRun> StyleRuns { get; set; } = new TextStyleRun[0];

        public IReadOnlyList<TextParagraph> Paragraphs { get; set; } = new TextParagraph[0];

        /// <summary>Distinct font PostScript names referenced by the style runs, in first-use order.</summary>
        public IReadOnlyList<string> Fonts { get; set; } = new string[0];

        /// <summary>Index into <see cref="TextEngineBlock.Frames"/> the object's view references.</summary>
        public int? FrameIndex { get; set; }

        /// <summary>The frame the object is laid out in, when it resolves.</summary>
        public TextEngineFrame Frame { get; set; }

        /// <summary>
        /// Vertical Roman glyphs lie rotated ("Standard Vertical Roman Alignment"): model key
        /// <c>/10/0</c> = 4 on a vertical frame. The runs' <see cref="TextStyleRun.BaselineDirection"/>
        /// (style key 35) says the same per run.
        /// </summary>
        public bool RotatedRoman { get; set; }

        /// <summary>The object's raw tree.</summary>
        public EngineValue Node { get; set; }
    }

    /// <summary>
    /// The document-level text engine block (<c>Txt2</c>): one text object per type
    /// layer plus the shared fonts, style sheets and frames. Photoshop trusts it over
    /// the per-layer TySh EngineData.
    /// </summary>
    public sealed class TextEngineBlock
    {
        private TextEngineBlock(EngineValue root)
        {
            Root = root;
        }

        /// <summary>The serialization version (<c>/98/0</c>; Photoshop 2026 writes 14), when stored.</summary>
        public int? FormatVersion { get; private set; }

        /// <summary>The font set (<c>/0/1/0</c>); style sheets reference fonts by index.</summary>
        public IReadOnlyList<TextEngineFont> Fonts { get; private set; } = new TextEngineFont[0];

        /// <summary>The text frame set (<c>/0/8/0</c>).</summary>
        public IReadOnlyList<TextEngineFrame> Frames { get; private set; } = new TextEngineFrame[0];

        /// <summary>The text objects (<c>/1/1</c>), indexed by <c>TextIndex</c>.</summary>
        public IReadOnlyList<TextEngineObject> Objects { get; private set; } = new TextEngineObject[0];

        /// <summary>The whole parsed block.</summary>
        public EngineValue Root { get; private set; }

        /// <summary>The object a type layer's <c>TextIndex</c> addresses, or null.</summary>
        public TextEngineObject GetObject(int textIndex)
        {
            return textIndex >= 0 && textIndex < Objects.Count ? Objects[textIndex] : null;
        }

        /// <summary>
        /// Parses a Txt2 payload. <paramref name="pixelsPerPoint"/> converts sheet
        /// values stored in points (unit flag 1) to document pixels: the document
        /// resolution divided by 72. Returns null when the block does not decode.
        /// </summary>
        public static TextEngineBlock Parse(byte[] payload, double pixelsPerPoint = 1)
        {
            return Parse(payload, pixelsPerPoint, null);
        }

        /// <summary>Parses a Txt2 payload whose colors convert like the document's pixels.</summary>
        internal static TextEngineBlock Parse(byte[] payload, double pixelsPerPoint, DocumentColors colors)
        {
            if (double.IsNaN(pixelsPerPoint) || double.IsInfinity(pixelsPerPoint) || pixelsPerPoint <= 0)
            {
                pixelsPerPoint = 1;
            }

            var root = EngineDataParser.Parse(payload);
            var objectList = root == null ? null : root.Get("1", "1");
            if (root == null || root.Kind != EngineValueKind.Dictionary || objectList == null || objectList.Kind != EngineValueKind.List)
            {
                return null;
            }

            var fonts = new List<TextEngineFont>();
            var fontSet = root.Get("0", "1", "0");
            if (fontSet != null)
            {
                foreach (var entry in fontSet.Items)
                {
                    var font = entry.Get("0", "0");
                    var name = font == null ? null : font["0"];
                    var version = font == null ? null : font["5"];
                    fonts.Add(new TextEngineFont(
                        name != null && name.Kind == EngineValueKind.String ? name.Text : string.Empty,
                        EngineStyles.Integer(font == null ? null : font["2"]) ?? 1,
                        version != null && version.Kind == EngineValueKind.String ? version.Text : null));
                }
            }

            var frames = new List<TextEngineFrame>();
            var frameList = root.Get("0", "8", "0");
            var frameItems = frameList != null ? frameList.Items : new EngineValue[0];
            for (var i = 0; i < frameItems.Count; i++)
            {
                frames.Add(ReadFrame(i, frameItems[i] == null ? null : frameItems[i]["0"]));
            }

            var fontNames = fonts.Select(delegate(TextEngineFont f) { return f.PostScriptName; }).ToList();

            // Sparse run sheets fall back to the normal style sheet, then to the document defaults.
            var normalStyle = root.Get("0", "5", "0", 0, "0", "6");
            var defaultStyle = root.Get("1", "2");
            var normalParagraph = root.Get("0", "6", "0", 0, "0", "5");
            var defaultParagraph = root.Get("1", "3");
            var objects = new List<TextEngineObject>(objectList.Items.Count);
            for (var i = 0; i < objectList.Items.Count; i++)
            {
                objects.Add(ReadObject(i, objectList.Items[i], fontNames, frames, normalStyle, defaultStyle, normalParagraph, defaultParagraph, pixelsPerPoint, colors));
            }

            return new TextEngineBlock(root)
            {
                FormatVersion = EngineStyles.Integer(root.Get("98", "0")),
                Fonts = fonts,
                Frames = frames,
                Objects = objects,
            };
        }

        private static TextEngineFrame ReadFrame(int index, EngineValue frame)
        {
            var settings = frame == null ? null : frame["2"];
            var kindValue = EngineStyles.Integer(settings == null ? null : settings["0"]);
            var kind = kindValue == null || kindValue == 0
                ? TextShapeKind.Point
                : kindValue == 1 ? TextShapeKind.Box : kindValue == 2 ? TextShapeKind.Path : TextShapeKind.Other;
            var orientation = EngineStyles.Integer(settings == null ? null : settings["1"]) == 2
                ? TextOrientation.Vertical
                : TextOrientation.Horizontal;
            var points = new List<double>();
            var source = frame == null ? null : frame.Get("1", "0");
            if (source != null)
            {
                foreach (var item in source.Items)
                {
                    var value = EngineStyles.Number(item);
                    if (value != null)
                    {
                        points.Add(value.Value);
                    }
                }
            }

            TextBounds? bounds = null;
            if (points.Count >= 2)
            {
                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                for (var p = 0; p + 1 < points.Count; p += 2)
                {
                    left = Math.Min(left, points[p]);
                    right = Math.Max(right, points[p]);
                    top = Math.Min(top, points[p + 1]);
                    bottom = Math.Max(bottom, points[p + 1]);
                }

                bounds = new TextBounds(left, top, right, bottom);
            }

            return new TextEngineFrame(index, kind, orientation, points, bounds)
            {
                Path = kind == TextShapeKind.Path ? TextPath.FromFrame(frame) : null,
            };
        }

        private static TextEngineObject ReadObject(
            int index,
            EngineValue node,
            IReadOnlyList<string> fontNames,
            IReadOnlyList<TextEngineFrame> frames,
            EngineValue normalStyle,
            EngineValue defaultStyle,
            EngineValue normalParagraph,
            EngineValue defaultParagraph,
            double pixelsPerPoint,
            DocumentColors colors)
        {
            var model = node["0"];
            var rawNode = model == null ? null : model["0"];
            var raw = rawNode != null && rawNode.Kind == EngineValueKind.String ? rawNode.Text : string.Empty;

            // Paragraph runs: /0/5/0[j] = << /0 << /0 << /5 sheet /6 unit >> >> /1 length >>.
            var paragraphs = new List<TextParagraph>();
            var start = 0;
            var paragraphRuns = model == null ? null : model.Get("5", "0");
            if (paragraphRuns != null)
            {
                foreach (var run in paragraphRuns.Items)
                {
                    if (start >= raw.Length)
                    {
                        break;
                    }

                    var length = Math.Min(EngineStyles.Length(run["1"]), raw.Length - start);
                    if (length <= 0)
                    {
                        continue;
                    }

                    var holder = run.Get("0", "0");
                    var sheet = holder == null ? null : holder["5"];
                    var scale = EngineStyles.Integer(holder == null ? null : holder["6"]) == 1 ? pixelsPerPoint : 1;
                    var captured = start;
                    paragraphs.Add(EngineStyles.Paragraph(
                        captured,
                        length,
                        EngineStyles.Segment(raw, captured, length),
                        key => (sheet != null && sheet[key] != null ? sheet[key] : normalParagraph != null ? normalParagraph[key] : null)
                            ?? (defaultParagraph == null ? null : defaultParagraph[key]),
                        ParagraphKeys.TextEngine,
                        scale));
                    start += length;
                }
            }

            // Style runs: /0/6/0[j] = << /0 << /0 << /6 sheet /5 unit >> >> /1 length >>.
            var runs = new List<TextStyleRun>();
            start = 0;
            var styleRuns = model == null ? null : model.Get("6", "0");
            if (styleRuns != null)
            {
                foreach (var run in styleRuns.Items)
                {
                    if (start >= raw.Length)
                    {
                        break;
                    }

                    var length = Math.Min(EngineStyles.Length(run["1"]), raw.Length - start);
                    if (length <= 0)
                    {
                        continue;
                    }

                    var holder = run.Get("0", "0");
                    var sheet = holder == null ? null : holder["6"];
                    var scale = EngineStyles.Integer(holder == null ? null : holder["5"]) == 1 ? pixelsPerPoint : 1;
                    var captured = start;
                    runs.Add(EngineStyles.Run(
                        captured,
                        length,
                        EngineStyles.Segment(raw, captured, length),
                        key => (sheet != null && sheet[key] != null ? sheet[key] : normalStyle != null ? normalStyle[key] : null)
                            ?? (defaultStyle == null ? null : defaultStyle[key]),
                        StyleKeys.TextEngine,
                        fontNames,
                        scale,
                        colors));
                    start += length;
                }
            }

            // Model key /10/0 is 4 on the rotated-Roman vertical capture, but also on horizontal
            // captures, so it only reports rotated Roman on vertical frames. The runs carry
            // their own BaselineDirection (style key 35).
            var frameIndex = EngineStyles.Integer(node.Get("1", "0", 0, "0"));
            var frame = frameIndex != null && frameIndex >= 0 && frameIndex < frames.Count ? frames[frameIndex.Value] : null;
            var rotatedRoman = EngineStyles.Integer(model == null ? null : model.Get("10", "0")) == 4
                && frame != null && frame.Orientation == TextOrientation.Vertical;
            return new TextEngineObject
            {
                Index = index,
                Text = TextLayerInfo.NormalizeText(raw),
                RawText = raw,
                StyleRuns = runs,
                Paragraphs = paragraphs,
                Fonts = TextLayerInfo.DistinctFonts(runs),
                FrameIndex = frameIndex,
                Frame = frame,
                RotatedRoman = rotatedRoman,
                Node = node,
            };
        }
    }
}
