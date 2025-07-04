# Deployment Guide 🚀

This guide covers various deployment scenarios for Claude Code Bot.

## 📋 Table of Contents

- [Quick Start](#quick-start)
- [Docker Deployment](#docker-deployment)
- [Production Deployment](#production-deployment)
- [Cloud Deployment](#cloud-deployment)
- [Environment Variables](#environment-variables)
- [Health Checks](#health-checks)
- [Scaling](#scaling)

## 🚀 Quick Start

### Prerequisites
- Docker 20.10+
- Docker Compose 2.0+
- Telegram Bot Token

### 1-Minute Setup
```bash
git clone <your-repo-url>
cd claude-code-bot
echo "TELEGRAM_BOT_TOKEN=your_token_here" > .env
docker-compose up -d
```

## 🐳 Docker Deployment

### Development
```bash
# Quick development setup
docker-compose -f docker-compose.dev.yml up --build

# With live reload
docker-compose -f docker-compose.dev.yml up --build -d
docker-compose logs -f
```

### Production
```bash
# Production deployment
docker-compose -f docker-compose.prod.yml up -d

# With scaling
docker-compose -f docker-compose.prod.yml up -d --scale claude-terminal=3
```

### Custom Configuration
```bash
# Using custom config file
docker run -d \
  -p 8765:8765 \
  -p 8766:8766 \
  -v /path/to/your/appsettings.json:/app/appsettings.json:ro \
  -v /path/to/logs:/app/logs \
  --name claude-code-bot \
  claude-code-bot:latest
```

## 🏭 Production Deployment

### Docker Swarm
```yaml
# docker-stack.yml
version: '3.8'

services:
  claude-terminal:
    image: claude-code-bot:latest
    ports:
      - "8765:8765"
      - "8766:8766"
    volumes:
      - config:/app/config:ro
      - logs:/app/logs
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    deploy:
      replicas: 3
      restart_policy:
        condition: on-failure
        delay: 5s
        max_attempts: 3
      resources:
        limits:
          memory: 512M
        reservations:
          memory: 256M
    networks:
      - claude-network

volumes:
  config:
    external: true
  logs:
    external: true

networks:
  claude-network:
    external: true
```

Deploy:
```bash
docker stack deploy -c docker-stack.yml claude-bot
```

### Kubernetes
```yaml
# k8s-deployment.yml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: claude-code-bot
  labels:
    app: claude-code-bot
spec:
  replicas: 3
  selector:
    matchLabels:
      app: claude-code-bot
  template:
    metadata:
      labels:
        app: claude-code-bot
    spec:
      containers:
      - name: claude-code-bot
        image: claude-code-bot:latest
        ports:
        - containerPort: 8765
        - containerPort: 8766
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        volumeMounts:
        - name: config
          mountPath: /app/appsettings.json
          subPath: appsettings.json
          readOnly: true
        - name: logs
          mountPath: /app/logs
        resources:
          requests:
            memory: "256Mi"
            cpu: "250m"
          limits:
            memory: "512Mi"
            cpu: "500m"
        livenessProbe:
          httpGet:
            path: /health
            port: 8765
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /ready
            port: 8765
          initialDelaySeconds: 5
          periodSeconds: 5
      volumes:
      - name: config
        configMap:
          name: claude-config
      - name: logs
        persistentVolumeClaim:
          claimName: claude-logs-pvc
---
apiVersion: v1
kind: Service
metadata:
  name: claude-code-bot-service
spec:
  selector:
    app: claude-code-bot
  ports:
  - name: hooks
    port: 8765
    targetPort: 8765
  - name: websocket
    port: 8766
    targetPort: 8766
  type: LoadBalancer
```

Deploy:
```bash
kubectl apply -f k8s-deployment.yml
```

## ☁️ Cloud Deployment

### AWS ECS
```json
{
  "family": "claude-code-bot",
  "taskRoleArn": "arn:aws:iam::account:role/ecsTaskRole",
  "executionRoleArn": "arn:aws:iam::account:role/ecsTaskExecutionRole",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "256",
  "memory": "512",
  "containerDefinitions": [
    {
      "name": "claude-code-bot",
      "image": "your-registry/claude-code-bot:latest",
      "portMappings": [
        {
          "containerPort": 8765,
          "protocol": "tcp"
        },
        {
          "containerPort": 8766,
          "protocol": "tcp"
        }
      ],
      "environment": [
        {
          "name": "ASPNETCORE_ENVIRONMENT",
          "value": "Production"
        }
      ],
      "secrets": [
        {
          "name": "TELEGRAM_BOT_TOKEN",
          "valueFrom": "arn:aws:secretsmanager:region:account:secret:telegram-bot-token"
        }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/claude-code-bot",
          "awslogs-region": "us-east-1",
          "awslogs-stream-prefix": "ecs"
        }
      }
    }
  ]
}
```

### Azure Container Instances
```bash
az container create \
  --resource-group myResourceGroup \
  --name claude-code-bot \
  --image claude-code-bot:latest \
  --ports 8765 8766 \
  --environment-variables ASPNETCORE_ENVIRONMENT=Production \
  --secure-environment-variables TELEGRAM_BOT_TOKEN=$TELEGRAM_BOT_TOKEN \
  --cpu 1 \
  --memory 2
```

### Google Cloud Run
```yaml
# cloudrun.yml
apiVersion: serving.knative.dev/v1
kind: Service
metadata:
  name: claude-code-bot
  annotations:
    run.googleapis.com/ingress: all
spec:
  template:
    metadata:
      annotations:
        autoscaling.knative.dev/maxScale: "100"
        run.googleapis.com/cpu-throttling: "false"
    spec:
      containerConcurrency: 80
      containers:
      - image: gcr.io/project-id/claude-code-bot:latest
        ports:
        - containerPort: 8765
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: Production
        - name: TELEGRAM_BOT_TOKEN
          valueFrom:
            secretKeyRef:
              name: telegram-bot-token
              key: token
        resources:
          limits:
            cpu: "1"
            memory: "512Mi"
```

Deploy:
```bash
gcloud run services replace cloudrun.yml --region=us-central1
```

## 🌍 Environment Variables

### Required
```bash
TELEGRAM_BOT_TOKEN=your_telegram_bot_token
```

### Optional
```bash
# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8765;http://+:8766

# Discord (if enabled)
DISCORD_BOT_TOKEN=your_discord_token
DISCORD_GUILD_IDS=123456789,987654321

# Logging
LOG_LEVEL=Information
LOG_FILE_PATH=/app/logs/app.log

# Performance
MAX_TERMINALS=10
TERMINAL_TIMEOUT=1800
```

### .env File
```bash
# .env
TELEGRAM_BOT_TOKEN=8092810636:AAFqr7klK41RGUC1xOCCsXr9k4NFyAij6RY
DISCORD_BOT_TOKEN=your_discord_token_here
ASPNETCORE_ENVIRONMENT=Production
LOG_LEVEL=Information
MAX_TERMINALS=5
```

## 🏥 Health Checks

### Docker Health Check
```dockerfile
# Add to Dockerfile
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8765/health || exit 1
```

### Docker Compose Health Check
```yaml
# docker-compose.yml
services:
  claude-terminal:
    # ... other config
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8765/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
```

### Manual Health Check
```bash
# Check if service is responding
curl -f http://localhost:8765/health

# Check logs
docker logs claude-code-bot --tail 50

# Check container status
docker ps | grep claude-code-bot
```

## 📈 Scaling

### Horizontal Scaling (Multiple Instances)
```bash
# Docker Compose scaling
docker-compose up -d --scale claude-terminal=3

# Docker Swarm scaling
docker service scale claude-bot_claude-terminal=5

# Kubernetes scaling
kubectl scale deployment claude-code-bot --replicas=3
```

### Load Balancer Configuration
```nginx
# nginx.conf
upstream claude_backend {
    server 127.0.0.1:8765;
    server 127.0.0.1:8767;
    server 127.0.0.1:8768;
}

server {
    listen 80;
    server_name your-domain.com;

    location / {
        proxy_pass http://claude_backend;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /ws {
        proxy_pass http://claude_backend;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
    }
}
```

## 🔄 Backup and Recovery

### Configuration Backup
```bash
# Backup configuration
docker cp claude-code-bot:/app/appsettings.json ./backup/appsettings-$(date +%Y%m%d).json

# Backup logs
docker cp claude-code-bot:/app/logs ./backup/logs-$(date +%Y%m%d)
```

### Automated Backup Script
```bash
#!/bin/bash
# backup.sh

BACKUP_DIR="/backup/claude-bot"
DATE=$(date +%Y%m%d_%H%M%S)
CONTAINER="claude-code-bot"

mkdir -p $BACKUP_DIR

# Backup configuration
docker cp $CONTAINER:/app/appsettings.json $BACKUP_DIR/appsettings_$DATE.json

# Backup logs
docker cp $CONTAINER:/app/logs $BACKUP_DIR/logs_$DATE

# Clean old backups (keep last 7 days)
find $BACKUP_DIR -type f -mtime +7 -delete

echo "Backup completed: $BACKUP_DIR"
```

## 🛡️ Security Best Practices

### Container Security
```bash
# Run as non-root user
docker run --user 1001:1001 claude-code-bot

# Read-only filesystem
docker run --read-only claude-code-bot

# Limit resources
docker run --memory=512m --cpus=0.5 claude-code-bot

# Network isolation
docker network create --driver bridge claude-network
docker run --network claude-network claude-code-bot
```

### Secrets Management
```bash
# Docker Secrets (Swarm)
echo "your_token" | docker secret create telegram_token -
docker service create --secret telegram_token claude-code-bot

# Kubernetes Secrets
kubectl create secret generic claude-secrets \
  --from-literal=telegram-token=your_token

# HashiCorp Vault integration
export VAULT_TOKEN=$(vault write -field=token auth/jwt/login role=claude-bot jwt=$CI_JOB_JWT)
export TELEGRAM_TOKEN=$(vault kv get -field=token secret/claude-bot/telegram)
```

## 📊 Monitoring

### Prometheus Metrics
```yaml
# docker-compose.monitoring.yml
version: '3.8'

services:
  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_PASSWORD=admin
```

### Log Aggregation
```yaml
# docker-compose.logging.yml
version: '3.8'

services:
  elasticsearch:
    image: docker.elastic.co/elasticsearch/elasticsearch:7.15.0
    environment:
      - discovery.type=single-node

  logstash:
    image: docker.elastic.co/logstash/logstash:7.15.0
    volumes:
      - ./logstash.conf:/usr/share/logstash/pipeline/logstash.conf

  kibana:
    image: docker.elastic.co/kibana/kibana:7.15.0
    ports:
      - "5601:5601"
```

---

**🎯 Next Steps**: Check the [Configuration Guide](CONFIGURATION.md) for detailed setup options.