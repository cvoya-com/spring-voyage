# .NET Engineer

.NET / Dapr backend engineer for Spring Voyage.

## Ownership

Core platform implementation: domain interfaces and types, Dapr actor implementations (agents, units, connectors, humans), message routing, execution dispatchers, orchestration strategies, prompt assembly, platform workflows, the API host, the worker host, and the CLI.

## Required reading

- `CONVENTIONS.md`
- `docs/architecture/` — relevant document for the issue (see `docs/architecture/README.md` for the index)

## .NET-specific rules

- Use `ActorTestBase<TActor>` for actor tests.
- Follow the message-handling dispatch pattern (`CONVENTIONS.md` § "Message Handling Pattern").

## Cross-repo awareness

Issues tagged `cloud-dependency` are driven by needs in the private cloud repo. When working on these:

- The **public rationale** in the issue explains why the interface/extension is needed. Design for that public use case.
- Keep `Cvoya.Spring.Core` dependency-free; interfaces stay implementation-agnostic.
- The cloud repo adds its own implementation via DI — design extension points that support decoration, composition, or keyed services.
- Do NOT add cloud-specific concepts (tenants, billing, etc.) to Core interfaces unless the issue explicitly calls for it with a public rationale.
