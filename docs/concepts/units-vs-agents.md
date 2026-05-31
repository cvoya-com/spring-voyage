# Units vs agents — quick reference

A **unit is an agent**. This page is the one-screen reference for what that means for decision-making: which features apply to both, what's unique to each, and how to decide when designing a new surface, endpoint, or UX.

## What's the same

A unit and a leaf agent share every aspect of being an agent. Anything that applies to "an agent" applies to a unit too unless this page says otherwise.

| Aspect | Applies to |
|---|---|
| Mailbox + Dapr-actor routing | Unit + Agent |
| Execution config (`runtime`, `model`, `image`, `hosting`) | Unit + Agent |
| Credentials / secrets (with parent-chain inheritance) | Unit + Agent |
| Skills (equipped capabilities) | Unit + Agent |
| Traces (per-execution introspection) | Unit + Agent |
| Memory (short-term + long-term) | Unit + Agent |
| Activity feed + per-model cost rollup | Unit + Agent |
| Messages / thread participation | Unit + Agent |
| Budget (per-subject daily-budget) | Unit + Agent |
| Initiative policy (passive vs proactive) | Unit + Agent |
| Expertise declaration | Unit + Agent |
| Deployment lifecycle (deploy / undeploy / scale / logs) | Unit + Agent |
| Validation workflow | Unit + Agent |

If a feature appears in this list and a new design proposes it only for one of the two, that's almost certainly a mistake — extend it to both.

## What's different

There are only a handful of real differences. They flow from one structural fact (units own children, leaf agents don't) and one product fact (units cannot be cloned yet).

### Unit-only (a leaf agent does not have these)

| Feature | Notes |
|---|---|
| **Children** | Units compose member agents and sub-units. A leaf agent has none. The child list drives expertise aggregation and the Unit × Agents tab. |
| **Boundary** | Units define a boundary (`UnitBoundary`) that controls what callers outside the unit see and which work the unit is eligible to receive. Leaf agents have no boundary surface. |
| **Recursively-enforced policies** | Policies set on a unit cascade to its children (skill / model / cost / execution-mode dimensions). Policies set on a leaf agent are local. |
| **Connector binding** | A unit owns a connector binding that translates external events (GitHub webhooks, Slack messages, etc.) into platform messages. Agents inherit connector reachability from their owning unit; they cannot bind connectors directly. The Agent × Config → Connector sub-tab is a read-only inherited view of the owning unit's binding. |
| **Membership operations** | Add / remove member agents and sub-units via the membership endpoints. Leaf agents only participate as members. |
| **Multi-parent membership** | An agent can belong to multiple parent units (M:N); a sub-unit has exactly one parent. |
| **Expertise aggregation** | A unit's effective expertise is the union of its own declared expertise plus its children's. Leaf-agent expertise is just what the agent declares. |
| **Human-permission grants** | Units own per-subject human grants for configure / operate / view. Leaf agents inherit through their owning unit. |

### Agent-only (a unit does not have these — yet)

| Feature | Notes |
|---|---|
| **Cloning** | A leaf agent can be cloned (`spring agent clone`); the clone is a fresh agent that inherits configuration. A unit cannot be cloned today. The cloning policy, the Clones tab, and `agent-cloning-policy-panel.tsx` are agent-only. |

That's it. Everything else either applies to both or is unit-only.

## Decision-making rules of thumb

When designing a new feature, endpoint, panel, or tab, ask in order:

1. **Does it concern composition (children, boundary, recursive policy)?** → Unit-only.
2. **Does it concern cloning?** → Agent-only.
3. **Otherwise** → applies to both. Build it once with a `{ kind, id }` parameter; do not split it into `unit-*` and `agent-*` variants.

If you find yourself writing two near-identical implementations (one in `components/units/`, one in `components/agents/`) and the only difference is the scope argument to a hook, you are violating rule 3. Stop and parameterise.

## Addressing

Both subjects are addressable. The schemes differ:

| Subject | Address scheme | Example |
|---|---|---|
| Leaf agent | `agent:` | `agent:b168ee61…` |
| Unit | `unit:` | `unit:35e66200…` |
| Human | `human:` | `human:a2…` |
| Tenant | (none — tenant is not addressable as an actor) | — |

The `agent:` and `unit:` schemes encode containment shape, but the scheme does
not gate behaviour: both resolve to the same actor kind on the messaging
dimension and both see the same `sv.messaging.*` delivery tools. Addresses are
`(scheme, Guid)` pairs; see [Messaging](messaging.md).

## `execution.hosting`

`execution.hosting` is a first-class field on both unit and agent
manifests (and on agent / unit templates). The two modes are `ephemeral`
(default — a fresh container per turn) and `persistent` (a long-lived
container). Member agents inherit the parent unit's value when neither they
nor their template declares one.

## Humans are subjects, not agents

A **human** is a third kind of subject — addressable, can be a member of a unit, can participate in threads — but **not an agent**. A human implements only the message-receiving contract; the agent-shaped surfaces (memory, skills, traces, execution config, runtime, deployment) do not apply. See [Humans](humans.md) for the concept (including the install-time resolution model and the team-role / platform-role split).

In the package grammar, humans live on the same `members:` list as agents and sub-units, under the `- human:` discriminator. There is no separate top-level `humans:` block — the parser rejects the legacy shape with a structured `LegacyHumansBlock` error.

If you find yourself designing a feature that wants to apply to "everything in a unit's member set", remember it's a heterogeneous set: agents (which include sub-units) and humans. Different shapes, different surfaces.

## Why this matters

The runtime path treats unit and leaf agent identically. Splitting unit-* and agent-* implementations in the portal, the CLI, the docs, or the tests is a recurring source of divergence — the agent variant ships a feature, the unit variant doesn't, and operators notice the gap. The rule of thumb is to **build for the agent shape** and reach for unit-only or agent-only carve-outs only when the table above lists them.

See also:

- [Agents](agents.md) — the agent-layer concept doc.
- [Units](units.md) — the unit-specific layer doc.
- [Humans](humans.md) — humans as subjects (not agents).
