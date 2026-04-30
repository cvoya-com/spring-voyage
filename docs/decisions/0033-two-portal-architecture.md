# 0033 — Two-portal architecture (Management Portal + Engagement Portal over the same Web API)

- **Status:** Accepted (2026-04-29). v0.1 work.
- **Date:** 2026-04-29
- **Related docs:** [`docs/plan/v0.1/areas/e2-new-ux.md`](../plan/v0.1/areas/e2-new-ux.md) (the E2 area narrative); [`docs/architecture/thread-model.md`](../architecture/thread-model.md) (the participant-set model the engagement portal surfaces); [`docs/decisions/0030-thread-model.md`](0030-thread-model.md) (the ADR establishing Thread / Engagement / Collaboration terminology).
- **Related ADRs:** [0029](0029-tenant-execution-boundary.md) (no-portal-private-API rule); [0032](0032-drawer-panel-extension-slot.md) (CLI parity rule for portal panels).
- **Issues:** [#1219](https://github.com/cvoya-com/spring-voyage/issues/1219) (E2 umbrella).

## Context

The current web portal is management-shaped: it is where operators create units, configure agents, bind connectors, review analytics, and track costs. That surface is the right tool for setup and oversight.

The F1 thread model ([ADR-0030](0030-thread-model.md), [#1268](https://github.com/cvoya-com/spring-voyage/issues/1268)) and the killer use case for v0.1 Area E2 imply a fundamentally different surface: a place where a user engages with units and agents in flight — viewing the Timeline of an active thread, sending messages, answering clarifying questions, observing agent-to-agent work. The concepts are **Engagement** (the enduring relationship surface) and **Collaboration** (the active workspace), not management, configuration, or monitoring.

Putting the engagement surface inside the existing management portal creates a UX that conflates two different jobs. A user navigating to "manage" a unit and a user navigating to "work with" a unit have different intents, different navigation paths, and different affordances they need. Mixing them under one shell makes both harder.

The question to decide: how should the engagement surface relate to the existing management portal? Same page? Embedded component? Separate route? Separate app?

## Decision

Two distinct portals, same Next.js application.

- **Management portal** — the existing portal, rooted at `/`. Unit/agent management, configuration, monitoring, analytics, cost.
- **Engagement portal** — new, rooted at a new top-level parent route (proposed: `/work`). Engagement / collaboration: viewing threads, sending messages, observing agent-to-agent work, answering clarifying questions.

**Shared:** one Next.js app, one session, one auth wiring, one design system, one typed API client.

**Distinct:** separate top-level parent routes, separate navigation structures, separate purposes. No embedding of one portal's components inside the other. Cross-portal navigation is a standard anchor link between routes.

**The parent-route boundary is the future-separation seam.** All engagement-portal code lives under the new parent route. No coupling beyond shared session and API client may cross this boundary. A future v0.2+ can extract the engagement portal into its own Next.js deployment by splitting at the route, not by re-architecting the application.

**No portal-private API.** The engagement portal consumes the public Web API exclusively. If the engagement portal needs an endpoint that does not yet exist, that endpoint is filed as a separate design task before implementation and CLI gets the same endpoint. A portal-private API is never acceptable — it would violate the v0.1-wide design lens that makes the CLI and the portal interchangeable consumers of the same surface.

**CLI parity is mandatory.** Every engagement-portal use case has a `spring …` CLI counterpart. This extends the platform-wide rule in CONVENTIONS.md § 13 explicitly to the engagement portal.

## Alternatives considered

### Embed the engagement surface inside the current portal

Rejected. The two surfaces serve different purposes — setup/oversight vs. active collaboration. Embedding conflates the mental models: a panel inside a unit-management page that shows an engagement timeline would feel like an afterthought and would push the engagement surface into a secondary position relative to management. The engagement surface for the v0.1 killer use case needs to be the first-class place a user goes when they want to work with their agents, not a nested view inside the management portal.

### Separate Next.js application

Rejected for v0.1. A second Next.js app means a second auth wiring, a second build pipeline, a second deployment artefact, duplicated session management, and duplicated design-system dependency wiring. All of that is overhead for a v0.1 OSS first cut where the engagement surface is new and not yet proven. The parent-route seam delivers the same future-split property without the upfront cost. If v0.2+ usage patterns prove that separate deployments add value, the split is additive along the existing seam — not a re-architecture.

### Single portal that mode-switches

Rejected. A mode-switch (e.g. a global toggle that re-skins the shell) increases shell complexity, makes it harder to reason about the active navigation context, and forces the two modes to share a shell contract they don't naturally share. Two routes compose more cleanly than one shell with internal state.

### Embed as a dedicated page inside the management portal's navigation

A softer form of the "embed" option — a sidebar link to `/threads` inside the existing portal's nav. Rejected for the same reason as the full embed: it still presents engagement as a sub-feature of management rather than as a first-class surface. The parent-route boundary is what signals that "engagement" is its own place.

## Consequences

### Explicit rules that follow from this decision

1. **Two top-level routes.** The management portal stays at `/`. The engagement portal lives under a new parent route (proposed `/work`, to be ratified in the PR review). All engagement-portal routes are children of that parent.

2. **One shared session, design system, and API client.** No new auth wiring. No design-system fork. No separate API client or API token.

3. **No portal-private API — hard rule.** Any engagement-portal need that requires a new endpoint is:
   - Filed as a separate design task before the endpoint is implemented.
   - Exposed on the public Web API (not a private portal endpoint).
   - Simultaneously reachable from the CLI.
   Violations of this rule are blocking defects, not deferred follow-ups.

4. **CLI parity remains mandatory across both portals.** The existing CONVENTIONS.md § 13 rule applies to the engagement portal. A portal feature without a CLI counterpart is incomplete.

5. **The parent-route boundary is the future-separation seam.** Code reviews must enforce that no engagement-portal component is imported from management-portal code (or vice versa) beyond what the shared session and typed API client provide. When a v0.2+ need arises to split deployments, the work is: extract the engagement-portal subtree into a new Next.js project and wire its own deployment — not re-architect the application.

6. **Cross-portal navigation is an anchor link.** A management-portal page may link to an engagement-portal route and vice versa. The link is a standard `<a>` (or `<Link>`) to the other portal's URL. No shared layout components, no shared navigation context, no shared drawers or shells across the boundary.

### What this means for E2 sub-tasks

- E2.3 establishes the route skeleton and the design-system token wiring for the engagement portal. This is the foundational sub-task; everything else in E2 builds on top of it.
- E2.2 delivers the CLI counterparts for the engagement primitives. It is a blocker for "CLI parity is mandatory" to be satisfied at any engagement-portal feature boundary.
- Any sub-task that requires an API endpoint not yet on the public Web API must be preceded by a design task filed against the API surface.
