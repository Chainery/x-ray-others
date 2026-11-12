using System;
using System.Collections.Generic;
using System.IO;

namespace XRay.PsdFx
{
    public enum PsdColorMode
    {
        Bitmap = 0,
        Grayscale = 1,
        Indexed = 2,
        Rgb = 3,
        Cmyk = 4,
        Multichannel = 7,
        Duotone = 8,
        Lab = 9,
    }

    /// <summary>
    /// A parsed PSD document: header values, layers and their effects. Pixel data
    /// (channels, merged image) is not decoded yet - the port is in progress, and
    /// this type grows into the full document (pixels, text, render) the way the
    /// original XRay.Psd.PsdDocument does.
    /// </summary>
    public sealed partial class PsdDocument
    {
        public bool IsLargeDocument { get; internal set; }

        public int Width { get; internal set; }

        public int Height { get; internal set; }

        public int Channels { get; internal set; }

        public int BitsPerChannel { get; internal set; }

        public PsdColorMode ColorMode { get; internal set; }

        /// <summary>The document's global light angle (image resource 1057; 120 when absent).</summary>
        public int GlobalLightAngle { get; internal set; }

        /// <summary>The document's global light altitude (image resource 1058; 30 when absent).</summary>
        public int GlobalLightAltitude { get; internal set; }

        public IReadOnlyList<PsdLayer> Layers { get; internal set; } = new PsdLayer[0];

        /// <summary>Bits per channel (depth): 1, 8, 16 or 32.</summary>
        public int Depth
        {
            get { return BitsPerChannel; }
        }

        /// <summary>Channel count of the merged image (color channels plus one when it has transparency).</summary>
        public int ChannelCount
        {
            get { return Channels; }
        }

        /// <summary>The document rectangle: 0, 0, Width, Height.</summary>
        public PsdRect Bounds
        {
            get { return new PsdRect(0, 0, Width, Height); }
        }

        /// <summary>Whether the merged image data section carries a transparency channel.</summary>
        public bool MergedImageHasTransparency { get; internal set; }

        /// <summary>
        /// Whether the saved merged image carries real pixels: Photoshop writes a blank
        /// placeholder when the document was saved without Maximize Compatibility
        /// (version info resource 1057). True when the resource is absent.
        /// </summary>
        public bool HasRealMergedImage
        {
            get
            {
                var versionInfo = Resources.VersionInfo;
                return versionInfo == null || versionInfo.HasRealMergedData;
            }
        }

        internal ByteRange MergedImage { get; set; }

        /// <summary>Decodes the flattened composite Photoshop saves after the layers.</summary>
        public RgbaImage GetMergedImage()
        {
            return MergedImageDecoder.Decode(this).ToRgba(Bounds);
        }

        /// <summary>Top-level layers and groups, bottom to top.</summary>
        public IReadOnlyList<PsdLayer> RootLayers { get; internal set; } = new PsdLayer[0];

        /// <summary>BuildTree accumulates here before the final root list is assigned.</summary>
        internal List<PsdLayer> RootLayersInternal = new List<PsdLayer>();

        /// <summary>Document-level tagged blocks that follow the layer info (Txt2, Patt, lnk2...).</summary>
        public IReadOnlyList<TaggedBlock> GlobalTaggedBlocks { get; internal set; }

        private Dictionary<string, PatternTile> _patterns;

        /// <summary>The document's pattern tiles (Patt/Pat2/Pat3), decoded on first use.</summary>
        internal Dictionary<string, PatternTile> Patterns
        {
            get
            {
                if (_patterns == null)
                {
                    _patterns = PatternStore.Parse(this);
                }

                return _patterns;
            }
        }

        /// <summary>Raw image resource blocks, in file order.</summary>
        public IReadOnlyList<ImageResource> ImageResources { get; internal set; }

        private PsdImageResources _resources;

        /// <summary>Typed views of the common image resources, parsed on first access.</summary>
        public PsdImageResources Resources
        {
            get
            {
                if (_resources == null)
                {
                    _resources = new PsdImageResources(this);
                }

                return _resources;
            }
        }

        /// <summary>The first resource with the given id, or null.</summary>
        public ImageResource GetImageResource(ushort id)
        {
            var resources = ImageResources;
            if (resources != null)
            {
                foreach (var resource in resources)
                {
                    if (resource.Id == id)
                    {
                        return resource;
                    }
                }
            }

            return null;
        }

        /// <summary>Alias of <see cref="BitsPerChannel"/> kept for the ported imaging code.</summary>
        internal int DepthInternal
        {
            get { return BitsPerChannel; }
        }

        /// <summary>The raw color mode data section (duotone specifications live here).</summary>
        internal byte[] ColorModeData { get; set; }

        /// <summary>The color table of an indexed document, when parsed.</summary>
        internal PsdColor[] Palette { get; set; }

        private readonly object _colorTransformLock = new object();
        private IccSrgbTransform _colorTransform;
        private bool _colorTransformParsed;

        /// <summary>Whether pixel decoding uses the embedded ICC profile.</summary>
        internal bool ColorManagementEnabled { get; set; }

        /// <summary>The cached embedded-profile-to-sRGB transform, or null when none applies (built on first use).</summary>
        internal IccSrgbTransform ColorTransform
        {
            get
            {
                if (_colorTransformParsed)
                {
                    return _colorTransform;
                }

                lock (_colorTransformLock)
                {
                    if (!_colorTransformParsed)
                    {
                        _colorTransform = ColorManagementEnabled ? IccSrgbTransform.ForDocument(this) : null;
                        _colorTransformParsed = true;
                    }
                }

                return _colorTransform;
            }
        }

        private readonly object _inkSpaceLock = new object();
        private IccInkSpace _inkSpace;
        private bool _inkSpaceParsed;

        /// <summary>The cached device-channel space the adjustment layers of CMYK, gray and duotone documents run in (built on first use).</summary>
        internal IccInkSpace InkSpace
        {
            get
            {
                if (_inkSpaceParsed)
                {
                    return _inkSpace;
                }

                lock (_inkSpaceLock)
                {
                    if (!_inkSpaceParsed)
                    {
                        _inkSpace = IccInkSpace.ForDocument(this);
                        _inkSpaceParsed = true;
                    }
                }

                return _inkSpace;
            }
        }

        /// <summary>Linked or embedded smart-object source files (lnk2/lnkD/lnk3/lnkE blocks).</summary>
        public IReadOnlyList<LinkedFile> LinkedFiles { get; internal set; } = new LinkedFile[0];

        private TextEngineBlock _textEngine;
        private bool _textEngineParsed;

        /// <summary>The document-level text engine block (Txt2), parsed on first use; null without one.</summary>
        public TextEngineBlock TextEngine
        {
            get
            {
                if (!_textEngineParsed)
                {
                    _textEngine = TextEngineResolver.ParseBlock(this);
                    _textEngineParsed = true;
                }

                return _textEngine;
            }
        }

        /// <summary>Returns the raw payload bytes of the first global tagged block with this key, or null.</summary>
        public byte[] GetGlobalTaggedBlock(string key)
        {
            if (GlobalTaggedBlocks == null)
            {
                return null;
            }

            foreach (var block in GlobalTaggedBlocks)
            {
                if (block.Key == key)
                {
                    return block.Data;
                }
            }

            return null;
        }

        /// <summary>
        /// All layers in panel order (top to bottom), groups before their children.
        /// Child paths are relative to the document root either way.
        /// </summary>
        public IEnumerable<PsdLayer> EnumerateLayersTopDown()
        {
            return EnumerateTopDown(RootLayers);
        }

        private static IEnumerable<PsdLayer> EnumerateTopDown(IReadOnlyList<PsdLayer> layers)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                var layer = layers[i];
                yield return layer;
                if (layer.IsGroup && layer.Children.Count > 0)
                {
                    foreach (var child in EnumerateTopDown(layer.Children))
                    {
                        yield return child;
                    }
                }
            }
        }

        /// <summary>Extracts every piece of text the document stores (see <see cref="TextExtractor"/>).</summary>
        public PsdTextContent ExtractText(TextExtractionOptions options = null)
        {
            return TextExtractor.Extract(this, options ?? new TextExtractionOptions());
        }

        private DocumentColors _colors;

        /// <summary>Single-color conversions that follow the document's color management.</summary>
        internal DocumentColors Colors
        {
            get
            {
                if (_colors == null)
                {
                    _colors = new DocumentColors(this);
                }

                return _colors;
            }
        }

        /// <summary>Loads a PSD or PSB file with the default options.</summary>
        public static PsdDocument Load(string path, PsdLoadOptions options = null)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Load(stream, options);
            }
        }

        /// <summary>Loads a PSD or PSB stream with the default options.</summary>
        public static PsdDocument Load(Stream stream, PsdLoadOptions options = null)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, read);
                }

                return Load(memory.ToArray(), options);
            }
        }

        /// <summary>Loads a PSD or PSB byte array with the default options.</summary>
        public static PsdDocument Load(byte[] data, PsdLoadOptions options = null)
        {
            return PsdParser.Parse(data, options ?? new PsdLoadOptions());
        }

        public static PsdDocument FromFile(string path)
        {
            return Load(path);
        }

        public static PsdDocument FromStream(Stream stream)
        {
            return Load(stream);
        }

        public static PsdDocument FromBytes(byte[] data)
        {
            return Load(data);
        }

        /// <summary>Convenience: every effect of every layer, in file order.</summary>
        public IEnumerable<PsdEffect> AllEffects
        {
            get
            {
                foreach (var layer in Layers)
                {
                    if (layer.Style == null)
                    {
                        continue;
                    }

                    foreach (var effect in layer.Style.Effects)
                    {
                        yield return effect;
                    }
                }
            }
        }
    }
}
