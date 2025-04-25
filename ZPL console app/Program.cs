using System;
using System.IO;
using Microsoft.Extensions.Logging; // NuGet: Microsoft.Extensions.Logging / .Console
using ZplRendererLib; // Your class library namespace

// Main application namespace
namespace ZplRendererTester
{
    internal class Program
    {
        // The required entry point for an executable application
        static void Main(string[] args)
        {
            Console.WriteLine("ZPL Renderer Test Application");

            // --- 1. Configure Logging ---
            // Setup basic console logging (adjust level for debugging)
            bool enableDebugLogging = true; // Set to true to see detailed logs
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(enableDebugLogging ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger<ZplRenderer> rendererLogger = loggerFactory.CreateLogger<ZplRenderer>();

            // --- 2. Create Renderer Instance ---
            // Pass the configured logger to the renderer
            ZplRenderer renderer = new ZplRenderer(rendererLogger);

            // --- 3. Define ZPL and Options ---
            // Ensure required options are set
            // IMPORTANT: Update file paths below!
            string testZpl = @"
^XA
^FO20,20
^CF0,30
^FB500,5,10,C,15^FDThis is a test of the Field Block command. It should wrap text within a 500-dot wide block, center each line, add 10 dots between lines, allow up to 5 lines, and apply a 15-dot hanging indent to the first line. ZPL newlines \\ should also work. \\ This is the second paragraph.^FS
^XZ
";

            string outputPath = @"C:\ZplOutputTemp\test_output.png"; // !!! CHANGE THIS PATH !!!
            // Ensure the base directory C:\ZplOutputTemp exists and renderer has write permission

            ZplRenderOptions options = new ZplRenderOptions
            {
                ZplCode = testZpl,
                DensityDpi = 203,
                WidthInches = 4,
                HeightInches = 6,
                OutputMode = ZplOutputMode.SaveToFile, // Or ReturnByteArray
                OutputFilePath = outputPath
            };

            // --- 4. Call the Renderer ---
            Console.WriteLine($"Rendering ZPL to {options.OutputMode}...");
            ZplRenderResult result = renderer.RenderZplToPng(options);

            // --- 5. Check Result ---
            if (result.Success)
            {
                Console.WriteLine("Rendering Succeeded!");
                if (options.OutputMode == ZplOutputMode.ReturnByteArray && result.PngData != null)
                {
                    Console.WriteLine($"Received {result.PngData.Length} bytes of PNG data.");
                    // Optionally save it here if needed
                    // try { File.WriteAllBytes(@"C:\ZplOutputTemp\output_from_caller.png", result.PngData); } catch(Exception ex) { Console.WriteLine($"Error saving: {ex.Message}"); }
                }
                else if (options.OutputMode == ZplOutputMode.SaveToFile)
                {
                    Console.WriteLine($"Output presumably saved to {options.OutputFilePath}");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Rendering Failed: {result.ErrorMessage}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
     }
    }
}