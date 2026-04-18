"use client";

import { Clock, ExternalLink, Inbox, User } from "lucide-react";
import Link from "next/link";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  parseConversationSource,
  type ParsedConversationSource,
} from "@/components/conversation/role";
import type { InboxItem } from "@/lib/api/types";
import { cn, timeAgo } from "@/lib/utils";

/**
 * Resolve a `scheme://path` sender address to a portal detail route
 * when one exists. `agent://` and `unit://` resolve to their detail
 * pages; `human://` has no detail page today, so the caller renders
 * the badge as plain text. Mirrors the cross-link rules in DESIGN.md
 * § 7.14.
 */
function fromHref(parsed: ParsedConversationSource): string | null {
  if (parsed.scheme === "agent") {
    return `/agents/${encodeURIComponent(parsed.path)}`;
  }
  if (parsed.scheme === "unit") {
    return `/units/${encodeURIComponent(parsed.path)}`;
  }
  return null;
}

export interface InboxCardProps {
  item: InboxItem;
  className?: string;
}

/**
 * Reusable card primitive for an inbox row — one conversation awaiting
 * a response from the current human. The shape matches the payload
 * returned by `GET /api/v1/inbox` (the same data feeding the CLI's
 * `spring inbox list`). Rendering stays consistent with the other
 * entity cards in DESIGN.md § 7.11: title on the top row, meta row
 * with `from` + `timeAgo(pendingSince)`, and a trailing "Open thread"
 * affordance that deep-links to `/conversations/{id}`.
 */
export function InboxCard({ item, className }: InboxCardProps) {
  const href = `/conversations/${encodeURIComponent(item.conversationId)}`;
  const from = parseConversationSource(item.from);
  const fromLink = fromHref(from);
  const title = item.summary?.trim() || item.conversationId;

  return (
    <Card
      data-testid={`inbox-card-${item.conversationId}`}
      className={cn(
        "h-full transition-colors hover:border-primary/50 hover:bg-muted/30",
        className,
      )}
    >
      <CardContent className="p-4">
        <Link
          href={href}
          aria-label={`Open conversation ${title}`}
          className="flex items-start justify-between gap-2"
        >
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <Inbox
                aria-hidden="true"
                className="h-4 w-4 shrink-0 text-muted-foreground"
              />
              <h3 className="truncate font-semibold">{title}</h3>
            </div>
            <p className="mt-0.5 truncate text-xs text-muted-foreground">
              {item.conversationId}
            </p>
          </div>
          <Badge
            variant="warning"
            data-testid="inbox-status-badge"
            className="shrink-0"
          >
            Awaiting you
          </Badge>
        </Link>

        <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
          <span
            className="flex items-center gap-1 min-w-0"
            data-testid="inbox-from"
          >
            <User className="h-3 w-3 shrink-0" />
            <span className="truncate">
              From{" "}
              {fromLink ? (
                <Link
                  href={fromLink}
                  className="font-mono hover:text-foreground hover:underline"
                  data-testid={`inbox-from-link-${item.conversationId}`}
                >
                  {item.from}
                </Link>
              ) : (
                <span className="font-mono">{item.from}</span>
              )}
            </span>
          </span>
          <span
            className="flex items-center gap-1"
            data-testid="inbox-pending-since"
          >
            <Clock className="h-3 w-3" />
            {timeAgo(item.pendingSince)}
          </span>
        </div>

        <div className="mt-3 flex items-center justify-end">
          <Link
            href={href}
            className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
            data-testid={`inbox-open-${item.conversationId}`}
          >
            Open thread
            <ExternalLink className="h-3 w-3" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
