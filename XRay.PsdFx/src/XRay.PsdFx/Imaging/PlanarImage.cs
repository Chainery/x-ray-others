using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Straight-alpha RGBA image stored as four float planes, positioned at
    /// <see cref="Bounds"/> in document space. net45 port of the original PlanarImage:
    /// scalar loops replace the SIMD scans; the output is bit-identical, only slower.
    /// </summary>
    internal sealed class PlanarImage
    {
        /// <summary>Array.MaxLength does not exist on net45.</summary>
        private const long MaxArrayLength = 0x7FFFFFC7L;

        public PlanarImage(PsdRect bounds) : this(bounds, default(PsdRect?), false)
        {
        }

        /// <summary>
        /// Band rendering keeps only the band's rows of a buffer (<see cref="Bounds"/>)
        /// but decides everything that depends on a buffer's extent (vector mask rasters,
        /// group and effect regions) from <paramref name="logical"/>.
        /// </summary>
        public PlanarImage(PsdRect bounds, PsdRect logical) : this(bounds, logical, true)
        {
        }

        private PlanarImage(PsdRect bounds, PsdRect? logical, bool hasLogical)
        {
            Bounds = bounds;
            _logical = hasLogical ? logical : (PsdRect?)null;
            var count = PixelCount(bounds);
            R = new float[count];
            G = new float[count];
            B = new float[count];
            A = new float[count];
        }

        /// <summary>
        /// The pixel count of <paramref name="bounds"/>, or a <see cref="PsdFormatException"/>
        /// when one float plane cannot hold it (about 46000 x 46000 pixels).
        /// </summary>
        internal static int PixelCount(PsdRect bounds)
        {
            var count = (long)bounds.Width * bounds.Height;
            if (count > MaxArrayLength)
            {
                throw new PsdFormatException(string.Format("A {0}x{1} region does not fit one float plane; render large documents in bands.", bounds.Width, bounds.Height));
            }

            return (int)count;
        }

        public PsdRect Bounds { get; private set; }

        private readonly PsdRect? _logical;

        /// <summary>The bounds this image has in a whole-canvas render.</summary>
        public PsdRect Logical
        {
            get { return _logical.HasValue ? _logical.Value : Bounds; }
        }

        public int Width
        {
            get { return Bounds.Width; }
        }

        public int Height
        {
            get { return Bounds.Height; }
        }

        public float[] R { get; private set; }

        public float[] G { get; private set; }

        public float[] B { get; private set; }

        public float[] A { get; private set; }

        public int RowOffset(int documentY, int documentX)
        {
            return ((documentY - Bounds.Top) * Width) + (documentX - Bounds.Left);
        }

        public PlanarImage Clone()
        {
            var copy = new PlanarImage(Bounds, _logical, _logical.HasValue);
            R.CopyTo(copy.R, 0);
            G.CopyTo(copy.G, 0);
            B.CopyTo(copy.B, 0);
            A.CopyTo(copy.A, 0);
            return copy;
        }

        /// <summary>Fills every pixel with an opaque or translucent color.</summary>
        public void Fill(float r, float g, float b, float a)
        {
            FillPlane(R, r);
            FillPlane(G, g);
            FillPlane(B, b);
            FillPlane(A, a);
        }

        private static void FillPlane(float[] plane, float value)
        {
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = value;
            }
        }

        /// <summary>Converts the region <paramref name="rect"/> (document space) to 8-bit straight RGBA. Pixels outside this image are transparent.</summary>
        public RgbaImage ToRgba(PsdRect rect)
        {
            var output = new RgbaImage(rect.Width, rect.Height);
            var overlap = rect.Intersect(Bounds);
            if (overlap.IsEmpty)
            {
                return output;
            }

            var width = overlap.Width;
            var pixels = output.Pixels;
            Parallelism.For(overlap.Height, width, delegate(int start, int end)
            {
                for (var y = overlap.Top + start; y < overlap.Top + end; y++)
                {
                    var source = RowOffset(y, overlap.Left);
                    var target = (((y - rect.Top) * rect.Width) + (overlap.Left - rect.Left)) * 4;
                    InterleaveRow(R, source, G, source, B, source, A, source, pixels, target, width);
                }
            });

            return output;
        }

        /// <summary>
        /// Rounds four float planes to bytes and interleaves them as RGBA; fully transparent
        /// pixels stay zero. Same rounding as the original's scalar tail: <c>(v * 255) + 0.5</c>,
        /// clamped — so the bytes are bit-identical.
        /// </summary>
        internal static void InterleaveRow(float[] r, int rOffset, float[] g, int gOffset, float[] b, int bOffset, float[] a, int aOffset, byte[] rgba, int target, int width)
        {
            for (var x = 0; x < width; x++, target += 4)
            {
                var alpha = ToByte(a[aOffset + x]);
                if (alpha == 0)
                {
                    rgba[target] = 0;
                    rgba[target + 1] = 0;
                    rgba[target + 2] = 0;
                    rgba[target + 3] = 0;
                    continue;
                }

                rgba[target] = ToByte(r[rOffset + x]);
                rgba[target + 1] = ToByte(g[gOffset + x]);
                rgba[target + 2] = ToByte(b[bOffset + x]);
                rgba[target + 3] = alpha;
            }
        }

        private static byte ToByte(float value)
        {
            var v = (value * 255f) + 0.5f;
            return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
        }

        /// <summary>Converts [0,1] floats to rounded, clamped bytes (same rounding as <see cref="InterleaveRow"/>).</summary>
        public static void UnitFloatToBytes(float[] source, byte[] destination)
        {
            for (var i = 0; i < source.Length && i < destination.Length; i++)
            {
                var v = (source[i] * 255f) + 0.5f;
                destination[i] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
            }
        }
    }
}
