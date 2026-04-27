Triage an issue — decide whether to close, route to a v0.1 area, or park.

Usage: `/triage <issue-number>`

## Steps

1. Read the issue: `GH_TOKEN=$(gh-app token) gh issue view <N> --repo cvoya-com/spring-voyage`.
2. Decide one of:
   - **Close** — obsolete, superseded, or already fixed. Add a comment via `gh-app issue comment <N> -- --body "..."` recording the reason, then `GH_TOKEN=$(gh-app token) gh issue close <N> --repo cvoya-com/spring-voyage`.
   - **Route to a v0.1 area** — fits one of areas A–J (see `docs/plan/v0.1/README.md`). Apply the matching `area:*` label, set milestone `v0.1`, and wire the issue as a sub-issue of the area umbrella.
   - **Backlog** — candidate for future consideration. Apply label `backlog`; no milestone.
   - **Needs-thinking** — architectural or product decision required first. Apply label `needs-thinking`; the user owns these.
   - **Ambient** — tracked but no release commitment. Apply label `ambient`.
3. Apply via `gh-app` (the App identity is mandatory for writes):
   - Label / milestone: `GH_TOKEN=$(gh-app token) gh issue edit <N> --add-label "<label>" --milestone v0.1 --repo cvoya-com/spring-voyage`.
   - Sub-issue link: GraphQL `addSubIssue(issueId: <parent>, subIssueId: <child>)` — see user `~/.claude/CLAUDE.md` § "Issue Tracking" for the mutation pattern and parent/child node-id resolution.
4. Note the decision rationale in a comment on the issue.

For triage scope and conventions, see `docs/plan/v0.1/areas/h-triage.md`.
