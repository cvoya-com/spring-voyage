# Connectors

A **connector** is a pluggable adapter that bridges an external system to a unit. Connectors make the platform domain-agnostic -- the platform itself knows nothing about GitHub, Slack, or Figma. Connectors provide that knowledge.

> **Connectors are not a package-shipped artefact kind in v0.1.** The platform does not support shipping a connector *definition* inside a package. Connector *bindings* — the load-bearing feature — work through `requires: [ { connector: <slug> } ]` on a consumer artefact.

> **The platform is connector-domain-agnostic.** Connectors *facilitate flow*; they do not replicate upstream subscription configs. The platform binds units to connector types, receives events, and routes them — it does not manage the upstream system's subscription model (e.g. which GitHub repos the App is installed on, which Slack channels the bot is invited to). Per-system bookkeeping that duplicates upstream config is out of scope.

## What Connectors Do

Every connector provides two things:

1. **Event translation** -- external events (a new GitHub issue, a Slack message, a Figma comment) are translated into one-way platform messages addressed at the bound unit.
2. **Skills** -- capabilities that agents can use to act on external systems (create a PR, send a Slack message, export a Figma design).

These are a connector's only two surfaces — an inbound event translator and an outbound skill provider. A connector is **not** a routable actor: nothing sends a message *to* a connector.

## Connector Categories

| Category | Examples | Inbound Events | Outbound Skills |
|----------|----------|----------------|-----------------|
| **Code** | GitHub, GitLab, Bitbucket | Issues, PRs, commits, reviews | Create PR, comment, merge, read code |
| **Communication** | Slack, Teams, Discord, Email | Messages, threads, reactions | Send message, create channel, reply |
| **Documents** | Google Docs, Notion, Confluence | Edits, comments, shares | Create/edit doc, add comment |
| **Design** | Figma, Canva | Component changes, comments | Read designs, modify, export |
| **Project Management** | Linear, Jira, Asana | Task created/updated/completed | Create task, update status, assign |
| **Knowledge** | Web search, arxiv, wikis | New publications, updates | Search, summarize, bookmark |
| **Infrastructure** | AWS, GCP, Kubernetes | Alerts, deployments, metrics | Deploy, scale, configure |

## How Connectors Participate

A connector has a synthetic address of the form `connector:<32-hex-no-dash>` (e.g., `connector:a1b2c3d4e5f6789012345678901234ab`), but it is **not** a routable actor — connectors are non-routable bridges. The connector address appears only as the `From` of an inbound event message, identifying the external origin; nothing routes a message *to* a connector.

When a connector is bound to a unit, it:

1. Begins listening for external events from the connected system
2. Translates those events into one-way platform messages
3. Delivers each message to the unit; the unit's runtime decides whether to answer directly or delegate to a child via the `sv.messaging.*` delivery tools
4. Registers any skills it ships with the unit, making them available to its agents

## Skill discovery

A connector that ships outbound tools auto-grants that tool surface (`<connector-slug>.*`) to the unit; agents inherit it through their unit membership with no per-agent wiring. The agent's runtime then sees connector tools in its MCP tool surface alongside the platform `sv.*` tools — see [Tools](tools.md) for the three-tier grant model.

Not every connector ships tools. The built-in GitHub connector is **event-only at the platform-MCP boundary**: it registers no `github.*` tools; agents bound to a GitHub unit run `gh` / `git` directly in-container instead. See [Tools](tools.md) for details.

## Built-in connectors

v0.1 ships three connectors:

| Slug | Scope |
|------|-------|
| `github` | App / PAT auth, webhook ingest + filtering, binding lifecycle, label-roundtrip. No `github.*` MCP tools. |
| `arxiv` | Read-only literature search; no auth, no webhooks |
| `web-search` | A façade over a pluggable web-search provider; API keys resolved by secret name at invoke time |

## Authentication

Connectors that require authentication expose installation / OAuth flows through their typed endpoints. For the GitHub connector, the unit binding pins **exactly one** of two outbound auth choices:

- **App installation** — `app_installation_id` references a configured GitHub App installation on the repo. The connector mints a short-lived installation access token per launch. Best for repos the operator administers.
- **PAT secret** — `pat_secret_name` references a tenant secret holding a personal access token. The connector resolves the secret and uses the PAT as the outbound bearer. Best for public-repo flows against a repo the SV App is not installed on, or for operator-controlled credentials.

The binding-create endpoint and the CLI enforce the choice — one auth method or the other. There is no fall-through and no per-caller credential lookup: every outbound call from the unit uses the binding's pinned credential.

The CLI surfaces binding through `spring connector bind` — for example, `spring connector bind --unit engineering-team --type github --repo octocat/hello-world --installation-id <id> --reviewer alice`, or, on the PAT branch, `spring connector bind --unit engineering-team --type github --repo octocat/hello-world --pat-secret-name <name> --reviewer alice`. The verb writes the connector binding plus its per-unit config atomically. The `--repo` flag accepts the qualified `owner/repo` form only — there is no separate `--owner` flag. Interactive OAuth prompts are handled by the connector package that owns the auth flow; tokens written through the OAuth round-trip land in the tenant secret store under a binding-scoped name and the wizard pre-fills `--pat-secret-name` with that name.

> **The `--reviewer` flag is optional.** Leaving it unset is a supported configuration: agents running under a unit with no default reviewer open pull requests **without** requesting a reviewer — no error, no fallback.

The portal's create-unit wizard and the unit's Connector tab use a different surface than the CLI. The GitHub step starts with an **auth-choice sub-step**: pick "Use an App installation" or "Use a PAT secret." On the App branch, the connector first links the operator's GitHub account through the connector-owned OAuth flow, then aggregates the visible repositories across all installations that OAuth session can see (via `GET /api/v1/tenant/connectors/github/actions/list-repositories`) and renders them as a single Repository dropdown; the chosen row carries its installation id along with `owner/repo`, so the operator never has to discover or paste one. On the PAT branch the wizard either runs the OAuth flow to mint a PAT-equivalent token (persisted as a tenant secret with the secret name returned to the wizard) or accepts a pasted PAT — either way the wizard ends up with a `pat_secret_name` it submits with the binding-create call. A Reviewer dropdown sources collaborators for the selected repo from `GET /api/v1/tenant/connectors/github/actions/list-collaborators`. Cloud / multi-tenant deployments are expected to scope the installation listing to a user-scoped view (for example, calling `GET /user/installations` against the operator's OAuth session) so the dropdown only ever shows installations the operator owns or has been granted access to — the OSS default uses the App-level listing for single-tenant set-ups.

> **Display-side connector handles** (the operator's GitHub login, Slack handle) are managed separately, on the per-user **User Identity** surface, and live on the calling `TenantUser` — not on the unit binding. See [Humans § Human → TenantUser display mapping](humans.md#human--tenantuser-display-mapping) for details.

## GitHub Label Routing

GitHub label roundtrip rules are configured on each GitHub unit binding.
`AddOnAssign` lists labels to add and `RemoveOnAssign` lists labels to remove
after the unit routes work to a child. The rules live on the connector binding;
the platform itself does not model label-routing policy.

Configure the lists with:

```bash
spring connector github label-rules set <binding-id> --add-on-assign triage --remove-on-assign needs-assignment
```

Both flags are repeatable. The command replaces the stored lists; passing no
label flags clears both lists while preserving the rest of the binding config.

Rules fire when the GitHub connector observes a routing-decision activity
event for the bound unit on the activity stream — a runtime records that
decision with an optional `sv.runtime.report_decision` call.
