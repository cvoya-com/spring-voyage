# Units & Agents

> **[Architecture Index](README.md)** | Related: [Agents](agents.md), [Policies](policies.md), [Expertise](expertise.md), [Unit Lifecycle](unit-lifecycle.md), [Messaging](messaging.md), [Infrastructure](infrastructure.md), [Initiative](initiative.md), [Workflows](workflows.md)

This document is the **entry point** for the units-and-agents cluster. It covers what a unit *is* as an entity — its identity, membership model, and how units nest recursively. Deeper topics live in focused sub-documents:

| Sub-document | Contents |
|---|---|
| [Agents](agents.md) | Agent model, execution pattern, cloning, role, prompt assembly & platform tools |
| [Policies](policies.md) | Unit policy framework, root unit |
| [Expertise](expertise.md) | Expertise profiles, directory, recursive aggregation, directory search, YAML seeding |
| [Unit Lifecycle](unit-lifecycle.md) | Status DAG, validation workflow, imperative and declarative creation paths |

---

## Unit Model

**A unit is an agent that has children.** It shares the agent's mailbox, address shape, and execution configuration; the only structural difference is the children list. When a unit's mailbox receives a message, the unit's own runtime runs — the same launcher path that runs a leaf agent — and the runtime's instructions decide whether to answer directly or delegate to a child. There is no separate orchestration-strategy layer ([ADR-0039](../decisions/0039-units-are-agents.md)).

The unit actor is responsible for:

- **Identity:** `unit://<id>` address, membership list, boundary configuration
- **Membership:** managing which agents and sub-units are children of the unit
- **Boundary:** controlling what is visible to the parent unit
- **Activity stream:** aggregating member activity for observation
- **Expertise directory:** maintaining the aggregated expertise of all members
- **Mailbox:** delivering inbound messages to the unit's own runtime via the shared launcher contract

Because `IUnitActor` inherits the shared `IAgent` contract (see [Messaging](messaging.md)), a unit plugged into a parent's member list receives messages through exactly the same mailbox seam that an agent member would. The address scheme distinction (`unit://` vs `agent://`) stays for routing and identity continuity; it does not gate behaviour.

```yaml
unit:
  name: engineering-team
  description: Software engineering team for the spring-voyage repo

  structure: hierarchical            # hierarchical | peer | custom

  # --- Unit AI (the unit IS an agent — same ai block pattern) ---
  ai:
    runtime: spring-voyage           # AgentRuntime id from platform/runtime-catalog.yaml
    model:
      provider: ollama
      id: llama3.2:3b
  execution:
    image: spring-workflows/software-dev-cycle:latest

  members:
    - agent: ada
    - agent: kay
    - agent: hopper
    - unit: database-team            # recursive composition

  # --- Default execution block for member agents (#601 B-wide) ---
  # Members that don't declare a given field inherit from this block
  # per the agent → unit → fail resolution chain.
  # #1732: 'tool' was dropped — the launcher is derived from
  # the runtime registry via ai.runtime.
  execution:
    image: ghcr.io/cvoya-com/claude-code-base:latest
    provider: anthropic              # spring-voyage runtime kind only (#598 gating)
    model: claude-sonnet             # spring-voyage runtime kind only (#598 gating)

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

**Unit runtime:**

A unit's `ai` block (`runtime`, `model`, `image`) describes the runtime that runs when the unit's mailbox receives a message — exactly the same shape as a leaf agent's per [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md). The launcher reads it, spawns the runtime container, and delivers the inbound message. The runtime answers, delegates to a child, or fans out — its instructions decide.

**Orchestration tools.** When the unit has at least one child, the launcher attaches a fixed set of orchestration tools to the runtime ([ADR-0039 § 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime)):

| Tool | Purpose |
|---|---|
| `list_children` | Enumerate direct children with addresses, kinds, and resolved execution config. |
| `inspect_child <address>` | Return a single child's metadata (role, declared expertise, status). |
| `delegate_to_child <address> <message>` | Forward the inbound message to one child and await its response. Records an `OrchestrationDecision` with `kind: delegate`. |
| `fanout_to_children <addresses[]> <message>` | Forward to multiple children in parallel. Records an `OrchestrationDecision` with `kind: fanout`. |
| `query_child_status <address>` | Cheap status check for a child without a full inspect. |

The set is closed for v0.1 — adding a tool requires a new ADR. Tools are reachable through two parallel surfaces that share the same handlers and emit the same `OrchestrationDecision` events:

- **Tool-call surface** — for LLM-driven runtime images (`spring-voyage`, `claude-code`, `codex`, `gemini`). Per-runtime mechanism (MCP server, env-var-keyed registry).
- **SDK surface** — `Cvoya.Spring.AgentSdk`'s typed `IOrchestrationClient` over an HTTP callback API, for workflow-driven runtime images that consume the orchestration tools as method calls. See [Agent SDK](agent-sdk.md).

The image author chooses which fits; the platform does not branch.

**Example: a unit that delegates by expertise:**

```yaml
unit:
  name: research-cell
  ai:
    runtime: claude-code              # AgentRuntime id from runtime-catalog.yaml
    model:
      provider: anthropic
      id: claude-sonnet-4-6
  execution:
    image: ghcr.io/cvoya-com/claude-code-base:latest
  instructions: |
    You coordinate a research team. Use `list_children` to see who's
    available, `inspect_child` to read declared expertise, and
    `delegate_to_child` to route a paper to the best fit. Provide a
    one-line `reason` on every delegation — it is recorded as
    OrchestrationDecision evidence.
  members:
    - agent: researcher-ml
    - agent: researcher-systems
```

A unit with zero children still receives messages and runs its runtime; the orchestration tools are simply absent from the launch.

---

## Nested Units (Units as Members of Units)

Members of a unit may be either agents (scheme `agent`) or sub-units (scheme `unit`). Nesting lets you compose larger organizations from smaller ones — a platform team contains a database team, which contains individual agents — without teaching the routing layer anything special about depth. A parent unit's runtime treats both agents and sub-units uniformly through the orchestration tools: `list_children` enumerates either kind, `delegate_to_child` forwards to either, and `IAgentProxyResolver` maps the address scheme to the right actor type at delivery time.

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


This list is illustrative, not exhaustive. Any organizational pattern can be modeled through unit composition, boundary configuration, and the runtime image / instructions a unit picks for itself. The primitives — recursive units, configurable boundaries, runtime-decided delegation through the orchestration tools — are the building blocks; the patterns emerge from how you compose them.

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
          "description": "Execution config for the unit's own runtime — same shape as an agent's per ADR-0038. The runtime decides how to use the orchestration tools attached when the unit has children.",
          "properties": {
            "runtime": {
              "type": "string",
              "description": "AgentRuntime id from runtime-catalog.yaml (e.g. spring-voyage, claude-code, codex, gemini)."
            },
            "model": {
              "type": "object",
              "description": "Model selection. Provider is intrinsic to the model entry in the catalogue.",
              "properties": {
                "provider": { "type": "string" },
                "id": { "type": "string" }
              }
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
              "description": "Skill references attached to the unit's runtime, alongside the orchestration tools."
            }
          }
        },
        "instructions": {
          "type": "string",
          "description": "Runtime-facing instructions composed into the unit's prompt. Decides whether the unit answers directly or delegates via the orchestration tools."
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
          "description": "Default execution block for member agents that don't declare the given field (#601 B-wide). Resolution chain: agent.X → unit.X → fail-clean. #1732: 'tool' is derived from 'agent' via the runtime registry.",
          "properties": {
            "image": { "type": "string", "description": "Default container image reference." },
            "runtime": {
              "type": "string",
              "enum": ["podman", "docker"],
              "description": "Default container runtime."
            },
            "agent": {
              "type": "string",
              "description": "Agent runtime registry id (e.g. claude, openai, google, ollama). Drives launcher selection at dispatch via IAgentRuntime.Kind."
            },
            "provider": {
              "type": "string",
              "description": "Default LLM provider. Meaningful only when the resolved runtime kind = spring-voyage (#598 gating)."
            },
            "model": {
              "type": "string",
              "description": "Default model identifier. Meaningful only when the resolved runtime kind = spring-voyage (#598 gating)."
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
- [Agent SDK](agent-sdk.md) — `Cvoya.Spring.AgentSdk`, `IOrchestrationClient`, env-var contract for workflow-driven runtimes
- [Policies](policies.md) — unit policy framework, root unit
- [Expertise](expertise.md) — expertise profiles, directory, aggregation, search
- [Unit Lifecycle](unit-lifecycle.md) — validation workflow, status DAG, creation paths
- [Messaging](messaging.md) — mailbox, thread model, `AgentMemory`, `ThreadMemoryPolicy`
- [Infrastructure](infrastructure.md) — Dapr actor model, `IAddressable`
- [Initiative](initiative.md) — initiative levels, tiered cognition, initiative policies
- [ADR-0039](../decisions/0039-units-are-agents.md) — units-are-agents; orchestration as runtime behaviour
