using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Channels;

public class WhatsAppChannel : IMessageChannel
{
    private readonly ILogger<WhatsAppChannel> _logger;
    private readonly WhatsAppConfiguration _config;
    private readonly HttpClient _httpClient;
    private Timer? _pollingTimer;
    private bool _isRunning;
    private readonly JsonSerializerOptions _jsonOptions;

    public string ChannelType => "WhatsApp";
    public bool IsEnabled => _config.Enabled;

    public event EventHandler<ChannelCommand>? CommandReceived;

    public WhatsAppChannel(ILogger<WhatsAppChannel> logger, IOptions<BotConfiguration> config, HttpClient httpClient)
    {
        _logger = logger;
        _config = config.Value.WhatsApp;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.RequestTimeout);
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("WhatsApp channel is disabled");
            return;
        }

        try
        {
            _logger.LogInformation("Starting WhatsApp channel...");

            // Check WAHA status
            var status = await GetSessionStatusAsync();
            _logger.LogInformation("WAHA Session status: {Status}", status);

            if (status != "WORKING")
            {
                _logger.LogWarning("WAHA session is not working. Status: {Status}", status);
                // Optionally try to start the session
                await StartSessionAsync();
            }

            // Set up webhook if configured
            if (_config.UseWebhook && !string.IsNullOrEmpty(_config.WebhookUrl))
            {
                await SetWebhookAsync(_config.WebhookUrl);
                _logger.LogInformation("WhatsApp webhook configured: {WebhookUrl}", _config.WebhookUrl);
            }
            else
            {
                // Start polling for messages
                StartPolling();
                _logger.LogInformation("WhatsApp polling started");
            }

            _isRunning = true;
            _logger.LogInformation("WhatsApp channel started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WhatsApp channel");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping WhatsApp channel...");

        _pollingTimer?.Dispose();
        _isRunning = false;

        _logger.LogInformation("WhatsApp channel stopped");
    }

    public async Task SendMessageAsync(string recipient, string message, Dictionary<string, string>? buttons = null)
    {
        try
        {
            var payload = new
            {
                chatId = FormatPhoneNumber(recipient),
                text = message,
                session = _config.SessionName
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_config.WAHAUrl}/api/sendText", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Message sent to WhatsApp: {Recipient}", recipient);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send WhatsApp message: {StatusCode} - {Error}", 
                    response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending WhatsApp message to {Recipient}", recipient);
        }
    }

    private async Task<string> GetSessionStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_config.WAHAUrl}/api/sessions/{_config.SessionName}");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var sessionInfo = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
                
                if (sessionInfo.TryGetProperty("status", out var statusElement))
                {
                    return statusElement.GetString() ?? "UNKNOWN";
                }
            }
            
            return "UNKNOWN";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session status");
            return "ERROR";
        }
    }

    private async Task StartSessionAsync()
    {
        try
        {
            var payload = new
            {
                name = _config.SessionName,
                config = new
                {
                    webhooks = new[]
                    {
                        new
                        {
                            url = _config.WebhookUrl,
                            events = new[] { "message" }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_config.WAHAUrl}/api/sessions/start", content);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("WhatsApp session started successfully");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to start WhatsApp session: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting WhatsApp session");
        }
    }

    private async Task SetWebhookAsync(string webhookUrl)
    {
        try
        {
            var payload = new
            {
                webhooks = new[]
                {
                    new
                    {
                        url = webhookUrl,
                        events = new[] { "message" }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_config.WAHAUrl}/api/sessions/{_config.SessionName}", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to set webhook: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting webhook");
        }
    }

    private void StartPolling()
    {
        _pollingTimer = new Timer(async _ => await PollMessagesAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private async Task PollMessagesAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_config.WAHAUrl}/api/messages?session={_config.SessionName}&limit=10");
            
            if (!response.IsSuccessStatusCode)
                return;

            var json = await response.Content.ReadAsStringAsync();
            var messages = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOptions);

            if (messages == null)
                return;

            foreach (var message in messages)
            {
                await ProcessMessageAsync(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling messages");
        }
    }

    private async Task ProcessMessageAsync(JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("from", out var fromElement) ||
                !message.TryGetProperty("body", out var bodyElement))
                return;

            var from = fromElement.GetString() ?? "";
            var body = bodyElement.GetString() ?? "";

            // Check if sender is allowed
            if (_config.AllowedNumbers.Any() && !_config.AllowedNumbers.Contains(from))
            {
                _logger.LogDebug("Ignoring message from unauthorized number: {From}", from);
                return;
            }

            // Parse command
            var command = ParseCommand(body);
            if (command != null)
            {
                command.ChannelType = ChannelType;
                command.SenderId = from;
                command.RawText = body;

                _logger.LogInformation("WhatsApp command received: {Command} from {From}", 
                    command.Command, from);

                CommandReceived?.Invoke(this, command);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WhatsApp message");
        }
    }

    public async Task ProcessWebhookAsync(string webhookBody)
    {
        try
        {
            var webhookData = JsonSerializer.Deserialize<JsonElement>(webhookBody, _jsonOptions);
            
            if (webhookData.TryGetProperty("messages", out var messagesElement))
            {
                if (messagesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in messagesElement.EnumerateArray())
                    {
                        await ProcessMessageAsync(message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WhatsApp webhook");
        }
    }

    private ChannelCommand? ParseCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        // Handle commands starting with /
        if (text.StartsWith('/'))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0][1..]; // Remove the /
            var args = parts.Skip(1).ToArray();

            return new ChannelCommand
            {
                Command = command,
                Arguments = args
            };
        }

        // Handle special commands
        if (text.StartsWith("//"))
        {
            return new ChannelCommand
            {
                Command = "terminal",
                Arguments = new[] { text[2..] }
            };
        }

        // Handle say command
        if (text.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
        {
            var message = text[4..].Trim();
            if (message.StartsWith('"') && message.EndsWith('"'))
            {
                message = message[1..^1];
            }

            return new ChannelCommand
            {
                Command = "say",
                Arguments = new[] { message }
            };
        }

        // Handle simple commands
        return new ChannelCommand
        {
            Command = text.ToLower(),
            Arguments = Array.Empty<string>()
        };
    }

    private string FormatPhoneNumber(string phoneNumber)
    {
        // Remove any non-digit characters except +
        var cleaned = new string(phoneNumber.Where(c => char.IsDigit(c) || c == '+').ToArray());
        
        // Ensure it has @ suffix for WhatsApp format
        if (!cleaned.Contains('@'))
        {
            cleaned = cleaned + "@c.us";
        }

        return cleaned;
    }
}