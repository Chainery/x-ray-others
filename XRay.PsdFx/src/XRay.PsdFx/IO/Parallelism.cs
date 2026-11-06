using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace XRay.PsdFx
{
    /// <summary>
    /// Row-strip parallelism for decode, render and encode loops (net45 port of the
    /// original Parallelism, with [ThreadStatic] ambient state). Every caller splits
    /// independent items whose results do not depend on each other, so the output is
    /// bit-identical to the sequential loop whatever the degree of parallelism.
    /// </summary>
    internal static class Parallelism
    {
        /// <summary>Approximate per-strip work (item count times cost) below which a strip is not worth a task.</summary>
        public const long MinimumStripWork = 1 << 16;

        [ThreadStatic]
        private static int Degree;

        [ThreadStatic]
        private static long StripWork;

        [ThreadStatic]
        private static bool insideStrip;

        /// <summary>The degree in effect: the innermost scope's, or the processor count.</summary>
        public static int MaxDegree
        {
            get { return Degree > 0 ? Degree : Environment.ProcessorCount; }
        }

        /// <summary>
        /// Sets the degree of parallelism until the returned scope is disposed.
        /// Zero or negative means the processor count; 1 runs every loop sequentially.
        /// </summary>
        public static Scope Use(int maxDegree)
        {
            return Use(maxDegree, 0);
        }

        internal static Scope Use(int maxDegree, long minimumStripWork)
        {
            var scope = new Scope(Degree, StripWork);
            Degree = maxDegree > 0 ? maxDegree : Environment.ProcessorCount;
            if (minimumStripWork > 0)
            {
                StripWork = minimumStripWork;
            }

            return scope;
        }

        /// <summary>
        /// Runs <paramref name="body"/>(start, end) over strips covering [0, <paramref name="count"/>).
        /// <paramref name="costPerItem"/> is a rough work estimate per item (pixels in a row,
        /// for instance) used only to size strips.
        /// </summary>
        public static void For(int count, long costPerItem, Action<int, int> body)
        {
            if (count <= 0)
            {
                return;
            }

            var token = Cancellation.Token;
            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var strips = StripCount(count, costPerItem);
            var degree = MaxDegree;
            if (strips <= 1 || insideStrip || (degree <= 1 && !token.CanBeCanceled))
            {
                body(0, count);
                return;
            }

            if (degree <= 1)
            {
                // Sequential, but in strips so that a cancelled token is noticed between them.
                for (var strip = 0; strip < strips; strip++)
                {
                    token.ThrowIfCancellationRequested();
                    body((int)((long)count * strip / strips), (int)((long)count * (strip + 1) / strips));
                }

                return;
            }

            var errors = new Exception[strips];
            var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token };
            Parallel.For(0, strips, options, delegate(int strip)
            {
                var start = (int)((long)count * strip / strips);
                var end = (int)((long)count * (strip + 1) / strips);
                var outer = insideStrip;
                insideStrip = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    body(start, end);
                }
                catch (Exception ex)
                {
                    errors[strip] = ex;
                }
                finally
                {
                    insideStrip = outer;
                }
            });

            foreach (var error in errors)
            {
                if (error != null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
        }

        /// <summary>
        /// Strip count from the work size alone (not the thread count), so the partition
        /// is the same on every machine.
        /// </summary>
        internal static int StripCount(int count, long costPerItem)
        {
            var work = (long)count * Math.Max(1, costPerItem);
            var threshold = StripWork > 0 ? StripWork : MinimumStripWork;
            var strips = work / threshold;
            return Compat.Clamp((int)strips, 1, Math.Min(count, 256));
        }

        /// <summary>Restores the previous degree on dispose.</summary>
        public struct Scope : IDisposable
        {
            private readonly int _degree;
            private readonly long _stripWork;

            internal Scope(int degree, long stripWork)
            {
                _degree = degree;
                _stripWork = stripWork;
            }

            public void Dispose()
            {
                Degree = _degree;
                StripWork = _stripWork;
            }
        }
    }
}
