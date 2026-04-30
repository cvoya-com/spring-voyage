"use client";

// Engagement Timeline (E2.5, #1417).
//
// Renders the full per-thread Timeline for an engagement, streaming
// live updates via the SSE activity stream filtered to the thread.
//
// Each event is rendered via `ThreadEventRow` (the same primitive used
// in the management portal's Messages tab). Error events (#1161 /
// thread-model Q7) render with destructive styling and `role="alert"`.
//
// The SSE filter uses `?thread=<id>` on `/api/stream/activity` —
// the server-side filter landed in PR #1421.

import { useEffect, useRef } from "react";
import { Loader2, Wifi, WifiOff } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { useThread } from "@/lib/api/queries";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import { ThreadEventRow } from "@/components/thread/thread-event-row";

interface EngagementTimelineProps {
  threadId: string;
}

/**
 * The full Timeline view for an engagement. Live-streams updates.
 * Also handles loading and error states.
 */
export function EngagementTimeline({ threadId }: EngagementTimelineProps) {
  const threadQuery = useThread(threadId, { staleTime: 0 });
  const { connected } = useThreadStream(threadId);
  const bottomRef = useRef<HTMLDivElement>(null);

  const events = threadQuery.data?.events ?? [];

  // Scroll to bottom when new events arrive (newest-last display).
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [events.length]);

  if (threadQuery.isPending) {
    return (
      <div
        className="space-y-3 p-4"
        role="status"
        aria-live="polite"
        data-testid="engagement-timeline-loading"
      >
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-3/4" />
        <Skeleton className="h-14 w-full" />
      </div>
    );
  }

  if (threadQuery.error) {
    return (
      <div
        role="alert"
        className="m-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="engagement-timeline-error"
      >
        Could not load engagement timeline:{" "}
        {threadQuery.error instanceof Error
          ? threadQuery.error.message
          : String(threadQuery.error)}
      </div>
    );
  }

  if (!threadQuery.data) {
    return (
      <p
        className="m-4 text-sm text-muted-foreground"
        data-testid="engagement-timeline-not-found"
      >
        Engagement not found. It may not exist yet.
      </p>
    );
  }

  return (
    <div
      className="flex flex-col min-h-0 flex-1"
      data-testid="engagement-timeline"
    >
      {/* Stream status indicator */}
      <div className="flex items-center gap-1.5 border-b border-border px-4 py-1.5 text-[11px] text-muted-foreground">
        {connected ? (
          <>
            <Wifi className="h-3 w-3 text-success" aria-hidden="true" />
            <span>Live</span>
          </>
        ) : (
          <>
            <WifiOff className="h-3 w-3 text-muted-foreground" aria-hidden="true" />
            <span>Connecting…</span>
          </>
        )}
        {threadQuery.isFetching && !threadQuery.isPending && (
          <>
            <span aria-hidden="true">·</span>
            <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
          </>
        )}
        <span aria-hidden="true">·</span>
        <span>{events.length} events</span>
      </div>

      {/* Event list — scrollable */}
      <div
        className="flex-1 overflow-y-auto p-4 space-y-3"
        data-testid="engagement-timeline-events"
        aria-label="Engagement timeline"
        aria-live="polite"
        aria-atomic="false"
      >
        {events.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No events in this engagement yet.
          </p>
        ) : (
          events.map((event) => (
            <ThreadEventRow key={event.id} event={event} />
          ))
        )}
        {/* Anchor for auto-scroll to bottom */}
        <div ref={bottomRef} aria-hidden="true" />
      </div>
    </div>
  );
}
