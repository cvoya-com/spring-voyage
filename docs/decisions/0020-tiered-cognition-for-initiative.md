# 0020 — Two-tier cognition model for initiative

- **Status:** Accepted — initiative uses a cheap local Tier 1 LLM to screen events; only Tier 1's "act" verdicts wake the agent's primary LLM (Tier 2).
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Core/Initiative/`, `src/Cvoya.Spring.Dapr/Initiative/Tier1CognitionProvider.cs`.
- **Related docs:** [`docs/architecture/initiative.md`](../architecture/initiative.md), [`docs/concepts/initiative.md`](../concepts/initiative.md).

## Context

Initiative is the agent's capacity to autonomously decide to act on observed events. The naïve implementation routes every event to the agent's primary LLM ("Tier 2") — Claude / GPT / Gemini — and lets the model decide whether to do anything. The arithmetic is brutal: a 5-minute polling cadence is 288 events per agent per day; multiplying that by a primary-LLM call cost of a few cents puts initiative at several dollars per agent per day before any work is done. That kills initiative as a default capability.

A rule-based screener avoids the cost but loses the ability to make context-sensitive judgements ("this comment is a follow-up question vs. this comment is a passing FYI"). Rules also encode the platform's opinion of what matters, which is exactly the kind of decision we want the agent to own.

## Decision

**Initiative runs a two-tier cognition model. A small, locally-hosted LLM (Tier 1) screens every event and emits one of three verdicts: `Ignore`, `Queue`, `Act`. Only `Act` verdicts wake the agent's primary LLM (Tier 2) for full reflection.**

- **Tier 1 — screening, ~zero marginal cost.** A small model (Phi-3, Llama 3.1 8B, or equivalent) runs on shared platform infrastructure. It evaluates each event against the agent's context summary and emits the verdict. The cost is dominated by infra availability, not per-call price.
- **Tier 2 — reflection, primary-LLM-priced.** Only invoked on `Act` verdicts (and on a periodic "did I miss something?" reflection). The agent runs its full perceive → reflect → decide → act → learn loop. Cost is proportional to actual decisions, not to event volume.
- **Batched observation.** Tier 2 receives "what happened since I last reflected?" — an aggregated digest, not one event at a time. Pairs with the partitioned mailbox ([ADR 0018](0018-partitioned-mailbox.md)).
- **Cost target: ~6–8% of total agent cost.** This is the empirical headroom that lets initiative be on by default rather than an opt-in premium.

The choice of where Tier 1 runs (in-process via ONNX/llama.cpp vs. separate Ollama container) is unresolved and tracked in [`docs/architecture/open-questions.md`](../architecture/open-questions.md).

## Alternatives considered

- **Primary LLM for every event.** Cost scales linearly with event volume; cannot ship as a default.
- **Rule-based screening.** Cheap and predictable, but every interesting "should I act?" question turns into a rule edit. Encodes the platform's opinion in the rules.
- **No initiative.** Reverts to v1's strictly-reactive model and forfeits an entire design goal of v2 (autonomous, continuous operation).

## Consequences

- **Initiative ships as a default capability, not a premium feature.** The cost envelope makes it viable for every agent.
- **Tier 1 is on the platform's critical path.** Outages of the Tier 1 host degrade initiative across every agent simultaneously. Mitigations: keep the screening prompt small and the model swappable; allow per-tenant Tier 1 endpoints once `Initiative:Tier1:OllamaBaseUrl` matures.
- **Two prompt budgets to manage.** Tier 1 has a tight context window; Tier 2 has the agent's full assembled prompt. Both are accounted separately in cost tracking.
- **Tier 1 verdict quality is observable.** False-`Ignore` rates and false-`Act` rates can be measured against historical activity events; we keep that signal in the platform's observability surface so the screening prompt can be tuned.
