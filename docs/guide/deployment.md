# Deployment

This guide walks an operator from zero to a working single-host Spring Voyage deployment using Docker Compose or Podman. Kubernetes and multi-region deployments are covered separately in the Spring Voyage Cloud repository — this guide targets the open-source single-host scenario (your workstation, a home server, or one VPS).

## Document map

- [Zero-to-running walkthrough](#zero-to-running-walkthrough) — the ten-minute path.
- [Container stack](#container-stack) — what runs, why, and on which ports.
- [Docker Compose](#docker-compose) — the `deployment/docker-compose.yml` reference.
- [Podman (rootless)](#podman-rootless) — the `deployment/deploy.sh` reference.
- [Dapr components](#dapr-components) — the state store / pub/sub / secret store YAML.
- [PostgreSQL setup](#postgresql-setup) — connection string, database, migrations.
- [Redis setup](#redis-setup) — pub/sub + distributed state.
- [TLS with Caddy](#tls-with-caddy) — automatic Let's Encrypt certificates.
- [Secrets bootstrap](#secrets-bootstrap) — API keys, GitHub App, OAuth.
- [Health checks](#health-checks) — verifying the stack is live.
- [Updating](#updating-to-a-new-version) — rolling to a new image tag.
- [Troubleshooting](#troubleshooting) — common failures and fixes.

For the architectural picture of how these pieces fit together, read [Architecture — Deployment](../architecture/deployment.md) and [Architecture — Infrastructure](../architecture/infrastructure.md) first. Operator tasks that sit above provisioning (backups, DataProtection keys, migrations) live in [Developer — Operations](../developer/operations.md).

## Prerequisites

- **Host:** Linux (any distro with kernel 5.10+), macOS, or Windows via WSL2. 4 GB RAM minimum, 8 GB recommended, 20 GB disk.
- **Container runtime:** either
  - Docker Engine 24+ with the Compose plugin, or
  - Podman 4.4+ (rootless-capable).
- **Ports:** 80 and 443 free on the host (Caddy binds them for TLS). Nothing else needs to be exposed.
- **A DNS name** pointing at the host — required only if you want Let's Encrypt TLS. Internal / `*.localhost` use is fine without DNS.
- **Git** — to check out the repository and pin the desired tag.

No Dapr CLI is required on the host. The stack bundles its own Dapr control plane (placement + scheduler).

## Zero-to-running walkthrough

This gets you from a clean host to a working stack in under ten minutes on a reasonable connection. Substitute `docker compose` for `podman compose` if you prefer Podman, or use `./deploy.sh` (Podman-native, see [below](#podman-rootless)).

```bash
# 1. Clone the repository and check out a stable tag.
git clone https://github.com/savasp/spring-voyage.git
cd spring-voyage
git checkout v0.1.0   # or `main` while tracking head

# 2. Seed the environment file from the documented template.
cd deployment
cp spring.env.example spring.env

# 3. Edit secrets. At minimum change POSTGRES_PASSWORD and — if you expose
#    the stack publicly — REDIS_PASSWORD, DEPLOY_HOSTNAME, and ACME_EMAIL.
$EDITOR spring.env

# 4. Build the platform image (one image serves api, worker, and web).
docker compose --env-file spring.env build

# 5. Start the stack.
docker compose --env-file spring.env up -d

# 6. Verify.
docker compose --env-file spring.env ps
curl -fsS http://localhost/health
```

`http://<DEPLOY_HOSTNAME>/` serves the Next.js web portal; `http://<DEPLOY_HOSTNAME>/api/` is the REST API. If you set a public FQDN with DNS pointing at the host, Caddy will issue a Let's Encrypt certificate automatically on the first request — at that point, switch to `https://`.

## Container stack

The same stack runs under both Docker Compose and Podman. Every container attaches to a single bridge network called `spring-net`.

| Container            | Image                                 | Role                                                    |
| -------------------- | ------------------------------------- | ------------------------------------------------------- |
| `spring-postgres`    | `postgres:17`                         | Primary database + Dapr state store backend.            |
| `spring-redis`       | `redis:7`                             | Dapr pub/sub backend.                                   |
| `spring-placement`   | `daprio/dapr:<tag>`                   | Dapr actor placement service.                           |
| `spring-scheduler`   | `daprio/dapr:<tag>`                   | Dapr actor reminder / scheduler service.                |
| `spring-api-dapr`    | `daprio/dapr:<tag>`                   | daprd sidecar paired with `spring-api`.                 |
| `spring-worker-dapr` | `daprio/dapr:<tag>`                   | daprd sidecar paired with `spring-worker`.              |
| `spring-worker`      | `localhost/spring-voyage:<tag>`       | Dapr actor host (agents, units, connectors). Runs EF migrations. |
| `spring-api`         | `localhost/spring-voyage:<tag>`       | ASP.NET Core REST API (port 8080 inside the network).   |
| `spring-web`         | `localhost/spring-voyage:<tag>`       | Next.js dashboard (port 3000 inside the network).       |
| `spring-caddy`       | `caddy:2`                             | Reverse proxy + automatic TLS (binds host `:80`, `:443`). |

Three image roles, one built image: `deployment/Dockerfile` produces a single `localhost/spring-voyage:<tag>` image that contains the published API, Worker, and Web outputs side-by-side. The container's `command` selects which process to run.

**Sidecar topology.** Each .NET host talks to its own daprd container — not a localhost sidecar. The Dapr .NET SDK honors `DAPR_HTTP_ENDPOINT` / `DAPR_GRPC_ENDPOINT`, which the stack sets per app:

```
spring-api ─ http://spring-api-dapr:3500 ─▶ spring-api-dapr
                                                 │
           ┌──────── spring-placement:50005 ─────┤
           │                                     │
           ▼                                     ▼
 spring-worker-dapr ◀─ http://spring-worker-dapr:3500 ─ spring-worker
```

See [Architecture — Deployment](../architecture/deployment.md) for why the sidecars are container-paired rather than process-paired.

## Docker Compose

The reference compose file is at `deployment/docker-compose.yml`. It is a working, minimal example — the same services, volumes, and network the Podman script manages. Run it from the `deployment/` directory so relative `../dapr/` bind mounts resolve:

```bash
cd deployment/
cp spring.env.example spring.env
$EDITOR spring.env

docker compose --env-file spring.env build    # build the platform image from source
docker compose --env-file spring.env up -d    # start the stack
docker compose --env-file spring.env ps       # status
docker compose --env-file spring.env logs -f spring-api
docker compose --env-file spring.env down     # stop (volumes preserved)
```

Volumes (`spring-postgres-data`, `spring-redis-data`, `spring-caddy-data`, `spring-caddy-config`, `spring-dataprotection-keys`, etc.) persist across `down`/`up` cycles. Remove them with `docker volume rm` when you want a clean slate.

**Image registry flow.** If you publish the platform image to a registry, set `SPRING_PLATFORM_IMAGE` in `spring.env` to the registry path and skip the `build` step — `up -d` will pull on demand.

## Podman (rootless)

`deployment/deploy.sh` is the Podman-native driver. It issues `podman` calls directly (no compose shim) so behaviour is deterministic across Podman versions, and it exposes Podman-specific operations like `ensure-user-net` for per-user agent isolation.

```bash
cd deployment/
cp spring.env.example spring.env
$EDITOR spring.env

./deploy.sh build              # build platform + agent images
./deploy.sh up                 # create network, start the full stack
./deploy.sh status             # list running containers
./deploy.sh logs spring-api    # tail one service
./deploy.sh down               # stop containers (volumes preserved)
./deploy.sh restart            # down + up
```

Rootless notes:

- Podman 4.4+ is required (earlier releases miss `podman network exists` and leak networking state).
- Ports 80 and 443 need either `CAP_NET_BIND_SERVICE` granted to the Podman user, or a line in `/etc/sysctl.d/` lowering `net.ipv4.ip_unprivileged_port_start`.
- The default `host.containers.internal` DNS name that delegated agents rely on works on Linux with Podman 4.1+; older versions require an explicit `--add-host` which the runtime adds automatically.

See `deployment/README.md` for the full Podman story (remote deploy via `deploy-remote.sh`, per-user agent networks, webhook relay for local-dev).

## Dapr components

Components and the Dapr Configuration live under `dapr/` at the repo root. Two profiles ship in-tree:

```
dapr/
├── components/
│   ├── local/         # dev loop (dapr run; env-var secret store)
│   │   ├── statestore.yaml     # state.redis on localhost:6379
│   │   ├── pubsub.yaml         # pubsub.redis on localhost:6379
│   │   └── secretstore.yaml    # secretstores.local.env
│   └── production/    # Docker Compose / Podman stack
│       ├── statestore.yaml     # state.postgresql via spring-postgres
│       ├── pubsub.yaml         # pubsub.redis via spring-redis
│       └── secretstore.yaml    # secretstores.local.env
└── config/
    ├── local.yaml              # tracing stdout, resiliency on
    └── production.yaml         # tracing 10% sampling, resiliency on
```

Both stacks bind-mount `dapr/components/production/` at `/components` inside each sidecar and `dapr/config/production.yaml` at `/config/config.yaml`. That means **you can edit a component YAML and restart the sidecar to apply the change** — you do not need to rebuild the image.

### State store (`statestore`)

`dapr/components/production/statestore.yaml`:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
auth:
  secretStore: secretstore
metadata:
  name: statestore
spec:
  type: state.postgresql
  version: v1
  metadata:
    - name: connectionString
      secretKeyRef:
        name: SPRING_POSTGRES_CONNECTION_STRING
        key: SPRING_POSTGRES_CONNECTION_STRING
    - name: actorStateStore
      value: "true"
```

The Dapr actor runtime (the backbone of every `AgentActor`, `UnitActor`, `ConnectorActor`) reads and writes actor state through this component. The connection string is pulled from the paired `secretstore` component rather than being inlined — which keeps the Postgres password out of git and out of the image.

### Pub/sub (`pubsub`)

`dapr/components/production/pubsub.yaml`:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
auth:
  secretStore: secretstore
metadata:
  name: pubsub
spec:
  type: pubsub.redis
  version: v1
  metadata:
    - name: redisHost
      value: "spring-redis:6379"
    - name: redisPassword
      secretKeyRef:
        name: REDIS_PASSWORD
        key: REDIS_PASSWORD
```

Redis Streams is the default pub/sub backend — it is cheap, single-node-friendly, and survives restarts. For multi-broker deployments (NATS, RabbitMQ, Kafka, cloud services) swap this file for the Dapr component you want. The platform keys off the component **name** (`pubsub`), not the implementation, so no code changes are required.

### Secret store (`secretstore`)

`dapr/components/production/secretstore.yaml`:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: secretstore
spec:
  type: secretstores.local.env
  version: v1
```

`secretstores.local.env` reads secrets from the sidecar process environment — the stack passes `spring.env` to every sidecar via `--env-file`, so any `secretKeyRef` resolves against the variables defined there. For cloud-grade secret management replace this file with the Dapr Azure Key Vault, HashiCorp Vault, or Kubernetes Secrets component. Keep the component name `secretstore` and the other components keep working unchanged.

## PostgreSQL setup

### Defaults

The default stack runs PostgreSQL 17 in a container (`spring-postgres`) with a named volume for data (`spring-postgres-data`). The postgres image's entrypoint creates the user, password, and database on first start from the environment variables `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` — all driven by `spring.env`.

### Connection strings

Two connection strings reach Postgres, and both are defined in `spring.env.example`:

| Variable                            | Consumer                                     | Format                                          |
| ----------------------------------- | -------------------------------------------- | ----------------------------------------------- |
| `ConnectionStrings__SpringDb`       | Platform hosts (EF Core, via `IConfiguration.GetConnectionString("SpringDb")`). | Npgsql (`Host=...;Port=...;Database=...;Username=...;Password=...`). |
| `SPRING_POSTGRES_CONNECTION_STRING` | Dapr state store component (`state.postgresql`). | libpq-style (`host=... port=... user=... password=... dbname=... sslmode=...`). |

A missing or empty `ConnectionStrings__SpringDb` is a hard configuration error — the host refuses to start so a misconfigured deployment cannot silently fall back to an in-memory store. Keep both variables in sync with the Postgres credentials you set; `spring.env.example` wires them up with `envsubst` so you only edit `POSTGRES_USER`, `POSTGRES_PASSWORD`, and `POSTGRES_DB` once.

### Migrations

EF Core migrations target the Npgsql provider and live under `src/Cvoya.Spring.Dapr/Data/Migrations/`. The **Worker host owns migrations** and runs them automatically on startup via `DatabaseMigrator` (a hosted service). The API host does not run migrations — it trusts the schema is in place. This is why the compose file declares `spring-api` as `depends_on: spring-worker`.

Disable auto-migrate if you run migrations out-of-band (CI/CD or a scripted SQL deploy):

```ini
# spring.env
Database__AutoMigrate=false
```

See [Developer — Operations § Database Migrations](../developer/operations.md#database-migrations) for the manual path (`dotnet ef database update`, idempotent SQL scripts, multi-replica coordination).

### External PostgreSQL

To point at an externally managed Postgres (RDS, Cloud SQL, a dedicated VM), remove the `spring-postgres` service from your compose file and update both connection strings in `spring.env`. Make sure the host resolves from inside `spring-net` (add an `extra_hosts:` entry or use a public DNS name) and that `sslmode=require` is set on a non-local database.

## Redis setup

### Defaults

Redis 7 runs as `spring-redis` with `appendonly yes` and a named volume (`spring-redis-data`) for AOF persistence. When `REDIS_PASSWORD` is set the container starts with `--requirepass`; when empty it runs without auth (acceptable for a laptop, not for a public VPS).

### Roles

Redis carries two Dapr building blocks in this stack:

- **Pub/sub** — Redis Streams topic per channel. At-least-once delivery; survives restarts while the AOF is intact.
- **Distributed state (optional).** The default `statestore` uses PostgreSQL, but you can swap it for `state.redis` by editing `dapr/components/production/statestore.yaml`. Redis is faster but lacks ACID semantics — the trade-off is appropriate for short-lived agent state that does not need cross-table durability.

### External Redis

Point `redisHost` in `dapr/components/production/pubsub.yaml` at your managed instance (`redis.example.com:6380`), set `REDIS_PASSWORD` in `spring.env`, and remove the `spring-redis` service from the compose file. Enable TLS in the Dapr component metadata (`enableTLS: "true"`) for a public-facing Redis.

## TLS with Caddy

Caddy is the stack's reverse proxy and TLS terminator. It fronts three upstreams:

- `spring-api:8080` (REST API, OpenAPI docs, `/health`)
- `spring-api:8080` via `/api/v1/webhooks/*` (third-party webhook ingress)
- `spring-web:3000` (Next.js dashboard — everything else)

Two Caddyfile variants ship in `deployment/`:

- **`Caddyfile`** — single public hostname, path-routed. The default.
- **`Caddyfile.multi-host`** — one FQDN per service (`app.example.com`, `api.example.com`, `hooks.example.com`). Select by setting `SPRING_CADDYFILE=Caddyfile.multi-host` in `spring.env`.

### Automatic Let's Encrypt

Caddy obtains a Let's Encrypt certificate for any FQDN it serves when three conditions hold:

1. The hostname's public DNS `A`/`AAAA` record points at this host.
2. Ports `80` and `443` on the host are reachable from the public internet. The ACME HTTP-01 challenge requires port 80 specifically.
3. `ACME_EMAIL` is set in `spring.env` so Let's Encrypt can email expiry and revocation notices.

Set `DEPLOY_HOSTNAME=app.example.com` and `DEPLOY_SCHEME=https` in `spring.env`, point DNS at the host, and `docker compose up -d` — a certificate lands automatically on the first HTTPS request.

### Local / private deployments

Hostnames ending in `.localhost`, set to `localhost`, or private LAN names like `*.local` fall back to plain HTTP. This is the right default for a laptop stack. Set `DEPLOY_SCHEME=http` explicitly to be safe.

### Using nginx instead

If you already run nginx for other services, terminate TLS there and proxy to the compose stack. Point the nginx upstream at the host ports that Caddy binds (`:80`/`:443`) or remove `spring-caddy` entirely and proxy directly to `spring-api:8080` and `spring-web:3000` (expose them via `ports:` in your compose override). A minimal upstream block:

```nginx
upstream spring_api { server 127.0.0.1:8080; }
upstream spring_web { server 127.0.0.1:3000; }

server {
    listen 443 ssl http2;
    server_name app.example.com;

    location /api/  { proxy_pass http://spring_api; }
    location /health { proxy_pass http://spring_api; }
    location /      { proxy_pass http://spring_web; }
}
```

You lose automatic certificate issuance — arrange your own certbot / cert-manager flow.

## Secrets bootstrap

All secrets live in `deployment/spring.env`. The file is **not** committed — only `spring.env.example` is — and `deploy.sh` / the compose file load it at container start via `--env-file`. Restrict its permissions on the host:

```bash
chmod 600 /opt/spring-voyage/deployment/spring.env
```

### Mandatory

| Variable                            | Purpose                                          |
| ----------------------------------- | ------------------------------------------------ |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Create the initial Postgres user, password, and database. |
| `ConnectionStrings__SpringDb`       | Npgsql connection string the .NET hosts use. The template in `spring.env.example` interpolates the three variables above. |
| `SPRING_POSTGRES_CONNECTION_STRING` | libpq-style connection string the Dapr state-store component uses. |
| `REDIS_PASSWORD`                    | Redis `requirepass`. Leave empty only on a laptop. |
| `DEPLOY_HOSTNAME`                   | Public FQDN (or `localhost` for a local stack). |

### Tier-1 platform credentials — GitHub App identity (env only)

Uncomment in `spring.env` when the deployment acts as a GitHub App (tier-1
platform-deploy config: these identify the Spring Voyage instance itself,
not a workload):

```ini
# GitHub App — consumed by the GitHub connector.
# Numeric / single-token values: UNQUOTED. PEM: SINGLE-QUOTED, single-line, `\n` between blocks.
GitHub__AppId=123456
GitHub__AppSlug=<slug from https://github.com/apps/<slug>>
GitHub__PrivateKeyPem='-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----'
GitHub__WebhookSecret=<shared secret you configured on the GitHub App>
```

The GitHub variables follow the .NET `Section__Key` convention and bind to the `GitHub:*` configuration section at startup. The short-form aliases `GITHUB_APP_ID` / `GITHUB_APP_PRIVATE_KEY` / `GITHUB_WEBHOOK_SECRET` are NOT consumed — only the `GitHub__*` form is read by the binder.

> **env-file quirks (#1186).** `spring.env` is read by three layers and each treats values differently:
>
> 1. `deploy.sh` sources it with `set -a; source spring.env` so bash can expand `${VAR}` references between keys (e.g. inside `ConnectionStrings__SpringDb`). Bash splits unquoted values on whitespace, so any value containing spaces or shell metacharacters — notably the PEM `-----BEGIN RSA PRIVATE KEY-----` line — must be **single-quoted**. Use single quotes (literal in bash) rather than double quotes to avoid `${...}` expansion of fragments that look like variables.
> 2. `envsubst` expands `${VAR}` references in the file content; everything else passes through verbatim, so the surrounding quotes survive into the resolved file.
> 3. Podman / Docker `--env-file` reads `KEY=VALUE` literally — surrounding quotes become part of the value, and multi-line values are not supported.
>
> The connector strips one matching pair of surrounding quotes from `GitHub__PrivateKeyPem` and decodes literal `\n` -> real newline before `RSA.ImportFromPem`, so the single-quoted single-line PEM round-trips through bash + envsubst + podman without breaking parsing. The same trick can NOT rescue a quoted numeric `GitHub__AppId`: `long` binding happens before the connector sees the value, so a quoted numeric id silently binds as `0` and the connector reports `Disabled` with "GitHub App not configured." **Always leave `GitHub__AppId` unquoted.**

> **GitHub App private key — PEM contents, not a path.** `GitHub__PrivateKeyPem` is the **contents** of the `.pem` file: either inlined verbatim, inlined as a single line with `\n` separators, or an absolute container-visible path whose file contents are valid PEM. `~` is **not** expanded by `--env-file`, so a value like `~/secrets/key.pem` reaches the container as the literal string `~/secrets/key.pem` — mount the file at a known absolute path if you want to reference it by path. Passing a path that does not resolve to a valid PEM fails the host at startup with a targeted error rather than waiting to return a 502 from the first `list-installations` call. See [Architecture — Connectors § disabled-with-reason](../architecture/connectors.md#disabled-with-reason-pattern) for the validation model. If either variable is missing, the GitHub connector boots in a disabled state and `GET /api/v1/connectors/github/actions/list-installations` returns a structured `404` the portal and CLI render as "GitHub App not configured" instead of attempting the JWT sign.

### Tier-2 tenant-default credentials — LLM provider keys (post-deploy)

**LLM API keys do NOT belong in `spring.env`.** They are tier-2
tenant-default credentials (issue #615) stored in the secret registry so
they can be rotated without a restart, scoped per-unit, and audited.
There is no env-variable fallback — if no tenant or unit secret is
configured the platform surfaces a fail-clean "no LLM credentials
configured" error with a remediation hint.

Set them after `docker compose up -d`:

```bash
# CLI
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."
spring secret create --scope tenant openai-api-key    --value "sk-..."
spring secret create --scope tenant google-api-key    --value "AIza..."

# or the portal: open Settings → "Tenant defaults" panel → paste value → Set
```

Units inherit these automatically; override per-unit with a same-name
secret at unit scope. The platform does not read LLM provider API keys
from environment variables — credentials must be set at tenant or unit
scope, or the feature fails cleanly. See [Managing Secrets](secrets.md)
for the full two-tier resolution chain.

### GitHub App — webhook delivery

Webhook providers (including GitHub) post to `/api/v1/webhooks/<provider>` on your public FQDN. Confirm:

- `WEBHOOK_HOSTNAME` (if using the multi-host Caddyfile) or `DEPLOY_HOSTNAME` resolves publicly.
- Port 443 is reachable from the internet.
- The GitHub App's webhook URL is `https://<host>/api/v1/webhooks/github` and `GitHub__WebhookSecret` matches both ends.

For local development against a laptop, use `deployment/relay.sh` to open an SSH reverse tunnel from a small relay VPS — see `deployment/README.md#local-dev-webhook-tunnel-relaysh`.

### Cloud-grade secret stores

For Azure Key Vault, HashiCorp Vault, AWS Secrets Manager, or Kubernetes Secrets, replace `dapr/components/production/secretstore.yaml` with the corresponding Dapr component. Leave the component name `secretstore` — the other components reference the store by name so they continue to work unchanged. See [Developer — Secret store](../developer/secret-store.md) for per-agent / per-unit secret scoping details.

## Health checks

The API and Worker hosts each expose a single `/health` liveness endpoint. They return `200 OK` with a JSON body `{"Status":"Healthy"}` once the host has bound its HTTP listener. There is no separate `/ready` endpoint today — readiness is signalled by the Dapr sidecar's `/v1.0/healthz` probe, which confirms components loaded and the control plane is reachable.

### Checking the stack

```bash
# API host (behind Caddy)
curl -fsS http://localhost/health

# Directly (inside the network / with ports exposed)
docker exec spring-api curl -fsS http://localhost:8080/health

# Dapr sidecar readiness
docker exec spring-api-dapr wget -q -O- http://localhost:3500/v1.0/healthz
```

### What each signal means

- `spring-api` `/health` — the API host is accepting HTTP traffic. Does not imply the database, Dapr sidecar, or any downstream is reachable.
- `spring-worker` `/health` — the Worker host is up. Migrations completed (`DatabaseMigrator` ran to completion before the listener bound).
- Dapr sidecar `/v1.0/healthz/outbound` — the sidecar loaded its component YAML and can reach its control plane. If this fails, the app will still start but `Actor` / `pubsub` / `state` calls error.
- Container-level healthchecks — `spring-postgres` runs `pg_isready` and `spring-redis` runs `redis-cli ping`. `docker compose ps` shows `(healthy)` once they pass.

### Deeper probes

Run a few CLI calls against the API to confirm actors and state persist end-to-end:

```bash
spring auth                         # only for hosted/remote deployments
spring unit create deployment-smoke
spring unit list                    # must list the unit
spring unit delete deployment-smoke
```

## Updating to a new version

Spring Voyage is currently pre-1.0, so treat every update as a potentially-breaking change: read the release notes, run the update in a staging environment first, and take a database backup before rolling production.

### Pull the new image

**Registry flow:**

```bash
cd deployment/
sed -i 's/^SPRING_IMAGE_TAG=.*/SPRING_IMAGE_TAG=0.2.0/' spring.env
docker compose --env-file spring.env pull
docker compose --env-file spring.env up -d
```

`up -d` recreates changed services and leaves unchanged services alone. Migrations run automatically when `spring-worker` restarts (before `spring-api` comes back up).

**Source flow:**

```bash
cd /path/to/spring-voyage
git fetch --tags
git checkout v0.2.0

cd deployment/
docker compose --env-file spring.env build
docker compose --env-file spring.env up -d
```

### Before / after checklist

- **Before:** `pg_dump` the database (`docker exec spring-postgres pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB" > backup.sql`). Back up `spring-dataprotection-keys` as well — it carries the key ring that decrypts auth cookies and OAuth tokens.
- **After:** confirm `/health` on the API, tail `spring-worker` logs for migration lines, and run the smoke test in [Deeper probes](#deeper-probes). Roll back by checking out the previous tag and running `up -d` again.

**Never delete `spring-dataprotection-keys`** as part of an update. It is preserved across `down`/`up` by default; an explicit `docker volume rm spring-dataprotection-keys` is the only thing that clears it (which invalidates every existing auth cookie, OAuth session token, and anti-forgery token). See [Developer — Operations § DataProtection](../developer/operations.md#dataprotection-keys).

## Troubleshooting

### `spring-api` exits immediately with `No connection string found for SpringDbContext`

`ConnectionStrings__SpringDb` is missing or empty in `spring.env`. The host refuses to start rather than silently fall back to an in-memory store. Restore the line and re-deploy.

### `spring-worker` logs `42P07: relation "..." already exists`

Two instances are trying to run EF migrations against the same database. The OSS topology runs migrations only on `spring-worker`; confirm `Database__AutoMigrate=false` is set anywhere else and that you only ever run one Worker replica against a given database. Details in [Developer — Operations § Multi-replica deployments](../developer/operations.md#multi-replica-deployments).

### Dapr sidecar crashes with `components path not found`

The compose file bind-mounts `../dapr/components/production` relative to `deployment/`. Make sure you invoke `docker compose` from inside `deployment/` so that relative path resolves. If you move the compose file, update the bind-mount source.

### `dapr_placement` and `dapr_scheduler` from `dapr init` are interfering

`dapr init` on the host creates control-plane containers on Podman's default network. They are invisible to `spring-net` but can steal ports if they try to bind externally. The deploy script runs its own placement/scheduler on `spring-net` instead — you can stop the `dapr init` containers with `dapr uninstall` and nothing in this stack is affected.

### Webhook deliveries 404

Verify two things:

- Public DNS for `WEBHOOK_HOSTNAME` (or `DEPLOY_HOSTNAME`) points at the host.
- The third-party URL is `https://<host>/api/v1/webhooks/<provider>` — not `/api/webhooks/...` (no `v1`) and not `/webhooks/...`.

Tail `spring-caddy` logs to see whether Caddy is receiving the request and forwarding it to `spring-api:8080`.

### Let's Encrypt issuance fails

The ACME HTTP-01 challenge requires inbound connections to port 80 from Let's Encrypt's servers. Check:

- Firewall rules allow inbound `:80` on the host.
- No upstream proxy (cloud load balancer, Cloudflare in "proxied" mode) is terminating `:80` itself — either turn it off during issuance or switch Caddy to the DNS-01 challenge.
- `ACME_EMAIL` is set.
- `DEPLOY_HOSTNAME` is a real FQDN, not `localhost`.

Caddy logs the ACME failure with a detailed reason — `docker compose logs spring-caddy` is the first place to look.

### Postgres says `authentication failed`

`ConnectionStrings__SpringDb` and `SPRING_POSTGRES_CONNECTION_STRING` diverged from `POSTGRES_PASSWORD`. The stack pre-processes `spring.env` with `envsubst` so inter-variable references resolve — but only if you use `${VAR}` syntax (bare `$VAR` does not expand). Check the file.

### The first `up -d` is stuck on `waiting for spring-postgres`

The Postgres health check polls `pg_isready` — a fresh database takes 10–30 seconds to initialise on slow disks. Give it a minute. If it never becomes healthy, check `docker logs spring-postgres` for volume permission issues (the most common cause on first start with a pre-existing data volume).

### Agents fail to reach the MCP server on `host.docker.internal`

Delegated agents run inside containers and need to reach the platform's MCP server on the host. This requires:

- Linux: Podman 4.1+ (or Docker 20.10+) with automatic `host.docker.internal` → host-gateway mapping. Older versions need an explicit `--add-host=host.docker.internal:host-gateway` which the platform adds automatically.
- macOS and Windows: `host.docker.internal` is built in — no action required.

If agents still cannot reach the host, confirm the per-user bridge network exists (`./deploy.sh ensure-user-net $(id -u)` for Podman deployments).

## Related documentation

- [Architecture — Deployment](../architecture/deployment.md) — agent hosting modes, persistent agent lifecycle, solution structure.
- [Architecture — Infrastructure](../architecture/infrastructure.md) — Dapr building blocks, IAddressable, data persistence.
- [Developer — Setup](../developer/setup.md) — local dev loop without containers (`dapr run` + `dotnet run`).
- [Developer — Operations](../developer/operations.md) — migrations, DataProtection keys, backups.
- [Developer — Secret store](../developer/secret-store.md) — per-agent / per-unit secret scoping and rotation.
- [`deployment/README.md`](../../deployment/README.md) — the deploy.sh reference, remote deploys, webhook relay.
- [`dapr/README.md`](../../dapr/README.md) — Dapr component and configuration reference.
