using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>A raw tagged block (8BIM signature, key, payload).</summary>
    public sealed class TaggedBlock
    {
        public TaggedBlock(string key, byte[] data)
        {
            Key = key;
            Data = data;
        }

        public string Key { get; private set; }

        public byte[] Data { get; private set; }
    }

    /// <summary>
    /// One stored channel of a layer: ID (0.. color, -1 alpha, -2/-3 masks) and its
    /// compressed payload (compression marker included). A payload longer than 2 GB
    /// keeps <see cref="Data"/> empty and rides in <see cref="LargeData"/>; the library
    /// loads files into memory, so that path exists for parity with the reference.
    /// </summary>
    public sealed class LayerChannel
    {
        public LayerChannel(short id, byte[] data)
        {
            Id = id;
            Data = data;
        }

        public short Id { get; private set; }

        public byte[] Data { get; private set; }

        internal ByteRange? LargeData { get; set; }

        /// <summary>The payload, wherever it lives.</summary>
        internal ByteRange Bytes
        {
            get { return LargeData.HasValue ? LargeData.Value : new ByteRange(Data); }
        }
    }

    /// <summary>
    /// One layer record with its parsed layer style. Channel pixel data is not
    /// kept: this library reads structure and effects only.
    /// </summary>
    public sealed class PsdLayer
    {
        internal List<TaggedBlock> Blocks = new List<TaggedBlock>();

        internal List<LayerChannel> _channels = new List<LayerChannel>();

        /// <summary>Payload lengths from the record's channel table, filled by the parser.</summary>
        internal long[] ChannelLengths = new long[0];

        /// <summary>The owning document; null for detached layers.</summary>
        public PsdDocument Document { get; internal set; }

        public string Name { get; set; }

        public PsdRect Bounds { get; set; }

        public bool IsVisible { get; set; }

        public byte Opacity { get; set; } = 255;

        public PsdBlendMode BlendMode { get; set; }

        /// <summary>The parsed layer style, or null when the layer has no effects block.</summary>
        public PsdLayerStyle Style { get; set; }

        /// <summary>Layer ID from lyid, or 0.</summary>
        public uint Id { get; set; }

        /// <summary>Position in the file's layer list (0 is the bottom record).</summary>
        public int Index { get; set; }

        public PsdLayerKind Kind { get; set; }

        public PsdSectionType SectionType { get; set; }

        /// <summary>Fill opacity 0-255 (iOpa): fades the layer content but not its effects.</summary>
        public byte FillOpacity { get; set; } = 255;

        /// <summary>Clipped to the layer below (clipping mask member).</summary>
        public bool IsClipped { get; set; }

        public bool TransparencyProtected { get; set; }

        public bool IsGroup
        {
            get { return Kind == PsdLayerKind.Group; }
        }

        /// <summary>Parent group, or null at the document root.</summary>
        public PsdLayer Parent { get; internal set; }

        private readonly List<PsdLayer> _children = new List<PsdLayer>();

        /// <summary>Children of a group, bottom to top (file order).</summary>
        public IReadOnlyList<PsdLayer> Children
        {
            get { return _children; }
        }

        /// <summary>Raw "blend if" ranges from the layer record.</summary>
        public byte[] BlendingRanges { get; set; }

        /// <summary>The raster layer mask the user painted, if any.</summary>
        public LayerMask Mask { get; set; }

        /// <summary>
        /// Photoshop's baked vector-mask coverage (mask flag bit 3). Rendering uses
        /// it in place of rasterizing <see cref="VectorMask"/> when present.
        /// </summary>
        public LayerMask RenderedVectorMask { get; set; }

        /// <summary>The vector mask path (vmsk/vsms), or null.</summary>
        public VectorPath VectorMask { get; set; }

        /// <summary>Vector mask density 0-255 from the mask parameters.</summary>
        public byte VectorMaskDensity { get; set; } = 255;

        /// <summary>Vector mask feather (gaussian sigma in pixels) from the mask parameters.</summary>
        public double VectorMaskFeather { get; set; }

        /// <summary>The shape's vector stroke and fill switch (vstk), when present.</summary>
        public VectorStrokeStyle VectorStroke { get; set; }

        /// <summary>Solid fill color for SoCo fill/shape layers.</summary>
        public PsdColor? FillColor { get; set; }

        /// <summary>The fill descriptor for fill/shape layers (SoCo, GdFl, PtFl).</summary>
        public Descriptor FillDescriptor { get; set; }

        /// <summary>Tagged-block key of the fill or adjustment (SoCo, curv, hue2...).</summary>
        public string ContentKey { get; set; }

        /// <summary>Smart-object placement descriptor (SoLd/SoLE/PlLd), when present.</summary>
        public Descriptor SmartObject { get; set; }

        /// <summary>Unique ID of the linked/embedded file this smart object shows.</summary>
        public string SmartObjectFileId { get; set; }

        /// <summary>"Blend Interior Effects as Group" (infx, default false).</summary>
        public bool BlendInteriorElements { get; set; }

        /// <summary>Whether clipped layers blend with this base as a group (clbl, default true).</summary>
        public bool BlendClippedElements { get; set; } = true;

        private PsdLayerMetadata _metadata;

        /// <summary>Typed view of the metadata block (shmd): layer time and per-comp state, parsed on first use; null without one.</summary>
        public PsdLayerMetadata Metadata
        {
            get
            {
                if (_metadata == null)
                {
                    var block = GetTaggedBlock("shmd");
                    _metadata = block != null ? PsdLayerMetadata.Parse(block) : null;
                }

                return _metadata;
            }
        }

        /// <summary>Visible here and in every ancestor group.</summary>
        public bool IsEffectivelyVisible
        {
            get
            {
                for (var layer = this; layer != null; layer = layer.Parent)
                {
                    if (!layer.IsVisible)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Slash-separated path of group names down to this layer.</summary>
        public string Path
        {
            get { return Parent == null ? Name : Parent.Path + "/" + Name; }
        }

        /// <summary>The keys of the layer's tagged blocks, in file order.</summary>
        public IReadOnlyList<string> TaggedBlockKeys
        {
            get
            {
                var keys = new List<string>(Blocks.Count);
                foreach (var block in Blocks)
                {
                    keys.Add(block.Key);
                }

                return keys;
            }
        }

        /// <summary>Returns the raw payload bytes of the first tagged block with this key, or null.</summary>
        public byte[] GetTaggedBlock(string key)
        {
            foreach (var block in Blocks)
            {
                if (block.Key == key)
                {
                    return block.Data;
                }
            }

            return null;
        }

        /// <summary>The layer's stored channels, in file order.</summary>
        public IReadOnlyList<LayerChannel> Channels
        {
            get { return _channels; }
            internal set { _channels = new List<LayerChannel>(value); }
        }

        /// <summary>Type-tool data of a text layer (TySh / tySh, merged with the Txt2 block); null for other layers.</summary>
        public TextLayerInfo Text { get; internal set; }

        /// <summary>The channel with the given ID, or null.</summary>
        public LayerChannel GetChannel(short id)
        {
            foreach (var channel in _channels)
            {
                if (channel.Id == id)
                {
                    return channel;
                }
            }

            return null;
        }

        /// <summary>Decodes one channel to a float plane of rect size (masks use their own rectangle).</summary>
        internal float[] DecodeChannelPlane(short id, PsdRect rect)
        {
            return DecodeChannelPlane(id, rect, rect.Top, rect.Bottom, null);
        }

        /// <summary>
        /// Decodes document rows [top, bottom) of one channel stored over rect into a
        /// plane of just those rows; each row is identical to the same row of the whole
        /// plane. readers keeps ZIP streams open between consecutive row ranges.
        /// </summary>
        internal float[] DecodeChannelPlane(short id, PsdRect rect, int top, int bottom, ChannelReaders readers)
        {
            var channel = GetChannel(id);
            var first = (int)Compat.Clamp((long)top - rect.Top, 0, Math.Max(0, rect.Height));
            var end = (int)Compat.Clamp((long)bottom - rect.Top, first, Math.Max(0, rect.Height));
            if (channel == null || rect.IsEmpty || end <= first)
            {
                return null;
            }

            var doc = Document;
            var rows = end - first;
            byte[] bytes;
            if (first == 0 && end == rect.Height && readers == null && channel.LargeData.HasValue == false)
            {
                bytes = ChannelCodec.DecodeLayerChannel(channel.Data, rect.Width, rect.Height, doc.Depth, doc.IsLargeDocument);
            }
            else
            {
                bytes = ChannelCodec.DecodeLayerChannelRows(channel.Bytes, rect.Width, rect.Height, doc.Depth, doc.IsLargeDocument, first, end, readers == null ? null : readers.For(channel));
            }

            var plane = new float[ChannelCodec.BufferLength((long)rect.Width * rows)];
            ChannelCodec.ToFloat(bytes, rect.Width, rows, doc.Depth, plane);
            return plane;
        }

        /// <summary>Decodes the layer's stored pixels (layer bounds) to straight sRGB floats. Masks, opacity and blend mode are not applied.</summary>
        internal PlanarImage DecodePixels()
        {
            return DecodePixels(Bounds.Top, Bounds.Bottom, null);
        }

        /// <summary>Whether DecodePixels() returns an image: the layer has bounds and a color or alpha channel.</summary>
        internal bool HasStoredPixels
        {
            get
            {
                if (Bounds.IsEmpty)
                {
                    return false;
                }

                var colorCount = ColorSpaces.ColorChannelCount(Document.ColorMode);
                for (short i = -1; i < colorCount; i++)
                {
                    if (GetChannel(i) != null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Decodes the stored pixels of document rows [top, bottom) (cut to the layer
        /// bounds). Returns null exactly when DecodePixels() would; otherwise an image
        /// whose Logical bounds are the layer's, holding the requested rows.
        /// </summary>
        internal PlanarImage DecodePixels(int top, int bottom, ChannelReaders readers)
        {
            Cancellation.Check();
            var bounds = Bounds;
            if (!HasStoredPixels)
            {
                return null;
            }

            var rows = new PsdRect(bounds.Left, Math.Max(bounds.Top, top), bounds.Right, Math.Min(bounds.Bottom, bottom));
            if (rows.IsEmpty)
            {
                return new PlanarImage(default(PsdRect)) { Logical = bounds };
            }

            var doc = Document;
            var colorCount = ColorSpaces.ColorChannelCount(doc.ColorMode);
            // Color planes, then alpha (index colorCount), inflated in parallel.
            var decoded = new float[colorCount + 1][];
            Parallelism.For(decoded.Length, (long)rows.Width * rows.Height, delegate(int start, int end)
            {
                for (var i = start; i < end; i++)
                {
                    decoded[i] = DecodeChannelPlane(i < colorCount ? (short)i : (short)-1, bounds, rows.Top, rows.Bottom, readers);
                }
            });

            var planes = new float[colorCount][];
            for (var i = 0; i < colorCount; i++)
            {
                planes[i] = decoded[i];
            }

            var alpha = decoded[colorCount];
            var image = new PlanarImage(rows) { Logical = bounds };
            ColorSpaces.ToRgb(doc, doc.Depth, planes, image);
            if (alpha != null)
            {
                Array.Copy(alpha, image.A, Math.Min(alpha.Length, image.A.Length));
            }
            else
            {
                for (var i = 0; i < image.A.Length; i++)
                {
                    image.A[i] = 1f;
                }
            }

            return image;
        }

        /// <summary>Decodes a mask plane (user mask or baked vector mask) for its own rectangle.</summary>
        internal float[] DecodeMask(LayerMask mask)
        {
            return DecodeChannelPlane(mask.ChannelId, mask.Bounds);
        }

        /// <summary>Returns the layer's stored pixels as straight 8-bit RGBA within Bounds, or null for empty layers.</summary>
        public RgbaImage GetPixels()
        {
            var planar = DecodePixels();
            return planar == null ? null : planar.ToRgba(Bounds);
        }

        /// <summary>Returns the layer's stored pixels (see GetPixels()), observing a cancellation token while the channels decode.</summary>
        public RgbaImage GetPixels(System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (Cancellation.Use(cancellationToken))
            {
                return GetPixels();
            }
        }

        internal void AddChild(PsdLayer child)
        {
            child.Parent = this;
            _children.Add(child);
        }

        public override string ToString()
        {
            var builder = new StringBuilder(Name);
            if (Style != null && Style.Effects.Count > 0)
            {
                builder.Append(" (").Append(Style.Effects.Count).Append(" effects)");
            }

            return builder.ToString();
        }
    }
}
