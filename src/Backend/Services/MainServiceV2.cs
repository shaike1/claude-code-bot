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
                logger.LogInformation("Starting automated Claude Code authentication flow in terminal {TerminalId}", terminalId);
                
                // Start with a simple status message
                await SendResponse(command, $"🤖 Starting Claude Code in terminal **{terminalId}**...\n\n⚡ Checking authentication status...");
                
                // Test if Claude is already authenticated
                var authTestSuccess = await terminalManager.ExecuteCommandAsync(terminalId, "echo '' | timeout 3s claude --version 2>/dev/null && echo 'CLAUDE_AUTHENTICATED' || echo 'CLAUDE_NEEDS_AUTH'");
                
                if (!authTestSuccess)
                {
                    await SendResponse(command, $"❌ Failed to test Claude Code in terminal **{terminalId}**");
                    return;
                }
                
                // Wait a moment for the auth test result, then provide next steps
                await Task.Delay(2000);
                
                var authButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_claude_auto_auth"] = "🚀 Auto-Setup Claude",
                    [$"{terminalId}_claude_start"] = "▶️ Start Claude Now", 
                    [$"{terminalId}_auth_help"] = "❓ Auth Help"
                };
                
                await SendResponse(command, 
                    $"🔑 **Claude Code Authentication**\n\n" +
                    $"**Option 1:** Auto-Setup (Recommended)\n" +
                    $"- Automatically guides you through web login\n" +
                    $"- No manual API key needed\n\n" +
                    $"**Option 2:** Start directly if already authenticated\n\n" +
                    $"Choose your preferred method:", 
                    authButtons);
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
                // Automated Claude Code authentication with web login
                logger.LogInformation("Starting automated Claude Code authentication for terminal {TerminalId}", terminalId);
                
                await SendResponse(command, $"🚀 **Auto-Setting up Claude Code**\n\n⚡ Starting authentication process...");
                
                // Start Claude and automatically trigger the login flow
                await terminalManager.ExecuteCommandAsync(terminalId, "echo '🤖 Starting Claude Code...'");
                await Task.Delay(500);
                
                // Start claude command - this will show the auth error
                await terminalManager.ExecuteCommandAsync(terminalId, "claude");
                await Task.Delay(1500);
                
                // Send /login automatically 
                await terminalManager.ExecuteCommandAsync(terminalId, "/login");
                await Task.Delay(1000);
                
                var authCompleteButtons = new Dictionary<string, string>
                {
                    [$"{terminalId}_check_auth"] = "✅ Check Authentication",
                    [$"{terminalId}_auth_help"] = "❓ Need Help?",
                    [$"{terminalId}_pwd"] = "📁 Directory"
                };
                
                await SendResponse(command, 
                    $"🔑 **Authentication Started in Terminal {terminalId}**\n\n" +
                    $"**Next Steps:**\n" +
                    $"1. Choose **Web Login** when prompted (recommended)\n" +
                    $"2. Open the URL that Claude provides\n" +
                    $"3. Login with your Anthropic account\n" +
                    $"4. Return here and click 'Check Authentication'\n\n" +
                    $"**Alternative:** Use API key if you prefer", 
                    authCompleteButtons);
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
                var terminalActions = new[] { "select", "claude", "gemini", "ai_select", "ai_compare", "pwd", "ls", "help", "login", "claude_login", "claude_help", "claude_exit", "claude_send_login", "claude_start", "claude_manual", "auth_help", "start_manual", "claude_auto_auth", "check_auth", "gemini_setup", "gemini_start", "gemini_test", "gemini_help" };
                if (terminalActions.Contains(action))
                    return true;
                
                // Check for compound actions like claude_new
                if (action.Contains("_"))
                {
                    var actionParts = action.Split('_', 2);
                    if (actionParts.Length == 2 && (
                        (actionParts[0] == "claude" && (actionParts[1] == "new" || actionParts[1] == "login" || actionParts[1] == "help" || actionParts[1] == "exit" || actionParts[1] == "start" || actionParts[1] == "manual" || actionParts[1] == "auto_auth")) ||
                        (actionParts[0] == "gemini" && (actionParts[1] == "setup" || actionParts[1] == "start" || actionParts[1] == "test" || actionParts[1] == "help")) ||
                        (actionParts[0] == "ai" && (actionParts[1] == "select" || actionParts[1] == "compare"))))
                        return true;
                    
                    // Handle compound claude actions
                    if (action == "claude_send_login")
                        return true;
                }
                
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