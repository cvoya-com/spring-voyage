# 0036 — Single-identity model: Guid identity, display_name as presentation only

- **Status:** Accepted — 2026-05-03 — every actor (unit, agent, human, connector, tenant) has exactly one stable identifier, a `Guid`. `display_name` is presentation-only — never unique, never addressable, never a foreign-key target. Slugs do not exist anywhere in the persistence or routing layers. The membership graph (`(tenant_id, parent_id, child_id)` triples, with the tenant as the root) is the addressing fabric; top-level units are membership rows whose `parent_id = tenant.id`. The public Guid wire form is 32-character lowercase no-dash hex; parsing is lenient. The OSS default tenant id is a deterministic v5 UUID, pinned as a literal constant.
- **Date:** 2026-05-03
- **Closes:** [#1631](https://github.com/cvoya-com/spring-voyage/issues/1631)
- **Implementation:** [#1629](https://github.com/cvoya-com/spring-voyage/issues/1629) — single-identity baseline, landed in [PR #1637](https://github.com/cvoya-com/spring-voyage/pull/1637) (commit `3c5e87c9` on `main`).
- **Related code:** `src/Cvoya.Spring.Core/Identifiers/GuidFormatter.cs` (canonical wire-form helper), `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs` (`Default` v5 UUID + dashed/no-dash literals), `src/Cvoya.Spring.Core/Messaging/Address.cs` (`(Scheme, Guid Id)` shape; `TryParse` lenient), `src/Cvoya.Spring.Dapr/Data/Entities/UnitDefinitionEntity.cs`, `src/Cvoya.Spring.Dapr/Data/Entities/AgentDefinitionEntity.cs`, `src/Cvoya.Spring.Dapr/Data/Entities/UnitMembershipEntity.cs`, `src/Cvoya.Spring.Dapr/Data/Entities/UnitSubunitMembershipEntity.cs`, `src/Cvoya.Spring.Host.Api/Services/ParticipantDisplayNameResolver.cs` (read-time display-name resolution).
- **Related ADRs:** [0023 — flat actor ids](0023-flat-actor-ids.md) — the predecessor this ADR amends. The single-hop routing decision in 0023 is unchanged; only the identifier *type* and *form* are tightened. [0017 — Unit is an agent composite](0017-unit-is-an-agent-composite.md) — the composite pattern this identity model preserves. [0030 — thread model](0030-thread-model.md) — participants are addressed by Guid identity, not by display name. [0035 — package as bundling unit](0035-package-as-bundling-unit.md) — cross-package reference grammar uses Guids only.
- **Related docs:** [`docs/architecture/messaging.md`](../architecture/messaging.md), [`docs/architecture/units.md`](../architecture/units.md), [`docs/architecture/identifiers.md`](../architecture/identifiers.md) (forthcoming under #1633).

## Context

Spring Voyage's actor identity model evolved through three increasingly leaky shapes before settling here.

**Shape 1 — slug-as-PK.** Early v0.1 keyed every actor row on a per-tenant unique slug (`agent_definitions.agent_id`, `unit_definitions.unit_id`). Slugs were both the addressable handle (`agent://team/alice`) and the foreign-key target (membership rows joined on slug). It read well in URLs and YAML, and it was the first thing tried because it matched what operators wrote in manifests.

**Shape 2 — slug-as-PK with multi-membership.** [ADR 0017](0017-unit-is-an-agent-composite.md) made unit a composite, which means the same agent can belong to many units. The slug-as-PK shape forced every multi-membership question through one of two unsatisfying answers: either the slug had to be globally unique (so an agent named `alice` could exist only once across the whole tenant — operationally absurd as the org grows), or the slug had to be scoped to a parent (making the address contextual — `team-a/alice` and `team-b/alice` are two different agents at two different addresses, even when they are the same person doing the same work). Both shapes leaked across the project: address parsers had to know about scope; activity-log entries that recorded a slug now meant different things depending on when they were read; rename of either the slug or the parent invalidated foreign keys, audit references, and any external link an operator had bookmarked.

**Shape 3 — hybrid (entity slug + edge override).** A staging design ([#1629](https://github.com/cvoya-com/spring-voyage/issues/1629) Comment 2) tried to keep slug as a presentation-stable handle on the entity *and* let an edge contextually rename it for membership-specific display. It collapsed under its own weight: there is no real-world requirement for an agent to have a *different name* in `team-a` than in `team-b` — the same agent is the same agent everywhere. Adding the edge override solved a problem nobody had while doubling the rendering rules every read site had to honour.

The forcing function for the redesign was a bug class — not a single bug, a class. Every layer of the system had its own answer to "what is this actor?" The address parser thought it was a slug-shaped path. The directory thought it was a row in `unit_directory`. The activity log stored whichever string happened to be on hand — sometimes a slug, sometimes a display name, sometimes a Guid hex. Audit dashboards rendered whatever the activity row contained, so a rename broke historical attribution. Manifest references resolved against slugs at install time but were stored as Guids; the round-trip lost fidelity. The bug pattern had a dozen surface manifestations and one root cause: identity and presentation were not distinguished.

The reframing in [#1629](https://github.com/cvoya-com/spring-voyage/issues/1629) (Comment 4 — "Final design: no slugs, Guid-only addressing") is what this ADR makes durable. **Slugs were the wrong axis.** Slugs encode three properties operators legitimately want — uniqueness, addressability, and human readability — into one column, and the three properties pull in different directions. Uniqueness and addressability want stability under rename and global scope; human readability wants the freedom to be ambiguous, contextual, and changed at will. Picking one column to carry both makes the column a bad fit for both jobs. The fix is to give each property its own column: a `Guid` for stable identity, a `display_name` for presentation, and the membership graph for addressing.

## Decision

### 1. Every actor has one identity: a `Guid`

Every actor — unit, agent, human, connector, tenant — has exactly one stable identifier: a `Guid`. The `Guid` is the primary key, the foreign-key target, the activity-log source, the wire-form identity, and the manifest cross-reference token. There is no parallel string identifier with equal status. Within a single actor's lifetime the `Guid` does not change, ever; rename a unit, move an agent, swap a connector — the `Guid` is the same.

Rejected: typed wrappers (`AgentId`, `UnitId`, `TenantId` as separate value types). Considered briefly during the implementation pass for #1629; the cost is real (every cast site, every conversion, every EF Core type configuration, every JSON converter, every Kiota-generated client) and the gain is the kind of compile-time check that already trips at the message-receiver boundary — schemes are checked at address parse time. `Guid` end-to-end is the simplest contract that does the job.

### 2. `display_name` is presentation-only

Every actor has a `display_name` string for human-facing rendering — wizard listings, activity-log narrative text, drawer panels, CLI table output. The `display_name`:

- **Is not unique.** Two agents in a tenant may share a `display_name`. Two humans may share a `display_name`. The system disambiguates by Guid, not by name.
- **Is not addressable.** No URL, no CLI verb, no manifest reference, and no API endpoint accepts a `display_name` as the canonical handle. CLI surfaces *do* accept `display_name` as **search input** — see decision 6 — but search is 0/1/n semantics, not a routed lookup.
- **Is not a foreign-key target.** No table joins on `display_name`. No row stores another row's `display_name` as its reference; cross-row references store the foreign actor's `Guid`.
- **Cannot itself parse as a Guid.** A `display_name` that round-trips through `Guid.TryParse` is a validation failure at write time; the rule is enforced by the validator landing under [#1632](https://github.com/cvoya-com/spring-voyage/issues/1632). This protects the parse hierarchy at every input surface (address parser, CLI argument parser, manifest reference parser): a token that looks Guid-shaped is unambiguously identity.

### 3. Slugs do not exist

There are no slug columns in the persistence layer. There are no slug forms in the routing layer. There are no slug forms in the activity log. The slug-bearing legacy URL shape (`/agents/team/alice`, `agent://team/alice`) is gone; URLs and addresses carry Guids in their canonical wire form. Manifests do not declare slugs; they declare local symbols within a file (decision 7) and resolve cross-package references by Guid.

Rejected: keeping a slug column "for SEO" or "for vanity URLs." That is a presentation-layer feature. If a future surface wants stable per-tenant short URLs, it can layer them above the canonical Guid-keyed routes (a redirector keyed on a tenant-scoped table whose presence is purely cosmetic). The mistake the prior shape made was elevating that cosmetic table to identity status.

### 4. Membership graph is the addressing fabric

Membership is stored as `(tenant_id, parent_id, child_id)` triples in `UnitMembershipEntity` (agent → unit) and `UnitSubunitMembershipEntity` (unit → unit). The tenant row itself is a node in the graph: top-level units appear as membership rows where `parent_id = tenant.id`. There is no separate `is_top_level` boolean and no separate "root collection." Walking the membership graph from any node toward the tenant is the canonical way to compute a path, an ancestry chain, or a permission walk.

The directory ([ADR 0023](0023-flat-actor-ids.md)) resolves a `Guid` to a flat Dapr actor id in one lookup; the membership graph is what the permission walk traverses at resolution time. Single-hop dispatch is preserved unchanged.

### 5. Public Guid wire form: 32-char lowercase no-dash hex

The canonical wire form for a Guid on every public surface (URLs, JSON DTOs, manifest references, CLI output, log entries) is `Guid.ToString("N")` — 32 lowercase hex characters, no dashes, no braces. `GuidFormatter.Format` is the one helper; it does not surface configuration knobs.

Parsers are lenient. `GuidFormatter.TryParse` (and every input surface that uses it) accepts the no-dash form, the conventional dashed form, the braced form, and any other form `Guid.TryParse` recognises. The asymmetric rule — emit one form, parse many — keeps copy-paste workflows working (operators paste Guids out of dashboards, GitHub issues, log lines) while eliminating rendering ambiguity at the source.

### 6. Address shape: `(Scheme, Guid Id)`

`Address` is a record with two fields: `Scheme` (e.g. `agent`, `unit`, `human`, `connector`) and `Id` (Guid). The wire form is `scheme:<32-hex-no-dash>` — for example `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`. There is no path form, no navigation form, no `scheme://path/segment/segment`. Convenience accessors (`Address.Path` returning the no-dash hex) exist for callers that need a string actor key, but the canonical render is always the `scheme:id` form.

CLI search-by-name surfaces (`spring agent show alice`) treat `alice` as a `display_name` search expression and return 0, 1, or n results; n-match returns a disambiguation table keyed on Guid. The same surface accepts a Guid form and skips the search; a token that parses as a Guid is treated as identity, never as a name (this is what decision 2's "display_name cannot parse as a Guid" rule protects).

### 7. Manifest grammar: local symbols within a file; Guids across packages

Inside a single manifest file, references are local symbols (IaC-style — the symbol is defined elsewhere in the same file or expanded from package inputs). Cross-package references are Guids. The package install pipeline ([ADR 0035](0035-package-as-bundling-unit.md)) mints a Guid per artefact at install time and threads it through the package's local symbol table; cross-package references in manifest text resolve to those minted Guids via the catalog.

There is no slug-shaped manifest reference and no name-shaped manifest reference. A reference is either a local symbol (resolved within the file) or a Guid (resolved against the catalog). PR7 of #1629 lands the parser changes that enforce this.

### 8. OSS default tenant id is a v5 UUID, pinned as a constant

`OssTenantIds.Default` is the deterministic v5 UUID derived from namespace `00000000-0000-0000-0000-000000000000` and label `cvoya/tenant/oss-default`, computed once and pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs`. The value is `dd55c4ea-8d72-5e43-a9df-88d07af02b69` (no-dash form: `dd55c4ea8d725e43a9df88d07af02b69`). Both the dashed and no-dash strings are exposed as `const string` literals on the same class for grep-ability across configs, dashboards, and audit trails.

Rejected: `Guid.Empty`. Reserved by convention for "uninitialised / programmer error" — using it as a real sentinel breaks every check that says "did this row get a tenant id?"

Rejected: a pattern-shaped Guid like `00000000-0000-0000-0000-000000000001`. Claims a chunk of low-numbered Guid space for one decision; provides no provenance; encourages the next sentinel to grab `…000000000002` and the one after that to grab `…000000000003`, until the system is full of magic numbers nobody can derive without reading source.

The v5 UUID approach is recomputable from outside the platform (any v5 implementation against the same namespace + label produces the same Guid), self-documenting (the label is the documentation), and collision-free against random Guid generation (the v5 namespace is distinct from v4's random space).

## Consequences

### URLs and CLI are stable; search has explicit n-result semantics

A URL or CLI verb that takes a `Guid` returns exactly the actor that has that `Guid`, regardless of rename, re-parent, or reorganisation. A URL or CLI verb that takes a `display_name` runs a search and returns 0, 1, or n results — the n-result case shows a disambiguation table; the operator picks one. The two semantics are clearly distinguished at the surface and never silently swap.

### Activity log is rename-safe by construction

Activity-log entries store the source actor's `Guid`. Display rendering happens at read time via `IDirectoryService` (live lookup) or `IParticipantDisplayNameResolver` (cached read-time resolution). When an actor is renamed, every historical activity row immediately renders with the new name. When an actor is soft-deleted, the resolver snapshots the `display_name` at the moment of deletion onto the activity row so the audit history continues to render meaningfully even after the actor is gone — the snapshot is the only place the activity log ever stores a name, and only as a tombstone.

### Manifest grammar is uniform across packages

Local references stay readable (`subUnit: sv-oss-design`, `agent: architect`). Cross-package references are explicit Guids. There is no rule like "you can name an artefact in the same package by its display name but not across packages" — the rule is "local references resolve within the file; cross-package references are Guids." Operators learn one rule.

### `display_name` validation is a real surface

Forbidding `display_name` from parsing as a Guid is a validation rule, not a convention. The validator runs at every write surface (manifest install, wizard create, CLI rename, API PATCH). A `display_name` of `alice` passes; a `display_name` of `8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` is rejected with a structured error. The rule is implemented under [#1632](https://github.com/cvoya-com/spring-voyage/issues/1632).

### Address shape collapse is a one-time wire change

Pre-#1637 wire forms (`agent://team/alice`) do not parse against the new `Address.TryParse`. There is no compatibility shim: every internal caller, every test fixture, every doc example was updated in #1637. External callers (CLI, portal, integrations) are pre-v0.1 and fall under the same "no migration" rule that ADR-0030 documented.

### Renames are free

A unit rename, an agent rename, a tenant rename — all of these are `display_name` updates. No foreign keys cascade. No paths recompute. No directory entries invalidate. No URL bookmarks break. The membership graph does not move. The activity log re-renders with the new name on next read.

### Schema reset for pre-#1637 dev databases

The single-identity baseline collapses every prior migration into one `InitialBaseline`. Dev / CI databases from before #1637 must be dropped and recreated; the operator playbook for the cutover is recorded in #1629's PR3 implementation notes. v0.1 has not shipped, so there is no live data to migrate.

### Dapr actor placement re-hashes

Dapr placement hashes the actor id; switching from slug-form ids to Guid-form ids re-distributes actors across the placement ring. This is a one-time cutover effect — dev state stores are dropped at the same moment as the relational schema reset.

## Alternatives considered

- **Entity-only slug (slug as a typed handle, kept as PK).** Considered in #1629 Comment 2's first half. Made the slug a typed value object instead of a bare string, preserved per-tenant uniqueness. Rejected: did not fix the multi-membership ambiguity. The same agent in two units still produced two slug-shaped addresses or one globally-unique one, both of which carried the leakage discussed in the Context section. Typing the slug improved compile-time safety but did not solve the modelling problem.

- **Edge-only slug (slug stored on the membership row, not the entity).** Considered as a corollary of the entity-only proposal. Stored a per-(parent, child) slug on every membership edge. Rejected: there is no contextual aliasing requirement — agents are the same agent everywhere; calling Alice `alice` in `team-a` and `the-architect` in `team-b` is a UX feature nobody asked for and a bug surface (audit ambiguity, mention-resolution ambiguity, role-confusion in cross-thread Timeline rendering) that the platform would now have to defend against.

- **Hybrid: entity slug + nullable edge override.** Considered in #1629 Comment 2's full proposal. Kept the entity slug as the canonical handle and added a nullable `slug_override` column on edges for contextual aliasing. Rejected after the user clarified (#1629 Comment 4) that names are not unique and identity is purely the `Guid`. The hybrid spent two columns and a `COALESCE` rule on a presentation-layer feature that the read-time `display_name` resolution already covers, and it preserved the audit-stability and rename-cascade pain that the slug-as-PK shape caused in the first place.

- **Slug-on-edge only (no entity slug).** Considered briefly between Comments 2 and 4 of #1629. Made the membership row the only place a slug existed; the entity carried only `display_name`. Rejected for the same reason as the hybrid — operators do not contextually rename actors per membership in real organisations, so the column was overhead without value. Once that became clear, eliminating slugs entirely (decision 3) was the simpler shape.

- **Pattern-sentinel Guid for the OSS default tenant id (`00000000-0000-0000-0000-000000000001`).** Considered for `OssTenantIds.Default` because the value reads obviously sentinel-shaped. Rejected: claims a chunk of low-numbered Guid space for a single decision, normalises the "magic constant" pattern for future sentinels, and provides no derivation — anyone reading the constant has to take it on faith. The v5 UUID over namespace + label is recomputable, self-documenting (the label is the documentation), and does not claim sentinel space.

- **`Guid.Empty` for the OSS default tenant id.** Considered as the cheapest possible sentinel. Rejected immediately: `Guid.Empty` is reserved by every nullability and initialisation convention for "uninitialised / programmer error." Reusing it as a real value collapses the distinction between "this row didn't get a tenant id" and "this row got the default tenant id" — exactly the failure mode that motivates having a real sentinel at all.
