# Contributing to Spring Voyage

Thank you for your interest in contributing to Spring Voyage. This document covers the workflow for the source-available platform.

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
8. Use a [Conventional Commits](https://www.conventionalcommits.org) message — `CHANGELOG.md` is generated from these (see [Commit messages and the changelog](#commit-messages-and-the-changelog)).

### Commit messages and the changelog

`CHANGELOG.md` is **generated** from Conventional Commit subjects by [git-cliff](https://git-cliff.org) — do **not** hand-edit it. The `[Unreleased]` section is regenerated from the commits on `main` (`eng/release/update-changelog.sh`, enforced by `release.sh` at each release), so what you write in the commit subject is what appears in the changelog:

- **Use Conventional Commits:** `type(scope): summary`. The type picks the changelog group — `feat` → Features, `fix` → Bug fixes, `perf` → Performance, `refactor` → Refactor, `docs` → Documentation. `chore`, `ci`, `test`, `build`, and `style` are omitted.
- **Write the summary for a reader** — it is the user-facing line — and put the area in the `scope` (e.g. `fix(messaging): …`).
- **Breaking changes:** add `!` after the type/scope (`feat(api)!: …`) or a `BREAKING CHANGE:` footer; they surface under **Breaking changes**. Also apply the `breaking-change` label. See [`docs/developer/releases.md`](docs/developer/releases.md) for the full policy.
- **The PR number** GitHub appends on squash-merge (`(#NNN)`) is auto-linked — no manual reference needed.
- **Earlier history** below the marker in `CHANGELOG.md` is hand-curated and frozen; leave it untouched.

### Code Review

All PRs require review before merging. Reviewers check:
- Adherence to [CONVENTIONS.md](CONVENTIONS.md)
- Test coverage
- Architecture alignment with [the architecture index](docs/architecture/README.md) and the [decision records](docs/decisions/README.md)
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
- [Architecture](docs/architecture/README.md) — how it's built
- [Decision Records](docs/decisions/README.md) — the "why" behind the major architectural choices
- [Roadmap (archived)](docs/archive/roadmap/README.md) — historical (pre-v0.1) planning narrative; live progress now lives on the GitHub [milestones](https://github.com/cvoya-com/spring-voyage/milestones)
- [Releases and Versioning](docs/developer/releases.md) — SemVer, release process, CI/CD

## Labels

| Label | Meaning |
|-------|---------|
| `bug` | Something is broken |
| `enhancement` | New feature or improvement |
| `oss-interface` | New interface/extension point |
| `breaking-change` | Requires coordinated updates |
| `good first issue` | Suitable for new contributors |
