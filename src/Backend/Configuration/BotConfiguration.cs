namespace ClaudeMobileTerminal.Backend.Configuration;

public class BotConfiguration
{
    public TelegramConfiguration Telegram { get; set; } = new();
    public DiscordConfiguration Discord { get; set; } = new();
}

public class TelegramConfiguration
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public List<long> AllowedChatIds { get; set; } = new();
}

public class DiscordConfiguration
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public List<ulong> AllowedGuildIds { get; set; } = new();
    public List<ulong> AllowedChannelIds { get; set; } = new();
}