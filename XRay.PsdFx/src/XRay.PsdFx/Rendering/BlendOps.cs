using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// A blend function B(source, backdrop) over one pixel channel. The original
    /// specializes one SIMD loop per mode; on net45 the same kernels run per pixel
    /// through struct ops, which the JIT inlines without per-pixel dispatch.
    /// </summary>
    internal interface IBlendOp
    {
        void Apply(
            float sr, float sg, float sb,
            float dr, float dg, float db,
            out float rr, out float rg, out float rb);
    }

    /// <summary>Per-channel blend function, lifted to RGB by <see cref="Separable{T}"/>.</summary>
    internal interface IChannelBlend
    {
        float Blend(float s, float d);
    }

    internal struct Separable<T> : IBlendOp
        where T : struct, IChannelBlend
    {
        public void Apply(
            float sr, float sg, float sb,
            float dr, float dg, float db,
            out float rr, out float rg, out float rb)
        {
            var op = default(T);
            rr = op.Blend(sr, dr);
            rg = op.Blend(sg, dg);
            rb = op.Blend(sb, db);
        }
    }

    // The formulas follow Photoshop's 8-bit kernels from the reference compositor
    // (blend_math.cpp) expressed on [0,1] floats: the 8-bit "128" split becomes
    // 128/255, and Linear Light / Pin Light keep Photoshop's -256/255 offsets.
    internal static class BlendConstants
    {
        public const float Zero = 0f;
        public const float One = 1f;
        public const float Two = 2f;
        public const float Half = 128f / 255f;
        public const float Offset256 = 256f / 255f;
        public const float Epsilon = 1e-6f;
        public const float LumR = 0.3f;
        public const float LumG = 0.59f;
        public const float LumB = 0.11f;

        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }

    internal struct NormalBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return s;
        }
    }

    internal struct MultiplyBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return s * d;
        }
    }

    internal struct ScreenBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return s + d - (s * d);
        }
    }

    internal struct OverlayBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return default(HardLightBlend).Blend(d, s);
        }
    }

    internal struct HardLightBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var low = BlendConstants.Two * s * d;
            var high = BlendConstants.One - (BlendConstants.Two * (BlendConstants.One - s) * (BlendConstants.One - d));
            return s < BlendConstants.Half ? low : high;
        }
    }

    internal struct SoftLightBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var one = BlendConstants.One;
            var two = BlendConstants.Two;
            var low = d - ((one - (two * s)) * d * (one - d));
            var dd = d <= 0.25f
                ? ((((16f * d) - 12f) * d) + 4f) * d
                : (float)Math.Sqrt(d);
            var high = d + (((two * s) - one) * (dd - d));
            return s <= 0.5f ? low : high;
        }
    }

    internal struct DarkenBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Min(s, d);
        }
    }

    internal struct LightenBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Max(s, d);
        }
    }

    internal struct ColorDodgeBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var divisor = Math.Max(BlendConstants.One - s, BlendConstants.Epsilon);
            var value = Math.Min(BlendConstants.One, d / divisor);
            if (s >= BlendConstants.One)
            {
                value = BlendConstants.One;
            }

            return d <= BlendConstants.Zero ? BlendConstants.Zero : value;
        }
    }

    internal struct ColorBurnBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var divisor = Math.Max(s, BlendConstants.Epsilon);
            var value = BlendConstants.One - Math.Min(BlendConstants.One, (BlendConstants.One - d) / divisor);
            if (s <= BlendConstants.Zero)
            {
                value = BlendConstants.Zero;
            }

            return d >= BlendConstants.One ? BlendConstants.One : value;
        }
    }

    internal struct LinearBurnBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Max(BlendConstants.Zero, s + d - BlendConstants.One);
        }
    }

    internal struct LinearDodgeBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Min(BlendConstants.One, s + d);
        }
    }

    internal struct DifferenceBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Abs(d - s);
        }
    }

    internal struct ExclusionBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return s + d - (BlendConstants.Two * s * d);
        }
    }

    internal struct SubtractBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return Math.Max(BlendConstants.Zero, d - s);
        }
    }

    internal struct DivideBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var value = Math.Min(BlendConstants.One, d / Math.Max(s, BlendConstants.Epsilon));
            if (s <= BlendConstants.Zero)
            {
                value = BlendConstants.One;
            }

            return d <= BlendConstants.Zero ? BlendConstants.Zero : value;
        }
    }

    internal struct VividLightBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var one = BlendConstants.One;
            var doubled = Math.Max(BlendConstants.Two * s, BlendConstants.Epsilon);
            var burn = Math.Max(BlendConstants.Zero, one - ((one - d) / doubled));
            var divisor = Math.Max(BlendConstants.Two * (one - s), BlendConstants.Epsilon);
            var dodge = Math.Min(one, d / divisor);
            var value = s < BlendConstants.Half ? burn : dodge;
            if (s <= BlendConstants.Zero)
            {
                value = BlendConstants.Zero;
            }

            return s >= one ? one : value;
        }
    }

    internal struct LinearLightBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            return BlendConstants.Clamp01(d + (BlendConstants.Two * s) - BlendConstants.Offset256);
        }
    }

    internal struct PinLightBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            var two = BlendConstants.Two * s;
            var low = Math.Min(d, two);
            var high = Math.Max(d, Math.Max(BlendConstants.Zero, two - BlendConstants.Offset256));
            return s < BlendConstants.Half ? low : high;
        }
    }

    internal struct HardMixBlend : IChannelBlend
    {
        public float Blend(float s, float d)
        {
            // Photoshop thresholds the textbook vivid light at 127/255.
            var one = BlendConstants.One;
            var doubled = Math.Max(BlendConstants.Two * s, BlendConstants.Epsilon);
            var burn = Math.Max(BlendConstants.Zero, one - ((one - d) / doubled));
            if (s <= BlendConstants.Zero)
            {
                burn = d >= one ? one : BlendConstants.Zero;
            }

            var dodge = Math.Min(one, d / Math.Max(one - (BlendConstants.Two * (s - BlendConstants.Half)), BlendConstants.Epsilon));
            var vivid = s < BlendConstants.Half ? burn : dodge;
            return vivid > (127.5f / 255f) ? one : BlendConstants.Zero;
        }
    }

    /// <summary>PDF-spec non-separable helpers (luma-weighted set_lum, clip_color and set_sat).</summary>
    internal static class NonSeparable
    {
        public static float Lum(float r, float g, float b)
        {
            return (BlendConstants.LumR * r) + (BlendConstants.LumG * g) + (BlendConstants.LumB * b);
        }

        public static float Sat(float r, float g, float b)
        {
            return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }

        public static void SetLum(ref float r, ref float g, ref float b, float l)
        {
            var delta = l - Lum(r, g, b);
            r += delta;
            g += delta;
            b += delta;
            ClipColor(ref r, ref g, ref b);
        }

        public static void ClipColor(ref float r, ref float g, ref float b)
        {
            var l = Lum(r, g, b);
            var n = Math.Min(r, Math.Min(g, b));
            var x = Math.Max(r, Math.Max(g, b));
            var zero = BlendConstants.Zero;
            var one = BlendConstants.One;

            if (n < zero && l - n > zero)
            {
                var lowScale = l / Math.Max(l - n, BlendConstants.Epsilon);
                r = l + ((r - l) * lowScale);
                g = l + ((g - l) * lowScale);
                b = l + ((b - l) * lowScale);
            }

            x = Math.Max(r, Math.Max(g, b));
            if (x > one && x - l > zero)
            {
                var highScale = (one - l) / Math.Max(x - l, BlendConstants.Epsilon);
                r = l + ((r - l) * highScale);
                g = l + ((g - l) * highScale);
                b = l + ((b - l) * highScale);
            }
        }

        /// <summary>
        /// set_sat: the max channel becomes s, the min 0, the middle scales
        /// proportionally. Ties resolve identically to the sorted PDF algorithm.
        /// </summary>
        public static void SetSat(ref float r, ref float g, ref float b, float s)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var range = max - min;
            if (range > BlendConstants.Zero)
            {
                var scale = s / Math.Max(range, BlendConstants.Epsilon);
                r = (r - min) * scale;
                g = (g - min) * scale;
                b = (b - min) * scale;
            }
            else
            {
                r = BlendConstants.Zero;
                g = BlendConstants.Zero;
                b = BlendConstants.Zero;
            }
        }
    }

    internal struct HueBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            rr = sr;
            rg = sg;
            rb = sb;
            NonSeparable.SetSat(ref rr, ref rg, ref rb, NonSeparable.Sat(dr, dg, db));
            NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
        }
    }

    internal struct SaturationBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            rr = dr;
            rg = dg;
            rb = db;
            NonSeparable.SetSat(ref rr, ref rg, ref rb, NonSeparable.Sat(sr, sg, sb));
            NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
        }
    }

    internal struct ColorBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            rr = sr;
            rg = sg;
            rb = sb;
            NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
        }
    }

    internal struct LuminosityBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            rr = dr;
            rg = dg;
            rb = db;
            NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(sr, sg, sb));
        }
    }

    internal struct DarkerColorBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            // Ties keep the backdrop.
            var pick = NonSeparable.Lum(sr, sg, sb) < NonSeparable.Lum(dr, dg, db);
            rr = pick ? sr : dr;
            rg = pick ? sg : dg;
            rb = pick ? sb : db;
        }
    }

    internal struct LighterColorBlend : IBlendOp
    {
        public void Apply(float sr, float sg, float sb, float dr, float dg, float db, out float rr, out float rg, out float rb)
        {
            var pick = NonSeparable.Lum(sr, sg, sb) > NonSeparable.Lum(dr, dg, db);
            rr = pick ? sr : dr;
            rg = pick ? sg : dg;
            rb = pick ? sb : db;
        }
    }
}
