# Spring Voyage - A CVOYA project

[![CI](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml/badge.svg)](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/cvoya-com/spring-voyage)](https://github.com/cvoya-com/spring-voyage/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/Cvoya.Spring.Cli?label=CLI%20%28NuGet%29)](https://www.nuget.org/packages/Cvoya.Spring.Cli)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1-blue.svg)](LICENSE.md)

<p align="center">
  <img src="docs/spring-voyage-sailboat-light.png" alt="Spring Voyage" width="200">
</p>

An open-source collaboration platform for teams of AI agents — and the humans they work with. Built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**. Orchestration is one mechanism inside a unit, not the whole of the platform.

Project home: [spring.voyage](https://spring.voyage).

## About

Spring Voyage is developed by [Cvoya](https://cvoya.com) and led by [Savas Parastatidis](https://savas.me). For news, examples, and the wider project, visit [spring.voyage](https://spring.voyage).

## Vision

The Spring Voyage platform does not prescribe a particular way of working. It does not have predefined workflows or orchestration logic for teams of agents. Instead, it offers the necessary primitives for teams of agents and humans to be defined by users. How they communicate, how they collaborate, how they organize to solve problems is left to the instructions that are given to the agents. The platform contributes the building blocks for teams of humans and AI agents to collaborate on any problem on any domain.

## Key Concepts

| Concept       | Description                                                                                                        |
| ------------- | ------------------------------------------------------------------------------------------------------------------ |
| **Agent**     | A single AI entity (Dapr virtual actor) with a mailbox and execution environment                                   |
| **Unit**      | An agent that has children (other agents or units); orchestration is runtime behaviour, not platform configuration |
| **Connector** | Bridges an external system (GitHub, Slack, etc.) into a unit                                                       |
| **Message**   | Typed communication between addressable entities                                                                   |
| **Skill**     | A prompt fragment + optional tool definitions that an agent can use                                                |

For the full mental model, see the [Concepts overview](docs/concepts/overview.md).

## Install

Spring Voyage runs on Linux or macOS with [Podman](https://podman.io/) 4+. One command:

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

The installer downloads a per-platform release archive, verifies it against `SHA256SUMS`, pulls the multi-arch platform image from GHCR, and brings the stack up. Two prompts only — `DEPLOY_HOSTNAME` (default `localhost`) and an optional GitHub-App registration. Pass `--yes` to skip both.

When it finishes, open the portal at `http://DEPLOY_HOSTNAME` (e.g. `http://localhost`) and continue with one of the getting-started guides below.

See the [operator deployment guide](docs/guide/operator/deployment.md) for the full walkthrough, flags, TLS, and design notes ([ADR-0042](docs/decisions/0042-local-operator-installer.md)).

### Alternative: install the CLI standalone

If you already have the .NET 10 runtime and just want the `spring` CLI to talk to a deployed Spring Voyage (your own, or someone else's), install it from NuGet:

```bash
dotnet tool install -g Cvoya.Spring.Cli
export SPRING_API_URL=https://your-spring-voyage-host
spring --help
```

The platform installer above already bundles a self-contained `spring` binary; this alternative is for users who manage their CLI alongside other .NET tools or who don't run the platform locally.

## Getting Started

Two paths, depending on what you want to build:

- **[Your first unit and agent](docs/guide/intro/getting-started.md)** — the smallest possible path: drop in one LLM credential, create a unit, send it a message. About five minutes after the installer finishes.
- **[Spring Voyage OSS — a ready-made dev team](docs/guide/intro/getting-started-spring-voyage-oss.md)** — install the built-in `spring-voyage-oss` package: one unit with engineer and program-manager agents that pick up work from a GitHub repository. Adds a GitHub App, the `gh webhook forward` forwarder for local dev, and the `spring-voyage-team` label that scopes which issues the team acts on.

## Day-2 Operations

```bash
voyage status                              # version, container health, dispatcher health, web URL
voyage logs [service]                      # tail container logs (or 'dispatcher' for the host process)
voyage restart                             # restart the stack
voyage version                             # print installed version + platform image tag
voyage gh-webhook-forward --repo <repo>    # forward GitHub webhooks to this install (for local dev)
voyage uninstall                           # tear down (preserves spring.env + workspaces)
voyage uninstall --purge                   # factory reset
```

For TLS, multi-host topology, secrets rotation, updates, and troubleshooting, see the [operator deployment guide](docs/guide/operator/deployment.md). The full three-tier secret model (platform / tenant / unit) is documented in [Secrets](docs/guide/operator/secrets.md).

## Container Images

Every release publishes the platform and agent images to GitHub Container Registry. All images are public and multi-arch (`linux/amd64`, `linux/arm64`).

| Image                                              | Purpose                                                                                                                 |
| -------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `ghcr.io/cvoya-com/spring-voyage`                  | Platform image — API + Worker + Web + Dapr CLI. One image serves all three containers; the command selects the process. |
| `ghcr.io/cvoya-com/spring-voyage-agent-base`       | BYOI base image — bundles the A2A sidecar bridge on `:8999`. Extend with a custom CLI.                                  |
| `ghcr.io/cvoya-com/spring-voyage-claude-code-base` | Reference image for the `claude-code` tool.                                                                             |
| `ghcr.io/cvoya-com/spring-voyage-gemini-base`      | Reference image for the `gemini` tool.                                                                                  |
| `ghcr.io/cvoya-com/spring-voyage-agent`            | Path-3 native A2A agent (Python).                                                                                       |
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-*`      | OSS role agents (software-engineering, program-management) used by the `spring-voyage-oss` package.                     |

Operators do not build images locally — the installer pulls them. Image tags follow [SemVer](docs/developer/releases.md#container-image-tagging-and-publishing): `:X.Y.Z` is immutable, `:X.Y` floats to the latest patch of that minor line, `:latest` floats to the latest stable.

### Bring your own agent image

Anyone can ship a custom agent CLI by extending `spring-voyage-agent-base`:

```dockerfile
FROM ghcr.io/cvoya-com/spring-voyage-agent-base:latest
USER root
RUN npm install -g my-private-agent-cli@1.2.3
USER agent
```

The bundled bridge ENTRYPOINT runs the A2A sidecar on `:8999` automatically; your CLI is dispatched as a child process via `SPRING_AGENT_ARGV`. Three conformance paths are documented in the [BYOI agent images guide](docs/guide/operator/byoi-agent-images.md) and [ADR-0027](docs/decisions/0027-agent-image-conformance-contract.md).

## Documentation

- [Getting Started](docs/guide/intro/getting-started.md) — first unit, first message
- [Spring Voyage OSS quickstart](docs/guide/intro/getting-started-spring-voyage-oss.md) — install the built-in dev-team package
- [Operator guide](docs/guide/operator/deployment.md) — install, configure, day-2 ops, secrets, troubleshooting
- [User guide](docs/guide/README.md) — `spring` CLI and web portal
- [Concepts](docs/concepts/overview.md) — units, agents, connectors, messages, skills
- [Architecture](docs/architecture/README.md) — how the concepts are realised as a running system
- [Documentation index](docs/README.md) — everything

## Project Status

Spring Voyage is preparing for its first stable release. The platform is feature-complete for the v0.1 scope — agents, units, messaging, connectors, packages, dashboard — and is in active use developing itself ([dogfooding via the Spring Voyage OSS package](docs/guide/intro/getting-started-spring-voyage-oss.md)).

Forward-looking narrative is published in [docs/roadmap/](docs/roadmap/README.md). Live progress — what's in flight, what's queued — lives on the GitHub [milestones](https://github.com/cvoya-com/spring-voyage/milestones) and in issues across [cvoya-com/spring-voyage](https://github.com/cvoya-com/spring-voyage/issues).

## Open Core Model

Spring Voyage follows an open-core model. This repository contains the complete, fully functional platform: agents, units (which are agents that have children), messaging, routing, runtime-decided work delivery over the `sv.messaging.*` tool surface, execution, connectors, CLI, API-key authentication, ephemeral cloning, observability, basic cost tracking, A2A, unit nesting, the package system (including the built-in `spring-voyage-oss` package), and dashboard. The multi-tenancy infrastructure is also included, configured as a single-operator identity model.

Commercial extensions (multi-user OSS sign-in, OAuth/SSO/SAML, cloud-style tenant provisioning, billing, and advanced features) are developed separately and are not part of this repository.

## Security

Found a vulnerability? Please follow the responsible-disclosure process in [SECURITY.md](SECURITY.md) — do **not** open a public GitHub issue for security reports.

## Contributing

Contributions welcome. Please read:

- [CONTRIBUTING.md](CONTRIBUTING.md) — development workflow and CLA
- [docs/developer/setup.md](docs/developer/setup.md) — prerequisites, building, running locally
- [CONVENTIONS.md](CONVENTIONS.md) — coding patterns (mandatory)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — community standards

To build the platform from source or hack on it locally:

```bash
git clone https://github.com/cvoya-com/spring-voyage
cd spring-voyage
```

Prerequisites (.NET 10 SDK, Dapr CLI, Podman, PostgreSQL, Redis; optional Node.js / Python) and the local-dev loop are in [docs/developer/setup.md](docs/developer/setup.md). Architecture, project layout, and where to put new code are in [docs/developer/overview.md](docs/developer/overview.md). Operators do not need to clone the repository; the installer is the supported install path.

## License

Spring Voyage is licensed under the [Business Source License 1.1](LICENSE.md).

**What this means:**

- **Free to use** for personal projects, development, testing, and internal non-production use
- **Free for production** except for offering it as a competing managed AI agent collaboration service
- **Converts to Apache 2.0** on 2030-04-10 (four years from initial release)

See [LICENSE](LICENSE.md) for the full terms and [NOTICE](NOTICE.md) for third-party attributions.
