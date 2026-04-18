// Type contracts for the portal extension system (#440).
//
// The OSS portal must stay tenancy-unaware. The private Spring Voyage
// Cloud repo extends it via DI-style registration (`registerExtension`)
// to add tenant switcher, billing, members, and audit surfaces. The
// contracts below are the seams the hosted build plugs into.

import type { ComponentType, ReactNode } from "react";

/**
 * Logical sidebar section that a route belongs to. Keeps the sidebar
 * free of hard-coded groupings — hosted-only entries (e.g. "Tenant",
 * "Billing") can pick the "settings" section without patching OSS.
 *
 * New sections are added by appending to this union. The sidebar
 * renders sections in the order declared by `NAV_SECTION_ORDER` below.
 */
export type NavSection = "primary" | "settings";

/**
 * Declared order in which nav sections render. Append new entries at
 * the end — renaming or reordering existing ones is a breaking change
 * for hosted consumers.
 */
export const NAV_SECTION_ORDER: readonly NavSection[] = ["primary", "settings"];

/**
 * A single navigable route exposed by the portal. Both the sidebar
 * and the command palette consume the manifest — the same type
 * therefore has to satisfy both surfaces.
 *
 * - `path`        — Next.js App Router path (`/units`, `/units/[id]`).
 *                   Dynamic routes are allowed but the sidebar/palette
 *                   only link to concrete paths (no param substitution
 *                   at this layer — extensions surface concrete routes).
 * - `label`       — short human-readable name used as the nav label
 *                   and the primary palette entry title.
 * - `icon`        — React component rendered at 16×16 in the sidebar
 *                   and 14×14 in the palette. Kept as a generic
 *                   component type so extensions aren't forced to use
 *                   `lucide-react`.
 * - `navSection`  — which sidebar group the route belongs to.
 * - `permission`  — optional capability key. When set, the route is
 *                   only rendered if the current auth context reports
 *                   it via `hasPermission(key)`. OSS's default auth
 *                   adapter grants all permissions, so OSS-only
 *                   entries omit this field entirely.
 * - `orderHint`   — optional sort key within a section. Lower numbers
 *                   render first. Entries without `orderHint` sort
 *                   after ordered entries, in registration order.
 * - `keywords`    — optional extra terms the palette uses for fuzzy
 *                   matching ("costs" → palette also matches
 *                   "spending", "budget").
 * - `description` — optional one-liner shown beneath the label in the
 *                   palette. Not rendered in the sidebar.
 */
export interface RouteEntry {
  path: string;
  label: string;
  icon: ComponentType<{ className?: string }>;
  navSection: NavSection;
  permission?: string;
  orderHint?: number;
  keywords?: readonly string[];
  description?: string;
}

/**
 * A command-palette action that performs work instead of navigating.
 * "Create unit", "Start unit …", "Rotate secret" all live here.
 *
 * An action can still reference a route (e.g. "Create unit" navigates
 * to `/units/create`) by supplying `href`. Actions without `href` must
 * provide an `onSelect` callback invoked when the user presses enter.
 */
export interface PaletteAction {
  id: string;
  label: string;
  icon?: ComponentType<{ className?: string }>;
  /** Grouped under this section in the palette UI. */
  section?: string;
  /** Optional fuzzy-match keywords. */
  keywords?: readonly string[];
  /** Optional short description shown beneath the label. */
  description?: string;
  /** Capability required to see this entry. Omit for OSS-always-on. */
  permission?: string;
  /** Lower numbers sort first inside the section. */
  orderHint?: number;
  /** Direct navigation target (preferred for anything that is just a link). */
  href?: string;
  /** Imperative handler — invoked by the palette on enter / click. */
  onSelect?: () => void | Promise<void>;
}

/**
 * Auth-context contract consumed by the portal. OSS ships a default
 * implementation that reports `{ id: "local", displayName: "local" }`
 * with every permission granted (no auth). Hosted overrides the
 * adapter at `registerExtension` time.
 *
 * Kept deliberately narrow — it answers "who is acting?" and "can
 * they do X?". Anything richer (avatar URL, tenant list, billing
 * state) is the hosted adapter's concern and lives behind its own
 * typed extension surface in the private repo.
 */
export interface AuthUser {
  id: string;
  displayName: string;
  email?: string;
}

export interface IAuthContext {
  /** Current signed-in user. `null` means "no session". */
  getUser(): AuthUser | null;
  /**
   * Whether the user has the named permission. OSS's default returns
   * `true` for every key (no auth). Hosted wires this up to the
   * tenant-scoped RBAC service.
   */
  hasPermission(permissionKey: string): boolean;
  /**
   * Headers the API client decorator should attach to every request.
   * OSS returns `{}`; hosted returns `Authorization` and tenant
   * headers.
   */
  getHeaders(): Record<string, string>;
}

/**
 * Minimal shape every "fetch-like" client we want to wrap must
 * support. The native `fetch` satisfies this, as does
 * `openapi-fetch`'s exported `Client["request"]` signature. Typed
 * this way the decorator stays library-agnostic.
 */
export type FetchFn = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>;

/**
 * A function that wraps a `FetchFn` and returns a new `FetchFn`. The
 * hosted build composes one or more decorators to inject auth
 * headers, tenant context, CSRF tokens, etc.
 *
 * Decorators compose right-to-left (outermost wrapper is the first
 * entry in the array): `decorators = [A, B, C]` yields `A(B(C(fn)))`.
 */
export type ClientDecorator = (inner: FetchFn) => FetchFn;

/**
 * A panel shown inside the Settings drawer (#451). Each panel is a
 * self-contained React component that the drawer renders alongside
 * every other registered panel, ordered by `orderHint`.
 *
 * OSS registers Budget / About / Auth as defaults; the hosted build
 * plugs in additional panels (tenant secrets, members / RBAC, SSO)
 * through the same registration surface.
 *
 * Contract rules (follow-up ADR tracked in #556):
 *
 * - `id` is globally unique. Re-registering the same `id` replaces the
 *   prior panel — matches how `registerExtension` already replaces by
 *   extension id.
 * - `orderHint` — lower numbers render first. Entries without
 *   `orderHint` sort after ordered entries, in registration order.
 * - `permission` — optional capability gate. When set, the panel is
 *   only rendered if the active auth adapter grants it. OSS's default
 *   adapter grants every permission, so OSS panels omit this.
 * - **CLI parity.** Every interactive control inside a panel MUST have
 *   a CLI equivalent. Panels that surface controls without parity are
 *   dropped from the drawer until the CLI lands (see the PR body of
 *   Sub-PR D for the audit notes).
 */
export interface DrawerPanel {
  /** Unique panel id — used for React keys and test harness lookups. */
  id: string;
  /** Short human-readable heading, rendered as the panel card title. */
  label: string;
  /** Icon rendered at 16×16 next to the label. */
  icon: ComponentType<{ className?: string }>;
  /** Optional one-liner shown under the label. */
  description?: string;
  /** Lower numbers sort first. */
  orderHint?: number;
  /** Capability required to render the panel. Omit for OSS-always-on. */
  permission?: string;
  /** The panel body — the drawer renders this inside a `<Card>`. */
  component: ReactNode;
}

/**
 * A single extension bundle — the shape the hosted build passes to
 * `registerExtension(...)`. Every field is optional; extensions opt
 * into only the surfaces they care about.
 */
export interface PortalExtension {
  /** Human-readable identifier — used in warnings and dev tooling. */
  id: string;
  /** Routes this extension adds to the sidebar / palette. */
  routes?: readonly RouteEntry[];
  /** Palette-only actions (no sidebar presence). */
  actions?: readonly PaletteAction[];
  /** Panels this extension adds to the Settings drawer (#451). */
  drawerPanels?: readonly DrawerPanel[];
  /**
   * Replaces the default auth adapter. Only one extension may set
   * `auth`; the registration call throws if a second registers one.
   */
  auth?: IAuthContext;
  /**
   * API-client decorator(s). Multiple extensions may each add their
   * own — they are composed in registration order.
   */
  decorators?: readonly ClientDecorator[];
  /**
   * Arbitrary React nodes rendered into named shell slots (footer
   * area of the sidebar, top-bar trailing, etc.). Unknown slot names
   * are ignored by the shell but the registry keeps them for
   * future expansion.
   */
  slots?: Partial<Record<ShellSlot, ReactNode>>;
}

/**
 * Named slots the portal shell exposes for extension content. Keep
 * this union small and purposeful — every slot added here obligates
 * the OSS shell to render it with a sensible default (usually
 * `null`).
 */
export type ShellSlot =
  | "topBarLeft"
  | "topBarRight"
  | "sidebarFooter"
  | "settingsNav"
  | "unitDetailHeader";
