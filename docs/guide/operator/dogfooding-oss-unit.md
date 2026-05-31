# Install and run the Spring Voyage OSS dogfooding unit

> **Audience.** Operators running Spring Voyage OSS who want to install the built-in dogfooding package on their tenant — a single unit with engineer and PM agents attached directly that uses the platform to develop the platform itself.

> **Scope.** How to install and verify the package. For the conceptual overview and design details, see related documentation links at the end of this guide.

---

## Catalog visibility

The `spring-voyage-oss` package ships as part of the platform and is automatically visible in every tenant's catalog as soon as the platform is up. No registration step is needed — the catalog is global and backed by the on-disk `packages/spring-voyage-oss/` tree. It appears in `spring package list` and in the portal's **New Unit** wizard under **Catalog** immediately after first boot.

---

## Prerequisites

Before installing the package, confirm all of the following:

- [ ] **Platform is up.** `./eng/deploy/deploy.sh up` has completed without errors. `spring system configuration` reports no mandatory-requirement failures.

- [ ] **OSS agent images are available.** Build them locally with:

  ```bash
  ./eng/build/build-agent-images.sh           # builds the two OSS images at :dev
  ```

  Or pull pre-published images from GHCR (after a `spring-voyage-v*` release tag has run):

  ```bash
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest
  ```

  Confirm they are present: `podman images | grep spring-voyage-agent-oss`.

- [ ] **Spring Voyage GitHub App is registered and installed.** The App must be registered with the platform and installed on the target repository (`cvoya-com/spring-voyage` for the canonical dogfooding case).

  Register the App if you haven't already:

  ```bash
  spring github-app register --name "Spring Voyage" --org cvoya-com
  ```

  This opens a browser flow, writes the App credentials to `eng/config/spring.env`, and prints an install URL. Visit the install URL and install the App on `cvoya-com/spring-voyage` (or whatever repository the unit will work on) via the GitHub UI.

  After installation, note the **installation ID** from the GitHub UI — it appears in the URL of the App's installation settings page (`.../installations/<id>`). You will pass it to the package install below.

- [ ] **Tenant-default LLM runtime credential is set.** The agent members need an LLM. Set the tenant default if you haven't already:

  ```bash
  spring secret create --scope tenant anthropic-oauth --value "<token from claude setup-token>"
  # or the API-key secret for the runtime/provider edge you choose
  ```

  Verify: `spring secret list --scope tenant`.

---

## Install via the CLI

```bash
spring package install spring-voyage-oss \
  --input github_repo=<your-org>/<your-repo> \
  --input github_installation_id=<installation-id>
```

Replace `<installation-id>` with the numeric ID from the App's installation settings page on GitHub.

The command installs the unit and its agent members in a single atomic operation. If any step fails, the whole install rolls back. On success you see the install ID and the unit's status:

```
install: <install-id>   status: active
  spring-voyage-oss             active
```

Inspect an install later with:

```bash
spring package status <install-id>
```

---

## Install via the New Unit wizard

1. Open the portal and navigate to `/units/create`.
2. Choose **Catalog** as the source.
3. Pick **Spring Voyage OSS** from the catalog list.
4. Fill in the two inputs — qualified `owner/repo` and installation ID — on the inputs form.
5. Click **Install**.

The status view shows the unit moving from staging to active as activation completes. The install is atomic: either the unit reaches active, or the whole install is rolled back.

---

## What to expect after installation

```bash
spring package status <install-id>
```

Should show a single entry:

| Name | Status |
| ---- | ------ |
| `spring-voyage-oss` | active |

The `spring-voyage-oss` unit holds the single `github` connector binding (matching the qualified `owner/repo` and `installation_id` you supplied). Webhooks the GitHub App delivers for that repository land on the unit's mailbox; the unit's own runtime runs and hands each event to the engineer or PM agent that owns the work.

The unit has:

- `execution.hosting: persistent` on the engineer and PM agent templates so the agent containers stay warm across messages — appropriate for a team that runs continuously rather than per-request.
- A per-agent persistent volume mounted at `$SPRING_WORKSPACE_PATH` (`/spring/workspace`). Each agent clones the bound repository into its own volume on first use and develops subsequent tasks from worktrees alongside the clone. The volume survives container restarts, so a recycled container resumes work without re-cloning. No host-side bind mount or pre-seeded checkout is required from the operator.

Agent members inherit `$GITHUB_TOKEN` and the other GitHub env vars from the unit's binding, so `gh` and `git` work in every container without further configuration.

---

## Smoke verification

### End-to-end (via GitHub)

The truest smoke is an actual webhook delivery. Open an issue on the bound repository with no `area:*` label and watch `spring activity list --source unit:<id>` for a `DecisionMade` event handing the issue to a PM agent (`drucker` or `deming`) — the unit's runtime runs PM triage first, then re-routes once an `area:*` label is on. Add an `area:*` label to a fresh issue and observe the unit hand the resulting `label_change` to an engineer agent.

### Program management (direct prompt)

Send a free-text triage prompt to the unit (look its `unit:<id>` up with `spring unit show spring-voyage-oss`) and let its runtime pick a PM agent:

```bash
spring message send unit:<id> \
  "New issue opened: 'Agent container restarts on every turn even with hosting: persistent set.' Triage this."
```

Expected response: identifies the sub-system (agent runtime / hosting mode), proposes a milestone matching whichever plan version is active under `docs/plan/`, suggests an issue type (`Bug`), proposes one or more `area:*` labels, and — if this looks like a dependency — suggests a sub-issue or `blocked-by` relationship with an existing issue.

### Software engineering (direct prompt)

Send a free-text planning prompt to the unit and let its runtime pick an engineer agent:

```bash
spring message send unit:<id> \
  "The unit execution defaults merge doesn't honour the agent's own model field when the unit also sets one. Propose a fix."
```

Expected response: cites scope discipline, references the active plan-of-record under `docs/plan/` for area placement, proposes an `area:*` label and issue type, and — because this touches the execution-config merge path — dispatches via Claude Code's Task tool to the `dotnet-engineer` or `architect` persona defined under `.claude/agents/` and may suggest an ADR before code.

---

## Troubleshooting

| Symptom | Likely cause | Resolution |
| ------- | ------------ | ---------- |
| `podman images \| grep spring-voyage-agent-oss` shows nothing | OSS images not built or pulled | Run `./eng/build/build-agent-images.sh --tag dev` and check the output of each step. |
| `"GitHub App not configured"` in the portal or CLI | App credentials not in `spring.env`, or the App is not installed on the target repository | Run `spring github-app register` and confirm the App is installed on the target repo via the App's installation settings page on GitHub. The API and Worker hosts validate GitHub credentials at startup; check `spring system configuration "GitHub Connector"` for the reported state. |
| HTTP 502 from an agent turn | Tenant-default LLM runtime credential is missing or invalid | Confirm the credential is set: `spring secret list --scope tenant`. For Claude Code, create it if absent: `spring secret create --scope tenant anthropic-oauth --value "<token from claude setup-token>"`. Restart the worker container after setting a new credential if the host is already running. |
| Agent stays in `Validating` indefinitely | Image pull failed or the OSS image tag is not available locally | Confirm the image is present (`podman images`). If using `:latest` and no release has published it yet, build locally (`./eng/build/build-agent-images.sh`) and update the relevant AgentTemplate's `execution.image` tag to `:dev`. |

---

## Where to go next

- [`packages/spring-voyage-oss/README.md`](../../../packages/spring-voyage-oss/README.md) — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
- [`docs/guide/operator/byoi-agent-images.md`](byoi-agent-images.md) — conformance contract the OSS images satisfy.
