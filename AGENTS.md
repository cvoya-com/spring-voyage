# Spring Voyage V2

AI agent orchestration platform — general-purpose, domain-agnostic. Built on .NET 10 and Dapr. Namespace: `Cvoya.Spring.*`.

## Coding Conventions

Read `CONVENTIONS.md` for all coding patterns, naming, testing, DI, Dapr usage, and error handling conventions. Everything in `CONVENTIONS.md` is mandatory.

### Documentation Updates

When shipping a feature, update the relevant architecture doc(s) under `docs/architecture/` and user guide(s) under `docs/guide/` in the same PR so the docs never lag behind the code. If the feature introduces a new concept, add or update the relevant concept doc under `docs/concepts/`. Treat these updates as part of the feature — a PR that changes user-visible behavior or architecture without touching the corresponding docs is not complete.

When a PR touches `src/Cvoya.Spring.Web/`, it must also keep [`src/Cvoya.Spring.Web/DESIGN.md`](src/Cvoya.Spring.Web/DESIGN.md) in sync. `DESIGN.md` is the portal's visual contract (color palette, typography, spacing, radii, shadows, component patterns, voice & tone, dark-mode behavior) — update it in the same PR whenever the change introduces, modifies, or removes a visual pattern. Leaving the design doc stale is the same kind of drift as leaving architecture docs stale.

## Architecture

The architecture is documented under `docs/architecture/` — see [`docs/architecture/README.md`](docs/architecture/README.md) for the full index. For execution status and phased implementation plan, see [`docs/roadmap/`](docs/roadmap/README.md).

Before working on an issue, read the relevant architecture document(s). Key concepts:

- **Agents** are Dapr virtual actors (`AgentActor`) with partitioned mailboxes — see [Units & Agents](docs/architecture/units.md)
- **Units** are composite agents (`UnitActor`) with pluggable orchestration strategies — see [Units & Agents](docs/architecture/units.md)
- **Connectors** bridge external systems (GitHub, Slack, etc.) to units — see [Connectors](docs/architecture/connectors.md)
- **Messages** are typed communications between addressable entities — see [Messaging](docs/architecture/messaging.md)
- Execution patterns: **hosted** (in-process LLM) and **delegated** (container with tool like Claude Code) — see [Units & Agents](docs/architecture/units.md)
- Four-layer prompt assembly: platform, unit context, conversation context, agent instructions — see [Units & Agents](docs/architecture/units.md)
- **Infrastructure**: Dapr building blocks, IAddressable, data persistence — see [Infrastructure](docs/architecture/infrastructure.md)

## Build & Test

Use the `/build`, `/test`, and `/lint` skills (defined in `.claude/commands/`). Each points at the canonical CI invocation for its step.

Pitfall: bare `dotnet test` or `dotnet test SpringVoyage.slnx` exits 0 without running tests — always use `/test` (or the full invocation it documents).

## Open-Source Platform & Extensibility

This is the **public, open-source core** of the Spring Voyage platform. A private repository (Spring Voyage Cloud) extends this codebase via git submodule and dependency injection to add multi-tenancy, OAuth/SSO, billing, and premium features.

**Every design decision in this repo must account for extensibility.** The private repo should be able to extend, override, or compose OSS behavior cleanly — without forking, patching, or working around limitations. Think of this repo as a framework that the private repo consumes.

### Extension Model

The private repo extends the OSS platform through dependency injection:

- **Tenant-scoped wrappers** around OSS repositories and services (the OSS codebase has no concept of tenants or `TenantId`)
- **DI overrides** — the cloud host replaces OSS service registrations with tenant-aware implementations
- **Additional actors, strategies, and connectors** that compose OSS building blocks
- **Plugin contracts** — implement `IAgentRuntime` (LLM backend + execution tool + credential schema + model catalog) or `IConnectorType` (external-system binding) and register with `TryAdd*`; the host picks new implementations up via DI without any core code change. Each agent runtime ships as its own `Cvoya.Spring.AgentRuntimes.<Name>` project that references `Cvoya.Spring.Core` only, exposes a single `AddCvoyaSpringAgentRuntime<Name>()` DI extension, and bundles a seed catalogue at `agent-runtimes/<id>/seed.json` (see the built-in runtimes table below)
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

### Built-in agent runtimes

The OSS core ships per-runtime `IAgentRuntime` plugins as sibling projects under `src/Cvoya.Spring.AgentRuntimes.*`. Each one is wired into the host via its own `AddCvoyaSpringAgentRuntime<Name>()` extension and the `IAgentRuntimeRegistry` (in `Cvoya.Spring.Dapr`) picks them up automatically.

| Runtime id | Project | Tool kind | DI extension |
|------------|---------|-----------|--------------|
| `claude` | `Cvoya.Spring.AgentRuntimes.Claude` | `claude-code-cli` | `AddCvoyaSpringAgentRuntimeClaude()` |
| `google` | `Cvoya.Spring.AgentRuntimes.Google` | `dapr-agent` | `AddCvoyaSpringAgentRuntimeGoogle()` |
| `ollama` | `Cvoya.Spring.AgentRuntimes.Ollama` | `dapr-agent` | `AddCvoyaSpringAgentRuntimeOllama()` |
| `openai` | `Cvoya.Spring.AgentRuntimes.OpenAI` | `dapr-agent` | `AddCvoyaSpringAgentRuntimeOpenAI()` |

To add a new runtime, follow the contract in [`src/Cvoya.Spring.Core/AgentRuntimes/README.md`](src/Cvoya.Spring.Core/AgentRuntimes/README.md) and append a row above. Per-runtime READMEs live next to their projects.

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
