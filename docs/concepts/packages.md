# Packages

Spring Voyage is domain-agnostic by design. The platform provides primitives — agents, units, messaging, orchestration. **Packages** provide the actors, the humans, and the domain knowledge that drives them.

This page is the canonical packaging-structure reference: the recursive folder layout, the conventional subdirectories, the unified `members:` grammar, the three template kinds, the strict-parsing rejections, and where the schemas live in source. Cross-references to ADRs carry the motivation; this page carries the "how to read / how to author".

## What a package is

A **package** is the unit of bundling, installation, and export. One package is what `spring package install` installs in one operator action ([ADR-0035](../decisions/0035-package-as-bundling-unit.md)). A package can ship agents, units, skills, templates, and the inline human-member declarations that wire each unit's team to its installed parts.

The catalog is global; installations are tenant-scoped. Two tenants installing the same package get independent actor sets.

## The recursive folder layout

Every standalone artefact in a package — the package itself, every unit, every agent, every skill, every template — is a **folder** whose root file is always named `package.yaml` ([ADR-0043](../decisions/0043-recursive-package-format.md) §1). The `package.yaml` file carries a `kind:` discriminator that tells the parser what the folder is:

```
my-package/
├── package.yaml                          # kind: Package — the install root
├── README.md
├── units/
│   └── engineering/                      # kind: Unit
│       ├── package.yaml
│       ├── agents/
│       │   └── tech-lead/                # kind: Agent — member of engineering
│       │       ├── package.yaml
│       │       └── skills/
│       │           └── review-prs/       # kind: Skill — granted only to tech-lead
│       │               ├── package.yaml
│       │               └── review-prs.md
│       └── skills/
│           └── team-standards/           # kind: Skill — unit-scoped
│               ├── package.yaml
│               └── team-standards.md
├── agents/
│   └── triage-bot/                       # kind: Agent at the package root
│       ├── package.yaml
│       └── README.md
├── skills/
│   └── …                                 # package-scoped skills
└── templates/
    ├── software-engineer/                # kind: AgentTemplate
    │   └── package.yaml
    ├── engineering-team/                 # kind: UnitTemplate
    │   ├── package.yaml
    │   └── agents/
    │       └── …
    └── oss-operator/                     # kind: HumanTemplate
        └── package.yaml
```

The conventional subdirectories at every depth are `units/`, `agents/`, `skills/`, and `templates/` ([ADR-0043 §2](../decisions/0043-recursive-package-format.md), amended by [ADR-0046 §2](../decisions/0046-unified-members-grammar.md)). Any folder that activates as an actor can itself contain these subdirectories — an agent folder can ship its own `skills/`, a unit folder can ship its own `agents/`, `units/`, `skills/`, and a template folder mirrors the same shape. Recursion bottoms out at skills and at trivial agents that ship no nested children.

Non-conventional directories (`docs/`, `examples/`, `tests/`, `.git/`) are ignored by the resolver. The folder layout *is* the manifest: there is no `content:` block on `package.yaml` enumerating what the package contains. The catalog walker discovers artefacts by walking the conventional subdirectories.

The conventional / rejected subdirectory tables that drive the walker are defined on `PackageManifestParser.ConventionalSubdirs` and `PackageManifestParser.RejectedSubdirs` in [`src/Cvoya.Spring.Manifest/PackageManifestParser.cs`](../../src/Cvoya.Spring.Manifest/PackageManifestParser.cs).

## The artefact kinds

| `kind:` value | Activates? | Folder lives under | Purpose |
|---|---|---|---|
| `Package` | No | Install root | Bundles artefacts; carries the package's `name`, `description`, `version`, `readme` |
| `Unit` | Yes | `units/` | Composite agent that owns children ([Units](units.md)) |
| `Agent` | Yes | `agents/` | Leaf agent with a mailbox and an execution config ([Agents](agents.md)) |
| `Skill` | Yes (granted, not activated) | `skills/` | A prompt fragment plus optional tool definitions ([Skills](skills.md)) |
| `UnitTemplate` | No | `templates/` | Archetype cloned by `from:` on a concrete `Unit` ([Templates](templates.md)) |
| `AgentTemplate` | No | `templates/` | Archetype cloned by `from:` on a concrete `Agent` |
| `HumanTemplate` | No | `templates/` | Archetype cloned by `from:` on a unit-member `- human:` entry ([ADR-0046 §4](../decisions/0046-unified-members-grammar.md)) |

`Package` and the three `*Template` kinds do not activate. `Package` is the install root; templates exist to be stamped. Everything else activates when installed; skills are *granted* to their owning agent at activation time rather than activated as standalone actors.

The `templates/` directory hosts `UnitTemplate`, `AgentTemplate`, and `HumanTemplate` side by side; the inner `kind:` field on each `package.yaml` disambiguates ([ADR-0043 §5b](../decisions/0043-recursive-package-format.md), amended by [ADR-0046 §4](../decisions/0046-unified-members-grammar.md)).

Source-of-truth schemas live in `src/Cvoya.Spring.Manifest/`:

- [`PackageManifest.cs`](../../src/Cvoya.Spring.Manifest/PackageManifest.cs) — the package root.
- [`UnitManifest.cs`](../../src/Cvoya.Spring.Manifest/UnitManifest.cs) — units and the `MemberManifest` / `HumanManifest` shapes that drive the `members:` block.
- [`AgentManifest.cs`](../../src/Cvoya.Spring.Manifest/AgentManifest.cs) — agents.
- [`SkillManifest.cs`](../../src/Cvoya.Spring.Manifest/SkillManifest.cs) — skills.
- [`UnitTemplateManifest.cs`](../../src/Cvoya.Spring.Manifest/UnitTemplateManifest.cs), [`AgentTemplateManifest.cs`](../../src/Cvoya.Spring.Manifest/AgentTemplateManifest.cs), [`HumanTemplateManifest.cs`](../../src/Cvoya.Spring.Manifest/HumanTemplateManifest.cs) — the three template kinds.
- [`ArtefactKind.cs`](../../src/Cvoya.Spring.Core/Artefacts/ArtefactKind.cs) — the runtime enum the walker and the lifecycle pipeline route on.

When you need to know exactly which fields a kind accepts, those files are authoritative; YAML examples in this guide are illustrations, not specifications.

## Names are package-scoped; folders mirror them

Every artefact carries a `name:` field on its `package.yaml`. The name must be **unique within its containing package**, regardless of where in the folder tree the artefact lives. Two agents named `architect` cannot coexist in the same package even if one sits under `units/eng/agents/architect/` and the other under `agents/architect/`.

The folder name must equal the `name:` field. `units/foo/` must contain a `package.yaml` whose `name:` is `foo`. Disagreement is a parse error (`ArtefactFolderNameMismatch`). This rule keeps the membership graph and the filesystem trivially scannable.

References from inside an artefact use bare names (`- agent: bar`). The resolver looks up `bar` by walking the containing artefact's `agents/`, then its parent's `agents/`, then the package root — **lexical scoping by directory containment**, first match wins.

## The unified `members:` grammar

A unit's participants — agents, sub-units, and humans — are declared on one `members:` list. The discriminator is the entry's key prefix: `- agent:`, `- unit:`, or `- human:` ([ADR-0046 §1](../decisions/0046-unified-members-grammar.md)). A member entry carries exactly one of the three; entries with zero or more than one are rejected at parse time.

```yaml
members:
  - agent: ada                                     # bare reference (folder agent)
  - agent:                                         # inline body — local symbol = name
      name: hopper
      from: software-engineer
      roles: [reviewer]
  - unit: { from: engineering, name: engineering-1 }
  - human:
      roles: [owner, security_lead]
      expertise: [security, infra]
      notifications: [escalation, completion]
  - human: { from: oss-operator }                  # template stamp
```

The slots and their shapes live on `MemberManifest` in [`UnitManifest.cs`](../../src/Cvoya.Spring.Manifest/UnitManifest.cs); the inline-vs-reference union is `InlineArtefactDefinition` in [`InlineArtefactDefinition.cs`](../../src/Cvoya.Spring.Manifest/InlineArtefactDefinition.cs).

### `- agent:` and `- unit:`

Both discriminators accept two shapes ([ADR-0046 §6](../decisions/0046-unified-members-grammar.md), preserving the [ADR-0043 §5g](../decisions/0043-recursive-package-format.md) status quo):

- **Bare reference** — a scalar naming a local symbol (a sibling agent / sub-unit) or a cross-package Guid. The resolver looks the symbol up by walking the lexical-scope chain.
- **Inline body** — a mapping carrying a fresh artefact body. When the body declares `from:` the install pipeline stamps a fresh concrete child by cloning the named template (§5d merge rules). The inline body's `name:` serves as the local symbol the unit references; identity is a fresh Guid minted at install time.

Authoring rule of thumb: **if the agent owns children (its own skills or sub-agents), give it a folder; otherwise, inline it.** Folder agents are the right shape for `agents/data-analyst/skills/literature-review/`; inline bodies are the right shape for "fan three engineers out of one template".

Both shapes carry the multi-valued `roles` and `expertise` ([ADR-0046 §3](../decisions/0046-unified-members-grammar.md)) — runtime metadata on the membership edge, surfaced through `sv.list_members` so peers can ask "who are the owners on my team?".

### `- human:`

Humans are inline-only — they own no sub-artefacts, so the folder shape is rejected ([ADR-0046 §6](../decisions/0046-unified-members-grammar.md)). The body fields are defined on `HumanManifest` in [`UnitManifest.cs`](../../src/Cvoya.Spring.Manifest/UnitManifest.cs):

| Field | Shape | Notes |
|---|---|---|
| `displayName` | string | Optional. Overrides the install policy's derived default. |
| `description` | string | Optional. Persisted verbatim onto `HumanEntity.Description`; editable post-install. |
| `from` | string | Optional. Stamps from a `HumanTemplate` (see below); supports cross-package addressing. |
| `roles` | string[] | Optional. Free-form team roles; multi-valued case-insensitive set. |
| `expertise` | string[] | Optional. Free-form expertise tags. |
| `notifications` | string[] | Optional. Free-form notification event tags. Humans only — agents have no notification surface. |

Each install-time `- human:` declaration mints a fresh `HumanEntity` row ([ADR-0046 §7](../decisions/0046-unified-members-grammar.md)). The OSS install policy resolves every declaration to the install caller; hosted policies decide whether to mint or to bind to an existing tenant member. See [Humans](humans.md) for the resolution model and the operator-editable Identity / Config surfaces.

The legacy top-level `humans:` block from [ADR-0044 §2](../decisions/0044-team-role-vs-platform-role.md) is removed. The parser rejects it with a structured `LegacyHumansBlock` error pointing at [ADR-0046](../decisions/0046-unified-members-grammar.md).

## Templates: type and instance

Templates separate the *type* of a unit, agent, or human from the *instances* that activate at install time. The mechanism is one `from:` operator, one set of override rules, three template kinds. Conceptual background is in [Templates](templates.md); the grammar is in [ADR-0043 §5](../decisions/0043-recursive-package-format.md#5-type-and-instance-templates-are-non-activating-artefact-folders-cloned-by-from); the HumanTemplate addition is in [ADR-0046 §4](../decisions/0046-unified-members-grammar.md).

```yaml
# packages/spring-voyage-oss/templates/oss-operator/package.yaml
apiVersion: spring.voyage/v1
kind: HumanTemplate
name: oss-operator
displayName: OSS Operator
description: Default OSS-deployment human; fills every team role.
roles: [owner]
expertise: [operations, escalation]
notifications: [escalation, completion]
```

Stamped from a unit's members:

```yaml
members:
  - human: { from: oss-operator }
  - human: { from: oss-operator, roles: [security_lead] }   # roles override
```

**Override semantics** ([ADR-0046 §5](../decisions/0046-unified-members-grammar.md)): when a member entry sets `roles`, `expertise`, or `notifications` and the referenced template also sets them, the entry **replaces** the template's value (full replacement, not union). Same scalar-vs-list rules apply across all three template kinds; deep merge on maps; scalars override; lists replace. Authors who want the template's list plus extras copy the template's list and add to it.

`HumanTemplate` folders own no sub-artefacts. `UnitTemplate` and `AgentTemplate` folders mirror the recursive layout — a `UnitTemplate` can ship `agents/`, `units/`, `skills/`, `templates/` exactly like a concrete `Unit`.

## `requires:` and connector bindings

Every artefact's `package.yaml` may declare a `requires:` block listing what that artefact needs to function ([ADR-0037 §3](../decisions/0037-package-schema-decomposition.md)):

```yaml
requires:
  - connector: github
  - connector: slack
```

The package's effective requirement set at install time is the union of every contained artefact's `requires:`, deduplicated by requirement key. The install pipeline asks the operator for one binding per unique requirement and injects it into every artefact that declared it. Connector *bindings* are the load-bearing feature — the platform binds a unit to an installed connector type and routes external events through it; see [Connectors](connectors.md) for the binding model.

What's **not** part of the package vocabulary in v0.1: shipping a connector *definition* inside a package ([ADR-0046 §2](../decisions/0046-unified-members-grammar.md)). The `connectors/` subdirectory is removed; the parser rejects it at any depth with a `LegacyConnectorsSubdir` error. Re-introduce the conventional directory only when a future ADR adds a real authoring need.

`requires:` also drives **install ordering**: the pipeline topo-sorts every discovered artefact across every subdirectory; ties are broken by stable alphabetical order on `name:`. Operators express "B needs A" by adding the `requires:` entry, not by listing A before B in some manifest.

## Cross-package references

Every artefact reference can be qualified to reach into another installed package ([ADR-0037 §5](../decisions/0037-package-schema-decomposition.md)):

```yaml
members:
  - agent: shared-tools/code-reviewer@2.1.0
  - unit: shared-platform/router@1.2.0
  - human: { from: shared-archetypes/oss-operator@1.0.0 }
```

The grammar is `<package>/<name>@<version>`. When `@<version>` is omitted, the resolver picks the most recently installed version of the package for the tenant. The qualifier resolves the same way for every artefact kind — units, agents, skills, and all three template kinds.

`name:` is the addressable id. Folder location is for human navigation; path-based addressing (`<pkg>/units/foo/agents/bar`) is **not** a wire form.

## Install scope: tenant by default, `--into` to bind to a parent unit

`spring package install <pkg>` installs at the **tenant scope**. Every top-level agent in the package becomes a tenant-level agent; every top-level unit becomes a tenant-level unit. Nested artefacts (an agent inside a unit folder, a skill inside an agent folder) are unaffected by install scope — they are owned by their containing artefact.

`--into <unit>` rebinds the top-level artefacts to a chosen parent unit ([ADR-0043 §6](../decisions/0043-recursive-package-format.md)):

```bash
spring package install my-package                              # top-level → tenant
spring package install my-package --into engineering-team      # top-level → engineering-team
```

When `--into` is set, every top-level agent in the package becomes a member of `engineering-team`, and every top-level unit becomes a sub-unit of `engineering-team`. The package's internal structure is unchanged; only the binding of the top-level artefacts to the install scope changes.

`--into tenant` is the explicit form of the default. `--into <package>` is rejected — packages don't contain other packages' artefacts.

## Display-name override: `--as <name>`

A package that ships a single top-level activatable (one top-level agent **or** one top-level unit, with no other top-level activatables alongside) may be installed under a renamed display name:

```bash
spring package install software-engineer-pack --as backend-team
```

The package's declared `name:` becomes the artefact's stable id; `--as <name>` overrides the operator-visible display name only. The rule applies only when the package has exactly one top-level activatable — packages that ship multiple top-level artefacts have no single thing to rename, and the flag is rejected with a precise error.

This is the operator escape hatch for installing the same package multiple times in one tenant (each install picks its own display name) and for naming a tenant's instance of a generic package after the role it plays.

## What activates when a package installs

Two phases run under one `install_id` ([ADR-0035 §11](../decisions/0035-package-as-bundling-unit.md)):

1. **Phase 1** (one EF transaction): the parser walks the recursive layout, every artefact is validated, `requires:` requirements are bound, name-collision pre-flight runs, and every directory row is written with `state = 'staging'`. Any failure rolls the whole transaction back.
2. **Phase 2** (post-commit): actors activate in dependency order — parents before sub-units, units before their member agents. Activation failures leave the staging rows visible so an operator can inspect with `spring package status <install-id>` and recover with `retry` or `abort`.

Templates and the package itself do not activate. Skills are granted to their owning agent at activation time, not activated as standalone actors. Human members are resolved during Phase 1 — each `- human:` declaration mints a fresh `HumanEntity` row and a corresponding `unit_memberships_humans` row keyed by `(tenant, unit, human)` ([ADR-0046 §7](../decisions/0046-unified-members-grammar.md)).

## Skills inside the recursive layout

A **skill** is the smallest unit of reusable domain knowledge:

1. A prompt fragment (`<name>.md`) — domain knowledge, decision criteria, procedures, behavioural guidance.
2. Tool definitions (optional `<name>.tools.json`) — actions the agent can take in this domain.

A skill is a folder under `skills/` whose `package.yaml` declares `kind: Skill` and `name:`; the markdown body and the tools file sit alongside `package.yaml`. Skill ownership follows the recursive layout: a skill under `units/eng/agents/tech-lead/skills/review-prs/` is granted only to `tech-lead`; a skill under `units/eng/skills/team-standards/` is unit-scoped; a skill under the package root's `skills/` is package-scoped and available to any artefact that grants it. When a unit or agent grants multiple skills, the prompt fragments concatenate in declaration order and the tool definitions merge into the agent's tool manifest.

## Strict-parsing rejections

v0.1 has no back-compat guarantees ([issue #2406](https://github.com/cvoya-com/spring-voyage/issues/2406)); the parser fails fast on every legacy signal. The structured-error names below are stable identifiers the operator can match against in CI output. The full table is in [ADR-0043 §8](../decisions/0043-recursive-package-format.md#8-migration-hard-rename-parse-error-on-the-flat-shape-no-shim); the ADR-0046 additions are below:

| Error | Meaning |
|---|---|
| `LegacyHumansBlock` | A top-level `humans:` block on a unit / unit-template manifest. Migrate each entry under `members:` as `- human: { … }` ([ADR-0046 §1](../decisions/0046-unified-members-grammar.md)). |
| `LegacyWorkflowsSubdir` | A `workflows/` subdirectory at any depth. The shipped artefact type is removed; bind connectors via `requires:` instead ([ADR-0046 §2](../decisions/0046-unified-members-grammar.md)). |
| `LegacyConnectorsSubdir` | A `connectors/` subdirectory at any depth. Connector *bindings* survive via `requires: [ { connector: <slug> } ]` on consumer artefacts ([ADR-0037 §3](../decisions/0037-package-schema-decomposition.md)). |
| `LegacyWorkflowKind` | A `kind: Workflow` on any `package.yaml`. The enum value is gone from `ArtefactKind`. |
| `LegacyFlatArtefactLayout` | A flat file at `./agents/<name>.yaml` or `./units/<name>.yaml`. Move it to `./agents/<name>/package.yaml` ([ADR-0043 §8](../decisions/0043-recursive-package-format.md#8-migration-hard-rename-parse-error-on-the-flat-shape-no-shim)). |
| `ArtefactFolderNameMismatch` | The folder name does not equal the `name:` field. |
| `UnexpectedInnerVersion` | An inner artefact `package.yaml` declares `version:`; that slot lives only on the install-root manifest. |

Each error ships with an actionable migration hint pointing at the relevant ADR section.

## Migration from the flat layout

The pre-[ADR-0043](../decisions/0043-recursive-package-format.md) layout used flat files: `./agents/<name>.yaml`, `./units/<name>.yaml`. That shape is removed. The parser errors out (`LegacyFlatArtefactLayout`) on a file directly under `./agents/`, `./units/`, etc.; the file must move to `./agents/<name>/package.yaml`. `content:` on the package manifest is removed; trivial leaves use inline-in-`members:` or a folder with one `package.yaml`. Packages still carrying the [ADR-0044 §2](../decisions/0044-team-role-vs-platform-role.md) top-level `humans:` block migrate per the [ADR-0046](../decisions/0046-unified-members-grammar.md) table above.

## See also

- [Templates](templates.md) — type / instance separation, `from:`, and the `templates/` directory (including `HumanTemplate`).
- [Agents](agents.md), [Units](units.md) — what activates when a package installs.
- [Humans](humans.md) — the install-time resolution model and the operator-editable identity surfaces.
- [Connectors](connectors.md) — the connector-binding model `requires:` plugs into.
- [Skills](skills.md) — the prompt-fragment + tools artefact kind.
- [Declarative configuration](../guide/user/declarative.md) — walkthroughs that build packages step by step.
- [ADR-0046](../decisions/0046-unified-members-grammar.md) — unified `members:` grammar; humans as a member kind; `HumanTemplate`; vocabulary trim.
- [ADR-0043](../decisions/0043-recursive-package-format.md) — recursive package format, type / instance separation, install scope.
- [ADR-0037](../decisions/0037-package-schema-decomposition.md) — per-artefact kind-discriminated YAMLs and cross-package addressing.
- [ADR-0035](../decisions/0035-package-as-bundling-unit.md) — package as the unit of bundling and the two-phase install pipeline.
