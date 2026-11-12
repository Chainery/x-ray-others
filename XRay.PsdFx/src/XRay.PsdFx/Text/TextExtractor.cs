using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace XRay.PsdFx
{
    /// <summary>Where a piece of extracted text came from.</summary>
    public enum PsdTextKind
    {
        /// <summary>Name of a pixel, text, shape, adjustment or smart-object layer.</summary>
        LayerName,

        /// <summary>Name of a layer group.</summary>
        GroupName,

        /// <summary>Content of a type layer.</summary>
        TextLayer,

        /// <summary>Name of a saved alpha or spot channel.</summary>
        ChannelName,

        /// <summary>Name of a saved path.</summary>
        PathName,

        /// <summary>Slice name, URL, message, alt text or cell text.</summary>
        Slice,

        /// <summary>Document metadata (XMP or IPTC title, description, keywords, headline, author, caption).</summary>
        Metadata,

        /// <summary>File name of an embedded or linked smart-object source.</summary>
        LinkedFileName,

        /// <summary>
        /// A text object of the document's <c>Txt2</c> block whose text no extracted type
        /// layer reports: one no layer references, or one whose text differs from its
        /// layer's own record (Photoshop displays the <c>Txt2</c> version).
        /// </summary>
        TextEngineObject,
    }

    /// <summary>One extracted string.</summary>
    public sealed class PsdTextItem
    {
        public PsdTextItem(PsdTextKind kind, string text, string source, PsdLayer layer, int depth)
        {
            Kind = kind;
            Text = text;
            Source = source;
            Layer = layer;
            Depth = depth;
        }

        /// <summary>What the text is.</summary>
        public PsdTextKind Kind { get; private set; }

        /// <summary>The text, paragraph breaks normalized to \n.</summary>
        public string Text { get; private set; }

        /// <summary>
        /// Context: the layer path for layer items (<c>Group/Layer</c>), the field name
        /// for metadata and slices. Items from embedded documents are prefixed with the
        /// smart-object layer path and <c>&gt;</c>.
        /// </summary>
        public string Source { get; private set; }

        /// <summary>The layer the text belongs to, for items of the top-level document.</summary>
        public PsdLayer Layer { get; private set; }

        /// <summary>0 for the top-level document, 1+ for text inside embedded smart objects.</summary>
        public int Depth { get; private set; }

        /// <summary>Style runs and fonts for <see cref="PsdTextKind.TextLayer"/> items.</summary>
        public TextLayerInfo TextInfo { get; set; }
    }

    /// <summary>Options for <see cref="PsdDocument.ExtractText"/>.</summary>
    public sealed class TextExtractionOptions
    {
        public bool IncludeLayerNames { get; set; } = true;

        public bool IncludeTextLayers { get; set; } = true;

        public bool IncludeChannelNames { get; set; } = true;

        public bool IncludePathNames { get; set; } = true;

        public bool IncludeSlices { get; set; } = true;

        public bool IncludeMetadata { get; set; } = true;

        /// <summary>Include hidden layers (and layers inside hidden groups).</summary>
        public bool IncludeHiddenLayers { get; set; } = true;

        /// <summary>
        /// Report <c>Txt2</c> text objects that no extracted type layer covers as
        /// <see cref="PsdTextKind.TextEngineObject"/> items (requires <see cref="IncludeTextLayers"/>).
        /// </summary>
        public bool IncludeTextEngineObjects { get; set; } = true;

        /// <summary>Recurse into embedded PSD/PSB smart objects up to this depth (0 disables).</summary>
        public int MaxEmbeddedDepth { get; set; } = 4;
    }

    /// <summary>All text extracted from a document.</summary>
    public sealed class PsdTextContent
    {
        public PsdTextContent(IReadOnlyList<PsdTextItem> items)
        {
            Items = items;
        }

        public IReadOnlyList<PsdTextItem> Items { get; private set; }

        public IEnumerable<PsdTextItem> OfKind(PsdTextKind kind)
        {
            return Items.Where(delegate(PsdTextItem i) { return i.Kind == kind; });
        }

        /// <summary>Type-layer contents in panel order (top to bottom).</summary>
        public IEnumerable<string> TextLayers
        {
            get { return OfKind(PsdTextKind.TextLayer).Select(delegate(PsdTextItem i) { return i.Text; }); }
        }

        /// <summary>Layer and group names in panel order.</summary>
        public IEnumerable<string> LayerNames
        {
            get
            {
                return Items
                    .Where(delegate(PsdTextItem i) { return i.Kind == PsdTextKind.LayerName || i.Kind == PsdTextKind.GroupName; })
                    .Select(delegate(PsdTextItem i) { return i.Text; });
            }
        }

        /// <summary>All text joined with blank lines, for indexing or search.</summary>
        public override string ToString()
        {
            return string.Join("\n\n", Items.Select(delegate(PsdTextItem i) { return i.Text; }).ToArray());
        }
    }

    internal static class TextExtractor
    {
        public static PsdTextContent Extract(PsdDocument document, TextExtractionOptions options)
        {
            var items = new List<PsdTextItem>();
            Collect(document, options, items, string.Empty, 0);
            return new PsdTextContent(items);
        }

        private static void Collect(PsdDocument document, TextExtractionOptions options, List<PsdTextItem> items, string prefix, int depth)
        {
            var top = depth == 0;
            foreach (var layer in document.EnumerateLayersTopDown())
            {
                Cancellation.Check();
                if (!options.IncludeHiddenLayers && !layer.IsEffectivelyVisible)
                {
                    continue;
                }

                var path = prefix + layer.Path;
                if (options.IncludeLayerNames && layer.Name.Length > 0)
                {
                    var kind = layer.IsGroup ? PsdTextKind.GroupName : PsdTextKind.LayerName;
                    items.Add(new PsdTextItem(kind, layer.Name, path, top ? layer : null, depth));
                }

                if (options.IncludeTextLayers && layer.Text != null && layer.Text.Text.Length > 0)
                {
                    items.Add(new PsdTextItem(PsdTextKind.TextLayer, layer.Text.Text, path, top ? layer : null, depth) { TextInfo = layer.Text });
                }

                if (layer.SmartObjectFileId != null)
                {
                    var fileId = layer.SmartObjectFileId;
                    LinkedFile file = null;
                    foreach (var candidate in document.LinkedFiles)
                    {
                        if (candidate.Id == fileId)
                        {
                            file = candidate;
                            break;
                        }
                    }

                    if (file != null)
                    {
                        if (options.IncludeLayerNames && file.FileName.Length > 0)
                        {
                            items.Add(new PsdTextItem(PsdTextKind.LinkedFileName, file.FileName, path, top ? layer : null, depth));
                        }

                        if (depth < options.MaxEmbeddedDepth && file.IsPhotoshopDocument)
                        {
                            try
                            {
                                var embedded = PsdDocument.FromBytes(file.Data);
                                Collect(embedded, options, items, path + ">", depth + 1);
                            }
                            catch (PsdFormatException)
                            {
                                // A damaged embedded document contributes nothing.
                            }
                        }
                    }
                }
            }

            if (options.IncludeTextLayers && options.IncludeTextEngineObjects)
            {
                CollectTextEngineObjects(document, options, items, prefix, depth);
            }

            if (options.IncludeChannelNames)
            {
                foreach (var name in ChannelNames(document))
                {
                    items.Add(new PsdTextItem(PsdTextKind.ChannelName, name, prefix + "channels", null, depth));
                }
            }

            if (options.IncludePathNames)
            {
                foreach (var resource in document.ImageResources)
                {
                    if (resource.Id >= ImageResourceIds.PathFirst && resource.Id <= ImageResourceIds.PathLast && resource.Name.Length > 0)
                    {
                        items.Add(new PsdTextItem(PsdTextKind.PathName, resource.Name, prefix + "paths", null, depth));
                    }
                }
            }

            if (options.IncludeSlices)
            {
                foreach (var slice in SliceText(document))
                {
                    items.Add(new PsdTextItem(PsdTextKind.Slice, slice.Value, prefix + slice.Key, null, depth));
                }
            }

            if (options.IncludeMetadata)
            {
                var seen = new HashSet<string>();
                var xmp = document.GetImageResource(ImageResourceIds.Xmp);
                if (xmp != null && xmp.Data != null)
                {
                    foreach (var field in XmpText(xmp.Data))
                    {
                        if (seen.Add(field.Key + "\0" + field.Value))
                        {
                            items.Add(new PsdTextItem(PsdTextKind.Metadata, field.Value, prefix + field.Key, null, depth));
                        }
                    }
                }

                var iptc = document.GetImageResource(ImageResourceIds.IptcNaa);
                if (iptc != null && iptc.Data != null)
                {
                    foreach (var field in IptcText(iptc.Data))
                    {
                        if (seen.Add(field.Key + "\0" + field.Value))
                        {
                            items.Add(new PsdTextItem(PsdTextKind.Metadata, field.Value, prefix + field.Key, null, depth));
                        }
                    }
                }
            }
        }

        private static void CollectTextEngineObjects(PsdDocument document, TextExtractionOptions options, List<PsdTextItem> items, string prefix, int depth)
        {
            if (document.GetGlobalTaggedBlock("Txt2") == null || document.TextEngine == null)
            {
                return;
            }

            var owners = new Dictionary<int, PsdLayer>();
            foreach (var layer in document.Layers)
            {
                if (layer.Text != null && layer.Text.TextIndex != null && !owners.ContainsKey(layer.Text.TextIndex.Value))
                {
                    owners.Add(layer.Text.TextIndex.Value, layer);
                }
            }

            foreach (var textObject in document.TextEngine.Objects)
            {
                Cancellation.Check();
                if (textObject.Text.Length == 0)
                {
                    continue;
                }

                var source = prefix + "Txt2/" + textObject.Index;
                PsdLayer owner = null;
                PsdLayer owningLayer;
                if (owners.TryGetValue(textObject.Index, out owningLayer))
                {
                    if (!options.IncludeHiddenLayers && !owningLayer.IsEffectivelyVisible)
                    {
                        continue;
                    }

                    if (owningLayer.Text.Text == textObject.Text)
                    {
                        continue;
                    }

                    owner = owningLayer;
                    source = prefix + owningLayer.Path;
                }

                items.Add(new PsdTextItem(PsdTextKind.TextEngineObject, textObject.Text, source, depth == 0 ? owner : null, depth)
                {
                    TextInfo = TextEngineResolver.FromObject(textObject, null),
                });
            }
        }

        internal static List<string> ChannelNames(PsdDocument document)
        {
            var names = new List<string>();
            var unicode = document.GetImageResource(ImageResourceIds.UnicodeAlphaNames);
            if (unicode != null && unicode.Data != null)
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
            if (pascal != null && pascal.Data != null)
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

        /// <summary>
        /// Slice text from the typed slices resource: the group name (version 6) or
        /// base name (versions 7 and 8), then each slice's name, URL, message, alt
        /// tag and cell text.
        /// </summary>
        private static List<KeyValuePair<string, string>> SliceText(PsdDocument document)
        {
            var results = new List<KeyValuePair<string, string>>();
            var slices = document.Resources.Slices;
            if (slices == null)
            {
                return results;
            }

            AddSlice(results, slices.Version == 6 ? "group" : "baseName", slices.GroupName);
            foreach (var slice in slices.Slices)
            {
                AddSlice(results, "name", slice.Name);
                AddSlice(results, "url", slice.Url);
                AddSlice(results, "message", slice.Message);
                AddSlice(results, "alt", slice.AltTag);
                AddSlice(results, "cellText", slice.CellText);
            }

            return results;
        }

        private static void AddSlice(List<KeyValuePair<string, string>> results, string field, string value)
        {
            if (value.Length > 0)
            {
                results.Add(new KeyValuePair<string, string>("slices/" + field, value));
            }
        }

        private static List<KeyValuePair<string, string>> XmpText(byte[] data)
        {
            var results = new List<KeyValuePair<string, string>>();
            try
            {
                var xml = System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0');
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                var document = System.Xml.Linq.XDocument.Load(System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings));
                var dc = (System.Xml.Linq.XNamespace)"http://purl.org/dc/elements/1.1/";
                var photoshop = (System.Xml.Linq.XNamespace)"http://ns.adobe.com/photoshop/1.0/";
                var fields = new[]
                {
                    new KeyValuePair<System.Xml.Linq.XName, string>(dc + "title", "xmp/title"),
                    new KeyValuePair<System.Xml.Linq.XName, string>(dc + "description", "xmp/description"),
                    new KeyValuePair<System.Xml.Linq.XName, string>(dc + "subject", "xmp/keywords"),
                    new KeyValuePair<System.Xml.Linq.XName, string>(dc + "creator", "xmp/creator"),
                    new KeyValuePair<System.Xml.Linq.XName, string>(dc + "rights", "xmp/rights"),
                    new KeyValuePair<System.Xml.Linq.XName, string>(photoshop + "Headline", "xmp/headline"),
                };
                foreach (var field in fields)
                {
                    foreach (var element in document.Descendants(field.Key))
                    {
                        var values = element.Descendants()
                            .Where(delegate(System.Xml.Linq.XElement e) { return e.Name.LocalName == "li"; })
                            .Select(delegate(System.Xml.Linq.XElement e) { return e.Value; })
                            .ToList();
                        if (values.Count == 0)
                        {
                            values.Add(element.Value);
                        }

                        foreach (var value in values)
                        {
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                results.Add(new KeyValuePair<string, string>(field.Value, value.Trim()));
                            }
                        }
                    }

                    foreach (var attribute in document.Descendants().Attributes(field.Key))
                    {
                        if (!string.IsNullOrWhiteSpace(attribute.Value))
                        {
                            results.Add(new KeyValuePair<string, string>(field.Value, attribute.Value.Trim()));
                        }
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Malformed XMP contributes nothing.
            }

            return results;
        }

        private static List<KeyValuePair<string, string>> IptcText(byte[] data)
        {
            var results = new List<KeyValuePair<string, string>>();
            var offset = 0;
            while (offset + 5 <= data.Length)
            {
                if (data[offset] != 0x1C)
                {
                    break;
                }

                var record = data[offset + 1];
                var dataset = data[offset + 2];
                int length = (int)ChannelCodec.ReadUInt16BE(data, offset + 3);
                offset += 5;
                if ((length & 0x8000) != 0 || offset + length > data.Length)
                {
                    break;
                }

                if (record == 2)
                {
                    string field;
                    switch (dataset)
                    {
                        case 5: field = "iptc/title"; break;
                        case 25: field = "iptc/keywords"; break;
                        case 80: field = "iptc/byline"; break;
                        case 105: field = "iptc/headline"; break;
                        case 116: field = "iptc/copyright"; break;
                        case 120: field = "iptc/caption"; break;
                        default: field = null; break;
                    }

                    if (field != null)
                    {
                        var value = System.Text.Encoding.UTF8.GetString(data, offset, length).Trim('\0', ' ');
                        if (value.Length > 0)
                        {
                            results.Add(new KeyValuePair<string, string>(field, value));
                        }
                    }
                }

                offset += length;
            }

            return results;
        }
    }
}
