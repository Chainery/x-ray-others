namespace XRay.PsdFx
{
    /// <summary>Options for <see cref="PsdDocument.Load(string, PsdLoadOptions)"/>.</summary>
    public sealed class PsdLoadOptions
    {
        /// <summary>Parse layer records. Turn off to read only the header, resources and merged image.</summary>
        public bool ReadLayers { get; set; } = true;

        /// <summary>Parse embedded smart-object files from the global link blocks.</summary>
        public bool ReadLinkedFiles { get; set; } = true;

        /// <summary>
        /// Convert gray, RGB, indexed and CMYK pixels to sRGB through the document's
        /// embedded ICC profile (resource 1039), as Photoshop displays them. Turn off
        /// to treat RGB and gray as sRGB and use the naive CMYK formula. Documents
        /// without a usable profile convert the same way either way.
        /// </summary>
        public bool ColorManagement { get; set; } = true;

        /// <summary>
        /// Accepted for parity with the original API; this port reads the file into memory,
        /// so the flag changes nothing.
        /// </summary>
        public bool MemoryMap { get; set; }
    }
}
