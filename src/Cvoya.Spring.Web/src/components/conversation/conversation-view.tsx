"use client";

// Shared conversation/timeline view (#1554).
//
// One implementation drives the engagement detail surface, the unit/agent
// "Messages" tab in the explorer, and the inbox right pane: each surface
// previously carried its own filter dropdown, scroll wiring, and event
// rendering. The duplication had drifted (different action affordances,
// different scrolling behaviour, different empty-state copy), so we
// collapse them into one component and let consumers customise via slots.
//
// Behaviour the component owns:
//   - Loads the thread via `useThread(threadId, { staleTime: 0 })` and
//     opens an SSE subscription via `useThreadStream(threadId)`.
//   - Filter dropdown (Messages / Full timeline) — defaults to "messages"
//     so users land on the conversation; full timeline reveals lifecycle
//     and tool events.
//   - Scrolls the event list to the bottom whenever a new event arrives.
//   - Renders each event through `<ThreadEventRow>`. The `rowActions`
//     prop chooses the footer affordance ("activity-link" for engagement /
//     unit-agent tabs, "metadata" for inbox).
//
// Consumers that need a custom header (inbox: participants strip + (i)
// popover + thread-id link) pass `renderHeader` and own that surface
// fully. The default header shows live status, event count, and the
// filter dropdown — sufficient for engagement and unit/agent tabs.

import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { ChevronDown, Loader2, Wifi, WifiOff } from "lucide-react";

import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useThread } from "@/lib/api/queries";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import {
  ThreadEventRow,
  type ThreadEventRowActions,
} from "@/components/thread/thread-event-row";
import type { ThreadDetail, ThreadEvent } from "@/lib/api/types";

export type ConversationFilter = "messages" | "full";

const FILTER_LABELS: Record<ConversationFilter, string> = {
  messages: "Messages",
  full: "Full timeline",
};

function isMessageEvent(event: ThreadEvent): boolean {
  return event.eventType === "MessageReceived";
}

interface FilterDropdownProps {
  value: ConversationFilter;
  onChange: (v: ConversationFilter) => void;
}

/**
 * The filter dropdown is exported so consumers that supply their own
 * header (inbox) can place it inside their custom layout.
 */
export function ConversationFilterDropdown({
  value,
  onChange,
}: FilterDropdownProps) {
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
        <span data-testid="timeline-filter-label">{FILTER_LABELS[value]}</span>
        <ChevronDown className="h-3 w-3 shrink-0" aria-hidden="true" />
      </button>
      {open && (
        <div
          role="listbox"
          aria-label="Timeline filter options"
          className="absolute right-0 top-full z-10 mt-1 min-w-[10rem] rounded-md border border-border bg-popover shadow-md"
          data-testid="timeline-filter-menu"
        >
          {(["messages", "full"] as ConversationFilter[]).map((opt) => (
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
                "flex w-full items-center px-3 py-1.5 text-xs text-left transition-colors hover:bg-accent",
                value === opt
                  ? "font-medium text-foreground"
                  : "text-muted-foreground",
              )}
              data-testid={`timeline-filter-option-${opt}`}
            >
              {FILTER_LABELS[opt]}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export interface ConversationViewHeaderApi {
  filter: ConversationFilter;
  setFilter: (v: ConversationFilter) => void;
  filterDropdown: ReactNode;
  connected: boolean;
  isFetching: boolean;
  totalEvents: number;
  filteredEventCount: number;
}

export interface ConversationViewProps {
  threadId: string;
  /**
   * Footer affordance per row. Defaults to `"activity-link"` (engagement
   * + unit/agent Messages tabs); inbox uses `"metadata"` for the inline
   * (i) toggle.
   */
  rowActions?: ThreadEventRowActions;
  /** Initial filter — defaults to `"messages"`. */
  defaultFilter?: ConversationFilter;
  /**
   * Render slot for a custom header (replaces the default Live + N events +
   * filter row). Receives the filter state plus a pre-built filter dropdown
   * so consumers can place the dropdown inside their own header chrome.
   */
  renderHeader?: (api: ConversationViewHeaderApi) => ReactNode;
  /**
   * Optional override for the empty-state copy. Receives the filter so the
   * caller can tailor "no messages yet" vs "no events yet" wording.
   */
  renderEmpty?: (api: {
    filter: ConversationFilter;
    totalEvents: number;
  }) => ReactNode;
  /** Optional `data-testid` for the outer container. */
  testId?: string;
  /**
   * Extra `data-testid` for the scrollable event list. Defaults to
   * `${testId}-events` when `testId` is set.
   */
  eventListTestId?: string;
  /**
   * Test-id prefix forwarded to each row. Defaults to
   * `"conversation-event"` so existing engagement / unit / agent tests
   * keep matching.
   */
  rowTestIdPrefix?: string;
  /**
   * Extra event predicate evaluated alongside the filter — used by inbox
   * to drop events that should never be shown in either filter.
   */
  shouldHideEvent?: (event: ThreadEvent) => boolean;
  /**
   * Pre-fetched thread detail. When supplied, the view skips the query
   * and renders these events directly. Used by surfaces that already
   * load the thread elsewhere (e.g. unit/agent Messages tabs).
   */
  detail?: ThreadDetail | null;
}

/**
 * Renders the conversation timeline for a single thread. Owns scroll,
 * live updates, filter state, and row rendering. See file header for
 * the full contract.
 */
export function ConversationView({
  threadId,
  rowActions = "activity-link",
  defaultFilter = "messages",
  renderHeader,
  renderEmpty,
  testId,
  eventListTestId,
  rowTestIdPrefix = "conversation-event",
  shouldHideEvent,
  detail: detailProp,
}: ConversationViewProps) {
  // When the caller provides a pre-fetched detail (unit/agent Messages
  // tab pattern) we still subscribe to the thread stream so SSE-driven
  // refetches push fresh events via the normal cache path; we just
  // don't issue a duplicate `useThread` here.
  const enableQuery = detailProp === undefined;
  const threadQuery = useThread(threadId, {
    staleTime: 0,
    enabled: enableQuery,
  });
  const { connected } = useThreadStream(threadId);
  const bottomRef = useRef<HTMLDivElement>(null);
  const [filter, setFilter] = useState<ConversationFilter>(defaultFilter);

  const detail: ThreadDetail | null | undefined = detailProp ?? threadQuery.data;

  const allEvents = useMemo(
    () => detail?.events ?? [],
    [detail?.events],
  );

  const visibleEvents = useMemo(() => {
    let events = allEvents;
    if (shouldHideEvent) {
      events = events.filter((e) => !shouldHideEvent(e));
    }
    if (filter === "messages") {
      events = events.filter(isMessageEvent);
    }
    return events;
  }, [allEvents, filter, shouldHideEvent]);

  // Scroll to bottom when new events arrive (newest-last display).
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [visibleEvents.length]);

  // Loading + error + not-found states only apply when we are responsible
  // for the fetch. Caller-managed details are assumed to have already
  // resolved their own pending/error UX upstream.
  if (enableQuery && threadQuery.isPending) {
    return (
      <div
        className="space-y-3 p-4"
        role="status"
        aria-live="polite"
        data-testid={testId ? `${testId}-loading` : undefined}
      >
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-3/4" />
        <Skeleton className="h-14 w-full" />
      </div>
    );
  }

  if (enableQuery && threadQuery.error) {
    return (
      <div
        role="alert"
        className="m-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid={testId ? `${testId}-error` : undefined}
      >
        Could not load conversation:{" "}
        {threadQuery.error instanceof Error
          ? threadQuery.error.message
          : String(threadQuery.error)}
      </div>
    );
  }

  if (enableQuery && !detail) {
    return (
      <p
        className="m-4 text-sm text-muted-foreground"
        data-testid={testId ? `${testId}-not-found` : undefined}
      >
        Conversation not found.
      </p>
    );
  }

  const headerApi: ConversationViewHeaderApi = {
    filter,
    setFilter,
    filterDropdown: (
      <ConversationFilterDropdown value={filter} onChange={setFilter} />
    ),
    connected,
    isFetching: threadQuery.isFetching && !threadQuery.isPending,
    totalEvents: allEvents.length,
    filteredEventCount: visibleEvents.length,
  };

  const headerContent = renderHeader ? (
    renderHeader(headerApi)
  ) : (
    <DefaultHeader api={headerApi} />
  );

  const emptyContent = renderEmpty ? (
    renderEmpty({ filter, totalEvents: allEvents.length })
  ) : (
    <DefaultEmpty filter={filter} totalEvents={allEvents.length} />
  );

  return (
    <div
      className="flex min-h-0 flex-1 flex-col"
      data-testid={testId}
    >
      {headerContent}

      <div
        className="flex-1 space-y-3 overflow-y-auto p-4"
        data-testid={eventListTestId ?? (testId ? `${testId}-events` : undefined)}
        aria-label="Conversation timeline"
        aria-live="polite"
        aria-atomic="false"
      >
        {visibleEvents.length === 0 ? (
          emptyContent
        ) : (
          visibleEvents.map((event) => (
            <ThreadEventRow
              key={event.id}
              event={event}
              actions={rowActions}
              testIdPrefix={rowTestIdPrefix}
            />
          ))
        )}
        <div ref={bottomRef} aria-hidden="true" />
      </div>
    </div>
  );
}

interface DefaultHeaderProps {
  api: ConversationViewHeaderApi;
}

/** Live status + filter row used when no `renderHeader` is supplied. */
function DefaultHeader({ api }: DefaultHeaderProps) {
  return (
    <div className="flex items-center justify-between gap-2 border-b border-border px-4 py-1.5 text-[11px] text-muted-foreground">
      <div className="flex items-center gap-1.5">
        {api.connected ? (
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
        {api.isFetching && (
          <>
            <span aria-hidden="true">·</span>
            <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
          </>
        )}
        <span aria-hidden="true">·</span>
        <span>{api.totalEvents} events</span>
      </div>
      {api.filterDropdown}
    </div>
  );
}

interface DefaultEmptyProps {
  filter: ConversationFilter;
  totalEvents: number;
}

function DefaultEmpty({ filter, totalEvents }: DefaultEmptyProps) {
  return (
    <p className="text-sm text-muted-foreground">
      {totalEvents === 0
        ? "No events in this conversation yet."
        : filter === "messages"
          ? "No messages yet — switch to “Full timeline” to see all events."
          : "No events match the current filter."}
    </p>
  );
}
