"use client";

// Engagement-portal shell (E2.3, #1415).
//
// Renders inside the root AppShell's <main> area. Provides the chrome that
// visually distinguishes the engagement portal from the management portal:
//
//   ┌─────────────────────────────────────────────────────────┐
//   │ [conversations icon] Engagement  · Spring Voyage   [Back to Management →] │
//   ├────────────────────┬────────────────────────────────────┤
//   │  My engagements    │                                    │
//   │  (mine link)       │   {children}                      │
//   └────────────────────┴────────────────────────────────────┘
//
// The engagement header band uses bg-secondary (darker than the main canvas)
// and the voyage-cyan accent to signal a different surface. Per ADR-0033,
// links between portals are standard anchors — no shared shell components
// cross the route boundary.

import Link from "next/link";
import { usePathname } from "next/navigation";
import { MessagesSquare, ArrowLeft } from "lucide-react";
import { cn } from "@/lib/utils";
import { useInbox } from "@/lib/api/queries";

interface EngagementShellProps {
  children: React.ReactNode;
}

interface NavEntry {
  href: string;
  label: string;
  /** Match exact path or any child path */
  exact?: boolean;
}

const ENGAGEMENT_NAV: readonly NavEntry[] = [
  { href: "/engagement/mine", label: "My engagements", exact: false },
  // #1455: dedicated entry point for kicking off a new engagement
  // with one or more participants.
  { href: "/engagement/new", label: "New engagement", exact: true },
];

/**
 * Thin nav link inside the engagement sidebar. Active when the current
 * pathname matches the entry's href (exact or prefix).
 */
function EngagementNavLink({
  entry,
  pathname,
}: {
  entry: NavEntry;
  pathname: string;
}) {
  const active = entry.exact
    ? pathname === entry.href
    : pathname === entry.href || pathname.startsWith(entry.href + "/");

  return (
    <Link
      href={entry.href}
      aria-current={active ? "page" : undefined}
      data-testid={`engagement-nav-${entry.href.replace(/\//g, "-").replace(/^-/, "")}`}
      className={cn(
        "flex items-center rounded-md px-3 py-2 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        active
          ? "bg-primary/10 text-primary font-medium"
          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
      )}
    >
      {entry.label}
      {entry.href === "/engagement/mine" && <GlobalInboxBadge />}
    </Link>
  );
}

/**
 * Global inbox badge: total count of engagements that have an unanswered
 * question from a unit/agent awaiting the current human. Computed from the
 * inbox endpoint (GET /api/v1/tenant/inbox) which returns items where the
 * human is the intended next responder.
 */
function GlobalInboxBadge() {
  const inbox = useInbox({ staleTime: 30_000 });
  const count = inbox.data?.length ?? 0;

  if (count === 0) return null;

  return (
    <span
      className="ml-1 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-warning px-1 text-[10px] font-semibold tabular-nums text-warning-foreground"
      aria-label={`${count} unanswered question${count === 1 ? "" : "s"}`}
      data-testid="engagement-inbox-badge"
    >
      {count > 99 ? "99+" : count}
    </span>
  );
}

export function EngagementShell({ children }: EngagementShellProps) {
  const pathname = usePathname();

  return (
    // Negative margin compensates for the AppShell <main>'s padding so the
    // engagement chrome fills the full pane edge-to-edge.
    <div
      data-testid="engagement-shell"
      className="-m-4 md:-m-6 flex flex-col min-h-full"
    >
      {/* Engagement portal header band */}
      <header
        data-testid="engagement-header"
        className="flex items-center justify-between border-b border-border bg-secondary px-4 py-3"
      >
        <div className="flex items-center gap-2">
          <MessagesSquare
            className="h-4 w-4 text-voyage"
            aria-hidden="true"
          />
          <span className="text-sm font-semibold">Engagement</span>
          <span
            className="hidden font-mono text-[10px] uppercase tracking-wider text-muted-foreground sm:inline"
            aria-hidden="true"
          >
            · Spring Voyage
          </span>
          {/* Global pending-question count badge — visible on mobile only.
              On desktop, the badge appears on the "My engagements" nav link. */}
          <span className="md:hidden" aria-hidden="true">
            <GlobalInboxBadge />
          </span>
        </div>

        {/* Cross-portal anchor: back to the management portal.
            Per ADR-0033 rule 6: cross-portal navigation is a standard anchor. */}
        <Link
          href="/"
          data-testid="engagement-back-to-management"
          className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ArrowLeft className="h-3 w-3" aria-hidden="true" />
          Back to Management
        </Link>
      </header>

      {/* Two-pane layout: sidebar (left) + content (right) */}
      <div className="flex flex-1 min-h-0">
        {/* Engagement sidebar */}
        <nav
          aria-label="Engagement navigation"
          data-testid="engagement-sidebar"
          className="hidden w-48 shrink-0 border-r border-border bg-card px-2 py-3 md:flex md:flex-col"
        >
          <div
            className="mb-1 px-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
            aria-hidden="true"
          >
            Engagements
          </div>
          {ENGAGEMENT_NAV.map((entry) => (
            <EngagementNavLink
              key={entry.href}
              entry={entry}
              pathname={pathname}
            />
          ))}
        </nav>

        {/* Page content */}
        <main
          id="engagement-main-content"
          tabIndex={-1}
          className="flex-1 min-w-0 overflow-y-auto p-4 md:p-6 focus:outline-none"
        >
          {children}
        </main>
      </div>
    </div>
  );
}
