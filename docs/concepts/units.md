# Units

A **unit** is a composite agent -- a group of agents that appears as a single entity to the outside world. Units are the organizational primitive of Spring Voyage.

## The Core Idea: A Unit IS an Agent

The most important thing about units is that they implement the same interface as individual agents. A unit can receive messages, has an address, has an expertise profile, and emits an activity stream. Any entity that can interact with an agent can interact with a unit -- without knowing or caring that it's actually a group.

This means units compose recursively. A unit can contain other units, which contain other units, to arbitrary depth. An engineering team (a unit) can contain a backend team (a unit), a frontend team (a unit), and a DevOps team (a unit). From the outside, the engineering team looks like a single agent.

## What a Unit Owns

Every unit manages:

- **Identity** -- an address, just like any agent (e.g., `agent://engineering-team`)
- **Membership** -- which agents and sub-units belong to this unit
- **Boundary** -- what is visible to the parent unit (see below)
- **Activity stream** -- aggregated activity from all members
- **Expertise directory** -- the combined expertise of all members
- **Policies** -- rules governing communication, work assignment, initiative, and cost

## Orchestration: A Mechanism Inside the Unit

Once a unit exists and has members, it needs an answer to a narrow question: when a message arrives, which member handles it? That decision is made by the unit's **orchestration strategy** -- a pluggable component that determines how messages are routed to members.

Orchestration is one mechanism inside the unit's collaboration model -- it sits alongside membership, the boundary, policies, and the activity stream. It is not the whole of what a unit is; it is how the unit decides to route the next piece of work.

Three orchestration strategies ship today:

| Strategy | Description | AI Involvement |
|----------|-------------|----------------|
| **AI-orchestrated** | A single LLM call receives the message plus the member list and returns the target member address. Default strategy. | Full |
| **Workflow** | A durable workflow container drives the sequence. The container invokes agents as activities. | None or minimal |
| **Label-routed** | Payload labels are matched against a trigger map; the message is forwarded to the mapped member. | None |

The strategy can be swapped independently of the unit's identity — for example, upgrading from label-routed to AI-orchestrated as a team matures. See [Architecture: Orchestration](../architecture/orchestration.md) for the full strategy catalogue and the resolver protocol.

### AI-Orchestrated Routing

When a unit uses the AI-orchestrated strategy, Spring Voyage makes a single lightweight LLM call (no tool loop — see [ADR 0021 — Spring Voyage is not an agent runtime](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md)) to decide which member should receive the incoming message. The LLM sees the message plus the unit's member directory and returns a target address. The member then runs in its own execution environment as usual.

For workflow-based orchestration, the unit delegates to a workflow container that drives the sequence and may invoke agents as activities.

## Unit Boundary

When a unit participates as a member of a parent unit, its **boundary** controls what the parent can see.

### Opacity Levels

| Level | What the parent sees |
|-------|---------------------|
| **Transparent** | All members, their capabilities, expertise, and activity streams. Full internal structure. |
| **Translucent** | A filtered or projected subset. The boundary defines what is exposed. |
| **Opaque** | The unit appears as a single agent. No internal structure visible. |

### Boundary Operations

- **Projection** -- expose a subset of member capabilities as the unit's own. The engineering team exposes "implement feature" and "review PR" but hides "run CI" and "deploy staging."
- **Filtering** -- only certain message types pass through the boundary. Internal status updates stay internal; only completed results, errors, and escalations propagate outward.
- **Synthesis** -- create new virtual capabilities by combining members. "Full-stack implementation" emerges from the combination of backend, frontend, and QA agents.
- **Aggregation** -- expertise profiles and activity streams are merged before exposing to the parent.

### Deep Access

Despite encapsulation, a human or agent with appropriate permissions can address any agent at arbitrary depth using the full address path (e.g., `agent://acme/engineering-team/backend-team/ada`). The boundary is a default, not a wall -- permission-based deep access is always available.

## Organizational Patterns

Units can model any organizational structure:

| Pattern | Description | Example |
|---------|-------------|---------|
| **Engineering Team** | Specialized agents with defined roles | Backend + frontend + QA + DevOps |
| **Product Squad** | Cross-functional group for a feature | PM + design + engineering agents |
| **Research Cell** | Agents autonomously monitoring a domain | Paper tracking, trend analysis |
| **Support Desk** | Agents responding to external requests | Customer support, internal helpdesk |
| **Creative Studio** | Agents collaborating on creative output | Writing, design, art direction |
| **Operations Center** | Agents monitoring systems and incidents | Infrastructure alerts, SLA monitoring |
| **Ad-hoc Task Force** | Temporary unit for a specific problem | Incident response, sprint goal |

These patterns are illustrative -- any structure can be modeled through unit composition, boundary configuration, and orchestration strategy selection.

## The Root Unit

Every tenant has an implicit **root unit** -- the top-level container. All units and standalone agents exist within the root unit. It provides tenant-wide directory services, addressing, cross-unit routing, and default policies.

## Human Participation

Units define which humans can interact with them and at what permission level:

| Role | Permissions |
|------|-------------|
| **Owner** | Full control -- configure, manage members, set policies, delete |
| **Operator** | Start/stop, interact with agents, approve workflow steps, view all |
| **Viewer** | Read-only -- state, activity feed, metrics, agent status |

Multiple humans can participate in the same unit at different permission levels.
