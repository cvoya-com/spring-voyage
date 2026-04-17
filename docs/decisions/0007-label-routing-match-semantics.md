# 0007 — Label-routing match semantics for `LabelRoutedOrchestrationStrategy`

- **Status:** Accepted — case-insensitive set intersection; first payload label wins; `UnitPolicy.LabelRouting` carries the config.
- **Date:** 2026-04-17
- **Closes:** [#389](https://github.com/cvoya-com/spring-voyage/issues/389)
- **Related code:** `src/Cvoya.Spring.Core/Policies/LabelRoutingPolicy.cs`, `src/Cvoya.Spring.Core/Policies/UnitPolicy.cs`, `src/Cvoya.Spring.Dapr/Orchestration/LabelRoutedOrchestrationStrategy.cs`, `src/Cvoya.Spring.Cli/Commands/UnitPolicyCommand.cs`, `src/Cvoya.Spring.Host.Api/Models/PolicyModels.cs`

## Context

v1 let humans assign work to a unit by applying labels (e.g. `agent:backend`); the unit only picked up work that carried one of its configured labels. v2 shipped `AiOrchestrationStrategy` and `WorkflowOrchestrationStrategy` first, and #389 closes the gap by adding label-routing as a third strategy.

Three design questions needed answers before the strategy could ship:

1. **What is the match spec?** Exact equality, prefix, glob, or regex?
2. **Where does the trigger-label config live?** A new top-level unit-manifest block, or hanging off the existing `UnitPolicy` record that PR-C2 (#453) already routes through the unified `spring unit policy` surface?
3. **What happens on an un-matched message?** Fallback to the default AI strategy, or drop?

#462 explicitly called out the coordinate-with direction: "`spring unit policy` includes label routing". The v1 behaviour is "drop un-tagged work" — humans decide what the unit sees.

## Decision

**Case-insensitive set intersection over payload labels and `TriggerLabels` keys; first payload label that hits the map wins; drop every un-matched or un-configured message. Configuration lives on `UnitPolicy.LabelRouting`.**

### Match spec

- Labels are compared with `StringComparer.OrdinalIgnoreCase`. Most label vocabularies on GitHub/Slack are lower-kebab by convention; case sensitivity bugs would be a constant source of "works-on-my-machine" failures.
- The match is **set intersection**, not prefix or glob. A policy like `{"agent:backend": "backend-engineer"}` matches exactly `agent:backend`. Operators wanting glob-like behaviour can enumerate the variants they care about — the dictionary is cheap.
- Ordering: the strategy iterates **payload labels in order** and returns on the first dictionary hit. That means the upstream connector's label order influences precedence — for GitHub this is apply order, which is stable and observable. Iterating the policy's declared order instead would have made precedence invisible to the human applying labels.
- A matched label → path that is not a current member of the unit is a **no-op drop**, not an error. This protects against rename drift: a sub-unit gets renamed, the policy lags, the unit stops picking up the label but nobody leaks work onto the wrong agent.

Alternatives considered and rejected:

- **Prefix / glob matching.** Added DSL surface for no real v1 use-case. If a future domain wants it, it can replace the match function without reshaping the slot.
- **Regex.** Same as above, plus ReDoS risk inside a hot orchestration path.
- **Many-to-one sets (label → [members])** with a tie-breaker. #389 explicitly asks for a scalar map; fan-out is the `AiOrchestrationStrategy`'s job.

### Config location

The trigger-label map hangs off a new nullable slot `LabelRouting` on `UnitPolicy`. Rationale:

- PR-C2 (#453) made `spring unit policy <dim> get|set|clear` the operator-facing surface for every dimension; adding a sixth slot keeps the command tree uniform instead of minting a parallel `spring unit labels` verb group.
- The persistence and wire shape already tolerate additive dimensions (each dimension is a nullable jsonb column / nullable record field). Adding a sixth slot is a purely additive schema change.
- Enforcers (`IUnitPolicyEnforcer`) walk only the first five governance slots and ignore `LabelRouting`. The slot is a routing **input** to the orchestration strategy, not a governance constraint — it is just co-located with the governance record because the edit path is shared.

Alternatives considered and rejected:

- **Top-level unit-manifest `orchestration.config` block.** Would have duplicated the edit surface: operators who already know `spring unit policy` would have had to learn a second verb group and a second endpoint. Adds API breadth for no user-facing benefit.
- **Per-strategy DI-options block bound from appsettings.** Works for process-wide defaults, fails the "each unit configures its own triggers" requirement.

### Un-matched drop

- No label on the payload → drop. Matches v1 ("humans assign work by labels").
- A label on the payload that is not in the trigger map → drop.
- A matched path not currently in `context.Members` → drop.

No fallback to AI. Operators who want hybrid routing can compose: register a decorator `IOrchestrationStrategy` that tries `"label-routed"` first and falls back to `"ai"` on null. Shipping the composition built-in would have hard-coded a policy that belongs in the host.

### Payload-shape tolerance

The strategy extracts labels from two shapes, because both arrive in real deployments:

- `{ "labels": ["agent:backend"] }` — bare string array, produced by direct producers.
- `{ "labels": [{"name": "agent:backend"}, ...] }` — the GitHub webhook shape.

Other shapes (missing `labels`, non-array, nested differently) are treated as "no labels" and the message is dropped. Expanding to other connector shapes is additive and does not require reshaping `LabelRoutingPolicy`.

## Consequences

- **DI registration.** The strategy is a keyed scoped service under `"label-routed"`. Scoped because it depends on `IUnitPolicyRepository`; the per-turn policy read picks up hot edits without actor recycling. The unkeyed default remains `AiOrchestrationStrategy` for backward compatibility; a manifest-driven strategy selector is tracked under #491.
- **Status-label roundtrip.** `AddOnAssign` / `RemoveOnAssign` are captured on the policy but not applied by the strategy itself — the connector owns the external credentials and is the natural place to apply labels after a successful dispatch. Wiring the GitHub connector to consume these fields is tracked under #492 so this PR stays scoped to routing.
- **Enforcer surface is unchanged.** `IUnitPolicyEnforcer` implementations (including private-cloud decorators) continue to walk the first five governance slots; they do not need to know `LabelRouting` exists.
- **Wire BC.** Every existing `GET /api/v1/units/{id}/policy` caller keeps working — `labelRouting` is a new nullable field. Existing persisted rows round-trip as `LabelRouting: null`.

## Revisit criteria

- **More than one connector needs different label-extraction shapes.** When the third connector's payload shape lands, hoist label extraction into an injected `ILabelExtractor` so each connector can contribute its own shape.
- **Fan-out semantics become a real use-case.** If we see repeated requests for "one label, multiple assignees", reshape `TriggerLabels` to `Dictionary<string, IReadOnlyList<string>>` and bump the wire version — keep the scalar map as a legacy decode path.
- **The manifest-driven strategy selector lands (#491).** At that point `LabelRouting` should imply `strategy: label-routed` by default so operators don't have to set both.
