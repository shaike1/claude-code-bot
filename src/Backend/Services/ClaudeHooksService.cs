using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ClaudeMobileTerminal.Backend.Configuration;

namespace ClaudeMobileTerminal.Backend.Services;

public class ClaudeHooksService(ILogger<ClaudeHooksService> logger, IOptions<ClaudeHooksSettings> settings) : IClaudeHooksService
{
    private readonly ClaudeHooksSettings _settings = settings.Value;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly List<WebSocket> _connectedClients = new();

    public event EventHandler<ClaudeHookEvent>? HookEventReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.EnableHooks)
        {
            logger.LogInformation("Claude hooks are disabled");
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _httpListener = new HttpListener();
            // Try binding to all interfaces first, fall back to localhost if access denied
            try
            {
                _httpListener.Prefixes.Add($"http://*:{_settings.WebSocketPort}/");
                _httpListener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // Access denied
            {
                _httpListener.Close();
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_settings.WebSocketPort}/");
                _httpListener.Start();
                logger.LogWarning("Could not bind to all interfaces (access denied), using localhost only. WSL hooks may not work.");
            }

            logger.LogInformation("Claude hooks HTTP server started on port {Port}", _settings.WebSocketPort);

            _ = Task.Run(() => AcceptWebSocketClients(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
            
            return Task.CompletedTask;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 183) // Port already in use
        {
            logger.LogWarning("Port {Port} is already in use. Claude hooks service will be disabled.", _settings.WebSocketPort);
            // Don't throw - allow the application to continue without hooks
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Claude hooks service");
            // Don't throw - allow the application to continue without hooks
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        
        foreach (var client in _connectedClients)
        {
            try
            {
                client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", cancellationToken).Wait();
                client.Dispose();
            }
            catch { }
        }
        
        _connectedClients.Clear();
        
        try
        {
            _httpListener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
        
        return Task.CompletedTask;
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
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var webSocket = wsContext.WebSocket;
                    
                    lock (_connectedClients)
                    {
                        _connectedClients.Add(webSocket);
                    }
                    
                    _ = Task.Run(() => HandleWebSocketClient(webSocket, cancellationToken), cancellationToken);
                }
                else if (context.Request.HttpMethod == "POST")
                {
                    // Handle HTTP POST requests from hooks
                    _ = Task.Run(() => HandleHttpRequest(context), cancellationToken);
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
                logger.LogError(ex, "Error accepting client");
            }
        }
    }

    private async Task HandleWebSocketClient(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[4096]);
        
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer.Array!, 0, result.Count);
                    ProcessHookMessage(message);
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
            logger.LogError(ex, "Error handling WebSocket client");
        }
        finally
        {
            lock (_connectedClients)
            {
                _connectedClients.Remove(webSocket);
            }
            
            webSocket.Dispose();
        }
    }

    private async Task HandleHttpRequest(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var requestBody = await reader.ReadToEndAsync();
            
            logger.LogInformation("Received HTTP hook request: {RequestBody}", requestBody);
            
            // Process the hook message
            ProcessHookMessage(requestBody);
            
            // Send response
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            var responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling HTTP request");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    private void ProcessHookMessage(string message)
    {
        try
        {
            var hookData = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
            if (hookData == null) return;

            var eventType = hookData.GetValueOrDefault("event")?.ToString() ?? "unknown";
            var terminalId = hookData.GetValueOrDefault("terminalId")?.ToString() ?? "unknown";
            var response = hookData.GetValueOrDefault("response")?.ToString();

            var hookEvent = new ClaudeHookEvent
            {
                EventType = eventType,
                TerminalId = terminalId,
                Response = response,
                Data = hookData
            };

            logger.LogInformation("Received hook event: {EventType} for terminal {TerminalId}, response: {Response}", 
                eventType, terminalId, response?.Length > 50 ? response.Substring(0, 50) + "..." : response);
            
            HookEventReceived?.Invoke(this, hookEvent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing hook message: {Message}", message);
        }
    }

    public Task SendToClaudeAsync(string terminalId, object data)
    {
        var message = JsonConvert.SerializeObject(new
        {
            terminalId,
            timestamp = DateTime.UtcNow,
            data
        });

        var bytes = Encoding.UTF8.GetBytes(message);
        var buffer = new ArraySegment<byte>(bytes);

        var disconnectedClients = new List<WebSocket>();

        lock (_connectedClients)
        {
            foreach (var client in _connectedClients)
            {
                if (client.State == WebSocketState.Open)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await client.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        catch
                        {
                            disconnectedClients.Add(client);
                        }
                    });
                }
                else
                {
                    disconnectedClients.Add(client);
                }
            }

            foreach (var client in disconnectedClients)
            {
                _connectedClients.Remove(client);
                client.Dispose();
            }
        }
        
        return Task.CompletedTask;
    }
}