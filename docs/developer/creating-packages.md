# Creating Packages

This guide covers how to author **domain packages** — installable bundles of
agents, units, skills, and templates that bring domain expertise to the platform.

For the architecture-level reference (install ordering, two-phase install,
export, catalogue discovery), see [`docs/architecture/packages.md`](../architecture/packages.md).

## Package Structure

A package is a **folder** rooted at a `package.yaml` whose `kind:` is `Package`.
The directory layout *is* the manifest — the install pipeline discovers artefacts
by walking conventional subdirectories ([ADR-0043](../decisions/0043-recursive-package-format.md)).
There is no `content:` block.

```
packages/<domain-name>/
  package.yaml          # kind: Package — name, description, version, inputs
  agents/               # agent definition folders
  units/                # unit definition folders
  skills/               # skill bundles (prompt fragment + optional tools)
  templates/            # AgentTemplate / UnitTemplate / HumanTemplate
  execution/            # Dockerfiles for agent execution images (source, not runtime)
```

Every standalone artefact is itself a folder rooted at a `package.yaml` carrying
its own `kind:` discriminator. Folders compose recursively — a unit folder can
contain its own `agents/`, `units/`, `skills/`, and `templates/` subdirectories.

`connectors/` and `workflows/` are **not** package-vocabulary directories.
Connector *bindings* are expressed through a `requires:` block and supplied at
install time; connector plugins ship as their own .NET projects under `src/`
(see [Creating Connectors](#creating-connectors)). Workflow-driven runtimes ship
as their own container images.

A `package.yaml`:

```yaml
apiVersion: spring.voyage/v1
kind: Package
name: my-domain
description: A short description of the domain bundle.
version: 1.0.0
readme: README.md
```

## Creating Agents

Each agent is a folder under `agents/` rooted at a `package.yaml` with
`kind: Agent`:

```yaml
# packages/my-domain/agents/researcher/package.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: researcher
description: Research analyst.
role: researcher
capabilities: [analysis, summarization, literature-review]

ai:
  runtime: claude-code     # AgentRuntime id from eng/runtime-catalog/runtime-catalog.yaml (ADR-0038)
  model:
    provider: anthropic    # ModelProvider id; intrinsic to the model
    id: claude-sonnet-4-6

execution:
  image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest

instructions: |
  You are a research analyst. You analyze papers,
  summarize findings, and identify patterns.

expertise:
  - domain: machine-learning
    level: advanced
  - domain: statistics
    level: intermediate
```

Execution config is the `(runtime, model)` pair plus a top-level
`execution.image`. The `runtime` selects an **AgentRuntime** from the runtime
catalogue; the `model` names a **ModelProvider** and a model id
([ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)). The
container image is declared exactly once — top-level `execution.image`, for
agents and units alike.

## Creating Units

A unit is an agent that has children. It is a folder under `units/` with
`kind: Unit`. Its members — agents, sub-units, and humans — are declared under
one `members:` list with a key-prefix discriminator:

```yaml
# packages/my-domain/units/research-cell/package.yaml
apiVersion: spring.voyage/v1
kind: Unit
name: research-cell
description: A research cell with analysts and a human owner.
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
  skills:
    - package: my-org/my-domain
      skill: paper-analysis
instructions: |
  You coordinate a research cell. Route incoming work to the most
  appropriate member based on their expertise.
members:
  - agent: researcher
  - human:
      roles: [owner]
      notifications: ["escalation", "completion"]
execution:
  image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
requires:
  - connector: github
```

How a unit routes work across its members is **runtime behaviour** — it lives in
the unit's runtime image and instructions, not in platform configuration. There
is no "orchestration strategy" field ([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md)).

## Creating Skills

A skill is the smallest reusable artefact — a markdown prompt fragment plus
optional tool definitions. It is a folder under `skills/` with `kind: Skill`.

### Prompt Fragment

```markdown
<!-- packages/my-domain/skills/paper-analysis/prompt.md -->
## Paper Analysis

When you receive a research paper:
1. Read the abstract and introduction first
2. Identify the key contribution and methodology
3. Assess the strength of evidence
4. Note any limitations or concerns
5. Summarize in 2-3 paragraphs with your assessment
```

### Tool Definitions (Optional)

A skill may declare MCP tools alongside its prompt fragment. At install the
platform validates each declared tool against unit policy — a policy-blocked
tool fails the install; an unprovided tool is an advisory warning.

### Composing Skills

Skills are referenced in unit or agent definitions:

```yaml
ai:
  skills:
    - package: my-org/my-domain
      skill: paper-analysis
    - package: my-org/my-domain
      skill: literature-review
```

Prompt fragments render into the unit-context layer of the assembled prompt.

## Templates

A **template** is a non-activating artefact folder (`kind: AgentTemplate`,
`UnitTemplate`, or `HumanTemplate`) that a concrete artefact clones via
`from: <template>`, with scalar/map override and list-replace semantics.
Templates can be reused across packages via `from: <pkg>/<name>@<version>`.
See [`docs/architecture/packages.md` § Members and templates](../architecture/packages.md#members-and-templates).

## Agent Hosting Mode

Every agent runs in one of two modes, set by the `execution.hosting` field on a
unit or agent `execution:` block:

- `ephemeral` *(default)* — a fresh container per dispatch, torn down after the
  turn. Strongest isolation; best for short, stateless turns.
- `persistent` — a long-lived container kept running for the agent's service
  lifetime. Best for warm state and low-latency interactive agents.

Member agents inherit the parent unit's `hosting` value when neither they nor
their template declares one (precedence: `agent > template > unit > default`).
The manifest parser rejects any other literal at parse time with a structured
`ManifestParseException` — install fails fast on a typo. See
[Deployment § Agent hosting modes](../architecture/deployment.md#agent-hosting-modes).

## Creating Connectors

Connectors are **plugins**, not package artefacts. A connector implements the
`IConnectorType` contract, ships as its own .NET project, and registers through
one `AddCvoyaSpring*()` DI extension. It is a non-routable bridge — code, not an
actor; nothing routes a message *to* it. A connector translates external events
into one-way platform messages and registers outbound skills for agents
([ADR-0045](../decisions/0045-connector-domain-agnostic-platform.md)).

```
src/Cvoya.Spring.Connector.MyService/
  Cvoya.Spring.Connector.MyService.csproj
  MyServiceConnector.cs
  MyServiceEventTranslator.cs
  MyServiceSkills.cs
```

The in-tree connectors — `Cvoya.Spring.Connector.{GitHub,Arxiv,WebSearch}` — are
the worked examples. A package declares the connectors it needs through a
`requires:` block; the binding is supplied at install time. See
[`docs/architecture/connectors.md`](../architecture/connectors.md).

## Building and Installing

```
# Install a package from the in-tree catalogue
spring package install my-domain

# Poll install status
spring package status <install-id>

# Send a message to an installed unit — resolve the unit's id first
spring unit show research-cell                       # prints the canonical Guid
spring message send unit:<id> "Analyze this paper: ..."
```

`FileSystemPackageCatalogService` walks `packages/`, so a new package folder
appears in the catalogue with no further wiring. The packages root is configured
by `Packages:Root` / `SPRING_PACKAGES_ROOT`.

Install is a two-phase atomic flow with a persisted install record — see
[`docs/architecture/packages.md` § Install and export](../architecture/packages.md#install-and-export)
for the full HTTP / CLI surface, install scope (`--into <unit>`), typed package
`inputs`, and export.
