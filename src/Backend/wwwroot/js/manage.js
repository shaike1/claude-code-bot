class ConfigManager {
    constructor() {
        this.config = {};
        this.init();
    }

    async init() {
        await this.loadConfiguration();
        this.setupEventListeners();
        this.loadSystemStatus();
        
        // Auto-refresh status every 30 seconds
        setInterval(() => this.loadSystemStatus(), 30000);
    }

    async loadConfiguration() {
        try {
            const response = await fetch('/api/configuration/all');
            if (response.ok) {
                this.config = await response.json();
                this.populateForm();
            } else {
                this.showAlert('danger', 'Failed to load configuration');
            }
        } catch (error) {
            this.showAlert('danger', `Error loading configuration: ${error.message}`);
        }
    }

    populateForm() {
        // Telegram
        const telegram = this.config.botConfiguration?.telegram || {};
        document.getElementById('telegramEnabled').checked = telegram.enabled || false;
        document.getElementById('telegramToken').value = telegram.botToken || '';
        document.getElementById('telegramChatIds').value = (telegram.allowedChatIds || []).join(',');

        // Discord
        const discord = this.config.botConfiguration?.discord || {};
        document.getElementById('discordEnabled').checked = discord.enabled || false;
        document.getElementById('discordToken').value = discord.botToken || '';
        document.getElementById('discordGuildIds').value = (discord.allowedGuildIds || []).join(',');
        document.getElementById('discordChannelIds').value = (discord.allowedChannelIds || []).join(',');

        // WhatsApp
        const whatsapp = this.config.botConfiguration?.whatsApp || {};
        document.getElementById('whatsappEnabled').checked = whatsapp.enabled || false;
        document.getElementById('wahaUrl').value = whatsapp.wahaUrl || 'http://localhost:3000';
        document.getElementById('sessionName').value = whatsapp.sessionName || 'default';
        document.getElementById('whatsappNumbers').value = (whatsapp.allowedNumbers || []).join(',');
        document.getElementById('useWebhook').checked = whatsapp.useWebhook || false;
        document.getElementById('webhookUrl').value = whatsapp.webhookUrl || '';
        this.toggleWebhookUrl();

        // WebSocket
        const websocket = this.config.webSocketChannel || {};
        document.getElementById('websocketEnabled').checked = websocket.enabled || false;
        document.getElementById('websocketPort').value = websocket.port || 8766;
        document.getElementById('allowedOrigins').value = (websocket.allowedOrigins || []).join(',');

        // Terminal
        const terminal = this.config.terminalSettings || {};
        document.getElementById('maxTerminals').value = terminal.maxTerminals || 5;
        document.getElementById('terminalTimeout').value = terminal.terminalTimeout || 1800;
        document.getElementById('autoSelectFirstOption').checked = terminal.autoSelectFirstOption || false;

        // Claude Hooks
        const hooks = this.config.claudeHooks || {};
        document.getElementById('enableHooks').checked = hooks.enableHooks || false;
        document.getElementById('hooksPort').value = hooks.webSocketPort || 8765;
    }

    setupEventListeners() {
        // Form submissions
        document.getElementById('telegramForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'telegram'));
        document.getElementById('discordForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'discord'));
        document.getElementById('whatsappForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'whatsapp'));
        document.getElementById('websocketForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'websocket'));
        document.getElementById('terminalForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'terminal'));
        document.getElementById('hooksForm').addEventListener('submit', (e) => this.handleFormSubmit(e, 'hooks'));

        // Webhook toggle
        document.getElementById('useWebhook').addEventListener('change', () => this.toggleWebhookUrl());
    }

    toggleWebhookUrl() {
        const useWebhook = document.getElementById('useWebhook').checked;
        const webhookUrlDiv = document.getElementById('webhookUrlDiv');
        webhookUrlDiv.style.display = useWebhook ? 'block' : 'none';
    }

    async handleFormSubmit(event, configType) {
        event.preventDefault();
        
        const submitButton = event.target.querySelector('button[type="submit"]');
        const originalText = submitButton.innerHTML;
        submitButton.innerHTML = '<i class="fas fa-spinner fa-spin me-2"></i>Saving...';
        submitButton.disabled = true;

        try {
            let configData;
            
            switch (configType) {
                case 'telegram':
                    configData = {
                        telegram: {
                            enabled: document.getElementById('telegramEnabled').checked,
                            botToken: document.getElementById('telegramToken').value,
                            allowedChatIds: this.parseNumberArray(document.getElementById('telegramChatIds').value)
                        },
                        discord: this.config.botConfiguration?.discord || {},
                        whatsApp: this.config.botConfiguration?.whatsApp || {}
                    };
                    await this.saveConfiguration('bot', configData);
                    break;

                case 'discord':
                    configData = {
                        telegram: this.config.botConfiguration?.telegram || {},
                        discord: {
                            enabled: document.getElementById('discordEnabled').checked,
                            botToken: document.getElementById('discordToken').value,
                            allowedGuildIds: this.parseNumberArray(document.getElementById('discordGuildIds').value),
                            allowedChannelIds: this.parseNumberArray(document.getElementById('discordChannelIds').value)
                        },
                        whatsApp: this.config.botConfiguration?.whatsApp || {}
                    };
                    await this.saveConfiguration('bot', configData);
                    break;

                case 'whatsapp':
                    configData = {
                        telegram: this.config.botConfiguration?.telegram || {},
                        discord: this.config.botConfiguration?.discord || {},
                        whatsApp: {
                            enabled: document.getElementById('whatsappEnabled').checked,
                            wahaUrl: document.getElementById('wahaUrl').value,
                            sessionName: document.getElementById('sessionName').value,
                            allowedNumbers: this.parseStringArray(document.getElementById('whatsappNumbers').value),
                            useWebhook: document.getElementById('useWebhook').checked,
                            webhookUrl: document.getElementById('webhookUrl').value,
                            requestTimeout: 30
                        }
                    };
                    await this.saveConfiguration('bot', configData);
                    break;

                case 'websocket':
                    configData = {
                        enabled: document.getElementById('websocketEnabled').checked,
                        port: parseInt(document.getElementById('websocketPort').value) || 8766,
                        allowedOrigins: this.parseStringArray(document.getElementById('allowedOrigins').value)
                    };
                    await this.saveConfiguration('websocket', configData);
                    break;

                case 'terminal':
                    configData = {
                        maxTerminals: parseInt(document.getElementById('maxTerminals').value) || 5,
                        terminalTimeout: parseInt(document.getElementById('terminalTimeout').value) || 1800,
                        autoSelectFirstOption: document.getElementById('autoSelectFirstOption').checked
                    };
                    await this.saveConfiguration('terminal', configData);
                    break;

                case 'hooks':
                    configData = {
                        enableHooks: document.getElementById('enableHooks').checked,
                        webSocketPort: parseInt(document.getElementById('hooksPort').value) || 8765
                    };
                    await this.saveConfiguration('hooks', configData);
                    break;
            }

            this.showAlert('success', `${configType.charAt(0).toUpperCase() + configType.slice(1)} configuration saved successfully!`);
        } catch (error) {
            this.showAlert('danger', `Failed to save ${configType} configuration: ${error.message}`);
        } finally {
            submitButton.innerHTML = originalText;
            submitButton.disabled = false;
        }
    }

    async saveConfiguration(endpoint, data) {
        const response = await fetch(`/api/configuration/${endpoint}`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(data)
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Unknown error');
        }

        return response.json();
    }

    async loadSystemStatus() {
        try {
            // Load terminals
            const terminalsResponse = await fetch('/api/status/terminals');
            if (terminalsResponse.ok) {
                const terminalsData = await terminalsResponse.json();
                this.updateTerminalsList(terminalsData);
            }

            // Load system info
            const systemResponse = await fetch('/api/status/system');
            if (systemResponse.ok) {
                const systemData = await systemResponse.json();
                this.updateSystemInfo(systemData);
            }

            // Update status cards
            this.updateStatusCards();
        } catch (error) {
            console.error('Failed to load system status:', error);
        }
    }

    updateStatusCards() {
        const statusCardsHtml = `
            <div class="col-md-3">
                <div class="card text-white bg-success">
                    <div class="card-body">
                        <div class="d-flex justify-content-between">
                            <div>
                                <h6 class="card-title">Telegram</h6>
                                <span class="badge status-badge ${this.config.botConfiguration?.telegram?.enabled ? 'bg-light text-success' : 'bg-light text-danger'}">
                                    ${this.config.botConfiguration?.telegram?.enabled ? 'Enabled' : 'Disabled'}
                                </span>
                            </div>
                            <i class="fab fa-telegram fa-2x"></i>
                        </div>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-primary">
                    <div class="card-body">
                        <div class="d-flex justify-content-between">
                            <div>
                                <h6 class="card-title">Discord</h6>
                                <span class="badge status-badge ${this.config.botConfiguration?.discord?.enabled ? 'bg-light text-primary' : 'bg-light text-danger'}">
                                    ${this.config.botConfiguration?.discord?.enabled ? 'Enabled' : 'Disabled'}
                                </span>
                            </div>
                            <i class="fab fa-discord fa-2x"></i>
                        </div>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-success">
                    <div class="card-body">
                        <div class="d-flex justify-content-between">
                            <div>
                                <h6 class="card-title">WhatsApp</h6>
                                <span class="badge status-badge ${this.config.botConfiguration?.whatsApp?.enabled ? 'bg-light text-success' : 'bg-light text-danger'}">
                                    ${this.config.botConfiguration?.whatsApp?.enabled ? 'Enabled' : 'Disabled'}
                                </span>
                            </div>
                            <i class="fab fa-whatsapp fa-2x"></i>
                        </div>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-info">
                    <div class="card-body">
                        <div class="d-flex justify-content-between">
                            <div>
                                <h6 class="card-title">WebSocket</h6>
                                <span class="badge status-badge ${this.config.webSocketChannel?.enabled ? 'bg-light text-info' : 'bg-light text-danger'}">
                                    ${this.config.webSocketChannel?.enabled ? 'Enabled' : 'Disabled'}
                                </span>
                            </div>
                            <i class="fas fa-plug fa-2x"></i>
                        </div>
                    </div>
                </div>
            </div>
        `;
        
        document.getElementById('statusCards').innerHTML = statusCardsHtml;
    }

    updateTerminalsList(data) {
        const terminalsHtml = data.terminals.length > 0 
            ? data.terminals.map(terminal => `
                <div class="d-flex justify-content-between align-items-center border-bottom pb-2 mb-2">
                    <div>
                        <strong>Terminal ${terminal.id}</strong>
                        <br>
                        <small class="text-muted">Created: ${new Date(terminal.createdAt).toLocaleString()}</small>
                    </div>
                    <span class="badge ${terminal.isActive ? 'bg-success' : 'bg-secondary'}">
                        ${terminal.isActive ? 'Active' : 'Inactive'}
                    </span>
                </div>
            `).join('')
            : '<p class="text-muted">No active terminals</p>';

        document.getElementById('terminalsList').innerHTML = terminalsHtml;
    }

    updateSystemInfo(data) {
        const uptimeHours = Math.floor(data.uptime / (1000 * 60 * 60));
        const uptimeMinutes = Math.floor((data.uptime % (1000 * 60 * 60)) / (1000 * 60));
        
        const systemHtml = `
            <div class="row">
                <div class="col-6">
                    <strong>Uptime:</strong><br>
                    <span class="text-muted">${uptimeHours}h ${uptimeMinutes}m</span>
                </div>
                <div class="col-6">
                    <strong>Memory:</strong><br>
                    <span class="text-muted">${(data.workingSet / 1024 / 1024).toFixed(1)} MB</span>
                </div>
                <div class="col-6 mt-2">
                    <strong>CPU Cores:</strong><br>
                    <span class="text-muted">${data.processorCount}</span>
                </div>
                <div class="col-6 mt-2">
                    <strong>GC Memory:</strong><br>
                    <span class="text-muted">${(data.gcMemory / 1024 / 1024).toFixed(1)} MB</span>
                </div>
            </div>
        `;
        
        document.getElementById('systemInfo').innerHTML = systemHtml;
    }

    parseNumberArray(str) {
        return str ? str.split(',').map(s => parseInt(s.trim())).filter(n => !isNaN(n)) : [];
    }

    parseStringArray(str) {
        return str ? str.split(',').map(s => s.trim()).filter(s => s.length > 0) : [];
    }

    showAlert(type, message) {
        const alertHtml = `
            <div class="alert alert-${type} alert-dismissible fade show" role="alert">
                ${message}
                <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
            </div>
        `;
        
        // Add to the active tab
        const activeTab = document.querySelector('.tab-pane.active .card-body');
        if (activeTab) {
            const existingAlert = activeTab.querySelector('.alert');
            if (existingAlert) {
                existingAlert.remove();
            }
            activeTab.insertAdjacentHTML('afterbegin', alertHtml);
        }
    }
}

// Initialize when DOM is loaded
document.addEventListener('DOMContentLoaded', () => {
    new ConfigManager();
});