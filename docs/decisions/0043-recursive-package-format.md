# 0043 — Recursive package format: every artefact is a folder

- **Status:** Proposed — every standalone artefact is a folder rooted at `package.yaml` with a `kind:` discriminator (`Package | Unit | Agent | Skill | Workflow | UnitTemplate | AgentTemplate`); folders compose recursively (a unit folder can contain its own `agents/`, `units/`, `connectors/`, `skills/`, `templates/`); the package-level `content:` block is removed — directory layout IS the content; auto-discovery walks the conventional directories; `requires:` drives install ordering via topo-sort; concrete instances reuse type definitions via `from: <template-name>` with override-merge semantics; install scope binds the package's top-level artefacts to the tenant or to an operator-chosen parent unit; pre-v1.0 hard rename, no shim.
- **Date:** 2026-05-15
- **Related:** [#2299](https://github.com/cvoya-com/spring-voyage/issues/2299) (this ADR), [#2298](https://github.com/cvoya-com/spring-voyage/issues/2298) (slot-alignment that adopts the shape decided here).
- **Related ADRs:** [0037](0037-package-schema-decomposition.md) — Package schema decomposition (parent — this ADR extends decisions 1 and 2); [0035](0035-package-as-bundling-unit.md) — Package as the unit of bundling; [0039](0039-units-are-agents.md) — Units are agents (forcing function for uniform folder shape).

## Context

ADR-0037 decomposed each artefact into a kind-discriminated top-level YAML, but kept the package layout flat — `agents/<name>.yaml`, `units/<name>.yaml`, one file per artefact. That solves the "fragments parsed only through `package.yaml`" problem for individual files, but leaves four structural rough edges:

1. **Asymmetric containment.** A unit's `members:` list expresses *which* artefacts belong to it, but those artefacts live in a flat sibling directory. The reader has to walk two trees in parallel — the membership graph and the filesystem layout — to understand what an artefact "owns". An agent that ships its own skills, or a unit that ships its own connectors, has no natural home for them.
2. **No extraction parity.** ADR-0037 §"cross-package reuse" promised that "extract this agent into its own package" should be cheap. Today it isn't: the agent's YAML is a flat sibling of every other agent in the package, and pulling it out requires choosing a new layout (a single-file package? a folder?). The grammar leaves the answer ambiguous.
3. **Install-scope inflexibility.** A package installs at one scope (currently tenant). Installing the same package under an existing parent unit (so its top-level units become sub-units of *that* unit, instead of tenant-level) is not expressible because the layout doesn't distinguish "top-level child of the install scope" from "nested artefact of a member".
4. **Drift between the per-kind manifest shapes.** The Unit shape (with `members:`, `connectors:`, etc.) and the Agent shape (with `instructions:`, `expertise:`) keep evolving independently. Per ADR-0039 ("units are agents"), they should converge. A uniform folder layout pushes them toward shared scaffolding rather than parallel-but-distinct file conventions.
5. **No type / instance separation.** A package that ships a *type of agent* — for example a "software engineer" archetype with shared instructions, runtime, and expertise — has no way to be instantiated multiple times under different names. Three software-engineer agents on one team today means three folders of duplicated content, with the duplication drifting the moment one instance is edited and the others aren't. The same is true for units (a "team-of-four-engineers" archetype with no way to stamp it out twice under different team names).

Issue #2299 ties (1)–(4) together by proposing a single move: every standalone artefact becomes a folder that itself looks like a sub-package, recursing to any depth. (5) compounds (1) — the recursive shape makes the duplication problem more visible, not less, because every duplicated agent now also duplicates its companion files (READMEs, embedded skills). This ADR resolves all five.

## Decision

### 1. Every standalone artefact is a folder rooted at `package.yaml`

A "standalone artefact" is any artefact that is referenced by name and shipped as a discrete unit of authoring — every Package, Unit, Agent, Skill, Workflow, AgentTemplate, and UnitTemplate that appears in the catalog. Each one is a folder; the folder's root file is always named `package.yaml` and carries the kind in a top-level `kind:` discriminator (per ADR-0037 §1):

```
spring-voyage-oss/                         # kind: Package (the install root)
├── package.yaml
├── README.md
├── units/
│   └── sv-oss-software-engineering/       # kind: Unit
│       ├── package.yaml
│       ├── README.md
│       ├── agents/
│       │   ├── dotnet-engineer/           # kind: Agent
│       │   │   ├── package.yaml
│       │   │   ├── README.md
│       │   │   └── skills/
│       │   │       └── dotnet-build/      # kind: Skill
│       │   │           ├── package.yaml
│       │   │           └── dotnet-build.md
│       │   └── web-engineer/
│       │       ├── package.yaml
│       │       └── skills/
│       │           └── …
│       └── connectors/
│           └── …
└── agents/
    └── triage-bot/                        # kind: Agent at the package root
        ├── package.yaml
        └── README.md
```

The single root-file name **`package.yaml`** is uniform across every kind. The `kind:` discriminator does the dispatch, just as it does in ADR-0037 for the flat layout. Rejected alternatives:

- Rejected: kind-named files (`agent.yaml`, `unit.yaml`, `skill.yaml`). Two file-naming conventions for the same role; "rename a folder from agent to unit" becomes a file rename + a folder rename instead of one field edit.
- Rejected: file-name-as-kind (`.agent.yaml` extension). Adds noise; `kind:` already carries the information; tooling that walks the folder for the root file can't rely on a single name.
- Rejected: leaf-as-single-YAML alongside folder-form (parser tries `<name>/package.yaml`, falls back to `<name>.yaml`). Two layouts for the same artefact double every resolver call site and the "should I use a folder?" question becomes a style debate. ADR-0037's inline-in-`members:` mechanism already covers trivial leaves without giving them their own file.

Trivial leaves (an agent with no nested skills, no expertise, nothing but instructions and runtime config) have two paths:

- **Folder form**: a folder with one `package.yaml`. Uniform with everything else; one new file in a new directory.
- **Inline form** (carried forward from ADR-0037 §"Inline artefacts"): the trivial leaf is embedded as a full document inside its parent's `members:`. No file at all.

The flat `./agents/<name>.yaml` shape from ADR-0037 is **removed**; trivial leaves use inline or folder, not flat.

### 2. The package-level `content:` block is removed; directory layout IS the content

ADR-0037 §2 carries forward a `content:` list on `package.yaml`:

```yaml
content:
  - unit: spring-voyage-oss
  - agent: triage-bot
```

This ADR drops that block. Inside a `kind: Package` folder, the package's contents are exactly the artefact folders found under the conventional subdirectories:

| Subdirectory | Kind(s) |
|---|---|
| `units/`       | `Unit` |
| `agents/`      | `Agent` |
| `skills/`      | `Skill` |
| `workflows/`   | `Workflow` |
| `connectors/`  | `Connector` (reserved per ADR-0037; v0.2+) |
| `templates/`   | `UnitTemplate`, `AgentTemplate` — see §5 |

The resolver walks these subdirectories, treats every direct child folder as one artefact whose kind is determined by both the subdirectory convention and the `kind:` field on its `package.yaml` (mismatch is a parse error per ADR-0037 §1). Non-conventional directories (`docs/`, `examples/`, `tests/`, `.git/`, etc.) are ignored.

Install ordering is determined by **`requires:` topology** (per-artefact, ADR-0037 §3). The install pipeline topo-sorts every discovered artefact across every subdirectory; ties are broken by stable alphabetical order on `name:`. Operators who need a specific install order express it by adding the corresponding `requires:` entry on the dependent artefact — declaring "B needs A" is more honest than declaring "list A before B" because it survives the artefact being reused in a different package.

Rejected alternatives:

- Rejected: keep `content:` as the source of truth and treat the directory walk as a sanity check. Two declaration sites for one fact; operators add an artefact folder, forget to list it in `content:`, file a bug. Layout-as-content removes the duplication.
- Rejected: optional `content:` for ordering overrides only. The only legitimate reason to override `requires:`-driven order is to introduce a dependency that *isn't* declared — and undeclared dependencies are the bug `requires:` exists to surface. Don't ship an escape hatch that lets that bug hide.
- Rejected: enumerate artefacts via `members:` on a synthetic root Unit. Folds Package into Unit, undermining decision 4 below.

Consequence: subset installation ("install only some of this package") is no longer expressible; the package is the install unit per ADR-0035. Ship a smaller package if needed.

### 3. Artefacts can ship their own nested children; the same conventional directories apply at every depth

A Unit folder can contain `units/` (sub-units), `agents/` (member agents), `connectors/` (unit-scoped connector bindings), `skills/` (unit-scoped skill bundles). An Agent folder can contain `skills/`. A Skill folder can contain companion files (the `.md` for its body plus auxiliary assets — fixtures, examples). The same conventional directory names apply at every depth.

A nested artefact's scope is its containing artefact:

- An agent under `units/foo/agents/bar/` is a member of unit `foo`, not of the package's top level. It activates as part of `foo`'s membership graph, not as a tenant-level agent.
- A skill under `agents/dotnet-engineer/skills/dotnet-build/` is granted to that one agent. Other agents in the package don't see it.
- A unit under `units/foo/units/baz/` is a sub-unit of `foo`.

References from inside an artefact use bare names (the existing `members: [agent: bar]` grammar). The resolver looks up `bar` by walking the containing artefact's `agents/` directory, then its parent's `agents/`, then the package root. **Lexical scoping by directory containment** — first match wins. This is identical to how programming-language imports typically work and matches the ADR-0037 `name:`-based addressing.

Cross-package references stay `<pkg>/<name>@<version>` per ADR-0037 §5. The `<name>` is the artefact's `name:` field (which must still be unique within its containing package, regardless of where it lives in the folder tree). Path-based cross-package addressing (`<pkg>/units/foo/agents/bar`) is **not** introduced — the `name:` field is the addressable id; folder location is for human navigation.

Template folders (kind `UnitTemplate` / `AgentTemplate`) compose recursively under exactly the same rules. A `UnitTemplate` folder may contain its own `agents/`, `units/`, `templates/`, `connectors/`, `skills/` — the contents look identical to those of a concrete `Unit` folder. The only difference, per §5, is that the template does not activate; `from:` clones the tree.

### 4. `kind: Package` remains a distinct root kind

A `kind: Package` folder bundles artefacts and does not activate as an actor. A `kind: Unit | Agent | Skill | Workflow` folder activates when installed. The distinction is semantic — a Package is the install unit (ADR-0035) — and survives the recursion because:

- A folder can be a Package containing Units that contain Agents (today's `spring-voyage-oss`).
- A folder can be an Agent containing only Skills (a personal-toolkit package).
- A folder can be a Unit containing other Units (a team-of-teams).

Folding `kind: Package` into "a Unit at the install root" would force every multi-artefact package to be modelled as a Unit, even when there is no meaningful "unit" semantic — for example, a package that ships three independent tenant-level agents with no orchestration relationship has no Unit to wrap them in.

The package's own metadata — `name:`, `description:`, `version:`, `readme:`, optional `execution:` defaults per #1679 — stays on `package.yaml` at the Package root. Inner artefact `package.yaml` files do **not** declare `version:` (they inherit from the containing Package). When a folder is lifted to the install root (extracted into its own package), the only addition is a top-level `version:` field.

### 5. Type and instance: templates are non-activating artefact folders cloned by `from:`

A template is a Unit or Agent folder that does not activate. Same folder shape, same fields, same recursive subdirectories as its concrete counterpart. The `kind:` discriminator (`UnitTemplate` / `AgentTemplate`) tells the resolver: "do not activate me; clone me when a concrete artefact declares `from:`."

That single conceptual delta — non-activation plus the `from:` clone operator — covers the type / instance separation without introducing parallel grammars, abstract-vs-concrete flags, or allowed-types lists.

#### 5a. Kinds

`AgentTemplate` and `UnitTemplate` are first-class kinds (per the §1 catalog). A template folder accepts every field its concrete counterpart accepts: an `AgentTemplate` can declare `ai:`, `instructions:`, `expertise:`, `requires:`, ship a `skills/` subdirectory; a `UnitTemplate` can additionally declare `members:` / `policies:` and ship `agents/`, `units/`, `connectors/`, `skills/`, `templates/` subdirectories. No new schema slots, no `abstract: true` flag — the kind is the discriminator.

#### 5b. Location

Templates live in a `templates/` directory at any depth (added to §2's conventional-directories table). The directory may hold both `UnitTemplate` and `AgentTemplate` folders side by side; the inner `kind:` field disambiguates. A package's root may have `templates/`; a `UnitTemplate` may carry its own `templates/`; a concrete `Unit` may carry `templates/` for unit-scoped templates that don't leak into sibling units.

#### 5c. `from:` clones the template's folder tree

A concrete `kind: Unit` (or `kind: Agent`) with `from: <template-name>` is resolved by walking the template's full folder tree and producing fresh concrete artefacts:

```
templates/engineering/                  # kind: UnitTemplate
├── package.yaml                        # ai, instructions, policies
└── agents/
    ├── team-lead/                      # kind: Agent (concrete child of the template)
    │   └── package.yaml
    └── senior-engineer/                # kind: Agent
        └── package.yaml

units/
├── engineering-1/                      # kind: Unit, from: engineering
│   └── package.yaml                    # clones the template tree → real team-lead + senior-engineer
└── engineering-2/                      # kind: Unit, from: engineering
    └── package.yaml                    # another clone with fresh identities
```

At install time the resolver:

1. Reads the template's outer `package.yaml`.
2. Merges the consumer's outer fields per the merge rules (scalars override, maps deep-merge, lists replace).
3. For each artefact under the template's `agents/`, `units/`, etc., produces a fresh concrete child of the consumer instance with a newly minted Guid identity (per ADR-0036).
4. Recurses into the cloned children: a nested concrete Agent inside the template that itself declares `from:` triggers another clone; a nested concrete Agent without `from:` is cloned literally.

Identity is Guid (ADR-0036), so two instances of the same template can share display names — `engineering-1`'s `team-lead` and `engineering-2`'s `team-lead` are distinct concrete agents that happen to share a display string. Disambiguation is via parent unit.

#### 5d. Overrides

The outer concrete instance's body overrides outer fields:

- Scalars (e.g. `instructions:`) — instance wins; template flows through if absent.
- Maps (e.g. `ai: { runtime, model }`) — deep-merge; instance keys win at each level.
- Lists (e.g. `expertise:`, `requires:`) — replace; the instance's list, when present, replaces the template's entirely.

`members:` is the special case. If the consumer omits `members:`, the template's stamped member tree (from the cloned `agents/` / `units/` subdirectories) is the member set. If the consumer declares `members:`, that list replaces the stamped tree entirely — the operator is opting out of the template's structure and declaring their own. **Partial per-member overrides** (changing one nested agent without re-listing the rest) are deferred to a future ADR; v0.1 is full-clone or full-override.

#### 5e. Template chaining

A template can itself declare `from:` to extend another template. The resolver walks the chain (template-of-template-of-…), with cycle detection (§7) covering the graph.

#### 5f. Snapshot binding

The instance binds to the resolved template body at install time — the cloned tree is captured into the instance's persisted definition. Editing the template later does not auto-propagate to already-installed instances. A "template upgrade" flow (re-resolve all dependents against the new template) is a future ADR.

#### 5g. Inline form

A parent's `members:` list admits inline `from:` entries, mirroring ADR-0037's inline-in-`members:` precedent:

```yaml
members:
  - agent: { name: Ada,      from: software-engineer }
  - agent: { name: Hopper,   from: software-engineer, expertise: [...] }
  - agent: { name: Lovelace, from: software-engineer }
```

Folder form is for instances that grow their own companion files (READMEs, additional skills); inline form is for the common "fan three engineers out of one template" case.

#### 5h. Cross-package addressing

`from: <pkg>/<name>@<version>` works the same as ADR-0037 §5 cross-package references. A package may ship nothing but templates and act as an archetype library; another package references those templates by qualified name. Install ordering (the library installed before the consumer), versioning, and cycle detection flow through unchanged.

#### 5i. Templates are optional

Nothing in the grammar requires a package to use templates. A package that ships only concrete `kind: Unit` / `kind: Agent` folders and no `templates/` directory is fully valid. Replicating identical definitions is a legitimate style — the operator chose simplicity over reuse. The mechanism kicks in only when an artefact declares `from:`.

#### 5j. Rejected alternatives

- Rejected: **templates list "allowed types"; concrete unit names each member**. Forces the operator to enumerate every member of every level even when the template fully defines the structure. Defeats the brevity goal of "three engineering teams just like this one."
- Rejected: **templates non-recursive**. A `UnitTemplate` that can't contain nested children can't express a hierarchical archetype in one folder — the author has to wire multiple templates together by name.
- Rejected: **`abstract: true` flag on existing `Agent` / `Unit` kinds**. Smaller schema delta, but `kind:` is no longer authoritative for activation behavior; every consumer of the manifest reads two fields to know whether an artefact activates.
- Rejected: **live binding** (instance lazily resolves to current template at every read). Spooky action when a template edit cascades through hundreds of instances unannounced. Snapshot makes upgrades explicit.
- Rejected: **per-member overrides in v0.1** (`member-overrides:` map keyed by template-local name). Real ergonomic value, but tangled enough to merit its own ADR; v0.1 ships full-clone-or-full-override.
- Rejected: **inline `parameters:` on templates with `${var}` substitution**. Powerful but verbose; the override-merge model already covers the common cases. Revisit if a parameter pattern emerges that the override model can't express.

### 6. Install scope binds top-level artefacts to the tenant or to a chosen parent unit

`spring package install <pkg>` installs at the tenant scope by default — the package's top-level artefacts under `agents/` and `units/` become tenant-level entities. A new flag `--into <unit-ref>` binds them to a chosen parent unit instead:

```bash
spring package install my-package                            # top-level artefacts → tenant
spring package install my-package --into engineering-team    # top-level artefacts → engineering-team unit
```

When `--into` is set, every top-level agent in the package becomes a member of `engineering-team`, and every top-level unit in the package becomes a sub-unit of `engineering-team`. The package's internal structure (a top-level unit's own member graph, an agent's own skills) is unaffected — only the binding of top-level artefacts to the install scope changes.

This is enabled by the recursive shape: "top-level" is now well-defined as "an artefact folder under the conventional directories at the Package root", and "nested" is "anywhere deeper". The install pipeline knows which artefacts are scope-bound and which are owned by their containing artefact.

`--into tenant` is the explicit form of the default. `--into <package>` is rejected (packages don't contain other packages' artefacts).

### 7. Cross-package cycle detection extends across folder boundaries

Cycle detection per ADR-0037 §4 walks the resolved graph: every artefact node is `(package, kind, name, version)`; every edge is a `members:` entry, a `requires:` entry, a **`from:` reference** (§5), or a future kind-discriminated reference. The recursive folder shape doesn't change the algorithm — the catalog still indexes artefacts by `name` within a package, regardless of where the folder sits in the tree.

A new edge type emerges: containment. An artefact at `units/foo/agents/bar/` has an implicit containment edge from `foo` to `bar`. Containment edges are walked by the cycle detector for completeness, but in practice the membership relationship is already expressed via `members:` on the parent Unit, so containment edges are typically redundant. The detector still includes them so that a future shape (a unit that lists `agents/X` as a member but ships `agents/X` outside the unit folder, or vice versa) is caught as a consistency error.

`from:` edges introduce a second extension: a chain of `Unit from: UnitTemplate from: UnitTemplate from: …` must terminate. Cycles in the `from:` graph (template A extends template B which extends A) are rejected with the offending path.

### 8. Migration: hard rename, parse error on the flat shape, no shim

> **Partially superseded by [#2406](https://github.com/cvoya-com/spring-voyage/issues/2406).** The field-level legacy hints (`LegacyContentField`, `LegacyAiPromptField`) were retired in v0.1's cleanup pass — strict YAML parsing on the typed manifest classes catches an unknown `content:` or `ai.prompt:` field with a generic but actionable parse error. The structural signals the catalog walker raises (`LegacyFlatArtefactLayout`, `UnexpectedInnerVersion`, `ArtefactFolderNameMismatch`) survive because filesystem-layout problems cannot be caught by YAML strictness. The table below is preserved as historical context.

ADR-0035 §1 and ADR-0037 §6 set the precedent. The catalog and parsers accept the recursive shape only. The flat shape from ADR-0037 (`./agents/<name>.yaml`, `./units/<name>.yaml`) becomes a parse error:

| Old shape | Error | Migration hint |
|---|---|---|
| File directly under `./agents/`, `./units/`, etc. (not a folder) | `LegacyFlatArtefactLayout` | "artefact must be a folder rooted at `package.yaml`; move `./agents/foo.yaml` to `./agents/foo/package.yaml`" |
| `package.yaml` has a `content:` block | `LegacyContentField` | "content: is removed in ADR-0043; the directory layout under agents/, units/, skills/, workflows/ is the content" |
| Inner artefact `package.yaml` declares `version:` | `UnexpectedInnerVersion` | "version: lives only on the install-root package.yaml; inner artefacts inherit from the container" |
| Folder name disagrees with `name:` field | `ArtefactFolderNameMismatch` | "the folder name `foo/` must equal the name: field of its package.yaml; rename one to match" |

The `kind: Package` distinction (decision 4) is preserved; folders rooted in `agents/`, `units/`, etc. that omit `kind:` continue to error per ADR-0037 §6 (`MissingKind`).

In-repo packages migrated in the same PR: `packages/spring-voyage-oss/` and any other catalogued package. Test fixtures migrate alongside. No transitional flag.

## Consequences

**Easier:**

- "Extract this agent into its own package" is `mv units/foo/agents/bar agents/bar` (or `mv` to a sibling repo) plus adding `version:` at the new install root. No grammar change; no re-layout of internal structure.
- An agent that ships its own skills, or a unit that ships its own connectors, has a single natural home. The membership graph and the filesystem layout converge.
- The package-level manifest collapses to pure metadata (`name`, `description`, `version`, `readme`, optional `execution:` defaults). No `content:` to keep in sync with the directory.
- Install scope is operator-controlled (`--into <unit>`), not baked into the package. The same package ships agents to the tenant in one install and to a unit in another.
- Per-kind manifest schemas can converge — the unit-vs-agent slot-name alignment in #2298 can adopt one shared base because both kinds now live in the same shape of folder.
- Type / instance separation lands without a new grammar. A reusable archetype (a "software engineer", an "engineering team", an "engineering org" hierarchy) is just an artefact folder with `kind:` set to a template variant. Three engineering teams from one definition is three thin `from:` references, not three folders of duplicated content. Cross-package archetype libraries are a natural shape — a package that ships only `templates/` works the same way as one that ships activated entities.

**Harder:**

- The OSS dogfooding package and every test fixture move to the new layout in lockstep. Pre-v1.0 budget; ADR-0037 precedent; no shim.
- The catalog walker now descends into nested directories rather than reading a flat sibling list. Cycle detection has a containment-edge dimension to model. Mostly localised to `FileSystemPackageCatalogService` and `CrossPackageCycleDetector`.
- A package author who omits a `requires:` edge will get the wrong install order in some cases. The flat shape's implicit "I'll just list things in the order I want" escape hatch is gone — declared dependencies are now load-bearing for ordering.

**Not abstracted:**

- Path-based cross-package addressing (`<pkg>/units/foo/agents/bar`). `<pkg>/<name>@<version>` per ADR-0037 stays the canonical addressing form; the folder layout is for human navigation, not for the wire grammar.
- Subset installation. The package remains the install unit per ADR-0035; if a subset is needed, ship it as its own package.
- In-repo per-version directory layout (`packages/<name>/<version>/`). Deferred per ADR-0037 §5; the recursive shape is orthogonal.
- `kind: Connector` activation. ADR-0037 reserved the kind for v0.2+; the `connectors/` directory is now defined here, but its activation semantics ship with the connector ADR.
- Inline-vs-folder for trivial leaves beyond the existing inline-in-`members:` shape. The "single file under `./agents/`" form is removed; leaves go inline or get a folder.
