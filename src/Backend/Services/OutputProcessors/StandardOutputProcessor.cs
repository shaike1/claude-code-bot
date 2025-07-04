using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services.OutputProcessors;

public class StandardOutputProcessor : BaseOutputProcessor
{
    public override string AppName => "Standard Terminal";
    
    public override bool CanHandle(string terminalId, string command, string output, Terminal? terminal = null)
    {
        // This is the fallback processor for standard terminals
        return true;
    }
    
    public override bool ShouldFlushBuffer(string lastOutput, string bufferContent)
    {
        var trimmedOutput = lastOutput.TrimEnd();
        
        // Flush if we see a prompt at the end
        if (trimmedOutput.EndsWith("$") || trimmedOutput.EndsWith("#"))
        {
            return true;
        }
        
        // Flush if we see common end patterns
        return lastOutput.Contains("not found") || 
               lastOutput.Contains("command not found") ||
               lastOutput.Contains("installed with:") ||
               lastOutput.Contains("Usage:") ||
               lastOutput.Contains("No such file") ||
               lastOutput.Contains("Permission denied") ||
               lastOutput.EndsWith("done") ||
               lastOutput.EndsWith("complete") ||
               trimmedOutput.EndsWith(".") ||
               trimmedOutput.EndsWith("!") ||
               trimmedOutput.EndsWith("?");
    }
    
    public override string ProcessOutput(string output)
    {
        // For standard terminals, just clean up the output
        var lines = output.Split('\n');
        var cleanedLines = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r', ' ');
            if (!string.IsNullOrWhiteSpace(trimmed) && !IsPromptLine(trimmed))
            {
                cleanedLines.Add(trimmed);
            }
        }
        
        return string.Join("\n", cleanedLines).Trim();
    }
    
    private bool IsPromptLine(string line)
    {
        return line.Trim().EndsWith("$") || line.Trim().EndsWith("#");
    }
}