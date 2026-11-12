using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Advanced Blending: Blend If gates, Fill on the eight special modes, and the
    /// channel restrictions (brst). Rules: .reference/docs/blend-modes.md,
    /// .reference/docs/ps-compat.md ("Advanced Blending Channels") and
    /// .reference/docs/layer-effects-render.md ("Blend If rendering").
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private const byte RestrictRed = 0x01;
        private const byte RestrictGreen = 0x02;
        private const byte RestrictBlue = 0x04;
        private const byte RestrictAll = RestrictRed | RestrictGreen | RestrictBlue;

        private readonly Dictionary<PsdLayer, BlendIf> _blendIfCache = new Dictionary<PsdLayer, BlendIf>();
        private readonly Dictionary<PsdLayer, byte> _restrictionCache = new Dictionary<PsdLayer, byte>();

        // Clip coverage of a clipping-run buffer whose base has Blend If: the base's
        // ungated alpha, which stays the clip shape for the members (reference
        // record_clip_coverage / freeze_clip).
        private readonly Dictionary<PlanarImage, float[]> _clipCoverage = new Dictionary<PlanarImage, float[]>();

        /// <summary>The layer's rendered Blend If gate, or null (cached).</summary>
        private BlendIf BlendIfOf(PsdLayer layer)
        {
            BlendIf gate;
            if (!_blendIfCache.TryGetValue(layer, out gate))
            {
                gate = BlendIf.FromLayer(layer);
                _blendIfCache[layer] = gate;
            }

            return gate;
        }

        /// <summary>
        /// The channels a layer excludes from compositing (bit 0 red, 1 green, 2 blue).
        /// brst is a bare list of big-endian u32 channel indices; anything that does
        /// not map onto RGB (another color mode, an index above 2, a ragged length) is
        /// not rendered, as in the reference import (psd_document_io.cpp).
        /// </summary>
        private byte RestrictionOf(PsdLayer layer)
        {
            byte mask;
            if (_restrictionCache.TryGetValue(layer, out mask))
            {
                return mask;
            }

            mask = 0;
            var block = layer.GetTaggedBlock("brst");
            if (block != null && block.Length % 4 == 0 && layer.Document.ColorMode == PsdColorMode.Rgb)
            {
                for (var i = 0; i + 4 <= block.Length; i += 4)
                {
                    var index = ((uint)block[i] << 24) | ((uint)block[i + 1] << 16) | ((uint)block[i + 2] << 8) | block[i + 3];
                    if (index > 2)
                    {
                        mask = 0;
                        break;
                    }

                    mask |= (byte)(1 << (int)index);
                }
            }

            _restrictionCache[layer] = mask;
            return mask;
        }

        /// <summary>Every pixel a layer (with its effects) can write, used to snapshot channel-restricted composites.</summary>
        private PsdRect AffectedBounds(PsdLayer layer)
        {
            if (layer.Kind == PsdLayerKind.Adjustment || layer.Kind == PsdLayerKind.Group)
            {
                return _canvas;
            }

            var pixels = LayerPixels(layer);
            var bounds = pixels != null ? pixels.Logical : default(PsdRect);
            if (!bounds.IsEmpty)
            {
                var fx = EffectsOf(layer);
                if (fx != null)
                {
                    var reach = EffectReach(fx);
                    bounds = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach);
                }
            }

            return bounds;
        }

        /// <summary>
        /// Runs draw with Photoshop's channel restriction: each excluded channel keeps
        /// the backdrop's PREMULTIPLIED value, c = pre_c * pre_a / out_a, while the
        /// other channels and alpha composite normally (reference
        /// ChannelRestrictedTarget). Keeping the premultiplied value per write or once
        /// at the end is the same thing, so the whole composite runs first and the
        /// excluded channels are restored over region afterwards.
        /// </summary>
        private static void WithChannelRestriction(PlanarImage target, byte restriction, PsdRect region, Action draw)
        {
            region = region.Intersect(target.Bounds);
            if (restriction == 0 || region.IsEmpty)
            {
                draw();
                return;
            }

            var before = Crop(target, region);
            draw();
            var width = region.Width;
            for (var y = region.Top; y < region.Bottom; y++)
            {
                var t = target.RowOffset(y, region.Left);
                var b = before.RowOffset(y, region.Left);
                if ((restriction & RestrictRed) != 0)
                {
                    KeepChannel(before.R, before.A, target.R, target.A, b, t, width);
                }

                if ((restriction & RestrictGreen) != 0)
                {
                    KeepChannel(before.G, before.A, target.G, target.A, b, t, width);
                }

                if ((restriction & RestrictBlue) != 0)
                {
                    KeepChannel(before.B, before.A, target.B, target.A, b, t, width);
                }
            }
        }

        private static void KeepChannel(float[] preColor, float[] preAlpha, float[] color, float[] alpha, int sourceOffset, int targetOffset, int width)
        {
            for (var i = 0; i < width; i++)
            {
                var a = alpha[targetOffset + i];
                if (a <= 0f)
                {
                    continue;
                }

                // Equal alphas restore the exact value so alpha-neutral writes cannot drift.
                var preC = preColor[sourceOffset + i];
                color[targetOffset + i] = preAlpha[sourceOffset + i] == a ? preC : Compat.Clamp(preC * preAlpha[sourceOffset + i] / a, 0f, 1f);
            }
        }

        /// <summary>The clip coverage row of a Blend If clipping-run buffer, or null.</summary>
        private float[] ClipCoverageRow(PlanarImage target, int offset, int width)
        {
            float[] plane;
            if (_clipCoverage.TryGetValue(target, out plane))
            {
                var row = new float[width];
                Array.Copy(plane, offset, row, 0, width);
                return row;
            }

            return null;
        }

        /// <summary>
        /// Composites one row of layer content. coverage holds the source coverage
        /// (pixel alpha times masks and knockouts, without opacity or Fill) and is
        /// consumed. Applies, in the reference order: clip-coverage recording, the
        /// Blend If gates (This Layer on the source color, Underlying on gateBackdrop
        /// or the target as it stands), the special-Fill split, then opacity, Fill and
        /// Dissolve.
        /// </summary>
        private void CompositeLayerRow(
            PlanarImage target, int y, int x0, PsdBlendMode mode,
            float[] r, float[] g, float[] b, float[] coverage,
            float opacity, float fill, BlendIf gate, PlanarImage gateBackdrop, bool clipMode)
        {
            var width = coverage.Length;
            var t = target.RowOffset(y, x0);
            float[] clip;
            _clipCoverage.TryGetValue(target, out clip);
            if (clip != null && !clipMode)
            {
                // The clip shape is the base's coverage before Blend If (Blend If on a
                // clipping base does not shrink the clip).
                var strength = opacity * fill;
                for (var i = 0; i < width; i++)
                {
                    var a = Compat.Clamp(coverage[i] * strength, 0f, 1f);
                    clip[t + i] = a + (clip[t + i] * (1f - a));
                }
            }

            if (gate != null)
            {
                ApplyGate(gate, r, g, b, coverage, gateBackdrop != null ? gateBackdrop : target, y, x0);
            }

            float[] clipRow = null;
            if (clipMode && clip != null)
            {
                clipRow = new float[width];
                Array.Copy(clip, t, clipRow, 0, width);
            }

            var dr = new float[width];
            var dg = new float[width];
            var db = new float[width];
            var da = new float[width];
            Array.Copy(target.R, t, dr, 0, width);
            Array.Copy(target.G, t, dg, 0, width);
            Array.Copy(target.B, t, db, 0, width);
            Array.Copy(target.A, t, da, 0, width);
            if (fill < 1f && SpecialFill.Applies(mode))
            {
                SpecialFill.CompositeRow(mode, r, g, b, coverage, fill, opacity, dr, dg, db, da, clipMode, clipRow);
            }
            else
            {
                BlendKernels.Scale(coverage, opacity * fill);
                if (mode == PsdBlendMode.Dissolve)
                {
                    Dissolve(coverage, x0, y);
                    mode = PsdBlendMode.Normal;
                }

                BlendKernels.CompositeRow(mode, r, g, b, coverage, dr, dg, db, da, clipMode, clipRow);
            }

            Array.Copy(dr, 0, target.R, t, width);
            Array.Copy(dg, 0, target.G, t, width);
            Array.Copy(db, 0, target.B, t, width);
            Array.Copy(da, 0, target.A, t, width);
        }

        /// <summary>Multiplies coverage by both Blend If gates; backdrop pixels outside backdrop count as transparent (they pass).</summary>
        private void ApplyGate(BlendIf gate, float[] r, float[] g, float[] b, float[] coverage, PlanarImage backdrop, int y, int x0)
        {
            gate.MultiplySource(r, g, b, coverage);
            if (!gate.HasUnderlying || y < backdrop.Bounds.Top || y >= backdrop.Bounds.Bottom)
            {
                return;
            }

            var start = Math.Max(x0, backdrop.Bounds.Left);
            var end = Math.Min(x0 + coverage.Length, backdrop.Bounds.Right);
            if (end <= start)
            {
                return;
            }

            var width = end - start;
            var o = backdrop.RowOffset(y, start);
            var slice = new float[width];
            Array.Copy(coverage, start - x0, slice, 0, width);
            var br = new float[width];
            var bg = new float[width];
            var bb = new float[width];
            var ba = new float[width];
            Array.Copy(backdrop.R, o, br, 0, width);
            Array.Copy(backdrop.G, o, bg, 0, width);
            Array.Copy(backdrop.B, o, bb, 0, width);
            Array.Copy(backdrop.A, o, ba, 0, width);
            gate.MultiplyUnderlying(br, bg, bb, ba, slice);
            Array.Copy(slice, 0, coverage, start - x0, width);
        }
    }
}
