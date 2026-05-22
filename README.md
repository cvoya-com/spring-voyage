# Spring Voyage

[![CI](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml/badge.svg)](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1-blue.svg)](LICENSE.md)

An open-source collaboration platform for teams of AI agents — and the humans they work with. Built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**. Orchestration is one mechanism inside a unit, not the whole of the platform.

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

The installer downloads the per-RID host archive (a single tarball bundling the deployment scripts, dispatcher, and `spring` CLI) for your platform; verifies it against `SHA256SUMS`; pulls the multi-arch platform image from GHCR; and brings the stack up. Two prompts only — `DEPLOY_HOSTNAME` (default `localhost`) and an optional GitHub-App registration flow. Pass `--yes` to skip both.

See the [operator deployment guide](docs/guide/operator/deployment.md) for the walkthrough, flags, and design notes ([ADR-0042](docs/decisions/0042-local-operator-installer.md)).

## First Steps

After the installer finishes, set your LLM provider credentials and create your first unit:

```bash
# LLM credentials live at tenant scope — units inherit them automatically.

# If you are planning to use the Spring Voyage Agent Runtime with Anthropic's API
spring secret create --scope tenant anthropic-api-key --value "sk-ant-api03..."

# If you are planning to use the "Claude Code" CLI
spring secret create --scope tenant anthropic-token --value "sk-ant-oat..."

# If you are planning to use OpenAPI's API or codex
spring secret create --scope tenant openai-api-key --value "sk-..."

# First unit, using the Claude Code agent.
spring unit create first-team --tool claude-code
```

The [Getting Started guide](docs/guide/intro/getting-started.md) walks through creating a unit, adding agents, wiring connectors, and sending the first message.

The web portal is at the configured hostname (`http://localhost` by default).

### Alternative: install the CLI standalone

If you already have the .NET 10 runtime and just want the `spring` CLI to talk to a deployed Spring Voyage (your own, or someone else's), install it from NuGet:

```bash
dotnet tool install -g Cvoya.Spring.Cli
export SPRING_API_URL=https://your-spring-voyage-host
spring --help
```

The platform installer above already includes a self-contained `spring` binary for your platform; this alternative is for users who manage their CLI alongside other .NET tools or who don't run the platform locally.

## Day-2 Operations

```bash
voyage status               # version, container health, dispatcher health, web URL
voyage logs [service]       # tail container logs (or 'dispatcher' for the host process)
voyage restart              # restart the stack
voyage version              # print installed version + platform image tag
voyage uninstall            # tear down (preserves spring.env + workspaces)
voyage uninstall --purge    # factory reset
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
| `ghcr.io/cvoya-com/spring-voyage-agent-oss-*`      | OSS role agents (software-engineering, design, product-management, program-management).                                 |

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

- [Getting Started](docs/guide/intro/getting-started.md) — first unit, first message, portal walkthrough
- [Operator guide](docs/guide/operator/deployment.md) — install, configure, day-2 ops, secrets, troubleshooting
- [User guide](docs/guide/README.md) — `spring` CLI and web portal
- [Concepts](docs/concepts/overview.md) — units, agents, connectors, messages, skills
- [Architecture](docs/architecture/README.md) — how the concepts are realised as a running system
- [Documentation index](docs/README.md) — everything

## For Contributors

This repository contains the platform source. To build, run locally, or contribute changes:

```bash
git clone https://github.com/cvoya-com/spring-voyage
cd spring-voyage
```

Prerequisites (.NET 10 SDK, Dapr CLI, Podman, PostgreSQL, Redis; optional Node.js / Python) and the local-dev loop are documented in [docs/developer/setup.md](docs/developer/setup.md). The platform architecture, project layout, and where to put new code are in [docs/developer/overview.md](docs/developer/overview.md). Read [CONVENTIONS.md](CONVENTIONS.md) before writing code — it is mandatory.

Operators do not need to clone the repository; the installer is the supported install path.

## Open Core Model

Spring Voyage follows an open-core model. This repository contains the complete, fully functional platform: agents, units (which are agents that have children), messaging, routing, runtime-decided work delivery over the `sv.messaging.*` tool surface, execution, connectors, CLI, basic auth (API key), ephemeral cloning, observability, basic cost tracking, A2A, unit nesting, package system, and dashboard.

Commercial extensions (multi-tenancy, OAuth/SSO/SAML, billing, advanced features) are developed separately and are not part of this repository.

## Contributing

Contributions welcome. Please read:

- [CONTRIBUTING.md](CONTRIBUTING.md) — development workflow and CLA
- [docs/developer/setup.md](docs/developer/setup.md) — prerequisites, building, running locally
- [CONVENTIONS.md](CONVENTIONS.md) — coding patterns (mandatory)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — community standards
- [SECURITY.md](SECURITY.md) — reporting security issues

## License

Spring Voyage is licensed under the [Business Source License 1.1](LICENSE.md).

**What this means:**

- **Free to use** for personal projects, development, testing, and internal non-production use
- **Free for production** except for offering it as a competing managed AI agent collaboration service
- **Converts to Apache 2.0** on 2030-04-10 (four years from initial release)

See [LICENSE](LICENSE.md) for the full terms and [NOTICE](NOTICE.md) for third-party attributions.
