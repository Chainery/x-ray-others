using System;
using System.Collections.Generic;
using System.Globalization;

namespace XRay.PsdFx
{
    /// <summary>A decoded pattern tile: straight RGBA bytes.</summary>
    internal sealed class PatternTile
    {
        public PatternTile(string id, string name, int width, int height, byte[] rgba)
        {
            Id = id;
            Name = name;
            Width = width;
            Height = height;
            Rgba = rgba;
        }

        public string Id { get; private set; }

        public string Name { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public byte[] Rgba { get; private set; }
    }

    /// <summary>Placement of a pattern (overlay effect, pattern fill layer, bevel texture).</summary>
    internal sealed class PatternPlacement
    {
        public PatternPlacement(string patternId, float scale, float angleDegrees, bool linkWithLayer, float phaseX, float phaseY)
        {
            PatternId = patternId;
            Scale = scale;
            AngleDegrees = angleDegrees;
            LinkWithLayer = linkWithLayer;
            PhaseX = phaseX;
            PhaseY = phaseY;
        }

        public string PatternId { get; private set; }

        public float Scale { get; private set; }

        public float AngleDegrees { get; private set; }

        public bool LinkWithLayer { get; private set; }

        public float PhaseX { get; private set; }

        public float PhaseY { get; private set; }

        public static PatternPlacement FromDescriptor(Descriptor descriptor)
        {
            var pattern = descriptor.GetObject("Ptrn");
            var id = pattern != null ? pattern.GetString("Idnt") : null;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var phase = descriptor.GetObject("phase");
            return new PatternPlacement(
                id,
                Math.Max(0.01f, (float)(descriptor.GetNumber("Scl ", 100) / 100)),
                (float)descriptor.GetNumber("Angl", 0),
                descriptor.GetBoolean("Algn", true),
                (float)(phase != null ? phase.GetNumber("Hrzn", 0) : 0),
                (float)(phase != null ? phase.GetNumber("Vrtc", 0) : 0));
        }
    }

    /// <summary>
    /// The document's pattern store, decoded from the global Patt/Pat2/Pat3 blocks
    /// (.reference/src/psd/psd_patterns.cpp): one record per pattern with a
    /// virtual-memory-array of channel planes (raw or PackBits, 8 or 16 bit).
    /// </summary>
    internal static class PatternStore
    {
        private const int MaxPixels = 64 * 1024 * 1024;

        public static Dictionary<string, PatternTile> Parse(PsdDocument document)
        {
            var patterns = new Dictionary<string, PatternTile>(StringComparer.Ordinal);
            var blocks = document.GlobalTaggedBlocks;
            if (blocks == null)
            {
                return patterns;
            }

            foreach (var block in blocks)
            {
                var key = block.Key;
                if (key != "Patt" && key != "Pat2" && key != "Pat3")
                {
                    continue;
                }

                var reader = new BigEndianReader(block.Data);
                try
                {
                    while (reader.Remaining >= 16)
                    {
                        var tile = ParseOne(reader, document.Colors);
                        if (tile != null && !patterns.ContainsKey(tile.Id))
                        {
                            patterns.Add(tile.Id, tile);
                        }
                    }
                }
                catch (PsdFormatException)
                {
                    // Keep what decoded cleanly.
                }
            }

            return patterns;
        }

        private static PatternTile ParseOne(BigEndianReader reader, DocumentColors colors)
        {
            var length = reader.ReadUInt32();
            if (length < 16 || length > (uint)reader.Remaining)
            {
                throw new PsdFormatException("Invalid pattern length.");
            }

            var end = reader.Position + (int)length;
            PatternTile result = null;
            try
            {
                var version = reader.ReadUInt32();
                var mode = reader.ReadUInt32();
                var height = (int)reader.ReadUInt16();
                var width = (int)reader.ReadUInt16();
                var name = reader.ReadUnicodeString();
                var id = BigEndianReader.DecodeLatin1(reader.ReadBytes(reader.ReadByte())).TrimEnd('\0');
                byte[] colorTable = null;
                if (version == 1 && mode == 2)
                {
                    colorTable = reader.ReadBytes(768);
                }

                var supported = mode == 1 || mode == 2 || mode == 3 || mode == 4 || mode == 7;
                if (version == 1 && supported && width > 0 && height > 0 && (long)width * height <= MaxPixels && id.Length > 0)
                {
                    result = DecodeImage(reader, end, (int)mode, width, height, id, name, colorTable, colors);
                }
            }
            catch (PsdFormatException)
            {
                result = null;
            }

            reader.Position = end;
            var padding = (int)((4 - ((4 + length) % 4)) % 4);
            reader.Skip(Math.Min(padding, reader.Remaining));
            return result;
        }

        // __PART3__
        private static PatternTile DecodeImage(BigEndianReader reader, int patternEnd, int mode, int width, int height, string id, string name, byte[] colorTable, DocumentColors colors)
        {
            var vmaVersion = reader.ReadUInt32();
            var vmaLength = reader.ReadUInt32();
            if (vmaVersion != 3 || vmaLength < 20 || vmaLength > (uint)(patternEnd - reader.Position))
            {
                return null;
            }

            var vmaEnd = reader.Position + (int)vmaLength;
            reader.Skip(16);
            var declaredChannels = reader.ReadUInt32();
            if (declaredChannels > 64)
            {
                return null;
            }

            var colorCount = mode == 3 ? 3 : (mode == 4 ? 4 : 1);
            var planes = new Plane[colorCount];
            var hasPlane = new bool[colorCount];
            Plane alpha = null;
            for (var slot = 0u; slot < declaredChannels + 2 && reader.Position < vmaEnd; slot++)
            {
                var relevant = slot < (uint)colorCount || slot == declaredChannels + 1;
                var plane = ReadPlane(reader, vmaEnd, relevant);
                if (plane == null)
                {
                    continue;
                }

                if (slot < (uint)colorCount)
                {
                    planes[slot] = plane;
                    hasPlane[slot] = true;
                }
                else if (slot == declaredChannels + 1)
                {
                    alpha = plane;
                }
            }

            var any = false;
            foreach (var flag in hasPlane)
            {
                any |= flag;
            }

            if (!any)
            {
                return null;
            }

            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = ((y * width) + x) * 4;
                    switch (mode)
                    {
                        case 2:
                            {
                                var index = Sample(planes[0], x, y, 0);
                                rgba[o] = colorTable[index * 3];
                                rgba[o + 1] = colorTable[(index * 3) + 1];
                                rgba[o + 2] = colorTable[(index * 3) + 2];
                                break;
                            }

                        case 3:
                            rgba[o] = Sample(planes[0], x, y, 0);
                            rgba[o + 1] = Sample(planes[1], x, y, 0);
                            rgba[o + 2] = Sample(planes[2], x, y, 0);
                            break;
                        case 4:
                            {
                                var k = Sample(planes[3], x, y, 255);
                                rgba[o] = (byte)(Sample(planes[0], x, y, 255) * k / 255);
                                rgba[o + 1] = (byte)(Sample(planes[1], x, y, 255) * k / 255);
                                rgba[o + 2] = (byte)(Sample(planes[2], x, y, 255) * k / 255);
                                break;
                            }

                        default:
                            {
                                var gray = Sample(planes[0], x, y, 0);
                                rgba[o] = gray;
                                rgba[o + 1] = gray;
                                rgba[o + 2] = gray;
                                break;
                            }
                    }

                    rgba[o + 3] = alpha == null ? (byte)255 : Sample(alpha, x, y, 255);
                }
            }

            ConvertTile(colors, mode, planes, width, height, rgba);
            return new PatternTile(id, name, width, height, rgba);
        }

        // __PART4__
        /// <summary>
        /// Converts a tile in the document's own color space through its ICC profile,
        /// like the pixels: CMYK tiles in CMYK documents, and, because this port
        /// converts RGB and gray pixels too, RGB and indexed tiles in RGB documents and
        /// gray tiles in grayscale documents. Other tiles keep the built-in formulas.
        /// </summary>
        private static void ConvertTile(DocumentColors colors, int mode, Plane[] planes, int width, int height, byte[] rgba)
        {
            var channels = mode == 4 ? 4 : ((mode == 2 || mode == 3) ? 3 : (mode == 1 ? 1 : 0));
            if (channels == 0 || colors.TransformFor(channels) == null)
            {
                return;
            }

            var count = width * height;
            var device = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                device[c] = new float[count];
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    if (mode == 4)
                    {
                        // Inverted inks (255 = no ink), the PSD channel convention the transform expects.
                        for (var c = 0; c < 4; c++)
                        {
                            device[c][i] = Sample(planes[c], x, y, 255) / 255f;
                        }
                    }
                    else
                    {
                        // Gray, RGB and indexed tiles already hold their device values in the RGBA bytes.
                        for (var c = 0; c < channels; c++)
                        {
                            device[c][i] = rgba[(i * 4) + c] / 255f;
                        }
                    }
                }
            }

            var image = new PlanarImage(new PsdRect(0, 0, width, height));
            if (!colors.TryConvert(device, image))
            {
                return;
            }

            var bytes = new byte[count];
            for (var offset = 0; offset < 3; offset++)
            {
                var plane = offset == 0 ? image.R : (offset == 1 ? image.G : image.B);
                PlanarImage.UnitFloatToBytes(plane, bytes);
                for (var i = 0; i < count; i++)
                {
                    rgba[(i * 4) + offset] = bytes[i];
                }
            }
        }

        private sealed class Plane
        {
            public Plane(int top, int left, int width, int height, byte[] samples)
            {
                Top = top;
                Left = left;
                Width = width;
                Height = height;
                Samples = samples;
            }

            public int Top { get; private set; }

            public int Left { get; private set; }

            public int Width { get; private set; }

            public int Height { get; private set; }

            public byte[] Samples { get; private set; }
        }

        private static byte Sample(Plane plane, int x, int y, byte fallback)
        {
            if (plane == null)
            {
                return fallback;
            }

            var px = x - plane.Left;
            var py = y - plane.Top;
            return px < 0 || py < 0 || px >= plane.Width || py >= plane.Height ? fallback : plane.Samples[(py * plane.Width) + px];
        }

        // __PART5__
        private static Plane ReadPlane(BigEndianReader reader, int containerEnd, bool decode)
        {
            if (containerEnd - reader.Position < 4)
            {
                throw new PsdFormatException("Pattern channel list is truncated.");
            }

            var written = reader.ReadUInt32();
            if (written == 0)
            {
                return null;
            }

            var length = reader.ReadUInt32();
            if (length == 0)
            {
                return null;
            }

            if (length < 23 || length > (uint)(containerEnd - reader.Position))
            {
                throw new PsdFormatException("Pattern channel is truncated.");
            }

            var end = reader.Position + (int)length;
            if (!decode)
            {
                reader.Position = end;
                return null;
            }

            var depth = reader.ReadUInt32();
            var top = reader.ReadInt32();
            var left = reader.ReadInt32();
            var bottom = reader.ReadInt32();
            var right = reader.ReadInt32();
            var pixelDepth = reader.ReadUInt16();
            var compression = reader.ReadByte();
            var width = right - left;
            var height = bottom - top;
            var depthOk = depth == 8 || depth == 16;
            var pixelDepthOk = pixelDepth == 8 || pixelDepth == 16;
            if (width <= 0 || height <= 0 || width > 30000 || height > 30000 || (long)width * height > MaxPixels || !depthOk || !pixelDepthOk)
            {
                throw new PsdFormatException("Pattern channel is invalid.");
            }

            var bytesPerSample = pixelDepth / 8;
            var rowBytes = width * bytesPerSample;
            var raw = new byte[rowBytes * height];
            if (compression == 0)
            {
                Array.Copy(reader.ReadBytes(raw.Length), raw, raw.Length);
            }
            else if (compression == 1)
            {
                var counts = reader.ReadBytes(height * 2);
                ChannelCodec.DecodeRle(reader.Data, reader.Position, end - reader.Position, counts, 0, counts.Length, 2, height, rowBytes, raw);
            }
            else
            {
                throw new PsdFormatException("Unsupported pattern compression.");
            }

            var samples = new byte[width * height];
            if (bytesPerSample == 1)
            {
                Array.Copy(raw, samples, samples.Length);
            }
            else
            {
                // Photoshop's 16-bit samples use the 0..32768 scale.
                for (var i = 0; i < samples.Length; i++)
                {
                    var value = (uint)((raw[i * 2] << 8) | raw[(i * 2) + 1]);
                    samples[i] = (byte)Math.Min(255u, ((value * 255u) + 16384u) / 32768u);
                }
            }

            reader.Position = end;
            return new Plane(top, left, width, height, samples);
        }
    }
    /// <summary>
    /// Wrap-tiled pattern sampler in document space, ported from
    /// .reference/src/core/pattern_sampler.hpp: linked patterns anchor at the
    /// layer's effects reference point (fxrp), unlinked at the document
    /// origin, plus the phase. 100% is nearest-texel, magnification and rotation
    /// interpolate linearly, minification box-averages the footprint.
    /// </summary>
    internal sealed class PatternSampler
    {
        private readonly PatternTile _tile;
        private readonly double _anchorX;
        private readonly double _anchorY;
        private readonly double _inverseScale;
        private readonly bool _rotated;
        private readonly double _cos;
        private readonly double _sin;
        private readonly bool _nearest;
        private readonly bool _box;

        public PatternSampler(PatternTile tile, PatternPlacement placement, PsdLayer layer)
        {
            _tile = tile;
            double anchorX = placement.PhaseX;
            double anchorY = placement.PhaseY;
            var fxrp = placement.LinkWithLayer && layer != null ? layer.GetTaggedBlock("fxrp") : null;
            if (fxrp != null && fxrp.Length == 16)
            {
                var reader = new BigEndianReader(fxrp);
                anchorX += reader.ReadDouble();
                anchorY += reader.ReadDouble();
            }

            _anchorX = anchorX;
            _anchorY = anchorY;
            var scale = Math.Max(0.01, placement.Scale);
            _inverseScale = 1.0 / scale;
            var radians = placement.AngleDegrees * Math.PI / 180.0;
            _rotated = Math.Abs(radians) > 1e-9;
            _cos = Math.Cos(radians);
            _sin = Math.Sin(radians);
            _nearest = !_rotated && Math.Abs(scale - 1.0) < 1e-6;
            _box = !_nearest && !_rotated && scale < 1.0;
        }

        private static int Wrap(long index, int size)
        {
            var value = (int)(index % size);
            return value < 0 ? value + size : value;
        }

        private void Texel(int x, int y, out float r, out float g, out float b, out float a)
        {
            var o = ((y * _tile.Width) + x) * 4;
            var p = _tile.Rgba;
            r = p[o];
            g = p[o + 1];
            b = p[o + 2];
            a = p[o + 3] / 255f;
        }

        /// <summary>Straight color (0..1) and alpha at a document pixel.</summary>
        public void Sample(int x, int y, out float outR, out float outG, out float outB, out float outA)
        {
            var width = _tile.Width;
            var height = _tile.Height;
            if (_nearest)
            {
                float r, g, b, a;
                Texel(Wrap((long)Math.Floor(x + 0.5 - _anchorX), width), Wrap((long)Math.Floor(y + 0.5 - _anchorY), height), out r, out g, out b, out a);
                outR = r / 255f;
                outG = g / 255f;
                outB = b / 255f;
                outA = a;
                return;
            }

            if (_box)
            {
                var u0 = (x - _anchorX) * _inverseScale;
                var u1 = (x + 1.0 - _anchorX) * _inverseScale;
                var v0 = (y - _anchorY) * _inverseScale;
                var v1 = (y + 1.0 - _anchorY) * _inverseScale;
                double sr = 0, sg = 0, sb = 0, sa = 0, total = 0;
                for (var ty = (long)Math.Floor(v0); ty <= (long)Math.Ceiling(v1) - 1; ty++)
                {
                    var coverY = Math.Min(v1, ty + 1.0) - Math.Max(v0, ty);
                    if (coverY <= 0)
                    {
                        continue;
                    }

                    var row = Wrap(ty, height);
                    for (var tx = (long)Math.Floor(u0); tx <= (long)Math.Ceiling(u1) - 1; tx++)
                    {
                        var coverX = Math.Min(u1, tx + 1.0) - Math.Max(u0, tx);
                        if (coverX <= 0)
                        {
                            continue;
                        }

                        float r, g, b, a;
                        var weight = coverX * coverY;
                        Texel(Wrap(tx, width), row, out r, out g, out b, out a);
                        sr += weight * r;
                        sg += weight * g;
                        sb += weight * b;
                        sa += weight * a;
                        total += weight;
                    }
                }

                if (total <= 0)
                {
                    outR = outG = outB = outA = 0f;
                    return;
                }

                outR = (float)(Math.Round(sr / total) / 255);
                outG = (float)(Math.Round(sg / total) / 255);
                outB = (float)(Math.Round(sb / total) / 255);
                outA = (float)Compat.Clamp(sa / total, 0.0, 1.0);
                return;
            }

            var u = x - _anchorX;
            var v = y - _anchorY;
            if (_rotated)
            {
                var ru = (u * _cos) - (v * _sin);
                var rv = (u * _sin) + (v * _cos);
                u = ru;
                v = rv;
            }

            u *= _inverseScale;
            v *= _inverseScale;
            var fu = Math.Floor(u);
            var fv = Math.Floor(v);
            var fx = (float)(u - fu);
            var fy = (float)(v - fv);
            var x0 = Wrap((long)fu, width);
            var y0 = Wrap((long)fv, height);
            var x1 = x0 + 1 == width ? 0 : x0 + 1;
            var y1 = y0 + 1 == height ? 0 : y0 + 1;
            float tlr, tlg, tlb, tla;
            float trr, trg, trb, tra;
            float blr, blg, blb, bla;
            float brr, brg, brb, bra;
            Texel(x0, y0, out tlr, out tlg, out tlb, out tla);
            Texel(x1, y0, out trr, out trg, out trb, out tra);
            Texel(x0, y1, out blr, out blg, out blb, out bla);
            Texel(x1, y1, out brr, out brg, out brb, out bra);
            outR = (float)Math.Round(Mix(tlr, trr, blr, brr, fx, fy)) / 255f;
            outG = (float)Math.Round(Mix(tlg, trg, blg, brg, fx, fy)) / 255f;
            outB = (float)Math.Round(Mix(tlb, trb, blb, brb, fx, fy)) / 255f;
            outA = Mix(tla, tra, bla, bra, fx, fy);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + ((b - a) * t);
        }

        private static float Mix(float a, float b, float c, float d, float fx, float fy)
        {
            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }
    }}
