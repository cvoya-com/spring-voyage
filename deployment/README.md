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
| `Dockerfile.dispatcher` | `spring-dispatcher` service image. Owns the host podman socket.    |
| `Caddyfile`             | Single-host path-routed Caddy config (default).                   |
| `Caddyfile.multi-host`  | Per-service hostnames variant (web / API / webhook each FQDN).    |
| `relay.sh`              | Local-dev SSH reverse tunnel for webhook delivery to a laptop.    |
| `spring.env.example`    | Documented env template. Copy to `spring.env` and fill in.        |
| `examples/dockerfiles/` | Starter Dockerfiles showing how to extend `spring-agent:latest` (see **Custom agent images** below). |

## Custom agent images

Unit and agent execution blocks (`execution.image`) accept any container
reference the host can pull. To run an agent in a custom image — whether
you just want a pinned tag, or you need to layer extra CLI tools on top
of `spring-agent:latest` — start from one of the templates under
`examples/dockerfiles/`:

| Template            | When to use it                                                         |
| ------------------- | ---------------------------------------------------------------------- |
| `minimal-extension` | Re-tag `spring-agent:latest` under your own registry / name. No code changes; useful for pinning a stable reference. |
| `custom-tools`      | Add extra CLI tools (system packages, npm-installed MCP servers, language toolchains). |

Each template ships with its own `README.md` covering build, reference,
and the extension pattern. Reference the built image from a unit or
agent manifest:

```yaml
unit:
  name: my-team
  execution:
    image: localhost/my-agent:latest
    runtime: podman
```

…or through the portal's **Execution** tab (new with the B-wide
#601 / #603 / #409 PR). The agent → unit → fail-clean resolution chain
documented in `../docs/architecture/units.md` means agents without their
own `execution.image` inherit the unit's default.

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

| Container            | Image                     | Role                                       |
| -------------------- | ------------------------- | ------------------------------------------ |
| `spring-postgres`    | `postgres:17`             | Primary data store.                        |
| `spring-redis`       | `redis:7`                 | Dapr state store + pub/sub backend.        |
| `spring-placement`   | `daprio/dapr:<tag>`       | Dapr actor placement service.              |
| `spring-scheduler`   | `daprio/dapr:<tag>`       | Dapr actor reminder/scheduler service.     |
| `spring-api-dapr`    | `daprio/dapr:<tag>`       | daprd sidecar paired with `spring-api`.    |
| `spring-worker-dapr` | `daprio/dapr:<tag>`       | daprd sidecar paired with `spring-worker`. |
| `spring-dispatcher`  | `spring-dispatcher:<tag>` | HTTP service that owns the host podman socket. Workers reach it over HTTP for every container op (#513). |
| `spring-worker`      | `spring-voyage:<tag>`     | Dapr actor runtime (agents, units).        |
| `spring-api`         | `spring-voyage:<tag>`     | ASP.NET Core REST API.                     |
| `spring-web`         | `spring-voyage:<tag>`     | Next.js dashboard.                         |
| `spring-caddy`       | `caddy:2`                 | Reverse proxy + automatic TLS.             |
| `spring-ollama` *    | `ollama/ollama:latest`    | Local LLM backend (optional; see below).   |

\* Optional. Only started when `OLLAMA_MODE=container` (the default). Set
`OLLAMA_MODE=host` on macOS to run Ollama on the host for Metal GPU access —
the container is skipped in that mode.

### Dapr sidecar topology

Each .NET host runs in its own container and talks to a dedicated `daprd`
sidecar container over the `spring-net` bridge — the app and the sidecar do
**not** share localhost. This is the container-sidecar form of the Dapr
pattern (as opposed to a shared Pod / process). The Dapr .NET SDK honors
`DAPR_HTTP_ENDPOINT` / `DAPR_GRPC_ENDPOINT`, which `deploy.sh` sets per app:

```
spring-api ─ DAPR_HTTP_ENDPOINT=http://spring-api-dapr:3500 ─▶ spring-api-dapr
                                                                   │
                                                                   ▼
           ┌──────────────── spring-placement:50005 ────────────────┐
           │                                                        │
           ▼                                                        ▼
 spring-worker-dapr ◀─ DAPR_HTTP_ENDPOINT=http://spring-worker-dapr:3500 ─ spring-worker
```

`spring-placement` and `spring-scheduler` are the Dapr control plane for
this stack. The deploy script starts its own copies on `spring-net` instead
of relying on `dapr init` leftovers (which live on Podman's default network
and are invisible from `spring-net`). Both control-plane containers use the
same `DAPR_IMAGE` as the per-app sidecars so the wire format is guaranteed
to match.

Dapr components (`dapr/components/production/*.yaml`) and the Dapr
Configuration (`dapr/config/production.yaml`) are bind-mounted read-only
into both sidecars at `/components` and `/config/config.yaml`, so operators
can tune tracing, resiliency, or swap the secret store without rebuilding
images. The image version is controlled by `DAPR_IMAGE` in `spring.env`
and MUST match the Dapr .NET SDK major.minor pinned in
`Directory.Packages.props` (currently `1.17.x`).

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
$EDITOR spring.env             # deploy-time config: hostname, DB password, image tags

./deploy.sh build              # build platform + agent images from source
./deploy.sh up                 # create network, start the full stack
./deploy.sh status             # list running containers
./deploy.sh logs spring-api    # tail a single service
./deploy.sh down               # stop containers (volumes preserved)
```

Volumes (`spring-postgres-data`, `spring-redis-data`, `spring-caddy-data`,
`spring-caddy-config`) persist across `down`/`up` cycles. Remove them with
`podman volume rm` when you need a clean slate.

### Startup configuration validation (#616)

The API and Worker hosts validate their tier-1 configuration at startup.
When a **mandatory** requirement is missing or malformed, the host refuses
to boot and exits non-zero — Podman / systemd restart as normal, but the
platform stays down until the operator fixes the underlying value. The
original fail-fast that landed in #261 for `ConnectionStrings:SpringDb`
remains the headline example: no connection string, no boot, and you'll
see something like:

```
System.InvalidOperationException: No connection string found for SpringDbContext.
Set the ConnectionStrings:SpringDb configuration value (environment
variable ConnectionStrings__SpringDb=...) to a valid PostgreSQL
connection string. See deployment/README.md.
```

PEM-parse failures for `GITHUB_APP_PRIVATE_KEY` fail-fast the same way
(carried forward from PR #621); a garbage value won't defer the failure
to the first `list-installations` call.

**Optional** requirements (GitHub App credentials when you haven't run
`spring github-app register` yet, Ollama when it's still warming up with
`LanguageModel:Ollama:RequireHealthyAtStartup=false`) do NOT abort boot
— they register the dependent features as disabled-with-reason and
surface the reason to operators.

Inspect the result post-deploy:

```bash
# CLI
spring system configuration                  # table view
spring system configuration --json           # JSON (for jq pipelines)
spring system configuration "GitHub Connector"  # drill into one subsystem

# HTTP
curl http://localhost:5000/api/v1/system/configuration | jq .

# Portal
# Visit /system/configuration in your browser.
```

The report is cached at host startup — it does not move until the host
restarts. Changing a tier-1 value (new connection string, new GitHub App)
requires `./deploy.sh restart` or the matching per-container restart.

### Post-deploy: LLM provider credentials (tier-2, #615)

LLM provider API keys (Anthropic, OpenAI, Google) are **tier-2 tenant-default
credentials** — per-tenant secrets that units inherit — and live in the
database, not in `spring.env`. After `./deploy.sh up` completes, set them
from the CLI or the portal:

```bash
# CLI — one row per provider in use
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."
spring secret create --scope tenant openai-api-key    --value "sk-..."
spring secret create --scope tenant google-api-key    --value "AIza..."

# Or open the portal, click Settings, and set the keys in the
# "Tenant defaults" panel. Values are encrypted at rest (AES-GCM envelope)
# and never returned to the browser.
#
# Or, when creating the very first unit, supply the key inline (#626):
# the wizard at `/units/create` and the `spring unit create` verb both
# accept `--api-key-from-file` + `--save-as-tenant-default` (CLI) or
# the "Save as tenant default" checkbox (portal) to write the tenant
# default in the same step.
spring unit create first-team \
  --tool claude-code \
  --api-key-from-file ./anthropic.txt \
  --save-as-tenant-default
```

Individual units can override the tenant default by registering a
same-name secret at unit scope (Secrets tab on the unit detail page, or
`spring secret create --scope unit --unit <name> <key> --value "..."`).
From the unit-creation wizard / CLI verb, the same effect is achieved by
supplying `--api-key(-from-file)` **without** `--save-as-tenant-default` —
the key is written as a per-unit secret after the unit exists.

If you are upgrading a deployment that had `ANTHROPIC_API_KEY` /
`OPENAI_API_KEY` in `spring.env`, move the keys to tenant defaults with
the CLI or portal above and remove them from `spring.env`. The platform
no longer reads those environment variables — credentials must be set at
tenant or unit scope. See [`docs/guide/secrets.md`](../docs/guide/secrets.md)
for the full resolution chain.

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
| `/openapi/*`, `/health` | `spring-api:8080`|
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

### GitHub App setup

There are two ways to bootstrap the GitHub App credentials the connector
needs. The CLI helper is the recommended path — it drops the ~10 manual
GitHub-docs steps to one browser click.

#### Option A — one-shot CLI helper (`spring github-app register`)

```bash
# User-account App
spring github-app register --name "Spring Voyage (prod)"

# Or register under an org
spring github-app register \
  --name "Spring Voyage (prod)" \
  --org cvoya-com

# Air-gapped / CI inspection — builds manifest + prints URL, no I/O
spring github-app register --name "Spring Voyage (prod)" --dry-run
```

The verb drives GitHub's [App-from-manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest):

1. Binds a loopback HTTP listener on `127.0.0.1:<ephemeral-port>` (retries
   on port collisions up to three times).
2. Opens your browser at `https://github.com/settings/apps/new?manifest=<base64>`
   with every permission + webhook event pre-filled.
3. You click **Create**. GitHub redirects back to the listener with a
   one-time code.
4. CLI exchanges the code via `POST /app-manifests/{code}/conversions`
   and receives the App ID, PEM, webhook secret, and OAuth client id/secret.
5. Credentials land in `deployment/spring.env` (default — `--write-env`)
   or in the platform-secrets store (`--write-secrets`; uses
   `spring secret --scope platform create` from #612).
6. The install URL is printed — visit it to install the App on the repos
   you care about.

The listener times out after 5 minutes; if you close the browser without
confirming, re-run the verb. GitHub's own errors (e.g. "name has already
been taken") are surfaced verbatim so you can rename with a suffix.

See [`docs/architecture/cli-and-web.md § GitHub App bootstrap verb (#631)`](../docs/architecture/cli-and-web.md#github-app-bootstrap-verb-631)
for the full flag list.

#### Option B — register manually via the GitHub UI

When you need fine-grained control (custom description, private-repo
restrictions beyond the manifest default, etc.), follow the GitHub docs
to register the App by hand. The connector expects the credentials in
the env vars documented below.

### GitHub App credentials — PEM, not a path

The GitHub connector reads its credentials through the .NET
`Section__Key` env-var convention:

| Variable | Maps to | Expected shape |
| -------- | ------- | -------------- |
| `GitHub__AppId` | `GitHub:AppId` | Numeric GitHub App id. |
| `GitHub__PrivateKeyPem` | `GitHub:PrivateKeyPem` | PEM **contents** (or a path to a readable PEM file — see below). |
| `GitHub__WebhookSecret` | `GitHub:WebhookSecret` | Shared secret configured on the GitHub App. |

`GitHub__PrivateKeyPem` must hold the full PEM block
(`-----BEGIN ... PRIVATE KEY-----` through `-----END ... PRIVATE KEY-----`),
not a filesystem path. The connector's startup validator accepts three
shapes and rejects everything else:

1. **Inline PEM contents** — the common case when pasting the key into
   `spring.env`.
2. **A readable file path whose contents are valid PEM** — ergonomic for
   Docker secret mounts / Kubernetes volumes. The contents are read
   once at startup and used in place of the path.
3. **Both variables unset** — the connector registers in a *disabled
   with reason* state; `GET /api/v1/connectors/github/actions/list-installations`
   returns a structured `404` the portal and CLI render as "GitHub App
   not configured" instead of attempting the JWT sign.

A path that does **not** point at a readable PEM file, or inline text that
isn't a PEM block, fails the host at startup with a targeted error — the
platform refuses to boot so the misconfiguration is visible immediately
rather than surfacing as an HTTP 502 from the first feature-use call
(#609).

#### Docker Compose / Podman example

Mount the PEM as a secret and point `GitHub__PrivateKeyPem` at the mount
path:

```yaml
# compose.yml
services:
  spring-api:
    environment:
      GitHub__AppId: "123456"
      GitHub__PrivateKeyPem: "/run/secrets/github-app-key"
      GitHub__WebhookSecret: "<shared>"
    secrets:
      - github-app-key

secrets:
  github-app-key:
    file: ./secrets/github-app-key.pem
```

The startup validator notices the value is a filesystem path, reads the
mounted file, verifies the contents are valid PEM, and adopts them. No
extra shell script or `entrypoint.sh` transformation is required.

Alternative — inline the contents in `spring.env`:

```ini
GitHub__AppId=123456
GitHub__PrivateKeyPem=-----BEGIN PRIVATE KEY-----
MIIEvwIBADAN... (full key) ...
-----END PRIVATE KEY-----
GitHub__WebhookSecret=<shared>
```

`spring.env.example` ships with the three variables commented out for
this reason; uncomment the set you intend to use.

## Local AI (Ollama)

Spring Voyage supports [Ollama](https://ollama.com) as a first-class LLM backend
for local and self-hosted deployments. Enable it by setting
`LanguageModel__Ollama__Enabled=true` in `spring.env` — the platform then uses
Ollama's OpenAI-compatible `/v1/chat/completions` endpoint and no API key is
required.

Two modes are supported and selected by the deploy-time `OLLAMA_MODE` variable:

| Mode          | Deploy flag           | When to use                                                       |
| ------------- | --------------------- | ----------------------------------------------------------------- |
| Container     | `OLLAMA_MODE=container` (default) | Linux/Windows; CPU-only or NVIDIA GPU via `OLLAMA_GPU=nvidia`.    |
| Host-install  | `OLLAMA_MODE=host`    | macOS with Metal GPU — Metal does not pass through into Podman.   |

### Container mode

```bash
# spring.env
LanguageModel__Ollama__Enabled=true
OLLAMA_MODE=container
OLLAMA_DEFAULT_MODEL=llama3.2:3b
# OLLAMA_GPU=nvidia   # Linux/WSL2 + nvidia-container-toolkit
```

`deploy.sh up` launches `docker.io/ollama/ollama:latest` as `spring-ollama`,
attaches volume `spring-ollama-data` at `/root/.ollama`, publishes port
`11434`, and best-effort `ollama pull`s the default model once the server
is up.

### Host mode (macOS GPU)

```bash
# on the host
brew install ollama
ollama serve &

# spring.env
LanguageModel__Ollama__Enabled=true
OLLAMA_MODE=host
LanguageModel__Ollama__BaseUrl=http://host.containers.internal:11434
```

`deploy.sh up` skips `spring-ollama` and the platform talks to the host
server through Podman's `host.containers.internal` DNS name.

See [`docs/developer/local-ai-ollama.md`](../docs/developer/local-ai-ollama.md)
for full details (troubleshooting, cloud-deployment patterns, and the GPU
feasibility matrix across Mac/Windows/Linux).

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
