# Claude Code cloud environment

Configuration for running Spring Voyage in a [Claude Code cloud
environment](https://code.claude.com/docs/en/claude-code-on-the-web) — a managed
sandbox for the inner dev loop: `/build`, `/test`, `/lint`.

It replicates the CI toolchain in [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)
so the commands behave identically to a local checkout and to CI.

## What this covers

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 10.x (`global.json` pins `10.0.100`) | build + test the solution |
| Node.js | 20 | portal + connector web workspaces (`package.json` requires `node>=20`) |
| npm | 10+ | workspace install (`npm ci`) |
| Dapr CLI | 1.14.1 + `dapr init --slim` | actor tests (CI installs it before `dotnet test`) |
| dotnet tools | Kiota `1.32.2`, `dotnet-ef` `10.0.8` | restored via `dotnet tool restore` |
| ruff | latest | Python lint for `agents/` |

**No Docker/Podman is required for build/test/lint.** The integration tests use
in-memory EF (not Testcontainers), and the CI Test job runs no Postgres service —
the only runtime dependency is Dapr, installed in slim (containerless) mode.
Running the full agent stack (host dispatcher + Podman + Postgres) is a different,
much heavier setup and is intentionally **out of scope** for this environment.

## How to fill in the "New cloud environment" form

### Name

```
Spring Voyage (.NET 10 · Node 20 · Dapr)
```

### Network access

Select **Trusted**. The setup reaches only standard package/tooling
infrastructure that the Trusted allowlist is built for:

- `api.nuget.org` — NuGet restore
- `registry.npmjs.org` — `npm ci`
- `pypi.org` — ruff
- `github.com`, `raw.githubusercontent.com`, GitHub release CDN — nvm & Dapr
  install scripts + Dapr binaries
- `dot.net`, `builds.dotnet.microsoft.com` — .NET SDK install
- `nodejs.org` — only if Node 20 is not already preinstalled

If a download in the setup script is blocked, raise the level to **Full** —
that is the only reason this environment would need it.

Git operations (clone, fetch, push) go through Anthropic's GitHub proxy and work
**independently of this network level**.

### Environment variables

Paste the contents of [`environment-variables.env`](environment-variables.env).
These are non-secret .NET CLI toggles only — **never put credentials here**, as
the field is visible to anyone using the environment. `PATH` and `DOTNET_ROOT`
are wired by the setup script (which can expand `$HOME`), not here.

### Setup script

Point the field at the committed script so it stays version-controlled:

```bash
bash eng/cloud-env/setup.sh
```

(You can also paste the full contents of [`setup.sh`](setup.sh) directly, but the
one-liner above means edits to the script ship through normal PRs.)

## How the environment behaves

- **Setup runs once, then is cached.** The setup script runs the first time a
  session starts; Anthropic then snapshots the filesystem and reuses it for later
  sessions (the script is skipped). It re-runs only when you change the setup
  script or network hosts, or after the cache's ~7-day expiry.
- **Keep setup under ~5 minutes** or the snapshot won't build and setup re-runs
  every session. `setup.sh` installs the four toolchains in parallel and skips the
  warm build for this reason.
- **Files persist via the snapshot; processes do not.** Anything `setup.sh` writes
  to disk (`~/.dotnet`, `~/.dapr`, restored packages) is captured. The home
  directory and other changes made *after* Claude Code launches are discarded at
  session end.

## Options

- **`CLOUD_ENV_WARM_BUILD`** — off by default. Set it (e.g. `CLOUD_ENV_WARM_BUILD=1`
  in the **Environment variables** field) to also run `dotnet build` (Release) in
  setup, so the first `/test` (`--no-build`) runs immediately. Expect this to push
  setup past the ~5-min cache window on a full solution; leave it unset unless you
  have confirmed your setup still caches.

## GitHub write access

`setup.sh` sets up the build/test/lint toolchain only — no GitHub credentials.
Commit, push, and open PRs from your local checkout. Cloud environments have no
dedicated secrets store (environment variables are visible to anyone who can edit
the environment), so this setup intentionally keeps no write credentials in the
sandbox.

## Verifying locally

The script is idempotent and location-independent, so you can dry-run it on any
Linux box (or in a container) to confirm it provisions cleanly:

```bash
bash eng/cloud-env/setup.sh
```

## Maintenance

Keep the pinned versions in sync with their sources of truth — they are
duplicated here only because the cloud environment provisions before the repo's
own tooling is available:

- .NET SDK — [`global.json`](../../global.json)
- Node / npm — [`package.json`](../../package.json) (`engines`)
- Dapr CLI — the `Install Dapr CLI` step in [`ci.yml`](../../.github/workflows/ci.yml)
- dotnet tools — [`.config/dotnet-tools.json`](../../.config/dotnet-tools.json)

If any of those change, update [`setup.sh`](setup.sh) and the table above in the
same PR.
