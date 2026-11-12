using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>
    /// Parses the PSD file structure down to layer records and their tagged
    /// blocks, then extracts the layer-effects descriptor (lfx2/lfxs/lmfx, or the
    /// Photoshop 5 lrFX block) and projects it into <see cref="PsdLayerStyle"/>.
    /// Ported from XRay.Psd's PsdParser.cs, minus all pixel decoding.
    /// </summary>
    internal static class PsdParser
    {
        /// <summary>PSB tagged blocks whose length field is 64-bit.</summary>
        private static readonly HashSet<string> WideKeys = new HashSet<string>
        {
            "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD", "cinf",
        };

        public static PsdDocument Parse(byte[] data, PsdLoadOptions options = null)
        {
            options = options ?? new PsdLoadOptions();
            var reader = new BigEndianReader(data);
            var document = new PsdDocument();
            document.ColorManagementEnabled = options.ColorManagement;
            try
            {
                ReadHeader(reader, document);
                ReadColorModeData(reader, document);
                ReadImageResources(reader, document);
                ReadLayerAndMaskInfo(reader, document, options);

                // Everything after the layer and mask section is the merged image data.
                if (reader.Remaining > 0)
                {
                    document.MergedImage = new ByteRange(reader.ReadBytes(reader.Remaining));
                }

                // Type layers merge with the document Txt2 block once everything is loaded.
                TextEngineResolver.Apply(document);
            }
            catch (PsdFormatException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PsdFormatException("Damaged PSD structure: " + ex.Message, ex);
            }

            return document;
        }

        private static void ReadHeader(BigEndianReader reader, PsdDocument document)
        {
            if (reader.Remaining < 26)
            {
                throw new PsdFormatException("File too small for a PSD header.");
            }

            var signature = reader.ReadSignature();
            if (signature != "8BPS")
            {
                throw new PsdFormatException(string.Format("Not a PSD file: signature '{0}'.", signature));
            }

            var version = reader.ReadUInt16();
            if (version != 1 && version != 2)
            {
                throw new PsdFormatException(string.Format("Unsupported PSD version {0}.", version));
            }

            document.IsLargeDocument = version == 2;
            reader.Skip(6); // reserved
            document.Channels = reader.ReadUInt16();
            document.Height = (int)reader.ReadUInt32();
            document.Width = (int)reader.ReadUInt32();
            document.BitsPerChannel = reader.ReadUInt16();
            document.ColorMode = (PsdColorMode)reader.ReadUInt16();
            document.GlobalLightAngle = 120;
            document.GlobalLightAltitude = 30;
        }

        private static void ReadColorModeData(BigEndianReader reader, PsdDocument document)
        {
            var length = reader.ReadUInt32();
            if (length > (uint)reader.Remaining)
            {
                throw new PsdFormatException(string.Format("Color mode data length {0} exceeds the file.", length));
            }

            // Kept as-is: the duotone specification lives here, and indexed
            // documents carry their color table in this section.
            document.ColorModeData = reader.ReadBytes((int)length);
            if (document.ColorMode == PsdColorMode.Indexed && length >= 768)
            {
                var span = document.ColorModeData;
                var palette = new PsdColor[256];
                for (var i = 0; i < 256; i++)
                {
                    palette[i] = new PsdColor(span[i], span[256 + i], span[512 + i]);
                }

                document.Palette = palette;
            }
        }

        private static void ReadImageResources(BigEndianReader reader, PsdDocument document)
        {
            var length = reader.ReadLength(false);
            var resources = reader.Slice(length);
            var collected = new List<ImageResource>();
            while (resources.Remaining >= 12)
            {
                var signature = resources.ReadSignature();
                if (signature != "8BIM" && signature != "MeSa" && signature != "AgHg" && signature != "PHUT" && signature != "DCSR")
                {
                    break;
                }

                var id = resources.ReadUInt16();
                var name = resources.ReadPascalString(2);
                var size = resources.ReadUInt32();
                if (size > (uint)resources.Remaining)
                {
                    break;
                }

                var data = resources.Slice((int)size);

                // Resource bodies are padded to an even length.
                if ((size & 1) != 0 && resources.Remaining > 0)
                {
                    resources.Skip(1);
                }

                // Every block is kept raw; the typed views parse them on demand.
                var payload = data.ReadBytes(data.Length);
                collected.Add(new ImageResource(id, name, payload));
                var block = new BigEndianReader(payload);

                // 1037 = global light angle, 1049 = global light altitude. They feed
                // every effect that has "Use Global Light" set.
                if (block.Length < 4)
                {
                    continue;
                }

                if (id == 1037)
                {
                    document.GlobalLightAngle = ClampAngle(block.ReadInt32());
                }
                else if (id == 1049)
                {
                    document.GlobalLightAltitude = ClampAngle(block.ReadInt32());
                }
            }

            document.ImageResources = collected;
        }

        private static int ClampAngle(int value)
        {
            return value < -360 ? -360 : value > 360 ? 360 : value;
        }

        private static void ReadLayerAndMaskInfo(BigEndianReader reader, PsdDocument document, PsdLoadOptions options)
        {
            var large = document.IsLargeDocument;
            if (reader.Remaining < (large ? 8 : 4))
            {
                return;
            }

            var length = reader.ReadLength(large);
            if (length == 0)
            {
                return;
            }

            var section = reader.Slice(length);
            if (section.Remaining < (large ? 8 : 4))
            {
                return;
            }

            var layerInfoLength = section.ReadLength(large);
            var layerInfo = section.Slice(layerInfoLength);
            var records = options.ReadLayers ? ReadLayerInfo(layerInfo, document) : new List<PsdLayer>();
            document.Layers = records;

            // The global layer mask info and the document-level tagged blocks follow.
            if (section.Remaining >= 4)
            {
                var maskLength = section.ReadUInt32();
                if (maskLength <= (uint)section.Remaining)
                {
                    section.Skip((int)maskLength);
                }
            }

            var blocks = ReadTaggedBlocks(section, large, section.Length);
            document.GlobalTaggedBlocks = blocks;
            document.LinkedFiles = options.ReadLinkedFiles
                ? ReadLinkedFiles(blocks, large)
                : new List<LinkedFile>();

            // 16/32-bit documents keep their layers in a global Lr16/Lr32/Layr block.
            if (options.ReadLayers && records.Count == 0)
            {
                foreach (var block in blocks)
                {
                    if (block.Key != "Lr16" && block.Key != "Lr32" && block.Key != "Layr")
                    {
                        continue;
                    }

                    var blockReader = new BigEndianReader(block.Data);
                    records = ReadLayerInfo(blockReader, document);
                    if (records.Count > 0)
                    {
                        document.Layers = records;
                        break;
                    }
                }
            }
        }

        private static List<PsdLayer> ReadLayerInfo(BigEndianReader reader, PsdDocument document)
        {
            var records = new List<PsdLayer>();
            if (reader.Remaining < 2)
            {
                return records;
            }

            var rawCount = reader.ReadInt16();
            document.MergedImageHasTransparency |= rawCount < 0;
            var count = rawCount < 0 ? -rawCount : rawCount;
            if (count > 4096)
            {
                throw new PsdFormatException(string.Format("Layer count {0} is out of range.", count));
            }

            var lengthsPerLayer = new List<long[]>(count);
            for (var i = 0; i < count; i++)
            {
                if (reader.Remaining < 2)
                {
                    break;
                }

                var record = ReadLayerRecord(reader, document);
                record.Index = i;
                records.Add(record);
                lengthsPerLayer.Add(record.ChannelLengths);
            }

            // Channel image data follows all records, in record and channel order.
            for (var i = 0; i < records.Count; i++)
            {
                var layer = records[i];
                var lengths = lengthsPerLayer[i];
                for (var c = 0; c < lengths.Length && c < layer._channels.Count; c++)
                {
                    var available = (int)Math.Min(lengths[c], reader.Remaining);
                    var placeholder = layer._channels[c];
                    layer._channels[c] = new LayerChannel(placeholder.Id, reader.ReadBytes(available));
                }
            }

            BuildTree(document, records);
            // Channel image data follows the records; it is skipped with the layer info.
            return records;
        }

        private static PsdLayer ReadLayerRecord(BigEndianReader reader, PsdDocument document)
        {
            var large = document.IsLargeDocument;
            var layer = new PsdLayer();
            var top = reader.ReadInt32();
            var left = reader.ReadInt32();
            var bottom = reader.ReadInt32();
            var right = reader.ReadInt32();
            layer.Bounds = right < left || bottom < top ? default(PsdRect) : new PsdRect(left, top, right, bottom);

            var channelCount = reader.ReadUInt16();
            if (channelCount > 56)
            {
                throw new PsdFormatException(string.Format("Layer has {0} channels.", channelCount));
            }

            // Placeholders carry the channel IDs; the payloads are filled in after all records.
            var lengths = new long[channelCount];
            var placeholders = new List<LayerChannel>(channelCount);
            for (var i = 0; i < channelCount; i++)
            {
                placeholders.Add(new LayerChannel((short)reader.ReadInt16(), null));
                lengths[i] = large ? (long)reader.ReadUInt64() : reader.ReadUInt32();
            }

            layer.Document = document;
            layer._channels = placeholders;
            layer.ChannelLengths = lengths;

            var signature = reader.ReadSignature();
            if (signature != "8BIM" && signature != "8B64")
            {
                throw new PsdFormatException(string.Format("Invalid layer blend signature '{0}'.", signature));
            }

            layer.BlendMode = BlendModeKeys.FromKey(reader.ReadSignature());
            layer.Opacity = reader.ReadByte();
            layer.IsClipped = reader.ReadByte() != 0;
            var flags = reader.ReadByte();
            layer.TransparencyProtected = (flags & 0x01) != 0;
            layer.IsVisible = (flags & 0x02) == 0;
            reader.Skip(1); // filler

            var extra = reader.Slice(reader.ReadUInt32());
            if (extra.Remaining >= 4)
            {
                ReadMaskData(extra.Slice(extra.ReadUInt32()), layer);
            }

            if (extra.Remaining >= 4)
            {
                var rangeLength = extra.ReadUInt32();
                if (rangeLength <= (uint)extra.Remaining)
                {
                    layer.BlendingRanges = extra.ReadBytes((int)rangeLength);
                }
            }

            if (extra.Remaining >= 1)
            {
                layer.Name = extra.ReadPascalString(4);
            }

            var blocks = ReadTaggedBlocks(extra, large, extra.Length);
            layer.Blocks = blocks;
            ApplyTaggedBlocks(layer, blocks, document);
            return layer;
        }

        private const long MaxDimensionPsb = 300000;

    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static readonly HashSet<string> AdjustmentKeys = new HashSet<string>
        {
            "levl", "curv", "brit", "blnc", "blwh", "hue ", "hue2", "selc", "mixr", "grdm",
            "phfl", "expA", "vibA", "nvrt", "thrs", "post", "clrL",
        };

        private static readonly HashSet<string> FillKeys = new HashSet<string> { "SoCo", "GdFl", "PtFl" };

        private static PsdRect SafeRect(int left, int top, int right, int bottom)
        {
            if (right < left || bottom < top || (long)right - left > MaxDimensionPsb || (long)bottom - top > MaxDimensionPsb)
            {
                return default(PsdRect);
            }

            return new PsdRect(left, top, right, bottom);
        }

        private static void ReadMaskData(BigEndianReader mask, PsdLayer layer)
        {
            if (mask.Remaining < 18)
            {
                return;
            }

            var top = mask.ReadInt32();
            var left = mask.ReadInt32();
            var bottom = mask.ReadInt32();
            var right = mask.ReadInt32();
            var defaultColor = mask.ReadByte();
            var flags = mask.ReadByte();
            var fromRendering = (flags & 0x08) != 0;

            LayerMask realUser = null;
            // The 36+ byte form carries the real user mask fields right after the flags,
            // ahead of any mask parameters (Photoshop's actual order).
            var parametersOnly = (flags & 0x18) == 0x10;
            if (mask.Length >= 36 && !parametersOnly && mask.Remaining >= 18)
            {
                var realFlags = mask.ReadByte();
                var realDefault = mask.ReadByte();
                var rt = mask.ReadInt32();
                var rl = mask.ReadInt32();
                var rb = mask.ReadInt32();
                var rr = mask.ReadInt32();
                realUser = new LayerMask
                {
                    Bounds = SafeRect(rl, rt, rr, rb),
                    DefaultColor = realDefault,
                    Disabled = (realFlags & 0x02) != 0,
                    Linked = (realFlags & 0x01) == 0,
                    ChannelId = -3,
                };
            }
            else if (mask.Remaining >= 2 && mask.Length == 20 && (flags & 0x10) == 0)
            {
                // The plain 20-byte form ends in two pad bytes. With mask parameters
                // (flag bit 4) those two bytes are the parameter flags and a density
                // byte instead (density-only and sdensity-60 corpus files).
                mask.Skip(2);
            }

            byte userDensity = 255;
            double userFeather = 0;
            byte vectorDensity = 255;
            double vectorFeather = 0;
            if ((flags & 0x10) != 0 && mask.Remaining >= 1)
            {
                var parameterFlags = mask.ReadByte();
                if ((parameterFlags & 0x01) != 0 && mask.Remaining >= 1)
                {
                    userDensity = mask.ReadByte();
                }

                if ((parameterFlags & 0x02) != 0 && mask.Remaining >= 8)
                {
                    userFeather = mask.ReadDouble();
                }

                if ((parameterFlags & 0x04) != 0 && mask.Remaining >= 1)
                {
                    vectorDensity = mask.ReadByte();
                }

                if ((parameterFlags & 0x08) != 0 && mask.Remaining >= 8)
                {
                    vectorFeather = mask.ReadDouble();
                }
            }

            layer.VectorMaskDensity = vectorDensity;
            layer.VectorMaskFeather = SafeFeather(vectorFeather);

            var primary = new LayerMask
            {
                Bounds = SafeRect(left, top, right, bottom),
                DefaultColor = defaultColor,
                Disabled = (flags & 0x02) != 0,
                Linked = (flags & 0x01) == 0,
                FromRendering = fromRendering,
                ChannelId = -2,
                Density = fromRendering ? vectorDensity : userDensity,
                Feather = SafeFeather(fromRendering ? vectorFeather : userFeather),
            };

            if (realUser != null)
            {
                // Raster mask plus a parameterized vector mask: -3 is the painted mask and
                // -2 only Photoshop's combined render of both, so the vector mask is
                // rasterized from its path instead.
                layer.Mask = new LayerMask
                {
                    Bounds = realUser.Bounds,
                    DefaultColor = realUser.DefaultColor,
                    Disabled = realUser.Disabled,
                    Linked = realUser.Linked,
                    ChannelId = -3,
                    Density = userDensity,
                    Feather = SafeFeather(userFeather),
                };
            }
            else if (fromRendering)
            {
                layer.RenderedVectorMask = primary;
            }
            else
            {
                layer.Mask = primary;
            }
        }

        private static double SafeFeather(double f)
        {
            return !double.IsNaN(f) && !double.IsInfinity(f) && f > 0 && f <= 1000 ? f : 0;
        }

        private static void BuildTree(PsdDocument document, List<PsdLayer> records)
        {
            // Records run bottom to top: a bounding divider opens a group's child list,
            // and the folder record above its children closes it.
            var stack = new Stack<List<PsdLayer>>();
            var current = new List<PsdLayer>();
            foreach (var record in records)
            {
                if (record.SectionType == PsdSectionType.BoundingDivider)
                {
                    stack.Push(current);
                    current = new List<PsdLayer>();
                    continue;
                }

                if (record.Kind == PsdLayerKind.Group)
                {
                    foreach (var child in current)
                    {
                        record.AddChild(child);
                    }

                    current = stack.Count > 0 ? stack.Pop() : new List<PsdLayer>();
                    current.Add(record);
                    document.RootLayersInternal.Add(record);
                    continue;
                }

                current.Add(record);
                document.RootLayersInternal.Add(record);
            }

            // Unbalanced dividers: flatten whatever is still open into the root.
            while (stack.Count > 0)
            {
                var outer = stack.Pop();
                outer.AddRange(current);
                current = outer;
            }

            document.RootLayers = current;
        }

        private static List<TaggedBlock> ReadTaggedBlocks(BigEndianReader reader, bool large, int end)
        {
            var blocks = new List<TaggedBlock>();
            while (end - reader.Position >= 12)
            {
                var start = reader.Position;
                var signature = reader.ReadSignature();
                if (signature != "8BIM" && signature != "8B64")
                {
                    reader.Position = start;
                    break;
                }

                var key = reader.ReadSignature();
                var wide = signature == "8B64" || (large && WideKeys.Contains(key));
                if (wide && reader.Remaining < 8)
                {
                    break;
                }

                ulong length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                if (length > (ulong)reader.Remaining)
                {
                    break;
                }

                blocks.Add(new TaggedBlock(key, reader.ReadBytes((long)length)));

                // Global blocks are padded to four bytes, layer blocks normally carry
                // their padding inside the length. Skip up to three pad bytes when
                // the next signature does not start right away.
                if (!StartsWithSignature(reader, end))
                {
                    for (var pad = 1; pad <= 3 && reader.Position + pad <= end; pad++)
                    {
                        reader.Position += pad;
                        if (StartsWithSignature(reader, end))
                        {
                            break;
                        }

                        reader.Position -= pad;
                    }
                }
            }

            return blocks;
        }

        private static bool StartsWithSignature(BigEndianReader reader, int end)
        {
            if (end - reader.Position < 4)
            {
                return false;
            }

            // Position is validated against Remaining by the signature read itself.
            var bytes = reader.ReadBytes(4);
            reader.Position -= 4;
            return bytes[0] == (byte)'8' && bytes[1] == (byte)'B' && ((bytes[2] == (byte)'I' && bytes[3] == (byte)'M') || (bytes[2] == (byte)'6' && bytes[3] == (byte)'4'));
        }

        private static void ApplyTaggedBlocks(PsdLayer layer, List<TaggedBlock> blocks, PsdDocument document)
        {
            Descriptor effects = null;
            var sawMultiEffects = false;
            foreach (var block in blocks)
            {
                var data = block.Data;
                switch (block.Key)
                {
                    // 'luni' carries the real Unicode layer name; the record's Pascal name
                    // is a legacy 8-bit fallback (mojibake for non-Latin text).
                    case "luni":
                        if (data.Length >= 4)
                        {
                            try
                            {
                                var name = new BigEndianReader(data).ReadUnicodeString();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    layer.Name = name;
                                }
                            }
                            catch (PsdFormatException)
                            {
                            }
                        }

                        break;
                    case "lyid":
                        if (data.Length >= 4)
                        {
                            layer.Id = new BigEndianReader(data).ReadUInt32();
                        }

                        break;
                    case "iOpa":
                        if (data.Length >= 1)
                        {
                            layer.FillOpacity = data[0];
                        }

                        break;
                    case "infx":
                        if (data.Length >= 1)
                        {
                            layer.BlendInteriorElements = data[0] != 0;
                        }

                        break;
                    case "clbl":
                        if (data.Length >= 1)
                        {
                            layer.BlendClippedElements = data[0] != 0;
                        }

                        break;
                    case "lsct":
                    case "lsdk":
                        {
                            var section = new BigEndianReader(data);
                            if (section.Remaining >= 4)
                            {
                                var type = section.ReadUInt32();
                                layer.SectionType = type <= 3 ? (PsdSectionType)type : PsdSectionType.None;
                                if (section.Remaining >= 8 && section.ReadSignature() == "8BIM")
                                {
                                    var mode = BlendModeKeys.FromKey(section.ReadSignature());
                                    if (layer.SectionType == PsdSectionType.OpenFolder || layer.SectionType == PsdSectionType.ClosedFolder)
                                    {
                                        layer.BlendMode = mode;
                                    }
                                }
                            }

                            break;
                        }

                    case "vmsk":
                    case "vsms":
                        if (layer.VectorMask == null)
                        {
                            layer.VectorMask = VectorPath.ParseVectorMask(data, document.Width, document.Height);
                        }

                        break;
                    case "vstk":
                        if (layer.VectorStroke == null)
                        {
                            layer.VectorStroke = VectorStrokeStyle.Parse(data);
                        }

                        break;
                    case "TySh":
                        if (layer.Text == null)
                        {
                            layer.Text = TextLayerInfo.Parse(data, document.Colors);
                            if (layer.Text != null)
                            {
                                layer.Kind = PsdLayerKind.Text;
                            }
                        }

                        break;
                    case "tySh":
                        if (layer.Text == null)
                        {
                            layer.Text = LegacyText.Parse(data, document.Colors);
                            if (layer.Text != null)
                            {
                                layer.Kind = PsdLayerKind.Text;
                            }
                        }

                        break;
                    case "lfx2":
                    case "lfxs":
                    case "lmfx":
                        if (data.Length >= 8)
                        {
                            try
                            {
                                // Object-effects version u32 (0), descriptor version u32 (16),
                                // descriptor. 'lmfx' (multiple instances) is authoritative
                                // over the compatibility lfx2 written beside it.
                                var trimmed = new byte[data.Length - 4];
                                Array.Copy(data, 4, trimmed, 0, trimmed.Length);
                                var parsed = Descriptor.ReadVersioned(new BigEndianReader(trimmed)).AttachColors(document.Colors);
                                if (block.Key == "lmfx" || !sawMultiEffects)
                                {
                                    sawMultiEffects |= block.Key == "lmfx";
                                    effects = parsed;
                                }
                            }
                            catch (PsdFormatException)
                            {
                            }
                        }

                        break;
                    case "SoLd":
                    case "SoLE":
                        {
                            // 'soLD' identifier, version u32, then a versioned descriptor.
                            var placed = TryReadDescriptor(data, 8, document.Colors);
                            if (placed != null)
                            {
                                layer.SmartObject = placed;
                                layer.SmartObjectFileId = placed.GetString("Idnt") ?? layer.SmartObjectFileId;
                                if (layer.Kind != PsdLayerKind.Text)
                                {
                                    layer.Kind = PsdLayerKind.SmartObject;
                                }
                            }

                            break;
                        }

                    case "PlLd":
                        {
                            if (layer.SmartObjectFileId == null && data.Length > 12)
                            {
                                try
                                {
                                    var placed = new BigEndianReader(data);
                                    placed.Skip(8); // 'plcL', version
                                    layer.SmartObjectFileId = placed.ReadPascalString(1);
                                }
                                catch (PsdFormatException)
                                {
                                    // Ignore malformed legacy placement data.
                                }
                            }

                            if (layer.Kind != PsdLayerKind.Text)
                            {
                                layer.Kind = PsdLayerKind.SmartObject;
                            }

                            break;
                        }

                    default:
                        if (FillKeys.Contains(block.Key))
                        {
                            layer.Kind = PsdLayerKind.Fill;
                            layer.ContentKey = block.Key;
                            layer.FillDescriptor = TryReadDescriptor(data, 0, document.Colors);
                            if (block.Key == "SoCo" && layer.FillDescriptor != null)
                            {
                                layer.FillColor = layer.FillDescriptor.GetColor("Clr ");
                            }
                        }
                        else if (AdjustmentKeys.Contains(block.Key))
                        {
                            layer.Kind = PsdLayerKind.Adjustment;
                            layer.ContentKey = block.Key;
                        }

                        break;
                }
            }

            // A legacy 'vscg' block on a shape (vector mask plus vstk) without SoCo/GdFl/PtFl
            // is the shape's paint: the fill when vstk enables filling, else the stroke's
            // fallback paint. Either way the layer is a shape and renders from its path.
            if (layer.Kind == PsdLayerKind.Pixel && layer.VectorMask != null && layer.VectorStroke != null)
            {
                var vscg = layer.GetTaggedBlock("vscg");
                if (vscg != null && vscg.Length > 8)
                {
                    var key = Latin1.GetString(vscg, 0, 4);
                    if (FillKeys.Contains(key))
                    {
                        var paint = TryReadDescriptor(vscg, 4, document.Colors);
                        if (paint != null && layer.VectorStroke.FillEnabled)
                        {
                            layer.Kind = PsdLayerKind.Fill;
                            layer.ContentKey = key;
                            layer.FillDescriptor = paint;
                            layer.FillColor = key == "SoCo" ? paint.GetColor("Clr ") : (PsdColor?)null;
                        }
                    }
                }
            }

            // Photoshop 5 effects (lrFX) speak only for layers without a descriptor
            // effects block: beside lfx2/lfxs/lmfx they are a lossy compatibility mirror.
            if (effects == null)
            {
                foreach (var block in blocks)
                {
                    if (block.Key == "lrFX")
                    {
                        var translated = LegacyEffects.ToDescriptor(block.Data);
                        if (translated != null)
                        {
                            layer.Style = PsdLayerStyle.FromLegacy(translated, document.GlobalLightAngle, document.GlobalLightAltitude);
                            if (layer.SectionType == PsdSectionType.OpenFolder || layer.SectionType == PsdSectionType.ClosedFolder)
                            {
                                layer.Kind = PsdLayerKind.Group;
                            }

                            return;
                        }

                        break;
                    }
                }
            }
            else
            {
                layer.Style = PsdLayerStyle.Parse(effects, document.GlobalLightAngle, document.GlobalLightAltitude);
            }

            if (layer.SectionType == PsdSectionType.OpenFolder || layer.SectionType == PsdSectionType.ClosedFolder)
            {
                layer.Kind = PsdLayerKind.Group;
            }
        }

        /// <summary>
        /// The linked and embedded smart-object sources of the lnk2/lnkD/lnk3/lnkE
        /// blocks: a list of 8-byte-length elements, each "liFD"/"liFE"/"liFA" data.
        /// </summary>
        private static List<LinkedFile> ReadLinkedFiles(List<TaggedBlock> blocks, bool large)
        {
            var files = new List<LinkedFile>();
            foreach (var block in blocks)
            {
                if (block.Key != "lnk2" && block.Key != "lnkD" && block.Key != "lnk3" && block.Key != "lnkE")
                {
                    continue;
                }

                var reader = new BigEndianReader(block.Data);
                while (reader.Remaining >= 8)
                {
                    Cancellation.Check();
                    try
                    {
                        var length = (long)reader.ReadUInt64();
                        if (length <= 0 || length > reader.Remaining)
                        {
                            break;
                        }

                        var element = reader.Slice(length);
                        var padding = (int)((4 - (length % 4)) % 4);
                        reader.Skip(Math.Min(padding, reader.Remaining));
                        var kind = element.ReadSignature();
                        var version = element.ReadUInt32();
                        if ((kind != "liFD" && kind != "liFE" && kind != "liFA") || version < 1 || version > 32)
                        {
                            continue;
                        }

                        var id = element.ReadPascalString(1);
                        var fileName = element.ReadUnicodeString();
                        var fileType = element.ReadSignature();
                        element.ReadSignature(); // creator
                        var size = (long)element.ReadUInt64();
                        if (element.ReadByte() != 0)
                        {
                            Descriptor.ReadVersioned(element);
                        }

                        byte[] payload = new byte[0];
                        if (kind == "liFD" && size >= 0 && size <= element.Remaining)
                        {
                            payload = element.ReadBytes(size);
                        }

                        files.Add(new LinkedFile(id, fileName, fileType.TrimEnd('\0', ' '), kind, payload));
                    }
                    catch (PsdFormatException)
                    {
                        break;
                    }
                }
            }

            return files;
        }

        /// <summary>Reads a u32-versioned descriptor after skipping prefix bytes; null when it fails to parse. Colors of a document's descriptors convert through <paramref name="colors"/>.</summary>
        internal static Descriptor TryReadDescriptor(byte[] data, int skip, DocumentColors colors = null)
        {
            try
            {
                var reader = new BigEndianReader(data);
                reader.Skip(skip);
                var descriptor = Descriptor.ReadVersioned(reader);
                return colors != null ? descriptor.AttachColors(colors) : descriptor;
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }
    }
}
