using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;
using ClaudeMobileTerminal.Backend.Services;
using ClaudeMobileTerminal.Backend.Channels;

namespace ClaudeMobileTerminal.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigurationController : ControllerBase
{
    private readonly ILogger<ConfigurationController> _logger;
    private readonly IOptionsSnapshot<BotConfiguration> _botConfig;
    private readonly IOptionsSnapshot<TerminalSettings> _terminalSettings;
    private readonly IOptionsSnapshot<ClaudeHooksSettings> _claudeHooksSettings;
    private readonly IOptionsSnapshot<WebSocketChannelConfiguration> _webSocketConfig;
    private readonly IConfiguration _configuration;
    private readonly IMessageChannelManager _channelManager;

    public ConfigurationController(
        ILogger<ConfigurationController> logger,
        IOptionsSnapshot<BotConfiguration> botConfig,
        IOptionsSnapshot<TerminalSettings> terminalSettings,
        IOptionsSnapshot<ClaudeHooksSettings> claudeHooksSettings,
        IOptionsSnapshot<WebSocketChannelConfiguration> webSocketConfig,
        IConfiguration configuration,
        IMessageChannelManager channelManager)
    {
        _logger = logger;
        _botConfig = botConfig;
        _terminalSettings = terminalSettings;
        _claudeHooksSettings = claudeHooksSettings;
        _webSocketConfig = webSocketConfig;
        _configuration = configuration;
        _channelManager = channelManager;
    }

    [HttpGet("bot")]
    public IActionResult GetBotConfiguration()
    {
        return Ok(_botConfig.Value);
    }

    [HttpGet("terminal")]
    public IActionResult GetTerminalSettings()
    {
        return Ok(_terminalSettings.Value);
    }

    [HttpGet("hooks")]
    public IActionResult GetClaudeHooksSettings()
    {
        return Ok(_claudeHooksSettings.Value);
    }

    [HttpGet("websocket")]
    public IActionResult GetWebSocketSettings()
    {
        return Ok(_webSocketConfig.Value);
    }

    [HttpGet("all")]
    public IActionResult GetAllConfiguration()
    {
        return Ok(new
        {
            BotConfiguration = _botConfig.Value,
            TerminalSettings = _terminalSettings.Value,
            ClaudeHooks = _claudeHooksSettings.Value,
            WebSocketChannel = _webSocketConfig.Value
        });
    }

    [HttpPost("bot")]
    public async Task<IActionResult> UpdateBotConfiguration([FromBody] BotConfiguration newConfig)
    {
        try
        {
            await UpdateConfigurationSection("BotConfiguration", newConfig);
            _logger.LogInformation("Bot configuration updated");
            return Ok(new { message = "Bot configuration updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update bot configuration");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("terminal")]
    public async Task<IActionResult> UpdateTerminalSettings([FromBody] TerminalSettings newSettings)
    {
        try
        {
            await UpdateConfigurationSection("TerminalSettings", newSettings);
            _logger.LogInformation("Terminal settings updated");
            return Ok(new { message = "Terminal settings updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update terminal settings");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("hooks")]
    public async Task<IActionResult> UpdateClaudeHooksSettings([FromBody] ClaudeHooksSettings newSettings)
    {
        try
        {
            await UpdateConfigurationSection("ClaudeHooks", newSettings);
            _logger.LogInformation("Claude hooks settings updated");
            return Ok(new { message = "Claude hooks settings updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Claude hooks settings");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("websocket")]
    public async Task<IActionResult> UpdateWebSocketSettings([FromBody] WebSocketChannelConfiguration newSettings)
    {
        try
        {
            await UpdateConfigurationSection("WebSocketChannel", newSettings);
            _logger.LogInformation("WebSocket settings updated");
            return Ok(new { message = "WebSocket settings updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update WebSocket settings");
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task UpdateConfigurationSection<T>(string sectionName, T newValue)
    {
        var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        
        if (!System.IO.File.Exists(appSettingsPath))
        {
            throw new FileNotFoundException("appsettings.json not found");
        }

        var json = await System.IO.File.ReadAllTextAsync(appSettingsPath);
        var configJson = JsonDocument.Parse(json);
        var configDict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        if (configDict == null)
        {
            throw new InvalidOperationException("Failed to parse configuration");
        }

        configDict[sectionName] = newValue!;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var updatedJson = JsonSerializer.Serialize(configDict, options);
        await System.IO.File.WriteAllTextAsync(appSettingsPath, updatedJson);
    }
}

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly ILogger<StatusController> _logger;
    private readonly IMessageChannelManager _channelManager;
    private readonly ITerminalManager _terminalManager;

    public StatusController(
        ILogger<StatusController> logger,
        IMessageChannelManager channelManager,
        ITerminalManager terminalManager)
    {
        _logger = logger;
        _channelManager = channelManager;
        _terminalManager = terminalManager;
    }

    [HttpGet("channels")]
    public IActionResult GetChannelStatus()
    {
        // This is a simplified status - in a real implementation,
        // you'd want to add status tracking to the channel manager
        return Ok(new { status = "Channels running" });
    }

    [HttpGet("terminals")]
    public IActionResult GetTerminalStatus()
    {
        var terminals = _terminalManager.GetActiveTerminals();
        return Ok(new { 
            count = terminals.Count(),
            terminals = terminals.Select(t => new { 
                id = t.Id,
                isActive = t.IsActive,
                createdAt = t.CreatedAt
            })
        });
    }

    [HttpGet("system")]
    public IActionResult GetSystemStatus()
    {
        return Ok(new
        {
            uptime = Environment.TickCount64,
            workingSet = Environment.WorkingSet,
            gcMemory = GC.GetTotalMemory(false),
            processorCount = Environment.ProcessorCount,
            osVersion = Environment.OSVersion.ToString()
        });
    }
}