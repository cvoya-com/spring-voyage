# Managing Secrets

This guide covers day-to-day secret management: storing API tokens and credentials, rotating them safely, pruning old versions, and choosing scope. Envelope encryption internals and the audit decorator pattern live in [OSS Secret Store](../../developer/secret-store.md) and [Secret Audit Logging](../../developer/secret-audit.md).

For the full architectural picture see [Security architecture — Secrets Stack](../../architecture/security.md#secrets-stack) and [Security architecture — Config tiers](../../architecture/security.md#config-tiers).

## The three config tiers (#615)

Spring Voyage distinguishes three tiers of configuration so credentials live where they can be rotated, scoped, and audited independently:

| Tier | Location | Examples | Who sets it |
|------|----------|----------|-------------|
| **Tier 1 — platform-deploy** | Env / `spring.env` / startup config | DB connection, Dapr wiring, `GitHub__AppId` / `GitHub__PrivateKeyPem` / `GitHub__WebhookSecret` (identity of the Spring Voyage instance itself as a GitHub App — every deployment registers its own App, see [Register your GitHub App](github-app-setup.md)) | Ops team at deploy time |
| **Tier 2 — tenant-default** | Database (`SecretScope.Tenant`) | LLM provider API keys (`anthropic-api-key`, `openai-api-key`, `google-api-key`), tenant-wide observability / monitoring tokens | Tenant admin post-deploy |
| **Tier 3 — unit-override** | Database (`SecretScope.Unit`) | Per-unit variants of any tier-2 credential (a unit that calls a different Anthropic account than the tenant default) | Unit operator |

LLM provider credentials explicitly belong to **tier 2**, not tier 1 — they are workload credentials, not deployment identity. The platform's tier-2 resolver ([`ILlmCredentialResolver`](../../../src/Cvoya.Spring.Core/Execution/ILlmCredentialResolver.cs)) reads them through the chain:

1. **Unit-scoped secret** (if the caller has a unit in context)
2. **Tenant-scoped secret** (the inheritance fall-through from unit scope, or the direct read when there is no unit context — e.g. the unit-create wizard fetching the model catalog)

When nothing resolves, the platform fails cleanly — the operator-facing error names the exact secret the resolver looked for ("no LLM credentials configured for this unit; set via `spring secret --scope unit` or configure tenant defaults at `spring secret --scope tenant create <name>` / the portal's Tenant defaults panel"). There is no environment-variable fallback: credentials must be set at tenant or unit scope. The private cloud build layers its own per-tenant resolver on top.

## Concepts at a glance

A secret is a named, scoped, versioned reference to sensitive material:

- **Name** — case-sensitive operator-chosen identifier (`openai-api-key`, `github-app-key`).
- **Scope** — `Unit`, `Tenant`, or `Platform`. Determines ownership and resolver visibility.
- **Version** — monotonically-increasing integer. Rotation appends; prior versions survive until pruned.
- **Origin** — `PlatformOwned` (platform holds the ciphertext) or `ExternalReference` (pointer to externally-managed material, e.g. an Azure Key Vault secret id).

Plaintext enters exactly once (on `create` or `rotate`) and is never returned in any response, list entry, or log. The only path that surfaces plaintext is `ISecretResolver.ResolveAsync`, which runs server-side and is consumed by agents, connectors, and tool launchers.

> **Startup-time credentials live outside this registry.** The GitHub App `GitHub__AppId` / `GitHub__PrivateKeyPem` pair is sourced from `spring.env` before the registry is reachable. Each deployment registers its own GitHub App; see [Register your GitHub App](github-app-setup.md). If missing, the GitHub connector boots disabled; if malformed, the host refuses to start. Everything on this page covers runtime secrets the platform manages — the startup bootstrap pair is out of scope.

## Surfaces

- **CLI — `spring secret`.** Seven verbs: `create`, `list`, `get`, `rotate`, `versions`, `prune`, `delete`. Every scope is reachable with `--scope <scope> [--unit <name>]`. Accepts `--output json`. This guide uses the CLI as the primary example.
- **HTTP API.** Scope-keyed endpoints under `/api/v1/units/{id}/secrets`, `/api/v1/tenant/secrets`, `/api/v1/platform/secrets`. Useful from CI runners or foreign services — one example is at the end of this guide.
- **Portal.** Two surfaces: the **Tenant defaults** panel (set / rotate tier-2 LLM credentials inherited by every unit — recommended first-run step) and the unit's **Secrets** tab (list, create, delete unit-scoped secrets with an **inherited from tenant** / **set on unit** badge). Rotation, version listing, and pruning are CLI-only.

Authenticate the CLI: `spring auth token create --name "<label>"` — the token is persisted to `~/.spring/config.json`.

## Choosing a scope

| Scope | Owner key | Use for |
|-------|-----------|---------|
| `Unit` | Unit name | Credentials belonging to one unit — connector tokens, per-unit LLM key |
| `Tenant` | Tenant id | Credentials shared across most units — tenant-wide observability token |
| `Platform` | `platform` (literal) | Infra-owned keys — platform signing keys, webhook shared secrets |

`Unit` secrets are visible only within that unit. `Tenant` secrets are visible to every unit that asks by name (see [inheritance](#environment-specific-secrets-and-tenant-inheritance)). `Platform` is admin-only — units do not fall through to it.

**Pick the narrowest scope that works.** Promote to `Tenant` only when the same credential is genuinely shared; reserve `Platform` for the platform's own keys.

## Storing secrets

### Via CLI

```bash
# Pass-through (platform holds ciphertext)
spring secret create \
  --scope unit --unit engineering-team \
  openai-api-key --value "sk-live-..."

# From file (useful for PEM keys with newlines)
spring secret create \
  --scope unit --unit engineering-team \
  github-app-private-key --from-file ./github-app.pem

# External reference (pointer to a customer-owned vault)
spring secret create \
  --scope unit --unit engineering-team \
  github-app-key --external-store-key "kv://prod/github-app-privatekey"

# Tenant-scoped (shared by every unit that reads it by name)
spring secret create --scope tenant observability-token --value "..."

# Platform-scoped (infra-owned; requires platform-admin authorization)
spring secret create --scope platform system-webhook-signing-key --value "..."
```

The CLI prints name, scope, and timestamp on success — never the plaintext or backing key. Supply exactly one of `--value`, `--from-file`, or `--external-store-key`. The OSS default (`AllowAllSecretAccessPolicy`) permits all writes; production deployments use `ISecretAccessPolicy` to enforce RBAC.

## Listing and inspecting

```bash
# List (name, scope, createdAt — no plaintext or store key)
spring secret list --scope unit --unit engineering-team
spring secret list --scope tenant
spring secret list --scope platform

# Inspect one secret (metadata only; never plaintext)
spring secret get --scope unit --unit engineering-team openai-api-key
spring secret get --scope unit --unit engineering-team openai-api-key --version 1

# All retained versions
spring secret versions --scope unit --unit engineering-team openai-api-key
```

Each version row reports `version`, `origin`, `createdAt`, and `isCurrent`. The current version is resolved by default; callers can pin older versions by number.

## Rotating

`spring secret rotate` appends a new version without destroying prior versions. The registry atomically writes the replacement and echoes the new version number.

```bash
spring secret rotate \
  --scope unit --unit engineering-team \
  openai-api-key --value "sk-live-NEW..."
# → Secret 'openai-api-key' rotated (Unit); new version = 2.
```

Use `--output json` to capture the `version` field in scripts. Rotation can flip origin (`ExternalReference` → `PlatformOwned` and vice versa); the registry records the transition for audit decorators.

Prior versions remain resolvable by version pin until pruned. If a pinned version does not exist the resolver returns `NotFound` — it never silently substitutes another version. See [Security architecture — Multi-version coexistence](../../architecture/security.md#multi-version-coexistence-and-rotation).

## Pruning old versions

```bash
# Keep the 2 most-recent versions; reclaim backing-store slots for dropped PlatformOwned versions
spring secret prune \
  --scope unit --unit engineering-team \
  openai-api-key --keep 2
# → keep=2, versionsRemoved=3
```

`--keep` must be `>= 1`; the current version is always retained. `ExternalReference` pruning never touches the external store. A `Secrets:VersionRetention` knob is reserved for a future scheduler; until then, prune explicitly.

## Deleting

```bash
spring secret delete --scope unit --unit engineering-team openai-api-key
```

Removes every version. Platform-owned backing slots are reclaimed; external-reference pointers leave the external store untouched. A partial store-side failure leaves the registry row intact so the operation is safe to retry.

## Environment-specific secrets and tenant inheritance

Spring Voyage has no first-class "environments" — production, staging, and dev are separate tenants (cloud) or separate deployments. Within a tenant, the only automatic cross-scope composition is **unit → tenant inheritance**:

1. When a caller asks for `(Unit, engineering-team, some-name)` and no unit-scoped row exists, the resolver falls through to `(Tenant, <tenantId>, some-name)`.
2. Access policy is checked at both scopes; a denial at either returns `NotFound`.
3. Unit-scoped entries always win — a unit overrides a tenant secret by registering the same name.
4. Fall-through is gated by `Secrets:InheritTenantFromUnit` (default `true`).
5. Tenant → Platform does **not** chain. Units cannot probe platform keys by name.

See [ADR 0003](../../decisions/0003-secret-inheritance-unit-to-tenant.md) for full rationale.

### Tenant default with a unit override

```bash
# Tenant default — every unit resolves "observability-token" by name
spring secret create --scope tenant observability-token --value "tenant-default-..."

# Unit override — wins for research-team; everyone else reads the tenant default
spring secret create --scope unit --unit research-team \
  observability-token --value "research-team-override-..."
```

### LLM credentials (tier-2 defaults + per-unit overrides)

The tier-2 resolver looks up `anthropic-api-key`, `openai-api-key`, and `google-api-key` by name. Match these names exactly.

```bash
# Tenant default
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."

# Per-unit override (bills against a different Anthropic account)
spring secret create --scope unit --unit research-team \
  anthropic-api-key --value "sk-ant-research-..."
```

Via portal: **Tenant defaults** panel for the tenant-wide key; unit's **Secrets** tab for per-unit overrides. The Secrets tab shows an "inherited from tenant" badge for transitively inherited secrets.

## Supplying a credential during unit creation

Both the portal wizard (`/units/create`) and `spring unit create` accept an LLM API key inline — the lowest-friction onboarding path.

### Via Portal

1. Pick an execution tool on Step 1. The wizard derives the required provider (Claude Code → Anthropic, Codex → OpenAI, Gemini → Google, Ollama → none).
2. If the credential is **not configured**, an inline input appears with a **"Save as tenant default"** checkbox. Unticked = unit-scoped secret; ticked = tenant-scoped secret (all future units inherit it).
3. If a tenant default already exists, an **Override** button appears. Use it to set a per-unit override or rotate the tenant default.
4. On blur the wizard validates the key against the provider's API. On success the Model dropdown appears seeded from the account's catalog.

The wizard never shows existing plaintext; Override clears the input.

### Via CLI

```bash
# Unit-scoped override
spring unit create research-team \
  --tool claude-code \
  --api-key-from-file ~/.secrets/anthropic-research.txt

# Tenant default (all subsequent units inherit)
spring unit create platform \
  --tool claude-code \
  --api-key "sk-ant-xyz" \
  --save-as-tenant-default

# Rejected — Ollama needs no API key
spring unit create local-dev --tool dapr-agent --provider ollama --api-key "anything"
# → "--api-key / --api-key-from-file is only valid for tools that need an LLM API key ..."
```

See [CLI & Web § Inline credential flags](../../architecture/cli-and-web.md#inline-credential-flags-626) for the full rejection matrix.

## Per-agent secrets

The OSS contract stops at unit scope. There is no `SecretScope.Agent`; every agent inside a unit sees the unit's full secret set.

For per-agent isolation today: spin up a single-agent unit for the agent that needs its own keys. This reuses the unit as the isolation boundary.

See [ADR 0004 — Per-agent secrets](../../decisions/0004-per-agent-secrets.md) for the rationale and revisit criteria.

## Advanced: HTTP API

Use the HTTP API from CI runners or environments without the CLI installed.

```bash
# Equivalent to: spring secret create --scope unit --unit engineering-team openai-api-key --value sk-live-...
curl -sS -X POST "$SPRING_API_URL/api/v1/units/engineering-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "openai-api-key", "value": "sk-live-..."}'
```

Other verbs follow the same pattern: `GET` (list / versions), `POST` (create), `PUT` (rotate), `POST /prune?keep=<n>`, `DELETE`. See `src/Cvoya.Spring.Host.Api/Endpoints/SecretEndpoints.cs` for definitions.

## Best practices

- **Name by consumer, not provider.** `github-app-key` beats `app-8743-private-key`; the consumer's code stays stable across vendor changes.
- **Match names across scopes.** A unit override must use the same name as the tenant default it shadows; mismatches silently fall through.
- **Prune ahead of your rotation cadence.** Match `--keep` to how far back a pinned caller might legitimately still be resolving.
- **Prefer `--from-file` over `--value`.** Reading from a `tmpfs`-backed temp file keeps the plaintext out of shell history.
- **Pick the narrowest scope.** Widening to tenant scope adds an access-policy probe on every unit resolve; don't pay that cost speculatively.
- **Don't hand-edit the Dapr state store.** Backing slots use AES-GCM with `"{tenantId}:{storeKey}"` as associated data; a transplanted ciphertext breaks authentication.
- **Never use `Secrets:AllowEphemeralDevKey = true` in production.** Restarts render all written envelopes unreadable. Use a durable `SPRING_SECRETS_AES_KEY` or `Secrets:AesKeyFile` in staging and production — see [OSS Secret Store](../../developer/secret-store.md).
- **Use the audit decorator for "who read what."** The resolver emits a `SecretResolvePath` (`Direct`, `InheritedFromTenant`, `NotFound`) per resolve. See [Secret Audit Logging](../../developer/secret-audit.md).
