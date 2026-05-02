# Spring Voyage OSS

The **Spring Voyage OSS** unit is a built-in, hierarchical unit that uses Spring Voyage to develop Spring Voyage itself. It ships as a package (`packages/spring-voyage-oss/`) that is automatically visible in the platform catalog. When installed via `spring package install spring-voyage-oss`, it creates a five-unit hierarchy in a single atomic operation: a top-level coordination unit plus four role-flavored sub-units covering software engineering, design, product management, and program management.

The unit is the concrete realisation of the v0.1 stretch criterion: "SV is usable for further development of SV" (`docs/plan/v0.1/README.md`, exit criteria). It turns that criterion into something observable — a running team that plans, triages, implements, reviews, and ships the platform on itself.

## Why this exists

Confidence in a platform's primitives comes from using them. The Spring Voyage OSS unit is a stress test of the same primitives every operator uses:

- Hierarchical unit composition with four `members: [unit: ...]` entries.
- `execution.hosting: permanent` so containers stay warm across the continuous work of a development team.
- GitHub connector binding at template-apply time, flowing all GitHub App identity through the connector rather than hardcoding it.
- Agent images built on the BYOI path 1 conformance contract (ADR-0027): each image extends the omnibus base, adds role tooling, and inherits the bridge ENTRYPOINT unchanged.

When the SE team hits a friction point, that friction is a bug or improvement opportunity in the platform. When the PgM team needs a feature in `gh issue` integration, that need is a feature request against the platform's GitHub connector. The feedback loop is direct.

## Structure: the four sub-units

### Software Engineering (`sv-oss-software-engineering`)

Responsible for implementing features, fixing bugs, and running the build/test/lint loop.

**Members:** architect, dotnet-engineer, web-engineer, cli-engineer, qa-engineer, devops-engineer, security-engineer, connector-engineer, api-designer, docs-writer.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` — extends the omnibus base with:
- .NET 10 SDK (for `dotnet build SpringVoyage.slnx -c Release`, `dotnet test --solution SpringVoyage.slnx`, `dotnet format SpringVoyage.slnx --verify-no-changes`)
- `gh` CLI for GitHub App-mediated issue and PR work
- Node 22 + npm (inherited from omnibus), `ruff`, and full Playwright browser support (Chromium, Firefox, WebKit) including all required system dependencies

**Orchestrator prompt:** The sub-unit's `ai.prompt` encodes the project's working norms: scope discipline (file-and-move-on default; bar for in-scope expansion is "the PR's declared goal is materially broken without it"), triage (close / route to area / park, with `area:*` labels and native issue types), PR review checklist (declared scope, ADR alignment, OpenAPI contract drift, formatting, lints, tests at the solution root), issue tracking (milestones for releases, issue types for category, labels only for what those don't cover, `sub-issues` + `blocked-by` for task dependencies), worktree workflow (branch off latest `origin/main`, rebase before push), pre-push gates (solution-wide build, test, format, lint, knip, tsc all green), and identity (all GitHub writes through the Spring Voyage GitHub App via this unit's connector binding).

Each member agent inherits the orchestrator's frame. The orchestrator routes incoming work to the appropriate persona based on file paths and the area touched.

### Design (`sv-oss-design`)

Responsible for visual review, component mockups, and accessibility checks.

**Members:** design-engineer.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-design` — adds Playwright with Chromium as the primary browser, `@mermaid-js/mermaid-cli` for diagram rendering, and ImageMagick for image processing.

**Orchestrator prompt:** Tuned to visual review and accessibility — screenshot capture, diagram diff, WCAG checklist.

### Product Management (`sv-oss-product-management`)

Responsible for issue triage, roadmap maintenance, sprint planning, and requirements.

**Members:** pm.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management` — adds `gh` CLI, `@mermaid-js/mermaid-cli` for roadmap diagrams, and `markdownlint-cli2` for documentation hygiene.

**Orchestrator prompt:** Tuned to the v0.1 plan (`docs/plan/v0.1/README.md`), milestone scheme, and the area label taxonomy. Issues are triaged against active milestones; new work is placed in the correct area bucket before any planning artefact is written.

### Program Management (`sv-oss-program-management`)

Responsible for milestone hygiene, sub-issue and blocked-by relationships, and dependency tracking.

**Members:** program-manager.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` — adds `gh` CLI and `markdownlint-cli2`.

**Orchestrator prompt:** Tuned to the three issue primitives (milestones, issue types, labels), the sub-issue/blocked-by relationship model, and the rule that prose "blocked by #N" in a body is not enough — the relationship must be registered natively so it surfaces in GitHub's dependency view.

## How the unit runs

Each sub-unit declares `execution.hosting: permanent`. The agent containers stay warm across messages — the right default for a team that runs continuously rather than responding to isolated, ephemeral requests.

The top-level `spring-voyage-oss` unit lists the four sub-units as `members`. Messages routed to the top-level unit are dispatched by the unit's orchestration strategy to the appropriate sub-unit; each sub-unit then routes internally to the appropriate member agent.

Each sub-unit declares a GitHub connector at template level:

```yaml
connectors:
  - type: github
    config:
      events: ["issues", "issue_comment", "pull_request", "pull_request_review"]
```

The `owner`, `repo`, and `installation_id` fields are intentionally absent from the checked-in package YAML — they require per-deployment identity that does not belong in source. The operator supplies them as inputs when running `spring package install spring-voyage-oss` (or through the **Catalog** path in the New Unit wizard), and the platform creates the unit hierarchy and connector bindings atomically as part of the install. See [Connectors](connectors.md) for the binding model and the GitHub connector's repository and reviewer discovery behaviour.

## How the unit dogfoods the platform

The Spring Voyage OSS unit exercises platform features as a working team, not as a test fixture.

**Software Engineering** runs the same commands any operator would run against the codebase:
- `dotnet build SpringVoyage.slnx -c Release` to verify the build
- `dotnet test --solution SpringVoyage.slnx` to run the full test suite
- `dotnet format SpringVoyage.slnx --verify-no-changes` for format enforcement
- `npm run lint`, `npm --workspace=spring-voyage-dashboard run knip`, `npm --workspace=spring-voyage-dashboard run typecheck` for the web layer

**Product Management** plans against `docs/plan/v0.1/README.md` and manages issues in `cvoya-com/spring-voyage` via the Spring Voyage GitHub App. Sprint planning outputs live in the same `docs/plan/` structure the project already uses.

**Program Management** manages milestones and sub-issue/blocked-by relationships via `gh issue` commands, exercising the same GitHub connector skills available to any unit.

**Design** renders screenshots via Playwright and produces mockups and accessibility reports against the portal's UI — exercising the same Playwright tooling the SE team's QA agents use.

Bugs the team encounters are bugs in Spring Voyage. Friction they hit — in the CLI, the connector, the portal wizard — is improvement feedback for the platform. The team works in the open: every issue it files, every PR it raises, and every review it posts flows through the Spring Voyage GitHub App, making the identity and access model a live part of the feedback loop.

## Where to go next

- `docs/guide/operator/dogfooding-oss-unit.md` — step-by-step bring-up: prerequisites, CLI and wizard paths, post-create verification, and troubleshooting.
- `docs/decisions/0034-oss-dogfooding-unit.md` — design rationale: why these four roles, the FROM-omnibus image strategy, `hosting: permanent`, and the connector-binding-at-apply-time pattern.
- `packages/spring-voyage-oss/README.md` — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
