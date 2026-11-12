using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Band rendering: the document composites in horizontal bands of full width, and
    /// every band comes out bit-identical to the same rows of a whole-canvas render.
    /// The canvas, isolated groups, clipping runs and the layers that only take part in
    /// row-local work hold just the band's rows. Everything that reads across rows runs
    /// at the extent a whole render gives it, independent of the band. A PlanarImage
    /// carries its whole-render bounds in Logical, and every decision that depends on a
    /// buffer's extent reads those. No halo is needed, because no band-clipped buffer
    /// is ever blurred, dilated or distance-transformed.
    /// </summary>
    internal sealed partial class LayerCompositor : IDisposable
    {
        private PsdRect? _window;
        private HashSet<PsdLayer> _wholeLayers;
        private ChannelReaders _readers;
        private readonly Dictionary<PsdLayer, PlanarImage> _bandPixels = new Dictionary<PsdLayer, PlanarImage>();
        private readonly Dictionary<PsdLayer, PlanarImage> _bandPrefetched = new Dictionary<PsdLayer, PlanarImage>();
        private readonly Dictionary<PsdLayer, bool> _storedCoverageEmpty = new Dictionary<PsdLayer, bool>();
        private readonly Dictionary<PsdLayer, PsdRect> _reachBounds = new Dictionary<PsdLayer, PsdRect>();
        private readonly Dictionary<MaskCacheKey, MaskSampler> _wholeMasks = new Dictionary<MaskCacheKey, MaskSampler>();
        private readonly Dictionary<VectorMaskCacheKey, MaskSampler> _wholeVectorMasks = new Dictionary<VectorMaskCacheKey, MaskSampler>();

        private struct MaskCacheKey
        {
            public MaskCacheKey(PsdLayer layer, LayerMask mask)
            {
                Layer = layer;
                Mask = mask;
            }

            public PsdLayer Layer;
            public LayerMask Mask;
        }

        private struct VectorMaskCacheKey
        {
            public VectorMaskCacheKey(PsdLayer layer, PsdRect region)
            {
                Layer = layer;
                Region = region;
            }

            public PsdLayer Layer;
            public PsdRect Region;
        }

        /// <summary>
        /// Prepares band rendering: finds the layers that must decode whole (see the
        /// class remarks) and decodes them, once for every band.
        /// </summary>
        public void BeginBands()
        {
            _wholeLayers = new HashSet<PsdLayer>();
            _readers = new ChannelReaders();
            CollectWholeLayers(_document.RootLayers, false);
            PrefetchPixels(_wholeLayers);
        }

        /// <summary>Composites the canvas rows of band; the result holds just those rows.</summary>
        public PlanarImage RenderBand(PsdRect band)
        {
            band = band.Intersect(_canvas);
            _window = band;
            _bandPixels.Clear();
            _bandPrefetched.Clear();
            PrefetchBand(band);
            var canvas = new PlanarImage(band) { Logical = _canvas };
            CompositeList(canvas, _document.RootLayers, null);
            _bandPixels.Clear();
            return canvas;
        }

        public void Dispose()
        {
            if (_readers != null)
            {
                _readers.Dispose();
                _readers = null;
            }
        }

        /// <summary>
        /// Layers whose cross-row work needs their whole pixels: anything with effects,
        /// every layer under a group with effects (they composite into the group's whole
        /// silhouette), and shapes with a vector stroke.
        /// </summary>
        private void CollectWholeLayers(IReadOnlyList<PsdLayer> layers, bool insideStyledGroup)
        {
            foreach (var layer in layers)
            {
                var styled = EffectsOf(layer) != null;
                if (insideStyledGroup || styled || (layer.Kind == PsdLayerKind.Fill && layer.VectorStroke != null))
                {
                    _wholeLayers.Add(layer);
                }

                if (layer.Kind == PsdLayerKind.Group)
                {
                    CollectWholeLayers(layer.Children, insideStyledGroup || styled);
                }
            }
        }

        /// <summary>
        /// The band's rows of a layer that decodes per band: its stored pixels, or the
        /// solid, pattern or gradient fill a fill layer synthesizes when it stores no
        /// coverage (decided on the whole stored layer, as a whole render decides it).
        /// </summary>
        private PlanarImage BandPixels(PsdLayer layer, PsdRect window)
        {
            PlanarImage cached;
            if (_bandPixels.TryGetValue(layer, out cached))
            {
                return cached;
            }

            PlanarImage prefetched = null;
            if (_bandPrefetched.ContainsKey(layer))
            {
                prefetched = _bandPrefetched[layer];
                _bandPrefetched.Remove(layer);
            }

            var pixels = prefetched != null ? prefetched : layer.DecodePixels(window.Top, window.Bottom, _readers);
            if (layer.Kind == PsdLayerKind.Fill && StoredCoverageEmpty(layer))
            {
                if (layer.FillColor.HasValue)
                {
                    var color = layer.FillColor.Value;
                    pixels = new PlanarImage(_canvas.Intersect(window)) { Logical = _canvas };
                    pixels.Fill(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
                }
                else if (layer.ContentKey == "PtFl" && layer.FillDescriptor != null)
                {
                    pixels = PatternFillPixels(layer, layer.FillDescriptor, window);
                }
                else if (layer.ContentKey == "GdFl" && layer.FillDescriptor != null)
                {
                    pixels = GradientFillPixels(layer, layer.FillDescriptor, window);
                }
            }

            _bandPixels[layer] = pixels;
            return pixels;
        }

        /// <summary>
        /// Whether a whole decode of the layer would be null or fully transparent (the
        /// IsEmptyCoverage test), found by scanning its alpha channel in strips so that
        /// no whole plane is held.
        /// </summary>
        private bool StoredCoverageEmpty(PsdLayer layer)
        {
            bool empty;
            if (_storedCoverageEmpty.TryGetValue(layer, out empty))
            {
                return empty;
            }

            empty = true;
            if (layer.HasStoredPixels)
            {
                var bounds = layer.Bounds;
                if (layer.GetChannel(-1) == null)
                {
                    // Without an alpha channel the decoded alpha is 1 everywhere.
                    empty = false;
                }
                else
                {
                    using (var readers = new ChannelReaders())
                    {
                        var strip = Math.Max(1, (1 << 24) / Math.Max(1, bounds.Width));
                        for (var top = bounds.Top; top < bounds.Bottom && empty; top += strip)
                        {
                            var alpha = layer.DecodeChannelPlane(-1, bounds, top, Math.Min(bounds.Bottom, top + strip), readers);
                            if (alpha != null)
                            {
                                foreach (var value in alpha)
                                {
                                    if (value > 0f)
                                    {
                                        empty = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            _storedCoverageEmpty[layer] = empty;
            return empty;
        }

        /// <summary>A copy of source that a band render limits to the band's rows (whole bounds stay its logical bounds).</summary>
        private PlanarImage BandCopy(PlanarImage source)
        {
            if (!_window.HasValue)
            {
                return source.Clone();
            }

            var window = _window.Value;
            var rows = source.Bounds.Intersect(window);
            var copy = new PlanarImage(rows) { Logical = source.Logical };
            var width = rows.Width;
            for (var y = rows.Top; y < rows.Bottom; y++)
            {
                var s = source.RowOffset(y, rows.Left);
                var d = copy.RowOffset(y, rows.Left);
                Array.Copy(source.R, s, copy.R, d, width);
                Array.Copy(source.G, s, copy.G, d, width);
                Array.Copy(source.B, s, copy.B, d, width);
                Array.Copy(source.A, s, copy.A, d, width);
            }

            return copy;
        }

        /// <summary>In a band render, whether nothing reach covers lies in the rows target holds.</summary>
        private bool OutsideBand(PlanarImage target, PsdRect reach)
        {
            return _window.HasValue && reach.Intersect(target.Bounds).IsEmpty;
        }

        /// <summary>
        /// Every pixel a layer can write, its effects and (for groups) its children's
        /// included: a superset used only to skip layers that cannot touch a band.
        /// </summary>
        private PsdRect ReachBounds(PsdLayer layer)
        {
            if (!_window.HasValue)
            {
                return _canvas;
            }

            PsdRect reach;
            if (_reachBounds.TryGetValue(layer, out reach))
            {
                return reach;
            }

            if (layer.Kind == PsdLayerKind.Group)
            {
                reach = default(PsdRect);
                foreach (var child in layer.Children)
                {
                    if (IsVisible(child))
                    {
                        reach = reach.Union(ReachBounds(child));
                    }
                }

                var fx = EffectsOf(layer);
                if (fx != null && !reach.IsEmpty)
                {
                    var r = EffectReach(fx);
                    reach = reach.Union(new PsdRect(reach.Left - r, reach.Top - r, reach.Right + r, reach.Bottom + r));
                }
            }
            else
            {
                reach = AffectedBounds(layer);
            }

            _reachBounds[layer] = reach;
            return reach;
        }

        /// <summary>A raster or baked vector mask sampler. A band render builds unfeathered ones for the band's rows and keeps feathered ones (built whole) for every band.</summary>
        private MaskSampler BandMask(PsdLayer layer, LayerMask mask, int top, int bottom, Func<MaskSampler> build)
        {
            if (!_window.HasValue || MaskSampler.IsRowLocal(mask) || (top == int.MinValue && bottom == int.MaxValue))
            {
                return build();
            }

            var key = new MaskCacheKey(layer, mask);
            MaskSampler sampler;
            if (!_wholeMasks.TryGetValue(key, out sampler))
            {
                sampler = build();
                _wholeMasks[key] = sampler;
            }

            return sampler;
        }

        /// <summary>A vector mask sampler; feathered ones (built whole) are kept for every band.</summary>
        private MaskSampler BandVectorMask(PsdLayer layer, PsdRect region, int top, int bottom, Func<MaskSampler> build)
        {
            if (!_window.HasValue || layer.VectorMaskFeather <= 0 || (top == int.MinValue && bottom == int.MaxValue))
            {
                return build();
            }

            var key = new VectorMaskCacheKey(layer, region);
            MaskSampler sampler;
            if (!_wholeVectorMasks.TryGetValue(key, out sampler))
            {
                sampler = build();
                _wholeVectorMasks[key] = sampler;
            }

            return sampler;
        }

        /// <summary>Decodes the band's rows of every reachable per-band layer that crosses the band, in parallel.</summary>
        private void PrefetchBand(PsdRect band)
        {
            var layers = new List<PsdLayer>();
            foreach (var layer in ReachableLayers())
            {
                if (!_wholeLayers.Contains(layer) && layer.Bounds.Top < band.Bottom && layer.Bounds.Bottom > band.Top)
                {
                    layers.Add(layer);
                }
            }

            if (layers.Count < 2)
            {
                return;
            }

            var decoded = new PlanarImage[layers.Count];
            var succeeded = new bool[layers.Count];
            Parallelism.For(layers.Count, (long)band.Width * band.Height, delegate(int start, int end)
            {
                for (var i = start; i < end; i++)
                {
                    try
                    {
                        decoded[i] = layers[i].DecodePixels(band.Top, band.Bottom, _readers);
                        succeeded[i] = true;
                    }
                    catch (PsdFormatException)
                    {
                        // Left to BandPixels: a damaged layer fails the render only if it is drawn.
                    }
                }
            });

            for (var i = 0; i < layers.Count; i++)
            {
                if (succeeded[i])
                {
                    _bandPrefetched[layers[i]] = decoded[i];
                }
            }
        }
    }
}
