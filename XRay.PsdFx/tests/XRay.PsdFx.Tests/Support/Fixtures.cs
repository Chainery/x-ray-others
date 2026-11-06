using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace XRay.PsdFx.Tests.Support
{
    /// <summary>
    /// Locates the PSD fixtures in the shared curiosity-ai/test_documents corpus
    /// (its psd/ folder, with the psd-tools subset in psd/psd-tools/), exactly like
    /// the original suite: the corpus root is XRAY_TEST_DOCUMENTS when set,
    /// otherwise the first non-empty directory named test_documents walking up from
    /// the test output directory. Without a corpus the tests that need it are
    /// inconclusive (the MSTest equivalent of the original's Assert.Skip).
    /// </summary>
    internal static class Fixtures
    {
        /// <summary>Environment variable that names the test_documents root explicitly.</summary>
        public const string EnvironmentVariable = "XRAY_TEST_DOCUMENTS";

        private static readonly Lazy<string> CorpusRoot = new Lazy<string>(FindCorpusRoot);

        /// <summary>True when the test_documents corpus was found.</summary>
        public static bool IsAvailable
        {
            get { return CorpusRoot.Value != null; }
        }

        /// <summary>The psd/ folder of the corpus. Marks the calling test inconclusive when there is no corpus.</summary>
        public static string RootDirectory
        {
            get
            {
                var root = CorpusRoot.Value;
                if (root == null)
                {
                    Assert.Inconclusive(
                        "test_documents not found: run 'git submodule update --init --depth 1 test_documents' in x-ray-others, " +
                        "clone curiosity-ai/test_documents next to it, or set " + EnvironmentVariable + " to its root.");
                }

                var psd = Path.Combine(root, "psd");
                if (!Directory.Exists(psd))
                {
                    throw new DirectoryNotFoundException("test_documents was found at " + root + " but has no psd/ folder; update the corpus.");
                }

                return psd;
            }
        }

        public static string PathOf(string name)
        {
            return Path.Combine(RootDirectory, name);
        }

        /// <summary>Loads a fixture with this library's parser.</summary>
        public static PsdDocument Load(string name)
        {
            return PsdDocument.FromBytes(File.ReadAllBytes(PathOf(name)));
        }

        /// <summary>A file from the psd-tools subset, by its path relative to psd-tools' tests/psd_files.</summary>
        public static string PsdTools(string relativePath)
        {
            return Path.Combine(RootDirectory, "psd-tools", relativePath);
        }

        /// <summary>Every PSD and PSB directly in the fixture folder.</summary>
        public static IEnumerable<string> AllDocuments()
        {
            var files = Directory.GetFiles(RootDirectory);
            var names = new List<string>();
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".psb", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(Path.GetFileName(file));
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static string FindCorpusRoot()
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrEmpty(configured))
            {
                return Directory.Exists(configured)
                    ? Path.GetFullPath(configured)
                    : null;
            }

            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            {
                // An uninitialized submodule is an empty directory: keep walking past it.
                var candidate = new DirectoryInfo(Path.Combine(directory.FullName, "test_documents"));
                if (candidate.Exists && candidate.GetFileSystemInfos().Length > 0)
                {
                    return candidate.FullName;
                }
            }

            return null;
        }
    }
}
