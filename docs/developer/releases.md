# Releases and Versioning

This document describes how Spring Voyage is versioned and how releases are cut. It covers both what currently exists in the repository and what is proposed as the project formalises its release process ahead of the OSS launch ([#794](https://github.com/cvoya-com/spring-voyage/issues/794)).

Sections are explicitly labelled **Observed** (what the codebase does today) or **Proposed** (the convention this document establishes going forward) so readers can tell which parts are descriptive and which are prescriptive.

## Semantic Versioning (Proposed)

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

## Pre-release Scheme (Proposed)

Pre-release versions use a SemVer-compatible suffix:

| Suffix | Purpose |
| --- | --- |
| `-alpha.N` | Early, possibly broken. Internal testing. May change freely. |
| `-beta.N` | Feature-complete for the target release; stabilising. Public testing encouraged. Breaking changes possible but called out. |
| `-rc.N` | Release candidate. Expected to become the final release unless a blocker is found. No further feature additions; only blocker fixes. |

Examples: `0.2.0-alpha.1`, `0.2.0-beta.3`, `1.0.0-rc.1`.

Pre-release versions are published alongside (not in place of) the most recent stable version; consumers must opt in explicitly (e.g., `--prerelease` on package commands).

## How Releases Are Cut (Proposed)

The repository has no formal release process and no git tags today — see the "Observed state" note below. The following is the proposed convention going forward.

1. **Source of truth: tags on `main`.** Releases are cut from `main` by creating an annotated git tag of the form `vMAJOR.MINOR.PATCH` (e.g., `v0.2.0`). Pre-releases use `vMAJOR.MINOR.PATCH-<suffix>` (e.g., `v0.2.0-rc.1`).
2. **Changelog finalisation.** Before tagging, move the `## [Unreleased]` section in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, create a fresh empty `[Unreleased]` section, and open a PR titled `Release vX.Y.Z`.
3. **Tag after merge.** Once the release PR merges, create the tag on the merge commit: `git tag -a vX.Y.Z -m "Release vX.Y.Z" && git push origin vX.Y.Z`.
4. **GitHub Release.** Create a corresponding GitHub Release from the tag, pasting the changelog section as the release notes.
5. **No long-lived release branches.** Branches are short-lived and PR-scoped. Patch releases on older minor versions are an exception (see "Patch releases on prior versions" below) and will use `release/X.Y` branches when needed.
6. **Who can cut a release.** Maintainers with write access to the repository. The release PR still goes through normal review.

### Patch releases on prior versions (Proposed)

If a critical fix needs to ship on an older minor line (e.g., current is `0.3.x` and we need to patch `0.2.x`), a `release/0.2` branch is created from the `v0.2.y` tag, the fix is cherry-picked, and a new tag is cut from that branch. This is an exception path — the default is "fix on `main`, ship in the next release".

## CI/CD Pipeline for Release Artefacts

### Observed state

The repository has two GitHub Actions workflows today, both under [`.github/workflows/`](../../.github/workflows/):

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

**There is no release-publishing workflow today.** CI validates every PR and every merge into `main`; it does not publish artefacts, push containers, publish NuGet packages, or create GitHub Releases. No git tags exist (`git tag -l` returns nothing at the time of writing), and no GitHub Releases have been cut.

### Proposed

Introduce a tag-triggered `release.yml` workflow that fires on pushes of tags matching `v*.*.*`. It would:

1. Re-run build, test, and format checks against the tagged commit.
2. Produce and attach release artefacts (see NuGet and container sections below for the current intent).
3. Create or update the GitHub Release, attaching the changelog section for the tagged version.

The shape of this workflow is left to a follow-up issue once the publishing targets below are settled.

## NuGet Package Publishing

### Observed state

The repository does **not** publish NuGet packages today. Inspection of the solution shows:

- No `GeneratePackageOnBuild`, `IsPackable=true`, or `nuspec` entries on any `src/` project.
- Only test projects set `IsPackable=false`, which is the only `IsPackable` usage in the repo.
- No `dotnet nuget push`, `NUGET_API_KEY`, or equivalent in any workflow.
- No published NuGet feed (neither `nuget.org` nor GitHub Packages) is referenced from the project.

`Cvoya.Spring.Core`, `Cvoya.Spring.Dapr`, and the connector libraries are consumed today as project references and via git-submodule embedding by the private Spring Voyage Cloud repo; they are not distributed as NuGet packages.

### Proposed

Once the API surface of `Cvoya.Spring.Core` and the primary implementation assemblies stabilise, publish them to NuGet as part of the tag-triggered release workflow. Initial candidates (subject to API stability):

- `Cvoya.Spring.Core`
- `Cvoya.Spring.Dapr`
- `Cvoya.Spring.A2A`
- `Cvoya.Spring.Connector.GitHub`

Until that happens, consumers outside the open-source repo should pin to a specific commit SHA on `main` via git submodule or project reference. This document will be updated with the concrete publishing workflow when packages are first shipped.

## Container Image Tagging and Publishing

### Observed state

The repository does **not** publish container images today. Inspection shows:

- [`deployment/Dockerfile`](../../deployment/Dockerfile) and [`deployment/Dockerfile.agent`](../../deployment/Dockerfile.agent) exist for local and VPS deployment.
- [`src/Cvoya.Spring.Dapr/Execution/UnitRuntimeOptions.cs`](../../src/Cvoya.Spring.Dapr/Execution/UnitRuntimeOptions.cs) references `ghcr.io/cvoya/spring-agent:latest` as the default agent image, but no workflow in this repository pushes to that tag. The image is expected to be built by an external process (or, at present, locally); a published image at that coordinate is not a guarantee of this repo.
- No `docker/build-push-action`, `docker login`, or equivalent appears in any workflow.
- `deployment/deploy.sh` and `deployment/deploy-remote.sh` build images locally with Podman on the target host; they do not push to a registry.

### Proposed

When container publishing is introduced, use the following tagging convention on GitHub Container Registry (`ghcr.io/cvoya/...`):

- `:vX.Y.Z` — immutable tag for each released version (never reused).
- `:X.Y` — floating tag pointing at the latest patch of the `X.Y` minor line.
- `:X` — floating tag pointing at the latest minor/patch of the `X` major line.
- `:latest` — floating tag pointing at the most recent stable release (never points at a pre-release).
- `:sha-<short>` — per-commit tag for builds off `main` (useful for debugging; never referenced from documentation).

Pre-release versions (`vX.Y.Z-rc.N`) get the immutable `:vX.Y.Z-rc.N` tag and do **not** move `:latest`, `:X.Y`, or `:X`.

The publishing workflow itself is a follow-up once the first image is ready to ship.

## Changelog

The canonical changelog is [`CHANGELOG.md`](../../CHANGELOG.md) at the repository root. It follows the [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) format. See [`CONTRIBUTING.md` § Changelog Expectations](../../CONTRIBUTING.md#changelog-expectations) for the per-PR convention.

## Summary Table

| Topic | State today |
| --- | --- |
| SemVer adopted | Proposed, documented here |
| Git tags | None yet; tag-based from `main` proposed |
| GitHub Releases | None yet; proposed to mirror tags |
| NuGet packages | Not published |
| Container images | Not published by this repo |
| CI (build, test, format, lint) | In place ([`ci.yml`](../../.github/workflows/ci.yml), [`codeql.yml`](../../.github/workflows/codeql.yml)) |
| Release-publishing workflow | Not present; to be added |
