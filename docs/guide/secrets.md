# Managing Secrets

This guide covers day-to-day secret management for operators: storing API tokens and other credentials, rotating them safely, pruning old versions, and deciding which scope a secret belongs to. It does not cover envelope encryption internals or the decorator-based audit pattern — those live in [OSS Secret Store](../developer/secret-store.md) and [Secret Audit Logging](../developer/secret-audit.md) respectively.

For the full architectural picture — how the registry, store, resolver, and access policy compose — see [Security architecture — Secrets Stack](../architecture/security.md#secrets-stack), and [Security architecture — Config tiers](../architecture/security.md#config-tiers) for the companion model that describes which layer of the platform owns which kind of credential.

## The three config tiers (#615)

Spring Voyage distinguishes three tiers of configuration so credentials live where they can be rotated, scoped, and audited independently:

| Tier | Location | Examples | Who sets it |
|------|----------|----------|-------------|
| **Tier 1 — platform-deploy** | Env / `spring.env` / startup config | DB connection, Dapr wiring, `GitHub__AppId` / `GitHub__PrivateKeyPem` / `GitHub__WebhookSecret` (identity of the Spring Voyage instance itself as a GitHub App) | Ops team at deploy time |
| **Tier 2 — tenant-default** | Database (`SecretScope.Tenant`) | LLM provider API keys (`anthropic-api-key`, `openai-api-key`, `google-api-key`), tenant-wide observability / monitoring tokens | Tenant admin post-deploy |
| **Tier 3 — unit-override** | Database (`SecretScope.Unit`) | Per-unit variants of any tier-2 credential (a unit that calls a different Anthropic account than the tenant default) | Unit operator |

LLM provider credentials explicitly belong to **tier 2**, not tier 1 — they are workload credentials, not deployment identity. The platform's tier-2 resolver ([`ILlmCredentialResolver`](../../src/Cvoya.Spring.Core/Execution/ILlmCredentialResolver.cs)) reads them through the chain:

1. **Unit-scoped secret** (if the caller has a unit in context)
2. **Tenant-scoped secret** (the inheritance fall-through from unit scope, or the direct read when there is no unit context — e.g. the unit-create wizard fetching the model catalog)

When nothing resolves, the platform fails cleanly — the operator-facing error names the exact secret the resolver looked for ("no LLM credentials configured for this unit; set via `spring secret --scope unit` or configure tenant defaults at `spring secret --scope tenant create <name>` / the portal's Tenant defaults panel"). There is no environment-variable fallback: credentials must be set at tenant or unit scope. The private cloud build layers its own per-tenant resolver on top.

## Concepts at a glance

A secret on Spring Voyage is a named, scoped, versioned reference to a piece of sensitive material:

- **Name** — a case-sensitive identifier chosen by the operator (`github-app-key`, `openai-api-key`, `slack-signing-secret`).
- **Scope** — one of `Unit`, `Tenant`, or `Platform`. Determines which owner the secret belongs to and who may resolve it.
- **Version** — a monotonically-increasing integer assigned by the registry. Every rotation appends a new version; prior versions remain resolvable until explicitly pruned.
- **Origin** — either `PlatformOwned` (the platform wrote the plaintext through `ISecretStore.WriteAsync` and owns the opaque backing slot) or `ExternalReference` (the operator supplied a key pointing at externally-managed material — for example, an Azure Key Vault secret id).

Plaintext enters the system exactly once — on a `POST` or `PUT` to a secret endpoint, or on `spring secret create`/`rotate` with `--value`/`--from-file` — and is never returned on any response, list entry, or log line. The only path that surfaces a plaintext value is `ISecretResolver.ResolveAsync`, which runs server-side and is consumed by agents, connectors, and tool launchers.

> **Startup-time configuration credentials live outside this registry.** A small set of credentials has to be available *before* the platform can talk to its secret registry — notably the GitHub App `GitHub__AppId` / `GitHub__PrivateKeyPem` pair that powers the GitHub connector itself. Those are sourced from environment variables at host startup (or a file mount the platform dereferences transparently — see [Deployment guide § Optional — connector credentials](deployment.md#optional--connector-credentials)), validated at connector-init time, and never enter the registry. If they are missing the GitHub connector boots in a disabled-with-reason state; if they are malformed the host refuses to start with a targeted error (#609). Everything on this page covers **runtime** secrets the platform manages on the operator's behalf — the startup-time bootstrap pair is deliberately out of scope.

## Surfaces

Three operator surfaces ship today:

- **CLI — `spring secret` (#432).** Seven verbs: `create`, `list`, `get`, `rotate`, `versions`, `prune`, `delete`. Every scope (`unit` / `tenant` / `platform`) is reachable through the same flag shape (`--scope <scope> [--unit <name>]`), and every verb accepts `--output json` for scripted consumers. This guide uses the CLI as the primary example throughout.
- **HTTP API.** Scope-keyed endpoints under `/api/v1/units/{id}/secrets`, `/api/v1/tenant/secrets`, and `/api/v1/platform/secrets`. Useful when integrating from a runtime that does not have the CLI (CI runners, foreign services) — one advanced example is retained at the end of this guide.
- **Web portal.** Two surfaces:
  - The **Tenant defaults** panel on the `/settings` hub (#615; the in-shell settings drawer it grew out of was retired in the v2 IA refactor under #815) — set / rotate / clear the tier-2 LLM credentials every unit inherits (`anthropic-api-key`, `openai-api-key`, `google-api-key`). This is the recommended first-run step after `./deploy.sh up`.
  - The unit detail page's **Secrets** tab — list, create, and delete unit-scoped secrets. Rows carry an **inherited from tenant** / **set on unit** badge so the active tier is always visible. Rotation, version listing, and pruning live on the CLI and HTTP API; the portal stays narrowly scoped to the most common operator flows.

Authenticate the CLI with an API token issued by `spring auth token create --name "<label>"`; the token is persisted to `~/.spring/config.json` and reused on every subsequent invocation.

## Choosing a scope

| Scope      | Owner key             | Use for                                                                                 |
| ---------- | --------------------- | --------------------------------------------------------------------------------------- |
| `Unit`     | Unit name             | Credentials that belong to one unit — its connector tokens, its LLM provider key.       |
| `Tenant`   | Tenant id (cloud)     | Credentials shared by most units in the tenant — a tenant-wide observability token.     |
| `Platform` | `platform` (literal)  | Infra-owned keys — platform signing keys, platform-wide webhook shared secrets.         |

By default, a secret registered at `Unit` scope is visible only to agents, connectors, and tools running inside that unit. A secret at `Tenant` scope is visible to every unit that asks for it by name (see [inheritance](#environment-specific-secrets-and-tenant-inheritance) below). `Platform` scope is an admin-only boundary — units do **not** fall through to it.

Pick the narrowest scope that works. Prefer `Unit` for anything a single unit owns; promote to `Tenant` only when the same credential is genuinely shared across many units; reserve `Platform` for the platform's own operational keys.

## Storing secrets

### Unit-scoped secret (pass-through write)

Pass-through writes hand the plaintext to the platform, which encrypts it at rest and records an opaque store key in the registry. Use this shape for API tokens and credentials you want the platform to own end-to-end.

```bash
spring secret create \
  --scope unit \
  --unit engineering-team \
  openai-api-key \
  --value "sk-live-..."
```

Or read the value from a file (useful for keys that contain newlines, such as PEM-encoded private keys):

```bash
spring secret create \
  --scope unit \
  --unit engineering-team \
  github-app-private-key \
  --from-file ./github-app.pem
```

The CLI prints a confirmation with the name, scope, and creation timestamp — **never** the plaintext or the backing store key. Both are intentionally asymmetric: plaintext flows in, metadata flows out.

### Unit-scoped secret bound to an external reference

When the actual secret material lives in a customer-owned vault, use `--external-store-key` instead of `--value` / `--from-file`. The platform records the pointer; the backing slot is never mutated by Spring Voyage (so a delete here can never destroy a customer-owned secret).

```bash
spring secret create \
  --scope unit \
  --unit engineering-team \
  github-app-key \
  --external-store-key "kv://prod/github-app-privatekey"
```

The CLI rejects invocations that supply more than one (or none) of `--value` / `--from-file` / `--external-store-key` with a clear error. Pass-through writes can be globally disabled for a deployment via `Secrets:AllowPassThroughWrites = false`, and external-reference writes via `Secrets:AllowExternalReferenceWrites = false`; both are permitted by default.

### Tenant-scoped and platform-scoped

Swap the `--scope` flag; the rest of the invocation is identical. `--unit` is not meaningful for these scopes and is ignored.

```bash
# Tenant-scoped: shared across every unit in the tenant that reads it by name.
spring secret create \
  --scope tenant \
  observability-token \
  --value "..."

# Platform-scoped: infra-owned keys. Requires platform-admin authorization.
spring secret create \
  --scope platform \
  system-webhook-signing-key \
  --value "..."
```

The private cloud deployment enforces a real RBAC model on all three scopes via the `ISecretAccessPolicy` extension seam. The OSS default — `AllowAllSecretAccessPolicy` — is intended only for local development.

## Listing and inspecting

List every secret registered for a unit, tenant, or platform:

```bash
spring secret list --scope unit --unit engineering-team
spring secret list --scope tenant
spring secret list --scope platform
```

The default output is an aligned table with `name`, `scope`, and `createdAt`; pipe `--output json` when you want to consume the response from a script. It deliberately does not expose the origin, version count, or store key — those details surface only through the `get` / `versions` verbs below.

Inspect a single secret — its current version plus the total number of retained versions:

```bash
spring secret get --scope unit --unit engineering-team openai-api-key
```

Pin the lookup to a specific version number with `--version <n>`:

```bash
spring secret get --scope unit --unit engineering-team openai-api-key --version 1
```

`get` never returns plaintext — it only surfaces metadata (name, scope, version number, origin, creation time, `isCurrent` flag). Plaintext is only accessible via the server-side resolver that agents and connectors use.

List every retained version for a single secret:

```bash
spring secret versions --scope unit --unit engineering-team openai-api-key
```

Each row reports its `version`, `origin` (`PlatformOwned` or `ExternalReference`), `createdAt`, and `isCurrent` flag. The current version is always the one resolved unless a caller explicitly pins an older version.

## Rotating

`spring secret rotate` appends a new version of an existing secret without destroying the prior versions. The registry atomically writes the replacement (for pass-through) or records the new pointer (for external references), then assigns the next integer version number and echoes it.

```bash
# Pass-through rotation: write the new plaintext.
spring secret rotate \
  --scope unit \
  --unit engineering-team \
  openai-api-key \
  --value "sk-live-NEW..."
```

The CLI prints the new version number (`Secret 'openai-api-key' rotated (Unit); new version = 2.`), and the `--output json` shape carries the same `version` field that CI pipelines and scripts can pin to for subsequent resolves. Prior versions remain resolvable by version pin until they are pruned — this is the "multi-version coexistence" model introduced in wave 7 A5; see [Security architecture — Multi-version coexistence and rotation](../architecture/security.md#multi-version-coexistence-and-rotation) for the full contract.

Rotation can flip the origin: a secret that was originally registered as `ExternalReference` can be rotated to a new `--value` (platform-owned), and vice versa. The registry records the origin transition in the `SecretRotation` summary that audit-log decorators observe — see [Secret Audit Logging](../developer/secret-audit.md) for what decorators can see without touching the inner call.

### Pinning a specific version

Server-side resolvers accept an explicit version pin through `ISecretResolver.ResolveWithPathAsync`. A caller asking for `(Unit, engineering-team, openai-api-key, v=1)` after a rotation to `v=2` still resolves `v=1` as long as it has not been pruned. If the pinned version does not exist — whether because it was never created or it was already pruned — the resolver returns `NotFound`, never silently substitutes a different version. This guarantee is load-bearing for consumers that need to coordinate across a rotation window.

## Pruning old versions

Retention is operator-driven today: pick a `--keep` count and prune. The current version is always retained (regardless of `--keep`), and `--keep` must be `>= 1`.

```bash
# Keep only the 2 most-recent versions; reclaim backing-store slots for
# platform-owned versions that get dropped.
spring secret prune \
  --scope unit \
  --unit engineering-team \
  openai-api-key \
  --keep 2
```

The CLI prints `keep={N}, versionsRemoved={M}` (and the same shape under `--output json`). For each pruned `PlatformOwned` version the platform also deletes the backing store slot; `ExternalReference` versions never touch the external store. A `Secrets:VersionRetention` configuration knob is documentary today — a scheduler will consume it in a future wave; until then, prune explicitly.

## Deleting

`spring secret delete` removes every version of a secret. Platform-owned versions have their backing store slots reclaimed; external-reference versions leave the external store untouched (deleting a Spring Voyage pointer never destroys a customer-owned secret). A delete that fails mid-way on the store side leaves the registry row intact so the operation is safe to retry.

```bash
spring secret delete \
  --scope unit \
  --unit engineering-team \
  openai-api-key
```

## Environment-specific secrets and tenant inheritance

Spring Voyage does not have a first-class notion of "environments" — production, staging, and dev are modelled by running separate tenants (cloud) or separate deployments. Within a tenant, the only automatic cross-scope composition is **unit → tenant inheritance**:

1. When a caller asks for `(Unit, engineering-team, some-name)` and no unit-scoped row exists, the resolver transparently falls through to `(Tenant, <tenantId>, some-name)`.
2. The access policy is consulted at **both** the unit scope and the tenant scope; a denial at either boundary returns `NotFound` rather than a silently-masked tenant value.
3. Unit-scoped entries always win when they exist — so a unit can override a tenant-wide secret by registering its own entry with the same name.
4. The fall-through is gated by `Secrets:InheritTenantFromUnit` (default `true`). Set it to `false` for strict-isolation deployments where tenant and unit scopes must stay separate.
5. Tenant → Platform does **not** chain. Platform is an admin-only boundary; a compromised unit cannot probe platform keys by name.

See [ADR 0003 — Secret inheritance semantics (Unit → Tenant)](../decisions/0003-secret-inheritance-unit-to-tenant.md) for the full rationale, rejected alternatives, and revisit criteria.

### Worked pattern: tenant default with a unit override

```bash
# Tenant-wide default: every unit can resolve "observability-token" by name.
spring secret create \
  --scope tenant \
  observability-token \
  --value "tenant-default-..."

# One unit needs a different token (e.g. a dedicated tracing endpoint).
# The unit-scoped row wins for that unit; everyone else still reads the tenant default.
spring secret create \
  --scope unit \
  --unit research-team \
  observability-token \
  --value "research-team-override-..."
```

### Worked pattern: LLM credentials (tier-2 defaults + per-unit overrides)

The tier-2 resolver ([`ILlmCredentialResolver`](../../src/Cvoya.Spring.Core/Execution/ILlmCredentialResolver.cs)) looks up canonical secret names per provider — `anthropic-api-key` for Claude, `openai-api-key` for OpenAI, `google-api-key` for Google / Gemini. Match those names when you set the secrets so the resolver finds them.

```bash
# Tenant default: one Anthropic key for every unit in the tenant.
spring secret create \
  --scope tenant \
  anthropic-api-key \
  --value "sk-ant-..."

# One unit (e.g. the research team) bills against a different Anthropic account.
# The override is read-only from the rest of the tenant.
spring secret create \
  --scope unit \
  --unit research-team \
  anthropic-api-key \
  --value "sk-ant-research-..."
```

The same flow is available from the portal: open the Settings drawer and use the **Tenant defaults** panel to set the tenant-wide key, then use the unit's **Secrets** tab to register a same-name override. The **Secrets** tab renders an "inherited from tenant" badge for every name the unit picks up transitively so operators can see at a glance which tier is active.

## Supplying a credential during unit creation

The unit-creation wizard at `/units/create` and the `spring unit create` / `spring unit create-from-template` CLI verbs both accept an LLM API key inline (#626). This is the least-friction onboarding path — a new operator can stand up their first unit without detouring through the Settings drawer first.

### Portal

1. On Step 1, pick an execution tool. The wizard derives which provider's API key is needed (Claude Code → Anthropic, Codex → OpenAI, Gemini → Google, Dapr Agent → the selected provider, Ollama → none).
2. If the probe reports the credential as **not configured**, the wizard renders an inline input with a show/hide toggle and a **"Save as tenant default"** checkbox.
   - Checkbox **unticked** (default) → the key is written as a unit-scoped `<provider>-api-key` secret after the unit is created. No other unit picks it up.
   - Checkbox **ticked** → the key is written as a tenant-scoped secret before the unit is created, so every future unit in the tenant inherits it. Pick this when you want one key to drive the whole tenant.
3. If the probe reports the credential as **inherited from tenant default**, the wizard shows an **Override** button. Clicking Override opens the same input — use it to set a per-unit override (toggle off) or to rotate the tenant default itself (toggle on).
4. As soon as the operator finishes entering the key (the input loses focus), the wizard posts it to `POST /api/v1/system/credentials/{provider}/validate` (#655). That endpoint performs a lightweight read-only call against the provider's own API (`GET /v1/models` for Anthropic/OpenAI, `GET /v1beta/models` for Google). On success the Model dropdown appears on the same step, seeded from the returned list so operators pick from what their account actually supports. On failure the error message is surfaced inline under the credential input and the Model dropdown stays hidden. The Override flow runs the same validation, so a per-unit override can reveal a different model list than the tenant default when the override's key has access to different models. Editing the key clears the verdict so the next blur re-validates. If the operator pastes a key and clicks Next before the blur-driven validation completes, the Next button waits for the verdict before advancing.

The wizard **never shows the existing plaintext** — Override clears the input so you type a replacement rather than editing the stored value. Both the probe and the validate endpoints are key-free by construction; see [`docs/architecture/security.md`](../architecture/security.md).

### CLI

```bash
# Unit-scoped override — the key is written as a per-unit secret
# (POST /api/v1/units/{id}/secrets) after the unit exists.
spring unit create research-team \
  --tool claude-code \
  --api-key-from-file ~/.secrets/anthropic-research.txt

# Tenant default — the key is written as a tenant-scoped secret
# (POST /api/v1/tenant/secrets) before the unit is created, so
# every subsequent unit inherits it unless it registers an override.
spring unit create platform \
  --tool claude-code \
  --api-key "sk-ant-xyz" \
  --save-as-tenant-default

# Rejected — Ollama is local (no API key).
spring unit create local-dev \
  --tool dapr-agent --provider ollama \
  --api-key "anything"
# → "--api-key / --api-key-from-file is only valid for tools that need an LLM API key ..."
```

See [CLI & Web §Inline credential flags (#626)](../architecture/cli-and-web.md#inline-credential-flags-626) for the full rejection matrix.

## Per-agent secrets

The OSS contract stops at unit scope. There is no `SecretScope.Agent`, and the resolver has no agent-aware logic: every agent inside a unit sees the unit's full secret set (and any tenant secrets the unit inherits under the rules above).

Operators who need per-agent isolation today use the unit boundary itself — spin up a single-agent unit for the agent that needs its own keys, and use tenant-scoped secrets only where cross-unit sharing is intentional. This reuses the unit as the isolation primitive instead of inventing a new one.

The full rationale — why an `Agent` scope, an agent-level ACL, and doing nothing were considered, and why "do nothing" was the right call for wave 2 — is captured in [ADR 0004 — Per-agent secrets](../decisions/0004-per-agent-secrets.md). That record also lists the concrete triggers that would cause us to revisit.

## Advanced: calling the HTTP API directly

Most operators should reach for the CLI. The HTTP API is retained for integration builders who need the raw request shape — for example, a GitHub Actions workflow that does not have the CLI installed.

```bash
# Raw HTTP create — exactly equivalent to
# `spring secret create --scope unit --unit engineering-team openai-api-key --value sk-live-...`.
curl -sS -X POST "$SPRING_API_URL/api/v1/units/engineering-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "openai-api-key", "value": "sk-live-..."}'
```

Every other lifecycle operation follows the same pattern: `GET` for listing / versions, `POST` for create, `PUT` for rotate, `POST /prune?keep=<n>` for pruning, `DELETE` for deletion. The CLI is a thin wrapper over these endpoints — see `src/Cvoya.Spring.Host.Api/Endpoints/SecretEndpoints.cs` for the canonical definitions.

## Best practices

- **Name secrets by their consumer, not their provider.** `github-app-key` is easier to reason about than `app-8743-private-key`; the consumer's code can hard-code the former and stay stable across vendor changes.
- **Match the name across scopes so inheritance works.** If a tenant-wide `observability-token` exists and a unit later needs to override it, the unit-scoped secret **must** be registered under the same name. Mismatched names silently fall through to the tenant default.
- **Prune ahead of your rotation cadence.** If you rotate monthly and keep `--keep 3`, a secret churns through roughly three months of history. Match the `--keep` count to how far back a pinned caller might legitimately still be resolving.
- **Rotate on fixed cadences for owned secrets; rotate on revocation for external references.** Pass-through secrets the platform owns end-to-end should follow your compliance clock. External-reference secrets rotate when the upstream vault rotates — the platform is just re-pointing the registry, so there's no value in rotating more often.
- **Pick the narrowest scope that works, and promote only when genuinely shared.** Dropping a secret into tenant scope because "it might be useful to another unit" widens the audit surface; every unit resolve now includes a tenant-scope access-policy probe. Let the shared-use case appear before paying that cost.
- **Never paste plaintext into logs or PR descriptions.** The CLI accepts plaintext exactly once on create/rotate; everything else — list responses, rotation responses, version listings — is metadata only. Prefer `--from-file` (piped from a temporary file under `tmpfs`) over `--value` when the plaintext is long-lived, so it never hits shell history.
- **Rely on the audit decorator for "who read what."** The resolver surface exposes a `SecretResolvePath` (`Direct`, `InheritedFromTenant`, `NotFound`) that audit decorators record for every resolve. If your deployment needs "which units read this tenant secret?" the answer is a log query, not a registry denormalisation — see [Secret Audit Logging](../developer/secret-audit.md).
- **Don't hand-edit the Dapr state store.** Backing slots are written through AES-GCM envelope encryption with `"{tenantId}:{storeKey}"` as associated data — a ciphertext cannot be transplanted across tenants or keys. Direct edits break authentication; use the CLI (or API) to rotate or delete.
- **Treat the ephemeral dev key as dev-only.** If `Secrets:AllowEphemeralDevKey = true`, restarts render previously-written envelopes unreadable. Never enable this outside local `dotnet run`; staging and production deployments **must** source a durable key via `SPRING_SECRETS_AES_KEY` or `Secrets:AesKeyFile` (see [OSS Secret Store](../developer/secret-store.md) for the full key-sources table).
