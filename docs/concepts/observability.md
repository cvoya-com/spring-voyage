# Observability

Observability is a first-class architectural concern in Spring Voyage V2. Every agent, unit, and connector emits a structured activity stream that can be observed in real-time by humans and other agents.

## Activity Events

Every observable entity emits typed **activity events**. Each event carries:

| Field | Description |
|-------|-------------|
| **Timestamp** | When the event occurred |
| **Source** | The address of the entity that emitted it |
| **Type** | What happened (see below) |
| **Severity** | Debug, Info, Warning, or Error |
| **Summary** | A human-readable one-liner |
| **Details** | Structured payload with full context |
| **Correlation ID** | Links related events across entities |
| **Cost** | LLM cost, if applicable |

### Event Types

Activity events cover the full spectrum of agent behavior:

- **MessageReceived / MessageSent** -- communication events
- **ConversationStarted / ConversationCompleted** -- work lifecycle
- **DecisionMade** -- the agent chose a course of action
- **ErrorOccurred** -- something went wrong
- **StateChanged** -- the agent's internal state changed
- **InitiativeTriggered** -- the agent decided to act autonomously
- **ReflectionCompleted** -- the agent finished a cognition cycle
- **WorkflowStepCompleted** -- a workflow step finished
- **CostIncurred** -- an LLM call was made (with cost)
- **TokenDelta** -- live LLM token streaming
- **ToolCallStart / ToolCallResult** -- tool usage events

## Observation Layers

Different observers see agent activity at different levels:

| Layer | What | How |
|-------|------|-----|
| **Agent to Agent** | One agent watches another (mentoring, quality monitoring) | Pub/sub subscription with permission |
| **Unit to Members** | The unit sees its members' activity | Implicit -- the unit always sees members |
| **Human to Agent/Unit** | Dashboard, CLI, alerts | SSE/WebSocket for real-time, REST for queries |
| **Platform to Everything** | Telemetry, cost tracking, audit | System-wide collection |

## Streaming: Real-Time Output

Execution environments stream tokens and events back to the platform in real-time:

- **TokenDelta** -- LLM tokens as they're generated (enables live text streaming)
- **ThinkingDelta** -- reasoning tokens (if the model supports it)
- **ToolCallStart** -- the agent is invoking a tool (name, arguments)
- **ToolCallResult** -- the tool returned a result
- **OutputDelta** -- stdout/stderr from delegated execution (e.g., Claude Code output)
- **Checkpoint** -- a state snapshot for recovery and progress tracking
- **Completed** -- work finished with final result

This enables live observation of agent work -- you can watch an agent write code in real-time through the web dashboard or CLI.

## Cost Observability

Every LLM call tracks its cost. Roll-ups are available at every level:

- **Per call** -- model, tokens in/out, cost, duration
- **Per agent** -- total cost today, this month, broken down by initiative vs. work
- **Per unit** -- total cost, cost by agent, cost by activity type
- **Per tenant** -- total cost, cost by unit, budget remaining

### Cost Alerts

The platform generates alerts when costs exceed thresholds:

- Agent exceeds daily budget -- pause initiative
- Unit exceeds monthly budget -- notify owner
- Unusual cost spike -- alert platform admin

## Delivery Channels

Activity events reach observers through multiple channels:

- **SSE/WebSocket** -- real-time streaming to the web dashboard and CLI
- **Pub/Sub Topics** -- agent-to-agent observation (with permission)
- **Persistent Store** -- all events stored for replay, analytics, and audit
- **Notifications** -- Slack, email, GitHub comments (via connectors)
