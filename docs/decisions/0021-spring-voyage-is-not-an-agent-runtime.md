# 0021 — Spring Voyage is not an agent runtime

- **Status:** Accepted — the platform coordinates external agent runtimes (Claude Code, Codex, Gemini CLI, dapr-agent, …) running in containers; it does not implement its own multi-turn tool-use loop. The legacy `Hosted` execution mode was removed in [#118](https://github.com/cvoya-com/spring-voyage/issues/118).
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Core/AgentRuntimes/`, `src/Cvoya.Spring.AgentRuntimes.*`, `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Dispatcher/`.
- **Related docs:** [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md), [`docs/architecture/units.md`](../architecture/units.md), [`docs/architecture/agent-runtimes-and-tenant-scoping.md`](../architecture/agent-runtimes-and-tenant-scoping.md).

## Context

Earlier v2 drafts split execution into two modes: `Delegated` (the agent runs in a container with an external tool like Claude Code) and `Hosted` (the platform calls the LLM's Messages API directly and runs the tool-use loop in-process). The hosted mode was attractive in principle — no container start cost, full visibility into the loop — but it forced the platform to maintain a parallel implementation of work that the upstream agent tools were already doing better.

Three concrete pressures pushed the question:

1. **Agent tools evolve fast.** Skills, slash commands, MCP integrations, permission models, streaming shapes, and session/checkpoint formats are moving targets. Every Claude Code release ships changes that a hosted loop would have to chase.
2. **MCP became the cross-tool contract.** Once Claude Code, Cursor, and several others standardised on MCP for tool surfaces, "platform skills exposed as MCP" became the universal way to reach every agent runtime — no per-tool work needed.
3. **Lightweight LLM calls are different.** Routing decisions ([`AiOrchestrationStrategy`](../architecture/units.md)), classification, summarisation, the Tier 1 screener ([ADR 0020](0020-tiered-cognition-for-initiative.md)) — none of those need an agent loop. They want a single completion call.

## Decision

**Spring Voyage coordinates external agent runtimes; it does not implement an in-platform tool-use loop. Every agent dispatch goes through the `A2AExecutionDispatcher` → `IAgentToolLauncher` → container path; the container holds the tool-use loop. Lightweight non-agentic LLM calls remain first-class through `IAiProvider.CompleteAsync` / `StreamCompleteAsync`; they do not need an agent loop.**

- **Don't reimplement what already exists.** Claude Code, Codex, Gemini CLI, and dapr-agent already do planning, tool use, permissioning, streaming, session management, and checkpointing. A hosted loop would be a worse, slower-evolving copy.
- **Stable platform, churning ecosystem.** Letting external tools own the per-tool details (auth.json layout, `--resume` handshake, MCP bridge specifics) keeps `Cvoya.Spring.Core` interfaces stable while the ecosystem reshapes itself.
- **MCP as the single integration seam.** Platform skills (GitHub ops, directory queries, A2A calls back into the platform) reach every agent through one MCP server the container consumes. We ship one implementation; every MCP-speaking tool gets every skill.
- **Lightweight calls stay lightweight.** `IAiProvider.CompleteAsync` is a single completion API for routing decisions, classification, and summarisation. It never goes near the agent dispatch path.

## Alternatives considered

- **Hosted execution alongside delegation.** What we removed in [#118](https://github.com/cvoya-com/spring-voyage/issues/118). Cost: a parallel implementation of every agent tool's behaviour, perpetually behind upstream. Benefit: faster start time per turn. The benefit did not justify the cost.
- **Custom in-process loop using Anthropic / OpenAI Messages API directly.** Identical trade-off — we'd be re-implementing planning, tool routing, permissioning, and resumption against a target whose behaviour the upstream owns.

## Consequences

- **Container start cost on every agent turn (ephemeral path).** Mitigated by the persistent-agent registry ([ADR 0011](0011-persistent-agent-lifecycle-http-surface.md)) for workloads where start cost dominates.
- **Adding a new agent runtime is a thin adapter.** `IAgentRuntime` + `IAgentToolLauncher` + a per-runtime project under `src/Cvoya.Spring.AgentRuntimes.*`; no platform changes. See [`docs/architecture/agent-runtimes-and-tenant-scoping.md`](../architecture/agent-runtimes-and-tenant-scoping.md) for the contract.
- **Two LLM-call surfaces, intentionally distinct.** `IAgentRuntime` (full agent dispatch) for tool-use work; `IAiProvider` for stateless completions. Mixing them is a code-review smell.
- **Platform skills are MCP-shaped, not tool-shaped.** Anyone adding a new platform-level skill ships an MCP tool definition; every agent runtime inherits it for free.
