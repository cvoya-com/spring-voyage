# Spring Voyage OSS

The **Spring Voyage OSS** unit is a built-in, hierarchical unit that uses Spring Voyage to develop Spring Voyage itself. It ships as a package (`packages/spring-voyage-oss/`) that is automatically visible in the platform catalog. When installed via `spring package install spring-voyage-oss`, it creates a three-unit hierarchy in a single atomic operation: a top-level coordination unit plus two role-flavoured sub-units covering software engineering and program management.

The unit is the concrete realisation of the dogfooding stretch criterion: "SV is usable for further development of SV". That criterion was first stated as a v0.1 stretch goal in `docs/plan/v0.1/README.md` and carries forward across plan versions. The unit turns it into something observable — a running team that plans, triages, implements, reviews, and ships the platform on itself.

## Why this exists

Confidence in a platform's primitives comes from using them. The Spring Voyage OSS unit is a stress test of the same primitives every operator uses:

- Hierarchical unit composition with `members: [unit: ...]` entries.
- `execution.hosting: permanent` so containers stay warm across the continuous work of a development team.
- GitHub connector binding at template-apply time, flowing all GitHub App identity through the connector rather than hardcoding it.
- Agent images built on the BYOI path 1 conformance contract (ADR-0027): each image extends `spring-voyage-agent-base`, adds the Claude Code CLI, role tooling, and inherits the bridge ENTRYPOINT unchanged.

When the SE sub-unit hits a friction point, that friction is a bug or improvement opportunity in the platform. When the PgM sub-unit needs a feature in `gh issue` integration, that need is a feature request against the platform's GitHub connector. The feedback loop is direct.

## Structure: the two sub-units

### Software Engineering (`sv-oss-software-engineering`)

Responsible for implementing features, fixing bugs, code review, and running the build/test/lint loop.

**Member:** A single `software-engineer` agent built on Claude Code. Specialised work is dispatched to repository-defined persona subagents under `.claude/agents/` (architect, dotnet-engineer, web-engineer, cli-engineer, api-designer, connector-engineer, qa-engineer, devops-engineer, security-engineer, design-engineer, docs-writer). The persona files are read by Claude Code's Task tool at dispatch time, so the package itself does not ship persona prompts.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` — extends `spring-voyage-agent-base` with:
- Claude Code CLI (Anthropic's claude-code launcher)
- .NET 10 SDK (for `dotnet build SpringVoyage.slnx -c Release`, `dotnet test --solution SpringVoyage.slnx`, `dotnet format SpringVoyage.slnx --verify-no-changes`)
- `gh` CLI for GitHub App-mediated issue and PR work
- Node 22 + npm (inherited from spring-voyage-agent-base), `ruff`, and full Playwright browser support (Chromium, Firefox, WebKit) including all required system dependencies

**Anchor documents:** The sub-unit's `instructions:` field points at the project's checked-in rules — `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, the `docs/architecture/` and `docs/decisions/` indexes, and whichever plan version is active under `docs/plan/`. The prompt does not duplicate those documents; it tells the agent to read them and defer to them when this prompt and a file disagree, so the package stays useful as the active milestone changes.

**Slash-command skills:** The sub-unit uses the canonical slash-command skills under `.agents/skills/` — `/build`, `/test`, `/lint`, `/openapi-diff`, `/triage`, `/adr-new`, `/web` — for CI-aligned invocations.

### Program Management (`sv-oss-program-management`)

Responsible for issue triage, milestone hygiene, sub-issue and blocked-by relationships, and dependency tracking.

**Member:** A single `program-manager` agent.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` — adds `gh` CLI and `markdownlint-cli2`.

**Anchor documents:** `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, and the active plan-of-record under `docs/plan/` (along with its `areas/` subdirectory). The sub-unit's prompt encodes the three issue primitives (milestones, issue types, labels), the sub-issue/blocked-by relationship model, and the rule that prose "blocked by #N" in a body is not enough — the relationship must be registered natively so it surfaces in GitHub's dependency view.

**Slash-command skills:** `/triage` — the canonical triage flow against the active area taxonomy.

## How the unit runs

Each sub-unit declares `execution.hosting: permanent`. The agent containers stay warm across messages — the right default for a team that runs continuously rather than responding to isolated, ephemeral requests.

Each unit and member agent runs in its own container with a per-agent persistent volume mounted at `$SPRING_WORKSPACE_PATH` (`/spring/workspace`). The sub-unit orchestrators and their engineer / PM members clone the bound repository (`$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO`) into that volume on first use, refresh `main` at the start of every task, and store derived data under `$SPRING_WORKSPACE_PATH/cache/` so subsequent turns avoid round-tripping to GitHub. Software-engineering instances additionally develop each PR in a worktree under `$SPRING_WORKSPACE_PATH/worktrees/<task>` so the clone itself stays on `main`. The umbrella `spring-voyage-oss` unit is a router and does not clone the repo — its job is to delegate to whichever sub-unit owns the work.

The top-level `spring-voyage-oss` unit lists the two sub-units as `members`. Messages routed to the top-level unit invoke the top-level unit's runtime; the runtime uses the orchestration tools to inspect and delegate to the appropriate sub-unit, which then runs its own runtime to delegate to its single member agent.

Each sub-unit declares a GitHub connector requirement. The `owner`, `repo`, and `installation_id` fields are intentionally absent from the checked-in package YAML — they require per-deployment identity that does not belong in source. The operator supplies them as inputs when running `spring package install spring-voyage-oss` (or through the **Catalog** path in the New Unit wizard), and the platform creates the unit hierarchy and connector bindings atomically as part of the install. See [Connectors](connectors.md) for the binding model and the GitHub connector's repository and reviewer discovery behaviour.

## How the unit dogfoods the platform

The Spring Voyage OSS unit exercises platform features as a working team, not as a test fixture.

**Software Engineering** runs the same commands any operator would run against the codebase:
- `dotnet build SpringVoyage.slnx -c Release` to verify the build
- `dotnet test --solution SpringVoyage.slnx` to run the full test suite
- `dotnet format SpringVoyage.slnx --verify-no-changes` for format enforcement
- `npm run lint`, `npm --workspace=spring-voyage-dashboard run knip`, `npm --workspace=spring-voyage-dashboard run typecheck` for the web layer

**Program Management** manages issues, milestones, and sub-issue/blocked-by relationships in `cvoya-com/spring-voyage` via `gh issue` commands, exercising the same GitHub connector skills available to any unit. Sprint planning outputs live in the same `docs/plan/` structure the project already uses; the plan-of-record is whichever version directory under `docs/plan/` is currently active.

Bugs the team encounters are bugs in Spring Voyage. Friction it hits — in the CLI, the connector, the portal wizard — is improvement feedback for the platform. The team works in the open: every issue it files, every PR it raises, and every review it posts flows through the Spring Voyage GitHub App, making the identity and access model a live part of the feedback loop.

## Where to go next

- `docs/guide/operator/dogfooding-oss-unit.md` — step-by-step bring-up: prerequisites, CLI and wizard paths, post-create verification, and troubleshooting.
- `docs/decisions/0034-oss-dogfooding-unit.md` — design rationale: why these roles, the FROM-agent-base + claude-code image strategy, `hosting: permanent`, and the connector-binding-at-apply-time pattern.
- `packages/spring-voyage-oss/README.md` — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
