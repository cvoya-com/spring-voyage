# v1.0-beta — Decision Log

Dated, strategy-level decisions for the v1.0-beta release frame (named "v0.2" until 2026-07-31). Append-only; newest at the bottom.

## 2026-05-29 — v0.2 opened; v0.1 plan archived

v0.1 shipped as the `1.0.0-alpha` family. Its plan-of-record moved to [`docs/archive/plan/v0.1/`](../../archive/plan/v0.1/README.md), and `docs/plan/v0.2/` becomes the active plan frame under `docs/plan/`. The `/areas` and `/triage` tooling resolves the active plan as the latest version directory under `docs/plan/`, so it now points here. Area definitions for v0.2 are TBD — see [`README.md`](README.md).

## 2026-07-31 — Frame renamed v0.2 → v1.0-beta; redesign plan of record settled

Internal frame names now follow the release train: since v0.1 shipped as `1.0.0-alpha`, the next frame is the beta graduation of the 1.0.0 train, not a "v0.2". This directory renamed `docs/plan/v0.2/` → `docs/plan/v1.0-beta/`; GitHub milestones renamed `v0.2` → `v1.0-beta` and `v0.3` → `backlog` (a frame-agnostic parking lot).

The frame's centerpiece and exit criteria are set by the Agent/Human/Team redesign plan of record ([#3245](https://github.com/cvoya-com/spring-voyage/issues/3245)): four architecture decision records ([#3247](https://github.com/cvoya-com/spring-voyage/issues/3247) subjects, [#3249](https://github.com/cvoya-com/spring-voyage/issues/3249) interactions, [#3250](https://github.com/cvoya-com/spring-voyage/issues/3250) ledger & continuity, [#3251](https://github.com/cvoya-com/spring-voyage/issues/3251) artifacts), a prioritized product/UX vision lane ([#3246](https://github.com/cvoya-com/spring-voyage/issues/3246), [#3248](https://github.com/cvoya-com/spring-voyage/issues/3248)), and eight implementation bands filed as each record merges. Beta graduation = the first `1.0.0-beta.N` release after bands 1–4, the artifacts MVP, and the multi-agent validation re-run land.
