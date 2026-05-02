# Decision Records

Short, dated records of decisions that lock in a specific trade-off — kept alongside the code so the reasoning survives contributor churn. The lower-numbered records (0001–0014) capture narrow per-feature trade-offs that arrived alongside specific PRs / issues; the 0015+ block captures the foundational platform-stack decisions that frame everything else.

Use these when:

- A decision affects more than one subsystem and needs to be discoverable from either side.
- The reasoning ("why not the obvious alternative?") will otherwise be forgotten.
- The decision is deferred pending an ecosystem signal — the record captures the trigger conditions.

For open design questions that have **not** yet been decided, see [`../architecture/open-questions.md`](../architecture/open-questions.md).

## Index

| # | Title | Status |
|---|-------|--------|
| [0001](0001-web-portal-rendering-strategy.md) | Web portal rendering strategy (static export vs SSR) | Superseded by [0005](0005-portal-standalone-mode.md) |
| [0002](0002-openapi-links-keyword.md) | OpenAPI `links` keyword vs plain URL fields | Deferred — revisit criteria recorded |
| [0003](0003-secret-inheritance-unit-to-tenant.md) | Secret inheritance semantics (Unit → Tenant) | Accepted — automatic fall-through with opt-out |
| [0004](0004-per-agent-secrets.md) | Per-agent secrets: storage scope vs ACL vs status quo | Deferred — unit remains the trust boundary |
| [0005](0005-portal-standalone-mode.md) | Web portal runs in Next.js `standalone` mode | Accepted — `output: "standalone"` |
| [0006](0006-expertise-directory-aggregation.md) | Recursive expertise directory aggregation | Accepted — single aggregator, path+origin on every entry |
| [0007](0007-label-routing-match-semantics.md) | Label-routing match semantics for `LabelRoutedOrchestrationStrategy` | Accepted — case-insensitive set intersection, first payload label wins |
| [0008](0008-unit-boundary-decorator.md) | Unit boundary as decorator over the expertise aggregator | Accepted — caller-aware decorator, opacity/projection/synthesis |
| [0009](0009-github-label-roundtrip-via-activity-event.md) | GitHub label roundtrip wired via activity-event subscription | Accepted — `LabelRouted` DecisionMade event; hosted-service subscriber |
| [0010](0010-manifest-orchestration-strategy-selector.md) | Manifest-driven orchestration-strategy selection resolves per message | Accepted — manifest key > policy inference > unkeyed default; per-message scope |
| [0011](0011-persistent-agent-lifecycle-http-surface.md) | Persistent-agent lifecycle HTTP surface (deploy/scale/logs/undeploy) | Accepted — dedicated `PersistentAgentLifecycle` service, CLI verbs on top |
| [0012](0012-spring-dispatcher-service-extraction.md) | Extract container-runtime ownership into `spring-dispatcher` | Accepted — HTTP-fronted service; worker binds `DispatcherClientContainerRuntime` |
| [0013](0013-hierarchy-aware-permission-resolution.md) | Hierarchy-aware permission resolution (inherit-by-default, nearest-grant-wins, fail-closed) | Accepted — `UnitPermissionInheritance` flag, `Isolated` opt-out |
| [0014](0014-skill-invoker-seam.md) | `ISkillInvoker` seam between skill callers and the router | Accepted — protocol-agnostic seam; default routes via `IMessageRouter` |
| [0015](0015-dapr-as-infrastructure-runtime.md) | Dapr as the infrastructure runtime for actors / pub-sub / state / workflows | Accepted — pluggable backends, virtual actors, sidecar pattern |
| [0016](0016-net-for-infrastructure-layer.md) | .NET 10 / C# for the platform infrastructure layer | Accepted — type safety where it matters, mature Dapr SDK, language-agnostic agents |
| [0017](0017-unit-is-an-agent-composite.md) | A Unit IS an Agent (composite pattern) | Accepted — recursive composition, single dispatch path |
| [0018](0018-partitioned-mailbox.md) | Three-channel partitioned mailbox per agent (control / conversation / observation) | Superseded by [0030](0030-thread-model.md) |
| [0019](0019-workflow-as-container.md) | Domain workflows run as containers, not in-process | Accepted — decoupled releases, in-flight safety |
| [0020](0020-tiered-cognition-for-initiative.md) | Two-tier cognition model for initiative | Accepted — Tier 1 screens, Tier 2 reflects only on `Act` verdicts |
| [0021](0021-spring-voyage-is-not-an-agent-runtime.md) | Spring Voyage is not an agent runtime | Accepted — coordinate external runtimes, no in-platform tool-use loop |
| [0022](0022-postgres-as-primary-store.md) | PostgreSQL as primary store; Dapr state store for actor runtime state | Accepted — relational data via EF Core; actor state via Dapr abstraction |
| [0023](0023-flat-actor-ids.md) | Flat actor ids; single-hop routing with directory resolution | Accepted — O(path) permission walk, single dispatch hop |
| [0024](0024-unit-validation-as-dapr-workflow.md) | Unit validation runs as a Dapr Workflow, not as an actor | Accepted — `UnitValidationWorkflow` + in-container probe activities ship in #941 |
| [0025](0025-unified-agent-launch-contract.md) | Unified agent launch contract (single dispatch path, response-capture as a property) | Accepted — `AgentLaunchSpec` + single A2A path; ephemeral is a retention policy |
| [0026](0026-per-agent-container-scope.md) | Per-agent container scope (one container per agent, not per unit) | Accepted — `Pooled` reserved for [#362](https://github.com/cvoya-com/spring-voyage/issues/362) |
| [0027](0027-agent-image-conformance-contract.md) | Agent-image conformance contract (A2A 0.3.x on `:8999`, three conformance paths) | Accepted — bridge ships via OCI base, npm, and SEA binary |
| [0029](0029-tenant-execution-boundary.md) | Tenant execution boundary and agent runtime execution contract | Accepted — v0.1; two directional buckets, A2A 0.3.x wire, per-agent volume |
| [0030](0030-thread-model.md) | Thread model: participant-set identity, single AgentMemory, per-thread visibility policy | Accepted — supersedes [0018](0018-partitioned-mailbox.md) |
| [0032](0032-drawer-panel-extension-slot.md) | Drawer-panel extension slot pattern (contract, ordering, CLI parity) | Accepted — `DrawerPanel` contract + ordering + CLI parity rule |
| [0033](0033-two-portal-architecture.md) | Two-portal architecture (Management Portal + Engagement Portal over the same Web API) | Accepted — separate parent routes, shared session + API client, no portal-private API, CLI parity mandatory |
| [0034](0034-oss-dogfooding-unit.md) | Spring Voyage OSS dogfooding unit (role decomposition, image strategy, hosting, identity) | Accepted — four role sub-units, omnibus-derivative images, `permanent` hosting, atomic GitHub-App binding |
| [0035](0035-package-as-bundling-unit.md) | Package as the unit of bundling, installation, and export | Accepted — one concept, two-phase atomic install, uniform cross-package composition, global catalog / tenant-scoped installs, multi-package batch |

## Format

Each record has the following sections: **Status**, **Context**, **Decision**, **Consequences**, and (when the decision is time-bound) **Revisit criteria**. Keep the file to roughly one page — if it grows longer, the extra detail belongs in an architecture doc that the record links to.
