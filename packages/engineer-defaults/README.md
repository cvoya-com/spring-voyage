# engineer-defaults

A skill bundle of guardrails for agents whose runtime is shell-heavy — engineers that build, test, and lint from a command line. It layers on top of the always-available platform tools.

It covers the shell-tooling footguns that matter when several threads share one agent process:

- Don't launch long-running watchers or dev servers that never exit on their own (`pytest --watch`, `npm run dev`, `cargo watch`, `dotnet watch run`, `tail -f`, …) — run builds, tests, and lint as one-shot commands.
- Don't use broad process kills (`pkill -f`, `killall`) that would also match other threads' child processes; kill a specific child by PID.
- Don't hand-roll a long-lived background service across turns; carry state through the runtime's session-resume instead.

The general platform guardrails are universal; this bundle adds the engineer-specific layer. Equip it on engineer-shaped agents and leave it off PM, analyst, and router agents that don't run shell tooling.

## Coordinates

| Field | Value |
| --- | --- |
| Package | `engineer-defaults` (canonical `spring-voyage/engineer-defaults`) |
| Skill | `engineer-defaults` |
| Display name | `sv.engineer.defaults` |
| Category | platform |
