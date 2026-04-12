# Dapr components and configuration

This directory holds the Dapr component and configuration YAML that the
Spring Voyage sidecars load at runtime. Two profiles live here side-by-side:

```
dapr/
├── components/
│   ├── local/           # localhost Redis + env-var secret store (dev)
│   │   ├── statestore.yaml
│   │   ├── pubsub.yaml
│   │   └── secretstore.yaml
│   └── production/      # Podman-hosted Postgres + Redis (deployment/)
│       ├── statestore.yaml
│       ├── pubsub.yaml
│       └── secretstore.yaml
└── config/
    ├── local.yaml       # Dapr Configuration (tracing, features) — dev
    └── production.yaml  # Dapr Configuration (tracing, features) — prod
```

Every component uses `apiVersion: dapr.io/v1alpha1` and `kind: Component`.
Configurations use `kind: Configuration`. Both profiles expose the same
component names (`statestore`, `pubsub`, `secretstore`), so application code
references them identically regardless of environment.

## Local development (`dapr run`)

```bash
dapr run \
  --app-id spring-worker \
  --app-port 5001 \
  --dapr-http-port 3500 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001
```

The local secret store is `secretstores.local.env`, so any `secretKeyRef`
resolves against the process environment. Export values before running:

```bash
export POSTGRES_PASSWORD=dev-password
export REDIS_PASSWORD=
```

See `docs/developer/setup.md` for the full local-dev loop (API + Worker +
Web dashboard).

## Production (Podman deployment)

The image built by `deployment/Dockerfile` bundles the entire `dapr/` tree
under `/dapr/`. Point sidecars at the production profile:

```bash
daprd \
  --app-id spring-worker \
  --resources-path /dapr/components/production \
  --config /dapr/config/production.yaml \
  ...
```

When the platform launches delegated agent containers, the paired Dapr
sidecar is started by `DaprSidecarManager`. Set
`ContainerRuntime__DaprComponentsPath` to the host-side directory you want
mounted into the sidecar container — the manager forwards it to `daprd` via
`-v <path>:/components` + `--resources-path /components`. On the VPS this
will typically be the repo-mounted `dapr/components/production` directory.

### Secrets

`dapr/components/production/secretstore.yaml` is `secretstores.local.env`.
Credentials never appear in git or in container images — they come from the
process environment of each sidecar. The Podman stack loads them from
`deployment/spring.env`:

| Secret key                            | Purpose                                    |
| ------------------------------------- | ------------------------------------------ |
| `SPRING_POSTGRES_CONNECTION_STRING`   | Full Npgsql connection string to Postgres. |
| `REDIS_PASSWORD`                      | Redis AUTH password (leave empty to skip). |

Add new secrets to `deployment/spring.env.example`, document them in
`deployment/README.md`, and reference them from the relevant component
YAML via `secretKeyRef`. For cloud-grade secret management (Azure Key
Vault, HashiCorp Vault, Kubernetes Secrets) swap `secretstore.yaml` for
the corresponding Dapr secret store — the rest of the profile is
unchanged as long as the component is named `secretstore`.

## Validating components

Dapr parses each YAML at sidecar startup; a malformed file makes the
sidecar exit. Lint with your editor or:

```bash
python3 -c "import yaml, sys; [yaml.safe_load(open(p)) for p in sys.argv[1:]]" \
  dapr/components/local/*.yaml \
  dapr/components/production/*.yaml \
  dapr/config/*.yaml
```
