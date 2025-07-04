using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Interfaces;

namespace ClaudeMobileTerminal.Backend.Services;

public interface IMessageChannelManager
{
    void RegisterChannel(IMessageChannel channel);
    Task StartAllChannelsAsync(CancellationToken cancellationToken);
    Task StopAllChannelsAsync(CancellationToken cancellationToken);
    Task SendMessageAsync(string channelType, string recipient, string message, Dictionary<string, string>? buttons = null);
    Task BroadcastToSubscribersAsync(string terminalId, string message, Dictionary<string, string>? buttons = null);
    void SubscribeToTerminal(string terminalId, string channelType, string senderId);
    void UnsubscribeFromTerminal(string terminalId, string channelType, string senderId);
    string? GetLastActiveTerminal(string channelType, string senderId);
    void SetLastActiveTerminal(string channelType, string senderId, string terminalId);
    void CleanupTerminalSubscriptions(string terminalId);
    event EventHandler<ChannelCommand>? CommandReceived;
}

public class MessageChannelManager : IMessageChannelManager
{
    private readonly ILogger<MessageChannelManager> _logger;
    private readonly Dictionary<string, IMessageChannel> _channels = new();
    private readonly Dictionary<string, HashSet<(string ChannelType, string SenderId)>> _terminalSubscriptions = new();
    private readonly Dictionary<(string ChannelType, string SenderId), string> _lastActiveTerminals = new();
    private readonly object _lock = new();

    public event EventHandler<ChannelCommand>? CommandReceived;

    public MessageChannelManager(ILogger<MessageChannelManager> logger)
    {
        _logger = logger;
    }

    public void RegisterChannel(IMessageChannel channel)
    {
        lock (_lock)
        {
            if (channel.IsEnabled)
            {
                _channels[channel.ChannelType] = channel;
                channel.CommandReceived += OnChannelCommandReceived;
                _logger.LogInformation("Registered {ChannelType} channel", channel.ChannelType);
            }
        }
    }

    public async Task StartAllChannelsAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        
        lock (_lock)
        {
            foreach (var channel in _channels.Values)
            {
                tasks.Add(channel.StartAsync(cancellationToken));
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Started {Count} message channels", tasks.Count);
    }

    public async Task StopAllChannelsAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        
        lock (_lock)
        {
            foreach (var channel in _channels.Values)
            {
                tasks.Add(channel.StopAsync(cancellationToken));
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Stopped all message channels");
    }

    public async Task SendMessageAsync(string channelType, string recipient, string message, Dictionary<string, string>? buttons = null)
    {
        IMessageChannel? channel;
        
        lock (_lock)
        {
            if (!_channels.TryGetValue(channelType, out channel))
            {
                _logger.LogWarning("Channel type {ChannelType} not found", channelType);
                return;
            }
        }

        try
        {
            await channel.SendMessageAsync(recipient, message, buttons);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message via {ChannelType}", channelType);
        }
    }

    public async Task BroadcastToSubscribersAsync(string terminalId, string message, Dictionary<string, string>? buttons = null)
    {
        HashSet<(string ChannelType, string SenderId)>? subscribers;
        
        lock (_lock)
        {
            if (!_terminalSubscriptions.TryGetValue(terminalId, out subscribers))
            {
                return;
            }
            
            // Create a copy to avoid holding the lock during async operations
            subscribers = new HashSet<(string, string)>(subscribers);
        }

        var tasks = new List<Task>();
        
        foreach (var (channelType, senderId) in subscribers)
        {
            tasks.Add(SendMessageAsync(channelType, senderId, message, buttons));
        }

        await Task.WhenAll(tasks);
    }

    public void SubscribeToTerminal(string terminalId, string channelType, string senderId)
    {
        lock (_lock)
        {
            if (!_terminalSubscriptions.ContainsKey(terminalId))
            {
                _terminalSubscriptions[terminalId] = new HashSet<(string, string)>();
            }
            
            _terminalSubscriptions[terminalId].Add((channelType, senderId));
            _logger.LogDebug("Subscribed {ChannelType}:{SenderId} to terminal {TerminalId}", 
                channelType, senderId, terminalId);
        }
    }

    public void UnsubscribeFromTerminal(string terminalId, string channelType, string senderId)
    {
        lock (_lock)
        {
            if (_terminalSubscriptions.TryGetValue(terminalId, out var subscribers))
            {
                subscribers.Remove((channelType, senderId));
                
                if (subscribers.Count == 0)
                {
                    _terminalSubscriptions.Remove(terminalId);
                }
            }
        }
    }

    public string? GetLastActiveTerminal(string channelType, string senderId)
    {
        lock (_lock)
        {
            var key = (channelType, senderId);
            return _lastActiveTerminals.TryGetValue(key, out var terminalId) ? terminalId : null;
        }
    }

    public void SetLastActiveTerminal(string channelType, string senderId, string terminalId)
    {
        lock (_lock)
        {
            var key = (channelType, senderId);
            _lastActiveTerminals[key] = terminalId;
            _logger.LogDebug("Set last active terminal for {ChannelType}:{SenderId} to {TerminalId}", 
                channelType, senderId, terminalId);
        }
    }

    public void CleanupTerminalSubscriptions(string terminalId)
    {
        lock (_lock)
        {
            _terminalSubscriptions.Remove(terminalId);
            
            // Clean up last active terminal references
            var keysToRemove = _lastActiveTerminals
                .Where(kvp => kvp.Value == terminalId)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var key in keysToRemove)
            {
                _lastActiveTerminals.Remove(key);
            }
            
            _logger.LogDebug("Cleaned up subscriptions for terminal {TerminalId}", terminalId);
        }
    }

    private void OnChannelCommandReceived(object? sender, ChannelCommand command)
    {
        CommandReceived?.Invoke(this, command);
    }
}