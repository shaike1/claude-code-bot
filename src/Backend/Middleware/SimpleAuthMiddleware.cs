using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace ClaudeMobileTerminal.Backend.Middleware;

public class SimpleAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _username;
    private readonly string _password;

    public SimpleAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _username = configuration["WebUI:Username"] ?? "admin";
        _password = configuration["WebUI:Password"] ?? "claude123";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip authentication for health check and static files
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/js") ||
            context.Request.Path.StartsWithSegments("/css") ||
            context.Request.Path.StartsWithSegments("/images"))
        {
            await _next(context);
            return;
        }

        // Check for basic authentication
        string? authHeader = context.Request.Headers["Authorization"];
        
        if (authHeader != null && authHeader.StartsWith("Basic "))
        {
            string encodedUsernamePassword = authHeader.Substring("Basic ".Length).Trim();
            string usernamePassword = Encoding.UTF8.GetString(Convert.FromBase64String(encodedUsernamePassword));
            
            string[] parts = usernamePassword.Split(':');
            if (parts.Length == 2)
            {
                string username = parts[0];
                string password = parts[1];
                
                if (username == _username && password == _password)
                {
                    await _next(context);
                    return;
                }
            }
        }

        // Return 401 Unauthorized
        context.Response.StatusCode = 401;
        context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Claude Code Bot Management\"";
        await context.Response.WriteAsync("Unauthorized");
    }
}