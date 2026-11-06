using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// An 8-bit sRGB color parsed from an effect descriptor. Alpha (255 = opaque)
    /// is additive for the rendering port: effect colors are always opaque, image
    /// pixels carry a real alpha.
    /// </summary>
    public struct PsdColor
    {
        public PsdColor(byte r, byte g, byte b) : this()
        {
            R = r;
            G = g;
            B = b;
            A = 255;
        }

        public PsdColor(byte r, byte g, byte b, byte a) : this()
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public byte R { get; private set; }

        public byte G { get; private set; }

        public byte B { get; private set; }

        /// <summary>Alpha: 0 transparent, 255 opaque. Effect descriptors never set it (opaque).</summary>
        public byte A { get; private set; }

        public static PsdColor White
        {
            get { return new PsdColor(255, 255, 255); }
        }

        public static PsdColor Black
        {
            get { return new PsdColor(0, 0, 0); }
        }

        public static PsdColor Transparent
        {
            get { return new PsdColor(0, 0, 0, 0); }
        }

        public override string ToString()
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", R, G, B);
        }

        public static byte ClampByte(double value)
        {
            var rounded = Math.Round(value);
            if (rounded < 0)
            {
                return 0;
            }

            if (rounded > 255)
            {
                return 255;
            }

            return (byte)rounded;
        }
    }

    /// <summary>The layer rectangle (top-left inclusive, bottom-right exclusive).</summary>
    public struct PsdRect
    {
        public PsdRect(int left, int top, int right, int bottom) : this()
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left { get; private set; }

        public int Top { get; private set; }

        public int Right { get; private set; }

        public int Bottom { get; private set; }

        public int Width => Right > Left ? Right - Left : 0;

        public int Height => Bottom > Top ? Bottom - Top : 0;

        public bool IsEmpty => Width == 0 || Height == 0;

        public PsdRect Intersect(PsdRect other)
        {
            var left = Math.Max(Left, other.Left);
            var top = Math.Max(Top, other.Top);
            var right = Math.Min(Right, other.Right);
            var bottom = Math.Min(Bottom, other.Bottom);
            return right <= left || bottom <= top ? default(PsdRect) : new PsdRect(left, top, right, bottom);
        }

        public PsdRect Union(PsdRect other)
        {
            if (IsEmpty)
            {
                return other;
            }

            if (other.IsEmpty)
            {
                return this;
            }

            return new PsdRect(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
        }

        public bool Contains(int x, int y)
        {
            return x >= Left && x < Right && y >= Top && y < Bottom;
        }

        public override string ToString()
        {
            return string.Format("[{0},{1} - {2},{3}]", Left, Top, Right, Bottom);
        }
    }
}
