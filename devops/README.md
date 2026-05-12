# devops/

Top-level home for Spring Voyage's build, deployment, and install artefacts. Three
audience-scoped buckets:

- [`build/`](build/README.md) — **Source-clone contributors.** Builds the platform
  image and agent images locally from this repo (`build.sh`, `build-agent-images.sh`,
  the various `Dockerfile*`s).
- [`deploy/`](deploy/README.md) — **Operators (both source-clone and installer paths).**
  Runtime scripts and config: `deploy.sh`, `setup.sh`, `spring-voyage-host.sh`,
  `Caddyfile`, `docker-compose.yml`, `spring.env.example`.
- [`install/`](install/README.md) — **Source-free operators.** Curlable installer
  that consumes published release assets. Stub today; populated by #2180.

The TypeScript agent sidecar (formerly `deployment/agent-sidecar/`) now lives at
`src/Cvoya.Spring.AgentSidecar/`, alongside the other `Cvoya.Spring.<Component>`
source projects.
