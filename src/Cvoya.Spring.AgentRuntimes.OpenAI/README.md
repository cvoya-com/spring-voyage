# Cvoya.Spring.AgentRuntimes.OpenAI

`IAgentRuntime` plugin for the OpenAI Platform API + the in-process
`dapr-agent` execution tool. Registered with DI via
`AddCvoyaSpringAgentRuntimeOpenAI()`; the host's `IAgentRuntimeRegistry`
picks it up automatically and exposes it under id `openai`.

## Identity

| Field         | Value                                       |
|---------------|---------------------------------------------|
| `Id`          | `openai`                                    |
| `DisplayName` | `OpenAI (dapr-agent + OpenAI API)`          |
| `ToolKind`    | `dapr-agent`                                |

## Credential format

The runtime expects a single OpenAI Platform API key
(`AgentRuntimeCredentialKind.ApiKey`). Keys typically start with `sk-` and
are issued at <https://platform.openai.com/api-keys>.

`ValidateCredentialAsync` issues `GET /v1/models` against
`https://api.openai.com` (or the `baseUrl` declared in the seed file) with
the supplied key in an `Authorization: Bearer` header:

| Outcome                | Status                                 |
|------------------------|----------------------------------------|
| HTTP 2xx               | `Valid`                                |
| HTTP 4xx (any 4xx)     | `Invalid` (response body surfaced)     |
| HTTP 5xx               | `NetworkError` (transient — retryable) |
| Network/DNS/timeout    | `NetworkError`                         |
| Empty / whitespace key | `Invalid` ("Supply an OpenAI API key…")|

The runtime never throws on transport-level failures; everything is
returned as a `CredentialValidationResult`.

## Model catalog

`DefaultModels` is loaded from
[`agent-runtimes/openai/seed.json`](agent-runtimes/openai/seed.json)
shipped alongside the assembly:

```json
{
  "models": ["gpt-4o", "gpt-4o-mini", "o3-mini"],
  "defaultModel": "gpt-4o",
  "baseUrl": "https://api.openai.com"
}
```

To add or remove a model, edit the seed file and ship a new build of this
project. Tenants may further override or extend this list at install time —
that path is owned by the install service, not the runtime.

## Container baseline

`VerifyContainerBaselineAsync` confirms that the runtime's tool dependency
(`dapr-agent`, implemented in `Cvoya.Spring.Dapr.Execution.DaprAgentLauncher`)
can be dispatched in the current process. Concretely it checks that the
`Dapr.Actors` assembly is loaded into the host — the marker for a fully
wired Dapr stack. Network reachability to `api.openai.com` and Dapr
sidecar health are host-wide concerns surfaced by other probes
(startup-configuration report, Dapr health endpoints).

## Wiring

```csharp
using Cvoya.Spring.AgentRuntimes.OpenAI.DependencyInjection;

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    // … other AddCvoyaSpring* calls …
    .AddCvoyaSpringAgentRuntimeOpenAI();
```

Registration uses `TryAddEnumerable` so a downstream host (private cloud
repo) can replace the default `OpenAiAgentRuntime` with a tenant-aware
implementation without forking this project.

## Scope

This project is a Phase-2 sub-issue of the
[#674](https://github.com/cvoya-com/spring-voyage/issues/674) refactor
(landed via [#680](https://github.com/cvoya-com/spring-voyage/issues/680)).
The wizard and CLI consume the runtime directly via
`IAgentRuntimeRegistry` — there is no hardcoded OpenAI path left in the
Dapr layer.
