# Connectors

> **[Architecture Index](README.md)** | Related: [Units & Agents](units.md), [Workflows](workflows.md), [Packages](packages.md)

---

## Connector Model

Connectors bridge external systems to the unit. They provide two things: **event translation** (external events → messages) and **skills** (capabilities agents can use to act on external systems).

### Connector Categories


| Category               | Examples                        | Events                         | Actions/Skills                       |
| ---------------------- | ------------------------------- | ------------------------------ | ------------------------------------ |
| **Code**               | GitHub, GitLab, Bitbucket       | Issues, PRs, commits, reviews  | Create PR, comment, merge, read code |
| **Communication**      | Slack, Teams, Discord, Email    | Messages, threads, reactions   | Send message, create channel, reply  |
| **Documents**          | Google Docs, Notion, Confluence | Edits, comments, shares        | Create/edit doc, add comment         |
| **Design**             | Figma, Canva                    | Component changes, comments    | Read designs, modify, export         |
| **Project Management** | Linear, Jira, Asana             | Task created/updated/completed | Create task, update status, assign   |
| **Knowledge**          | Web search, arxiv, wikis        | New publications, updates      | Search, summarize, bookmark          |
| **Infrastructure**     | AWS, GCP, Kubernetes            | Alerts, deployments, metrics   | Deploy, scale, configure             |


### Connector Interface

```csharp
interface IConnector : IMessageReceiver, IActivityObservable
{
    ConnectorCapabilities GetCapabilities();
    ConnectionStatus GetStatus();
}
```

### Implementation Tiers

**Simple connectors** — Dapr bindings (YAML config, no code):

```yaml
# Cron trigger, HTTP webhook, SMTP — configured, not coded
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: github-webhook
spec:
  type: bindings.http
```

**Rich connectors** — Custom `ConnectorActor` (code):
For GitHub, Slack, Figma — bidirectional, stateful, domain-aware. Translates events, manages connections, provides skills.

### Connector Skills

A connector doesn't just pass events — it gives agents **skills**. The GitHub connector provides:

- Read issue details, PR diffs, file contents
- Create branches, commits, PRs
- Post comments, manage labels
- Manage project board items

These skills are surfaced to the agent's AI as available tools, making the agent capable in that domain.

**Skill discovery:** Connectors register their available skills with the unit when they are initialized. At agent activation time, the actor assembles the agent's tool manifest by combining: (1) platform tools, (2) tools from the agent's own tool manifest, and (3) skills from all connectors attached to the agent's unit. This means an agent automatically gains access to connector capabilities without explicit per-agent configuration.

## Connector discovery surfaces

Connector types are first-class browseable resources on every operator surface:

| Surface | Entry point | Notes |
| ------- | ----------- | ----- |
| **HTTP API** | `GET /api/v1/connectors` (catalog), `GET /api/v1/connectors/{slugOrId}` (single type), `GET /api/v1/connectors/{slug}/config-schema` (JSON Schema), `GET /api/v1/connectors/{slugOrId}/bindings` (every unit bound to a type — single round-trip, #520) | Authoritative — the CLI and portal both consume these. |
| **CLI** | `spring connector catalog` (catalog), `spring connector show --unit <name>` (typed config for a unit binding), `spring connector bindings <slugOrId>` (every unit bound to a type) | See [src/Cvoya.Spring.Cli/Commands/ConnectorCommand.cs](../../src/Cvoya.Spring.Cli/Commands/ConnectorCommand.cs). |
| **Portal** | `/connectors` (catalog), `/connectors/{slug}` (per-type detail with schema + bound units — rendered from the bulk bindings endpoint) | Sidebar entry under "Connectors". The unit detail page's Connector tab deep-links into the per-type detail page. See the [portal walkthrough](../guide/portal.md#connectors-browser-connectors). |

The catalog is hydrated from the open-source registration API: every package that calls `services.AddSpringConnector<TConnector>()` becomes visible across all three surfaces with no further wiring. Connector authors only need to implement `IConnectorType.GetConfigSchemaAsync` to get a typed configuration form on the portal automatically.
