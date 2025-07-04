# Web Management Interface 🌐

Claude Code Bot now includes a comprehensive web management interface for easy configuration and monitoring.

## 🚀 Quick Access

**URL:** `http://localhost:8765/manage`  
**Default Credentials:**
- Username: `admin`
- Password: `claude123`

## ✨ Features

### 📊 Dashboard Overview
- **Channel Status**: Real-time status of all communication channels
- **Active Terminals**: Monitor running terminal sessions
- **System Information**: Memory usage, uptime, CPU cores
- **Health Monitoring**: System health and performance metrics

### ⚙️ Configuration Management

#### 🤖 Bot Channels
- **Telegram**: Configure bot token, allowed chat IDs
- **Discord**: Setup bot token, guild/channel permissions  
- **WhatsApp (WAHA)**: Configure WAHA URL, session, phone numbers
- **WebSocket**: Port configuration and CORS origins

#### 🖥️ Terminal Settings
- **Max Terminals**: Limit concurrent terminal sessions
- **Timeout**: Configure session timeout periods
- **Auto Options**: Automatic selection preferences

#### 🔗 Claude Hooks
- **Enable/Disable**: Control Claude integration hooks
- **Port Configuration**: WebSocket port settings

### 🔒 Security Features
- **Basic Authentication**: Username/password protection
- **Environment Override**: Configure credentials via environment variables
- **Session Management**: Secure web session handling

## 📋 Configuration Tabs

### 1. Overview Tab
- Channel status cards with real-time indicators
- Active terminal list with session details
- System resource monitoring
- Health check endpoints

### 2. Telegram Tab
```json
{
  "enabled": true,
  "botToken": "your_bot_token",
  "allowedChatIds": [123456789]
}
```

### 3. Discord Tab
```json
{
  "enabled": true,
  "botToken": "your_discord_token", 
  "allowedGuildIds": [987654321],
  "allowedChannelIds": [123456789]
}
```

### 4. WhatsApp Tab
```json
{
  "enabled": true,
  "wahaUrl": "http://localhost:3000",
  "sessionName": "default",
  "allowedNumbers": ["+1234567890@c.us"],
  "useWebhook": false
}
```

### 5. WebSocket Tab
```json
{
  "enabled": false,
  "port": 8766,
  "allowedOrigins": ["http://localhost:3000"]
}
```

### 6. Terminal Tab
```json
{
  "maxTerminals": 5,
  "terminalTimeout": 1800,
  "autoSelectFirstOption": false
}
```

### 7. Claude Hooks Tab
```json
{
  "enableHooks": true,
  "webSocketPort": 8765
}
```

## 🔧 Setup Instructions

### 1. Access the Interface
```bash
# Start the bot
docker-compose up -d

# Open browser
open http://localhost:8765/manage
```

### 2. Login
- Enter username: `admin`
- Enter password: `claude123`
- Click "Login" or press Enter

### 3. Configure Channels
1. Navigate to the desired channel tab
2. Toggle "Enable" switch
3. Fill in required configuration
4. Click "Save Settings"
5. Monitor status in Overview tab

## 🌍 Environment Configuration

Override default credentials with environment variables:

```bash
# Docker Compose
environment:
  - WebUI__Username=your_admin_user
  - WebUI__Password=your_secure_password

# Docker Run
docker run -e WebUI__Username=admin \
           -e WebUI__Password=secure123 \
           claude-code-bot
```

## 📡 API Endpoints

The web interface uses these REST API endpoints:

### Configuration Endpoints
- `GET /api/configuration/all` - Get all configuration
- `POST /api/configuration/bot` - Update bot configuration
- `POST /api/configuration/terminal` - Update terminal settings
- `POST /api/configuration/websocket` - Update WebSocket settings
- `POST /api/configuration/hooks` - Update Claude hooks

### Status Endpoints  
- `GET /api/status/channels` - Channel status
- `GET /api/status/terminals` - Active terminals
- `GET /api/status/system` - System information

### Health Check
- `GET /health` - Application health status

## 🔄 Real-time Updates

The interface automatically refreshes:
- **System Status**: Every 30 seconds
- **Terminal List**: Every 30 seconds  
- **Configuration**: On manual save
- **Channel Status**: On configuration change

## 📱 Mobile Responsive

The web interface is fully responsive and works on:
- **Desktop**: Full feature set
- **Tablet**: Optimized layout
- **Mobile**: Touch-friendly interface

## 🎨 UI Components

### Status Cards
- **Green**: Service enabled and running
- **Red**: Service disabled or error
- **Blue**: Information/neutral status
- **Yellow**: Warning or attention needed

### Form Controls
- **Toggle Switches**: Enable/disable features
- **Input Fields**: Configuration values
- **Save Buttons**: Apply changes
- **Alert Messages**: Success/error feedback

## 🔍 Troubleshooting

### Common Issues

**Can't Access Web Interface:**
```bash
# Check if service is running
docker ps | grep claude

# Check logs  
docker logs claude-terminal-orchestra

# Verify port mapping
docker port claude-terminal-orchestra
```

**Authentication Failed:**
```bash
# Check environment variables
docker exec claude-terminal-orchestra env | grep WebUI

# Reset to defaults
docker restart claude-terminal-orchestra
```

**Configuration Not Saving:**
```bash
# Check file permissions
docker exec claude-terminal-orchestra ls -la appsettings.json

# Check logs for errors
docker logs claude-terminal-orchestra | grep ERROR
```

### Debug Mode
Enable detailed logging:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "ClaudeMobileTerminal": "Trace"
    }
  }
}
```

## 🚀 Advanced Usage

### Custom Styling
Add custom CSS by mounting a volume:
```yaml
volumes:
  - ./custom.css:/app/wwwroot/css/custom.css
```

### Reverse Proxy Setup
Configure nginx for production:
```nginx
server {
    listen 80;
    server_name your-domain.com;
    
    location / {
        proxy_pass http://localhost:8765;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}
```

### SSL/HTTPS
Configure HTTPS in production:
```bash
# Generate certificate
openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 365

# Mount certificates
docker run -v ./cert.pem:/app/cert.pem \
           -v ./key.pem:/app/key.pem \
           -e ASPNETCORE_URLS="https://+:8765" \
           claude-code-bot
```

---

**🎯 The web interface makes Claude Code Bot management intuitive and accessible from any device!**