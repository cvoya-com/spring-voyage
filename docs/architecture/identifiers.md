# Identifiers and Wire Forms

> **[Architecture Index](README.md)** | Related: [Messaging](messaging.md), [Units](units.md), [Tenants](../concepts/tenants.md)

Spring Voyage operates on a single-identity model: every actor — unit, agent, human, connector, tenant, tenant-user — has exactly one stable identifier, a `Guid`. `display_name` is presentation-only. Slugs do not exist anywhere in the persistence, routing, or addressing layers. This document records the canonical wire forms and parser rules so every surface (URLs, JSON DTOs, manifests, CLI, log lines, address strings) emits and accepts identifiers consistently.

The durable architectural decision is [ADR 0036 — Single-identity model](../decisions/0036-single-identity-model.md), amended by [ADR-0047](../decisions/0047-platform-user-human-split.md) §1 to include the `tenant-user` actor kind. [ADR 0023](../decisions/0023-flat-actor-ids.md) (flat actor ids; single-hop routing) carries the routing semantics; the amendment block at the top of that ADR points back here for the identifier shape.

---

## 1. Identity is a `Guid`

Every actor row has exactly one stable identifier: a `Guid`. The `Guid` is the primary key, the foreign-key target, the activity-log source, the wire-form identity, and the manifest cross-reference token. Within an actor's lifetime the `Guid` does not change — rename a unit, move an agent, swap a connector, the `Guid` is the same.

There is no parallel string identifier with equal status. There is no slug column, no slug-shaped path, no namespace+name pair, no scoped handle. A `display_name` field exists for human-facing rendering; it is not unique, not addressable, not a foreign-key target, and validation rejects any `display_name` that parses as a Guid (so a token that looks Guid-shaped is unambiguously identity).

The actor-kind enumeration is **unit, agent, human, connector, tenant, tenant-user**. The `tenant-user` kind was added by [ADR-0047 §1](../decisions/0047-platform-user-human-split.md) — the authenticated principal of Spring Voyage scoped to one tenant; see [Tenants — TenantUser](../concepts/tenants.md#tenantuser-the-authenticated-principal). Every property listed above applies unchanged across the enumeration.

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

## 6. The OSS operator `TenantUser` id

The OSS deployment ships with exactly one `TenantUser` — the operator. Its id is `OssTenantUserIds.Operator` — a deterministic v5 UUID derived once and pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantUserIds.cs` ([ADR-0047 §§ 1, 3](../decisions/0047-platform-user-human-split.md)):

```
namespace = 00000000-0000-0000-0000-000000000000
label     = "cvoya/tenant-user/oss-operator"
uuidv5    = 5c4c8e29-d91b-5b50-8651-64536cfb68ee
```

For grep-ability across configuration files, dashboards, and audit logs, the constant is exposed in three forms on the same class — mirroring the `OssTenantIds.Default` shape from § 5:

| Member | Type | Value |
|---|---|---|
| `OssTenantUserIds.Operator` | `Guid` | `5c4c8e29-d91b-5b50-8651-64536cfb68ee` |
| `OssTenantUserIds.OperatorDashed` | `const string` | `"5c4c8e29-d91b-5b50-8651-64536cfb68ee"` |
| `OssTenantUserIds.OperatorNoDash` | `const string` | `"5c4c8e29d91b5b50865164536cfb68ee"` |

```csharp
public static class OssTenantUserIds
{
    public static readonly Guid Operator = new("5c4c8e29-d91b-5b50-8651-64536cfb68ee");
    public const string OperatorDashed = "5c4c8e29-d91b-5b50-8651-64536cfb68ee";
    public const string OperatorNoDash = "5c4c8e29d91b5b50865164536cfb68ee";
}
```

The recipe (namespace + label) is the documentation; the literal is the pin. Any v5 implementation against the same namespace + label reproduces the value, so the constant is auditable from outside the platform. Reproduce with Python: `uuid.uuid5(uuid.UUID("00000000-0000-0000-0000-000000000000"), "cvoya/tenant-user/oss-operator")`.

`OssTenantUserIds` and `OssTenantIds` are deliberately separate classes — they name different kinds of well-known id (tenant-user vs tenant). Co-locating them under one class would erode the discrimination ADR-0036 §1 worked to preserve.

In OSS every `Human` row resolves to this single `TenantUser` through the `Human → TenantUser` mapping — see [Humans § Human → TenantUser display mapping](../concepts/humans.md#human--tenantuser-display-mapping) and [Tenants § TenantUser](../concepts/tenants.md#tenantuser-the-authenticated-principal).

---

## 7. Manifests: local symbols within a file, Guids across packages

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

## 8. CLI: Guid for direct lookup, name for search

Every `show` verb on a tenant entity accepts both forms:

- `spring agent show <guid>` — direct lookup. The argument parses as a Guid (canonical no-dash or dashed); the resolver short-circuits the API call and returns the canonical record. 404 if the id does not exist.
- `spring agent show <display_name> [--unit <name-or-guid>]` — search by `display_name` (case-insensitive, exact). Optional `--unit` constrains the candidate set to members of a specific parent unit (the parent reference itself accepts a name or a Guid). Result is 0, 1, or n; an n-match prints a disambiguation table keyed on Guid and exits non-zero so the caller can re-run with the chosen id.

The same shape applies to `spring unit show`. The resolver lives in `src/Cvoya.Spring.Cli/CliResolver.cs`; renderer in `CliResolutionPrinter.cs`. Decision: [ADR 0036](../decisions/0036-single-identity-model.md) § 6 and PR [#1650](https://github.com/cvoya-com/spring-voyage/pull/1650).

A token that parses as a Guid is **always** treated as identity, never as a name — that asymmetry is what the `display_name` validator (§ 1; PR [#1640](https://github.com/cvoya-com/spring-voyage/pull/1640)) protects, by rejecting any submitted `display_name` that round-trips through `Guid.TryParseExact` for any standard form.

---

## 9. URLs

Public URL routes that take an actor identifier carry a `Guid` in 32-char no-dash hex:

```
GET  /api/v1/tenant/agents/{8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7}
GET  /api/v1/tenant/units/{dd55c4ea8d725e43a9df88d07af02b69}
POST /api/v1/tenant/threads/{thread-guid}/messages
```

The route templates use `{id:guid}` constraints; ASP.NET Core's `Guid` model binder accepts both no-dash and dashed forms (lenient parse), so a copy-pasted dashed Guid hitting a route works. Emit always uses the no-dash form.

JSON request and response bodies that carry the same id render it in dashed form (§ 4).

---

## 10. Activity log

Activity-log entries store the source actor's `Guid`. The display name renders at read time via `IDirectoryService` (live lookup) or `IParticipantDisplayNameResolver` (cached read-time resolution in `src/Cvoya.Spring.Host.Api/Services/ParticipantDisplayNameResolver.cs`). When an actor is renamed, every historical activity row immediately renders with the new name on the next read. When an actor is soft-deleted, the resolver snapshots the `display_name` at the moment of deletion onto the activity row so the audit history continues to render meaningfully — the snapshot is the only place the activity log ever stores a name, and only as a tombstone.

`ParticipantRef` carries a non-empty server-resolved `displayName` on every wire-form participant reference, satisfying the contract recorded in [#1635](https://github.com/cvoya-com/spring-voyage/issues/1635) and shipped in PR [#1643](https://github.com/cvoya-com/spring-voyage/pull/1643). Deleted entities surface as the `<deleted>` sentinel.

---

## 11. Execution-config shape: `(runtime, model)`

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

## 12. Connector-native identities — the bridge

Spring Voyage operates on a single-identity model internally (§ 1), but the **outside world** addresses humans through connector-native identifiers — a GitHub login, a Slack member id, an email. The platform stores both forms and resolves between them on the boundary. The display-side identity row lives on the [`TenantUser`](../concepts/tenants.md#tenantuser-the-authenticated-principal), not on the `Human` ([ADR-0047 §§ 2, 7](../decisions/0047-platform-user-human-split.md)) — a `Human` is a configuration entity declared by a package and resolves to a `TenantUser` through the [`Human → TenantUser` mapping](../concepts/humans.md#human--tenantuser-display-mapping); the `TenantUser` is the authenticated principal that owns the connector handle.

| Surface | Identifier shape | Examples |
|---|---|---|
| `sv.*` MCP tools (`sv.messaging.send`, `sv.directory.list_members`, …) | Stable `Guid` | `human:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` |
| Container-native CLI tools agents invoke (`gh`, `git`) — populated via the `SPRING_*` env-vars from [#2380](https://github.com/cvoya-com/spring-voyage/pull/2380) | Connector-native | `gh issue assign --add-assignee octocat` |
| `TenantUserConnectorIdentity` table — the bridge | Triple `(tenant_id, tenant_user_id, connector_id)` | `(<tenant>, <operator-tu>, github)` with `username = octocat` |

The bridge lets both surfaces stay in their natural form. An `sv.*` tool that needs to act on a GitHub user takes a `human_uuid`, walks `Human → TenantUser → TenantUserConnectorIdentity for connector=github`, and resolves to the `username`. Conversely, an inbound webhook that arrives with a login resolves to the platform-native `tenant_user` UUID via `ITenantUserConnectorIdentityResolver.ResolveTenantUserAsync("github", "octocat")` before threading through the rest of the platform.

The mapping rows live in the `TenantUserConnectorIdentities` table:

| Column | Notes |
|---|---|
| `id` | Surrogate `Guid` PK |
| `tenant_id` | `ITenantScopedEntity` |
| `tenant_user_id` | FK to `tenant_users.id` |
| `connector_id` | Connector slug (`github`, `slack`, …) — matches `IConnectorType.Slug` |
| `username` | The connector-side login (e.g. GitHub `octocat`, Slack `@alice`). No leading `@`. |
| `display_handle` | Optional human-friendly rendering (e.g. `"Alice Smith (@alice)"`). Falls back to `username` when null. |
| `created_at`, `updated_at` | Audit timestamps |

The natural key is `(tenant_id, tenant_user_id, connector_id)` — exactly one display-identity row per `(tenant_user, connector)`. The row is **strictly display identity** ([ADR-0047 §2](../decisions/0047-platform-user-human-split.md)) — no PAT, no installation override, no `config_json`, no auth fields. Outbound credentials live on the unit binding, not here.

The unique invariant `(tenant_id, connector_id, username)` backs inbound resolution ("which tenant user is this GitHub login?") — within a tenant a connector login maps to at most one tenant user. Cross-tenant the same login may legitimately appear on two different tenant-user rows in two different tenants (see [Tenants § cross-tenant identity is two rows](../concepts/tenants.md#cross-tenant-identity-is-two-rows)).

**OSS default.** Every `Human` row maps to `OssTenantUserIds.Operator` (§ 6), so all display-identity reads against humans resolve to the single operator `TenantUser`'s connector rows. The operator's GitHub login is configured once and serves every `Human` declared by every installed package.

**CLI.** `spring user identity set --connector <slug> --username <name> [--display-handle <h>]`, `spring user identity list`, and `spring user identity remove --connector <slug>` manage the rows for the authenticated caller's `TenantUser`. There is no `--human` flag — the row belongs to the caller's tenant user, not to a `Human`. Every CLI write routes through the Kiota-generated `SpringApiKiotaClient` against `/api/v1/tenant/users/{tenantUserId}/identities`.

The decision is recorded in [ADR-0047](../decisions/0047-platform-user-human-split.md); the prior `HumanConnectorIdentity`-shaped scaffold from [#2408](https://github.com/cvoya-com/spring-voyage/issues/2408) is dropped and recreated under the new shape ([ADR-0047 §8](../decisions/0047-platform-user-human-split.md)).

---

## See also

- [ADR 0036 — Single-identity model](../decisions/0036-single-identity-model.md) — the durable decision.
- [ADR 0047 — TenantUser / human split; connector identity on the tenant user](../decisions/0047-platform-user-human-split.md) — the `tenant-user` actor kind, the `OssTenantUserIds.Operator` pin, the `TenantUserConnectorIdentity` shape.
- [ADR 0023 — Flat actor ids; single-hop routing with directory resolution](../decisions/0023-flat-actor-ids.md) — the routing decision, amended at the top to point here.
- [`docs/architecture/messaging.md`](messaging.md) — addressing inside the messaging layer.
- [`docs/architecture/units.md`](units.md) — membership graph; how the directory walks it at resolution time.
- [`docs/concepts/tenants.md`](../concepts/tenants.md) — tenants from the user's vantage, including the `TenantUser` actor kind.
- [`docs/concepts/humans.md`](../concepts/humans.md) — humans as configuration entities; `Human → TenantUser` display mapping.
- [`CONVENTIONS.md` § 12](../../CONVENTIONS.md#12-extensibility--tenancy) — tenancy code patterns.
