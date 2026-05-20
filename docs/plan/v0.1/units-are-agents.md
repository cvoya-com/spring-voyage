# Units are agents — execution plan

> **Amendment (2026-05-19, [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)) — orchestration is messaging, no separate gates.** Several Phase D rules are superseded:
>
> 1. The "caller is a unit (`unit://` scheme)" gate (D8 / D13) is **removed**. The platform makes no entity-type assumption about who can orchestrate.
> 2. The "tools attached only when children exist" gate (D2 / launcher attachment) is **removed**. `DirectoryOrchestrationToolProvider` now always returns the orchestration tool set for `agent://` and `unit://` addresses; the `IUnitMemberGraphStore` dependency on that provider is dropped.
> 3. The "target is a direct child" gate (D8 / D13) is **removed**. A caller may target any addressable entity in the same tenant. The `OrchestrationTargetNotChild` reject code is dropped.
> 4. Tool wire names rename: `list_members`, `inspect`, `delegate_to`, `fanout_to`, `query_status` (C# enum: `ListMembers`, `Inspect`, `DelegateTo`, `FanoutTo`, `QueryStatus`). C# types rename: `OrchestrationChildDescriptor` → `OrchestrationMemberDescriptor`, `ChildStatusResult` → `MemberStatusResult`.
>
> **Amendment (2026-05-19, [#2537](https://github.com/cvoya-com/spring-voyage/issues/2537)) — orchestration surface shrunk to action verbs.** The closed orchestration tool set is now **2 tools** (`delegate_to`, `fanout_to`), not 5. The `list_members`, `inspect`, and `query_status` tools are removed from the orchestration surface — their semantics are already covered by the `sv.*` directory tools exposed via `SvDirectorySkillRegistry` (`sv.list_members`, `sv.get_member`, `sv.get_status`, plus `sv.get_siblings` / `sv.get_parents` / `sv.get_self`). Consequences: `OrchestrationToolName` keeps only `DelegateTo` / `FanoutTo`; `OrchestrationDecisionKind` keeps only `Delegate` / `Fanout`; `OrchestrationMemberDescriptor` / `MemberStatusResult` / `IUnitActor.GetMemberDescriptorsAsync` are deleted; dispatcher routes `/list-members`, `/inspect`, `/query-status` are removed. The historical D6 / D8 / D10 / D11 / D13 prose for the removed tools is superseded.
>
> The dispatcher's only scheme check is now "scheme is `unit://` or `agent://`" with reject code `UnsupportedCallerScheme`. The handler-side surviving gates are token validity (auth), cross-tenant, self-delegation, and per-thread depth.
>
> The historical Phase D prose below preserves the original gate / name wording; treat it as superseded by these amendments when reading.

**Initiative.** Implementation of [ADR-0039 — Units are agents (orchestration is runtime behaviour, not platform configuration)](../../decisions/0039-units-are-agents.md). Tracked under [#1786](https://github.com/cvoya-com/spring-voyage/issues/1786). Subsumes the agent-create UX redesign tracked under [#1763](https://github.com/cvoya-com/spring-voyage/issues/1763); the design contract for the UX-side tasks lives at [`docs/design/v0.1/agent-create-redesign.md`](../../design/v0.1/agent-create-redesign.md).

This plan is structured for execution by **less-capable code-generation agents** that do not need to understand the architecture. Every task is concrete, narrow, and testable; system-design thinking is done up front and lives in the ADR + design doc. A task picker reads the task, follows the file paths and signatures it specifies, executes the deliverable, and verifies the acceptance criteria. Cross-task coordination is expressed as **GitHub `blockedBy` edges set structurally** on each task issue (not in prose) so a downstream agent never starts a task whose prerequisites have not landed.

## Conventions

- **One task = one PR.** No batching, no "while we're here." If a task has a follow-up, file a new task.
- **Acceptance criteria are testable.** A bullet under "Acceptance" is something the agent or CI can mechanically verify (build clean, test passes, file exists, function behaves).
- **Tasks reference the ADR + design doc by section, not by paraphrase.** "See ADR-0039 §3" is enough; the agent reads it.
- **Every task issue is filed as a sub-issue of the [#1786 umbrella](#github-issue-filing-plan), labelled `area:units-are-agents`, type `Task`.**
- **`blockedBy` is structural, not prose.** The relationship is set via the GraphQL `addSubIssue` + native blocked-by edges (or `gh-app issue create --blocked-by N`), not stated in the issue body. CI's milestone view and the parent's sub-issue panel surface the dependencies; the body stays thin.
- **Aggressive cleanup; no back-compat.** When deleting code, delete the tests too. No `_legacyFoo` shims, no `// removed in 1786` markers, no deprecation paths beyond what ADR-0039 §9 lists.

## Phases at a glance

| Phase | Bucket | Tasks | Independent of orchestration work? |
|---|---|---|---|
| A | Core types added (additive) | 5 | — (foundation) |
| B | Multi-parent inheritance validation at endpoints | 9 | depends on A |
| C | UnitActor invokes runtime launcher | 6 | depends on A |
| D | Children-as-tools + OrchestrationDecision events + SDK surface | 18 | depends on C |
| E | Delete strategy taxonomy + LabelRoutingPolicy | 18 | depends on D |
| F | GitHub connector rewrite | 5 | depends on D + E |
| G | Container-runtime field removal | 9 | independent (depends on A only) |
| H | `spring agent create` positional `<id>` removal | 4 | independent (depends on A only) |
| I | Agent-create form schema + shared `<AgentCreateForm>` | 8 | depends on B |
| J | Unit-tab dialog rewrite | 6 | depends on I |
| K | Three-paths Source step + drop YAML synthesis | 10 | depends on J |
| L | CLI agent-create parity | 9 | depends on H + B |
| M | Docs overhaul | 5 | depends on D + E |

**Total: 112 tasks.** Phases G, H run in parallel from the start of the initiative. Phases I–L are the UX track, weakly coupled to platform work — they begin once Phase B lands. Phase D includes both the LLM tool-call surface (D1–D11) and the SDK surface (D12–D18) — same handlers, two transports, per ADR-0039 §3.

## Task catalogue

Format. Each task lists **Files**, **Deliverable** (what to write / change), **Acceptance** (mechanically verifiable), **Blocked by** (predecessor task ids, none = open). The agent picks up a task only when every "Blocked by" entry is closed.

---

### Phase A — Core types added (additive)

**A1 — Add `IOrchestrationToolProvider` interface and supporting types**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/IOrchestrationToolProvider.cs`, `src/Cvoya.Spring.Core/Orchestration/OrchestrationToolDescriptor.cs`, `src/Cvoya.Spring.Core/Orchestration/OrchestrationToolName.cs`.
- **Deliverable.** `IOrchestrationToolProvider` interface with `OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, Guid threadId)`. `OrchestrationToolDescriptor` sealed record with `Name` (`OrchestrationToolName`), `JsonElement InputSchema`, `JsonElement OutputSchema`. `OrchestrationToolName` closed enum: `ListChildren`, `InspectChild`, `DelegateToChild`, `FanoutToChildren`, `QueryChildStatus`. Match exact spelling from ADR-0039 §3 (`list_children` / `inspect_child` / `delegate_to_child` / `fanout_to_children` / `query_child_status` are the wire names; the C# enum members are PascalCase).
- **Acceptance.** Build clean. No DI registration. No call sites yet.
- **Blocked by.** —

**A2 — Add `OrchestrationDecision` record and supporting enums**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/OrchestrationDecision.cs`, `src/Cvoya.Spring.Core/Orchestration/OrchestrationDecisionKind.cs`, `src/Cvoya.Spring.Core/Orchestration/OrchestrationDecisionStatus.cs`.
- **Deliverable.** Records and enums match ADR-0039 §4 verbatim — `OrchestrationDecision(Guid DecisionId, Guid TenantId, Address UnitAddress, Guid ThreadId, Guid InputMessageId, OrchestrationDecisionKind Kind, Address[] Targets, OrchestrationDecisionStatus Status, Guid[] ResultMessageIds, string? Reason, JsonElement? Metadata, DateTimeOffset CreatedAt)`. `OrchestrationDecisionKind`: `Delegate`, `Fanout`, `Inspect`, `NoOp`. `OrchestrationDecisionStatus`: `Accepted`, `Routed`, `Failed`.
- **Acceptance.** Build clean. No producers, no consumers yet.
- **Blocked by.** —

**A3 — Add `IExecutionConfigInheritanceResolver` and supporting records**

- **Files:** `src/Cvoya.Spring.Core/Agents/IExecutionConfigInheritanceResolver.cs`, `src/Cvoya.Spring.Core/Agents/InheritanceResolution.cs`, `src/Cvoya.Spring.Core/Agents/ParentValue.cs`.
- **Deliverable.** Interface and records match ADR-0039 §6 verbatim. `InheritanceResolution(AgentExecutionConfig Effective, IReadOnlyDictionary<string, IReadOnlyList<ParentValue>> ConflictingFields)`. `ParentValue(UnitId Source, string Value)`. The interface signature: `InheritanceResolution ResolveAgentConfig(AgentExecutionConfig agentOwn, IReadOnlyList<UnitId> parentUnitIds, TenantId tenantId, CancellationToken ct);`.
- **Acceptance.** Build clean. No DI registration. No call sites.
- **Blocked by.** —

**A4 — Default `EmptyOrchestrationToolProvider` implementation + DI registration**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/EmptyOrchestrationToolProvider.cs`, `src/Cvoya.Spring.Dapr/DependencyInjection/ServiceCollectionExtensions.Orchestration.cs` (add registration).
- **Deliverable.** `EmptyOrchestrationToolProvider : IOrchestrationToolProvider` returns `Array.Empty<OrchestrationToolDescriptor>()` for every input. Registered as the default `IOrchestrationToolProvider` in DI. Will be replaced by `DirectoryOrchestrationToolProvider` in task D2; this is a placeholder that lets later tasks consume the interface without a full implementation.
- **Acceptance.** Build clean. DI test resolves `IOrchestrationToolProvider` and gets the empty provider.
- **Blocked by.** A1.

**A5 — Default `ExecutionConfigInheritanceResolver` implementation + tests + DI registration**

- **Files:** `src/Cvoya.Spring.Dapr/Agents/ExecutionConfigInheritanceResolver.cs`, `tests/unit/Cvoya.Spring.Dapr.Tests/Agents/ExecutionConfigInheritanceResolverTests.cs`, DI registration in `ServiceCollectionExtensions.cs` (or appropriate extension).
- **Deliverable.** Implementation reads each parent unit's effective config, intersects per field. Tests cover four cases per ADR-0039 §6: zero parents (tenant fallback), one parent (inherit), N parents identical (inherit), N parents diverging (returns `ConflictingFields` with the diverging field name → list of `(unitId, value)` pairs).
- **Acceptance.** Test class has at least four `[Theory]` cases pinning each branch. Build + tests green.
- **Blocked by.** A3.

---

### Phase B — Multi-parent inheritance validation at endpoints

**B1 — Wire resolver into `POST /api/v1/tenant/agents` (create)**

- **Files:** `src/Cvoya.Spring.Host.Api/Endpoints/AgentEndpoints.cs` (`CreateAgentAsync`), associated test file.
- **Deliverable.** Before writing the agent, resolve inherited config. If `InheritanceResolution.ConflictingFields.Any()`, return 422 with `{ "error": "MultiParentInheritanceConflict", "conflictingFields": { … } }`. Otherwise proceed.
- **Acceptance.** Integration test: create agent with two units that resolve different runtimes, leave runtime inherited → 422 with the structured body. Set runtime explicitly → 201.
- **Blocked by.** A5.

**B2 — Wire resolver into `PUT /api/v1/tenant/agents/{id}/execution`**

- **Files:** `src/Cvoya.Spring.Host.Api/Endpoints/AgentEndpoints.cs` (execution-update path), test file.
- **Deliverable.** Same shape as B1 but on update. The "agent's own config" includes the patched values; resolution runs against the patched-in values + current parent set.
- **Acceptance.** Integration test as above for the update path.
- **Blocked by.** A5.

**B3 — Wire resolver into `POST /api/v1/units/{id}/agents/{agentId}` (assign)**

- **Files:** `src/Cvoya.Spring.Host.Api/Endpoints/MembershipEndpoints.cs` or `UnitEndpoints.cs` (locate the assignment endpoint), test file.
- **Deliverable.** When assigning an agent that is already inheriting (no own config) to a parent set that diverges, reject with the same 422 shape.
- **Acceptance.** Integration test: agent inherits in unit X with runtime claude-code; assign to unit Y with runtime spring-voyage → 422.
- **Blocked by.** A5.

**B4 — Wire resolver into `DELETE /api/v1/units/{id}/agents/{agentId}` (un-assign)**

- **Files:** Same as B3.
- **Deliverable.** When un-assigning, recompute inheritance against the *remaining* parent set. If the remaining set still diverges and the agent inherits, reject with 422. (If the remaining set is consistent, accept; if the remaining set is empty, the agent becomes top-level — accept.)
- **Acceptance.** Integration test pinning the three branches.
- **Blocked by.** A5.

**B5 — Wire resolver into unit-side sub-unit assign**

- **Files:** Locate the sub-unit assignment endpoint (likely `UnitEndpoints.cs` or a sub-unit-specific endpoint), test file.
- **Deliverable.** Same rule applied to a unit-as-member: a unit with multiple parent units must have consistent resolved config or its own.
- **Acceptance.** Integration test: unit-as-member, two parent units with diverging configs, inherit → 422.
- **Blocked by.** A5.

**B6 — Wire resolver into unit-side sub-unit un-assign**

- **Files:** Same as B5.
- **Deliverable.** Mirror B4 for unit-as-member.
- **Acceptance.** Integration test pinning the three branches.
- **Blocked by.** A5.

**B7 — `spring agent create` surfaces the structured 422**

- **Files:** `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs` (`CreateCreateCommand`), CLI scenario test.
- **Deliverable.** Parse the 422 body; print one line per conflicting field naming the parent values: `runtime: unit-engineering=claude-code, unit-support=spring-voyage`. Exit code per `CONVENTIONS.md`.
- **Acceptance.** CLI scenario test pinning the printed shape.
- **Blocked by.** B1, B3.

**B8 — `spring units add <unit-id> --agent <agent-id>` surfaces the structured 422**

- **Files:** `src/Cvoya.Spring.Cli/Commands/UnitsCommand.cs` (or wherever the agent-assign command lives), CLI scenario test.
- **Deliverable.** Same printed shape as B7.
- **Acceptance.** CLI scenario test.
- **Blocked by.** B3.

**B9 — Portal multi-parent inheritance conflict UX**

- **Files:** `src/Cvoya.Spring.Web/src/components/units/multi-parent-picker.tsx` (or wherever the multi-unit picker lives — locate during task), test file.
- **Deliverable.** When the form's parent-set + inherit values would conflict, surface an inline error per DESIGN.md §6.3 with `data-testid="multi-parent-inheritance-conflict"`. The error lists each diverging field and the conflicting parent values. Block the form's submit until the operator either trims the parent set or sets explicit config.
- **Acceptance.** Vitest covers conflict display; e2e (Playwright) covers blocked submit + unblock-on-resolve.
- **Blocked by.** B1.

---

### Phase C — UnitActor invokes runtime launcher

**C1 — Extract `IRuntimeInvocationPath` helper from `AgentActor`**

- **Files:** `src/Cvoya.Spring.Dapr/Actors/IRuntimeInvocationPath.cs` (new), `src/Cvoya.Spring.Dapr/Actors/RuntimeInvocationPath.cs` (new), `src/Cvoya.Spring.Dapr/Actors/AgentActor.cs` (refactor).
- **Deliverable.** `IRuntimeInvocationPath.InvokeAsync(Address subject, Message inbound, CancellationToken ct)` encapsulates resolve-config → resolve-skills → resolve-tools (via `IOrchestrationToolProvider`) → resolve-credentials → launch via `IAgentRuntimeLauncher` → publish response. `AgentActor.ReceiveAsync` delegates to it. Behaviour identical for agents (no functional change).
- **Acceptance.** Existing `AgentActor` tests pass unchanged. The helper is testable on its own.
- **Blocked by.** A4.

**C2 — Refactor `UnitActor.ReceiveAsync` to call `IRuntimeInvocationPath`**

- **Files:** `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs`.
- **Deliverable.** Drop the strategy-resolution path (`IOrchestrationStrategyResolver` lookup → `IOrchestrationStrategy.OrchestrateAsync`). Replace with `_runtimeInvocationPath.InvokeAsync(unitAddress, message, ct)`. The strategy code is *not yet deleted* (Phase E does that); the unit just stops calling it.
- **Acceptance.** Build clean. The strategy types still exist but are not consulted from `UnitActor`.
- **Blocked by.** C1.

**C3 — Update `IUnitActor.cs` doc comments**

- **Files:** `src/Cvoya.Spring.Dapr/Actors/IUnitActor.cs`.
- **Deliverable.** Drop the "Domain messages are delegated to the unit's configured `IOrchestrationStrategy`" sentence. Replace with: "Domain messages are dispatched through the unit's runtime launcher via the same path used by `IAgentActor`. When the unit has children, the launcher attaches orchestration tools (`list_children` / `inspect_child` / `delegate_to_child` / `fanout_to_children` / `query_child_status`) so the runtime can choose to delegate; the platform records each delegation as an `OrchestrationDecision` event (ADR-0039 §3, §4)."
- **Acceptance.** Doc-only change. Build clean.
- **Blocked by.** C2.

**C4 — Migrate `UnitActorTests` from strategy-mocking to runtime-mocking**

- **Files:** `tests/unit/Cvoya.Spring.Dapr.Tests/Actors/UnitActorTests.cs`.
- **Deliverable.** Replace mocks for `IOrchestrationStrategyResolver` and `IOrchestrationStrategy` with mocks for `IRuntimeInvocationPath`. Tests assert that `ReceiveAsync` invokes the runtime path with the unit's address.
- **Acceptance.** Tests green.
- **Blocked by.** C2.

**C5 — Add Tier-2 smoke test reproducing #1759**

- **Files:** `tests/integration/Tier2/UnitNoMembersResponds.cs` (new).
- **Deliverable.** A unit configured `(runtime: spring-voyage, model: ollama/llama3.2:3b)` with zero members receives a domain message and emits a non-null response. The test fails before C2 lands and passes after.
- **Acceptance.** Test green; documented in the integration-tests README under `Tier 2 smoke`.
- **Blocked by.** C2.

**C6 — Tier-2 smoke test for unit with members** *(temporary regression-allowance test)*

- **Files:** `tests/integration/Tier2/UnitWithMembersRespondsViaRuntime.cs` (new).
- **Deliverable.** A unit configured `(runtime: spring-voyage, model: ollama/llama3.2:3b)` with two member agents receives a domain message and the unit's *runtime* responds (no delegation yet — children-tooling is empty until D2). Test pins the temporary "responds directly" behaviour during the C2→D2 window.
- **Acceptance.** Test green. Test description explicitly notes: "after D2 lands, the runtime may instead delegate via `delegate_to_child`; that case is covered by D10."
- **Blocked by.** C2.

---

### Phase D — Children-as-tools + OrchestrationDecision events

**D1 — Implement `DirectoryOrchestrationToolProvider` + tests**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/DirectoryOrchestrationToolProvider.cs`, `tests/unit/Cvoya.Spring.Dapr.Tests/Orchestration/DirectoryOrchestrationToolProviderTests.cs`.
- **Deliverable.** Reads the directory; returns the closed orchestration-tool set when the agent has children, otherwise empty. The five descriptors carry the JSON schemas for input and output (ADR-0039 §3 lists them; the schemas live alongside the provider as `Resources/<tool-name>.input.schema.json` etc., loaded once at startup).
- **Acceptance.** Tests cover: leaf agent returns empty; unit with one child returns five descriptors; unit with three children returns five descriptors (the descriptors are static — only the *invocation* sees the children).
- **Blocked by.** A1.

**D2 — Replace DI registration: `EmptyOrchestrationToolProvider` → `DirectoryOrchestrationToolProvider`**

- **Files:** `src/Cvoya.Spring.Dapr/DependencyInjection/ServiceCollectionExtensions.Orchestration.cs`.
- **Deliverable.** One-line change: register the directory-backed implementation as the default. Delete `EmptyOrchestrationToolProvider.cs`.
- **Acceptance.** Build clean. The existing wiring tests pass against the new default.
- **Blocked by.** D1, C2.

**D3 — Extend `IAgentRuntimeLauncher` contract for orchestration tools**

- **Files:** `src/Cvoya.Spring.Core/Execution/IAgentRuntimeLauncher.cs` (or equivalent post-ADR-0038), every existing launcher implementation, integration test stubs.
- **Deliverable.** Add `OrchestrationToolDescriptor[] OrchestrationTools` field on the launcher's invocation context (or a parameter — match ADR-0025 conventions exactly). Each launcher accepts the field; the four built-in launchers initially ignore it (per-runtime attachment lands in D4–D7). The platform-side caller passes the descriptors from `IOrchestrationToolProvider`.
- **Acceptance.** Build clean. Existing launcher contract tests updated to pass the new field. No behaviour change yet.
- **Blocked by.** D1.

**D4 — Implement orchestration-tool attachment in `SpringVoyageAgentLauncher`**

- **Files:** `src/Cvoya.Spring.AgentRuntimes/Launchers/SpringVoyageAgentLauncher.cs`, integration test.
- **Deliverable.** When `OrchestrationTools` is non-empty, write the descriptor list to the env var `SPRING_ORCHESTRATION_TOOLS` (JSON-serialised). The Spring Voyage Agent's image is responsible for reading this env var and presenting the tools to the LLM as tool-call surfaces. The image side is out of scope here — the launcher's job is just to set the env var. Image-side image work is filed as a separate task pair (D8 covers the platform-side handlers; the image-side change is part of `cvoya-com/spring-voyage-agent` and tracked there).
- **Acceptance.** Integration test: launcher invoked with two descriptors writes the env var; test asserts the env var content matches the expected JSON shape.
- **Blocked by.** D3.

**D5 — Implement orchestration-tool attachment in `ClaudeCodeLauncher`**

- **Files:** `src/Cvoya.Spring.AgentRuntimes/Launchers/ClaudeCodeLauncher.cs`, integration test.
- **Deliverable.** When `OrchestrationTools` is non-empty, configure an MCP server entry per Claude Code's MCP convention. Tools are exposed under `mcp__spring__<tool_name>` (or whatever Claude Code expects). Implementation pattern: stamp an MCP config file in the workspace; Claude Code reads it on launch.
- **Acceptance.** Integration test asserts the MCP config file is present and contains the expected tool shape.
- **Blocked by.** D3.

**D6 — Implement orchestration-tool attachment in `CodexLauncher`**

- **Files:** `src/Cvoya.Spring.AgentRuntimes/Launchers/CodexLauncher.cs`, integration test.
- **Deliverable.** Same shape as D5 but for Codex's MCP / tool surface (Codex uses an MCP-like config; verify the exact convention at implementation time).
- **Acceptance.** Integration test.
- **Blocked by.** D3.

**D7 — Implement orchestration-tool attachment in `GeminiLauncher`**

- **Files:** `src/Cvoya.Spring.AgentRuntimes/Launchers/GeminiLauncher.cs`, integration test.
- **Deliverable.** Same shape as D5 but for Gemini CLI's tool surface.
- **Acceptance.** Integration test.
- **Blocked by.** D3.

**D8 — Implement platform-side orchestration tool-call handlers (with auth + depth gates)**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/OrchestrationToolHandlers.cs` (new), `src/Cvoya.Spring.Dapr/Orchestration/OrchestrationDepthCounter.cs` (new — per-thread counter, location TBD at implementation per existing thread-state pattern), unit tests.
- **Deliverable.** A class with five methods, one per tool name, applying these gates per ADR-0039 §3 "Authorization rules":
  1. **Caller is a unit (`unit://` scheme).** Non-unit caller → reject with `OrchestrationCallerIsNotUnit`. Address scheme is read from the call context; no directory lookup needed.
  2. **Target is a direct child of the caller.** For `delegate_to_child` / `fanout_to_children` / `inspect_child` / `query_child_status`, resolve each target against the caller's *current* direct children via the directory at call time. Non-child target → `OrchestrationTargetNotChild`.
  3. **Self-delegation rejected.** Target equal to caller → `OrchestrationSelfDelegation`.
  4. **Per-thread depth budget.** Each `delegate_to_child` / `fanout_to_children` increments a thread-scoped counter; ceiling is 8 (configurable per host via standard config). Exceeded → `OrchestrationDepthExceeded`. Decrement when the call's response is captured (or on exception).

  Method shapes (after gates pass): `HandleListChildren(caller, thread)` returns direct child descriptors. `HandleInspectChild(caller, target)` returns metadata. `HandleDelegateToChild(caller, target, message, reason?)` resolves via `IAgentProxyResolver`, calls `IAgent.ReceiveAsync`, returns the response. `HandleFanoutToChildren(caller, targets, message, reason?)` does the same in parallel with per-target timeout. `HandleQueryChildStatus(caller, target)` returns a status descriptor.

  Both transports (LLM tool-call handlers from D4–D7 and HTTP endpoints from D13) call into these handlers; the gates fire identically regardless of caller surface.
- **Acceptance.** Unit tests cover each method's happy path plus each gate's rejection path:
  - non-unit caller → `OrchestrationCallerIsNotUnit`
  - non-child target → `OrchestrationTargetNotChild`
  - self target → `OrchestrationSelfDelegation`
  - depth-budget-exhausted → `OrchestrationDepthExceeded`
  - happy path with explicit `reason` argument round-trips into the `OrchestrationDecision.Reason` field.
- **Blocked by.** D2.

**D9 — Emit `OrchestrationDecision` events from delegation handlers**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/OrchestrationToolHandlers.cs` (extend), `IActivityEventBus` integration test.
- **Deliverable.** `HandleDelegateToChild` and `HandleFanoutToChildren` emit `OrchestrationDecision` via `IActivityEventBus.PublishAsync` with the correct `Kind` (`Delegate` / `Fanout`), `Status` (`Routed` on success, `Failed` on exception), `Targets`, `ResultMessageIds`, `Reason` (passed in by the runtime's tool-call argument). `HandleInspectChild` and `HandleListChildren` and `HandleQueryChildStatus` do **not** emit events (per ADR-0039 §4).
- **Acceptance.** Integration test verifies the bus receives the expected event for both delegation kinds; verifies non-delegation handlers do not emit.
- **Blocked by.** D8.

**D10 — Tier-3 e2e: `delegate_to_child` end-to-end**

- **Files:** `tests/integration/Tier3/DelegateToChildE2E.cs` (new).
- **Deliverable.** A unit with two `spring-voyage` member agents receives a message; the unit's runtime calls `delegate_to_child` to one of them; the agent responds; the unit returns the response. Activity stream contains one `OrchestrationDecision` with `kind: delegate, status: routed, targets: [<agent-address>]`.
- **Acceptance.** E2E green; activity stream verified.
- **Blocked by.** D4, D9.

**D11 — Tier-3 e2e: `fanout_to_children` end-to-end**

- **Files:** `tests/integration/Tier3/FanoutToChildrenE2E.cs` (new).
- **Deliverable.** Same shape as D10 but with `fanout_to_children` to both children. `OrchestrationDecision.kind: fanout, targets: [<both>]`.
- **Acceptance.** E2E green; activity stream verified.
- **Blocked by.** D4, D9.

**D12 — Define per-invocation callback token contract + validation middleware**

- **Files:** `src/Cvoya.Spring.Core/Runtime/CallbackToken.cs` (new — claim shape), `src/Cvoya.Spring.Dispatcher/Auth/CallbackTokenValidator.cs` (new), `src/Cvoya.Spring.Dispatcher/Auth/CallbackTokenIssuer.cs` (new), tests.
- **Deliverable.** JWT-shaped token with claims: `tenantId`, `agentAddress`, `threadId`, `messageId`, `expiresAt`. Issuer signs with the existing tenant-scoped signing key (locate the existing pattern from ADR-0029 / dispatcher auth at implementation time; if no pattern exists, the issuer adds a tenant signing key abstraction). Validator middleware on the dispatcher's orchestration endpoints rejects expired, mis-scoped, unsigned, or replay-suspect tokens. **Validation only checks token integrity and claim shape — the caller-has-children and target-is-child authorization checks live in the endpoint handlers (D13), per ADR-0039 §3 "Authorization rules."** Use ASP.NET's existing JWT validation with a custom claim-shape validator.
- **Acceptance.** Unit tests cover sign/verify roundtrip, expiry, scope mismatch, signature failure. The validator does **not** consult the directory (that is D13's responsibility).
- **Blocked by.** A1 (depends on `Address` and `TenantId` types).

**D13 — Add HTTP orchestration callback API endpoints to the dispatcher**

- **Files:** `src/Cvoya.Spring.Dispatcher/OrchestrationCallbackEndpoints.cs` (new), `src/Cvoya.Spring.Dispatcher/OrchestrationCallbackModels.cs` (new — request/response DTOs), tests.
- **Deliverable.** REST endpoints — one per orchestration tool — under a base path the dispatcher controls (e.g. `/v1/runtime/orchestration/list-children`, `/inspect-child`, `/delegate-to-child`, `/fanout-to-children`, `/query-child-status`). Each endpoint applies, in order:
  1. **Token validation** via D12 middleware. Failure → 401.
  2. **Cross-tenant containment.** `claims.tenantId` must match the caller's tenant; mismatch → 403.
  3. **Dispatch to the D8 handler.** All remaining gates (caller-is-unit, target-is-direct-child, self-delegation, per-thread depth) live in D8 and apply uniformly across both transports. The endpoint maps each gate's rejection to its HTTP status code: `OrchestrationCallerIsNotUnit` → 403, `OrchestrationTargetNotChild` → 404, `OrchestrationSelfDelegation` → 400, `OrchestrationDepthExceeded` → 429. Response body carries `{ error: <reason-code>, message: <human-readable> }`.

  Endpoints are not part of the public OpenAPI surface — they are runtime-internal callbacks; document them in `docs/architecture/agent-sdk.md` (D18).
- **Acceptance.** Integration test covers each endpoint with:
  - **Happy path** (caller is a unit with the target as a direct child) → 200.
  - **Non-unit caller** (`agent://` scheme in claims) → 403 `OrchestrationCallerIsNotUnit`. **This is the test that pins the unit-callable-only contract.** Without this test, the contract is undefended.
  - **Unit caller with zero children** calling `list_children` → 200, `[]`. (Empty unit is still a unit.)
  - **Non-child target** → 404 `OrchestrationTargetNotChild`.
  - **Target was a child but is not now** (membership shrank between mint and call) → 404 `OrchestrationTargetNotChild`.
  - **Self-target** → 400 `OrchestrationSelfDelegation`.
  - **Depth budget exhausted** (8th nested delegation in one inbound thread) → 429 `OrchestrationDepthExceeded`.
  - **Cross-tenant token** → 403.
  - **Invalid / expired token** → 401.
- **Blocked by.** D8, D12.

**D14 — Launcher injects `SPRING_CALLBACK_URL` and `SPRING_CALLBACK_TOKEN`**

- **Files:** Each of the four launchers (`SpringVoyageAgentLauncher`, `ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`), tests.
- **Deliverable.** At launch time, the launcher computes the dispatcher URL (host config — locate at implementation), mints a callback token via D12 scoped to `(tenantId, agentAddress, threadId, messageId)`, and writes both as env vars on the runtime container. The two env-var names are uniform across all runtimes (no per-runtime variation).
- **Acceptance.** Integration test per launcher asserts both env vars are written; the token validates against D12; the URL is reachable from the container's network namespace (or test-mocked).
- **Blocked by.** D12.

**D15 — `Cvoya.Spring.AgentSdk` package: typed `IOrchestrationClient`**

- **Files:** New project `src/Cvoya.Spring.AgentSdk/Cvoya.Spring.AgentSdk.csproj`, `src/Cvoya.Spring.AgentSdk/IOrchestrationClient.cs`, `src/Cvoya.Spring.AgentSdk/OrchestrationClient.cs` (HTTP impl), `src/Cvoya.Spring.AgentSdk/SpringAgent.cs` (env-var-bootstrap entrypoint), `src/Cvoya.Spring.AgentSdk/Exceptions.cs` (typed exception hierarchy), unit tests.
- **Deliverable.** `IOrchestrationClient` exposes the five orchestration tools as method calls (signatures match ADR-0039 §3 SDK sketch). `OrchestrationClient` posts JSON over HTTP to the dispatcher endpoints from D13, carrying the callback token from `SPRING_CALLBACK_TOKEN`. `SpringAgent.FromEnvironment()` reads `SPRING_CALLBACK_URL` and `SPRING_CALLBACK_TOKEN`, throws a precise `MissingCallbackEnvironmentException` when either is unset. The package depends only on `Cvoya.Spring.Core` (for `Address`, `Message`, `ChildDescriptor`) and `System.Net.Http`.
- **Typed exceptions.** `OrchestrationAuthException` carries an `OrchestrationAuthReason` enum with: `InvalidToken` (401), `CallerIsNotUnit` (403, ADR-0039 §3), `TargetNotChild` (404), `SelfDelegation` (400), `DepthExceeded` (429), `CrossTenant` (403). `OrchestrationTransportException` for timeouts and network errors. `MissingCallbackEnvironmentException` for the env-var bootstrap failure. The `IOrchestrationClient` XML documentation explicitly notes that **every method throws `OrchestrationAuthException(Reason = CallerIsNotUnit)` when invoked from a leaf-agent image** — leaf-agent image authors must not write code paths that quietly swallow this.
- **Acceptance.** Unit tests against a mocked HTTP backend cover each method's happy path, every `OrchestrationAuthException.Reason` branch (one test per reason), the `MissingCallbackEnvironmentException` branch, and the transport-error path. Public surface listed in the project's `<PublicAPI>` and reviewed during PR.
- **Blocked by.** D13.

**D16 — Sample workflow image demonstrating SDK usage**

- **Files:** New sample under `samples/workflow-agent-image/` (locate the conventional samples root at implementation), Dockerfile, README.
- **Deliverable.** A minimal runtime image written in .NET that:
  - reads the inbound message from stdin (or env, per the runtime contract — match `spring-voyage` convention),
  - constructs `IOrchestrationClient` via `SpringAgent.FromEnvironment()`,
  - runs a deterministic state machine (e.g. round-robin or expertise-keyed) that calls `DelegateToChildAsync` once,
  - returns the child's response on stdout.

  The state machine is illustrative; the goal is to prove the SDK round-trips. The image's Dockerfile pins the platform image base for SDK consumption (similar to existing runtime base images). README walks an operator through running the image as a unit's runtime locally.
- **Acceptance.** Image builds; unit tests of the state-machine code pass; the image runs end-to-end in D17.
- **Blocked by.** D15.

**D17 — Tier-3 e2e: SDK-based workflow image dispatches via the SDK**

- **Files:** `tests/integration/Tier3/SdkWorkflowDispatch.cs` (new).
- **Deliverable.** Configure a unit with the sample image from D16 as its runtime, with two member agents. Send a domain message to the unit. The sample's state machine selects one child via SDK, calls `DelegateToChildAsync`, and returns the response. Activity stream contains one `OrchestrationDecision` with `kind: delegate, status: routed, targets: [<chosen-child>]` — same shape as D10 (the LLM-driven path) — proving both transports produce identical evidence.
- **Acceptance.** E2E green; activity-stream payload verified to match D10's shape exactly.
- **Blocked by.** D14, D15, D16, D9.

**D18 — Documentation: `docs/architecture/agent-sdk.md`**

- **Files:** `docs/architecture/agent-sdk.md` (new).
- **Deliverable.** Sections: SDK package overview; env-var contract (`SPRING_CALLBACK_URL`, `SPRING_CALLBACK_TOKEN`); typed client surface (`IOrchestrationClient` + `SpringAgent.FromEnvironment()`); error model with each `OrchestrationAuthException.Reason` value documented (`InvalidToken`, `CallerIsNotUnit`, `TargetNotChild`, `SelfDelegation`, `DepthExceeded`, `CrossTenant`); **authorization model** — the SDK is structurally unit-callable only (per ADR-0039 §3 "Authorization rules"); leaf-agent images that consume the SDK get `CallerIsNotUnit` on every call; targets must be direct children (cross-level delegation deferred to v0.2); self-delegation rejected; per-thread depth budget; A2A messaging remains available to leaf agents through the existing A2A protocol, separately from this SDK; workflow-state guidance ("the platform does not provide durability; pick a state store and place it in your image or sidecar"); security model (per-invocation token; no cross-invocation reuse; tenant-scoped signing key); sample image walkthrough cross-linking the D16 sample.
- **Acceptance.** Doc renders cleanly; CI's docs-evergreen-framing job passes. Leaf-agent rejection behaviour described before any code-sample callouts so image authors see it.
- **Blocked by.** D15.

---

### Phase E — Delete strategy taxonomy + LabelRoutingPolicy

**E1 — Delete `IOrchestrationStrategy` and `AiOrchestrationStrategy`**

- **Files:** Delete `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategy.cs`, `src/Cvoya.Spring.Dapr/Orchestration/AiOrchestrationStrategy.cs`, all matching test files.
- **Deliverable.** Remove the type and its only AI-routing impl + tests. Remove DI registration line.
- **Acceptance.** Build clean. No remaining references in the tree (`grep IOrchestrationStrategy` empty; `grep AiOrchestrationStrategy` empty).
- **Blocked by.** D2 (the unit no longer consults strategies).

**E2 — Delete `LabelRoutedOrchestrationStrategy`**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/LabelRoutedOrchestrationStrategy.cs` + tests.
- **Deliverable.** Delete file + tests. Note in PR body: GitHub label-routing replacement is delivered in Phase F.
- **Acceptance.** Build clean (assumes E1 lands first; otherwise rests on the same DI-registration cleanup).
- **Blocked by.** D2.

**E3 — Delete `WorkflowOrchestrationStrategy`**

- **Files:** `src/Cvoya.Spring.Dapr/Orchestration/WorkflowOrchestrationStrategy.cs`, `WorkflowOrchestrationOptions.cs` if present, tests.
- **Acceptance.** Build clean.
- **Blocked by.** D2.

**E4 — Delete `IOrchestrationStrategyProvider` and impls**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyProvider.cs`, `src/Cvoya.Spring.Dapr/Orchestration/CachingOrchestrationStrategyProvider.cs`, `DbOrchestrationStrategyProvider.cs`, tests.
- **Acceptance.** Build clean.
- **Blocked by.** E1, E2, E3.

**E5 — Delete `IOrchestrationStrategyResolver` and impls**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyResolver.cs`, `src/Cvoya.Spring.Dapr/Orchestration/DefaultOrchestrationStrategyResolver.cs`, tests.
- **Acceptance.** Build clean.
- **Blocked by.** E4.

**E6 — Delete `IUnitOrchestrationStore` and impl**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/IUnitOrchestrationStore.cs`, `src/Cvoya.Spring.Dapr/Orchestration/DbUnitOrchestrationStore.cs`, tests.
- **Acceptance.** Build clean.
- **Blocked by.** E5.

**E7 — Delete `IOrchestrationStrategyCacheInvalidator` and `NullOrchestrationStrategyCacheInvalidator`**

- **Files:** `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyCacheInvalidator.cs`, `src/Cvoya.Spring.Core/Orchestration/NullOrchestrationStrategyCacheInvalidator.cs`, tests.
- **Acceptance.** Build clean.
- **Blocked by.** E6.

**E8 — Delete `OrchestrationEndpoints` and `OrchestrationModels`**

- **Files:** `src/Cvoya.Spring.Host.Api/Endpoints/OrchestrationEndpoints.cs`, `src/Cvoya.Spring.Host.Api/Models/OrchestrationModels.cs`, tests, route-registration site.
- **Acceptance.** Build clean. The route is no longer registered.
- **Blocked by.** E7.

**E9 — Add 410 Gone handler at `POST /api/v1/units/{id}/orchestration`**

- **Files:** A small endpoint registration (could live in a "legacy errors" file under `Cvoya.Spring.Host.Api/Endpoints/`), test file.
- **Deliverable.** Returns 410 with the migration hint from ADR-0039 §9: "the orchestration endpoint is removed in ADR-0039; configure the unit's runtime instead."
- **Acceptance.** Integration test pins the 410 + body.
- **Blocked by.** E8.

**E10 — Regenerate OpenAPI, Kiota, openapi-typescript**

- **Files:** `openapi.json`, generated Kiota client files, generated `openapi-typescript` types.
- **Deliverable.** Regen and commit. CI's openapi-drift job (locked from `/openapi-diff` skill) passes.
- **Acceptance.** `/openapi-diff` clean.
- **Blocked by.** E8.

**E11 — Delete `LabelRoutingPolicy`**

- **Files:** `src/Cvoya.Spring.Core/Policies/LabelRoutingPolicy.cs`, any direct test files.
- **Acceptance.** Build clean.
- **Blocked by.** E2.

**E12 — Remove `LabelRoutingPolicy` slot from `UnitPolicy`**

- **Files:** `src/Cvoya.Spring.Core/Policies/UnitPolicy.cs`, `IUnitPolicyRepository.cs`, repository impl, tests.
- **Deliverable.** Drop the `LabelRouting` property and any read/write paths. Storage path is dropped (the row remains; Phase E14 handles the schema).
- **Acceptance.** Build clean.
- **Blocked by.** E11.

**E13 — Remove `orchestration:` block from `UnitManifest` and add `LegacyUnitOrchestrationField` parser error**

- **Files:** `src/Cvoya.Spring.Manifest/UnitManifest.cs`, parser file (locate during task), tests.
- **Deliverable.** Drop the `Orchestration` property. Add a parse-time check: if YAML contains `orchestration:` at the unit root, raise a parse error with the migration hint from ADR-0039 §9.
- **Acceptance.** Parser test pins the error for legacy YAML; clean parse for new YAML.
- **Blocked by.** E1, E2, E3.

**E14 — EF Core migration: drop orchestration-strategy and label-routing-policy tables**

- **Files:** `src/Cvoya.Spring.Dapr/Migrations/<timestamp>_DropOrchestrationStrategyAndLabelRouting.cs` (generated), updated model snapshot.
- **Deliverable.** `dotnet ef migrations add DropOrchestrationStrategyAndLabelRouting`. Verify the migration drops both tables. Migration is forward-only; rollback path documented but not exercised (clean-deploy per ADR-0039 §9).
- **Acceptance.** Migration applies cleanly against a non-empty starting state in a test DB.
- **Blocked by.** E6, E12.

**E15 — Remove orchestration-strategy step from unit-create wizard**

- **Files:** `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` (and supporting wizard files — locate during task), test files.
- **Deliverable.** Delete the strategy-picker step entirely. Reduce step counts (`maxStepForSource`) accordingly.
- **Acceptance.** Vitest + Playwright e2e specs covering the step are deleted. Wizard reaches Install step in the new step count.
- **Blocked by.** E1, E2, E3.

**E16 — Audit and remove orchestration references from portal unit-detail surfaces**

- **Files:** `src/Cvoya.Spring.Web/src/components/units/` (locate during task), test files.
- **Deliverable.** Find any unit-detail surface that displays the orchestration strategy (settings card, status panel) and remove. Replace with the runtime + model display already shipped per ADR-0038.
- **Acceptance.** No portal source files reference "orchestration strategy" or "OrchestrationStrategy" (grep clean).
- **Blocked by.** E15.

**E17 — Remove `--orchestration` / strategy flags from `spring unit create` and any other CLI command**

- **Files:** `src/Cvoya.Spring.Cli/Commands/UnitsCommand.cs` (locate by grep for orchestration), CLI scenario tests.
- **Deliverable.** Remove the flags and parse-error any leftover usage with a hint pointing at ADR-0039.
- **Acceptance.** CLI scenarios green; help text clean.
- **Blocked by.** E1.

**E18 — Trim `ServiceCollectionExtensions.Orchestration.cs` to default-impl-only**

- **Files:** `src/Cvoya.Spring.Dapr/DependencyInjection/ServiceCollectionExtensions.Orchestration.cs`.
- **Deliverable.** The file ends up registering only `DirectoryOrchestrationToolProvider` and (if applicable) `IExecutionConfigInheritanceResolver`. Every line that referenced strategy/store/provider/resolver/cache types is gone.
- **Acceptance.** File reads cleanly; build clean.
- **Blocked by.** E7.

---

### Phase F — GitHub connector rewrite

**F1 — Add per-binding label-roundtrip rule fields to GitHub connector binding**

- **Files:** `src/Cvoya.Spring.Connector.GitHub/Configuration/GitHubBindingConfig.cs` (or equivalent), schema versioning notes, tests.
- **Deliverable.** Two new optional fields: `addOnAssign: string[]` and `removeOnAssign: string[]`. Configuration is per-binding (one binding per repo or per repo group, depending on connector semantics).
- **Acceptance.** Configuration round-trips through serialisation; tests cover empty / non-empty cases.
- **Blocked by.** D9.

**F2 — Rewrite `LabelRoutingRoundtripSubscriber.cs` against `OrchestrationDecision` events**

- **Files:** `src/Cvoya.Spring.Connector.GitHub/Labels/LabelRoutingRoundtripSubscriber.cs`, tests.
- **Deliverable.** Subscribe to `OrchestrationDecision` with `Kind == Delegate`. For each event, look up the GitHub binding for the unit; if rules are configured, apply `addOnAssign` / `removeOnAssign` via Octokit. Idempotent retries on transient failure.
- **Acceptance.** Unit tests cover: rule applied, rule absent (no-op), Octokit failure (retry path).
- **Blocked by.** F1, E11.

**F3 — Add `spring connector github label-rules set <binding-id>` CLI command**

- **Files:** `src/Cvoya.Spring.Cli/Commands/ConnectorCommand.cs` (or wherever connector subcommands live), CLI scenario test.
- **Deliverable.** `spring connector github label-rules set <binding-id> --add-on-assign <…> --remove-on-assign <…>`. Reads the existing binding, patches the two fields, writes back.
- **Acceptance.** CLI scenario covers happy path + missing-binding error.
- **Blocked by.** F1.

**F4 — E2E test: label-routing roundtrip via `OrchestrationDecision`**

- **Files:** `tests/integration/Tier3/GitHubLabelRoutingRoundtrip.cs` (new).
- **Deliverable.** A unit's runtime calls `delegate_to_child` for a labeled GitHub issue; subscriber observes the event; Octokit (mocked) applies the configured labels.
- **Acceptance.** E2E green.
- **Blocked by.** F2.

**F5 — Update `docs/concepts/connectors.md#github-label-routing`**

- **Files:** `docs/concepts/connectors.md`.
- **Deliverable.** Rewrite the section to describe connector-side configuration (per-binding rules, no platform-side `LabelRoutingPolicy`). Cross-link to ADR-0039 §4.
- **Acceptance.** Doc renders cleanly. CI's docs-evergreen-framing job passes.
- **Blocked by.** F2.

---

### Phase G — Container-runtime field removal

**G1 — Remove `--container-runtime` flag from `spring agent create`**

- **Files:** `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs`, CLI scenario test.
- **Deliverable.** Drop the flag. Reject the flag at parse time with a hint per ADR-0039 §9.
- **Acceptance.** CLI scenario covers parse-time rejection.
- **Blocked by.** A1.

**G2 — Remove `--container-runtime` flag from `spring unit create`**

- **Files:** `src/Cvoya.Spring.Cli/Commands/UnitsCommand.cs` (locate), CLI scenario test.
- **Deliverable.** Same as G1.
- **Acceptance.** Same.
- **Blocked by.** A1.

**G3 — Remove container-runtime selector from unit-create wizard**

- **Files:** `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` (and wizard helpers — locate), test files.
- **Deliverable.** Delete the selector. Adjust step plumbing if the field was its own step or share a step (it shares; just drop the input).
- **Acceptance.** Vitest + Playwright e2e specs covering the selector are deleted.
- **Blocked by.** A1.

**G4 — Remove container-runtime selector from agent-create wizard**

- **Files:** `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx` (and supporting form code — locate), test files.
- **Deliverable.** Same as G3.
- **Acceptance.** Same.
- **Blocked by.** A1.

**G5 — Remove `containerRuntime` from `UnitManifest` + `LegacyContainerRuntimeField` parser error**

- **Files:** `src/Cvoya.Spring.Manifest/UnitManifest.cs` (and equivalent agent manifest type), parser file, tests.
- **Deliverable.** Drop the property. Add parse-time error per ADR-0039 §9.
- **Acceptance.** Parser test pins the error for legacy YAML.
- **Blocked by.** A1.

**G6 — Remove `containerRuntime` from create / update wire DTOs**

- **Files:** `src/Cvoya.Spring.Host.Api/Models/CreateAgentRequest.cs`, `UpdateAgentExecutionRequest.cs`, `CreateUnitRequest.cs`, `UpdateUnitExecutionRequest.cs` (verify exact names), tests.
- **Deliverable.** Drop the field. Add a wire-DTO error path: a request body carrying `containerRuntime` is rejected with `LegacyContainerRuntimeField`.
- **Acceptance.** Integration tests pin both clean-request and legacy-rejection paths.
- **Blocked by.** A1.

**G7 — Regenerate OpenAPI, Kiota, openapi-typescript**

- **Files:** Generated artefacts.
- **Deliverable.** Regen and commit.
- **Acceptance.** `/openapi-diff` clean.
- **Blocked by.** G6.

**G8 — Remove `ContainerRuntime` from execution-config records**

- **Files:** `src/Cvoya.Spring.Core/Execution/AgentExecutionConfig.cs` (or equivalent post-ADR-0038), tests.
- **Deliverable.** Drop the property.
- **Acceptance.** Build clean.
- **Blocked by.** G5, G6.

**G9 — EF Core migration: drop `containerRuntime` column**

- **Files:** Migration file, model snapshot.
- **Deliverable.** Drops the column from the relevant table(s).
- **Acceptance.** Migration applies cleanly against a non-empty starting state.
- **Blocked by.** G8.

---

### Phase H — `spring agent create` positional `<id>` removal

**H1 — Remove positional `<id>` from `spring agent create`**

- **Files:** `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs`, CLI scenario test.
- **Deliverable.** Drop the positional. Reject a positional argument at parse time with a hint per ADR-0039 §9.
- **Acceptance.** CLI scenario covers happy path + parse-time rejection.
- **Blocked by.** A1.

**H2 — Audit `spring unit create` for analogous positional + remove**

- **Files:** `src/Cvoya.Spring.Cli/Commands/UnitsCommand.cs`, CLI scenario test.
- **Deliverable.** If a positional `<id>` exists on `spring unit create`, drop it with the same parse-time error.
- **Acceptance.** Audit recorded in PR body whether positional was present; CLI scenarios reflect the outcome.
- **Blocked by.** A1.

**H3 — Update CLI scenarios under `tests/e2e/cli/`**

- **Files:** Every scenario file that referenced a positional `<id>`.
- **Deliverable.** Migrate to `--name <…>`. Remove the now-obsolete positional scenarios.
- **Acceptance.** All scenarios green.
- **Blocked by.** H1, H2.

**H4 — Update `docs/cli-reference.md`**

- **Files:** `docs/cli-reference.md`.
- **Deliverable.** Drop the positional argument from the help shape; document `--name` as the source of display identity.
- **Acceptance.** Doc renders; lint clean.
- **Blocked by.** H1, H2.

---

### Phase I — Agent-create form schema + shared `<AgentCreateForm>`

**I1 — Extend `buildAgentDefinitionJson` to accept structured `model: {provider, id}`**

- **Files:** `src/Cvoya.Spring.Web/src/lib/agents/create-agent.ts`, vitest.
- **Deliverable.** Accept `model: { provider: string; id: string }` and serialise it correctly into `definitionJson`. Today's flat shape is replaced.
- **Acceptance.** Vitest covers the new shape.
- **Blocked by.** —

**I2 — Extend `buildAgentDefinitionJson` to accept `hosting`**

- **Files:** Same as I1.
- **Deliverable.** Add `hosting: 'ephemeral' | 'persistent' | null` (null = inherit). Serialise into `execution.hosting`.
- **Acceptance.** Vitest.
- **Blocked by.** I1.

**I3 — Extract `<AgentCreateForm>` from `app/agents/create/page.tsx`**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-form.tsx` (new), `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx` (refactor to consume).
- **Deliverable.** Component owns identity, execution, and unit-assignment fields. Form schema in `src/lib/agents/create-agent.ts`.
- **Acceptance.** Existing scratch-path e2e specs pass against the extracted component.
- **Blocked by.** I2.

**I4 — Per-field inherit affordance on Execution fields**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-form.tsx`, `DESIGN.md` (update §12.6 cross-reference if needed).
- **Deliverable.** Italic placeholder per DESIGN.md §12.6 (`inherited from <unit-name>: <resolved-value>`); help-copy below the field with `data-testid="inherit-indicator"`. Applied to runtime, model.provider, model.id, image, hosting.
- **Acceptance.** Vitest covers data-testid presence + placeholder copy in inherit / configured states.
- **Blocked by.** I3.

**I5 — `[Inherits]/[Configured]` outline badge in card header**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-form.tsx`.
- **Deliverable.** Badge flips when any execution field is overridden. Pattern reused from DESIGN.md §12.6.
- **Acceptance.** Vitest covers the flip.
- **Blocked by.** I4.
- **Status.** Delivered as part of I4 (PR #1940). The Execution card-header badge (`data-testid="execution-card-badge"`), the `executionHasOverride` flip predicate, and the `flips the Execution card badge from \`Inherits\` to \`Configured\` when any field is set` vitest case all land with I4 because the badge predicate reads the same `runtime`/`modelProviderId`/`modelId`/`image`/`hosting` form state the per-field inherit affordances introduce.

**I6 — Render structured 422 multi-parent inheritance conflict response inline**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-form.tsx`, vitest, e2e spec.
- **Deliverable.** When the create call returns the structured 422 from B1, render an inline error per DESIGN.md §6.3 with `data-testid="multi-parent-inheritance-conflict"`. List each diverging field with parent-attributed values.
- **Acceptance.** Vitest covers parsing + display; e2e covers the full submit-fail-display loop.
- **Blocked by.** I3, B1.

**I7 — Update `/agents/create` standalone page to consume `<AgentCreateForm>`**

- **Files:** `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx`.
- **Deliverable.** Page becomes a thin wrapper around the form. Existing behaviour preserved modulo the new inherit visuals.
- **Acceptance.** Existing e2e spec passes; visual regression budget noted.
- **Blocked by.** I3.

**I8 — Update / migrate existing `/agents/create` scratch-path e2e specs**

- **Files:** Existing Playwright specs under `tests/web-e2e/`.
- **Deliverable.** Update assertions for the new visuals (inherit indicators, badges) and ensure scratch round-trip still passes.
- **Acceptance.** All pre-existing specs green.
- **Blocked by.** I4, I5, I7.

---

### Phase J — Unit-tab dialog rewrite

**J1 — Implement `<AgentCreateDialog>` shell**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-dialog.tsx` (new), vitest.
- **Deliverable.** Shell dialog wrapping `<AgentCreateForm>`. Accepts `unitId` and `unitDisplayName` props. Preselects the unit in the form's parent multi-select (collapsed to a confirmation strip per design §2.5).
- **Acceptance.** Vitest covers preselection and display name in header.
- **Blocked by.** I3.

**J2 — Replace `<MembershipDialog mode="add">` with `<AgentCreateDialog>` in `agents-tab.tsx`**

- **Files:** `src/Cvoya.Spring.Web/src/components/units/tab-impls/agents-tab.tsx`.
- **Deliverable.** Click handler for `+ Add agent` opens `<AgentCreateDialog>`. `<MembershipDialog mode="edit">` continues to handle per-membership edit.
- **Acceptance.** Vitest + e2e cover the click-to-open path.
- **Blocked by.** J1.

**J3 — Pass unit display name (not Guid) to dialog header**

- **Files:** `src/Cvoya.Spring.Web/src/components/units/tab-impls/agents-tab.tsx`.
- **Deliverable.** Header description reads "This agent will be registered in `<unit display name>` …", not the Guid. Lookup uses the unit row already in scope.
- **Acceptance.** Vitest pins the rendered header copy.
- **Blocked by.** J2.

**J4 — Delete picker code from `<MembershipDialog>`**

- **Files:** `src/Cvoya.Spring.Web/src/components/units/membership-dialog.tsx`.
- **Deliverable.** Remove the `mode === "add"` branch including the picker, "no agents available," and `+ New agent` link. If the surgical separation is large, factor `<MembershipEditDialog>` in the same PR.
- **Acceptance.** Build + tests clean. Component is `mode: "edit"`-only.
- **Blocked by.** J2.

**J5 — Delete picker e2e specs**

- **Files:** Picker-related Playwright specs (locate by grep for "no agents available" or "Add agent" picker copy).
- **Deliverable.** Delete the specs; do not rewrite (the behaviour is gone).
- **Acceptance.** Test count drops; e2e suite green.
- **Blocked by.** J4.

**J6 — Add e2e specs for create-dialog flow**

- **Files:** New Playwright specs under `tests/web-e2e/agents-tab/`.
- **Deliverable.** Cover happy path: open dialog → fill name → submit → see new agent in the tab. Inherit-only path: leave fields blank → submit → resolved on dispatch.
- **Acceptance.** Specs green.
- **Blocked by.** J3, J4.

---

### Phase K — Three-paths Source step + drop YAML synthesis

**K1 — Add Source step to `<AgentCreateForm>` (page only)**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/create-form.tsx`.
- **Deliverable.** Three `<SourceCard>`s (scratch / from-package / browse). Step counts adjusted for each branch per design §2.2. Dialog still defaults to scratch with a "From package…" footer link (added in K9).
- **Acceptance.** Vitest covers all three branches.
- **Blocked by.** I7.

**K2 — Add package picker for from-package path**

- **Files:** `src/Cvoya.Spring.Web/src/components/agents/source-package-picker.tsx` (new), vitest.
- **Deliverable.** Lists packages from `GET /api/v1/packages`; client-side filter to `agentTemplateCount > 0`.
- **Acceptance.** Vitest with mocked API.
- **Blocked by.** K1.

**K3 — Add connector-requirements panel for from-package**

- **Files:** Add to `<AgentCreateForm>` source-package branch.
- **Deliverable.** Reuses the unit-create wizard's connector-requirements affordance (locate during task). Visible copy describes connector requirements; legacy package inputs surface only when the package declares them.
- **Acceptance.** Vitest covers required + legacy-input paths.
- **Blocked by.** K2.

**K4 — Wire from-package branch to `POST /api/v1/packages/install`**

- **Files:** `<AgentCreateForm>` submit path.
- **Deliverable.** From-package submit posts `{ packageName, connectors, inputs }`. Same shape the unit-create wizard already uses.
- **Acceptance.** Integration test (mocked API) pins the request shape.
- **Blocked by.** K2, K3.

**K5 — Make package summary + success copy manifest-derived**

- **Files:** `<AgentCreateForm>` summary screen.
- **Deliverable.** Inspect the installed package's manifest; if it declares units, the success copy says "Unit installed" not "Agent created."
- **Acceptance.** Vitest with three manifest variants.
- **Blocked by.** K4.

**K6 — Drop `build-agent-package.ts` YAML synthesis for scratch path**

- **Files:** Delete `src/Cvoya.Spring.Web/src/app/agents/create/build-agent-package.ts`. Update form submit to post directly.
- **Deliverable.** Scratch path posts `{ displayName, description, role, unitIds, definitionJson }` directly to `POST /api/v1/tenant/agents`.
- **Acceptance.** Existing scratch e2e migrates cleanly; the file is gone (`grep build-agent-package` empty).
- **Blocked by.** K5.

**K7 — Add Browse stub matching unit-create pattern**

- **Files:** `<AgentCreateForm>` browse branch + the stub copy from design §3.5.
- **Acceptance.** Vitest covers stub render + Next button disabled.
- **Blocked by.** K1.

**K8 — Add wizard persistence under `spring.agent-create.v1`**

- **Files:** Existing `wizard-persistence.ts` (or equivalent) extended to cover the agent flow.
- **Deliverable.** Page wizard persists via sessionStorage; dialog does not (matches unit-tab behaviour).
- **Acceptance.** Vitest covers reload-restores-state for the page.
- **Blocked by.** K1.

**K9 — Add "From package…" footer link in the unit-tab dialog**

- **Files:** `<AgentCreateDialog>`.
- **Deliverable.** Footer-left text-link button pivots the dialog into the package-picker sub-mode (uses the same components from K2–K5).
- **Acceptance.** E2E covers the pivot path.
- **Blocked by.** K2, J3.

**K10 — Update `DESIGN.md` §12.6 with the new agent-create surface**

- **Files:** `src/Cvoya.Spring.Web/DESIGN.md`.
- **Deliverable.** Document the inherit-affordance contract for the agent-create surfaces; cross-link to the unit-pane indicator.
- **Acceptance.** Lint clean. Docs-evergreen-framing CI job passes.
- **Blocked by.** K1, J1.

---

### Phase L — CLI agent-create parity

**L1 — Add `--description` flag**

- **Files:** `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs`, CLI scenario test.
- **Deliverable.** Maps to `description` on `CreateAgentRequest`.
- **Acceptance.** CLI scenario covers the flag.
- **Blocked by.** H1.

**L2 — Add `--from-package` flag**

- **Files:** Same as L1.
- **Deliverable.** When set, routes through `POST /api/v1/packages/install`. Mutually exclusive with `--definition*` and execution shorthands (validation in L6).
- **Acceptance.** CLI scenario covers the from-package path against a mocked API.
- **Blocked by.** L1.

**L3 — Add `--connector` repeatable flag (paired with `--from-package`)**

- **Files:** Same as L1.
- **Deliverable.** Same syntax as `spring package install --connector`. Errors when used without `--from-package`.
- **Acceptance.** CLI scenarios cover paired + unpaired (error).
- **Blocked by.** L2.

**L4 — Add `--input` repeatable flag (paired with `--from-package`)**

- **Files:** Same as L1.
- **Deliverable.** Forwards to package-install legacy-input pipeline. Errors when used without `--from-package`.
- **Acceptance.** CLI scenarios cover paired + unpaired (error).
- **Blocked by.** L2.

**L5 — Add `--inherit` boolean flag**

- **Files:** Same as L1.
- **Deliverable.** When set, the CLI omits every execution shorthand from the wire body. Mutually exclusive with execution shorthands (validation in L6).
- **Acceptance.** CLI scenario covers `--inherit` against a mocked API.
- **Blocked by.** L1.

**L6 — Add mutual-exclusion validation**

- **Files:** Same as L1.
- **Deliverable.** Parse-time error for `--inherit` ↔ exec shorthands and `--from-package` ↔ `--definition*`. Each error names the conflicting flags.
- **Acceptance.** CLI scenarios cover every mutual-exclusion pair.
- **Blocked by.** L2, L5.

**L7 — Make `--unit` optional**

- **Files:** Same as L1.
- **Deliverable.** Empty `--unit` set creates a top-level tenant-parented agent. The CLI does not require `--unit`.
- **Acceptance.** CLI scenario covers the no-`--unit` happy path.
- **Blocked by.** B1.

**L8 — CLI scenario tests for the seven-row test matrix from design §5**

- **Files:** Scenarios under `tests/e2e/cli/agent-create/`.
- **Deliverable.** One scenario per row of the matrix in [`agent-create-redesign.md` §5](../../design/v0.1/agent-create-redesign.md#5-cross-portal--cross-cli-test-matrix).
- **Acceptance.** All seven scenarios green.
- **Blocked by.** L1–L7.

**L9 — Update `docs/cli-reference.md`**

- **Files:** `docs/cli-reference.md`.
- **Deliverable.** Document the new flags, mutual-exclusion rules, and the optional `--unit`.
- **Acceptance.** Lint clean.
- **Blocked by.** L1–L7.

---

### Phase M — Docs overhaul

**M1 — Rewrite `docs/concepts/agents.md` under units-are-agents framing**

- **Files:** `docs/concepts/agents.md`.
- **Deliverable.** Sections: what an agent is, mailbox, execution config, runtime invocation, leaf vs. with-children, orchestration tools, orchestration decisions, inheritance. Cross-links to ADR-0039, ADR-0038, this plan.
- **Acceptance.** Doc renders cleanly. CI's docs-evergreen-framing job passes.
- **Blocked by.** D9.

**M2 — Shrink `docs/concepts/units.md` to unit-only delta**

- **Files:** `docs/concepts/units.md`.
- **Deliverable.** Keep only what is unit-specific: children, permissions, lifecycle workflow, expertise aggregation, boundary, connector binding. Cross-link to `agents.md` for the shared concepts.
- **Acceptance.** No content duplicated between `agents.md` and `units.md`.
- **Blocked by.** M1.

**M3 — Retire `docs/architecture/orchestration.md`**

- **Files:** `docs/architecture/orchestration.md`.
- **Deliverable.** Either delete (preferred — cross-links removed) or replace with a one-pager: "Orchestration in Spring Voyage is the runtime's behaviour, not platform configuration. See concepts/agents.md."
- **Acceptance.** Either no file or a stub-pointer file.
- **Blocked by.** M1.

**M4 — Update `docs/architecture/agent-runtime.md`**

- **Files:** `docs/architecture/agent-runtime.md`.
- **Deliverable.** Add: orchestration-tool surface (the closed five tools), launcher's tool-attachment responsibility (matching the per-runtime mechanisms in D4–D7), `OrchestrationDecision` event shape and emission rules.
- **Acceptance.** Doc renders.
- **Blocked by.** D9.

**M5 — Update `docs/glossary.md`**

- **Files:** `docs/glossary.md`.
- **Deliverable.** Retire "orchestration strategy" and "orchestration policy" entries. Add "orchestration tools," "orchestration decision," "unit-as-agent."
- **Acceptance.** No glossary entry references the deleted types.
- **Blocked by.** E18.

---

## Dependency graph (compact)

Format `<task> ← <prerequisites>`. Empty after `←` means root.

```
A1 ←
A2 ←
A3 ←
A4 ← A1
A5 ← A3
B1 ← A5
B2 ← A5
B3 ← A5
B4 ← A5
B5 ← A5
B6 ← A5
B7 ← B1, B3
B8 ← B3
B9 ← B1
C1 ← A4
C2 ← C1
C3 ← C2
C4 ← C2
C5 ← C2
C6 ← C2
D1 ← A1
D2 ← D1, C2
D3 ← D1
D4 ← D3
D5 ← D3
D6 ← D3
D7 ← D3
D8 ← D2
D9 ← D8
D10 ← D4, D9
D11 ← D4, D9
D12 ← A1
D13 ← D8, D12
D14 ← D12
D15 ← D13
D16 ← D15
D17 ← D14, D15, D16, D9
D18 ← D15
E1 ← D2
E2 ← D2
E3 ← D2
E4 ← E1, E2, E3
E5 ← E4
E6 ← E5
E7 ← E6
E8 ← E7
E9 ← E8
E10 ← E8
E11 ← E2
E12 ← E11
E13 ← E1, E2, E3
E14 ← E6, E12
E15 ← E1, E2, E3
E16 ← E15
E17 ← E1
E18 ← E7
F1 ← D9
F2 ← F1, E11
F3 ← F1
F4 ← F2
F5 ← F2
G1 ← A1
G2 ← A1
G3 ← A1
G4 ← A1
G5 ← A1
G6 ← A1
G7 ← G6
G8 ← G5, G6
G9 ← G8
H1 ← A1
H2 ← A1
H3 ← H1, H2
H4 ← H1, H2
I1 ←
I2 ← I1
I3 ← I2
I4 ← I3
I5 ← I4
I6 ← I3, B1
I7 ← I3
I8 ← I4, I5, I7
J1 ← I3
J2 ← J1
J3 ← J2
J4 ← J2
J5 ← J4
J6 ← J3, J4
K1 ← I7
K2 ← K1
K3 ← K2
K4 ← K2, K3
K5 ← K4
K6 ← K5
K7 ← K1
K8 ← K1
K9 ← K2, J3
K10 ← K1, J1
L1 ← H1
L2 ← L1
L3 ← L2
L4 ← L2
L5 ← L1
L6 ← L2, L5
L7 ← B1
L8 ← L1, L2, L3, L4, L5, L6, L7
L9 ← L1, L2, L3, L4, L5, L6, L7
M1 ← D9
M2 ← M1
M3 ← M1
M4 ← D9
M5 ← E18
```

## GitHub issue filing plan

Pending user approval, the following actions execute as the project's GitHub App identity. **No issues are filed before approval.**

### 1. Umbrella

- File **one umbrella issue** as a sub-issue of `#1786`. Title: "ADR-0039 implementation: 112 tasks, structurally wired by `blockedBy`." Type: `Task`. Milestone: `v0.1`. Body: a thin pointer to ADR-0039, this plan, and the design doc — no enumeration of tasks (the sub-issue panel surfaces them).
- Set `--sub-issue-of 1786` via `gh-app issue create`.

### 2. Per-task issues

For each of the 112 tasks above (Phases A–M, 5 + 9 + 6 + 18 + 18 + 5 + 9 + 4 + 8 + 6 + 10 + 9 + 5 = 112):

- File one issue as a sub-issue of the umbrella. Title: `<phase-letter><number> — <task title>`. Type: `Task`. Milestone: `v0.1`. Body: ~10 lines — files, deliverable, acceptance criteria copy from the plan; no architectural reasoning, no rationale.
- Apply the `area:units-are-agents` label (file label first if it does not exist).
- Set `--sub-issue-of <umbrella-id>` via `gh-app issue create`.

### 3. `blockedBy` edges

- For every `<task> ← <prereq>` edge in the dependency graph, call `gh-app issue create … --blocked-by <prereq-task-id>` at file time, **or** `gh-app` equivalent post-create wiring (use `--blocked-by` repeatedly when filing). The relationship is created via GitHub's native blocked-by edge (the GraphQL `addIssueDependency` underneath `gh-app`). The relationship is **never** stated in the issue body.
- Verification: after wiring, the umbrella's sub-issue panel and each task's "blocked by" panel show the dependencies; CI's milestone view reflects the order.

### 4. Filing order

Issues file in dependency order so that `--blocked-by <id>` always points at an existing issue:

1. Umbrella.
2. Roots (A1, A2, A3, I1).
3. Tasks whose prerequisites are all filed, iteratively, until all 112 are filed.

The plan agent that executes this is mechanical — it walks the dependency table and fans out filing in topological order. No architectural calls.

### 5. Closing rules

- `#1759` closes when **C5** lands.
- `#1763` closes when **L9** lands.
- `#1786` closes when **M5** lands.
- The umbrella closes when its last sub-issue closes.

## Risks and mitigations

**Performance regression on routing-only units.** Pre-ADR-0039, `AiOrchestrationStrategy` was a single `IAiProvider.CompleteAsync` round-trip. Post-ADR-0039, the unit's runtime container launches per turn. C5 and C6 record baseline latency; if the regression breaks a tenant pattern, the answer is operator-side (smaller image, smaller model, warm pool — outside this initiative).

**Window between C2 and D2.** `UnitActor` invokes the runtime but children-tooling is empty. Units with members lose AI-router behaviour temporarily. Mitigation: D2 lands within the same review cycle as C2; release notes for any intermediate build call this out explicitly.

**LabelRouting roundtrip operators with no migration command.** F3 ships the one-shot CLI. F2 lands on the same release; release notes call it out.

**Multi-parent inheritance is a behavioural change for existing agents.** B1–B6 enforce on update flows, so existing agents do not break until an operator edits them. The first edit surfaces the conflict; the operator either trims the parent set or sets explicit config. No retroactive scan; no surprise rejection at message dispatch.

**ADR-0038 churn.** This initiative depends on ADR-0038 type / field names. If the names shift during ADR-0038 implementation, A1–A3 pick them up; subsequent tasks reference the resolved names.

## Out of scope

- Cost optimisation (warm pools, container reuse, per-thread session affinity for unit-side runtimes). Mechanisms exist on the agent side and apply uniformly; no new optimisation work in this initiative.
- Per-tenant orchestration tool extension. v0.1 closes the tool surface; v0.2 may reopen.
- Address scheme collapse (`unit://` and `agent://` stay distinct).
- `IUnitActor` / `IAgentActor` interface collapse — both stay as kind-specific groupings layered on `IAgent`.
- A general "decision evidence" pipeline for non-orchestration agent decisions. v0.1 evidence is scoped to orchestration.
- `spring-voyage-agent` image-side work for orchestration tool consumption. The platform-side launcher writes the env var (D4); the image consumes it. Image work is tracked in the runtime's repository, not here.
