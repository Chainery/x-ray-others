using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Layer-effect passes, following the order and rules of the reference
    /// composite_pixel_layer (.reference/src/render/layer_compositor.hpp)
    /// and .reference/docs/layer-effects-render.md:
    /// drop shadows and outer glows below the layer; gradient and color overlays
    /// and satin folded into the layer's own color; inner glows, inner shadows
    /// and strokes above it. Effects scale with layer opacity but not with Fill.
    /// net45 port: effects come from PsdLayerStyle's PsdEffect list (Layers/Effects.cs)
    /// instead of a dedicated render-side parser.
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private readonly Dictionary<PsdLayer, StyledEffects> _effectsCache = new Dictionary<PsdLayer, StyledEffects>();
        private readonly Dictionary<PsdEffect, Gradient> _gradientOfEffect = new Dictionary<PsdEffect, Gradient>();

        /// <summary>A float plane in document space.</summary>
        private sealed class Plane
        {
            public Plane(PsdRect rect, float[] data)
            {
                Rect = rect;
                Data = data;
            }

            public PsdRect Rect { get; private set; }

            public float[] Data { get; private set; }

            public float At(int x, int y)
            {
                return Rect.Contains(x, y) ? Data[((y - Rect.Top) * Rect.Width) + (x - Rect.Left)] : 0f;
            }

            public float[] Row(int y, int x0, int width)
            {
                var row = new float[width];
                Array.Copy(Data, ((y - Rect.Top) * Rect.Width) + (x0 - Rect.Left), row, 0, width);
                return row;
            }
        }

        /// <summary>Per-pixel effect color (straight, 0..1) and an extra alpha factor.</summary>
        internal struct EffectColor
        {
            public EffectColor(float r, float g, float b, float a)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }

            public float R;
            public float G;
            public float B;
            public float A;
        }

        internal delegate EffectColor EffectPaint(int x, int y);

        /// <summary>A layer's enabled effects grouped by kind, in file order.</summary>
        internal sealed class StyledEffects
        {
            public readonly List<PsdEffect> DropShadows = new List<PsdEffect>();
            public readonly List<PsdEffect> InnerShadows = new List<PsdEffect>();
            public readonly List<PsdEffect> OuterGlows = new List<PsdEffect>();
            public readonly List<PsdEffect> InnerGlows = new List<PsdEffect>();
            public readonly List<PsdEffect> Bevels = new List<PsdEffect>();
            public readonly List<PsdEffect> Satins = new List<PsdEffect>();
            public readonly List<PsdEffect> Overlays = new List<PsdEffect>();
            public readonly List<PsdEffect> Strokes = new List<PsdEffect>();

            /// <summary>"Layer Mask Hides Effects" (lmgm/vmgm).</summary>
            public bool MaskHidesEffects;
        }

        /// <summary>Everything the effect passes of one layer share.</summary>
        private sealed class StyledLayer
        {
            public PsdLayer Layer;

            public StyledEffects Effects;

            public PlanarImage Source;

            /// <summary>The effect domain: source bounds plus the largest effect reach.</summary>
            public PsdRect Domain;

            /// <summary>Layer coverage over the domain: pixel alpha times the layer's masks (unless masks hide effects).</summary>
            public float[] Matte;

            public MaskSampler LayerMask;

            /// <summary>
            /// The matte is the shape's coverage rather than the pixels' alpha (a gradient or
            /// pattern fill that is transparent inside its shape): overlays and satin then
            /// paint as passes over the matte instead of folding into the layer's color.
            /// </summary>
            public bool HasEffectMatte;

            /// <summary>Pass-through groups fade their whole result afterwards, so their effects draw at full strength.</summary>
            public float? OpacityOverride;

            public float Opacity
            {
                get { return OpacityOverride.HasValue ? OpacityOverride.Value : Layer.Opacity / 255f; }
            }

            public float Fill
            {
                get { return Layer.FillOpacity / 255f; }
            }

            public float MatteAt(int x, int y)
            {
                return Domain.Contains(x, y) ? Matte[((y - Domain.Top) * Domain.Width) + (x - Domain.Left)] : 0f;
            }

            /// <summary>Content knockout from strokes without Overprint (1 = keep content).</summary>
            public Plane Knockout;

            /// <summary>Stroke planes drawn above the content (inner band share).</summary>
            public readonly List<StrokePlane> StrokePlanes = new List<StrokePlane>();

            /// <summary>Stroke planes drawn under the content (outer band share, where content is missing).</summary>
            public readonly List<StrokePlane> StrokeUnderlays = new List<StrokePlane>();

            /// <summary>The union of the visible stroke bands over the domain, the surface a Stroke Emboss bevel shades.</summary>
            public float[] StrokeEmbossMatte;
        }

        private sealed class StrokePlane
        {
            public StrokePlane(PsdEffect stroke, Plane plane, EffectPaint paint)
            {
                Stroke = stroke;
                Plane = plane;
                Paint = paint;
            }

            public PsdEffect Stroke { get; private set; }

            public Plane Plane { get; private set; }

            public EffectPaint Paint { get; private set; }
        }

        private StyledEffects EffectsOf(PsdLayer layer)
        {
            StyledEffects cached;
            if (_effectsCache.TryGetValue(layer, out cached))
            {
                return cached;
            }

            StyledEffects effects = null;
            var style = layer.Style;
            if (style != null && style.Visible && style.Effects.Count > 0)
            {
                effects = new StyledEffects();
                foreach (var effect in style.Effects)
                {
                    if (!effect.Enabled)
                    {
                        continue;
                    }

                    switch (effect.Kind)
                    {
                        case PsdEffectKind.DropShadow:
                            effects.DropShadows.Add(effect);
                            break;
                        case PsdEffectKind.InnerShadow:
                            effects.InnerShadows.Add(effect);
                            break;
                        case PsdEffectKind.OuterGlow:
                            effects.OuterGlows.Add(effect);
                            break;
                        case PsdEffectKind.InnerGlow:
                            effects.InnerGlows.Add(effect);
                            break;
                        case PsdEffectKind.BevelEmboss:
                            effects.Bevels.Add(effect);
                            break;
                        case PsdEffectKind.Satin:
                            effects.Satins.Add(effect);
                            break;
                        case PsdEffectKind.ColorOverlay:
                        case PsdEffectKind.GradientOverlay:
                        case PsdEffectKind.PatternOverlay:
                            effects.Overlays.Add(effect);
                            break;
                        case PsdEffectKind.Stroke:
                            effects.Strokes.Add(effect);
                            break;
                    }
                }

                var lmgm = layer.GetTaggedBlock("lmgm");
                var vmgm = layer.GetTaggedBlock("vmgm");
                effects.MaskHidesEffects = (lmgm != null && lmgm.Length > 0 && lmgm[0] != 0)
                    || (vmgm != null && vmgm.Length > 0 && vmgm[0] != 0);
                if (effects.DropShadows.Count == 0 && effects.InnerShadows.Count == 0 && effects.OuterGlows.Count == 0
                    && effects.InnerGlows.Count == 0 && effects.Bevels.Count == 0 && effects.Satins.Count == 0
                    && effects.Overlays.Count == 0 && effects.Strokes.Count == 0)
                {
                    effects = null;
                }
            }

            _effectsCache[layer] = effects;
            return effects;
        }

        private static int EffectReach(StyledEffects fx)
        {
            var reach = 0;
            foreach (var s in fx.DropShadows)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(DistanceOf(s) + SizeOf(s)) + 3);
            }

            foreach (var g in fx.OuterGlows)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(SizeOf(g)) + 3);
            }

            foreach (var s in fx.Strokes)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(SizeOf(s)) + 3);
            }

            foreach (var s in fx.InnerShadows)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(DistanceOf(s) + SizeOf(s)) + 3);
            }

            foreach (var g in fx.InnerGlows)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(SizeOf(g)) + 3);
                if (g.Precise == true)
                {
                    // The three box passes reach 1.5 x size (reference sample padding).
                    reach = Math.Max(reach, (EffectMasks.InteriorBlurRadius(SizeOf(g)) * 3) + (int)Math.Ceiling(SizeOf(g) * SpreadOf(g) / 100f) + 3);
                }
            }

            foreach (var s in fx.Satins)
            {
                reach = Math.Max(reach, (int)Math.Ceiling(DistanceOf(s) + SizeOf(s)) + 3);
            }

            var strokeReach = 0;
            foreach (var s in fx.Strokes)
            {
                strokeReach = Math.Max(strokeReach, (int)Math.Ceiling(SizeOf(s)) + 1);
            }

            foreach (var b in fx.Bevels)
            {
                // Stroke Emboss shades the stroke band, so its surface starts past the stroke.
                var start = BevelStyleOf(b) == PsdBevelStyle.StrokeEmboss ? strokeReach : 0;
                reach = Math.Max(reach, (int)Math.Ceiling(SizeOf(b) + (b.Soften.HasValue ? b.Soften.Value : 0f)) + start + 4);
            }

            // Keep pathological descriptors (30000 px distances) bounded to the canvas scale.
            return Math.Min(reach, 4096);
        }

        private static float SizeOf(PsdEffect effect)
        {
            return effect.Size.HasValue ? effect.Size.Value : 0f;
        }

        private static float DistanceOf(PsdEffect effect)
        {
            return effect.Distance.HasValue ? effect.Distance.Value : 0f;
        }

        private static float SpreadOf(PsdEffect effect)
        {
            return effect.Spread.HasValue ? effect.Spread.Value : 0f;
        }

        private static float AngleOf(PsdEffect effect)
        {
            return effect.Angle.HasValue ? effect.Angle.Value : 0f;
        }

        private static float OpacityOf(PsdEffect effect)
        {
            return effect.Opacity;
        }

        private static PsdColor ColorOf(PsdEffect effect)
        {
            return effect.Color.HasValue ? effect.Color.Value : default(PsdColor);
        }

        private static PsdBlendMode ModeOf(PsdEffect effect)
        {
            return effect.BlendMode;
        }

        private static PsdBevelStyle BevelStyleOf(PsdEffect effect)
        {
            return effect.BevelStyle.HasValue ? effect.BevelStyle.Value : PsdBevelStyle.InnerBevel;
        }

        /// <summary>The effect's gradient, resolved once per effect descriptor.</summary>
        private Gradient GradientOf(PsdEffect effect)
        {
            Gradient gradient;
            if (_gradientOfEffect.TryGetValue(effect, out gradient))
            {
                return gradient;
            }

            gradient = effect.Descriptor != null ? Gradient.FromDescriptor(effect.Descriptor, _document.Colors, 90) : null;
            _gradientOfEffect[effect] = gradient;
            return gradient;
        }

        private StyledLayer PrepareStyled(PsdLayer layer, PlanarImage source, StyledEffects fx, float? opacityOverride)
        {
            var reach = EffectReach(fx);
            var bounds = source.Bounds;
            var domain = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach);
            // Effects cannot draw outside the canvas; the matte is zero beyond the layer,
            // so a domain clipped to canvas plus reach keeps every blur sample available.
            var limit = new PsdRect(_canvas.Left - reach, _canvas.Top - reach, _canvas.Right + reach, _canvas.Bottom + reach);
            domain = domain.Intersect(limit);
            if (domain.IsEmpty)
            {
                return null;
            }

            var layerMask = LayerMasks(layer, domain, domain, int.MinValue, int.MaxValue);

            // Shape and fill layers key their effects off the shape's coverage, not the
            // painted alpha of a gradient or pattern fill (reference effect_matte_source_for_render).
            var ownPixels = layer.Kind == PsdLayerKind.Fill && ReferenceEquals(source, LayerPixels(layer));
            PlanarImage shapeMatte = null;
            ShapePlanes planes;
            if (ownPixels && _shapePlanes.TryGetValue(layer, out planes))
            {
                shapeMatte = planes.Matte;
            }

            var raiseToCoverage = ownPixels && shapeMatte == null && !ShapeOwnsVectorMask(layer) && !fx.MaskHidesEffects
                && (layer.ContentKey == "GdFl" || layer.ContentKey == "PtFl")
                && (layer.VectorStroke == null || layer.VectorStroke.FillEnabled);
            bool raised;
            var matte = MatteOf(shapeMatte != null ? shapeMatte : source, domain, fx.MaskHidesEffects ? null : layerMask, raiseToCoverage, out raised);
            // When nothing rose the two mattes are equal (alpha is 1 wherever the masks show it).
            var hasEffectMatte = shapeMatte != null || raised;
            return new StyledLayer
            {
                Layer = layer,
                Effects = fx,
                Source = source,
                Domain = domain,
                Matte = matte,
                LayerMask = layerMask,
                OpacityOverride = opacityOverride,
                HasEffectMatte = hasEffectMatte,
            };
        }

        /// <summary>
        /// The alpha of the image over domain times the layer mask. With fullAlpha the
        /// pixels count as opaque inside their bounds, so the masks (the shape's vector
        /// mask among them) alone give the coverage; raised reports whether that differs
        /// from the painted alpha.
        /// </summary>
        private static float[] MatteOf(float[] alpha, PsdRect imageBounds, PsdRect domain, MaskSampler layerMask, bool fullAlpha, out bool raised)
        {
            var matte = new float[domain.Width * domain.Height];
            var inner = imageBounds.Intersect(domain);
            var anyRaised = 0;
            Parallelism.For(inner.Height, inner.Width, delegate(int start, int end)
            {
                var stripRaised = false;
                var row = new float[Math.Max(1, inner.Width)];
                for (var y = inner.Top + start; y < inner.Top + end; y++)
                {
                    var offset = ((y - imageBounds.Top) * imageBounds.Width) + (inner.Left - imageBounds.Left);
                    if (fullAlpha)
                    {
                        for (var i = 0; i < inner.Width; i++)
                        {
                            row[i] = 1f;
                        }
                    }
                    else
                    {
                        Array.Copy(alpha, offset, row, 0, inner.Width);
                    }

                    if (layerMask != null)
                    {
                        layerMask.MultiplyRow(y, inner.Left, row);
                    }

                    if (fullAlpha && !stripRaised)
                    {
                        for (var i = 0; i < row.Length && !stripRaised; i++)
                        {
                            stripRaised = alpha[offset + i] < 1f && row[i] > 0f;
                        }
                    }

                    Array.Copy(row, 0, matte, ((y - domain.Top) * domain.Width) + (inner.Left - domain.Left), inner.Width);
                }

                if (stripRaised)
                {
                    System.Threading.Volatile.Write(ref anyRaised, 1);
                }
            });

            raised = anyRaised != 0;
            return matte;
        }

        private static float[] MatteOf(PlanarImage image, PsdRect domain, MaskSampler layerMask, bool fullAlpha, out bool raised)
        {
            return MatteOf(image.A, image.Bounds, domain, layerMask, fullAlpha, out raised);
        }

        private void CompositeStyledPixels(PlanarImage target, PsdLayer layer, PlanarImage source, StyledEffects fx, MaskChain masks, PsdBlendMode mode, bool clipMode, PlanarImage gateBackdrop)
        {
            var styled = PrepareStyled(layer, source, fx, null);
            if (styled == null)
            {
                return;
            }

            // Blend If gates the content only, never the effects, and its Underlying gate
            // reads the backdrop as it was before this layer's exterior effects landed.
            var gate = BlendIfOf(layer);
            if (gate != null && gate.HasUnderlying && gateBackdrop == null)
            {
                var region = source.Bounds.Intersect(target.Bounds);
                gateBackdrop = region.IsEmpty ? null : Crop(target, region);
            }

            RenderExteriorEffects(target, styled, masks, clipMode);
            PrepareStrokes(styled);
            CompositeContent(target, styled, source, mode, masks, clipMode, gate, gateBackdrop);
            RenderInteriorEffects(target, styled, masks, clipMode);
        }

        private void RenderExteriorEffects(PlanarImage target, StyledLayer styled, MaskChain masks, bool clipMode)
        {
            var fx = styled.Effects;
            var domain = styled.Domain;
            var paintScale = styled.Fill * styled.Opacity;
            foreach (var shadow in fx.DropShadows)
            {
                if (OpacityOf(shadow) <= 0)
                {
                    continue;
                }

                int dx, dy;
                Offset(AngleOf(shadow), DistanceOf(shadow), out dx, out dy);
                var plane = Shifted(styled, dx, dy);
                EffectMasks.SoftExterior(plane, domain.Width, domain.Height, SizeOf(shadow), SpreadOf(shadow));
                ApplyContour(plane, shadow.Contour, shadow.ContourAntiAliased);
                var conceals = shadow.LayerConceals == true;
                var effect = shadow;
                DrawEffect(target, styled, new Plane(domain, plane), OpacityOf(shadow) * styled.Opacity, ColorOf(shadow), ModeOf(shadow), masks, clipMode,
                    delegate(int x, int y) { return conceals ? ExteriorKnockout(styled.MatteAt(x, y), paintScale) : 1f; }, null);
            }

            foreach (var glow in fx.OuterGlows)
            {
                if (OpacityOf(glow) <= 0 || SizeOf(glow) <= 0)
                {
                    continue;
                }

                var plane = (float[])styled.Matte.Clone();
                if (glow.Precise == true)
                {
                    EffectMasks.PreciseExterior(plane, domain.Width, domain.Height, SizeOf(glow), SpreadOf(glow));
                }
                else
                {
                    EffectMasks.SoftExterior(plane, domain.Width, domain.Height, SizeOf(glow), SpreadOf(glow));
                    ApplyRange(plane, glow.Range.HasValue ? glow.Range.Value : 100f, false);
                }

                ApplyContour(plane, glow.Contour, glow.ContourAntiAliased);
                var paint = GlowPaint(glow, plane, domain);
                DrawEffect(target, styled, new Plane(domain, plane), OpacityOf(glow) * styled.Opacity, ColorOf(glow), ModeOf(glow), masks, clipMode,
                    delegate(int x, int y) { return ExteriorKnockout(styled.MatteAt(x, y), paintScale); }, paint);
            }
        }

        private void RenderInteriorEffects(PlanarImage target, StyledLayer styled, MaskChain masks, bool clipMode)
        {
            var fx = styled.Effects;
            var domain = styled.Domain;
            var knockout = styled.Knockout;
            Func<int, int, float> inside = delegate(int x, int y)
            {
                return styled.MatteAt(x, y) * (knockout != null ? knockout.At(x, y) : 1f);
            };

            foreach (var glow in fx.InnerGlows)
            {
                if (OpacityOf(glow) <= 0 || (SizeOf(glow) <= 0 && glow.CenterSource != true))
                {
                    continue;
                }

                var plane = (float[])styled.Matte.Clone();
                var center = glow.CenterSource == true;
                if (glow.Precise == true)
                {
                    EffectMasks.PreciseInterior(plane, domain.Width, domain.Height, SizeOf(glow), SpreadOf(glow), center);
                }
                else
                {
                    EffectMasks.SoftInterior(plane, domain.Width, domain.Height, SizeOf(glow), SpreadOf(glow));
                    ApplyRange(plane, glow.Range.HasValue ? glow.Range.Value : 100f, center);
                }

                ApplyContour(plane, glow.Contour, glow.ContourAntiAliased);
                DrawEffect(target, styled, new Plane(domain, plane), OpacityOf(glow) * styled.Opacity, ColorOf(glow), ModeOf(glow), masks, clipMode, inside, GlowPaint(glow, plane, domain));
            }

            foreach (var shadow in fx.InnerShadows)
            {
                if (OpacityOf(shadow) <= 0)
                {
                    continue;
                }

                int dx, dy;
                Offset(AngleOf(shadow), DistanceOf(shadow), out dx, out dy);
                var plane = Shifted(styled, dx, dy);
                EffectMasks.SoftInterior(plane, domain.Width, domain.Height, SizeOf(shadow), SpreadOf(shadow));
                ApplyContour(plane, shadow.Contour, shadow.ContourAntiAliased);
                DrawEffect(target, styled, new Plane(domain, plane), OpacityOf(shadow) * styled.Opacity, ColorOf(shadow), ModeOf(shadow), masks, clipMode, inside, null);
            }

            foreach (var stroke in styled.StrokePlanes)
            {
                DrawEffect(target, styled, stroke.Plane, OpacityOf(stroke.Stroke) * styled.Opacity, ColorOf(stroke.Stroke), ModeOf(stroke.Stroke), masks, clipMode, null, stroke.Paint);
            }

            RenderBevels(target, styled, masks, clipMode);
        }

        /// <summary>
        /// Builds stroke bands: the outer share (where content is missing) paints
        /// immediately, under the content; the inner share is kept for the pass above
        /// the content; strokes without Overprint knock the content out of their band.
        /// </summary>
        private void PrepareStrokes(StyledLayer styled)
        {
            var fx = styled.Effects;
            if (fx.Strokes.Count == 0)
            {
                return;
            }

            var domain = styled.Domain;
            var width = domain.Width;
            var height = domain.Height;
            var needOutside = false;
            var needInside = false;
            foreach (var s in fx.Strokes)
            {
                var position = s.StrokePosition.HasValue ? s.StrokePosition.Value : PsdStrokePosition.Outside;
                if (position != PsdStrokePosition.Inside)
                {
                    needOutside = true;
                }

                if (position != PsdStrokePosition.Outside)
                {
                    needInside = true;
                }
            }

            float[] outside, insideField;
            EffectMasks.StrokeDistanceFields(styled.Matte, width, height, needOutside, needInside, out outside, out insideField);
            float[] knockout = null;
            var emboss = false;
            foreach (var b in fx.Bevels)
            {
                if (BevelStyleOf(b) == PsdBevelStyle.StrokeEmboss)
                {
                    emboss = true;
                }
            }

            foreach (var stroke in fx.Strokes)
            {
                var position = stroke.StrokePosition.HasValue ? stroke.StrokePosition.Value : PsdStrokePosition.Outside;
                var size = SizeOf(stroke);
                var bandOut = position == PsdStrokePosition.Inside ? 0f : (position == PsdStrokePosition.Center ? size * 0.5f : size);
                var bandIn = position == PsdStrokePosition.Outside ? 0f : (position == PsdStrokePosition.Center ? size * 0.5f : size);
                var under = new float[styled.Matte.Length];
                var over = new float[styled.Matte.Length];

                // Rows run in parallel. The knockout plane is allocated up front for a stroke
                // without Overprint and dropped again when no pixel of it was written.
                var overprint = stroke.Overprint == true;
                var fresh = knockout == null && !overprint;
                var strokeKnockout = fresh ? Ones(styled.Matte.Length) : knockout;
                var anyPaint = 0;
                var anyKnockout = 0;
                Parallelism.For(height, width, delegate(int start, int end)
                {
                    var stripAny = false;
                    var knocked = false;
                    for (var i = start * width; i < end * width; i++)
                    {
                        var a = Compat.Clamp(styled.Matte[i], 0f, 1f);
                        var outBand = bandOut > 0 && outside != null ? Compat.Clamp(bandOut + 1f - outside[i], 0f, 1f) : 0f;
                        var inBand = bandIn > 0 && insideField != null ? Compat.Clamp(bandIn + 1f - insideField[i], 0f, 1f) : 0f;
                        var innerShare = a * inBand;
                        under[i] = outBand * (1f - innerShare);
                        over[i] = innerShare;
                        stripAny |= under[i] > 0 || over[i] > 0;
                        if (!overprint && innerShare > 0)
                        {
                            strokeKnockout[i] *= 1f - innerShare;
                            knocked = true;
                        }
                    }

                    if (stripAny)
                    {
                        System.Threading.Volatile.Write(ref anyPaint, 1);
                    }

                    if (knocked)
                    {
                        System.Threading.Volatile.Write(ref anyKnockout, 1);
                    }
                });

                if (!fresh || anyKnockout != 0)
                {
                    knockout = strokeKnockout;
                }

                if (anyPaint == 0)
                {
                    continue;
                }

                var paint = StrokePaint(styled, stroke, bandOut, bandIn, outside, insideField);
                styled.StrokePlanes.Add(new StrokePlane(stroke, new Plane(domain, over), paint));
                styled.StrokeUnderlays.Add(new StrokePlane(stroke, new Plane(domain, under), paint));
                if (emboss && OpacityOf(stroke) > 0)
                {
                    if (styled.StrokeEmbossMatte == null)
                    {
                        styled.StrokeEmbossMatte = new float[styled.Matte.Length];
                    }

                    AccumulateStrokeEmbossMatte(styled, stroke, paint, bandOut, bandIn, outside, insideField);
                }
            }

            if (knockout != null)
            {
                styled.Knockout = new Plane(domain, knockout);
            }
        }

        private static float[] Ones(int length)
        {
            var ones = new float[length];
            for (var i = 0; i < ones.Length; i++)
            {
                ones[i] = 1f;
            }

            return ones;
        }

        /// <summary>
        /// Adds one stroke to the Stroke Emboss surface: the band coverage
        /// a * inside + (1 - a) * outside times the stroke opacity and gradient
        /// opacity, united with the earlier strokes by screen. With "Layer Mask Hides
        /// Effects" the mask hides the band where it lands (reference
        /// render_bevel_emboss, the stroke_emboss branch).
        /// </summary>
        private static void AccumulateStrokeEmbossMatte(StyledLayer styled, PsdEffect stroke, EffectPaint paint, float bandOut, float bandIn, float[] outside, float[] insideField)
        {
            var domain = styled.Domain;
            var matte = styled.StrokeEmbossMatte;
            var masked = styled.Effects.MaskHidesEffects && styled.LayerMask != null;
            var strokeOpacity = OpacityOf(stroke);
            Parallelism.For(domain.Height, domain.Width, delegate(int start, int end)
            {
                var maskRow = masked ? new float[domain.Width] : null;
                for (var y = start; y < end; y++)
                {
                    if (maskRow != null)
                    {
                        for (var i = 0; i < maskRow.Length; i++)
                        {
                            maskRow[i] = 1f;
                        }

                        styled.LayerMask.MultiplyRow(domain.Top + y, domain.Left, maskRow);
                    }

                    for (var x = 0; x < domain.Width; x++)
                    {
                        var i = (y * domain.Width) + x;
                        var a = Compat.Clamp(styled.Matte[i], 0f, 1f);
                        var outBand = bandOut > 0 && outside != null ? Compat.Clamp(bandOut + 1f - outside[i], 0f, 1f) : 0f;
                        var inBand = bandIn > 0 && insideField != null ? Compat.Clamp(bandIn + 1f - insideField[i], 0f, 1f) : 0f;
                        var alpha = Compat.Clamp((a * inBand) + ((1f - a) * outBand), 0f, 1f) * strokeOpacity;
                        if (alpha > 0 && paint != null)
                        {
                            alpha *= paint(domain.Left + x, domain.Top + y).A;
                        }

                        if (alpha > 0 && maskRow != null)
                        {
                            alpha *= maskRow[x];
                        }

                        matte[i] = alpha + (matte[i] * (1f - alpha));
                    }
                }
            });
        }

        /// <summary>The layer's own pixels with overlays and satin folded in, stroke underlays first.</summary>
        private void CompositeContent(PlanarImage target, StyledLayer styled, PlanarImage source, PsdBlendMode mode, MaskChain masks, bool clipMode, BlendIf gate, PlanarImage gateBackdrop)
        {
            foreach (var stroke in styled.StrokeUnderlays)
            {
                DrawEffect(target, styled, stroke.Plane, OpacityOf(stroke.Stroke) * styled.Opacity, ColorOf(stroke.Stroke), ModeOf(stroke.Stroke), masks, clipMode, null, stroke.Paint);
            }

            var region = source.Bounds.Intersect(target.Bounds);
            if (region.IsEmpty)
            {
                return;
            }

            var fx = styled.Effects;

            // Overlays and satin fold into the layer's color only when that is what
            // Photoshop computes: a Normal layer (or "Blend Interior Effects as Group")
            // at full Fill without Blend If. Otherwise they paint after the layer's own
            // blend, unscaled by Fill and ungated.
            var fold = styled.Fill >= 1f && (mode == PsdBlendMode.Normal || styled.Layer.BlendInteriorElements) && gate == null;
            ShapePlanes split = null;
            ShapePlanes shapePlanes;
            var anyOverlay = false;
            foreach (var o in fx.Overlays)
            {
                if (OpacityOf(o) > 0)
                {
                    anyOverlay = true;
                }
            }

            if (gate == null && styled.Layer.Kind == PsdLayerKind.Fill && anyOverlay
                && ReferenceEquals(source, LayerPixels(styled.Layer))
                && _shapePlanes.TryGetValue(styled.Layer, out shapePlanes) && shapePlanes.Fill != null && shapePlanes.Stroke != null)
            {
                split = shapePlanes;
            }

            var foldSatin = fold && !styled.HasEffectMatte;
            var satinPlanes = new List<SatinPlanePair>();
            if (foldSatin)
            {
                foreach (var satin in fx.Satins)
                {
                    if (OpacityOf(satin) > 0)
                    {
                        satinPlanes.Add(new SatinPlanePair(satin, SatinPlane(styled, satin)));
                    }
                }
            }

            var overlays = foldSatin && split == null ? fx.Overlays : new List<PsdEffect>();
            var overlayPaints = new List<EffectPaint>();
            foreach (var overlay in overlays)
            {
                overlayPaints.Add(OverlayPaint(styled, overlay));
            }

            var width = region.Width;
            var ones = Ones(width);
            Parallelism.For(region.Height, width * (1L + overlays.Count + satinPlanes.Count), delegate(int start, int end)
            {
                var alpha = new float[width];
                var r = new float[width];
                var g = new float[width];
                var b = new float[width];
                var er = new float[width];
                var eg = new float[width];
                var eb = new float[width];
                var ea = new float[width];
                for (var y = region.Top + start; y < region.Top + end; y++)
                {
                    var s = source.RowOffset(y, region.Left);
                    Array.Copy(source.R, s, r, 0, width);
                    Array.Copy(source.G, s, g, 0, width);
                    Array.Copy(source.B, s, b, 0, width);

                    // Interior overlays fold into the layer's straight color: gradient under color, then satin.
                    for (var o = 0; o < overlays.Count; o++)
                    {
                        var overlay = overlays[o];
                        var paint = overlayPaints[o];
                        var color = ColorOf(overlay);
                        var opacity = OpacityOf(overlay);
                        for (var i = 0; i < width; i++)
                        {
                            if (paint == null)
                            {
                                er[i] = color.R / 255f;
                                eg[i] = color.G / 255f;
                                eb[i] = color.B / 255f;
                                ea[i] = opacity;
                            }
                            else
                            {
                                var painted = paint(region.Left + i, y);
                                er[i] = painted.R;
                                eg[i] = painted.G;
                                eb[i] = painted.B;
                                ea[i] = opacity * painted.A;
                            }
                        }

                        FoldInto(ModeOf(overlay), er, eg, eb, ea, r, g, b, ones, region.Left, y);
                    }

                    foreach (var satin in satinPlanes)
                    {
                        var row = satin.Plane.Row(y, region.Left, width);
                        var color = ColorOf(satin.Satin);
                        var opacity = OpacityOf(satin.Satin);
                        for (var i = 0; i < width; i++)
                        {
                            er[i] = color.R / 255f;
                            eg[i] = color.G / 255f;
                            eb[i] = color.B / 255f;
                            ea[i] = row[i] * opacity;
                        }

                        FoldInto(ModeOf(satin.Satin), er, eg, eb, ea, r, g, b, ones, region.Left, y);
                    }

                    Array.Copy(source.A, s, alpha, 0, width);
                    if (styled.LayerMask != null)
                    {
                        styled.LayerMask.MultiplyRow(y, region.Left, alpha);
                    }

                    for (var chain = masks; chain != null; chain = chain.Next)
                    {
                        chain.Sampler.MultiplyRow(y, region.Left, alpha);
                    }

                    if (styled.Knockout != null)
                    {
                        var knockoutRow = styled.Knockout.Row(y, region.Left, width);
                        BlendKernels.MultiplyInPlace(alpha, knockoutRow);
                    }

                    CompositeLayerRow(target, y, region.Left, mode, r, g, b, alpha, styled.Opacity, styled.Fill, gate, gateBackdrop, clipMode);
                }
            });

            if (!foldSatin || split != null)
            {
                RenderOverlayPasses(target, styled, masks, clipMode, split != null ? split.Fill : null, !foldSatin);
            }

            if (split != null)
            {
                RestampStroke(target, styled, split.Stroke, mode, masks, clipMode);
            }
        }

        private sealed class SatinPlanePair
        {
            public SatinPlanePair(PsdEffect satin, Plane plane)
            {
                Satin = satin;
                Plane = plane;
            }

            public PsdEffect Satin { get; private set; }

            public Plane Plane { get; private set; }
        }

        /// <summary>
        /// The vector stroke plane re-composited above the interior overlays with the base
        /// pass's factors: masks, stroke knockout, opacity, Fill and the layer's blend mode
        /// (reference "vector_stroke_over_overlays" in composite_pixel_layer).
        /// </summary>
        private void RestampStroke(PlanarImage target, StyledLayer styled, PlanarImage stroke, PsdBlendMode mode, MaskChain masks, bool clipMode)
        {
            var region = stroke.Bounds.Intersect(target.Bounds);
            if (region.IsEmpty)
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
                    var s = stroke.RowOffset(y, region.Left);
                    Array.Copy(stroke.A, s, alpha, 0, width);
                    var any = false;
                    for (var i = 0; i < width; i++)
                    {
                        if (alpha[i] != 0f)
                        {
                            any = true;
                            break;
                        }
                    }

                    if (!any)
                    {
                        continue;
                    }

                    Array.Copy(stroke.R, s, sr, 0, width);
                    Array.Copy(stroke.G, s, sg, 0, width);
                    Array.Copy(stroke.B, s, sb, 0, width);
                    if (styled.LayerMask != null)
                    {
                        styled.LayerMask.MultiplyRow(y, region.Left, alpha);
                    }

                    for (var chain = masks; chain != null; chain = chain.Next)
                    {
                        chain.Sampler.MultiplyRow(y, region.Left, alpha);
                    }

                    if (styled.Knockout != null)
                    {
                        var knockoutRow = styled.Knockout.Row(y, region.Left, width);
                        BlendKernels.MultiplyInPlace(alpha, knockoutRow);
                    }

                    CompositeLayerRow(target, y, region.Left, mode, sr, sg, sb, alpha, styled.Opacity, styled.Fill, null, null, clipMode);
                }
            });
        }

        /// <summary>Folds an effect color into straight color rows (destination treated as opaque), with the burn/dodge alpha fold.</summary>
        private static void FoldInto(PsdBlendMode mode, float[] er, float[] eg, float[] eb, float[] ea, float[] r, float[] g, float[] b, float[] ones, int x0, int y)
        {
            PsdBlendMode effectiveMode;
            PrepareEffectColor(mode, er, eg, eb, ea, null, x0, y, out effectiveMode);
            var scratch = (float[])ones.Clone();
            BlendKernels.CompositeRow(effectiveMode, er, eg, eb, ea, r, g, b, scratch, true, null);
        }

        /// <summary>
        /// Photoshop composites effect planes with burn-mode alpha folded into the color
        /// (toward white for Linear/Color Burn, toward black for Color Dodge) and blends at
        /// full coverage over opaque destination pixels; Dissolve becomes a coverage decision.
        /// Over pixels that are not opaque the caller hands burn and dodge to
        /// SpecialFill.CompositeEffectRow instead.
        /// </summary>
        private static void PrepareEffectColor(PsdBlendMode mode, float[] r, float[] g, float[] b, float[] a, float[] destinationAlpha, int x0, int y, out PsdBlendMode effectiveMode)
        {
            effectiveMode = mode;
            if (mode == PsdBlendMode.Dissolve)
            {
                Dissolve(a, x0, y);
                effectiveMode = PsdBlendMode.Normal;
                return;
            }

            if (mode == PsdBlendMode.LinearBurn || mode == PsdBlendMode.ColorBurn)
            {
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] > 0 && (destinationAlpha == null || destinationAlpha[i] >= 1f))
                    {
                        r[i] = 1f - ((1f - r[i]) * a[i]);
                        g[i] = 1f - ((1f - g[i]) * a[i]);
                        b[i] = 1f - ((1f - b[i]) * a[i]);
                        a[i] = 1f;
                    }
                }

                return;
            }

            if (mode == PsdBlendMode.ColorDodge)
            {
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] > 0 && (destinationAlpha == null || destinationAlpha[i] >= 1f))
                    {
                        r[i] *= a[i];
                        g[i] *= a[i];
                        b[i] *= a[i];
                        a[i] = 1f;
                    }
                }
            }
        }

        /// <summary>Composites a solid-color or painted effect plane into the target.</summary>
        private void DrawEffect(PlanarImage target, StyledLayer styled, Plane plane, float scale, PsdColor color, PsdBlendMode mode, MaskChain masks, bool clipMode, Func<int, int, float> factor, EffectPaint paint)
        {
            var region = plane.Rect.Intersect(target.Bounds);
            if (region.IsEmpty || scale <= 0)
            {
                return;
            }

            var width = region.Width;
            Parallelism.For(region.Height, width, delegate(int start, int end)
            {
                var r = new float[width];
                var g = new float[width];
                var b = new float[width];
                var a = new float[width];
                var chainRow = masks == null ? null : new float[width];
                var dr = new float[width];
                var dg = new float[width];
                var db = new float[width];
                var da = new float[width];
                for (var y = region.Top + start; y < region.Top + end; y++)
                {
                    var row = plane.Row(y, region.Left, width);
                    var any = false;
                    for (var i = 0; i < width; i++)
                    {
                        var value = row[i] * scale;
                        if (value > 0 && factor != null)
                        {
                            value *= factor(region.Left + i, y);
                        }

                        a[i] = value;
                        any |= value > 0;
                    }

                    if (!any)
                    {
                        continue;
                    }

                    if (styled.Effects.MaskHidesEffects && styled.LayerMask != null)
                    {
                        styled.LayerMask.MultiplyRow(y, region.Left, a);
                    }

                    if (paint == null)
                    {
                        for (var i = 0; i < width; i++)
                        {
                            r[i] = color.R / 255f;
                            g[i] = color.G / 255f;
                            b[i] = color.B / 255f;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < width; i++)
                        {
                            if (a[i] <= 0)
                            {
                                continue;
                            }

                            var painted = paint(region.Left + i, y);
                            r[i] = painted.R;
                            g[i] = painted.G;
                            b[i] = painted.B;
                            a[i] *= painted.A;
                        }
                    }

                    // The group mask chain scales coverage after the effect color is resolved
                    // (the reference's masked target): the burn/dodge fold and Dissolve read the
                    // effect's own alpha.
                    float[] coverage = null;
                    if (masks != null)
                    {
                        coverage = chainRow;
                        for (var i = 0; i < width; i++)
                        {
                            coverage[i] = 1f;
                        }

                        for (var chain = masks; chain != null; chain = chain.Next)
                        {
                            chain.Sampler.MultiplyRow(y, region.Left, coverage);
                        }
                    }

                    var t = target.RowOffset(y, region.Left);
                    Array.Copy(target.R, t, dr, 0, width);
                    Array.Copy(target.G, t, dg, 0, width);
                    Array.Copy(target.B, t, db, 0, width);
                    Array.Copy(target.A, t, da, 0, width);
                    var clipRow = clipMode ? ClipCoverageRow(target, t, width) : null;
                    PsdBlendMode effectiveMode;
                    PrepareEffectColor(mode, r, g, b, a, da, region.Left, y, out effectiveMode);
                    if (mode == PsdBlendMode.LinearBurn || mode == PsdBlendMode.ColorBurn || mode == PsdBlendMode.ColorDodge)
                    {
                        SpecialFill.CompositeEffectRow(mode, r, g, b, a, coverage, dr, dg, db, da, clipMode, clipRow);
                    }

                    if (coverage != null)
                    {
                        BlendKernels.MultiplyInPlace(a, coverage);
                    }

                    BlendKernels.CompositeRow(effectiveMode, r, g, b, a, dr, dg, db, da, clipMode, clipRow);
                    Array.Copy(dr, 0, target.R, t, width);
                    Array.Copy(dg, 0, target.G, t, width);
                    Array.Copy(db, 0, target.B, t, width);
                    Array.Copy(da, 0, target.A, t, width);
                }
            });
        }

        /// <summary>Per-pixel paint of a gradient or pattern overlay; null for solid colors (and unresolvable patterns paint nothing).</summary>
        private EffectPaint OverlayPaint(StyledLayer styled, PsdEffect overlay)
        {
            if (overlay.Gradient != null)
            {
                // Two-stop ramps ease like gradient fills (endpoint smoothing): Photoshop's
                // photoshop-overlay-zorder.psd overlays go from max 13 to max 1 with it.
                var gradient = GradientOf(overlay);
                if (gradient == null)
                {
                    return null;
                }

                var box = GradientBox.From(gradient.AlignWithLayer ? MatteBounds(styled) : styled.Source.Bounds);
                var frame = gradient.Frame(box, GradientSpan.LayerProjection);
                var dither = overlay.Dither == true;
                return delegate(int x, int y)
                {
                    var position = frame.Position(x, y);
                    float r, g, b;
                    gradient.Color(position, true, out r, out g, out b);
                    if (dither)
                    {
                        DitherGradientColor(ref r, ref g, ref b, x, y);
                    }

                    return new EffectColor(r, g, b, gradient.Opacity(position, true));
                };
            }

            if (overlay.Pattern != null)
            {
                var placement = PatternPlacementOf(overlay.Pattern);
                PatternTile tile;
                if (placement == null || !_document.Patterns.TryGetValue(placement.PatternId, out tile))
                {
                    return delegate(int x, int y) { return new EffectColor(0f, 0f, 0f, 0f); };
                }

                var sampler = new PatternSampler(tile, placement, styled.Layer);
                return delegate(int x, int y)
                {
                    float r, g, b, a;
                    sampler.Sample(x, y, out r, out g, out b, out a);
                    return new EffectColor(r, g, b, a);
                };
            }

            return null;
        }

        private static PatternPlacement PatternPlacementOf(PsdPatternPlacement pattern)
        {
            return pattern == null
                ? null
                : new PatternPlacement(pattern.PatternId, pattern.Scale, pattern.AngleDegrees, pattern.LinkWithLayer, pattern.PhaseX, pattern.PhaseY);
        }

        /// <summary>
        /// Gradient strokes. Shape Burst follows the band's anchored distance field
        /// (position 0 at the outer band limit, 1 at the inner one, each limit reaching
        /// +1 px) with Photoshop's 1-2-1 tent across the pixel footprint; other types
        /// use the ordinary gradient placement over the layer's bounds.
        /// </summary>
        private EffectPaint StrokePaint(StyledLayer styled, PsdEffect stroke, float bandOut, float bandIn, float[] outside, float[] insideField)
        {
            if (stroke.Gradient == null)
            {
                return null;
            }

            var gradient = GradientOf(stroke);
            if (gradient == null)
            {
                return null;
            }

            var domain = styled.Domain;
            var dither = stroke.Dither == true;
            if (gradient.Type == PsdGradientType.ShapeBurst)
            {
                var outerReach = bandOut > 0 ? bandOut + 1f : 0f;
                var innerReach = bandIn > 0 ? bandIn + 1f : 0f;
                var span = Math.Max(1f, outerReach + innerReach);
                var step = 1f / span;
                return delegate(int x, int y)
                {
                    var i = ((y - domain.Top) * domain.Width) + (x - domain.Left);
                    float along;
                    if (styled.Matte[i] >= 0.5f)
                    {
                        along = insideField == null ? span : bandOut + insideField[i];
                    }
                    else
                    {
                        along = outside == null ? 0f : outerReach - outside[i];
                    }

                    var position = Compat.Clamp(along / span, 0f, 1f);
                    var p0 = Compat.Clamp(gradient.Reverse ? 1f - Compat.Clamp(position - step, 0f, 1f) : Compat.Clamp(position - step, 0f, 1f), 0f, 1f);
                    var p1 = Compat.Clamp(gradient.Reverse ? 1f - position : position, 0f, 1f);
                    var p2 = Compat.Clamp(gradient.Reverse ? 1f - Compat.Clamp(position + step, 0f, 1f) : Compat.Clamp(position + step, 0f, 1f), 0f, 1f);
                    float r0, g0, b0, r1, g1, b1, r2, g2, b2;
                    gradient.Color(p0, true, out r0, out g0, out b0);
                    gradient.Color(p1, true, out r1, out g1, out b1);
                    gradient.Color(p2, true, out r2, out g2, out b2);
                    var opacity = 0.25f * (gradient.Opacity(p0, true) + (2f * gradient.Opacity(p1, true)) + gradient.Opacity(p2, true));
                    var r = ((r0 + (2f * r1) + r2) * 0.25f);
                    var g = ((g0 + (2f * g1) + g2) * 0.25f);
                    var b = ((b0 + (2f * b1) + b2) * 0.25f);
                    if (dither)
                    {
                        DitherGradientColor(ref r, ref g, ref b, x, y);
                    }

                    return new EffectColor(r, g, b, opacity);
                };
            }

            var frame = gradient.Frame(GradientBox.From(gradient.AlignWithLayer ? MatteBounds(styled) : styled.Source.Bounds), GradientSpan.LayerProjection);
            return delegate(int x, int y)
            {
                var position = frame.Position(x, y);
                float r, g, b;
                gradient.Color(position, true, out r, out g, out b);
                if (dither)
                {
                    DitherGradientColor(ref r, ref g, ref b, x, y);
                }

                return new EffectColor(r, g, b, gradient.Opacity(position, true));
            };
        }

        /// <summary>
        /// A clipping base with effects: the base pixels plus its clipped members form
        /// the content, the base's effects key off its own matte, and its interior
        /// effects and overlays land over the members (reference ClipRunContent).
        /// </summary>
        private void CompositeStyledClipRun(PlanarImage target, PsdLayer layer, PlanarImage source, StyledEffects fx, IReadOnlyList<PsdLayer> members, MaskChain masks)
        {
            var content = BandCopy(source);
            CompositeClipMembers(content, members, 0, members.Count);

            var styled = PrepareStyled(layer, source, fx, null);
            if (styled == null)
            {
                return;
            }

            var mode = layer.BlendMode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : layer.BlendMode;
            RenderExteriorEffects(target, styled, masks, false);
            PrepareStrokes(styled);
            CompositeContent(target, styled, content, mode, masks, false, null, null);
            RenderInteriorEffects(target, styled, masks, false);
        }

        /// <summary>
        /// A styled group. Isolated groups route their flattened children through the
        /// ordinary styled pipeline (the group plays the layer). Pass Through groups do not
        /// isolate: exterior effects paint from the children's silhouette before the
        /// children, interior effects after them, and group opacity fades the result.
        /// </summary>
        private void CompositeStyledGroup(PlanarImage target, PsdLayer group, StyledEffects fx, MaskChain masks, PsdBlendMode mode, bool clipMode, MaskSampler groupMask, PlanarImage gateBackdrop)
        {
            var bounds = ContentBounds(group).Intersect(target.Logical);
            if (bounds.IsEmpty)
            {
                return;
            }

            var silhouette = new PlanarImage(bounds);
            CompositeList(silhouette, group.Children, null);
            var fill = group.FillOpacity / 255f;
            if (mode != PsdBlendMode.PassThrough || fill < 1f || BlendIfOf(group) != null)
            {
                CompositeStyledPixels(target, group, silhouette, fx, masks, mode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : mode, clipMode, gateBackdrop);
                return;
            }

            var opacity = group.Opacity / 255f;
            var reach = EffectReach(fx);
            var fadeRegion = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach).Intersect(target.Bounds);
            if (HasAdjustmentDescendant(group))
            {
                fadeRegion = target.Bounds;
            }

            var before = opacity < 1f ? Crop(target, fadeRegion) : null;
            var styled = PrepareStyled(group, silhouette, fx, 1f);
            if (styled != null)
            {
                RenderExteriorEffects(target, styled, masks, clipMode);
            }

            CompositeList(target, group.Children, Chain(masks, groupMask));
            if (styled != null)
            {
                RenderOverlayPasses(target, styled, masks, clipMode, null, true);
                PrepareStrokes(styled);
                foreach (var stroke in styled.StrokeUnderlays)
                {
                    DrawEffect(target, styled, stroke.Plane, OpacityOf(stroke.Stroke), ColorOf(stroke.Stroke), ModeOf(stroke.Stroke), masks, clipMode, null, stroke.Paint);
                }

                styled.Knockout = null;
                RenderInteriorEffects(target, styled, masks, clipMode);
            }

            if (before != null)
            {
                FadeToward(target, before, fadeRegion, opacity);
            }
        }

        /// <summary>
        /// Overlays and satin as destination passes over the matte (pass-through groups cannot
        /// fold them). Overlays use the alpha of overlayAlpha instead when given (the fill
        /// plane of a stroked shape).
        /// </summary>
        private void RenderOverlayPasses(PlanarImage target, StyledLayer styled, MaskChain masks, bool clipMode, PlanarImage overlayAlpha, bool includeSatin)
        {
            // A stroke without Overprint knocks these passes out of its band as it does
            // the content (reference render_*_overlay and the satin pass, StrokeKnockoutPlane).
            Plane matte;
            if (overlayAlpha == null)
            {
                matte = new Plane(styled.Domain, styled.Matte);
            }
            else
            {
                bool raised;
                var plane = MatteOf(overlayAlpha, styled.Domain, styled.Effects.MaskHidesEffects ? null : styled.LayerMask, false, out raised);
                matte = new Plane(styled.Domain, plane);
            }

            var knockout = styled.Knockout;
            Func<int, int, float> overlayFactor = knockout == null ? null : new Func<int, int, float>(knockout.At);
            foreach (var overlay in styled.Effects.Overlays)
            {
                DrawEffect(target, styled, matte, OpacityOf(overlay) * styled.Opacity, ColorOf(overlay), ModeOf(overlay), masks, clipMode, overlayFactor, OverlayPaint(styled, overlay));
            }

            if (includeSatin)
            {
                foreach (var satin in styled.Effects.Satins)
                {
                    var plane = SatinPlane(styled, satin);
                    DrawEffect(target, styled, plane, OpacityOf(satin) * styled.Opacity, ColorOf(satin), ModeOf(satin), masks, clipMode,
                        delegate(int x, int y) { return styled.MatteAt(x, y) * (knockout != null ? knockout.At(x, y) : 1f); }, null);
                }
            }
        }

        private static void FadeToward(PlanarImage target, PlanarImage before, PsdRect region, float opacity)
        {
            var width = region.Width;
            var br = new float[width];
            var bg = new float[width];
            var bb = new float[width];
            var ba = new float[width];
            var dr = new float[width];
            var dg = new float[width];
            var db = new float[width];
            var da = new float[width];
            for (var y = region.Top; y < region.Bottom; y++)
            {
                var t = target.RowOffset(y, region.Left);
                var b = before.RowOffset(y, region.Left);
                Array.Copy(before.R, b, br, 0, width);
                Array.Copy(before.G, b, bg, 0, width);
                Array.Copy(before.B, b, bb, 0, width);
                Array.Copy(before.A, b, ba, 0, width);
                Array.Copy(target.R, t, dr, 0, width);
                Array.Copy(target.G, t, dg, 0, width);
                Array.Copy(target.B, t, db, 0, width);
                Array.Copy(target.A, t, da, 0, width);
                BlendKernels.FadeTowardRow(br, bg, bb, ba, dr, dg, db, da, opacity);
                Array.Copy(dr, 0, target.R, t, width);
                Array.Copy(dg, 0, target.G, t, width);
                Array.Copy(db, 0, target.B, t, width);
                Array.Copy(da, 0, target.A, t, width);
            }
        }

        /// <summary>
        /// Photoshop's "knocks out" factor for exterior effects: the layer's shape
        /// removes its own shadow or glow, divided by what the content pass will
        /// cover anyway (reference exterior_effect_knockout).
        /// </summary>
        private static float ExteriorKnockout(float shape, float paintScale)
        {
            var paint = Compat.Clamp(shape * paintScale, 0f, 1f);
            var remaining = 1f - paint;
            return remaining <= 0 ? 0f : Math.Min(1f, (1f - Compat.Clamp(shape, 0f, 1f)) / remaining);
        }

        private static void ApplyRange(float[] plane, float range, bool center)
        {
            var gain = 100f / Compat.Clamp(range, 1f, 100f);
            for (var i = 0; i < plane.Length; i++)
            {
                var value = Math.Min(1f, plane[i] * gain);
                plane[i] = center ? 1f - value : value;
            }
        }

        /// <summary>Photoshop's light offset: the effect moves away from the light angle.</summary>
        private static void Offset(float angleDegrees, float distance, out int dx, out int dy)
        {
            var radians = (180f - angleDegrees) * MathF.PI / 180f;
            dx = (int)Math.Round(MathF.Cos(radians) * distance, MidpointRounding.AwayFromZero);
            dy = (int)Math.Round(MathF.Sin(radians) * distance, MidpointRounding.AwayFromZero);
        }

        /// <summary>The matte moved by (dx, dy) over the same domain (samples from outside the domain are transparent).</summary>
        private static float[] Shifted(StyledLayer styled, int dx, int dy)
        {
            var domain = styled.Domain;
            var width = domain.Width;
            var height = domain.Height;
            var result = new float[styled.Matte.Length];
            Parallelism.For(height, width, delegate(int start, int end)
            {
                for (var y = start; y < end; y++)
                {
                    var sy = y - dy;
                    for (var x = 0; x < width; x++)
                    {
                        var sx = x - dx;
                        result[(y * width) + x] = (uint)sx < (uint)width && (uint)sy < (uint)height ? styled.Matte[(sy * width) + sx] : 0f;
                    }
                }
            });

            return result;
        }

        /// <summary>Satin: |tent(A(x+v) - A(x-v))|, inverted by default (reference satin_alpha_mask).</summary>
        private static Plane SatinPlane(StyledLayer styled, PsdEffect satin)
        {
            int dx, dy;
            Offset(AngleOf(satin), Math.Max(1f, DistanceOf(satin)), out dx, out dy);
            var forward = Shifted(styled, -dx, -dy);
            var backward = Shifted(styled, dx, dy);
            for (var i = 0; i < forward.Length; i++)
            {
                forward[i] -= backward[i];
            }

            var size = SizeOf(satin);
            if (size > 0)
            {
                EffectMasks.TentBlur(forward, styled.Domain.Width, styled.Domain.Height, Math.Max(2, (int)Math.Round(size, MidpointRounding.AwayFromZero)));
            }

            var invert = satin.Invert == true;
            for (var i = 0; i < forward.Length; i++)
            {
                var value = Compat.Clamp(Math.Abs(forward[i]), 0f, 1f);
                forward[i] = invert ? 1f - value : value;
            }

            return new Plane(styled.Domain, forward);
        }

        /// <summary>Bounding box of the visible matte (gradient overlays aligned with the layer span it).</summary>
        private static PsdRect MatteBounds(StyledLayer styled)
        {
            var domain = styled.Domain;
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

            // Strips find their own extremes; the integer min/max merge is order-free.
            var gate = new object();
            Parallelism.For(domain.Height, domain.Width, delegate(int start, int end)
            {
                int stripLeft = int.MaxValue, stripTop = int.MaxValue, stripRight = int.MinValue, stripBottom = int.MinValue;
                for (var y = start; y < end; y++)
                {
                    for (var x = 0; x < domain.Width; x++)
                    {
                        if (styled.Matte[(y * domain.Width) + x] > 0)
                        {
                            stripLeft = Math.Min(stripLeft, x);
                            stripRight = Math.Max(stripRight, x);
                            stripTop = Math.Min(stripTop, y);
                            stripBottom = Math.Max(stripBottom, y);
                        }
                    }
                }

                lock (gate)
                {
                    left = Math.Min(left, stripLeft);
                    right = Math.Max(right, stripRight);
                    top = Math.Min(top, stripTop);
                    bottom = Math.Max(bottom, stripBottom);
                }
            });

            return left > right ? styled.Source.Bounds : new PsdRect(domain.Left + left, domain.Top + top, domain.Left + right + 1, domain.Top + bottom + 1);
        }
    }
}
