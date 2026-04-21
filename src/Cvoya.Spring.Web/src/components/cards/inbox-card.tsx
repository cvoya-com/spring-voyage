"use client";

import { ExternalLink } from "lucide-react";
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
 * the address as plain mono text. Mirrors the cross-link rules in
 * DESIGN.md § 7.14.
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
 * a response from the current human. Reskinned for the v2 design
 * system (plan §7 / CARD-inbox-refresh): the `from://` address is the
 * card's lead line in Geist mono, the status pill sits top-right, and
 * a timestamp pill sits in the footer. The summary (one-line excerpt)
 * is the primary overlay link target. Data shape matches
 * `GET /api/v1/inbox`.
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
        "relative h-full transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2",
        className,
      )}
    >
      <CardContent className="p-4">
        {/* Mono `from://` identity line — plan §7 inbox pattern. Sits
            above the primary overlay link in DOM order so its own
            anchor (agent/unit detail) does not nest inside the card
            overlay `<a>` (which would be invalid HTML). The status
            pill sits alongside so the reader gets the "from + state"
            pair up top. Interactive descendants are promoted via
            `relative z-[1]` so they click through the overlay. */}
        <div className="flex items-start justify-between gap-3">
          <div
            className="relative z-[1] min-w-0 flex-1 truncate text-xs font-mono text-muted-foreground"
            data-testid="inbox-from"
          >
            {fromLink ? (
              <Link
                href={fromLink}
                className="hover:text-foreground hover:underline"
                data-testid={`inbox-from-link-${item.conversationId}`}
              >
                {item.from}
              </Link>
            ) : (
              <span>{item.from}</span>
            )}
          </div>
          <Badge
            variant="warning"
            data-testid="inbox-status-badge"
            className="shrink-0"
          >
            Awaiting you
          </Badge>
        </div>

        {/* Primary overlay link (#593). The `::after` pseudo covers
            the whole card; the `from://` link above and the
            "Open thread" link below are `relative z-[1]` to stay
            clickable. Tab focus lands on this link; Enter activates
            it. */}
        <Link
          href={href}
          aria-label={`Open conversation ${title}`}
          data-testid={`inbox-card-link-${item.conversationId}`}
          className="mt-2 block rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <h3 className="truncate text-sm font-semibold">{title}</h3>
          <p className="mt-0.5 truncate text-xs text-muted-foreground font-mono">
            {item.conversationId}
          </p>
        </Link>

        <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs">
          <Badge
            variant="outline"
            className="font-mono"
            data-testid="inbox-pending-since"
          >
            {timeAgo(item.pendingSince)}
          </Badge>
          <Link
            href={href}
            className="relative z-[1] inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
            data-testid={`inbox-open-${item.conversationId}`}
          >
            Open thread
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
