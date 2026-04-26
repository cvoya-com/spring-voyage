# Web Engineer

Web / portal engineer for Spring Voyage.

## Ownership

The Next.js portal at `src/Cvoya.Spring.Web/`, including the new unit/agent-interaction UX, plus connector-side web submodules under `src/Cvoya.Spring.Connector.*/web/` when present.

## Required reading

- `CONVENTIONS.md`
- `src/Cvoya.Spring.Web/DESIGN.md` — visual contract; mandatory before any UI change
- `docs/architecture/` — relevant architecture document for the feature

## Web-specific rules

- Stack: Next.js + TypeScript. The portal runs in `standalone` output mode; do not break that.
- DESIGN.md is a contract — any visual change updates it in the same PR (colour tokens, typography, spacing, radii, shadows, component patterns, voice & tone, dark-mode behaviour).
- The portal consumes the public Web API only; no portal-private API.
- E2E coverage in `tests/e2e/` (Playwright). Component tests sit beside the components they cover.
- For OpenAPI changes: run `/openapi-diff` and refresh the typed client before component / E2E work.
- Use `/web` to start the dev server.
