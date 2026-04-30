"use client";

// /inbox — two-pane list-detail redesign (#1474).
//
// Left pane: compact thread rows from GET /api/v1/inbox, sorted by
// last activity descending (unread-first sort deferred until the server
// exposes unreadCount — filed as follow-up). Each row shows the other
// participants' names (user is implicit), a "pending since" timestamp,
// and the thread summary. No unread badge — see #1484.
//
// Right pane: the selected thread's timeline rendered via the same
// ThreadEventRow primitive used by the engagement portal, with a small
// (i) metadata toggle per event. Auto-selects the first thread on entry
// when no ?thread= param is present.
//
// Blocker note: agent replies do not surface here yet because #1476
// (HumanActor permission default) is not fixed. Once that lands, inbox
// items will include agent-reply events and the timeline will populate.

import { Suspense, useEffect, useMemo, useRef, useState } from "react";
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
import { useInbox, useThread } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import { parseThreadSource, roleFromEvent, ROLE_STYLES } from "@/components/thread/role";
import type { InboxItem, ThreadEvent } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Thread-row helpers
// ---------------------------------------------------------------------------

/**
 * Derive the display label for a thread row: the other participants
 * excluding any human:// address (the current user is implicit).
 * Falls back to the threadId when no participants are available.
 */
function otherParticipants(item: InboxItem): string {
  // InboxItem carries `from` (the sender address) — use that as the
  // primary identity, stripping the scheme prefix for brevity.
  const from = item.from ?? "";
  // Strip scheme:// prefix for a friendlier label (e.g. "ada" not "agent://ada").
  const idx = from.indexOf("://");
  return idx > 0 ? from.slice(idx + 3) : from || item.threadId;
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
  const label = otherParticipants(item);
  const summary = item.summary?.trim();

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
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span
          className="font-medium text-sm truncate"
          data-testid={`inbox-row-label-${item.threadId}`}
        >
          {label}
        </span>
        <span className="text-[10px] font-mono text-muted-foreground shrink-0 tabular-nums">
          {timeAgo(item.pendingSince)}
        </span>
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
 */
function InboxEventRow({ event }: InboxEventRowProps) {
  const [showMeta, setShowMeta] = useState(false);
  const [expanded, setExpanded] = useState(true);

  const role = roleFromEvent(event.source, event.eventType);
  const style = ROLE_STYLES[role];
  const source = parseThreadSource(event.source);
  const timestamp = new Date(event.timestamp);

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
            source={source.raw}
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
          source={source.raw}
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
  source: string;
  timestamp: Date;
  eventTimestamp: string;
  showMeta: boolean;
  onToggleMeta: () => void;
  variant: "error" | "human" | "default";
  align: "start" | "end";
}

function MetaHeader({
  source,
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
      <span className="truncate font-mono">{source}</span>
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
        {event.source}
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
}

function ThreadTimeline({ threadId }: ThreadTimelineProps) {
  const threadQuery = useThread(threadId, { staleTime: 0 });
  const { connected } = useThreadStream(threadId);
  const bottomRef = useRef<HTMLDivElement>(null);

  const events = threadQuery.data?.events ?? [];

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

  return (
    <div className="flex flex-col min-h-0 flex-1" data-testid="inbox-thread-timeline">
      {/* Thread metadata strip */}
      <div className="border-b border-border px-4 py-2 text-xs text-muted-foreground space-y-1">
        <div className="flex items-center gap-2 font-mono truncate">
          {participants.join(" · ")}
        </div>
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
          <span>{events.length} event{events.length === 1 ? "" : "s"}</span>
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
            No events in this thread yet.{" "}
            <span className="text-xs">
              Agent replies require #1476 to be fixed first.
            </span>
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
  const stream = useActivityStream();

  // Wrap the empty-array fallback in its own useMemo so `items`'s identity
  // is stable when `inboxQuery.data` is unchanged. Otherwise the `??` would
  // mint a fresh `[]` on every render and force `sortedItems` to recompute
  // (react-hooks/exhaustive-deps).
  const items = useMemo(
    () => inboxQuery.data ?? [],
    [inboxQuery.data],
  );

  // Sort by last activity descending: pendingSince is the best proxy we have.
  const sortedItems = useMemo(
    () =>
      [...items].sort(
        (a, b) =>
          new Date(b.pendingSince).getTime() -
          new Date(a.pendingSince).getTime(),
      ),
    [items],
  );

  // Auto-select the first thread on entry when no ?thread= param is set.
  const firstThreadId = sortedItems[0]?.threadId ?? null;
  useEffect(() => {
    if (!selectedThreadId && firstThreadId) {
      router.replace(`/inbox?thread=${encodeURIComponent(firstThreadId)}`);
    }
  }, [selectedThreadId, firstThreadId, router]);

  const errorMessage =
    inboxQuery.error instanceof Error ? inboxQuery.error.message : null;

  return (
    <div className="flex flex-col h-full space-y-0" data-testid="inbox-page">
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between pb-4 border-b border-border mb-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <InboxIcon className="h-5 w-5" aria-hidden="true" /> Inbox
            {items.length > 0 && (
              <Badge variant="warning" data-testid="inbox-count-badge">
                {items.length}
              </Badge>
            )}
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
          <p className="text-sm text-muted-foreground">
            Conversations addressed to you. Mirrors{" "}
            <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
              spring inbox list
            </code>
            .
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
                  onSelect={() =>
                    router.replace(
                      `/inbox?thread=${encodeURIComponent(item.threadId)}`,
                    )
                  }
                />
              ))}
            </div>
          </div>

          {/* Right pane: thread timeline */}
          <div className="flex-1 min-w-0 flex flex-col bg-background">
            {selectedThreadId ? (
              <ThreadTimeline threadId={selectedThreadId} />
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
