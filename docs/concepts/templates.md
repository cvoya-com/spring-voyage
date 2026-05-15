# Templates

A **template** is an artefact folder that does not activate. It exists to be cloned. Templates separate the *type* of a unit or agent (its shared instructions, runtime, expertise, member graph) from the *instances* that activate at install time. One software-engineer template, instantiated three times, ships three concrete agents with newly minted identities — each editable in isolation but starting from one shared definition.

This page covers what templates are, when to use them, and how the `from:` operator stamps instances. The grammar lives in [ADR-0043 §5](../decisions/0043-recursive-package-format.md#5-type-and-instance-templates-are-non-activating-artefact-folders-cloned-by-from); this page explains the why.

## Why templates exist

Without templates, a package that ships three agents with the same instructions, the same runtime, and the same expertise has to ship three folders of duplicated content. The duplication drifts the moment one instance is edited and the others aren't. The recursive folder layout from [ADR-0043 §1](../decisions/0043-recursive-package-format.md#1-every-standalone-artefact-is-a-folder-rooted-at-packageyaml) makes that problem more visible, not less — every duplicated agent also duplicates its companion files (READMEs, embedded skills).

Templates resolve the duplication without introducing a parallel grammar. A template is shaped exactly like the concrete artefact it stands in for; the only delta is non-activation and the `from:` clone operator. Three engineering teams from one definition is three thin `from:` references, not three folders that have to be kept in sync by hand.

## Template kinds

| `kind:` | Stands in for | Folder lives under |
|---|---|---|
| `AgentTemplate` | `kind: Agent` | `templates/` |
| `UnitTemplate` | `kind: Unit` | `templates/` |

Both are first-class kinds in the catalog. There is no `abstract: true` flag; the kind is the discriminator. An `AgentTemplate` accepts every field its concrete counterpart accepts (`ai:`, `instructions:`, `expertise:`, `requires:`, a nested `skills/` directory). A `UnitTemplate` additionally accepts `members:` / `policies:` and ships the same recursive subdirectories a concrete `Unit` does (`agents/`, `units/`, `connectors/`, `skills/`, `templates/`).

Templates live in a `templates/` directory at any depth ([ADR-0043 §5b](../decisions/0043-recursive-package-format.md#5b-location)). A package's root may have `templates/`; a `UnitTemplate` may carry its own `templates/`; a concrete `Unit` may carry `templates/` for unit-scoped templates that don't leak into sibling units. `AgentTemplate` and `UnitTemplate` folders sit side by side; the inner `kind:` field disambiguates.

## The `from:` operator

A concrete `kind: Unit` or `kind: Agent` declares `from: <template-name>` to be stamped from a template:

```yaml
apiVersion: spring.voyage/v1
kind: Agent
name: ada
description: Ada — software engineer specialised in numerical work.
from: software-engineer
```

At install time the resolver walks the template's full folder tree and produces fresh concrete artefacts. The bare form (`from: software-engineer`) resolves within the same package. The qualified form (`from: shared-archetypes/software-engineer@1.2.0`) reaches into another installed package using the same `<pkg>/<name>@<version>` grammar as every other cross-package reference ([ADR-0043 §5h](../decisions/0043-recursive-package-format.md#5h-cross-package-addressing)).

## Stamping semantics

When a concrete instance declares `from: <template>`, the install resolver:

1. Reads the template's outer `package.yaml`.
2. Merges the consumer's outer fields per the override rules below.
3. For each artefact under the template's `agents/`, `units/`, etc., produces a fresh concrete child of the consumer instance with a newly minted Guid identity ([ADR-0036](../decisions/0036-single-identity-model.md)).
4. Recurses into the cloned children: a nested artefact inside the template that itself declares `from:` triggers another clone.

Identity is Guid, so two instances of the same template can share display names — `engineering-1`'s `team-lead` and `engineering-2`'s `team-lead` are distinct concrete agents that happen to share a display string. Disambiguation is via parent unit.

### Override rules

The outer concrete instance's body overrides the template's outer fields per [ADR-0043 §5d](../decisions/0043-recursive-package-format.md#5d-overrides):

- **Scalars** (e.g. `instructions:`) — the instance wins; the template's value flows through if the instance is absent.
- **Maps** (e.g. `ai: { runtime, model }`) — deep merge; instance keys win at each level. An instance can pin its model without restating the runtime.
- **Lists** (e.g. `expertise:`, `requires:`) — the instance's list replaces the template's entirely. Partial list edits are not expressible without restating the whole list.

`members:` is the special case. If the consumer omits `members:`, the template's stamped child tree (from the cloned `agents/` / `units/` subdirectories) is the member set. If the consumer declares `members:`, that list **replaces** the stamped tree entirely — the operator is opting out of the template's structure and declaring their own. Per-member overrides (changing one nested agent without re-listing the rest) are a follow-up; today's choice is full-clone or full-override.

## Snapshot binding

The instance binds to the resolved template body **at install time** — the cloned tree is captured into the instance's persisted definition ([ADR-0043 §5f](../decisions/0043-recursive-package-format.md#5f-snapshot-binding)). Editing the template later does not auto-propagate to already-installed instances. A "template upgrade" flow that re-resolves dependents against a new template is on the roadmap; today's installs are independent of subsequent template edits.

The consequence: an in-place edit of a template affects future installs, not existing ones. Re-installing a package against an updated template version produces a fresh install with the new shape, distinct from the previous install. This makes upgrades observable rather than silent.

## Cross-package archetype libraries

A package that ships only templates — no concrete units, no concrete agents, just a `templates/` directory at the package root — is a valid shape. Installing such a package makes its templates available in the tenant catalog; other packages then reference them by qualified name:

```yaml
# in another package's units/platform-eng/package.yaml
kind: Unit
name: platform-eng
from: archetypes/engineering-team@1.0.0
```

This is the cross-package archetype-library pattern. Cycle detection and install ordering flow through unchanged — the resolver treats `from:` edges the same as `members:` edges when walking the dependency graph ([ADR-0043 §7](../decisions/0043-recursive-package-format.md#7-cross-package-cycle-detection-extends-across-folder-boundaries)).

## Templates are optional

Nothing in the package grammar requires a package to use templates. A package that ships only concrete `kind: Unit` / `kind: Agent` folders and no `templates/` directory is fully valid ([ADR-0043 §5i](../decisions/0043-recursive-package-format.md#5i-templates-are-optional)). Replicating identical definitions is a legitimate authoring style — the operator chose simplicity over reuse.

Reach for templates when:

- You need two or more instances of the same shape in one package.
- You want to ship a reusable archetype that other packages reference cross-package.
- The shape has nested children (a unit with a team-lead and a senior-engineer) and you want one definition to drive multiple instantiations.

Skip templates when the package ships a small set of one-off artefacts and the duplication cost is zero.

## Inline form

A parent's `members:` list admits inline `from:` entries ([ADR-0043 §5g](../decisions/0043-recursive-package-format.md#5g-inline-form)) — useful for the "fan three engineers out of one template" case without giving each instance its own folder:

```yaml
members:
  - agent: { name: ada,      from: software-engineer }
  - agent: { name: hopper,   from: software-engineer, expertise: [{ domain: compilers, level: expert }] }
  - agent: { name: lovelace, from: software-engineer }
```

Folder form is for instances that grow their own companion files (READMEs, additional skills). Inline form is for the common stamp-three-from-one-template case.

## Worked example

The [`example-templated`](../guide/user/declarative.md#building-example-templated-step-by-step) package in the repo demonstrates both kinds. The layout:

```
example-templated/
├── package.yaml                              # kind: Package
├── templates/
│   ├── software-engineer/                    # kind: AgentTemplate
│   │   └── package.yaml
│   └── engineering-team/                     # kind: UnitTemplate (with stamped children)
│       ├── package.yaml
│       └── agents/
│           ├── team-lead/                    # kind: Agent — cloned into every instance
│           │   └── package.yaml
│           └── senior-engineer/              # kind: Agent — cloned into every instance
│               └── package.yaml
└── units/
    └── platform-eng/                         # kind: Unit, from: engineering-team
        ├── package.yaml
        └── agents/
            ├── ada/                          # kind: Agent, from: software-engineer
            │   └── package.yaml
            ├── hopper/                       # kind: Agent, from: software-engineer
            │   └── package.yaml
            └── lovelace/                     # kind: Agent, from: software-engineer
                └── package.yaml
```

Installing `example-templated` activates one unit and five agents:

```
platform-eng (Unit)              ← concrete, from: engineering-team
├── team-lead (Agent)            ← stamped from the template's nested agents/team-lead/
├── senior-engineer (Agent)      ← stamped from the template's nested agents/senior-engineer/
├── ada (Agent)                  ← concrete, from: software-engineer
├── hopper (Agent)               ← concrete, from: software-engineer
└── lovelace (Agent)             ← concrete, from: software-engineer
```

`platform-eng` omits `members:`, so the `engineering-team` template's stamped tree (`team-lead`, `senior-engineer`) is the inherited member set. The three concrete `from: software-engineer` agents that sit under `units/platform-eng/agents/` add to that set — they are concrete members of `platform-eng`, not part of the template's stamped tree.

Each of the three software-engineer instances overrides only `description:`. The `ai:`, `role:`, `capabilities:`, `instructions:`, and `expertise:` all flow through from the template.

See the [declarative-configuration walkthrough](../guide/user/declarative.md#building-example-templated-step-by-step) for a step-by-step build of this package.

## See also

- [Packages](packages.md) — the recursive folder layout, `kind:`, install scope.
- [Agents](agents.md), [Units](units.md) — what an `AgentTemplate` / `UnitTemplate` stands in for.
- [ADR-0043 §5](../decisions/0043-recursive-package-format.md#5-type-and-instance-templates-are-non-activating-artefact-folders-cloned-by-from) — the type / instance separation decision.
- [Declarative configuration](../guide/user/declarative.md) — step-by-step walkthroughs.
