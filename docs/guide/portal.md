# Web Portal Walkthrough

The Spring Voyage web portal (`Cvoya.Spring.Web`, a Next.js app) is a browser-based companion to the `spring` CLI. It surfaces the same resources — units, agents, activity, costs — through a point-and-click UI, and is the preferred surface for workflows that are awkward to type (configuring a GitHub App installation, editing per-membership overrides, reviewing filtered activity feeds).

> **Status note:** UX exploration is ongoing (see issue #406). The portal's layout, navigation, and terminology may evolve. Screenshots are intentionally omitted from this guide — sections below describe current behavior and flag any known areas of drift. If you run into a discrepancy, file it against #406.

This guide walks through every page, lists the equivalent `spring` CLI command for each action, and calls out known CLI/UI parity gaps.

## Launching the portal

The portal is a separate Next.js project under `src/Cvoya.Spring.Web/`. In the local development stack it is typically served alongside the API Host. When the API is available at `http://localhost:5000`, you can launch the portal from the CLI:

```
spring dashboard
```

`spring dashboard` opens the configured web URL in your default browser. If you are running the portal directly (e.g. `npm run dev` inside `src/Cvoya.Spring.Web/`), navigate to `http://localhost:3000` by hand.

Authentication uses the same token flow as the CLI: when the API Host is running in LocalDev mode, no login is required; when it is running with auth enabled, the portal redirects through the platform sign-in page (the same one `spring auth` pops open).

## Navigation and shell

The left sidebar ([src/Cvoya.Spring.Web/src/components/sidebar.tsx](../../src/Cvoya.Spring.Web/src/components/sidebar.tsx)) is the top-level navigator. It currently exposes five entries:

| Portal route | What it shows | Primary CLI equivalent |
|--------------|---------------|------------------------|
| `/` — **Dashboard** | Stats header, unit cards, agent cards, recent activity | (no single CLI equivalent — see individual pages) |
| `/units` — **Units** | List of all units with status + delete action | `spring unit list` |
| `/activity` — **Activity** | Paginated activity feed with filters | `spring activity list` |
| `/initiative` — **Initiative** | Per-agent initiative policy editor + recent initiative events | (no CLI equivalent today — parity gap) |
| `/budgets` — **Budgets** | Tenant daily budget + per-agent budget rows | (no CLI equivalent today — parity gap) |

A theme toggle (light/dark) sits at the bottom of the sidebar. On mobile the sidebar collapses behind a hamburger button.

## Dashboard (`/`)

The root page ([src/Cvoya.Spring.Web/src/app/page.tsx](../../src/Cvoya.Spring.Web/src/app/page.tsx)) is a three-column overview:

1. **Stats header** — four cards: `Units` (with counts of running / stopped / errored), `Agents`, `Total Cost`, and `System Health`. Health is `Healthy` when at least one unit exists and none are in `Error`; `Degraded` if any unit is `Error`; `No units` otherwise.
2. **Units** — one card per registered unit with a status dot, display name, address, and relative registration time. Clicking a card navigates to the unit detail page at `/units/{name}`. When no units exist, this section shows a prominent "Create your first unit" call-to-action that links to `/units/create`.
3. **Agents** — one card per agent, showing role, parent unit (if inferable from the name path), registration time, and the most recent activity summary for that agent.
4. **Recent Activity** — up to ten most-recent activity events, each with a severity dot, source badge (`agent://…` or `unit://…`), event type, and relative timestamp. "View all" links to `/activity`.

The dashboard polls `/api/v1/dashboard/summary` every ten seconds, so status changes and new events appear without a manual refresh.

**CLI equivalent:** there is no single CLI command that produces the dashboard's combined view. The closest approximation is:

```
spring unit list
spring agent list
spring activity list --limit 10
```

## Units list (`/units`)

The Units page ([src/Cvoya.Spring.Web/src/app/units/page.tsx](../../src/Cvoya.Spring.Web/src/app/units/page.tsx)) lists every unit registered in the current tenant. Each row shows the display name, address, status badge, relative "Registered" time, and a trash icon that opens a confirmation dialog before deletion. A "New unit" button in the header routes to the create wizard at `/units/create`.

| Action | Portal | CLI |
|--------|--------|-----|
| List units | `/units` | `spring unit list` |
| Open create wizard | **New unit** button | `spring unit create <name>` (no wizard — single-shot) |
| Delete a unit | trash icon + confirm | `spring unit delete <name>` |

## Create a unit (`/units/create`)

The create flow ([src/Cvoya.Spring.Web/src/app/units/create/page.tsx](../../src/Cvoya.Spring.Web/src/app/units/create/page.tsx)) is a five-step wizard. The wizard drives the same `/api/v1/units` endpoints the CLI uses; anything created here is indistinguishable from a unit created with `spring unit create`.

### Step 1 — Details

Collects the unit `name` (URL-safe lowercase/digits/hyphens), `display name`, `description`, execution `tool` (claude-code, codex, gemini, dapr-agent, custom), `hosting mode` (ephemeral or persistent), LLM `provider` + `model`, and a UI `color`. When the `dapr-agent` + `ollama` combination is chosen, the model picker auto-populates from the connected Ollama server's `/api/tags` response.

**CLI equivalent:**

```
spring unit create <name> \
  --display-name "..." \
  --description "..." \
  --tool <claude-code|codex|gemini|dapr-agent|custom> \
  --hosting <ephemeral|persistent> \
  --provider <ollama|openai|google|anthropic|claude> \
  --model <model-id> \
  --color "#6366f1"
```

### Step 2 — Mode

Pick one of three creation modes:

- **Template** — start from a packaged template (e.g. `software-engineering/engineering-team`). The server returns the template catalog from `/api/v1/packages/templates`.
- **Scratch** — create an empty unit you will configure after the fact.
- **YAML** — paste or upload a unit manifest (same grammar as `spring apply -f`).

**CLI equivalents:**

```
# Template
spring unit create --from-template <package>/<template-name> --name <override>

# Scratch
spring unit create <name>

# YAML
spring apply -f unit.yaml
```

### Step 3 — Connector

Optionally bind a connector (GitHub today) as part of the create call. The binding is atomic: if it fails, the unit is rolled back. **Skip** is always allowed, and adding a connector later from the unit's Connector tab has the same effect.

**CLI equivalent:** there is no direct CLI command to bind a connector during unit creation. Create the unit first, then bind the connector via YAML (`spring apply -f`) or through the portal. **This is a CLI/UI parity gap.**

### Step 4 — Secrets

Queue one or more unit-scoped secrets. Each can be a pass-through value or an external reference (e.g. `kv://vault/secret-id`). Secrets are applied after the unit is created; a failure on one secret surfaces as a warning and does not roll back the unit.

**CLI equivalent:** there is no CLI command for unit secrets today. Use the portal or bake secrets into a YAML manifest and `spring apply`. **This is a CLI/UI parity gap.**

### Step 5 — Finalize

Shows a summary of every field and submits.

## Unit detail (`/units/{id}`)

The unit detail page ([src/Cvoya.Spring.Web/src/app/units/[id]/unit-config-client.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/unit-config-client.tsx)) is a tabbed workspace built on the unit-configuration API. The header carries the unit's color swatch, display name, address, a live status badge, and three lifecycle buttons:

| Button | Portal behavior | CLI equivalent |
|--------|-----------------|----------------|
| **Start** | `POST /api/v1/units/{id}/start`. Disabled when the unit is already Running or Starting, or when a Draft unit is missing required configuration (see the readiness tooltip). | `spring unit start <id>` |
| **Stop** | `POST /api/v1/units/{id}/stop`. Disabled when the unit is already Stopped, Draft, or Starting. | `spring unit stop <id>` |
| **Delete** | Opens a confirmation dialog, then `DELETE /api/v1/units/{id}`. | `spring unit delete <id>` |

Transitional states (`Starting`, `Stopping`) are polled every two seconds until they settle. The status badge colours: green = Running, amber = Starting/Stopping, red = Error, outline = Stopped, default = Draft.

The page has eight tabs:

### General

Editable display name, description, model, and color. "Save" PATCHes the unit.

**CLI equivalent:** no direct CLI command today — there is no `spring unit set` in the shipped CLI. You can recreate the unit via `spring apply -f` after editing a YAML export. **This is a CLI/UI parity gap.**

### Agents

One row per membership ([agents-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/agents-tab.tsx)). Each row shows the agent's display name, disabled/specialty badges, and per-membership overrides (`Model`, `Mode`, `agentAddress`). Actions:

| Action | Portal | CLI |
|--------|--------|-----|
| Add agent to unit | **Add agent** button → dialog with agent picker + override fields | `spring unit members add <unit> --agent <agent> [--model …] [--specialty …] [--enabled …] [--execution-mode …]` |
| Edit a membership | pencil icon → same dialog pre-filled | `spring unit members config <unit> --agent <agent> [--model …] [--enabled …] …` |
| Remove a membership | trash icon + confirm | `spring unit members remove <unit> --agent <agent>` |
| List memberships (with JSON) | (tab body) | `spring unit members list <unit> --output json` |

The membership dialog lets you assign an existing agent (one the server already knows about) — you cannot create a new agent from this tab. To create a brand-new agent, use `spring agent create` (there is no portal page for agent creation today — **CLI/UI parity gap**).

### Sub-units

Lists every unit-scheme member of this unit ([sub-units-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/sub-units-tab.tsx)). Add/remove dialogs wrap the scheme-agnostic member endpoints.

| Action | Portal | CLI |
|--------|--------|-----|
| List sub-units | (tab body) | `spring unit members list <unit> --output json` (filter `scheme=unit`) |
| Add sub-unit | **Add sub-unit** button | `spring unit members add <parent> --unit <child>` |
| Remove sub-unit | trash icon + confirm | `spring unit members remove <parent> --unit <child>` |

### Skills

Grid of per-agent skill toggles ([skills-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/skills-tab.tsx)). Each checkbox fires `PUT /api/v1/agents/{id}/skills` with the agent's full skill list — optimistic update, reconciled on server response.

**CLI equivalent:** none today. **This is a CLI/UI parity gap.** You can declare skills in an agent YAML definition and reapply with `spring apply -f agent.yaml`.

### Connector

Generic connector host ([connector-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/connector-tab.tsx)) that delegates to a connector-specific component registered under a `typeSlug`. Currently the GitHub connector ships a UI:

#### GitHub connector configuration

The GitHub form ([connector-tab.tsx](../../src/Cvoya.Spring.Connector.GitHub/web/connector-tab.tsx)) collects:

- **Repository owner / repository name** — the `{owner}/{repo}` pair the unit listens to.
- **App installation** — choose from the list of GitHub App installations the platform can see. If the list is empty, the portal shows an "Install App" banner with a deep-link to the platform's install URL (`/api/v1/connectors/github/install-url`). Clicking it opens GitHub's App installation flow in a new tab.
- **Webhook events** — toggle which events (issues, pull_request, issue_comment, push, release) the unit subscribes to.

Saving POSTs `/api/v1/connectors/github/units/{unitId}/config` and registers the webhook subscription server-side.

**CLI equivalent:** none today — GitHub connector configuration is portal-only. You can embed the same fields in a unit YAML manifest's `connectors:` block and `spring apply -f` it. **This is a CLI/UI parity gap.**

### Secrets

Unit-scoped secrets tab ([secrets-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/secrets-tab.tsx)). Two modes:

- **Pass-through value** — plaintext is POSTed once and stored server-side. The portal never re-reads it. Only the secret name, scope, and creation timestamp are returned by the list endpoint.
- **External reference** — store a pointer like `kv://vault/secret-id`; the server-side `ISecretResolver` dereferences it at use time.

| Action | Portal | CLI |
|--------|--------|-----|
| List secrets (metadata only) | (tab body) | no CLI equivalent |
| Add a secret | form in "Add secret" card | no CLI equivalent |
| Delete a secret | trash icon | no CLI equivalent |

**CLI equivalent:** none. Secrets are portal-only or declared inside a YAML manifest applied with `spring apply -f`. **This is a CLI/UI parity gap.**

### Activity

Unit-scoped activity feed ([activity-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/activity-tab.tsx)) — pulls `/api/v1/activity?source=unit:{id}&pageSize=20`.

**CLI equivalent:**

```
spring activity list --source unit:<id> --limit 20
```

### Costs

Shows the unit's running totals: total cost, input/output tokens, record count, and the period window.

**CLI equivalent:** cost figures are surfaced in the portal's dashboard and unit detail pages, but the shipped CLI has no cost subcommand today. **This is a CLI/UI parity gap.**

## Agent detail (`/agents/{id}`)

The agent detail page ([src/Cvoya.Spring.Web/src/app/agents/[id]/page.tsx](../../src/Cvoya.Spring.Web/src/app/agents/%5Bid%5D/page.tsx)) renders a client view configured via `<AgentDetailClient>`. It is linked from the dashboard's Agents column and from the Budgets page. Use it to review an agent's metadata, budget, and recent activity.

**CLI equivalents:**

```
spring agent status <id>
spring activity list --source agent:<id>
```

There is no portal flow for creating a brand-new agent today — use `spring agent create`. **This is a CLI/UI parity gap.**

## Activity (`/activity`)

The activity page ([src/Cvoya.Spring.Web/src/app/activity/page.tsx](../../src/Cvoya.Spring.Web/src/app/activity/page.tsx)) is a paginated view of all activity events in the tenant. Filters:

- **Source** — free-text, e.g. `unit:my-unit` or `agent:my-agent`.
- **Event type** — dropdown across every event type the server emits (`MessageReceived`, `MessageSent`, `ConversationStarted`, `ConversationCompleted`, `DecisionMade`, `ErrorOccurred`, `StateChanged`, `InitiativeTriggered`, `ReflectionCompleted`, `WorkflowStepCompleted`, `CostIncurred`, `TokenDelta`).
- **Severity** — dropdown (`Debug`, `Info`, `Warning`, `Error`).

Each row collapses to an expandable panel that shows `id`, `correlationId`, `cost`, and the full timestamp. Pagination is page-based (20 rows per page).

**CLI equivalents:**

```
spring activity list                                   # no filters
spring activity list --source unit:<id>
spring activity list --type MessageSent
spring activity list --severity Warning
spring activity list --limit 50
```

## Initiative (`/initiative`)

The initiative page ([src/Cvoya.Spring.Web/src/app/initiative/page.tsx](../../src/Cvoya.Spring.Web/src/app/initiative/page.tsx)) lists every agent with its current initiative `level` and policy `maxLevel`. Clicking an agent opens an inline policy editor where you set:

- **Max level** (`Passive`, `Attentive`, `Proactive`, `Autonomous`).
- **Require unit approval** (checkbox).
- **Tier 2 rate limits** — max calls per hour, max cost per day (USD).
- **Allowed actions** / **blocked actions** (comma-separated allow/deny lists).

The bottom of the page streams recent `InitiativeTriggered` / `ReflectionCompleted` events.

**CLI equivalent:** none today. The CLI does not expose initiative policy editing. **This is a CLI/UI parity gap.**

## Budgets (`/budgets`)

The budgets page ([src/Cvoya.Spring.Web/src/app/budgets/page.tsx](../../src/Cvoya.Spring.Web/src/app/budgets/page.tsx)) configures spend caps:

- **Tenant daily budget** — single USD input applied across every agent and unit. A period-to-date utilization bar shows current spend against the limit.
- **Per-agent budgets** — one row per agent. Each row has a **Configure** button that deep-links to the agent's detail page to set or update the per-agent cap.

**CLI equivalent:** none today. Budgets are portal-only. **This is a CLI/UI parity gap.**

## Common workflows

### First-time setup (portal-driven)

1. Open `/` — confirm `System Health` reads `No units`.
2. Click **Create your first unit**.
3. In the wizard: name the unit, pick **Template** and select `software-engineering/engineering-team`, optionally add a GitHub connector, optionally add secrets, **Create unit**.
4. On the unit detail page, open the **Agents** tab — the template's three agents (tech-lead, backend-engineer, qa-engineer) are already members.
5. Click **Start** to bring the unit to `Running`.
6. Watch the **Activity** tab (or `/activity`) to confirm the unit comes online.

Equivalent CLI sequence:

```
spring unit create engineering-team --from-template software-engineering/engineering-team
spring unit members list engineering-team
spring unit start engineering-team
spring activity list --source unit:engineering-team
```

### Adding an agent to an existing unit

1. Navigate to `/units/{unit}` → **Agents** tab.
2. Click **Add agent**, pick an agent from the dropdown, set per-membership overrides (optional), save.

Equivalent CLI:

```
spring unit members add <unit> --agent <agent> \
  [--model <model>] [--specialty <specialty>] [--enabled true|false] \
  [--execution-mode OnDemand|Continuous]
```

### Wiring up GitHub

1. Navigate to `/units/{unit}` → **Connector** tab.
2. If no installations are listed, click **Install App** and complete GitHub's install flow.
3. Enter the repository owner/name, select the installation, pick webhook events, **Save**.

Equivalent CLI: no direct CLI surface — fall back to a YAML manifest with a `connectors:` block and `spring apply -f`.

### Viewing and filtering activity

1. Navigate to `/activity`.
2. Set filters (source, event type, severity) and paginate.
3. Click a row to expand it and see the full correlation id and cost.

Equivalent CLI:

```
spring activity list --source <unit:..|agent:..> --type <type> --severity <level> --limit 50 --output json
```

## CLI/UI parity — known gaps

Today's portal has capabilities not mirrored in the CLI, and vice versa. These are tracked as follow-up work:

| Capability | Portal | CLI | Notes |
|------------|--------|-----|-------|
| Create an agent | not implemented | `spring agent create` | portal-only gap |
| Edit unit general settings (display name, description, model, color) | **General** tab | not implemented | CLI-only gap; workaround: export + `spring apply` |
| Bind a connector during unit creation | Create wizard Step 3 | not implemented | |
| GitHub connector configuration UI | Connector tab | not implemented | use YAML |
| Unit-scoped secrets CRUD | Secrets tab | not implemented | use YAML or portal |
| Per-agent skills toggles | Skills tab | not implemented | declare in agent YAML |
| Initiative policy editor | `/initiative` | not implemented | |
| Budget configuration | `/budgets` | not implemented | |
| Cost breakdown views | dashboard + unit detail | not implemented | |
| `spring apply` for YAML manifests | not implemented | `spring apply -f` | |
| Activity streaming (live follow) | polling refresh | not implemented | neither surface has a real-time `activity stream` today |
| Cost summary / budget CLI | — | not implemented | the older `docs/guide/observing.md` references `spring cost summary`/`spring cost budget`/`spring activity stream` which are not on the shipped CLI surface |
| Messaging UI | not implemented | `spring message send` | portal is observe-only for messages |

Parity is a project norm (see the top-level `AGENTS.md`): any time you find yourself building a feature on one surface, file a follow-up to bring the other in line.

## Related reading

- [Getting Started](getting-started.md) — end-to-end setup with the CLI.
- [Managing Units and Agents](units-and-agents.md) — deeper CLI reference.
- [Observing Activity](observing.md) — activity/cost patterns (note: describes target-state CLI commands, some of which are still in flight).
- [Declarative Configuration](declarative.md) — YAML authoring and `spring apply`.
- Architecture: [CLI & Web](../architecture/cli-and-web.md).
