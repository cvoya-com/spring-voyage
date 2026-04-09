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

Connectors that require authentication prompt during setup:

```
spring connector auth github --unit engineering-team
```

This opens an OAuth flow or accepts a token. Once authenticated, the connector begins listening for events. Credentials are stored securely via the platform's secret management.
