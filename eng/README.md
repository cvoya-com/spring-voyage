# eng/

Top-level home for Spring Voyage's build, deployment, and install artefacts. Three
audience-scoped buckets:

- [`build/`](build/README.md) — **Source-clone contributors.** Builds the platform
  image and agent images locally from this repo (`build.sh`, `build-agent-images.sh`,
  the various `Dockerfile*`s).
- [`config/`](config/) — **Shared deployment environment template.** Holds
  `spring.env.example` (and the per-deployment `spring.env` it spawns, which
  is gitignored). Read by `build/`, `deploy/`, and `install/` scripts so no
  one script reaches into another's directory for runtime config.
- [`deploy/`](deploy/README.md) — **Operators (both source-clone and installer paths).**
  Runtime scripts and config: `deploy.sh`, `setup.sh`, `spring-voyage-host.sh`,
  `Caddyfile`, `docker-compose.yml`.
- [`install/`](install/README.md) — **Source-free operators.** Curlable
  `install.sh`/`uninstall.sh` that consume the release assets published by
  `.github/workflows/release.yml`. Design captured in
  [ADR-0042](../docs/decisions/0042-local-operator-installer.md). For most
  operators, this is the starting point — `curl … install.sh | bash`.

The TypeScript agent sidecar (formerly `deployment/agent-sidecar/`) now lives at
`src/Cvoya.Spring.AgentSidecar/`, alongside the other `Cvoya.Spring.<Component>`
source projects.
