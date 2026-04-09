# Workflows and Orchestration

This document describes the two workflow models in Spring Voyage V2 and how external workflow engines integrate.

## Two Workflow Models

### 1. Workflow-as-Container (Primary Model)

Domain workflows -- the structured processes that units use to coordinate work -- run as **containers**. Each workflow container has its own Dapr sidecar and orchestrates by sending messages to agents in the unit.

**How it works:**

1. The unit's workflow orchestration strategy receives an incoming message
2. The strategy dispatches to the workflow container via Dapr service invocation
3. The workflow container orchestrates the work -- calling agents as activities, waiting for events, managing state
4. The workflow communicates with agents via its Dapr sidecar (messages, pub/sub, state)
5. On completion, the workflow reports results back to the unit actor

**Key benefit:** Updating a workflow means deploying a new container image, not recompiling the platform. Running workflow instances complete on the old container; new instances use the new image.

**Any workflow engine** can run inside the container:
- Dapr Workflows (C# or Python) for durable orchestration
- Temporal, if the team prefers that model
- Any custom process that can speak to the Dapr sidecar

### 2. Platform-Internal Workflows

A small set of workflows are compiled into the .NET host for platform-internal orchestration:

- Agent lifecycle management (create, activate, deactivate, delete)
- Cloning lifecycle (spawn clone, manage memory flow-back, destroy)
- Other platform concerns

Platform-internal workflows are **never** used for domain orchestration. Domain workflows always run in containers.

## Orchestration Strategies

The unit's orchestration strategy determines how incoming messages are routed to members. Five strategies are available:

| Strategy | How It Works | When to Use |
|----------|-------------|-------------|
| **Rule-based** | Deterministic routing by policy (round-robin, role-matching, capability-based, priority queue). No LLM. | Load-balanced work distribution, simple routing |
| **Workflow** | A Dapr Workflow in a container drives the sequence. Steps invoke agents as activities. | CI/CD pipeline, compliance review, structured processes |
| **AI-orchestrated** | An LLM receives the message plus unit context and decides routing, assignment, and coordination. | Intelligent triage, adaptive team coordination |
| **AI+Workflow hybrid** | A workflow provides the skeleton (phases); an LLM fills in decisions within each phase. | Structured development cycles with flexible decision-making |
| **Peer** | Broadcast to all members. No routing. Members decide for themselves whether to act (via initiative). | Research brainstorming, open-ended collaboration |

The AI+Workflow hybrid is recommended for structured work: reliable enough to be auditable, flexible enough to handle novel situations.

## Workflow Patterns

All patterns are supported regardless of which workflow engine runs inside the container:

| Pattern | Description | Example |
|---------|-------------|---------|
| **Sequential** | Steps execute one after another | triage, assign, implement, review |
| **Parallel** | Multiple steps concurrently | tests + linting + security scan |
| **Fan-out/Fan-in** | Distribute work, aggregate results | assign to 3 agents, collect PRs |
| **Conditional** | Branch based on state | if complexity > threshold, require human review |
| **Loop** | Repeat until condition met | review cycle until approved |
| **Human-in-the-loop** | Pause, wait for human input | approval before implementing |
| **Sub-workflow** | Delegate to nested workflow | "implement feature" is multi-step |

## External Workflow Engines via A2A

The platform supports external workflow engines as unit orchestrators via the A2A (Agent-to-Agent) protocol:

| Engine | Integration |
|--------|------------|
| **Google ADK** | An ADK agent graph runs as a Python process, participates via A2A |
| **LangGraph** | A LangGraph graph runs as a Python process, same A2A integration |
| **Custom** | Any process that speaks A2A can orchestrate a unit |

### A2A Protocol

A2A is an open protocol for cross-framework agent communication. It enables:

- **External agents as unit members** -- an ADK agent or LangGraph node participates in a Spring unit via A2A, wrapped as an actor
- **External orchestrators** -- an external workflow engine drives a Spring unit's agents via A2A
- **Cross-platform collaboration** -- Spring agents collaborate with agents built on other frameworks

Each external agent is wrapped to look like a native agent at the messaging level -- indistinguishable from a native agent to the rest of the unit.
