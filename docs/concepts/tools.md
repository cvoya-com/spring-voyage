# Tools

A **tool** is the runtime-invocation surface an agent calls to interact with the world. When an agent decides to "create a GitHub issue", "search the expertise directory", or "post a Slack message", what it actually invokes is a tool. Tools are the concrete, named, schema-typed actions; they sit one layer below the agent's reasoning loop and one layer above the external system or platform primitive that ultimately runs.

Tools are deliberately distinct from **skills** ([Packages — Skills inside the recursive layout](packages.md#skills-inside-the-recursive-layout)). A skill is an authored capability bundle — a prompt fragment plus optional tool definitions — that a package grants to an agent. A tool is the concrete invocable thing the agent reaches for at runtime. One skill can ship many tools; a tool can also reach an agent without a skill, via the platform itself or via a connector binding.

## Canonical naming

Every tool id is `<namespace>.<tool_name>` — lowercase, dot-separated, snake_case segments, with the first segment carrying the namespace and the suffix identifying the tool. The pattern is enforced at registration: a registry that produces a non-canonical id fails loudly rather than silently shipping a non-conforming surface.

```
^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$
```

The canonical names supersede the earlier ad-hoc shapes that mixed slash and underscore conventions. Operators upgrading from earlier names should expect:

| Earlier name | Canonical name |
|---|---|
| `directory/search` | `sv.expertise.search` |
| `expertise/{slug}` | `sv.expertise.{slug}` |
| `arxiv_search`, `arxiv_fetch_abstract` | `arxiv.search`, `arxiv.fetch_abstract` |
| `websearch_query`, `websearch_summarize` | `websearch.query`, `websearch.summarize` |

The first dotted segment is the namespace. `sv.*` belongs to the platform; connector-provided namespaces match the connector's slug (`arxiv.*`, `websearch.*`); container-image-provided namespaces are chosen by the agent author.

Every platform MCP tool follows the finer-grained `sv.<area>.<verb>` taxonomy — `sv.` plus an area (`directory`, `memory`, `messaging`, `runtime`, `expertise`) plus a verb. See [Platform MCP Tools](../architecture/platform-mcp-tools.md) for the full catalogue.

> **No `github.*` platform tools.** The GitHub connector ships only the
> binding lifecycle, webhook ingestion, App-auth, and per-launch runtime-context
> contribution (#2380); it does **not** register an `ISkillRegistry`. Agents
> bound to a unit with a GitHub binding receive the
> `SPRING_CONNECTOR_GITHUB_*` env vars + the
> `connectors/github/binding.json` context file inside their container and
> use the in-container `gh` and `git` CLIs for all GitHub workloads. Issues
> [#2384](https://github.com/cvoya-com/spring-voyage/issues/2384) and
> [#2383](https://github.com/cvoya-com/spring-voyage/issues/2383) record the
> v0.1 decision.

## The three tiers

A subject (agent or unit) sees an **effective tool set** assembled from three tiers. The same flat list reaches the agent's runtime regardless of which tier any single tool came from; the tier only matters for how the tool was granted and where the operator inspects it.

| Tier | Namespace shape | Where it comes from | Granularity | Inheritance |
|---|---|---|---|---|
| **Platform** | `sv.*` | The runtime itself — every `sv.*` tool registered with the platform's skill registries is implicitly granted to every unit and every agent. No database row is involved. | All-or-nothing per-namespace (the whole `sv.*` namespace is on for every subject). | N/A — implicit everywhere. |
| **Connector** | `<connector-slug>.*` | Auto-granted when an operator binds a connector to a unit. The connector ships its tool surface through its skill registry; the platform writes one grant per `<ToolNamespace>.*` row under `provenance = "connector:<slug>"`. Unbinding the connector revokes the grants. | Namespace-level: granting `github` grants every tool the connector exposes. | Agents inherit their unit's connector grants. |
| **Image** | Chosen by the agent author (e.g. `acme.*`) | The agent's container image declares its tool surface at `GET /a2a/tools`. The platform-side introspector calls that endpoint at deploy and on image rotation, then caches the result onto the subject's `image_tools` column. | One row per declared tool — image-tier tools are 1:1 with the agent or unit running the image; there is nothing to grant or revoke. | None — image tools belong to the subject that runs the image. |

In addition to these tiers, an operator may write an **explicit** grant row directly against a subject. Explicit grants take precedence when the same tool surfaces in more than one tier; the resolver collapses duplicates and reports each tool once with its highest-precedence provenance (`explicit > connector > platform > image`).

## Inspecting an effective tool set

The portal's **Config → Tools** sub-tab on a unit or agent renders the resolved effective tool set with three sections, one per tier:

- **Platform** — a collapsed listing of every `sv.*` tool. Read-only; the section exists so the operator can confirm the platform surface is present and see what an `sv.*` tool actually does.
- **Connectors** — one group per `connector:<slug>` provenance. Each group surfaces an inherited-from-unit badge when the binding lives on a parent unit, with a direct link back to that unit's Tools sub-tab so the operator can edit the binding in one click.
- **Image** — read-only listing of the tools the running container declared. Empty when the image declares no custom tools.

The same data is available over HTTP on every `AgentResponse` / `UnitResponse` via the `effectiveTools` array — one row per tool with its name, namespace, description, provenance, and (when inherited) the unit it was inherited from. Programmatic consumers that need the merged surface read that array directly rather than walking the three tiers themselves.

## Where each tier comes from

**Platform tools** are part of the runtime. The platform's skill registries (the expertise-directory registry, the directory / memory / runtime registries, and the `sv.messaging.*` delivery tools) declare the `sv.<area>.<verb>` tools they own, and the resolver surfaces them on every subject without writing a row anywhere. There is no operator action that grants or revokes a platform tool; the only way to drop one is to ship a runtime that does not register it.

**Connector tools** ship with the connector package itself. A connector declares a `ToolNamespace` (defaulting to its slug) and registers its tool surface through the same `ISkillRegistry` seam every other registry uses. When an operator binds the connector to a unit, the binding write path auto-grants every `<ToolNamespace>.*` tool to that unit; unbinding revokes them; re-binding swaps cleanly. Authoring details live in the [connector developer guide](../guide/developer/connectors.md).

**Image tools** belong to the agent's container image. An agent built on the SDK uses `IToolRegistry.Register` to declare its tools in-process and `app.MapToolsEndpoint(registry)` to expose them at `GET /a2a/tools` on the same listener that already serves A2A. CLI-wrapped agents that go through the sidecar bake the tool manifest into the image at the path named by `SPRING_TOOLS_MANIFEST`. Either way, the platform's introspector calls `/a2a/tools` at deploy time and on image rotation; the result lands on the `image_tools` column of the subject's definition and feeds the resolver. The [agent-tools developer guide](../guide/developer/agent-tools.md) covers the authoring flow.

## The grant model

The grant model is **namespace-level**. Binding a connector grants every tool under its namespace; the `sv.*` namespace is granted implicitly on every subject; an image's declared tools are surfaced 1:1 for the subject running it. There is no per-tool toggle — an operator either has a namespace or does not.

The resolver records provenance per tool and applies a defined precedence (`explicit > connector > platform > image`), so per-tool deny within a granted namespace can be layered in without changing the shape. An agent that needs a tighter surface than "the whole namespace" is granted a narrower namespace from a more focused connector or image.

## See also

- [Packages](packages.md) — the recursive folder layout, what a skill is, and how skills relate to tools.
- [Connectors](connectors.md) — what a connector is and how it bridges an external system into a unit.
- [Connector developer guide](../guide/developer/connectors.md) — authoring a connector that ships tools.
- [Agent tools developer guide](../guide/developer/agent-tools.md) — registering custom tools from an agent image via the SDK.
- [ADR-0040 — Actor state ownership matrix](../decisions/0040-actor-state-ownership-matrix.md) — the EF-authoritative shape that backs `agent_tool_grants` / `unit_tool_grants`.
- [ADR-0043 — Recursive package format](../decisions/0043-recursive-package-format.md) — how a package ships skills (and the tools they carry) inside a unit or agent folder.
