using System;
using System.IO;
using System.Threading;

namespace XRay.PsdFx
{
    /// <summary>
    /// The cancellation token of the public call in progress. net45 port: the
    /// original publishes an <see cref="AsyncLocal{T}"/>, which net45 lacks; every
    /// entry point of this library is synchronous, so a [ThreadStatic] slot has the
    /// same effect for the worker threads of a parallel loop.
    /// </summary>
    internal static class Cancellation
    {
        [ThreadStatic]
        private static CancellationToken Current;

        /// <summary>The token in effect (default, which never cancels, outside a scope).</summary>
        public static CancellationToken Token
        {
            get { return Current; }
        }

        public static bool IsActive
        {
            get { return Current.CanBeCanceled; }
        }

        /// <summary>
        /// Publishes <paramref name="token"/> until the scope is disposed. A token that cannot
        /// be cancelled keeps the outer one, so a nested call without a token does not hide
        /// the caller's.
        /// </summary>
        public static Scope Use(CancellationToken token)
        {
            var previous = Current;
            if (token.CanBeCanceled)
            {
                Current = token;
            }

            return new Scope(previous);
        }

        /// <summary>Throws <see cref="OperationCanceledException"/> when the token in effect is cancelled.</summary>
        public static void Check()
        {
            if (Current.IsCancellationRequested)
            {
                Current.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Checks once every <c>mask + 1</c> calls (<paramref name="mask"/> is a power of two minus one).</summary>
        public static void Every(ref int counter, int mask)
        {
            if ((++counter & mask) == 0)
            {
                Check();
            }
        }

        /// <summary>Restores the previous token on dispose.</summary>
        public struct Scope : IDisposable
        {
            private readonly CancellationToken _previous;

            internal Scope(CancellationToken previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                Current = _previous;
            }
        }
    }
}
