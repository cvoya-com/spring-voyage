# Spring Voyage v0.1 — Plan of Record

**Active release frame.** First public release of Spring Voyage — the open-source collaboration platform for teams of AI agents and the humans they work with. Replaces the scrapped V2 release. V1 was internal-only.

This directory is the canonical narrative for v0.1 planning and execution. Live status (what's in/out, who's doing what) lives in GitHub: milestone `v0.1`, per-area umbrella issues, and `area:*` labels. The files here own the *intent*; GitHub owns the *state*.

## Strategic frame

- V2 release scrapped on 2026-04-25 — bug volume blocked release.
- First public release becomes **v0.1**, intended as the foundation for the hosted service.
- V1 was internal only; the "v2" name is no longer in use.
- v0.1 is foundational, not feature-complete — it establishes the contracts, boundaries, and primary UX (CLI) that subsequent releases evolve.

See [`decisions.md`](decisions.md) for the dated decision log.

## Exit criteria

- [ ] **CLI** — fully functional, well-tested; primary user experience for v0.1.
- [ ] **Public Web API** — surface reviewed, OpenAPI contract documented, frozen for v0.1.
- [ ] **Web Portal** — current portal continues to work on the same public Web API (regression criterion).
- [ ] **New unit/agent UX** — separate UX started for interacting with units/agents (vs current portal's management/configuration/monitoring focus). Scope in v0.1 TBD.
- [ ] **ADR-0029 boundaries** — tenant/platform/UX boundaries documented; component-API surface mapped; agent runtime/orchestrator contracts published.
- [ ] **Conversation concept (#1123)** — implemented (term may be renamed during the area planning session).
- [ ] **Documentation overhaul** — split user/operator vs developer audiences; less verbose, more useful.
- [ ] **Code review + cleanup pass** — discovery of areas needing decomposition; targeted PRs.
- [ ] **All open issues triaged** — milestone-blind sweep.
- [ ] **ADRs re-evaluated** — identify which evolve, retire, or stand.
- [ ] **Stretch:** SV is usable for further development of SV (dogfooding).

## Design lenses (apply to every area)

1. **Web Portal continuity** — every API change must keep the current portal working on the same public Web API. No portal-private API.
2. **Hosted-service foundation** — every architectural decision must be hostable-as-a-service compatible.

## Areas

Each area gets its own planning session, narrative file under `areas/`, and umbrella issue.

| ID | Area | File | Umbrella | Status |
| --- | --- | --- | --- | --- |
| A | Coding-agent config | [areas/a-agent-config.md](areas/a-agent-config.md) | [#1214](https://github.com/cvoya-com/spring-voyage/issues/1214) | ✅ Done |
| B | Documentation overhaul | [areas/b-docs.md](areas/b-docs.md) | [#1215](https://github.com/cvoya-com/spring-voyage/issues/1215) | ✅ Done |
| C | Public Web API + OpenAPI | [areas/c-web-api.md](areas/c-web-api.md) | [#1216](https://github.com/cvoya-com/spring-voyage/issues/1216) | ✅ Done |
| D | ADR-0029 boundaries + component APIs | [areas/d-adr-0029.md](areas/d-adr-0029.md) | [#1217](https://github.com/cvoya-com/spring-voyage/issues/1217) | 🟢 D1–D3d shipped; 3 deferred sub-issues remain |
| E1 | CLI as primary UX | [areas/e1-cli.md](areas/e1-cli.md) | [#1218](https://github.com/cvoya-com/spring-voyage/issues/1218) | ✅ Done |
| E2 | New unit/agent-interaction UX | [areas/e2-new-ux.md](areas/e2-new-ux.md) | [#1219](https://github.com/cvoya-com/spring-voyage/issues/1219) | 🟢 Planning done; sub-issues to be filed |
| F | Conversation concept (#1123) | [areas/f-conversation.md](areas/f-conversation.md) | [#1220](https://github.com/cvoya-com/spring-voyage/issues/1220) | 🟢 F1/F2/F3 done; 2 sub-issues v0.1-deferred |
| G | Code review + decomposition | [areas/g-code-cleanup.md](areas/g-code-cleanup.md) | [#1221](https://github.com/cvoya-com/spring-voyage/issues/1221) | ✅ Done |
| H | Issue triage (milestone-blind) | [areas/h-triage.md](areas/h-triage.md) | [#1222](https://github.com/cvoya-com/spring-voyage/issues/1222) | ✅ Done |
| J | ADR audit + re-evaluation | [areas/j-adr-audit.md](areas/j-adr-audit.md) | [#1223](https://github.com/cvoya-com/spring-voyage/issues/1223) | ✅ Done |

Legend: ✅ done, 🟢 in-progress / partial, 🟡 newly unblocked / planning pending.

## Dependency picture

```text
Pre-work ✅  (rename V2 → v0.1; drop V2.1; retire stale umbrellas)
   │
   ├──►  A ✅  coding-agent config
   ├──►  H ✅  issue triage (milestone-blind)
   ├──►  J ✅  ADR audit + re-evaluation
   ├──►  C ✅  Web API (C1 audit + C2 freeze)
   ├──►  B ✅  doc audience-split + cleanup
   └──►  G ✅  code review + cleanup
                              │
                              ▼
                        D 🟢  ADR-0029 boundaries (D1–D3d done; 3 deferred sub-issues)
                        F 🟢  conversation concept (F1/F2/F3 done; 2 deferred sub-issues)
                              │
                              ▼
                        E1 ✅  CLI on top of frozen API
                        E2 🟢  new unit/agent UX (planning done; sub-issues to be filed; ADR-0033)
```

C and B each had two phases — audit/decision early (parallelisable), freeze/rewrite after architectural settling. Both are now complete.

## Cross-cutting umbrellas (post-kickoff)

Two v0.1 blockers surfaced mid-flight and do not map onto a single A–J area; both are tracked under their own umbrellas:

- **[#1554](https://github.com/cvoya-com/spring-voyage/issues/1554) — Package as the unit of bundling, installation, and export.** Collapses the *template* / *package* split, lands atomic multi-unit install, and rewires the new-unit / new-agent wizards onto one install pipeline. Forcing function: [#1530](https://github.com/cvoya-com/spring-voyage/issues/1530) (the OSS dogfooding package's README promises capabilities the v0.1 install path does not yet have). Locked in [ADR-0035](../../decisions/0035-package-as-bundling-unit.md).
- **[#1556](https://github.com/cvoya-com/spring-voyage/issues/1556) — v0.1 documentation reality audit.** Concept / guide / architecture / package READMEs swept for "describes-current-implementation vs aspirational" drift; convention added that aspirational content lives in `docs/plan/` or carries an explicit "Planned" callout.

## Workflow

- **Per-area planning session** produces or updates `areas/<x>.md` via PR.
- **`README.md`** updates when areas enter/exit or scope shifts materially.
- **`decisions.md`** appended on strategy-level changes (with dates).
- **Issues** are the unit of execution; **plan docs** are the unit of intent.
- **Triage and execution** flow through GitHub issues + the area's umbrella.

## Conventions

- Stop using "V2" or "V2.1" in any new artefact. Use **v0.1**.
- Don't propose mega-plans for v0.1 — defer detail to per-area planning sessions.
- Apply both design lenses to every area's design.
- Treat existing `CLAUDE.md` (user + repo) and Claude config (`.claude/agents`, skills, plugins, MCPs) as candidates for evolution — see Area A.

## Tracking model

Hybrid:

- **Files (this directory)** own narrative.
- **Milestone `v0.1`** owns release scope (no top-level umbrella — milestone + this README cover that).
- **Per-area umbrella issues** anchor sub-issues + discussion thread per area; bodies are thin (link to `areas/<x>.md` + sub-issue panel).
- **`area:*` labels** filter cross-area issues that don't fit cleanly under one umbrella.
- **Sub-issues + `addBlockedBy`** express real cross-area dependencies.
