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

Each unit and member agent runs in its own container with a per-agent persistent volume mounted at `$SPRING_WORKSPACE_PATH` (`/spring/workspace`). The sub-unit orchestrators and their engineer / PM members clone the bound repository (`$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO`) into that volume on first use, refresh `main` at the start of every task, and store derived data under `$SPRING_WORKSPACE_PATH/cache/` so subsequent turns avoid round-tripping to GitHub. Software-engineering instances additionally develop each PR in a worktree under `$SPRING_WORKSPACE_PATH/worktrees/<task>` so the clone itself stays on `main`. The umbrella `spring-voyage-oss` unit is a router and does not clone the repo — its job is to delegate to whichever sub-unit owns the work. The canonical definition of the workspace layout, the per-message `/workspace` mount, and the recommended clone bootstrap lives in [`docs/architecture/agent-runtime.md` § 4i — Per-agent workspace volume](../architecture/agent-runtime.md#4i-per-agent-workspace-volume); the OSS package prompts reference that section rather than redefining it.

The top-level `spring-voyage-oss` unit lists the two sub-units as `members`. Messages routed to the top-level unit invoke the top-level unit's runtime; the runtime uses the orchestration tools to inspect and delegate to the appropriate sub-unit, which then runs its own runtime to delegate to its single member agent.

The umbrella `spring-voyage-oss` unit is the sole holder of the GitHub connector binding. The qualified `repo` (`owner/repo`) and `installation_id` are intentionally absent from the checked-in package YAML — they require per-deployment identity that does not belong in source. The operator supplies them as inputs when running `spring package install spring-voyage-oss` (or through the **Catalog** path in the New Unit wizard), and the platform creates the unit hierarchy and the umbrella's binding atomically as part of the install. Per [ADR-0047](../decisions/0047-platform-user-human-split.md) §11 the binding accepts **exactly one** of `app_installation_id` or `pat_secret_name`; the OSS install path defaults to the App and an operator can override the choice at install time (the New Unit wizard's auth-choice sub-step + the CLI's `--pat-secret-name` flag are the surfaces). Sub-units do not bind the connector themselves; they inherit `$GITHUB_TOKEN` and the other GitHub env vars from the umbrella's binding via the platform's connector binding-walk (closest binding in the parent chain wins). See [Connectors](connectors.md) for the binding model and the GitHub connector's repository and reviewer discovery behaviour.

## How GitHub events reach the OSS unit

A GitHub webhook arrives at the platform's `/api/v1/webhooks/github` endpoint. The GitHub connector validates the HMAC signature against the App's webhook secret, parses the payload, and translates the event into a domain message with a structured `intent` field plus the typed event-specific payload. The translator then resolves the destination per [ADR-0047](../decisions/0047-platform-user-human-split.md) §10: it matches the webhook's `(tenant, owner, repo)` against every binding in the receiving tenant whose `(owner, repo)` matches and rewrites the message's `To` address to each matching unit. Within a tenant, many bindings on the same repo are supported — for this package the umbrella is the sole binding, so the event always lands on `spring-voyage-oss`'s mailbox. Any inbound filters declared on a binding (label, author, path) are evaluated per binding; filtered-out events are dropped with an audit entry.

The umbrella unit's runtime receives the resulting A2A message on its mailbox. Because the umbrella has members, the platform attaches a closed five-tool orchestration MCP surface (`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`, `query_child_status`) at launch time. The umbrella's prompt embodies the v0.1 triage routing: `(intent, payload)` → child sub-unit. The full routing table — including which `area:*` label signals route a `work_assignment` straight to `sv-oss-software-engineering` versus which path through `sv-oss-program-management` triage — lives in the umbrella's `instructions:` block in [`packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml`](../../packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml), and the canonical `intent` vocabulary the connector emits (`work_assignment`, `assignment`, `label_change`, `feedback`, `review_result`, `review_request`, `code_change`, `review_thread`, `lifecycle`, `edit`, …) is generated by `GitHubWebhookHandler.TranslatePayload` in the connector. Each delegation hop emits an `OrchestrationDecision` activity event for audit.

The sub-unit orchestrators (`sv-oss-software-engineering`, `sv-oss-program-management`) carry their own `intent`-aware routing — they decide which engineer / PM instance handles each delegated message and, in the SE case, which persona subagent under `.claude/agents/` to dispatch to for specialist work.

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
