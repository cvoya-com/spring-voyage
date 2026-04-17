"use client";

import { useEffect, useMemo, useRef } from "react";
import Link from "next/link";
import { ArrowLeft, MessagesSquare, RefreshCw } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Breadcrumbs } from "@/components/breadcrumbs";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useConversation } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import { ConversationComposer } from "@/components/conversation/conversation-composer";
import { ConversationEventRow } from "@/components/conversation/conversation-event-row";
import { parseConversationSource } from "@/components/conversation/role";
import { timeAgo } from "@/lib/utils";

interface ConversationDetailClientProps {
  id: string;
}

const STATUS_VARIANTS: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  active: "success",
  open: "default",
  waiting: "warning",
  "waiting-on-human": "warning",
  blocked: "warning",
  completed: "secondary",
  error: "destructive",
};

export function ConversationDetailClient({ id }: ConversationDetailClientProps) {
  const conversationQuery = useConversation(id);
  const conversation = conversationQuery.data ?? null;
  const loading = conversationQuery.isPending;
  const error =
    conversationQuery.error instanceof Error
      ? conversationQuery.error.message
      : null;

  // Live updates: scope the stream to events that share this
  // conversation id (correlationId === id) OR originate from one of
  // the participants. We don't strictly need the filter — the
  // invalidation in `queryKeysAffectedBySource` covers conversation/
  // human/agent/unit scopes — but filtering keeps the in-memory
  // events list lean for any future debugging panel.
  useActivityStream({
    filter: (event) => event.correlationId === id,
  });

  // Auto-scroll the thread to the bottom whenever a new event lands.
  const eventCount = conversation?.events?.length ?? 0;
  const bottomRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [eventCount]);

  // Pick the most recent non-self speaker as the default reply
  // recipient. We don't have a "self" concept in the portal yet so
  // we approximate: prefer the latest event whose source is NOT a
  // human:// (the human is most likely the current portal user).
  // Falls back to the first participant.
  const defaultRecipient = useMemo(() => {
    if (!conversation) return undefined;
    const events = conversation.events ?? [];
    for (let i = events.length - 1; i >= 0; i--) {
      const src = parseConversationSource(events[i].source);
      if (src.scheme !== "human") {
        return src.raw;
      }
    }
    return conversation.summary?.participants?.[0];
  }, [conversation]);

  const breadcrumbs = (
    <Breadcrumbs
      items={[
        { label: "Conversations", href: "/conversations" },
        { label: conversation?.summary?.id ?? id },
      ]}
    />
  );

  if (loading) {
    return (
      <div className="space-y-4">
        {breadcrumbs}
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-4">
        {breadcrumbs}
        <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {error}
        </p>
        <Link
          href="/conversations"
          className="inline-flex items-center gap-1 text-sm text-primary hover:underline"
        >
          <ArrowLeft className="h-3.5 w-3.5" /> Back to conversations
        </Link>
      </div>
    );
  }

  if (!conversation) {
    return (
      <div className="space-y-4">
        {breadcrumbs}
        <p className="text-sm text-muted-foreground">
          Conversation <span className="font-mono">{id}</span> was not found.
        </p>
        <Link
          href="/conversations"
          className="inline-flex items-center gap-1 text-sm text-primary hover:underline"
        >
          <ArrowLeft className="h-3.5 w-3.5" /> Back to conversations
        </Link>
      </div>
    );
  }

  const summary = conversation.summary;
  const events = conversation.events ?? [];
  const statusKey = (summary?.status ?? "").toLowerCase();
  const statusVariant = STATUS_VARIANTS[statusKey] ?? "outline";
  const origin = summary?.origin ?? "";

  return (
    <div className="space-y-4">
      {breadcrumbs}

      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <MessagesSquare className="h-5 w-5" />
            <span className="truncate font-mono text-base">{summary.id}</span>
          </h1>
          <p className="text-sm text-muted-foreground">
            {summary.summary || "No summary available."}
          </p>
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            {summary.status && (
              <Badge variant={statusVariant}>{summary.status}</Badge>
            )}
            {origin && (
              <span>
                Origin:{" "}
                <Link
                  href={`/activity?source=${encodeURIComponent(origin)}`}
                  className="font-mono text-primary hover:underline"
                  aria-label="Open origin in activity log"
                >
                  {origin}
                </Link>
              </span>
            )}
            <span>Created {timeAgo(summary.createdAt)}</span>
            <span>Last activity {timeAgo(summary.lastActivity)}</span>
            <span>
              {String(summary.eventCount)}{" "}
              {Number(summary.eventCount) === 1 ? "event" : "events"}
            </span>
          </div>
          {summary.participants && summary.participants.length > 0 && (
            <div className="flex flex-wrap items-center gap-1 pt-1 text-xs">
              <span className="text-muted-foreground">Participants:</span>
              {summary.participants.map((p) => (
                <Badge key={p} variant="outline" className="font-mono">
                  {p}
                </Badge>
              ))}
            </div>
          )}
        </div>
        <div className="flex shrink-0 gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => conversationQuery.refetch()}
            disabled={conversationQuery.isFetching}
          >
            <RefreshCw
              className={`h-4 w-4 mr-1 ${
                conversationQuery.isFetching ? "animate-spin" : ""
              }`}
            />
            Refresh
          </Button>
          <Link
            href={`/activity?correlationId=${encodeURIComponent(id)}`}
            className="inline-flex h-9 items-center gap-1 rounded-md border border-input bg-background px-3 text-sm font-medium shadow-sm hover:bg-accent"
          >
            View activity
          </Link>
        </div>
      </div>

      {/* Thread + composer */}
      <Card className="flex flex-col">
        <CardHeader className="border-b border-border py-3">
          <CardTitle className="text-sm">Thread</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <div
            className="max-h-[60vh] space-y-4 overflow-y-auto p-4"
            aria-live="polite"
            aria-label="Conversation thread"
            data-testid="conversation-thread"
          >
            {events.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No events on this thread yet.
              </p>
            ) : (
              events.map((event) => (
                <ConversationEventRow key={event.id} event={event} />
              ))
            )}
            <div ref={bottomRef} />
          </div>

          <ConversationComposer
            conversationId={id}
            defaultRecipient={defaultRecipient}
            participants={summary.participants ?? []}
          />
        </CardContent>
      </Card>
    </div>
  );
}
