using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>
    /// A layer-style contour curve evaluated as natural cubic runs split at corners,
    /// like .reference/src/core/style_contour.cpp. The parsed curve lives on
    /// Layers' PsdContour; this adds the byte LUT and the sampling used by effects.
    /// </summary>
    internal static class StyleContour
    {
        private struct Node
        {
            public Node(double x, double y, bool corner)
            {
                X = x;
                Y = y;
                Corner = corner;
            }

            public double X;
            public double Y;
            public bool Corner;
        }

        public static byte[] BuildLut(PsdContour contour)
        {
            var lut = new byte[256];
            var nodes = new List<Node>();
            if (contour != null && contour.Points != null)
            {
                foreach (var point in contour.Points)
                {
                    nodes.Add(new Node(Compat.Clamp(point.X, 0.0, 255.0), Compat.Clamp(point.Y, 0.0, 255.0), point.Corner));
                }
            }

            nodes = nodes.OrderBy(p => p.X).ToList();
            var unique = new List<Node>();
            foreach (var node in nodes)
            {
                if (unique.Count > 0 && Math.Abs(unique[unique.Count - 1].X - node.X) < 1e-9)
                {
                    unique[unique.Count - 1] = node;
                }
                else
                {
                    unique.Add(node);
                }
            }

            if (unique.Count < 2)
            {
                for (var i = 0; i < 256; i++)
                {
                    lut[i] = (byte)i;
                }

                return lut;
            }

            var front = (byte)Compat.Clamp((int)Math.Round(unique[0].Y, MidpointRounding.AwayFromZero), 0, 255);
            var back = (byte)Compat.Clamp((int)Math.Round(unique[unique.Count - 1].Y, MidpointRounding.AwayFromZero), 0, 255);
            for (var i = 0; i < 256; i++)
            {
                if (i <= unique[0].X)
                {
                    lut[i] = front;
                }
                else if (i >= unique[unique.Count - 1].X)
                {
                    lut[i] = back;
                }
            }

            var runStart = 0;
            for (var index = 1; index < unique.Count; index++)
            {
                if (unique[index].Corner || index + 1 == unique.Count)
                {
                    FillRun(lut, unique, runStart, index);
                    runStart = index;
                }
            }

            return lut;
        }

        private static void FillRun(byte[] lut, List<Node> nodes, int first, int last)
        {
            var count = last - first + 1;
            var second = new double[count];
            var work = new double[count];
            for (var i = 1; i + 1 < count; i++)
            {
                var previous = nodes[first + i - 1];
                var current = nodes[first + i];
                var next = nodes[first + i + 1];
                var previousSpan = current.X - previous.X;
                var nextSpan = next.X - current.X;
                var combined = previousSpan + nextSpan;
                var sigma = previousSpan / combined;
                var pivot = (sigma * second[i - 1]) + 2.0;
                second[i] = (sigma - 1.0) / pivot;
                var previousSlope = (current.Y - previous.Y) / previousSpan;
                var nextSlope = (next.Y - current.Y) / nextSpan;
                work[i] = ((6.0 * (nextSlope - previousSlope) / combined) - (sigma * work[i - 1])) / pivot;
            }

            for (var upper = count - 1; upper > 0; upper--)
            {
                var index = upper - 1;
                second[index] = (second[index] * second[upper]) + work[index];
            }

            var begin = (int)Math.Ceiling(nodes[first].X - 1e-9);
            var end = (int)Math.Floor(nodes[last].X + 1e-9);
            var up = 1;
            for (var input = Math.Max(0, begin); input <= Math.Min(255, end); input++)
            {
                while (up + 1 < count && input > nodes[first + up].X)
                {
                    up++;
                }

                var left = nodes[first + up - 1];
                var right = nodes[first + up];
                var span = right.X - left.X;
                var output = left.Y;
                if (span > 1e-9)
                {
                    var lw = (right.X - input) / span;
                    var rw = (input - left.X) / span;
                    output = (lw * left.Y) + (rw * right.Y) + ((((lw * lw * lw) - lw) * second[up - 1]) + (((rw * rw * rw) - rw) * second[up])) * span * span / 6.0;
                }

                lut[input] = (byte)Compat.Clamp((int)Math.Round(output, MidpointRounding.AwayFromZero), 0, 255);
            }
        }

        public static float Sample(byte[] lut, float t, bool antiAliased)
        {
            var clamped = Compat.Clamp(t, 0f, 1f);
            if (!antiAliased)
            {
                return lut[(int)Math.Round(clamped * 255f, MidpointRounding.AwayFromZero)] / 255f;
            }

            var scaled = clamped * 255f;
            var low = (int)scaled;
            var high = Math.Min(low + 1, 255);
            var fraction = scaled - low;
            return ((lut[low] * (1f - fraction)) + (lut[high] * fraction)) / 255f;
        }
    }
}