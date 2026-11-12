using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>
    /// Reader for Photoshop 5.x type records (<c>tySh</c>): a fixed binary layout
    /// with no EngineData. Recovers the text, faces, style runs (font, size,
    /// tracking, leading, kerning, baseline shift), the layer color, per-line
    /// alignment and anti-aliasing. The shared 10-byte color record reader also
    /// serves the duotone inks and PS 5 lrFX effects.
    /// </summary>
    internal static class LegacyText
    {
        private const ushort Version = 1;
        private const int StyleRecordBytes = 26;
        private const int ColorBytes = 10;
        private const char CarriageReturn = '\r';

        private sealed class Face
        {
            public Face(ushort mark, string postScriptName, string family, string style)
            {
                Mark = mark;
                PostScriptName = postScriptName;
                Family = family;
                Style = style;
            }

            public ushort Mark;
            public string PostScriptName;
            public string Family;
            public string Style;
        }

        private sealed class Style
        {
            public Style(ushort mark, ushort faceMark, double size, double trackingEm, double kerningEm, double leading, double baselineShift, bool autoKern)
            {
                Mark = mark;
                FaceMark = faceMark;
                Size = size;
                TrackingEm = trackingEm;
                KerningEm = kerningEm;
                Leading = leading;
                BaselineShift = baselineShift;
                AutoKern = autoKern;
            }

            public ushort Mark;
            public ushort FaceMark;
            public double Size;
            public double TrackingEm;
            public double KerningEm;
            public double Leading;
            public double BaselineShift;
            public bool AutoKern;
        }

        private sealed class Line
        {
            public Line(ushort orientation, short alignment, List<KeyValuePair<char, ushort>> units)
            {
                Orientation = orientation;
                Alignment = alignment;
                Units = units;
            }

            public ushort Orientation;
            public short Alignment;
            public List<KeyValuePair<char, ushort>> Units;
        }

        private sealed class Record
        {
            public Record(DocumentColors colors)
            {
                Colors = colors;
            }

            public double[] Transform = new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0 };
            public List<Face> Faces = new List<Face>();
            public List<Style> Styles = new List<Style>();
            public List<Line> Lines = new List<Line>();
            public PsdColor Color = PsdColor.Black;
            public byte AntiAlias;
            public DocumentColors Colors;
        }

        /// <summary>Parses a Photoshop 5.x <c>tySh</c> record. Returns null when it does not decode.</summary>
        public static TextLayerInfo Parse(byte[] payload, DocumentColors colors = null)
        {
            var record = ParseRecord(payload, colors);
            if (record == null || record.Styles.Count == 0 || record.Faces.Count == 0)
            {
                return null;
            }

            // Flatten the lines into one unit stream. A non-final line without its '\r' gets a
            // synthetic separator in the line's last style, and the last line's '\r' is dropped,
            // so run and paragraph indices match the text.
            var units = new List<KeyValuePair<char, ushort>>();
            var lineSpans = new List<KeyValuePair<int, int>>();
            var alignments = new List<short>();
            var vertical = false;
            for (var i = 0; i < record.Lines.Count; i++)
            {
                var line = record.Lines[i];
                vertical |= line.Orientation != 0;
                var start = units.Count;
                units.AddRange(line.Units);
                var last = i + 1 == record.Lines.Count;
                if (!last && (line.Units.Count == 0 || line.Units[line.Units.Count - 1].Key != CarriageReturn))
                {
                    var mark = line.Units.Count == 0 ? record.Styles[0].Mark : line.Units[line.Units.Count - 1].Value;
                    units.Add(new KeyValuePair<char, ushort>(CarriageReturn, mark));
                }

                if (last && units.Count > 0 && units[units.Count - 1].Key == CarriageReturn)
                {
                    units.RemoveAt(units.Count - 1);
                }

                lineSpans.Add(new KeyValuePair<int, int>(start, units.Count - start));
                alignments.Add(line.Alignment);
            }

            if (units.Count == 0)
            {
                return null;
            }

            var builder = new char[units.Count];
            for (var i = 0; i < units.Count; i++)
            {
                builder[i] = units[i].Key;
            }

            var raw = new string(builder);
            var runs = new List<TextStyleRun>();
            var runStart = 0;
            while (runStart < units.Count)
            {
                var mark = units[runStart].Value;
                var runEnd = runStart + 1;
                while (runEnd < units.Count && units[runEnd].Value == mark)
                {
                    runEnd++;
                }

                Style style = record.Styles.FirstOrDefault(delegate(Style s) { return s.Mark == mark; }) ?? record.Styles[0];
                Face face = record.Faces.FirstOrDefault(delegate(Face f) { return f.Mark == style.FaceMark; }) ?? record.Faces[0];

                // PS 5 tracking and kerning are em fractions; the run model uses thousandths of an em.
                runs.Add(new TextStyleRun(
                    runStart,
                    runEnd - runStart,
                    TextLayerInfo.NormalizeText(raw.Substring(runStart, runEnd - runStart)),
                    face.PostScriptName.Length > 0 ? face.PostScriptName : null,
                    Compat.Clamp(style.Size, 1, EngineStyles.MaxTextSize),
                    record.Color,
                    false,
                    false,
                    Math.Round(style.TrackingEm * 1000),
                    style.Leading > 0 ? style.Leading : (double?)null)
                {
                    AutoLeading = style.Leading <= 0,
                    AutoKerning = style.AutoKern,
                    Kerning = Math.Round(style.KerningEm * 1000),
                    BaselineShift = style.BaselineShift,
                    FontFamily = face.Family.Length > 0 ? face.Family : null,
                    FontStyleName = face.Style.Length > 0 ? face.Style : null,
                });
                runStart = runEnd;
            }

            var paragraphs = new List<TextParagraph>();
            for (var i = 0; i < lineSpans.Count; i++)
            {
                var start = lineSpans[i].Key;
                var length = lineSpans[i].Value;
                if (length <= 0)
                {
                    continue;
                }

                var justification = alignments[i] == 1
                    ? TextJustification.Center
                    : alignments[i] == -1 ? TextJustification.Right : TextJustification.Left;
                paragraphs.Add(new TextParagraph(start, length, TextLayerInfo.NormalizeText(raw.Substring(start, length)), justification));
            }

            // Fonts the runs use first, then the record's other faces.
            var fonts = TextLayerInfo.DistinctFonts(runs).ToList();
            foreach (var face in record.Faces)
            {
                if (face.PostScriptName.Length > 0 && !fonts.Contains(face.PostScriptName))
                {
                    fonts.Add(face.PostScriptName);
                }
            }

            return new TextLayerInfo
            {
                Text = TextLayerInfo.NormalizeText(raw),
                RawText = raw,
                Transform = record.Transform,
                Orientation = vertical ? TextOrientation.Vertical : TextOrientation.Horizontal,
                StyleRuns = runs,
                Paragraphs = paragraphs,
                Fonts = fonts,

                // Byte 1 reads as Photoshop 2026's Sharp (legacy_type_tool_anti_alias in the reference).
                AntiAlias = record.AntiAlias == 0 ? TextAntiAlias.None : TextAntiAlias.Sharp,
                Origin = TextDataOrigin.LegacyTypeTool,
            };
        }

        /// <summary>
        /// Photoshop's 10-byte color: u16 space and four u16 components. 0 RGB, 1 HSB
        /// (hue over 0..65535 for 0..360 degrees), 2 CMYK stored inverted (65535 is no
        /// ink), 8 grayscale with the level on 0..10000. With <paramref name="colors"/>,
        /// CMYK converts through the document's profile like the pixels (the plain
        /// inverse-ink formula otherwise) and RGB through an RGB document's profile;
        /// HSB and gray stay as they are, as in the reference. The same record is the
        /// color of PS 5 lrFX effects.
        /// </summary>
        internal static PsdColor ReadColor(BigEndianReader reader)
        {
            return ReadColor(reader, null);
        }

        internal static PsdColor ReadColor(BigEndianReader reader, DocumentColors colors)
        {
            var space = reader.ReadUInt16();
            var c = new ushort[4];
            for (var i = 0; i < 4; i++)
            {
                c[i] = reader.ReadUInt16();
            }

            switch (space)
            {
                case 1:
                    {
                        var hue = c[0] * 360.0 / 65535.0;
                        var saturation = c[1] / 65535.0;
                        var brightness = c[2] / 65535.0;
                        var position = (hue / 60.0) % 6.0;
                        var sector = (int)Math.Floor(position);
                        var fraction = position - sector;
                        var p = brightness * (1 - saturation);
                        var q = brightness * (1 - (saturation * fraction));
                        var t = brightness * (1 - (saturation * (1 - fraction)));
                        double r, g, b;
                        switch (sector)
                        {
                            case 1:
                                r = q; g = brightness; b = p;
                                break;
                            case 2:
                                r = p; g = brightness; b = t;
                                break;
                            case 3:
                                r = p; g = q; b = brightness;
                                break;
                            case 4:
                                r = t; g = p; b = brightness;
                                break;
                            case 5:
                                r = brightness; g = p; b = q;
                                break;
                            default:
                                r = brightness; g = t; b = p;
                                break;
                        }

                        return new PsdColor(Level(r), Level(g), Level(b));
                    }

                case 2 when colors != null:
                    return colors.FromInk(1 - (c[0] / 65535.0), 1 - (c[1] / 65535.0), 1 - (c[2] / 65535.0), 1 - (c[3] / 65535.0));

                case 0 when colors != null:
                    return colors.FromRgb((c[0] + 128) / 257.0, (c[1] + 128) / 257.0, (c[2] + 128) / 257.0);

                case 2:
                    {
                        // Stored inverted: the component is 1 - ink, so RGB = component x (1 - black ink).
                        var k = c[3] / 65535.0;
                        return new PsdColor(Level(c[0] / 65535.0 * k), Level(c[1] / 65535.0 * k), Level(c[2] / 65535.0 * k));
                    }

                case 8:
                    {
                        var level = (byte)Compat.Clamp(Math.Round(c[0] * 255.0 / 10000.0), 0, 255);
                        return new PsdColor(level, level, level);
                    }

                default:
                    return new PsdColor((byte)((c[0] + 128) / 257), (byte)((c[1] + 128) / 257), (byte)((c[2] + 128) / 257));
            }
        }

        private static byte Level(double value)
        {
            return (byte)Compat.Clamp(Math.Round(value * 255), 0, 255);
        }

        private static Record ParseRecord(byte[] payload, DocumentColors colors)
        {
            try
            {
                var reader = new BigEndianReader(payload);
                if (reader.ReadUInt16() != Version)
                {
                    return null;
                }

                var transform = TextLayerInfo.ReadTransform(reader);
                foreach (var value in transform)
                {
                    if (double.IsNaN(value) || double.IsInfinity(value))
                    {
                        return null;
                    }
                }

                var record = new Record(colors) { Transform = transform };
                reader.ReadUInt16(); // font info version (6)
                var faceCount = reader.ReadUInt16();
                if (faceCount == 0 || faceCount * 15L > reader.Remaining)
                {
                    return null;
                }

                for (var i = 0; i < faceCount; i++)
                {
                    var mark = reader.ReadUInt16();
                    reader.ReadUInt32(); // font type
                    var postScriptName = reader.ReadPascalString(1);
                    var family = reader.ReadPascalString(1);
                    var style = reader.ReadPascalString(1);
                    reader.ReadUInt16(); // script
                    var axes = reader.ReadUInt32();
                    if (axes > (uint)(reader.Remaining / 4))
                    {
                        return null;
                    }

                    reader.Skip((int)(axes * 4L)); // design vector
                    record.Faces.Add(new Face(mark, postScriptName, family, style));
                }

                // Adobe's specification puts a u16 version before the style count, but the PS 5.x
                // files seen so far go straight from the last face to the count. Read count-first
                // with known face marks; retry with a version word skipped; finally accept the
                // count-first layout with unknown face marks (they fall back to the first face).
                var stylesAt = reader.Position;
                if (TryReadStylesTextAndColor(reader, true, record))
                {
                    return record;
                }

                if (payload.Length - stylesAt >= 2)
                {
                    reader.Position = stylesAt + 2;
                    if (TryReadStylesTextAndColor(reader, true, record))
                    {
                        return record;
                    }
                }

                reader.Position = stylesAt;
                return TryReadStylesTextAndColor(reader, false, record) ? record : null;
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the style records, the text section and the color from the style count on.
        /// False when the bytes do not describe a sane record from here; <paramref name="strict"/>
        /// also requires every style to name a known face, the check that tells the two
        /// style-section layouts apart.
        /// </summary>
        private static bool TryReadStylesTextAndColor(BigEndianReader reader, bool strict, Record record)
        {
            try
            {
                var styleCount = reader.ReadUInt16();
                if (styleCount == 0 || styleCount * (long)StyleRecordBytes > reader.Remaining)
                {
                    return false;
                }

                var styles = new List<Style>(styleCount);
                for (var i = 0; i < styleCount; i++)
                {
                    var mark = reader.ReadUInt16();
                    var faceMark = reader.ReadUInt16();
                    var size = reader.ReadFixed16();
                    var tracking = reader.ReadFixed16();
                    var kerning = reader.ReadFixed16();
                    var leading = reader.ReadFixed16();
                    var baselineShift = reader.ReadFixed16();
                    var autoKern = reader.ReadByte() != 0;
                    reader.ReadByte(); // rotate (vertical type)
                    if (size <= 0 || size > EngineStyles.MaxTextSize || Math.Abs(tracking) > 10 || leading < 0 || leading > EngineStyles.MaxTextSize * 4)
                    {
                        return false;
                    }

                    if (strict && !record.Faces.Any(delegate(Face f) { return f.Mark == faceMark; }))
                    {
                        return false;
                    }

                    styles.Add(new Style(mark, faceMark, size, tracking, kerning, leading, baselineShift, autoKern));
                }

                reader.ReadUInt16(); // text type: 0, point text (the only kind PS 5 had)
                reader.ReadUInt32(); // scaling factor (16.16)
                var characterCount = reader.ReadUInt32();
                reader.Skip(16); // horizontal and vertical placement, selection start and end
                if (characterCount > (uint)(reader.Remaining / 4))
                {
                    return false;
                }

                var lineCount = reader.ReadUInt16();
                var lines = new List<Line>(lineCount);
                long unitsSeen = 0;
                for (var i = 0; i < lineCount; i++)
                {
                    var count = reader.ReadUInt32();
                    var orientation = reader.ReadUInt16();
                    var alignment = reader.ReadInt16();
                    if (count > (uint)(reader.Remaining / 4) || unitsSeen + count > characterCount)
                    {
                        return false;
                    }

                    var units = new List<KeyValuePair<char, ushort>>((int)count);
                    for (var u = 0u; u < count; u++)
                    {
                        var unit = (char)reader.ReadUInt16();
                        units.Add(new KeyValuePair<char, ushort>(unit, reader.ReadUInt16()));
                    }

                    unitsSeen += count;
                    lines.Add(new Line(orientation, alignment, units));
                }

                if (reader.Remaining < ColorBytes + 1)
                {
                    return false;
                }

                record.Color = ReadColor(reader, record.Colors);
                record.AntiAlias = reader.ReadByte();
                record.Styles = styles;
                record.Lines = lines;
                return true;
            }
            catch (PsdFormatException)
            {
                return false;
            }
        }
    }
}
