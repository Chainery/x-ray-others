using System;
using System.IO;
using System.IO.Compression;

namespace XRay.PsdFx
{
    /// <summary>
    /// Decompresses PSD channel image data (raw, PackBits RLE, ZIP, ZIP with
    /// prediction) into big-endian sample bytes. Damaged rows decode as zeros
    /// (or partially) rather than failing the whole file.
    /// net45 port: scalar loops replace the original's SIMD scans; the output is
    /// bit-identical, only slower.
    /// </summary>
    internal static class ChannelCodec
    {
        public static int RowBytes(int width, int depth)
        {
            switch (depth)
            {
                case 1: return (width + 7) / 8;
                case 8: return width;
                case 16: return width * 2;
                case 32: return width * 4;
                default: throw new PsdFormatException(string.Format("Unsupported bit depth {0}.", depth));
            }
        }

        /// <summary>
        /// <paramref name="length"/> as an array length, or a <see cref="PsdFormatException"/> when
        /// one array cannot hold it (a channel too large to decode in one piece; band rendering
        /// decodes such channels a band at a time).
        /// </summary>
        public static int BufferLength(long length)
        {
            if (length <= 0x7FFFFFC7L)
            {
                return (int)Math.Max(0L, length);
            }

            throw new PsdFormatException(string.Format("{0} bytes do not fit one buffer; render large documents in bands.", length));
        }

        /// <summary>
        /// Decodes a layer channel payload whose first two bytes are the compression
        /// method. Returns <c>height * RowBytes(width, depth)</c> big-endian bytes.
        /// </summary>
        public static byte[] DecodeLayerChannel(byte[] payload, int width, int height, int depth, bool large)
        {
            Cancellation.Check();
            var rowBytes = RowBytes(width, depth);
            var output = new byte[BufferLength((long)rowBytes * height)];
            if (width <= 0 || height <= 0 || payload.Length < 2)
            {
                return output;
            }

            var compression = (PsdCompression)((payload[0] << 8) | payload[1]);
            switch (compression)
            {
                case PsdCompression.Raw:
                    var rawCount = Math.Min(payload.Length - 2, output.Length);
                    Buffer.BlockCopy(payload, 2, output, 0, rawCount);
                    break;

                case PsdCompression.Rle:
                    {
                        var countSize = large ? 4 : 2;
                        var tableBytes = (long)height * countSize;
                        if (tableBytes > payload.Length - 2)
                        {
                            break;
                        }

                        DecodeRle(payload, 2 + (int)tableBytes, (int)(payload.Length - 2 - tableBytes), payload, 2, (int)tableBytes, countSize, height, rowBytes, output);
                        break;
                    }

                case PsdCompression.Zip:
                case PsdCompression.ZipPrediction:
                    Inflate(payload, 2, payload.Length - 2, output);
                    if (compression == PsdCompression.ZipPrediction)
                    {
                        UndoPrediction(output, width, height, depth);
                    }

                    break;

                default:
                    throw new PsdFormatException(string.Format("Unknown channel compression {0}.", (int)compression));
            }

            return output;
        }

        /// <summary>
        /// Decodes rows [<paramref name="first"/>, <paramref name="end"/>) of a layer channel
        /// payload (compression method first) that may be longer than one array. Every row
        /// comes out byte for byte as in <see cref="DecodeLayerChannel"/>.
        /// </summary>
        public static byte[] DecodeLayerChannelRows(ByteRange payload, int width, int height, int depth, bool large, int first, int end, ZipRowReader zip)
        {
            Cancellation.Check();
            var rowBytes = RowBytes(width, depth);
            first = Compat.Clamp(first, 0, Math.Max(0, height));
            end = Compat.Clamp(end, first, Math.Max(0, height));
            var rows = end - first;
            var output = new byte[BufferLength((long)rowBytes * rows)];
            if (width <= 0 || rows <= 0 || payload.Length < 2)
            {
                return output;
            }

            var head = payload.Window(0, 2);
            var compression = head.Length >= 2 ? (PsdCompression)((head[0] << 8) | head[1]) : PsdCompression.Raw;
            var data = payload.From(2);
            switch (compression)
            {
                case PsdCompression.Raw:
                    var window = data.Window((long)first * rowBytes, output.Length);
                    Buffer.BlockCopy(window, 0, output, 0, window.Length);
                    break;

                case PsdCompression.Rle:
                    {
                        var countSize = large ? 4 : 2;
                        var tableBytes = (long)height * countSize;
                        if (tableBytes > data.Length)
                        {
                            break;
                        }

                        DecodeRleRows(data, data.Slice(tableBytes, data.Length - tableBytes), countSize, rowBytes, first, end, output);
                        break;
                    }

                case PsdCompression.Zip:
                case PsdCompression.ZipPrediction:
                    if (zip != null)
                    {
                        zip.Read(data, (long)first * rowBytes, output);
                    }
                    else
                    {
                        InflateRange(data, (long)first * rowBytes, output);
                    }

                    if (compression == PsdCompression.ZipPrediction)
                    {
                        UndoPrediction(output, width, rows, depth);
                    }

                    break;

                default:
                    throw new PsdFormatException(string.Format("Unknown channel compression {0}.", (int)compression));
            }

            return output;
        }

        /// <summary>
        /// RLE rows [<paramref name="first"/>, <paramref name="end"/>) of one channel:
        /// <paramref name="counts"/> starts with its row-count table, the body holds its
        /// packed rows. Offsets follow <see cref="DecodeRle"/>: each count is cut to the
        /// bytes that remain.
        /// </summary>
        internal static void DecodeRleRows(ByteRange counts, ByteRange body, int countSize, int rowBytes, int first, int end, byte[] output)
        {
            var bodyLength = body.Length;
            var offsets = new long[end - first + 1];
            var offset = 0L;
            const int tableChunk = 1 << 20;
            for (var row = 0; row < end; row += tableChunk / countSize)
            {
                var chunkRows = Math.Min(tableChunk / countSize, end - row);
                var table = counts.Window((long)row * countSize, chunkRows * countSize);
                for (var i = 0; i < chunkRows; i++)
                {
                    long count = countSize == 4 ? ReadUInt32BE(table, i * 4) : ReadUInt16BE(table, i * 2);
                    if (row + i >= first)
                    {
                        offsets[row + i - first] = offset;
                    }

                    offset += Math.Min(count, Math.Max(0L, bodyLength - offset));
                }
            }

            offsets[end - first] = offset;

            var start = offsets[0];
            var span = offset - start;
            if (span <= int.MaxValue)
            {
                var window = body.Window(start, (int)span);
                var local = new int[offsets.Length];
                for (var i = 0; i < offsets.Length; i++)
                {
                    local[i] = (int)(offsets[i] - start);
                }

                if (window.Length > 0)
                {
                    UnpackRows(window, 0, window.Length, local, 0, end - first, rowBytes, output);
                }

                return;
            }

            for (var row = 0; row < end - first; row++)
            {
                var length = offsets[row + 1] - offsets[row];
                var packed = body.Window(offsets[row], (int)Math.Min(length, int.MaxValue));
                UnpackBits(packed, 0, packed.Length, output, row * rowBytes, rowBytes);
            }
        }

        /// <summary>
        /// Decodes PackBits rows. <paramref name="counts"/> holds one big-endian byte
        /// count per row (u16, or u32 in PSB files), taken from
        /// <paramref name="countsData"/>[<paramref name="countsOffset"/>..].
        /// </summary>
        public static void DecodeRle(byte[] data, int dataOffset, int dataLength, byte[] countsData, int countsOffset, int countsLength, int countSize, int rows, int rowBytes, byte[] output)
        {
            // Row offsets first (a cheap prefix sum, clamped exactly like a sequential
            // walk), so the rows themselves can unpack independently in parallel.
            var offsets = new int[rows + 1];
            var offset = 0;
            for (var row = 0; row < rows; row++)
            {
                var countsIndex = countsOffset + (row * countSize);
                var count = countSize == 4
                    ? (int)Math.Min(ReadUInt32BE(countsData, countsIndex), int.MaxValue)
                    : ReadUInt16BE(countsData, countsIndex);
                offsets[row] = offset;
                offset += Math.Min(count, Math.Max(0, dataLength - offset));
            }

            offsets[rows] = offset;
            UnpackRows(data, dataOffset, dataLength, offsets, 0, rows, rowBytes, output);
        }

        /// <summary>Unpacks rows whose packed bytes start at <paramref name="offsets"/> (one more entry than rows).</summary>
        private static void UnpackRows(byte[] data, int dataOffset, int dataLength, int[] offsets, int start, int end, int rowBytes, byte[] output)
        {
            for (var row = start; row < end; row++)
            {
                var from = dataOffset + offsets[row];
                var length = offsets[row + 1] - offsets[row];
                UnpackBits(data, from, length, output, row * rowBytes, rowBytes);
            }
        }

        /// <summary>PackBits decoder. Returns false when the input under- or overflows the row.</summary>
        public static bool UnpackBits(byte[] source, int sourceOffset, int sourceLength, byte[] destination, int destinationOffset, int destinationLength)
        {
            var s = 0;
            var d = 0;
            while (s < sourceLength && d < destinationLength)
            {
                var header = (sbyte)source[sourceOffset + s];
                s++;
                if (header >= 0)
                {
                    var literal = header + 1;
                    if (literal > sourceLength - s || literal > destinationLength - d)
                    {
                        var n = Math.Min(Math.Min(literal, sourceLength - s), destinationLength - d);
                        Buffer.BlockCopy(source, sourceOffset + s, destination, destinationOffset + d, n);
                        return false;
                    }

                    Buffer.BlockCopy(source, sourceOffset + s, destination, destinationOffset + d, literal);
                    s += literal;
                    d += literal;
                }
                else if (header != -128)
                {
                    var run = 1 - header;
                    if (s >= sourceLength)
                    {
                        return false;
                    }

                    var value = source[sourceOffset + s];
                    s++;
                    var n = Math.Min(run, destinationLength - d);
                    for (var i = 0; i < n; i++)
                    {
                        destination[destinationOffset + d + i] = value;
                    }

                    d += n;
                    if (n < run)
                    {
                        return false;
                    }
                }
            }

            return d == destinationLength;
        }

        /// <summary>
        /// Inflates the zlib payload source[start..start+length] into destination.
        /// The zlib wrapper (CMF/FLG) is skipped; a DeflateStream inflates the rest.
        /// Damage keeps the rows that inflated before it.
        /// </summary>
        public static void Inflate(byte[] source, int start, int length, byte[] destination)
        {
            if (length < 2)
            {
                return;
            }

            using (var input = new MemoryStream(source, start + 2, length - 2, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var total = 0;
                try
                {
                    while (total < destination.Length)
                    {
                        Cancellation.Check();
                        var read = deflate.Read(destination, total, destination.Length - total);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                    }
                }
                catch (InvalidDataException)
                {
                    // Keep the rows that inflated before the damage.
                }
            }
        }

        /// <summary>
        /// Inflates a zlib stream held in a range of any length, skipping its first
        /// <paramref name="skip"/> output bytes and filling <paramref name="destination"/>
        /// with the next ones.
        /// </summary>
        public static void InflateRange(ByteRange source, long skip, byte[] destination)
        {
            if (source.IsContiguous && skip == 0)
            {
                var data = source.Contiguous;
                Inflate(data, 0, data.Length, destination);
                return;
            }

            using (var reader = new ZipRowReader())
            {
                reader.Read(source, skip, destination);
            }
        }

        /// <summary>Reverses the ZIP-with-prediction row delta filter in place. Rows are independent and run in parallel.</summary>
        public static void UndoPrediction(byte[] data, int width, int height, int depth)
        {
            if ((depth != 8 && depth != 16 && depth != 32) || width <= 0 || height <= 0)
            {
                return;
            }

            var rowBytes = RowBytes(width, depth);
            Parallelism.For(height, rowBytes, delegate(int start, int end)
            {
                var planes = depth == 32 ? new byte[rowBytes] : null;
                for (var y = start; y < end; y++)
                {
                    var rowOffset = y * rowBytes;
                    switch (depth)
                    {
                        case 8:
                            PrefixSumBytes(data, rowOffset, rowBytes);
                            break;

                        case 16:
                            PrefixSumBigEndianWords(data, rowOffset, rowBytes);
                            break;

                        default:
                            // 32-bit rows are delta-coded bytewise after splitting each float
                            // into four byte planes (all high bytes first, then the next...).
                            Buffer.BlockCopy(data, rowOffset, planes, 0, rowBytes);
                            PrefixSumBytes(planes, 0, rowBytes);
                            for (var x = 0; x < width; x++)
                            {
                                data[rowOffset + (x * 4) + 0] = planes[x];
                                data[rowOffset + (x * 4) + 1] = planes[width + x];
                                data[rowOffset + (x * 4) + 2] = planes[(2 * width) + x];
                                data[rowOffset + (x * 4) + 3] = planes[(3 * width) + x];
                            }

                            break;
                    }
                }
            });
        }

        /// <summary>In-place wrapping prefix sum of bytes.</summary>
        internal static void PrefixSumBytes(byte[] row, int offset, int count)
        {
            byte acc = 0;
            for (var x = 0; x < count; x++)
            {
                acc += row[offset + x];
                row[offset + x] = acc;
            }
        }

        /// <summary>In-place wrapping prefix sum of big-endian 16-bit words.</summary>
        internal static void PrefixSumBigEndianWords(byte[] row, int offset, int rowBytes)
        {
            var words = rowBytes / 2;
            ushort acc = 0;
            for (var x = 0; x < words; x++)
            {
                acc += ReadUInt16BE(row, offset + (x * 2));
                row[offset + (x * 2)] = (byte)(acc >> 8);
                row[offset + (x * 2) + 1] = (byte)acc;
            }
        }

        /// <summary>
        /// Converts big-endian samples to normalized floats. 8- and 16-bit samples map
        /// to [0, 1]; 32-bit samples are copied as-is (linear light, possibly above 1);
        /// 1-bit samples map set bits to 0 (black) and clear bits to 1.
        /// </summary>
        public static void ToFloat(byte[] samples, int width, int height, int depth, float[] destination)
        {
            ToFloatRows(samples, 0, width, height, depth, destination, 0);
        }

        private static void ToFloatRows(byte[] samples, int samplesOffset, int width, int height, int depth, float[] destination, int destinationOffset)
        {
            var pixels = width * height;
            switch (depth)
            {
                case 8:
                    BytesToUnitFloat(samples, samplesOffset, pixels, destination, destinationOffset);
                    break;

                case 16:
                    {
                        for (var i = 0; i < pixels; i++)
                        {
                            destination[destinationOffset + i] = ReadUInt16BE(samples, samplesOffset + (i * 2)) * (1f / 65535f);
                        }

                        break;
                    }

                case 32:
                    {
                        for (var i = 0; i < pixels; i++)
                        {
                            var bits = ReadUInt32BE(samples, samplesOffset + (i * 4));
                            var value = Int32BitsToSingle((int)bits);
                            // Replace NaN/inf from damaged data so they cannot poison blends.
                            destination[destinationOffset + i] = IsFinite(value) ? value : 0f;
                        }

                        break;
                    }

                case 1:
                    {
                        var rowBytes = RowBytes(width, 1);
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var bit = (samples[samplesOffset + (y * rowBytes) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                                destination[destinationOffset + (y * width) + x] = bit ? 0f : 1f;
                            }
                        }

                        break;
                    }

                default:
                    throw new PsdFormatException(string.Format("Unsupported bit depth {0}.", depth));
            }
        }

        /// <summary>Converts a byte range to [0,1] floats.</summary>
        public static void BytesToUnitFloat(byte[] source, int sourceOffset, int count, float[] destination, int destinationOffset)
        {
            for (var i = 0; i < count; i++)
            {
                destination[destinationOffset + i] = source[sourceOffset + i] * (1f / 255f);
            }
        }

        /// <summary>Converts big-endian u16 samples to [0,1] floats.</summary>
        public static void UShortsToUnitFloat(byte[] source, int sourceOffset, int count, float[] destination, int destinationOffset)
        {
            for (var i = 0; i < count; i++)
            {
                destination[destinationOffset + i] = ReadUInt16BE(source, sourceOffset + (i * 2)) * (1f / 65535f);
            }
        }

        internal static ushort ReadUInt16BE(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        internal static uint ReadUInt32BE(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        internal static float Int32BitsToSingle(int bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && !float.IsNegativeInfinity(value) && !float.IsPositiveInfinity(value);
        }
    }
}
