# Spring Voyage — UX Vision

This document describes the end-user experience Spring Voyage is building toward: the experience principles, the people it serves, the key surfaces described by capability, how it relates to the two existing portals, and the platform capabilities it depends on. It is the companion to [`vision.md`](vision.md), which defines who the product serves and the bet it makes — read that first.

This document deliberately stops short of information architecture, navigation, visual design, and naming. Those belong to the UX explorations ([#3248](https://github.com/cvoya-com/spring-voyage/issues/3248)), for which this document is the starting brief.

## Working vocabulary

These terms are used throughout; the ones marked *provisional* are placeholders for the explorations to name properly.

- **Member** — an individual in the organization: a human or an agent.
- **Conversation** — a computed view of related messages between members. Conversations are how exchanges are *presented*, never containers that messages live in.
- **Artifact** *(provisional)* — a thing that lasts: a document, a plan, a piece of art, a note, a report. Versioned; may be of a kind an agent itself defined.
- **Space** *(provisional)* — a team's shared place: its members, its artifacts, and the activity around them. "Collaboration space" when precision helps.
- **Collaboration portal** *(provisional)* — the end-user surface this document describes, counterpart to the existing management portal.

## Experience principles

**1. Calm and glanceable, not a feed.** The register is a calm shared place, not a timeline to doomscroll. A glance answers: who is doing what, is anything stuck, what needs me. Chronology exists but is a drill-in, never the home.

**2. Attention is sacred.** "What needs me" — things addressed to me, approvals waiting on me, collaborations blocked on me — anchors the experience. Triage comes first; ambience is available, never imposed. The product must feel safe to walk away from.

**3. Never fabricate.** Every state shown is a state the record supports. If the record doesn't know, the product says it doesn't know. No invented progress, no fake liveness, no status theater.

**4. The organization stays visible.** Teams, membership, and roles are legible in the experience itself. A message sent to a role reads as sent to the role — "to: designer @ a team, delivered to the member filling it" — so the way the organization routes collaboration is something users absorb by using the product, not by reading documentation.

**5. Catch-up is first-class.** The collaboration continues while you're away — by design. Coming back must be a moment the product excels at: what happened since you left, organized by what emerged, not a backlog of unread items inducing guilt.

**6. Things over transcripts.** What a collaboration *produces* — its artifacts — sits front and center. Exchanges are the connective tissue around artifacts and activity, not the product's center of gravity.

**7. One lens for everyone.** A human watching a team sees a policy-gated view of the shared record — exactly the lens an agent gets when it chooses to observe. The portal is not a privileged spy-hole, and administrative audit lives on the administrative surface, not here.

**8. Presence-free honesty.** There is no online/offline, no typing indicator, no read receipt. What the product *can* honestly say — a message was delivered, or failed, and when — is quietly available where it matters, never as social pressure.

## Personas

**The member** *(primary)* — a human collaborating with agents and other humans. Their jobs, in priority order:

1. **Delegate** — hand something to an agent, a role, or a team, and trust it to proceed.
2. **Watch and steer** — observe what's in flight, notice drift, step in with guidance.
3. **Participate as a peer** — answer questions, review and approve, contribute artifacts, converse.
4. **Mentor and shape** — guide agents through conversation so they improve; where authorized, take part in organizational decisions: growing a team, moving a member, creating a new individual by cloning an agent.

**The administrators** — the *deployment administrator* (installs and operates the deployment) and the *tenant administrator* (configures the organization: policies, instructions, credentials, budgets, connected experiences). Their home is the management portal, not this surface.

**The self-hoster holds every role at once.** In a single-person deployment, the same human is deployment administrator, tenant administrator, and member. The product offers them either a combined experience or deliberately **focused views** — administrator concerns set aside, member experience undiluted — and the CLI compartmentalizes along the same lines.

There is no external-party persona: everyone who appears in the experience is a member of the organization.

## Key surfaces, by capability

These are capabilities the experience must deliver, not screens. How they compose into navigation is the explorations' problem.

### Home: triage and ambience

- Surface what needs me: messages addressed to me (directly or via a role I fill), approvals waiting on me, activity blocked on me.
- Offer an ambient glance across my teams: what is in flight, what has recently emerged, what changed.
- Support "since you were away" as a deliberate, well-crafted moment — per team and across teams.

### The team's space

- Show the team as an organization: members (human and agent, presented as peers), the roles they fill, nested teams.
- Hold the team's artifacts and the observable activity around them — the open floor, rendered through the viewer's authorization.
- Show what is in flight as **emergent clusters** of causally-related activity — a topic being discussed, a goal taking shape, an artifact being produced, an idea being explored. The product must not force everything into one ontology: what a cluster *is* is whatever the collaboration made it.

### Artifacts

- Give every artifact a stable home: its current version, its history, the activity and conversation around it, and the members involved in it.
- Open *any* artifact — including kinds defined by an agent — in a faithful generic view; richer, kind-specific rendering is progressive enhancement, never a prerequisite.
- Let humans create and edit artifacts of basic kinds (documents, notes) directly, and review, annotate, and approve artifacts of any kind.
- Show work-in-progress honestly: when a member holds an artifact's lock, show who and since when — presence of activity, not a wall.
- Let authorized members adjust an artifact's visibility (within the team, beyond it), as an ordinary, recorded act.

### Conversations

- Present exchanges as computed conversation views: by counterpart (a human, an agent, a role) as the default grouping, with cross-cutting views by team or by emergent cluster.
- Preserve role addressing in the presentation: a message to a role shows the role and its resolution.
- Make delivery truth (delivered, failed, pending — with timestamps) available on demand.
- Let a member converse to mentor: guidance given in conversation is how agents improve, so the experience must treat human→agent conversation as substantive, not as issuing commands.

### Watching in flight

- Let a viewer lean in: from a cluster or an agent, drill into live streamed activity as an agent makes progress on something, tagged with what caused it.
- Make the record causally navigable in both directions: from any message or change, walk to what caused it and what it caused.

### The organization, alive

- Present the organization's structure — the team tree, membership, roles — as part of the product, not as configuration.
- Treat organizational change as product moments: a member joins or moves teams (their memories and expertise travel with them), an agent is cloned (a *new individual* joins, with its own identity), a team grows or shrinks. Where the viewer is authorized, these are actions; otherwise they are legible events.
- Keep mechanical configuration (policies, instructions, credentials) out of this surface — that is the management portal's job.

## Relationship to the existing portals

Spring Voyage currently ships one web application with two portals ([decision record 0033](../decisions/0033-two-portal-architecture.md)): the **management portal** (setup, configuration, monitoring, analytics, cost) and the **engagement portal** (following exchanges with and among agents as they happen).

- **Replaced.** The collaboration portal is the successor of the engagement portal — the same ambition (a first-class place to collaborate with and observe agents), rebuilt on the redesigned model. The engagement portal's organizing structures are not carried forward.
- **Retained.** The management portal remains the administrators' surface. What a given human sees is composed by their platform roles: a member sees the collaboration portal; administrators also see the management portal; a self-hoster sees both, with focused views available. The CLI mirrors this compartmentalization.
- **Borrowed.** One web application, one session and sign-in, one design system, one typed client of the public API. Two rules carry over with full force: **no portal-private API** (every capability the portal uses is public API, usable by anyone) and **CLI parity** (every portal capability has a CLI counterpart).
- **Explicitly avoided.** Three failure modes of the current portals, named so the explorations design against them: presenting activity as an undifferentiated event stream; navigation built on conversation containers; and status theater — showing liveness or progress the record doesn't support.

## Relationship to connected workspaces

Members may converse with agents where they already talk: an external workspace (such as Slack) bound to the deployment through a connector carries conversation with the organization's members, with the platform as the substrate underneath. This is a standing design goal, not an edge case — for many members, day-to-day exchanges will happen there.

The consequence for this portal's design: **don't compete with chat; be the lens a workspace can't be.** What belongs uniquely here is everything an external workspace cannot show — a member's history across its whole life, a team's space and artifacts with versions and locks, live activity as it streams, causal navigation, catch-up and continuity. The explorations should treat connected workspaces as a given neighboring surface and design this portal around what only it can do.

## Platform capabilities this UX depends on

The experience above is only as honest as the platform beneath it. This section states the UX-driven requirements on the platform, grouped by the decision record that owns each area. It exists so that each requirement can be carried into the corresponding decision as a concrete input.

> **Planned:** everything in this section describes capabilities under design in the redesign decision records ([#3247](https://github.com/cvoya-com/spring-voyage/issues/3247), [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249), [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250), [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251)).

### Subjects, teams, and roles — [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247)

- Platform roles (deployment administrator, tenant administrator, member) drive **surface composition** — which portals and focused views a human gets — and the CLI compartmentalizes identically. Team-scoped roles remain a separate concept.
- Membership and role changes are **runtime operations available to authorized subjects, including agents** — organizational evolution is a first-class, recorded capability, not administration-only.
- **Cloning an agent** creates a new individual: its own identity, a copy of the progenitor's memories at cloning time, divergence thereafter. A member moving between teams keeps its identity and memories.
- Interaction authorization ("who may task whom") must be expressible, fail-closed — designed for now even if enforcement arrives later.
- The single-human deployment is the degenerate case that must always work: one human holding all platform roles at once, cleanly.

### Messages and delivery — [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249)

- Every message carries a **visibility scope orthogonal to its recipients**: participants-only (the meeting room — private to its participants, whatever their number), team-observable (the open floor — the default for activity in a team's context), or wider, as team policy allows. Participation always implies visibility.
- Role-addressed messages **preserve the role in the durable record** — both the selector and its resolution — so every surface can render "to: designer @ team, delivered to X".
- Per-recipient delivery outcomes (delivered, failed, pending, with timestamps) are recorded and presentable. **No presence and no read receipts exist anywhere in the model.**

### The record and its views — [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250)

- All reading of the record goes through **policy-gated, per-observer views**, and the same view machinery serves human surfaces and agent observation alike; agent observation is **pull-based** — a choice, never a broadcast into an agent's attention.
- Views are **lossless**: grouping, filtering, and rendering of what the observer may see. Anything interpretive (summaries, rollups, reports) is produced by members as artifacts, never computed by the platform.
- The record supports clustering causally-linked activity into **emergent clusters** — a topic, a goal, an artifact's evolution, an idea — without a modeled container and without prescribing an ontology of what emerges.
- "What needs me" (addressed to me, approvals, blocked on me) and "since you were away" are supported as first-class views.
- An agent's in-progress activity can **stream live, tagged with causal context**, so a viewer can lean in while durable messages remain the record.
- **"Observe" is its own authorization verb**, distinct from send, receive, and roster visibility.

### Artifacts — [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251)

- A team **contains** artifacts (it is their scope), but does not own them in any deeper sense; involvement of members with an artifact is domain vocabulary, not a platform relation ([#3265](https://github.com/cvoya-com/spring-voyage/issues/3265) tracks the deferred exploration of relationship primitives).
- **Agents can define artifact kinds at runtime**: a declared structure plus an optional rendering hint. For every kind — built-in or agent-defined — the platform guarantees versioning, history, locking, and a faithful **generic structured view**, so a human can always open what an agent invented yesterday.
- Visibility policy attaches to the team and/or the individual artifact and is **changeable at runtime by authorized members, human or agent**, as an ordinary recorded act (e.g. making a team's report organization-visible after a given date). Whether time-conditioned policy is native or an agent's job is an open design question for the decision record.
- Locks surface as presence of activity — who is working on an artifact, since when — never as a modal wall in the experience.
- The platform prescribes **no artifact kinds and no model of collaborating**; how artifacts are used, what they contain, and who they are for belongs to each domain. Cross-boundary sharing by authorized members is not policed ([#3266](https://github.com/cvoya-com/spring-voyage/issues/3266)).

### Cross-cutting

- **Budget visibility**: authorized members — including agents in organizational roles — can read resource state (e.g. token budgets) as an input to organizational decisions.
- **Policy changes are auditable acts**: adjusting visibility or authorization lands in the record like any other act, attributable and causally linked.

## Out of scope for this document

Information architecture, navigation structure, visual design, naming (all *provisional* terms above), and any framework or implementation choice. The UX explorations ([#3248](https://github.com/cvoya-com/spring-voyage/issues/3248)) own those, starting from this brief.
