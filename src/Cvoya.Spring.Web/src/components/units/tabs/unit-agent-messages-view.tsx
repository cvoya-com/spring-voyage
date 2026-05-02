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

import { useMemo } from "react";

import { Skeleton } from "@/components/ui/skeleton";
import { useThread, useThreads } from "@/lib/api/queries";
import type { ThreadSummary } from "@/lib/api/types";

import { ConversationView } from "@/components/conversation/conversation-view";
import { MessageComposer } from "@/components/conversation/message-composer";

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

  const canonical = useMemo(
    () => pickCanonicalThread(threadsQuery.data ?? []),
    [threadsQuery.data],
  );

  const threadDetailQuery = useThread(canonical?.id ?? "", {
    enabled: Boolean(canonical?.id),
  });

  const isInitialLoading =
    threadsQuery.isLoading ||
    (Boolean(canonical?.id) && threadDetailQuery.isPending);

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
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid={`${rootTestId}-error`}
      >
        Couldn&apos;t load conversation:{" "}
        {threadsQuery.error instanceof Error
          ? threadsQuery.error.message
          : String(threadsQuery.error)}
      </p>
    );
  }

  const threadId = canonical?.id ?? null;

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
