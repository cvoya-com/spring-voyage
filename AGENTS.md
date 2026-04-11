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
