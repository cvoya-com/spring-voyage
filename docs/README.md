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

### [Architecture](architecture/README.md)

How the concepts are realized as a running system.

- [Infrastructure](architecture/infrastructure.md) -- Dapr, IAddressable, data persistence
- [Messaging](architecture/messaging.md) -- mailbox, addressing, routing, activation
- [Units & Agents](architecture/units.md) -- agent model, cloning, orchestration strategies
- [Initiative](architecture/initiative.md) -- initiative levels, tiered cognition
- [Workflows](architecture/workflows.md) -- workflow-as-container, A2A, patterns
- [Connectors](architecture/connectors.md) -- connector model, skills, implementation tiers
- [Observability](architecture/observability.md) -- activity events, cost tracking
- [CLI & Web](architecture/cli-and-web.md) -- API surface, hosting, CLI, deployment
- [Deployment](architecture/deployment.md) -- execution modes, solution structure
- [Security](architecture/security.md) -- RBAC, authentication, resilience
- [Packages](architecture/packages.md) -- domain packages, skill format
- [Agent Runtimes & Tenant Scoping](architecture/agent-runtimes-and-tenant-scoping.md) -- V2 plugin model, tenant installs, credential-health lifecycle (#674)
- [Open Questions](architecture/open-questions.md) -- design questions, future work

### [User Guide](guide/overview.md)

How to use Spring Voyage V2 through the `spring` CLI.

- [Getting Started](guide/getting-started.md) -- first setup, first unit, first agent
- [Managing Units and Agents](guide/units-and-agents.md) -- creation, configuration, lifecycle
- [Messaging and Interaction](guide/messaging.md) -- sending messages, conversations
- [Observing Activity](guide/observing.md) -- streams, cost tracking, dashboards
- [Declarative Configuration](guide/declarative.md) -- YAML definitions and `spring apply`
- [Deployment](guide/deployment.md) -- self-hosting on Docker Compose or Podman (including the no-build / registry path)

### [Developer Guide](developer/overview.md)

For contributors to the Spring Voyage V2 platform.

- [Development Setup](developer/setup.md) -- prerequisites, building, running locally
- [Creating Packages](developer/creating-packages.md) -- agents, skills, workflows, connectors
- [Platform Operations](developer/operations.md) -- running locally, health checks, troubleshooting

### Reference

- [Glossary](glossary.md) -- definitions of all key terms
- [Design Decisions](design-decisions.md) -- the "why" behind major architectural choices
- [Decision Records](decisions/README.md) -- narrow, dated records for trade-offs that lock in a specific approach
- [Roadmap](roadmap/README.md) -- phased implementation plan
- [Architecture Plan](SpringVoyage-v2-plan.md) -- original monolithic specification (deprecated, see `architecture/`)
