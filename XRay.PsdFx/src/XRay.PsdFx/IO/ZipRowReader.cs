using System;
using System.IO;
using System.IO.Compression;

namespace XRay.PsdFx
{
    /// <summary>
    /// One zlib stream read forward in pieces: band rendering asks for consecutive row
    /// ranges of a ZIP channel, and this keeps the inflater between them. net45 port:
    /// ZLibStream does not exist, so the stream opens over the payload past the
    /// two-byte zlib header and wraps it in a <see cref="DeflateStream"/>. Asking for
    /// bytes before the current position restarts the stream. Damage keeps the bytes
    /// inflated before it; everything after reads as zeros.
    /// </summary>
    internal sealed class ZipRowReader : IDisposable
    {
        private Stream _input;
        private DeflateStream _deflate;
        private ByteRange _range;
        private long _position;
        private bool _damaged;
        private byte[] _scratch;

        /// <summary>Fills <paramref name="destination"/> with the inflated bytes starting at output offset <paramref name="offset"/>.</summary>
        public void Read(ByteRange range, long offset, byte[] destination)
        {
            if (_deflate == null || offset < _position || !SameRange(range))
            {
                Open(range);
            }

            Array.Clear(destination, 0, destination.Length);
            if (_damaged)
            {
                return;
            }

            try
            {
                if (_scratch == null)
                {
                    _scratch = new byte[1 << 16];
                }

                while (_position < offset)
                {
                    Cancellation.Check();
                    var want = (int)Math.Min(_scratch.Length, offset - _position);
                    var read = _deflate.Read(_scratch, 0, want);
                    if (read == 0)
                    {
                        _damaged = true;
                        return;
                    }

                    _position += read;
                }

                var total = 0;
                while (total < destination.Length)
                {
                    Cancellation.Check();
                    var read = _deflate.Read(destination, total, destination.Length - total);
                    if (read == 0)
                    {
                        _damaged = true;
                        break;
                    }

                    total += read;
                }

                _position += total;
            }
            catch (InvalidDataException)
            {
                // Keep what inflated before the damage; later reads are zeros.
                _damaged = true;
            }
        }

        public void Dispose()
        {
            if (_deflate != null)
            {
                _deflate.Dispose();
                _deflate = null;
            }

            if (_input != null)
            {
                _input.Dispose();
                _input = null;
            }
        }

        private bool SameRange(ByteRange range)
        {
            return range.Length == _range.Length;
        }

        private void Open(ByteRange range)
        {
            Dispose();
            _range = range;
            _input = range.OpenRead();
            SkipZlibHeader(_input);
            _deflate = new DeflateStream(_input, CompressionMode.Decompress);
            _position = 0;
            _damaged = false;
        }

        /// <summary>Skips the 2-byte zlib header (CMF/FLG) of an RFC 1950 stream.</summary>
        internal static void SkipZlibHeader(Stream stream)
        {
            var header = new byte[2];
            var read = 0;
            while (read < 2)
            {
                var n = stream.Read(header, read, 2 - read);
                if (n == 0)
                {
                    throw new InvalidDataException("The zlib stream is shorter than its header.");
                }

                read += n;
            }

            // A preset dictionary (FLG bit 5) would need the Adler32 dictionary id
            // skipped as well; Photoshop never writes one.
            if ((header[1] & 0x20) != 0)
            {
                var id = new byte[4];
                stream.Read(id, 0, 4);
            }
        }
    }
}
