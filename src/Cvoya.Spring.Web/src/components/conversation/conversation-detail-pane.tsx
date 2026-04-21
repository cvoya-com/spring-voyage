"use client";

/**
 * Inline conversation detail — right-hand pane of the Unit and Agent
 * Messages tabs (#937 / #815 §2). Replaces the retired
 * `/conversations/[id]` route with an in-tab thread view.
 *
 * Renders the event timeline via `ConversationEventRow` and a reply
 * composer via `ConversationComposer`. Loading / error / 404 are all
 * handled locally so neither Messages tab has to know about the
 * fetching story.
 */

import { useMemo } from "react";

import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useConversation } from "@/lib/api/queries";

import { ConversationComposer } from "./conversation-composer";
import { ConversationEventRow } from "./conversation-event-row";
import { parseConversationSource } from "./role";

interface ConversationDetailPaneProps {
  conversationId: string;
  /**
   * Address of the node hosting the Messages tab (scheme://path). Used
   * to pick a sensible default reply recipient (the most-recently-
   * active non-self participant).
   */
  selfAddress?: string;
}

export function ConversationDetailPane({
  conversationId,
  selfAddress,
}: ConversationDetailPaneProps) {
  const query = useConversation(conversationId);

  const detail = query.data;
  // Memoize the participants array so downstream `useMemo` deps don't
  // change identity on every render (react-hooks/exhaustive-deps).
  const participants = useMemo(
    () => detail?.summary?.participants ?? [],
    [detail?.summary?.participants],
  );

  // Default recipient: the most-recently-active non-self participant.
  // `summary.participants` is emitted oldest-first server-side; the
  // last entry that isn't the hosting node is our best guess for
  // "who was I just talking to".
  const defaultRecipient = useMemo(() => {
    if (participants.length === 0) return undefined;
    for (let i = participants.length - 1; i >= 0; i--) {
      if (selfAddress && participants[i] === selfAddress) continue;
      return participants[i];
    }
    return participants[participants.length - 1];
  }, [participants, selfAddress]);

  if (query.isPending) {
    return (
      <div
        className="space-y-2 p-3"
        data-testid="conversation-detail-loading"
        role="status"
        aria-live="polite"
      >
        <Skeleton className="h-12" />
        <Skeleton className="h-12" />
        <Skeleton className="h-12" />
      </div>
    );
  }

  if (query.error) {
    return (
      <p
        role="alert"
        className="m-3 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="conversation-detail-error"
      >
        Couldn&apos;t load conversation:{" "}
        {query.error instanceof Error
          ? query.error.message
          : String(query.error)}
      </p>
    );
  }

  if (!detail) {
    return (
      <p
        className="m-3 text-sm text-muted-foreground"
        data-testid="conversation-detail-missing"
      >
        Conversation not found. It may have been deleted.
      </p>
    );
  }

  const events = detail.events ?? [];

  return (
    <div
      className="flex min-h-0 flex-1 flex-col"
      data-testid="conversation-detail"
    >
      <header className="flex items-center gap-2 border-b border-border px-3 py-2 text-xs">
        <span className="font-mono text-muted-foreground">
          {conversationId}
        </span>
        {detail.summary?.status ? (
          <Badge variant="outline">{detail.summary.status}</Badge>
        ) : null}
        {participants.length > 0 ? (
          <span className="truncate text-muted-foreground">
            {participants
              .map((p) => parseConversationSource(p).raw)
              .join(" · ")}
          </span>
        ) : null}
      </header>

      <div
        className="flex-1 space-y-3 overflow-auto p-3"
        data-testid="conversation-detail-events"
      >
        {events.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No events in this conversation yet.
          </p>
        ) : (
          events.map((event) => (
            <ConversationEventRow key={event.id} event={event} />
          ))
        )}
      </div>

      <ConversationComposer
        conversationId={conversationId}
        defaultRecipient={defaultRecipient}
        participants={participants}
      />
    </div>
  );
}
