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

### Built-in connectors

The open-source host ships three connector types out of the box:

| Slug | Project | Scope |
| ---- | ------- | ----- |
| `github` | `src/Cvoya.Spring.Connector.GitHub/` | Rich: OAuth / App auth, webhooks, issue + PR CRUD, rate limiting, response cache. |
| `arxiv` | `src/Cvoya.Spring.Connector.Arxiv/` | Read-only: `searchLiterature` and `fetchAbstract` skills; no auth, no webhooks. |
| `web-search` | `src/Cvoya.Spring.Connector.WebSearch/` | Generic façade over a pluggable `IWebSearchProvider`. Default provider is Brave Search; Bing, Google Custom Search, or SearxNG can be slotted in by registering an additional `IWebSearchProvider` before the host resolves the DI graph. API keys are referenced by unit-scoped secret name and resolved through `ISecretResolver` at skill-invoke time — plaintext never lands in the stored binding. |

## Disabled-with-reason pattern

A connector's credentials are environmental configuration (env vars bound
to an `Options` class) — they aren't available at compile time, and they
aren't first-class platform secrets. The platform still has to decide what
to do when they are missing or malformed at startup. The GitHub connector
establishes the pattern other connectors should follow; a platform-wide
validation framework is tracked as #616.

### Three outcomes at connector-init time

| State | Example | What happens |
| ----- | ------- | ------------ |
| **Valid** | `GitHub__AppId` + a parseable PEM in `GitHub__PrivateKeyPem`. | The hot path runs normally. If the PEM value is a readable filesystem path whose contents parse as PEM, the connector adopts the file contents (ergonomic for Docker secret mounts). |
| **Missing** | Both `GitHub__AppId` and `GitHub__PrivateKeyPem` unset. | The `GitHubAppConfigurationRequirement` reports `Disabled` (status) with a human-readable reason. Connector-scoped endpoints (`list-installations`, `install-url`) consult the requirement and short-circuit to a structured `404` with `{ "disabled": true, "reason": "…" }` that the portal and CLI render cleanly. The platform still boots; every other surface is unaffected. |
| **Malformed** | Garbage in `GitHub__PrivateKeyPem`, or a filesystem path that does not resolve to a readable PEM file. | The `GitHubAppConfigurationRequirement` reports `Invalid` with a `FatalError`. The platform's startup configuration validator (#616) throws the fatal error from `StartAsync` so the host fails fast at boot, not on the first `list-installations` call. |

The classifier lives in
`Cvoya.Spring.Connector.GitHub.Auth.GitHubAppCredentialsValidator`. It is
consulted by `GitHubAppConfigurationRequirement` — the tier-1
`IConfigurationRequirement` implementation introduced in #616 — and is the
same seam the startup validator enumerates to build the
`/system/configuration` report (see [Configuration](configuration.md)). The
requirement is `TryAdd*`-registered so the private cloud repo can
substitute tenant-scoped implementations (e.g. "App installed for tenant
X, missing for tenant Y") without forking. The pre-#616
`IGitHubConnectorAvailability` interface has been removed; the GitHub
connector now uses the cross-subsystem framework, so there is one seam,
not two.

### Why structured disabled bodies, not 502s

The original bug (#609) let a missing PEM surface as a `502 Bad Gateway`
on the first `list-installations` call, carrying the raw
`System.Security.Cryptography` exception message. That is hostile to both
the portal (which can't render a configuration banner off a generic 502)
and operators (who get no guidance on which env var to fix). Returning a
`404` with an explicit `{ "disabled": true, "reason": "…" }` shape keeps
the portal simple — it already knows how to render "install the app" —
and keeps the CLI deterministic.

Connectors adopting this pattern should:

1. **Validate at DI-registration time.** Throw on malformed credentials,
   register a disabled-with-reason singleton on missing credentials, and
   normalise the options (e.g. path → contents) on the happy path.
2. **Gate connector-scoped actions** on the availability marker so hot-
   path calls short-circuit with a structured `404` when the connector
   is disabled. Unit-bound actions fail per-unit when the unit is
   configured against a disabled connector.
3. **Keep the structured body stable.** The portal and CLI consume
   `disabled` (boolean) and `reason` (string); other fields are
   advisory.

The generic framework in #616 will fold this behaviour into a
`IConnectorTypeAvailability` the platform can resolve without connector-
specific plumbing; until that lands, each connector carries its own typed
marker.

## Tier-1 credential bootstrap (GitHub)

The GitHub connector's tier-1 credentials (`GitHub__AppId`,
`GitHub__PrivateKeyPem`, `GitHub__WebhookSecret`, and the OAuth client
id/secret) can be bootstrapped via the `spring github-app register` CLI
verb (#631). The verb drives GitHub's [App-from-manifest
flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest):
it opens a pre-filled "create GitHub App" page, receives the one-time
conversion code on a loopback listener, exchanges it via
`POST /app-manifests/{code}/conversions`, and writes the resolved
credentials to either `deployment/spring.env` (default) or platform-scoped
secrets (via `spring secret --scope platform create`, #612). The
permission set embedded in the manifest (read issues/PRs/contents/metadata,
write issue_comment/statuses/checks; webhook events
issues/pull_request/issue_comment/installation) is locked to what the
shipped skill bundles actually use — adding a new scope requires updating
the manifest builder in `Cvoya.Spring.Cli/GitHubApp/GitHubAppManifest.cs`
alongside the connector.

See
[`docs/architecture/cli-and-web.md § GitHub App bootstrap verb (#631)`](cli-and-web.md#github-app-bootstrap-verb-631)
for the verb's flag list and out-of-scope boundary. The connector's
disabled-with-reason classifier still fires when credentials are missing
— the verb just makes "missing" a single-step problem to fix.
