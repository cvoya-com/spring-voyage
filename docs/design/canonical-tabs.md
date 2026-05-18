# Canonical tab catalog for Tenant, Unit, Agent, and Human

> **Status:** Design — gates the implementation sub-issues under [#2252](https://github.com/cvoya-com/spring-voyage/issues/2252).
> **Issue:** [#2261](https://github.com/cvoya-com/spring-voyage/issues/2261).
> **Audience:** the implementation agents executing #2254 (Config), #2255 (Policies), #2256 (Messages), #2257 (Memory), #2258 (Overview); the reviewer of those PRs.
> **Related:** [`src/Cvoya.Spring.Web/DESIGN.md`](../../src/Cvoya.Spring.Web/DESIGN.md) § 9 — Tab catalog (visual contract).

This document is the canonical structure the Explorer's per-subject Detail Pane must converge on. It is **not** an ADR — it captures a UX/structure decision scoped to v0.1, not a durable cross-cutting architecture rule. When the per-tab sub-issues land, the resulting `UNIT_TABS` / `AGENT_TABS` / `TENANT_TABS` arrays in `src/Cvoya.Spring.Web/src/components/units/aggregate.ts` are the runtime source of truth; this doc is the rationale.

---

## 1. Goal and non-goals

### Goal

A user moving between Tenant, Unit, and Agent detail panes sees a **familiar tab layout** — the same conceptual tab in the same position, with the same control inside, with the same content groupings — wherever the tab conceptually applies to that subject. Settings that exist at multiple scopes (cloning policy, budgets, tenant-default credentials, agent-scope secret overrides) have **one canonical home**; every other surface that exposes them embeds or links to that home rather than re-implementing the form.

Alignment is **structural, not visual**. We are not reskinning anything. We are converging the **order**, the **component**, and the **content** of conceptually-shared tabs.

### Non-goals

- **No new configuration options.** Every setting reachable today must remain reachable; nothing new ships under this umbrella.
- **No hidden options.** If alignment would force a setting to disappear, alignment yields and the option keeps its current surface. Variances are documented in § 4 / § 5.
- **No visual reskin.** Token catalog, typography, spacing, button styling all stay as `src/Cvoya.Spring.Web/DESIGN.md` defines them today. Tab-strip chrome is unchanged.
- **No backend wiring changes.** Endpoints are not renamed, merged, or moved. Hooks may need to accept a `kind` discriminator (the Activity tab unification under #2253 already does this); they do not gain new wire shapes.
- **No subject-unique tab removal.** Skills, Traces, Clones, Deployment (Agent), and Agents-list (Unit), Budgets (Tenant) all keep their content. They get repositioned within the canonical order; they are not bolted on at the end and they are not deleted.
- **Subject-unique sub-tab ordering inside a canonical tab is not touched** unless a sub-issue's scope explicitly calls for it.
- **No forcing tabs onto a subject the concept doesn't apply to.** Tenant is not an agent and does not participate in threads; tabs whose purpose is bound to thread participation (Messages) or to composing thread participants (Agents) do **not** apply to Tenant. Alignment converges the canonical *order* across subjects, but a slot only renders for a subject when the concept genuinely applies to that subject. We do not add stub or deep-link content to Tenant merely to keep every column populated.
- **Human is a fourth subject — foundation landed under #2266 / #2267.** The platform already models humans (`HumanActor`, `human:` address scheme, `/api/v1/tenant/units/{id}/humans/{humanId}/permissions`). Humans implement `IMessageReceiver` only (per [`docs/architecture/infrastructure.md`](../architecture/infrastructure.md)) — they participate in threads but do not have expertise, activity, capabilities, runtime execution, memory, skills, traces, clones, budgets, or policies as agents do. The Explorer's portal-side `NodeKind` was extended to include `Human` under #2266, and the Overview tab body landed under #2267. The Messages (#2268) and Config (#2269) tab bodies follow as Portal Wave B PRs; in the meantime the registry's `<TabPlaceholder>` renders the deferred-tab copy. See § 4 for the matrix column.

---

## 2. Audit summary

What is reachable today, where it lives, and which canonical tab it maps to (forward reference to § 5 / § 6). No source paste — paths only.

### 2.1 Tab catalogs and ordering today

Source: `src/Cvoya.Spring.Web/src/components/units/aggregate.ts`.

| Subject | Visible tabs (in render order) | Overflow |
|---|---|---|
| Tenant | Overview, Activity, Policies, Budgets, Memory | — |
| Unit | Overview, Agents, Activity, Messages, Memory, Policies | Config |
| Agent | Overview, Activity, Messages, Memory, Skills, Traces, Clones, Policies, Config, Deployment | — |

Observed misalignment:

- **Memory vs. Policies order flips.** Unit puts Memory before Policies (positions 5 / 6); Tenant puts Policies before Memory (positions 3 / 5 with Budgets between). Agent puts Memory before Policies but separates them by four agent-only tabs.
- **Config visibility flips.** Unit hides Config in overflow; Agent surfaces Config inline at position 9 of 10; Tenant has no Config tab at all today, even though it has tenant-scoped configuration (tenant-default credentials, tenant cloning policy, tenant budget) reachable only from `/settings`.
- **The Unit `Agents` tab has no analogue** on Agent (correct — agents do not contain agents) or Tenant (correct under the § 1 principle — Tenant does not compose thread participants; the tenant root's Overview-grid of top-level units is the right surface, and the Explorer tree already surfaces every agent in the tenant).
- **"Agent-only" tabs are mislabelled.** Skills, Traces, and Deployment exist only on Agent today, but a unit *is* an agent (see [units-vs-agents](../concepts/units-vs-agents.md)) — they apply identically to Unit. Today's `unit-skills` / `unit-traces` / `unit-deployment` absence is a portal-side gap, not a domain-model one. Clones is the only legitimately agent-only tab (units cannot be cloned today).
- **Config sub-tabs diverge.** Unit Config has Boundary / Execution / Connector / Skills / Secrets / Expertise; Agent Config has Execution / Budget / Expertise / Debug. Each subject is missing sub-tabs the other has even though the underlying settings apply to both.
- **Tenant has a Memory tab.** A static empty state with no v0.1 endpoint. The placeholder is removed under this design — tenant does not have memory and will not (memory is what thread participants accumulate; Tenant doesn't participate in threads, see § 1 principle).

### 2.2 Per-subject tab files

Source: `src/Cvoya.Spring.Web/src/components/units/tabs/`.

Each row registers exactly one `(kind, tab)` pair via `registerTab` and uses a `useFoo()` hook plus a `kind` runtime guard.

| File | Pair | Notes |
|---|---|---|
| `tenant-overview.tsx` | Tenant × Overview | Renders a grid of top-level `<UnitCard>` for the tenant's children. |
| `tenant-activity.tsx` | Tenant × Activity | Tiny placeholder — only deep-links to `/analytics/throughput` + `/analytics/waits`. |
| `tenant-policies.tsx` | Tenant × Policies | Tiny placeholder — only deep-links to `/policies`. |
| `tenant-budgets.tsx` | Tenant × Budgets | Full implementation: 24h tile + 7d sparkline + Top-N units; deep-links to `/analytics/costs` and `/budgets`. |
| `tenant-memory.tsx` | Tenant × Memory | Static empty state — "tenant memory lands in v2.1." |
| `unit-overview.tsx` | Unit × Overview | Validation panel + IssuesPanel + 5 StatCards + cost sparkline + expertise card + engagement-portal link. |
| `unit-agents.tsx` | Unit × Agents | Thin wrapper around `tab-impls/agents-tab.tsx`. |
| `unit-activity.tsx` | Unit × Activity | Thin wrapper around the unified `tab-impls/activity-tab.tsx` (#2253). |
| `unit-messages.tsx` | Unit × Messages | Thin wrapper around `unit-agent-messages-view.tsx`. |
| `unit-memory.tsx` | Unit × Memory | `useMemories("unit", id)` → empty state or list. |
| `unit-policies.tsx` | Unit × Policies | Thin wrapper around `tab-impls/policies-tab.tsx`. |
| `unit-config.tsx` | Unit × Config | Sub-tabs: Boundary, Execution, Connector, Skills, Secrets, Expertise. URL-owned via `?subtab=`. |
| `unit-agent-messages-view.tsx` | (shared) | Shared body used by Unit Messages and Agent Messages. |
| `agent-overview.tsx` | Agent × Overview | Description + IssuesPanel + LifecyclePanel + cost summary card + engagement-portal link. |
| `agent-activity.tsx` | Agent × Activity | Thin wrapper around `tab-impls/activity-tab.tsx`; activity-tab adds agent-only cost cards. |
| `agent-messages.tsx` | Agent × Messages | Thin wrapper around `unit-agent-messages-view.tsx`. |
| `agent-memory.tsx` | Agent × Memory | `useMemories("agent", id)` — duplicate of unit-memory aside from the scope arg. |
| `agent-skills.tsx` | Agent × Skills | Writable list of equipped skills + add combobox. |
| `agent-traces.tsx` | Agent × Traces | Mock-fixture table — real endpoint ships in v2.1. |
| `agent-clones.tsx` | Agent × Clones | Read-only list from `useAgentClones`. |
| `agent-policies.tsx` | Agent × Policies | `AgentInitiativePanel` + `AgentCloningPolicyPanel`. |
| `agent-config.tsx` | Agent × Config | `AgentExecutionPanel` + `AgentBudgetPanel` + `AgentExpertisePanel` + collapsible debug section. |
| `agent-deployment.tsx` | Agent × Deployment | `LifecyclePanel` — same component the Overview tab embeds. |

### 2.3 Canonical tab implementations (subject-parameterised)

Source: `src/Cvoya.Spring.Web/src/components/units/tab-impls/`.

| File | Props today | Subjects that use it |
|---|---|---|
| `activity-tab.tsx` | `{ kind: "Unit" \| "Agent", id }` | Unit, Agent (unified by #2253 / #2259) |
| `agents-tab.tsx` | `{ unitId, unitDisplayName }` | Unit only |
| `boundary-tab.tsx` | `{ unitId }` | Unit only (sub-tab inside Config) |
| `connector-tab.tsx` | `{ unitId }` | Unit only (sub-tab inside Config) |
| `execution-tab.tsx` | `{ unitId }` | Unit only (sub-tab inside Config) |
| `policies-tab.tsx` | `{ unitId }` | Unit only |
| `secrets-tab.tsx` | `{ unitId }` | Unit only (sub-tab inside Config) |
| `skills-tab.tsx` | `{ unitId }` | Unit only (sub-tab inside Config) |

### 2.4 Tab-impls under `components/agents/tab-impls/`

Source: `src/Cvoya.Spring.Web/src/components/agents/tab-impls/`.

These are stored under `agents/` today but, per [`docs/concepts/units-vs-agents.md`](units-vs-agents.md) — a unit *is* an agent — they apply to both subjects under alignment. Post-alignment they are re-parameterised by subject `{ kind, id }` and re-homed under `components/units/tab-impls/` (or kept in place and renamed if the path move is non-trivial).

| File | Notes |
|---|---|
| `execution-panel.tsx` | `AgentExecutionPanel({ agentId, parentUnitId })` — overlays inherited defaults from owning unit. Used by Agent Config tab today; under alignment it also drives Unit Config → Execution. |
| `lifecycle-panel.tsx` | `LifecyclePanel({ agentId })` — deploy / undeploy / scale / logs. Used by Agent Overview (compact embed) and Agent Deployment (full surface) today; under alignment it also drives Unit × Deployment and Unit Overview's lifecycle embed. |

### 2.5 Other subject-scoped panels under `components/agents/`

Source: `src/Cvoya.Spring.Web/src/components/agents/`.

Same note as § 2.4: most of these apply to units too. **Cloning is the only one that stays agent-only**, because a unit cannot be cloned (yet). The two others (Budget and Initiative) become canonical for both subjects under alignment.

| File | Applies to | Used by |
|---|---|---|
| `agent-budget-panel.tsx` | Unit + Agent | Config → Budget sub-tab on both subjects. Mirrors `spring agent budget`. |
| `agent-initiative-panel.tsx` | Unit + Agent | Policies tab on both subjects. Mirrors `spring agent initiative`. |
| `agent-cloning-policy-panel.tsx` | **Agent only** | Agent Policies tab. Mirrors `spring agent clone policy --scope agent`. Read-only summary. Units cannot be cloned today. |

### 2.6 Settings hub — `src/Cvoya.Spring.Web/src/app/settings/`

| Path | Implementation | Conceptual home (forward ref § 6) |
|---|---|---|
| `page.tsx` (the hub) | Renders the drawer-panel registry grid + the cloning-policy card + the tile grid. | **Remains as standalone route.** |
| `skills/page.tsx` | Re-exports `@/components/admin/...` — read-only tenant skill catalog grouped by registry. Mirrors `spring skill list`. | **Remains as standalone route** (catalog). |
| `packages/page.tsx`, `packages/[name]/...` | Re-exports `@/components/admin/packages-page` / `package-detail-client`. Visibility-only per AGENTS.md admin carve-out — `spring` owns the install/configure verbs. | **Remains as standalone route** (admin / catalog). |
| `model-providers/page.tsx` | Re-exports `@/components/admin/model-providers-page` — installed providers, model catalogues, credential health. Admin carve-out. | **Remains as standalone route** (admin). |
| `system-configuration/page.tsx` | Re-exports `@/components/admin/system-configuration-page` — startup configuration report. Admin carve-out. | **Remains as standalone route** (admin). |

### 2.7 Settings panels — `src/Cvoya.Spring.Web/src/components/settings/`

Each panel is registered as a `DrawerPanel` in `src/Cvoya.Spring.Web/src/lib/extensions/defaults.tsx` and rendered as a card on `/settings`.

| File | Drawer-panel id (ordering) | Conceptual home (forward ref § 6) |
|---|---|---|
| `budget-panel.tsx` | `budget` (10) — Tenant budget editor. | **Tenant × Budgets** (canonical home). |
| `tenant-defaults-panel.tsx` | `tenant-defaults` (15) — fixed-list of tier-2 LLM credentials at tenant scope. | **Tenant × Config → Secrets** (canonical home). |
| `agent-overrides-panel.tsx` | `agent-overrides` (17) — agent picker + free-form secret CRUD at agent scope. | **Agent × Config → Secrets** (canonical home). |
| `auth-panel.tsx` | `auth` (20) — `auth/me` + API token CRUD. | **Stays on `/settings`** — not a per-subject concern. |
| `about-panel.tsx` | `about` (90) — platform version + license. | **Stays on `/settings`** — not a per-subject concern. |
| `cloning-policy-panel.tsx` | Not a `DrawerPanel`; rendered directly on `/settings` under the "Cloning" heading. | **Tenant × Policies** (canonical home). |

---

## 3. Canonical tab catalog and order

The canonical order, left to right, is:

1. **Overview** — what's the state of this subject right now? *(applies to all subjects, including Human in v0.2.)*
2. **Activity** — what has been happening / costing money? *(applies to Tenant, Unit, Agent. Human is undecided pending v0.2 design.)*
3. **Messages** — what conversations does this subject participate in? *(applies to Unit, Agent, Human; does-not-apply on Tenant — Tenant does not participate in threads. See § 1 principle.)*
4. **Memory** — what does this subject remember? *(applies to Unit and Agent; does-not-apply on Tenant — see § 4 note. Does not apply to Human — humans implement only `IMessageReceiver`.)*
5. **Agents** *(rename to "Members" pending v0.2 — see § 4.1)* — what subjects belong to this unit? *(applies to Unit; does-not-apply on Agent — an agent does not contain agents — and does-not-apply on Tenant — see § 1 principle. Does not apply to Human.)*
6. **Skills** — what capabilities is this subject equipped with? *(applies to Unit and Agent — a unit is an agent; see [units-vs-agents](units-vs-agents.md). Does not apply to Human.)*
7. **Traces** — what individual executions has this subject run? *(applies to Unit and Agent — a unit is an agent; see [units-vs-agents](units-vs-agents.md). Does not apply to Human.)*
8. **Clones** — what spawned copies of this subject exist? *(applies to Agent only — units cannot be cloned today; see [units-vs-agents](units-vs-agents.md). Does not apply to Human.)*
9. **Policies** — what guard-rails govern this subject? *(applies to Tenant, Unit, Agent. Does not apply to Human.)*
10. **Budgets** — what cost limits are set against this subject? *(applies to Tenant only today; see § 4 note.)*
11. **Config** — what does this subject's wiring look like (image, runtime, model, secrets, connector, boundary, expertise)? *(applies to Tenant, Unit, Agent. Applies to Human in v0.2 — humans have connector-routing config so messages addressed to them can be delivered via the right channel.)*
12. **Deployment** — what does this subject's runtime container look like? *(applies to Unit and Agent — a unit is an agent; see [units-vs-agents](units-vs-agents.md). Does not apply to Human.)*

### 3.1 Why this order

The order is **observation → action → constraint → wiring**:

- Slots **1–4** (Overview, Activity, Messages, Memory) are the **reading** slots: every subject has them, every subject puts them up front, every subject puts them in the same order. They are the high-frequency tabs and they should be the first thing an operator can tab to.
- Slots **5–8** (Agents, Skills, Traces, Clones) are the **composition / introspection** slots: "what is this subject made of, and what came out of it?" Skills, Traces, and Deployment (slot 12) apply to Unit + Agent because a unit *is* an agent (see [units-vs-agents](units-vs-agents.md)). Agents (slot 5) is Unit-only — units contain agents, agents do not. Clones is Agent-only — units cannot be cloned (yet). Tenant has none of these slots.
- Slot **9** (Policies) is the **constraint** slot: it sits *after* observation and composition because configuring policy is a less-frequent action than reading state. Putting Policies after Memory matches Unit's current order and pulls Tenant into line (Tenant currently surfaces Policies at position 3 — that's the misalignment we're fixing).
- Slot **10** (Budgets) is **also a constraint**, ranked after Policies because cost guard-rails are conceptually a subset of policy — you set them less frequently still. Budgets today is Tenant-only; the slot exists in the canonical order so that if per-unit / per-agent budgets ever promote to first-class tabs (today they live inside Config) they have a home.
- Slots **11–12** (Config, Deployment) are the **wiring** slots: the lowest-frequency, highest-detail surfaces. Config first because every subject has one; Deployment last because it only applies to Agent and it concerns the persistent-runtime layer that sits below configuration. Putting Config last brings Unit (overflow today) and Agent (position 9 today) into agreement.

### 3.2 Why not put Skills / Traces / Clones after Policies

Two reasons.

First, **familiarity**: a user who learns "Policies is to the right of Memory" on Unit and Tenant should see Policies to the right of Memory on Agent too. If Skills/Traces/Clones were appended after Policies, Policies would sit at different positions depending on the subject — the position of Config-vs-Policies and Policies-vs-Deployment would still diverge across subjects.

Second, **conceptual grouping**: Skills/Traces/Clones describe what the subject *is* and what came out of it. They are introspective. Putting them between the "reading" slots (Overview/Activity/Messages/Memory) and the "constraint" slots (Policies/Budgets) preserves the left-to-right reading model.

### 3.3 The Detail Pane visible/overflow split

`UNIT_TABS` / `AGENT_TABS` / `TENANT_TABS` each declare a `visible` array and an `overflow` array. The canonical *order* lives in the union of those two arrays; the `visible` / `overflow` split is a **Detail-Pane rendering hint** — visible tabs render in the primary strip, overflow tabs render in the secondary strip after a `bg-border` separator.

Unit today puts Config in overflow because the primary strip already has six visible tabs and Config is a deep editor that operators reach less often than Agents/Activity/Messages. We **keep that hint** post-alignment, and apply the same split shape to Unit as to Agent (since a unit is an agent). Concretely:

- **Unit:** Overview, Activity, Messages, Memory, Agents, Skills, Traces, Policies visible — Config, Deployment overflow.
- **Agent:** Overview, Activity, Messages, Memory, Skills, Traces, Clones, Policies visible — Config, Deployment overflow.
- **Tenant:** Overview, Activity, Policies, Budgets visible — Config overflow. Messages, Agents, Memory, Skills, Traces, Clones, Deployment do not apply to Tenant (see § 1 principle and § 4).
- **Human (foundation landed under #2266):** Overview, Messages visible — Config overflow. Activity is intentionally absent (humans don't implement `IActivityObservable`). Memory, Agents, Skills, Traces, Clones, Policies, Budgets, Deployment do not apply (humans implement only `IMessageReceiver`). Catalog locked in `HUMAN_TABS` (`src/Cvoya.Spring.Web/src/components/units/aggregate.ts`); Overview body landed under #2267; Messages and Config bodies are slot-reserved placeholders pending #2268 / #2269.

Rationale for pushing Config + Deployment to overflow on both Unit and Agent: both are deep editors / lifecycle surfaces, the activity-side cluster (Overview/Activity/Messages/Memory + the composition slots) is the high-frequency surface, and the existing `<AgentCard>` Deployment quick-action already deep-links to `?tab=Deployment`, so overflow placement does not regress that path. Unit gains Skills + Traces + Deployment slots because a unit is an agent — they apply to both subjects identically.

### 3.4 New tabs introduced versus today

This design introduces the following new tabs on subjects that don't have them today. Each is either (a) a re-home — surfacing settings reachable today elsewhere in the per-subject context — or (b) an extension of an existing agent surface to units, since a unit *is* an agent (see [units-vs-agents](units-vs-agents.md)). **No new options, no new endpoints, no new features.**

- **Tenant × Config.** Tenant today has no Config tab. The canonical structure adds one as a per-subject home for tenant-scope wiring that today is reached via `/settings`: tenant-default credentials (`tenant-defaults-panel`), tenant budget (`budget-panel`), and tenant cloning-policy summary (`cloning-policy-panel`). See § 4 for the matrix and § 5.11 for the content map.
- **Unit × Skills.** Unit gains the equipped-skills surface that Agent has today (`agent-skills.tsx`). A unit is an agent; it can be equipped with skills exactly as a leaf agent can.
- **Unit × Traces.** Unit gains the trace-list surface that Agent has today (`agent-traces.tsx`). Same fixture caveat applies in v0.1.
- **Unit × Deployment.** Unit gains the deploy/undeploy/scale/logs surface that Agent has today (`agent-deployment.tsx`). A unit is dispatched through the same runtime layer as a leaf agent; its lifecycle surface is the same.

**On the word "re-home":** the `/settings` page is **not removed**. `/settings` remains the standalone route for tenant-level admin work; the panels there keep rendering. What "re-home" means is that the same panel body also surfaces inside the per-subject Detail Pane (Tenant × Config → Secrets uses the same `<TenantDefaultsPanel>` body that `/settings` already renders), so a user inspecting the tenant in the Explorer doesn't need to jump to `/settings` to see or edit those values. There is **one canonical implementation** of each panel, used in both places. See § 6 for the full other-surface mapping.

Per the § 1 principle, the canonical order does **not** invent a Tenant × Messages, Tenant × Agents, or Tenant × Memory tab to keep every column populated. Tenant does not participate in threads (Messages), does not compose thread participants (Agents), and does not have a memory surface (the existing tenant-memory.tsx empty-state is removed under this design — see § 4.1). Those slots stay empty for Tenant in the matrix below and are not rendered in the Tenant strip.

---

## 4. Subject × tab matrix

`A` = applies; `—` = does-not-apply; `A*` = applies with a per-subject variant noted below.

| Tab | Tenant | Unit | Agent | Human |
|---|---|---|---|---|
| 1. Overview | A | A | A | A (personal info — name, email; see § 4.1 / v0.2 tracker) |
| 2. Activity | A* (deep-link to `/analytics/throughput`; cost cards may appear if a tenant-feed surface lands) | A (same control as Agent — cost cards + activity feed) | A (same control as Unit — cost cards + activity feed) | TBD (humans don't implement `IActivityObservable`; v0.2 may decide on a deep-link to thread participation history) |
| 3. Messages | — (Tenant does not participate in threads; see § 1 principle) | A | A | A (humans participate in threads; the timeline is filtered to threads they're addressed in) |
| 4. Memory | — (Tenant does not have memory; the existing tenant-memory empty-state is removed under this design) | A | A | — (humans implement only `IMessageReceiver`) |
| 5. Agents *(see § 4.1 rename note)* | — (Tenant does not compose thread participants; see § 1 principle) | A (existing — children agents, nested units, and v0.2 humans) | — (agents do not contain agents) | — |
| 6. Skills | — | A (**new tab** — equipped skills surface, same shape as Agent) | A (existing) | — |
| 7. Traces | — | A (**new tab** — trace list, same shape as Agent; v0.1 fixture) | A (existing; v0.1 fixture) | — |
| 8. Clones | — | — (units cannot be cloned today; see [units-vs-agents](units-vs-agents.md)) | A | — |
| 9. Policies | A* (renders the dimension panels via the canonical `<PoliciesTab>` component reading tenant-scope endpoints; deep-link to `/policies` for the full multi-scope view) | A* (Initiative + Cost + Model + ExecutionMode + Skill dimensions — the full canonical set) | A* (Initiative + Cloning policy only — Cost/Model/Skill are declared on owning unit, per `agent-policies.tsx` today. Initiative applies because a unit is an agent — same panel as Unit × Policies → Initiative.) | — (no platform-enforced policy surface on humans in v0.2) |
| 10. Budgets | A (canonical home for tenant budget editor + Top-N units + sparkline) | — (per-unit budgets live inside Config → Budget) | — (per-agent budgets live inside Config → Budget) | — |
| 11. Config | A* (**new tab**: Secrets sub-tab + Cloning sub-tab + Budget sub-tab — re-home from `/settings` panels) | A* (sub-tabs: Boundary, Budget, Expertise, Execution, Connector, Skills, Secrets, Debug — Budget + Debug added under alignment; existing Unit Config keeps Boundary, Execution, Connector, Skills, Secrets, Expertise) | A* (sub-tabs: Budget, Expertise, Execution, Connector, Skills, Secrets, Debug — Connector + Skills + Secrets added under alignment; existing Agent Config keeps Execution, Budget, Expertise, Debug) | A* (sub-tabs: Identity + Connector — store the human's personal info and the inbound-routing connector binding so messages addressed to this human are delivered via the right channel; see § 4.1) |
| 12. Deployment | — | A (**new tab** — same `<LifecyclePanel>` as Agent) | A (existing) | — |

### 4.1 Variance notes

- **Activity (Unit vs Agent).** Same control. Today the cost sparkline (#1363) and per-model breakdown table (#1364) were tagged "agent-only"; under alignment they apply to Unit too — a unit is an agent with a runtime, so its activity feed and cost rollup share the same shape. The canonical `<ActivityTab kind="Unit" | "Agent" id={…} />` renders identical content for both.
- **Tenant × Activity.** Today this is a near-empty deep-link card. Post-alignment it embeds the same `<ActivityTab kind="Tenant" id={...} />` shape via a thin tenant-feed adapter, or — if no tenant-level activity feed exists — keeps the deep-link card. **Implementation chooses the cheaper path**; the tab position is what aligns.
- **Tenant × Policies.** Today this is a deep-link card to `/policies`. Post-alignment it surfaces a richer tenant view (cloning-policy card, cost/model/skill summary cards) inline, with the deep-link to `/policies` preserved for the full per-unit roll-up. **The dedicated `/policies` route is not deleted** — it remains the per-unit roll-up surface; the Tenant × Policies tab is the tenant-scope summary.
- **Agent × Policies.** Preserves today's scope (Initiative + Cloning only). Cost / Model / Skill dimensions are declared on the owning unit by design — the agent-policies tab body's own header text states this. **No new editors are added** under the alignment work; this is the "alignment yields to information preservation" rule in action. Initiative on Agent uses the same panel as Unit × Policies → Initiative (because a unit is an agent).
- **Unit × Skills / Unit × Traces / Unit × Deployment (new).** Same controls as Agent — `agent-skills.tsx`, `agent-traces.tsx`, `lifecycle-panel.tsx` — re-parameterised by subject `{ kind, id }`. The "agent-" prefix in the file paths reflects history, not scope; per [units-vs-agents](units-vs-agents.md), units are agents and these surfaces apply identically.
- **Unit × Config (expanded).** Today's sub-tabs (Boundary, Execution, Connector, Skills, Secrets, Expertise) are preserved and the alignment adds Budget + Debug sub-tabs to bring it into parity with Agent. Budget mirrors Agent × Config → Budget (same `<AgentBudgetPanel>` reused); Debug mirrors Agent's collapsible debug section.
- **Agent × Config (expanded).** Today's sub-tabs (Execution, Budget, Expertise, Debug) are preserved and the alignment adds Connector + Skills + Secrets sub-tabs to bring it into parity with Unit. Connector and Skills mirror Unit × Config → Connector / Skills; Secrets embeds `<AgentOverridesPanel>` scoped to the open agent (same panel `/settings` already renders).
- **Tenant × Config (new).** Holds the three settings currently reached via `/settings`: tenant credentials (Secrets sub-tab); tenant budget editor (Budget sub-tab — Tenant × Budgets surfaces the *read-side* breakdown card; the editor is canonically inside Config); tenant cloning-policy read-only summary (Cloning sub-tab; the editor still rides `spring agent clone policy set --scope tenant`). The `/settings` page is **not removed** — its cards still render, embedding the same panel bodies the Config sub-tabs use, so there is **one canonical implementation** of each panel. See § 6 for the full mapping.

Tenant × Messages, Tenant × Agents, and Tenant × Memory do **not** appear in the matrix and are not rendered in the Tenant strip. Tenant-wide thread / agent roll-ups remain reachable via `/inbox` and the Explorer tree (which already surfaces every agent in the tenant); we do not duplicate those surfaces into the Tenant Detail Pane. The pre-existing `tenant-memory.tsx` empty-state is removed.

- **Human — Overview (#2267, landed).** Personal info card (display name, username, email, platform role, created-at) + compact 4-fact pill grid + caveat copy. The display-name row carries a "You" badge when the loaded human matches the currently-authenticated caller. No `<IssuesPanel>`, lifecycle embed, cost summary, or engagement link — humans don't have those surfaces. Memberships drill-down is deferred to v0.2 (no per-human memberships endpoint in v0.1; the v0.2 follow-up surfaces team-role rows from `unit_memberships_humans`).
- **Human — Messages (#2268, landed).** View-only timeline for threads where the human is an addressed participant. Filters `useThreads({ participant: "human:<id>" })` and renders the most-recently-active matching thread through the canonical `<ConversationView>` primitive (same control as Unit/Agent Messages and the inbox right pane). **No composer** — the Human page is an observer surface ("the operator looking at this person's threads"); there is no meaningful outbound "send to this human from their own page". The "You" hint is implicit through `<ConversationView>`'s default `layout="dialog"`: the under-view human's own bubbles align right when the conversation includes the caller. The canonical `<MessagesTab kind="Unit|Agent">` (#2256) is *not* reused — it carries a composer that doesn't apply here; instead the Human-specific tab body wires `useThreads` + `<ConversationView>` directly.
- **Human — Config (#2269, landed).** Two sub-tabs: **Identity** and **Connector**. The shell mirrors the canonical `<ConfigTab>` chrome (sub-tab strip + URL contract `?tab=Config&subtab=<name>` + `useSyncExternalStore` snapshot of the live URL) but builds its own body because the canonical `<ConfigTab>` is typed against Tenant \| Unit \| Agent — Humans have no budgets, secrets, expertise, or instructions to factor in. Identity is the default sub-tab. **Identity sub-tab**: list of `(connectorId, connectorUserId, displayHandle?)` rows from `GET /api/v1/tenant/humans/{id}/identities` (PR #2420), each row carries an inline outline-style Remove button (`<ConfirmDialog>`-guarded DELETE), and a four-control inline form (connector `<select>` from `useConnectorTypes`, connector-user-id `Input`, optional display-handle `Input`, Submit) POSTs new rows. **Connector sub-tab**: caveat-only for v0.1 — per-Human connector bindings ship in v0.2 (#2375); the body links the issue and points the operator at the owning unit's Config → Connector sub-tab for unit-scoped binding edits today. Distinct from Unit × Config → Connector, which is *outbound* (binds the unit to an external system to receive events). The human-side connector is inbound-only.
- **Unit × Agents — rename to "Members" pending v0.2.** Today the Unit × Agents tab lists agents + nested sub-units; in v0.2 it will also list humans (per `/api/v1/tenant/units/{id}/humans/{humanId}/permissions`). The label is already imprecise; v0.2 renames it to "Members" and updates the canonical order accordingly. **v0.1 keeps the existing label** to avoid a copy-only rename without the human additions; the rename rides the v0.2 tracker.

### 4.2 Why Policies/Budgets are kept distinct on Tenant

Today's `/settings` page has both a "Tenant budget" card and a "Tenant cloning policy" card. They are conceptually distinct: a budget is a hard spend ceiling that triggers enforcement and notifications; a policy is a structural constraint on what the platform can do. Folding them into one tab on Tenant would either bury the Top-N-units affordance (Budgets-only content) or bury the policy summary (Policies-only content). They get separate tabs at the canonical positions 9 and 10. Unit and Agent do not have a Budgets tab today and we do not add one — per-subject daily-budget editors stay inside Config → Budget on Unit and Agent.

---

## 5. Content map per tab

For each canonical tab, the complete content list. **Bold settings** are the canonical home for that setting; non-bold settings are embeds or summary-card renders of a control whose canonical home is elsewhere. Variants are marked `[Tenant|Unit|Agent]`. Component paths are absolute under `src/Cvoya.Spring.Web/`.

### 5.1 Overview

Subjects: Tenant, Unit, Agent.

| Content | Tenant | Unit | Agent | Canonical home |
|---|---|---|---|---|
| Description / desc-text | A | A | A | this tab |
| Issues panel (`<IssuesPanel>`) | — | A | A | this tab |
| Stat tiles (`<StatCard>` x N) | A (units/agents/cost-24h/messages-24h) | A (agents/sub-units/cost-24h/messages-24h/status) | — (cost summary card below) | this tab |
| Validation panel (when subject is in Error) | — | A (`<ValidationPanel>`) | — | this tab |
| Lifecycle panel — compact embed | — | — | A (`<LifecyclePanel>`) | **Deployment tab** (full surface); Overview embeds. |
| Cost-over-time sparkline card | A (today on Budgets — variance) | A | — | this tab on Unit; this tab on Tenant variance — see § 4.1 / § 5.10. |
| Cost summary card (totals) | — | — | A | this tab |
| Top-level units grid | A (today's `<UnitCard>` grid) | — | — | this tab |
| Expertise card (read-only) | — | A (`<UnitOverviewExpertiseCard>`) | — | **Config → General** sub-tab (expertise editor folded into General under #2331); Overview embeds a summary with "Manage" deep-link. |
| Engagement-portal link | — | A | A | this tab |

**Subject-unique:** the Lifecycle panel is canonically owned by Agent × Deployment; Agent × Overview embeds the compact variant so deploy/undeploy is one click from the landing tab. The expertise editor is canonically owned by Unit × Config → General (post-#2331; previously a standalone Expertise sub-tab); Unit × Overview embeds a read-only summary with a Manage link.

### 5.2 Activity

Subjects: Tenant, Unit, Agent.

Canonical component: `src/components/units/tab-impls/activity-tab.tsx` accepting `{ kind: "Tenant" | "Unit" | "Agent", id }`.

| Content | Tenant | Unit | Agent | Canonical home |
|---|---|---|---|---|
| Activity feed (event rows + expandable details) | A (or deep-link card to `/analytics/throughput` — see § 4.1) | A | A | this tab |
| Cost-over-time sparkline (#1363, agent-only) | — | — | A | this tab |
| Per-model cost breakdown table (#1364, agent-only) | — | — | A | this tab |

**Variance:** the Agent variant renders two extra cards above the feed. They are agent-only and we preserve them — no information loss.

### 5.3 Messages

Subjects: Unit, Agent, Human. *(Tenant does not participate in threads — see § 1 principle / § 4.1.)*

Canonical components: `src/components/units/tabs/unit-agent-messages-view.tsx` (Unit + Agent — composer-bearing) and `src/components/units/tabs/human-messages.tsx` (Human — view-only). Both wrap the shared `<ConversationView>` primitive (`src/components/conversation/conversation-view.tsx`).

| Content | Unit | Agent | Human | Canonical home |
|---|---|---|---|---|
| Inline timeline (`<ConversationView>` body) | A | A | A | this tab |
| `+ New conversation` button + modal composer | A | A | N | this tab |
| Persistent composer at bottom | A | A | N | this tab |
| Timeline filter dropdown (Messages / Full timeline) | A | A | A | this tab |

The Human surface is **view-only** (no composer) because the Human page is observer-view ("the operator looking at this person's threads"). There is no meaningful outbound recipient for sending a message *from* a human's own page. Threads are filtered by `participant=human:<id>` instead of by `unit=` / `agent=` (the server's `participant` filter parses any address form via `AddressIdentity.TryGetActorId`).

### 5.4 Memory

Subjects: Tenant, Unit, Agent.

Canonical component: a unified `<MemoryTab>` parameterised by `{ kind, id }`. Today `agent-memory.tsx` and `unit-memory.tsx` are byte-for-byte duplicates aside from the `useMemories(scope, id)` argument; `tenant-memory.tsx` is a static empty state. The unification (#2257) collapses all three into one component.

| Content | Tenant | Unit | Agent | Canonical home |
|---|---|---|---|---|
| Short-term memory list | A* (always empty in v0.1) | A | A | this tab |
| Long-term memory list | A* (always empty in v0.1) | A | A | this tab |
| Empty state ("Write API ships in v2.1") | A | A | A | this tab |

### 5.5 Agents

Subject: Unit. *(Tenant does not compose thread participants — see § 1 principle / § 4.1; Agent does not contain agents.)*

| Content | Unit | Canonical home |
|---|---|---|
| Children grid (agents + nested units, with type-pill) | A (`<AgentsTab>`) | this tab |
| `+ New agent` affordance | A (existing) | this tab |
| Membership edit dialog | A (existing) | this tab |

### 5.6 Skills

Subject: Agent.

| Content | Agent | Canonical home |
|---|---|---|
| Equipped skill chips with remove | A (`AgentSkillsTab` body) | this tab |
| Add-skill combobox seeded from `useSkillsCatalog()` | A | this tab |

The **catalog** (`/settings/skills`) stays where it is — the tab is the *editor* for one agent; the catalog is the tenant-wide read-only roll-up.

### 5.7 Traces

Subject: Agent.

| Content | Agent | Canonical home |
|---|---|---|
| Fixture table (v0.1) / real trace list (v2.1) | A | this tab |
| "These rows are a static fixture" call-out (v0.1 only) | A | this tab |

### 5.8 Clones

Subject: Agent.

| Content | Agent | Canonical home |
|---|---|---|
| Clones list (read-only) | A (`useAgentClones`) | this tab |

Editing (create/delete) rides `spring agent clone` per the existing tab comment — no portal control today, none introduced under this work.

### 5.9 Policies

Subjects: Tenant, Unit, Agent.

Canonical component: `src/components/units/tab-impls/policies-tab.tsx`. Today it accepts `{ unitId }` and renders Skill / Model / Cost / ExecutionMode / Initiative dimensions plus an Effective-policy footer. Unification (#2255) re-parameterises it as `{ kind, id }` so Tenant and Agent can reuse the dimension panels with their own scoped endpoints.

| Content | Tenant | Unit | Agent | Canonical home |
|---|---|---|---|---|
| Initiative panel | A* (tenant default) | A (`<InitiativePolicyCard>`) | A (`<AgentInitiativePanel>`) | this tab |
| Skill policy panel (allow-list / block-list) | A* (tenant default) | A | — (declared on owning unit) | this tab |
| Model policy panel (allow-list / block-list) | A* (tenant default) | A | — (declared on owning unit) | this tab |
| Cost policy panel (per-dispatch cap, etc.) | A* (tenant default) | A | — (declared on owning unit) | this tab |
| ExecutionMode policy panel | A* (tenant default) | A | — (declared on owning unit) | this tab |
| Cloning policy summary | A (read-only summary; editor rides CLI) | — | A (`<AgentCloningPolicyPanel>`) | this tab on Tenant + Agent |
| Effective-policy footer (which scope wins per dimension) | — | A | A | this tab |
| Deep-link to `/policies` for multi-unit roll-up | A | A | A | `/policies` route remains as the cross-unit roll-up. |

**Tenant variance.** Today `tenant-policies.tsx` is a 51-line placeholder. Post-alignment the tab embeds the same dimension panels via `<PoliciesTab kind="Tenant" id={tenantId} />` reading the tenant-scope endpoints. **If a tenant-scope endpoint does not exist for a dimension, that dimension's panel renders a "set via CLI" placeholder** rather than disappearing — the alignment rule does not let us hide options. The deep-link to `/policies` is preserved.

**Agent variance preserved.** Cost / Model / Skill / ExecutionMode panels are intentionally absent on Agent because the agent-policies surface today documents this: "Cost, model, and skill dimensions are declared on the owning unit." The alignment work does **not** add agent-scope editors for those dimensions.

### 5.10 Budgets

Subject: Tenant.

| Content | Tenant | Canonical home |
|---|---|---|
| 24h total cost tile | A | this tab |
| 7-day sparkline | A | this tab |
| Top-N units by 24h spend | A | this tab |
| Deep-link to `/analytics/costs` | A | this tab |
| Deep-link to `/budgets` | A | this tab |
| **Daily-budget editor** (today on `<BudgetPanel>` in `/settings`) | — (canonical home is Config → Budget; see § 5.11) | **Tenant × Config → Budget** |

**Read vs. write split.** Budgets surfaces the *read-side* (current spend, top units, trend). The *editor* — the only writable control today's `<BudgetPanel>` carries — moves to Tenant × Config → Budget. Rationale: aligns with Unit and Agent, where the daily-budget editor sits inside Config (Agent today; the unit-default has no per-unit budget editor in v0.1). One canonical home for "set the daily budget"; the read view stays on Budgets.

### 5.11 Config

Subjects: Tenant (**new**), Unit, Agent.

Config is a **container** with sub-tabs. Sub-tab catalog by subject:

#### Tenant × Config (new)

URL: `?tab=Config&subtab=<name>`.

| Sub-tab | Content | Canonical home | Notes |
|---|---|---|---|
| Secrets | `<TenantDefaultsPanel>` body (fixed-list LLM credentials: anthropic-api-key, anthropic-oauth, openai-api-key, google-api-key) | this sub-tab | `/settings` Tenant-defaults card embeds `<TenantDefaultsPanel>` so the canonical body lives in one place. |
| Budget | `<BudgetPanel>` body (daily-budget editor) | this sub-tab | `/settings` Tenant-budget card embeds `<BudgetPanel>`. |
| Cloning | `<CloningPolicyPanel>` body (read-only summary) | this sub-tab | `/settings` Tenant-cloning card embeds `<CloningPolicyPanel>`. **The editor rides `spring agent clone policy set --scope tenant`** — read-only-in-portal is unchanged. |

#### Unit × Config

URL: `?tab=Config&subtab=<name>`. #2331 added General as the first sub-tab and folded the standalone Expertise sub-tab into General; every other sub-tab is preserved from #2254.

| Sub-tab | Content | Canonical home |
|---|---|---|
| General | `<UnitGeneralPanel>` (displayName / description / model hint / color, with `<UnitExpertisePanel>` folded in) | this sub-tab |
| Boundary | `<BoundaryTab>` | this sub-tab |
| Execution | `<ExecutionTab>` (image / runtime / model / hosting; surfaces member-agent rows) | this sub-tab |
| Instructions | `<InstructionsPanel kind="Unit">` | this sub-tab |
| Connector | `<ConnectorTab>` | this sub-tab |
| Skills | `<SkillsTab>` (per-agent toggle of skills against the catalog) | this sub-tab |
| Secrets | `<SecretsTab>` | this sub-tab |
| Budget | `<UnitBudgetPanel>` | this sub-tab |
| Debug | Collapsible raw-status JSON | this sub-tab |

#### Agent × Config

URL: `?tab=Config&subtab=<name>`. #2254 promoted the stacked Execution / Budget / Expertise / Debug sections into a sub-tab strip; #2331 added General first and folded Expertise into it.

| Sub-tab | Content | Canonical home |
|---|---|---|
| General | `<AgentGeneralPanel>` (displayName / description / role / model hint / specialty / enabled toggle / executionMode, with `<AgentExpertisePanel>` folded in) | this sub-tab |
| Execution | `<AgentExecutionPanel>` (image / runtime / model / hosting + inherited-from-unit overlay) | this sub-tab |
| Instructions | `<InstructionsPanel kind="Agent">` | this sub-tab |
| Budget | `<AgentBudgetPanel>` (daily-budget editor) | this sub-tab |
| Connector | inherited-from-unit read-only view | this sub-tab |
| Skills | `<EquippedSkillsTab kind="Agent">` | this sub-tab |
| Secrets | `<AgentOverridesPanel>` body **scoped to this agent** | this sub-tab |
| Debug | Collapsible raw-status JSON | this sub-tab |

**Why Agent gains a Secrets sub-tab.** Today agent-scope secret overrides live exclusively on the `/settings` page (`<AgentOverridesPanel>`), which forces the operator to leave the agent's Detail Pane, pick the agent from a dropdown, then edit. Under alignment **the canonical home for agent-scope secret overrides is Agent × Config → Secrets**, mirroring Unit × Config → Secrets. The `/settings` panel becomes a convenience entry point: when opened standalone it keeps the agent picker; when reached via the Agent Detail Pane the picker is hidden and the panel scopes to the open agent. **No new agent-scope-secret editing capability is added** — the panel and the API are unchanged.

### 5.12 Deployment

Subject: Agent.

| Content | Agent | Canonical home |
|---|---|---|
| `<LifecyclePanel>` (deploy / undeploy / scale / status / logs) | A | this tab |

Identical to today. Position in the canonical strip moves from "visible position 10" to "overflow position 2" — the deep-link contract (`?tab=Deployment`) is preserved.

---

## 6. Other-surface mapping

For each surface outside the Explorer Detail Pane, the canonical tab that owns the same setting and whether the standalone surface stays.

### 6.1 `/settings` hub — `src/Cvoya.Spring.Web/src/app/settings/page.tsx`

**Stays as standalone route.** It is the canonical entry point for tenant-scope settings + admin catalogs. Under alignment its cards embed (not re-implement) the controls whose canonical home is inside a per-subject Config tab:

| Card on `/settings` | Embeds | Canonical home |
|---|---|---|
| Tenant budget | `<BudgetPanel>` | Tenant × Config → Budget |
| Tenant defaults | `<TenantDefaultsPanel>` | Tenant × Config → Secrets |
| Agent overrides | `<AgentOverridesPanel>` (with agent picker) | Agent × Config → Secrets (per-agent scope; the standalone panel keeps the picker for cross-agent operations) |
| Account | `<AuthPanel>` | `/settings` (not a per-subject concern) |
| About | `<AboutPanel>` | `/settings` (not a per-subject concern) |
| Tenant cloning policy | `<CloningPolicyPanel>` | Tenant × Config → Cloning |

**Rationale for keeping the `/settings` cards.** A new-tenant operator's first stop is `/settings` to set up LLM credentials; they have not yet selected a subject in the Explorer. Routing them through Tenant Detail Pane → Config → Secrets adds friction. The `/settings` cards are a **workflow shortcut**, not a duplicate implementation — they embed the canonical bodies.

### 6.2 `/settings/skills` — tenant skill catalog

**Stays as standalone route.** It is the tenant-wide read-only catalog (`spring skill list`), not a per-subject setting. The per-agent Skills *editor* lives at Agent × Skills; the unit's per-agent toggle matrix lives at Unit × Config → Skills. The catalog is the *reference* for both editors.

### 6.3 `/settings/packages`, `/settings/packages/[name]`

**Stays as standalone route** under the AGENTS.md admin carve-out (visibility-only; install/configure rides `spring`).

### 6.4 `/settings/model-providers`

**Stays as standalone route** under the AGENTS.md admin carve-out (visibility-only; install/configure rides `spring`).

### 6.5 `/settings/system-configuration`

**Stays as standalone route** under the AGENTS.md admin carve-out (visibility-only — startup configuration report).

### 6.6 `/policies` route

**Stays as standalone route.** Today it is the cross-unit roll-up of every policy across every unit. The per-subject Policies tab is the *one-subject* surface; `/policies` is the *cross-subject* surface. They are different views of the same data. The Tenant × Policies tab deep-links to `/policies` for the full matrix.

### 6.7 `/budgets` route

**Stays as standalone route.** Today it is the cross-unit budget overview. Tenant × Budgets deep-links to it for the full matrix.

### 6.8 `/analytics/costs` + `/analytics/throughput` + `/analytics/waits`

**Stays as standalone routes** — these are deep-dive analytics surfaces, not per-subject configuration. Tenant × Activity and Tenant × Budgets deep-link to them.

### 6.9 `/inbox`

**Stays as standalone route.** It is the cross-thread surface. Per-subject Messages tabs are filtered views of the same data.

### 6.10 Drawer-panel extension contract

The drawer-panel registry (`useDrawerPanels()`, `defaultDrawerPanels`) and the hosted-extension seam stay as today (see `src/Cvoya.Spring.Web/DESIGN.md` § 11.3). Hosted extensions register additional panels (Members / RBAC, SSO) at the `/settings` hub — they do not register tabs inside the Explorer Detail Pane. The CLI-parity rule still applies: every interactive control in a panel must have a matching CLI verb.

---

## 7. Migration plan

### 7.1 New `aggregate.ts` shape

The implementation PRs converge `UNIT_TABS` / `AGENT_TABS` / `TENANT_TABS` on the following shape. This is the **runtime source of truth** post-alignment.

```ts
export const TENANT_TABS = {
  // Messages, Agents, Memory, Skills, Traces, Clones, and Deployment
  // are intentionally absent — Tenant does not participate in threads,
  // does not compose thread participants, does not have memory, and is
  // not addressable as an agent. See § 1 principle and § 3.4. The
  // canonical *order* is honored: every slot that does apply renders in
  // its canonical position.
  visible: [
    "Overview",
    "Activity",
    "Policies",
    "Budgets",
  ] as const,
  overflow: ["Config"] as const,
};

export const UNIT_TABS = {
  // A unit is an agent (see docs/concepts/units-vs-agents.md), so it
  // gains the agent's introspection slots (Skills, Traces) and the
  // lifecycle slot (Deployment). Clones is the only agent-only slot —
  // units cannot be cloned today.
  visible: [
    "Overview",
    "Activity",
    "Messages",
    "Memory",
    "Agents",
    "Skills",
    "Traces",
    "Policies",
  ] as const,
  overflow: ["Config", "Deployment"] as const,
};

export const AGENT_TABS = {
  // Agents-list is Unit-only (agents do not contain agents). Clones is
  // Agent-only (units cannot be cloned today).
  visible: [
    "Overview",
    "Activity",
    "Messages",
    "Memory",
    "Skills",
    "Traces",
    "Clones",
    "Policies",
  ] as const,
  overflow: ["Config", "Deployment"] as const,
};

// Landed under #2266 / #2267. NodeKind also gains "Human";
// HumanTabName / TabsFor extend accordingly. The Overview body
// landed under #2267; the Messages (#2268) and Config (#2269) bodies
// are slot-reserved placeholders pending Portal Wave B follow-ups.
export const HUMAN_TABS = {
  visible: ["Overview", "Messages"] as const,
  overflow: ["Config"] as const,
};
```

Notes:

- `TabName` is the union `UnitTabName | AgentTabName | TenantTabName | HumanTabName`. `TabsFor<K>` narrows per-kind so `("Tenant", "Skills")` rejects at compile time.
- The `visible` / `overflow` split is unchanged in its semantics (Detail-Pane rendering hint). The `register-all` test must be updated to enumerate the new pairs.

### 7.2 Per-sub-issue migration checklist

Each row maps a sub-issue to its new scope, the files it touches, and any re-scope decision. Sub-issues should land in this order (implementation order is selectable; the order below minimises rebase pain).

#### #2258 — Overview unification (new scope: unify Unit + Agent + Tenant Overview)

- **Scope:** merge `unit-overview.tsx` + `agent-overview.tsx` into one `<OverviewTab kind id />` component. Subject-specific affordances (LifecyclePanel embed on Agent; UnitCard grid on Tenant; stat tiles vary; expertise card on Unit; cost summary card on Agent; validation panel on Unit; engagement-portal link on Unit + Agent) gate behind `node.kind`. `tenant-overview.tsx` keeps its body (UnitCard grid) but routes through the same shared chrome (description, IssuesPanel slot when applicable).
- **Files:** `src/components/units/tabs/unit-overview.tsx`, `agent-overview.tsx`, `tenant-overview.tsx`, `aggregate.ts` (no order change for Overview), `register-all.ts` (no pair change).
- **Re-scope:** **expanded** to include Tenant (sub-issue body covers Unit + Agent only; Tenant Overview is small enough to fold in cheaply rather than file a follow-up).

#### #2257 — Memory unification

- **Scope:** merge `unit-memory.tsx` + `agent-memory.tsx` + `tenant-memory.tsx` into one `<MemoryTab kind id />` component. The empty-state copy stays as today.
- **Files:** `src/components/units/tabs/unit-memory.tsx`, `agent-memory.tsx`, `tenant-memory.tsx`.
- **Re-scope:** the issue body already mentions tenant-memory.tsx "if cheap" — confirmed cheap (static empty state, byte-for-byte mergeable). Fold in.

#### #2256 — Messages unification

- **Scope:** merge `unit-messages.tsx` + `agent-messages.tsx` into one `<MessagesTab kind id />` component. They are 27-line and 33-line thin wrappers around `<UnitAgentMessagesView>` today — the unification is mostly mechanical (the view itself is already shared). Tenant is not in scope: per the § 1 principle, Tenant does not participate in threads and does not gain a Messages tab.
- **Files:** `src/components/units/tabs/unit-messages.tsx`, `agent-messages.tsx`. `unit-agent-messages-view.tsx` untouched.
- **Re-scope:** unchanged for Unit + Agent. No tenant follow-up filed.

#### #2255 — Policies unification

- **Scope:** merge `unit-policies.tsx` + `agent-policies.tsx` + `tenant-policies.tsx` into one `<PoliciesTab kind id />` component. Re-parameterise the canonical `tab-impls/policies-tab.tsx` from `{ unitId }` to `{ kind, id }`. Agent variant renders only Initiative + Cloning panels (per § 5.9). Tenant variant renders the dimension panels with tenant-scope endpoints, falling back to "set via CLI" placeholders where the endpoint does not exist; the deep-link to `/policies` is preserved.
- **Files:** `src/components/units/tab-impls/policies-tab.tsx`, `src/components/units/tabs/unit-policies.tsx`, `agent-policies.tsx`, `tenant-policies.tsx`. `agent-initiative-panel.tsx` and `agent-cloning-policy-panel.tsx` stay as the Agent variants.
- **Re-scope:** **expanded** to include Tenant (issue body says "plus `tenant-policies.tsx`"; we honour that).

#### #2254 — Config unification (largest sub-issue; reconsider splitting)

- **Scope:** the largest change. Three parts.
  - **Part A — Agent × Config gains sub-tabs.** Promote today's stacked sections (Execution / Budget / Expertise / Debug) into the canonical `<Tabs>` strip matching Unit's pattern, **and** introduce a Secrets sub-tab that embeds `<AgentOverridesPanel>` scoped to the open agent. URL contract: `?tab=Config&subtab=<name>` — same shape as Unit × Config.
  - **Part B — Tenant × Config is created.** New tab; sub-tabs Secrets / Budget / Cloning. Bodies are the existing `<TenantDefaultsPanel>`, `<BudgetPanel>`, `<CloningPolicyPanel>` lifted into a shared sub-tab strip. `/settings` continues to embed the same components.
  - **Part C — `<AgentOverridesPanel>` gains an optional `agentId` prop.** When passed, the panel hides its agent picker and scopes to that agent. When omitted (the `/settings` standalone case), the panel keeps the picker. The mutation surface is unchanged.
- **Files:**
  - `src/components/units/tabs/agent-config.tsx` — promotes sections to sub-tabs.
  - `src/components/units/tabs/tenant-config.tsx` — new file.
  - `src/components/units/tabs/unit-config.tsx` — unchanged structurally; aligned URL state pattern stays.
  - `src/components/units/aggregate.ts` — `TENANT_TABS.overflow` gains `Config`.
  - `src/components/units/tabs/register-all.ts` — register Tenant × Config.
  - `src/components/settings/agent-overrides-panel.tsx` — optional `agentId` prop.
- **Re-scope:** **expanded significantly** — issue body covers Unit + Agent only. The Tenant × Config addition is the design's biggest move and must land alongside Agent Config sub-tabs so the URL contract (`?subtab=`) lands once. **Recommendation: keep #2254 as one PR despite the size; splitting Tenant × Config off would force an interim state where Tenant has no Config tab but Agent has new sub-tabs.**

#### #2253 — Activity unification (already shipped, #2259)

- **Status:** complete.
- **Follow-up under canonical-tabs:** none for Unit + Agent. **Tenant × Activity** stays as today's deep-link card unless a tenant feed endpoint lands; the position aligns at slot 2 with no change to `<ActivityTab>` props.

### 7.3 Follow-ups to file before the canonical-order ships

The only Tenant-side addition is the new Config tab, and that is absorbed by the expanded #2254 scope. **If the implementation agent decides to split #2254**, file Tenant × Config as a new sub-issue and link it.

Per the § 1 principle, no Tenant × Messages or Tenant × Agents sub-issue is needed — those tabs are intentionally absent on Tenant.

### 7.4 What stays unchanged

- The `register-all` completeness test (`src/components/units/tabs/register-all.test.ts`) — its purpose (every `(kind, tab)` pair has a registered component) is unchanged; only the pair set grows.
- The Detail Pane URL contract — `?node=<id>&tab=<name>&subtab=<name>` is the same.
- The Cmd-K teleport bridge — unchanged.
- The drawer-panel registry — unchanged.
- The CLI-parity rule — unchanged.
- The token catalog, typography, and spacing rules in `src/Cvoya.Spring.Web/DESIGN.md` § 2–§ 4 — unchanged.

---

## 8. Open questions and known trade-offs

These are flagged so the implementation PRs do not stop on them — the design's answer is given, but reviewers may push back.

- **Q: Does promoting Agent × Config sections to sub-tabs hurt operators who scan the page vertically?** Possibly — today the agent Config page is one scroll. Under alignment it becomes a sub-tab strip matching Unit. We accept the trade-off because consistency across subjects is the umbrella goal and a sub-issue can refine the sub-tab strip later (e.g. an "All" view) without breaking the canonical structure.
- **Q: The Tenant × Budgets daily-budget editor moves from `/settings` to Tenant × Config → Budget. Does this hide it from a first-time operator?** No: `/settings` keeps the Tenant-budget card and that card embeds the same `<BudgetPanel>` component. **Two access paths, one canonical home.**
- **Q: Why not delete `/policies` / `/budgets` since the per-subject tabs now cover them?** Because they are cross-subject roll-ups that the per-subject tabs cannot deliver. Deleting them would lose information.

---

## 9. Constraint compliance

A final pass against the constraints in #2261 and #2252:

- **No hidden options** — every setting reachable today has a row in § 5 or § 6. Variances are documented (§ 4.1).
- **Familiar shape** — § 3 defines a single canonical order; § 4 binds every subject to it.
- **One canonical home per setting** — § 5 marks the canonical home and lists every embed/link. Settings-hub cards embed, do not re-implement.
- **`aggregate.ts` is the source of truth** — § 7.1 names the post-alignment shape exactly.
- **No code changes in this PR** — yes; this is doc-only.
