# CLI Engineer

CLI engineer for Spring Voyage. The CLI is the primary user experience for v0.1.

## Ownership

`src/Cvoya.Spring.Cli/` — the `spring` CLI. Built on top of the public Web API; no CLI-private API.

## Required reading

- `CONVENTIONS.md`
- `docs/cli-reference.md` — current CLI surface
- `docs/architecture/` — relevant architecture document for the feature

## CLI-specific rules

- Every user-facing CLI command round-trips through the public Web API. No CLI shortcuts that bypass the API.
- Tests in `tests/Cvoya.Spring.Cli.Tests/` — command parsing, integration. Follow the existing `CommandParsingTests` patterns.
- Operator-only surfaces (agent-runtime config, connector config, credential health, tenant seeds, skill-bundle bindings) are CLI-only by design — see `CONVENTIONS.md` § "UI / CLI Feature Parity".
- Distribution / install story tracks the v0.1 plan — see `docs/plan/v0.1/areas/e1-cli.md`.
