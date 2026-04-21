"use client";

import { Sidebar } from "@/components/sidebar";
import { CommandPaletteProvider } from "@/components/command-palette";
import { ExtensionProvider } from "@/lib/extensions";
import type { ReactNode } from "react";

/**
 * Portal shell. Wraps every route with the extension registry, the
 * command palette, and the sidebar chrome.
 *
 * Settings are reached via the `/settings` route (plan §2 of the v2
 * design-system rollout — umbrella #815). The legacy `<SettingsDrawer>`
 * mount + `onOpenSettings` plumbing was retired in IA-appshell.
 */
export function AppShell({ children }: { children: ReactNode }) {
  return (
    <ExtensionProvider>
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
    </ExtensionProvider>
  );
}
