# Architecture Overview

This document series describes the technical architecture of Spring Voyage V2 -- how the concepts described in the [Concepts](../concepts/overview.md) section are realized as a running system.

## Document Map

| Document | Description |
|----------|-------------|
| [Infrastructure: Dapr](infrastructure.md) | The Dapr runtime, sidecar pattern, and building blocks |
| [Actor Model](actors.md) | How agents, units, connectors, and humans map to Dapr virtual actors |
| [Workflows and Orchestration](workflows.md) | Workflow-as-container model, platform-internal workflows, A2A integration |
| [Execution Environments](execution.md) | Container isolation, streaming, and the brain/hands separation |
| [Data Persistence](data.md) | PostgreSQL, Dapr state stores, secrets, and configuration |
| [API and Hosting](api-hosting.md) | API host, worker host, deployment topologies, and the CLI |
| [Security and Resilience](security.md) | Authentication, mTLS, multi-tenancy isolation, and failure recovery |

## Architectural Principles

**Dapr as the infrastructure layer.** The platform talks to Dapr sidecars via gRPC/HTTP; Dapr handles state, messaging, secrets, and service invocation with pluggable backends. This makes infrastructure concerns (which message broker? which state store?) a configuration choice, not a code change.

**Language-agnostic agents, .NET infrastructure.** The infrastructure layer (actors, routing, workflows, API surface) is .NET/C#. Agent brain logic can be any language that speaks HTTP/gRPC to the Dapr sidecar -- .NET, Python, or anything else.

**Actors as the concurrency model.** Every agent, unit, connector, and human is a Dapr virtual actor. Actors provide turn-based concurrency (no locks), automatic activation/deactivation, durable reminders, and built-in state management.

**Containers as the deployment model.** Domain workflows and agent execution environments run in containers. Updating a workflow means deploying a new container image, not recompiling the platform. This decouples domain evolution from platform releases.

**Platform never inspects domain payloads.** The platform routes messages by type and delivery mechanism. It never looks inside a Domain message's payload. Domain semantics live in packages and skills.

## High-Level Component Map

The system consists of these major components:

- **API Host** -- the .NET web application that serves REST, WebSocket, and SSE endpoints. Handles authentication, authorization, and multi-tenant routing. In local development mode, runs as a single-tenant daemon.
- **Worker Host** -- a headless .NET process that hosts actor runtimes for background processing. Scaled independently from the API host.
- **Dapr Sidecar** -- runs alongside every host process. Provides state management, pub/sub, service invocation, secrets, and configuration.
- **Execution Environment Containers** -- isolated containers where delegated agents do their work (e.g., running Claude Code).
- **Workflow Containers** -- isolated containers running domain orchestration logic (e.g., the software development cycle).
- **PostgreSQL** -- primary relational store for tenant data, agent definitions, activity history, and (via Dapr) agent runtime state.
- **CLI (`spring`)** -- the command-line tool for interacting with the platform.
- **Web Portal** -- the browser-based dashboard for observation, interaction, and administration.

## How They Connect

All inter-component communication goes through Dapr:

- Host processes talk to their Dapr sidecars via localhost gRPC/HTTP
- Dapr sidecars handle service-to-service communication with mTLS
- Execution environment and workflow containers each have their own Dapr sidecars
- The CLI and Web Portal talk to the API Host via REST/WebSocket
