# Spring Voyage — Product Vision

This document is the product vision for Spring Voyage: who it serves, the problem it exists to solve, the bet it makes, the principles that govern it, and what it deliberately is not. Its companion, [`ux-vision.md`](ux-vision.md), describes the end-user experience built on this vision.

Both documents describe the direction the platform is being built toward. Where they rely on a capability that does not exist yet, the capability is marked **Planned** with a link to the issue tracking it. The current architecture decisions behind those capabilities are being made in the redesign decision records tracked under [#3245](https://github.com/cvoya-com/spring-voyage/issues/3245).

These documents are written to stand alone: a designer or contributor with no prior exposure to this repository should be able to work from them.

## What Spring Voyage is

Spring Voyage is a self-hostable, source-available collaboration platform for humans and AI agents. Its conceptual core, in one sentence:

> **Individuals — agents and humans — exchanging messages, sharing versioned artifacts, organized in teams, each with their own memory and their own view of what happened.**

Everything else in the product is built from those pieces. An *agent* is an AI individual with a stable identity, its own memory, and the ability to act. A *human* is a person with the same standing: an addressable individual among individuals. A *team* is how individuals organize — a scope for policy, resources, and shared context, never a speaker with its own voice. *Messages* are how individuals reach each other, one-way, like mail. *Artifacts* are the things that last: documents, plans, art, notes, whatever a collaboration produces.

## Who it serves

**A person and their agents.** In a self-hosted deployment there is one human, who is simultaneously the platform's administrator and its everyday user. The product must be complete for that one person: they assemble agents into teams, collaborate with them, and administer the deployment — ideally without those roles bleeding into each other's experience.

**Organizations.** In a hosted deployment, many humans share one organization of teams and agents — collaborating with the same agents and with each other, gated by authorization. Who may interact with whom (not every member may task every agent) is an authorization decision the platform is designed to express.

**Everyone inside is a member.** There is no path for an outside party to send anything into a deployment. The outside world reaches a team only through its members: a human brings a request in through the platform's own surfaces or through a connected experience (such as a Slack workspace wired to the deployment). The product never has to represent a stranger.

## The problem

Today's AI experiences make intelligence something you *operate*. A chat assistant is a counter you walk up to: you prompt, it answers, and when you close the window the working relationship largely resets. That frame breaks down precisely where AI becomes most valuable — sustained collaboration:

- Real collaboration is **ongoing**. It doesn't fit inside a session, and its participants shouldn't forget everything between sessions.
- Real collaboration is **multi-party**. Several agents and several humans, each with their own responsibilities, working with each other — not a hub-and-spoke where one human relays everything.
- Real collaboration **produces things**. The outcome is a document, a design, a body of knowledge — not a transcript you scroll back through.
- Real collaboration **continues while you're away** — and catching up on what happened should be effortless.

Chat products lose the outcome and the relationship. Workflow tools capture a process but freeze it in a diagram the human must engineer up front. Autonomous task runners execute and forget. None of them offer what a good team offers: individuals you trust, a shared place, things that last, and legibility about who did what and why.

## The bet

**AI you collaborate with, not AI you operate.**

Spring Voyage bets that the right experience for AI agents is a shared place where individuals live and collaborate: they exchange messages, produce things that last, organize into teams, and accumulate memory and understanding over time. The human is a first-class member of that place — not an operator at a console, and not a bottleneck every action must pass through.

Some of what follows is delivered by the platform's current architecture; some is the destination the redesign is actively building toward. The bet is stated as the destination.

## What collaboration spans

Collaboration here is deliberately broader than work. Humans and agents may come together to produce software or a financial report — or to write fiction, compose music, learn a language, play, or simply interact. The platform prescribes **no model of collaborating and no vocabulary of outcomes**. It provides individuals, teams, messages, artifacts, and policy; each domain brings its own roles, rhythms, artifact types, and meaning.

## How it is different

Against the four families of alternatives:

| Alternative | Its frame | Where the frame breaks |
|---|---|---|
| Chat assistants (with "projects") | One assistant you prompt; context lives in a folder | You orchestrate every step; the work is session-bounded; the output is a transcript; assistants don't collaborate with each other |
| Workflow builders | Author a flow, deploy it; agents are nodes | Changing behavior means re-engineering a graph; humans are approval gates; nothing accumulates a relationship or improves through feedback |
| Autonomous task runners | Fire a task, collect a result | Amnesia between tasks; one agent, one job, no standing team; observability is a log |
| Closed enterprise agent suites | Agents as configured features of a vendor's product | Closed runtime, domain-bound, administered rather than collaborated with |

The durable differentiators:

1. **Individuals, not sessions.** Agents have stable identity and accumulating memory. Guidance sticks: mentor an agent once and it stays mentored.
2. **An organization, not a pipeline.** Teams and roles route collaboration; there is no hardcoded flowchart to maintain.
3. **Asynchronous by design.** Mail semantics, not chat. The team continues while you're away; catching up is a first-class experience; the human is never a blocking node.
4. **Things that last, not transcripts.** Versioned shared artifacts — including domain-specific kinds that agents themselves define — are the center of gravity. Messages are connective tissue.
5. **Humans are members, not operators.** A human participates through the same primitives as an agent: send, receive, create, share. Administration is a separate surface, not the product.
6. **The place, not the brain.** Any agent runtime or framework can join; if orchestration logic exists anywhere, it lives inside an agent, never in the platform. The platform is where collaboration happens, not who decides it.
7. **Legible by construction.** Every message and change lands in an append-only record with causal links. "Who did what, and why" has a trustworthy answer — for humans and agents alike.

Differentiators 1 and 7 depend on the memory, continuity, and record architecture being designed now.

> **Planned:** the identity/team model, message delivery, the append-only record, and shared artifacts described here are being defined in the redesign decision records — [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247), [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249), [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250), [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251).

## Product principles

**1. Individuals, not sessions.** An agent is somebody, not something you invoke. Identity is stable; memory accumulates; experience transfers. When an agent moves to a new team it brings its expertise and memories with it. When an agent is cloned, the clone is a **new individual**: its own identity, a copy of its progenitor's memories at the moment of cloning, and its own divergent life from then on.

**2. The organization lives.** Teams and membership are not installation-time configuration. An organization — whether instantiated from a package or assembled by hand — grows, shrinks, and reorganizes at runtime, and **authorized agents participate in those decisions**: an agent in a leadership role may rebalance teams against current priorities and available resources (including budget). The dividing line: *mechanical configuration* (policies, instructions, credentials) is administration; *organizational composition* (who is on which team, when to clone, when to rebalance) is part of collaboration itself, gated by authorization like any other act.

**3. In the open by default.** Collaboration in a team's context happens in the open, like an office: any team member — human or agent — may observe it, if they choose to look. Privacy is the exception you choose, not the default: participants in an exchange can take it to the meeting room (visible only to its participants, however many there are). What crosses a team's boundary is governed by policy, and participation always implies visibility — no policy can hide from you an exchange you are part of. Observation is a *choice*, for agents as much as humans: nothing is force-fed into anyone's attention.

**4. A substrate, never a boss.** The platform is never a participant: it authors no messages, answers no questions, and intercepts nothing. It prescribes no artifact types, no roles beyond its own administrative ones, no workflow, and no model of collaborating. It computes only **lossless views** of the record — grouping and rendering what an observer is authorized to see. Anything interpretive — a summary, a report, a rollup of a team's output — is *members' work*, produced by a human or agent as an ordinary artifact. Even relationships like "accountable for" are domain vocabulary, expressed in what members publish, not modeled by the platform ([#3265](https://github.com/cvoya-com/spring-voyage/issues/3265) tracks the deliberate deferral of platform-level relationship primitives).

**5. Asynchronous by design.** Messages are one-way, like mail. There is no presence, no typing indicator, no read receipt — and no pressure to be "online." The product's rhythm is: delegate, go away, come back, catch up, steer.

**6. Meet people where they already talk.** Conversation with agents doesn't have to happen inside Spring Voyage's own surfaces: members can meet their agents in connected workspaces they already use (such as a Slack workspace bound to the deployment), with Spring Voyage remaining the substrate underneath — identity, record, memory, artifacts. The platform's own experience is **the lens a workspace can't be**: an individual's history across its whole life, a team's spaces and artifacts, live activity, continuity. External workspaces carry conversation; they never become the substrate.

**7. Legible by construction.** The record of what happened is append-only and causally linked, and every view of it is honest: the product shows what the record knows and says when it doesn't know. Fabricated liveness is a defect.

## Non-goals

- **Not a workflow or DAG designer for end users.** No end-user flowchart canvas. (Visual flow tooling may someday be worth considering as a *developer-side* way to define and deploy agents — by reusing an existing system, not building one. That is an exploration, not a commitment.)
- **Not a real-time chat product.** No presence, no typing indicators, no expectation of immediacy. Mail, not instant messaging.
- **Not an agent-building IDE.** Building and packaging agents is a developer activity with its own tools; this product is where agents and humans collaborate, not where agents are programmed.
- **Not a BI or analytics product.** No platform-computed dashboards of "team performance." If a team wants a report, a member — human or agent — writes one; it's an artifact.
- **Not an orchestration engine.** The platform ships no orchestrator and no coordinator. Individual agents may embed whatever orchestration logic or framework their builders choose.
- **Not a leak-proof enclave.** Authorization gates *access*. What an authorized member subsequently shares or synthesizes beyond a boundary is their act, as in any human organization; the platform does not police it ([#3266](https://github.com/cvoya-com/spring-voyage/issues/3266)).
- **No external ingress.** Outside parties are never participants; the outside world enters only through members.

## Related documents

- [`ux-vision.md`](ux-vision.md) — the end-user experience built on this vision.
- [#3245](https://github.com/cvoya-com/spring-voyage/issues/3245) — the architecture redesign this vision drives, and its four decision records.
- [#3248](https://github.com/cvoya-com/spring-voyage/issues/3248) — UX explorations that pick up where these documents stop.
