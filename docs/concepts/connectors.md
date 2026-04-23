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

Connectors are addressable entities -- they have an address (e.g., `connector://engineering-team/github`), can receive messages, and emit an activity stream. They are implemented as Dapr actors, just like agents and units.

When a connector is attached to a unit, it:

1. Begins listening for external events from the connected system
2. Translates those events into platform messages
3. Routes messages to the unit (which then routes to agents via its orchestration strategy)
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

The portal's create-unit wizard and the unit's Connector tab use a different surface than the CLI: rather than asking the operator to type `--owner`, `--repo`, and `--installation-id`, the GitHub connector aggregates the visible repositories across all installations the current user can see (via `GET /api/v1/connectors/github/actions/list-repositories`) and renders them as a single Repository dropdown. The chosen row carries its installation id along with `owner`/`repo`, so the operator never has to discover or paste an installation id. A Reviewer dropdown then sources collaborators for the selected repo from `GET /api/v1/connectors/github/actions/list-collaborators`. The list-repositories endpoint is **user-scoped by default**: it resolves the operator's GitHub OAuth token through the injected `IGitHubUserAccessTokenProvider` and calls `GET /user/installations` + `GET /user/installations/{installation_id}/repositories` against that token, returning the intersection of (installations the App is part of) ∩ (repositories the signed-in operator can see). If no operator identity is on the request the endpoint returns `401 Unauthorized` with a `requires_signin: true` extension so the wizard prompts the operator to sign in with GitHub before the dropdown is populated. Cloud / multi-tenant deployments override `IGitHubUserAccessTokenProvider` to resolve the OAuth token from their own session/identity store instead of the OSS default that reads `oauth_session_id` from the request. ([#1153](https://github.com/cvoya-com/spring-voyage/issues/1153))

## Label Roundtrip

When a unit is configured with a `LabelRoutingPolicy` (see the unit's policy surface) and an inbound message carries a trigger label, the `LabelRoutedOrchestrationStrategy` dispatches the message to the mapped member and publishes a `DecisionMade` activity event carrying the originating `source`, `repository`, and `issue.number` plus the policy's `AddOnAssign` and `RemoveOnAssign` lists.

The GitHub connector subscribes to that event and performs the label write back on the originating issue — applying the `AddOnAssign` labels (e.g. `in-progress`) and stripping the `RemoveOnAssign` labels (typically the trigger label itself, so a second agent does not race onto the same work). The strategy itself does not mutate external state because only the connector holds the GitHub App credentials needed to make the call.

Roundtrip semantics:

- **Idempotent.** Adding a label that is already applied is a server-side no-op; removing one that is already absent returns 404 and is swallowed. Re-delivery of an assignment event converges on the same final label set.
- **Best-effort.** Permission errors (403 / 401) and transient failures are logged and swallowed; the subscription stays live so subsequent assignments keep flowing.
- **Cross-connector.** Any future label-aware connector (Linear, Jira, ...) can subscribe to the same activity-event bus and filter on the event's `source` field — the contract is the event shape, not a direct dependency on the strategy.
