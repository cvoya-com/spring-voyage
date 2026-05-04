# Spring Voyage -- User Guide

Spring Voyage is an open-source collaboration platform for teams of AI agents -- and the humans they work with. You stand up small fleets of AI collaborators that operate on real work, on the real systems where that work happens, with people in the loop where it counts. Units organise the agents; orchestration is one mechanism inside a unit, not the whole of the platform -- see the [concepts overview](../../concepts/overview.md) for the full mental model.

This guide covers how to use Spring Voyage through the `spring` CLI. It walks through authentication, creating and managing units and agents, sending messages, observing activity, and day-to-day operations.

## Document Map

| Document | Description |
|----------|-------------|
| [Getting Started](getting-started.md) | Authentication, first unit, first agent |
| [Managing Units and Agents](../user/units-and-agents.md) | Creating, configuring, and operating units and agents |
| [Messaging and Interaction](../user/messaging.md) | Sending messages, reading conversations, interacting with agents |
| [Observing Activity](../user/observing.md) | Activity streams, cost tracking, dashboards |
| [Web Portal Walkthrough](../user/portal.md) | Pages, tabs, and CLI equivalents for the browser UI |
| [Declarative Configuration](../user/declarative.md) | YAML definitions, `spring apply`, and version-controlled setup |
| [Deployment](../operator/deployment.md) | Operator guide: Docker Compose / Podman, Dapr components, TLS, secrets |
| [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) | How to ship a custom agent container image — the three conformance paths, env contract, and debugging tips |
| [Runnable Examples](../user/examples.md) | Catalog of e2e scenario scripts that double as usage examples |

## Prerequisites

- The `spring` CLI installed (via `dotnet tool install -g spring-cli` or standalone executable)
- For local development: Podman (or Docker) and Dapr CLI installed
- For remote platform: network access to the Spring Voyage API host

## Quick Start

```
# Authenticate (skip for local dev mode)
spring auth login

# Create a unit and an agent inside it
spring unit create my-team
spring agent create my-agent \
    --role engineer \
    --unit my-team \
    --tool claude-code \
    --image ghcr.io/cvoya-com/spring-agent:latest

# Look up the agent's id (display-name search), then send a message
spring agent show my-agent --unit my-team           # prints the canonical Guid
spring message send agent:<id> "Hello, what can you do?"

# Watch the activity feed (no live-stream verb yet — re-run as needed)
spring activity list --source unit:my-team --limit 20
```

> `spring agent create` requires at least one `--unit`. Pick the agent's runtime via `--tool` (`claude-code`, `codex`, `gemini`, `dapr-agent`, `custom`) and supply richer execution config through `--definition-file` when needed; see [Managing Units and Agents](../user/units-and-agents.md) for the full set.

## See it in action

Every workflow in this guide is exercised by a runnable end-to-end scenario under [`tests/cli-scenarios/scenarios/`](../../../tests/cli-scenarios/scenarios). Each scenario is a self-contained bash script that drives the real `spring` CLI against a running stack, so you can read them as concrete usage examples — or execute them yourself to sanity-check an environment.

- [`tests/cli-scenarios/README.md`](../../../tests/cli-scenarios/README.md) — prerequisites, runner usage, and conventions for the scenarios directory.
- See [Runnable Examples](../user/examples.md) for a curated catalog of individual scenarios grouped by what they demonstrate.
