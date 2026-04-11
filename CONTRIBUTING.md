# Contributing to Spring Voyage

Thank you for your interest in contributing to Spring Voyage. This document covers the workflow for the open-source platform.

## Development Setup

See [docs/developer/setup.md](docs/developer/setup.md) for prerequisites and build instructions.

## Workflow

### Issues

- **Bug reports:** Use the "Bug Report" template.
- **Feature requests:** Use the "Feature Request" template.
- **New interfaces/extension points:** Use the "OSS Interface" template when proposing a new abstraction or extension point.

### Branches and PRs

1. Create a branch from `main` for your work.
2. Make focused changes — one issue per PR.
3. Write tests for all new public methods.
4. Run `dotnet build` and `dotnet test` before opening a PR.
5. Run `dotnet format --verify-no-changes` to check formatting.
6. Open a PR against `main` with a clear description.
7. Reference the issue in your commit message: `Closes #N`.

### Code Review

All PRs require review before merging. Reviewers check:
- Adherence to [CONVENTIONS.md](CONVENTIONS.md)
- Test coverage
- Architecture alignment with [the plan](docs/SpringVoyage-v2-plan.md)
- No breaking changes to Core interfaces without discussion

## Contributor License Agreement (CLA)

All external contributors must sign a Contributor License Agreement before their first PR can be merged. The CLA grants CVOYA LLC a license to use your contributions, which is necessary to support the open core model (see [LICENSE.md](LICENSE.md)).

When you open your first PR, the CLA bot will comment with instructions. Signing is a one-time process.

**What the CLA covers:**
- You grant CVOYA LLC a perpetual, worldwide, non-exclusive license to use your contributions
- You retain full copyright over your contributions
- You confirm that you have the right to submit the contribution

## Coding Conventions

All conventions are in [CONVENTIONS.md](CONVENTIONS.md). Key points:

- Namespace: `Cvoya.Spring.*`
- Target: .NET 10
- `Cvoya.Spring.Core` has ZERO external dependencies
- System.Text.Json only
- Interface-first: define in Core, implement in Dapr
- Test naming: `MethodName_Scenario_ExpectedResult`

## Architecture

- [Concepts](docs/concepts/overview.md) — the mental model
- [Architecture](docs/architecture/overview.md) — how it's built
- [Design Decisions](docs/design-decisions.md) — the "why"
- [Roadmap](docs/roadmap.md) — phased plan with OSS/Private split

## Labels

| Label | Meaning |
|-------|---------|
| `bug` | Something is broken |
| `enhancement` | New feature or improvement |
| `oss-interface` | New interface/extension point |
| `phase-1` through `phase-6` | Roadmap phase |
| `breaking-change` | Requires coordinated updates |
| `good first issue` | Suitable for new contributors |
