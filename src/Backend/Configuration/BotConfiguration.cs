namespace ClaudeMobileTerminal.Backend.Configuration;

public class BotConfiguration
{
    public TelegramConfiguration Telegram { get; set; } = new();
    public DiscordConfiguration Discord { get; set; } = new();
    public WhatsAppConfiguration WhatsApp { get; set; } = new();
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

public class WhatsAppConfiguration
{
    public bool Enabled { get; set; }
    public string WAHAUrl { get; set; } = "http://localhost:3000";
    public string SessionName { get; set; } = "default";
    public string WebhookUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public List<string> AllowedNumbers { get; set; } = new();
    public bool UseWebhook { get; set; } = false;
    public int RequestTimeout { get; set; } = 30;
}