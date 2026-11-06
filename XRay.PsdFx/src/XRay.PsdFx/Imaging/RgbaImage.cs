using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// An 8-bit sRGB image with straight (non-premultiplied) alpha, stored as
    /// interleaved RGBA rows. The ToPng/ToJpeg/Save methods of the original arrive
    /// with the encoders in phase 9.
    /// </summary>
    public sealed class RgbaImage
    {
        public RgbaImage(int width, int height)
        {
            if (width < 0)
            {
                throw new ArgumentOutOfRangeException("width");
            }

            if (height < 0)
            {
                throw new ArgumentOutOfRangeException("height");
            }

            Width = width;
            Height = height;
            Pixels = new byte[checked(width * height * 4)];
        }

        public RgbaImage(int width, int height, byte[] pixels)
        {
            if (pixels == null)
            {
                throw new ArgumentNullException("pixels");
            }

            if (pixels.Length != (long)width * height * 4)
            {
                throw new ArgumentException("Pixel buffer size does not match the dimensions.", "pixels");
            }

            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Interleaved RGBA bytes, row-major, no padding.</summary>
        public byte[] Pixels { get; private set; }

        public PsdColor GetPixel(int x, int y)
        {
            var i = ((y * Width) + x) * 4;
            return new PsdColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }

        public void SetPixel(int x, int y, PsdColor color)
        {
            var i = ((y * Width) + x) * 4;
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = color.A;
        }

        /// <summary>True when every pixel is fully opaque.</summary>
        public bool IsOpaque
        {
            get
            {
                for (var i = 3; i < Pixels.Length; i += 4)
                {
                    if (Pixels[i] != 255)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Returns a copy composited over a solid background color (the result is opaque).</summary>
        public RgbaImage Flatten(PsdColor background)
        {
            var result = new RgbaImage(Width, Height);
            var source = Pixels;
            var target = result.Pixels;
            Parallelism.For(Height, Width, delegate(int first, int last)
            {
                for (var i = first * Width * 4; i < last * Width * 4; i += 4)
                {
                    int a = source[i + 3];
                    var inverse = 255 - a;
                    target[i] = (byte)(((source[i] * a) + (background.R * inverse) + 127) / 255);
                    target[i + 1] = (byte)(((source[i + 1] * a) + (background.G * inverse) + 127) / 255);
                    target[i + 2] = (byte)(((source[i + 2] * a) + (background.B * inverse) + 127) / 255);
                    target[i + 3] = 255;
                }
            });

            return result;
        }
    }
}
