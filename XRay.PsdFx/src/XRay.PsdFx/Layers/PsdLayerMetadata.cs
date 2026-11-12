using System;
using System.Collections.Generic;

namespace XRay.PsdFx
{
    /// <summary>A point or offset in document pixels.</summary>
    public struct PsdPoint
    {
        public PsdPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public readonly double X;
        public readonly double Y;
    }

    /// <summary>
    /// One item of a layer's metadata block (shmd): a four-character key, the
    /// "copy on sheet duplication" flag and the payload. Photoshop's items are
    /// cust (layer time), cmls (layer comp state), mlst (animation frames),
    /// tmln (timeline), extn (generator settings), all versioned descriptors,
    /// and mdyn (animation flags, binary).
    /// </summary>
    public sealed class PsdLayerMetadataItem
    {
        public PsdLayerMetadataItem(string key, bool copyOnSheetDuplication, byte[] data, Descriptor descriptor)
        {
            Key = key;
            CopyOnSheetDuplication = copyOnSheetDuplication;
            Data = data;
            Descriptor = descriptor;
        }

        /// <summary>The item key, such as cmls.</summary>
        public string Key { get; private set; }

        /// <summary>Whether Photoshop copies the item when the layer is duplicated.</summary>
        public bool CopyOnSheetDuplication { get; private set; }

        /// <summary>The raw payload.</summary>
        public byte[] Data { get; private set; }

        /// <summary>The payload as a descriptor, when it is a versioned descriptor that decodes.</summary>
        public Descriptor Descriptor { get; private set; }
    }

    /// <summary>
    /// What one layer comp records for a layer (one entry of cmls/layerSettings).
    /// CompIds lists the comps (resource 1065 compID) the entry applies to; ID 0 is
    /// the document state Photoshop keeps beside the comps ("Last Document State").
    /// Each value is null when the entry does not store it, which means the comp
    /// does not change it from the layer's saved state.
    /// </summary>
    public sealed class PsdLayerCompState
    {
        /// <summary>The comp IDs this state applies to (compList).</summary>
        public List<int> CompIds = new List<int>();

        /// <summary>Layer visibility in the comp (enab).</summary>
        public bool? Visible;

        /// <summary>The layer's position in the comp as an offset in pixels from its saved position (Ofst).</summary>
        public PsdPoint? Offset;

        /// <summary>The vector mask's stored offset (vectorMask/Ofst), as stored.</summary>
        public PsdPoint? VectorMaskOffset;

        /// <summary>The layer effects' reference point (FXRefPoint), in pixels.</summary>
        public PsdPoint? EffectsReferencePoint;

        /// <summary>Blend mode (appearance, blendOptions/Md).</summary>
        public PsdBlendMode? BlendMode;

        /// <summary>Opacity from 0 to 1 (appearance, blendOptions/Opct).</summary>
        public double? Opacity;

        /// <summary>Fill opacity from 0 to 1 (appearance, blendOptions/fillOpacity).</summary>
        public double? FillOpacity;

        /// <summary>The comp's layer effects descriptor (Lefx), in the lfx2 layout.</summary>
        public Descriptor Effects;

        /// <summary>The raw entry.</summary>
        public Descriptor Descriptor;
    }

    /// <summary>
    /// Typed view of a layer's metadata block (shmd): every item, the layer time
    /// (cust) and the per-comp layer state (cmls) that Photoshop's layer comps
    /// (PsdImageResources.LayerComps) apply. Built by PsdLayer.Metadata; a damaged
    /// block yields the items that decoded.
    /// </summary>
    public sealed class PsdLayerMetadata
    {
        private PsdLayerMetadata()
        {
        }

        /// <summary>Every item in file order.</summary>
        public IReadOnlyList<PsdLayerMetadataItem> Items { get; private set; }

        /// <summary>The layer's last modification time (cust/layerTime, Unix seconds), when stored.</summary>
        public DateTime? LayerTime { get; private set; }

        /// <summary>The layer ID the comp state was recorded for (cmls/LyrI), when stored.</summary>
        public int? CompLayerId { get; private set; }

        /// <summary>The effects reference point when the comps were captured (cmls/origFXRefPoint).</summary>
        public PsdPoint? OriginalEffectsReferencePoint { get; private set; }

        /// <summary>The per-comp states (cmls/layerSettings); empty without layer comps.</summary>
        public IReadOnlyList<PsdLayerCompState> CompStates { get; private set; }

        /// <summary>The item with the given key, or null.</summary>
        public PsdLayerMetadataItem GetItem(string key)
        {
            foreach (var item in Items)
            {
                if (item.Key == key)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>The state the comp with compId records for this layer, or null.</summary>
        public PsdLayerCompState GetCompState(int compId)
        {
            foreach (var state in CompStates)
            {
                if (state.CompIds.Contains(compId))
                {
                    return state;
                }
            }

            return null;
        }

        /// <summary>Parses a shmd payload. Returns null when not even the item count decodes.</summary>
        internal static PsdLayerMetadata Parse(byte[] payload)
        {
            // Layout (Adobe "Metadata setting"): u32 count, then per item '8BIM'
            // (or '8ELE'), key, u8 copy flag, 3 padding bytes, u32 length, payload.
            var reader = new BigEndianReader(payload);
            if (reader.Remaining < 4)
            {
                return null;
            }

            var count = reader.ReadUInt32();
            var items = new List<PsdLayerMetadataItem>();
            try
            {
                for (uint i = 0; i < count && reader.Remaining >= 16; i++)
                {
                    var signature = reader.ReadSignature();
                    if (signature != "8BIM" && signature != "8ELE")
                    {
                        break;
                    }

                    var key = reader.ReadSignature();
                    var copy = reader.ReadByte() != 0;
                    reader.Skip(3);
                    var length = reader.ReadUInt32();
                    if (length > (uint)reader.Remaining)
                    {
                        break;
                    }

                    var data = reader.ReadBytes((int)length);
                    items.Add(new PsdLayerMetadataItem(key, copy, data, ReadDescriptor(data)));
                }
            }
            catch (PsdFormatException)
            {
                // Keep the items that decoded.
            }

            Descriptor custom = null;
            Descriptor comps = null;
            foreach (var item in items)
            {
                if (item.Key == "cust" && item.Descriptor != null)
                {
                    custom = item.Descriptor;
                }
                else if (item.Key == "cmls" && item.Descriptor != null)
                {
                    comps = item.Descriptor;
                }
            }

            return new PsdLayerMetadata
            {
                Items = items,
                LayerTime = LayerTimeOf(custom),
                CompLayerId = CompLayerIdOf(comps),
                OriginalEffectsReferencePoint = Point(comps == null ? null : comps.GetObject("origFXRefPoint")),
                CompStates = comps == null ? new List<PsdLayerCompState>() : CompStatesOf(comps),
            };
        }

        private static Descriptor ReadDescriptor(byte[] data)
        {
            if (data == null || data.Length < 12 || ((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]) != 16)
            {
                return null;
            }

            try
            {
                return Descriptor.ReadVersioned(new BigEndianReader(data));
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        private static int? CompLayerIdOf(Descriptor comps)
        {
            DescriptorValue value;
            if (comps == null || !comps.TryGet("LyrI", out value))
            {
                return null;
            }

            return value.Type == DescriptorValueType.Integer || value.Type == DescriptorValueType.LargeInteger ? (int)value.Integer : (int?)null;
        }

        private static DateTime? LayerTimeOf(Descriptor custom)
        {
            var seconds = custom == null ? double.NaN : custom.GetNumber("layerTime");
            // Plausible Unix times only (1970 to 9999).
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds >= 253402300800.0)
            {
                return null;
            }

            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Math.Round(seconds * 1000.0));
        }

        private static List<PsdLayerCompState> CompStatesOf(Descriptor comps)
        {
            var states = new List<PsdLayerCompState>();
            var entries = comps.GetList("layerSettings");
            if (entries == null)
            {
                return states;
            }

            foreach (var item in entries)
            {
                var entry = item == null ? null : item.Object;
                if (entry == null)
                {
                    continue;
                }

                var state = new PsdLayerCompState();
                var ids = entry.GetList("compList");
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        var value = id.AsNumber();
                        if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= int.MinValue && value <= int.MaxValue)
                        {
                            state.CompIds.Add((int)value);
                        }
                    }
                }

                DescriptorValue visible;
                state.Visible = entry.TryGet("enab", out visible) && visible.Type == DescriptorValueType.Boolean ? (bool?)visible.Boolean : null;
                var blend = entry.GetObject("blendOptions");
                state.Offset = Point(entry.GetObject("Ofst"));
                state.VectorMaskOffset = Point(entry.GetObject("vectorMask") == null ? null : entry.GetObject("vectorMask").GetObject("Ofst"));
                state.EffectsReferencePoint = Point(entry.GetObject("FXRefPoint"));
                var mode = blend == null ? null : blend.GetEnum("Md  ");
                state.BlendMode = mode != null ? (PsdBlendMode?)BlendModeKeys.FromDescriptorEnum(mode) : null;
                state.Opacity = Percent(blend, "Opct");
                state.FillOpacity = Percent(blend, "fillOpacity");
                state.Effects = entry.GetObject("Lefx");
                state.Descriptor = entry;
                states.Add(state);
            }

            return states;
        }

        private static PsdPoint? Point(Descriptor point)
        {
            if (point == null)
            {
                return null;
            }

            var x = point.GetNumber("Hrzn");
            var y = point.GetNumber("Vrtc");
            return !double.IsNaN(x) && !double.IsInfinity(x) && !double.IsNaN(y) && !double.IsInfinity(y) ? (PsdPoint?)new PsdPoint(x, y) : null;
        }

        private static double? Percent(Descriptor descriptor, string key)
        {
            var value = descriptor == null ? double.NaN : descriptor.GetNumber(key);
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : Compat.Clamp(value / 100.0, 0, 1);
        }
    }
}