# UX explorations

Four deliberately divergent concepts for the collaboration portal, produced from [`../ux-vision.md`](../ux-vision.md) (issue [#3248](https://github.com/cvoya-com/spring-voyage/issues/3248)). Each concept is an *extreme*, pushed honestly: it names its bet, walks the primary persona through a full day, sketches its surfaces low-fi, lists the platform capabilities it assumes (marking what goes beyond the ux-vision baseline as **NEW**), and pays for its stance in a required "what this concept sacrifices" section.

> **Explorations — not commitments.** These documents exist to make a direction choosable and to surface platform requirements while the redesign decision records ([#3247](https://github.com/cvoya-com/spring-voyage/issues/3247), [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249), [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250), [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251)) are still open. Direction selection is recorded on [#3248](https://github.com/cvoya-com/spring-voyage/issues/3248).

Each concept invents its own small cast and domain for its walkthrough; names recur across concepts coincidentally and carry nothing between documents.

## The axes

Two axes generate the divergence:

1. **What anchors the experience** — the center of gravity you arrive at and navigate by. Three concepts each take one pole: your **exchanges** (01), the **things being made** (02), or the **organization itself** (03).
2. **How much conversation the portal hosts** — from a first-class composer at the center (01) to none at all (04, where conversation lives in a connected workspace and the portal is purely the lens).

## The concepts

| | [01 — Correspondence](01-conversation-first.md) | [02 — The studio](02-artifact-first.md) | [03 — The observatory](03-organization-first.md) | [04 — The Lens](04-lens-only.md) |
|---|---|---|---|---|
| **Home is** | The desk: needs-me + conversation views | The team's space: artifacts + clusters taking shape | The living org map | Catch-up + needs-me, with every exit leading to a thing or out to the workspace |
| **Core loop** | Read → answer → follow doors messages open | Open the space → open a thing → contribute from it | Sweep the map → notice → drill → steer | Notice in the lens → jump out to talk → return to see |
| **Hero capability** | Conversation views by counterpart/role/team/cluster | Agent-defined kinds in the generic view; locks as activity | Org evolution as watchable, actionable product moments | Whole-life member history; the workspace↔record seam |
| **Stresses hardest** | #3249, #3250 | #3251, #3250 | #3247, #3250 | #3250, connectors (#3263) |
| **Biggest sacrifice** | Ambient org legibility; artifact browsing | Correspondence ergonomics; cross-team triage | Conversational intimacy; artifact depth | The self-contained experience; conversation quality outsourced |
| **Single-human fit** | Strong (a desk works at any scale) | Strong (one space, one shelf) | Weakest (a map of a village) | Conditional (requires binding a workspace, or the CLI) |

None of these is a proposal to ship as-is. The selected direction will likely be one concept's anchor with another's strengths grafted on — the point of the extremes is to make the trade-offs visible before information architecture begins.

## What all four converged on

The strongest requirements signal is what every concept demanded independently, whatever its stance:

- **Attention rhythm must be legible without presence.** Every concept needed the felt difference between an agent that attends one exchange per turn and one that triages continuously to be explainable from declared, mechanical facts — never invented busyness. (Filed on #3250.)
- **Messages must carry mechanical references.** Sender-attached, durable references to artifacts (optionally a version, or a place within one) and members are the navigation spine of every concept. (Filed on #3249/#3251.)
- **"Attended" is an act, not a read state.** Needs-me clears because the member responded (on any surface) or explicitly set an item aside — recorded, private to the member, never a receipt to the sender. (Filed on #3250.)
- **Delivered items wear the role they arrived through.** One human filling several roles in one team is ordinary; every concept renders which role an item reached you through. (Filed on #3247.)
- **Live activity is strands with causes.** Streamed in-progress activity presents as distinct strands per member, each tagged with what caused it — the answer to "is anything happening?" everywhere. (Baseline in ux-vision; sharpened on #3250.)

## Requirements filed

The **NEW**-marked assumptions from all four concepts were consolidated, deduplicated, and filed as exploration-round requirement comments on the owning issues: [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247) (subjects, teams, roles), [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249) (messages and delivery), [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250) (the record and its views), [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251) (artifacts), and [#3263](https://github.com/cvoya-com/spring-voyage/issues/3263) (the connected-workspace seam, from concept 04). Each concept's own "Platform capabilities this concept assumes" section is the per-concept source; the comments are the consolidated, deduplicated cut.

## What happens next

1. Direction selection (or "iterate again") is recorded on [#3248](https://github.com/cvoya-com/spring-voyage/issues/3248).
2. The selected direction proceeds to information architecture, navigation, and visual design — deliberately out of scope for every document in this directory.
3. The filed requirements are weighed inside their decision records; a concept's assumption is an input, not a decision.
