# Agents

An **agent** is an autonomous AI-powered entity. It is the fundamental building block of Spring Voyage.

Agents are not limited to "workers" -- an agent can be an observer, advisor, monitor, researcher, reviewer, or any other role. What makes something an agent is that it has an identity, can receive messages, and can reason about how to respond.

## What Defines an Agent

Every agent has:

- **Identity** -- a globally unique address within the platform (e.g., `agent://engineering-team/ada`)
- **Role** -- a label signaling what kind of work the agent does (e.g., `backend-engineer`, `qa-engineer`, `researcher`)
- **Capabilities** -- tags describing what the agent can do (e.g., `csharp`, `python`, `postgresql`)
- **Instructions** -- the system prompt that guides the agent's behavior, personality, and domain knowledge
- **Expertise profile** -- a structured description of what the agent knows and how well it knows it
- **Activations** -- what causes the agent to wake up and act (messages, subscriptions, schedules, external events)

## How Agents Think: Brain and Hands

Every agent has a separation between its **brain** (reasoning, decisions) and its **hands** (actions on external systems). The brain is a configured AI agent tool — Claude Code, Codex, or similar — running inside a sandboxed **execution environment** (a container). The tool drives the full agentic loop: reads files, writes code, runs tests, creates pull requests, calls MCP servers. Spring Voyage orchestrates these containers; it does not implement its own agent loop.

The agent actor monitors the execution environment via streaming events and collects results when the work completes. The execution environment is sandboxed: no network access, no filesystem access beyond the mounted workspace, unless explicitly granted.

For non-agentic platform needs — routing decisions, classification, summarisation — Spring Voyage makes lightweight LLM calls directly (no tool loop) via `IAiProvider`. These are utilities internal to the platform, not an agent execution model.

See [ADR 0021 — Spring Voyage is not an agent runtime](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md) for the rationale.

## Agent Lifecycle

An agent's lifecycle is: **define, create, activate, run, deactivate, delete**.

1. **Define** -- describe the agent in YAML or via the CLI (role, capabilities, instructions, AI configuration)
2. **Create** -- the platform registers the agent and assigns it a unique identity
3. **Activate** -- the agent actor comes to life on first message (automatic via Dapr virtual actors)
4. **Run** -- the agent processes messages, takes initiative, collaborates with peers
5. **Deactivate** -- the agent actor goes idle after a timeout (automatic, state preserved)
6. **Delete** -- the agent is removed (soft delete; history retained for audit)

Agents are typically defined declaratively in YAML files and created via `spring apply`. They can also be created imperatively through the CLI.

## The Mailbox: How Agents Handle Messages

Each agent has a **mailbox** -- a structured system for receiving and processing messages. The mailbox is logically partitioned into three channel types:

### Control Channel

Platform control messages -- cancellations, status queries, health checks, policy updates -- always go to the control channel and are processed immediately. A cancellation is never blocked behind other work.

### Conversation Channels

When an agent receives a work message (e.g., "implement this feature"), it creates a **conversation** -- a sequence of related messages identified by a conversation ID. The agent works on one conversation at a time (the **active** conversation). Additional messages for the same conversation are accumulated and available at the next checkpoint.

New conversations queue as **pending** and are started in arrival order when the active conversation completes.

An agent can **suspend** the active conversation (e.g., blocked waiting for human approval) and promote the next pending conversation. The suspended conversation resumes later with its full state intact. This ensures agents are never idle when blocked.

### Observation Channel

Events from subscriptions (pub/sub topics, observed agents, timers) accumulate in the observation channel. The agent's initiative loop processes these in batch -- "what happened since I last looked?" -- rather than one at a time.

## Agent Cloning

When an agent is busy and new work arrives, the platform can spawn **clones** -- copies of the agent that handle concurrent work. Cloning replaces the v1 approach of manually defining multiple identical agents.

### Cloning Policies

| Policy | Behavior |
|--------|----------|
| **none** | Singleton. Work queues if busy. The agent accumulates unique knowledge over time. |
| **ephemeral-no-memory** | Clone handles one conversation, then is destroyed. Nothing flows back to the parent. |
| **ephemeral-with-memory** | Clone handles one conversation, sends learnings back to the parent, then is destroyed. |
| **persistent** | Clone persists independently, evolves on its own path. A full agent in its own right. |

### Attachment Modes

| Mode | Effect |
|------|--------|
| **detached** | Clones become peers of the parent within the same unit. |
| **attached** | The parent promotes itself to a unit, with clones as its members. From the outside, the parent still looks like a single agent. |

### When to Use Which

- **none** -- agents where continuity matters: lead architects, specialized experts
- **ephemeral-no-memory** -- stateless workers: formatters, linters, validators
- **ephemeral-with-memory** -- skilled workers whose learnings feed back to a parent
- **persistent** -- genuinely independent instances that specialize over time

## Prompt Assembly

When an agent activates, the platform assembles its full prompt from four layers:

| Layer | Content |
|-------|---------|
| **Platform** | Platform tool descriptions, safety constraints, behavioral guidance (immutable) |
| **Unit context** | Unit policies, peer directory, active workflow state, skill prompts (dynamic) |
| **Conversation context** | Prior messages, checkpoints, partial results for the active conversation (per-invocation) |
| **Agent instructions** | The user-defined instructions from the agent's YAML definition (user-controlled) |

The composed prompt becomes the system prompt handed to the execution environment (for example, written to `AGENTS.md` / `CLAUDE.md` in the container's working directory, or passed via `SPRING_SYSTEM_PROMPT`).

## Platform Tools

Agents interact with the platform through **tools** -- callable functions exposed to the agent's AI. These are not external tools like "create a file" but platform-level capabilities:

| Tool | Purpose |
|------|---------|
| **checkMessages** | Retrieve pending messages on the active conversation |
| **discoverPeers** | Query the unit directory for agents with specific expertise |
| **requestHelp** | Ask another agent for assistance |
| **store** | Persist a memory artifact (a fact, a lesson, an observation) to the agent's `AgentMemory` |
| **recall** | Read from the agent's `AgentMemory` (filtered to entries visible in the current thread) |
| **checkpoint** | Save progress (enables message retrieval and recovery) |
| **reportStatus** | Update the activity stream with current status |
| **escalate** | Raise an issue to a human or to the unit |

Additional tools come from the agent's tool manifest and from connectors attached to the agent's unit.
