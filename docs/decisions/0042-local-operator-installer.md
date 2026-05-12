# 0042 — Local-host operator installer (curlable `install.sh`)

- **Status:** Accepted — 2026-05-11.
- **Date:** 2026-05-11.
- **Tracks:** [#2158](https://github.com/cvoya-com/spring-voyage/issues/2158) (epic), [#2180](https://github.com/cvoya-com/spring-voyage/issues/2180) (installer PR).
- **Related code:** `devops/install/install.sh`, `devops/install/uninstall.sh`, `devops/deploy/deploy.sh`, `devops/deploy/setup.sh`, `src/Cvoya.Spring.Cli/Commands/GitHubAppCommand.cs`, `.github/workflows/release.yml`.
- **Related docs:** [`docs/guide/operator/deployment.md`](../guide/operator/deployment.md), [`devops/install/README.md`](../../devops/install/README.md), [`devops/deploy/README.md`](../../devops/deploy/README.md).
- **Related ADRs:** [ADR-0012 — spring-dispatcher service extraction](0012-spring-dispatcher-service-extraction.md), [ADR-0028 — Tenant-scoped runtime topology](0028-tenant-scoped-runtime-topology.md). Also [#1063](https://github.com/cvoya-com/spring-voyage/issues/1063) (dispatcher-on-host).

## Context

Before this ADR an operator stood up Spring Voyage by cloning the repository and running `devops/build/build.sh` followed by `devops/deploy/deploy.sh up`. That path stays — it is the contributor loop — but it is the only path. Most operators do not want a source checkout to run a release; they want to download something, run it, and have a stack.

[#2158](https://github.com/cvoya-com/spring-voyage/issues/2158) was triaged into v0.1 with `needs-thinking`. The open question was the delivery mechanism (curlable `install.sh` vs. native `spring` CLI subcommand vs. Homebrew). PR0 ([#2185](https://github.com/cvoya-com/spring-voyage/issues/2185)) reorganised `deployment/` into `devops/{build,deploy,install}/` to give each audience its own home. PR1 ([#2187](https://github.com/cvoya-com/spring-voyage/issues/2187)) extended the release pipeline to publish the platform image, deployment bundle, dispatcher binaries, `spring` CLI binaries, and `SHA256SUMS` on every `v*.*.*` tag. This ADR records the eight design decisions that govern the resulting installer.

## Decision

### 0. Maximally automate, minimize prompts.

Anything the installer can generate, infer, or look up — it does. Operator-facing prompts are limited to (a) `DEPLOY_HOSTNAME` and (b) opt-in for the GitHub App manifest flow. Passwords, AES keys, paths, image tags, Dapr components location, OAuth redirect URI, and dispatcher-bin path are all derived or generated automatically. `--yes` mode bypasses both prompts (uses defaults: `localhost` + skip GitHub App).

### 1. Curlable `install.sh` is the canonical entry-point.

Not Homebrew/apt/snap/winget — those are deferred follow-ups that can later wrap the same release assets. The canonical one-liner is:

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

### 2. Podman-first, local-host only.

The installer targets the same machine as the stack. No SSH, no remote container contexts, no Docker Desktop assumptions. Docker is a manual secondary path (compose file shipped in the bundle, no installer support). `install.sh` and `uninstall.sh` are forbidden from containing `ssh`, `scp`, `--host`, `DOCKER_HOST`, `CONTAINER_HOST`, or `--context`; this is enforced by negative grep in CI smoke.

### 3. Native `spring` CLI binaries on the 5-RID matrix.

`spring-<v>-<rid>.{tar.gz|zip}` is published for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`. `install.sh` symlinks `~/.local/bin/spring` on POSIX systems. Distribution wrappers (Homebrew/winget) are deferred but will reuse these assets. Windows operators follow the manual install docs in v0.1.

### 4. No upgrades in v0.1.

Re-running `install.sh` on an existing install fails fast and tells the operator to `uninstall` first. The version-matrix / migration story is filed as a v0.2 issue ([#2179](https://github.com/cvoya-com/spring-voyage/issues/2179)). The supported v0.1 model is "uninstall, then install new version, then restore data manually if you kept volumes." Anything smarter requires a versioned migration table and per-pair upgrade tests we are not building in this milestone.

### 5. Uninstall is first-class, two modes.

`devops/install/uninstall.sh` ships at the same release URL as `install.sh` and is also bundled into the deployment tarball so it survives install-root cleanup. Two modes:

- **Default** — stops containers, removes platform images/volumes/networks, deletes `~/.spring-voyage/releases/`, `~/.spring-voyage/current`, `~/.local/bin/spring`, `~/.local/bin/spring-voyage`. **Preserves** `spring.env`, `~/.spring-voyage/host/`, `~/.spring-voyage/workspaces/`. Prints the preserved paths and how to delete them.
- **`--purge`** — everything above plus `spring.env`, `~/.spring-voyage/host/`, `~/.spring-voyage/workspaces/`. Factory-reset.

Both modes prompt for confirmation (skipped with `--yes`). Both are idempotent. The default mode never destroys operator data without an explicit second-step opt-in.

### 6. Local-clone path unchanged.

`devops/build/build.sh` keeps tagging `localhost/spring-voyage:latest`; `devops/deploy/deploy.sh` keeps reading `SPRING_PLATFORM_IMAGE` from `spring.env`. The installer is purely additive — it does not touch the source-clone path. A contributor with a clone of the repository never touches `install.sh`.

### 7. Single release-version surface.

Platform image, deployment bundle, dispatcher binaries, and `spring` CLI binaries all share one release tag. The release pipeline ([#2187](https://github.com/cvoya-com/spring-voyage/issues/2187)) publishes everything from `v*.*.*` tags. `manifest.json` inside the bundle pins the version, the platform image ref, and the dispatcher / CLI versions; `install.sh` reads it as the single source of truth for what to pull and where to symlink.

## Consequences

### Gains

- **Zero-to-running in a single command.** Operators with Podman 4+ on Linux/macOS go from a clean home directory to a stack at `https://localhost` without cloning the repository.
- **One release surface.** The platform image, the deployment bundle, the dispatcher binaries, and the `spring` CLI binaries are now produced by one workflow on one tag with a unified `SHA256SUMS` index. There is no second cadence for the dispatcher or the CLI.
- **The CLI is the supported interface to the deployment.** Because `~/.local/bin/spring` is installed by `install.sh`, the post-install steps that today require a source checkout (`spring github-app register`, `spring secret create --scope tenant`) work out of the box.
- **The GitHub-App manifest flow is reachable without leaving the installer.** Operators who opt in get a single-prompt path that drives the existing `spring github-app register` end-to-end; declining is non-blocking and gets a clear re-run hint.
- **First-class uninstall removes the "stuck install" foot-gun.** Operators who hit a broken state have a documented, scripted teardown — not a list of `podman` and `rm -rf` commands in a wiki.

### Costs

- **Two parallel install paths to support.** Source-clone (`build.sh` + `deploy.sh up`) and source-free (`install.sh`) must both keep working through v0.1. We mitigate this by having `install.sh` invoke `deploy.sh up` directly out of the bundle — the runtime path is shared.
- **`SHA256SUMS` is a coordination point.** Adding a new release-attached asset means updating both the publish workflow and the installer's verification list. Schema is intentionally minimal in v0.1 (`bundle_schema_version: 1` in `manifest.json`).
- **No upgrade path in v0.1.** Operators who want to move between versions go through `uninstall` then `install` and re-bootstrap secrets. Documented at install time and in the release notes. Tracked under [#2179](https://github.com/cvoya-com/spring-voyage/issues/2179).
- **GitHub-imposed manual steps remain.** Clicking "Create" on the GitHub manifest page and installing the App on a repo/org are not automatable — the installer prints exactly what to click and when.

## Alternatives considered

- **Homebrew formula as the canonical entry-point.** Rejected for v0.1 — Homebrew is macOS / Linux only, requires its own publishing pipeline, and pushes new operators through a tool they may not already have. A formula wrapping the same release assets is a viable follow-up ([deferred from #2158](https://github.com/cvoya-com/spring-voyage/issues/2158)).
- **`spring install` as a CLI subcommand.** Rejected for v0.1 — chicken-and-egg: the operator has nothing yet, so the CLI has to be acquired first. Only meaningful after CLI distribution is settled, which Homebrew/winget would gate.
- **Docker Desktop-first installer.** Rejected — Podman is the supported single-host runtime for the OSS stack ([ADR-0012](0012-spring-dispatcher-service-extraction.md), [#1063](https://github.com/cvoya-com/spring-voyage/issues/1063)). Docker is a manual secondary path (compose file in the bundle, no installer support).
- **Remote / multi-host / Kubernetes installer.** Rejected — explicitly out of scope for v0.1 ([ADR-0028](0028-tenant-scoped-runtime-topology.md) covers tenant topology, but tenant-on-cluster is a Spring Voyage Cloud concern, not OSS). Installer has no `--host`, no SSH, no remote container contexts.

## Revisit criteria

- If macOS / Linux operators start asking for Homebrew, file the formula wrapping the same assets — no change to this ADR.
- If we cut a v0.2 with breaking schema changes, the upgrade story tracked under [#2179](https://github.com/cvoya-com/spring-voyage/issues/2179) lands and this ADR gets a follow-up amendment.
- If the bundle schema grows (more pinned components, alternative platform-image refs per RID), bump `bundle_schema_version` and document the new contract here.
