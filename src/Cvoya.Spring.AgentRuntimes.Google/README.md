# Cvoya.Spring.AgentRuntimes.Google

Google AI agent runtime plugin for Spring Voyage. Implements
[`IAgentRuntime`](../Cvoya.Spring.Core/AgentRuntimes/IAgentRuntime.cs) and
binds the in-process [`dapr-agent`](../Cvoya.Spring.Dapr/Execution/DaprAgentLauncher.cs)
execution tool to the Google AI (Generative Language) API.

## What this package ships

| Surface | Value |
|---------|-------|
| `IAgentRuntime.Id` | `google` |
| `IAgentRuntime.DisplayName` | `Google AI (dapr-agent + Google AI API)` |
| `IAgentRuntime.ToolKind` | `dapr-agent` |
| `CredentialSchema.Kind` | `ApiKey` |
| In-container probe | `curl -sS "https://generativelanguage.googleapis.com/v1beta/models?key=…"` (via `GetProbeSteps`) |
| Seed catalogue | [`agent-runtimes/google/seed.json`](agent-runtimes/google/seed.json) |

The seed file lists the curated default model ids
(`gemini-2.5-pro`, `gemini-2.5-flash`) and the runtime's default base URL.
Tenants that need to extend or override the catalogue do so through
per-tenant install configuration (Phase 3 of the [#674](https://github.com/cvoya-com/spring-voyage/issues/674)
refactor) — this package only declares the out-of-the-box defaults.

## Installation

Add the project reference and call the DI extension on host startup:

```csharp
using Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    .AddCvoyaSpringAgentRuntimeGoogle();
```

The extension uses `TryAddEnumerable` on `IAgentRuntime`, so a downstream
host (e.g. the private cloud repo) can register a replacement runtime
before this call without being silently shadowed by the default.

## In-container probe plan

`GetProbeSteps` returns an ordered plan the `UnitValidationWorkflow`
executes inside the unit's container image:

- `VerifyingTool` — `curl --version`, confirming the image ships a
  runnable HTTP client (see
  [runtime-image contract](../../docs/guide/operator/agent-runtimes.md#runtime-image-contract)).
- `ValidatingCredential` — `GET /v1beta/models?key=<credential>` against
  the configured base URL. Exit 0 with a 2xx status → success;
  401/403 → `CredentialInvalid`; other 4xx → `CredentialInvalid`;
  5xx / transport failure → `NetworkError` (transient — the workflow's
  caller may retry via `POST /units/{name}/revalidate`).
- `ResolvingModel` — a second `curl` probe against `/v1beta/models/<id>`
  surfacing `ModelNotFound` when the provider does not list the model.

The interpreters never bubble the raw credential into
`UnitValidationError.Details`.

## Seed file format

See [`src/Cvoya.Spring.Core/AgentRuntimes/README.md`](../Cvoya.Spring.Core/AgentRuntimes/README.md)
for the canonical schema. The runtime fails fast at first use if the
seed file is missing, malformed, or declares a `defaultModel` that does
not appear in `models`.

## Scope note

The wizard and CLI consume the runtime directly via
`IAgentRuntimeRegistry` — there is no hardcoded Google path left in the
Dapr layer.
