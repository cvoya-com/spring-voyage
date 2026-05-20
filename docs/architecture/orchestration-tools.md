# Orchestration Tools — Runtime-Image Author Contract

> **[Architecture Index](README.md)** | Source of truth: [ADR-0039 § 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime) | Sibling docs: [Agent SDK](agent-sdk.md), [Agent Runtime](agent-runtime.md)
>
> **Last reviewed:** 2026-05-09
>
> Companion to [`agent-sdk.md`](agent-sdk.md) covering the tool-call surface that LLM-driven runtimes consume. The SDK doc covers the typed HTTP callback surface for workflow-driven runtimes; this doc covers the per-runtime tool-call attachment surface (MCP for the CLI runtimes, env-var registry for `spring-voyage-agent`). Both surfaces dispatch to the same platform-side handlers and emit the same `OrchestrationDecision` events.

---

## Audience

This doc is for runtime-image authors building or extending an LLM-driven runtime image (`claude-code`, `codex`, `gemini`, `spring-voyage-agent`, or a new image). If you are building a workflow-driven runtime (Temporal, Dapr Workflows, custom state machines, plain code) consume the SDK surface in [`agent-sdk.md`](agent-sdk.md) instead — the contract there exposes the same tools as typed C# methods over HTTP.

Operators deploying agents do not need to read this doc. The orchestration surface is wired by the platform's launchers; nothing in this contract is operator-configurable.

---

## 1. The closed tool surface

ADR-0039 § 3 closes the orchestration surface to **five tools**. The names are wire-stable, snake_case, and fixed for v0.1. Adding a tool requires a new ADR.

| Tool name | Purpose | Returns | Side effect |
|---|---|---|---|
| `list_members` | Enumerate the caller's own direct members with their addresses, display names, kinds (`agent` / `unit`), and resolved execution config. Returns an empty array for leaf agents. | Array of member descriptors. | None. |
| `inspect` | Return metadata for any addressable target in the caller's tenant: role, description, declared expertise, current status. | Single descriptor. | None. |
| `delegate_to` | Forward the inbound message to the named target and await the target's response (synchronous within the turn budget). | The target's response message. | Records an `OrchestrationDecision` with `Kind=Delegate`. |
| `fanout_to` | Forward to multiple targets in parallel; collect responses with a per-target timeout. | Array of `(address, response, status)` triples. | Records an `OrchestrationDecision` with `Kind=Fanout`. |
| `query_status` | Cheap status check for a target without a full inspect. | `{ status, lastActivityAt, busyOnThread? }`. | None. |

**Closed enum.** The C# enum [`OrchestrationToolName`](../../src/Cvoya.Spring.Core/Orchestration/OrchestrationToolName.cs) maps each member to its wire name via `[JsonStringEnumMemberName(...)]`. Runtime-side dispatch must use exactly these spellings; the platform will not route an unknown tool.

**Rename rationale.** The earlier names (`list_children` / `inspect_child` / `delegate_to_child` / `fanout_to_children` / `query_child_status`) baked in the structural assumption that a caller targets only its own direct children. The 2026-05-19 ADR-0039 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)) removes that assumption: a caller may target any addressable entity in the same tenant — peer, sibling, parent, or member. `list_members` is the only tool whose scope is still "own direct members" (the directory does not provide a tenant-wide enumeration); the other four accept any address and do not consult the membership graph.

**Why the surface is closed.** Every runtime image in the catalogue implements the same tool set; widening it implicitly forces every image to keep up. The closed list also bounds the platform-side audit space — if a runtime calls a tool the platform does not enumerate, the platform has nowhere to put the evidence. A future ADR can extend the surface; runtime-image authors do not.

---

## 2. Descriptor shape

The platform hands each runtime its tool descriptors as `OrchestrationToolDescriptor[]`. Source: [`Cvoya.Spring.Core/Orchestration/OrchestrationToolDescriptor.cs`](../../src/Cvoya.Spring.Core/Orchestration/OrchestrationToolDescriptor.cs).

```csharp
public sealed record OrchestrationToolDescriptor(
    OrchestrationToolName Name,
    JsonElement InputSchema,
    JsonElement OutputSchema);
```

The schemas are JSON Schema draft 2020-12. They live as embedded resources under [`Cvoya.Spring.Dapr/Orchestration/Resources/`](../../src/Cvoya.Spring.Dapr/Orchestration/Resources/) and are loaded once at startup by [`DirectoryOrchestrationToolProvider`](../../src/Cvoya.Spring.Dapr/Orchestration/DirectoryOrchestrationToolProvider.cs).

### `list_members`

Input: `{}` (no arguments; `additionalProperties: false`).

Output:

```json
{
  "members": [
    {
      "address": "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
      "displayName": "string",
      "kind": "agent | unit",
      "executionConfig": { /* opaque; read inspect for typed access */ }
    }
  ]
}
```

`address`, `displayName`, and `kind` are required. Semantics: returns the caller's own direct members; an `agent://` caller (leaf) returns an empty array. The tool does not enumerate "every addressable entity" — that is the directory's job, exposed through other surfaces.

### `inspect`

Input:

```json
{ "address": "agent:8c5fab2a..." }
```

Output:

```json
{
  "address": "agent:8c5fab2a...",
  "displayName": "string",
  "kind": "agent | unit",
  "description": "string | null",
  "expertise": ["string"],
  "status": "string"
}
```

`address`, `displayName`, and `kind` are required; the rest are optional.

### `delegate_to`

Input:

```json
{
  "address": "agent:8c5fab2a...",
  "message": { /* runtime-defined payload, opaque to the platform */ },
  "reason": "string | null"
}
```

`address` and `message` are required; `reason` is optional and is recorded verbatim on the `OrchestrationDecision`. Runtimes that surface internal model reasoning **must redact it** before passing it as `reason` — the platform records what the runtime sends, and the field is operator-visible audit.

Output:

```json
{
  "address": "agent:8c5fab2a...",
  "response": { /* runtime-defined */ },
  "status": "accepted | routed | failed"
}
```

`status` maps directly to `OrchestrationDecisionStatus`.

### `fanout_to`

Input:

```json
{
  "addresses": ["agent:...", "unit:..."],
  "message": { /* runtime-defined */ },
  "reason": "string | null"
}
```

`addresses` (minItems: 1) and `message` are required.

Output:

```json
{
  "results": [
    { "address": "agent:...", "response": { ... }, "status": "accepted | routed | failed" }
  ]
}
```

### `query_status`

Input: `{ "address": "..." }`.

Output:

```json
{
  "status": "ready | busy | stopped | error | unknown",
  "lastActivityAt": "2026-05-09T12:34:56Z | null",
  "busyOnThread": "string | null"
}
```

Only `status` is required. The platform-side handler probes the child via the existing actor mailbox `StatusQuery` path and maps the response onto the closed enum (`ready` for an idle agent, `busy` for an agent on an active thread, `stopped` / `error` for the unit lifecycle equivalents, `unknown` when the probe fails or the actor is unreachable). `lastActivityAt` is currently emitted as `null` — the dispatcher process does not have the activity-event store wired and the schema explicitly tolerates `null` rather than fabricating a timestamp.

---

## 3. Per-runtime attachment mechanism

The launcher consults [`IOrchestrationToolProvider.GetOrchestrationTools(agent, threadId)`](../../src/Cvoya.Spring.Core/Orchestration/IOrchestrationToolProvider.cs) at launch time. The default platform implementation is [`DirectoryOrchestrationToolProvider`](../../src/Cvoya.Spring.Dapr/Orchestration/DirectoryOrchestrationToolProvider.cs) — it returns the closed five-tool descriptor array for every `agent://` and `unit://` address (other schemes get an empty array). Per the 2026-05-19 ADR-0039 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)) attachment is unconditional: entity type is not a gate, membership is not a gate. Orchestration tools are exposed for any caller whose scheme is an orchestration caller — the runtime's instructions decide whether to use them.

The descriptor array is carried into the launcher on `AgentLaunchContext.OrchestrationTools` ([`IAgentRuntimeLauncher.cs`](../../src/Cvoya.Spring.Core/Execution/IAgentRuntimeLauncher.cs)). Each launcher then attaches the descriptors using its runtime's native mechanism.

### `claude-code` — MCP server

Source: [`ClaudeCodeLauncher`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/ClaudeCodeLauncher.cs).

When `OrchestrationTools` is non-empty, the launcher adds a second MCP server entry to the workspace's `.mcp.json` alongside the standard `spring-voyage` MCP server:

```jsonc
{
  "mcpServers": {
    "spring-voyage": {
      "type": "http",
      "url": "<MCP endpoint>",
      "headers": { "Authorization": "Bearer <MCP token>" }
    },
    "spring-orchestration": {
      "type": "http",
      "url": "<SPRING_CALLBACK_URL>/v1/runtime/orchestration",
      "headers": { "Authorization": "Bearer <SPRING_CALLBACK_TOKEN>" }
    }
  }
}
```

The orchestration MCP URL is the dispatcher's base callback URL with the route prefix from [`AgentCallbackEnvironmentContract.OrchestrationRoutePrefix`](../../src/Cvoya.Spring.Core/Execution/AgentCallbackEnvironmentContract.cs) (`/v1/runtime/orchestration`). The runtime sees the orchestration tools as MCP `tools/list` entries on the second server; the names are the closed enum values.

### `codex` — MCP server

Source: [`CodexLauncher`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/CodexLauncher.cs).

Identical mechanism to `claude-code`. The launcher writes the same two-server `.mcp.json` (Codex consumes the same `mcpServers` shape) into the workspace alongside `AGENTS.md`. The orchestration server is only added when `OrchestrationTools` is non-empty.

### `gemini` — MCP server

Source: [`GeminiLauncher`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/GeminiLauncher.cs).

Gemini CLI reads MCP server config from `.gemini/settings.json`. The launcher writes:

```jsonc
{
  "mcpServers": {
    "spring-voyage": {
      "httpUrl": "<MCP endpoint>",
      "headers": { "Authorization": "Bearer <MCP token>" }
    },
    "spring-orchestration": {
      "httpUrl": "<SPRING_CALLBACK_URL>/v1/runtime/orchestration",
      "headers": { "Authorization": "Bearer <SPRING_CALLBACK_TOKEN>" },
      "includeTools": ["list_members", "inspect", "delegate_to", "fanout_to", "query_status"]
    }
  }
}
```

The `includeTools` array is computed by mapping each `OrchestrationToolDescriptor.Name` through the closed enum's wire-name projection — the launcher only lists the tools the platform actually attached. Gemini's settings use `httpUrl` (camelCase) rather than Claude/Codex's `url`, but the contract is the same.

### `spring-voyage-agent` — env-var-keyed registry

Source: [`SpringVoyageAgentLauncher`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/SpringVoyageAgentLauncher.cs).

The Spring Voyage agent (Python, runs `dapr-agents`) is A2A-native and does not consume MCP for its tool list. Instead, the launcher serializes the descriptor array as JSON into the `SPRING_ORCHESTRATION_TOOLS` env var:

```csharp
if (context.OrchestrationTools is { Length: > 0 })
{
    envVars[OrchestrationToolsContract.EnvVar] =
        System.Text.Json.JsonSerializer.Serialize(context.OrchestrationTools);
}
```

The contract constant lives in [`OrchestrationToolsContract`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/OrchestrationToolsContract.cs):

```csharp
internal static class OrchestrationToolsContract
{
    public const string EnvVar = "SPRING_ORCHESTRATION_TOOLS";
}
```

The runtime image reads `SPRING_ORCHESTRATION_TOOLS` at startup, deserializes the array, and registers each descriptor with whatever tool dispatch surface the in-container agent uses. Each tool's wire name (snake_case) routes back to the same dispatcher endpoints under `/v1/runtime/orchestration/<tool>` using the bearer token from `SPRING_CALLBACK_TOKEN`.

---

## 4. Tools attach unconditionally

Per the 2026-05-19 ADR-0039 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)) the launcher attaches the closed five-tool set for every `agent://` and `unit://` address — there is no membership-based attachment gate, no entity-type attachment gate, and no live "does this address have children right now?" check. Orchestration is not a separate message-sending mechanism with its own attachment policy; it is the same messaging surface every caller has.

The runtime's instructions decide whether to invoke any of the tools. A leaf agent (no members) calling `list_members` gets an empty array; calling `delegate_to` succeeds for any addressable target in the same tenant. The absence of dispatchable members is not signalled via tool absence — the runtime sees the empty array and proceeds accordingly.

---

## 5. Authorization model (server-side)

Runtime authors **do not enforce these gates** — the platform-side handler [`OrchestrationToolHandlers`](../../src/Cvoya.Spring.Dapr/Orchestration/OrchestrationToolHandlers.cs) applies them on every call regardless of which transport (MCP, env-var registry, or SDK callback) reached it. Runtime authors must **surface failures cleanly** so an agent's instructions can react.

The dispatcher applies these gates in order (per ADR-0039 § 3, as amended 2026-05-19, [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)):

| # | Gate | HTTP status | Dispatcher reject code |
|---|---|---|---|
| 1 | Token validation (signature, expiry, claim shape) | 401 | `InvalidToken` |
| 2 | Caller scheme is `unit://` or `agent://` (rejects `human://`, `connector://`, etc.) | 403 | `UnsupportedCallerScheme` |
| 3 | Self-delegation rejected (target equals caller) | 400 | `OrchestrationSelfDelegation` |
| 4 | Per-thread orchestration depth budget (default 8) | 429 | `OrchestrationDepthExceeded` |
| 5 | Cross-tenant containment | 403 | `OrchestrationCrossTenant` |

Entity type (agent vs unit) is **not** a gate; the platform makes no assumption about who can orchestrate. Membership is **not** a gate; a caller may target any addressable entity in the same tenant. The previous "target is a direct child" gate (and its `OrchestrationTargetNotChild` reject code) was removed in [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536) — orchestration is messaging, and messaging is not gated on the membership graph.

Cross-link: the same model applies to the SDK transport — see [`agent-sdk.md` § Authorization Model](agent-sdk.md#authorization-model). The single source of truth is [ADR-0039 § 3](../decisions/0039-units-are-agents.md#authorization-rules) (see the amendment at the top of the ADR for the entity-type-gating removal).

**What runtime authors must do.** When the runtime's tool-call surface receives a non-success response from the platform, surface the reject code to the agent's instructions. For MCP-based runtimes this is the standard MCP error envelope; for the env-var registry runtime it is the dispatcher's JSON error body. The agent's instructions can then decide whether to retry, fall back to a different child, or answer directly.

---

## 6. `OrchestrationDecision` events

Three of the five tools are read-only probes and do **not** emit decisions:

- `list_members` — no event.
- `inspect` — no event.
- `query_status` — no event.

Two tools emit `OrchestrationDecision`:

- `delegate_to` — `Kind=Delegate`, `Targets` contains the single target address. Status is `Routed` on success, `Failed` on dispatch error.
- `fanout_to` — `Kind=Fanout`, `Targets` contains every requested target. Status is `Routed` if every target responded, `Failed` if any failed.

Both events carry `Reason` (the runtime-supplied tool-call argument, verbatim) and `ResultMessageIds` (the ids of the response messages, in target order). Decisions land on the activity stream as `Activity_OrchestrationDecision` rows. See [`agent-runtime.md` § 4d](agent-runtime.md#4d-orchestrationdecision-event-shape) for the normalized event JSON shape and [ADR-0039 § 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence) for the design rationale.

The `OrchestrationDecisionKind` enum also defines `Inspect` and `NoOp` values; v0.1 handlers do not currently emit them, but they are reserved for future read-tool audit and explicit non-delegation evidence.

---

## 7. Sample wiring per runtime

These snippets show what the runtime image sees at startup. Code shapes are derived by inspecting the launchers; treat them as illustrative shape for image authors. The launcher source is the canonical wiring — verify against [`Cvoya.Spring.AgentRuntimes/Launchers/`](../../src/Cvoya.Spring.AgentRuntimes/Launchers/) before writing image code.

### MCP-driven example — `claude-code`

The launcher materialises a workspace at `/workspace/` containing `CLAUDE.md` (system prompt) and `.mcp.json`:

```jsonc
// /workspace/.mcp.json — what claude-code reads at startup
{
  "mcpServers": {
    "spring-voyage": {
      "type": "http",
      "url": "http://host.docker.internal:5040/mcp",
      "headers": { "Authorization": "Bearer <MCP token>" }
    },
    "spring-orchestration": {
      "type": "http",
      "url": "http://host.docker.internal:5050/v1/runtime/orchestration",
      "headers": { "Authorization": "Bearer <SPRING_CALLBACK_TOKEN>" }
    }
  }
}
```

The runtime calls `tools/list` on the `spring-orchestration` server and receives five entries (`list_members`, `inspect`, `delegate_to`, `fanout_to`, `query_status`) with the JSON Schemas from § 2. A `tools/call` on `delegate_to` is routed to `POST /v1/runtime/orchestration/delegate-to` with the bearer token; the server handles auth, dispatches the message to the target, and returns the response.

### Env-var registry example — `spring-voyage-agent`

The launcher injects:

```text
SPRING_CALLBACK_URL=http://host.docker.internal:5050
SPRING_CALLBACK_TOKEN=<per-invocation JWT>
SPRING_ORCHESTRATION_TOOLS=[{"Name":"list_members","InputSchema":{...},"OutputSchema":{...}},...]
```

The runtime image reads the env at startup and registers each descriptor with its tool surface. Sketch (illustrative shape; see launcher for canonical wiring):

```python
# In the runtime image's startup code
import json, os

descriptors = json.loads(os.environ.get("SPRING_ORCHESTRATION_TOOLS", "[]"))
callback_url = os.environ["SPRING_CALLBACK_URL"]
callback_token = os.environ["SPRING_CALLBACK_TOKEN"]

for descriptor in descriptors:
    tool_name = descriptor["Name"]               # "list_members", "delegate_to", ...
    register_tool(
        name=tool_name,
        input_schema=descriptor["InputSchema"],
        output_schema=descriptor["OutputSchema"],
        invoke=lambda args, n=tool_name: post(
            f"{callback_url}/v1/runtime/orchestration/{n}",
            json=args,
            headers={"Authorization": f"Bearer {callback_token}"},
        ),
    )
```

The wire-name projection follows the `[JsonStringEnumMemberName(...)]` attribute on each enum member; the launcher's serializer emits the snake_case wire form, not the C# PascalCase name.

---

## 8. Adding a new runtime image

Checklist for a runtime-image author wiring orchestration into a new image:

1. **Implement tool-call dispatch for the closed enum.** Map each of the five wire names to a handler that posts to `${SPRING_CALLBACK_URL}${OrchestrationRoutePrefix}/<tool>` with `Authorization: Bearer ${SPRING_CALLBACK_TOKEN}`. Use the schemas from § 2 as the dispatch contract.
2. **Read `SPRING_CALLBACK_URL` and `SPRING_CALLBACK_TOKEN` from the environment.** Both env vars are written by the launcher uniformly across runtimes per ADR-0039 § 3, even if the runtime uses MCP exclusively. LLM-only runtimes that ignore them waste only the bytes; consistency keeps the launcher contract uniform.
3. **Choose an attachment mechanism for the tool list.** Three options exist today:
   - MCP server attached by the launcher (`claude-code`, `codex`, `gemini` shape) — the runtime discovers tools via MCP `tools/list`.
   - Env-var registry (`spring-voyage-agent` shape) — the runtime reads `SPRING_ORCHESTRATION_TOOLS` from the environment.
   - A custom mechanism implemented by a new launcher — see [`agent-runtime.md` § 9](agent-runtime.md#9-adding-a-new-launcher).
4. **Surface auth errors cleanly.** The platform's reject codes (§ 5) carry actionable signals — surface them to the agent's instructions or to the workflow author so callers can decide whether to retry, fall back, or escalate.
5. **Honour the closed enum.** The runtime must not invent tool names. The platform routes only the five canonical wire names; an unknown name returns 404 from the dispatcher.

Authoring a new launcher (rather than a new image consuming an existing launcher) is a separate task — the launcher checklist is in [`agent-runtime.md` § 9](agent-runtime.md#9-adding-a-new-launcher).

---

## See also

- [ADR-0039](../decisions/0039-units-are-agents.md) — units-are-agents; canonical contract for the tool surface, attachment, and authorization rules.
- [Agent SDK](agent-sdk.md) — typed HTTP callback surface for workflow-driven runtimes; same handlers, different transport.
- [Agent Runtime](agent-runtime.md) — overall runtime contract; the launcher tier that attaches these tools.
- [Agents](agents.md) — agent / unit composite model and the role of orchestration in the broader dispatch path.
- [Units & Agents](units.md) — unit entity model, membership, and how children are exposed as orchestration targets.
