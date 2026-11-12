using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Shadow and glow transfer contours (TrnS, AntA) and gradient glows,
    /// which the reference does not model (it renders them as Linear and solid color).
    /// Calibrated against Photoshop's merged image of psd-tools' layer_params.psd;
    /// see docs/rendering.md, "Contours and gradient glows".
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        /// <summary>
        /// Maps a shadow or glow falloff through its contour: after spread, blur and the
        /// glow Range gain, before the falloff becomes the effect's alpha. Anti-aliasing
        /// interpolates between the table's entries.
        /// </summary>
        private static void ApplyContour(float[] plane, PsdContour contour, bool antiAliased)
        {
            if (contour == null || contour.IsLinear)
            {
                return;
            }

            var lut = StyleContour.BuildLut(contour);
            for (var i = 0; i < plane.Length; i++)
            {
                if (plane[i] > 0f)
                {
                    plane[i] = StyleContour.Sample(lut, plane[i], antiAliased);
                }
            }
        }

        /// <summary>
        /// A gradient glow paints the gradient across its falloff f (after the contour):
        /// color and opacity come from position 1 - f, so the gradient's first stop
        /// sits where the glow is strongest (the layer edge of an outer glow). The falloff
        /// only marks where the glow reaches (above one 8-bit level), and the
        /// gradient's own opacity stops give the alpha. plane is replaced by that reach.
        /// </summary>
        private EffectPaint GlowPaint(PsdEffect glow, float[] plane, PsdRect domain)
        {
            if (glow.Gradient == null)
            {
                return null;
            }

            var gradient = GradientOf(glow);
            if (gradient == null)
            {
                return null;
            }

            var falloff = (float[])plane.Clone();
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = plane[i] * 255f > 1f ? 1f : 0f;
            }

            return delegate(int x, int y)
            {
                var position = 1f - Compat.Clamp(falloff[((y - domain.Top) * domain.Width) + (x - domain.Left)], 0f, 1f);
                if (gradient.Reverse)
                {
                    position = 1f - position;
                }

                float r, g, b;
                gradient.Color(position, false, out r, out g, out b);
                return new EffectColor(r, g, b, gradient.Opacity(position, false));
            };
        }
    }
}
