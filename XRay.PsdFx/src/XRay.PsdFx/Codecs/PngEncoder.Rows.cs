using System;
using System.IO;

namespace XRay.PsdFx
{
    public static partial class PngEncoder
    {
        /// <summary>
        /// Writes a PNG from consecutive bands of rows. net45 has no streaming zlib, so the
        /// filtered rows of the whole image are held until <see cref="Finish"/> and written
        /// as one IDAT chunk; the decoded image is the one <see cref="Encode(RgbaImage, bool, PngCompressionLevel)"/>
        /// produces for the same pixels and alpha choice.
        /// </summary>
        internal sealed class RowWriter
        {
            private readonly Stream _output;
            private readonly int _width;
            private readonly int _height;
            private readonly bool _withAlpha;
            private readonly int _channels;
            private readonly int _stride;
            private readonly int _filteredStride;
            private readonly PngCompressionLevel _level;
            private readonly byte[] _previous;
            private byte[] _pending;
            private int _pendingLength;
            private int _rows;

            public RowWriter(Stream output, int width, int height, bool withAlpha, PngCompressionLevel level)
            {
                if (output == null)
                {
                    throw new ArgumentNullException("output");
                }

                _output = output;
                _width = width;
                _height = height;
                _withAlpha = withAlpha;
                _channels = withAlpha ? 4 : 3;
                _stride = width * _channels;
                _filteredStride = _stride + 1;
                _level = level;
                _previous = new byte[_stride];
                _pending = new byte[Math.Max(1, _filteredStride) * Math.Max(1, Math.Min(height, 1 << 12))];
                WriteHeader(output, width, height, withAlpha);
            }

            /// <summary>Appends the rows of <paramref name="band"/> (the image's full width), top to bottom.</summary>
            public void Write(RgbaImage band)
            {
                if (band == null)
                {
                    throw new ArgumentNullException("band");
                }

                if (band.Width != _width || _rows + band.Height > _height)
                {
                    throw new ArgumentException("The band does not continue the image.", "band");
                }

                var rows = band.Height;
                if (rows == 0)
                {
                    return;
                }

                Reserve(_pendingLength + ((long)rows * _filteredStride));
                var pending = _pending;
                var at = _pendingLength;
                var carried = _previous;

                // Rows filter in parallel: each strip packs the raw row above its first row.
                Parallelism.For(rows, _stride * 5L, delegate(int start, int end)
                {
                    var previous = new byte[_stride];
                    var current = new byte[_stride];
                    var candidate = new byte[_filteredStride];
                    if (start > 0)
                    {
                        PackRow(band, start - 1, _withAlpha, previous);
                    }
                    else
                    {
                        Array.Copy(carried, previous, _stride);
                    }

                    for (var y = start; y < end; y++)
                    {
                        PackRow(band, y, _withAlpha, current);
                        var destination = new byte[_filteredStride];
                        FilterRow(current, previous, _channels, candidate, destination);
                        Array.Copy(destination, 0, pending, at + (y * _filteredStride), _filteredStride);
                        var swap = previous;
                        previous = current;
                        current = swap;
                    }
                });

                PackRow(band, rows - 1, _withAlpha, _previous);
                _pendingLength += rows * _filteredStride;
                _rows += rows;
            }

            /// <summary>Compresses the filtered rows and writes the IDAT and IEND chunks.</summary>
            public void Finish()
            {
                if (_rows != _height)
                {
                    throw new InvalidOperationException(_rows + " of " + _height + " rows were written.");
                }

                var idat = Compress(_pending, _pendingLength, _level);
                WriteChunk(_output, "IDAT", idat, 0, idat.Length);
                WriteChunk(_output, "IEND", new byte[0], 0, 0);
                _pending = null;
            }

            private void Reserve(long length)
            {
                if (length <= _pending.Length)
                {
                    return;
                }

                var grown = new byte[(int)Math.Min(int.MaxValue - 64, Math.Max(length, Math.Min((long)_pending.Length * 2, int.MaxValue - 64)))];
                Array.Copy(_pending, 0, grown, 0, _pendingLength);
                _pending = grown;
            }
        }
    }
}
