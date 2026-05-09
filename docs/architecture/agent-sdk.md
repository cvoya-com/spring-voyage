# Agent SDK

`Cvoya.Spring.AgentSdk` is the thin HTTP client package that agent processes use to call back into the Spring Voyage dispatcher. It lives at `src/Cvoya.Spring.AgentSdk/`, inherits the solution target framework from `Directory.Build.props`, and has no Dapr, EF, API-host, or worker-host dependency. Runtime image authors reference it from their image project when they need workflow-style orchestration callbacks.

## Environment-Variable Contract

The dispatcher injects these variables into launched agent processes:

| Variable | Description |
| --- | --- |
| `SPRING_CALLBACK_URL` | Dispatcher base URL. The SDK constructs endpoint paths relative to this. |
| `SPRING_CALLBACK_TOKEN` | Per-invocation bearer token. The SDK sends it as `Authorization: Bearer <token>`. |

`SpringAgent.FromEnvironment()` reads those two variables and throws `MissingCallbackEnvironmentException` when either is absent. The launcher also injects `SPRING_THREAD_ID` for message-bound invocations; callers pass that thread id into SDK methods.

Persistent containers are launched once and receive multiple A2A turns. Their launch-time `SPRING_CALLBACK_TOKEN` is valid only for the first turn, so the dispatcher also includes a fresh per-turn token in the inbound A2A message metadata as `callbackToken`. Runtime images that receive the raw inbound message can call `SpringAgent.FromEnvironment(inboundMessageBody)` or `SpringAgent.FromEnvironment(JsonElement)`; the SDK prefers that per-message `callbackToken` when present and falls back to the launch-time env var for ephemeral agents and first-message bootstrap.

## Authorization Model

The orchestration SDK is structurally callable only by unit processes: processes launched as the runtime for a unit. Leaf agents can reply through their normal runtime output path, and A2A messaging remains available through the existing A2A protocol, but leaf agents are rejected when they call orchestration methods.

Dispatcher authorization gates:

| Gate | Rejection reason |
| --- | --- |
| Invalid or expired callback token | `InvalidToken` |
| Leaf-agent caller | `CallerIsNotUnit` |
| Target is not a direct child | `TargetNotChild` |
| Target equals the calling unit | `SelfDelegation` |
| Delegation depth budget is exhausted | `DepthExceeded` |
| Target crosses the tenant boundary | `CrossTenant` |

Targets must be direct children of the calling unit. Cross-level delegation is not supported in v0.1. The token is scoped to the current tenant, caller, thread, and inbound message. For persistent containers, use the per-message `callbackToken` from the inbound A2A metadata for the current turn and discard it when the turn completes.

## Typed Client Surface

Create the client from the injected environment:

```csharp
IOrchestrationClient client = SpringAgent.FromEnvironment();
```

Or construct it directly:

```csharp
IOrchestrationClient client = new OrchestrationClient(baseUrl, callbackToken);
```

Post a final result to the dispatcher thread:

```csharp
await client.PostResultAsync(threadId, result);
```

Delegate to a direct child:

```csharp
DelegateResponse response = await client.DelegateAsync(
    threadId,
    targetChildAddress,
    prompt);
```

Fan out to multiple direct children:

```csharp
FanoutResponse response = await client.FanoutAsync(
    threadId,
    new[] { childAddress1, childAddress2 },
    prompt);
```

Child targets are Spring Voyage address strings such as `agent:aaaaaaaa000000000000000000000001` or `unit:bbbbbbbb000000000000000000000001`. For unit children, a bare Guid is also accepted and normalized to a `unit:` address.

## Error Model

| Exception | When thrown |
| --- | --- |
| `MissingCallbackEnvironmentException` | `SPRING_CALLBACK_URL` or `SPRING_CALLBACK_TOKEN` is absent when calling `SpringAgent.FromEnvironment()`. |
| `OrchestrationAuthException` | The dispatcher rejects authentication or orchestration authorization. `Reason` carries values such as `InvalidToken`, `CallerIsNotUnit`, `TargetNotChild`, `SelfDelegation`, `DepthExceeded`, or `CrossTenant`. |
| `OrchestrationTransportException` | HTTP transport failures, timeouts, invalid dispatcher JSON, or non-success responses outside the authorization model. |

## Workflow-State Guidance

The SDK does not provide workflow durability. If an image needs restart survival or multi-step state, put a state store such as SQLite or Redis in the image or alongside it as a sidecar. The platform delivers the inbound message, validates callbacks, records orchestration evidence, and collects the runtime's final result.

## Security Model

- The callback token is per-invocation and scoped to the current thread.
- Do not cache or reuse the token across invocations.
- The token is tenant-scoped and cannot authenticate callbacks for another tenant.
- Token rotation during an invocation is not supported in v0.1.

## Sample Image Walkthrough

See [`samples/workflow-agent-image/README.md`](../../samples/workflow-agent-image/README.md) for a minimal .NET workflow image that uses `SpringAgent.FromEnvironment()`, chooses a child deterministically, calls `DelegateAsync`, and writes the delegated result to stdout.
