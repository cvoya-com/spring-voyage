"use client";

// /inbox — two-pane list-detail view (#1474, consolidated #1554).
//
// Left pane: compact thread rows from GET /api/v1/inbox, sorted with
// unread-first then most-recent-first. Each row shows the resolved
// display name of the other participant, a "pending since" timestamp,
// the thread summary, and an unread (N) badge when unreadCount > 0.
//
// Right pane: the selected thread's timeline rendered via the shared
// <ConversationView> primitive (#1554) with the metadata-toggle row
// affordance (an inline (i) per bubble that reveals event id / type /
// source / severity / summary). A shared <MessageComposer> is pinned at
// the bottom so users can reply to an inbox thread without bouncing into
// the engagement portal — the recipient is derived from the thread's
// other participants the same way the engagement composer does it.
//
// The custom timeline header keeps the inbox-specific bits the shared
// default header does not own: a participants strip with per-name (i)
// popover (full address + "Open 1:1" link), the live SSE pill, and the
// thread-id link. The shared filter dropdown is slotted in.

import {
  Suspense,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  AlertTriangle,
  Inbox as InboxIcon,
  Info,
  Loader2,
  RefreshCw,
  Wifi,
  WifiOff,
} from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn, timeAgo } from "@/lib/utils";
import {
  useCurrentUser,
  useInbox,
  useMarkInboxRead,
  useThread,
} from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import { useThreadStream } from "@/lib/stream/use-thread-stream";
import { parseThreadSource } from "@/components/thread/role";
import {
  ConversationView,
  type ConversationViewHeaderApi,
} from "@/components/conversation/conversation-view";
import {
  MessageComposer,
  type MessageRecipient,
} from "@/components/conversation/message-composer";
import type { InboxItem, ParticipantRef } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Address / display-name helpers
// ---------------------------------------------------------------------------

function isHumanAddress(address: string): boolean {
  return address.startsWith("human://");
}

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
 * Returns up to `max` names with a trailing "..." when the list is
 * truncated.
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

/**
 * Derive a default reply recipient from the thread's participant list.
 * Mirrors the engagement composer logic: prefer the first non-human
 * participant, fall back to the first non-self participant when every
 * other party is human (rare).
 */
function deriveRecipient(
  participants: ParticipantRef[],
  selfAddress: string | null,
): MessageRecipient | null {
  const others = participants.filter((p) =>
    selfAddress ? p.address !== selfAddress : !isHumanParticipant(p),
  );
  for (const p of others) {
    if (!isHumanParticipant(p)) {
      const { scheme, path } = parseThreadSource(p.address);
      if (scheme && path) return { scheme, path };
    }
  }
  if (others.length > 0) {
    const { scheme, path } = parseThreadSource(others[0].address);
    if (scheme && path) return { scheme, path };
  }
  return null;
}

// ---------------------------------------------------------------------------
// Participant popover card
// ---------------------------------------------------------------------------

interface ParticipantPopoverProps {
  participant: ParticipantRef;
  currentThreadId: string;
}

function ParticipantPopover({
  participant,
  currentThreadId,
}: ParticipantPopoverProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const { address, displayName } = participant;

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

  const open1on1Href = `/inbox?participant=${encodeURIComponent(participant.address)}`;

  return (
    <div ref={ref} className="relative inline-flex items-center">
      <span
        className="text-xs font-medium"
        data-testid={`participant-name-${address}`}
      >
        {displayName}
      </span>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={`Show info for ${displayName}`}
        aria-pressed={open}
        className={cn(
          "ml-0.5 rounded p-0.5 transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          open
            ? "text-primary"
            : "text-muted-foreground/50 hover:text-muted-foreground",
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
          <p className="text-xs font-medium text-foreground mb-1">
            {displayName}
          </p>
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
        <p className="mt-0.5 text-xs text-muted-foreground truncate">
          {summary}
        </p>
      )}
    </button>
  );
}

// ---------------------------------------------------------------------------
// Thread timeline (right pane)
// ---------------------------------------------------------------------------

interface ThreadTimelineProps {
  threadId: string;
  /** The current user's address for self-filtering in participant lists. */
  selfAddress?: string | null;
}

function ThreadTimeline({ threadId, selfAddress }: ThreadTimelineProps) {
  const threadQuery = useThread(threadId, { staleTime: 0 });
  const { connected } = useThreadStream(threadId);

  // Memoise the participants list so its identity is stable when the
  // underlying summary doesn't change. Otherwise the `??` fallback mints
  // a fresh empty array on every render and forces `recipient` to
  // recompute (react-hooks/exhaustive-deps).
  const participants = useMemo(
    () => threadQuery.data?.summary?.participants ?? [],
    [threadQuery.data?.summary?.participants],
  );
  const otherParticipants = useMemo(
    () =>
      participants.filter((p) =>
        selfAddress ? p.address !== selfAddress : !isHumanParticipant(p),
      ),
    [participants, selfAddress],
  );
  const recipient = useMemo(
    () => deriveRecipient(participants, selfAddress ?? null),
    [participants, selfAddress],
  );

  if (threadQuery.isPending) {
    return (
      <div
        className="space-y-3 p-4"
        role="status"
        aria-live="polite"
        data-testid="inbox-thread-loading"
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
      <p
        className="m-4 text-sm text-muted-foreground"
        data-testid="inbox-thread-not-found"
      >
        Thread not found.
      </p>
    );
  }

  return (
    <div
      className="flex min-h-0 flex-1 flex-col"
      data-testid="inbox-thread-timeline"
    >
      <ConversationView
        threadId={threadId}
        rowActions="metadata"
        rowTestIdPrefix="inbox-event"
        detail={threadQuery.data}
        renderEmpty={({ filter, totalEvents }) => (
          <p
            className="text-sm text-muted-foreground"
            data-testid="inbox-thread-empty"
          >
            {totalEvents === 0
              ? "No events in this thread yet."
              : filter === "messages"
                ? "No messages in this thread yet."
                : "No events match the current filter."}
          </p>
        )}
        renderHeader={(api) => (
          <InboxTimelineHeader
            api={api}
            threadId={threadId}
            connected={connected}
            isFetching={threadQuery.isFetching && !threadQuery.isPending}
            otherParticipants={otherParticipants}
            allParticipants={participants}
          />
        )}
      />
      <MessageComposer
        threadId={threadId}
        recipient={recipient}
        testId="inbox-composer"
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Inbox-specific timeline header — participants strip + live status +
// thread-id link + the shared filter dropdown.
// ---------------------------------------------------------------------------

interface InboxTimelineHeaderProps {
  api: ConversationViewHeaderApi;
  threadId: string;
  connected: boolean;
  isFetching: boolean;
  otherParticipants: ParticipantRef[];
  allParticipants: ParticipantRef[];
}

function InboxTimelineHeader({
  api,
  threadId,
  connected,
  isFetching,
  otherParticipants,
  allParticipants,
}: InboxTimelineHeaderProps) {
  return (
    <div className="border-b border-border px-4 py-2 text-xs text-muted-foreground space-y-1">
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
            <span className="font-mono truncate">
              {allParticipants.map((p) => p.displayName).join(" · ")}
            </span>
          )}
        </div>
        {api.filterDropdown}
      </div>
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
        {isFetching && (
          <>
            <span aria-hidden="true">·</span>
            <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
          </>
        )}
        <span aria-hidden="true">·</span>
        <span>
          {api.totalEvents} event{api.totalEvents === 1 ? "" : "s"}
        </span>
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
        <InboxIcon
          className="h-6 w-6 text-muted-foreground"
          aria-hidden="true"
        />
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

  // The profile carries a human:// address that's compared against each
  // participant's address rather than relying on display-name equality
  // or scheme-based exclusion (#1485).
  const profileQuery = useCurrentUser();
  const selfAddress = profileQuery.data?.address ?? null;

  const items = useMemo(
    () => inboxQuery.data ?? [],
    [inboxQuery.data],
  );

  // Sort: unread-first, then by pendingSince descending (#1477).
  const sortedItems = useMemo(
    () =>
      [...items].sort((a, b) => {
        const aUnread = (a.unreadCount ?? 0) as number;
        const bUnread = (b.unreadCount ?? 0) as number;
        const aHasUnread = aUnread > 0 ? 1 : 0;
        const bHasUnread = bUnread > 0 ? 1 : 0;
        if (bHasUnread !== aHasUnread) {
          return bHasUnread - aHasUnread;
        }
        return (
          new Date(b.pendingSince).getTime() -
          new Date(a.pendingSince).getTime()
        );
      }),
    [items],
  );

  const firstThreadId = sortedItems[0]?.threadId ?? null;
  useEffect(() => {
    if (!selectedThreadId && firstThreadId) {
      router.replace(`/inbox?thread=${encodeURIComponent(firstThreadId)}`);
    }
  }, [selectedThreadId, firstThreadId, router]);

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
          <p
            className="text-sm text-muted-foreground"
            data-testid="inbox-subtitle"
          >
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
            <AlertTriangle
              className="h-4 w-4 shrink-0 mt-0.5"
              aria-hidden="true"
            />
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

          {/* Right pane: thread timeline + composer */}
          <div className="flex-1 min-w-0 flex flex-col bg-background">
            {selectedThreadId ? (
              <ThreadTimeline
                threadId={selectedThreadId}
                selfAddress={selfAddress}
              />
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
