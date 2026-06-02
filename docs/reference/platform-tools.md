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
| `sv.memory.get_messages` | Fetch 1–100 specific messages by message_id, returning only those on timelines you participate in (others come back under `skipped`). |

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

## Category usage guidance — `sv.tools.list(<category>)`

The discovery surface groups the `sv.*` tools above into capability **categories**. `sv.tools.list_categories` returns a one-line **summary** per category; `sv.tools.list(<category>)` returns an extended **usage-guidance** string describing *when* to reach for each tool the category enumerates.

This prose is the single source of truth `PlatformToolCatalog` ([`src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs`](../../src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs)) — the same strings the runtime serves to an agent are reproduced verbatim below. A CI test (`PlatformToolsCatalogDocTests.CategoryGuidance_MatchesCatalog`) normalises whitespace and fails the build if this section and the catalog diverge, and a second test (`PlatformToolCatalogConsistencyTests`) fails the build if a category's guidance stops naming a tool the category enumerates — so neither this doc nor the agent-facing guidance can drift from the registered tool set. Edit the catalog, not the prose here; then re-run the tests to sync this block.

The blocks below are delimited by `platform-tool-catalog:<token>` HTML comments the sync test keys on — keep them intact when regenerating.

<!-- platform-tool-catalog:messaging -->
**`messaging`** — Send a one-way message to humans, agents, or units.

> Use sv.messaging.send to deliver a message to one or more humans, agents, or units; every recipient lands on a single shared thread with the caller. Use sv.messaging.multicast to deliver the same message to several recipients, each on its own independent 1-1 thread with the caller (or to a resolved scope: unit-members, siblings). Use sv.messaging.respond_to to continue an existing conversation — the platform delivers to everyone already on the thread a message_id belongs to (minus the caller). Valid recipient kinds are human, agent, and unit; connector addresses appear on inbound messages as a sender but are non-routable and are rejected synchronously with an UnroutableTarget error. Delivery is one-way (ADR-0049): each call returns a delivery acknowledgement; any response from a recipient arrives later as a separate inbound message.
<!-- /platform-tool-catalog:messaging -->

<!-- platform-tool-catalog:directory -->
**`directory`** — Look up agents, units, and humans by address, role, or expertise.

> Use sv.directory.lookup when you already know an address (for example the sender of the inbound message) and need the entry's role / expertise / status. Use sv.directory.list to enumerate members of a unit, the caller's siblings, or peers matching a role or expertise filter. To walk the unit hierarchy explicitly, use sv.directory.get_self for the calling entity, sv.directory.get_member for a single entity by uuid, sv.directory.list_members for a unit's direct members, sv.directory.get_siblings for entities sharing a parent, sv.directory.get_parents for an entity's parents, and sv.directory.get_status for an entity's advisory runtime-status snapshot. Every entry carries enough to act on (address, display name, role, expertise, advisory live status) — feed an address back into sv.messaging.send to reach the entry.
<!-- /platform-tool-catalog:directory -->

<!-- platform-tool-catalog:observability -->
**`observability`** — Emit progress and decision signals operators can see live.

> Use sv.progress.report to publish a narrative progress beat with an optional 0..1 fraction so a long-running turn is not silent until completion. Use sv.runtime.report_decision to record a structured routing / delegation decision so the choice is visible on the activity stream. The platform records these as RuntimeProgress and DecisionMade activities visible in the portal and CLI live-tail.
<!-- /platform-tool-catalog:observability -->

<!-- platform-tool-catalog:tools -->
**`tools`** — Discover capability categories and their full tool definitions.

> Call sv.tools.list_categories on startup to see what your tool surface contains beyond the fundamental core, then call sv.tools.list(category) to retrieve the full tool definitions (name + description + JSON input schema) and category-level usage guidance for any category you need to act through.
<!-- /platform-tool-catalog:tools -->

<!-- platform-tool-catalog:memory -->
**`memory`** — Private memory and shared participant-set history.

> Two surfaces on the same category. Private memory: use sv.memory.add to record agent-scoped entries recalled across all your conversations (the default) or thread-scoped notes recalled only within the current conversation (scope='thread'); sv.memory.get to read one entry by id; sv.memory.list / sv.memory.search to retrieve; sv.memory.update / sv.memory.delete to mutate. Caller-scoped — another agent's entries are not visible. Shared history: sv.memory.engagements lists the participant sets you share a timeline with (most-recent activity first); sv.memory.history_with(participants=[…]) fetches the full timeline shared with a participant set (your own address is auto-included — do not list yourself); sv.memory.search_messages free-text-searches across the timelines you participate in, optionally scoped to a single participant set; sv.memory.get_messages fetches specific messages by message_id when you already hold the ids (1–100 per call), returning only those on timelines you participate in. The agent never names a thread_id — the participant set identifies the timeline.
<!-- /platform-tool-catalog:memory -->

> The `expertise` category (dynamic per-tenant `sv.expertise.<slug>` tools) carries no static usage-guidance entry — its membership varies per tenant and the tools are being folded into directory discovery (#2989), so there is no fixed prose to pin.

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

The system-prompt platform-contract layer (rendered by [`PlatformPromptProvider`](../../src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs)) names a **fundamental-core subset** in the prompt by default — the messaging tools, the **full `sv.memory.*` surface** (durable-store CRUD *and* shared participant-set history), the directory list/lookup pair, progress reporting, and the discovery tools — and points at `sv.tools.list_categories` / `sv.tools.list(<category>)` for everything else (connector tools, observability decision-reporting, the directory-traversal expansion). The durable-store memory tools are not merely listed: the contract's durable-memory clause names them inline and *actively promotes* recall-at-turn-start / record-before-turn-end (ADR-0065 Decision 3 / audit finding F1; [ADR-0056](../decisions/0056-tool-only-side-effects.md) §8 amendment promoting memory into the in-prompt core). This doc is the union of every platform tool; the in-prompt snippet is the narrower surface delivered to the runtime.

To keep a hand-maintained list from drifting, the **exact** in-prompt tool set is deliberately not duplicated here — it is whatever `PlatformPromptProvider` renders, pinned by `PlatformPromptProviderTests.NamesEveryFundamentalCoreToolWithOneLinePurpose` (so the snippet cannot silently grow or shrink), with `AdvertisesDurableMemoryCrudToolSurface` and `ActivelyPromotesDurableMemoryUse` additionally pinning the F1 fix (durable-store tools named *and* actively promoted, not just listed). The **category-level** usage guidance the runtime serves from `sv.tools.list(<category>)` is the single source of truth `PlatformToolCatalog` ([`src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs`](../../src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs)) — the same strings reproduced in the "Category usage guidance" section above and CI-pinned to it.

## Excluded from the static catalog

- **Custom connector tools** added by third-party connector packages outside this repository. The CI sync test only enforces coverage of registries linked into the platform's solution.

## Editing this doc

1. Add or remove the tool's `ToolDefinition` row in its owning `ISkillRegistry` source file.
2. Update the corresponding table row in this document.
3. To change a category's summary or usage guidance, edit `PlatformToolCatalog` ([`src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs`](../../src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs)) — **not** the prose in the "Category usage guidance" section — then reproduce the new `Summary` + `UsageGuidance` verbatim in the matching `<!-- platform-tool-catalog:<token> -->` block.
4. Run the tests below — they fail until the doc matches the registered tool set and the catalog. (`--filter` runs zero tests on these Microsoft.Testing.Platform projects; run the whole project.)

```bash
dotnet test --project tests/unit/Cvoya.Spring.Host.Worker.Tests
```
