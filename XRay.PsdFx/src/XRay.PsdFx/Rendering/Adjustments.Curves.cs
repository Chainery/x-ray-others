using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>
    /// Curves: the ACV body reader (.reference/src/formats/acv_curves_io.cpp) and the
    /// calibrated LUT builder (build_curve_lut / build_curves_lut).
    /// </summary>
    internal static partial class Adjustments
    {
        private const int AcvMaxCurves = 19;
        private const int AcvMinPoints = 2;
        private const int AcvMaxPoints = 19;
        private const int AcvMaxBytes = 4096;

        /// <summary>
        /// Reads an ACV body: version 4 (counted curves in channel order) or version 1 (a
        /// channel bitmap, optionally followed by the indexed Crv version-4 extension).
        /// Channels 0..3 are composite, red, green and blue, 4 is a CMYK black ink.
        /// </summary>
        /// <exception cref="PsdFormatException">The body is malformed.</exception>
        internal static CurvesSettings ReadAcv(byte[] bytes)
        {
            if (bytes.Length > AcvMaxBytes)
            {
                throw new PsdFormatException("Curves data is too large.");
            }

            var reader = new BigEndianReader(bytes);
            var version = reader.ReadUInt16();
            CurvePoint[][] curves;
            if (version == 4)
            {
                var count = reader.ReadUInt16();
                if (count == 0 || count > AcvMaxCurves)
                {
                    throw new PsdFormatException("Curves data has an invalid curve count.");
                }

                curves = new CurvePoint[5][];
                for (var channel = 0; channel < count; channel++)
                {
                    SetCurve(curves, channel, ReadCurvePoints(reader));
                }
            }
            else if (version == 1)
            {
                var start = reader.Position;
                CurvePoint[][] selected = null;
                var end = 0;
                foreach (var photoshopBitmap in new[] { true, false })
                {
                    var candidate = new BigEndianReader(bytes);
                    candidate.Position = start;
                    try
                    {
                        var parsed = ReadBitmapShape(candidate, photoshopBitmap);
                        if (candidate.Remaining <= 3 && TrailingZeros(bytes, candidate.Position))
                        {
                            selected = parsed;
                            end = candidate.Position;
                            break;
                        }
                    }
                    catch (PsdFormatException)
                    {
                        // Try the other bitmap width.
                    }
                }

                curves = selected ?? throw new PsdFormatException("Curves data has malformed version-1 data.");
                reader.Position = end;
            }
            else
            {
                throw new PsdFormatException("Unsupported Curves version.");
            }

            if (reader.Remaining > 3 || !TrailingZeros(bytes, reader.Position))
            {
                throw new PsdFormatException("Curves data has trailing bytes.");
            }

            var settings = new CurvesSettings(
                curves[0] ?? CurvesSettings.IdentityPoints,
                curves[1] ?? CurvesSettings.IdentityPoints,
                curves[2] ?? CurvesSettings.IdentityPoints,
                curves[3] ?? CurvesSettings.IdentityPoints);
            settings.BlackInk = curves[4] ?? CurvesSettings.IdentityPoints;
            return settings;
        }

        private static CurvePoint[][] ReadBitmapShape(BigEndianReader reader, bool photoshopBitmap)
        {
            var bitmap = photoshopBitmap ? reader.ReadUInt32() : reader.ReadUInt16();
            var bits = photoshopBitmap ? AcvMaxCurves : 16;
            if ((bitmap & ~((1u << AcvMaxCurves) - 1u)) != 0)
            {
                throw new PsdFormatException("Curves data has an invalid curve bitmap.");
            }

            var curves = new CurvePoint[5][];
            for (var channel = 0; channel < bits; channel++)
            {
                if ((bitmap & (1u << channel)) != 0)
                {
                    SetCurve(curves, channel, ReadCurvePoints(reader));
                }
            }

            if (reader.Remaining == 0)
            {
                if (bitmap == 0)
                {
                    throw new PsdFormatException("Curves data has an empty curve bitmap.");
                }

                return curves;
            }

            if (reader.Remaining < 4 || reader.ReadSignature() != "Crv ")
            {
                throw new PsdFormatException("Curves data has invalid extra curve data.");
            }

            if (reader.ReadUInt16() != 4)
            {
                throw new PsdFormatException("Curves data has an unsupported extra curve version.");
            }

            var extra = reader.ReadUInt32();
            if (extra > AcvMaxCurves)
            {
                throw new PsdFormatException("Curves data has an invalid extra curve count.");
            }

            var seen = new bool[AcvMaxCurves];
            for (var item = 0u; item < extra; item++)
            {
                var channel = reader.ReadUInt16();
                if (channel >= AcvMaxCurves || seen[channel])
                {
                    throw new PsdFormatException("Curves data has a duplicate or invalid channel index.");
                }

                SetCurve(curves, channel, ReadCurvePoints(reader));
                seen[channel] = true;
            }

            return curves;
        }

        private static CurvePoint[] ReadCurvePoints(BigEndianReader reader)
        {
            var count = reader.ReadUInt16();
            if (count < AcvMinPoints || count > AcvMaxPoints)
            {
                throw new PsdFormatException("Curves data has an invalid point count.");
            }

            var points = new CurvePoint[count];
            var previous = -1;
            for (var i = 0; i < count; i++)
            {
                // Photoshop stores the output before the input.
                var output = reader.ReadUInt16();
                var input = reader.ReadUInt16();
                if (input > 255 || output > 255)
                {
                    throw new PsdFormatException("Curves point values must be between 0 and 255.");
                }

                if (input <= previous)
                {
                    throw new PsdFormatException("Curves inputs must be strictly increasing.");
                }

                previous = input;
                points[i] = new CurvePoint(input, output);
            }

            return points;
        }

        private static void SetCurve(CurvePoint[][] curves, int channel, CurvePoint[] points)
        {
            if (channel < curves.Length)
            {
                curves[channel] = points;
            }
        }

        private static bool TrailingZeros(byte[] data, int from)
        {
            for (var i = from; i < data.Length; i++)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// normalized_curve_control_points: clamp to 0..255, stable sort by input, the last
        /// point wins on duplicate inputs, pad a single point to two, keep at most 19.
        /// </summary>
        internal static CurvePoint[] NormalizeCurve(IReadOnlyList<CurvePoint> source)
        {
            var sorted = new List<CurvePoint>(source.Count);
            foreach (var p in source)
            {
                sorted.Add(new CurvePoint((int)Compat.Clamp(p.Input, 0, 255), (int)Compat.Clamp(p.Output, 0, 255)));
            }

            // Stable sort by input (List.Sort is not stable).
            sorted = StableSortByInput(sorted);
            var unique = new List<CurvePoint>(sorted.Count + 2);
            foreach (var point in sorted)
            {
                if (unique.Count > 0 && unique[unique.Count - 1].Input == point.Input)
                {
                    unique[unique.Count - 1] = point;
                }
                else
                {
                    unique.Add(point);
                }
            }

            if (unique.Count == 0)
            {
                return CurvesSettings.IdentityPoints;
            }

            if (unique.Count == 1)
            {
                if (unique[0].Input < 255)
                {
                    unique.Add(new CurvePoint(255, 255));
                }
                else
                {
                    unique.Insert(0, new CurvePoint(0, 0));
                }
            }

            if (unique.Count <= AcvMaxPoints)
            {
                return unique.ToArray();
            }

            // Keep the endpoints and an evenly spread deterministic subset of the interior.
            var bounded = new CurvePoint[AcvMaxPoints];
            bounded[0] = unique[0];
            var last = unique.Count - 1;
            for (var slot = 1; slot <= AcvMaxPoints - 2; slot++)
            {
                bounded[slot] = unique[((slot * last) + ((AcvMaxPoints - 1) / 2)) / (AcvMaxPoints - 1)];
            }

            bounded[AcvMaxPoints - 1] = unique[last];
            return bounded;
        }

        private static List<CurvePoint> StableSortByInput(List<CurvePoint> points)
        {
            var result = new List<CurvePoint>(points.Count);
            foreach (var point in points)
            {
                var insert = result.Count;
                while (insert > 0 && result[insert - 1].Input > point.Input)
                {
                    insert--;
                }

                result.Insert(insert, point);
            }

            return result;
        }

        /// <summary>
        /// build_curve_lut: a natural cubic spline through the points (zero second derivative
        /// at both ends), clamped to the endpoint outputs outside the first and last inputs.
        /// </summary>
        internal static byte[] BuildCurveLut(IReadOnlyList<CurvePoint> source)
        {
            var points = NormalizeCurve(source);
            var count = points.Length;
            var second = new double[count];
            var work = new double[count];
            for (var i = 1; i + 1 < count; i++)
            {
                var previousSpan = (double)(points[i].Input - points[i - 1].Input);
                var nextSpan = (double)(points[i + 1].Input - points[i].Input);
                var combined = previousSpan + nextSpan;
                var sigma = previousSpan / combined;
                var pivot = (sigma * second[i - 1]) + 2.0;
                second[i] = (sigma - 1.0) / pivot;
                var previousSlope = (points[i].Output - points[i - 1].Output) / previousSpan;
                var nextSlope = (points[i + 1].Output - points[i].Output) / nextSpan;
                work[i] = ((6.0 * (nextSlope - previousSlope) / combined) - (sigma * work[i - 1])) / pivot;
            }

            for (var upperIndex = count - 1; upperIndex > 0; upperIndex--)
            {
                var index = upperIndex - 1;
                second[index] = (second[index] * second[upperIndex]) + work[index];
            }

            var lut = new byte[256];
            var upper = 1;
            for (var input = 0; input < 256; input++)
            {
                if (input <= points[0].Input)
                {
                    lut[input] = (byte)points[0].Output;
                    continue;
                }

                if (input >= points[count - 1].Input)
                {
                    lut[input] = (byte)points[count - 1].Output;
                    continue;
                }

                while (upper + 1 < count && input > points[upper].Input)
                {
                    upper++;
                }

                if (input == points[upper - 1].Input)
                {
                    lut[input] = (byte)points[upper - 1].Output;
                    continue;
                }

                if (input == points[upper].Input)
                {
                    lut[input] = (byte)points[upper].Output;
                    continue;
                }

                var span = (double)(points[upper].Input - points[upper - 1].Input);
                var left = (points[upper].Input - (double)input) / span;
                var right = (input - (double)points[upper - 1].Input) / span;
                var output = (left * points[upper - 1].Output) + (right * points[upper].Output) +
                             ((((left * left * left) - left) * second[upper - 1]) +
                              (((right * right * right) - right) * second[upper])) * span * span / 6.0;
                lut[input] = RoundByte(output);
            }

            return lut;
        }

        /// <summary>build_curves_lut: Photoshop applies the component curve first, then Composite RGB.</summary>
        internal static ChannelLuts BuildCurvesLut(CurvesSettings curves)
        {
            var composite = BuildCurveLut(curves.Rgb);
            var red = BuildCurveLut(curves.Red);
            var green = BuildCurveLut(curves.Green);
            var blue = BuildCurveLut(curves.Blue);
            for (var i = 0; i < 256; i++)
            {
                red[i] = composite[red[i]];
                green[i] = composite[green[i]];
                blue[i] = composite[blue[i]];
            }

            return new ChannelLuts(red, green, blue);
        }
    }
}
