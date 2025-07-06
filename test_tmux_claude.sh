#!/bin/bash

echo "=== Testing Claude with proper TTY via tmux ==="

# Create a tmux session and run claude login inside it
docker exec claude-terminal-orchestra bash -c "
# Start a tmux session and run claude login
tmux new-session -d -s claude_login 'claude /login'

# Wait a moment for it to start
sleep 3

# Send the theme selection (option 1)
tmux send-keys -t claude_login '1' Enter

# Wait for next screen
sleep 3

# Capture the current screen content
echo '=== TMUX SCREEN CONTENT ==='
tmux capture-pane -t claude_login -p

# Try to send web login option if we see the menu
tmux send-keys -t claude_login '1' Enter

# Wait and capture again
sleep 3
echo '=== AFTER WEB LOGIN SELECTION ==='
tmux capture-pane -t claude_login -p

# Kill the session
tmux kill-session -t claude_login
"