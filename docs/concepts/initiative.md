# Initiative

**Initiative** is the agent's capacity to autonomously decide to act -- not just respond to triggers, but originate actions.

## Initiative Levels

Initiative is not a binary on/off -- it's a spectrum of increasing autonomy, where each level grants the agent control over more of its own behavior.

| Level | What the Agent Controls | Example |
|-------|------------------------|---------|
| **Passive** | Nothing. Only acts when explicitly triggered. | A code formatter invoked on demand |
| **Attentive** | Monitors events via fixed triggers. Decides *whether* to act on each event. | A security scanner watching commits |
| **Proactive** | Adjusts its own trigger frequency. Chooses actions from an allowed set. May modify its own schedule. | An agent that notices untested code and writes tests |
| **Autonomous** | Creates its own triggers, manages its own subscriptions. Full self-direction. | A research agent tracking a field |

Higher initiative levels require more permissions. A proactive agent can modify its own reminder schedule. An autonomous agent can create new subscriptions and change its own activation configuration.

## Tiered Cognition: Making Initiative Cost-Efficient

Initiative could be expensive -- if every event triggered a full LLM call, costs would spiral. Spring Voyage V2 solves this with a two-tier cognition model.

### Tier 1: Screening (Cheap)

A small, locally-hosted LLM (e.g., Phi-3, Llama 3.1 8B) performs fast, cheap screening of incoming events. For each event, it decides:

- **Ignore** -- this event is not relevant (~90% of events)
- **Queue for later** -- interesting but not urgent (~8%)
- **Act immediately** -- this needs the agent's full attention (~2%)

The cost is effectively zero -- the small model runs on shared platform infrastructure.

### Tier 2: Reflection (Selective)

Only when Tier 1 decides it's warranted does the agent's primary LLM (Claude, GPT-4, etc.) get invoked. This happens selectively -- perhaps 5-20 times per day rather than hundreds.

The Tier 2 cognition loop:

1. **Perceive** -- What has changed since I last reflected? (batched observation events, new messages, time elapsed)
2. **Reflect** -- Given my expertise, instructions, and context, is there something I should do?
3. **Decide** -- What action, if any? (send a message, start a conversation, query the directory, alert a human, update knowledge, or do nothing)
4. **Act** -- Execute the decided action
5. **Learn** -- Record the outcome

"Do nothing" is a common and valid outcome. Not every reflection leads to action.

## Initiative Policies

Initiative is governed by unit-level policies that set boundaries on what agents can do:

- **Maximum initiative level** -- no agent in the unit can exceed this
- **Allowed actions** -- what kinds of actions initiative can trigger (send messages, start conversations, query directories)
- **Blocked actions** -- what initiative cannot do (modify connector config, spawn agents)
- **Cost limits** -- maximum Tier 2 calls per hour, maximum cost per day
- **Approval requirements** -- whether initiative actions require unit approval before execution

These policies ensure initiative is useful without being uncontrolled.

## How Initiative Adds Up

Initiative adds roughly 6-8% to total agent cost while enabling proactive value. The two-tier model keeps screening nearly free, and the selective use of the primary LLM keeps reflection costs predictable.
