using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Phase 2 partial port of the original LegacyText: the shared 10-byte color
    /// record reader is needed by the duotone inks now; the full legacy text
    /// block parser arrives with the Text phase.
    /// </summary>
    internal static class LegacyText
    {
        /// <summary>
        /// Photoshop's 10-byte color: u16 space and four u16 components. 0 RGB, 1 HSB
        /// (hue over 0..65535 for 0..360 degrees), 2 CMYK stored inverted (65535 is no
        /// ink), 8 grayscale with the level on 0..10000. With <paramref name="colors"/>,
        /// CMYK converts through the document's profile like the pixels (the plain
        /// inverse-ink formula otherwise) and RGB through an RGB document's profile;
        /// HSB and gray stay as they are, as in the reference. The same record is the
        /// color of PS 5 lrFX effects.
        /// </summary>
        internal static PsdColor ReadColor(BigEndianReader reader)
        {
            return ReadColor(reader, null);
        }

        internal static PsdColor ReadColor(BigEndianReader reader, DocumentColors colors)
        {
            var space = reader.ReadUInt16();
            var c = new ushort[4];
            for (var i = 0; i < 4; i++)
            {
                c[i] = reader.ReadUInt16();
            }

            switch (space)
            {
                case 1:
                    {
                        var hue = c[0] * 360.0 / 65535.0;
                        var saturation = c[1] / 65535.0;
                        var brightness = c[2] / 65535.0;
                        var position = (hue / 60.0) % 6.0;
                        var sector = (int)Math.Floor(position);
                        var fraction = position - sector;
                        var p = brightness * (1 - saturation);
                        var q = brightness * (1 - (saturation * fraction));
                        var t = brightness * (1 - (saturation * (1 - fraction)));
                        double r, g, b;
                        switch (sector)
                        {
                            case 1:
                                r = q; g = brightness; b = p;
                                break;
                            case 2:
                                r = p; g = brightness; b = t;
                                break;
                            case 3:
                                r = p; g = q; b = brightness;
                                break;
                            case 4:
                                r = t; g = p; b = brightness;
                                break;
                            case 5:
                                r = brightness; g = p; b = q;
                                break;
                            default:
                                r = brightness; g = t; b = p;
                                break;
                        }

                        return new PsdColor(Level(r), Level(g), Level(b));
                    }

                case 2 when colors != null:
                    return colors.FromInk(1 - (c[0] / 65535.0), 1 - (c[1] / 65535.0), 1 - (c[2] / 65535.0), 1 - (c[3] / 65535.0));

                case 0 when colors != null:
                    return colors.FromRgb((c[0] + 128) / 257.0, (c[1] + 128) / 257.0, (c[2] + 128) / 257.0);

                case 2:
                    {
                        // Stored inverted: the component is 1 - ink, so RGB = component x (1 - black ink).
                        var k = c[3] / 65535.0;
                        return new PsdColor(Level(c[0] / 65535.0 * k), Level(c[1] / 65535.0 * k), Level(c[2] / 65535.0 * k));
                    }

                case 8:
                    {
                        var level = (byte)Compat.Clamp(Math.Round(c[0] * 255.0 / 10000.0), 0, 255);
                        return new PsdColor(level, level, level);
                    }

                default:
                    return new PsdColor((byte)((c[0] + 128) / 257), (byte)((c[1] + 128) / 257), (byte)((c[2] + 128) / 257));
            }
        }

        private static byte Level(double value)
        {
            return (byte)Compat.Clamp(Math.Round(value * 255), 0, 255);
        }
    }
}
