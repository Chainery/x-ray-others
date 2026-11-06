using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// net45 / C# 7.3 compatibility shims for the ported XRay.Psd renderer code.
    /// MathF does not exist on net45 and System.Memory is not consumable, so the
    /// hot paths run on plain float arrays with these helpers.
    /// </summary>
    internal static class Compat
    {
        /// <summary>Clamps value to [min, max] (Math.Clamp is netcoreapp2.0+).</summary>
        public static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static long Clamp(long value, long min, long max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    /// <summary>
    /// Minimal System.MathF replacement. All members must behave bit-identically to
    /// System.MathF, which forwards to double Math — same as here.
    /// </summary>
    internal static class MathF
    {
        public const float PI = (float)Math.PI;

        public static float Abs(float x)
        {
            return Math.Abs(x);
        }

        public static float Min(float x, float y)
        {
            return Math.Min(x, y);
        }

        public static float Max(float x, float y)
        {
            return Math.Max(x, y);
        }

        public static float Sqrt(float x)
        {
            return (float)Math.Sqrt(x);
        }

        public static float Sin(float x)
        {
            return (float)Math.Sin(x);
        }

        public static float Cos(float x)
        {
            return (float)Math.Cos(x);
        }

        public static float Tan(float x)
        {
            return (float)Math.Tan(x);
        }

        public static float Atan2(float y, float x)
        {
            return (float)Math.Atan2(y, x);
        }

        public static float Pow(float x, float y)
        {
            return (float)Math.Pow(x, y);
        }

        public static float Exp(float x)
        {
            return (float)Math.Exp(x);
        }

        public static float Log(float x)
        {
            return (float)Math.Log(x);
        }

        public static float Floor(float x)
        {
            return (float)Math.Floor(x);
        }

        public static float Ceiling(float x)
        {
            return (float)Math.Ceiling(x);
        }

        public static float Round(float x)
        {
            return (float)Math.Round(x);
        }

        public static float Truncate(float x)
        {
            return (float)Math.Truncate(x);
        }

        public static int RoundToInt(float x)
        {
            return (int)Math.Round(x);
        }

        public static float Clamp(float value, float min, float max)
        {
            return Compat.Clamp(value, min, max);
        }
    }
}
