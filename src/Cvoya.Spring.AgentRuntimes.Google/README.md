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
| Validation endpoint | `GET https://generativelanguage.googleapis.com/v1beta/models?key=…` |
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

## Credential validation

`ValidateCredentialAsync` issues a single read-only request against
`/v1beta/models` using the supplied API key. The result mapping follows
the [contract on the interface](../Cvoya.Spring.Core/AgentRuntimes/IAgentRuntime.cs):

| Outcome | Status |
|---------|--------|
| HTTP 2xx | `Valid` |
| HTTP 4xx (401, 403, 400, …) | `Invalid` (with the body excerpt in `ErrorMessage`) |
| HTTP 5xx | `NetworkError` (transient — caller may retry) |
| Transport failure (DNS / TLS / timeout) | `NetworkError` |
| Empty / whitespace credential | `Invalid` |

The validator never throws for transport failures — they always surface as
`NetworkError`. Cancellation propagates as expected.

## Container baseline

`VerifyContainerBaselineAsync` reports `Passed = true` when the
`Dapr.Actors` assembly is loaded into the host process — that is the
runtime's only host-side dependency. Outbound HTTPS reachability to
`generativelanguage.googleapis.com` and the Dapr sidecar's health are
covered by the host-wide startup configuration report and Dapr's own
health probes; duplicating those checks here would lie about scope.

## Seed file format

See [`src/Cvoya.Spring.Core/AgentRuntimes/README.md`](../Cvoya.Spring.Core/AgentRuntimes/README.md)
for the canonical schema. The runtime fails fast at first use if the
seed file is missing, malformed, or declares a `defaultModel` that does
not appear in `models`.

## Scope note

This package coexists with the hardcoded Google paths in
`Cvoya.Spring.Dapr.Execution.ProviderCredentialValidator` and
`ModelCatalog.StaticFallback` until the Phase 3 wizard issue removes
them; both paths produce the same model list and the same validation
behaviour.
