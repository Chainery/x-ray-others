using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>One point of a layer-style contour curve (ShpC): 0..255 with a corner flag.</summary>
    public struct PsdContourPoint
    {
        public PsdContourPoint(double x, double y, bool corner) : this()
        {
            X = x;
            Y = y;
            Corner = corner;
        }

        /// <summary>Horizontal position, 0..255.</summary>
        public double X { get; private set; }

        /// <summary>Vertical position, 0..255.</summary>
        public double Y { get; private set; }

        /// <summary>True when the curve has a hard corner at this point (Cnty false).</summary>
        public bool Corner { get; private set; }

        public override string ToString()
        {
            return string.Format("({0:0.#},{1:0.#}{2})", X, Y, Corner ? "!" : "");
        }
    }

    /// <summary>
    /// A layer-style contour curve (TrnS / MpgS / gloss TrnS). Ported from
    /// XRay.Psd's StyleContour: the transfer contour of shadows and glows, the
    /// contour of bevels and the gloss curve.
    /// </summary>
    public sealed class PsdContour
    {
        public static PsdContour Linear { get; } = new PsdContour("Linear", new List<PsdContourPoint>
        {
            new PsdContourPoint(0, 0, false),
            new PsdContourPoint(255, 255, false),
        });

        public static PsdContour FromDescriptor(Descriptor shape)
        {
            if (shape == null)
            {
                return Linear;
            }

            var name = shape.GetString("Nm  ");
            var points = new List<PsdContourPoint>();
            var curve = shape.GetList("Crv ");
            if (curve != null)
            {
                foreach (var item in curve)
                {
                    var point = item.Object;
                    if (point != null)
                    {
                        points.Add(new PsdContourPoint(point.GetNumber("Hrzn", 0), point.GetNumber("Vrtc", 0), !point.GetBoolean("Cnty", true)));
                    }
                }
            }

            if (points.Count == 0)
            {
                return Linear;
            }

            return new PsdContour(string.IsNullOrEmpty(name) ? "Custom" : name, points);
        }

        private PsdContour(string name, List<PsdContourPoint> points)
        {
            Name = name;
            Points = points;
        }

        /// <summary>The contour's name (Nm  key); "Linear" for the default straight line.</summary>
        public string Name { get; private set; }

        /// <summary>The curve points in file order.</summary>
        public IReadOnlyList<PsdContourPoint> Points { get; private set; }

        /// <summary>True when the curve is the straight 0-255 diagonal.</summary>
        public bool IsLinear
        {
            get
            {
                if (Points.Count == 0)
                {
                    return true;
                }

                if (Points.Count != 2)
                {
                    return false;
                }

                var first = Points[0];
                var second = Points[1];
                return Math.Abs(first.X) < 1e-4 && Math.Abs(first.Y) < 1e-4 && Math.Abs(second.X - 255) < 1e-4 && Math.Abs(second.Y - 255) < 1e-4;
            }
        }

        public override string ToString()
        {
            var builder = new StringBuilder(Name).Append(" [");
            for (var i = 0; i < Points.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Points[i]);
            }

            builder.Append(']');
            return builder.ToString();
        }
    }
}
