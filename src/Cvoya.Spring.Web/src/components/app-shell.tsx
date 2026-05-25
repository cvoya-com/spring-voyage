"use client";

import { Sidebar } from "@/components/sidebar";
import { CommandPaletteProvider } from "@/components/command-palette";
import { ExplorerSelectionProvider } from "@/components/units/explorer-selection-context";
import { ExtensionProvider } from "@/lib/extensions";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { ReactNode } from "react";

/**
 * Portal shell. Wraps every route with the extension registry, the
 * Explorer-selection bridge, the command palette, and the sidebar
 * chrome.
 *
 * Settings are reached via the `/settings` route (plan §2 of the v2
 * design-system rollout — umbrella #815). The legacy in-shell settings
 * drawer + its `onOpenSettings` plumbing was retired in IA-appshell
 * (#896) and fully dropped in SET-drop-drawer (#867).
 *
 * The `<ExplorerSelectionProvider>` owns the Cmd-K ⇄ Explorer bridge
 * introduced in EXP-cmdk-bridge: selecting a node in the palette
 * dispatches into a mounted `<UnitExplorer>` when one is present,
 * otherwise the palette navigates to `/explorer/units/<id>` (#2473).
 *
 * #2528: the global `useActivityStream()` call here keeps the TanStack
 * Query cache invalidated for every page — previously only routes that
 * explicitly mounted the hook (Dashboard, Activity tab, Validation
 * panel) saw live updates, so navigating to the Explorer and creating
 * or deleting a unit left the tree stale until a manual reload. The
 * hook's `events` array is unused here; we mount only for the cache-
 * invalidation side effect.
 */
export function AppShell({ children }: { children: ReactNode }) {
  useActivityStream();

  return (
    <ExtensionProvider>
      <ExplorerSelectionProvider>
        <CommandPaletteProvider>
          <Sidebar />
          {/* `min-w-0` lets the flex main pane shrink below its
              intrinsic content width when a descendant carries a fixed
              pixel width — without it, flexbox pins main to the widest
              child and the sidebar + page overflow horizontally on
              narrow viewports. */}
          <main
            id="main-content"
            tabIndex={-1}
            className="flex-1 min-w-0 overflow-y-auto p-4 md:p-6 pt-14 md:pt-6 focus:outline-none"
          >
            {children}
          </main>
        </CommandPaletteProvider>
      </ExplorerSelectionProvider>
    </ExtensionProvider>
  );
}
