using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Channels;

public class WebSocketChannelConfiguration
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8766;
    public string[] AllowedOrigins { get; set; } = [];
}

public class WebSocketChannel(ILogger<WebSocketChannel> logger, IOptions<WebSocketChannelConfiguration> config) : IMessageChannel
{
    private readonly WebSocketChannelConfiguration _config = config.Value;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    public string ChannelType => "WebSocket";
    public bool IsEnabled => _config.Enabled;
    public event EventHandler<ChannelCommand>? CommandReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            logger.LogInformation("WebSocket channel is disabled");
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{_config.Port}/");
            _httpListener.Start();

            logger.LogInformation("WebSocket channel started on port {Port}", _config.Port);

            _ = Task.Run(() => AcceptWebSocketClients(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start WebSocket channel");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        
        foreach (var client in _clients.Values)
        {
            try
            {
                client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", cancellationToken).Wait();
                client.Dispose();
            }
            catch { }
        }
        
        _clients.Clear();
        _httpListener?.Stop();
        
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync(string recipient, string message, Dictionary<string, string>? buttons = null)
    {
        if (!_clients.TryGetValue(recipient, out var client) || client.State != WebSocketState.Open)
        {
            logger.LogWarning("Client {Recipient} not found or disconnected", recipient);
            return;
        }

        try
        {
            var payload = new
            {
                type = "message",
                content = message,
                buttons,
                timestamp = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            await client.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send WebSocket message to {Recipient}", recipient);
            _clients.TryRemove(recipient, out _);
        }
    }

    private async Task AcceptWebSocketClients(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                
                if (context.Request.IsWebSocketRequest)
                {
                    if (!IsOriginAllowed(context.Request.Headers["Origin"]))
                    {
                        context.Response.StatusCode = 403;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var webSocket = wsContext.WebSocket;
                    var clientId = Guid.NewGuid().ToString();
                    
                    _clients[clientId] = webSocket;
                    
                    _ = Task.Run(() => HandleWebSocketClient(clientId, webSocket, cancellationToken), cancellationToken);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error accepting WebSocket client");
            }
        }
    }

    private async Task HandleWebSocketClient(string clientId, WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[4096]);
        
        try
        {
            // Send welcome message
            await SendMessageAsync(clientId, "Connected to ClaudeMobileTerminal WebSocket channel");

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer.Array!, 0, result.Count);
                    ProcessWebSocketMessage(clientId, message);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WebSocket client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            webSocket.Dispose();
        }
    }

    private void ProcessWebSocketMessage(string clientId, string message)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
            if (data == null) return;

            var command = data.GetValueOrDefault("command")?.ToString() ?? "";
            var rawText = data.GetValueOrDefault("text")?.ToString() ?? command;
            
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var cmd = parts[0].TrimStart('/');
            var args = parts.Skip(1).ToArray();

            var channelCommand = new ChannelCommand
            {
                ChannelType = ChannelType,
                SenderId = clientId,
                Command = cmd,
                Arguments = args,
                RawText = rawText
            };

            CommandReceived?.Invoke(this, channelCommand);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing WebSocket message from {ClientId}", clientId);
        }
    }

    private bool IsOriginAllowed(string? origin)
    {
        if (_config.AllowedOrigins.Length == 0) return true;
        if (string.IsNullOrEmpty(origin)) return false;
        
        return _config.AllowedOrigins.Any(allowed => 
            origin.Equals(allowed, StringComparison.OrdinalIgnoreCase));
    }
}