# Identifiers and Wire Forms

> **[Architecture Index](README.md)** | Related: [Messaging](messaging.md), [Units](units.md), [Tenants](../concepts/tenants.md)

Spring Voyage operates on a single-identity model: every actor — unit, agent, human, connector, tenant — has exactly one stable identifier, a `Guid`. `display_name` is presentation-only. Slugs do not exist anywhere in the persistence, routing, or addressing layers. This document records the canonical wire forms and parser rules so every surface (URLs, JSON DTOs, manifests, CLI, log lines, address strings) emits and accepts identifiers consistently.

The durable architectural decision is [ADR 0036 — Single-identity model](../decisions/0036-single-identity-model.md). [ADR 0023](../decisions/0023-flat-actor-ids.md) (flat actor ids; single-hop routing) carries the routing semantics; the amendment block at the top of that ADR points back here for the identifier shape.

---

## 1. Identity is a `Guid`

Every actor row has exactly one stable identifier: a `Guid`. The `Guid` is the primary key, the foreign-key target, the activity-log source, the wire-form identity, and the manifest cross-reference token. Within an actor's lifetime the `Guid` does not change — rename a unit, move an agent, swap a connector, the `Guid` is the same.

There is no parallel string identifier with equal status. There is no slug column, no slug-shaped path, no namespace+name pair, no scoped handle. A `display_name` field exists for human-facing rendering; it is not unique, not addressable, not a foreign-key target, and validation rejects any `display_name` that parses as a Guid (so a token that looks Guid-shaped is unambiguously identity).

---

## 2. Wire form: 32-character lowercase no-dash hex

The canonical wire form for a `Guid` on URLs, address strings, manifest references, CLI output, and log entries is `Guid.ToString("N")` — 32 lowercase hex characters, no dashes, no braces.

```
8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
```

`Cvoya.Spring.Core.Identifiers.GuidFormatter.Format` is the one helper. It does not surface configuration knobs.

JSON DTO bodies are the one exception — see § 4.

---

## 3. Address shape: `scheme:<32-hex-no-dash>`

`Address` is a record with two fields: `Scheme` (e.g. `agent`, `unit`, `human`, `connector`) and `Id` (`Guid`). The wire form is `scheme:<32-hex-no-dash>`:

```
agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
unit:dd55c4ea8d725e43a9df88d07af02b69
human:f47ac10b58cc4372a5670e02b2c3d479
connector:a1b2c3d4e5f6789012345678901234ab
```

There is no path form, no navigation form, no `scheme://` URI shape. Addresses identify an actor; they do not encode hierarchy. Permission-aware traversal of the membership graph happens at resolution time inside the directory (see [Messaging — Routing](messaging.md#routing) and [ADR 0023](../decisions/0023-flat-actor-ids.md)), not in the address string.

`Address.Path` is a convenience accessor that returns the no-dash hex on its own (useful for callers that need a string actor key — Dapr `ActorId` construction, log correlation, dictionary keys); the canonical render is always `scheme:<id>`.

---

## 4. Asymmetric rule: emit one form, parse many

Parsers are lenient. `GuidFormatter.TryParse`, `Address.TryParse`, and every input surface that uses them accept:

- The canonical no-dash form (`8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`).
- The conventional dashed form (`8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`).
- The braced form (`{8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7}`).
- Any other form `Guid.TryParse` recognises.

This keeps copy-paste workflows working — operators paste Guids out of dashboards, GitHub issues, log lines, and database query results — while eliminating rendering ambiguity at the source.

### The two canonical Guid wire forms

A single value may render in two distinct shapes depending on the surface:

| Surface | Form | Helper | Why |
|---|---|---|---|
| URL paths, `Address` strings, manifest references, CLI table output, log lines | 32-char no-dash hex | `GuidFormatter.Format` | Compact, terminal-friendly, never confused with a name. |
| JSON DTO bodies | dashed `8-4-4-4-12` | STJ default + `NoDashGuidJsonConverter` parse path | Kiota's `GetGuidValue()` and STJ's default `Utf8JsonReader.GetGuid()` accept the dashed form natively; emitting no-dash in JSON would force a custom converter on every typed client. |

Parse remains lenient on both surfaces — a JSON body containing the no-dash form deserialises, and an `Address` carrying the dashed form parses. Only the **emit** path differs.

The decision is recorded in PR [#1643](https://github.com/cvoya-com/spring-voyage/pull/1643) and the converter lives in `src/Cvoya.Spring.Host.Api/Serialization/NoDashGuidJsonConverter.cs`.

---

## 5. The OSS default tenant id

The OSS deployment ships functionally single-tenant. Every tenant-scoped row in a fresh OSS install is owned by `OssTenantIds.Default` — a deterministic v5 UUID derived once and pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs`:

```
namespace = 00000000-0000-0000-0000-000000000000
label     = "cvoya/tenant/oss-default"
uuidv5    = dd55c4ea-8d72-5e43-a9df-88d07af02b69
```

For grep-ability across configuration files, dashboards, and audit logs, the constant is exposed in three forms on the same class:

| Member | Type | Value |
|---|---|---|
| `OssTenantIds.Default` | `Guid` | `dd55c4ea-8d72-5e43-a9df-88d07af02b69` |
| `OssTenantIds.DefaultDashed` | `const string` | `"dd55c4ea-8d72-5e43-a9df-88d07af02b69"` |
| `OssTenantIds.DefaultNoDash` | `const string` | `"dd55c4ea8d725e43a9df88d07af02b69"` |

A v5 UUID over a fixed namespace + label is recomputable from outside the platform (any v5 implementation against the same inputs produces the same Guid), self-documenting (the label is the documentation), and collision-free against random-Guid generation.

`Guid.Empty` is reserved by every nullability and initialisation convention for "uninitialised / programmer error" — it is never reused as a real tenant id. A pattern-shaped Guid like `00000000-0000-0000-0000-000000000001` would claim a chunk of low-numbered Guid space for one decision and provide no provenance — also rejected.

Tenant-scoped writes do not set `TenantId` explicitly; `Cvoya.Spring.Dapr.Data.SpringDbContext` auto-populates it from the injected `ITenantContext`. Cross-tenant reads/writes go through `ITenantScopeBypass.BeginBypass(reason)`. See [`CONVENTIONS.md` § 12](../../CONVENTIONS.md#12-extensibility--tenancy).

---

## 6. Manifests: local symbols within a file, Guids across packages

Inside a single manifest file, references between artefacts are **local symbols** scoped to the file. The artefact's `name` / `id` field IS the symbol — the install pipeline (`Cvoya.Spring.Dapr.Packaging.Install.LocalSymbolMap`) mints a fresh `Guid` per artefact and binds the local symbol to it, so the staging row and the activator's directory entry share a single Guid identity.

Across packages, references are **Guids** in 32-char no-dash hex form. Display-name lookup across packages does not exist — names are not unique, so resolving by name across the catalog would silently bind to the wrong target.

```yaml
# Inside a single package — local symbols.
unit:
  name: engineering-team        # local symbol
  members:
    - agent: ada                # local symbol resolved within the file
    - unit: backend-team        # local symbol resolved within the file

# Across packages — Guid.
unit:
  name: dogfooding
  members:
    - agent: 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7   # Guid minted by another package
```

Path-style references (`unit://eng/backend/alice`) are rejected by the manifest parser with an actionable error pointing at the new grammar. The rejection is wired into `ParseRaw` so the failure fires at every entry-point — parser, validator, export tooling — not just the resolution path. The decision is recorded in [ADR 0035](../decisions/0035-package-as-bundling-unit.md) and PR [#1642](https://github.com/cvoya-com/spring-voyage/pull/1642).

---

## 7. CLI: Guid for direct lookup, name for search

Every `show` verb on a tenant entity accepts both forms:

- `spring agent show <guid>` — direct lookup. The argument parses as a Guid (canonical no-dash or dashed); the resolver short-circuits the API call and returns the canonical record. 404 if the id does not exist.
- `spring agent show <display_name> [--unit <name-or-guid>]` — search by `display_name` (case-insensitive, exact). Optional `--unit` constrains the candidate set to members of a specific parent unit (the parent reference itself accepts a name or a Guid). Result is 0, 1, or n; an n-match prints a disambiguation table keyed on Guid and exits non-zero so the caller can re-run with the chosen id.

The same shape applies to `spring unit show`. The resolver lives in `src/Cvoya.Spring.Cli/CliResolver.cs`; renderer in `CliResolutionPrinter.cs`. Decision: [ADR 0036](../decisions/0036-single-identity-model.md) § 6 and PR [#1650](https://github.com/cvoya-com/spring-voyage/pull/1650).

A token that parses as a Guid is **always** treated as identity, never as a name — that asymmetry is what the `display_name` validator (§ 1; PR [#1640](https://github.com/cvoya-com/spring-voyage/pull/1640)) protects, by rejecting any submitted `display_name` that round-trips through `Guid.TryParseExact` for any standard form.

---

## 8. URLs

Public URL routes that take an actor identifier carry a `Guid` in 32-char no-dash hex:

```
GET  /api/v1/tenant/agents/{8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7}
GET  /api/v1/tenant/units/{dd55c4ea8d725e43a9df88d07af02b69}
POST /api/v1/tenant/threads/{thread-guid}/messages
```

The route templates use `{id:guid}` constraints; ASP.NET Core's `Guid` model binder accepts both no-dash and dashed forms (lenient parse), so a copy-pasted dashed Guid hitting a route works. Emit always uses the no-dash form.

JSON request and response bodies that carry the same id render it in dashed form (§ 4).

---

## 9. Activity log

Activity-log entries store the source actor's `Guid`. The display name renders at read time via `IDirectoryService` (live lookup) or `IParticipantDisplayNameResolver` (cached read-time resolution in `src/Cvoya.Spring.Host.Api/Services/ParticipantDisplayNameResolver.cs`). When an actor is renamed, every historical activity row immediately renders with the new name on the next read. When an actor is soft-deleted, the resolver snapshots the `display_name` at the moment of deletion onto the activity row so the audit history continues to render meaningfully — the snapshot is the only place the activity log ever stores a name, and only as a tombstone.

`ParticipantRef` carries a non-empty server-resolved `displayName` on every wire-form participant reference, satisfying the contract recorded in [#1635](https://github.com/cvoya-com/spring-voyage/issues/1635) and shipped in PR [#1643](https://github.com/cvoya-com/spring-voyage/pull/1643). Deleted entities surface as the `<deleted>` sentinel.

---

## 10. Execution-config shape: `(runtime, model)`

The user-facing execution config on units and agents — the `ai:` block in
manifests, on the wire DTOs, and in the portal/CLI — is the structured pair
**`(runtime, model)`**, where `model` is itself the structured pair
`{provider, id}`. The provider is intrinsic to the model and is not a
separate user-facing axis.

```yaml
ai:
  runtime: spring-voyage              # AgentRuntime id (closed set)
  model:
    provider: ollama                  # ModelProvider id (open set)
    id: llama3.2:3b                   # provider-native model identifier
```

Runtime ids and provider ids are short kebab-case strings, matched
case-sensitively against entries in `eng/runtime-catalog/runtime-catalog.yaml`. Model
ids are provider-native — Ollama model ids may contain `/`, `:`, and `-`;
the structured `{provider, id}` shape avoids the parser ambiguity a flat
`provider/id` string would carry. The pair is the single source of truth for
provider routing and credential resolution; there is no separate `provider`
slot stored on a unit / agent. See [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md).

---

## 11. Connector-native identities — the bridge

Spring Voyage operates on a single-identity model internally (§ 1), but the **outside world** addresses humans through connector-native identifiers — a GitHub login, a Slack member id, an email. The platform stores both forms and resolves between them on the boundary.

| Surface | Identifier shape | Examples |
|---|---|---|
| `sv.*` MCP tools (`sv.send_message`, `sv.list_members`, future `sv.github.request_review`) | Stable `Guid` | `human:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` |
| Container-native CLI tools agents invoke (`gh`, `git`) — populated via the `SPRING_*` env-vars from [#2380](https://github.com/cvoya-com/spring-voyage/pull/2380) | Connector-native | `gh issue assign --add-assignee alice-mccoder` |
| `human_connector_identities` table — the bridge | Pair `(human_id, connector_id, connector_user_id)` | `(8c5fab…, github, alice-mccoder)` |

The bridge lets both surfaces stay in their natural form. An sv.* tool that needs to act on a GitHub user takes a `human_uuid` and internally resolves to the login via `IHumanConnectorIdentityResolver.ResolveUserIdAsync(humanId, "github")`. Conversely, an inbound webhook that arrives with a login resolves to the platform-native human UUID via `ResolveHumanAsync("github", "alice-mccoder")` before threading through the rest of the platform.

The mapping rows live in the `human_connector_identities` table:

| Column | Notes |
|---|---|
| `id` | Surrogate `Guid` PK |
| `tenant_id` | `ITenantScopedEntity` |
| `human_id` | FK to `humans.id` |
| `connector_id` | Connector slug (`github`, `slack`, …) — matches `IConnectorType.Slug` |
| `connector_user_id` | The stable external id — for GitHub this is the login string |
| `display_handle` | Optional human-readable label |
| `created_at`, `updated_at` | Audit timestamps |

The unique invariant `(tenant_id, connector_id, connector_user_id)` enforces "one external identity → at most one human per tenant." Including `human_id` in the uniqueness would let two humans claim the same login and defeat inbound resolution; it is intentionally excluded. The covering index `(tenant_id, human_id)` backs the "list this human's identities" read pattern.

**Auto-seed.** When a unit's GitHub binding is created or updated and `UnitGitHubConfig.Reviewer` is set, the platform writes `(operator_human, github, <reviewer>)` to `human_connector_identities` if the tuple is not already claimed. The seed is idempotent and the binding-write path tolerates absent / conflicting rows — the auto-seed is an opportunistic UX shortcut, not a correctness invariant. v0.2 retires the per-binding `Reviewer` in favour of per-Human GitHub-handle configuration ([#2417](https://github.com/cvoya-com/spring-voyage/issues/2417)); the table is the data carrier for both shapes.

**CLI.** `spring human identity set --connector <slug> --user-id <id> [--human <id>] [--display-handle <h>]`, `spring human identity list [--human <id>]`, and `spring human identity remove --connector <slug> --user-id <id> [--human <id>]` manage the rows. `--human` defaults to the authenticated caller's UUID (resolved through `/api/v1/tenant/auth/me`). Every CLI write routes through the Kiota-generated `SpringApiKiotaClient`.

The decision is recorded in [#2408](https://github.com/cvoya-com/spring-voyage/issues/2408).

---

## See also

- [ADR 0036 — Single-identity model](../decisions/0036-single-identity-model.md) — the durable decision.
- [ADR 0023 — Flat actor ids; single-hop routing with directory resolution](../decisions/0023-flat-actor-ids.md) — the routing decision, amended at the top to point here.
- [`docs/architecture/messaging.md`](messaging.md) — addressing inside the messaging layer.
- [`docs/architecture/units.md`](units.md) — membership graph; how the directory walks it at resolution time.
- [`docs/concepts/tenants.md`](../concepts/tenants.md) — tenants from the user's vantage.
- [`CONVENTIONS.md` § 12](../../CONVENTIONS.md#12-extensibility--tenancy) — tenancy code patterns.
