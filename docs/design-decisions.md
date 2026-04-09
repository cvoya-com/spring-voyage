# Design Decisions

This document captures the key architectural decisions behind Spring Voyage V2 and the reasoning that led to them.

---

## Why Dapr?

**Decision:** Use Dapr as the infrastructure runtime for all distributed systems concerns.

**Alternatives considered:** Building directly on Kubernetes primitives, using a specific message broker (Kafka/RabbitMQ), using Orleans for actors.

**Why Dapr wins:**
- **Pluggable backends.** State store, pub/sub, secrets, and bindings are all swappable via YAML. This means the same code runs on a developer laptop (PostgreSQL + Redis) and in production (Cosmos DB + Kafka + Azure Key Vault) with zero code changes.
- **Virtual actors.** Dapr's actor model provides turn-based concurrency, automatic lifecycle, durable reminders, and built-in state management -- exactly what agents need.
- **Language-agnostic.** Any process that speaks HTTP/gRPC to `localhost:3500` is a first-class citizen. This enables .NET infrastructure with Python (or any language) agent brains.
- **Sidecar pattern.** Application code never connects directly to infrastructure. mTLS, retries, observability, and circuit breakers are automatic.
- **Durable workflows.** Dapr Workflows provide task chaining, fan-out, and human-in-the-loop patterns with automatic recovery -- used for platform-internal orchestration.

---

## Why .NET for Infrastructure?

**Decision:** The platform infrastructure layer (actors, routing, API surface, workflows) is .NET/C#.

**Alternatives considered:** Python (carry forward from v1), Go, Rust.

**Why .NET wins:**
- **Type safety at the infrastructure layer.** v1 (Python) had runtime errors that type safety would have caught. The infrastructure layer -- message routing, actor state, API contracts -- benefits enormously from compile-time type checking.
- **Dapr SDK quality.** The .NET Dapr SDK is the most mature, with first-class actor and workflow support.
- **Performance.** The infrastructure layer handles high-throughput message routing and actor management. .NET provides better throughput than Python without the complexity of Go or Rust.
- **Agent brains stay language-agnostic.** The choice of .NET for infrastructure does not constrain agent brain logic. Python agents communicate via the Dapr sidecar.

---

## Why "A Unit IS an Agent" (Composite Pattern)?

**Decision:** Units implement the same interface as agents (`IMessageReceiver`, `IAddressable`). A unit can be used anywhere an agent can.

**Alternatives considered:** Separate interfaces for agents and units, with explicit delegation logic.

**Why the composite pattern wins:**
- **Recursive composition.** Units containing units is natural and requires no special handling. An engineering team (unit) containing a backend team (unit) containing individual agents -- all addressable, all messageable.
- **Boundary encapsulation.** An opaque unit looks exactly like an agent to the outside. No sender needs to know whether they're talking to an agent or a unit.
- **Simplified routing.** The platform routes messages to addresses. Whether an address resolves to an agent actor or a unit actor is transparent.

---

## Why Partitioned Mailbox (Not Simple Queue)?

**Decision:** Each agent has a three-channel mailbox (control, conversation, observation) instead of a single message queue.

**Alternatives considered:** Single FIFO queue, priority queue with sender-specified priority.

**Why the partitioned mailbox wins:**
- **Control messages are never blocked.** A cancellation or status query is processed immediately, even during active work. A simple queue would block control messages behind work messages.
- **Conversation isolation.** Messages for the same conversation are grouped. New conversations queue as pending. This prevents interleaving of unrelated work.
- **Batch observation.** Events from subscriptions accumulate and are processed in batch ("what happened since I last looked?"), which produces better LLM reasoning than event-by-event processing.
- **Platform-controlled routing.** The sender never specifies priority. The platform routes by MessageType and delivery mechanism. No sender can escalate their own message.

---

## Why Workflow-as-Container (Not Compiled into Host)?

**Decision:** Domain workflows run in containers with their own Dapr sidecars, not compiled into the .NET host process.

**Alternatives considered:** Compiling all workflows into the host, using a shared workflow service.

**Why containers win:**
- **Decoupled releases.** Updating a workflow means deploying a new container image, not recompiling and redeploying the platform. This is critical for teams iterating on domain workflows independently.
- **Any workflow engine.** The container can run Dapr Workflows, Temporal, or anything else. The platform doesn't care.
- **Running instance safety.** Running workflow instances complete on the old container; new instances use the new image. No in-flight disruption.
- **Platform-internal workflows are the exception.** A small set of lifecycle workflows (agent creation, cloning) are compiled into the host because they are platform concerns, not domain concerns.

---

## Why Tiered Cognition for Initiative?

**Decision:** Initiative uses a two-tier cognition model: cheap local LLM screening (Tier 1) before expensive primary LLM reflection (Tier 2).

**Alternatives considered:** Primary LLM for all initiative processing, rule-based screening only, no initiative.

**Why two tiers win:**
- **Cost control.** Without screening, every event triggers a full LLM call. With 5-minute polling, that's 288 calls/day at several dollars each. Tier 1 filters ~90% of events for nearly zero cost, keeping Tier 2 invocations at 5-20/day.
- **Better reasoning.** Batch processing observations ("what happened since I last looked?") produces better LLM reasoning than event-by-event processing.
- **Predictable costs.** Initiative adds ~6-8% to total agent cost, making it feasible as a default capability.

---

## Why Not Runtime Mode Switching (Hosted/Delegated)?

**Decision:** An agent is either hosted or delegated -- it does not switch at runtime.

**Alternatives considered:** Agents that dynamically switch between hosted (for reasoning) and delegated (for tool use) modes.

**Why fixed mode wins:**
- **Simpler mental model.** A triage agent is always hosted. A code-writing agent is always delegated. No mode confusion.
- **Composition over switching.** When a hosted agent needs tool use, it delegates to a delegated agent in the same unit via `requestHelp`. The triage decision and the code-writing are genuinely different cognitive tasks that benefit from different tool sets.
- **The unit provides composition.** Units already manage multiple agents working together. Using the existing composition mechanism is cleaner than adding runtime mode complexity to a single agent.

---

## Why PostgreSQL as Primary Store?

**Decision:** PostgreSQL for relational data (tenants, definitions, activity history), with Dapr state store abstraction for actor runtime state.

**Alternatives considered:** All-in on Dapr state store, separate databases for different concerns.

**Why this split wins:**
- **Relational data stays relational.** Tenant/user/org data, agent definitions, and activity history benefit from SQL queries, joins, and schema enforcement. EF Core provides a mature ORM.
- **Actor state gets portability.** Runtime state (active conversation, pending queue) goes through Dapr's state store abstraction. PostgreSQL is the backend today, but swapping to Redis or Cosmos DB requires zero code changes.
- **Single operational database.** PostgreSQL handles both roles, simplifying operations. The Dapr abstraction is about code portability, not about needing a different database.

---

## Why Flat Actor IDs (Not Hierarchical Routing)?

**Decision:** All actors have flat, globally unique Dapr actor IDs. Path addresses are resolved to actor IDs in a single directory lookup. No multi-hop forwarding through each unit in the hierarchy.

**Alternatives considered:** Messages forwarded hop-by-hop through each unit in the path.

**Why flat routing wins:**
- **Performance.** A single lookup (O(path depth) for permission checks) is always faster than forwarding through N units.
- **Simplicity.** Forwarding creates complex failure modes (what if an intermediate unit is down?). Direct routing is straightforward.
- **Permission enforcement at resolution.** Boundary checks happen once at resolution time, not at each hop.
- **Cache-friendly.** Each unit caches member paths to actor IDs. Directory-change events keep caches fresh with millisecond consistency.

---

## Open Design Questions

These decisions are not yet finalized:

1. **Web UI Technology** -- React/Next.js vs. Blazor. React has the stronger testing ecosystem; Blazor stays in .NET. Pending evaluation.
2. **Tier 1 LLM Hosting** -- In-process (ONNX/llama.cpp) vs. separate container (Ollama).
3. **Testing Strategy** -- Integration test patterns with Dapr sidecar in CI.
4. **Rx.NET Version** -- Pin to 6.x or track latest.
5. **A2A Protocol Version** -- Which version to target; maturity assessment.
6. **Initiative Policy Granularity** -- Is `max_level` sufficient, or should there be per-capability flags?
7. **Event Stream Separation** -- Whether to split ActivityEvent into high-frequency and low-frequency streams.
