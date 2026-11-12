using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>Rendering entry points on the document.</summary>
    public sealed partial class PsdDocument
    {
        /// <summary>Renders the document to RGBA (see <see cref="PsdRenderer"/> for the source choice).</summary>
        public RgbaImage Render(RenderOptions options = null)
        {
            return PsdRenderer.Render(this, options ?? new RenderOptions());
        }

        /// <summary>Renders the document as bands (one band in this port).</summary>
        public IEnumerable<RenderBand> RenderBands(RenderOptions options = null)
        {
            return PsdRenderer.RenderBands(this, options ?? new RenderOptions());
        }

        /// <summary>Renders the document into a PNG stream.</summary>
        public void RenderToPng(System.IO.Stream output, RenderOptions options = null, PngCompressionLevel level = PngCompressionLevel.Default)
        {
            PsdRenderer.RenderToPng(this, output, options, level);
        }
    }
}
