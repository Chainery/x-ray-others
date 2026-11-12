using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// A raster layer mask. Pixels outside <see cref="Bounds"/> take
    /// <see cref="DefaultColor"/>. The plane itself decodes on demand from the
    /// owning layer's channel data (phase 5).
    /// </summary>
    public sealed class LayerMask
    {
        /// <summary>Channel ID holding the plane: -2 (user mask) or -3 (real user mask).</summary>
        public short ChannelId { get; set; } = -2;

        public PsdRect Bounds { get; set; }

        /// <summary>Mask value (0-255) outside the stored rectangle.</summary>
        public byte DefaultColor { get; set; }

        public bool Disabled { get; set; }

        /// <summary>The mask moves with the layer (chain icon on).</summary>
        public bool Linked { get; set; } = true;

        /// <summary>
        /// The stored plane was rendered from other data (Photoshop's baked
        /// vector-mask coverage) rather than painted by the user.
        /// </summary>
        public bool FromRendering { get; set; }

        /// <summary>Mask density 0-255 (255 is full strength).</summary>
        public byte Density { get; set; } = 255;

        /// <summary>Mask feather radius in pixels.</summary>
        public double Feather { get; set; }
    }
}
