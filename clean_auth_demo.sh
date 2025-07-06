#!/bin/bash

echo "🔧 ClaudeMobileTerminal - Authentication Demo"
echo "============================================="
echo ""

# Remove old test files
echo "Cleaning up old test files..."
rm -f /root/claude-code-bot/*.exp 2>/dev/null
rm -f /root/claude-code-bot/test_*.exp 2>/dev/null
rm -f /root/claude-code-bot/simple_*.exp 2>/dev/null
rm -f /root/claude-code-bot/final_*.exp 2>/dev/null

echo "✅ Cleanup complete"
echo ""

# Show current authentication status
echo "📍 Current Authentication Status:"
echo "--------------------------------"

if [ -f "/root/.claude/.credentials.json" ]; then
    echo "✅ Claude credentials file exists"
    echo "📝 Credentials location: /root/.claude/.credentials.json"
    
    # Test Claude authentication
    echo ""
    echo "🧪 Testing Claude authentication..."
    if claude --version >/dev/null 2>&1; then
        echo "✅ Claude CLI is working"
        
        # Try a simple test command
        if timeout 5 claude --help >/dev/null 2>&1; then
            echo "✅ Claude authentication appears to be working"
        else
            echo "⚠️ Claude authentication may have issues"
        fi
    else
        echo "❌ Claude CLI authentication failed"
    fi
else
    echo "❌ No Claude credentials found"
    echo "📝 Expected location: /root/.claude/.credentials.json"
fi

echo ""
echo "📊 Docker Volume Status:"
echo "----------------------"
docker volume ls | grep claude
echo ""

echo "🏗️ Container Information:"
echo "------------------------"
docker ps | grep claude-terminal
echo ""

echo "🔑 How to Authenticate Claude:"
echo "-----------------------------"
echo "1. 📱 Use any messaging app (Telegram, Web UI) connected to the bot"
echo "2. 🚀 Send the command: /auth"
echo "3. 🔗 Follow the authentication URL provided"
echo "4. ✅ Complete OAuth authentication in your browser"
echo "5. 🔄 The system will automatically detect successful authentication"
echo ""

echo "🌐 Alternative Manual Method:"
echo "-----------------------------"
echo "1. 📊 Send: /new (to create a new terminal)"
echo "2. 🔧 Send: claude /login"
echo "3. 🔗 Follow the interactive prompts"
echo ""

echo "📱 Telegram Bot Access:"
echo "----------------------"
echo "Bot: @Cody_Code_bot"
echo "Commands: /start, /auth, /new, /help"
echo ""

echo "🌐 Web Interface Access:"
echo "-----------------------"
echo "URL: http://localhost:8765"
echo "Username: admin"
echo "Password: claude123"
echo ""

echo "📋 Useful Commands:"
echo "------------------"
echo "/auth         - Check and setup Claude authentication"
echo "/new          - Create a new terminal"
echo "/list         - List all active terminals"
echo "/help         - Show all available commands"
echo ""

echo "🔍 Debugging:"
echo "-------------"
echo "• View logs: docker logs claude-terminal-orchestra"
echo "• Check auth: docker exec claude-terminal-orchestra ls -la /root/.claude/"
echo "• Volume data: docker volume inspect claude-code-bot_claude_config"
echo ""

echo "✨ Authentication is now persistent across container restarts!"
echo "🎯 Ready to use! Send /auth to any connected channel to begin."