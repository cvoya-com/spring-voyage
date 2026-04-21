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

`GetProbeSteps` emits a `ValidatingCredential` step that the
`UnitValidationWorkflow` dispatches inside the unit container: a
`curl -sS -H "Authorization: Bearer <key>" <baseUrl>/v1/models` call
against `https://api.openai.com` (or the `baseUrl` declared in the
seed / install config). The step's `InterpretOutput` maps the exit
code / HTTP status onto `UnitValidationError`:

| Outcome                | Error code                           |
|------------------------|--------------------------------------|
| HTTP 2xx               | success                              |
| HTTP 401 / 403         | `CredentialInvalid`                  |
| HTTP 4xx (other)       | `CredentialInvalid`                  |
| HTTP 5xx               | `NetworkError` (transient)           |
| Network / DNS / timeout| `NetworkError`                       |
| Empty / whitespace key | `CredentialInvalid`                  |

Everything is surfaced as a `UnitValidationError` on the unit's
persisted `LastValidationError`; the interpreter never bubbles raw
credential bytes into the error message.

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

## Runtime-image contract

The OpenAI runtime's probe plan shells out to `curl` against
`api.openai.com`. Every container image used to host a unit bound to this
runtime MUST include a runnable HTTP client on `PATH` — typically
`curl`. The `VerifyingTool` step fails fast with
`UnitValidationCodes.ToolUnavailable` when the image lacks this
dependency, so operators hit a precise error instead of a
credential-validation failure downstream. See the runtime-image
contract in
[`docs/guide/operator/agent-runtimes.md`](../../docs/guide/operator/agent-runtimes.md#runtime-image-contract).

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
