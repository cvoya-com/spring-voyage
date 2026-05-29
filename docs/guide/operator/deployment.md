# Deployment

This guide walks an operator from zero to a working single-host Spring Voyage deployment. Kubernetes and multi-region deployments are covered in the Spring Voyage Cloud repository; this guide targets the open-source single-host scenario (workstation, home server, or single server).

For the architectural picture read [Architecture — Deployment](../../architecture/deployment.md) and [Architecture — Components](../../architecture/components.md) first. Operator tasks above provisioning (backups, DataProtection keys, migrations) live in [Developer — Operations](../../developer/operations.md).

Two paths are supported:

1. **Source-free installer (canonical).** Curlable `install.sh` — see [§ Quick install](#quick-install-source-free) below. Recorded in [ADR-0042](../../decisions/0042-local-operator-installer.md).
2. **Build from source.** Clone the repo and run `eng/build/build.sh && eng/deploy/deploy.sh up`. Covered in [§ Build from source](#build-from-source) further down.

## Prerequisites

- **Host:** Linux (any distro with kernel 5.10+) or macOS. Windows operators follow the manual install docs in v0.1.
- **Container runtime:** Podman 4.4+ (rootless-capable). Docker Engine 24+ is a manual secondary path; the installer is Podman-only.
- **Ports:** 80 and 443 free on the host (Caddy binds them for TLS), plus 8090 (dispatcher) and 5050 (worker MCP). If any are taken, the installer offers to remap them — or set `CADDY_HTTP_PORT` / `CADDY_HTTPS_PORT` / `SPRING_DISPATCHER_PORT` / `Mcp__Port` in `spring.env`. Moving Caddy off 80/443 disables automatic Let's Encrypt (ACME validates against the public host's 80/443).
- **A DNS name** pointing at the host — required only if you want Let's Encrypt TLS. Internal / `*.localhost` use is fine without DNS.

No Dapr CLI is required on the host. The stack bundles its own Dapr control plane (placement + scheduler).

## Quick install (source-free)

The canonical entry-point. Runs from a release tarball; no git clone needed.

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

The installer validates pre-flight requirements, downloads and verifies the release archive, generates `~/.spring-voyage/spring.env`, and starts the container stack. Two prompts: `DEPLOY_HOSTNAME` (default `localhost`) and an optional GitHub App registration — press Enter to accept both defaults.

Open **http://localhost** (or `https://<DEPLOY_HOSTNAME>` for a custom hostname with DNS). The portal guides you through creating your first unit, including any credential setup. The `spring` CLI and `voyage` operator wrapper are both on `PATH`.

LLM credentials and other post-install configuration are covered in [§ Secrets bootstrap](#secrets-bootstrap).

### Uninstall

```bash
voyage uninstall          # stops stack, removes release assets; preserves spring.env and workspaces
voyage uninstall --purge  # factory reset — removes everything including spring.env and workspaces
voyage uninstall --yes    # skip confirmation prompt (works with --purge too)
```

Both modes are idempotent.

### Day-2 operations

```bash
voyage status             # install version, container health, dispatcher health, web URL
voyage logs               # tail all container logs
voyage logs spring-api    # tail one container
voyage logs dispatcher    # tail the host-process dispatcher log
voyage restart            # restart the stack
voyage version            # installed version + platform image tag
voyage install            # re-install from the latest release
```

For the day-to-day platform CLI (units, agents, secrets), use `spring` instead. `voyage help` lists all subcommands.

### Installer options

| Flag | Purpose |
|------|---------|
| `--version <tag>` | Install a specific release. Accepted forms: `1.0.0`, `v1.0.0`, or `spring-voyage-v1.0.0`. Defaults to latest stable. |
| `--root <dir>` | Install root (defaults to `~/.spring-voyage`). |
| `--yes` | Non-interactive. Uses `DEPLOY_HOSTNAME=localhost`, skips the GitHub App prompt. |
| `--force` | Bypass the "already installed" refusal. Only use this if `uninstall` is broken. |
| `--no-start` | Generate `spring.env` and assets but skip `deploy.sh up`. Useful for CI bring-up that wants to inspect the env file first. |

### Pin to a specific version

Every release attaches a version-baked installer at `install-<v>.sh`. Use it when a doc, runbook, or CI job must install exactly one version:

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/download/spring-voyage-v1.0.0/install-1.0.0.sh | bash
```

`install-<v>.sh` is identical to `install.sh` except its `BAKED_VERSION` is pre-filled; it refuses `--version` overrides pointing elsewhere.

### Manual install (air-gapped / custom layout)

Download and verify the archive yourself, then extract:

```bash
VERSION=1.0.0
RID=linux-x64   # or linux-arm64, osx-x64, osx-arm64, win-x64
TAG="spring-voyage-v${VERSION}"

curl -fSL "https://github.com/cvoya-com/spring-voyage/releases/download/${TAG}/spring-voyage-${VERSION}-${RID}.tar.gz" \
  -o /tmp/spring-voyage.tar.gz
curl -fSL "https://github.com/cvoya-com/spring-voyage/releases/download/${TAG}/SHA256SUMS" \
  -o /tmp/SHA256SUMS

( cd /tmp && sha256sum -c --ignore-missing SHA256SUMS )

mkdir -p "${HOME}/.spring-voyage/releases/${VERSION}"
tar -xzf /tmp/spring-voyage.tar.gz -C "${HOME}/.spring-voyage/releases/${VERSION}"
```

The archive expands into `bundle/`, `cli/`, and `dispatcher/` under `~/.spring-voyage/releases/<v>/`. Run `releases/<v>/bundle/deploy.sh up` after generating a `spring.env` (see [§ Build from source](#build-from-source) for the env template).

### What ends up on disk

```text
~/.spring-voyage/
  releases/<v>/bundle/        # Deployment bundle (deploy.sh, dapr/, manifest.json, …)
  releases/<v>/dispatcher/    # Dispatcher binary (Cvoya.Spring.Dispatcher)
  releases/<v>/cli/           # `spring` CLI binary
  current  ->  releases/<v>/bundle
  spring.env                  # Mode 0600
  host/                       # spring-dispatcher state (dispatcher.env, PID)
  workspaces/                 # Per-thread agent workspaces
~/.local/bin/
  spring         -> releases/<v>/cli/spring
  voyage            # operator wrapper: status | logs | restart | version | install | uninstall
```

### What the installer does (detail)

1. Validates pre-flight: not root, `bash >= 4`, `curl`, `tar`, `openssl`, `podman >= 4`, the four host-published ports (80, 443, 8090, 5050) free — offering to remap any in use — `~/.local/bin` on PATH, `podman machine` running on macOS.
2. Resolves the release tag (`--version`, `$SPRING_VOYAGE_VERSION`, or latest stable from the GitHub API).
3. Downloads the per-RID host archive and `SHA256SUMS`; verifies the checksum.
4. Pulls the platform image (`ghcr.io/cvoya-com/spring-voyage:<v>`).
5. Prompts for `DEPLOY_HOSTNAME` (default `localhost`).
6. Generates `~/.spring-voyage/spring.env` (mode 0600) — passwords, AES key, redirect URI, and image refs are all derived automatically.
7. Starts the stack via `deploy.sh up`.
8. Optionally drives the GitHub App manifest flow (`spring github-app register`) — a single browser click on GitHub. Skip it here and run it later when you want GitHub features.
9. Prints a summary: install path, `spring.env` path, web URL, log location, and the uninstall command.

The web URL is `http://localhost` for the default install, or `https://${DEPLOY_HOSTNAME}` once you point public DNS at the host and Caddy issues a Let's Encrypt certificate.

## Build from source

If you want to track `main`, develop against an unreleased commit, or use Docker instead of Podman, clone the repo. Substitute `docker compose` for `podman compose` if you prefer Podman, or use `./deploy.sh` (Podman-native, see [below](#podman-rootless)).

```bash
# 1. Clone the repository and check out a stable tag.
git clone https://github.com/cvoya-com/spring-voyage.git
cd spring-voyage
git checkout v1.0.0   # or `main` while tracking head

# 2. Seed the environment file from the documented template.
cp eng/config/spring.env.example eng/config/spring.env

# 3. Edit secrets. At minimum change POSTGRES_PASSWORD and — if you expose
#    the stack publicly — REDIS_PASSWORD, DEPLOY_HOSTNAME, and ACME_EMAIL.
$EDITOR eng/config/spring.env

# 4. Build the platform image (one image serves api, worker, and web).
cd eng/deploy
docker compose --env-file ../config/spring.env build

# 5. Start the stack.
docker compose --env-file ../config/spring.env up -d

# 6. Verify.
docker compose --env-file ../config/spring.env ps
curl -fsS http://localhost/health
```

`http://<DEPLOY_HOSTNAME>/` serves the Next.js web portal; `http://<DEPLOY_HOSTNAME>/api/` is the REST API. If you set a public FQDN with DNS pointing at the host, Caddy will issue a Let's Encrypt certificate automatically on the first request — at that point, switch to `https://`.

## Container stack

`eng/build/Dockerfile` produces one `localhost/spring-voyage:<tag>` image; the container `command` selects which process to run. Platform services share the `spring-net` bridge. Caddy and selected control-plane services are also dual-attached to `spring-tenant-default` so agent and workflow containers on the tenant bridge can reach them.

| Container | Image | Role |
|-----------|-------|------|
| `spring-postgres` | `postgres:17` | Primary database + Dapr state store backend |
| `spring-redis` | `redis:7` | Dapr pub/sub backend |
| `spring-placement` | `daprio/dapr:<tag>` | Dapr actor placement |
| `spring-scheduler` | `daprio/dapr:<tag>` | Dapr actor reminders / scheduling |
| `spring-api-dapr` | `daprio/dapr:<tag>` | daprd sidecar for `spring-api` |
| `spring-worker-dapr` | `daprio/dapr:<tag>` | daprd sidecar for `spring-worker` |
| `spring-worker` | `localhost/spring-voyage:<tag>` | Dapr actor host + EF migrations |
| `spring-api` | `localhost/spring-voyage:<tag>` | ASP.NET Core REST API (port 8080) |
| `spring-web` | `localhost/spring-voyage:<tag>` | Next.js portal (port 3000) |
| `spring-caddy` | `caddy:2` | Reverse proxy + TLS (host `:80`/`:443`); also tenant-to-platform ingress at `:8443` (see below) |

Each .NET host talks to its own daprd sidecar container. See [Architecture — Deployment](../../architecture/deployment.md) for the topology rationale.

### Tenant-to-platform ingress

Agent containers and workflow containers run on the `spring-tenant-default` bridge network and must reach the platform's authenticated REST API without crossing onto `spring-net`. Caddy is dual-attached to both networks and exposes a dedicated listener at port 8443 inside the tenant bridge.

Tenant containers call the API at `http://spring-caddy:8443/api/v1/...` — the same authenticated surface external clients use, via the same auth middleware. No special-case routing or direct-infra-access shortcuts are involved (ADR 0028 Decision D).

```
# From inside spring-tenant-default (agent or workflow container):
curl -H "Authorization: Bearer <token>" http://spring-caddy:8443/api/v1/units
```

Port 8443 is not published to the host. It is accessible only from containers on `spring-tenant-default`. Production TLS hardening for this internal path is tracked in [#1375](https://github.com/cvoya-com/spring-voyage/issues/1375).

## Docker Compose

Reference file: `eng/deploy/docker-compose.yml`. Run from the `eng/deploy/` directory so `../dapr/` bind mounts resolve.

```bash
cp eng/config/spring.env.example eng/config/spring.env && $EDITOR eng/config/spring.env

cd eng/deploy/
docker compose --env-file ../config/spring.env build    # build platform image
docker compose --env-file ../config/spring.env up -d    # start stack
docker compose --env-file ../config/spring.env ps       # status
docker compose --env-file ../config/spring.env logs -f spring-api
docker compose --env-file ../config/spring.env down     # stop (volumes preserved)
```

Volumes persist across `down`/`up` cycles; `docker volume rm` clears them. To use a registry image, set `SPRING_PLATFORM_IMAGE` in `spring.env` and skip the `build` step.

## Podman (rootless)

`eng/deploy/deploy.sh` is the Podman-native driver (no compose shim).

```bash
cp eng/config/spring.env.example eng/config/spring.env && $EDITOR eng/config/spring.env

cd eng/deploy/
../build/build.sh              # build platform + agent images
../build/build.sh clean        # remove local Spring Voyage image refs
./deploy.sh up                 # create network, republish dispatcher, start stack
./deploy.sh status             # list running containers
./deploy.sh logs spring-api    # tail one service
./deploy.sh down               # stop (volumes preserved)
./deploy.sh clean              # destructive reset: containers, volumes, networks, local images
./deploy.sh restart            # down + up
```

For host-installed Ollama, use `./deploy.sh up --local-ollama`; add
`--ollama-port <port>` or `--ollama-endpoint <host-or-url>` when the
service is not reachable on the default `127.0.0.1:11434` host port. Host mode
removes any stale `spring-ollama` container and generates a delegated-agent
Dapr component profile so Spring Voyage agent sidecars use the same host
endpoint as the platform containers. Missing Ollama models are pulled by the
`spring-voyage` agent runtime on first use unless `SPRING_OLLAMA_AUTO_PULL=false`
is set for the agent.

Rootless notes:
- Podman 4.4+ required.
- Ports 80 and 443 need `CAP_NET_BIND_SERVICE` or `net.ipv4.ip_unprivileged_port_start` lowered.
- `host.containers.internal` requires Podman 4.1+ on Linux; older versions get `--add-host` added automatically.

See `eng/deploy/README.md` for per-user agent networks and webhook relay.

## Dapr components

Components live under `eng/dapr/` in the repo. Two profiles ship in-tree: `eng/dapr/components/local/` (dev loop) and `eng/dapr/components/production/` (Docker Compose / Podman). Both stacks bind-mount the production directory at `/components` inside each sidecar. **Edit a component YAML and restart the sidecar to apply — no image rebuild needed.**

### State store (`statestore`)

`eng/dapr/components/production/statestore.yaml` uses `state.postgresql` backed by `spring-postgres`. The connection string is pulled from the `secretstore` component (never inlined in the YAML). Swap to `state.redis` in this file to trade ACID semantics for speed — keep the component name `statestore`.

### Secrets-backing state store (`secretsstore`)

`eng/dapr/components/production/secretsstore.yaml` is a second `state.postgresql` component dedicated to the OSS application-layer secret store (`DaprStateBackedSecretStore`). It pins `keyPrefix: none` so every host (`spring-api`, `spring-worker`, dispatcher) reads and writes through the same key namespace; the default `statestore` component uses Dapr's standard `keyPrefix: appid`, which silently scoped each secret by sidecar app-id and broke cross-host credential resolution (#2212). The actor / general state store cannot share `keyPrefix: none` because the Dapr actor runtime relies on the per-app prefix to partition actor state — hence the dedicated component.

### Pub/sub (`pubsub`)

`eng/dapr/components/production/pubsub.yaml` uses `pubsub.redis` (Redis Streams). For multi-broker deployments (NATS, RabbitMQ, Kafka) swap this file. The platform keys off the component **name** (`pubsub`), not the implementation.

### Secret store (`secretstore`)

`eng/dapr/components/production/secretstore.yaml` uses `secretstores.local.env`, which reads secrets from the sidecar process environment (`spring.env` is passed via `--env-file`). For cloud-grade management replace this file with the Dapr Azure Key Vault, HashiCorp Vault, or Kubernetes Secrets component — keep the name `secretstore`.

## PostgreSQL setup

The stack runs PostgreSQL 17 in `spring-postgres` with a named volume (`spring-postgres-data`). The image creates the user, password, and database on first start from `POSTGRES_USER`, `POSTGRES_PASSWORD`, and `POSTGRES_DB` in `spring.env`.

Two connection strings must be kept in sync:

| Variable | Consumer | Format |
|----------|----------|--------|
| `ConnectionStrings__SpringDb` | Platform hosts (EF Core) | Npgsql (`Host=…;Database=…;Username=…;Password=…`) |
| `SPRING_POSTGRES_CONNECTION_STRING` | Dapr state store | libpq-style (`host=… user=… password=… dbname=…`) |

A missing `ConnectionStrings__SpringDb` is a hard startup error. `spring.env.example` wires both from the three `POSTGRES_*` variables via `envsubst`.

**Migrations:** the Worker host runs EF Core migrations automatically at startup via `DatabaseMigrator`. The API host trusts the schema is in place (hence `depends_on: spring-worker`). To run migrations out-of-band:

```ini
# spring.env
Database__AutoMigrate=false
```

See [Developer — Operations § Database Migrations](../../developer/operations.md#database-migrations) for the manual path.

**External Postgres (RDS, Cloud SQL, etc.):** remove `spring-postgres` from your compose file, update both connection strings, and set `sslmode=require` for a non-local database.

## Redis setup

Redis 7 runs as `spring-redis` with AOF persistence (`appendonly yes`). Set `REDIS_PASSWORD` for any public-facing deployment; leave it empty only on a laptop.

Redis carries the pub/sub building block (Redis Streams, at-least-once). The default state store is PostgreSQL; swap `eng/dapr/components/production/statestore.yaml` to `state.redis` if you need faster but non-ACID state.

**External Redis:** update `redisHost` in `pubsub.yaml`, set `REDIS_PASSWORD` in `spring.env`, and remove `spring-redis` from your compose file. Add `enableTLS: "true"` to the Dapr component metadata for a TLS-protected instance.

## TLS with Caddy

Caddy fronts three upstreams: `spring-api:8080` (API + `/health`), `/api/v1/webhooks/*` (webhook ingress), and `spring-web:3000` (portal).

Two Caddyfile variants ship in `eng/deploy/`:
- **`Caddyfile`** — single hostname, path-routed (default).
- **`Caddyfile.multi-host`** — one FQDN per service. Select with `SPRING_CADDYFILE=Caddyfile.multi-host`.

**Let's Encrypt (automatic):** set `DEPLOY_HOSTNAME=app.example.com`, `DEPLOY_SCHEME=https`, and `ACME_EMAIL` in `spring.env`. Point DNS `A`/`AAAA` at the host and ensure ports 80 and 443 are open. Caddy issues a certificate on the first request.

**Local / private:** hostnames ending in `.localhost` or `localhost` fall back to plain HTTP. Set `DEPLOY_SCHEME=http` explicitly.

**nginx instead:** remove `spring-caddy` and proxy directly to `spring-api:8080` and `spring-web:3000`. You are responsible for TLS (certbot / cert-manager).

## Secrets bootstrap

All secrets live in `eng/config/spring.env`. The file is **not** committed (only `spring.env.example` is). Restrict its permissions:

```bash
chmod 600 /opt/spring-voyage/eng/config/spring.env
```

### Mandatory variables

| Variable | Purpose |
|----------|---------|
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Initial Postgres credentials |
| `ConnectionStrings__SpringDb` | Npgsql connection string (interpolated from the three above in `spring.env.example`) |
| `SPRING_POSTGRES_CONNECTION_STRING` | libpq-style connection string for the Dapr state store |
| `REDIS_PASSWORD` | Redis `requirepass` (leave empty only on a laptop) |
| `DEPLOY_HOSTNAME` | Public FQDN or `localhost` |

### Tier-1 platform credentials — GitHub App identity (env only)

Spring Voyage does **not** ship a shared GitHub App private key. Each
deployment registers its own GitHub App and configures the values below
with that App's credentials. The full registration walkthrough — both
the `spring github-app register` one-liner and the manual github.com
flow — lives in [Register your GitHub App](github-app-setup.md).

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

> **GitHub App private key — PEM contents, not a path.** `GitHub__PrivateKeyPem` is the **contents** of the `.pem` file: either inlined verbatim, inlined as a single line with `\n` separators, or an absolute container-visible path whose file contents are valid PEM. `~` is **not** expanded by `--env-file`, so a value like `~/secrets/key.pem` reaches the container as the literal string `~/secrets/key.pem` — mount the file at a known absolute path if you want to reference it by path. Passing a path that does not resolve to a valid PEM fails the host at startup with a targeted error rather than waiting to return a 502 from the first `list-installations` call. See [Architecture — Connectors § disabled-with-reason](../../architecture/connectors.md#disabled-with-reason-pattern) for the validation model. If either variable is missing, the GitHub connector boots in a disabled state and `GET /api/v1/connectors/github/actions/list-installations` returns a structured `404` the portal and CLI render as "GitHub App not configured" instead of attempting the JWT sign.

### Tier-2 tenant-default credentials — LLM runtime credentials (post-deploy)

**LLM runtime credentials do NOT belong in `spring.env`.** They are tier-2
tenant-default credentials (issue #615) stored in the secret registry so
they can be rotated without a restart, scoped per-unit, and audited.
There is no env-variable fallback — if no tenant or unit secret is
configured the platform surfaces a fail-clean "no LLM credentials
configured" error with a remediation hint.

Set them after `docker compose up -d`:

```bash
# CLI
spring secret create --scope tenant anthropic-oauth   --value "<token from claude setup-token>"
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."
spring secret create --scope tenant openai-api-key    --value "sk-..."
spring secret create --scope tenant google-api-key    --value "AIza..."

# or the portal: open Settings → "Tenant defaults" panel → paste value → Set
```

Units inherit these automatically; override per-unit with a same-name
secret at unit scope. The platform does not read LLM runtime credentials
from environment variables — credentials must be set at tenant or unit
scope, or the feature fails cleanly. See [Managing Secrets](secrets.md)
for the full two-tier resolution chain.

### GitHub App — webhook delivery

Webhook providers (including GitHub) post to `/api/v1/webhooks/<provider>` on your public FQDN. Confirm:

- `WEBHOOK_HOSTNAME` (if using the multi-host Caddyfile) or `DEPLOY_HOSTNAME` resolves publicly.
- Port 443 is reachable from the internet.
- The GitHub App's webhook URL is `https://<host>/api/v1/webhooks/github` and `GitHub__WebhookSecret` matches both ends.

For local development against a laptop, use `eng/deploy/relay.sh` to open an SSH reverse tunnel from a small relay VPS — see `eng/deploy/README.md#local-dev-webhook-tunnel-relaysh`.

### Cloud-grade secret stores

For Azure Key Vault, HashiCorp Vault, AWS Secrets Manager, or Kubernetes Secrets, replace `eng/dapr/components/production/secretstore.yaml` with the corresponding Dapr component. Leave the component name `secretstore` — the other components reference the store by name so they continue to work unchanged. See [Developer — Secret store](../../developer/secret-store.md) for per-agent / per-unit secret scoping details.

## Health checks

```bash
# API host (behind Caddy)
curl -fsS http://localhost/health
# Directly
docker exec spring-api curl -fsS http://localhost:8080/health
# Dapr sidecar readiness
docker exec spring-api-dapr wget -q -O- http://localhost:3500/v1.0/healthz
```

- `spring-api /health` — HTTP listener is up. Does not imply database or Dapr reachability.
- `spring-worker /health` — Worker is up; migrations completed.
- Dapr sidecar `/v1.0/healthz/outbound` — components loaded and control plane reachable.
- `docker compose ps` — shows `(healthy)` for Postgres (`pg_isready`) and Redis (`redis-cli ping`).

End-to-end smoke test:

```bash
spring unit create deployment-smoke
spring unit list        # must include deployment-smoke
spring unit delete deployment-smoke
```

## Updating to a new version

Treat every update as potentially breaking: read the release notes, test in staging first, and back up the database before rolling production.

**Registry flow:**
```bash
sed -i 's/^SPRING_IMAGE_TAG=.*/SPRING_IMAGE_TAG=0.2.0/' eng/config/spring.env
cd eng/deploy/
docker compose --env-file ../config/spring.env pull
docker compose --env-file ../config/spring.env up -d
```

**Source flow:**
```bash
git fetch --tags && git checkout v0.2.0
cd eng/deploy/
docker compose --env-file ../config/spring.env build
docker compose --env-file ../config/spring.env up -d
```

`up -d` recreates only changed services. Migrations run automatically on `spring-worker` restart before `spring-api` comes up.

**Before:** `pg_dump` the database and back up the `spring-dataprotection-keys` volume — it holds the key ring for auth cookies and OAuth tokens. **Never `docker volume rm spring-dataprotection-keys`** during an update.

**After:** confirm `/health`, tail `spring-worker` logs for migration lines, run the smoke test above. Roll back by checking out the previous tag and re-running `up -d`. See [Developer — Operations § DataProtection](../../developer/operations.md#dataprotection-keys).

## Troubleshooting

### `spring-api` exits immediately with `No connection string found for SpringDbContext`

`ConnectionStrings__SpringDb` is missing or empty in `spring.env`. The host refuses to start rather than silently fall back to an in-memory store. Restore the line and re-deploy.

### `spring-worker` logs `42P07: relation "..." already exists`

Two instances are trying to run EF migrations against the same database. The OSS topology runs migrations only on `spring-worker`; confirm `Database__AutoMigrate=false` is set anywhere else and that you only ever run one Worker replica against a given database. Details in [Developer — Operations § Multi-replica deployments](../../developer/operations.md#multi-replica-deployments).

### Dapr sidecar crashes with `components path not found`

The compose file bind-mounts `../dapr/components/production` relative to `eng/deploy/` (so it resolves to `eng/dapr/components/production`). Make sure you invoke `docker compose` from inside `eng/deploy/` so that relative path resolves. If you move the compose file, update the bind-mount source.

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

- [Architecture — Deployment](../../architecture/deployment.md) — agent hosting modes, persistent agent lifecycle, solution structure.
- [Architecture — Components](../../architecture/components.md) — Dapr building blocks, actors, infrastructure dependencies.
- [Developer — Setup](../../developer/setup.md) — local dev loop without containers (`dapr run` + `dotnet run`).
- [Developer — Operations](../../developer/operations.md) — migrations, DataProtection keys, backups.
- [Developer — Secret store](../../developer/secret-store.md) — per-agent / per-unit secret scoping and rotation.
- [`eng/install/README.md`](../../../eng/install/README.md) — installer/uninstaller details and troubleshooting.
- [`eng/deploy/README.md`](../../../eng/deploy/README.md) — the build/deploy script reference, per-user agent networks, webhook relay.
- [`eng/dapr/README.md`](../../../eng/dapr/README.md) — Dapr component and configuration reference.
- [ADR-0042 — Local-host operator installer](../../decisions/0042-local-operator-installer.md) — design decisions behind `install.sh`/`uninstall.sh`.
