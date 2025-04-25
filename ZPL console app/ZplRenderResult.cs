using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace ZplRendererLib
{
    /// <summary>
    /// Represents the result of a ZPL rendering operation.
    /// </summary>
    public class ZplRenderResult
    {
        /// <summary>
        /// Indicates whether the rendering (and optional saving) was successful.
        /// </summary>
        public bool Success { get; internal set; }

        /// <summary>
        /// Contains error messages if Success is false.
        /// </summary>
        public string ErrorMessage { get; internal set; }

        /// <summary>
        /// Contains the generated PNG data. May be populated even on failure if data was generated before failure.
        /// </summary>
        public byte[] PngData { get; internal set; }

        // Internal constructor
        internal ZplRenderResult() { }

        // Static factory methods
        internal static ZplRenderResult CreateSuccess(byte[] pngData)
        {
            return new ZplRenderResult { Success = true, PngData = pngData };
        }

        internal static ZplRenderResult CreateFailure(string errorMessage)
        {
            return new ZplRenderResult { Success = false, ErrorMessage = errorMessage };
        }
    }
}