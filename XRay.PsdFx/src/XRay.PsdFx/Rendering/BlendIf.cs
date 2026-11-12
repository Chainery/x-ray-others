using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Photoshop's Blend If gates (layer blending ranges), calibrated in the reference
    /// <c>blend_if_*</c> functions and documented in .reference/docs/layer-effects-render.md
    /// "Blend If rendering":
    /// <list type="bullet">
    /// <item>The record holds Gray, Red, Green and Blue entries, each a This Layer and an Underlying Layer range of four bytes (black low, black high, white low, white high), followed by one identity transparency entry. Other shapes stay unrendered.</item>
    /// <item>A split black range <c>[a,b]</c> keeps byte v as <c>(v-a+1)/(b-a+1)</c>, joined handles cut off hard, the white side mirrors it. Each gate is a byte <c>(n*255)/d</c>, and the four gates multiply with truncation (<c>f = f*g/255</c>).</item>
    /// <item>Gray is <c>(299R + 590G + 111B + 500)/1000</c>.</item>
    /// <item>The Underlying gate tests the backdrop's covered fraction only: <c>(1-da) + da*gate</c>.</item>
    /// </list>
    /// Gates are evaluated on 8-bit colors; the float planes are rounded to bytes first.
    /// </summary>
    internal sealed class BlendIf
    {
        private const int EntryCount = 4;
        private static readonly byte[] IdentityEntry = { 0, 0, 255, 255, 0, 0, 255, 255 };

        // Per channel (Gray, R, G, B): 256-entry gate tables for each side.
        private readonly byte[][] _source;
        private readonly byte[][] _underlying;

        private BlendIf(byte[][] source, byte[][] underlying, bool hasSource, bool hasUnderlying)
        {
            _source = source;
            _underlying = underlying;
            HasSource = hasSource;
            HasUnderlying = hasUnderlying;
        }

        /// <summary>Some This Layer range is not the identity.</summary>
        public bool HasSource { get; private set; }

        /// <summary>Some Underlying Layer range is not the identity (the gate needs the backdrop).</summary>
        public bool HasUnderlying { get; private set; }

        /// <summary>
        /// The rendered gate of a layer, or null when its ranges are absent, the identity,
        /// or not modeled (a non-RGB document, another payload size, unordered handles).
        /// </summary>
        public static BlendIf FromLayer(PsdLayer layer)
        {
            return layer.Document.ColorMode == PsdColorMode.Rgb
                ? Parse(layer.BlendingRanges)
                : null;
        }

        /// <summary>Decodes an RGB blending-ranges payload (reference <c>decode_layer_blend_if</c>).</summary>
        public static BlendIf Parse(byte[] payload)
        {
            if (payload == null || payload.Length != 40 || !TailIsIdentity(payload))
            {
                return null;
            }

            var source = new byte[EntryCount][];
            var underlying = new byte[EntryCount][];
            var hasSource = false;
            var hasUnderlying = false;
            for (var channel = 0; channel < EntryCount; channel++)
            {
                var o = channel * 8;
                if (!IsValid(payload, o) || !IsValid(payload, o + 4))
                {
                    return null;
                }

                hasSource |= !IsIdentity(payload, o);
                hasUnderlying |= !IsIdentity(payload, o + 4);
                source[channel] = Table(payload[o], payload[o + 1], payload[o + 2], payload[o + 3]);
                underlying[channel] = Table(payload[o + 4], payload[o + 5], payload[o + 6], payload[o + 7]);
            }

            return hasSource || hasUnderlying ? new BlendIf(source, underlying, hasSource, hasUnderlying) : null;
        }

        private static bool TailIsIdentity(byte[] payload)
        {
            for (var i = 0; i < IdentityEntry.Length; i++)
            {
                if (payload[32 + i] != IdentityEntry[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValid(byte[] payload, int offset)
        {
            return payload[offset] <= payload[offset + 1]
                && payload[offset + 1] <= payload[offset + 2]
                && payload[offset + 2] <= payload[offset + 3];
        }

        private static bool IsIdentity(byte[] payload, int offset)
        {
            return payload[offset] == 0 && payload[offset + 1] == 0
                && payload[offset + 2] == 255 && payload[offset + 3] == 255;
        }

        private static byte[] Table(byte blackLow, byte blackHigh, byte whiteLow, byte whiteHigh)
        {
            var table = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                table[v] = ThresholdByte(blackLow, blackHigh, whiteLow, whiteHigh, (byte)v);
            }

            return table;
        }

        /// <summary>One range's gate for a byte value (reference <c>blend_if_threshold_alpha_byte</c>).</summary>
        public static byte ThresholdByte(byte blackLow, byte blackHigh, byte whiteLow, byte whiteHigh, byte value)
        {
            if (value < blackLow || value > whiteHigh)
            {
                return 0;
            }

            if (value < blackHigh)
            {
                // Split handles include both byte endpoints in the feather.
                return (byte)((value - blackLow + 1) * 255 / (blackHigh - blackLow + 1));
            }

            if (value > whiteLow)
            {
                return (byte)((whiteHigh - value + 1) * 255 / (whiteHigh - whiteLow + 1));
            }

            return 255;
        }

        /// <summary>Photoshop's composite gray for Blend If (reference <c>blend_if_gray_value</c>).</summary>
        public static byte GrayValue(byte r, byte g, byte b)
        {
            return (byte)(((299 * r) + (590 * g) + (111 * b) + 500) / 1000);
        }

        /// <summary>The This Layer gate (0-255) for a source color.</summary>
        public byte SourceByte(byte r, byte g, byte b)
        {
            return Gate(_source, r, g, b);
        }

        /// <summary>The Underlying Layer gate (0-255) for a fully covered backdrop color.</summary>
        public byte UnderlyingByte(byte r, byte g, byte b)
        {
            return Gate(_underlying, r, g, b);
        }

        private static byte Gate(byte[][] tables, byte r, byte g, byte b)
        {
            var factor = 255;
            factor = factor * tables[0][GrayValue(r, g, b)] / 255;
            factor = factor * tables[1][r] / 255;
            factor = factor * tables[2][g] / 255;
            factor = factor * tables[3][b] / 255;
            return (byte)factor;
        }

        /// <summary>Multiplies <paramref name="factor"/> by the This Layer gate of each source pixel.</summary>
        public void MultiplySource(float[] r, float[] g, float[] b, float[] factor)
        {
            if (!HasSource)
            {
                return;
            }

            for (var i = 0; i < factor.Length; i++)
            {
                if (factor[i] > 0f)
                {
                    factor[i] *= SourceByte(ToByte(r[i]), ToByte(g[i]), ToByte(b[i])) / 255f;
                }
            }
        }

        /// <summary>
        /// Multiplies <paramref name="factor"/> by the Underlying Layer gate of each backdrop
        /// pixel; the transparent fraction of the backdrop always passes.
        /// </summary>
        public void MultiplyUnderlying(float[] r, float[] g, float[] b, float[] a, float[] factor)
        {
            if (!HasUnderlying)
            {
                return;
            }

            for (var i = 0; i < factor.Length; i++)
            {
                if (factor[i] > 0f)
                {
                    var coverage = Compat.Clamp(a[i], 0f, 1f);
                    factor[i] *= (1f - coverage) + (coverage * (UnderlyingByte(ToByte(r[i]), ToByte(g[i]), ToByte(b[i])) / 255f));
                }
            }
        }

        internal static byte ToByte(float value)
        {
            return (byte)Compat.Clamp((int)((value * 255f) + 0.5f), 0, 255);
        }
    }
}
