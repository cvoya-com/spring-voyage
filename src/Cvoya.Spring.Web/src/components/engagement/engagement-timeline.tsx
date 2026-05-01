"use client";

// Engagement Timeline.
//
// Renders the per-thread Timeline as a chat-like dialog, streaming live
// updates via the SSE activity stream filtered to the thread. A filter
// dropdown in the top-right toggles between "Messages" (the natural-language
// dialog only) and "Full timeline" (every event the thread has emitted).
//
// Default is "Messages" so the user lands on the conversation by default;
// switch to "Full timeline" to see lifecycle and tool events.

import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, Loader2, Wifi, WifiOff } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useThread } from "@/lib/api/queries";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import { ThreadEventRow } from "@/components/thread/thread-event-row";
import type { ThreadEvent } from "@/lib/api/types";

interface EngagementTimelineProps {
  threadId: string;
}

type TimelineFilter = "messages" | "full";

const TIMELINE_FILTER_LABELS: Record<TimelineFilter, string> = {
  messages: "Messages",
  full: "Full timeline",
};

function isMessageEvent(event: ThreadEvent): boolean {
  return event.eventType === "MessageReceived";
}

interface TimelineFilterDropdownProps {
  value: TimelineFilter;
  onChange: (v: TimelineFilter) => void;
}

function TimelineFilterDropdown({
  value,
  onChange,
}: TimelineFilterDropdownProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [open]);

  return (
    <div ref={ref} className="relative" data-testid="timeline-filter-dropdown">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className={cn(
          "flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          open && "bg-accent text-foreground",
        )}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label="Filter timeline events"
        data-testid="timeline-filter-trigger"
      >
        <span data-testid="timeline-filter-label">
          {TIMELINE_FILTER_LABELS[value]}
        </span>
        <ChevronDown className="h-3 w-3 shrink-0" aria-hidden="true" />
      </button>
      {open && (
        <div
          role="listbox"
          aria-label="Timeline filter options"
          className="absolute right-0 top-full z-10 mt-1 min-w-[10rem] rounded-md border border-border bg-popover shadow-md"
          data-testid="timeline-filter-menu"
        >
          {(["messages", "full"] as TimelineFilter[]).map((opt) => (
            <button
              key={opt}
              type="button"
              role="option"
              aria-selected={value === opt}
              onClick={() => {
                onChange(opt);
                setOpen(false);
              }}
              className={cn(
                "flex w-full items-center px-3 py-1.5 text-left text-xs transition-colors hover:bg-accent",
                value === opt
                  ? "font-medium text-foreground"
                  : "text-muted-foreground",
              )}
              data-testid={`timeline-filter-option-${opt}`}
            >
              {TIMELINE_FILTER_LABELS[opt]}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * The Timeline view for an engagement. Live-streams updates and renders
 * each event as a chat-style bubble.
 */
export function EngagementTimeline({ threadId }: EngagementTimelineProps) {
  const threadQuery = useThread(threadId, { staleTime: 0 });
  const { connected } = useThreadStream(threadId);
  const bottomRef = useRef<HTMLDivElement>(null);
  const [filter, setFilter] = useState<TimelineFilter>("messages");

  const allEvents = useMemo(
    () => threadQuery.data?.events ?? [],
    [threadQuery.data?.events],
  );
  const events = useMemo(
    () =>
      filter === "messages" ? allEvents.filter(isMessageEvent) : allEvents,
    [allEvents, filter],
  );

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
      className="flex min-h-0 flex-1 flex-col"
      data-testid="engagement-timeline"
    >
      {/* Status row + filter dropdown (top-right) */}
      <div className="flex items-center justify-between gap-2 border-b border-border px-4 py-1.5 text-[11px] text-muted-foreground">
        <div className="flex items-center gap-1.5">
          {connected ? (
            <>
              <Wifi className="h-3 w-3 text-success" aria-hidden="true" />
              <span>Live</span>
            </>
          ) : (
            <>
              <WifiOff
                className="h-3 w-3 text-muted-foreground"
                aria-hidden="true"
              />
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
        <TimelineFilterDropdown value={filter} onChange={setFilter} />
      </div>

      {/* Event list — scrollable */}
      <div
        className="flex-1 space-y-3 overflow-y-auto p-4"
        data-testid="engagement-timeline-events"
        aria-label="Engagement timeline"
        aria-live="polite"
        aria-atomic="false"
      >
        {events.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            {allEvents.length === 0
              ? "No events in this engagement yet."
              : "No messages yet — switch to “Full timeline” to see all events."}
          </p>
        ) : (
          events.map((event) => <ThreadEventRow key={event.id} event={event} />)
        )}
        <div ref={bottomRef} aria-hidden="true" />
      </div>
    </div>
  );
}
