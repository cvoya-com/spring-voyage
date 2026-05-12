# devops/install/

Source-free operator install path for Spring Voyage. Curlable
`install.sh` + `uninstall.sh` that consume the release assets published
by `.github/workflows/release.yml` on every `v*.*.*` tag.

Design recorded in [ADR-0042 — Local-host operator installer](../../docs/decisions/0042-local-operator-installer.md). The operator
narrative — prerequisites, prompts, on-disk layout, troubleshooting — lives in [`docs/guide/operator/deployment.md`](../../docs/guide/operator/deployment.md).

## Quick install

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

That's the one-liner. Two prompts: `DEPLOY_HOSTNAME` (default
`localhost`) and an opt-in GitHub-App manifest flow. `--yes` skips
both.

## What the installer does

1. Validates pre-flight: not root, `bash >= 4`, `curl`, `tar`,
   `openssl`, `podman >= 4`, ports 80/443 free, `~/.local/bin` on
   PATH, `podman machine` running on macOS.
2. Resolves the release tag (`--version`, `$SPRING_VOYAGE_VERSION`,
   else latest stable from the GitHub API).
3. Downloads three release-attached archives plus `SHA256SUMS`:
   - `spring-voyage-<v>-bundle.tar.gz`
   - `spring-dispatcher-<v>-<rid>.tar.gz`
   - `spring-<v>-<rid>.tar.gz`
4. Verifies each archive's checksum against `SHA256SUMS`.
5. Extracts to `~/.spring-voyage/releases/<v>/{bundle,dispatcher,cli}/`.
6. Reads `bundle/manifest.json` for the platform image ref and `podman
   pull`s it.
7. Symlinks `~/.spring-voyage/current -> releases/<v>/bundle`,
   `~/.local/bin/spring -> releases/<v>/cli/spring`, and writes a
   `spring-voyage` wrapper that dispatches `install|uninstall|status`.
8. Generates `~/.spring-voyage/spring.env` (mode 0600) with auto-
   generated secrets and auto-derived paths — see the env-var table
   in [ADR-0042](../../docs/decisions/0042-local-operator-installer.md).
9. Runs `SPRING_ENV_FILE=... bundle/deploy.sh up`.
10. Optionally invokes `spring github-app register --env-path …
    --write-env` if the operator opts in.

## Uninstall

`uninstall.sh` ships at the same release URL as `install.sh` and is
also bundled into the tarball as `bundle/uninstall.sh`. The
`spring-voyage` wrapper invokes the bundle-side copy so the script
survives install-root cleanup mid-teardown.

| Command | Removes | Preserves |
|---|---|---|
| `spring-voyage uninstall` (default) | Containers, volumes, networks, images, `~/.spring-voyage/releases/`, `~/.spring-voyage/current`, `~/.local/bin/spring`, `~/.local/bin/spring-voyage`. | `spring.env`, `~/.spring-voyage/host/`, `~/.spring-voyage/workspaces/`. |
| `spring-voyage uninstall --purge` | All of the above + `spring.env`, `host/`, `workspaces/`. | Nothing. Factory reset. |

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
bash devops/install/tests/test-install.sh
```

End-to-end smoke is deferred to the operator — cut a real
`v*.*.*` tag, then `curl … install.sh | bash` from a clean machine
and verify the stack at `https://${DEPLOY_HOSTNAME}`.

## Related

- [Operator deployment guide](../../docs/guide/operator/deployment.md) — full operator narrative, including post-install steps and troubleshooting.
- [ADR-0042](../../docs/decisions/0042-local-operator-installer.md) — design decisions captured.
- [`devops/deploy/`](../deploy/README.md) — runtime scripts both the source-clone and source-free paths share.
- [`devops/build/`](../build/README.md) — source-clone build path (orthogonal; not used by `install.sh`).
