namespace ClaudeMobileTerminal.Backend.Interfaces;

public interface IMessageChannel
{
    string ChannelType { get; }
    bool IsEnabled { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SendMessageAsync(string recipient, string message, Dictionary<string, string>? buttons = null);
    event EventHandler<ChannelCommand>? CommandReceived;
}

public class ChannelCommand
{
    public string ChannelType { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string[] Arguments { get; set; } = Array.Empty<string>();
    public string RawText { get; set; } = string.Empty;
}

public class ChannelMessage
{
    public string ChannelType { get; set; } = string.Empty;
    public string RecipientId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Dictionary<string, string>? Buttons { get; set; }
}