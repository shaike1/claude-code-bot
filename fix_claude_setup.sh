#!/bin/bash

echo "=== Claude Code Setup Fix Script ==="

# Option 1: Try to manually complete the setup by creating proper config
echo "Creating complete Claude configuration..."

# Set all required config values
docker exec claude-terminal-orchestra claude config set -g theme dark
docker exec claude-terminal-orchestra claude config set -g editorMode normal  
docker exec claude-terminal-orchestra claude config set -g autoUpdates false

# Option 2: Try to force complete the setup using automation
echo "Attempting automated setup completion..."
docker exec claude-terminal-orchestra bash -c "
cat > /tmp/complete_setup.exp << 'EOF'
#!/usr/bin/expect -f
set timeout 30
spawn claude --version
expect {
    'Choose the text style' {
        send '1\r'
        expect eof
    }
    timeout { puts 'No setup needed' }
    eof { puts 'Version shown directly' }
}
EOF
chmod +x /tmp/complete_setup.exp
/tmp/complete_setup.exp
"

# Option 3: Try to bypass setup with environment variables
echo "Testing with environment bypass..."
docker exec claude-terminal-orchestra bash -c "
export CLAUDE_SKIP_SETUP=1
export ANTHROPIC_THEME=dark
claude --version
"

# Option 4: Check if we can force API key mode
echo "Testing direct API key configuration..."
docker exec claude-terminal-orchestra bash -c "
mkdir -p /root/.anthropic
echo 'test-key' > /root/.anthropic/api_key
claude auth status 2>&1 || echo 'API key test failed'
rm -f /root/.anthropic/api_key
"

echo "=== Setup fix attempts complete ==="