"use client";

// Engagement list component.
//
// Renders the sorted list of engagements for three slice contexts:
//   - "mine" (/engagement/mine): threads where the current human appears.
//   - per-unit (/engagement/mine?unit=<id>): all threads involving a given unit.
//   - per-agent (/engagement/mine?agent=<id>): all threads involving a given agent.
//
// Title/labelling: a card's title is the comma-separated display names of the
// other participants (everyone except the current human). The thread UUID is
// retained as the link target only — never as the visible label.
//
// Filter: the user can filter the list to the engagements they participate in,
// the engagements they merely observe (A2A-only or other people's threads),
// or all of them.
//
// Recency-driven sort: latest activity first. Inactive engagements render at
// lower opacity but remain visible; they resurface when new activity arrives.
//
// Archived section (#2732): below the active list we fetch a second slice
// (`archived: true`) and render it under a collapsible `Archived (N)` header.
// An engagement is "archived" when every non-human participant has been
// soft-deleted (the user has nothing actionable left). The two queries are
// independent — either can fail without blocking the other. The visibility
// filter (`all` / `participant` / `observer`) applies to both lists. When N=0
// the archived header is omitted entirely. Expand/collapse is session-scoped
// (`useState`, no persistence).

import Link from "next/link";
import { useState, useEffect, useRef } from "react";
import {
  MessagesSquare,
  Loader2,
  Eye,
  MessageCircleQuestion,
  ChevronDown,
  ChevronRight,
  Archive,
} from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useThreads, useInbox, useCurrentUser } from "@/lib/api/queries";
import type { ParticipantRef, ThreadSummary } from "@/lib/api/types";
import {
  addressOf,
  idOf,
  isHumanAddress as sharedIsHumanAddress,
  participantDisplayName,
} from "@/components/thread/role";
import { HatChip } from "@/components/conversation/hat-chip";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type EngagementVisibilityFilter = "all" | "participant" | "observer";

interface EngagementListProps {
  /**
   * Which slice to show.
   *  - "mine": threads where the authenticated human is in scope.
   *  - "unit": all threads involving a specific unit (id / slug).
   *  - "agent": all threads involving a specific agent (id / slug).
   */
  slice: "mine" | "unit" | "agent";
  /** Unit id / slug — only required when `slice === "unit"`. */
  unit?: string;
  /** Agent id / slug — only required when `slice === "agent"`. */
  agent?: string;
  /**
   * Currently-selected thread id (highlighted in the list). When omitted no
   * card is highlighted.
   */
  selectedThreadId?: string;
  /**
   * Layout density. "sidebar" renders a tight, single-column list suitable
   * for the engagement-portal sidebar. "page" renders the full card layout.
   * Defaults to "page".
   */
  variant?: "page" | "sidebar";
  /** Optional initial visibility filter. Defaults to "all". */
  initialFilter?: EngagementVisibilityFilter;
  /** Hide the visibility filter dropdown. */
  hideFilter?: boolean;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function isHumanAddress(address: string): boolean {
  // #2082 follow-up: post-RenderAddress unification, humans surface as
  // canonical `human:<hex>`. The shared role-helper recognises every
  // historical address form (navigation, identity, canonical), so use
  // it rather than rolling another prefix check that misses one of them.
  return sharedIsHumanAddress(address);
}

/**
 * Returns true when none of the participants is a human.
 */
function isA2aOnly(participants: ParticipantRef[]): boolean {
  return participants.every((p) => !isHumanAddress(addressOf(p)));
}

/**
 * Whether the current user appears in the participant list. #2082:
 * identity is a typed Guid, not an address string — compare on the
 * `id` field the server emits alongside the display address.
 */
function userIsParticipant(
  participants: ParticipantRef[],
  currentUserId: string | undefined,
): boolean {
  if (!currentUserId) return false;
  return participants.some((p) => idOf(p) === currentUserId);
}

/**
 * How "active" an engagement is — drives opacity in the list.
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
 */
function formatRelativeTime(dateStr: string): string {
  const diffMs = Date.now() - new Date(dateStr).getTime();
  const secs = Math.floor(diffMs / 1000);
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.floor(days / 30);
  if (months < 12) return `${months}mo ago`;
  const years = Math.floor(months / 12);
  return `${years}y ago`;
}

const FRESHNESS_OPACITY: Record<string, string> = {
  active: "",
  recent: "opacity-80",
  old: "opacity-60",
};

/**
 * Build the visible title for an engagement card: the display names of
 * everyone except the current user, joined by commas. Long lists fall back
 * to the first three names plus an ellipsis indicator.
 *
 * For engagements where the user is an *observer* (not a participant)
 * we render the names of every participant — there's no "self" to
 * exclude. Solo threads (just the user) surface "Just you" as a neutral
 * fallback.
 *
 * Names that fail to resolve (UUID-shaped paths, missing displayName)
 * are dropped quietly rather than printed as raw GUIDs (#1630). When
 * every name fails to resolve we surface "Unknown" as a soft fallback
 * — leaking a GUID into the title is the bug this issue tracks.
 *
 * Defensive against both `ParticipantRef` objects (server v2) and plain
 * address strings (server v1 / schema fallback). #1502 Fix 4 / #1630.
 */
function engagementTitle(
  participants: ParticipantRef[],
  currentUserId: string | undefined,
): string {
  // #2082: filter by Guid identity, not address string.
  const others = participants.filter((p) =>
    currentUserId ? idOf(p) !== currentUserId : true,
  );
  // Solo thread (just the active user)
  if (others.length === 0) return "Just you";
  const visibleNames = others
    .slice(0, 3)
    .map((p) => participantDisplayName(p))
    .filter(Boolean) as string[];
  const rest = others.length - 3;
  const head = visibleNames.join(", ");
  if (!head) {
    // No name resolved — rather than leak GUIDs, surface a neutral
    // placeholder. The user can open the engagement to see participant
    // details via the activity log if needed.
    return others.length === 1 ? "Unknown participant" : "Unknown participants";
  }
  return rest > 0 ? `${head}, …` : head;
}

// ---------------------------------------------------------------------------
// Visibility filter dropdown
// ---------------------------------------------------------------------------

const FILTER_LABELS: Record<EngagementVisibilityFilter, string> = {
  all: "All",
  participant: "Participant",
  observer: "Observer",
};

interface FilterDropdownProps {
  value: EngagementVisibilityFilter;
  onChange: (next: EngagementVisibilityFilter) => void;
}

function VisibilityFilterDropdown({ value, onChange }: FilterDropdownProps) {
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
    <div ref={ref} className="relative" data-testid="engagement-filter-dropdown">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className={cn(
          "flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          open && "bg-accent text-foreground",
        )}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label="Filter engagements by visibility"
        data-testid="engagement-filter-trigger"
      >
        <span data-testid="engagement-filter-label">
          {FILTER_LABELS[value]}
        </span>
        <ChevronDown className="h-3 w-3 shrink-0" aria-hidden="true" />
      </button>
      {open && (
        <div
          role="listbox"
          aria-label="Engagement filter options"
          className="absolute right-0 top-full z-10 mt-1 min-w-[10rem] rounded-md border border-border bg-popover shadow-md"
          data-testid="engagement-filter-menu"
        >
          {(["all", "participant", "observer"] as EngagementVisibilityFilter[]).map(
            (opt) => (
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
                data-testid={`engagement-filter-option-${opt}`}
              >
                {FILTER_LABELS[opt]}
              </button>
            ),
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Engagement card
// ---------------------------------------------------------------------------

interface EngagementCardProps {
  thread: ThreadSummary;
  /** Whether the inbox has a pending question for this engagement. */
  hasPendingQuestion?: boolean;
  /** Whether the current human is a participant on this thread. */
  isParticipant: boolean;
  /** The display title (participant names — see {@link engagementTitle}). */
  title: string;
  /** Highlight as currently selected. */
  selected?: boolean;
  /** Layout variant. */
  variant: "page" | "sidebar";
}

function EngagementCard({
  thread,
  hasPendingQuestion,
  isParticipant,
  title,
  selected,
  variant,
}: EngagementCardProps) {
  const freshness = activityFreshness(thread.lastActivity);
  const isObserver = !isParticipant;

  const containerClass =
    variant === "sidebar"
      ? cn(
          "block rounded-md border-l-2 border-l-transparent px-3 py-2 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          selected
            ? "border-l-primary bg-primary/10 text-foreground"
            : "hover:bg-accent",
          FRESHNESS_OPACITY[freshness],
        )
      : cn(
          "block rounded-lg border border-border bg-card text-card-foreground shadow-sm",
          "transition-all hover:border-primary/40 hover:bg-accent focus-visible:outline-none",
          "focus-visible:ring-2 focus-visible:ring-ring",
          selected && "border-primary/60 bg-primary/5",
          FRESHNESS_OPACITY[freshness],
        );

  return (
    <Link
      href={`/engagement/${thread.id}`}
      className={containerClass}
      data-testid={`engagement-card-${thread.id}`}
      aria-label={`Engagement with ${title}`}
      aria-current={selected ? "page" : undefined}
    >
      <div
        className={cn(
          "flex flex-col",
          variant === "sidebar" ? "gap-1" : "gap-2 p-4",
        )}
      >
        {/* Header row: icon + title + activity */}
        <div className="flex items-start justify-between gap-2">
          <div className="flex min-w-0 items-center gap-2">
            {hasPendingQuestion ? (
              <MessageCircleQuestion
                className="h-4 w-4 shrink-0 text-warning"
                aria-label="Awaiting your answer"
              />
            ) : isObserver ? (
              <Eye
                className="h-4 w-4 shrink-0 text-muted-foreground"
                aria-label="You are observing this engagement"
              />
            ) : (
              <MessagesSquare
                className="h-4 w-4 shrink-0 text-voyage"
                aria-hidden="true"
              />
            )}
            <span
              className="truncate text-sm font-medium"
              data-testid="engagement-card-title"
            >
              {title}
            </span>
          </div>

          <div className="flex shrink-0 items-center gap-1.5">
            {hasPendingQuestion && (
              <Badge variant="warning" className="h-5 px-1.5 text-[10px]">
                Question
              </Badge>
            )}
            <span className="tabular-nums text-[10px] text-muted-foreground">
              {formatRelativeTime(thread.lastActivity)}
            </span>
          </div>
        </div>

        {/* Summary — only on the page variant */}
        {variant === "page" && thread.summary && (
          <p className="line-clamp-2 text-sm text-muted-foreground">
            {thread.summary}
          </p>
        )}

        {/* ADR-0062 § 5 (#2826, #2829): per-row Hat chip — identifies
            which of the operator's bound Humans received the latest
            inbound on this thread. Renders the server-computed
            disambiguated label (e.g. "Bob — designer") so same-named
            siblings stay distinct; falls back to the raw display name
            when the recipient is outside the caller's bound set.
            <HatChip /> returns null when the wire field is absent
            (pure A2A threads), so no extra gate here. */}
        {(thread.recipientHumanDisambiguatedLabel ?? thread.recipientHumanDisplayName) && (
          <div>
            <HatChip
              label={thread.recipientHumanDisambiguatedLabel ?? thread.recipientHumanDisplayName}
              testId={`engagement-hat-chip-${thread.id}`}
            />
          </div>
        )}

        {/* Footer */}
        {variant === "page" && (
          <div className="flex items-center justify-between text-[11px] text-muted-foreground">
            <span>{thread.eventCount ?? 0} events</span>
          </div>
        )}
      </div>
    </Link>
  );
}

// ---------------------------------------------------------------------------
// Loading skeleton
// ---------------------------------------------------------------------------

function EngagementListSkeleton({ variant }: { variant: "page" | "sidebar" }) {
  return (
    <div
      className={variant === "sidebar" ? "space-y-2" : "space-y-3"}
      role="status"
      aria-live="polite"
      data-testid="engagement-list-loading"
    >
      {[1, 2, 3].map((i) => (
        <Skeleton
          key={i}
          className={cn(
            "w-full rounded-md",
            variant === "sidebar" ? "h-10" : "h-28",
          )}
        />
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
  variant: "page" | "sidebar";
  filter: EngagementVisibilityFilter;
}

function EngagementListEmpty({
  slice,
  unit,
  agent,
  variant,
  filter,
}: EmptyStateProps) {
  const message = (() => {
    if (filter === "participant") {
      return "No engagements where you are a participant.";
    }
    if (filter === "observer") {
      return "No engagements where you are an observer.";
    }
    if (slice === "unit") {
      return `No engagements found for unit "${unit}".`;
    }
    if (slice === "agent") {
      return `No engagements found for agent "${agent}".`;
    }
    return "No engagements yet. Start a unit and assign it a task to begin an engagement.";
  })();

  if (variant === "sidebar") {
    return (
      <div
        className="px-3 py-6 text-center text-xs text-muted-foreground"
        data-testid="engagement-list-empty"
      >
        {message}
      </div>
    );
  }

  return (
    <Card data-testid="engagement-list-empty">
      <CardContent className="flex flex-col items-center justify-center p-8 text-center">
        <MessagesSquare
          className="mb-3 h-10 w-10 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="mb-1 font-medium">No engagements</p>
        <p className="text-sm text-muted-foreground">{message}</p>
        {slice === "mine" && filter === "all" && (
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
// Archived section header (#2732)
// ---------------------------------------------------------------------------

interface ArchivedSectionHeaderProps {
  count: number;
  open: boolean;
  onToggle: () => void;
  panelId: string;
  variant: "page" | "sidebar";
}

function ArchivedSectionHeader({
  count,
  open,
  onToggle,
  panelId,
  variant,
}: ArchivedSectionHeaderProps) {
  const Chevron = open ? ChevronDown : ChevronRight;
  // Real <button> with aria-expanded + aria-controls. The count is a real
  // text node ("Archived, N items") so screen readers read it as part of
  // the label rather than as a separate badge node.
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={open}
      aria-controls={panelId}
      aria-label={`Archived, ${count} ${count === 1 ? "item" : "items"}`}
      className={cn(
        "group flex w-full items-center gap-2 rounded-md text-left text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        variant === "sidebar" ? "px-2 py-1.5" : "px-3 py-2",
      )}
      data-testid="engagement-archived-toggle"
    >
      <Chevron className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <Archive className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <span className="text-[11px] font-medium uppercase tracking-wider">
        Archived
      </span>
      <span
        className="tabular-nums text-[11px] text-muted-foreground/80"
        data-testid="engagement-archived-count"
      >
        ({count})
      </span>
    </button>
  );
}

// ---------------------------------------------------------------------------
// Section body (active or archived) — internal renderer
// ---------------------------------------------------------------------------

interface DecoratedThread {
  thread: ThreadSummary;
  isParticipant: boolean;
}

interface ListBodyProps {
  threads: DecoratedThread[];
  pendingThreadIds: Set<string>;
  currentUserId: string | undefined;
  selectedThreadId: string | undefined;
  variant: "page" | "sidebar";
  /**
   * Mute the cards (used by the archived section so the section is
   * visually distinct from the active list).
   */
  muted?: boolean;
  /** Element id for aria-controls wiring. */
  id?: string;
  /** Stable testid for the list root. */
  testId: string;
}

function ListBody({
  threads,
  pendingThreadIds,
  currentUserId,
  selectedThreadId,
  variant,
  muted,
  id,
  testId,
}: ListBodyProps) {
  return (
    <div
      id={id}
      className={cn(
        variant === "sidebar" ? "flex flex-col gap-1" : "space-y-3",
        muted && "opacity-70",
      )}
      data-testid={testId}
      aria-label={muted ? "Archived engagements" : "Engagements"}
    >
      {threads.map(({ thread, isParticipant }) => (
        <EngagementCard
          key={thread.id}
          thread={thread}
          hasPendingQuestion={pendingThreadIds.has(thread.id)}
          isParticipant={isParticipant}
          title={engagementTitle(thread.participants ?? [], currentUserId)}
          selected={selectedThreadId === thread.id}
          variant={variant}
        />
      ))}
    </div>
  );
}

/**
 * Apply the visibility filter and recency sort to a raw thread list.
 * Shared between the active and archived slices so the two stay in
 * lock-step.
 */
function filterAndSort(
  threads: ThreadSummary[],
  currentUserId: string | undefined,
  filter: EngagementVisibilityFilter,
): DecoratedThread[] {
  const decorated = threads.map((thread) => {
    const participants = thread.participants ?? [];
    const isParticipant = userIsParticipant(participants, currentUserId);
    return { thread, isParticipant };
  });

  const visible =
    filter === "participant"
      ? decorated.filter((d) => d.isParticipant)
      : filter === "observer"
        ? decorated.filter((d) => !d.isParticipant)
        : decorated;

  return [...visible].sort(
    (a, b) =>
      new Date(b.thread.lastActivity).getTime() -
      new Date(a.thread.lastActivity).getTime(),
  );
}

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

export function EngagementList({
  slice,
  unit,
  agent,
  selectedThreadId,
  variant = "page",
  initialFilter = "all",
  hideFilter = false,
}: EngagementListProps) {
  const [filter, setFilter] =
    useState<EngagementVisibilityFilter>(initialFilter);
  // Session-scoped collapse state for the archived section (#2732). No
  // localStorage / no URL param — the operator's last expand state does
  // not survive a refresh on purpose; archived threads should not stay
  // visually expanded across sessions.
  const [archivedOpen, setArchivedOpen] = useState(false);

  // Build the filter for the API call.
  const baseFilters = (() => {
    if (slice === "unit" && unit) return { unit };
    if (slice === "agent" && agent) return { agent };
    return {};
  })();

  const threadsQuery = useThreads(baseFilters, { staleTime: 10_000 });
  // #2732: archived slice — same scope (slice + unit/agent filter)
  // independently fetched so either query can fail without blocking
  // the other. The default-off `archived: true` flag keeps the
  // active-list call URL identical to the pre-archive shape.
  const archivedQuery = useThreads(
    { ...baseFilters, archived: true },
    { staleTime: 10_000 },
  );
  const inboxQuery = useInbox({ staleTime: 10_000 });
  const userQuery = useCurrentUser({ staleTime: 60_000 });

  // #2082: identity comparisons go via the typed Guid id, not the
  // textual address.
  const currentUserId = userQuery.data?.id?.toLowerCase() ?? undefined;

  // Pending-question lookup keyed by thread id.
  const pendingThreadIds = new Set<string>(
    (inboxQuery.data ?? []).map((item) => item.threadId).filter(Boolean),
  );

  if (threadsQuery.isPending) {
    return <EngagementListSkeleton variant={variant} />;
  }

  if (threadsQuery.error) {
    return (
      <div
        className={cn(variant === "sidebar" ? "px-3 py-2" : "px-4 py-3")}
        data-testid="engagement-list-error"
      >
        <ApiErrorMessage error={threadsQuery.error} />
      </div>
    );
  }

  const activeVisible = filterAndSort(
    threadsQuery.data ?? [],
    currentUserId,
    filter,
  );
  const archivedVisible = filterAndSort(
    archivedQuery.data ?? [],
    currentUserId,
    filter,
  );
  const archivedCount = archivedVisible.length;

  const archivedPanelId = "engagement-archived-list";

  // Decide whether to render the active-list empty state. The empty
  // state replaces the active list only when *both* slices are empty;
  // if the operator has archived threads waiting, those should still
  // surface (the archived section becomes the only visible content).
  const showActiveEmpty = activeVisible.length === 0 && archivedCount === 0;

  return (
    <div
      className={variant === "sidebar" ? "flex flex-col gap-2" : "space-y-3"}
      data-testid="engagement-list-root"
    >
      {!hideFilter && (
        <div
          className={cn(
            "flex items-center justify-between",
            variant === "sidebar" && "px-1",
          )}
          data-testid="engagement-list-filter-bar"
        >
          <span className="text-[11px] uppercase tracking-wider text-muted-foreground">
            {variant === "sidebar" ? "Engagements" : "Filter"}
          </span>
          <VisibilityFilterDropdown value={filter} onChange={setFilter} />
        </div>
      )}

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

      {showActiveEmpty ? (
        <EngagementListEmpty
          slice={slice}
          unit={unit}
          agent={agent}
          variant={variant}
          filter={filter}
        />
      ) : activeVisible.length > 0 ? (
        <ListBody
          threads={activeVisible}
          pendingThreadIds={pendingThreadIds}
          currentUserId={currentUserId}
          selectedThreadId={selectedThreadId}
          variant={variant}
          testId="engagement-list"
        />
      ) : null}

      {archivedCount > 0 && (
        <section
          className={cn(
            // Visual separation from the active list. The section
            // header stays low-key (muted) so the active list keeps
            // visual priority.
            variant === "sidebar"
              ? "mt-1 flex flex-col gap-1"
              : "mt-2 space-y-2 border-t border-border/60 pt-3",
          )}
          data-testid="engagement-archived-section"
          aria-label="Archived engagements"
        >
          <ArchivedSectionHeader
            count={archivedCount}
            open={archivedOpen}
            onToggle={() => setArchivedOpen((v) => !v)}
            panelId={archivedPanelId}
            variant={variant}
          />
          {archivedOpen && (
            <ListBody
              threads={archivedVisible}
              pendingThreadIds={pendingThreadIds}
              currentUserId={currentUserId}
              selectedThreadId={selectedThreadId}
              variant={variant}
              muted
              id={archivedPanelId}
              testId="engagement-archived-list"
            />
          )}
        </section>
      )}
    </div>
  );
}
