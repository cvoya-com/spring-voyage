# Execution Environments

This document describes how agent work is isolated, executed, and monitored in Spring Voyage V2.

## Brain and Hands Separation

Every agent has a clear separation between reasoning (brain) and action (hands):

- **Brain** -- the agent actor, running in the host process. Manages state, makes decisions, processes messages.
- **Hands** -- the execution environment, running in an isolated container. Reads files, writes code, runs tests, interacts with external tools.

The brain dispatches work to the hands, monitors progress via streaming events, and collects results.

## Execution Patterns

### Hosted Execution

The agent actor calls the AI provider directly. The LLM reasons and responds within the actor process. No separate container. No filesystem access, no tool use.

Good for: routing, classification, triage, advisory, monitoring agents.

### Delegated Execution

The agent actor dispatches work to a container that runs a registered tool (e.g., Claude Code). The tool drives its own agentic loop inside the container.

Good for: software engineering, document editing, any multi-step tool use.

## Isolation Modes

| Mode | Isolation | Startup | Best For |
|------|-----------|---------|----------|
| **in-process** | None | Instant | LLM-only agents, research, advisory |
| **container-per-agent** | Full | Seconds | Software engineering, tool use |
| **ephemeral** | Maximum | Seconds | Untrusted code, compliance-sensitive work |
| **pool** | Full (warm) | Instant | Large-scale, mixed workloads |
| **a2a** | External | Varies | External agents (ADK, LangGraph, etc.) |

## Container Security

Execution environments are sandboxed by default:

- **No network access** unless explicitly granted
- **No filesystem access** beyond the mounted workspace
- **No access to host resources** unless explicitly permitted

Permission grants are explicit in the agent definition -- the agent specifies what network, filesystem, and secret access it needs.

## Streaming: Real-Time Output

Execution environments stream tokens and events back to the platform in real-time via Dapr pub/sub. Each agent has a dedicated streaming topic (`agent/{id}/stream`).

### Stream Event Types

| Event | Description |
|-------|-------------|
| **TokenDelta** | LLM tokens generated -- enables live text streaming |
| **ThinkingDelta** | Reasoning tokens (if model supports) |
| **ToolCallStart** | Agent is invoking a tool (name, arguments) |
| **ToolCallResult** | Tool returned a result |
| **OutputDelta** | Stdout/stderr from delegated execution |
| **Checkpoint** | State snapshot for recovery and progress tracking |
| **Completed** | Work finished with final result |

### Dual Subscriber Model

Two consumers subscribe to the same streaming topic concurrently:

1. **Agent Actor** -- processes checkpoints and completion events to update state. Projects all events to the activity stream for agent-to-agent observation.
2. **API Host** -- relays events directly to connected browsers via SSE/WebSocket for real-time display. This avoids routing every token through the actor, reducing latency for human observers.

This is standard Dapr pub/sub with multiple subscribers. The actor remains the authority on state; the API host is a pass-through for display.

## Checkpoints and Recovery

Execution environments periodically emit **checkpoint** events containing a state snapshot. If the environment crashes or is interrupted:

1. The actor detects the failure via heartbeat/timeout
2. The actor can restart the work from the last checkpoint
3. If restart fails, the actor marks the conversation as failed and escalates

This enables resumable long-running work without losing progress.

## Message Retrieval for Delegated Agents

Delegated execution environments drive their own agentic loop and don't naturally check back with the actor. The platform provides a `checkMessages` tool in the agent's tool manifest.

The agent calls `checkMessages` at natural boundaries (between subtasks, after completing a step). The tool returns any accumulated messages on the active conversation channel. This is pull-based -- the agent decides when to check.

The actor also includes a "messages pending" flag in checkpoint acknowledgments, hinting that the agent should call `checkMessages` soon.
