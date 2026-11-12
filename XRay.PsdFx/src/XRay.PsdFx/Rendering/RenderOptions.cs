using System;

namespace XRay.PsdFx
{
    /// <summary>Where PsdDocument.Render takes its pixels from.</summary>
    public enum RenderSource
    {
        /// <summary>The merged image Photoshop saved when it is real; otherwise the layer compositor.</summary>
        Auto,

        /// <summary>Always decode the saved merged image.</summary>
        MergedImage,

        /// <summary>Always composite the layer tree.</summary>
        Layers,
    }

    /// <summary>
    /// Render options. LayerVisibility can override each layer's stored visibility (a
    /// viewer's show/hide checkboxes). Setting it forces the layer compositor under
    /// RenderSource.Auto. Text re-rasterization is not part of this port: text layers
    /// always use their stored pixels.
    /// </summary>
    public sealed class RenderOptions
    {
        /// <summary>Which source Render takes. Defaults to Auto.</summary>
        public RenderSource Source { get; set; }

        /// <summary>When set, the result is flattened over this color (and fully opaque).</summary>
        public PsdColor? Background { get; set; }

        /// <summary>Per-layer visibility override, or null to use the stored flags.</summary>
        public Func<PsdLayer, bool> LayerVisibility { get; set; }

        /// <summary>
        /// Re-renders type layers from their text model. Null (the default) always uses the
        /// pixels Photoshop stored.
        /// </summary>
        public ITextLayerRasterizer TextRasterizer { get; set; }

        /// <summary>Which type layers TextRasterizer draws.</summary>
        public TextRasterMode TextRasterMode { get; set; }

        /// <summary>
        /// Renders Color Lookup (clrL) layers through their LUT or ICC profile. Off by default:
        /// no Photoshop capture with a visible Color Lookup exists in the corpus
        /// (docs/adjustments.md).
        /// </summary>
        public bool RenderColorLookup { get; set; }

        /// <summary>True when every type layer is re-rendered, so the merged image cannot be used.</summary>
        internal bool RerendersAllText
        {
            get { return TextRasterizer != null && TextRasterMode == TextRasterMode.Always; }
        }
    }
}
