using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Profile connection space helpers. Phase 10 will port the full IccPcs
    /// (Lab conversions, 3x3 math); the sRGB transfer functions are already final
    /// because the duotone path uses them.
    /// </summary>
    internal static class IccPcs
    {
        public static double EncodeSrgb(double linear)
        {
            if (!(linear > 0))
            {
                return 0;
            }

            if (linear >= 1)
            {
                return 1;
            }

            return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
        }

        public static double DecodeSrgb(double encoded)
        {
            return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }
    }
}
