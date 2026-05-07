# Agent-create flow redesign — design spec

Status: Design — implementation deferred. Refs `#1763` (issue), with cross-dependencies on `#1786` (unit-as-agent vs unit-as-router) and ADR-0038 (runtime / provider / model split).

This document is the implementation contract for the redesign described in `#1763`. It audits the three creation surfaces that exist today (the unit-tab "Add Agent" dialog, the standalone `/agents/create` wizard, and `spring agent create`), describes the unified target design, and slices the work for downstream PRs. **No production code is touched in this PR — the implementation lands separately.**

---

## 1. Audit — what exists today

### 1.1 Surfaces

| Surface | Route / file | Wire path | Configuration coverage today |
|---|---|---|---|
| Unit-tab "Add Agent" dialog (picker) | `src/components/units/tab-impls/agents-tab.tsx` + `src/components/units/membership-dialog.tsx` | `POST /api/v1/tenant/agents` (via `buildCreateAgentRequest`) for inline-create; `PUT /api/v1/units/{id}/memberships/{addr}` for the picker path | **Picker mode:** agent select + per-membership (model, specialty, executionMode, enabled). **Inline-create sub-mode:** displayName + role only — runtime / provider / model / image not exposed |
| Standalone wizard | `src/app/agents/create/page.tsx` + `src/app/agents/create/build-agent-package.ts` | `POST /api/v1/packages/install/file` (assembles an `AgentPackage` YAML); per-unit `POST /api/v1/units/{id}/agents/{agentId}` after install reaches `active` | id, displayName, role, description, runtime, model provider, model id, image (no hosting; no connector — connectors are unit-scoped today), unit assignments (multi) |
| Unit-create wizard (the surface agents must mirror) | `src/app/units/create/page.tsx` (3,901 LOC; `wizard-persistence.ts`) | `POST /api/v1/packages/install` (catalog), `POST /api/v1/packages/install/file` (file/scratch), or `POST /api/v1/tenant/units` + `PUT /api/v1/tenant/units/{id}/execution` (scratch direct path); see lines 1226–1340 | Source step (catalog / browse / scratch) → branch-specific steps. Scratch covers identity, runtime, provider, model, image, hosting, color, connector binding, parent-unit picker, inline-LLM-credential entry |
| CLI | `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs` (`CreateCreateCommand`) | `POST /api/v1/tenant/agents` via the generated `ApiClient` | id, `--name` (display), `--role`, `--unit` (≥1, repeatable, required), `--definition` / `--definition-file` (JSON blob), `--image`, `--container-runtime`, `--runtime`, `--model-provider`, `--model`. Legacy `--agent` rejected at parse time per ADR-0038 §7. **No `--from-package`, no `--inherit`, no `--description`** |

### 1.2 Backend contract (`POST /api/v1/tenant/agents`)

`AgentEndpoints.CreateAgentAsync` (`src/Cvoya.Spring.Host.Api/Endpoints/AgentEndpoints.cs:718-887`) accepts `CreateAgentRequest`:

```ts
CreateAgentRequest = {
    displayName: string;       // required, validated; Guid-shaped names rejected (#1632)
    description: string;       // required (allowed empty)
    role: null | string;
    unitIds: string[];         // ≥1; entries are stable Guids (#1492 / #1629)
    definitionJson?: null | string;  // optional; persisted on AgentDefinitions.Definition
}
```

The endpoint:
- Validates `displayName`, normalises and rejects empty `unitIds`.
- Resolves every referenced unit through the directory; tenant-guards each (#745).
- Validates `definitionJson` as a JSON document if supplied; otherwise leaves the agent's definition row untouched.
- Server allocates the agent's stable Guid (`actorGuid`) — callers cannot supply `name`. Existing CLI callers may still pass `--name`; the server treats it as the display name only (the issue's "Notes" section confirms).
- Writes the membership rows + the legacy cached pointer; mirrors the first unit as `ParentUnit`.
- Persists the supplied `definitionJson` blob verbatim onto `AgentDefinitionEntity.Definition` if present.

Implication: **the redesign does not require any new endpoint.** Every value the new form collects either rides in `CreateAgentRequest` directly (display name, description, role, unit ids) or in the `definitionJson` blob (`execution.image`, `execution.runtime`, `execution.model.{provider,id}`, `execution.hosting`). The portal's `buildAgentDefinitionJson` (`src/lib/agents/create-agent.ts:116`) already produces a compatible shape; it needs to accept the structured `model: {provider, id}` field and `hosting` to reach full parity with the unit wizard.

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
| Catalog | "Install a pre-built unit from a package in the catalog. The package supplies the full definition; you only need to provide any required inputs." |
| Browse | "Search the Spring Voyage package registry for community packages. (Coming soon — use the CLI for now.)" |
| Scratch | "Define a new unit from scratch. You supply the name, execution tool, model, and optional connector binding." |

Branch step counts (`maxStepForSource`, line 159): `catalog → 4`, `browse → 2 (stub)`, `scratch → 5`. The scratch step labels are `Source → Identity → Execution → Connector → Install`.

The standalone agent wizard skips this step entirely — it is hard-wired to "scratch" and does not offer catalog/browse. The unit-tab inline-create dialog only offers a sparse "from scratch" path.

### 1.5 Cross-cutting concerns surfaced by the audit

- **Connectors are unit-scoped, not agent-scoped.** The standalone wizard does not expose a connector field — the unit-create wizard's connector step binds the connector to the *unit*. The issue lists "connector" in the configuration coverage gap, but the operationally meaningful connector binding for an agent is resolved through the unit it joins. The redesign therefore retains "connector" as a unit-level concern; the agent-create form does not grow a connector picker. It surfaces the chosen unit's effective connector binding as **read-only display** so the operator understands the resolved environment.
- **Multi-unit assignment.** The standalone wizard allows ≥1 unit; the unit-tab dialog implicitly assigns to the current unit only. The redesign retains both contracts: when launched from a unit's Agents tab, the unit is preselected and the multi-select widget collapses into a confirmation strip; when launched standalone, full multi-select is available.
- **Unit GUID rendered in the picker dialog.** Line 380 of `membership-dialog.tsx` literally interpolates `unitLabel` (a Guid) into the dialog description — the issue's specific complaint. The fix is mechanical: the parent supplies a display name, not the Guid.
- **`/agents/create` uses package-install pipeline; the inline path uses `POST /api/v1/tenant/agents` directly.** Convergence requires picking one pipeline. See § 6.

---

## 2. Target design

### 2.1 One form schema, three surfaces

A single component — `<AgentCreateForm>` — drives every surface. It owns a self-contained `useState` form, validation, runtime/provider/model resolution, and submit. Three thin shells consume it:

| Shell | Where | Defaults |
|---|---|---|
| `<AgentCreateDialog>` | Unit Agents tab; opens from the **Add Agent** button | `unitIds` preselected to `[currentUnit.id]`, multi-unit picker collapsed; the dialog header shows the unit's display name and Guid; submission closes the dialog and refreshes the memberships query |
| `<AgentCreateWizardPage>` | `/agents/create` | `unitIds` empty (multi-select required); rendered as a full page with breadcrumbs |
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
| **From package** | `POST /api/v1/packages/install` with `{packageName, inputs}` (catalog branch) — same endpoint the unit wizard uses | Full when an `AgentPackage` is selected |
| **Browse** | Stub — same "Coming soon, use the CLI" pattern as the unit wizard's browse branch | Stub only |

The unit wizard's catalog endpoint already accepts agent packages (the CLI uses it for `spring package install <agent-pkg>`); the redesign filters the package list to entries whose manifest declares an agent (`pkg.agentTemplateCount > 0` is already on `PackageSummary`). The from-package shape unifies the standalone wizard's existing two-phase pipeline (package YAML → install → membership wiring) with the catalog code path.

#### 2.2.1 Why drop the YAML-build path

Today the standalone wizard hand-rolls an `AgentPackage` YAML and posts it to `POST /api/v1/packages/install/file`. This works but introduces a code path (`build-agent-package.ts`) that exists only because direct `POST /api/v1/tenant/agents` did not historically accept the full `definitionJson`. Post-ADR-0038 the direct endpoint accepts the structured shape; the hand-rolled YAML adds no value over a direct POST and creates a divergence with the unit-tab inline-create flow (which already uses direct POST). **The redesign moves the scratch path off the package pipeline onto the direct endpoint.** The from-package path remains on `POST /api/v1/packages/install`.

This collapses three install pipelines into two:
- Direct: scratch → `POST /api/v1/tenant/agents` (+ membership wiring is implicit because `unitIds` is in the body).
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
| Identity > parent picker | required (top-level vs has-parents) | required (≥1 unit; multi-select) | `unitIds[]` |
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

#### 2.4.1 What does "parent" mean for an unassigned agent?

When the operator has selected zero units, the inheritance source is undefined — every Execution field falls back to the **tenant default** (the runtime catalogue's defaults, mediated by `IAgentDefinitionProvider`). The placeholder text adapts:

- **0 units selected:** `"inherited from tenant defaults: claude-code"` (italic).
- **1 unit selected:** `"inherited from {unit-display-name}: claude-code"`.
- **>1 units selected:** `"inherited per-unit (resolved at dispatch)"` — no concrete preview because the agent's effective config will differ across the units it joins. The `<Inherits>` badge stays; the placeholder copy declares the per-unit deferral.

This mirrors the unit-create wizard's existing handling for top-level vs. parented units.

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
- The unit-assignment widget shows the full multi-select, not a fixed single-unit row.
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
0 units selected (rare — submit is disabled when unitIds is empty):
▼ inherited from tenant defaults: claude-code
inherited from tenant defaults: claude-code

>1 unit selected:
▼ inherited per-unit (resolved at dispatch)
inherited per-unit — value will differ across the 3 units this
agent joins. Override here to apply the same value everywhere.
```

### 3.4 From-package state — dialog and page

Dialog (Agents tab) after the operator clicks "From package…" in the footer:

```
┌────────────────────────────────────────────────────────────────────────┐
│  Create agent in Engineering Team — from package                  [X]  │
│  Pick a package containing an agent template. The package's defaults   │
│  are applied; required inputs are collected below.                     │
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
│  Inputs (required *)                                                   │
│  github.repo *           ┌──────────────────────────────────────┐      │
│                          │ cvoya-com/spring-voyage              │      │
│                          └──────────────────────────────────────┘      │
│                                                                        │
├────────────────────────────────────────────────────────────────────────┤
│   ← Back to scratch                          [ Cancel ]  [ Install ]   │
└────────────────────────────────────────────────────────────────────────┘
```

Notes:
- Package list comes from `GET /api/v1/packages` (existing endpoint), filtered client-side to entries with `agentTemplateCount > 0`.
- Inputs panel reuses the existing `<CatalogInputsPanel>` from the unit-create wizard (line 1965 of `/units/create/page.tsx`). No new component.
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
| `--container-runtime <docker\|podman>` | no | Container runtime shorthand |
| `--runtime <id>` | no | Agent runtime id (shorthand for `execution.runtime`) |
| `--model-provider <id>` | no | Model-provider id (shorthand for `execution.model.provider`) |
| `--model <id>` | no | Model id (shorthand for `execution.model.id`) |
| `--agent <…>` | rejected at parse | Legacy ADR-0038 §7 — rejected with a migration message |

### 4.2 Target flag set after this redesign

| Flag | Required | Behaviour |
|---|---|---|
| `<id>` (positional) | **becomes optional** | Kept for back-compat as the `--name` shorthand. When supplied AND `--name` is also supplied, `--name` wins (current behaviour). v0.2 (issue follow-up) deprecates `<id>` in favour of `--name`. **No semantic change in this PR — the server already ignores client-supplied stable ids and allocates its own.** |
| `--name <displayName>` | yes | Required when `<id>` is omitted |
| `--description <text>` | **new** | Mirrors the form's Description field. Lands as `description` on `CreateAgentRequest` |
| `--role <role>` | no | Unchanged |
| `--unit <id>` (repeatable) | yes (≥1) | Unchanged |
| `--from-package <ref>` | **new** | Mutually exclusive with `--definition` / `--definition-file` and the execution shorthand flags. Routes through `POST /api/v1/packages/install` with `{packageName: <ref>, inputs: {…}}` |
| `--input <key=value>` (repeatable) | **new (paired with `--from-package`)** | Supplies package inputs. Errors when used without `--from-package` |
| `--inherit` | **new** | Boolean. When set, omits every execution shorthand flag from the wire body — even ones with defaults — so the agent inherits all execution config from the unit at dispatch. Mutually exclusive with the execution-shorthand flags (collision triggers a parse-time error with a migration message) |
| `--definition-file <path>` | no | Unchanged. Mutually exclusive with `--from-package`/`--inherit` |
| `--definition <inline>` | no | Unchanged. Mutually exclusive with `--from-package`/`--inherit` |
| `--image <ref>` | no | Unchanged |
| `--container-runtime <id>` | no | Unchanged |
| `--runtime <id>` | no | Unchanged |
| `--model-provider <id>` | no | Unchanged |
| `--model <id>` | no | Unchanged |

### 4.3 Inheritance default in the CLI

The form's default-on-inherit mental model has to translate to the CLI without breaking existing scripts. The chosen rule:

- **Absence of an execution shorthand flag = inherit** (today's silent behaviour, restated explicitly in `--help`).
- `--inherit` is sugar for "omit all execution shorthand fields." It exists for two reasons: (1) declarative parity with the portal's per-field inherit semantics (you can write the same intent both ways), and (2) self-documenting scripts — `spring agent create --name ada --unit eng --inherit` reads cleanly, where `spring agent create --name ada --unit eng` reads as "did the operator forget to set runtime?"
- The CLI does **not** support per-field inherit on the command line (e.g. there is no `--runtime inherit` keyword). Per-field inherit on the CLI is achieved by simply not passing the flag. The portal's per-field UX is a UX-only affordance; the wire is the same in both directions.

### 4.4 From-package on the CLI

```
spring agent create \
    --name ada-reviewer \
    --unit engineering-team \
    --from-package spring-voyage/software-engineering \
    --input github.repo=cvoya-com/spring-voyage \
    --input github.token-secret=engineering/github-token
```

Mirrors `spring package install <ref>` but scoped to "install only the agents declared by this package, into the specified unit." Implementation detail: the CLI fans out to the existing `POST /api/v1/packages/install` endpoint with the package name + inputs, and assigns the resulting agent(s) to `--unit` using the same post-install loop the portal wizard runs. The server's package activator already handles the unit assignment when the manifest's `members:` block is empty (no follow-up units to wire); when the package declares its own unit assignments, the CLI prints a dry-run summary and asks for confirmation unless `--yes` is passed.

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
| Create-from-package | `spring agent create --name ada --unit eng --from-package spring-voyage/software-engineering --input github.repo=…` | Standalone wizard, from-package path | `POST /api/v1/packages/install` |
| Reject mutually exclusive flags | `spring agent create --inherit --runtime claude-code` | n/a (UI cannot express the conflict) | parse-time CLI error |
| Reject empty `--unit` list | `spring agent create --name ada` | UI submit button stays disabled | parse-time CLI error |

The first three scenarios are the new parity-critical ones; the existing CLI scenarios already cover today's flag set and stay green.

---

## 6. Open questions and cross-dependencies

### 6.1 #1786 — unit-as-agent vs unit-as-router

`#1786` asks whether a unit configured with a runtime should self-execute (instead of always routing to a member). The resolution affects this design at three points:

1. **What "inherit from parent" means for the agent.** Today `IAgentDefinitionProvider` resolves inherited fields against the parent unit's *unit-level* execution config, which is configured today via the unit-create scratch step. If `#1786` resolves to "a unit with a runtime *is* an agent and serves directly," the unit's `execution.runtime` field becomes simultaneously the inheritance source for member agents AND the unit's own runtime. The placeholder copy `"inherited from Engineering Team: claude-code"` becomes ambiguous — does the inherited value come from the unit-as-router config (the routing model, today's behaviour) or from the unit-as-agent config (the unit's own runtime, post-`#1786`)? Most likely the post-`#1786` design will say "the unit only has *one* runtime field, and member agents inherit it" — but if the design instead bifurcates the field, the agent-create form needs to know which one to display in the placeholder. **Action:** re-validate the placeholder copy against the `#1786` ADR's chosen direction before implementation lands.

2. **Whether a "convert this agent to a unit-agent" affordance belongs in the create form.** If the post-`#1786` unit-create wizard exposes a "this unit is itself an agent" toggle, the agent-create surface should be honest about *not* being that toggle — creating an agent and creating a unit-as-agent are different operations even if the resulting wire artefacts overlap. **Action:** the agent-create form does not grow a "create as unit-agent" path. Operators wanting a unit-as-agent go through the unit-create wizard.

3. **Whether the from-package path needs to handle agent-as-unit packages.** Some packages declare a unit-as-agent shape (a unit with a runtime + a degenerate `members: []`). When the operator picks such a package from the agent-create form, what's installed — an agent or a unit-as-agent? The cleanest answer is "the package installs whatever it declares; the agent-create form is the entry point but not the contract author." But the form's success message has to be honest about the result — "Agent created" lies if the package actually created a unit-as-agent. **Action:** flag the package's manifest shape on the from-package summary panel, and use a manifest-derived success message.

The redesign of `#1763` does **not** block on `#1786`. The chosen affordances (per-field inherit, three-paths source step, single create form) are robust to either resolution. The three points above are integration concerns the implementing PR should validate against the `#1786` ADR draft before merging.

### 6.2 Multi-unit assignment and per-field inherit

When the operator selects >1 unit, the per-field inherit placeholder cannot show a concrete value (different units may resolve different runtimes). The current design says `"inherited per-unit (resolved at dispatch)"`, which is honest but does not let the operator preview what the agent will run. Two follow-up moves to evaluate later:

1. A `[Resolve preview]` button that resolves the agent's effective config against each selected unit and renders a small per-unit table. Hits the existing `IAgentDefinitionProvider.ResolveAsync` path; needs an API endpoint.
2. A constraint that warns when the selected units would resolve different runtimes. (Today this is silent; the agent's effective config is just whatever the unit-of-dispatch resolves.)

Filing these as follow-ups (not PR-scope).

### 6.3 Description field on `CreateAgentRequest`

The schema declares `description: string` (non-optional). The portal's `buildCreateAgentRequest` defaults to empty string when the operator leaves the field blank. The CLI today does not collect description and sends `""`. The redesign adds `--description` to the CLI (above) and a description input to the form. **No backend change needed.**

### 6.4 Container runtime vs agent runtime — naming

ADR-0038 split the two: `execution.containerRuntime` (docker/podman) is the host-level container engine; `execution.runtime` is the agent runtime (claude-code/codex/…). The CLI carries both as separate flags. The portal's standalone wizard exposes `--runtime` (agent runtime, line 671) but not `--container-runtime`. The redesign keeps the portal's surface narrow — `containerRuntime` is operator/host configuration and out of scope for the agent-create form per CONVENTIONS.md §13's operator carve-out.

### 6.5 Image-history datalist parity

The unit-create wizard's image input is wired to `<datalist id="image-history-suggestions">` (DESIGN.md §12.13). The agent-create wizard's image input has no such suggestion list today. The redesign adds the same `<datalist>` plumbing — the localStorage key (`spring.image-history.v1`) is shared so units and agents draw from the same recently-used image references.

### 6.6 Wizard persistence

The unit-create wizard persists state in sessionStorage with schema versioning (`wizard-persistence.ts`). The agent-create wizard does not. The redesign adds equivalent persistence under `spring.agent-create.v1`. The dialog form does **not** persist — closing the dialog discards state, which matches how the unit-tab dialog works today. Operators who need persistence are expected to use the `/agents/create` page.

---

## 7. Implementation slicing

The redesign is non-trivial but breaks cleanly into reviewable PRs. Recommendation: **four PRs**, each independently mergeable.

### PR 1 — Form schema + shared component (foundation)

- Extract `<AgentCreateForm>` from today's `app/agents/create/page.tsx`. The component owns identity, execution, and unit-assignment fields.
- Extend `buildAgentDefinitionJson` to accept structured `model: {provider, id}` and `hosting`. Today it accepts a flat shape.
- Add per-field inherit affordance (DESIGN.md §12.6 pattern). Adds `data-testid="inherit-indicator"` to every Execution field.
- The standalone page is rewired to consume `<AgentCreateForm>` but otherwise preserves its current behaviour.
- **No CLI changes.** **No new shells.** No backend changes.

Acceptance: the standalone wizard renders identically to today (modulo the per-field inherit visuals) and round-trips through the same install pipeline. Existing tests stay green.

### PR 2 — Unit-tab dialog rewrite

- Replace `<MembershipDialog mode="add">` with `<AgentCreateDialog>` in `agents-tab.tsx`. The dialog wraps `<AgentCreateForm>` and preselects the host unit.
- `<MembershipDialog mode="edit">` stays for per-membership edit.
- Update the Agents tab's `displayNameMap` lookup to feed the dialog the unit's display name (fixes issue § Problem 2).
- Remove the picker code from `<MembershipDialog>` (or carve out a `<MembershipEditDialog>` if the cleanup is large; choose at implementation time).

Acceptance: clicking **Add agent** opens the new create dialog; the dialog shows the unit's display name; submitting creates an agent in the host unit and refreshes memberships. Picker mode is gone; e2e tests covering the picker are deleted.

### PR 3 — Three-paths Source step + from-package

- Add the Source step to `<AgentCreateForm>`. The page renders all three sources; the dialog defaults to scratch with a "From package…" link in the footer.
- Add the package picker (reuse `<CatalogInputsPanel>`) and wire `POST /api/v1/packages/install` for the from-package branch.
- Drop the `build-agent-package.ts` YAML synthesis. The scratch branch now posts directly to `POST /api/v1/tenant/agents`.
- Add the Browse stub.
- Add wizard persistence under `spring.agent-create.v1`.

Acceptance: all three paths are reachable from the page; the dialog reaches scratch + from-package. Existing scratch-path e2e specs migrate to the new wire path. Browse is the documented stub.

### PR 4 — CLI parity

- Add `--description`, `--from-package`, `--input` (repeatable), `--inherit` to `spring agent create`.
- Make `<id>` optional (tracking issue for v0.2 deprecation).
- Add mutual-exclusion validation between `--inherit` / `--from-package` / `--definition*` / execution shorthands.
- Update CLI scenarios; update `docs/cli-reference.md`.
- Update DESIGN.md to record the new "inherit" affordance under §12.6 (cross-link to the unit-pane indicator).

Acceptance: all six rows of the test matrix in §5 pass. `spring agent create --help` reads cleanly. CLI parity gap closed.

### Why four PRs and not one

- PR 1 is contained and high-confidence — extract + rewire. It unblocks the rest by establishing the schema.
- PR 2 deletes UI behaviour (picker) — a clean, reviewable surgical change that is easier to audit on its own than tangled with new step plumbing.
- PR 3 introduces wire-path changes (drop the package-install YAML synthesis) — the riskiest change, deserves its own review window.
- PR 4 is CLI-only — different code area, different review profile (CLI tests, not React tests).

Each PR ships a working portal + CLI; none leaves the tree in a broken intermediate state. Each PR's e2e and CLI scenarios stand on their own.

### What does NOT slice

- The DESIGN.md update covering the new agent-create surface lives **with PR 3** (the PR that introduces the visible new pattern). Keeping it earlier orphans the doc; keeping it later violates the "update DESIGN.md in the same PR" rule.
- `#1786` does NOT block this design or any of the four PRs. The placeholder-copy revalidation in §6.1 is a single-line change in PR 1 that the implementing engineer makes after reading the `#1786` ADR draft; if the ADR has not landed by the time PR 1 is ready, ship with the today-correct copy and update in a follow-up.

---

## 8. Out of scope (filed as follow-ups, not in this redesign)

- **Multi-unit dispatch preview** (§6.2). Useful but cosmetic; no downstream surface depends on it.
- **Per-field inherit on the CLI command line** (§4.3). The CLI's inheritance model is "absence = inherit," which already works; per-flag inherit is not worth the parse-time complexity for v0.1.
- **Agent connector binding.** Connectors stay unit-scoped. If a future ADR moves connector binding to the agent level, this design bends to it without breaking the form schema.
- **Drag-target agent assignment from the unit tree.** Out of scope; mentioned only because operators sometimes want it as a substitute for the picker we are deleting.
- **Removing `<id>` from `spring agent create` arguments entirely.** Filed as a v0.2 deprecation (CLI breaking change requires a release boundary).

---

## 9. References

- Issue: `#1763` — "Align agent creation flow with unit creation: configuration, inheritance, and multi-path entry"
- Cross-dependency: `#1786` — unit-as-agent vs unit-as-router
- ADR-0038 — runtime / image / provider / model split (the field semantics this design layers on)
- ADR-0035 — package-install pipeline (the catalog and file-upload paths this design consolidates against)
- `src/Cvoya.Spring.Web/DESIGN.md` §12.6 — inherit-from-parent indicator (the visual pattern this design reuses)
- `src/Cvoya.Spring.Web/DESIGN.md` §12.12 / §12.13 — create-unit wizard parent-picker and image-history datalist (the patterns the agent wizard mirrors)
- `CONVENTIONS.md` §13 — UI / CLI feature parity (the rule that gates §4 of this design)
