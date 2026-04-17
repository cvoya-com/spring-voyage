# Phase 4: A2A + Strategies + Runtime + Portal UX

> **[Roadmap Index](README.md)** | **Status: Partially complete**

Cross-framework interoperability, full orchestration strategy spectrum, multi-AI runtime, and the first wave of portal UX features. This phase also absorbs follow-up enhancements from Phase 2.

## Shipped

- [x] A2A protocol support — A2AExecutionDispatcher (#346), Dapr Agent container (#347), persistent hosting (#361), Codex/Gemini launchers (#358), model/provider UX (#367)

## Orchestration Strategies

- [ ] Label-based unit orchestration strategy (#389) — v1 parity, config-driven label→agent routing
- [ ] Peer orchestration strategy (#407) — agents self-select and coordinate without central orchestrator
- [ ] External workflow engine integration via A2A (#408) — ADK, LangGraph as orchestrators

## Runtime & Infrastructure

- [ ] Ollama-driven agent runtime (#334) — first-class local/OSS agent option
- [ ] spring-agent container image (#390) — bake Claude Code into the runtime image
- [ ] Persistent agents: CLI lifecycle commands (#396) — deploy, status, scale, logs, undeploy
- [ ] Agents automatically exposed as skills (#359) — agent-as-skill interop

## Portal UX (blocked by UX exploration #406)

- [ ] Dashboard: drill-down views (#392) — unit, agent, conversation detail pages
- [ ] Dashboard: RBAC management UI (#393) — invite, change role, remove, audit
- [ ] Dashboard: cost rollup (#394) — per-unit and per-agent cost attribution
- [ ] Agent execution config in portal (#409) — container image, Dockerfile configuration
- [ ] Conversation / chat UI (#410) — message composer, streaming responses, history
- [ ] Agent autonomy controls (#411) — initiative level, approval gates, budget visualization

## Observability Follow-ups

- [x] Complete Rx.NET activity pipeline end-to-end (#391) — full observable graph wiring

**Delivers:** Full orchestration strategy spectrum, cross-framework agent collaboration, multi-AI runtime, rich portal experience.
