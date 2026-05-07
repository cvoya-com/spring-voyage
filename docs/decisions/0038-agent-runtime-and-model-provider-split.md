# 0038 — AgentRuntime and ModelProvider as separate identities

- **Status:** Proposed — 2026-05-06 — `IAgentRuntime`, `IModelProvider`, and `Model{provider, id}` become three distinct identities; the user-facing execution config collapses to `(runtime, model)`; provider is intrinsic to the model and is the credential / routing boundary; provider config moves to a checked-in `model-providers.yaml`; provider classes per company are removed in favour of a small set of wire-format adapters; `IAgentRuntime.Kind` is removed; credentials re-key to `(tenant, provider, shape)`; Dapr component files rename `conversation-*` → `llm-*`; clean-deploy hard rename across CLI, manifest, OpenAPI, Kiota, portal, and docs.
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

The wizard, CLI, and manifest do not expose a separate "Provider" pick. The model picker filters by `model.provider`; for fixed-provider runtimes the filter is implicit (only one provider's models ever appear).

Rejected: keep `provider` as a separate user-facing axis. Carries dead weight for the three CLI runtimes; the redundancy is the core complaint #1752 partially papered over.
Rejected: encode the model as a flat `provider/id` string. Some Ollama model ids contain `/`; ambiguity at parse time. The structured shape is unambiguous and maps cleanly to typed clients.

### 2. AgentRuntime declares which providers it accepts

`IAgentRuntime` carries an `AllowedProviders` set:

```csharp
public interface IAgentRuntime
{
    string Id { get; }
    string DisplayName { get; }
    string DefaultImage { get; }
    IReadOnlySet<string> AllowedProviders { get; }
    bool IsProviderFixed => AllowedProviders.Count == 1;
    IReadOnlyList<ProbeStep> GetProbeSteps(...);
}
```

Built-in runtimes:

| Runtime         | AllowedProviders                                |
|-----------------|-------------------------------------------------|
| `claude-code`   | `{ anthropic }`                                 |
| `codex`         | `{ openai }`                                    |
| `gemini`        | `{ google }`                                    |
| `spring-voyage` | `{ anthropic, openai, google, ollama }`         |
| `custom`        | declared by the runtime author — non-empty closed set |

The previous `Kind` property is **removed**. With one `IAgentRuntime` per actual runtime, `Id` and `Kind` become 1:1 and the launcher dispatch dictionary keys directly on `Id`. `Kind` was scaffolding for the conflation this ADR removes.

Rejected: keep `Kind` for forward compatibility. There is no extant or planned use case in which two runtimes share a `Kind`. Reintroduce when there is.

### 3. ModelProviders are platform configuration, not per-provider classes

The set of supported providers is a checked-in configuration file. There are no `AnthropicModelProvider` / `OllamaModelProvider` classes.

```yaml
# /platform/model-providers.yaml
providers:
  - id: anthropic
    displayName: Anthropic
    apiBaseUrl: https://api.anthropic.com
    modelsEndpoint: /v1/models
    adapter: anthropic
    acceptedShapes: [oauth, api-key]
    llmComponent: llm-anthropic       # Dapr Conversation component name on the sidecar resources path
  - id: openai
    displayName: OpenAI
    apiBaseUrl: https://api.openai.com
    modelsEndpoint: /v1/models
    adapter: openai-compatible
    acceptedShapes: [api-key]
    llmComponent: llm-openai
  - id: google
    displayName: Google
    apiBaseUrl: https://generativelanguage.googleapis.com
    modelsEndpoint: /v1/models
    adapter: google
    acceptedShapes: [api-key]
    llmComponent: llm-google
  - id: ollama
    displayName: Ollama
    apiBaseUrl: http://localhost:11434
    modelsEndpoint: /api/tags
    adapter: openai-compatible
    acceptedShapes: [none]
    llmComponent: llm-ollama
```

A single generic `ModelProvider` class holds a `ModelProviderConfig` loaded from this file. Wire-format differences (parsing the live-models response shape, credential format checks, request envelope) are handled by a small set of `IModelProviderAdapter` strategies registered by adapter id (`openai-compatible`, `anthropic`, `google`). Provider names do not appear in class names — the strategy ids name the wire-format family, not the company.

New providers can be added config-only when they are OpenAI-compatible. Otherwise the author registers a new adapter strategy and an entry in the YAML.

The `llmComponent` field names a Dapr Conversation component on the sidecar's `--resources-path` directory. The field is intentionally named `llmComponent` rather than `daprConversationComponent` so the config reads naturally for operators who do not know the Dapr internals; a header comment in the file disambiguates. Dapr component files are also renamed: `dapr/components/conversation-{provider}.yaml` → `dapr/components/llm-{provider}.yaml`. The `metadata.name` inside each file is updated to match. The Dapr `type:` field stays `conversation.<provider>` — that is Dapr's contract, not ours.

Rejected: per-provider classes. Forces a class per company name in code, obstructs config-only addition, and turns trivial wire-format additions into PRs.
Rejected: keep the field name `daprConversationComponent`. Bakes Dapr terminology into a config file most operators read without knowing what a Dapr building block is. The field name should describe role, not implementation.

### 4. Credential matrix: per-provider shapes, runtime declares which shape it consumes

A provider declares the credential shapes it accepts. Storage is keyed `(tenant, provider, shape)`:

```csharp
public sealed record ProviderCredentialShape(
    string Id,                       // "oauth", "api-key", "none"
    string DisplayHint,
    string EnvVarOnInjection,
    Func<string, bool> FormatCheck);
```

The matrix between runtimes and providers names exactly one shape per cell:

| Runtime         | Provider  | Shape    |
|-----------------|-----------|----------|
| `claude-code`   | anthropic | oauth    |
| `codex`         | openai    | api-key  |
| `gemini`        | google    | api-key  |
| `spring-voyage` | anthropic | api-key  |
| `spring-voyage` | openai    | api-key  |
| `spring-voyage` | google    | api-key  |
| `spring-voyage` | ollama    | none     |

If the matching credential row is absent, dispatch fails with a precise error pointing at the missing credential.

The `--bare` flag (passing an Anthropic API key to the Claude Code CLI for degraded functionality) is **not** supported. The Claude Code agent runtime only consumes OAuth tokens. This carries forward the strict per-path matrix from #1714; the dual-acceptance framing from #1690 stays rejected.

Rejected: store credentials per `(tenant, provider)` only, with shape negotiation at dispatch. Negotiation hides errors and complicates the operator's mental model.
Rejected: store credentials per `(tenant, runtime, provider)`. Forces the same Anthropic API key to be entered twice when a tenant uses both `spring-voyage` and a future Anthropic-fronted runtime — the current design's actual flaw.

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

- One credential row per `(tenant, provider, shape)`.
- One Anthropic API key serves both `spring-voyage` (with Anthropic) and any future Anthropic-fronted runtime that consumes the api-key shape.
- One Anthropic OAuth token serves `claude-code`.
- The tenant install surface has one row per provider, not per runtime — the wizard groups credential entry by provider.

Live-model fetch is per provider (`/v1/models`, `/api/tags`, …) rather than per runtime, so the catalog is shared across runtimes that target the same provider.

### 7. Migration: clean-deploy hard rename, no shim

This ADR re-shapes:

- The C# domain: `IAgentRuntime` (no `Kind`, with `AllowedProviders`); new `IModelProvider`; new `Model` record; reshaped `AgentExecutionConfig`.
- Project layout: `Cvoya.Spring.AgentRuntimes.{Ollama,OpenAI}` removed; `Cvoya.Spring.AgentRuntimes.{Claude,Google}` rename to `{ClaudeCode,GeminiCli}`; `Cvoya.Spring.AgentRuntimes.SpringVoyage` exists as a separate runtime project (the launcher relocates from `Cvoya.Spring.Dapr/Execution/SpringVoyageAgentLauncher.cs`); a new `Cvoya.Spring.ModelProviders` project carries the generic `ModelProvider` plus adapter strategies.
- New file: `platform/model-providers.yaml`.
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

### 8. The Custom runtime declares its allow-list

A custom runtime, when added in a future release, must declare a non-empty closed set of allowed providers in its launcher. There is no sentinel "any" value. The platform validates user input against this set at submit time.

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

- Per-tenant overrides of the shipped `model-providers.yaml`. v0.1 ships the file as platform-level configuration; tenants add models via per-install model lists, not by adding new providers. A future ADR can introduce tenant-level provider extension if a real need surfaces.
- Fully dynamic provider discovery (a tenant pasting an OpenAI-compatible endpoint URL and the platform inferring the provider). Out of scope; v0.1 admits providers via the checked-in YAML.
- Multi-shape credentials per `(tenant, provider, runtime)`. The matrix's per-edge single-shape rule is intentional. If a future runtime genuinely accepts both shapes for the same provider, a follow-up ADR introduces a discriminator.

## Surface affected (delivery scope)

This is a multi-PR initiative. The tracker issue [#1761](https://github.com/cvoya-com/spring-voyage/issues/1761) breaks the work into per-area PRs sequenced by `blocked-by`:

- **Core domain.** `IAgentRuntime` reshape; `IModelProvider` introduction; `Kind` removal; `AgentExecutionConfig` shape change; `Model` record. Lands first; everything else depends on it.
- **Provider config + adapters.** `platform/model-providers.yaml`; `IModelProviderAdapter`; `OpenAiCompatibleAdapter`, `AnthropicAdapter`, `GoogleAdapter`.
- **Project re-layout.** `AgentRuntimes.{Ollama,OpenAI}` removed; `AgentRuntimes.{Claude,Google}` → `{ClaudeCode,GeminiCli}`; `AgentRuntimes.SpringVoyage` extracted from `Cvoya.Spring.Dapr`; `ModelProviders` project added.
- **Manifest + parser.** `ai.runtime`, `ai.model{provider,id}`; legacy errors per the table above.
- **Web API / OpenAPI / Kiota.** DTO restructure; `openapi.json` regen; `openapi-typescript` regen; Kiota regen.
- **CLI.** `--runtime`, `--model-provider`, `--model`; help text; legacy `--agent` rejection.
- **Web portal.** Unit-create wizard, agent-create wizard, execution tab, execution panel, model selector, credential entry, tenant install screens. Credential entry moves from per-runtime to per-provider; `Provider` axis disappears for fixed-provider runtimes.
- **Dapr components.** Rename files `conversation-*` → `llm-*`; update `metadata.name`.
- **Docs.** `docs/architecture/agent-runtime.md` refresh; `docs/architecture/identifiers.md` add the (runtime, model) shape; `docs/concepts/packages.md` AI block; `docs/glossary.md` add ModelProvider, refine AgentRuntime, retire Kind.
- **Tests.** Every layer.

The tracker issue carries the per-area umbrella sub-issues with `blocked-by` wiring; the Core-domain umbrella blocks every other area.
