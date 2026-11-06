using System;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>
    /// Typed readers for the image resources section. Phase 3 partial port: the text
    /// helper the IPTC dataset type needs is here; the per-resource parsers
    /// (resolution, grid and guides, thumbnails, slices, layer comps, print scale,
    /// version info, ICC header, IPTC) follow in the next step of this phase.
    /// </summary>
    internal static partial class PsdImageResources
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>The value as text: UTF-8 when it decodes cleanly, else Latin-1.</summary>
        public static string DecodeIptcText(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return StrictUtf8.GetString(value);
            }
            catch (DecoderFallbackException)
            {
                return Latin1.GetString(value);
            }
        }
    }
}
