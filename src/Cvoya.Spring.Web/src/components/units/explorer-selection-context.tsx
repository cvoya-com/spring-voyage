"use client";

import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  type ReactNode,
} from "react";

/**
 * Bridge between the command palette and the canonical Explorer at
 * `/units`. The palette cannot call `<UnitExplorer>` directly — the
 * palette lives in `<AppShell>` while the Explorer mounts per-route —
 * so the palette dispatches a node id through this context and the
 * Explorer subscribes.
 *
 * Teleport semantics (EXP-cmdk-bridge, umbrella #815):
 *
 *   - When the palette activates a node entry and an Explorer is
 *     currently mounted under the same shell, `dispatchSelect(id)`
 *     forwards the id to the mounted Explorer's `setSelected`.
 *   - Otherwise the palette navigates to `/units?node=<id>`; the
 *     Explorer reads the URL on first render so the post-navigation
 *     selection is already correct.
 *
 * The context holds a ref, not React state: the Explorer registers its
 * `setSelected` callback once on mount and the palette reads it
 * synchronously at dispatch time. Using a ref avoids re-renders on
 * every Explorer mount/unmount.
 */
export interface ExplorerSelectionContextValue {
  /**
   * True iff an Explorer is currently mounted under this context.
   * Palette uses this to pick between "teleport in-place" and
   * "navigate + select on arrival".
   */
  hasListener: () => boolean;
  /**
   * Dispatch a node-id to the mounted Explorer. No-ops when no
   * Explorer is mounted — callers must check {@link hasListener}
   * first if they need to fall back to navigation.
   */
  dispatchSelect: (id: string) => void;
  /**
   * Register the Explorer's `setSelected` callback. Call from a
   * `useEffect` cleanup unsubscribe on unmount. Returns the cleanup
   * function for convenience.
   */
  registerListener: (fn: (id: string) => void) => () => void;
}

const noopContext: ExplorerSelectionContextValue = {
  hasListener: () => false,
  dispatchSelect: () => {},
  registerListener: () => () => {},
};

const ExplorerSelectionContext =
  createContext<ExplorerSelectionContextValue>(noopContext);

/**
 * Provider that owns the Explorer-selection ref. Mount once in the
 * shell so both the command palette and the Explorer are descendants.
 */
export function ExplorerSelectionProvider({
  children,
}: {
  children: ReactNode;
}) {
  const listenerRef = useRef<((id: string) => void) | null>(null);

  const hasListener = useCallback(() => listenerRef.current !== null, []);
  const dispatchSelect = useCallback((id: string) => {
    listenerRef.current?.(id);
  }, []);
  const registerListener = useCallback((fn: (id: string) => void) => {
    listenerRef.current = fn;
    return () => {
      if (listenerRef.current === fn) {
        listenerRef.current = null;
      }
    };
  }, []);

  const value = useMemo<ExplorerSelectionContextValue>(
    () => ({ hasListener, dispatchSelect, registerListener }),
    [hasListener, dispatchSelect, registerListener],
  );

  return (
    <ExplorerSelectionContext.Provider value={value}>
      {children}
    </ExplorerSelectionContext.Provider>
  );
}

/**
 * Consume the Explorer-selection bridge. Safe to call anywhere inside
 * `<AppShell>`; returns a no-op fallback when no provider is mounted
 * (e.g. isolated component tests that don't wrap in the provider).
 */
export function useExplorerSelection(): ExplorerSelectionContextValue {
  return useContext(ExplorerSelectionContext);
}
