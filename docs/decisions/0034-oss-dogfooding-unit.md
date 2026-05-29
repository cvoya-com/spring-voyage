# 0034 — Spring Voyage OSS dogfooding unit (role decomposition, image strategy, hosting, identity)

- **Status:** Accepted — 2026-05-01 — `packages/spring-voyage-oss/` ships a built-in template package that creates a parent unit with four role sub-units (software-engineering, design, product-management, program-management), each pinning a role-flavored agent image derived from `spring-voyage-agent-base` + Claude Code and running with `execution.hosting: permanent`. The unit's GitHub work flows through a per-unit binding to the **Spring Voyage** GitHub App, collected at template-apply time on both the wizard and the CLI surfaces.
- **Amended:** 2026-05-16 — the four role sub-units were collapsed to two (`sv-oss-software-engineering`, `sv-oss-program-management`), and the per-role agent personas were replaced by repository-defined Claude Code subagents under `.claude/agents/`. The design / product-management sub-units and their dedicated agent images (`spring-voyage-agent-oss-design`, `spring-voyage-agent-oss-product-management`) were removed; their work is now done by the SE sub-unit dispatching to the `design-engineer` / repository conventions and by the program-management sub-unit consulting whichever plan version is active under `docs/plan/`. The package's prompts no longer hardcode a specific plan version so the unit stays useful across the lifecycle. Sections 1 and 2 of this ADR are historical; the current shape is documented in `docs/concepts/spring-voyage-oss.md` and `packages/spring-voyage-oss/README.md`.
- **Amended:** 2026-05-19 (#2525) — the two role sub-units were collapsed into a single flat unit. The five engineer instances (`ada`, `hopper`, `knuth`, `ritchie`, `turing`) stamped from the `software-engineer` AgentTemplate and the two PM instances (`drucker`, `deming`) stamped from `program-manager` now attach directly to `spring-voyage-oss`. The unit's router delegates each inbound GitHub event straight to the agent member whose expertise and capacity fit the work — no intermediate orchestrator turn. The agent images, hosting mode, GitHub binding, and template-apply-time identity collection are unchanged from the 2026-05-16 amendment. Sub-unit boundaries (and the summarisation seam between them) remain a v0.2 concern.
- **Date:** 2026-05-01
- **Closes:** [#1530](https://github.com/cvoya-com/spring-voyage/issues/1530)
- **Related code:** `packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml` (single flat unit, post-#2525), `eng/build/Dockerfile.agent.oss-{software-engineering,program-management}`, `eng/build/build-agent-images.sh`, `.github/workflows/release.yml` (`publish-oss-agent-images` job, unified `spring-voyage-v*` tag), `src/Cvoya.Spring.Cli/ApiClient.cs:356` (`CreateUnitFromTemplateAsync`), `src/Cvoya.Spring.Cli/ApiClient.cs:417` (`BuildGitHubConnectorBinding`), `src/Cvoya.Spring.Host.Api/Models/UnitModels.cs:152` (`CreateUnitFromTemplateRequest`), `src/Cvoya.Spring.Web/src/app/units/create/page.tsx:221` (wizard connector step), `src/Cvoya.Spring.Connector.GitHub/UnitGitHubConfig.cs:30` (`AppInstallationId`).
- **Related docs:** [`docs/concepts/spring-voyage-oss.md`](../concepts/spring-voyage-oss.md), [`docs/guide/operator/dogfooding-oss-unit.md`](../guide/operator/dogfooding-oss-unit.md), [ADR 0025 — Unified agent launch contract](0025-unified-agent-launch-contract.md), [ADR 0026 — Per-agent container scope](0026-per-agent-container-scope.md), [ADR 0027 — Agent-image conformance contract](0027-agent-image-conformance-contract.md).

## Context

The v0.1 plan-of-record lists "SV is usable for further development of SV (dogfooding)" as a stretch criterion (`docs/archive/plan/v0.1/README.md:28`). Realising it means a single, opinionated template that, when applied, gives an operator a working multi-role team that can plan, code, review, and triage against this repository — not a kit of parts that must be assembled. Four design questions had to be locked in before the package, the Dockerfiles, and the operator playbook were written:

1. What role decomposition reflects how the project actually ships?
2. How are role-flavored images built without forking the agent-base?
3. Should the unit run ephemerally per-message or stay warm?
4. How does GitHub identity bind to the unit at apply time?

ADR 0027 already pins the wire contract (A2A 0.3.x on `:8999`); ADR 0026 already pins per-agent container scope; ADR 0025 already pins the unified dispatch path. This ADR is the layer above: the template-package shape and the operator-facing identity / hosting choices that those contracts compose into.

## Decision

### 1. Four role sub-units: software-engineering, design, product-management, program-management

The parent `spring-voyage-oss` unit has exactly four members, one per role:

- **Software engineering (SE)** — code, tests, build, PRs, ADRs. Maps to `dotnet build SpringVoyage.slnx`, `dotnet test --solution SpringVoyage.slnx`, `dotnet format --verify-no-changes`, web lints (ESLint / knip / tsc), Playwright smokes, and PR review.
- **Design** — visual review, mockups, accessibility, screenshot evidence on PRs.
- **Product management (PM)** — issue triage, roadmap shape, milestone scoping, release notes.
- **Program management (PgM)** — milestone hygiene, sub-issue and blocked-by relationships, area labels, cross-cutting umbrella maintenance.

Rejected: a single "team" sub-unit (loses the role separation that lets operators route a turn to the right surface and lets each sub-unit pin its own toolchain); a flat list of agents under the parent (no orchestration seam — the unit-level `ai.prompt` is where each role's working norms live); arbitrary roles disconnected from how the project actually ships (e.g. "QA" as a standalone unit when QA work is already part of SE's review loop). The four chosen roles map 1:1 onto the four discriminable kinds of work that show up on this repo's issue tracker.

### 2. Image strategy: `FROM` agent-base + Claude Code + role tooling

Each role image is a thin layer over `ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>` that installs the Claude Code CLI and adds the toolchain that role actually needs. Conformance is BYOI path 1 (ADR 0027 § "Three conformance paths"): the bundled bridge ENTRYPOINT is inherited unchanged.

Rejected: a single image where role differentiation lives only in the prompt (does not satisfy "tools the role actually needs" — SE specifically needs the .NET 10 SDK and Playwright browsers + system deps that are not in `agent-base`). With ADR-0038 the per-runtime CLI is no longer a four-runtime matrix here — these images target the `claude-code` runtime specifically, so installing only the Claude Code CLI matches actual scope and avoids carrying CLIs the role will never invoke.

Cost: the SE image is ~2GB+ (agent-base + claude-code CLI + .NET 10 SDK + three Playwright browser builds + system libs). Acceptable for the dogfooding case; multi-stage hygiene is filed as a follow-up under [#1540](https://github.com/cvoya-com/spring-voyage/issues/1540) and gated on a real measurement of pull time, not on speculation.

### 3. `execution.hosting: permanent` at the sub-unit level

Each sub-unit's `execution` block sets `hosting: permanent` so member agents inherit it via the agent → unit resolution chain (`docs/architecture/agent-runtime.md` § 1; `eng/deploy/README.md:74-79`). The OSS unit runs continuously rather than ephemerally: it triages issues as they arrive, drives PR review loops, and benefits from a warm container holding workspace state across messages. ADR 0026 § "Decision" already keys `Persistent` per agent, so the per-agent container scope is preserved — `permanent` here means "every member agent of this unit gets a long-lived container", not "one container for the unit".

Rejected: `ephemeral` (the default). Each turn would re-provision a container, re-pull or re-warm the role image, and discard whatever workspace materialisation the previous turn produced. For a unit whose work is repo-aware and conversation-shaped, that latency and state loss is the wrong default.

### 4. Connector binding atomically at template-apply time, on both surfaces

The template's sub-unit YAMLs declare:

```yaml
connectors:
  - type: github
    config:
      events: ["issues", "issue_comment", "pull_request", "pull_request_review"]
```

`owner`, `repo`, and `installation_id` are **omitted** — those require auth and identity that don't belong in a checked-in template. Both apply paths already accept a `UnitConnectorBindingRequest` so the unit and its GitHub binding are created atomically:

- CLI: `CreateUnitFromTemplateAsync(...)` at `src/Cvoya.Spring.Cli/ApiClient.cs:356`, with `BuildGitHubConnectorBinding(owner, repo, installationId, ...)` at `ApiClient.cs:417` as the helper.
- Wizard: `src/Cvoya.Spring.Web/src/app/units/create/page.tsx:221` runs a per-connector step that produces the binding payload before posting `CreateUnitFromTemplateRequest`.

Whether the wizard / CLI auto-prompt for template-declared connectors today (vs requiring a post-creation `spring unit github bind`) is verified during implementation; the gap, if any, is filed as v0.2 polish under [#1543](https://github.com/cvoya-com/spring-voyage/issues/1543) — the OSS template remains usable end-to-end either way.

### 5. Identity boundary

The unit's GitHub work flows exclusively through the per-unit binding to the **Spring Voyage** GitHub App. No other identity is named in any template YAML, agent persona, orchestrator prompt, operator doc, or filed issue under this umbrella. The tenant-default fallback wired through `UnitGitHubConfig.AppInstallationId == null` → `Auth.GitHubConnectorOptions.InstallationId` is intentionally **not** relied on here: the binding is set explicitly at apply time so a missing or rotated tenant default never accidentally re-routes the unit's writes through a different installation.

## Consequences

### Gains

- **Operator picks one template, gets a working team.** No assembly required; the role decomposition, image pins, hosting mode, and connector seam land together.
- **Per-role toolchains are honest.** SE has the .NET SDK and Playwright browsers it needs; Design has Chromium + Mermaid + ImageMagick; PM and PgM have `gh` and markdown lints. The prompt-only-differentiation alternative would lie about what each role can do.
- **Identity is explicit.** The Spring Voyage App is the only identity in any artefact; the binding is wired atomically so no unit ever transiently writes under a different identity between creation and binding.
- **`permanent` hosting matches the workload.** Repo-aware, conversation-shaped work keeps its container warm; cold-start latency stops being a per-turn cost.

### Costs

- **The SE image is large.** ~2GB+ pull on a fresh host. Mitigated by the agent-base pull cache (per-host) and by the deferred multi-stage hygiene work; not eliminated.
- **Four role images instead of one.** Each role image needs its own Dockerfile, version pins, and release-workflow matrix entry. Bounded by `agent-base` carrying the sidecar/Node/Python; only the Claude Code CLI and role tooling are per-image.
- **Connector binding-at-apply-time depends on both surfaces honouring the template's `connectors:` list.** If a future surface (a third apply path) skips the prompt, the OSS unit would land without a binding and silently fall back to no GitHub identity. Mitigated by the explicit atomic-binding contract on both existing surfaces and by the verification step that the binding is present post-apply.

### Known follow-ups

- **[#1540](https://github.com/cvoya-com/spring-voyage/issues/1540)** — multi-stage hygiene for the SE image. Triggered by measured pull-time pain, not speculation.
- **[#1543](https://github.com/cvoya-com/spring-voyage/issues/1543)** — wizard / CLI auto-prompt for template-declared connectors at apply time, if verification finds a gap.

## Revisit criteria

Revisit if any of the below hold:

- The four-role decomposition stops matching how the project ships (e.g. a "release engineering" surface emerges as a discriminable kind of work with its own toolchain). At that point add a fifth sub-unit and amend this ADR; do not stretch an existing role to absorb it.
- `permanent` hosting becomes a footprint problem on operator hosts (mean container count or memory pressure dominates). At that point the conversation is `Pooled` ([#362](https://github.com/cvoya-com/spring-voyage/issues/362)) per the warm-pool seam ADR 0026 already reserved, not back to `ephemeral`.
- A second built-in dogfooding-shaped template ships and the role-flavored image strategy duplicates layers across templates. At that point the right move is probably a shared "role tooling" intermediate base, not flattening back into a single role-agnostic image.
