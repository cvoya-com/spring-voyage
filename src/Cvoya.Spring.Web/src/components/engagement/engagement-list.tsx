"use client";

// Engagement list component (E2.4, #1416).
//
// Renders the sorted list of engagements for three slice contexts:
//   - "mine" (/engagement/mine): threads where the current human is a participant.
//   - per-unit (/engagement/mine?unit=<id>): all threads involving a given unit.
//   - per-agent (/engagement/mine?agent=<id>): all threads involving a given agent.
//
// A2A-only engagements (no human participant) are excluded from the "mine"
// slice but visible from per-unit / per-agent (they can be observed read-only).
//
// Recency-driven sort: latest activity first. Inactive engagements render at
// lower opacity but remain visible; they resurface when new activity arrives.
// No "close" affordance — engagements never close.

import Link from "next/link";
import {
  MessagesSquare,
  AlertCircle,
  Loader2,
  Eye,
  MessageCircleQuestion,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useThreads, useInbox } from "@/lib/api/queries";
import type { ParticipantRef, ThreadSummary } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface EngagementListProps {
  /**
   * Which slice to show.
   *  - "mine": threads where the authenticated human is a participant.
   *    A2A-only threads are excluded.
   *  - "unit": all threads involving a specific unit (id / slug).
   *  - "agent": all threads involving a specific agent (id / slug).
   */
  slice: "mine" | "unit" | "agent";
  /** Unit id / slug — only required when `slice === "unit"`. */
  unit?: string;
  /** Agent id / slug — only required when `slice === "agent"`. */
  agent?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns true when all participants are agents/units (no human:// address).
 * Used to hide A2A-only engagements from the "mine" slice.
 */
function isA2aOnly(participants: ParticipantRef[]): boolean {
  return participants.every(
    (p) => !p.address.startsWith("human://"),
  );
}

/**
 * How "active" an engagement is — drives opacity in the list.
 * Active = last activity within 24 h; recent = within 7 d; otherwise old.
 */
function activityFreshness(
  lastActivity: string,
): "active" | "recent" | "old" {
  const diffMs = Date.now() - new Date(lastActivity).getTime();
  if (diffMs < 24 * 60 * 60 * 1000) return "active";
  if (diffMs < 7 * 24 * 60 * 60 * 1000) return "recent";
  return "old";
}

/**
 * Lightweight relative-time formatter — no external dependency.
 * Examples: "just now", "2 minutes ago", "3 hours ago", "5 days ago".
 */
function formatRelativeTime(dateStr: string): string {
  const diffMs = Date.now() - new Date(dateStr).getTime();
  const secs = Math.floor(diffMs / 1000);
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins} minute${mins === 1 ? "" : "s"} ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} day${days === 1 ? "" : "s"} ago`;
  const months = Math.floor(days / 30);
  if (months < 12) return `${months} month${months === 1 ? "" : "s"} ago`;
  const years = Math.floor(months / 12);
  return `${years} year${years === 1 ? "" : "s"} ago`;
}

const FRESHNESS_OPACITY: Record<string, string> = {
  active: "",
  recent: "opacity-80",
  old: "opacity-50",
};

/**
 * Format the participants list for display. Truncates at 3 with a +N remainder.
 */
function formatParticipants(participants: ParticipantRef[]): string {
  const display = participants.slice(0, 3).map((p) => p.displayName).join(", ");
  const rest = participants.length - 3;
  return rest > 0 ? `${display} (+${rest})` : display;
}

// ---------------------------------------------------------------------------
// Engagement card
// ---------------------------------------------------------------------------

interface EngagementCardProps {
  thread: ThreadSummary;
  /** Whether the inbox has a pending question for this engagement. */
  hasPendingQuestion?: boolean;
}

function EngagementCard({ thread, hasPendingQuestion }: EngagementCardProps) {
  const freshness = activityFreshness(thread.lastActivity);
  const a2aOnly = isA2aOnly(thread.participants ?? []);

  return (
    <Link
      href={`/engagement/${thread.id}`}
      className={cn(
        "block rounded-lg border border-border bg-card text-card-foreground shadow-sm",
        "transition-all hover:border-primary/40 hover:bg-accent focus-visible:outline-none",
        "focus-visible:ring-2 focus-visible:ring-ring",
        FRESHNESS_OPACITY[freshness],
      )}
      data-testid={`engagement-card-${thread.id}`}
      aria-label={`Engagement ${thread.id} — ${thread.summary ?? "no summary"}`}
    >
      <div className="flex flex-col gap-2 p-4">
        {/* Header row */}
        <div className="flex items-start justify-between gap-2">
          <div className="flex items-center gap-2 min-w-0">
            {hasPendingQuestion ? (
              <MessageCircleQuestion
                className="h-4 w-4 shrink-0 text-warning"
                aria-label="Awaiting your answer"
              />
            ) : a2aOnly ? (
              <Eye
                className="h-4 w-4 shrink-0 text-muted-foreground"
                aria-label="Agent-to-agent engagement (observe only)"
              />
            ) : (
              <MessagesSquare
                className="h-4 w-4 shrink-0 text-voyage"
                aria-hidden="true"
              />
            )}
            <span
              className="font-mono text-xs text-muted-foreground truncate"
              data-testid="engagement-card-id"
            >
              {thread.id}
            </span>
          </div>

          <div className="flex items-center gap-2 shrink-0">
            {hasPendingQuestion && (
              <Badge
                variant="warning"
                className="text-[10px] px-1.5 h-5"
              >
                Question
              </Badge>
            )}
            {a2aOnly && (
              <Badge
                variant="secondary"
                className="text-[10px] px-1.5 h-5"
              >
                A2A
              </Badge>
            )}
            <Badge
              variant={freshness === "active" ? "success" : "outline"}
              className="text-[10px] px-1.5 h-5 tabular-nums"
            >
              {formatRelativeTime(thread.lastActivity)}
            </Badge>
          </div>
        </div>

        {/* Summary */}
        {thread.summary && (
          <p className="text-sm text-foreground line-clamp-2">
            {thread.summary}
          </p>
        )}

        {/* Participants */}
        <p
          className="font-mono text-[11px] text-muted-foreground truncate"
          aria-label={`Participants: ${formatParticipants(thread.participants ?? [])}`}
        >
          {formatParticipants(thread.participants ?? [])}
        </p>

        {/* Footer */}
        <div className="flex items-center justify-between text-[11px] text-muted-foreground">
          <span>{thread.eventCount ?? 0} events</span>
          <span className="font-mono">{thread.status}</span>
        </div>
      </div>
    </Link>
  );
}

// ---------------------------------------------------------------------------
// Loading skeleton
// ---------------------------------------------------------------------------

function EngagementListSkeleton() {
  return (
    <div
      className="space-y-3"
      role="status"
      aria-live="polite"
      data-testid="engagement-list-loading"
    >
      {[1, 2, 3].map((i) => (
        <Skeleton key={i} className="h-28 w-full rounded-lg" />
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty state
// ---------------------------------------------------------------------------

interface EmptyStateProps {
  slice: "mine" | "unit" | "agent";
  unit?: string;
  agent?: string;
}

function EngagementListEmpty({ slice, unit, agent }: EmptyStateProps) {
  const message =
    slice === "unit"
      ? `No engagements found for unit "${unit}".`
      : slice === "agent"
        ? `No engagements found for agent "${agent}".`
        : "No engagements yet. Start a unit and assign it a task to begin an engagement.";

  return (
    <Card data-testid="engagement-list-empty">
      <CardContent className="flex flex-col items-center justify-center p-8 text-center">
        <MessagesSquare
          className="mb-3 h-10 w-10 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="mb-1 font-medium">No engagements</p>
        <p className="text-sm text-muted-foreground">{message}</p>
        {slice === "mine" && (
          <Link
            href="/engagement/new"
            data-testid="engagement-list-empty-new-cta"
            className="mt-4 inline-flex h-8 items-center justify-center gap-1 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            Start a new engagement
          </Link>
        )}
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

export function EngagementList({ slice, unit, agent }: EngagementListProps) {
  // Build the filter for the API call.
  // For "mine": filter by participant (the authenticated human's address).
  // For "unit" / "agent": filter by unit or agent id.
  const filters = (() => {
    if (slice === "unit" && unit) return { unit };
    if (slice === "agent" && agent) return { agent };
    // "mine" — the server will return threads visible to the current
    // authenticated caller. We use the human:// filter client-side to
    // exclude A2A-only threads from the display.
    return {};
  })();

  const threadsQuery = useThreads(filters, { staleTime: 10_000 });
  // Inbox drives the "pending question" badges. It returns items that are
  // "awaiting the current human" — which is exactly the Q&A use case.
  const inboxQuery = useInbox({ staleTime: 10_000 });

  // Build a Set of thread ids that have pending inbox items so we can
  // badge them on the list cards without a per-thread fetch.
  // InboxItem.threadId is a required field in the OpenAPI schema.
  const pendingThreadIds = new Set<string>(
    (inboxQuery.data ?? []).map((item) => item.threadId).filter(Boolean),
  );

  if (threadsQuery.isPending) {
    return <EngagementListSkeleton />;
  }

  if (threadsQuery.error) {
    return (
      <div
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive flex items-start gap-2"
        data-testid="engagement-list-error"
      >
        <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        <span>
          Could not load engagements:{" "}
          {threadsQuery.error instanceof Error
            ? threadsQuery.error.message
            : String(threadsQuery.error)}
        </span>
      </div>
    );
  }

  let threads = threadsQuery.data ?? [];

  // For the "mine" slice, exclude A2A-only engagements (threads with no
  // human:// participant). Per-unit and per-agent slices show all threads.
  if (slice === "mine") {
    threads = threads.filter((t) => !isA2aOnly(t.participants ?? []));
  }

  // Sort recency-driven: latest activity first.
  threads = [...threads].sort(
    (a, b) =>
      new Date(b.lastActivity).getTime() - new Date(a.lastActivity).getTime(),
  );

  if (threads.length === 0) {
    return <EngagementListEmpty slice={slice} unit={unit} agent={agent} />;
  }

  return (
    <div
      className="space-y-3"
      data-testid="engagement-list"
      aria-label="Engagements"
    >
      {threadsQuery.isFetching && !threadsQuery.isPending && (
        <div
          className="flex items-center gap-1.5 text-xs text-muted-foreground"
          role="status"
          aria-live="polite"
        >
          <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
          Refreshing…
        </div>
      )}
      {threads.map((thread) => (
        <EngagementCard
          key={thread.id}
          thread={thread}
          hasPendingQuestion={pendingThreadIds.has(thread.id)}
        />
      ))}
    </div>
  );
}
