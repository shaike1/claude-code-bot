using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClaudeMobileTerminal.Backend.Services;
using ClaudeMobileTerminal.Backend.Channels;
using ClaudeMobileTerminal.Backend.Configuration;
using ClaudeMobileTerminal.Backend.Interfaces;
using Serilog;
using Serilog.Events;
using System.Diagnostics;

namespace ClaudeMobileTerminal.Backend;

class Program
{
    static void KillExistingInstances()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);
            
            foreach (var process in processes)
            {
                if (process.Id != currentProcess.Id)
                {
                    try
                    {
                        process.Kill();
                        Console.WriteLine($"Killed existing instance with PID {process.Id}");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
    
    static async Task Main(string[] args)
    {
        // Kill any existing instances to prevent Telegram conflicts
        KillExistingInstances();
        
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File("logs/claudemobile-.txt", 
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Starting ClaudeMobileTerminal application");
            
            var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddEnvironmentVariables("CMT_");
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                services.Configure<BotConfiguration>(context.Configuration.GetSection("BotConfiguration"));
                services.Configure<TerminalSettings>(context.Configuration.GetSection("TerminalSettings"));
                services.Configure<ClaudeHooksSettings>(context.Configuration.GetSection("ClaudeHooks"));
                services.Configure<WebSocketChannelConfiguration>(context.Configuration.GetSection("WebSocketChannel"));
                services.Configure<InteractiveAppSettings>(context.Configuration.GetSection("InteractiveApps"));
                
                // Core services
                services.AddSingleton<ITerminalManager, TerminalManager>();
                services.AddSingleton<IClaudeHooksService, ClaudeHooksService>();
                services.AddSingleton<TerminalOutputProcessor>();
                services.AddSingleton<IMessageChannelManager, MessageChannelManager>();
                
                // Channel implementations
                services.AddSingleton<TelegramChannel>();
                services.AddSingleton<DiscordChannel>();
                services.AddSingleton<WebSocketChannel>();
                
                // Register channels with the manager
                services.AddHostedService<ChannelRegistrationService>();
                services.AddHostedService<MainServiceV2>();
            })
            .UseSerilog()  // Use Serilog for logging
            .Build();

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

// Helper service to register channels
public class ChannelRegistrationService : IHostedService
{
    private readonly IMessageChannelManager _channelManager;
    private readonly IEnumerable<IMessageChannel> _channels;

    public ChannelRegistrationService(
        IMessageChannelManager channelManager,
        TelegramChannel telegram,
        DiscordChannel discord,
        WebSocketChannel webSocket)
    {
        _channelManager = channelManager;
        _channels = new IMessageChannel[] { telegram, discord, webSocket };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            _channelManager.RegisterChannel(channel);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}