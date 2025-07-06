#!/bin/bash

echo "🧪 Testing New Claude Authentication System"
echo "==========================================="
echo ""

echo "📊 Container Status:"
docker ps --format "table {{.Names}}\t{{.Status}}" | grep claude-terminal

echo ""
echo "🔍 Authentication Status Check:"
echo "-------------------------------"

# Check if credentials are properly mounted
echo "📁 Credentials file check:"
if docker exec claude-terminal-orchestra test -f /root/.claude/.credentials.json; then
    echo "✅ Credentials file exists in container"
    echo "📝 File size: $(docker exec claude-terminal-orchestra stat -c%s /root/.claude/.credentials.json) bytes"
else
    echo "❌ Credentials file not found in container"
fi

echo ""
echo "🔑 Testing Authentication Commands:"
echo "-----------------------------------"

echo "Now you can test the new authentication system:"
echo ""
echo "🌐 Web Interface: http://localhost:8765"
echo "   Username: admin"
echo "   Password: claude123"
echo ""
echo "📱 Telegram Bot: @Cody_Code_bot"
echo ""
echo "🧪 Commands to Test:"
echo "   /auth        - Check and setup Claude authentication"
echo "   /login       - Alias for /auth"
echo "   /help        - Show all available commands"
echo "   /new         - Create a new terminal"
echo ""
echo "✨ The new system will:"
echo "   1. 🔍 Automatically check authentication status"
echo "   2. 🔗 Generate authentication URL if needed"
echo "   3. 📱 Send clean instructions to user"
echo "   4. ✅ Persist authentication across restarts"
echo ""
echo "🎯 No more manual button pressing or complex interactions!"
echo "🚀 Try sending '/auth' to any connected channel now!"