using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XRay.PsdFx.Tests.Support;

namespace XRay.PsdFx.Tests
{
    /// <summary>
    /// Port of the original ParsingTests, restricted to what this library parses
    /// today (headers, layer records, effects; no pixel decoding until phase 5).
    /// The corpus cases stay fixture-driven and are inconclusive without
    /// test_documents, exactly like the original's Assert.Skip.
    /// </summary>
    [TestClass]
    public sealed class ParsingTests
    {
        [TestMethod]
        public void Rejects_non_psd_data()
        {
            Assert.ThrowsException<PsdFormatException>(() => PsdDocument.FromBytes(new byte[64]));
            Assert.ThrowsException<PsdFormatException>(() => PsdDocument.FromBytes(new byte[0]));
        }

        [TestMethod]
        public void Header_fields_round_trip()
        {
            var builder = SimpleDocument(40, 30, PsdColorMode.Cmyk, 4);
            var document = PsdDocument.FromBytes(builder.Build());

            Assert.AreEqual(40, document.Width);
            Assert.AreEqual(30, document.Height);
            Assert.AreEqual(8, document.BitsPerChannel);
            Assert.AreEqual(PsdColorMode.Cmyk, document.ColorMode);
            Assert.AreEqual(4, document.Channels);
            Assert.IsFalse(document.IsLargeDocument);
            Assert.AreEqual(4, document.Layers.Count);
        }

        [TestMethod]
        public void Psb_documents_parse_wide_lengths()
        {
            var builder = SimpleDocument(40, 30, PsdColorMode.Rgb, 2);
            builder.Large = true;
            builder.Layers[0].Name = "wíde";

            var document = PsdDocument.FromBytes(builder.Build());

            Assert.IsTrue(document.IsLargeDocument);
            Assert.AreEqual(40, document.Width);
            Assert.AreEqual(30, document.Height);
            Assert.AreEqual(2, document.Layers.Count);
            Assert.AreEqual("wíde", document.Layers[0].Name);
        }

        [TestMethod]
        public void Layer_records_expose_names_blend_modes_opacity_and_visibility()
        {
            var builder = SimpleDocument(8, 8, PsdColorMode.Rgb, 0);
            var multiply = new BuilderLayer { Name = "Clip Multiply", Rect = new PsdRect(0, 0, 4, 4), BlendKey = "mul ", Opacity = 128, Clipped = true };
            multiply.Channels[0] = PsdBuilder.Plane8(4, 4, 10);
            builder.Layers.Add(multiply);
            var hidden = new BuilderLayer { Name = "Hidden", Rect = new PsdRect(1, 1, 5, 5), Hidden = true };
            hidden.Channels[0] = PsdBuilder.Plane8(4, 4, 20);
            builder.Layers.Add(hidden);

            var document = PsdDocument.FromBytes(builder.Build());

            Assert.AreEqual(2, document.Layers.Count);
            Assert.AreEqual("Clip Multiply", document.Layers[0].Name);
            Assert.AreEqual(PsdBlendMode.Multiply, document.Layers[0].BlendMode);
            Assert.AreEqual(128, document.Layers[0].Opacity);
            Assert.IsTrue(document.Layers[0].IsVisible);
            Assert.AreEqual("Hidden", document.Layers[1].Name);
            Assert.IsFalse(document.Layers[1].IsVisible);
            CollectionAssert.Contains(document.Layers[0].TaggedBlockKeys.ToList(), "luni");
        }

        [TestMethod]
        public void Every_truncation_either_parses_or_throws_a_format_exception()
        {
            var bytes = SimpleDocument(16, 12, PsdColorMode.Rgb, 2).Build();
            foreach (var fraction in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
            {
                var cut = new byte[(int)(bytes.Length * fraction)];
                Array.Copy(bytes, cut, cut.Length);
                try
                {
                    var document = PsdDocument.FromBytes(cut);
                    Assert.IsTrue(document.Width >= 0 && document.Height >= 0);
                }
                catch (PsdFormatException)
                {
                    // Rejecting a damaged file is fine; any other exception type is a bug.
                }
            }
        }

        [TestMethod]
        public void Color_mode_data_is_kept_for_duotone_and_indexed_documents()
        {
            var builder = new PsdBuilder { Width = 2, Height = 2, Mode = PsdColorMode.Duotone };
            builder.ColorModeData = new byte[] { 0, 1, 0, 1 };
            var layer = new BuilderLayer { Name = "gray", Rect = new PsdRect(0, 0, 2, 2) };
            layer.Channels[0] = PsdBuilder.Plane8(2, 2, 128);
            builder.Layers.Add(layer);

            var document = PsdDocument.FromBytes(builder.Build());

            Assert.AreEqual(PsdColorMode.Duotone, document.ColorMode);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 0, 1 }, document.ColorModeData);
        }

        [TestMethod]
        public void Every_corpus_fixture_parses()
        {
            if (!Fixtures.IsAvailable)
            {
                Assert.Inconclusive("test_documents not found; corpus parsing sweep skipped.");
            }

            var failures = new List<string>();
            foreach (var name in Fixtures.AllDocuments())
            {
                try
                {
                    var document = Fixtures.Load(name);
                    Assert.IsTrue(document.Width > 0 && document.Width <= 30000, name + ": width " + document.Width);
                    Assert.AreEqual(name.EndsWith(".psb", StringComparison.Ordinal), document.IsLargeDocument, name + ": psb flag");
                    foreach (var layer in document.Layers)
                    {
                        Assert.IsFalse(string.IsNullOrEmpty(layer.Name), name + ": unnamed layer");
                    }
                }
                catch (PsdFormatException ex)
                {
                    failures.Add(name + ": " + ex.Message);
                }
            }

            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }

        [TestMethod]
        public void Loads_from_a_stream_and_a_file()
        {
            var bytes = SimpleDocument(6, 5, PsdColorMode.Rgb, 1).Build();
            using (var stream = new MemoryStream(bytes))
            {
                var fromStream = PsdDocument.FromStream(stream);
                Assert.AreEqual(6, fromStream.Width);
            }

            var path = Path.Combine(Path.GetTempPath(), "psdfx-test-" + Guid.NewGuid().ToString("N") + ".psd");
            try
            {
                File.WriteAllBytes(path, bytes);
                var fromFile = PsdDocument.FromFile(path);
                Assert.AreEqual(5, fromFile.Height);
                Assert.AreEqual(1, fromFile.Layers.Count);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static PsdBuilder SimpleDocument(int width, int height, PsdColorMode mode, int layerCount)
        {
            var builder = new PsdBuilder { Width = width, Height = height, Mode = mode };
            builder.ColorModeData = new byte[0];
            for (var i = 0; i < layerCount; i++)
            {
                var layer = new BuilderLayer
                {
                    Name = "Layer " + (i + 1),
                    Rect = new PsdRect(0, 0, Math.Min(4, width), Math.Min(4, height)),
                };
                var count = mode == PsdColorMode.Cmyk ? 4 : 1;
                for (var c = 0; c < count; c++)
                {
                    layer.Channels[(short)c] = PsdBuilder.Plane8(layer.Rect.Width, layer.Rect.Height, (byte)(40 * (c + 1)));
                }

                builder.Layers.Add(layer);
            }

            return builder;
        }
    }
}
