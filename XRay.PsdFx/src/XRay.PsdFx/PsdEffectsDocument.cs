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
    /// (channels, merged image) is skipped - this library reads structure and layer
    /// effects only, so it works the same on PSD and PSB files.
    /// </summary>
    public sealed class PsdEffectsDocument
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

        public static PsdEffectsDocument FromFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return FromStream(stream);
            }
        }

        public static PsdEffectsDocument FromStream(Stream stream)
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

        public static PsdEffectsDocument FromBytes(byte[] data)
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
