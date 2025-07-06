using System.Text.RegularExpressions;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services.OutputProcessors;

/// <summary>
/// Clean, simple Claude Code output processor that waits for completion and extracts final responses
/// </summary>
public class ClaudeOutputProcessor : BaseOutputProcessor
{
    public override string AppName => "Claude Code";
    public override int TimeoutMs => 15000; // 15 seconds - enough time for Claude to respond
    
    private readonly string[] _detectionKeywords = { "claude", "Claude Code", "anthropic", "Welcome to Claude Code" };
    private readonly HashSet<string> _claudeTerminals = new();
    private readonly object _lock = new();
    
    public override bool CanHandle(string terminalId, string command, string output, Terminal? terminal = null)
    {
        lock (_lock)
        {
            // Once marked as Claude terminal, always use Claude processor
            if (_claudeTerminals.Contains(terminalId))
            {
                return true;
            }
            
            // Check for Claude patterns in current output
            var hasClaudePatterns = output.Contains("✻ Welcome to Claude Code!") ||
                                   output.Contains("? for shortcuts") ||
                                   output.Contains("Claude Opus") ||
                                   output.Contains("Claude Sonnet") ||
                                   output.Contains("esc to interrupt") ||
                                   output.Contains("Invalid API key") ||
                                   output.Contains("Please run /login") ||
                                   output.Contains("api key") ||
                                   output.Contains("authentication") ||
                                   output.Contains("/login") ||
                                   output.Contains("Login method") ||
                                   output.Contains("Web Login") ||
                                   output.Contains("console.anthropic.com") ||
                                   output.Contains("Choose login method") ||
                                   output.Contains("https://");
            
            // Check command and output for keywords
            var allText = $"{command} {output}".ToLower();
            var hasKeywords = _detectionKeywords.Any(keyword => allText.Contains(keyword.ToLower()));
            
            // Check command history
            var hasHistoryKeywords = false;
            if (terminal?.CommandHistory != null)
            {
                var historyText = string.Join(" ", terminal.CommandHistory).ToLower();
                hasHistoryKeywords = _detectionKeywords.Any(keyword => historyText.Contains(keyword.ToLower()));
            }
            
            if (hasClaudePatterns || hasKeywords || hasHistoryKeywords)
            {
                _claudeTerminals.Add(terminalId);
                return true;
            }
            
            return false;
        }
    }
    
    public override bool ShouldFlushBuffer(string lastOutput, string bufferContent)
    {
        // Flush if we have Claude startup content
        if (bufferContent.Contains("✻ Welcome to Claude Code"))
        {
            return true;
        }
        
        // Flush if we have bullet point responses
        if (bufferContent.Contains("●"))
        {
            return true;
        }
        
        // Flush authentication errors and prompts immediately
        if (bufferContent.Contains("Invalid API key") ||
            bufferContent.Contains("Please run /login") ||
            bufferContent.Contains("authentication") ||
            bufferContent.Contains("Login method") ||
            bufferContent.Contains("Web Login") ||
            bufferContent.Contains("API Key") ||
            bufferContent.Contains("https://") ||
            bufferContent.Contains("console.anthropic.com") ||
            bufferContent.Contains("Choose login method") ||
            bufferContent.Contains("Copy and paste") ||
            bufferContent.Contains("successfully authenticated"))
        {
            return true;
        }
        
        // Only flush on timeout for non-bullet content to avoid premature flushing
        // This allows the timeout mechanism to work properly
        // The timeout-based flush happens in TerminalOutputProcessor.CheckTimeout()
        
        return false;
    }
    
    private bool HasCompletedResponse(string bufferContent)
    {
        // Look for bullet point followed by clean prompt
        var lines = bufferContent.Split('\n');
        bool foundBulletResponse = false;
        bool foundCleanPrompt = false;
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Found bullet point with content
            if (line.StartsWith("●") && line.Length > 3)
            {
                foundBulletResponse = true;
            }
            
            // Found empty prompt box after bullet point
            if (foundBulletResponse && line.Contains("│ >") && line.Trim().EndsWith("│"))
            {
                foundCleanPrompt = true;
                break;
            }
        }
        
        return foundBulletResponse && foundCleanPrompt;
    }
    
    private bool HasProgressIndicators(string output)
    {
        return output.Contains("Actualizing") ||
               output.Contains("Conjuring") ||
               output.Contains("Envisioning") ||
               output.Contains("Mustering") ||
               output.Contains("Pontificating") ||
               output.Contains("Synthesizing") ||
               output.Contains("Cogitating") ||
               output.Contains("Wizarding") ||
               output.Contains("Greeting") ||
               output.Contains("Clauding") ||
               output.Contains("Simmering") ||
               output.Contains("Vibing");
    }
    
    public override string ProcessOutput(string content)
    {
        // Extract bullet point responses if available, otherwise return cleaned content
        var lines = content.Split('\n');
        var responses = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Extract text after bullet points - handle bullet points anywhere in the line
            var bulletIndex = trimmed.IndexOf("●");
            if (bulletIndex >= 0)
            {
                var afterBullet = trimmed.Substring(bulletIndex + 1).Trim();
                if (!string.IsNullOrWhiteSpace(afterBullet))
                {
                    responses.Add(afterBullet);
                }
            }
        }
        
        if (responses.Count > 0)
        {
            return string.Join("\n", responses);
        }
        
        // If no bullet points, return filtered content
        // Filter out obvious junk but keep the content
        // Note: Don't filter lines that contain bullet points, even if they have other junk
        var cleanLines = lines.Where(line => 
            !string.IsNullOrWhiteSpace(line.Trim()) &&
            !line.Contains("? for shortcuts") &&
            !line.Contains("esc to interrupt") &&
            !line.Contains("tokens") &&
            (!line.Contains("…") || line.Contains("●")) // Keep bullet points even if they have ellipsis
        ).ToList();
        
        return string.Join("\n", cleanLines);
    }
    
    public void RemoveTerminal(string terminalId)
    {
        lock (_lock)
        {
            _claudeTerminals.Remove(terminalId);
        }
    }
}