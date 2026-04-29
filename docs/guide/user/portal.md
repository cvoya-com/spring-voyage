# Web Portal Walkthrough

The Spring Voyage web portal (`Cvoya.Spring.Web`, a Next.js app) is a browser-based companion to the `spring` CLI. It surfaces the same resources — units, agents, engagements, activity, costs — through a point-and-click UI, and is the preferred surface for workflows that are awkward at the command line (configuring a GitHub App installation, editing per-membership overrides, reviewing filtered activity feeds).

> **Doc-currency note.** This walkthrough reflects the current portal IA. Some routes and surface names shifted during the [#815](https://github.com/cvoya-com/spring-voyage/issues/815) redesign. If you spot drift, file it on that issue. The CLI-equivalent columns are authoritative because they reference shipped CLI verbs.

## Launching the portal

```bash
spring dashboard   # opens the configured web URL in your default browser
```

If running the portal directly (`npm run dev` inside `src/Cvoya.Spring.Web/`), navigate to `http://localhost:3000`. Authentication uses the same token flow as the CLI; in `LocalDev` mode no login is required.

## Navigation

The left sidebar lists all top-level routes:

| Portal route | What it shows | CLI equivalent |
|--------------|---------------|----------------|
| `/` — **Dashboard** | Stats header, unit cards, agent cards, recent activity | `spring unit list` + `spring activity list` |
| `/inbox` — **Inbox** | Engagements awaiting your reply | `spring inbox list` |
| `/units` — **Units** | Unit list with status and delete | `spring unit list` |
| `/activity` — **Activity** | Paginated activity feed with filters | `spring activity list` |
| `/conversations` — **Engagements** | Engagement list, "Awaiting you" inbox, thread deep-links | `spring conversation list` / `spring inbox list` |
| `/connectors` — **Connectors** | Catalog of connector types and unit bindings | `spring connector catalog` |
| `/initiative` — **Initiative** | Per-agent initiative policy editor + recent events | *(no CLI equivalent — parity gap)* |
| `/analytics/costs` — **Analytics → Costs** | Tenant-wide spend, per-source breakdown, budget editors | `spring analytics costs`, `spring cost set-budget` |
| `/analytics/throughput` — **Analytics → Throughput** | Messages / turns / tool calls per window | `spring analytics throughput` |
| `/analytics/waits` — **Analytics → Wait times** | Idle / busy / waiting-for-human durations | `spring analytics waits` |
| `/packages` — **Packages** | Installed packages and templates | `spring package list` |
| `/directory` — **Directory** | Tenant-wide expertise directory | `spring directory list` / `spring directory search` |
| `/system/configuration` — **System configuration** | Per-subsystem health report | `spring system configuration` |

Detail pages (`/units/{id}`, `/agents/{id}`, `/conversations/{id}`) are reached by clicking entity cards. A breadcrumb trail keeps navigation depth visible.

A **Settings** drawer (bottom of the sidebar) collects cross-cutting configuration:

| Panel | What it does | CLI equivalent |
|-------|--------------|----------------|
| **Tenant budget** | Read and edit the tenant-wide daily cost ceiling | `spring cost set-budget --scope tenant --amount <n> --period daily` |
| **Tenant defaults** | Set / rotate tenant-scoped LLM credentials (`anthropic-api-key`, `openai-api-key`, `google-api-key`) | `spring secret --scope tenant {create,rotate,delete} <name>` |
| **Account** | Current user and API tokens | `spring auth token list` |
| **About** | Version, build hash, license | `spring platform info` |

The **Tenant defaults** panel is the recommended first-run step after deploy: paste the Anthropic / OpenAI / Google key, click **Set**, and every unit in the tenant inherits it immediately — no restart needed. See [Managing Secrets](../operator/secrets.md) for the full resolution chain.

## Dashboard (`/`)

Three-column overview polling `/api/v1/dashboard/summary` every ten seconds:

1. **Stats header** — Units (running / stopped / errored), Agents, Total Cost, System Health.
2. **Units** — one card per unit; clicking navigates to `/units/{name}`. Empty state shows "Create your first unit".
3. **Agents** — one card per agent with role, parent unit, and most-recent activity.
4. **Recent Activity** — up to ten events with severity, source, type, and timestamp. "View all" links to `/activity`.

## Via CLI

```bash
spring unit list
spring agent list
spring activity list --limit 10
```

## Units list (`/units`)

| Action | Portal | CLI |
|--------|--------|-----|
| List units | `/units` | `spring unit list` |
| Create unit | **New unit** button → wizard | `spring unit create <name>` |
| Delete a unit | trash icon + confirm | `spring unit delete <name>` |

## Create a unit (`/units/create`)

A five-step wizard driving the same `/api/v1/units` endpoints as the CLI. Units created here are indistinguishable from those created with `spring unit create`.

### Top-level vs sub-unit

- **Top-level:** visit `/units/create` directly.
- **Sub-unit:** open the parent unit in the Explorer and click **Create sub-unit**; the wizard receives `?parent=<parent-id>` and threads it through the create call.

| Action | Portal | CLI |
|--------|--------|-----|
| Create top-level unit | `/units/create` | `spring unit create <name>` |
| Create sub-unit | **Create sub-unit** on the parent's detail pane | `spring unit create <name> --parent-unit <parent-id>` |

### Step 1 — Details

Collects name, display name, description, execution tool (claude-code, codex, gemini, dapr-agent, custom), hosting mode, image/runtime defaults, and a UI color.

- **Provider dropdown** is shown only for `dapr-agent`; other tools hardcode their provider.
- **Model dropdown** is shown for every tool with a known catalog (Claude, OpenAI, Gemini, Dapr Agent). The catalog comes from `GET /api/v1/models/{provider}`.
- **Credential section** shows the resolved status for the required LLM provider key. Three states: *Tenant default inherited* (green + Override button), *Unit override set* (green), *Not configured* (amber + inline input + "Save as tenant default" checkbox). The Create button is disabled until the credential resolves.

## Via CLI

```bash
spring unit create my-unit \
  --display-name "My Unit" \
  --tool claude-code \
  --model claude-sonnet-4-6 \
  --hosting ephemeral

# With inline credential (saves as tenant default):
spring unit create my-unit \
  --tool claude-code \
  --api-key-from-file ~/.config/anthropic/api-key \
  --save-as-tenant-default

# dapr-agent with explicit provider:
spring unit create my-unit \
  --tool dapr-agent \
  --provider anthropic \
  --model claude-sonnet-4-6
```

### Step 2 — Mode

| Mode | Portal | CLI |
|------|--------|-----|
| Template | select from catalog | `spring unit create --from-template <pkg>/<template> --name <name>` |
| Scratch | create empty unit | `spring unit create <name>` |
| YAML | paste or upload manifest | `spring apply -f unit.yaml` |

### Step 3 — Connector

Optionally bind a connector (GitHub today) atomically with unit creation. **Skip** is always available; binding later from the unit's Connector tab has the same effect.

**CLI note:** no CLI command binds a connector during unit creation. Create first, then `spring connector bind`. This is a CLI/UI parity gap.

### Step 4 — Secrets

Queue one or more unit-scoped secrets (pass-through or external reference). Applied after unit creation; a failure surfaces as a warning and does not roll back the unit.

**CLI note:** no CLI command for unit secrets today. Use the portal or bake secrets into a YAML manifest. This is a CLI/UI parity gap.

### Step 5 — Finalize

Shows a summary and submits.

## Unit detail (`/units/{id}`)

Tabbed workspace. The header carries the unit's color swatch, display name, address, live status badge, and three lifecycle buttons:

| Button | Behavior | CLI |
|--------|----------|-----|
| **Start** | `POST /api/v1/units/{id}/start` | `spring unit start <id>` |
| **Stop** | `POST /api/v1/units/{id}/stop` | `spring unit stop <id>` |
| **Delete** | confirmation dialog + `DELETE` | `spring unit delete <id>` |

The page has eleven tabs:

### General

Editable display name, description, model, and color. **CLI note:** no `spring unit set` in the shipped CLI; workaround is `spring apply -f` after editing a YAML export. Parity gap.

### Agents

| Action | Portal | CLI |
|--------|--------|-----|
| Add agent | **Add agent** → picker + overrides | `spring unit members add <unit> --agent <agent> [--model …] [--specialty …]` |
| Create + add new agent | **Add agent** → **+ New agent** sub-mode | `spring agent create <id> --name "<name>" --unit <unit>` |
| Edit membership | pencil icon | `spring unit members config <unit> --agent <agent> [--model …]` |
| Remove membership | trash icon + confirm | `spring unit members remove <unit> --agent <agent>` |

### Sub-units

| Action | Portal | CLI |
|--------|--------|-----|
| Add existing sub-unit | **Add sub-unit** | `spring unit members add <parent> --unit <child>` |
| Create + nest sub-unit | **Create sub-unit** | `spring unit create <name> --parent-unit <parent-id>` |
| Remove sub-unit | trash icon + confirm | `spring unit members remove <parent> --unit <child>` |

### Skills

Grid of per-agent skill toggles. **CLI note:** no CLI equivalent today. Declare skills in agent YAML and `spring apply`. Parity gap.

### Policies

Covers all five `UnitPolicy` dimensions — **Skill**, **Model**, **Cost**, **Execution mode**, **Initiative** — plus an **Effective policy** footer showing the inheritance chain. Edits route through `PUT /api/v1/units/{id}/policy`.

| Dimension | CLI |
|-----------|-----|
| Skill allow/block | `spring unit policy skill set <unit> --allowed … --blocked …` |
| Model allow/block | `spring unit policy model set <unit> --allowed … --blocked …` |
| Cost caps | `spring unit policy cost set <unit> --max-per-invocation … --max-per-hour … --max-per-day …` |
| Execution mode | `spring unit policy execution-mode set <unit> --forced Auto` |
| Initiative | `spring unit policy initiative set <unit> --max-level Proactive` |
| Read | `spring unit policy <dim> get <unit>` |
| Clear one dimension | `spring unit policy <dim> clear <unit>` |

### Orchestration

Surfaces the unit's orchestration strategy and label routing:

| Action | CLI |
|--------|-----|
| Inspect effective strategy | `spring unit orchestration get <unit>` |
| Select strategy | `spring unit orchestration set <unit> --strategy {ai\|workflow\|label-routed}` |
| Clear (fall back to inferred) | `spring unit orchestration clear <unit>` |
| Add / edit label routing rule | `spring unit policy label-routing set <unit> --label frontend=frontend-engineer` |
| Clear label routing | `spring unit policy label-routing clear <unit>` |

### Expertise

Two cards: **Own expertise** (editable, reads/writes `/api/v1/units/{id}/expertise/own`) and **Effective (aggregated) expertise** (recursive, read-only).

| Action | CLI |
|--------|-----|
| Show own | `spring unit expertise get <unit>` |
| Replace own | `spring unit expertise set <unit> --domain name[:level[:description]]` |
| Show aggregated | `spring unit expertise aggregated <unit>` |

### Connector

Delegates to a connector-specific component. For GitHub:

- **Repository** — dropdown of `{owner}/{repo}` pairs across all visible GitHub App installations. If empty, shows an **Install App** banner.
- **Default reviewer** — optional; agents request this login as PR reviewer unless overriding per-call.
- **Webhook events** — **Connector defaults** checkbox (issues, pull_request, issue_comment) or a custom per-event selection.

Saving POSTs `/api/v1/connectors/github/units/{unitId}/config`. **CLI note:** GitHub connector configuration is portal-only; use a unit YAML manifest's `connectors:` block and `spring apply -f` as alternative. Parity gap.

### Secrets

Lists unit-scoped and inherited tenant secrets. Each row carries a **set on unit** or **inherited from tenant** badge.

| Action | CLI |
|--------|-----|
| List (metadata only) | *(portal only)* |
| Add / delete | *(portal only)* |

**CLI note:** secrets are portal-only or declared inside a YAML manifest. Parity gap.

### Boundary

Surfaces the three boundary dimensions (opacities, projections, syntheses). Edits via `PUT /api/v1/units/{id}/boundary`; **Clear all rules** issues `DELETE`.

| Action | CLI |
|--------|-----|
| Inspect | `spring unit boundary get <unit>` |
| Save | `spring unit boundary set <unit> [--opaque …] [--project …] [--synthesise …]` |
| Clear | `spring unit boundary clear <unit>` |

Also accepts YAML upload (drop-zone) — both camelCase CLI export and snake_case manifest shapes.

### Execution

Unit-level defaults inherited by member agents: `image`, `runtime`, `tool`, `provider`, `model`. Each field is independently clearable.

| Field | CLI flag |
|-------|----------|
| Image | `--image <ref>` |
| Runtime | `--runtime docker\|podman` |
| Tool | `--tool <key>` |
| Provider | `--provider <key>` (dapr-agent only) |
| Model | `--model <id>` |

```bash
spring unit execution set <unit> --image ghcr.io/example/agent:latest --tool claude-code
spring unit execution clear <unit> --field image
```

### Activity

Unit-scoped activity feed pulling `/api/v1/activity?source=unit:{id}&pageSize=20`.

```bash
spring activity list --source unit:<id> --limit 20
```

### Costs

Running cost totals (total, input/output tokens, record count, period). **CLI note:** no cost subcommand in the shipped CLI today. Parity gap.

## Connectors browser (`/connectors`)

| Action | CLI |
|--------|-----|
| List connector types | `spring connector catalog` |
| Show a connector type | `spring connector show --unit <name>` |
| List bound units | `spring connector bindings <slugOrId>` |

Connector detail (`/connectors/{slug}`) shows identity, config URL templates, the JSON Schema, and all bound units.

## Agent creation (`/agents/create`)

Mirrors `spring agent create` field-for-field. For lightweight "create-and-add to this unit", use the **+ New agent** sub-mode inside the unit's Agents tab instead.

| Field | Required | CLI flag |
|-------|----------|----------|
| Agent id | yes | positional `<id>` |
| Display name | yes | `--name` |
| Role | no | `--role` |
| Execution tool | no | `--tool` |
| Container image | no | `--image` |
| Container runtime | no | `--runtime` |
| Model | no | *(via `--definition-file`)* |
| Initial unit assignment | yes | `--unit` (repeatable) |

```bash
spring agent create ada \
  --name "Ada Lovelace" \
  --role reviewer \
  --unit engineering \
  --image ghcr.io/example/agent:latest \
  --tool claude-code
```

## Agents lens (`/agents`)

Tenant-wide agent roster. Filter bar:

| Filter | CLI |
|--------|-----|
| Name / role search | `spring agent list \| grep` |
| Owning unit | `spring unit members list <unit>` |
| Enabled status | `spring agent list` |
| Expertise | `spring directory search <text>` |

Cards carry two quick actions: **Conversation** (deep-links to `/conversations?participant=agent://<name>`) and **Deployment** (deep-links to the agent's lifecycle anchor).

## Agent detail (`/agents/{id}`)

Four tabs:

| Tab | Contents |
|-----|----------|
| **Interaction** | Engagement quick-link · Clones panel |
| **Runtime** *(default)* | Deployment lifecycle · Cost summary and breakdown |
| **Settings** | Agent info · Daily Budget · Expertise · Execution |
| **Advanced** | Status JSON (only when `data.status` is non-null) |

### Persistent deployment panel

| Action | CLI |
|--------|-----|
| Deploy | `spring agent deploy <id> [--image <image>]` |
| Undeploy | `spring agent undeploy <id>` |
| Scale to 1 | `spring agent scale <id> --replicas 1` |
| Scale to 0 | `spring agent scale <id> --replicas 0` |
| View logs | `spring agent logs <id> [--tail <n>]` |

### Agent Execution panel

Same five fields as the unit Execution tab plus an agent-exclusive **Hosting** dropdown (`ephemeral` / `persistent`). When a field is blank and the owning unit has a default, the control renders the inherited value as an italic grey placeholder.

| Action | CLI |
|--------|-----|
| Show | `spring agent execution get <id>` |
| Override a field | `spring agent execution set <id> --<field> <value>` |
| Clear one field | `spring agent execution clear <id> --field <name>` |
| Clear all | `spring agent execution clear <id>` |

## Directory (`/directory`)

Tenant-wide expertise index. Filters: free-text search, level, owner (agent / unit).

| Action | CLI |
|--------|-----|
| Browse | `spring directory list` |
| Open by slug | `spring directory show <slug>` |
| Search | `spring directory search "<query>"` |
| Filter | `spring directory list --domain <name> --owner <scheme://path>` |

## Activity (`/activity`)

Paginated feed with source, event-type, and severity filters. 20 rows per page; each row expands to show `id`, `correlationId`, `cost`, and full timestamp.

```bash
spring activity list
spring activity list --source unit:<id>
spring activity list --type MessageSent
spring activity list --severity Warning
spring activity list --limit 50
```

## Inbox (`/inbox`)

Engagements where the latest event is a message directed at you and you have not yet replied. One card per engagement, showing summary, from-address, time pending, and an **Open thread** link.

| Action | CLI |
|--------|-----|
| List inbox | `spring inbox list` |
| Open thread | `spring inbox show <id>` |
| Reply | `spring inbox respond <id> <text>` |

## Engagements (`/conversations`, `/conversations/{id}`)

The engagement surface is the portal's view of threads. The routes currently use `/conversations` (the pre-rename surface; rename to `/threads` tracks in [#1288](https://github.com/cvoya-com/spring-voyage/issues/1288)).

### Via CLI

```bash
spring conversation list
spring conversation list --unit engineering-team --status active
```

### Engagement list (`/conversations`)

- **Filters** — unit, agent, participant, status. Filter values live in the URL query string (`?unit=…&status=active`).
- **"Awaiting you"** panel — inbox rows at the top.
- **Engagement grid** — one card per engagement with participants, status, and last-activity time.
- **Live updates** — subscribes to the activity SSE stream; no polling.

| Action | Portal | CLI |
|--------|--------|-----|
| List engagements | `/conversations` | `spring conversation list` |
| Filter by unit | `?unit=…` | `--unit …` |
| Filter by participant | `?participant=scheme://path` | `--participant scheme://path` |
| Filter by status | `?status=active\|completed` | `--status active\|completed` |

### Thread view (`/conversations/{id}`)

The thread view is the per-engagement workspace — the collaboration surface.

- **Header** — thread id, status, participants, and a "View activity" pivot.
- **Thread** — one bubble per event, role-attributed by sender scheme (`human://` right-aligned, `agent://` / `unit://` / `system://` left-aligned). `DecisionMade`, `StateChanged`, `WorkflowStepCompleted`, and `ReflectionCompleted` events collapse by default.
- **Composer** — textarea + recipient field. Submit on click or `⌘/Ctrl+Enter`. POSTs to `/api/v1/conversations/{id}/messages`.
- **Live updates** — subscribes to the SSE stream filtered by `correlationId`.

| Action | Portal | CLI |
|--------|--------|-----|
| Show a thread | `/conversations/{id}` | `spring conversation show <id>` |
| Send into a thread | composer | `spring conversation send --conversation <id> <addr> <text>` |

## Initiative (`/initiative`)

Lists every agent with its current initiative level and policy. Click an agent to edit:

- **Max level** (`Passive`, `Attentive`, `Proactive`, `Autonomous`)
- **Require unit approval** (checkbox)
- **Tier 2 rate limits** — max calls per hour, max cost per day (USD)
- **Allowed / blocked actions**

Recent `InitiativeTriggered` / `ReflectionCompleted` events stream at the bottom.

**CLI note:** no CLI equivalent for initiative policy editing. Parity gap.

## System configuration (`/system/configuration`)

Renders the cached startup configuration report. Per-subsystem cards expand when not Healthy so degradation is visible without clicking. Each requirement row shows status, severity, a plain-language reason, an actionable suggestion, env-var names, and a docs link.

```bash
spring system configuration                   # all subsystems, table view
spring system configuration --json            # raw JSON
spring system configuration "GitHub Connector"  # one subsystem
```

## Analytics (`/analytics`)

Three tabs sharing a window picker (24h / 7d / 30d) and a scope filter:

| Tab | CLI |
|-----|-----|
| **Costs** | `spring analytics costs --window <w> [--unit\|--agent]` |
| **Throughput** | `spring analytics throughput --window <w> [--unit\|--agent]` |
| **Wait times** | `spring analytics waits --window <w> [--unit\|--agent]` |

Costs also shows the tenant daily budget editor and per-agent budget links.

## Common workflows

### First-time setup

#### Via Portal

1. Open `/` — confirm System Health reads `No units`.
2. Click **Create your first unit**.
3. Wizard: name the unit, pick **Template** → `software-engineering/engineering-team`, optionally add GitHub connector and secrets, **Create unit**.
4. **Agents** tab — the template's agents are already members.
5. Click **Start**.
6. Watch **Activity** tab to confirm the unit comes online.

#### Via CLI

```bash
spring unit create engineering-team \
  --from-template software-engineering/engineering-team
spring unit members list engineering-team
spring unit start engineering-team
spring activity list --source unit:engineering-team
```

### Adding an agent to an existing unit

#### Via Portal

Navigate to `/units/{unit}` → **Agents** tab → **Add agent** → pick agent, set overrides, save.

#### Via CLI

```bash
spring unit members add <unit> --agent <agent> \
  [--model <model>] [--specialty <specialty>] [--enabled true|false]
```

### Wiring up GitHub

#### Via Portal

Navigate to `/units/{unit}` → **Connector** tab → Install App (if needed) → pick repository → set reviewer and events → **Save**.

#### Via CLI

No direct CLI surface. Use a YAML manifest with a `connectors:` block and `spring apply -f`.

### Viewing and filtering activity

#### Via Portal

Navigate to `/activity`, set filters, paginate, click a row to expand.

#### Via CLI

```bash
spring activity list --source <unit:..|agent:..> \
  --type <type> --severity <level> --limit 50 --output json
```

## CLI/UI parity — known gaps

| Capability | Portal | CLI | Notes |
|------------|--------|-----|-------|
| Edit unit general settings | **General** tab | *(none)* | workaround: export + `spring apply` |
| Bind a connector during unit creation | Wizard Step 3 | *(none)* | |
| GitHub connector configuration | Connector tab | *(none)* | use YAML |
| Unit-scoped secrets CRUD | Secrets tab | *(none)* | use YAML or portal |
| Per-agent skills toggles | Skills tab | *(none)* | declare in agent YAML |
| Initiative policy editor | `/initiative` | *(none)* | |
| Per-source cost breakdown | `/analytics/costs` (bars) | *(none)* | tracked [#554](https://github.com/cvoya-com/spring-voyage/issues/554) |
| `spring apply` for YAML manifests | *(none)* | `spring apply -f` | |
| Unit policy editor | Policies tab | `spring unit policy <dim> get/set/clear` | at parity since PR #473 |
| Orchestration strategy | Orchestration tab | `spring unit orchestration get/set/clear` | at parity since #606 |
| Label-routing policy | Orchestration tab | `spring unit policy label-routing set/clear` | at parity since #602 |
| Budget configuration | `/analytics/costs` | `spring cost set-budget` | at parity since PR #474 |
| Expertise directory | `/directory` | `spring directory list/show/search` | at parity since PR #555 |

## Related reading

- [Getting Started](../intro/getting-started.md) — end-to-end setup with the CLI.
- [Managing Units and Agents](units-and-agents.md) — CLI reference.
- [Observing Activity](observing.md) — activity and cost patterns.
- [Declarative Configuration](declarative.md) — YAML authoring and `spring apply`.
- [Managing Secrets](../operator/secrets.md) — credential tiers and rotation.
- Architecture: [CLI & Web](../../architecture/cli-and-web.md).
