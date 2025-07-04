# Configuration Guide ⚙️

Comprehensive configuration guide for Claude Code Bot.

## 📋 Table of Contents

- [Configuration Files](#configuration-files)
- [Telegram Configuration](#telegram-configuration)
- [Discord Configuration](#discord-configuration)
- [WhatsApp Configuration (WAHA)](#whatsapp-configuration-waha)
- [WebSocket Configuration](#websocket-configuration)
- [Terminal Settings](#terminal-settings)
- [Logging Configuration](#logging-configuration)
- [Environment Variables](#environment-variables)
- [Security Configuration](#security-configuration)

## 📄 Configuration Files

### Primary Configuration
The main configuration file is `src/Backend/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "ClaudeMobileTerminal": "Information"
    }
  },
  "BotConfiguration": {
    "Telegram": {
      "Enabled": true,
      "BotToken": "YOUR_BOT_TOKEN",
      "AllowedChatIds": [],
      "_instructions": "Get token from @BotFather"
    },
    "Discord": {
      "Enabled": false,
      "BotToken": "YOUR_DISCORD_TOKEN",
      "AllowedGuildIds": [],
      "AllowedChannelIds": []
    },
    "WhatsApp": {
      "Enabled": false,
      "WAHAUrl": "http://localhost:3000",
      "SessionName": "default",
      "WebhookUrl": "",
      "ApiKey": "",
      "AllowedNumbers": [],
      "UseWebhook": false,
      "RequestTimeout": 30
    }
  },
  "TerminalSettings": {
    "AutoSelectFirstOption": false,
    "MaxTerminals": 5,
    "TerminalTimeout": 1800
  },
  "ClaudeHooks": {
    "WebSocketPort": 8765,
    "EnableHooks": true
  },
  "WebSocketChannel": {
    "Enabled": false,
    "Port": 8766,
    "AllowedOrigins": ["http://localhost:3000"]
  }
}
```

### Environment-Specific Configuration
Create environment-specific files:

```bash
# Development
appsettings.Development.json

# Production
appsettings.Production.json

# Staging
appsettings.Staging.json
```

## 📱 Telegram Configuration

### Basic Setup
```json
{
  "BotConfiguration": {
    "Telegram": {
      "Enabled": true,
      "BotToken": "8092810636:AAFqr7klK41RGUC1xOCCsXr9k4NFyAij6RY",
      "AllowedChatIds": [],
      "WebhookUrl": "",
      "UseWebhook": false,
      "MaxConnections": 40,
      "AllowedUpdates": ["message", "callback_query"]
    }
  }
}
```

### Getting Bot Token
1. Message [@BotFather](https://t.me/botfather)
2. Send `/newbot`
3. Choose a name: `My Claude Bot`
4. Choose a username: `my_claude_bot` (must end with 'bot')
5. Copy the token provided

### Getting Chat ID
```bash
# Method 1: Use @userinfobot
# Send any message to @userinfobot

# Method 2: API call
curl "https://api.telegram.org/bot<YOUR_BOT_TOKEN>/getUpdates"

# Method 3: Send a message to your bot and check logs
# Bot will log incoming chat IDs
```

### Security Settings
```json
{
  "Telegram": {
    "AllowedChatIds": [123456789, 987654321],
    "AdminChatIds": [123456789],
    "EnableWhitelist": true,
    "RejectUnknownUsers": true
  }
}
```

### Advanced Telegram Settings
```json
{
  "Telegram": {
    "Enabled": true,
    "BotToken": "YOUR_TOKEN",
    "AllowedChatIds": [123456789],
    "WebhookUrl": "https://your-domain.com/webhook/telegram",
    "UseWebhook": true,
    "WebhookSecretToken": "your-secret-token",
    "MaxConnections": 40,
    "AllowedUpdates": ["message", "callback_query", "inline_query"],
    "DropPendingUpdates": true,
    "RequestTimeout": 30,
    "RetryPolicy": {
      "MaxRetries": 3,
      "DelayMs": 1000,
      "BackoffMultiplier": 2.0
    },
    "RateLimiting": {
      "RequestsPerSecond": 30,
      "BurstSize": 10
    }
  }
}
```

## 📱 WhatsApp Configuration (WAHA)

### WAHA Installation
WAHA (WhatsApp HTTP API) provides a REST API for WhatsApp integration.

```bash
# Using Docker (recommended)
docker run -it --rm \
  -p 3000:3000/tcp \
  -v $(pwd)/.sessions:/app/.sessions \
  devlikeapro/waha-plus

# Using Docker Compose
version: '3.8'
services:
  waha:
    image: devlikeapro/waha-plus
    ports:
      - "3000:3000"
    volumes:
      - ./waha-sessions:/app/.sessions
    environment:
      - WHATSAPP_HOOK_URL=http://claude-code-bot:8765/api/whatsappwebhook
```

### Basic WhatsApp Setup
```json
{
  "BotConfiguration": {
    "WhatsApp": {
      "Enabled": true,
      "WAHAUrl": "http://localhost:3000",
      "SessionName": "default",
      "AllowedNumbers": ["+1234567890@c.us"],
      "UseWebhook": false,
      "RequestTimeout": 30
    }
  }
}
```

### Starting WhatsApp Session
```bash
# 1. Start WAHA session
curl -X POST http://localhost:3000/api/sessions/start \
  -H "Content-Type: application/json" \
  -d '{
    "name": "default",
    "config": {
      "webhooks": [
        {
          "url": "http://your-bot-url:8765/api/whatsappwebhook",
          "events": ["message"]
        }
      ]
    }
  }'

# 2. Get QR code for WhatsApp authentication
curl http://localhost:3000/api/sessions/default/auth/qr

# 3. Check session status
curl http://localhost:3000/api/sessions/default
```

### WhatsApp Phone Number Format
```json
{
  "WhatsApp": {
    "AllowedNumbers": [
      "+1234567890@c.us",     // Individual contact
      "1234567890-group@g.us" // Group chat
    ]
  }
}
```

### Webhook Configuration
```json
{
  "WhatsApp": {
    "Enabled": true,
    "WAHAUrl": "http://waha:3000",
    "SessionName": "claude-bot",
    "WebhookUrl": "http://claude-code-bot:8765/api/whatsappwebhook",
    "UseWebhook": true,
    "AllowedNumbers": ["+1234567890@c.us"],
    "RequestTimeout": 30
  }
}
```

### Advanced WAHA Configuration
```json
{
  "WhatsApp": {
    "Enabled": true,
    "WAHAUrl": "http://waha:3000",
    "SessionName": "production-bot",
    "WebhookUrl": "https://your-domain.com/api/whatsappwebhook",
    "ApiKey": "your-waha-api-key",
    "UseWebhook": true,
    "AllowedNumbers": [
      "+1234567890@c.us",
      "+0987654321@c.us"
    ],
    "RequestTimeout": 60,
    "RetryPolicy": {
      "MaxRetries": 3,
      "DelayMs": 1000
    },
    "MessageOptions": {
      "LinkPreview": false,
      "SendSeen": true
    }
  }
}
```

### WAHA with Docker Compose Integration
```yaml
# docker-compose.yml
version: '3.8'

services:
  waha:
    image: devlikeapro/waha-plus
    ports:
      - "3000:3000"
    volumes:
      - ./waha-sessions:/app/.sessions
    environment:
      - WHATSAPP_HOOK_URL=http://claude-terminal:8765/api/whatsappwebhook
      - WHATSAPP_HOOK_EVENTS=message
    networks:
      - claude-network

  claude-terminal:
    build: .
    ports:
      - "8765:8765"
      - "8766:8766"
    depends_on:
      - waha
    environment:
      - BotConfiguration__WhatsApp__WAHAUrl=http://waha:3000
      - BotConfiguration__WhatsApp__Enabled=true
    networks:
      - claude-network

networks:
  claude-network:
    driver: bridge
```

### WhatsApp Commands
The WhatsApp channel supports the same commands as other channels:

```
/new - Create new terminal
/list - List active terminals  
/kill 1 - Kill terminal with ID 1
claude - Start Claude Code
say "ls -la" - Send command to Claude
//pwd - Run command directly on terminal
```

### Getting Phone Numbers for AllowedNumbers
```bash
# Send a test message and check logs to get the correct format
# The bot will log the sender ID in the correct format

# Or use WAHA API to get contacts
curl http://localhost:3000/api/contacts?session=default
```

### Troubleshooting WhatsApp
```bash
# Check WAHA status
curl http://localhost:3000/api/sessions

# Check session health
curl http://localhost:3000/api/sessions/default

# Restart session if needed
curl -X POST http://localhost:3000/api/sessions/default/restart

# Stop and start session
curl -X POST http://localhost:3000/api/sessions/default/stop
curl -X POST http://localhost:3000/api/sessions/start -d '{"name":"default"}'
```

## 🎮 Discord Configuration

### Basic Setup
```json
{
  "BotConfiguration": {
    "Discord": {
      "Enabled": true,
      "BotToken": "YOUR_DISCORD_BOT_TOKEN",
      "AllowedGuildIds": [123456789],
      "AllowedChannelIds": [987654321],
      "CommandPrefix": "!",
      "EnableSlashCommands": true
    }
  }
}
```

### Creating Discord Bot
1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Click "New Application"
3. Go to "Bot" section
4. Click "Add Bot"
5. Copy the token
6. Enable required permissions:
   - Send Messages
   - Read Message History
   - Use Slash Commands
   - Embed Links

### Discord Permissions
```json
{
  "Discord": {
    "Permissions": {
      "SendMessages": true,
      "ReadMessageHistory": true,
      "UseSlashCommands": true,
      "EmbedLinks": true,
      "AttachFiles": true,
      "ManageMessages": false
    },
    "Intents": [
      "Guilds",
      "GuildMessages",
      "DirectMessages",
      "MessageContent"
    ]
  }
}
```

### Getting Guild and Channel IDs
```bash
# Enable Developer Mode in Discord
# Right-click server -> Copy ID (Guild ID)
# Right-click channel -> Copy ID (Channel ID)
```

## 🌐 WebSocket Configuration

### Basic WebSocket Setup
```json
{
  "WebSocketChannel": {
    "Enabled": true,
    "Port": 8766,
    "AllowedOrigins": [
      "http://localhost:3000",
      "https://your-domain.com"
    ],
    "EnableCors": true,
    "MaxConnections": 100,
    "HeartbeatInterval": 30,
    "ConnectionTimeout": 60
  }
}
```

### WebSocket Security
```json
{
  "WebSocketChannel": {
    "Enabled": true,
    "Port": 8766,
    "AllowedOrigins": ["https://your-domain.com"],
    "RequireAuthentication": true,
    "AuthenticationToken": "your-secret-token",
    "EnableSsl": true,
    "SslCertificatePath": "/app/certs/certificate.pfx",
    "SslCertificatePassword": "cert-password"
  }
}
```

### Claude Hooks Configuration
```json
{
  "ClaudeHooks": {
    "WebSocketPort": 8765,
    "EnableHooks": true,
    "HookTimeout": 30,
    "MaxHookRetries": 3,
    "EnableLogging": true,
    "LogLevel": "Information"
  }
}
```

## 🖥️ Terminal Settings

### Basic Terminal Configuration
```json
{
  "TerminalSettings": {
    "AutoSelectFirstOption": false,
    "MaxTerminals": 5,
    "TerminalTimeout": 1800,
    "ShowTerminalWindow": false,
    "DefaultShell": "/bin/bash",
    "WorkingDirectory": "/home/user"
  }
}
```

### Advanced Terminal Settings
```json
{
  "TerminalSettings": {
    "AutoSelectFirstOption": false,
    "MaxTerminals": 10,
    "TerminalTimeout": 3600,
    "ShowTerminalWindow": false,
    "DefaultShell": "/bin/bash",
    "WorkingDirectory": "/workspace",
    "EnvironmentVariables": {
      "CLAUDE_AUTO_UPDATE": "true",
      "CLAUDE_LOG_LEVEL": "info",
      "PATH": "/usr/local/bin:/usr/bin:/bin"
    },
    "StartupCommands": [
      "cd /workspace",
      "source ~/.bashrc"
    ],
    "OutputFiltering": {
      "EnableFiltering": true,
      "MaxOutputLines": 1000,
      "FilterPatterns": [
        "^\\[DEBUG\\]",
        "^\\[TRACE\\]"
      ]
    }
  }
}
```

### Interactive App Settings
```json
{
  "InteractiveAppSettings": {
    "AutoSelectFirstOption": true,
    "MaxTerminals": 5,
    "TerminalTimeout": 1800,
    "Apps": [
      {
        "DetectionKeywords": ["claude", "claude-code"],
        "InputMethod": "Direct",
        "CharacterDelay": 0,
        "OutputProcessing": {
          "EnableProcessing": true,
          "MaxOutputLines": 500,
          "AutoHandlePagination": true,
          "PaginationPrompt": "Press any key to continue...",
          "MaxPaginationAttempts": 3,
          "PaginationDelayMs": 1000
        }
      }
    ]
  }
}
```

## 📝 Logging Configuration

### Basic Logging
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "ClaudeMobileTerminal": "Debug"
    }
  }
}
```

### Advanced Logging with Serilog
```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.Console", "Serilog.Sinks.File"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "/app/logs/app-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7,
          "fileSizeLimitBytes": 10485760,
          "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"]
  }
}
```

### Structured Logging
```json
{
  "Serilog": {
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "/app/logs/app-.json",
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
          "rollingInterval": "Day"
        }
      }
    ]
  }
}
```

## 🌍 Environment Variables

### Override Configuration with Environment Variables
```bash
# Telegram
export BotConfiguration__Telegram__BotToken="your_token"
export BotConfiguration__Telegram__Enabled="true"

# Discord
export BotConfiguration__Discord__BotToken="your_discord_token"
export BotConfiguration__Discord__Enabled="false"

# Terminal Settings
export TerminalSettings__MaxTerminals="10"
export TerminalSettings__TerminalTimeout="3600"

# Logging
export Logging__LogLevel__Default="Debug"
export Serilog__MinimumLevel__Default="Information"

# ASP.NET Core
export ASPNETCORE_ENVIRONMENT="Production"
export ASPNETCORE_URLS="http://+:8765;http://+:8766"
```

### Docker Environment Variables
```yaml
# docker-compose.yml
version: '3.8'

services:
  claude-terminal:
    image: claude-code-bot:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - BotConfiguration__Telegram__BotToken=${TELEGRAM_BOT_TOKEN}
      - BotConfiguration__Discord__BotToken=${DISCORD_BOT_TOKEN}
      - TerminalSettings__MaxTerminals=10
      - Logging__LogLevel__Default=Information
    env_file:
      - .env
```

### .env File
```bash
# .env
TELEGRAM_BOT_TOKEN=8092810636:AAFqr7klK41RGUC1xOCCsXr9k4NFyAij6RY
DISCORD_BOT_TOKEN=your_discord_token_here
ASPNETCORE_ENVIRONMENT=Production
CLAUDE_AUTO_UPDATE=true
MAX_TERMINALS=5
LOG_LEVEL=Information
```

## 🔒 Security Configuration

### Authentication and Authorization
```json
{
  "Security": {
    "Authentication": {
      "Enabled": true,
      "TokenValidationParameters": {
        "ValidateIssuer": true,
        "ValidateAudience": true,
        "ValidateLifetime": true,
        "ValidateIssuerSigningKey": true,
        "ValidIssuer": "claude-code-bot",
        "ValidAudience": "claude-users",
        "IssuerSigningKey": "your-secret-key"
      }
    },
    "RateLimiting": {
      "Enabled": true,
      "RequestsPerMinute": 60,
      "BurstSize": 10
    }
  }
}
```

### API Keys and Secrets
```json
{
  "ApiKeys": {
    "TelegramWebhook": "your-webhook-secret",
    "DiscordWebhook": "your-discord-webhook-secret",
    "ClaudeApi": "your-claude-api-key"
  },
  "Encryption": {
    "Key": "your-encryption-key-32-chars-long",
    "Algorithm": "AES256"
  }
}
```

### HTTPS Configuration
```json
{
  "Kestrel": {
    "EndPoints": {
      "Http": {
        "Url": "http://+:8765"
      },
      "Https": {
        "Url": "https://+:8443",
        "Certificate": {
          "Path": "/app/certs/certificate.pfx",
          "Password": "certificate-password"
        }
      }
    }
  }
}
```

## 🔧 Configuration Validation

### Validate Configuration on Startup
```json
{
  "ConfigurationValidation": {
    "ValidateOnStartup": true,
    "StrictMode": true,
    "RequiredSettings": [
      "BotConfiguration:Telegram:BotToken",
      "TerminalSettings:MaxTerminals"
    ]
  }
}
```

### Configuration Schema
```json
{
  "$schema": "https://json.schemastore.org/appsettings.json",
  "type": "object",
  "properties": {
    "BotConfiguration": {
      "type": "object",
      "properties": {
        "Telegram": {
          "type": "object",
          "properties": {
            "Enabled": {"type": "boolean"},
            "BotToken": {"type": "string", "minLength": 1},
            "AllowedChatIds": {"type": "array", "items": {"type": "integer"}}
          },
          "required": ["BotToken"]
        }
      }
    }
  }
}
```

## 🧪 Testing Configuration

### Test Configuration Script
```bash
#!/bin/bash
# test-config.sh

echo "Testing Claude Code Bot Configuration..."

# Test Telegram bot token
if [[ -n "$TELEGRAM_BOT_TOKEN" ]]; then
    echo "✓ Telegram bot token is set"
    curl -s "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/getMe" | jq .
else
    echo "✗ Telegram bot token is not set"
fi

# Test Docker configuration
if docker-compose config > /dev/null 2>&1; then
    echo "✓ Docker Compose configuration is valid"
else
    echo "✗ Docker Compose configuration has errors"
fi

# Test JSON configuration
if jq empty src/Backend/appsettings.json > /dev/null 2>&1; then
    echo "✓ JSON configuration is valid"
else
    echo "✗ JSON configuration has syntax errors"
fi

echo "Configuration test completed."
```

---

**🎯 Next Steps**: See [Troubleshooting Guide](TROUBLESHOOTING.md) for common configuration issues.