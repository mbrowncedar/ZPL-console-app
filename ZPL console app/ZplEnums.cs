using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZplRendererLib // Use your preferred namespace
{
    /// <summary>
    /// Specifies how the rendered ZPL output should be handled.
    /// </summary>
    public enum ZplOutputMode
    {
        /// <summary>
        /// The renderer method will return the PNG image data as a byte array.
        /// </summary>
        ReturnByteArray,

        /// <summary>
        /// The renderer method will save the PNG image data directly to a file path
        /// specified in ZplRenderOptions.OutputFilePath.
        /// </summary>
        SaveToFile
    }

    // Potentially add other enums here later if needed (e.g., Rotation)
}