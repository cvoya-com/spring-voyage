# Cvoya.Spring.AgentRuntimes.Ollama

`IAgentRuntime` plug-in for the local **Ollama** LLM endpoint, executed
through the **`dapr-agent`** tool kind. Targets developer laptops and
air-gapped deployments where the LLM is hosted on the host machine (or a
sidecar container) and reached without authentication.

## Identity

| Field | Value |
|-------|-------|
| `Id` | `ollama` |
| `DisplayName` | `Ollama (dapr-agent + local Ollama)` |
| `ToolKind` | `dapr-agent` |
| `CredentialSchema.Kind` | `None` |

The `Id` is persisted on tenant installs and unit bindings — treat it as
immutable.

## Configuration

The runtime binds `OllamaAgentRuntimeOptions` to the
`AgentRuntimes:Ollama` configuration section. In multi-tenant deployments,
the install record's `config_json` payload carries the same fields and the
host materialises a per-tenant `IOptions<OllamaAgentRuntimeOptions>`
overlay.

| Field | Default | Purpose |
|-------|---------|---------|
| `BaseUrl` | `http://spring-ollama:11434` | URL of the Ollama server. macOS hosts running Ollama natively for GPU passthrough should override this to `http://host.containers.internal:11434`. |
| `HealthCheckTimeoutSeconds` | `5` | Cap applied to the `/api/tags` reachability probe dispatched by the `UnitValidationWorkflow`'s `VerifyingTool` step. |

Example `appsettings.json` snippet:

```json
{
  "AgentRuntimes": {
    "Ollama": {
      "BaseUrl": "http://host.containers.internal:11434"
    }
  }
}
```

## Wiring

```csharp
builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    .AddCvoyaSpringAgentRuntimeOllama(builder.Configuration);
```

The extension uses `TryAdd*` and guards against double-registration so it
is safe to call from composite hosts. The default `IAgentRuntimeRegistry`
(in `Cvoya.Spring.Dapr`) picks the runtime up automatically.

## Seed catalog

The runtime ships its model catalog as an embedded resource at
`agent-runtimes/ollama/seed.json`:

```json
{
  "models": [
    "qwen2.5:14b",
    "llama3.2:3b",
    "llama3.1:8b",
    "mistral:7b",
    "deepseek-coder-v2:16b"
  ],
  "defaultModel": "llama3.2:3b",
  "baseUrl": "http://spring-ollama:11434"
}
```

`OllamaAgentRuntime.DefaultModels` returns one `ModelDescriptor` per entry.
Tenants may extend the list per-install — this property is the
out-of-the-box default only. The schema is documented in
[`src/Cvoya.Spring.Core/AgentRuntimes/README.md`](../Cvoya.Spring.Core/AgentRuntimes/README.md#seed-file-schema).

## In-container probe plan

`OllamaAgentRuntime.GetProbeSteps` returns an ordered plan the
`UnitValidationWorkflow` dispatches inside the unit's container:

- `VerifyingTool` — `curl -sS -m <timeout> <BaseUrl>/api/tags` checks
  reachability of the Ollama server from inside the chosen image. Any
  non-2xx status (or transport failure) is reported as a
  `UnitValidationError` with code `ToolUnavailable`.
- `ResolvingModel` — `curl` against `/api/show` resolves the requested
  model id and surfaces `ModelNotFound` when the provider does not
  publish it.

No `ValidatingCredential` step is emitted because Ollama has no secret
concept. Runtime images that lack `curl` fail the probe at
`VerifyingTool` with a clear "missing runnable HTTP client" error — see
the runtime-image contract in
[`docs/guide/operator/agent-runtimes.md`](../../docs/guide/operator/agent-runtimes.md#runtime-image-contract).

## Local-Ollama setup

See [`docs/developer/local-ai-ollama.md`](../../docs/developer/local-ai-ollama.md)
for the canonical macOS / Linux setup guide. Quick summary:

1. Install Ollama (`brew install ollama` on macOS).
2. Start the server (`ollama serve`).
3. Pre-pull the models in the seed catalog (`ollama pull llama3.2:3b`,
   etc.) so the first request does not block on a download.
4. Point the runtime at the host (`AgentRuntimes:Ollama:BaseUrl =
   http://host.containers.internal:11434` for podman on macOS).

## Extension points

| Hook | How to extend |
|------|----------------|
| `OllamaAgentRuntime.ProbeTagsEndpointAsync` | `protected virtual` — override to add custom auth headers for a reverse-proxied Ollama install. |
| Registration | The extension uses `TryAdd*` for the strongly-typed `OllamaAgentRuntime` — pre-register a subclass before calling `AddCvoyaSpringAgentRuntimeOllama` to substitute behaviour. |
| Per-tenant config | Materialise a tenant-scoped `IOptions<OllamaAgentRuntimeOptions>` in the cloud host's request scope; the runtime resolves it via DI per call. |
