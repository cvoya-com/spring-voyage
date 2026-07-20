---
name: triage
description: "Triage an issue \u2014 decide whether to close, route to an active-plan area, or park. Codex equivalent of the Claude /triage command."
---

# triage

Codex equivalent of the Claude `/triage` command. Follow the same project workflow, using any provided user request as the command arguments.

Triage an issue — decide whether to close, route to an active-plan area, or park.

Usage: `$triage <issue-number>`

## Steps

1. Read the issue: `gh issue view <N> --repo cvoya-com/spring-voyage`.
2. Decide one of:
   - **Close** — obsolete, superseded, or already fixed. Comment with the reason, then close the issue.
   - **Route to an active-plan area** — fits an area in the active plan-of-record (the latest version under `docs/plan/`; see its `README.md`). Apply the matching `area:*` label, set the active release milestone, and wire the issue as a sub-issue of the area umbrella. (v0.1's area taxonomy A–J is archived at `docs/archive/plan/v0.1/`.)
   - **Backlog** — candidate for future consideration. Apply label `backlog`; no milestone.
   - **Needs-thinking** — architectural or product decision required first. Apply label `needs-thinking`; the user owns these.
   - **Ambient** — tracked but no release commitment. Apply label `ambient`.
3. Apply the triage decision:
   - Label / milestone: `gh-app gh issue edit <N> --add-label "<label>" --milestone "<active release, e.g. v0.2>" --repo cvoya-com/spring-voyage`.
   - Comments / closing: use `gh-app gh issue comment` and `gh-app gh issue close`.
   - Sub-issue link: `gh-app link-issue <N> --sub-issue-of <parent>` (see the `github-app` skill).
4. Note the decision rationale in a comment on the issue.

For triage scope and conventions, see `docs/archive/plan/v0.1/areas/h-triage.md`.
