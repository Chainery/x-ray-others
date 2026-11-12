using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Draws a type layer from its text model (PsdLayer.Text) instead of the pixels
    /// Photoshop stored. The core library has no font engine; a text package implements
    /// this with a real rasterizer. Plug an implementation into RenderOptions.TextRasterizer.
    /// </summary>
    public interface ITextLayerRasterizer
    {
        /// <summary>
        /// Rasterizes the layer's text in document pixels: glyph coverage in the fill color,
        /// without the layer's masks, opacity, blend mode or effects (the compositor applies
        /// those, as it does for stored pixels). Returns null to keep the stored pixels.
        /// </summary>
        TextLayerRaster Rasterize(PsdLayer layer);
    }

    /// <summary>Which type layers RenderOptions.TextRasterizer draws.</summary>
    public enum TextRasterMode
    {
        /// <summary>Only type layers whose stored pixels are missing or fully transparent.</summary>
        MissingPixels,

        /// <summary>Every type layer; the stored pixels are ignored. Forces the layer compositor under RenderSource.Auto.</summary>
        Always,
    }

    /// <summary>A re-rendered type layer: straight-alpha sRGB pixels placed at (Left, Top) in document coordinates.</summary>
    public sealed class TextLayerRaster
    {
        public TextLayerRaster(int left, int top, RgbaImage image)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            Left = left;
            Top = top;
            Image = image;
        }

        public int Left { get; private set; }

        public int Top { get; private set; }

        public RgbaImage Image { get; private set; }

        /// <summary>The raster's rectangle in document coordinates.</summary>
        public PsdRect Bounds
        {
            get { return new PsdRect(Left, Top, Left + Image.Width, Top + Image.Height); }
        }
    }
}
