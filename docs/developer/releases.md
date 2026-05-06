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
| `-alpha.<date>` | Early, possibly broken. Internal testing. May change freely. |
| `-beta.<date>` | Feature-complete for the target release; stabilising. Public testing encouraged. Breaking changes possible but called out. |
| `-rc.<date>` | Release candidate. Expected to become the final release unless a blocker is found. No further feature additions; only blocker fixes. |

### Date-anchored convention

The date field uses the `YYYYMMDD` form of the release date (UTC). Same-day re-releases append a numeric counter: `.1`, `.2`, …

```
v0.1.0-alpha.20260504       # first alpha on 2026-05-04
v0.1.0-alpha.20260504.1     # second alpha on the same day
v0.1.0-rc.20260601          # release candidate on 2026-06-01
v0.1.0                      # stable release
```

The git tag is the sole source of truth for the release version. There is no `VERSION` file.

Pre-release versions are published alongside (not in place of) the most recent stable version; consumers must opt in explicitly.

## How Releases Are Cut

Use `scripts/release.sh` to cut a release. The script pushes four component tags in dependency order and waits for each workflow to succeed before proceeding.

```bash
# Dry-run: print the computed tags without pushing anything.
./scripts/release.sh v0.1.0 --pre alpha --plan

# Cut an alpha release.
./scripts/release.sh v0.1.0 --pre alpha

# Cut a stable release.
./scripts/release.sh v0.1.0
```

**Tag chain** (pushed in order, each waited on before the next):

| Step | Tag pushed | Workflow triggered |
| --- | --- | --- |
| 1 | `agent-base-v<version>` | `release-agent-base.yml` |
| 2 | `agents-v<version>` | `release-spring-voyage-agents.yml` |
| 3 | `oss-agents-v<version>` | `release-oss-agent-images.yml` |
| 4 | `v<version>` | `release.yml` (platform + GitHub Release) |

After all four workflows succeed, the script verifies that every image referenced in `packages/**/*.yaml` is anonymously pullable from `ghcr.io`.

**Flags:**

| Flag | Effect |
| --- | --- |
| `--pre alpha\|beta\|rc` | Append `-<suffix>.YYYYMMDD` to the semver argument. |
| `--plan` | Print the computed tags and exit 0; no tags pushed. |
| `--force-retag` | Skip the idempotency guard (allows re-tagging an existing version). |

**Changelog finalisation** (stable releases only):

1. Before tagging, move the `## [Unreleased]` section in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, create a fresh empty `[Unreleased]` section, and merge a PR titled `Release vX.Y.Z`.
2. Run `./scripts/release.sh vX.Y.Z` from clean `main`.

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
  - `python-lint` / `python-test` — ruff and pytest for `agents/spring-voyage-agent/`.
  - `openapi-drift` — rebuilds `openapi.json` and the Kiota CLI client and fails if the working tree is dirty.
  - `required-checks` — aggregation gate for branch protection.
- **[`codeql.yml`](../../.github/workflows/codeql.yml)** — CodeQL C# analysis on pushes, pull requests, merge queue, and weekly.

### Release workflows

Releases are triggered by tag pushes only — never by merges to `main`. The table below shows each workflow and the tag prefix that activates it.

| Workflow | Tag prefix | Publishes |
| --- | --- | --- |
| [`release-agent-base.yml`](../../.github/workflows/release-agent-base.yml) | `agent-base-v*` | `ghcr.io/cvoya-com/agent-base`, `@cvoya/spring-voyage-agent-sidecar` npm package, SEA binaries |
| [`release-spring-voyage-agents.yml`](../../.github/workflows/release-spring-voyage-agents.yml) | `agents-v*` | `ghcr.io/cvoya-com/spring-voyage-agents` |
| [`release-oss-agent-images.yml`](../../.github/workflows/release-oss-agent-images.yml) | `oss-agents-v*` | Four OSS role images (software-engineering, design, product-management, program-management) |
| [`release.yml`](../../.github/workflows/release.yml) | `v*` | `ghcr.io/cvoya-com/spring-agent`, `ghcr.io/cvoya-com/agent-base`, `ghcr.io/cvoya-com/agent-dapr`, GitHub Release |
| [`release-spring-dispatcher.yml`](../../.github/workflows/release-spring-dispatcher.yml) | `dispatcher-v*` | Self-contained dispatcher binaries (5 RIDs) |

`scripts/release.sh` orchestrates steps 1–4 in dependency order. The dispatcher workflow is independent and not driven by the release script.

Each release workflow calls `gh api -X PATCH /orgs/cvoya-com/packages/container/<name> -F visibility=public` after pushing, so packages are publicly pullable from the first publish onward.

## NuGet Package Publishing

The repository does **not** publish NuGet packages. No `src/` project sets `IsPackable=true`. Consumers outside the open-source repo pin to a specific commit SHA on `main` via git submodule or project reference.

The decision to publish NuGet packages (names, registry, `IsPackable` wiring) is tracked separately in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395).

## Container Image Tagging and Publishing

Container images are published to the GitHub Container Registry (`ghcr.io/cvoya-com/`). All images are publicly pullable (no credentials required).

| Image | Published by | Description |
| --- | --- | --- |
| `ghcr.io/cvoya-com/spring-agent` | `release.yml` | Primary agent runtime (claude-code); the default image in `UnitRuntimeOptions.cs`. |
| `ghcr.io/cvoya-com/agent-base` | `release-agent-base.yml`, `release.yml` | BYOI conformance path-1 base image; bundles the A2A sidecar bridge. |
| `ghcr.io/cvoya-com/agent-dapr` | `release.yml` | Dapr-native A2A agent (path-3). |
| `ghcr.io/cvoya-com/spring-voyage-agents` | `release-spring-voyage-agents.yml` | Omnibus image: all OSS runtime CLIs pre-installed. Default for OSS unit creation. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` | `release-oss-agent-images.yml` | OSS software-engineering role agent. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-design` | `release-oss-agent-images.yml` | OSS design role agent. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management` | `release-oss-agent-images.yml` | OSS product-management role agent. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` | `release-oss-agent-images.yml` | OSS program-management role agent. |

### Tag convention

| Tag | Meaning |
| --- | --- |
| `:X.Y.Z` | Immutable version tag. Never reused. |
| `:X.Y` | Floating tag pointing at the latest patch of the `X.Y` minor line. |
| `:latest` | Floating tag pointing at the most recent stable release. Never points at a pre-release. |

Pre-release tags (e.g., `v0.1.0-alpha.20260504`) push only the immutable version tag; `:latest` and `:X.Y` are not updated.

The tag pushed to the container registry has the leading `v` stripped (e.g., git tag `v0.1.0` → image tag `0.1.0`).

### Local and VPS deployment

`deployment/deploy.sh` and `deployment/deploy-remote.sh` build images locally with Podman on the target host. They do not pull from the registry; `ghcr.io/cvoya-com/spring-agent:latest` is available for operators who prefer a pre-built image.

## Changelog

The canonical changelog is [`CHANGELOG.md`](../../CHANGELOG.md) at the repository root. It follows the [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) format. See [`CONTRIBUTING.md` § Changelog Expectations](../../CONTRIBUTING.md#changelog-expectations) for the per-PR convention.

## Helper Scripts

| Script | Purpose |
| --- | --- |
| [`scripts/release.sh`](../../scripts/release.sh) | Orchestrates the full release: computes tags, pushes them in dependency order, waits on each workflow, verifies anonymous pull. Flags: `--pre alpha\|beta\|rc`, `--plan` (dry-run), `--force-retag`. |
| [`scripts/extract-changelog-section.sh`](../../scripts/extract-changelog-section.sh) | Extracts a named section (default: `Unreleased`) from `CHANGELOG.md` and prints it to stdout. Used by `release.yml` to populate the GitHub Release body. |

## Summary Table

| Topic | State today |
| --- | --- |
| SemVer | Adopted |
| Git tags | Tag-based from `main`; sole source of truth for version |
| GitHub Releases | Automated via `release.yml` on `v*` tag push |
| NuGet packages | Not published; decision tracked in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395) |
| Container images | Published to `ghcr.io/cvoya-com/*`; all images public |
| Component release script | In place ([`scripts/release.sh`](../../scripts/release.sh)) |
| CI (build, test, format, lint) | In place ([`ci.yml`](../../.github/workflows/ci.yml), [`codeql.yml`](../../.github/workflows/codeql.yml)) |
| Release-publishing workflows | In place (five tag-scoped workflows) |
