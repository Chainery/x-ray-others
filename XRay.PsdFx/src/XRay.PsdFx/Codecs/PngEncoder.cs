using System;
using System.IO;
using System.IO.Compression;

namespace XRay.PsdFx
{
    /// <summary>Compression effort for <see cref="PngEncoder"/>.</summary>
    public enum PngCompressionLevel
    {
        Fastest,
        Default,
        Smallest,
    }

    /// <summary>
    /// Dependency-free PNG writer (8-bit RGB/RGBA, non-interlaced) with per-row adaptive
    /// filtering (minimum sum of absolute differences).
    /// </summary>
    /// <remarks>
    /// Differences from the reference: net45 has no <c>ZLibStream</c>, so the zlib container
    /// is written by hand (header, one raw <see cref="DeflateStream"/>, Adler-32) and the
    /// filtered data is deflated as a single stream instead of about-1 MiB parallel segments;
    /// the pixels decode identically, the container bytes differ. SIMD is replaced by plain
    /// loops (same results).
    /// </remarks>
    public static partial class PngEncoder
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Encodes as PNG, dropping the alpha channel when the image is opaque.</summary>
        public static byte[] Encode(RgbaImage image, bool dropAlphaIfOpaque = true, PngCompressionLevel level = PngCompressionLevel.Default)
        {
            using (var output = new MemoryStream())
            {
                Encode(image, output, dropAlphaIfOpaque, level);
                return output.ToArray();
            }
        }

        /// <summary>Encodes as PNG into <paramref name="output"/>, a band of rows at a time.</summary>
        public static void Encode(RgbaImage image, Stream output, bool dropAlphaIfOpaque = true, PngCompressionLevel level = PngCompressionLevel.Default)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            var withAlpha = !(dropAlphaIfOpaque && image.IsOpaque);
            var writer = new RowWriter(output, image.Width, image.Height, withAlpha, level);
            var bandRows = Compat.Clamp((1 << 22) / Math.Max(1, image.Width), 1, Math.Max(1, image.Height));
            var rowBytes = image.Width * 4;
            for (var top = 0; top < image.Height; top += bandRows)
            {
                var rows = Math.Min(bandRows, image.Height - top);
                var pixels = new byte[rows * rowBytes];
                Array.Copy(image.Pixels, top * rowBytes, pixels, 0, pixels.Length);
                writer.Write(new RgbaImage(image.Width, rows, pixels));
            }

            writer.Finish();
        }

        /// <summary>The signature and the IHDR chunk of an 8-bit RGB or RGBA image.</summary>
        private static void WriteHeader(Stream output, int width, int height, bool withAlpha)
        {
            output.Write(Signature, 0, Signature.Length);
            var header = new byte[13];
            WriteUInt32BigEndian(header, 0, (uint)width);
            WriteUInt32BigEndian(header, 4, (uint)height);
            header[8] = 8;
            header[9] = (byte)(withAlpha ? 6 : 2);
            header[10] = 0;
            header[11] = 0;
            header[12] = 0;
            WriteChunk(output, "IHDR", header, 0, header.Length);
        }

        /// <summary>Packs row <paramref name="y"/> of the band into the filter input.</summary>
        private static void PackRow(RgbaImage image, int y, bool withAlpha, byte[] row)
        {
            var sourceOffset = y * image.Width * 4;
            if (withAlpha)
            {
                Array.Copy(image.Pixels, sourceOffset, row, 0, image.Width * 4);
                return;
            }

            for (int x = 0, s = sourceOffset, d = 0; x < image.Width; x++, s += 4, d += 3)
            {
                row[d] = image.Pixels[s];
                row[d + 1] = image.Pixels[s + 1];
                row[d + 2] = image.Pixels[s + 2];
            }
        }

        /// <summary>
        /// Picks the filter with the smallest sum of absolute differences and writes
        /// <c>filter byte + body</c> into <paramref name="best"/>.
        /// </summary>
        private static void FilterRow(byte[] row, byte[] previous, int bpp, byte[] candidate, byte[] best)
        {
            var bestScore = long.MaxValue;
            for (byte filter = 0; filter <= 4; filter++)
            {
                candidate[0] = filter;
                switch (filter)
                {
                    case 0:
                        Array.Copy(row, 0, candidate, 1, row.Length);
                        break;
                    case 1:
                        Array.Copy(row, 0, candidate, 1, bpp);
                        Subtract(row, bpp, row, 0, candidate, 1 + bpp, row.Length - bpp);
                        break;
                    case 2:
                        Subtract(row, 0, previous, 0, candidate, 1, row.Length);
                        break;
                    case 3:
                        for (var i = 0; i < bpp; i++)
                        {
                            candidate[1 + i] = (byte)(row[i] - (previous[i] >> 1));
                        }

                        AverageFilter(row, bpp, row, 0, previous, bpp, candidate, 1 + bpp, row.Length - bpp);
                        break;
                    default:
                        for (var i = 0; i < bpp; i++)
                        {
                            candidate[1 + i] = (byte)(row[i] - previous[i]);
                        }

                        PaethFilter(row, bpp, row, 0, previous, bpp, previous, 0, candidate, 1 + bpp, row.Length - bpp);
                        break;
                }

                var score = Score(candidate, 1, row.Length);
                if (score < bestScore)
                {
                    bestScore = score;
                    Array.Copy(candidate, 0, best, 0, row.Length + 1);
                }
            }
        }

        /// <summary>destination = a - b (wrapping).</summary>
        private static void Subtract(byte[] a, int aOffset, byte[] b, int bOffset, byte[] destination, int dOffset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                destination[dOffset + i] = (byte)(a[aOffset + i] - b[bOffset + i]);
            }
        }

        /// <summary>destination = x - floor((left + up) / 2).</summary>
        private static void AverageFilter(byte[] x, int xOffset, byte[] left, int leftOffset, byte[] up, int upOffset, byte[] destination, int dOffset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                destination[dOffset + i] = (byte)(x[xOffset + i] - ((left[leftOffset + i] + up[upOffset + i]) >> 1));
            }
        }

        /// <summary>destination = x - Paeth(left, up, upLeft).</summary>
        private static void PaethFilter(byte[] x, int xOffset, byte[] left, int leftOffset, byte[] up, int upOffset, byte[] upLeft, int upLeftOffset, byte[] destination, int dOffset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                destination[dOffset + i] = (byte)(x[xOffset + i] - Paeth(left[leftOffset + i], up[upOffset + i], upLeft[upLeftOffset + i]));
            }
        }

        /// <summary>The Paeth predictor; ties prefer a, then b.</summary>
        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        /// <summary>Sum of filtered bytes as signed magnitudes (the libpng heuristic).</summary>
        private static long Score(byte[] filtered, int offset, int count)
        {
            long sum = 0;
            for (var i = 0; i < count; i++)
            {
                var value = filtered[offset + i];
                sum += value < 128 ? value : 256 - value;
            }

            return sum;
        }

        /// <summary>The .NET Framework mapping of the effort levels (no SmallestSize there).</summary>
        private static CompressionLevel CompressionOf(PngCompressionLevel level)
        {
            return level == PngCompressionLevel.Fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal;
        }

        /// <summary>The second zlib header byte for a level (the first is 0x78).</summary>
        private static byte ZlibFlags(PngCompressionLevel level)
        {
            switch (level)
            {
                case PngCompressionLevel.Fastest:
                    return 0x01;
                case PngCompressionLevel.Smallest:
                    return 0xDA;
                default:
                    return 0x9C;
            }
        }

        /// <summary>Wraps the filtered rows in one zlib stream (header, raw deflate, Adler-32).</summary>
        private static byte[] Compress(byte[] filtered, int length, PngCompressionLevel level)
        {
            using (var body = new MemoryStream())
            {
                using (var deflate = new DeflateStream(body, CompressionOf(level), true))
                {
                    deflate.Write(filtered, 0, length);
                }

                var compressed = body.ToArray();
                var result = new byte[2 + compressed.Length + 4];
                result[0] = 0x78;
                result[1] = ZlibFlags(level);
                Array.Copy(compressed, 0, result, 2, compressed.Length);
                WriteUInt32BigEndian(result, result.Length - 4, Adler32.Compute(filtered, 0, length));
                return result;
            }
        }

        private static void WriteChunk(Stream output, string type, byte[] data, int offset, int length)
        {
            var buffer = new byte[8];
            WriteUInt32BigEndian(buffer, 0, (uint)length);
            for (var i = 0; i < 4; i++)
            {
                buffer[4 + i] = (byte)type[i];
            }

            output.Write(buffer, 0, 8);
            output.Write(data, offset, length);
            var crc = Crc32.Update(0xFFFFFFFFu, buffer, 4, 4);
            crc = Crc32.Update(crc, data, offset, length) ^ 0xFFFFFFFFu;
            WriteUInt32BigEndian(buffer, 0, crc);
            output.Write(buffer, 0, 4);
        }

        internal static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        internal static class Adler32
        {
            private const uint Modulus = 65521;

            // zlib's NMAX: the longest run whose sums cannot overflow 32 bits before the modulo.
            private const int Block = 5552;

            public static uint Compute(byte[] data, int offset, int count)
            {
                uint a = 1;
                uint b = 0;
                while (count > 0)
                {
                    var chunk = Math.Min(Block, count);
                    for (var i = 0; i < chunk; i++)
                    {
                        a += data[offset + i];
                        b += a;
                    }

                    a %= Modulus;
                    b %= Modulus;
                    offset += chunk;
                    count -= chunk;
                }

                return (b << 16) | a;
            }
        }

        internal static class Crc32
        {
            private static readonly uint[] Table = CreateTable();

            private static uint[] CreateTable()
            {
                var table = new uint[256];
                for (var n = 0u; n < 256; n++)
                {
                    var c = n;
                    for (var k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }

                    table[n] = c;
                }

                return table;
            }

            public static uint Update(uint crc, byte[] data, int offset, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
                }

                return crc;
            }
        }
    }
}
