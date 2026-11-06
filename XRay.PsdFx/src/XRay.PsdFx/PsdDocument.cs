using System;
using System.Collections.Generic;
using System.IO;

namespace XRay.PsdFx
{
    public enum PsdColorMode
    {
        Bitmap = 0,
        Grayscale = 1,
        Indexed = 2,
        Rgb = 3,
        Cmyk = 4,
        Multichannel = 7,
        Duotone = 8,
        Lab = 9,
    }

    /// <summary>
    /// A parsed PSD document: header values, layers and their effects. Pixel data
    /// (channels, merged image) is not decoded yet - the port is in progress, and
    /// this type grows into the full document (pixels, text, render) the way the
    /// original XRay.Psd.PsdDocument does.
    /// </summary>
    public sealed class PsdDocument
    {
        public bool IsLargeDocument { get; internal set; }

        public int Width { get; internal set; }

        public int Height { get; internal set; }

        public int Channels { get; internal set; }

        public int BitsPerChannel { get; internal set; }

        public PsdColorMode ColorMode { get; internal set; }

        /// <summary>The document's global light angle (image resource 1057; 120 when absent).</summary>
        public int GlobalLightAngle { get; internal set; }

        /// <summary>The document's global light altitude (image resource 1058; 30 when absent).</summary>
        public int GlobalLightAltitude { get; internal set; }

        public IReadOnlyList<PsdLayer> Layers { get; internal set; }

        /// <summary>Alias of <see cref="BitsPerChannel"/> used by the ported imaging code.</summary>
        internal int Depth
        {
            get { return BitsPerChannel; }
        }

        /// <summary>The raw color mode data section (duotone specifications live here).</summary>
        internal byte[] ColorModeData { get; set; }

        /// <summary>The color table of an indexed document, when parsed.</summary>
        internal PsdColor[] Palette { get; set; }

        /// <summary>The document's ICC transform (phase 10); null keeps the built-in formulas.</summary>
        internal IccSrgbTransform ColorTransform { get; set; }

        private DocumentColors _colors;

        /// <summary>Single-color conversions that follow the document's color management.</summary>
        internal DocumentColors Colors
        {
            get
            {
                if (_colors == null)
                {
                    _colors = new DocumentColors(this);
                }

                return _colors;
            }
        }

        public static PsdDocument FromFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return FromStream(stream);
            }
        }

        public static PsdDocument FromStream(Stream stream)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, read);
                }

                return FromBytes(memory.ToArray());
            }
        }

        public static PsdDocument FromBytes(byte[] data)
        {
            return PsdParser.Parse(data);
        }

        /// <summary>Convenience: every effect of every layer, in file order.</summary>
        public IEnumerable<PsdEffect> AllEffects
        {
            get
            {
                foreach (var layer in Layers)
                {
                    if (layer.Style == null)
                    {
                        continue;
                    }

                    foreach (var effect in layer.Style.Effects)
                    {
                        yield return effect;
                    }
                }
            }
        }
    }
}
