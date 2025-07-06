using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Services;

public class ClaudeStartupService : IHostedService
{
    private readonly ILogger<ClaudeStartupService> _logger;
    private readonly IClaudeAuthenticationService _authService;
    private readonly IMessageChannelManager _channelManager;

    public ClaudeStartupService(
        ILogger<ClaudeStartupService> logger,
        IClaudeAuthenticationService authService,
        IMessageChannelManager channelManager)
    {
        _logger = logger;
        _authService = authService;
        _channelManager = channelManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking Claude authentication status...");

        try
        {
            var isAuthenticated = await _authService.IsAuthenticatedAsync();
            
            if (isAuthenticated)
            {
                _logger.LogInformation("✓ Claude is already authenticated and ready");
                await NotifyChannels("🟢 Claude authentication verified - Terminal is ready!", "success");
            }
            else
            {
                _logger.LogWarning("⚠ Claude authentication required");
                
                var authResult = await _authService.EnsureAuthenticatedAsync();
                
                if (!string.IsNullOrEmpty(authResult.AuthUrl))
                {
                    var message = $"🔑 **Claude Authentication Required**\n\n" +
                                $"Please complete authentication by visiting:\n" +
                                $"{authResult.AuthUrl}\n\n" +
                                $"After authentication, restart the container or send any command to refresh.";
                    
                    await NotifyChannels(message, "auth_required");
                    
                    _logger.LogInformation("Authentication URL sent to all channels: {Url}", authResult.AuthUrl);
                }
                else
                {
                    var message = "❌ **Claude Authentication Failed**\n\n" +
                                "Could not retrieve authentication URL. Please run:\n" +
                                "`claude /login` manually in a terminal.";
                    
                    await NotifyChannels(message, "auth_failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Claude authentication startup check");
            
            var message = "❌ **Claude Authentication Check Failed**\n\n" +
                         $"Error: {ex.Message}\n\n" +
                         "Please check container logs and try manual authentication.";
            
            await NotifyChannels(message, "auth_error");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task NotifyChannels(string message, string type)
    {
        try
        {
            // Send notification to all active channels
            await _channelManager.BroadcastMessageAsync(message, type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending authentication status to channels");
        }
    }
}