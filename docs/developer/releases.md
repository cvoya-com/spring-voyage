# Releases and Versioning

This document describes how Spring Voyage is versioned and how releases are cut.

## Semantic Versioning

Spring Voyage follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html): `MAJOR.MINOR.PATCH`.

| Change type | Bump | Examples |
| --- | --- | --- |
| **MAJOR** — incompatible changes that require users or extenders to modify their code or configuration | `MAJOR` | Remove or rename a public type/member in `Cvoya.Spring.Core`; change an interface signature; rename a Dapr state key in a way that loses data on upgrade; change default DI registrations so existing hosts fail to start; drop support for a runtime (e.g., .NET version) |
| **MINOR** — backwards-compatible additions | `MINOR` | New interface, new orchestration strategy, new connector, new API endpoint, new CLI command, new optional configuration |
| **PATCH** — backwards-compatible bug fixes | `PATCH` | Fix a crash, correct incorrect routing, tighten validation, resolve a regression |

### What counts as a breaking change

For the OSS core platform (`Cvoya.Spring.*` assemblies under `src/`), a change is breaking if any of the following are true:

1. **Public API removal or rename.** Any public type, member, or parameter in `Cvoya.Spring.Core`, `Cvoya.Spring.Dapr`, `Cvoya.Spring.A2A`, `Cvoya.Spring.Host.Api`, `Cvoya.Spring.Cli`, or a published connector package is removed, renamed, or gets an incompatible signature.
2. **Behavioural contract change.** An existing method still compiles but now returns a different shape, throws on previously valid input, or changes its persistence format.
3. **Extension-point break.** A change that forces downstream consumers (notably the private Spring Voyage Cloud repo) to modify their DI wiring, inheritance hierarchies, or overrides. Extensibility is a first-class contract in this repo — see [`AGENTS.md` § "Open-Source Platform & Extensibility"](../../AGENTS.md).
4. **Persistent state / schema change without migration.** Any change to actor state keys, EF Core entities, Dapr state shapes, or OpenAPI contracts that does not ship with a compatible migration path.
5. **CLI / web surface removal.** Removing a `spring` CLI subcommand or flag, or a web portal feature that was previously documented. Per [`CONVENTIONS.md` § 14](../../CONVENTIONS.md#14-ui--cli-feature-parity), UI and CLI parity is enforced — both surfaces move together.
6. **Configuration break.** Renaming an environment variable, Dapr component name, appsettings key, or connector binding in a way that stops existing deployments from starting.

Non-breaking additions — new interfaces, new optional parameters with defaults, new strategies registered alongside existing ones, new CLI subcommands, new API endpoints — are MINOR bumps.

Breaking changes to `Cvoya.Spring.Core` interfaces require explicit discussion per [`CONTRIBUTING.md`](../../CONTRIBUTING.md#code-review) and must be flagged with the `breaking-change` label.

### Pre-1.0 stability

While the project is pre-1.0 (`0.x.y`), minor version bumps (`0.x.0`) may contain breaking changes, as permitted by SemVer. We will still flag them as breaking in the changelog. Once the project reaches `1.0.0`, the full SemVer contract applies.

## Pre-release Scheme

Pre-release versions use a SemVer-compatible suffix:

| Suffix | Purpose |
| --- | --- |
| `-alpha.N` | Early, possibly broken. Internal testing. May change freely. |
| `-beta.N` | Feature-complete for the target release; stabilising. Public testing encouraged. Breaking changes possible but called out. |
| `-rc.N` | Release candidate. Expected to become the final release unless a blocker is found. No further feature additions; only blocker fixes. |

Examples: `0.2.0-alpha.1`, `0.2.0-beta.3`, `1.0.0-rc.1`.

Pre-release versions are published alongside (not in place of) the most recent stable version; consumers must opt in explicitly (e.g., `--prerelease` on package commands).

## How Releases Are Cut

1. **Source of truth: tags on `main`.** Releases are cut from `main` by creating an annotated git tag of the form `vMAJOR.MINOR.PATCH` (e.g., `v0.2.0`). Pre-releases use `vMAJOR.MINOR.PATCH-<suffix>` (e.g., `v0.2.0-rc.1`).
2. **Changelog finalisation.** Before tagging, move the `## [Unreleased]` section in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, create a fresh empty `[Unreleased]` section, and open a PR titled `Release vX.Y.Z`.
3. **Tag after merge.** Once the release PR merges, create the tag on the merge commit: `git tag -a vX.Y.Z -m "Release vX.Y.Z" && git push origin vX.Y.Z`.
4. **Automated release pipeline.** Pushing the tag triggers [`.github/workflows/release.yml`](../../.github/workflows/release.yml), which runs the full test suite, builds and pushes the container images, and creates the GitHub Release with the `## [Unreleased]` section of `CHANGELOG.md` as the release notes.
5. **No long-lived release branches.** Branches are short-lived and PR-scoped. Patch releases on older minor versions are an exception (see "Patch releases on prior versions" below) and will use `release/X.Y` branches when needed.
6. **Who can cut a release.** Maintainers with write access to the repository. The release PR still goes through normal review.

### Patch releases on prior versions

If a critical fix needs to ship on an older minor line (e.g., current is `0.3.x` and we need to patch `0.2.x`), a `release/0.2` branch is created from the `v0.2.y` tag, the fix is cherry-picked, and a new tag is cut from that branch. This is an exception path — the default is "fix on `main`, ship in the next release".

## CI/CD Pipeline for Release Artefacts

### CI (build, test, lint)

The repository has two continuous-integration workflows under [`.github/workflows/`](../../.github/workflows):

- **[`ci.yml`](../../.github/workflows/ci.yml)** — runs on `push` to `main`, on `pull_request` targeting `main`, and in the merge queue. Jobs:
  - `changes` — path-filter gate for downstream jobs.
  - `build` — `dotnet build SpringVoyage.slnx --configuration Release`.
  - `test` — `dotnet test --solution SpringVoyage.slnx --configuration Release` with a Dapr slim init.
  - `format` — `dotnet format --verify-no-changes`.
  - `agent-definitions-lint` — validates referenced paths in agent YAML/markdown definitions.
  - `connector-web-lint` — validates per-connector web submodules.
  - `web-lint` / `web-build` — ESLint and `next build` for the web portal.
  - `python-lint` / `python-test` — ruff and pytest for `agents/dapr-agent/`.
  - `openapi-drift` — rebuilds `openapi.json` and the Kiota CLI client and fails if the working tree is dirty.
  - `required-checks` — aggregation gate for branch protection.
- **[`codeql.yml`](../../.github/workflows/codeql.yml)** — CodeQL C# analysis on pushes, pull requests, merge queue, and weekly.

### Release pipeline (`release.yml`)

[`.github/workflows/release.yml`](../../.github/workflows/release.yml) fires on tag pushes matching `v[0-9]+.[0-9]+.[0-9]+*` (e.g., `v0.1.0`, `v0.2.0-rc.1`). It can also be triggered manually via `workflow_dispatch` with an explicit tag input.

Jobs, in order:

1. **`resolve`** — validates the tag shape (`vMAJOR.MINOR.PATCH[-prerelease]`) and emits `version`, `major_minor`, and `is_prerelease` outputs consumed by downstream jobs.
2. **`test`** — runs the full .NET test suite (restore, build, `dotnet test`) with a Dapr slim init, mirroring `ci.yml`'s `test` job exactly. The release is gated on tests passing.
3. **`publish-images`** — builds the three agent container images via `deployment/build-agent-images.sh`, then tags and pushes them to `ghcr.io` (see Container Images section below).
4. **`github-release`** — creates the GitHub Release using `scripts/extract-changelog-section.sh` to pull the `## [Unreleased]` block of `CHANGELOG.md` as the release body.

Component-scoped release workflows (agent-base binaries, dispatcher self-contained builds) use separate tag prefixes (`agent-base-v*`, `dispatcher-v*`) so they do not fight with `release.yml`.

## NuGet Package Publishing

The repository does **not** publish NuGet packages. No `src/` project sets `IsPackable=true`. Consumers outside the open-source repo pin to a specific commit SHA on `main` via git submodule or project reference.

The decision to publish NuGet packages (names, registry, `IsPackable` wiring) is tracked separately in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395).

## Container Image Tagging and Publishing

Container images are published to the GitHub Container Registry (`ghcr.io/cvoya/`) by the `release.yml` workflow on every tag push. Three images are built and pushed:

| Image | Description |
| --- | --- |
| `ghcr.io/cvoya/spring-agent` | Primary agent runtime (claude-code); the default image in `UnitRuntimeOptions.cs`. |
| `ghcr.io/cvoya/agent-base`   | BYOI conformance path-1 base image; bundles the A2A sidecar bridge. |
| `ghcr.io/cvoya/agent-dapr`   | Dapr-native A2A agent (path-3). |

### Tag convention

| Tag | Meaning |
| --- | --- |
| `:X.Y.Z` | Immutable version tag. Never reused. |
| `:X.Y` | Floating tag pointing at the latest patch of the `X.Y` minor line. |
| `:latest` | Floating tag pointing at the most recent stable release. Never points at a pre-release. |

Pre-release tags (e.g., `v0.2.0-rc.1`) push only the immutable `:X.Y.Z-rc.N` tag; `:latest` and `:X.Y` are not updated.

The tag pushed to the container registry has the leading `v` stripped (e.g., git tag `v0.1.0` → image tag `0.1.0`).

### Local and VPS deployment

`deployment/deploy.sh` and `deployment/deploy-remote.sh` build images locally with Podman on the target host. They do not pull from the registry; `ghcr.io/cvoya/spring-agent:latest` is available for operators who prefer a pre-built image.

## Changelog

The canonical changelog is [`CHANGELOG.md`](../../CHANGELOG.md) at the repository root. It follows the [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) format. See [`CONTRIBUTING.md` § Changelog Expectations](../../CONTRIBUTING.md#changelog-expectations) for the per-PR convention.

## Helper Scripts

| Script | Purpose |
| --- | --- |
| [`scripts/extract-changelog-section.sh`](../../scripts/extract-changelog-section.sh) | Extracts a named section (default: `Unreleased`) from `CHANGELOG.md` and prints it to stdout. Used by `release.yml` to populate the GitHub Release body. |

## Summary Table

| Topic | State today |
| --- | --- |
| SemVer | Adopted |
| Git tags | Tag-based from `main`; `v*` prefix |
| GitHub Releases | Automated via `release.yml` on tag push |
| NuGet packages | Not published; decision tracked in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395) |
| Container images | Published to `ghcr.io/cvoya/*` by `release.yml` |
| CI (build, test, format, lint) | In place ([`ci.yml`](../../.github/workflows/ci.yml), [`codeql.yml`](../../.github/workflows/codeql.yml)) |
| Release-publishing workflow | In place ([`release.yml`](../../.github/workflows/release.yml)) |
