# Releases and Versioning

This document describes how Spring Voyage is versioned and how releases are cut.

## Semantic Versioning

Spring Voyage follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html): `MAJOR.MINOR.PATCH`.

| Change type | Bump | Examples |
| --- | --- | --- |
| **MAJOR** — incompatible changes that require users or extenders to modify their code or configuration | `MAJOR` | Remove or rename a public type/member in `Cvoya.Spring.Core`; change an interface signature; rename a Dapr state key in a way that loses data on upgrade; change default DI registrations so existing hosts fail to start; drop support for a runtime (e.g., .NET version) |
| **MINOR** — backwards-compatible additions | `MINOR` | New interface, new connector, new API endpoint, new CLI command, new optional configuration |
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

### Pre-release stability

While the project is in pre-release on the `1.0.0` line (e.g. `1.0.0-alpha.<date>`, `1.0.0-beta.<date>`, `1.0.0-rc.<date>`), breaking changes are permitted between pre-release tags. We will still flag them as breaking in the changelog. Once the project reaches stable `1.0.0`, the full SemVer contract applies.

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
spring-voyage-v1.0.0-alpha.20260504       # first alpha on 2026-05-04
spring-voyage-v1.0.0-alpha.20260504.1     # second alpha on the same day
spring-voyage-v1.0.0-rc.20260601          # release candidate on 2026-06-01
spring-voyage-v1.0.0                      # stable release
```

The git tag carries the `spring-voyage-v` prefix; the embedded SemVer string (`1.0.0-alpha.20260504`, used in image tags, the `manifest.json` `version` field, `CHANGELOG.md` headings, and the GitHub Release title's trailing `v<version>`) is the prefix-stripped form. Tags shaped `v1.0.0` or `dispatcher-v1.0.0` are historical; only `spring-voyage-v*` triggers the release pipeline today.

The git tag is the sole source of truth for the release version. There is no `VERSION` file.

Pre-release versions are published alongside (not in place of) the most recent stable version; consumers must opt in explicitly.

## How Releases Are Cut

Spring Voyage uses **one tag, one workflow, one GitHub Release**. A single `spring-voyage-v<version>` tag triggers `release.yml`, which builds and publishes every artefact — agent images (multi-arch), platform image, sidecar SEA binaries, dispatcher binaries, `spring` CLI binaries, `Cvoya.Spring.Cli` .NET tool (to nuget.org), deployment bundle, `install.sh`, `SHA256SUMS`, and the GitHub Release — in one run. The previous three-workflow / three-tag chain (`agent-base-v*`, `oss-agents-v*`, `dispatcher-v*` / `v*.*.*`) was collapsed in [#2172](https://github.com/cvoya-com/spring-voyage/issues/2172) and [#2229](https://github.com/cvoya-com/spring-voyage/issues/2229).

Use `eng/release/release.sh` to cut a release. The script pushes the tag, watches `release.yml` to completion, and verifies that every image referenced in `packages/**/*.yaml` is anonymously pullable from `ghcr.io`.

```bash
# Dry-run: print the computed tag without pushing anything.
./eng/release/release.sh v1.0.0 --pre alpha --plan

# Cut an alpha release.
./eng/release/release.sh v1.0.0 --pre alpha

# Cut an alpha release and make it the current "latest" (see below).
./eng/release/release.sh v1.0.0 --pre alpha --latest

# Cut a stable release.
./eng/release/release.sh v1.0.0
```

**Flags:**

| Flag | Effect |
| --- | --- |
| `--pre alpha\|beta\|rc` | Append `-<suffix>.YYYYMMDD` to the semver argument. |
| `--latest` | Move `:latest` to this release — the container `:latest` tags **and** the GitHub Release "Latest" badge. Redundant for stable (always latest); use it for `--pre`. See [Marking a pre-release as latest](#marking-a-pre-release-as-latest). |
| `--plan` | Print the computed tag and exit 0; no tag pushed. |
| `--force-retag` | Skip the idempotency guard (allows re-tagging an existing version). |

**Changelog finalisation** (stable releases only):

1. Before tagging, move the `## [Unreleased]` section in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, create a fresh empty `[Unreleased]` section, and merge a PR titled `Release vX.Y.Z`.
2. Run `./eng/release/release.sh vX.Y.Z` from clean `main`.

### Marking a pre-release as latest

Two distinct surfaces answer "what is the latest release", and they move independently:

- **Container `:latest` tags** in GHCR (`ghcr.io/cvoya-com/spring-voyage-*:latest`). The catalog packages under [`packages/`](../../packages/) pin `:latest` (e.g. `image: …/spring-voyage-claude-code-base:latest`), so this is the tag a `spring package install` / portal install actually pulls. If `:latest` does not exist, installs fail with `manifest unknown`.
- **The GitHub Release "Latest" badge** on the Releases page.

By default `release.yml` moves both only for **stable** releases; the immutable `:<version>` tag is the only thing a pre-release publishes. That is correct once `v1.0.0` ships, but during the pre-release line there is no stable release, so nothing carries `:latest` — and the `:latest`-pinning catalog packages cannot be installed.

`--latest` opts a chosen pre-release into being latest. `release.sh --latest` writes a `Mark-Latest: true` trailer onto the **annotated** release tag; `release.yml`'s `resolve` job reads that trailer (or the `mark_latest` input on a manual `workflow_dispatch`) and exports `mark_latest`, which gates the `:latest` push for every image and the `gh release edit --latest` badge in `finalize-release`. The `:MAJOR_MINOR` channel tag (e.g. `:1.0`) is deliberately **not** affected — it stays stable-only so `:1.0` never resolves to a pre-release.

```bash
# During the v1.0.0 alpha line: cut today's alpha and make it the current default.
./eng/release/release.sh v1.0.0 --pre alpha --latest
```

`:latest` only moves when a release explicitly claims it, so the most recently `--latest`-marked alpha stays latest until another release displaces it. Once `v1.0.0` (stable) ships it reclaims `:latest` automatically and `--latest` is no longer needed.

To promote an **already-published** pre-release without cutting a new one, re-run the workflow for its tag with the input set: dispatch `release.yml` with `tag=spring-voyage-v<version>` and `mark_latest=true`.

### Draft-then-finalize behaviour

`release.yml` creates the GitHub Release as a **draft** immediately after the CI gate succeeds (job: `create-draft-release`). Every publish job — agent images, platform image, sidecar SEA binaries, per-RID host archives, `Cvoya.Spring.Cli` NuGet tool, top-level `install.sh` and `install-<v>.sh` — attaches its assets to that draft. A final `finalize-release` job promotes the draft to a published release only after every publish job has succeeded.

The consequence for operators watching the Releases page during a release: the entry shows up as a draft within a minute or two of the tag push and stays drafted for roughly 10–20 minutes (the long pole is the multi-arch image builds). If any publish job fails, the draft sits there until the workflow is re-run successfully; consumers never see a partially-published release. The GitHub Release title is `Spring Voyage v<version>` and the body is resolved at the tagged commit by [`eng/release/resolve-release-notes.sh`](../../eng/release/resolve-release-notes.sh) — the curated `docs/releases/<version>.md` notes if present, otherwise the `[Unreleased]` section of `CHANGELOG.md` — followed by an auto-generated asset reference table. See [Release notes](#release-notes).

### Patch releases on prior versions

If a critical fix needs to ship on an older minor line (e.g., current is `1.1.x` and we need to patch `1.0.x`), a `release/1.0` branch is created from the `spring-voyage-v1.0.y` tag, the fix is cherry-picked, and a new tag is cut from that branch. This is an exception path — the default is "fix on `main`, ship in the next release".

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

### Release workflow

Releases are triggered by tag pushes only — never by merges to `main`. One workflow, one tag prefix.

| Workflow | Tag prefix | Publishes |
| --- | --- | --- |
| [`release.yml`](../../.github/workflows/release.yml) | `spring-voyage-v*` | `ghcr.io/cvoya-com/spring-voyage-agent-base` (multi-arch), `ghcr.io/cvoya-com/spring-voyage-claude-code-base`, `ghcr.io/cvoya-com/spring-voyage-agent`, `ghcr.io/cvoya-com/spring-voyage` (platform image), the four OSS role images (`spring-voyage-agent-oss-*`, multi-arch), sidecar SEA binaries (3 targets), per-RID host archives bundling the deployment scripts + `spring` CLI + dispatcher in one tarball (5 RIDs), `Cvoya.Spring.Cli` .NET tool to nuget.org, top-level `install.sh` and version-baked `install-<v>.sh`, `SHA256SUMS`, GitHub Release |

GHCR packages are publicly pullable from the first publish onward via the `cvoya-com` org's **Inherit access from source repository** setting (Org Settings → Packages → Default package settings) combined with the `org.opencontainers.image.source=https://github.com/cvoya-com/spring-voyage` OCI label every release-built image carries via `docker/metadata-action@v6`. The GitHub REST API has no public endpoint to flip org-package visibility from a workflow, so `release.yml` does not attempt one.

### Release-attached files

Every release attaches the following files to its GitHub Release. Operators download the unversioned `install.sh` (or its version-baked `install-<v>.sh` companion) and the matching per-RID host archive; the installer handles the rest.

| Asset | Description |
| --- | --- |
| `install.sh` | Source-free installer entry point — the `curl` command in the README fetches this file directly off the release. Resolves the version from `--version`, `$SPRING_VOYAGE_VERSION`, or the GitHub API. |
| `install-<v>.sh` | Version-baked installer companion — a copy of `install.sh` with the release version compiled into the script. Refuses to install any other version. Use this when documentation pins to a specific release. |
| `spring-voyage-<v>-<rid>.{tar.gz,zip}` | Per-RID operator host archive (5 RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`). Each archive contains three top-level subdirectories — `bundle/` (deployment scripts, `voyage` wrapper, `uninstall.sh`, Dapr component templates, `manifest.json`), `cli/` (the self-contained `spring` CLI binary), and `dispatcher/` (the self-contained `Cvoya.Spring.Dispatcher` binary). One download per host. |
| `spring-voyage-agent-sidecar-<v>-<target>` | A2A sidecar bridge SEA binaries (3 targets: `linux-amd64`, `linux-arm64`, `darwin-arm64`). Used by BYOI conformance path 2 — extenders `curl` the binary into a custom agent image; not consumed by `install.sh`. |
| `SHA256SUMS` | `sha256sum -c`-consumable manifest covering every other release-attached file — the per-RID host archives, both installer scripts, and the sidecar SEA binaries. |

The deployment bundle (`bundle/`) is not a standalone downloadable asset — it ships only as a subdirectory inside every per-RID host archive. The same bundle tree is staged once per release and copied into all five archives so the operator-facing scripts are identical across platforms.

## NuGet Package Publishing

`Cvoya.Spring.Cli` is published to [nuget.org](https://www.nuget.org/packages/Cvoya.Spring.Cli) as a .NET tool. Users with the .NET runtime installed can use NuGet instead of the per-RID self-contained CLI binaries attached to the GitHub Release:

```bash
dotnet tool install -g Cvoya.Spring.Cli
```

The `publish-spring-cli-nuget` job in `release.yml` packs `src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj` (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>spring</ToolCommandName>`) and pushes the `.nupkg` to nuget.org using [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing). The `NuGet/login@v1` action exchanges the workflow's GitHub OIDC token for a short-lived NuGet API key valid for the run; no long-lived secret is stored in the repo. The job reads the nuget.org account username from a `NUGET_USER` repository variable (Settings → Secrets and variables → Actions → Variables) and skips the whole job — logged as a workflow warning — when that variable is unset, so a fork without a NuGet account can still run `release.yml`.

Trusted-publisher setup on nuget.org (one-time, owner-side): create a trusted publisher entry on the `Cvoya.Spring.Cli` package scoped to repo `cvoya-com/spring-voyage`, ref pattern `refs/tags/spring-voyage-v*`, job name `publish-spring-cli-nuget`. The cvoya-com account already has this configured.

`src/Cvoya.Spring.Cli/README.md` is the package-facing README shipped inside the `.nupkg` (via `<PackageReadmeFile>`); it is intentionally narrower than the repo-root `README.md` — focused on the CLI tool, not the platform.

Other Spring Voyage packages (`Cvoya.Spring.Core`, `Cvoya.Spring.Dapr`, the connector abstractions) are **not** published to NuGet. Consumers outside the open-source repo pin to a specific commit SHA on `main` via git submodule or project reference. Publication of those packages is tracked separately in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395).

## Container Image Tagging and Publishing

Container images are published to the GitHub Container Registry (`ghcr.io/cvoya-com/`). All images are publicly pullable (no credentials required).

This table is the canonical, ground-truth list of every published image. The authoritative source is the build matrix in [`release.yml`](../../.github/workflows/release.yml) (`publish-platform-image`, `publish-agent-base-image`, `publish-agent-images`, `publish-oss-agent-images`); keep this table in sync with it.

| Image | Published by | Description |
| --- | --- | --- |
| `ghcr.io/cvoya-com/spring-voyage` | `release.yml` | Platform image (API + Worker + Web + Dapr CLI); consumed by `deploy.sh` via `SPRING_PLATFORM_IMAGE`. |
| `ghcr.io/cvoya-com/spring-voyage-agent-base` | `release.yml` | BYOI conformance path-1 base image; bundles the A2A sidecar bridge. The three CLI runtime images below are built `FROM` it. |
| `ghcr.io/cvoya-com/spring-voyage-claude-code-base` | `release.yml` | Claude Code runtime image; the default image for the `claude-code` runtime. |
| `ghcr.io/cvoya-com/spring-voyage-gemini-base` | `release.yml` | Gemini CLI runtime image; the default image for the `gemini` runtime. |
| `ghcr.io/cvoya-com/spring-voyage-codex-base` | `release.yml` | Codex CLI runtime image; the default image for the `codex` runtime. |
| `ghcr.io/cvoya-com/spring-voyage-agent` | `release.yml` | Dapr-native A2A agent (path-3). |
| `ghcr.io/cvoya-com/spring-voyage-langgraph-orchestrator` | `release.yml` | Magazine LangGraph orchestrator — the always-on `a2a-process` engine (path-3, ADR-0066); default image for the `a2a-process` runtime. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` | `release.yml` | OSS software-engineering role agent. |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` | `release.yml` | OSS program-management role agent. |

### Tag convention

| Tag | Meaning |
| --- | --- |
| `:X.Y.Z` | Immutable version tag. Never reused. |
| `:X.Y` | Floating tag pointing at the latest patch of the `X.Y` minor line. |
| `:latest` | Floating tag pointing at the most recent stable release. Never points at a pre-release. |

Pre-release tags (e.g., `spring-voyage-v1.0.0-alpha.20260504`) push only the immutable version tag; `:latest` and `:X.Y` are not updated.

The container-registry tag is the prefix-stripped SemVer string (e.g., git tag `spring-voyage-v1.0.0` → image tag `1.0.0`).

### Local deployment

`eng/build/build.sh` builds images locally with Podman. The local build writes the same canonical GHCR refs that release builds publish, so the dispatcher can resolve runtime defaults from the local image store before any registry pull.

## Changelog

The canonical changelog is [`CHANGELOG.md`](../../CHANGELOG.md) at the repository root. It follows the [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) format. See [`CONTRIBUTING.md` § Changelog Expectations](../../CONTRIBUTING.md#changelog-expectations) for the per-PR convention.

## Release notes

The `CHANGELOG.md` is the exhaustive, per-PR technical record. The **release notes** are the human narrative that becomes the GitHub Release body — "what's in this release and why you'd care" for someone landing on the Releases page. They live under [`docs/releases/`](../../docs/releases/), one file per release line, checked in **before** the tag is cut.

**How the body is resolved.** At tag time, `create-draft-release` runs [`eng/release/resolve-release-notes.sh <version>`](../../eng/release/resolve-release-notes.sh), which picks the body in this order, then the workflow appends the auto-generated asset/image tables:

1. `docs/releases/<full-version>.md` — exact, e.g. `1.0.0-rc.1.md` (rarely needed).
2. `docs/releases/<major.minor.patch>.md` — the release line, e.g. `1.0.0.md`. **The common case** — reused by every `-alpha`/`-beta`/`-rc` and the final stable cut of that version.
3. `CHANGELOG.md` `[Unreleased]` — fallback when no curated file exists (preserves the prior behaviour; nothing breaks if you skip the notes file).

**Authoring.** Run the `/release-notes` Claude Code command (defined in [`.claude/commands/release-notes.md`](../../.claude/commands/release-notes.md)) to draft or refresh `docs/releases/<line>.md` from the changelog, the README, and the previous notes; review it, then include it in the release-prep PR. Because the file is read from the **tagged commit**, it must be merged to `main` before `release.sh` is run. Conventions and the "no asset/image tables" rule are in [`docs/releases/README.md`](../../docs/releases/README.md).

## Helper Scripts

| Script | Purpose |
| --- | --- |
| [`eng/release/release.sh`](../../eng/release/release.sh) | Orchestrates the full release: computes tags, pushes them in dependency order, waits on each workflow, verifies anonymous pull. Flags: `--pre alpha\|beta\|rc`, `--plan` (dry-run), `--force-retag`. |
| [`eng/release/resolve-release-notes.sh`](../../eng/release/resolve-release-notes.sh) | Resolves the GitHub Release body for a version: curated `docs/releases/<version>.md` → release-line `docs/releases/<x.y.z>.md` → `CHANGELOG.md` `[Unreleased]` fallback. Used by `release.yml`. |
| [`eng/release/extract-changelog-section.sh`](../../eng/release/extract-changelog-section.sh) | Extracts a named section (default: `Unreleased`) from `CHANGELOG.md` and prints it to stdout. The fallback used by `resolve-release-notes.sh`. |

## Summary Table

| Topic | State today |
| --- | --- |
| SemVer | Adopted |
| Git tags | Tag-based from `main`; sole source of truth for version |
| GitHub Releases | Automated via `release.yml` on `spring-voyage-v*` tag push; draft-then-finalize |
| NuGet packages | `Cvoya.Spring.Cli` published to nuget.org as a .NET tool; remaining packages (Core, Dapr, connectors) not published — tracked in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395) |
| Container images | Published to `ghcr.io/cvoya-com/*`; all images public |
| Component release script | In place ([`eng/release/release.sh`](../../eng/release/release.sh)) |
| CI (build, test, format, lint) | In place ([`ci.yml`](../../.github/workflows/ci.yml), [`codeql.yml`](../../.github/workflows/codeql.yml)) |
| Release-publishing workflow | In place (single workflow on `spring-voyage-v*`) |
