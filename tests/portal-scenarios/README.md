# Portal scenarios — narrative Playwright tests

Complement to [`tests/e2e-portal/`](../e2e-portal/). Both suites use
Playwright; the difference is style, not framework:

- **`tests/e2e-portal/`** — full Playwright Test harness with fixtures,
  helpers, runtime/cleanup tracker, and three project pools
  (`fast`/`llm`/`killer`). Optimised for systematic coverage of every
  CRUD + IA path.
- **`tests/portal-scenarios/`** (this folder) — one user journey per
  spec, written inline. No shared fixtures, no auto-cleanup tracker,
  minimal config. Optimised for "operator did X, the bug was Y" — the
  kind of scenario a contributor (or a coding agent) reproduces by
  hand and wants to lock in as a regression.

Pick the suite that matches the test you are writing. They run against
the same live stack, so a scenario that grows a helper-ful skeleton can
graduate to `e2e-portal/`.

## Prerequisites

- A running stack reachable at `http://localhost` (single-host
  docker-compose default) or at `PLAYWRIGHT_BASE_URL`. The stack must
  include the API host AND the portal.
- For LLM-backed scenarios: a reachable Ollama with at least one model
  pulled. The default scenarios pin `tool=dapr-agent` +
  `provider=ollama` so they need no operator-supplied secrets.
- Node ≥ 20 and npm.
- `npm install` inside this directory (it is **not** a workspace
  member — it ships its own Playwright dep).

```bash
cd tests/portal-scenarios
npm install
npm run install:browsers   # one-time
PLAYWRIGHT_BASE_URL=http://localhost npm test
```

## Layout

```
tests/portal-scenarios/
├── playwright.config.ts             # one project, no fixtures
├── package.json
├── tsconfig.json
└── scenarios/
    └── unit-create-wizard-scratch.spec.ts
```

## Conventions for new scenarios

- One scenario per file. The filename is the user-visible journey
  (`unit-create-wizard-scratch`, not `wizard-success`).
- Inline the steps. If you reach for a shared helper, that's the
  signal to graduate the test to `e2e-portal/` instead.
- Clean up via the public REST surface (`DELETE /api/v1/tenant/units/{name}`)
  at the end of each test, even on failure. No global teardown.
- Use slugs prefixed with `scn-` so a sweep can find orphans.

## CI

These scenarios are heavier than unit tests (require a live stack) but
lighter than the full `e2e-portal/` suite. The intent is to run them on
a daily / few-times-a-day cadence rather than per-PR. Tracked in the
v0.1 issue that scopes scenario expansion + CI wiring.
