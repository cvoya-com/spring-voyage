# End-to-end portal test scenarios (Playwright)

Browser-driven scenarios that exercise the **Spring Voyage portal** UI
against a running v0.1 stack. Sibling to [`tests/e2e/`](../e2e/) (shell-based
CLI/API scenarios). Every workflow here has a counterpart there or is
deliberately portal-only (sidebar, command palette, engagement portal IA,
wizard flows, etc.).

> **v0.1 scope.** This suite assumes a v0.1 deployment is feature-complete and
> ready to test. The user will configure tenant-default secrets on the live
> deployment so units and agents inherit them — every spec pins
> `tool=dapr-agent` + `provider=ollama` (the credential-free local runtime),
> so most flows do not need any operator-supplied secrets.

## Prerequisites

- A running stack reachable at `http://localhost` (single-host docker-compose
  default) or at `PLAYWRIGHT_BASE_URL`. The stack must include the API host
  (`spring-api`) AND the portal (`spring-web`); a CLI-only deployment isn't
  enough.
- For the **`llm`** project: a reachable Ollama server with at least one
  model pulled (default: `llama3.2`). Set `LLM_BASE_URL` if it isn't on
  `http://localhost:11434`.
- Node ≥ 20 and npm.
- `npm install` inside this directory (it is **not** a workspace member; it
  ships its own Playwright dependency to avoid coupling with
  `src/Cvoya.Spring.Web/`).

```bash
cd tests/e2e-portal
npm install
npm run install:browsers   # one-time: download chromium with deps
```

## Layout

```
tests/e2e-portal/
├── playwright.config.ts      # 3 projects: fast / llm / killer
├── fixtures/
│   ├── api.ts                # Direct REST helpers + readiness probes
│   ├── ids.ts                # Run-id naming (mirrors e2e::unit_name)
│   ├── runtime.ts            # tool=dapr-agent, provider=ollama defaults
│   └── test.ts               # Custom test() with auto-cleanup tracker
├── helpers/
│   ├── nav.ts                # Sidebar + route helpers
│   ├── unit-wizard.ts        # 6-step /units/create flow
│   ├── agent-create.ts       # /agents/create form driver
│   └── engagement.ts         # Engagement portal interactions
├── specs/
│   ├── fast/                 # No-LLM: CRUD, IA, settings, panels
│   ├── llm/                  # Real Ollama turn — needs a live LLM
│   └── killer/               # E2 plan's killer use case (templates)
└── scripts/sweep.ts          # Orphan cleanup, mirrors run.sh --sweep
```

## Test pools

| Project   | Includes                                            | Requires                       |
| --------- | --------------------------------------------------- | ------------------------------ |
| `fast`    | Wizard, CRUD, settings, IA, all 36 fast specs       | API + Postgres + portal        |
| `llm`     | Engagement send/turn flows, 3 specs                 | Above + Ollama with a model    |
| `killer`  | Template wizard → engagement, 2 specs               | Above; optionally GitHub App   |

Mirrors the shell suite's `fast` / `llm` / `infra` partitioning. No `infra`
pool here — startup-race scenarios stay shell-only.

## Usage

```bash
# Default: fast pool only.
npm test -- --project=fast

# All pools.
npm test

# Single spec.
npm test -- specs/fast/03-units-create-scratch.spec.ts

# Headed mode for debugging.
npm run test:headed -- --project=fast

# Inspector.
npm run test:debug

# HTML report after a run.
npm run report
```

## Run identity and concurrent invocations

Like the shell suite, every artefact name is `${prefix}-${runId}-${suffix}`
so two concurrent invocations never collide on the server's unique-name
constraint.

| Env var               | Default               | Purpose                                                          |
| --------------------- | --------------------- | ---------------------------------------------------------------- |
| `E2E_PORTAL_PREFIX`   | `e2e-portal`          | Static leading segment. CI lanes set `e2e-portal-ci` etc.        |
| `E2E_PORTAL_RUN_ID`   | `${epoch}-${pid}`     | Per-invocation. Override only to reproduce a specific run.       |
| `PLAYWRIGHT_BASE_URL` | `http://localhost`    | Portal origin.                                                   |
| `SPRING_API_URL`      | (falls back to above) | API base. Resolved separately so the portal can sit behind Caddy.|
| `SPRING_API_TOKEN`    | unset                 | Bearer token for non-LocalDev deployments.                       |
| `LLM_BASE_URL`        | `http://localhost:11434` | Ollama base URL for the `llm` pool.                          |
| `E2E_PORTAL_OLLAMA_MODEL` | `llama3.2`        | Default model picked when the wizard's catalog includes it.      |

The `e2e-portal-` prefix is **distinct** from the shell suite's `e2e-`
prefix so a shell `--sweep` never wipes mid-flight portal artefacts and
vice versa, even when both suites run concurrently against the same stack.

## Cleanup

Two layers, mirrored from the shell suite:

1. **Per-test auto-cleanup** — `fixtures/test.ts` exposes a `tracker`
   fixture. Specs call `tracker.unit(name)` / `tracker.agent(id)` /
   `tracker.token(name)` / `tracker.tenantSecret(name)` to register
   artefacts, and the fixture's `afterEach` deletes them via direct API
   calls. Cleanup errors are attached to the test as
   `cleanup-errors.json` but never fail the test (matches
   `e2e::cleanup_unit`'s swallow-and-log contract).

2. **Orphan sweep** — when a test crashes before its cleanup hook can
   fire (kill -9, machine sleep, network blip):

   ```bash
   npm run sweep
   E2E_PORTAL_PREFIX=e2e-portal-ci npm run sweep
   ```

   The sweep enumerates every unit / agent / tenant secret / API token
   whose name starts with the prefix and deletes them. Sweep never runs
   implicitly — same contract as the shell suite.

## Conventions

- **Naming.** Always derive names from `unitName(suffix)` /
  `agentName(suffix)` etc. Never embed a literal `${PREFIX}` or run id.
  Suffixes must match `/^[a-z0-9-]+$/` (the API rejects anything else).
- **Cleanup.** Always pass through the tracker. The auto-cleanup hook
  is the only thing keeping the suite re-runnable.
- **No portal-private API.** Everything the suite POSTs to is on the
  public Web API ([ADR-0029](../../docs/decisions/0029-tenant-execution-boundary.md)).
- **Runtime pin.** `dapr-agent` + `ollama` everywhere. See
  [`fixtures/runtime.ts`](fixtures/runtime.ts) for the rationale.
- **No webServer.** The local stack is operator-managed; the runner
  doesn't boot one. Prevents port collisions with the shell suite.
- **`data-testid` first.** The portal exposes 539+ test ids; prefer them
  over text-matching for stable selectors.

## Adding a scenario

1. Pick a pool (`fast`, `llm`, or `killer`).
2. Create `specs/<pool>/NN-short-name.spec.ts`.
3. Import from `@fixtures/test` (NOT `@playwright/test`) so cleanup
   wiring can't be silently bypassed.
4. Derive every artefact name through `unitName(suffix)` etc.
5. Register every created artefact with the tracker.
6. End by asserting on user-visible state, not internal API responses
   (the API-only assertion is fine as a *cross-check*, not the
   primary outcome).

Example skeleton:

```ts
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { createScratchUnit } from "../../helpers/unit-wizard.js";

test.describe("units — my new flow", () => {
  test("does the thing", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("my-flow"));
    await createScratchUnit(page, { name });
    await expect(page.getByRole("heading", { name })).toBeVisible();
  });
});
```

## Coverage map

The fast pool covers every primary management-portal route plus the
engagement-portal shell. The killer pool drives the v0.1 north-star flow
([Area E2](../../docs/plan/v0.1/areas/e2-new-ux.md)). The llm pool
exercises the agent turn end-to-end against a real LLM.

| Pool   | Spec                                     | Surface                                            |
| ------ | ---------------------------------------- | -------------------------------------------------- |
| fast   | `01-portal-shell`                        | Boot, IA, dark mode                                |
| fast   | `02-auth-tokens`                         | Settings → API tokens lifecycle                    |
| fast   | `03-units-create-scratch`                | Wizard scratch flow                                |
| fast   | `04-units-create-from-template`          | Wizard template flow (engineering + product)       |
| fast   | `05-units-create-from-yaml`              | Wizard YAML flow                                   |
| fast   | `06-units-sub-unit`                      | Wizard parent picker                               |
| fast   | `07-units-detail-tabs`                   | All detail-page tabs render                        |
| fast   | `08-units-lifecycle`                     | Start / stop / delete                              |
| fast   | `09-units-policy`                        | Five policy dimensions                             |
| fast   | `10-units-boundary`                      | Boundary YAML upload + diff                        |
| fast   | `11-units-orchestration`                 | Strategy roundtrip                                 |
| fast   | `12-units-execution-defaults`            | Image/runtime/model defaults                       |
| fast   | `13-units-secrets`                       | Unit-scoped secret CRUD                            |
| fast   | `14-agents-create`                       | Agent create form                                  |
| fast   | `15-agents-detail`                       | Agent detail panels                                |
| fast   | `16-agents-membership`                   | Membership dialog roundtrip                        |
| fast   | `17-agents-budget`                       | Per-agent budget panel                             |
| fast   | `18-agents-expertise`                    | Expertise editor                                   |
| fast   | `19-connectors-list`                     | Connector cards + GitHub detail                    |
| fast   | `20-connectors-bind`                     | Unit binding clear path                            |
| fast   | `21-engagement-portal-shell`             | /engagement IA + back-to-management                |
| fast   | `22-engagement-mine`                     | /engagement/mine list states                       |
| fast   | `23-activity-feed`                       | Activity sparkline + feed                          |
| fast   | `24-inbox`                               | Inbox states                                       |
| fast   | `25-analytics`                           | Costs / throughput / waits                         |
| fast   | `26-budgets`                             | Legacy /budgets redirect + tenant budget API       |
| fast   | `27-discovery-search`                    | Directory search                                   |
| fast   | `28-policies-summary`                    | Policies rollup                                    |
| fast   | `29-settings-pages`                      | Agent runtimes / skills / packages / system        |
| fast   | `30-units-explorer-search`               | Explorer renders seeded units                      |
| fast   | `31-tenant-secrets`                      | Tenant-scope secret CRUD                           |
| fast   | `32-cloning-policy`                      | Settings card + per-agent panel                    |
| fast   | `33-engagement-observe-banner`           | Read-only banner for non-participants              |
| fast   | `34-agent-persistent-error`              | Lifecycle error surfacing                          |
| fast   | `35-tenant-tree-explorer`                | Parent → child explorer                            |
| fast   | `36-command-palette`                     | Cmd+K palette                                      |
| llm    | `01-engagement-send-message`             | Composer → timeline event                          |
| llm    | `02-thread-from-unit`                    | "+ New conversation" from unit detail              |
| llm    | `03-engagement-question-cta`             | Clarification CTA contract (placeholder, see E2.6) |
| killer | `01-software-engineering-team`           | Wizard → unit → engagement                         |
| killer | `02-product-management-squad`            | Wizard → unit (PM template variant)                |

Total: **41 specs / 60 tests**.

## Tracking

Companion to [`tests/e2e/`](../e2e/README.md) which covers the same v0.1
surface from the CLI/API side. Issues for portal-side coverage gaps and
flake follow-ups belong under the `area:e2` label / E2 umbrella in
[the v0.1 plan](../../docs/plan/v0.1/areas/e2-new-ux.md).
