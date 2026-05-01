"use client";

// /inbox — two-pane list-detail redesign (#1474).
//
// Left pane: compact thread rows from GET /api/v1/inbox, sorted by
// last activity descending (unread-first sort deferred until the server
// exposes unreadCount — filed as follow-up). Each row shows the display
// names of the other participants (derived from address paths, excluding
// human:// scheme addresses which are the current user), a "pending since"
// timestamp, and the thread summary. No unread badge — see #1484.
//
// Right pane: the selected thread's timeline rendered via <InboxEventRow>,
// with a small (i) metadata toggle per event. The timeline header shows
// the full list of other participants with per-participant (i) popover
// cards (address + "Open 1:1" button). A "Full timeline / Messages"
// dropdown at the top-right of the right pane filters what events render;
// default is "Messages" (only MessageReceived events).
//
// Blocker note: agent replies do not surface here yet because #1476
// (HumanActor permission default) is not fixed. Once that lands, inbox
// items will include agent-reply events and the timeline will populate.

import {
  Suspense,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Inbox as InboxIcon,
  Info,
  Loader2,
  RefreshCw,
  Wifi,
  WifiOff,
  Wrench,
} from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn, timeAgo } from "@/lib/utils";
import { useCurrentUser, useInbox, useMarkInboxRead, useThread } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import { parseThreadSource, roleFromEvent, ROLE_STYLES } from "@/components/thread/role";
import type { InboxItem, ParticipantRef, ThreadEvent } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Address / display-name helpers
// ---------------------------------------------------------------------------

/**
 * Returns true when the address belongs to the human scheme.
 * Human addresses use the "human://" scheme.
 */
function isHumanAddress(address: string): boolean {
  return address.startsWith("human://");
}

/**
 * Returns true when the ParticipantRef belongs to a human participant.
 */
function isHumanParticipant(p: ParticipantRef): boolean {
  return isHumanAddress(p.address);
}

/**
 * Derive the display label for a thread row from the `from` field of
 * the inbox item. Uses the resolved displayName from the API wire shape,
 * falling back to the threadId when absent.
 */
export function otherParticipantsFromInboxItem(item: InboxItem): string {
  return item.from?.displayName || item.threadId;
}

/**
 * Derive display names for other participants from a ParticipantRef array,
 * excluding any human:// addresses. The caller's address is used to filter
 * "self" when provided; otherwise all human participants are excluded.
 * Returns up to `max` names with a trailing "..." when the list is truncated.
 */
export function otherParticipantNames(
  participants: ParticipantRef[],
  selfAddress?: string | null,
  max = 3,
): string {
  const others = participants.filter((p) => {
    if (selfAddress) {
      return p.address !== selfAddress;
    }
    return !isHumanParticipant(p);
  });
  if (others.length === 0) return "";
  const names = others.map((p) => p.displayName);
  if (names.length <= max) return names.join(", ");
  return names.slice(0, max).join(", ") + ", ...";
}

// ---------------------------------------------------------------------------
// Timeline filter type
// ---------------------------------------------------------------------------

type TimelineFilter = "messages" | "full";

const TIMELINE_FILTER_LABELS: Record<TimelineFilter, string> = {
  messages: "Messages",
  full: "Full timeline",
};

/**
 * Returns true when the event should be visible under the "messages" filter.
 * This includes MessageReceived events and any future interaction-event types
 * (report cards, question cards, etc.). Everything else (lifecycle, tool, etc.)
 * is hidden.
 */
function isMessageEvent(event: ThreadEvent): boolean {
  return event.eventType === "MessageReceived";
}

// ---------------------------------------------------------------------------
// Timeline filter dropdown
// ---------------------------------------------------------------------------

interface TimelineFilterDropdownProps {
  value: TimelineFilter;
  onChange: (v: TimelineFilter) => void;
}

function TimelineFilterDropdown({ value, onChange }: TimelineFilterDropdownProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close on outside click
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
        <span data-testid="timeline-filter-label">{TIMELINE_FILTER_LABELS[value]}</span>
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
                "flex w-full items-center px-3 py-1.5 text-xs text-left transition-colors hover:bg-accent",
                value === opt ? "font-medium text-foreground" : "text-muted-foreground",
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

// ---------------------------------------------------------------------------
// Participant popover card
// ---------------------------------------------------------------------------

interface ParticipantPopoverProps {
  participant: ParticipantRef;
  currentThreadId: string;
}

/**
 * Per-participant (i) button that toggles a popover card showing the
 * participant's full address and a "Open 1:1" button that navigates to
 * the 1:1 thread with that participant (when not already in a 1:1 with them).
 */
function ParticipantPopover({ participant, currentThreadId }: ParticipantPopoverProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const { address, displayName } = participant;

  // Close on outside click
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

  // The "Open 1:1" button navigates to the inbox filtered to threads
  // involving this participant. The button is hidden when the current
  // thread already is a 1:1 with this participant (single non-human participant).
  // For now we always show it since we don't track that state without a
  // thread detail fetch — the page already has the thread data loaded.
  const open1on1Href = `/inbox?participant=${encodeURIComponent(participant.address)}`;

  return (
    <div ref={ref} className="relative inline-flex items-center">
      <span className="text-xs font-medium" data-testid={`participant-name-${address}`}>
        {displayName}
      </span>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={`Show info for ${displayName}`}
        aria-pressed={open}
        className={cn(
          "ml-0.5 rounded p-0.5 transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          open ? "text-primary" : "text-muted-foreground/50 hover:text-muted-foreground",
        )}
        data-testid={`participant-info-btn-${address}`}
      >
        <Info className="h-3 w-3" aria-hidden="true" />
      </button>
      {open && (
        <div
          className="absolute left-0 top-full z-10 mt-1 w-64 rounded-md border border-border bg-popover p-3 shadow-md"
          data-testid={`participant-popover-${address}`}
        >
          <p className="text-xs font-medium text-foreground mb-1">{displayName}</p>
          <p className="text-[10px] font-mono text-muted-foreground break-all mb-2">
            {address}
          </p>
          {currentThreadId !== address && (
            <Link
              href={open1on1Href}
              className="inline-flex items-center rounded-md border border-border bg-accent px-2 py-1 text-xs hover:bg-accent/80 transition-colors"
              onClick={() => setOpen(false)}
              data-testid={`participant-open-1on1-${address}`}
            >
              Open 1:1 with {displayName}
            </Link>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Thread row (left pane)
// ---------------------------------------------------------------------------

interface ThreadRowProps {
  item: InboxItem;
  selected: boolean;
  onSelect: () => void;
}

function ThreadRow({ item, selected, onSelect }: ThreadRowProps) {
  const label = otherParticipantsFromInboxItem(item);
  const summary = item.summary?.trim();
  const unread = (item.unreadCount ?? 0) as number;

  return (
    <button
      type="button"
      onClick={onSelect}
      data-testid={`inbox-thread-row-${item.threadId}`}
      aria-current={selected ? "true" : undefined}
      className={cn(
        "w-full text-left px-3 py-2.5 rounded-md border transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "border-primary/40 bg-primary/10 text-foreground"
          : "border-transparent hover:border-border hover:bg-accent text-foreground",
        unread > 0 && !selected && "font-medium",
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span
          className="font-medium text-sm truncate"
          data-testid={`inbox-row-label-${item.threadId}`}
        >
          {label}
        </span>
        <div className="flex items-center gap-1 shrink-0">
          {unread > 0 && (
            <Badge
              variant="warning"
              className="h-5 px-1.5 text-[10px] tabular-nums"
              data-testid={`inbox-unread-badge-${item.threadId}`}
            >
              ({unread})
            </Badge>
          )}
          <span className="text-[10px] font-mono text-muted-foreground tabular-nums">
            {timeAgo(item.pendingSince)}
          </span>
        </div>
      </div>
      {summary && (
        <p className="mt-0.5 text-xs text-muted-foreground truncate">{summary}</p>
      )}
    </button>
  );
}

// ---------------------------------------------------------------------------
// Event row with (i) metadata toggle (#1474 spec)
// ---------------------------------------------------------------------------

interface InboxEventRowProps {
  event: ThreadEvent;
}

/**
 * Inbox-specific event row: renders the message text inline by default,
 * with a small (i) icon to toggle the platform metadata (event id,
 * source URI, type, payload). Mirrors the engagement portal's
 * ThreadEventRow but replaces the "View in activity" link with the
 * inline metadata toggle.
 *
 * Message bubbles show the display name (path of the address) rather than
 * the full address. The full address is always available via the (i) popover.
 */
function InboxEventRow({ event }: InboxEventRowProps) {
  const [showMeta, setShowMeta] = useState(false);
  const [expanded, setExpanded] = useState(true);

  const role = roleFromEvent(event.source.address, event.eventType);
  const style = ROLE_STYLES[role];
  const source = parseThreadSource(event.source.address);
  const timestamp = new Date(event.timestamp);

  // Display name: use the resolved displayName from the API wire shape.
  const sourceDisplayName = event.source.displayName || source.path || source.raw;

  const isToolOrLifecycle =
    role === "tool" ||
    event.eventType === "StateChanged" ||
    event.eventType === "WorkflowStepCompleted" ||
    event.eventType === "ReflectionCompleted";

  const bodyText =
    event.eventType === "MessageReceived" && event.body ? event.body : null;

  const isError =
    event.eventType === "ErrorOccurred" || event.severity === "Error";

  if (isError) {
    return (
      <div
        className="flex w-full justify-start"
        data-testid={`inbox-event-${event.id}`}
        data-role="error"
      >
        <div className="flex max-w-[80%] min-w-0 flex-col gap-1">
          <MetaHeader
            sourceDisplayName={sourceDisplayName}
            sourceAddress={source.raw}
            timestamp={timestamp}
            eventTimestamp={event.timestamp}
            showMeta={showMeta}
            onToggleMeta={() => setShowMeta((v) => !v)}
            variant="error"
            align="start"
          />
          <div
            role="alert"
            className="flex items-start gap-2 rounded-lg border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-foreground shadow-sm"
          >
            <AlertTriangle
              className="mt-0.5 h-4 w-4 shrink-0 text-destructive"
              aria-hidden="true"
            />
            <p className="whitespace-pre-wrap break-words">{event.summary}</p>
          </div>
          {showMeta && <EventMeta event={event} />}
        </div>
      </div>
    );
  }

  return (
    <div
      className={cn(
        "flex w-full",
        style.align === "end" ? "justify-end" : "justify-start",
      )}
      data-testid={`inbox-event-${event.id}`}
      data-role={role}
    >
      <div className="flex max-w-[80%] min-w-0 flex-col gap-1">
        <MetaHeader
          sourceDisplayName={sourceDisplayName}
          sourceAddress={source.raw}
          timestamp={timestamp}
          eventTimestamp={event.timestamp}
          showMeta={showMeta}
          onToggleMeta={() => setShowMeta((v) => !v)}
          variant={style.align === "end" ? "human" : "default"}
          align={style.align === "end" ? "end" : "start"}
        />

        <div className={cn("rounded-lg px-3 py-2 text-sm shadow-sm", style.bubble)}>
          {isToolOrLifecycle ? (
            <button
              type="button"
              onClick={() => setExpanded((v) => !v)}
              className="flex w-full items-center gap-2 text-left"
              aria-expanded={expanded}
            >
              {expanded ? (
                <ChevronDown className="h-3.5 w-3.5 shrink-0" />
              ) : (
                <ChevronRight className="h-3.5 w-3.5 shrink-0" />
              )}
              {role === "tool" && (
                <Wrench className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              )}
              <span className="min-w-0 flex-1 truncate">
                {role === "tool" ? "Tool call" : event.eventType}
                {event.summary ? ` — ${event.summary}` : ""}
              </span>
            </button>
          ) : (
            <p className="whitespace-pre-wrap break-words">
              {bodyText ?? event.summary}
            </p>
          )}
          {expanded && isToolOrLifecycle && (
            <div className="mt-2 space-y-1 rounded border border-black/5 bg-background/40 p-2 text-xs">
              <p className="whitespace-pre-wrap break-words">{event.summary}</p>
              <p className="text-muted-foreground">
                {event.eventType} · {event.severity}
              </p>
            </div>
          )}
        </div>

        {showMeta && <EventMeta event={event} />}
      </div>
    </div>
  );
}

interface MetaHeaderProps {
  /** Short display name (address path, e.g. "ada"). */
  sourceDisplayName: string;
  /** Full address string (e.g. "agent://ada"). Used in the meta panel. */
  sourceAddress: string;
  timestamp: Date;
  eventTimestamp: string;
  showMeta: boolean;
  onToggleMeta: () => void;
  variant: "error" | "human" | "default";
  align: "start" | "end";
}

function MetaHeader({
  sourceDisplayName,
  timestamp,
  eventTimestamp,
  showMeta,
  onToggleMeta,
  variant,
  align,
}: MetaHeaderProps) {
  return (
    <div
      className={cn(
        "flex items-center gap-2 text-xs text-muted-foreground",
        align === "end" ? "justify-end" : "justify-start",
      )}
    >
      {variant === "error" && (
        <Badge variant="destructive" className="h-5 px-1.5 text-[10px]">
          Error
        </Badge>
      )}
      <span className="truncate font-medium text-foreground/80" data-testid="inbox-event-source-name">
        {sourceDisplayName}
      </span>
      <span aria-hidden="true">·</span>
      <time dateTime={eventTimestamp} title={timestamp.toLocaleString()}>
        {timestamp.toLocaleTimeString([], {
          hour: "2-digit",
          minute: "2-digit",
        })}
      </time>
      <button
        type="button"
        onClick={onToggleMeta}
        aria-label={showMeta ? "Hide metadata" : "Show metadata"}
        aria-pressed={showMeta}
        data-testid="inbox-event-meta-toggle"
        className={cn(
          "rounded p-0.5 transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          showMeta ? "text-primary" : "text-muted-foreground/50 hover:text-muted-foreground",
        )}
      >
        <Info className="h-3 w-3" aria-hidden="true" />
      </button>
    </div>
  );
}

interface EventMetaProps {
  event: ThreadEvent;
}

function EventMeta({ event }: EventMetaProps) {
  return (
    <div
      className="rounded border border-border bg-muted/40 p-2 text-[10px] font-mono text-muted-foreground space-y-0.5"
      data-testid={`inbox-event-meta-${event.id}`}
    >
      <p>
        <span className="text-foreground">id</span>{" "}
        {event.id}
      </p>
      <p>
        <span className="text-foreground">type</span>{" "}
        {event.eventType}
      </p>
      <p>
        <span className="text-foreground">source</span>{" "}
        {event.source.address}
      </p>
      <p>
        <span className="text-foreground">severity</span>{" "}
        {event.severity}
      </p>
      {event.summary && (
        <p>
          <span className="text-foreground">summary</span>{" "}
          {event.summary}
        </p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Thread timeline (right pane)
// ---------------------------------------------------------------------------

interface ThreadTimelineProps {
  threadId: string;
  /** The current user's human:// address for self-filtering in participant lists. */
  selfAddress?: string | null;
}

function ThreadTimeline({ threadId, selfAddress }: ThreadTimelineProps) {
  const threadQuery = useThread(threadId, { staleTime: 0 });
  const { connected } = useThreadStream(threadId);
  const bottomRef = useRef<HTMLDivElement>(null);
  const [filter, setFilter] = useState<TimelineFilter>("messages");

  const allEvents = useMemo(
    () => threadQuery.data?.events ?? [],
    [threadQuery.data],
  );

  const events = useMemo(
    () =>
      filter === "messages"
        ? allEvents.filter(isMessageEvent)
        : allEvents,
    [allEvents, filter],
  );

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [events.length]);

  if (threadQuery.isPending) {
    return (
      <div className="space-y-3 p-4" role="status" aria-live="polite" data-testid="inbox-thread-loading">
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
        data-testid="inbox-thread-error"
      >
        Could not load thread:{" "}
        {threadQuery.error instanceof Error
          ? threadQuery.error.message
          : String(threadQuery.error)}
      </div>
    );
  }

  if (!threadQuery.data) {
    return (
      <p className="m-4 text-sm text-muted-foreground" data-testid="inbox-thread-not-found">
        Thread not found.
      </p>
    );
  }

  const participants = threadQuery.data.summary?.participants ?? [];
  // Filter "self" using the resolved address when available; fall back to
  // excluding all human:// addresses when the profile hasn't loaded yet.
  const otherParticipants = participants.filter((p) =>
    selfAddress ? p.address !== selfAddress : !isHumanParticipant(p),
  );

  return (
    <div className="flex flex-col min-h-0 flex-1" data-testid="inbox-thread-timeline">
      {/* Thread metadata strip */}
      <div className="border-b border-border px-4 py-2 text-xs text-muted-foreground space-y-1">
        {/* Participants row: names with (i) popover, and the filter dropdown */}
        <div className="flex items-center justify-between gap-2">
          <div
            className="flex items-center gap-3 flex-wrap"
            data-testid="inbox-thread-participants"
          >
            {otherParticipants.length > 0 ? (
              otherParticipants.map((p) => (
                <ParticipantPopover
                  key={p.address}
                  participant={p}
                  currentThreadId={threadId}
                />
              ))
            ) : (
              <span className="font-mono truncate">{participants.map((p) => p.displayName).join(" · ")}</span>
            )}
          </div>
          <TimelineFilterDropdown value={filter} onChange={setFilter} />
        </div>
        {/* Live status row */}
        <div className="flex items-center gap-1.5">
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
          <span>{allEvents.length} event{allEvents.length === 1 ? "" : "s"}</span>
          <span aria-hidden="true">·</span>
          <Link
            href={`/inbox?thread=${encodeURIComponent(threadId)}`}
            className="font-mono text-[10px] hover:underline"
            data-testid={`inbox-open-${threadId}`}
          >
            {threadId}
          </Link>
        </div>
      </div>

      {/* Event list */}
      <div
        className="flex-1 overflow-y-auto p-4 space-y-3"
        data-testid="inbox-thread-events"
        aria-label="Thread timeline"
        aria-live="polite"
        aria-atomic="false"
      >
        {events.length === 0 ? (
          <p className="text-sm text-muted-foreground" data-testid="inbox-thread-empty">
            {filter === "messages"
              ? "No messages in this thread yet."
              : "No events in this thread yet."}
            {filter === "full" && (
              <span className="text-xs">
                {" "}Agent replies require #1476 to be fixed first.
              </span>
            )}
          </p>
        ) : (
          events.map((event) => (
            <InboxEventRow key={event.id} event={event} />
          ))
        )}
        <div ref={bottomRef} aria-hidden="true" />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty right-pane placeholder
// ---------------------------------------------------------------------------

function NoThreadSelected() {
  return (
    <div
      className="flex flex-col items-center justify-center flex-1 p-8 text-center"
      data-testid="inbox-no-thread"
    >
      <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full border border-border bg-muted/40">
        <InboxIcon className="h-6 w-6 text-muted-foreground" aria-hidden="true" />
      </div>
      <p className="mt-3 text-sm font-medium">Select a conversation</p>
      <p className="mt-1 text-xs text-muted-foreground">
        Choose a thread from the list to view its timeline.
      </p>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main page
// ---------------------------------------------------------------------------

function InboxPageContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const selectedThreadId = searchParams.get("thread") ?? null;

  const inboxQuery = useInbox();
  const markRead = useMarkInboxRead();
  const stream = useActivityStream();

  // Fetch the current user profile so we can identify "self" by address
  // in participant lists (#1485). The profile carries a human:// address
  // field that's compared against each participant's address rather than
  // relying on display-name equality or scheme-based exclusion.
  const profileQuery = useCurrentUser();
  const selfAddress = profileQuery.data?.address ?? null;

  // Wrap the empty-array fallback in its own useMemo so `items`'s identity
  // is stable when `inboxQuery.data` is unchanged. Otherwise the `??` would
  // mint a fresh `[]` on every render and force `sortedItems` to recompute
  // (react-hooks/exhaustive-deps).
  const items = useMemo(
    () => inboxQuery.data ?? [],
    [inboxQuery.data],
  );

  // Sort: unread-first (any unreadCount > 0 ranks ahead of any unreadCount === 0),
  // then by pendingSince descending within each bucket (#1477).
  // We do NOT re-sort on mark-read; the optimistic update zeroes the badge
  // in-place but the row stays put until the next inbox refetch to avoid
  // the selected row jumping away while the user is reading.
  const sortedItems = useMemo(
    () =>
      [...items].sort((a, b) => {
        const aUnread = (a.unreadCount ?? 0) as number;
        const bUnread = (b.unreadCount ?? 0) as number;
        const aHasUnread = aUnread > 0 ? 1 : 0;
        const bHasUnread = bUnread > 0 ? 1 : 0;
        if (bHasUnread !== aHasUnread) {
          // Unread rows rank first.
          return bHasUnread - aHasUnread;
        }
        // Within each bucket sort by most recent first.
        return (
          new Date(b.pendingSince).getTime() -
          new Date(a.pendingSince).getTime()
        );
      }),
    [items],
  );

  // Auto-select the first thread on entry when no ?thread= param is set.
  const firstThreadId = sortedItems[0]?.threadId ?? null;
  useEffect(() => {
    if (!selectedThreadId && firstThreadId) {
      router.replace(`/inbox?thread=${encodeURIComponent(firstThreadId)}`);
    }
  }, [selectedThreadId, firstThreadId, router]);

  // Fire mark-read when a thread is selected. Uses the mutation's optimistic
  // update so the badge clears immediately. This is a best-effort call —
  // failure is silent because the badge resets on the next inbox refetch.
  const handleSelectThread = (threadId: string) => {
    router.replace(`/inbox?thread=${encodeURIComponent(threadId)}`);
    markRead.mutate(threadId);
  };

  const errorMessage =
    inboxQuery.error instanceof Error ? inboxQuery.error.message : null;

  return (
    <div className="flex flex-col h-full space-y-0" data-testid="inbox-page">
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between pb-4 border-b border-border mb-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <InboxIcon className="h-5 w-5" aria-hidden="true" /> Inbox
            {stream.connected && (
              <Badge
                variant="outline"
                className="font-mono text-[10px]"
                data-testid="inbox-live-pill"
              >
                live
              </Badge>
            )}
          </h1>
          <p className="text-sm text-muted-foreground" data-testid="inbox-subtitle">
            Engagements with you as a participant
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => inboxQuery.refetch()}
          disabled={inboxQuery.isFetching}
          data-testid="inbox-refresh"
          className="self-start sm:self-auto"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${inboxQuery.isFetching ? "animate-spin" : ""}`}
            aria-hidden="true"
          />
          Refresh
        </Button>
      </div>

      {/* Error banner */}
      {errorMessage && (
        <Card
          className="border-destructive/50 bg-destructive/10 mb-4"
          data-testid="inbox-error"
        >
          <CardContent className="flex items-start gap-2 p-4 text-sm text-destructive">
            <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" aria-hidden="true" />
            <div>
              <p className="font-medium">Failed to load inbox.</p>
              <p className="text-xs opacity-80">{errorMessage}</p>
            </div>
          </CardContent>
        </Card>
      )}

      {/* Loading state */}
      {inboxQuery.isPending ? (
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
          data-testid="inbox-loading"
        >
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
        </div>
      ) : items.length === 0 && !errorMessage ? (
        /* Empty state */
        <Card data-testid="inbox-empty">
          <CardContent className="space-y-2 p-10 text-center">
            <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full border border-border bg-muted/40">
              <InboxIcon
                className="h-6 w-6 text-muted-foreground"
                aria-hidden="true"
              />
            </div>
            <p className="text-sm font-medium">Nothing waiting on you.</p>
            <p className="text-xs text-muted-foreground">
              Agents will surface here when they ask for your input.
            </p>
          </CardContent>
        </Card>
      ) : (
        /* Two-pane list-detail layout */
        <div
          className="flex flex-1 min-h-0 gap-0 border border-border rounded-lg overflow-hidden"
          data-testid="inbox-list"
        >
          {/* Left pane: thread list */}
          <div
            className="w-64 shrink-0 border-r border-border bg-card flex flex-col"
            aria-label="Inbox threads"
            role="navigation"
          >
            <div className="flex-1 overflow-y-auto p-2 space-y-1">
              {sortedItems.map((item) => (
                <ThreadRow
                  key={item.threadId}
                  item={item}
                  selected={item.threadId === selectedThreadId}
                  onSelect={() => handleSelectThread(item.threadId)}
                />
              ))}
            </div>
          </div>

          {/* Right pane: thread timeline */}
          <div className="flex-1 min-w-0 flex flex-col bg-background">
            {selectedThreadId ? (
              <ThreadTimeline threadId={selectedThreadId} selfAddress={selfAddress} />
            ) : (
              <NoThreadSelected />
            )}
          </div>
        </div>
      )}
    </div>
  );
}

// Next.js requires `useSearchParams()` callers to sit under a Suspense
// boundary so the route can prerender. The fallback mirrors the empty-list
// shape so the prerendered HTML doesn't shift when hydration takes over.
export default function InboxPage() {
  return (
    <Suspense
      fallback={
        <div
          className="flex flex-col h-full space-y-0"
          data-testid="inbox-page"
        >
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between pb-4 border-b border-border mb-4">
            <div className="space-y-1">
              <h1 className="text-2xl font-bold flex items-center gap-2">
                <InboxIcon className="h-5 w-5" aria-hidden="true" /> Inbox
              </h1>
            </div>
          </div>
          <div
            className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
            data-testid="inbox-loading"
          >
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
          </div>
        </div>
      }
    >
      <InboxPageContent />
    </Suspense>
  );
}
