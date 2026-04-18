"use client";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { cn, timeAgo } from "@/lib/utils";
import { Clock, ExternalLink, MessagesSquare, Users } from "lucide-react";
import Link from "next/link";

/**
 * Minimal shape for the ConversationCard. Spring Voyage's conversation
 * API is still firming up (see #410 for the detail view); this interface
 * captures the fields the card needs without coupling to any specific
 * response type. Callers can pass a record with whichever fields exist.
 */
export interface ConversationCardConversation {
  id: string;
  /** Optional short title; falls back to the first participant or id. */
  title?: string | null;
  /** `agent://…`, `unit://…`, or `human://…` addresses involved. */
  participants?: string[] | null;
  /** ISO timestamp of the latest activity. */
  lastActivityAt?: string | null;
  /** E.g. "open", "waiting-on-human", "completed". */
  status?: string | null;
}

interface ConversationCardProps {
  conversation: ConversationCardConversation;
  className?: string;
}

const statusVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  open: "default",
  active: "success",
  "waiting-on-human": "warning",
  waiting: "warning",
  blocked: "warning",
  completed: "secondary",
  error: "destructive",
};

/**
 * Reusable conversation card primitive. The `/conversations/[id]` route
 * is not yet implemented — see #410 — but cards still render the link so
 * they become live as soon as that route ships.
 */
export function ConversationCard({
  conversation,
  className,
}: ConversationCardProps) {
  const href = `/conversations/${encodeURIComponent(conversation.id)}`;
  const participants = conversation.participants ?? [];
  const title =
    conversation.title?.trim() ||
    participants[0] ||
    `Conversation ${conversation.id}`;

  const statusKey = conversation.status?.toLowerCase() ?? "";
  const statusBadgeVariant =
    statusVariant[statusKey] ?? "outline";

  return (
    <Card
      data-testid={`conversation-card-${conversation.id}`}
      className={cn(
        "relative h-full transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2",
        className,
      )}
    >
      <CardContent className="p-4">
        {/*
          Full-card overlay link (#593). See the agent/unit cards for the
          overlay pattern — the primary link's `::after` pseudo covers the
          whole card, and any descendant interactive controls are promoted
          to `relative z-[1]` so they stay clickable and focusable.
        */}
        <Link
          href={href}
          aria-label={`Open conversation ${title}`}
          data-testid={`conversation-card-link-${conversation.id}`}
          className="flex items-start justify-between gap-2 rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <MessagesSquare
                aria-hidden="true"
                className="h-4 w-4 shrink-0 text-muted-foreground"
              />
              <h3 className="truncate font-semibold">{title}</h3>
            </div>
            <p className="mt-0.5 truncate text-xs text-muted-foreground">
              {conversation.id}
            </p>
          </div>
          {conversation.status && (
            <Badge
              variant={statusBadgeVariant}
              data-testid="conversation-status-badge"
            >
              {conversation.status}
            </Badge>
          )}
        </Link>

        <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
          {participants.length > 0 ? (
            <span
              className="flex items-center gap-1"
              data-testid="conversation-participants"
            >
              <Users className="h-3 w-3" />
              <span className="truncate">
                {participants.slice(0, 3).join(", ")}
                {participants.length > 3 &&
                  ` +${participants.length - 3} more`}
              </span>
            </span>
          ) : (
            <span
              className="flex items-center gap-1"
              data-testid="conversation-participants-empty"
            >
              <Users className="h-3 w-3" />
              No participants
            </span>
          )}
          {conversation.lastActivityAt && (
            <span
              className="flex items-center gap-1"
              data-testid="conversation-last-activity"
            >
              <Clock className="h-3 w-3" />
              {timeAgo(conversation.lastActivityAt)}
            </span>
          )}
        </div>

        <div className="relative z-[1] mt-3 flex items-center justify-end">
          <Link
            href={href}
            className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
            data-testid={`conversation-open-${conversation.id}`}
          >
            Open
            <ExternalLink className="h-3 w-3" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
