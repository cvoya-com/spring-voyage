# 0038 — AgentRuntime and ModelProvider as separate identities

> **Amendment (2026-05-22, [#2634](https://github.com/cvoya-com/spring-voyage/issues/2634)) — the persistence and dispatch layers are finished against one canonical execution-config shape.** This ADR collapsed user-facing execution config to `(runtime, model)` at the *authoring* layer (the manifest `ai:` block), but the *persistence* and *dispatch* layers were never migrated. Execution config drifted into three representations: the authored manifest YAML (an `ai:` block *and* a separate `execution:` block, with the image declarable in either), the persisted definition JSON (`DbAgentDefinitionProvider.ExtractExecution` reads an `execution:` shape *or* a legacy `ai:` shape), and the C# `AgentExecutionConfig` record (flat `Provider` / `Model` strings, a misnamed `Agent` slot). This amendment pins the target so the migration can be finished:
>
> 1. **One canonical authoring shape, one canonical persisted shape.** The image is declared exactly once — top-level `execution.image`, for agents and units alike. `AiEnvironmentManifest` / `AiManifest.Environment` are removed; the image is not an `ai`-block concept (a container image need not host an AI runtime — it may be a plain A2A agent).
> 2. **`AgentExecutionConfig` and the persisted `execution:` JSON block carry exactly `(runtime, model{provider, id}, image, hosting)`.** `model` is the structured `{provider, id}` pair this ADR already mandates — not a flat string. The flat `Provider` / `Model` slots and the misnamed `Agent` slot (renamed `runtime`, matching the `AgentRuntime` term) are removed.
> 3. **The dead execution fields are removed:** `ExecutionManifest.runtime` (the docker/podman container-runtime selector — already removed from operator surfaces by [ADR-0039 §7](archive/0039-units-are-agents.md)) and `ExecutionManifest.model` (duplicates `ai.model`). `ExtractExecution`'s legacy `ai:` branch is deleted.
> 4. **Clean-deploy, no back-compat** (v0.1), consistent with this ADR's original migration stance.
>
> The decision here is unchanged — this is ADR-0038's `(runtime, model)` collapse, applied to the two layers it never reached. The implementation breakdown and the full entanglement map live on [#2634](https://github.com/cvoya-com/spring-voyage/issues/2634).

- **Status:** Accepted — 2026-05-06 — `AgentRuntime`, `ModelProvider`, and `Model{provider, id}` become three distinct identities; the user-facing execution config collapses to `(runtime, model)`; the model provider is intrinsic to the model and is the credential / routing boundary; both runtimes and providers are platform configuration in a checked-in `runtime-catalog.yaml` with universal cross-runtime fields (`threadBinding`, `systemPromptInjection`, per-edge `credentialEnvVar`); per-provider and per-runtime classes are replaced by small strategy registries; `IAgentRuntime.Kind` is removed; credentials re-key to `(tenant, provider, authMethod)` with unit-level inheritance carried forward per ADR-0003; the wizard hides the provider picker for fixed-provider runtimes and shows it as a model-list filter otherwise; Dapr component files rename `conversation-*` → `llm-*`; clean-deploy hard rename across CLI, manifest, OpenAPI, Kiota, portal, and docs.
- **Date:** 2026-05-06
- **Umbrella:** [#1761](https://github.com/cvoya-com/spring-voyage/issues/1761) — AgentRuntime and ModelProvider split — multi-PR initiative (ADR-0038).
- **Related code:** see "Surface affected" below — the change touches Core domain, Manifest, Host.Api, AgentRuntimes projects, ModelProviders (new), Cli, Web, Dapr components, and docs.
- **Related ADRs:** [0021](0021-spring-voyage-is-not-an-agent-runtime.md), [0029](0029-tenant-execution-boundary.md), [0036](0036-single-identity-model.md), [0037](0037-package-schema-decomposition.md). Builds on the vocabulary alignment delivered in #1758.

## Context

The platform models the agent execution stack as a single concept, `IAgentRuntime`, which fuses three distinct things: the in-container execution engine, the LLM company whose API the engine calls, and a specific (engine, company, default-model) configuration.

This is visible in the project layout: `Cvoya.Spring.AgentRuntimes.{Claude, Google, Ollama, OpenAI}` register four `IAgentRuntime` instances. But:

- `OllamaAgentRuntime.Kind = "spring-voyage"` and `DisplayName = "Spring Voyage Agent (Ollama)"`. Ollama is not a runtime — it is the company hosting the LLM that the Spring Voyage Agent runtime talks to.
- `OpenAiAgentRuntime` has the same shape — it is the Spring Voyage Agent runtime configured with OpenAI.
- `ClaudeAgentRuntime` IS Claude Code (a real runtime), hardcoded to Anthropic.
- `GoogleAgentRuntime` IS Gemini CLI (a real runtime), hardcoded to Google.

The conflation has visible costs:

- The portal wizard exposes a "Provider" field whose value is mostly redundant — for the three CLI runtimes it must equal a fixed value, and only for `spring-voyage` does it actually pick anything.
- Credential storage is keyed `(tenant, runtime-id)`, so a tenant using both Claude Code and Spring Voyage with Anthropic stores the same Anthropic credential twice under different keys.
- New companies cannot be added without writing an `*AgentRuntime` class that is not, by the documented terminology, a runtime at all.
- The `Kind` property exists only to dispatch launchers around the conflation. It is vestigial scaffolding.
- The vocabulary in code, OpenAPI, CLI, manifest, and portal does not match the platform's own documentation, even after the rename pass in #1758.

The discussion that prompted this ADR settled on a clean three-concept split: `AgentRuntime`, `ModelProvider`, and `Model`. The user-facing execution config is just `(runtime, model)`; provider is intrinsic to the model. This ADR records the resulting design.

## Decision

### 1. Three concepts, one tuple at the user-facing surface

The platform reasons in terms of three distinct identities:

- **AgentRuntime** — the in-container execution engine. Closed set in v0.1: `claude-code`, `codex`, `gemini`, `spring-voyage`, `custom`.
- **ModelProvider** — the company whose API hosts a set of LLMs. Open set: `anthropic`, `openai`, `google`, `ollama`, future additions. The provider is not a user-facing pick; it is a property of the chosen model and is the routing / credential boundary the platform uses internally.
- **Model** — a specific LLM, identified by `(provider, id)`. The provider is intrinsic to the model.

The user-facing execution config is the pair `(runtime, model)`. The model carries its own provider; the platform validates that the model's provider is in the runtime's allowed set.

```yaml
ai:
  runtime: spring-voyage
  model:
    provider: ollama
    id: llama3.2:3b
```

Or, for a fixed-provider runtime:

```yaml
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-3-5-sonnet-20241022
```

The wire format is always `(runtime, model)` with the provider intrinsic to the model. There is no separate `provider` field on the wire, in the manifest, or stored on a tenant install row.

The wizard's UX is keyed off `IsProviderFixed`:

- **Fixed-provider runtimes** (`claude-code`, `codex`, `gemini`): the provider picker is **hidden**. The user picks a runtime; the model list shows only that runtime's single allowed provider's models.
- **Multi-provider runtimes** (`spring-voyage`, future `custom`): the provider picker **is shown** as a model-list filter. The user picks the runtime, optionally narrows to one provider to filter the model list, and picks a model. The selected provider is recorded only as the `model.provider` field — there is no separate `provider` slot stored on the unit/agent.

Both UX paths produce the same wire form. The picker is a presentation aid, not a data axis.

Rejected: keep `provider` as a separate user-facing axis carrying its own data slot. Carries dead weight for fixed-provider runtimes and creates two ways to express the same fact for multi-provider runtimes (the picker would diverge from `model.provider`).
Rejected: hide the provider picker uniformly. With a long combined model list (Anthropic + OpenAI + Google + Ollama for `spring-voyage`), the filter is genuine UX value, not redundant.
Rejected: encode the model as a flat `provider/id` string. Some Ollama model ids contain `/`; ambiguity at parse time. The structured shape is unambiguous and maps cleanly to typed clients.

### 2. AgentRuntimes are platform configuration, not per-runtime classes

The same configuration-driven approach used for model providers (decision 3) applies to agent runtimes. The set of runtimes the platform supports — together with each runtime's allowed providers and per-edge auth method — lives as data in the same checked-in YAML file. There is no `IAgentRuntime` interface carrying static metadata, no `ClaudeAgentRuntime` / `GoogleAgentRuntime` class.

Per-runtime *behaviour* (preparing the per-invocation working directory, env vars, MCP wiring, in-container probe plan) stays as code, behind the existing `IAgentRuntimeLauncher` strategy interface registered in DI by the runtime entry's `launcher` id. This is the analogue of the small `IModelProviderAdapter` strategies in decision 3: per-wire-family code, not per-runtime metadata.

The catalogue is loaded once at startup; entries deserialise to a generic `AgentRuntime` data record. The dispatch dictionary keys directly on `Id`. The previous `Kind` property is **removed** — with one entry per real runtime, `Id` and `Kind` become 1:1 and `Kind` was scaffolding for the conflation this ADR removes.

The shape (full file in decision 3):

```yaml
agentRuntimes:
  - id: claude-code
    displayName: Claude Code
    defaultImage: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
    launcher: claude-code-cli            # IAgentRuntimeLauncher strategy id
    threadBinding:
      kind: cli-arg
      argName: --resume                  # claude resumes by --resume <session-id>
    systemPromptInjection:
      kind: file
      filePath: AGENTS.md                # workspace-relative; the runtime reads it
    modelProviders:
      - id: anthropic
        authMethod: oauth
        credentialEnvVar: CLAUDE_CODE_OAUTH_TOKEN
  - id: codex
    displayName: Codex
    defaultImage: ghcr.io/cvoya-com/spring-voyage-codex-base:latest
    launcher: codex-cli
    threadBinding:
      kind: cli-arg
      argName: --conversation-id         # codex's resume flag
    systemPromptInjection:
      kind: file
      filePath: AGENTS.md
    modelProviders:
      - id: openai
        authMethod: api-key
        credentialEnvVar: OPENAI_API_KEY
  - id: gemini
    displayName: Gemini CLI
    defaultImage: ghcr.io/cvoya-com/spring-voyage-gemini-base:latest
    launcher: gemini-cli
    threadBinding:
      kind: cli-arg
      argName: --session-id
    systemPromptInjection:
      kind: file
      filePath: GEMINI.md                # gemini's own file convention
    modelProviders:
      - id: google
        authMethod: api-key
        credentialEnvVar: GOOGLE_API_KEY
  - id: spring-voyage
    displayName: Spring Voyage Agent
    defaultImage: ghcr.io/cvoya-com/spring-voyage-agent:latest
    launcher: spring-voyage-agent
    threadBinding:
      kind: env-var
      envVarName: SPRING_THREAD_ID
    systemPromptInjection:
      kind: env-var
      envVarName: SPRING_SYSTEM_PROMPT
    modelProviders:
      - id: anthropic
        authMethod: api-key
        credentialEnvVar: ANTHROPIC_API_KEY
      - id: openai
        authMethod: api-key
        credentialEnvVar: OPENAI_API_KEY
      - id: google
        authMethod: api-key
        credentialEnvVar: GOOGLE_API_KEY
      - id: ollama        # no authMethod, no credentialEnvVar — Ollama requires no credential
```

Each `agentRuntime` entry's `modelProviders:` list carries the (provider, authMethod, credentialEnvVar) edges that decision 4's matrix used to enumerate in code. The matrix is now data; adding a runtime, a provider edge, or a new auth method on an existing edge is a config edit (and a launcher strategy addition only when behaviour is genuinely novel).

`AllowedProviders` and `IsProviderFixed` (used by the wizard rule in decision 1) are derived from `modelProviders[].id` on the runtime's entry. A custom runtime, when added in a future release (decision 8), declares its own non-empty `modelProviders:` list in this same file.

#### Universal cross-runtime fields

Three fields on each `agentRuntime` entry capture features the platform's cross-cutting code reasons about uniformly across runtimes. Each one carries data the platform would otherwise have to ask a per-runtime class. The line is: anything the platform-side code reads across runtimes goes in YAML; runtime-protocol details (argv shape, output parsing, A2A choreography, MCP stamping) stay in the launcher strategy.

**`threadBinding` — how the platform thread reaches the runtime's session/conversation.** Every runtime has some kind of session-resume mechanism — Claude Code calls it a "session," Codex calls it a "conversation," Spring Voyage Agent calls it a "thread." The platform's [Thread](0030-thread-model.md) is the binding key: every unique participant set gets one runtime session, stable across invocations. The platform persists `(agent, thread) → runtime-session-id` in its own store; the YAML describes how to *deliver* that id to the runtime.

```yaml
threadBinding:
  kind: cli-arg | env-var | none
  argName: --resume               # required when kind: cli-arg
  envVarName: SPRING_THREAD_ID    # required when kind: env-var
```

`kind: none` is for runtimes that have no session concept (none ship today; reserved for completeness). The launcher strategy handles allocation flow — whether the runtime allocates the id and the launcher captures it from output (Claude Code, Codex, Gemini), or the platform passes the thread id verbatim (Spring Voyage Agent). Only the *delivery* axis (cli-arg vs env-var) is uniform enough to live in YAML.

**`systemPromptInjection` — how the assembled prompt reaches the agent.** The platform's prompt assembler (`IPromptAssembler`) produces the system prompt from the four-layer composition described in [`docs/concepts/agents.md`](../concepts/agents.md). The runtime receives the result via one of three mechanisms:

```yaml
systemPromptInjection:
  kind: file | env-var | argv
  filePath: AGENTS.md             # required when kind: file (workspace-relative)
  envVarName: SPRING_SYSTEM_PROMPT  # required when kind: env-var
  argName: --system-prompt        # required when kind: argv
```

CLI runtimes typically use `kind: file` because their CLIs already read a fixed-name file (`AGENTS.md` for Claude Code, `GEMINI.md` for Gemini). Spring Voyage Agent uses `kind: env-var`. A future runtime that takes the prompt as a flag uses `kind: argv`.

**`credentialEnvVar` (per-edge) — the env var the launcher writes the resolved credential into.** Naturally per-edge, because the env var name depends on **both** the runtime and the provider. Claude Code dispatching against Anthropic uses `CLAUDE_CODE_OAUTH_TOKEN`. Spring Voyage Agent dispatching against Anthropic uses `ANTHROPIC_API_KEY`. Modelling this on the (runtime, provider) edge is the only place it fits cleanly.

Edges whose `authMethod` is omitted (Ollama on Spring Voyage Agent) also omit `credentialEnvVar` — there is no credential to inject.

Rejected: lift the executable path / argv template into YAML. Each CLI runtime's argv is genuinely bespoke; YAML-level templating would need a placeholder language for `${THREAD_ID}` substitution and stays in the strategy more cheaply.
Rejected: `threadBinding.kind: file` (write the thread id to a file in the workspace). No runtime ships this convention today; introduce when one does.
Rejected: keep `IAgentRuntime` as a code interface and persist the matrix in C# constants. Doubles the surface — every matrix change is a code change.
Rejected: split the YAML into one file per runtime. Loses the central audit surface; a contributor cannot answer "which auth method does Codex accept from OpenAI?" by reading one file.
Rejected: keep `Kind` for forward compatibility. There is no extant or planned use case in which two runtimes share a `Kind`. Reintroduce when there is.

### 3. ModelProviders are platform configuration, alongside AgentRuntimes in `runtime-catalog.yaml`

Both the supported model providers and the supported agent runtimes live in the same checked-in configuration file. There are no `AnthropicModelProvider` / `OllamaModelProvider` classes.

```yaml
# /eng/runtime-catalog/runtime-catalog.yaml
modelProviders:
  - id: anthropic
    displayName: Anthropic
    apiBaseUrl: https://api.anthropic.com
    modelsEndpoint: /v1/models
    adapter: anthropic
    authMethods: [oauth, api-key]
    llmApiContract:
      name: anthropic
      version: v1
    defaultModels:
      - claude-opus-4-7
      - claude-sonnet-4-6
      - claude-haiku-4-5-20251001
  - id: openai
    displayName: OpenAI
    apiBaseUrl: https://api.openai.com
    modelsEndpoint: /v1/models
    adapter: openai-compatible
    authMethods: [api-key]
    llmApiContract:
      name: openai
      version: v1
    defaultModels:
      - gpt-4o
      - gpt-4o-mini
      - gpt-4-turbo
  - id: google
    displayName: Google
    apiBaseUrl: https://generativelanguage.googleapis.com
    modelsEndpoint: /v1/models
    adapter: google
    authMethods: [api-key]
    llmApiContract:
      name: google
      version: v1
    defaultModels:
      - gemini-2.0-flash
      - gemini-1.5-pro
      - gemini-1.5-flash
  - id: ollama
    displayName: Ollama
    apiBaseUrl: http://localhost:11434
    modelsEndpoint: /api/tags
    adapter: openai-compatible
    authMethods: []                      # empty list — Ollama requires no credential
    llmApiContract:                      # Ollama exposes an OpenAI-compatible chat-completions surface
      name: openai
      version: v1
    defaultModels:
      - llama3.2:3b
      - llama3.2:1b
      - qwen2.5:7b

agentRuntimes:
  # See decision 2 for the agent-runtime entries; they reference modelProviders by id.
  - id: claude-code
    # ...
```

A single generic `ModelProvider` class holds a `ModelProviderConfig` loaded from the `modelProviders:` section. Wire-format differences (parsing the live-models response, credential format checks, request envelope) are handled by a small set of `IModelProviderAdapter` strategies registered by adapter id (`openai-compatible`, `anthropic`, `google`). Provider names do not appear in class names — the strategy ids name the wire-format family, not the company.

New providers can be added config-only when they are OpenAI-compatible. Otherwise the author registers a new adapter strategy and a config entry.

**`authMethods`** lists the auth mechanisms the provider's API will accept. Closed enum: `oauth | api-key`. An empty list (`[]`) denotes a provider that requires no credential — Ollama's local endpoint, in v0.1. Per-runtime per-provider entries (decision 2) name a single `authMethod` from this list when one is required; for providers with an empty `authMethods` list the per-edge `authMethod` field is omitted entirely. There is no `none` token in the schema — absence is the representation.

**`llmApiContract`** names the LLM API surface the provider implements, as a structured `{name, version}` value. Closed enum on `name`: `anthropic | openai | google` for v0.1. The version is currently `v1` for every contract; including it explicitly leaves room for a future Anthropic Messages API v2, OpenAI v2, etc., without rewriting the schema.

**`defaultModels`** is the seed list of model ids the platform ships for the provider until live-fetch (`modelsEndpoint` consumed by the adapter's `FetchLiveModelsAsync`) is wired up. Plain string ids — no display names, no context windows, no other metadata. Once live fetch is implemented, the live response replaces this list and the YAML's `defaultModels` becomes the cold-start seed used the first time the platform talks to the provider (and the fallback when live fetch is unavailable, e.g. an offline Ollama host or a provider rate-limit). Tenants can add or override models via per-install configuration (`AgentRuntimeInstallConfig`), unchanged by this ADR. Schema validation does **not** pin the model ids — the list is allowed to grow without a schema bump.

The platform maps `llmApiContract` → Dapr Conversation component by convention: the in-tree Dapr component file lives at `eng/dapr/components/llm-{provider.id}.yaml` with `metadata.name: llm-{provider.id}`. Two providers that share a contract still ship distinct component files because Dapr requires the connection metadata (base URL, credentials) on each component's YAML; the *contract* tells the platform which adapter strategy to dispatch through, the *component file* tells Dapr how to reach the actual endpoint. The Dapr `type:` field stays `conversation.<provider>` — that is Dapr's contract, not ours.

A JSON schema ships alongside the YAML at `eng/runtime-catalog/runtime-catalog.schema.json` and pins the closed enums (`adapter`, `authMethods` element values, `llmApiContract.name`, runtime-entry `launcher` ids). CI lints the YAML against the schema so a typo in a contract name or auth method fails fast at PR review rather than at startup.

Rejected: per-provider classes. Forces a class per company name in code, obstructs config-only addition, and turns trivial wire-format additions into PRs.
Rejected: keep the field name `daprConversationComponent`. Bakes Dapr terminology into a config file most operators read without knowing what a Dapr building block is. The field name should describe role and contract, not implementation.
Rejected: name the field by the route key it dispatches through (e.g. the Dapr component name) rather than by the contract. Loses the contract identity; "two providers share a contract" becomes invisible at the YAML level even though it is a real platform property.
Rejected: drop the version on `llmApiContract`. Cheap to include now; expensive to retrofit when the second version of any provider's API ships.
Rejected: split runtimes and providers into separate files. The whole point of putting them together is that runtime entries reference provider ids in the same document — keeping them together is the audit surface.

### 4. Credential matrix is derived from `runtime-catalog.yaml`

A provider declares the auth methods it accepts (decision 3's `authMethods`). Each agent runtime declares, per provider it can dispatch to, the single auth method it consumes (decision 2's per-edge `authMethod`). The runtime × provider × authMethod matrix is the projection of these two pieces of config.

For the v0.1 catalogue, the projection is:

| Runtime         | Provider  | authMethod | credentialEnvVar          |
|-----------------|-----------|------------|---------------------------|
| `claude-code`   | anthropic | oauth      | `CLAUDE_CODE_OAUTH_TOKEN` |
| `codex`         | openai    | api-key    | `OPENAI_API_KEY`          |
| `gemini`        | google    | api-key    | `GOOGLE_API_KEY`          |
| `spring-voyage` | anthropic | api-key    | `ANTHROPIC_API_KEY`       |
| `spring-voyage` | openai    | api-key    | `OPENAI_API_KEY`          |
| `spring-voyage` | google    | api-key    | `GOOGLE_API_KEY`          |
| `spring-voyage` | ollama    | —          | —                         |

This table is documentation, not a code constant. The runtime constructs it at startup from the YAML; CI's schema check enforces that every per-edge `authMethod` is present in the corresponding provider's `authMethods` list.

Storage is keyed `(tenant, provider, authMethod)` — the credential row is shared across every runtime that consumes the same `(provider, authMethod)` pair (per decision 6). Dispatch resolves the runtime's per-edge `authMethod` against the catalogue, looks up that exact row, fails with a precise error if the matching credential is absent, and otherwise hands the resolved value to the launcher to inject into the container env under the same edge's `credentialEnvVar` name (per decision 2's universal cross-runtime fields). Edges whose provider has an empty `authMethods` list — and therefore no per-edge `authMethod` or `credentialEnvVar` — skip credential resolution entirely.

```csharp
// AuthMethod entries are loaded from runtime-catalog.yaml and exposed as a
// closed enum; the C# representation is data, not behaviour. Format checks
// and env-var injection live on the provider adapter (decision 3) keyed by
// authMethod id.
public enum AuthMethod { Oauth, ApiKey }
// "no auth" is represented by an empty authMethods list on the provider
// and an absent authMethod field on the per-runtime per-provider edge.
// There is no None enum value — absence is the representation.
```

The `--bare` flag (passing an Anthropic API key to the Claude Code CLI for degraded functionality) is **not** supported. The catalogue's per-edge entry for `claude-code` × `anthropic` names `oauth` and only `oauth`. This carries forward the strict per-path matrix from #1714; the dual-acceptance framing from #1690 stays rejected.

Rejected: store credentials per `(tenant, provider)` only, with method negotiation at dispatch. Negotiation hides errors and complicates the operator's mental model.
Rejected: store credentials per `(tenant, runtime, provider)`. Forces the same Anthropic API key to be entered twice when a tenant uses both `spring-voyage` and a future Anthropic-fronted runtime — the current design's actual flaw.
Rejected: hardcode the matrix in C#. The whole point of decisions 2 + 3 is that the matrix is data; encoding it twice would be the kind of duplication that goes stale silently.

### 5. Wire format: structured model object, no `provider` field

The `provider` field disappears from execution-config DTOs and manifests. The model is structured.

```json
{
  "execution": {
    "runtime": "spring-voyage",
    "model": { "provider": "ollama", "id": "llama3.2:3b" }
  }
}
```

CLI:

```
spring agent create --runtime spring-voyage --model-provider ollama --model llama3.2:3b
```

For fixed-provider runtimes, `--model-provider` is optional and inferred from `--runtime`; specifying it must equal the implied value or the CLI rejects the input.

The `agent` field on existing wire DTOs (renamed from `tool` in #1732) is replaced by the `runtime` + structured `model` pair. The `toolKind`-derived response field (renamed to `kind` in #1758) is removed — the runtime's identity and the model's provider supply everything callers were using `kind` for.

### 6. Provider is the credential and routing boundary

When a tenant configures a provider:

- One credential row per `(tenant, provider, authMethod)` at the **tenant default** layer.
- One Anthropic API key serves both `spring-voyage` (with Anthropic) and any future Anthropic-fronted runtime that consumes the api-key method.
- One Anthropic OAuth token serves `claude-code`.
- The tenant install surface has one row per provider, not per runtime — the wizard groups credential entry by provider.

Live-model fetch is per provider (`/v1/models`, `/api/tags`, …) rather than per runtime, so the catalog is shared across runtimes that target the same provider.

**Unit-level overrides carry forward unchanged.** [ADR-0003](0003-secret-inheritance-unit-to-tenant.md) — automatic fall-through from unit to tenant with opt-out — applies to provider credentials the same way it applies to every other secret today. A unit may store its own `(provider, authMethod)` credential row that overrides the tenant default; absent a unit-level row, the resolver falls through to the tenant. Per-agent secrets remain deferred per [ADR-0004](0004-per-agent-secrets.md) — the unit is still the trust boundary; agents inherit from their unit. This ADR does not change the inheritance shape; it only re-keys the row identity from `(scope, runtime-id)` to `(scope, provider-id, authMethod)`.

### 7. Migration: clean-deploy hard rename, no shim

This ADR re-shapes:

- The C# domain: `IAgentRuntime` interface **removed** (replaced by an `AgentRuntime` data record loaded from YAML, with `AllowedProviders` derived from the entry's `providers:` list); new `ModelProvider` data record; new `Model` record; reshaped `AgentExecutionConfig`. `Kind` removed.
- Project layout: the four per-provider / per-runtime projects (`Cvoya.Spring.AgentRuntimes.{Claude, Google, Ollama, OpenAI}`) **collapse** — their static metadata moves into `runtime-catalog.yaml`; their per-provider seed model catalogues become provider-attached data (inline or sibling JSON, scope decision in the implementation PR). `IAgentRuntimeLauncher` strategies (`ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`, `SpringVoyageAgentLauncher`) consolidate in a single `Cvoya.Spring.AgentRuntimes` project. `IModelProviderAdapter` strategies (`OpenAiCompatibleAdapter`, `AnthropicAdapter`, `GoogleAdapter`) live in a new `Cvoya.Spring.ModelProviders` project.
- New files: `eng/runtime-catalog/runtime-catalog.yaml` and `eng/runtime-catalog/runtime-catalog.schema.json`.
- Dapr component files: `conversation-*` → `llm-*`.
- Manifest: `ai.agent` → `ai.runtime`; `ai.model` becomes `{provider, id}`.
- Wire DTOs (`UnitExecutionResponse`, `AgentExecutionResponse`, `InstalledAgentRuntimeResponse`): `agent` → `runtime`; structured `model`; no flat `provider`; no `kind`.
- Kiota and openapi-typescript regenerated.
- CLI: `--agent` removed in favour of `--runtime`; `--model-provider` added; `--model` carries the provider-scoped id.
- Portal: unit-create wizard, agent-create wizard, execution tab, execution panel, model selector, credential entry, tenant install screens shift to the (runtime, model) shape with provider grouping for credentials.
- All tests and docs.

There is no transitional flag, no dual-acceptance window, no shim. Old-shape signals surface as parse errors with precise migration hints:

| Old shape                                  | Error                          | Migration hint                                                                                  |
|--------------------------------------------|--------------------------------|-------------------------------------------------------------------------------------------------|
| Manifest has `ai.agent:`                   | `LegacyAiAgentField`           | "ai.agent: is removed in ADR-0038; use ai.runtime: with a runtime id (claude-code, codex, gemini, spring-voyage, custom)" |
| Manifest has `ai.model:` as a string       | `LegacyAiModelStringForm`      | "ai.model: is now a {provider, id} object in ADR-0038"                                          |
| Manifest has `execution.provider:`         | `LegacyExecutionProviderField` | "execution.provider: is removed in ADR-0038; the provider is intrinsic to ai.model.provider"    |
| HTTP DTO sends `provider` at top level     | `LegacyProviderField`          | "provider is now nested inside model.provider"                                                  |
| HTTP DTO sends `agent` field               | `LegacyAgentField`             | "agent is renamed to runtime in ADR-0038"                                                       |
| HTTP DTO sends `kind` on execution responses | `LegacyKindField`            | "kind is removed in ADR-0038; identity is supplied by runtime + model.provider"                 |
| CLI uses `--agent`                         | rejected with hint             | "use --runtime in ADR-0038"                                                                     |

Operators with persisted installs from the prior shape will lose their install configuration. Clean deployment is authorised for this refactor.

### 8. Custom runtimes are entries in `runtime-catalog.yaml`

A custom runtime, when added in a future release, is an entry in the same `agentRuntimes:` list as the built-in runtimes. The entry declares a non-empty `providers:` list naming each `(provider-id, authMethod)` edge it supports. There is no sentinel "any" value — the runtime author lists the providers explicitly. The platform validates user input against this list at submit time. The runtime's behaviour ships as an `IAgentRuntimeLauncher` strategy registered against the entry's `launcher` id, exactly as the built-in runtimes do.

## Consequences

**Easier:**

- The vocabulary in code, OpenAPI, CLI, manifest, and portal matches the documentation. "Ollama is an agent runtime" disappears as a code surface.
- One credential per provider per tenant. Anthropic API keys, Google API keys, Anthropic OAuth tokens are entered once and served to every runtime that consumes them.
- New providers added by editing one config file when the wire format is OpenAI-compatible. New custom adapters are required only for genuinely non-compatible providers.
- The wizard's "Provider" pick disappears for fixed-provider runtimes; for `spring-voyage` it becomes a model-list filter rather than a separate axis.
- Live-model refresh is per provider, so two runtimes targeting the same provider share the catalog.
- `Kind` removal eliminates a property whose only purpose was scaffolding around the conflation.

**Harder:**

- The wire format change is breaking. CLI, Kiota client, portal, and any tenant-side external integrations release together.
- Provider-shape adapters (anthropic, google, openai-compatible, …) are a new extension surface. The split is at the right grain — one per wire-format family rather than per provider — but it is a surface to maintain.
- Operators with persisted installs from the prior shape lose their install configuration on the deployment that lands this ADR. Clean deploy is acknowledged, not absorbed.

**Not abstracted:**

- Per-tenant overrides of the shipped `runtime-catalog.yaml`. v0.1 ships the file as platform-level configuration; tenants add models via per-install model lists, not by adding new providers or runtimes. A future ADR can introduce tenant-level catalogue extension if a real need surfaces.
- Fully dynamic provider discovery (a tenant pasting an OpenAI-compatible endpoint URL and the platform inferring the provider). Out of scope; v0.1 admits providers via the checked-in YAML.
- Multi-shape credentials per `(tenant, provider, runtime)`. The matrix's per-edge single-shape rule is intentional. If a future runtime genuinely accepts both shapes for the same provider, a follow-up ADR introduces a discriminator.

## Surface affected (delivery scope)

This is a multi-PR initiative. The tracker issue [#1761](https://github.com/cvoya-com/spring-voyage/issues/1761) breaks the work into per-area PRs sequenced by `blocked-by`:

- **Core domain.** `IAgentRuntime` removal (replaced by `AgentRuntime` data record); `ModelProvider` data record; `Model` record; `Kind` removal; `AgentExecutionConfig` shape change; catalogue loader from YAML. Lands first; everything else depends on it.
- **Catalogue + adapters.** `eng/runtime-catalog/runtime-catalog.yaml` + `runtime-catalog.schema.json`; `IModelProviderAdapter` (`OpenAiCompatibleAdapter`, `AnthropicAdapter`, `GoogleAdapter`); the existing `IAgentRuntimeLauncher` strategies move under the new layout.
- **Project re-layout.** Per-provider / per-runtime projects (`AgentRuntimes.{Claude, Google, Ollama, OpenAI}`) collapse; new `Cvoya.Spring.AgentRuntimes` (launcher strategies) and `Cvoya.Spring.ModelProviders` (adapter strategies) projects.
- **Manifest + parser.** `ai.runtime`, `ai.model{provider,id}`; legacy errors per the table above.
- **Web API / OpenAPI / Kiota.** DTO restructure; `openapi.json` regen; `openapi-typescript` regen; Kiota regen.
- **CLI.** `--runtime`, `--model-provider`, `--model`; help text; legacy `--agent` rejection.
- **Web portal.** Unit-create wizard, agent-create wizard, execution tab, execution panel, model selector, credential entry, tenant install screens. Credential entry moves from per-runtime to per-provider; `Provider` axis disappears for fixed-provider runtimes.
- **Dapr components.** Rename files `conversation-*` → `llm-*`; update `metadata.name`.
- **Docs.** `docs/architecture/agent-runtime.md` refresh; `docs/architecture/identifiers.md` add the (runtime, model) shape; `docs/concepts/packages.md` AI block; `docs/glossary.md` add ModelProvider, refine AgentRuntime, retire Kind.
- **Tests.** Every layer.

The tracker issue carries the per-area umbrella sub-issues with `blocked-by` wiring; the Core-domain umbrella blocks every other area.
