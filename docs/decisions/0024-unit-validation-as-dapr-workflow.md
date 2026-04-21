# 0024 — Unit validation runs as a Dapr Workflow, not as an actor

- **Status:** Accepted — 2026-04-21 — defer-validation rollout ([#941](https://github.com/cvoya-com/spring-voyage/issues/941)) shipped as `UnitValidationWorkflow`, a Dapr Workflow + in-container probe activities. The actor-shaped `RuntimeProbeActor` that surfaced in an early wave of the design was rejected in favour of the workflow.
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Dapr/Workflows/UnitValidationWorkflow.cs`, `src/Cvoya.Spring.Dapr/Workflows/Activities/`, `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs` (validation scheduling / completion), `src/Cvoya.Spring.Core/AgentRuntimes/ProbeStep.cs`, `src/Cvoya.Spring.Core/Units/UnitValidationCodes.cs`, `src/Cvoya.Spring.Cli/Commands/UnitValidationWaitLoop.cs`.
- **Related docs:** [`docs/architecture/units.md § Unit validation workflow`](../architecture/units.md#unit-validation-workflow), [`docs/architecture/agent-runtimes-and-tenant-scoping.md`](../architecture/agent-runtimes-and-tenant-scoping.md), [ADR 0019 — Workflow as container](0019-workflow-as-container.md), [ADR 0015 — Dapr as infrastructure runtime](0015-dapr-as-infrastructure-runtime.md).

## Context

Before #941, unit validation (image pull, tool verify, credential validate, model resolve) ran **host-side** inside `UnitCreationService`: the API host shelled out to `claude --version` / `curl` / `podman` against its own file system, then persisted the outcome on the unit row. Two problems:

1. **Host-vs-container drift.** Operators ran the portal and dispatcher on different hosts / images, so a probe that succeeded on the API host had no guarantee of working inside the unit's chosen runtime image. Users hit cryptic failures at *start* time for units that passed validation at *accept* time.
2. **Credential leakage risk.** The probe shelled out in-process, which meant stdout/stderr capture, exception surfaces, and log enrichment all had to be individually audited to confirm the raw credential never reached an `ActivityEvent`, a log line, or the persisted unit row.

The rework moved probing into the unit's container and introduced a new `UnitStatus.Validating` transition. The outstanding design question was **where** the probe orchestration should live. Two shapes were considered:

1. **`IRuntimeProbeActor`** — a Dapr actor, scheduled per unit, that orchestrates the probe steps by reminder-driven self-dispatch. Follows the existing "every async process is an actor" pattern.
2. **`UnitValidationWorkflow`** — a Dapr Workflow whose activities call the new `PullImageActivity`, `RunContainerProbeActivity`, `EmitValidationProgressActivity`, and `CompleteUnitValidationActivity`.

Actors and workflows have meaningful differences for this use case:

| Axis | Actor | Workflow |
|------|-------|----------|
| Step sequencing | Operator writes explicit reminder-driven state machine | Declarative `await` sequence with durable replay |
| Failure recovery | Reminder re-delivery + state-machine branch | Runtime retries the activity with built-in backoff |
| Scoping | One long-lived actor; activation state | One workflow instance per run; naturally garbage-collected |
| Observability | Custom activity-event emission per step | Built-in workflow history + operator console |
| SSE / polling contract | Needs a custom subscription surface | Activities publish events; progress poll reads workflow state |
| Test shape | Virtual-actor harness with reminder simulation | Pure `WorkflowContext` substitution (see `UnitValidationWorkflowTests`) |

## Decision

**Ship the in-container validation orchestration as `UnitValidationWorkflow`, a Dapr Workflow.**

- `UnitActor.TransitionAsync(Validating)` dispatches the workflow through `IUnitValidationWorkflowScheduler`; the actor itself holds only the status + the current run-id, and records the terminal outcome via `CompleteValidationAsync`.
- The workflow sequences four activities: `PullImageActivity` (dispatcher-owned) → `RunContainerProbeActivity` (for each `ProbeStep` the runtime emits via `GetProbeSteps`, stopping on first failure) → `EmitValidationProgressActivity` after each step → `CompleteUnitValidationActivity` at the end.
- Operators observe progress via SSE (portal Validation panel) or CLI poll (`spring unit create --wait`, `spring unit revalidate`); terminal state is surfaced on `GET /api/v1/units/{name}`.
- Runtimes declare the probe plan as pure data (`ProbeStep` records with an `InterpretOutput` delegate). The workflow stays agnostic of runtime specifics; a new runtime lands by adding one more `IAgentRuntime` registration.

## Alternatives considered

- **`IRuntimeProbeActor`.** Rejected. Reminder-driven state machines for a short-lived sequential orchestration is strictly more code than an `await`-chained workflow, and the failure recovery + retries story duplicates what Dapr Workflow already ships. The actor shape would also have forced a custom SSE surface; the workflow's activity-event path reuses the existing `IActivityEventBus`.
- **Host-side validation with stricter redaction.** Rejected. Doesn't fix the host-vs-container drift; every per-step credential audit would stay manual. The workflow moves the probe into the chosen image, which eliminates the drift category entirely.
- **Custom in-process orchestrator (`IUnitValidator` scoped service).** Rejected. Would need bespoke retry + SSE + progress machinery; re-implements 90% of what Dapr Workflow provides.

## Consequences

### Gains

- **Host-vs-container drift is eliminated.** Every probe runs inside the unit's chosen runtime image via `RunContainerProbeActivity`, so if the probe succeeds, `spring unit start` will find the same environment.
- **Structured redaction.** The `RunContainerProbeActivity` redacts captured `stdout` / `stderr` before persisting or emitting them, and `ProbeStep.InterpretOutput` is contractually forbidden from echoing the raw credential into `UnitValidationError`. The new credential-leak canary test in `tests/Cvoya.Spring.Integration.Tests/` guards the end-to-end path.
- **Retry is a first-class CLI verb.** `POST /units/{name}/revalidate` + `spring unit revalidate <name>` give operators a one-shot recovery path from `Error` / `Stopped`; the workflow handles the retry lifecycle.
- **Runtime authors stay in data.** Adding a new runtime is still "register one `IAgentRuntime`", but the plugin surface is now explicitly pure-data (`GetProbeSteps` returns records; the interpreter delegate is local). No actor or workflow implementation per runtime.

### Costs

- **Dapr Workflow dependency for a first-class user journey.** Previously workflows were only for "domain workflows in containers" ([ADR 0019](0019-workflow-as-container.md)); now they also sit on the unit accept path. If Dapr Workflows are unhealthy, unit creation cannot complete. Mitigated by the `/revalidate` retry surface and by keeping the activities individually short / idempotent.
- **Progress latency.** The CLI poll loop queries the unit's status; SSE is available in the portal. Neither is sub-second; the workflow's step boundaries are the observable cadence.

### Known follow-ups (V2.1)

- **[#952](https://github.com/cvoya-com/spring-voyage/issues/952)** — extract the probe-running activities into a `spring-probe-worker` service, so the worker host doesn't carry the container-runtime ownership. Mirrors the `spring-dispatcher` extraction ([ADR 0012](0012-spring-dispatcher-service-extraction.md)).
- **[#956](https://github.com/cvoya-com/spring-voyage/issues/956)** — switch actor-remoting enum serialization to by-name so a v2.1 release can extend `UnitValidationStep` / `UnitValidationCodes` without wire churn.
- **[#965](https://github.com/cvoya-com/spring-voyage/issues/965)** — finer-grained per-step CLI progress output (today the `--wait` loop polls terminal state; per-step streaming needs a richer progress channel).

## Revisit criteria

Revisit if any of the below hold:

- The workflow's in-container probe path develops structural credential-leak vectors (e.g. a platform-level log enricher that captures activity stdout verbatim). In that case add a second layer of redaction at the workflow boundary rather than reverting to host-side probing.
- Dapr Workflows become a reliability concern in production (sustained outage rates > 0.1% of new-unit accepts); revisit whether the actor shape's simpler failure model wins. Today's bar for that is very high.
- A fifth probe step is added and the plan shape no longer fits cleanly in a sequential workflow — at that point break out into a workflow + sub-workflow pattern rather than denormalise into an actor state machine.
