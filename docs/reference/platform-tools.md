# Platform tools catalog

The authoritative inventory of MCP tools the Spring Voyage platform exposes. Every tool registered through an `ISkillRegistry` in the worker's DI container is listed below; a CI test (`PlatformToolsCatalogDocTests`) parses this document and fails the build if the table diverges from the registered tool set, so adding, renaming, or removing a tool without updating this doc breaks the build.

This catalog is consumer-facing: it tells an operator, package author, or new contributor *what tool exists, which namespace it lives in, what it does, when it's available, and who maintains it*. The runtime-side discovery surface (`sv.tools.list_categories` + `sv.tools.list(<category>)`) returns a structurally-identical subset filtered by the caller's effective grants — this doc is the union of every tool the platform code declares.

## Reading the columns

| Column | Meaning |
| --- | --- |
| **Tool** | The canonical, dotted-snake tool name. Validated against `ToolNaming.Pattern` at registration. |
| **Owning registry** | The `ISkillRegistry` implementation that publishes the tool. Source path is the file under `src/` you edit to change behaviour. |
| **Category** | The capability category token (`ToolCategories`) the discovery surface groups the tool under. Empty (`—`) when the tool is reachable directly by name but does not appear in category-aware discovery. |
| **Effective-grant rule** | When the tool is reachable to a given caller. *Always-on (platform)* tools are surfaced on every agent / unit; *connector-binding* tools are gated by the caller's owning-or-ancestor unit having an active binding to the relevant connector type; *unit-grant* tools require an explicit grant in the unit's `grants:` block. |
| **Stability** | `stable` — public surface; rename / removal goes through deprecation. `experimental` — may change without deprecation. |

The **Description** under each row is the one-line `ToolDefinition.Description` summary as it appears in the registry; the full input schema is in the source file.

## Platform tools — `sv.*` namespace

### `sv.directory.*` — directory & participant lookup

Owning registry: [`SvDirectorySkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvDirectorySkillRegistry.cs). Category: `directory`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.directory.get_self` | Returns metadata for the calling agent or unit. |
| `sv.directory.get_member` | Returns metadata for a single agent or unit identified by uuid. |
| `sv.directory.list_members` | Returns the direct members of a unit by uuid (mixed agent / unit / human entries). |
| `sv.directory.get_siblings` | Returns entities that share at least one parent with the entity identified by uuid. |
| `sv.directory.get_parents` | Returns the parents of the entity identified by uuid. |
| `sv.directory.get_status` | Returns the advisory runtime-status snapshot for a single agent or unit. |
| `sv.directory.list` | Resolve members / siblings / peers matching a role / expertise filter (fundamental-core). |
| `sv.directory.lookup` | Resolve a known canonical address (scheme:32-hex) to a single directory entry. |

> Expertise discovery is part of the directory surface above. `sv.directory.list` resolves peers matching an `expertise` filter and every entry carries its `expertise` list, so an agent finds "who has expertise X" through the caller-aware directory tools (#2989). The dynamic `sv.expertise.*` capability tools and the `sv.expertise.*` search meta-skill were removed in #2989. The HTTP expertise-search surface (`POST /api/v1/directory/search`) is unchanged and is not an MCP tool.

### `sv.memory.*` — agent private memory

Owning registry: [`SvMemorySkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvMemorySkillRegistry.cs). Category: `memory`. Effective-grant rule: unit-grant (`category: memory`). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.memory.add` | Store a memory entry on the calling agent's private memory store. |
| `sv.memory.get` | Read a single memory entry by id from the calling agent's store. |
| `sv.memory.list` | Enumerate the calling agent's memory entries with optional kind / tag filters and pagination. |
| `sv.memory.search` | Free-text search across the calling agent's private memory. |
| `sv.memory.update` | Replace the body / tags of an existing memory entry. |
| `sv.memory.delete` | Remove a memory entry from the calling agent's store. |

### `sv.memory.*` — shared participant-set timelines

Owning registry: [`SvMemoryHistoryRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvMemoryHistoryRegistry.cs). Category: `memory`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.memory.engagements` | List the participant sets (engagements) you share a timeline with. |
| `sv.memory.history_with` | Fetch the full message timeline you share with a named participant set. |
| `sv.memory.search_messages` | Free-text search across the timelines you participate in. |

> ADR-0060 §"Two changes…" deferred the namespace split between agent-private memory and shared timelines. The two surfaces live in `sv.memory.*` together for v0.1; they are distinguished by verb (`add` / `get` / `list` / `search` / `update` / `delete` for the private store; `engagements` / `history_with` / `search_messages` for the shared timelines).

### `sv.messaging.*` — message delivery

Owning registry: [`SvMessagingSkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs). Category: `messaging`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.messaging.send` | Send a one-way message to one or more recipients on a single shared thread. |
| `sv.messaging.multicast` | Send the same message to several recipients, each on its own independent 1-1 thread. |
| `sv.messaging.respond_to` | Continue the conversation a message belongs to — deliver to its current routable participants (minus the caller) on the same thread, addressed by `message_id` (ADR-0064). |

### `sv.progress.*` — turn-progress instrumentation

Owning registry: [`SvProgressSkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvProgressSkillRegistry.cs). Category: `observability`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.progress.report` | Publish a narrative progress beat during a long-running turn so the platform is not silent until completion. |

### `sv.runtime.*` — runtime-decision instrumentation

Owning registry: [`SvRuntimeSkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvRuntimeSkillRegistry.cs). Category: `observability`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.runtime.report_decision` | Record a structured routing / delegation decision as a `DecisionMade` activity so the choice is visible on the activity stream. |

**Intent of the `sv.runtime.*` namespace.** This namespace carries *this turn's instrumentation* — structured records the runtime emits about how it processed the inbound message, distinct from `sv.progress.*` (mid-turn narrative beats) and `sv.messaging.*` (the actual outbound message side-effect). Today the only registered tool is `sv.runtime.report_decision`; `sv.progress.report` was kept in its own namespace because narrative progress and structured decisions are different shapes and consumers (a progress UI vs the activity stream). The naming distinction is intentional, not an artefact pending consolidation — see issue [#2572](https://github.com/cvoya-com/spring-voyage/issues/2572) for the related runtime-facing routing-outcome design that this namespace will host if it lands. The platform-prompt layer does NOT enumerate `sv.runtime.*` in the always-on catalog snippet (#2748 acceptance) — agents that need it discover it through `sv.tools.list(observability)`.

### `sv.tools.*` — tool discovery

Owning registry: [`SvToolsDiscoverySkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvToolsDiscoverySkillRegistry.cs). Category: `tools`. Effective-grant rule: always-on (platform). Stability: stable.

| Tool | Description |
| --- | --- |
| `sv.tools.list_categories` | Enumerate the capability categories available to the calling agent. |
| `sv.tools.list` | Return the full tool definitions (name + description + input schema) for a named category. |

## Connector-tier tools — `github.*` / `arxiv.*` / `websearch.*` namespaces

These tools live outside the `sv.*` namespace because they are connector-emitted: the registry's tools surface only on agents whose owning-or-ancestor unit has an active binding to the relevant connector type, and the grant pipeline (#2335) gates visibility per binding. Connector tools are not platform-universal; they are listed here for completeness so the catalog covers every namespace.

### `github.*` — GitHub connector

Owning registry: [`GitHubSkillRegistry`](../../src/Cvoya.Spring.Connector.GitHub/GitHubSkillRegistry.cs). Effective-grant rule: connector-binding (`connector: github`). Stability: stable.

| Tool | Category | Description |
| --- | --- | --- |
| `github.get_installation_token` | — | Return the outbound bearer token your unit's GitHub binding is currently authenticated with. |
| `github.describe_inbound_contract` | `connector:github` | Return the GitHub-connector inbound-message envelope + intent vocabulary as a structured document. |

> The "two narrow tools, never three without re-opening the design" rule from `GitHubSkillRegistry`'s class-level remarks applies: agents bound to a GitHub unit reach the upstream API by running `gh` / `git` inside their container against the credentials and identity env-vars stamped by `GitHubConnectorRuntimeContextContributor` — not through a shape like *create_issue* or *review_pr*. Adding any third `github.*` tool re-opens the design protected by the `GitHubConnectorDoesNotRegisterMcpToolsTests` regression test.

### `arxiv.*` — Arxiv connector

Owning registry: [`ArxivSkillRegistry`](../../src/Cvoya.Spring.Connector.Arxiv/ArxivSkillRegistry.cs). Effective-grant rule: connector-binding (`connector: arxiv`). Category: `—` (not enumerated by category-aware discovery). Stability: stable.

| Tool | Description |
| --- | --- |
| `arxiv.search_literature` | Search the arxiv preprint catalogue for papers matching the supplied query, optionally scoped to arxiv categories and a publication-year window. |
| `arxiv.fetch_abstract` | Fetch the full abstract and metadata for a single arxiv entry by its canonical id (e.g. 2401.12345). |

### `websearch.*` — Web-search connector

Owning registry: [`WebSearchSkillRegistry`](../../src/Cvoya.Spring.Connector.WebSearch/WebSearchSkillRegistry.cs). Effective-grant rule: connector-binding (`connector: web-search`). Category: `—` (not enumerated by category-aware discovery). Stability: stable.

| Tool | Description |
| --- | --- |
| `websearch.search` | Run a general-purpose web search via the unit's configured provider and return the top results. |

## How the platform-contract layer relates to this doc

The system-prompt platform-contract layer (rendered by `PlatformPromptProvider`) names the **fundamental-core** subset every agent sees in the prompt by default — `sv.messaging.send`, `sv.messaging.multicast`, `sv.messaging.respond_to`, `sv.memory.history_with`, `sv.memory.engagements`, `sv.memory.search_messages`, `sv.directory.list`, `sv.directory.lookup`, `sv.progress.report`, `sv.tools.list_categories`, `sv.tools.list`. That snippet is intentionally narrower than this doc — it names the seven-or-so tools an agent reading the prompt cold needs to know about to participate, and points at `sv.tools.list_categories` / `sv.tools.list(<category>)` for everything else. This doc is the union; the snippet is the surface delivered in-prompt.

A test (`PlatformPromptProviderCatalogCoversNamedToolsTests`) pins that the catalog-snippet's tool names are a subset of the doc's full inventory so the snippet cannot drift into naming a tool the platform does not actually register.

## Excluded from the static catalog

- **Custom connector tools** added by third-party connector packages outside this repository. The CI sync test only enforces coverage of registries linked into the platform's solution.

## Editing this doc

1. Add or remove the tool's `ToolDefinition` row in its owning `ISkillRegistry` source file.
2. Update the corresponding table row in this document.
3. Run the test below — it fails until the doc matches the registered set.

```bash
dotnet test --project tests/unit/Cvoya.Spring.Dapr.Tests --filter "FullyQualifiedName~PlatformToolsCatalogDocTests"
```
