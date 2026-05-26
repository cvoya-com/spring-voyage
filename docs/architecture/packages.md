# Packages

> **[Architecture index](README.md)** · Related: [Units & agents](units-and-agents.md), [Connectors](connectors.md), [Interfaces](interfaces.md)

A **package** is the unit of bundling, installation, and export — an installable
bundle of agent and unit definitions, skills, and templates
([ADR-0035](../decisions/0035-package-as-bundling-unit.md)).

---

## The recursive folder format

Every standalone artefact is a **folder** rooted at a `package.yaml` carrying a
`kind:` discriminator (`Package`, `Unit`, `Agent`, `Skill`, and the template
kinds). Folders compose recursively — a unit folder can contain its own
`agents/`, `units/`, `skills/`, and `templates/` subdirectories
([ADR-0043](../decisions/0043-recursive-package-format.md)).

**Directory layout *is* the manifest.** There is no `content:` block — the
install pipeline discovers artefacts by walking conventional subdirectories:

```
my-package/
  package.yaml          # kind: Package — name, description, version, inputs
  agents/               # agent definition YAMLs
  units/                # unit definition YAMLs
  skills/               # skill bundles (prompt fragment + optional tools)
  templates/            # AgentTemplate / UnitTemplate / HumanTemplate
  execution/            # Dockerfiles for agent execution images (source, not runtime)
```

Each artefact YAML is a kind-discriminated document and carries its own
`requires:` block; install ordering is a `requires:` topological sort with an
alphabetical tie-break ([ADR-0037](../decisions/0037-package-schema-decomposition.md)).

`workflows/` and `connectors/` are **not** package-vocabulary directories —
connector *bindings* are expressed through `requires:` and supplied at install
time ([ADR-0046](../decisions/0046-unified-members-grammar.md)). Workflow-driven
runtimes ship as their own container images (see [Agent runtime](agent-runtime.md)).

## Members and templates

A unit's members — agents, sub-units, and humans — are declared under one
`members:` list with a key-prefix discriminator (`- agent:` / `- unit:` /
`- human:`); see [Units & agents](units-and-agents.md). A **template** is a
non-activating artefact folder that a concrete artefact clones via
`from: <template>`, with scalar/map override and list-replace semantics.
Templates can be reused across packages via `from: <pkg>/<name>@<version>`.

Cross-package references use `Guid`s, never display names; references *within* a
package file use local symbols mapped to fresh `Guid`s at install time (see
[Data & identity](data-and-identity.md)). Cross-package input substitution is
not allowed — a package must be self-contained.

## Install and export

Install is a **two-phase atomic flow** with a persisted install record:

| Step | HTTP | CLI |
|------|------|-----|
| Start install (from catalogue) | `POST /api/v1/packages/install` | `spring package install <name>` |
| Start install (from upload) | `POST /api/v1/packages/install/file` | — |
| Poll status | `GET /api/v1/installs/{id}` | `spring package status <id>` |
| Retry / abort a failed install | `POST /api/v1/installs/{id}/{retry\|abort}` | `spring package retry\|abort <id>` |
| Export | `POST /api/v1/tenant/packages/export` | `spring package export <name>` |

Install **scope** is operator-controlled: top-level artefacts bind to the tenant
or, with `--into <unit>`, to a chosen parent unit. Packages declare typed scalar
`inputs` (`string`, `int`, `bool`, `secret`) validated against supplied values.
A failed install rolls back; the install record carries per-artefact
staging/active counts.

The portal's new-unit wizard exposes a **From catalogue** mode that routes
through the same install endpoint.

## In-tree packages

The repository ships catalogue packages under `packages/`:

| Package | Purpose |
|---------|---------|
| `hello-world` | Minimal connector-free smoke test — one unit, one agent |
| `example-simple` / `example-templated` | Authoring examples (plain and template-based) |
| `software-engineering` | A software-engineering unit |
| `product-management` | A product-management unit |
| `research` / `magazine` | Domain examples |
| `spring-voyage-oss` | The OSS dogfooding unit ([ADR-0034](../decisions/0034-oss-dogfooding-unit.md)) |

`FileSystemPackageCatalogService` walks `packages/` so a new package appears in
the catalogue with no further wiring. The cloud overlay swaps in a tenant-scoped
catalogue via DI. The packages root is configured by `Packages:Root` /
`SPRING_PACKAGES_ROOT`.

## Skills

A **skill** is a bundle: a markdown prompt fragment plus optional tool
definitions. At install the platform resolves each referenced skill, validates
its declared tools against unit policy (a policy-blocked tool fails the install;
an unprovided tool is an advisory warning), and persists the resolved bundle so
prompt assembly can rehydrate it each turn. Skill prompt fragments render into
Layer 2 (unit context) of the assembled prompt; see [Units & agents](units-and-agents.md).
