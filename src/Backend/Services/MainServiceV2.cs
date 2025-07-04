using System.Text;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Models;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Services;

public class MainServiceV2(
    ILogger<MainServiceV2> logger,
    ITerminalManager terminalManager,
    IClaudeHooksService claudeHooks,
    TerminalOutputProcessor outputProcessor,
    IMessageChannelManager channelManager,
    IOptions<TerminalSettings> terminalSettings) : BackgroundService
{
    private readonly TerminalSettings _terminalSettings = terminalSettings.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ClaudeMobileTerminal starting...");

        channelManager.CommandReceived += OnChannelCommandReceived;
        terminalManager.TerminalMessageReceived += OnTerminalMessageReceived;
        claudeHooks.HookEventReceived += OnClaudeHookEventReceived;

        await channelManager.StartAllChannelsAsync(stoppingToken);
        await claudeHooks.StartAsync(stoppingToken);

        logger.LogInformation("ClaudeMobileTerminal started successfully");
        
        PrintWelcomeMessage();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ClaudeMobileTerminal stopping...");

        // First, unsubscribe from events to prevent new messages
        channelManager.CommandReceived -= OnChannelCommandReceived;
        terminalManager.TerminalMessageReceived -= OnTerminalMessageReceived;
        claudeHooks.HookEventReceived -= OnClaudeHookEventReceived;

        // Kill all terminals before stopping channels
        try
        {
            // Set shutting down flag to suppress messages
            terminalManager.SetShuttingDown(true);
            
            var terminals = await terminalManager.ListTerminalsAsync();
            logger.LogInformation("Terminating {Count} active terminals", terminals.Count);
            
            // Kill terminals in parallel for faster shutdown
            var killTasks = new List<Task>();
            foreach (var terminal in terminals)
            {
                killTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await terminalManager.KillTerminalAsync(terminal.Id);
                        outputProcessor.CleanupTerminal(terminal.Id);
                        channelManager.CleanupTerminalSubscriptions(terminal.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error killing terminal {TerminalId}", terminal.Id);
                    }
                }));
            }
            
            // Wait for all terminals to be killed with a timeout
            try
            {
                await Task.WhenAll(killTasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Timeout while waiting for terminals to terminate");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during terminal cleanup");
        }

        // Now stop the channels and services
        await channelManager.StopAllChannelsAsync(cancellationToken);
        await claudeHooks.StopAsync(cancellationToken);

        await base.StopAsync(cancellationToken);
    }

    private async void OnChannelCommandReceived(object? sender, ChannelCommand command)
    {
        try
        {
            logger.LogInformation("Received command: {Command} from {ChannelType}:{SenderId}", 
                command.Command, command.ChannelType, command.SenderId);

            var lowerCommand = command.Command.ToLower();
            
            switch (lowerCommand)
            {
                case "list":
                    await HandleListCommand(command);
                    break;

                case "new":
                case "wsl":
                    await HandleNewTerminalCommand(command);
                    break;

                case "kill":
                    await HandleKillCommand(command);
                    break;

                case "rename":
                    await HandleRenameCommand(command);
                    break;

                case "settings":
                    await HandleSettingsCommand(command);
                    break;

                case "help":
                    await HandleHelpCommand(command);
                    break;

                default:
                    // Check if it's a button callback (format: terminalId_action)
                    if (command.Command.Contains("_"))
                    {
                        await HandleButtonCallback(command);
                        return;
                    }
                    
                    // Check if it might be a terminal ID (case-insensitive)
                    var terminals = await terminalManager.ListTerminalsAsync();
                    var terminal = terminals.FirstOrDefault(t => t.Id.Equals(command.Command, StringComparison.OrdinalIgnoreCase));
                    
                    if (terminal != null)
                    {
                        // Create new command with correct case
                        var correctedCommand = new ChannelCommand
                        {
                            ChannelType = command.ChannelType,
                            SenderId = command.SenderId,
                            Command = terminal.Id,
                            Arguments = command.Arguments,
                            RawText = command.RawText
                        };
                        await HandleTerminalCommand(correctedCommand);
                    }
                    else if (!command.Command.StartsWith("/") || 
                             command.Command.StartsWith("//") || 
                             command.Command.StartsWith("./"))
                    {
                        // Plain text command or escaped command - route to last active terminal
                        await HandlePlainTextCommand(command);
                    }
                    else
                    {
                        await SendResponse(command, 
                            $"Unknown command: {command.Command}. Type /help for available commands.");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling channel command");
        }
    }

    private async Task HandleListCommand(ChannelCommand command)
    {
        var terminals = await terminalManager.ListTerminalsAsync();
        
        if (terminals.Count == 0)
        {
            await SendResponse(command, "No active terminals");
            return;
        }

        var activeTerminalId = channelManager.GetLastActiveTerminal(command.ChannelType, command.SenderId);
        var sb = new StringBuilder("Active terminals:\n\n");
        
        // Create dynamic buttons for each terminal
        var terminalButtons = new Dictionary<string, string>();
        
        foreach (var terminal in terminals)
        {
            var isActive = terminal.Id == activeTerminalId;
            sb.AppendLine($"**{terminal.Id}** - Status: {terminal.Status}{(isActive ? " ✓ (active)" : "")}");
            sb.AppendLine($"  Started: {terminal.StartTime:HH:mm:ss}");
            sb.AppendLine($"  Last activity: {terminal.LastActivity:HH:mm:ss}");
            if (!string.IsNullOrEmpty(terminal.CurrentTask))
            {
                sb.AppendLine($"  Task: {terminal.CurrentTask}");
            }
            sb.AppendLine();
            
            // Add quick action buttons for each terminal
            terminalButtons[$"{terminal.Id}_select"] = $"📋 Select {terminal.Id}";
            terminalButtons[$"{terminal.Id}_claude"] = $"🤖 Claude on {terminal.Id}";
            terminalButtons[$"{terminal.Id}_pwd"] = $"📁 PWD {terminal.Id}";
            terminalButtons[$"{terminal.Id}_ls"] = $"📄 LS {terminal.Id}";
        }
        
        // Add general action buttons
        terminalButtons["new_terminal"] = "➕ New Terminal";
        terminalButtons["help_commands"] = "❓ Help";

        await SendResponse(command, sb.ToString(), terminalButtons);
    }

    private async Task HandleNewTerminalCommand(ChannelCommand command)
    {
        try
        {
            var customId = command.Arguments.Length > 0 ? command.Arguments[0] : null;
            var terminal = await terminalManager.CreateTerminalAsync(customId);
            
            // Subscribe this sender to terminal events
            channelManager.SubscribeToTerminal(terminal.Id, command.ChannelType, command.SenderId);
            
            // Set as active terminal
            channelManager.SetLastActiveTerminal(command.ChannelType, command.SenderId, terminal.Id);
            
            // Create helpful quick action buttons
            var quickActions = new Dictionary<string, string>
            {
                [$"{terminal.Id}_claude"] = "🤖 Start Claude Code",
                [$"{terminal.Id}_pwd"] = "📁 Show Current Directory",
                [$"{terminal.Id}_ls"] = "📋 List Files",
                [$"{terminal.Id}_help"] = "❓ Show Help"
            };
            
            await SendResponse(command, 
                $"Terminal **{terminal.Id}** created successfully and set as active\n" +
                $"Use `/{terminal.Id} [command]` to send commands\n" +
                $"Or just type commands directly to send to this terminal\n\n" +
                $"**Quick Actions:**", quickActions);
        }
        catch (Exception ex)
        {
            await SendResponse(command, $"Failed to create terminal: {ex.Message}");
        }
    }

    private async Task HandleKillCommand(ChannelCommand command)
    {
        if (command.Arguments.Length == 0)
        {
            await SendResponse(command, "Usage: /kill <terminal_id>");
            return;
        }

        var terminalId = command.Arguments[0];
        var success = await terminalManager.KillTerminalAsync(terminalId);
        
        if (success)
        {
            // Clean up associated resources
            outputProcessor.CleanupTerminal(terminalId);
            channelManager.CleanupTerminalSubscriptions(terminalId);
            
            await SendResponse(command, $"Terminal **{terminalId}** terminated");
        }
        else
        {
            await SendResponse(command, $"Failed to kill terminal **{terminalId}**");
        }
    }

    private async Task HandleRenameCommand(ChannelCommand command)
    {
        if (command.Arguments.Length < 2)
        {
            await SendResponse(command, "Usage: /rename <old_id> <new_id>");
            return;
        }

        var oldId = command.Arguments[0];
        var newId = command.Arguments[1];
        
        var success = await terminalManager.RenameTerminalAsync(oldId, newId);
        
        if (success)
        {
            await SendResponse(command, $"Terminal renamed from **{oldId}** to **{newId}**");
        }
        else
        {
            await SendResponse(command, $"Failed to rename terminal **{oldId}**");
        }
    }

    private async Task HandleTerminalCommand(ChannelCommand command)
    {
        var terminalId = command.Command;
        var terminal = await terminalManager.GetTerminalAsync(terminalId);
        
        if (terminal == null)
        {
            await SendResponse(command, $"Terminal **{terminalId}** not found");
            return;
        }

        // Set this as the last active terminal
        channelManager.SetLastActiveTerminal(command.ChannelType, command.SenderId, terminalId);

        var commandStartIndex = terminalId.Length + 1;
        if (command.RawText.StartsWith("/"))
        {
            commandStartIndex++;
        }

        if (command.RawText.Length <= commandStartIndex)
        {
            await SendResponse(command, $"Terminal **{terminalId}** - Status: {terminal.Status}");
            return;
        }

        var terminalCommand = command.RawText.Substring(commandStartIndex).Trim();
        
        // Handle escaped forward slashes for terminal commands
        if (terminalCommand.StartsWith("//"))
        {
            // Double slash: remove one slash and send to terminal
            terminalCommand = terminalCommand.Substring(1);
        }
        else if (terminalCommand.StartsWith("./"))
        {
            // Already a valid terminal command, send as-is
        }
        
        if (terminal.Status == TerminalStatus.WaitingForInput && int.TryParse(terminalCommand, out var choice))
        {
            await terminalManager.SendChoiceAsync(terminalId, choice);
            await SendResponse(command, $"Sent choice **{choice}** to terminal **{terminalId}**");
        }
        else
        {
            var success = await terminalManager.ExecuteCommandAsync(terminalId, terminalCommand);
            if (!success)
            {
                await SendResponse(command, $"Failed to send command to terminal **{terminalId}**");
            }
            // Don't send confirmation for successful commands - the output will show soon
        }
    }

    private async Task HandlePlainTextCommand(ChannelCommand command)
    {
        var lastTerminalId = channelManager.GetLastActiveTerminal(command.ChannelType, command.SenderId);
        
        if (string.IsNullOrEmpty(lastTerminalId))
        {
            await SendResponse(command, 
                "No active terminal. Use `/new` to create one or `/<id>` to select a terminal.");
            return;
        }

        var terminal = await terminalManager.GetTerminalAsync(lastTerminalId);
        if (terminal == null)
        {
            await SendResponse(command, 
                $"Last active terminal **{lastTerminalId}** not found. Use `/list` to see available terminals.");
            return;
        }

        // Execute the entire raw text as a command on the last active terminal
        var terminalCommand = command.RawText;
        
        // Handle escaped forward slashes for terminal commands
        if (terminalCommand.StartsWith("//"))
        {
            // Double slash: remove one slash and send to terminal
            terminalCommand = terminalCommand.Substring(1);
        }
        else if (terminalCommand.StartsWith("./"))
        {
            // Already a valid terminal command, send as-is
        }
        
        if (terminal.Status == TerminalStatus.WaitingForInput && int.TryParse(terminalCommand, out var choice))
        {
            await terminalManager.SendChoiceAsync(lastTerminalId, choice);
            await SendResponse(command, $"Sent choice **{choice}** to terminal **{lastTerminalId}**");
        }
        else
        {
            var success = await terminalManager.ExecuteCommandAsync(lastTerminalId, terminalCommand);
            if (!success)
            {
                await SendResponse(command, 
                    $"Failed to send command to terminal **{lastTerminalId}**");
            }
            // Don't send confirmation for successful commands - the output will show soon
        }
    }

    private async Task HandleSettingsCommand(ChannelCommand command)
    {
        var buttons = new Dictionary<string, string>
        {
            { "toggle_auto", _terminalSettings.AutoSelectFirstOption ? "Disable Auto-Select" : "Enable Auto-Select" }
        };

        var message = $"Current Settings:\n\n" +
                     $"Auto-select first option: **{(_terminalSettings.AutoSelectFirstOption ? "Enabled" : "Disabled")}**\n" +
                     $"Max terminals: **{_terminalSettings.MaxTerminals}**\n" +
                     $"Terminal timeout: **{_terminalSettings.TerminalTimeout}s**";

        await SendResponse(command, message, buttons);
    }

    private async Task HandleHelpCommand(ChannelCommand command)
    {
        var help = @"**ClaudeMobileTerminal Commands:**

/list - List all running terminals
/new [id] - Create new terminal (optional custom ID)
/wsl [id] - Alias for /new
/<id> [command] - Execute command in specific terminal
/<id> [number] - Send choice when prompted
/rename <old_id> <new_id> - Rename terminal
/kill <id> - Terminate terminal
/settings - View and modify settings
/help - Show this help message

**Plain Text Commands:**
After using a terminal, any text without / is sent to the last active terminal.

**Sending Commands Starting with /**
To send a command that starts with / to the terminal:
• Use double slash: `//usr/bin/ls` → `/usr/bin/ls`
• Use dot slash: `./myapp` → `./myapp` (unchanged)

Example:
  /new → Creates terminal 'A1a'
  ls → Runs 'ls' on A1a
  //usr/bin/env → Runs '/usr/bin/env' on A1a
  ./script.sh → Runs './script.sh' on A1a
  /new B2b → Creates terminal 'B2b'
  whoami → Runs 'whoami' on B2b
  /A1a //bin/bash → Runs '/bin/bash' on A1a";

        await SendResponse(command, help);
    }

    private async void OnTerminalMessageReceived(object? sender, TerminalMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.Started:
                case MessageType.Completed:
                    await channelManager.BroadcastToSubscribersAsync(message.TerminalId, 
                        $"[{message.TerminalId}] {message.Content}");
                    break;
                    
                case MessageType.Output:
                    // Send output to subscribers (limit length to avoid spam)
                    var content = message.Content.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        if (content.Length > 1000)
                        {
                            content = content.Substring(0, 1000) + "... (truncated)";
                        }
                        // Format as code block for terminal output
                        await channelManager.BroadcastToSubscribersAsync(message.TerminalId, 
                            $"[**{message.TerminalId}**]\n```\n{content}\n```");
                    }
                    break;
                    
                case MessageType.Question when message.Options != null:
                    var buttons = new Dictionary<string, string>();
                    for (int i = 0; i < message.Options.Count; i++)
                    {
                        buttons[$"{message.TerminalId}_{i + 1}"] = message.Options[i];
                    }

                    await channelManager.BroadcastToSubscribersAsync(message.TerminalId, 
                        $"[{message.TerminalId}] Choice required:\n{message.Content}", buttons);
                    break;
                    
                case MessageType.Status:
                    var terminal = await terminalManager.GetTerminalAsync(message.TerminalId);
                    if (terminal != null)
                    {
                        terminal.CurrentTask = message.Content;
                    }
                    break;
                    
                case MessageType.Error:
                    await channelManager.BroadcastToSubscribersAsync(message.TerminalId, 
                        $"[{message.TerminalId}] ❌ Error:\n{message.Content}");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling terminal message");
        }
    }

    private void OnClaudeHookEventReceived(object? sender, ClaudeHookEvent hookEvent)
    {
        try
        {
            logger.LogDebug("Received Claude hook: {EventType} for {TerminalId}", 
                hookEvent.EventType, hookEvent.TerminalId);

            // Simplified - Claude hooks disabled, using timeout-based approach instead
            logger.LogDebug("Claude hook event received but hooks are disabled in favor of timeout-based processing");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Claude hook event");
        }
    }

    private async Task HandleButtonCallback(ChannelCommand command)
    {
        // Handle special commands that don't follow terminalId_action pattern
        if (command.Command == "new_terminal")
        {
            await HandleNewTerminalCommand(command);
            return;
        }
        
        if (command.Command == "help_commands")
        {
            await HandleHelpCommand(command);
            return;
        }

        var parts = command.Command.Split('_', 2);
        if (parts.Length != 2)
        {
            await SendResponse(command, "Invalid button command format");
            return;
        }

        var terminalId = parts[0];
        var action = parts[1];

        // Verify terminal exists
        var terminal = await terminalManager.GetTerminalAsync(terminalId);
        if (terminal == null)
        {
            await SendResponse(command, $"Terminal **{terminalId}** not found");
            return;
        }

        // Set as active terminal
        channelManager.SetLastActiveTerminal(command.ChannelType, command.SenderId, terminalId);
        channelManager.SubscribeToTerminal(terminalId, command.ChannelType, command.SenderId);

        // Handle different actions
        switch (action)
        {
            case "select":
                await SendResponse(command, $"Terminal **{terminalId}** selected as active terminal");
                return;
                
            case "claude":
                var success = await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                if (!success)
                {
                    await SendResponse(command, $"Failed to start Claude Code in terminal **{terminalId}**");
                }
                return;
                
            case "pwd":
                await terminalManager.ExecuteCommandAsync(terminalId, "pwd");
                return;
                
            case "ls":
                await terminalManager.ExecuteCommandAsync(terminalId, "ls -la");
                return;
                
            case "help":
                await terminalManager.ExecuteCommandAsync(terminalId, "help");
                return;
                
            default:
                // Handle numbered choices for interactive menus
                if (int.TryParse(action, out var choice))
                {
                    await terminalManager.SendChoiceAsync(terminalId, choice);
                    await SendResponse(command, $"Sent choice **{choice}** to terminal **{terminalId}**");
                }
                else
                {
                    await SendResponse(command, $"Unknown action: {action}");
                }
                break;
        }
    }

    private async Task SendResponse(ChannelCommand command, string message, Dictionary<string, string>? buttons = null)
    {
        await channelManager.SendMessageAsync(command.ChannelType, command.SenderId, message, buttons);
    }

    private void PrintWelcomeMessage()
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║     ClaudeMobileTerminal v1.0.0        ║");
        Console.WriteLine("╠════════════════════════════════════════╣");
        Console.WriteLine("║  Control Claude Code terminals via     ║");
        Console.WriteLine("║  multiple communication channels       ║");
        Console.WriteLine("╚════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Active channels:");
        Console.WriteLine("  Check logs for channel status");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop...");
        Console.WriteLine();
    }
}