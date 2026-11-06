using System;
using System.IO;

namespace XRay.PsdFx
{
    /// <summary>
    /// A run of file bytes addressed with 64-bit offsets: either one in-memory array,
    /// or a range of a <see cref="PsdSource"/> that may be longer than one array (a
    /// layer channel or merged image of a PSB past 2 GB). Readers take windows of it;
    /// windows are copies (net45 has no System.Memory).
    /// </summary>
    internal struct ByteRange
    {
        private byte[] _memory;
        private PsdSource _source;
        private long _offset;

        public ByteRange(byte[] memory)
        {
            _memory = memory;
            _source = null;
            _offset = 0;
            Length = memory.Length;
        }

        public ByteRange(PsdSource source, long offset, long length)
        {
            _memory = null;
            _source = source;
            _offset = offset;
            Length = length;
        }

        public long Length { get; private set; }

        /// <summary>Whether the range is one in-memory array (<see cref="Contiguous"/> holds all of it).</summary>
        public bool IsContiguous
        {
            get { return _source == null; }
        }

        /// <summary>The bytes as one array; only for contiguous ranges.</summary>
        public byte[] Contiguous
        {
            get
            {
                if (_source != null)
                {
                    throw new InvalidOperationException("The range is not contiguous.");
                }

                return _memory;
            }
        }

        /// <summary>
        /// A copy of the bytes at [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>),
        /// cut to the end of the range (an offset past the end gives an empty window).
        /// </summary>
        public byte[] Window(long offset, int length)
        {
            if (offset < 0 || length <= 0 || offset >= Length)
            {
                return new byte[0];
            }

            var count = (int)Math.Min(length, Length - offset);
            return _source == null ? WindowOf(_memory, (int)(_offset + offset), count) : _source.Slice(_offset + offset, count);
        }

        private static byte[] WindowOf(byte[] data, int start, int count)
        {
            if (start < 0 || start >= data.Length || count <= 0)
            {
                return new byte[0];
            }

            count = (int)Math.Min(count, data.Length - start);
            var result = new byte[count];
            Buffer.BlockCopy(data, start, result, 0, count);
            return result;
        }

        /// <summary>A sub-range from <paramref name="offset"/> to the end.</summary>
        public ByteRange From(long offset)
        {
            offset = Compat.Clamp(offset, 0, Length);
            return _source == null ? new ByteRange(SuffixOf(_memory, (int)(_offset + offset))) : new ByteRange(_source, _offset + offset, Length - offset);
        }

        private static byte[] SuffixOf(byte[] data, int start)
        {
            if (start <= 0)
            {
                return data;
            }

            if (start >= data.Length)
            {
                return new byte[0];
            }

            var result = new byte[data.Length - start];
            Buffer.BlockCopy(data, start, result, 0, result.Length);
            return result;
        }

        /// <summary>A sub-range of at most <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
        public ByteRange Slice(long offset, long length)
        {
            offset = Compat.Clamp(offset, 0, Length);
            length = Compat.Clamp(length, 0, Length - offset);
            return _source == null
                ? new ByteRange(WindowOf(_memory, (int)(_offset + offset), (int)length))
                : new ByteRange(_source, _offset + offset, length);
        }

        /// <summary>A read-only stream over the range, reading it window by window.</summary>
        public Stream OpenRead()
        {
            return new RangeStream(this);
        }

        /// <summary>Forward-only reads in windows of at most 1 GiB.</summary>
        private sealed class RangeStream : Stream
        {
            private const int Chunk = 1 << 30;

            private readonly ByteRange _range;
            private long _position;

            public RangeStream(ByteRange range)
            {
                _range = range;
            }

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override long Length
            {
                get { return _range.Length; }
            }

            public override long Position
            {
                get { return _position; }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var window = _range.Window(_position, Math.Min(count, Chunk));
                Buffer.BlockCopy(window, 0, buffer, offset, window.Length);
                _position += window.Length;
                return window.Length;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
