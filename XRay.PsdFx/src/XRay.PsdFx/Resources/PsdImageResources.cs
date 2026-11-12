using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace XRay.PsdFx
{
    /// <summary>
    /// Typed views of the common image resources, parsed on first access. Each
    /// property is null when the document has no such resource or it does not
    /// decode; a damaged resource never throws. The raw blocks stay available in
    /// <see cref="PsdDocument.ImageResources"/>.
    /// </summary>
    public sealed class PsdImageResources
    {
        private readonly PsdDocument _document;
        private readonly Lazy<PsdResolutionInfo> _resolution;
        private readonly Lazy<PsdGridAndGuides> _gridAndGuides;
        private readonly Lazy<PsdThumbnail> _thumbnail;
        private readonly Lazy<PsdSlices> _slices;
        private readonly Lazy<PsdLayerComps> _layerComps;
        private readonly Lazy<PsdPrintScale> _printScale;
        private readonly Lazy<PsdVersionInfo> _versionInfo;
        private readonly Lazy<string> _xmp;
        private readonly Lazy<List<PsdIptcDataSet>> _iptc;
        private readonly Lazy<List<string>> _channelNames;
        private readonly Lazy<double?> _pixelAspectRatio;
        private readonly Lazy<PsdIccProfile> _iccProfile;

        internal PsdImageResources(PsdDocument document)
        {
            _document = document;
            const LazyThreadSafetyMode Mode = LazyThreadSafetyMode.PublicationOnly;
            _resolution = new Lazy<PsdResolutionInfo>(() => Parse(ImageResourceIds.ResolutionInfo, ParseResolution), Mode);
            _gridAndGuides = new Lazy<PsdGridAndGuides>(() => Parse(ImageResourceIds.GridAndGuides, ParseGridAndGuides), Mode);
            _thumbnail = new Lazy<PsdThumbnail>(ParseThumbnail, Mode);
            _slices = new Lazy<PsdSlices>(() => Parse(ImageResourceIds.Slices, ParseSlices), Mode);
            _layerComps = new Lazy<PsdLayerComps>(() => Parse(ImageResourceIds.LayerComps, ParseLayerComps), Mode);
            _printScale = new Lazy<PsdPrintScale>(() => Parse(ImageResourceIds.PrintScale, ParsePrintScale), Mode);
            _pixelAspectRatio = new Lazy<double?>(ParsePixelAspectRatio, Mode);
            _versionInfo = new Lazy<PsdVersionInfo>(() => Parse(ImageResourceIds.VersionInfo, ParseVersionInfo), Mode);
            _iccProfile = new Lazy<PsdIccProfile>(ParseIccProfile, Mode);
            _xmp = new Lazy<string>(ParseXmp, Mode);
            _iptc = new Lazy<List<PsdIptcDataSet>>(ParseIptc, Mode);
            _channelNames = new Lazy<List<string>>(() => ReadChannelNames(_document), Mode);
        }

        /// <summary>Print resolution (1005).</summary>
        public PsdResolutionInfo Resolution
        {
            get { return _resolution.Value; }
        }

        /// <summary>Grid spacing and ruler guides (1032).</summary>
        public PsdGridAndGuides GridAndGuides
        {
            get { return _gridAndGuides.Value; }
        }

        /// <summary>The thumbnail (1036, or the Photoshop 4 resource 1033 when 1036 is absent).</summary>
        public PsdThumbnail Thumbnail
        {
            get { return _thumbnail.Value; }
        }

        /// <summary>Web slices (1050).</summary>
        public PsdSlices Slices
        {
            get { return _slices.Value; }
        }

        /// <summary>Layer comps (1065).</summary>
        public PsdLayerComps LayerComps
        {
            get { return _layerComps.Value; }
        }

        /// <summary>Print scale and placement (1062).</summary>
        public PsdPrintScale PrintScale
        {
            get { return _printScale.Value; }
        }

        /// <summary>Pixel aspect ratio, width over height (1064); null when absent or invalid.</summary>
        public double? PixelAspectRatio
        {
            get { return _pixelAspectRatio.Value; }
        }

        /// <summary>Version information (1057): writer and reader names and whether the merged image is real.</summary>
        public PsdVersionInfo VersionInfo
        {
            get { return _versionInfo.Value; }
        }

        /// <summary>The embedded ICC profile (1039) with its description (details in phase 10).</summary>
        public PsdIccProfile IccProfile
        {
            get { return _iccProfile.Value; }
        }

        /// <summary>The XMP packet (1060) as XML text.</summary>
        public string Xmp
        {
            get { return _xmp.Value; }
        }

        /// <summary>IPTC-NAA datasets (1028) in file order; empty when absent.</summary>
        public List<PsdIptcDataSet> Iptc
        {
            get { return _iptc.Value; }
        }

        /// <summary>Alpha channel names (Unicode 1045, else Pascal 1006); empty when absent.</summary>
        public List<string> ChannelNames
        {
            get { return _channelNames.Value; }
        }

        private T Parse<T>(ushort id, Func<BigEndianReader, T> parse)
            where T : class
        {
            var resource = _document.GetImageResource(id);
            if (resource == null)
            {
                return null;
            }

            try
            {
                return parse(new BigEndianReader(resource.Data));
            }
            catch (PsdFormatException)
            {
                return null;
            }
        }

        // 16.16 unsigned resolutions, always pixels per inch; non-positive values read as 300.
        private static PsdResolutionInfo ParseResolution(BigEndianReader reader)
        {
            if (reader.Remaining < 16)
            {
                return null;
            }

            var horizontal = SanitizedPpi(reader.ReadUInt32() / 65536.0);
            var horizontalUnit = (PsdResolutionUnit)Unit(reader.ReadUInt16(), 2);
            var widthUnit = (PsdDimensionUnit)Unit(reader.ReadUInt16(), 5);
            var vertical = SanitizedPpi(reader.ReadUInt32() / 65536.0);
            var verticalUnit = (PsdResolutionUnit)Unit(reader.ReadUInt16(), 2);
            var heightUnit = (PsdDimensionUnit)Unit(reader.ReadUInt16(), 5);
            return new PsdResolutionInfo(horizontal, horizontalUnit, widthUnit, vertical, verticalUnit, heightUnit);
        }

        private static double SanitizedPpi(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 ? value : 300.0;
        }

        private static int Unit(ushort value, int max)
        {
            return value <= max ? value : 0;
        }

        // Version 1, cycles and positions in 1/32 pixel, a guide count bounded by the
        // payload. Grid cycles of 0 or less read as the default 576 (18 px). Guide
        // positions keep their sign.
        private static PsdGridAndGuides ParseGridAndGuides(BigEndianReader reader)
        {
            const int DefaultGridCycle32 = 576;
            if (reader.Remaining < 16 || reader.ReadUInt32() != 1)
            {
                return null;
            }

            var horizontal = reader.ReadInt32();
            var vertical = reader.ReadInt32();
            var count = reader.ReadUInt32();
            if (count > (uint)(reader.Remaining / 5))
            {
                return null;
            }

            var guides = new PsdGuide[count];
            for (var i = 0; i < guides.Length; i++)
            {
                var position = reader.ReadInt32();
                var direction = reader.ReadByte();
                guides[i] = new PsdGuide(position / 32.0, direction == 1 ? PsdGuideOrientation.Horizontal : PsdGuideOrientation.Vertical);
            }

            return new PsdGridAndGuides(
                (horizontal > 0 ? horizontal : DefaultGridCycle32) / 32.0,
                (vertical > 0 ? vertical : DefaultGridCycle32) / 32.0,
                guides);
        }

        // Thumbnail resource: format, width, height, row bytes, total size, compressed
        // size (u32 each), bits per pixel, planes (u16 each), then the payload.
        private PsdThumbnail ParseThumbnail()
        {
            var legacy = _document.GetImageResource(ImageResourceIds.Thumbnail) == null;
            return Parse(legacy ? ImageResourceIds.ThumbnailLegacy : ImageResourceIds.Thumbnail, delegate(BigEndianReader reader)
            {
                var format = reader.ReadUInt32();
                var width = reader.ReadUInt32();
                var height = reader.ReadUInt32();
                var rowBytes = reader.ReadUInt32();
                reader.ReadUInt32(); // total size
                var compressed = reader.ReadUInt32();
                var bits = reader.ReadUInt16();
                reader.ReadUInt16(); // planes
                if (format > 1 || width == 0 || width > 65535 || height == 0 || height > 65535 || rowBytes > int.MaxValue)
                {
                    return null;
                }

                var length = format == 1 ? Math.Min(compressed, (uint)reader.Remaining) : (uint)reader.Remaining;
                if (format == 1 && compressed == 0)
                {
                    length = (uint)reader.Remaining;
                }

                var data = reader.ReadBytes(length);
                return new PsdThumbnail((PsdThumbnailFormat)format, (int)width, (int)height, (int)rowBytes, bits, legacy, data);
            });
        }

        private static PsdSlices ParseSlices(BigEndianReader reader)
        {
            var version = reader.ReadUInt32();
            if (version == 7 || version == 8)
            {
                return SlicesFromDescriptor((int)version, Descriptor.ReadVersioned(reader));
            }

            if (version != 6)
            {
                return null;
            }

            // Version 6 (Adobe "Slices resource format"): group bounds, group name, slice records.
            var bounds = ReadRect(reader, true);
            var groupName = reader.ReadUnicodeString();
            var count = reader.ReadUInt32();
            var slices = new List<PsdSlice>();
            for (uint i = 0; i < count && reader.Remaining > 0; i++)
            {
                try
                {
                    slices.Add(ReadSliceV6(reader));
                }
                catch (PsdFormatException)
                {
                    break; // keep the slices that decoded
                }
            }

            return new PsdSlices(6, bounds, groupName, slices, null);
        }

        private static PsdSlice ReadSliceV6(BigEndianReader reader)
        {
            var id = reader.ReadInt32();
            var groupId = reader.ReadInt32();
            var origin = reader.ReadUInt32();
            var layerId = origin == 1 ? reader.ReadInt32() : 0;
            var name = reader.ReadUnicodeString();
            var type = reader.ReadUInt32();
            var left = reader.ReadInt32();
            var top = reader.ReadInt32();
            var right = reader.ReadInt32();
            var bottom = reader.ReadInt32();
            var slice = new PsdSlice
            {
                Id = id,
                GroupId = groupId,
                Origin = origin <= 2 ? (PsdSliceOrigin)origin : PsdSliceOrigin.UserGenerated,
                AssociatedLayerId = layerId,
                Name = name,
                Type = type == 0 ? PsdSliceType.NoImage : PsdSliceType.Image,
                Bounds = new PsdRect(left, top, right, bottom),
                Url = reader.ReadUnicodeString(),
                Target = reader.ReadUnicodeString(),
                Message = reader.ReadUnicodeString(),
                AltTag = reader.ReadUnicodeString(),
                CellTextIsHtml = reader.ReadByte() != 0,
                CellText = reader.ReadUnicodeString(),
                HorizontalAlignment = reader.ReadInt32(),
                VerticalAlignment = reader.ReadInt32(),
            };
            var alpha = reader.ReadByte();
            var red = reader.ReadByte();
            var green = reader.ReadByte();
            var blue = reader.ReadByte();
            slice.BackgroundColor = new PsdColor(red, green, blue, alpha);

            // Photoshop 7 and later append a descriptor (version 16) to each record.
            // There is no length; like psd-tools, peek for the version and rewind
            // when what follows does not read as a descriptor.
            if (reader.Remaining >= 8)
            {
                var start = reader.Position;
                if (reader.ReadUInt32() == 16)
                {
                    try
                    {
                        DescriptorReader.ReadDescriptor(reader, 0);
                        return slice;
                    }
                    catch (PsdFormatException)
                    {
                    }
                }

                reader.Position = start;
            }

            return slice;
        }

        private static PsdSlices SlicesFromDescriptor(int version, Descriptor root)
        {
            var slices = new List<PsdSlice>();
            var list = root.GetList("slices");
            if (list != null)
            {
                foreach (var item in list)
                {
                    var d = item == null ? null : item.Object;
                    if (d == null)
                    {
                        continue;
                    }

                    // ESliceBGColorType: None, matte or Clr; only a "Clr " background stores a color.
                    var bgType = d.GetEnum("bgColorType");
                    var background = string.IsNullOrEmpty(bgType) || bgType == "Clr " ? d.GetObject("bgColor") : null;
                    var slice = new PsdSlice
                    {
                        Id = (int)d.GetNumber("sliceID", 0),
                        GroupId = (int)d.GetNumber("groupID", 0),
                        Origin = SliceOrigin(d.GetEnum("origin")),
                        AssociatedLayerId = (int)d.GetNumber("layerID", 0),
                        Name = d.GetString("Nm  ") ?? string.Empty,
                        Type = d.GetEnum("Type") == "noImage" ? PsdSliceType.NoImage : PsdSliceType.Image,
                        Bounds = DescriptorRect(d.GetObject("bounds")),
                        // Photoshop writes the URL key as the three-character string "url" (metadata.psd).
                        Url = d.GetString("url") ?? d.GetString("url ") ?? string.Empty,
                        Target = d.GetString("null") ?? string.Empty,
                        Message = d.GetString("Msge") ?? string.Empty,
                        AltTag = d.GetString("altTag") ?? string.Empty,
                        CellTextIsHtml = d.GetBoolean("cellTextIsHTML"),
                        CellText = d.GetString("cellText") ?? string.Empty,
                        HorizontalAlignment = HorizontalAlignment(d.GetEnum("horzAlign")),
                        VerticalAlignment = VerticalAlignment(d.GetEnum("vertAlign")),
                        TopOutset = Int(d, "topOutset"),
                        LeftOutset = Int(d, "leftOutset"),
                        BottomOutset = Int(d, "bottomOutset"),
                        RightOutset = Int(d, "rightOutset"),
                        BackgroundColor = background == null
                            ? PsdColor.Transparent
                            : new PsdColor(Byte(background, "Rd  "), Byte(background, "Grn "), Byte(background, "Bl  "), Byte(background, "alpha")),
                    };
                    slices.Add(slice);
                }
            }

            return new PsdSlices(version, DescriptorRect(root.GetObject("bounds")), root.GetString("baseName") ?? string.Empty, slices, root);
        }

        private static PsdSliceOrigin SliceOrigin(string value)
        {
            switch (value)
            {
                case "autoGenerated":
                    return PsdSliceOrigin.AutoGenerated;
                case "layerGenerated":
                    return PsdSliceOrigin.LayerBased;
                default:
                    return PsdSliceOrigin.UserGenerated;
            }
        }

        private static byte Byte(Descriptor d, string key)
        {
            return (byte)Compat.Clamp(d.GetNumber(key, 0), 0, 255);
        }

        // ESliceHorzAlign and ESliceVertAlign tokens, numbered like the version 6 integers
        // (0 default, then left/top, center/middle, right/bottom, baseline).
        private static int HorizontalAlignment(string value)
        {
            switch (value)
            {
                case "Left":
                case "left":
                case "Lft ":
                    return 1;
                case "Cntr":
                case "center":
                    return 2;
                case "Rght":
                case "right":
                    return 3;
                default:
                    return 0;
            }
        }

        private static int VerticalAlignment(string value)
        {
            switch (value)
            {
                case "Top ":
                case "top":
                    return 1;
                case "Mddl":
                case "Cntr":
                case "middle":
                case "center":
                    return 2;
                case "Btom":
                case "bottom":
                    return 3;
                case "Bsln":
                case "baseline":
                    return 4;
                default:
                    return 0;
            }
        }

        private static PsdRect DescriptorRect(Descriptor bounds)
        {
            return bounds == null
                ? default(PsdRect)
                : new PsdRect(Int(bounds, "Left"), Int(bounds, "Top "), Int(bounds, "Rght"), Int(bounds, "Btom"));
        }

        private static int Int(Descriptor d, string key)
        {
            return (int)Compat.Clamp(d.GetNumber(key, 0), int.MinValue, int.MaxValue);
        }

        private static PsdRect ReadRect(BigEndianReader reader, bool topLeftFirst)
        {
            var a = reader.ReadInt32();
            var b = reader.ReadInt32();
            var c = reader.ReadInt32();
            var d = reader.ReadInt32();
            return topLeftFirst ? new PsdRect(b, a, d, c) : new PsdRect(a, b, c, d);
        }

        // Layer comps: descriptor version 16, then a descriptor whose "list" holds one
        // "Comp" object per comp; capturedInfo bits are visibility (1), position (2)
        // and appearance (4).
        private static PsdLayerComps ParseLayerComps(BigEndianReader reader)
        {
            var root = Descriptor.ReadVersioned(reader);
            var comps = new List<PsdLayerComp>();
            var list = root.GetList("list");
            if (list != null)
            {
                foreach (var item in list)
                {
                    var d = item == null ? null : item.Object;
                    if (d == null)
                    {
                        continue;
                    }

                    var captured = (int)d.GetNumber("capturedInfo", 0);
                    comps.Add(new PsdLayerComp(
                        Int(d, "compID"),
                        d.GetString("Nm  ") ?? string.Empty,
                        d.GetString("comment") ?? string.Empty,
                        (captured & 1) != 0,
                        (captured & 2) != 0,
                        (captured & 4) != 0));
                }
            }

            DescriptorValue lastValue;
            int? last = root.TryGet("lastAppliedComp", out lastValue) ? (int?)Int(root, "lastAppliedComp") : null;
            return new PsdLayerComps(comps, last, root);
        }

        private static PsdPrintScale ParsePrintScale(BigEndianReader reader)
        {
            var style = reader.ReadUInt16();
            var x = reader.ReadSingle();
            var y = reader.ReadSingle();
            var scale = reader.ReadSingle();
            return new PsdPrintScale(style <= 2 ? (PsdPrintScaleStyle)style : PsdPrintScaleStyle.Centered, x, y, scale);
        }

        private double? ParsePixelAspectRatio()
        {
            var resource = _document.GetImageResource(ImageResourceIds.PixelAspectRatio);
            if (resource == null || resource.Data.Length < 12)
            {
                return null;
            }

            var reader = new BigEndianReader(resource.Data);
            reader.ReadUInt32(); // version 1 or 2
            var ratio = reader.ReadDouble();
            return !double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio > 0 ? ratio : (double?)null;
        }

        private static PsdVersionInfo ParseVersionInfo(BigEndianReader reader)
        {
            var version = reader.ReadInt32();
            var real = reader.ReadByte() != 0;
            var writer = reader.ReadUnicodeString();
            var readerName = reader.ReadUnicodeString();
            var fileVersion = reader.Remaining >= 4 ? reader.ReadInt32() : 0;
            return new PsdVersionInfo(version, real, writer, readerName, fileVersion);
        }

        private PsdIccProfile ParseIccProfile()
        {
            var resource = _document.GetImageResource(ImageResourceIds.IccProfile);
            if (resource == null || resource.Data.Length == 0)
            {
                return null;
            }

            // The profile header description arrives with the ICC port (phase 10);
            // until then the raw bytes ride along untouched.
            return new PsdIccProfile(resource.Data, string.Empty, string.Empty, string.Empty, new Version(0, 0));
        }

        private string ParseXmp()
        {
            var resource = _document.GetImageResource(ImageResourceIds.Xmp);
            return resource == null ? null : StrictUtf8NoBom.GetString(resource.Data).TrimEnd('\0');
        }

        // IPTC-NAA records: 0x1C, record, dataset, u16 length (extended lengths stop the walk).
        private List<PsdIptcDataSet> ParseIptc()
        {
            var resource = _document.GetImageResource(ImageResourceIds.IptcNaa);
            if (resource == null)
            {
                return new List<PsdIptcDataSet>();
            }

            var data = resource.Data;
            var results = new List<PsdIptcDataSet>();
            var offset = 0;
            while (offset + 5 <= data.Length && data[offset] == 0x1C)
            {
                var length = (data[offset + 3] << 8) | data[offset + 4];
                if ((length & 0x8000) != 0 || offset + 5 + length > data.Length)
                {
                    break;
                }

                var value = new byte[length];
                Array.Copy(data, offset + 5, value, 0, length);
                results.Add(new PsdIptcDataSet(data[offset + 1], data[offset + 2], value));
                offset += 5 + length;
            }

            return results;
        }

        // Alpha channel names (Unicode 1045, else Pascal 1006). Lives in the original's
        // TextExtractor; it moves there with the phase 6 port.
        private static List<string> ReadChannelNames(PsdDocument document)
        {
            var names = new List<string>();
            var unicode = document.GetImageResource(ImageResourceIds.UnicodeAlphaNames);
            if (unicode != null)
            {
                var reader = new BigEndianReader(unicode.Data);
                try
                {
                    while (reader.Remaining >= 4)
                    {
                        var name = reader.ReadUnicodeString();
                        if (name.Length > 0)
                        {
                            names.Add(name);
                        }
                    }
                }
                catch (PsdFormatException)
                {
                    // Keep what decoded.
                }

                return names;
            }

            var pascal = document.GetImageResource(ImageResourceIds.AlphaChannelNames);
            if (pascal != null)
            {
                var reader = new BigEndianReader(pascal.Data);
                try
                {
                    while (reader.Remaining >= 1)
                    {
                        var name = reader.ReadPascalString(1);
                        if (name.Length > 0)
                        {
                            names.Add(name);
                        }
                    }
                }
                catch (PsdFormatException)
                {
                    // Keep what decoded.
                }
            }

            return names;
        }

        private static readonly UTF8Encoding StrictUtf8NoBom = new UTF8Encoding(false, true);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>The value as text: UTF-8 when it decodes cleanly, else Latin-1.</summary>
        internal static string DecodeIptcText(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return StrictUtf8NoBom.GetString(value);
            }
            catch (DecoderFallbackException)
            {
                return Latin1.GetString(value);
            }
        }
    }
}
