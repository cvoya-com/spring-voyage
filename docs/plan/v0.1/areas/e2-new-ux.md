# Area E2: New unit/agent-interaction UX

**Status:** In progress — planning settled; sub-tasks defined; implementation ahead.

## Scope

### The killer use case

A v0.1 user creates a unit from a template (software-engineering or product-management), connects it to a GitHub repository, assigns tasks to the unit, and engages with it directly. The unit's orchestrator triages incoming work and delegates to its agents. While work is in flight, the user can monitor progress, intervene with feedback, and answer clarifying questions the unit asks back. Every step of this flow is reachable from the `spring` CLI as well as from the web portal.

This use case is the justification for a separate engagement surface. The existing management portal is shaped around configuration and monitoring — the right tool for setting up a unit, reviewing analytics, and managing credentials. It is the wrong mental model for the back-and-forth of active collaboration. E2 builds the surface where that collaboration happens.

### In scope for v0.1

- Engagement portal as a distinct top-level route in the existing Next.js app (same auth, same design system, same typed API client). See the Architecture section for the route boundary rationale.
- Engagement list view: mine / per-unit / per-agent; recency-driven sort; no "close" affordance (engagements never close — they age out and resurface on new activity).
- Engagement detail view: full Timeline, send message, observe A2A threads (read-only), first-class error surfacing (errors appear in the timeline as primary artifacts, not buried in an activity log).
- Bidirectional clarification: the unit can ask the user a question inside the `{unit, human}` engagement thread; the user replies in the same thread.
- A2A engagements observable: agent-to-agent threads are navigable from a unit's or agent's full engagement list, even when no human is a participant. They do not appear in the human's "my engagements" list.
- Template wizard (software-engineering + product-management): create-unit-from-template flow, GitHub connector binding, first task assignment. Both templates ship working and out of the box.
- CLI parity: every engagement-portal use case has a `spring …` CLI counterpart. See the CLI parity section.

### Out of scope for v0.1

- Cost / budget surfacing inside the engagement view. Stays in the management portal.
- Multi-human engagements. OSS is single-human; multi-human requires a hosted overlay and separate permission design.
- Connectors other than GitHub.
- Joining a running engagement as an active participant. v0.1 supports **observe only**. Join is deferred to v0.2 — the thread-model semantics for a human joining a running engagement need their own design pass first (see [#1292](https://github.com/cvoya-com/spring-voyage/issues/1292)).
- Engagement closure. Engagements never close; new activity resurfaces them. The UX has no "close" button.

## Architecture

### Two-portal model

E2 introduces an **engagement portal** alongside the existing **management portal**. These are two distinct surfaces over the same public Web API — not a single portal that mode-switches, not a second Next.js app, and not an embedded component inside the management portal.

The portals share: the same Next.js application, the same session and auth (no new auth wiring), the same design-system tokens and components, and the same typed API client.

The portals are distinct: each has its own top-level parent route, its own navigation structure, and its own purpose.

| Surface | Parent route | Purpose |
|---|---|---|
| Management portal | `/` (current root) | Unit / agent management, configuration, monitoring, analytics, cost |
| Engagement portal | `/work` (proposed — see note) | Engagement / collaboration: viewing threads, sending messages, observing agent-to-agent work, answering clarifying questions |

> **Route name rationale.** `/work` signals "where work happens" — it is activity-oriented rather than management-oriented, matches the user's mental model when they open the portal to do something with their agents, and does not conflict with any current route. The final name is to be ratified by the product owner in the PR review.

The parent-route boundary is the seam that allows a future v0.2+ deployment to split the engagement portal into its own Next.js app without re-architecting the application. No coupling that crosses this seam beyond the shared session and API client should be introduced in E2.

See [ADR-0033](../../decisions/0033-two-portal-architecture.md) for the full decision record, including the alternatives considered.

### No portal-private API

The engagement portal consumes the **public Web API** exclusively. If the engagement portal needs an endpoint the API does not yet expose, that is a separately-tracked design task — filed before the endpoint lands — and CLI gets the same endpoint. No portal-private endpoint is ever acceptable. This reaffirms the v0.1-wide design lens established in [the plan README](../README.md) and [ADR-0029](../../decisions/0029-tenant-execution-boundary.md).

### Cross-link contract

Navigation links between the two portals are allowed. A unit-detail page in the management portal may link to the unit's engagement list in the engagement portal, and vice versa. The link is a standard anchor to the other portal's route — no embed, no component sharing across the route boundary.

## CLI parity (mandatory)

CONVENTIONS.md § 13 makes CLI / UI parity a hard rule for all user-facing features. Every engagement-portal use case must have a `spring …` CLI counterpart.

Specifically required for v0.1 E2:

| Use case | CLI command |
|---|---|
| List engagements (mine) | `spring engagement list` |
| List engagements for a unit | `spring engagement list --unit <id>` |
| List engagements for an agent | `spring engagement list --agent <id>` |
| Observe an engagement (streaming) | `spring engagement watch <id>` |
| Send a message into an engagement | `spring engagement send <id> "<message>"` |
| Answer a clarifying question | `spring engagement answer <id> "<answer>"` |
| See first-class errors on a thread | `spring engagement errors <id>` |
| Create unit from template | `spring unit create --template software-engineering` / `--template product-management` |
| Bind GitHub connector | `spring connector bind --unit <id> --type github …` |

CLI implementation gaps are captured in E2.2.

## Engagement model in v0.1

These properties are first-class, not implementation notes:

**Engagements never close.** An engagement is the enduring relationship between a fixed participant set ([ADR-0030](../../decisions/0030-thread-model.md)). There is no "close" operation. Within the same engagement (same participant set), multiple collaborations about different topics and tasks accumulate over time. The UX is recency-driven: sort by latest activity, fade inactive engagements from prominence, resurface them when new activity arrives.

**Bidirectional clarification.** The unit can ask the user a question inside the `{unit, human}` engagement thread. The user's reply arrives in the same thread. The engagement portal surfaces inbound questions as a distinct call-to-action so they are not lost in a long Timeline.

**A2A engagements are observable.** Agent-to-agent threads exist and accumulate artifacts even when no human is a participant. A human can navigate to such a thread from a unit's or agent's full engagement list and observe (read-only). These threads do not appear in the human's personal "my engagements" list.

**Errors are first-class.** Errors that arise during agent execution appear as primary Timeline entries, not tucked into an activity log. The engagement detail view must surface them visibly. The CLI surfaces them via `spring engagement errors <id>`.

**Observe, not join.** In v0.1, a human who is not an original participant in a thread can observe (read the Timeline) but cannot send messages into it. The join interaction — a human becoming an active participant in a running engagement — is deferred to v0.2 pending a design pass on the thread-model semantics ([#1292](https://github.com/cvoya-com/spring-voyage/issues/1292)).

## Sub-tasks

| ID | Issue | Description |
|---|---|---|
| E2.1 | (this PR) | This planning doc + ADR-0033. |
| E2.2 | [#1414](https://github.com/cvoya-com/spring-voyage/issues/1414) | CLI surface gaps for engagement primitives: `spring engagement list/watch/send/answer/errors`. |
| E2.3 | [#1415](https://github.com/cvoya-com/spring-voyage/issues/1415) | Engagement-portal route skeleton under the new parent route; design-system tokens; navigation cross-links between management and engagement portals. |
| E2.4 | [#1416](https://github.com/cvoya-com/spring-voyage/issues/1416) | Engagement list view: mine / per-unit / per-agent; recency-driven sort; no "close" affordance. |
| E2.5 | [#1417](https://github.com/cvoya-com/spring-voyage/issues/1417) | Engagement detail view: Timeline, send message, observe (read-only mode), first-class error surfacing. |
| E2.6 | [#1418](https://github.com/cvoya-com/spring-voyage/issues/1418) | Inbound clarification UX: unit asks user a question inside the `{unit, human}` engagement thread; user replies in the same thread. |
| E2.7 | [#1419](https://github.com/cvoya-com/spring-voyage/issues/1419) | Template-flow parity: wizard and CLI aligned for software-engineering and product-management templates; both bind to GitHub; both work out of the box. |
| E2.8 | (no issue) | Out-of-scope tracker: deferred items listed for visibility — engagement-join (v0.2), multi-human, additional connectors, cost-in-engagement-view. Filed separately when the time comes. |

## Dependencies

- **D** (ADR-0029 boundaries): done. Defines the public Web API surface and the no-portal-private-API rule E2 reaffirms.
- **F** (Thread model): done. [ADR-0030](../../decisions/0030-thread-model.md) and [#1268](https://github.com/cvoya-com/spring-voyage/issues/1268) (F1) are the load-bearing primitives for the engagement model — participant-set identity, Timeline, engagement/collaboration terminology. Code rename [#1287](https://github.com/cvoya-com/spring-voyage/issues/1287) (`Conversation*` → `Thread*`) is a prerequisite for implementation.
- **C2** (API freeze): done. The public API surface is frozen; the engagement portal builds against it.

## Open questions

RESOLVED — see planning decisions above and [ADR-0033](../../decisions/0033-two-portal-architecture.md). The questions that existed in the prior draft of this doc (deliverable shape, relationship to current portal, auth, tech stack, killer use case) are all settled. This section is retained so readers looking for open questions know to stop here.
