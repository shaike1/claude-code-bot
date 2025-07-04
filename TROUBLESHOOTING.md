# Troubleshooting Guide 🔧

Common issues and solutions for Claude Code Bot.

## 📋 Quick Diagnostics

### Health Check Commands
```bash
# Check container status
docker ps | grep claude

# Check logs
docker logs claude-code-bot --tail 50

# Check configuration
docker exec claude-code-bot cat /app/appsettings.json

# Test bot token
curl -s "https://api.telegram.org/bot<TOKEN>/getMe"
```

## 🤖 Bot Issues

### Bot Not Responding

**Symptoms:**
- Bot doesn't respond to messages
- No logs showing incoming messages

**Solutions:**
```bash
# 1. Check bot token
curl -s "https://api.telegram.org/bot$BOT_TOKEN/getMe"

# 2. Check if bot is started
# Message @BotFather -> /mybots -> Select your bot -> Check status

# 3. Verify webhook settings
curl -s "https://api.telegram.org/bot$BOT_TOKEN/getWebhookInfo"

# 4. Delete webhook if using polling
curl -s "https://api.telegram.org/bot$BOT_TOKEN/deleteWebhook"

# 5. Check allowed chat IDs
docker logs claude-code-bot | grep "Unauthorized chat"
```

### Invalid Bot Token

**Error Message:**
```
[ERROR] Failed to start Telegram bot: Unauthorized
```

**Solutions:**
1. Verify token format: `123456789:ABC-DEF1234ghIkl-zyx57W2v1u123ew11`
2. Check token in @BotFather
3. Ensure no extra spaces in configuration
4. Regenerate token if needed

### Bot Commands Not Working

**Symptoms:**
- `/new` command not recognized
- Bot responds but commands fail

**Solutions:**
```bash
# Check bot commands are set
curl -s "https://api.telegram.org/bot$BOT_TOKEN/getMyCommands"

# Set bot commands manually
curl -X POST "https://api.telegram.org/bot$BOT_TOKEN/setMyCommands" \
  -H "Content-Type: application/json" \
  -d '{
    "commands": [
      {"command": "new", "description": "Create new terminal"},
      {"command": "list", "description": "List active terminals"},
      {"command": "help", "description": "Show help"}
    ]
  }'
```

## 🐳 Docker Issues

### Container Won't Start

**Error Message:**
```
Error response from daemon: Ports are not available
```

**Solutions:**
```bash
# Check port availability
netstat -tulpn | grep :8765
lsof -i :8765

# Use different ports
docker run -p 8767:8765 -p 8768:8766 claude-code-bot

# Kill processes using ports
sudo fuser -k 8765/tcp
```

### Container Keeps Restarting

**Symptoms:**
- Container status shows "Restarting"
- Exit codes 125, 126, 127

**Solutions:**
```bash
# Check exit code
docker ps -a | grep claude

# Common exit codes:
# 125: Docker daemon error
# 126: Container command not executable
# 127: Container command not found

# Check logs for specific error
docker logs claude-code-bot --tail 100

# Remove and recreate container
docker-compose down
docker-compose up --build -d
```

### Out of Memory

**Error Message:**
```
[ERROR] System.OutOfMemoryException
```

**Solutions:**
```bash
# Check memory usage
docker stats claude-code-bot

# Increase memory limit
docker run --memory=1g claude-code-bot

# In docker-compose.yml:
deploy:
  resources:
    limits:
      memory: 1G
    reservations:
      memory: 512M
```

## ⚙️ Configuration Issues

### JSON Syntax Errors

**Error Message:**
```
[ERROR] Configuration error: Invalid JSON
```

**Solutions:**
```bash
# Validate JSON syntax
jq empty src/Backend/appsettings.json

# Common issues:
# - Missing commas
# - Trailing commas
# - Unescaped quotes
# - Wrong brackets

# Use online JSON validator
# https://jsonlint.com/
```

### Missing Configuration Values

**Error Message:**
```
[ERROR] Required configuration 'BotToken' is missing
```

**Solutions:**
```bash
# Check environment variables
env | grep BOT

# Verify configuration override
docker exec claude-code-bot env | grep BotConfiguration

# Check file permissions
docker exec claude-code-bot ls -la /app/appsettings.json
```

### Environment Variable Override Not Working

**Issue:** Environment variables not overriding configuration

**Solutions:**
```bash
# Correct format for nested properties
export BotConfiguration__Telegram__BotToken="your_token"

# Not this:
export BotConfiguration.Telegram.BotToken="your_token"

# Check Docker environment
docker exec claude-code-bot printenv | grep BotConfiguration

# Restart container after environment changes
docker-compose restart
```

## 🔌 Connection Issues

### WebSocket Connection Failed

**Error Message:**
```
WebSocket connection failed: Error 1006
```

**Solutions:**
```bash
# Check if ports are exposed
docker port claude-code-bot

# Test WebSocket connection
wscat -c ws://localhost:8766

# Check firewall rules
sudo ufw status
sudo iptables -L

# Enable CORS if needed
"AllowedOrigins": ["*"]  # For testing only
```

### Network Issues

**Symptoms:**
- Cannot reach external APIs
- DNS resolution fails

**Solutions:**
```bash
# Test network connectivity from container
docker exec claude-code-bot ping google.com
docker exec claude-code-bot nslookup api.telegram.org

# Check Docker network
docker network ls
docker network inspect bridge

# Restart Docker daemon
sudo systemctl restart docker
```

## 🖥️ Terminal Issues

### Terminal Creation Fails

**Error Message:**
```
[ERROR] Failed to create terminal: Access denied
```

**Solutions:**
```bash
# Check container permissions
docker exec claude-code-bot whoami
docker exec claude-code-bot ls -la /

# Run with correct user
docker run --user root claude-code-bot

# Check available shells
docker exec claude-code-bot cat /etc/shells
```

### Claude Code Not Found

**Error Message:**
```
claude: command not found
```

**Solutions:**
```bash
# Install Claude Code in container
# Add to Dockerfile:
RUN curl -fsSL https://claude.ai/install.sh | bash

# Or mount Claude binary
docker run -v /usr/local/bin/claude:/usr/local/bin/claude claude-code-bot

# Check PATH environment
docker exec claude-code-bot echo $PATH
```

### Permission Denied

**Error Message:**
```
/bin/bash: Permission denied
```

**Solutions:**
```bash
# Check file permissions
docker exec claude-code-bot ls -la /bin/bash

# Fix permissions
docker exec --user root claude-code-bot chmod +x /bin/bash

# Use different shell
export TerminalSettings__DefaultShell="/bin/sh"
```

## 📊 Performance Issues

### High Memory Usage

**Symptoms:**
- Container using excessive memory
- OOM killer activated

**Solutions:**
```bash
# Monitor memory usage
docker stats claude-code-bot

# Limit memory
docker run --memory=512m claude-code-bot

# Check for memory leaks
docker exec claude-code-bot top
docker exec claude-code-bot ps aux

# Garbage collection tuning
export DOTNET_gcServer=1
export DOTNET_GCRetainVM=1
```

### High CPU Usage

**Solutions:**
```bash
# Check CPU usage
docker stats claude-code-bot

# Limit CPU
docker run --cpus=0.5 claude-code-bot

# Check running processes
docker exec claude-code-bot ps aux --sort=-%cpu

# Profile application
docker exec claude-code-bot dotnet-trace collect -p 1
```

### Slow Response Times

**Solutions:**
```bash
# Check network latency
docker exec claude-code-bot ping api.telegram.org

# Monitor logs for slow operations
docker logs claude-code-bot | grep "took"

# Increase timeouts
export TerminalSettings__TerminalTimeout=3600
export BotConfiguration__Telegram__RequestTimeout=60
```

## 🔍 Debugging

### Enable Debug Logging

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

### Application Insights

```bash
# Add Application Insights
export APPLICATIONINSIGHTS_CONNECTION_STRING="your_connection_string"

# Monitor in Azure portal
```

### Debugging in Container

```bash
# Access container shell
docker exec -it claude-code-bot /bin/bash

# Install debugging tools
apt-get update && apt-get install -y curl wget net-tools

# Check processes
ps aux

# Check network connections
netstat -tulpn

# Check file descriptors
lsof

# Monitor system calls
strace -p 1
```

### Trace Network Calls

```bash
# Enable HTTP logging
export DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION=1

# Use tcpdump to capture traffic
docker exec claude-code-bot tcpdump -i any -w /tmp/capture.pcap

# Analyze with Wireshark
```

## 🚨 Emergency Procedures

### Complete Reset

```bash
# Stop everything
docker-compose down -v

# Remove images
docker rmi $(docker images claude-code-bot -q)

# Clean build cache
docker builder prune -a

# Rebuild from scratch
docker-compose up --build -d
```

### Backup Current State

```bash
# Backup configuration
docker cp claude-code-bot:/app/appsettings.json ./backup/

# Backup logs
docker cp claude-code-bot:/app/logs ./backup/

# Export container
docker export claude-code-bot > claude-backup.tar
```

### Recovery

```bash
# Import backup
docker import claude-backup.tar claude-code-bot:backup

# Restore configuration
docker cp ./backup/appsettings.json claude-code-bot:/app/

# Restart services
docker-compose restart
```

## 📞 Getting Help

### Log Collection Script

```bash
#!/bin/bash
# collect-logs.sh

echo "Collecting Claude Code Bot diagnostic information..."

mkdir -p debug-info
cd debug-info

# System information
uname -a > system-info.txt
docker version > docker-version.txt
docker-compose version > compose-version.txt

# Container information
docker ps > container-status.txt
docker images | grep claude > image-info.txt
docker logs claude-code-bot > container-logs.txt

# Configuration
docker exec claude-code-bot cat /app/appsettings.json > config.json

# Network information
docker port claude-code-bot > port-mapping.txt
docker network ls > networks.txt

# Resource usage
docker stats --no-stream claude-code-bot > resource-usage.txt

echo "Debug information collected in debug-info/ directory"
tar -czf claude-debug-$(date +%Y%m%d-%H%M%S).tar.gz *
```

### Support Checklist

Before reporting issues, provide:

1. **Environment Information:**
   - Operating System
   - Docker version
   - Container logs
   - Configuration (redacted)

2. **Error Details:**
   - Exact error message
   - Steps to reproduce
   - Expected vs actual behavior

3. **Diagnostic Information:**
   - Container status
   - Network configuration
   - Resource usage

---

**🎯 Need more help?** Check the [GitHub Issues](https://github.com/your-repo/claude-code-bot/issues) or create a new issue with the debug information.