using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services;

public interface ITerminalManager
{
    Task<Terminal> CreateTerminalAsync(string? customId = null);
    Task<bool> ExecuteCommandAsync(string terminalId, string command);
    Task<bool> KillTerminalAsync(string terminalId);
    Task<bool> RenameTerminalAsync(string oldId, string newId);
    Task<List<Terminal>> ListTerminalsAsync();
    Task<Terminal?> GetTerminalAsync(string terminalId);
    Task<bool> SendChoiceAsync(string terminalId, int choice);
    void SetShuttingDown(bool isShuttingDown);
    IEnumerable<Terminal> GetActiveTerminals();
    event EventHandler<TerminalMessage>? TerminalMessageReceived;
}