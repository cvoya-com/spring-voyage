# Magazine (LangGraph)

A goal-driven editorial team that produces a daily online edition. You open a
conversation with the **Magazine Director** — the editor-in-chief — who plans
the edition; the team coordinates through every stage of the editorial process,
and a human approver signs off before the edition is released.

The team's coordinator — the **managing editor** — is driven by a real
agent-workflow **orchestration engine** (LangGraph) running in its container,
rather than an LLM following routing instructions. The pipeline is an explicit
state machine: the engine assigns each story, advances it stage by stage by
delegating to the specialists over messaging, assembles the edition, and brings
it to the director for sign-off. This package is the working demonstration
behind
[ADR-0066](../../docs/decisions/0066-a2a-process-runtime-engine-orchestration.md)
and issue [#2591](https://github.com/cvoya-com/spring-voyage/issues/2591) —
hosting an external orchestration framework on the platform.

## The team

- **Magazine Director** (the unit) — plans each edition, sets editorial
  direction, decides every editorial question, and signs off on the assembled
  edition before it goes to the publisher.
- **Managing Editor** — runs the floor: turns the director's plan into
  assignments and keeps each story moving through the pipeline.
- **Staff Writer** — reports each assigned story with web search and files the
  first draft.
- **Fact Checker** — verifies every checkable claim and audits sourcing.
- **Copy Editor** — polishes language and house style without reshaping the
  story.
- **Audience Editor** — writes the final headline and promo line for each piece.
- **Production Editor** — assembles the edition, applies any revisions, and
  publishes it to the publisher after sign-off.
- **Publisher** — a human approver who receives the assembled edition and either
  signs off or returns notes.

## How an edition flows

1. A reader opens a conversation with the director to start an edition.
2. The director proposes the theme and the stories to run, then briefs the
   managing editor.
3. The managing editor assigns each story to the staff writer, then moves each
   piece through fact-check → copy-edit → packaging as the work returns.
4. When every story is packaged, the managing editor has the production editor
   assemble the edition and brings it to the director for sign-off.
5. On approval, the production editor publishes the edition and delivers it to
   the publisher. On revision notes, the managing editor routes a revise pass
   and returns it for sign-off.

## Using the package

```bash
spring package install magazine-langgraph
```

The team uses the **web-search** connector to source stories — bind it on the
unit and configure a provider (the portal exposes a configuration UI on the
unit's Connector tab; the CLI exposes `spring connector web-search …`).
