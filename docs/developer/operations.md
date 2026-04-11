# Platform Operations

This guide covers running and managing a local Spring Voyage deployment.

## Running Locally

### Local Development

API Host in single-tenant mode + Dapr sidecar + Podman containers. Single machine.

```
dapr run --app-id spring-api --app-port 5000 -- dotnet run --project src/Cvoya.Spring.Host.Api -- --local
```

See [setup.md](setup.md) for full setup instructions.

## Health Checks

```
curl http://localhost:5001/health
```

Checks:
- API Host and Worker Host liveness
- Dapr sidecar connectivity
- State store connectivity
- Pub/sub broker connectivity
- Actor runtime health

## Database Migrations

Schema changes use EF Core migrations:

```
# Add a new migration
dotnet ef migrations add <MigrationName> --project src/Cvoya.Spring.Host.Api

# Apply migrations
dotnet ef database update --project src/Cvoya.Spring.Host.Api
```

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

## Backup and Recovery

### Database

PostgreSQL backups cover all platform data:
- Agent definitions, activity history
- Actor runtime state (stored in PostgreSQL via Dapr state store)

Use standard PostgreSQL backup tools: `pg_dump`, continuous archiving, or managed database backups.

### Secret Rotation

Dapr Secrets building block supports rotation. Connectors re-authenticate when secrets change via Dapr Configuration change subscriptions.
