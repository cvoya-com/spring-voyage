# 0032 — Drawer-panel extension slot pattern (contract, ordering, CLI parity)

- **Status:** Accepted (2026-04-29). v0.1 work.
- **Date:** 2026-04-29
- **Related code:** `src/Cvoya.Spring.Web/src/lib/extensions/` — `types.ts` (`DrawerPanel`, `PortalExtension`), `defaults.tsx` (OSS defaults), `registry.ts` (merge + sort).
- **Related docs:** [`src/Cvoya.Spring.Web/src/lib/extensions/README.md`](../../src/Cvoya.Spring.Web/src/lib/extensions/README.md); [`src/Cvoya.Spring.Web/DESIGN.md`](../../src/Cvoya.Spring.Web/DESIGN.md) § 11.3.
- **Issues:** [#556](https://github.com/cvoya-com/spring-voyage/issues/556) (ADR tracking); [#557](https://github.com/cvoya-com/spring-voyage/issues/557) (Auth panel token CRUD — the first panel to exercise create/revoke).

## Context

PR-S1 Sub-PR D (closes #451) ships a Settings drawer that is extensible via a panel-slot contract layered on top of the portal extension registry (#440 / PR #465). Panels register declaratively via `registerExtension({ drawerPanels: [...] })`; the downstream private repo plugs in additional panels (tenant secrets, members / RBAC, SSO) through the same shape — no OSS fork.

The pattern is worth recording because it generalises beyond the Settings drawer: any future shell surface that is a "stack of panels with `orderHint` + CLI parity" (a command centre, a unit-scoped config drawer) should follow the same rules. The Auth panel's token CRUD (#557) is the first interactive panel and therefore the first to exercise the CLI parity rule in a non-trivial way.

## Decision

### Panel contract

A `DrawerPanel` (declared in `src/Cvoya.Spring.Web/src/lib/extensions/types.ts`) exposes:

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | `string` | yes | Globally unique. Re-registering the same `id` replaces the prior panel. |
| `label` | `string` | yes | Short human-readable heading, rendered as the panel card title. |
| `icon` | `ComponentType<{ className?: string }>` | yes | Rendered at 16×16 next to the label. |
| `description` | `string` | no | One-liner shown under the label in the settings hub. |
| `orderHint` | `number` | no | Sort key — lower numbers render first. |
| `permission` | `string` | no | Capability gate. When set, the panel renders only if the active auth adapter grants it. |
| `component` | `ReactNode` | yes | The panel body — rendered inside a `<Card>` on the settings hub page. |

Extensions register panels via `registerExtension({ drawerPanels: [...] })`. The `id` is the de-duplication key; if two extensions register the same `id`, the second registration wins (last-write semantics).

### Ordering rule

The merged panel list is sorted by `orderHint` (ascending), with panels that omit `orderHint` appearing after all ordered panels, in registration order. This is the same rule `RouteEntry` and `PaletteAction` already follow inside the registry (`src/Cvoya.Spring.Web/src/lib/extensions/registry.ts`).

OSS default panels use `orderHint` values in the 10–90 range. Hosted extensions conventionally use `orderHint >= 100` to sit after the OSS defaults without needing to know the OSS range.

### CLI parity rule

Every **interactive control** inside a panel body MUST have a matching CLI verb. A button or input that lets the operator create, read, update, or delete a resource is an interactive control. A read-only list or label is not.

CLI parity status for the OSS default panels shipped in v0.1:

| Panel | CLI equivalent | Parity status |
|---|---|---|
| Tenant budget | `spring cost set-budget` | parity-complete |
| Tenant defaults | `spring secret --scope tenant {create,rotate,delete}` | parity-complete |
| Account (auth tokens) | `spring auth token {create,list,revoke}` | parity-complete |
| About | `spring platform info` (read-only) | parity-complete |

Panels whose controls lack a CLI equivalent are dropped from the settings hub until the CLI verb lands. A CLI follow-up must be filed before the panel ships.

The CLI parity audit for hosted panels is tracked in [#1386](https://github.com/cvoya-com/spring-voyage/issues/1386).

### Permission gate

A panel that declares `permission` is only rendered when `authAdapter.hasPermission(permission)` returns true for the current session. OSS's default auth adapter grants every permission unconditionally, so OSS panels omit the field. Hosted panels that gate premium or tenant-admin surfaces set it explicitly.

Permission checks are evaluated at render time, not at registration time. A panel registered with a permission key is always in the merged registry; the settings hub page silently omits it when the gate fails.

### No tenant references in the OSS contract

The `DrawerPanel` type is tenancy-neutral. Any tenant-scoped data model lives behind the `component: ReactNode` boundary — in the hosted extension — and is not part of the OSS contract. The OSS portal never imports from the hosted extension; the hosted extension imports from and augments the OSS registry.

### Generalisation

The same four rules (contract, ordering, CLI parity, permission gate) apply to any future shell surface that follows the "stack of panels" pattern:

- A future **command centre** would register `CommandPanel` entries through the same `registerExtension` surface.
- A future **unit-scoped config drawer** would register `UnitConfigPanel` entries with an additional `kind` gate limiting which unit types surface the panel.

The surface-specific type (e.g. `DrawerPanel`, `CommandPanel`) declares its own fields on top of this shared shape; the ordering and gating rules are identical.

## Alternatives considered

- **Hard-coding panels in the settings page component.** Rejected: the private downstream repo would need to fork the OSS page every time it adds a panel. The registry indirection keeps the fork surface to zero.
- **React Context–based slot injection (portals / render props).** Considered. Rejected for this surface: the ordered-list + permission-gate requirements are cleanly expressible as a sorted array of typed descriptors. A slot system adds runtime complexity (Provider nesting, Portal DOM placement) without any additional expressiveness for the use case at hand.
- **`orderHint` as a namespace (100–199 for hosted, 0–99 for OSS).** Not adopted. The convention ("hosted uses ≥ 100") is documented here but not enforced by the registry. Enforcement would need runtime validation that could produce unhelpful errors during development. Convention is sufficient; hosted-side CI lint can enforce the rule cheaply.

## Consequences

### Simpler

- Any downstream repo adds a settings panel by calling `registerExtension({ drawerPanels: [...] })` — no OSS fork, no page-level patch.
- The ordering rule (`orderHint`, then registration order) is identical to the route and action registries; contributors learn one rule.
- The CLI parity rule is explicit and auditable: every PR that ships a panel body lists its interactive controls and their CLI equivalents.

### Harder

- Extensions that need to render panels in a specific relative order have to coordinate their `orderHint` values. There is no runtime conflict detection — two panels with the same `orderHint` render in registration order, which may differ between builds.
- Removing a panel requires the downstream extension to call `registerExtension` with the same `id` and a no-op `component`; there is no `unregister` verb. This is an acceptable trade-off for v0.1; a `deregisterExtension` surface is deferred.
