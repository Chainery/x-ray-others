using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>One horizontal strip of a rendered document, from Top on, at full width.</summary>
    public sealed class RenderBand
    {
        internal RenderBand(int top, RgbaImage image)
        {
            Top = top;
            Image = image;
        }

        /// <summary>The document row of the band's first row.</summary>
        public int Top { get; private set; }

        /// <summary>The band's pixels, as wide as the document.</summary>
        public RgbaImage Image { get; private set; }
    }

    /// <summary>
    /// Renders a document: the saved merged image when it is real and eligible, otherwise
    /// the layer compositor. This port has no banded compositor, so RenderBands yields the
    /// whole document as a single band.
    /// </summary>
    internal static class PsdRenderer
    {
        public static RgbaImage Render(PsdDocument document, RenderOptions options)
        {
            PlanarImage planar = null;
            if (UsesMergedImage(document, options))
            {
                try
                {
                    planar = MergedImageDecoder.Decode(document);
                }
                catch (PsdFormatException)
                {
                    // A corrupt merged image falls back to the layer tree.
                    planar = null;
                }
            }

            if (planar == null)
            {
                planar = new LayerCompositor(document, options).Render();
            }

            var image = planar.ToRgba(document.Bounds);
            return options.Background.HasValue ? image.Flatten(options.Background.Value) : image;
        }

        /// <summary>Which source Render takes for these options.</summary>
        internal static bool UsesMergedImage(PsdDocument document, RenderOptions options)
        {
            if (options.Source == RenderSource.MergedImage)
            {
                return true;
            }

            if (options.Source == RenderSource.Layers)
            {
                return false;
            }

            // A placeholder merged image (saved without Maximize Compatibility) is not real.
            return options.LayerVisibility == null
                && !options.RerendersAllText
                && document.HasRealMergedImage
                && (document.MergedImageHasTransparency || document.RootLayers.Count == 0 || document.MergedImage.Length >= 2);
        }

        /// <summary>
        /// Renders the document as bands. The whole image is rendered at once because the
        /// compositor has no band mode in this port, so a single band is yielded.
        /// </summary>
        public static IEnumerable<RenderBand> RenderBands(PsdDocument document, RenderOptions options)
        {
            yield return new RenderBand(0, Render(document, options));
        }

        /// <summary>
        /// Streams the rendered document into a PNG band by band. The image keeps its alpha
        /// channel unless the result is opaque by construction: a Background, or a merged
        /// image without transparency.
        /// </summary>
        public static void RenderToPng(PsdDocument document, System.IO.Stream output, RenderOptions options, PngCompressionLevel level)
        {
            options = options ?? new RenderOptions();
            var opaque = options.Background.HasValue || (UsesMergedImage(document, options) && !document.MergedImageHasTransparency);
            var writer = new PngEncoder.RowWriter(output, document.Width, document.Height, !opaque, level);
            foreach (var band in RenderBands(document, options))
            {
                writer.Write(band.Image);
            }

            writer.Finish();
        }

        /// <summary>Renders a single layer (or group, isolated) to document-size RGBA.</summary>
        public static RgbaImage RenderLayer(PsdLayer layer)
        {
            var document = layer.Document;
            var options = new RenderOptions
            {
                Source = RenderSource.Layers,
                LayerVisibility = delegate(PsdLayer candidate)
                {
                    return candidate == layer || IsDescendant(candidate, layer) || IsAncestor(candidate, layer);
                },
            };
            return new LayerCompositor(document, options).Render().ToRgba(document.Bounds);
        }

        private static bool IsDescendant(PsdLayer candidate, PsdLayer root)
        {
            for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
            {
                if (parent == root)
                {
                    return candidate.IsVisible;
                }
            }

            return false;
        }

        private static bool IsAncestor(PsdLayer candidate, PsdLayer layer)
        {
            for (var parent = layer.Parent; parent != null; parent = parent.Parent)
            {
                if (parent == candidate)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
