# Spring Voyage v0.1 — Decisions Log

Strategy-level decisions for v0.1, with dates. Append-only — corrections happen via new entries that supersede older ones, not by editing history.

## 2026-04-25 — V2 release scrapped

V2 release scrapped due to bug volume. V1 was internal only; first public release becomes **v0.1**. The "v2" name is no longer used in any new artefact.

## 2026-04-25 — V2.1 milestone dropped

No successor release defined yet after v0.1. The V2.1 milestone is dropped entirely; existing issues there are triaged milestone-blind alongside everything else.

## 2026-04-25 — v0.1 is the foundation for the hosted service

Architectural decisions in v0.1 must be compatible with hosting SV as a service. This is a design lens, not a separate area.

## 2026-04-25 — CLI is the primary UX

CLI becomes the primary user experience for v0.1. The CLI builds on the public Web API; no CLI-private API.

## 2026-04-25 — Web Portal continuity is a regression criterion

The current Web Portal continues to work on the same public Web API. No portal-private API. Every API change must preserve portal compatibility.

## 2026-04-25 — A new, separate UX for unit/agent interaction is started

Distinct from the current portal's management/configuration/monitoring focus. This is interactive-with-the-agent surface. Scope within v0.1 is TBD; full delivery may span beyond v0.1.

## 2026-04-25 — Issue triage is milestone-blind

Open issues are evaluated on merit regardless of which milestone they currently sit in. Milestone is not a filter signal during the v0.1 triage sweep.

## 2026-04-25 — ADRs are re-evaluated as part of v0.1

All ADRs reviewed for evolve / retire / stand. Owned by Area J.

## 2026-04-25 — Existing Claude / coding-agent config is candidate for evolution

`CLAUDE.md` (user + repo), `AGENTS.md`, `CONVENTIONS.md`, agent definitions, skills, plugins, MCPs — all up for review. Owned by Area A. No specific guidance line is treated as immutable.

## 2026-04-26 — Plan-of-record lives in `docs/plan/v0.1/` + per-area umbrellas

Hybrid tracking: this directory owns narrative; GitHub owns state via milestone `v0.1`, per-area umbrella issues (thin body, sub-issue panel), `area:*` labels, and sub-issues + `addBlockedBy` for real cross-area dependencies. No top-level v0.1 umbrella — the milestone + the README cover that role.

This refines the prior "no umbrella that duplicates the milestone" lesson (2026-04-18): umbrellas that anchor cross-cutting structure inside a milestone are net-positive when bodies stay thin (link + sub-issue panel) and don't replicate prose.
