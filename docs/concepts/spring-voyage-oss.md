# Spring Voyage OSS

The **Spring Voyage OSS** unit is a built-in, single-unit dogfooding org that uses Spring Voyage to develop Spring Voyage itself. It ships as a package (`packages/spring-voyage-oss/`) that is automatically visible in the platform catalog. When installed via `spring package install spring-voyage-oss`, it creates one unit with engineer and PM agents attached directly in a single atomic operation.

The unit is the concrete realisation of the dogfooding stretch criterion: "SV is usable for further development of SV". That criterion was first stated as a v0.1 stretch goal in `docs/archive/plan/v0.1/README.md` and carries forward across plan versions. The unit turns it into something observable — a running team that plans, triages, implements, reviews, and ships the platform on itself.

## Why this exists

Confidence in a platform's primitives comes from using them. The Spring Voyage OSS unit is a stress test of the same primitives every operator uses:

- A single unit with mixed-role agent members declared inline from per-role AgentTemplates.
- `execution.hosting: persistent` on the agent templates so containers stay warm across the continuous work of a development team.
- GitHub connector binding at template-apply time, flowing all GitHub App identity through the connector rather than hardcoding it.
- Agent images built on the BYOI path 1 conformance contract: each image extends `spring-voyage-agent-base`, adds the Claude Code CLI, role tooling, and inherits the bridge ENTRYPOINT unchanged.

When an engineer agent hits a friction point, that friction is a bug or improvement opportunity in the platform. When a PM agent needs a feature in `gh issue` integration, that need is a feature request against the platform's GitHub connector. The feedback loop is direct.

## Structure: one unit, two agent roles

The unit `spring-voyage-oss` is a router. It receives GitHub-webhook-derived events on its mailbox and delegates each one directly to the agent member best suited to handle it. There is no intermediate orchestrator layer; the seven agent members and one human are all attached at the same level.

### Engineer agents (5 instances stamped from `software-engineer`)

`ada`, `hopper`, `knuth`, `ritchie`, `turing`. Responsible for implementing features, fixing bugs, code review, and running the build/test/lint loop. Specialised work is dispatched to repository-defined persona subagents. The persona files are read at dispatch time, so the package itself does not ship persona prompts.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering` — extends `spring-voyage-agent-base` with:
- Claude Code CLI
- .NET 10 SDK
- `gh` CLI for GitHub App-mediated issue and PR work
- Node 22 + npm, `ruff`, and full Playwright browser support

**Anchor documents:** The engineer template's `instructions:` field points at the project's checked-in rules and planning documents. The prompt does not duplicate those documents; it tells the agent to read them and defer to them when this prompt and a file disagree, so the package stays useful as the active milestone changes.

**Slash-command skills:** Engineer agents use the canonical slash-command skills for CI-aligned invocations.

### PM agents (2 instances stamped from `program-manager`)

`drucker`, `deming`. Responsible for issue triage, milestone hygiene, sub-issue and blocked-by relationships, and dependency tracking.

**Image:** `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management` — adds `gh` CLI and `markdownlint-cli2`.

**Anchor documents:** The active plan-of-record under the project documentation. The PM template's prompt encodes the issue primitives (milestones, issue types, labels) and the sub-issue/blocked-by relationship model.

**Slash-command skills:** Canonical triage flow and area taxonomy enumeration.

## How the unit runs

Engineer and PM agent templates declare `execution.hosting: persistent`. The agent containers stay warm across messages — the right default for a team that runs continuously rather than responding to isolated, ephemeral requests.

The unit and every member agent runs in its own container with a per-agent persistent volume. Each agent clones the bound repository into that volume on first use, refreshes the main branch at the start of every task, and stores derived data for caching so subsequent turns avoid round-tripping to GitHub. Engineer agents additionally develop each PR in a worktree so the clone itself stays on the main branch. The unit's own container is a router that delegates rather than editing code; it clones the repo only for inspection, never for writes.

The `spring-voyage-oss` unit lists the engineer and PM agents as `members:`. Messages addressed to the unit invoke the unit's router runtime; the runtime uses the directory tools to inspect agents and messaging tools to deliver work directly to whichever one's expertise and capacity fit it.

The unit is the sole holder of the GitHub connector binding. The repository and installation details are intentionally absent from the checked-in package YAML — they require per-deployment identity that does not belong in source. The operator supplies them as inputs when running `spring package install spring-voyage-oss` (or through the **Catalog** path in the New Unit wizard), and the platform creates the unit and the binding atomically as part of the install. The binding accepts exactly one of App-installation or PAT-secret auth; the OSS install path defaults to the App and an operator can override the choice at install time. Agent members do not bind the connector themselves; they inherit the GitHub credentials from the unit's binding. See [Connectors](connectors.md) for details.

## How GitHub events reach the OSS unit

A GitHub webhook arrives at the platform. The GitHub connector validates the HMAC signature, parses the payload, and translates the event into a domain message. The translator matches the webhook's repository against every binding in the receiving tenant and rewrites the message's destination to each matching unit. Within a tenant, many bindings on the same repo are supported — for this package the unit is the sole binding, so the event always lands on the unit's mailbox. Any inbound filters declared on a binding (label, author, path) are evaluated per binding; filtered-out events are dropped.

The unit's router runtime receives the message on its mailbox. The platform attaches the messaging and directory tools at launch time. The unit's prompt embodies direct-dispatch routing: based on the event intent and labels, the unit routes the work to the right agent member. Each delivery records activity; the router optionally calls the decision-reporting tool to record the routing decision itself.

## How the unit dogfoods the platform

The Spring Voyage OSS unit exercises platform features as a working team, not as a test fixture.

**Engineer agents** run the same commands any operator would run against the codebase — build, test, lint, and format checks.

**PM agents** manage issues, milestones, and sub-issue/blocked-by relationships via the GitHub connector tools available to any unit. Sprint planning outputs live in the same planning structure the project uses.

Bugs the team encounters are bugs in Spring Voyage. Friction it hits — in the CLI, the connector, the portal wizard — is improvement feedback for the platform. The team works in the open: every issue it files, every PR it raises, and every review it posts flows through the Spring Voyage GitHub App, making the identity and access model a live part of the feedback loop.

## Where to go next

- `docs/guide/operator/dogfooding-oss-unit.md` — step-by-step bring-up: prerequisites, CLI and wizard paths, post-create verification, and troubleshooting.
- `packages/spring-voyage-oss/README.md` — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
