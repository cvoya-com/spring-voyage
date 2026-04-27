# Area F: Conversation concept (#1123)

**Status:** Planning session pending.

## Scope (provisional)

Implement the conversation concept (or a renamed equivalent) per [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123). Foundational primitive that other areas (D, C2, E1, E2) depend on architecturally.

## Dependencies

- Depends on: J (ADR audit) for context on related decisions.
- Blocks: C2, E1, E2 (architecturally).

## Open questions

- Is "conversation" the right term? What alternatives?
- What's the relationship to existing primitives (sessions, threads, runs, messages, etc.)?
- API surface — what does it look like in the public Web API?
- Persistence model?
- Multi-tenant / boundary implications (intersects with D)?
- How does it surface in the CLI (E1) and the new UX (E2)?

## Notes

Term may be renamed during the planning session; if so, the area gets a renamed file plus a redirect note.
