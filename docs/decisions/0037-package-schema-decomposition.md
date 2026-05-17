# 0037 — Package schema decomposition: per-artefact YAMLs as kind-discriminated documents

- **Status:** Accepted — 2026-05-05 — every YAML in the package schema becomes a kind-discriminated top-level document (`kind: Package | Unit | Agent | Skill | Workflow`); each artefact YAML carries its own `requires:` block; `package.yaml` loses `inputs:`, `connectors:`, and the `metadata:` nesting; cross-package references gain explicit version pinning (`<pkg>/<name>@<version>`) with default-latest semantics; cycle detection extends across package boundaries; pre-v1.0 hard rename, no shim. **Superseded in part by [ADR-0043](0043-recursive-package-format.md):** decisions 1 and 2 are extended — the flat per-artefact YAML layout (`./agents/<name>.yaml`, `./units/<name>.yaml`) is replaced by the recursive folder layout where every artefact is a folder rooted at `package.yaml`; the conventional subdirectories now apply at every depth so nested artefacts (an agent's skills, a unit's member agents) live inside their owning artefact's folder; the `content:` block on `package.yaml` is removed — directory layout *is* the content. The `kind:` discriminator gains `UnitTemplate` and `AgentTemplate` for the type / instance separation introduced in ADR-0043 §5. The kind-discriminated document grammar, the per-artefact `requires:` block, and the `<pkg>/<name>@<version>` cross-package addressing carry forward unchanged.
- **Date:** 2026-05-05
- **Closes:** [#1722](https://github.com/cvoya-com/spring-voyage/issues/1722) and (folded in) [#1720](https://github.com/cvoya-com/spring-voyage/issues/1720).
- **Umbrella:** [#1554](https://github.com/cvoya-com/spring-voyage/issues/1554) — Package as the unit of bundling, installation, and export — v0.1 collapse.
- **Related code:** `src/Cvoya.Spring.Manifest/PackageManifest.cs`, `src/Cvoya.Spring.Manifest/UnitManifest.cs`, `src/Cvoya.Spring.Manifest/AgentManifest.cs` *(new)*, `src/Cvoya.Spring.Manifest/SkillManifest.cs` *(new)*, `src/Cvoya.Spring.Manifest/WorkflowManifest.cs` *(new)*, `src/Cvoya.Spring.Manifest/RequirementEntry.cs` *(new)*, `src/Cvoya.Spring.Manifest/CrossPackageCycleDetector.cs` *(new)*, `src/Cvoya.Spring.Manifest/PackageManifestParser.cs`, `src/Cvoya.Spring.Manifest/ManifestParser.cs`, `src/Cvoya.Spring.Manifest/InlineArtefactDefinition.cs`, `src/Cvoya.Spring.Manifest/Validation/PackageValidator.cs`, `src/Cvoya.Spring.Host.Api/Services/PackageInstallService.cs`, `src/Cvoya.Spring.Host.Api/Services/PackageExportService.cs`, `src/Cvoya.Spring.Host.Api/Services/FileSystemPackageCatalogService.cs`, `packages/{research,product-management,software-engineering,spring-voyage-oss}/`.
- **Related ADRs:** [0035](0035-package-as-bundling-unit.md) — Package as the unit of bundling (parent — extends decisions 2 + 3); [0034](0034-oss-dogfooding-unit.md) — OSS dogfooding unit (forcing function, again); [0017](0017-unit-is-an-agent-composite.md) — Unit is an agent composite.

## Context

PR #1719 reshaped `package.yaml` at the top level: `kind:` was dropped (any package can bundle any mix of kinds), the flat artefact lists (`unit:` / `subUnits:` / `agents:` / `skills:` / `workflows:`) were unified into a single `content:` list of kind-discriminated entries, and a declarative `connectors:` block landed alongside them with an inheritance matrix that lets the package declare connector dependencies once and have member units pick them up.

What PR #1719 did not finish is the per-artefact side of the same decomposition. Today's `packages/spring-voyage-oss/units/sv-oss-software-engineering.yaml` starts with a bare `unit:` key and the unit body nested below it; today's `packages/spring-voyage-oss/agents/architect.yaml` does the same with `agent:`. Both files are *fragments* — they only parse correctly when the parser walks them from `package.yaml`. There is no `apiVersion`, no `kind:` discriminator, no metadata header. Loading one in isolation, referencing one cross-package independently of its parent package, or evolving its schema without breaking the package walker is impossible.

Three downstream pressures push this to a head:

1. **Cross-package reuse.** ADR-0035 §3 promised a uniform `<pkg>/<name>` reference grammar across artefact types. The grammar resolves through `package.yaml`'s flat lists today; with `content:` it resolves through one entry per artefact, and with full decomposition it resolves directly to a stand-alone artefact YAML.
2. **Open-ended requirement types.** The `connectors:` block PR #1719 added at package level is connector-specific. It is going to grow to `secret:` / `capability:` / future connector types. Renaming to `requires:` and generalising the entry shape now avoids a second migration.
3. **Versioning.** ADR-0035's known-risk on cross-package versioning ("references always resolve to currently-installed version") was deferred. With multiple packages in the OSS dogfooding org and in-flight extensibility work for connectors and orchestrators, versioned addressing is becoming load-bearing.

Issue #1722 raised the four open design questions (artefact file location, addressing, requirement placement, kind discriminator) and folded #1720's `connectors:` → `requires:` rename in. This ADR resolves all of them.

## Decision

### 1. Per-artefact YAMLs are kind-discriminated top-level documents

Every artefact lives in its own file with its own document root and no wrapping key:

```yaml
# packages/spring-voyage-oss/units/spring-voyage-oss.yaml
apiVersion: spring.voyage/v1
kind: Unit
name: Spring Voyage OSS
description: The umbrella unit for the Spring Voyage OSS dogfooding org.
readme: spring-voyage-oss.md      # optional; long-form prose for UIs
ai: { ... }
members: [ ... ]
execution: { ... }
requires:                          # this artefact's own requirements
  - connector: github
boundary: { ... }
expertise: [ ... ]
orchestration: { ... }
```

```yaml
# packages/spring-voyage-oss/agents/architect.yaml
apiVersion: spring.voyage/v1
kind: Agent
name: Architect
description: Platform architect — owns ADRs, boundary design, contract review.
readme: architect.md               # optional
role: architect
capabilities: [architecture, adr-authoring, boundary-design, contract-review]
ai: { ... }
instructions: |
  ...
requires:
  - connector: github
```

Kinds for v0.1: `Unit`, `Agent`, `Skill`, `Workflow`. `Orchestrator` and `Connector` are reserved.

`apiVersion: spring.voyage/v1` on every kind-discriminated document. The wrapping `unit:` / `agent:` keys are gone. `name:` and `description:` move to the top of the document; the previously-nested `metadata:` block is flattened (decision 2 below applies the same flattening to `package.yaml`). `description:` is a required single-line summary; long-form prose moves to a markdown file referenced via the optional `readme:` slot (resolved relative to the YAML).

The `kind:` field is the parser's dispatch key. Directory placement (`./units/`, `./agents/`, …) remains **convention, not contract** — the parser dispatches on `kind:`. Mismatch between path and `kind:` is a parse error with a precise location, surfacing the migration in one error rather than as silent breakage.

`structure:` is **dropped** from the Unit shape. Today's only value is `hierarchical`, and the hierarchy is already expressed by the recursive `members:` graph — the property restates information the structure already encodes. Detection of an old-shape `structure:` field surfaces a parse error per decision 6.

Rejected: keep the wrapping key (`unit:` / `agent:`) and add headers above it. Two ways to root the document; the wrapping key tells the reader nothing the `kind:` field doesn't.
Rejected: keep `structure:` for non-hierarchical future shapes (mesh, peer-to-peer, …). No such shape exists or is on the v0.1 horizon; reintroduce when there's a second concrete value to discriminate.
Rejected: file-extension dispatch (`.unit.yaml`). Adds noise without payload — `kind:` already carries the information.
Rejected: directory-only dispatch (any YAML under `./units/` is a Unit). Couples grammar to layout; obstructs cross-package URL-based addressing if it ever lands.

### 2. `package.yaml` is the container manifest only — flatten `metadata:`, drop `inputs:` and `connectors:`, add optional `readme:`

```yaml
# packages/spring-voyage-oss/package.yaml
apiVersion: spring.voyage/v1
kind: Package
name: spring-voyage-oss
description: The Spring Voyage OSS dogfooding organisation.
readme: README.md                  # optional; relative path to a markdown file
version: 1.0.0                     # see decision 5
content:
  - unit: spring-voyage-oss        # bare → ./units/spring-voyage-oss.yaml
```

`metadata:` nesting is removed; `name:` and `description:` are top-level for symmetry with per-artefact YAMLs. Every kind-discriminated YAML in the schema (this `package.yaml` and every per-artefact YAML from decision 1) carries the same mandatory `apiVersion` / `kind` / `name` / `description` set plus an optional `readme:` slot.

`kind: Package` is **present** on `package.yaml` (in contrast to PR #1719's "no `kind:`" decision). With every other YAML in the schema kind-discriminated, uniformity beats the small saving of dropping the key — the historical concern was the leaky `UnitPackage` / `AgentPackage` discriminator, which is moot once the value collapses to a single `Package`. The post-resolve `PackageKind` enum used internally by the install pipeline is unaffected: it is still computed from `Content` at resolve time and is never present on the YAML side.

`inputs:` is **removed** from `package.yaml`. Today's only uses are connector-binding parameters (`github_owner`, `github_repo`, `github_installation_id` on `packages/{product-management,software-engineering}/package.yaml`); those move into the per-artefact `requires:` block as the requirement's binding configuration (decision 3). Behaviour parameters that don't configure a connector (e.g. `max_actions_per_hour`) already live on per-unit `policies:` and stay there.

`connectors:` is **removed** from `package.yaml`. The package-level connector dependency declaration introduced in PR #1719 (with `RequiredConnector { type, required, inherit }` and the `inherit:` matrix) is replaced by per-artefact `requires:`. Each member unit / agent declares what it needs; the install pipeline asks the operator for one binding per connector type at install time and applies that binding to every artefact that declared the requirement. The "inheritance" matrix (`inherit: all` / `inherit: [unit-a, unit-b]` / `inherit: false`) is no longer expressible because it is no longer needed: artefacts that don't declare the requirement don't get the binding; artefacts that do, do.

`content:` carries forward unchanged in shape — bare or qualified `<kind>: <name>` references, with bare names resolving to `./<kind>s/<name>.yaml` (skills: `.md`).

Rejected: keep `metadata:` nesting on both `package.yaml` and per-artefact YAMLs. Internal symmetry without operator value; flatter wins on every read.
Rejected: keep `inputs:` for non-connector behaviour parameters. The only existing uses are connector-related; the abstraction has not earned its weight. If behaviour parameters that don't fit `requires:` ever surface, a follow-up ADR introduces a `parameters:` block on per-artefact YAMLs.
Rejected: drop `kind:` on `package.yaml` per PR #1719's logic. Uniform kind-discrimination across every YAML in the schema is more important than the original "leaky discriminator" critique now that the value is a single `Package`.
Rejected: keep `connectors:` at package level and per-artefact, with merge rules. Two declaration sites force operators to learn precedence; inheritance is recoverable from per-artefact declarations alone.

### 3. Requirements live on per-artefact YAMLs as `requires:`; package effective requirements = union

Each artefact YAML carries its own `requires:` block — what that artefact needs to function. Today: connectors only. Tomorrow: secrets, capabilities, future connector types, all expressible under the same shape:

```yaml
requires:
  - connector: github
  - connector: slack
```

Each entry is a single-key map; the key is the requirement type (`connector:`, future: `secret:`, `capability:`); the value is the type-specific binding identifier (the connector slug for the v0.1 connector entry). The discriminator-as-key shape matches the `content:` entry shape from PR #1719 — same parser pattern, same operator mental model.

The package's effective requirement set at install time = union of every contained artefact's `requires:`, deduplicated by `(type, identifier)`. The install pipeline asks the operator for one binding per unique entry; the binding is then injected into every artefact that declared it. No inheritance matrix, no `inherit:` slot, no opt-out keyword — declarations and bindings are 1:1.

Rejected: package-level `requires:` only. Defeats the reuse story — a `unit.yaml` referenced cross-package would not carry its own requirements across the boundary.
Rejected: package-level + per-artefact with merge semantics. Two sites; non-trivial precedence; #1722 explicitly flagged this as a placement question — the simplest answer is "one site."
Rejected: keep the name `connectors:`. About to grow beyond connectors; doing the rename in this ADR avoids a second migration when the second requirement type lands.

**Follow-up filed for v0.2.** A v0.2 issue tracks evolving `requires:` entries to use the same versioned addressing as cross-package artefact references (decision 5):

```yaml
requires:
  - connector: github@2.0
  - connector: spring-voyage/github@1.0
  - orchestrator: routing-agents/label-router@1.4
  - secret: secrets-pkg/openai-key
```

This presupposes that connectors (including platform defaults like `github`), orchestrators, and any future extension point become installable as packages, with platform-default ones simply being packages installed at platform bootstrap time. Once that lands, every requirement points at a versioned package artefact via the same `<pkg>/<name>@<version>` grammar — there is no special-cased "platform default connector" identity. ADR-0037 leaves the requirement-entry value as an opaque slug for v0.1 and the v0.2 follow-up generalises it.

### 4. Cross-package cycle detection across the resolved graph

Today's parser detects cycles within a package via `LocalSymbolMap`. After decisions 1+2 a per-artefact YAML in package A may reference an artefact in package B which references back into A — a cycle that no existing check catches.

The resolver extends cycle detection to the post-resolve graph: every node is a `(package, kind, name, version)` tuple; every edge is a `content:` entry, a `members:` entry on a Unit, or any other manifest field that holds a kind-discriminated reference. Detection runs after cross-package references are resolved (so the catalog has been consulted) and aborts with the offending path including package boundaries.

Cycle detection respects decision 5's versioning: a reference to `pkg-b/agent-x@1.0.0` and a reference to `pkg-b/agent-x@2.0.0` are different nodes and do not collide. If two installed versions of the same artefact transitively reference each other, that's a cycle and is rejected.

### 5. Versioning: package-level `version:`, `<pkg>/<name>@<version>` cross-package addressing, default-latest

Each `package.yaml` declares a `version:` scalar — opaque string for v0.1. No semver parsing yet; a future ADR may add semver-aware ordering once packages adopt semver consistently. Two packages with the same `name` and different `version:` values may be installed in the same tenant simultaneously; each install gets a distinct install id.

Cross-package references use the form `<pkg>/<name>@<version>`:

```yaml
content:
  - unit: shared-platform/router@1.2.0
members:
  - agent: shared-tools/code-reviewer@2025.10
```

When `@<version>` is omitted (`shared-tools/code-reviewer`), the resolver picks the **most recently installed** version of the package for the tenant — chronological by install timestamp on the `installs` row. This is deterministic and requires no semver awareness. A future ADR can introduce semver-aware "latest" if and when packages adopt semver consistently.

If the requested version is not installed at resolve time, the install pipeline returns 400 with `{ error: "PackageVersionUnavailable", pkg, version, requested-by }`. There is no automatic dependency installation in v0.1 — operators install dependencies first, top-level packages second.

The catalog is keyed by `(name, version)` rather than `(name)` alone. `FileSystemPackageCatalogService` treats every directory under `packages/` as one `(name, version)` entry; for v0.1 each repo-shipped package has a single in-repo version, but installed packages can carry multiple. A future ADR may introduce a sub-directory layout (`packages/<name>/<version>/`) when in-repo versioning becomes useful — out of scope here.

ADR-0035's known-risk on cross-package versioning ("references always resolve to currently-installed version") is **resolved** by this decision: explicit `@<version>` pins; default-latest is well-defined.

Rejected: reject default-latest (require every cross-package ref to pin). Adds friction for the OSS dogfooding story where there is exactly one version of every package today.
Rejected: most-recently-installed by version-string compare instead of install time. String-compare on opaque versions ("1.10.0" vs "1.9.0") gives wrong answers; install-time is unambiguous.
Rejected: in-repo `packages/<name>/<version>/` layout right now. Existing four packages would re-shuffle for no operator benefit; defer until a second version actually exists.

### 6. Migration: hard rename, parse error on old shape, no shim

> **Superseded by [#2406](https://github.com/cvoya-com/spring-voyage/issues/2406).** The per-shape "graceful migration-hint" rejection branches (every row in the migration table below) were retired in v0.1's cleanup pass. Strict YAML parsing on the typed manifest classes now rejects unknown fields with a generic but actionable error — no per-field migration hint. The structural signals the catalog walker still raises (`LegacyFlatArtefactLayout`, `UnexpectedInnerVersion`, `ArtefactFolderNameMismatch`) survive because filesystem-layout problems cannot be caught by YAML strictness. The table below is preserved as historical context.

ADR-0035 §1 set the precedent: pre-v1.0, hard renames are bounded and cheaper than carrying parallel grammars. `PackageManifestParser` accepts the new shape only. Every old-shape signal is a parse error with a precise migration hint:

| Old shape | Error | Migration hint |
|---|---|---|
| `package.yaml` has `inputs:` | `LegacyInputsField` | "inputs: is removed in ADR-0037; move connector-binding parameters into per-artefact requires:" |
| `package.yaml` has `connectors:` | `LegacyPackageConnectorsField` | "package-level connectors: is removed in ADR-0037; declare requirements on per-artefact YAMLs as requires:" |
| `package.yaml` has `metadata:` block | `LegacyMetadataNesting` | "metadata: nesting is removed; hoist name and description to top level" |
| `package.yaml` has `kind:` ≠ `Package` | `LegacyPackageKind` | "kind: must be Package in ADR-0037" |
| Per-artefact YAML wrapped in `unit:` / `agent:` | `LegacyArtefactWrapper` | "drop the wrapping unit:/agent: key; hoist body to top level with apiVersion/kind/name/description" |
| Per-artefact YAML missing `apiVersion` | `MissingApiVersion` | "every artefact YAML declares apiVersion: spring.voyage/v1" |
| Per-artefact YAML missing `kind` | `MissingKind` | "every artefact YAML declares kind: Unit/Agent/Skill/Workflow" |
| Unit YAML has `connectors:` | `LegacyUnitConnectorsField` | "unit-level connectors: is renamed to requires: in ADR-0037" |
| Unit YAML has `structure:` | `LegacyStructureField` | "structure: is removed in ADR-0037; the membership graph already encodes it" |
| `package.yaml` missing `version:` | `MissingPackageVersion` | "every package declares a version: scalar (decision 5)" |

The four in-repo packages and five test fixtures are migrated in the same PR. There is no transitional flag, no per-shape detection branch, no shim verb.

## Consequences

**Easier:**

- Per-artefact YAMLs become first-class — they can be loaded in isolation, schema-validated, edited without the package walker, and (post-v0.2) referenced cross-package without their parent.
- A new requirement type is one converter case in `RequirementEntry`, not a new top-level field on `package.yaml`.
- Cross-package cycle detection finally has a graph to walk; the previous "best effort" is replaced by a concrete check at the resolve boundary.
- The wizard's "scratch" path (ADR-0035 §6) collapses onto the same kind-discriminated grammar as everything else — inline artefacts are full documents, not unwrapped fragments.

**Harder:**

- Every authored YAML in the repo plus every fixture moves to the new shape in lockstep. Pre-v1.0 budget; ADR-0035 §1 precedent; no shim cost amortised over time.
- Catalog and install pipeline gain a `(name, version)` key; queries that used to assume "one current version" now disambiguate. Mostly internal; CLI and portal get one new flag (`--version`) and one new column.

**Not abstracted:**

- Semver-aware "latest" — explicitly deferred. Install timestamp is the v0.1 ordering.
- External (HTTPS / Git) artefact addressing — explicitly out of scope per #1722.
- Per-version directory layout. Defer until a second in-repo version exists.
- New requirement types beyond `connector:` (`secret:`, `capability:`). Schema is forward-compatible; convert when the second type concretises.

**Cost / gain:** the migration cost is concentrated in this PR; the gain compounds over every future artefact-shape change because the schema is now per-kind, kind-discriminated, and uniform in metadata position.
