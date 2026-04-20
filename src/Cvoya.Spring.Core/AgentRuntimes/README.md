# Agent Runtime Plugin Contract

`IAgentRuntime` is the plugin shape that turns an execution tool + LLM
backend + credential schema + supported model catalog into a single
pluggable unit. The host's API layer, CLI, and wizard consume the
contract via DI; no core code imports a concrete runtime package.

This folder defines the contract only. Per-runtime implementations live in
sibling `Cvoya.Spring.AgentRuntimes.<Name>` projects (landing in
sibling issues #679–#682 of the #674 refactor) and the default registry
impl lives in `Cvoya.Spring.Dapr/AgentRuntimes/`.

## Contract surface

| Type | Purpose |
|------|---------|
| `IAgentRuntime` | The runtime itself: `Id`, `DisplayName`, `ToolKind`, `CredentialSchema`, `ValidateCredentialAsync`, `DefaultModels`, `VerifyContainerBaselineAsync`, `FetchLiveModelsAsync`. |
| `IAgentRuntimeRegistry` | Singleton enumeration + case-insensitive `Get(id)` lookup over every DI-registered runtime. |
| `AgentRuntimeCredentialSchema` | Record describing the expected credential shape (kind + optional display hint). |
| `AgentRuntimeCredentialKind` | `None` / `ApiKey` / `OAuthToken`. |
| `CredentialValidationResult` | Outcome record: `Valid`, `ErrorMessage`, `Status`. |
| `CredentialValidationStatus` | `Unknown` / `Valid` / `Invalid` / `NetworkError`. |
| `ModelDescriptor` | One entry in a runtime's catalog: `Id`, `DisplayName`, `ContextWindow`. |
| `ContainerBaselineCheckResult` | Outcome of the container-baseline probe: `Passed`, `Errors`. |
| `FetchLiveModelsResult` | Outcome of `FetchLiveModelsAsync`: `Status`, `Models`, `ErrorMessage`. |
| `FetchLiveModelsStatus` | `Unknown` / `Success` / `InvalidCredential` / `NetworkError` / `Unsupported`. |

## Adding a new agent runtime

1. Create a new project `src/Cvoya.Spring.AgentRuntimes.<Name>/`.
   The project references `Cvoya.Spring.Core` only. It must not reference
   any other runtime project.
2. Implement `IAgentRuntime` for your runtime. Choose a stable, lowercase
   `Id` (e.g. `claude`, `openai`, `google`, `ollama`). The id is
   persisted in tenant installs and unit bindings — do not change it
   once shipped.
3. Add a seed catalog file at
   `agent-runtimes/<id>/seed.json`. See [Seed file schema](#seed-file-schema)
   below.
4. Ship a DI extension:

   ```csharp
   public static IServiceCollection AddCvoyaSpringAgentRuntime<Name>(
       this IServiceCollection services)
   {
       services.TryAddSingleton<IAgentRuntime, <Name>AgentRuntime>();
       // … any runtime-specific services
       return services;
   }
   ```

   Use `TryAdd*` so downstream hosts (e.g. the private cloud repo) can
   pre-register a replacement.
5. Add unit tests under
   `tests/Cvoya.Spring.AgentRuntimes.<Name>.Tests/`. Cover
   `ValidateCredentialAsync` (valid / invalid / network-error paths),
   `FetchLiveModelsAsync` (success / unsupported / network-error paths),
   seed deserialization round-trip, and
   `VerifyContainerBaselineAsync`.
6. Update any user-facing docs: `docs/guide/` for install/config,
   `docs/architecture/` if the runtime introduces a novel pattern.

## Seed file schema

Each runtime ships a seed catalog as JSON at
`agent-runtimes/<id>/seed.json`:

```json
{
  "models": ["model-id-1", "model-id-2"],
  "defaultModel": "model-id-1",
  "baseUrl": "https://api.example.com",
  "extras": { "any": "runtime-specific" }
}
```

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| `models` | `string[]` | yes | Seed list of model ids the runtime supports out of the box. |
| `defaultModel` | `string` | yes | The model id selected by default in the wizard when a tenant installs this runtime. Must appear in `models`. |
| `baseUrl` | `string?` | no | Default base URL for the runtime's API. Used when the backend is self-hostable (Ollama, OpenAI-compatible endpoints). |
| `extras` | `object?` | no | Runtime-specific config defaults. Opaque to the host. |

The contract in this folder does not load seed files itself — each
per-runtime project owns its own seed-loading logic. The schema is
defined here so consumers of the contract (tenant bootstrap, install
service) can deserialize seeds uniformly.

## Extension checklist

When you add or change an agent runtime, confirm:

- [ ] `Id` is stable, lowercase, and persisted-safe.
- [ ] `CredentialSchema.Kind` matches how the backend actually authenticates.
- [ ] `ValidateCredentialAsync` never throws — transport failures are reported as `CredentialValidationStatus.NetworkError`.
- [ ] `DefaultModels` mirrors the seed file exactly (no drift between
      source code defaults and the seed).
- [ ] `VerifyContainerBaselineAsync` checks every binary / network
      dependency the runtime needs at execution time.
- [ ] The DI extension uses `TryAdd*` so downstream hosts can override.
- [ ] Tests cover all three `ValidateCredentialAsync` outcomes plus the
      seed round-trip.
- [ ] User guide updated. CLI `--help` examples updated. No drift.

## Scope note

This contract is the cornerstone of phase 2 of the V2 refactor tracked
by #674. The existing hardcoded provider/validator/model-catalog code
(in `Cvoya.Spring.Dapr/Execution`) keeps working untouched while
per-runtime migrations (#679–#682) land. Once those ship, the hardcoded
paths are deleted.
