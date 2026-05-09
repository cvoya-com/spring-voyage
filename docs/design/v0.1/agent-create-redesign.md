# Agent-create flow redesign — design spec

Status: Design — implementation deferred. Refs `#1763` (UX redesign issue), `#1786` (units-are-agents architecture umbrella). Layers on top of [ADR-0038](../../decisions/0038-agent-runtime-and-model-provider-split.md) (runtime / provider / model split) and **[ADR-0039](../../decisions/0039-units-are-agents.md)** (units are agents — orchestration is runtime behaviour, not platform configuration). Sequenced alongside the broader platform work in [`docs/plan/v0.1/units-are-agents.md`](../../plan/v0.1/units-are-agents.md).

This document is the implementation contract for the agent-create UX redesign described in `#1763`. It audits the three creation surfaces that exist today (the unit-tab "Add Agent" dialog, the standalone `/agents/create` wizard, and `spring agent create`), describes the unified target design under the units-are-agents framing recorded in ADR-0039, and slices the UX-side work for downstream PRs. **No production code is touched in this PR — the implementation lands separately.**

ADR-0039 is the architectural prerequisite: it decides that a unit is an agent, that orchestration policy is not platform configuration, and that inheritance generalises to "top-level under tenant; multi-parent must define their own when parents diverge; reparenting revalidates." This design assumes those rules and treats them as fixed. The platform-side implementation of ADR-0039 (PRs 2–9 in the execution plan) precedes most of this design's UX-side work; only the multi-parent inheritance backend (plan PR 3) is a strict prerequisite for the form work that begins with PR 10.

---

## 1. Audit — what exists today

### 1.1 Surfaces

| Surface | Route / file | Wire path | Configuration coverage today |
|---|---|---|---|
| Unit-tab "Add Agent" dialog (picker) | `src/components/units/tab-impls/agents-tab.tsx` + `src/components/units/membership-dialog.tsx` | `POST /api/v1/tenant/agents` (via `buildCreateAgentRequest`) for inline-create; `PUT /api/v1/units/{id}/memberships/{addr}` for the picker path | **Picker mode:** agent select + per-membership (model, specialty, executionMode, enabled). **Inline-create sub-mode:** displayName + role only — runtime / provider / model / image not exposed |
| Standalone wizard | `src/app/agents/create/page.tsx` + `src/app/agents/create/build-agent-package.ts` | `POST /api/v1/packages/install/file` (assembles an `AgentPackage` YAML); per-unit `POST /api/v1/units/{id}/agents/{agentId}` after install reaches `active` | id, displayName, role, description, runtime, model provider, model id, image (no hosting; no connector — connectors are unit-scoped today), unit assignments (multi) |
| Unit-create wizard (the surface agents must mirror) | `src/app/units/create/page.tsx` (3,901 LOC; `wizard-persistence.ts`) | `POST /api/v1/packages/install` (catalog), `POST /api/v1/packages/install/file` (file/scratch), or `POST /api/v1/tenant/units` + `PUT /api/v1/tenant/units/{id}/execution` (scratch direct path); see lines 1226–1340 | Source step (catalog / browse / scratch) → branch-specific steps. Scratch covers identity, runtime, provider, model, image, hosting, color, connector binding, parent-unit picker, inline-LLM-credential entry |
| CLI | `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs` (`CreateCreateCommand`) | `POST /api/v1/tenant/agents` via the generated `ApiClient` | id, `--name` (display), `--role`, `--unit` (≥1, repeatable, required), `--definition` / `--definition-file` (JSON blob), `--image`, `--runtime`, `--model-provider`, `--model`. Legacy `--agent` rejected at parse time per ADR-0038 §7; legacy `--container-runtime` rejected at parse time per ADR-0039 §7. **No `--from-package`, no `--inherit`, no `--description`** |

### 1.2 Backend contract (`POST /api/v1/tenant/agents`)

`AgentEndpoints.CreateAgentAsync` (`src/Cvoya.Spring.Host.Api/Endpoints/AgentEndpoints.cs:718-887`) accepts `CreateAgentRequest`:

```ts
CreateAgentRequest = {
    displayName: string;       // required, validated; Guid-shaped names rejected (#1632)
    description: string;       // required (allowed empty)
    role: null | string;
    unitIds: string[];         // today ≥1; target permits [] for top-level tenant-parented agents
    definitionJson?: null | string;  // optional; persisted on AgentDefinitions.Definition
}
```

The endpoint today:
- Validates `displayName`, normalises and rejects empty `unitIds`.
- Resolves every referenced unit through the directory; tenant-guards each (#745).
- Validates `definitionJson` as a JSON document if supplied; otherwise leaves the agent's definition row untouched.
- Server allocates the agent's stable Guid (`actorGuid`) — callers cannot supply `name`. Existing CLI callers may still pass `--name`; the server treats it as the display name only (the issue's "Notes" section confirms).
- Writes the membership rows + the legacy cached pointer; mirrors the first unit as `ParentUnit`.
- Persists the supplied `definitionJson` blob verbatim onto `AgentDefinitionEntity.Definition` if present.

Implication: **the redesign keeps the same endpoint but changes its validation.** The target endpoint accepts an empty `unitIds` list as "top-level agent parented by the tenant," mirroring top-level units; in that case it writes no unit membership rows and resolves inherited execution config from tenant defaults. Every form value still rides in `CreateAgentRequest` directly (display name, description, role, unit ids) or in the `definitionJson` blob (`execution.image`, `execution.runtime`, `execution.model.{provider,id}`, `execution.hosting`). The portal's `buildAgentDefinitionJson` (`src/lib/agents/create-agent.ts:116`) already produces a compatible shape; it needs to accept the structured `model: {provider, id}` field and `hosting` to reach full parity with the unit wizard.

### 1.3 Inheritance semantics today

There is no explicit "inherit" affordance on either creation surface. The current behaviour is:

- **Standalone wizard** — the model dropdown's first option is literally `"Inherit from unit / runtime default"` (line 769 of `app/agents/create/page.tsx`). Other fields (image, runtime, provider) have no equivalent. Leaving runtime blank isn't possible — the dropdown has no empty value, and `INITIAL_FORM` defaults to `claude-code`.
- **Unit-tab inline create** — image / runtime / provider / model are not exposed at all; the agent silently inherits whatever the unit resolves at dispatch.
- **CLI** — no flag for "inherit"; absence of a shorthand flag is implicit inheritance.
- **Backend** — `IAgentDefinitionProvider` resolves a missing field to the unit's resolved value at dispatch time. The schema treats absence as inheritance.

The existing **DESIGN.md §12.6 "Inherit-from-parent indicator"** (line 544) is the canonical visual contract for fields that resolve to a parent default at save time: italic placeholder copy, help text below carrying `data-testid="inherit-indicator"`, no visual lock, and the card header carrying an `Inherits` outline badge that flips to `Configured` once any override is persisted. The redesign reuses this pattern verbatim for per-field overrides.

### 1.4 The unit-create three-paths model

Step 1 of `/units/create` renders three `<SourceCard>` choices (line 1799–1840):

| Source | Description as written today |
|---|---|
| Catalog | "Install a pre-built unit from a package in the catalog. The package supplies the full definition; you only need to provide required connector bindings." |
| Browse | "Search the Spring Voyage package registry for community packages. (Coming soon — use the CLI for now.)" |
| Scratch | "Define a new unit from scratch. You supply the name, execution tool, model, and optional connector binding." |

Branch step counts (`maxStepForSource`, line 159): `catalog → 4`, `browse → 2 (stub)`, `scratch → 5`. The scratch step labels are `Source → Identity → Execution → Connector → Install`.

The standalone agent wizard skips this step entirely — it is hard-wired to "scratch" and does not offer catalog/browse. The unit-tab inline-create dialog only offers a sparse "from scratch" path.

### 1.5 Cross-cutting concerns surfaced by the audit

- **Connectors are unit-scoped, not agent-scoped.** The standalone wizard does not expose a connector field — the unit-create wizard's connector step binds the connector to the *unit*. The issue lists "connector" in the configuration coverage gap, but the operationally meaningful connector binding for an agent is resolved through the unit it joins. The redesign therefore retains "connector" as a unit-level concern; the agent-create form does not grow a connector picker. It surfaces the chosen unit's effective connector binding as **read-only display** so the operator understands the resolved environment.
- **Parent assignment.** The standalone wizard currently requires ≥1 unit; the unit-tab dialog implicitly assigns to the current unit only. The redesign changes the standalone rule to match units: zero selected units means a top-level, tenant-parented agent; one or more selected units means a member agent. When launched from a unit's Agents tab, the unit is preselected and the multi-select widget collapses into a confirmation strip; when launched standalone, full multi-select is available.
- **Unit GUID rendered in the picker dialog.** Line 380 of `membership-dialog.tsx` literally interpolates `unitLabel` (a Guid) into the dialog description — the issue's specific complaint. The fix is mechanical: the parent supplies a display name, not the Guid.
- **`/agents/create` uses package-install pipeline; the inline path uses `POST /api/v1/tenant/agents` directly.** Convergence requires picking one pipeline. See § 6.

---

## 2. Target design

### 2.1 One form schema, three surfaces

A single component — `<AgentCreateForm>` — drives every surface. It owns a self-contained `useState` form, validation, runtime/provider/model resolution, and submit. Three thin shells consume it:

| Shell | Where | Defaults |
|---|---|---|
| `<AgentCreateDialog>` | Unit Agents tab; opens from the **Add Agent** button | `unitIds` preselected to `[currentUnit.id]`, multi-unit picker collapsed; the dialog header shows the unit's display name and Guid; submission closes the dialog and refreshes the memberships query |
| `<AgentCreateWizardPage>` | `/agents/create` | `unitIds` empty (top-level tenant-parented agent); rendered as a full page with breadcrumbs |
| `<AgentCreateWizardPage>` (with `?parent=<unitId>`) | `/agents/create?parent=<id>` | Same as standalone, but with the named unit preselected. Mirrors the unit-create wizard's `?parent=<id>` URL-seed pattern |

Both shells render the same tab-by-tab inner contents; only the chrome (dialog frame vs page-with-breadcrumbs) differs. This is the unit-create wizard's existing pattern — the wizard component is huge, but the same step skeleton can drive both the page and a dialog. The form schema lives in `src/lib/agents/create-agent.ts` (already partially populated) and is the **single source of truth** for both the dialog and the page.

#### 2.1.1 The picker dies

The unit-tab dialog **no longer offers an agent picker for existing agents**. The product reasoning, restated from the issue: there is no operator workflow that re-assigns an already-assigned agent through the unit's Agents tab — that surface is conceptually "add a member to this unit," not "browse the agent directory and stage an assignment." Operators who want to assign an existing agent to an additional unit do so from the agent's detail page (or from the unit's tree-view drag-target — out of scope). The dialog's `mode === "add"` branch becomes unconditional create.

The dialog's `mode === "edit"` branch (per-membership edit: model override, specialty, execution mode, enabled) **stays**. That surface is still needed.

### 2.2 Three creation paths, mirroring the unit wizard

The unified form opens on a Source step matching `/units/create`:

| Path | Wire path | Coverage in v0.1 |
|---|---|---|
| **From scratch** | `POST /api/v1/tenant/agents` with `definitionJson` carrying `execution.{image,hosting,runtime,model:{provider,id}}` | Full |
| **From package** | `POST /api/v1/packages/install` with `{packageName, connectors, inputs}` (catalog branch) — same endpoint the unit wizard uses | Full when an `AgentPackage` is selected |
| **Browse** | Stub — same "Coming soon, use the CLI" pattern as the unit wizard's browse branch | Stub only |

The unit wizard's catalog endpoint already accepts agent packages (the CLI uses it for `spring package install <agent-pkg>`); the redesign filters the package list to entries whose manifest declares an agent (`pkg.agentTemplateCount > 0` is already on `PackageSummary`). The from-package shape unifies the standalone wizard's existing two-phase pipeline (package YAML → install → membership wiring) with the catalog code path.

#### 2.2.1 Why drop the YAML-build path

Today the standalone wizard hand-rolls an `AgentPackage` YAML and posts it to `POST /api/v1/packages/install/file`. This works but introduces a code path (`build-agent-package.ts`) that exists only because direct `POST /api/v1/tenant/agents` did not historically accept the full `definitionJson`. Post-ADR-0038 the direct endpoint accepts the structured shape; the hand-rolled YAML adds no value over a direct POST and creates a divergence with the unit-tab inline-create flow (which already uses direct POST). **The redesign moves the scratch path off the package pipeline onto the direct endpoint.** The from-package path remains on `POST /api/v1/packages/install`.

This collapses three install pipelines into two:
- Direct: scratch → `POST /api/v1/tenant/agents` (+ membership wiring is implicit when `unitIds` has entries; empty means tenant-parented top-level agent).
- Catalog: from-package → `POST /api/v1/packages/install` (+ post-install membership wiring loop, same as unit wizard).
- File-upload: gone for agents. The wizard-as-package YAML synthesis is removed; the `/api/v1/packages/install/file` endpoint stays for actual user-supplied `.spring.yaml` files (CLI `spring package install ./pkg.yaml`).

### 2.3 Configuration fields (parity with unit-create scratch step)

Field-by-field map between the unit-create scratch step and the agent-create scratch step:

| Field | Unit-create scratch | Agent-create (target) | Wire field |
|---|---|---|---|
| Identity > name (URL-safe id) | required | **dropped** — server allocates Guid post-`#1632`; the form does not collect a name | (server-allocated `actorId`) |
| Identity > displayName | required | required | `displayName` |
| Identity > description | optional | optional | `description` |
| Identity > role | n/a | optional (agents have a role; units don't) | `role` |
| Identity > color | optional | n/a — agents do not carry a color today | (none) |
| Identity > parent picker | required (top-level vs has-parents) | optional (top-level tenant parent or ≥1 unit; multi-select) | `unitIds[]` |
| Execution > runtime | required, default `claude-code` | required, default `claude-code` (or **inherit** — see §2.4) | `definitionJson.execution.runtime` |
| Execution > model provider | required when not fixed | required when not fixed (or **inherit**) | `definitionJson.execution.model.provider` |
| Execution > model | optional | optional (or **inherit**) | `definitionJson.execution.model.id` |
| Execution > image | optional, defaults to runtime's `defaultImage` | optional, defaults to runtime's `defaultImage` (or **inherit**) | `definitionJson.execution.image` |
| Execution > hosting | optional, default `ephemeral` | optional, default `ephemeral` (or **inherit**) | `definitionJson.execution.hosting` |
| Execution > LLM credential | inline-create + override pattern | **not exposed** — agents resolve credentials through the unit they join. Surface a read-only "Resolved through {unit}: {provider} key {status}" indicator | (resolved server-side) |
| Connector | scratch step 4 | **not exposed** — unit-scoped only. Surface the chosen unit's effective binding read-only on a "Resolved environment" panel | (none on the agent body) |

The "inherit" column is the new affordance — see § 2.4.

### 2.4 The inheritance affordance — chosen pattern

Three patterns considered:

| Option | Pros | Cons |
|---|---|---|
| **A.** Single top-level `"Inherit configuration from parent unit"` checkbox, default ON. When checked, the entire Execution card collapses to a read-only summary | Simplest copy ("most of the time you want the unit's defaults"). Aligns with the issue's "default-selected toggle" wording | All-or-nothing. Operators wanting to override only `model` lose the affordance — they must toggle the master switch off and re-enter every field |
| **B.** A radio per category — `[ ] Inherit  [ ] Override` — for runtime/provider/model bundle, then a separate one for image, then for hosting | Granular at the category level; matches today's mental model where runtime+provider+model are coupled | Three radios per execution card is busy; the wizard's chrome budget is already tight |
| **C.** Per-field toggle (italic-placeholder pattern from DESIGN.md §12.6). Each field is editable; leaving it blank persists `null` and the backend resolves the parent's value at dispatch | Reuses an already-shipped, documented portal pattern (§12.6). Aligns with how the *unit* wizard already handles fields like Model when the unit has a parent. No new visual vocabulary | "Inherit" is implicit, not declarative. Operators have to know that "blank means inherit." |

**Decision: hybrid C + a top-level outline badge.** Adopt the per-field italic-placeholder pattern from DESIGN.md §12.6 verbatim — every Execution field carries an italic muted placeholder reading `"inherited from {unit-name}: {resolved-value}"`. Below the field, the help copy duplicates the resolved value with `data-testid="inherit-indicator"`. The card header carries an `Inherits` outline badge that flips to `Configured` once any override is persisted. **No master "Inherit everything" toggle.**

Rationale:
- The DESIGN.md §12.6 pattern already exists, has tests, and is the portal's canonical idiom for this exact problem on units. Inventing a master toggle for agents would create a divergent vocabulary for the same semantic.
- The "all-or-nothing" complaint about Option A is the reason we ruled it out — operators routinely override `model` while keeping `runtime` inherited, and a single checkbox cannot express that.
- The "inherit is implicit" complaint about Option C is mitigated by the explicit placeholder copy AND the card-header badge. Operators who skim the form see the badge before the fields; operators who read the fields see the placeholder. The discoverability cost is paid once at design-system-learn time and is reused across both the unit and agent surfaces.

The "explicit `Inherit` affordance" the issue calls for is the **placeholder + badge + help copy** combination — visible on every render, named in the help copy, and tied to the same `data-testid` as on the unit pane.

#### 2.4.1 What does "parent" mean?

Agents use the same parent model as units:

- **0 units selected:** the agent is top-level and the tenant is its parent. Execution fields inherit from tenant defaults; placeholder copy reads `"inherited from tenant defaults: claude-code"`.
- **1 unit selected:** the agent inherits from that unit; placeholder copy reads `"inherited from {unit-display-name}: claude-code"`.
- **>1 units selected:** inheritance is valid only when every selected unit resolves the same effective execution configuration. When the selected units disagree, the form must require the operator to define the agent's own execution configuration before save.

The same validation applies when an operator changes an existing agent's parent set: if the agent was inheriting and the new parents resolve different execution configurations, the platform rejects the membership change until the agent has its own execution configuration.

### 2.5 Unit-tab dialog: header copy and unit display

| Issue's complaint | Fix |
|---|---|
| Dialog title `"Add agent to unit"` | Change to `"Create agent in {unit-display-name}"`. Picker mode is gone; the title declares creation directly |
| Description `"Choose an agent and configure how it behaves inside e46903ed…"` (raw Guid) | Change to `"This agent will be registered in {unit-display-name} and inherits its execution defaults. Override below if needed."`. The display name is the unit row's `displayName` (already loaded by the parent — `unit.displayName` from `useUnit(unitId)` or, in the Agents tab's case, the parent unit detail pane already has the unit row in scope) |
| `"No agents available to add"` empty state | Removed — no picker, no empty state |
| `"+ New agent"` link | Removed — the dialog *is* the create form |

Resolved-environment strip below the title:

```
Unit:      Engineering Team
Address:   unit://e46903edab9c4bb28096609fa416a2b9
Connector: GitHub (read-only — managed at the unit level)
Provider:  Anthropic (resolved from tenant default)
```

The strip is non-editable. It is the operator's confirmation that they are creating into the right unit and that the inherited environment is what they expect.

### 2.6 Standalone page: parity with the dialog

`/agents/create` becomes a thin wrapper around the same component. Differences:

- Breadcrumbs: `Dashboard > Agents > New agent` (today's wizard already has these).
- The Source step (catalog / browse / scratch) is the page's first step; the dialog skips it and lands directly on scratch (with a small `From package…` link in the dialog header that pivots to a package-picker substep on click — operators in the Agents-tab context who want a packaged agent get the same path without leaving the dialog).
- The parent-assignment widget shows the full multi-select, not a fixed single-unit row; leaving it empty creates a top-level tenant-parented agent.
- A persisted snapshot (sessionStorage) protects the form across reload — the unit-create wizard already has `wizard-persistence.ts`; reuse that pattern with a new key (`spring.agent-create.v1`).

---

## 3. Mockups

### 3.1 Unit-tab create dialog — full state, scratch path

```
┌────────────────────────────────────────────────────────────────────────┐
│  Create agent in Engineering Team                                  [X] │
│  This agent will be registered in Engineering Team and inherits its    │
│  execution defaults. Override below if needed.                         │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Unit:      Engineering Team                                           │
│  Address:   unit://e46903edab9c4bb28096609fa416a2b9                    │
│  Connector: GitHub (managed at the unit level)                         │
│  Provider:  Anthropic (resolved from unit default)                     │
│                                                                        │
│  ─────────────────────────  Identity  ──────────────────────  [Inherits]│
│                                                                        │
│  Display name *           ┌──────────────────────────────────────┐     │
│                           │ Ada Lovelace                         │     │
│                           └──────────────────────────────────────┘     │
│  Role (optional)          ┌──────────────────────────────────────┐     │
│                           │ reviewer                             │     │
│                           └──────────────────────────────────────┘     │
│  Description (optional)   ┌──────────────────────────────────────┐     │
│                           │                                      │     │
│                           └──────────────────────────────────────┘     │
│                                                                        │
│  ─────────────────────────  Execution  ──────────────────────  [Inherits]│
│                                                                        │
│  Agent runtime            ┌──────────────────────────────────────┐     │
│                           │ ▼ inherited from Engineering Team:   │     │
│                           │   claude-code                        │     │
│                           └──────────────────────────────────────┘     │
│                           inherited from Engineering Team: claude-code │
│                                                                        │
│  Model provider           Hidden — claude-code fixes provider to       │
│                           Anthropic.                                   │
│                                                                        │
│  Model                    ┌──────────────────────────────────────┐     │
│                           │ ▼ inherited                          │     │
│                           └──────────────────────────────────────┘     │
│                           inherited from Engineering Team: (resolved)  │
│                                                                        │
│  Container image          ┌──────────────────────────────────────┐     │
│                           │ ghcr.io/cvoya-com/claude-code-base… │     │
│                           └──────────────────────────────────────┘     │
│                           inherited from Engineering Team             │
│                                                                        │
│  Hosting                  ◯ Ephemeral   ◯ Persistent   ● Inherit       │
│                                                                        │
├────────────────────────────────────────────────────────────────────────┤
│   From package…                              [ Cancel ]  [ Create ]    │
└────────────────────────────────────────────────────────────────────────┘
```

Notes:
- The two `[Inherits]` badges flip to `[Configured]` the moment the operator types in any field on that card. Pattern is DESIGN.md §12.6 verbatim.
- `From package…` (left of the footer) is a text-link button that pivots the dialog into the package-picker sub-mode (see § 3.4). This is the dialog's only entry point into the from-package path; from the dialog there is no Browse path (registry is page-level).
- Hosting renders as a 3-radio (Ephemeral / Persistent / Inherit) because hosting is a small closed enum where "blank means inherit" reads worse than an explicit option.

### 3.2 Standalone wizard — Source step

```
┌────────────────────────────────────────────────────────────────────────┐
│  Dashboard › Agents › New agent                                        │
│                                                                        │
│  Create a new agent                                                    │
│  Builds an agent from scratch, an installed package, or a registry     │
│  entry. Mirrors `spring agent create`.                                 │
│                                                                        │
│  ●─────────  ○─────────  ○─────────  ○                                 │
│  Source       Branch       Branch        Install                       │
│                                                                        │
│  ┌────────────────────────────────────────────────────────────────┐    │
│  │  Choose a source                                               │    │
│  ├────────────────────────────────────────────────────────────────┤    │
│  │                                                                │    │
│  │  ┌──────────────────────────────────────────────────────────┐  │    │
│  │  │ ○ ✦  Scratch                                             │  │    │
│  │  │      Define a new agent from scratch. You supply the     │  │    │
│  │  │      display name, runtime, provider, model, and unit    │  │    │
│  │  │      assignments.                                        │  │    │
│  │  └──────────────────────────────────────────────────────────┘  │    │
│  │  ┌──────────────────────────────────────────────────────────┐  │    │
│  │  │ ○ 📦 From package                                        │  │    │
│  │  │      Install an agent template from an installed package │  │    │
│  │  │      in the catalog.                                     │  │    │
│  │  └──────────────────────────────────────────────────────────┘  │    │
│  │  ┌──────────────────────────────────────────────────────────┐  │    │
│  │  │ ○ 🔍 Browse                                              │  │    │
│  │  │      Search the registry for community agent packages.   │  │    │
│  │  │      Coming soon — use the CLI for now.                  │  │    │
│  │  └──────────────────────────────────────────────────────────┘  │    │
│  │                                                                │    │
│  └────────────────────────────────────────────────────────────────┘    │
│                                                                        │
│  [ Back ]                                                  [ Next ]    │
└────────────────────────────────────────────────────────────────────────┘
```

The card layout, copy tone, and `<SourceCard>` shape are inherited from `/units/create` (DESIGN.md §12.4-area + the existing wizard component). The branch step counts:
- `scratch → 4 steps` (Source → Identity → Execution → Install).
- `from-package → 3 steps` (Source → Package → Install). No Connector step — agent packages don't bind to a unit-level connector at the agent level.
- `browse → 2 steps` (Source → Browse stub).

### 3.3 Inherit affordance — close-up of one field

Reusing the `data-testid="inherit-indicator"` shape from DESIGN.md §12.6:

```
Agent runtime
┌───────────────────────────────────────────────────────────────┐
│ ▼ inherited from Engineering Team: claude-code                │   <-- italic placeholder
└───────────────────────────────────────────────────────────────┘
inherited from Engineering Team: claude-code                       <-- help copy below; data-testid="inherit-indicator"
```

Once the operator picks a value, the placeholder is replaced and the help copy disappears. The card header's `[Inherits]` badge flips to `[Configured]`.

For 0-unit and >1-unit cases:

```
0 units selected (top-level tenant-parented agent):
▼ inherited from tenant defaults: claude-code
inherited from tenant defaults: claude-code

>1 unit selected with matching resolved config:
▼ inherited from selected units: claude-code
inherited from selected units: claude-code

>1 unit selected with conflicting resolved config:
⚠ Selected units resolve different execution configs.
Define this agent's execution config before saving.
```

### 3.4 From-package state — dialog and page

Dialog (Agents tab) after the operator clicks "From package…" in the footer:

```
┌────────────────────────────────────────────────────────────────────────┐
│  Create agent in Engineering Team — from package                  [X]  │
│  Pick a package containing an agent template. The package's defaults   │
│  are applied; required connector bindings are resolved below.          │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Package                                                               │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  ◉ spring-voyage/software-engineering                            │  │
│  │     Reference package — bundles a coding agent + skill set.      │  │
│  │     1 unit, 4 agents, 12 skills                                  │  │
│  ├──────────────────────────────────────────────────────────────────┤  │
│  │  ○ acme/ada-reviewer                                             │  │
│  │     Custom reviewer agent.                                       │  │
│  │     0 units, 1 agent, 0 skills                                   │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  Required connectors                                                  │
│  GitHub *                ┌──────────────────────────────────────┐      │
│                          │ cvoya-com/spring-voyage              │      │
│                          └──────────────────────────────────────┘      │
│                                                                        │
├────────────────────────────────────────────────────────────────────────┤
│   ← Back to scratch                          [ Cancel ]  [ Install ]   │
└────────────────────────────────────────────────────────────────────────┘
```

Notes:
- Package list comes from `GET /api/v1/packages` (existing endpoint), filtered client-side to entries with `agentTemplateCount > 0`.
- Connector requirements reuse the unit-create wizard's package-install connector affordance. Legacy package inputs still pass through the package-install API when a package declares them, but visible copy should describe connector requirements.
- The dialog still inherits the unit context from its host: the package's agents are wired to the host unit on install. If the package declares agents that should join *other* units (`members:` block on the package manifest), the operator gets a confirmation step naming the additional unit assignments.

### 3.5 Browse state — page-only (dialog does not offer browse)

```
┌────────────────────────────────────────────────────────────────────────┐
│  ●─────────  ●─────────  ○                                             │
│  Source       Browse       (disabled)                                  │
│                                                                        │
│  ┌────────────────────────────────────────────────────────────────┐    │
│  │  🔍  Browse agent packages                                     │    │
│  ├────────────────────────────────────────────────────────────────┤    │
│  │                                                                │    │
│  │            ┌──────────────────────────┐                        │    │
│  │            │           📦              │                        │    │
│  │            │      Coming soon          │                        │    │
│  │            └──────────────────────────┘                        │    │
│  │                                                                │    │
│  │  The Spring Voyage package registry browser is not yet         │    │
│  │  available in the portal. Use the CLI to search and install    │    │
│  │  packages from the registry:                                   │    │
│  │                                                                │    │
│  │            spring package install <package-name>               │    │
│  │                                                                │    │
│  └────────────────────────────────────────────────────────────────┘    │
│                                                                        │
│  [ Back ]                                                  [Next: ✗]   │
└────────────────────────────────────────────────────────────────────────┘
```

Identical pattern to the unit-create wizard's browse stub (line 1844). The Next button stays disabled (`data-testid="browse-coming-soon"`).

### 3.6 Unit-tab "Add Agent" affordance — tabs change

Today the Agents tab opens a picker dialog. The tab itself stays the same — `Add agent` button in the card header — but the click action opens `<AgentCreateDialog>` (the new shell) instead of `<MembershipDialog open mode="add">`. Edit mode for an existing membership keeps using `<MembershipDialog open mode="edit">` unchanged.

```
Card header (Agents tab)
┌──────────────────────────────────────────────────────────────────┐
│  Agents                                            [ + Add agent ]│
└──────────────────────────────────────────────────────────────────┘
                                                       └─ now opens
                                                          AgentCreateDialog,
                                                          not MembershipDialog
```

---

## 4. CLI parity spec — `spring agent create`

### 4.1 Today's flag set (audit)

From `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs:205-376`:

| Flag | Required | Description |
|---|---|---|
| `<id>` (positional) | yes | Agent identifier (sent as the server's `Name` field) |
| `--name <displayName>` | no | Human-readable display name (defaults to id) |
| `--role <role>` | no | The agent role |
| `--unit <id>` (repeatable) | **yes (≥1)** | Unit Guids to assign (repeatable; ≥1 required) |
| `--definition-file <path>` | no | Path to a JSON file containing the agent definition document |
| `--definition <inline>` | no | Inline JSON literal for the agent definition |
| `--image <ref>` | no | Container image (shorthand for `execution.image`) |
| `--container-runtime <docker\|podman>` | rejected at parse | Legacy ADR-0039 §7 — container engine is platform/host configuration |
| `--runtime <id>` | no | Agent runtime id (shorthand for `execution.runtime`) |
| `--model-provider <id>` | no | Model-provider id (shorthand for `execution.model.provider`) |
| `--model <id>` | no | Model id (shorthand for `execution.model.id`) |
| `--agent <…>` | rejected at parse | Legacy ADR-0038 §7 — rejected with a migration message |

### 4.2 Target flag set after this redesign

| Flag | Required | Behaviour |
|---|---|---|
| `<id>` (positional) | **removed** | v0.1 is allowed to take breaking CLI changes. The server already allocates the stable Guid; display identity comes from `--name`. |
| `--name <displayName>` | yes | Human-readable display name |
| `--description <text>` | **new** | Mirrors the form's Description field. Lands as `description` on `CreateAgentRequest` |
| `--role <role>` | no | Unchanged |
| `--unit <id>` (repeatable) | no | Optional parent assignment. Omit to create a top-level tenant-parented agent; repeat to join multiple units. |
| `--from-package <ref>` | **new** | Mutually exclusive with `--definition` / `--definition-file` and the execution shorthand flags. Routes through `POST /api/v1/packages/install`. |
| `--connector <binding>` (repeatable) | **new (paired with `--from-package`)** | Supplies required connector bindings using the same syntax as `spring package install --connector`. Errors when used without `--from-package`. |
| `--input <key=value>` (repeatable) | **new (paired with `--from-package`)** | Supplies legacy non-connector package inputs when a package still declares them. UI copy should prefer connector requirements. Errors when used without `--from-package`. |
| `--inherit` | **new** | Boolean. When set, omits every execution shorthand flag from the wire body — even ones with defaults — so the agent inherits all execution config from its parent source at dispatch. Mutually exclusive with the execution-shorthand flags (collision triggers a parse-time error with a migration message) |
| `--definition-file <path>` | no | Unchanged. Mutually exclusive with `--from-package`/`--inherit` |
| `--definition <inline>` | no | Unchanged. Mutually exclusive with `--from-package`/`--inherit` |
| `--image <ref>` | no | Unchanged |
| `--runtime <id>` | no | Unchanged |
| `--model-provider <id>` | no | Unchanged |
| `--model <id>` | no | Unchanged |

`--container-runtime` is removed from `spring agent create`. The container engine is platform/host configuration, not an agent-create choice.

### 4.3 Inheritance default in the CLI

The form's default-on-inherit mental model has to translate to the CLI without breaking existing scripts. The chosen rule:

- **Absence of an execution shorthand flag = inherit** (today's silent behaviour, restated explicitly in `--help`).
- `--inherit` is sugar for "omit all execution shorthand fields." It exists for two reasons: (1) declarative parity with the portal's per-field inherit semantics (you can write the same intent both ways), and (2) self-documenting scripts — `spring agent create --name ada --unit eng --inherit` reads cleanly, where `spring agent create --name ada --unit eng` reads as "did the operator forget to set runtime?"
- With no `--unit`, inherited fields resolve from tenant defaults. With multiple `--unit` values, inheritance is accepted only when the selected units resolve the same effective execution configuration; otherwise the server requires an explicit agent execution configuration.
- The CLI does **not** support per-field inherit on the command line (e.g. there is no `--runtime inherit` keyword). Per-field inherit on the CLI is achieved by simply not passing the flag. The portal's per-field UX is a UX-only affordance; the wire is the same in both directions.

### 4.4 From-package on the CLI

```
spring agent create \
    --name ada-reviewer \
    --unit engineering-team \
    --from-package spring-voyage/software-engineering \
    --connector github=owner/repo@installation-id
```

Mirrors `spring package install <ref>` but scoped to "install the agents declared by this package." Implementation detail: the CLI fans out to the existing `POST /api/v1/packages/install` endpoint with the package name, connector bindings, and any legacy inputs, then assigns the resulting agent(s) to `--unit` when units were provided. If no `--unit` is supplied, resulting agents are top-level and tenant-parented. When the package declares its own unit assignments, the CLI prints a dry-run summary and asks for confirmation unless `--yes` is passed.

### 4.5 Browse on the CLI

`spring package search <query>` already exists for the registry browse use case; no new flag on `spring agent create`.

---

## 5. Cross-portal / cross-CLI test matrix

The implementing PR(s) must add scenarios under `tests/cli-scenarios/` and corresponding Playwright e2e specs:

| Scenario | CLI command | Portal flow | Wire path |
|---|---|---|---|
| Create-from-scratch with full overrides | `spring agent create --name ada --unit eng --runtime claude-code --model-provider anthropic --model claude-opus-4-7 --image ghcr.io/…` | Standalone wizard, scratch path, all fields filled | `POST /api/v1/tenant/agents` |
| Create-from-scratch with full inherit | `spring agent create --name ada --unit eng --inherit` | Unit-tab dialog, no field touched | `POST /api/v1/tenant/agents` (no `definitionJson`) |
| Create-from-scratch with partial override | `spring agent create --name ada --unit eng --model claude-opus-4-7` | Unit-tab dialog, only Model overridden | `POST /api/v1/tenant/agents` (`definitionJson.execution.model.id` only) |
| Create top-level tenant-parented agent | `spring agent create --name ada --inherit` | Standalone wizard, no parent units selected | `POST /api/v1/tenant/agents` (`unitIds: []`, no `definitionJson`) |
| Reject conflicting multi-parent inherit | `spring agent create --name ada --unit eng --unit support --inherit` when the units resolve different configs | Standalone wizard, two conflicting parent units selected and no explicit execution config | validation error |
| Create-from-package | `spring agent create --name ada --unit eng --from-package spring-voyage/software-engineering --connector github=owner/repo@installation-id` | Standalone wizard, from-package path | `POST /api/v1/packages/install` |
| Reject mutually exclusive flags | `spring agent create --inherit --runtime claude-code` | n/a (UI cannot express the conflict) | parse-time CLI error |

The inherit scenarios are parity-critical because the parent model now includes tenant-parented agents and multi-parent validation. Existing CLI scenarios that expect a positional `<id>`, `--container-runtime`, or a required `--unit` must be updated or removed as part of the v0.1 breaking cleanup.

---

## 6. Open questions and cross-dependencies

### 6.1 ADR-0039 — units are agents (architectural prerequisite)

[ADR-0039](../../decisions/0039-units-are-agents.md) is the architectural prerequisite this design assumes. The relevant decisions:

- **A unit is an agent.** Same address shape, same mailbox, same execution config, same runtime path. The only structural difference is that a unit has children. There is no separate "unit-as-router" or "unit-as-agent" mode; there is no creation-time toggle to declare what a unit "is."
- **Orchestration is runtime behaviour.** No `IOrchestrationStrategy`, no `LabelRoutingPolicy`, no `unit.orchestration:` block on the manifest, no orchestration HTTP endpoint. Workflow / AI routing / hybrid routing / direct response are runtime-image and instruction concerns.
- **Children are exposed as orchestration tools — through two surfaces.** When a unit has children, the platform exposes a closed set of orchestration tools (`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`, `query_child_status`) via (a) the runtime's tool-call surface (MCP / env-var-keyed, for LLM-driven runtimes) and (b) a typed SDK over an HTTP callback API (`Cvoya.Spring.AgentSdk`, for workflow-driven runtimes). Both surfaces share the same handlers and emit the same `OrchestrationDecision` events. The image author chooses which fits.
- **Orchestration decisions are first-class evidence.** Every delegation tool call records an `OrchestrationDecision` event. The shape is in ADR-0039 §4; the GitHub label-roundtrip subscriber rewrites against this shape.
- **Inheritance generalises.** Top-level agents are tenant-parented (same as top-level units). A multi-parent agent that leaves any execution field inherited must have every parent resolve identical effective config; otherwise the platform rejects with a structured 422 naming the diverging field. Reparenting revalidates the same rule.
- **Container-runtime is platform configuration.** Operators do not pick podman vs docker. The selector is removed from CLI and portal.

Consequences for this design:

1. **No create-time unit mode toggle.** Units already are agents; the form needs no "create as unit-agent" path. Operators who want a composite agent create a unit (an agent with children); operators who want a leaf participant create an agent.
2. **One execution config per unit.** A unit's execution config is both the unit's own runtime config and the default inherited by children. There is no separate "orchestration runtime" or "self-execution runtime" field; there is one `(runtime, model, image, hosting)` per the ADR-0038 shape.
3. **No orchestration policy surface anywhere.** The agent-create form has no orchestration field, no strategy picker, no routing toggle. The unit-create wizard's strategy step is removed by ADR-0039 PR 6 (see [`units-are-agents.md`](../../plan/v0.1/units-are-agents.md)).
4. **Packages install what they declare.** If an agent-create from-package path selects a package that declares units, the package result is unit creation, not "agent creation." The summary panel and success copy must be manifest-derived.
5. **Multi-parent inheritance is enforced server-side.** This design's inheritance UX surfaces the conflict; the validation lives in `IExecutionConfigInheritanceResolver` (added in plan PR 2, wired in plan PR 3). The form does not own the rule; it surfaces it.

### 6.2 Multi-parent inheritance validation

When the operator selects >1 unit and leaves execution fields inherited, the backend must resolve each selected unit's effective execution configuration before accepting the agent create/update. If every parent resolves the same config, the agent may inherit. If any parent differs, the request is rejected unless the agent provides its own execution configuration.

The same rule applies to parent changes on existing agents. A membership update that would move an inheriting agent under conflicting parents is rejected until the agent defines its own execution config.

### 6.3 Description field on `CreateAgentRequest`

The schema declares `description: string` (non-optional). The portal's `buildCreateAgentRequest` defaults to empty string when the operator leaves the field blank. The CLI today does not collect description and sends `""`. The redesign adds `--description` to the CLI (above) and a description input to the form. **No backend change needed.**

### 6.4 Container runtime vs agent runtime — naming

ADR-0038 split the two: `execution.containerRuntime` (docker/podman) is the host-level container engine; `execution.runtime` is the agent runtime (claude-code/codex/…). The target user-facing create surfaces expose only agent runtime. `containerRuntime` is platform/host configuration and is removed from both the portal plan and `spring agent create`.

### 6.5 Image-history datalist parity

The unit-create wizard's image input is wired to `<datalist id="image-history-suggestions">` (DESIGN.md §12.13). The agent-create wizard's image input has no such suggestion list today. The redesign adds the same `<datalist>` plumbing — the localStorage key (`spring.image-history.v1`) is shared so units and agents draw from the same recently-used image references.

### 6.6 Wizard persistence

The unit-create wizard persists state in sessionStorage with schema versioning (`wizard-persistence.ts`). The agent-create wizard does not. The redesign adds equivalent persistence under `spring.agent-create.v1`. The dialog form does **not** persist — closing the dialog discards state, which matches how the unit-tab dialog works today. Operators who need persistence are expected to use the `/agents/create` page.

---

## 7. Implementation slicing

This design is implemented as **per-task issues** under the [`#1786` umbrella](https://github.com/cvoya-com/spring-voyage/issues/1786), not as a small set of coarse PRs. The fine-grained task list and dependency graph live in [`docs/plan/v0.1/units-are-agents.md`](../../plan/v0.1/units-are-agents.md). The UX-side work for this design is **Phases I–L** in the plan: 33 small tasks covering the form schema (I1–I8), the unit-tab dialog rewrite (J1–J6), the three-paths Source step (K1–K10), and the CLI agent-create parity (L1–L9).

Each task is sized for execution by a less-capable code-generation agent: concrete files, concrete deliverable, mechanically-verifiable acceptance, no architecture decisions left to the implementer. Cross-task dependencies are wired structurally on GitHub via the native `blockedBy` edge on each task issue (not stated in prose); the task picker walks the dependency graph and never starts a task whose prerequisites have not landed.

The platform-side prerequisites (delete strategy taxonomy, label-routing rewrite, container-runtime removal, multi-parent inheritance backend) are sequenced earlier in the same plan (Phases A–H, 67 tasks). Phase B's multi-parent inheritance validation (`IExecutionConfigInheritanceResolver` wired into create / update / membership endpoints) is the strict prerequisite for Phase I; phase H's positional `<id>` removal is the strict prerequisite for Phase L.

DESIGN.md updates land as task **K10** with the rest of Phase K. The `#1786` ADR direction is recorded in [ADR-0039](../../decisions/0039-units-are-agents.md), filed in this same PR.

---

## 8. Out of scope (filed as follow-ups, not in this redesign)

- **Multi-parent resolved-config preview table** (§6.2). Validation is in scope; a richer preview table is useful but cosmetic.
- **Per-field inherit on the CLI command line** (§4.3). The CLI's inheritance model is "absence = inherit," which already works; per-flag inherit is not worth the parse-time complexity for v0.1.
- **Agent connector binding.** Connectors stay unit-scoped. If a future ADR moves connector binding to the agent level, this design bends to it without breaking the form schema.
- **Drag-target agent assignment from the unit tree.** Out of scope; mentioned only because operators sometimes want it as a substitute for the picker we are deleting.

---

## 9. References

- Issue: `#1763` — "Align agent creation flow with unit creation: configuration, inheritance, and multi-path entry"
- Architecture umbrella: `#1786` — Design: unit-as-agent vs unit-as-router (recorded in ADR-0039)
- [ADR-0039](../../decisions/0039-units-are-agents.md) — units are agents (orchestration is runtime behaviour, not platform configuration); the architectural prerequisite this design assumes
- [ADR-0038](../../decisions/0038-agent-runtime-and-model-provider-split.md) — runtime / image / provider / model split (the field semantics this design layers on)
- [ADR-0035](../../decisions/0035-package-as-bundling-unit.md) — package-install pipeline (the catalog and file-upload paths this design consolidates against)
- [Execution plan](../../plan/v0.1/units-are-agents.md) — fine-grained per-task breakdown for ADR-0039 implementation, including Phases I–L for this design's UX-side work
- `src/Cvoya.Spring.Web/DESIGN.md` §12.6 — inherit-from-parent indicator (the visual pattern this design reuses)
- `src/Cvoya.Spring.Web/DESIGN.md` §12.12 / §12.13 — create-unit wizard parent-picker and image-history datalist (the patterns the agent wizard mirrors)
- `CONVENTIONS.md` §13 — UI / CLI feature parity (the rule that gates §4 of this design)
