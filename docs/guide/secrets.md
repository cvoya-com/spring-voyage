# Managing Secrets

> **Heads up — this guide will be rewritten.** There is no `spring secret` CLI verb today, so every lifecycle example below uses the HTTP API via `curl`. Issue [#432](https://github.com/cvoya-com/spring-voyage/issues/432) tracks adding the `spring secret` CLI family and refactoring this guide so the CLI is the primary surface (with at most one or two `curl` examples retained for advanced cases). If you are reading this after #432 has shipped, the CLI version of the guide supersedes the HTTP snippets here.

This guide covers day-to-day secret management for operators: storing API tokens and other credentials, rotating them safely, pruning old versions, and deciding which scope a secret belongs to. It does not cover envelope encryption internals or the decorator-based audit pattern — those live in [OSS Secret Store](../developer/secret-store.md) and [Secret Audit Logging](../developer/secret-audit.md) respectively.

For the full architectural picture — how the registry, store, resolver, and access policy compose — see [Security architecture — Secrets Stack](../architecture/security.md#secrets-stack).

## Concepts at a glance

A secret on Spring Voyage is a named, scoped, versioned reference to a piece of sensitive material:

- **Name** — a case-sensitive identifier chosen by the operator (`github-app-key`, `openai-api-key`, `slack-signing-secret`).
- **Scope** — one of `Unit`, `Tenant`, or `Platform`. Determines which owner the secret belongs to and who may resolve it.
- **Version** — a monotonically-increasing integer assigned by the registry. Every rotation appends a new version; prior versions remain resolvable until explicitly pruned.
- **Origin** — either `PlatformOwned` (the platform wrote the plaintext through `ISecretStore.WriteAsync` and owns the opaque backing slot) or `ExternalReference` (the operator supplied a key pointing at externally-managed material — for example, an Azure Key Vault secret id).

Plaintext enters the system exactly once — on a `POST` or `PUT` to a secret endpoint — and is never returned on any response, list entry, or log line. The only path that surfaces a plaintext value is `ISecretResolver.ResolveAsync`, which runs server-side and is consumed by agents, connectors, and tool launchers.

## Surfaces

Two operator surfaces ship today:

- **HTTP API.** Scope-keyed endpoints under `/api/v1/units/{id}/secrets`, `/api/v1/tenant/secrets`, and `/api/v1/platform/secrets`. Every lifecycle operation — create, list, rotate, list versions, prune, delete — is available here.
- **Web portal.** The unit detail page has a **Secrets** tab that supports listing, creating, and deleting unit-scoped secrets. Rotation, version listing, and pruning are not yet wired into the portal; drive those through the API.

A first-class `spring secret` CLI verb is not yet implemented ([#432](https://github.com/cvoya-com/spring-voyage/issues/432) tracks the work). Until it lands, use `curl` (or your HTTP client of choice) against the endpoints below. When the CLI catches up, the web portal, CLI, and API will all cover the same surface per the platform's UI/CLI parity rule. Authenticate either surface with an API token issued by `spring auth token create --name "<label>"`.

Throughout this guide, commands assume `$SPRING_API_URL` points at the platform endpoint and `$SPRING_TOKEN` holds a current API token; adjust to match your environment.

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
curl -sS -X POST "$SPRING_API_URL/api/v1/units/engineering-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "openai-api-key",
    "value": "sk-live-..."
  }'
```

The response echoes the name, scope, and creation timestamp — **never** the plaintext or the backing store key. Both are intentionally asymmetric: plaintext flows in, metadata flows out.

### Unit-scoped secret bound to an external reference

When the actual secret material lives in a customer-owned vault, supply `externalStoreKey` instead of `value`. The platform records the pointer; the backing slot is never mutated by Spring Voyage (so a delete here can never destroy a customer-owned secret).

```bash
curl -sS -X POST "$SPRING_API_URL/api/v1/units/engineering-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "github-app-key",
    "externalStoreKey": "kv://prod/github-app-privatekey"
  }'
```

The endpoint rejects requests that provide both `value` and `externalStoreKey`, or neither, with `400 Bad Request`. Pass-through writes can be globally disabled for a deployment via `Secrets:AllowPassThroughWrites = false`, and external-reference writes via `Secrets:AllowExternalReferenceWrites = false`; both are permitted by default.

### Tenant-scoped and platform-scoped

Swap the URL segment; the body shape is identical:

```bash
# Tenant-scoped: shared across every unit in the tenant that reads it by name.
curl -sS -X POST "$SPRING_API_URL/api/v1/tenant/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "observability-token", "value": "..."}'

# Platform-scoped: infra-owned keys. Requires platform-admin authorization.
curl -sS -X POST "$SPRING_API_URL/api/v1/platform/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "system-webhook-signing-key", "value": "..."}'
```

The private cloud deployment enforces a real RBAC model on all three scopes via the `ISecretAccessPolicy` extension seam. The OSS default — `AllowAllSecretAccessPolicy` — is intended only for local development.

## Listing and inspecting

List every secret registered for a unit, tenant, or platform:

```bash
curl -sS "$SPRING_API_URL/api/v1/units/engineering-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN"

curl -sS "$SPRING_API_URL/api/v1/tenant/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN"
```

The response carries a list of `{ name, scope, createdAt }` records. It deliberately does not expose the origin, version count, or store key — those details surface only through the per-version endpoint below.

List every retained version for a single secret:

```bash
curl -sS "$SPRING_API_URL/api/v1/units/engineering-team/secrets/openai-api-key/versions" \
  -H "Authorization: Bearer $SPRING_TOKEN"
```

Each entry reports its `version`, `origin` (`PlatformOwned` or `ExternalReference`), `createdAt`, and `isCurrent` flag. The current version is always the one resolved unless a caller explicitly pins an older version.

## Rotating

`PUT` on a secret rotates it by appending a new version. The registry atomically writes the replacement (for pass-through) or records the new pointer (for external references), then assigns the next integer version number and returns it.

```bash
# Pass-through rotation: write the new plaintext.
curl -sS -X PUT "$SPRING_API_URL/api/v1/units/engineering-team/secrets/openai-api-key" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"value": "sk-live-NEW..."}'
```

The response includes the new version number (`{ "name": "openai-api-key", "scope": "Unit", "version": 2 }`), which CI pipelines and scripts can pin to for subsequent resolves. Prior versions remain resolvable by version pin until they are pruned — this is the "multi-version coexistence" model introduced in wave 7 A5; see [Security architecture — Multi-version coexistence and rotation](../architecture/security.md#multi-version-coexistence-and-rotation) for the full contract.

Rotation can flip the origin: a secret that was originally registered as `ExternalReference` can be rotated to a new `value` (platform-owned), and vice versa. The registry records the origin transition in the `SecretRotation` summary that audit-log decorators observe — see [Secret Audit Logging](../developer/secret-audit.md) for what decorators can see without touching the inner call.

### Pinning a specific version

Server-side resolvers accept an explicit version pin through `ISecretResolver.ResolveWithPathAsync`. A caller asking for `(Unit, engineering-team, openai-api-key, v=1)` after a rotation to `v=2` still resolves `v=1` as long as it has not been pruned. If the pinned version does not exist — whether because it was never created or it was already pruned — the resolver returns `NotFound`, never silently substitutes a different version. This guarantee is load-bearing for consumers that need to coordinate across a rotation window.

## Pruning old versions

Retention is operator-driven today: pick a `keep` count and prune. The current version is always retained (regardless of `keep`), and `keep` must be `>= 1`.

```bash
# Keep only the 2 most-recent versions; reclaim backing-store slots for
# platform-owned versions that get dropped.
curl -sS -X POST "$SPRING_API_URL/api/v1/units/engineering-team/secrets/openai-api-key/prune?keep=2" \
  -H "Authorization: Bearer $SPRING_TOKEN"
```

The response returns `{ name, scope, keep, pruned }` where `pruned` is the count of version rows removed from the registry. For each pruned `PlatformOwned` version the platform also deletes the backing store slot; `ExternalReference` versions never touch the external store. A `Secrets:VersionRetention` configuration knob is documentary today — a scheduler will consume it in a future wave; until then, prune explicitly.

## Deleting

`DELETE` removes every version of a secret. Platform-owned versions have their backing store slots reclaimed; external-reference versions leave the external store untouched (deleting a Spring Voyage pointer never destroys a customer-owned secret). A delete that fails mid-way on the store side leaves the registry row intact so the operation is safe to retry.

```bash
curl -sS -X DELETE "$SPRING_API_URL/api/v1/units/engineering-team/secrets/openai-api-key" \
  -H "Authorization: Bearer $SPRING_TOKEN"
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
curl -sS -X POST "$SPRING_API_URL/api/v1/tenant/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "observability-token", "value": "tenant-default-..."}'

# One unit needs a different token (e.g. a dedicated tracing endpoint).
# The unit-scoped row wins for that unit; everyone else still reads the tenant default.
curl -sS -X POST "$SPRING_API_URL/api/v1/units/research-team/secrets" \
  -H "Authorization: Bearer $SPRING_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "observability-token", "value": "research-team-override-..."}'
```

## Per-agent secrets

The OSS contract stops at unit scope. There is no `SecretScope.Agent`, and the resolver has no agent-aware logic: every agent inside a unit sees the unit's full secret set (and any tenant secrets the unit inherits under the rules above).

Operators who need per-agent isolation today use the unit boundary itself — spin up a single-agent unit for the agent that needs its own keys, and use tenant-scoped secrets only where cross-unit sharing is intentional. This reuses the unit as the isolation primitive instead of inventing a new one.

The full rationale — why an `Agent` scope, an agent-level ACL, and doing nothing were considered, and why "do nothing" was the right call for wave 2 — is captured in [ADR 0004 — Per-agent secrets](../decisions/0004-per-agent-secrets.md). That record also lists the concrete triggers that would cause us to revisit.

## Best practices

- **Name secrets by their consumer, not their provider.** `github-app-key` is easier to reason about than `app-8743-private-key`; the consumer's code can hard-code the former and stay stable across vendor changes.
- **Match the name across scopes so inheritance works.** If a tenant-wide `observability-token` exists and a unit later needs to override it, the unit-scoped secret **must** be registered under the same name. Mismatched names silently fall through to the tenant default.
- **Prune ahead of your rotation cadence.** If you rotate monthly and keep `keep=3`, a secret churns through roughly three months of history. Match the `keep` count to how far back a pinned caller might legitimately still be resolving.
- **Rotate on fixed cadences for owned secrets; rotate on revocation for external references.** Pass-through secrets the platform owns end-to-end should follow your compliance clock. External-reference secrets rotate when the upstream vault rotates — the platform is just re-pointing the registry, so there's no value in rotating more often.
- **Pick the narrowest scope that works, and promote only when genuinely shared.** Dropping a secret into tenant scope because "it might be useful to another unit" widens the audit surface; every unit resolve now includes a tenant-scope access-policy probe. Let the shared-use case appear before paying that cost.
- **Never paste plaintext into logs or PR descriptions.** The HTTP API accepts plaintext exactly once on write; everything else — list responses, rotation responses, version listings — is metadata only. Treat the original paste moment as the only time the value exists outside the encrypted store.
- **Rely on the audit decorator for "who read what."** The resolver surface exposes a `SecretResolvePath` (`Direct`, `InheritedFromTenant`, `NotFound`) that audit decorators record for every resolve. If your deployment needs "which units read this tenant secret?" the answer is a log query, not a registry denormalisation — see [Secret Audit Logging](../developer/secret-audit.md).
- **Don't hand-edit the Dapr state store.** Backing slots are written through AES-GCM envelope encryption with `"{tenantId}:{storeKey}"` as associated data — a ciphertext cannot be transplanted across tenants or keys. Direct edits break authentication; use the API to rotate or delete.
- **Treat the ephemeral dev key as dev-only.** If `Secrets:AllowEphemeralDevKey = true`, restarts render previously-written envelopes unreadable. Never enable this outside local `dotnet run`; staging and production deployments **must** source a durable key via `SPRING_SECRETS_AES_KEY` or `Secrets:AesKeyFile` (see [OSS Secret Store](../developer/secret-store.md) for the full key-sources table).
