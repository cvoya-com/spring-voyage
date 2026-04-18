"use client";

import { cn } from "@/lib/utils";
import { useTheme } from "@/lib/theme";
import { NAV_SECTION_ORDER, useExtensions } from "@/lib/extensions";
import type { NavSection, RouteEntry } from "@/lib/extensions";
import { Menu, Moon, Settings, Sun, X } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMemo, useState, type ReactNode } from "react";

interface SidebarProps {
  /**
   * Called when the user activates the sidebar-footer Settings trigger.
   * Hoisted up to `AppShell` so the drawer's focus trap and scroll lock
   * live at the shell level rather than inside the sidebar.
   */
  onOpenSettings?: () => void;
}

export function Sidebar({ onOpenSettings }: SidebarProps = {}) {
  const pathname = usePathname();
  const { theme, toggleTheme } = useTheme();
  const [mobileOpen, setMobileOpen] = useState(false);
  // Auto-close the mobile drawer when the route changes. Using the
  // "adjusting state while rendering" pattern (React docs:
  // https://react.dev/reference/react/useState#storing-information-from-previous-renders)
  // avoids the `react-hooks/set-state-in-effect` cascading-render warning
  // that a `useEffect` resetting this same state would produce.
  const [lastPathname, setLastPathname] = useState(pathname);
  if (pathname !== lastPathname) {
    setLastPathname(pathname);
    setMobileOpen(false);
  }

  const { routes, slots, auth } = useExtensions();

  // Route manifest → grouped sections. The sidebar never hard-codes
  // a route list — it reads whatever the (OSS + hosted) extension
  // registry supplies. See `src/lib/extensions/README.md`.
  const sections = useMemo(
    () => groupVisibleRoutes(routes, (perm) => auth.hasPermission(perm)),
    [routes, auth],
  );

  const sidebarContent = (
    <>
      <div className="flex items-center justify-between px-4 py-4">
        <span className="text-lg font-bold">Spring Voyage</span>
        <button
          onClick={() => setMobileOpen(false)}
          className="md:hidden rounded-md p-1 text-muted-foreground hover:text-foreground"
          aria-label="Close sidebar"
        >
          <X className="h-5 w-5" />
        </button>
      </div>

      <nav className="flex-1 space-y-4 px-2 py-2 overflow-y-auto">
        {sections.map((section) => (
          <SidebarSection
            key={section.id}
            section={section.id}
            entries={section.entries}
            pathname={pathname}
          />
        ))}
      </nav>

      <div className="border-t border-border px-4 py-3 space-y-2">
        {slots.sidebarFooter ? (
          <div data-testid="sidebar-footer-slot">
            {slots.sidebarFooter as ReactNode}
          </div>
        ) : null}
        {onOpenSettings ? (
          <button
            onClick={onOpenSettings}
            className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            data-testid="sidebar-settings-trigger"
            aria-haspopup="dialog"
          >
            <Settings className="h-4 w-4" />
            Settings
          </button>
        ) : null}
        <div className="flex items-center justify-between">
          <span className="text-xs text-muted-foreground">Spring Voyage v2</span>
          <button
            onClick={toggleTheme}
            className="rounded-md p-1 text-muted-foreground hover:text-foreground"
            title={`Switch to ${theme === "dark" ? "light" : "dark"} mode`}
          >
            {theme === "dark" ? (
              <Sun className="h-3.5 w-3.5" />
            ) : (
              <Moon className="h-3.5 w-3.5" />
            )}
          </button>
        </div>
      </div>
    </>
  );

  return (
    <>
      <button
        onClick={() => setMobileOpen(true)}
        className="fixed top-3 left-3 z-40 rounded-md border border-border bg-card p-2 text-muted-foreground hover:text-foreground md:hidden"
        aria-label="Open sidebar"
      >
        <Menu className="h-5 w-5" />
      </button>

      {mobileOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/50 md:hidden"
          onClick={() => setMobileOpen(false)}
        />
      )}

      <aside
        className={cn(
          "fixed inset-y-0 left-0 z-50 flex w-56 flex-col border-r border-border bg-card transition-transform duration-200 md:hidden",
          mobileOpen ? "translate-x-0" : "-translate-x-full"
        )}
      >
        {sidebarContent}
      </aside>

      <aside className="hidden md:flex h-screen w-56 flex-col border-r border-border bg-card">
        {sidebarContent}
      </aside>
    </>
  );
}

interface GroupedSection {
  id: NavSection;
  entries: readonly RouteEntry[];
}

function groupVisibleRoutes(
  routes: readonly RouteEntry[],
  hasPermission: (key: string) => boolean,
): readonly GroupedSection[] {
  const bySection = new Map<NavSection, RouteEntry[]>();
  for (const route of routes) {
    if (route.permission && !hasPermission(route.permission)) continue;
    const bucket = bySection.get(route.navSection) ?? [];
    bucket.push(route);
    bySection.set(route.navSection, bucket);
  }

  const ordered: GroupedSection[] = [];
  for (const id of NAV_SECTION_ORDER) {
    const entries = bySection.get(id);
    if (entries && entries.length > 0) {
      ordered.push({ id, entries });
    }
  }
  return ordered;
}

function SidebarSection({
  section,
  entries,
  pathname,
}: {
  section: NavSection;
  entries: readonly RouteEntry[];
  pathname: string;
}) {
  return (
    <div className="space-y-1" data-testid={`sidebar-section-${section}`}>
      {section !== "primary" && (
        <div className="px-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
          {section}
        </div>
      )}
      {entries.map((item) => (
        <NavLink key={item.path} item={item} pathname={pathname} />
      ))}
    </div>
  );
}

function NavLink({ item, pathname }: { item: RouteEntry; pathname: string }) {
  const active =
    item.path === "/"
      ? pathname === "/"
      : pathname === item.path || pathname.startsWith(item.path + "/");

  const Icon = item.icon;

  return (
    <Link
      href={item.path}
      className={cn(
        "flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors",
        active
          ? "bg-primary/10 text-primary font-medium"
          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
      )}
    >
      <Icon className="h-4 w-4" />
      {item.label}
    </Link>
  );
}
