using System;
using System.Collections.Generic;
using System.Linq;

namespace XRay.PsdFx
{
    /// <summary>
    /// Knockout (Advanced Blending, knko: 1 shallow, 2 deep), which the reference
    /// does not model. Calibrated against the Photoshop merged images of psd-tools'
    /// transparency/knockout-*.psd:
    /// A knockout element (layer, group or clipping run) composites against a
    /// knockout backdrop K instead of the stack below it. Shallow takes K at the start
    /// of the containing group: transparent inside an isolated group, the backdrop under
    /// a Pass Through group, the clipping base in a clipping run. Deep passes through
    /// Pass Through groups to the Background layer (transparent without one) and stops
    /// at isolated groups. At the root both are the Background state.
    /// The knockout shape m is the element's coverage times masks and opacity,
    /// without Fill: Fill fades only the content drawn over K. The result is
    /// (1 - m) B + m (content at Fill over K) in premultiplied color. The
    /// composite is linear in its coverage, so with X the element composited over K
    /// at coverage m x Fill, this equals X + (1 - m)(B - K).
    /// </summary>
    internal sealed partial class LayerCompositor
    {
        private const byte KnockoutNone = 0;
        private const byte KnockoutShallow = 1;
        private const byte KnockoutDeep = 2;

        private bool? _hasKnockout;

        /// <summary>The buffer the current deep backdrop belongs to; a list on any other buffer is isolated.</summary>
        private PlanarImage _knockoutTarget;

        /// <summary>The deep knockout backdrop of the current list (null is transparent).</summary>
        private PlanarImage _deepBackdrop;

        internal sealed class KnockoutScope
        {
            public PlanarImage SavedTarget { get; set; }

            public PlanarImage SavedDeep { get; set; }

            /// <summary>Knockout backdrops for the elements of this list (null is transparent).</summary>
            public PlanarImage Shallow { get; set; }

            public PlanarImage Deep { get; set; }

            public bool IsRoot { get; set; }
        }

        private byte KnockoutOf(PsdLayer layer)
        {
            if (layer.Kind == PsdLayerKind.Adjustment)
            {
                return KnockoutNone;
            }

            var block = layer.GetTaggedBlock("knko");
            if (block == null || block.Length == 0)
            {
                return KnockoutNone;
            }

            if (block[0] == KnockoutShallow)
            {
                return KnockoutShallow;
            }

            return block[0] == KnockoutDeep ? KnockoutDeep : KnockoutNone;
        }

        private bool HasKnockout
        {
            get
            {
                if (!_hasKnockout.HasValue)
                {
                    var any = false;
                    foreach (var layer in _document.Layers)
                    {
                        if (KnockoutOf(layer) != KnockoutNone)
                        {
                            any = true;
                            break;
                        }
                    }

                    _hasKnockout = any;
                }

                return _hasKnockout.Value;
            }
        }

        /// <summary>
        /// Photoshop's Background layer: the bottom root layer of a document whose layer
        /// count was positive (the merged image has no transparency channel).
        /// </summary>
        private bool IsBackgroundLayer(PsdLayer layer)
        {
            return !_document.MergedImageHasTransparency && _document.RootLayers.Count > 0
                && ReferenceEquals(_document.RootLayers[0], layer) && layer.Kind == PsdLayerKind.Pixel;
        }

        private KnockoutScope EnterKnockoutScope(PlanarImage target, IReadOnlyList<PsdLayer> siblings)
        {
            if (!HasKnockout)
            {
                return null;
            }

            var root = _knockoutTarget == null && ReferenceEquals(siblings, _document.RootLayers);
            var passThrough = !root && ReferenceEquals(_knockoutTarget, target);
            var scope = new KnockoutScope { SavedTarget = _knockoutTarget, SavedDeep = _deepBackdrop, IsRoot = root };
            if (passThrough)
            {
                scope.Deep = _deepBackdrop;
                foreach (var s in siblings)
                {
                    if (KnockoutOf(s) == KnockoutShallow && IsVisible(s))
                    {
                        scope.Shallow = Crop(target, target.Bounds);
                        break;
                    }
                }
            }

            // Isolated lists (and the root before its Background) knock out to transparency.
            _knockoutTarget = target;
            _deepBackdrop = scope.Deep;
            return scope;
        }

        private void ExitKnockoutScope(KnockoutScope scope)
        {
            if (scope != null)
            {
                _knockoutTarget = scope.SavedTarget;
                _deepBackdrop = scope.SavedDeep;
            }
        }

        /// <summary>After the root Background layer composites, both backdrops become its state.</summary>
        private void NoteKnockoutBackground(KnockoutScope scope, PlanarImage target, PsdLayer layer)
        {
            if (scope != null && scope.IsRoot && IsBackgroundLayer(layer))
            {
                scope.Shallow = scope.Deep = Crop(target, target.Bounds);
                _deepBackdrop = scope.Deep;
            }
        }

        /// <summary>Composites one element of a list, through its knockout when it has one.</summary>
        private void CompositeKnockoutElement(PlanarImage target, PsdLayer layer, KnockoutScope scope, MaskChain masks, Action draw)
        {
            var kind = scope == null ? KnockoutNone : KnockoutOf(layer);
            if (kind == KnockoutNone || !IsVisible(layer) || layer.Opacity == 0)
            {
                draw();
                return;
            }

            WithKnockout(target, layer, kind == KnockoutDeep ? scope.Deep : scope.Shallow, masks, draw);
        }

        /// <summary>
        /// Composites the members of a clipping run into run: a knockout member knocks
        /// out to the run as it stood after the base (the end of the clipping group).
        /// </summary>
        private void CompositeClipMembers(PlanarImage run, IReadOnlyList<PsdLayer> siblings, int start, int count)
        {
            PlanarImage baseState = null;
            for (var i = start; i < start + count; i++)
            {
                var member = siblings[i];
                if (HasKnockout && KnockoutOf(member) != KnockoutNone && IsVisible(member) && member.Opacity > 0)
                {
                    if (baseState == null)
                    {
                        baseState = run.Clone();
                    }

                    WithKnockout(run, member, baseState, null, delegate
                    {
                        CompositeLayer(run, member, null, null, true, null, false);
                    });
                }
                else
                {
                    CompositeLayer(run, member, null, null, true, null, false);
                }
            }
        }

        private void WithKnockout(PlanarImage target, PsdLayer layer, PlanarImage backdrop, MaskChain masks, Action draw)
        {
            var region = (layer.Kind == PsdLayerKind.Group ? target.Bounds : AffectedBounds(layer)).Intersect(target.Bounds);
            if (region.IsEmpty)
            {
                draw();
                return;
            }

            var shape = KnockoutShape(layer, region, masks);
            var before = Crop(target, region);
            var width = region.Width;
            for (var y = region.Top; y < region.Bottom; y++)
            {
                var t = target.RowOffset(y, region.Left);
                if (backdrop == null)
                {
                    Array.Clear(target.R, t, width);
                    Array.Clear(target.G, t, width);
                    Array.Clear(target.B, t, width);
                    Array.Clear(target.A, t, width);
                }
                else
                {
                    var k = backdrop.RowOffset(y, region.Left);
                    Array.Copy(backdrop.R, k, target.R, t, width);
                    Array.Copy(backdrop.G, k, target.G, t, width);
                    Array.Copy(backdrop.B, k, target.B, t, width);
                    Array.Copy(backdrop.A, k, target.A, t, width);
                }
            }

            draw();

            for (var y = region.Top; y < region.Bottom; y++)
            {
                var t = target.RowOffset(y, region.Left);
                var b = before.RowOffset(y, region.Left);
                var k = backdrop != null ? backdrop.RowOffset(y, region.Left) : 0;
                var m = (y - region.Top) * width;
                for (var i = 0; i < width; i++)
                {
                    var rest = 1f - Compat.Clamp(shape[m + i], 0f, 1f);
                    if (rest <= 0f)
                    {
                        continue;
                    }

                    float kr = 0f, kg = 0f, kb = 0f, ka = 0f;
                    if (backdrop != null)
                    {
                        ka = backdrop.A[k + i];
                        kr = backdrop.R[k + i] * ka;
                        kg = backdrop.G[k + i] * ka;
                        kb = backdrop.B[k + i] * ka;
                    }

                    var ba = before.A[b + i];
                    var xa = target.A[t + i];
                    var a = Compat.Clamp(xa + (rest * (ba - ka)), 0f, 1f);
                    if (a <= 0f)
                    {
                        target.R[t + i] = target.G[t + i] = target.B[t + i] = target.A[t + i] = 0f;
                        continue;
                    }

                    target.R[t + i] = Compat.Clamp(((target.R[t + i] * xa) + (rest * ((before.R[b + i] * ba) - kr))) / a, 0f, 1f);
                    target.G[t + i] = Compat.Clamp(((target.G[t + i] * xa) + (rest * ((before.G[b + i] * ba) - kg))) / a, 0f, 1f);
                    target.B[t + i] = Compat.Clamp(((target.B[t + i] * xa) + (rest * ((before.B[b + i] * ba) - kb))) / a, 0f, 1f);
                    target.A[t + i] = a;
                }
            }
        }

        /// <summary>
        /// The knockout shape over region: the element's alpha (a group's isolated
        /// content) times its masks, the parent mask chain and opacity, not Fill.
        /// </summary>
        private float[] KnockoutShape(PsdLayer layer, PsdRect region, MaskChain masks)
        {
            var shape = new float[region.Width * region.Height];
            PlanarImage content;
            if (layer.Kind == PsdLayerKind.Group)
            {
                var bounds = ContentBounds(layer).Intersect(region);
                if (bounds.IsEmpty)
                {
                    return shape;
                }

                content = new PlanarImage(bounds);
                CompositeList(content, layer.Children, null);
            }
            else
            {
                content = LayerPixels(layer);
            }

            var inner = content != null ? content.Bounds.Intersect(region) : default(PsdRect);
            if (inner.IsEmpty)
            {
                return shape;
            }

            var layerMask = LayerMasks(layer, inner, inner, inner.Top, inner.Bottom);
            var opacity = layer.Opacity / 255f;
            var row = new float[inner.Width];
            for (var y = inner.Top; y < inner.Bottom; y++)
            {
                Array.Copy(content.A, content.RowOffset(y, inner.Left), row, 0, inner.Width);
                if (layerMask != null)
                {
                    layerMask.MultiplyRow(y, inner.Left, row);
                }

                for (var chain = masks; chain != null; chain = chain.Next)
                {
                    chain.Sampler.MultiplyRow(y, inner.Left, row);
                }

                var offset = ((y - region.Top) * region.Width) + (inner.Left - region.Left);
                for (var i = 0; i < row.Length; i++)
                {
                    shape[offset + i] = row[i] * opacity;
                }
            }

            return shape;
        }
    }
}
