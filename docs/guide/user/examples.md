# Runnable Examples

The CLI scenario suite under [`tests/cli-scenarios/scenarios/`](../../../tests/cli-scenarios/scenarios) is more than a regression safety net — each script is a self-contained usage example that drives the real `spring` CLI against a running stack. Reading them is the fastest way to see how a given feature is used today; executing them is the fastest way to validate a fresh environment.

Every scenario:

- Sources [`tests/cli-scenarios/_lib.sh`](../../../tests/cli-scenarios/_lib.sh) for shared helpers (`e2e::cli`, `e2e::http`, `e2e::expect_status`, …).
- Generates run-scoped names (`e2e-<runid>-<suffix>`) so two concurrent runs never collide.
- Wires cascading teardown through an `EXIT` trap so it cleans up after itself even on assertion failure.
- Carries a `# pool: fast|llm` header on line 2 so the runner can filter by execution requirements.

See [`tests/cli-scenarios/README.md`](../../../tests/cli-scenarios/README.md) for prerequisites (Podman/Dapr stack, `bash`, `curl`, `jq`, .NET 10 SDK) and the `./run.sh` harness. By default `./run.sh` runs every `pool: fast` scenario; `--llm` opts into the LLM-backed pool, which needs a reachable Ollama server at `$LLM_BASE_URL`.

## Fast scenarios (no LLM required)

These run against a stack with no LLM backend and are safe for CI.

| Scenario | What it demonstrates |
|----------|----------------------|
| [`api/api-health.sh`](../../../tests/cli-scenarios/scenarios/api/api-health.sh) | Raw HTTP smoke check — `GET /api/v1/connectors` returns a JSON array. Use this to confirm the stack is up before investigating deeper failures. |
| [`units/unit-create-scratch.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-scratch.sh) | Minimal `spring unit create` + `spring unit list` round-trip. Exercises directory registration without touching actor metadata. |
| [`units/unit-create-with-model.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-with-model.sh) | `spring unit create --model --color` — goes through the Dapr actor's `SetMetadataAsync` path, which is where actor-wiring bugs typically surface. |
| [`units/unit-create-from-template.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-from-template.sh) | *(Exercises a removed endpoint — `POST /api/v1/units/from-template` — and is scheduled for replacement with a package-install scenario.)* |
| [`cli-meta/cli-version-and-help.sh`](../../../tests/cli-scenarios/scenarios/cli-meta/cli-version-and-help.sh) | CLI sanity check — `spring --help` starts cleanly and exposes the expected subcommands. Runs before heavier scenarios to catch CLI startup regressions early. |
| [`units/unit-membership-roundtrip.sh`](../../../tests/cli-scenarios/scenarios/units/unit-membership-roundtrip.sh) | Full membership CRUD — `spring unit members add` with per-membership overrides (`--model`, `--specialty`, `--enabled`, `--execution-mode`), upsert via `members config`, remove, and cascading `spring unit purge --confirm` including the refusal path when `--confirm` is omitted. |
| [`units/unit-create-and-start.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-and-start.sh) | `spring unit create` + `spring unit start` + poll for `Running`/`Starting` status — the lifecycle path you run after first-time setup. |
| [`units/unit-nested.sh`](../../../tests/cli-scenarios/scenarios/units/unit-nested.sh) | Nested units — `spring unit members add <parent> --unit <child>` with verification that the child appears in both the parent actor's status payload and the CLI's joined members list. |
| [`messaging/agent-domain-message.sh`](../../../tests/cli-scenarios/scenarios/messaging/agent-domain-message.sh) | Messaging plumbing — `POST /api/v1/messages` to an agent lands a `MessageReceived` activity event, proving router → actor → activity-bus wiring without needing an LLM backend. |
| [`messaging/conversation-lifecycle.sh`](../../../tests/cli-scenarios/scenarios/messaging/conversation-lifecycle.sh) | Conversation state machine — a fresh `ConversationId` triggers `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)` in order. Exercises the upstream half of the dispatch loop. |
| [`policy/unit-policy-http-roundtrip.sh`](../../../tests/cli-scenarios/scenarios/policy/unit-policy-http-roundtrip.sh) | Policy CRUD — `GET`/`PUT /api/v1/units/{id}/policy` for the two shipped dimensions (`skill`, `model`): empty → write → read → clear → read, plus 404 on unknown unit. |
| [`cost/cost-api-shape.sh`](../../../tests/cli-scenarios/scenarios/cost/cost-api-shape.sh) | Cost aggregation API shape — a brand-new agent/unit/tenant each return a well-formed `CostSummary` with zero counters and a valid time window; explicit `from`/`to` overrides are honoured. |
| [`activity/activity-query-filters.sh`](../../../tests/cli-scenarios/scenarios/activity/activity-query-filters.sh) | Activity query filters — asserts that `source`, `eventType`, `severity`, and `pageSize` on `/api/v1/activity` all narrow results correctly. Covers the query path every observability surface (portal, CLI, dashboard) depends on. |

## LLM scenarios (require Ollama)

Opt in with `./run.sh --llm` (or `--all`). Each of these self-skips cleanly when the Ollama server defined by `$LLM_BASE_URL` is unreachable, but in interactive runs they are the best proof that the full inference path wires correctly.

| Scenario | What it demonstrates |
|----------|----------------------|
| [`messaging/message-human-to-agent.sh`](../../../tests/cli-scenarios/scenarios/messaging/message-human-to-agent.sh) | Human-to-agent round-trip — create unit + agent + membership, then `spring message send agent:<id>` with a thread id. Asserts the send succeeds and a `messageId` is returned. |
| [`policy/policy-block-at-turn-time.sh`](../../../tests/cli-scenarios/scenarios/policy/policy-block-at-turn-time.sh) | Policy enforcement at turn time — dispatches a message that would otherwise exercise a blocked tool, proving the server doesn't 5xx when a policy denies the action server-side. |
| [`agents/dapr-agent-turn.sh`](../../../tests/cli-scenarios/scenarios/agents/dapr-agent-turn.sh) | Dapr Agent via A2A — creates an agent with `--tool dapr-agent`, dispatches a turn, and confirms the DaprAgentLauncher + Python Dapr Agent container can receive a task and return a response. |

## Running a single scenario

The runner accepts a glob against the scenario basenames across both pools:

```
cd tests/cli-scenarios
./run.sh 'unit-create-scratch'   # just one scenario
./run.sh 'unit-*'                # every unit scenario across pools
E2E_PREFIX=e2e-dev ./run.sh --llm 'policy-*'   # LLM-only, dev lane
```

Set `SPRING_CLI=/path/to/prebuilt` to skip the per-invocation `dotnet build` wait. `SPRING_API_URL` is forwarded to `spring apply`; `E2E_BASE_URL` overrides where the scenarios send HTTP traffic.

## Adding a new scenario

Pick the right domain bucket under `scenarios/` (or add a new one if no existing folder fits), source `../../_lib.sh`, derive unit/agent names with `e2e::unit_name` / `e2e::agent_name` (so `--sweep` can orphan-collect them), and wire an `EXIT` trap to `e2e::cleanup_unit` / `e2e::cleanup_agent`. End with `e2e::summary`. Add a `# pool: fast|llm` header on line 2 so the runner can include it in the right invocations. See existing scenarios for the shape — each opens with a short header comment explaining what the scenario proves, which is what populates this catalog.

## Related reading

- [Getting Started](../intro/getting-started.md) — the same flows walked through step-by-step.
- [Managing Units and Agents](units-and-agents.md) — the CLI reference these scenarios exercise.
- [`tests/cli-scenarios/README.md`](../../../tests/cli-scenarios/README.md) — runner, prerequisites, and conventions.
