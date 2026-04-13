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
| [0001](0001-web-portal-rendering-strategy.md) | Web portal rendering strategy (static export vs SSR) | Accepted — keep static export |
| [0002](0002-openapi-links-keyword.md) | OpenAPI `links` keyword vs plain URL fields | Deferred — revisit criteria recorded |

## Format

Each record has the following sections: **Status**, **Context**, **Decision**, **Consequences**, and (when the decision is time-bound) **Revisit criteria**. Keep the file to roughly one page — if it grows longer, the extra detail belongs in an architecture doc that the record links to.
