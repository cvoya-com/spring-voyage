# Software Engineering Package

A domain package that ships a software engineering team — tech lead, backend engineer, and QA — wired to a GitHub repository with triage, PR-review, and coding-standards skills.

## What this package ships

- **Agents** (`agents/`):
  - `tech-lead` (Tech Lead) — reviews plans, provides technical guidance, makes architecture decisions, and breaks down work.
  - `backend-engineer` (Backend Engineer) — implements features, fixes bugs, writes tests, and opens pull requests.
  - `qa-engineer` (QA Engineer) — writes tests, analyses coverage, identifies bugs, and ensures quality across the codebase.
- **Unit** (`units/`): `engineering-team` — a hierarchical unit that routes incoming issues and PRs to the team member whose expertise best fits the work.
- **Skills** (`skills/`):
  - `triage-and-assign` — classify incoming GitHub issues and route them to the right team member.
  - `pr-review-cycle` — coordinate the review, revision, and merge cycle for pull requests.
  - `coding-standards` — enforce project conventions and quality standards during review.
- **Workflow** (`workflows/`): `software-dev-cycle` — end-to-end workflow template for issue-to-PR development cycles.

## Agent runtime

All agents use the `claude-code` tool backed by `claude-sonnet-4-6`. The execution image is `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` running under **podman**.

## Connector

The `engineering-team` unit binds the **GitHub** connector and listens for `issues`, `pull_request`, and `issue_comment` events on the repository you specify at install time.

## Required inputs

| Input | Description |
| --- | --- |
| `github_owner` | GitHub owner (org or user) hosting the repository. |
| `github_repo` | GitHub repository name. |
| `github_installation_id` | GitHub App installation ID for the Spring Voyage App on the target repository. Find it at **GitHub → your org → Settings → GitHub Apps → Spring Voyage → Configure** — the ID appears in the URL. |

## Installing the package

### CLI

```bash
spring package install software-engineering \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

### Portal

Navigate to `/settings/packages/software-engineering` and click **Install**. The wizard pre-fills the input fields from the package's declared inputs — fill in the three GitHub values and submit.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — agents route responses through the team orchestrator.
- **Work assignment**: capability-match — the orchestrator routes each task to the agent whose expertise best fits.
