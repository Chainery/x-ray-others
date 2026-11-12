using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// The open ZIP streams of one band render: each ZIP-compressed channel keeps its
    /// inflater from band to band, so a document rendered top to bottom inflates every
    /// channel once. Raw and RLE channels need no state (their rows are addressable).
    /// Channels decode on different threads, but each channel's stream is used by one at a time.
    /// </summary>
    internal sealed class ChannelReaders : IDisposable
    {
        // net45 has no ReferenceEqualityComparer; a small object-identity comparer
        // keeps one reader per LayerChannel instance.
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private readonly ConcurrentDictionary<object, ZipRowReader> _readers =
            new ConcurrentDictionary<object, ZipRowReader>(ReferenceComparer.Instance);

        /// <summary>The stream of a ZIP channel, or null for other compressions.</summary>
        public ZipRowReader For(LayerChannel channel)
        {
            if (!IsZip(channel.Bytes))
            {
                return null;
            }

            ZipRowReader reader;
            return _readers.TryGetValue(channel, out reader) ? reader : _readers.GetOrAdd(channel, delegate { return new ZipRowReader(); });
        }

        public void Dispose()
        {
            foreach (var reader in _readers.Values)
            {
                reader.Dispose();
            }

            _readers.Clear();
        }

        private static bool IsZip(ByteRange payload)
        {
            if (payload.Length < 2)
            {
                return false;
            }

            var head = payload.Window(0, 2);
            var compression = (PsdCompression)((head[0] << 8) | head[1]);
            return compression == PsdCompression.Zip || compression == PsdCompression.ZipPrediction;
        }
    }
}