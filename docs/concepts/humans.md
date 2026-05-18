# Humans

## What a human is

A **human** is an addressable subject that participates in threads alongside agents and units. The platform models humans as actors (`HumanActor`) with their own address scheme (`human:<guid>`); a message addressed to `human:<id>` is routed to the right channel (Slack, GitHub, email, etc.) through the human's configured inbound connector binding.

Humans are **subjects**, not agents. They share thread participation with agents and units, but they do not have execution config, memory, skills, traces, runtime, or any of the agent-shaped surfaces. See [Units vs agents](units-vs-agents.md) for the agent-shaped contract; humans share only the `IMessageReceiver` slice.

## What a human can do

| Capability | Applies to Human |
|---|---|
| Participate in threads (receive + send messages) | Yes |
| Be a member of a unit | Yes (via `/api/v1/tenant/units/{id}/humans/{humanId}/permissions`) |
| Hold permissions on a unit (configure, operate, view) | Yes |
| Have an inbound connector binding (Slack handle, GitHub handle, email) | Yes |
| Have an outbound connector binding (translate external events into messages) | No — that's a unit/connector concern |
| Be cloned, deployed, scaled | No |
| Have memory, skills, traces, expertise, budget, policy, runtime | No |
| Be addressable via the directory | No — `human:` short-circuits the directory ([messaging.md](../architecture/messaging.md)) |

## How humans relate to units

A human becomes a member of a unit through the unit's human-permission surface. Membership grants the human one of the standard permission tiers (configure / operate / view) for that unit. The membership is what makes a human reachable as `human:<id>` *in the context of that unit's threads* — a human with no unit grants is not addressable by the platform's routing fabric.

This makes Unit's "Agents" tab a misnomer once humans land: a unit's member set is **agents + sub-units + humans**. The canonical-tabs design ([`docs/design/canonical-tabs.md`](../design/canonical-tabs.md)) renames the tab to "Members" pending v0.2.

## Portal scope

The portal's `NodeKind` (`src/Cvoya.Spring.Web/src/components/units/aggregate.ts`) was extended to include `"Human"` under #2266 / #2267. Humans are a fourth Explorer subject with a minimal canonical tab set:

- **Overview** — personal info (display name, username, email, platform role, created-at). Renders the "You" badge when the loaded human matches the currently-authenticated caller. Landed under #2267.
- **Messages** — threads the human is addressed in. Slot reserved by #2266; body ships under #2268 (Portal Wave B).
- **Config** — Identity + Connector sub-tabs (the inbound-routing binding). Slot reserved by #2266; body ships under #2269 (Portal Wave B).

No Memory, Agents, Skills, Traces, Clones, Policies, Budgets, or Deployment tabs — humans don't have those surfaces.

Human pages live at `/humans/<guid>` and are reached either directly (Cmd-K, activity-feed `human:` rows, future unit-membership rows from #2270 + #2427) or by selecting an Explorer node with the `human:` address scheme (`/units?node=human:<guid>` bounces to the dedicated route). The Detail Pane chrome is the shared `<DetailPane>` the Explorer mounts — same address-copy affordance, same tab strip — minus the lifecycle status badge (humans don't have a runtime lifecycle).

## See also

- [ADR-0039 — Units are agents](../decisions/0039-units-are-agents.md) — what makes a unit an agent (and by contrast, what makes a human *not* an agent).
- [Units vs agents](units-vs-agents.md) — agent-shaped contract.
- [`docs/architecture/messaging.md`](../architecture/messaging.md) — why `human:` short-circuits the directory.
- [`docs/architecture/infrastructure.md`](../architecture/infrastructure.md) — `HumanActor` and the actor-interface table.
- [`docs/design/canonical-tabs.md`](../design/canonical-tabs.md) — Explorer tab structure including the deferred Human column.
