# Platform Operations

This guide covers platform administration: tenant management, deployment, upgrades, monitoring, and troubleshooting.

## Tenant Management

### Creating a Tenant

```
spring-admin tenant create acme --admin user@acme.com
```

This creates the Dapr namespace, state store schema, pub/sub consumer group, and root unit.

### Tenant Administration

| Operation | Command |
|-----------|---------|
| View tenant info | `spring-admin tenant info acme` |
| List all tenants | `spring-admin tenant list` |
| Manage users | `spring-admin tenant users list/add/remove acme` |
| Suspend tenant | `spring-admin tenant suspend acme --reason "..."` |
| View usage | `spring-admin tenant usage acme --period last-30d` |

### Tenant Policies

Set tenant-wide defaults:

```
spring-admin tenant policy set acme initiative.max-level=proactive
spring-admin tenant policy set acme cost.monthly-budget=5000
spring-admin tenant policy set acme execution.max-containers=50
```

### API Key Management

Tenant admins can manage all API keys for their tenant:

```
spring tenant apikeys list
spring tenant apikeys create --name "ci-pipeline"
spring tenant apikeys revoke ci-pipeline
```

Platform admins can manage keys across tenants.

## Deployment

### Local Development

API Host in single-tenant mode + Dapr sidecar + Podman containers. Single machine.

```
dapr run --app-id spring-api --app-port 5000 -- dotnet run --project src/Spring.Host.Api -- --local
```

### Docker Compose (Staging)

API Host + Worker Host behind a reverse proxy. PostgreSQL + Redis:

```
docker compose -f docker-compose.yaml up -d
```

### Kubernetes (Production)

Kubernetes with Dapr operator. Helm chart deployment:

```
helm install spring-voyage spring/spring-voyage \
  --set apiHost.replicas=3 \
  --set workerHost.replicas=5
```

Key production components:
- API Host replicas behind load balancer
- Worker Hosts scaled by workload
- Execution environments as ephemeral pods
- Kafka for pub/sub
- Azure Key Vault for secrets
- PostgreSQL for persistence

## Platform Upgrades

### Pre-Upgrade Check

```
spring-admin platform upgrade --version 2.1.0 --dry-run
```

Reports required changes: database migrations, component config changes, compatibility notes.

### Upgrade Process

```
# 1. Apply database migrations
spring-admin migrate --version 2.1.0

# 2. Rolling update of hosts (zero-downtime)
# Kubernetes:
helm upgrade spring-voyage spring/spring-voyage --version 2.1.0
# Docker Compose:
docker compose pull && docker compose up -d

# 3. Verify
spring-admin platform health
spring-admin platform version
```

### Migration Layers

| Layer | Strategy |
|-------|---------|
| **Database schema** | EF Core migrations. Backwards-compatible within major version. |
| **Actor state** | Versioned serialization with in-place migration chains. Lazy or eager. |
| **Dapr components** | YAML configs versioned alongside the platform. |
| **Agent/Unit definitions** | Schema versioned. Older definitions auto-upgraded on apply. |
| **Workflow containers** | Independent of host. Running instances complete on old container; new instances use new image. |

## Monitoring

### Health Checks

```
spring-admin platform health
```

Checks:
- API Host and Worker Host liveness
- Dapr sidecar connectivity
- PostgreSQL connectivity
- Pub/sub broker connectivity
- Actor runtime health

### Metrics

```
spring-admin platform metrics
```

Key metrics:
- Active actors by type
- Message throughput
- Workflow execution counts
- LLM API latency and error rates
- Container count and resource usage

### Log Aggregation

All hosts and sidecars emit structured logs. In production:
- OpenTelemetry collector for aggregation
- Centralized logging (Loki, Elasticsearch, Azure Monitor)
- Dapr emits distributed traces automatically

### Cost Monitoring

```
spring-admin tenant usage --all --period last-30d
```

Platform-wide cost visibility across all tenants.

## Troubleshooting

### Agent Not Responding

1. Check agent status: `spring agent status <agent>`
2. Check the activity stream: `spring activity stream --agent <agent>`
3. Check for errors: `spring activity history --agent <agent> --type error`
4. Check the Dapr sidecar logs for actor activation issues

### Workflow Stuck

1. Check workflow status: `spring workflow status <id>`
2. Look for pending human-in-the-loop steps
3. Check the workflow container logs
4. Check for dead-lettered pub/sub messages

### Cost Spike

1. Check cost breakdown: `spring cost breakdown --unit <unit> --period today`
2. Look for runaway initiative loops: high Tier 2 call counts
3. Check for retry loops on failing LLM calls
4. Review initiative policies: `spring unit get <unit> --policies`

## Backup and Recovery

### Database

PostgreSQL backups cover all platform data:
- Tenant data, agent definitions, activity history
- Actor runtime state (stored in PostgreSQL via Dapr state store)

Use standard PostgreSQL backup tools: `pg_dump`, continuous archiving, or managed database backups.

### Secret Rotation

Dapr Secrets building block supports rotation. Connectors re-authenticate when secrets change via Dapr Configuration change subscriptions.

## Resource Limits

Per-tenant resource quotas in production:

```
spring-admin tenant quota set acme \
  --max-containers 50 \
  --max-agents 100 \
  --max-storage 10GB
```

Enforced via Kubernetes ResourceQuotas and platform-level policies.
