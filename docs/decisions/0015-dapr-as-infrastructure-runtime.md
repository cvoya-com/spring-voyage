# 0015 — Dapr as the infrastructure runtime

- **Status:** Accepted — Dapr is the runtime substrate for actors, pub/sub, state, secrets, bindings, and durable workflows across every host process in the platform.
- **Date:** 2026-04-21
- **Related code:** `dapr/` (component + config profiles), `src/Cvoya.Spring.Host.Worker/`, `src/Cvoya.Spring.Host.Api/`, `src/Cvoya.Spring.Dapr/Actors/`, `src/Cvoya.Spring.Dapr/Workflows/`.
- **Related docs:** [`docs/architecture/infrastructure.md`](../architecture/infrastructure.md).

## Context

V2 needs a distributed-systems substrate that gives us virtual actors, pub/sub, state, secrets, durable workflows, and a clean local-vs-production boundary — without binding the platform to one cloud or message broker. The infrastructure layer is .NET (see [ADR 0016](0016-net-for-infrastructure-layer.md)); whatever we pick has to interop with .NET first-class, plus stay open to Python (and other) agent runtimes that the platform dispatches into containers.

Three families of options were on the table:

1. Build directly on Kubernetes primitives (Operators, CRDs, raw pub/sub).
2. Pick concrete runtimes per concern (Kafka or RabbitMQ for messaging, Orleans for actors, vendor secrets, …).
3. A sidecar runtime that abstracts every building block behind a uniform API.

## Decision

**Dapr is the infrastructure runtime. Every Spring Voyage host process runs alongside a Dapr sidecar; application code talks to building-block APIs only — never directly to Postgres, Redis, Kafka, or vault.**

The choice is load-bearing for several other v2 decisions:

- **Pluggable backends via YAML.** State store, pub/sub, secrets, and bindings are swappable per environment. Local dev (Postgres + Redis), production (Postgres + Redis or vendor equivalents), and the private cloud overlay (Cosmos DB + Service Bus + Key Vault) all run the same application code.
- **Virtual actors.** Dapr's actor model gives turn-based concurrency, automatic placement, durable reminders, and built-in state — exactly the per-agent / per-unit isolation V2 needs. We did not want to build that ourselves.
- **Sidecar pattern.** mTLS, retries, observability, and circuit breakers live in the sidecar. Application code never holds infra credentials directly (the dispatcher service is the only narrow exception, see [ADR 0012](0012-spring-dispatcher-service-extraction.md)).
- **Language-agnostic.** Anything that speaks HTTP/gRPC to `localhost:3500` participates as a first-class citizen, including Python agent containers and the dispatcher.
- **Durable workflows.** Dapr Workflows provide task chaining, fan-out, and human-in-the-loop patterns with automatic recovery — used by every platform-internal lifecycle workflow (agent creation, cloning, validation, …).

## Alternatives considered

- **Direct Kubernetes primitives.** Maximum control, but every concern (actors, pub/sub, state) becomes a custom operator we have to maintain. Operationally heavier; loses the local-dev story (devs would need a real cluster).
- **Concrete runtime per concern.** Pick Kafka, Orleans, vendor secrets, etc., and wire them directly. Forces the platform to ship one set of choices; private cloud overlays would have to fork or pre-empt every binding.
- **Build a thin in-process abstraction over the same backends.** Re-creates 80 % of Dapr without the language-agnostic sidecar story.

## Consequences

- **Dapr availability is on the critical path.** Every host needs a healthy sidecar to start; outages are platform-wide, not per-feature. We accept this in exchange for the rest of the deal.
- **Dapr SDK quality matters.** We pin .NET SDK versions and track upstream churn; see ADR 0016 for the .NET-specific reasoning.
- **One runtime to learn for new contributors.** Operators don't pick "which messaging?" — they pick which Dapr component YAML matches their environment.
