# CLI scenarios

Shell-based, narrative scenarios that drive the real `spring` CLI and the
Web API against a live v0.1 stack. Companion to
[`tests/e2e-portal/`](../e2e-portal/) (Playwright-driven portal tests).

Each scenario is a single self-contained bash script: arrange (create the
unit / agent / token), act (drive the CLI verb under test), assert
(`spring … show`, `GET /api/v1/…`), clean up (cascading `unit purge` via
the EXIT trap). No frameworks, no fixtures, no shared state — what you
read is what runs.

> Why shell? The CLI is shell-friendly, the test infra is shell-friendly,
> and the goal is "make it cheap to add a regression," not "build a new
> framework." See issue #1602 for the decision and tradeoffs.

## Prerequisites

- A running stack (Podman or `dapr run`-launched) reachable at `http://localhost`.
- `bash`, `curl`, `jq` (the last is only required for `--sweep`).
- `dotnet` (.NET 10 SDK) on PATH for the CLI-driven scenarios. To skip the
  build wait, override `SPRING_CLI` with a path to a prebuilt binary.

## Layout

```
tests/cli-scenarios/
├── README.md           narrative + runner documentation (this file)
├── run.sh              master runner — globs scenarios, filters by pool
├── _lib.sh             shared helpers (e2e::cli, e2e::http, expect_*, …)
└── scenarios/          one subfolder per domain; one .sh per journey
    ├── activity/       /api/v1/activity query filters
    ├── agents/         persistent-agent CLI, dapr-agent turn (llm)
    ├── api/            raw API health / smoke checks
    ├── auth/           bootstrap, token create/list/revoke, Bearer use
    ├── cli-meta/       CLI version / help / startup
    ├── cost/           cost API shape, analytics costs breakdown
    ├── directory/      tenant-wide expertise directory list/search/show
    ├── engagements/    engagement list / thread show observability surface
    ├── exit-codes/     parse-level + server-side exit-code contract
    ├── github-app/     GitHub App secret / key rotation
    ├── humans/         unit humans CRUD CLI
    ├── messaging/      message dispatch, multi-turn, multi-agent (llm)
    ├── orchestration/  unit orchestration strategy round-trip
    ├── packages/       catalog install (incl. spring-voyage-oss)
    ├── policy/         unit policy CRUD (HTTP + CLI), llm enforcement
    ├── secrets/        secret CRUD + rotation across scopes
    └── units/          unit CRUD, membership, lifecycle, nesting, delete, image defaults
```

Domain buckets group scenarios by what an operator would search for, not
by filename order. There is no top-level `fast/` vs `llm/` split — each
scenario declares its pool inline (see below).

Scenarios drive only the Web API + CLI surface that real users see.
Anything that would require bringing up its own hosts, talking to
Postgres directly, or otherwise bypassing the deployed stack belongs in
`tests/Cvoya.Spring.Integration.Tests/` instead.

## Pools (the `# pool:` header)

Every scenario declares its execution pool on line 2, immediately after
the shebang:

```bash
#!/usr/bin/env bash
# pool: fast
# Brief one-line summary of the scenario.
set -euo pipefail
…
```

Two pools are defined today:

- `pool: fast` — no LLM, no container runtime beyond a running stack
  reachable via the Web API. Default for every invocation.
- `pool: llm` — needs a live LLM backend reachable via Ollama
  (`LLM_BASE_URL`). Opt-in with `--llm` (or `--all`). Each `llm` scenario
  also calls `e2e::require_ollama` first and skips cleanly when Ollama
  isn't reachable.

The runner reads the header to decide whether to include a scenario in a
given invocation. Forgetting the header means the runner skips the
scenario with a warning — never silently runs it.

## Usage

```bash
./run.sh                              # all fast-pool scenarios (default)
./run.sh --llm                        # all llm-pool scenarios (needs Ollama)
./run.sh --all                        # all pools, fast first
./run.sh 'unit-*'                     # glob over scenario basenames across all pools
./run.sh --sweep                      # orphan cleanup (see below)
E2E_BASE_URL=http://sv:80 ./run.sh    # custom host
SPRING_CLI=/usr/local/bin/spring ./run.sh   # prebuilt CLI
SPRING_API_URL=http://sv:80 ./run.sh        # forwarded to `spring apply`
```

Glob matches are against the scenario basename (e.g. `unit-create-scratch`),
not the path — callers don't need to remember which domain bucket a
scenario lives in.

Each scenario exits 0 on pass, non-zero on any failure. The runner
aggregates results and exits non-zero if any scenario failed. `--llm`
without a reachable Ollama exits 2 with a pointer to
`docs/developer/local-ai-ollama.md`.

## Run identity and concurrent invocations

`run.sh` generates one `E2E_RUN_ID` (e.g. `1744560123-54321`,
timestamp-pid) at the top of the batch and exports it so every scenario
derives its unit/agent names from the same id. Combined with the static
`E2E_PREFIX` (default `e2e`), generated names look like
`e2e-1744560123-54321-scratch`.

- `E2E_PREFIX` — override the leading segment (CI typically sets
  `E2E_PREFIX=e2e-ci` or `E2E_PREFIX=e2e-dev` so sweeps stay lane-local).
- `E2E_RUN_ID` — override the per-run id. Useful for reproducing a
  specific run's artefacts; leave unset for a fresh id each time.

Two concurrent `./run.sh` invocations (or two back-to-back on a shared
environment) do **not** collide, because each invocation generates its
own id. Scenarios source `_lib.sh` and call `e2e::unit_name <suffix>` or
`e2e::agent_name <suffix>` to derive names, rather than embedding the
prefix inline.

## Cascading cleanup

Scenarios set a single `EXIT` trap that calls `e2e::cleanup_unit` (and
optionally `e2e::cleanup_agent`) with every artefact they created. The
helper invokes `spring unit purge --confirm <name>` per unit, which
cascades through every membership row before deleting the unit itself.
Failures during cleanup are logged but swallowed so they can never mask
the scenario's real exit code. Because every artefact carries the run-id
prefix, `--sweep` remains the backstop if a scenario aborts before the
trap can fire (e.g. killed -9).

## Orphan cleanup (`--sweep`)

When a scenario aborts mid-way (ctrl-c, assertion failure, network
hiccup) it may leave units or agents behind. `./run.sh --sweep`
enumerates every unit whose name starts with `${E2E_PREFIX}-` via
`spring unit list --output json`, enumerates agents the same way via
`spring agent list --output json`, and deletes them. It prints a summary
with the count cleaned up.

```bash
./run.sh --sweep                       # wipe orphans under the default prefix
E2E_PREFIX=e2e-ci ./run.sh --sweep     # wipe CI orphans only
```

Sweep never runs implicitly — it is an explicit `--sweep`-only invocation
— so a stray sweep cannot delete another concurrent run's in-flight
artefacts.

## CLI vs HTTP

Scenarios prefer the `spring` CLI when a viable subcommand exists,
because the CLI exercises three layers raw `curl` skips: the
Kiota-generated client (which breaks if `openapi.json` drifts), CLI
argument parsing + output formatting, and the `ApiTokenAuthHandler`
Bearer-token path. Scenarios that have no CLI counterpart stay on
`e2e::http` with a TODO referencing the gap.

| Domain | Scenario | Pool | Driver | Why |
|--------|----------|------|--------|-----|
| api | api-health | fast | curl | Raw contract check; the point is to bypass the CLI/Kiota layer. |
| units | unit-create-scratch | fast | CLI (`spring unit create`) | Covered by the CLI today. |
| units | unit-create-with-model | fast | CLI (`spring unit create --model/--color`) | #315 exposed the flags. |
| units | unit-create-from-template | fast | CLI (`spring unit create --from-template`) | #316 exposed the CLI path; #325 added the `UnitName` override that makes this scenario concurrent-safe. |
| cli-meta | cli-version-and-help | fast | CLI (`spring --help`) | Sanity-check the CLI starts up before heavier scenarios spend API time. |
| units | unit-membership-roundtrip | fast | CLI (`spring unit members …`) | Full CLI coverage of #320. |
| units | unit-create-and-start | fast | CLI (`spring unit start / status`) | Lifecycle path through `Running` (or `Starting`). |
| units | unit-nested | fast | CLI (`spring unit members add --unit`) | #331 added the `--unit` flag so the scenario drops its HTTP fallback. |
| messaging | agent-domain-message | fast | CLI + curl | #404: asserts `MessageReceived` persists after a Domain message to an agent, proving the router → actor → activity-bus path without an LLM. |
| messaging | conversation-lifecycle | fast | CLI + curl | #404: verifies `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)` all fire when a fresh thread kicks off on an idle agent. |
| policy | unit-policy-http-roundtrip | fast | CLI + curl | #404: GET empty → PUT (skill + model) → GET round-trip → PUT clear → GET empty; also asserts 404 for an unknown unit. |
| cost | cost-api-shape | fast | CLI + curl | #404: fresh agent/unit return the full CostSummaryResponse with zero counters and a valid time window; explicit from/to override is honoured. |
| activity | activity-query-filters | fast | curl | #404: asserts the four server-side filters on `/api/v1/activity` (source, eventType, severity, pageSize) actually narrow results — complements the SSE path until a cross-host event bridge lands. |
| policy | unit-policy-cli-roundtrip | fast | CLI | #453: per-dimension `spring unit policy <dim> get/set/clear` roundtrip; proves merge doesn't clobber adjacent slots. |
| humans | unit-humans-cli | fast | CLI | #454: `spring unit humans add/remove/list` lifecycle. |
| agents | persistent-agent-cli | fast | CLI | #396: exercises the error paths for `spring agent deploy/logs/undeploy/scale` without a container runtime. Happy-path tracked by #1390. |
| secrets | secret-cli | fast | CLI | #432: `spring secret create/list/rotate/versions/prune/get/delete` roundtrip across all three scopes. |
| exit-codes | validation-exit-codes | fast | CLI | #990 / #311: asserts exit-code table appears in `--help`; parse-level rejection exits non-zero; server-side validation failure maps to the 20..27 range. |
| auth | bootstrap-and-auth | fast | CLI + curl | #311: `spring auth token create/list/revoke` lifecycle; token usability via Bearer header. |
| cost | analytics-costs-breakdown | fast | CLI | #554 / E1-A: `spring analytics costs` (scalar total) and `--by-source` / `--breakdown` per-source rollup; `--window` flag accepted; bad window value exits non-zero. |
| github-app | github-app-rotate | fast | CLI | #636 / E1-A: `spring github-app rotate-key --dry-run` (preamble + dry-run exit 0); `--from-file` PEM validation; missing file exits non-zero; `rotate-webhook-secret --dry-run` generates secret without persisting. |
| exit-codes | exit-code-mapping | fast | CLI | #990 / E1-A: `ApiExceptionRenderer.DetermineExitCode` path — 404 on non-existent unit revalidate exits non-zero; revalidate on Draft unit exits non-zero (20..27 if ProblemDetails carries a code extension, 1 otherwise); E1-A canary asserts `--by-source` in analytics costs help. |
| units | omnibus-image-default | fast | CLI | Asserts persistent-agent default image resolves to the omnibus image when the agent declares no explicit image. |
| messaging | message-human-to-agent | llm | CLI | Human-to-agent round-trip — `spring message send agent://<agent>` with a conversation id. Asserts the send succeeds and a `messageId` is returned. |
| policy | policy-block-at-turn-time | llm | CLI + curl | Policy enforcement at turn time — dispatches a message that would otherwise exercise a blocked tool; proves the server doesn't 5xx when a policy denies the action server-side. |
| agents | dapr-agent-turn | llm | CLI | Dapr Agent via A2A — creates an agent with `--tool dapr-agent`, dispatches a turn, and confirms the DaprAgentLauncher + Python Dapr Agent container can receive a task and return a response. |
| units | unit-create-from-template | fast | CLI + curl | Catalog install of `software-engineering` package; cross-verifies the engineering-team unit + 3 agents across CLI `members list`, HTTP `/memberships`, and HTTP `/agents`. Replaces the deleted `unit create --from-template` path (#1583). |
| units | unit-and-agent-delete | fast | CLI | Plain `agent delete` and `unit delete` verbs (non-cascading) plus their idempotency on missing artefacts — complementary to the cascading `purge` paths. |
| orchestration | orchestration-strategies-roundtrip | fast | CLI | `spring unit orchestration {get,set,clear}` round-trip across the three platform-offered strategies (ai, workflow, label-routed). |
| directory | directory-discover-agents | fast | CLI | Seed an expertise domain on a unit, discover via `directory list` / `search` / `show`. |
| packages | package-install-spring-voyage-oss | fast | CLI | Install the spring-voyage-oss meta-package (5 units, 13 agents); verify all sub-units appear and the engineering sub-unit has its members wired. |
| engagements | engagement-list-and-show | fast | CLI | `spring engagement list` and `spring thread show` round-trip; engagement index polling guarded against the eventual-consistency window. |
| messaging | multi-turn-conversation | llm | CLI | Three-turn conversation with a Dapr Agent on a single thread id; verifies each turn produces a fresh agent reply. |
| messaging | multi-agent-engagement | llm | CLI | Two dapr-agents in one unit; observe both engagements via `engagement list --agent`. |

## Authentication

The CLI reads its endpoint and token from `~/.spring/config.json` (see
`src/Cvoya.Spring.Cli/CliConfig.cs`). `spring apply` additionally honours
`SPRING_API_URL` as an override. When the API is launched with
`LocalDev=true`, no token is required and the harness can run without
configuring one.

## Verifying membership changes

Every scenario that creates, adds, or removes a member MUST cross-verify
the change across BOTH read paths:

1. The CLI `spring unit members list <unit> --output json` (exercises
   the Kiota-generated client and the CLI formatting layer).
2. The HTTP endpoint `GET /api/v1/units/{id}/memberships` (reads the DB
   membership table — the Agents tab's source of truth).

The scenario must assert these two paths AGREE (same count, same agent
addresses). During #340, the two stores drifted: `membersAdded` in the
response was non-zero, the actor's in-memory member list looked
populated, but the DB membership table stayed empty, so `/memberships`
and `/agents` returned `[]` while the CLI's read still reported success
via the actor-state path. Asserting only one side let the regression
slip past the suite. The two-path check catches that class of bug
immediately.

Where it matters for membership counts (e.g. template creation, which
adds an exact known number of agents), also cross-check
`GET /units/{id}/agents` so all three read paths (CLI, /memberships,
/agents) must agree.

## CI integration

The harness runs via `.github/workflows/e2e-cli.yml` on a **weekly
schedule** (Mondays 06:00 UTC) and on **manual trigger**
(`workflow_dispatch`). It is intentionally opt-in and not part of the
per-PR required-checks gate — standing up Postgres + the API host takes
several minutes and would slow every PR merge.

**Gating rationale:** the unit + integration suite in `ci.yml` catches
logic regressions quickly (< 2 minutes). This harness catches wiring
regressions (actor type-name mismatches, Dapr sidecar misses,
serialisation failures) that the mocked suite can't see. Weekly cadence
ensures regressions surface before a release window.

The workflow runs the **`e2e-fast`** job — every scenario tagged
`# pool: fast`, against a stack brought up for the run.

The LLM-gated companion runs via `.github/workflows/e2e-cli-llm.yml`
monthly, plus on `workflow_dispatch`. It installs Ollama on the runner,
pulls a model, and runs `tests/cli-scenarios/run.sh --llm`.

To trigger a run manually: Actions → "E2E CLI (scheduled / manual)" → Run workflow.

To run a single scenario in CI: use the `scenario_glob` input
(e.g. `unit-*` or `validation-exit-codes`).

## Adding a scenario

Pick the right domain bucket (or add a new one if no existing folder
fits — there's no rule that says only the buckets above can exist). Name
the file after the journey, not the order: `unit-create-scratch.sh`,
`secret-rotate-roundtrip.sh`. Don't prefix with numbers; the runner
sorts deterministically and pool filtering is by header, not path.

Skeleton:

```bash
#!/usr/bin/env bash
# pool: fast
# One-line summary of what this scenario covers.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name my-journey)"
trap 'e2e::cleanup_unit "${unit}"' EXIT

# arrange / act / assert here, using:
#   e2e::cli ...           run the spring CLI
#   e2e::http METHOD PATH  raw HTTP
#   e2e::expect_status     status-code assertion
#   e2e::expect_contains   substring assertion

e2e::summary
```

Every scenario MUST:

- Source `_lib.sh` (always at the relative path `../../_lib.sh`; the
  domain folder makes this consistent).
- Derive every unit/agent name from `e2e::unit_name <suffix>` /
  `e2e::agent_name <suffix>` so `--sweep` can identify orphans and two
  concurrent invocations of `./run.sh` never collide.
- Wire cleanup through an EXIT trap that calls `e2e::cleanup_unit` (and
  `e2e::cleanup_agent` if the scenario creates standalone agents) with
  every artefact created.
- End with `e2e::summary` so the per-scenario pass/fail counters surface
  in the script's stderr.
- Carry a `# pool: fast|llm` header on line 2.

## Tracking

See issue #311 for the original roadmap. #404 tracks the fast-pool
expansion for messaging, conversation lifecycle, policy, cost, and
activity coverage. CLI gaps discovered while porting scenarios live
under #315, #316, and #331. The LLM-backed scenario pool is tracked by
#330. Issue #1602 records the v0.1 refactor that introduced this layout
(domain folders + `# pool:` headers, replacing the old
`tests/e2e/scenarios/{fast,llm}/` split).

Follow-ups filed alongside #311:
- #1388 — migration-safety concurrent API+Worker startup race test (now
  covered as an integration test, not a CLI scenario; this suite is
  API/CLI/portal-only)
- #1389 — multi-tenant isolation scenarios
- #1390 — full persistent-agent deploy/logs/undeploy happy-path
  (requires container runtime)
- #1391 — full Connector E2E scenarios (bind, webhook, unbind)
