# Spring Voyage OSS Dogfooding Package

The built-in package for standing up the multi-role unit that develops the Spring Voyage platform on itself. Install it once and you get a working organisation that can triage issues, ship PRs, review designs, and keep the program plan honest — all backed by the **Spring Voyage** GitHub App.

## What this package ships

- **Root unit** (`units/spring-voyage-oss.yaml`) — the org unit. Its four sub-units are:
  - `sv-oss-software-engineering` — 10 personas (architect, dotnet-engineer, web-engineer, cli-engineer, api-designer, connector-engineer, qa-engineer, devops-engineer, security-engineer, docs-writer). Carries the SE-team orchestrator prompt encoding how the project plans, triages, and reviews.
  - `sv-oss-design` — 1 persona (design-engineer). Visual review, accessibility, mockups.
  - `sv-oss-product-management` — 1 persona (pm). Triage, roadmap, sprint planning against the v0.1 plan-of-record.
  - `sv-oss-program-management` — 1 persona (program-manager). Milestone hygiene, sub-issue / blocked-by wiring, dependency tracking.
- **Agents** (`agents/`) — 13 persona YAMLs.

Each sub-unit binds a `github` connector using the `github_owner`, `github_repo`, and `github_installation_id` inputs supplied at install time.

## Required inputs

| Input | Description |
| --- | --- |
| `github_owner` | GitHub owner (org or user) that owns the repository. |
| `github_repo` | GitHub repository name. |
| `github_installation_id` | GitHub App installation ID for the Spring Voyage App on the target repository. |

To find the installation ID: go to **GitHub → your org → Settings → GitHub Apps → Spring Voyage → Configure**. The installation ID appears in the URL: `https://github.com/organizations/<org>/settings/installations/<id>`.

## Image references

Each sub-unit pins an OSS-flavored agent image:

| Sub-unit | Image |
| --- | --- |
| `sv-oss-software-engineering` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest` |
| `sv-oss-design` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-design:latest` |
| `sv-oss-product-management` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:latest` |
| `sv-oss-program-management` | `ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest` |

The four images `FROM` the omnibus agent base and add per-role tooling. Build them locally with:

```bash
./deployment/build-agent-images.sh --tag dev
```

The release workflow `.github/workflows/release-oss-agent-images.yml` publishes multi-arch images to GHCR on `oss-agents-v*` tag pushes.

## Installing the package

### CLI

```bash
spring package install spring-voyage-oss \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

The command installs all 5 units (root + 4 sub-units) in a single atomic operation. If any step fails, the whole install rolls back. On success you see the install ID and the status of each unit:

```
install: <install-id>   status: active
  spring-voyage-oss             active
  sv-oss-software-engineering   active
  sv-oss-design                 active
  sv-oss-product-management     active
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

The status view shows each unit moving from staging to active as activation completes. The install is atomic: either all 5 units reach active, or the whole install is rolled back.

## Post-install checks

- Confirm each sub-unit is active: `spring package status <install-id>`.
- Send a triage prompt to `sv-oss-program-management` and confirm it returns a milestone + label + sub-issue/blocked-by recommendation.
- Send a triage prompt to `sv-oss-software-engineering` and confirm it routes against scope discipline + the `area:*` label scheme.

## Identity

All GitHub writes from agents in this organisation go through each sub-unit's binding to the **Spring Voyage** GitHub App. No other GitHub identity is referenced anywhere in this package's YAML, prompts, or instructions — that is a non-negotiable property of the package.

## Further reading

- `docs/concepts/spring-voyage-oss.md` — the multi-role unit at conceptual level.
- `docs/guide/operator/dogfooding-oss-unit.md` — operator-facing bring-up guide.
- `docs/plan/v0.1/README.md` — the active plan-of-record.
