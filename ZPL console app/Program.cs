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

^FX Top section with logo, name and address.

^FO50,50^GB100,100,100^FS
^FO75,75^FR^GB100,100,100^FS
^FO93,93^GB40,40,40^FS
^FO220,50^FDIntershipping, Inc.^FS
^CF0,30
^FO220,115^FD1000 Shipping Lane^FS
^FO220,155^FDShelbyville TN 38102^FS
^FO220,195^FDUnited States (USA)^FS
^FO50,250^GB700,3,3^FS

^FX Second section with recipient address and permit information.
^CFA,30
^FO50,300^FDJohn Doe^FS
^FO50,340^FD100 Main Street^FS
^FO50,380^FDSpringfield TN 39021^FS
^FO50,420^FDUnited States (USA)^FS
^CFA,15
^FO600,300^GB150,150,3^FS
^FO638,340^FDPermit^FS
^FO638,390^FD123456^FS
^FO50,500^GB700,3,3^FS

^FX Third section with bar code.
^BY5,2,270
^FO100,550^BC^FD12345678^FS

^FX Fourth section (the two boxes on the bottom).
^FO50,900^GB700,250,3^FS
^FO400,900^GB3,250,3^FS
^CF0,40
^FO100,960^FDCtr. X34B-1^FS
^FO100,1010^FDREF1 F00B47^FS
^FO100,1060^FDREF2 BL4H8^FS
^CF0,190
^FO470,955^FDCA^FS

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