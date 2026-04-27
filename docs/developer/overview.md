# Developer Guide

This guide is for developers contributing to the Spring Voyage platform itself -- the .NET infrastructure, connectors, workflows, agents, and tooling.

## Document Map

| Document | Description |
|----------|-------------|
| [Development Setup](setup.md) | Prerequisites, building, running locally |
| [Creating Packages](creating-packages.md) | Building domain packages: agents, skills, workflows, connectors |
| [Platform Operations](operations.md) | Running locally, health checks, and troubleshooting |

## Project Layout

```
SpringVoyage.sln
src/
  Spring.Core/                    # Domain interfaces and types (no Dapr dependency)
    Messaging/                    # IAddressable, IMessageReceiver, Message, Address
    Orchestration/                # IOrchestrationStrategy, IUnitContext
    Observability/                # IActivityObservable, ActivityEvent
  Spring.Dapr/                    # Dapr implementations of Core interfaces
    Actors/                       # AgentActor, UnitActor, ConnectorActor, HumanActor
    Orchestration/                # RuleBasedStrategy, WorkflowStrategy, AiStrategy, etc.
  Spring.A2A/                     # A2A protocol client and server
  Spring.Connector.GitHub/        # GitHub connector (C#)
  Spring.Connector.Slack/         # Slack connector
  Spring.Host.Api/                # API host (REST, WS, SSE, auth, multi-tenant)
  Spring.Host.Worker/             # Headless worker host
  Spring.Cli/                     # CLI ("spring" command)
  Spring.Web/                     # Web portal
python/                           # Optional Python components
  spring_agent/                   # Python agent process
  spring_connectors/              # Python-side connector logic
dapr/
  components/                     # Dapr component configs (pub/sub, state, bindings, secrets)
  configuration/                  # Access control, resiliency policies
packages/                         # Domain packages
  software-engineering/           # Phase 1 package
    agents/                       # Agent definition YAML
    units/                        # Unit definition YAML
    skills/                       # Prompt fragments + tool definitions
    workflows/                    # Workflow container sources (Dockerfiles + code)
    execution/                    # Agent execution environment sources
    connectors/                   # Compiled into host
tests/
```

## Key Design Principles

**Spring.Core has no Dapr dependency.** All interfaces and types are pure .NET. Dapr-specific implementations live in Spring.Dapr. This makes core logic testable without Dapr infrastructure.

**Actors are the concurrency boundary.** All mutable state lives inside actors. No shared mutable state between actors. State changes happen within actor turns.

**Domain workflows run in containers.** Never add domain workflows to the host process. Domain logic deploys as container images, decoupled from platform releases.

**The platform never inspects domain payloads.** Routing decisions are based on MessageType and delivery mechanism, never on payload content. Domain semantics live in packages.
