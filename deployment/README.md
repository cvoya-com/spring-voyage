# Deployment

Podman-based deployment scripts for running Spring Voyage on a single machine
(local workstation or single VPS). For Kubernetes / cloud-scale deployment see
the private Spring Voyage Cloud repository — this directory targets the
open-source single-host scenario.

## Contents

| File                 | Purpose                                                           |
| -------------------- | ----------------------------------------------------------------- |
| `deploy.sh`          | Local Podman deployment (network, containers, images).            |
| `deploy-remote.sh`   | SSH + rsync wrapper that runs `deploy.sh` on a remote VPS.        |
| `Dockerfile`         | Multi-stage platform image (.NET 10 API/Worker + Web + Dapr CLI). |
| `Dockerfile.agent`   | Slim image for delegated agent execution containers.              |
| `Caddyfile`          | Caddy reverse-proxy config (TLS via Let's Encrypt when FQDN set). |
| `spring.env.example` | Documented env template. Copy to `spring.env` and fill in.        |

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

## Secrets

Secrets are passed via `spring.env` (`--env-file`) and Dapr's secret store.
Never commit `spring.env`; it is in `.gitignore` implicitly because only
`spring.env.example` is tracked. On the VPS, restrict its permissions:

```bash
chmod 600 /opt/spring-voyage/deployment/spring.env
```

For production-grade secret management, replace the local-file Dapr secret
store (`dapr/components/secretstore.yaml`) with Azure Key Vault, HashiCorp
Vault, or Kubernetes secrets. See [Infrastructure](../docs/architecture/infrastructure.md#data-persistence--configuration).

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
