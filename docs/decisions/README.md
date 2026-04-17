# Decision Records

Short, dated records of decisions that lock in a specific trade-off — kept alongside the code so the reasoning survives contributor churn. These are intentionally narrow: one decision per file, one page per decision, with explicit revisit criteria when the decision is time-bound.

Use these when:

- A decision affects more than one subsystem and needs to be discoverable from either side.
- The reasoning ("why not the obvious alternative?") will otherwise be forgotten.
- The decision is deferred pending an ecosystem signal — the record captures the trigger conditions.

For the high-level architectural "why" behind the platform as a whole, see [`../design-decisions.md`](../design-decisions.md). For open design questions that have **not** yet been decided, see [`../architecture/open-questions.md`](../architecture/open-questions.md).

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

## Format

Each record has the following sections: **Status**, **Context**, **Decision**, **Consequences**, and (when the decision is time-bound) **Revisit criteria**. Keep the file to roughly one page — if it grows longer, the extra detail belongs in an architecture doc that the record links to.
