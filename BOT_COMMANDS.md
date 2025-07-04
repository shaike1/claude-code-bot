# Claude Code Bot - Complete Command Guide

## 📋 **Overview**
Control Claude Code terminals remotely through Telegram, Discord, WhatsApp, or WebSocket with these commands.

## 🎯 **Core Commands**

### **Terminal Management**
| Command | Syntax | Description |
|---------|--------|-------------|
| `/list` | `/list` | List all active terminals with status |
| `/new` | `/new [custom_id]` | Create new terminal (also `/wsl`) |
| `/kill` | `/kill <terminal_id>` | Terminate specific terminal |
| `/rename` | `/rename <old_id> <new_id>` | Rename terminal |

### **Terminal Interaction**
| Command | Syntax | Description |
|---------|--------|-------------|
| `/<terminal_id>` | `/<terminal_id> [command]` | Execute command in specific terminal |
| `/<terminal_id>` | `/<terminal_id> [number]` | Send choice number to terminal |
| Plain text | `any text` | Send to last active terminal |

### **System Commands**
| Command | Syntax | Description |
|---------|--------|-------------|
| `/help` | `/help` | Show all commands and examples |
| `/settings` | `/settings` | View/modify bot settings |

## 🚀 **Fast Completion Features**

### **Auto-Select First Option**
- **Enable**: Use `/settings` → Toggle "Auto-select first option"
- **Benefit**: Automatically selects first choice in interactive prompts
- **Usage**: Perfect for repetitive tasks

### **Last Active Terminal**
- **Feature**: System remembers your last used terminal
- **Benefit**: No need to specify terminal ID for follow-up commands
- **Usage**: After `/A1a ls`, just type `pwd` (goes to A1a automatically)

### **Command Shortcuts**
| Shortcut | Syntax | Description |
|----------|--------|-------------|
| `//` | `//command` | Remove one slash, send to terminal |
| `./` | `./command` | Send command unchanged to terminal |

## 🎮 **Control Sequences**
| Command | Description |
|---------|-------------|
| `!enter` | Send Enter key |
| `!ctrlc` | Send Ctrl+C |
| `!ctrld` | Send Ctrl+D |
| `!tab` | Send Tab key |
| `!esc` | Send Escape key |

## 📱 **Platform-Specific Features**

### **Telegram**
- ✅ Interactive buttons for choices
- ✅ Markdown formatting
- ✅ Message splitting (4000 char limit)
- ✅ Chat ID authorization

### **Discord**
- ✅ Embed messages with timestamps
- ✅ Button components
- ✅ Guild/Channel authorization

### **WhatsApp (WAHA)**
- ✅ Special `say "message"` command
- ✅ Phone number authorization
- ✅ Webhook/polling support

### **WebSocket**
- ✅ Real-time bidirectional communication
- ✅ JSON message protocol
- ✅ Origin-based access control

## 📝 **Command Examples**

### **Quick Start**
```bash
/new                    # Create new terminal
/list                   # See your terminals
/A1a ls                 # Run 'ls' in terminal A1a
pwd                     # Runs in A1a (last active)
/A1a 1                  # Send choice "1" to terminal A1a
```

### **Advanced Usage**
```bash
/new webdev             # Create terminal with custom ID
/webdev npm install     # Install packages
/webdev npm run dev     # Start development server
/kill webdev           # Terminate when done
```

### **Escaped Commands**
```bash
//usr/bin/env          # Sends: /usr/bin/env
./script.sh            # Sends: ./script.sh
```

## ⚙️ **Configuration Settings**

### **Terminal Settings**
- **Max Terminals**: Maximum concurrent terminals (default: 5)
- **Terminal Timeout**: Auto-close inactive terminals (default: 30 min)
- **Auto-select First**: Skip interactive prompts

### **Channel Settings**
- **Enable/Disable**: Each channel can be toggled
- **Authorization**: Control who can access the bot
- **Custom Tokens**: Configure your bot tokens

## 🔐 **Security & Authorization**

### **Access Control**
- **Telegram**: Chat ID allowlist
- **Discord**: Guild ID + Channel ID allowlist  
- **WhatsApp**: Phone number allowlist
- **WebSocket**: Origin-based access control

### **Web Management**
- **URL**: http://localhost:8765/manage
- **Login**: admin/claude123 (configurable)
- **Features**: Real-time configuration, status monitoring

## 💡 **Pro Tips**

1. **Use Custom IDs**: `/new project1` is easier than `/new` → random ID
2. **Enable Auto-select**: Speeds up repetitive tasks
3. **Use Plain Text**: After selecting a terminal, just type commands
4. **Interactive Buttons**: Click buttons instead of typing numbers
5. **Multiple Channels**: Access same terminals from different platforms

## 🌐 **API Endpoints**

For programmatic access:
- **GET** `/api/configuration/all` - Get all settings
- **POST** `/api/configuration/bot` - Update bot settings
- **GET** `/health` - Health check

## 🎯 **Common Use Cases**

### **Development Workflow**
```bash
/new dev                # Create dev terminal
/dev git status         # Check git status
/dev npm run build      # Build project
/dev npm test           # Run tests
```

### **Server Management**
```bash
/new server             # Create server terminal
/server top             # Monitor processes
/server systemctl status nginx  # Check service status
```

### **Quick Commands**
```bash
/list                   # See all terminals
/A1a                    # Check terminal A1a status
pwd                     # Quick command to active terminal
```

## 🛠️ **Troubleshooting**

### **False Error Messages (FIXED)**
If you previously saw error messages like `[ABC] ❌ Error: your_command`, this has been fixed! The bot now properly filters command echoes and only shows actual errors.

### **Common Issues**
- **Terminal not responding**: Use `/kill <terminal_id>` and create a new one
- **Long outputs**: Large outputs are automatically split into multiple messages  
- **Authentication errors**: Check your chat/channel ID is in the allowlist
- **Commands not working**: Ensure you're using the correct terminal ID format

---

**🚀 Ready to control your Claude Code terminals like a pro!**