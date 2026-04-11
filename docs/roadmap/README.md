# Roadmap

Spring Voyage V2 is developed in six phases. Each phase delivers a complete, usable increment. Later phases build on earlier ones but don't invalidate them.

For architectural details, see [`docs/architecture/`](../architecture/README.md).

| Phase | Name | Status | Details |
| --- | --- | --- | --- |
| 1 | [Platform Foundation + Software Engineering](phase-1.md) | Complete | Core actors, messaging, orchestration, GitHub connector, CLI |
| 2 | [Observability + Multi-Human](phase-2.md) | In progress | Activity events, cost tracking, cloning, RBAC, web dashboard (2 items remaining) |
| 3 | [Initiative + Product Management](phase-3.md) | Not started | Initiative levels, tiered cognition, second domain |
| 4 | [A2A + Additional Strategies](phase-4.md) | Not started | Cross-framework interop, rule-based/peer strategies |
| 5 | [Unit Nesting + Directory + Boundaries](phase-5.md) | Not started | Recursive composition, expertise directory, boundaries |
| 6 | [Platform Maturity](phase-6.md) | Not started | Package system, additional domain packages |

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
