using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace XRay.PsdFx
{
    /// <summary>
    /// The bytes of a PSD/PSB file, addressed with 64-bit offsets. net45 port of the
    /// original's PsdSource: windows come back as copied byte arrays instead of
    /// zero-copy memory views (net45 cannot consume System.Memory), which costs one
    /// copy per window but keeps every offset of a multi-GB PSB addressable.
    /// </summary>
    internal abstract class PsdSource : IDisposable
    {
        public abstract long Length { get; }

        /// <summary>A copy of the bytes at [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>).</summary>
        public abstract byte[] Slice(long offset, int length);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    /// <summary>A file already in memory.</summary>
    internal sealed class MemorySource : PsdSource
    {
        private readonly byte[] _data;

        public MemorySource(byte[] data)
        {
            _data = data;
        }

        public override long Length
        {
            get { return _data.Length; }
        }

        public override byte[] Slice(long offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(_data, (int)offset, result, 0, length);
            return result;
        }
    }

    /// <summary>
    /// A read-only memory-mapped file. Windows are copied out of the view on demand,
    /// so parsing a large document touches only the record headers and the channel
    /// data actually decoded.
    /// </summary>
    internal sealed class MappedFileSource : PsdSource
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private int _disposed;

        private MappedFileSource(MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
        {
            _file = file;
            _view = view;
            Length = length;
        }

        public override long Length { get; }

        public static PsdSource Open(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
            {
                var length = stream.Length;
                if (length == 0)
                {
                    // An empty file cannot be mapped; the parser rejects it with a format error.
                    return new MemorySource(new byte[0]);
                }

                // The security-aware overload is .NET Framework only; netstandard2.0 has the
                // six-argument one instead. Same effect: a read-only mapping over the stream.
#if NETSTANDARD2_0
                var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
#else
                var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, null, HandleInheritability.None, false);
#endif
                try
                {
                    var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    return new MappedFileSource(file, view, length);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
        }

        public override byte[] Slice(long offset, int length)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            {
                throw new ObjectDisposedException("PsdSource");
            }

            if (offset < 0 || length < 0 || offset > Length - length)
            {
                throw new ArgumentOutOfRangeException("offset");
            }

            var result = new byte[length];
            _view.ReadArray(offset, result, 0, length);
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (disposing)
            {
                _view.Dispose();
                _file.Dispose();
            }
        }
    }
}
