---
name: spring-voyage-daily-triage
description: Daily read-only triage of all open cvoya-com/spring-voyage issues: grouping/dedup/consolidation, milestone moves, attention/design/executable split, and prioritized parallel execution waves.
---

## Objective
Produce a comprehensive, code-grounded triage of ALL open issues in the GitHub repo `cvoya-com/spring-voyage`, and from it a prioritized set of execution "waves." This is **advisory and read-only**: deliver a report as your final message. Do NOT close, edit, label, re-milestone, or comment on any issue, and do NOT open or modify PRs or push code.

## Runtime
This skill is designed to run unattended as a **daily cloud routine** in Claude Code or Codex. Each run is a fresh, autonomous session with `cvoya-com/spring-voyage` already cloned into your working directory, and there is no human in the loop — do not wait for input or approvals. The "report as your final message" IS the deliverable: it is captured in the routine run's transcript, where `@savasp` reviews it. Producing no report is a failed run.

## Repository & context
- GitHub repo: `cvoya-com/spring-voyage`. Get issue data via the **GitHub connector** (the `mcp__github__*` tools), which routines include by default, or via the `gh` CLI when it is authenticated (set `GH_TOKEN` in the routine environment). The `gh` commands in Method are illustrative; the equivalent connector calls are equally fine — use whichever is available.
- Code grounding: the repo is **already checked out in your working directory** (the routine clones it fresh each run) — read it directly; do not assume any local absolute path. Only fall back to `gh repo clone cvoya-com/spring-voyage` into a temp dir if you somehow are not inside a checkout. Read `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, `docs/plan/`, and `docs/decisions/` (ADRs) for ground truth before judging anything.
- Release framing: v0.1 shipped as `v1.0.0-alpha`; **v0.2** is the internal next phase toward a non-alpha **v1**; **v0.3** is later; the `backlog` label = kept but unscheduled. Reserve v0.2 for the toward-v1 critical path.
- The architecture changes quickly. ALWAYS re-read the highest-numbered ADRs and the most recently closed issues before judging an open issue — a once-valid issue may now be obsolete because a subsystem was deleted or the direction changed (e.g. past examples: the platform stopped owning orchestration; the GitHub MCP-skill surface was deleted; memory/threads were re-architected).

## Method
1. Fetch all open issues: `gh issue list -R cvoya-com/spring-voyage --state open --limit 400 --json number,title,labels,milestone,body,createdAt,updatedAt` (or the connector equivalent). Fetch recently-closed for done/superseded detection: `gh issue list -R cvoya-com/spring-voyage --state closed --limit 1000 --json number,title,closedAt`.
2. Skim the ADRs in `docs/decisions/` (especially the newest) and note architectural shifts that obsolete issues.
3. The open set is large (well over 100). Fan out parallel sub-agents (Agent tool, `general-purpose`) partitioned by theme — each reads its bucket's issue bodies, greps the local checkout to VERIFY claims, and returns structured per-issue verdicts. Then synthesize yourself. Cover every open issue exactly once and validate full coverage (no issue dropped or double-counted).
4. Per issue assign: verdict ∈ {KEEP, CLOSE-OBSOLETE, CLOSE-DONE, CLOSE-DUPLICATE, CONSOLIDATE, NEEDS-DECISION}; recommended milestone ∈ {v0.2, v0.3, backlog}; duplicate_of / consolidate_with; depends_on / blocks; confidence. VERIFY every CLOSE-DONE / CLOSE-OBSOLETE against code or a specific closed issue and cite it. Prefer KEEP when genuinely unsure and flag low confidence rather than guessing.

## Report (final message, markdown) — these sections
1. **Summary counts** — totals by verdict and recommended milestone; what changed vs. the prior run if discernible (newly filed, newly closed, new ADRs).
2. **Close recommendations** — split into *obsolete* (premise changed; cite the ADR/deleted subsystem) and *already-done* (cite the shipping issue/PR/code path).
3. **Duplicates & consolidations** — duplicate pairs (which to keep as canonical, which to fold in) and sibling clusters that should be grouped under an umbrella or wired with dependency edges.
4. **Milestone moves** — current → recommended, one-line rationale each; keep v0.2 = the toward-v1 critical path.
5. **Attention / Decision / Executable** — (a) issues the maintainer @savasp should personally weigh in on; (b) issues needing design/ADR work before they can be scheduled; (c) issues ready to execute as-is.
6. **Prioritized execution waves** — the headline deliverable. An ordered list of waves; each wave is a set of issues runnable concurrently. Optimize for: **maximize parallelism** (group issues in one wave only if genuinely orthogonal); **minimize PR count** by grouping closely-related issues that touch the same area into a single PR where sensible; **group relevant issues together**; **minimize merge-conflict risk** — never place two issues that edit the same files/projects/areas in the same parallel wave; sequence them across waves instead. For each wave give: the issues and proposed PR groupings within it, the files/areas each PR touches, why the parallel items are conflict-safe, and dependencies on prior waves. Put NEEDS-DECISION items in a separate "blocked on decision" track, not in the executable waves.

## Constraints
- Read-only. Never mutate issues, milestones, labels, PRs, or code. (Repo convention: any GitHub write would go through the `gh-app` CLI — but this routine never writes.)
- Honor `AGENTS.md` / `CLAUDE.md` conventions: scope discipline; v0.2-vs-v0.3 discipline; the native issue-types / milestones / sub-issues tracking model; "thread" is internal while agent-facing surfaces say "conversation"; refer to the maintainer as `@savasp`.
- Lead the report with the close list, the wave plan, and anything needing a human decision, so the maintainer can act in minutes.
