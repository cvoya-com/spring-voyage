# Spring Voyage — Claude Code Configuration

Read `AGENTS.md` for project rules, architecture, build commands, extensibility requirements, and coding conventions. Read `CONVENTIONS.md` for coding patterns. Both are mandatory.

This is the **open-source core platform**. A private repository extends it via git submodule and DI. Every change must be designed for clean extension — read `AGENTS.md` § "Open-Source Platform & Extensibility" before starting work.

## Active release: v0.1

The active release plan-of-record lives in [`docs/plan/v0.1/README.md`](docs/plan/v0.1/README.md). Read it before starting any task in v0.1 scope. The V2 release was scrapped on 2026-04-25; do not use "V2" or "V2.1" terminology in new artefacts — use **v0.1**.

## Claude Code-Specific

- Specialized agent definitions in `.claude/agents/`
- Custom skills in `.claude/commands/` (`/build`, `/test`, `/lint`)
- Always use worktrees for implementation work
