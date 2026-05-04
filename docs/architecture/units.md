# Units & Agents

> **[Architecture Index](README.md)** | Related: [Agents](agents.md), [Orchestration](orchestration.md), [Policies](policies.md), [Expertise](expertise.md), [Unit Lifecycle](unit-lifecycle.md), [Messaging](messaging.md), [Infrastructure](infrastructure.md), [Initiative](initiative.md), [Workflows](workflows.md)

This document is the **entry point** for the units-and-agents cluster. It covers what a unit *is* as an entity — its identity, membership model, and how units nest recursively. Deeper topics live in focused sub-documents:

| Sub-document | Contents |
|---|---|
| [Agents](agents.md) | Agent model, execution pattern, cloning, role, prompt assembly & platform tools |
| [Orchestration](orchestration.md) | Orchestration strategies, unit boundary, execution-defaults resolution chain |
| [Policies](policies.md) | Unit policy framework, root unit |
| [Expertise](expertise.md) | Expertise profiles, directory, recursive aggregation, directory search, YAML seeding |
| [Unit Lifecycle](unit-lifecycle.md) | Status DAG, validation workflow, imperative and declarative creation paths |

---

## Unit Model

A unit is a **composite agent** — a group of agents that appears as a single `IMessageReceiver` to the outside world. The unit owns **identity** (address, membership, boundary, activity stream) and delegates **orchestration** (how incoming messages are routed to members) to a pluggable strategy.

The unit actor is responsible for:

- **Identity:** address, membership list, boundary configuration
- **Membership:** managing which agents and sub-units belong to the unit
- **Boundary:** controlling what is visible to the parent unit
- **Activity stream:** aggregating member activity for observation
- **Expertise directory:** maintaining the aggregated expertise of all members

Because `IUnitActor` inherits the shared `IAgent` contract (see [Messaging](messaging.md)), a unit plugged into a parent's member list receives messages through exactly the same mailbox seam that an agent member would. This is the **composite pattern**: a unit IS an agent from the parent's perspective.

```yaml
unit:
  name: engineering-team
  description: Software engineering team for the spring-voyage repo
  
  structure: hierarchical            # hierarchical | peer | custom
  
  # --- Unit AI (the unit IS an agent — same ai block pattern) ---
  # Delegated: orchestration runs in a workflow container
  ai:
    execution: delegated
    tool: software-dev-cycle         # registered workflow tool
    environment:                     # container for orchestration logic
      image: spring-workflows/software-dev-cycle:latest
      runtime: podman
  
  members:
    - agent: ada
    - agent: kay
    - agent: hopper
    - unit: database-team            # recursive composition
  
  # --- Default execution block for member agents (#601 B-wide) ---
  # Members that don't declare a given field inherit from this block
  # per the agent → unit → fail resolution chain.
  execution:
    image: spring-agent:latest
    runtime: podman                  # docker | podman
    tool: claude-code                # claude-code | codex | gemini | dapr-agent | custom
    provider: anthropic              # dapr-agent only (#598 gating)
    model: claude-sonnet             # dapr-agent only (#598 gating)
  
  connectors:
    - type: github
      config:
        repo: savasp/spring
        webhook_secret: ${GITHUB_WEBHOOK_SECRET}
    - type: slack
      config:
        channel: "#engineering-team"
  
  packages:
    - spring-voyage/software-engineering
  
  policies:
    communication: hybrid            # through-unit | peer-to-peer | hybrid
    work_assignment: unit-assigns    # unit-assigns | self-select | capability-match
    expertise_sharing: advertise
    initiative:
      max_level: proactive
      max_actions_per_hour: 20
    
  humans:
    - identity: savasp
      permission: owner
      notifications: [slack, email]
    - identity: reviewer2
      permission: operator
      notifications: [github]
    - identity: stakeholder1
      permission: viewer
      notifications: [email]
```

**Unit AI:**

A unit's `ai` block describes how the unit orchestrates its members. Two flavours:

- **AI-orchestrated** — the unit uses a lightweight LLM call to decide routing (see `AiOrchestrationStrategy` and [ADR 0021](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md)). Requires `agent`, `model`, `prompt`, and optionally `skills`. No multi-turn tool loop in the platform; this is a single prompt that returns a routing decision.
- **Workflow** — the unit delegates orchestration to a workflow container. Requires `tool` and `environment`. The workflow container drives the sequence — it may invoke agents as activities.

**Example: AI-orchestrated unit:**

```yaml
unit:
  name: research-cell
  ai:
    agent: claude
    model: claude-sonnet-4-6
    prompt: |
      You coordinate a research team. Route papers
      to the most relevant researcher by expertise.
    skills:
      - package: spring-voyage/research
        skill: paper-triage
  members:
    - agent: researcher-ml
    - agent: researcher-systems
```

For the three concrete orchestration strategies (`ai`, `workflow`, `label-routed`) and the strategy resolution protocol, see [Orchestration](orchestration.md).

---

## Nested Units (Units as Members of Units)

Members of a unit may be either agents (scheme `agent`) or sub-units (scheme `unit`). Nesting lets you compose larger organizations from smaller ones — a platform team contains a database team, which contains individual agents — without teaching the routing layer anything special about depth. A parent's orchestration strategy treats both agents and sub-units uniformly: it picks one member, dispatches via `IUnitContext.SendAsync`, and the `IAgentProxyResolver` maps the address scheme to the right actor type.

Membership has two invariants:

1. **Agents are leaves with M:N memberships.** An agent may belong to any number of units. Each `(parent_unit_id, child_agent_id)` edge is stored as a row in the `unit_memberships` table with optional per-membership config overrides (model, specialty, enabled, execution mode). The wire-shape `parentUnit` pointer on `AgentMetadata` / `AgentResponse` is convenience-only — it is derived server-side from the agent's membership list (the earliest `CreatedAt` row wins) and there is no authoritative 1:N invariant. **Unit-typed members stay 1:N** per #217: a sub-unit has exactly one parent unit, and nesting lives on the unit-unit axis.
2. **Unit membership is acyclic.** The graph of unit-typed members must be a DAG — no unit may contain itself, directly or transitively.

Top-level membership is a row in the membership graph, not a flag. A unit is "top-level" when it has a `unit_subunit_memberships` row whose `parent_id = tenant.id` — the tenant row itself is a node in the graph and the membership graph is rooted there. There is no separate `is_top_level` boolean on the unit; queries that need to know whether a unit is top-level walk the membership graph and look for a tenant-owned parent row. See [ADR 0036 § 4](../decisions/0036-single-identity-model.md#4-membership-graph-is-the-addressing-fabric).

**Cycle detection.** Every call to `IUnitActor.AddMemberAsync` with a unit-typed member walks the candidate's sub-unit graph before persisting the new edge. The walk:

- Rejects a self-loop (adding a unit to itself).
- Rejects a back-edge of any depth — e.g., if `A` already contains `B`, adding `A` to `B` fails; if `A` → `B` → `C` already exists, adding `A` to `C` fails.
- Is bounded by a maximum nesting depth of 64. Exceeding the bound is itself treated as a cycle signal — the add is rejected with the path walked so far.
- Is read-only and resilient to concurrent modifications: if a sub-unit is deleted mid-walk, or the directory cannot be read, the traversal treats that path as a dead end and continues. Side-cycles in the sub-graph that do not close back on the parent are ignored.
- Resolves each candidate through `IDirectoryService` (Guid → flat actor id) and reads the sub-unit's member list through a typed `IUnitActor` proxy, so the walk reflects live actor state, not a stale cache.
- Does **not** run for agent-typed members — agents cannot introduce a cycle because they are leaves.

A rejected add surfaces a `CyclicMembershipException` carrying the parent unit, the candidate member, and the full ordered cycle path. The HTTP API projects this as a 409 Conflict `ProblemDetails` response with `parentUnit`, `candidateMember`, and `cyclePath` fields so callers can show a precise diagnostic.

Removing a unit-typed member is a straightforward state write — no cycle check is needed because removing an edge cannot introduce one.

**Persistent projection of unit-as-member edges (#1154).** `IUnitActor` state is the source of truth for runtime dispatch and cycle detection, but it is invisible to readers that don't fan out one actor call per unit (the tenant-tree endpoint, future analytics, the cloud overlay's audit pipeline). To unblock those readers without forcing them to walk every actor, `UnitActor.AddMemberAsync` and `RemoveMemberAsync` write through to a sibling `unit_subunit_memberships` table whenever the mutated member is unit-typed. Composite primary key `(tenant_id, parent_unit_id, child_unit_id)`; per-edge config overrides for unit-typed members remain deferred to #217. Top-level units are projection rows whose `parent_unit_id = tenant.id` — the tenant row anchors the membership graph (see [ADR 0036 § 4](../decisions/0036-single-identity-model.md#4-membership-graph-is-the-addressing-fabric)).

The projection is best-effort by design: a write-through failure logs and continues, never aborting the actor turn that already updated state. Two safety nets recover any drift:

1. The unit-delete cascade in `DirectoryService.CascadeDeleteUnitAsync` deletes every projection row that mentions the deleted unit on either side, in the same EF transaction that flips its `deleted_at`. A surviving ghost would otherwise render a deleted unit as a child node on the next `GET /api/v1/tenant/tree`.
2. A startup hosted service (`UnitSubunitMembershipReconciliationService`, registered by the Worker host) iterates the directory, asks each unit actor for its current member list, upserts every missing unit-to-unit edge, and retires every projection row whose edge is no longer present in actor state. The reconciliation runs once per host start, is idempotent, and is the only path that backfills the projection on existing deployments where the column did not exist before this migration.

**Sub-unit creation surfaces.** The `POST /api/v1/units` endpoint and the package-install path accept an optional `parentUnitIds: [<parent-id>]` field — `<parent-id>` is the parent unit's `Guid`. When supplied, `UnitCreationService.ValidateParentRequest` resolves the parent ids through `IDirectoryService`, registers the new unit, and persists the unit-to-unit membership edge in one server-side transaction (so a partial failure rolls the unit back). Omitting `parentUnitIds` creates a top-level unit — a membership row anchored at the tenant. The CLI exposes this via `spring unit create <display-name> --parent-unit <parent-id-or-name>` (the resolver accepts a Guid for direct lookup or a display-name search; see [Identifiers § 7](identifiers.md#7-cli-guid-for-direct-lookup-name-for-search)). The portal exposes it via the **Create sub-unit** action on the parent's detail pane (#1150) — see [docs/guide/portal.md § Top-level vs sub-unit creation](../guide/user/portal.md#top-level-vs-sub-unit-creation-1150). Cycle detection runs on the resulting `AddMemberAsync` call, so a sub-unit creation that would close a cycle is rejected with the same `CyclicMembershipException` projection as an after-the-fact `members add`. The membership edge is recorded through the same `AddMemberAsync` path, so the persistent projection (#1154) sees sub-units created via this surface immediately — `GET /api/v1/tenant/tree` returns the new unit nested under its parent on the next call.

---

## Organizational Patterns


| Pattern               | Description                                                 | Example                               |
| --------------------- | ----------------------------------------------------------- | ------------------------------------- |
| **Engineering Team**  | Specialized agents with defined roles working on a codebase | Backend + frontend + QA + DevOps      |
| **Product Squad**     | Cross-functional group working on a feature                 | PM + design + engineering agents      |
| **Research Cell**     | Agents autonomously monitoring a domain                     | Paper tracking, trend analysis        |
| **Support Desk**      | Agents responding to requests from multiple humans          | Customer support, internal helpdesk   |
| **Creative Studio**   | Agents collaborating on creative output                     | Writing, design, art direction        |
| **Operations Center** | Agents monitoring systems, responding to incidents          | Infrastructure alerts, SLA monitoring |
| **Ad-hoc Task Force** | Temporary unit for a specific problem                       | Incident response, sprint goal        |


This list is illustrative, not exhaustive. Any organizational pattern can be modeled through unit composition, boundary configuration, and orchestration strategy selection. The primitives — recursive units, configurable boundaries, three orchestration strategies — are the building blocks; the patterns emerge from how you compose them.

---

## Appendix: Unit Definition Schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "schemas/unit.schema.json",
  "title": "Unit Definition",
  "type": "object",
  "required": ["unit"],
  "properties": {
    "unit": {
      "type": "object",
      "required": ["name", "structure", "members"],
      "properties": {
        "name": {
          "type": "string",
          "pattern": "^[a-z][a-z0-9-]*$",
          "description": "Local symbol for the unit inside this manifest file. Mapped to a fresh Guid by the install pipeline and never persisted as the unit's identity. The unit's stable identifier is the Guid; `display_name` (presentation-only, not unique) is set separately."
        },
        "description": { "type": "string" },
        "structure": {
          "type": "string",
          "enum": ["hierarchical", "peer", "custom"]
        },
        "ai": {
          "type": "object",
          "properties": {
            "agent": {
              "type": "string",
              "description": "Registered AI agent provider for lightweight orchestration decisions."
            },
            "model": {
              "type": "string",
              "description": "Model identifier for lightweight orchestration decisions."
            },
            "prompt": {
              "type": "string",
              "description": "Orchestration prompt used when the unit's routing strategy is AI-orchestrated."
            },
            "skills": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["package", "skill"],
                "properties": {
                  "package": { "type": "string" },
                  "skill": { "type": "string" }
                }
              },
              "description": "Skill references exposed in the orchestration prompt."
            },
            "tool": {
              "type": "string",
              "description": "Registered workflow tool name when the unit delegates orchestration to a workflow container."
            },
            "environment": {
              "type": "object",
              "properties": {
                "image": { "type": "string", "description": "Container image." },
                "runtime": { "type": "string", "enum": ["podman", "docker", "kubernetes"] }
              },
              "description": "Container definition for the workflow tool (when used)."
            }
          }
        },
        "members": {
          "type": "array",
          "items": {
            "type": "object",
            "oneOf": [
              { "required": ["agent"], "properties": { "agent": { "type": "string" } } },
              { "required": ["unit"], "properties": { "unit": { "type": "string" } } }
            ]
          }
        },
        "execution": {
          "type": "object",
          "description": "Default execution block for member agents that don't declare the given field. Five-field shape (#601 B-wide). Resolution chain: agent.X → unit.X → fail-clean.",
          "properties": {
            "image": { "type": "string", "description": "Default container image reference." },
            "runtime": {
              "type": "string",
              "enum": ["podman", "docker"],
              "description": "Default container runtime."
            },
            "tool": {
              "type": "string",
              "description": "Default external agent tool identifier. Known values: claude-code, codex, gemini, dapr-agent, custom."
            },
            "provider": {
              "type": "string",
              "description": "Default LLM provider. Meaningful only when tool = dapr-agent (#598 gating)."
            },
            "model": {
              "type": "string",
              "description": "Default model identifier. Meaningful only when tool = dapr-agent (#598 gating)."
            }
          }
        },
        "connectors": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["type"],
            "properties": {
              "type": { "type": "string" },
              "config": { "type": "object" }
            }
          }
        },
        "packages": {
          "type": "array",
          "items": { "type": "string" }
        },
        "policies": {
          "type": "object",
          "properties": {
            "communication": {
              "type": "string",
              "enum": ["through-unit", "peer-to-peer", "hybrid"]
            },
            "work_assignment": {
              "type": "string",
              "enum": ["unit-assigns", "self-select", "capability-match"]
            },
            "expertise_sharing": {
              "type": "string",
              "enum": ["advertise", "on-request", "private"]
            },
            "initiative": {
              "type": "object",
              "properties": {
                "max_level": {
                  "type": "string",
                  "enum": ["passive", "attentive", "proactive", "autonomous"]
                },
                "max_actions_per_hour": { "type": "integer", "minimum": 0 }
              }
            }
          }
        },
        "humans": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["identity", "permission"],
            "properties": {
              "identity": { "type": "string" },
              "permission": {
                "type": "string",
                "enum": ["owner", "operator", "viewer"]
              },
              "notifications": {
                "type": "array",
                "items": { "type": "string" }
              }
            }
          }
        }
      }
    }
  }
}
```

---

## Template-flow parity: wizard ↔ CLI

> Canonical mapping per #1419. Future template authors must keep these in lock-step.
> CONVENTIONS.md § 13 (UI / CLI Feature Parity) makes this a hard rule.

The two ship-with templates (`software-engineering` and `product-management`) must work
identically from the management-portal wizard and from the `spring` CLI.

### Ship-with templates

| Template | Package path | Unit manifest | GitHub connector |
|---|---|---|---|
| software-engineering | `packages/software-engineering/` | `units/engineering-team.yaml` | Defined in manifest |
| product-management | `packages/product-management/` | `units/product-squad.yaml` | Defined in manifest |

Both templates declare a `connectors[type: github]` block so the GitHub connector is wired
at instantiation time. The user supplies the specific repository at creation time — the
`config` block in the manifest does not hard-code an owner/repo.

### Creating units from templates

The `spring unit create-from-template` verb and the `/api/v1/units/from-template` endpoint
were removed in ADR-0035. The current path is to use `spring package install <package>` (CLI)
or the new-unit wizard's **From catalog** mode (portal), both of which route through
`POST /api/v1/packages/install` and activate all artefacts in the package atomically.

---

## See Also

- [Agents](agents.md) — agent model, execution pattern, cloning, prompt assembly
- [Orchestration](orchestration.md) — orchestration strategies, unit boundary, execution defaults
- [Policies](policies.md) — unit policy framework, root unit
- [Expertise](expertise.md) — expertise profiles, directory, aggregation, search
- [Unit Lifecycle](unit-lifecycle.md) — validation workflow, status DAG, creation paths
- [Messaging](messaging.md) — mailbox, thread model, `AgentMemory`, `ThreadMemoryPolicy`
- [Infrastructure](infrastructure.md) — Dapr actor model, `IAddressable`
- [Initiative](initiative.md) — initiative levels, tiered cognition, initiative policies
