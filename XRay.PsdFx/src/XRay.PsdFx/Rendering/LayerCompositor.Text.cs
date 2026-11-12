namespace XRay.PsdFx
{
    internal sealed partial class LayerCompositor
    {
        /// <summary>
        /// Type-layer content when RenderOptions.TextRasterizer is set: the rasterizer's
        /// output for TextRasterMode.Always, or when the stored pixels are missing or empty;
        /// otherwise the stored pixels.
        /// </summary>
        private PlanarImage TextLayerPixels(PsdLayer layer, ITextLayerRasterizer rasterizer, PlanarImage stored)
        {
            if (_options.TextRasterMode != TextRasterMode.Always && stored != null && !IsEmptyCoverage(stored))
            {
                return stored;
            }

            var raster = rasterizer.Rasterize(layer);
            var planar = raster != null ? ToPlanar(raster) : null;
            return planar != null ? planar : stored;
        }

        private static PlanarImage ToPlanar(TextLayerRaster raster)
        {
            var bounds = raster.Bounds;
            if (bounds.IsEmpty)
            {
                return null;
            }

            var image = new PlanarImage(bounds);
            var pixels = raster.Image.Pixels;
            const float scale = 1f / 255f;
            for (int i = 0, p = 0; i < image.A.Length; i++, p += 4)
            {
                image.R[i] = pixels[p] * scale;
                image.G[i] = pixels[p + 1] * scale;
                image.B[i] = pixels[p + 2] * scale;
                image.A[i] = pixels[p + 3] * scale;
            }

            return image;
        }
    }
}
