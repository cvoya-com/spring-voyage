"use client";

// Engagement-portal shell.
//
// The sidebar shows the live list of engagements (see <EngagementList>) and
// selecting one navigates to /engagement/<id> where {children} renders the
// detail. The "+ New engagement" CTA lives in the top-right of the header,
// mirroring the /units page pattern.

import Link from "next/link";
import { Suspense } from "react";
import { usePathname, useSearchParams } from "next/navigation";
import { MessagesSquare, ArrowLeft, Plus } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { useInbox } from "@/lib/api/queries";
import { EngagementList } from "./engagement-list";

interface EngagementShellProps {
  children: React.ReactNode;
}

/**
 * Global inbox badge: total count of engagements that have an unanswered
 * question from a unit/agent awaiting the current human.
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

/**
 * Pull the currently-selected thread id out of /engagement/<id>. Returns
 * undefined for /engagement/mine, /engagement/new, and the bare
 * /engagement route so the sidebar list shows no highlight on those pages.
 */
function selectedThreadIdFromPath(pathname: string): string | undefined {
  const m = /^\/engagement\/([^/]+)$/.exec(pathname);
  if (!m) return undefined;
  const id = m[1];
  if (id === "mine" || id === "new") return undefined;
  return id;
}

/**
 * Sidebar list lives in its own component so the `useSearchParams()` /
 * `usePathname()` call sites stay inside a Suspense boundary — Next.js
 * requires that for any client hook that reads URL state, otherwise
 * static prerendering of /engagement/mine bails out.
 */
function EngagementSidebarList() {
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const unit = searchParams.get("unit") ?? undefined;
  const agent = searchParams.get("agent") ?? undefined;
  const slice: "mine" | "unit" | "agent" = unit
    ? "unit"
    : agent
      ? "agent"
      : "mine";

  const selectedThreadId = selectedThreadIdFromPath(pathname);

  return (
    <EngagementList
      slice={slice}
      unit={unit}
      agent={agent}
      selectedThreadId={selectedThreadId}
      variant="sidebar"
    />
  );
}

function EngagementSidebarFallback() {
  return (
    <div className="space-y-2" aria-hidden="true">
      <Skeleton className="h-10 w-full rounded-md" />
      <Skeleton className="h-10 w-full rounded-md" />
      <Skeleton className="h-10 w-full rounded-md" />
    </div>
  );
}

export function EngagementShell({ children }: EngagementShellProps) {
  return (
    <div
      data-testid="engagement-shell"
      className="-m-4 flex min-h-full flex-col md:-m-6"
    >
      {/* Engagement portal header band */}
      <header
        data-testid="engagement-header"
        className="flex items-center justify-between gap-2 border-b border-border bg-secondary px-4 py-3"
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
          <span className="md:hidden" aria-hidden="true">
            <GlobalInboxBadge />
          </span>
        </div>

        <div className="flex items-center gap-2">
          <Link
            href="/"
            data-testid="engagement-back-to-management"
            className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <ArrowLeft className="h-3 w-3" aria-hidden="true" />
            Back to Management
          </Link>

          <Link
            href="/engagement/new"
            data-testid="engagement-new-cta"
            className="inline-flex h-8 items-center justify-center gap-1 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />
            New engagement
          </Link>
        </div>
      </header>

      <div className="flex min-h-0 flex-1">
        {/* Engagement sidebar — the live list of threads. */}
        <aside
          aria-label="Engagement list"
          data-testid="engagement-sidebar"
          className="hidden w-72 shrink-0 flex-col gap-3 overflow-y-auto border-r border-border bg-card px-3 py-3 md:flex"
        >
          <Suspense fallback={<EngagementSidebarFallback />}>
            <EngagementSidebarList />
          </Suspense>
        </aside>

        {/* Fix 3 (#1502): remove overflow-y-auto and padding from main so
            the engagement-detail flex column owns its own scroll budget.
            The timeline scrolls independently; the composer stays pinned
            at the bottom of the detail pane. Non-engagement routes (new,
            mine) are wrapped to restore padding via their own containers. */}
        <main
          id="engagement-main-content"
          tabIndex={-1}
          className="flex min-h-0 min-w-0 flex-1 flex-col focus:outline-none"
        >
          {children}
        </main>
      </div>
    </div>
  );
}
