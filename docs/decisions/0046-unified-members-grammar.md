# 0046 — Unified `members:` grammar; humans as a member kind; `HumanTemplate`; vocabulary trim

- **Status:** Proposed — every participant on a unit (agent, sub-unit, human) is declared under a single `members:` list with an implicit key-prefix discriminator (`- agent:` / `- unit:` / `- human:`); the legacy top-level `humans:` block is a parse error; `workflows/` and `connectors/` are removed from the package vocabulary in v0.1 (connector *bindings* keep working via `requires:`); every member kind carries multi-valued `roles` (renamed from `role`) and `expertise`; `notifications` stays on humans only; `HumanTemplate` joins `AgentTemplate` / `UnitTemplate` as a first-class artefact kind stamped via `- human: { from: <template-name> }`; the `unit_memberships_humans` natural key drops `role` and becomes `(tenant, unit, human)`; each install-time `- human:` declaration mints a fresh `HumanEntity` row; agent / unit-membership rows gain `roles` + `expertise` jsonb columns; `sv.list_members` emits `roles: string[]` (was `team_role: string`) and surfaces `roles` / `expertise` on agent + unit entries. Pre-v0.1 hard rename; no shim.
- **Date:** 2026-05-17
- **Related:** [#2450](https://github.com/cvoya-com/spring-voyage/issues/2450) (this ADR); [#2402](https://github.com/cvoya-com/spring-voyage/issues/2402) (ADR-0044 work this supersedes the relevant sections of); [#2299](https://github.com/cvoya-com/spring-voyage/issues/2299) (ADR-0043 work this amends).
- **Related ADRs:** [0044 — Team role vs. platform role](0044-team-role-vs-platform-role.md) — this ADR **supersedes its §2 (`humans:` shape)**, §3 (`unit_memberships_humans` natural key + columns), and §5 (`sv.list_members` `team_role` field); §1 (team-role vs. platform-role separation) and §4 (`IPackageHumanResolutionPolicy` seam) survive unchanged. [0043 — Recursive package format](0043-recursive-package-format.md) — this ADR **amends §2** (the conventional-directories table) by removing `workflows/` and `connectors/`, **amends §5b** to add `HumanTemplate` to the kinds resolved under `templates/`, and **clarifies §1's inline-vs-folder rules** for member entries. [0040 — Actor state ownership matrix](0040-actor-state-ownership-matrix.md) — `unit_memberships` and `unit_memberships_humans` stay sibling tables; this ADR adds `roles` + `expertise` to the former. [0036 — Single-identity model](0036-single-identity-model.md) — fresh `HumanEntity` Guid per install-time declaration (the `{human_id → user_id}` mapping is v0.2).
- **Related code:** `src/Cvoya.Spring.Manifest/UnitManifest.cs:99-101` (legacy `humans:` slot to remove), `src/Cvoya.Spring.Manifest/UnitManifest.cs:272-312` (`MemberManifest` — gains `Human` slot), `src/Cvoya.Spring.Manifest/UnitManifest.cs:453-479` (`HumanManifest` — `role:` → `roles:`), `src/Cvoya.Spring.Manifest/InlineArtefactDefinition.cs:39-79` (inline-vs-reference converter the `- human:` discriminator reuses), `src/Cvoya.Spring.Manifest/ManifestParser.cs:30-63` (strict-parse seam where `LegacyHumansBlock` / `LegacyWorkflowsSubdir` / `LegacyConnectorsSubdir` errors land), `src/Cvoya.Spring.Core/Artefacts/ArtefactKind.cs:22-35` (gains `HumanTemplate`; drops `Workflow`), `src/Cvoya.Spring.Core/Packages/IPackageHumanResolutionPolicy.cs:117-124` (request record — `Role` → `Roles`), `src/Cvoya.Spring.Core/Units/IUnitHumanMembershipStore.cs:36-104` (drops `role` from the natural key), `src/Cvoya.Spring.Dapr/Data/Configuration/UnitMembershipHumanEntityConfiguration.cs:24-69` (unique-index shift; `role` column drop; `roles` jsonb add), `src/Cvoya.Spring.Dapr/Data/Configuration/UnitMembershipEntityConfiguration.cs:16-37` (gains `roles` + `expertise` jsonb columns), `src/Cvoya.Spring.Dapr/Auth/OssPackageHumanResolutionPolicy.cs` (mints a fresh `HumanEntity` per declaration), `src/Cvoya.Spring.Dapr/Skills/SvDirectorySkillRegistry.cs:89-340` (`sv.list_members` shape change), `packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml:48-52` (one of nine packages rewritten in the implementation PR), `packages/software-engineering/workflows/software-dev-cycle/package.yaml` (deleted alongside any empty `connectors/` directories).

## Context

ADR-0044 landed the package-declared human team-member story behind a separate top-level `humans:` block. ADR-0043 left `workflows/` and `connectors/` as reserved subdirectories on every artefact folder. Three months of dogfooding has shown four rough edges that compound:

1. **Two declaration sites for the same fact.** A unit's participants are split between `members:` (agents, sub-units) and `humans:` (humans). The runtime already treats humans as just-another-member-kind through `sv.list_members` and `IUnitHumanMembershipStore`; the YAML's split is visual drag with no semantic benefit. Authors discovering `- agent:` / `- unit:` keep asking where `- human:` is.

2. **`workflows/` and `connectors/` are empty calories.** The only shipped `Workflow` artefact in the catalog is `packages/software-engineering/workflows/software-dev-cycle/package.yaml`, which is a one-line stub backed by an out-of-tree .NET project. The only `connectors/` directories that exist are empty `.gitkeep` placeholders. Both subdirectories carry parser surface, documentation footprint, and contributor question budget without paying for themselves. Connector *bindings* are a real and load-bearing feature, but bindings are wired via `requires:` on a consumer artefact (ADR-0037 §3); the shipped artefact type adds nothing.

3. **`role: <string>` is single-valued where the domain wants a set.** A human filling "owner" and "security_lead" needs two entries with the same identity and no way to express "this is the same person". An agent that wants to advertise multiple roles has no slot at all. ADR-0044's natural-key choice — `(tenant, unit, human, role)` — forces this awkwardness into the schema.

4. **Inline-vs-folder ambiguity for member entries.** ADR-0043 accepts both a bare reference (`- agent: ada`) and an inline body (`- agent: { name: ada, instructions: "…" }`) on `- agent:` / `- unit:` slots, but never spells out when each is appropriate. Authors guess; the catalog drifts. Issue #2450 surfaces this as Q2.

This ADR resolves all four. The principle: the unit's `members:` list is the single declaration site for who participates; the participant kind is the discriminator; the vocabulary trims to what actually ships.

## Decision

### 1. Unified `members:` grammar with three implicit discriminators

Every participant — agent, sub-unit, human — is declared as one entry on the unit's `members:` list. The discriminator is the key prefix (`agent:` / `unit:` / `human:`); no separate `kind:` field on member entries. The legacy top-level `humans:` block is removed; the parser rejects it with a structured `LegacyHumansBlock` error (migration hint: "move each entry under `members:` as `- human: { … }`; ADR-0046").

```yaml
members:
  - agent: ada                                     # bare reference
  - agent: { name: hopper, from: software-engineer, roles: [reviewer] }
  - unit: { from: engineering, name: engineering-1 }
  - human:
      roles: [owner, security_lead]
      expertise: [security, infra]
      notifications: [escalation, completion]
  - human: { from: oss-operator }                  # template stamp
```

`MemberManifest` (`src/Cvoya.Spring.Manifest/UnitManifest.cs`) gains a third slot `Human` of type `InlineArtefactDefinition` (same union shape as `Agent` / `Unit`). A member entry carries exactly one of `agent:` / `unit:` / `human:`; entries with zero or more than one are rejected.

### 2. `workflows/` and `connectors/` removed from package vocabulary

The conventional-directories table in ADR-0043 §2 contracts to `units/`, `agents/`, `skills/`, `templates/`. The parser rejects `workflows/` and `connectors/` subdirectories at any depth with `LegacyWorkflowsSubdir` / `LegacyConnectorsSubdir` errors (migration hint pointing at this ADR). `ArtefactKind.Workflow` is removed from the Core enum; `kind: Workflow` in a `package.yaml` is a `LegacyWorkflowKind` parse error.

What survives: connector *bindings* via `requires:` (ADR-0037 §3 — `requires: [ { connector: github } ]`). What's removed: the *shipped artefact type*. The one extant workflow file (`packages/software-engineering/workflows/software-dev-cycle/package.yaml`) and the empty `connectors/` `.gitkeep` directories are deleted in the same PR as the parser change. Re-introduce either if and when a real authoring need surfaces; until then, the vocabulary tracks reality.

### 3. Multi-valued `roles` and `expertise` on every member kind; `notifications` stays human-only

`HumanManifest.Role` becomes `HumanManifest.Roles` (typed `List<string>`). The same `roles: [...]` slot is added to inline agent / unit bodies on `MemberManifest`. Within a single entry, role and expertise lists are **case-insensitive sets** — duplicates within one entry are collapsed at parse time; cross-entry duplicates are a separate concern handled by the resolution policy (humans) or the unit-membership upsert (agents / units). Empty list and absent field are equivalent.

`notifications` stays on humans only. Agents do not subscribe to notification events; their messaging surface is the thread mailbox.

### 4. `HumanTemplate` is a first-class artefact kind

`templates/` now resolves to `{ ArtefactKind.UnitTemplate, ArtefactKind.AgentTemplate, ArtefactKind.HumanTemplate }` and the inner `kind:` field disambiguates (per ADR-0043 §5b). A `HumanTemplate` folder declares `displayName`, `description`, `roles`, `expertise`, `notifications`; it does not own sub-artefacts (humans have no child slots). It is stamped via `- human: { from: <template-name> }` and supports cross-package addressing (`from: <pkg>/<name>@<version>`) under ADR-0037 §5.

```yaml
# packages/spring-voyage-oss/templates/oss-operator/package.yaml
apiVersion: spring.voyage/v1
kind: HumanTemplate
name: oss-operator
displayName: OSS Operator
description: Default OSS-deployment human, fills every role.
roles: [owner]
expertise: [operations, escalation]
notifications: [escalation, completion]
```

```yaml
# stamped on a unit
members:
  - human: { from: oss-operator, roles: [overall-lead] }   # roles override
```

### 5. Override semantics on stamped templates: full replacement on `roles` / `expertise`

When a member entry sets `roles` or `expertise` and the referenced template also sets them, the entry replaces the template's value (full replacement, not union). Same rule applies to `notifications` on a `HumanTemplate` stamp. This mirrors the existing `InlineArtefactDefinition` scalar / list semantics: scalars override, lists replace (ADR-0043 §5d). No partial-merge keyword in v0.1; authors who want the template's list plus extras copy the template's list and add to it.

### 6. Inline vs. folder on member entries: Option A — both shapes for agents and units; humans inline-only

The grammar accepts:

- **Agent**: inline body **or** folder reference (folder lives at `agents/<name>/` per ADR-0043).
- **Unit**: inline body **or** folder reference (folder lives at `units/<name>/`).
- **Human**: inline body only. No folder form; humans own no sub-artefacts.

Two other shapes were considered:

- **Option B — inline-only members, skills-only-via-templates.** Every `- agent:` and `- unit:` is an inline body; an agent that wants to ship custom skills is promoted to an `AgentTemplate` folder and stamped via `from:`. Pro: single shape per kind; no two-shape ambiguity; uniform cross-package reuse. Con: any agent with one custom skill becomes a template; the dogfooding pattern of "this team has a one-off engineer with one team-specific skill" forces template ceremony for one body.
- **Option C — folder-only.** Every agent / unit lives in a folder; `members:` carries pure references. Pro: maximally uniform; folder is the canonical artefact shape. Con: trivial inline agents (one-line bodies) all need their own directory; significant authoring overhead for small packages, especially the in-tree examples.

**This ADR adopts Option A.** Reasoning: folder agents are a real authoring pattern — `packages/research/agents/data-analyst/skills/literature-review/` is the shape an agent uses when it owns its own skills, and forcing that into a template (Option B) or onto every agent (Option C) is paperwork for a pattern that already works. The two-shape grammar is enforced cheaply (the converter at `InlineArtefactDefinition.ReadYaml` already dispatches on scalar-vs-mapping) and the authoring guide can anchor the choice to one rule: **"if this agent owns children, give it a folder; otherwise, inline it."** The cost is one extra paragraph in the authoring guide. Q2 of #2450 set out to remove two-shape ambiguity for *humans*; the unified `members:` grammar achieves that without dragging agents / units along for the ride.

Implication: **humans-inline-only is the only restriction this ADR adds versus the ADR-0043 status quo.** Agents and units keep the inline-or-folder choice they already had.

### 7. `unit_memberships_humans` schema shift

The natural key drops `role`. One row per `(tenant, unit, human)` triple; `roles` is a jsonb list on the row itself.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Synthetic membership Guid (unchanged from ADR-0044). |
| `tenant_id` | `uuid` | `ITenantScopedEntity` per CONVENTIONS § 12. |
| `unit_id` | `uuid` | Foreign-key shape only; no DB FK constraint. |
| `human_id` | `uuid` | The `HumanEntity.Id` resolved (or freshly minted) by the install policy. |
| `roles` | `jsonb` | List of free-form team-role strings; empty list when absent. **Replaces the `role text` column.** |
| `expertise` | `jsonb` | Unchanged from ADR-0044. |
| `notifications` | `jsonb` | Unchanged from ADR-0044. |
| `created_at` | `timestamptz` | Unchanged. |

**Index shift:** drop the unique `(tenant_id, unit_id, human_id, role)`; add a unique `(tenant_id, unit_id, human_id)`. The secondary index from ADR-0044 (`(tenant_id, unit_id, human_id)`) is now the unique key. A pre-v0.1 hard rename — the existing migration `20260517072427_UnitMembershipsHumans` is rewritten in place (no follow-up migration; database is reset on v0.1 deploys).

**Fresh `HumanEntity` per declaration.** Each `- human:` entry mints a brand-new `HumanEntity` row with `Id = Guid.NewGuid()` and a derived `DisplayName` (the OSS policy uses `"Operator · <roles[0]>"`, falling back to `"Operator"` when the entry declares no roles). This keeps the existing Identity / Connector / DisplayName surfaces working uniformly — a portal "Humans" tab listing five OSS-installed declarations sees five rows with sensible labels, no special-casing for the operator UUID. The `{human_id → user_id}` mapping table that would let the hosted policy bind multiple declarations to the same physical user is **v0.2** and explicitly out of scope.

**Two same-`roles` entries produce two rows.** A unit with `members: [ { human: { roles: [reviewer] } }, { human: { roles: [reviewer] } } ]` lands two distinct `unit_memberships_humans` rows with two distinct `human_id` values. The unit has two "positions" of the reviewer role — exactly what the package asked for. Whether they map to the same physical user is the resolution policy's call (v0.2 via the mapping table).

### 8. Agent / unit-membership metadata: `roles` + `expertise` jsonb on the existing row

`unit_memberships` (the agent ↔ unit edge backing `IUnitMembershipRepository`) gains two columns: `roles jsonb not null default '[]'` and `expertise jsonb not null default '[]'`. No new table; no new repository. The fields are runtime metadata only — surfaced on `sv.list_members` so agents can ask "which of my teammates are owners?", carried through the UnitMembership EF entity, and otherwise inert. The platform makes no decisions based on them in v0.1 (orchestration policy, dispatch routing, ACL inheritance are all unchanged).

### 9. `sv.list_members` shape

Human entries emit `roles: string[]` (was `team_role: string` per ADR-0044 §5). Agent and unit entries gain optional `roles: string[]` and `expertise: string[]` fields. The change is additive on agent / unit entries (the fields were absent) and breaking-but-pre-v0.1 on human entries (the field is renamed and re-typed). The wire schema documented on `SvDirectorySkillRegistry` is updated in the same PR.

```json
{
  "uuid": "…",
  "kind": "human",
  "display_name": "Operator · owner",
  "roles": ["owner", "security_lead"],
  "expertise": [{ "name": "security" }, { "name": "infra" }],
  "parent_uuids": ["<unit_uuid>"]
}
```

### 10. Resolution policy: shape unchanged, OSS implementation evolves

`IPackageHumanResolutionPolicy` keeps its current interface and request / response records. `PackageHumanResolutionRequest.Role` is renamed to `Roles` (typed `IReadOnlyList<string>`); every other field is unchanged. The OSS implementation evolves: for each declaration it mints a fresh `HumanEntity` with a derived `DisplayName`, persists it, and returns `Resolved` with the freshly-minted `human_id`. The install caller's UUID is no longer the auto-fill — the OSS dogfooding pattern is "every package-declared human is a distinct operator-managed identity", which keeps the Identity / Connector surfaces consistent across declarations. The hosted policy is unchanged conceptually; concrete implementations supplied by the cloud overlay decide whether to mint or to bind to an existing tenant member, and the `{human_id → user_id}` mapping (v0.2) makes that binding explicit.

## What this ADR does NOT decide

- **Notification vocabulary and delivery.** The `notifications` field is still persisted verbatim. The event taxonomy and the delivery surface (email, portal, none) remain a separate design pass (ADR-0044 §6 still holds).
- **Per-role canonical vocabularies.** `roles` and `expertise` remain free-form in v0.1; a future ADR may pin canonical sets if the authoring surface needs autocomplete or validation.
- **`{human_id → user_id}` mapping table.** v0.2. Until then, each `- human:` declaration is a distinct physical row; the hosted policy that wants to bind multiple declarations to the same tenant member waits for v0.2.
- **Hosted resolution policy details.** Concrete shapes (operator-fills-all, prompt-per-slot, match-by-claim, reject) live in the cloud overlay and are unaffected by this ADR's grammar change.
- **Per-member partial overrides on template stamps.** ADR-0043 §5d deferred this for `AgentTemplate` / `UnitTemplate`; the same deferral applies to `HumanTemplate` stamps. Full-clone or full-replace, no middle ground in v0.1.
- **Workflow re-introduction.** If a real authoring need surfaces for `kind: Workflow` as a shipped artefact type, a future ADR re-adds the conventional directory and the kind enum value. The deletion here is "we don't ship one in v0.1", not "we never will".

## Consequences

**Easier:**

- "Where do I declare participants?" has a one-line answer: `members:`. The visual grouping matches the runtime model; `sv.list_members` returns one homogeneous list and the YAML now does too.
- The vocabulary surface contracts. New contributors learn three conventional directories (`units/`, `agents/`, `skills/`, plus `templates/`) and three member kinds. The "what's `workflows/` for?" question disappears.
- Humans get the same template / reuse story as agents and units. A `HumanTemplate` is the natural home for "the OSS operator" — a single declaration referenced from every package, evolving once.
- The `unit_memberships_humans` schema collapses to one row per `(unit, human)`. Reads, updates, and the portal Humans-tab projection all key on a single Guid pair; "list my roles on this unit" is one column read instead of a fan-out.
- Authors who learn the inline shape for member entries never need the folder shape (and vice versa). The choice is anchored to one rule — "does this agent own children?" — that maps cleanly to the existing folder-recursion model.

**Harder:**

- Nine in-tree packages with `humans:` blocks rewrite to `members: [- human: …]` in the same PR as the grammar change. ADR-0044 only just landed the shape these packages adopted; the churn cost is real but bounded (pre-v0.1, no shim).
- The `unit_memberships_humans` migration is rewritten in place. Any local development database carrying ADR-0044's `role` column is reset on the v0.1 deploy; this is the standing v0.1 policy.
- The `sv.list_members` shape change breaks any agent that was reading `team_role` from the wire JSON. Two agents in the catalog touch the field; both are updated in the same PR. Out-of-tree consumers (if any) see a single-cycle break.
- The parser gains three new structured errors (`LegacyHumansBlock`, `LegacyWorkflowsSubdir`, `LegacyConnectorsSubdir`) plus a refusal of `kind: Workflow` (`LegacyWorkflowKind`). Each one ships with an actionable migration hint pointing at this ADR.

**Not abstracted:**

- A read-time cache on `unit_memberships_humans`. v0.1 reads through EF on every `sv.list_members`; the actor-warm-cache pattern from ADR-0040 is the natural follow-up if the call becomes hot.
- A team-membership upgrade on package reinstall. The install path still asserts rows once; rewriting the YAML's `members:` after install does not retroactively rewrite existing rows. Same deferral as ADR-0044's not-abstracted bullet.
- Cross-tenant resolution-policy chaining. Out of scope; the seam returns one resolution per call.
- `notifications` on `HumanTemplate` stamps as a union of template + override. Full replacement only (decision 5); the union shape can be re-litigated when the notifications delivery surface is designed.
