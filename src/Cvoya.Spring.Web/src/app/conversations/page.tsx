"use client";

// /conversations — tenant-wide read-only observation view (#2787 / #2790).
//
// Left pane: every thread in the tenant, polled at 60s + refetch-on-focus
// from GET /api/v1/tenant/observation/threads. Threads are grouped by
// activity recency (Today / Yesterday / Earlier this week / Older) and
// rendered with a compact row variant — observation patterns favour
// dense scanning over the per-participant detail that inbox / engagement
// need.
//
// Filter bar (#2790): unit / agent / participant / search / since /
// archived. Filter state is mirrored to the URL search params so a
// filtered view is shareable and survives refresh. Search + since are
// observer-only knobs (the engagement endpoint stays unchanged).
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

import { Suspense, useCallback, useEffect, useMemo, useState } from "react";
import {
  Activity,
  Filter,
  MessagesSquare,
  RefreshCw,
  Search,
  X,
} from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { cn, timeAgo } from "@/lib/utils";
import { useConversation, useConversations } from "@/lib/api/queries";
import { ConversationView } from "@/components/conversation/conversation-view";
import type {
  ConversationListFilters,
  ParticipantRef,
  ThreadSummary,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Filter state — mirrored to URL search params for shareability + refresh
// ---------------------------------------------------------------------------

interface ConversationsFilterState {
  unit: string;
  agent: string;
  participant: string;
  search: string;
  /** ISO date (yyyy-MM-dd) entered in the date input. Empty = no filter. */
  sinceDate: string;
  archived: boolean;
}

const EMPTY_FILTERS: ConversationsFilterState = {
  unit: "",
  agent: "",
  participant: "",
  search: "",
  sinceDate: "",
  archived: false,
};

function readFiltersFromUrl(
  params: URLSearchParams,
): ConversationsFilterState {
  return {
    unit: params.get("unit") ?? "",
    agent: params.get("agent") ?? "",
    participant: params.get("participant") ?? "",
    search: params.get("search") ?? "",
    sinceDate: params.get("since") ?? "",
    archived: params.get("archived") === "true",
  };
}

function buildSearchString(
  filters: ConversationsFilterState,
  selectedThreadId: string | null,
): string {
  const out = new URLSearchParams();
  if (filters.unit) out.set("unit", filters.unit);
  if (filters.agent) out.set("agent", filters.agent);
  if (filters.participant) out.set("participant", filters.participant);
  if (filters.search) out.set("search", filters.search);
  if (filters.sinceDate) out.set("since", filters.sinceDate);
  if (filters.archived) out.set("archived", "true");
  if (selectedThreadId) out.set("thread", selectedThreadId);
  const s = out.toString();
  return s.length === 0 ? "" : `?${s}`;
}

function toApiFilters(
  state: ConversationsFilterState,
): ConversationListFilters {
  const filters: ConversationListFilters = {};
  if (state.unit) filters.unit = state.unit;
  if (state.agent) filters.agent = state.agent;
  if (state.participant) filters.participant = state.participant;
  if (state.search) filters.search = state.search;
  if (state.archived) filters.archived = true;
  if (state.sinceDate) {
    // yyyy-MM-dd → midnight UTC ISO instant; the backend accepts any
    // ISO-8601 form and applies the `LastActivity >= since` predicate.
    filters.since = `${state.sinceDate}T00:00:00Z`;
  }
  return filters;
}

function filtersAreEmpty(state: ConversationsFilterState): boolean {
  return (
    state.unit === "" &&
    state.agent === "" &&
    state.participant === "" &&
    state.search === "" &&
    state.sinceDate === "" &&
    !state.archived
  );
}

// ---------------------------------------------------------------------------
// Time-aware grouping
// ---------------------------------------------------------------------------

type ActivityBucket = "today" | "yesterday" | "thisWeek" | "older" | "noActivity";

const BUCKET_ORDER: readonly ActivityBucket[] = [
  "today",
  "yesterday",
  "thisWeek",
  "older",
  "noActivity",
];

const BUCKET_LABELS: Record<ActivityBucket, string> = {
  today: "Today",
  yesterday: "Yesterday",
  thisWeek: "Earlier this week",
  older: "Older",
  noActivity: "No recorded activity",
};

function bucketFor(
  lastActivity: string | null | undefined,
  now: Date,
): ActivityBucket {
  if (!lastActivity) return "noActivity";
  const ts = new Date(lastActivity).getTime();
  if (Number.isNaN(ts)) return "noActivity";
  const diffMs = now.getTime() - ts;
  const DAY = 24 * 60 * 60 * 1000;
  if (diffMs < DAY) return "today";
  if (diffMs < 2 * DAY) return "yesterday";
  if (diffMs < 7 * DAY) return "thisWeek";
  return "older";
}

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
// Compact thread row (left pane) — denser than the inbox row variant
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
        // #2790: compact density vs inbox/engagement (py-1.5 + tighter inner
        // spacing). The observation flow favours scanning many threads at
        // once, so we trade row whitespace for visible row count.
        "w-full text-left px-2.5 py-1.5 rounded-md border transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "border-primary/40 bg-primary/10 text-foreground"
          : "border-transparent hover:border-border hover:bg-accent text-foreground",
      )}
    >
      <div className="flex items-baseline justify-between gap-2">
        <span
          className="font-medium text-sm truncate"
          data-testid={`conversations-row-label-${thread.id}`}
        >
          {label}
        </span>
        <span className="text-[10px] font-mono text-muted-foreground tabular-nums shrink-0">
          {thread.lastActivity ? timeAgo(thread.lastActivity) : "—"}
        </span>
      </div>
      <div className="flex items-baseline justify-between gap-2 text-xs text-muted-foreground">
        <span className="truncate">{summary || "No summary available."}</span>
        <span
          className="font-mono tabular-nums shrink-0"
          data-testid={`conversations-row-events-${thread.id}`}
        >
          {eventCount}
        </span>
      </div>
    </button>
  );
}

// ---------------------------------------------------------------------------
// Filter bar (#2790)
// ---------------------------------------------------------------------------

interface FilterBarProps {
  filters: ConversationsFilterState;
  onChange: (next: ConversationsFilterState) => void;
  resultCount: number;
  totalCount: number;
}

function FilterBar({
  filters,
  onChange,
  resultCount,
  totalCount,
}: FilterBarProps) {
  const set = useCallback(
    <K extends keyof ConversationsFilterState>(
      key: K,
      value: ConversationsFilterState[K],
    ) => {
      onChange({ ...filters, [key]: value });
    },
    [filters, onChange],
  );

  const hasFilters = !filtersAreEmpty(filters);

  return (
    <div
      className="border-b border-border bg-card/60 px-3 py-2 space-y-2"
      data-testid="conversations-filter-bar"
      role="search"
      aria-label="Conversation filters"
    >
      {/* Search row — always visible because it's the highest-value filter. */}
      <div className="relative">
        <Search
          className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground"
          aria-hidden="true"
        />
        <Input
          type="search"
          value={filters.search}
          onChange={(e) => set("search", e.target.value)}
          placeholder="Search summary, participants, addresses…"
          className="h-8 pl-7 text-xs"
          data-testid="conversations-filter-search"
          aria-label="Search conversations"
        />
      </div>

      <div className="grid grid-cols-2 gap-2">
        <Input
          type="text"
          value={filters.unit}
          onChange={(e) => set("unit", e.target.value)}
          placeholder="Unit"
          className="h-8 text-xs"
          data-testid="conversations-filter-unit"
          aria-label="Filter by unit"
        />
        <Input
          type="text"
          value={filters.agent}
          onChange={(e) => set("agent", e.target.value)}
          placeholder="Agent"
          className="h-8 text-xs"
          data-testid="conversations-filter-agent"
          aria-label="Filter by agent"
        />
      </div>

      <Input
        type="text"
        value={filters.participant}
        onChange={(e) => set("participant", e.target.value)}
        placeholder="Participant address (scheme:hex)"
        className="h-8 font-mono text-[10px]"
        data-testid="conversations-filter-participant"
        aria-label="Filter by participant address"
      />

      <div className="flex items-center gap-2">
        <Input
          type="date"
          value={filters.sinceDate}
          onChange={(e) => set("sinceDate", e.target.value)}
          className="h-8 text-xs"
          data-testid="conversations-filter-since"
          aria-label="Show conversations active since this date"
        />
        <label
          className="flex items-center gap-1.5 text-xs text-muted-foreground select-none cursor-pointer"
          data-testid="conversations-filter-archived-label"
        >
          <input
            type="checkbox"
            checked={filters.archived}
            onChange={(e) => set("archived", e.target.checked)}
            className="h-3.5 w-3.5 rounded border-input"
            data-testid="conversations-filter-archived"
          />
          Archived only
        </label>
      </div>

      <div className="flex items-center justify-between gap-2 text-[10px] text-muted-foreground">
        <span
          className="font-mono tabular-nums"
          data-testid="conversations-result-count"
        >
          {hasFilters
            ? `${resultCount} of ${totalCount} match`
            : `${totalCount} total`}
        </span>
        {hasFilters && (
          <button
            type="button"
            onClick={() => onChange(EMPTY_FILTERS)}
            className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 hover:bg-accent hover:text-foreground transition-colors"
            data-testid="conversations-filter-clear"
            aria-label="Clear all filters"
          >
            <X className="h-3 w-3" aria-hidden="true" />
            Clear
          </button>
        )}
      </div>
    </div>
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

  // Local filter state is seeded from the URL on mount and re-synced on
  // every URL change (e.g. browser back/forward, shared link). Outbound
  // updates go through `applyFilters` which mirrors to the URL.
  //
  // We depend on the URL *string* rather than the `searchParams` object —
  // `useSearchParams()` returns a fresh instance on every render even when
  // the underlying URL hasn't changed, so memoising on the object would
  // recompute `urlFilters` forever and the sync effect below would feed
  // back into itself.
  const urlString = searchParams.toString();
  const urlFilters = useMemo(
    () => readFiltersFromUrl(new URLSearchParams(urlString)),
    [urlString],
  );
  const [filters, setFilters] = useState<ConversationsFilterState>(urlFilters);

  // Keep local state in sync if the URL changes externally (back/forward).
  useEffect(() => {
    setFilters(urlFilters);
  }, [urlFilters]);

  const applyFilters = useCallback(
    (next: ConversationsFilterState) => {
      setFilters(next);
      const search = buildSearchString(next, selectedThreadId);
      router.replace(`/conversations${search}`);
    },
    [router, selectedThreadId],
  );

  // Send filters that map directly to backend query params through to the
  // server; everything else (search, since) is also server-side now via
  // the observation endpoint's narrowing knobs (#2790).
  const apiFilters = useMemo(() => toApiFilters(filters), [filters]);
  const conversationsQuery = useConversations(apiFilters);

  const items = useMemo(
    () => conversationsQuery.data ?? [],
    [conversationsQuery.data],
  );

  // Sort by lastActivity descending so the freshest threads sit at the top.
  // Threads with no lastActivity (rare) sort to the bottom of their bucket.
  const sortedItems = useMemo(
    () =>
      [...items].sort((a, b) => {
        const aTime = a.lastActivity ? new Date(a.lastActivity).getTime() : 0;
        const bTime = b.lastActivity ? new Date(b.lastActivity).getTime() : 0;
        return bTime - aTime;
      }),
    [items],
  );

  // Group by activity bucket so the operator gets a Today / Yesterday /
  // Earlier this week / Older spine through the list. The "now" anchor is
  // captured once per render — buckets are stable for the lifetime of the
  // current data set (a refetch produces a fresh anchor).
  const grouped = useMemo(() => {
    const now = new Date();
    const byBucket = new Map<ActivityBucket, ThreadSummary[]>();
    for (const thread of sortedItems) {
      const bucket = bucketFor(thread.lastActivity, now);
      const list = byBucket.get(bucket) ?? [];
      list.push(thread);
      byBucket.set(bucket, list);
    }
    return BUCKET_ORDER.filter((b) => byBucket.has(b)).map((bucket) => ({
      bucket,
      threads: byBucket.get(bucket)!,
    }));
  }, [sortedItems]);

  const firstThreadId = sortedItems[0]?.id ?? null;
  useEffect(() => {
    if (!selectedThreadId && firstThreadId) {
      const search = buildSearchString(filters, firstThreadId);
      router.replace(`/conversations${search}`);
    }
  }, [selectedThreadId, firstThreadId, router, filters]);

  const handleSelectThread = useCallback(
    (threadId: string) => {
      const search = buildSearchString(filters, threadId);
      router.replace(`/conversations${search}`);
    },
    [filters, router],
  );

  const error = conversationsQuery.error ?? null;
  const filtersActive = !filtersAreEmpty(filters);

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
            {filtersActive && (
              <Badge
                variant="outline"
                className="font-mono text-[10px] gap-1"
                data-testid="conversations-filters-active-pill"
              >
                <Filter className="h-3 w-3" aria-hidden="true" />
                filtered
              </Badge>
            )}
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
      ) : (
        /* Two-pane list-detail layout — the filter bar is always visible so
           the user can adjust the lens even when the current result set is
           empty (e.g. typed search returned nothing). */
        <div
          className="flex flex-1 min-h-0 gap-0 border border-border rounded-lg overflow-hidden"
          data-testid="conversations-list"
        >
          {/* Left pane: filter bar + grouped thread list */}
          <div
            className="w-72 shrink-0 border-r border-border bg-card flex flex-col"
            aria-label="Tenant conversations"
            role="navigation"
          >
            <FilterBar
              filters={filters}
              onChange={applyFilters}
              resultCount={sortedItems.length}
              totalCount={sortedItems.length}
            />
            <div className="flex-1 overflow-y-auto px-2 py-2">
              {items.length === 0 ? (
                <EmptyResults filtersActive={filtersActive} />
              ) : (
                grouped.map(({ bucket, threads }) => (
                  <section
                    key={bucket}
                    className="mb-3 last:mb-0"
                    data-testid={`conversations-bucket-${bucket}`}
                  >
                    <h2
                      className="px-1 mb-1 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground"
                      data-testid={`conversations-bucket-label-${bucket}`}
                    >
                      {BUCKET_LABELS[bucket]} ({threads.length})
                    </h2>
                    <div className="space-y-0.5">
                      {threads.map((thread) => (
                        <ThreadRow
                          key={thread.id}
                          thread={thread}
                          selected={thread.id === selectedThreadId}
                          onSelect={() => handleSelectThread(thread.id)}
                        />
                      ))}
                    </div>
                  </section>
                ))
              )}
            </div>
          </div>

          {/* Right pane: thread timeline (no composer) */}
          <div className="flex-1 min-w-0 flex flex-col bg-background">
            {selectedThreadId && items.length > 0 ? (
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

// ---------------------------------------------------------------------------
// Empty-results state for the list (replaces the previous full-page Card —
// keeping the filter bar visible is important when zero results come from
// an over-narrow filter).
// ---------------------------------------------------------------------------

function EmptyResults({ filtersActive }: { filtersActive: boolean }) {
  if (filtersActive) {
    return (
      <Card
        className="mt-2 border-dashed"
        data-testid="conversations-empty-filtered"
      >
        <CardContent className="space-y-2 p-6 text-center">
          <Filter
            className="mx-auto h-6 w-6 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="text-sm font-medium">No conversations match.</p>
          <p className="text-xs text-muted-foreground">
            Try clearing one of the filters above.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card className="mt-2" data-testid="conversations-empty">
      <CardContent className="space-y-2 p-6 text-center">
        <MessagesSquare
          className="mx-auto h-6 w-6 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="text-sm font-medium">
          No conversations in this tenant yet.
        </p>
        <p className="text-xs text-muted-foreground">
          Threads appear here as soon as units and agents start exchanging
          messages.
        </p>
      </CardContent>
    </Card>
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
