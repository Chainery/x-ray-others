using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace XRay.PsdFx
{
    /// <summary>
    /// Reads a stream into one exactly sized array. net45 port of the original
    /// StreamLoader: no ArrayPool/ReadAtLeast, so chunked streams allocate their
    /// chunks directly; the observable result is identical.
    /// </summary>
    internal static class StreamLoader
    {
        private const int ChunkSize = 1 << 20;

        public static byte[] ReadAll(Stream stream)
        {
            return ReadAll(stream, CancellationToken.None);
        }

        /// <summary>
        /// Reads in chunks of 1 MB, so that a cancelled token is noticed while a large
        /// stream is still being read.
        /// </summary>
        public static byte[] ReadAll(Stream stream, CancellationToken cancellationToken)
        {
            if (RemainingLength(stream) >= 0)
            {
                var remaining = RemainingLength(stream);
                var data = new byte[remaining];
                var total = 0;
                while (total < data.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = stream.Read(data, total, Math.Min(ChunkSize, data.Length - total));
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                if (total == data.Length)
                {
                    return data;
                }

                var trimmed = new byte[total];
                Buffer.BlockCopy(data, 0, trimmed, 0, total);
                return trimmed;
            }

            var chunks = new List<byte[]>();
            try
            {
                long totalLong = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = new byte[ChunkSize];
                    var read = 0;
                    while (read < ChunkSize)
                    {
                        var n = stream.Read(chunk, read, ChunkSize - read);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    chunks.Add(chunk);
                    totalLong += read;
                    CheckSize(totalLong);
                    if (read < ChunkSize)
                    {
                        return Concatenate(chunks, totalLong);
                    }
                }
            }
            finally
            {
                chunks.Clear();
            }
        }

        private static int RemainingLength(Stream stream)
        {
            if (!stream.CanSeek)
            {
                return -1;
            }

            var remaining = Math.Max(0, stream.Length - stream.Position);
            CheckSize(remaining);
            return (int)remaining;
        }

        private static void CheckSize(long length)
        {
            if (length > 0x7FFFFFC7L)
            {
                throw new PsdFormatException("The stream is too large to load into memory (2 GB or more); load the file from a path, which memory-maps it.");
            }
        }

        private static byte[] Concatenate(List<byte[]> chunks, long total)
        {
            var result = new byte[total];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                var count = (int)Math.Min(ChunkSize, total - offset);
                Buffer.BlockCopy(chunk, 0, result, offset, count);
                offset += count;
            }

            return result;
        }
    }
}
