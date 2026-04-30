"use client";

import {
  ChevronLeft,
  ChevronRight,
  Menu,
  Moon,
  Sun,
  X,
} from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useMemo, useState, type ReactNode } from "react";

import { BrandMark } from "@/components/brand-mark";
import { Tooltip } from "@/components/ui/tooltip";
import {
  NAV_SECTION_LABEL,
  NAV_SECTION_ORDER,
  useExtensions,
} from "@/lib/extensions";
import type { NavSection, RouteEntry } from "@/lib/extensions";
import { usePlatformInfo } from "@/lib/api/queries";
import { useTheme } from "@/lib/theme";
import { cn } from "@/lib/utils";

// Dimensions from plan §8 — expanded/collapsed widths are load-bearing
// for the canvas the main pane gets, so keep them as named constants
// rather than magic numbers scattered through the markup.
const SIDEBAR_EXPANDED_PX = 224;
const SIDEBAR_COLLAPSED_PX = 56;
const COLLAPSE_STORAGE_KEY = "spring-voyage-sidebar-collapsed";

const OSS_ENV_LABEL = "local-dev";

// Safe localStorage read — jsdom, opaque-origin contexts, and
// privacy-hardened browsers can throw on access, so the collapse
// preference degrades silently to "expanded" when storage is
// unavailable.
function readStoredCollapsed(): boolean {
  if (typeof window === "undefined") return false;
  try {
    return window.localStorage.getItem(COLLAPSE_STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

function writeStoredCollapsed(collapsed: boolean): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(COLLAPSE_STORAGE_KEY, collapsed ? "1" : "0");
  } catch {
    // Storage unavailable — preference is session-only, which is fine.
  }
}

/**
 * Top-level sidebar chrome introduced in IA-sidebar-chrome (plan §8):
 * BrandMark + wordmark header with an env pill, grouped route manifest
 * (Overview / Orchestrate / Control / Settings), and a footer carrying
 * the signed-in user, theme toggle, and version pill. The legacy
 * in-sidebar Settings-drawer trigger is gone — `/settings` is now a
 * route under the Control group.
 */
export function Sidebar() {
  const pathname = usePathname();
  const { routes, auth } = useExtensions();
  const [mobileOpen, setMobileOpen] = useState(false);
  const [collapsed, setCollapsed] = useState<boolean>(() =>
    readStoredCollapsed(),
  );
  const toggleCollapsed = () => {
    setCollapsed((prev) => {
      writeStoredCollapsed(!prev);
      return !prev;
    });
  };

  // Keyboard shortcut: Cmd+\ (Mac) / Ctrl+\ (Windows/Linux).
  // Mirrors VS Code's sidebar-toggle affordance (high familiarity) but
  // uses backslash instead of Shift+B to avoid colliding with browser
  // print / bold shortcuts. The handler is a no-op when focus is on an
  // editable element so typing backslash in a text field is unaffected.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (!e.key || e.key !== "\\") return;
      if (!(e.metaKey || e.ctrlKey)) return;
      const target = e.target as HTMLElement | null;
      if (
        target &&
        (target.tagName === "INPUT" ||
          target.tagName === "TEXTAREA" ||
          target.tagName === "SELECT" ||
          target.isContentEditable)
      ) {
        return;
      }
      e.preventDefault();
      toggleCollapsed();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
    // toggleCollapsed reads collapsed via the setCollapsed functional
    // updater — the effect doesn't need to re-run when collapsed changes.
  }, []);

  // Auto-close the mobile drawer when the route changes. Using the
  // "adjusting state while rendering" pattern (React docs:
  // https://react.dev/reference/react/useState#storing-information-from-previous-renders)
  // avoids the `react-hooks/set-state-in-effect` cascading-render warning.
  const [lastPathname, setLastPathname] = useState(pathname);
  if (pathname !== lastPathname) {
    setLastPathname(pathname);
    setMobileOpen(false);
  }

  const sections = useMemo(
    () => groupVisibleRoutes(routes, (perm) => auth.hasPermission(perm)),
    [routes, auth],
  );

  const sidebarContent = (
    <SidebarContent
      sections={sections}
      pathname={pathname}
      collapsed={collapsed}
      toggleCollapsed={toggleCollapsed}
      onMobileClose={() => setMobileOpen(false)}
    />
  );

  return (
    <>
      {/* Skip-to-content shortcut for keyboard users. Hidden visually
          until focused; target is the `<main id="main-content">` landmark
          rendered by `AppShell`. Matches the WCAG 2.1 "Bypass Blocks"
          criterion (2.4.1). */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-[100] focus:rounded-md focus:border focus:border-border focus:bg-card focus:px-3 focus:py-2 focus:text-sm focus:font-medium focus:text-foreground focus:shadow-lg focus:outline-none focus:ring-2 focus:ring-ring"
        data-testid="skip-to-main"
      >
        Skip to main content
      </a>

      <button
        onClick={() => setMobileOpen(true)}
        className="fixed top-3 left-3 z-40 rounded-md border border-border bg-card p-2 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring md:hidden"
        aria-label="Open sidebar"
        aria-expanded={mobileOpen}
        aria-controls="mobile-sidebar"
      >
        <Menu className="h-5 w-5" aria-hidden="true" />
      </button>

      {mobileOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/50 md:hidden"
          onClick={() => setMobileOpen(false)}
          aria-hidden="true"
        />
      )}

      <aside
        id="mobile-sidebar"
        aria-label="Sidebar navigation"
        className={cn(
          "fixed inset-y-0 left-0 z-50 flex flex-col border-r border-border bg-card transition-transform duration-200 md:hidden",
          mobileOpen ? "translate-x-0" : "-translate-x-full",
        )}
        style={{ width: SIDEBAR_EXPANDED_PX }}
      >
        {sidebarContent}
      </aside>

      <aside
        aria-label="Sidebar navigation"
        data-collapsed={collapsed || undefined}
        className="hidden md:flex h-screen flex-col border-r border-border bg-card transition-[width] duration-150"
        style={{
          width: collapsed ? SIDEBAR_COLLAPSED_PX : SIDEBAR_EXPANDED_PX,
        }}
      >
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

function SidebarContent({
  sections,
  pathname,
  collapsed,
  toggleCollapsed,
  onMobileClose,
}: {
  sections: readonly GroupedSection[];
  pathname: string;
  collapsed: boolean;
  toggleCollapsed: () => void;
  onMobileClose: () => void;
}) {
  return (
    <>
      <SidebarHeader collapsed={collapsed} onMobileClose={onMobileClose} />

      <nav
        aria-label="Primary"
        className="flex-1 space-y-4 px-2 py-2 overflow-y-auto"
      >
        {sections.map((section) => (
          <SidebarSection
            key={section.id}
            section={section.id}
            entries={section.entries}
            pathname={pathname}
            collapsed={collapsed}
          />
        ))}
      </nav>

      <SidebarFooter collapsed={collapsed} toggleCollapsed={toggleCollapsed} />
    </>
  );
}

function SidebarHeader({
  collapsed,
  onMobileClose,
}: {
  collapsed: boolean;
  onMobileClose: () => void;
}) {
  return (
    <div
      data-testid="sidebar-header"
      className={cn(
        "flex items-center gap-2 border-b border-border px-3 py-3",
        collapsed && "justify-center px-0",
      )}
    >
      <BrandMark size={24} className="shrink-0" />
      {collapsed ? null : (
        <div className="flex min-w-0 flex-1 flex-col">
          <span className="truncate text-sm font-semibold">Spring Voyage</span>
          <span
            data-testid="sidebar-env-pill"
            className="truncate font-mono text-[10px] uppercase tracking-wider text-muted-foreground"
          >
            env · {OSS_ENV_LABEL}
          </span>
        </div>
      )}
      <button
        onClick={onMobileClose}
        className="md:hidden rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        aria-label="Close sidebar"
      >
        <X className="h-5 w-5" aria-hidden="true" />
      </button>
    </div>
  );
}

function SidebarSection({
  section,
  entries,
  pathname,
  collapsed,
}: {
  section: NavSection;
  entries: readonly RouteEntry[];
  pathname: string;
  collapsed: boolean;
}) {
  return (
    <div className="space-y-1" data-testid={`sidebar-section-${section}`}>
      {collapsed ? null : (
        <div
          data-testid={`sidebar-section-label-${section}`}
          className="px-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
        >
          {NAV_SECTION_LABEL[section]}
        </div>
      )}
      {entries.map((item) => (
        <NavLink
          key={item.path}
          item={item}
          pathname={pathname}
          collapsed={collapsed}
        />
      ))}
    </div>
  );
}

function NavLink({
  item,
  pathname,
  collapsed,
  badge,
}: {
  item: RouteEntry;
  pathname: string;
  collapsed: boolean;
  badge?: NavItemBadgeSpec;
}) {
  const active =
    item.path === "/"
      ? pathname === "/"
      : pathname === item.path || pathname.startsWith(item.path + "/");

  const Icon = item.icon;

  // On the collapsed rail the visible label is gone, so a hover/focus
  // tooltip is how the user confirms a route without clicking. We ring
  // the focus ring *inside* the 56 px rail (`focus-visible:ring-inset`)
  // so the 2 px outline isn't clipped by the sidebar's right border.
  const link = (
    <Link
      href={item.path}
      aria-current={active ? "page" : undefined}
      data-testid={`sidebar-nav-link-${item.path}`}
      className={cn(
        "relative flex items-center gap-2 rounded-md text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        collapsed
          ? "justify-center px-2 py-2 focus-visible:ring-inset"
          : "px-3 py-2",
        active
          ? "bg-primary/10 text-primary font-medium"
          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
      )}
    >
      <span className="relative inline-flex shrink-0" data-slot="nav-icon">
        <Icon className="h-4 w-4" aria-hidden="true" />
        {badge ? <NavItemBadge spec={badge} collapsed={collapsed} /> : null}
      </span>
      {collapsed ? (
        <span className="sr-only">
          {item.label}
          {item.secondaryLabel ? ` ${item.secondaryLabel}` : null}
        </span>
      ) : (
        // #1454: routes can carry a `secondaryLabel` (e.g. `(experimental)`)
        // rendered subordinately below the primary label.
        <span className="flex min-w-0 flex-col leading-tight">
          <span className="truncate">{item.label}</span>
          {item.secondaryLabel ? (
            <span
              data-testid={`sidebar-nav-link-${item.path}-secondary`}
              className="truncate text-[10px] font-normal opacity-70"
            >
              {item.secondaryLabel}
            </span>
          ) : null}
        </span>
      )}
    </Link>
  );

  return (
    <Tooltip
      label={
        item.secondaryLabel
          ? `${item.label} ${item.secondaryLabel}`
          : item.label
      }
      side="right"
      enabled={collapsed}
    >
      {link}
    </Tooltip>
  );
}

/**
 * Status-dot / unread-count pattern for sidebar nav items. When the
 * rail is collapsed, badges anchor to the top-right of the icon box so
 * they stay inside the 56 px rail and don't get clipped under the
 * group divider. When the rail is expanded, they ride alongside the
 * icon so the label has room to breathe.
 *
 * Callers describe the badge via {@link NavItemBadgeSpec}; this helper
 * handles positioning + accessible labelling so nav code stays
 * declarative. `ariaLabel` is wired onto the badge wrapper so assistive
 * tech announces "3 unread, Inbox" instead of "Inbox".
 */
export interface NavItemBadgeSpec {
  /**
   * Accessible label describing the badge content (e.g. "3 unread"
   * or "connector error"). Rendered as the badge's `aria-label`.
   */
  ariaLabel: string;
  /** Tone — maps to one of the semantic color tokens. */
  tone?: "primary" | "success" | "warning" | "destructive";
  /**
   * Optional visible count. When omitted the badge renders as a
   * status dot; when present, a small numeric pill.
   */
  count?: number;
}

const BADGE_TONE: Record<NonNullable<NavItemBadgeSpec["tone"]>, string> = {
  primary: "bg-primary text-primary-foreground",
  success: "bg-success text-primary-foreground",
  warning: "bg-warning text-primary-foreground",
  destructive: "bg-destructive text-primary-foreground",
};

export function NavItemBadge({
  spec,
  collapsed,
}: {
  spec: NavItemBadgeSpec;
  collapsed: boolean;
}) {
  const tone = spec.tone ?? "primary";
  const hasCount = typeof spec.count === "number";
  const displayCount =
    hasCount && spec.count! > 99 ? "99+" : String(spec.count ?? "");

  return (
    <span
      role="status"
      aria-label={spec.ariaLabel}
      data-slot="badge"
      data-collapsed={collapsed || undefined}
      className={cn(
        // Anchor top-right of the icon box; a 2 px ring of card colour
        // keeps the badge legible against both the rail and any hover
        // background without clipping under the group divider.
        "absolute -top-1 -right-1 inline-flex items-center justify-center rounded-full ring-2 ring-card",
        hasCount ? "min-w-4 px-1 text-[10px] font-semibold leading-4" : "h-2 w-2",
        BADGE_TONE[tone],
      )}
    >
      {hasCount ? displayCount : null}
    </span>
  );
}

function SidebarFooter({
  collapsed,
  toggleCollapsed,
}: {
  collapsed: boolean;
  toggleCollapsed: () => void;
}) {
  const { auth, slots } = useExtensions();
  const user = auth.getUser();
  const platform = usePlatformInfo({ staleTime: 60 * 1000 });
  const version = platform.data?.version ?? "dev";

  return (
    <div
      data-testid="sidebar-footer"
      className="border-t border-border p-2 text-xs"
    >
      {slots.sidebarFooter ? (
        <div data-testid="sidebar-footer-slot" className="mb-2">
          {slots.sidebarFooter as ReactNode}
        </div>
      ) : null}

      <UserBlock user={user} collapsed={collapsed} />

      <div
        className={cn(
          "mt-2 flex items-center",
          collapsed ? "flex-col gap-2" : "justify-between gap-2",
        )}
      >
        <ThemeToggle />
        {collapsed ? null : (
          <span
            data-testid="sidebar-version"
            className="truncate font-mono text-[10px] uppercase tracking-wider text-muted-foreground"
          >
            v{version}
          </span>
        )}
        <CollapseToggle collapsed={collapsed} onToggle={toggleCollapsed} />
      </div>
    </div>
  );
}

function UserBlock({
  user,
  collapsed,
}: {
  user: ReturnType<
    ReturnType<typeof useExtensions>["auth"]["getUser"]
  >;
  collapsed: boolean;
}) {
  const displayName = user?.displayName ?? user?.id ?? "local";
  const email = user?.email;
  const initial = (displayName[0] ?? "?").toUpperCase();

  return (
    <div
      data-testid="sidebar-user"
      className={cn(
        "flex items-center gap-2",
        collapsed && "justify-center",
      )}
    >
      <div className="relative flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-accent text-xs font-semibold text-accent-foreground">
        <span aria-hidden="true">{initial}</span>
        {/* Uses the same slot/anchoring contract as `NavItemBadge`
            (`data-slot="badge"`, ring in card colour) so future
            per-nav status dots pick up identical visuals. A dedicated
            testid keeps the existing sidebar-user assertions stable. */}
        <span
          role="status"
          aria-label="Connected"
          data-testid="sidebar-user-status"
          data-slot="badge"
          className="absolute -bottom-0.5 -right-0.5 h-2 w-2 rounded-full bg-success ring-2 ring-card"
        />
      </div>
      {collapsed ? null : (
        <div className="flex min-w-0 flex-col">
          <span className="truncate text-xs font-medium text-foreground">
            {displayName}
          </span>
          {email ? (
            <span className="truncate text-[10px] text-muted-foreground">
              {email}
            </span>
          ) : null}
        </div>
      )}
    </div>
  );
}

function ThemeToggle() {
  const { theme, toggleTheme } = useTheme();
  const next = theme === "dark" ? "light" : "dark";
  return (
    <button
      onClick={toggleTheme}
      className="rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
      aria-label={`Switch to ${next} mode`}
      data-testid="sidebar-theme-toggle"
    >
      {theme === "dark" ? (
        <Sun className="h-3.5 w-3.5" aria-hidden="true" />
      ) : (
        <Moon className="h-3.5 w-3.5" aria-hidden="true" />
      )}
    </button>
  );
}

function CollapseToggle({
  collapsed,
  onToggle,
}: {
  collapsed: boolean;
  onToggle: () => void;
}) {
  // `aria-expanded` is the load-bearing state for AT; `aria-label`
  // describes the action (expand vs. collapse) so screen readers
  // announce the right verb. Focus ring is `ring-inset` so the 56 px
  // rail doesn't clip the 2 px outline.
  //
  // The title attribute surfaces the keyboard shortcut as a browser
  // tooltip on hover without adding visible text to the 56 px rail.
  // AT already reads the aria-label so adding `title` purely for
  // the pointer hint is safe.
  return (
    <button
      onClick={onToggle}
      data-testid="sidebar-collapse-toggle"
      aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
      aria-expanded={!collapsed}
      aria-controls="mobile-sidebar"
      title={collapsed ? "Expand sidebar (Ctrl+\\)" : "Collapse sidebar (Ctrl+\\)"}
      className="rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
    >
      {collapsed ? (
        <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />
      ) : (
        <ChevronLeft className="h-3.5 w-3.5" aria-hidden="true" />
      )}
    </button>
  );
}
