using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Bevel and emboss, ported from the reference render_bevel_emboss and
    /// bevel_technique_height_mask: a height field per technique (Smooth is the
    /// tent-blurred matte, the chisels an exact distance roof), optional contour and
    /// texture, then Lambert shading against the light with Photoshop's calibrated split
    /// (highlight = excess over the flat face, normalized to the headroom; shadow = deficit
    /// normalized to the floor; pillow and emboss shadows use the unnormalized deficit).
    /// Stroke Emboss runs the inner-bevel model on the union of the visible stroke
    /// bands instead of the layer matte.
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        /// <summary>
        /// Bevels paint over the stroke bands; Stroke Emboss shades the rendered Stroke
        /// effect itself, so it paints after every other bevel (reference
        /// composite_pixel_layer, the stroke_emboss step).
        /// </summary>
        private void RenderBevels(PlanarImage target, StyledLayer styled, MaskChain masks, bool clipMode)
        {
            foreach (var strokeEmboss in new[] { false, true })
            {
                foreach (var bevel in styled.Effects.Bevels)
                {
                    var highlightOpacity = OpacityOf(bevel);
                    var shadowOpacity = bevel.ShadowOpacity.HasValue ? bevel.ShadowOpacity.Value : 0f;
                    if ((BevelStyleOf(bevel) == PsdBevelStyle.StrokeEmboss) != strokeEmboss || (highlightOpacity <= 0 && shadowOpacity <= 0))
                    {
                        continue;
                    }

                    // Stroke Emboss needs a visible stroke: without one it renders nothing.
                    var surface = strokeEmboss ? styled.StrokeEmbossMatte : styled.Matte;
                    if (surface == null)
                    {
                        continue;
                    }

                    float[] highlight, shadow;
                    BevelPlanes(styled, bevel, surface, out highlight, out shadow);
                    var highlightColor = ColorOf(bevel);
                    var shadowColor = bevel.ShadowColor.HasValue ? bevel.ShadowColor.Value : default(PsdColor);
                    var shadowMode = bevel.ShadowBlendMode.HasValue ? bevel.ShadowBlendMode.Value : PsdBlendMode.Multiply;
                    DrawEffect(target, styled, new Plane(styled.Domain, highlight), highlightOpacity * styled.Opacity, highlightColor, ModeOf(bevel), masks, clipMode, null, null);
                    DrawEffect(target, styled, new Plane(styled.Domain, shadow), shadowOpacity * styled.Opacity, shadowColor, shadowMode, masks, clipMode, null, null);
                }
            }
        }

        private static int TentPeak(float size)
        {
            return size <= 0 ? 0 : Math.Max(2, (int)Math.Round(size, MidpointRounding.AwayFromZero));
        }

        /// <summary>Highlight and shadow planes over the domain for alpha: the layer matte, or the stroke bands for Stroke Emboss.</summary>
        private void BevelPlanes(StyledLayer styled, PsdEffect bevel, float[] alpha, out float[] highlight, out float[] shadow)
        {
            var width = styled.Domain.Width;
            var height = styled.Domain.Height;
            var style = BevelStyleOf(bevel);
            var pillow = style == PsdBevelStyle.PillowEmboss;
            var pillowFamily = pillow || style == PsdBevelStyle.Emboss;
            var depth = (bevel.Depth.HasValue ? bevel.Depth.Value : 100f) / 100f;
            var size = SizeOf(bevel);
            var contour = bevel.BevelContour;
            var contourRange = bevel.ContourRange.HasValue ? bevel.ContourRange.Value : 1f;

            var slopeGain = 1f;
            if (contour != null && contour.IsLinear)
            {
                slopeGain = 1f / Compat.Clamp(contourRange, 0.01f, 1f);
            }

            var normalScale = pillowFamily
                ? 0.5f * Compat.Clamp(depth, 0.25f, 10f) * TentPeak(size * 0.5f) * slopeGain
                : 0.5f * Compat.Clamp(depth, 0.01f, 10f) * Math.Max(1f, size) * slopeGain;

            var heightField = HeightField(alpha, width, height, bevel, pillowFamily ? size * 0.5f : size);
            if (contour != null && !contour.IsLinear)
            {
                var lut = StyleContour.BuildLut(contour);
                var range = Compat.Clamp(contourRange, 0.01f, 1f);
                var antiAliased = bevel.ContourAntiAliased;
                for (var i = 0; i < heightField.Length; i++)
                {
                    heightField[i] = StyleContour.Sample(lut, Compat.Clamp(heightField[i] / range, 0f, 1f), antiAliased);
                }
            }

            PatternTile tile;
            if (bevel.Texture != null)
            {
                var placement = PatternPlacementOf(bevel.Texture);
                if (placement != null && _document.Patterns.TryGetValue(placement.PatternId, out tile))
                {
                    ApplyTexture(styled, bevel, placement, tile, alpha, heightField, width, height);
                }
            }

            var angle = (180f - AngleOf(bevel)) * MathF.PI / 180f;
            var altitudeDegrees = bevel.Altitude.HasValue ? bevel.Altitude.Value : 30f;
            var altitude = Compat.Clamp(altitudeDegrees, 0f, 90f) * MathF.PI / 180f;
            var horizontal = MathF.Cos(altitude);
            var lightX = -MathF.Cos(angle) * horizontal;
            var lightY = -MathF.Sin(angle) * horizontal;
            var lightZ = MathF.Sin(altitude);
            var glossContour = bevel.Gloss;
            var gloss = glossContour == null || glossContour.IsLinear ? null : StyleContour.BuildLut(glossContour);
            var glossAntiAliased = bevel.GlossAntiAliased;
            var direction = bevel.DirectionUp == true ? 1f : -1f;

            var highlights = new float[alpha.Length];
            var shadows = new float[alpha.Length];
            var field = heightField;
            Parallelism.For(height, width, delegate(int start, int end)
            {
                for (var y = start; y < end; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var index = (y * width) + x;
                        var matte = Compat.Clamp(alpha[index], 0f, 1f);
                        float effectAlpha;
                        if (style == PsdBevelStyle.InnerBevel || style == PsdBevelStyle.StrokeEmboss)
                        {
                            effectAlpha = matte;
                        }
                        else if (style == PsdBevelStyle.OuterBevel)
                        {
                            effectAlpha = 1f - matte;
                        }
                        else
                        {
                            effectAlpha = 1f;
                        }

                        if (effectAlpha <= 0)
                        {
                            continue;
                        }

                        var left = FieldAt(field, width, height, x - 1, y);
                        var right = FieldAt(field, width, height, x + 1, y);
                        var top = FieldAt(field, width, height, x, y - 1);
                        var bottom = FieldAt(field, width, height, x, y + 1);
                        var baseX = (left - right) * normalScale;
                        var baseY = (top - bottom) * normalScale;
                        var flat = left == right && top == bottom;

                        // Shade both sides of the pillow; a single direction otherwise.
                        var passes = pillow ? 2 : 1;
                        for (var pass = 0; pass < passes; pass++)
                        {
                            var sign = direction;
                            var weight = effectAlpha;
                            if (pillow)
                            {
                                if (pass == 0)
                                {
                                    sign = bevel.DirectionUp == true ? -1f : 1f;
                                    weight = effectAlpha * (1f - matte);
                                }
                                else
                                {
                                    sign = -(bevel.DirectionUp == true ? -1f : 1f);
                                    weight = effectAlpha * matte;
                                }
                            }

                            if (weight <= 0)
                            {
                                continue;
                            }

                            var gx = baseX * sign;
                            var gy = baseY * sign;
                            var length = MathF.Sqrt((gx * gx) + (gy * gy) + 1f);
                            var raw = (gx * lightX) + (gy * lightY) + lightZ;
                            var surface = raw / Math.Max(0.0001f, length);
                            var lighting = Split(surface, lightZ);
                            if (pillowFamily && raw < lightZ)
                            {
                                lighting = -((lightZ - raw) / Math.Max(0.01f, lightZ));
                            }

                            if (gloss != null)
                            {
                                if (flat)
                                {
                                    weight *= matte;
                                    if (weight <= 0)
                                    {
                                        continue;
                                    }
                                }

                                var remapped = StyleContour.Sample(gloss, Compat.Clamp(surface, 0f, 1f), glossAntiAliased);
                                lighting = Split(remapped, lightZ);
                                if (pillowFamily && remapped < lightZ)
                                {
                                    var remappedRaw = StyleContour.Sample(gloss, Compat.Clamp(raw, 0f, 1f), glossAntiAliased);
                                    if (remappedRaw < lightZ)
                                    {
                                        lighting = -((lightZ - remappedRaw) / Math.Max(0.01f, lightZ));
                                    }
                                }
                            }

                            if (lighting > 0)
                            {
                                highlights[index] = Math.Min(1f, highlights[index] + (Math.Min(1f, lighting) * weight));
                            }
                            else if (lighting < 0)
                            {
                                shadows[index] = Math.Min(1f, shadows[index] + (Math.Min(1f, -lighting) * weight));
                            }
                        }
                    }
                }
            });

            highlight = highlights;
            shadow = shadows;
        }

        private static float FieldAt(float[] field, int width, int height, int x, int y)
        {
            return (uint)x < (uint)width && (uint)y < (uint)height ? field[(y * width) + x] : 0f;
        }

        private static float Split(float surface, float lightZ)
        {
            return surface >= lightZ
                ? (surface - lightZ) / Math.Max(0.01f, 1f - lightZ)
                : -((lightZ - surface) / Math.Max(0.01f, lightZ));
        }

        /// <summary>0 outside, 1 inside: Smooth blurs the matte with the tent, the chisels build an exact distance roof.</summary>
        private static float[] HeightField(float[] alpha, int width, int height, PsdEffect bevel, float size)
        {
            size = Math.Max(0.01f, size);
            var technique = bevel.BevelTechnique.HasValue ? bevel.BevelTechnique.Value : PsdBevelTechnique.Smooth;
            float[] field;
            if (technique == PsdBevelTechnique.Smooth)
            {
                field = (float[])alpha.Clone();
                EffectMasks.TentBlur(field, width, height, TentPeak(size));
            }
            else
            {
                var toPainted = EffectMasks.DistanceField(alpha, width, height, true);
                var toClear = EffectMasks.DistanceField(alpha, width, height, false);
                var roof = new float[alpha.Length];
                for (var i = 0; i < roof.Length; i++)
                {
                    var a = Compat.Clamp(alpha[i], 0f, 1f);
                    var inside = 0.5f + (0.5f * Compat.Clamp(toClear[i] / size, 0f, 1f));
                    var outside = 0.5f - (0.5f * Compat.Clamp(toPainted[i] / size, 0f, 1f));
                    roof[i] = (outside * (1f - a)) + (inside * a);
                }

                field = roof;

                if (technique == PsdBevelTechnique.ChiselSoft)
                {
                    EffectMasks.BoxBlur(field, width, height, 1, 1);
                }
            }

            var soften = bevel.Soften.HasValue ? bevel.Soften.Value : 0f;
            if (soften > 0)
            {
                EffectMasks.LayerStyleBlur(field, width, height, soften);
            }

            for (var i = 0; i < field.Length; i++)
            {
                field[i] = Compat.Clamp(field[i], 0f, 1f);
            }

            return field;
        }

        /// <summary>
        /// Texture embosses the face: pattern luminance (dark raised by default) is
        /// smoothed by a 3x3 box and added to the height field, weighted by the face.
        /// </summary>
        private static void ApplyTexture(StyledLayer styled, PsdEffect bevel, PatternPlacement texture, PatternTile tile, float[] alpha, float[] heightField, int width, int height)
        {
            var sampler = new PatternSampler(tile, texture, styled.Layer);
            var bump = new float[heightField.Length];
            var domain = styled.Domain;
            Parallelism.For(height, width, delegate(int start, int end)
            {
                for (var y = start; y < end; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        float r, g, b, a;
                        sampler.Sample(domain.Left + x, domain.Top + y, out r, out g, out b, out a);
                        var luminance = ((float)Math.Round(r * 255f) * 299f + ((float)Math.Round(g * 255f) * 590f) + ((float)Math.Round(b * 255f) * 111f)) / 255000f;
                        bump[(y * width) + x] = bevel.TextureInvert == true ? luminance - 0.5f : 0.5f - luminance;
                    }
                }
            });

            EffectMasks.BoxBlur(bump, width, height, 1, 1);
            var style = BevelStyleOf(bevel);
            var faceFromMatte = style == PsdBevelStyle.InnerBevel || style == PsdBevelStyle.StrokeEmboss;
            var textureDepth = bevel.TextureDepth.HasValue ? bevel.TextureDepth.Value : 1f;
            for (var i = 0; i < heightField.Length; i++)
            {
                var coverage = faceFromMatte ? alpha[i] : 1f - Math.Abs((Compat.Clamp(heightField[i], 0f, 1f) * 2f) - 1f);
                heightField[i] += bump[i] * textureDepth * Compat.Clamp(coverage, 0f, 1f);
            }
        }
    }
}
