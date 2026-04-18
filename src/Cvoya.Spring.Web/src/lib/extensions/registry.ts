// Extension registry. Consumers call `registerExtension(...)` at app
// startup (OSS never does; hosted does once). The sidebar and command
// palette read the merged view via the hooks exported from
// `./context.tsx` so no component ever needs to know whether it is
// running in OSS or hosted mode.

import {
  defaultActions,
  defaultAuthContext,
  defaultDrawerPanels,
  defaultRoutes,
} from "./defaults";
import type {
  ClientDecorator,
  DrawerPanel,
  IAuthContext,
  PaletteAction,
  PortalExtension,
  RouteEntry,
  ShellSlot,
} from "./types";
import type { ReactNode } from "react";

/**
 * Merged view of every registered extension. Consumed by React
 * components via the `ExtensionProvider` in `./context.tsx`.
 */
export interface MergedExtensions {
  routes: readonly RouteEntry[];
  actions: readonly PaletteAction[];
  /** Settings drawer panels, sorted by `orderHint`. */
  drawerPanels: readonly DrawerPanel[];
  auth: IAuthContext;
  decorators: readonly ClientDecorator[];
  slots: Readonly<Partial<Record<ShellSlot, ReactNode>>>;
}

type RegistryState = {
  extensions: PortalExtension[];
  authOwner: string | null;
};

// Module-level singleton. A React context wraps it for components.
// Using a module singleton (not React state) lets extensions register
// during module import, before any provider mounts — matches how the
// hosted build wants to plug in at boot.
const state: RegistryState = {
  extensions: [],
  authOwner: null,
};

/**
 * Register a portal extension. Safe to call at any time; the merged
 * view is recomputed on the next read. Calling twice with the same
 * `id` replaces the prior registration (common during HMR).
 *
 * Throws when two distinct extensions try to set `auth` — only one
 * auth adapter may own the session contract at a time.
 */
export function registerExtension(ext: PortalExtension): void {
  const existingIndex = state.extensions.findIndex((e) => e.id === ext.id);
  if (existingIndex >= 0) {
    const existing = state.extensions[existingIndex];
    if (existing === state.extensions[existingIndex] && state.authOwner === ext.id) {
      // Re-register of the same extension — release its claim on
      // `auth` before we re-evaluate below.
      state.authOwner = null;
    }
    state.extensions.splice(existingIndex, 1);
  }

  if (ext.auth) {
    if (state.authOwner && state.authOwner !== ext.id) {
      throw new Error(
        `portal extension '${ext.id}' tried to register an auth adapter but '${state.authOwner}' already owns it`,
      );
    }
    state.authOwner = ext.id;
  }

  state.extensions.push(ext);
}

/**
 * Clear every registered extension. Used by tests and by HMR
 * tear-down. Not exported from the package `index.ts` to discourage
 * production call sites.
 */
export function __resetExtensionsForTesting(): void {
  state.extensions.length = 0;
  state.authOwner = null;
}

/**
 * Compute the merged view from the current registry state plus the
 * OSS defaults. Pure — returns a fresh object on every call so
 * callers can `useMemo` on it.
 */
export function computeMergedExtensions(): MergedExtensions {
  const routes: RouteEntry[] = [...defaultRoutes];
  const actions: PaletteAction[] = [...defaultActions];
  // Drawer panels share the same "append, sort by orderHint, last
  // registration wins on id collision" rules as routes/actions. Defaults
  // (Budget / About / Auth) seed the list; extensions append.
  const drawerPanels: DrawerPanel[] = [...defaultDrawerPanels];
  const decorators: ClientDecorator[] = [];
  const slots: Partial<Record<ShellSlot, ReactNode>> = {};
  let auth: IAuthContext = defaultAuthContext;

  for (const ext of state.extensions) {
    if (ext.routes) routes.push(...ext.routes);
    if (ext.actions) actions.push(...ext.actions);
    if (ext.drawerPanels) drawerPanels.push(...ext.drawerPanels);
    if (ext.decorators) decorators.push(...ext.decorators);
    if (ext.auth) auth = ext.auth;
    if (ext.slots) {
      for (const [slotName, node] of Object.entries(ext.slots)) {
        slots[slotName as ShellSlot] = node;
      }
    }
  }

  return {
    routes: sortRoutes(routes),
    actions: sortActions(actions),
    drawerPanels: sortDrawerPanels(drawerPanels),
    auth,
    decorators,
    slots,
  };
}

// ---------------------------------------------------------------------------
// Sorting helpers — shared between the sidebar and the palette so both
// surfaces see the same order.
// ---------------------------------------------------------------------------

function sortRoutes(entries: RouteEntry[]): RouteEntry[] {
  return [...entries].sort(compareByOrderHint);
}

function sortActions(entries: PaletteAction[]): PaletteAction[] {
  return [...entries].sort(compareByOrderHint);
}

/**
 * Sort drawer panels and drop earlier entries whose `id` was shadowed
 * by a later registration. The last-wins rule matches `registerExtension`
 * which already replaces a prior registration by extension `id`; this
 * extends it to per-panel identity so a hosted extension can override a
 * single default panel without re-registering the whole extension.
 */
function sortDrawerPanels(entries: DrawerPanel[]): DrawerPanel[] {
  const seen = new Set<string>();
  const deduped: DrawerPanel[] = [];
  // Walk right-to-left so the last registration for a given id wins;
  // flip the accumulator back at the end so the natural registration
  // order is preserved for the stable-sort tiebreaker.
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const p = entries[i];
    if (seen.has(p.id)) continue;
    seen.add(p.id);
    deduped.push(p);
  }
  deduped.reverse();
  return deduped.sort(compareByOrderHint);
}

function compareByOrderHint<T extends { orderHint?: number }>(
  a: T,
  b: T,
): number {
  const ah = a.orderHint ?? Number.POSITIVE_INFINITY;
  const bh = b.orderHint ?? Number.POSITIVE_INFINITY;
  if (ah !== bh) return ah - bh;
  return 0;
}
