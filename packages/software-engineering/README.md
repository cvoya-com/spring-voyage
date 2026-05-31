# Software Engineering

A ready-to-run software engineering team — a tech lead, a backend engineer, and a QA engineer — that picks up issues and pull requests from a GitHub repository, develops changes on their own worktrees, and opens PRs for review.

## What this package ships

- **Unit:** `engineering-team` — the team leader. It receives GitHub events and direct prompts, picks the member whose expertise best fits the work, delegates, and holds the thread back to you.
- **Agents:**
  - `tech-lead` — reviews designs and PRs, breaks down work, and sets technical direction.
  - `backend-engineer` — implements features, fixes bugs, writes tests, and opens pull requests.
  - `qa-engineer` — writes and strengthens tests, analyses coverage, and gates quality.
- **Skills** equipped on the team:
  - `triage-and-assign` — classify incoming work and route it to the right member.
  - `pr-review-cycle` — run the review → revise → merge loop for pull requests.
  - `coding-standards` — the quality bar applied during review.

## How the team works

The engineers clone the bound repository into their own workspace and develop each change on a dedicated worktree branched off the latest default branch. Before requesting review they make sure the branch is current, the project's build / lint / test gates pass, docs touched by the change are updated, and the PR closes the issues it resolves. The tech lead reviews for architectural fit and holds the line on scope; the QA engineer covers changes with tests. The unit coordinates the work and escalates to you — the human owner — when a call needs you.

If the target repository defines specialist subagents, the engineers dispatch focused work to them.

## Agent runtime

All agents use the `claude-code` runtime backed by `claude-sonnet-4-6`, on the `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` image.

## Connecting to GitHub

The `engineering-team` unit binds the **GitHub** connector and listens for `issues`, `pull_request`, and `issue_comment` events on the repository you choose at install time. Connect it through the Spring Voyage GitHub App or a personal access token — the install flow walks you through the choice, and the agents read `$GITHUB_TOKEN` either way.

### CLI

```bash
spring package install software-engineering \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

### Portal

Navigate to `/settings/packages/software-engineering` and click **Install**. The wizard prompts for the GitHub repository and how to authenticate, then activates the team.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — agents route responses through the team leader.
- **Work assignment**: capability-match — the team leader routes each task to the member whose expertise best fits.
