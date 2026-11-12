using System;
using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx.Icc
{
    /// <summary>A CIE XYZ triple (Y of the PCS white is 1).</summary>
    internal struct IccXyz
    {
        public IccXyz(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; private set; }

        public double Y { get; private set; }

        public double Z { get; private set; }
    }

    /// <summary>ICC rendering intents (header and tag numbering).</summary>
    internal enum IccRenderingIntent
    {
        Perceptual = 0,
        RelativeColorimetric = 1,
        Saturation = 2,
        AbsoluteColorimetric = 3,
    }

    /// <summary>
    /// A parsed ICC profile (v2 or v4): the header, the tag table, and the tags a
    /// device-to-sRGB conversion needs: colorants, white point, <c>chad</c>, the RGB and
    /// gray tone curves, and the <c>A2Bx</c>/<c>B2Ax</c> LUTs. Untrusted input: every
    /// offset is bounds-checked and a damaged tag is dropped, so Parse either returns a
    /// profile or null, never throws.
    /// </summary>
    internal sealed class IccProfile
    {
        public const uint ClassInput = 0x73636E72; // 'scnr'
        public const uint ClassDisplay = 0x6D6E7472; // 'mntr'
        public const uint ClassOutput = 0x70727472; // 'prtr'
        public const uint ClassColorSpace = 0x73706163; // 'spac'
        public const uint SpaceRgb = 0x52474220; // 'RGB '
        public const uint SpaceGray = 0x47524159; // 'GRAY'
        public const uint SpaceCmyk = 0x434D594B; // 'CMYK'
        public const uint SpaceLab = 0x4C616220; // 'Lab '
        public const uint SpaceXyz = 0x58595A20; // 'XYZ '

        private const int MaxTags = 1024;

        private readonly IccLut[] _aToB = new IccLut[3];
        private readonly IccLut[] _bToA = new IccLut[3];
        private readonly IccLut[] _dToB = new IccLut[4];

        private IccProfile()
        {
            Description = "";
            TagSignatures = new string[0];
        }

        public int MajorVersion { get; private set; }

        public int MinorVersion { get; private set; }

        /// <summary>Header version word, e.g. 0x02100000 for 2.1.</summary>
        public uint EncodedVersion { get; private set; }

        public uint DeviceClass { get; private set; }

        public uint ColorSpace { get; private set; }

        public uint Pcs { get; private set; }

        public IccRenderingIntent HeaderIntent { get; private set; }

        public string Description { get; private set; }

        /// <summary>Tag signatures in table order.</summary>
        public IReadOnlyList<string> TagSignatures { get; private set; }

        public IccXyz? MediaWhitePoint { get; private set; }

        /// <summary>The <c>chad</c> chromatic adaptation matrix (row major), when present.</summary>
        public double[] ChromaticAdaptation { get; private set; }

        public IccXyz? RedColorant { get; private set; }

        public IccXyz? GreenColorant { get; private set; }

        public IccXyz? BlueColorant { get; private set; }

        public IccCurve RedTrc { get; private set; }

        public IccCurve GreenTrc { get; private set; }

        public IccCurve BlueTrc { get; private set; }

        public IccCurve GrayTrc { get; private set; }

        public bool IsMatrixShaper
        {
            get
            {
                return ColorSpace == SpaceRgb
                    ? RedColorant.HasValue && GreenColorant.HasValue && BlueColorant.HasValue && RedTrc != null && GreenTrc != null && BlueTrc != null
                    : ColorSpace == SpaceGray && GrayTrc != null;
            }
        }

        /// <summary>The device-to-PCS LUT for an intent tag (0..2), without fallback.</summary>
        public IccLut GetAToB(int index)
        {
            return index >= 0 && index < 3 ? _aToB[index] : null;
        }

        /// <summary>The PCS-to-device LUT for an intent tag (0..2), without fallback.</summary>
        public IccLut GetBToA(int index)
        {
            return index >= 0 && index < 3 ? _bToA[index] : null;
        }

        /// <summary>The float device-to-PCS LUT (<c>D2B0..3</c>) for an intent, without fallback.</summary>
        public IccLut GetDToB(int index)
        {
            return index >= 0 && index < 4 ? _dToB[index] : null;
        }

        /// <summary>
        /// The device-to-PCS LUT Little CMS picks for <paramref name="intent"/>: the intent's
        /// float <c>D2Bx</c> tag when present (absolute has its own <c>D2B3</c>), else
        /// <c>A2B0/1/2</c> by intent (absolute uses <c>A2B1</c>), falling back to <c>A2B0</c>;
        /// null when the profile has no LUT (matrix/TRC only).
        /// </summary>
        public IccLut InputLut(IccRenderingIntent intent)
        {
            var floatLut = _dToB[(int)intent];
            if (floatLut != null)
            {
                return floatLut;
            }

            var index = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
            return _aToB[index] != null ? _aToB[index] : _aToB[0];
        }

        /// <summary>The PCS-to-device LUT for an intent, falling back to <c>B2A0</c>.</summary>
        public IccLut OutputLut(IccRenderingIntent intent)
        {
            var index = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
            return _bToA[index] != null ? _bToA[index] : _bToA[0];
        }

        public int DeviceChannels
        {
            get
            {
                switch (ColorSpace)
                {
                    case SpaceGray:
                        return 1;
                    case SpaceRgb:
                    case SpaceLab:
                    case SpaceXyz:
                        return 3;
                    case SpaceCmyk:
                        return 4;
                    default:
                        return ColorSpaceChannels(ColorSpace);
                }
            }
        }

        public static string SignatureToString(uint signature)
        {
            var bytes = new byte[4];
            bytes[0] = (byte)(signature >> 24);
            bytes[1] = (byte)(signature >> 16);
            bytes[2] = (byte)(signature >> 8);
            bytes[3] = (byte)signature;
            return Encoding.ASCII.GetString(bytes, 0, 4);
        }

        /// <summary>Parses profile bytes; null when they are not a usable ICC profile.</summary>
        public static IccProfile Parse(byte[] data)
        {
            if (data == null || data.Length < 132 || IccBytes.U32(data, 36) != 0x61637370) // 'acsp'
            {
                return null;
            }

            // Trust the declared size only when it fits; some writers pad the resource.
            var length = data.Length;
            var declared = IccBytes.U32(data, 0);
            if (declared >= 132 && declared < data.Length)
            {
                length = (int)declared;
            }

            var profile = new IccProfile();
            profile.EncodedVersion = IccBytes.U32(data, 8);
            profile.MajorVersion = data[8];
            profile.MinorVersion = data[9] >> 4;
            profile.DeviceClass = IccBytes.U32(data, 12);
            profile.ColorSpace = IccBytes.U32(data, 16);
            profile.Pcs = IccBytes.U32(data, 20);
            profile.HeaderIntent = (IccRenderingIntent)(IccBytes.U32(data, 64) & 3);

            var count = IccBytes.U32(data, 128);
            if (count > MaxTags || 132 + (count * 12) > length)
            {
                return null;
            }

            var signatures = new List<string>((int)count);
            for (var i = 0; i < (int)count; i++)
            {
                var entry = 132 + (i * 12);
                var signature = IccBytes.U32(data, entry);
                var offset = IccBytes.U32(data, entry + 4);
                var size = IccBytes.U32(data, entry + 8);
                signatures.Add(SignatureToString(signature));
                if (size < 8 || offset > length || size > length - offset)
                {
                    continue;
                }

                try
                {
                    profile.ReadTag(SignatureToString(signature), IccBytes.Slice(data, offset, size));
                }
                catch (Exception ex)
                {
                    if (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                    {
                        // A tag whose internal offsets point outside it is dropped.
                        continue;
                    }

                    throw;
                }
            }

            profile.TagSignatures = signatures;
            return profile;
        }

        private void ReadTag(string signature, byte[] tag)
        {
            switch (signature)
            {
                case "desc":
                    {
                        var text = ReadText(tag);
                        if (text != null)
                        {
                            Description = text;
                        }

                        break;
                    }

                case "wtpt":
                    MediaWhitePoint = ReadXyz(tag);
                    break;
                case "rXYZ":
                    RedColorant = ReadXyz(tag);
                    break;
                case "gXYZ":
                    GreenColorant = ReadXyz(tag);
                    break;
                case "bXYZ":
                    BlueColorant = ReadXyz(tag);
                    break;
                case "rTRC":
                    {
                        int consumed;
                        RedTrc = ReadCurve(tag, out consumed);
                        break;
                    }

                case "gTRC":
                    {
                        int consumed;
                        GreenTrc = ReadCurve(tag, out consumed);
                        break;
                    }

                case "bTRC":
                    {
                        int consumed;
                        BlueTrc = ReadCurve(tag, out consumed);
                        break;
                    }

                case "kTRC":
                    {
                        int consumed;
                        GrayTrc = ReadCurve(tag, out consumed);
                        break;
                    }

                case "chad":
                    if (TypeOf(tag) == "sf32" && tag.Length >= 44)
                    {
                        var matrix = new double[9];
                        for (var i = 0; i < 9; i++)
                        {
                            matrix[i] = IccLut.ReadS15Fixed16(tag, 8 + (i * 4));
                        }

                        ChromaticAdaptation = matrix;
                    }

                    break;
                case "A2B0":
                case "A2B1":
                case "A2B2":
                    _aToB[signature[3] - '0'] = ReadLut(tag, true);
                    break;
                case "B2A0":
                case "B2A1":
                case "B2A2":
                    _bToA[signature[3] - '0'] = ReadLut(tag, false);
                    break;
                case "D2B0":
                case "D2B1":
                case "D2B2":
                case "D2B3":
                    _dToB[signature[3] - '0'] = TypeOf(tag) == "mpet" ? ReadLut(tag, true) : null;
                    break;
            }
        }

        private IccLut ReadLut(byte[] tag, bool deviceToPcs)
        {
            var lut = IccLut.Parse(tag);
            var device = DeviceChannels;
            if (lut == null || device == 0)
            {
                return null;
            }

            // The device side must match the header color space and the PCS side has three channels.
            var deviceSide = deviceToPcs ? lut.InputChannels : lut.OutputChannels;
            var pcsSide = deviceToPcs ? lut.OutputChannels : lut.InputChannels;
            return deviceSide == device && pcsSide == 3 ? lut : null;
        }

        private static string TypeOf(byte[] tag)
        {
            return tag.Length >= 4 ? IccBytes.Ascii(tag, 0, 4) : "";
        }

        private static IccXyz? ReadXyz(byte[] tag)
        {
            if (TypeOf(tag) != "XYZ " || tag.Length < 20)
            {
                return null;
            }

            return new IccXyz(IccLut.ReadS15Fixed16(tag, 8), IccLut.ReadS15Fixed16(tag, 12), IccLut.ReadS15Fixed16(tag, 16));
        }

        /// <summary>Reads a <c>curv</c> or <c>para</c> element; <paramref name="consumed"/> is its unpadded byte length.</summary>
        internal static IccCurve ReadCurve(byte[] tag, out int consumed)
        {
            consumed = 0;
            if (tag.Length < 12)
            {
                return null;
            }

            switch (TypeOf(tag))
            {
                case "curv":
                    {
                        var count = IccBytes.U32(tag, 8);
                        if (count > (tag.Length - 12) / 2)
                        {
                            return null;
                        }

                        consumed = 12 + ((int)count * 2);
                        if (count == 0)
                        {
                            return IccCurve.Identity;
                        }

                        if (count == 1)
                        {
                            return IccCurve.FromGamma(IccBytes.U16(tag, 12) / 256.0);
                        }

                        var table = new float[count];
                        for (var i = 0; i < table.Length; i++)
                        {
                            table[i] = IccBytes.U16(tag, 12 + (i * 2)) / 65535f;
                        }

                        return IccCurve.FromTable(table);
                    }

                case "para":
                    {
                        var function = IccBytes.U16(tag, 8);
                        var counts = new[] { 1, 3, 4, 5, 7 };
                        if (function >= counts.Length || 12 + (counts[function] * 4) > tag.Length)
                        {
                            return null;
                        }

                        var parameters = new double[7];
                        for (var i = 0; i < counts[function]; i++)
                        {
                            parameters[i] = IccLut.ReadS15Fixed16(tag, 12 + (i * 4));
                        }

                        consumed = 12 + (counts[function] * 4);
                        return IccCurve.FromParametric(function, parameters);
                    }

                default:
                    return null;
            }
        }

        private static string ReadText(byte[] tag)
        {
            switch (TypeOf(tag))
            {
                case "desc":
                    {
                        if (tag.Length < 12)
                        {
                            return null;
                        }

                        var count = IccBytes.U32(tag, 8);
                        var length = (int)Math.Min(count, (uint)(tag.Length - 12));
                        return IccBytes.Ascii(tag, 12, length).TrimEnd('\0');
                    }

                case "text":
                    return IccBytes.Ascii(tag, 8, tag.Length - 8).TrimEnd('\0');
                case "mluc":
                    {
                        if (tag.Length < 16)
                        {
                            return null;
                        }

                        var records = IccBytes.U32(tag, 8);
                        var recordSize = IccBytes.U32(tag, 12);
                        if (recordSize < 12 || records == 0)
                        {
                            return null;
                        }

                        string first = null;
                        for (var i = 0L; i < records; i++)
                        {
                            var at = 16 + (i * recordSize);
                            if (at + 12 > tag.Length)
                            {
                                break;
                            }

                            var language = IccBytes.Ascii(tag, (int)at, 2);
                            var length = IccBytes.U32(tag, (int)(at + 4));
                            var offset = IccBytes.U32(tag, (int)(at + 8));
                            if (offset > tag.Length || length > tag.Length - offset)
                            {
                                continue;
                            }

                            var text = Encoding.BigEndianUnicode.GetString(tag, (int)offset, (int)length & ~1).TrimEnd('\0');
                            if (first == null)
                            {
                                first = text;
                            }

                            if (language == "en")
                            {
                                return text;
                            }
                        }

                        return first;
                    }

                default:
                    return null;
            }
        }

        private static int ColorSpaceChannels(uint space)
        {
            // 'nCLR' spaces (2CLR..FCLR) carry their channel count in the first character.
            var first = (char)(space >> 24);
            if ((space & 0x00FFFFFF) == 0x00434C52)
            {
                if (first >= '2' && first <= '9')
                {
                    return first - '0';
                }

                return first >= 'A' && first <= 'F' ? first - 'A' + 10 : 0;
            }

            return 0;
        }
    }
}
