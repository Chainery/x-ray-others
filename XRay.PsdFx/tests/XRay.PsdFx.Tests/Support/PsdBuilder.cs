using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XRay.PsdFx.Tests.Support
{
    /// <summary>A layer for <see cref="PsdBuilder"/>. Channel planes hold raw big-endian samples at the document depth.</summary>
    internal sealed class BuilderLayer
    {
        public BuilderLayer()
        {
            Name = "Layer";
            BlendKey = "norm";
            Opacity = 255;
            Channels = new SortedDictionary<short, byte[]>();
            Blocks = new List<KeyValuePair<string, byte[]>>();
        }

        public string Name { get; set; }

        public PsdRect Rect { get; set; }

        public string BlendKey { get; set; }

        public byte Opacity { get; set; }

        public bool Clipped { get; set; }

        public bool Hidden { get; set; }

        /// <summary>Section divider type for groups: 0 none, 1 open folder, 3 bounding divider.</summary>
        public int SectionType { get; set; }

        public string SectionBlendKey { get; set; }

        public SortedDictionary<short, byte[]> Channels { get; private set; }

        public List<KeyValuePair<string, byte[]>> Blocks { get; private set; }

        public PsdRect? MaskRect { get; set; }

        public byte MaskDefault { get; set; }

        public byte MaskFlags { get; set; }

        /// <summary>Bytes after the mask flags (mask parameters with flag 0x10); two pad bytes when null.</summary>
        public byte[] MaskTail { get; set; }

        /// <summary>Raw layer blending ranges ("Blend If"); empty when null.</summary>
        public byte[] BlendingRanges { get; set; }

        public void AddBlock(string key, byte[] data)
        {
            Blocks.Add(new KeyValuePair<string, byte[]>(key, data));
        }
    }

    /// <summary>
    /// Writes minimal but valid PSD/PSB files for tests: any depth and color mode,
    /// all four channel compressions, layers in the standard section or (16/32-bit)
    /// in the Lr16/Lr32 global block, and arbitrary resources and tagged blocks.
    /// Direct net45 port of the original test support builder.
    /// </summary>
    internal sealed class PsdBuilder
    {
        public PsdBuilder()
        {
            Width = 4;
            Height = 4;
            Depth = 8;
            Mode = PsdColorMode.Rgb;
            LayerCompression = PsdCompression.Rle;
            MergedCompression = PsdCompression.Rle;
            MergedChannels = new List<byte[]>();
            DeepLayersInGlobalBlock = true;
            ColorModeData = new byte[0];
            Resources = new List<ResourceEntry>();
            Layers = new List<BuilderLayer>();
            GlobalBlocks = new List<KeyValuePair<string, byte[]>>();
        }

        public int Width { get; set; }

        public int Height { get; set; }

        public int Depth { get; set; }

        public PsdColorMode Mode { get; set; }

        public bool Large { get; set; }

        public PsdCompression LayerCompression { get; set; }

        public PsdCompression MergedCompression { get; set; }

        /// <summary>Merged image channel planes (raw big-endian samples), in channel order.</summary>
        public List<byte[]> MergedChannels { get; private set; }

        public bool MergedTransparency { get; set; }

        /// <summary>Puts 16/32-bit layers into the Lr16/Lr32 global block like Photoshop does.</summary>
        public bool DeepLayersInGlobalBlock { get; set; }

        public byte[] ColorModeData { get; set; }

        public List<ResourceEntry> Resources { get; private set; }

        public List<BuilderLayer> Layers { get; private set; }

        public List<KeyValuePair<string, byte[]>> GlobalBlocks { get; private set; }

        /// <summary>Channel count Photoshop writes for the color mode (before alpha planes).</summary>
        private int ModeChannels()
        {
            switch (Mode)
            {
                case PsdColorMode.Rgb:
                case PsdColorMode.Lab:
                    return 3;
                case PsdColorMode.Cmyk:
                    return 4;
                default:
                    return 1;
            }
        }

        public int RowBytes
        {
            get
            {
                switch (Depth)
                {
                    case 1:
                        return (Width + 7) / 8;
                    default:
                        return Width * Depth / 8;
                }
            }
        }

        internal sealed class ResourceEntry
        {
            public ResourceEntry(ushort id, string name, byte[] data)
            {
                Id = id;
                Name = name;
                Data = data;
            }

            public ushort Id { get; private set; }

            public string Name { get; private set; }

            public byte[] Data { get; private set; }
        }

        public static byte[] Plane8(int width, int height, Func<int, int, byte> sample)
        {
            var data = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    data[(y * width) + x] = sample(x, y);
                }
            }

            return data;
        }

        public static byte[] Plane8(int width, int height, byte value)
        {
            return Plane8(width, height, delegate { return value; });
        }

        public static byte[] Plane16(int width, int height, Func<int, int, ushort> sample)
        {
            var data = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = sample(x, y);
                    var offset = ((y * width) + x) * 2;
                    data[offset] = (byte)(value >> 8);
                    data[offset + 1] = (byte)value;
                }
            }

            return data;
        }

        public static byte[] Plane32(int width, int height, Func<int, int, float> sample)
        {
            var data = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var bytes = BitConverter.GetBytes(sample(x, y));
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(bytes);
                    }

                    var offset = ((y * width) + x) * 4;
                    Array.Copy(bytes, 0, data, offset, 4);
                }
            }

            return data;
        }

        public byte[] Build()
        {
            var output = new Writer();
            output.Ascii("8BPS");
            output.U16((ushort)(Large ? 2 : 1));
            output.Zeros(6);
            output.U16((ushort)Math.Max(Math.Max(1, MergedChannels.Count), ModeChannels()));
            output.U32((uint)Height);
            output.U32((uint)Width);
            output.U16((ushort)Depth);
            output.U16((ushort)Mode);

            output.U32((uint)ColorModeData.Length);
            output.Bytes(ColorModeData);

            var resources = new Writer();
            foreach (var resource in Resources)
            {
                resources.Ascii("8BIM");
                resources.U16(resource.Id);
                resources.Pascal(resource.Name, 2);
                resources.U32((uint)resource.Data.Length);
                resources.Bytes(resource.Data);
                if (resource.Data.Length % 2 != 0)
                {
                    resources.U8(0);
                }
            }

            output.U32((uint)resources.Length);
            output.Bytes(resources.ToArray());

            var layerSection = new Writer();
            var layerInfo = Layers.Count > 0 ? BuildLayerInfo() : new byte[0];
            var deep = Depth > 8 && DeepLayersInGlobalBlock && Layers.Count > 0;
            if (deep)
            {
                Length(layerSection, 0);
            }
            else
            {
                Length(layerSection, layerInfo.Length);
                layerSection.Bytes(layerInfo);
            }

            layerSection.U32(0); // global layer mask info
            if (deep)
            {
                WriteBlock(layerSection, Depth == 16 ? "Lr16" : "Lr32", layerInfo, true);
            }

            foreach (var block in GlobalBlocks)
            {
                WriteBlock(layerSection, block.Key, block.Value, true);
            }

            Length(output, layerSection.Length);
            output.Bytes(layerSection.ToArray());

            var merged = MergedChannels.Count > 0 ? MergedChannels : new List<byte[]> { new byte[RowBytes * Height] };
            output.U16((ushort)MergedCompression);
            output.Bytes(EncodeMerged(merged));
            return output.ToArray();
        }

        private byte[] BuildLayerInfo()
        {
            var info = new Writer();
            info.I16((short)(MergedTransparency ? -Layers.Count : Layers.Count));
            var encoded = new List<List<byte[]>>();
            foreach (var layer in Layers)
            {
                var channels = new List<byte[]>();
                foreach (var channel in layer.Channels)
                {
                    var rect = channel.Key == -2 && layer.MaskRect.HasValue ? layer.MaskRect.Value : layer.Rect;
                    channels.Add(EncodeLayerChannel(channel.Value, rect.Width, rect.Height));
                }

                encoded.Add(channels);
                info.I32(layer.Rect.Top);
                info.I32(layer.Rect.Left);
                info.I32(layer.Rect.Bottom);
                info.I32(layer.Rect.Right);
                info.U16((ushort)layer.Channels.Count);
                var index = 0;
                foreach (var id in layer.Channels.Keys)
                {
                    info.I16(id);
                    Length(info, channels[index++].Length);
                }

                info.Ascii("8BIM");
                info.Ascii(layer.BlendKey);
                info.U8(layer.Opacity);
                info.U8((byte)(layer.Clipped ? 1 : 0));
                info.U8((byte)(layer.Hidden ? 0x02 : 0x00));
                info.U8(0);

                var extra = new Writer();
                if (layer.MaskRect.HasValue)
                {
                    var maskRect = layer.MaskRect.Value;
                    var tail = layer.MaskTail ?? new byte[2];
                    extra.U32((uint)(18 + tail.Length));
                    extra.I32(maskRect.Top);
                    extra.I32(maskRect.Left);
                    extra.I32(maskRect.Bottom);
                    extra.I32(maskRect.Right);
                    extra.U8(layer.MaskDefault);
                    extra.U8(layer.MaskFlags);
                    extra.Bytes(tail);
                }
                else
                {
                    extra.U32(0);
                }

                var ranges = layer.BlendingRanges ?? new byte[0];
                extra.U32((uint)ranges.Length);
                extra.Bytes(ranges);
                extra.Pascal(layer.Name, 4);

                var unicode = new Writer();
                unicode.UnicodeString(layer.Name);
                WriteBlock(extra, "luni", unicode.ToArray(), false);

                if (layer.SectionType != 0)
                {
                    var section = new Writer();
                    section.U32((uint)layer.SectionType);
                    if (layer.SectionBlendKey != null)
                    {
                        section.Ascii("8BIM");
                        section.Ascii(layer.SectionBlendKey);
                    }

                    WriteBlock(extra, "lsct", section.ToArray(), false);
                }

                foreach (var block in layer.Blocks)
                {
                    WriteBlock(extra, block.Key, block.Value, false);
                }

                info.U32((uint)extra.Length);
                info.Bytes(extra.ToArray());
            }

            foreach (var channels in encoded)
            {
                foreach (var channel in channels)
                {
                    info.Bytes(channel);
                }
            }

            if (info.Length % 2 != 0)
            {
                info.U8(0);
            }

            return info.ToArray();
        }

        private void Length(Writer writer, long length)
        {
            if (Large)
            {
                writer.U64((ulong)length);
            }
            else
            {
                writer.U32((uint)length);
            }
        }

        private void WriteBlock(Writer writer, string key, byte[] data, bool global)
        {
            var wide = Large && (key == "Lr16" || key == "Lr32" || key == "Layr" || key == "lnk2");
            writer.Ascii("8BIM");
            writer.Ascii(key);
            var padded = data.Length + (global ? (4 - (data.Length % 4)) % 4 : data.Length % 2);
            if (wide)
            {
                writer.U64((ulong)padded);
            }
            else
            {
                writer.U32((uint)padded);
            }

            writer.Bytes(data);
            writer.Zeros(padded - data.Length);
        }

        private byte[] EncodeLayerChannel(byte[] samples, int width, int height)
        {
            var writer = new Writer();
            writer.U16((ushort)LayerCompression);
            var rowBytes = Depth == 1 ? (width + 7) / 8 : width * Depth / 8;
            switch (LayerCompression)
            {
                case PsdCompression.Raw:
                    writer.Bytes(samples);
                    break;

                case PsdCompression.Rle:
                    {
                        var rows = new List<byte[]>();
                        for (var y = 0; y < height; y++)
                        {
                            rows.Add(PackBits(samples, y * rowBytes, rowBytes));
                        }

                        foreach (var row in rows)
                        {
                            if (Large)
                            {
                                writer.U32((uint)row.Length);
                            }
                            else
                            {
                                writer.U16((ushort)row.Length);
                            }
                        }

                        foreach (var row in rows)
                        {
                            writer.Bytes(row);
                        }

                        break;
                    }

                default:
                    writer.Bytes(Zip(samples, width, height, LayerCompression == PsdCompression.ZipPrediction));
                    break;
            }

            return writer.ToArray();
        }

        private byte[] EncodeMerged(List<byte[]> channels)
        {
            var writer = new Writer();
            switch (MergedCompression)
            {
                case PsdCompression.Raw:
                    foreach (var channel in channels)
                    {
                        writer.Bytes(channel);
                    }

                    break;

                case PsdCompression.Rle:
                    {
                        var rows = new List<byte[]>();
                        foreach (var channel in channels)
                        {
                            for (var y = 0; y < Height; y++)
                            {
                                rows.Add(PackBits(channel, y * RowBytes, RowBytes));
                            }
                        }

                        foreach (var row in rows)
                        {
                            if (Large)
                            {
                                writer.U32((uint)row.Length);
                            }
                            else
                            {
                                writer.U16((ushort)row.Length);
                            }
                        }

                        foreach (var row in rows)
                        {
                            writer.Bytes(row);
                        }

                        break;
                    }

                default:
                    {
                        var joined = new List<byte>();
                        foreach (var channel in channels)
                        {
                            joined.AddRange(channel);
                        }

                        writer.Bytes(Zip(joined.ToArray(), Width, Height * channels.Count, MergedCompression == PsdCompression.ZipPrediction));
                        break;
                    }
            }

            return writer.ToArray();
        }

        /// <summary>zlib (RFC 1950) framed deflate; net45 has no ZLibStream, the reader skips the two-byte header.</summary>
        private byte[] Zip(byte[] samples, int width, int height, bool prediction)
        {
            var data = (byte[])samples.Clone();
            if (prediction)
            {
                ApplyPrediction(data, width, height);
            }

            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x78);
                stream.WriteByte(0x9C);
                using (var deflate = new DeflateStream(stream, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                return stream.ToArray();
            }
        }

        private void ApplyPrediction(byte[] data, int width, int height)
        {
            var rowBytes = width * Depth / 8;
            for (var y = 0; y < height; y++)
            {
                var row = y * rowBytes;
                switch (Depth)
                {
                    case 8:
                        for (var x = rowBytes - 1; x > 0; x--)
                        {
                            data[row + x] = (byte)(data[row + x] - data[row + x - 1]);
                        }

                        break;

                    case 16:
                        for (var x = width - 1; x > 0; x--)
                        {
                            var current = (data[row + (x * 2)] << 8) | data[row + (x * 2) + 1];
                            var previous = (data[row + ((x - 1) * 2)] << 8) | data[row + ((x - 1) * 2) + 1];
                            var value = (ushort)(current - previous);
                            data[row + (x * 2)] = (byte)(value >> 8);
                            data[row + (x * 2) + 1] = (byte)value;
                        }

                        break;

                    case 32:
                        {
                            // Split into byte planes, then delta-code the whole row.
                            var planes = new byte[rowBytes];
                            for (var x = 0; x < width; x++)
                            {
                                for (var b = 0; b < 4; b++)
                                {
                                    planes[(b * width) + x] = data[row + (x * 4) + b];
                                }
                            }

                            for (var i = rowBytes - 1; i > 0; i--)
                            {
                                planes[i] = (byte)(planes[i] - planes[i - 1]);
                            }

                            Array.Copy(planes, 0, data, row, rowBytes);
                            break;
                        }
                }
            }
        }

        /// <summary>PackBits encoder (runs of 3+ become repeat packets).</summary>
        public static byte[] PackBits(byte[] row, int offset, int count)
        {
            var output = new List<byte>();
            var i = offset;
            var end = offset + count;
            while (i < end)
            {
                var run = 1;
                while (i + run < end && run < 128 && row[i + run] == row[i])
                {
                    run++;
                }

                if (run >= 3)
                {
                    output.Add((byte)(sbyte)(1 - run));
                    output.Add(row[i]);
                    i += run;
                    continue;
                }

                var start = i;
                var literal = 0;
                while (i < end && literal < 128)
                {
                    if (i + 2 < end && row[i] == row[i + 1] && row[i] == row[i + 2])
                    {
                        break;
                    }

                    i++;
                    literal++;
                }

                output.Add((byte)(literal - 1));
                for (var k = start; k < start + literal; k++)
                {
                    output.Add(row[k]);
                }
            }

            return output.ToArray();
        }

        /// <summary>Little helper for big-endian output.</summary>
        internal sealed class Writer
        {
            private readonly MemoryStream _stream = new MemoryStream();

            public long Length
            {
                get { return _stream.Length; }
            }

            public byte[] ToArray()
            {
                return _stream.ToArray();
            }

            public void U8(byte value)
            {
                _stream.WriteByte(value);
            }

            public void U16(ushort value)
            {
                _stream.WriteByte((byte)(value >> 8));
                _stream.WriteByte((byte)value);
            }

            public void I16(short value)
            {
                U16(unchecked((ushort)value));
            }

            public void U32(uint value)
            {
                _stream.WriteByte((byte)(value >> 24));
                _stream.WriteByte((byte)(value >> 16));
                _stream.WriteByte((byte)(value >> 8));
                _stream.WriteByte((byte)value);
            }

            public void I32(int value)
            {
                U32(unchecked((uint)value));
            }

            public void U64(ulong value)
            {
                for (var shift = 56; shift >= 0; shift -= 8)
                {
                    _stream.WriteByte((byte)(value >> shift));
                }
            }

            public void F64(double value)
            {
                var bytes = BitConverter.GetBytes(value);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }

                _stream.Write(bytes, 0, 8);
            }

            public void Bytes(byte[] data)
            {
                _stream.Write(data, 0, data.Length);
            }

            public void Zeros(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    _stream.WriteByte(0);
                }
            }

            public void Ascii(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                _stream.Write(bytes, 0, bytes.Length);
            }

            public void Pascal(string text, int padding)
            {
                var bytes = Encoding.GetEncoding(28591).GetBytes(text);
                U8((byte)bytes.Length);
                Bytes(bytes);
                var consumed = bytes.Length + 1;
                var padded = (consumed + padding - 1) / padding * padding;
                Zeros(padded - consumed);
            }

            public void UnicodeString(string text)
            {
                U32((uint)text.Length);
                var bytes = Encoding.BigEndianUnicode.GetBytes(text);
                _stream.Write(bytes, 0, bytes.Length);
            }

            /// <summary>Descriptor ID: 0 + four-char code, or length + ASCII for longer keys.</summary>
            public void DescriptorId(string id)
            {
                if (id.Length == 4)
                {
                    U32(0);
                }
                else
                {
                    U32((uint)id.Length);
                }

                Ascii(id);
            }
        }
    }
}
