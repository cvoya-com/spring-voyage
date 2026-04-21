# Initiative

**Initiative** is an agent's capacity to autonomously decide to act — not just respond to triggers, but originate actions.

It is a spectrum, not an on/off switch:

| Level | What the agent controls | Example |
|-------|-------------------------|---------|
| **Passive** | Nothing. Only acts when explicitly triggered. | A code formatter invoked on demand |
| **Attentive** | Monitors events via fixed triggers. Decides *whether* to act on each event. | A security scanner watching commits |
| **Proactive** | Adjusts its own trigger frequency. Chooses actions from an allowed set. May modify its own schedule. | An agent that notices untested code and writes tests |
| **Autonomous** | Creates its own triggers, manages its own subscriptions. Full self-direction. | A research agent tracking a field |

Higher levels require more permissions. Initiative is governed by unit-level policies (allowed/blocked actions, cost limits, approval requirements) so it stays useful without being uncontrolled.

To keep cost predictable, Spring Voyage runs initiative through a two-tier cognition model: a cheap, locally-hosted screening LLM filters incoming events, and the agent's primary LLM only reflects on the small fraction that warrants attention. See [ADR 0020 — Tiered cognition for initiative](../decisions/0020-tiered-cognition-for-initiative.md) for the rationale.

For the full execution model — Tier 1/Tier 2 mechanics, the perceive-reflect-decide-act-learn loop, policy enforcement, and how initiative integrates with messaging and observability — see [Architecture: Initiative](../architecture/initiative.md).
