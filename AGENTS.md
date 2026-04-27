# Spring Voyage — Project Rules

AI agent orchestration platform — general-purpose, domain-agnostic. Built on .NET 10 and Dapr. Namespace: `Cvoya.Spring.*`.

## Coding conventions

[`CONVENTIONS.md`](CONVENTIONS.md) is mandatory. Read it before writing code.

## Architecture

Architecture lives under [`docs/architecture/`](docs/architecture/README.md); decision records under [`docs/decisions/`](docs/decisions/README.md). Read the relevant doc before working on an issue.

Key concepts:

- **Agents** — Dapr virtual actors with partitioned mailboxes.
- **Units** — composite agents with pluggable orchestration strategies.
- **Connectors** — bridges between external systems and units.
- **Messages** — typed communications between addressable entities.
- **Execution patterns** — *hosted* (in-process) and *delegated* (containerised tool execution).
- **Prompt assembly** — four-layer composition: platform, unit context, conversation context, agent instructions.

## Build, test, lint

Use the `/build`, `/test`, and `/lint` skills. Each points at the canonical CI invocation.

Pitfall: bare `dotnet test SpringVoyage.slnx` exits 0 without running tests. Always go through `/test`.

## Documentation updates

When shipping a feature, update the relevant architecture or guide doc in the same PR. A PR that changes user-visible behaviour or architecture without touching the corresponding docs is not complete. New concepts get a doc entry alongside the change.

For changes under `src/Cvoya.Spring.Web/`, keep `src/Cvoya.Spring.Web/DESIGN.md` in sync — it is the portal's visual contract.

## Open-source platform and extensibility

This repository is the **public, open-source core** of Spring Voyage. A private repository extends it via git submodule and dependency injection — adding multi-tenancy, OAuth/SSO, billing, and premium features.

- **Don't bypass `ITenantContext`.** Resolve the current tenant through `ITenantContext.CurrentTenantId`; never hardcode `"default"` or assume only one tenant exists. New persisted entities that should be tenant-scoped must implement `ITenantScopedEntity` so the cloud host can enforce isolation through its scoped overrides.
- **Don't make services static or use singletons outside DI.** Everything must go through the container so the private repo can control lifetime and scoping.
- **Don't create internal types that the private repo would need to access.** If a type is part of the extension contract, make it `public`. Use `internal` only for true implementation details.
- **Don't reference private repo issues, PRs, or branches.** It is fine to acknowledge that a Spring Voyage hosted service exists, but do not link to or create dependencies on `cvoya-com/spring` issues or PRs from this repo. The dependency direction is one-way: the private repo may reference this repo's work, not the reverse.
- **Every design decision must account for extensibility.** The private repo extends, overrides, or composes OSS behaviour cleanly — without forking, patching, or working around limitations. Treat this repo as a framework that consumers use.

### Extension model

- **Tenant-aware overrides.** The OSS core models tenancy through `ITenantContext` and ships a single-tenant default. The cloud host swaps in a scoped implementation. OSS code must not assume a single tenant or hardcode the default tenant id.
- **DI overrides.** The cloud host replaces OSS service registrations with tenant-aware implementations using `TryAdd*`-friendly registration on the OSS side.
- **Plugin contracts.** Implement `IAgentRuntime` (LLM backend + execution tool + credential schema + model catalogue) or `IConnectorType` (external-system binding) and register via DI; the host picks new implementations up without core changes.
- **Cloud API host.** Layers middleware (auth, tenant context) on top of the OSS API host.

### Design principles for extensibility

1. **Interface-first.** Define interfaces in `Cvoya.Spring.Core`, implement in `Cvoya.Spring.Dapr`. Alternative implementations slot in via DI.
2. **`TryAdd*` for DI registrations.** Downstream consumers register their own implementations before calling `AddCvoyaSpring*()`, and OSS registrations don't overwrite them.
3. **Don't seal extensible types.** Services, handlers, strategies, middleware are not `sealed` unless leaf-only.
4. **Composition over inheritance.** Inject collaborators; extend by wrapping or decorating.
5. **No hardcoded single-tenant assumptions.** Use injected services for anything the cloud might scope per tenant.
6. **Virtual hooks on base classes.** Make template methods on `*Base` classes `virtual`.
7. **`Cvoya.Spring.Core` stays dependency-free.** Domain abstractions only — zero NuGet packages.
8. **Extension-point checklist** for new features:
   - Can the cloud swap this implementation via DI? → interface.
   - Can it extend behaviour? → decorator/wrapper or virtual methods.
   - Does it assume a single deployment context? → parameterise via DI.

### What not to do

- **Don't bypass `ITenantContext`.** Resolve the tenant through `ITenantContext.CurrentTenantId`. New persisted entities that should be tenant-scoped implement `ITenantScopedEntity`.
- **Don't make services static or use singletons outside DI.** Everything goes through the container.
- **Don't create internal types that the cloud overlay would need access to.** If a type is part of the extension contract, it is `public`.

### Plugins (agent runtimes and connectors)

Agent runtimes and connectors are first-class plugins. Each ships as its own `Cvoya.Spring.AgentRuntimes.<Name>` or `Cvoya.Spring.Connector.<Name>` project, references only what its contract demands, and registers via a single `AddCvoyaSpring<Kind><Name>()` DI extension. Host-side code references the abstraction only — the registry, install surface, and bootstrap pick up new plugins automatically. Per-project READMEs document each runtime/connector's contract; see also `CONVENTIONS.md` § "Agent Runtimes and Connectors Are Plugins".

## Operator surfaces — relaxation of UI/CLI parity

Operational surfaces (agent-runtime config, connector config, credential health, tenant seeds, skill-bundle bindings) are **CLI-only by design**. The portal MAY expose **read-only** views for visibility, but every mutation goes through the `spring` CLI.

User-facing features remain strictly parity-bound — see [`CONVENTIONS.md`](CONVENTIONS.md) § "UI / CLI Feature Parity".

## Agents, Sub-agents, concurrent agents

Multiple coding agents work on this codebase simultaneously.

Rules:
- Each agent must operate in its own dedicated worktree — never share a worktree between agents.
- Small, focused PRs — one issue per PR unless instructed to combine issues into one.
- Rebase onto `main` before merging.
- When adding to shared files (`StateKeys`, DI registrations, enums) — append to the end.
- File follow-up issues before the PR lands and reference concrete numbers in the PR body. Prose-only "we'll file it later" routinely drops follow-ups on the floor.

## Repository Configuration

- `.claude/settings.local.json` is gitignored and is the correct place for user-specific tooling (MCP servers, design tools, personal preferences). Do not add user-specific tool configuration to committed repo files (`settings.json`, agent definitions, or CLAUDE.md). Repo-level config should reflect project requirements shared by all contributors, not individual workflow preferences.
