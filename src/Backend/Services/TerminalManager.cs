using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services;

public class TerminalManager : ITerminalManager
{
    private readonly ILogger<TerminalManager> _logger;
    private readonly TerminalSettings _settings;
    private readonly InteractiveAppSettings _interactiveAppSettings;
    private readonly ConcurrentDictionary<string, Terminal> _terminals = new();
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly Random _random = new();
    private readonly TerminalOutputProcessor _outputProcessor;
    private volatile bool _isShuttingDown = false;

    public event EventHandler<TerminalMessage>? TerminalMessageReceived;

    public TerminalManager(ILogger<TerminalManager> logger, IOptions<TerminalSettings> settings, IOptions<InteractiveAppSettings> interactiveAppSettings, TerminalOutputProcessor outputProcessor)
    {
        _logger = logger;
        _settings = settings.Value;
        _interactiveAppSettings = interactiveAppSettings.Value;
        _outputProcessor = outputProcessor;
    }

    public async Task<Terminal> CreateTerminalAsync(string? customId = null)
    {
        if (_terminals.Count >= _settings.MaxTerminals)
        {
            throw new InvalidOperationException($"Maximum number of terminals ({_settings.MaxTerminals}) reached");
        }

        var terminalId = customId ?? GenerateTerminalId();
        
        if (_terminals.ContainsKey(terminalId))
        {
            throw new ArgumentException($"Terminal with ID {terminalId} already exists");
        }

        ProcessStartInfo processInfo;
        
        // Detect OS and create appropriate terminal
        if (OperatingSystem.IsWindows())
        {
            if (_settings.ShowTerminalWindow)
            {
                // For debugging: create a visible console window
                processInfo = new ProcessStartInfo
                {
                    FileName = "conhost.exe",
                    Arguments = "wsl.exe bash",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };
                
                _logger.LogInformation("Creating terminal with visible window using conhost");
            }
            else
            {
                // When hiding window, use WSL directly
                processInfo = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "bash",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };
            }
        }
        else
        {
            // On Linux/Mac, create a bash terminal
            processInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-i",  // Interactive mode
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = !_settings.ShowTerminalWindow,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        var process = new Process { StartInfo = processInfo };
        
        var terminal = new Terminal
        {
            Id = terminalId,
            ProcessId = -1,
            StartTime = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            Status = TerminalStatus.Running
        };

        try
        {
            process.Start();
            terminal.ProcessId = process.Id;

            _terminals[terminalId] = terminal;
            _processes[terminalId] = process;

            _logger.LogInformation("Started process for terminal {TerminalId}: PID={ProcessId}, FileName={FileName}, Args={Arguments}", 
                terminalId, process.Id, process.StartInfo.FileName, process.StartInfo.Arguments);

            _ = Task.Run(() => MonitorTerminalOutput(terminalId, process));
            _ = Task.Run(() => MonitorOutputTimeout(terminalId));

            await Task.Delay(500);

            // Don't send initial test command - let the terminal start cleanly

            TerminalMessageReceived?.Invoke(this, new TerminalMessage
            {
                TerminalId = terminalId,
                Type = MessageType.Started,
                Content = $"Terminal {terminalId} started successfully (PID: {process.Id})"
            });

            _logger.LogInformation("Created terminal with ID: {TerminalId}", terminalId);
            return terminal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create terminal");
            process.Dispose();
            throw;
        }
    }

    public async Task<bool> ExecuteCommandAsync(string terminalId, string command)
    {
        if (!_processes.TryGetValue(terminalId, out var process) || process.HasExited)
        {
            _logger.LogWarning("Terminal {TerminalId} not found or has exited", terminalId);
            return false;
        }

        if (!_terminals.TryGetValue(terminalId, out var terminal))
        {
            return false;
        }

        try
        {
            // Handle special terminal commands
            if (command.StartsWith("!"))
            {
                switch (command.ToLower())
                {
                    case "!enter":
                        await process.StandardInput.WriteAsync('\n');
                        await process.StandardInput.FlushAsync();
                        _logger.LogInformation("Sent Enter key to terminal {TerminalId}", terminalId);
                        return true;
                        
                    case "!ctrlc":
                        await process.StandardInput.WriteAsync('\x03');
                        await process.StandardInput.FlushAsync();
                        _logger.LogInformation("Sent Ctrl+C to terminal {TerminalId}", terminalId);
                        return true;
                        
                    case "!ctrld":
                        await process.StandardInput.WriteAsync('\x04');
                        await process.StandardInput.FlushAsync();
                        _logger.LogInformation("Sent Ctrl+D to terminal {TerminalId}", terminalId);
                        return true;
                        
                    case "!tab":
                        await process.StandardInput.WriteAsync('\t');
                        await process.StandardInput.FlushAsync();
                        _logger.LogInformation("Sent Tab to terminal {TerminalId}", terminalId);
                        return true;
                        
                    case "!esc":
                        await process.StandardInput.WriteAsync('\x1B');
                        await process.StandardInput.FlushAsync();
                        _logger.LogInformation("Sent Escape to terminal {TerminalId}", terminalId);
                        return true;
                }
            }
            
            // Notify output processor that a command is starting
            _outputProcessor.StartCommand(terminalId, command);
            
            // Detect interactive app and get configuration
            var appConfig = DetectInteractiveApp(terminal);
            
            if (appConfig != null)
            {
                await ExecuteWithAppConfig(process, command, appConfig);
            }
            else
            {
                // Standard method for regular commands
                await process.StandardInput.WriteAsync(command);
                await process.StandardInput.WriteAsync('\n');
                await process.StandardInput.FlushAsync();
            }
            
            terminal.LastActivity = DateTime.UtcNow;
            terminal.Status = TerminalStatus.Running;
            terminal.CommandHistory.Add(command);
            
            _logger.LogInformation("Executed command on terminal {TerminalId}: {Command}", terminalId, command);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute command on terminal {TerminalId}", terminalId);
            return false;
        }
    }

    public async Task<bool> KillTerminalAsync(string terminalId)
    {
        if (!_processes.TryGetValue(terminalId, out var process))
        {
            return false;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    // Send Ctrl+C
                    await process.StandardInput.WriteAsync('\x03');
                    await process.StandardInput.FlushAsync();
                    await Task.Delay(500);
                }
                catch
                {
                    // Ignore errors during shutdown
                }

                if (!process.HasExited)
                {
                    _logger.LogInformation("Force killing terminal {TerminalId} process", terminalId);
                    process.Kill();
                    await Task.Delay(100); // Give it time to exit
                }
            }
            else
            {
                _logger.LogWarning("Terminal {TerminalId} process already exited", terminalId);
            }

            _processes.TryRemove(terminalId, out _);
            _terminals.TryRemove(terminalId, out _);

            // Clean up output processor state
            _outputProcessor.CleanupTerminal(terminalId);

            // Dispose the process
            try
            {
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing process for terminal {TerminalId}", terminalId);
            }

            // Only send messages if not shutting down
            if (!_isShuttingDown)
            {
                TerminalMessageReceived?.Invoke(this, new TerminalMessage
                {
                    TerminalId = terminalId,
                    Type = MessageType.Completed,
                    Content = $"Terminal {terminalId} terminated"
                });
            }

            _logger.LogInformation("Killed terminal {TerminalId}", terminalId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill terminal {TerminalId}", terminalId);
            return false;
        }
    }

    public void SetShuttingDown(bool isShuttingDown)
    {
        _isShuttingDown = isShuttingDown;
    }

    public IEnumerable<Terminal> GetActiveTerminals()
    {
        return _terminals.Values.Where(t => t.IsActive);
    }

    public Task<bool> RenameTerminalAsync(string oldId, string newId)
    {
        if (!_terminals.TryRemove(oldId, out var terminal) || 
            !_processes.TryRemove(oldId, out var process))
        {
            return Task.FromResult(false);
        }

        if (_terminals.ContainsKey(newId))
        {
            _terminals[oldId] = terminal;
            _processes[oldId] = process;
            return Task.FromResult(false);
        }

        terminal.Id = newId;
        _terminals[newId] = terminal;
        _processes[newId] = process;

        _logger.LogInformation("Renamed terminal from {OldId} to {NewId}", oldId, newId);
        return Task.FromResult(true);
    }

    public Task<List<Terminal>> ListTerminalsAsync()
    {
        var terminals = _terminals.Values.ToList();
        return Task.FromResult(terminals);
    }

    public Task<Terminal?> GetTerminalAsync(string terminalId)
    {
        _terminals.TryGetValue(terminalId, out var terminal);
        return Task.FromResult(terminal);
    }

    public async Task<bool> SendChoiceAsync(string terminalId, int choice)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal) || 
            terminal.Status != TerminalStatus.WaitingForInput)
        {
            return false;
        }

        return await ExecuteCommandAsync(terminalId, choice.ToString());
    }

    private string GenerateTerminalId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var result = new char[3];
        
        for (int i = 0; i < 3; i++)
        {
            result[i] = chars[_random.Next(chars.Length)];
        }
        
        return new string(result);
    }
    
    private InteractiveAppConfig? DetectInteractiveApp(Terminal terminal)
    {
        // Check command history and current task for app detection keywords
        var allText = string.Join(" ", terminal.CommandHistory);
        if (!string.IsNullOrEmpty(terminal.CurrentTask))
        {
            allText += " " + terminal.CurrentTask;
        }
        
        var lowerText = allText.ToLower();
        
        foreach (var app in _interactiveAppSettings.Apps)
        {
            foreach (var keyword in app.DetectionKeywords)
            {
                if (lowerText.Contains(keyword.ToLower()))
                {
                    return app;
                }
            }
        }
        
        return null;
    }
    
    private async Task ExecuteWithAppConfig(Process process, string command, InteractiveAppConfig config)
    {
        switch (config.InputMethod)
        {
            case InputMethod.CharacterByCharacter:
                // Add small initial delay to ensure terminal is ready
                await Task.Delay(50);
                
                // Type character by character
                foreach (char c in command)
                {
                    await process.StandardInput.WriteAsync(c);
                    await process.StandardInput.FlushAsync();
                    
                    // Use configured delay or minimum of 20ms for stability
                    var delay = Math.Max(config.CharacterDelay, 20);
                    await Task.Delay(delay);
                }
                
                // Small delay before Enter to ensure all characters are processed
                await Task.Delay(50);
                
                // Send Enter to execute
                await process.StandardInput.WriteAsync('\r');
                await process.StandardInput.FlushAsync();
                
                // Handle pagination if configured
                if (config.OutputProcessing.AutoHandlePagination && 
                    !string.IsNullOrEmpty(config.OutputProcessing.PaginationPrompt))
                {
                    _ = Task.Run(async () => await HandlePagination(process, config.OutputProcessing));
                }
                break;
                
            case InputMethod.WithDelay:
                await process.StandardInput.WriteAsync(command);
                await Task.Delay(config.CharacterDelay);
                await process.StandardInput.WriteAsync('\n');
                await process.StandardInput.FlushAsync();
                break;
                
            case InputMethod.Standard:
            default:
                await process.StandardInput.WriteAsync(command);
                await process.StandardInput.WriteAsync('\n');
                await process.StandardInput.FlushAsync();
                break;
        }
    }
    
    private async Task HandlePagination(Process process, OutputProcessing config)
    {
        for (int i = 0; i < config.MaxPaginationAttempts; i++)
        {
            await Task.Delay(config.PaginationDelayMs);
            try
            {
                await process.StandardInput.WriteAsync('\r');
                await process.StandardInput.FlushAsync();
            }
            catch
            {
                // Process might be closed, ignore
                break;
            }
        }
    }
    

    private async void MonitorTerminalOutput(string terminalId, Process process)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var lineBuffer = new StringBuilder();

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardOutput;
                var buffer = new char[1024];
                while (!process.HasExited)
                {
                    try
                    {
                        var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            var text = new string(buffer, 0, read);
                            _logger.LogDebug("Terminal {TerminalId} stdout: {Output}", terminalId, text);
                            ProcessOutput(terminalId, text);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading stdout for terminal {TerminalId}", terminalId);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in stdout reader for terminal {TerminalId}", terminalId);
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardError;
                var buffer = new char[1024];
                while (!process.HasExited)
                {
                    try
                    {
                        var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            var text = new string(buffer, 0, read);
                            _logger.LogDebug("Terminal {TerminalId} stderr: {Error}", terminalId, text);
                            
                            // Filter out command echoes and non-error output
                            if (IsActualError(text, terminalId))
                            {
                                TerminalMessageReceived?.Invoke(this, new TerminalMessage
                                {
                                    TerminalId = terminalId,
                                    Type = MessageType.Error,
                                    Content = text
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading stderr for terminal {TerminalId}", terminalId);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in stderr reader for terminal {TerminalId}", terminalId);
            }
        });

        await process.WaitForExitAsync();

        if (_terminals.TryGetValue(terminalId, out var terminal))
        {
            terminal.Status = TerminalStatus.Terminated;
        }

        TerminalMessageReceived?.Invoke(this, new TerminalMessage
        {
            TerminalId = terminalId,
            Type = MessageType.Completed,
            Content = "Terminal process exited"
        });
    }

    private async void MonitorOutputTimeout(string terminalId)
    {
        while (_terminals.ContainsKey(terminalId) && _processes.ContainsKey(terminalId))
        {
            // Check if it's a Claude session for different timeout handling
            if (_terminals.TryGetValue(terminalId, out var terminal))
            {
                bool isClaudeSession = terminal.CommandHistory.Any(h => h.ToLower().Contains("claude"));
                
                if (isClaudeSession)
                {
                    // For Claude, use processor-specific timeout
                    await Task.Delay(2000); // Check every 2 seconds
                    var processedOutput = _outputProcessor.CheckTimeout(terminalId, TimeSpan.FromSeconds(15)); // 15 second timeout
                    if (processedOutput != null && !_isShuttingDown)
                    {
                        TerminalMessageReceived?.Invoke(this, new TerminalMessage
                        {
                            TerminalId = terminalId,
                            Type = MessageType.Output,
                            Content = processedOutput.Content
                        });
                    }
                }
                else
                {
                    // Standard timeout for regular terminals
                    await Task.Delay(300);
                    var processedOutput = _outputProcessor.CheckTimeout(terminalId, TimeSpan.FromMilliseconds(750));
                    if (processedOutput != null && !_isShuttingDown)
                    {
                        TerminalMessageReceived?.Invoke(this, new TerminalMessage
                        {
                            TerminalId = terminalId,
                            Type = MessageType.Output,
                            Content = processedOutput.Content
                        });
                    }
                }
            }
            else
            {
                // Terminal not found, break the loop
                break;
            }
        }
    }
    
    private void ProcessOutput(string terminalId, string output)
    {
        if (_terminals.TryGetValue(terminalId, out var terminal))
        {
            terminal.LastActivity = DateTime.UtcNow;

            // Check for interactive prompts
            if (output.Contains("Choose an option:") || 
                output.Contains("Select from:") ||
                (output.Contains("[1]") && output.Contains("[2]")))
            {
                terminal.Status = TerminalStatus.WaitingForInput;
                
                var lines = output.Split('\n');
                var options = new List<string>();
                
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("[") && line.Contains("]"))
                    {
                        options.Add(line.Trim());
                    }
                }

                if (options.Count > 0)
                {
                    TerminalMessageReceived?.Invoke(this, new TerminalMessage
                    {
                        TerminalId = terminalId,
                        Type = MessageType.Question,
                        Content = output,
                        Options = options
                    });

                    if (_settings.AutoSelectFirstOption)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            await SendChoiceAsync(terminalId, 1);
                        });
                    }
                    return;
                }
            }

            // Process output through the output processor
            var processedOutput = _outputProcessor.ProcessOutput(terminalId, output);
            if (processedOutput != null && !_isShuttingDown)
            {
                TerminalMessageReceived?.Invoke(this, new TerminalMessage
                {
                    TerminalId = terminalId,
                    Type = MessageType.Output,
                    Content = processedOutput.Content
                });
            }
            
            _logger.LogDebug("Terminal {TerminalId} raw output: {Output}", terminalId, output.TrimEnd());
        }
    }
    
    private bool IsActualError(string text, string terminalId)
    {
        // Clean the text for analysis
        var cleanText = text.Trim();
        
        // Ignore empty text
        if (string.IsNullOrWhiteSpace(cleanText))
            return false;
        
        // Get the last command sent to this terminal to check for command echoes
        var lastCommand = GetLastCommandForTerminal(terminalId);
        
        // Check if this is just a command echo (common in some terminal setups)
        if (!string.IsNullOrEmpty(lastCommand) && cleanText.Trim() == lastCommand.Trim())
        {
            _logger.LogDebug("Ignoring command echo on stderr for terminal {TerminalId}: {Text}", terminalId, cleanText);
            return false;
        }
        
        // Check for actual error patterns
        var lowerText = cleanText.ToLower();
        var isActualError = lowerText.Contains("error") ||
                           lowerText.Contains("failed") ||
                           lowerText.Contains("exception") ||
                           lowerText.Contains("not found") ||
                           lowerText.Contains("permission denied") ||
                           lowerText.Contains("no such file") ||
                           lowerText.Contains("command not found") ||
                           lowerText.Contains("syntax error") ||
                           lowerText.Contains("fatal:") ||
                           lowerText.Contains("panic:") ||
                           lowerText.Contains("traceback") ||
                           lowerText.StartsWith("usage:"); // Usage messages often indicate command errors
        
        // Ignore common non-error stderr output
        var isNonError = lowerText.Contains("warning") || // Warnings are not errors
                        lowerText.Contains("info") ||
                        lowerText.Contains("debug") ||
                        cleanText.Length < 3; // Very short output is usually not an error
        
        if (isActualError && !isNonError)
        {
            _logger.LogDebug("Detected actual error on stderr for terminal {TerminalId}: {Text}", terminalId, cleanText);
            return true;
        }
        
        _logger.LogDebug("Ignoring stderr output for terminal {TerminalId}: {Text}", terminalId, cleanText);
        return false;
    }
    
    private string? GetLastCommandForTerminal(string terminalId)
    {
        // This would ideally track the last command sent to each terminal
        // For now, we'll return null and rely on the other filtering logic
        // You could enhance this by storing the last command per terminal
        return null;
    }
}