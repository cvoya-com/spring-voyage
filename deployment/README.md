# Deployment

Podman-based deployment scripts for running Spring Voyage on a single machine
(local workstation or single VPS). For Kubernetes / cloud-scale deployment see
the private Spring Voyage Cloud repository — this directory targets the
open-source single-host scenario.

## Contents

| File                    | Purpose                                                           |
| ----------------------- | ----------------------------------------------------------------- |
| `deploy.sh`             | Local Podman deployment (network, containers, images).            |
| `deploy-remote.sh`      | SSH + rsync wrapper that runs `deploy.sh` on a remote VPS.        |
| `Dockerfile`            | Multi-stage platform image (.NET 10 API/Worker + Web + Dapr CLI). |
| `Dockerfile.agent`      | Slim image for delegated agent execution containers.              |
| `Caddyfile`             | Single-host path-routed Caddy config (default).                   |
| `Caddyfile.multi-host`  | Per-service hostnames variant (web / API / webhook each FQDN).    |
| `relay.sh`              | Local-dev SSH reverse tunnel for webhook delivery to a laptop.    |
| `spring.env.example`    | Documented env template. Copy to `spring.env` and fill in.        |

## Prerequisites

- [Podman](https://podman.io/) 4.4+ (required for `podman network exists` and
  modern rootless networking). Install via your distro's package manager.
- `bash`, `rsync`, `ssh` for the remote workflow.
- On the VPS: Podman installed, a non-root user able to run rootless Podman,
  ports 80/443 available for Caddy.

No Docker Compose / Podman Compose dependency — the script uses `podman` directly
so behavior is deterministic across Podman versions.

## Container stack

All platform containers attach to a shared Podman network called `spring-net`:

| Container         | Image                     | Role                                     |
| ----------------- | ------------------------- | ---------------------------------------- |
| `spring-postgres` | `postgres:17`             | Primary data store.                      |
| `spring-redis`    | `redis:7`                 | Dapr state store + pub/sub backend.      |
| `spring-worker`   | `spring-voyage:<tag>`     | Dapr actor runtime (agents, units).      |
| `spring-api`      | `spring-voyage:<tag>`     | ASP.NET Core REST API.                   |
| `spring-web`      | `spring-voyage:<tag>`     | Next.js dashboard.                       |
| `spring-caddy`    | `caddy:2`                 | Reverse proxy + automatic TLS.           |

Delegated agent execution containers (launched by `ContainerLifecycleManager`
at runtime) do **not** join `spring-net`. They join a per-user bridge network
named `spring-user-<uid>` to isolate one user's agents from another's while
still allowing them to reach their paired Dapr sidecar. Create or ensure a
user network with:

```bash
./deploy.sh ensure-user-net 1000
```

## Local deployment

```bash
cd deployment/
cp spring.env.example spring.env
$EDITOR spring.env             # fill in secrets, hostname, image tags

./deploy.sh build              # build platform + agent images from source
./deploy.sh up                 # create network, start the full stack
./deploy.sh status             # list running containers
./deploy.sh logs spring-api    # tail a single service
./deploy.sh down               # stop containers (volumes preserved)
```

Volumes (`spring-postgres-data`, `spring-redis-data`, `spring-caddy-data`,
`spring-caddy-config`) persist across `down`/`up` cycles. Remove them with
`podman volume rm` when you need a clean slate.

## Remote (VPS) deployment

`deploy-remote.sh` rsyncs the repo + `deployment/` to the VPS and then invokes
`deploy.sh` there over SSH.

```bash
export SPRING_REMOTE_HOST=deploy@vps.example.com
export SPRING_REMOTE_DIR=/opt/spring-voyage    # optional, this is the default

./deploy-remote.sh deploy      # sync + build + up
./deploy-remote.sh logs spring-worker
./deploy-remote.sh down
```

**Registry flow (no source on the VPS).** If you publish platform + agent
images to a registry, skip source sync and build:

```bash
export SPRING_SKIP_SOURCE_SYNC=1
# Point SPRING_PLATFORM_IMAGE / SPRING_AGENT_IMAGE in spring.env at the registry.
./deploy-remote.sh deploy      # now: rsync deployment/ + spring.env, then `up` (pulls images)
```

Podman pulls images on demand when `podman run` runs — no explicit pull step
is needed. Rotate by bumping `SPRING_IMAGE_TAG` in `spring.env` and re-running
`./deploy-remote.sh up`.

## Reverse proxy and TLS

Spring Voyage fronts the web portal, API, and webhook endpoint with
[Caddy](https://caddyserver.com/). Caddy obtains Let's Encrypt certificates
automatically for any public FQDN it serves, provided:

- The hostname's public DNS `A`/`AAAA` record points at the VPS.
- Ports `80` and `443` on the VPS are reachable from the public internet
  (the ACME HTTP-01 challenge requires `:80`).
- `ACME_EMAIL` is set in `spring.env` so Let's Encrypt can email expiry
  and revocation notices.

Hostnames ending in `.localhost`, set to `localhost`, or private LAN names
like `*.local` fall back to plain HTTP — useful for local Podman runs.

### Single-host deployment (default)

The default `Caddyfile` puts everything behind a single public hostname
and disambiguates by URL path:

| Path prefix           | Upstream           |
| --------------------- | ------------------ |
| `/api/v1/webhooks/*`  | `spring-api:8080`  |
| `/api/*`              | `spring-api:8080`  |
| `/swagger/*`, `/health` | `spring-api:8080`|
| everything else       | `spring-web:3000`  |

Set `DEPLOY_HOSTNAME` in `spring.env` to your FQDN and run `./deploy.sh up`.

### Per-service hostnames

For `app.example.com` / `api.example.com` / `hooks.example.com`, switch to
the multi-host Caddyfile:

```bash
# in spring.env
WEB_HOSTNAME=app.example.com
API_HOSTNAME=api.example.com
WEBHOOK_HOSTNAME=hooks.example.com
ACME_EMAIL=ops@example.com
SPRING_CADDYFILE=Caddyfile.multi-host
```

Each hostname gets its own certificate. Any unset `*_HOSTNAME` falls back
to `DEPLOY_HOSTNAME`, so you can mix (e.g. share the API and portal on one
host while giving the webhook endpoint its own).

## Local-dev webhook tunnel (`relay.sh`)

Webhook providers (GitHub, etc.) must POST to a publicly reachable URL.
During local development the `dotnet run` API is bound to `127.0.0.1`,
so `relay.sh` opens an SSH reverse tunnel from a small relay VPS to the
laptop:

```
provider  --HTTPS-->  relay VPS (Caddy :443 -> 127.0.0.1:$RELAY_REMOTE_PORT)
                         |
                         | SSH reverse tunnel (held open by relay.sh)
                         v
                       laptop 127.0.0.1:$LOCAL_WEBHOOK_PORT
```

Minimum setup:

1. **Relay VPS.** A separately provisioned host reachable on the public
   internet. Install sshd with `GatewayPorts clientspecified` (so the
   tunnel can bind an externally visible interface if desired) and a
   dedicated `webhooks` user whose authorized key belongs to the developer.
2. **TLS on the relay.** Run Caddy (or any reverse proxy) on the relay
   with a hostname like `hooks.dev.example.com` proxying to
   `127.0.0.1:$RELAY_REMOTE_PORT` — the tunnel endpoint. Let's Encrypt
   issues a cert for that hostname. Webhook providers then POST to
   `https://hooks.dev.example.com/api/v1/webhooks/<provider>`.
3. **Run the tunnel on the laptop.**

   ```bash
   export RELAY_HOST=relay.example.com
   export RELAY_USER=webhooks
   export RELAY_REMOTE_PORT=19080
   export LOCAL_WEBHOOK_PORT=8080      # the port spring-api listens on
   ./relay.sh
   ```

   The script uses `autossh` when available and falls back to a plain
   `ssh -N -R` reconnect loop. Press Ctrl-C to exit cleanly.

See the top of `relay.sh` for the full environment variable reference.
The script is only meant for local development — production webhooks
should target a deployed `WEBHOOK_HOSTNAME` directly.

## Secrets

Secrets are passed via `spring.env` (`--env-file`) and Dapr's secret store.
Never commit `spring.env`; it is in `.gitignore` implicitly because only
`spring.env.example` is tracked. On the VPS, restrict its permissions:

```bash
chmod 600 /opt/spring-voyage/deployment/spring.env
```

The production profile under `dapr/components/production/` uses
`secretstores.local.env`, so every `secretKeyRef` in a Dapr component
resolves against the variables in `spring.env` (loaded as `--env-file`).
For cloud-grade secret management replace `secretstore.yaml` with Azure
Key Vault, HashiCorp Vault, or Kubernetes secrets; the other production
components reference the store by name (`secretstore`) so they require no
changes. See [Infrastructure](../docs/architecture/infrastructure.md#data-persistence--configuration)
and [`dapr/README.md`](../dapr/README.md) for profile details.

## Per-user bridge networks

Delegated agents run in containers that must not see each other across user
boundaries. The scheme is:

```
spring-net            shared platform network (postgres, redis, worker, api, web, caddy)
spring-user-<uid>     per-user network for that user's agent execution containers
```

`ContainerLifecycleManager` creates the per-user network on demand via
`IContainerRuntime.EnsureNetworkAsync`. The `ensure-user-net` deploy command
exists so operators can pre-create networks (e.g., when running a pre-warmed
pool). Networks created by the script are safe to re-run — `podman network
exists` gates the create call.

## Related documentation

- [Architecture — Deployment](../docs/architecture/deployment.md) — execution modes and solution structure.
- [Architecture — Infrastructure](../docs/architecture/infrastructure.md) — Dapr building blocks, data stores.
- [Developer — Setup](../docs/developer/setup.md) — local dev flow (runs hosts via `dapr run`, not containers).
- [Developer — Operations](../docs/developer/operations.md) — health checks, backups, troubleshooting.
