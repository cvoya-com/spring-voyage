# Unit Lifecycle

> **[Architecture Index](README.md)** | Related: [Units](units.md), [Agents](agents.md), [Orchestration](orchestration.md), [Workflows](workflows.md), [CLI & Web](cli-and-web.md)

This document covers how units move from definition to operation: the status DAG, the validation workflow, and the two creation paths (imperative CLI and declarative YAML). For the unit entity model (membership, nesting, identity), see [Units](units.md). For the agent lifecycle, the same patterns apply — replace `spring unit` with `spring agent` in the CLI commands.

---

## Unit Status & Validation

A unit's status transitions form a DAG:

```
Draft → Validating → Stopped → Starting → Running → Stopping → Stopped
         │               ^
         └──→ Error ─────┘  (via POST /units/{name}/revalidate)
```

- **Draft** — unit persisted but never validated. Rare in normal flow; the API's `POST /units` creates in `Validating` directly.
- **Validating** — `UnitValidationWorkflow` is executing against the unit's chosen image. Terminal on the first probe failure or a successful model resolution.
- **Error** — the workflow recorded a structured `LastValidationError` on `UnitDefinition`. `POST /units/{name}/revalidate` (and `spring unit revalidate <name>`) dispatch a fresh workflow run from here or from `Stopped`.
- **Stopped** — validation passed (or the unit was stopped after running). Ready for `spring unit start`.
- **Starting / Running / Stopping** — the normal runtime-container lifecycle.

### Unit Validation Workflow

`UnitValidationWorkflow` (source: [`src/Cvoya.Spring.Dapr/Workflows/UnitValidationWorkflow.cs`](../../src/Cvoya.Spring.Dapr/Workflows/UnitValidationWorkflow.cs)) is a Dapr Workflow dispatched by `UnitActor.TransitionAsync(Validating)`. Rationale for "workflow, not actor" in [ADR 0024](../decisions/0024-unit-validation-as-dapr-workflow.md).

The workflow runs four ordered activity steps; the first failure short-circuits:

1. **PullingImage** — the dispatcher pulls the unit's configured image via `PullImageActivity`. The image pull is always workflow-owned; runtimes never emit a `PullingImage` step themselves.
2. **VerifyingTool** — `RunContainerProbeActivity` runs the runtime's declared tool check inside the image (e.g. `claude --version`, `curl --version`).
3. **ValidatingCredential** — a second `RunContainerProbeActivity` runs the credential probe (e.g. `curl` against `/v1/models`). Skipped for credential-less runtimes (Ollama).
4. **ResolvingModel** — a third `RunContainerProbeActivity` confirms the requested model id exists in the provider's catalog.

After each step the workflow calls `EmitValidationProgressActivity` (SSE for the portal, activity event for the CLI `--wait` poll loop). The terminal `CompleteUnitValidationActivity` writes `LastValidationError` (or clears it on success) on the `UnitDefinition` row, and calls `IUnitActor.CompleteValidationAsync` to flip status to `Stopped` or `Error`.

The probe plan is built by `IAgentRuntime.GetProbeSteps(config, credential)`. Each step carries:

- `Args` — the in-container command line.
- `Timeout` — bounded step duration (all shipped steps cap well below 5 minutes).
- `Env` — additional environment variables (including the credential, which the probe command typically reads from `$SPRING_CREDENTIAL`).
- `InterpretOutput` — a delegate that maps `(exitCode, stdout, stderr)` onto a `StepResult` carrying either success-extras or a structured `UnitValidationError`. **The interpreter MUST NOT echo the raw credential into the error** — the workflow's `RunContainerProbeActivity` also redacts the captured `stdout` / `stderr` before persisting or emitting them.

**Retry surface.** `POST /api/v1/units/{name}/revalidate` (allowed only from `Error` / `Stopped`) flips the unit back into `Validating` and dispatches a fresh workflow instance. The CLI (`spring unit revalidate <name>`) wraps this and polls the terminal state.

---

## Path A: Imperative (CLI)

Build up a unit progressively via the CLI:

```bash
# Authenticate with the platform (required once; skipped in local dev mode)
spring auth

# Create the unit with delegated orchestration (workflow container)
spring unit create engineering-team
spring unit set engineering-team \
  --description "Software engineering team" \
  --structure hierarchical \
  --ai-execution delegated \
  --ai-tool software-dev-cycle \
  --ai-environment-image spring-workflows/software-dev-cycle:latest \
  --ai-environment-runtime podman

# Set default execution environment for member agents
spring unit set engineering-team \
  --execution-image spring-agent:latest \
  --execution-runtime podman

# Add agents (creates them if they don't exist)
spring agent create ada \
  --role backend-engineer \
  --capabilities "csharp,python,postgresql" \
  --ai-backend claude \
  --execution delegated \
  --tool claude-code

spring unit members add engineering-team ada
spring unit members add engineering-team kay
spring unit members add engineering-team hopper

# Add a connector
spring connector add github --unit engineering-team \
  --repo savasp/spring
spring connector auth github --unit engineering-team

# Set policies
spring unit set engineering-team \
  --policy communication=hybrid \
  --policy work-assignment=unit-assigns \
  --policy initiative.max-level=proactive

# Add yourself as owner
spring unit humans add engineering-team savasp --permission owner

# Activate
spring unit start engineering-team
```

Each command takes effect immediately — the unit is usable after the first `spring unit create`. You can add agents, connectors, and policies incrementally as you refine the setup.

---

## Path B: Declarative (YAML)

Define everything in version-controlled YAML files and apply in one step:

```yaml
# units/engineering-team.yaml
unit:
  name: engineering-team
  description: Software engineering team
  structure: hierarchical
  ai:
    execution: delegated
    tool: software-dev-cycle
    environment:
      image: spring-workflows/software-dev-cycle:latest
      runtime: podman
  members:
    - agent: agents/ada.yaml           # references agent definition file
    - agent: agents/kay.yaml
    - agent: agents/hopper.yaml
  execution:                           # default for member agents
    image: spring-agent:latest
    runtime: podman
  connectors:
    - type: github
      config:
        repo: savasp/spring
        webhook_secret: ${GITHUB_WEBHOOK_SECRET}
  policies:
    communication: hybrid
    work_assignment: unit-assigns
    initiative:
      max_level: proactive
  humans:
    - identity: savasp
      permission: owner
```

```bash
spring apply -f units/engineering-team.yaml
```

This validates all definitions, creates actors, registers subscriptions, initializes connectors, and reports status. Re-applying performs a diff and applies changes incrementally — no teardown required.

**Export:** `spring unit export engineering-team > engineering-team.yaml` captures the current state as declarative YAML, regardless of how it was built.

---

## Connect External Systems

Connectors that require authentication prompt during apply or can be pre-configured:

```bash
spring connector auth github --unit engineering-team
# Opens OAuth flow or accepts a token
```

Once authenticated, the connector actor begins listening for external events and translating them into messages.

---

## Observe and Interact

```bash
# Watch the unit's activity stream in real-time
spring activity stream --unit engineering-team

# Check agent status
spring agent status --unit engineering-team

# View cost breakdown
spring cost summary --unit engineering-team --period today

# Open the web dashboard
spring dashboard
```

---

## Iterate

```bash
# Imperative changes
spring agent create new-agent --role qa-engineer ...
spring unit members add engineering-team new-agent
spring unit members remove engineering-team hopper
spring unit set engineering-team --policy initiative.max-level=autonomous

# Or declarative: edit YAML and re-apply
spring apply -f units/engineering-team.yaml
```

---

## Teardown

```bash
spring unit delete engineering-team
```

Stops all agents, deactivates actors, cleans up subscriptions and execution environments. Agent state and activity history are retained (soft delete) for audit and potential recovery.

---

## See Also

- [Units](units.md) — unit entity model, membership, nested units, sub-unit creation surfaces
- [Agents](agents.md) — agent model, cloning, prompt assembly
- [Orchestration](orchestration.md) — orchestration strategies; execution defaults; boundary
- [Workflows](workflows.md) — Dapr Workflow integration; `UnitValidationWorkflow` rationale
- [CLI & Web](cli-and-web.md) — full CLI reference
- [ADR-0024](../decisions/0024-unit-validation-as-dapr-workflow.md) — why validation runs as a Dapr Workflow
