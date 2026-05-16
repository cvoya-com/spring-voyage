# Runnable Examples

Two surfaces ship as runnable examples: the **example packages** under `packages/` show the ADR-0043 recursive package shape end-to-end, and the **CLI scenario suite** under `tests/e2e/cli/scenarios/` exercises individual platform features against a running stack.

## Example packages

The `packages/` directory ships two purpose-built examples that pair as a learning sequence — one builds the same problem shape twice, once instance-only and once templated, so the difference between the two authoring styles is visible at a glance. Both install end-to-end against a fresh stack and are documented as step-by-step walkthroughs in the [Declarative configuration guide](declarative.md).

### `example-simple` — instance-only

[`packages/example-simple/`](../../../packages/example-simple/) demonstrates the ADR-0043 recursive folder shape using **concrete instances only** — no templates, no `from:` clones. Every artefact is authored as a literal folder under the conventional subdirectories. Installing it produces one unit (`greeting-team`), two agents that are members of that unit (`friendly-greeter`, `polite-greeter`), and one skill owned by each agent (`say-hello`, `say-hello-formally`). The skills sit inside their owning agent's folder, so they are granted only to that one agent.

Read this package first to see the recursive shape without the type / instance separation; pair with `example-templated` to see what the same problem looks like templated.

- Package README: [`packages/example-simple/README.md`](../../../packages/example-simple/README.md)
- Step-by-step walkthrough: [Building `example-simple` step by step](declarative.md#building-example-simple-step-by-step)

### `example-templated` — template-based

[`packages/example-templated/`](../../../packages/example-templated/) demonstrates **type / instance separation** via templates ([ADR-0043 §5](../../decisions/0043-recursive-package-format.md#5-type-and-instance-templates-are-non-activating-artefact-folders-cloned-by-from)). The same problem shape as `example-simple` — a unit with member agents — but expressed via one `UnitTemplate` (`engineering-team`, with two stamped nested children) and one `AgentTemplate` (`software-engineer`, stamped three times under one concrete unit). Installing it produces one unit and five agents; the duplication that would otherwise be three folders of repeated content is collapsed to three `from: software-engineer` references.

Read this package after `example-simple` to see how `from:` cloning, snapshot binding, and the override-merge rules ([ADR-0043 §5d](../../decisions/0043-recursive-package-format.md#5d-overrides)) apply to a real package.

- Package README: [`packages/example-templated/README.md`](../../../packages/example-templated/README.md)
- Step-by-step walkthrough: [Building `example-templated` step by step](declarative.md#building-example-templated-step-by-step)

## Sample agent images

The `samples/` directory holds runnable agent images that exercise specific SDK contracts. Each sample is small enough to read end-to-end and exists as the deploy target for an integration test elsewhere in the repo.

### `tools-agent-image` — image-tier tool registration

[`samples/tools-agent-image/`](../../../samples/tools-agent-image/) demonstrates registering custom tools from a .NET agent image via `IToolRegistry.Register` and exposing them at `GET /a2a/tools` with `app.MapToolsEndpoint(registry)`. The sample registers two `acme.*` tools on an in-process registry and serves them from a minimal-API host on the standard agent port; the platform's introspector hits the same endpoint at deploy time and caches the declared surface onto the agent's `image_tools` column.

Pair this sample with the [agent-tools developer guide](../developer/agent-tools.md) for the authoring walkthrough.

- Sample README: [`samples/tools-agent-image/README.md`](../../../samples/tools-agent-image/README.md)

## CLI scenario suite

The CLI scenario suite under [`tests/e2e/cli/scenarios/`](../../../tests/e2e/cli/scenarios) is more than a regression safety net — each script is a self-contained usage example that drives the real `spring` CLI against a running stack. Reading them is the fastest way to see how a given feature is used today; executing them is the fastest way to validate a fresh environment.

Every scenario:

- Sources [`tests/e2e/cli/_lib.sh`](../../../tests/e2e/cli/_lib.sh) for shared helpers (`e2e::cli`, `e2e::http`, `e2e::expect_status`, …).
- Generates run-scoped names (`e2e-<runid>-<suffix>`) so two concurrent runs never collide.
- Wires cascading teardown through an `EXIT` trap so it cleans up after itself even on assertion failure.
- Carries a `# pool: fast|llm` header on line 2 so the runner can filter by execution requirements.

See [`tests/e2e/cli/README.md`](../../../tests/e2e/cli/README.md) for prerequisites (Podman/Dapr stack, `bash`, `curl`, `jq`, .NET 10 SDK) and the `./run.sh` harness. By default `./run.sh` runs every `pool: fast` scenario; `--llm` opts into the LLM-backed pool, which needs a reachable Ollama server at `$LLM_BASE_URL`.

## Fast scenarios (no LLM required)

These run against a stack with no LLM backend and are safe for CI.

| Scenario | What it demonstrates |
|----------|----------------------|
| [`api/api-health.sh`](../../../tests/e2e/cli/scenarios/api/api-health.sh) | Raw HTTP smoke check — `GET /api/v1/connectors` returns a JSON array. Use this to confirm the stack is up before investigating deeper failures. |
| [`units/unit-create-scratch.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-scratch.sh) | Minimal `spring unit create` + `spring unit list` round-trip. Exercises directory registration without touching actor metadata. |
| [`units/unit-create-with-model.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-with-model.sh) | `spring unit create --model --color` — goes through the Dapr actor's `SetMetadataAsync` path, which is where actor-wiring bugs typically surface. |
| [`units/unit-create-from-template.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-from-template.sh) | *(Exercises a removed endpoint — `POST /api/v1/units/from-template` — and is scheduled for replacement with a package-install scenario.)* |
| [`cli-meta/cli-version-and-help.sh`](../../../tests/e2e/cli/scenarios/cli-meta/cli-version-and-help.sh) | CLI sanity check — `spring --help` starts cleanly and exposes the expected subcommands. Runs before heavier scenarios to catch CLI startup regressions early. |
| [`units/unit-membership-roundtrip.sh`](../../../tests/e2e/cli/scenarios/units/unit-membership-roundtrip.sh) | Full membership CRUD — `spring unit members add` with per-membership overrides (`--model`, `--specialty`, `--enabled`, `--execution-mode`), upsert via `members config`, remove, and cascading `spring unit purge --confirm` including the refusal path when `--confirm` is omitted. |
| [`units/unit-create-and-start.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-and-start.sh) | `spring unit create` + `spring unit start` + poll for `Running`/`Starting` status — the lifecycle path you run after first-time setup. |
| [`units/unit-nested.sh`](../../../tests/e2e/cli/scenarios/units/unit-nested.sh) | Nested units — `spring unit members add <parent> --unit <child>` with verification that the child appears in both the parent actor's status payload and the CLI's joined members list. |
| [`messaging/agent-domain-message.sh`](../../../tests/e2e/cli/scenarios/messaging/agent-domain-message.sh) | Messaging plumbing — `POST /api/v1/messages` to an agent lands a `MessageReceived` activity event, proving router → actor → activity-bus wiring without needing an LLM backend. |
| [`messaging/conversation-lifecycle.sh`](../../../tests/e2e/cli/scenarios/messaging/conversation-lifecycle.sh) | Conversation state machine — a fresh `ConversationId` triggers `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)` in order. Exercises the upstream half of the dispatch loop. |
| [`policy/unit-policy-http-roundtrip.sh`](../../../tests/e2e/cli/scenarios/policy/unit-policy-http-roundtrip.sh) | Policy CRUD — `GET`/`PUT /api/v1/units/{id}/policy` for the two shipped dimensions (`skill`, `model`): empty → write → read → clear → read, plus 404 on unknown unit. |
| [`cost/cost-api-shape.sh`](../../../tests/e2e/cli/scenarios/cost/cost-api-shape.sh) | Cost aggregation API shape — a brand-new agent/unit/tenant each return a well-formed `CostSummary` with zero counters and a valid time window; explicit `from`/`to` overrides are honoured. |
| [`activity/activity-query-filters.sh`](../../../tests/e2e/cli/scenarios/activity/activity-query-filters.sh) | Activity query filters — asserts that `source`, `eventType`, `severity`, and `pageSize` on `/api/v1/activity` all narrow results correctly. Covers the query path every observability surface (portal, CLI, dashboard) depends on. |

## LLM scenarios (require Ollama)

Opt in with `./run.sh --llm` (or `--all`). Each of these self-skips cleanly when the Ollama server defined by `$LLM_BASE_URL` is unreachable, but in interactive runs they are the best proof that the full inference path wires correctly.

| Scenario | What it demonstrates |
|----------|----------------------|
| [`messaging/message-human-to-agent.sh`](../../../tests/e2e/cli/scenarios/messaging/message-human-to-agent.sh) | Human-to-agent round-trip — create unit + agent + membership, then `spring message send agent:<id>` with a thread id. Asserts the send succeeds and a `messageId` is returned. |
| [`policy/policy-block-at-turn-time.sh`](../../../tests/e2e/cli/scenarios/policy/policy-block-at-turn-time.sh) | Policy enforcement at turn time — dispatches a message that would otherwise exercise a blocked tool, proving the server doesn't 5xx when a policy denies the action server-side. |
| [`agents/spring-voyage-agent-turn.sh`](../../../tests/e2e/cli/scenarios/agents/spring-voyage-agent-turn.sh) | Dapr Agent via A2A — creates an agent with `--agent spring-voyage`, dispatches a turn, and confirms the SpringVoyageAgentLauncher + Python Spring Voyage Agent container can receive a task and return a response. |

## Running a single scenario

The runner accepts a glob against the scenario basenames across both pools:

```
cd tests/e2e/cli
./run.sh 'unit-create-scratch'   # just one scenario
./run.sh 'unit-*'                # every unit scenario across pools
E2E_PREFIX=e2e-dev ./run.sh --llm 'policy-*'   # LLM-only, dev lane
```

Set `SPRING_CLI=/path/to/prebuilt` to skip the per-invocation `dotnet build` wait. `SPRING_API_URL` is forwarded to `spring apply`; `E2E_BASE_URL` overrides where the scenarios send HTTP traffic.

## Adding a new scenario

Pick the right domain bucket under `scenarios/` (or add a new one if no existing folder fits), source `../../_lib.sh`, derive unit/agent names with `e2e::unit_name` / `e2e::agent_name` (so `--sweep` can orphan-collect them), and wire an `EXIT` trap to `e2e::cleanup_unit` / `e2e::cleanup_agent`. End with `e2e::summary`. Add a `# pool: fast|llm` header on line 2 so the runner can include it in the right invocations. See existing scenarios for the shape — each opens with a short header comment explaining what the scenario proves, which is what populates this catalog.

## Related reading

- [Declarative configuration](declarative.md) — step-by-step walkthroughs of the two example packages plus the install-flag reference.
- [Packages](../../concepts/packages.md), [Templates](../../concepts/templates.md) — the concept docs that frame what the example packages demonstrate.
- [Getting Started](../intro/getting-started.md) — the same flows walked through step-by-step.
- [Managing Units and Agents](units-and-agents.md) — the CLI reference these scenarios exercise.
- [`tests/e2e/cli/README.md`](../../../tests/e2e/cli/README.md) — runner, prerequisites, and conventions.
