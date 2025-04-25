using System;
using SkiaSharp;
using System.Collections.Generic;
using System.IO; // Required for File.Exists check
using System.Globalization; // Required for CultureInfo in TryParse
using System.Linq; // Required for FirstOrDefault

namespace ZplRendererLib
{
    /// <summary>
    /// Holds the current state during ZPL rendering (position, fonts, barcode defaults, etc.).
    /// </summary>
    internal class ZplRenderState // internal as it's used only by ZplRenderer
    {
        // --- Position Properties ---
        // --- Field Reverse State ---
        public bool IsFieldReversed { get; set; } = false;
        public int LabelHomeX { get; set; } = 0;
        public int LabelHomeY { get; set; } = 0;
        public int CurrentX { get; set; } = 0;
        public int CurrentY { get; set; } = 0;
        public int DensityDpi { get; }
        // --- Field Block State (^FB) ---
        public int FieldBlockWidthDots { get; set; } = 0; // 0 means ^FB is inactive
        public int FieldBlockMaxLines { get; set; } = 1;
        public int FieldBlockLineSpacingDots { get; set; } = 0; // Added spacing between lines
        public char FieldBlockJustification { get; set; } = 'L'; // L, R, C, J
        public int FieldBlockHangingIndentDots { get; set; } = 0;
        // --- Font Properties ---
        public string CurrentFontIdentifier { get; set; } = "0";
        public int CurrentFontHeightDots { get; set; } = 10;
        public int CurrentFontWidthDots { get; set; } = 10;
        public char CurrentFontRotation { get; set; } = 'N';

        // *** Make sure this property definition exists ***
        public string DefaultTtfFontPath { get; set; } = @"C:\ZplOutputTemp\Fonts\arial.ttf"; // <-- !!! CONFIGURE / VERIFY PATH !!!

        // --- Font Mapping Dictionary ---
        public Dictionary<string, string> FontMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "0", @"C:\ZplOutputTemp\Fonts\arial.ttf" },
            { "A", @"C:\ZplOutputTemp\Fonts\arial.ttf" },
            { "B", @"C:\ZplOutputTemp\Fonts\cour.ttf" },
            { "D", @"C:\ZplOutputTemp\Fonts\arial.ttf" },
            { "F", @"C:\ZplOutputTemp\Fonts\arial.ttf" },
            { "DEFAULT", @"C:\ZplOutputTemp\Fonts\arial.ttf" }
            // Add other mappings as needed
        };
        private string _ultimateFallbackFontPath = @"C:\ZplOutputTemp\Fonts\arial.ttf";

        // --- Barcode Properties ---
        public int BarcodeModuleWidthDots { get; set; } = 2;
        public float BarcodeWideBarRatio { get; set; } = 3.0f;
        public int BarcodeHeightDots { get; set; } = 10;

        // --- State for current operation ---
        public ZplCommand CurrentBarcodeCommand { get; set; } = null;
        public Dictionary<string, string> CurrentBarcodeParams { get; set; } = new Dictionary<string, string>();

        public ZplRenderState(int densityDpi)
        {
            DensityDpi = densityDpi > 0 ? densityDpi : 203;
            // Initialize DefaultTtfFontPath before InitializeFontPaths uses it
            InitializeDefaultFontPath();
            InitializeFontPaths();
        }

        // Sets the initial DefaultTtfFontPath based on OS detection
        private void InitializeDefaultFontPath()
        {
            string initialDefault = @"C:\ZplOutputTemp\Fonts\arial.ttf";
            // Check if the initial default exists
            bool initialDefaultExists = File.Exists(initialDefault);

            // Try OS-specific paths only if the initial Windows path doesn't exist
            if (!initialDefaultExists)
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    string[] linuxPaths = { /* ... common paths ... */ };
                    initialDefault = linuxPaths.FirstOrDefault(File.Exists) ?? string.Empty;
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    string[] macPaths = { /* ... common paths ... */ };
                    initialDefault = macPaths.FirstOrDefault(p => File.Exists(p.Split(',')[0])) ?? string.Empty;
                }
                else { initialDefault = @"C:\ZplOutputTemp\Fonts\arial.ttf"; }
            }
            // If still no valid path found after OS checks, set to null (will cause errors later if not mapped)
            if (string.IsNullOrEmpty(initialDefault) || !File.Exists(initialDefault.Split(',')[0]))
            {
                DefaultTtfFontPath = null;
            }
            else
            {
                DefaultTtfFontPath = initialDefault;
            }
            _ultimateFallbackFontPath = DefaultTtfFontPath; // Use the determined default as ultimate fallback
        }


        // Updates FontMap based on determined default
        private void InitializeFontPaths()
        {
            FontMap["DEFAULT"] = DefaultTtfFontPath; // Use the property set in InitializeDefaultFontPath

            // Update other initial mappings if they point to a non-existent default path
            // This assumes the dictionary was initialized with Windows paths
            const string defaultWinPath = @"C:\ZplOutputTemp\Fonts\arial.ttf";
            const string defaultCourPath = @"C:\ZplOutputTemp\Fonts\cour.ttf";
            bool winDefaultExists = File.Exists(defaultWinPath); // Check just once

            // Only overwrite if the original path was the non-existent windows default
            if (!winDefaultExists)
            {
                if (FontMap.TryGetValue("0", out var path0) && path0 == defaultWinPath) FontMap["0"] = DefaultTtfFontPath;
                if (FontMap.TryGetValue("A", out var pathA) && pathA == defaultWinPath) FontMap["A"] = DefaultTtfFontPath;
                if (FontMap.TryGetValue("B", out var pathB) && pathB == defaultCourPath) FontMap["B"] = DefaultTtfFontPath; // Fallback Cour to default too
                if (FontMap.TryGetValue("D", out var pathD) && pathD == defaultWinPath) FontMap["D"] = DefaultTtfFontPath;
                if (FontMap.TryGetValue("F", out var pathF) && pathF == defaultWinPath) FontMap["F"] = DefaultTtfFontPath;
            }
        }

        // Coordinate/Dimension Conversion Helpers
        public (float pixelX, float pixelY) ConvertDotsToPixels(int xDots, int yDots) { int aX = LabelHomeX + xDots; int aY = LabelHomeY + yDots; return (aX, aY); }
        public float ConvertDimensionToPixels(int dots) { return Math.Max(1, dots); }

        // Parameter Parsing Helpers
        // Replace the ParseIntegerParams method in ZplRenderState.cs

        public static int[] ParseIntegerParams(string parameters, int expectedCount, int[] defaultValues = null)
        {
            int[] result = new int[expectedCount];
            // Initialize with defaults or zeros
            if (defaultValues != null && defaultValues.Length == expectedCount)
            {
                Array.Copy(defaultValues, result, expectedCount);
            }
            else
            {
                for (int i = 0; i < expectedCount; i++) result[i] = 0;
            }

            // Early return if no parameters to parse
            if (string.IsNullOrEmpty(parameters))
            {
                return result;
            }

            // Parse the parameters string
            string[] parts = parameters.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, expectedCount); i++)
            {
                if (!string.IsNullOrWhiteSpace(parts[i]) &&
                    int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    // Update the result array with the parsed value
                    result[i] = value;
                }
                // If TryParse fails or part is empty, the default/initial value in result[i] remains
            }

            // *** ADD THIS RETURN STATEMENT ***
            // Ensure the result array is always returned after parsing attempt
            return result;
        }
        public void ParseBarcodeParams(string p, int c) { /* ... implementation ... */ }
        // Replace the GetBarcodeParam method in ZplRenderState.cs

        public string GetBarcodeParam(string key, string defaultValue = null)
        {
            if (CurrentBarcodeParams.TryGetValue(key, out var value))
            {
                // Key was found, return the associated value
                return value;
            }
            else
            {
                // Key was not found, return the provided default value
                return defaultValue;
            }
            // Now all paths explicitly return a value
        }

        // Corrected method for ZplRenderState.cs
        // Alternative structure for GetBarcodeIntParam
        // Replace the expression-bodied member with this standard block:
        /// <summary>
        /// Gets an integer parameter from the CurrentBarcodeParams dictionary.
        /// </summary>
        /// <param name="key">The parameter key (e.g., "P1", "P2").</param>
        /// <param name="defaultValue">The value to return if the key is not found or parsing fails.</param>
        /// <returns>The parsed integer value or the default value.</returns>
        public int GetBarcodeIntParam(string key, int defaultValue)
        {
            int parsedValue; // Variable to store the successfully parsed value

            // 1. Try to get the string value associated with the key
            // 2. Check if the retrieved string is not null or whitespace
            // 3. Try to parse the trimmed string into an integer
            if (CurrentBarcodeParams.TryGetValue(key, out var valueString) &&
                !string.IsNullOrWhiteSpace(valueString) &&
                int.TryParse(valueString.Trim(),
                             NumberStyles.Integer,
                             CultureInfo.InvariantCulture,
                             out parsedValue))
            {
                // If all steps succeeded (key found, value not empty, parsing successful)
                return parsedValue; // Return the integer value obtained from TryParse
            }
            else
            {
                // If any step failed (key not found, value empty, parsing failed)
                return defaultValue; // Return the provided default value
            }
            // The structure ensures either the 'if' block or the 'else' block returns,
            // satisfying the "all code paths return a value" requirement.
        }

        // Corrected method for ZplRenderState.cs
        public bool GetBarcodeBoolParam(string key, bool defaultValue)
        {
            // Get the parameter value, using "Y" or "N" as the effective default if key is missing
            string valueStr = GetBarcodeParam(key, defaultValue ? "Y" : "N");

            // Explicitly check for "Y" or "N"
            if (valueStr.Equals("Y", StringComparison.OrdinalIgnoreCase)) return true;
            if (valueStr.Equals("N", StringComparison.OrdinalIgnoreCase)) return false;

            // If the value retrieved was neither "Y" nor "N", return the original default boolean value
            return defaultValue;
        }

        /// <summary>
        /// Gets the fully qualified path to the TTF font file corresponding to the
        /// CurrentFontIdentifier, using fallbacks if necessary.
        /// </summary>
        /// <returns>The font file path (including collection index if applicable), or null if no valid font found.</returns>
        public string GetCurrentTtfFontPath()
        {
            string identifier = this.CurrentFontIdentifier ?? "0"; // Default to '0' if null
            string path = null;
            string reason = "requested identifier";

            // 1. Try the specific identifier from the map
            if (FontMap.TryGetValue(identifier, out path) && IsValidFontPath(path))
            {
                // Found directly in map and file exists
            }
            // 2. If identifier wasn't 'DEFAULT' and direct lookup failed, try the 'DEFAULT' mapping
            else if (!identifier.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) &&
                     FontMap.TryGetValue("DEFAULT", out path) && IsValidFontPath(path))
            {
                reason = "DEFAULT map entry";
            }
            // 3. If 'DEFAULT' mapping failed or wasn't tried, try the ultimate fallback path
            else if (IsValidFontPath(_ultimateFallbackFontPath))
            {
                path = _ultimateFallbackFontPath;
                reason = "ultimate fallback path";
            }
            else
            {
                // 4. No valid font found anywhere
                path = null;
                reason = "no valid path found";
            }

            // Use Trace level as this might be called frequently
            System.Diagnostics.Trace.WriteLine($"GetCurrentTtfFontPath: Requested='{identifier}', Reason='{reason}', Result='{path ?? "NULL"}'"); // Simple Trace for now

            return path;
        }

        /// <summary>
        /// Helper to check if a font path string points to an existing file.
        /// Handles paths with collection indices (e.g., "font.ttc,1").
        /// </summary>
        private bool IsValidFontPath(string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath)) return false;

            string actualFile = fontPath;
            if (fontPath.Contains(','))
            {
                actualFile = fontPath.Split(',')[0];
            }
            return File.Exists(actualFile);
        }
    }
}