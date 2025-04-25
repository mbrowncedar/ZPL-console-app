using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace ZplRendererLib
{
    /// <summary>
    /// Represents a single parsed ZPL command.
    /// </summary>
    public class ZplCommand
    {
        public char Prefix { get; }
        public string CommandCode { get; }
        public string Parameters { get; }
        public string RawCommand { get; }

        public ZplCommand(string rawCommand)
        {
            RawCommand = rawCommand?.Trim() ?? string.Empty;

            if (RawCommand.Length >= 3 && (RawCommand.StartsWith("^") || RawCommand.StartsWith("~")))
            {
                Prefix = RawCommand[0];
                // Basic parsing: Assume 2-char command code, rest is parameters
                // More sophisticated parsing might be needed for some commands later
                CommandCode = RawCommand.Substring(1, 2).ToUpperInvariant();
                Parameters = RawCommand.Length > 3 ? RawCommand.Substring(3) : string.Empty;
            }
            else
            {
                // Not a valid-looking command format for parsing purposes here
                Prefix = '\0'; // Null char indicates invalid/unparsed
                CommandCode = string.Empty;
                Parameters = string.Empty;
            }
        }

        public override string ToString()
        {
            // Useful for debugging/logging
            return $"Cmd: {Prefix}{CommandCode}, Params: '{Parameters}'";
        }
    }
}