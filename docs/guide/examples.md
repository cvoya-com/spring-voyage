# Runnable Examples

The end-to-end scenario suite under [`tests/e2e/scenarios/`](../../tests/e2e/scenarios/) is more than a regression safety net — each script is a self-contained usage example that drives the real `spring` CLI against a running stack. Reading them is the fastest way to see how a given feature is used today; executing them is the fastest way to validate a fresh environment.

Every scenario:

- Sources [`tests/e2e/_lib.sh`](../../tests/e2e/_lib.sh) for shared helpers (`e2e::cli`, `e2e::http`, `e2e::expect_status`, …).
- Generates run-scoped names (`e2e-<runid>-<suffix>`) so two concurrent runs never collide.
- Wires cascading teardown through an `EXIT` trap so it cleans up after itself even on assertion failure.

See [`tests/e2e/README.md`](../../tests/e2e/README.md) for prerequisites (Podman/Dapr stack, `bash`, `curl`, `jq`, .NET 10 SDK) and the `./run.sh` harness. By default `./run.sh` runs every `fast/` scenario; `--llm` opts into the LLM-backed pool, which needs a reachable Ollama server at `$LLM_BASE_URL`.

## Fast scenarios (no LLM required)

These run against a stack with no LLM backend and are safe for CI.

| # | Scenario | What it demonstrates |
|---|----------|----------------------|
| 01 | [`fast/01-api-health.sh`](../../tests/e2e/scenarios/fast/01-api-health.sh) | Raw HTTP smoke check — `GET /api/v1/connectors` returns a JSON array. Use this to confirm the stack is up before investigating deeper failures. |
| 02 | [`fast/02-create-unit-scratch.sh`](../../tests/e2e/scenarios/fast/02-create-unit-scratch.sh) | Minimal `spring unit create` + `spring unit list` round-trip. Exercises directory registration without touching actor metadata. |
| 03 | [`fast/03-create-unit-with-model.sh`](../../tests/e2e/scenarios/fast/03-create-unit-with-model.sh) | `spring unit create --model --color` — goes through the Dapr actor's `SetMetadataAsync` path, which is where actor-wiring bugs typically surface. |
| 04 | [`fast/04-create-unit-from-template.sh`](../../tests/e2e/scenarios/fast/04-create-unit-from-template.sh) | `spring unit create --from-template software-engineering/engineering-team --name <override>` with three-way verification (CLI `members list`, HTTP `/memberships`, HTTP `/agents`) that the template's three agents are reachable from every read path. |
| 05 | [`fast/05-cli-version-and-help.sh`](../../tests/e2e/scenarios/fast/05-cli-version-and-help.sh) | CLI sanity check — `spring --help` starts cleanly and exposes the expected subcommands. Runs before heavier scenarios to catch CLI startup regressions early. |
| 06 | [`fast/06-unit-membership-roundtrip.sh`](../../tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh) | Full membership CRUD — `spring unit members add` with per-membership overrides (`--model`, `--specialty`, `--enabled`, `--execution-mode`), upsert via `members config`, remove, and cascading `spring unit purge --confirm` including the refusal path when `--confirm` is omitted. |
| 07 | [`fast/07-create-start-unit.sh`](../../tests/e2e/scenarios/fast/07-create-start-unit.sh) | Template create + `spring unit start` + poll for `Running`/`Starting` status — the lifecycle path you run after first-time setup. |
| 12 | [`fast/12-nested-units.sh`](../../tests/e2e/scenarios/fast/12-nested-units.sh) | Nested units — `spring unit members add <parent> --unit <child>` with verification that the child appears in both the parent actor's status payload and the CLI's joined members list. |

## LLM scenarios (require Ollama)

Opt in with `./run.sh --llm` (or `--all`). Each of these self-skips cleanly when the Ollama server defined by `$LLM_BASE_URL` is unreachable, but in interactive runs they are the best proof that the full inference path wires correctly.

| # | Scenario | What it demonstrates |
|---|----------|----------------------|
| 20 | [`llm/20-message-human-to-agent.sh`](../../tests/e2e/scenarios/llm/20-message-human-to-agent.sh) | Human-to-agent round-trip — create unit + agent + membership, then `spring message send agent://<agent>` with a conversation id. Asserts the send succeeds and a `messageId` is returned. |
| 30 | [`llm/30-policy-block-at-turn-time.sh`](../../tests/e2e/scenarios/llm/30-policy-block-at-turn-time.sh) | Policy enforcement at turn time — dispatches a message that would otherwise exercise a blocked tool, proving the server doesn't 5xx when a policy denies the action server-side. |
| 40 | [`llm/40-dapr-agent-turn.sh`](../../tests/e2e/scenarios/llm/40-dapr-agent-turn.sh) | Dapr Agent via A2A — creates an agent with `--tool dapr-agent`, dispatches a turn, and confirms the DaprAgentLauncher + Python Dapr Agent container can receive a task and return a response. |

## Running a single scenario

The runner accepts a glob against both pools:

```
cd tests/e2e
./run.sh '02-*'              # just 02-create-unit-scratch
./run.sh 'fast/06-*'          # just the membership round-trip
E2E_PREFIX=e2e-dev ./run.sh --llm '30-*'   # LLM-only, dev lane
```

Set `SPRING_CLI=/path/to/prebuilt` to skip the per-invocation `dotnet build` wait. `SPRING_API_URL` is forwarded to `spring apply`; `E2E_BASE_URL` overrides where the scenarios send HTTP traffic.

## Adding a new scenario

Drop a new script under `fast/` or `llm/`, source `../../_lib.sh`, derive unit/agent names with `e2e::unit_name` / `e2e::agent_name` (so `--sweep` can orphan-collect them), and wire an `EXIT` trap to `e2e::cleanup_unit` / `e2e::cleanup_agent`. End with `e2e::summary`. See existing scenarios for the shape — each opens with a short header comment explaining what the scenario proves, which is what populates this catalog.

## Related reading

- [Getting Started](getting-started.md) — the same flows walked through step-by-step.
- [Managing Units and Agents](units-and-agents.md) — the CLI reference these scenarios exercise.
- [`tests/e2e/README.md`](../../tests/e2e/README.md) — runner, prerequisites, and conventions.
