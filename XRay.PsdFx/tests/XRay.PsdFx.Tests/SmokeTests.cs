using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace XRay.PsdFx.Tests
{
    /// <summary>Toolchain smoke test: the harness runs and the library is referenced.</summary>
    [TestClass]
    public sealed class SmokeTests
    {
        [TestMethod]
        public void Garbage_is_rejected_with_a_format_exception()
        {
            Assert.ThrowsException<PsdFormatException>(() => PsdDocument.FromBytes(new byte[64]));
        }
    }
}
