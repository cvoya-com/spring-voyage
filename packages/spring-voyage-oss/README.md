# Spring Voyage OSS Dogfooding Package

> **Tier 7 of 7.** The largest catalog package — a multi-unit hierarchy that uses Spring Voyage to develop Spring Voyage itself. Builds on the patterns shown in the simpler packages (`hello-world` → `example-simple` → `example-templated` → `research` → `product-management` → `software-engineering`) and adds nested sub-units, package-level execution inheritance, required connector inputs, and CI-aligned slash-command skills.

The package stands up a working organisation that can triage issues, ship PRs, and keep the program plan honest — all backed by the **Spring Voyage** GitHub App. Install it once and the two role-flavoured sub-units take over from there.

## Catalog visibility

This package ships as part of the platform and is **automatically visible in the tenant catalog** on first boot — no registration or binding step required. The catalog is backed by the on-disk `packages/` tree, so `spring-voyage-oss` appears in `spring package list` and in the portal's **New Unit** wizard under **Catalog** immediately after the platform comes up.

## What this package ships

- **Umbrella unit** (`units/spring-voyage-oss/`, display name *Spring Voyage OSS*) — routes incoming work between the two sub-units.
  - `sv-oss-software-engineering` (*Software Engineering*) — five engineer instances (`ada`, `hopper`, `knuth`, `ritchie`, `turing`) all stamped from the package's `software-engineer` AgentTemplate. Owns implementation, code review, and the build/test/lint loop; dispatches focused work to repository-defined persona subagents under `.claude/agents/`. Five instances let the orchestrator parallelise genuinely independent work without one instance blocking another.
  - `sv-oss-program-management` (*Program Management*) — two PM instances (`drucker`, `deming`) stamped from the package's `program-manager` AgentTemplate. Owns issue triage, milestone hygiene, and native sub-issue / blocked-by relationships against whichever plan version is active under `docs/plan/`. Two instances let triage run concurrently on disjoint issue sets.
- **AgentTemplates** (`templates/`):
  - `software-engineer` — the shared instructions / model / image / capabilities every engineer instance inherits.
  - `program-manager` — the shared instructions / model / image / capabilities every PM instance inherits.

Per ADR-0043 §5g, each engineer / PM is declared inline on the sub-unit's `members:` list as `- agent: { name: <instance>, from: <template>, displayName: "<label>" }`. At install time the inline body is stamped into a fresh concrete agent: the named template is cloned per §5d (scalars on the instance win, the template fills the rest) and the inline `displayName:` flows through to the persisted agent. Each instance gets a fresh Guid identity and runs in its own container, so multiple instances can handle independent tasks concurrently.

Each sub-unit binds the `github` connector. Both rely on the repository's checked-in instructions (`CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, the `docs/architecture/` and `docs/decisions/` indexes, and whichever plan version is active under `docs/plan/`) and the canonical slash-command skills under `.agents/skills/` (`/build`, `/test`, `/lint`, `/triage`, `/areas`, `/adr-new`, `/openapi-diff`, `/web`). The package itself ships no agent prompts that duplicate those documents — when the project's rules or active milestone change, the in-repo source of truth is the only thing to edit.

## Required inputs

| Input | Description |
| --- | --- |
| `github_owner` | GitHub owner (org or user) that owns the repository. |
| `github_repo` | GitHub repository name. |
| `github_installation_id` | Numeric installation ID for the Spring Voyage GitHub App on the target repository. |

To find the installation ID: go to **GitHub → your org → Settings → GitHub Apps → Spring Voyage → Configure**. The ID appears in the URL: `https://github.com/organizations/<org>/settings/installations/<id>`.

## Image references

Each sub-unit pins an OSS-flavoured agent image:

| Sub-unit | Image |
| --- | --- |
| `sv-oss-software-engineering` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest` |
| `sv-oss-program-management` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest` |

The umbrella unit inherits the package-level default image (`ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest`) declared on `package.yaml`. The two role images `FROM` `spring-voyage-agent-base`, install the Claude Code CLI, and add per-role tooling (Software Engineering: .NET 10 SDK, `gh` CLI, Playwright, `ruff`; Program Management: `gh` CLI, `markdownlint-cli2`).

Build them locally with:

```bash
./eng/build/build-agent-images.sh --tag dev
```

The unified release workflow `.github/workflows/release.yml` publishes multi-arch images to GHCR on `spring-voyage-v*` tag pushes.

## Installing the package

### CLI

```bash
spring package install spring-voyage-oss \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

The command installs all three units (umbrella + two sub-units) in a single atomic operation. If any step fails, the whole install rolls back. On success the install report shows each unit reaching `active`:

```
install: <install-id>   status: active
  spring-voyage-oss             active
  sv-oss-software-engineering   active
  sv-oss-program-management     active
```

Inspect an install later with:

```bash
spring package status <install-id>
```

### Wizard

Navigate to `/units/create` in the Spring Voyage portal.

1. Choose **Catalog** as the source.
2. Pick **Spring Voyage OSS** from the catalog list.
3. Fill in the three inputs — GitHub owner, repository, and installation ID — on the inputs form.
4. Click **Install**.

The status view shows each unit moving from staging to active as activation completes. The install is atomic: either all three units reach active, or the whole install rolls back.

## Post-install checks

- Confirm each unit is active: `spring package status <install-id>`.
- Send a triage prompt to `sv-oss-program-management` and confirm it returns a milestone + label + sub-issue/blocked-by recommendation.
- Send a planning prompt to `sv-oss-software-engineering` and confirm it routes against scope discipline and the `area:*` label scheme.

## Identity

All GitHub writes from agents in this organisation go through each sub-unit's binding to the **Spring Voyage** GitHub App. No other GitHub identity is referenced anywhere in this package's YAML, prompts, or instructions — that is a non-negotiable property of the package.

## Further reading

- `docs/concepts/spring-voyage-oss.md` — what the dogfooding org is at conceptual level.
- `docs/guide/operator/dogfooding-oss-unit.md` — operator-facing bring-up guide.
- `docs/plan/` — the active plan-of-record lives under the latest version directory in here.
- `docs/decisions/0043-recursive-package-format.md` — the recursive folder layout this package uses.
