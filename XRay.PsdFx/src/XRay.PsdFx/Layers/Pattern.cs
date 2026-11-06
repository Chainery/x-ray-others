using System;

namespace XRay.PsdFx
{
    /// <summary>
    /// Placement of a pattern (pattern overlay effect, bevel texture). Ported from
    /// XRay.Psd's PatternPlacement. The tile pixels themselves live in the document's
    /// global Patt/Pat2/Pat3 blocks; only the placement is mapped here.
    /// </summary>
    public sealed class PsdPatternPlacement
    {
        /// <summary>The pattern's Idnt key; null when the effect has no pattern.</summary>
        public static PsdPatternPlacement FromDescriptor(Descriptor descriptor)
        {
            var pattern = descriptor.GetObject("Ptrn");
            var id = pattern != null ? pattern.GetString("Idnt") : null;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var phase = descriptor.GetObject("phase");
            return new PsdPatternPlacement
            {
                PatternId = id,
                PatternName = Descriptor.Localize(pattern.GetString("Nm  ")),
                Scale = Math.Max(0.01f, (float)(descriptor.GetNumber("Scl ", 100) / 100)),
                AngleDegrees = (float)descriptor.GetNumber("Angl", 0),
                // Two different checkboxes: Algn = "Align With Layer" (the tile is
                // anchored to the layer), lnkA = "Link With Layer" (scale/angle
                // follow the layer). Photoshop omits both when checked (default true).
                AlignWithLayer = descriptor.GetBoolean("Algn", true),
                LinkWithLayer = descriptor.GetBoolean("lnkA", true),
                PhaseX = (float)(phase != null ? phase.GetNumber("Hrzn", 0) : 0),
                PhaseY = (float)(phase != null ? phase.GetNumber("Vrtc", 0) : 0),
            };
        }

        /// <summary>The pattern id (Ptrn Idnt) used to look the tile up in the Patt blocks.</summary>
        public string PatternId { get; private set; }

        /// <summary>The pattern's display name (Ptrn Nm  ).</summary>
        public string PatternName { get; private set; }

        /// <summary>Scale (Scl  / 100; at least 0.01).</summary>
        public float Scale { get; private set; }

        /// <summary>Angle in degrees (Angl; the reference forces 0 for bevel textures).</summary>
        public float AngleDegrees { get; set; }

        /// <summary>Align With Layer (Algn); the tile is anchored to the layer's bounds.</summary>
        public bool AlignWithLayer { get; internal set; }

        /// <summary>Link With Layer (lnkA); scale and angle follow the layer.</summary>
        public bool LinkWithLayer { get; internal set; }

        /// <summary>Horizontal phase offset (phase Hrzn).</summary>
        public float PhaseX { get; private set; }

        /// <summary>Vertical phase offset (phase Vrtc).</summary>
        public float PhaseY { get; private set; }

        public override string ToString()
        {
            var name = string.IsNullOrEmpty(PatternName) ? "" : " \"" + PatternName + "\"";
            var flags = "";
            if (!LinkWithLayer)
            {
                flags += " unlinked";
            }

            if (!AlignWithLayer)
            {
                flags += " unaligned";
            }

            return string.Format("pattern {0}{1} scale {2:0.##} angle {3:0.#} phase ({4:0.#},{5:0.#}){6}",
                PatternId, name, Scale, AngleDegrees, PhaseX, PhaseY, flags);
        }
    }
}
