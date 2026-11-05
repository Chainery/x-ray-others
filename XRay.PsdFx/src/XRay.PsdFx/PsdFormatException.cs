using System;

namespace XRay.PsdFx
{
    /// <summary>Thrown when a PSD file's structure cannot be parsed.</summary>
    public class PsdFormatException : Exception
    {
        public PsdFormatException(string message) : base(message)
        {
        }

        public PsdFormatException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
