"use client";

// Human × Messages tab body (#2268, Portal Wave B). Renders the threads
// the active Human is addressed in as a participant — the same canonical
// `<ConversationView>` primitive the Unit/Agent Messages tabs use, with
// two deliberate differences:
//
//   1. Threads are filtered by `participant=human:<id>` (not `unit=` /
//      `agent=`). The server's participant filter (#2082) tolerates any
//      of the historical address forms — canonical `human:<guid>`,
//      navigation `human://<guid>`, identity `human:id:<hex>` — and
//      compares on the parsed typed Guid identity, so a bare canonical
//      `human:<id>` string is sufficient.
//
//   2. **View-only.** No `<MessageComposer>` ships in v0.1: the Human
//      page is a third-party observer surface (the operator looking at
//      "this person's threads") — sending messages from the Human page
//      has no recipient ("to" would be the human themselves, which is
//      not a meaningful outbound). See `docs/design/canonical-tabs.md`
//      § 5.3 and `src/Cvoya.Spring.Web/DESIGN.md` § 9.1 (Human ×
//      Messages row) for the contract.
//
// The "You" hint convention from the Overview tab (#2267 / A5) is
// implicit here: `<ConversationView>`'s default `layout="dialog"` aligns
// human-role bubbles to the right, so when the human under view is the
// caller their own bubbles land on the right axis automatically — no
// extra "You" badge is rendered, because the alignment IS the hint.

import { useMemo } from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Skeleton } from "@/components/ui/skeleton";
import { ConversationView } from "@/components/conversation/conversation-view";
import { useHuman, useThreads } from "@/lib/api/queries";
import type { ThreadSummary } from "@/lib/api/types";

import type { HumanNode } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

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

interface HumanMessagesBodyProps {
  /** Stable Guid id of the human under view. */
  id: string;
  /** Display name for the empty-state copy. Falls back to "this human". */
  name: string;
}

/**
 * The Human × Messages body — view-only timeline for threads the human
 * is addressed in. Exported separately from the registered adapter so
 * component tests can render the body directly without re-mounting the
 * full tab registry.
 */
export function HumanMessagesBody({ id, name }: HumanMessagesBodyProps) {
  // Canonical address form: `human:<guid>`. The server parses any
  // address form via `AddressIdentity.TryGetActorId` so this is the
  // shape we use across the portal.
  const participant = `human:${id}`;
  const threadsQuery = useThreads({ participant });

  const canonical = useMemo(
    () => pickCanonicalThread(threadsQuery.data ?? []),
    [threadsQuery.data],
  );

  const rootTestId = "tab-human-messages";

  if (threadsQuery.isLoading) {
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

  const threadId = canonical?.id ?? null;

  return (
    // h-full + min-h-0 anchors the column to the explorer tab panel
    // height so the timeline owns the only scrollbar (mirror of the
    // Unit/Agent Messages shape — #1549). The min-h-[28rem] floor keeps
    // the layout from collapsing on short tab panels. No composer
    // pinned at the bottom — the surface is view-only.
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
                <>No messages yet.</>
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
            No messages yet for{" "}
            <span className="font-medium">{name}</span>.
          </p>
        </div>
      )}
    </div>
  );
}

/**
 * Registered tab adapter. Resolves the human's display name via
 * `useHuman(id)` so the empty-state copy reads "No messages yet for
 * <name>" without the caller needing to pass it in (the tree node only
 * carries `name` from the wire envelope, and humans don't appear in
 * `GET /api/v1/tenant/tree` — so the page route is the only place that
 * sees an authoritative display name).
 */
function HumanMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Human") return null;
  return <HumanMessagesAdapter node={node} />;
}

interface HumanMessagesAdapterProps {
  node: HumanNode;
}

function HumanMessagesAdapter({ node }: HumanMessagesAdapterProps) {
  // Mirror the Overview tab pattern: a single `useHuman(id)` query
  // hydrates the display name from the wire envelope. While the query
  // is pending we fall back to the tree-side name (which the route
  // sets on the node when the Detail Pane mounts).
  const humanQuery = useHuman(node.id);
  const name = humanQuery.data?.displayName ?? node.name;
  return <HumanMessagesBody id={node.id} name={name} />;
}

registerTab("Human", "Messages", HumanMessagesTab);

export default HumanMessagesTab;
