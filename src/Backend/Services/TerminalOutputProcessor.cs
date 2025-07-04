using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Services.OutputProcessors;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services;

public class TerminalOutputProcessor
{
    private readonly ILogger<TerminalOutputProcessor> _logger;
    private readonly Dictionary<string, OutputBuffer> _buffers = new();
    private readonly Dictionary<string, IOutputProcessor> _terminalProcessors = new();
    private readonly List<IOutputProcessor> _processors;
    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _terminalResponseInProgress = new();
    private readonly Dictionary<string, List<string>> _terminalCommandHistory = new();
    
    // Regex patterns for filtering
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[^@-~]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex TerminalControlRegex = new(@"\[\?[0-9]+[hl]", RegexOptions.Compiled);
    
    public TerminalOutputProcessor(ILogger<TerminalOutputProcessor> logger)
    {
        _logger = logger;
        
        // Initialize processors in order of precedence
        _processors = new List<IOutputProcessor>
        {
            new ClaudeOutputProcessor(),
            new StandardOutputProcessor() // Always last as fallback
        };
    }
    
    public class OutputBuffer
    {
        private StringBuilder? _buffer;
        public StringBuilder Buffer => _buffer ??= new StringBuilder(1024); // Pre-allocate reasonable size
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
        public bool IsCommandExecuting { get; set; }
        public string? LastCommand { get; set; }
        
        public void Clear()
        {
            _buffer?.Clear();
        }
        
        public void Dispose()
        {
            _buffer?.Clear();
            _buffer = null;
        }
    }
    
    public void StartCommand(string terminalId, string command)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(terminalId, out var buffer))
            {
                buffer = new OutputBuffer();
                _buffers[terminalId] = buffer;
            }
            
            buffer.IsCommandExecuting = true;
            buffer.LastCommand = command;
            buffer.Clear();
            buffer.LastUpdate = DateTime.UtcNow;
            
            // Track command history
            if (!_terminalCommandHistory.TryGetValue(terminalId, out var history))
            {
                history = new List<string>();
                _terminalCommandHistory[terminalId] = history;
            }
            history.Add(command);
            
            _logger.LogInformation("Started command '{Command}' on terminal {TerminalId}", command, terminalId);
        }
    }
    
    public ProcessedOutput? ProcessOutput(string terminalId, string rawOutput)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(terminalId, out var buffer))
            {
                buffer = new OutputBuffer();
                _buffers[terminalId] = buffer;
            }
            
            // Get the appropriate processor for this terminal
            var processor = GetProcessor(terminalId, buffer.LastCommand ?? "", rawOutput);
            
            // Clean the output
            var cleanedOutput = CleanOutput(rawOutput, processor);
            
            // Skip empty or whitespace-only output
            if (string.IsNullOrWhiteSpace(cleanedOutput))
            {
                return null;
            }
            
            // Add to buffer
            buffer.Buffer.Append(cleanedOutput);
            buffer.LastUpdate = DateTime.UtcNow;
            
            // Check if we should flush the buffer using the processor
            var shouldFlush = processor.ShouldFlushBuffer(cleanedOutput, buffer.Buffer.ToString());
            _logger.LogDebug("Terminal {TerminalId} - Processor: {Processor}, ShouldFlush: {ShouldFlush}, BufferLength: {Length}", 
                terminalId, processor.AppName, shouldFlush, buffer.Buffer.Length);
                
            if (shouldFlush)
            {
                var result = FlushBuffer(terminalId, buffer, processor);
                buffer.IsCommandExecuting = false;
                _logger.LogInformation("Flushed buffer for terminal {TerminalId}, Content length: {Length}", 
                    terminalId, result?.Content?.Length ?? 0);
                return result;
            }
            
            return null;
        }
    }
    
    public ProcessedOutput? CheckTimeout(string terminalId, TimeSpan timeout)
    {
        lock (_lock)
        {
            if (_buffers.TryGetValue(terminalId, out var buffer) && 
                buffer.IsCommandExecuting &&
                buffer.Buffer.Length > 0)
            {
                var processor = GetProcessor(terminalId, buffer.LastCommand ?? "", buffer.Buffer.ToString());
                var processorTimeout = TimeSpan.FromMilliseconds(processor.TimeoutMs);
                
                if (DateTime.UtcNow - buffer.LastUpdate > timeout)
                {
                    var result = FlushBuffer(terminalId, buffer, processor);
                    buffer.IsCommandExecuting = false;
                    return result;
                }
            }
            
            return null;
        }
    }
    
    private IOutputProcessor GetProcessor(string terminalId, string command, string output)
    {
        // For Claude patterns, always force re-detection to ensure we use Claude processor
        var looksLikeClaudeOutput = output.Contains("✻ Welcome to Claude Code!") ||
                                   output.Contains("? for shortcuts") ||
                                   output.Contains("esc to interrupt") ||
                                   output.Contains("Claude Opus") ||
                                   output.Contains("Claude Sonnet") ||
                                   output.Contains("●");
        
        if (looksLikeClaudeOutput)
        {
            _terminalProcessors.Remove(terminalId); // Force re-detection
        }
        
        // Check if we already have a processor for this terminal
        if (_terminalProcessors.TryGetValue(terminalId, out var cachedProcessor))
        {
            return cachedProcessor;
        }
        
        // Create terminal object with command history for detection
        Terminal? terminal = null;
        if (_terminalCommandHistory.TryGetValue(terminalId, out var history))
        {
            terminal = new Terminal
            {
                Id = terminalId,
                CommandHistory = history
            };
        }
        
        // Find the appropriate processor - Claude processor has priority
        foreach (var processor in _processors)
        {
            if (processor.CanHandle(terminalId, command, output, terminal))
            {
                _terminalProcessors[terminalId] = processor;
                _logger.LogInformation("Selected processor '{ProcessorName}' for terminal {TerminalId}", 
                    processor.AppName, terminalId);
                return processor;
            }
        }
        
        // Fallback to standard processor
        var standardProcessor = _processors.Last();
        _terminalProcessors[terminalId] = standardProcessor;
        _logger.LogInformation("Using fallback processor '{ProcessorName}' for terminal {TerminalId}", 
            standardProcessor.AppName, terminalId);
        return standardProcessor;
    }
    
    private string CleanOutput(string output, IOutputProcessor processor)
    {
        // Remove ANSI escape sequences
        output = AnsiEscapeRegex.Replace(output, "");
        
        // Remove terminal control sequences
        output = TerminalControlRegex.Replace(output, "");
        
        // Remove special terminal sequences
        output = output.Replace("\x1B[?2004l", "")
                      .Replace("\x1B[?2004h", "")
                      .Replace("[?2004l", "")
                      .Replace("[?2004h", "")
                      .Replace("\x1B]", "")
                      .Replace("\x07", "")
                      .Replace("\x1B", "");
        
        // Remove formatting artifacts
        output = output.Replace("▌", "")
                      .Replace("[0m", "")
                      .Replace("[1m", "")
                      .Replace("[32m", "")
                      .Replace("[33m", "")
                      .Replace("[34m", "")
                      .Replace("[35m", "")
                      .Replace("[36m", "");
        
        // Extract meaningful content from the output
        var outputLines = output.Split('\n');
        var cleanedLines = new List<string>();
        
        foreach (var line in outputLines)
        {
            var trimmedLine = line.TrimEnd('\r', ' ');
            
            // Skip empty lines, prompts, and other unwanted content
            if (!string.IsNullOrEmpty(trimmedLine) && 
                !IsPromptLine(trimmedLine) &&
                !IsEchoedCommand(trimmedLine) &&
                !processor.ShouldIgnoreLine(trimmedLine))
            {
                // Clean up the line further
                var cleanLine = CleanLine(trimmedLine);
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    cleanedLines.Add(cleanLine);
                }
            }
        }
        
        return string.Join("\n", cleanedLines);
    }
    
    public void OnClaudeResponseStart(string terminalId)
    {
        lock (_lock)
        {
            _terminalResponseInProgress[terminalId] = true;
            _logger.LogDebug("Claude response started for terminal {TerminalId}", terminalId);
        }
    }
    
    public void OnClaudeResponseComplete(string terminalId)
    {
        lock (_lock)
        {
            _terminalResponseInProgress[terminalId] = false;
            _logger.LogDebug("Claude response completed for terminal {TerminalId}", terminalId);
        }
    }
    
    private bool IsClaudeResponseInProgress(string terminalId)
    {
        return _terminalResponseInProgress.GetValueOrDefault(terminalId, false);
    }
    
    private string CleanLine(string line)
    {
        // Remove box drawing characters and other artifacts
        return line.Replace("│", "").Replace("╭", "").Replace("╮", "").Replace("╰", "").Replace("╯", "").Trim();
    }
    
    private bool IsPromptLine(string line)
    {
        // Check if it's just a prompt (ends with $ or # with optional whitespace)
        return (line.Trim().EndsWith("$") || line.Trim().EndsWith("#")) && line.Length < 100;
    }
    
    private bool IsEchoedCommand(string line)
    {
        // Skip lines that are just the echoed command
        if (_buffers.Values.Any(b => b.LastCommand != null && line.Trim() == b.LastCommand.Trim()))
        {
            return true;
        }
        return false;
    }
    
    
    private ProcessedOutput? FlushBuffer(string terminalId, OutputBuffer buffer, IOutputProcessor processor)
    {
        var content = buffer.Buffer.ToString().Trim();
        buffer.Clear();
        
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        
        // Remove duplicate newlines
        content = Regex.Replace(content, @"\n{3,}", "\n\n");
        
        // Process the output using the appropriate processor
        content = processor.ProcessOutput(content);
        
        return new ProcessedOutput
        {
            TerminalId = terminalId,
            Content = content,
            Command = buffer.LastCommand
        };
    }
    
    public void CleanupTerminal(string terminalId)
    {
        lock (_lock)
        {
            // Dispose buffer resources before removing
            if (_buffers.TryGetValue(terminalId, out var buffer))
            {
                buffer.Dispose();
            }
            
            // Remove buffers and processors for this terminal
            _buffers.Remove(terminalId);
            _terminalProcessors.Remove(terminalId);
            _terminalResponseInProgress.Remove(terminalId);
            _terminalCommandHistory.Remove(terminalId);
            
            // Clean up Claude processor state
            foreach (var processor in _processors)
            {
                if (processor is ClaudeOutputProcessor claudeProcessor)
                {
                    claudeProcessor.RemoveTerminal(terminalId);
                }
            }
            
            _logger.LogInformation("Cleaned up terminal {TerminalId} from output processor", terminalId);
        }
    }
    
}

public class ProcessedOutput
{
    public string TerminalId { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Command { get; set; }
}