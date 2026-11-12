using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using XRay.PsdFx.Icc;

namespace XRay.PsdFx
{
    /// <summary>
    /// A color lookup: optional per-channel 1D shapers, then an optional 3D lattice (red
    /// varies fastest), both on normalized RGB. Built from the LUT file or the ICC profile a
    /// Color Lookup layer embeds.
    /// </summary>
    internal sealed class ColorLookupTable
    {
        /// <summary>Largest 3D lattice accepted (the .cube specification's limit).</summary>
        public const int MaxSize = 256;

        /// <summary>Largest 1D table or shaper accepted.</summary>
        public const int MaxShaper = 65536;

        public ColorLookupTable()
        {
            Lattice = new float[0];
            LatticeMin = new[] { 0f, 0f, 0f };
            LatticeMax = new[] { 1f, 1f, 1f };
        }

        /// <summary>Lattice size per axis, 0 for a 1D-only lookup.</summary>
        public int Size { get; set; }

        /// <summary>Lattice outputs, three per node, node index r + Size * (g + Size * b).</summary>
        public float[] Lattice { get; set; }

        /// <summary>Per-channel input range of the lattice (.cube DOMAIN_MIN/DOMAIN_MAX).</summary>
        public float[] LatticeMin { get; set; }

        public float[] LatticeMax { get; set; }

        /// <summary>Per-channel shaper breakpoints (increasing) and values, applied before the lattice; null for none.</summary>
        public float[][] ShaperInputs { get; set; }

        public float[][] ShaperOutputs { get; set; }

        /// <summary>Maps one color; values outside the domains clamp to them.</summary>
        public void Apply(float red, float green, float blue, out float r, out float g, out float b)
        {
            var v = new[] { red, green, blue };
            var inputs = ShaperInputs;
            var outputs = ShaperOutputs;
            if (inputs != null && outputs != null)
            {
                for (var c = 0; c < 3; c++)
                {
                    v[c] = Piecewise(inputs[c], outputs[c], v[c]);
                }
            }

            if (Size < 2)
            {
                r = v[0];
                g = v[1];
                b = v[2];
                return;
            }

            // Trilinear interpolation between the eight surrounding nodes.
            var max = Size - 1;
            var index = new int[3];
            var fraction = new float[3];
            for (var c = 0; c < 3; c++)
            {
                var range = LatticeMax[c] - LatticeMin[c];
                var position = Compat.Clamp(range > 0 ? (v[c] - LatticeMin[c]) / range : 0f, 0f, 1f) * max;
                index[c] = Math.Min((int)position, max - 1);
                fraction[c] = position - index[c];
            }

            float sr = 0, sg = 0, sb = 0;
            for (var corner = 0; corner < 8; corner++)
            {
                var weight = 1f;
                var node = 0;
                for (var c = 2; c >= 0; c--)
                {
                    var upper = ((corner >> c) & 1) != 0;
                    weight *= upper ? fraction[c] : 1f - fraction[c];
                    node = (node * Size) + index[c] + (upper ? 1 : 0);
                }

                if (weight == 0f)
                {
                    continue;
                }

                var at = node * 3;
                sr += weight * Lattice[at];
                sg += weight * Lattice[at + 1];
                sb += weight * Lattice[at + 2];
            }

            r = sr;
            g = sg;
            b = sb;
        }

        private static float Piecewise(float[] xs, float[] ys, float x)
        {
            if (x <= xs[0])
            {
                return ys[0];
            }

            var last = xs.Length - 1;
            if (x >= xs[last])
            {
                return ys[last];
            }

            int low = 0, high = last;
            while (high - low > 1)
            {
                var mid = (low + high) >> 1;
                if (xs[mid] <= x)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            var span = xs[high] - xs[low];
            return span > 0 ? ys[low] + ((ys[high] - ys[low]) * (x - xs[low]) / span) : ys[low];
        }
    }

    /// <summary>
    /// Color Lookup (clrL): u16 version 1, then a version-16 descriptor. Photoshop stores the
    /// chosen lookup two ways: <c>profile</c> (raw data) holds an ICC profile (always for the
    /// abstract and device link types; Photoshop also keeps one for 3D LUT files), and for 3D
    /// LUT files <c>LUT3DFileData</c> holds the file itself, in the format <c>LUTFormat</c>
    /// names (LUTFormatCUBE, LUTFormat3DL, LUTFormatCSP; LUTFormatLOOK is not read). The
    /// profile wins when it parses, since it is what Photoshop renders from; otherwise the
    /// file is parsed, and a missing or unknown format is detected from the text. Rendering
    /// stays off unless <see cref="RenderOptions.RenderColorLookup"/> turns it on.
    /// </summary>
    internal static partial class Adjustments
    {
        private const int ProfileLatticeSize = 33;

        internal static ColorLookupTable ParseColorLookup(byte[] data)
        {
            if (data.Length < 6 || ((data[0] << 8) | data[1]) != 1)
            {
                return null;
            }

            var body = new byte[data.Length - 2];
            Array.Copy(data, 2, body, 0, body.Length);
            var descriptor = ReadVersionedDescriptor(body);
            if (descriptor == null)
            {
                return null;
            }

            DescriptorValue profile;
            if (descriptor.TryGet("profile", out profile) && profile.Type == DescriptorValueType.RawData)
            {
                var fromProfile = ColorLookupFromProfile(profile.Raw);
                if (fromProfile != null)
                {
                    return fromProfile;
                }
            }

            DescriptorValue file;
            if (!descriptor.TryGet("LUT3DFileData", out file) || file.Type != DescriptorValueType.RawData)
            {
                return null;
            }

            return ParseLutFile(file.Raw, descriptor.GetEnum("LUTFormat"));
        }

        /// <summary>Parses a .cube, .3dl or .csp file (the format named, or detected from the text).</summary>
        internal static ColorLookupTable ParseLutFile(byte[] bytes, string format = null)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 64 << 20)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(bytes);
            try
            {
                switch (format)
                {
                    case "LUTFormatCUBE":
                        return ParseCube(text);
                    case "LUTFormat3DL":
                        return Parse3dl(text);
                    case "LUTFormatCSP":
                        return ParseCsp(text);
                    default:
                        if (text.TrimStart().StartsWith("CSPLUTV100", StringComparison.Ordinal))
                        {
                            return ParseCsp(text);
                        }

                        return text.IndexOf("LUT_3D_SIZE", StringComparison.Ordinal) >= 0
                            || text.IndexOf("LUT_1D_SIZE", StringComparison.Ordinal) >= 0
                            ? ParseCube(text)
                            : Parse3dl(text);
                }
            }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is OverflowException || ex is IndexOutOfRangeException || ex is ArgumentException)
                {
                    return null;
                }

                throw;
            }
        }

        /// <summary>
        /// Adobe's Cube LUT format: LUT_3D_SIZE and/or LUT_1D_SIZE, DOMAIN_MIN/DOMAIN_MAX (or
        /// Resolve's LUT_1D_INPUT_RANGE and LUT_3D_INPUT_RANGE), then the rows: the 1D table
        /// first when both are present (a shaper in front of the lattice), the lattice with red
        /// varying fastest.
        /// </summary>
        private static ColorLookupTable ParseCube(string text)
        {
            int size3 = 0, size1 = 0;
            var min = new[] { 0f, 0f, 0f };
            var max = new[] { 1f, 1f, 1f };
            float[] min1 = null, max1 = null, min3 = null, max3 = null;
            var rows = new List<float>();
            foreach (var raw in Lines(text))
            {
                var parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "TITLE":
                        continue;
                    case "LUT_3D_SIZE":
                        size3 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        continue;
                    case "LUT_1D_SIZE":
                        size1 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        continue;
                    case "DOMAIN_MIN":
                        min = new[] { Number(parts[1]), Number(parts[2]), Number(parts[3]) };
                        continue;
                    case "DOMAIN_MAX":
                        max = new[] { Number(parts[1]), Number(parts[2]), Number(parts[3]) };
                        continue;
                    case "LUT_1D_INPUT_RANGE":
                        min1 = new[] { Number(parts[1]), Number(parts[1]), Number(parts[1]) };
                        max1 = new[] { Number(parts[2]), Number(parts[2]), Number(parts[2]) };
                        continue;
                    case "LUT_3D_INPUT_RANGE":
                        min3 = new[] { Number(parts[1]), Number(parts[1]), Number(parts[1]) };
                        max3 = new[] { Number(parts[2]), Number(parts[2]), Number(parts[2]) };
                        continue;
                }

                if (char.IsLetter(parts[0][0]))
                {
                    continue; // Unknown keywords are ignored, as the specification asks.
                }

                if (parts.Length != 3)
                {
                    return null;
                }

                rows.Add(Number(parts[0]));
                rows.Add(Number(parts[1]));
                rows.Add(Number(parts[2]));
                if (rows.Count > (ColorLookupTable.MaxShaper + (ColorLookupTable.MaxSize * ColorLookupTable.MaxSize * ColorLookupTable.MaxSize)) * 3)
                {
                    return null;
                }
            }

            var size3Invalid = size3 < 0 || size3 == 1 || size3 > ColorLookupTable.MaxSize;
            var size1Invalid = size1 < 0 || size1 == 1 || size1 > ColorLookupTable.MaxShaper;
            if ((size3 == 0 && size1 == 0) || size3Invalid || size1Invalid)
            {
                return null;
            }

            var nodes = size3 * size3 * size3;
            if (rows.Count != (size1 + nodes) * 3)
            {
                return null;
            }

            float[][] shaperInputs = null, shaperOutputs = null;
            if (size1 > 0)
            {
                var low = min1 ?? min;
                var high = max1 ?? max;
                shaperInputs = new float[3][];
                shaperOutputs = new float[3][];
                for (var c = 0; c < 3; c++)
                {
                    shaperInputs[c] = new float[size1];
                    shaperOutputs[c] = new float[size1];
                    for (var i = 0; i < size1; i++)
                    {
                        shaperInputs[c][i] = low[c] + ((high[c] - low[c]) * i / (size1 - 1));
                        shaperOutputs[c][i] = rows[(i * 3) + c];
                    }
                }
            }

            return new ColorLookupTable
            {
                Size = size3,
                Lattice = rows.Skip(size1 * 3).ToArray(),
                LatticeMin = min3 ?? (size1 > 0 ? new[] { 0f, 0f, 0f } : min),
                LatticeMax = max3 ?? (size1 > 0 ? new[] { 1f, 1f, 1f } : max),
                ShaperInputs = shaperInputs,
                ShaperOutputs = shaperOutputs,
            };
        }

        /// <summary>
        /// Autodesk's .3dl: an optional 3DMESH and "Mesh inBits outBits" header, a line of N
        /// input positions (the lattice nodes on the input scale, 0..1023 for 10 bits), then
        /// N^3 integer rows with blue varying fastest. The input scale is the smallest
        /// 2^k - 1 at or above the last position; the output scale comes from the Mesh line,
        /// else from the largest value (10, 12, 14 or 16 bits).
        /// </summary>
        private static ColorLookupTable Parse3dl(string text)
        {
            int[] positions = null;
            var outputBits = 0;
            var rows = new List<int>();
            foreach (var raw in Lines(text))
            {
                var parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "3DMESH")
                {
                    continue;
                }

                if (parts[0] == "Mesh" && parts.Length >= 3)
                {
                    outputBits = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    continue;
                }

                if (char.IsLetter(parts[0][0]))
                {
                    continue;
                }

                var values = parts.Select(p => int.Parse(p, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
                if (positions == null)
                {
                    if (values.Length < 2 || values.Length > ColorLookupTable.MaxSize)
                    {
                        return null;
                    }

                    positions = values;
                    continue;
                }

                if (values.Length != 3)
                {
                    return null;
                }

                rows.AddRange(values);
                if (rows.Count > ColorLookupTable.MaxSize * ColorLookupTable.MaxSize * ColorLookupTable.MaxSize * 3)
                {
                    return null;
                }
            }

            if (positions == null)
            {
                return null;
            }

            var size = positions.Length;
            if (rows.Count != size * size * size * 3)
            {
                return null;
            }

            var inputScale = BitScale(positions[positions.Length - 1]);
            var largest = 0;
            foreach (var row in rows)
            {
                if (row > largest)
                {
                    largest = row;
                }
            }

            var outputScale = outputBits > 0 && outputBits <= 16 ? (1 << outputBits) - 1 : BitScale(Math.Max(largest, 1023));

            // Rows run with blue fastest; the lattice wants red fastest.
            var lattice = new float[rows.Count];
            for (var r = 0; r < size; r++)
            {
                for (var g = 0; g < size; g++)
                {
                    for (var b = 0; b < size; b++)
                    {
                        var from = (((r * size) + g) * size) + b;
                        var to = (((b * size) + g) * size) + r;
                        for (var c = 0; c < 3; c++)
                        {
                            lattice[(to * 3) + c] = rows[(from * 3) + c] / (float)outputScale;
                        }
                    }
                }
            }

            return new ColorLookupTable
            {
                Size = size,
                Lattice = lattice,
                ShaperInputs = UniformShaper(positions, inputScale),
                ShaperOutputs = UniformShaperOutputs(size),
            };
        }

        /// <summary>
        /// cineSpace .csp: CSPLUTV100, the type (1D or 3D), an optional metadata block, three
        /// per-channel pre-LUTs (a count, the input breakpoints, the values), then for 3D the
        /// lattice size line and its rows with red varying fastest. For a 1D file the pre-LUTs
        /// are the whole lookup.
        /// </summary>
        private static ColorLookupTable ParseCsp(string text)
        {
            var lines = new List<string>();
            var inMetadata = false;
            foreach (var line in Lines(text))
            {
                if (line.StartsWith("BEGIN METADATA", StringComparison.Ordinal))
                {
                    inMetadata = true;
                }
                else if (line.StartsWith("END METADATA", StringComparison.Ordinal))
                {
                    inMetadata = false;
                }
                else if (!inMetadata)
                {
                    lines.Add(line);
                }
            }

            if (lines.Count < 2 || lines[0] != "CSPLUTV100" || (lines[1] != "1D" && lines[1] != "3D"))
            {
                return null;
            }

            var is3d = lines[1] == "3D";
            var at = 2;
            var inputs = new float[3][];
            var outputs = new float[3][];
            for (var c = 0; c < 3; c++)
            {
                if (at + 2 >= lines.Count)
                {
                    return null;
                }

                var count = int.Parse(lines[at++], CultureInfo.InvariantCulture);
                inputs[c] = Numbers(lines[at++]);
                outputs[c] = Numbers(lines[at++]);
                if (count < 2 || count > ColorLookupTable.MaxShaper || inputs[c].Length != count || outputs[c].Length != count)
                {
                    return null;
                }

                for (var i = 1; i < count; i++)
                {
                    if (!(inputs[c][i] > inputs[c][i - 1]))
                    {
                        return null;
                    }
                }
            }

            if (!is3d)
            {
                return new ColorLookupTable { ShaperInputs = inputs, ShaperOutputs = outputs };
            }

            if (at >= lines.Count)
            {
                return null;
            }

            var sizes = lines[at++].Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (sizes.Length != 3 || sizes[0] != sizes[1] || sizes[1] != sizes[2] || sizes[0] < 2 || sizes[0] > ColorLookupTable.MaxSize)
            {
                return null;
            }

            var size = sizes[0];
            var nodes = size * size * size;
            if (lines.Count - at < nodes)
            {
                return null;
            }

            var lattice = new float[nodes * 3];
            for (var i = 0; i < nodes; i++)
            {
                var row = Numbers(lines[at + i]);
                if (row.Length != 3)
                {
                    return null;
                }

                Array.Copy(row, 0, lattice, i * 3, 3);
            }

            return new ColorLookupTable { Size = size, Lattice = lattice, ShaperInputs = inputs, ShaperOutputs = outputs };
        }

        /// <summary>
        /// An ICC device link (RGB to RGB) or abstract profile (PCS to PCS) sampled on a 33-node
        /// lattice. Device links read the document's RGB values directly; abstract profiles take
        /// them through sRGB to the D50 PCS and back.
        /// </summary>
        internal static ColorLookupTable ColorLookupFromProfile(byte[] bytes)
        {
            var profile = IccProfile.Parse(bytes);
            var lut = profile == null ? null : profile.GetAToB(0);
            if (lut == null || lut.InputChannels != 3 || lut.OutputChannels != 3)
            {
                return null;
            }

            const uint classLink = 0x6C696E6B; // 'link'
            const uint classAbstract = 0x61627374; // 'abst'
            var colorIsLabOrXyz = profile.ColorSpace == IccProfile.SpaceLab || profile.ColorSpace == IccProfile.SpaceXyz;
            var pcsIsLabOrXyz = profile.Pcs == IccProfile.SpaceLab || profile.Pcs == IccProfile.SpaceXyz;
            var link = profile.DeviceClass == classLink && profile.ColorSpace == IccProfile.SpaceRgb && profile.Pcs == IccProfile.SpaceRgb;
            var abstractProfile = profile.DeviceClass == classAbstract && colorIsLabOrXyz && pcsIsLabOrXyz;
            if (!link && !abstractProfile)
            {
                return null;
            }

            const int size = ProfileLatticeSize;
            var lattice = new float[size * size * size * 3];
            var input = new double[3];
            var output = new double[IccLut.MaxChannels];
            var m = IccPcs.SrgbToXyzD50;
            for (var b = 0; b < size; b++)
            {
                for (var g = 0; g < size; g++)
                {
                    for (var r = 0; r < size; r++)
                    {
                        double red = r / (double)(size - 1), green = g / (double)(size - 1), blue = b / (double)(size - 1);
                        if (link)
                        {
                            input[0] = red;
                            input[1] = green;
                            input[2] = blue;
                        }
                        else
                        {
                            var lr = IccPcs.DecodeSrgb(red);
                            var lg = IccPcs.DecodeSrgb(green);
                            var lb = IccPcs.DecodeSrgb(blue);
                            var xyz = new IccXyz((m[0] * lr) + (m[1] * lg) + (m[2] * lb), (m[3] * lr) + (m[4] * lg) + (m[5] * lb), (m[6] * lr) + (m[7] * lg) + (m[8] * lb));
                            IccPcs.Encode(profile.ColorSpace, lut.LabEncoding, xyz, input);
                        }

                        lut.Evaluate(input, output);
                        var at = ((((b * size) + g) * size) + r) * 3;
                        if (link)
                        {
                            lattice[at] = (float)output[0];
                            lattice[at + 1] = (float)output[1];
                            lattice[at + 2] = (float)output[2];
                        }
                        else
                        {
                            double sr, sg, sb;
                            IccPcs.XyzToLinearSrgb(IccPcs.Decode(profile.Pcs, lut.LabEncoding, output), out sr, out sg, out sb);
                            lattice[at] = (float)IccPcs.EncodeSrgb(sr);
                            lattice[at + 1] = (float)IccPcs.EncodeSrgb(sg);
                            lattice[at + 2] = (float)IccPcs.EncodeSrgb(sb);
                        }
                    }
                }
            }

            return new ColorLookupTable { Size = size, Lattice = lattice };
        }

        private static IEnumerable<string> Lines(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed[0] != '#')
                {
                    yield return trimmed;
                }
            }
        }

        private static float Number(string text)
        {
            var value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new FormatException("LUT value is not finite.");
            }

            return value;
        }

        private static float[] Numbers(string line)
        {
            return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
        }

        /// <summary>The smallest 2^k - 1 at or above <paramref name="value"/> (at least 255).</summary>
        private static int BitScale(int value)
        {
            var scale = 255;
            while (scale < value && scale < int.MaxValue / 2)
            {
                scale = (scale * 2) + 1;
            }

            return scale;
        }

        // A .3dl input line: node positions on the input scale, mapped onto the lattice's [0,1].
        private static float[][] UniformShaper(int[] positions, int scale)
        {
            var xs = positions.Select(p => p / (float)scale).ToArray();
            return new[] { xs, xs, xs };
        }

        private static float[][] UniformShaperOutputs(int size)
        {
            var ys = Enumerable.Range(0, size).Select(i => i / (float)(size - 1)).ToArray();
            return new[] { ys, ys, ys };
        }

        /// <summary>Color Lookup through a parsed table, per pixel on the float backdrop.</summary>
        private sealed class ColorLookupAdjustment : IAdjustment
        {
            private readonly ColorLookupTable _table;

            public ColorLookupAdjustment(ColorLookupTable table)
            {
                _table = table;
            }

            public void Apply(float[] r, float[] g, float[] b)
            {
                for (var i = 0; i < r.Length; i++)
                {
                    float or, og, ob;
                    _table.Apply(r[i], g[i], b[i], out or, out og, out ob);
                    r[i] = Compat.Clamp(or, 0f, 1f);
                    g[i] = Compat.Clamp(og, 0f, 1f);
                    b[i] = Compat.Clamp(ob, 0f, 1f);
                }
            }
        }
    }
}
