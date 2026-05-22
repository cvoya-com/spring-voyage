# Spring Voyage — Architecture

**Status:** Living document — kept in sync with the implementation. Any
design-affecting change updates the relevant page here, and its diagrams, in the
same PR.

This is the canonical description of how Spring Voyage is built. It describes
the system **as it is**. The narrow, dated trade-offs behind each major choice
live as [Architecture Decision Records](../decisions/README.md) — reach for an
ADR when you want the "why not the obvious alternative?"; reach for these pages
when you want "how does it work?".

---

## What Spring Voyage is

Spring Voyage is an open-source collaboration platform for teams of AI agents —
and the humans they work with. It is a substrate for standing up small fleets of
AI collaborators that operate on real work, on the real systems where that work
happens, with people in the loop where it counts.

Autonomous AI agents — organised into composable groups called **units** —
collaborate with each other and with humans on any domain: software engineering,
product management, research, operations, creative work. Agents connect to
external systems through pluggable **connectors**, exchange typed **messages** on
durable **threads**, can take **initiative**, and are observable in real time.

The platform is built on **.NET 10** and **Dapr**. Its load-bearing principles:

- **A unit is an agent that has children.** Composition is recursive; a unit
  appears to its parent as a single agent. There is no separate "orchestrator"
  concept ([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md)).
- **The platform delivers messages; it does not orchestrate.** Domain messages
  are one-way events. How a unit routes work across its members is *runtime
  behaviour* — the decision lives in the agent's runtime image and instructions,
  not in platform configuration.
- **Spring Voyage is not an agent runtime.** It coordinates *external* agent
  runtimes (Claude Code, Codex, Gemini CLI, and others) running in containers;
  it does not implement its own tool-use loop
  ([ADR-0021](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md)).
- **The OSS core is a framework.** A private repository extends it via DI for
  multi-tenancy, auth, and billing. Every abstraction is an extension seam.

## Reading order

If you are new to the system, read these four pages in order:

1. **[Components](components.md)** — every host, service, actor, and sidecar,
   what each one owns, and how they connect. Start here.
2. **[Runtime flows](runtime-flows.md)** — the principal end-to-end flows
   (connector event → routing turn → dispatch → tool call; deploy; lifecycle),
   each with a sequence diagram.
3. **[Messaging](messaging.md)** — messages, threads, the agent mailbox, the
   one-way delivery substrate, and the platform MCP tool surface.
4. **[Units & agents](units-and-agents.md)** — the entity model: composition,
   membership, lifecycle, expertise, policies, initiative, cloning.

## The full set

| Document | Topics |
|----------|--------|
| [Components](components.md) | Host roles, services, Dapr actors, the dispatcher, the sidecar bridge, infrastructure dependencies, component topology |
| [Runtime flows](runtime-flows.md) | Connector event → turn → tool call; persistent-agent deploy; agent/unit lifecycle; message delivery |
| [Messaging](messaging.md) | `Message`, `MessageType`, threads & the Timeline, the agent mailbox, one-way delivery, the `sv.<area>.<verb>` MCP tool surface, `AgentMemory` |
| [Units & agents](units-and-agents.md) | Composite model, membership graph, lifecycle & validation, expertise & the directory, unit policies, initiative, cloning, boundary |
| [Agent runtime](agent-runtime.md) | Runtime catalogue, the AgentRuntime / ModelProvider split, launchers, the A2A sidecar bridge, agent images, the AgentSDK, credential handling |
| [Connectors](connectors.md) | The `IConnectorType` contract, inbound webhook translation, outbound skills, connector bindings |
| [Packages](packages.md) | The recursive package format, the `members:` grammar, install / export |
| [Security](security.md) | Authentication, platform roles, permissions, tenant isolation, the secrets stack |
| [Data & identity](data-and-identity.md) | PostgreSQL / EF Core vs Dapr actor state, the state-ownership matrix, identifiers and addresses, the OSS default tenant |
| [Deployment](deployment.md) | Agent hosting modes, container topology, the dispatcher, Dapr sidecar bootstrap, startup configuration, releases |
| [Observability](observability.md) | Activity events, the `IObservable` streams, OpenTelemetry capture, cost tracking |
| [Interfaces](interfaces.md) | The public Web API, the `spring` CLI, the two-portal web architecture, UI/CLI parity |
| [Open questions](open-questions.md) | Design questions not yet decided |

For the concept-level introduction (what an agent *is*, what a unit *is*), see
[`docs/concepts/`](../concepts/overview.md). For role-based how-to guides, see
[`docs/guide/`](../guide/README.md).
