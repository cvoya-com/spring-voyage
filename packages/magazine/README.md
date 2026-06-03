# Magazine

A **goal-driven editorial team** that produces a daily online edition.
You open a conversation with the **Magazine Director** — the magazine's
editor-in-chief — who plans the edition; the team coordinates through
every stage of the editorial process, and a human publisher signs off
before the edition is released.

## Why this package exists

The catalog's other teams each illustrate one coordination shape:

| Package | Shape |
| --- | --- |
| `software-engineering`, `product-management` | Delegation-shaped — a unit receives inbound work and routes it down to the member best suited to handle it. |
| `research` | Team-leader-shaped — a coordinator picks the right specialist and holds the thread back to the requester. |
| `magazine` | **Workflow-shaped** — a central decider (the editor) sets direction, a managing editor coordinates the assembly line, and drafts flow through a sequence of specialist stages until a human approves the assembled output. |

The interesting question this package asks is: **how does a team take in
a large goal — produce today's edition — and coordinate to accomplish
it?** The answer is a mix of central direction (the editor decides
every editorial question), a single coordinator who owns the assembly
line (the managing editor routes each stage's finished work to the
next), specialist stages (writer → fact-checker → copy-editor →
audience-editor → production-editor), and a hard human approval gate at
the end.

## What this package ships

- **Unit** (`units/magazine-director/`, display name *Magazine Director*) — the
  magazine's **editor-in-chief** (the team simply calls them "the
  editor"). Plans each edition, briefs the team, decides every
  editorial question, signs off on the assembled edition before it
  goes to the human publisher.
- **Agents** (`agents/`):
  - `managing-editor` — runs the floor and owns the pipeline: turns the
    editor's plan into 1:1 assignments and tracks every story, with each
    stage's finished work returning to them to route onward.
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
3. The managing editor opens the edition with one short kickoff to the
   whole team — "assignments come from me in 1:1; send finished work
   back to me and I'll route it" — then assigns each slot to the staff
   writer in 1:1 with the angle, the deadline, and any non-negotiables.
4. The staff writer reports the story using web search, consults the
   editor before going deep on any angle that materially differs from
   the brief, and **returns the finished draft to the managing editor**,
   who routes it to the next stage (by default the fact-checker).
5. The fact-checker verifies every claim and returns the result to the
   managing editor — the verified draft, or the draft with notes — who
   routes it onward (default: copy editor) or back to the writer for
   fixes.
6. The copy editor polishes the language and returns the draft to the
   managing editor, who routes it on (default: audience editor).
7. The audience editor writes the final headline and promo line and
   returns the packaged piece to the managing editor, who routes it on
   (default: production editor).
8. When all stories are in, the managing editor signals the production
   editor to assemble. The production editor builds the running order
   and **returns the assembled edition to the managing editor**, who
   brings it to the editor for sign-off.
9. The editor either signs off or returns notes (routed by the managing
   editor). Once approved, the managing editor releases the edition to
   the production editor, who delivers it to the human publisher.
10. The publisher approves or returns notes. On approval, the
    production editor publishes the edition as the day's output.

The user does not need to schedule editions — each edition is initiated
by a conversation with the editor.

### Local memory and information sharing

The pipeline above is the spine, not the whole picture. As the team
works, useful context turns up everywhere — a source the writer
followed, a framing the audience editor tried, a number the fact-checker
audited. Two rules govern how that context flows:

1. **Each member has their own local memory.** They keep notes there
   as they work — sources, decisions, framings, anything they may
   need again. Memory is private to the member; peers don't read it.
2. **Sharing happens via messaging.** When a member finds something
   another member can use, they send it. The recipient folds it into
   their own local memory and uses it when they author their piece.

Members discover what their peers are doing three ways:

- the team directory tells them who is on the desk and what each is
  covering,
- they can ask a peer directly when they need to know quickly, and
- the editor's briefs and the managing editor's running budget make
  most of the picture explicit without anyone having to chase it.

The managing editor is the channel for routine cross-member notes —
the editor sees connections across the room and routes the right
information to the right member.

Three norms keep this coherent and low-noise:

1. **Pipeline transitions go through the managing editor.** Each stage's
   finished work returns to the managing editor, who routes it to the
   next stage; members don't advance pieces themselves. That single hub
   is also the guard against duplicate sends — one role owns each
   outbound artifact. The human publisher has a single point of contact:
   the production editor delivers the edition (after sign-off) and the
   editor handles sign-off and escalation; no other member messages the
   publisher.
2. **Peer consultation is free; the team thread stays low-noise.**
   Members may consult each other directly whenever it sharpens the work
   (copy editor ↔ audience editor on a headline, writer ↔ fact-checker
   on a source) — only pipeline-state transitions are coordinated through
   the managing editor. Routine status is reported 1:1 to the managing
   editor, who holds the running budget; the whole-agent-team thread is
   reserved for the managing editor's kickoff and the binding decisions
   the editor and managing editor advertise (tie-breaks, reversals, a
   direction the whole desk must follow) via `send()` (one shared,
   recallable thread), not `multicast()` (which makes N private
   one-to-one copies and fragments the picture).
3. **Check before you send or ask.** Before re-requesting an artifact or
   chasing a peer, a member checks with the managing editor (who holds
   the running picture) and the team thread for any binding decision
   already settled, so nobody re-delivers, re-requests, or re-opens work
   that is already handled.

Underlying all three is a timing habit: **sending isn't seeing.** Work is
asynchronous — teammates process their inboxes a turn at a time and
messages can cross in transit — so a request that your just-sent message
already answers needs no "I already sent that," and a message you haven't
heard back on isn't necessarily missed. Members give it a beat instead of
chasing or re-sending; the managing editor, as the busiest hub, treats a
crossed or not-yet-answered message as ordinary timing rather than a
problem to correct. (A genuine stall is still worth a check.)

### Who owns pipeline routing

The managing editor is the pipeline's single routing authority. Every
assignment a member receives carries three things: the brief (what to
do), the deadline, and anything non-negotiable from the editor. Where
the finished work goes next is not part of the brief — it always returns
to the managing editor, who decides what advances, to whom, and when.

Funnelling every pipeline-state transition through one coordinator —
rather than letting each member hand its artifact straight to the next
stage — is deliberate. It keeps a single, coherent picture of what is
advancing and stops members from racing pieces forward (or issuing their
own holds) while a routing question is still open. Peer discussion stays
free; only the transitions are coordinated.

## Using the package

Install via the CLI:

```bash
spring package install magazine
```

Or use the portal's **New Unit → Catalog** flow and select **Magazine**.
Both paths activate all artefacts atomically.

The team uses the **web-search** connector to source stories. Bind
the unit to the web-search connector and configure a provider (the
portal exposes a configuration UI on the unit's Connector tab; the
CLI exposes `spring connector web-search …`).
