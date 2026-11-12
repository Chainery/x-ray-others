using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Row compositing with the W3C/PDF source-over formula Photoshop uses:
    /// <c>co = as(1-ab)Cs + as*ab*B(Cs,Cb) + (1-as)ab*Cb</c>, <c>ao = as + ab(1-as)</c>,
    /// stored as straight color <c>co/ao</c>. All planes are straight-alpha floats.
    /// The original vectorizes with System.Numerics; net45 keeps the identical math
    /// in plain per-pixel loops.
    /// </summary>
    internal static class BlendKernels
    {
        /// <summary>
        /// Composites one row. <paramref name="sa"/> is the effective source alpha
        /// (pixel alpha times opacity and masks). In <paramref name="clipMode"/> the
        /// destination is a clipping group: the source blends against the base color
        /// at full strength only where the base has coverage, and alpha is unchanged.
        /// With <paramref name="clipCoverage"/> (a clip base whose Blend If hid part of
        /// its own alpha) the clip shape is that coverage instead, and members restore
        /// output alpha inside it: <c>max(da, clip * (as + min(da, clip)/clip * (1 - as)))</c>
        /// (reference <c>IsolatedClipGroupTarget</c> after <c>freeze_clip</c>).
        /// </summary>
        public static void CompositeRow(
            PsdBlendMode mode,
            float[] sr, float[] sg, float[] sb, float[] sa,
            float[] dr, float[] dg, float[] db, float[] da,
            bool clipMode, float[] clipCoverage = null)
        {
            if (clipMode && clipCoverage != null && clipCoverage.Length > 0)
            {
                CompositeRowWithClipCoverage(mode, sr, sg, sb, sa, dr, dg, db, da, clipCoverage);
                return;
            }

            switch (mode)
            {
                case PsdBlendMode.Multiply: Row<Separable<MultiplyBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Screen: Row<Separable<ScreenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Overlay: Row<Separable<OverlayBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.SoftLight: Row<Separable<SoftLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.HardLight: Row<Separable<HardLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Darken: Row<Separable<DarkenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Lighten: Row<Separable<LightenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.ColorDodge: Row<Separable<ColorDodgeBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.ColorBurn: Row<Separable<ColorBurnBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.LinearBurn: Row<Separable<LinearBurnBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.LinearDodge: Row<Separable<LinearDodgeBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Difference: Row<Separable<DifferenceBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Exclusion: Row<Separable<ExclusionBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Subtract: Row<Separable<SubtractBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Divide: Row<Separable<DivideBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.VividLight: Row<Separable<VividLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.LinearLight: Row<Separable<LinearLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.PinLight: Row<Separable<PinLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.HardMix: Row<Separable<HardMixBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Hue: Row<HueBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Saturation: Row<SaturationBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Color: Row<ColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.Luminosity: Row<LuminosityBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.DarkerColor: Row<DarkerColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                case PsdBlendMode.LighterColor: Row<LighterColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
                default: Row<Separable<NormalBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            }
        }

        // The clip run of a Blend If base: colors blend exactly as in clip mode, then
        // alpha grows inside the recorded clip coverage. Rare, so it reuses the
        // ordinary clip-mode row on a copy of the alpha and fixes alpha afterwards.
        private static void CompositeRowWithClipCoverage(
            PsdBlendMode mode,
            float[] sr, float[] sg, float[] sb, float[] sa,
            float[] dr, float[] dg, float[] db, float[] da,
            float[] clipCoverage)
        {
            var count = sa.Length;
            var inside = new float[count];
            for (var i = 0; i < count; i++)
            {
                inside[i] = clipCoverage[i] > 0f ? 1f : 0f;
            }

            CompositeRow(mode, sr, sg, sb, sa, dr, dg, db, inside, clipMode: true);
            for (var i = 0; i < count; i++)
            {
                var clip = clipCoverage[i];
                if (clip <= 0f)
                {
                    continue;
                }

                var a = Compat.Clamp(sa[i], 0f, 1f);
                var normalized = Math.Min(da[i], clip) / clip;
                da[i] = Math.Max(da[i], clip * (a + (normalized * (1f - a))));
            }
        }

        private static void Row<TOp>(
            float[] sr, float[] sg, float[] sb, float[] sa,
            float[] dr, float[] dg, float[] db, float[] da,
            bool clipMode)
            where TOp : struct, IBlendOp
        {
            var op = default(TOp);
            var count = sa.Length;
            for (var i = 0; i < count; i++)
            {
                sa[i] = BlendConstants.Clamp01(sa[i]);
                op.Apply(sr[i], sg[i], sb[i], dr[i], dg[i], db[i], out var br, out var bg, out var bb);
                br = BlendConstants.Clamp01(br);
                bg = BlendConstants.Clamp01(bg);
                bb = BlendConstants.Clamp01(bb);
                if (clipMode)
                {
                    if (da[i] > BlendConstants.Zero)
                    {
                        dr[i] += sa[i] * (br - dr[i]);
                        dg[i] += sa[i] * (bg - dg[i]);
                        db[i] += sa[i] * (bb - db[i]);
                    }

                    continue;
                }

                var outAlpha = sa[i] + da[i] - (sa[i] * da[i]);
                var sourceOnly = sa[i] * (BlendConstants.One - da[i]);
                var both = sa[i] * da[i];
                var destinationOnly = (BlendConstants.One - sa[i]) * da[i];
                if (outAlpha > BlendConstants.Zero)
                {
                    var inverse = BlendConstants.One / Math.Max(outAlpha, BlendConstants.Epsilon);
                    dr[i] = ((sourceOnly * sr[i]) + (both * br) + (destinationOnly * dr[i])) * inverse;
                    dg[i] = ((sourceOnly * sg[i]) + (both * bg) + (destinationOnly * dg[i])) * inverse;
                    db[i] = ((sourceOnly * sb[i]) + (both * bb) + (destinationOnly * db[i])) * inverse;
                }
                else
                {
                    dr[i] = 0f;
                    dg[i] = 0f;
                    db[i] = 0f;
                }

                da[i] = outAlpha;
            }
        }

        /// <summary>Premultiplied interpolation from the before rows toward the current row by <paramref name="t"/> (pass-through group opacity).</summary>
        public static void FadeTowardRow(
            float[] br, float[] bg, float[] bb, float[] ba,
            float[] dr, float[] dg, float[] db, float[] da, float t)
        {
            for (var i = 0; i < da.Length; i++)
            {
                var alpha = ba[i] + ((da[i] - ba[i]) * t);
                if (alpha <= 0)
                {
                    dr[i] = dg[i] = db[i] = da[i] = 0;
                    continue;
                }

                var inverse = 1f / alpha;
                dr[i] = ((br[i] * ba[i]) + (((dr[i] * da[i]) - (br[i] * ba[i])) * t)) * inverse;
                dg[i] = ((bg[i] * ba[i]) + (((dg[i] * da[i]) - (bg[i] * ba[i])) * t)) * inverse;
                db[i] = ((bb[i] * ba[i]) + (((db[i] * da[i]) - (bb[i] * ba[i])) * t)) * inverse;
                da[i] = alpha;
            }
        }

        /// <summary>Multiplies <paramref name="destination"/> by <paramref name="factor"/> element-wise.</summary>
        public static void MultiplyInPlace(float[] destination, float[] factor)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] *= factor[i];
            }
        }

        /// <summary>Scales <paramref name="destination"/> by a constant.</summary>
        public static void Scale(float[] destination, float factor)
        {
            if (factor == 1f)
            {
                return;
            }

            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] *= factor;
            }
        }
    }
}
