# Area E2: New unit/agent-interaction UX

**Status:** Planning session pending. Design can run parallel with D / F; build follows.

## Scope (provisional)

A new, separate UX focused on **interacting with units/agents** — distinct from the current portal's management/configuration/monitoring focus. Started in v0.1; full delivery may span beyond v0.1.

## Dependencies

- Design depends on: D, F (architectural shape).
- Build depends on: C2 (API surface), F (conversation concept).
- Coexists with: current Web Portal (continuity is a regression criterion).

## Open questions

- What's the v0.1 deliverable — exploratory prototype, MVP, or shipped?
- Relationship to current portal — separate app, separate route, embedded?
- Auth/session sharing with current portal?
- Tech stack — same as current portal or a clean break?
- What's the killer use case that justifies separating from the existing portal?

## Notes

This is *new* surface, not a refactor of the current portal. The current portal stays on the same public Web API and is not deprecated.
