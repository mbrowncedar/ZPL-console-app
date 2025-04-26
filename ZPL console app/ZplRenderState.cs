using System;
using SkiaSharp;
using System.Collections.Generic;
using System.IO; // Required for File.Exists check
using System.Globalization; // Required for CultureInfo in TryParse
using System.Linq; // Required for FirstOrDefault
using System.Runtime.InteropServices; // Needed for OSPlatform check

namespace ZplRendererLib
{
    /// <summary>
    /// Holds the current state during ZPL rendering (position, fonts, barcode defaults, etc.).
    /// </summary>
    internal class ZplRenderState // internal as it's used only by ZplRenderer
    {
        // --- Position Properties ---
        public int LabelHomeX { get; set; } = 0;
        public int LabelHomeY { get; set; } = 0;
        public int CurrentX { get; set; } = 0;
        public int CurrentY { get; set; } = 0;
        public int DensityDpi { get; }

        // --- Font Properties ---
        public string CurrentFontIdentifier { get; set; } = "0";
        public int CurrentFontHeightDots { get; set; } = 10;
        public int CurrentFontWidthDots { get; set; } = 10;
        public char CurrentFontRotation { get; set; } = 'N';

        // *** Default TTF Font Path (determined at runtime) ***
        public string DefaultTtfFontPath { get; private set; }

        // --- Font Mapping Dictionary ---
        // **** Changed FontMap to be readonly after initialization ****
        public IReadOnlyDictionary<string, string> FontMap { get; private set; }
        private string _ultimateFallbackFontPath = null; // Will be set based on DefaultTtfFontPath

        // --- Barcode Properties ---
        public int BarcodeModuleWidthDots { get; set; } = 2;
        public float BarcodeWideBarRatio { get; set; } = 3.0f;
        public int BarcodeHeightDots { get; set; } = 10;

        // --- State for current operation ---
        public ZplCommand CurrentBarcodeCommand { get; set; } = null;
        public Dictionary<string, string> CurrentBarcodeParams { get; set; } = new Dictionary<string, string>();

        // --- Field Block State (^FB) ---
        public int FieldBlockWidthDots { get; set; } = 0; // 0 means ^FB is inactive
        public int FieldBlockMaxLines { get; set; } = 1;
        public int FieldBlockLineSpacingDots { get; set; } = 0; // Added spacing between lines
        public char FieldBlockJustification { get; set; } = 'L'; // L, R, C, J
        public int FieldBlockHangingIndentDots { get; set; } = 0;

        // --- Field Reverse State ---
        public bool IsFieldReversed { get; set; } = false; // Default to normal printing

        // **** Updated Constructor to accept optional font map ****
        public ZplRenderState(int densityDpi, Dictionary<string, string> fontMapOverride = null)
        {
            DensityDpi = densityDpi > 0 ? densityDpi : 203;

            // Use provided font map or create default
            var initialFontMap = fontMapOverride ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Default mappings - these might be overwritten by InitializeFontPaths
                { "0", @"C:\Windows\Fonts\arial.ttf" },
                { "A", @"C:\Windows\Fonts\arial.ttf" },
                { "B", @"C:\Windows\Fonts\cour.ttf" }, // Courier New
                { "D", @"C:\Windows\Fonts\arial.ttf" },
                { "F", @"C:\Windows\Fonts\arial.ttf" },
                { "DEFAULT", @"C:\Windows\Fonts\arial.ttf" }
            };

            // Initialize DefaultTtfFontPath and _ultimateFallbackFontPath based on OS/availability
            InitializeDefaultFontPath(initialFontMap); // Pass the map to potentially update font B

            // Update the initial map with potentially better OS-specific paths
            InitializeFontPaths(initialFontMap);

            // Set the final, read-only FontMap property
            FontMap = initialFontMap;
        }

        // Sets the initial DefaultTtfFontPath based on OS detection
        // **** Updated to accept and potentially modify the map ****
        private void InitializeDefaultFontPath(Dictionary<string, string> fontMap)
        {
            string arialPath = null;
            string courierPath = null;

            // Define potential paths for different OS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                arialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                courierPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "cour.ttf");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string[] linuxArialPaths = {
                    "/usr/share/fonts/truetype/msttcorefonts/Arial.ttf",
                    "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                };
                arialPath = linuxArialPaths.FirstOrDefault(File.Exists);

                string[] linuxCourierPaths = {
                    "/usr/share/fonts/truetype/msttcorefonts/cour.ttf", // Courier New
                    "/usr/share/fonts/liberation/LiberationMono-Regular.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                };
                courierPath = linuxCourierPaths.FirstOrDefault(File.Exists);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string[] macArialPaths = {
                    "/Library/Fonts/Arial.ttf",
                    "/System/Library/Fonts/Arial.ttf", // Newer macOS might use this
                    "/System/Library/Fonts/Supplemental/Arial.ttf",
                 };
                arialPath = macArialPaths.FirstOrDefault(File.Exists);

                string[] macCourierPaths = {
                    "/Library/Fonts/Courier New.ttf",
                    "/System/Library/Fonts/Courier New.ttf", // Newer macOS might use this
                    "/System/Library/Fonts/Supplemental/Courier New.ttf",
                 };
                courierPath = macCourierPaths.FirstOrDefault(File.Exists);
            }

            // Set the determined default path, fallback to null if none found
            DefaultTtfFontPath = File.Exists(arialPath) ? arialPath : null;
            _ultimateFallbackFontPath = DefaultTtfFontPath; // Use the determined default as ultimate fallback

            // Update FontMap specific entry for B if a valid Courier path was found
            if (File.Exists(courierPath))
            {
                fontMap["B"] = courierPath; // Update the passed-in map
            }
            else if (DefaultTtfFontPath != null)
            {
                // Fallback Courier to default Arial if Courier wasn't found but Arial was
                fontMap["B"] = DefaultTtfFontPath; // Update the passed-in map
            }
            // If neither found, FontMap["B"] retains its initial value from the dictionary creation
        }


        // Updates FontMap based on determined default
        // **** Updated to accept the map ****
        private void InitializeFontPaths(Dictionary<string, string> fontMap)
        {
            // Only proceed if a default path was actually found
            if (string.IsNullOrEmpty(DefaultTtfFontPath)) return;

            fontMap["DEFAULT"] = DefaultTtfFontPath;

            // Update other mappings that might have pointed to the Windows default
            const string defaultWinArialPath = @"C:\Windows\Fonts\arial.ttf";
            if (DefaultTtfFontPath != defaultWinArialPath)
            {
                // Update only if the current mapping points to the old Windows default
                if (fontMap.TryGetValue("0", out var path0) && path0 == defaultWinArialPath) fontMap["0"] = DefaultTtfFontPath;
                if (fontMap.TryGetValue("A", out var pathA) && pathA == defaultWinArialPath) fontMap["A"] = DefaultTtfFontPath;
                if (fontMap.TryGetValue("D", out var pathD) && pathD == defaultWinArialPath) fontMap["D"] = DefaultTtfFontPath;
                if (fontMap.TryGetValue("F", out var pathF) && pathF == defaultWinArialPath) fontMap["F"] = DefaultTtfFontPath;
            }
            // Font B (Courier) was handled in InitializeDefaultFontPath
        }

        // Coordinate/Dimension Conversion Helpers
        public (float pixelX, float pixelY) ConvertDotsToPixels(int xDots, int yDots) { int aX = LabelHomeX + xDots; int aY = LabelHomeY + yDots; return (aX, aY); }
        public float ConvertDimensionToPixels(int dots) { return Math.Max(1, dots); }

        // Parameter Parsing Helpers
        public static int[] ParseIntegerParams(string parameters, int expectedCount, int[] defaultValues = null)
        {
            int[] result = new int[expectedCount];
            if (defaultValues != null && defaultValues.Length == expectedCount) { Array.Copy(defaultValues, result, expectedCount); }
            else { for (int i = 0; i < expectedCount; i++) result[i] = 0; }
            if (string.IsNullOrEmpty(parameters)) { return result; }
            string[] parts = parameters.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, expectedCount); i++)
            {
                if (!string.IsNullOrWhiteSpace(parts[i]) && int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                { result[i] = value; }
            }
            return result;
        }

        // Get Barcode Parameters with Defaults
        public string GetBarcodeParam(string key, string defaultValue = null)
        {
            return CurrentBarcodeParams.TryGetValue(key, out var value) ? value : defaultValue;
        }

        public int GetBarcodeIntParam(string key, int defaultValue)
        {
            int parsedValue;
            if (CurrentBarcodeParams.TryGetValue(key, out var valueString) &&
                !string.IsNullOrWhiteSpace(valueString) &&
                int.TryParse(valueString.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue))
            {
                return parsedValue;
            }
            return defaultValue;
        }

        public bool GetBarcodeBoolParam(string key, bool defaultValue)
        {
            string valueStr = GetBarcodeParam(key, defaultValue ? "Y" : "N");
            if (valueStr.Equals("Y", StringComparison.OrdinalIgnoreCase)) return true;
            if (valueStr.Equals("N", StringComparison.OrdinalIgnoreCase)) return false;
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
            // Check if the file exists before returning true
            return File.Exists(actualFile);
        }
    }
}
