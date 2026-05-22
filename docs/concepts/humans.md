# Humans

## What a human is

A **human** is an addressable subject that participates in threads alongside agents and units. The platform models humans as actors (`HumanActor`) with their own address scheme (`human:<guid>`); a message addressed to `human:<id>` is routed to the right channel (Slack, GitHub, email, etc.) through the human's configured inbound connector binding.

Humans are **subjects**, not agents. They share thread participation with agents and units, but they do not have execution config, memory, skills, traces, runtime, or any of the agent-shaped surfaces. See [Units vs agents](units-vs-agents.md) for the agent-shaped contract; humans share only the `IMessageReceiver` slice.

This page covers humans in two roles: as ACL-bound platform subjects (the part that originated in [ADR-0044 §1](../decisions/0044-team-role-vs-platform-role.md)) and as declarative *team members* on a unit's `members:` block (the part introduced by [ADR-0046](../decisions/0046-unified-members-grammar.md) §§ 1, 4, 7).

## What a human can do

| Capability | Applies to Human |
|---|---|
| Participate in threads (receive + send messages) | Yes |
| Be a member of a unit | Yes — via the unified `members:` grammar (see below) and the post-install permission surface |
| Hold permissions on a unit (configure, operate, view) | Yes — distinct from team-role membership |
| Be addressable through a connector (Slack handle, GitHub handle, email) | Yes — via the `Human → TenantUser` mapping; the handle itself lives on the [`TenantUser`](tenants.md#tenantuser-the-authenticated-principal), not on the `Human` row ([ADR-0047 §§ 2, 7](../decisions/0047-platform-user-human-split.md)) |
| Have an outbound connector binding (translate external events into messages) | No — that's a unit/connector concern |
| Be cloned, deployed, scaled | No |
| Have memory, skills, traces, expertise, budget, policy, runtime | No |
| Be discoverable via the expertise directory | No — humans have no expertise profile; they surface in `sv.directory.list_members` as members, not as routable expertise |

## Team role vs. platform role

Two orthogonal axes ([ADR-0044 §1](../decisions/0044-team-role-vs-platform-role.md), preserved unchanged by [ADR-0046](../decisions/0046-unified-members-grammar.md)):

| Axis | Granted by | Carries | Surface |
|---|---|---|---|
| **Platform role** | tenant operator (post-install) | `PermissionLevel` (`Viewer / Operator / Owner`); the right to *do* things to the system | `unit_human_permissions` row; `PUT/DELETE /units/{id}/humans/{humanId}/permissions` |
| **Team role** | package author (at-install, via YAML) | `roles` (free-form list), `expertise`, `notifications`; the *role* the human plays on the team | `unit_memberships_humans` row keyed by `(tenant, unit, human)` |

A single physical human can hold any subset of each axis independently. In OSS the operator is necessarily Owner platform-wide *and* fills every team role declared by an installed package (one human, every slot). In hosted, a non-operator tenant member can fill a team role without holding platform-admin rights; the operator can hold Owner without ever appearing on a team.

## Humans as team members in package YAML

A unit declares its human team members on the same `members:` list that carries its agents and sub-units, under a `- human:` discriminator ([ADR-0046 §1](../decisions/0046-unified-members-grammar.md)):

```yaml
members:
  - agent: ada
  - unit: { from: engineering, name: engineering-1 }
  - human:
      roles: [owner, security_lead]
      expertise: [security, infra]
      notifications: [escalation, completion]
  - human: { from: oss-operator }                  # template stamp
```

The slot is **inline-only** — humans own no sub-artefacts, so there is no `humans/<name>/package.yaml` folder shape ([ADR-0046 §6](../decisions/0046-unified-members-grammar.md)). The schema is `HumanManifest` in [`src/Cvoya.Spring.Manifest/UnitManifest.cs`](../../src/Cvoya.Spring.Manifest/UnitManifest.cs); the fields are:

| Field | Shape | Notes |
|---|---|---|
| `displayName` | string | Optional. Overrides the install policy's derived default (e.g. OSS `"Operator · <roles[0]>"`). |
| `description` | string | Optional. Single-line description; persisted verbatim onto `HumanEntity.Description`; editable post-install. |
| `from` | string | Optional. Stamps from a `HumanTemplate`. Bare name resolves within the package; qualified `<pkg>/<name>@<version>` resolves cross-package. |
| `roles` | string[] | Optional. Free-form team-role tags; multi-valued case-insensitive set ([ADR-0046 §3](../decisions/0046-unified-members-grammar.md)). |
| `expertise` | string[] | Optional. Free-form expertise tags. |
| `notifications` | string[] | Optional. Free-form notification event tags. Human-only — agents have no notification surface. |

Every field is optional. An empty `- human: {}` is a valid declaration; the install policy fills in sensible defaults.

The legacy top-level `humans:` block ([ADR-0044 §2](../decisions/0044-team-role-vs-platform-role.md)) is removed; the parser rejects it with a `LegacyHumansBlock` error pointing at [ADR-0046](../decisions/0046-unified-members-grammar.md).

## `HumanTemplate`: reusable team-role definitions

A `HumanTemplate` is a non-activating artefact under `templates/` that defines a reusable human team-member shape, stamped from a unit's `members:` block via `- human: { from: <template-name> }` ([ADR-0046 §4](../decisions/0046-unified-members-grammar.md)).

```yaml
# packages/spring-voyage-oss/templates/oss-operator/package.yaml
apiVersion: spring.voyage/v1
kind: HumanTemplate
name: oss-operator
displayName: OSS Operator
description: Default OSS-deployment human; fills every team role.
roles: [owner]
expertise: [operations, escalation]
notifications: [escalation, completion]
```

Stamped:

```yaml
members:
  - human: { from: oss-operator }                          # all fields flow through
  - human: { from: oss-operator, roles: [security_lead] }  # `roles` replaces the template's [owner]
```

**Override semantics** ([ADR-0046 §5](../decisions/0046-unified-members-grammar.md)): when a member entry sets `roles`, `expertise`, or `notifications` and the template also sets them, the entry **replaces** the template's value (full replacement, not union). Scalars (`displayName`, `description`) follow the same scalar-override rule. Authors who want the template's list plus extras copy the template's list and add to it.

The template schema lives in [`src/Cvoya.Spring.Manifest/HumanTemplateManifest.cs`](../../src/Cvoya.Spring.Manifest/HumanTemplateManifest.cs). `HumanTemplate` folders own no sub-artefacts.

## Install-time resolution

Each install-time `- human:` declaration is resolved through the `IPackageHumanResolutionPolicy` DI seam ([ADR-0044 §4](../decisions/0044-team-role-vs-platform-role.md), preserved by [ADR-0046 §10](../decisions/0046-unified-members-grammar.md)).

- **OSS default** — every declaration mints a fresh `HumanEntity` row with a newly-generated Guid and a derived `DisplayName` (`"Operator · <roles[0]>"`, falling back to `"Operator"` when no roles are declared). Two `- human:` entries in one unit produce two distinct rows; the OSS deployment maps every `Human` to the single OSS-operator `TenantUser` (`OssTenantUserIds.Operator`) through the `Human → TenantUser` mapping described in [Human → TenantUser display mapping](#human--tenantuser-display-mapping) below. This keeps the Identity / Connector / DisplayName affordances uniform across declarations — the portal's Humans list sees N rows with sensible labels, no special-casing for the operator UUID.
- **Hosted overlay** — concrete cloud-side implementations decide whether to mint a fresh row or to bind to an existing tenant member (operator-fills-all, prompt-per-slot, match-by-claim, reject). The `{human_id → tenant_user_id}` mapping table that would let one tenant user fill multiple declarations is **v0.2** and explicitly out of scope ([ADR-0047](../decisions/0047-platform-user-human-split.md) §7, "OUT2" in the umbrella).

Membership is keyed by `(tenant, unit, human)` in `unit_memberships_humans` ([ADR-0046 §7](../decisions/0046-unified-members-grammar.md)); `roles`, `expertise`, and `notifications` are jsonb columns on the row. Two declarations with the same `roles` produce two distinct rows backed by two distinct `HumanEntity` Guids — the unit has two "positions" of that role.

Platform ACLs are deliberately **not** a manifest concern. Package authors have no authority to grant tenant permissions; the resolver writes the team-membership row only. ACL grants stay on `unit_human_permissions` and are managed through the existing `/api/v1/tenant/units/{id}/humans/{humanId}/permissions` surface.

## `Human → TenantUser` display mapping

A `Human` row is a **configuration entity** introduced by a package — a slot on a unit's team that names a role, an expertise set, and notification preferences. A `TenantUser` row is the **authenticated principal** of Spring Voyage scoped to one tenant (the operator in OSS, tenant members in cloud). The two are deliberately distinct: a package author can declare team slots without knowing who will fill them; the deployment decides which `TenantUser` answers for each slot at install time.

Display-side connector identity — the GitHub login, the Slack handle, the human-friendly rendering name on a connector — is owned by the **`TenantUser`**, not by the `Human`. The `Human` row itself carries no connector-identity fields ([ADR-0047](../decisions/0047-platform-user-human-split.md) §§ 2, 7). The resolution path is:

```
Human (configuration slot) → TenantUser (authenticated principal) → TenantUserConnectorIdentity (display fields per connector)
```

When an agent renders `@<human-name>` in a PR comment or calls `--add-reviewer <login>`, the agent walks `Human → TenantUser → TenantUserConnectorIdentity` for the connector and reads the `username` from the tenant-user's row. The outbound API call's **credential** is, separately and unconnectedly, the unit binding's pinned credential (App-installation or PAT secret — [ADR-0047 §6](../decisions/0047-platform-user-human-split.md)). The mapping is the display / mention / attribution seam, never the auth seam.

**OSS default.** Every `Human` row maps to the single OSS-operator `TenantUser` pinned by `OssTenantUserIds.Operator` (deterministic v5 UUID — see [Tenants § OSS operator TenantUser](tenants.md#oss-operator-tenantuser) and [Data & identity](../architecture/data-and-identity.md#the-oss-default-tenant)). N declared `Human` rows resolve to the same one tenant user, which carries the operator's GitHub / Slack / Linear handles once. No duplication onto every `Human` row.

**Hosted overlay.** Per-`Human` explicit override (binding `Human X` to `TenantUser Y` independent of the default policy) is **v0.2** — OUT2 in the umbrella. v0.1 ships the derived projection (default-to-operator in OSS; the resolution policy's choice in hosted).

## Post-install editing

`HumanEntity` gains `Description` alongside the existing `DisplayName` ([ADR-0046](../decisions/0046-unified-members-grammar.md)); both are editable after install through two parallel surfaces:

- **Portal** — Human × Config × General lets operators edit `displayName` and `description` on the loaded human row. The Human page intentionally does **not** carry a per-connector identity sub-tab — connector handles live on the calling user's [User Identity](../guide/user/portal.md) surface (one `TenantUserConnectorIdentity` per `(tenant_user, connector)`), not per `Human` row.
- **CLI** — `spring human set --display-name "…" --description "…"` ([`src/Cvoya.Spring.Cli/Commands/HumanCommand.cs`](../../src/Cvoya.Spring.Cli/Commands/HumanCommand.cs)). At least one of `--display-name` / `--description` must be supplied; omitted flags leave the existing value untouched; pass `""` to clear `description`. The display-identity verbs (`set`, `list`, `remove`) live under `spring user identity …`, targeting the calling `TenantUser`, per [ADR-0047 §12](../decisions/0047-platform-user-human-split.md).

These are operator-facing affordances; the package author's declared values land at install time and are then editable independently of the package YAML. Reinstalling the package against a refined YAML does not retroactively overwrite operator edits — same deferral as the wider "no install-time upsert" rule.

## Discovery via `sv.directory.list_members`

The platform-internal directory tool `sv.directory.list_members(unit_id)` surfaces humans alongside agents and sub-units ([ADR-0046 §9](../decisions/0046-unified-members-grammar.md)). The wire shape on a human entry:

```json
{
  "uuid": "…",
  "kind": "human",
  "display_name": "Operator · owner",
  "roles": ["owner", "security_lead"],
  "expertise": [{ "name": "security" }, { "name": "infra" }],
  "parent_uuids": ["<unit_uuid>"]
}
```

`roles` is multi-valued (renamed and re-typed from ADR-0044 §5's single-valued `team_role`). Agent and unit entries gain the same optional `roles` / `expertise` fields — additive on those kinds. An agent that wants to ask "who is the security lead on my team?" looks the field up on the response from this tool.

Human entries deliberately **omit** the `live_status` field that agent and unit entries carry — humans have no runtime in v0.1, so there is nothing to surface (see [#2491](https://github.com/cvoya-com/spring-voyage/issues/2491)). Field absence — not a null value — is the contract; callers MUST treat missing `live_status` as "no runtime here".

## Portal scope

The portal's `NodeKind` (`src/Cvoya.Spring.Web/src/components/units/aggregate.ts`) was extended to include `"Human"` under #2266 / #2267. Humans are a fourth Explorer subject with a minimal canonical tab set:

- **Overview** — personal info (display name, description, platform role, created-at). Renders the "You" badge when the loaded human matches the currently-authenticated caller's mapped `TenantUser` ([ADR-0047 §7](../decisions/0047-platform-user-human-split.md)).
- **Messages** — threads the human is addressed in.
- **Config** — General sub-tab (display name + description editing). Connector handles (GitHub login, Slack handle, etc.) are **not** edited here — they belong to the calling `TenantUser` and are managed on the per-user User Identity page in the portal (and via `spring user identity` on the CLI). The mapping from `Human` to `TenantUser` resolves the rendered handle at display time.

No Memory, Agents, Skills, Traces, Clones, Policies, Budgets, or Deployment tabs — humans don't have those surfaces.

Human pages live at `/humans/<guid>` and are reached either directly (Cmd-K, activity-feed `human:` rows, unit-membership rows from #2270 + #2427) or by selecting an Explorer node with the `human:` address scheme (`/units?node=human:<guid>` bounces to the dedicated route). The Detail Pane chrome is the shared `<DetailPane>` the Explorer mounts — same address-copy affordance, same tab strip — minus the lifecycle status badge (humans don't have a runtime lifecycle).

## See also

- [ADR-0047](../decisions/0047-platform-user-human-split.md) — `TenantUser` actor kind; display-side connector identity owned by the `TenantUser`; `Human → TenantUser` mapping; the `OssTenantUserIds.Operator` pin.
- [ADR-0046](../decisions/0046-unified-members-grammar.md) — unified `members:` grammar; humans as a member kind; `HumanTemplate`; vocabulary trim.
- [ADR-0044](../decisions/0044-team-role-vs-platform-role.md) — team role vs. platform role; the `IPackageHumanResolutionPolicy` seam (§§ 1, 4 survive ADR-0046 unchanged).
- [ADR-0053 — Units are agents](../decisions/0053-units-are-agents-and-one-way-delivery.md) — what makes a unit an agent (and by contrast, what makes a human *not* an agent).
- [Packages](packages.md) — the unified `members:` grammar and the recursive folder layout.
- [Templates](templates.md) — `HumanTemplate` alongside `UnitTemplate` / `AgentTemplate`.
- [Units vs agents](units-vs-agents.md) — agent-shaped contract.
- [`docs/architecture/messaging.md`](../architecture/messaging.md) — addressing and the agent mailbox.
- [`docs/architecture/components.md`](../architecture/components.md) — `HumanActor` and the actor inventory.
- [`docs/design/canonical-tabs.md`](../design/canonical-tabs.md) — Explorer tab structure including the Human column.
