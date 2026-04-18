"use client";

import { Sidebar } from "@/components/sidebar";
import { CommandPaletteProvider } from "@/components/command-palette";
import { SettingsDrawer } from "@/components/settings-drawer";
import { ExtensionProvider } from "@/lib/extensions";
import { useState, type ReactNode } from "react";

export function AppShell({ children }: { children: ReactNode }) {
  // Drawer state lives at the shell level so the focus trap and body
  // scroll lock compose with the rest of the portal (sidebar, command
  // palette). The trigger sits in the sidebar footer (§ 3.2 of the
  // portal design doc — "bottom-sidebar Settings drawer").
  const [settingsOpen, setSettingsOpen] = useState(false);
  return (
    <ExtensionProvider>
      <CommandPaletteProvider>
        <Sidebar onOpenSettings={() => setSettingsOpen(true)} />
        <main className="flex-1 overflow-y-auto p-4 md:p-6 pt-14 md:pt-6">
          {children}
        </main>
        <SettingsDrawer
          open={settingsOpen}
          onClose={() => setSettingsOpen(false)}
        />
      </CommandPaletteProvider>
    </ExtensionProvider>
  );
}
