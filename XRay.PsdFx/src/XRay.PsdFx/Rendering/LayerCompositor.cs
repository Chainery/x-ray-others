using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Composites the layer tree in Photoshop order. Semantics follow the
    /// calibrated reference compositor (.reference/src/render/layer_compositor.hpp):
    /// Pass Through groups composite children against the live backdrop, then fade
    /// toward the pre-group backdrop by group opacity; other groups (and pass-through
    /// groups with Fill below 100%) isolate, then merge with their blend mode, opacity
    /// and mask; a clipping run renders into an isolated buffer seeded by the base;
    /// a hidden or fully transparent base hides its whole clipping run.
    /// net45 port: adjustment layers and text re-rasterization are not rendered
    /// (no Adjustments port yet), and effects attach through the partial files.
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private readonly PsdDocument _document;
        private readonly RenderOptions _options;
        private readonly PsdRect _canvas;
        private readonly Dictionary<PsdLayer, PlanarImage> _pixelCache = new Dictionary<PsdLayer, PlanarImage>();
        private readonly Dictionary<PsdLayer, PlanarImage> _prefetched = new Dictionary<PsdLayer, PlanarImage>();

        public LayerCompositor(PsdDocument document, RenderOptions options)
        {
            _document = document;
            _options = options;
            _canvas = document.Bounds;
        }

        internal sealed class MaskChain
        {
            public MaskChain(MaskSampler sampler, MaskChain next)
            {
                Sampler = sampler;
                Next = next;
            }

            public MaskSampler Sampler { get; private set; }

            public MaskChain Next { get; private set; }
        }

        public PlanarImage Render()
        {
            PrefetchPixels(null);
            var canvas = new PlanarImage(_canvas);
            CompositeList(canvas, _document.RootLayers, null);
            return canvas;
        }

        private bool IsVisible(PsdLayer layer)
        {
            return _options.LayerVisibility != null ? _options.LayerVisibility(layer) : layer.IsVisible;
        }

        private static bool IsClipBase(PsdLayer layer)
        {
            return layer.Kind != PsdLayerKind.Adjustment;
        }

        private static bool IsClipped(PsdLayer layer)
        {
            return layer.IsClipped && layer.Kind != PsdLayerKind.Group;
        }

        private void CompositeList(PlanarImage target, IReadOnlyList<PsdLayer> siblings, MaskChain masks)
        {
            var knockout = EnterKnockoutScope(target, siblings);
            CompositeSiblings(target, siblings, masks, knockout);
            ExitKnockoutScope(knockout);
        }

        private void CompositeSiblings(PlanarImage target, IReadOnlyList<PsdLayer> siblings, MaskChain masks, KnockoutScope knockout)
        {
            var index = 0;
            while (index < siblings.Count)
            {
                Cancellation.Check();
                var layer = siblings[index];
                var runEnd = index + 1;
                if (!IsClipped(layer))
                {
                    while (runEnd < siblings.Count && IsClipped(siblings[runEnd]))
                    {
                        runEnd++;
                    }
                }

                if (runEnd == index + 1 || !IsClipBase(layer))
                {
                    CompositeKnockoutElement(target, layer, knockout, masks, delegate
                    {
                        CompositeLayer(target, layer, masks, null, false, null, false);
                    });
                    NoteKnockoutBackground(knockout, target, layer);
                    index++;
                    continue;
                }

                // An all-channels-restricted base removes the whole run.
                var baseRestriction = RestrictionOf(layer);
                if (!IsVisible(layer) || layer.Opacity == 0 || baseRestriction == RestrictAll)
                {
                    index = runEnd;
                    continue;
                }

                // A restricted base gates the merged run against the original backdrop
                // (ps-compat.md "Advanced Blending Channels"); members restrict themselves.
                var baseGate = BlendIfOf(layer);
                if (layer.Kind != PsdLayerKind.Group && baseGate == null && EffectsOf(layer) != null && LayerPixels(layer) != null)
                {
                    var members = new List<PsdLayer>();
                    for (var member = index + 1; member < runEnd; member++)
                    {
                        members.Add(siblings[member]);
                    }

                    if (!OutsideBand(target, AffectedBounds(layer)))
                    {
                        CompositeKnockoutElement(target, layer, knockout, masks, delegate
                        {
                            WithChannelRestriction(target, baseRestriction, AffectedBounds(layer), delegate
                            {
                                CompositeStyledClipRun(target, layer, LayerPixels(layer), EffectsOf(layer), members, masks);
                            });
                        });
                    }

                    index = runEnd;
                    continue;
                }

                var runBounds = layer.Kind == PsdLayerKind.Group ? RenderBounds(layer) : AffectedBounds(layer);
                var logical = runBounds.Intersect(target.Logical);
                var bounds = logical.Intersect(target.Bounds);
                if (!bounds.IsEmpty)
                {
                    // A Blend If base keeps the isolated path: its Underlying gate reads the
                    // backdrop outside the buffer, and its ungated alpha stays the clip shape.
                    var group = new PlanarImage(bounds) { Logical = logical };
                    var gateBackdrop = baseGate != null && baseGate.HasUnderlying ? Crop(target, bounds) : null;
                    if (baseGate != null)
                    {
                        _clipCoverage[group] = new float[bounds.Width * bounds.Height];
                    }

                    CompositeLayer(group, layer, null, PsdBlendMode.Normal, false, gateBackdrop, true);
                    CompositeClipMembers(group, siblings, index + 1, runEnd - index - 1);
                    _clipCoverage.Remove(group);
                    var mode = layer.BlendMode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : layer.BlendMode;
                    var capturedLayer = layer;
                    CompositeKnockoutElement(target, layer, knockout, masks, delegate
                    {
                        WithChannelRestriction(target, baseRestriction, bounds, delegate
                        {
                            MergeImage(target, group, mode, 1f, masks, false, baseGate, gateBackdrop);
                        });
                    });
                }

                index = runEnd;
            }
        }

        /// <summary>
        /// Composites one layer. gateBackdrop overrides the backdrop the Underlying
        /// Blend If gate reads (a clipping-run base renders into a transparent buffer);
        /// suppressRestriction skips the layer's own channel restriction when the caller
        /// applies it at a merge instead.
        /// </summary>
        private void CompositeLayer(PlanarImage target, PsdLayer layer, MaskChain masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage gateBackdrop, bool suppressRestriction)
        {
            Cancellation.Check();
            if (!IsVisible(layer) || layer.Opacity == 0)
            {
                return;
            }

            // All three channels excluded removes the layer, effects included.
            var restriction = RestrictionOf(layer);
            if (restriction == RestrictAll || OutsideBand(target, ReachBounds(layer)))
            {
                return;
            }

            if (restriction != 0 && !suppressRestriction)
            {
                WithChannelRestriction(target, restriction, AffectedBounds(layer), delegate
                {
                    CompositeLayer(target, layer, masks, modeOverride, clipMode, gateBackdrop, true);
                });
                return;
            }

            if (layer.Kind == PsdLayerKind.Group)
            {
                CompositeGroup(target, layer, masks, modeOverride, clipMode, gateBackdrop);
            }
            else if (layer.Kind == PsdLayerKind.Adjustment)
            {
                ApplyAdjustment(target, layer, masks);
            }
            else
            {
                CompositePixels(target, layer, masks, modeOverride, clipMode, gateBackdrop);
            }
        }

        private void CompositeGroup(PlanarImage target, PsdLayer group, MaskChain masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage gateBackdrop)
        {
            var mode = modeOverride.HasValue ? modeOverride.Value : group.BlendMode;
            var opacity = group.Opacity / 255f;
            var fill = group.FillOpacity / 255f;
            var groupMask = LayerMasks(group, ContentBounds(group).Intersect(target.Logical), target.Bounds, int.MinValue, int.MaxValue);
            if (EffectsOf(group) != null)
            {
                CompositeStyledGroup(target, group, EffectsOf(group), masks, modeOverride.HasValue ? modeOverride.Value : mode, clipMode, groupMask, gateBackdrop);
                return;
            }

            // Blend If groups always isolate, Pass Through included: the gates test the
            // merged children against the outside backdrop.
            var gate = BlendIfOf(group);
            if (mode != PsdBlendMode.PassThrough || fill < 1f || modeOverride.HasValue || gate != null)
            {
                var logical = ContentBounds(group).Intersect(target.Logical);
                var bounds = logical.Intersect(target.Bounds);
                if (bounds.IsEmpty)
                {
                    return;
                }

                var isolated = new PlanarImage(bounds) { Logical = logical };
                CompositeList(isolated, group.Children, null);
                var merged = mode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : mode;
                MergeImage(target, isolated, merged, opacity * fill, Chain(masks, groupMask), clipMode, gate, gateBackdrop);
                return;
            }

            PlanarImage before = null;
            var fadeRegion = HasAdjustmentDescendant(group) ? target.Bounds : ContentBounds(group).Intersect(target.Bounds);
            if (opacity < 1f && !fadeRegion.IsEmpty)
            {
                before = Crop(target, fadeRegion);
            }

            CompositeList(target, group.Children, Chain(masks, groupMask));
            if (before != null)
            {
                FadeToward(target, before, fadeRegion, opacity);
            }
        }

        private void CompositePixels(PlanarImage target, PsdLayer layer, MaskChain masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage gateBackdrop)
        {
            var source = LayerPixels(layer);
            if (source == null)
            {
                return;
            }

            // A band render keeps only some rows of the source and target; the region a
            // whole render would composite decides what runs (styled layers draw effects past it).
            var logicalRegion = source.Logical.Intersect(target.Logical);
            if (logicalRegion.IsEmpty)
            {
                return;
            }

            var region = source.Bounds.Intersect(target.Bounds);
            var mode = modeOverride.HasValue ? modeOverride.Value : layer.BlendMode;
            if (mode == PsdBlendMode.PassThrough)
            {
                mode = PsdBlendMode.Normal;
            }

            if (EffectsOf(layer) != null)
            {
                CompositeStyledPixels(target, layer, source, EffectsOf(layer), masks, mode, clipMode, gateBackdrop);
                return;
            }

            if (region.IsEmpty)
            {
                return;
            }

            var layerMask = LayerMasks(layer, logicalRegion, region, region.Top, region.Bottom);
            var opacity = layer.Opacity / 255f;
            var fill = layer.FillOpacity / 255f;
            var gate = BlendIfOf(layer);
            var width = region.Width;
            Parallelism.For(region.Height, width, delegate(int start, int end)
            {
                var alpha = new float[width];
                var sr = new float[width];
                var sg = new float[width];
                var sb = new float[width];
                for (var y = region.Top + start; y < region.Top + end; y++)
                {
                    var s = source.RowOffset(y, region.Left);
                    Array.Copy(source.A, s, alpha, 0, width);
                    Array.Copy(source.R, s, sr, 0, width);
                    Array.Copy(source.G, s, sg, 0, width);
                    Array.Copy(source.B, s, sb, 0, width);
                    if (layerMask != null)
                    {
                        layerMask.MultiplyRow(y, region.Left, alpha);
                    }

                    for (var chain = masks; chain != null; chain = chain.Next)
                    {
                        chain.Sampler.MultiplyRow(y, region.Left, alpha);
                    }

                    CompositeLayerRow(target, y, region.Left, mode, sr, sg, sb, alpha, opacity, fill, gate, gateBackdrop, clipMode);
                }
            });
        }

        /// <summary>Merges an isolated buffer (a group or clipping run) into the target with a blend mode, opacity, masks and, for groups, the group's Blend If gate.</summary>
        private void MergeImage(PlanarImage target, PlanarImage source, PsdBlendMode mode, float opacity, MaskChain masks, bool clipMode, BlendIf gate, PlanarImage gateBackdrop)
        {
            var region = source.Bounds.Intersect(target.Bounds);
            if (region.IsEmpty || opacity <= 0)
            {
                return;
            }

            var width = region.Width;
            Parallelism.For(region.Height, width, delegate(int start, int end)
            {
                var alpha = new float[width];
                var sr = new float[width];
                var sg = new float[width];
                var sb = new float[width];
                for (var y = region.Top + start; y < region.Top + end; y++)
                {
                    var s = source.RowOffset(y, region.Left);
                    Array.Copy(source.A, s, alpha, 0, width);
                    Array.Copy(source.R, s, sr, 0, width);
                    Array.Copy(source.G, s, sg, 0, width);
                    Array.Copy(source.B, s, sb, 0, width);
                    for (var chain = masks; chain != null; chain = chain.Next)
                    {
                        chain.Sampler.MultiplyRow(y, region.Left, alpha);
                    }

                    CompositeLayerRow(target, y, region.Left, mode, sr, sg, sb, alpha, opacity, 1f, gate, gateBackdrop, clipMode);
                }
            });
        }

        // __PART3__
        private static MaskChain Chain(MaskChain chain, MaskSampler sampler)
        {
            return sampler == null ? chain : new MaskChain(sampler, chain);
        }

        /// <summary>
        /// Combined raster and vector mask of a layer over region, or null when none
        /// applies. Rows [top, bottom) bounds the rows a caller will sample; a band
        /// render passes its band so unfeathered masks decode and rasterize only those
        /// rows. The raster always follows region, the region of a whole render, so
        /// every row comes out bit-identical to that render.
        /// </summary>
        private MaskSampler LayerMasks(PsdLayer layer, PsdRect region, PsdRect rows, int top, int bottom)
        {
            MaskSampler result = null;
            if (layer.Mask != null)
            {
                var mask = layer.Mask;
                result = BandMask(layer, mask, top, bottom, delegate
                {
                    return MaskSampler.FromLayerMask(layer, mask, _canvas, top, bottom, _readers);
                });
            }

            MaskSampler vector = null;
            if (!ShapeOwnsVectorMask(layer))
            {
                if (layer.RenderedVectorMask != null)
                {
                    var rendered = layer.RenderedVectorMask;
                    vector = BandMask(layer, rendered, top, bottom, delegate
                    {
                        return MaskSampler.FromRenderedVectorMask(layer, rendered, _canvas, top, bottom, _readers);
                    });
                }
                else if (layer.VectorMask != null && !region.IsEmpty)
                {
                    var path = layer.VectorMask;
                    var reach = layer.VectorMaskFeather > 0 ? (int)Math.Ceiling(layer.VectorMaskFeather * 3) + 1 : 0;
                    // The vector feather does not clamp at the canvas: rasterize the path
                    // past the edge so the blur sees real coverage there.
                    var limit = new PsdRect(_canvas.Left - reach, _canvas.Top - reach, _canvas.Right + reach, _canvas.Bottom + reach);
                    var vectorRegion = new PsdRect(region.Left - reach, region.Top - reach, region.Right + reach, region.Bottom + reach).Intersect(limit);
                    vector = BandVectorMask(layer, vectorRegion, top, bottom, delegate
                    {
                        return MaskSampler.FromVectorMask(path, vectorRegion, layer.VectorMaskDensity, layer.VectorMaskFeather, _canvas, top, bottom);
                    });
                }
            }

            if (vector == null)
            {
                return result;
            }

            return result == null ? vector : MaskSampler.Multiply(result, vector);
        }

        /// <summary>
        /// Inflates the stored pixels of every layer the render can reach (visible, with
        /// visible ancestors and nonzero opacity) in parallel before compositing starts.
        /// </summary>
        private void PrefetchPixels(HashSet<PsdLayer> only)
        {
            var layers = new List<PsdLayer>();
            foreach (var layer in ReachableLayers())
            {
                if (only == null || only.Contains(layer))
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
            var area = 0L;
            foreach (var layer in layers)
            {
                area += (long)layer.Bounds.Width * layer.Bounds.Height;
            }

            Parallelism.For(layers.Count, area / layers.Count, delegate(int start, int end)
            {
                for (var i = start; i < end; i++)
                {
                    try
                    {
                        decoded[i] = layers[i].DecodePixels();
                        succeeded[i] = true;
                    }
                    catch (PsdFormatException)
                    {
                        // Left to LayerPixels: a damaged layer fails the render only if it is drawn.
                    }
                }
            });

            for (var i = 0; i < layers.Count; i++)
            {
                if (succeeded[i])
                {
                    _prefetched[layers[i]] = decoded[i];
                }
            }
        }

        /// <summary>Content layers with stored channels that the render can reach: visible with visible ancestors, nonzero opacity.</summary>
        private IEnumerable<PsdLayer> ReachableLayers()
        {
            foreach (var layer in _document.Layers)
            {
                if (layer.Kind == PsdLayerKind.Group || layer.Kind == PsdLayerKind.Adjustment || layer.Bounds.IsEmpty || layer.Opacity == 0 || layer.Channels.Count == 0)
                {
                    continue;
                }

                var reachable = true;
                for (var node = layer; node != null && reachable; node = node.Parent)
                {
                    reachable = IsVisible(node);
                }

                if (reachable)
                {
                    yield return layer;
                }
            }
        }

        /// <summary>Decoded pixels for a content layer, synthesizing solid, pattern and gradient fills when the layer stores none.</summary>
        private PlanarImage LayerPixels(PsdLayer layer)
        {
            if (_window.HasValue && _wholeLayers != null && !_wholeLayers.Contains(layer))
            {
                return BandPixels(layer, _window.Value);
            }

            PlanarImage cached;
            if (_pixelCache.TryGetValue(layer, out cached))
            {
                return cached;
            }

            PlanarImage prefetched = null;
            if (_prefetched.ContainsKey(layer))
            {
                prefetched = _prefetched[layer];
                _prefetched.Remove(layer);
            }

            var pixels = prefetched != null ? prefetched : layer.DecodePixels();
            if (layer.Kind == PsdLayerKind.Text && _options.TextRasterizer != null)
            {
                pixels = TextLayerPixels(layer, _options.TextRasterizer, pixels);
            }
            if (layer.Kind == PsdLayerKind.Fill && (pixels == null || IsEmptyCoverage(pixels)) && StrokedShapePixels(layer) != null)
            {
                pixels = StrokedShapePixels(layer);
            }
            else if (layer.Kind == PsdLayerKind.Fill && (pixels == null || IsEmptyCoverage(pixels)) && layer.FillColor.HasValue)
            {
                // Fill layers render their content from the fill settings; files that
                // carry no channel data get a canvas-wide solid fill that the vector
                // mask then shapes.
                var color = layer.FillColor.Value;
                pixels = new PlanarImage(_canvas);
                pixels.Fill(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
            }
            else if (layer.Kind == PsdLayerKind.Fill && layer.ContentKey == "PtFl" && layer.FillDescriptor != null && (pixels == null || IsEmptyCoverage(pixels)))
            {
                pixels = PatternFillPixels(layer, layer.FillDescriptor, _canvas);
            }
            else if (layer.Kind == PsdLayerKind.Fill && layer.ContentKey == "GdFl" && layer.FillDescriptor != null && (pixels == null || IsEmptyCoverage(pixels)))
            {
                pixels = GradientFillPixels(layer, layer.FillDescriptor, _canvas);
            }

            _pixelCache[layer] = pixels;
            return pixels;
        }

        // __PART4__
        /// <summary>
        /// Gradient fill layers span the center chord of their shape's bounds when
        /// aligned (the canvas otherwise), with Photoshop's eased two-stop ramp and
        /// dither (reference vector_raster.cpp, GdFl branch, gradient_color_dithered).
        /// The vector mask shapes it later.
        /// </summary>
        private PlanarImage GradientFillPixels(PsdLayer layer, Descriptor descriptor, PsdRect rows)
        {
            var gradient = Gradient.FromDescriptor(descriptor, _document.Colors, 0);
            if (gradient == null)
            {
                return null;
            }

            GradientBox placement;
            var shape = PathGradientBounds(layer.VectorMask);
            if (shape.HasValue)
            {
                placement = shape.Value;
            }
            else
            {
                var maskBounds = MaskGradientBounds(layer.Mask);
                placement = maskBounds.HasValue ? maskBounds.Value : GradientBox.From(_canvas);
            }

            var frame = gradient.Frame(placement, GradientSpan.CenterChord);
            var dither = descriptor.GetBoolean("Dthr");
            var pixels = new PlanarImage(_canvas.Intersect(rows)) { Logical = _canvas };
            var area = pixels.Bounds;
            Parallelism.For(area.Height, area.Width * 4L, delegate(int start, int end)
            {
                for (var y = area.Top + start; y < area.Top + end; y++)
                {
                    for (var x = area.Left; x < area.Right; x++)
                    {
                        var position = frame.Position(x, y);
                        float r, g, b;
                        gradient.Color(position, true, out r, out g, out b);
                        if (dither)
                        {
                            DitherGradientColor(ref r, ref g, ref b, x, y);
                        }

                        var i = pixels.RowOffset(y, x);
                        pixels.R[i] = r;
                        pixels.G[i] = g;
                        pixels.B[i] = b;
                        pixels.A[i] = gradient.Opacity(position, true);
                    }
                }
            });

            return pixels;
        }

        /// <summary>
        /// The bounds an aligned gradient fill spans: its path's exact bounds (anchors
        /// and control points, clipped to the canvas); null without a path. Photoshop
        /// measures the path itself, not its pixel hull.
        /// </summary>
        private GradientBox? PathGradientBounds(VectorPath path)
        {
            if (path == null || path.Subpaths.Count == 0)
            {
                return null;
            }

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var subpath in path.Subpaths)
            {
                foreach (var k in subpath.Knots)
                {
                    left = Math.Min(left, Math.Min(k.X, Math.Min(k.InX, k.OutX)));
                    top = Math.Min(top, Math.Min(k.Y, Math.Min(k.InY, k.OutY)));
                    right = Math.Max(right, Math.Max(k.X, Math.Max(k.InX, k.OutX)));
                    bottom = Math.Max(bottom, Math.Max(k.Y, Math.Max(k.InY, k.OutY)));
                }
            }

            left = Math.Max(left, _canvas.Left);
            top = Math.Max(top, _canvas.Top);
            right = Math.Min(right, _canvas.Right);
            bottom = Math.Min(bottom, _canvas.Bottom);
            if (right > left && bottom > top)
            {
                return new GradientBox((float)left, (float)top, (float)(right - left), (float)(bottom - top));
            }

            return null;
        }

        /// <summary>
        /// Without a path, an aligned gradient fill spans its raster mask's rectangle
        /// when the mask hides everything outside it (default color 0).
        /// </summary>
        private GradientBox? MaskGradientBounds(LayerMask mask)
        {
            if (mask == null || mask.Disabled || mask.DefaultColor != 0)
            {
                return null;
            }

            var bounds = mask.Bounds.Intersect(_canvas);
            if (bounds.IsEmpty)
            {
                return null;
            }

            return GradientBox.From(bounds);
        }

        /// <summary>Pattern fill layers tile their pattern over the canvas; the vector mask shapes it later.</summary>
        private PlanarImage PatternFillPixels(PsdLayer layer, Descriptor descriptor, PsdRect rows)
        {
            var placement = PatternPlacement.FromDescriptor(descriptor);
            PatternTile tile;
            if (placement == null || !_document.Patterns.TryGetValue(placement.PatternId, out tile))
            {
                return null;
            }

            var sampler = new PatternSampler(tile, placement, layer);
            var pixels = new PlanarImage(_canvas.Intersect(rows)) { Logical = _canvas };
            var area = pixels.Bounds;
            Parallelism.For(area.Height, area.Width * 4L, delegate(int start, int end)
            {
                for (var y = area.Top + start; y < area.Top + end; y++)
                {
                    for (var x = area.Left; x < area.Right; x++)
                    {
                        var i = pixels.RowOffset(y, x);
                        float r, g, b, a;
                        sampler.Sample(x, y, out r, out g, out b, out a);
                        pixels.R[i] = r;
                        pixels.G[i] = g;
                        pixels.B[i] = b;
                        pixels.A[i] = a;
                    }
                }
            });

            return pixels;
        }

        private static bool IsEmptyCoverage(PlanarImage image)
        {
            foreach (var a in image.A)
            {
                if (a > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private PsdRect RenderBounds(PsdLayer layer)
        {
            if (layer.Kind == PsdLayerKind.Group)
            {
                return ContentBounds(layer);
            }

            if (layer.Kind == PsdLayerKind.Adjustment)
            {
                return _canvas;
            }

            var pixels = LayerPixels(layer);
            return pixels != null ? pixels.Logical : default(PsdRect);
        }

        private PsdRect ContentBounds(PsdLayer group)
        {
            var bounds = default(PsdRect);
            foreach (var child in group.Children)
            {
                if (!IsVisible(child))
                {
                    continue;
                }

                if (child.Kind == PsdLayerKind.Adjustment)
                {
                    return _canvas;
                }

                bounds = bounds.Union(RenderBounds(child));
            }

            return bounds;
        }

        private bool HasAdjustmentDescendant(PsdLayer group)
        {
            foreach (var child in group.Children)
            {
                if (!IsVisible(child))
                {
                    continue;
                }

                if (child.Kind == PsdLayerKind.Adjustment || (child.Kind == PsdLayerKind.Group && HasAdjustmentDescendant(child)))
                {
                    return true;
                }
            }

            return false;
        }

        private static PlanarImage Crop(PlanarImage source, PsdRect region)
        {
            var copy = new PlanarImage(region);
            var width = region.Width;
            for (var y = region.Top; y < region.Bottom; y++)
            {
                var s = source.RowOffset(y, region.Left);
                var d = copy.RowOffset(y, region.Left);
                Array.Copy(source.R, s, copy.R, d, width);
                Array.Copy(source.G, s, copy.G, d, width);
                Array.Copy(source.B, s, copy.B, d, width);
                Array.Copy(source.A, s, copy.A, d, width);
            }

            return copy;
        }

        /// <summary>
        /// Photoshop's Dissolve: each pixel paints fully or not at all, with a
        /// deterministic per-document-pixel threshold (splitmix64 over x, y).
        /// </summary>
        private static void Dissolve(float[] alpha, int x0, int y)
        {
            for (var i = 0; i < alpha.Length; i++)
            {
                var a = alpha[i];
                if (a <= 0f || a >= 1f)
                {
                    continue;
                }

                var key = (ulong)(uint)(x0 + i) | ((ulong)(uint)y << 32);
                var threshold = (uint)(SplitMix64(key) >> 40);
                var probability = (uint)Math.Round(a * (1 << 24));
                alpha[i] = probability > threshold ? 1f : 0f;
            }
        }

        private static ulong SplitMix64(ulong value)
        {
            value += 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }

        /// <summary>
        /// A dithered gradient (Dthr): the color rounds to bytes and moves by a
        /// per-pixel offset in [-1, 2] from the top bits of the splitmix64 hash of the
        /// document coordinate (reference apply_gradient_dither, blend_math.cpp).
        /// </summary>
        internal static void DitherGradientColor(ref float r, ref float g, ref float b, int x, int y)
        {
            var hash = SplitMix64((ulong)(uint)x | ((ulong)(uint)y << 32));
            var delta = (int)((hash >> 61) & 3) - 1;
            r = DitherChannel(r, delta);
            g = DitherChannel(g, delta);
            b = DitherChannel(b, delta);
        }

        private static float DitherChannel(float value, int delta)
        {
            var level = (int)Math.Round(Compat.Clamp(value, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
            return Compat.Clamp(level + delta, 0, 255) / 255f;
        }
    }
}
