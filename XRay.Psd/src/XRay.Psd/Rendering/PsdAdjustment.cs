using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>The adjustment layer kinds a PSD carries in its content tagged block.</summary>
public enum PsdAdjustmentKind
{
    /// <summary><c>levl</c></summary>
    Levels,

    /// <summary><c>curv</c></summary>
    Curves,

    /// <summary><c>brit</c> with the modern <c>CgEd</c> descriptor beside it.</summary>
    BrightnessContrast,

    /// <summary><c>expA</c></summary>
    Exposure,

    /// <summary><c>blnc</c></summary>
    ColorBalance,

    /// <summary><c>hue2</c> or the Photoshop 4 <c>hue </c></summary>
    HueSaturation,

    /// <summary><c>blwh</c></summary>
    BlackAndWhite,

    /// <summary><c>mixr</c></summary>
    ChannelMixer,

    /// <summary><c>selc</c></summary>
    SelectiveColor,

    /// <summary><c>phfl</c></summary>
    PhotoFilter,

    /// <summary><c>vibA</c></summary>
    Vibrance,

    /// <summary><c>nvrt</c>, which stores no parameters.</summary>
    Invert,

    /// <summary><c>thrs</c></summary>
    Threshold,

    /// <summary><c>post</c></summary>
    Posterize,

    /// <summary><c>grdm</c>; only <see cref="PsdAdjustment.Data"/> is exposed.</summary>
    GradientMap,

    /// <summary><c>clrL</c>; only <see cref="PsdAdjustment.Data"/> is exposed.</summary>
    ColorLookup,
}

/// <summary>
/// A typed view of an adjustment layer's parameters, read from its content tagged block
/// (<see cref="PsdLayer.ContentKey"/>) on first use. Exactly one of the typed properties
/// is set for the kind (<see cref="Kind"/>); the rest stay null, and <see cref="Data"/>
/// always holds the raw payload.
/// </summary>
/// <remarks>
/// The values are what the file stores, not what the renderer applies: a grayscale or
/// other single-plane document keeps its Levels record and curve in the slot an RGB
/// document calls red (the renderer copies it to green and blue), and the payload's
/// Levels fields are 8-bit even in a 16-bit document.
/// </remarks>
public sealed class PsdAdjustment
{
    internal PsdAdjustment(PsdAdjustmentKind kind, ReadOnlyMemory<byte> data)
    {
        Kind = kind;
        Data = data;
    }

    /// <summary>Which adjustment this is.</summary>
    public PsdAdjustmentKind Kind { get; }

    /// <summary>The tagged block's raw payload, for the kinds with no typed form.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Whether any parameters parsed. False for <see cref="PsdAdjustmentKind.Invert"/>,
    /// Gradient Map, Color Lookup and a payload that did not parse.</summary>
    public bool HasParameters =>
        Levels is not null || Curves is not null || BrightnessContrast is not null || Exposure is not null ||
        ColorBalance is not null || HueSaturation is not null || BlackAndWhite is not null ||
        ChannelMixer is not null || SelectiveColor is not null || PhotoFilter is not null ||
        Vibrance is not null || ThresholdLevel is not null || PosterizeLevels is not null;

    public LevelsSettings? Levels { get; init; }

    public CurvesSettings? Curves { get; init; }

    public BrightnessContrastSettings? BrightnessContrast { get; init; }

    public ExposureSettings? Exposure { get; init; }

    public ColorBalanceSettings? ColorBalance { get; init; }

    public HueSaturationSettings? HueSaturation { get; init; }

    public BlackWhiteSettings? BlackAndWhite { get; init; }

    public ChannelMixerSettings? ChannelMixer { get; init; }

    public SelectiveColorSettings? SelectiveColor { get; init; }

    public PhotoFilterSettings? PhotoFilter { get; init; }

    public VibranceSettings? Vibrance { get; init; }

    /// <summary>The Threshold level, 1..255.</summary>
    public int? ThresholdLevel { get; init; }

    /// <summary>The Posterize levels, 2..255.</summary>
    public int? PosterizeLevels { get; init; }

    /// <summary>The layer's adjustment, or null for a layer that is not one.</summary>
    internal static PsdAdjustment? TryCreate(PsdLayer layer)
    {
        if (layer.Kind != PsdLayerKind.Adjustment || layer.ContentKey is not { } key || layer.GetTaggedBlock(key) is not { } block)
        {
            return null;
        }

        var data = block.Data;
        var span = data.Span;
        try
        {
            return key switch
            {
                "levl" => new PsdAdjustment(PsdAdjustmentKind.Levels, data) { Levels = Adjustments.ParseLevels(span) },
                "curv" => new PsdAdjustment(PsdAdjustmentKind.Curves, data) { Curves = Adjustments.ParseCurves(span) },
                "brit" => new PsdAdjustment(PsdAdjustmentKind.BrightnessContrast, data) { BrightnessContrast = Adjustments.ResolveBrightnessContrast(layer, span) },
                "expA" => new PsdAdjustment(PsdAdjustmentKind.Exposure, data) { Exposure = Adjustments.ParseExposure(span) },
                "blnc" => new PsdAdjustment(PsdAdjustmentKind.ColorBalance, data) { ColorBalance = Adjustments.ParseColorBalance(span) },
                "hue2" or "hue " => new PsdAdjustment(PsdAdjustmentKind.HueSaturation, data) { HueSaturation = Adjustments.ParseHueSaturation(span) },
                "blwh" => new PsdAdjustment(PsdAdjustmentKind.BlackAndWhite, data) { BlackAndWhite = Adjustments.ParseBlackWhite(data) },
                "mixr" => new PsdAdjustment(PsdAdjustmentKind.ChannelMixer, data) { ChannelMixer = Adjustments.ParseChannelMixer(span) },
                "selc" => new PsdAdjustment(PsdAdjustmentKind.SelectiveColor, data) { SelectiveColor = Adjustments.ParseSelectiveColor(span) },
                "phfl" => new PsdAdjustment(PsdAdjustmentKind.PhotoFilter, data) { PhotoFilter = Adjustments.ParsePhotoFilter(span) },
                "vibA" => new PsdAdjustment(PsdAdjustmentKind.Vibrance, data) { Vibrance = Adjustments.ParseVibrance(data) },
                "nvrt" => new PsdAdjustment(PsdAdjustmentKind.Invert, data),
                "thrs" => new PsdAdjustment(PsdAdjustmentKind.Threshold, data) { ThresholdLevel = Adjustments.ParseThreshold(span) },
                "post" => new PsdAdjustment(PsdAdjustmentKind.Posterize, data) { PosterizeLevels = Adjustments.ParsePosterize(span) },
                "grdm" => new PsdAdjustment(PsdAdjustmentKind.GradientMap, data),
                "clrL" => new PsdAdjustment(PsdAdjustmentKind.ColorLookup, data),
                _ => null,
            };
        }
        catch (PsdFormatException)
        {
            // A malformed payload leaves the typed property null, as the renderer's factory does.
            return null;
        }
    }

    /// <summary>Photoshop's name for the kind.</summary>
    public override string ToString() => Kind switch
    {
        PsdAdjustmentKind.Levels => "Levels",
        PsdAdjustmentKind.Curves => "Curves",
        PsdAdjustmentKind.BrightnessContrast => "Brightness/Contrast",
        PsdAdjustmentKind.Exposure => "Exposure",
        PsdAdjustmentKind.ColorBalance => "Color Balance",
        PsdAdjustmentKind.HueSaturation => "Hue/Saturation",
        PsdAdjustmentKind.BlackAndWhite => "Black & White",
        PsdAdjustmentKind.ChannelMixer => "Channel Mixer",
        PsdAdjustmentKind.SelectiveColor => "Selective Color",
        PsdAdjustmentKind.PhotoFilter => "Photo Filter",
        PsdAdjustmentKind.Vibrance => "Vibrance",
        PsdAdjustmentKind.Invert => "Invert",
        PsdAdjustmentKind.Threshold => "Threshold",
        PsdAdjustmentKind.Posterize => "Posterize",
        PsdAdjustmentKind.GradientMap => "Gradient Map",
        PsdAdjustmentKind.ColorLookup => "Color Lookup",
        _ => Kind.ToString(),
    };
}
