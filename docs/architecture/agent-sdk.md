# Agent SDK

> **See also:** [`orchestration-tools.md`](orchestration-tools.md) — companion doc covering the tool-call surface for LLM-driven runtimes. Both surfaces dispatch to the same platform-side handlers and emit the same `OrchestrationDecision` events; the choice between them is the runtime image author's.

`Cvoya.Spring.AgentSdk` is the thin HTTP client package that agent processes use to call back into the Spring Voyage dispatcher. It lives at `src/Cvoya.Spring.AgentSdk/`, inherits the solution target framework from `Directory.Build.props`, and has no Dapr, EF, API-host, or worker-host dependency. Runtime image authors reference it from their image project when they need workflow-style orchestration callbacks.

## Environment-Variable Contract

The dispatcher injects these variables into launched agent processes:

| Variable | Description |
| --- | --- |
| `SPRING_CALLBACK_URL` | Dispatcher base URL. The SDK constructs endpoint paths relative to this. |
| `SPRING_CALLBACK_TOKEN` | Per-invocation bearer token. The SDK sends it as `Authorization: Bearer <token>`. |

`SpringAgent.FromEnvironment()` reads those two variables and throws `MissingCallbackEnvironmentException` when either is absent. The launcher also injects `SPRING_THREAD_ID` for message-bound invocations; callers pass that thread id into SDK methods.

Persistent containers are launched once and receive multiple A2A turns. Their launch-time `SPRING_CALLBACK_TOKEN` is valid only for the first turn, so the dispatcher also includes a fresh per-turn token in the inbound A2A message metadata at `message.metadata.callbackToken`. Runtime images that receive the raw A2A `message/send` params can call `SpringAgent.FromEnvironment(inboundMessageBody)` or `SpringAgent.FromEnvironment(JsonElement)`; the SDK reads only `message.metadata.callbackToken`, preferring it when present and falling back to the launch-time env var only when no per-message token is present.

## Authorization Model

The platform does not gate orchestration by entity type; calls succeed whenever the caller's direct-child membership permits. Agents and units both reach the dispatcher under their own address scheme. The dispatcher rejects callers whose scheme is neither `unit://` nor `agent://` (e.g. `human://`, `connector://`) with `UnsupportedCallerScheme`.

A2A messaging remains available to every addressable entity through the existing A2A protocol, separately from this SDK.

Dispatcher authorization gates:

| Gate | Rejection reason |
| --- | --- |
| Invalid or expired callback token | `InvalidToken` |
| Caller scheme is not `unit://` or `agent://` | `UnsupportedCallerScheme` |
| Target is not a direct child | `TargetNotChild` |
| Target equals the caller | `SelfDelegation` |
| Delegation depth budget is exhausted | `DepthExceeded` |
| Target crosses the tenant boundary | `CrossTenant` |

Targets must be direct children of the caller. Cross-level delegation is not supported in v0.1. The token is scoped to the current tenant, caller, thread, and inbound message. For persistent containers, use the per-message `message.metadata.callbackToken` from the inbound A2A metadata for the current turn and discard it when the turn completes.

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
| `OrchestrationAuthException` | The dispatcher rejects authentication or orchestration authorization. `Reason` carries values such as `InvalidToken`, `UnsupportedCallerScheme`, `TargetNotChild`, `SelfDelegation`, `DepthExceeded`, or `CrossTenant`. |
| `OrchestrationTransportException` | HTTP transport failures, timeouts, invalid dispatcher JSON, or non-success responses outside the authorization model. |

## Workflow-State Guidance

The SDK does not provide workflow durability. If an image needs restart survival or multi-step state, put a state store such as SQLite or Redis in the image or alongside it as a sidecar. The platform delivers the inbound message, validates callbacks, records orchestration evidence, and collects the runtime's final result.

## Per-Thread State

This section governs **per-thread** state held by an agent process — both on disk and in memory — and applies to the Python SDK (`agents/spring-voyage-agent-sdk/`) and to runtime images that hold their own per-thread state. The contract derives from [ADR-0041 — Actor-runtime contract](../decisions/0041-actor-runtime-contract.md).

> **Operational summary with safe-vs-unsafe examples** (shared with CLI-runtime authors): [Agent Runtime § "concurrent_threads: true author contract"](agent-runtime.md#10-concurrent_threads-true-author-contract). The current section is the SDK-flavoured guidance; the linked section is the canonical contract narrative.

### `thread.id` is delivered on every turn

Every inbound message carries the platform-assigned thread id. In the Python SDK it is `Message.thread_id` (sourced from the A2A SDK's `Message.context_id`). The id is stable across the thread's lifetime and globally unique (a `Guid`, per ADR-0036), so it can be used directly as a session / file-key without hashing.

### On-disk per-thread state

On-disk per-thread state lives under `$SPRING_WORKSPACE_PATH/threads/<thread.id>/`. The Python SDK exposes `IAgentContext.thread_workspace(thread_id) -> Path` which returns this directory and creates it on first access (`mkdir(parents=True, exist_ok=True)`):

```python
async def on_message(message: Message):
    workspace = context.thread_workspace(message.thread_id)
    transcript = workspace / "transcript.jsonl"
    transcript.write_text(...)  # safe: directory exists
```

This is the canonical location for thread-local files (transcripts, scratchpads, vector caches, partial work). Anything written here survives container restarts (the workspace is a durable volume — see `docs/specs/agent-runtime-boundary.md` § 3) and is scoped to one thread, so it cannot collide between concurrent thread invocations.

Authors **MUST NOT** write thread-local state outside this directory — the [ADR-0041 author contract](../decisions/0041-actor-runtime-contract.md) for `concurrent_threads: true` makes this normative for opt-in agents, and the convention applies to the default mode as well so that switching the flag never silently corrupts state.

### In-memory per-thread state

Whether in-memory per-thread state is safe depends on the agent's `concurrent_threads` setting:

- **`concurrent_threads: false`** (default) — the platform-side mailbox serialises invocations across all threads inside the container; only one `on_message` runs at a time. In-memory state keyed by `thread.id` (e.g. a `dict[str, ThreadState]`) is safe by construction. Trade-off: head-of-line blocking — a long turn on thread A queues thread B inside the same agent.

- **`concurrent_threads: true`** (opt-in) — the platform dispatches per-thread channels concurrently; N `on_message` invocations may run in parallel. In-memory per-thread state is allowed but the agent author signs up for the full [ADR-0041 `concurrent_threads: true` contract](../decisions/0041-actor-runtime-contract.md): no fixed ports, no shared global mutation, no `pkill`-style child-process assumptions, and all thread-local files under `$SPRING_WORKSPACE_PATH/threads/<thread.id>/`. Agents that cannot meet the contract should stay on `false` and accept HoL.

For Python SDK agents, the contract surfaces in code as additional re-entrancy
discipline:

- Avoid module-level `os.chdir()`, signal-handler installation, and env-var
  mutation inside `on_message`. These are process-global and will surprise
  every other concurrent turn.
- Module-level dicts keyed by `thread_id` are safe only when every read / write
  path is re-entrant — the SDK invokes `on_message` from many concurrent tasks
  under `concurrent_threads: true`.
- The CLI-runtime system-prompt guard fragment (see [Agent Runtime § 10](agent-runtime.md#10-concurrent_threads-true-author-contract)) does not apply to the SDK — the SDK author writes the code directly and is responsible for honouring the contract in code.

`spring agent validate <agent-id>` surfaces a `WARN` line when an agent declares `concurrent_threads: true` so the author opts in deliberately and sees the contract.

## Security Model

- The callback token is per-invocation and scoped to the current thread.
- Do not cache or reuse the token across invocations.
- The token is tenant-scoped and cannot authenticate callbacks for another tenant.
- Token rotation during an invocation is not supported in v0.1.

## Sample Image Walkthrough

See [`samples/workflow-agent-image/README.md`](../../samples/workflow-agent-image/README.md) for a minimal .NET workflow image that uses `SpringAgent.FromEnvironment()`, chooses a child deterministically, calls `DelegateAsync`, and writes the delegated result to stdout.
