using System;
using System.Collections.Generic;
using System.IO;

namespace XRay.PsdFx
{
    /// <summary>Display unit of a resolution value (resource 1005). The stored value is always pixels per inch.</summary>
    public enum PsdResolutionUnit
    {
        Unknown = 0,
        PixelsPerInch = 1,
        PixelsPerCentimeter = 2,
    }

    /// <summary>Display unit of the document width or height in the Image Size dialog (resource 1005).</summary>
    public enum PsdDimensionUnit
    {
        Unknown = 0,
        Inches = 1,
        Centimeters = 2,
        Points = 3,
        Picas = 4,
        Columns = 5,
    }

    /// <summary>
    /// Print resolution from the ResolutionInfo resource (1005). Both resolutions are
    /// in pixels per inch whatever the display units say, as Photoshop reports them.
    /// </summary>
    public sealed class PsdResolutionInfo
    {
        public PsdResolutionInfo(double horizontalPpi, PsdResolutionUnit horizontalDisplayUnit, PsdDimensionUnit widthDisplayUnit, double verticalPpi, PsdResolutionUnit verticalDisplayUnit, PsdDimensionUnit heightDisplayUnit)
        {
            HorizontalPpi = horizontalPpi;
            HorizontalDisplayUnit = horizontalDisplayUnit;
            WidthDisplayUnit = widthDisplayUnit;
            VerticalPpi = verticalPpi;
            VerticalDisplayUnit = verticalDisplayUnit;
            HeightDisplayUnit = heightDisplayUnit;
        }

        public double HorizontalPpi { get; private set; }

        public PsdResolutionUnit HorizontalDisplayUnit { get; private set; }

        public PsdDimensionUnit WidthDisplayUnit { get; private set; }

        public double VerticalPpi { get; private set; }

        public PsdResolutionUnit VerticalDisplayUnit { get; private set; }

        public PsdDimensionUnit HeightDisplayUnit { get; private set; }
    }

    /// <summary>Guide orientation.</summary>
    public enum PsdGuideOrientation
    {
        /// <summary>A vertical line at an x position.</summary>
        Vertical = 0,

        /// <summary>A horizontal line at a y position.</summary>
        Horizontal = 1,
    }

    /// <summary>One ruler guide. <see cref="Position"/> is in document pixels (stored in 1/32 pixel steps).</summary>
    public struct PsdGuide
    {
        public PsdGuide(double position, PsdGuideOrientation orientation) : this()
        {
            Position = position;
            Orientation = orientation;
        }

        public double Position { get; private set; }

        public PsdGuideOrientation Orientation { get; private set; }

        public override string ToString()
        {
            return string.Format("{0} {1:0.##}", Orientation, Position);
        }
    }

    /// <summary>Grid spacing and ruler guides from resource 1032. Grid cycles are in pixels.</summary>
    public sealed class PsdGridAndGuides
    {
        public PsdGridAndGuides(double horizontalGridCycle, double verticalGridCycle, IReadOnlyList<PsdGuide> guides)
        {
            HorizontalGridCycle = horizontalGridCycle;
            VerticalGridCycle = verticalGridCycle;
            Guides = guides ?? new PsdGuide[0];
        }

        public double HorizontalGridCycle { get; private set; }

        public double VerticalGridCycle { get; private set; }

        public IReadOnlyList<PsdGuide> Guides { get; private set; }
    }

    /// <summary>Thumbnail payload encoding.</summary>
    public enum PsdThumbnailFormat
    {
        /// <summary>Uncompressed rows of 24-bit pixels, <see cref="PsdThumbnail.RowBytes"/> apart.</summary>
        RawRgb = 0,

        /// <summary>A JPEG file (JFIF).</summary>
        Jpeg = 1,
    }

    /// <summary>
    /// The thumbnail Photoshop stores for file browsers: resource 1036, or the
    /// Photoshop 4 resource 1033 whose pixels are in BGR order. Data holds the encoded
    /// payload; decoding the JPEG form needs the Skia package of the original, which
    /// this port does not carry (a stored JPEG is written out intact).
    /// </summary>
    public sealed class PsdThumbnail
    {
        public PsdThumbnail(PsdThumbnailFormat format, int width, int height, int rowBytes, int bitsPerPixel, bool isBgr, byte[] data)
        {
            Format = format;
            Width = width;
            Height = height;
            RowBytes = rowBytes;
            BitsPerPixel = bitsPerPixel;
            IsBgr = isBgr;
            Data = data ?? new byte[0];
        }

        public PsdThumbnailFormat Format { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int RowBytes { get; private set; }

        public int BitsPerPixel { get; private set; }

        public bool IsBgr { get; private set; }

        public byte[] Data { get; private set; }

        /// <summary>Whether <see cref="Data"/> is a complete JPEG file.</summary>
        public bool IsJpeg
        {
            get { return Format == PsdThumbnailFormat.Jpeg; }
        }

        /// <summary>
        /// Writes the stored payload to <paramref name="destination"/> unchanged: a JPEG
        /// file for JPEG thumbnails (nothing is decoded or re-encoded), the pixel rows for
        /// raw ones. The stream is not disposed.
        /// </summary>
        public void CopyTo(Stream destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException("destination");
            }

            destination.Write(Data, 0, Data.Length);
        }

        /// <summary>
        /// The pixels of a raw thumbnail as RGBA (rows <see cref="RowBytes"/> apart, 24 bits
        /// per pixel, swapped when <see cref="IsBgr"/>); null for a JPEG thumbnail or a
        /// payload too short for its dimensions.
        /// </summary>
        public RgbaImage ToRgba()
        {
            if (IsJpeg || BitsPerPixel != 24 || RowBytes < Width * 3 || Data.Length < (long)RowBytes * (Height - 1) + (Width * 3L))
            {
                return null;
            }

            var image = new RgbaImage(Width, Height);
            var target = image.Pixels;
            for (var y = 0; y < Height; y++)
            {
                var row = y * RowBytes;
                var offset = y * Width * 4;
                for (var x = 0; x < Width; x++)
                {
                    var r = Data[row + (x * 3)];
                    var g = Data[row + (x * 3) + 1];
                    var b = Data[row + (x * 3) + 2];
                    target[offset] = IsBgr ? b : r;
                    target[offset + 1] = g;
                    target[offset + 2] = IsBgr ? r : b;
                    target[offset + 3] = 255;
                    offset += 4;
                }
            }

            return image;
        }
    }

    /// <summary>Where a slice came from.</summary>
    public enum PsdSliceOrigin
    {
        AutoGenerated = 0,
        LayerBased = 1,
        UserGenerated = 2,
    }

    /// <summary>Slice content type.</summary>
    public enum PsdSliceType
    {
        NoImage = 0,
        Image = 1,
    }

    /// <summary>One web slice from resource 1050.</summary>
    public sealed class PsdSlice
    {
        public int Id { get; internal set; }

        public int GroupId { get; internal set; }

        public PsdSliceOrigin Origin { get; internal set; }

        /// <summary>The layer ID a layer-based slice follows, or 0.</summary>
        public int AssociatedLayerId { get; internal set; }

        public string Name { get; internal set; }

        public PsdSliceType Type { get; internal set; }

        public PsdRect Bounds { get; internal set; }

        public string Url { get; internal set; }

        public string Target { get; internal set; }

        public string Message { get; internal set; }

        public string AltTag { get; internal set; }

        public bool CellTextIsHtml { get; internal set; }

        public string CellText { get; internal set; }

        /// <summary>Cell text alignment: 0 default, 1 left, 2 center, 3 right.</summary>
        public int HorizontalAlignment { get; internal set; }

        /// <summary>Cell text alignment: 0 default, 1 top, 2 middle, 3 bottom, 4 baseline.</summary>
        public int VerticalAlignment { get; internal set; }

        /// <summary>Outsets in pixels (version 7 and 8 descriptors only; 0 otherwise).</summary>
        public int TopOutset { get; internal set; }

        public int LeftOutset { get; internal set; }

        public int BottomOutset { get; internal set; }

        public int RightOutset { get; internal set; }

        /// <summary>Background color with its alpha (0 for none).</summary>
        public PsdColor BackgroundColor { get; internal set; }
    }

    /// <summary>
    /// The slices resource (1050): the slice group bounds and name, and every slice.
    /// Version is 6 for the binary layout, 7 or 8 for the descriptor layout, whose raw
    /// descriptor is kept in <see cref="Descriptor"/>. In the descriptor layout
    /// <see cref="GroupName"/> is the baseName (every corpus file stores "User").
    /// </summary>
    public sealed class PsdSlices
    {
        public PsdSlices(int version, PsdRect bounds, string groupName, IReadOnlyList<PsdSlice> slices, Descriptor descriptor)
        {
            Version = version;
            Bounds = bounds;
            GroupName = groupName;
            Slices = slices ?? new PsdSlice[0];
            Descriptor = descriptor;
        }

        public int Version { get; private set; }

        public PsdRect Bounds { get; private set; }

        public string GroupName { get; private set; }

        public IReadOnlyList<PsdSlice> Slices { get; private set; }

        public Descriptor Descriptor { get; private set; }
    }

    /// <summary>One layer comp from resource 1065. <see cref="Id"/> is the comp ID (compID).</summary>
    public sealed class PsdLayerComp
    {
        public PsdLayerComp(int id, string name, string comment, bool capturesVisibility, bool capturesPosition, bool capturesAppearance)
        {
            Id = id;
            Name = name;
            Comment = comment;
            CapturesVisibility = capturesVisibility;
            CapturesPosition = capturesPosition;
            CapturesAppearance = capturesAppearance;
        }

        public int Id { get; private set; }

        public string Name { get; private set; }

        public string Comment { get; private set; }

        public bool CapturesVisibility { get; private set; }

        public bool CapturesPosition { get; private set; }

        public bool CapturesAppearance { get; private set; }
    }

    /// <summary>The layer comps resource (1065). <see cref="LastAppliedCompId"/> is the comp last applied in Photoshop, when recorded.</summary>
    public sealed class PsdLayerComps
    {
        public PsdLayerComps(IReadOnlyList<PsdLayerComp> comps, int? lastAppliedCompId, Descriptor descriptor)
        {
            Comps = comps ?? new PsdLayerComp[0];
            LastAppliedCompId = lastAppliedCompId;
            Descriptor = descriptor;
        }

        public IReadOnlyList<PsdLayerComp> Comps { get; private set; }

        public int? LastAppliedCompId { get; private set; }

        public Descriptor Descriptor { get; private set; }
    }

    /// <summary>Print placement style from resource 1062.</summary>
    public enum PsdPrintScaleStyle
    {
        Centered = 0,
        SizeToFit = 1,
        UserDefined = 2,
    }

    /// <summary>Print scale and position (resource 1062).</summary>
    public sealed class PsdPrintScale
    {
        public PsdPrintScale(PsdPrintScaleStyle style, float x, float y, float scale)
        {
            Style = style;
            X = x;
            Y = y;
            Scale = scale;
        }

        public PsdPrintScaleStyle Style { get; private set; }

        public float X { get; private set; }

        public float Y { get; private set; }

        public float Scale { get; private set; }
    }

    /// <summary>
    /// Version information (resource 1057). HasRealMergedData is false when the file
    /// was saved without "Maximize Compatibility".
    /// </summary>
    public sealed class PsdVersionInfo
    {
        public PsdVersionInfo(int version, bool hasRealMergedData, string writerName, string readerName, int fileVersion)
        {
            Version = version;
            HasRealMergedData = hasRealMergedData;
            WriterName = writerName;
            ReaderName = readerName;
            FileVersion = fileVersion;
        }

        public int Version { get; private set; }

        public bool HasRealMergedData { get; private set; }

        public string WriterName { get; private set; }

        public string ReaderName { get; private set; }

        public int FileVersion { get; private set; }
    }

    /// <summary>
    /// The embedded ICC profile (resource 1039): the raw bytes, the description tag
    /// (desc), the device color space signature (RGB / GRAY / CMYK...), the profile
    /// class (mntr, prtr...) and the major.minor version. The parsed fields are empty
    /// (version 0.0) when the profile does not parse.
    /// </summary>
    public sealed class PsdIccProfile
    {
        public PsdIccProfile(byte[] data, string description, string colorSpace, string deviceClass, Version version)
        {
            Data = data ?? new byte[0];
            Description = description;
            ColorSpace = colorSpace;
            DeviceClass = deviceClass;
            Version = version ?? new Version(0, 0);
        }

        public byte[] Data { get; private set; }

        public string Description { get; private set; }

        public string ColorSpace { get; private set; }

        public string DeviceClass { get; private set; }

        public Version Version { get; private set; }
    }

    /// <summary>One IPTC-NAA dataset (resource 1028), such as 2:120 caption or 2:25 keyword.</summary>
    public sealed class PsdIptcDataSet
    {
        public PsdIptcDataSet(byte record, byte dataSet, byte[] value)
        {
            Record = record;
            DataSet = dataSet;
            Value = value ?? new byte[0];
        }

        public byte Record { get; private set; }

        public byte DataSet { get; private set; }

        public byte[] Value { get; private set; }

        /// <summary>The value as text: UTF-8 when it decodes cleanly, else Latin-1.</summary>
        public string Text
        {
            get { return PsdImageResources.DecodeIptcText(Value); }
        }
    }
}
