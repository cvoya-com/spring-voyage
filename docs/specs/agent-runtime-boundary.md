# Agent Runtime Boundary — Contract Specification

- **Status:** Accepted
- **Version:** v0.1 (initial)
- **Date:** 2026-04-28
- **Implements:** [ADR-0029 — Tenant execution boundary](../decisions/0029-tenant-execution-boundary.md), Stage 1
- **Anchors on:** [ADR-0030 — Thread model](../decisions/0030-thread-model.md), [`docs/architecture/thread-model.md`](../architecture/thread-model.md) (F1)
- **Aligned with:** [ADR-0026 — Per-agent container scope](../decisions/0026-per-agent-container-scope.md), [ADR-0027 — Agent-image conformance contract](../decisions/0027-agent-image-conformance-contract.md)

---

## 0. Preamble

### 0.1 Scope

This specification pins the contract surfaces that sit between the Spring Voyage platform and a tenant-scoped agent container. It is **implementation-neutral**: any conforming agent runtime, SDK, or test harness — in any language — can be built against this document without re-deriving choices from ADR-0029 or the F1 design.

Four surfaces are specified:

1. **Bucket 1 — Agent SDK contract**: the three lifecycle hooks the SDK MUST expose to agent code (`initialize`, `on_message`, `on_shutdown`).
2. **`IAgentContext` payload**: the bootstrap bundle delivered to the SDK at container start, plus its delivery channels (env vars + mounted files).
3. **Per-agent workspace volume**: the durable filesystem state contract.
4. **Bucket 2 — Public platform A2A send endpoint**: the single tenant → platform interface for sending A2A messages.

### 0.2 Out of scope

The following are explicitly **not** specified by this document. See § 6 for the full list and references to where they will be settled.

- The wire shape of memory tools (`store(memory)`, `recall(query)`) and the `MemoryEntry` schema — Stage 4 of ADR-0029.
- MCP tool surfaces beyond `store`, `recall`, `message.retract`, and `peek_pending`.
- Multi-language SDK implementations (each is a downstream artefact of this spec).
- `task.*` MCP tools (tasks are memory entries per F1 Q5 / ADR-0030).
- Cold-start fields (`is_first_contact`, `instructions.opening_offer`) — F1 Q9 makes cold start a UX + agent-runtime concern.
- The ADR-0028 Decision C amendment for platform-wide Ollama (separate ADR).
- Implementation choices: programming language, framework, transport library, supervision topology — Stages 2 / 3.

### 0.3 Normative language (RFC 2119)

The key words "**MUST**", "**MUST NOT**", "**REQUIRED**", "**SHALL**", "**SHALL NOT**", "**SHOULD**", "**SHOULD NOT**", "**RECOMMENDED**", "**MAY**", and "**OPTIONAL**" in this document are to be interpreted as described in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119).

Each normative requirement in this spec is intended to be testable. Example payloads (JSON) accompany the requirements; they are illustrative unless explicitly marked normative.

### 0.4 References

- ADR-0029 — buckets and surfaces this spec implements: [`docs/decisions/0029-tenant-execution-boundary.md`](../decisions/0029-tenant-execution-boundary.md).
- ADR-0030 — the thread model this spec consumes: [`docs/decisions/0030-thread-model.md`](../decisions/0030-thread-model.md).
- F1 design — long-form rationale and the per-question decisions on threads / memory / tasks: [`docs/architecture/thread-model.md`](../architecture/thread-model.md).
- ADR-0026 — per-agent container scope: [`docs/decisions/0026-per-agent-container-scope.md`](../decisions/0026-per-agent-container-scope.md).
- ADR-0027 — A2A 0.3.x conformance and the three image paths: [`docs/decisions/0027-agent-image-conformance-contract.md`](../decisions/0027-agent-image-conformance-contract.md).
- Public Web API conventions, versioning, deprecation, role taxonomy: [`docs/architecture/web-api.md`](../architecture/web-api.md).
- Platform-side runtime / dispatcher description (informative): [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md).

### 0.5 Conformance summary

A platform implementation conforms to this spec when it provides Bucket 2 with the shapes, status codes, auth model, and routing semantics specified in § 4, delivers `IAgentContext` to every tenant container per § 2, and provisions a per-agent workspace volume per § 3.

A tenant-side SDK conforms when it exposes the three Bucket-1 hooks per § 1, consumes `IAgentContext` per § 2, treats the workspace volume per § 3, calls Bucket 2 per § 4, and observes the per-thread FIFO + concurrent-threads invariants of § 1.2 and § 5.

§ 5 carries the cross-cutting conformance checklist a future test suite will exercise.

---

## 1. Bucket 1 — Agent SDK contract (platform → tenant)

The SDK MUST expose exactly three lifecycle hooks to agent code: `initialize(context)`, `on_message(message)`, and `on_shutdown(reason)`. The platform calls these hooks (directly or via the SDK's runtime). The agent implements them. No fourth hook is part of the contract.

This is the minimal surface ADR-0029 commits to. Anything else an SDK exposes for ergonomics — typed helpers, MCP client wrappers, prompt assembly utilities — is implementation-defined and out of scope for this spec.

### 1.1 `initialize(context)`

The SDK MUST invoke `initialize(context)` exactly once per container instance, before any inbound A2A message is delivered to `on_message`. The hook receives the `IAgentContext` payload defined in § 2.

Requirements:

- The SDK **MUST** complete `initialize` before accepting any A2A traffic on the agent's `:8999` listener (per ADR-0027). The agent's A2A server MAY be bound but MUST NOT begin invoking `on_message` until `initialize` has returned successfully.
- The agent **MAY** use this hook to open telemetry exporters, inspect the workspace volume to determine recovery state (see § 3.3), wire identity, prefetch tools via MCP, or any initialization work the agent author chooses.
- The hook **MUST** complete (return successfully) or fail (raise an error / return a failure) within a recommended initialization window of **30 seconds**. The platform **MAY** abort the container if the window elapses without completion. Platforms **SHOULD** make the window configurable for operators, but the SDK **MUST NOT** assume more than 30 seconds is available.
- If `initialize` fails (raises, returns an error, or exceeds the window), the SDK **MUST** surface the failure to the platform via the container exit code (non-zero). The platform **MAY** retry the container per its own restart policy; the agent **MUST NOT** assume the platform will retry.
- The SDK **MUST NOT** invoke `on_message` for any inbound A2A request received before `initialize` completes. It **SHOULD** either buffer such requests until `initialize` returns or reject them with an A2A-level transient error. The choice is implementation-defined, but the invariant — `on_message` does not run before `initialize` returns — is normative.
- The SDK **MUST NOT** invoke `on_shutdown` until after `initialize` has completed (successfully or not). If termination is requested during `initialize`, the SDK **SHOULD** wait for `initialize` to return (or time out) before calling `on_shutdown`.

### 1.2 `on_message(message)`

The SDK MUST invoke `on_message(message)` once per inbound A2A 0.3.x message. The hook receives the message payload defined in § 1.2.1 below and returns a streaming response per § 1.2.2.

#### 1.2.1 Inbound message shape

The payload **MUST** carry at minimum the following fields:

| Field | Type | Required | Description |
|---|---|---|---|
| `thread_id` | string | yes | The platform-assigned identifier of the thread the message belongs to. Stable across the thread's lifetime. |
| `message_id` | string | yes | The platform-assigned identifier of this individual message, unique within the thread. |
| `sender` | object | yes | The originating participant: `{ kind: "human" | "agent" | "unit" | "system", id: string, display_name?: string }`. |
| `payload` | object | yes | The A2A 0.3.x message body — typically `{ role, parts: [...] }` per the A2A spec. The SDK **MAY** surface this either as the raw A2A envelope or as a deserialised structure; the field MUST be present and faithful to the wire shape. |
| `timestamp` | string (RFC 3339) | yes | When the platform received the message. Per-thread FIFO is on this timestamp (§ 1.2.3). |
| `pending_count` | integer | no | Cheap hint indicating how many additional messages are queued for this thread. Implementations MAY omit it; agents MUST NOT rely on its precision. See § 1.2.4. |
| `context` | object | no | Optional UX-hint metadata; see § 1.2.5. |

Example (illustrative):

```json
{
  "thread_id": "thr_01HJX5K2N3M4P5Q6R7S8T9V0W1",
  "message_id": "msg_01HJX5K2P3Q4R5S6T7U8V9W0X1",
  "sender": {
    "kind": "human",
    "id": "user_01HJX0000000000000000000A",
    "display_name": "Savas"
  },
  "payload": {
    "role": "user",
    "parts": [{ "kind": "text", "text": "re: #flaky-test-fix — try the integration test scope." }]
  },
  "timestamp": "2026-04-28T14:22:13.418Z",
  "pending_count": 0,
  "context": {
    "kind": "task_update",
    "task": "#flaky-test-fix"
  }
}
```

The platform **MUST NOT** branch on `context` (§ 1.2.5). The SDK **MUST** pass it through to the agent verbatim.

#### 1.2.2 Streaming response semantics

`on_message` returns a stream of zero-or-more **chunks** terminated by exactly one **completion sentinel** OR exactly one **error frame**.

- The SDK **MUST** expose a streaming abstraction appropriate to the host language (async iterator, generator, observable, callback-driven emitter, etc.). The on-the-wire shape is governed by A2A 0.3.x; this spec specifies the SDK-facing contract.
- A **chunk** carries an incremental fragment of the agent's response (text, tool-call status, partial structured output, etc.). Chunk shape MUST be A2A-compatible — the SDK is responsible for marshalling chunks into A2A streaming responses on the wire.
- A **completion sentinel** marks the end of a successful response. Exactly one MUST be emitted on the success path.
- An **error frame** terminates the stream with an A2A-compatible error. Exactly one MUST be emitted on the failure path.
- After a completion sentinel or an error frame, the SDK **MUST NOT** emit further chunks for this `message_id`.
- A long-running agent **MAY** emit zero chunks before completion (a final-only response). It **MAY** emit many chunks (token streaming). Both shapes are valid.

Streaming is per-message; an agent's response to message N is independent of its response to message N+1 (subject to the FIFO invariant in § 1.2.3).

#### 1.2.3 Per-thread FIFO

The platform **MUST** preserve per-thread FIFO order: messages with the same `thread_id` MUST be delivered to `on_message` in the order the platform received them. The SDK **MUST** preserve this order — it MUST NOT reorder messages within a thread or invoke `on_message` for `message N+1` before `on_message` for `message N` has begun.

The platform **MAKES NO PROMISE** about ordering across distinct threads. Two threads may race; their messages may arrive in any interleaving.

#### 1.2.4 Concurrent threads

By default, the platform **MAY** have multiple `on_message` invocations in flight for the same agent — at most one per distinct `thread_id`. The SDK **MUST** be re-entrant in this default case: it MUST be safe to have N `on_message` calls executing concurrently, one per thread.

The agent / unit definition carries a **`concurrent_threads`** boolean field. This spec normatively defines its semantics:

- **`concurrent_threads: true`** (default): the platform MAY invoke `on_message` concurrently across distinct threads. Per-thread FIFO is preserved within each thread.
- **`concurrent_threads: false`**: the platform **MUST** serialize `on_message` invocations across all threads the agent participates in. At most one `on_message` invocation is in flight for the agent at any time.

The flag is resolved at the agent level (not per-message). The platform **MUST** deliver the resolved value to the SDK via `IAgentContext` (§ 2.1) so the SDK knows whether to expect re-entrant invocations.

The `pending_count` hint (§ 1.2.1) is a non-binding signal that more messages exist for the same thread. The agent **MAY** use it to decide whether to drain the queue via `peek_pending(thread_id)` (an MCP tool surfaced by the platform) or to process messages one at a time. The platform's dispatch contract is **one message per `on_message` invocation** — auto-batching is not imposed.

#### 1.2.5 Optional `context` UX hint

Inbound messages **MAY** carry an optional `context` object — a UX hint passed through from the sender's surface, defined by F1 Q7. The shape is:

```json
{
  "kind": "task_update" | "reminder" | "observation" | "spontaneous",
  "task": "#name",         /* optional */
  "originating_message": "msg_..." /* optional */
}
```

Normative requirements:

- The platform **MUST NOT** branch on `context`. It is opaque metadata at the platform layer.
- The SDK **MUST** pass `context` through to the agent verbatim if present.
- The SDK **MUST NOT** synthesise a `context` if the sender did not provide one; absent is absent.
- The agent's outbound response **MAY** include a `context` block of the same shape; the platform **MUST** carry it through to the recipient surface unchanged.
- The set of `kind` values is a UX vocabulary; this spec does not pin it as a typed enum. Implementations **MUST** treat unknown `kind` values as a pass-through string.

The platform-defined message types — `Message`, `ParticipantStateChanged`, `Retraction`, system events — and their Timeline placement are described in F1 Q7 / ADR-0030. This spec only defines the wire shape for **inbound `Message` payloads** delivered to `on_message`; participant-state and retraction events do not go through `on_message` (they are platform-emitted Timeline artifacts).

### 1.3 `on_shutdown(reason)`

The SDK **MUST** invoke `on_shutdown(reason)` exactly once when the platform terminates the container.

Signal mechanism:

- The platform **MUST** signal termination via **SIGTERM** delivered to the container's PID 1.
- The platform **MUST** then wait at least the **grace window** (recommended **30 seconds**) before escalating to SIGKILL. Implementations **MAY** make the window operator-configurable but MUST NOT escalate sooner than the documented window.
- The SDK **MUST** trap SIGTERM and invoke `on_shutdown` synchronously. The hook **MUST** complete within the grace window. If it does not, the platform **MAY** SIGKILL.

The `reason` parameter is an enum:

| Value | Meaning |
|---|---|
| `requested` | An operator or upstream tenant action requested termination (explicit cancel, container delete, redeploy). |
| `idle_timeout` | The platform's idle-eviction policy fired. |
| `policy` | A platform-level policy (e.g., per-agent budget exhausted, runtime version retired) terminated the container. |
| `error` | The platform detected a fatal condition (repeated crash, healthcheck failure) and is reaping the container. |
| `platform_restart` | The platform itself is restarting and is draining tenant containers. |
| `unknown` | None of the above; the SDK MAY surface this when SIGTERM arrives without a discernible cause. |

Normative requirements on the hook:

- The agent **SHOULD** flush in-progress work to its workspace volume (§ 3) before returning. The agent **MUST NOT** assume the platform will retry on its behalf — recovery on next start is the agent's own responsibility (§ 3.3).
- The agent **MAY** use `on_shutdown` to close telemetry exporters, drain MCP sessions, finalise log streams, etc.
- The SDK **MUST NOT** invoke `on_message` for any new messages after `on_shutdown` has been called.
- In-flight `on_message` invocations at the moment SIGTERM arrives **SHOULD** be cancelled cooperatively; the SDK **MUST NOT** wait for them to complete past the grace window. Whether and how to cancel is implementation-defined; the invariant is that `on_shutdown` runs and completes (or is killed) within the grace window.

### 1.4 Conformance — Bucket 1

An SDK conforms to Bucket 1 iff:

1. It exposes `initialize`, `on_message`, and `on_shutdown` to agent code with the semantics in §§ 1.1 – 1.3.
2. `initialize` runs before any `on_message`; `on_shutdown` runs after the last `on_message`; both are invoked exactly once per container instance.
3. `initialize` completion (success or failure) is bounded by the documented window (§ 1.1).
4. Per-thread FIFO is preserved end-to-end (§ 1.2.3).
5. Concurrent-threads re-entrancy is supported when `concurrent_threads: true`; serialised when `concurrent_threads: false` (§ 1.2.4).
6. SIGTERM triggers `on_shutdown` and the hook completes (or is killed) within the grace window (§ 1.3).
7. The optional `context` UX hint is passed through verbatim and is not branched on by the SDK or platform (§ 1.2.5).

A future conformance test suite (out of scope for this spec) will exercise each numbered requirement against a candidate SDK.

---

## 2. `IAgentContext` payload

`IAgentContext` is the bootstrap bundle delivered to the SDK at `initialize` (§ 1.1). It is **read-only data and handles** — neither an API call nor a hook — and it carries everything an agent needs to wire itself up: identity, the Bucket-2 endpoint, scoped credentials, platform-provided service endpoints, and the workspace mount path.

### 2.1 What's in it

The bundle MUST carry the following fields. Field names below are normative; their representation in env vars vs. mounted files is specified in § 2.2.

#### Static metadata

| Field | Type | Required | Description |
|---|---|---|---|
| `tenant_id` | string | yes | The tenant the agent runs under. Stable for the agent's lifetime. |
| `unit_id` | string | no | The unit the agent is a member of, if applicable. Absent for standalone agents. |
| `agent_id` | string | yes | The agent's stable identifier within the tenant. Used by the platform to route. |
| `thread_id` | string | no | The Spring Voyage thread id associated with this launch, when the launch was triggered by a dispatch on a known thread. Absent on supervisor-driven restarts (see § 2.2.1). |
| `agent_definition` | object | yes | The agent's full definition (instructions, execution config, etc.). Delivered as a structured document; see § 2.2 for the file-channel mechanism. |
| `tenant_config` | object | no | Tenant-level configuration the agent may read (feature flags, defaults). Structure is operator-defined; the platform MUST NOT reinterpret. |

#### Bucket-2 endpoint

| Field | Type | Required | Description |
|---|---|---|---|
| `bucket2_url` | string (URL) | yes | The public Web API base URL the agent uses to send A2A messages back into the platform (§ 4). MUST be reachable from the container. |
| `bucket2_token` | string | yes | A scoped credential the agent presents to Bucket 2. MUST be agent-scoped (§ 4.5). |

#### Platform-provided service endpoints

For each of these endpoint categories, the platform MUST supply both a URL the container can reach and a credential scoped to this agent. Credentials MUST NOT be shared across agents.

| Field | Type | Required | Description |
|---|---|---|---|
| `llm_provider_url` | string (URL) | yes | The endpoint for the agent's primary LLM provider (platform-hosted Ollama, managed-provider proxy, etc.). The provider's native API is the contract (per ADR-0029). |
| `llm_provider_token` | string | yes | Agent-scoped credential for the LLM endpoint. |
| `mcp_url` | string (URL) | yes | The platform's MCP endpoint the agent uses for tool discovery and invocation (`store`, `recall`, `peek_pending`, `message.retract`, plus any other tools the platform exposes). |
| `mcp_token` | string | yes | Agent-scoped credential for the MCP endpoint. |
| `telemetry_url` | string (URL) | yes | The OpenTelemetry collector endpoint the agent emits traces/metrics/logs to. |
| `telemetry_token` | string | no | Agent-scoped credential, if the collector requires authentication. MAY be omitted in deployments where the collector is unauthenticated and reached only through the container's network namespace. |

The platform **MAY** add additional service endpoints in future revisions (e.g., a dedicated artifacts endpoint). Adding a new endpoint is an additive change to this section and follows the versioning posture of § 4.6.

#### Workspace mount path

| Field | Type | Required | Description |
|---|---|---|---|
| `workspace_path` | string (filesystem path) | yes | The path inside the container at which the agent's persistent volume is mounted. See § 3. The recommended default is `/spring/workspace/`. |

#### Concurrent-threads policy

| Field | Type | Required | Description |
|---|---|---|---|
| `concurrent_threads` | boolean | yes | The resolved value of the agent / unit `concurrent_threads` flag (§ 1.2.4). The SDK uses this to decide whether to expect re-entrant `on_message` invocations. |

#### Example payload (illustrative)

The composite logical view, for an SDK that materialises `IAgentContext` as a typed object:

```json
{
  "tenant_id": "tenant_acme",
  "unit_id": "unit_engineering-team",
  "agent_id": "agent_backend-engineer-3",
  "agent_definition": {
    "id": "agent_backend-engineer-3",
    "instructions": "...",
    "execution": {
      "tool": "claude-code",
      "image": "ghcr.io/cvoya-com/agent-claude-code:1.4.2",
      "hosting": "persistent",
      "concurrent_threads": true
    }
  },
  "tenant_config": {
    "features": { "extended-context": true }
  },
  "bucket2_url": "https://api.example.com/api/v1/",
  "bucket2_token": "svat_...",
  "llm_provider_url": "https://api.example.com/llm/anthropic/",
  "llm_provider_token": "svlt_...",
  "mcp_url": "https://api.example.com/mcp/",
  "mcp_token": "svmt_...",
  "telemetry_url": "https://otel.example.com:4318",
  "telemetry_token": null,
  "workspace_path": "/spring/workspace/",
  "concurrent_threads": true
}
```

The actual on-disk representation is split between env vars and files per § 2.2.

### 2.2 How it's delivered

The platform **MUST** deliver `IAgentContext` via two channels, both populated before the container's main process starts:

1. **Environment variables** — for scalar / short values (URLs, credentials, ids, the `concurrent_threads` flag).
2. **Mounted files** — for structured / multi-value payloads (the agent definition, tenant-level config blob).

Both channels **MUST** be readable synchronously at the top of `initialize` — the SDK does not wait on a network call to assemble the bundle.

#### 2.2.1 Canonical environment variable names (normative)

| Env var | Maps to | Required |
|---|---|---|
| `SPRING_TENANT_ID` | `tenant_id` | yes |
| `SPRING_UNIT_ID` | `unit_id` | no |
| `SPRING_AGENT_ID` | `agent_id` | yes |
| `SPRING_THREAD_ID` | `thread_id` | no |
| `SPRING_BUCKET2_URL` | `bucket2_url` | yes |
| `SPRING_BUCKET2_TOKEN` | `bucket2_token` | yes |
| `SPRING_LLM_PROVIDER_URL` | `llm_provider_url` | yes |
| `SPRING_LLM_PROVIDER_TOKEN` | `llm_provider_token` | yes |
| `SPRING_MCP_URL` | `mcp_url` | yes |
| `SPRING_MCP_TOKEN` | `mcp_token` | yes |
| `SPRING_TELEMETRY_URL` | `telemetry_url` | yes |
| `SPRING_TELEMETRY_TOKEN` | `telemetry_token` | no |
| `SPRING_WORKSPACE_PATH` | `workspace_path` | yes |
| `SPRING_CONCURRENT_THREADS` | `concurrent_threads` (`"true"` or `"false"`) | yes |

The platform **MUST** populate every required env var before the container's PID 1 begins execution. The SDK **MUST** treat any required env var as missing/empty as a fatal `initialize` error.

`SPRING_THREAD_ID` is present when the container launch originates from a specific dispatch context (e.g. the first launch triggered by a message on a known thread). It is **absent** on supervisor-driven restarts (which are agent-level lifecycle events, not bound to any particular thread). The SDK **MUST NOT** treat the absence of `SPRING_THREAD_ID` as a fatal error. The SDK **MAY** use the thread id when present to associate the current container with the dispatching thread (e.g. for runtime-session resume — see #1300). See also follow-up #1357 for Python SDK adoption of this env var.

#### 2.2.2 Canonical mount path (normative)

The platform **MUST** mount structured files at `/spring/context/` inside the container. The directory **MUST** contain at minimum:

| File | Maps to | Required | Format |
|---|---|---|---|
| `/spring/context/agent-definition.yaml` | `agent_definition` | yes | YAML or JSON (operators MAY pick; SDK SHOULD accept both — file extension is normative) |
| `/spring/context/tenant-config.json` | `tenant_config` | no | JSON |

If the agent definition is delivered as YAML, the file MUST be `agent-definition.yaml`; if JSON, `agent-definition.json`. The SDK **MUST** check both extensions.

The `/spring/context/` directory **MUST** be readable to the container's UID. The platform **SHOULD** mount it read-only.

The platform **MAY** add additional files under `/spring/context/` in future revisions (e.g., `cloning-policy.json`, `expertise-projection.json`); SDKs **MUST** ignore files they do not recognise.

#### 2.2.3 Credential rotation

Scoped credentials delivered through `IAgentContext` (`bucket2_token`, `llm_provider_token`, `mcp_token`, optional `telemetry_token`) are minted **per container launch** and **MUST** be valid for the lifetime of that launch. The platform **MUST NOT** issue tokens whose published TTL is shorter than the container's expected uptime under normal operation; operators **SHOULD** size token TTLs to comfortably exceed the agent's idle-eviction window so a healthy container never observes a credential expiry mid-run.

Restart is the rotation primitive. When the platform needs new credentials in a running container — because the previous launch's tokens have expired, are about to expire, or have been revoked — it **MUST** re-mint them through the same path that minted them at first launch (the platform-side `IAgentContext` builder), and it **MUST** apply them by performing a clean **stop + restart** of the container. The restarted container's `initialize` reads the new env vars and mounted files exactly as it did at first launch (§ 2.2.1, § 2.2.2). The workspace volume (§ 3) carries any state the agent needs to resume.

Normative requirements:

- **Per-launch minting (MUST).** The platform **MUST** mint a fresh, agent-scoped credential set on every container launch, including every supervisor-driven restart. Reusing the previous launch's credentials on a restart **MUST NOT** occur. Persisting credentials in the supervisor's own state for replay on restart **MUST NOT** occur.
- **Restart re-injection (MUST).** A platform component that restarts an agent container after a crash **MUST** route the restart through the same `IAgentContext` build path used for first launch, so that `SPRING_BUCKET2_TOKEN`, `SPRING_LLM_PROVIDER_TOKEN`, `SPRING_MCP_TOKEN`, and any other scoped tokens are present and fresh in the restarted container's env / file layer before its PID 1 begins execution.
- **Stop + start, not in-place mutation (MUST).** The platform **MUST NOT** mutate env vars or mounted files of a running container to change credentials. New credentials reach the container only via a new launch.
- **SDK re-read (MUST).** The SDK **MUST** read credentials at the top of `initialize` for every container launch. The SDK **MUST NOT** assume a credential cached from a prior process lifetime is still valid.
- **In-process caching (MAY).** The SDK **MAY** cache credentials in memory for the duration of a single container launch. Because rotation is achieved by restart, in-process caching is safe within a launch.
- **Revocation contract (SHOULD).** When a credential is revoked while a container is running, the platform **SHOULD** terminate and restart the container so the next launch picks up a fresh credential. SDKs **MUST** treat an authentication failure on a platform-provided endpoint (Bucket 2, LLM provider, MCP) as a fatal in-flight error: surface the error per § 1.2.2 (error frame on the affected `on_message`) and either fail the container (non-zero exit) or surface the failure to the platform's supervisor through whatever liveness mechanism the platform exposes. The SDK **MUST NOT** silently retry an authentication failure against a platform-provided endpoint — auth failures from a platform endpoint indicate the credential set is no longer valid for this launch and only a restart can resolve them.
- **No long-running zero-downtime rotation in this revision.** The platform **MUST NOT** rely on any in-container credential-refresh hook (`SIGHUP`, file-watch, polling) being implemented by the SDK. SDKs **MAY** expose such a hook for forward compatibility, but conforming platforms **MUST NOT** require it.

Lifetime guidance (informative): operators **SHOULD** set published token TTLs to at least **24 hours** to give a typical persistent-agent restart cycle ample headroom. The platform's restart path (§ "Failure recovery" in ADR-0029) re-mints on every restart, so token TTL only constrains how long a container can run **without** a restart before the platform must force one.

Future evolution (informative): a follow-up revision **MAY** add a mounted-files + credential-refresher mechanism that allows zero-downtime rotation for long-running containers. That mechanism would be additive to this section: the env-var channel of § 2.2.1 stays as the canonical credential-delivery surface; a refresher would write fresh credentials into a new path under `/spring/context/credentials/` that the SDK MAY re-read on demand. SDKs that consume only the env-var channel remain conforming. See [`docs/architecture/agent-credential-rotation.md`](../architecture/agent-credential-rotation.md) for the design rationale and the path to that evolution.

### 2.3 Conformance — `IAgentContext`

A platform conforms to § 2 when:

1. Every required env var (§ 2.2.1) is populated before container start.
2. Every required mounted file (§ 2.2.2) is present and readable at `/spring/context/`.
3. Every credential is agent-scoped (no cross-agent reuse).
4. The `SPRING_CONCURRENT_THREADS` value matches the resolved agent / unit definition.
5. Every container launch — including supervisor-driven restarts — sees freshly minted scoped credentials (§ 2.2.3); no replay of a prior launch's credentials.

An SDK conforms when it reads the env vars and files at the top of `initialize`, materialises an `IAgentContext`-shaped object for the agent, and surfaces missing required fields as a fatal `initialize` failure. An SDK additionally conforms to § 2.2.3 when it re-reads credentials at the top of every `initialize` (no cross-launch caching) and treats authentication failures against platform-provided endpoints as fatal in-flight errors rather than silently retrying.

---

## 3. Per-agent workspace volume

The platform **MUST** grant every agent exactly one persistent filesystem volume. The volume is the agent's durable state primitive — there is no KV interface, no platform-defined serialisation shape, and no `on_recover` hook. The agent owns recovery (§ 3.3).

### 3.1 Mount and naming

- The volume **MUST** be mounted at the path delivered in `SPRING_WORKSPACE_PATH` (§ 2.2.1). The recommended default is `/spring/workspace/`. Operators **MAY** override the path; SDKs **MUST** read the env var rather than hard-coding the default.
- The mount **MUST** be writable to the container's UID.
- The volume **MUST** be private to the agent. No cross-agent read/write access at the volume layer is permitted; the platform **MUST NOT** mount one agent's workspace into another agent's container.

### 3.2 Lifetime semantics

- The volume **MUST** persist across container restarts: crashes, redeploys, scaling events, image upgrades, host migrations.
- The volume **MUST** be reclaimed only when the agent is explicitly deleted, OR — for cloned agents whose cloning policy declares ephemeral workspace — when the clone is reaped. The cloning-policy semantics are governed by [`docs/architecture/units.md`](../architecture/units.md) and outside the scope of this spec.
- The platform **MUST NOT** silently truncate, snapshot-revert, or otherwise mutate the volume's contents during its lifetime. Operator-driven restoration (e.g., from a backup) is permitted but is an operator action, not a platform action.

### 3.3 Recovery is agent-owned

The platform **MUST NOT**:

- Pass any `state` parameter to `initialize`, `on_message`, or `on_shutdown`.
- Provide an `on_recover` (or equivalently named) hook.
- Otherwise signal "you are recovering from a crash" vs. "you are starting fresh."

The agent **MUST** inspect the workspace during `initialize` to determine recovery state. The presence of checkpoint files, in-progress transaction markers, etc., is the agent's signal.

This is a deliberate minimisation: the agent knows best what its own state shape is and what counts as "recoverable." A platform-level recovery contract would either dictate a state shape or be vacuous.

### 3.4 Cross-agent transfer

Cross-agent state transfer **MUST** flow through A2A payloads (§ 4) — not through volume sharing. The volume layer is single-writer / single-reader.

If two agents need to exchange state, they exchange A2A messages. If an agent needs to publish artifacts the platform routes for it, that goes through MCP / Bucket 2, not through the volume layer.

### 3.5 Platform-side concerns (informative)

These are out of scope for the SDK's contract but listed so implementers know what to expect from a conforming platform. The platform owns:

- **Quotas** — operator-configurable size and file-count limits per volume. The SDK **MUST** handle quota-exceeded errors (typically `ENOSPC`) gracefully — fail the in-progress write, surface the error, do not crash the container.
- **Encryption-at-rest** — platform-controlled. The agent does not see encryption keys.
- **Backup / snapshot** — platform-controlled. The agent **MUST NOT** assume any specific snapshot cadence; agents that care about recovery checkpointing **MUST** write their own checkpoints.
- **Migration across container hosts** — platform-controlled. The agent **MUST** treat the volume as the same volume across restarts even if the underlying host changes.

### 3.6 Conformance — Workspace volume

A platform conforms to § 3 when:

1. Every agent has exactly one volume mounted at `SPRING_WORKSPACE_PATH`.
2. The volume is writable to the container's UID.
3. The volume survives container restart (testable: write file, restart container, read file).
4. The volume is private (testable: agent A's volume is not visible inside agent B's container).
5. Quota errors are surfaced as standard filesystem errors.

An SDK conforms when it reads the path from `SPRING_WORKSPACE_PATH`, treats the volume as writable durable storage, and inspects it during `initialize` for recovery state rather than expecting a platform-delivered recovery signal.

---

## 4. Bucket 2 — Public platform A2A send endpoint (tenant → platform)

The single public-API interface tenant containers call. The platform implements it; the agent calls it via the credential delivered in `IAgentContext` (§ 2).

### 4.1 Wire protocol

- The wire protocol **MUST** be A2A 0.3.x, per ADR-0027 and ADR-0029.
- A conforming platform **MUST** support the **HTTP** binding of A2A 0.3.x (JSON-RPC 2.0 over HTTP).
- A conforming platform **MUST** support the **gRPC** binding of A2A 0.3.x.
- The agent **MAY** choose either binding. The SDK **MAY** wrap both or one; this spec does not constrain the SDK's transport choice beyond the requirement that both bindings reach the same logical endpoint.

The platform's own conformance to A2A 0.3.x as an A2A server (when it acts as one — Bucket 2 is the agent → platform direction) follows ADR-0027's wire contract.

### 4.2 URL surface

The Bucket-2 endpoint sits under the public Web API's `/api/v1/` namespace. The canonical path-style URL is:

```
POST /api/v1/threads/{thread_id}/messages
```

per F1 and the URL surface decision tracked in [#1291](https://github.com/cvoya-com/spring-voyage/issues/1291).

Normative requirements:

- A conforming platform **MUST** expose `POST /api/v1/threads/{thread_id}/messages` with the request / response shapes specified in §§ 4.3 – 4.4.
- The platform **MAY** expose A2A-specific URL conventions (e.g., the JSON-RPC `POST /` method-routed surface used between dispatcher and agent containers per ADR-0027) **in addition** to the path-style URL. Where it does, the path-style URL remains the canonical Bucket-2 surface for tenant containers.
- The path component `{thread_id}` **MUST** be the platform-assigned thread identifier (the same value carried in inbound `on_message` payloads, § 1.2.1).
- The platform **MUST NOT** alias `/api/v1/conversations/...` as a backward-compatible URL. Per F1 Q10 / ADR-0030, v0.1 is not migrated to; there is no legacy alias.

### 4.3 Request body

The request body **MUST** be a JSON object whose top level conforms to an A2A 0.3.x message envelope, augmented with the thread / participant metadata required to route on the platform.

| Field | Type | Required | Description |
|---|---|---|---|
| `message` | object | yes | The A2A 0.3.x `Message` body — `{ role, parts, ... }` per the A2A spec. |
| `recipient` | object | no | The intended recipient participant within the thread, when the agent is addressing one specific peer rather than the thread at large. Shape: `{ kind: "human" | "agent" | "unit" | "system", id: string }`. If absent, the platform routes the message to all active participants of the thread per its routing rules. |
| `context` | object | no | Optional UX-hint metadata (§ 1.2.5). The platform passes it through; it does not branch on it. |
| `idempotency_key` | string | no | Client-supplied key; the platform MAY use it to deduplicate retries. RECOMMENDED for at-least-once retry safety. |

Example:

```json
{
  "message": {
    "role": "agent",
    "parts": [
      { "kind": "text", "text": "I scoped it to the integration test and it now passes locally." }
    ]
  },
  "recipient": {
    "kind": "human",
    "id": "user_01HJX0000000000000000000A"
  },
  "context": {
    "kind": "task_update",
    "task": "#flaky-test-fix",
    "originating_message": "msg_01HJX5K2P3Q4R5S6T7U8V9W0X1"
  },
  "idempotency_key": "agent-backend-engineer-3:reply:01HJX5K9..."
}
```

The platform **MUST** assign and return a `message_id` (§ 4.4) for the appended message. The agent **MUST NOT** supply `message_id`, `timestamp`, or `sender` in the request — the platform stamps these from the authenticated identity.

### 4.4 Response semantics

The endpoint returns a streaming response: chunked HTTP for the HTTP binding, gRPC server-streaming for the gRPC binding.

Frame types:

- **Acknowledgement frame** — first frame, MUST include the platform-assigned `message_id` and `timestamp` for the appended message:
  ```json
  { "kind": "ack", "message_id": "msg_...", "timestamp": "2026-04-28T14:22:13.418Z" }
  ```
- **Progress frame (zero or more)** — non-binding signals about routing, recipient acknowledgements, partial delivery state. Shape:
  ```json
  { "kind": "progress", "stage": "routed" | "delivered" | "...", "detail": { ... } }
  ```
  Implementations **MAY** omit progress frames entirely.
- **Completion sentinel** — exactly one terminal frame on the success path:
  ```json
  { "kind": "complete" }
  ```
- **Error frame** — exactly one terminal frame on the failure path:
  ```json
  { "kind": "error", "code": "string", "message": "string", "retryable": true | false }
  ```

Conforming clients (SDKs) **MUST**:

- Treat the absence of an acknowledgement frame as a transport error.
- Treat any frame after a completion sentinel or error frame as a protocol violation.
- Treat unknown `stage` values in progress frames as opaque pass-through.

Synchronous (non-streaming) clients **MAY** wait for the completion sentinel and surface a single result; the streaming shape is normative but the SDK's surface to the agent is implementation-defined.

### 4.5 Auth

- Tenant identity **MUST** be verified at the endpoint via the scoped credential delivered in `IAgentContext` (§ 2.1, `bucket2_token`).
- Authz **MUST** be uniform: any caller presenting a valid scoped credential for tenant T **MAY** send to any thread T participates in. The endpoint **MUST NOT** apply per-message role gates beyond the tenant-scope check; per-message authz is not a function of the broader Web API role taxonomy (see [`docs/architecture/web-api.md` § Roles and URL scope](../architecture/web-api.md#roles-and-url-scope)) — Bucket 2 is the agent → platform return path, not an operator surface.
- Scoped tokens **MUST** be agent-scoped: one agent's `bucket2_token` **MUST NOT** be valid for another agent's traffic. The platform **MUST** reject (HTTP 403 / gRPC `PERMISSION_DENIED`) attempts to send on threads the token's owning agent does not participate in.
- Tokens **SHOULD** be opaque to the agent (the SDK reads the token from env, presents it as a bearer credential, does not interpret it).

The token format is platform-defined and out of scope for this spec.

### 4.6 Versioning posture

- The wire protocol is A2A 0.3.x; bumping to A2A 0.4.x or 1.x is a coordinated breaking change per ADR-0027.
- The URL path is `/api/v1/...`. Per [`docs/architecture/web-api.md`](../architecture/web-api.md) § "Versioning and deprecation":
  - `v1` is strictly additive. Adding new optional request fields, new response frame types (with new `stage` / `kind` values), or new endpoints under the same surface ships transparently.
  - Breaking changes wait for `v2` (new URL space `/api/v2/...`).
  - Deprecated endpoints carry the `Deprecation: true` and `Sunset` headers per the public API policy.
- The `context` UX hint vocabulary (§ 1.2.5, § 4.3) is intentionally schema-loose — adding new `kind` values is not a breaking change. SDKs **MUST** treat unknown values as opaque.

### 4.7 Routing semantics

The platform **MAY** route an outbound message:

- **In-network**, where the recipient is reachable on the platform's internal substrate (e.g., another agent on the same tenant network).
- **Via the dispatcher proxy**, where the recipient is not directly reachable.

The agent **MUST** treat both routes as equivalent. The routing decision is opaque to the agent; the response semantics (§ 4.4) are identical regardless of the path the platform picks.

### 4.8 Test-harness expectation

A conforming SDK / agent **MUST** be implementable and testable in isolation against a fake platform that exposes only this one endpoint (plus a tmpdir mounted as the workspace volume and a populated `IAgentContext`). The agent does not need access to the real Spring Voyage platform to exercise its end-to-end behaviour.

This is a normative requirement on the **shape of the boundary**, not on the SDK or agent — it follows from §§ 1 – 4 being self-contained. Test-harness availability is itself a downstream deliverable.

### 4.9 Conformance — Bucket 2

A platform conforms to Bucket 2 iff:

1. `POST /api/v1/threads/{thread_id}/messages` is exposed with the request / response shapes of §§ 4.3 – 4.4.
2. Both HTTP and gRPC bindings of A2A 0.3.x reach the endpoint.
3. The auth model of § 4.5 is enforced — scoped tokens, no cross-agent reuse, tenant-uniform authz within the scope.
4. The response stream conforms to the frame types of § 4.4.
5. Routing (§ 4.7) is opaque to the agent — same response semantics across in-network and proxied routes.
6. The `context` field is passed through unchanged (§ 4.3).

A conforming agent uses the endpoint per the request shape and consumes the response per § 4.4.

---

## 5. Conformance summary

A conformance test suite (a future deliverable, not in scope for this spec) exercises the following cross-cutting checklist. Each item maps to one or more numbered requirements in §§ 1 – 4.

**Bucket 1 — SDK hooks**

- [ ] Three hooks (`initialize`, `on_message`, `on_shutdown`) present and invoked with the lifecycle of § 1.
- [ ] `initialize` runs to completion before any `on_message`; bounded by the documented window (§ 1.1).
- [ ] `on_message` payload conforms to § 1.2.1; streaming response conforms to § 1.2.2.
- [ ] Per-thread FIFO preserved (§ 1.2.3).
- [ ] Concurrent-thread re-entrancy honours `concurrent_threads` (§ 1.2.4).
- [ ] `on_shutdown` runs on SIGTERM and completes within the grace window (§ 1.3).
- [ ] Optional `context` passed through verbatim, not branched on (§ 1.2.5).

**`IAgentContext`**

- [ ] Every required env var (§ 2.2.1) is read at the top of `initialize`.
- [ ] Required mounted files (§ 2.2.2) are present at `/spring/context/`.
- [ ] Credentials are agent-scoped — testable by attempting to use one agent's token to access another's threads (§ 4.5).
- [ ] Every container launch (including supervisor-driven restarts) sees freshly minted scoped credentials; the prior launch's tokens are never replayed (§ 2.2.3).
- [ ] The SDK does not cache credentials across container launches (§ 2.2.3).

**Workspace volume**

- [ ] Mounted at `SPRING_WORKSPACE_PATH`, writable, private (§§ 3.1, 3.4).
- [ ] Survives container restart (§ 3.2).
- [ ] No platform `state` parameter, no `on_recover` hook — recovery is agent-owned (§ 3.3).

**Bucket 2 — A2A send**

- [ ] `POST /api/v1/threads/{thread_id}/messages` reachable and observes the auth model (§§ 4.2, 4.5).
- [ ] Both HTTP and gRPC bindings supported (§ 4.1).
- [ ] Response stream emits ack → progress* → (complete | error) per § 4.4.
- [ ] Routing is opaque — same observable semantics regardless of in-network vs. proxy path (§ 4.7).
- [ ] `context` flows through unchanged (§ 4.3).

---

## 6. Out of scope

The following surfaces are deliberately not specified by this document. Each has a documented home for the work that will pin it.

- **Memory contract** — the wire shape of `store(memory)`, `recall(query)`, the `MemoryEntry` schema (`id`, `timestamp`, `payload`, `thread_id?`, `threadOnly?`), and the `ThreadMemoryPolicy` shape attached to threads. F1 / ADR-0030 settle the **behavioural model** (single per-agent `AgentMemory`, per-thread visibility, default `threadOnly: true`); the **MCP tool surface** that exposes it will be specified in a separate spec under `docs/specs/` per Stage 4 of ADR-0029. v0.1 agents call `store` and `recall` MCP tools as documented in F1 Q4; the wire-level contract is deferred.
- **MCP tool surfaces beyond `store`, `recall`, `message.retract`, and `peek_pending`.** Future tools (timers, pub/sub for cross-agent observation, agent registry, cloning — see ADR-0029 § "Capabilities reached through MCP, not at the boundary") will be added incrementally on the public Web API and exposed via MCP. This spec does not constrain them.
- **Multi-language SDK implementations.** Each language's SDK is a downstream artefact of this spec, not part of it. Python, .NET, TypeScript, and Go SDKs are anticipated; their packaging, idiomatic surface, and ergonomics are out of scope.
- **`task.*` MCP tools.** F1 Q5 / ADR-0030 collapses tasks to memory entries; there are no separate task tools at the platform layer.
- **Cold-start fields** (`is_first_contact`, `instructions.opening_offer`). F1 Q9 / ADR-0030 makes cold start a UX (E2) and agent-runtime concern, not a platform contract field. Agent runtimes (Claude CLI, etc.) carry their own cold-start prompt mechanisms.
- **ADR-0028 Decision C amendment for platform-wide Ollama.** Flagged in ADR-0029 as a follow-up; tracked separately and not part of this spec.
- **Implementation choices** — programming language, framework, transport library, supervision topology, container-runtime backend (Podman vs. Kubernetes). Stages 2 / 3 of ADR-0029.
- **Long-running zero-downtime credential rotation.** § 2.2.3 specifies restart as the rotation primitive; a future revision MAY add a mounted-files + refresher mechanism for in-place rotation without container restart. Design rationale and the path to that evolution live in [`docs/architecture/agent-credential-rotation.md`](../architecture/agent-credential-rotation.md).
- **Credential revocation propagation latency.** § 2.2.3 sets the SDK's auth-failure contract; the cross-platform mechanism for proactively revoking a credential and forcing a restart is its own design (separate follow-up).

---

## 7. Change log

| Version | Date | Change |
|---|---|---|
| v0.1 | 2026-04-28 | Initial specification. Implements ADR-0029 Stage 1; consumes F1 / ADR-0030. |
| v0.1.1 | 2026-04-28 | § 2.2.3 (Credential rotation): replace "TBD in Stage 2" with the restart-as-rotation-primitive contract — per-launch minting, restart re-injection, no-in-place-mutation, SDK auth-failure contract. § 2.3, § 5 conformance updated. Long-running zero-downtime rotation deferred to a future revision; see [`docs/architecture/agent-credential-rotation.md`](../architecture/agent-credential-rotation.md). Closes #1325 (design phase). |
| v0.1.2 | 2026-04-29 | § 2.1 static metadata: add `thread_id` field (optional; present when launch originates from a known dispatch thread, absent on supervisor-driven restarts). § 2.2.1 env-var table: add `SPRING_THREAD_ID` (optional). Closes #1300 (propagation) and closes #1347 (D3d implementation: `IAgentContextBuilder.RefreshForRestartAsync`, `SupervisorState` identity fields, `ContainerSupervisorActor.RestartAsync` re-mint). |
