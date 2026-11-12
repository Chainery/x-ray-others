using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Shape layers with a vector stroke (vstk), following rasterize_vector_shape in
    /// .reference/src/core/vector_raster.cpp: the fill paints under the path's coverage
    /// (unless fillEnabled is off), the stroke paints its own band on top with its
    /// opacity and blend mode, and the result becomes the layer's pixels. The path is
    /// baked in here, so LayerMasks skips the vector mask for these layers.
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private const int MaxFeatherMargin = 256;

        private readonly HashSet<PsdLayer> _strokedShapes = new HashSet<PsdLayer>();

        private readonly Dictionary<PsdLayer, ShapePlanes> _shapePlanes = new Dictionary<PsdLayer, ShapePlanes>();

        /// <summary>
        /// The extra planes of a shape rendered here, each over the layer's pixel bounds
        /// (reference ShapeRasterResult). Fill and Stroke are the split planes interior
        /// overlays need: the overlays cover the fill only and the stroke re-composites
        /// above them. They exist when both paint and the stroke blends Normal (a
        /// non-Normal stroke folds against the fill during the bake). Matte is the effect
        /// silhouette: the layer with alpha raised to the fill and stroke coverage, kept
        /// only when a gradient or pattern fill made the two differ.
        /// </summary>
        internal sealed class ShapePlanes
        {
            public ShapePlanes(PlanarImage fill, PlanarImage stroke, PlanarImage matte)
            {
                Fill = fill;
                Stroke = stroke;
                Matte = matte;
            }

            public PlanarImage Fill { get; private set; }

            public PlanarImage Stroke { get; private set; }

            public PlanarImage Matte { get; private set; }
        }

        /// <summary>Whether the layer's pixels already carry its path coverage (a stroked or fill-disabled shape).</summary>
        private bool ShapeOwnsVectorMask(PsdLayer layer)
        {
            return layer.VectorStroke != null && LayerPixels(layer) != null && _strokedShapes.Contains(layer);
        }

        /// <summary>
        /// Renders a shape layer whose stroke is on or whose fill is off, or returns null
        /// so the plain fill-plus-vector-mask path handles it.
        /// </summary>
        private PlanarImage StrokedShapePixels(PsdLayer layer)
        {
            if (layer.Kind != PsdLayerKind.Fill || layer.VectorStroke == null || layer.VectorMask == null)
            {
                return null;
            }

            var stroke = layer.VectorStroke;
            var path = layer.VectorMask;
            string paintKey;
            Descriptor paintContent;
            StrokePaintContent(layer, stroke, out paintKey, out paintContent);
            var strokeOn = stroke.Enabled && stroke.Width > 0 && stroke.Opacity > 0 && paintKey != null
                && path.Subpaths.Count > 0 && !path.Disabled;
            var fillOn = stroke.FillEnabled;
            if (fillOn && !strokeOn)
            {
                return null;
            }

            // A feathered shape blurs fill and stroke together, rasterized past the canvas by
            // the blur reach so the canvas edge does not clamp it (reference feather_shape_raster).
            var radii = layer.VectorMaskFeather > 0 ? MaskSampler.BoxRadii(layer.VectorMaskFeather) : new[] { 0, 0, 0 };
            var reach = radii[0] + radii[1] + radii[2];

            // The margin is capped so a huge feather cannot allocate an unbounded plane; past
            // it the blur clamps at the domain edge.
            var margin = Math.Min(reach, MaxFeatherMargin);
            var domain = new PsdRect(_canvas.Left - margin, _canvas.Top - margin, _canvas.Right + margin, _canvas.Bottom + margin);
            var image = new PlanarImage(domain);
            var width = domain.Width;
            var shapeBounds = PathGradientBounds(path);

            // Density shows the fill everywhere at (255 - density) / 255, the vector-mask rule.
            float[] fillCoverage = null;
            if (fillOn)
            {
                var fill = PaintContent(layer, layer.ContentKey, layer.FillDescriptor, domain, shapeBounds);
                if (fill != null)
                {
                    fillCoverage = new float[image.A.Length];
                    for (var i = 0; i < fillCoverage.Length; i++)
                    {
                        fillCoverage[i] = 1f;
                    }

                    var coverage = MaskSampler.FromVectorMask(path, domain, layer.VectorMaskDensity, 0, domain);
                    Parallelism.For(domain.Height, width, delegate(int start, int end)
                    {
                        for (var y = domain.Top + start; y < domain.Top + end; y++)
                        {
                            var o = fill.RowOffset(y, domain.Left);
                            var row = new float[width];
                            Array.Copy(fillCoverage, o, row, 0, width);
                            if (coverage != null)
                            {
                                coverage.MultiplyRow(y, domain.Left, row);
                            }

                            Array.Copy(row, 0, fillCoverage, o, width);
                            var alpha = new float[width];
                            Array.Copy(fill.A, o, alpha, 0, width);
                            BlendKernels.MultiplyInPlace(alpha, row);
                            Array.Copy(alpha, 0, fill.A, o, width);
                        }
                    });

                    Array.Copy(fill.R, image.R, Math.Min(fill.R.Length, image.R.Length));
                    Array.Copy(fill.G, image.G, Math.Min(fill.G.Length, image.G.Length));
                    Array.Copy(fill.B, image.B, Math.Min(fill.B.Length, image.B.Length));
                    Array.Copy(fill.A, image.A, Math.Min(fill.A.Length, image.A.Length));
                }
            }

            var fillPaints = false;
            if (fillCoverage != null)
            {
                for (var i = 0; i < fillCoverage.Length; i++)
                {
                    if (fillCoverage[i] != 0f)
                    {
                        fillPaints = true;
                        break;
                    }
                }
            }

            PlanarImage fillPlane = null;
            PlanarImage strokePlane = null;
            float[] strokeCoverage = null;
            if (strokeOn)
            {
                // Split planes for interior overlays (reference rasterize_vector_shape, split_planes).
                var split = fillPaints && stroke.BlendMode == PsdBlendMode.Normal;
                fillPlane = split ? image.Clone() : null;
                strokePlane = split ? new PlanarImage(domain) : null;
                strokeCoverage = CompositeStroke(image, layer, path, stroke, paintKey, paintContent, strokePlane);
                if (strokeCoverage == null)
                {
                    fillPlane = null;
                    strokePlane = null;
                }
            }

            // The effect silhouette of a gradient or pattern fill: coverage, not painted alpha
            // (.reference/docs/layer-effects-render.md, "Effect silhouette of shape layers").
            PlanarImage matte = null;
            if (fillCoverage != null && (layer.ContentKey == "GdFl" || layer.ContentKey == "PtFl"))
            {
                matte = image.Clone();
                var fillDiffers = RaiseAlpha(matte.A, fillCoverage);
                var differs = fillDiffers || (strokeCoverage != null && RaiseAlpha(matte.A, strokeCoverage));
                if (fillDiffers && fillPlane != null)
                {
                    // Overlays cover the fill's whole coverage too.
                    RaiseAlpha(fillPlane.A, fillCoverage);
                }

                if (!differs)
                {
                    matte = null;
                }
            }

            if (reach > 0)
            {
                image = FeatherShape(image, radii, _canvas);
                fillPlane = fillPlane == null ? null : FeatherShape(fillPlane, radii, _canvas);
                strokePlane = strokePlane == null ? null : FeatherShape(strokePlane, radii, _canvas);
                matte = matte == null ? null : FeatherShape(matte, radii, _canvas);
            }

            if (fillPlane != null || matte != null)
            {
                _shapePlanes[layer] = new ShapePlanes(fillPlane, strokePlane, matte);
            }

            _strokedShapes.Add(layer);
            return image;
        }

        /// <summary>Raises alpha to coverage; true when any value rose.</summary>
        private static bool RaiseAlpha(float[] alpha, float[] coverage)
        {
            var raised = 0;
            Parallelism.For(alpha.Length, 1, delegate(int start, int end)
            {
                var stripRaised = false;
                for (var i = start; i < end; i++)
                {
                    if (coverage[i] > alpha[i])
                    {
                        alpha[i] = coverage[i];
                        stripRaised = true;
                    }
                }

                if (stripRaised)
                {
                    System.Threading.Volatile.Write(ref raised, 1);
                }
            });

            return raised != 0;
        }

        /// <summary>
        /// Paints the stroke band over image, optionally also writing the standalone stroke
        /// plane (paint color, coverage times paint alpha times stroke opacity). Returns the
        /// band's coverage over the image domain, or null when it paints nothing.
        /// </summary>
        private float[] CompositeStroke(PlanarImage image, PsdLayer layer, VectorPath path, VectorStrokeStyle stroke, string paintKey, Descriptor paintContent, PlanarImage strokePlane)
        {
            PsdRect rect;
            var coverage = VectorStroker.Rasterize(path, stroke, image.Bounds, out rect);
            var painted = TrimmedBounds(coverage, rect);
            if (painted.IsEmpty)
            {
                return null;
            }

            // Aligned gradients span the stroke's own painted bounds.
            var content = PaintContent(layer, paintKey, paintContent, painted, GradientBox.From(painted));
            if (content == null)
            {
                return null;
            }

            var mode = stroke.BlendMode == PsdBlendMode.PassThrough || stroke.BlendMode == PsdBlendMode.Dissolve ? PsdBlendMode.Normal : stroke.BlendMode;
            var opacity = (float)stroke.Opacity;
            var width = painted.Width;
            Parallelism.For(painted.Height, width, delegate(int start, int end)
            {
                var alpha = new float[width];
                var cr = new float[width];
                var cg = new float[width];
                var cb = new float[width];
                var dr = new float[width];
                var dg = new float[width];
                var db = new float[width];
                var da = new float[width];
                for (var y = painted.Top + start; y < painted.Top + end; y++)
                {
                    var s = content.RowOffset(y, painted.Left);
                    var c = ((y - rect.Top) * rect.Width) + (painted.Left - rect.Left);
                    var t = image.RowOffset(y, painted.Left);
                    Array.Copy(content.A, s, alpha, 0, width);
                    var band = new float[width];
                    Array.Copy(coverage, c, band, 0, width);
                    BlendKernels.MultiplyInPlace(alpha, band);
                    BlendKernels.Scale(alpha, opacity);
                    if (strokePlane != null)
                    {
                        Array.Copy(content.R, s, strokePlane.R, t, width);
                        Array.Copy(content.G, s, strokePlane.G, t, width);
                        Array.Copy(content.B, s, strokePlane.B, t, width);
                        Array.Copy(alpha, 0, strokePlane.A, t, width);
                    }

                    // Non-Normal stroke modes blend against the fill within the same raster.
                    Array.Copy(content.R, s, cr, 0, width);
                    Array.Copy(content.G, s, cg, 0, width);
                    Array.Copy(content.B, s, cb, 0, width);
                    Array.Copy(image.R, t, dr, 0, width);
                    Array.Copy(image.G, t, dg, 0, width);
                    Array.Copy(image.B, t, db, 0, width);
                    Array.Copy(image.A, t, da, 0, width);
                    BlendKernels.CompositeRow(mode, cr, cg, cb, alpha, dr, dg, db, da, false, null);
                    Array.Copy(dr, 0, image.R, t, width);
                    Array.Copy(dg, 0, image.G, t, width);
                    Array.Copy(db, 0, image.B, t, width);
                    Array.Copy(da, 0, image.A, t, width);
                }
            });

            var domainCoverage = new float[image.A.Length];
            var bandWidth = painted.Width;
            for (var y = painted.Top; y < painted.Bottom; y++)
            {
                var source = ((y - rect.Top) * rect.Width) + (painted.Left - rect.Left);
                var target = image.RowOffset(y, painted.Left);
                Array.Copy(coverage, source, domainCoverage, target, bandWidth);
            }

            return domainCoverage;
        }

        /// <summary>
        /// The stroke paint: strokeStyleContent, or the legacy vscg block (content key,
        /// then a versioned descriptor) for stroke-only shapes that omit it. With filling
        /// on, vscg is the fill's paint and never the stroke's (reference
        /// psd_document_io.cpp, legacy_vector_content).
        /// </summary>
        private static void StrokePaintContent(PsdLayer layer, VectorStrokeStyle stroke, out string key, out Descriptor content)
        {
            if (stroke.ContentKey != null)
            {
                key = stroke.ContentKey;
                content = stroke.Content;
                return;
            }

            var legacy = layer.GetTaggedBlock("vscg");
            if (!stroke.FillEnabled && legacy != null && legacy.Length > 8)
            {
                var keyBytes = new byte[4];
                Array.Copy(legacy, 0, keyBytes, 0, 4);
                var legacyKey = BigEndianReader.DecodeLatin1(keyBytes);
                if (legacyKey == "SoCo" || legacyKey == "GdFl" || legacyKey == "PtFl")
                {
                    var descriptor = PsdParser.TryReadDescriptor(legacy, 4);
                    if (descriptor != null)
                    {
                        key = legacyKey;
                        content = descriptor;
                        return;
                    }
                }
            }

            key = null;
            content = null;
        }

        /// <summary>
        /// Paints fill-layer content (SoCo solid, GdFl gradient, PtFl pattern) over rect.
        /// Aligned gradients span alignBounds (the canvas when null) and dither when Dthr
        /// is set (reference paint_coverage, gradient_color_dithered).
        /// </summary>
        private PlanarImage PaintContent(PsdLayer layer, string key, Descriptor descriptor, PsdRect rect, GradientBox? alignBounds)
        {
            if (descriptor == null || rect.IsEmpty)
            {
                return null;
            }

            var pixels = new PlanarImage(rect);
            if (key == "SoCo")
            {
                var color = descriptor.GetColor("Clr ");
                if (!color.HasValue)
                {
                    return null;
                }

                pixels.Fill(color.Value.R / 255f, color.Value.G / 255f, color.Value.B / 255f, 1f);
                return pixels;
            }

            if (key == "GdFl")
            {
                var gradient = Gradient.FromDescriptor(descriptor, _document.Colors, 0);
                if (gradient == null)
                {
                    return null;
                }

                var placement = gradient.AlignWithLayer && alignBounds.HasValue ? alignBounds.Value : GradientBox.From(_canvas);
                var frame = gradient.Frame(placement, GradientSpan.CenterChord);
                var dither = descriptor.GetBoolean("Dthr");
                Parallelism.For(rect.Height, rect.Width * 4L, delegate(int start, int end)
                {
                    for (var y = rect.Top + start; y < rect.Top + end; y++)
                    {
                        for (var x = rect.Left; x < rect.Right; x++)
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

            if (key == "PtFl")
            {
                var placement = PatternPlacement.FromDescriptor(descriptor);
                PatternTile tile;
                if (placement == null || !_document.Patterns.TryGetValue(placement.PatternId, out tile))
                {
                    return null;
                }

                var sampler = new PatternSampler(tile, placement, layer);
                Parallelism.For(rect.Height, rect.Width * 4L, delegate(int start, int end)
                {
                    for (var y = rect.Top + start; y < rect.Top + end; y++)
                    {
                        for (var x = rect.Left; x < rect.Right; x++)
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

            return null;
        }

        /// <summary>Bounds of the non-zero pixels of a coverage plane.</summary>
        private static PsdRect TrimmedBounds(float[] coverage, PsdRect rect)
        {
            if (rect.IsEmpty)
            {
                return default(PsdRect);
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = 0; y < rect.Height; y++)
            {
                var rowOffset = y * rect.Width;
                var first = -1;
                var last = -1;
                for (var x = 0; x < rect.Width; x++)
                {
                    if (coverage[rowOffset + x] != 0f)
                    {
                        if (first < 0)
                        {
                            first = x;
                        }

                        last = x;
                    }
                }

                if (first < 0)
                {
                    continue;
                }

                minX = Math.Min(minX, first);
                maxX = Math.Max(maxX, last);
                minY = Math.Min(minY, y);
                maxY = y;
            }

            return maxX < 0 ? default(PsdRect) : new PsdRect(rect.Left + minX, rect.Top + minY, rect.Left + maxX + 1, rect.Top + maxY + 1);
        }

        /// <summary>
        /// Shape feather: the vector-mask gaussian (three box passes per axis) over the
        /// premultiplied shape raster, then cropped to clip.
        /// </summary>
        private static PlanarImage FeatherShape(PlanarImage image, int[] radii, PsdRect clip)
        {
            var width = image.Width;
            var height = image.Height;
            for (var i = 0; i < image.A.Length; i++)
            {
                var a = image.A[i];
                image.R[i] *= a;
                image.G[i] *= a;
                image.B[i] *= a;
            }

            var scratch = new float[image.A.Length];
            var planes = new[] { image.R, image.G, image.B, image.A };
            foreach (var plane in planes)
            {
                foreach (var radius in radii)
                {
                    if (radius > 0)
                    {
                        BoxBlur(plane, scratch, width, height, radius, true);
                        BoxBlur(scratch, plane, width, height, radius, false);
                    }
                }
            }

            var cropped = new PlanarImage(clip);
            var cropWidth = clip.Width;
            for (var y = clip.Top; y < clip.Bottom; y++)
            {
                var s = image.RowOffset(y, clip.Left);
                var d = cropped.RowOffset(y, clip.Left);
                for (var x = 0; x < cropWidth; x++)
                {
                    var a = Compat.Clamp(image.A[s + x], 0f, 1f);
                    var inverse = a > 1e-6f ? 1f / a : 0f;
                    cropped.R[d + x] = Compat.Clamp(image.R[s + x] * inverse, 0f, 1f);
                    cropped.G[d + x] = Compat.Clamp(image.G[s + x] * inverse, 0f, 1f);
                    cropped.B[d + x] = Compat.Clamp(image.B[s + x] * inverse, 0f, 1f);
                    cropped.A[d + x] = a;
                }
            }

            return cropped;
        }

        // Edge-clamped running box filter from source into destination.
        private static void BoxBlur(float[] source, float[] destination, int width, int height, int radius, bool horizontal)
        {
            var length = horizontal ? width : height;
            var lines = horizontal ? height : width;
            var step = horizontal ? 1 : width;
            var inverse = 1f / ((2 * radius) + 1);
            Parallelism.For(lines, length + (2L * radius), delegate(int first, int last)
            {
                var line = first;
                for (; line < last; line++)
                {
                    var baseIndex = horizontal ? line * width : line;
                    var sum = 0f;
                    for (var i = -radius; i <= radius; i++)
                    {
                        sum += source[baseIndex + (Compat.Clamp(i, 0, length - 1) * step)];
                    }

                    for (var i = 0; i < length; i++)
                    {
                        destination[baseIndex + (i * step)] = sum * inverse;
                        sum += source[baseIndex + (Compat.Clamp(i + radius + 1, 0, length - 1) * step)]
                            - source[baseIndex + (Compat.Clamp(i - radius, 0, length - 1) * step)];
                    }
                }
            });
        }
    }
}
