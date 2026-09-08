# Area A — Agent, Human, Team

**Intent narrative for the subjects reshape.** Plan of record:
[#3245](https://github.com/cvoya-com/spring-voyage/issues/3245). Live status
(issues, assignments, sequencing) lives in GitHub; this file owns the intent.

## Scope

Replace the fused **unit** concept with three subjects carrying sharp
contracts:

- **Agent** — an AI individual: stable identity, executes, sends/receives
  messages, owns memory.
- **Human** — a human individual: stable identity, sends/receives messages.
- **Team** — a recursive organizational, policy, and resource scope. Not a
  participant: no mailbox, no runtime, no lifecycle.

Identity, team membership, and role assignment become three separately managed
relations; addressing gains send-time-resolved selectors (role @ team, team
with a recursive flag) alongside direct addresses; Hats are retired in favour
of one canonical Human per authenticated user with package slots bound at
instantiation; execution config becomes intrinsic to the agent; and `Unit` is
eliminated from every surface as a clean cut (no alias, no data migration —
alpha-era state does not carry into beta).

Out of scope for this area: message/delivery semantics, the interaction
ledger, and shared artifacts — each owned by a companion record below.

## The four-record stack

| Record | Issue | Owns |
|---|---|---|
| ADR-0070 — Subjects ([`docs/decisions/0070-subjects-agents-humans-teams.md`](../../../decisions/0070-subjects-agents-humans-teams.md)) | [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247) | Who exists; identity / membership / role relations; addresses and selectors; team topology; unit elimination |
| ADR — Interactions | [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249) | Message semantics, audience expansion and the delivery contract, selector-resolution snapshots on the wire |
| ADR — Interaction ledger | [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250) | History, memory, execution continuity; membership history that keeps past deliveries explicable |
| ADR — Shared artifacts | [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251) | Team-owned work products: artifact contract, versioning, access |

ADR-0070 lands first and gates the others: interactions and the ledger resolve
against the subject relations it defines; artifacts hang off team resource
ownership.

## Band plan

Execution is banded so each band leaves the tree consistent.

- **Band 1 — domain core.** The Team aggregate and tree topology (tenant-
  rooted, single parent); the membership and role-assignment relations
  (covering agents and humans uniformly); address/selector types with
  parser-enforced non-routable selector schemes; `UnitActor` retirement (teams
  have no actor); the directory reshape (kind-agnostic role resolution, roster
  projection seam); the EF clean-cut migration (fresh baseline, no data
  migration); and the package grammar (`kind: Team`, `- team:` members, human
  slots with instantiation-time binding and the distinct-principals
  constraint). Draft issue list reviewed under [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247).
- **Band 2 — surfaces.** API (`/teams`), CLI (`spring team`), portal explorer
  and inbox simplification (Hat machinery removal), OpenAPI/Kiota/
  openapi-typescript regeneration, prompt-surface vocabulary.
- **Band 3 — companions.** Implementation waves for the interactions, ledger,
  and artifacts records as each is accepted.

Band boundaries are working assumptions for sequencing, not commitments; the
issue tracker is authoritative for what ships when.

## Design lenses

- **Single-user OSS stays the degenerate case** of the multi-user model —
  every mechanism (slot binding, disclosure projection, policy composition)
  collapses to one person without a forked code path.
- **Fail-closed authorization** is a separate layer; no relation in this area
  grants authority.
- **Clean cut over compatibility**: structured errors naming ADR-0070 instead
  of aliases; release notes document the alpha-state reset.
