using System;
using System.Collections.Generic;

namespace XRay.PsdFx.Icc
{
    /// <summary>
    /// Reads the ICC v4 multiProcessElementsType (<c>mpet</c>) used by the float <c>D2Bx</c>
    /// tags, following Little CMS: curve sets of segmented curves (<c>cvst</c>, with
    /// <c>parf</c> formula and <c>samf</c> sampled segments), matrices (<c>matf</c>) and
    /// float CLUTs (<c>clut</c>); <c>bACS</c>/<c>eACS</c> are skipped. Outputs are real PCS
    /// values. Every offset and count is checked; malformed data returns null.
    /// </summary>
    internal static class IccMpet
    {
        private const int MaxElements = 64;
        private const int MaxSegments = 1024;
        private const int MaxSamples = 1 << 20;
        private const long MaxClutValues = 1L << 24;

        // Little CMS's MINUS_INF and PLUS_INF segment bounds.
        private const double Infinity = 1e22;

        public static IccLut Parse(byte[] tag)
        {
            if (tag.Length < 16)
            {
                return null;
            }

            int inputs = IccBytes.U16(tag, 8), outputs = IccBytes.U16(tag, 10);
            var count = IccBytes.U32(tag, 12);
            if (inputs < 1 || inputs > IccLut.MaxChannels || outputs < 1 || outputs > IccLut.MaxChannels || count == 0 || count > MaxElements)
            {
                return null;
            }

            var stages = new List<IccLutStage>();
            var channels = inputs;
            for (var i = 0; i < (int)count; i++)
            {
                var element = IccBytes.Slice(tag, IccBytes.U32(tag, 16 + (i * 8)), IccBytes.U32(tag, 20 + (i * 8)));
                if (element.Length < 12)
                {
                    return null;
                }

                var signature = IccBytes.Ascii(element, 0, 4);
                if (signature == "bACS" || signature == "eACS")
                {
                    continue;
                }

                int elementInputs = IccBytes.U16(element, 8), elementOutputs = IccBytes.U16(element, 10);
                if (elementInputs != channels || elementOutputs < 1 || elementOutputs > IccLut.MaxChannels)
                {
                    return null;
                }

                IccLutStage stage;
                switch (signature)
                {
                    case "cvst":
                        stage = ReadCurveSet(element, elementInputs, elementOutputs);
                        break;
                    case "matf":
                        stage = ReadMatrix(element, elementInputs, elementOutputs);
                        break;
                    case "clut":
                        stage = ReadClut(element, elementInputs, elementOutputs);
                        break;
                    default:
                        stage = null;
                        break;
                }

                if (stage == null)
                {
                    return null;
                }

                stages.Add(stage);
                channels = elementOutputs;
            }

            return channels == outputs
                ? IccLut.FromStages(inputs, outputs, IccPcsEncoding.Float, stages.ToArray(), "mpet")
                : null;
        }

        private static SegmentedCurveStage ReadCurveSet(byte[] element, int inputs, int outputs)
        {
            if (inputs != outputs || element.Length < 12 + (inputs * 8))
            {
                return null;
            }

            var curves = new SegmentedCurve[inputs];
            for (var i = 0; i < inputs; i++)
            {
                var curve = SegmentedCurve.Read(IccBytes.Slice(element, IccBytes.U32(element, 12 + (i * 8)), IccBytes.U32(element, 16 + (i * 8))));
                if (curve == null)
                {
                    return null;
                }

                curves[i] = curve;
            }

            return new SegmentedCurveStage(curves);
        }

        private static FloatMatrixStage ReadMatrix(byte[] element, int inputs, int outputs)
        {
            var values = (inputs * outputs) + outputs;
            if (element.Length < 12 + (values * 4))
            {
                return null;
            }

            var matrix = new double[inputs * outputs];
            for (var i = 0; i < matrix.Length; i++)
            {
                matrix[i] = IccBytes.Float(element, 12 + (i * 4));
            }

            var offset = new double[outputs];
            for (var i = 0; i < outputs; i++)
            {
                offset[i] = IccBytes.Float(element, 12 + ((matrix.Length + i) * 4));
            }

            foreach (var value in matrix)
            {
                if (!IsFinite(value))
                {
                    return null;
                }
            }

            foreach (var value in offset)
            {
                if (!IsFinite(value))
                {
                    return null;
                }
            }

            return new FloatMatrixStage(inputs, outputs, matrix, offset);
        }

        private static ClutStage ReadClut(byte[] element, int inputs, int outputs)
        {
            if (element.Length < 28)
            {
                return null;
            }

            var grid = new int[inputs];
            long entries = 1;
            for (var i = 0; i < inputs; i++)
            {
                grid[i] = element[12 + i];
                if (grid[i] < 2)
                {
                    return null;
                }

                entries *= grid[i];
            }

            var total = entries * outputs;
            if (total > MaxClutValues || element.Length < 28 + (total * 4))
            {
                return null;
            }

            var values = new float[total];
            for (var i = 0; i < values.Length; i++)
            {
                var sample = IccBytes.Float(element, 28 + (i * 4));
                if (!IsFinite(sample))
                {
                    return null;
                }

                values[i] = (float)sample;
            }

            return new ClutStage(grid, outputs, values);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>A <c>curf</c> segmented curve.</summary>
        private sealed class SegmentedCurve
        {
            private readonly Segment[] _segments;

            private SegmentedCurve(Segment[] segments)
            {
                _segments = segments;
            }

            public static SegmentedCurve Read(byte[] data)
            {
                if (data.Length < 12 || IccBytes.Ascii(data, 0, 4) != "curf")
                {
                    return null;
                }

                var count = IccBytes.U16(data, 8);
                if (count == 0 || count > MaxSegments || data.Length < 12 + ((count - 1) * 4))
                {
                    return null;
                }

                var bounds = new double[count + 1];
                bounds[0] = -Infinity;
                bounds[count] = Infinity;
                for (var i = 1; i < count; i++)
                {
                    bounds[i] = IccBytes.Float(data, 12 + ((i - 1) * 4));
                    if (!IsFinite(bounds[i]) || bounds[i] < bounds[i - 1])
                    {
                        return null;
                    }
                }

                var segments = new Segment[count];
                var at = 12 + ((count - 1) * 4);
                for (var i = 0; i < count; i++)
                {
                    if (data.Length < at + 12)
                    {
                        return null;
                    }

                    var signature = IccBytes.Ascii(data, at, 4);
                    if (signature == "parf")
                    {
                        var type = IccBytes.U16(data, at + 8);
                        var parameterCount = type == 0 ? 4 : (type == 1 || type == 2) ? 5 : 0;
                        if (parameterCount == 0 || data.Length < at + 12 + (parameterCount * 4))
                        {
                            return null;
                        }

                        var parameters = new double[parameterCount];
                        for (var p = 0; p < parameterCount; p++)
                        {
                            parameters[p] = IccBytes.Float(data, at + 12 + (p * 4));
                        }

                        segments[i] = new Segment(bounds[i], bounds[i + 1], type, parameters, null);
                        at += 12 + (parameterCount * 4);
                    }
                    else if (signature == "samf" && i > 0)
                    {
                        // The first point is implicit: the previous segment's value at the breakpoint.
                        var samples = IccBytes.U32(data, at + 8);
                        if (samples == 0 || samples > MaxSamples || data.Length < at + 12 + (samples * 4L))
                        {
                            return null;
                        }

                        var points = new double[samples + 1];
                        points[0] = segments[i - 1].Evaluate(bounds[i]);
                        for (var p = 1; p <= samples; p++)
                        {
                            points[p] = IccBytes.Float(data, at + 12 + ((p - 1) * 4));
                        }

                        segments[i] = new Segment(bounds[i], bounds[i + 1], -1, new double[0], points);
                        at += 12 + ((int)samples * 4);
                    }
                    else
                    {
                        return null;
                    }
                }

                return new SegmentedCurve(segments);
            }

            /// <summary>Little CMS <c>EvalSegmentedFn</c>: the last segment whose (x0, x1] holds x.</summary>
            public double Evaluate(double x)
            {
                for (var i = _segments.Length - 1; i >= 0; i--)
                {
                    var segment = _segments[i];
                    if (x > segment.Start && x <= segment.End)
                    {
                        return segment.Evaluate(x);
                    }
                }

                return -Infinity;
            }
        }

        private sealed class Segment
        {
            private readonly int _type;
            private readonly double[] _parameters;
            private readonly double[] _samples;

            public Segment(double start, double end, int type, double[] parameters, double[] samples)
            {
                Start = start;
                End = end;
                _type = type;
                _parameters = parameters;
                _samples = samples;
            }

            public double Start { get; private set; }

            public double End { get; private set; }

            public double Evaluate(double x)
            {
                var samples = _samples;
                if (samples != null)
                {
                    var position = Compat.Clamp((x - Start) / (End - Start), 0, 1) * (samples.Length - 1);
                    var index = Math.Min((int)position, samples.Length - 2);
                    var t = position - index;
                    return samples[index] + ((samples[index + 1] - samples[index]) * t);
                }

                var p = _parameters;
                switch (_type)
                {
                    case 0:
                        {
                            // Y = (a X + b)^gamma + c
                            var e = (p[1] * x) + p[2];
                            return p[0] == 1.0 ? e + p[3] : e < 0 ? p[3] : Math.Pow(e, p[0]) + p[3];
                        }

                    case 1:
                        {
                            // Y = a log10(b X^gamma + c) + d
                            var e = (p[2] * Math.Pow(x, p[0])) + p[3];
                            return e <= 0 ? p[4] : (p[1] * Math.Log10(e)) + p[4];
                        }

                    default:
                        // Y = a b^(c X + d) + e
                        return (p[0] * Math.Pow(p[1], (p[2] * x) + p[3])) + p[4];
                }
            }
        }

        private sealed class SegmentedCurveStage : IccLutStage
        {
            private readonly SegmentedCurve[] _curves;

            public SegmentedCurveStage(SegmentedCurve[] curves)
            {
                _curves = curves;
            }

            public override int Inputs
            {
                get { return _curves.Length; }
            }

            public override int Outputs
            {
                get { return _curves.Length; }
            }

            public override void Apply(double[] input, double[] output)
            {
                for (var i = 0; i < _curves.Length; i++)
                {
                    var value = _curves[i].Evaluate(input[i]);
                    output[i] = IsFinite(value) ? value : 0;
                }
            }
        }

        /// <summary>An outputs x inputs matrix plus offset on real values, without clamping.</summary>
        private sealed class FloatMatrixStage : IccLutStage
        {
            private readonly int _inputs;
            private readonly int _outputs;
            private readonly double[] _matrix;
            private readonly double[] _offset;

            public FloatMatrixStage(int inputs, int outputs, double[] matrix, double[] offset)
            {
                _inputs = inputs;
                _outputs = outputs;
                _matrix = matrix;
                _offset = offset;
            }

            public override int Inputs
            {
                get { return _inputs; }
            }

            public override int Outputs
            {
                get { return _outputs; }
            }

            public override void Apply(double[] input, double[] output)
            {
                for (var row = 0; row < _outputs; row++)
                {
                    var value = _offset[row];
                    for (var column = 0; column < _inputs; column++)
                    {
                        value += _matrix[(row * _inputs) + column] * input[column];
                    }

                    output[row] = value;
                }
            }
        }
    }
}
