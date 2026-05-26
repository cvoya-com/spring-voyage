"use client";

// /conversations — tenant-wide read-only observation view (#2787).
//
// Left pane: every thread in the tenant, polled at 60s + refetch-on-focus
// from GET /api/v1/tenant/observation/threads. Sorted by lastActivity
// descending so the most recent traffic floats to the top.
//
// Right pane: the selected thread's timeline rendered via the shared
// <ConversationView> primitive — identical to inbox/engagement — but
// **without** the <MessageComposer> sibling. This view is gated by the
// TenantObserver role and is explicitly read-only: even if the caller
// happens to be a participant of an observed thread, sending requires
// TenantUser and the engagement / inbox surfaces.
//
// Live updates on the open thread flow through `useThreadStream` inside
// <ConversationView>, which invalidates both `queryKeys.threads.*` and
// `queryKeys.conversations.*` so observed views refresh as messages
// arrive. The list itself relies on the 60s poll (new threads are rare).

import { Suspense, useEffect, useMemo } from "react";
import { Activity, MessagesSquare, RefreshCw } from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn, timeAgo } from "@/lib/utils";
import { useConversation, useConversations } from "@/lib/api/queries";
import { ConversationView } from "@/components/conversation/conversation-view";
import type { ParticipantRef, ThreadSummary } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Participant label helpers
// ---------------------------------------------------------------------------

function formatParticipants(participants: ParticipantRef[] | undefined): string {
  if (!participants || participants.length === 0) return "—";
  const names = participants.map((p) => p.displayName || p.address);
  if (names.length <= 3) return names.join(" · ");
  return `${names.slice(0, 3).join(" · ")} +${names.length - 3}`;
}

// ---------------------------------------------------------------------------
// Thread row (left pane)
// ---------------------------------------------------------------------------

interface ThreadRowProps {
  thread: ThreadSummary;
  selected: boolean;
  onSelect: () => void;
}

function ThreadRow({ thread, selected, onSelect }: ThreadRowProps) {
  const label = formatParticipants(thread.participants);
  const summary = thread.summary?.trim();
  const eventCount = thread.eventCount ?? 0;

  return (
    <button
      type="button"
      onClick={onSelect}
      data-testid={`conversations-thread-row-${thread.id}`}
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
          data-testid={`conversations-row-label-${thread.id}`}
        >
          {label}
        </span>
        <span className="text-[10px] font-mono text-muted-foreground tabular-nums shrink-0">
          {thread.lastActivity ? timeAgo(thread.lastActivity) : ""}
        </span>
      </div>
      <div className="mt-0.5 flex items-center justify-between gap-2 text-xs text-muted-foreground">
        <span className="truncate">
          {summary || "No summary available."}
        </span>
        <span
          className="font-mono tabular-nums shrink-0"
          data-testid={`conversations-row-events-${thread.id}`}
        >
          {eventCount} {eventCount === 1 ? "event" : "events"}
        </span>
      </div>
    </button>
  );
}

// ---------------------------------------------------------------------------
// Thread timeline (right pane) — ConversationView WITHOUT composer
// ---------------------------------------------------------------------------

interface ThreadTimelineProps {
  threadId: string;
}

function ThreadTimeline({ threadId }: ThreadTimelineProps) {
  // The observation endpoint returns the same ThreadDetail shape as the
  // participant-scoped /threads/{id} endpoint, so we feed it directly into
  // ConversationView. The component does its own per-thread SSE
  // subscription via useThreadStream.
  const threadQuery = useConversation(threadId, { staleTime: 0 });

  if (threadQuery.isPending) {
    return (
      <div
        className="space-y-3 p-4"
        role="status"
        aria-live="polite"
        data-testid="conversations-thread-loading"
      >
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-3/4" />
        <Skeleton className="h-14 w-full" />
      </div>
    );
  }

  if (threadQuery.error) {
    return (
      <div className="m-4" data-testid="conversations-thread-error">
        <ApiErrorMessage error={threadQuery.error} />
      </div>
    );
  }

  if (!threadQuery.data) {
    return (
      <p
        className="m-4 text-sm text-muted-foreground"
        data-testid="conversations-thread-not-found"
      >
        Conversation not found.
      </p>
    );
  }

  // Important: do NOT render <MessageComposer> here. /conversations is
  // strictly read-only; users send via /engagement or /inbox.
  return (
    <div
      className="flex min-h-0 flex-1 flex-col"
      data-testid="conversations-thread-timeline"
    >
      <ConversationView
        threadId={threadId}
        rowActions="metadata"
        rowTestIdPrefix="conversations-event"
        detail={threadQuery.data}
        renderEmpty={({ filter, totalEvents }) => (
          <p
            className="text-sm text-muted-foreground"
            data-testid="conversations-thread-empty"
          >
            {totalEvents === 0
              ? "No events in this conversation yet."
              : filter === "messages"
                ? "No messages in this conversation yet."
                : "No events match the current filter."}
          </p>
        )}
      />
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
      data-testid="conversations-no-thread"
    >
      <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full border border-border bg-muted/40">
        <MessagesSquare
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

function ConversationsPageContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const selectedThreadId = searchParams.get("thread") ?? null;

  const conversationsQuery = useConversations();

  const items = useMemo(
    () => conversationsQuery.data ?? [],
    [conversationsQuery.data],
  );

  // Sort by lastActivity descending so the freshest threads sit at the top.
  // Threads with no lastActivity (rare) sort to the bottom.
  const sortedItems = useMemo(
    () =>
      [...items].sort((a, b) => {
        const aTime = a.lastActivity ? new Date(a.lastActivity).getTime() : 0;
        const bTime = b.lastActivity ? new Date(b.lastActivity).getTime() : 0;
        return bTime - aTime;
      }),
    [items],
  );

  const firstThreadId = sortedItems[0]?.id ?? null;
  useEffect(() => {
    if (!selectedThreadId && firstThreadId) {
      router.replace(
        `/conversations?thread=${encodeURIComponent(firstThreadId)}`,
      );
    }
  }, [selectedThreadId, firstThreadId, router]);

  const handleSelectThread = (threadId: string) => {
    router.replace(`/conversations?thread=${encodeURIComponent(threadId)}`);
  };

  const error = conversationsQuery.error ?? null;

  return (
    <div
      className="flex flex-col h-full space-y-0"
      data-testid="conversations-page"
    >
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between pb-4 border-b border-border mb-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <Activity className="h-5 w-5" aria-hidden="true" /> Conversations
          </h1>
          <p
            className="text-sm text-muted-foreground"
            data-testid="conversations-subtitle"
          >
            Every thread between units and agents in the tenant. Read-only —
            use{" "}
            <Link href="/engagement" className="underline hover:text-foreground">
              Engagement
            </Link>{" "}
            or{" "}
            <Link href="/inbox" className="underline hover:text-foreground">
              Inbox
            </Link>{" "}
            to participate.
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => conversationsQuery.refetch()}
          disabled={conversationsQuery.isFetching}
          data-testid="conversations-refresh"
          className="self-start sm:self-auto"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${conversationsQuery.isFetching ? "animate-spin" : ""}`}
            aria-hidden="true"
          />
          Refresh
        </Button>
      </div>

      {/* Error banner */}
      {error && (
        <div className="mb-4" data-testid="conversations-error">
          <ApiErrorMessage error={error} />
        </div>
      )}

      {/* Loading state */}
      {conversationsQuery.isPending ? (
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
          data-testid="conversations-loading"
        >
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
        </div>
      ) : items.length === 0 && !error ? (
        /* Empty state */
        <Card data-testid="conversations-empty">
          <CardContent className="space-y-2 p-10 text-center">
            <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full border border-border bg-muted/40">
              <MessagesSquare
                className="h-6 w-6 text-muted-foreground"
                aria-hidden="true"
              />
            </div>
            <p className="text-sm font-medium">
              No conversations in this tenant yet.
            </p>
            <p className="text-xs text-muted-foreground">
              Threads appear here as soon as units and agents start exchanging
              messages.
            </p>
          </CardContent>
        </Card>
      ) : (
        /* Two-pane list-detail layout */
        <div
          className="flex flex-1 min-h-0 gap-0 border border-border rounded-lg overflow-hidden"
          data-testid="conversations-list"
        >
          {/* Left pane: thread list */}
          <div
            className="w-72 shrink-0 border-r border-border bg-card flex flex-col"
            aria-label="Tenant conversations"
            role="navigation"
          >
            <div className="flex-1 overflow-y-auto p-2 space-y-1">
              {sortedItems.map((thread) => (
                <ThreadRow
                  key={thread.id}
                  thread={thread}
                  selected={thread.id === selectedThreadId}
                  onSelect={() => handleSelectThread(thread.id)}
                />
              ))}
            </div>
          </div>

          {/* Right pane: thread timeline (no composer) */}
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
// boundary so the route can prerender. Mirrors the empty-list shape so
// the prerendered HTML doesn't shift when hydration takes over.
export default function ConversationsPage() {
  return (
    <Suspense
      fallback={
        <div
          className="flex flex-col h-full space-y-0"
          data-testid="conversations-page"
        >
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between pb-4 border-b border-border mb-4">
            <div className="space-y-1">
              <h1 className="text-2xl font-bold flex items-center gap-2">
                <Activity className="h-5 w-5" aria-hidden="true" />{" "}
                Conversations
              </h1>
            </div>
          </div>
          <div
            className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
            data-testid="conversations-loading"
          >
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
          </div>
        </div>
      }
    >
      <ConversationsPageContent />
    </Suspense>
  );
}
