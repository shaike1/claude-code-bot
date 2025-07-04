namespace ClaudeMobileTerminal.Backend.Models;

public class Terminal
{
    public string Id { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LastActivity { get; set; }
    public TerminalStatus Status { get; set; }
    public string? CurrentTask { get; set; }
    public List<string> CommandHistory { get; set; } = new();
    public Dictionary<long, string> PendingChoices { get; set; } = new();
    
    public bool IsActive => Status != TerminalStatus.Terminated;
    public DateTime CreatedAt => StartTime;
}

public enum TerminalStatus
{
    Idle,
    Running,
    WaitingForInput,
    Terminated
}

public class TerminalMessage
{
    public string TerminalId { get; set; } = string.Empty;
    public MessageType Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public List<string>? Options { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum MessageType
{
    Output,
    Error,
    Question,
    Started,
    Completed,
    Status
}