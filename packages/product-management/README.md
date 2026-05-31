# Product Management

A product squad — a product manager and a product designer — wired to a GitHub repository and equipped with triage, roadmap, sprint-planning, and design-review skills.

## What this package ships

- **Unit:** `product-squad` — routes each request to the PM or the designer based on what it needs, and keeps roadmap and design decisions aligned.
- **Agents:**
  - `pm` (Product Manager) — triages incoming requests, maintains the roadmap, plans sprints, and writes requirements.
  - `design` (Product Designer) — shapes the user experience, produces design artefacts, and reviews proposals for usability and accessibility.
- **Skills:**
  - `issue-triage` — classify and prioritise incoming issues against the current roadmap.
  - `roadmap-management` — group work into themes and milestones and keep the roadmap current.
  - `sprint-planning` — scope, estimate, and sequence high-value work within team capacity.
  - `design-review` — evaluate design proposals for usability, accessibility, and consistency.

## Agent runtime

All agents use the `claude-code` runtime backed by `claude-sonnet-4-6`, on the `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` image.

## Connecting to GitHub

The `product-squad` unit binds the **GitHub** connector and listens for `issues`, `issue_comment`, and `pull_request` events on the repository you choose at install time. Connect it through the Spring Voyage GitHub App or a personal access token — the install flow walks you through the choice.

### CLI

```bash
spring package install product-management \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

### Portal

Navigate to `/settings/packages/product-management` and click **Install**. The wizard prompts for the GitHub repository and how to authenticate, then activates the squad.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — agents route responses through the squad orchestrator.
- **Work assignment**: capability-match — the orchestrator routes each task to the agent whose expertise best fits.
