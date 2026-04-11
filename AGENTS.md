# Spring Voyage V2

AI agent orchestration platform — general-purpose, domain-agnostic. Built on .NET 10 and Dapr. Namespace: `Cvoya.Spring.*`.

## Coding Conventions

Read `CONVENTIONS.md` for all coding patterns, naming, testing, DI, Dapr usage, and error handling conventions. Everything in `CONVENTIONS.md` is mandatory.

## Architecture

The architecture plan at `docs/SpringVoyage-v2-plan.md` is the source of truth for all design decisions. Read the relevant sections before starting work. Key concepts:

- **Agents** are Dapr virtual actors (`AgentActor`) with partitioned mailboxes
- **Units** are composite agents (`UnitActor`) with pluggable orchestration strategies
- **Connectors** bridge external systems (GitHub, Slack, etc.) to units
- **Messages** are typed communications between addressable entities
- Execution patterns: **hosted** (in-process LLM) and **delegated** (container with tool like Claude Code)
- Four-layer prompt assembly: platform, unit context, conversation context, agent instructions

## Build & Test

```bash
dotnet build                       # build all projects
dotnet test                        # run all tests
dotnet format --verify-no-changes  # check formatting
```

## Open-Source Platform & Extensibility

This is the **public, open-source core** of the Spring Voyage platform. A private repository (Spring Voyage Cloud) extends this codebase via git submodule and dependency injection to add multi-tenancy, OAuth/SSO, billing, and premium features.

**Every design decision in this repo must account for extensibility.** The private repo should be able to extend, override, or compose OSS behavior cleanly — without forking, patching, or working around limitations. Think of this repo as a framework that the private repo consumes.

### Extension Model

The private repo extends the OSS platform through dependency injection:

- **Tenant-scoped wrappers** around OSS repositories and services (the OSS codebase has no concept of tenants or `TenantId`)
- **DI overrides** — the cloud host replaces OSS service registrations with tenant-aware implementations
- **Additional actors, strategies, and connectors** that compose OSS building blocks
- **Cloud API host** that layers middleware (auth, tenant context) on top of the OSS API host

### Design Principles for Extensibility

1. **Interface-first, always.** Define interfaces in `Cvoya.Spring.Core`, implement in `Cvoya.Spring.Dapr`. The private repo can provide alternative implementations without touching OSS code.
2. **Use `TryAdd*` for DI registrations.** Use `TryAddSingleton`, `TryAddScoped`, etc. so the private repo can register its own implementations before calling `AddCvoyaSpring*()`, and OSS registrations won't overwrite them. For keyed services, check before registering.
3. **Don't seal extensible types.** Classes that represent extension points (services, handlers, strategies, middleware) should not be `sealed` unless there is a specific reason. Mark them `sealed` only for leaf types that are not designed for inheritance.
4. **Favor composition over inheritance.** Prefer injecting collaborators over deep class hierarchies. The private repo extends behavior by wrapping or decorating OSS services, not by subclassing.
5. **No hardcoded assumptions about single-tenancy.** Don't embed assumptions like "there's one user" or "one set of config." Use injected services for anything that the private repo might scope per-tenant (repositories, configuration, policies).
6. **Virtual methods on base classes.** When providing base classes (e.g., `ConnectorBase`, `ActorBase`), make hook/template methods `virtual` so the private repo can override behavior.
7. **Keep `Cvoya.Spring.Core` dependency-free.** It defines the domain contract. The private repo depends on these abstractions directly without pulling in infrastructure packages.
8. **Extension point checklist.** When adding a new feature, ask:
   - Can the private repo swap this implementation via DI? → Use an interface.
   - Can the private repo extend this behavior? → Use decorator/wrapper pattern or virtual methods.
   - Does this assume a single deployment context? → Parameterize via injected configuration/services.

### What NOT to Do

- **Don't reference tenant concepts.** No `TenantId`, no multi-tenancy awareness. The private repo layers that on.
- **Don't make services static or use singletons outside DI.** Everything must go through the container so the private repo can control lifetime and scoping.
- **Don't create internal types that the private repo would need to access.** If a type is part of the extension contract, make it `public`. Use `internal` only for true implementation details.

## Key Rules

- `Cvoya.Spring.Core` must have ZERO external NuGet package references. It defines domain abstractions only.
- System.Text.Json only. No Newtonsoft.Json.
- .NET 10 target framework.
- Interface-first: define interfaces in `Cvoya.Spring.Core`, implement in `Cvoya.Spring.Dapr`.
- Always create PRs against `main`. Never push directly.
- After creating a PR, always enable auto-merge with `gh pr merge <number> --auto --squash`.
- Run `dotnet format` before committing.
- Reference GitHub issues in commit messages with `Closes #N`.

## Concurrent Agents

Multiple agents work on v2 simultaneously. Rules:
- Always use worktree isolation.
- Small, focused PRs — one issue per PR.
- Rebase onto `main` before merging.
- When adding to shared files (`StateKeys`, DI registrations, enums) — append to the end.
