using ETP.QB.FileOperationItem.Mark.PSD.XRayPSD;
using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>The kind of a layer effect in the Layer Style dialog.</summary>
    public enum PsdEffectKind
    {
        DropShadow,
        InnerShadow,
        OuterGlow,
        InnerGlow,
        BevelEmboss,
        Satin,
        ColorOverlay,
        GradientOverlay,
        PatternOverlay,
        Stroke,
    }

    public enum PsdStrokePosition
    {
        Outside,
        Inside,
        Center,
    }

    public enum PsdBevelStyle
    {
        InnerBevel,
        OuterBevel,
        Emboss,
        PillowEmboss,
        StrokeEmboss,
    }

    public enum PsdBevelTechnique
    {
        Smooth,
        ChiselHard,
        ChiselSoft,
    }

    /// <summary>What an effect paints: a color, a gradient or a pattern (stroke's PntT).</summary>
    public enum PsdFillType
    {
        Solid,
        Gradient,
        Pattern,
    }

    /// <summary>
    /// One layer effect with every property the file carries projected onto typed
    /// fields, following XRay.Psd's effect model (defaults and clamps included) and
    /// extended with Noise (Nose). Properties that do not apply to <see cref="Kind"/>
    /// are null / false. <see cref="Descriptor"/> keeps the raw parsed data, so even
    /// keys without a projected field stay reachable.
    /// </summary>
    public sealed class PsdEffect
    {
        public PsdEffectKind Kind { get; set; }

        /// <summary>The effect's own check box. Disabled effects are listed but not rendered.</summary>
        public bool Enabled { get; set; }

        /// <summary>Blend mode (the highlight mode for Bevel and Emboss).</summary>
        public PsdBlendMode BlendMode { get; set; }

        /// <summary>Opacity from 0 to 1 (the highlight opacity for Bevel and Emboss).</summary>
        public float Opacity { get; set; }

        /// <summary>
        /// Noise (杂色) from the effect's Nose key, 0 to 1. Null when the key is absent;
        /// a value of 0 means present but zero. Shadows and glows carry it.
        /// </summary>
        public float? Noise { get; set; }

        /// <summary>Size (blur radius or stroke width) in pixels.</summary>
        public float? Size { get; set; }

        /// <summary>Offset distance in pixels (shadows and satin).</summary>
        public float? Distance { get; set; }

        /// <summary>Light or offset angle in degrees.</summary>
        public float? Angle { get; set; }

        /// <summary>Spread (drop shadow, outer glow) or choke (inner shadow, inner glow) in percent.</summary>
        public float? Spread { get; set; }

        /// <summary>
        /// The solid color (highlight color for Bevel); null for gradient and pattern
        /// fills and for gradient glows and strokes.
        /// </summary>
        public PsdColor? Color { get; set; }

        // ---- shadows ----

        /// <summary>Layer Knocks Out Drop Shadow (layerConceals; drop shadow only, default true).</summary>
        public bool? LayerConceals { get; set; }

        // ---- shadows and glows ----

        /// <summary>
        /// The transfer contour (TrnS); null when absent or the straight Linear line.
        /// </summary>
        public PsdContour Contour { get; set; }

        /// <summary>Anti-aliased contour (AntA).</summary>
        public bool ContourAntiAliased { get; set; }

        // ---- glows ----

        /// <summary>Range in percent (Inpr, clamped 1..100).</summary>
        public float? Range { get; set; }

        /// <summary>True for the Center source (glwS SrcC); false for Edge.</summary>
        public bool? CenterSource { get; set; }

        /// <summary>True for the Precise technique (GlwT PrBL); false for Softer.</summary>
        public bool? Precise { get; set; }

        // ---- gradient / pattern content (glows, overlays, stroke, bevel texture) ----

        /// <summary>The gradient (Grad descriptor) with its placement keys; null when the effect paints a solid color or pattern.</summary>
        public PsdGradient Gradient { get; set; }

        /// <summary>The pattern placement (Ptrn); null when the effect paints a color or gradient.</summary>
        public PsdPatternPlacement Pattern { get; set; }

        /// <summary>Dither (Dthr) of a gradient overlay or gradient stroke.</summary>
        public bool? Dither { get; set; }

        /// <summary>
        /// What the effect paints: a solid color, a gradient or a pattern
        /// (stroke's PntT, glows' Clr/Grad, overlays' kind). Null for effects
        /// that do not fill anything (shadows, bevel, satin).
        /// </summary>
        public PsdFillType? FillType { get; set; }

        // ---- overlays ----

        /// <summary>Offset angle of a gradient overlay in degrees (Angl, default 90). Same field as <see cref="Angle"/>.</summary>

        // ---- stroke ----

        /// <summary>Overprint stroke fill (overprint).</summary>
        public bool? Overprint { get; set; }

        // ---- satin ----

        /// <summary>Invert (Invr; default true).</summary>
        public bool? Invert { get; set; }

        // ---- stroke and bevel ----

        public PsdStrokePosition? StrokePosition { get; set; }

        // ---- bevel ----

        public PsdBevelStyle? BevelStyle { get; set; }

        public PsdBevelTechnique? BevelTechnique { get; set; }

        /// <summary>Bevel light altitude in degrees.</summary>
        public float? Altitude { get; set; }

        /// <summary>Bevel depth in percent (srgR).</summary>
        public float? Depth { get; set; }

        /// <summary>Direction Up (bvlD); false for Down.</summary>
        public bool? DirectionUp { get; set; }

        /// <summary>Soften in pixels (Sftn).</summary>
        public float? Soften { get; set; }

        /// <summary>The gloss contour (TrnS of a bevel); never null (Linear when absent).</summary>
        public PsdContour Gloss { get; set; }

        /// <summary>Anti-aliased gloss contour (antialiasGloss).</summary>
        public bool GlossAntiAliased { get; set; }

        /// <summary>The bevel contour (MpgS) when Use Shape is on; null otherwise.</summary>
        public PsdContour BevelContour { get; set; }

        /// <summary>Contour range 0..1 (Inpr / 100) of the bevel contour.</summary>
        public float? ContourRange { get; set; }

        /// <summary>The bevel texture (pattern placement) when Use Texture is on; its angle is forced to 0.</summary>
        public PsdPatternPlacement Texture { get; set; }

        /// <summary>Invert texture (InvT).</summary>
        public bool? TextureInvert { get; set; }

        /// <summary>Texture depth (textureDepth / 100, clamped -10..10).</summary>
        public float? TextureDepth { get; set; }

        // ---- bevel shadow half ----

        public PsdBlendMode? ShadowBlendMode { get; set; }

        public float? ShadowOpacity { get; set; }

        public PsdColor? ShadowColor { get; set; }

        /// <summary>The effect's raw descriptor; every file key stays reachable through it.</summary>
        public Descriptor Descriptor { get; set; }

        /// <summary>Reads any number straight from the raw descriptor (e.g. "ShdN" jitter).</summary>
        public double GetNumber(string key, double fallback = double.NaN)
        {
            var d = Descriptor;
            return d != null ? d.GetNumber(key, fallback) : fallback;
        }

        public override string ToString()
        {
            var parts = new List<string> { Kind.ToString() };
            if (!Enabled)
            {
                parts.Add("off");
            }

            parts.Add(BlendMode.ToString());
            parts.Add(string.Format("{0:0}%", Opacity * 100));
            if (Noise.HasValue)
            {
                parts.Add(string.Format("noise {0:0.#}%", Noise.Value * 100));
            }

            if (Color.HasValue)
            {
                parts.Add(Color.Value.ToString());
            }

            if (Gradient != null)
            {
                parts.Add(Gradient.ToString());
            }

            if (Pattern != null)
            {
                parts.Add(Pattern.ToString());
            }

            if (Size.HasValue)
            {
                parts.Add(string.Format("size {0:0.##}", Size.Value));
            }

            if (Distance.HasValue)
            {
                parts.Add(string.Format("distance {0:0.##}", Distance.Value));
            }

            if (Angle.HasValue)
            {
                parts.Add(string.Format("angle {0:0.##}", Angle.Value));
            }

            if (Spread.HasValue)
            {
                parts.Add(string.Format("spread {0:0.#}", Spread.Value));
            }

            if (Range.HasValue)
            {
                parts.Add(string.Format("range {0:0.#}", Range.Value));
            }

            if (Depth.HasValue)
            {
                parts.Add(string.Format("depth {0:0.#}%", Depth.Value));
            }

            if (Altitude.HasValue)
            {
                parts.Add(string.Format("altitude {0:0.#}", Altitude.Value));
            }

            if (Soften.HasValue)
            {
                parts.Add(string.Format("soften {0:0.##}", Soften.Value));
            }

            if (StrokePosition.HasValue)
            {
                parts.Add(StrokePosition.Value.ToString());
            }

            if (BevelStyle.HasValue)
            {
                parts.Add(BevelStyle.Value.ToString());
            }

            if (BevelTechnique.HasValue)
            {
                parts.Add(BevelTechnique.Value.ToString());
            }

            if (DirectionUp.HasValue)
            {
                parts.Add(DirectionUp.Value ? "up" : "down");
            }

            if (CenterSource.HasValue)
            {
                parts.Add(CenterSource.Value ? "center" : "edge");
            }

            if (Precise.HasValue)
            {
                parts.Add(Precise.Value ? "precise" : "softer");
            }

            if (LayerConceals.HasValue && !LayerConceals.Value)
            {
                parts.Add("knockout");
            }

            if (Invert.HasValue && Invert.Value)
            {
                parts.Add("inverted");
            }

            if (Overprint.HasValue && Overprint.Value)
            {
                parts.Add("overprint");
            }

            if (Dither.HasValue && Dither.Value)
            {
                parts.Add("dither");
            }

            if (TextureInvert.HasValue && TextureInvert.Value)
            {
                parts.Add("texture-inverted");
            }

            if (TextureDepth.HasValue)
            {
                parts.Add(string.Format("texture depth {0:0.##}", TextureDepth.Value));
            }

            if (ContourAntiAliased)
            {
                parts.Add("contour AA");
            }

            if (GlossAntiAliased)
            {
                parts.Add("gloss AA");
            }

            if (Contour != null && !Contour.IsLinear)
            {
                parts.Add("contour " + Contour);
            }

            if (Gloss != null && !Gloss.IsLinear)
            {
                parts.Add("gloss " + Gloss);
            }

            if (BevelContour != null && !BevelContour.IsLinear)
            {
                parts.Add("bevel contour " + BevelContour);
            }

            if (ContourRange.HasValue)
            {
                parts.Add(string.Format("contour range {0:0.##}", ContourRange.Value));
            }

            return string.Join(" ", parts.ToArray());
        }
    }

    /// <summary>
    /// A read-only view of a layer's style (lfx2/lfxs/lmfx, or Photoshop 5 lrFX):
    /// every effect instance in file order, enabled or not.
    /// </summary>
    public sealed class PsdLayerStyle
    {
        private PsdLayerStyle(bool visible, List<PsdEffect> effects, Descriptor descriptor, bool isLegacy)
        {
            Visible = visible;
            Effects = effects;
            Descriptor = descriptor;
            IsLegacy = isLegacy;
        }

        /// <summary>The master "effects visible" switch (masterFXSwitch).</summary>
        public bool Visible { get; private set; }

        /// <summary>Every effect instance in file order, including disabled ones.</summary>
        public IReadOnlyList<PsdEffect> Effects { get; private set; }

        /// <summary>The effects that render: enabled ones, when <see cref="Visible"/> is on.</summary>
        public IEnumerable<PsdEffect> ActiveEffects
        {
            get
            {
                if (!Visible)
                {
                    yield break;
                }

                foreach (var effect in Effects)
                {
                    if (effect.Enabled)
                    {
                        yield return effect;
                    }
                }
            }
        }

        /// <summary>The layer-effects descriptor (for legacy styles, the translation of lrFX).</summary>
        public Descriptor Descriptor { get; private set; }

        /// <summary>True when the style comes from Photoshop 5 effects (lrFX).</summary>
        public bool IsLegacy { get; private set; }

        internal static PsdLayerStyle Parse(Descriptor root, float globalAngle, float globalAltitude)
        {
            var effects = new List<PsdEffect>();
            foreach (var item in root.Items)
            {
                var kind = KindOf(item.Key);
                if (!kind.HasValue)
                {
                    continue;
                }

                foreach (var descriptor in Instances(item.Value))
                {
                    effects.Add(Project(kind.Value, descriptor.GetBoolean("enab"), descriptor, globalAngle, globalAltitude));
                }
            }

            return new PsdLayerStyle(root.GetBoolean("masterFXSwitch", true), effects, root, false);
        }

        /// <summary>Builds a style from a legacy lrFX block that was translated into the descriptor shape.</summary>
        internal static PsdLayerStyle FromLegacy(Descriptor translated, float globalAngle, float globalAltitude)
        {
            var style = Parse(translated, globalAngle, globalAltitude);
            var legacyCopy = new List<PsdEffect>(style.Effects.Count);
            legacyCopy.AddRange(style.Effects);
            return new PsdLayerStyle(style.Visible, legacyCopy, style.Descriptor, true);
        }

        private static PsdEffectKind? KindOf(string key)
        {
            switch (key)
            {
                case "DrSh":
                case "dropShadowMulti":
                    return PsdEffectKind.DropShadow;
                case "IrSh":
                case "innerShadowMulti":
                    return PsdEffectKind.InnerShadow;
                case "OrGl":
                case "outerGlowMulti":
                    return PsdEffectKind.OuterGlow;
                case "IrGl":
                case "innerGlowMulti":
                    return PsdEffectKind.InnerGlow;
                case "ebbl":
                case "bevelEmbossMulti":
                    return PsdEffectKind.BevelEmboss;
                case "ChFX":
                case "chromeFXMulti":
                    return PsdEffectKind.Satin;
                case "SoFi":
                case "solidFillMulti":
                    return PsdEffectKind.ColorOverlay;
                case "GrFl":
                case "gradientFillMulti":
                    return PsdEffectKind.GradientOverlay;
                case "patternFill":
                case "patternFillMulti":
                    return PsdEffectKind.PatternOverlay;
                case "FrFX":
                case "frameFXMulti":
                    return PsdEffectKind.Stroke;
                default:
                    return null;
            }
        }

        private static IEnumerable<Descriptor> Instances(DescriptorValue value)
        {
            if (value != null && value.Type == DescriptorValueType.List && value.List != null)
            {
                foreach (var item in value.List)
                {
                    if (item.Object != null)
                    {
                        yield return item.Object;
                    }
                }
            }
            else if (value != null && value.Object != null)
            {
                yield return value.Object;
            }
        }

        private static PsdEffect Project(PsdEffectKind kind, bool enabled, Descriptor d, float globalAngle, float globalAltitude)
        {
            var effect = new PsdEffect { Kind = kind, Enabled = enabled, Descriptor = d, BlendMode = PsdBlendMode.Normal, Opacity = 1f };
            switch (kind)
            {
                case PsdEffectKind.DropShadow:
                case PsdEffectKind.InnerShadow:
                    {
                        effect.BlendMode = Mode(d, "mul ");
                        effect.Color = d.GetColor("Clr ") ?? new PsdColor(0, 0, 0);
                        effect.Opacity = Percent(d, "Opct", 75);
                        effect.Angle = Angle(d, globalAngle);
                        effect.Distance = Math.Max(0, (float)d.GetNumber("Dstn", 5));
                        effect.Spread = Clamp((float)d.GetNumber("Ckmt", 0), 0, 100);
                        effect.Size = Math.Max(0, (float)d.GetNumber("blur", 5));
                        effect.Noise = NoiseOf(d);
                        effect.LayerConceals = kind == PsdEffectKind.DropShadow ? d.GetBoolean("layerConceals", true) : (bool?)false;
                        effect.Contour = ContourOf(d);
                        effect.ContourAntiAliased = d.GetBoolean("AntA");
                        break;
                    }

                case PsdEffectKind.OuterGlow:
                case PsdEffectKind.InnerGlow:
                    {
                        var gradient = d.GetObject("Clr ") == null && d.GetObject("Grad") != null;
                        effect.FillType = gradient ? PsdFillType.Gradient : PsdFillType.Solid;
                        effect.BlendMode = Mode(d, "scrn");
                        effect.Color = gradient ? (PsdColor?)null : d.GetColor("Clr ") ?? new PsdColor(255, 255, 190);
                        effect.Opacity = Percent(d, "Opct", 75);
                        effect.Spread = Clamp((float)d.GetNumber("Ckmt", 0), 0, 100);
                        effect.Size = Math.Max(0, (float)d.GetNumber("blur", 5));
                        effect.Gradient = gradient ? PsdGradient.FromDescriptor(d) : null;
                        effect.Range = Clamp((float)d.GetNumber("Inpr", 100), 1, 100);
                        effect.CenterSource = d.GetEnum("glwS") == "SrcC";
                        effect.Precise = d.GetEnum("GlwT") == "PrBL";
                        effect.Noise = NoiseOf(d);
                        effect.Contour = ContourOf(d);
                        effect.ContourAntiAliased = d.GetBoolean("AntA");
                        break;
                    }

                case PsdEffectKind.ColorOverlay:
                case PsdEffectKind.GradientOverlay:
                case PsdEffectKind.PatternOverlay:
                    effect.FillType =
                        kind == PsdEffectKind.GradientOverlay ? PsdFillType.Gradient :
                        kind == PsdEffectKind.PatternOverlay ? PsdFillType.Pattern : PsdFillType.Solid;
                    effect.BlendMode = Mode(d, "norm");
                    effect.Opacity = Percent(d, "Opct", 100);
                    effect.Color = kind == PsdEffectKind.ColorOverlay ? d.GetColor("Clr ") ?? new PsdColor(255, 0, 0) : (PsdColor?)null;
                    effect.Gradient = kind == PsdEffectKind.GradientOverlay ? PsdGradient.FromDescriptor(d) : null;
                    effect.Pattern = kind == PsdEffectKind.PatternOverlay ? PsdPatternPlacement.FromDescriptor(d) : null;
                    effect.Angle = kind == PsdEffectKind.GradientOverlay ? (float)d.GetNumber("Angl", 90) : (float?)null;
                    effect.Dither = d.HasBoolean("Dthr") ? d.GetBoolean("Dthr") : (bool?)null;
                    break;

                case PsdEffectKind.Stroke:
                    {
                        // PntT: SClr = solid color, GrFl = gradient, Ptrn = pattern.
                        var paint = d.GetEnum("PntT");
                        var fill =
                            paint == "GrFl" ? PsdFillType.Gradient :
                            paint == "Ptrn" ? PsdFillType.Pattern : PsdFillType.Solid;
                        effect.FillType = fill;
                        effect.BlendMode = Mode(d, "norm");
                        effect.Color = fill == PsdFillType.Solid ? d.GetColor("Clr ") ?? new PsdColor(0, 0, 0) : (PsdColor?)null;
                        effect.Opacity = Percent(d, "Opct", 100);
                        effect.Size = Math.Max(1, (float)d.GetNumber("Sz  ", 3));
                        effect.Gradient = fill == PsdFillType.Gradient ? PsdGradient.FromDescriptor(d) : null;
                        effect.Pattern = fill == PsdFillType.Pattern ? PsdPatternPlacement.FromDescriptor(d) : null;
                        effect.Overprint = d.GetBoolean("overprint");
                        effect.Dither = fill == PsdFillType.Gradient && d.HasBoolean("Dthr") ? d.GetBoolean("Dthr") : (bool?)null;
                        var styl = d.GetEnum("Styl");
                        effect.StrokePosition = styl == "InsF" ? PsdStrokePosition.Inside : styl == "CtrF" ? PsdStrokePosition.Center : PsdStrokePosition.Outside;
                        break;
                    }

                case PsdEffectKind.Satin:
                    effect.BlendMode = Mode(d, "mul ");
                    effect.Color = d.GetColor("Clr ") ?? new PsdColor(0, 0, 0);
                    effect.Opacity = Percent(d, "Opct", 50);
                    effect.Angle = (float)d.GetNumber("lagl", 19);
                    effect.Distance = Math.Max(0, (float)d.GetNumber("Dstn", 11));
                    effect.Size = Math.Max(0, (float)d.GetNumber("blur", 14));
                    effect.Invert = d.GetBoolean("Invr", true);
                    // ChFX is the old Chrome FX block: it carries a contour (MpgS) and
                    // its anti-alias flag even though the Satin UI never shows them.
                    effect.Contour = PsdContour.FromDescriptor(d.GetObject("MpgS"));
                    effect.ContourAntiAliased = d.GetBoolean("AntA");
                    break;

                case PsdEffectKind.BevelEmboss:
                    {
                        var global = d.GetBoolean("uglg");
                        var useContour = d.GetBoolean("useShape");
                        var useTexture = d.GetBoolean("useTexture");
                        effect.BlendMode = Mode(d, "hglM", "scrn");
                        effect.Color = d.GetColor("hglC") ?? new PsdColor(255, 255, 255);
                        effect.Opacity = Percent(d, "hglO", 75);
                        effect.ShadowBlendMode = Mode(d, "sdwM", "mul ");
                        effect.ShadowColor = d.GetColor("sdwC") ?? new PsdColor(0, 0, 0);
                        effect.ShadowOpacity = Percent(d, "sdwO", 75);
                        effect.Angle = global ? globalAngle : (float)d.GetNumber("lagl", 120);
                        effect.Altitude = global ? globalAltitude : (float)d.GetNumber("Lald", 30);
                        effect.Depth = Math.Max(1f, (float)d.GetNumber("srgR", 100));
                        effect.Size = Math.Max(1f, (float)d.GetNumber("blur", 5));
                        var bvlS = d.GetEnum("bvlS");
                        effect.BevelStyle =
                            bvlS == "OtrB" ? PsdBevelStyle.OuterBevel :
                            bvlS == "Embs" ? PsdBevelStyle.Emboss :
                            bvlS == "PlEb" ? PsdBevelStyle.PillowEmboss :
                            bvlS == "strokeEmboss" ? PsdBevelStyle.StrokeEmboss : PsdBevelStyle.InnerBevel;
                        var bvlT = d.GetEnum("bvlT");
                        effect.BevelTechnique =
                            bvlT == "PrBL" ? PsdBevelTechnique.ChiselHard :
                            bvlT == "Slmt" ? PsdBevelTechnique.ChiselSoft : PsdBevelTechnique.Smooth;
                        effect.DirectionUp = d.GetEnum("bvlD") != "Out ";
                        effect.Soften = Math.Max(0f, (float)d.GetNumber("Sftn", 0));
                        effect.Gloss = PsdContour.FromDescriptor(d.GetObject("TrnS"));
                        effect.GlossAntiAliased = d.GetBoolean("antialiasGloss");
                        effect.BevelContour = useContour ? PsdContour.FromDescriptor(d.GetObject("MpgS")) : null;
                        effect.ContourAntiAliased = d.GetBoolean("AntA");
                        effect.ContourRange = Clamp((float)(d.GetNumber("Inpr", 50) / 100), 0f, 1f);
                        effect.Texture = null;
                        if (useTexture)
                        {
                            // Texture has no angle; its scale and phase follow the effect keys.
                            var texture = PsdPatternPlacement.FromDescriptor(d);
                            if (texture != null)
                            {
                                texture.AngleDegrees = 0;
                                // The texture panel has a single "Link With Layer"
                                // checkbox, stored in Algn — lnkA never appears in ebbl
                                // (verified across real files; the reference renderer
                                // maps the texture link to Algn too).
                                texture.LinkWithLayer = d.GetBoolean("Algn", true);
                                texture.AlignWithLayer = texture.LinkWithLayer;
                                effect.Texture = texture;
                            }
                        }

                        effect.TextureInvert = d.GetBoolean("InvT");
                        effect.TextureDepth = Clamp((float)(d.GetNumber("textureDepth", 100) / 100), -10f, 10f);
                        break;
                    }
            }

            return effect;
        }

        private static PsdBlendMode Mode(Descriptor d, string fallback)
        {
            return Mode(d, "Md  ", fallback);
        }

        private static PsdBlendMode Mode(Descriptor d, string key, string fallback)
        {
            var mode = d.GetEnum(key);
            return mode == null ? BlendModeKeys.FromKey(fallback) : BlendModeKeys.FromDescriptorEnum(mode);
        }

        private static float Percent(Descriptor d, string key, double fallback)
        {
            return Clamp((float)(d.GetNumber(key, fallback) / 100), 0f, 1f);
        }

        private static float Angle(Descriptor d, float globalAngle)
        {
            return d.GetBoolean("uglg") ? globalAngle : (float)d.GetNumber("lagl", 120);
        }

        /// <summary>Noise (杂色, Nose key) as 0..1; null when the effect has no such key.</summary>
        private static float? NoiseOf(Descriptor d)
        {
            if (!d.HasNumber("Nose"))
            {
                return null;
            }

            return Clamp((float)(d.GetNumber("Nose", 0) / 100), 0f, 1f);
        }

        /// <summary>The transfer contour of a shadow or glow, or null when it is Linear.</summary>
        private static PsdContour ContourOf(Descriptor d)
        {
            return PsdContour.FromDescriptor(d.GetObject("TrnS"));          
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
