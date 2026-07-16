# Spring Voyage - A CVOYA project

[![CI](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml/badge.svg)](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/cvoya-com/spring-voyage)](https://github.com/cvoya-com/spring-voyage/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/Cvoya.Spring.Cli?label=CLI%20%28NuGet%29)](https://www.nuget.org/packages/Cvoya.Spring.Cli)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1-blue.svg)](LICENSE.md)

![Spring Voyage](docs/spring-voyage-sailboat-light.png)

> **Spring Voyage is source-available under the Business Source License 1.1.** You may read, modify, and run it in production — including commercially — but may not offer it to third parties as a managed AI-agent-collaboration service. That restriction ends on 10 April 2030, when the license converts to Apache 2.0.

## About

<p>
  <a href="https://cvoya.com">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/cvoya-com/.github/main/profile/cvoya-wordmark-dark.png">
      <img src="https://raw.githubusercontent.com/cvoya-com/.github/main/profile/cvoya-wordmark.png" alt="CVOYA" width="260">
    </picture>
  </a>
</p>

Spring Voyage is downloadable, source-available computer software developed by [CVOYA](https://cvoya.com) and led by [Savas Parastatidis](https://savas.me). For news, examples, and the wider project, visit [spring.voyage](https://spring.voyage) or the [CVOYA software catalog](https://cvoya.com/software).

## Introduction

Spring Voyage is a source-available human–AI agent collaboration platform. Humans and agents work together toward a goal in a domain, as members of the same team, each with their own roles, responsibilities, and expertise. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via **messages**.

The platform does not orchestrate. It prescribes no workflows and no fixed way of working — how a team communicates, collaborates, and organizes is left to the instructions its agents are given. What the platform provides instead are the building blocks teams need:

- **Communication** primitives for interactions and conversations.
- **Memory** tools that keep a per-engagement history of interactions (between two or more participants).
- **Directory** tools for discovering members, roles, and expertise.
- **Isolated execution environment** for agent runtimes — which can use domain-specific tools and, if necessary, host their own orchestration frameworks: **[LangGraph](https://www.langchain.com/langgraph)**, **[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)**, **[CrewAI](https://www.crewai.com/)**, **[Google ADK](https://google.github.io/adk-docs/)**, **[OpenAI Agents SDK](https://github.com/openai/openai-agents-python)**, **[Ruflo](https://github.com/ruvnet/ruflo)**, **[Gas Town](https://github.com/gastownhall/gastown)**, and more.
- **Policy** management for team behavior — interaction boundary enforcement, work projection/summarization via a team leader, and more (still in progress).
- **Connectors** for integration with external systems (currently GitHub and Slack).
- **CLI and web portal** for managing the platform, configuring agent teams, monitoring budgets, and gaining insight into team operations and engagements.

## Install

Spring Voyage runs on Linux or macOS with [Podman](https://podman.io/) 4+.

**[Download the latest Spring Voyage installer](https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh)**, or install from a terminal:

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

Two prompts: `DEPLOY_HOSTNAME` (default `localhost`) and an optional GitHub App registration — press Enter to accept both defaults. When it finishes, open **http://localhost** and the portal guides you through the rest.

For flags, TLS, troubleshooting, and updates see the [operator deployment guide](docs/guide/operator/deployment.md).

## Getting Started

After installing, open the portal at **http://localhost** — the new-unit wizard walks you through everything, including LLM credentials, GitHub or Slack integration, and other configuration. No CLI required to get going.

When you want to go deeper:

- **[Your first unit and agent](docs/guide/intro/getting-started.md)** — the same flow from the `spring` CLI: create a unit, send it a message, add an agent member.
- **[Spring Voyage OSS — a ready-made dev team](docs/guide/intro/getting-started-spring-voyage-oss.md)** — install the built-in `spring-voyage-oss` package: a unit with engineer and program-manager agents that pick up work from a GitHub repository.

## Operating Spring Voyage

Day-2 operations — status, logs, restart, updates, and uninstall — run through the `voyage` wrapper that the installer puts on your `PATH` (start with `voyage status`). The [operator deployment guide](docs/guide/operator/deployment.md) covers the full command set plus TLS, multi-host topology, secrets rotation, updates, and troubleshooting; the three-tier secret model (platform / tenant / unit) is documented in [Secrets](docs/guide/operator/secrets.md).

## Documentation

- [Getting Started](docs/guide/intro/getting-started.md) — first unit, first message
- [Spring Voyage OSS quickstart](docs/guide/intro/getting-started-spring-voyage-oss.md) — install the built-in dev-team package
- [Operator guide](docs/guide/operator/deployment.md) — install, configure, day-2 ops, secrets, troubleshooting
- [User guide](docs/guide/README.md) — `spring` CLI and web portal
- [Concepts](docs/concepts/overview.md) — units, agents, connectors, messages, skills
- [Architecture](docs/architecture/README.md) — how the concepts are realised as a running system
- [Releases & container images](docs/developer/releases.md) — versioning plus the canonical list of published GHCR images and their tags
- [Bring your own agent image](docs/guide/operator/byoi-agent-images.md) — ship a custom agent CLI on the published base image ([ADR-0027](docs/decisions/0027-agent-image-conformance-contract.md))
- [Documentation index](docs/README.md) — everything

## Project Status

Spring Voyage is in alpha and in active development. Pull Requests are welcomed and encouraged. A lot of functionality is already in place:

- **Infrastructure** — container-based hosting, an actor concurrency model, reliable message delivery, and more.
- **Functionality** — create teams of agents, compose the system prompt given to them, MCP tools for message-based communication, directory services, engagement-scoped memory access, and more.
- **CLI and web portal** — user interfaces to manage teams of agents and engage with them.
- **Connectors** — GitHub and Slack (personal workspace) integration.

Forward-looking work and live progress — what's in flight, what's queued — live on the GitHub [milestones](https://github.com/cvoya-com/spring-voyage/milestones) and in issues across [cvoya-com/spring-voyage](https://github.com/cvoya-com/spring-voyage/issues).

## Open Core Model

Spring Voyage follows an open-core model. This repository is the complete, fully functional platform — agents, units, messaging and routing, connectors, the package system (including the built-in `spring-voyage-oss` package), CLI, web portal, A2A, ephemeral cloning, observability, and basic cost tracking — with the multi-tenancy infrastructure included and configured as a single-operator identity model.

Commercial extensions (multi-user sign-in, OAuth/SSO/SAML, cloud-style tenant provisioning, billing, and advanced features) are developed separately and are not part of this repository.

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

Host prerequisites (.NET 10 SDK, Podman, Dapr CLI; optional Node.js / Python — everything else runs in containers) and the local-dev loop are in [docs/developer/setup.md](docs/developer/setup.md). Architecture, project layout, and where to put new code are in [docs/developer/overview.md](docs/developer/overview.md). Operators do not need to clone the repository; the installer is the supported install path.

## License

Spring Voyage is licensed under the [Business Source License 1.1](LICENSE.md).

**What this means:**

- **Free to use** for personal projects, development, testing, and internal non-production use
- **Free for production** except for offering it as a competing managed AI agent collaboration service
- **Converts to Apache 2.0** on 2030-04-10 (four years from initial release)

See [LICENSE](LICENSE.md) for the full terms and [NOTICE](NOTICE.md) for third-party attributions.
