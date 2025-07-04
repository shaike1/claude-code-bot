using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;
using ClaudeMobileTerminal.Backend.Services;

namespace ClaudeMobileTerminal.Backend.Channels;

public class TelegramChannel(ILogger<TelegramChannel> logger, IOptions<BotConfiguration> config) : IMessageChannel
{
    private readonly TelegramConfiguration _config = config.Value.Telegram;
    private readonly TelegramMarkdownConverter _markdownConverter = new();
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _cancellationTokenSource;

    public string ChannelType => "Telegram";
    public bool IsEnabled => _config.Enabled && !string.IsNullOrWhiteSpace(_config.BotToken);
    public event EventHandler<ChannelCommand>? CommandReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            logger.LogInformation("Telegram channel is disabled or not configured");
            return;
        }

        try
        {
            _botClient = new TelegramBotClient(_config.BotToken);
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var me = await _botClient.GetMeAsync(cancellationToken);
            logger.LogInformation("Telegram bot started: @{Username}", me.Username);

            // Set up bot commands for the menu
            try
            {
                var commands = new[]
                {
                    new BotCommand { Command = "start", Description = "🚀 Show main menu with action buttons" },
                    new BotCommand { Command = "new", Description = "➕ Create new terminal" },
                    new BotCommand { Command = "list", Description = "📋 List all terminals" },
                    new BotCommand { Command = "switch", Description = "🔄 Switch between terminals" },
                    new BotCommand { Command = "session", Description = "🆕 Start new session" },
                    new BotCommand { Command = "help", Description = "❓ Show help and commands" }
                };
                
                await _botClient.SetMyCommandsAsync(commands, cancellationToken: cancellationToken);
                logger.LogInformation("Set bot commands for menu");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to set bot commands, continuing without them");
            }

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _cancellationTokenSource.Token
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Telegram bot");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync(string recipient, string message, Dictionary<string, string>? buttons = null)
    {
        if (_botClient == null || _cancellationTokenSource?.IsCancellationRequested == true) return;

        IReplyMarkup? replyMarkup = null;
        string messageText = "";
        CancellationTokenSource? cts = null;
        CancellationTokenSource? linkedCts = null;

        try
        {
            var chatIdLong = long.Parse(recipient);
            
            if (!IsAuthorizedChat(chatIdLong))
            {
                logger.LogWarning("Unauthorized chat attempt: {ChatId}", recipient);
                return;
            }
            
            if (buttons != null && buttons.Count > 0)
            {
                logger.LogDebug("Creating {Count} buttons for message", buttons.Count);
                
                var keyboardButtons = new List<InlineKeyboardButton>();
                
                foreach (var button in buttons)
                {
                    try
                    {
                        // Ensure button text and callback data are not too long
                        var buttonText = button.Value.Length > 64 ? button.Value.Substring(0, 61) + "..." : button.Value;
                        var callbackData = button.Key.Length > 64 ? button.Key.Substring(0, 64) : button.Key;
                        
                        keyboardButtons.Add(InlineKeyboardButton.WithCallbackData(buttonText, callbackData));
                        logger.LogDebug("Created button: {Text} -> {Callback}", buttonText, callbackData);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error creating button {Key}:{Value}", button.Key, button.Value);
                    }
                }
                
                if (keyboardButtons.Count > 0)
                {
                    // Create rows with 1-2 buttons per row for better mobile experience
                    var rows = new List<InlineKeyboardButton[]>();
                    for (int i = 0; i < keyboardButtons.Count; i += 2)
                    {
                        if (i + 1 < keyboardButtons.Count)
                        {
                            rows.Add([keyboardButtons[i], keyboardButtons[i + 1]]);
                        }
                        else
                        {
                            rows.Add([keyboardButtons[i]]);
                        }
                    }
                    
                    replyMarkup = new InlineKeyboardMarkup(rows);
                    logger.LogDebug("Created inline keyboard with {RowCount} rows", rows.Count);
                }
            }

            messageText = _markdownConverter.ConvertToTelegramMarkdownV2(message);
            
            // Telegram has a 4096 character limit for messages
            const int maxLength = 4000; // Leave some buffer for safety
            
            // Use a shorter timeout during shutdown
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cts.Token, 
                _cancellationTokenSource?.Token ?? CancellationToken.None
            );
            
            // Split long messages
            var messageParts = SplitLongMessage(messageText, maxLength);
            
            for (int i = 0; i < messageParts.Count; i++)
            {
                var part = messageParts[i];
                var isLastPart = i == messageParts.Count - 1;
                
                logger.LogDebug("Sending Telegram message part {Part}/{Total}: {Message}", 
                    i + 1, messageParts.Count, part);
                
                await _botClient.SendTextMessageAsync(
                    chatId: chatIdLong,
                    text: part,
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: isLastPart ? replyMarkup : null, // Only add buttons to last message
                    cancellationToken: linkedCts.Token
                );
                
                // Small delay between messages to avoid rate limiting
                if (!isLastPart)
                {
                    await Task.Delay(100, linkedCts.Token);
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Expected during shutdown
            logger.LogDebug("Message send cancelled during shutdown");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogError(ex, "Telegram formatting error. Original message: {Message}, Formatted: {FormattedMessage}", 
                message, messageText);
            
            // Fallback: send as plain text
            try
            {
                await _botClient.SendTextMessageAsync(
                    chatId: long.Parse(recipient),
                    text: message, // Send original unformatted message
                    replyMarkup: replyMarkup,
                    cancellationToken: linkedCts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception fallbackEx)
            {
                logger.LogError(fallbackEx, "Failed to send fallback message");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram message to {Recipient}", recipient);
        }
        finally
        {
            cts?.Dispose();
            linkedCts?.Dispose();
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Message is { } message && message.Text is { } messageText)
            {
                var chatId = message.Chat.Id;
                
                if (!IsAuthorizedChat(chatId))
                {
                    await botClient.SendTextMessageAsync(chatId, "Unauthorized access", cancellationToken: cancellationToken);
                    return;
                }

                logger.LogInformation("Received message from {ChatId}: {Text}", chatId, messageText);
                
                // Check if this is the very first message in the chat
                if (messageText == "/start" && message.Chat.Type == ChatType.Private)
                {
                    // Send immediate welcome with buttons for new private chats
                    ProcessCommand(chatId.ToString(), "start");
                }
                else
                {
                    ProcessCommand(chatId.ToString(), messageText);
                }
            }
            else if (update.CallbackQuery is { } callbackQuery)
            {
                var chatId = callbackQuery.Message!.Chat.Id;
                
                logger.LogInformation("Received callback query from {ChatId}: {Data}", chatId, callbackQuery.Data);
                
                if (!IsAuthorizedChat(chatId))
                {
                    await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Unauthorized", cancellationToken: cancellationToken);
                    return;
                }

                await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                
                ProcessCommand(chatId.ToString(), callbackQuery.Data ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Telegram update");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        logger.LogError("Telegram polling error: {Error}", errorMessage);
        return Task.CompletedTask;
    }

    private void ProcessCommand(string chatId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Auto-show menu for common conversation starters
        if (IsConversationStarter(text))
        {
            var startCommand = new ChannelCommand
            {
                ChannelType = ChannelType,
                SenderId = chatId,
                Command = "start",
                Arguments = Array.Empty<string>(),
                RawText = "/start"
            };
            CommandReceived?.Invoke(this, startCommand);
            return;
        }

        // Check if this is an escaped command (// or ./)
        if (text.StartsWith("//") || text.StartsWith("./"))
        {
            // Treat as plain text command
            var escapedCommand = new ChannelCommand
            {
                ChannelType = ChannelType,
                SenderId = chatId,
                Command = text, // Keep the full text as command
                Arguments = Array.Empty<string>(),
                RawText = text
            };

            CommandReceived?.Invoke(this, escapedCommand);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var command = parts[0].TrimStart('/');
        var args = parts.Skip(1).ToArray();

        var channelCommand = new ChannelCommand
        {
            ChannelType = ChannelType,
            SenderId = chatId,
            Command = command,
            Arguments = args,
            RawText = text
        };

        CommandReceived?.Invoke(this, channelCommand);
    }

    private bool IsConversationStarter(string text)
    {
        var starters = new[]
        {
            "/start", "start", "hi", "hello", "hey", "menu", "/menu",
            "help", "what can you do", "commands", "options"
        };
        
        return starters.Any(starter => 
            text.Trim().Equals(starter, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAuthorizedChat(long chatId)
    {
        return _config.AllowedChatIds.Count == 0 || _config.AllowedChatIds.Contains(chatId);
    }
    
    private List<string> SplitLongMessage(string message, int maxLength)
    {
        var parts = new List<string>();
        
        if (message.Length <= maxLength)
        {
            parts.Add(message);
            return parts;
        }
        
        // Try to split on newlines first
        var lines = message.Split('\n');
        var currentPart = new StringBuilder();
        var currentLength = 0;
        
        foreach (var line in lines)
        {
            var lineWithNewline = line + "\n";
            
            // If adding this line would exceed the limit, save current part
            if (currentLength + lineWithNewline.Length > maxLength && currentLength > 0)
            {
                // Remove trailing newline
                if (currentPart.Length > 0 && currentPart[currentPart.Length - 1] == '\n')
                {
                    currentPart.Length--;
                }
                
                parts.Add(currentPart.ToString());
                currentPart.Clear();
                currentLength = 0;
            }
            
            // If a single line is too long, we need to split it
            if (lineWithNewline.Length > maxLength)
            {
                // Save any accumulated content first
                if (currentLength > 0)
                {
                    if (currentPart.Length > 0 && currentPart[currentPart.Length - 1] == '\n')
                    {
                        currentPart.Length--;
                    }
                    parts.Add(currentPart.ToString());
                    currentPart.Clear();
                    currentLength = 0;
                }
                
                // Split the long line at word boundaries
                var words = line.Split(' ');
                var wordPart = new StringBuilder();
                
                foreach (var word in words)
                {
                    if (wordPart.Length > 0 && wordPart.Length + word.Length + 1 > maxLength)
                    {
                        parts.Add(wordPart.ToString());
                        wordPart.Clear();
                    }
                    
                    if (wordPart.Length > 0)
                    {
                        wordPart.Append(' ');
                    }
                    wordPart.Append(word);
                }
                
                if (wordPart.Length > 0)
                {
                    currentPart.Append(wordPart);
                    currentPart.Append('\n');
                    currentLength = wordPart.Length + 1;
                }
            }
            else
            {
                currentPart.Append(lineWithNewline);
                currentLength += lineWithNewline.Length;
            }
        }
        
        // Add any remaining content
        if (currentLength > 0)
        {
            // Remove trailing newline
            if (currentPart.Length > 0 && currentPart[currentPart.Length - 1] == '\n')
            {
                currentPart.Length--;
            }
            parts.Add(currentPart.ToString());
        }
        
        // Add part indicators if there are multiple parts
        if (parts.Count > 1)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i] = $"[Part {i + 1}/{parts.Count}]\n{parts[i]}";
            }
        }
        
        return parts;
    }

}