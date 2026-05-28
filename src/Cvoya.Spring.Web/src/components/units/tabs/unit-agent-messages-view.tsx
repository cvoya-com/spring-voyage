"use client";

// Shared timeline + inline-composer view for the Unit and Agent
// Messages tabs (#1459 / #1460, fixed #1472, consolidated #1554). The
// two tabs both render all threads involving the hosting unit/agent,
// with the most-recently-active one shown inline.
//
// #1472 fix: the `participant` filter was gating on `address` from the
// user-profile response, but `UserProfileResponse` does not include an
// `address` field on the wire. The filter now keys only on the hosting
// node — the tab is already scoped to that node, so showing all threads
// involving it is the correct semantic for v0.1.
//
// #1554 consolidation: the timeline + composer are now the shared
// <ConversationView> + <MessageComposer> primitives so this surface
// behaves identically to the engagement detail view and the inbox right
// pane. Differences kept here: the recipient is fixed (the hosting
// unit/agent — not derived from participants), and the empty-state copy
// names the target so "No conversation with <ada> yet" reads natural.
//
// #2885 multi-thread switcher: when the host participates in 2+ threads
// (e.g. copy-editor is in both `{op, copy-editor, staff-writer}` and
// `{op, copy-editor}`), a compact chip row above the timeline lets the
// operator pick which one is rendered inline. Default selection is
// `pickCanonicalThread()` — the most-recently-active — so the change is
// additive and the single-thread case stays chrome-free.

import { useMemo, useState } from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Skeleton } from "@/components/ui/skeleton";
import { useThread, useThreads } from "@/lib/api/queries";
import type { ParticipantRef, ThreadSummary } from "@/lib/api/types";
import { cn } from "@/lib/utils";

import { ConversationView } from "@/components/conversation/conversation-view";
import { HatChip } from "@/components/conversation/hat-chip";
import { MessageComposer } from "@/components/conversation/message-composer";
import { parseThreadSource } from "@/components/thread/role";

interface UnitAgentMessagesViewProps {
  /** Hosting node kind — drives the threads filter and routing target. */
  targetScheme: "unit" | "agent";
  /** Hosting node id (slug). */
  targetPath: string;
  /** Display name for empty-state copy. */
  targetName: string;
  /** Test-id root for the empty state + container, e.g. `tab-unit-messages`. */
  rootTestId: string;
}

/** Pick the most-recently-active thread when more than one matches. */
function pickCanonicalThread(threads: ThreadSummary[]): ThreadSummary | null {
  if (threads.length === 0) return null;
  if (threads.length === 1) return threads[0];
  return [...threads].sort((a, b) => {
    const ba = b.lastActivity ?? "";
    const aa = a.lastActivity ?? "";
    return ba.localeCompare(aa);
  })[0];
}

/**
 * Derive a "to" label for a thread row by listing every participant
 * except the hosting unit/agent. Mirrors the conversation-card
 * convention — same idea as `otherParticipantNames` in `/inbox` and
 * `formatParticipants` in `/conversations`, but scoped to "exclude the
 * hosting node" rather than the caller. Uses `parseThreadSource()` so
 * canonical (`scheme:<hex>`), navigation (`scheme://path`), and identity
 * (`scheme:id:<uuid>`) wire forms all compare equal when they point at
 * the same node. Truncates at 3 names with a "+N" tail.
 */
function otherParticipantsLabel(
  thread: ThreadSummary,
  hostScheme: "unit" | "agent",
  hostPath: string,
): string {
  // Defensive: some legacy projection paths (and some neighbour test
  // mocks) still pass bare-string participants. Coerce both shapes into
  // `{ address, displayName }` so the switcher renders rather than
  // crashing in `parseThreadSource()`.
  const raw = thread.participants ?? [];
  const normalised = raw
    .map((p: unknown): { address: string; displayName: string } | null => {
      if (typeof p === "string") return { address: p, displayName: p };
      if (p && typeof p === "object" && "address" in p) {
        const ref = p as ParticipantRef;
        return {
          address: ref.address ?? "",
          displayName: ref.displayName ?? ref.address ?? "",
        };
      }
      return null;
    })
    .filter((p): p is { address: string; displayName: string } => p !== null);

  const others = normalised.filter((p) => {
    if (!p.address) return true;
    const parsed = parseThreadSource(p.address);
    return !(parsed.scheme === hostScheme && parsed.path === hostPath);
  });
  if (others.length === 0) return "Just you";
  const names = others.map((p) => p.displayName || p.address);
  if (names.length <= 3) return names.join(", ");
  return `${names.slice(0, 3).join(", ")} +${names.length - 3}`;
}

/**
 * Compact chip row above the timeline that lets the operator switch
 * between every thread the host participates in (#2885). Rendered only
 * when there are 2+ threads — the single-thread surface stays
 * chrome-free. Each chip is a `<button type="button">` so keyboard
 * activation (Enter / Space) is handled natively by the browser.
 */
interface ThreadSwitcherProps {
  threads: ThreadSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  hostScheme: "unit" | "agent";
  hostPath: string;
  rootTestId: string;
}

function ThreadSwitcher({
  threads,
  selectedId,
  onSelect,
  hostScheme,
  hostPath,
  rootTestId,
}: ThreadSwitcherProps) {
  return (
    <div
      role="tablist"
      aria-label="Switch conversation"
      className="flex flex-wrap items-center gap-1.5 border-b border-border px-3 py-1.5"
      data-testid={`${rootTestId}-thread-switcher`}
    >
      {threads.map((t) => {
        const selected = t.id === selectedId;
        const label = otherParticipantsLabel(t, hostScheme, hostPath);
        const eventCount = t.eventCount ?? 0;
        return (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={selected}
            onClick={() => onSelect(t.id)}
            data-testid={`${rootTestId}-thread-switcher-item-${t.id}`}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              selected
                ? "border-primary/40 bg-primary/10 text-foreground"
                : "border-border text-muted-foreground hover:bg-accent hover:text-foreground",
            )}
          >
            <span className="max-w-[16ch] truncate">{label}</span>
            <span className="font-mono tabular-nums text-[10px] text-muted-foreground">
              {eventCount}
            </span>
          </button>
        );
      })}
    </div>
  );
}

export function UnitAgentMessagesView({
  targetScheme,
  targetPath,
  targetName,
  rootTestId,
}: UnitAgentMessagesViewProps) {
  const threadsQuery = useThreads(
    targetScheme === "unit"
      ? { unit: targetPath }
      : { agent: targetPath },
  );

  const threads = useMemo(
    () => threadsQuery.data ?? [],
    [threadsQuery.data],
  );

  const canonical = useMemo(
    () => pickCanonicalThread(threads),
    [threads],
  );

  // #2885: which thread the operator is viewing inline. The operator's
  // last explicit pick is tracked as "intent" — when the intent is null
  // (mount) or its target disappears from the threads list (deletion /
  // re-query) we fall back to `pickCanonicalThread()` so single-thread
  // surfaces and first-paint multi-thread surfaces both behave like the
  // pre-switcher world. Deriving the effective selection during render
  // (instead of mirroring it via `useEffect` + `setState`) keeps this
  // single-source-of-truth and avoids the cascade-rerender lint
  // (`react-hooks/set-state-in-effect`).
  const [selectedThreadIntent, setSelectedThreadIntent] = useState<
    string | null
  >(null);

  const selectedThread = useMemo(() => {
    if (selectedThreadIntent) {
      const match = threads.find((t) => t.id === selectedThreadIntent);
      if (match) return match;
    }
    return canonical;
  }, [threads, selectedThreadIntent, canonical]);

  const threadDetailQuery = useThread(selectedThread?.id ?? "", {
    enabled: Boolean(selectedThread?.id),
  });

  const isInitialLoading =
    threadsQuery.isLoading ||
    (Boolean(selectedThread?.id) && threadDetailQuery.isPending);

  if (isInitialLoading) {
    return (
      <div
        className="space-y-2"
        role="status"
        aria-live="polite"
        data-testid={`${rootTestId}-loading`}
      >
        <Skeleton className="h-16" />
        <Skeleton className="h-16" />
        <Skeleton className="h-16" />
      </div>
    );
  }

  if (threadsQuery.error) {
    return (
      <div data-testid={`${rootTestId}-error`}>
        <ApiErrorMessage error={threadsQuery.error} />
      </div>
    );
  }

  const threadId = selectedThread?.id ?? null;
  // ADR-0062 § 5 (#2826, #2829): show the receiving Hat for the
  // selected thread inline above the timeline. Render the server-
  // computed disambiguated label so same-named Hats stay distinct;
  // fall back to the raw display name when the recipient is outside
  // the caller's bound set. <HatChip /> returns null for pure A2A
  // threads, so there's no extra "is this human-addressed?" gate.
  const hatLabel =
    selectedThread?.recipientHumanDisambiguatedLabel ??
    selectedThread?.recipientHumanDisplayName ??
    null;

  return (
    // h-full + min-h-0 anchors the column to the explorer tab panel's
    // height so the timeline owns the only scrollbar and the composer
    // stays pinned at the bottom (#1549). The min-h-[28rem] floor still
    // applies for short tab panels (compact viewports) so the layout
    // does not collapse to nothing when the panel itself is short.
    <div
      className="flex h-full min-h-[28rem] flex-col"
      data-testid={rootTestId}
    >
      {threads.length >= 2 && (
        <ThreadSwitcher
          threads={threads}
          selectedId={threadId}
          onSelect={setSelectedThreadIntent}
          hostScheme={targetScheme}
          hostPath={targetPath}
          rootTestId={rootTestId}
        />
      )}
      {hatLabel && threadId && (
        <div
          className="border-b border-border px-3 py-1.5"
          data-testid={`${rootTestId}-hat-banner`}
        >
          <HatChip
            label={hatLabel}
            testId={`${rootTestId}-hat-chip-${threadId}`}
          />
        </div>
      )}
      {threadId ? (
        <ConversationView
          threadId={threadId}
          rowActions="activity-link"
          testId={`${rootTestId}-timeline`}
          eventListTestId={`${rootTestId}-timeline-events`}
          renderEmpty={({ filter, totalEvents }) => (
            <p
              className="text-sm text-muted-foreground"
              data-testid={`${rootTestId}-empty`}
            >
              {totalEvents === 0 ? (
                <>
                  No conversation with{" "}
                  <span className="font-medium">{targetName}</span> yet. Send
                  the first message below to start one.
                </>
              ) : filter === "messages" ? (
                <>
                  No messages yet — switch to “Full timeline” to see all
                  events.
                </>
              ) : (
                <>No events match the current filter.</>
              )}
            </p>
          )}
        />
      ) : (
        <div
          className="flex-1 overflow-auto rounded-md border border-border bg-background p-3"
          data-testid={`${rootTestId}-timeline`}
        >
          <p
            className="text-sm text-muted-foreground"
            data-testid={`${rootTestId}-empty`}
          >
            No conversation with{" "}
            <span className="font-medium">{targetName}</span> yet. Send the
            first message below to start one.
          </p>
        </div>
      )}

      <MessageComposer
        threadId={threadId}
        recipient={{ scheme: targetScheme, path: targetPath }}
        testId={`${rootTestId}-composer`}
      />
    </div>
  );
}
