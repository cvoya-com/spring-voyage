"use client";

import { ExternalLink } from "lucide-react";
import Link from "next/link";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  parseThreadSource,
  type ParsedThreadSource,
} from "@/components/thread/role";
import type { InboxItem } from "@/lib/api/types";
import { cn, timeAgo } from "@/lib/utils";

/**
 * Resolve a `scheme://path` sender address to a portal detail route
 * when one exists. Post-v2-IA (DEL-agents #870, DEL-units-id #878) the
 * legacy `/agents/<id>` and `/units/<id>` detail routes are retired;
 * agents and units both surface in the Explorer and deep-link via
 * `/units?node=<id>[&tab=Overview]`. `human://` has no detail page
 * today, so the caller renders the address as plain mono text.
 * Mirrors the cross-link rules in DESIGN.md § 7.14.
 */
function fromHref(parsed: ParsedThreadSource): string | null {
  if (parsed.scheme === "agent") {
    return `/units?node=${encodeURIComponent(parsed.path)}&tab=Overview`;
  }
  if (parsed.scheme === "unit") {
    return `/units?node=${encodeURIComponent(parsed.path)}`;
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
  // Post-`DEL-conversations` (#871): the legacy `/conversations/<id>`
  // detail route is gone. Until a replacement conversation-thread
  // surface lands, the card navigates back to `/inbox` so the click
  // is never a 404. The query string preserves the conversation id so
  // future routing can scroll/highlight the corresponding row.
  const href = `/inbox?thread=${encodeURIComponent(item.threadId)}`;
  const from = parseThreadSource(item.from);
  const fromLink = fromHref(from);
  const title = item.summary?.trim() || item.threadId;

  return (
    <Card
      data-testid={`inbox-card-${item.threadId}`}
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
                data-testid={`inbox-from-link-${item.threadId}`}
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
          data-testid={`inbox-card-link-${item.threadId}`}
          className="mt-2 block rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <h3 className="truncate text-sm font-semibold">{title}</h3>
          <p className="mt-0.5 truncate text-xs text-muted-foreground font-mono">
            {item.threadId}
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
            data-testid={`inbox-open-${item.threadId}`}
          >
            Open thread
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
