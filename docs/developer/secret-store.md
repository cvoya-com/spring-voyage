# OSS Secret Store: At-Rest Encryption & Per-Tenant Components

The OSS `ISecretStore` implementation (`DaprStateBackedSecretStore`) persists secret plaintext through the Dapr state-management building block. Two layers of defence protect that data on disk:

1. **Application-layer AES-GCM envelope encryption** wraps every value before handing it to Dapr.
2. **Optional per-tenant Dapr component isolation** — each tenant can be routed to its own `statestore-<tenantId>` Dapr component.

Neither layer replaces a proper KMS-backed store in production, but together they make "plaintext in Redis" a non-issue in local dev and reduce the blast radius of a leaked backend snapshot.

---

## Envelope Encryption (AES-GCM-256)

Every `WriteAsync` call wraps the plaintext in a versioned envelope:

```
[version(1)][nonce(12)][ciphertext(N)][auth tag(16)]   →   base64
```

- **Version 1** is AES-GCM-256 with a random per-write nonce.
- **Associated Data** is `"{tenantId}:{storeKey}"`. A ciphertext cannot be transplanted across tenants or store keys — authentication fails on read.
- **Pre-encryption legacy values** (plain UTF-8 strings with no version prefix) are still readable; they get re-enveloped on the next write.

Platform-scoped secrets use the literal string `"platform"` as the tenant id in the AAD — this matches how platform-owned registry entries identify themselves everywhere else in the system.

### Key Sources

In priority order:

1. **`SPRING_SECRETS_AES_KEY` environment variable** — base64-encoded 32-byte key.
2. **`Secrets:AesKeyFile` config value** — filesystem path to a file whose contents are the base64-encoded key. Useful for container deployments that mount a secret volume.

If neither is satisfied, the service refuses to boot. Earlier versions accepted a `Secrets:AllowEphemeralDevKey=true` fallback that generated a random in-memory key per process; that path was removed because the platform's multi-process topology (spring-api / spring-worker share the same encrypted secret store) means a per-process random key silently corrupted every cross-process secret read. Configure a real key on every deployment, including local dev.

### Startup Self-Check

The encryptor fails fast on obviously weak keys:

- Wrong length (anything other than 32 bytes decoded).
- All zeros or all `0xFF`.
- Sentinel/test patterns (`0x00, 0x01, 0x02, …` ascending; ASCII `"changeme…"`; ASCII `"testtest…"`; all spaces; all `A`).

Error messages name the key source and list both configuration options (env var, key file), so operators don't have to guess.

### Generating a Key

```bash
# Generate a fresh base64-encoded 32-byte key.
openssl rand -base64 32
```

Export it:

```bash
export SPRING_SECRETS_AES_KEY="$(openssl rand -base64 32)"
```

Or write it to a mounted file:

```bash
openssl rand -base64 32 > /secrets/spring-aes.key
# ...and set Secrets:AesKeyFile=/secrets/spring-aes.key in config.
```

### Rotation

**Per-secret rotation** is supported via `PUT /api/v1/units/{id}/secrets/{name}` (and the tenant / platform mirrors). The endpoint atomically:

1. Writes the replacement value via `ISecretStore.WriteAsync` (pass-through) or records the new pointer (external reference).
2. Updates the registry row's `StoreKey`, `Origin`, `Version` (incremented), and `UpdatedAt`.
3. Immediately deletes the old backing slot — **only** for platform-owned entries. External-reference rotations never touch the customer-owned slot.

The registry-level primitive is `ISecretRegistry.RotateAsync`, which returns a `SecretRotation` summary (from/to versions, pointer transition, whether the old slot was reclaimed). Audit-log decorators wrapping the registry consume this to emit rotation events without any private state — see [`secret-audit.md`](secret-audit.md).

**Delete policy.** The OSS core applies an immediate-delete policy: once the registry points at the new key, no in-flight reader can reach the old slot, so any retention window would only leak plaintext. Callers that already hold the old plaintext in memory are unaffected.

**Multi-version coexistence** (caller pinning to `v1` while the server is on `v2`) is tracked as a follow-up to #201 — the current wave intentionally supports only a single live version per `(tenant, scope, owner, name)` triple.

**Key-material rotation** (the AES-GCM envelope key itself) is still operator-driven:

1. Stand up a new environment with the new key.
2. Export secrets from the old environment (via the API, which emits plaintext once the caller passes authorization), re-import into the new one.
3. Retire the old key.

Production deployments that need transparent key rotation should externalize key material via the private cloud `ISecretStore` implementation (Azure Key Vault / AWS KMS), which supports native rotation policies.

---

## Per-Tenant Dapr Component Isolation

By default the OSS store uses a single Dapr component (`Secrets:StoreComponent`, defaulting to `secretsstore`) for every tenant. Structural tenant isolation comes from the registry; the shared component is a dev convenience.

The `secretsstore` component (shipped under `eng/dapr/components/production/`) is a second `state.postgresql` instance pinned to `keyPrefix: none`, so every host (`spring-api`, `spring-worker`, dispatcher) reads and writes through the same key namespace. The default `statestore` component uses Dapr's `keyPrefix: appid` and was therefore unsuitable for cross-host secret resolution (#2212).

Set `Secrets:ComponentNameFormat` to a template containing `{tenantId}` to switch to per-tenant components:

```json
{
  "Secrets": {
    "ComponentNameFormat": "statestore-{tenantId}"
  }
}
```

With this setting, a call from tenant `acme` reads and writes via the Dapr component named `statestore-acme`. Operators are responsible for provisioning those components (see [Dapr's multi-component guide](https://docs.dapr.io/operations/components/component-scopes/)). A misconfigured caller that targets the wrong component sees nothing — this is defence in depth on top of the registry's tenant filter.

### Legacy Key Fallback

The canonical backend key is `Secrets:KeyPrefix + storeKey` (no tenant segment). An earlier shape embedded the tenant — `secrets/{tenantId}/{storeKey}` — and the store still tries that form if the canonical read misses. This keeps data readable immediately after flipping `ComponentNameFormat` on; writes migrate the row to the canonical key naturally, and `DeleteAsync` best-effort cleans up both shapes.

A one-time rewrite is only needed if operators want to reclaim storage eagerly; the resolver tolerates the mixed state.

---

## Runtime credential identity (#2161)

LLM credentials are keyed by `(provider, authMethod)`, not provider alone. `CredentialNaming.SecretNameFor(provider, authMethod)` is the single source for persisted names:

| Secret name | Runtime/provider edge | Env var |
|---|---|---|
| `anthropic-oauth` | `claude-code` → `anthropic` | `CLAUDE_CODE_OAUTH_TOKEN` |
| `anthropic-api-key` | `spring-voyage` → `anthropic` | `ANTHROPIC_API_KEY` |
| `openai-api-key` | `codex` → `openai`; `spring-voyage` → `openai` | `OPENAI_API_KEY` |
| `google-api-key` | `gemini` → `google`; `spring-voyage` → `google` | `GOOGLE_API_KEY` |

The runtime catalogue declares the edge auth method, the CLI and portal write that exact secret name, and `ILlmCredentialResolver.ResolveAsync(provider, authMethod, ...)` reads the same name at dispatch time. This prevents Claude Code OAuth tokens and Anthropic API keys from cross-binding through one shared slot.

**Where the routing lives:**

- **Catalog edge:** `eng/runtime-catalog/runtime-catalog.yaml` names each runtime/provider `authMethod` and `credentialEnvVar`.
- **Secret naming:** `Cvoya.Spring.Core.Catalog.CredentialNaming` maps the edge to a persisted name such as `anthropic-oauth`.
- **Resolver:** `LlmCredentialResolver` resolves by `(provider, authMethod)` across agent, unit, parent-unit, and tenant scopes.
- **Status endpoint:** `GET /api/v1/platform/credentials/{provider}/status?authMethod=...` reports whether the requested edge credential is configured without returning plaintext.

> **Spring Voyage runtime: per-provider Conversation wiring.** The Spring Voyage runtime talks to the LLM through Dapr Conversation components. Per ADR-0038, the OSS deployment ships one component file per provider under `eng/dapr/components/delegated-spring-voyage-agent/llm-{provider}.yaml` (`llm-anthropic`, `llm-openai`, `llm-google`, `llm-ollama`). The `SpringVoyageAgentLauncher` resolves credentials via `ILlmCredentialResolver`, propagates the per-provider env var (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GOOGLE_API_KEY`) into the daprd sidecar, and pins `SPRING_LLM_COMPONENT=llm-{provider}` so the Python agent dials the right component.

## Recommended Defaults

| Environment   | `SPRING_SECRETS_AES_KEY` | `ComponentNameFormat` |
|---------------|--------------------------|-----------------------|
| Local dev     | generated once per workstation, kept in `eng/config/spring.env` | unset (shared) |
| CI            | generated per run, set in the pipeline env | unset |
| Staging/Prod  | sourced from vault / mounted secret file | `"statestore-{tenantId}"` when running multi-tenant |

Production deployments should not rely on this at-rest layer as their primary protection — use the KMS-backed store implementation provided by the cloud host. The envelope layer is a belt-and-braces guard against backup leaks and operator errors.
