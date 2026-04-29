# End-to-end CLI test scenarios

Shell-based scenarios that exercise the running SV v2 stack. Unlike the
unit/integration suite, these hit real containers and can catch wiring
regressions the mocked harness misses (see #311 for rationale).

## Prerequisites

- A running stack (Podman or `dapr run`-launched) reachable at `http://localhost`.
- `bash`, `curl`, `jq` (the last is only required for `--sweep`).
- `dotnet` (.NET 10 SDK) on PATH for the CLI-driven scenarios. To skip the
  build wait, override `SPRING_CLI` with a path to a prebuilt binary.

## Layout

```
tests/e2e/scenarios/
├── fast/         no-LLM scenarios (CRUD, membership, templates, help)
└── llm/          LLM-backed scenarios (empty until #330 lands a local backend)
```

Scenarios are classified by whether they need a running LLM. The fast pool is
the default every invocation runs; the llm pool is an explicit opt-in with
`--llm` (or `--all`) and requires `LLM_BASE_URL`.

## Usage

```
./run.sh                              # all fast scenarios (default)
./run.sh --llm                        # all llm scenarios (needs LLM_BASE_URL)
./run.sh --all                        # both pools, fast first
./run.sh '12-*'                       # glob across both pools
./run.sh --sweep                      # orphan cleanup (see below)
E2E_BASE_URL=http://sv:80 ./run.sh    # custom host
SPRING_CLI=/usr/local/bin/spring ./run.sh   # prebuilt CLI
SPRING_API_URL=http://sv:80 ./run.sh        # forwarded to `spring apply`
```

Each scenario exits 0 on pass, non-zero on any failure. The runner aggregates
results and exits non-zero if any scenario failed. `--llm` without
`LLM_BASE_URL` exits 2 with a pointer to #330.

## Run identity and concurrent invocations

`run.sh` generates one `E2E_RUN_ID` (e.g. `1744560123-54321`, timestamp-pid) at
the top of the batch and exports it so every scenario derives its unit/agent
names from the same id. Combined with the static `E2E_PREFIX` (default `e2e`),
generated names look like `e2e-1744560123-54321-scratch`.

- `E2E_PREFIX` — override the leading segment (CI typically sets
  `E2E_PREFIX=e2e-ci` or `E2E_PREFIX=e2e-dev` so sweeps stay lane-local).
- `E2E_RUN_ID` — override the per-run id. Useful for reproducing a specific
  run's artefacts; leave unset for a fresh id each time.

Two concurrent `./run.sh` invocations (or two back-to-back on a shared
environment) do **not** collide, because each invocation generates its own id.
Scenarios source `_lib.sh` and call `e2e::unit_name <suffix>` or
`e2e::agent_name <suffix>` to derive names, rather than embedding the prefix
inline.

### Scenario 04 (now concurrent-safe)

Scenario `fast/04-create-unit-from-template.sh` used to be `@serial` because
the from-template endpoint derived the unit's `name` from the template's
manifest verbatim (`engineering-team`), and two concurrent runs collided on
the server's unique-name constraint. #325 added an optional `UnitName`
override to `CreateUnitFromTemplateRequest`, and the scenario now passes a
run-scoped id through the CLI's `--name` flag (#316). It is therefore
concurrent-safe alongside the rest of the suite.

## Cascading cleanup

Scenarios set a single `EXIT` trap that calls `e2e::cleanup_unit` (and
optionally `e2e::cleanup_agent`) with every artefact they created. The helper
invokes `spring unit purge --confirm <name>` per unit, which cascades through
every membership row before deleting the unit itself. Failures during cleanup
are logged but swallowed so they can never mask the scenario's real exit
code. Because every artefact carries the run-id prefix, `--sweep` remains
the backstop if a scenario aborts before the trap can fire (e.g. killed -9).

## Orphan cleanup (`--sweep`)

When a scenario aborts mid-way (ctrl-c, assertion failure, network hiccup) it
may leave units or agents behind. `./run.sh --sweep` enumerates every unit
whose name starts with `${E2E_PREFIX}-` via `spring unit list --output json`,
enumerates agents the same way via `spring agent list --output json`, and
deletes them. It prints a summary with the count cleaned up.

```
./run.sh --sweep                       # wipe orphans under the default prefix
E2E_PREFIX=e2e-ci ./run.sh --sweep     # wipe CI orphans only
```

Sweep never runs implicitly — it is an explicit `--sweep`-only invocation —
so a stray sweep cannot delete another concurrent run's in-flight artefacts.

## CLI vs HTTP

Scenarios prefer the `spring` CLI when a viable subcommand exists, because the
CLI exercises three layers raw `curl` skips: the Kiota-generated client (which
breaks if `openapi.json` drifts), CLI argument parsing + output formatting, and
the `ApiTokenAuthHandler` Bearer-token path. Scenarios that have no CLI
counterpart stay on `e2e::http` with a TODO referencing the gap.

| # | Scenario | Pool | Driver | Why |
|---|----------|------|--------|-----|
| 01 | api-health | fast | curl | Raw contract check; the point is to bypass the CLI/Kiota layer. |
| 02 | create-unit-scratch | fast | CLI (`spring unit create`) | Covered by the CLI today. |
| 03 | create-unit-with-model | fast | CLI (`spring unit create --model/--color`) | #315 exposed the flags. |
| 04 | create-unit-from-template | fast | CLI (`spring unit create --from-template`) | #316 exposed the CLI path; #325 added the `UnitName` override that makes this scenario concurrent-safe. |
| 05 | cli-version-and-help | fast | CLI (`spring --help`) | Sanity-check the CLI starts up before heavier scenarios spend API time. |
| 06 | unit-membership-roundtrip | fast | CLI (`spring unit members …`) | Full CLI coverage of #320. |
| 07 | create-start-unit | fast | CLI (`spring unit start / status`) | Lifecycle path through `Running` (or `Starting`). |
| 12 | nested-units | fast | CLI (`spring unit members add --unit`) | #331 added the `--unit` flag so the scenario drops its HTTP fallback. |
| 13 | agent-domain-message | fast | CLI + curl | #404: asserts `MessageReceived` persists after a Domain message to an agent, proving the router → actor → activity-bus path without an LLM. |
| 14 | conversation-lifecycle | fast | CLI + curl | #404: verifies `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)` all fire when a fresh thread kicks off on an idle agent. |
| 15 | unit-policy-roundtrip | fast | CLI + curl | #404: GET empty → PUT (skill + model) → GET round-trip → PUT clear → GET empty; also asserts 404 for an unknown unit. |
| 16 | cost-api-shape | fast | CLI + curl | #404: fresh agent/unit return the full CostSummaryResponse with zero counters and a valid time window; explicit from/to override is honoured. |
| 17 | activity-query-filters | fast | curl | #404: asserts the four server-side filters on `/api/v1/activity` (source, eventType, severity, pageSize) actually narrow results — complements the SSE path until a cross-host event bridge lands. |

## Authentication

The CLI reads its endpoint and token from `~/.spring/config.json` (see
`src/Cvoya.Spring.Cli/CliConfig.cs`). `spring apply` additionally honours
`SPRING_API_URL` as an override. When the API is launched with
`LocalDev=true`, no token is required and the harness can run without
configuring one.

## Verifying membership changes

Every scenario that creates, adds, or removes a member MUST cross-verify
the change across BOTH read paths:

1. The CLI `spring unit members list <unit> --output json` (exercises the
   Kiota-generated client and the CLI formatting layer).
2. The HTTP endpoint `GET /api/v1/units/{id}/memberships` (reads the DB
   membership table — the Agents tab's source of truth).

The scenario must assert these two paths AGREE (same count, same agent
addresses). During #340, the two stores drifted: `membersAdded` in the
response was non-zero, the actor's in-memory member list looked populated,
but the DB membership table stayed empty, so `/memberships` and `/agents`
returned `[]` while the CLI's read still reported success via the
actor-state path. Asserting only one side let the regression slip past the
suite. The two-path check catches that class of bug immediately.

Where it matters for membership counts (e.g. template creation, which adds
an exact known number of agents), also cross-check `GET /units/{id}/agents`
so all three read paths (CLI, /memberships, /agents) must agree.

## Adding a scenario

Create `scenarios/{fast,llm}/NN-short-name.sh`, source `../../_lib.sh`, use
`e2e::cli` (or `e2e::http` for raw checks), `e2e::expect_status`,
`e2e::expect_contains`. Derive every unit/agent name from `e2e::unit_name
<suffix>` or `e2e::agent_name <suffix>` so `--sweep` can identify orphans and
two concurrent invocations of `./run.sh` never collide. Wire cleanup through
an EXIT trap that calls `e2e::cleanup_unit` with every unit you created. End
with `e2e::summary`.

## Tracking

See issue #311 for the full roadmap and future scenario list. #404 tracks the
ongoing fast-pool expansion for messaging, conversation lifecycle, policy,
cost, and activity coverage (scenarios 13–17). CLI gaps discovered while
porting scenarios live under #315, #316, and #331. The LLM-backed scenario
pool is tracked by #330; the secrets, SSE-push, and clone-lifecycle items
from #404 are deferred until the supporting plumbing (pass-through secret
encryption defaults, cross-host activity bridging, and workflow-driven
clone liveness) stabilises on main.
