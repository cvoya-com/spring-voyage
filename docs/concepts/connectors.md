# Connectors

A **connector** is a pluggable adapter that bridges an external system to a unit. Connectors make the platform domain-agnostic -- the platform itself knows nothing about GitHub, Slack, or Figma. Connectors provide that knowledge.

## What Connectors Do

Every connector provides two things:

1. **Event translation** -- external events (a new GitHub issue, a Slack message, a Figma comment) are translated into platform messages and routed to the appropriate agents.
2. **Skills** -- capabilities that agents can use to act on external systems (create a PR, send a Slack message, export a Figma design).

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

Connectors are addressable entities -- they have an address of the form `connector:<32-hex-no-dash>` (e.g., `connector:a1b2c3d4e5f6789012345678901234ab`), can receive messages, and emit an activity stream. They are implemented as Dapr actors, just like agents and units.

When a connector is attached to a unit, it:

1. Begins listening for external events from the connected system
2. Translates those events into platform messages
3. Routes messages to the unit; the unit's runtime decides whether to answer directly or delegate to a child via the orchestration tools (see [ADR-0039](../decisions/0039-units-are-agents.md))
4. Registers its skills with the unit, making them available to all agents

## Skill Discovery

Connectors register their available skills when initialized. At agent activation time, the platform assembles the agent's tool manifest by combining:

1. Platform tools (checkMessages, discoverPeers, etc.)
2. Tools from the agent's own tool manifest
3. Skills from all connectors attached to the agent's unit

This means an agent automatically gains access to connector capabilities without per-agent configuration. When a GitHub connector is attached to a unit, all agents in that unit can create PRs, read code, and manage issues.

## Implementation Tiers

### Simple Connectors

For straightforward integrations (cron triggers, HTTP webhooks, SMTP), connectors are just Dapr binding configurations -- YAML config, no code. The platform translates binding events into messages automatically.

### Rich Connectors

For bidirectional, stateful, domain-aware integrations (GitHub, Slack, Figma), connectors are custom actors with full event translation, connection management, and skill provision.

## Authentication

Connectors that require authentication expose installation / OAuth flows through their typed endpoints. The CLI surfaces binding through `spring connector bind` (for example, `spring connector bind --unit engineering-team --type github --owner my-org --repo platform --installation-id <id> --reviewer alice`) which atomically writes the connector binding plus its per-unit config. Interactive OAuth prompts are handled by the connector package that owns the auth flow; credentials are stored securely via the platform's secret management.

> **The `--reviewer` flag is optional.** Leaving it unset is a supported configuration: agents running under a unit with no default reviewer open pull requests **without** requesting a reviewer — no error, no fallback. The PR-without-reviewer end-to-end contract is documented in [Agent Runtime § 4g — PR-without-reviewer is a valid flow](../architecture/agent-runtime.md#pr-without-reviewer-is-a-valid-flow).

The portal's create-unit wizard and the unit's Connector tab use a different surface than the CLI: rather than asking the operator to type `--owner`, `--repo`, and `--installation-id`, the GitHub connector first links the operator's GitHub account through the connector-owned OAuth flow, then aggregates the visible repositories across all installations that OAuth session can see (via `GET /api/v1/tenant/connectors/github/actions/list-repositories`) and renders them as a single Repository dropdown. GitHub redirects back to the API callback; the callback page posts the issued session id back to the opener, and the portal caches it automatically for the current browser tab. The chosen repository row carries its installation id along with `owner`/`repo`, so the operator never has to discover or paste an installation id. A Reviewer dropdown then sources collaborators for the selected repo from `GET /api/v1/tenant/connectors/github/actions/list-collaborators`. Cloud / multi-tenant deployments are expected to override the underlying `IGitHubInstallationsClient` with a user-scoped implementation (for example, calling `GET /user/installations` against the operator's OAuth session) so the dropdown only ever shows installations the operator owns or has been granted access to — the OSS default uses the App-level listing for single-tenant set-ups.

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

Rules fire when the GitHub connector observes an `OrchestrationDecision` event
for the bound unit with `Kind=Delegate` and `Status=Routed`. The event shape is
defined in [ADR-0039 section 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence).
