# Agents

> **[Architecture Index](README.md)** | Related: [Units](units.md), [Orchestration](orchestration.md), [Policies](policies.md), [Expertise](expertise.md), [Messaging](messaging.md), [Initiative](initiative.md), [Workflows](workflows.md), [Agent Runtime](agent-runtime.md)

This document describes the **agent** as an individual entity: its definition, execution pattern, cloning model, role, and how its prompt is assembled at activation time. For how agents compose into units, see [Units](units.md). For how units route work to agents, see [Orchestration](orchestration.md).

---

## Agent Model

An agent definition describes *what* the agent is — not *where* or *how* it runs. Agents are created declaratively (YAML applied via CLI or API) or programmatically (API call). The lifecycle is: **define → create → activate → run → deactivate → delete**. Dapr virtual actors handle activation/deactivation automatically — an agent actor is activated on first message and deactivated after idle timeout.

```yaml
# yaml-language-server: $schema=schemas/agent.schema.json
agent:
  id: ada
  name: Ada
  
  role: backend-engineer
  capabilities: [csharp, python, fastapi, postgresql, testing]
  
  ai:
    agent: claude                       # registered AI agent provider
    model: claude-sonnet-4-6
    tool: claude-code                   # registered agent tool
    environment:                        # container definition
      image: spring-agent:latest
      runtime: podman                   # podman | docker | kubernetes
    
  cloning:
    policy: ephemeral-with-memory
    attachment: attached
    max_clones: 3
    
  instructions: |
    You are a backend engineer...
    
  expertise:
    - domain: python/fastapi
      level: advanced
    - domain: postgresql
      level: intermediate
    
  activations:
    - type: message                     # direct messages
    - type: subscription
      topic: pr-reviews
      filter: "labels contains 'backend'"
    - type: reminder
      schedule: "0 9 * * MON-FRI"
      payload: { action: "daily-standup" }
    - type: binding
      component: github-webhook
      route: /issues
```

### Execution Pattern

The agent actor dispatches work to an execution environment (container) that launches a registered agent tool (e.g., `claude-code`). The tool drives the agentic loop — reading files, writing code, running tests, invoking MCP servers the platform exposes. The actor monitors via streaming events and collects results. Requires `tool` and `environment` in the `ai` block — `tool` names the registered agent tool, `environment` specifies the container image and runtime. Essential for: software engineering, document editing, any multi-step tool use.

Spring Voyage does **not** implement its own in-platform tool-use loop. The `Hosted` execution mode that previously sat alongside delegation was removed — see [ADR 0021 — Spring Voyage is not an agent runtime](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md).

**Execution environment definition** is the same for agents and units. The `ai.environment` block specifies the container:

```yaml
ai:
  environment:
    image: spring-agent:latest         # container image
    runtime: podman                    # podman | docker | kubernetes
```

Agents that don't specify `execution.<field>` inherit the default from their parent unit's `execution` block (see [Unit execution defaults](orchestration.md#unit-execution-defaults-and-the-agent--unit--fail-resolution-chain-601-b-wide) in the Orchestration doc). This is implemented end-to-end per the resolution chain described there — the `IAgentDefinitionProvider` merges the unit-level block onto the agent-declared block at dispatch time, and both HTTP / CLI surfaces edit the same persisted JSON document the resolver reads.

**Lightweight LLM calls** (routing decisions, classification, summarisation) remain in-platform via `IAiProvider.CompleteAsync` / `StreamCompleteAsync`. These are utility calls — no multi-turn loop, no tool use — and do not constitute agent execution.

### Agent Cloning

In v1, handling concurrent work of the same type required manually defining multiple identical agents (e.g., three backend engineers). The current platform replaces this with platform-managed cloning — the platform spawns copies of an agent on demand, governed by the agent's cloning policy.

**Cloning policies** (property of the agent definition):


| Policy                  | Behavior                                                                                                                                                                       |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `none`                  | Singleton. Work queues if the agent is busy. The agent accumulates unique knowledge and experiences over time.                                                                 |
| `ephemeral-no-memory`   | Clone spawned from the parent's current state (instructions, capabilities, memory snapshot). Handles one thread. Destroyed after completion. Nothing flows back.               |
| `ephemeral-with-memory` | Same as above, but the clone's experiences are sent back to the parent before destruction. The parent integrates what it deems relevant into its own memory.                   |
| `persistent`            | Clone persists independently and evolves on its own path. A persistent clone is a full agent — it can define its own cloning policy (bounded by `max_clones` and cost budget). |


**Attachment model** (how clones relate to the parent's unit):


| Mode       | Effect                                                                                                                                                                                                                                                                                                                                                               |
| ---------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `detached` | Clones become direct members of the parent's unit — peers of the parent. The unit's orchestration strategy routes work across the parent and its clones.                                                                                                                                                                                                             |
| `attached` | The parent agent promotes itself to a unit. Clones become its members. From the enclosing unit's perspective, the parent remains a single entity (a unit IS an agent). The parent becomes the orchestrator — it stops taking work itself and only routes to its clones. If all clones are destroyed and no active cloning is needed, the parent reverts to an agent. |


**Constraints:**

- **Units cannot be cloned.** A unit already manages composition through membership. Cloning is an agent-level concept.
- **Clones inherit** the parent's instructions, capabilities, expertise, execution pattern, and (for ephemeral clones) a snapshot of the parent's memory at clone time.
- **`max_clones`** caps the number of concurrent clones. The platform will not exceed this limit regardless of work queue depth.
- **Cost budget enforcement.** Clone creation respects the unit's cost budget. If the budget is exhausted, work queues instead of spawning new clones.
- **Persistent clones can clone.** A persistent clone is a full agent with its own UUID, memory, and evolution. It can define its own cloning policy, enabling recursive scaling — bounded by `max_clones` at each level and the unit's cost budget.
- **Observability.** Clone activity is attributed to the parent agent in activity streams and cost tracking, with the clone's UUID as a sub-identifier. Persistent clones that have diverged sufficiently may be promoted to independent agents (manual operation).

**When to use which:**

- `none` — Agents where continuity and unique evolution matter: lead architects, specialized experts, agents that build long-term relationships with humans.
- `ephemeral-no-memory` — Stateless workers: formatters, linters, validators, anything where the clone's experience has no lasting value.
- `ephemeral-with-memory` — Skilled workers: the parent is a senior engineer who spawns temporary helpers. Each helper's learnings (patterns discovered, pitfalls encountered) feed back to the parent, making it better over time.
- `persistent` — Scale-out: the agent needs genuinely independent instances that build their own expertise. Each clone diverges and specializes.
- `detached` — Simple scaling within an existing unit. The unit's orchestration strategy manages routing.
- `attached` — Encapsulated scaling. The parent hides its clones behind a unit boundary. Clean abstraction for the enclosing unit.

#### Persistent Cloning Policy (#416)

The enum above tells the lifecycle workflow which memory shape to use for a single clone. A **persistent cloning policy** (`AgentCloningPolicy`) is the governance record attached to the agent — or tenant-wide as the default — that constrains every clone request an operator makes. It is consulted by `IAgentCloningPolicyEnforcer` before the clone endpoint schedules the lifecycle workflow.

| Slot | Type | What it constrains |
| ---- | ---- | ------------------ |
| `AllowedPolicies` | `IReadOnlyList<CloningPolicy>?` | Allow-list over the memory-shape enum. `null` = any. |
| `AllowedAttachmentModes` | `IReadOnlyList<AttachmentMode>?` | Allow-list over `detached` / `attached`. `null` = any. |
| `MaxClones` | `int?` | Concurrent-clone cap. The lifecycle workflow's `ValidateCloneRequestActivity` enforces this; the enforcer forwards the resolved value. |
| `MaxDepth` | `int?` | Recursive-cloning cap. `0` disables cloning entirely at this scope; `null` defers to the platform default. |
| `Budget` | `decimal?` | Per-clone cost budget forwarded to the validation activity. |

**Resolution order.** The enforcer walks agent-scoped policy first and tenant-scoped second. Numeric caps (`MaxClones`, `MaxDepth`, `Budget`) collapse to the **tightest non-null value** across the two scopes so a tenant ceiling cannot be relaxed by an agent-scoped override. Allow-list slots intersect — a request is accepted only if every scope that set the list contains the requested value.

**Unit-boundary honouring (PR #497).** A detached clone is registered as a peer in the parent's unit. When the parent's unit has opaque boundary rules (`UnitBoundary.Opacities` non-empty), creating a detached clone would surface a new addressable entity through that wall. The enforcer refuses detached clone requests whose source agent is a member of such a unit and returns a `boundary` deny with an actionable message: switch to `--attachment-mode attached`, or widen the boundary.

**Operator surface.**

- **HTTP** — `GET / PUT / DELETE /api/v1/agents/{id}/cloning-policy` and `/api/v1/tenant/cloning-policy`. The empty shape is always returned for scopes that have never had a policy persisted so callers never need to branch on 404 vs empty-policy.
- **CLI** — `spring agent clone policy get|set|clear` with `--scope agent|tenant`. `set` accepts per-flag edits (`--allowed-policy`, `--allowed-attachment`, `--max-clones`, `--max-depth`, `--budget`) or a YAML fragment via `-f`. `clear` removes the policy row.
- **Portal** — tracked as [#534](https://github.com/cvoya-com/spring-voyage/issues/534) (backend + CLI ship here; the portal tab lands as a follow-up).

**Storage.** Policies persist via `IAgentCloningPolicyRepository`, backed in the OSS default by the shared `IStateStore` under `Agent:CloningPolicy:{id}` / `Tenant:CloningPolicy:{tenantId}`. An all-null policy (`AgentCloningPolicy.Empty`) is represented as a deleted row so the store reflects scopes that actually have a policy. A private-cloud host can layer a tenant-scoped wrapper via `TryAdd*` without reshaping persistence.

### Role

Role serves two purposes:

1. **Multicast routing input** — a multicast send can target every agent in a unit advertising a given role; the multicast resolver expands the (unit, role) pair into a set of `agent:<id>` addresses, each routed individually
2. **Capability signal** — other agents reason about delegation based on role

### Prompt Assembly & Platform Tools

The agent's AI needs context beyond its user-defined instructions. The actor assembles the full prompt at activation time by composing four layers:


| Layer                          | Source                                      | Content                                                                      | Mutability      |
| ------------------------------ | ------------------------------------------- | ---------------------------------------------------------------------------- | --------------- |
| **1. Platform**                | System-provided                             | Platform tool descriptions, safety constraints, behavioral guidance          | Immutable       |
| **2. Unit context**            | Injected by actor at activation             | Unit policies, peer directory snapshot, active workflow state, skill prompts | Dynamic         |
| **3. Thread context**          | Injected by actor per invocation            | Prior messages, checkpoints, partial results for the agent's current thread  | Per-invocation  |
| **4. Agent instructions**      | User-defined (`instructions` in agent YAML) | Role-specific guidance, domain knowledge, personality                        | User-controlled |


The composed prompt becomes the system prompt handed to the execution environment (typically written to `AGENTS.md` / `CLAUDE.md` in the container's working directory or passed via `SPRING_SYSTEM_PROMPT`).

**Layer 3 — Thread context** is the platform-side continuity affordance for delegated agents across invocations. The actor composes Layer 3 from: (1) prior messages exchanged in this thread, (2) the last checkpoint state (if the previous invocation checkpointed), and (3) any partial results from prior invocations. Layer 3 is empty for new threads and grows as threads progress. For suspended-then-resumed threads, Layer 3 includes the full thread history up to the suspension point. This continuity is platform-provided — it does not depend on the runtime tool itself supporting session resume.

**Open question — context-assembly strategy.** The Layer 3 inventory above (messages + checkpoint + partial results) is the v0.1 starting point, not the final shape. Context assembly is a design knob: the platform could pre-assemble a richer Layer 3 — recent timeline entries plus task summaries, memory highlights, and domain-specific cues the agent is likely to need — or could keep Layer 3 minimal and let the agent pull additional context on demand via `recall`, `peek_pending`, and other MCP tools. Push-heavy assembly gives the agent a richer cold-start prompt at higher token cost and lower flexibility; pull-heavy assembly keeps prompts lean at the cost of more LLM round-trips when the agent realises it needs more context. A hybrid is likely; the baseline-vs-opt-in split, the opt-in mechanism (per-agent? per-unit? per-thread?), and the pull-side tool surface for context discovery are tracked as [#1301](https://github.com/cvoya-com/spring-voyage/issues/1301).

**Runtime-level persistence is additionally available** via the per-agent persistent volume specified in [ADR-0029](../decisions/0029-tenant-execution-boundary.md) and [`docs/specs/agent-runtime-boundary.md`](../specs/agent-runtime-boundary.md) § 3. An agent runtime that has its own session / thread / conversation concept — e.g., Claude CLI's session resume — can persist its session state to the volume and resume natively across invocations. Different runtimes have different conventions; the platform does not standardise the mechanism, and an agent author who wants to use runtime-side persistence configures it via the runtime's own knobs.

**Open mechanism — thread ↔ runtime-session association.** When a runtime supports native session resume, the Spring Voyage `thread_id` (platform side) and the runtime's session id (runtime side) are not currently linked: the runtime doesn't know which of its sessions corresponds to which Spring Voyage thread, and vice versa. Both forms of continuity work, but each is independent. A mechanism for the association — and an option for the platform to nudge a runtime toward reusing the right session for a given thread — is a forward-looking design question; tracked as [#1300](https://github.com/cvoya-com/spring-voyage/issues/1300). See [ADR-0030](../decisions/0030-thread-model.md) and [`docs/architecture/thread-model.md`](thread-model.md) for the thread model.

**Platform tools (Layer 1)** expose platform capabilities to the agent's AI as callable tools. The agent reasons in terms of actions, not messages — the platform translates tool calls into the appropriate messages and service calls internally.


| Tool             | Description                                                                                   |
| ---------------- | --------------------------------------------------------------------------------------------- |
| `checkMessages`  | Retrieve pending messages on the agent's current thread (delegated agents call at task boundaries) |
| `discoverPeers`  | Query the unit directory for agents with specific expertise or roles                           |
| `requestHelp`    | Ask another agent (by ID or role) for assistance on the current thread                         |
| `store`          | Persist a memory artifact (a fact, a lesson, an observation, …) to the agent's `AgentMemory`. The platform stamps `thread_id` and `threadOnly` from the thread's `ThreadMemoryPolicy`. (Replaces `storeLearning` per [ADR-0030](../decisions/0030-thread-model.md).) |
| `recall`         | Read from the agent's `AgentMemory` (filtered to entries visible in the current thread). (Replaces `recallMemory` per [ADR-0030](../decisions/0030-thread-model.md).) |
| `checkpoint`     | Save progress on the current thread (enables message retrieval and recovery)                   |
| `reportStatus`   | Update the activity stream with current status                                                 |
| `escalate`       | Raise an issue to a human or to the unit for re-routing                                        |


Additional tools are injected based on the agent's tool manifest and the unit's connectors (e.g., a GitHub connector adds `createPR`, `pushCommit`, etc.).

---

## Appendix: Agent Definition Schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "schemas/agent.schema.json",
  "title": "Agent Definition",
  "type": "object",
  "required": ["agent"],
  "properties": {
    "agent": {
      "type": "object",
      "required": ["id", "name", "role", "ai"],
      "properties": {
        "id": {
          "type": "string",
          "pattern": "^[a-z][a-z0-9-]*$",
          "description": "Local symbol for the agent inside this manifest file. Mapped to a fresh Guid by the install pipeline and never persisted as the agent's identity. The agent's stable identifier is the Guid; `display_name` (presentation-only, not unique) is set separately."
        },
        "name": {
          "type": "string",
          "description": "Display name (presentation-only). Not unique, not addressable, validated to reject Guid-shaped values."
        },
        "role": {
          "type": "string",
          "description": "Role identifier. Used as input to multicast routing and as a capability signal."
        },
        "capabilities": {
          "type": "array",
          "items": { "type": "string" },
          "description": "List of capability tags."
        },
        "ai": {
          "type": "object",
          "required": ["agent", "model", "execution"],
          "properties": {
            "agent": {
              "type": "string",
              "description": "Registered AI agent provider (e.g., claude, openai)."
            },
            "model": {
              "type": "string",
              "description": "Model identifier."
            },
            "tool": {
              "type": "string",
              "description": "Registered agent tool name (e.g., claude-code)."
            },
            "environment": {
              "type": "object",
              "properties": {
                "image": { "type": "string", "description": "Container image for the execution environment." },
                "runtime": { "type": "string", "enum": ["podman", "docker", "kubernetes"] }
              },
              "description": "Container definition. Inherited from unit's execution block if not specified."
            }
          },
          "required": ["tool"]
        },
        "cloning": {
          "type": "object",
          "properties": {
            "policy": {
              "type": "string",
              "enum": ["none", "ephemeral-no-memory", "ephemeral-with-memory", "persistent"],
              "default": "none"
            },
            "attachment": {
              "type": "string",
              "enum": ["detached", "attached"],
              "description": "detached: clones join the parent's unit as peers. attached: parent promotes to a unit with clones as members."
            },
            "max_clones": {
              "type": "integer",
              "minimum": 1,
              "description": "Maximum number of concurrent clones."
            }
          },
          "if": { "not": { "properties": { "policy": { "const": "none" } } } },
          "then": { "required": ["attachment", "max_clones"] }
        },
        "instructions": {
          "type": "string",
          "description": "System prompt / instructions for the agent."
        },
        "expertise": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["domain", "level"],
            "properties": {
              "domain": { "type": "string" },
              "level": {
                "type": "string",
                "enum": ["beginner", "intermediate", "advanced", "expert"]
              }
            }
          },
          "description": "Seeded expertise profile."
        },
        "activations": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["type"],
            "properties": {
              "type": {
                "type": "string",
                "enum": ["message", "subscription", "reminder", "binding"]
              },
              "topic": { "type": "string" },
              "filter": { "type": "string" },
              "schedule": {
                "type": "string",
                "description": "Cron expression (for reminder type)."
              },
              "payload": { "type": "object" },
              "component": {
                "type": "string",
                "description": "Dapr binding component name (for binding type)."
              },
              "route": {
                "type": "string",
                "description": "Route path (for binding type)."
              }
            }
          },
          "description": "What causes this agent to activate."
        }
      }
    }
  }
}
```

---

## See Also

- [Units](units.md) — unit entity model; how agents compose into groups
- [Orchestration](orchestration.md) — how units route work to agents; execution defaults; unit boundary
- [Policies](policies.md) — unit policy framework constraining agent behaviour
- [Expertise](expertise.md) — expertise profiles and directory
- [Unit Lifecycle](unit-lifecycle.md) — validation, status, paths to creation
- [Messaging](messaging.md) — mailbox, thread model, `AgentMemory`
- [Initiative](initiative.md) — agent initiative levels and tiered cognition
- [Agent Runtime](agent-runtime.md) — dispatcher, launcher contract, MCP callback
- [ADR-0021](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md) — why the platform does not host its own tool-use loop
- [ADR-0030](../decisions/0030-thread-model.md) — thread / `AgentMemory` / `ThreadMemoryPolicy` model
