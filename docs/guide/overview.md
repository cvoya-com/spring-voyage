# Spring Voyage V2 -- User Guide

This guide covers how to use Spring Voyage V2 through the `spring` CLI. It walks through authentication, creating and managing units and agents, sending messages, observing activity, and day-to-day operations.

## Document Map

| Document | Description |
|----------|-------------|
| [Getting Started](getting-started.md) | Authentication, first unit, first agent |
| [Managing Units and Agents](units-and-agents.md) | Creating, configuring, and operating units and agents |
| [Messaging and Interaction](messaging.md) | Sending messages, reading conversations, interacting with agents |
| [Observing Activity](observing.md) | Activity streams, cost tracking, dashboards |
| [Declarative Configuration](declarative.md) | YAML definitions, `spring apply`, and version-controlled setup |

## Prerequisites

- The `spring` CLI installed (via `dotnet tool install -g spring-cli` or standalone executable)
- For local development: Podman (or Docker) and Dapr CLI installed
- For remote platform: network access to the Spring Voyage API host

## Quick Start

```
# Authenticate (skip for local dev mode)
spring auth

# Create a unit with an agent
spring unit create my-team
spring agent create my-agent --role engineer --ai-backend claude --execution delegated --tool claude-code
spring unit members add my-team my-agent

# Send a message
spring message send agent://my-team/my-agent "Hello, what can you do?"

# Watch the activity stream
spring activity stream --unit my-team
```
