using System;
using System.IO;

namespace XRay.PsdFx.Tests.Support
{
    /// <summary>Difference statistics between two images of equal size.</summary>
    internal struct ImageDiff
    {
        public readonly int MaxDelta;
        public readonly double MeanDelta;
        public readonly double FractionOver2;

        public ImageDiff(int maxDelta, double meanDelta, double fractionOver2)
        {
            MaxDelta = maxDelta;
            MeanDelta = meanDelta;
            FractionOver2 = fractionOver2;
        }

        public override string ToString()
        {
            return string.Format("max {0}, mean {1:F3}, >2: {2:P2}", MaxDelta, MeanDelta, FractionOver2);
        }
    }

    internal static class ImageTools
    {
        /// <summary>Reads an uncompressed 24- or 32-bit BMP (the Photoshop reference renders) into opaque RGBA.</summary>
        public static RgbaImage ReadBmp(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
            {
                throw new InvalidDataException("Not a BMP file.");
            }

            var offset = ReadInt32(data, 10);
            var width = ReadInt32(data, 18);
            var rawHeight = ReadInt32(data, 22);
            var bits = ReadUInt16(data, 28);
            var compression = ReadInt32(data, 30);
            if ((bits != 24 && bits != 32) || (compression != 0 && compression != 3))
            {
                throw new InvalidDataException(string.Format("Unsupported BMP ({0} bpp, compression {1}).", bits, compression));
            }

            var height = Math.Abs(rawHeight);
            var bottomUp = rawHeight > 0;
            var bytesPerPixel = bits / 8;
            var stride = ((width * bytesPerPixel) + 3) & ~3;
            var image = new RgbaImage(width, height);
            for (var y = 0; y < height; y++)
            {
                var row = offset + ((bottomUp ? height - 1 - y : y) * stride);
                for (var x = 0; x < width; x++)
                {
                    var p = row + (x * bytesPerPixel);
                    image.SetPixel(x, y, new PsdColor(data[p + 2], data[p + 1], data[p]));
                }
            }

            return image;
        }

        /// <summary>Per-channel RGB difference after flattening both images over white.</summary>
        public static ImageDiff Compare(RgbaImage a, RgbaImage b, PsdColor? background = null)
        {
            if (a.Width != b.Width || a.Height != b.Height)
            {
                throw new ArgumentException(string.Format("Size mismatch {0}x{1} vs {2}x{3}.", a.Width, a.Height, b.Width, b.Height));
            }

            var fa = a.Flatten(background ?? PsdColor.White).Pixels;
            var fb = b.Flatten(background ?? PsdColor.White).Pixels;
            var max = 0;
            long sum = 0;
            long over = 0;
            long samples = 0;
            for (var i = 0; i < fa.Length; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    var delta = Math.Abs(fa[i + c] - fb[i + c]);
                    max = Math.Max(max, delta);
                    sum += delta;
                    if (delta > 2)
                    {
                        over++;
                    }

                    samples++;
                }
            }

            return new ImageDiff(
                max,
                samples == 0 ? 0 : (double)sum / samples,
                samples == 0 ? 0 : (double)over / samples);
        }

        private static int ReadInt32(byte[] data, int at)
        {
            return data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);
        }

        private static int ReadUInt16(byte[] data, int at)
        {
            return data[at] | (data[at + 1] << 8);
        }
    }
}
