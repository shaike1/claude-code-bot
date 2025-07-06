using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Services;

public interface IClaudeAuthenticationService
{
    Task<bool> IsAuthenticatedAsync();
    Task<AuthenticationResult> EnsureAuthenticatedAsync();
    Task<string?> GetAuthenticationUrlAsync();
    Task<bool> ValidateAuthenticationAsync();
}

public class AuthenticationResult
{
    public bool IsSuccess { get; set; }
    public string? Message { get; set; }
    public string? AuthUrl { get; set; }
    public string? Error { get; set; }
}

public class ClaudeCredentials
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long ExpiresAt { get; set; }
    public string[]? Scopes { get; set; }
    public string? SubscriptionType { get; set; }
}

public class ClaudeCredentialsWrapper
{
    public ClaudeCredentials? ClaudeAiOauth { get; set; }
}

public class ClaudeAuthenticationService : IClaudeAuthenticationService
{
    private readonly ILogger<ClaudeAuthenticationService> _logger;
    private readonly string _credentialsPath = "/root/.claude/.credentials.json";
    private readonly string _configPath = "/root/.claude/settings.local.json";
    private readonly string _globalConfigPath = "/root/.claude.json";

    public ClaudeAuthenticationService(ILogger<ClaudeAuthenticationService> logger)
    {
        _logger = logger;
        EnsureDirectoriesExist();
    }

    private void EnsureDirectoriesExist()
    {
        var claudeDir = Path.GetDirectoryName(_credentialsPath);
        if (!string.IsNullOrEmpty(claudeDir))
        {
            Directory.CreateDirectory(claudeDir);
        }
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                _logger.LogInformation("Claude credentials file not found at {Path}", _credentialsPath);
                return false;
            }

            var credentialsJson = await File.ReadAllTextAsync(_credentialsPath);
            var credentialsWrapper = JsonSerializer.Deserialize<ClaudeCredentialsWrapper>(credentialsJson);
            var credentials = credentialsWrapper?.ClaudeAiOauth;

            if (credentials?.AccessToken == null)
            {
                _logger.LogWarning("No access token found in credentials");
                return false;
            }

            // Check if token is expired (with 5 minute buffer)
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(credentials.ExpiresAt).DateTime;
            if (expiresAt <= DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogWarning("Access token is expired or will expire soon. Expires at: {ExpiresAt}", expiresAt);
                return false;
            }

            // Test the token by running a simple claude command
            return await ValidateAuthenticationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Claude authentication status");
            return false;
        }
    }

    public async Task<bool> ValidateAuthenticationAsync()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Claude authentication validation successful");
                return true;
            }

            _logger.LogWarning("Claude authentication validation failed. Exit code: {ExitCode}, Error: {Error}", 
                process.ExitCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating Claude authentication");
            return false;
        }
    }

    public async Task<AuthenticationResult> EnsureAuthenticatedAsync()
    {
        try
        {
            // First check if already authenticated
            if (await IsAuthenticatedAsync())
            {
                return new AuthenticationResult
                {
                    IsSuccess = true,
                    Message = "Already authenticated with Claude"
                };
            }

            _logger.LogInformation("Claude authentication required. Starting automated login process...");

            // Get authentication URL
            var authUrl = await GetAuthenticationUrlAsync();
            if (string.IsNullOrEmpty(authUrl))
            {
                return new AuthenticationResult
                {
                    IsSuccess = false,
                    Error = "Failed to retrieve authentication URL"
                };
            }

            return new AuthenticationResult
            {
                IsSuccess = false, // Requires manual intervention
                Message = "Authentication URL retrieved. Manual login required.",
                AuthUrl = authUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring Claude authentication");
            return new AuthenticationResult
            {
                IsSuccess = false,
                Error = ex.Message
            };
        }
    }

    public async Task<string?> GetAuthenticationUrlAsync()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "/login",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["TERM"] = "xterm-256color" }
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();

            var outputTask = Task.Run(async () =>
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                return output + error;
            });

            // Automated interaction sequence
            await Task.Delay(1000); // Wait for initial prompt

            // Select theme (option 1 - default)
            await process.StandardInput.WriteLineAsync("1");
            await Task.Delay(500);
            
            // Confirm theme selection
            await process.StandardInput.WriteLineAsync("");
            await Task.Delay(500);
            
            // Select login method (option 2 - console account)
            await process.StandardInput.WriteLineAsync("2");
            await Task.Delay(2000); // Wait for URL generation

            // Close stdin to signal end of input
            process.StandardInput.Close();

            // Wait for process completion with timeout
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            var completedTask = await Task.WhenAny(outputTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Timeout waiting for Claude login process");
                process.Kill();
                return null;
            }

            var allOutput = await outputTask;
            
            // Extract URL from output using regex
            var urlPattern = @"https://console\.anthropic\.com/auth\?[^\s\r\n]+";
            var match = Regex.Match(allOutput, urlPattern);
            
            if (match.Success)
            {
                var url = match.Value;
                _logger.LogInformation("Successfully extracted authentication URL");
                return url;
            }

            // Fallback patterns
            var fallbackPatterns = new[]
            {
                @"https://[^\s]*anthropic[^\s]*",
                @"Visit:\s*(https://[^\s]+)",
                @"Open this URL[^\n]*:\s*(https://[^\s]+)"
            };

            foreach (var pattern in fallbackPatterns)
            {
                match = Regex.Match(allOutput, pattern);
                if (match.Success)
                {
                    var url = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                    _logger.LogInformation("Extracted authentication URL using fallback pattern");
                    return url;
                }
            }

            _logger.LogWarning("Could not extract authentication URL from output: {Output}", allOutput);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Claude authentication URL");
            return null;
        }
    }
}