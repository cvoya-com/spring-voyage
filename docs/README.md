# Spring Voyage V2 Documentation

Spring Voyage V2 is a general-purpose AI agent orchestration platform. It enables autonomous AI agents -- organized into composable groups called units -- to collaborate on any domain.

## Documentation

### [Concepts](concepts/overview.md)

The abstractions and mental model behind Spring Voyage V2. Start here.

- [Agents](concepts/agents.md) -- autonomous AI entities
- [Units](concepts/units.md) -- composable groups of agents
- [Messaging and Addressing](concepts/messaging.md) -- how entities communicate
- [Connectors](concepts/connectors.md) -- bridges to external systems
- [Initiative](concepts/initiative.md) -- autonomous decision-making
- [Observability](concepts/observability.md) -- real-time visibility
- [Packages and Skills](concepts/packages.md) -- reusable domain knowledge
- [Tenants and Permissions](concepts/tenants.md) -- multi-tenancy and access control

### [Architecture](architecture/overview.md)

How the concepts are realized as a running system.

- [Infrastructure: Dapr](architecture/infrastructure.md) -- the runtime foundation
- [Actor Model](architecture/actors.md) -- agents as virtual actors
- [Workflows and Orchestration](architecture/workflows.md) -- structured coordination
- [Execution Environments](architecture/execution.md) -- container isolation and streaming
- [Data Persistence](architecture/data.md) -- storage and state management
- [API and Hosting](architecture/api-hosting.md) -- deployment and the CLI
- [Security and Resilience](architecture/security.md) -- auth, isolation, and failure recovery

### [User Guide](guide/overview.md)

How to use Spring Voyage V2 through the `spring` CLI.

- [Getting Started](guide/getting-started.md) -- first setup, first unit, first agent
- [Managing Units and Agents](guide/units-and-agents.md) -- creation, configuration, lifecycle
- [Messaging and Interaction](guide/messaging.md) -- sending messages, conversations
- [Observing Activity](guide/observing.md) -- streams, cost tracking, dashboards
- [Declarative Configuration](guide/declarative.md) -- YAML definitions and `spring apply`

### [Developer Guide](developer/overview.md)

For contributors to the Spring Voyage V2 platform.

- [Development Setup](developer/setup.md) -- prerequisites, building, running locally
- [Creating Packages](developer/creating-packages.md) -- agents, skills, workflows, connectors
- [Platform Operations](developer/operations.md) -- running locally, health checks, troubleshooting

### Reference

- [Glossary](glossary.md) -- definitions of all key terms
- [Design Decisions](design-decisions.md) -- the "why" behind major architectural choices
- [Roadmap](roadmap.md) -- phased implementation plan
- [Architecture Plan](SpringVoyage-v2-plan.md) -- the full technical specification
