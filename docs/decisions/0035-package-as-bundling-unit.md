# 0035 — Package as the unit of bundling, installation, and export

- **Status:** Accepted — 2026-05-02 — *package* replaces *template* on user-facing surfaces; one root `package.yaml` is the entry point; references compose uniformly across artefact types; install is two-phase atomic with a persisted `installs` row; the catalog is global, installations are tenant-scoped; `spring package install` accepts a multi-target batch with topological resolution.
- **Date:** 2026-05-02
- **Closes:** [#1555](https://github.com/cvoya-com/spring-voyage/issues/1555)
- **Umbrella:** [#1554](https://github.com/cvoya-com/spring-voyage/issues/1554) — Package as the unit of bundling, installation, and export — v0.1 collapse
- **Related code:** `src/Cvoya.Spring.Manifest/ManifestParser.cs`, `src/Cvoya.Spring.Manifest/UnitManifest.cs`, `src/Cvoya.Spring.Host.Api/Services/IPackageCatalogService.cs`, `src/Cvoya.Spring.Host.Api/Services/FileSystemPackageCatalogService.cs`, `src/Cvoya.Spring.Host.Api/Services/UnitCreationService.cs` (`CreateFromManifestAsync` at line 162; `TryRollbackAsync` at line 1070), `src/Cvoya.Spring.Host.Api/Endpoints/UnitEndpoints.cs`, `src/Cvoya.Spring.Cli/Commands/UnitCommand.cs`, `src/Cvoya.Spring.Cli/Commands/ApplyCommand.cs` (to be deleted), `src/Cvoya.Spring.Web/src/app/units/create/page.tsx`, `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx`, `src/Cvoya.Spring.Core/Skills/SkillBundleReference.cs`, `src/Cvoya.Spring.Core/Skills/ISkillBundleResolver.cs`, `packages/{research,product-management,spring-voyage-oss}/`.
- **Related ADRs:** [0017](0017-unit-is-an-agent-composite.md) — Unit is an agent composite. [0021](0021-spring-voyage-is-not-an-agent-runtime.md) — SV is not an agent runtime. [0022](0022-postgres-as-primary-store.md) — Postgres as primary store. [0023](0023-flat-actor-ids.md) — Flat actor ids. [0024](0024-unit-validation-as-dapr-workflow.md) — Unit validation as a Dapr workflow. [0029](0029-tenant-execution-boundary.md) — Tenant execution boundary. [0034](0034-oss-dogfooding-unit.md) — OSS dogfooding unit (the forcing function for this ADR).

## Context

PR #1530 shipped `packages/spring-voyage-oss/` — a directory containing five unit YAMLs, thirteen agent YAMLs, and a README claiming the wizard / CLI would atomically instantiate the whole five-unit organisation from one operator action. That capability does not exist. The CLI's `spring unit create-from-template` produces one unit at a time, the new-unit wizard does the same, the README's flag names do not match the actual surface, and `spring apply` lives parallel to that path with overlapping but distinct semantics.

Diagnosing the gap exposed two compounding problems:

1. **Two concepts for one thing.** The codebase distinguishes *package* (a directory under `packages/`) from *template* (a YAML inside it). Operators have to learn both, but both describe the same shape at different granularities — "what gets installed as one unit of work" vs "a namespace prefix." The duplication has produced inconsistent verbs (`spring unit create-from-template` vs `spring apply`), inconsistent UX (catalog-pick vs YAML-paste mode), and a doc surface that explains the same idea two ways.
2. **No atomic multi-unit install.** Even if the OSS package's `package.yaml` listed sub-units, the install pipeline has no transactional shape: best-effort `TryRollbackAsync` (`UnitCreationService.cs:1070`) catches failures one row at a time, leaves orphans on multi-row failure, and offers no operator-visible recovery surface. Multi-package installs (a package referencing artefacts in another package) have no contract at all.

[ADR 0034](0034-oss-dogfooding-unit.md) is the forcing function: the OSS dogfooding package's whole value proposition is "one operator action → working five-unit organisation." Without the collapse and the install contract this ADR locks in, that promise is unfulfillable.

This ADR is the conceptual gate for the v0.1 package-collapse umbrella ([#1554](https://github.com/cvoya-com/spring-voyage/issues/1554)). It locks the schema, the reference grammar, the install contract, the tenancy boundary, and the CLI / wizard surface so the implementation issues that hang under #1554 can land in parallel without diverging.

## Decision

### 1. One concept: *package*

The word *template* leaves user-facing surfaces entirely — CLI verbs, wizard copy, doc nav, file paths, response DTOs that operators see. A **package** is the unit of bundling (one directory in `packages/` or one uploaded archive), the unit of installation (one `spring package install` invocation), and the unit of export (`spring package export <unit-name>` writes a package back). Internal placeholder substitution becomes "package inputs."

Rejected: keeping both terms with a layered explanation ("a package contains templates"). The layered model is a real cost on every surface that has to render it (CLI help, wizard step copy, doc nav, API response DTOs) and has zero compensating benefit — the package and the template are 1:1 in every package shipped or planned. The internal namespace meaning of *package* is preserved; the user-facing meaning collapses onto the artefact.

Rejected: keeping *template* as the user term and demoting *package* to an internal directory naming convention. The OSS package's value is precisely that it is a multi-unit composite — calling that a "template" understates it and re-introduces the "what does that contain?" problem on the surface where it matters most.

### 2. Schema shape: one root `package.yaml` per package

A package has exactly one root `package.yaml` as its entry point. Composability is via flat-string references resolved relative to the package root: `subUnit: sv-oss-design` resolves to `./units/sv-oss-design.yaml`; `agent: architect` resolves to `./agents/architect.yaml`; `skill: code-review` resolves to `./skills/code-review.md` (+ optional `.tools.json`). No `$ref:` / `$packageRef:` / `!Ref` / inline-import syntax. Sub-unit YAMLs keep today's grammar (`unit.name`, `members`, `connectors`, `execution`, `boundary`, `expertise`, `orchestration`).

Rejected: a JSON-Schema-style `$ref:` syntax. It buys nothing here — every reference is a flat string against a package-local lookup, and `$ref:` carries an entire JSON Pointer model the parser would have to implement and operators would have to learn. The flat-string form parses unambiguously and reads naturally.

Rejected: inline composition (every member's full YAML inline in the parent `package.yaml`). Disallows reuse across packages, makes the parent file unbounded, and loses the per-file diff locality that today's tree gives.

The parser detects cycles in sub-unit and skill references and aborts with the offending path; cycles are operator errors, not silent infinite recursion.

### 3. Reference scope: uniform across artefact types

Units, agents, workflows, and skills follow the same composition rule:

- **Bare name** (`agent: architect`, `subUnit: sv-oss-design`, `skill: code-review`) resolves within the current package.
- **Qualified name** (`agent: spring-voyage-oss/architect`) resolves to another installed package via the catalog.

Today only `SkillBundleReference` (`src/Cvoya.Spring.Core/Skills/SkillBundleReference.cs`) supports cross-package references; this ADR generalises that mechanism to every artefact type. The existing `ISkillBundleResolver` shape — coordinate-keyed lookup against a pluggable resolver — is the seed for the general resolver implemented in #1557.

Rejected: keeping the carve-out (skills cross packages, units / agents / workflows do not). The carve-out is a historical accident — skills landed first and grew the cross-package shape because their content was the most obviously reusable. Every other artefact type has the same reuse motivation; sustaining the asymmetry would force operators to learn two composition rules and would force authors to choose at design time which kind of artefact a thing is based on whether it might one day need to cross a package boundary.

Rejected: per-type qualifier prefixes (`@spring-voyage-oss/architect`, `~code-review`). Different sigils for the same operation are noise. The `<package>/<name>` form is unambiguous against today's directory and namespace rules and is the same shape Docker, npm scoped packages, and Cargo all converged on.

**v0.1 has no package versioning.** Cross-package references always resolve to the currently-installed version. If package A is installed at version v1 and references `B/some-agent`, and B is later upgraded to v2, A's reference now points at v2's agent — even if v2's `some-agent` is incompatible. This is a known limitation; lifecycle coupling between cross-referencing packages is revisited when versioning lands (out of v0.1 scope; tracked under [#1554](https://github.com/cvoya-com/spring-voyage/issues/1554)'s known-risks).

### 4. CLI verb cluster: `spring package …`

The user-facing surface for installing, inspecting, recovering, and exporting packages is one verb cluster:

- `spring package install <name> [<name>...]` — install one or more packages from the catalog.
- `spring package install --file <path>` — install from a local file (replaces `spring apply` entirely).
- `spring package status <install-id>` — inspect install state, including post-Phase-2 staging rows.
- `spring package retry <install-id>` — re-run Phase 2 after fixing a transient failure.
- `spring package abort <install-id>` — discard an install whose Phase 2 cannot complete.
- `spring package export <unit-name> [--with-values]` — write a package YAML back from an installed unit.
- `spring package list` / `spring package show <name>` — catalog browse.

Inputs are supplied as `--input k=v` (repeatable) or `--input-file values.yaml`. For multi-target installs, inputs are namespaced by package: `--input <package>.<key>=<value>`.

`spring unit create-from-template` and `spring apply` are **deleted outright** — no deprecation tail, no shim verbs, no compatibility flag. Spring Voyage is pre-v1.0; the cost of a hard rename is bounded (one CHANGELOG entry, one `--help` message pointing operators at the replacement) and the cost of carrying parallel verbs is not.

Rejected: keeping `spring apply` as a thin alias for `spring package install --file`. Aliases that drift produce subtle behavioural divergence (different exit codes, different validation order, different stderr framing). One name, one path.

Rejected: a softer rename like `spring template install`. Restates problem 1 — the user surface should not name an internal distinction the operator does not use.

### 5. Wizard entry choices: `catalog | browse | scratch`

The new-unit and new-agent wizards offer exactly three sources:

- **Catalog** — pick from the installed catalog; render an inputs form generated from the package's `inputs` schema.
- **Browse** — upload a `package.yaml` (or a tarball / zip of a package directory). Ships as a "coming soon" stub for v0.1; the surface exists so doc and UX align with the final shape.
- **Scratch** — build a unit (or agent) from first principles. The wizard constructs a package in memory and submits it through the same install endpoint the CLI uses.

YAML-paste mode is removed. It overlapped *browse* without offering anything *browse* does not — and forced the wizard to maintain a YAML editor surface (~5 tests, syntax highlighting, schema hints) that no other surface used.

Rejected: collapsing further to `catalog | scratch` and shipping browse later. The "coming soon" stub is cheap (one card, one link to the v0.2 issue) and prevents a doc rewrite when browse lands; without it, every reference to "the three sources" in concept docs would be aspirational until v0.2.

### 6. Both wizards build a package; one install pipeline

The new-unit wizard's *scratch* path and the new-agent wizard both construct a package in memory and POST to `/api/v1/packages/install` — the same endpoint the CLI uses. The new-agent wizard submits a payload of `kind: AgentPackage`; the new-unit *scratch* path submits `kind: UnitPackage`. There is one validation pipeline, one install pipeline, one set of error semantics.

Rejected: keeping a wizard-private `/api/v1/units/from-yaml` path "for simplicity." Two pipelines means two sets of edge cases (input interpolation order, name-collision phase, transaction boundary). Every divergence between them becomes a portal-vs-CLI parity bug.

### 7. Connector bindings declared in the package schema at every unit level

The `package.yaml` and each member sub-unit YAML can declare a `connectors:` block with `${{ inputs.<name> }}` interpolation against the package's `inputs` schema. The wizard introspects the manifest to render the connector-config form; the CLI accepts the same values via `--input` flags.

Rejected: a wizard-only post-creation "bind connectors" step. Splits the operator action into two and requires a recovery story for "I created the unit but the connector binding step failed" that we would rather not author.

### 8. Inputs are scalar-only for v0.1

Input types are **string**, **int**, **bool**, **secret** (`secret: true`). No lists, no objects, no `if:` / `default-when:` conditional defaults, no expression language beyond literal substitution.

`${{ inputs.<name> }}` substitution happens **pre-validation** as pure string replacement on the parsed manifest text — before schema validation, before reference resolution, before the install pipeline. Substitution failures (referencing an undeclared input, or supplying a value of the wrong scalar type) produce a parse-stage error naming the offending key.

Rejected: shipping a real templating engine (Liquid, Handlebars, Jinja-shaped) in v0.1. Templating engines pull in a long tail of decisions — escaping rules, partials, filters, evaluation order, sandboxing, error-message ergonomics — that have nothing to do with the v0.1 install story. Scalar substitution is sufficient for every shipped package and every package on the v0.1 horizon. The engine choice is deferred until a real package needs it.

### 9. Secret inputs reuse the existing tenant-secret store

Inputs marked `secret: true` are stored as references, not values. The wizard / CLI prompt for the secret value, write it to the tenant-secret store via the existing path, and the install records the secret reference (not the cleartext) in `InputBindings`. Export with `--with-values` materialises secret-typed inputs as placeholder names, never as the cleartext.

Rejected: a new package-scoped secret store. The tenant-secret store already enforces tenancy, ACLs, and rotation; building a parallel one for "package install secrets" would require us to re-derive every property already in production.

### 10. Name collisions = error

Every install pre-flights every name in the package against `IDirectoryService.ResolveAsync` (`src/Cvoya.Spring.Core/Directory/IDirectoryService.cs`). The first collision aborts the install with an error that names every offending name, not just the first. There is no "force-overwrite" flag; an operator who wants to replace an installed package uninstalls it first (uninstall is out of v0.1 scope; the operator can delete the units / agents directly until then).

Rejected: silently overwriting on collision. Multi-tenant safety: in a tenant where a unit already exists at `team/architect`, an unrelated install of `team/architect` from a different operator must not silently replace it. Failing closed is the only safe default.

### 11. Two-phase atomic install

Installs run in two phases tracked by a single `install_id`:

- **Phase 1** (single EF transaction): validate the package(s), resolve and substitute inputs, run name-collision pre-flight, write all `unit_directory` + `connector_bindings` + `skill_bundles` rows with `state = 'staging'`. Any failure rolls the whole transaction back — zero rows survive. No Dapr involvement.
- **Phase 2** (post-commit): activate actors in dependency order (parents before sub-units), flip `state = 'active'` per row as activation succeeds. Activation failures leave the staging rows visible so an operator can `spring package status <install-id>` to inspect, then `retry` (after fixing the underlying issue) or `abort` (which deletes the staging rows).

The existing best-effort `TryRollbackAsync` ladder (`UnitCreationService.cs:1070`) is replaced by this contract. Phase 1's transaction abort is the only rollback Phase 1 needs; Phase 2 has no rollback — it has the recovery surface (`status` / `retry` / `abort`).

Rejected: a single-phase install that activates inline. Activation failures (Dapr placement timeout, container image pull, model probe failure) would force us to either roll back the EF rows (silently destroying operator-visible state) or leave them with no `state` discriminator (silently presenting half-installed packages as healthy). The two-phase shape lets the EF transaction be atomic and the activation step be observable + recoverable.

Rejected: a Saga / compensating-transaction Dapr workflow spanning the whole install. The shape is much heavier than the problem requires — Phase 1 catches ~95% of failure cases (validation, name collision, missing inputs) and finishes in milliseconds; only Phase 2 needs the recoverable surface, and `status` + `retry` + `abort` is exactly that surface without the workflow scaffolding.

### 12. Round-trip fidelity via `OriginalManifestYaml` + `InputBindings`

The `installs` table persists per install:

- `OriginalManifestYaml` — the operator-supplied YAML as a **string blob**. Preserves comments, ordering, formatting, and anything else the parser would lose.
- `InputBindings` — per-input resolved value (or secret reference for `secret: true` inputs).
- `package_name` — for multi-target installs, each row carries its package name so `export` can filter per-package.
- `install_id`, `status`, `created_at`, `updated_at`.

`spring package export <unit-name>` reads `OriginalManifestYaml` verbatim. With `--with-values`, the export materialises an `inputs:` block from `InputBindings`, with `secret: true` inputs exported as placeholder names rather than cleartext.

Rejected: round-tripping the parsed manifest object via YamlDotNet's serializer. Loses comments, loses key ordering, loses any formatting choice the operator made. The blob is the simplest contract that makes round-trip a non-question.

### 13. Tenancy: global catalog, tenant-scoped installs, browse one-shot in v0.1

- The **catalog is global** — one filesystem source for OSS, surfaced by `IPackageCatalogService` (`src/Cvoya.Spring.Host.Api/Services/IPackageCatalogService.cs`). The interface is already comment-marked as pluggable, so the private cloud repo can swap in a tenant-scoped or hybrid implementation without changing the portal or CLI surface.
- **Installations are tenant-scoped.** Units, agents, bindings, and secrets all live in the tenant namespace today; this ADR does not change that. Different tenants installing the same package get independent unit / agent / binding sets; cross-tenant reference is impossible by construction.
- **Browse-uploaded packages are one-shot in v0.1.** Upload → install → discard. A persistent tenant-scoped catalog is v0.2 work; the v0.1 stub for `browse` keeps the surface honest without committing to the storage shape.

Rejected: a tenant-scoped catalog in v0.1. Requires choosing a storage shape (S3 / blob / Postgres-backed), an upload UI, a per-tenant ACL story, and a versioning model — all of which are v0.2-or-later by their own merits.

### 14. Multi-package install

`spring package install A B C` accepts multiple packages and installs them as one transaction:

- The server topologically sorts the batch by cross-package references.
- It validates the dep graph is closed: any reference to a package neither in the batch nor already installed in the tenant fails fast with `package <X> references <pkg>/<name>, which is not in the install batch and not installed in this tenant`.
- Phase 1 runs across the whole batch in a single EF transaction.
- Phase 2 runs in dep order (the package whose actors no other package depends on activates last).
- One `install_id` per command (whether 1 or N packages); each `unit_directory` / `connector_bindings` / `skill_bundles` row carries `package_name` so `export` and `status` can filter per-package.

Cross-package reference resolution looks in **(1) the in-flight batch first, (2) the tenant's already-installed packages second**. There is **no auto-resolution from the catalog** — operators must list every package explicitly. Predictable; no implicit installs of unfamiliar packages; auto-resolution is v0.2.

Inputs are namespaced by package: `--input A.foo=bar --input B.baz=qux`.

Rejected: auto-resolution from the catalog. "Install pulls in dependencies the operator did not name" is a dangerous default in a multi-tenant system — the operator has not consented to install package X, may not have read its `inputs:` block, may not want it installed at all. Explicit listing makes every install reviewable from the command alone.

Rejected: per-package install transactions ("install A, then B, then C"). Loses the all-or-nothing property; a Phase-1 failure on C would leave A and B installed in a state the operator did not request.

## Consequences

### Gains

- **One concept, one verb, one pipeline.** Operators learn *package* once. The CLI verb cluster, the wizard sources, the API endpoint, and the export shape all speak the same noun. The doc surface shrinks.
- **OSS dogfooding works as the README promises.** ADR 0034's package becomes installable in one operator action; the multi-unit composite is the install primitive, not a kit of parts.
- **Two-phase atomic install replaces best-effort rollback.** Phase-1 failures are kill-switch atomic; Phase-2 failures are operator-visible and recoverable. The half-installed-package failure mode that `TryRollbackAsync` produces today is gone.
- **Uniform composition.** Every artefact type composes across packages on the same terms. The `SkillBundleReference` resolver pattern generalises; no per-type carve-out.
- **Round-trip fidelity is a non-question.** `OriginalManifestYaml` as a blob preserves what operators wrote; export gives them back exactly what they would have written by hand.
- **Tenant isolation by construction.** The catalog being global and the installs being tenant-scoped is the same shape v0.1 already has for units / agents / bindings; this ADR does not invent a new tenancy axis.

### Costs

- **Hard rename, no deprecation tail.** `spring unit create-from-template` and `spring apply` go away in one step. Anyone with scripts pinning those verbs has to update them. Acceptable pre-v1.0; a CHANGELOG entry and pointed `--help` text mitigate the rough edge.
- **Cross-package references couple lifecycles without versioning.** Until package versioning lands, an upgrade of package B can break package A's references silently at next actor activation. Documented as a v0.1 known limitation; mitigated until then by `uninstall` (out of v0.1 scope) blocking when other installed packages reference the target.
- **Phase-2 failures will be the support burden.** Phase 1 catches validation, collision, missing-input, and dep-graph-closure failures (~95% of cases). Phase 2's failures (Dapr placement timeout, container image pull, model probe failure) all surface as "I clicked Install and now I have a half-installed package." `spring package status` / `retry` / `abort` and the staging-rows-stay-visible model are non-negotiable.
- **Multi-package install error messages have to be precise.** "Missing dependency" without context is a sharp edge; the dep-graph validator must produce errors of the form `"package <X> references <pkg>/<name>, which is not in the install batch and not installed in this tenant"`. Tested explicitly in the implementation issue (#1558).
- **Existing packages need migration.** `packages/research/`, `packages/product-management/`, and `packages/spring-voyage-oss/` are wrapped in `package.yaml` files; the OSS README is rewritten against the real surface. Bounded one-time work; tracked under #1562.
- **Three implementation seams converge in one place.** Manifest parser, install service, and the resolver all change at once. The phased issue tree (#1557 → #1558 → #1559 → fanout) is what keeps that load reviewable.

### Known follow-ups

- **Package versioning.** Cross-package reference lifecycle coupling is the v0.1 known limitation that versioning resolves. Filed under #1554's known-risks.
- **`spring package uninstall`.** Out of v0.1 scope; needed to make cross-package reference uninstall safety enforceable rather than documented.
- **Persistent tenant-scoped browse catalog.** v0.1 ships browse as a one-shot upload-then-discard surface. v0.2 picks the storage shape.
- **Auto-resolution of dependencies in `spring package install`.** v0.1 requires explicit listing; v0.2 may add a `--with-deps` opt-in once the failure model and trust model are clearer.
- **`Pooled` cross-package references.** v0.1's resolver returns a single matching artefact per coordinate. If a future package wants to express "any of the three I have on hand" semantics, that is a resolver-level extension, not a schema change.

## Revisit criteria

Revisit if any of the below hold:

- A real package needs an input shape scalar substitution cannot express (lists, conditional defaults, derived values). At that point the conversation is the templating-engine choice deferred under decision 8 — not a one-off escape hatch in the parser.
- Cross-package reference lifecycle pain becomes routine before package versioning ships. At that point the answer is to accelerate versioning, not to retract decision 3 — the cross-package composition gain is large and per-type carve-outs are not the right cost-cut.
- Phase-2 failures dominate the operator support burden in measurable terms. At that point the conversation is whether Phase 2 should be a Dapr workflow with its own checkpointed activities — not whether to merge Phases 1 and 2 back together.
- Multi-tenant catalog requirements arrive earlier than v0.2 (a private-cloud customer ships a tenant-private package). At that point `IPackageCatalogService` already accommodates the swap; this ADR does not need amendment, only a follow-up implementation.
- A second built-in package collides with the global-catalog assumption (e.g. two packages claim the same name across customer environments). At that point the conversation is a namespacing convention for catalog packages, layered above the resolver — not a retraction of the global-catalog choice.
