using System;

namespace XRay.PsdFx
{
    /// <summary>An image resource block from the PSD image resources section.</summary>
    public sealed class ImageResource
    {
        public ImageResource(ushort id, string name, byte[] data)
        {
            Id = id;
            Name = name;
            Data = data ?? new byte[0];
        }

        public ushort Id { get; private set; }

        public string Name { get; private set; }

        /// <summary>The resource payload (a copy; net45 has no memory views).</summary>
        public byte[] Data { get; private set; }

        public override string ToString()
        {
            return string.Format("resource {0} \"{1}\" ({2} bytes)", Id, Name, Data.Length);
        }
    }

    /// <summary>Well-known image resource IDs.</summary>
    public static class ImageResourceIds
    {
        public const ushort ResolutionInfo = 1005;
        public const ushort AlphaChannelNames = 1006;
        public const ushort Caption = 1008;
        public const ushort IptcNaa = 1028;
        public const ushort GridAndGuides = 1032;
        public const ushort Thumbnail = 1036;
        public const ushort GlobalAngle = 1037;
        public const ushort IccProfile = 1039;
        public const ushort UnicodeAlphaNames = 1045;
        public const ushort IndexedColorTableCount = 1046;
        public const ushort TransparencyIndex = 1047;
        public const ushort GlobalAltitude = 1049;
        public const ushort Slices = 1050;
        public const ushort AlphaIdentifiers = 1053;
        public const ushort VersionInfo = 1057;
        public const ushort Xmp = 1060;
        public const ushort PathFirst = 2000;
        public const ushort PathLast = 2997;
        public const ushort ClippingPathName = 2999;
        public const ushort WorkPath = 1025;
        public const ushort ThumbnailLegacy = 1033;
        public const ushort PrintScale = 1062;
        public const ushort PixelAspectRatio = 1064;
        public const ushort LayerComps = 1065;
    }

    /// <summary>A linked or embedded smart-object source file from the lnk2/lnkD/lnk3/lnkE blocks.</summary>
    public sealed class LinkedFile
    {
        public LinkedFile(string id, string fileName, string fileType, string kind, byte[] data)
        {
            Id = id;
            FileName = fileName;
            FileType = fileType;
            Kind = kind;
            Data = data ?? new byte[0];
        }

        public string Id { get; private set; }

        public string FileName { get; private set; }

        public string FileType { get; private set; }

        public string Kind { get; private set; }

        public byte[] Data { get; private set; }

        /// <summary>True when the payload is itself a PSD or PSB document.</summary>
        public bool IsPhotoshopDocument
        {
            get
            {
                return Data.Length >= 4 && Data[0] == (byte)'8' && Data[1] == (byte)'B' && Data[2] == (byte)'P' && Data[3] == (byte)'S';
            }
        }
    }
}
