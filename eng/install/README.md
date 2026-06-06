# eng/install/

Source-free operator install path for Spring Voyage. Curlable
`install.sh` + `uninstall.sh` that consume the release assets published
by `.github/workflows/release.yml` on every `spring-voyage-v*` tag.

Design recorded in [ADR-0042 — Local-host operator installer](../../docs/decisions/0042-local-operator-installer.md). The operator
narrative — prerequisites, prompts, on-disk layout, troubleshooting — lives in [`docs/guide/operator/deployment.md`](../../docs/guide/operator/deployment.md).

## Quick install

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

That's the one-liner. Two prompts: `DEPLOY_HOSTNAME` (default
`localhost`) and an opt-in GitHub-App manifest flow. `--yes` skips
both.

To pin to a specific release, use the version-baked companion
attached to every release (`install-<v>.sh`):

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/download/spring-voyage-v1.0.0/install-1.0.0.sh | bash
```

`install-<v>.sh` is byte-identical to `install.sh` except its
`BAKED_VERSION` is filled in at release time; it refuses to install
any other version.

## What the installer does

1. Validates pre-flight: not root, `bash >= 4`, `curl`, `tar`,
   `openssl`, `podman >= 4`, ports 80/443 free, `~/.local/bin` on
   PATH, `podman machine` running on macOS.
2. Resolves the release tag (`--version`, `$SPRING_VOYAGE_VERSION`,
   else latest stable from the GitHub API). Accepted version inputs:
   `1.0.0`, `v1.0.0`, or `spring-voyage-v1.0.0`.
3. Downloads two release-attached files:
   - `spring-voyage-<v>-<rid>.{tar.gz|zip}` — the per-RID host
     archive (contains `bundle/`, `cli/`, and `dispatcher/`
     subdirectories in one tarball).
   - `SHA256SUMS`.
4. Verifies the archive's checksum against `SHA256SUMS`.
5. Extracts to `~/.spring-voyage/releases/<v>/` so the existing
   `{bundle,dispatcher,cli}/` paths land where the rest of the
   installer expects them.
6. Reads `bundle/manifest.json` for the platform image ref and `podman
   pull`s it.
7. Symlinks `~/.spring-voyage/current -> releases/<v>/bundle`,
   `~/.local/bin/spring -> releases/<v>/cli/spring`, and copies the
   `voyage` operator wrapper (`status | logs | restart |
   version | install | uninstall`) into `~/.local/bin/`.
8. Generates `~/.spring-voyage/spring.env` (mode 0600) with auto-
   generated secrets and auto-derived paths — see the env-var table
   in [ADR-0042](../../docs/decisions/0042-local-operator-installer.md).
9. Runs `SPRING_ENV_FILE=... bundle/deploy.sh up`.
10. Optionally invokes `spring github-app register --env-path …
    --write-env` if the operator opts in.
11. Points the `spring` CLI at this deployment by running
    `spring config set endpoint <DEPLOY_SCHEME://host:port>` (the
    Caddy-fronted API), so the CLI doesn't fall back to its
    `http://localhost:5000` dev default. The CLI merges the endpoint
    into `~/.spring/config.json` (mode 0600), so a re-install refreshes
    a changed host port while preserving any stored auth token; override
    anytime with `SPRING_API_URL`.

## Operator wrapper (`voyage`)

`eng/install/voyage` is a stand-alone bash script. The
release pipeline ships it inside the bundle as `bundle/voyage`,
and `install.sh` copies it to `~/.local/bin/voyage` so it is on
the operator's `PATH`. The wrapper reads `SPRING_VOYAGE_HOME` and
`SPRING_ENV_FILE` from the environment with sensible defaults
(`~/.spring-voyage` and `~/.spring-voyage/spring.env`), so the same
checked-in script works for every install without per-install
substitution.

| Subcommand | Purpose |
|---|---|
| `voyage status` | Install version (from `manifest.json`), container state (delegated to `deploy.sh status`), dispatcher PID + liveness, web URL, log paths. |
| `voyage logs [service]` | `logs` tails all containers via `deploy.sh logs`. `logs <service>` tails one. `logs dispatcher` is special-cased to tail `~/.spring-voyage/host/spring-dispatcher.log` (the host-process dispatcher is not a container). |
| `voyage restart` | One-line delegate to `deploy.sh restart`. |
| `voyage install` | (Re-)install Spring Voyage by re-running the canonical `install.sh` from the latest release. |
| `voyage uninstall` | Delegates to the bundle's `uninstall.sh`; `--purge` for factory reset. |
| `voyage version` | Prints the installed version and platform image tag from `manifest.json`. |
| `voyage help` | Show usage. |

## Uninstall

`uninstall.sh` ships at the same release URL as `install.sh` and is
also bundled into the tarball as `bundle/uninstall.sh`. The
`voyage` wrapper invokes the bundle-side copy so the script
survives install-root cleanup mid-teardown.

| Command | Removes | Preserves |
|---|---|---|
| `voyage uninstall` (default) | Containers, volumes, networks, images, `~/.spring-voyage/releases/`, `~/.spring-voyage/current`, `~/.local/bin/spring`, `~/.local/bin/voyage`. | `spring.env`, `~/.spring-voyage/host/`, `~/.spring-voyage/workspaces/`. |
| `voyage uninstall --purge` | All of the above + `spring.env`, `host/`, `workspaces/`. | Nothing. Factory reset. |

Flags:

- `--yes` skips the interactive confirmation prompt.
- `--force` bypasses the "containers still running" refusal.
- Both modes are idempotent — re-running on a clean system exits 0.

## Tests

`tests/test-install.sh` is a fixture-driven dry-run suite. It builds a
stub release in a temp directory, shadows `curl`/`podman`/`uname` via
a PATH-injected stub directory, and runs `install.sh` end-to-end
against an isolated `$HOME`. Covers happy-path install, re-run
refusal, `--force` bypass, RID detection on all four POSIX RIDs,
generated-env-file shape and mode, and uninstall preserve/purge/
idempotence.

```bash
bash eng/install/tests/test-install.sh
```

End-to-end smoke is deferred to the operator — cut a real
`v*.*.*` tag, then `curl … install.sh | bash` from a clean machine
and verify the stack at `https://${DEPLOY_HOSTNAME}`.

## Related

- [Operator deployment guide](../../docs/guide/operator/deployment.md) — full operator narrative, including post-install steps and troubleshooting.
- [ADR-0042](../../docs/decisions/0042-local-operator-installer.md) — design decisions captured.
- [`eng/deploy/`](../deploy/README.md) — runtime scripts both the source-clone and source-free paths share.
- [`eng/build/`](../build/README.md) — source-clone build path (orthogonal; not used by `install.sh`).
