# Packages

Spring Voyage is domain-agnostic by design. The platform provides primitives — agents, units, messaging, orchestration. **Packages** provide the actors and the domain knowledge that drives them.

## What a package is

A **package** is the unit of bundling, installation, and export. One package is what `spring package install` installs in one operator action ([ADR-0035](../decisions/0035-package-as-bundling-unit.md)). A package can ship agents, units, skills, workflows, connector bindings, and templates — any mix of them.

The catalog is global; installations are tenant-scoped. Two tenants installing the same package get independent actor sets.

## The recursive folder layout

Every standalone artefact in a package — the package itself, every unit, every agent, every skill, every workflow, every template — is a **folder** whose root file is always named `package.yaml` ([ADR-0043](../decisions/0043-recursive-package-format.md) §1). The `package.yaml` file carries a `kind:` discriminator that tells the parser what the folder is:

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
│       └── connectors/
│           └── …
├── agents/
│   └── triage-bot/                       # kind: Agent at the package root
│       ├── package.yaml
│       └── README.md
├── skills/
│   └── …                                 # package-scoped skills
├── workflows/
│   └── …
└── templates/
    ├── software-engineer/                # kind: AgentTemplate
    │   └── package.yaml
    └── engineering-team/                 # kind: UnitTemplate
        ├── package.yaml
        └── agents/
            └── …
```

The conventional subdirectories at every depth are `units/`, `agents/`, `skills/`, `workflows/`, `connectors/`, and `templates/`. Any folder that activates as an actor can itself contain these subdirectories — an agent folder can ship its own `skills/`, a unit folder can ship its own `agents/`, `units/`, `connectors/`, and a template folder mirrors the same shape. Recursion bottoms out at skills and at trivial agents that ship no nested children.

Non-conventional directories (`docs/`, `examples/`, `tests/`, `.git/`) are ignored by the resolver. The folder layout *is* the manifest: there is no `content:` block on `package.yaml` enumerating what the package contains ([ADR-0043](../decisions/0043-recursive-package-format.md) §2). The catalog walker discovers artefacts by walking the conventional subdirectories.

## The seven kinds

| `kind:` value | Activates? | Folder lives under | Purpose |
|---|---|---|---|
| `Package` | No | Install root | Bundles artefacts; carries the package's `name`, `description`, `version`, `readme` |
| `Unit` | Yes | `units/` | Composite agent that owns children ([Units](units.md)) |
| `Agent` | Yes | `agents/` | Leaf agent with a mailbox and an execution config ([Agents](agents.md)) |
| `Skill` | Yes (granted, not activated) | `skills/` | A prompt fragment plus optional tool definitions |
| `Workflow` | Yes | `workflows/` | Containerised structured-orchestration unit ([ADR-0019](../decisions/0019-workflow-as-container.md)) |
| `UnitTemplate` | No | `templates/` | Archetype cloned by `from:` on a concrete `Unit` ([Templates](templates.md)) |
| `AgentTemplate` | No | `templates/` | Archetype cloned by `from:` on a concrete `Agent` |

`Package` and the two `*Template` kinds do not activate. `Package` is the install root; templates exist to be stamped. Everything else activates when installed.

`kind: Connector` is reserved for v0.2+ ([ADR-0037](../decisions/0037-package-schema-decomposition.md) §1); the `connectors/` directory is defined today but its activation semantics ship with the connector ADR.

## Names are package-scoped, folders mirror them

Every artefact carries a `name:` field on its `package.yaml`. The name must be **unique within its containing package**, regardless of where in the folder tree the artefact lives. Two agents named `architect` cannot coexist in the same package even if one sits under `units/eng/agents/architect/` and the other under `agents/architect/`.

The folder name must equal the `name:` field. `units/foo/` must contain a `package.yaml` whose `name:` is `foo`. Disagreement is a parse error (`ArtefactFolderNameMismatch`). This rule keeps the membership graph and the filesystem trivially scannable.

References from inside an artefact use bare names (`members: [agent: bar]`). The resolver looks up `bar` by walking the containing artefact's `agents/`, then its parent's `agents/`, then the package root — **lexical scoping by directory containment**, first match wins.

## `requires:` and install ordering

Every artefact's `package.yaml` may declare a `requires:` block listing what that artefact needs to function ([ADR-0037](../decisions/0037-package-schema-decomposition.md) §3):

```yaml
requires:
  - connector: github
  - connector: slack
```

The package's effective requirement set at install time is the union of every contained artefact's `requires:`, deduplicated by requirement key. The install pipeline asks the operator for one binding per unique requirement and injects it into every artefact that declared it.

`requires:` also drives **install ordering**: the pipeline topo-sorts every discovered artefact across every subdirectory; ties are broken by stable alphabetical order on `name:`. There is no `content:` list to express ordering — operators express "B needs A" by adding the `requires:` entry, not by listing A before B in some manifest.

## Cross-package references

Every artefact reference can be qualified to reach into another installed package ([ADR-0037](../decisions/0037-package-schema-decomposition.md) §5):

```yaml
members:
  - agent: shared-tools/code-reviewer@2.1.0
content: # historical example — actual cross-pkg refs appear in members:, requires:, from:
  - unit: shared-platform/router@1.2.0
```

The grammar is `<package>/<name>@<version>`. When `@<version>` is omitted, the resolver picks the most recently installed version of the package for the tenant. The qualifier resolves the same way for every artefact kind — units, agents, workflows, skills, templates.

`name:` is the addressable id. Folder location is for human navigation; path-based addressing (`<pkg>/units/foo/agents/bar`) is **not** a wire form.

## Install scope: tenant by default, `--into` to bind to a parent unit

`spring package install <pkg>` installs at the **tenant scope**. Every top-level agent in the package becomes a tenant-level agent; every top-level unit becomes a tenant-level unit. Nested artefacts (an agent inside a unit folder, a skill inside an agent folder) are unaffected by install scope — they are owned by their containing artefact.

`--into <unit>` rebinds the top-level artefacts to a chosen parent unit ([ADR-0043](../decisions/0043-recursive-package-format.md) §6):

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

Two phases run under one `install_id` ([ADR-0035](../decisions/0035-package-as-bundling-unit.md) §11):

1. **Phase 1** (one EF transaction): the parser walks the recursive layout, every artefact is validated, `requires:` requirements are bound, name-collision pre-flight runs, and every directory row is written with `state = 'staging'`. Any failure rolls the whole transaction back.
2. **Phase 2** (post-commit): actors activate in dependency order — parents before sub-units, units before their member agents. Activation failures leave the staging rows visible so an operator can inspect with `spring package status <install-id>` and recover with `retry` or `abort`.

Templates and the package itself do not activate. Skills are granted to their owning agent at activation time, not activated as standalone actors.

## Skills inside the recursive layout

A **skill** is the smallest unit of reusable domain knowledge:

1. A prompt fragment (`<name>.md`) — domain knowledge, decision criteria, procedures, behavioural guidance.
2. Tool definitions (optional `<name>.tools.json`) — actions the agent can take in this domain.

A skill is a folder under `skills/` whose `package.yaml` declares `kind: Skill` and `name:`; the markdown body and the tools file sit alongside `package.yaml`. Skill ownership follows the recursive layout: a skill under `units/eng/agents/tech-lead/skills/review-prs/` is granted only to `tech-lead`; a skill under `units/eng/skills/team-standards/` is unit-scoped; a skill under the package root's `skills/` is package-scoped and available to any artefact that grants it. When a unit or agent grants multiple skills, the prompt fragments concatenate in declaration order and the tool definitions merge into the agent's tool manifest.

## Migration from the flat layout

The pre-[ADR-0043](../decisions/0043-recursive-package-format.md) layout used flat files: `./agents/<name>.yaml`, `./units/<name>.yaml`. That shape is removed. The parser errors out (`LegacyFlatArtefactLayout`) on a file directly under `./agents/`, `./units/`, etc.; the file must move to `./agents/<name>/package.yaml`. The full migration error table is in [ADR-0043 §8](../decisions/0043-recursive-package-format.md#8-migration-hard-rename-parse-error-on-the-flat-shape-no-shim). `content:` on the package manifest is removed; trivial leaves use inline-in-`members:` or a folder with one `package.yaml`.

## See also

- [Templates](templates.md) — type / instance separation, `from:`, and the `templates/` directory.
- [Agents](agents.md), [Units](units.md) — what activates when a package installs.
- [Declarative configuration](../guide/user/declarative.md) — walkthroughs that build packages step by step.
- [ADR-0043](../decisions/0043-recursive-package-format.md) — recursive package format, type / instance separation, install scope.
- [ADR-0037](../decisions/0037-package-schema-decomposition.md) — per-artefact kind-discriminated YAMLs and cross-package addressing.
- [ADR-0035](../decisions/0035-package-as-bundling-unit.md) — package as the unit of bundling and the two-phase install pipeline.
