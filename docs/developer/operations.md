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

Schema changes use EF Core migrations. `SpringDbContext` lives in the
`Cvoya.Spring.Dapr` project, and migrations target the Npgsql
(PostgreSQL) provider.

### Auto-migrate on startup (default)

The Worker host runs a hosted service
(`Cvoya.Spring.Dapr.Data.DatabaseMigrator`) on startup that applies any
pending EF Core migrations. This is on by default so fresh
deployments and local dev databases come up with an up-to-date schema
without operator intervention.

**Only the Worker host runs migrations.** The API host calls
`AddCvoyaSpringDapr` (which binds `DatabaseOptions`) but does not
register `DatabaseMigrator`. Earlier versions registered the migrator
in both hosts, and on a fresh database they raced on DDL and one host
crashed with `42P07: relation "..." already exists` (issue #305). The
Worker now owns migrations; the API trusts that the schema is in
place.

You can disable it if you run migrations out-of-band (CI/CD pipeline,
scripted SQL deployment, or manual `dotnet ef database update`) by
setting the following on the Worker host (in `appsettings.json` or an
equivalent environment variable, e.g. `Database__AutoMigrate=false`):

```json
{
  "Database": {
    "AutoMigrate": false
  }
}
```

With the flag disabled, the Worker logs that it is skipping migrations
and assumes the schema is already up to date.

#### Multi-replica deployments

The single-owner pattern above is safe for single-replica Worker
deployments (the OSS Podman / `deploy.sh` topology). If you scale the
Worker beyond one replica, two replicas can still race on `MigrateAsync`
in the same way. Coordinate externally — for example with a Postgres
advisory lock taken before `MigrateAsync`, a Kubernetes init-container
that runs `dotnet ef database update` once before the Worker pods
start, or a leader-election primitive — and leave `AutoMigrate=false`
on the non-leader replicas. A built-in advisory-lock implementation is
deferred until that topology is supported in the OSS deployment
recipes.

#### Hosting a custom migration owner

If you do not deploy the OSS Worker (for example a private cloud host
that bundles API and migrations into one process), call
`builder.Services.AddCvoyaSpringDatabaseMigrator()` exactly once on the
host that should own migrations. Do not call it from more than one
host that targets the same database, and do not call
`AddHostedService<DatabaseMigrator>()` directly — the extension method
is the single registration entry point.

### Adding a new migration

Run from the repository root after making model changes:

```
dotnet tool restore
dotnet ef migrations add <MigrationName> \
  --project src/Cvoya.Spring.Dapr \
  --output-dir Data/Migrations
```

Commit the generated files under `src/Cvoya.Spring.Dapr/Data/Migrations/`.

### Applying migrations externally

```
dotnet ef database update \
  --project src/Cvoya.Spring.Dapr \
  --connection "Host=...;Database=...;Username=...;Password=..."
```

To emit an idempotent SQL script (for a DBA-managed deployment):

```
dotnet ef migrations script \
  --idempotent \
  --project src/Cvoya.Spring.Dapr \
  --output spring-migrations.sql
```

### Design-time connection

`dotnet ef` uses `Cvoya.Spring.Dapr.Data.SpringDbContextDesignTimeFactory`
to build the context at design time. It uses a placeholder connection
string (never opened) to pin the Npgsql provider so generated
migrations target PostgreSQL. Pass `--connection` at `database update`
time to hit a real database.

### Test databases

Unit and integration tests run against the EF Core in-memory provider,
which does not support migrations. Test harnesses continue to rely on
the implicit schema that the in-memory provider materializes from the
model; `DatabaseMigrator` is a no-op against non-relational providers.

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

## DataProtection Keys

ASP.NET Core's DataProtection API encrypts authentication cookies, OAuth
session tokens, anti-forgery tokens, and anything routed through
`IDataProtector.Protect(...)`. Without a stable persistence location the
framework writes keys under `~/.aspnet/DataProtection-Keys` *inside the
container*, regenerates them on every rebuild, and silently invalidates
every payload protected by the previous key ring (issue #337).

### OSS Podman topology

`deploy.sh` mounts the named volume `spring-dataprotection-keys` into
both `spring-api` and `spring-worker` at
`/home/app/.aspnet/DataProtection-Keys`, and sets
`DataProtection__KeysPath` in `spring.env.example` to that same path.
The hosts register `AddCvoyaSpringDataProtection` (in `Program.cs`),
which:

- Sets `SetApplicationName("Cvoya.Spring")` so both hosts — and any
  future replicas — agree on the key-ring identity.
- Calls `PersistKeysToFileSystem(...)` on the configured path.

`./deploy.sh down` preserves the volume. Clearing the key ring (which
invalidates all existing encrypted payloads) requires an explicit
`podman volume rm spring-dataprotection-keys` after stopping the stack.

### Multi-replica deployments

All replicas that talk to the same logical application MUST share one
key ring. Two options:

- **Shared on-disk path.** Back `DataProtection__KeysPath` with a
  shared file system (NFS, Azure Files) and point every replica at it.
  Acceptable for small horizontal fanouts and when the shared file
  system's durability matches the encrypted data's sensitivity.
- **Centralized persister.** Call `AddDataProtection()` in the host
  *before* `AddCvoyaSpringDataProtection`, register your own
  persister (e.g. `PersistKeysToStackExchangeRedis(...)` or the
  EF Core-backed store), and let
  `AddCvoyaSpringDataProtection` short-circuit. This is the
  recommended path for anything beyond a single host.

### Cloud deployments

The private cloud host is expected to chain
`ProtectKeysWithAzureKeyVault(...)` (or a comparable KMS-backed
encryptor) and usually a centralized persister, and to register that
chain via its own `AddDataProtection()` call *before*
`AddCvoyaSpringDataProtection`. The OSS extension detects a
pre-registered `IDataProtectionProvider` and is a no-op in that case,
leaving the cloud configuration intact.

## Backup and Recovery

### Database

PostgreSQL backups cover all platform data:
- Agent definitions, activity history
- Actor runtime state (stored in PostgreSQL via Dapr state store)

Use standard PostgreSQL backup tools: `pg_dump`, continuous archiving, or managed database backups.

### Secret Rotation

Dapr Secrets building block supports rotation. Connectors re-authenticate when secrets change via Dapr Configuration change subscriptions.
