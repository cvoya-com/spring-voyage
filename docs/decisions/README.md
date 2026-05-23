# Decision Records

Short, dated records of decisions that lock in a specific trade-off — kept
alongside the code so the reasoning survives contributor churn.

Reach for an ADR when you want the **why** behind a choice: "why not the obvious
alternative?", or "what triggers a revisit?". For the current **what** — how the
system works today — start from the [architecture docs](../architecture/README.md).

When a record's decision is later reversed, narrowed, or restated, it moves to
[`archive/`](archive/README.md) — never deleted. Two **re-baseline** records,
[0053](0053-units-are-agents-and-one-way-delivery.md) and
[0054](0054-one-mcp-server-one-execution-host.md), restate designs that had
spread across long supersession chains; the chains they replaced are in the
archive.

For open design questions not yet decided, see
[`../architecture/open-questions.md`](../architecture/open-questions.md).

## Index

| # | Title | Status |
|---|-------|--------|
| [0002](0002-openapi-links-keyword.md) | OpenAPI `links` keyword vs plain URL fields | Deferred |
| [0003](0003-secret-inheritance-unit-to-tenant.md) | Secret inheritance (Unit → Tenant) | Accepted |
| [0004](0004-per-agent-secrets.md) | Per-agent secrets — unit stays the trust boundary | Deferred |
| [0005](0005-portal-standalone-mode.md) | Web portal runs in Next.js `standalone` mode | Accepted |
| [0006](0006-expertise-directory-aggregation.md) | Recursive expertise directory aggregation | Accepted |
| [0008](0008-unit-boundary-decorator.md) | Unit boundary as a decorator over the expertise aggregator | Accepted |
| [0011](0011-persistent-agent-lifecycle-http-surface.md) | Persistent-agent lifecycle HTTP surface | Accepted |
| [0012](0012-spring-dispatcher-service-extraction.md) | `spring-dispatcher` owns the container runtime | Accepted |
| [0013](0013-hierarchy-aware-permission-resolution.md) | Hierarchy-aware permission resolution | Accepted |
| [0014](0014-skill-invoker-seam.md) | `ISkillInvoker` seam between skill callers and routing | Accepted |
| [0015](0015-dapr-as-infrastructure-runtime.md) | Dapr as the infrastructure runtime | Accepted |
| [0016](0016-net-for-infrastructure-layer.md) | .NET for the platform infrastructure layer | Accepted |
| [0017](0017-unit-is-an-agent-composite.md) | A unit IS an agent (composite pattern) | Accepted |
| [0019](0019-workflow-as-container.md) | Domain workflows run as containers | Accepted |
| [0020](0020-tiered-cognition-for-initiative.md) | Two-tier cognition model for initiative | Accepted |
| [0021](0021-spring-voyage-is-not-an-agent-runtime.md) | Spring Voyage is not an agent runtime | Accepted |
| [0022](0022-postgres-as-primary-store.md) | PostgreSQL as primary store; Dapr state for actor state | Accepted |
| [0023](0023-flat-actor-ids.md) | Flat actor ids; single-hop routing | Accepted — amended by [0036](0036-single-identity-model.md) |
| [0024](0024-unit-validation-as-dapr-workflow.md) | Unit validation runs as a Dapr Workflow | Accepted |
| [0025](0025-unified-agent-launch-contract.md) | Unified agent launch contract | Accepted |
| [0026](0026-per-agent-container-scope.md) | Per-agent container scope | Accepted |
| [0027](0027-agent-image-conformance-contract.md) | Agent-image conformance contract (A2A 0.3.x) | Accepted |
| [0028](0028-tenant-scoped-runtime-topology.md) | Tenant-scoped runtime topology | Accepted — amended |
| [0029](0029-tenant-execution-boundary.md) | Tenant execution boundary | Accepted |
| [0030](0030-thread-model.md) | Thread model: participant-set identity, single AgentMemory | Accepted — supersedes archived 0018 |
| [0031](0031-kiota-nullable-oneof-tracking.md) | Kiota nullable-oneOf / discriminator tracking | Tracking (upstream) |
| [0032](0032-drawer-panel-extension-slot.md) | Drawer-panel extension slot pattern | Accepted |
| [0033](0033-two-portal-architecture.md) | Two-portal architecture (Management + Engagement) | Accepted |
| [0034](0034-oss-dogfooding-unit.md) | Spring Voyage OSS dogfooding unit | Accepted |
| [0035](0035-package-as-bundling-unit.md) | Package as the unit of bundling, install, export | Accepted |
| [0036](0036-single-identity-model.md) | Single-identity model: Guid identity, `display_name` presentation-only | Accepted |
| [0037](0037-package-schema-decomposition.md) | Package schema decomposition (kind-discriminated YAMLs) | Accepted |
| [0038](0038-agent-runtime-and-model-provider-split.md) | AgentRuntime and ModelProvider as separate identities | Accepted |
| [0040](0040-actor-state-ownership-matrix.md) | Actor state ownership matrix | Accepted — skill-grant tables reshaped by the Tools wave |
| [0041](0041-actor-runtime-contract.md) | Actor-runtime contract (per-thread resume + concurrent threads) | Accepted |
| [0042](0042-local-operator-installer.md) | Local-host operator installer (`install.sh`) | Accepted |
| [0043](0043-recursive-package-format.md) | Recursive package format: every artefact is a folder | Accepted — amended by [0046](0046-unified-members-grammar.md) |
| [0044](0044-team-role-vs-platform-role.md) | Team role vs. platform role; package-declared human members | Accepted — §§2/3/5 superseded by [0046](0046-unified-members-grammar.md) |
| [0045](0045-connector-domain-agnostic-platform.md) | Connectors facilitate flow; they do not replicate upstream config | Accepted |
| [0046](0046-unified-members-grammar.md) | Unified `members:` grammar; humans as a member kind; `HumanTemplate` | Accepted |
| [0047](0047-platform-user-human-split.md) | TenantUser / human split; connector identity on the tenant user | Accepted |
| [0053](0053-units-are-agents-and-one-way-delivery.md) | **Re-baseline** — units are agents; the platform delivers one-way messages | Accepted |
| [0054](0054-one-mcp-server-one-execution-host.md) | **Re-baseline** — one platform MCP server, one execution host | Accepted |
| [0055](0055-pull-based-agent-bootstrap.md) | Pull-based agent bootstrap and workspace delivery | Proposed |

Archived records are listed in [`archive/README.md`](archive/README.md).

## Format

Each record has: **Status**, **Context**, **Decision**, **Consequences**, and —
when the decision is time-bound — **Revisit criteria**. Keep a record to roughly
one page; if it grows past that, the extra detail belongs in an architecture doc
the record links to. A **re-baseline** record may run longer because it
consolidates several earlier records into one current statement.
