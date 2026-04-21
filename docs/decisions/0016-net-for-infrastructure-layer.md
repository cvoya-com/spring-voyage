# 0016 — .NET for the platform infrastructure layer

- **Status:** Accepted — the platform infrastructure (actors, routing, REST API, workflows, CLI) is .NET 10 / C#. Agent runtimes stay language-agnostic via the Dapr sidecar.
- **Date:** 2026-04-21
- **Related code:** every project under `src/Cvoya.Spring.*/` except `src/Cvoya.Spring.Web/` (Next.js) and the Python-based agent runtimes in containers.
- **Related docs:** [`docs/architecture/infrastructure.md`](../architecture/infrastructure.md), [ADR 0015](0015-dapr-as-infrastructure-runtime.md).

## Context

V1 of the platform was Python end-to-end. Despite a thousands-strong test suite, an outsized share of bugs surfaced only at runtime — message envelope mismatches, actor-state shape drift, API-contract changes that compiled fine on both ends and only blew up under load. The infrastructure layer (message routing, actor state, API contracts, workflow shapes) is exactly where compile-time type checking pays off the most: the data shapes are stable enough to typify, but the failure cost when a shape changes silently is high.

V2's choice of Dapr ([ADR 0015](0015-dapr-as-infrastructure-runtime.md)) made the language question narrower than "what should we write the platform in?" — we needed the language whose Dapr SDK was strongest and whose ecosystem fit ASP.NET-grade web hosting + EF Core-grade relational data access without re-inventing either.

Three serious options:

1. **Carry forward Python.** Lowest migration cost; familiar to v1 contributors.
2. **Go.** Strong concurrency primitives, simple deployment story, smaller runtime.
3. **.NET / C#.** Strong type system, mature ASP.NET stack, mature Dapr SDK with first-class actor and workflow support.

Rust was not seriously considered — the productivity gap relative to .NET was not justified for an infrastructure layer that is not CPU-bound.

## Decision

**Platform infrastructure (actors, routing, REST API, workflows, CLI, EF Core data access) is .NET 10 / C#. Agent runtimes stay language-agnostic — they ship as containers and talk to the platform over MCP / A2A through the Dapr sidecar.**

- **Type safety where it matters.** Message envelopes, `IAddressable`, `Cvoya.Spring.Core` interfaces, EF entity configurations, OpenAPI-generated clients — every contract is compile-time-checked. The class of v1 bug we want to extinguish does not survive a compiled C# build.
- **Dapr SDK quality.** The .NET Dapr SDK has the most complete actor + workflow support; the actor remoting path under `DataContractSerializer` (which all v2 actors use) is mature.
- **ASP.NET + EF Core.** REST hosting, OpenAPI generation, validation, auth handlers, and EF Core for tenant-aware repositories are all first-class — we do not need to assemble a stack.
- **Throughput.** The infrastructure layer routes a high volume of small messages and serves OpenAPI traffic. .NET delivers throughput well beyond Python without the cognitive cost of Go's manual concurrency primitives or Rust's borrow checker.
- **Single-language platform, multi-language agents.** The choice does NOT constrain agent behaviour: every agent runtime runs in its own container with whatever language and tool it ships (Claude Code CLI, OpenAI SDK in Python, dapr-agent in TypeScript, …). The platform never executes agent business logic in-process.

## Alternatives considered

- **Python.** Familiar but precisely the source of v1's runtime-error class. Type hints are advisory at runtime; mypy coverage in the layers we care about would have to approach 100 % to match what C# gives us by construction.
- **Go.** Excellent runtime, but the SDK and EF Core gap is real (we'd be hand-rolling repository code), and the type system loses the discriminated-union / records / pattern-matching ergonomics that v2's message and policy types depend on.

## Consequences

- **One language to onboard contributors into.** Anyone who can read C# can read every layer of the platform.
- **Dapr SDK pin matters.** Major Dapr SDK upgrades are platform events, not per-feature changes; tracked under the platform-versioning section of [`docs/architecture/security.md`](../architecture/security.md).
- **Web portal is its own stack.** `src/Cvoya.Spring.Web/` is Next.js / React / TypeScript (see [ADR 0001](0001-web-portal-rendering-strategy.md), [ADR 0005](0005-portal-standalone-mode.md)). The contract between portal and platform is the OpenAPI surface, generated once and consumed on both sides.
- **Agent runtime authors keep their language.** Adding a new agent runtime (under `src/Cvoya.Spring.AgentRuntimes.*`) means writing a thin C# `IAgentRuntime` adapter; the agent's actual behaviour stays in whatever container it ships in.
