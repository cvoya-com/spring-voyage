# Roadmap

Spring Voyage V2 is developed in six phases plus a parallel Foundation track. Each phase delivers a complete, usable increment. Later phases build on earlier ones but don't invalidate them.

For architectural details, see [`docs/architecture/`](../architecture/README.md). For the full tracking issue, see [#418](https://github.com/cvoya-com/spring-voyage/issues/418).

| Phase | Name | Status | Details |
| --- | --- | --- | --- |
| — | [Foundation](foundation.md) | In progress | Documentation, UX exploration, test infrastructure |
| 1 | [Platform Foundation + Software Engineering](phase-1.md) | Complete | Core actors, messaging, orchestration, GitHub connector, CLI |
| 2 | [Observability + Multi-Human](phase-2.md) | Complete | Activity events, cost tracking, cloning, RBAC, web dashboard |
| 3 | [Initiative + Product Management](phase-3.md) | Complete | Initiative levels, tiered cognition, second domain |
| 4 | [A2A + Strategies + Runtime + Portal UX](phase-4.md) | Partially complete | A2A shipped; orchestration strategies, runtime, portal UX remaining |
| 5 | [Unit Nesting + Directory + Boundaries](phase-5.md) | Partially complete | Nesting + M:N shipped; boundaries, expertise remaining |
| 6 | [Platform Maturity](phase-6.md) | Not started | Package system, additional domain packages |

## Work Beyond Original Phasing

Significant work shipped that the original roadmap didn't anticipate:

- **Multi-AI agent runtime** — Claude Code, Codex, Gemini, Ollama, Dapr Agents, custom A2A agents (#334, #346-#361)
- **Persistent agent hosting** — long-lived agent containers with Dapr sidecars (#349, #361)
- **E2E test harness** — shell-based CLI scenarios against a live local stack (#311)
- **Policy framework** — unit-level skill, model, cost, and execution-mode policies (#162, #163, #247-#250)
- **Secrets stack** — multi-version, at-rest encryption, rotation, inheritance, audit logging (#200-#209, #253)
- **GitHub connector depth** — OAuth, Projects v2, GraphQL batching, caching, rate limiting, webhooks (#224-#242)
- **Web portal** — unit CRUD, agents tab, skills, connectors, secrets, sub-units, costs, delete, activity feed (#82-#84, #119-#126)

## Open Core Model

Spring Voyage follows an open core model. This repository contains the complete, fully functional platform: agents, messaging, routing, orchestration strategies, execution, connectors, CLI, basic RBAC, ephemeral cloning, observability, basic cost tracking, A2A protocol, unit nesting, package system, and dashboard.

Commercial extensions (multi-tenancy, OAuth/SSO/SAML, billing, and advanced features) are developed separately and extend this codebase via dependency injection without modifying it.

### License

Spring Voyage is licensed under the Business Source License 1.1 (BSL 1.1). On the Change Date (2030-04-10), the license converts to the Apache License, Version 2.0.

### Extension Model

The platform is designed for extensibility via DI. All core abstractions are defined as interfaces in `Cvoya.Spring.Core` and implemented in `Cvoya.Spring.Dapr`. External extensions can override default implementations by registering their own services after the default registrations. The OSS codebase does not reference tenant concepts.

## Future Work (Beyond Phase 6)

The architecture is designed to accommodate these capabilities. Interfaces and extension points are in place. See [Open Questions](../architecture/open-questions.md) for details.

- **Dynamic Agent and Unit Creation** — Agents and units created programmatically at runtime: workload scaling, specialist spawning, ad-hoc units, emergent structure.
- **Advanced Self-Organization** — Agents negotiating task allocation, forming ad-hoc sub-units, and reorganizing unit structure based on workload patterns.
- **Alwyse: Cognitive Backbone** — Optional observer agent providing cognitive memory, pattern recognition, expertise evolution, and sub-agent spawning.
