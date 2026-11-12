using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Photoshop's eight "special Fill" blend modes, where Fill Opacity does not just
    /// scale coverage the way Opacity does (reference composite_special_fill_rgb,
    /// .reference/src/core/blend_math.cpp, and .reference/docs/blend-modes.md):
    /// Color Burn, Linear Burn, Color Dodge, Linear Dodge and Difference move the
    /// source byte toward the mode's neutral by Fill, then blend with the 8-bit
    /// kernels; Vivid Light, Linear Light and Hard Mix fade their kernels' internal
    /// integer terms by the Fill byte. The alpha split: the blend result carries the
    /// full coverage x opacity weight against the backdrop, while Fill scales only the
    /// output-alpha growth and the transparent-backdrop source term. The kernels are
    /// integer math on 8-bit colors; float planes are rounded to bytes first.
    /// </summary>
    internal static class SpecialFill
    {
        /// <summary>Whether Fill below 100% takes the special path for mode (reference blend_mode_has_special_fill).</summary>
        public static bool Applies(PsdBlendMode mode)
        {
            return mode == PsdBlendMode.ColorBurn || mode == PsdBlendMode.LinearBurn
                || mode == PsdBlendMode.ColorDodge || mode == PsdBlendMode.LinearDodge || mode == PsdBlendMode.Difference
                || mode == PsdBlendMode.VividLight || mode == PsdBlendMode.LinearLight || mode == PsdBlendMode.HardMix;
        }

        /// <summary>The blended byte for one channel at a Fill byte (0-255).</summary>
        public static byte Blend(PsdBlendMode mode, byte source, byte destination, int fillByte)
        {
            switch (mode)
            {
                case PsdBlendMode.VividLight:
                    return VividLight(source, destination, fillByte);
                case PsdBlendMode.LinearLight:
                    return LinearLight(source, destination, fillByte);
                case PsdBlendMode.HardMix:
                    return HardMix(source, destination, fillByte);
            }

            var fill = fillByte / 255f;
            var neutral = mode == PsdBlendMode.ColorBurn || mode == PsdBlendMode.LinearBurn ? 255f : 0f;
            var adjusted = neutral + ((source - neutral) * fill);
            var s = Compat.Clamp((int)Math.Round(adjusted, MidpointRounding.AwayFromZero), 0, 255);
            int d = destination;
            switch (mode)
            {
                case PsdBlendMode.ColorBurn:
                    {
                        var quotient = adjusted <= 0f ? 256 : (int)Math.Floor((255f - d) * 256f / adjusted);
                        return (byte)Compat.Clamp(255 - quotient, 0, 255);
                    }

                case PsdBlendMode.ColorDodge:
                    {
                        var divisor = 256f - adjusted;
                        var value = divisor <= 0f ? 255 : (int)Math.Round(d * 256f / divisor, MidpointRounding.AwayFromZero);
                        return (byte)Compat.Clamp(value, 0, 255);
                    }

                case PsdBlendMode.LinearBurn:
                    return (byte)Compat.Clamp(s + d - 255, 0, 255);
                case PsdBlendMode.LinearDodge:
                    return (byte)Math.Min(255, s + d);
                default:
                    return (byte)Math.Abs(d - s);
            }
        }

        // floor(a/b) for signed a and positive b.
        private static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : -((-a + b - 1) / b);
        }

        private static int NearestHalfUp(int a, int b)
        {
            return ((2 * a) + b) / (2 * b);
        }

        private static int NearestHalfDown(int a, int b)
        {
            return ((2 * a) + b - 1) / (2 * b);
        }

        private static int NearestHalfUpSigned(int a, int b)
        {
            return FloorDiv((2 * a) + b, 2 * b);
        }

        // result = clamp(d + round((2s - 255) * fb / 255) - 1); Fill 0 is identity.
        private static byte LinearLight(byte source, byte destination, int fillByte)
        {
            if (fillByte <= 0)
            {
                return destination;
            }

            var delta = NearestHalfUpSigned(((2 * source) - 255) * fillByte, 255) - 1;
            return (byte)Compat.Clamp(destination + delta, 0, 255);
        }

        // Each half fades its 100%-Fill doubled term toward the half's neutral.
        private static byte VividLight(byte source, byte destination, int fillByte)
        {
            if (fillByte <= 0)
            {
                return destination;
            }

            if (source >= 128)
            {
                var doubled100 = NearestHalfUp((source - 128) * 255, 127);
                var faded = NearestHalfUp(doubled100 * fillByte, 255);
                var divisor = 255 - faded;
                return divisor <= 0 ? (byte)255 : (byte)Math.Min(255, NearestHalfUp(destination * 255, divisor));
            }

            var burn100 = NearestHalfUp(source * 255, 128);
            var doubled = 255 - NearestHalfUp((255 - burn100) * fillByte, 255);
            var numerator = destination + doubled - 255;
            if (numerator <= 0 || doubled <= 0)
            {
                return 0;
            }

            return (byte)Math.Min(255, NearestHalfDown(numerator * 255, doubled));
        }

        // A steep ramp: clamp(round((d - A) * 255 / (255 - fb2))), A = round((255 - s) * fb2 / 255).
        private static byte HardMix(byte source, byte destination, int fillByte)
        {
            var fb2 = fillByte - (fillByte >= 128 ? 1 : 0);
            if (fb2 <= 0)
            {
                return destination;
            }

            var anchor = NearestHalfUp((255 - source) * fb2, 255);
            var value = NearestHalfUpSigned((destination - anchor) * 255, 255 - fb2);
            return (byte)Compat.Clamp(value, 0, 255);
        }

        // __PART3__
        /// <summary>
        /// Composites one row with the special-Fill split. coverage is the source
        /// coverage (pixel alpha times masks and Blend If, without opacity or Fill).
        /// In clipMode the destination is a clipping group: the pixel blends at full
        /// strength inside the clip and alpha follows clipCoverage when given
        /// (unchanged otherwise).
        /// </summary>
        public static void CompositeRow(
            PsdBlendMode mode,
            float[] sr, float[] sg, float[] sb, float[] coverage,
            float fill, float opacity,
            float[] dr, float[] dg, float[] db, float[] da,
            bool clipMode, float[] clipCoverage)
        {
            fill = Compat.Clamp(fill, 0f, 1f);
            opacity = Compat.Clamp(opacity, 0f, 1f);
            var fillByte = (int)Math.Round(fill * 255f, MidpointRounding.AwayFromZero);
            for (var i = 0; i < coverage.Length; i++)
            {
                float pr = dr[i], pg = dg[i], pb = db[i], pa = da[i];
                CompositePixel(mode, sr[i], sg[i], sb[i], coverage[i], fill, fillByte, opacity, ref pr, ref pg, ref pb, ref pa, clipMode, clipCoverage == null ? -1f : clipCoverage[i]);
                dr[i] = pr;
                dg[i] = pg;
                db[i] = pb;
                da[i] = pa;
            }
        }

        /// <summary>
        /// Effect pixels in Color Burn, Linear Burn and Color Dodge over a backdrop that
        /// is not opaque: Photoshop's alpha fold describes only the overlap with the
        /// backdrop, so the reference routes these pixels through the special-Fill split
        /// with the effect alpha as Fill (composite_effect_color calling
        /// composite_special_fill_color(color, 1, alpha, 1, mode)). Handles every pixel
        /// with alpha > 0 whose destination alpha is below 1 and zeroes its alpha so a
        /// following plain composite skips it. coverage carries the group mask chain
        /// (null for none), which scales coverage, not Fill.
        /// </summary>
        public static void CompositeEffectRow(
            PsdBlendMode mode,
            float[] sr, float[] sg, float[] sb, float[] alpha, float[] coverage,
            float[] dr, float[] dg, float[] db, float[] da,
            bool clipMode, float[] clipCoverage)
        {
            for (var i = 0; i < alpha.Length; i++)
            {
                var fill = Compat.Clamp(alpha[i], 0f, 1f);
                if (fill <= 0f || da[i] >= 1f)
                {
                    continue;
                }

                alpha[i] = 0f;
                var fillByte = (int)Math.Round(fill * 255f, MidpointRounding.AwayFromZero);
                var c = coverage == null ? 1f : coverage[i];
                float pr = dr[i], pg = dg[i], pb = db[i], pa = da[i];
                CompositePixel(mode, sr[i], sg[i], sb[i], c, fill, fillByte, 1f, ref pr, ref pg, ref pb, ref pa, clipMode, clipCoverage == null ? -1f : clipCoverage[i]);
                dr[i] = pr;
                dg[i] = pg;
                db[i] = pb;
                da[i] = pa;
            }
        }

        // __PART4__
        // One pixel of the split; clipCoverage below 0 means none.
        private static void CompositePixel(
            PsdBlendMode mode, float sr, float sg, float sb, float coverage, float fill, int fillByte, float opacity,
            ref float dr, ref float dg, ref float db, ref float da, bool clipMode, float clipCoverage)
        {
            var c = Compat.Clamp(coverage, 0f, 1f);
            var effective = c * fill * opacity;
            if (effective <= 0f)
            {
                return;
            }

            var inside = clipCoverage < 0f ? da > 0f : clipCoverage > 0f;
            if (clipMode && !inside)
            {
                return;
            }

            var destinationAlpha = clipMode ? 1f : Compat.Clamp(da, 0f, 1f);
            var overlap = c * opacity;
            var outAlpha = effective + (destinationAlpha * (1f - effective));
            var inverse = 1f / outAlpha;
            dr = Channel(mode, sr, dr, fillByte, effective, overlap, destinationAlpha) * inverse;
            dg = Channel(mode, sg, dg, fillByte, effective, overlap, destinationAlpha) * inverse;
            db = Channel(mode, sb, db, fillByte, effective, overlap, destinationAlpha) * inverse;
            if (!clipMode)
            {
                da = outAlpha;
            }
            else if (clipCoverage >= 0f)
            {
                var normalized = Math.Min(da, clipCoverage) / clipCoverage;
                da = Math.Max(da, clipCoverage * (effective + (normalized * (1f - effective))));
            }
        }

        // Premultiplied numerator of one channel: source-only + blended overlap + destination-only.
        private static float Channel(PsdBlendMode mode, float source, float destination, int fillByte, float effective, float overlap, float destinationAlpha)
        {
            var blended = Blend(mode, BlendIf.ToByte(source), BlendIf.ToByte(destination), fillByte) / 255f;
            return (source * effective * (1f - destinationAlpha))
                + (blended * overlap * destinationAlpha)
                + (destination * destinationAlpha * (1f - overlap));
        }    }
}
