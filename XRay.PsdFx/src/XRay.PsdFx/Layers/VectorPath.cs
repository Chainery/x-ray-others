using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>How a subpath group combines with the coverage accumulated before it.</summary>
    public enum PathCombineOperation
    {
        Xor = 0,
        Add = 1,
        Subtract = 2,
        Intersect = 3,
    }

    /// <summary>A bezier knot in document pixel coordinates.</summary>
    public struct PathKnot
    {
        public PathKnot(double inX, double inY, double x, double y, double outX, double outY, bool smooth)
        {
            InX = inX;
            InY = inY;
            X = x;
            Y = y;
            OutX = outX;
            OutY = outY;
            Smooth = smooth;
        }

        public readonly double InX;
        public readonly double InY;
        public readonly double X;
        public readonly double Y;
        public readonly double OutX;
        public readonly double OutY;
        public readonly bool Smooth;
    }

    /// <summary>One contour of a vector path.</summary>
    public sealed class PathSubpath
    {
        public bool Closed { get; set; }

        public PathCombineOperation Operation { get; set; }

        /// <summary>Subpaths sharing a group index fill together under the even-odd rule.</summary>
        public int ShapeGroup { get; set; }

        public List<PathKnot> Knots { get; } = new List<PathKnot>();
    }

    /// <summary>
    /// A vector path from a <c>vmsk</c>/<c>vsms</c> layer block or a path image
    /// resource. Coordinates are converted to document pixels on load.
    /// </summary>
    public sealed class VectorPath
    {
        private const int RecordSize = 26;

        public VectorPath()
        {
            Subpaths = new List<PathSubpath>();
        }

        public IReadOnlyList<PathSubpath> Subpaths { get; private set; }

        /// <summary>Selector 8: when set, coverage starts from "all pixels" (Photoshop's initial fill record).</summary>
        public bool InitialFillAll { get; private set; }

        /// <summary>Vector mask flag: the path is inverted.</summary>
        public bool Inverted { get; private set; }

        /// <summary>Vector mask flag: the mask is not linked to the layer.</summary>
        public bool Unlinked { get; private set; }

        /// <summary>Vector mask flag: the mask is disabled.</summary>
        public bool Disabled { get; private set; }

        /// <summary>Parses a <c>vmsk</c>/<c>vsms</c> payload: version, flags, then path records.</summary>
        internal static VectorPath ParseVectorMask(byte[] payload, int documentWidth, int documentHeight)
        {
            if (payload == null || payload.Length < 8)
            {
                return null;
            }

            var flags = U32(payload, 4);
            return ParseRecords(SliceFrom(payload, 8), documentWidth, documentHeight, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
        }

        /// <summary>Parses a raw path record stream (path image resources 2000-2997).</summary>
        internal static VectorPath ParseRecords(byte[] records, int documentWidth, int documentHeight, bool inverted = false, bool unlinked = false, bool disabled = false)
        {
            var subpaths = new List<PathSubpath>();
            PathSubpath current = null;
            var expected = 0;
            var initialFill = false;
            for (var offset = 0; offset + RecordSize <= records.Length; offset += RecordSize)
            {
                var selector = U16(records, offset);
                if (selector == 0 || selector == 3)
                {
                    if (current != null && current.Knots.Count != expected)
                    {
                        return null;
                    }

                    expected = U16(records, offset + 2);
                    int operation = U16(records, offset + 4);
                    var group = I32(records, offset + 12);
                    if (operation == 0xFFFF)
                    {
                        // A continuation contour of a compound group inherits the group's
                        // operation; CS4-era files leave it unset and fill by parity (Xor).
                        operation = current != null && current.ShapeGroup == group ? (int)current.Operation : 0;
                    }

                    if (operation > 3)
                    {
                        return null;
                    }

                    current = new PathSubpath { Closed = selector == 0, Operation = (PathCombineOperation)operation, ShapeGroup = group };
                    subpaths.Add(current);
                }
                else if (selector == 1 || selector == 2 || selector == 4 || selector == 5)
                {
                    if (current == null || current.Knots.Count >= expected)
                    {
                        return null;
                    }

                    current.Knots.Add(new PathKnot(
                        Fixed(records, offset + 6, documentWidth),
                        Fixed(records, offset + 2, documentHeight),
                        Fixed(records, offset + 14, documentWidth),
                        Fixed(records, offset + 10, documentHeight),
                        Fixed(records, offset + 22, documentWidth),
                        Fixed(records, offset + 18, documentHeight),
                        selector == 1 || selector == 4));
                }
                else if (selector == 6 || selector == 7)
                {
                    // Clipboard and fill-rule records: nothing to keep.
                }
                else if (selector == 8)
                {
                    initialFill = U16(records, offset + 2) == 1;
                }
                else
                {
                    return null;
                }
            }

            if (current != null && current.Knots.Count != expected)
            {
                return null;
            }

            return new VectorPath
            {
                Subpaths = subpaths,
                InitialFillAll = initialFill,
                Inverted = inverted,
                Unlinked = unlinked,
                Disabled = disabled,
            };
        }

        /// <summary>Bounding box of all anchors and control points, in pixels (rounded outward).</summary>
        public PsdRect Bounds
        {
            get
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var subpath in Subpaths)
                {
                    foreach (var k in subpath.Knots)
                    {
                        minX = Math.Min(minX, Math.Min(k.X, Math.Min(k.InX, k.OutX)));
                        minY = Math.Min(minY, Math.Min(k.Y, Math.Min(k.InY, k.OutY)));
                        maxX = Math.Max(maxX, Math.Max(k.X, Math.Max(k.InX, k.OutX)));
                        maxY = Math.Max(maxY, Math.Max(k.Y, Math.Max(k.InY, k.OutY)));
                    }
                }

                return minX > maxX ? default(PsdRect) : new PsdRect((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY));
            }
        }

        private static ushort U16(byte[] b, int at)
        {
            return (ushort)((b[at] << 8) | b[at + 1]);
        }

        private static uint U32(byte[] b, int at)
        {
            return (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
        }

        private static int I32(byte[] b, int at)
        {
            return unchecked((int)U32(b, at));
        }

        // Photoshop stores 16.16 fixed point relative to the document extent.
        private static double Fixed(byte[] b, int at, int extent)
        {
            return I32(b, at) / 16777216.0 * extent;
        }

        private static byte[] SliceFrom(byte[] data, int start)
        {
            var tail = new byte[data.Length - start];
            Array.Copy(data, start, tail, 0, tail.Length);
            return tail;
        }
    }
}
