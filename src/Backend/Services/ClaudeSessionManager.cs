using System.Text.Json;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Interfaces;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Services;

public interface IClaudeSessionManager
{
    Task<List<ClaudeSession>> GetActiveSessionsAsync();
    Task<ClaudeSession?> GetSessionAsync(string sessionId);
    Task<ClaudeSession> CreateSessionAsync(string terminalId, string? projectPath = null, string? description = null);
    Task<bool> ResumeSessionAsync(string sessionId, string terminalId);
    Task<bool> DeleteSessionAsync(string sessionId);
    Task SaveSessionAsync(ClaudeSession session);
    Task<List<ClaudeSession>> GetTerminalSessionsAsync(string terminalId);
}

public class ClaudeSession
{
    public string Id { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string? ProjectPath { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
    public bool IsActive { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class ClaudeSessionManager : IClaudeSessionManager
{
    private readonly ILogger<ClaudeSessionManager> _logger;
    private readonly string _sessionsPath = "/root/.claude_sessions";
    private readonly string _metadataPath = "/app/terminals/claude_sessions.json";

    public ClaudeSessionManager(ILogger<ClaudeSessionManager> logger)
    {
        _logger = logger;
        EnsureDirectoriesExist();
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_sessionsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
    }

    public async Task<List<ClaudeSession>> GetActiveSessionsAsync()
    {
        try
        {
            if (!File.Exists(_metadataPath))
                return new List<ClaudeSession>();

            var json = await File.ReadAllTextAsync(_metadataPath);
            var sessions = JsonSerializer.Deserialize<List<ClaudeSession>>(json) ?? new List<ClaudeSession>();
            
            // Filter active sessions and update last used time based on file modification
            var activeSessions = new List<ClaudeSession>();
            foreach (var session in sessions)
            {
                var sessionFile = Path.Combine(_sessionsPath, $"{session.Id}.session");
                if (File.Exists(sessionFile))
                {
                    session.LastUsed = File.GetLastWriteTime(sessionFile);
                    session.IsActive = true;
                    activeSessions.Add(session);
                }
            }

            return activeSessions.OrderByDescending(s => s.LastUsed).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active Claude sessions");
            return new List<ClaudeSession>();
        }
    }

    public async Task<ClaudeSession?> GetSessionAsync(string sessionId)
    {
        var sessions = await GetActiveSessionsAsync();
        return sessions.FirstOrDefault(s => s.Id == sessionId);
    }

    public async Task<ClaudeSession> CreateSessionAsync(string terminalId, string? projectPath = null, string? description = null)
    {
        var sessionId = GenerateSessionId();
        var session = new ClaudeSession
        {
            Id = sessionId,
            TerminalId = terminalId,
            ProjectPath = projectPath,
            Description = description ?? $"Claude session for terminal {terminalId}",
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow,
            IsActive = true,
            WorkingDirectory = projectPath ?? "/workspace"
        };

        // Create session directory
        var sessionDir = Path.Combine(_sessionsPath, sessionId);
        Directory.CreateDirectory(sessionDir);

        // Create session metadata file
        var sessionFile = Path.Combine(sessionDir, "session.json");
        await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));

        await SaveSessionAsync(session);
        
        _logger.LogInformation("Created Claude session {SessionId} for terminal {TerminalId}", sessionId, terminalId);
        return session;
    }

    public async Task<bool> ResumeSessionAsync(string sessionId, string terminalId)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            if (session == null)
                return false;

            // Update session with new terminal
            session.TerminalId = terminalId;
            session.LastUsed = DateTime.UtcNow;
            session.IsActive = true;

            await SaveSessionAsync(session);
            
            _logger.LogInformation("Resumed Claude session {SessionId} in terminal {TerminalId}", sessionId, terminalId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Claude session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        try
        {
            var sessionDir = Path.Combine(_sessionsPath, sessionId);
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, true);
            }

            // Remove from metadata
            var sessions = await GetActiveSessionsAsync();
            sessions.RemoveAll(s => s.Id == sessionId);
            await SaveSessionsMetadata(sessions);

            _logger.LogInformation("Deleted Claude session {SessionId}", sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Claude session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task SaveSessionAsync(ClaudeSession session)
    {
        var sessions = await GetActiveSessionsAsync();
        var existingIndex = sessions.FindIndex(s => s.Id == session.Id);
        
        if (existingIndex >= 0)
            sessions[existingIndex] = session;
        else
            sessions.Add(session);

        await SaveSessionsMetadata(sessions);
    }

    public async Task<List<ClaudeSession>> GetTerminalSessionsAsync(string terminalId)
    {
        var sessions = await GetActiveSessionsAsync();
        return sessions.Where(s => s.TerminalId == terminalId).ToList();
    }

    private async Task SaveSessionsMetadata(List<ClaudeSession> sessions)
    {
        try
        {
            var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_metadataPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Claude sessions metadata");
        }
    }

    private string GenerateSessionId()
    {
        return $"claude_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Random.Shared.Next(1000, 9999)}";
    }
}