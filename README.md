# Claude Code Bot 🤖

Control Claude Code terminals remotely via Telegram, Discord, or WebSocket with clean, filtered output.

![Docker](https://img.shields.io/badge/docker-%230db7ed.svg?style=for-the-badge&logo=docker&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=.net&logoColor=white)
![Telegram](https://img.shields.io/badge/Telegram-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)
![Discord](https://img.shields.io/badge/Discord-%235865F2.svg?style=for-the-badge&logo=discord&logoColor=white)

## 🚀 Quick Start

### Prerequisites
- Docker and Docker Compose
- Telegram Bot Token (from @BotFather)

### 1. Clone and Configure
```bash
git clone <your-repository-url>
cd claude-code-bot
```

### 2. Setup Telegram Bot
1. Message [@BotFather](https://t.me/botfather) on Telegram
2. Send `/newbot` and follow instructions
3. Copy your bot token
4. Edit `src/Backend/appsettings.json`:
```json
{
  "BotConfiguration": {
    "Telegram": {
      "Enabled": true,
      "BotToken": "YOUR_BOT_TOKEN_HERE",
      "AllowedChatIds": []
    }
  }
}
```

### 3. Deploy with Docker
```bash
docker-compose up --build -d
```

### 4. Start Using
1. Find your bot on Telegram
2. Send `/new` to create a terminal
3. Send `claude` to start Claude Code
4. Use `say "your command"` to send commands to Claude

## 📋 Features

- **🔌 Multiple Channels**: Telegram, Discord, WhatsApp (WAHA), WebSocket support
- **🖥️ Remote Terminal Control**: Full Claude Code terminal access
- **🔍 Clean Output**: Filtered and processed terminal output
- **🚀 Easy Deployment**: One-command Docker deployment
- **🔒 Secure**: Token-based authentication and user restrictions
- **📱 Mobile Friendly**: Perfect for mobile development workflows

## 🎯 Use Cases

- **Remote Development**: Access Claude Code from your phone
- **Team Collaboration**: Share Claude terminal sessions
- **Mobile Workflows**: Run Claude commands during commutes
- **Documentation**: Generate clean command outputs
- **Multi-Device Access**: Switch between desktop and mobile seamlessly

## 📖 Commands

### Bot Commands
- `/new` - Create a new terminal session
- `/list` - Show active terminals
- `/kill <id>` - Close a specific terminal
- `/help` - Show available commands

### Terminal Commands
- `claude` - Start Claude Code in the terminal
- `say "command"` - Send command to Claude
- `//command` - Run command directly on terminal (bypass bot)

## 🐳 Docker Deployment

### Using Docker Compose (Recommended)
```bash
# Build and start
docker-compose up --build -d

# View logs
docker-compose logs -f

# Stop
docker-compose down

# Restart
docker-compose restart
```

### Using Docker directly
```bash
# Build image
docker build -t claude-code-bot .

# Run container
docker run -d \
  -p 8765:8765 \
  -p 8766:8766 \
  -v ./src/Backend/appsettings.json:/app/appsettings.json:ro \
  --name claude-code-bot \
  claude-code-bot
```

## 🔧 Configuration

### Telegram Setup
1. Get bot token from [@BotFather](https://t.me/botfather)
2. Get your chat ID from [@userinfobot](https://t.me/userinfobot)
3. Update `appsettings.json`:
```json
{
  "BotConfiguration": {
    "Telegram": {
      "Enabled": true,
      "BotToken": "YOUR_TOKEN",
      "AllowedChatIds": [123456789]
    }
  }
}
```

### Discord Setup
1. Create application at [Discord Developer Portal](https://discord.com/developers/applications)
2. Create bot and get token
3. Add bot to server with proper permissions
4. Update configuration:
```json
{
  "BotConfiguration": {
    "Discord": {
      "Enabled": true,
      "BotToken": "YOUR_DISCORD_TOKEN",
      "AllowedGuildIds": [987654321],
      "AllowedChannelIds": [123456789]
    }
  }
}
```

### WhatsApp Setup (WAHA)
1. Install WAHA (WhatsApp HTTP API):
```bash
docker run -it --rm -p 3000:3000/tcp devlikeapro/waha-plus
```
2. Start a WhatsApp session and scan QR code:
```bash
curl -X POST http://localhost:3000/api/sessions/start \
  -H "Content-Type: application/json" \
  -d '{"name": "default"}'
```
3. Get QR code and scan with WhatsApp:
```bash
curl http://localhost:3000/api/sessions/default/auth/qr
```
4. Update configuration:
```json
{
  "BotConfiguration": {
    "WhatsApp": {
      "Enabled": true,
      "WAHAUrl": "http://localhost:3000",
      "SessionName": "default",
      "AllowedNumbers": ["+1234567890"]
    }
  }
}
```

### WebSocket Setup
```json
{
  "WebSocketChannel": {
    "Enabled": true,
    "Port": 8766,
    "AllowedOrigins": ["http://localhost:3000"]
  }
}
```

## 🌐 Ports

- **8765**: WebSocket port for Claude hooks
- **8766**: WebSocket port for web interface

## 🔒 Security

- Use `AllowedChatIds` to restrict Telegram access
- Use `AllowedGuildIds` and `AllowedChannelIds` for Discord
- Configure `AllowedOrigins` for WebSocket connections
- Consider running behind a reverse proxy for production
- Never commit tokens to version control

## 🛠️ Development

### Local Development
```bash
# Navigate to backend
cd src/Backend

# Restore dependencies
dotnet restore

# Run application
dotnet run
```

### Building
```bash
# Build for release
dotnet build -c Release

# Publish
dotnet publish -c Release -o ./publish
```

## 📝 Logs

View application logs:
```bash
# Docker Compose
docker-compose logs -f claude-terminal

# Docker
docker logs claude-code-bot -f

# Local files (if volume mounted)
tail -f ./logs/app.log
```

## 🐛 Troubleshooting

### Common Issues

**Bot not responding:**
- Check bot token in configuration
- Verify bot is started (@BotFather /mybots)
- Check allowed chat IDs

**Container won't start:**
- Check Docker logs: `docker logs claude-code-bot`
- Verify configuration file syntax
- Check port availability

**Permission errors:**
- Ensure proper file permissions
- Check Docker volume mounts
- Verify user permissions

### Debug Mode
Enable debug logging in `appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "ClaudeMobileTerminal": "Debug"
    }
  }
}
```

## 📜 License

This project is dual-licensed:
- **Open Source Use**: Apache 2.0 License for non-commercial use
- **Commercial Use**: Custom license requiring attribution

See [LICENSE](LICENSE) for full details.

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Test thoroughly
5. Submit a pull request

## 📞 Support

- Create an issue for bugs or feature requests
- Check existing issues before creating new ones
- Provide detailed information and logs

## ⭐ Star History

If this project helps you, please consider giving it a star!

---

**Made with ❤️ for the Claude Code community**