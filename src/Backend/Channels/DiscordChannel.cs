using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Channels;

public class DiscordChannel(ILogger<DiscordChannel> logger, IOptions<BotConfiguration> config) : ClaudeMobileTerminal.Backend.Interfaces.IMessageChannel
{
    private readonly DiscordConfiguration _config = config.Value.Discord;
    private DiscordSocketClient? _client;
    private CancellationTokenSource? _cancellationTokenSource;

    public string ChannelType => "Discord";
    public bool IsEnabled => _config.Enabled && !string.IsNullOrWhiteSpace(_config.BotToken);
    public event EventHandler<ChannelCommand>? CommandReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            logger.LogInformation("Discord channel is disabled or not configured");
            return;
        }

        try
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                MessageCacheSize = 100
            });

            _client.Log += LogAsync;
            _client.MessageReceived += MessageReceivedAsync;
            _client.InteractionCreated += InteractionCreatedAsync;

            await _client.LoginAsync(TokenType.Bot, _config.BotToken);
            await _client.StartAsync();

            logger.LogInformation("Discord bot started successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Discord bot");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            await _client.StopAsync();
            await _client.DisposeAsync();
        }
        
        _cancellationTokenSource?.Cancel();
    }

    public async Task SendMessageAsync(string recipient, string message, Dictionary<string, string>? buttons = null)
    {
        if (_client == null) return;

        try
        {
            if (!ulong.TryParse(recipient, out var channelId))
            {
                logger.LogWarning("Invalid channel ID: {Recipient}", recipient);
                return;
            }

            var channel = _client.GetChannel(channelId) as Discord.IMessageChannel;
            if (channel == null)
            {
                logger.LogWarning("Channel not found: {ChannelId}", channelId);
                return;
            }

            if (!IsAuthorizedChannel(channelId))
            {
                logger.LogWarning("Unauthorized channel: {ChannelId}", channelId);
                return;
            }

            var embed = new EmbedBuilder()
                .WithDescription(message)
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            ComponentBuilder? components = null;
            
            if (buttons != null && buttons.Count > 0)
            {
                components = new ComponentBuilder();
                var row = new ActionRowBuilder();
                
                int buttonCount = 0;
                foreach (var button in buttons)
                {
                    if (buttonCount >= 5)
                    {
                        components.AddRow(row);
                        row = new ActionRowBuilder();
                        buttonCount = 0;
                    }
                    
                    row.WithButton(button.Value, button.Key, ButtonStyle.Primary);
                    buttonCount++;
                }
                
                if (buttonCount > 0)
                {
                    components.AddRow(row);
                }
            }

            await channel.SendMessageAsync(
                embed: embed,
                components: components?.Build()
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord message to {Recipient}", recipient);
        }
    }

    private Task LogAsync(LogMessage log)
    {
        switch (log.Severity)
        {
            case LogSeverity.Critical:
            case LogSeverity.Error:
                logger.LogError(log.Exception, "[Discord] {Message}", log.Message);
                break;
            case LogSeverity.Warning:
                logger.LogWarning("[Discord] {Message}", log.Message);
                break;
            case LogSeverity.Info:
                logger.LogInformation("[Discord] {Message}", log.Message);
                break;
            case LogSeverity.Verbose:
            case LogSeverity.Debug:
                logger.LogDebug("[Discord] {Message}", log.Message);
                break;
        }
        
        return Task.CompletedTask;
    }

    private Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return Task.CompletedTask;
        
        var channel = message.Channel;
        if (!IsAuthorizedChannel(channel.Id) || !IsAuthorizedGuild(message))
        {
            return Task.CompletedTask;
        }

        ProcessCommand(channel.Id.ToString(), message.Content);
        
        return Task.CompletedTask;
    }

    private async Task InteractionCreatedAsync(SocketInteraction interaction)
    {
        if (interaction is SocketMessageComponent component)
        {
            if (!IsAuthorizedChannel(component.Channel.Id) || !IsAuthorizedGuildInteraction(component))
            {
                await component.RespondAsync("Unauthorized", ephemeral: true);
                return;
            }

            await component.DeferAsync();
            
            ProcessCommand(component.Channel.Id.ToString(), component.Data.CustomId);
        }
    }

    private void ProcessCommand(string channelId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var command = parts[0].TrimStart('/');
        var args = parts.Skip(1).ToArray();

        var channelCommand = new ChannelCommand
        {
            ChannelType = ChannelType,
            SenderId = channelId,
            Command = command,
            Arguments = args,
            RawText = text
        };

        CommandReceived?.Invoke(this, channelCommand);
    }

    private bool IsAuthorizedChannel(ulong channelId)
    {
        return _config.AllowedChannelIds.Count == 0 || _config.AllowedChannelIds.Contains(channelId);
    }

    private bool IsAuthorizedGuild(SocketMessage message)
    {
        if (message.Channel is SocketGuildChannel guildChannel)
        {
            return _config.AllowedGuildIds.Count == 0 || _config.AllowedGuildIds.Contains(guildChannel.Guild.Id);
        }
        return true;
    }

    private bool IsAuthorizedGuildInteraction(SocketMessageComponent component)
    {
        if (component.GuildId.HasValue)
        {
            return _config.AllowedGuildIds.Count == 0 || _config.AllowedGuildIds.Contains(component.GuildId.Value);
        }
        return true;
    }
}