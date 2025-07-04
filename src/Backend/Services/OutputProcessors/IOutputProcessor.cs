using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services.OutputProcessors;

public interface IOutputProcessor
{
    string AppName { get; }
    bool CanHandle(string terminalId, string command, string output, Terminal? terminal = null);
    bool ShouldFlushBuffer(string lastOutput, string bufferContent);
    string ProcessOutput(string output);
    bool ShouldIgnoreLine(string line);
    int TimeoutMs { get; }
}

public abstract class BaseOutputProcessor : IOutputProcessor
{
    public abstract string AppName { get; }
    public abstract bool CanHandle(string terminalId, string command, string output, Terminal? terminal = null);
    public abstract bool ShouldFlushBuffer(string lastOutput, string bufferContent);
    public abstract string ProcessOutput(string output);
    public virtual int TimeoutMs => 750;
    
    public virtual bool ShouldIgnoreLine(string line)
    {
        // Common ignore patterns
        return string.IsNullOrWhiteSpace(line) ||
               line.Contains("╭") || line.Contains("╮") || line.Contains("╰") || line.Contains("╯") ||
               line.Trim() == "│" || line.Contains("│    │");
    }
    
    protected string CleanLine(string line)
    {
        return line.Replace("│", "").Replace("╭", "").Replace("╮", "").Replace("╰", "").Replace("╯", "").Trim();
    }
}