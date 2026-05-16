# Declarative Configuration

This guide covers how to build a Spring Voyage **package** as the canonical source of truth for a set of units, agents, skills, and workflows — then install it with `spring package install`. Declarative is the recommended path for any configuration you intend to version, review, or reproduce across environments.

## When to use declarative

`spring package install <name>` is the primary entry point. Everything the platform installs — the OSS dogfooding org, the in-tree example packages, the agents and units that ship with `software-engineering` / `product-management` / `research` — comes from a YAML package under `packages/`. The CLI's imperative verbs (`spring unit create`, `spring agent create`) are for prototyping and one-off exploration; once a configuration is worth keeping, ship it as a package.

This guide:

- Shows a minimal package skeleton.
- Walks through building [`packages/example-simple/`](../../../packages/example-simple/) step by step — instances only, no templates.
- Walks through building [`packages/example-templated/`](../../../packages/example-templated/) step by step — same shape, templated.
- Covers the install-time flags: `--into`, `--as`, and how a multi-package install runs.
- Lists common pitfalls.

Conceptual background is in [Packages](../../concepts/packages.md) and [Templates](../../concepts/templates.md); the schema decisions are in [ADR-0043](../../decisions/0043-recursive-package-format.md), [ADR-0037](../../decisions/0037-package-schema-decomposition.md), and [ADR-0035](../../decisions/0035-package-as-bundling-unit.md).

## The recursive folder layout

Every artefact in a package is a **folder** whose root file is named `package.yaml`. The `kind:` discriminator tells the parser what the folder is. The conventional subdirectories — `units/`, `agents/`, `skills/`, `workflows/`, `connectors/`, `templates/` — apply at every depth. There is no `content:` block on the package manifest; the directory layout *is* the manifest.

A minimal package skeleton:

```
hello/
├── package.yaml                # kind: Package
└── units/
    └── hello-team/             # kind: Unit
        └── package.yaml
```

`hello/package.yaml`:

```yaml
apiVersion: spring.voyage/v1
kind: Package
name: hello
description: Minimal package — one unit, no nested artefacts.
version: 1.0.0
```

`hello/units/hello-team/package.yaml`:

```yaml
apiVersion: spring.voyage/v1
kind: Unit
name: hello-team
description: A minimal unit.
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
instructions: |
  You are a friendly unit. Reply to incoming messages with a brief acknowledgement.
```

Install it:

```bash
spring package install hello
```

The package's one top-level artefact (`hello-team`) activates at the tenant scope. That is the entire grammar.

## Building `example-simple` step by step

[`packages/example-simple/`](../../../packages/example-simple/) is the instance-only example package. Installing it produces one unit, two agents that are members of that unit, and one skill owned by each agent. There are no templates and no `from:` clones — every artefact is a concrete folder.

### Step 1 — the package root

```yaml
# packages/example-simple/package.yaml
apiVersion: spring.voyage/v1
kind: Package
name: example-simple
description: ADR-0043 example — instance-only package with no templates. A single greeting team with two distinct greeter agents, each owning its own skill.
version: 1.0.0
readme: README.md
# ADR-0043 §2: directory layout is the manifest — no `content:` block.
```

The `package.yaml` carries only metadata: `name`, `description`, `version`, optional `readme`. Notice there is no `content:` block listing the contained artefacts — the resolver discovers them by walking `./units/`, `./agents/`, etc.

### Step 2 — the unit folder

```
example-simple/
└── units/
    └── greeting-team/
        └── package.yaml
```

```yaml
# packages/example-simple/units/greeting-team/package.yaml
apiVersion: spring.voyage/v1
kind: Unit
name: greeting-team
description: An ADR-0043 example unit — coordinates two greeter agents (one casual, one formal). Connector-free.
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
instructions: |
  You coordinate a two-agent greeting team. Route casual / informal
  greetings to friendly-greeter and formal / professional greetings to
  polite-greeter. There are no other team members; do not invent
  capabilities the package does not ship.
members:
  - agent: friendly-greeter
  - agent: polite-greeter
execution:
  image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
policies:
  communication: through-unit
  work_assignment: capability-match
  expertise_sharing: advertise
  initiative:
    max_level: attentive
    max_actions_per_hour: 10
humans:
  - identity: owner
    permission: owner
    notifications: ["escalation", "completion"]
```

Two things to notice:

- The folder is named `greeting-team/`. The `name:` field inside its `package.yaml` is also `greeting-team`. Folder name must equal `name:` — disagreement is a parse error.
- `members:` references its child agents by bare name (`friendly-greeter`, `polite-greeter`). The resolver looks those up by walking the unit folder's own `agents/` directory first.

### Step 3 — the agent folders

```
greeting-team/
└── agents/
    ├── friendly-greeter/
    │   └── package.yaml
    └── polite-greeter/
        └── package.yaml
```

```yaml
# packages/example-simple/units/greeting-team/agents/friendly-greeter/package.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: friendly-greeter
description: Friendly Greeter — replies to incoming messages with a warm, casual acknowledgement. Owns the `say-hello` skill.
role: greeter
capabilities: ["greeting", "casual-tone"]
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
  environment:
    image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
instructions: |
  You are a friendly, casual greeter. When you receive a message, reply
  with a warm acknowledgement that uses the `say-hello` skill verbatim
  as your opener, then a short personalised follow-up sentence.
expertise:
  - domain: casual-greeting
    level: expert
```

`polite-greeter/package.yaml` is the mirror image — same shape, formal tone, owns `say-hello-formally` instead.

Because both agent folders sit inside `units/greeting-team/agents/`, they are members of `greeting-team`, not tenant-level agents. The recursive layout makes ownership explicit: an agent's folder location *is* its parent.

### Step 4 — the skill folders

```
friendly-greeter/
└── skills/
    └── say-hello/
        ├── package.yaml
        └── say-hello.md
```

```yaml
# packages/example-simple/units/greeting-team/agents/friendly-greeter/skills/say-hello/package.yaml
apiVersion: spring.voyage/v1
kind: Skill
name: say-hello
description: Casual hello opener — a one-line warm greeting.
```

```markdown
<!-- say-hello.md -->
## say-hello

Use this opener verbatim:

> Hey there — thanks for the message!

Follow it with a brief, personalised sentence that names the message's
subject if one is identifiable.
```

The skill is owned by `friendly-greeter` and only granted to that one agent — it sits *inside* the agent folder, not in a sibling `skills/` directory. The agent-scoped grant means `polite-greeter`, even though it is a member of the same unit, does not see `say-hello`.

### Step 5 — install

```bash
spring package install example-simple
```

The resolver walks the recursive layout, topo-sorts the artefacts, runs the two-phase install, and activates:

- **1 Unit:** `greeting-team` at the tenant scope.
- **2 Agents:** `friendly-greeter`, `polite-greeter`, both members of `greeting-team`.
- **2 Skills:** `say-hello` granted to `friendly-greeter`, `say-hello-formally` granted to `polite-greeter`.

The package ships connector-free, so no `--connector` flag is required.

## Equipping skills on units and agents (#2361)

The declarative path above plants skills inside an agent folder so the package installer grants them at install time. After install, operators can also equip already-installed skill bundles onto units and agents from the CLI — useful for trying a bundle without re-publishing the package, or for assembling a working configuration before committing it back to YAML. The bundles still come from installed packages; equipping just attaches them to a subject and feeds the bundle prompt into Layer 2 (unit) or Layer 4 (agent) of the next dispatched prompt.

Addressing is `<pkg>/<skill>`. No `@<version>`, no aliases — skills track the currently installed package version, so reinstalling the package picks up the new body on the next dispatch.

```bash
# Inspect what is equipped on an agent or unit
spring agent skills list ada
spring unit skills list engineering

# Equip / unequip a single bundle (idempotent on both ends)
spring agent skills add ada    --skill spring-voyage/software-engineering/code-review
spring agent skills remove ada --skill spring-voyage/software-engineering/code-review

# Bulk-replace the persisted list (comma-separated, declaration-order)
spring agent skills set ada --skills \
  spring-voyage/software-engineering/code-review,\
  spring-voyage/product-management/prd-review

# Clear all equipped bundles on a subject
spring agent skills set ada --skills ""
```

`set` composes its diff client-side (remove dropped, equip new) — the API has no atomic bulk write, so a mid-flight failure leaves the subject partially applied. Run `list` after a failed `set` and replay the missing rows. Exit codes match the rest of the CLI: `0` on success, `1` for API errors (404 subject-not-found, 400 package-or-skill-not-installed), `2` for argument errors.

The same verbs apply to units (`spring unit skills …`) with identical semantics; bundles equipped at the unit level feed Layer 2 of the unit's own prompt assembly.

## When to use templates — sidebar

Templates ([ADR-0043 §5](../../decisions/0043-recursive-package-format.md#5-type-and-instance-templates-are-non-activating-artefact-folders-cloned-by-from)) are an **optional** mechanism. The grammar is exactly the same as for concrete artefacts; the only delta is non-activation plus the `from:` operator. Reach for templates when:

- The package ships two or more instances of the same shape — for example, three software-engineer agents that share instructions, runtime, and expertise but differ only in display name.
- You want a reusable archetype that other packages reference cross-package (an archetype library: a package that ships only `templates/` and no concrete artefacts).
- The shape has nested children that should be stamped together (a unit template that ships a team-lead and a senior-engineer; every instance gets both).

Skip templates when each artefact is a one-off — `example-simple` deliberately uses two concrete agents to keep the package readable. The cost of duplication for two distinct one-offs is zero.

Conceptual background is in [Templates](../../concepts/templates.md).

## Building `example-templated` step by step

[`packages/example-templated/`](../../../packages/example-templated/) is the templated counterpart to `example-simple`. Same problem shape (a unit with member agents); different authoring style. Installing it produces one unit and five agents — two stamped from a `UnitTemplate`'s nested children plus three stamped from an `AgentTemplate` referenced inline.

### Step 1 — the package root

```yaml
# packages/example-templated/package.yaml
apiVersion: spring.voyage/v1
kind: Package
name: example-templated
description: ADR-0043 example — template-based package. An engineering team archetype (UnitTemplate with nested children) and a software-engineer archetype (AgentTemplate) instantiated three times under one concrete unit.
version: 1.0.0
readme: README.md
```

### Step 2 — the `AgentTemplate`

```
example-templated/
└── templates/
    └── software-engineer/
        └── package.yaml
```

```yaml
# packages/example-templated/templates/software-engineer/package.yaml
apiVersion: spring.voyage/v1
kind: AgentTemplate
name: software-engineer
description: "Software engineer archetype — the type definition. Concrete instances declare `from: software-engineer` and inherit ai, role, capabilities, and instructions by default."
role: software-engineer
capabilities: ["dotnet-development", "code-review", "testing", "refactoring"]
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
  environment:
    image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
instructions: |
  You are a software engineer. When assigned work:
  1. Read the task brief and the relevant code paths before editing
  2. Make focused changes — one logical concern per PR
  3. Write or update tests for every behavioural change
  4. Run the project's build and test gates locally before requesting review
expertise:
  - domain: dotnet-development
    level: advanced
  - domain: code-review
    level: advanced
  - domain: testing
    level: advanced
```

`kind: AgentTemplate` accepts every field a concrete `Agent` accepts. The only behavioural difference is that `AgentTemplate` does not activate when the package installs — it exists to be stamped.

### Step 3 — the `UnitTemplate` with stamped children

```
templates/
└── engineering-team/
    ├── package.yaml
    └── agents/
        ├── team-lead/
        │   └── package.yaml
        └── senior-engineer/
            └── package.yaml
```

```yaml
# packages/example-templated/templates/engineering-team/package.yaml
apiVersion: spring.voyage/v1
kind: UnitTemplate
name: engineering-team
description: Engineering team archetype — the type definition. Ships nested concrete children (`team-lead`, `senior-engineer`) that the install pipeline stamps under each consumer instance.
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
instructions: |
  You orchestrate a software engineering team made up of a team lead, a
  senior engineer, and additional individual contributors that the
  consumer adds via concrete `from: software-engineer` instances.
execution:
  image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
policies:
  communication: through-unit
  work_assignment: capability-match
  expertise_sharing: advertise
  initiative:
    max_level: attentive
    max_actions_per_hour: 10
humans:
  - identity: owner
    permission: owner
    notifications: ["escalation", "completion"]
```

The two children under `engineering-team/agents/` (`team-lead`, `senior-engineer`) are concrete `kind: Agent` folders that look exactly like ordinary agent folders — the difference is purely positional. Because they live inside a `UnitTemplate`, they are stamped fresh under every consumer instance:

```yaml
# packages/example-templated/templates/engineering-team/agents/team-lead/package.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: team-lead
description: Team lead — owns design decisions, code-review hygiene, and unblocking the team. Stamped fresh under each `engineering-team` instance.
role: team-lead
capabilities: ["design-review", "code-review", "mentoring", "scope-discipline"]
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-sonnet-4-6
  environment:
    image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
instructions: |
  You are the team lead. Hold the line on declared PR scope. Review
  designs before code lands. Coach individual contributors rather than
  rewriting their work.
expertise:
  - domain: design-review
    level: expert
  - domain: code-review
    level: expert
```

`senior-engineer/package.yaml` follows the same shape.

### Step 4 — the concrete `Unit` that stamps the template

```
units/
└── platform-eng/
    ├── package.yaml
    └── agents/
        ├── ada/
        ├── hopper/
        └── lovelace/
```

```yaml
# packages/example-templated/units/platform-eng/package.yaml
apiVersion: spring.voyage/v1
kind: Unit
name: platform-eng
description: Platform engineering team — concrete instance of the `engineering-team` template. Stamps the template's nested team-lead and senior-engineer, plus three software-engineer instances declared below.
from: engineering-team
# Inherits ai, instructions, execution, policies, humans from the
# engineering-team template per ADR-0043 §5d. `members:` is omitted so
# the template's stamped child tree (team-lead, senior-engineer) is the
# member set, augmented by the three concrete agents under
# `units/platform-eng/agents/` (ada, hopper, lovelace).
```

`platform-eng` is concrete (`kind: Unit`, not `UnitTemplate`) — it activates at install time. It declares `from: engineering-team`, so the resolver:

- Reads `templates/engineering-team/package.yaml` and merges its outer fields into `platform-eng`'s body (instance wins on scalars, deep-merge on maps, replace on lists).
- Walks `templates/engineering-team/agents/` and produces fresh `team-lead` and `senior-engineer` agents under `platform-eng` with newly minted Guids.
- Because `platform-eng/package.yaml` omits `members:`, the stamped child tree from the template becomes the inherited member set.
- The three concrete `from: software-engineer` agents under `units/platform-eng/agents/` (ada, hopper, lovelace) are then concrete members of `platform-eng` — they augment the stamped tree.

### Step 5 — the three stamped engineers

```yaml
# packages/example-templated/units/platform-eng/agents/ada/package.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: ada
description: Ada — software engineer specialised in numerical / algorithmic work. Concrete instance of the `software-engineer` template.
from: software-engineer
# Inherits ai, role, capabilities, instructions, expertise from the
# software-engineer template per ADR-0043 §5d. The instance overrides
# description only; everything else flows through.
```

`hopper/package.yaml` and `lovelace/package.yaml` follow the same shape — each declares `from: software-engineer`, each overrides only its `description:`. The `ai:`, `role:`, `capabilities:`, `instructions:`, and `expertise:` all flow through from the template per the scalar-and-deep-merge override rules.

### Step 6 — install

```bash
spring package install example-templated
```

Activated tree:

```
platform-eng (Unit)
├── team-lead         (Agent, stamped from engineering-team's nested children)
├── senior-engineer   (Agent, stamped from engineering-team's nested children)
├── ada               (Agent, concrete, from: software-engineer)
├── hopper            (Agent, concrete, from: software-engineer)
└── lovelace          (Agent, concrete, from: software-engineer)
```

Each agent gets a fresh Guid identity ([ADR-0036](../../decisions/0036-single-identity-model.md)). Installing the package a second time under a different display name (`--as platform-eng-2`) produces a second, independent `platform-eng` with its own five fresh-identity agents — including a *second* `team-lead`-named agent that shares the display string but not the underlying entity.

## Install flags

### `spring package install <pkg>` — default tenant scope

```bash
spring package install example-simple
```

Top-level artefacts (every artefact directly under the package's `agents/` or `units/`) install at the tenant scope. In `example-simple`, that means `greeting-team` becomes a tenant-level unit. Nested artefacts (agents under `greeting-team/agents/`, skills under each agent) are owned by their containing artefact regardless of install scope.

### `spring package install <pkg> --into <unit>` — bind top-level to a parent

```bash
spring package install example-simple --into eng-portfolio
```

Every top-level agent in the package becomes a member of `eng-portfolio`; every top-level unit becomes a sub-unit of `eng-portfolio`. For `example-simple`, `greeting-team` becomes a sub-unit of `eng-portfolio` instead of a tenant-level unit. The package's internal structure (`greeting-team`'s own members, the skills owned by each member agent) is unaffected — `--into` rebinds only the package's top-level artefacts.

`--into tenant` is the explicit form of the default. `--into <package-name>` is rejected — packages don't contain other packages' artefacts.

### `spring package install <pkg> --as <name>` — override the display name

```bash
spring package install example-simple --as greeters-alpha
```

When the package has exactly one top-level activatable, `--as <name>` overrides that artefact's display name. For `example-simple`, the one top-level activatable is `greeting-team`; the install produces a unit displayed as `greeters-alpha`. The artefact's stable id is still platform-assigned (Guid per [ADR-0036](../../decisions/0036-single-identity-model.md)); only the operator-visible display name is overridden.

The flag rejects packages that ship more than one top-level activatable — there is no single artefact to rename. To install the same package multiple times under different display names, run multiple installs each with its own `--as <name>`:

```bash
spring package install example-simple --as greeters-alpha
spring package install example-simple --as greeters-beta
```

Each install gets independent Guid identities and an independent membership graph.

## Multi-package installs

`spring package install A B C` installs multiple packages in one operator action. The pipeline topo-sorts the batch by cross-package references and installs them under a single `install_id`. Cross-package references resolve first against the in-flight batch, then against the tenant's already-installed packages; missing references abort the install with a precise error naming the offending package and reference. See [ADR-0035 §14](../../decisions/0035-package-as-bundling-unit.md#14-multi-package-install) for the full grammar.

Inputs are namespaced by package: `--input A.foo=bar --input B.baz=qux`. The `--into <unit>` and `--as <name>` flags apply to every target in the batch.

## Inspecting and recovering

After an install:

```bash
spring package list                          # everything in the catalog
spring package show example-simple           # one package's manifest summary
spring package status <install-id>           # phase + per-artefact state
```

If Phase 2 fails (a Dapr placement timeout, a container image pull error, a model probe failure), staging rows stay visible:

```bash
spring package retry <install-id>            # re-run Phase 2 after fixing the underlying issue
spring package abort <install-id>            # discard the staging rows; uninstall partial work
```

`spring package export <unit-name>` writes the originally-supplied package YAML back from an installed unit, using the verbatim `OriginalManifestYaml` blob persisted at install time. See [`spring package`](../../cli-reference.md) in the CLI reference.

## Common pitfalls

**Folder name must equal `name:`.** A folder `units/foo/` must contain a `package.yaml` whose `name:` is `foo`. Disagreement produces `ArtefactFolderNameMismatch`. The folder is the human-navigation surface; the `name:` is the resolver's addressable id; the rule keeps them in lockstep.

**Names are package-scoped, not folder-scoped.** Two agents named `architect` cannot coexist in the same package, even if they live in different parts of the folder tree (`units/eng/agents/architect/` and `agents/architect/`). The catalog indexes artefacts by `name:` within a package.

**`content:` is removed.** The previous package layout listed contained artefacts in a `content:` block on `package.yaml`. That block is gone. The resolver walks the conventional subdirectories (`units/`, `agents/`, `skills/`, `workflows/`, `connectors/`, `templates/`) and treats every direct child folder as one artefact. Install ordering is derived from `requires:` topology; if you need B to install after A, declare `requires:` on B. See the [ADR-0043 §8 migration table](../../decisions/0043-recursive-package-format.md#8-migration-hard-rename-parse-error-on-the-flat-shape-no-shim) for the full set of migration errors and the exact error names.

**Inner `package.yaml` files do not declare `version:`.** `version:` lives only on the install-root `package.yaml`. Inner artefacts inherit from the container. An inner `version:` is a parse error (`UnexpectedInnerVersion`).

**Trivial leaves don't need their own folder.** An agent with no nested skills, no expertise, nothing but instructions and runtime config can be embedded inline in its parent's `members:` list as a full document. The folder form is for artefacts that grow companion files (READMEs, embedded skills).

**`from: <template>` triggers stamping, not extension.** Each `from:` produces a *new* concrete artefact with a freshly minted Guid identity. Installing a templated package twice produces two independent sets of stamped instances that share display names but not entities.

## Related reading

- [Packages](../../concepts/packages.md) — recursive folder layout, kinds, install scope.
- [Templates](../../concepts/templates.md) — type / instance separation, `from:`, snapshot binding.
- [Examples](examples.md) — index of the in-tree example packages and the CLI scenario suite.
- [`spring package` CLI reference](../../cli-reference.md) — full flag set, exit codes, recovery surface.
- [ADR-0043](../../decisions/0043-recursive-package-format.md), [ADR-0037](../../decisions/0037-package-schema-decomposition.md), [ADR-0035](../../decisions/0035-package-as-bundling-unit.md) — the schema and install-pipeline decisions.
