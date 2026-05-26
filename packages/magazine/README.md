# Magazine Domain Package

A **goal-driven editorial team** that produces a daily online edition.
The user opens a conversation with the editor; the editor plans the
edition, the team coordinates through every stage of the editorial
process, and a human publisher signs off before the edition is released.

## Why this package exists

Most example packages illustrate one specific coordination shape:

| Package | Shape |
| --- | --- |
| `software-engineering`, `product-management`, `spring-voyage-oss` | Delegation-shaped — a unit receives inbound work and routes it down to the member best suited to handle it. |
| `research` | Team-leader-shaped — a coordinator picks the right specialist and holds the thread back to the requester. |
| `magazine` | **Workflow-shaped** — a central decider (the editor) sets direction, members consult before going deep, and drafts flow through a sequence of specialist stages until a human approves the assembled output. |

The interesting question this package asks is: **how does a team take in
a large goal — produce today's edition — and coordinate to accomplish
it?** The answer is a mix of central direction (the editor decides
every editorial question), specialist handoffs (writer → fact-checker →
copy-editor → audience-editor → production-editor), and a hard human
approval gate at the end.

## What this package ships

- **Unit** (`units/magazine-editor/`, display name *Magazine Editor*) — the
  editor-in-chief. Plans each edition, briefs the team, decides every
  editorial question, signs off on the assembled edition before it
  goes to the human publisher.
- **Agents** (`agents/`):
  - `managing-editor` — runs the floor: turns the editor's plan into
    assignments, tracks every story, moves drafts between stages.
  - `staff-writer` — reports each assigned story using web search and
    files the first draft.
  - `fact-checker` — verifies every checkable claim and audits sourcing.
  - `copy-editor` — polishes language and house style without
    reshaping the story.
  - `audience-editor` — represents the reader; crafts the final
    headline and the promo line for each piece.
  - `production-editor` — assembles the day's edition, walks it
    through the editor's sign-off and the publisher's final approval,
    and publishes.
- **Human member** — the `publisher`, sitting on the unit with role
  `approver`. Gets the assembled edition with a "review and approve"
  ask, and either signs off or returns notes.

## The editorial workflow

1. The owner (human) opens a conversation with the editor to start a
   new edition.
2. The editor proposes the day's theme and the story slots to fill,
   confirms with the owner if anything is ambiguous, and briefs the
   managing editor.
3. The managing editor assigns each slot to the staff writer with a
   clear brief.
4. The staff writer reports the story using web search and consults
   the editor before going deep on any angle that materially differs
   from the brief. They file a draft to the fact-checker.
5. The fact-checker verifies every claim and either returns the draft
   with notes or hands the verified draft to the copy editor.
6. The copy editor polishes language and sends to the audience editor.
7. The audience editor writes the final headline and promo line and
   sends the packaged piece to the production editor.
8. When all stories are in, the managing editor signals the production
   editor that the edition is ready. The production editor assembles
   the running order and presents the edition to the editor.
9. The editor either signs off or sends notes. Once approved, the
   production editor sends the edition to the human publisher.
10. The publisher approves or returns notes. On approval, the
    production editor publishes the edition as the day's output.

The user does not need to schedule editions — each edition is initiated
by a conversation with the editor.

## Using the package

Install via the CLI:

```bash
spring package install magazine
```

Or use the portal wizard's **From catalogue** source at `/units/create`
and select **Magazine**. Both paths route through
`POST /api/v1/packages/install` and activate all artefacts atomically.

The team uses the **web-search** connector to source stories. Bind
the unit to the web-search connector and configure a provider (the
portal exposes a configuration UI on the unit's Connector tab; the
CLI exposes `spring connector web-search …`).

## Shape

The directory layout mirrors the other in-tree domain packages
(`packages/research/`, `packages/software-engineering/`) so the
file-system package catalogue (`GET /api/v1/packages`) exposes it on
the CLI (`spring package show magazine`) and the portal
(`/packages/magazine`) without any extra wiring.
