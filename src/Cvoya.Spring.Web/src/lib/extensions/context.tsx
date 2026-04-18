"use client";

// React wiring for the extension registry. A single provider mounts
// near the root of the app shell; components read the merged view via
// the hooks below.
//
// Using a React context (not just the module singleton) means tests
// can swap the registry contents via a test-only provider and have
// the sidebar / palette pick up the change deterministically.

import {
  createContext,
  useContext,
  useMemo,
  type ReactNode,
} from "react";

import {
  computeMergedExtensions,
  type MergedExtensions,
} from "./registry";
import type {
  DrawerPanel,
  IAuthContext,
  PaletteAction,
  RouteEntry,
} from "./types";

const ExtensionContext = createContext<MergedExtensions | null>(null);

/**
 * Mounts the merged extension view into the React tree. Callers that
 * want to override the view in tests can pass `override`.
 */
export function ExtensionProvider({
  children,
  override,
}: {
  children: ReactNode;
  override?: MergedExtensions;
}) {
  const value = useMemo(
    () => override ?? computeMergedExtensions(),
    [override],
  );
  return (
    <ExtensionContext.Provider value={value}>
      {children}
    </ExtensionContext.Provider>
  );
}

/**
 * Read the merged extension view. Falls back to a freshly computed
 * view if no provider has mounted — keeps Storybook-style isolated
 * renders working without ceremony.
 */
export function useExtensions(): MergedExtensions {
  const ctx = useContext(ExtensionContext);
  // Deliberately uncached when no provider is present: the hook is
  // designed for components that sit inside the app shell; the
  // provider-less branch exists only so ad-hoc test harnesses don't
  // crash.
  return ctx ?? computeMergedExtensions();
}

export function useRoutes(): readonly RouteEntry[] {
  return useExtensions().routes;
}

export function usePaletteActions(): readonly PaletteAction[] {
  return useExtensions().actions;
}

export function useAuthContext(): IAuthContext {
  return useExtensions().auth;
}

/**
 * Merged drawer panels (OSS defaults + hosted extensions), already
 * sorted by `orderHint`. The Settings drawer is the only consumer
 * today; exporting it keeps the consumer surface symmetrical with the
 * routes/actions hooks.
 */
export function useDrawerPanels(): readonly DrawerPanel[] {
  return useExtensions().drawerPanels;
}
