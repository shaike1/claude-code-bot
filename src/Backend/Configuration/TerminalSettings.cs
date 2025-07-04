namespace ClaudeMobileTerminal.Backend.Configuration;

public class TerminalSettings
{
    public bool AutoSelectFirstOption { get; set; }
    public int MaxTerminals { get; set; } = 10;
    public int TerminalTimeout { get; set; } = 3600;
    public bool ShowTerminalWindow { get; set; } = true;
}

public class ClaudeHooksSettings
{
    public int WebSocketPort { get; set; } = 8765;
    public bool EnableHooks { get; set; } = true;
}

public class InteractiveAppSettings
{
    public bool AutoSelectFirstOption { get; set; }
    public int MaxTerminals { get; set; } = 10;
    public int TerminalTimeout { get; set; } = 3600;
    public List<InteractiveAppConfig> Apps { get; set; } = new();
}

public class InteractiveAppConfig
{
    public bool AutoSelectFirstOption { get; set; }
    public int MaxTerminals { get; set; } = 10;
    public int TerminalTimeout { get; set; } = 3600;
    public InputMethod InputMethod { get; set; } = InputMethod.Direct;
    public int CharacterDelay { get; set; } = 0;
    public OutputProcessing OutputProcessing { get; set; } = new();
    public string Value { get; set; } = "";
    public List<string> DetectionKeywords { get; set; } = new();
}

public class OutputProcessing
{
    public bool EnableProcessing { get; set; } = true;
    public int MaxOutputLines { get; set; } = 1000;
    public int MaxPaginationAttempts { get; set; } = 3;
    public int PaginationDelayMs { get; set; } = 1000;
    public bool AutoHandlePagination { get; set; } = true;
    public string PaginationPrompt { get; set; } = "Press any key to continue...";
}

public enum InputMethod
{
    Direct,
    Simulated,
    Paste,
    CharacterByCharacter,
    WithDelay,
    Standard
}