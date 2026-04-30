# Roadmap

> **Role:** this directory publishes **forward-looking narrative** about Spring Voyage — what we're thinking about, why, and where the platform is headed. **It is not a progress tracker.** Progress lives in GitHub.

Spring Voyage is an open-source collaboration platform for teams of AI agents — and the humans they work with. The roadmap items below describe how the substrate evolves over time; orchestration capabilities are listed as the mechanism that supports collaboration, not as the platform's headline.

## Where to see live progress

| Surface | Use it for |
| --- | --- |
| **V2 milestone** ([#1](https://github.com/cvoya-com/spring-voyage/milestone/1)) | Auto-computed progress bar for the V2 release |
| **V2.1 milestone** ([#2](https://github.com/cvoya-com/spring-voyage/milestone/2)) | Progress bar for V2.1 (follow-ups shipping after V2) |
| **V2 umbrella** ([#418](https://github.com/cvoya-com/spring-voyage/issues/418)) | V2 narrative + sub-issue hierarchy |
| **V2.1 umbrella** ([#582](https://github.com/cvoya-com/spring-voyage/issues/582)) | V2.1 narrative + sub-issue hierarchy |
| `gh issue list --label backlog` | Candidates for future consideration (not scheduled) |
| `gh issue list --label needs-thinking` | Architectural / product decisions pending |
| `gh issue list --label ambient` | Housekeeping, upstream trackers, retrospective docs |

## Historical phase snapshots

The phase documents below are **historical**: they record how V2 was originally planned and decomposed. Tick states are intentionally not maintained here anymore — look at the milestones and umbrellas for current status.

| Phase | Name | Details |
| --- | --- | --- |
| — | [Foundation](foundation.md) | Documentation, UX exploration, test infrastructure |
| 1 | [Platform Foundation + Software Engineering](phase-1.md) | Core actors, messaging, orchestration, GitHub connector, CLI |
| 2 | [Observability + Multi-Human](phase-2.md) | Activity events, cost tracking, cloning, RBAC, web dashboard |
| 3 | [Initiative + Product Management](phase-3.md) | Initiative levels, tiered cognition, second domain |
| 4 | [A2A + Strategies + Runtime + Portal UX](phase-4.md) | Cross-framework interop, orchestration spectrum, portal |
| 5 | [Unit Nesting + Directory + Boundaries](phase-5.md) | Hierarchical organization, expertise aggregation, boundary opacity |
| 6 | [Platform Maturity](phase-6.md) | Package system, additional domain packages |

## Open Core Model

Spring Voyage follows an open core model. This repository contains the complete, fully functional platform: agents, messaging, routing, orchestration strategies, execution, connectors, CLI, basic RBAC, ephemeral cloning, observability, basic cost tracking, A2A protocol, unit nesting, package system, and dashboard.

Commercial extensions (multi-tenancy, OAuth/SSO/SAML, billing, and advanced features) are developed separately and extend this codebase via dependency injection without modifying it.

### License

Spring Voyage is licensed under the Business Source License 1.1 (BSL 1.1). On the Change Date (2030-04-10), the license converts to the Apache License, Version 2.0.

### Extension Model

The platform is designed for extensibility via DI. All core abstractions are defined as interfaces in `Cvoya.Spring.Core` and implemented in `Cvoya.Spring.Dapr`. External extensions can override default implementations by registering their own services after the default registrations. The OSS codebase does not reference tenant concepts.

## Future work — beyond V2

Forward-looking capabilities the architecture is designed to accommodate. Interfaces and extension points are in place; see [Open Questions](../architecture/open-questions.md) for details.

- **Dynamic agent and unit creation** — agents and units created programmatically at runtime: workload scaling, specialist spawning, ad-hoc units, emergent structure.
- **Advanced self-organization** — agents negotiating task allocation, forming ad-hoc sub-units, and reorganizing unit structure based on workload patterns.
- **Alwyse: cognitive backbone** — optional observer agent providing cognitive memory, pattern recognition, expertise evolution, and sub-agent spawning.
- **A2A as the agent-facing wire protocol** — evolve `A2AExecutionDispatcher` into a governance-mediated gateway so agents speak A2A outward for all messaging (see [#539](https://github.com/cvoya-com/spring-voyage/issues/539)).
- **Peer orchestration** — agents self-select and coordinate without a central orchestrator (see [#407](https://github.com/cvoya-com/spring-voyage/issues/407)).
- **External workflow engine integration** — ADK, LangGraph, and similar engines as orchestrators over A2A (see [#408](https://github.com/cvoya-com/spring-voyage/issues/408)).

This list is not a commitment — it's where our thinking is pointing. Items graduate into milestones when we decide to commit.
