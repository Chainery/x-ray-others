using System;
using System.Collections.Generic;

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

        public static PsdDocument Parse(byte[] data)
        {
            var reader = new BigEndianReader(data);
            var document = new PsdDocument();
            try
            {
                ReadHeader(reader, document);
                ReadColorModeData(reader, document);
                ReadImageResources(reader, document);
                ReadLayerAndMaskInfo(reader, document);
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
        }

        private static void ReadImageResources(BigEndianReader reader, PsdDocument document)
        {
            var length = reader.ReadLength(false);
            var resources = reader.Slice(length);
            while (resources.Remaining >= 12)
            {
                var signature = resources.ReadSignature();
                if (signature != "8BIM" && signature != "MeSa" && signature != "AgHg" && signature != "PHUT" && signature != "DCSR")
                {
                    break;
                }

                var id = resources.ReadUInt16();
                resources.ReadPascalString(2);
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

                // 1037 = global light angle, 1049 = global light altitude (ImageResourceIds
                // in XRay.Psd). They feed every effect that has "Use Global Light" set.
                if (data.Length < 4)
                {
                    continue;
                }

                if (id == 1037)
                {
                    document.GlobalLightAngle = ClampAngle(data.ReadInt32());
                }
                else if (id == 1049)
                {
                    document.GlobalLightAltitude = ClampAngle(data.ReadInt32());
                }
            }
        }

        private static int ClampAngle(int value)
        {
            return value < -360 ? -360 : value > 360 ? 360 : value;
        }

        private static void ReadLayerAndMaskInfo(BigEndianReader reader, PsdDocument document)
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
            var records = ReadLayerInfo(layerInfo, document);
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

            // 16/32-bit documents keep their layers in a global Lr16/Lr32/Layr block.
            if (records.Count == 0)
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
            var count = rawCount < 0 ? -rawCount : rawCount;
            if (count > 4096)
            {
                throw new PsdFormatException(string.Format("Layer count {0} is out of range.", count));
            }

            for (var i = 0; i < count; i++)
            {
                if (reader.Remaining < 2)
                {
                    break;
                }

                records.Add(ReadLayerRecord(reader, document));
            }

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

            for (var i = 0; i < channelCount; i++)
            {
                reader.ReadInt16(); // channel id
                if (large)
                {
                    reader.ReadUInt64();
                }
                else
                {
                    reader.ReadUInt32();
                }
            }

            var signature = reader.ReadSignature();
            if (signature != "8BIM" && signature != "8B64")
            {
                throw new PsdFormatException(string.Format("Invalid layer blend signature '{0}'.", signature));
            }

            layer.BlendMode = BlendModeKeys.FromKey(reader.ReadSignature());
            layer.Opacity = reader.ReadByte();
            reader.ReadByte(); // clipping
            var flags = reader.ReadByte();
            layer.IsVisible = (flags & 0x02) == 0;
            reader.Skip(1); // filler

            var extra = reader.Slice(reader.ReadUInt32());
            if (extra.Remaining >= 4)
            {
                extra.Skip((int)extra.ReadUInt32()); // mask data
            }

            if (extra.Remaining >= 4)
            {
                extra.Skip((int)extra.ReadUInt32()); // blending ranges
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
                // 'luni' carries the real Unicode layer name; the record's Pascal name
                // is a legacy 8-bit fallback (mojibake for non-Latin text).
                if (block.Key == "luni" && block.Data.Length >= 4)
                {
                    try
                    {
                        var name = new BigEndianReader(block.Data).ReadUnicodeString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            layer.Name = name;
                        }
                    }
                    catch (PsdFormatException)
                    {
                    }

                    continue;
                }

                if (block.Key != "lfx2" && block.Key != "lfxs" && block.Key != "lmfx")
                {
                    continue;
                }

                if (block.Data.Length < 8)
                {
                    continue;
                }

                try
                {
                    // Object-effects version u32 (0), descriptor version u32 (16), descriptor.
                    // 'lfxs' is the group (layer set) form; 'lmfx' (multiple instances)
                    // is authoritative over the compatibility lfx2 written beside it.
                    var data = new byte[block.Data.Length - 4];
                    Array.Copy(block.Data, 4, data, 0, data.Length);
                    var parsed = Descriptor.ReadVersioned(new BigEndianReader(data));
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
                            return;
                        }

                        break;
                    }
                }

                return;
            }

            layer.Style = PsdLayerStyle.Parse(effects, document.GlobalLightAngle, document.GlobalLightAltitude);
        }
    }
}
