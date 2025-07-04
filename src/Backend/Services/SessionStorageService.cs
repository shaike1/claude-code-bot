using System.Text.Json;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services;

public class SessionStorageService
{
    private readonly ILogger<SessionStorageService> _logger;
    private readonly string _sessionsFilePath;
    private readonly string _terminalsFilePath;

    public SessionStorageService(ILogger<SessionStorageService> logger)
    {
        _logger = logger;
        _sessionsFilePath = "/app/data/user_sessions.json";
        _terminalsFilePath = "/app/data/terminal_sessions.json";
        
        // Ensure data directory exists
        Directory.CreateDirectory("/app/data");
    }

    public async Task SaveUserSessionsAsync(HashSet<string> seenUsers)
    {
        try
        {
            var json = JsonSerializer.Serialize(seenUsers.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_sessionsFilePath, json);
            _logger.LogDebug("Saved {Count} user sessions to {Path}", seenUsers.Count, _sessionsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user sessions");
        }
    }

    public async Task<HashSet<string>> LoadUserSessionsAsync()
    {
        try
        {
            if (!File.Exists(_sessionsFilePath))
            {
                _logger.LogInformation("No existing user sessions file found");
                return new HashSet<string>();
            }

            var json = await File.ReadAllTextAsync(_sessionsFilePath);
            var users = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            var result = new HashSet<string>(users);
            
            _logger.LogInformation("Loaded {Count} user sessions from {Path}", result.Count, _sessionsFilePath);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user sessions, starting fresh");
            return new HashSet<string>();
        }
    }

    public async Task SaveTerminalSessionsAsync(Dictionary<string, TerminalSessionData> terminalSessions)
    {
        try
        {
            var json = JsonSerializer.Serialize(terminalSessions, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_terminalsFilePath, json);
            _logger.LogDebug("Saved {Count} terminal sessions to {Path}", terminalSessions.Count, _terminalsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save terminal sessions");
        }
    }

    public async Task<Dictionary<string, TerminalSessionData>> LoadTerminalSessionsAsync()
    {
        try
        {
            if (!File.Exists(_terminalsFilePath))
            {
                _logger.LogInformation("No existing terminal sessions file found");
                return new Dictionary<string, TerminalSessionData>();
            }

            var json = await File.ReadAllTextAsync(_terminalsFilePath);
            var result = JsonSerializer.Deserialize<Dictionary<string, TerminalSessionData>>(json) ?? new Dictionary<string, TerminalSessionData>();
            
            _logger.LogInformation("Loaded {Count} terminal sessions from {Path}", result.Count, _terminalsFilePath);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load terminal sessions, starting fresh");
            return new Dictionary<string, TerminalSessionData>();
        }
    }
}

public class TerminalSessionData
{
    public string Id { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public List<string> CommandHistory { get; set; } = new();
    public DateTime LastActivity { get; set; }
    public string CurrentTask { get; set; } = string.Empty;
    public Dictionary<string, string> UserActiveTerminals { get; set; } = new(); // userKey -> terminalId
}