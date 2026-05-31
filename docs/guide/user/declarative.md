# Declarative Configuration

This guide covers how to build a Spring Voyage **package** as the canonical source of truth for a set of units, agents, skills, templates, and human team-member declarations — then install it with `spring package install`. Declarative is the recommended path for any configuration you intend to version, review, or reproduce across environments.

## When to use declarative

`spring package install <name>` is the primary entry point. Everything the platform installs — the OSS dogfooding org, the in-tree example packages, the agents and units that ship with `software-engineering` / `product-management` / `research` — comes from a YAML package under `packages/`. The CLI's imperative verbs (`spring unit create`, `spring agent create`) are for prototyping and one-off exploration; once a configuration is worth keeping, ship it as a package.

This guide:

- Shows a minimal package skeleton.
- Walks through building [`packages/templated-team/`](../../../packages/templated-team/) step by step — a team assembled from reusable templates.
- Covers the install-time flags: `--into`, `--as`, and how a multi-package install runs.
- Lists common pitfalls.

Conceptual background is in [Packages](../../concepts/packages.md), [Templates](../../concepts/templates.md), and [Humans](../../concepts/humans.md).

## The recursive folder layout

Every artefact in a package is a **folder** whose root file is named `package.yaml`. The `kind:` discriminator tells the parser what the folder is. The conventional subdirectories — `units/`, `agents/`, `skills/`, `templates/` — apply at every depth. There is no `content:` block on the package manifest; the directory layout *is* the manifest.

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

## Equipping skills on units and agents

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

Templates are an **optional** mechanism. The grammar is exactly the same as for concrete artefacts; the only delta is non-activation plus the `from:` operator. Reach for templates when:

- The package ships two or more instances of the same shape — for example, three software-engineer agents that share instructions, runtime, and expertise but differ only in display name.
- You want a reusable archetype that other packages reference cross-package (an archetype library: a package that ships only `templates/` and no concrete artefacts).
- The shape has nested children that should be stamped together (a unit template that ships a team-lead and a senior-engineer; every instance gets both).

Skip templates when each artefact is a genuine one-off — two agents that share nothing are clearer as two concrete folders than as a template with a single instance. The cost of duplication for two distinct one-offs is zero.

Conceptual background is in [Templates](../../concepts/templates.md).

## Adding a human team member

A unit's `members:` list carries human team members alongside agents and sub-units with the `- human:` discriminator. The slot is **inline-only** — humans own no sub-artefacts, so there is no `humans/<name>/` folder shape.

```yaml
# minimal — one role, no notifications
members:
  - human: { roles: [reviewer] }

# typical — multi-valued roles, expertise tags, notification subscriptions
members:
  - human:
      roles: [owner, security_lead]
      expertise: [security, infra]
      notifications: [escalation, completion]
```

All fields are optional. The OSS install policy resolves every declaration to the install caller (minting a fresh human entry per declaration); hosted policies decide whether to mint anew or to bind to an existing tenant member. The membership row is keyed by tenant, unit, and human; `roles` / `expertise` / `notifications` are stored as structured data on the row.

The `roles` list is multi-valued and case-insensitive; duplicates within one entry collapse at parse time. Notifications stay human-only — agents have no notification surface.

### Defining a reusable `HumanTemplate`

When the same team-role shape repeats across multiple units (an "owner" that recurs, an "OSS operator" shared across every package), promote the shape to a `HumanTemplate` and stamp it from each unit's members.

```
example-with-humans/
└── templates/
    └── oss-operator/
        └── package.yaml         # kind: HumanTemplate
```

```yaml
# packages/example-with-humans/templates/oss-operator/package.yaml
apiVersion: spring.voyage/v1
kind: HumanTemplate
name: oss-operator
displayName: OSS Operator
description: Default OSS-deployment human; fills every team role.
roles: [owner]
expertise: [operations, escalation]
notifications: [escalation, completion]
```

Stamp from any unit's `members:`:

```yaml
members:
  - human: { from: oss-operator }                          # all fields flow through
  - human: { from: oss-operator, roles: [security_lead] }  # roles replaces [owner]
```

The member entry's `roles`, `expertise`, and `notifications` fully replace the template's values when present; scalars (`displayName`, `description`) follow the scalar-override rule. Cross-package addressing uses the same `<pkg>/<name>@<version>` grammar as `AgentTemplate` / `UnitTemplate`:

```yaml
members:
  - human: { from: shared-archetypes/oss-operator@1.0.0 }
```

Conceptual background is in [Humans](../../concepts/humans.md) (the install-time resolution model) and [Templates](../../concepts/templates.md) (the `HumanTemplate` section).

## Building `templated-team` step by step

[`packages/templated-team/`](../../../packages/templated-team/) builds a unit with member agents from reusable templates. Installing it produces one unit and five agents — two stamped from a `UnitTemplate`'s nested children plus three stamped from an `AgentTemplate` referenced inline.

### Step 1 — the package root

```yaml
# packages/templated-team/package.yaml
apiVersion: spring.voyage/v1
kind: Package
name: templated-team
description: A team built from reusable templates — an engineering-team archetype (a UnitTemplate with nested children) and a software-engineer archetype (an AgentTemplate) instantiated three times under one concrete unit.
version: 1.0.0
readme: README.md
```

### Step 2 — the `AgentTemplate`

```
templated-team/
└── templates/
    └── software-engineer/
        └── package.yaml
```

```yaml
# packages/templated-team/templates/software-engineer/package.yaml
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
# packages/templated-team/templates/engineering-team/package.yaml
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
members:
  - human:
      roles: [owner]
      notifications: ["escalation", "completion"]
execution:
  image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
policies:
  communication: through-unit
  work_assignment: capability-match
  expertise_sharing: advertise
  initiative:
    max_level: attentive
    max_actions_per_hour: 10
```

The two children under `engineering-team/agents/` (`team-lead`, `senior-engineer`) are concrete `kind: Agent` folders that look exactly like ordinary agent folders — the difference is purely positional. Because they live inside a `UnitTemplate`, they are stamped fresh under every consumer instance:

```yaml
# packages/templated-team/templates/engineering-team/agents/team-lead/package.yaml
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
# packages/templated-team/units/platform-eng/package.yaml
apiVersion: spring.voyage/v1
kind: Unit
name: platform-eng
description: Platform engineering team — concrete instance of the `engineering-team` template. Stamps the template's nested team-lead and senior-engineer, plus three software-engineer instances declared below.
from: engineering-team
# Inherits ai, instructions, execution, policies from the engineering-team template.
# `members:` is omitted so the template's stamped member set (the team-lead and
# senior-engineer agents plus the - human: owner entry) flows through unchanged.
# The three concrete `from: software-engineer` agents under `units/platform-eng/agents/`
# (ada, hopper, lovelace) augment the stamped set.
```

`platform-eng` is concrete (`kind: Unit`, not `UnitTemplate`) — it activates at install time. It declares `from: engineering-team`, so the resolver:

- Reads `templates/engineering-team/package.yaml` and merges its outer fields into `platform-eng`'s body (instance wins on scalars, deep-merge on maps, replace on lists).
- Walks `templates/engineering-team/agents/` and produces fresh `team-lead` and `senior-engineer` agents under `platform-eng` with newly minted Guids.
- Because `platform-eng/package.yaml` omits `members:`, the stamped child tree from the template becomes the inherited member set.
- The three concrete `from: software-engineer` agents under `units/platform-eng/agents/` (ada, hopper, lovelace) are then concrete members of `platform-eng` — they augment the stamped tree.

### Step 5 — the three stamped engineers

```yaml
# packages/templated-team/units/platform-eng/agents/ada/package.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: ada
description: Ada — software engineer specialised in numerical / algorithmic work. Concrete instance of the `software-engineer` template.
from: software-engineer
# Inherits ai, role, capabilities, instructions, expertise from the
# software-engineer template. The instance overrides description only;
# everything else flows through.
```

`hopper/package.yaml` and `lovelace/package.yaml` follow the same shape — each declares `from: software-engineer`, each overrides only its `description:`. The `ai:`, `role:`, `capabilities:`, `instructions:`, and `expertise:` all flow through from the template per the scalar-and-deep-merge override rules.

### Step 6 — install

```bash
spring package install templated-team
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

Each agent gets a fresh Guid identity. Installing the package a second time under a different display name (`--as platform-eng-2`) produces a second, independent `platform-eng` with its own five fresh-identity agents — including a *second* `team-lead`-named agent that shares the display string but not the underlying entity.

## Install flags

### `spring package install <pkg>` — default tenant scope

```bash
spring package install hello-world
```

Top-level artefacts (every artefact directly under the package's `agents/` or `units/`) install at the tenant scope. In `hello-world`, that means the `hello-world` unit becomes a tenant-level unit. Nested artefacts (the agents under a unit's `agents/`, the skills under each agent) are owned by their containing artefact regardless of install scope.

### `spring package install <pkg> --into <unit>` — bind top-level to a parent

```bash
spring package install hello-world --into eng-portfolio
```

Every top-level agent in the package becomes a member of `eng-portfolio`; every top-level unit becomes a sub-unit of `eng-portfolio`. For `hello-world`, the `hello-world` unit becomes a sub-unit of `eng-portfolio` instead of a tenant-level unit. The package's internal structure (the unit's own greeter member) is unaffected — `--into` rebinds only the package's top-level artefacts.

`--into tenant` is the explicit form of the default. `--into <package-name>` is rejected — packages don't contain other packages' artefacts.

### `spring package install <pkg> --as <name>` — override the display name

```bash
spring package install hello-world --as my-greeter
```

When the package has exactly one top-level activatable, `--as <name>` overrides that artefact's display name. For `hello-world`, the one top-level activatable is the `hello-world` unit; the install produces a unit displayed as `my-greeter`. The artefact's stable id is still platform-assigned (a Guid); only the operator-visible display name is overridden.

The flag rejects packages that ship more than one top-level activatable — there is no single artefact to rename. To install the same package multiple times under different display names, run multiple installs each with its own `--as <name>`:

```bash
spring package install hello-world --as greeter-alpha
spring package install hello-world --as greeter-beta
```

Each install gets independent Guid identities and an independent membership graph.

## Multi-package installs

`spring package install A B C` installs multiple packages in one operator action. The pipeline topo-sorts the batch by cross-package references and installs them under a single `install_id`. Cross-package references resolve first against the in-flight batch, then against the tenant's already-installed packages; missing references abort the install with a precise error naming the offending package and reference.

Inputs are namespaced by package: `--input A.foo=bar --input B.baz=qux`. The `--into <unit>` and `--as <name>` flags apply to every target in the batch.

## Inspecting and recovering

After an install:

```bash
spring package list                          # everything in the catalog
spring package show templated-team           # one package's manifest summary
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

**`content:` is removed.** The previous package layout listed contained artefacts in a `content:` block on `package.yaml`. That block is gone. The resolver walks the conventional subdirectories (`units/`, `agents/`, `skills/`, `templates/`) and treats every direct child folder as one artefact. Install ordering is derived from `requires:` topology; if you need B to install after A, declare `requires:` on B. The `workflows/` and `connectors/` subdirectories are also removed.

**Humans live on `members:`, not on a separate top-level block.** Unit manifests with a top-level `humans:` block are no longer supported. Add a `- human:` entry to the unit's `members:` list instead — see the "Adding a human team member" subsection above.

**Inner `package.yaml` files do not declare `version:`.** `version:` lives only on the install-root `package.yaml`. Inner artefacts inherit from the container. An inner `version:` is a parse error (`UnexpectedInnerVersion`).

**Trivial leaves don't need their own folder.** An agent with no nested skills, no expertise, nothing but instructions and runtime config can be embedded inline in its parent's `members:` list as a full document. The folder form is for artefacts that grow companion files (READMEs, embedded skills).

**`from: <template>` triggers stamping, not extension.** Each `from:` produces a *new* concrete artefact with a freshly minted Guid identity. Installing a templated package twice produces two independent sets of stamped instances that share display names but not entities.

## Related reading

- [Packages](../../concepts/packages.md) — recursive folder layout, kinds, install scope.
- [Templates](../../concepts/templates.md) — type / instance separation, `from:`, snapshot binding, `HumanTemplate`.
- [Humans](../../concepts/humans.md) — team-role / platform-role split, install-time resolution, post-install editing.
- [Examples](examples.md) — index of the in-tree example packages and the CLI scenario suite.
- [`spring package` CLI reference](../../cli-reference.md) — full flag set, exit codes, recovery surface.
