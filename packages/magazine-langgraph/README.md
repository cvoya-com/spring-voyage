# Magazine (LangGraph)

A goal-driven editorial team that produces a daily online edition — the same
shape as the [`magazine`](../magazine/README.md) package, with **one part
changed**: the **managing editor** that coordinates the assembly line is a real
workflow **engine** (LangGraph), not an LLM following prose instructions.

This package is the working demonstration behind
[ADR-0066](../../docs/decisions/0066-a2a-process-runtime-engine-orchestration.md)
and issue [#2591](https://github.com/cvoya-com/spring-voyage/issues/2591): it
tests the platform's premise that an agent's runtime can *host its own
orchestration framework*.

## What's different from `magazine`

| Role | `magazine` | `magazine-langgraph` |
| --- | --- | --- |
| Director (the unit) | LLM — plans the edition | LLM — plans the edition (unchanged) |
| Staff writer, fact-checker, copy/audience/production editors | LLM specialists | LLM specialists (unchanged) |
| **Managing editor** | LLM coordinator keeping a prose "running budget" ledger and routing each stage by hand | **LangGraph engine** (`a2a-process` runtime) — the pipeline is an explicit state machine; routing, the ledger, and the join-counting are deterministic graph state, not prompt text |

The managing editor runs the platform-built
`ghcr.io/cvoya-com/spring-voyage-langgraph-orchestrator` image on the generic
`a2a-process` runtime: a long-running, always-on process that receives every
message as an event, holds the edition's state in a durable checkpoint on its
workspace volume, and delegates each pipeline stage to the right specialist
over `sv.messaging` — suspending the per-slot graph at each delegation and
resuming it when the specialist's reply lands.

## How an edition flows

1. The owner opens a conversation with the **director** to start an edition.
2. The director proposes the theme and the story slots, then briefs the
   **managing editor** (the engine) with the theme and a bulleted list of slots.
3. The engine starts one LangGraph pipeline per slot and delegates the first
   stage (the draft) to the **staff writer**.
4. Each specialist returns its work to the managing editor, echoing a short
   reference token so the engine can route the reply to the right slot and
   stage. The engine advances that slot to the next stage
   (writer → fact-checker → copy editor → audience editor).
5. When every slot is packaged, the engine asks the **production editor** to
   assemble the edition, then brings the assembled edition back to the director
   for sign-off.
6. On approval, the engine releases the edition to the production editor to
   publish and deliver to the human **publisher**. On revision notes, it routes
   them back to production for a revise pass and returns for sign-off again.

## Correlation note (v1)

Because a Spring Voyage conversation is identified by its participant set, every
brief the engine sends to the same specialist shares one conversation — so the
reply's thread cannot by itself say which delegation it answers. v1 resolves
this with an explicit reference token the engine embeds in each brief and the
specialists echo verbatim. ADR-0066 §5 records the principled platform fix (a
correlation id the platform round-trips through `respond_to`) as the next
increment.

## Using the package

```bash
spring package install magazine-langgraph
```

The team uses the **web-search** connector to source stories (bind it on the
unit, same as `magazine`). The managing editor is a deterministic engine and
makes no LLM calls of its own; it declares the team's anthropic model only to
satisfy the platform's structured-model contract (ADR-0038), and the credential
it resolves is the same one the specialist agents already require.
