"use client";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { cn, timeAgo } from "@/lib/utils";
import { ExternalLink } from "lucide-react";
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
 * Reusable conversation card primitive. Reskinned for the v2 design
 * system (plan §7 / CARD-conversation-refresh): participants render
 * as a mono address list, the status pill sits top-right, and the
 * timestamp collapses into an outline badge.
 *
 * The `/conversations/[id]` route was retired by `DEL-conversations-
 * routes` (umbrella #815 §2) — every node has a Messages tab now. The
 * card synthesises a deep-link into the first `unit://` or `agent://`
 * participant's Messages tab with the selection carried in the URL
 * query, falling back to a plain `/inbox?conversation=<id>` shortcut
 * when no unit/agent anchor is available.
 */
function resolveMessagesHref(
  conversationId: string,
  participants: readonly string[],
): string {
  for (const p of participants) {
    const idx = p.indexOf("://");
    if (idx <= 0) continue;
    const scheme = p.slice(0, idx).toLowerCase();
    const path = p.slice(idx + 3);
    if (scheme === "unit" || scheme === "agent") {
      return `/units?node=${encodeURIComponent(path)}&tab=Messages&conversation=${encodeURIComponent(conversationId)}`;
    }
  }
  // No unit/agent anchor — send the user to inbox with the selection
  // carried as a hint; the inbox card already renders a matching URL.
  return `/inbox?conversation=${encodeURIComponent(conversationId)}`;
}

export function ConversationCard({
  conversation,
  className,
}: ConversationCardProps) {
  const participants = conversation.participants ?? [];
  const href = resolveMessagesHref(conversation.id, participants);
  const title =
    conversation.title?.trim() ||
    participants[0] ||
    `Conversation ${conversation.id}`;

  const statusKey = conversation.status?.toLowerCase() ?? "";
  const statusBadgeVariant = statusVariant[statusKey] ?? "outline";
  const visibleParticipants = participants.slice(0, 3);
  const extraParticipants = participants.length - visibleParticipants.length;

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
          Full-card overlay link (#593). The primary link's `::after`
          pseudo covers the whole card; any descendant interactive
          controls are promoted to `relative z-[1]`.
        */}
        <Link
          href={href}
          aria-label={`Open conversation ${title}`}
          data-testid={`conversation-card-link-${conversation.id}`}
          className="flex items-start justify-between gap-3 rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <div className="min-w-0 flex-1">
            <h3 className="truncate text-sm font-semibold">{title}</h3>
            <p className="mt-0.5 truncate text-xs text-muted-foreground font-mono">
              {conversation.id}
            </p>
          </div>
          {conversation.status && (
            <Badge
              variant={statusBadgeVariant}
              data-testid="conversation-status-badge"
              className="shrink-0"
            >
              {conversation.status}
            </Badge>
          )}
        </Link>

        {/* Participants — mono address list. Plan §7 conversation
            pattern: addresses are identity and render in Geist mono
            so `agent://ada`, `unit://eng`, `human://savas` read as
            distinct sources at a glance. Keeps the single testid +
            comma-separated text so existing callers' snapshots and
            fallback messaging continue to work. */}
        <div className="mt-3">
          {participants.length > 0 ? (
            <div
              className="flex flex-wrap items-center gap-1 text-xs font-mono text-muted-foreground"
              data-testid="conversation-participants"
            >
              <span className="truncate">
                {visibleParticipants.join(", ")}
                {extraParticipants > 0 && ` +${extraParticipants} more`}
              </span>
            </div>
          ) : (
            <p
              className="text-xs text-muted-foreground"
              data-testid="conversation-participants-empty"
            >
              No participants
            </p>
          )}
        </div>

        <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs">
          {conversation.lastActivityAt ? (
            <Badge
              variant="outline"
              className="font-mono"
              data-testid="conversation-last-activity"
            >
              {timeAgo(conversation.lastActivityAt)}
            </Badge>
          ) : (
            <span />
          )}
          <Link
            href={href}
            className="relative z-[1] inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
            data-testid={`conversation-open-${conversation.id}`}
          >
            Open
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
