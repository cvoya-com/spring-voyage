# Units & agents

> **[Architecture index](README.md)** · Related: [Messaging](messaging.md), [Agent runtime](agent-runtime.md), [Runtime flows](runtime-flows.md), [Security](security.md)

The entity model. An **agent** is an autonomous AI-powered participant. A
**unit** is an agent that has children. This page covers the composite model,
the membership graph, the lifecycle, expertise and the directory, unit policies,
initiative, and cloning.

---

## A unit is an agent

A unit and a leaf agent are the same kind of thing on the dispatch dimension:
each has an address, a mailbox, and an execution configuration. The **only**
structural difference is that a unit has a list of children — member agents and
sub-units. When a message reaches a unit's mailbox, the unit's **own runtime
runs** — the same launcher path that runs a leaf agent — and decides whether to
answer directly or hand work to a member by sending it a message. There is no
separate orchestration layer ([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md),
[ADR-0017](../decisions/0017-unit-is-an-agent-composite.md)).

`AgentActor` and `UnitActor` are kind-specific actor groupings — a unit has
membership, expertise aggregation, a boundary, a validation workflow — but
neither is a distinct concept at the messaging boundary. To a parent, a member
unit looks exactly like a member agent.

## The definition

An agent or unit is defined declaratively (YAML applied via CLI or a package
install) or programmatically (an API call). A definition describes *what* the
entity is, not *where* it runs:

```yaml
agent:
  name: Ada                          # display name — presentation only
  role: backend-engineer
  capabilities: [csharp, postgresql, testing]
  ai:
    runtime: claude-code             # AgentRuntime id from runtime-catalog.yaml
    model:
      provider: anthropic
      id: claude-sonnet-4-6
  execution:
    image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
    hosting: ephemeral               # or persistent
  instructions: |
    You are a backend engineer...
  expertise:
    - domain: postgresql
      level: advanced
```

A unit adds a `members:` list and may carry connector bindings, unit policies,
and a boundary. The `ai` / `execution` block has the **same shape** for a unit —
it configures the unit's own runtime. The `(runtime, model)` split is
[ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md); see
[Agent runtime](agent-runtime.md).

> **Identity.** The stable identifier is a `Guid` minted at creation. A
> definition's `name` is a presentation-only display name — never unique, never
> addressable. Manifests use a local symbol mapped to a fresh `Guid` on install.
> See [Data & identity](data-and-identity.md).

### Execution config inheritance

An agent that omits an execution field inherits it from its parent unit, and a
top-level entity from tenant defaults. A **multi-parent** agent must define a
field itself whenever its parents disagree — `IExecutionConfigInheritanceResolver`
enforces this and returns a structured 422 naming the diverging field
([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md) §6).

## The membership graph

Members of a unit are agents (scheme `agent`) or sub-units (scheme `unit`),
declared under one `members:` list with a key-prefix discriminator
([ADR-0046](../decisions/0046-unified-members-grammar.md)):

```yaml
unit:
  name: engineering-team
  members:
    - agent: { ref: ada, roles: [backend], expertise: [postgresql] }
    - unit:  { ref: database-team }
    - human: { displayName: "Reviewer" }
```

The graph is **EF-authoritative** — there is no actor-state mirror
([ADR-0040](../decisions/0040-actor-state-ownership-matrix.md)):

- **Agent members** are `unit_memberships` rows, with optional per-membership
  config overrides. An agent may belong to any number of units (M:N).
- **Sub-unit members** are `unit_subunit_memberships` rows. A sub-unit has
  exactly one parent (1:N) — nesting lives on the unit-unit axis.
- A **top-level** unit has a membership row whose parent is the tenant. The
  tenant is itself a node in the graph; the graph is rooted there.
- **Humans** are a member kind too — addressable thread participants, not agents.

Unit membership must be **acyclic**. Every `AddMember` of a unit-typed member
walks the candidate's sub-unit graph (bounded to depth 64) and rejects any
self-loop or back-edge with a `CyclicMembershipException` projected as a 409.

## Lifecycle

An agent or unit moves through a status DAG; it cannot run until validation
passes.

```
Draft → Validating → Stopped → Starting → Running → Stopping → Stopped
            │            ↑
            └─→ Error ────┘   (revalidate)
```

Validation runs as **`ArtefactValidationWorkflow`**, a Dapr Workflow — not an
actor ([ADR-0024](../decisions/0024-unit-validation-as-dapr-workflow.md)). It
runs ordered, durable activities, short-circuiting on the first failure: pull
the image, probe the runtime tool in-container, probe the credential, resolve
the model. Each step emits a progress event (SSE for the portal, an activity
event for the CLI `--wait` loop). The terminal activity writes a structured
`LastValidationError` (or clears it) and flips the status to `Stopped` or
`Error`. `POST /api/v1/tenant/units/{id}/revalidate` re-runs it. Agent and unit
creation (`AgentLifecycleWorkflow`) and cloning (`CloningLifecycleWorkflow`) are
the other two platform workflows.

## Expertise and the directory

Each agent carries an **expertise profile** — domains with a level
(`beginner | intermediate | advanced | expert`). A YAML `expertise:` block seeds
the profile on first activation; actor state wins once an operator has edited it.

The **directory** is a property of the unit. A unit's *effective expertise* is
the recursive union of its own declared domains and every descendant's effective
expertise ([ADR-0006](../decisions/0006-expertise-directory-aggregation.md)).
Each aggregated entry keeps its `Origin` (the contributing address) and `Path`
(the chain down to the origin), so a caller can route to the leaf and a
permission check can decide whether the caller may traverse there. The walk is
depth-capped (64) and cached per unit with precise invalidation on membership or
expertise changes.

The **boundary** filters the directory's outside-the-unit view — a
`BoundaryFilteringExpertiseAggregator` decorator applies per-unit opacity,
projection, and synthesis rules ([ADR-0008](../decisions/0008-unit-boundary-decorator.md)).
A runtime discovers peers and capabilities through the `sv.directory.*` and
`sv.expertise.*` MCP tools; an expertise entry with a structured input schema is
itself skill-callable.

## Unit policies

A `UnitPolicy` is the governance record on a unit — five optional slots, each a
constraint on member agents:

| Slot | Constrains |
|------|-----------|
| `Skill` | Which tools (skills) members may invoke — allow / block lists |
| `Model` | Which models members may run |
| `Cost` | Per-invocation / per-hour / per-day cost caps |
| `ExecutionMode` | Pins or whitelists `Auto` / `OnDemand` dispatch |
| `Initiative` | A DENY overlay on per-agent initiative actions |

A unit is a **trust boundary**: a unit policy cannot be escaped by a
per-membership or per-agent override. `IUnitPolicyEnforcer` is the DI-swappable
enforcement seam — it walks every unit an agent belongs to; the first deny
short-circuits. The skill gate is consulted by the `McpServer` on every tool
call; the model / cost / execution-mode gates run before every dispatch.
Operators edit policy via `GET / PUT /api/v1/units/{id}/policy` and
`spring unit policy`.

## Initiative

Initiative is an agent's capacity to act without an external trigger. There are
four levels — `Passive`, `Attentive`, `Proactive`, `Autonomous` — each granting
a wider self-modification scope.

Initiative runs a **two-tier cognition model**
([ADR-0020](../decisions/0020-tiered-cognition-for-initiative.md)): a cheap
locally-hosted Tier-1 LLM screens every observed event (ignore / queue / act);
only Tier-1's "act" verdicts wake the agent's primary Tier-2 LLM for the full
perceive → reflect → decide → act → learn loop. This keeps initiative cost
proportional to value.

`IAgentInitiativeEvaluator` is the fail-closed governance seam. Per proposed
action it returns `ActAutonomously`, `ActWithConfirmation` (surface a proposal
for human / unit approval), or `Defer` (do nothing). It composes the effective
initiative level, the unit initiative-action overlay, cost caps, and the
`RequireUnitApproval` override; any error downgrades the result one step.

## Cloning

Cloning replaces v1's "define three identical agents" pattern: the platform
spawns copies of an agent on demand, governed by the agent's cloning policy.

| Policy | Behaviour |
|--------|-----------|
| `none` | Singleton; work queues if busy |
| `ephemeral-no-memory` | Clone handles one thread, then is destroyed; nothing flows back |
| `ephemeral-with-memory` | As above, but the clone's experiences feed back to the parent |
| `persistent` | Clone persists and evolves independently — a full agent |

An **attachment mode** controls how clones relate to the parent's unit:
`detached` makes clones peers in the parent's unit; `attached` promotes the
parent to a unit with the clones as its children. Units cannot be cloned —
composition is the unit's scaling mechanism.

A separate `AgentCloningPolicy` governance record (agent- or tenant-scoped,
stored in the `cloning_policies` table) constrains every clone request — allowed
policies, attachment modes, `MaxClones`, `MaxDepth`, per-clone budget — with
numeric caps collapsing to the tightest value across scopes.

## Prompt assembly

At dispatch the actor composes the runtime's system prompt from four layers:

| Layer | Source | Content |
|-------|--------|---------|
| 1. Platform | System-provided | Platform tool descriptions, the one-way messaging model, safety guidance, auto-injected connector context |
| 2. Unit context | Actor, at activation | Unit policies, peer directory snapshot, unit-equipped skill bundles |
| 3. Thread context | Actor, per invocation | Prior messages and partial results for the agent's current thread |
| 4. Agent instructions | The definition | Role guidance, domain knowledge, agent-equipped skills |

The composed prompt is handed to the runtime container (typically as
`CLAUDE.md` / `AGENTS.md` in the working directory, or via an environment
variable). See [Agent runtime](agent-runtime.md) for how a launcher delivers it.
