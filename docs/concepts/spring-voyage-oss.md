# Spring Voyage OSS

The **Spring Voyage OSS** unit is a built-in, single-unit dogfooding org that uses Spring Voyage to develop Spring Voyage itself. It ships as a package (`packages/spring-voyage-oss/`) that is automatically visible in the platform catalog. When installed via `spring package install spring-voyage-oss`, it creates one unit with engineer and PM agents attached directly in a single atomic operation.

The unit is the concrete realisation of the dogfooding stretch criterion: "SV is usable for further development of SV". That criterion was first stated as a v0.1 stretch goal in `docs/plan/v0.1/README.md` and carries forward across plan versions. The unit turns it into something observable — a running team that plans, triages, implements, reviews, and ships the platform on itself.

## Why this exists

Confidence in a platform's primitives comes from using them. The Spring Voyage OSS unit is a stress test of the same primitives every operator uses:

- A single unit with mixed-role agent members declared inline from per-role AgentTemplates.
- `execution.hosting: persistent` on the agent templates so containers stay warm across the continuous work of a development team.
- GitHub connector binding at template-apply time, flowing all GitHub App identity through the connector rather than hardcoding it.
- Agent images built on the BYOI path 1 conformance contract (ADR-0027): each image extends `spring-voyage-agent-base`, adds the Claude Code CLI, role tooling, and inherits the bridge ENTRYPOINT unchanged.

When an engineer agent hits a friction point, that friction is a bug or improvement opportunity in the platform. When a PM agent needs a feature in `gh issue` integration, that need is a feature request against the platform's GitHub connector. The feedback loop is direct.

## Structure: one unit, two agent roles

The unit `spring-voyage-oss` is a router. It receives GitHub-webhook-derived events on its mailbox and delegates each one directly to the agent member best suited to handle it. There is no intermediate orchestrator layer; the seven agent members and one human are all attached at the same level.

### Engineer agents (5 instances stamped from `software-engineer`)

`ada`, `hopper`, `knuth`, `ritchie`, `turing`. Responsible for implementing features, fixing bugs, code review, and running the build/test/lint loop. Specialised work is dispatched to repository-defined persona subagents under `.claude/agents/` (architect, dotnet-engineer, web-engineer, cli-engineer, api-designer, connector-engineer, qa-engineer, devops-engineer, security-engineer, design-engineer, docs-writer). The persona files are read by Claude Code's Task tool at dispatch time, so the package itself does not ship persona prompts.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` — extends `spring-voyage-agent-base` with:
- Claude Code CLI (Anthropic's claude-code launcher)
- .NET 10 SDK (for `dotnet build SpringVoyage.slnx -c Release`, `dotnet test --solution SpringVoyage.slnx`, `dotnet format SpringVoyage.slnx --verify-no-changes`)
- `gh` CLI for GitHub App-mediated issue and PR work
- Node 22 + npm (inherited from spring-voyage-agent-base), `ruff`, and full Playwright browser support (Chromium, Firefox, WebKit) including all required system dependencies

**Anchor documents:** The engineer template's `instructions:` field points at the project's checked-in rules — `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, the `docs/architecture/` and `docs/decisions/` indexes, and whichever plan version is active under `docs/plan/`. The prompt does not duplicate those documents; it tells the agent to read them and defer to them when this prompt and a file disagree, so the package stays useful as the active milestone changes.

**Slash-command skills:** Engineer agents use the canonical slash-command skills under `.agents/skills/` — `/build`, `/test`, `/lint`, `/openapi-diff`, `/triage`, `/adr-new`, `/web` — for CI-aligned invocations.

### PM agents (2 instances stamped from `program-manager`)

`drucker`, `deming`. Responsible for issue triage, milestone hygiene, sub-issue and blocked-by relationships, and dependency tracking.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` — adds `gh` CLI and `markdownlint-cli2`.

**Anchor documents:** `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, and the active plan-of-record under `docs/plan/` (along with its `areas/` subdirectory). The PM template's prompt encodes the three issue primitives (milestones, issue types, labels), the sub-issue/blocked-by relationship model, and the rule that prose "blocked by #N" in a body is not enough — the relationship must be registered natively so it surfaces in GitHub's dependency view.

**Slash-command skills:** `/triage` — the canonical triage flow against the active area taxonomy; `/areas` — enumerate the current `area:*` taxonomy.

## How the unit runs

Engineer and PM agent templates declare `execution.hosting: persistent`. The agent containers stay warm across messages — the right default for a team that runs continuously rather than responding to isolated, ephemeral requests.

The unit and every member agent runs in its own container with a per-agent persistent volume mounted at `$SPRING_WORKSPACE_PATH` (`/spring/workspace`). Each agent clones the bound repository (`$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO`) into that volume on first use, refreshes `main` at the start of every task, and stores derived data under `$SPRING_WORKSPACE_PATH/cache/` so subsequent turns avoid round-tripping to GitHub. Engineer agents additionally develop each PR in a worktree under `$SPRING_WORKSPACE_PATH/worktrees/<task>` so the clone itself stays on `main`. The unit's own container is a router that delegates rather than editing code; it clones the repo only for inspection, never for writes. The canonical definition of the workspace layout, the per-message `/workspace` mount, and the recommended clone bootstrap lives in [`docs/architecture/agent-runtime.md` § 4i — Per-agent workspace volume](../architecture/agent-runtime.md#4i-per-agent-workspace-volume); the OSS package prompts reference that section rather than redefining it.

The `spring-voyage-oss` unit lists the engineer and PM agents as `members:`. Messages addressed to the unit invoke the unit's router runtime; the runtime uses the orchestration tools to inspect agents and delegate directly to whichever one's expertise and capacity fit the work.

The unit is the sole holder of the GitHub connector binding. The qualified `repo` (`owner/repo`) and `installation_id` are intentionally absent from the checked-in package YAML — they require per-deployment identity that does not belong in source. The operator supplies them as inputs when running `spring package install spring-voyage-oss` (or through the **Catalog** path in the New Unit wizard), and the platform creates the unit and the binding atomically as part of the install. Per [ADR-0047](../decisions/0047-platform-user-human-split.md) §11 the binding accepts **exactly one** of `app_installation_id` or `pat_secret_name`; the OSS install path defaults to the App and an operator can override the choice at install time (the New Unit wizard's auth-choice sub-step + the CLI's `--pat-secret-name` flag are the surfaces). Agent members do not bind the connector themselves; they inherit `$GITHUB_TOKEN` and the other GitHub env vars from the unit's binding via the platform's connector binding-walk (closest binding in the parent chain wins). See [Connectors](connectors.md) for the binding model and the GitHub connector's repository and reviewer discovery behaviour.

## How GitHub events reach the OSS unit

A GitHub webhook arrives at the platform's `/api/v1/webhooks/github` endpoint. The GitHub connector validates the HMAC signature against the App's webhook secret, parses the payload, and translates the event into a domain message with a structured `intent` field plus the typed event-specific payload. The translator then resolves the destination per [ADR-0047](../decisions/0047-platform-user-human-split.md) §10: it matches the webhook's `(tenant, owner, repo)` against every binding in the receiving tenant whose `(owner, repo)` matches and rewrites the message's `To` address to each matching unit. Within a tenant, many bindings on the same repo are supported — for this package the unit is the sole binding, so the event always lands on `spring-voyage-oss`'s mailbox. Any inbound filters declared on a binding (label, author, path) are evaluated per binding; filtered-out events are dropped with an audit entry.

The unit's router runtime receives the resulting A2A message on its mailbox. Because the unit has members, the platform attaches a closed five-tool orchestration MCP surface (`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`, `query_child_status`) at launch time. The unit's prompt embodies direct-dispatch routing: `(intent, payload)` → the right agent member. The full routing table — including which `area:*` label signals route a `work_assignment` straight to an engineer versus which path runs a PM triage first — lives in the unit's `instructions:` block in [`packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml`](../../packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml), and the canonical `intent` vocabulary the connector emits (`work_assignment`, `assignment`, `label_change`, `feedback`, `review_result`, `review_request`, `code_change`, `review_thread`, `lifecycle`, `edit`, …) is generated by `GitHubWebhookHandler.TranslatePayload` in the connector. Each delegation emits an `OrchestrationDecision` activity event for audit.

## How the unit dogfoods the platform

The Spring Voyage OSS unit exercises platform features as a working team, not as a test fixture.

**Engineer agents** run the same commands any operator would run against the codebase:
- `dotnet build SpringVoyage.slnx -c Release` to verify the build
- `dotnet test --solution SpringVoyage.slnx` to run the full test suite
- `dotnet format SpringVoyage.slnx --verify-no-changes` for format enforcement
- `npm run lint`, `npm --workspace=spring-voyage-dashboard run knip`, `npm --workspace=spring-voyage-dashboard run typecheck` for the web layer

**PM agents** manage issues, milestones, and sub-issue/blocked-by relationships in `cvoya-com/spring-voyage` via `gh issue` commands, exercising the same GitHub connector skills available to any unit. Sprint planning outputs live in the same `docs/plan/` structure the project already uses; the plan-of-record is whichever version directory under `docs/plan/` is currently active.

Bugs the team encounters are bugs in Spring Voyage. Friction it hits — in the CLI, the connector, the portal wizard — is improvement feedback for the platform. The team works in the open: every issue it files, every PR it raises, and every review it posts flows through the Spring Voyage GitHub App, making the identity and access model a live part of the feedback loop.

## Where to go next

- `docs/guide/operator/dogfooding-oss-unit.md` — step-by-step bring-up: prerequisites, CLI and wizard paths, post-create verification, and troubleshooting.
- `docs/decisions/0034-oss-dogfooding-unit.md` — design rationale: why these roles, the FROM-agent-base + claude-code image strategy, `hosting: persistent`, and the connector-binding-at-apply-time pattern.
- `packages/spring-voyage-oss/README.md` — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
