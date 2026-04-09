# Spring Voyage V2

AI agent orchestration platform — general-purpose, domain-agnostic. Built on .NET 10 and Dapr. Namespace: `Cvoya.Spring.*`.

## Coding Conventions

Read `CONVENTIONS.md` in this directory for all coding patterns, naming, testing, DI, Dapr usage, and error handling conventions. Everything in `CONVENTIONS.md` is mandatory for all agents.

## Architecture

The architecture plan at `v2/docs/SpringVoyage-v2-plan.md` is the source of truth for all design decisions. Key concepts:

- **Agents** are Dapr virtual actors (`AgentActor`) with partitioned mailboxes
- **Units** are composite agents (`UnitActor`) with pluggable orchestration strategies
- **Connectors** bridge external systems (GitHub, Slack, etc.) to units
- **Messages** are typed communications between addressable entities
- Execution patterns: **hosted** (in-process LLM) and **delegated** (container with tool like Claude Code)
- Four-layer prompt assembly: platform, unit context, conversation context, agent instructions

## Build & Test

```bash
cd v2 && dotnet build                       # build all projects
cd v2 && dotnet test                        # run all tests
cd v2 && dotnet format --verify-no-changes  # check formatting
```

## Key Rules

- All v2 code lives under `/v2`.
- `Cvoya.Spring.Core` must have ZERO external NuGet package references.
- System.Text.Json only. No Newtonsoft.Json.
- .NET 10 target framework.
- Always create PRs against `main`. Never push directly.
- Run `dotnet format` before committing.
- Reference GitHub issues in commit messages with `Closes #N`.

## Key Folders

- `v2/src/Cvoya.Spring.Core/` — Domain interfaces and types (no Dapr dependency)
- `v2/src/Cvoya.Spring.Dapr/` — Dapr implementations (actors, routing, execution, orchestration)
- `v2/src/Cvoya.Spring.Connector.GitHub/` — GitHub connector (C#, Octokit.net)
- `v2/src/Cvoya.Spring.Host.Api/` — ASP.NET Core Web API host
- `v2/src/Cvoya.Spring.Host.Worker/` — Headless worker host (Dapr actor runtime)
- `v2/src/Cvoya.Spring.Cli/` — CLI ("spring" command, System.CommandLine)
- `v2/packages/software-engineering/` — Domain package (agent templates, skills, workflows)
- `v2/dapr/` — Dapr component YAML
- `v2/tests/` — xUnit test projects

## Agent Roles

| Agent | Role |
|-------|------|
| **dotnet-engineer** | .NET/Dapr backend: actors, messaging, execution, orchestration |
| **connector-engineer** | Connector implementations (GitHub, future connectors) |
| **devops-engineer** | CI/CD, Docker, Dapr config, deployment |
| **qa-engineer** | Testing: unit, integration, e2e with Dapr test host |

## Concurrent Agents

Multiple agents work on v2 simultaneously. Rules:
- Always use worktree isolation.
- Small, focused PRs — one issue per PR.
- Rebase onto `main` before merging.
- When adding to shared files (`StateKeys`, DI registrations, enums) — append to the end.
- Interface-first: define in `Cvoya.Spring.Core`, implement in `Cvoya.Spring.Dapr`.

## Evolving Agent Definitions

Agent definitions in `.claude/agents/`, `AGENTS.md`, and `.cursor/rules/` should stay in sync with the codebase as it evolves. To keep them current:

1. **CI lint check:** A GitHub Actions step scans agent definition files against the actual project structure. If an agent references a folder, namespace, or pattern that no longer exists, CI flags it as a warning. See `.github/workflows/v2-ci.yml` for the `agent-definitions-lint` job.

2. **Post-merge hook:** After a PR merges that adds or renames projects/folders under `v2/src/` or `v2/tests/`, a scheduled workflow opens a follow-up issue to update agent definitions. This prevents silent drift.

3. **Manual review cadence:** At each phase boundary (see architecture plan), review agent ownership tables and update role scopes. New projects get assigned to the appropriate agent; deprecated projects get removed.

4. **Single source of truth:** `CONVENTIONS.md` is the canonical reference for coding patterns. Agent definitions reference it but do not duplicate its content. When conventions change, update `CONVENTIONS.md` — agent definitions inherit the change automatically.
