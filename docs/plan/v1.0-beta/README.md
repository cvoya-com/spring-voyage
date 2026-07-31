# Spring Voyage v1.0-beta — Plan of Record

**Active release frame** of the 1.0.0 train. Supersedes the v0.1 frame (shipped as the `1.0.0-alpha` family; plan-of-record archived at [`docs/archive/plan/v0.1/`](../../archive/plan/v0.1/README.md)). This frame was briefly named "v0.2"; renamed 2026-07-31 so internal frame names follow the release train — see [`decisions.md`](decisions.md).

This directory is the canonical narrative for v1.0-beta planning and execution. Live status (what's in/out, who's doing what) lives in GitHub: milestone `v1.0-beta`, per-area umbrella issues, and `area:*` labels. The files here own the *intent*; GitHub owns the *state*.

## Strategic frame

The frame's centerpiece is the **Agent/Human/Team redesign** ([#3245](https://github.com/cvoya-com/spring-voyage/issues/3245) — plan of record in its comment thread), driven by a **new end-user experience exploration** ([#3246](https://github.com/cvoya-com/spring-voyage/issues/3246)):

- **Subjects, not composites.** Agents and humans are the only participants; teams become organizational/policy/resource scopes with no lifecycle. The `Unit` concept is eliminated.
- **Messages, not threads.** The message is the only interaction primitive; history views are computed projections over an append-only interaction ledger. The platform is never a participant.
- **Agents as individuals.** Intrinsic execution config; one continuous memory over the ledger; serialized turns by default with an opt-in dispatch tier for internal concurrency.
- **Shared artifacts.** A versioned, immutable artifact store with coordination locks, visible to agents and humans, so work products travel by reference.
- **Vision first.** Product and UX vision docs (`docs/product/`) lead, so experience requirements flow into the architecture decisions rather than after them.

Releases continue shipping `1.0.0-alpha.N` while the work lands; the first `1.0.0-beta.N` marks beta graduation.

## Exit criteria

- [ ] The four redesign ADRs merged with architecture/concepts docs updated: [#3247](https://github.com/cvoya-com/spring-voyage/issues/3247) (subjects), [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249) (interactions), [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250) (ledger & continuity), [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251) (artifacts).
- [ ] Implementation bands 1–4 landed (domain core; messaging & delivery; ledger & continuity; runtime surface & SDKs).
- [ ] Shared-artifacts MVP landed.
- [ ] Product & UX vision published ([#3246](https://github.com/cvoya-com/spring-voyage/issues/3246)) and at least one exploration round complete ([#3248](https://github.com/cvoya-com/spring-voyage/issues/3248)).
- [ ] Validation: the multi-agent self-contradiction failure class ([#3053](https://github.com/cvoya-com/spring-voyage/issues/3053)) is gone in a dogfood re-run on the new model.
- [ ] First `1.0.0-beta.N` release cut, with release notes documenting the alpha→beta reset (no state migration).

## Areas

Each area gets its own planning session, a narrative file under `areas/`, and an umbrella issue.

| ID | Area | File | Umbrella | Status |
| --- | --- | --- | --- | --- |
| A | Agent/Human/Team redesign | _authored with the ADRs_ | [#3245](https://github.com/cvoya-com/spring-voyage/issues/3245) | ADRs in flight |
| B | Next end-user experience | _authored with the vision docs_ | [#3163](https://github.com/cvoya-com/spring-voyage/issues/3163) → [#3246](https://github.com/cvoya-com/spring-voyage/issues/3246) | vision capture |

Further areas (dashboards, carried-over engineering) enter as they get their planning sessions.

## Workflow

- **Per-area planning session** produces or updates `areas/<x>.md` via PR.
- **`README.md`** updates when areas enter/exit or scope shifts materially.
- **`decisions.md`** is appended on strategy-level changes (with dates).
- **Issues** are the unit of execution; **plan docs** are the unit of intent.

## Conventions

- Aspirational content lives here (or under an explicit "Planned" callout) — see [CONVENTIONS.md](../../../CONVENTIONS.md).
- Don't propose mega-plans; defer detail to per-area planning sessions.
- Design lenses carried over from v0.1: Web Portal continuity and hosted-service-foundation compatibility.
