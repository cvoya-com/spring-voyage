# Specifications

> **Active contract specifications — not historical.** For how the platform implements these contracts see [docs/architecture/](../architecture/README.md); for the rationale behind them see [docs/decisions/](../decisions/README.md). Each spec carries its own change log.

Implementation-neutral contract specifications for downstream implementers (SDKs, agent runtimes, integrations).

Architecture docs (`docs/architecture/`) describe the platform implementation. ADRs (`docs/decisions/`) record decisions and trade-offs. Specs (here) define contracts that external implementers conform to.

| Spec | Scope | Status |
|---|---|---|
| [Agent runtime boundary](agent-runtime-boundary.md) | SDK lifecycle hooks + IAgentContext + per-agent volume + the MCP send path | v0.2 — Accepted |
