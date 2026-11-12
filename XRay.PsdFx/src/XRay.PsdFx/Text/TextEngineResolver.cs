using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>
    /// Connects type layers to their objects in the document's Txt2 block at load
    /// time and fills the gaps of the layer's own record from them: text missing
    /// from the TySh descriptor and EngineData, style and paragraph runs when the
    /// EngineData does not decode, paragraph direction and lists (which Photoshop
    /// stores only in Txt2), and whole layers whose TySh descriptor is damaged.
    /// The layer's own values win wherever both exist.
    /// </summary>
    internal static class TextEngineResolver
    {
        /// <summary>Parses the document's Txt2 block, or returns null when it has none or it does not decode.</summary>
        public static TextEngineBlock ParseBlock(PsdDocument document)
        {
            var block = document.GetGlobalTaggedBlock("Txt2");
            return block == null ? null : TextEngineBlock.Parse(block, PixelsPerPoint(document), document.Colors);
        }

        public static void Apply(PsdDocument document)
        {
            var hasTextLayer = false;
            foreach (var layer in document.Layers)
            {
                if (layer.Kind == PsdLayerKind.Text)
                {
                    hasTextLayer = true;
                    break;
                }
            }

            if (!hasTextLayer || document.GetGlobalTaggedBlock("Txt2") == null)
            {
                return;
            }

            var block = document.TextEngine;
            if (block == null)
            {
                return;
            }

            foreach (var layer in document.Layers)
            {
                if (layer.Kind != PsdLayerKind.Text)
                {
                    continue;
                }

                if (layer.Text == null)
                {
                    // A damaged TySh: recover the TextIndex and transform from the raw bytes.
                    var tySh = layer.GetTaggedBlock("TySh");
                    if (tySh != null)
                    {
                        var salvaged = TextLayerInfo.Salvage(tySh);
                        var recovered = salvaged.TextIndex != null ? block.GetObject(salvaged.TextIndex.Value) : null;
                        if (recovered != null)
                        {
                            layer.Text = FromObject(recovered, salvaged.Transform);
                        }
                    }

                    continue;
                }

                if (layer.Text.TextIndex != null)
                {
                    var textObject = block.GetObject(layer.Text.TextIndex.Value);
                    if (textObject != null)
                    {
                        layer.Text = Merge(layer.Text, textObject);
                    }
                }
            }
        }

        /// <summary>A layer's info built from its Txt2 object alone.</summary>
        internal static TextLayerInfo FromObject(TextEngineObject textObject, double[] transform)
        {
            var frame = textObject.Frame;
            return new TextLayerInfo
            {
                Text = textObject.Text,
                RawText = textObject.RawText,
                Transform = transform ?? new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0 },
                Orientation = frame != null ? frame.Orientation : TextOrientation.Horizontal,
                IsParagraphText = frame != null && frame.ShapeKind == TextShapeKind.Box,
                ShapeKind = frame == null
                    ? TextShapeKind.Point
                    : frame.ShapeKind == TextShapeKind.Path && frame.Path == null
                        ? TextShapeKind.Other
                        : frame.ShapeKind,
                Path = frame == null ? null : frame.Path,
                StyleRuns = textObject.StyleRuns,
                Paragraphs = textObject.Paragraphs,
                Fonts = textObject.Fonts,
                TextIndex = textObject.Index,
                TextEngineObject = textObject,
                BoxBounds = frame != null && frame.ShapeKind == TextShapeKind.Box ? frame.Bounds : null,
                Origin = TextDataOrigin.TextEngineBlock,
            };
        }

        private static TextLayerInfo Merge(TextLayerInfo info, TextEngineObject textObject)
        {
            string rawText = null;
            var sameText = info.Text == textObject.Text;
            if (info.Text.Length == 0 && textObject.Text.Length > 0)
            {
                rawText = textObject.RawText;
                sameText = true;
            }

            IReadOnlyList<TextStyleRun> runs = null;
            IReadOnlyList<string> fonts = null;
            IReadOnlyList<TextParagraph> paragraphs = null;
            if (sameText)
            {
                if (info.StyleRuns.Count == 0 && textObject.StyleRuns.Count > 0)
                {
                    runs = textObject.StyleRuns;
                    fonts = textObject.Fonts;
                }

                if (info.Paragraphs.Count == 0 && textObject.Paragraphs.Count > 0)
                {
                    paragraphs = textObject.Paragraphs;
                }
                else if (info.Paragraphs.Count > 0)
                {
                    paragraphs = MergeParagraphs(info.Paragraphs, textObject.Paragraphs);
                }
            }

            // Type on a path: TySh records it as box text; only the Txt2 frame has the path.
            TextPath path = null;
            if (textObject.Frame != null && textObject.Frame.ShapeKind == TextShapeKind.Path && textObject.Frame.Path != null)
            {
                path = textObject.Frame.Path;
            }

            var used = rawText != null || runs != null || paragraphs != null || path != null;
            return info.With(
                rawText: rawText,
                styleRuns: runs,
                paragraphs: paragraphs,
                fonts: fonts,
                textEngineObject: textObject,
                origin: used ? TextDataOrigin.TextEngineBlock : TextDataOrigin.None,
                path: path);
        }

        /// <summary>Takes direction and list settings, which TySh does not store, from the Txt2 paragraph at the same start.</summary>
        private static IReadOnlyList<TextParagraph> MergeParagraphs(IReadOnlyList<TextParagraph> own, IReadOnlyList<TextParagraph> engine)
        {
            List<TextParagraph> merged = null;
            for (var i = 0; i < own.Count; i++)
            {
                var paragraph = own[i];
                TextParagraph match = null;
                foreach (var candidate in engine)
                {
                    if (candidate.Start == paragraph.Start)
                    {
                        match = candidate;
                        break;
                    }
                }

                if (match == null)
                {
                    continue;
                }

                var direction = paragraph.Direction == TextDirection.Auto ? match.Direction : paragraph.Direction;
                var listStyle = paragraph.ListStyleIndex ?? match.ListStyleIndex;
                var listTier = paragraph.ListStyleIndex == null ? match.ListTier : paragraph.ListTier;
                if (direction == paragraph.Direction && listStyle == paragraph.ListStyleIndex && listTier == paragraph.ListTier)
                {
                    continue;
                }

                if (merged == null)
                {
                    merged = new List<TextParagraph>(own);
                }

                merged[i] = paragraph.WithDirection(direction, listStyle, listTier);
            }

            return merged;
        }

        /// <summary>Document resolution (resource 1005, 16.16 fixed pixels per inch) divided by 72.</summary>
        private static double PixelsPerPoint(PsdDocument document)
        {
            var resolution = document.GetImageResource(ImageResourceIds.ResolutionInfo);
            if (resolution != null && resolution.Data != null && resolution.Data.Length >= 4)
            {
                var ppi = ChannelCodec.ReadUInt32BE(resolution.Data, 0) / 65536.0;
                if (ppi > 1 && ppi < 100000)
                {
                    return ppi / 72.0;
                }
            }

            return 1;
        }
    }
}
