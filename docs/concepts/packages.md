# Packages and Skills

Spring Voyage is domain-agnostic by design. The platform provides primitives (agents, units, messaging, orchestration); **packages** provide domain expertise.

## What is a Package?

A **package** is an installable bundle of domain-specific content, described by a `package.yaml` manifest at the package root:

- **Agents** -- pre-configured agent definitions (e.g., backend-engineer, qa-engineer)
- **Units** -- pre-configured unit definitions (e.g., engineering-team)
- **Skills** -- prompt fragments and tool definitions (see below)
- **Workflows** -- container images for structured orchestration
- **Connectors** -- bridges to domain-specific external systems
- **Execution environments** -- container images for agent work

Three packages ship in-tree: `software-engineering`, `product-management`, and `research`. All three are discoverable via `spring package list` and `/packages`.

### Example: The Software Engineering Package

The `software-engineering` package includes:

- Agents: backend engineer, QA engineer, tech lead
- Unit: engineering team
- Skills: triage-and-assign, PR review cycle
- Workflow: software development cycle (triage, assign, implement, review, merge)
- Connector: GitHub
- Execution environment: pre-configured container with Claude Code

## What is a Skill?

A **skill** is the smallest unit of reusable domain knowledge. Each skill is a bundle of:

1. **A prompt fragment** (`.md` file) -- domain knowledge, decision criteria, procedures, behavioral guidance
2. **Tool definitions** (optional `.tools.json` file) -- actions the agent can take in this domain

### How Skills Compose

When a unit or agent references skills, the prompt fragments are concatenated into the prompt in declaration order. Tool definitions from all referenced skills are merged into the agent's tool manifest.

For example, an engineering team unit might reference two skills:

- `triage-and-assign` -- adds knowledge about classifying issues and matching agents by expertise, plus an `assignToAgent` tool
- `pr-review-cycle` -- adds knowledge about code review standards and process, plus a `requestReview` tool

The unit's AI receives the combined prompt and has access to both tools.

### Skill Prompt Fragment Example

A skill prompt might contain:

- Procedures: "When you receive a new work item, classify by type, estimate complexity, match to the best-fit agent by expertise"
- Decision criteria: "For large items, consider breaking into sub-tasks first"
- Quality standards: "All code changes must have tests; all PRs must have descriptions"
- Domain conventions: "Use conventional commits; prefix feature branches with `feat/`"

### Skill Tool Definition Example

A skill tool definition specifies:

- Tool name and description
- Parameter schema (what the tool accepts)
- The platform translates tool calls into the appropriate messages and service calls

## Package Lifecycle

Packages are installed via `spring package install <name>` (CLI) or the portal wizard's **From catalog** source at `/units/create`. Both paths route through `POST /api/v1/packages/install` and activate all artefacts in the package atomically. If any step fails, the whole install rolls back. Recovery uses `GET /api/v1/installs/{id}` to inspect status and `POST /api/v1/installs/{id}/retry` or `/abort` to recover.

Connectors within a domain package are compiled into the host. Workflows and execution environments are deployed as containers.

## Building Package Images

Packages include Dockerfiles for workflows and execution environments. The `spring build` command builds container images from these Dockerfiles:

```
spring build packages/software-engineering           # build all images
spring build packages/software-engineering/workflows  # build workflow images only
spring images list                                    # list built images
```

Production deployments use pre-built images from a container registry.
