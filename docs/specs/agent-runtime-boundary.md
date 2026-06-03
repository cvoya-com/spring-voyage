# Agent Runtime Boundary — Contract Specification

> **Active contract specification.** The agent-runtime boundary specified here is implemented today; see the [change log](#8-change-log) for revision history and [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md) for the platform-side implementation. The full catalogue of MCP tools an agent may call is the CI-pinned reference [`docs/reference/platform-tools.md`](../reference/platform-tools.md); this spec governs the *boundary* (lifecycle hooks, bootstrap context, workspace, and the send transport), not the per-tool schemas.

- **Status:** Accepted
- **Version:** v0.2.1
- **Date:** 2026-06-03
- **Implements:** [ADR-0029 — Tenant execution boundary](../decisions/0029-tenant-execution-boundary.md), Stage 1
- **Anchors on:** [ADR-0030 — Thread model](../decisions/0030-thread-model.md), [ADR-0060 — Participant-set agent API and structured envelope](../decisions/0060-participant-set-agent-api-and-structured-envelope.md), [ADR-0064 — Conversation participants and continuation](../decisions/0064-conversation-participants-and-continuation.md)
- **Aligned with:** [ADR-0026 — Per-agent container scope](../decisions/0026-per-agent-container-scope.md), [ADR-0027 — Agent-image conformance contract](../decisions/0027-agent-image-conformance-contract.md), [ADR-0055 — Pull-based agent bootstrap](../decisions/0055-pull-based-agent-bootstrap.md), [ADR-0057 — Sidecar-local MCP server](../decisions/0057-sidecar-local-mcp-server.md)

---

## 0. Preamble

### 0.1 Scope

This specification pins the contract surfaces that sit between the Spring Voyage platform and a tenant-scoped agent container. It is **implementation-neutral**: any conforming agent runtime, SDK, or test harness — in any language — can be built against this document.

Four surfaces are specified:

1. **Bucket 1 — Agent SDK contract**: the three lifecycle hooks the SDK MUST expose to agent code (`initialize`, `on_message`, `on_shutdown`), and the shape of the inbound message envelope.
2. **`IAgentContext` payload**: the bootstrap bundle delivered to the SDK at container start, and its delivery channels (env vars + workspace files).
3. **Per-agent workspace volume**: the durable filesystem state contract.
4. **Agent → platform send path**: how an agent emits messages back into the platform — the MCP messaging tool surface reached at `SPRING_MCP_URL`.

### 0.2 Out of scope

The following are explicitly **not** specified by this document. See § 7 for the full list.

- **The per-tool MCP schemas.** The agent-facing tool catalogue (`sv.messaging.*`, `sv.memory.*`, `sv.directory.*`, …) — names, parameters, grant rules — is specified and CI-pinned in [`docs/reference/platform-tools.md`](../reference/platform-tools.md). § 5 summarises the surface and the auth model; the per-tool input/output schemas are authoritative there, not here.
- **The `context` UX hint** (`task_update` / `reminder` / `observation` / `spontaneous`, `is_first_contact`, `opening_offer`). [ADR-0030](../decisions/0030-thread-model.md) describes these as **surface-rendering cues a UX may attach**, but the platform does not branch on them and **no field for them exists on the wire today**. They are a deferred design concept, not part of this contract.
- **Multi-language SDK implementations** (each is a downstream artefact of this spec).
- **Implementation choices**: programming language, framework, transport library, supervision topology, container-runtime backend.

### 0.3 Normative language (RFC 2119)

The key words "**MUST**", "**MUST NOT**", "**REQUIRED**", "**SHALL**", "**SHALL NOT**", "**SHOULD**", "**SHOULD NOT**", "**RECOMMENDED**", "**MAY**", and "**OPTIONAL**" are to be interpreted as described in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119). Example payloads are illustrative unless explicitly marked normative.

### 0.4 References

- ADR-0029 — buckets and surfaces this spec implements: [`docs/decisions/0029-tenant-execution-boundary.md`](../decisions/0029-tenant-execution-boundary.md).
- ADR-0030 — the thread model (threads are identified by their participant set): [`docs/decisions/0030-thread-model.md`](../decisions/0030-thread-model.md).
- ADR-0060 — the participant-set agent API and the structured inbound envelope: [`docs/decisions/0060-participant-set-agent-api-and-structured-envelope.md`](../decisions/0060-participant-set-agent-api-and-structured-envelope.md).
- ADR-0064 — conversation participants and `respond_to` continuation: [`docs/decisions/0064-conversation-participants-and-continuation.md`](../decisions/0064-conversation-participants-and-continuation.md).
- ADR-0055 — pull-based agent bootstrap (the bundle the workspace files arrive in): [`docs/decisions/0055-pull-based-agent-bootstrap.md`](../decisions/0055-pull-based-agent-bootstrap.md).
- ADR-0057 — sidecar-local MCP server (the per-turn MCP token bridge): [`docs/decisions/0057-sidecar-local-mcp-server.md`](../decisions/0057-sidecar-local-mcp-server.md).
- ADR-0026 / ADR-0027 — per-agent container scope and A2A image conformance.
- Agent-facing MCP tool catalogue: [`docs/reference/platform-tools.md`](../reference/platform-tools.md).
- Platform-side runtime / dispatcher (informative): [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md).

### 0.5 Conformance summary

A platform implementation conforms when it delivers `IAgentContext` to every tenant container per § 2, provisions a per-agent workspace volume per § 3, and exposes the agent send path (the MCP messaging tools) per § 4. A tenant-side SDK conforms when it exposes the three Bucket-1 hooks per § 1, consumes `IAgentContext` per § 2, treats the workspace volume per § 3, and emits messages via the send path per § 4. § 6 carries the cross-cutting checklist.

---

## 1. Bucket 1 — Agent SDK contract (platform → tenant)

The SDK MUST expose exactly three lifecycle hooks to agent code: `initialize(context)`, `on_message(message)`, and `on_shutdown(reason)`. The platform calls these hooks (directly or via the SDK's runtime); the agent implements them. No fourth hook is part of the contract. The reference Python SDK is [`agents/spring-voyage-agent-sdk`](../../agents/spring-voyage-agent-sdk).

### 1.1 `initialize(context)`

The SDK MUST invoke `initialize(context)` exactly once per container instance, before any inbound message is delivered to `on_message`. The hook receives the `IAgentContext` payload of § 2.

- The SDK **MUST** complete `initialize` before accepting traffic on the agent's `:8999` A2A listener (per ADR-0027). The listener MAY be bound earlier, but MUST NOT invoke `on_message` until `initialize` has returned successfully.
- The agent **MAY** use this hook to open telemetry exporters, inspect the workspace for recovery state (§ 3.3), or do any setup it chooses.
- The hook **MUST** complete or fail within a recommended **30 second** window. The platform **MAY** abort the container if the window elapses. The SDK **MUST NOT** assume more than 30 seconds.
- If `initialize` fails, the SDK **MUST** surface the failure via a non-zero container exit code. The agent **MUST NOT** assume the platform will retry.
- The SDK **MUST NOT** invoke `on_message` before `initialize` completes, nor `on_shutdown` before `initialize` has returned (success or failure).

### 1.2 `on_message(message)`

The SDK MUST invoke `on_message(message)` once per inbound message dispatched to the container.

#### 1.2.1 Inbound message shape (the structured envelope)

Per [ADR-0060](../decisions/0060-participant-set-agent-api-and-structured-envelope.md), the platform delivers a **structured envelope**. CLI-based runtimes receive it as a rendered markdown header plus a fenced-JSON appendix injected into the runtime's user-message slot; SDK-based runtimes receive it as a structured object. The envelope carries:

| Field | Type | Required | Description |
|---|---|---|---|
| `message_id` | string | yes | Platform-assigned id for this message. |
| `timestamp` | string (ISO-8601) | yes | When the platform received the message. Per-thread FIFO is on this timestamp (§ 1.2.3). |
| `from` | string | yes | The sender's canonical address (`agent:`/`unit:`/`human:` + id). A flat address string — **not** a nested object. |
| `from_display_name` | string | no | Human-friendly sender label, when known. |
| `to` | string[] | yes | The recipient addresses for this delivery (receiver included, sender excluded). |
| `participants` | string[] | yes | The full routable roster of the conversation (ADR-0064). This is the set `sv.messaging.respond_to` will deliver to. |
| `payload` | object | yes | The message body — e.g. `{ "content": "..." }` or a connector-shaped object. Not wrapped in an A2A `role`/`parts` envelope. |

> **Thread identity is the participant set, not an id.** The envelope **deliberately omits `thread_id`** (#2747): a thread *is* its set of participants ([ADR-0030](../decisions/0030-thread-model.md)). Agents never name a thread id — they address peers (§ 4), and the platform resolves the thread from the participant set. There is no `sender` object, no `pending_count`, and no `context` field on the wire (§ 0.2).

Example envelope JSON (illustrative):

```json
{
  "message_id": "msg_01HJX5K2P3Q4R5S6T7U8V9W0X1",
  "timestamp": "2026-05-29T14:22:13.418Z",
  "from": "human:01HJX0000000000000000000A",
  "from_display_name": "Savas",
  "to": ["agent:01HJX...backend3"],
  "participants": ["human:01HJX...A", "agent:01HJX...backend3"],
  "payload": { "content": "re: #flaky-test-fix — try the integration test scope." }
}
```

When more than one message is pending for a thread as the runtime activates, the platform delivers them **together as one ordered batch** in a single turn — each message carries the full set of fields above, oldest-first — rather than one per turn. See § 1.2.5.

#### 1.2.2 Response semantics — chunks are a diagnostic trace, not delivery

`on_message` MAY yield zero or more **chunks** (the SDK exposes a streaming abstraction appropriate to the host language — async iterator, generator, etc.). **These chunks do not reach any participant.** The platform captures the agent's returned/streamed output as a **diagnostic reasoning trace** (telemetry); it is never synthesised into a message routed back to a sender.

An agent communicates by **calling the send tools of § 4** (`sv.messaging.*`) during `on_message`. The contract is therefore:

- To reply or message anyone, the agent **MUST** invoke a § 4 send tool. Returning text from `on_message` is **not** a way to send a message.
- Per-message processing is independent: the agent's handling of message N is independent of N+1, subject to the FIFO invariant of § 1.2.3.

#### 1.2.3 Per-thread FIFO

The platform **MUST** preserve FIFO order within a thread (a participant set): messages are delivered to `on_message` in receive order. The SDK **MUST** preserve this order and MUST NOT begin `on_message` for message N+1 before `on_message` for message N has begun. The platform makes **no** ordering promise across distinct threads.

#### 1.2.4 Concurrent threads

The agent / unit definition carries a **`concurrent_threads`** boolean (delivered as `SPRING_CONCURRENT_THREADS`, § 2.2.1):

- **`concurrent_threads: true`**: the platform MAY invoke `on_message` concurrently across distinct threads (at most one in flight per thread). The SDK **MUST** be re-entrant in this case.
- **`concurrent_threads: false`**: the platform **MUST** serialise `on_message` across all threads; at most one invocation in flight for the agent.

The flag is resolved at the agent level and delivered via `IAgentContext`. Inbound messages are **pushed** to the runtime; there is no agent-facing "drain the queue" / poll tool. A turn delivers either a single message or — when several are pending for the thread — the pending set as one ordered batch (§ 1.2.5); concurrency (this flag) governs only whether *distinct* threads run in parallel.

#### 1.2.5 Batched delivery (drain on activation)

When the platform activates the runtime for a thread and more than one message is pending in that thread's inbox, it **drains the pending messages and delivers them together as one ordered batch in a single turn** (oldest-first), rather than one message per turn. This lets the runtime reason over the *net current* state of the conversation and act once, instead of responding to an early message with already-stale context while later messages — which may update or supersede it — wait behind it.

- **Self-described.** Every message in the batch carries the full envelope of § 1.2.1 — `from`, `from_display_name`, `to`, `participants`, `message_id`, `timestamp`, `payload` — so the runtime can order and attribute each one. Because a thread *is* its participant set, a batch groups only messages from the **same conversation**: there is no participant-set context to switch between within a turn.
- **FIFO preserved.** Messages are ordered oldest-first; per-thread FIFO (§ 1.2.3) holds across batches and within a batch. A message that arrives while a turn is running is delivered in the *next* batch, never injected into the running one.
- **Atomic + idempotent.** The platform marks the whole delivered batch processed atomically and is idempotent on `message_id` — a re-delivered id (e.g. a delivery retried after its enqueue already succeeded) is not surfaced twice.
- **Bounded.** Very large backlogs (rare) are split: a turn delivers at most a platform-defined number of messages and the remainder form the next batch, so a turn's context stays manageable.
- **Still one turn.** A batch is delivered as a single runtime turn carrying the ordered set — for a CLI-based runtime, the rendered set in the user-message slot (§ 1.2.1). The runtime reasons over the whole set (handling the messages individually, grouped, or as a whole) and emits its actions once, via the § 4 send tools, issuing as many tool calls in the turn as the resulting state warrants.

This composes with the per-thread session isolation tracked for the multi-agent coordination work: batching coalesces a single thread's pending input into one coherent turn; per-thread isolation provides that turn one coherent context.

### 1.3 `on_shutdown(reason)`

The SDK **MUST** invoke `on_shutdown(reason)` exactly once on platform-initiated termination.

- The platform **MUST** signal termination via **SIGTERM** to PID 1, then wait at least the **grace window** (recommended **30 seconds**) before SIGKILL.
- The SDK **MUST** trap SIGTERM and run `on_shutdown` within the grace window.
- The agent **SHOULD** flush in-progress work to the workspace (§ 3) before returning; recovery on next start is the agent's responsibility (§ 3.3).
- After `on_shutdown` the SDK **MUST NOT** invoke `on_message`. In-flight invocations **SHOULD** be cancelled cooperatively and MUST NOT block past the grace window.

`reason` is an enum: `requested`, `idle_timeout`, `policy`, `error`, `platform_restart`, `unknown`.

### 1.4 Conformance — Bucket 1

An SDK conforms iff: it exposes the three hooks with the lifecycle of §§ 1.1–1.3; `initialize` runs before any `on_message` and is bounded by the window; `on_shutdown` runs after the last `on_message`, on SIGTERM, within the grace window; per-thread FIFO is preserved (§ 1.2.3), including across batched deliveries (§ 1.2.5); concurrency honours `concurrent_threads` (§ 1.2.4); and the agent emits messages only via § 4 tools, never via `on_message` return values (§ 1.2.2). A platform conforms to § 1.2.5 iff it delivers a thread's pending messages as one ordered, self-described, FIFO-preserving batch, marks the batch processed atomically, is idempotent on `message_id`, and bounds batch size.

---

## 2. `IAgentContext` payload

`IAgentContext` is the bootstrap bundle delivered to the SDK at `initialize`. It is **read-only data and handles**, carrying identity, scoped credentials, platform service endpoints, and the workspace mount path. The platform-side builder is `AgentContextBuilder` ([`src/Cvoya.Spring.Dapr/Execution/AgentContextBuilder.cs`](../../src/Cvoya.Spring.Dapr/Execution/AgentContextBuilder.cs)); the SDK-side reader is the Python SDK's `context.py`.

### 2.1 What's in it

The bundle is delivered as environment variables (§ 2.2.1) plus workspace files (§ 2.2.2). Field names below are normative.

#### Identity & routing

| Field | Env var | Required | Description |
|---|---|---|---|
| `tenant_id` | `SPRING_TENANT_ID` | yes | The tenant the agent runs under. |
| `agent_id` | `SPRING_AGENT_ID` | yes | The agent's stable id within the tenant. |
| `unit_id` | `SPRING_UNIT_ID` | no | The parent unit, when the agent is a member. |
| `thread_id` | `SPRING_THREAD_ID` | no | Present when the launch originates from a known dispatch; **absent on supervisor-driven restarts**. (Guaranteed on the `AgentContextBuilder`-emitted value; SDKs MUST treat absence as non-fatal.) |
| `concurrent_threads` | `SPRING_CONCURRENT_THREADS` | yes | `"true"`/`"false"` — the resolved re-entrancy policy (§ 1.2.4). |

#### Platform service endpoints (each with an agent-scoped credential)

| Field | Env vars | Required | Description |
|---|---|---|---|
| MCP endpoint | `SPRING_MCP_URL`, `SPRING_MCP_TOKEN` | yes | The platform MCP server — the agent's send path and tool surface (§ 4, § 5). **`SPRING_MCP_TOKEN` is empty on the deploy/restart path**; the per-turn session token arrives out-of-band via the workspace bridge (§ 2.2.2, ADR-0057). |
| LLM provider | `SPRING_LLM_PROVIDER_URL`, `SPRING_LLM_PROVIDER_TOKEN` | yes | The agent's primary LLM endpoint and scoped token. The provider's native API is the contract. |
| Bootstrap | `SPRING_BOOTSTRAP_URL`, `SPRING_BOOTSTRAP_TOKEN` | yes | The pull-bootstrap endpoint (ADR-0055) and an **agent-lifetime** bearer the SDK uses to fetch its bundle (the system prompt and other workspace files). |
| Telemetry | `SPRING_CALLBACK_URL`, `SPRING_CALLBACK_TOKEN`, and the derived `OTEL_EXPORTER_OTLP_*` set | conditional | OTLP ingest endpoint + bearer. Emitted when configured; omitted otherwise. |

#### Reserved — public A2A send endpoint

| Field | Env vars | Required | Description |
|---|---|---|---|
| Bucket 2 | `SPRING_BUCKET2_URL`, `SPRING_BUCKET2_TOKEN` | conditional | A **reserved** public-API A2A send surface. The token is minted per launch and the URL is emitted when configured, **but the live agent send path is the MCP messaging tools of § 4** — no current runtime sends via Bucket 2. Documented here because the variables are part of the delivered context; treat as forward-looking, not the path to use today. |

#### Workspace mount

| Field | Env var | Required | Description |
|---|---|---|---|
| `workspace_path` | `SPRING_WORKSPACE_PATH` | yes | The container path of the agent's persistent volume. Default `/spring/members/{agent_id}/` (§ 3). SDKs **MUST** read the env var, not hard-code the default. |

#### LLM selection (Dapr-agent launcher only)

`SPRING_MODEL`, `SPRING_LLM_PROVIDER`, `SPRING_LLM_COMPONENT` — optional, emitted only by the Spring Voyage Agent launcher for telemetry / Dapr Conversation component selection (per [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)). Other runtimes MUST ignore them.

### 2.2 How it's delivered

Two channels, both readable synchronously at the top of `initialize`: **environment variables** (scalars, URLs, credentials) and **workspace files** (multi-line payloads such as the system prompt).

#### 2.2.1 Canonical environment variables (normative)

The platform **MUST** populate every required env var before PID 1 begins. The SDK **MUST** treat a missing/empty required var as a fatal `initialize` error.

| Env var | Required | Notes |
|---|---|---|
| `SPRING_TENANT_ID` | yes | |
| `SPRING_AGENT_ID` | yes | |
| `SPRING_UNIT_ID` | no | Omitted for standalone agents. |
| `SPRING_THREAD_ID` | no | Absent on supervisor restarts (§ 2.1). |
| `SPRING_CONCURRENT_THREADS` | yes | `"true"`/`"false"`. |
| `SPRING_MCP_URL` | yes | |
| `SPRING_MCP_TOKEN` | yes (may be empty) | Empty on deploy/restart; per-turn token via the bridge (§ 2.2.2). |
| `SPRING_LLM_PROVIDER_URL` | yes | |
| `SPRING_LLM_PROVIDER_TOKEN` | yes | Per-launch minted. |
| `SPRING_BOOTSTRAP_URL` | yes | Pull-bootstrap endpoint (ADR-0055). |
| `SPRING_BOOTSTRAP_TOKEN` | yes | Agent-lifetime bearer. |
| `SPRING_WORKSPACE_PATH` | yes | |
| `SPRING_BUCKET2_URL` | conditional | Reserved (§ 2.1); omitted when unconfigured. |
| `SPRING_BUCKET2_TOKEN` | conditional | Per-launch minted. |
| `SPRING_CALLBACK_URL` / `SPRING_CALLBACK_TOKEN` | conditional | OTLP ingest; present when telemetry is configured. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `_PROTOCOL` / `_HEADERS`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | conditional | Derived from the callback vars for OpenTelemetry export. |
| `SPRING_MODEL` / `SPRING_LLM_PROVIDER` / `SPRING_LLM_COMPONENT` | no | Dapr-agent launcher only. |

> **Below the boundary.** Each CLI-runtime launcher (Claude Code, Codex, Gemini) injects additional, runtime-specific variables — e.g. `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_OAUTH_TOKEN`, `OPENAI_API_KEY`, `GOOGLE_API_KEY`, `GEMINI_CLI_HOME`, `SPRING_AGENT_ARGV`, `SPRING_THREAD_ID_ARG_{CREATE,RESUME}`. These configure a specific CLI/bridge and are **not** part of the implementation-neutral contract; a custom SDK neither receives nor needs them.

#### 2.2.2 Workspace files and the `.spring/` namespace (normative)

Files are not statically mounted — they arrive via the **ADR-0055 pull bundle**: each launcher contributes workspace-relative files, and the SDK/bridge pulls them from `SPRING_BOOTSTRAP_URL` on container start. The platform owns the `.spring/` namespace under the workspace mount:

| Path | Purpose |
|---|---|
| `$SPRING_WORKSPACE_PATH/.spring/system-prompt.md` | The platform-assembled system prompt (instructions + identity + equipped skills + connector context). **Required.** |
| `$SPRING_WORKSPACE_PATH/.spring/bridge/mcp-token` | The **per-turn** MCP session token, written atomically (mode 0600) by the A2A sidecar before each runtime spawn (ADR-0057). This — not `SPRING_MCP_TOKEN` — is the live MCP credential for CLI runtimes. |
| `$SPRING_WORKSPACE_PATH/.spring/bridge/seen-threads` | Sidecar thread-session binding state. |
| `$SPRING_WORKSPACE_PATH/.spring/connectors/<slug>/` | Connector-contributed context files (ADR-0055/0058). |

SDKs **MUST** ignore files under `.spring/` they do not recognise. How the system prompt reaches the model is launcher-specific and **not** uniform auto-discovery:

- **Spring Voyage Agent SDK** — the SDK reads `.spring/system-prompt.md` and exposes it as `IAgentContext.system_prompt`.
- **Claude Code** — passed on argv via `--append-system-prompt-file` (or `--system-prompt-file` when the agent declares `system_prompt_mode: replace`). The CLI's own `CLAUDE.md` auto-discovery is deliberately bypassed so it does not collide with a cloned project's `CLAUDE.md`.
- **Codex** — written to `AGENTS.md` at the workspace root (Codex auto-discovers it; it has no system-prompt flag).
- **Gemini** — `GEMINI.md` at the root (append mode) or `.spring/system-prompt.md` pointed at by `GEMINI_SYSTEM_MD` (replace mode).

#### 2.2.3 Credential rotation

Scoped credentials (`SPRING_LLM_PROVIDER_TOKEN`, `SPRING_BUCKET2_TOKEN`, and the per-turn MCP token) are minted **per container launch / per turn** and MUST be valid for that lifetime. **Restart is the rotation primitive**: to rotate, the platform performs a clean stop + restart that re-runs the `IAgentContext` build path, re-minting fresh credentials.

- **Per-launch minting (MUST).** Every launch — including supervisor restarts — sees freshly minted scoped credentials. The previous launch's credentials MUST NOT be replayed.
- **No in-place mutation (MUST).** The platform MUST NOT mutate a running container's env/files to change credentials; new credentials reach the container only via a new launch (or, for the per-turn MCP token, via the bridge file written before each spawn).
- **SDK re-read (MUST).** The SDK MUST read credentials at the top of every `initialize`. It MUST treat an auth failure against a platform endpoint (MCP, LLM) as a fatal in-flight error — surface it and fail / signal the supervisor — never silently retry.
- **TTL guidance (informative).** Operators SHOULD size token TTLs to exceed the idle-eviction window so a healthy container never observes mid-run expiry.

### 2.3 Conformance — `IAgentContext`

A platform conforms when every required env var (§ 2.2.1) is populated before container start, the required workspace file (§ 2.2.2) is present and readable, every credential is agent-scoped, `SPRING_CONCURRENT_THREADS` matches the resolved definition, and every launch sees freshly minted credentials (§ 2.2.3). An SDK conforms when it reads the env/files at the top of `initialize`, surfaces missing required fields as fatal, and re-reads credentials per launch.

---

## 3. Per-agent workspace volume

The platform **MUST** grant every agent exactly one persistent filesystem volume. It is the agent's durable state primitive — no KV interface, no platform serialisation shape, no `on_recover` hook.

### 3.1 Mount and naming

- Mounted at `SPRING_WORKSPACE_PATH` — default `/spring/members/{agent_id}/`. SDKs **MUST** read the env var.
- Writable to the container's UID.
- Private to the agent. The platform **MUST NOT** mount one agent's workspace into another's container.

### 3.2 Lifetime

- Persists across all container restarts (crashes, redeploys, image upgrades, host migration).
- Reclaimed only on explicit agent deletion, or when an ephemeral clone is reaped (cloning policy is governed by [`docs/architecture/units-and-agents.md`](../architecture/units-and-agents.md)).
- The platform **MUST NOT** silently truncate or snapshot-revert the volume.

### 3.3 Recovery is agent-owned

The platform **MUST NOT** pass a `state` parameter, provide an `on_recover` hook, or signal "recovering vs. fresh." The agent **MUST** inspect the workspace during `initialize` to determine recovery state (checkpoint files, transaction markers, etc.). The agent knows its own state shape; a platform-level recovery contract would dictate it or be vacuous.

### 3.4 Cross-agent transfer

Cross-agent state exchange flows through messages (§ 4), not volume sharing. The volume layer is single-writer / single-reader.

### 3.5 Platform-side concerns (informative)

The platform owns quotas (SDKs MUST handle `ENOSPC` gracefully), encryption-at-rest, backup/snapshot cadence (agents that care MUST checkpoint themselves), and host migration.

### 3.6 Conformance — Workspace volume

Conforms when: every agent has exactly one volume at `SPRING_WORKSPACE_PATH`; it is writable, private, and survives restart (testable); and quota errors surface as standard filesystem errors. An SDK conforms when it reads the path from the env var and inspects the volume for recovery state during `initialize`.

---

## 4. Agent → platform send path

An agent emits messages by calling the platform's **MCP messaging tools** over JSON-RPC 2.0 `tools/call`. This is the only way an agent communicates with participants (§ 1.2.2).

### 4.1 Transport

- The send transport is the **platform MCP server**, reached at `SPRING_MCP_URL` (JSON-RPC 2.0 over HTTP) with the bearer token from § 2.2.2 (`SPRING_MCP_TOKEN`, or the per-turn `.spring/bridge/mcp-token` for CLI runtimes).
- There is **no gRPC binding and no streaming-frame protocol** for the send path. A call returns a single JSON acknowledgement (§ 4.3).
- **A2A 0.3.x runs in the opposite direction.** The platform dispatcher is the A2A *client*; the agent container is the A2A *server* on `:8999`, receiving dispatch (`message/send`, `tasks/cancel`, `tasks/get`). The A2A response the agent returns is captured as a diagnostic reasoning trace (§ 1.2.2), not a routed message. A2A is **platform → agent**, never the agent's send path.

### 4.2 The messaging tools

Three tools form the send surface (authoritative schemas in [`docs/reference/platform-tools.md`](../reference/platform-tools.md)):

| Tool | Arguments | Semantics |
|---|---|---|
| `sv.messaging.send` | `message` (required); exactly one of `recipients` (canonical `agent:`/`unit:`/`human:` addresses) **or** `scope` (`"unit-members"` \| `"siblings"`); `reason?` | One shared thread over `{caller} ∪ recipients`. |
| `sv.messaging.multicast` | same shape as `send` | N independent 1-to-1 threads, one per recipient. |
| `sv.messaging.respond_to` | `message_id` (required), `message` (required), `reason?` | Continue an existing conversation; the platform derives recipients from that message's participant roster (ADR-0064). |

Invariants (normative):

- The caller is **always auto-included** and **MUST NOT** list itself in `recipients`.
- An agent **never names a `thread_id`** — the platform resolves the thread from the participant set (§ 1.2.1, ADR-0030).
- Recipient kind is restricted to `agent` / `unit` / `human`; a `connector:` recipient is rejected synchronously.
- **Delivery is one-way.** The call returns a delivery acknowledgement; any reply arrives later as a separate inbound message (§ 1.2.1).
- `delegate_to` / `fanout_to` do not exist (removed in #2578); fan-out is `sv.messaging.multicast`.

### 4.3 Response

Each call returns a single JSON object (not a stream):

```json
{
  "messageId": "msg_...",
  "threadId": "thr_...",
  "deliveries": [
    { "target": "agent:01HJX...backend3", "delivered": true, "threadId": "thr_...", "error": null }
  ]
}
```

(`multicast` returns per-recipient `threadId`s; `respond_to` returns the continued thread.)

### 4.4 Auth

- The MCP bearer is an opaque, **agent- and thread-scoped session token** minted by the platform per turn; it is validated by direct lookup, not as a signed JWT.
- Every `tools/call` is gated by an effective-grant check (the agent must have been granted the tool) and unit-policy enforcement. `tools/list` is filtered to the granted set, so an agent never sees a tool it cannot call.
- Tenant scoping is ambient: an agent cannot send on behalf of, or read the memory/history of, another subject.

### 4.5 Conformance — send path

A platform conforms when: the messaging tools are reachable over MCP JSON-RPC at `SPRING_MCP_URL`; the auth/grant model of § 4.4 is enforced; delivery is one-way with the acknowledgement shape of § 4.3; and the agent need not name a thread id. An agent conforms when it emits all participant-visible communication via these tools (never via `on_message` return values).

---

## 5. Agent-facing MCP tool surface

Beyond the send path (§ 4), the platform MCP server exposes a broader tool surface to agents. **The authoritative, CI-pinned catalogue is [`docs/reference/platform-tools.md`](../reference/platform-tools.md)** — names, parameters, grant rules, and owning registries. This spec does not duplicate it; the categories are:

| Namespace | Purpose |
|---|---|
| `sv.messaging.*` | Outbound send (§ 4): `send`, `multicast`, `respond_to`. |
| `sv.memory.*` | Private per-agent memory (`add`, `get`, `list`, `search`, `update`, `delete`) and shared participant-set history (`engagements`, `history_with`, `search_messages`). |
| `sv.directory.*` | Membership / roster / status lookups (`list`, `lookup`, `get_self`, `get_member`, `list_members`, `get_siblings`, `get_parents`, `get_status`). |
| `sv.expertise.*` | Expertise search (`search`) plus dynamically published per-skill tools (`sv.expertise.<slug>`). |
| `sv.progress.*` / `sv.runtime.*` | Progress reporting and decision-trace observability. |
| `sv.tools.*` | Tool self-discovery (`list_categories`, `list`). |
| Connector tier (`github.*`, `arxiv.*`, `websearch.*`, …) | Gated by an active connector binding on the owning/ancestor unit. |

The per-category *usage guidance* an agent receives from `sv.tools.list(<category>)` — the "when to reach for each tool" prose, not the per-tool schemas — is the single source of truth `PlatformToolCatalog`, reproduced and CI-pinned in [`docs/reference/platform-tools.md` § "Category usage guidance"](../reference/platform-tools.md#category-usage-guidance--svtoolslistcategory). This spec summarises the namespaces only; that section is authoritative for the guidance text.

> **Superseded names.** Earlier drafts of this spec referenced `store`, `recall`, `peek_pending`, `message.retract`, and `sv.thread.*`. None exist under those names: memory is `sv.memory.add` / `sv.memory.search`; conversation history is `sv.memory.engagements` / `history_with` / `search_messages`; there is no poll-the-queue tool (inbound is pushed per § 1.2.4) and no message-retraction tool (retraction is a persistence concept surfaced as a badge, not an agent-callable action).

---

## 6. Conformance summary

A future conformance suite (out of scope) exercises:

**Bucket 1 — SDK hooks**
- [ ] Three hooks present with the lifecycle of § 1.
- [ ] `initialize` completes before any `on_message`; bounded by the window (§ 1.1).
- [ ] Inbound envelope conforms to § 1.2.1 (participant-set shape; no `thread_id`).
- [ ] The agent emits messages only via § 4 tools; `on_message` return values are treated as diagnostic traces (§ 1.2.2).
- [ ] Per-thread FIFO preserved (§ 1.2.3); concurrency honours `concurrent_threads` (§ 1.2.4).
- [ ] A thread's pending messages are delivered as one ordered, self-described batch in a single turn; the batch is marked processed atomically, idempotent on `message_id`, and bounded (§ 1.2.5).
- [ ] `on_shutdown` runs on SIGTERM within the grace window (§ 1.3).

**`IAgentContext`**
- [ ] Every required env var (§ 2.2.1) read at the top of `initialize`.
- [ ] `.spring/system-prompt.md` present under the workspace mount (§ 2.2.2).
- [ ] Credentials agent-scoped; freshly minted per launch; never cached across launches (§ 2.2.3).

**Workspace volume**
- [ ] Mounted at `SPRING_WORKSPACE_PATH`, writable, private; survives restart (§ 3).
- [ ] No platform `state` / `on_recover` — recovery is agent-owned (§ 3.3).

**Send path**
- [ ] Messaging tools reachable over MCP JSON-RPC at `SPRING_MCP_URL`; auth/grant model enforced (§ 4.4).
- [ ] Delivery one-way; acknowledgement shape per § 4.3; agent never names a thread id.

---

## 7. Out of scope

- **Per-tool MCP schemas** — specified and CI-pinned in [`docs/reference/platform-tools.md`](../reference/platform-tools.md).
- **The `context` UX hint** (`task_update`/`reminder`/`observation`/`spontaneous`, `is_first_contact`, `opening_offer`) — a deferred [ADR-0030](../decisions/0030-thread-model.md) design concept; no wire field exists.
- **The public A2A "Bucket 2" send endpoint** — reserved (§ 2.1); the live agent send path is § 4. A future revision MAY specify it if it becomes a real surface.
- **Multi-language SDK implementations** — downstream artefacts of this spec.
- **Implementation choices** — language, framework, transport library, supervision topology, container-runtime backend.
- **Long-running zero-downtime credential rotation** — § 2.2.3 specifies restart-as-rotation; an in-place refresher is future work.

---

## 8. Change log

| Version | Date | Change |
|---|---|---|
| v0.1 | 2026-04-28 | Initial specification. Implements ADR-0029 Stage 1; consumed F1 / ADR-0030. |
| v0.1.1–v0.1.5 | 2026-04-28 → 2026-05-24 | Credential-rotation contract (restart-as-primitive); `thread_id` / `SPRING_THREAD_ID` added; Dapr-agent LLM-selection fields; ADR-0038 alignment; dropped the retired `/spring/context/` mount in favour of `$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`. |
| v0.2.1 | 2026-06-03 | **Batched delivery (#3056).** Added § 1.2.5: on activation the platform drains a thread's pending inbox and delivers the messages as one ordered, self-described batch in a single turn (oldest-first), so the runtime reasons over the net current state instead of a stale prefix — per-thread FIFO preserved across and within batches, the batch marked processed atomically and idempotent on `message_id`, and bounded for very large backlogs. Revised § 1.2.4's "one message per invocation" line and forward-referenced the batch from § 1.2.1; extended the § 1.4 / § 6 conformance criteria. Companion v0.1 mitigation to the multi-agent coordination breakdown (#3053); composes with the per-thread session isolation tracked there. |
| **v0.2** | **2026-05-29** | **Implementation-alignment pass.** Rewrote the send path (§ 4) around the **MCP messaging tools** (`sv.messaging.send`/`multicast`/`respond_to`) — removed the non-existent gRPC binding, the streaming `ack`/`progress`/`complete`/`error` frame protocol, and the claim that A2A 0.3.x is the agent→platform protocol (it is platform→agent dispatch; A2A replies are diagnostic traces). Rewrote the inbound shape (§ 1.2.1) to the **participant-set structured envelope** (ADR-0060): `message_id`/`timestamp`/`from`/`to`/`participants`/`payload`, **no `thread_id`** (#2747), no `sender` object, no `pending_count`. Clarified that `on_message` return values are diagnostic traces, not delivery (§ 1.2.2). Removed the `context` UX hint from the wire contract (→ out-of-scope). Completed the env-var table (`SPRING_BOOTSTRAP_*`, `SPRING_CALLBACK_*`, `OTEL_*`; `SPRING_MCP_TOKEN` empty-on-deploy) and corrected the workspace default to `/spring/members/{agent_id}/`. Documented the `.spring/` namespace and per-launcher system-prompt delivery. Demoted `SPRING_BUCKET2_URL` to a reserved surface. Replaced the stale `store`/`recall`/`peek_pending`/`message.retract`/`sv.thread.*` names with a pointer to the CI-pinned tool catalogue (§ 5). |
