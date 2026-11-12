using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Adjustment layers of CMYK, grayscale and duotone documents. Photoshop evaluates the
    /// channel-wise kinds on the document's own channels, not on RGB; the reference does the
    /// same through its InkSpace (apply_adjustment_in_ink_space and adjustment_runs_in_ink_space
    /// in .reference/src/core/adjustment_layer.cpp,
    /// .reference/docs/adjustments-calibration.md "Adjustment layers of CMYK documents").
    /// </summary>
    internal static partial class Adjustments
    {
        /// <summary>
        /// Levels, Curves, Invert, Posterize and Brightness/Contrast run on the inks, as in the
        /// reference. Threshold runs on a gray channel like them; on four inks it compares one
        /// luma (<see cref="CmykThresholdAdjustment"/>), which the reference leaves on RGB.
        /// Exposure runs on a gray channel too, but in that channel's linear light
        /// (<see cref="GrayExposureTable"/>); Photoshop offers no Exposure in CMYK, so a CMYK
        /// document's keeps the RGB math with the other kinds that mix channels.
        /// </summary>
        internal static bool RunsOnInks(string key, PsdColorMode mode)
        {
            if (mode != PsdColorMode.Cmyk && mode != PsdColorMode.Grayscale && mode != PsdColorMode.Duotone)
            {
                return false;
            }

            switch (key)
            {
                case "levl":
                case "curv":
                case "nvrt":
                case "post":
                case "brit":
                case "thrs":
                    return true;
                case "expA":
                    return mode != PsdColorMode.Cmyk;
                default:
                    return false;
            }
        }

        private static IAdjustment CreateInkAdjustment(PsdLayer layer, string key, byte[] data, IccInkSpace space)
        {
            if (key == "thrs" && space.Channels == 4)
            {
                var threshold = ParseThreshold(data);
                return threshold.HasValue ? new CmykThresholdAdjustment(space, threshold.Value) : null;
            }

            if (key == "post" && layer.Document.Depth == 16)
            {
                var posterize = ParsePosterize(data);
                return posterize.HasValue ? new InkPosterize16Adjustment(space, posterize.Value) : null;
            }

            byte[][] tables;
            if (key == "expA")
            {
                var exposure = ParseExposure(data);
                tables = exposure.HasValue ? Uniform(GrayExposureTable(exposure.Value, space)) : null;
            }
            else
            {
                tables = InkTables(layer, key, data);
            }

            if (tables == null)
            {
                return null;
            }

            var channels = space.Channels == 1 ? new[] { tables[0] } : tables;
            foreach (var table in channels)
            {
                if (!IsIdentity(table))
                {
                    return new InkAdjustment(space, channels);
                }
            }

            return null;
        }

        /// <summary>
        /// The four per-ink tables (cyan, magenta, yellow, black; a gray document uses the
        /// first). Cyan, magenta and yellow read the records an RGB document calls red, green
        /// and blue; the black ink runs the same stages with its own record
        /// (build_ink_adjustment_tables).
        /// </summary>
        internal static byte[][] InkTables(PsdLayer layer, string key, byte[] data)
        {
            switch (key)
            {
                case "nvrt":
                    {
                        var invert = new byte[256];
                        for (var v = 0; v < 256; v++)
                        {
                            invert[v] = (byte)(255 - v);
                        }

                        return new[] { invert, invert, invert, invert };
                    }

                case "thrs":
                    {
                        var level = ParseThreshold(data);
                        if (!level.HasValue)
                        {
                            return null;
                        }

                        var threshold = new byte[256];
                        for (var v = 0; v < 256; v++)
                        {
                            threshold[v] = (byte)(v >= level.Value ? 255 : 0);
                        }

                        return new[] { threshold, threshold, threshold, threshold };
                    }

                case "post":
                    {
                        var levels = ParsePosterize(data);
                        return levels.HasValue ? Uniform(PosterizeLut(levels.Value)) : null;
                    }

                case "brit":
                    {
                        var bc = ResolveBrightnessContrast(layer, data);
                        return bc.HasValue ? Uniform(BuildBrightnessContrastLut(bc.Value)) : null;
                    }

                case "expA":
                    {
                        var exposure = ParseExposure(data);
                        return exposure.HasValue ? Uniform(BuildExposureLut(exposure.Value)) : null;
                    }

                case "levl":
                    {
                        var settings = ParseLevels(data);
                        if (settings == null)
                        {
                            return null;
                        }

                        settings = ForDocument(layer, settings);
                        var luts = BuildLevelsLut(settings);
                        var black = BuildLevelsLut(settings.WithRed(settings.BlackInk)).Red;
                        return new[] { luts.Red, luts.Green, luts.Blue, black };
                    }

                case "curv":
                    {
                        var settings = ParseCurves(data);
                        if (settings == null)
                        {
                            return null;
                        }

                        settings = ForDocument(layer, settings);
                        var luts = BuildCurvesLut(settings);
                        var black = BuildCurvesLut(settings.WithRed(settings.BlackInk)).Red;
                        return new[] { luts.Red, luts.Green, luts.Blue, black };
                    }

                default:
                    return null;
            }
        }

        private static byte[][] Uniform(byte[] lut)
        {
            return new[] { lut, lut, lut, lut };
        }

        /// <summary>
        /// Exposure on a gray channel: each stored level is linearized through the document's
        /// gray conversion (the luminance its profile gives it), exposed, offset and
        /// gamma-corrected as on RGB, and mapped back to the nearest stored level.
        /// </summary>
        internal static byte[] GrayExposureTable(ExposureSettings settings, IccInkSpace space)
        {
            settings = settings.Clamped();
            var gain = Math.Pow(2.0, settings.ExposureHundredths / 100.0);
            var offset = settings.OffsetTenThousandths / 10000.0;
            var table = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                var exposed = (space.LevelLuminance(v) * gain) + offset;
                var corrected = Math.Pow(Math.Max(0.0, exposed), 100.0 / settings.GammaHundredths);
                table[v] = (byte)space.LevelFromLuminance(Compat.Clamp(corrected, 0.0, 1.0));
            }

            return table;
        }

        private static bool IsIdentity(byte[] table)
        {
            for (var v = 0; v < 256; v++)
            {
                if (table[v] != v)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Per-ink tables applied through the document's ink space.</summary>
        private sealed class InkAdjustment : IAdjustment
        {
            private readonly IccInkSpace _space;
            private readonly TableMap _map;

            public InkAdjustment(IccInkSpace space, byte[][] tables)
            {
                _space = space;
                _map = new TableMap(tables);
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                _space.Apply(r, g, b, _map);
            }
        }

        /// <summary>8-bit semantics: each channel rounds to a stored byte and goes through its table.</summary>
        private struct TableMap : IInkMap
        {
            private readonly byte[][] _tables;

            public TableMap(byte[][] tables)
            {
                _tables = tables;
            }

            public void Map(float[] channels)
            {
                for (var c = 0; c < channels.Length; c++)
                {
                    channels[c] = _tables[c][Byte(channels[c])] / 255f;
                }
            }
        }

        /// <summary>Posterize of a 16-bit document on its channels.</summary>
        private sealed class InkPosterize16Adjustment : IAdjustment
        {
            private readonly IccInkSpace _space;
            private readonly Posterize16Map _map;

            public InkPosterize16Adjustment(IccInkSpace space, int levels)
            {
                _space = space;
                _map = new Posterize16Map(levels);
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                _space.Apply(r, g, b, _map);
            }
        }

        private struct Posterize16Map : IInkMap
        {
            private readonly int _levels;

            public Posterize16Map(int levels)
            {
                _levels = levels;
            }

            public void Map(float[] channels)
            {
                for (var c = 0; c < channels.Length; c++)
                {
                    channels[c] = Posterize16Value(channels[c], _levels);
                }
            }
        }

        /// <summary>
        /// Threshold in a CMYK document: the inks become black ink alone (C = M = Y = 0,
        /// K = 100%) or no ink, by the rounded 30/59/11 luma of the naive mix
        /// R = C' K' / 255 (stored values, 255 = no ink) against the level.
        /// </summary>
        private sealed class CmykThresholdAdjustment : IAdjustment
        {
            private readonly IccInkSpace _space;
            private readonly ThresholdMap _map;

            public CmykThresholdAdjustment(IccInkSpace space, int level)
            {
                _space = space;
                _map = new ThresholdMap(level);
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                _space.Apply(r, g, b, _map);
            }
        }

        private struct ThresholdMap : IInkMap
        {
            private readonly int _level;

            public ThresholdMap(int level)
            {
                _level = level;
            }

            public void Map(float[] channels)
            {
                var k = Byte(channels[3]);
                var luma = ((30.0 * Byte(channels[0]) * k) + (59.0 * Byte(channels[1]) * k) + (11.0 * Byte(channels[2]) * k)) / (100.0 * 255.0);
                var white = Math.Floor(luma + 0.5) >= _level;
                channels[0] = 1f;
                channels[1] = 1f;
                channels[2] = 1f;
                channels[3] = white ? 1f : 0f;
            }
        }
    }
}
