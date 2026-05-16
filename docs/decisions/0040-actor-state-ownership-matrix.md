# 0040 — Actor state ownership matrix

- **Status:** Accepted — 2026-05-09. **Superseded in part by the Tools wave ([#2332](https://github.com/cvoya-com/spring-voyage/issues/2332)).** The `agent_skill_grants` / `unit_skill_grants` tables introduced here have been reshaped into `agent_tool_grants` / `unit_tool_grants` with a `(namespace, tool_name, provenance)` shape that records where each grant came from (`explicit`, `connector:<slug>`, `platform`, `image:<digest>`); existing rows migrate forward as `provenance = "explicit"`. The rest of the matrix is unaffected. See [Tools](../concepts/tools.md) and the Tools-wave umbrella for the new shape. Sharpens the boundary set in [0022 — PostgreSQL as primary store](0022-postgres-as-primary-store.md): every state key currently held in Dapr actor / state-store storage gets a single, named, EF-or-actor home, governed by one rule. Configuration, authorization, budget, capability, membership, and policy data is EF-authoritative; Dapr actor state holds only runtime-ephemeral scratch.
- **Date:** 2026-05-09
- **Umbrella:** [#2032](https://github.com/cvoya-com/spring-voyage/issues/2032) — Harden state management boundaries before v0.1.
- **Related code:** `src/Cvoya.Spring.Dapr/Actors/StateKeys.cs`, `src/Cvoya.Spring.Dapr/Data/SpringDbContext.cs`, `src/Cvoya.Spring.Dapr/Auth/PermissionService.cs`, `src/Cvoya.Spring.Host.Api/Endpoints/BudgetEndpoints.cs`, `src/Cvoya.Spring.Dapr/Observability/ThreadQueryService.cs`, `src/Cvoya.Spring.Dapr/Units/UnitMembershipCoordinator.cs`.
- **Related ADRs:** [0022 — PostgreSQL as primary store](0022-postgres-as-primary-store.md) (the boundary this ADR sharpens); [0030 — Thread model](0030-thread-model.md) (the Thread Timeline that drives `threads` + `messages`); [0015 — Dapr as infrastructure runtime](0015-dapr-as-infrastructure-runtime.md) (why actors and state-store exist at all).

## Context

ADR-0022 says relational business data lives in EF/Postgres; actor runtime state goes through the Dapr state store. That boundary is the right north star, but the code has drifted. A live unit-message send recently failed with `403 Permission denied` because the Dapr Postgres state component had drifted between schemas (`public.state` vs `spring.state`); the unit's `Unit:HumanPermissions` actor-state map ended up in the wrong table and the permission check returned `null`. PR [#2029](https://github.com/cvoya-com/spring-voyage/pull/2029) pinned the table placement, but the underlying structural fault is that ACLs were being held in a state shape vulnerable to that class of incident at all. A wider audit of [`StateKeys.cs`](../../src/Cvoya.Spring.Dapr/Actors/StateKeys.cs) found that several other categories of authorization-critical, query-heavy, or operator-facing data live in Dapr actor / key-value state with no EF backing — budgets (with a literal `tenantId ?? "default"` fallback in `BudgetEndpoints`), the unit member graph (dual-stored against `unit_memberships`), agent and unit "live config" values, expertise, skill grants, connector bindings, and cloning policies.

Two distinct shapes were getting mixed:

1. **Configuration / business data.** Set by operators, read at authorization or routing time, queried across actors, and required to survive actor restarts without reconciliation. ACLs, budgets, membership, model / runtime / hosting selections, skill grants, expertise, connector bindings, cloning policies, thread identity, message history. This is exactly what EF Core + a tenant-scoped query filter is for.

2. **Runtime ephemeral scratch.** State that only matters while an actor is active and processing a turn. Mailbox slots, pending queues, in-flight checkpoints, reminder guards, stream cursors, supervisor handles, instance-local connector status. Losing it on a clean restart is acceptable; rebuilding it from EF on activation is the contract.

The drift produced concrete failures: the `403`, dual-storage hazards (`Unit:HumanPermissions` and `Human:UnitPermissions` for the same fact; `Unit:Members` and `unit_memberships` for the same edges), broken multi-tenancy for budgets, full-table activity-event scans in `ThreadQueryService` and `MessageQueryService`, and an O(depth) actor-proxy hierarchy walk in `PermissionService`. v0.1 has not shipped, so the fix can be a clean re-shape rather than a migration.

## Decision

### 1. One rule for every state key

Every datum the platform persists has exactly one authoritative home. The home is determined by a single classification:

> **If the datum is configuration, authorization, budget, capability, membership, identity, policy, or query-shaped, it is EF-authoritative.**
> **If the datum is runtime ephemeral scratch — only meaningful while an actor is active — it stays in the Dapr state store via the actor's `IActorStateManager`.**
> **If the datum is computable from EF authoritatively at read time, it is dropped from actor state.**

Actor state may keep a *warm cache* of EF-authoritative data for hot-path performance, but the cache is rebuilt from EF on `OnActivateAsync` and is never the write target. Writes always go to EF.

### 2. The canonical state-ownership matrix

The matrix below covers every key currently in [`StateKeys.cs`](../../src/Cvoya.Spring.Dapr/Actors/StateKeys.cs). The "Disposition" column is normative for v0.1.

#### Stay (runtime ephemeral; only valid while actor is active)

| Key | Stored on | Reason |
|---|---|---|
| `Agent:ActiveThread` | `AgentActor` | Active mailbox slot; meaningless after deactivation. |
| `Agent:PendingConversations` | `AgentActor` | Pending mailbox queue; rebuilt from inbound messages, not preserved across cluster restart. |
| `Agent:ObservationChannel` | `AgentActor` | Batched transient observations awaiting consolidation. |
| `Agent:Checkpoint:{ThreadId}` | `AgentActor` | Per-thread in-flight computation checkpoint. |
| `Agent:InitiativeState` | `AgentActor` | Initiative loop intermediate state. |
| `Agent:InitiativeReminderRegistered` | `AgentActor` | Reminder-registration guard; re-evaluated on activation. |
| `Agent:PendingAmendments` | `AgentActor` | Mid-flight amendments queue; consumed between tool calls (#142). |
| `Agent:Paused` | `AgentActor` | "Paused awaiting clarification" flag (#142). |
| `Agent:CloneIdentity` | `AgentActor` | Clone provenance for the lifetime of the clone. |
| `Agent:CloneChildren` | `AgentActor` | Parent's list of live clone children. |
| `Agent:StreamSequence` | `AgentActor` | Last-processed stream-event sequence cursor. |
| `Agent:StreamConfig` | `AgentActor` | Streaming configuration used while invoking. |
| `Unit:DirectoryCache` | `UnitActor` | In-memory address-resolution cache. |
| `Unit:Status` | `UnitActor` | Unit lifecycle status (Draft / Validating / Running / Stopped / Error). v0.2 candidate for EF if cross-unit status query is needed; not v0.1. |
| `Connector:Status` | `UnitActor` | Instance-local runtime connection health. |
| `Connector:Type` | `UnitActor` | Instance-local runtime tag (the durable type identifier lives in `unit_connector_bindings`). |
| `Connector:Config` | `UnitActor` | Instance-local runtime config snapshot (durable copy in `unit_connector_bindings`). |
| `Supervisor:State` | `ContainerSupervisorActor` | Container supervision handles, restart counter; instance-specific. |
| `Human:LastReadAt` | `HumanActor` | Per-thread read cursors. v0.2 candidate for EF backing; v0.1 keeps in actor state because losing it resets unread counts (low-stakes UX) rather than corrupting data. |

#### Move to EF (configuration / authorization / business data)

| Key | EF destination | Notes |
|---|---|---|
| `Unit:HumanPermissions` | `unit_human_permissions` (new) | Tenant-scoped ACL table; replaces both this key and `Human:UnitPermissions`. `PermissionService` queries EF directly — no per-hop actor proxy. |
| `Agent:CostBudget`, `Unit:CostBudget`, `Tenant:CostBudget` | `budget_limits` (new) | Single tenant-scoped table with `(scope_type, scope_id)` discriminator. `BudgetEndpoints` routes through `ITenantContext` / `OssTenantIds.Default` — no `tenantId ?? "default"`. |
| `Human:Identity` | `humans.username` (existing) | Already in EF; the actor-state copy is removed entirely. |
| `Human:Permission` | `humans.permission_level` (extension) | New column on the existing `humans` table. |
| `Human:NotificationPreferences` | `humans.notification_preferences` (extension) | New `jsonb` column on the existing `humans` table. |
| `Unit:Members` | `unit_memberships`, `unit_subunit_memberships` (existing) | EF becomes the sole writer; `UnitActor` rebuilds the in-memory members list from EF on activation. The startup reconciliation path is deleted. |
| `Agent:ParentUnit` | `unit_memberships` (existing) | The membership row is the source of truth; the actor-state pointer is removed. |
| `Unit:Policies` | `unit_policies` (existing) | The actor-state copy is dropped; `UnitPolicyEntity` is the only write path. |
| `Agent:Model`, `Agent:Specialty`, `Agent:Enabled`, `Agent:ExecutionMode` | `agent_live_config` (new, 1:1 with agent) | Operator-editable runtime values, distinct from `agent_definitions` (the package/YAML template). |
| `Agent:Skills` | `agent_skill_grants` (new) | One row per granted skill; uniqueness on `(tenant_id, agent_id, skill_name)`. |
| `Agent:Expertise` | `agent_expertise` (new) | One row per expertise domain. |
| `Unit:Model`, `Unit:Color`, `Unit:Provider`, `Unit:Hosting`, `Unit:Boundary`, `Unit:PermissionInheritance` | `unit_live_config` (new, 1:1 with unit) | Operator-editable runtime values, distinct from `unit_definitions` (the package/YAML template). `Unit:PermissionInheritance` lives here so `PermissionService` reads it from the same EF query that resolves the inheritance walk. |
| `Unit:OwnExpertise` | `unit_expertise` (new) | One row per expertise domain. |
| `Unit:ConnectorBinding`, `Unit:ConnectorMetadata` | `unit_connector_bindings` (new) | Durable connector binding (`connector_type`, `config jsonb`, `metadata jsonb`). The instance-local `Connector:Status/Type/Config` keys above are runtime mirrors, rebuilt at activation. |
| `Agent:CloningPolicy`, `Tenant:CloningPolicy` | `cloning_policies` (new) | Single tenant-scoped table with `(scope_type, scope_id)` discriminator. Replaces `StateStoreAgentCloningPolicyRepository`. |
| Thread identity (newly required by ADR-0030 and currently absent) | `threads` (new) | Participant-set keyed registry. `IThreadRegistry.GetOrCreateAsync` is the single allocation point; `MessageEndpoints` consults it for every domain send. Closes the [#2034](https://github.com/cvoya-com/spring-voyage/issues/2034) bug where unit-targeted sends produced `CorrelationId = null`. |
| Message history (newly required by ADR-0030 and currently scanned out of `activity_events`) | `messages` (new) | Per-message row keyed by `thread_id`. The dispatcher writes to this table at send time. `ThreadQueryService` and `MessageQueryService` query relational data; the `ExtractMessageEnvelope` JSON-parsing path in [`ThreadQueryService.cs`](../../src/Cvoya.Spring.Dapr/Observability/ThreadQueryService.cs) is deleted. |

#### Drop (redundant or computable from EF)

| Key | Reason |
|---|---|
| `Human:UnitPermissions` | Redundant dual-view of `Unit:HumanPermissions`; `unit_human_permissions` is the single source. |
| `Agent:CostTotal` | Computable as `SUM(cost) FROM cost_records WHERE agent_id = …`; the actor-state running total can silently diverge from the EF audit log. |
| `Agent:Definition` | Cloning re-reads from `agent_definitions` in EF directly. The actor-state copy was a transient artefact of the old cloning workflow. |
| `Unit:Definition` | Same as `Agent:Definition` for unit cloning. |

### 3. Activation reads from EF, with timing metrics

When an actor activates, any in-memory configuration the hot path needs (member list, model selection, skill grants, expertise) is read from EF in `OnActivateAsync`. Each read is wrapped in a timing instrument (logged metric or OpenTelemetry counter). The metrics are the input for a v0.2 cache-optimisation decision: if activation latency turns out to be a problem, a Dapr-state-store-backed warm cache for hot keys can be introduced behind the same actor-state interface. v0.1 ships without that optimisation deliberately — the simpler shape (one read, no cache) makes the boundary unambiguous and is the right baseline against which the optimisation can be measured.

`Human:LastReadAt` is the one *non*-trivial v0.1 exception to the EF-authoritative rule (see the matrix above). The justification is operational, not architectural: read-cursor writes happen on every inbox open, the loss-of-data consequence is "unread counts reset," and the v0.1 EF schema does not need to grow another table for a UI affordance. A v0.2 issue tracks moving it to EF (`thread_read_cursors`) once the rest of the boundary is settled.

### 4. The OSS extension contract is preserved

Every new table added by this ADR is tenant-scoped via `ITenantScopedEntity` + a `HasQueryFilter(e => e.TenantId == tenantContext.CurrentTenantId)` clause in `IEntityTypeConfiguration<T>`. Cross-tenant reads continue to require `ITenantScopeBypass.BeginBypass(reason)` per [`CONVENTIONS.md`](../../CONVENTIONS.md) § 13. Multi-tenant correctness ceases to depend on whether an actor was activated under the right tenant: the EF query filter is the gate, regardless of which actor reads or writes.

The cloud overlay swaps `EfThreadRegistry`, `EfAgentCloningPolicyRepository`, and any other EF-backed implementations through the same `TryAdd*` registration seams; the OSS surface stays interface-first per [`AGENTS.md`](../../AGENTS.md).

## Consequences

**Easier:**

- One sentence answers "where does X live?" for any key in `StateKeys.cs`. The matrix is normative; new state keys must take a side and justify it.
- The 403-class incident is structurally not reproducible: ACL writes go through EF transactions, not Dapr state-store table placement.
- `PermissionService` becomes a single SQL read per call instead of an O(depth) actor-proxy walk that cold-activates ancestors.
- `ThreadQueryService` and `MessageQueryService` drop their JSON-parsing paths and become indexed SQL queries; inbox listing stops doing a full table scan of `activity_events`.
- Multi-tenant correctness for budgets stops depending on a hardcoded `"default"` string.
- Cloning re-reads templates from EF instead of carrying transient actor-state copies, removing an entire class of cloning-state-drift bugs.

**Harder:**

- Every actor activation does at least one EF read for the configuration it caches in memory. Activation latency is longer than the previous "all-in actor state" path. The decision accepts this for v0.1 and instruments the read so the v0.2 cache decision is data-driven.
- Several PRs touch `AgentActor`, `UnitActor`, `HumanActor`, `UnitMembershipCoordinator`, and the coordinator implementations — the per-issue PR boundaries are designed so each lands self-contained, but the actor surfaces churn during the rollout.
- Operators who previously inspected `dapr_state.state` in Postgres for diagnostic spelunking will find configuration, ACLs, budgets, membership, and metadata under the EF schema instead. `dapr_state.state` keeps the runtime-ephemeral scratch and nothing else.

**Not abstracted (deliberately):**

- A read-through cache layer between actors and EF is *not* introduced in v0.1. The simpler "read on activation, cache in memory, reload on next activation" shape is the v0.1 baseline; the cache decision is deferred to v0.2 with metrics in hand.
- Secret-blob isolation (separating encrypted secret payloads onto a dedicated Dapr state component instead of the shared `statestore`) is acknowledged in [#2032](https://github.com/cvoya-com/spring-voyage/issues/2032) but tracked as a follow-up; this ADR does not change secrets-component placement.
- Versioned actor state envelopes (a `schemaVersion` on each persisted runtime-ephemeral payload) are valuable and a v0.2 candidate, but they are not on the critical path for the boundary correction this ADR delivers.
- Durable persistence of every event currently flowing through the in-memory `IActivityEventBus` is a separate design question (an outbox pattern between the bus and EF). This ADR does not change the bus contract; it does require that thread / message identity is durable through EF, which removes the most pressing UI-correctness driver for that broader change.

## Implementation tracking

This ADR is implemented as an issue series under [#2032](https://github.com/cvoya-com/spring-voyage/issues/2032). Each issue is sized to a single PR and lands the matrix slice it owns:

- [#2044](https://github.com/cvoya-com/spring-voyage/issues/2044) — `unit_human_permissions`; `PermissionService` rewrite.
- [#2045](https://github.com/cvoya-com/spring-voyage/issues/2045) — `budget_limits`; `BudgetEndpoints` rewrite; tenancy fallback fix.
- [#2046](https://github.com/cvoya-com/spring-voyage/issues/2046) — `humans` extension (permission, notification preferences); `Human:Identity` removal.
- [#2047](https://github.com/cvoya-com/spring-voyage/issues/2047) — `IThreadRegistry` + `threads` table; `MessageEndpoints` fix (closes [#2034](https://github.com/cvoya-com/spring-voyage/issues/2034)).
- [#2048](https://github.com/cvoya-com/spring-voyage/issues/2048) — `agent_live_config`, `agent_skill_grants`, `agent_expertise`; `AgentActor` rewrite; `Agent:CostTotal` and `Agent:Definition` drops.
- [#2049](https://github.com/cvoya-com/spring-voyage/issues/2049) — `unit_live_config`, `unit_expertise`; `UnitActor` rewrite; `Unit:Policies` collapse; `Unit:Definition` drop.
- [#2050](https://github.com/cvoya-com/spring-voyage/issues/2050) — `unit_connector_bindings`.
- [#2051](https://github.com/cvoya-com/spring-voyage/issues/2051) — `cloning_policies`; `EfAgentCloningPolicyRepository`.
- [#2052](https://github.com/cvoya-com/spring-voyage/issues/2052) — `unit_memberships` authority; top-level unit model.
- [#2053](https://github.com/cvoya-com/spring-voyage/issues/2053) — `messages` table; dispatch-time write.
- [#2054](https://github.com/cvoya-com/spring-voyage/issues/2054) — `ThreadQueryService` / `MessageQueryService` rewrite.

The series can land in two streams that converge once the table additions are in: the security/tenancy slice (#2044–#2046), independent; the thread-identity slice (#2047), independent; the configuration-metadata slice (#2048–#2052), serialised because they share `AgentActor` / `UnitActor` files; and the message-persistence slice (#2053–#2054), blocked by #2047.
