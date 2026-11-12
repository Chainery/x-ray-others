using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Adjustment layers: the transform recolors the backdrop over the whole target,
    /// masked by the layer's masks, gated by Blend If (This Layer on the adjusted color,
    /// Underlying on the backdrop before adjustment) and merged with the layer's blend
    /// mode, opacity and Fill.
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private void ApplyAdjustment(PlanarImage target, PsdLayer layer, MaskChain masks)
        {
            var adjustment = Adjustments.Create(layer, _options.RenderColorLookup);
            if (adjustment == null)
            {
                return;
            }

            var region = target.Bounds;
            if (region.IsEmpty)
            {
                return;
            }

            var layerMask = LayerMasks(layer, target.Logical, region, region.Top, region.Bottom);
            var opacity = layer.Opacity / 255f * (layer.FillOpacity / 255f);
            var mode = layer.BlendMode == PsdBlendMode.PassThrough || layer.BlendMode == PsdBlendMode.Dissolve
                ? PsdBlendMode.Normal
                : layer.BlendMode;
            var gate = BlendIfOf(layer);
            var width = region.Width;
            Parallelism.For(region.Height, width, delegate(int start, int end)
            {
                var alpha = new float[width];
                var r = new float[width];
                var g = new float[width];
                var b = new float[width];
                var tr = new float[width];
                var tg = new float[width];
                var tb = new float[width];
                for (var y = region.Top + start; y < region.Top + end; y++)
                {
                    var t = target.RowOffset(y, region.Left);
                    Array.Copy(target.R, t, r, 0, width);
                    Array.Copy(target.G, t, g, 0, width);
                    Array.Copy(target.B, t, b, 0, width);
                    Array.Copy(r, tr, width);
                    Array.Copy(g, tg, width);
                    Array.Copy(b, tb, width);
                    adjustment.Apply(r, g, b);
                    for (var i = 0; i < width; i++)
                    {
                        alpha[i] = opacity;
                    }

                    if (layerMask != null)
                    {
                        layerMask.MultiplyRow(y, region.Left, alpha);
                    }

                    for (var chain = masks; chain != null; chain = chain.Next)
                    {
                        chain.Sampler.MultiplyRow(y, region.Left, alpha);
                    }

                    // Blend If: This Layer tests the adjusted color, Underlying the backdrop before it.
                    if (gate != null)
                    {
                        ApplyAdjustmentGate(gate, r, g, b, tr, tg, tb, target, y, region.Left, alpha);
                    }

                    // Adjustments recolor existing coverage only: the clip-mode kernel
                    // blends at full strength where the backdrop has alpha and never grows it.
                    var dr = new float[width];
                    var dg = new float[width];
                    var db = new float[width];
                    var da = new float[width];
                    Array.Copy(target.R, t, dr, 0, width);
                    Array.Copy(target.G, t, dg, 0, width);
                    Array.Copy(target.B, t, db, 0, width);
                    Array.Copy(target.A, t, da, 0, width);
                    BlendKernels.CompositeRow(mode, r, g, b, alpha, dr, dg, db, da, true, ClipCoverageRow(target, t, width));
                    Array.Copy(dr, 0, target.R, t, width);
                    Array.Copy(dg, 0, target.G, t, width);
                    Array.Copy(db, 0, target.B, t, width);
                    Array.Copy(da, 0, target.A, t, width);
                }
            });
        }

        /// <summary>Multiplies an adjustment's amount row by its Blend If gates: This Layer on the adjusted color, Underlying on the pre-adjustment backdrop.</summary>
        private static void ApplyAdjustmentGate(BlendIf gate, float[] adjustedR, float[] adjustedG, float[] adjustedB, float[] beforeR, float[] beforeG, float[] beforeB, PlanarImage target, int y, int x0, float[] alpha)
        {
            gate.MultiplySource(adjustedR, adjustedG, adjustedB, alpha);
            if (!gate.HasUnderlying)
            {
                return;
            }

            var t = target.RowOffset(y, x0);
            var beforeAlpha = new float[alpha.Length];
            Array.Copy(target.A, t, beforeAlpha, 0, alpha.Length);
            gate.MultiplyUnderlying(beforeR, beforeG, beforeB, beforeAlpha, alpha);
        }
    }
}
