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

The left sidebar ([src/Cvoya.Spring.Web/src/components/sidebar.tsx](../../src/Cvoya.Spring.Web/src/components/sidebar.tsx)) is the top-level navigator. It exposes the following entries:

| Portal route | What it shows | Primary CLI equivalent |
|--------------|---------------|------------------------|
| `/` — **Dashboard** | Stats header, unit cards, agent cards, recent activity | (no single CLI equivalent — see individual pages) |
| `/inbox` — **Inbox** | Conversations awaiting a response from you; cross-links to each thread | `spring inbox list` |
| `/units` — **Units** | List of all units with status + delete action | `spring unit list` |
| `/activity` — **Activity** | Paginated activity feed with filters | `spring activity list` |
| `/conversations` — **Conversations** | Filtered conversation list, "Awaiting you" inbox, deep links to threads | `spring conversation list` / `spring inbox list` |
| `/connectors` — **Connectors** | Catalog of connector types and which units bind them | `spring connector catalog` / `spring connector show` |
| `/initiative` — **Initiative** | Per-agent initiative policy editor + recent initiative events | (no CLI equivalent today — parity gap) |
| `/analytics/costs` — **Analytics → Costs** | Tenant-wide spend, per-source breakdown, tenant + per-agent budget editors | `spring analytics costs`, `spring cost set-budget` |
| `/analytics/throughput` — **Analytics → Throughput** | Messages / turns / tool calls per source over 24h/7d/30d | `spring analytics throughput` |
| `/analytics/waits` — **Analytics → Wait times** | Idle / busy / waiting-for-human durations per source | `spring analytics waits` |
| `/packages` — **Packages** | Browse installed packages and their templates | `spring package list` / `spring package show` |
| `/directory` — **Directory** | Tenant-wide expertise directory — searchable domains declared by every agent and unit | `spring directory list` / `spring directory show <slug>` / `spring directory search "<query>"` |
| `/system/configuration` — **System configuration** | Cached startup configuration report — per-subsystem status (Healthy / Degraded / Failed), per-requirement rows with reason, suggestion, env-var names, and docs links | `spring system configuration` |

Detail pages (`/units/{id}`, `/agents/{id}`, `/conversations/{id}`) are reached by clicking entity cards on the dashboard, list pages, or by following deep-links from activity rows. Every detail page renders a breadcrumb trail (`Dashboard › Units › {id}` and so on) so navigation depth is always visible.

A theme toggle (light/dark) sits at the bottom of the sidebar. On mobile the sidebar collapses behind a hamburger button.

A **Settings** trigger ([src/Cvoya.Spring.Web/src/components/sidebar.tsx](../../src/Cvoya.Spring.Web/src/components/sidebar.tsx)) opens a right-aligned drawer that collects the portal's cross-cutting configuration in one place. The drawer is focus-trapped, ESC-dismissable, and keyboard-reachable from any page. Panels are added via the portal extension registry — OSS ships four:

| Panel | What it does | Primary CLI equivalent |
|-------|--------------|------------------------|
| **Tenant budget** | Read and edit the tenant-wide daily cost ceiling. | `spring cost set-budget --scope tenant --amount <n> --period daily` |
| **Tenant defaults** | Manage tenant-scoped LLM credentials (`anthropic-api-key`, `openai-api-key`, `google-api-key`). Units inherit these unless they override with a same-name unit-scoped secret. | `spring secret --scope tenant {create,rotate,delete} <name>` |
| **Account** | Show the current signed-in user and list active API tokens. Sign-out button lives here. | `spring auth token list` |
| **About** | Read-only platform metadata: version, build hash, license reference. | `spring platform info` |

Token create and revoke from inside the drawer are tracked as a separate follow-up (#557) so the "reveal once" primitive can be designed alongside the flow; use `spring auth token create <name>` / `spring auth token revoke <name>` until that lands.

The **Tenant defaults** panel is the recommended post-deploy place to set LLM provider credentials. After the first `./deploy.sh up`, open the drawer, paste the Anthropic / OpenAI / Google key into the matching row, click **Set**, and every unit in the tenant immediately inherits the default — no container restart needed. Rotating re-posts via `PUT /api/v1/tenant/secrets/{name}`; clearing calls `DELETE`. See [Managing Secrets](secrets.md) for the full three-tier model and resolution order.

Hosted deployments extend the drawer with additional panels (members / RBAC, SSO, etc.) through the same registration surface — see `src/Cvoya.Spring.Web/src/lib/extensions/README.md`.

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

Collects the unit `name` (URL-safe lowercase/digits/hyphens), `display name`, `description`, execution `tool` (claude-code, codex, gemini, dapr-agent, custom), `hosting mode` (ephemeral or persistent), unit-level `image` + `runtime` defaults (#601 B-wide — inherited by member agents; see [Execution tab](#execution-601-b-wide)), and a UI `color`.

**Provider dropdown is only shown when `tool = dapr-agent`** (#598). Claude Code, Codex, and Gemini hardcode their provider inside the tool CLI, so exposing a Provider dropdown on them would be misleading — the selection would have no runtime effect. See [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md) for the full tool × provider matrix. When the `dapr-agent` + `ollama` combination is chosen, the model picker auto-populates from the connected Ollama server's `/api/tags` response.

**The Model dropdown is always rendered for every tool that carries a known model catalog** (#641 — regression fix). Claude Code lists the Anthropic Claude models (`opus` / `sonnet` / `haiku` families), Codex lists the OpenAI chat-capable models, Gemini lists the Google Gemini models, and `dapr-agent` lists the models for the currently-selected provider. The catalog feeds the dropdown through `GET /api/v1/models/{provider}` with the tool-implied provider id (`claude-code` → `claude`, `codex` → `openai`, `gemini` → `google`) and falls back to the static list in [`src/Cvoya.Spring.Web/src/lib/ai-models.ts`](../../src/Cvoya.Spring.Web/src/lib/ai-models.ts) if the probe fails. `custom` is the only tool without a Model dropdown — custom launchers declare their own model contract, so forcing a choice from a known list would be wrong.

**Credential section (#626).** The wizard derives which LLM provider actually needs an API key from the selected tool + provider, then shows one of four shapes:

| Selection | Required provider | Status shape |
|---|---|---|
| `claude-code` | `anthropic` | full credential section (always — #626 threads Anthropic even though the Provider dropdown is hidden on Claude Code) |
| `codex` | `openai` | full credential section |
| `gemini` | `google` | full credential section |
| `dapr-agent` + provider `anthropic` / `openai` / `google` | same | full credential section |
| `dapr-agent` + `ollama` | none | reachability banner only — no inline input (Ollama is local; no API key) |
| `custom` | none | nothing rendered — custom tools have no declared credential contract |

The full credential section has three visible states:

- **Tenant default inherited** — `Anthropic credentials: inherited from tenant default` (green) with an **Override** button. Clicking Override opens the inline input so the operator can supply a new value — the existing tenant-default plaintext is NEVER shown in the browser.
- **Unit override set** — `Anthropic credentials: set on unit` (green). No Override button — the operator edits per-unit secrets via the unit's Secrets tab.
- **Not configured** — amber banner with an inline credential input, a show/hide password toggle, and a **"Save as tenant default"** checkbox. Checkbox unticked = the key is written as a unit-scoped secret (`<provider>-api-key`) after the unit is created; ticked = the key is written as a tenant default BEFORE the unit is created, so every future unit inherits it.

The Create button is disabled with a targeted message (`Set the <Provider> API key to continue.`) whenever the selected tool requires a credential and the field is empty and the probe reports nothing resolvable at tenant/unit scope.

**Security invariant.** The probe endpoint (`GET /api/v1/system/credentials/{provider}/status`) is read-only and **never returns the credential value** — only a boolean resolvable flag, the source tier (`unit` / `tenant` / `null`), and an operator-facing suggestion string. See `docs/architecture/security.md` § "Credential status endpoint" for the full argument.

**Override flow (§3 of #626).** When a tenant default already exists and the operator clicks Override then ticks "Save as tenant default," the wizard **rotates** the tenant secret (`PUT /api/v1/tenant/secrets/{name}`) instead of creating a new one — so the keys a tenant uses as defaults can be rotated directly from the wizard without detouring through the Settings drawer.

**CLI equivalent:**

```
spring unit create <name> \
  --display-name "..." \
  --description "..." \
  --tool <claude-code|codex|gemini|dapr-agent|custom> \
  --hosting <ephemeral|persistent> \
  --color "#6366f1"

# --provider and --model are only valid when --tool=dapr-agent (#598):
spring unit create <name> --tool dapr-agent \
  --provider <ollama|openai|google|anthropic|claude> \
  --model <model-id>

# #626: inline credential entry. Pair --api-key / --api-key-from-file
# with either --tool=<tool-with-fixed-provider> or --tool=dapr-agent
# + --provider=<anthropic|openai|google>. Rejected on Ollama and
# custom tools. Without --save-as-tenant-default the key is written
# as a unit-scoped secret (POST /api/v1/units/{id}/secrets); with the
# flag it is written as a tenant default first.
spring unit create <name> --tool claude-code \
  --api-key-from-file ~/.config/anthropic/api-key \
  --save-as-tenant-default
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

The page has eleven tabs:

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

### Policies

Unified policy tab ([policies-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/policies-tab.tsx)) covering all five `UnitPolicy` dimensions — **Skill**, **Model**, **Cost**, **Execution mode**, and **Initiative** — plus an **Effective policy** footer that previews the inheritance chain. Every panel has the same "allow list / block list / caps" shape: once you learn one, the others follow.

Edits route through `PUT /api/v1/units/{id}/policy`, the same surface the CLI's `spring unit policy <dim> set|clear` commands ride (PR #473). Per-dimension edits are merged — changing the Skill panel never wipes the Cost caps, and vice versa. A **Clear** button next to each panel removes just that dimension while leaving the others untouched.

| Dimension | Portal | CLI |
|-----------|--------|-----|
| Skill allow/block list | **Edit** on Skill panel | `spring unit policy skill set <unit> --allowed … --blocked …` / `spring unit policy skill clear <unit>` |
| Model allow/block list | **Edit** on Model panel | `spring unit policy model set <unit> --allowed … --blocked …` / `spring unit policy model clear <unit>` |
| Cost caps (per-invocation / per-hour / per-day USD) | **Edit** on Cost panel | `spring unit policy cost set <unit> --max-per-invocation … --max-per-hour … --max-per-day …` / `spring unit policy cost clear <unit>` |
| Execution mode (forced + allowed whitelist) | **Edit** on Execution mode panel | `spring unit policy execution-mode set <unit> --forced Auto --allowed Auto,OnDemand` / `spring unit policy execution-mode clear <unit>` |
| Initiative (max level, unit-approval flag, action allow/block list) | **Edit** on Initiative panel | `spring unit policy initiative set <unit> --max-level Proactive --require-unit-approval true --allowed … --blocked …` / `spring unit policy initiative clear <unit>` |
| Read current policy | (tab body) | `spring unit policy <dim> get <unit>` |

The Cost panel links out to `/analytics/costs` so you can compare the caps against current spend. The Effective policy block shows a single-hop chain today; parent-unit overlay is tracked under [#414](https://github.com/cvoya-com/spring-voyage/issues/414) and will extend the chain without a UI reshape.

### Orchestration

Unit orchestration configuration tab ([orchestration-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/orchestration-tab.tsx)) — #602 / #606. Surfaces the two slices that make up a unit's orchestration contract:

- **Strategy** — dropdown of the three platform-offered strategies (`ai`, `workflow`, `label-routed`) plus an **— inferred / default —** sentinel at the top of the list. Fully editable: picking a key issues `PUT /api/v1/units/{id}/orchestration` and selecting the sentinel issues `DELETE` so the resolver falls back through the precedence ladder. Writes hit the same `UnitDefinitions.Definition` JSON slot a `spring apply -f unit.yaml` manifest writes, so the two entry points stay wire-identical.
- **Effective strategy** — read-only status line summarising the resolver's current answer per [ADR-0010](../decisions/0010-manifest-orchestration-strategy-selector.md): manifest key → `UnitPolicy.LabelRouting` inference → unkeyed platform default. All three hops are observable through the portal since #606 landed the `GET /api/v1/units/{id}/orchestration` surface.
- **Label routing** — editable rules that the `label-routed` strategy consumes ([#389](https://github.com/cvoya-com/spring-voyage/issues/389)). Each rule is a `trigger label → target member path` pair; the add-rule form, inline row edits, `AddOnAssign` / `RemoveOnAssign` roundtrip inputs, and **Save** / **Clear** ride the existing `PUT /api/v1/units/{id}/policy` endpoint so the portal and CLI round-trip the same shape.

| Slice | Portal | CLI |
|-------|--------|-----|
| Inspect effective strategy | Orchestration tab → **Effective strategy** card | `spring unit orchestration get <unit>` |
| Select strategy | Orchestration tab → **Strategy** dropdown | `spring unit orchestration set <unit> --strategy {ai\|workflow\|label-routed}` |
| Clear strategy (fall back to inferred / default) | Orchestration tab → **Strategy** dropdown → **— inferred / default —** | `spring unit orchestration clear <unit>` |
| Add / edit / remove label routing rule | Orchestration tab → **Label routing** card | `spring unit policy label-routing set <unit> --label frontend=frontend-engineer` |
| Set `AddOnAssign` / `RemoveOnAssign` labels | Orchestration tab → **Label routing** inputs | `spring unit policy label-routing set <unit> --add-on-assign … --remove-on-assign …` |
| Clear label routing | Orchestration tab → **Clear** | `spring unit policy label-routing clear <unit>` |

### Expertise

The Expertise tab ([components/expertise/unit-expertise-panel.tsx](../../src/Cvoya.Spring.Web/src/components/expertise/unit-expertise-panel.tsx)) renders two cards side-by-side:

- **Own expertise** — editable list of the unit's declared capabilities. Reads/writes `/api/v1/units/{id}/expertise/own`. The list is auto-seeded from the unit YAML's `expertise:` block on first activation (#488 / PR #498); operator edits are authoritative from that point forward. Matches `spring unit expertise set`.
- **Effective (aggregated) expertise** — read-only view of the unit's recursively-composed expertise directory (#412 / PR #487). Each row shows the originating `agent://` or `unit://` address (click to open its detail page) and the depth from this unit to the origin. Matches `spring unit expertise aggregated`.

Saving the own-expertise list invalidates every aggregated view in the cache so every ancestor unit's view refetches in place.

| Action | Portal | CLI |
|--------|--------|-----|
| Show unit's own expertise | Expertise tab → **Own expertise** card | `spring unit expertise get <unit>` |
| Replace unit's own expertise | Expertise tab → edit rows + **Save** | `spring unit expertise set <unit> --domain name[:level[:description]]` |
| Show aggregated (recursive) expertise | Expertise tab → **Effective expertise** card | `spring unit expertise aggregated <unit>` |

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

**Inheritance indicator (#615).** The list merges unit-scoped entries with the tenant defaults visible to this unit. Each row carries a badge:

- **set on unit** — a unit-scoped entry with that name exists. It overrides the tenant default (if any) for this unit. Deletable from the row.
- **inherited from tenant** — no unit-scoped entry exists; the unit picks up the tenant default. The row is read-only; clear or rotate the default from the **Tenant defaults** panel in the Settings drawer, or override by creating a unit-scoped entry with the same name.

| Action | Portal | CLI |
|--------|--------|-----|
| List secrets (metadata only) | (tab body) | no CLI equivalent |
| Add a secret | form in "Add secret" card | no CLI equivalent |
| Delete a secret | trash icon | no CLI equivalent |

**CLI equivalent:** none. Secrets are portal-only or declared inside a YAML manifest applied with `spring apply -f`. **This is a CLI/UI parity gap.**

### Boundary

Unit-boundary configuration tab ([boundary-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/boundary-tab.tsx)) — the portal half of PR-PLAT-BOUND-2 (#413). Surfaces the three dimensions of a unit's outside-facing view so an operator can pick which aggregated expertise entries are hidden, rewritten, or collapsed into a unit-level capability.

The tab reads `GET /api/v1/units/{id}/boundary`, edits each dimension in place, and PUTs the full boundary on **Save boundary**. **Clear all rules** issues `DELETE` and returns the unit to the transparent default. An empty boundary renders a `Transparent` badge; any configured rule flips the badge to `Configured`.

| Dimension | Fields | CLI flag |
|-----------|--------|----------|
| **Opacities** (hide) | `domainPattern`, `originPattern` | `--opaque 'domain=…,origin=…'` |
| **Projections** (rewrite) | `domainPattern`, `originPattern`, `renameTo`, `retag`, `overrideLevel` | `--project 'domain=…,origin=…,rename=…,retag=…,level=…'` |
| **Syntheses** (collapse) | `name` (required), `domainPattern`, `originPattern`, `description`, `level` | `--synthesise 'name=…,domain=…,origin=…,description=…,level=…'` |

| Action | Portal | CLI |
|--------|--------|-----|
| Inspect boundary | (tab body) | `spring unit boundary get <unit>` |
| Save full boundary | **Save boundary** | `spring unit boundary set <unit> [--opaque …] [--project …] [--synthesise …]` or `-f boundary.yaml` |
| Clear every rule | **Clear all rules** + confirm | `spring unit boundary clear <unit>` |

The tab is **not** a per-dimension API — saving always PUTs the entire boundary (matching the CLI's "replace in full" semantics). The portal and CLI target the same endpoints, so rules authored in either surface are immediately visible in the other.

**Bulk YAML upload (#524).** Next to the per-rule editor the tab also accepts a YAML file (drop-zone + paste area), parsed client-side with a live diff against the current boundary before anything hits the server. Both the `spring unit boundary set -f` camelCase shape and the `spring apply -f` manifest snake_case shape are accepted, so a `spring unit boundary get <unit> --output json` dump or an existing unit manifest's `boundary:` block can be round-tripped through the portal. Malformed YAML surfaces an inline error with no server round-trip; applying triggers the same `PUT /api/v1/units/{id}/boundary` path the per-rule form uses.

### Execution (#601 B-wide)

Unit-level defaults for the container-runtime configuration member agents inherit: `image`, `runtime`, `tool`, `provider`, `model`. Implemented at [src/Cvoya.Spring.Web/src/app/units/[id]/execution-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/execution-tab.tsx); the backend contract landed in PR #628.

The tab reads `GET /api/v1/units/{id}/execution`, edits each field in place, and writes through `PUT /api/v1/units/{id}/execution` per field (partial update). A per-field **Clear** pill next to every input re-PUTs with the field set to `null` (the remaining fields carry through verbatim) or falls through to `DELETE` when the operator clears the last surviving field — matching PR #628's partial-update contract. A card-level **Clear all** button issues `DELETE` directly.

| Field | Input shape | CLI equivalent |
|-------|-------------|----------------|
| **Image** | Plain text input. Placeholder: `ghcr.io/... or spring-agent:latest`. Shape 1 — autocomplete from history is #622 (V2.1), registry discovery is #623 (V2.1). | `spring unit execution set <unit> --image <ref>` |
| **Runtime** | Dropdown: `docker` / `podman` (or `(leave to default)`). | `--runtime docker\|podman` |
| **Tool** | Dropdown: `claude-code` / `codex` / `gemini` / `dapr-agent` / `custom`. | `--tool <key>` |
| **Provider** | Dropdown: `anthropic` / `openai` / `google` / `ollama`. **Only shown when Tool is `dapr-agent`, or when Tool is unset** (#598 gating, matches PR #627). | `--provider <key>` |
| **Model** | Text input — promoted to a dropdown when the provider publishes a model catalog (#613). Same gating as Provider. | `--model <id>` |

Each field is independently clearable — the editor lets an operator wipe just `image` while leaving `runtime` configured. The matching CLI verb is `spring unit execution clear <unit> --field image`.

Whenever Provider is visible and selected, the tab surfaces the credential-status banner reused from the wizard's Step 1 (PR #627): emerald "configured" pill when a secret resolves at unit or tenant scope, warning "not configured" pill otherwise with a deep-link to Settings → Tenant defaults.

The **agent** detail page carries a symmetric **Execution** panel at [src/Cvoya.Spring.Web/src/app/agents/[id]/execution-panel.tsx](../../src/Cvoya.Spring.Web/src/app/agents/%5Bid%5D/execution-panel.tsx): same five fields plus the agent-exclusive **Hosting** dropdown (`ephemeral` / `persistent`). When an agent leaves a field blank and its owning unit has a default for that field, the input renders the inherited value as an italic grey placeholder (`inherited from unit: ghcr.io/...:v1`) so the operator sees the effective value without guessing; the help copy below the input repeats the value for screen readers. Clicking into the field clears the placeholder and lets the operator type their own override; leaving the field blank on save persists `null` on the agent block, and the dispatcher merges the unit default at dispatch (per PR #628). The owning unit is resolved from `agent.parentUnit` on the detail response and its execution defaults are fetched via `GET /api/v1/units/{unitId}/execution` (cached through TanStack Query).

**Save-time validation.** A save is rejected when an agent declares ephemeral hosting and no image is resolvable on either the agent or the unit. The portal surfaces the error inline with a link to whichever surface needs an image. The CLI mirrors the check at `set` time.

### Activity

Unit-scoped activity feed ([activity-tab.tsx](../../src/Cvoya.Spring.Web/src/app/units/%5Bid%5D/activity-tab.tsx)) — pulls `/api/v1/activity?source=unit:{id}&pageSize=20`.

**CLI equivalent:**

```
spring activity list --source unit:<id> --limit 20
```

### Costs

Shows the unit's running totals: total cost, input/output tokens, record count, and the period window.

**CLI equivalent:** cost figures are surfaced in the portal's dashboard and unit detail pages, but the shipped CLI has no cost subcommand today. **This is a CLI/UI parity gap.**

## Connectors browser (`/connectors`)

The connectors page ([src/Cvoya.Spring.Web/src/app/connectors/page.tsx](../../src/Cvoya.Spring.Web/src/app/connectors/page.tsx)) is the portal's mirror of `spring connector catalog`. It lists every `IConnectorType` registered with the host — one card per connector — showing the display name, slug, and short description. Cards link to a per-connector detail page at `/connectors/{slug}`.

When no connector packages are installed (i.e. the catalog is empty), the page shows a guided empty state pointing at `/packages` so operators can find the package catalog and learn how to add a connector package.

| Action | Portal | CLI |
|--------|--------|-----|
| List every registered connector type | `/connectors` | `spring connector catalog` |
| Show a single connector type's metadata, schema, and bindings | `/connectors/{slug}` | `spring connector show --unit <name>` (per-unit view of the same connector) |
| List every unit bound to a connector type | `/connectors/{slug}` (Bound units section) | `spring connector bindings <slugOrId>` |

### Connector detail (`/connectors/{slug}`)

The detail page ([src/Cvoya.Spring.Web/src/app/connectors/[type]/connector-detail-client.tsx](../../src/Cvoya.Spring.Web/src/app/connectors/%5Btype%5D/connector-detail-client.tsx)) renders four sections beneath a `<Breadcrumbs>` trail:

1. **Identity** — display name, slug, and stable `typeId` (the same id persisted with every binding).
2. **Binds to** — the URL templates a unit binding writes to (`configUrl`) and the connector's actions base URL (`actionsBaseUrl`).
3. **Configuration schema** — the JSON Schema fetched from `GET /api/v1/connectors/{slug}/config-schema`, pretty-printed. Connectors that do not advertise a schema show a hint pointing at the raw endpoint.
4. **Bound units** — every unit currently bound to this connector type. Each row links back to `/units/{id}` so you can open the unit's Connector tab. Rendered from a single round-trip to `GET /api/v1/connectors/{slugOrId}/bindings` (#520), so the list stays responsive on tenants with many units.

The Connector tab on the unit detail page also carries a **Details** deep-link back into `/connectors/{slug}` so navigation is bidirectional.

**CLI equivalent:** `spring connector show --unit <name>` shows the connector + typed config for a single unit binding. `spring connector bindings <slugOrId>` prints the full bound-units list for a given connector type, matching the portal's Bound units section (#520).

## Agents lens (`/agents`)

The Agents list ([src/Cvoya.Spring.Web/src/app/agents/page.tsx](../../src/Cvoya.Spring.Web/src/app/agents/page.tsx)) is the tenant-wide roster view — a first-class peer to `/units` and `/conversations` (#450 / PR-S1 Sub-PR C). It reads the existing `GET /api/v1/agents` roster and filters client-side; the expertise filter runs through the shared `POST /api/v1/directory/search` endpoint so results ride the same ranking the CLI's `spring directory search` does.

The filter bar carries five controls. Each maps to an existing CLI verb so the two surfaces never drift:

| Filter | Portal | CLI |
|--------|--------|-----|
| Free-text search (name / display name / role) | Search input | `spring agent list \| grep` |
| Owning unit (substring match on `parentUnit`) | Unit input | `spring unit members list <unit>` |
| Enabled status | Status dropdown (`Enabled` / `Disabled` / any) | `spring agent list` (the `enabled` column) |
| Expertise | Expertise input (free text) | `spring directory search <text>` |
| Grouping | Group-by dropdown (`Flat` / `By unit`) | — (presentation-only) |

Filter state is serialised into the URL query string (`?q=…&unit=…&status=…&expertise=…&group=…`) so any filtered view is sharable. Each card reuses the shared `<AgentCard>` primitive and carries two lens-specific quick actions in the `actions` slot:

- **Conversation** — deep-links to `/conversations?participant=agent://<name>`, mirroring `spring conversation list --participant agent://<name>`.
- **Deployment** — deep-links to the per-agent detail page's lifecycle anchor (`/agents/<name>#deployment`). For ephemeral agents the lifecycle panel surfaces the server's `400` verbatim, matching the CLI.

The empty state depends on whether any agents exist at all: when the fleet is empty, the page shows a CTA pointing at `/units`, `/directory`, and `/packages`; when filters narrow the list to zero matches, the page shows a "widen the filters" hint plus a cross-link to `/directory`.

### Out of scope (today)

Hosting-mode (`ephemeral` / `persistent`) and initiative-level filters need the API list response to grow those fields — and the CLI `spring agent list` to grow matching flags — before they can land without breaking parity. Tracked as parity follow-ups (#572 hosting mode, #573 initiative level); the lens's filter bar deliberately stays at "every filter maps to a CLI verb".

## Agent detail (`/agents/{id}`)

The agent detail page ([src/Cvoya.Spring.Web/src/app/agents/[id]/page.tsx](../../src/Cvoya.Spring.Web/src/app/agents/%5Bid%5D/page.tsx)) renders a client view configured via `<AgentDetailClient>`. It is linked from the dashboard's Agents column and from the **Analytics → Costs** page (`/analytics/costs`). Use it to review an agent's metadata, budget, expertise, clones, and recent activity.

The detail page uses the same tabbed layout as unit detail (#604 / PR `feat-604-agent-detail-tabbed-layout`). Cards are split into four groups; the active tab rides on the `?tab=` query parameter so deep links and browser back/forward preserve the view:

| Tab | What lives there |
|-----|------------------|
| **Interaction** | Conversation quick-link card · Clones panel (create form + existing-clones list) |
| **Runtime** (default) | Persistent-deployment lifecycle panel · Cost summary (totals, tokens, records) · Cost-over-time card (24h / 7d / 30d) · Cost breakdown by activity table |
| **Settings** | Agent Info (description, role, registered-at — read-only) · Daily Budget editor · Expertise panel · Execution panel |
| **Advanced** | Status JSON debug card — only rendered when `data.status` is non-null |

The bare URL (`/agents/<id>`) opens the Runtime tab; `/agents/<id>?tab=settings` and the other values deep-link into the matching group. The trigger chrome ships from `src/components/ui/tabs.tsx`, so keyboard navigation (Arrow keys + Home/End), WAI-ARIA roles (`tablist` / `tab` / `tabpanel`), and the mobile `overflow-x-auto` wrap are inherited from the shared primitive.

The page embeds an **Expertise** card ([components/expertise/agent-expertise-panel.tsx](../../src/Cvoya.Spring.Web/src/components/expertise/agent-expertise-panel.tsx)) that reads/writes `/api/v1/agents/{id}/expertise`. The domain list is auto-seeded from the agent YAML on first activation (#488 / PR #498); operator edits made in the panel become authoritative. Saving a new list also invalidates every unit's aggregated directory in the cache so ancestor unit pages pick up the change without a manual refresh.

| Action | Portal | CLI |
|--------|--------|-----|
| Show agent expertise | Agent detail → **Expertise** card | `spring agent expertise get <id>` |
| Replace agent expertise | Agent detail → edit rows + **Save** | `spring agent expertise set <id> --domain name[:level[:description]]` |

**CLI equivalents:**

```
spring agent status <id>
spring activity list --source agent:<id>
```

There is no portal flow for creating a brand-new agent today — use `spring agent create`. **This is a CLI/UI parity gap.**

### Persistent deployment panel

Right under "Agent Info" the page carries a **Persistent deployment** panel ([lifecycle-panel.tsx](../../src/Cvoya.Spring.Web/src/app/agents/%5Bid%5D/lifecycle-panel.tsx)) that mirrors the `spring agent deploy / undeploy / scale / logs` verbs 1:1. The panel is rendered for every agent so the portal stays on the same surface as the CLI; ephemeral agents simply receive a `400` from the lifecycle endpoints, which the panel surfaces as a toast.

The header badge flips between **Running** (with a health pill: `healthy` / `unhealthy` / `unknown`) and **Not deployed**. When the agent is running, a details grid shows the image, endpoint, short container id, start time, consecutive health failures, and replica count.

| Action | Portal | CLI |
|--------|--------|-----|
| Deploy a persistent agent (with optional image override) | **Deploy** button (and the image-override input) | `spring agent deploy <id> [--image <image>] [--replicas 0|1]` |
| Undeploy (tear down the container) | **Undeploy** button | `spring agent undeploy <id>` |
| Scale to 1 (ensure running) | **Scale to 1** | `spring agent scale <id> --replicas 1` |
| Scale to 0 (undeploy) | **Scale to 0** | `spring agent scale <id> --replicas 0` |
| Read the container log tail | **Show logs** (with a `tail` input; defaults to 200) | `spring agent logs <id> [--tail <n>]` |
| Refresh deployment status | refresh icon in the toolbar | `spring agent deploy <id>` is idempotent; re-reading state uses `GET /api/v1/agents/{id}/deployment` |

Deployment status is kept fresh by the same activity SSE stream that drives the rest of the portal — agent-scoped events invalidate the `agents.deployment(id)` query slice so health transitions appear without a manual refresh. Logs are a snapshot today (server-side `docker logs --tail`), consistent with the CLI; a streaming upgrade (SSE-backed) is a tracked follow-up and will reuse the existing activity-stream infrastructure rather than a second transport.

### Agent Execution panel

Directly below the Persistent deployment panel, the detail page carries an **Execution** card ([execution-panel.tsx](../../src/Cvoya.Spring.Web/src/app/agents/%5Bid%5D/execution-panel.tsx)) that surfaces the agent's own `execution:` block — `image`, `runtime`, `tool`, `provider`, `model`, plus the agent-exclusive `hosting` slot. The card reads / writes `GET|PUT|DELETE /api/v1/agents/{id}/execution` (backend PR #628) with the same per-field clear semantics as the unit Execution tab.

When the agent leaves a field blank AND the owning unit has a default for that field, the control renders the inherited value as an italic grey placeholder (`inherited from unit: ghcr.io/...:v1`). The help copy directly below the control repeats the value in plain text so screen readers can surface it. Clicking into a field clears the placeholder; typing persists the operator's override on save. Leaving the field blank on save writes `null` on the agent block — the dispatcher merges the unit default at runtime (per PR #628).

**Provider** is gated behind the effective launcher tool: visible when the resolved `tool` (agent's own value winning over the unit default) is `dapr-agent` or unset, hidden for every other launcher. **Model** now follows the same rule as the unit-creation wizard (#641) — it is rendered whenever the effective tool carries a known model catalog (`claude-code` → Claude family, `codex` → OpenAI chat models, `gemini` → Google Gemini family, `dapr-agent` → the provider's catalog), and is hidden only when the effective tool is `custom`. The credential-status banner from PR #627 reappears whenever Provider is shown and has a value, linking back to Settings → Tenant defaults on "not configured".

| Action | Portal | CLI |
|--------|--------|-----|
| Show agent execution block | Agent detail → **Execution** card | `spring agent execution get <id>` |
| Override a unit default on the agent | Edit field + **Save** | `spring agent execution set <id> --<field> <value>` |
| Clear one field (falls back to unit default) | Per-field **Clear** button | `spring agent execution clear <id> --field <name>` |
| Clear every field | **Clear all** | `spring agent execution clear <id>` |

## Directory (`/directory`)

The directory page ([src/Cvoya.Spring.Web/src/app/directory/page.tsx](../../src/Cvoya.Spring.Web/src/app/directory/page.tsx)) is the tenant-wide expertise index. It fans out per-agent `GET /api/v1/agents/{id}/expertise` and per-unit `GET /api/v1/units/{id}/expertise/own` reads, flattens them into a single list, and exposes three filters:

- **Search** — free-text match against domain, description, and owner display name / id.
- **Level** — `beginner` / `intermediate` / `advanced` / `expert`, or `Any`.
- **Owner** — `Agents`, `Units`, or `Any`.

Each row shows the domain name, its level badge (when set), the description (when set), the owner scheme badge (`agent` / `unit`), and a deep link to the owning detail page. Because the list is the union of per-entity reads, entries auto-seeded from YAML (#488 / PR #498) appear alongside operator-edited entries without any visual distinction — the API does not expose provenance once the actor's expertise state has been written, so the UI cannot either. The per-unit aggregated (recursive) view remains on the unit detail page's Expertise tab.

| Action | Portal | CLI |
|--------|--------|-----|
| Browse every declared domain across the tenant | `/directory` | `spring directory list` (parity with the portal landed in PR #555 / closes #528) |
| Open a single directory entry by slug | click the row's slug | `spring directory show <slug>` — includes the ancestor-chain breadcrumb + `projection/{slug}` paths (#553) |
| Free-text search + ranked results | search box above the list | `spring directory search "<query>"` |
| Filter by domain / level / owner | filters above the list | `spring directory list --domain <name> --owner <scheme://path>` (plus `--typed-only` / `--inside`) |
| Open owning agent / unit | click the owner link | `spring agent status <id>` / `spring unit show <id>` |

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

## Inbox (`/inbox`)

The inbox page ([src/Cvoya.Spring.Web/src/app/inbox/page.tsx](../../src/Cvoya.Spring.Web/src/app/inbox/page.tsx)) is the one-to-one portal counterpart of `spring inbox list`. It lists conversations where the latest event is a `MessageReceived` targeting the current `human://` address and the human has not yet replied — a task queue, not an archive (see `docs/design/portal-exploration.md` § 3.4).

- **Card grid** — one `<InboxCard>` per row. Each card shows the summary, an `Awaiting you` warning badge, the `from` address (cross-linked to `/agents/{id}` or `/units/{id}` when applicable — `human://` senders render as plain monospace), the `timeAgo(pendingSince)` meta, and an "Open thread" deep-link to `/conversations/{id}`.
- **No filters** — `spring inbox list` ships without filter flags today, so the portal surface exposes none either. Any future CLI filter grows the same knob on this page in the same PR (CONVENTIONS.md § 14 UI / CLI parity).
- **Empty state** — "Nothing waiting on you." when the list is empty.
- **Error state** — the page surfaces the server error verbatim in a `border-destructive` card and leaves the refresh button reachable.
- **Live updates** — the page subscribes to the activity SSE stream; `human://`-scoped events invalidate the inbox cache through `queryKeysAffectedBySource`, so new asks appear (and resolved ones disappear) without polling.

| Action | Portal | CLI |
|--------|--------|-----|
| List inbox rows | `/inbox` | `spring inbox list` |
| Open thread from a row | "Open thread" link on any card | `spring inbox show <conversation-id>` |
| Reply to a row | composer at the bottom of `/conversations/{id}` | `spring inbox respond <conversation-id> <text>` |

## Conversations (`/conversations`, `/conversations/{id}`)

The conversations surface ([src/Cvoya.Spring.Web/src/app/conversations/](../../src/Cvoya.Spring.Web/src/app/conversations/)) is the chat-shaped projection over the activity event stream. A "conversation" is the set of activity events that share a `correlationId` (which the platform sets to the message envelope's `ConversationId` — see [Messaging — Conversation Surfaces](../architecture/messaging.md#conversation-surfaces)).

### List (`/conversations`)

The list page is the one-to-one portal counterpart of `spring conversation list`:

- **Filters** — `unit`, `agent`, `participant`, and `status` (`active` / `completed`). Filter values live in the URL query string, so a link like `/conversations?unit=engineering-team&status=active` round-trips with the CLI's `--unit engineering-team --status active`.
- **"Awaiting you"** — at the top, the inbox panel renders the rows returned by `GET /api/v1/inbox` (the same data feeding `spring inbox list`). Each row deep-links to the relevant conversation.
- **Conversation grid** — one `<ConversationCard>` per row, showing the participants, status, last-activity time, and a one-click "Open" link.
- **Live updates** — the page subscribes to the activity SSE stream (`/api/stream/activity`); whenever a relevant event lands, the list is invalidated and refetched. There is no polling.

| Action | Portal | CLI |
|--------|--------|-----|
| List conversations | `/conversations` | `spring conversation list` |
| Filter by unit | `?unit=…` | `--unit …` |
| Filter by agent | `?agent=…` | `--agent …` |
| Filter by participant | `?participant=scheme://path` | `--participant scheme://path` |
| Filter by status | `?status=active|completed` | `--status active|completed` |
| Inbox (awaiting me) | "Awaiting you" panel | `spring inbox list` |

### Thread (`/conversations/{id}`)

The thread page is the one-to-one portal counterpart of `spring conversation show <id>` — plus a composer that mirrors `spring conversation send --conversation <id> <addr> <text>`.

- **Header** — conversation id, status, summary, participants, "Origin" address (a link back to `/activity?source=…` so users can pivot to the raw event log), and a "View activity" button that filters the activity surface by this conversation.
- **Thread** — one bubble per `ConversationEvent`, with role attribution by source scheme:
  - `human://` — right-aligned, primary surface (the human's voice).
  - `agent://` — left-aligned, muted surface.
  - `unit://` — left-aligned, dimmer muted surface.
  - `system://` — left-aligned, italic muted surface.
  - `DecisionMade` events render as left-aligned **tool calls** with an amber outline; they collapse by default to keep the thread readable. `StateChanged`, `WorkflowStepCompleted`, and `ReflectionCompleted` also collapse by default.
  - Each bubble carries a "View in activity →" link to deep-link back to the activity surface, the inverse of the activity row's "Open thread" pill.
- **Composer** — a textarea + recipient field at the bottom of the thread. The recipient is seeded with the most-recently-active non-human participant and can be changed via quick-pick pills. Submit on click or `⌘/Ctrl+Enter`. The composer POSTs to `/api/v1/conversations/{id}/messages` exactly like the CLI's `spring conversation send`.
- **Live updates** — the page subscribes to the activity SSE stream filtered by `correlationId`; new events appear in the thread as they land, with no manual refresh.

| Action | Portal | CLI |
|--------|--------|-----|
| Show a thread | `/conversations/{id}` | `spring conversation show <id>` |
| Send into a thread | composer at the bottom of `/conversations/{id}` | `spring conversation send --conversation <id> <addr> <text>` |
| Jump to activity event | "View in activity →" on any bubble | — |
| Jump to thread from an activity row | "Open thread" pill on rows with a correlation id | — |

## Initiative (`/initiative`)

The initiative page ([src/Cvoya.Spring.Web/src/app/initiative/page.tsx](../../src/Cvoya.Spring.Web/src/app/initiative/page.tsx)) lists every agent with its current initiative `level` and policy `maxLevel`. Clicking an agent opens an inline policy editor where you set:

- **Max level** (`Passive`, `Attentive`, `Proactive`, `Autonomous`).
- **Require unit approval** (checkbox).
- **Tier 2 rate limits** — max calls per hour, max cost per day (USD).
- **Allowed actions** / **blocked actions** (comma-separated allow/deny lists).

The bottom of the page streams recent `InitiativeTriggered` / `ReflectionCompleted` events.

**CLI equivalent:** none today. The CLI does not expose initiative policy editing. **This is a CLI/UI parity gap.**

## System configuration (`/system/configuration`)

The system-configuration page ([src/Cvoya.Spring.Web/src/app/system/configuration/page.tsx](../../src/Cvoya.Spring.Web/src/app/system/configuration/page.tsx)) renders the cached startup configuration report produced by the platform's tier-1 validator (issue #616). Operators land on it to answer "is the platform deployed correctly?" without reading container logs.

- **Overall card** — badge (Healthy / Degraded / Failed) plus the `generatedAt` timestamp. The timestamp does not move until the host restarts — the validator caches the report at boot and never re-runs.
- **Per-subsystem cards** — collapsible sections, one per subsystem (Database, GitHub Connector, Ollama, …). Cards for subsystems that aren't Healthy expand by default so degradation is visible without a click.
- **Requirement rows** — each requirement shows display name, status (Met / Disabled / Invalid), severity (Information / Warning / Error), mandatory vs optional, a plain-language description, a reason, an actionable suggestion (e.g. "run `spring github-app register`"), the env-var names to set, the `appsettings.json` section path, and a docs link.
- **Refresh** — button in the header re-fetches the endpoint. The report is still cached server-side; refresh only picks up the freshly-cached value after the host restarts.

The page rides the existing banner primitives (axe-clean per #580) — no new colour tokens. Status badges use the same `success` / `warning` / `destructive` palette the rest of the portal uses.

**CLI equivalent:**

```
spring system configuration                       # all subsystems, table view
spring system configuration --json                # raw JSON (jq-friendly)
spring system configuration "GitHub Connector"    # drill into one subsystem
```

Both surfaces read `GET /api/v1/system/configuration` — anonymous in the OSS build, the private cloud host layers auth middleware on top.

## Analytics (`/analytics`)

The Analytics surface ([src/Cvoya.Spring.Web/src/app/analytics/](../../src/Cvoya.Spring.Web/src/app/analytics/)) is the portal's operational-health lens. It has three tabs that share a window picker (24h / 7d / 30d) and a unit/agent scope filter. All three map 1:1 to `spring analytics` CLI subcommands.

### Costs (`/analytics/costs`)

Replaces the legacy `/budgets` page (old deep links 308-redirect here). Surfaces:

- **Scoped total** — when filtered to a unit or agent, shows the windowed total / work / initiative split.
- **Breakdown by source** — bars ranked by total spend, with deep links to the matching `/units/[id]` or `/agents/[id]` page. The CLI doesn't expose this breakdown today; tracked by [#554](https://github.com/cvoya-com/spring-voyage/issues/554).
- **Tenant daily budget** — USD editor with a period-to-date utilization bar.
- **Per-agent budgets** — one row per agent with a **Configure** button that deep-links to the agent's detail page.

**CLI equivalents:** `spring analytics costs --window <w> [--unit|--agent]` for the scoped total; `spring cost set-budget tenant|agent <target> --daily <usd>` for budget configuration.

### Throughput (`/analytics/throughput`)

Per-source counters over the selected window: messages received, messages sent, conversation turns, tool-call decisions. Rows are ranked by total event volume and sources are deep-linked to the matching detail page.

**CLI equivalent:** `spring analytics throughput --window <w> [--unit|--agent]`.

### Wait times (`/analytics/waits`)

Time-in-state rollups derived from paired `StateChanged` activity events: idle, busy, waiting-for-human. The bar on each row composes the three durations so "agent stuck waiting for humans" versus "agent idle" is visible at a glance. A transitions counter alongside tells quiet (no activity) apart from never-transitioned.

**CLI equivalent:** `spring analytics waits --window <w> [--unit|--agent]`.

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
| Initiative policy editor (per-agent) | `/initiative` | not implemented | |
| Unit policy editor (all five dimensions) | Policies tab on `/units/{id}` | `spring unit policy <dim> get/set/clear` | portal + CLI at parity since PR #473 / PR-R5 |
| Unit orchestration strategy selector | Orchestration tab on `/units/{id}` → **Strategy** dropdown | `spring unit orchestration get/set/clear` | portal + CLI at parity since #606 |
| Unit label-routing policy editor | Orchestration tab on `/units/{id}` → Label routing card | `spring unit policy label-routing set/clear` | portal + CLI at parity since #602 / PR #493 |
| Budget configuration | `/analytics/costs` | `spring cost set-budget` | full parity since PR #474 |
| Per-source cost breakdown | `/analytics/costs` (bars by agent/unit) | not implemented | tracked in [#554](https://github.com/cvoya-com/spring-voyage/issues/554) |
| Cost breakdown views | dashboard + unit detail | not implemented | |
| `spring apply` for YAML manifests | not implemented | `spring apply -f` | |
| Activity streaming (live follow) | polling refresh | not implemented | neither surface has a real-time `activity stream` today |
| Cost summary / budget CLI | — | not implemented | the older `docs/guide/observing.md` references `spring cost summary`/`spring cost budget`/`spring activity stream` which are not on the shipped CLI surface |
| Messaging UI (one-shot send to an arbitrary address) | not implemented | `spring message send` | use the conversation composer for in-thread replies; new-conversation send is still CLI-only |
| Tenant-wide expertise directory | `/directory` | `spring directory list` / `spring directory show` / `spring directory search` | at parity since PR #555 (closes #528, #553) — both surfaces ride `POST /api/v1/directory/search` and carry the full owner chain + `projection/{slug}` paths |

Parity is a project norm (see the top-level `AGENTS.md`): any time you find yourself building a feature on one surface, file a follow-up to bring the other in line.

## Related reading

- [Getting Started](getting-started.md) — end-to-end setup with the CLI.
- [Managing Units and Agents](units-and-agents.md) — deeper CLI reference.
- [Observing Activity](observing.md) — activity/cost patterns (note: describes target-state CLI commands, some of which are still in flight).
- [Declarative Configuration](declarative.md) — YAML authoring and `spring apply`.
- Architecture: [CLI & Web](../architecture/cli-and-web.md).
