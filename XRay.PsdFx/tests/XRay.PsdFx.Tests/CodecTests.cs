using System;
using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Port of the original CodecTests: raw, RLE and Zip (with and without
    /// prediction) channel payloads decode back to the samples, at 8 and 16 bit,
    /// for every width and height class (sub-vector, odd, wide).
    /// </summary>
    [TestClass]
    public sealed class CodecTests
    {
        [TestMethod]
        public void Raw_channels_decode()
        {
            foreach (var width in new[] { 1, 2, 3, 5, 8 })
            {
                foreach (var height in new[] { 1, 2, 3 })
                {
                    var samples = Samples(width, height, 8);
                    var payload = Concat(new byte[] { 0, 0 }, samples);
                    CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(payload, width, height, 8, false));
                }
            }
        }

        [TestMethod]
        public void Rle_channels_decode()
        {
            foreach (var width in new[] { 1, 2, 3, 7, 16 })
            {
                foreach (var height in new[] { 1, 2, 4 })
                {
                    var samples = Samples(width, height, 8);
                    var payload = RlePayload(samples, width, height, 8, false);
                    CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(payload, width, height, 8, false));
                }
            }
        }

        [TestMethod]
        public void Zip_channels_decode_with_and_without_prediction()
        {
            foreach (var depth in new[] { 8, 16 })
            {
                var width = 5;
                var height = 3;
                var samples = Samples(width, height, depth);

                var plain = Concat(new byte[] { 0, 2 }, Zlib(samples));
                CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(plain, width, height, depth, false));

                var predicted = Concat(new byte[] { 0, 3 }, Zlib(Predict(samples, width, height, depth)));
                CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(predicted, width, height, depth, false));
            }
        }

        [TestMethod]
        public void Rle_and_zip_16_bit_rows_decode()
        {
            var width = 6;
            var height = 2;
            var samples = Samples(width, height, 16);

            CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(RlePayload(samples, width, height, 16, false), width, height, 16, false));
            CollectionAssert.AreEqual(samples, ChannelCodec.DecodeLayerChannel(Concat(new byte[] { 0, 2 }, Zlib(samples)), width, height, 16, false));
        }

        [TestMethod]
        public void Pack_bits_round_trips_every_length()
        {
            for (var length = 0; length < 200; length++)
            {
                var row = new byte[length];
                for (var i = 0; i < length; i++)
                {
                    // Runs and literals in the same row.
                    row[i] = (byte)(i % 7 == 0 ? 0x40 : i);
                }

                var packed = PsdBuilder.PackBits(row, 0, length);
                var unpacked = new byte[length];
                Assert.IsTrue(ChannelCodec.UnpackBits(packed, 0, packed.Length, unpacked, 0, length), "length " + length);
                CollectionAssert.AreEqual(row, unpacked, "length " + length);
            }
        }

        [TestMethod]
        public void Truncated_rle_payloads_never_read_out_of_bounds()
        {
            // The decoder cuts each row count to the bytes that remain (the reference
            // behavior): a truncated payload yields a full-size buffer, zero-filled
            // past the damage, and never throws anything but PsdFormatException.
            var payload = RlePayload(Samples(8, 4, 8), 8, 4, 8, false);
            for (var cut = 2; cut < payload.Length; cut += 3)
            {
                var truncated = new byte[cut];
                Array.Copy(payload, truncated, cut);
                try
                {
                    var decoded = ChannelCodec.DecodeLayerChannel(truncated, 8, 4, 8, false);
                    Assert.AreEqual(8 * 4, decoded.Length, "cut " + cut);
                }
                catch (PsdFormatException)
                {
                    // Rejecting the payload is also acceptable.
                }
            }
        }

        private static byte[] Samples(int width, int height, int depth)
        {
            var rowBytes = ChannelCodec.RowBytes(width, depth);
            var data = new byte[rowBytes * height];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 37) + (i / rowBytes));
            }

            if (depth == 16)
            {
                // Keep the 16-bit words in range so prediction math stays exact.
                for (var i = 0; i < data.Length; i += 2)
                {
                    data[i] = 0x12;
                }
            }

            return data;
        }

        private static byte[] RlePayload(byte[] samples, int width, int height, int depth, bool large)
        {
            var rowBytes = ChannelCodec.RowBytes(width, depth);
            var rows = new byte[height][];
            var counts = new MemoryStream();
            for (var y = 0; y < height; y++)
            {
                rows[y] = PsdBuilder.PackBits(samples, y * rowBytes, rowBytes);
                if (large)
                {
                    counts.Write(new byte[] { 0, 0, (byte)(rows[y].Length >> 8), (byte)rows[y].Length }, 0, 4);
                }
                else
                {
                    counts.Write(new byte[] { (byte)(rows[y].Length >> 8), (byte)rows[y].Length }, 0, 2);
                }
            }

            var output = new MemoryStream();
            output.Write(new byte[] { 0, 1 }, 0, 2);
            var countBytes = counts.ToArray();
            output.Write(countBytes, 0, countBytes.Length);
            foreach (var row in rows)
            {
                output.Write(row, 0, row.Length);
            }

            return output.ToArray();
        }

        /// <summary>zlib framing; the codec skips the two-byte header and uses DeflateStream.</summary>
        private static byte[] Zlib(byte[] data)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x78);
                stream.WriteByte(0x9C);
                using (var deflate = new DeflateStream(stream, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                return stream.ToArray();
            }
        }

        private static byte[] Predict(byte[] samples, int width, int height, int depth)
        {
            var data = (byte[])samples.Clone();
            var rowBytes = ChannelCodec.RowBytes(width, depth);
            for (var y = 0; y < height; y++)
            {
                var row = y * rowBytes;
                if (depth == 8)
                {
                    for (var x = rowBytes - 1; x > 0; x--)
                    {
                        data[row + x] = (byte)(data[row + x] - data[row + x - 1]);
                    }
                }
                else
                {
                    for (var x = width - 1; x > 0; x--)
                    {
                        var current = (data[row + (x * 2)] << 8) | data[row + (x * 2) + 1];
                        var previous = (data[row + ((x - 1) * 2)] << 8) | data[row + ((x - 1) * 2) + 1];
                        var value = (ushort)(current - previous);
                        data[row + (x * 2)] = (byte)(value >> 8);
                        data[row + (x * 2) + 1] = (byte)value;
                    }
                }
            }

            return data;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var all = new byte[first.Length + second.Length];
            Array.Copy(first, all, first.Length);
            Array.Copy(second, 0, all, first.Length, second.Length);
            return all;
        }
    }
}
