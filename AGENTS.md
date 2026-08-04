# Spring Voyage — project instructions

Spring Voyage is a source-available, domain-agnostic collaboration platform for teams of AI agents and humans. It uses .NET 10 and Dapr under the `Cvoya.Spring.*` namespace. Read [CONVENTIONS.md](CONVENTIONS.md) before changing code.

The as-built architecture is under [docs/architecture/](docs/architecture/README.md), with accepted decisions under [docs/decisions/](docs/decisions/README.md). Read the relevant pages before working in an area.

## Platform invariants

- Agents are Dapr virtual actors with durable per-thread mailboxes.
- A unit is a composite agent; unit routing is runtime behavior, not platform configuration. See [units vs agents](docs/concepts/units-vs-agents.md).
- Humans are addressable thread participants, not agents; see [humans](docs/concepts/humans.md).
- Connectors bridge external systems to units but are not routable subjects.
- Messages are typed, one-way communications on durable threads.
- The platform coordinates external agent runtimes in ephemeral or persistent containers; it does not host its own tool loop.
- Prompt assembly has platform, unit, thread, and agent layers.

## Build and verification

Use the repository `build`, `test`, `lint`, and `format` skills. Bare `dotnet test SpringVoyage.slnx` can exit successfully without running tests, so never use it as evidence.

Install `eng/install-hooks.sh` once per clone. The pre-push hook runs the change-scoped static gate in `eng/ci/ci-local.sh`; hosted CI runs the full build, lint, and test gates. During iteration use focused checks, but API/contract changes still require the solution-wide test gate because integration failures often surface first. A subagent's unqualified “tests pass” means the complete repository test gate passed.

Generated-file and migration rules live in [generated files](docs/agent-rules/generated-files.md) and [database migrations](docs/agent-rules/database-migrations.md). Claude loads their matching shims lazily from `.claude/rules/`.

## Source-available extension boundary

The public repository is the core platform. A private host extends it through dependency injection with tenancy, identity, billing, and premium behavior. Preserve that one-way boundary:

- Resolve tenancy through `ITenantContext.CurrentTenantId`; never hardcode `"default"`. Fresh OSS rows use `OssTenantIds.Default`.
- Tenant-owned persisted entities implement `ITenantScopedEntity`.
- Services use DI, not static state or ad-hoc singletons. Register replaceable defaults with `TryAdd*`.
- Extension contracts are public, interface-first, and composable; do not seal intended extension points.
- `Cvoya.Spring.Core` remains dependency-free.
- Do not reference private-repository issues, branches, or PRs.
- New features must remain replaceable or decoratable by a downstream host without forks or patches.

Agent runtimes implement `IAgentRuntime`; connectors implement `IConnectorType`. Each ships in its own `Cvoya.Spring.AgentRuntimes.<Name>` or `Cvoya.Spring.Connector.<Name>` project and registers through one DI extension.

Operational mutations for runtime, connector, credential, tenant-seed, and skill-bundle administration are CLI-only. The portal may expose read-only visibility. User-facing features still follow the UI/CLI parity rule in [CONVENTIONS.md](CONVENTIONS.md).

## Documentation

Architecture pages are living as-built truth. A design-affecting change updates the relevant page and diagrams in the same PR. Feature changes update the relevant guide; new concepts get a `docs/concepts/` entry; decisions get an ADR. Web changes keep `src/Cvoya.Spring.Web/DESIGN.md` synchronized. The concise cross-agent checklist is [documentation](docs/agent-rules/documentation.md).

Release plans live under `docs/plan/<release>/README.md`; use the active release plan as the plan of record.

## Workflow

- Work only in a dedicated task worktree under `~/dev/worktrees/spring-voyage/<task>`, based on current `origin/main`; never edit the main checkout.
- One PR owns one coherent outcome. Combine issues only when instructed or when they are inseparable within one owned surface and verification boundary.
- Rebase on current `origin/main` before pushing and before merging.
- Append to shared registries such as `StateKeys`, DI registrations, and enums; avoid reorder churn.
- File and natively wire genuinely separate follow-ups before the PR lands.

All GitHub writes for this repository, including commits, use `gh-app`. PRs are review-ready by default, use squash merge, and repeat the closing keyword for every completed issue.

User-specific tools and MCP servers belong in `.claude/settings.local.json` or user configuration, not committed project settings, agent definitions, or this file.
