# Packages

> **[Architecture Index](README.md)** | Related: [Connectors](connectors.md), [Units](units.md), [Agents](agents.md), [Roadmap](../roadmap/README.md)

---

## Domain Packages

A **domain package** is an installable bundle of domain-specific content — agent definitions, unit definitions, skills, workflows, and connector implementations — described by a `package.yaml` manifest at the package root and organized by directory convention.

Each domain package follows a standard directory convention:

- **`agents/`** — Agent definition YAML files
- **`units/`** — Unit definition YAML files
- **`skills/`** — Prompt fragments (`.md`) + optional tool definitions (`.tools.json`)
- **`workflows/`** — Workflow container sources (Dockerfile + project code)
- **`execution/`** — Agent execution environment sources (Dockerfile)
- **`connectors/`** — Connector implementations (compiled into host)

Three packages ship in-tree: `software-engineering`, `product-management`, and `research`. The file-system catalog (`GET /api/v1/packages`) picks them up automatically because `FileSystemPackageCatalogService` walks every directory under `packages/`.

**Dockerfiles are source; images are runtime.** Packages include Dockerfiles for workflows and agent execution environments — they are the source of truth for how images are built. Agent and unit definitions reference pre-built images at runtime. The `spring build` command bridges the gap:

```bash
# Build all images from a package's Dockerfiles
spring build packages/software-engineering

# Build a specific workflow or execution environment
spring build packages/software-engineering/workflows/software-dev-cycle

# List built images
spring images list
```

Production deployments always use pre-built images from a registry.

Connectors within a domain package are compiled into the host. Workflows and execution environments are deployed as containers (see [Workflows](workflows.md)).

## Browsing Packages (CLI + Portal)

Discovery runs through a shared endpoint family so the CLI and portal stay in parity per [`CONVENTIONS.md`](../../CONVENTIONS.md) § `ui-cli-parity`:

| Surface | CLI | Portal | Endpoint |
|---------|-----|--------|----------|
| List packages with content counts | `spring package list` | `/packages` | `GET /api/v1/packages` |
| Show contents of a single package | `spring package show <name>` | `/packages/<name>` | `GET /api/v1/packages/{name}` |

The resolver is the `IPackageCatalogService`, with `FileSystemPackageCatalogService` as the OSS implementation. The packages root is configured via `Packages:Root` (falling back to the `SPRING_PACKAGES_ROOT` environment variable). The hosted cloud repo swaps in a tenant-scoped implementation via DI — consumers never reference the file-system layout directly.

Summary payloads carry only stable fields (name, description, per-content counts). Detail payloads carry the full content lists (agent definitions, unit definitions, skills, connectors, workflows).

## Skill Format & Composition

A **skill** is a bundle of a prompt fragment and optional tool definitions. Skills are how domain knowledge and domain-specific actions are packaged for reuse.

**Skill files:**

- `{skill-name}.md` — A markdown prompt fragment. Contains domain knowledge, decision criteria, procedures, and behavioral guidance. Injected into Layer 2 (unit context) of the prompt.
- `{skill-name}.tools.json` (optional) — Tool definitions in JSON schema format. Each tool specifies a name, description, and parameter schema. The platform translates tool calls into the appropriate messages and service calls.

**Composition:** When a unit or agent references skills, the skill prompt fragments are concatenated into the prompt in declaration order. The unit's `ai.prompt` is the base; skills append to it. Tool definitions from all referenced skills are merged into the agent's tool manifest.

```yaml
# Unit AI references skills — prompts and tools compose automatically
ai:
  prompt: |                          # base prompt (always included)
    You coordinate a software engineering team.
  skills:                            # appended in declaration order
    - package: spring-voyage/software-engineering
      skill: triage-and-assign       # adds triage prompt + assignToAgent tool
    - package: spring-voyage/software-engineering
      skill: pr-review-cycle         # adds review prompt + requestReview tool
```

**Example skill prompt fragment** (`triage-and-assign.md`):

```markdown
## Triage & Assignment

When you receive a new work item:
1. Classify by type: feature, bug, refactor, documentation
2. Estimate complexity: small (< 1 hour), medium (1-4 hours), large (> 4 hours)
3. Match to the best-fit agent by expertise using `discoverPeers`
4. Assign using `assignToAgent` with a clear description of the work
5. For large items, consider breaking into sub-tasks first
```

**Example tool definition** (`triage-and-assign.tools.json`):

```json
[
  {
    "name": "assignToAgent",
    "description": "Assign a work item to a specific agent in the unit",
    "parameters": {
      "type": "object",
      "required": ["agentId", "description"],
      "properties": {
        "agentId": { "type": "string", "description": "Agent ID or role to assign to" },
        "description": { "type": "string", "description": "Clear description of the work" },
        "conversationId": { "type": "string", "description": "Optional — attach to existing conversation" }
      }
    }
  }
]
```

## Authoring a Skill Bundle

Each bundle lives in `packages/{package}/skills/` as a pair of sibling files:

1. `{skill-name}.md` — markdown prompt fragment. Required. Keep the guidance focused; the bundle will be concatenated into the unit's Layer 2 context in declaration order.
2. `{skill-name}.tools.json` — optional JSON array of tool requirements. Each entry must have `name` and `description` and should include a JSON-schema `parameters` object. Add `"optional": true` for tools that may be absent without failing validation.

At unit creation (via the package-install activator or `POST /api/v1/units`), the platform:

1. Resolves each `{package, skill}` pair via `ISkillBundleResolver`. Missing packages or missing `.md` files produce a `400 Bad Request` with a diagnostic search path.
2. Validates the bundle's declared tools against two checks:
   - **Tool availability** (advisory): if a required (non-optional) tool is not surfaced by any registered `ISkillRegistry`, the platform returns the unit creation result with an advisory warning in `UnitCreationResponse.warnings` rather than failing the call. Shipped bundles often reference aspirational unit-orchestration primitives (`assignToAgent`, `requestReview`, `submitReview`, `setPriority`, ...) that no connector provides yet; blocking creation would make those templates unusable. The agent will receive a runtime "tool not found" error from the LLM tooling layer if it actually attempts to invoke a missing tool. Tracked for a platform-level resolution in [#306](https://github.com/cvoya-com/spring-voyage/issues/306).
   - **Unit policy** (blocking): tools registered but blocked by the unit's `SkillPolicy` are rejected with a `400 Bad Request`. This is the C3 security invariant and is never softened to a warning.
3. Persists resolved bundles via `IUnitSkillBundleStore` so prompt assembly can rehydrate them on every message turn without reparsing the manifest.

At prompt-assembly time, bundle prompts render as a sub-section of Layer 2 (unit context), after the connector-skills listing and before Layer 3 (conversation context). Declaration order in the manifest determines the bundle order in the prompt.

A bundle's tools still pass through the unit `SkillPolicy` enforcement at invocation time — the validation step only protects against misconfiguration at create-time. Blocking a tool on an existing unit will not retroactively delete its bundle; it will only refuse the tool at invocation.

## Package Install & Recovery

Packages are installed through a two-phase atomic flow:

- **`POST /api/v1/packages/install`** — starts an install from the catalog.
- **`POST /api/v1/packages/install/file`** — starts an install from an uploaded `package.yaml`.
- **`GET /api/v1/installs/{id}`** — poll install status; returns per-artefact staging/active counts.
- **`POST /api/v1/installs/{id}/retry`** — retry a failed install.
- **`POST /api/v1/installs/{id}/abort`** — abort and roll back a failed install.

Export is via **`POST /api/v1/tenant/packages/export`**.

**CLI surface:**

```bash
spring package install <name> [--input key=value ...]
spring package status  <install-id>
spring package retry   <install-id>
spring package abort   <install-id>
spring package export  <name>
spring package list
spring package show    <name>
```

**Portal:** the portal's `/units/create` wizard exposes a **From catalog** source that routes through the same install endpoint. `/installs/{id}` shows install status. The unit detail page shows an **Install** button and an inputs form for packages with declared inputs.

### Inputs

Packages declare typed scalar inputs (`string`, `int`, `bool`, `secret`) in `package.yaml`. The install endpoint validates supplied values against declared inputs. Cross-package references must be self-contained — no `${{ inputs.* }}` substitution across package boundaries (see [ADR-0035](../decisions/0035-cross-package-self-contained.md)).
