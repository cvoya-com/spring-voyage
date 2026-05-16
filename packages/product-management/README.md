# Product Management Package

> **Tier 5 of 7.** Adds a **required** GitHub connector, three install-time inputs, and package-level skills consumed by every member of the unit. Builds on the optional-connector pattern from [`research`](../research/); the next package ([`software-engineering`](../software-engineering/)) layers a Dapr workflow on top of the same shape.

A domain package that ships a product squad — a product manager and a product designer — wired to a GitHub repository and equipped with triage, roadmap, sprint-planning, and design-review skills.

## What this package ships

- **Agents** (`agents/`):
  - `pm` (Product Manager) — triages incoming requests, maintains the roadmap, plans sprints, and writes requirements.
  - `design` (Product Designer) — shapes user experience, produces design artifacts, and reviews proposals for usability and accessibility.
- **Unit** (`units/`): `product-squad` — a hierarchical unit that routes work to the PM or designer based on the nature of the request and keeps roadmap and design decisions aligned.
- **Skills** (`skills/`):
  - `issue-triage` — classify and prioritize incoming GitHub issues against the current roadmap.
  - `roadmap-management` — group work into themes and milestones, keep the roadmap current.
  - `sprint-planning` — scope, estimate, and sequence high-value work within team capacity.
  - `design-review` — evaluate design proposals for usability, accessibility, and consistency.

## Agent runtime

All agents use the `claude-code` tool backed by `claude-sonnet-4-6`. The execution image is `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` running under **podman**.

## Connector

The `product-squad` unit binds the **GitHub** connector and listens for `issues`, `issue_comment`, and `pull_request` events on the repository you specify at install time.

## Required inputs

| Input | Description |
| --- | --- |
| `github_owner` | GitHub owner (org or user) hosting the repository. |
| `github_repo` | GitHub repository name. |
| `github_installation_id` | GitHub App installation ID for the Spring Voyage App on the target repository. Find it at **GitHub → your org → Settings → GitHub Apps → Spring Voyage → Configure** — the ID appears in the URL. |

## Installing the package

### CLI

```bash
spring package install product-management \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

### Portal

Navigate to `/settings/packages/product-management` and click **Install**. The wizard pre-fills the input fields from the package's declared inputs — fill in the three GitHub values and submit.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — agents route responses through the squad orchestrator.
- **Work assignment**: capability-match — the orchestrator routes each task to the agent whose expertise best fits.
