# Install and run the Spring Voyage OSS dogfooding unit

> **Audience.** Operators running Spring Voyage OSS who want to install the built-in dogfooding package on their tenant — a five-unit hierarchy (one parent, four role sub-units) that uses the platform to develop the platform itself.

> **Scope.** How to install and verify the package. For the conceptual overview — what each sub-unit is responsible for and how they coordinate — see [`docs/concepts/spring-voyage-oss.md`](../../concepts/spring-voyage-oss.md). For the design rationale, see [`docs/decisions/0034-oss-dogfooding-unit.md`](../../decisions/0034-oss-dogfooding-unit.md).

---

## Catalog visibility

The `spring-voyage-oss` package ships as part of the platform and is automatically visible in every tenant's catalog as soon as the platform is up. No registration step is needed — the catalog is global and backed by the on-disk `packages/spring-voyage-oss/` tree. It appears in `spring package list` and in the portal's **New Unit** wizard under **Catalog** immediately after first boot.

---

## Prerequisites

Before installing the package, confirm all of the following:

- [ ] **Platform is up.** `./deployment/deploy.sh up` has completed without errors. `spring system configuration` reports no mandatory-requirement failures.

- [ ] **OSS agent images are available.** Build them locally with:

  ```bash
  ./deployment/build-agent-images.sh           # builds all four OSS images at :dev
  ```

  Or pull pre-published images from GHCR (after an `oss-agents-v*` release tag has run):

  ```bash
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-design:latest
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:latest
  podman pull ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest
  ```

  Confirm they are present: `podman images | grep spring-voyage-agent-oss`.

- [ ] **Spring Voyage GitHub App is registered and installed.** The App must be registered with the platform and installed on the target repository (`cvoya-com/spring-voyage` for the canonical dogfooding case).

  Register the App if you haven't already:

  ```bash
  spring github-app register --name "Spring Voyage" --org cvoya-com
  ```

  This opens a browser flow, writes the App credentials to `deployment/spring.env`, and prints an install URL. Visit the install URL and install the App on `cvoya-com/spring-voyage` (or whatever repository the unit will work on) via the GitHub UI.

  After installation, capture the installation ID:

  ```bash
  spring github-app list-installations
  ```

  Note the numeric ID for the repository you will bind the unit to.

- [ ] **Tenant-default LLM provider key is set.** The sub-unit agents need an LLM. Set the tenant default if you haven't already:

  ```bash
  spring secret create --scope tenant anthropic-api-key --value "<sk-ant-...>"
  # or the OpenAI / Google / Ollama equivalent
  ```

  Verify: `spring secret list --scope tenant`.

---

## Install via the CLI

```bash
spring package install spring-voyage-oss \
  --input github_owner=<your-org> \
  --input github_repo=<your-repo> \
  --input github_installation_id=<installation-id>
```

Replace `<installation-id>` with the numeric ID from `spring github-app list-installations`.

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

---

## Install via the New Unit wizard

1. Open the portal and navigate to `/units/create`.
2. Choose **Catalog** as the source.
3. Pick **Spring Voyage OSS** from the catalog list.
4. Fill in the three inputs — GitHub owner, repository, and installation ID — on the inputs form.
5. Click **Install**.

The status view shows each unit moving from staging to active as activation completes. The install is atomic: either all 5 units reach active, or the whole install is rolled back.

---

## What to expect after installation

```bash
spring package status <install-id>
```

Should show five entries:

| Name | Status |
| ---- | ------ |
| `spring-voyage-oss` | active |
| `sv-oss-software-engineering` | active |
| `sv-oss-design` | active |
| `sv-oss-product-management` | active |
| `sv-oss-program-management` | active |

Each sub-unit has:

- A `github` connector binding pointing at the Spring Voyage App's installation on the target repository.
- `execution.hosting: permanent` so the agent containers stay warm across messages — appropriate for a team that runs continuously rather than per-request.

---

## Smoke verification

### Program management

Send a triage prompt to the program-management sub-unit:

```bash
spring message send sv-oss-program-management \
  "New issue opened: 'Agent container restarts on every turn even with hosting: permanent set.' Triage this."
```

Expected response: identifies the sub-system (agent runtime / hosting mode), proposes a milestone (`v0.1` or `v0.2`), suggests an issue type (`Bug`), proposes one or more `area:*` labels, and — if this looks like a dependency — suggests a sub-issue or `blocked-by` relationship with an existing issue.

### Software engineering

Send a planning prompt to the software-engineering sub-unit:

```bash
spring message send sv-oss-software-engineering \
  "The unit execution defaults merge doesn't honour the agent's own model field when the unit also sets one. Propose a fix."
```

Expected response: cites scope discipline, references `docs/plan/v0.1/README.md` for area placement, proposes an `area:*` label and issue type, and — because this touches the execution-config merge path — routes to the `dotnet-engineer` or `architect` persona and may suggest an ADR before code.

---

## Troubleshooting

| Symptom | Likely cause | Resolution |
| ------- | ------------ | ---------- |
| `podman images \| grep spring-voyage-agent-oss` shows nothing | OSS images not built or pulled | Run `./deployment/build-agent-images.sh --tag dev` and check the output of each step. |
| `"GitHub App not configured"` in the portal or CLI | App credentials not in `spring.env`, or the App is not installed on the target repository | Run `spring github-app register` and confirm the App is installed on the target repo (`spring github-app list-installations` should show a row for that repo). The API and Worker hosts validate GitHub credentials at startup; check `spring system configuration "GitHub Connector"` for the reported state. |
| HTTP 502 from an agent turn | Tenant-default LLM key is missing or invalid | Confirm the key is set: `spring secret list --scope tenant`. Create it if absent: `spring secret create --scope tenant anthropic-api-key --value "<sk-ant-...>"`. Restart the worker container after setting a new key if the host is already running. |
| Sub-unit stays in `Validating` indefinitely | Image pull failed or the OSS image tag is not available locally | Confirm the image is present (`podman images`). If using `:latest` and no release has published it yet, build locally (`./deployment/build-agent-images.sh`) and update the sub-unit's `execution.image` tag to `:dev`. |

---

## Where to go next

- [`docs/concepts/spring-voyage-oss.md`](../../concepts/spring-voyage-oss.md) — what the unit is: sub-unit responsibilities, orchestrator prompts, how it dogfoods the platform.
- [`docs/decisions/0034-oss-dogfooding-unit.md`](../../decisions/0034-oss-dogfooding-unit.md) — why this design: role decomposition, FROM-omnibus image strategy, `hosting: permanent`, connector binding at apply time.
- [`packages/spring-voyage-oss/README.md`](../../../packages/spring-voyage-oss/README.md) — package internals: unit and agent YAML layout, connector declaration, and post-install steps.
- [`docs/guide/operator/byoi-agent-images.md`](byoi-agent-images.md) — conformance contract the four OSS images satisfy (BYOI path 1).
