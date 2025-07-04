namespace ClaudeMobileTerminal.Backend.Services;

public static class TerminalCommands
{
    // Special commands that can be sent to terminals
    public const string SendEnter = "!enter";
    public const string SendCtrlC = "!ctrlc";
    public const string SendCtrlD = "!ctrld";
    public const string SendTab = "!tab";
    public const string SendEscape = "!esc";
}