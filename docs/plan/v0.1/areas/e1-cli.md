# Area E1: CLI as primary UX

**Status:** Planning session pending. Build phase depends on C2.

## Scope (provisional)

Fully functional, well-tested CLI as the primary user experience. Built on top of the public Web API (no CLI-private API). Becomes the primary surface for users and for SV-developing-SV (dogfooding).

## Dependencies

- Depends on: C2 (frozen API contract); D (boundaries).
- Provides: foundation for the dogfooding stretch criterion.

## Open questions

- What's in the v0.1 CLI scope vs deferred?
- Distribution / install story?
- Auth flow on top of the public Web API?
- Test strategy — unit, integration, E2E against a live API?
- Cross-platform expectations?

## Notes

CLI source already lives at `src/Cvoya.Spring.Cli/`; this area evolves it from current state to v0.1-ready, not a clean rewrite. Dogfooding stretch criterion explicitly depends on this area being usable enough to build SV with.
