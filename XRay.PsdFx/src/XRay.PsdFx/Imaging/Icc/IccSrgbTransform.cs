namespace XRay.PsdFx
{
    /// <summary>
    /// Placeholder for phase 10: the profile-aware device-to-sRGB transform built
    /// from the document's embedded ICC profile (resource 1039). Until the ICC
    /// module is ported, instances are never created (documents report no
    /// transform) and every conversion takes the built-in formula path, which is
    /// the same behavior as a document without a usable profile.
    /// </summary>
    internal sealed class IccSrgbTransform
    {
        /// <summary>Device channels this transform converts: 1 gray, 3 RGB, 4 CMYK.</summary>
        public int Channels { get; private set; }

        /// <summary>Converts device planes in place; phase 10 replaces the stub body.</summary>
        public bool Apply(float[][] planes, int depth, PlanarImage image)
        {
            return false;
        }

        /// <summary>One device value through the gray path, as linear sRGB; phase 10 replaces the stub body.</summary>
        public IccSrgbTransformSample EvaluateLinear(double device)
        {
            return new IccSrgbTransformSample(device, device, device);
        }
    }

    /// <summary>One sample result of <see cref="IccSrgbTransform.EvaluateLinear"/>.</summary>
    internal struct IccSrgbTransformSample
    {
        public IccSrgbTransformSample(double r, double g, double b) : this()
        {
            R = r;
            G = g;
            B = b;
        }

        public double R { get; private set; }

        public double G { get; private set; }

        public double B { get; private set; }
    }
}
