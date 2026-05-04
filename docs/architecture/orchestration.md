# Orchestration

> **[Architecture Index](README.md)** | Related: [Units](units.md), [Agents](agents.md), [Policies](policies.md), [Workflows](workflows.md), [Messaging](messaging.md)

> **Orchestration is a mechanism in service of collaboration.** Spring Voyage is a collaboration platform for teams of AI agents and the humans they work with; orchestration is how a unit decides which of its members handles an incoming message. This document is mechanism-level: it specifies the routing contract, the strategies that ship in the platform, and the configuration surfaces. The collaboration narrative — engagements, threads, humans-in-the-loop — lives in the [concepts overview](../concepts/overview.md) and [thread model](thread-model.md).

This document covers how a unit routes incoming messages to its members: the `IOrchestrationStrategy` contract and its three concrete implementations, unit boundary configuration, and the execution-defaults resolution chain that member agents inherit. For the unit entity model (membership, nesting, identity), see [Units](units.md). For the governance policies that constrain what agents may do, see [Policies](policies.md). External orchestrators (ADK, LangGraph, Temporal, …) participate as members or peers via A2A and are covered in [Workflows § External Workflow Engines via A2A](workflows.md#external-workflow-engines-via-a2a).

---

## Orchestration Strategies

The unit delegates message handling to an **`IOrchestrationStrategy`**:

```csharp
interface IOrchestrationStrategy
{
    Task<Message?> HandleMessageAsync(Message message, IUnitContext context);
}
```

Where `IUnitContext` provides access to members, directory, policies, connectors, and workflow state. The strategy decides how to route, assign, and coordinate work. The strategy can be swapped independently of the unit's identity — e.g., upgrading from rule-based to AI-orchestrated as a team matures.

Three concrete implementations of `IOrchestrationStrategy` ship today. Each is registered under its own DI key in `AddCvoyaSpringDapr` (`"ai"` is the unkeyed default; `"workflow"` and `"label-routed"` are selected explicitly). `UnitActor` resolves the strategy per message through `IOrchestrationStrategyResolver`, which consults the unit's declared strategy key from the manifest and — when one isn't declared — falls back to policy-based inference and finally the unkeyed default (#491).

| Strategy (DI key)              | Concrete type                            | How it routes                                                                                                                                                          | AI Involvement | Example                                   |
| ------------------------------ | ---------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ----------------------------------------- |
| **AI-orchestrated** (`ai`)     | `AiOrchestrationStrategy`                | A single LLM call receives the message + member list and returns the target member address. Default strategy.                                                          | Full           | Software dev team with intelligent triage |
| **Workflow** (`workflow`)      | `WorkflowOrchestrationStrategy`          | Runs a workflow container (with a co-launched Dapr sidecar). The container drives the sequence; its stdout decides routing.                                            | None (minimal) | CI/CD pipeline, compliance review         |
| **Label-routed** (`label-routed`) | `LabelRoutedOrchestrationStrategy`    | Reads the unit's `UnitPolicy.LabelRouting` slot, matches payload labels against the trigger map (case-insensitive set intersection; first payload label hit wins), forwards to the mapped member. Drops the message when the unit has no label-routing policy, the payload carries no labels, or the matched path is not a current member. | None           | GitHub issue triage where humans assign work by label (#389) |

The strategy pattern is intentionally open — a host can register its own `IOrchestrationStrategy` under a new DI key without touching core code.

Matching semantics and design rationale for label routing are captured in [ADR-0007](../decisions/0007-label-routing-match-semantics.md). Status-label roundtrip (the connector applying `AddOnAssign` / `RemoveOnAssign` after a successful assignment) is wired via the activity-event bus as described in [ADR-0009](../decisions/0009-github-label-roundtrip-via-activity-event.md): the strategy publishes a `DecisionMade` event with `decision = "LabelRouted"` and the GitHub connector subscribes to it. The per-message resolution protocol that honours the manifest key is captured in [ADR-0010](../decisions/0010-manifest-orchestration-strategy-selector.md).

**Workflow patterns within a workflow strategy** — sequential, parallel, fan-out/fan-in, conditional, human-in-the-loop — are driven by the workflow engine inside the container; see [Workflows](workflows.md) for the full pattern catalogue.

### Selecting a Strategy from YAML

A unit manifest can pin the strategy explicitly via an `orchestration.strategy` key (#491). The value is the DI key the platform should resolve — `ai`, `workflow`, `label-routed`, or any key a host has registered under `AddKeyedScoped<IOrchestrationStrategy, ...>` / `AddKeyedSingleton<...>`:

```yaml
unit:
  name: issue-triage
  orchestration:
    strategy: label-routed
  members:
    - agent: backend-engineer
    - agent: qa-engineer
```

`UnitCreationService` persists the declared key onto the unit's `UnitDefinitions.Definition` JSON at manifest ingestion. On each domain message `UnitActor` consults `IOrchestrationStrategyResolver`, which resolves in the following precedence:

1. **Manifest key** — the `orchestration.strategy` value, when a DI registration matches.
2. **Policy inference** — `label-routed`, when `UnitPolicy.LabelRouting` is non-null on a unit whose manifest did not declare a strategy (ADR-0007 revisit criterion). An operator who has already configured label routing through `spring unit policy label-routing set` does not need to also add an `orchestration` block to the manifest.
3. **Unkeyed default** — the `IOrchestrationStrategy` registered without a key (the platform's `ai` strategy by default; a private-cloud host can override via `TryAdd*`).

An unknown manifest key (declared but not registered in DI) is a misconfiguration, not a routing bug: the resolver logs a warning, drops to the policy inference, and then to the unkeyed default so messages keep flowing while the operator corrects the manifest. The resolver creates a fresh DI scope per message so scoped strategies — `LabelRoutedOrchestrationStrategy` in particular — pick up hot `IUnitPolicyRepository` edits without actor recycling.

### Direct API / CLI Surface

The manifest-persisted `orchestration.strategy` slot is also addressable directly, without needing a full `spring apply` re-apply (#606):

- `GET /api/v1/units/{id}/orchestration` — returns `{ "strategy": "..." }` or `{ "strategy": null }` when no key is declared.
- `PUT /api/v1/units/{id}/orchestration` with body `{ "strategy": "ai" | "workflow" | "label-routed" }` — writes the slot in place, preserving every other property on the unit's `Definition` JSON.
- `DELETE /api/v1/units/{id}/orchestration` — strips the slot so the resolver falls back to policy inference / the unkeyed default.

Writes fire `IOrchestrationStrategyCacheInvalidator.Invalidate(actorId)` so the per-message resolver picks up the change on the next dispatch instead of waiting for the provider cache's TTL. The same `IUnitOrchestrationStore` seam is consumed by `UnitCreationService` at manifest ingestion, so a YAML apply and a direct `PUT` produce wire-identical on-disk shape.

CLI parity for the three verbs:

```
spring unit orchestration get <unit>
spring unit orchestration set <unit> --strategy {ai|workflow|label-routed} [--label-routing <file>]
spring unit orchestration clear <unit>
```

`set --label-routing <file>` is a UI-parity convenience that co-applies a `UnitPolicy.LabelRouting` block through the existing `/api/v1/units/{id}/policy` endpoint so a scripted operator can edit strategy + label routing in one invocation (matching the Orchestration portal tab's two-card layout). Host-registered custom strategy keys are tracked under #605 — today the CLI whitelists only the platform-offered set so `--help` stays authoritative.

---

## Unit Boundary

When a unit participates as a member of a parent, its **boundary** controls what is visible to the outside.

**Opacity levels:**


| Level           | Behavior                                                                                              |
| --------------- | ----------------------------------------------------------------------------------------------------- |
| **Transparent** | Parent sees all members, their capabilities, expertise, and activity streams. Internal structure fully visible.       |
| **Translucent** | Parent sees a filtered/projected subset. Boundary defines what is exposed.                            |
| **Opaque**      | Parent sees the unit as a single agent. No internal structure visible. All capabilities are synthesized from members. |


**Boundary operations:**

- **Projection** — Expose a subset of member capabilities as the unit's own. E.g., the engineering team exposes "implement feature" and "review PR" but hides internal "run CI" and "deploy staging."
- **Filtering** — Route only certain message types through the boundary. Internal status updates stay internal. Only completed results, errors, and escalations propagate outward.
- **Synthesis** — Create new virtual capabilities by combining members. E.g., "full-stack implementation" is not a capability of any single member but emerges from the combination of backend + frontend + QA agents.
- **Aggregation** — Expertise profiles are aggregated from all members. Activity streams are merged and optionally filtered before exposing to the parent.

**Deep access with permissions:** Despite encapsulation, a human or agent with appropriate permissions can address any agent at arbitrary depth. The boundary is a default, not a wall. Permission-based deep access uses the actor's `Guid` directly (e.g., `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`); the directory walks the membership graph from the addressed actor toward the tenant root and checks the requester's permissions at each boundary edge before routing.

**Hierarchy-aware permission resolution (#414).** Permission checks walk up the parent chain by default — a human who is `Owner` or `Operator` on a parent unit is treated as having at least that permission on every descendant unit unless something along the path blocks the walk. Each unit carries a `UnitPermissionInheritance` flag (`Inherit` by default, `Isolated` to opt out). An isolated unit is the permission-layer analogue of an opaque boundary: ancestor authority does not flow through it, but direct grants on the unit and its descendants still work normally. Direct grants always override inheritance — a child that explicitly grants `Viewer` is never silently promoted to `Owner` by a higher ancestor grant. See [Security § Hierarchy-aware permission resolution](security.md#hierarchy-aware-permission-resolution-414) for the full rules.

```yaml
unit:
  boundary:
    opacity: translucent
    projections:
      - capability: implement-feature
        maps_to: [ada.implement, kay.implement]
      - capability: review-code
        maps_to: [hopper.review]
    filters:
      outbound: [completed, error, escalation]
      inbound: [query, control]
    deep_access:
      policy: permission-required       # permission-required | deny-all | allow-all
```

### Boundary Configuration (#413)

The boundary is a declarative record attached to the unit and stored on the unit actor (`StateKeys.UnitBoundary`). It's surfaced through `IUnitBoundaryStore` and composed over the aggregator via the `BoundaryFilteringExpertiseAggregator` decorator. The boundary has three slot types, each a nullable collection — a boundary with every slot empty is equivalent to "transparent" and the decorator is a straight pass-through.

| Slot | Rule type | Effect on outside callers |
|------|-----------|---------------------------|
| `Opacities` | `BoundaryOpacityRule(DomainPattern?, OriginPattern?)` | Every matching `ExpertiseEntry` is removed from the outside view. Rules OR together. |
| `Projections` | `BoundaryProjectionRule(DomainPattern?, OriginPattern?, RenameTo?, Retag?, OverrideLevel?)` | Matching entries are rewritten (new name / description / level). First matching rule wins. Origin and path are preserved so permission checks (#414) still see the true contributor. |
| `Syntheses` | `BoundarySynthesisRule(Name, DomainPattern?, OriginPattern?, Description?, Level?)` | Matching entries are removed and replaced with a single synthesised entry attributed to the unit (`Origin = unit`, `Path = [unit]`). When no member matches the rule, the synthesised capability is **not** fabricated. |

**Matching patterns** are case-insensitive and support a single trailing `*` (prefix match). `OriginPattern` matches against an entry's `Origin` field, which is the contributor's address in `scheme:<id>` form (e.g. `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`); patterns may target a specific id (`agent:8c5fab2a*`) or any id of a given scheme (`agent:*`).

**Rule precedence.** Opacity wins over projection and synthesis — a matched opaque entry is gone, not rewritten. Synthesis runs before projection so the raw entries consumed by a synthesis rule never flow through the projection stage.

**Caller-aware filtering.** The decorator reads `BoundaryViewContext`:

- `BoundaryViewContext.External` (default) — outside caller; opacity / projection / synthesis apply.
- `BoundaryViewContext.InsideUnit` — the unit itself or a descendant; the raw aggregator output is returned verbatim.

PR-PLAT-BOUND-3 (#414) consumes this seam to decide the caller's identity from the authenticated principal. Until then, HTTP callers are treated as external.

**Write path.** The unit actor persists the boundary through `SetBoundaryAsync`; an empty boundary is represented as an absent state row. HTTP and CLI writes call `IExpertiseAggregator.InvalidateAsync` so the next aggregate read sees fresh rules.

**Operator surface.**

- **HTTP** — `GET / PUT / DELETE /api/v1/units/{id}/boundary`. The empty shape is always returned for units that have never had a boundary persisted, so callers never need to branch on 404 vs empty-boundary.
- **CLI** — `spring unit boundary get|set|clear`. `set` accepts `--opaque`, `--project`, `--synthesise` repeatable flags (comma-separated key=value pairs) or a YAML fragment via `-f`. `clear` removes every rule.
- **Portal** — the unit detail page at `/units/{id}` ships a **Boundary** tab (#495) that mirrors the three dimensions one-to-one with the CLI. Save PUTs the full boundary; Clear issues DELETE. See [docs/guide/portal.md §Boundary](../guide/user/portal.md#boundary).
- **YAML manifest (#494)** — `unit.boundary` follows the same three-list shape (`opacities` / `projections` / `syntheses`), so an operator can check the config in alongside `members` / `policies` and a single `spring apply -f unit.yaml` is wire-equivalent to a subsequent `spring unit boundary set -f`. `ApplyRunner` PUTs the boundary after create; the package-install activator writes it through `IUnitBoundaryStore` in `UnitCreationService.CreateFromManifestAsync`. An absent or all-empty `boundary:` block is a no-op — the unit keeps the default "transparent" view.

```yaml
unit:
  name: triage-cell
  boundary:
    opacities:
      - domain_pattern: internal-*
      - origin_pattern: agent:*                # all agent-scheme contributors
    projections:
      - domain_pattern: backend-*
        rename_to: engineering
        override_level: advanced
    syntheses:
      - name: full-stack
        domain_pattern: frontend
        level: expert
        description: team-level full-stack coverage
```

Synthesis entries with a blank / missing `name:` are silently dropped so a misspelled manifest never fabricates an empty team capability. Unknown `override_level` / `level` strings resolve to `null` rather than failing deserialisation — matches the HTTP DTO so operators can copy values verbatim between the two surfaces.

---

## Unit Execution Defaults and the Agent → Unit → Fail Resolution Chain (#601 B-wide)

Each unit owns an optional `execution:` block that acts as the **default container-runtime configuration** inherited by member agents. The block carries five fields:

| Field      | Semantics                                                                                          |
| ---------- | -------------------------------------------------------------------------------------------------- |
| `image`    | Container image reference (e.g. `ghcr.io/...:tag`, `spring-agent:latest`).                         |
| `runtime`  | Container runtime (`docker` or `podman`).                                                          |
| `tool`     | External agent tool identifier (`claude-code`, `codex`, `gemini`, `dapr-agent`, `custom`).         |
| `provider` | LLM provider. Meaningful only when `tool = dapr-agent` (#598 gating).                              |
| `model`    | Model identifier. Meaningful only when `tool = dapr-agent` (#598 gating).                          |

Every field is **independently optional and independently clearable** — a unit can declare only `runtime: podman` and leave `image`, `tool`, etc. for each member agent to provide.

**Resolution chain per field.** At dispatch time `IAgentDefinitionProvider` merges the agent's own declared block with its parent unit's defaults:

1. **Agent wins** when the agent sets the field (non-null / non-whitespace).
2. Otherwise the **unit default** fills in.
3. Otherwise the field is null; the dispatcher fails cleanly at dispatch or the save-time validator rejects the configuration (required fields: `image` under ephemeral hosting, `tool` always).

`hosting` (ephemeral vs persistent) is **agent-exclusive** — a unit cannot change whether an agent is ephemeral or persistent.

**Tool-specific gating.** `provider` and `model` are only meaningful when the resolved `tool` is `dapr-agent`. The portal's Execution tab hides those fields when another tool is selected; the CLI accepts them unconditionally but they are ignored at dispatch for non-`dapr-agent` launchers. This matches the symmetric gating on unit creation from #598.

**Save-time validation.** The portal and CLI reject a save whenever ephemeral hosting is declared on an agent and no resolvable image exists on either the agent or the unit. This surfaces the error when the operator is still editing rather than deferring to dispatch.

**Persistence and surfaces.**

- **Wire shape.** Both HTTP (`GET / PUT / DELETE /api/v1/units/{id}/execution`, `/api/v1/agents/{id}/execution`) and manifest apply write through `IUnitExecutionStore` / `IAgentExecutionStore` so the on-disk JSON cannot drift between the two entry points.
- **Manifest.** A unit YAML's `execution:` block is persisted on `UnitDefinitions.Definition` under `execution`; the manifest applier no longer warns "unsupported section" for it.
- **CLI.** `spring unit execution get|set|clear` and `spring agent execution get|set|clear` with `--image / --runtime / --tool / --provider / --model` (plus `--hosting` on the agent verb). `clear` without arguments strips the whole block; `clear --field X` clears one field.
- **Portal.** A dedicated Execution tab on the unit detail page and an Execution panel on the agent detail page (delivered in the companion portal PR).

**Extension seams (future).** Two seams are reserved for follow-up issues and **not implemented** in the B-wide PR:

- **#622 — `IImageReferenceHistory`** — a scoped-registered seam so a hosted downstream can partition recently-dispatched image references per tenant. Shape 2 of the image-selection UX: autocomplete suggestions on the `Image` text field.
- **#623 — registry integration** — Shape 3: discover images directly from a configured container registry (GHCR / GCR / ECR / Harbor / Quay). Marked `needs-thinking` pending auth-scope and caching decisions.

Both are deferred work; today ships Shape 1 (plain text input) with the inheritance merge and the save-time validation.

---

## See Also

- [Units](units.md) — unit entity model, membership, nested units
- [Agents](agents.md) — agent model, cloning, prompt assembly
- [Policies](policies.md) — unit policy framework (skill, model, cost, execution mode, initiative constraints)
- [Workflows](workflows.md) — workflow patterns within a workflow strategy
- [ADR-0007](../decisions/0007-label-routing-match-semantics.md) — label-routing match semantics
- [ADR-0009](../decisions/0009-github-label-roundtrip-via-activity-event.md) — GitHub label roundtrip via activity event
- [ADR-0010](../decisions/0010-manifest-orchestration-strategy-selector.md) — manifest orchestration-strategy selector protocol
