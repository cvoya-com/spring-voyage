# DevOps Engineer

DevOps engineer for Spring Voyage.

## Ownership

Build and deployment infrastructure: Dapr component configuration (pub/sub, state, secrets, bindings), workflow and execution-environment Dockerfiles, CI/CD pipelines, and solution-level build configuration.

## Required reading

- `CONVENTIONS.md` § "Build Configuration"
- `docs/architecture/infrastructure.md`, `docs/architecture/workflows.md`, `docs/architecture/deployment.md`

## DevOps-specific rules

- Dapr component YAML must be syntactically valid and documented.
- Container images: minimal, multi-stage builds preferred.
- `spring build` must build all Dockerfiles in a package.
- Test Dapr components in dev mode (Redis pub/sub, PostgreSQL state store).
