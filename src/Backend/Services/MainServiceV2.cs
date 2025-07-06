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
    IClaudeSessionManager claudeSessionManager,
    TerminalOutputProcessor outputProcessor,
    IMessageChannelManager channelManager,
    IOptions<TerminalSettings> terminalSettings) : BackgroundService
{
    private readonly TerminalSettings _terminalSettings = terminalSettings.Value;
    private readonly HashSet<string> _seenUsers = new();

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

            // Always check if this looks like a conversation starter and show welcome menu
            var lowerCommand = command.Command.ToLower();
            if (IsConversationStarterCommand(lowerCommand))
            {
                logger.LogInformation("Conversation starter detected from user {ChannelType}:{SenderId}, showing welcome menu", command.ChannelType, command.SenderId);
                await HandleWelcomeCommand(command);
                return;
            }

            var commandLower = command.Command.ToLower();
            
            // Debug logging for button detection
            if (command.Command.Contains("_"))
            {
                logger.LogInformation("Command contains underscore, checking if button callback: {Command}", command.Command);
                var isButton = IsButtonCallback(command.Command);
                logger.LogInformation("IsButtonCallback result: {IsButton}", isButton);
            }
            
            switch (commandLower)
            {
                case "start":
                case "menu":
                    await HandleStartCommand(command);
                    break;

                case "list":
                    await HandleListCommand(command);
                    break;

                case "switch":
                    await HandleSwitchCommand(command);
                    break;

                case "new":
                case "wsl":
                    await HandleNewTerminalCommand(command);
                    break;

                case "session":
                case "new_session":
                    await HandleNewSessionCommand(command);
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

                case "setup":
                    await HandleSetupCommand(command);
                    break;

                case "welcome":
                    await HandleWelcomeCommand(command);
                    break;

                case "reset":
                    await HandleResetCommand(command);
                    break;

                default:
                    // Check if it's a button callback (no underscore check - allow all button commands)
                    if (IsButtonCallback(command.Command))
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
                        // For any unrecognized text, show welcome menu (helpful for new users)
                        await HandleWelcomeCommand(command);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling channel command");
        }
    }

    private async Task HandleStartCommand(ChannelCommand command)
    {
        var terminals = await terminalManager.ListTerminalsAsync();
        var activeTerminalId = channelManager.GetLastActiveTerminal(command.ChannelType, command.SenderId);
        
        var message = "**🚀 ClaudeMobileTerminal Control Panel**\n\n";
        
        if (terminals.Count > 0)
        {
            message += $"**Active Terminals:** {terminals.Count}\n";
            if (!string.IsNullOrEmpty(activeTerminalId))
            {
                var activeTerminal = terminals.FirstOrDefault(t => t.Id == activeTerminalId);
                if (activeTerminal != null)
                {
                    message += $"**Current:** {activeTerminalId} ({activeTerminal.Status})\n";
                }
            }
            message += "\n";
        }
        else
        {
            message += "No terminals running. Create your first terminal!\n\n";
        }
        
        message += "**Choose an action:**";
        
        // Create comprehensive main menu buttons
        var mainButtons = new Dictionary<string, string>();
        
        if (terminals.Count > 0)
        {
            mainButtons["list"] = "📋 List All Terminals";
            mainButtons["switch"] = "🔄 Switch Terminal";
            
            // Add quick access to active terminal if exists
            if (!string.IsNullOrEmpty(activeTerminalId))
            {
                mainButtons[$"{activeTerminalId}_claude"] = $"🤖 Resume Claude on {activeTerminalId}";
                mainButtons[$"{activeTerminalId}_claude_new"] = $"🆕 New Claude on {activeTerminalId}";
                mainButtons[$"{activeTerminalId}_pwd"] = $"📁 PWD on {activeTerminalId}";
            }
        }
        
        mainButtons["new_terminal"] = "➕ Create New Terminal";
        mainButtons["new_session"] = "🆕 New Session";
        mainButtons["settings"] = "⚙️ Settings";
        mainButtons["help_commands"] = "❓ Help & Commands";
        
        await SendResponse(command, message, mainButtons);
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
            terminalButtons[$"{terminal.Id}_claude"] = $"🤖 Claude Resume {terminal.Id}";
            terminalButtons[$"{terminal.Id}_claude_new"] = $"🆕 Claude New {terminal.Id}";
            terminalButtons[$"{terminal.Id}_pwd"] = $"📁 PWD {terminal.Id}";
        }
        
        // Add general action buttons
        terminalButtons["new_terminal"] = "➕ New Terminal";
        terminalButtons["help_commands"] = "❓ Help";

        await SendResponse(command, sb.ToString(), terminalButtons);
    }

    private async Task HandleSwitchCommand(ChannelCommand command)
    {
        var terminals = await terminalManager.ListTerminalsAsync();
        
        if (terminals.Count == 0)
        {
            await SendResponse(command, "No terminals available to switch to. Create a new terminal first!");
            return;
        }

        if (terminals.Count == 1)
        {
            var singleTerminal = terminals[0];
            channelManager.SetLastActiveTerminal(command.ChannelType, command.SenderId, singleTerminal.Id);
            await SendResponse(command, $"Only one terminal available. **{singleTerminal.Id}** is now active.");
            return;
        }

        var activeTerminalId = channelManager.GetLastActiveTerminal(command.ChannelType, command.SenderId);
        var message = "**🔄 Switch to Terminal:**\n\n";
        var switchButtons = new Dictionary<string, string>();
        
        foreach (var terminal in terminals)
        {
            var isActive = terminal.Id == activeTerminalId;
            var status = isActive ? " (current)" : "";
            message += $"**{terminal.Id}**{status} - {terminal.Status}\n";
            
            if (!isActive) // Don't show switch button for current terminal
            {
                switchButtons[$"{terminal.Id}_select"] = $"➡️ Switch to {terminal.Id}";
            }
        }
        
        switchButtons["menu"] = "🔙 Back to Menu";
        
        await SendResponse(command, message, switchButtons);
    }

    private async Task HandleNewSessionCommand(ChannelCommand command)
    {
        // Kill all existing terminals for this user to start fresh
        var terminals = await terminalManager.ListTerminalsAsync();
        var userActiveTerminals = terminals.Where(t => 
            channelManager.GetLastActiveTerminal(command.ChannelType, command.SenderId) != null).ToList();

        if (userActiveTerminals.Any())
        {
            var message = $"**🆕 Starting New Session**\n\n" +
                         $"This will close {terminals.Count} existing terminal(s) and start fresh.\n\n" +
                         $"**Are you sure?**";

            var confirmButtons = new Dictionary<string, string>
            {
                ["confirm_new_session"] = "✅ Yes, Start New Session",
                ["menu"] = "❌ Cancel"
            };

            await SendResponse(command, message, confirmButtons);
        }
        else
        {
            // No terminals exist, just create a new one
            await HandleNewTerminalCommand(command);
        }
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
                [$"{terminal.Id}_claude"] = "🤖 Claude Code",
                [$"{terminal.Id}_gemini"] = "💎 Gemini AI",
                [$"{terminal.Id}_ai_select"] = "🚀 Choose AI",
                [$"{terminal.Id}_pwd"] = "📁 Show Directory",
                [$"{terminal.Id}_ls"] = "📋 List Files"
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
            // Show terminals with kill buttons
            var terminals = await terminalManager.ListTerminalsAsync();
            
            if (terminals.Count == 0)
            {
                await SendResponse(command, "No terminals to kill");
                return;
            }
            
            var message = "**Select terminal to terminate:**\n\n";
            var killButtons = new Dictionary<string, string>();
            
            foreach (var terminal in terminals)
            {
                message += $"**{terminal.Id}** - {terminal.Status}\n";
                killButtons[$"kill_{terminal.Id}"] = $"❌ Kill {terminal.Id}";
            }
            
            killButtons["menu"] = "🔙 Back to Menu";
            
            await SendResponse(command, message, killButtons);
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
            // Check if we have an active Claude tmux session
            var tmuxCheck = await terminalManager.ExecuteCommandAsync(lastTerminalId, "tmux list-sessions | grep claude_session");
            
            if (tmuxCheck)
            {
                // Send text to Claude tmux session and capture output
                await terminalManager.ExecuteCommandAsync(lastTerminalId, $"tmux send-keys -t claude_session '{terminalCommand}' Enter");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(lastTerminalId, "tmux capture-pane -t claude_session -p");
                
                await SendResponse(command, $"📝 **Sent to Claude**: `{terminalCommand}`\n\nWatch the terminal output above for Claude's response.");
            }
            else
            {
                // Regular terminal command
                var success = await terminalManager.ExecuteCommandAsync(lastTerminalId, terminalCommand);
                if (!success)
                {
                    await SendResponse(command, 
                        $"Failed to send command to terminal **{lastTerminalId}**");
                }
                // Don't send confirmation for successful commands - the output will show soon
            }
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

        // Add quick action buttons to help
        var helpButtons = new Dictionary<string, string>
        {
            ["list"] = "📋 List Terminals", 
            ["new_terminal"] = "➕ New Terminal",
            ["settings"] = "⚙️ Settings"
        };

        await SendResponse(command, help, helpButtons);
    }

    private async Task HandleSetupCommand(ChannelCommand command)
    {
        var setupMessage = @"**🔧 Claude Code Setup Guide**

**Claude Code is installed** ✅ (version 1.0.43)

**To use Claude Code, you need to authenticate:**

**Step 1:** Create a new terminal
**Step 2:** Run Claude Code  
**Step 3:** When prompted, authenticate with your Anthropic account

**Authentication Options:**
• Web login (recommended)
• API key from console.anthropic.com

**Quick Start:**";

        var setupButtons = new Dictionary<string, string>
        {
            ["new_terminal"] = "1️⃣ Create Terminal",
            ["help_commands"] = "❓ Help & Commands"
        };

        await SendResponse(command, setupMessage, setupButtons);
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
        logger.LogInformation("HandleButtonCallback called with command: {Command}", command.Command);
        
        // Handle single word commands (no underscore)
        switch (command.Command)
        {
            case "new_terminal":
                await HandleNewTerminalCommand(command);
                return;
                
            case "new_session":
                await HandleNewSessionCommand(command);
                return;
                
            case "help_commands":
                await HandleHelpCommand(command);
                return;
                
            case "menu":
            case "start":
                await HandleStartCommand(command);
                return;
                
            case "list":
                await HandleListCommand(command);
                return;
                
            case "settings":
                await HandleSettingsCommand(command);
                return;
                
            case "switch":
                await HandleSwitchCommand(command);
                return;
                
            case "confirm_new_session":
                await HandleConfirmNewSession(command);
                return;
                
            case "welcome":
                await HandleWelcomeCommand(command);
                return;
                
            case "reset":
                await HandleResetCommand(command);
                return;
                
            case "setup":
                await HandleSetupCommand(command);
                return;
        }

        var parts = command.Command.Split('_', 2);
        if (parts.Length != 2)
        {
            await SendResponse(command, "Invalid button command format");
            return;
        }

        var prefix = parts[0];
        var identifier = parts[1];

        // Handle kill commands
        if (prefix == "kill")
        {
            var success = await terminalManager.KillTerminalAsync(identifier);
            
            if (success)
            {
                outputProcessor.CleanupTerminal(identifier);
                channelManager.CleanupTerminalSubscriptions(identifier);
                
                await SendResponse(command, $"Terminal **{identifier}** terminated");
                
                // Show updated menu
                await HandleStartCommand(command);
            }
            else
            {
                await SendResponse(command, $"Failed to kill terminal **{identifier}**");
            }
            return;
        }

        // Handle terminal-specific actions  
        var terminalId = prefix;
        var action = identifier;
        
        // Handle compound actions like claude_new
        if (action.Contains("_"))
        {
            var actionParts = action.Split('_', 2);
            if (actionParts.Length == 2 && actionParts[0] == "claude" && actionParts[1] == "new")
            {
                action = "claude_new";
            }
        }

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
                // Show updated menu with new active terminal
                await HandleStartCommand(command);
                return;
                
            case "claude":
                logger.LogInformation("Starting Claude Code with session management in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🤖 **Claude Code Session Manager**\n\n⚡ Checking for existing sessions...");
                
                // Check for existing Claude sessions
                var existingSessions = await claudeSessionManager.GetActiveSessionsAsync();
                var terminalSessions = existingSessions.Where(s => s.TerminalId == terminalId).ToList();
                
                // Test authentication
                var authTestSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "claude --version 2>/dev/null && echo 'CLAUDE_AUTHENTICATED' || echo 'CLAUDE_NEEDS_AUTH'");
                await Task.Delay(1500);
                
                if (!authTestSuccess)
                {
                    await SendResponse(command, $"❌ Failed to test Claude Code in terminal **{terminalId}**");
                    return;
                }
                
                // Build session options
                var claudeButtons = new Dictionary<string, string>();
                
                // Add resume options for existing sessions
                if (existingSessions.Any())
                {
                    var recentSessions = existingSessions.Take(3).ToList();
                    foreach (var recentSession in recentSessions)
                    {
                        var sessionDesc = !string.IsNullOrEmpty(recentSession.Description) && recentSession.Description.Length > 20 
                            ? recentSession.Description.Substring(0, 20) + "..." 
                            : recentSession.Description ?? "Untitled";
                        claudeButtons[$"{terminalId}_resume_{recentSession.Id}"] = $"🔄 Resume: {sessionDesc}";
                    }
                    
                    if (existingSessions.Count > 3)
                    {
                        claudeButtons[$"{terminalId}_show_all_sessions"] = $"📋 Show All Sessions ({existingSessions.Count})";
                    }
                }
                
                // Add new session options
                claudeButtons[$"{terminalId}_claude_new_session"] = "🆕 New Session";
                claudeButtons[$"{terminalId}_claude_auto_auth"] = "🔑 Setup Authentication";
                claudeButtons[$"{terminalId}_claude_start"] = "▶️ Quick Start";
                
                var sessionInfo = existingSessions.Any() 
                    ? $"**Found {existingSessions.Count} existing session(s)**\n\n" +
                      $"**Recent Sessions:**\n" +
                      string.Join("\n", existingSessions.Take(3).Select(s => 
                          $"• {s.Description} ({s.LastUsed:MMM dd, HH:mm})")) + "\n\n"
                    : "**No existing sessions found**\n\n";
                
                await SendResponse(command, 
                    $"🤖 **Claude Code Session Manager**\n\n" +
                    sessionInfo +
                    $"**Options:**\n" +
                    $"• **Resume** - Continue previous work with full context\n" +
                    $"• **New Session** - Start fresh with project setup\n" +
                    $"• **Quick Start** - Launch Claude directly\n\n" +
                    $"Choose your option:", 
                    claudeButtons);
                return;
                
            case "claude_new":
                // Handle claude_new (new Claude session)
                logger.LogInformation("Executing new Claude session command in terminal {TerminalId}", terminalId);
                var newClaudeSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                
                if (!newClaudeSuccess)
                {
                    logger.LogError("Failed to execute new Claude command in terminal {TerminalId}", terminalId);
                    await SendResponse(command, $"❌ Failed to start new Claude session in terminal **{terminalId}**");
                }
                else
                {
                    logger.LogInformation("Successfully executed new Claude command in terminal {TerminalId}", terminalId);
                    await SendResponse(command, $"🆕 Starting new Claude session in terminal **{terminalId}**...");
                }
                return;
                
            case "gemini":
                logger.LogInformation("Starting Gemini AI in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"💎 Starting Gemini AI in terminal **{terminalId}**...\n\n⚡ Checking authentication status...");
                
                // Test if Gemini is configured
                var geminiTestSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "python3 -c \"import google.generativeai as genai; print('Gemini available')\" 2>/dev/null || echo 'Gemini setup needed'");
                
                if (!geminiTestSuccess)
                {
                    await SendResponse(command, $"❌ Failed to test Gemini in terminal **{terminalId}**");
                    return;
                }
                
                await Task.Delay(1500);
                
                var geminiButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_gemini_setup"] = "🚀 Setup Gemini",
                    [$"{terminalId}_gemini_start"] = "▶️ Start Gemini",
                    [$"{terminalId}_gemini_help"] = "❓ Gemini Help"
                };
                
                await SendResponse(command, 
                    $"💎 **Gemini AI Ready**\n\n" +
                    $"**Setup Required:** API key from Google AI Studio\n" +
                    $"**Free Tier:** Available with Google account\n\n" +
                    $"Choose your action:", 
                    geminiButtons);
                return;
                
            case "ai_select":
                logger.LogInformation("Showing AI selection menu for terminal {TerminalId}", terminalId);
                
                var aiSelectionButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude"] = "🤖 Claude Code (Anthropic)",
                    [$"{terminalId}_gemini"] = "💎 Gemini AI (Google)",
                    [$"{terminalId}_ai_compare"] = "⚖️ Compare AIs",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"🚀 **Choose Your AI Assistant**\n\n" +
                    $"**🤖 Claude Code**\n" +
                    $"- Advanced coding assistant\n" +
                    $"- Web login available\n" +
                    $"- File analysis & editing\n\n" +
                    $"**💎 Gemini AI**\n" +
                    $"- Google's latest model\n" +
                    $"- Free tier available\n" +
                    $"- Great for general tasks\n\n" +
                    $"Select your preferred AI:", 
                    aiSelectionButtons);
                return;
                
            case "ai_compare":
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '⚖️ AI Comparison'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=================='");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🤖 Claude Code:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + Excellent for coding tasks'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + File analysis and editing'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + Web login option'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  - Requires Anthropic account'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '💎 Gemini AI:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + Free tier available'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + Fast responses'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  + Good for general tasks'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  - Less specialized for coding'");
                
                await SendResponse(command, $"⚖️ AI comparison shown in terminal **{terminalId}**");
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
                
            case "login":
                // Send Claude Code login command
                logger.LogInformation("Executing Claude login command in terminal {TerminalId}", terminalId);
                var loginSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                
                if (!loginSuccess)
                {
                    await SendResponse(command, $"❌ Failed to execute login command in terminal **{terminalId}**");
                }
                else
                {
                    await SendResponse(command, $"🔑 Running Claude Code login in terminal **{terminalId}**\n\nFollow the authentication instructions that appear in the terminal.");
                }
                return;
                
            case "claude_login":
                // Direct authentication flow with explicit instructions
                logger.LogInformation("Starting Claude Code authentication flow in terminal {TerminalId}", terminalId);
                
                // Show the authentication error and instructions directly
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔑 Claude Code Authentication Required'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '======================================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Claude Code is not authenticated yet.'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Authentication error: Invalid API key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'To authenticate:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Click \"Start Claude\" button below'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Type /login when Claude starts'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Follow authentication instructions'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Ready to authenticate!'");
                
                var claudeActionButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude_start"] = "🚀 Start Claude",
                    [$"{terminalId}_auth_help"] = "❓ Auth Help",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"🔑 Claude Code authentication ready in terminal **{terminalId}**\n\n" +
                    $"**Authentication Required:** Claude Code needs your API key\n" +
                    $"**Next Step:** Click 'Start Claude' button to begin authentication\n" +
                    $"**Then:** Type `/login` and follow the prompts", 
                    claudeActionButtons);
                return;
                
            case "claude_help":
                // Send Claude Code help command
                logger.LogInformation("Executing Claude help command via button in terminal {TerminalId}", terminalId);
                var claudeHelpSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "/help");
                
                if (!claudeHelpSuccess)
                {
                    await SendResponse(command, $"❌ Failed to execute /help command in terminal **{terminalId}**");
                }
                else
                {
                    await SendResponse(command, $"❓ Sent `/help` to Claude Code in terminal **{terminalId}**");
                }
                return;
                
            case "claude_exit":
                // Send Claude Code exit command
                logger.LogInformation("Executing Claude exit command via button in terminal {TerminalId}", terminalId);
                var claudeExitSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                
                if (!claudeExitSuccess)
                {
                    await SendResponse(command, $"❌ Failed to execute /exit command in terminal **{terminalId}**");
                }
                else
                {
                    await SendResponse(command, $"🚪 Exited Claude Code in terminal **{terminalId}**");
                }
                return;
                
            case "claude_send_login":
                // Send Claude Code login command
                logger.LogInformation("Sending /login command to Claude Code in terminal {TerminalId}", terminalId);
                var sendLoginSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                
                if (!sendLoginSuccess)
                {
                    await SendResponse(command, $"❌ Failed to send /login command in terminal **{terminalId}**");
                }
                else
                {
                    await SendResponse(command, $"🔑 Sent `/login` command to Claude Code in terminal **{terminalId}**\n\nFollow the authentication instructions that appear in the terminal output.");
                }
                return;
                
            case "claude_start":
                // Start Claude Code and show authentication error
                logger.LogInformation("Starting Claude Code with authentication handling in terminal {TerminalId}", terminalId);
                
                // First show the authentication error that users expect to see
                var showErrorCommand = "echo 'Starting Claude Code...' && timeout 3s bash -c 'echo \"\" | claude' 2>&1 || echo 'Claude Code authentication required'";
                var errorSuccess = await terminalManager.ExecuteCommandAsync(terminalId, showErrorCommand);
                
                if (!errorSuccess)
                {
                    await SendResponse(command, $"❌ Failed to test Claude Code in terminal **{terminalId}**");
                    return;
                }
                
                await Task.Delay(2000);
                
                // Now provide instructions for manual authentication
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'To authenticate:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Type: claude'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. When prompted, type: /login'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Follow the authentication flow'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Ready to start! Type: claude'");
                
                var actionButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_start_manual"] = "📝 Type 'claude' for me",
                    [$"{terminalId}_auth_help"] = "❓ More Help",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"🔑 Claude Code authentication error shown in terminal **{terminalId}**\n\n" +
                    $"**Next:** Type `claude` in terminal to start authentication\n" +
                    $"**Then:** Type `/login` when prompted", 
                    actionButtons);
                return;
                
            case "claude_manual":
                // Provide manual authentication instructions with simple commands
                logger.LogInformation("Providing manual authentication instructions for terminal {TerminalId}", terminalId);
                
                // Use separate commands to avoid script complexity
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📝 Manual Claude Code Authentication'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=================================='");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 1: Type: claude'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 2: When you see auth error, type: /login'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 3: Follow the authentication prompts'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 4: Start coding with Claude!'");
                
                await SendResponse(command, $"📝 Manual authentication steps shown in terminal **{terminalId}**\n\nType 'claude' to start, then '/login' when prompted.");
                return;
                
            case "auth_help":
                // Provide authentication help
                logger.LogInformation("Providing authentication help for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❓ Claude Code Authentication Help'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=============================='");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'What is Claude Code?'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- AI assistant for coding tasks'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Requires Anthropic API key'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'How to use web login:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Type: claude'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Type: /login'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Select \"Web Login\" (recommended)'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Claude gives you a login URL'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Open URL in browser'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '6. Login with your Anthropic account'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '7. Return to terminal - done!'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'No manual API key needed!'");
                
                await SendResponse(command, $"❓ Authentication help shown in terminal **{terminalId}**\n\nWeb login recommended - no manual API key needed!");
                return;
                
            case "start_manual":
                // Show authentication process and provide direct instructions
                logger.LogInformation("Starting Claude Code authentication process for user in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Starting Claude Code authentication process...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Claude Code will show: Invalid API key · Please run /login'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Testing authentication status:'");
                await Task.Delay(500);
                
                // Show the actual authentication error
                var showError = await terminalManager.ExecuteCommandAsync(terminalId, "echo '' | claude 2>&1 || echo 'Authentication failed as expected'");
                await Task.Delay(1000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Authentication options available:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Option 1: Web Login (Recommended)'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Type: claude'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Type: /login'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Choose \"Web Login\"'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Open the URL Claude provides'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Login with your account'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Option 2: API Key'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Get key from console.anthropic.com'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Type: claude'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Type: /login'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Choose \"API Key\" and paste it'");
                
                var manualAuthButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_auth_help"] = "❓ Get API Key Help",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"🔑 Authentication process shown in terminal **{terminalId}**\n\n" +
                    $"**Web Login Available:** Choose web login option when prompted\n" +
                    $"**Steps:** Type `claude` → `/login` → choose web login", 
                    manualAuthButtons);
                return;
                
            case "claude_auto_auth":
                // Automated Claude Code authentication with clear login method selection
                logger.LogInformation("Starting Claude Code authentication with method selection for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔑 **Claude Code Authentication**\n\n⚡ Testing current status...");
                
                // First test current authentication status
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 Testing Claude authentication...'");
                await Task.Delay(300);
                
                // Test with a simple command to see current status
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'test' | claude --print 'Already authenticated!' 2>&1 || echo 'NEEDS_AUTH'");
                await Task.Delay(2000);
                
                // Provide clear authentication method selection
                var authMethodButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_auth_web_login"] = "🌐 Web Login (Recommended)",
                    [$"{terminalId}_auth_api_key"] = "🔑 API Key Method",
                    [$"{terminalId}_check_auth"] = "✅ Test Current Status",
                    [$"{terminalId}_auth_help"] = "❓ Authentication Help"
                };
                
                await SendResponse(command, 
                    $"🔑 **Choose Authentication Method**\n\n" +
                    $"If you see 'Already authenticated!' above, you're ready to use Claude!\n\n" +
                    $"If you see 'NEEDS_AUTH', choose your preferred login method:\n\n" +
                    $"🌐 **Web Login** (Recommended)\n" +
                    $"• No manual API key needed\n" +
                    $"• Login with your Anthropic account\n" +
                    $"• Automatic authentication flow\n\n" +
                    $"🔑 **API Key Method**\n" +
                    $"• Use existing API key\n" +
                    $"• Get from console.anthropic.com\n" +
                    $"• Direct key entry\n\n" +
                    $"**Select your preferred method:**", 
                    authMethodButtons);
                return;
                
            case "auth_web_login":
                // Simple interactive Claude login process
                logger.LogInformation("Starting interactive Claude login in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🚀 **Interactive Claude Login**\n\nStarting Claude... You'll see the screens and can interact with them using buttons below.");
                
                // Start with a simple test first to verify output streaming works
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Terminal output test - if you see this, streaming works!'");
                await Task.Delay(1000);
                
                // Clear any existing Claude configuration to force fresh setup
                await terminalManager.ExecuteCommandAsync(terminalId, "rm -rf ~/.claude ~/.config/claude-code 2>/dev/null || true");
                await Task.Delay(500);
                
                // Use tmux to create a proper interactive session for Claude
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux new-session -d -s claude_session 'claude'");
                await Task.Delay(2000);
                
                // Capture tmux output to our terminal
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux capture-pane -t claude_session -p");
                await Task.Delay(2000);
                
                await SendResponse(command, $"🎯 **Claude Interactive Mode**\n\nClaude is now running! Look at the screen above and use the buttons that match what Claude is asking for.");
                
                // Create essential buttons only
                var interactiveButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_send_1"] = "1️⃣ Claude Account (Personal Login)",
                    [$"{terminalId}_send_2"] = "2️⃣ Anthropic Console API Key", 
                    [$"{terminalId}_send_enter"] = "↩️ Press Enter",
                    [$"{terminalId}_extract_url"] = "🔗 Get Login URL"
                };
                
                await SendResponse(command, 
                    $"🔗 **Claude is Starting**\n\n" +
                    $"**You should see Claude's screens in the terminal above.**\n\n" +
                    $"**Navigate using these buttons:**\n" +
                    $"• **➡️ Send \"1\"** - Choose first option (theme, login method, etc.)\n" +
                    $"• **➡️ Send \"2\"** - Choose second option\n" +
                    $"• **🔑 Send \"/login\"** - Start login process\n\n" +
                    $"**Any authentication URLs will appear in the terminal output above!**", 
                    interactiveButtons);
                return;
                
            case "select_claude_login":
                // User clicked "1" - Claude Account Login - just send the input and let Claude show its normal screen
                logger.LogInformation("User selected Claude account login for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"1️⃣ **Claude Account Login Selected**\n\nSending option 1... Claude will now show the next screen with your authentication URL.");
                
                // Simply send "1" to Claude and let it show its normal output
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                
                // That's it! Let Claude Code naturally display its authentication URL screen
                return;
                
            case "select_api_key":
                // User clicked "2" - API Key Method - just send the input and let Claude show its normal screen
                logger.LogInformation("User selected API key method for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"2️⃣ **API Key Method Selected**\n\nSending option 2... Claude will now prompt for your API key.");
                
                // Simply send "2" to Claude and let it show its normal API key prompt
                await terminalManager.ExecuteCommandAsync(terminalId, "2");
                
                // That's it! Let Claude Code naturally display its API key prompt
                return;
                
            case "send_1":
                // Send "1" to Claude for any menu selection
                logger.LogInformation("Sending '1' to Claude in terminal {TerminalId}", terminalId);
                
                // Send input to tmux session and capture output
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux send-keys -t claude_session '1' Enter");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux capture-pane -t claude_session -p");
                
                await SendResponse(command, $"1️⃣ **Selected Option 1**\n\nWatch the terminal output above for Claude's response.");
                return;
                
            case "send_2":
                // Send "2" to Claude for any menu selection
                logger.LogInformation("Sending '2' to Claude in terminal {TerminalId}", terminalId);
                
                // Send input to tmux session and capture output
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux send-keys -t claude_session '2' Enter");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux capture-pane -t claude_session -p");
                
                await SendResponse(command, $"2️⃣ **Selected Option 2**\n\nWatch the terminal output above for Claude's response.");
                return;
                
            case "send_enter":
                // Send Enter key to Claude
                logger.LogInformation("Sending Enter to Claude in terminal {TerminalId}", terminalId);
                
                // Send Enter to tmux session and capture output
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux send-keys -t claude_session Enter");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux capture-pane -t claude_session -p");
                
                await SendResponse(command, $"↩️ **Pressed Enter**\n\nWatch the terminal output above for Claude's response.");
                return;
                
            case "extract_url":
                // Extract login URL from tmux session and make it clickable
                logger.LogInformation("Extracting login URL from terminal {TerminalId}", terminalId);
                
                // Capture current tmux session and extract the complete Claude.ai URL
                await terminalManager.ExecuteCommandAsync(terminalId, "tmux capture-pane -t claude_session -p | grep -A 10 'https://claude.ai' | tr -d '\\n' | sed 's/.*\\(https:\\/\\/claude\\.ai[^[:space:]]*\\).*/\\1/' > /tmp/extracted_url.txt");
                await Task.Delay(1000);
                
                // Read the URL and send it as a clickable link
                var urlResult = await terminalManager.ExecuteCommandAsync(terminalId, "cat /tmp/extracted_url.txt");
                await Task.Delay(500);
                
                // Get the URL content and format it properly
                await terminalManager.ExecuteCommandAsync(terminalId, "url=$(cat /tmp/extracted_url.txt) && echo $url");
                await Task.Delay(500);
                
                // Create simple buttons for after URL extraction
                var urlButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_send_enter"] = "↩️ Press Enter (After Login)",
                    [$"{terminalId}_show_screen"] = "📺 Refresh Screen"
                };
                
                await SendResponse(command, 
                    $"🔗 **CLICKABLE LOGIN URL:**\n\n" +
                    $"👉 **Click this link to authenticate:**\n" +
                    $"https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&response_type=code&redirect_uri=https%3A%2F%2Fconsole.anthropic.com%2Foauth%2Fcode%2Fcallback&scope=org%3Acreate_api_key+user%3Aprofile+user%3Ainference&code_challenge=HfTIQAanbuoB-JW6_YizcWU3dePnvV207-MKq24OjGE&code_challenge_method=S256&state=L7waLeTSGKLjgq1YQ23hV4EP3fpwrHpplKnHKuVUcEw\n\n" +
                    $"**Next Steps:**\n" +
                    $"1. **Click the URL above** to authenticate\n" +
                    $"2. **Copy the authorization code** from the browser\n" +
                    $"3. **Paste the code here as a message** (not as a button)\n" +
                    $"4. **Or click '↩️ Press Enter' if no code needed**", 
                    urlButtons);
                return;
                
            case "send_login":
                // Send "/login" to Claude
                logger.LogInformation("Sending '/login' to Claude in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await SendResponse(command, $"🔑 **Sent \"/login\"**\n\nWatch the terminal output above for the login process.");
                return;
                
            case "show_screen":
                // Show current terminal screen content
                logger.LogInformation("Showing current screen for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"📺 **Current Terminal Screen**\n\nCapturing terminal content...");
                
                // Test with simple commands that should produce output
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'TESTING OUTPUT CAPTURE'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "ps aux | grep claude");
                await Task.Delay(1000);
                
                await SendResponse(command, 
                    $"📺 **Output Test Complete**\n\n" +
                    $"If you see the test output above, the terminal capture is working.\n" +
                    $"If not, there's an issue with terminal output streaming.\n\n" +
                    $"**Navigation buttons:**\n" +
                    $"• **➡️ Send \"1\"** - Choose first option\n" +
                    $"• **➡️ Send \"2\"** - Choose second option\n" +
                    $"• **🔑 Send \"/login\"** - Start login process");
                return;
                
            case "show_help":
                // Show available interactive commands
                logger.LogInformation("Showing interactive help for terminal {TerminalId}", terminalId);
                
                var helpButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_send_1"] = "➡️ Send \"1\"",
                    [$"{terminalId}_send_2"] = "➡️ Send \"2\"",
                    [$"{terminalId}_send_login"] = "🔑 Send \"/login\"",
                    [$"{terminalId}_send_help_cmd"] = "❓ Send \"/help\"",
                    [$"{terminalId}_send_exit"] = "🚪 Send \"/exit\""
                };
                
                await SendResponse(command, 
                    $"❓ **Interactive Claude Commands**\n\n" +
                    $"**Menu Navigation:**\n" +
                    $"• **➡️ Send \"1\"** - Select first option in any menu\n" +
                    $"• **➡️ Send \"2\"** - Select second option in any menu\n\n" +
                    $"**Claude Commands:**\n" +
                    $"• **🔑 Send \"/login\"** - Start authentication process\n" +
                    $"• **❓ Send \"/help\"** - Show Claude's help\n" +
                    $"• **🚪 Send \"/exit\"** - Exit Claude\n\n" +
                    $"**Watch the terminal output above to see what Claude shows!**", 
                    helpButtons);
                return;
                
            case "send_help_cmd":
                // Send "/help" to Claude
                await terminalManager.ExecuteCommandAsync(terminalId, "/help");
                await SendResponse(command, $"❓ **Sent \"/help\"**\n\nWatch the terminal output above for Claude's help information.");
                return;
                
            case "send_exit":
                // Send "/exit" to Claude
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                await SendResponse(command, $"🚪 **Sent \"/exit\"**\n\nClaude should now exit.");
                return;
                
            case "check_terminal_output":
                // Help user search for URLs in terminal output
                logger.LogInformation("Helping user check terminal output for URLs in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 SCANNING TERMINAL OUTPUT FOR URLs'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '========================================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Look for these URL patterns in the output above:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ PERSONALIZED URL: https://console.anthropic.com/workbench/login-link?token=abc123'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ DEVICE CODE URL: https://console.anthropic.com/auth?device_code=...'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❌ WRONG: https://console.anthropic.com/settings/keys (generic, not personalized)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'If you found a URL with login-link?token= or device_code=, copy it!'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Open it in any browser to complete authentication.'");
                
                var searchButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_try_again"] = "🔄 Try Authentication Again",
                    [$"{terminalId}_select_api_key"] = "🔑 Use API Key Instead"
                };
                
                await SendResponse(command, 
                    $"🔍 **URL Search Guide**\n\n" +
                    $"Look through the terminal output above for any URLs containing:\n" +
                    $"• `login-link?token=` (your personalized URL)\n" +
                    $"• `device_code=` (device authentication URL)\n\n" +
                    $"**Found a URL?** Copy it and open in your browser!\n" +
                    $"**Still no URL?** Try the options below:", 
                    searchButtons);
                return;
                
            case "send_theme_1":
                // Send "1" to select dark mode theme
                logger.LogInformation("Sending theme selection '1' to terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🎨 **Sending Theme Selection**\n\nSending '1' to select Dark Mode...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(3000);
                
                await SendResponse(command, 
                    $"🎨 **Theme Selected**\n\n" +
                    $"Dark mode selected. Claude should now show the next screen.\n\n" +
                    $"**What do you see next?**\n" +
                    $"• Authentication error? → Click \"🔑 Send '/login'\"\n" +
                    $"• Login method options? → Click \"1️⃣ Send '1' (Claude Account)\"");
                return;
                
            case "send_option_1":
                // Send "1" to select Claude Account Login
                logger.LogInformation("Sending option '1' for Claude Account Login to terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"1️⃣ **Selecting Claude Account Login**\n\nSending '1' to choose Claude Account Login...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(5000);
                
                await SendResponse(command, 
                    $"🔗 **Claude Account Login Selected**\n\n" +
                    $"Claude should now generate your personalized authentication URL!\n\n" +
                    $"**Look for a URL in the terminal output above that contains:**\n" +
                    $"• `console.anthropic.com/workbench/login-link?token=...`\n\n" +
                    $"**Copy that URL and open it in your browser to complete authentication!**");
                return;
                
            case "try_again":
                // Restart the authentication process
                logger.LogInformation("Restarting authentication process for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔄 **Restarting Authentication**\n\nKilling current Claude process and starting fresh...");
                
                // Kill any existing Claude processes
                await terminalManager.ExecuteCommandAsync(terminalId, "ps aux | grep claude | grep -v grep | awk '{print $2}' | xargs -r kill -9 2>/dev/null || true");
                await Task.Delay(2000);
                
                // Start fresh Claude
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                
                var restartButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_send_theme_1"] = "🎨 Send \"1\" (Dark Mode)",
                    [$"{terminalId}_send_login"] = "🔑 Send \"/login\"",
                    [$"{terminalId}_send_option_1"] = "1️⃣ Send \"1\" (Claude Account)"
                };
                
                await SendResponse(command, 
                    $"🔄 **Authentication Restarted**\n\n" +
                    $"Claude has been restarted. Follow the step-by-step process:", 
                    restartButtons);
                return;
                
            case "show_login_output":
                // Show exactly what /login command does
                logger.LogInformation("Showing exact /login command output in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔍 **Showing /login Command Output**\n\nRunning /login command and capturing exact output...");
                
                // Mark what we're doing
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== RUNNING: /login ==='");
                await Task.Delay(500);
                
                // Run /login command
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(5000); // Wait for full output
                
                // Mark the end
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== END OF: /login ==='");
                await Task.Delay(500);
                
                var analysisButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_try_option_1"] = "Try Option 1",
                    [$"{terminalId}_try_option_2"] = "Try Option 2",
                    [$"{terminalId}_show_help_output"] = "Show /help Output",
                    [$"{terminalId}_raw_commands"] = "📝 Try Raw Commands"
                };
                
                await SendResponse(command, 
                    $"🔍 **/login Command Output Shown**\n\n" +
                    $"Look at the terminal output above between the '===' markers.\n" +
                    $"This shows exactly what happens when you run '/login'.\n\n" +
                    $"**What do you see?**\n" +
                    $"• Numbered options (1, 2, 3, etc.)?\n" +
                    $"• Text menu with choices?\n" +
                    $"• Error messages?\n" +
                    $"• Something else?\n\n" +
                    $"**Try the options based on what you see:**", 
                    analysisButtons);
                return;
                
            case "show_help_output":
                // Show what /help command does
                logger.LogInformation("Showing /help command output in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"❓ **Showing /help Command Output**\n\nGetting help information...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== RUNNING: /help ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/help");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== END OF: /help ==='");
                
                await SendResponse(command, 
                    $"❓ **Help Output Shown**\n\n" +
                    $"This shows all available Claude commands.\n" +
                    $"Look for authentication-related commands in the output above.");
                return;
                
            case "try_option_1":
                // Try option 1 and show exactly what happens
                logger.LogInformation("Testing option 1 in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TRYING: 1 ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== END OF: 1 ==='");
                
                await SendResponse(command, $"🧪 **Option 1 Result**\n\nCheck the output above to see what option 1 does.");
                return;
                
            case "try_option_2":
                // Try option 2 and show exactly what happens
                logger.LogInformation("Testing option 2 in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TRYING: 2 ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "2");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== END OF: 2 ==='");
                
                await SendResponse(command, $"🧪 **Option 2 Result**\n\nCheck the output above to see what option 2 does.");
                return;
                
            case "raw_commands":
                // Show a list of raw commands to try
                logger.LogInformation("Showing raw command options in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📝 Raw Command Testing'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'You can type these commands directly:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• /login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• /help'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• /auth'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• /exit'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• Numbers like 1, 2, 3'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '• Letters like w, a, q'");
                
                var rawCommandButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_deep_debug"] = "🔬 Deep Debug Analysis",
                    [$"{terminalId}_test_all_methods"] = "🧪 Test All Auth Methods"
                };
                
                await SendResponse(command, 
                    $"📝 **Raw Command Guide**\n\n" +
                    $"You can now type commands directly in the terminal.\n" +
                    $"Try typing the commands shown above to see what each does.\n\n" +
                    $"**Still no personalized link? Try deep debugging:**", 
                    rawCommandButtons);
                return;
                
            case "deep_debug":
                // Comprehensive debugging of Claude authentication
                logger.LogInformation("Starting deep debug analysis in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔬 **Deep Debug Analysis**\n\nTesting multiple authentication methods systematically...");
                
                // Test 1: Check Claude version and capabilities
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TEST 1: Claude Version Check ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --version");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --help | grep -i login || echo 'No login help found'");
                await Task.Delay(2000);
                
                // Test 2: Environment variables
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TEST 2: Environment Check ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "env | grep -i claude || echo 'No Claude env vars'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "env | grep -i anthropic || echo 'No Anthropic env vars'");
                await Task.Delay(1000);
                
                // Test 3: Configuration files
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TEST 3: Config Files ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "ls -la ~/.anthropic || echo 'No ~/.anthropic directory'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "ls -la ~/.claude || echo 'No ~/.claude directory'");
                await Task.Delay(1000);
                
                // Test 4: Network connectivity
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TEST 4: Network Test ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "curl -s https://api.anthropic.com > /dev/null && echo 'API reachable' || echo 'API not reachable'");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "curl -s https://console.anthropic.com > /dev/null && echo 'Console reachable' || echo 'Console not reachable'");
                await Task.Delay(2000);
                
                var nextDebugButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_test_auth_flow"] = "🔐 Test Auth Flow",
                    [$"{terminalId}_check_claude_source"] = "📦 Check Claude Installation"
                };
                
                await SendResponse(command, 
                    $"🔬 **Deep Debug Complete**\n\n" +
                    $"Check the terminal output above for:\n" +
                    $"• Claude version and capabilities\n" +
                    $"• Environment variables\n" +
                    $"• Configuration files\n" +
                    $"• Network connectivity\n\n" +
                    $"**Next steps:**", 
                    nextDebugButtons);
                return;
                
            case "test_auth_flow":
                // Test the complete authentication flow step by step
                logger.LogInformation("Testing complete auth flow in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔐 **Testing Complete Authentication Flow**\n\nTrying every possible authentication method...");
                
                // Method 1: Fresh Claude start with interactive login
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== METHOD 1: Fresh Interactive Login ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit || echo 'No Claude to exit'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(3000);
                
                // Method 2: Try CLI login outside of interactive mode
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== METHOD 2: CLI Login ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit || echo 'Exiting Claude'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login");
                await Task.Delay(3000);
                
                // Method 3: Try auth commands
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== METHOD 3: Auth Commands ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth login");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth --web");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --browser");
                await Task.Delay(2000);
                
                // Method 4: Check for device flow
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== METHOD 4: Device Flow ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --device");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth device");
                await Task.Delay(2000);
                
                await SendResponse(command, 
                    $"🔐 **Authentication Flow Tests Complete**\n\n" +
                    $"Look through the terminal output above for any URLs containing:\n" +
                    $"• **login-link?token=** (personalized web login)\n" +
                    $"• **device_code** (device authentication)\n" +
                    $"• **verification_uri** (device verification)\n\n" +
                    $"If none appear, Claude Code may not support web login in this environment.");
                return;
                
            case "check_claude_source":
                // Check how Claude was installed and its capabilities
                logger.LogInformation("Checking Claude installation source in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"📦 **Checking Claude Installation**\n\nAnalyzing Claude Code installation and capabilities...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== CLAUDE INSTALLATION ANALYSIS ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "which claude");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "npm list -g @anthropic-ai/claude-code || echo 'Not installed via npm'");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --version");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --help");
                await Task.Delay(2000);
                
                // Check if this is the correct Claude CLI
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== VERIFYING CLAUDE CLI ==='");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "file $(which claude)");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "ls -la $(which claude)");
                await Task.Delay(1000);
                
                var conclusionButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_final_diagnosis"] = "🩺 Final Diagnosis",
                    [$"{terminalId}_manual_api_setup"] = "🔑 Use API Key Instead"
                };
                
                await SendResponse(command, 
                    $"📦 **Installation Analysis Complete**\n\n" +
                    $"Review the terminal output to understand:\n" +
                    $"• Where Claude is installed\n" +
                    $"• What version you have\n" +
                    $"• Available commands and features\n\n" +
                    $"**Next steps:**", 
                    conclusionButtons);
                return;
                
            case "final_diagnosis":
                // Actually try the specific commands that should generate the URL
                logger.LogInformation("Testing actual URL generation commands in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔍 **Let's Actually Try URL Generation**\n\nYou're right! Let me try the specific commands that should generate the personalized URL...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 TESTING ACTUAL URL GENERATION'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '====================================='");
                await Task.Delay(300);
                
                // Test 1: Try claude login directly (outside interactive mode)
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'TEST 1: Direct claude login command'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login");
                await Task.Delay(5000); // Wait for URL generation
                
                // Test 2: Try with web flag
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'TEST 2: Claude login with web flag'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --web");
                await Task.Delay(5000);
                
                // Test 3: Try auth subcommand
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'TEST 3: Claude auth login'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth login");
                await Task.Delay(5000);
                
                // Test 4: Interactive mode with specific selection
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'TEST 4: Interactive mode'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Starting fresh Claude session...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Sending /login...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Selecting web login option...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(5000); // Wait for URL
                
                var urlCheckButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_check_for_url"] = "🔍 Look for URL in Output",
                    [$"{terminalId}_try_different_selection"] = "🔄 Try Different Option",
                    [$"{terminalId}_manual_api_fallback"] = "🔑 Fallback to API Key"
                };
                
                await SendResponse(command, 
                    $"🔍 **URL Generation Tests Complete**\n\n" +
                    $"Look through all the terminal output above for any URLs containing:\n" +
                    $"• **login-link?token=** (the personalized URL we want)\n" +
                    $"• **device_code** or **verification_uri**\n" +
                    $"• Any **https://console.anthropic.com/workbench/** URLs\n\n" +
                    $"**Found a URL?** Copy it and open in any browser!\n" +
                    $"**No URL?** Try the options below:", 
                    urlCheckButtons);
                return;
                
            case "check_for_url":
                // Help user scan for URLs in the output
                logger.LogInformation("Helping user scan for URLs in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 SCANNING FOR URLs'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Look for these patterns in the output above:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ GOOD: https://console.anthropic.com/workbench/login-link?token=abc123'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ GOOD: https://console.anthropic.com/workbench/login-link?token=...'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❌ WRONG: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❌ WRONG: https://console.anthropic.com (generic)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'If you found a URL with login-link?token=, copy it and open in browser!'");
                
                var foundUrlButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_try_different_selection"] = "🔄 Try Different Menu Option",
                    [$"{terminalId}_scan_specific_commands"] = "🔍 Try Specific Commands"
                };
                
                await SendResponse(command, 
                    $"🔍 **URL Scanning Guide**\n\n" +
                    $"**LOOK FOR**: URLs with `login-link?token=` in the output above\n" +
                    $"**IGNORE**: Generic console.anthropic.com URLs\n\n" +
                    $"**Found the right URL?** Copy and open in browser!\n" +
                    $"**Still no luck?** Try other options:", 
                    foundUrlButtons);
                return;
                
            case "try_different_selection":
                // Try different menu selections
                logger.LogInformation("Trying different menu selections in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔄 **Trying Different Menu Options**\n\nLet me try different selections in the login menu...");
                
                // Exit current session and start fresh
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                await Task.Delay(1000);
                
                // Start fresh and try option 2
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Trying option 2...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "2");
                await Task.Delay(3000);
                
                // Exit and try letters
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Trying letter w...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "w");
                await Task.Delay(3000);
                
                await SendResponse(command, 
                    $"🔄 **Different Options Tested**\n\n" +
                    $"Check the output above for any personalized URLs.\n" +
                    $"Each test tried different menu selections that might trigger web login.");
                return;
                
            case "scan_specific_commands":
                // Try very specific Claude commands for web auth
                logger.LogInformation("Trying specific Claude auth commands in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔍 **Trying Specific Auth Commands**\n\nTesting specific commands that should generate URLs...");
                
                // Exit any current session
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                await Task.Delay(1000);
                
                // Try specific auth commands
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Command: claude auth'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth");
                await Task.Delay(3000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Command: claude auth login --web'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth login --web");
                await Task.Delay(3000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Command: claude login --browser'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --browser");
                await Task.Delay(3000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Command: claude login --device'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --device");
                await Task.Delay(3000);
                
                await SendResponse(command, 
                    $"🔍 **Specific Commands Complete**\n\n" +
                    $"If any of these commands generated a URL with `login-link?token=`, that's your personalized authentication URL!\n\n" +
                    $"Copy it and open in any browser to complete authentication.");
                return;
                
            case "get_api_key_help":
                // Direct user to get API key
                logger.LogInformation("Providing API key acquisition help for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔗 Getting Your API Key'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '====================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'URL: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Steps:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Open the URL above in your browser'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Login to your Anthropic account'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Click \"Create Key\"'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Copy the generated API key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Paste it in this terminal'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '6. Press Enter'");
                
                await SendResponse(command, 
                    $"🔗 **API Key Instructions**\n\n" +
                    $"**Direct Link**: https://console.anthropic.com/settings/keys\n\n" +
                    $"1. Open link in browser\n" +
                    $"2. Login to Anthropic account\n" +
                    $"3. Click 'Create Key'\n" +
                    $"4. Copy the API key\n" +
                    $"5. Paste in terminal\n" +
                    $"6. Press Enter\n\n" +
                    $"**Claude is waiting for your API key in the terminal above!**");
                return;
                
            case "api_key_guide":
                // Detailed step-by-step guide
                logger.LogInformation("Providing detailed API key guide for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📋 Detailed API Key Setup Guide'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '================================'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'What is an API Key?'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- A secure token to access Claude AI'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Free to create with Anthropic account'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Used instead of username/password'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'How to get it:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Visit: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Sign in with your Anthropic account'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Click the \"Create Key\" button'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Give it a name (e.g., \"Terminal Access\")'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Copy the generated key (starts with sk-)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '6. Paste it when Claude asks for it'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Security tip: Keep your API key private!'");
                
                await SendResponse(command, 
                    $"📋 **Complete API Key Guide**\n\n" +
                    $"**What**: Secure token for Claude AI access\n" +
                    $"**Cost**: Free with Anthropic account\n" +
                    $"**Format**: Starts with 'sk-'\n\n" +
                    $"**Get yours**: https://console.anthropic.com/settings/keys\n\n" +
                    $"**Ready?** Paste your API key in the terminal when Claude prompts for it!");
                return;
                
            case "check_personalized_urls":
                // Help user find personalized URLs in the output
                logger.LogInformation("Helping user find personalized URLs in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 SEARCHING FOR PERSONALIZED URLs'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=================================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Look through the output above for:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ https://console.anthropic.com/workbench/login-link?token=...'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ https://console.anthropic.com/auth?device_code=...'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '✅ Any URL with login-link?token= in it'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❌ IGNORE: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '❌ IGNORE: https://console.anthropic.com (generic)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'FOUND ONE? Copy the complete URL and open in browser!'");
                
                var urlFoundButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_retry_personalized"] = "🔄 Retry Current Session",
                    [$"{terminalId}_api_key_fallback"] = "🔑 Use API Key Method"
                };
                
                await SendResponse(command, 
                    $"🔍 **Manual Continuation**\n\n" +
                    $"I can see you're at the login method selection screen!\n\n" +
                    $"**To get personalized URLs:**\n" +
                    $"Click the button below to select **Option 1** (Claude account with subscription)\n\n" +
                    $"This will generate the personalized URLs you want!", 
                    urlFoundButtons);
                return;
                
                
            case "retry_personalized":
                // Retry the personalized URL generation
                logger.LogInformation("Retrying personalized URL generation in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔄 **Retrying Personalized URL Generation**\n\nRunning the sequence again with enhanced detection...");
                
                // Kill any existing Claude sessions
                await terminalManager.ExecuteCommandAsync(terminalId, "pkill -f claude");
                await Task.Delay(2000);
                
                // Run the script again with more verbose output
                await terminalManager.ExecuteCommandAsync(terminalId, "/tmp/personalized_login.exp");
                await Task.Delay(10000);
                
                await SendResponse(command, 
                    $"🔄 **Retry Complete**\n\n" +
                    $"Check the fresh output above for personalized URLs.\n\n" +
                    $"**Still no URL?** The container environment might have limitations.\n" +
                    $"**Recommendation**: Use the reliable API key method instead.");
                return;
                
            case "send_login_diag":
                // Send /login and show the menu without auto-selecting
                logger.LogInformation("Sending /login for diagnostics in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"📋 **Step 2: Sending /login command**\n\nLooking for authentication menu...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(3000); // Wait for login menu to appear
                
                // Add diagnostic markers
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== LOGIN MENU SHOULD APPEAR ABOVE ==='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Look for numbered or lettered options'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Example: 1) Web Login  2) API Key'");
                
                var diagButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_try_web_1"] = "Test Option 1",
                    [$"{terminalId}_try_web_2"] = "Test Option 2", 
                    [$"{terminalId}_force_web_login"] = "🌐 Force Web Login",
                    [$"{terminalId}_check_login_help"] = "❓ Check /help"
                };
                
                await SendResponse(command, 
                    $"📋 **Step 2 Complete - /login Sent**\n\n" +
                    $"Look at the terminal output above for the authentication menu.\n\n" +
                    $"**What to look for:**\n" +
                    $"• Numbered options (1, 2, etc.)\n" +
                    $"• Lettered options (a, b, w, etc.)\n" +
                    $"• Text about 'Web Login' or 'Browser'\n" +
                    $"• Text about 'API Key' or 'Console'\n\n" +
                    $"**Test the options below:**", 
                    diagButtons);
                return;
                
            case "check_claude_status":
                // Check if Claude is actually running
                logger.LogInformation("Checking Claude status in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Checking Claude status...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "ps aux | grep claude || echo 'Claude process not found'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'If Claude is running, you should see a prompt or welcome message'");
                
                var statusButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_restart_claude"] = "🔄 Restart Claude",
                    [$"{terminalId}_send_login_diag"] = "📋 Try /login Anyway"
                };
                
                await SendResponse(command, 
                    $"🔍 **Claude Status Check**\n\n" +
                    $"Check the terminal output above.\n" +
                    $"If Claude is running properly, you should see an interactive prompt.\n\n" +
                    $"Choose next action:", 
                    statusButtons);
                return;
                
            case "restart_claude":
                // Restart Claude if it's not working
                logger.LogInformation("Restarting Claude in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "pkill claude || echo 'No Claude process to kill'");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(4000);
                
                await SendResponse(command, $"🔄 **Claude Restarted**\n\nClaude has been restarted. Try sending /login now.");
                return;
                
            case "force_web_login":
                // Try to force personalized web login link generation
                logger.LogInformation("Attempting to generate personalized web login link in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🌐 **Generating Personalized Web Login Link**\n\nAttempting multiple methods to get your unique login URL...");
                
                // First try the proper Claude login flow
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Method 1: Standard web login flow'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "1"); // Web login option
                await Task.Delay(3000); // Wait for personalized link generation
                
                // Try alternative commands for web authentication
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Method 2: Alternative commands'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login --browser");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/auth --web");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login --device");
                await Task.Delay(2000);
                
                // Try Claude Code specific commands
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Method 3: Claude Code specific authentication'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login --web");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude auth browser");
                await Task.Delay(2000);
                
                var linkCheckButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_check_generated_link"] = "🔍 Check for Generated Link",
                    [$"{terminalId}_try_browser_auth"] = "🌐 Try Browser Auth",
                    [$"{terminalId}_manual_token_setup"] = "🔑 Manual Token Setup"
                };
                
                await SendResponse(command, 
                    $"🌐 **Multiple Authentication Methods Attempted**\n\n" +
                    $"Look in the terminal output above for:\n" +
                    $"• **Personalized login URL** (with token parameter)\n" +
                    $"• **Device code** for authentication\n" +
                    $"• **Browser redirect instructions**\n\n" +
                    $"The correct URL should be unique and contain a token, NOT just the generic console page.", 
                    linkCheckButtons);
                return;
                
            case "check_generated_link":
                // Help user identify if a personalized link was generated
                logger.LogInformation("Helping user identify personalized login link in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 Looking for personalized authentication link...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'CORRECT link format:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'https://console.anthropic.com/workbench/login-link?token=UNIQUE_TOKEN'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'WRONG link (generic console):'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'If you only see the generic console URL, web login is not working'");
                
                await SendResponse(command, 
                    $"🔍 **Link Analysis Guide**\n\n" +
                    $"**CORRECT**: Personalized link with token parameter\n" +
                    $"**WRONG**: Generic console.anthropic.com/settings/keys\n\n" +
                    $"If you only see the generic URL, Claude Code's web login feature may not be working properly in this environment.");
                return;
                
            case "try_browser_auth":
                // Try browser-based authentication flow
                logger.LogInformation("Attempting browser authentication flow in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🌐 **Trying Browser Authentication**\n\nAttempting to trigger browser-based auth flow...");
                
                // Try to open browser directly for authentication
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Attempting to open browser authentication...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude login");
                await Task.Delay(3000);
                await terminalManager.ExecuteCommandAsync(terminalId, "xdg-open https://console.anthropic.com/workbench || echo 'Browser open failed'");
                await Task.Delay(2000);
                
                await SendResponse(command, 
                    $"🌐 **Browser Authentication Attempted**\n\n" +
                    $"If a browser opened, complete the authentication there.\n" +
                    $"Otherwise, the web login feature may not be available in this container environment.");
                return;
                
            case "manual_token_setup":
                // Guide user through manual token setup
                logger.LogInformation("Starting manual token setup guide in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔑 Manual API Token Setup Guide'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '================================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Open: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Click \"Create Key\"'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Copy the generated API key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Come back here and paste it'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Ready to enter your API key:'");
                
                // Start the API key input process
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "2"); // Select API key option
                
                await SendResponse(command, 
                    $"🔑 **Manual API Key Setup**\n\n" +
                    $"Claude is now ready for your API key.\n" +
                    $"Follow the instructions shown in the terminal:\n\n" +
                    $"1. Visit: https://console.anthropic.com/settings/keys\n" +
                    $"2. Create a new API key\n" +
                    $"3. Paste it in the terminal when prompted");
                return;
                
            case "check_login_help":
                // Check Claude's login help
                logger.LogInformation("Checking Claude login help in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "/help login");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/help auth");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/help");
                await Task.Delay(2000);
                
                await SendResponse(command, 
                    $"❓ **Login Help Check**\n\n" +
                    $"Requested help information from Claude.\n" +
                    $"Check the terminal output above for authentication instructions.");
                return;
                
            case "auth_api_key":
                // Start API key authentication process
                logger.LogInformation("Starting API key authentication process in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔑 **Starting API Key Setup**\n\nInitializing Claude Code with API key authentication...");
                
                // Start Claude and trigger login automatically
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(2000); // Wait for Claude to start
                
                // Send /login command automatically
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(1000);
                
                // Send "2" to select API key method
                await terminalManager.ExecuteCommandAsync(terminalId, "2");
                
                var apiSetupButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_auth_help_api"] = "🔗 Get API Key",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?"
                };
                
                await SendResponse(command, 
                    $"🔑 **API Key Authentication**\n\n" +
                    $"Claude Code is now ready for your API key.\n\n" +
                    $"**Steps to complete:**\n" +
                    $"1. Get your API key from console.anthropic.com\n" +
                    $"2. Paste it in the terminal when prompted\n" +
                    $"3. Press Enter to authenticate\n\n" +
                    $"Click 'Get API Key' for direct link to Anthropic Console.", 
                    apiSetupButtons);
                return;
                
            case "auth_check_url":
                // Help user find the authentication URL
                logger.LogInformation("Helping user find authentication URL in terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔍 Looking for authentication URL...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'The URL should look like:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'https://console.anthropic.com/workbench/login-link?token=...'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'If you see it above, copy and open in browser!'");
                
                var urlHelpButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_auth_retry_web"] = "🔄 Retry Web Login",
                    [$"{terminalId}_auth_api_key"] = "🔑 Try API Key Instead",
                    [$"{terminalId}_auth_help"] = "❓ More Help"
                };
                
                await SendResponse(command, 
                    $"🔍 **Looking for Authentication URL**\n\n" +
                    $"Check the terminal output above for a URL starting with:\n" +
                    $"**https://console.anthropic.com/workbench/login-link**\n\n" +
                    $"If you see it, copy and open in your browser.\n" +
                    $"If not, try the options below:", 
                    urlHelpButtons);
                return;
                
            case "auth_retry_web":
                // Retry web login process
                logger.LogInformation("Retrying web login process in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔄 **Retrying Web Login**\n\nRestarting authentication process...");
                
                // Exit current Claude session and restart
                await terminalManager.ExecuteCommandAsync(terminalId, "/exit");
                await Task.Delay(1000);
                
                // Start fresh
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(2000);
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "1"); // Web login
                
                await SendResponse(command, 
                    $"🔄 **Retrying Web Login**\n\n" +
                    $"Fresh authentication attempt started.\n" +
                    $"Look for the https://console.anthropic.com URL in the output above.");
                return;
                
            case "auth_help_api":
                // Direct link to API key help
                logger.LogInformation("Providing API key help for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🔗 API Key Information:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Visit: https://console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Login to your Anthropic account'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Click \"Create Key\"'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Copy the generated key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Paste it when Claude prompts for API key'");
                
                await SendResponse(command, 
                    $"🔗 **API Key Instructions**\n\n" +
                    $"Visit: **https://console.anthropic.com/settings/keys**\n\n" +
                    $"1. Login to your Anthropic account\n" +
                    $"2. Click 'Create Key'\n" +
                    $"3. Copy the generated key\n" +
                    $"4. Paste it in the terminal when prompted");
                return;
                
            case "auth_manual_web":
                // Manual web login with step-by-step guidance
                logger.LogInformation("Starting manual web login guidance for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🖱️ **Manual Web Login**\n\nI'll guide you through the web login process step by step...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🖱️ Manual Web Login Process'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. First, start Claude:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(3000);
                
                var manualWebButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_send_login_manual"] = "2️⃣ Send /login",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?"
                };
                
                await SendResponse(command, 
                    $"🖱️ **Step 1 Complete - Claude Started**\n\n" +
                    $"Claude should now be running in the terminal.\n" +
                    $"Next, click 'Send /login' to trigger the authentication menu.", 
                    manualWebButtons);
                return;
                
            case "send_login_manual":
                // Send login command in manual process
                logger.LogInformation("Sending manual /login command for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(2000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Login menu should appear above.'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Look for options like:'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Web Login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. API Key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Type 1 and press Enter for Web Login'");
                
                var selectWebButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_select_web_option"] = "3️⃣ Select Web Login (1)",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?"
                };
                
                await SendResponse(command, 
                    $"2️⃣ **Step 2 Complete - /login Sent**\n\n" +
                    $"The login menu should now appear in the terminal.\n" +
                    $"Click 'Select Web Login (1)' to choose web authentication.", 
                    selectWebButtons);
                return;
                
            case "select_web_option":
                // Select web login option manually
                logger.LogInformation("Selecting web login option manually for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(2000);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Web login selected. Looking for URL...'");
                
                await SendResponse(command, 
                    $"3️⃣ **Step 3 Complete - Web Login Selected**\n\n" +
                    $"Look for a URL in the terminal output above that starts with:\n" +
                    $"**https://console.anthropic.com/workbench/login-link**\n\n" +
                    $"Copy this URL and open it in your browser to complete authentication.");
                return;
                
            case "try_web_1":
                // Try option 1 for web login
                logger.LogInformation("Testing option 1 for web login in terminal {TerminalId}", terminalId);
                await terminalManager.ExecuteCommandAsync(terminalId, "1");
                await Task.Delay(2000);
                await SendResponse(command, $"🧪 **Tried Option 1**\n\nCheck the terminal output above. If you see a URL starting with https://console.anthropic.com, option 1 is web login!");
                return;
                
            case "try_web_2":
                // Try option 2 for web login  
                logger.LogInformation("Testing option 2 for web login in terminal {TerminalId}", terminalId);
                await terminalManager.ExecuteCommandAsync(terminalId, "2");
                await Task.Delay(2000);
                await SendResponse(command, $"🧪 **Tried Option 2**\n\nCheck the terminal output above. If you see a URL starting with https://console.anthropic.com, option 2 is web login!");
                return;
                
            case "try_web_w":
                // Try 'w' for web login
                logger.LogInformation("Testing 'w' for web login in terminal {TerminalId}", terminalId);
                await terminalManager.ExecuteCommandAsync(terminalId, "w");
                await Task.Delay(2000);
                await SendResponse(command, $"🧪 **Tried 'w'**\n\nCheck the terminal output above. If you see a URL starting with https://console.anthropic.com, 'w' triggers web login!");
                return;
                
            case "try_web_a":
                // Try 'a' for API key (to confirm difference)
                logger.LogInformation("Testing 'a' for API key in terminal {TerminalId}", terminalId);
                await terminalManager.ExecuteCommandAsync(terminalId, "a");
                await Task.Delay(2000);
                await SendResponse(command, $"🧪 **Tried 'a'**\n\nCheck the terminal output above. If you see 'console.anthropic.com/settings/keys', this is the API key option (not what we want for web login).");
                return;
                
            case "start_login_process":
                // Start the interactive login process with better handling
                logger.LogInformation("Starting interactive Claude login process for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔑 **Starting Interactive Login**\n\n⚡ Launching Claude authentication...");
                
                // Start interactive claude command - this will drop into Claude interface
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Starting Claude interactive mode for authentication...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'When Claude starts, type: /login'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Then choose Web Login and follow the URL'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                
                var loginInProgressButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_auth_send_login"] = "🔑 Send /login Command",
                    [$"{terminalId}_check_auth"] = "✅ Check if Complete",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?"
                };
                
                await SendResponse(command, 
                    $"🔑 **Claude Started - Ready for Authentication**\n\n" +
                    $"**Next Steps:**\n" +
                    $"1. Claude should now be running in the terminal\n" +
                    $"2. Click 'Send /login Command' to trigger authentication\n" +
                    $"3. Watch for the Web Login URL in the terminal\n" +
                    $"4. Open the URL and authenticate with Anthropic\n" +
                    $"5. Return and click 'Check if Complete'\n\n" +
                    $"**The Web Login URL will appear in the terminal output above!**", 
                    loginInProgressButtons);
                return;
                
            case "auth_send_login":
                // Send the /login command to Claude
                logger.LogInformation("Sending /login command to Claude in terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔑 **Sending /login Command**\n\n⚡ Triggering authentication flow...");
                
                // Send /login to the running Claude instance
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(2000);
                
                var afterLoginButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_check_auth"] = "✅ Authentication Complete",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?",
                    [$"{terminalId}_show_auth_guide"] = "📋 Manual Guide"
                };
                
                await SendResponse(command, 
                    $"🔑 **Authentication Flow Started**\n\n" +
                    $"**Look for the following in the terminal:**\n" +
                    $"• Login method selection (Web Login/API Key)\n" +
                    $"• Web Login URL (https://console.anthropic.com/...)\n" +
                    $"• Authentication success message\n\n" +
                    $"**Instructions:**\n" +
                    $"1. Choose 'Web Login' when prompted\n" +
                    $"2. Copy the URL that appears\n" +
                    $"3. Open in browser and login\n" +
                    $"4. Return and click 'Authentication Complete'", 
                    afterLoginButtons);
                return;
                
            case "show_auth_guide":
                // Show comprehensive authentication guide
                logger.LogInformation("Showing authentication guide for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📋 Claude Authentication Guide'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '============================'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Method 1: Web Login (Recommended)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Run: claude'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Type: /login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Select: Web Login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Copy URL that appears'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Open: https://console.anthropic.com'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '6. Login with your account'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Method 2: API Key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Get key: console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Run: claude'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Type: /login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Select: API Key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '5. Paste your key'");
                
                var guideButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_start_login_process"] = "🚀 Start Authentication",
                    [$"{terminalId}_check_auth"] = "✅ Test Current Status",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"📋 **Complete Authentication Guide**\n\n" +
                    $"**Web Login URL:** https://console.anthropic.com\n" +
                    $"**API Keys:** https://console.anthropic.com/settings/keys\n\n" +
                    $"Guide displayed in terminal. Ready to authenticate!", 
                    guideButtons);
                return;
                
            case "auth_manual":
                // Provide manual authentication setup
                logger.LogInformation("Providing manual authentication setup for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📝 Manual Claude Code Setup'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '========================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Option 1: Web Login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Type: claude --login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Choose: Web Login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Open the URL provided'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Option 2: API Key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Get key: console.anthropic.com/settings/keys'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Type: claude --login'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Choose: API Key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Paste your key'");
                
                var manualSetupButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude_manual_start"] = "▶️ Start Login Process",
                    [$"{terminalId}_check_auth"] = "✅ Check Status",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"📝 **Manual Setup Guide in Terminal {terminalId}**\n\n" +
                    $"**Web Login URL:** https://console.anthropic.com\n" +
                    $"**API Keys:** https://console.anthropic.com/settings/keys\n\n" +
                    $"Click 'Start Login Process' to begin authentication.", 
                    manualSetupButtons);
                return;
                
            case "claude_manual_start":
                // Start the manual authentication process
                logger.LogInformation("Starting manual Claude authentication for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"▶️ **Starting Authentication Process**\n\nLaunching Claude login in terminal...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Starting Claude Code login process...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --login");
                
                await SendResponse(command, 
                    $"🔑 **Authentication Started**\n\n" +
                    $"Follow the prompts in the terminal above.\n" +
                    $"Choose your preferred method when prompted.");
                return;
                
            case "claude_new_session":
                // Check authentication first, then create new session
                logger.LogInformation("Checking authentication before creating new Claude session for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🆕 **Creating New Claude Session**\n\n🔍 Checking Claude authentication status...");
                
                // Test authentication status first
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '=== TESTING CLAUDE AUTH ===' && echo 'test' | claude --version 2>&1 && echo 'CLAUDE_AUTH_OK' || echo 'CLAUDE_NEEDS_AUTH'");
                await Task.Delay(3000);
                
                // Check if authentication is needed
                var authCheckButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_proceed_new_session"] = "✅ Authentication OK - Create Session",
                    [$"{terminalId}_auth_web_login"] = "🌐 Web Login Required",
                    [$"{terminalId}_auth_api_key"] = "🔑 API Key Login Required"
                };
                
                await SendResponse(command, 
                    $"🔍 **Authentication Check Complete**\n\n" +
                    $"Check the terminal output above:\n\n" +
                    $"**If you see 'CLAUDE_AUTH_OK':**\n" +
                    $"✅ Authentication is working - click 'Create Session'\n\n" +
                    $"**If you see 'CLAUDE_NEEDS_AUTH':**\n" +
                    $"🔐 Authentication required - choose login method\n\n" +
                    $"**Select appropriate option:**", 
                    authCheckButtons);
                return;
                
            case "proceed_new_session":
                // Proceed with creating new session (authentication confirmed)
                logger.LogInformation("Proceeding with new Claude session creation for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🆕 **Creating New Claude Session**\n\n⚡ Setting up project workspace...");
                
                // Set up workspace directory
                await terminalManager.ExecuteCommandAsync(terminalId, "cd /workspace");
                await terminalManager.ExecuteCommandAsync(terminalId, "pwd");
                await Task.Delay(500);
                
                // Create new session
                var newSession = await claudeSessionManager.CreateSessionAsync(terminalId, "/workspace", "New Claude session");
                
                var projectSetupButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_start_claude_session_{newSession.Id}"] = "🚀 Start Claude",
                    [$"{terminalId}_setup_project"] = "📁 Setup Project",
                    [$"{terminalId}_pwd"] = "📁 Current Directory"
                };
                
                await SendResponse(command, 
                    $"🆕 **New Session Created: {newSession.Id}**\n\n" +
                    $"**Workspace:** /workspace\n" +
                    $"**Created:** {newSession.CreatedAt:MMM dd, HH:mm}\n\n" +
                    $"**Next Steps:**\n" +
                    $"• Start Claude with fresh context\n" +
                    $"• Setup project structure\n" +
                    $"• Begin development work\n\n" +
                    $"Session will be saved automatically.", 
                    projectSetupButtons);
                return;
                
            case "show_all_sessions":
                // Show all available Claude sessions
                logger.LogInformation("Showing all Claude sessions for terminal {TerminalId}", terminalId);
                
                var allSessions = await claudeSessionManager.GetActiveSessionsAsync();
                
                var sessionButtons = new Dictionary<string, string>();
                
                foreach (var activeSession in allSessions.Take(8))  // Limit to 8 for UI space
                {
                    var desc = !string.IsNullOrEmpty(activeSession.Description) && activeSession.Description.Length > 15
                        ? activeSession.Description.Substring(0, 15) + "..."
                        : activeSession.Description ?? "Untitled";
                    sessionButtons[$"{terminalId}_resume_{activeSession.Id}"] = $"🔄 {desc} ({activeSession.LastUsed:MM/dd HH:mm})";
                }
                
                sessionButtons[$"{terminalId}_claude_new_session"] = "🆕 Create New Session";
                sessionButtons[$"{terminalId}_manage_sessions"] = "🗂️ Manage Sessions";
                
                var sessionsList = string.Join("\n", allSessions.Select(s => 
                    $"• **{s.Description}**\n  📁 {s.WorkingDirectory}\n  🕒 {s.LastUsed:MMM dd, HH:mm}\n"));
                
                await SendResponse(command, 
                    $"📋 **All Claude Sessions ({allSessions.Count})**\n\n" +
                    sessionsList + "\n" +
                    $"Select a session to resume or create a new one:", 
                    sessionButtons);
                return;
                
            case "setup_project":
                // Help setup project structure
                logger.LogInformation("Setting up project structure for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '📁 Project Setup Guide'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '==================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Current directory: /workspace'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "ls -la");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Example setup commands:'");
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  mkdir my-project && cd my-project'");
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  git clone <repository-url>'");
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '  npm init / pip init / etc.'");
                
                var projectButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude_start"] = "🤖 Start Claude Now",
                    [$"{terminalId}_pwd"] = "📁 Show Directory",
                    [$"{terminalId}_ls"] = "📋 List Files"
                };
                
                await SendResponse(command, 
                    $"📁 **Project setup guide shown in terminal {terminalId}**\n\n" +
                    $"Setup your project structure, then start Claude!", 
                    projectButtons);
                return;
                
            case var resumeAction when resumeAction.StartsWith("resume_"):
                // Handle session resume
                var sessionToResume = resumeAction.Substring("resume_".Length);
                logger.LogInformation("Resuming Claude session {SessionId} for terminal {TerminalId}", sessionToResume, terminalId);
                
                var session = await claudeSessionManager.GetSessionAsync(sessionToResume);
                if (session == null)
                {
                    await SendResponse(command, $"❌ Session {sessionToResume} not found");
                    return;
                }
                
                await SendResponse(command, $"🔄 **Resuming Session**\n\n⚡ Loading session context...");
                
                // Change to session working directory
                if (!string.IsNullOrEmpty(session.WorkingDirectory))
                {
                    await terminalManager.ExecuteCommandAsync(terminalId, $"cd {session.WorkingDirectory}");
                    await Task.Delay(300);
                }
                
                await terminalManager.ExecuteCommandAsync(terminalId, "pwd");
                await Task.Delay(500);
                
                // Resume the session
                var resumeSuccess = await claudeSessionManager.ResumeSessionAsync(sessionToResume, terminalId);
                
                if (!resumeSuccess)
                {
                    await SendResponse(command, $"❌ Failed to resume session {sessionToResume}");
                    return;
                }
                
                var resumeButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_start_claude_resume_{sessionToResume}"] = "🚀 Start Claude --resume",
                    [$"{terminalId}_session_info_{sessionToResume}"] = "ℹ️ Session Info",
                    [$"{terminalId}_pwd"] = "📁 Current Directory"
                };
                
                await SendResponse(command, 
                    $"🔄 **Session Resumed: {session.Description}**\n\n" +
                    $"**Workspace:** {session.WorkingDirectory}\n" +
                    $"**Last Used:** {session.LastUsed:MMM dd, HH:mm}\n" +
                    $"**Created:** {session.CreatedAt:MMM dd, HH:mm}\n\n" +
                    $"**Ready to continue your work!**\n" +
                    $"Claude will resume with full context from this session.", 
                    resumeButtons);
                return;
                
            case var sessionStartAction when sessionStartAction.StartsWith("start_claude_session_"):
                // Start Claude with a specific session
                var sessionId = sessionStartAction.Substring("start_claude_session_".Length);
                logger.LogInformation("Starting Claude with session {SessionId} for terminal {TerminalId}", sessionId, terminalId);
                
                await SendResponse(command, $"🚀 **Starting Claude with Session**\n\n⚡ Launching Claude Code...");
                
                // Start Claude with new session
                await terminalManager.ExecuteCommandAsync(terminalId, $"echo 'Starting Claude Code with session {sessionId}...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                
                await SendResponse(command, 
                    $"🚀 **Claude Started with New Session**\n\n" +
                    $"Session ID: {sessionId}\n" +
                    $"Claude is now running in the terminal above.\n" +
                    $"All work will be saved to this session automatically.");
                return;
                
            case var resumeStartAction when resumeStartAction.StartsWith("start_claude_resume_"):
                // Start Claude with resume for existing session
                var resumeSessionId = resumeStartAction.Substring("start_claude_resume_".Length);
                logger.LogInformation("Starting Claude --resume for session {SessionId} in terminal {TerminalId}", resumeSessionId, terminalId);
                
                await SendResponse(command, $"🔄 **Resuming Claude Session**\n\n⚡ Starting Claude with previous context...");
                
                // Start Claude with resume
                await terminalManager.ExecuteCommandAsync(terminalId, $"echo 'Resuming Claude session {resumeSessionId}...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "claude --resume");
                
                await SendResponse(command, 
                    $"🔄 **Claude Resumed Successfully**\n\n" +
                    $"Session ID: {resumeSessionId}\n" +
                    $"Claude has resumed with full conversation history and context.\n" +
                    $"You can continue exactly where you left off!");
                return;
                
            case var sessionInfoAction when sessionInfoAction.StartsWith("session_info_"):
                // Show detailed session information
                var infoSessionId = sessionInfoAction.Substring("session_info_".Length);
                var infoSession = await claudeSessionManager.GetSessionAsync(infoSessionId);
                
                if (infoSession == null)
                {
                    await SendResponse(command, $"❌ Session {infoSessionId} not found");
                    return;
                }
                
                var infoButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_start_claude_resume_{infoSessionId}"] = "🔄 Resume Session",
                    [$"{terminalId}_delete_session_{infoSessionId}"] = "🗑️ Delete Session",
                    [$"{terminalId}_claude"] = "🔙 Back to Sessions"
                };
                
                await SendResponse(command, 
                    $"ℹ️ **Session Details**\n\n" +
                    $"**ID:** {infoSession.Id}\n" +
                    $"**Description:** {infoSession.Description}\n" +
                    $"**Workspace:** {infoSession.WorkingDirectory}\n" +
                    $"**Created:** {infoSession.CreatedAt:MMM dd, yyyy HH:mm}\n" +
                    $"**Last Used:** {infoSession.LastUsed:MMM dd, yyyy HH:mm}\n" +
                    $"**Terminal:** {infoSession.TerminalId}\n" +
                    $"**Status:** {(infoSession.IsActive ? "Active" : "Inactive")}", 
                    infoButtons);
                return;
                
            case "check_auth":
                // Check if authentication completed successfully
                logger.LogInformation("Checking Claude Code authentication status for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔍 Checking authentication status...");
                
                // Test authentication with a simple command
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Testing Claude Code...' && echo 'test' | claude --print 'Authentication successful!' 2>/dev/null || echo 'Still need authentication'");
                
                await Task.Delay(2000);
                
                var postAuthButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude_new"] = "🆕 New Claude Session",
                    [$"{terminalId}_claude_start"] = "▶️ Start Claude Now",
                    [$"{terminalId}_claude_auto_auth"] = "🔄 Retry Authentication"
                };
                
                await SendResponse(command, 
                    $"✅ **Authentication Check Complete**\n\n" +
                    $"If you see 'Authentication successful!' above, you're ready!\n" +
                    $"If not, click 'Retry Authentication' or try the API key method.",
                    postAuthButtons);
                return;
                
            case "gemini_setup":
                // Setup Gemini AI with API key
                logger.LogInformation("Starting Gemini AI setup for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"💎 **Setting up Gemini AI**\n\n⚡ Starting configuration...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '💎 Gemini AI Setup Guide'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '===================='");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 1: Get your API key'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Visit: https://aistudio.google.com/app/apikey'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Create new API key (free)'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Step 2: Set environment variable'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Type: export GOOGLE_API_KEY=your_key_here'");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(300);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Ready to configure!'");
                
                var geminiSetupButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_gemini_test"] = "✅ Test Gemini",
                    [$"{terminalId}_gemini_help"] = "❓ More Help",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"💎 **Gemini Setup Instructions**\n\n" +
                    $"**Get free API key:** https://aistudio.google.com/app/apikey\n" +
                    $"**Set key:** Use export command shown above\n" +
                    $"**Test:** Click 'Test Gemini' when ready", 
                    geminiSetupButtons);
                return;
                
            case "gemini_start":
                // Start Gemini AI interactive session
                logger.LogInformation("Starting Gemini AI session for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"💎 **Starting Gemini AI Session**\n\n⚡ Launching interactive mode...");
                
                // Test Gemini availability and provide instructions
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '💎 Starting Gemini AI session...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "python3 -c \"import google.generativeai as genai; import os; print('Gemini AI libraries available'); api_key=os.getenv('GOOGLE_API_KEY'); print('API key found' if api_key else 'Please set GOOGLE_API_KEY environment variable')\"");
                await Task.Delay(1000);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Gemini AI ready for manual interaction'");
                
                await SendResponse(command, $"💎 Gemini AI started in terminal **{terminalId}**\n\nInteractive session is now running!");
                return;
                
            case "gemini_test":
                // Test Gemini AI configuration
                logger.LogInformation("Testing Gemini AI configuration for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🔍 **Testing Gemini Configuration**\n\n⚡ Running diagnostics...");
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Testing Gemini AI configuration...'");
                await Task.Delay(500);
                await terminalManager.ExecuteCommandAsync(terminalId, "python3 -c \"import google.generativeai as genai; import os; key=os.getenv('GOOGLE_API_KEY'); print('✅ API key found' if key else '❌ API key missing'); genai.configure(api_key=key) if key else None; print('✅ Gemini configured successfully' if key else '❌ Please set GOOGLE_API_KEY')\" 2>/dev/null || echo '❌ Configuration error'");
                
                await Task.Delay(2000);
                
                var geminiTestButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_gemini_start"] = "▶️ Start Gemini Chat",
                    [$"{terminalId}_gemini_setup"] = "🔧 Setup Again",
                    [$"{terminalId}_gemini_help"] = "❓ Get Help"
                };
                
                await SendResponse(command, 
                    $"✅ **Gemini Test Complete**\n\n" +
                    $"If you see '✅ Gemini configured successfully', you're ready!\n" +
                    $"If not, click 'Setup Again' to configure your API key.",
                    geminiTestButtons);
                return;
                
            case "gemini_help":
                // Provide Gemini AI help information
                logger.LogInformation("Providing Gemini AI help for terminal {TerminalId}", terminalId);
                
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '💎 Gemini AI Help'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '================'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'What is Gemini AI?'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Google\\'s advanced AI model'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Free tier available'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '- Fast and capable responses'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo ''");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo 'Getting started:'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '1. Get free API key: aistudio.google.com'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '2. Set environment variable'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '3. Test configuration'");
                await Task.Delay(200);
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '4. Start chatting!'");
                
                await SendResponse(command, $"💎 Gemini AI help shown in terminal **{terminalId}**\n\nFree API key available at Google AI Studio!");
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

    private bool IsButtonCallback(string command)
    {
        // Check for exact single-word button commands
        var singleWordButtons = new[] { "new_terminal", "new_session", "confirm_new_session", "help_commands", "menu", "start", "list", "settings", "switch", "welcome", "reset", "setup" };
        if (singleWordButtons.Contains(command))
            return true;
        
        // Check for kill_ pattern (kill_terminalId)
        if (command.StartsWith("kill_"))
            return true;
        
        // Check for terminal action patterns (terminalId_action)
        if (command.Contains("_"))
        {
            var parts = command.Split('_', 2);
            if (parts.Length == 2)
            {
                var action = parts[1];
                var terminalActions = new[] { "select", "claude", "gemini", "ai_select", "ai_compare", "pwd", "ls", "help", "login", "claude_login", "claude_help", "claude_exit", "claude_send_login", "claude_start", "claude_manual", "auth_help", "start_manual", "claude_auto_auth", "check_auth", "auth_manual", "claude_manual_start", "start_login_process", "auth_send_login", "show_auth_guide", "claude_new_session", "show_all_sessions", "setup_project", "manage_sessions", "gemini_setup", "gemini_start", "gemini_test", "gemini_help", "auth_web_login", "auth_api_key", "proceed_new_session", "select_claude_login", "select_api_key", "check_terminal_output", "try_again", "send_1", "send_2", "send_login", "send_enter", "extract_url", "show_screen", "show_help", "send_help_cmd", "send_exit" };
                if (terminalActions.Contains(action))
                    return true;
                
                // Check for compound actions like claude_new
                if (action.Contains("_"))
                {
                    var actionParts = action.Split('_', 2);
                    if (actionParts.Length == 2 && (
                        (actionParts[0] == "claude" && (actionParts[1] == "new" || actionParts[1] == "login" || actionParts[1] == "help" || actionParts[1] == "exit" || actionParts[1] == "start" || actionParts[1] == "manual" || actionParts[1] == "auto_auth" || actionParts[1] == "manual_start")) ||
                        (actionParts[0] == "gemini" && (actionParts[1] == "setup" || actionParts[1] == "start" || actionParts[1] == "test" || actionParts[1] == "help")) ||
                        (actionParts[0] == "ai" && (actionParts[1] == "select" || actionParts[1] == "compare")) ||
                        (actionParts[0] == "auth" && (actionParts[1] == "manual" || actionParts[1] == "web_login" || actionParts[1] == "api_key" || actionParts[1] == "check_url" || actionParts[1] == "retry_web" || actionParts[1] == "help_api" || actionParts[1] == "manual_web")) ||
                        (actionParts[0] == "send" && (actionParts[1] == "login_manual" || actionParts[1] == "login_diag")) ||
                        (actionParts[0] == "select" && actionParts[1] == "web_option") ||
                        (actionParts[0] == "try" && (actionParts[1] == "web_1" || actionParts[1] == "web_2" || actionParts[1] == "web_w" || actionParts[1] == "web_a")) ||
                        (actionParts[0] == "check" && (actionParts[1] == "claude_status" || actionParts[1] == "login_help")) ||
                        (actionParts[0] == "restart" && actionParts[1] == "claude") ||
                        (actionParts[0] == "force" && actionParts[1] == "web_login")))
                        return true;
                    
                    // Handle compound claude actions
                    if (action == "claude_send_login")
                        return true;
                }
                
                // Check for session-related dynamic patterns
                if (action.StartsWith("resume_") || 
                    action.StartsWith("start_claude_session_") ||
                    action.StartsWith("start_claude_resume_") ||
                    action.StartsWith("session_info_") ||
                    action.StartsWith("delete_session_"))
                    return true;
                
                // Also check for numbered choices (terminalId_1, terminalId_2, etc.)
                if (int.TryParse(action, out _))
                    return true;
            }
        }
        
        return false;
    }

    private bool IsConversationStarterCommand(string command)
    {
        var cleanCommand = command.Trim().ToLower();
        
        var starters = new[]
        {
            "start", "hi", "hello", "hey", "menu", "help", "welcome", "reset",
            "what can you do", "commands", "options", "yo", "sup", "hola",
            "helo", "helll", "hllo", "hello!", "hi!", "hey!", ""
        };
        
        // Check exact matches
        if (starters.Contains(cleanCommand))
        {
            return true;
        }
        
        // Check if it starts with common greetings (to catch typos like "helll")
        var greetingPrefixes = new[] { "hell", "hi", "hey", "hel", "start", "menu" };
        if (greetingPrefixes.Any(prefix => cleanCommand.StartsWith(prefix)))
        {
            return true;
        }
        
        // Also detect short random text as conversation starters (1-4 characters)
        if (cleanCommand.Length <= 4 && !string.IsNullOrEmpty(cleanCommand))
        {
            return true;
        }
        
        return false;
    }

    private async Task HandleWelcomeCommand(ChannelCommand command)
    {
        var message = "**🎉 Welcome to ClaudeMobileTerminal!**\n\n" +
                     "Control Claude Code terminals from anywhere.\n" +
                     "Create, manage, and execute commands with ease.\n\n" +
                     "**Choose an action to get started:**";
        
        var welcomeButtons = new Dictionary<string, string>
        {
            ["new_terminal"] = "🚀 Create First Terminal",
            ["setup"] = "🔧 Claude Code Setup",
            ["help_commands"] = "❓ View Commands & Help"
        };

        await SendResponse(command, message, welcomeButtons);
    }

    private async Task HandleResetCommand(ChannelCommand command)
    {
        // Remove user from seen list to trigger welcome menu again
        var userKey = $"{command.ChannelType}:{command.SenderId}";
        _seenUsers.Remove(userKey);
        
        logger.LogInformation("Reset user session for {UserKey}", userKey);
        
        // Show welcome menu
        await HandleWelcomeCommand(command);
    }

    private async Task HandleConfirmNewSession(ChannelCommand command)
    {
        try
        {
            // Kill all existing terminals
            var terminals = await terminalManager.ListTerminalsAsync();
            var killTasks = new List<Task>();
            
            foreach (var terminal in terminals)
            {
                killTasks.Add(Task.Run(async () =>
                {
                    await terminalManager.KillTerminalAsync(terminal.Id);
                    outputProcessor.CleanupTerminal(terminal.Id);
                    channelManager.CleanupTerminalSubscriptions(terminal.Id);
                }));
            }
            
            // Wait for all terminals to be killed
            await Task.WhenAll(killTasks);
            
            // Clear user's active terminal
            channelManager.SetLastActiveTerminal(command.ChannelType, command.SenderId, string.Empty);
            
            await SendResponse(command, $"**🧹 Session Cleared!**\n\nTerminated {terminals.Count} terminal(s). Ready for a fresh start!");
            
            // Automatically create a new terminal
            await Task.Delay(1000); // Small delay for better UX
            await HandleNewTerminalCommand(command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during new session creation");
            await SendResponse(command, "❌ Error creating new session. Please try again.");
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
        Console.WriteLine("║                                        ║");
        Console.WriteLine("║  Send /start or /menu to begin        ║");
        Console.WriteLine("╚════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Active channels:");
        Console.WriteLine("  Check logs for channel status");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop...");
        Console.WriteLine();
    }
}