# 0067 — Runtime/DB config is the single source of truth; package retention is install provenance only; export reconstructs

- **Status:** Accepted (2026-06-07) — [#3090](https://github.com/cvoya-com/spring-voyage/issues/3090). **Supersedes [ADR-0035](0035-package-as-bundling-unit.md) decision 12** (export-verbatim) and **amends [ADR-0040](0040-actor-state-ownership-matrix.md)** (gives unit/agent `model` and `hosting` a single home, finishing the actor-state-ownership boundary that ADR-0040 set). Export reconstruction (R1) ships with this record; the `model`/`hosting` single-home rewrite (R2) and the slim-provenance column drops + re-install/upgrade diff (R4) are tracked as follow-ups (decided direction below), because they touch the dispatch hot path and a destructive migration that is gated behind reworking install retry.
- **Date:** 2026-06-07
- **Related ADRs:** [0035](0035-package-as-bundling-unit.md) — package as the unit of bundling/install/export (decision 12 reversed here); [0040](0040-actor-state-ownership-matrix.md) — actor state ownership matrix (the boundary this record finishes); [0043](0043-recursive-package-format.md) — recursive package format (the folder shape export reconstructs); [0038](0038-agent-runtime-and-model-provider-split.md) — the `execution:` / `ai:` block shapes export renders.
- **Related code:** `src/Cvoya.Spring.Host.Api/Services/{PackageReconstructor,PackageExportService}.cs`; `src/Cvoya.Spring.Dapr/Execution/{DbAgentDefinitionProvider,DbAgentExecutionStore,DbUnitExecutionStore}.cs`; `src/Cvoya.Spring.Dapr/Data/Entities/{Agent,Unit}LiveConfigEntity.cs`, `PackageInstallEntity.cs`; `src/Cvoya.Spring.Host.Api/Services/PackageInstallService.cs`.
- **Related issues:** [#3090](https://github.com/cvoya-com/spring-voyage/issues/3090) (this work); the sibling read-path consolidation [#3089](https://github.com/cvoya-com/spring-voyage/issues/3089) (one DB-backed seam for unit member + role resolution — same north star, the read layer); the R2 follow-up and R4 follow-up filed against #3090.

## Context

Every configuration option of a unit / agent / human is editable after the
package that introduced it is installed — identity, instructions,
execution/model/hosting, memberships + roles + expertise, connector bindings,
secrets, policies, permissions. Each edit is persisted to a dedicated runtime
store (`agent_live_config`, `unit_live_config`, `unit_memberships`,
`unit_connector_bindings`, `unit_policies`, `*_expertise`, the secret registry,
`humans`) or to the per-subject `*_definitions.definition` jsonb. That is the
de-facto runtime contract: the dispatcher, the permission resolver, and the
directory all read these stores, never the package the install captured.

Two *retained representations* are not kept in sync with those edits, and an
analysis on #3090 found them to be distinct problems:

1. **Retained package artefacts** — `package_installs.original_manifest_yaml`
   (raw YAML verbatim) + `inputs_json` + `package_root`. Written once at
   install, never reconciled with a runtime edit. Read by exactly two
   consumers: `spring package export` (which returned the captured manifest
   **verbatim**) and install retry / status re-parse. **`spring package export`
   was therefore a drift bug** — a rename, an instructions change, a model
   swap, a membership/role edit, a policy or connector reconfiguration was
   invisible to the exported package. ADR-0035 decision 12 chose verbatim
   export deliberately, on the assumption an install is frozen; now that
   everything is editable, verbatim export is actively wrong.

2. **The `*_definitions.definition` jsonb vs the dedicated runtime tables.**
   ADR-0040 made the dedicated tables authoritative and dropped the actor-state
   `*:Definition` copies, but left the `definition` jsonb behind carrying
   `execution.*` (including `model` and `hosting`), `instructions`, `role`, and
   `expertise`. The result is that **`model` has two writable homes**:
   `agent_live_config.model` / `unit_live_config.model` *and*
   `definition.execution.model`. For units the dispatch projection in
   `DbAgentDefinitionProvider` overlays `unit_live_config` `model`/`hosting`
   onto the jsonb at dispatch — a patch added precisely because *"a unit
   created with `hosting: persistent` was being dispatched as Ephemeral because
   the JSON didn't carry the flag."* That overlay is the live runtime
   consequence of the dual home.

Editability is satisfied today; what is not satisfied is that **an edit is
reflected consistently through a single source**. The two retained
representations are the gap.

## Decision

**The runtime/DB stores are the single source of truth.** The package is an
install-time template; after install it has no authority. Two consequences
follow.

### 1. Export reconstructs from runtime config (R1 — shipped)

`spring package export` renders a re-installable package **from the live
relational stores**, not from the captured blob. `PackageReconstructor` walks
the artefact graph rooted at the install's top-level unit (or single top-level
agent) and assembles typed `Unit` / `Agent` / `Package` manifest documents
from: the definition jsonb + the live-config / execution projection
(`IAgentDefinitionProvider` — the same source the dispatcher reads), the
membership graph (`unit_memberships` + `unit_subunit_memberships`), own
expertise, unit policies, and the durable connector binding. It serialises them
with the camelCase convention the manifest parser consumes, so a reconstructed
package re-parses and re-installs (proven by a round-trip integration test that
re-parses the export through the production parser).

- **Comment / key-order fidelity is deliberately lost.** That was the value
  ADR-0035 decision 12 protected; export correctness after edits is worth more.
- **Connector bindings render as `requires:` placeholders** (the connector
  *type* only). Connector `config` and any bound secret are never exported, so
  export cannot leak a credential; a re-install re-binds and re-supplies
  secrets.
- `package_installs.original_manifest_yaml` is **no longer the export source.**
  It remains only as install-replay provenance for the retry / abort path
  (see decision 3).

This **reverses ADR-0035 decision 12.**

### 2. `model` and `hosting` get a single home (R2 — amends ADR-0040; tracked)

No field has two writable homes. ADR-0040 moved model/hosting to the
live-config tables in spirit but left the jsonb carrying a competing
`execution.model` / `execution.hosting`. This record commits to a single home
per subject, choosing the home where that subject's hosting already lives and
where its dispatch path already reads — which is **asymmetric by necessity**,
not by preference:

- **Units → `unit_live_config`.** Unit `hosting` already lives only in
  `unit_live_config`; the metadata-edit path already writes `model` / `provider`
  / `hosting` there; the dispatch overlay already reads them from there. The
  unit's `definition.execution` keeps `runtime` / `image` /
  `system_prompt_mode`; `model` and `hosting` are removed from the unit jsonb.
  The dispatch read becomes a direct read of `unit_live_config` (the "overlay"
  stops overlaying a competing value). The member-agent model-inheritance
  default (`Merge`) reads the unit's model from `unit_live_config` too.
- **Agents → `agent_definitions.definition.execution`.** Agent `hosting`
  already lives only in the agent jsonb; the dispatch projection already reads
  `model` / `hosting` from the jsonb and never overlays `agent_live_config`.
  `agent_live_config.model` (consumed today only by effective-metadata
  resolution + model-policy evaluation) is folded onto the jsonb home so the
  two cannot diverge.

Forcing a *uniform* home would move one subject's hosting + dispatch read onto a
different store — churning the dispatch hot path for no invariant gain. The
invariant ("one writable home") is what matters; the per-subject home is chosen
for minimal hot-path risk.

`instructions`, `role`, and the expertise seed stay on the jsonb as their only
home — they have no live-config equivalent, so there is no competing home to
collapse.

**Migration ordering is load-bearing** (and the reason R2 is tracked rather
than rushed): the backfill of `unit_live_config.{hosting,provider,model}` into
the unit jsonb (and the agent jsonb model fold) must land — and be proven —
**before** the dispatch reader changes and **before** any column is dropped, or
a unit loses its `hosting` flag mid-migration (the exact persistent-vs-ephemeral
regression the overlay was added to prevent). No actor-state migration is
needed — ADR-0040 already removed the `*:Definition` copies.

### 3. Package retention is install provenance only (R3); re-install diffs runtime config (R4 — tracked)

`package_installs` keeps lightweight install provenance: `install_id` + package
name/version + a slim status / timestamps / error row, plus
`install_state` / `last_validation_*` (runtime install state). `inputs_json` is
kept **only** while install retry / abort needs it. `original_manifest_yaml`
and `package_root` are retired **once retry no longer replays them** — retry and
status currently re-parse the captured YAML, so the column drop is gated behind
reworking retry to diff against runtime config and to shrink `inputs_json` to
the secret-binding map needed to re-resolve placeholders (R4). That rework is an
upgrade-semantics design the #3090 analysis does not settle, so it is a tracked
follow-up; the destructive column drop ships with it, not before.

## Consequences

**Easier:**

- `spring package export` is correct after any edit — the exported package
  *is* the running configuration, not a stale snapshot.
- One sentence answers "where does `model` / `hosting` live?" per subject, and
  the dispatch-time overlay hack disappears.
- The `original_manifest_yaml` blob (the largest retained artefact) is no longer
  load-bearing for export; it is provenance that a later follow-up retires.

**Harder / explicitly deferred:**

- Export no longer preserves authoring comments or key order. Operators who
  treated export as a verbatim backup of their hand-written YAML lose that;
  export now means "the current configuration, re-rendered."
- Reconstruction fidelity for the full artefact grammar (cross-package member
  references, human team-members, template re-derivation, boundary rules) is
  staged: R1 ships the common shapes (unit + member agents / sub-units,
  execution, model/hosting, expertise, policies, connector `requires`) that get
  edited; any remaining grammar gap is a tracked follow-up rather than a guess.
- The `model` / `hosting` single-home rewrite (R2) and the destructive
  provenance drop (R4) are tracked follow-ups, not in the shipping change. The
  principle is decided here; the dispatch-hot-path edit and the
  retry-gated migration land in focused PRs with the migration meticulousness
  they require.

**Not abstracted (deliberately):**

- `withValues` on the export surface is reserved for a future `inputs:`
  materialisation; reconstruction already emits placeholders and never cleartext
  regardless, so the flag is not load-bearing today.
- Multi-package install export remains first-package-only (the pre-existing
  #1579 limitation); this record does not change it.
