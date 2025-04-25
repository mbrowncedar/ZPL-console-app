using System;
using System.Text; // Needed for StringBuilder
using System.Collections.Generic;
using System.IO; // Needed for Path, Directory, File, File.Exists
using System.Text.RegularExpressions; // Needed for parsing
using System.Linq;
using SkiaSharp; // Graphics library
using Microsoft.Extensions.Logging; // Logging framework
using Microsoft.Extensions.Logging.Abstractions;
using ZXing; // Barcode library
using ZXing.Common;
using ZXing.Rendering; // Not directly used now
using ZXing.SkiaSharp; // Added for BarcodeWriter
using ZXing.SkiaSharp.Rendering; // Also useful for the renderer if needed later
using System.Globalization;
using System.Runtime.InteropServices; // Needed for Marshal, IntPtr and OSPlatform check

namespace ZplRendererLib
{
    /// <summary>
    /// Renders ZPL code to PNG images using SkiaSharp and ZXing.Net.
    /// </summary>
    public class ZplRenderer
    {
        private readonly ILogger<ZplRenderer> _logger;
        private const int MaxZplLength = 50 * 1024; // Example limit
        private const int MaxImageDimensionPixels = 16384; // Example limit
        private readonly string _allowedOutputDirectory = @"C:\ZplOutputTemp"; // !!! Needs configuration

        // Simple in-memory store for downloaded graphics (Name -> Bitmap)
        private Dictionary<string, SKBitmap> _graphicStore = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);

        // Constructor with ILogger injection and temporary test image loading
        public ZplRenderer(ILogger<ZplRenderer> logger)
        {
            _logger = logger ?? NullLogger<ZplRenderer>.Instance;
            // TEMPORARY TEST CODE: Preload an image
            try
            {
                string testImagePath = @"C:\ZplOutputTemp\logo.png"; // <<< PUT A REAL PNG HERE
                if (File.Exists(testImagePath))
                {
                    SKBitmap testLogo = SKBitmap.Decode(testImagePath);
                    if (testLogo != null)
                    {
                        _graphicStore["R:LOGO.GRF"] = testLogo;
                        _logger.LogInformation("Loaded test image into graphic store as R:LOGO.GRF");
                    }
                    else { _logger.LogWarning("Failed to decode test image: {Path}", testImagePath); }
                }
                else { _logger.LogWarning("Test image file not found: {Path}", testImagePath); }
            }
            catch (Exception ex) { _logger.LogError(ex, "Error loading test image."); }
        }

        // Parameterless constructor
        public ZplRenderer() : this(NullLogger<ZplRenderer>.Instance) { }

        /// <summary>
        /// Renders ZPL code to a PNG byte array and optionally saves it to a file.
        /// </summary>
        public ZplRenderResult RenderZplToPng(ZplRenderOptions options)
        {
            _logger.LogInformation("Starting ZPL rendering process...");

            // --- 1. Validation ---
            if (options == null) { _logger.LogError("Render options cannot be null."); return ZplRenderResult.CreateFailure("Render options cannot be null."); }
            if (options.DensityDpi <= 0 || options.WidthInches <= 0 || options.HeightInches <= 0) { _logger.LogError("Invalid dimensions or DPI: Width={W}, Height={H}, DPI={D}", options.WidthInches, options.HeightInches, options.DensityDpi); return ZplRenderResult.CreateFailure("Invalid dimensions or DPI provided."); }
            if (string.IsNullOrWhiteSpace(options.ZplCode)) { _logger.LogError("ZPL code cannot be null or empty."); return ZplRenderResult.CreateFailure("ZPL code cannot be null or empty."); }
            if (options.ZplCode.Length > MaxZplLength) { _logger.LogError("ZPL code length ({Len}) exceeds maximum allowed ({Max}).", options.ZplCode.Length, MaxZplLength); return ZplRenderResult.CreateFailure($"ZPL code exceeds maximum length of {MaxZplLength} bytes."); }
            if (options.OutputMode == ZplOutputMode.SaveToFile)
            {
                string pathValidationError;
                if (!IsValidOutputPath(options.OutputFilePath, _allowedOutputDirectory, out pathValidationError)) { _logger.LogError("Invalid output file path provided: {Error}", pathValidationError); return ZplRenderResult.CreateFailure($"Invalid output file path: {pathValidationError}"); }
                _logger.LogDebug("Output file path appears valid: {Path}", options.OutputFilePath);
            }

            SKSurface surface = null;
            byte[] generatedPngData = null;
            ZplRenderState currentState = null;

            try
            {
                // --- 2. Calculate Dimensions ---
                _logger.LogDebug("Calculating pixel dimensions...");
                int widthPixels = (int)Math.Ceiling(options.WidthInches * options.DensityDpi);
                int heightPixels = (int)Math.Ceiling(options.HeightInches * options.DensityDpi);
                _logger.LogInformation("Label dimensions: {W}w x {H}h inches @ {DPI} DPI => {Pw} x {Ph} pixels", options.WidthInches, options.HeightInches, options.DensityDpi, widthPixels, heightPixels);
                if (widthPixels <= 0 || heightPixels <= 0 || widthPixels > MaxImageDimensionPixels || heightPixels > MaxImageDimensionPixels) { _logger.LogError("Calculated pixel dimensions ({Pw}x{Ph}) are invalid or exceed max limit ({MaxDim}).", widthPixels, heightPixels, MaxImageDimensionPixels); return ZplRenderResult.CreateFailure($"Calculated image dimensions are invalid or exceed limit of {MaxImageDimensionPixels} pixels."); }

                // --- 3. Init Canvas ---
                _logger.LogDebug("Initializing SkiaSharp canvas ({Pw}x{Ph})...", widthPixels, heightPixels);
                SKImageInfo imageInfo = new SKImageInfo(widthPixels, heightPixels, SKColorType.Bgra8888, SKAlphaType.Opaque);
                surface = SKSurface.Create(imageInfo);
                if (surface == null) { _logger.LogError("SkiaSharp SKSurface.Create failed for {W}x{H}.", widthPixels, heightPixels); return ZplRenderResult.CreateFailure("Canvas creation failed."); }
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.White);
                _logger.LogDebug("SkiaSharp canvas created and cleared.");

                // --- 4. Init State ---
                _logger.LogDebug("Initializing render state...");
                currentState = new ZplRenderState(options.DensityDpi);

                // --- 5. Parse ---
                _logger.LogDebug("Parsing ZPL raw commands...");
                List<string> rawCommands = ParseZplRawCommands(options.ZplCode); // Uses method defined below
                _logger.LogInformation("Found {Count} raw commands.", rawCommands.Count);
                if (rawCommands.Count == 0 && !string.IsNullOrWhiteSpace(options.ZplCode)) { _logger.LogWarning("ZPL parsing resulted in zero commands. Output might be empty."); }

                // --- 6. Process Commands ---
                _logger.LogDebug("--- Processing Commands ---");
                for (int i = 0; i < rawCommands.Count; i++)
                {
                    ZplCommand command = new ZplCommand(rawCommands[i]);
                    if (command.Prefix == '\0') { _logger.LogWarning("Skipping invalid command segment: {Segment}", rawCommands[i]); continue; }
                    _logger.LogDebug("Processing: {Cmd}", command);

                    // Handle barcode data association FIRST
                    if (command.CommandCode == "FD" && currentState.CurrentBarcodeCommand != null)
                    {
                        _logger.LogDebug("Detected FD data for pending barcode {BarcodeCmd}", currentState.CurrentBarcodeCommand);
                        RenderBarcode(canvas, currentState, command.Parameters ?? "");
                        // State is reset inside RenderBarcode's finally block now
                    }
                    else // Process other commands or non-barcode FD
                    {
                        // Cancel pending barcode if another command appears before FD
                        if (command.CommandCode != "FD" && currentState.CurrentBarcodeCommand != null)
                        {
                            _logger.LogWarning("Cancelling pending barcode {BarcodeCmd} because {CurrentCmd} command was received before FD data.", currentState.CurrentBarcodeCommand, command);
                            currentState.CurrentBarcodeCommand = null;
                            currentState.CurrentBarcodeParams.Clear();
                        }

                        // **** CORRECTED SWITCH STATEMENT ****
                        // Process the current command
                        switch (command.CommandCode)
                        {
                            // Format Control
                            case "XA": case "XZ": break; // Start/End Format
                            case "FS": _logger.LogDebug("Field Separator (^FS) encountered."); break; // Field Separator

                            // Label/State Setup
                            case "LH": HandleLhCommand(currentState, command.Parameters); break; // Label Home
                            case "FO": HandleFoCommand(currentState, command.Parameters); break; // Field Origin
                            case "CF": HandleCfCommand(currentState, command.Parameters); break; // Change Default Font
                            case "A": // Select Font (^Ax or ^A@)
                                if (command.Parameters.Length > 0 && command.Parameters[0] == '@') { HandleAAtCommand(currentState, command.Parameters.Substring(1)); }
                                else if (command.RawCommand.Length > 2) { char fontIdChar = command.RawCommand[2]; HandleAxCommand(currentState, fontIdChar, command.Parameters); }
                                else { _logger.LogWarning("Invalid ^A command format: {Raw}", command.RawCommand); }
                                break;
                            case "BY": HandleByCommand(currentState, command.Parameters); break; // Barcode Defaults
                            case "FB": HandleFbCommand(currentState, command.Parameters); break; // Field Block
                            case "FR": HandleFrCommand(currentState, command.Parameters); break; // Field Reverse

                            // Drawing Commands
                            case "FD": // Field Data (handles regular text)
                                if (currentState.CurrentBarcodeCommand == null) RenderText(canvas, currentState, command.Parameters ?? "");
                                break;
                            case "GB": RenderGraphicBox(canvas, currentState, command.Parameters); break; // Graphic Box

                            // Barcode Setup Commands (Set state for next FD)
                            case "BC": HandleBcCommand(currentState, command); break; // Code 128
                            case "B3": HandleB3Command(currentState, command); break; // Code 39
                            case "BQ": HandleBqCommand(currentState, command); break; // QR Code
                            // Add other barcode setup cases here

                            // Image Commands
                            case "XG": RenderGraphicImage(canvas, currentState, command.Parameters); break; // Recall Graphic
                            case "DG": HandleDgCommand(currentState, command); break; // Download Graphic

                            // Default for unsupported
                            default: _logger.LogWarning("Unsupported command encountered: {Cmd}", command); break;
                        }
                        // **** END CORRECTED SWITCH STATEMENT ****
                    }
                }
                _logger.LogDebug("--- Finished Processing Commands ---");

                // --- 7. Encode ---
                _logger.LogDebug("Encoding final image to PNG...");
                using (SKImage image = surface.Snapshot())
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    if (data == null) { _logger.LogError("Encoding to PNG failed (SKData is null)."); return ZplRenderResult.CreateFailure("Encoding failed."); }
                    generatedPngData = data.ToArray();
                    _logger.LogInformation("Encoding OK ({Len} bytes).", generatedPngData.Length);
                }

                // --- 8. Output ---
                _logger.LogDebug("Checking encoded data before output...");
                if (generatedPngData == null || generatedPngData.Length == 0) { _logger.LogError("Output generation failed: Encoded data is null or empty after encoding block."); return ZplRenderResult.CreateFailure("Output generation failed: No encoded data."); }
                _logger.LogDebug("Encoded data seems valid ({Len} bytes). Checking output mode...", generatedPngData.Length);

                if (options.OutputMode == ZplOutputMode.SaveToFile)
                {
                    _logger.LogDebug("Output mode is SaveToFile. Entering save logic block...");
                    _logger.LogInformation("--- Starting File Write Operation ---");
                    string filePath = options.OutputFilePath;
                    _logger.LogDebug($"Target file path from options: {filePath}");
                    _logger.LogDebug($"Data size to write: {generatedPngData.Length} bytes");
                    try
                    {
                        string pathValidationError;
                        if (!IsValidOutputPath(filePath, _allowedOutputDirectory, out pathValidationError))
                        {
                            _logger.LogError("Invalid output file path: {Error}", pathValidationError);
                            return ZplRenderResult.CreateFailure($"Invalid output file path: {pathValidationError}");
                        }
                        _logger.LogDebug("Output path passed validation.");
                        string directoryPath = Path.GetDirectoryName(filePath);
                        _logger.LogDebug($"Checking if directory exists: {directoryPath}");
                        if (!Directory.Exists(directoryPath))
                        {
                            _logger.LogInformation($"Directory does not exist. Attempting to create: {directoryPath}");
                            try { Directory.CreateDirectory(directoryPath); _logger.LogInformation($"Successfully created directory: {directoryPath}"); }
                            catch (Exception dirEx) { _logger.LogError(dirEx, $"Failed to create directory {directoryPath}."); return ZplRenderResult.CreateFailure($"Failed to create output directory: {dirEx.Message}"); }
                        }
                        else { _logger.LogDebug($"Directory already exists: {directoryPath}"); }
                        _logger.LogInformation($"Attempting to write file: {filePath}");
                        File.WriteAllBytes(filePath, generatedPngData);
                        _logger.LogInformation($"Successfully wrote {generatedPngData.Length} bytes to file: {filePath}");
                    }
                    catch (UnauthorizedAccessException authEx) { _logger.LogError(authEx, $"!!! Access Denied during file write operation for {filePath}. Check permissions."); return ZplRenderResult.CreateFailure($"File write failed (Access Denied): {authEx.Message}"); }
                    catch (IOException ioEx) { _logger.LogError(ioEx, $"!!! IO Exception during file write operation for {filePath}. (Disk full? Path invalid?)"); return ZplRenderResult.CreateFailure($"File write failed (IO Error): {ioEx.Message}"); }
                    catch (Exception ex) { _logger.LogError(ex, $"!!! Unexpected Exception during file write operation for {filePath}: {ex.ToString()}"); return ZplRenderResult.CreateFailure($"File write failed (Unexpected Error): {ex.Message}"); }
                    _logger.LogInformation("--- Finished File Write Operation ---");
                    _logger.LogDebug("Finished save logic block.");
                }
                else { _logger.LogDebug("Output mode is not SaveToFile (it's {Mode}). Skipping file save.", options.OutputMode); }

                _logger.LogInformation("Preparing successful result...");
                return ZplRenderResult.CreateSuccess(generatedPngData);
            }
            catch (Exception ex) { _logger.LogError(ex, "!!! Unhandled exception during ZPL rendering process: {ExceptionMessage}", ex.Message); return ZplRenderResult.CreateFailure($"Unhandled exception: {ex.Message}"); }
            finally { _logger.LogDebug("Entering finally block for surface disposal..."); surface?.Dispose(); _logger.LogDebug("Surface disposal complete (if surface existed)."); }
        }


        // --- Command Handler Helper Methods ---
        // (Assuming full implementations exist for these from previous steps)
        private void HandleLhCommand(ZplRenderState state, string parameters)
        {
            int[] defaultValues = { 0, 0 };
            int[] values = ZplRenderState.ParseIntegerParams(parameters, 2, defaultValues);
            state.LabelHomeX = values[0];
            state.LabelHomeY = values[1];
            _logger.LogDebug("Handled ^LH: Label Home set to X={X}, Y={Y}", state.LabelHomeX, state.LabelHomeY);
        }
        private void HandleFoCommand(ZplRenderState state, string parameters)
        {
            int[] defaultValues = { 0, 0 };
            int[] values = ZplRenderState.ParseIntegerParams(parameters, 2, defaultValues);
            state.CurrentX = values[0];
            state.CurrentY = values[1];
            _logger.LogDebug("Handled ^FO: Field Origin set to X={X}, Y={Y}", state.CurrentX, state.CurrentY);
        }
        private void HandleCfCommand(ZplRenderState state, string parameters)
        {
            string fontId = state.CurrentFontIdentifier; int fontHeight = state.CurrentFontHeightDots; int fontWidth = state.CurrentFontWidthDots;
            if (!string.IsNullOrEmpty(parameters))
            {
                string[] parts = parameters.Split(',');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) { fontId = parts[0].Trim().ToUpperInvariant(); if (fontId.Length > 1 && !fontId.Contains(":")) { fontId = fontId.Substring(0, 1); _logger.LogTrace("^CF: Using first char '{FontId}' as identifier from '{Original}'.", fontId, parts[0]); } else if (fontId.Length == 0) { fontId = "0"; } else { _logger.LogTrace("^CF: Using full identifier '{FontId}'.", fontId); } } else { fontId = "0"; }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) { fontHeight = h; }
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) { fontWidth = w; }
            }
            else { fontId = "0"; _logger.LogTrace("^CF: No parameters, resetting font identifier to default '0', keeping last size."); }
            state.CurrentFontIdentifier = fontId; state.CurrentFontHeightDots = Math.Max(1, fontHeight); state.CurrentFontWidthDots = Math.Max(0, fontWidth);
            _logger.LogDebug("Handled ^CF: Default Font set to ID={ID}, H={H}, W={W}", state.CurrentFontIdentifier, state.CurrentFontHeightDots, state.CurrentFontWidthDots);
        }
        private void HandleAxCommand(ZplRenderState state, char fontIdChar, string parameters)
        {
            string fontId = fontIdChar.ToString().ToUpperInvariant(); char orientation = state.CurrentFontRotation; int height = state.CurrentFontHeightDots; int width = state.CurrentFontWidthDots;
            if (!string.IsNullOrEmpty(parameters))
            {
                string[] parts = parameters.Split(',');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) { char o = parts[0].Trim().ToUpperInvariant().FirstOrDefault('N'); if ("NRIB".Contains(o)) { orientation = o; } }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h_parsed)) { height = Math.Max(1, h_parsed); }
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w_parsed)) { width = Math.Max(0, w_parsed); }
            }
            state.CurrentFontIdentifier = fontId; state.CurrentFontRotation = orientation; state.CurrentFontHeightDots = height; state.CurrentFontWidthDots = width;
            _logger.LogDebug("Handled ^A{ID}: Font set to ID={ID}, Orient={O}, H={H}, W={W}", fontId, state.CurrentFontIdentifier, state.CurrentFontRotation, state.CurrentFontHeightDots, state.CurrentFontWidthDots);
        }
        private void HandleAAtCommand(ZplRenderState state, string parameters)
        {
            char orientation = state.CurrentFontRotation; int height = state.CurrentFontHeightDots; int width = state.CurrentFontWidthDots; string fontPath = state.CurrentFontIdentifier;
            if (!string.IsNullOrEmpty(parameters))
            {
                string[] parts = parameters.Split(',');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) { char o = parts[0].Trim().ToUpperInvariant().FirstOrDefault('N'); if ("NRIB".Contains(o)) orientation = o; }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h_parsed)) { height = Math.Max(1, h_parsed); }
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w_parsed)) { width = Math.Max(0, w_parsed); }
                if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) { fontPath = parts[3].Trim().ToUpperInvariant(); }
            }
            state.CurrentFontIdentifier = fontPath; state.CurrentFontRotation = orientation; state.CurrentFontHeightDots = height; state.CurrentFontWidthDots = width;
            _logger.LogDebug("Handled ^A@: Font set to Path='{Path}', Orient={O}, H={H}, W={W}", state.CurrentFontIdentifier, state.CurrentFontRotation, state.CurrentFontHeightDots, state.CurrentFontWidthDots);
        }
        private SKColor InvertColor(SKColor color) { if (color == SKColors.Black) return SKColors.White; if (color == SKColors.White) return SKColors.Black; return color; }
        private void RenderGraphicBox(SKCanvas canvas, ZplRenderState state, string parameters)
        {
            int[] defaultValues = { 0, 0, 1, 0, 0 }; int[] values = ZplRenderState.ParseIntegerParams(parameters, 5, defaultValues);
            int widthDots = values[0]; int heightDots = values[1]; int thicknessDots = values[2]; int rounding = values[4];
            string[] parts = parameters?.Split(',') ?? Array.Empty<string>(); char lineColorChar = 'B'; if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) { lineColorChar = parts[3].Trim().ToUpperInvariant().FirstOrDefault('B'); }
            rounding = Math.Clamp(rounding, 0, 8);
            _logger.LogDebug("Handling ^GB: W={W}, H={H}, Thick={T}, Color={C}, Round={R}", widthDots, heightDots, thicknessDots, lineColorChar, rounding);
            if (widthDots <= 0 || heightDots <= 0) { _logger.LogWarning("^GB skipped: Invalid width or height ({W}x{H}).", widthDots, heightDots); return; }
            (float pixelX, float pixelY) = state.ConvertDotsToPixels(state.CurrentX, state.CurrentY); float pixelWidth = state.ConvertDimensionToPixels(widthDots); float pixelHeight = state.ConvertDimensionToPixels(heightDots); float pixelThickness = state.ConvertDimensionToPixels(thicknessDots);
            SKRect rect = new SKRect(pixelX, pixelY, pixelX + pixelWidth, pixelY + pixelHeight);
            using (SKPaint paint = new SKPaint())
            {
                bool isReversed = state.IsFieldReversed; SKColor requestedColor = (lineColorChar == 'W') ? SKColors.White : SKColors.Black; paint.Color = isReversed ? InvertColor(requestedColor) : requestedColor; if (isReversed) _logger.LogTrace("  Applying field reverse (Color: {Color})", paint.Color);
                paint.IsAntialias = true; float cornerRadius = 0f; if (rounding > 0) { cornerRadius = rounding * 2.0f; cornerRadius = Math.Min(cornerRadius, pixelWidth / 2.0f); cornerRadius = Math.Min(cornerRadius, pixelHeight / 2.0f); _logger.LogTrace("  Calculated corner radius: {Radius:F1}px for ZPL rounding {ZplRound}", cornerRadius, rounding); }
                if (thicknessDots <= 0) { paint.Style = SKPaintStyle.Fill; _logger.LogTrace("Drawing filled {Shape} at ({X:F1},{Y:F1}), Size=({W:F1}x{H:F1}), Color={C}", cornerRadius > 0 ? "RoundRect" : "Rect", rect.Left, rect.Top, rect.Width, rect.Height, paint.Color); if (cornerRadius > 0) { canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, paint); } else { canvas.DrawRect(rect, paint); } }
                else { paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = Math.Max(1, pixelThickness); float inset = paint.StrokeWidth / 2.0f; rect.Inflate(-inset, -inset); float adjustedCornerRadius = Math.Max(0, cornerRadius - inset); _logger.LogTrace("Drawing bordered {Shape} at ({X:F1},{Y:F1}), Size=({W:F1}x{H:F1}), Thick={T:F1}, Radius={Rad:F1}, Color={C}", cornerRadius > 0 ? "RoundRect" : "Rect", rect.Left, rect.Top, rect.Width, rect.Height, paint.StrokeWidth, adjustedCornerRadius, paint.Color); if (cornerRadius > 0) { canvas.DrawRoundRect(rect, adjustedCornerRadius, adjustedCornerRadius, paint); } else { canvas.DrawRect(rect, paint); } }
                if (isReversed) state.IsFieldReversed = false;
            }
        }
        private void HandleByCommand(ZplRenderState state, string parameters)
        {
            int moduleWidth = 2; float ratio = 3.0f; int height = 10;
            if (!string.IsNullOrEmpty(parameters))
            {
                string[] parts = parameters.Split(',');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) { moduleWidth = Math.Clamp(w, 1, 10); }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) { ratio = Math.Clamp(r, 2.0f, 3.0f); }
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) { height = Math.Max(1, h); }
            }
            state.BarcodeModuleWidthDots = moduleWidth; state.BarcodeWideBarRatio = ratio; state.BarcodeHeightDots = height;
            _logger.LogDebug("Handled ^BY: Defaults set - ModuleWidth={W}, Ratio={R:F1}, Height={H}", state.BarcodeModuleWidthDots, state.BarcodeWideBarRatio, state.BarcodeHeightDots);
        }
        private void HandleBcCommand(ZplRenderState state, ZplCommand command)
        {
            _logger.LogDebug("Setting up ^BC barcode state."); state.CurrentBarcodeCommand = command; state.CurrentBarcodeParams.Clear();
            if (!string.IsNullOrEmpty(command.Parameters))
            {
                string[] parts = command.Parameters.Split(','); _logger.LogTrace("Parsing ^BC parameters: {Params}", command.Parameters);
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) state.CurrentBarcodeParams["Orientation"] = parts[0].Trim().ToUpperInvariant(); if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) state.CurrentBarcodeParams["Height"] = parts[1].Trim(); if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])) state.CurrentBarcodeParams["PrintInterpretationLine"] = parts[2].Trim().ToUpperInvariant(); if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) state.CurrentBarcodeParams["LineAbove"] = parts[3].Trim().ToUpperInvariant(); if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4])) state.CurrentBarcodeParams["UccCheckDigit"] = parts[4].Trim().ToUpperInvariant(); if (parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5])) state.CurrentBarcodeParams["Mode"] = parts[5].Trim().ToUpperInvariant();
                foreach (var kvp in state.CurrentBarcodeParams) { _logger.LogTrace("  Stored Barcode Param: {Key} = '{Value}'", kvp.Key, kvp.Value); }
            }
            else { _logger.LogTrace("No parameters provided for ^BC."); }
        }
        private void HandleB3Command(ZplRenderState state, ZplCommand command)
        {
            _logger.LogDebug("Setting up ^B3 barcode state."); state.CurrentBarcodeCommand = command; state.CurrentBarcodeParams.Clear();
            if (!string.IsNullOrEmpty(command.Parameters))
            {
                string[] parts = command.Parameters.Split(','); _logger.LogTrace("Parsing ^B3 parameters: {Params}", command.Parameters);
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) state.CurrentBarcodeParams["Orientation"] = parts[0].Trim().ToUpperInvariant(); if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) state.CurrentBarcodeParams["Mod43CheckDigit"] = parts[1].Trim().ToUpperInvariant(); if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])) state.CurrentBarcodeParams["Height"] = parts[2].Trim(); if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) state.CurrentBarcodeParams["PrintInterpretationLine"] = parts[3].Trim().ToUpperInvariant(); if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4])) state.CurrentBarcodeParams["LineAbove"] = parts[4].Trim().ToUpperInvariant();
                foreach (var kvp in state.CurrentBarcodeParams) { _logger.LogTrace("  Stored Barcode Param: {Key} = '{Value}'", kvp.Key, kvp.Value); }
            }
            else { _logger.LogTrace("No parameters provided for ^B3."); }
        }
        private void HandleFrCommand(ZplRenderState state, string parameters)
        {
            state.IsFieldReversed = !state.IsFieldReversed;
            _logger.LogDebug("Handled ^FR: Field Reverse state toggled to {State}", state.IsFieldReversed);
        }
        private void RenderGraphicImage(SKCanvas canvas, ZplRenderState state, string parameters)
        {
            string imageName = null; int xMagnification = 1; int yMagnification = 1;
            if (!string.IsNullOrEmpty(parameters))
            {
                string[] parts = parameters.Split(','); if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) { imageName = parts[0].Trim(); }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), out int xMagParsed)) { xMagnification = Math.Clamp(xMagParsed, 1, 10); }
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2].Trim(), out int yMagParsed)) { yMagnification = Math.Clamp(yMagParsed, 1, 10); }
            }
            _logger.LogDebug("Handling ^XG: Name='{Name}', Mag=({X}x{Y})", imageName, xMagnification, yMagnification);
            if (string.IsNullOrEmpty(imageName)) { _logger.LogWarning("^XG skipped: Image name parameter is missing."); return; }
            _logger.LogTrace("Attempting to retrieve '{Name}' from graphic store...", imageName);
            if (_graphicStore.TryGetValue(imageName, out SKBitmap bitmap))
            {
                _logger.LogDebug("  Successfully retrieved graphic '{Name}' from store.", imageName); if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0) { _logger.LogWarning("Stored graphic '{Name}' is invalid (null or zero dimensions). Skipping draw.", imageName); return; }
                _logger.LogTrace("  Retrieved graphic details: {W}x{H} pixels.", bitmap.Width, bitmap.Height);
                bool isReversed = state.IsFieldReversed; if (isReversed) { _logger.LogWarning("Field Reverse (^FR) is not supported for recalled graphics (^XG). Drawing normally."); state.IsFieldReversed = false; }
                (float pixelX, float pixelY) = state.ConvertDotsToPixels(state.CurrentX, state.CurrentY); float destWidth = bitmap.Width * xMagnification; float destHeight = bitmap.Height * yMagnification; SKRect destRect = SKRect.Create(pixelX, pixelY, destWidth, destHeight); _logger.LogTrace("  Calculated DestRect=({L:F1}, {T:F1}, {W:F1}, {H:F1})", destRect.Left, destRect.Top, destRect.Width, destRect.Height);
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.None }) { _logger.LogTrace("  Calling canvas.DrawBitmap..."); canvas.DrawBitmap(bitmap, destRect, paint); _logger.LogDebug("  Finished canvas.DrawBitmap for '{Name}'.", imageName); }
            }
            else { _logger.LogWarning("Graphic '{Name}' specified in ^XG not found in graphic store.", imageName); }
        }
        private void HandleDgCommand(ZplRenderState state, ZplCommand command)
        {
            _logger.LogDebug("Handling ~DG command..."); string imageName = null; int totalBytes = 0; int bytesPerRow = 0; string hexData = null;
            if (!string.IsNullOrEmpty(command.Parameters)) { string[] parts = command.Parameters.Split(new char[] { ',' }, 4); if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) { imageName = parts[0].Trim(); } if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int t)) { totalBytes = t; } if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int w)) { bytesPerRow = w; } if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) { hexData = parts[3].Trim(); } }
            _logger.LogInformation("Parsed ~DG: Name='{Name}', TotalBytes={T}, BytesPerRow={W}", imageName, totalBytes, bytesPerRow); _logger.LogTrace("  Hex Data (full): {Data}", hexData);
            if (string.IsNullOrEmpty(imageName) || totalBytes <= 0 || bytesPerRow <= 0 || string.IsNullOrEmpty(hexData)) { _logger.LogError("~DG command ignored: Invalid or missing parameters. Name='{Name}', TotalBytes={T}, BytesPerRow={W}, HasHexData={HasData}", imageName, totalBytes, bytesPerRow, !string.IsNullOrEmpty(hexData)); return; }
            _logger.LogTrace("Decoding hex data..."); byte[] graphicBytes = DecodeHexToBytes(hexData); if (graphicBytes == null) { _logger.LogError("~DG failed: Could not decode hex data for image '{Name}'.", imageName); return; }
            _logger.LogDebug("Successfully decoded {Count} bytes of graphic data for '{Name}'. Expected {Expected}.", graphicBytes.Length, imageName, totalBytes);
            _logger.LogTrace("Calling CreateBitmapFromMonochromeData..."); SKBitmap bitmap = CreateBitmapFromMonochromeData(graphicBytes, bytesPerRow, totalBytes);
            if (bitmap != null) { _logger.LogInformation("Successfully created {W}x{H} bitmap for '{Name}'.", bitmap.Width, bitmap.Height, imageName); _graphicStore[imageName] = bitmap; _logger.LogDebug("Stored graphic '{Name}' in memory store.", imageName); } else { _logger.LogError("~DG failed: Could not create bitmap for image '{Name}'.", imageName); }
        }
        private byte[] DecodeHexToBytes(string hexDataString)
        {
            if (string.IsNullOrWhiteSpace(hexDataString)) return null; var cleanHex = new StringBuilder(hexDataString.Length); foreach (char c in hexDataString) { if (char.IsLetterOrDigit(c)) { cleanHex.Append(c); } }
            string hex = cleanHex.ToString(); if (hex.Length % 2 != 0) { _logger.LogWarning("Hex data string has odd length ({Len}). Cannot decode.", hex.Length); return null; }
            try { byte[] bytes = new byte[hex.Length / 2]; for (int i = 0; i < bytes.Length; i++) { string hexPair = hex.Substring(i * 2, 2); bytes[i] = byte.Parse(hexPair, NumberStyles.HexNumber, CultureInfo.InvariantCulture); } return bytes; } catch (Exception ex) { _logger.LogError(ex, "Failed to decode hex string segment near '{Segment}'.", hex.Length > 20 ? hex.Substring(0, 20) : hex); return null; }
        }
        private SKBitmap CreateBitmapFromMonochromeData(byte[] data, int bytesPerRow, int totalBytes)
        {
            if (data == null || bytesPerRow <= 0 || totalBytes <= 0 || data.Length < totalBytes) { _logger.LogError("CreateBitmapFromMonochromeData: Invalid input data or parameters. BytesPerRow={Bpr}, TotalBytes={Tb}, DataLength={Len}", bytesPerRow, totalBytes, data?.Length ?? 0); return null; }
            int widthPixels = bytesPerRow * 8; if (totalBytes % bytesPerRow != 0) { _logger.LogWarning("CreateBitmapFromMonochromeData: TotalBytes ({Tb}) is not an even multiple of BytesPerRow ({Bpr}). Bitmap height might be incorrect.", totalBytes, bytesPerRow); }
            int heightPixels = totalBytes / bytesPerRow; if (heightPixels <= 0 || widthPixels <= 0) { _logger.LogError("CreateBitmapFromMonochromeData: Calculated dimensions are invalid ({W}x{H}).", widthPixels, heightPixels); return null; }
            _logger.LogDebug("Creating monochrome bitmap with dimensions: {W}x{H}", widthPixels, heightPixels); var bitmap = new SKBitmap(widthPixels, heightPixels, SKColorType.Gray8, SKAlphaType.Opaque); if (bitmap == null) { _logger.LogError("SKBitmap creation returned null for {W}x{H}", widthPixels, heightPixels); return null; }
            try
            {
                IntPtr pixelPtr = bitmap.GetPixels(); _logger.LogTrace("Bitmap pixel pointer obtained. RowBytes={RB}", bitmap.RowBytes);
                unsafe
                {
                    byte* pixels = (byte*)pixelPtr.ToPointer(); _logger.LogTrace("Iterating through {H} rows, {Bpr} bytes per row...", heightPixels, bytesPerRow);
                    for (int y = 0; y < heightPixels; y++)
                    {
                        for (int byteInRow = 0; byteInRow < bytesPerRow; byteInRow++)
                        {
                            int byteIndex = y * bytesPerRow + byteInRow; if (byteIndex >= data.Length) { _logger.LogWarning("CreateBitmapFromMonochromeData: Attempted to read past end of data array at byte index {Idx} (y={Y}, byteInRow={Br}).", byteIndex, y, byteInRow); break; }
                            byte currentByte = data[byteIndex];
                            for (int bitInByte = 0; bitInByte < 8; bitInByte++) { int px = byteInRow * 8 + bitInByte; if (px >= widthPixels) continue; bool isBitSet = (currentByte & (1 << (7 - bitInByte))) != 0; int offset = y * bitmap.RowBytes + px; pixels[offset] = isBitSet ? (byte)0x00 : (byte)0xFF; if (y == 0 && byteInRow == 0 && bitInByte == 0) { _logger.LogTrace(" -> First pixel (0,0) set to: {Val} (BitSet={Bit})", pixels[offset], isBitSet); } }
                        }
                    }
                } // end unsafe
                _logger.LogTrace("Finished setting pixels for ~DG bitmap."); return bitmap;
            }
            catch (Exception ex) { _logger.LogError(ex, "Error creating bitmap from monochrome data."); bitmap?.Dispose(); return null; }
        }
        private void RenderInterpretationLine(SKCanvas canvas, ZplRenderState state, string data, float bcX, float bcY, float bcW, float bcH, bool above, char orientation)
        {
            if (string.IsNullOrEmpty(data)) return; _logger.LogDebug("Attempting to render interpretation line for '{Data}' (Center-Aligned)", data); if (orientation != 'N') { _logger.LogWarning("RenderInterpretationLine: Rotation '{Orient}' not implemented. Skipping interpretation line.", orientation); return; }
            string fontPath = state.GetCurrentTtfFontPath(); if (string.IsNullOrEmpty(fontPath)) { _logger.LogError("RenderInterpretationLine failed: Cannot find valid font path..."); return; }
            int fontIndex = 0; string actualFontFile = fontPath; if (fontPath.Contains(',')) { var parts = fontPath.Split(','); actualFontFile = parts[0]; if (parts.Length > 1 && int.TryParse(parts[1], out int index)) fontIndex = index; }
            if (!File.Exists(actualFontFile)) { _logger.LogError("RenderInterpretationLine failed: Font file not found..."); return; }
            try
            {
                using (SKTypeface typeface = SKTypeface.FromFile(actualFontFile, fontIndex)) using (SKPaint paint = new SKPaint())
                {
                    if (typeface == null) { _logger.LogError("RenderInterpretationLine failed: SKTypeface.FromFile returned null..."); return; }
                    paint.Typeface = typeface; float interpHeightDots = Math.Max(8, state.CurrentFontHeightDots * 0.8f); paint.TextSize = state.ConvertDimensionToPixels((int)Math.Round(interpHeightDots)); bool isReversed = state.IsFieldReversed; paint.Color = isReversed ? SKColors.White : SKColors.Black; if (isReversed) _logger.LogTrace("  Applying field reverse to interpretation line (White text)"); paint.IsAntialias = true; paint.TextAlign = SKTextAlign.Center;
                    float textCenterX = bcX + (bcW / 2.0f); float textBaselineY; const float gap = 3; if (above) { textBaselineY = bcY - gap; } else { textBaselineY = bcY + bcH + gap + paint.TextSize; }
                    _logger.LogTrace("Drawing interpretation line '{Data}' at center X={X:F1}, baseline Y={Y:F1}, Size={Size:F1}", data, textCenterX, textBaselineY, paint.TextSize); canvas.DrawText(data, textCenterX, textBaselineY, paint);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "RenderInterpretationLine failed during SkiaSharp operation..."); }
        }
        private bool IsValidOutputPath(string outputPath, string allowedBaseDirectory, out string errorMessage)
        {
            errorMessage = ""; if (string.IsNullOrWhiteSpace(outputPath)) { errorMessage = "Output path cannot be empty."; return false; }
            try { string fullPath = Path.GetFullPath(outputPath); string fullAllowedBase = Path.GetFullPath(allowedBaseDirectory); if (!fullPath.StartsWith(fullAllowedBase, StringComparison.OrdinalIgnoreCase)) { errorMessage = $"Output path '{outputPath}' must be within the allowed directory '{allowedBaseDirectory}'. Resolved to '{fullPath}'."; _logger.LogWarning(errorMessage); return false; } if (outputPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { errorMessage = $"Output path '{outputPath}' contains invalid characters."; _logger.LogWarning(errorMessage); return false; } string fileName = Path.GetFileName(outputPath); if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { errorMessage = $"Output filename '{fileName}' is invalid."; _logger.LogWarning(errorMessage); return false; } } catch (ArgumentException argEx) { errorMessage = $"Output path '{outputPath}' is invalid: {argEx.Message}"; _logger.LogWarning(argEx, errorMessage); return false; } catch (Exception ex) { errorMessage = $"Error validating output path '{outputPath}': {ex.Message}"; _logger.LogError(ex, errorMessage); return false; }
            _logger.LogTrace("IsValidOutputPath passed for: {Path}", outputPath); return true;
        }
        private BarcodeFormat? MapZplToZXingFormat(string zplCommandCode)
        {
            switch (zplCommandCode?.ToUpperInvariant()) { case "BC": return BarcodeFormat.CODE_128; case "B3": return BarcodeFormat.CODE_39; case "BQ": return BarcodeFormat.QR_CODE; default: _logger.LogWarning("MapZplToZXingFormat: No mapping for ZPL command {Cmd}", zplCommandCode); return null; }
        }
        private void HandleBqCommand(ZplRenderState state, ZplCommand command)
        {
            _logger.LogDebug("Setting up ^BQ (QR Code) barcode state."); state.CurrentBarcodeCommand = command; state.CurrentBarcodeParams.Clear();
            if (!string.IsNullOrEmpty(command.Parameters)) { string[] parts = command.Parameters.Split(','); _logger.LogTrace("Parsing ^BQ parameters: {Params}", command.Parameters); if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])) state.CurrentBarcodeParams["Magnification"] = parts[2].Trim(); if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) state.CurrentBarcodeParams["ErrorCorrection"] = parts[3].Trim().ToUpperInvariant(); foreach (var kvp in state.CurrentBarcodeParams) { _logger.LogTrace("  Stored Barcode Param: {Key} = '{Value}'", kvp.Key, kvp.Value); } } else { _logger.LogTrace("No parameters provided for ^BQ."); }
        }
        private void HandleFbCommand(ZplRenderState state, string parameters)
        {
            int width = 0; int maxLines = 1; int lineSpacing = 0; char justification = 'L'; int hangingIndent = 0;
            if (!string.IsNullOrEmpty(parameters)) { string[] parts = parameters.Split(','); if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int w)) width = Math.Max(0, w); if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int b)) maxLines = Math.Max(1, b); if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int c)) lineSpacing = c; if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) { char j = parts[3].Trim().ToUpperInvariant().FirstOrDefault('L'); if ("LRCJ".Contains(j)) justification = j; } if (parts.Length > 4 && int.TryParse(parts[4].Trim(), out int e)) hangingIndent = Math.Max(0, e); }
            state.FieldBlockWidthDots = width; state.FieldBlockMaxLines = maxLines; state.FieldBlockLineSpacingDots = lineSpacing; state.FieldBlockJustification = justification; state.FieldBlockHangingIndentDots = hangingIndent;
            _logger.LogDebug("Handled ^FB: Width={W}, MaxLines={B}, LineSpace={C}, Justify='{D}', Indent={E}", width, maxLines, lineSpacing, justification, hangingIndent);
        }
        private void RenderText(SKCanvas canvas, ZplRenderState state, string data)
        {
            if (string.IsNullOrEmpty(data)) { _logger.LogTrace("RenderText skipped: No data provided."); return; }
            bool isFieldBlockActive = state.FieldBlockWidthDots > 0; (float originX, float originY) = state.ConvertDotsToPixels(state.CurrentX, state.CurrentY); int fontHeightDots = state.CurrentFontHeightDots; char rotation = state.CurrentFontRotation;
            string fontPath = state.GetCurrentTtfFontPath(); if (string.IsNullOrEmpty(fontPath)) { _logger.LogError("RenderText failed: Cannot find valid font path..."); return; }
            int fontIndex = 0; string actualFontFile = fontPath; if (fontPath.Contains(',')) { var parts = fontPath.Split(','); actualFontFile = parts[0]; if (parts.Length > 1 && int.TryParse(parts[1], out int index)) fontIndex = index; }
            if (!File.Exists(actualFontFile)) { _logger.LogError("RenderText failed: Font file not found..."); return; }
            _logger.LogDebug("Rendering Text (Block Active={IsBlock}): '{Data}' at Origin=({X:F1},{Y:F1}) using Font='{FontPath}', H={H}d", isFieldBlockActive, data, originX, originY, fontPath, fontHeightDots);
            try
            {
                using (SKTypeface typeface = SKTypeface.FromFile(actualFontFile, fontIndex)) using (SKPaint paint = new SKPaint())
                {
                    if (typeface == null) { _logger.LogError("RenderText failed: SKTypeface.FromFile returned null..."); return; }
                    paint.Typeface = typeface; paint.TextSize = state.ConvertDimensionToPixels(fontHeightDots); bool isReversed = state.IsFieldReversed; paint.Color = isReversed ? SKColors.White : SKColors.Black; if (isReversed) _logger.LogTrace("  Applying field reverse (White text)"); paint.IsAntialias = true;
                    if (rotation != 'N') { _logger.LogWarning("RenderText: Rotation '{Rotation}' not fully implemented. Drawing normally.", rotation); }
                    if (!isFieldBlockActive) { float baselineY = originY + paint.TextSize; _logger.LogTrace("  Drawing simple text line at ({X:F1},{Y:F1})", originX, baselineY); canvas.DrawText(data, originX, baselineY, paint); }
                    else
                    {
                        _logger.LogTrace("  Applying Field Block: Width={W}d, MaxLines={Mx}, Spacing={Sp}d, Justify='{J}', Indent={I}d", state.FieldBlockWidthDots, state.FieldBlockMaxLines, state.FieldBlockLineSpacingDots, state.FieldBlockJustification, state.FieldBlockHangingIndentDots);
                        float blockWidthPixels = state.ConvertDimensionToPixels(state.FieldBlockWidthDots); float lineSpacingPixels = state.ConvertDimensionToPixels(state.FieldBlockLineSpacingDots); float hangingIndentPixels = state.ConvertDimensionToPixels(state.FieldBlockHangingIndentDots); float currentY = originY; string[] paragraphs = data.Split(new[] { @"\\" }, StringSplitOptions.None); int linesDrawn = 0;
                        foreach (string paragraph in paragraphs)
                        {
                            if (linesDrawn >= state.FieldBlockMaxLines) break; string textToProcess = paragraph.Replace(@"\&", ""); string[] words = textToProcess.Split(' '); var currentLine = new StringBuilder(); float currentLineWidth = 0; bool isFirstLineOfPara = true;
                            for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
                            {
                                string word = words[wordIndex]; if (string.IsNullOrEmpty(word)) continue; string potentialWord = (currentLine.Length > 0 ? " " : "") + word; float potentialWordWidth = paint.MeasureText(potentialWord); float availableWidth = blockWidthPixels - (isFirstLineOfPara ? hangingIndentPixels : 0);
                                if (currentLineWidth + potentialWordWidth <= availableWidth || currentLine.Length == 0) { currentLine.Append(potentialWord); currentLineWidth += potentialWordWidth; }
                                else
                                {
                                    if (currentLine.Length > 0) { DrawTextBlockLine(canvas, state, currentLine.ToString(), paint, originX, currentY, blockWidthPixels, isFirstLineOfPara); linesDrawn++; currentY += paint.TextSize + lineSpacingPixels; isFirstLineOfPara = false; if (linesDrawn >= state.FieldBlockMaxLines) break; }
                                    currentLine.Clear(); currentLine.Append(word); currentLineWidth = paint.MeasureText(word); if (currentLineWidth > availableWidth) { _logger.LogWarning("Word '{Word}' is wider than the field block width ({W}px). Truncating or overflow may occur.", word, availableWidth); }
                                }
                            }
                            if (currentLine.Length > 0 && linesDrawn < state.FieldBlockMaxLines) { DrawTextBlockLine(canvas, state, currentLine.ToString(), paint, originX, currentY, blockWidthPixels, isFirstLineOfPara); linesDrawn++; currentY += paint.TextSize + lineSpacingPixels; }
                        }
                    }
                    if (isFieldBlockActive) { _logger.LogTrace("  Resetting Field Block state."); state.FieldBlockWidthDots = 0; }
                    if (isReversed) state.IsFieldReversed = false;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "RenderText failed during SkiaSharp operation for font '{FontPath}'. Text='{Data}'", fontPath, data); }
        }
        private void DrawTextBlockLine(SKCanvas canvas, ZplRenderState state, string line, SKPaint paint, float blockOriginX, float currentY, float blockWidthPixels, bool isFirstLine)
        {
            float textWidth = paint.MeasureText(line);
            float xPos = blockOriginX;
            float yPos = currentY + paint.TextSize; // Baseline adjustment

            // Apply hanging indent for the first line
            if (isFirstLine && state.FieldBlockHangingIndentDots > 0)
            {
                float indentPixels = state.ConvertDimensionToPixels(state.FieldBlockHangingIndentDots);
                xPos += indentPixels;
                // Adjust available width? Not needed for left-align, but matters for others.
            }

            // Apply Justification (Basic Left/Center/Right for now)
            switch (state.FieldBlockJustification)
            {
                case 'C': // Center
                    xPos += (blockWidthPixels - textWidth) / 2.0f;
                    break;
                case 'R': // Right
                    xPos += blockWidthPixels - textWidth;
                    break;
                case 'J': // Justified (Treat as Left for now - complex)
                    _logger.LogWarning("Field Block Justification 'J' not fully implemented. Using Left alignment.");
                    goto case 'L'; // **** CORRECTED: Use goto case ****
                case 'L': // Left (Default)
                default:
                    // xPos already calculated with indent
                    break; // Ensure default/L case also ends correctly
            }

            _logger.LogTrace("    Drawing FB Line: '{Line}' at X={X:F1}, Y={Y:F1}", line, xPos, yPos);
            canvas.DrawText(line, xPos, yPos, paint);
        }

        private void RenderBarcode(SKCanvas canvas, ZplRenderState state, string data)
        {
            if (state.CurrentBarcodeCommand == null || data == null) { _logger.LogWarning("RenderBarcode cannot execute: Invalid state or missing data."); return; }
            string commandCode = state.CurrentBarcodeCommand.CommandCode; _logger.LogDebug("==> Attempting RenderBarcode: {Cmd}, Data='{Data}'", commandCode, data); SKBitmap barcodeBitmap = null;
            try
            {
                BarcodeFormat? zxingFormat = MapZplToZXingFormat(commandCode); if (zxingFormat == null) { _logger.LogWarning("Cannot map ZPL command '{Cmd}' to a supported ZXing format.", commandCode); return; }
                char orientation = state.GetBarcodeParam("Orientation", "N").ToUpperInvariant().FirstOrDefault('N'); int barcodeHeightDots = state.GetBarcodeIntParam("Height", state.BarcodeHeightDots); bool printInterpretationLine = state.GetBarcodeBoolParam("PrintInterpretationLine", true); bool interpAbove = state.GetBarcodeBoolParam("LineAbove", false); int zplModuleWidthDots = state.BarcodeModuleWidthDots;
                _logger.LogTrace("  Retrieved Common Params: Orient='{O}', Height={H}d, ZplModWidth={Mw}d, PrintLine={P}, LineAbove={La}", orientation, barcodeHeightDots, zplModuleWidthDots, printInterpretationLine, interpAbove);
                int pixelHeight = (int)state.ConvertDimensionToPixels(barcodeHeightDots); int pixelWidth = 0; if (pixelHeight <= 0) pixelHeight = 10;
                var writerOptions = new ZXing.Common.EncodingOptions { Margin = 0 }; int zplMagnification = 1;
                if (zxingFormat == BarcodeFormat.QR_CODE)
                {
                    printInterpretationLine = false; zplMagnification = state.GetBarcodeIntParam("Magnification", 3); zplMagnification = Math.Clamp(zplMagnification, 1, 10); string ecl = state.GetBarcodeParam("ErrorCorrection", "M"); var errorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel.M; switch (ecl) { case "H": errorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel.H; break; case "Q": errorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel.Q; break; case "M": errorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel.M; break; case "L": errorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel.L; break; }
             //       if (writerOptions.Hints == null) writerOptions.Hints = new Dictionary<EncodeHintType, object>(); writerOptions.Hints[EncodeHintType.ERROR_CORRECTION] = errorCorrectionLevel; writerOptions.Hints[EncodeHintType.CHARACTER_SET] = "UTF-8"; _logger.LogTrace("  QR Code Params: ZplMagnification={Mag}, ErrorCorrection={ECL}, Charset=UTF-8", zplMagnification, errorCorrectionLevel); writerOptions.Width = 0; writerOptions.Height = 0; writerOptions.PureBarcode = true;
                }
                else { writerOptions.Height = pixelHeight; writerOptions.Width = 0; writerOptions.PureBarcode = true; } //!printInterpretationLine; }
                var writer = new ZXing.SkiaSharp.BarcodeWriter { Format = zxingFormat.Value, Options = writerOptions }; _logger.LogDebug("  Calling SkiaSharp ZXing Writer: Format={Fmt}, Options(H={H},W={W},Pure={P})", writer.Format, writerOptions.Height, writerOptions.Width, writerOptions.PureBarcode);
                barcodeBitmap = writer.Write(data); if (barcodeBitmap == null || barcodeBitmap.Width <= 0 || barcodeBitmap.Height <= 0) { _logger.LogError("SkiaSharp ZXing Write failed to generate bitmap for {Fmt}, Data='{Data}'", zxingFormat.Value, data); return; }
                _logger.LogDebug("  SkiaSharp ZXing generated initial bitmap: {W}x{H}", barcodeBitmap.Width, barcodeBitmap.Height);
                float targetPixelWidth; float targetPixelHeight; if (zxingFormat == BarcodeFormat.QR_CODE) { float scaleFactor = zplMagnification; targetPixelWidth = barcodeBitmap.Width * scaleFactor; targetPixelHeight = barcodeBitmap.Height * scaleFactor; _logger.LogTrace("  Scaling QR Code using ZplMagnification={Mag}: Target Size={TW:F0}x{TH:F0}", zplMagnification, targetPixelWidth, targetPixelHeight); } else { targetPixelWidth = barcodeBitmap.Width * zplModuleWidthDots; targetPixelHeight = pixelHeight; _logger.LogTrace("  Scaling 1D Barcode using ZplModWidth={ZplW}: Target Size={TW:F0}x{TH:F0}", zplModuleWidthDots, targetPixelWidth, targetPixelHeight); }
                (float pixelX, float pixelY) = state.ConvertDotsToPixels(state.CurrentX, state.CurrentY); _logger.LogDebug("  Drawing barcode at canvas coordinates ({Px:F1},{Py:F1})", pixelX, pixelY); SKRect destRect = SKRect.Create(pixelX, pixelY, targetPixelWidth, targetPixelHeight);
                bool isRotated = (orientation != 'N'); if (isRotated) { canvas.Save(); float degrees = 0; switch (orientation) { case 'R': degrees = 90; break; case 'I': degrees = 180; break; case 'B': degrees = 270; break; } canvas.RotateDegrees(degrees, pixelX, pixelY); _logger.LogDebug("  Applied canvas rotation: {Deg} degrees around ({Px:F1},{Py:F1})", degrees, pixelX, pixelY); }
                try { using (var paint = new SKPaint { FilterQuality = SKFilterQuality.None }) { canvas.DrawBitmap(barcodeBitmap, destRect, paint); } _logger.LogDebug("  Drew scaled barcode bitmap into DestRect ({L:F1}, {T:F1}, {W:F1}, {H:F1})", destRect.Left, destRect.Top, destRect.Width, destRect.Height); if (zxingFormat != BarcodeFormat.QR_CODE && printInterpretationLine) { RenderInterpretationLine(canvas, state, data, destRect.Left, destRect.Top, destRect.Width, destRect.Height, interpAbove, orientation); } } finally { if (isRotated) { canvas.Restore(); _logger.LogDebug("  Restored canvas state after rotation."); } }
            }
            catch (Exception ex) { _logger.LogError(ex, "Error rendering barcode {Cmd}, Data='{Data}'", commandCode, data); }
            finally { barcodeBitmap?.Dispose(); state.CurrentBarcodeCommand = null; state.CurrentBarcodeParams.Clear(); }
        }

        // **** REPLACED ParseZplRawCommands ****
        /// <summary>
        /// Parses a raw ZPL string into a list of individual command strings.
        /// Handles comments (^FX) and basic structure.
        /// </summary>
        /// <param name="zplCode">The raw ZPL input string.</param>
        /// <returns>A list of raw command strings (e.g., "^FO50,50").</returns>
        private List<string> ParseZplRawCommands(string zplCode)
        {
            var commands = new List<string>();
            if (string.IsNullOrWhiteSpace(zplCode))
            {
                _logger.LogDebug("ParseZplRawCommands received null or empty input.");
                return commands; // Return empty list
            }

            _logger.LogDebug("Starting ZPL raw command parsing.");

            // Regex to split the string *before* each command prefix (^ or ~)
            // using positive lookahead (?=...)
            // It also removes leading/trailing whitespace from the whole input first.
            string[] segments = Regex.Split(zplCode.Trim(), @"(?=[\^~])");

            foreach (string segment in segments)
            {
                string cleanSegment = segment.Trim();

                // Skip empty segments that might result from the split
                if (string.IsNullOrEmpty(cleanSegment))
                {
                    continue;
                }

                // Check if it starts with a valid prefix
                if (cleanSegment.StartsWith("^") || cleanSegment.StartsWith("~"))
                {
                    // Simple handling for standard comments: skip ^FX commands entirely
                    if (cleanSegment.StartsWith("^FX", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping comment segment: {Segment}", cleanSegment);
                        continue;
                    }

                    // Handle potentially incorrect comment parsing from previous test ZPL
                    // If ZplCommand constructor incorrectly creates a ^CO or ~CO command,
                    // we might want to filter them here too, although fixing ZplCommand is better.
                    // For now, let's assume ZplCommand handles prefixes correctly and only filter ^FX.

                    // Add the valid command segment to the list
                    commands.Add(cleanSegment);
                    _logger.LogTrace("Parsed command segment: {Segment}", cleanSegment);
                }
                else
                {
                    // Log segments that don't start with a prefix
                    _logger.LogDebug("Skipping segment without command prefix: {Segment}", cleanSegment);
                }
            }

            _logger.LogDebug("Finished ZPL raw command parsing, found {Count} commands (excluding comments/invalid segments).", commands.Count);
            return commands;
        }


    } // End class ZplRenderer
} // End namespace ZplRendererLib
