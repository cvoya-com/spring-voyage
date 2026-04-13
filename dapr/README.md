# Dapr components and configuration

This directory holds the Dapr component and configuration YAML that the
Spring Voyage sidecars load at runtime during local development.

```
dapr/
├── components/
│   └── local/           # localhost Redis + env-var secret store (dev)
│       ├── statestore.yaml
│       ├── pubsub.yaml
│       └── secretstore.yaml
└── config/
    └── local.yaml       # Dapr Configuration (tracing, features) — dev
```

Every component uses `apiVersion: dapr.io/v1alpha1` and `kind: Component`.
Configurations use `kind: Configuration`. Application code references
component names (`statestore`, `pubsub`, `secretstore`) identically
regardless of environment.

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

Or use `scripts/dev.sh up` which handles this automatically.

See `docs/developer/setup.md` for the full local-dev loop (API + Worker +
Web dashboard).

## Production profiles

Production Dapr components (PostgreSQL state store, Redis pub/sub with
authentication, etc.) live in the private Spring repository alongside the
deployment scripts. Swap `secretstore.yaml` for Azure Key Vault, HashiCorp
Vault, or Kubernetes Secrets for cloud-grade secret management — the other
components reference the store by name (`secretstore`) so they require no
changes.

## Validating components

Dapr parses each YAML at sidecar startup; a malformed file makes the
sidecar exit. Lint with your editor or:

```bash
python3 -c "import yaml, sys; [yaml.safe_load(open(p)) for p in sys.argv[1:]]" \
  dapr/components/local/*.yaml \
  dapr/config/*.yaml
```
