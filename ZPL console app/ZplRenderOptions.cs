using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace ZplRendererLib
{
    /// <summary>
    /// Options for rendering ZPL code to an image.
    /// </summary>
    public class ZplRenderOptions
    {
        /// <summary>
        /// The raw ZPL code string to be rendered. Required.
        /// </summary>
        public string ZplCode { get; set; }

        /// <summary>
        /// The target printer density in Dots Per Inch (DPI). Default: 203.
        /// </summary>
        public int DensityDpi { get; set; } = 203;

        /// <summary>
        /// The desired output width of the label image in inches. Required.
        /// </summary>
        public float WidthInches { get; set; }

        /// <summary>
        /// The desired output height of the label image in inches. Required.
        /// </summary>
        public float HeightInches { get; set; }

        /// <summary>
        /// Specifies how the rendered output should be handled. Default is ReturnByteArray.
        /// </summary>
        public ZplOutputMode OutputMode { get; set; } = ZplOutputMode.ReturnByteArray;

        /// <summary>
        /// The full path where the PNG file should be saved if OutputMode is SaveToFile.
        /// </summary>
        public string OutputFilePath { get; set; }

        // --- Optional Configuration Settings ---

        /// <summary>
        /// Optional. Overrides the default allowed base directory for OutputFilePath validation.
        /// If null or empty, the renderer's default will be used.
        /// </summary>
        public string AllowedOutputDirectoryOverride { get; set; } = null;

        /// <summary>
        /// Optional. Provides a custom mapping from ZPL font identifiers (e.g., "0", "A", "B")
        /// to TTF font file paths. If null, the renderer's default map will be used.
        /// Use StringComparer.OrdinalIgnoreCase for the dictionary for best results.
        /// Example: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "0", "/path/to/myfont.ttf" } }
        /// </summary>
        public Dictionary<string, string> FontMapOverride { get; set; } = null;

        // Consider adding DefaultTtfFontPath override here too?

        // --- Optional settings can be added later ---
        // public string DefaultTtfFontPath { get; set; } = @"C:\ZplOutputTemp\Fonts\arial.ttf"; // Consider passing via options
    }
}