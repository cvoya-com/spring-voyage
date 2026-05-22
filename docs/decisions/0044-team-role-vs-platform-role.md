# 0044 — Team role vs. platform role; package-declared human members and the install resolution seam

- **Status:** Accepted — package-declared `humans:` describes a unit's *team* (who plays which **team role**, with what **expertise** and **notifications**); platform ACLs continue to describe *platform authority* (who can do what to the system). The two axes are orthogonal. Install-time resolution of a team-role declaration to a concrete human UUID goes through a single DI seam, `IPackageHumanResolutionPolicy`, with an OSS default that auto-fills every declared role with the install caller's UUID; the hosted overlay swaps via `TryAdd*`. Membership rows are keyed by a synthetic membership UUID with a non-unique `(tenant_id, unit_id, human_id)` and a unique `(tenant_id, unit_id, human_id, role)`. ACL grants stay where they are (`unit_human_permissions`) and are never written by the install path.
- **Date:** 2026-05-17
- **Related:** [#2402](https://github.com/cvoya-com/spring-voyage/issues/2402) (this ADR), [#2399](https://github.com/cvoya-com/spring-voyage/issues/2399) (dogfooding consumer that needs package-declared humans to resolve at install time).
- **Related ADRs:** [0040 — Actor state ownership matrix](0040-actor-state-ownership-matrix.md) — places ACLs in `unit_human_permissions` (single source of truth for permissions); this ADR is careful to add a sibling membership-domain table rather than overload that one. [0036 — Single-identity model](0036-single-identity-model.md) — humans are `Guid`-keyed; package YAML cannot bind by username because usernames are not addressable in OSS or hosted deployments. [0043 — Recursive package format](0043-recursive-package-format.md) — establishes the `Unit` / `UnitTemplate` folder shape that carries `humans:`. [0035 — Package as bundling unit](0035-package-as-bundling-unit.md) — the two-phase atomic install pipeline this ADR plugs the resolution policy into. [0034 — OSS dogfooding unit](0034-oss-dogfooding-unit.md) — the dogfooding package whose `humans: [{ identity: owner, ... }]` first surfaced the gap.
- **Related code:** `src/Cvoya.Spring.Host.Api/Services/DefaultPackageArtefactActivator.cs` (currently ignores `humans:`; the new reader lands here), `src/Cvoya.Spring.Manifest/UnitManifest.cs` (`HumanManifest` schema; field rename from `identity`/`permission` to `role`/`expertise`), `src/Cvoya.Spring.Core/Security/IHumanIdentityResolver.cs` + `src/Cvoya.Spring.Dapr/Auth/HumanIdentityResolver.cs` (identity ↔ UUID seam the OSS policy consults), `src/Cvoya.Spring.Host.Api/Auth/IAuthenticatedCallerAccessor.cs` (source of the install caller's UUID for the OSS policy), `src/Cvoya.Spring.Dapr/Auth/PermissionService.cs` + `src/Cvoya.Spring.Host.Api/Endpoints/UnitEndpoints.cs` `/api/v1/tenant/units/{id}/humans/...` (existing ACL surface, unchanged), `src/Cvoya.Spring.Dapr/Skills/SvDirectorySkillRegistry.cs` (the `sv.*` MCP directory surface extended to enumerate human members).

## Context

Package authors declare human members on units. Nine `packages/*/units/*/package.yaml` files in the catalog ship the shape

```yaml
humans:
  - identity: owner
    permission: owner
    notifications: ["escalation", "completion"]
```

and `Cvoya.Spring.Manifest.HumanManifest` parses it. No install-time code reads the field. `DefaultPackageArtefactActivator` walks every other slot on a unit manifest — `members`, `execution`, `connectors`, `policies`, `expertise` — and leaves `humans:` untouched. The runtime model assumes humans are added imperatively after install via `POST /api/v1/tenant/units/{id}/humans/{humanId}/permissions`. The dogfooding package (#2399) and every other package in the catalog implicitly assume otherwise; the gap shows up as "I installed the OSS unit and no human is wired to it."

Four problems compound:

1. **The shape conflates two axes.** `identity: owner` names a slot the package author has no way to bind (the package doesn't know who the install caller will be), and `permission: owner` mixes domain participation (which role does this human play on the team?) with platform authority (what may this human do to the unit's configuration?). They are different decisions made by different actors at different times.
2. **The install path has no resolution seam.** Even if `humans:` were read, the activator has no way to turn `identity: owner` into a `Guid`. In OSS there's one operator and the operator should fill the slot; in hosted there's a tenant of N members and the answer depends on a tenant-level rule (operator-fills-all, prompt-per-slot, match-by-claim, reject). Without a seam the OSS and hosted answers fork at the call site.
3. **Humans aren't discoverable to agents.** `sv.directory.list_members(unit_id)` enumerates agent + unit members of a unit but not humans. An agent that wants to answer "who is the security lead on my team?" cannot — the data isn't even surfaced. Package `humans:` declarations are dead metadata until both an install reader **and** a runtime discovery surface exist.
4. **The schema decision is open.** A human may fill more than one role on the same unit (in OSS the operator fills every role; in hosted, a senior teammate might be `security_lead` *and* `owner`). The existing `unit_human_permissions` table — which uniqueness is `(tenant_id, unit_id, human_id)` — cannot carry per-role rows without breaking the ACL invariant. The membership relation needs its own home.

The converged design for #2402 settles axes (1)–(3); this ADR captures the resulting boundary so it survives contributor churn, and locks in (4) so the implementation PR knows which table to add.

## Decision

### 1. Team role and platform role are orthogonal

The package YAML's `humans:` block describes a unit's **team**: who plays which role, with what expertise, and which event notifications they care about. It does **not** grant any platform authority. ACLs continue to be managed through the existing `/api/v1/tenant/units/{id}/humans/{humanId}/permissions` surface and the `unit_human_permissions` table; that surface is unchanged.

Two axes, exhaustively:

| Axis | Granted by | Carries | Surface |
|---|---|---|---|
| **Platform role** | tenant operator (post-install) | `PermissionLevel` (`Viewer / Operator / Owner`); the right to *do* things to the system | `unit_human_permissions` row; `PUT/DELETE /units/{id}/humans/{humanId}/permissions` |
| **Team role** | package author (at-install, via the YAML) | `role` (free-form), `expertise` (free-form list), `notifications` (free-form list); the *role* the human plays on the team | new `unit_memberships_humans` row (see §3); install-time only |

A single physical human can hold any subset of each axis independently. In OSS the operator is necessarily Owner platform-wide *and* fills every team role declared by an installed package (one human, every slot). In hosted, a non-operator tenant member can fill a team role without holding platform-admin rights; the operator can hold Owner without ever appearing on a team. Messaging-tab visibility derives from platform role; team-membership routing — "who is the security lead I should escalate to?" — derives from team role.

### 2. Package YAML shape

`HumanManifest` is rewritten to three fields, only one required:

```yaml
humans:
  - role: owner                              # team role (mandatory; free-form string)
    expertise: [security, infra]             # optional list of free-form tags
    notifications: [escalation, completion]  # optional list of event tags
```

Removed: `identity:` (install-time concern; the package can't bind it), `permission:` / `permissions:` (the package author has no authority to grant platform ACLs).

Vocabularies for `role`, `expertise`, and notification keys are free-form in v0.1. Enforcing a canonical set requires an authoring story (validation, well-known constants, surface in the UI / CLI to pick from) that is out of scope here. The notifications event vocabulary + delivery surface is a separate design pass; see Consequences.

### 3. Membership row lives in its own table; uniqueness is per `(unit, human, role)`

A new tenant-scoped table `unit_memberships_humans` carries one row per `(unit, human, role)` triple. The row is keyed by a synthetic membership `Guid` (the primary key); a unique index enforces `(tenant_id, unit_id, human_id, role)`; a non-unique index covers `(tenant_id, unit_id, human_id)` for "list my roles on this unit" reads.

Column shape:

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Synthetic membership Guid; ADR-0036 wire form. |
| `tenant_id` | `uuid` | `ITenantScopedEntity` per CONVENTIONS § 12. |
| `unit_id` | `uuid` | Foreign key shape only; no DB FK constraint (matches `unit_human_permissions`). |
| `human_id` | `uuid` | Resolved by the install policy; never a username. |
| `role` | `text` | Free-form team role (e.g. `owner`, `security_lead`). |
| `expertise` | `jsonb` | List of free-form tags (`["security", "infra"]`); empty list when absent. |
| `notifications` | `jsonb` | List of free-form event tags (`["escalation"]`); empty list when absent. |
| `created_at` | `timestamptz` | Server-set on insert; not user-editable. |

Why a synthetic membership Guid and not a `(unit_id, human_id, role)` composite PK:

- The membership row is itself an addressable artefact in the directory: an agent's `sv.unit.list_members` response returns one entry per row, and that entry is more useful with a stable `uuid` per row than with a degenerate composite. A future "update this membership's expertise" surface (CLI / API) takes the membership UUID; a composite PK forces the surface to take three columns instead.
- Uniqueness is still enforced exactly where it matters — the `(tenant_id, unit_id, human_id, role)` unique index — without coupling the row identity to its uniqueness shape. The same pattern is in use on `unit_human_permissions` (synthetic `id` PK + unique index on `(tenant_id, unit_id, human_id)`) and was the right call there for the same reason.

The table is **not** an extension of `unit_human_permissions`. ACLs and team membership are different facts owned by different actors (tenant operator vs. package author) on different lifecycles (post-install vs. at-install). Overloading one row to carry both would re-tangle the two axes §1 separates.

**Set semantics — multiple slots with the same role; install outcome depends on resolution.** The unique index states the model in one sentence: a unit's human members are a *set*; the same `(human, role)` pair appears at most once. Multiple package-declared slots that share a role (e.g. `humans: [{role: reviewer}, {role: reviewer}]`) are a **legitimate shape** — package authors use it whenever a team has N seats with the same function. What rows land at install time depends on what `IPackageHumanResolutionPolicy` returns for each declaration:

- **Different humans → multiple rows.** A hosted policy that maps the two `reviewer` declarations to two distinct tenant members produces two rows: both `role: reviewer`, distinct `human_id`s. The unique index passes (the `human_id` differs). The unit has two reviewer members — exactly what the package asked for, in a multi-user tenant.
- **Same human → one row (collapse).** When two declarations resolve to the same `human_id` — the OSS case where every role folds to the install caller's UUID, or the hosted case where a tenant fills both seats with the same person — the second upsert is a no-op and the declarations collapse. At runtime, that human receives all role-targeted messages exactly once, not duplicated per declaration. This is the correct outcome.

A package author who wants two semantically distinct seats sharing the same shape (e.g. "lead reviewer" and "backup reviewer") uses two distinct role names. No `slot` field is introduced; the role name *is* the seat identity.

### 4. Install-time resolution goes through `IPackageHumanResolutionPolicy`

A new DI-swappable seam in `Cvoya.Spring.Core` (kept dependency-free per AGENTS.md):

```csharp
namespace Cvoya.Spring.Core.Packages;

public interface IPackageHumanResolutionPolicy
{
    Task<PackageHumanResolution> ResolveAsync(
        PackageHumanResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PackageHumanResolutionRequest(
    Guid TenantId,
    Guid UnitId,
    string UnitDisplayName,
    string Role,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications,
    Guid? InstallCallerHumanId);

public sealed record PackageHumanResolution(
    PackageHumanResolutionOutcome Outcome,
    IReadOnlyList<Guid> HumanIds);

public enum PackageHumanResolutionOutcome { Resolved, Skipped, Rejected }
```

The activator calls the policy once per declared `humans[]` entry; each call returns zero or more human Guids (zero for `Skipped`, ≥1 for `Resolved`; `Rejected` is a hard install failure that surfaces as an `InstallException` with the policy's reason). The activator then upserts one `unit_memberships_humans` row per returned Guid, idempotent on the unique index.

**OSSPolicy (default, lives in `Cvoya.Spring.Dapr/Auth/OssPackageHumanResolutionPolicy.cs`)** auto-fills every declared role with the install caller's UUID and returns `Resolved`. The install caller's UUID is supplied to `PackageInstallService` through `IAuthenticatedCallerAccessor.GetCallerAddressAsync()` (already wired in the API layer); the policy receives it on the request rather than re-resolving from `HttpContext` so the policy stays usable from the worker / out-of-request install paths too. ACLs are not granted by this policy — the operator already has platform authority and the team-membership row is the only new write.

**HostedPolicy** is supplied by the cloud overlay. Concrete shapes worth implementing when that overlay adds support are `operator-fills-all` (single-admin model, mirrors OSS), `prompt-per-slot` (interactive, returns `Skipped` and surfaces a follow-up task to the operator), `match-by-identity-claim` (consults a tenant-level claim-to-role map), and `reject` (no package-declared humans allowed; the install fails). Registration is `services.TryAddSingleton<IPackageHumanResolutionPolicy, OssPackageHumanResolutionPolicy>()` from `Cvoya.Spring.Dapr` so the cloud overlay can register first and win.

### 5. Discoverability via the `sv.*` MCP directory surface

`SvDirectorySkillRegistry.ListMembersTool` (`sv.directory.list_members`) is extended to fold human members into the same homogeneous response shape it already returns for agents and units. Today the tool returns entries keyed by `kind in { "agent", "unit", "tenant" }`; a fourth value `"human"` is added with the team-membership fields exposed alongside the universal entry fields:

- `uuid` — the human's `Guid` (the `HumanEntity.Id`, not the membership row id).
- `kind` — `"human"`.
- `display_name` — the human's `HumanEntity.DisplayName`.
- `parent_uuids` — `[unit_uuid]` (the units the human is a member of via team-membership rows).
- `description` — empty string (humans don't carry a description column).
- `expertise` — the membership row's `expertise` tags rendered as `{name, description, level}` entries (level null because the team-membership shape doesn't yet have a level field).
- `member_count` — `null` (humans aren't aggregates).
- `live_status` — `"n/a"` (humans don't have a runtime container; the surface stays uniform).

The team-role-specific fields (`role`, `notifications`) are surfaced as a sibling list on `sv.directory.list_members` only — not via `sv.directory.get_self` or `sv.directory.get_member`, which operate on addressable actors. The choice is to extend the existing `sv.directory.list_members` rather than add a new `sv.unit.list_members`: the existing tool already takes `uuid` (the unit's id), already returns a flat heterogeneous member list, and clients filter by `entry.kind` today; adding `"human"` keeps one tool to learn instead of two. The schema response gains an optional `team_role` field on entries with `kind == "human"`:

```json
{
  "uuid": "…",
  "kind": "human",
  "display_name": "…",
  "team_role": "security_lead",
  "expertise": [{ "name": "security" }],
  "parent_uuids": ["<unit_uuid>"],
  …
}
```

Agents discover their human teammates on-demand via this tool. The agent-context bundle (the per-launch prompt context) does **not** materialise the member list at launch time — agents fetch on demand. The bundle would otherwise need to grow with every membership change and would invalidate every cached prompt assembly; a single `sv.directory.list_members` call when an agent needs to route is cheaper than baking the list into every prompt assembly.

### 6. What this ADR does NOT decide

- **Notification vocabulary and delivery.** The `notifications: [escalation, completion]` field is persisted verbatim. The event taxonomy (what events count as "escalation"?), the delivery surface (email, portal, both, none?), and the routing model are a separate design pass. The field is captured at install so that pass has data to design against; nothing in this ADR commits to a delivery mechanism.
- **Agent routing decisioning.** The MCP surface exposes "who is my human teammate with what expertise"; how agents combine that with task intent to *pick* a target is agent-runtime behaviour, not platform shape.
- **Per-role canonical vocabularies.** `role` and `expertise` are free-form. A future ADR may pin a canonical set if the authoring surface (UI / CLI) needs autocomplete or validation; v0.1 deliberately stays open.
- **Membership UUID exposure on the existing `/api/v1/tenant/units/{id}/humans/...` ACL surface.** That surface stays exactly as it is — operator-managed permissions on the existing table. Team-membership management surfaces (CLI / API) for editing a row after install are a follow-up; v0.1 lands install-time write only and read via MCP.
- **`HumanActor` changes.** The actor's responsibilities (lifecycle, per-thread read cursors, ack semantics) are unaffected. The new table is a sibling concern; the actor does not gain a "my team roles" cache because the data is read on demand by the MCP surface and by any future routing surface.

## Consequences

**Easier:**

- "Where do team membership facts live?" has a one-line answer (`unit_memberships_humans`), distinct from "where do permissions live?" (`unit_human_permissions`).
- The OSS install path produces a working dogfooding unit out of the box: every declared team role auto-binds to the operator's UUID; the operator's existing platform Owner permission already gives them everything they can do to the unit.
- The hosted overlay swaps one DI seam (`TryAddSingleton<IPackageHumanResolutionPolicy, …>`) and gets full control over identity resolution without forking the install pipeline.
- Agents asking "who is on my team?" get a single MCP call away from the answer, in the same shape they already use for agent / unit / tenant members.
- ACL evolution and team-membership evolution can move independently — the package author can rename a team role without touching tenant permission rows, and the tenant operator can change permissions without touching package YAML.

**Harder:**

- Every `packages/*/units/*/package.yaml` carrying the old shape is rewritten in the same PR as the install reader lands (nine files; ADR-0043 § 8 sets the precedent: no back-compat shim while v0.1). The parser surfaces a precise error on the old shape (`LegacyHumanPermissionField` / `LegacyHumanIdentityField`) so any out-of-tree package gets an actionable migration hint.
- The install pipeline gains a new write site that the existing `unit_human_permissions` migration does not cover; the schema migration adds one table, one composite unique index, and one secondary index.
- The MCP tool's response shape grows a `team_role` field on `kind == "human"` entries. The change is additive — existing clients (agent / unit / tenant entries) are byte-for-byte identical — but it does expand the wire schema documented on `SvDirectorySkillRegistry`.

**Not abstracted:**

- A read-time cache for the team-membership graph. v0.1 reads through the EF table on every `sv.directory.list_members` call; if the call becomes hot the same Dapr-actor warm-cache pattern as the rest of ADR-0040 is the natural follow-up. The wire shape doesn't change.
- A "team-membership upgrade on package reinstall" flow. The install path inserts the rows once (idempotently on the unique index); changing the YAML's `humans:` after a package is installed does not retroactively rewrite existing membership rows. A future re-install / upgrade story addresses this.
- Multi-tenant policy chaining (e.g. tenant A's policy delegates to a parent tenant's policy). The seam returns a single resolution per call; composition lives in whichever overlay needs it.
- Notification preferences keyed per team role. The `NotificationPreferences` field on `HumanEntity` is per-human-globally; the team-membership row's `notifications` list is per-team-membership-instance. The two coexist; routing precedence between them is in scope for the notifications design pass.
