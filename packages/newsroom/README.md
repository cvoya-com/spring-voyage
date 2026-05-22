# Newsroom Domain Package

A **communication-shaped** example package. It ships a flat news desk of beat
reporters who each autonomously own a beat and keep each other informed —
nobody delegates, nobody routes inbound work down a hierarchy.

## Why this package exists — the contrast with `spring-voyage-oss`

Most example packages are **delegation-shaped**: a unit receives work and
routes it down to the member best suited to handle it. The
[`spring-voyage-oss`](../spring-voyage-oss/README.md) package is the canonical
example — its `spring-voyage-oss` unit is a *router*: it takes inbound GitHub
events and dispatches each one down to an engineer or PM agent. The unit's
entire job is delegation; work is **routed**, and the agents below it receive
assignments.

`newsroom` is the deliberate inverse. The `news-desk` unit is a *room of
peers*, not a router. Each beat reporter owns a beat — politics, business,
technology — and decides for themselves what to cover. The wire editor
aggregates the desk's output but does not assign anyone stories. Work is
**owned**, not routed. There is no triage step, no capability-match dispatch,
no hierarchy.

What peers on the desk do constantly is **communicate**:

- A reporter who turns up something touching *another* beat **sends** it to
  that peer — `sv.messaging.send` to `business-desk`: "the merger I'm covering
  drew an antitrust referral — regulatory angle, you'll want this." A one-way
  heads-up, not an instruction; the peer owns their beat and decides what to do
  with it.
- A reporter who learns something newsroom-wide **broadcasts** it —
  `sv.messaging.multicast` with `scope: unit-members`: "major story breaking,
  hold the front page." Every peer on the desk gets it at once.

| | `spring-voyage-oss` (delegation-shaped) | `newsroom` (communication-shaped) |
| --- | --- | --- |
| Unit's role | Router — receives work, dispatches down | Room — peers exchange information |
| Work flow | Routed to the best-fit member | Owned by each member; never routed |
| Member relationship | Hierarchy: unit assigns, agents receive | Peers: equals informing each other |
| Reach-for tool | Delegation-style routing | `sv.messaging.send` / `sv.messaging.multicast` |

Read the two packages side by side to see **when to reach for `sv.messaging.*`
vs delegation-style routing**: route work down when a unit owns triage and
assignment; send or broadcast a message when peers own their own work and only
need to stay informed.

## What this package ships

- **Unit** (`units/news-desk/`, display name *News Desk*) — a flat peer room.
  It does not route inbound work down a hierarchy. Its policies
  (`communication: peer-to-peer`, `work_assignment: self-select`) are the
  inverse of the delegation-routing values (`through-unit`,
  `capability-match`) every delegation-shaped example uses.
- **Agents** (`agents/`):
  - `politics-desk` — beat reporter; owns the politics beat.
  - `business-desk` — beat reporter; owns the business beat.
  - `technology-desk` — beat reporter; owns the technology beat.
  - `wire-editor` — aggregates the desk's output; still a peer, does not
    delegate.

Every agent's `instructions:` teach the pattern explicitly: work your own
beat; when you find something cross-cutting, `sv.messaging.send` it to the
relevant peer; when something is newsroom-wide, `sv.messaging.multicast` it;
never delegate to a peer — they own their beat, you own yours.

## Using the package

Install via the CLI:

```bash
spring package install newsroom
```

Or use the portal wizard's **From catalog** source at `/units/create` and
select **Newsroom**. Both paths route through `POST /api/v1/packages/install`
and activate all artefacts atomically.

## Shape

The directory layout mirrors the other in-tree domain packages
(`packages/research/`, `packages/spring-voyage-oss/`) so the file-system
package catalogue (`GET /api/v1/packages`) exposes it on the CLI
(`spring package show newsroom`) and the portal (`/packages/newsroom`) without
any extra wiring.
