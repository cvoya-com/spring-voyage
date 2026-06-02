# 0056 — Tool calls are the only side-effect channel; stdout is reasoning trace

- **Status:** Approved — 2026-05-22. Removes the host-side synthesis of "response" messages from runtime terminal output. Every effect a runtime has on the world outside its container — messaging, task updates, UI cards, future primitives — happens through a platform tool call. Runtime stdout and A2A task replies are captured as a reasoning trace for diagnostics, never as messages on a thread.
- **Date:** 2026-05-22
- **Related ADRs:** [0053](0053-units-are-agents-and-one-way-delivery.md) — domain messaging is one-way and the platform owns no orchestration policy; this record removes the last remaining host-side surface that violated that model (the synthesised dispatch response). [0054](0054-one-mcp-server-one-execution-host.md) — the platform MCP server is the single tool surface; this record makes that surface the *only* effect channel and adds a category/discovery layer to keep its growth manageable. [0048 (archived)](archive/0048-event-vs-request-message-semantics.md) — established one-way semantics for domain messaging; this record honours that contract end-to-end. [0049 (archived)](archive/0049-message-delivery-tool-contract.md) — `sv.messaging.send` returns a delivery ack, not a reply; this record extends the same shape to every side-effect primitive.
- **Related code:** `src/Cvoya.Spring.Core/Execution/IExecutionDispatcher.cs`, `src/Cvoya.Spring.Dapr/Execution/AgentDispatchCoordinator.cs`, `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs` (the synthesis at line ~1409), `src/Cvoya.Spring.Dapr/Messaging/MessagingToolHandlers.cs`, `src/Cvoya.Spring.Core/Capabilities/ActivityEventType.cs`, `src/Cvoya.Spring.Core/Execution/IAgentRuntimeLauncher.cs` (`AgentResponseCapture` enum).

## Context

The observed bug. A human sends "Hello" to agent `d1c7b410…`. The thread persists two rows: the inbound `Hello` and an outbound `Hello! How can I help you today?` addressed back to the human. The activity log records three events: `MessageReceived`, `ThreadStarted`, `WorkflowStepCompleted "Dispatch response recorded on thread."` — and **no `MessageSent`**. The outbound row appears on the thread but is invisible as a sent action on the agent's activity stream.

The cause is structural, not a missing emission. `A2AExecutionDispatcher` (~line 1409) synthesises a `Message` from the runtime container's terminal text and flips `From`/`To` so it looks like the agent replied:

```csharp
return new SvMessage(
    Id: Guid.NewGuid(),
    From: originalMessage.To,        // the agent
    To:   originalMessage.From,      // the original sender
    Type: MessageType.Domain,
    ThreadId: originalMessage.ThreadId,
    Payload: payload,                // { Output, ExitCode }
    Timestamp: DateTimeOffset.UtcNow);
```

`AgentDispatchCoordinator.RecordResponseAsync` then persists that row to `spring.messages` and emits a neutral `WorkflowStepCompleted` activity. The result conflates three distinct things: a runtime lifecycle event (the container produced text and exited), a routing decision the agent never made (it didn't call `sv.messaging.send`), and a thread message that exists in storage without a sender event that explains it.

This synthesis was load-bearing before [ADR-0054](0054-one-mcp-server-one-execution-host.md). Pre-`sv.messaging.*`, the only output channel a runtime had was its terminal text; the host had to harvest it or the human would see nothing. With `sv.messaging.send` now first-class, the synthesis is a legacy bridge that contradicts [ADR-0053](0053-units-are-agents-and-one-way-delivery.md)'s one-way semantics ("the response is recorded on the thread and never routed back to `Message.From`" — recorded *as if* routed back, from the human's view).

The forcing question: what is the runtime allowed to do to the outside world, and through which channel? Today the answer is "call tools, **and also** have its terminal text turned into a message by the host." This record makes it: **only** tool calls. Terminal text becomes diagnostic data.

## Decision

### 1. Tool calls are the only side-effect channel

A runtime container affects the outside world exclusively through platform tool calls. Sending a message, updating a task, emitting a UI card, recording a note — every observable outcome — is a tool invocation against the platform MCP surface ([ADR-0054](0054-one-mcp-server-one-execution-host.md)). A runtime that emits only terminal text has produced **no** outside-world effect; the text is diagnostic.

The platform stops inferring intent from terminal text. The host neither synthesises a message envelope from stdout / A2A task replies nor delivers stdout to any recipient.

### 2. Runtime output becomes a reasoning trace

Stdout, A2A task replies, and file-capture buffers are captured as a *reasoning trace* — a low-priority diagnostic stream the platform records but never acts on. It surfaces in the activity log as a `RuntimeReasoning` event (capture-level controlled per [ADR-0054](0054-one-mcp-server-one-execution-host.md)'s OTLP capture knobs) and is visible in the portal alongside the runtime's lifecycle events.

The reasoning trace is for humans debugging a turn — *"what did the runtime conclude before calling these tools?"* — not for downstream routing. Removing the synthesis path closes the silent-effect surface where a runtime "said something" that no `MessageSent` event explained.

### 3. `IExecutionDispatcher` returns a lifecycle outcome, not a message

```csharp
public interface IExecutionDispatcher
{
    Task<RuntimeOutcome> DispatchAsync(
        Message inboundMessage,
        PromptAssemblyContext? context,
        CancellationToken ct = default);
}

public sealed record RuntimeOutcome(
    int ExitCode,
    TimeSpan Duration,
    string? ReasoningTrace,
    IReadOnlyDictionary<string, object?> Diagnostics);
```

`Task<Message?>` is deleted. The dispatcher's contract is *"run the runtime; tell me how it terminated"*; it does not return anything the host treats as a message. The `AgentResponseCapture` enum collapses from a delivery-mode selector into a trace-capture selector (A2A task body, stdout, file) — each mode reads bytes and produces a `ReasoningTrace`, none of them produce a `Message`.

### 4. Multi-action turns are the general case

A single inbound message may produce N tool calls of any mix: two `sv.messaging.send` calls back to the original sender, three more to three other agents, a `sv.task.update`, a `sv.notes.append`. The runtime's agentic loop iterates as long as the runtime decides — the platform exposes primitives, never gates how many of them a turn may use beyond per-tool authorisation (hop budgets, grants, rate limits) that already exist.

No "single response" assumption survives this record. The host treats a turn as *"a runtime ran; here are the tool calls it made, here is its reasoning trace, here is how it exited"*.

### 5. Compliance gap: tolerate, surface, never auto-wrap

When the runtime invokes no tools and produces only terminal text — i.e. the user sees nothing on the thread — the platform:

- Emits a `RuntimeCompletedSilent` activity on the agent's stream so the silence is diagnosable (timestamp, duration, exit code, reasoning-trace summary).
- Does **not** synthesise a message. The fix for "runtime said something but no one saw it" is a runtime/prompt bug, not a host responsibility.

Auto-wrapping silent runtimes — restoring the synthesis behind a longer name — is the seductive trap this decision explicitly rejects. Tolerating non-compliance keeps the activity log honest about what the runtime did, makes the prompt's clarity the load-bearing surface (where it belongs), and avoids hiding regressions in runtime behaviour behind host-side rescue.

### 6. Tool catalog is organised into categories, discovered at runtime

A flat tool catalog scales linearly in system-prompt tokens as new primitives land. To bound that growth the catalog is organised into **categories** (`messaging`, `directory`, `memory`, `ui`, `notes`, `tasks`, …). The system prompt lists the *categories* the agent has access to plus the discovery tools — not the per-tool schemas of every category. The agent's runtime calls the discovery tools when it needs a category's surface:

```
sv.tools.list_categories      → categories the agent has access to + one-line descriptions
sv.tools.list(category)       → full tool definitions in the category (name + description + JSON schema)
                                plus category-level usage guidance ("when/how to use these tools")
```

The listing tool returns **full tool definitions**, not just names. This mirrors MCP's `tools/list` convention (name + description + `inputSchema` always travel together) and enforces a "no tool known by name alone" invariant: if a runtime has heard of a tool, it already has everything it needs to call it. A separate `sv.tools.describe(tool)` would be redundant and is intentionally absent.

Category-level guidance — *when* and *how* to use the tools in a category — is returned alongside the tool definitions, so the runtime gets context the moment it asks for a category, not as static prompt overhead it pays per-turn.

Categories are themselves grants: a unit/agent's tool grants slice the catalog by category as well as by individual tool. A default conversational bundle grants the `messaging` category; richer roles grant more. The prompt budget for tool definitions becomes proportional to the categories the runtime actually queries, not to the platform-wide primitive count.

The MCP server already enumerates available tools; this decision layers a category index on top and pushes per-tool schemas behind the discovery call. This is a presentation/disclosure choice, not a re-architecture of the MCP surface.

### 7. Activity model: typed events per phase

Activity types align with the phases a turn actually has, rather than the synthesised request/response model:

| Activity | Source | When | Replaces / Adds |
|---|---|---|---|
| `MessageArrived` | recipient | Message lands in mailbox (routing event) | Rename of today's `MessageReceived` (semantics tightened to "mailbox arrival") |
| `MessageDispatchedToRuntime` | recipient | Mailbox hands message to runtime | New — lifecycle |
| `RuntimeStarted` / `RuntimeCompleted` / `RuntimeFailed` | recipient | Runtime container lifecycle | New — lifecycle, carries exit code, duration, token cost |
| `RuntimeCompletedSilent` | recipient | Runtime exited with no tool calls | New — compliance gap (§5) |
| `RuntimeReasoning` | recipient | Captured stdout / A2A trace | New — diagnostic, capture-level controlled |
| `MessageSent` | sender | Runtime called `sv.messaging.send` / `multicast` | Unchanged shape; now the **only** path that produces it |
| `TaskUpdated` / `NotebookUpdated` / `CardEmitted` / … | sender | Runtime called a side-effect primitive | New per primitive — each tool emits its typed activity |
| ~~`WorkflowStepCompleted "Dispatch response recorded on thread."`~~ | — | — | **Deleted** |

Every observable outcome is a typed activity emitted by exactly one code path — the tool handler. There is no longer a class of activities that the host invents on the runtime's behalf.

### 8. Default skill bundle: a fundamental core plus discoverable categories

A naive conversational use case — *"I just want a chatbot"* — shouldn't require an operator to know about `sv.messaging.send`. The platform ships a default skill bundle (working name `sv.conversational.defaults`) that grants and **pre-loads a fundamental core** of tools (always present in the system prompt, no discovery call required), plus the *category index* for everything else (loaded only when the runtime queries it).

#### Fundamental-core criterion

A tool is fundamental — and therefore worth its system-prompt budget cost on every turn — if its absence breaks the runtime's ability to do any of:

- (a) **Reply** on the thread its turn was triggered by.
- (b) **Discover** the rest of the catalog.
- (c) **Address** other members it might want to message (members, siblings, role/expertise lookup).
- (d) **Report progress** mid-turn so humans and upstream agents see signal before the turn completes.

By this criterion, the v0.1 fundamental core is:

| Tool | Why fundamental |
|---|---|
| `sv.messaging.send` | (a) The only way to reply on a thread. |
| `sv.messaging.multicast` | (a) Fan-out variant. Same category as `send`; pre-loading both gives the runtime the messaging surface in one block. |
| `sv.tools.list_categories` | (b) Without it, the runtime cannot enumerate what else exists. |
| `sv.tools.list` | (b) Per-category listing of full tool definitions + usage guidance (§6). Supersedes a separate `describe` call. |
| `sv.directory.list` *(working name; accepts a scope and optional role/expertise filter)* | (c) Resolves "who can I reach?" — members of the caller's unit, siblings, peers matching a role or expertise. Returns each entry with the info needed to act on it (address + role + expertise + status). |
| `sv.directory.lookup(address)` *(working name)* | (c) Resolves a known address (e.g. the sender of the inbound message) to its role / expertise / status. Cheap, frequent: every turn that wants to reason about who sent it uses this. |
| `sv.progress.report(message, fraction?)` | (d) Emits a `RuntimeProgress` activity (existing event type, see [ADR-0054](0054-one-mcp-server-one-execution-host.md)'s OTLP capture surface) so a long-running turn isn't silent until completion. |

Everything else — `sv.task.*`, `sv.notes.*`, `sv.ui.card.*`, connector-side primitives — sits behind discovery in its own category. (**Amended 2026-06-02:** `sv.memory.*` is no longer in this set — the full memory surface was promoted into the in-prompt core; see the **Memory** amendment under "Candidates flagged for discussion" below.)

Exact tool names under `sv.directory.*` are working names; the implementation PR refines the API shape (e.g. whether `list` and `lookup` collapse into one tool with optional `address`, whether scope and filter are one parameter or two). What's load-bearing here is *the capability* — find members + describe by address — not the call signature.

#### Candidates flagged for discussion (not in v0.1 core, see notes)

- **Identity introspection** (`sv.directory.who_am_i` or equivalent). Per [ADR-0055](0055-pull-based-agent-bootstrap.md) the agent definition and runtime context arrive via the bootstrap bundle (assembled prompt + workspace context files), so identity is *pushed* on every turn rather than queried. A runtime-time query is redundant under that model. Recommendation: **not** fundamental.
- **Decision reporting** (`sv.runtime.report_decision`). An observability primitive — the runtime annotates *why* it took an action. Useful for richer traces but not load-bearing for the action itself, and overlaps with `sv.progress.report` for in-turn signal. Recommendation: **not** fundamental.
- **Thread context** (`sv.thread.history` or similar). Prior messages are assembled into the prompt context already (prompt assembler + [ADR-0055](0055-pull-based-agent-bootstrap.md) bootstrap). A runtime-time query is duplication unless a workflow needs more history than the prompt budget carried. Recommendation: **not** fundamental for v0.1; revisit if long-thread workflows hit prompt-budget limits.
- **Memory** (`sv.memory.*` — store/retrieve goals, tasks, notes, learned facts across threads). Conceptually load-bearing for the platform's stateful-agent positioning, but **the strict criterion is "needed to complete a turn"** and a runtime can complete a turn without persistent memory. Recommendation: **category, not core.** Grant the `memory` category in `sv.conversational.defaults` so it appears in the categories listing — the runtime sees memory exists and can pull its tools on demand — but don't pre-load the per-tool schemas. Memory is *distinct* from `thread`: thread is per-work-item context (already in the prompt); memory is cross-thread state (goals, persistent notes, accumulated learning). The split is intentional.

  > **Amendment (2026-06-02) — `sv.memory.*` promoted into the in-prompt fundamental core.** Per the "promotions land as ADR amendments, not silently" rule under [Revisit criteria](#revisit-criteria), this records a promotion. The original "category, not core" call relied on runtime discovery surfacing the durable store; **in practice agents never discovered it, and cross-turn memory loss followed** — a real multi-agent run forgot prior work and disavowed its own earlier messages ([#2980](https://github.com/cvoya-com/spring-voyage/issues/2980)). [ADR-0065](0065-agent-memory-architecture.md) (Decision 3) accordingly requires the concrete `sv.memory.*` tool surface to be *auto-injected, not left to discovery*. The system prompt's platform-tool catalog now enumerates the **full** `sv.memory.*` surface — both the durable-store CRUD tools and the shared participant-set history tools — and the contract's durable-memory clause names the durable-store tools inline and *actively promotes* recall-at-turn-start / record-before-turn-end. The strict "needed to complete a turn" criterion still holds for *mechanical* turn completion, but the platform's stateful-agent positioning makes durable memory load-bearing *across* turns, which the in-prompt core now reflects. The `memory` category remains granted by `sv.conversational.defaults` (the grant is unchanged); what changed is that the surface is advertised in-prompt rather than discovered.
  >
  > This amendment deliberately does **not** reproduce the tool names — an inline list is bound to drift (as the §8 fundamental-core table above already has: it predates `sv.messaging.respond_to` and the shared-history memory tools, which ship in the in-prompt core today). The authoritative inventory is [`docs/reference/platform-tools.md`](../reference/platform-tools.md) — CI-pinned to the live `ISkillRegistry` set and to the single-source-of-truth [`PlatformToolCatalog`](../../src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs) introduced in [#3011](https://github.com/cvoya-com/spring-voyage/pull/3011). The exact in-prompt subset is whatever [`PlatformPromptProvider`](../../src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs) renders, pinned by `PlatformPromptProviderTests`. **Treat the §8 fundamental-core table above as the original-decision snapshot, not the maintained list** — consult those sources for the current surface.

#### Platform-prompt authority

The system prompt the platform assembles has layers (platform → unit → agent → user). The **platform layer is authoritative**: it states the contract the runtime is operating under and must take precedence over any conflicting guidance in higher layers. Implementers of the prompt assembler and of `sv.conversational.defaults` must surface the platform-layer fragment in a way the runtime treats as non-negotiable — concretely, the fragment begins with an explicit precedence header so model-trained instruction-following surfaces it as load-bearing:

```
[PLATFORM CONTRACT — NON-NEGOTIABLE]
These instructions define how this runtime communicates with the platform
and with other participants. They take precedence over any conflicting
guidance in the rest of the prompt and must be followed on every turn.
```

This is not stylistic. The synthesis-removed model in this ADR depends on the runtime actually using the tools — the platform has stopped catching its silence. A platform-layer fragment that reads as advice rather than contract is the failure mode this header guards against.

#### Prompt fragment (working text, subject to iteration)

The `sv.conversational.defaults` bundle contributes a platform-layer prompt fragment along the lines of:

> **[PLATFORM CONTRACT — NON-NEGOTIABLE]**
>
> *These instructions define how this runtime communicates with the platform and with other participants. They take precedence over any conflicting guidance in the rest of the prompt and must be followed on every turn.*
>
> *1. Your terminal output is captured for diagnostics only and is not delivered to anyone. Every side effect — including replying to whoever started this turn — happens through a tool call.*
>
> *2. The following tools are always available and may be called as often as needed in a single turn:*
> - *`sv.messaging.send`, `sv.messaging.multicast` — reply or send to other participants.*
> - *`sv.directory.list`, `sv.directory.lookup` — find or describe members, siblings, and peers by role / expertise / address.*
> - *`sv.progress.report` — emit a progress signal if you are doing extended work in this turn.*
> - *`sv.tools.list_categories`, `sv.tools.list` — discover additional capability categories and their tools.*
>
> *3. Additional capabilities are organised into categories the discovery tools enumerate; call `sv.tools.list(<category>)` to retrieve the full tool definitions and usage guidance for a category.*

The prompt's exact wording will iterate as we learn how runtimes interpret it; what locks in here is the **structure** (fundamental core named inline; everything else behind discovery), the **contract** (terminal output is diagnostic only; side effects are tool calls), and the **authority** (the `[PLATFORM CONTRACT — NON-NEGOTIABLE]` header is the marker implementers must preserve).

A fresh agent created without bespoke prompt work has the right behaviour out of the box. Operators with richer roles compose additional bundles ([ADR-0053](0053-units-are-agents-and-one-way-delivery.md) §1: a unit is an agent; bundle composition is the same as for agents).

### 9. Deletions in the implementation PR(s)

- `IExecutionDispatcher.DispatchAsync`'s `Task<Message?>` return — replaced by `Task<RuntimeOutcome>`.
- `A2AExecutionDispatcher`'s synthesis at line ~1409 (the `new SvMessage(From: To, To: From, …)` block) and every code path that fed it (A2A task → message mapper, stdout → message mapper, file-capture → message mapper).
- `AgentDispatchCoordinator.RecordResponseAsync` and the `WorkflowStepCompleted "Dispatch response recorded on thread."` emission. The coordinator's role narrows to *"invoke dispatcher, observe outcome, signal exit"* — no host-side message persistence.
- The `messageRouter.PersistAsync(response, …)` call on the dispatch terminal. `MessageRouter.PersistAsync` keeps existing call sites that persist genuine messages (inbound, tool-invoked sends); the response path is gone.
- `AgentResponseCapture` mode names that imply delivery (`Stdout`, `File`) — renamed to reflect trace-capture semantics (`StdoutTrace`, `FileTrace`, `A2ATrace`).
- `ActivityEventType.MessageReceived` — renamed in place to `MessageArrived` (§7). The enum ordinal is preserved (the rename does not violate the actor-remoting wire-format ordinal-stability rule). Every code consumer that compares against the string `"MessageReceived"` (`ThreadQueryService`, CLI activity filters, web detail pages, analytics aggregations) is updated atomically with the enum rename; a one-shot EF migration backfills `spring.activity_events.event_type` rows from `'MessageReceived'` to `'MessageArrived'` so historical data renders under the new name. No deprecation alias.

### 10. Additions in the implementation PR(s)

- `RuntimeOutcome` record on the dispatcher contract (§3).
- New activity types in `ActivityEventType` (§7), appended per the enum-ordinal-stability rule.
- Activity emitter on `RuntimeStarted` / `RuntimeCompleted` / `RuntimeFailed` / `RuntimeCompletedSilent` / `RuntimeReasoning` paths in `AgentDispatchCoordinator`.
- Reasoning-trace capture wired into the existing `AgentResponseCapture` modes — the bytes the modes already read become the `ReasoningTrace` field on `RuntimeOutcome`.
- Tool discovery surface: `sv.tools.list_categories`, `sv.tools.list(category)` — implemented as MCP tools on the platform MCP server ([ADR-0054](0054-one-mcp-server-one-execution-host.md)). `sv.tools.list(category)` returns the full tool definitions (name + description + JSON schema) plus category-level usage guidance in a single response. A separate `sv.tools.describe(tool)` is intentionally **not** added — MCP's `tools/list` already pairs schemas with names, and the category-aware listing preserves that "no tool known by name alone" invariant (§6).
- Directory core tools: `sv.directory.list(scope?, filter?)`, `sv.directory.lookup(address)` — implemented as MCP tools in the `directory` category, registered in the §8 fundamental core (pre-loaded by `sv.conversational.defaults`).
- Progress reporting: `sv.progress.report(message, fraction?)` — implemented as an MCP tool wired to the existing `RuntimeProgress` activity type. Registered in the §8 fundamental core.
- Category metadata on existing tool handlers (`sv.messaging.send` → category `messaging`; future primitives slot in similarly).
- `sv.conversational.defaults` skill bundle (§8) shipped under `packages/` and referenced from the default agent template. Grants the fundamental-core tools listed in §8, plus the `memory` category (visible in the categories listing, tools loaded on demand). Contributes the `[PLATFORM CONTRACT — NON-NEGOTIABLE]` platform-layer prompt fragment.
- Per-primitive typed activities for non-messaging tool handlers as those tools are added (`TaskUpdated`, `NotebookUpdated`, etc.) — out of scope for this ADR's first PR, listed so the activity-emit pattern is consistent when each lands.

## Consequences

- **The activity log becomes truthful.** Every `MessageSent` corresponds to an `sv.messaging.send` call the runtime actually made. Every thread row has a matching sender event. The "outbound message with no sender activity" class of bug is structurally impossible.
- **Stdout stops being a magic channel.** Operators stop debugging "why didn't my agent's stdout show up" — the answer becomes a documented contract: it doesn't, by design; call the tool. The reasoning-trace activity makes the text visible without making it load-bearing.
- **Multi-action turns are first-class.** Turning the dispatcher's "optional response" into a lifecycle outcome removes the implicit "at most one reply per turn" assumption. A turn that emits five messages, two task updates, and a UI card is just five + two + one tool activities on the stream.
- **Tool catalog growth is bounded.** Categories + discovery means adding a tenth primitive does not add tenth-of-a-tenth more system-prompt tokens to every dispatch. The platform can introduce abstractions aggressively without bloating the base prompt budget.
- **Non-compliant runtimes fail loudly, not silently.** A runtime that produces only terminal text emits a `RuntimeCompletedSilent` activity. Operators see exactly what happened. The temptation to paper over with auto-wrap is rejected up-front.
- **A2A protocol compliance is preserved.** A2A's `message/send` still returns a `Message`; the host still receives it. The host's choice not to treat that return as a routing instruction is a host-side semantic — the wire contract is unchanged, the runtime sees the same protocol shape it always did.
- **The default chatbot path needs the default bundle to exist.** Without §8's bundle, naive setups would break: a fresh agent with no tool grants would have no way to reply. The bundle is part of this ADR's surface, not a follow-up.
- **Migration is destructive within v0.1.** Existing agent configs that rely on stdout-as-reply break by design. Per the project's pre-1.0 cleanup norm, the synthesis path is deleted in the same PR(s) — no compat shim, no deprecation period. Test runtimes that "echo stdin to stdout" are updated to call `sv.messaging.send`.
- **The reasoning trace is bytes-on-disk.** Capturing stdout/A2A/file at a non-trivial capture level produces volume. The OTLP capture-level knob from [ADR-0054](0054-one-mcp-server-one-execution-host.md) controls this; default `summary` is the right starting point for v0.1.
- **One more tool category is the runtime's responsibility to query.** Adding a primitive doesn't require the runtime to relearn the prompt — but a runtime that never calls `sv.tools.list` won't see new primitives. The default bundle's prompt fragment instructs the runtime to query discovery on startup; bundles for richer roles do the same.

## Revisit criteria

- **Streaming output.** `sv.messaging.send_stream` (or similar) is named in this record's premise but deferred. If turn-grained delivery proves too coarse for a class of workflows (long-form code generation, narrative assistants), a streaming primitive lands as an extension of the messaging category — the activity model stays the same (each chunk is or is not its own activity; design choice at the time).
- **Auto-categorisation pressure.** If the platform grows to dozens of categories, the *category list itself* becomes prompt overhead. Mitigation by per-unit category grants is the planned answer; if grants don't bound it enough, a "category-of-categories" / namespace layer becomes worth re-opening.
- **Discovery latency.** Tool discovery is a per-turn round-trip cost when the runtime queries categories it hasn't seen. Caching at the runtime (sidecar / SDK) is the obvious mitigation; if discovery cost dominates, pre-warming descriptions in the bootstrap bundle ([ADR-0055](0055-pull-based-agent-bootstrap.md)) is a candidate path.
- **Strict compliance mode.** If a deployment wants to *reject* silent runtimes (treat `RuntimeCompletedSilent` as `RuntimeFailed`), a per-tenant or per-unit policy knob is the right place. Tolerate is the default; strict is opt-in if needed.
- **Fundamental-core composition.** §8's core is the v0.1 minimum. As real workflows surface, candidates currently on the "discussion" list (identity introspection, directory traversal, decision reporting, thread context) may earn promotion — or new fundamentals may emerge. Promotions land as ADR amendments, not silently.
