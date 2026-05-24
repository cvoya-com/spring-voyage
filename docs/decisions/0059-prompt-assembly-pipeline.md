# 0059 — Prompt-assembly pipeline — layered providers, launcher-owned delivery, and `system_prompt_mode`

- **Status:** Proposed — consolidates the per-agent system-prompt pipeline as the single canonical record. Defines, in one place, the three layered providers (`PlatformPromptProvider`, `UnitContextBuilder`, role-specific instructions), the two injection sources that ride alongside them (connector prompt fragments and equipped skill bundles), the `system_prompt_mode` cascade (agent override → unit default → `append`), and the per-launcher delivery mechanism each runtime uses. Supersedes the §`systemPromptInjection` discussion in [ADR-0038](0038-agent-runtime-and-model-provider-split.md) (the cross-runtime field that decision introduced was deleted in PR [#2716](https://github.com/cvoya-com/spring-voyage/pull/2716) — each launcher knows its own delivery mechanism and `system_prompt_mode` chooses the variant; the field was dead metadata). The rest of ADR-0038 stands.
- **Date:** 2026-05-24
- **Related ADRs:** [0038](0038-agent-runtime-and-model-provider-split.md) — `(runtime, model)` split; this record supersedes only its §`systemPromptInjection` discussion. The catalogue's other universal cross-runtime fields (`threadBinding`, per-edge `credentialEnvVar`) are unaffected. [0056](0056-tool-only-side-effects.md) — tool calls are the only side-effect channel; the platform-contract layer carries the reads-vs-side-effects clause and the always-on tool catalog this record's §2 enumerates. [0057](0057-sidecar-local-mcp-server.md) — sidecar-local MCP server; the platform tools the prompt names are reached through the sidecar's stdio MCP proxy. [0058](0058-spring-voyage-container-contract.md) — Spring Voyage container contract; the `.spring/system-prompt.md` path this record's §5 describes lives under the `.spring/` namespace declared there in §2.2.2.
- **Related code:** `src/Cvoya.Spring.Core/Execution/IPromptAssembler.cs`, `src/Cvoya.Spring.Core/Execution/PromptAssemblyContext.cs`, `src/Cvoya.Spring.Core/Execution/AgentBootstrapContribution.cs`, `src/Cvoya.Spring.Core/Catalog/SystemPromptMode.cs`, `src/Cvoya.Spring.Dapr/Prompts/PromptAssembler.cs`, `src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs`, `src/Cvoya.Spring.Dapr/Prompts/UnitContextBuilder.cs`, `src/Cvoya.Spring.Dapr/Prompts/AgentInstructionsBuilder.cs`, `src/Cvoya.Spring.Dapr/Connectors/IConnectorPromptContextResolver.cs`, `src/Cvoya.Spring.Dapr/Skills/EquippedBundleLoader.cs`, `src/Cvoya.Spring.Dapr/Execution/AgentBootstrapBundleProvider.cs`, `src/Cvoya.Spring.AgentRuntimes/Launchers/{ClaudeCodeLauncher,CodexLauncher,GeminiLauncher,SpringVoyageAgentLauncher}.cs`.
- **Related issues:** [#2719](https://github.com/cvoya-com/spring-voyage/issues/2719) — this ADR. PRs that delivered the current shape: [#2664](https://github.com/cvoya-com/spring-voyage/pull/2664) (thread-history layer removed; bundle composition routed through the assembler), [#2709](https://github.com/cvoya-com/spring-voyage/pull/2709) (always-on tool catalog folded into the platform-contract layer), [#2710](https://github.com/cvoya-com/spring-voyage/pull/2710) (platform-contract layer expansion), [#2711](https://github.com/cvoya-com/spring-voyage/pull/2711) (inbound-message envelope + `sv.thread.*` tools named in the platform contract), [#2714](https://github.com/cvoya-com/spring-voyage/pull/2714) (`system_prompt_mode` plumbed through Core + Web API + Kiota), [#2715](https://github.com/cvoya-com/spring-voyage/pull/2715) (`--system-prompt-mode` on the CLI), [#2716](https://github.com/cvoya-com/spring-voyage/pull/2716) (launcher honouring + filename refactor + `systemPromptInjection` deletion), [#2717](https://github.com/cvoya-com/spring-voyage/pull/2717) (Portal panels surface the toggle), [#2721](https://github.com/cvoya-com/spring-voyage/pull/2721) (GitHub connector contributes `sv.connectors.github.get_installation_token` guidance).

## Context

The per-agent system prompt is the platform's largest cross-cutting surface inside the agent container: every runtime CLI we wrap consumes some shape of it on every turn, and the prompt is the only place where the platform's messaging discipline, tool catalog, and identity framing reach a fresh model session before the first inbound message. Until the #2664 / #2709 / #2716 wave the assembly path had grown three implicit shapes: an in-code composer with a removed-but-not-deleted thread-history layer, a per-runtime cross-cutting field (`systemPromptInjection`) on the catalogue that no runtime actually consulted at dispatch time, and a per-launcher tangle of "which file do we write, and what flag do we pass?" decided inline. The cost was the usual one — every change to the prompt's structure had to touch three of those surfaces, and the field-vs-launcher disagreement was a latent footgun (a YAML edit on `systemPromptInjection` would silently no-op).

The current shape is uniform. `PromptAssembler` composes three layered sections from a `PromptAssemblyContext`; the bundle provider hands the assembled result to each launcher; each launcher writes the prompt into its CLI's native delivery channel and selects an append-or-replace variant from `system_prompt_mode`. The cross-runtime metadata field is gone. This ADR is the canonical statement of that pipeline so future amendments to the prompt's structure, to a launcher's delivery mechanism, or to `system_prompt_mode`'s semantics land in one place.

## Decision

The per-agent system prompt is composed by `PromptAssembler.AssembleAsync` from three layered providers plus two adjacent injection sources, then delivered to the runtime CLI by the per-runtime `IAgentRuntimeLauncher` strategy. The decision is the union of the seven sections below.

### 1. Pipeline overview

`PromptAssembler` (interface in `src/Cvoya.Spring.Core/Execution/IPromptAssembler.cs`; implementation in `src/Cvoya.Spring.Dapr/Prompts/PromptAssembler.cs`) is the platform's sole prompt composer. The bundle provider (`AgentBootstrapBundleProvider`) calls it once per bootstrap build with a `PromptAssemblyContext` that carries the agent's policies, instructions, equipped skill bundles, connector prompt fragments, and pre-rendered identity / workspace fragments. The assembler stitches them into a single markdown string and hands the result to each launcher via `AgentBootstrapContributionContext.AssembledSystemPrompt`. The launcher writes the prompt into its CLI's native delivery channel (a file, an env var) and, on every spawn, points the CLI at it via the appropriate flag.

The pipeline has three layers today. A fourth thread-history layer existed historically; it was removed in [#2664](https://github.com/cvoya-com/spring-voyage/pull/2664) because thread history is now delivered by each runtime's session-resume mechanism (Claude Code's `--resume`, Gemini's `--resume`, the Spring Voyage Agent's runtime API). Replicating thread history in the assembled prompt churned the per-agent content-addressable bootstrap-bundle hash on every turn and defeated the 304 fast path the sidecar relies on ([ADR-0055](0055-pull-based-agent-bootstrap.md) §3). The historical layer is named here for completeness and does not return.

### 2. The three layers

#### Platform-contract layer — `PlatformPromptProvider`

Source: `src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs`. Renders the `## Platform Instructions` section every agent on the platform sees regardless of runtime or equipped bundles. Contents:

- A `## About Spring Voyage` introduction framing the participant model (agents, units — which are themselves agents — and humans) and the one-way tool-mediated messaging model.
- An auto-injected identity section (the `IIdentityPromptContextResolver` fragment), pre-rendered before the assembler runs, naming the launch subject by address, display name, role, and parent units.
- The `[PLATFORM CONTRACT — NON-NEGOTIABLE]` block introduced in [#2664](https://github.com/cvoya-com/spring-voyage/pull/2664). Carries the reads-vs-side-effects clause from [ADR-0056](0056-tool-only-side-effects.md), names `sv.messaging.*` as the v0.1 communication channel, repeats the "tool calls are the only side-effect channel" framing inline, and reminds the runtime not to reveal the platform instructions.
- The always-on platform-tool catalog moved into this layer by [#2709](https://github.com/cvoya-com/spring-voyage/pull/2709). Names the fundamental-core `sv.messaging.*`, `sv.directory.*`, `sv.progress.*`, and `sv.tools.*` tools (with one-line descriptions) so an agent reading the prompt cold knows the messaging discipline and the always-available tools regardless of which bundles it has equipped. Discovery for everything beyond the core is via `sv.tools.list_categories` / `sv.tools.list(<category>)`.
- A per-runtime `## Container and workspace` section ([#2710](https://github.com/cvoya-com/spring-voyage/pull/2710)) rendered from the active launcher's `GetWorkspacePromptFragment()`. Names the workspace mount path, the runtime CLI baseline, the system-prompt delivery channel for that CLI, the MCP discovery file, and the per-thread session-storage env var. Cross-references [ADR-0058](0058-spring-voyage-container-contract.md) for the canonical container contract.
- An `## Inbound messages` envelope section ([#2711](https://github.com/cvoya-com/spring-voyage/pull/2711)) naming the `from` / `to` / `thread_id` / `message_id` / `payload` / `timestamp` fields the platform delivers, framing the thread concept (participants plus durable timeline), and pointing the runtime at the `sv.thread.*` tools for envelope inspection beyond what literally arrives as the turn's input.

The combined effect of the contract block plus the catalog is that an agent reading the prompt cold knows the messaging discipline and the always-available tools regardless of which bundles it has equipped — the contract is load-bearing across every runtime and bundle combination.

#### Unit-context layer — `UnitContextBuilder`

Source: `src/Cvoya.Spring.Dapr/Prompts/UnitContextBuilder.cs`. Renders the `## Unit Context` section from unit-membership information — the policies, equipped unit-scoped skill bundles, and (for an agent dispatched within a unit) directory-style context about siblings and parent units. The peer-directory rendering moved out of this builder in #2231 (composition is queried on demand via `sv.directory.*`); the old `### Available Skills` block was removed in [#2709](https://github.com/cvoya-com/spring-voyage/pull/2709) when the platform-tool catalog moved to the platform-contract layer. What remains is unit-scoped state the role-specific layer below cannot reproduce: the unit's policies and the unit's equipped skill bundles (which member agents inherit through the unit-context section without an explicit inheritance table).

#### Role-specific-instructions layer

Source: `src/Cvoya.Spring.Dapr/Prompts/AgentInstructionsBuilder.cs`. Renders the `## Role-specific instructions` section from the agent's own `instructions:` block in its YAML / template, plus any package-level skill bundles equipped directly on the agent subject. The section header was renamed from `## Agent Instructions` to `## Role-specific instructions` in [#2664](https://github.com/cvoya-com/spring-voyage/pull/2664) so the name describes what the content *is* rather than who authored it — unit-shaped subjects (a unit-as-agent under [ADR-0017](0017-unit-is-an-agent-composite.md)) see the same header as agent-shaped subjects.

### 3. Injection sources outside the layered providers

Two sources contribute prompt content alongside the three layers; they are not "layers" because they are dispatched per-binding / per-bundle rather than rendered as a single fixed slot.

#### Connector prompt fragments

Flow through `IConnectorPromptContextResolver` (source: `src/Cvoya.Spring.Dapr/Connectors/IConnectorPromptContextResolver.cs`). At bundle-build time the resolver walks every connector binding applicable to the launch subject (direct bindings on the subject's unit plus inherited bindings on parent units), invokes each connector's `IConnectorPromptContextContributor`, and returns the ordered list of markdown fragments the assembler renders under a single `## Connector context (auto-injected by platform)` heading. A contributor that returns `null` is silently skipped; bindings without a registered contributor are legal config. Example: the GitHub connector contributes its installation context plus `sv.connectors.github.get_installation_token` tool guidance ([#2721](https://github.com/cvoya-com/spring-voyage/pull/2721)) so a fresh model session knows the connector's identity and the tool name to mint an installation token.

#### Equipped skill bundles

Each bundle equipped on the agent or its parent units contributes its own prompt fragment via `EquippedBundleLoader.LoadAsync` (source: `src/Cvoya.Spring.Dapr/Skills/EquippedBundleLoader.cs`). The loader returns two ordered lists — bundles equipped on the unit (rendered as a sub-section of the unit-context layer) and bundles equipped directly on the agent (rendered as a sub-section of the role-specific-instructions layer). Multi-parent unions dedup on `(PackageName, SkillName)` with first-occurrence wins; parent units sort alphabetically by display name so the operator's portal view matches the rendered order. The `sv.conversational.defaults` bundle ([#2661](https://github.com/cvoya-com/spring-voyage/pull/2661)) is the canonical example: it points the runtime at the platform-contract layer's tool catalog rather than duplicating it, and adds package-specific guidance on top.

### 4. `system_prompt_mode`

A two-mode toggle on the agent or unit `execution:` block, declared as a `SystemPromptMode` enum in `src/Cvoya.Spring.Core/Catalog/SystemPromptMode.cs`. Added in [#2714](https://github.com/cvoya-com/spring-voyage/pull/2714) (Core + Web API + Kiota).

| Mode | YAML literal | Semantics |
|---|---|---|
| `Append` | `append` | Default. The platform prompt is appended to the runtime CLI's own default system prompt (Claude Code's coding-assistant baseline, etc.) so engineer-shaped agents keep the CLI's coding guidance. |
| `Replace` | `replace` | The platform prompt replaces the runtime CLI's default system prompt entirely. Non-coding agents (routers, PMs, analysts) opt in so the CLI's baseline does not shape responses. |

**Resolution cascade.** Agent override → unit default → default `append`. Applied at the dispatch site (`A2AExecutionDispatcher`) so each launcher consumes a resolved `context.SystemPromptMode` without further fallback. The cascade is uniform across launchers; per-launcher honouring varies because CLI capability varies (see §5).

**Surfaces.** The CLI exposes `--system-prompt-mode <append|replace>` on agent and unit execution commands ([#2715](https://github.com/cvoya-com/spring-voyage/pull/2715)). The Portal surfaces the toggle on the agent-execution and unit-execution panels ([#2717](https://github.com/cvoya-com/spring-voyage/pull/2717)). The Web API accepts the field via the regenerated Kiota client.

### 5. Per-launcher delivery

Each `IAgentRuntimeLauncher` strategy under `src/Cvoya.Spring.AgentRuntimes/Launchers/` knows its CLI's prompt-delivery mechanism and selects the variant from `context.SystemPromptMode`. The cross-runtime metadata field this section's behaviour used to be described through (`AgentRuntime.SystemPromptInjection` in the runtime catalogue) was deleted in [#2716](https://github.com/cvoya-com/spring-voyage/pull/2716) — see the supersession sub-section below.

**`ClaudeCodeLauncher`.** Writes the assembled prompt to `$SPRING_WORKSPACE_PATH/.spring/system-prompt.md` (under the `.spring/` namespace per [ADR-0058](0058-spring-voyage-container-contract.md) §2.2.2 so it does not collide with any project clone's own `CLAUDE.md`). Passes `--append-system-prompt-file <path>` when `system_prompt_mode` is `Append` and `--system-prompt-file <path>` when `Replace`. Both flags consume a workspace-resolved absolute path; the launcher emits exactly one.

**`GeminiLauncher`.** Gemini CLI's only system-prompt override is the `GEMINI_SYSTEM_MD` env var, which is **replace-only** — gemini-cli 0.41.x exposes no append flag. The delivery is asymmetric by mode:
- `Append` (default): the launcher writes the assembled prompt to `GEMINI.md` at the workspace root and leaves `GEMINI_SYSTEM_MD` unset. Gemini auto-discovers `GEMINI.md` as its instructions file; the CLI's coding-assistant baseline is preserved.
- `Replace`: the launcher writes the assembled prompt to `.spring/system-prompt.md` under the `.spring/` namespace and sets `GEMINI_SYSTEM_MD` to the absolute path. The CLI drops its own baseline; `GEMINI.md` is not written.

The Append-mode auto-discovery route is documented as the only delivery channel Gemini supports for that mode.

**`CodexLauncher`.** Writes the assembled prompt to `AGENTS.md` at the workspace root regardless of mode. The Codex CLI has no `--system-prompt-*` flags and no replace-only env var (tracked upstream at openai/codex#11588). `system_prompt_mode` cannot be honoured on Codex today; when `Replace` is requested the launcher logs an informational message so operators see the mismatch, and the next turn still uses `AGENTS.md`. Revisit when openai/codex#11588 ships per-runtime override flags.

**`SpringVoyageAgentLauncher`.** A2A-native — the Python agent owns its own A2A endpoint and consumes the assembled prompt via the `SPRING_SYSTEM_PROMPT` env var rather than a workspace file. There is no CLI surface to express append-vs-replace against; the field is informational on this launcher.

#### Supersession sub-section — `AgentRuntime.SystemPromptInjection` removal

PR [#2716](https://github.com/cvoya-com/spring-voyage/pull/2716) deleted the `AgentRuntime.SystemPromptInjection` field that [ADR-0038](0038-agent-runtime-and-model-provider-split.md) introduced as one of the universal cross-runtime fields on `runtime-catalog.yaml`. The deletion covered the record property, the YAML schema, the catalogue loader, and the tests that pinned it. Rationale:

- **Each launcher knows its own delivery mechanism.** The CLI's prompt-delivery channel — a file path, an env var, a flag — is determined by the CLI itself, not by platform configuration. Encoding it on the catalogue forced a parallel surface that read at startup but was never consulted at dispatch time; the launcher always took the authoritative path.
- **`system_prompt_mode` selects the variant.** The append-vs-replace decision is now an agent / unit author concern carried on the execution spec and resolved at dispatch time, not a static catalogue property of the runtime. The cross-runtime field could not express the per-spec variant.
- **The field was dead metadata.** No code path at dispatch consulted it. A YAML edit on `systemPromptInjection` was silently no-op — the kind of drift [ADR-0058](0058-spring-voyage-container-contract.md) §6 names as the trigger for explicit deprecation.

[ADR-0038](0038-agent-runtime-and-model-provider-split.md) is **superseded in part** by this section — only its §`systemPromptInjection` discussion (the bullet under "Universal cross-runtime fields"). The rest of ADR-0038 stands: the `(runtime, model)` split, the `runtime-catalog.yaml` configuration shape, the per-edge credential model, the wizard rules, the migration directive, and the other two universal cross-runtime fields (`threadBinding` and per-edge `credentialEnvVar`) are unaffected.

### 6. End-to-end assembly flow

When an agent is dispatched, the bundle build proceeds in order:

1. **Bundle-build trigger.** The sidecar pulls a fresh bootstrap bundle via `GET /v1/bootstrap/agents/{agentId}` ([ADR-0055](0055-pull-based-agent-bootstrap.md) §3). The worker's `AgentBootstrapBundleProvider.BuildAsync` runs.
2. **Equipped bundles resolved.** The provider calls `EquippedBundleLoader.LoadAsync` to fetch the unit-scoped and agent-scoped skill bundles (with multi-parent dedup and display-name ordering).
3. **Connector + identity + workspace fragments resolved.** The provider invokes `IConnectorPromptContextResolver.ResolveAsync` for the launch subject, `IIdentityPromptContextResolver` for the identity section, and the active launcher's `GetWorkspacePromptFragment()` for the per-runtime container description.
4. **Assembly context built.** The provider builds a `PromptAssemblyContext` carrying policies, agent instructions, the two equipped-bundle lists, the connector fragments, the identity fragment, and the workspace fragment.
5. **Assembly.** `PromptAssembler.AssembleAsync` composes the layered prompt — platform-contract layer (including the identity and workspace fragments, the connector-context subsection) → unit-context layer (policies + unit bundles) → role-specific-instructions layer (agent instructions + agent bundles) — and returns a single markdown string.
6. **Hand-off to launchers.** The assembled prompt is set on `AgentBootstrapContributionContext.AssembledSystemPrompt`. The provider invokes the active launcher's `ContributeBundleAsync`, which reads the assembled prompt and `context.SystemPromptMode` and writes the file (or returns env-var content) per §5.
7. **CLI consumption.** On every subsequent A2A `message/send`, the sidecar spawns the CLI with the launcher-stamped argv. The runtime CLI consumes the prompt via its native mechanism (file at known path, env var, or flag) and the inbound message via the A2A envelope.

The numbering above is for the flow steps, which are sequential by nature.

### 7. Related ADRs and PRs

See the front matter for the full ADR and PR cross-link set. The load-bearing relationships are:

- **[ADR-0038](0038-agent-runtime-and-model-provider-split.md)** — partially superseded by §5 of this record (`systemPromptInjection` field removed). The `(runtime, model)` split and the rest of the catalogue model stand.
- **[ADR-0056](0056-tool-only-side-effects.md)** — tool calls are the only side-effect channel. Informs the platform-contract layer's reads-vs-side-effects clause and the always-on `sv.messaging.*` tool catalog.
- **[ADR-0057](0057-sidecar-local-mcp-server.md)** — sidecar-local MCP server. Informs how runtimes reach the platform tools the prompt names — the catalog entries dispatch through the sidecar's stdio MCP proxy, not a cross-network HTTP MCP transport.
- **[ADR-0058](0058-spring-voyage-container-contract.md)** — Spring Voyage container contract. The `.spring/system-prompt.md` path the Claude and Gemini-Replace launchers use lives under the `.spring/` namespace declared in §2.2.2.

## Consequences

- **One canonical pipeline.** The three layers, the two adjacent sources, the mode toggle, and the four launchers are enumerated in one document. Future amendments to the prompt's structure (a new section, a removed layer, a rename) land here.
- **Each launcher owns its delivery mechanism.** The cross-runtime `systemPromptInjection` field is gone; the launcher is the authoritative source. A new runtime ships with its own launcher and chooses its own file / env-var / flag; nothing in `runtime-catalog.yaml` changes.
- **`system_prompt_mode` honouring is asymmetric, by design.** Claude Code honours both modes natively; Gemini honours Replace via env var and Append via auto-discovery (no append flag exists upstream); Codex cannot honour Replace today (openai/codex#11588); the Spring Voyage Agent is A2A-native and the field is informational. The asymmetry is documented per launcher in §5; operator surfaces (CLI, Portal) expose the toggle uniformly and the launcher absorbs the per-CLI capability.
- **The platform contract is load-bearing across every runtime + bundle combination.** Moving the always-on tool catalog into the platform-contract layer ([#2709](https://github.com/cvoya-com/spring-voyage/pull/2709)) means a fresh agent reading the prompt cold knows the messaging discipline and the always-available tools without depending on any equipped bundle. The `sv.conversational.defaults` bundle points at the catalog rather than duplicating it.
- **Thread history stays out of the assembled prompt.** Each runtime's session-resume mechanism owns thread history. Replicating it in the prompt churned the content-addressable bundle hash on every turn and defeated the sidecar's 304 fast path. The removed thread-history layer does not return.
- **The bundle hash is stable per `(agent, equipped bundles, connectors, policies, instructions, mode)` tuple.** The assembled prompt is a deterministic function of inputs that change on operator action, not per-turn data. The bundle hash gates the sidecar's pull / 304 path ([ADR-0055](0055-pull-based-agent-bootstrap.md) §3); this property is what keeps it cheap.

## Not abstracted

- **The per-CLI argv / flag shape.** Each launcher's `BuildClaudeArgv` / `DefaultGeminiArgv` / Codex argv / Spring Voyage argv lives in the launcher's source. The contract here is "the launcher chooses the delivery mechanism and the flag"; the *contents* of that argv stay per-launcher.
- **The runtime-author's per-bundle prompt body.** A bundle's markdown is bundle-author content; the pipeline carries it through to the role-specific-instructions or unit-context layer without rewriting. Bundle-authoring conventions live on `docs/concepts/skills.md`, not here.
- **The connector contributor implementation.** Each connector's `IConnectorPromptContextContributor` decides what to render; the pipeline only orders fragments and wraps them under a single heading. Connector-authoring conventions live on `docs/concepts/connectors.md`.
- **Tenant- or per-install overrides of the platform-contract text.** v0.1 ships the platform-contract layer as platform-level code. A future ADR can introduce per-tenant overlay if a real need surfaces; the current shape is "every tenant gets the same contract".

## Revisit criteria

- A fourth layered provider (a return of thread history, a new "task context" layer, etc.). The pipeline accommodates an additional source under §3, but adding a fourth fixed-slot layer is a §2 amendment with a content-addressability impact analysis.
- A second cross-runtime field's removal or addition on the catalogue. §5 names the supersession of `systemPromptInjection` specifically; a future removal of `threadBinding` or a new universal field amends this ADR in addition to ADR-0038.
- openai/codex#11588 shipping per-runtime override flags. The trigger to revisit §5's Codex entry and lift the launcher's informational-log-only handling of `Replace`.
- A new launcher whose CLI's prompt-delivery mechanism does not fit the file / env-var / flag taxonomy §5 enumerates. The trigger to amend §5 with the new shape rather than silently extending the taxonomy.
- A bundle-hash churn problem traced back to the assembler emitting per-turn content. The trigger to re-audit which inputs flow into `PromptAssemblyContext` and re-confirm the deterministic property §"Consequences" relies on.
