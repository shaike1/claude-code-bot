namespace ClaudeMobileTerminal.Backend.Services;

public interface IClaudeHooksService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    event EventHandler<ClaudeHookEvent>? HookEventReceived;
}

public class ClaudeHookEvent
{
    public string EventType { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string? Response { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}