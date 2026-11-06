using System;

namespace XRay.PsdFx
{
    /// <summary>Channel image compression methods from the PSD header / layer records.</summary>
    public enum PsdCompression : ushort
    {
        Raw = 0,
        Rle = 1,
        Zip = 2,
        ZipPrediction = 3,
    }

    /// <summary>What a layer record represents.</summary>
    public enum PsdLayerKind
    {
        /// <summary>An ordinary raster layer.</summary>
        Pixel,

        /// <summary>A layer group (folder). Its children are in <see cref="PsdLayer.Children"/>.</summary>
        Group,

        /// <summary>A type layer; see <see cref="PsdLayer.Text"/>.</summary>
        Text,

        /// <summary>A shape or fill layer (solid color, gradient, or pattern fill, with an optional vector mask).</summary>
        Fill,

        /// <summary>An adjustment layer (curves, levels, hue/saturation and so on).</summary>
        Adjustment,

        /// <summary>A placed or embedded smart object.</summary>
        SmartObject,
    }

    /// <summary>Section divider types from the <c>lsct</c>/<c>lsdk</c> blocks.</summary>
    public enum PsdSectionType
    {
        None = 0,
        OpenFolder = 1,
        ClosedFolder = 2,
        BoundingDivider = 3,
    }
}
