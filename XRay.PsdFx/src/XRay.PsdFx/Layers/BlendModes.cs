using System;

namespace XRay.PsdFx
{
    public enum PsdBlendMode
    {
        Normal,
        PassThrough,
        Dissolve,
        Darken,
        Multiply,
        ColorBurn,
        LinearBurn,
        DarkerColor,
        Lighten,
        Screen,
        ColorDodge,
        LinearDodge,
        LighterColor,
        Overlay,
        SoftLight,
        HardLight,
        VividLight,
        LinearLight,
        PinLight,
        HardMix,
        Difference,
        Exclusion,
        Subtract,
        Divide,
        Hue,
        Saturation,
        Color,
        Luminosity,
    }

    /// <summary>Maps PSD blend-mode keys (layer records and descriptor enums) to <see cref="PsdBlendMode"/>.</summary>
    public static class BlendModeKeys
    {
        /// <summary>Maps a four-character layer-record key such as norm or "mul ".</summary>
        public static PsdBlendMode FromKey(string key)
        {
            switch (key)
            {
                case "pass": return PsdBlendMode.PassThrough;
                case "diss": case "Dslv": return PsdBlendMode.Dissolve;
                case "dark": case "Drkn": return PsdBlendMode.Darken;
                case "mul ": case "Mltp": return PsdBlendMode.Multiply;
                case "idiv": case "CBrn": return PsdBlendMode.ColorBurn;
                case "lbrn": return PsdBlendMode.LinearBurn;
                case "dkCl": return PsdBlendMode.DarkerColor;
                case "lite": case "Lghn": return PsdBlendMode.Lighten;
                case "scrn": case "Scrn": return PsdBlendMode.Screen;
                case "div ": case "CDdg": return PsdBlendMode.ColorDodge;
                case "lddg": return PsdBlendMode.LinearDodge;
                case "lgCl": return PsdBlendMode.LighterColor;
                case "over": case "Ovrl": return PsdBlendMode.Overlay;
                case "sLit": case "SftL": return PsdBlendMode.SoftLight;
                case "hLit": case "HrdL": return PsdBlendMode.HardLight;
                case "vLit": return PsdBlendMode.VividLight;
                case "lLit": return PsdBlendMode.LinearLight;
                case "pLit": return PsdBlendMode.PinLight;
                case "hMix": return PsdBlendMode.HardMix;
                case "diff": case "Dfrn": return PsdBlendMode.Difference;
                case "smud": case "Xclu": return PsdBlendMode.Exclusion;
                case "fsub": return PsdBlendMode.Subtract;
                case "fdiv": return PsdBlendMode.Divide;
                case "hue ": case "H   ": return PsdBlendMode.Hue;
                case "sat ": case "Strt": return PsdBlendMode.Saturation;
                case "colr": case "Clr ": return PsdBlendMode.Color;
                case "lum ": case "Lmns": return PsdBlendMode.Luminosity;
                default: return PsdBlendMode.Normal;
            }
        }

        /// <summary>Maps a descriptor BlnM enum value (string IDs such as linearDodge or char IDs such as Mltp).</summary>
        public static PsdBlendMode FromDescriptorEnum(string value)
        {
            switch (value)
            {
                case "passThrough": return PsdBlendMode.PassThrough;
                case "dissolve": return PsdBlendMode.Dissolve;
                case "darken": return PsdBlendMode.Darken;
                case "multiply": return PsdBlendMode.Multiply;
                case "colorBurn": return PsdBlendMode.ColorBurn;
                case "linearBurn": return PsdBlendMode.LinearBurn;
                case "darkerColor": return PsdBlendMode.DarkerColor;
                case "lighten": return PsdBlendMode.Lighten;
                case "screen": return PsdBlendMode.Screen;
                case "colorDodge": return PsdBlendMode.ColorDodge;
                case "linearDodge": return PsdBlendMode.LinearDodge;
                case "lighterColor": return PsdBlendMode.LighterColor;
                case "overlay": return PsdBlendMode.Overlay;
                case "softLight": return PsdBlendMode.SoftLight;
                case "hardLight": return PsdBlendMode.HardLight;
                case "vividLight": return PsdBlendMode.VividLight;
                case "linearLight": return PsdBlendMode.LinearLight;
                case "pinLight": return PsdBlendMode.PinLight;
                case "hardMix": return PsdBlendMode.HardMix;
                case "difference": return PsdBlendMode.Difference;
                case "exclusion": return PsdBlendMode.Exclusion;
                case "blendSubtraction": return PsdBlendMode.Subtract;
                case "blendDivide": return PsdBlendMode.Divide;
                case "hue": return PsdBlendMode.Hue;
                case "saturation": return PsdBlendMode.Saturation;
                case "color": return PsdBlendMode.Color;
                case "luminosity": return PsdBlendMode.Luminosity;
                default: return FromKey(value);
            }
        }
    }
}
