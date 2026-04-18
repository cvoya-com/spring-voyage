"use client";

// /inbox — conversations awaiting a response from the current human
// (#447). This is the portal counterpart of `spring inbox list`
// (PR #469 / PR-C1): same endpoint, same payload, same ordering —
// rendered as entity cards per DESIGN.md § 7.11.
//
// Parity note: `spring inbox list` ships with no filter flags today,
// so the portal surface exposes none either (CONVENTIONS.md § 14 UI /
// CLI feature parity). If the CLI grows filters, the portal gains the
// same knobs in the same PR.

import { AlertTriangle, Inbox as InboxIcon, RefreshCw } from "lucide-react";

import { InboxCard } from "@/components/cards/inbox-card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useInbox } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";

export default function InboxPage() {
  const inboxQuery = useInbox();

  // The activity SSE stream invalidates the inbox cache through
  // `queryKeysAffectedBySource` whenever a `human://`-scoped event
  // lands, so the list picks up new asks (and drops resolved ones)
  // without polling.
  useActivityStream();

  const items = inboxQuery.data ?? [];
  const errorMessage =
    inboxQuery.error instanceof Error ? inboxQuery.error.message : null;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <InboxIcon className="h-5 w-5" /> Inbox
            {items.length > 0 && (
              <Badge variant="warning" data-testid="inbox-count-badge">
                {items.length}
              </Badge>
            )}
          </h1>
          <p className="text-sm text-muted-foreground">
            Conversations awaiting a response from you. Mirrors{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring inbox list
            </code>
            .
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => inboxQuery.refetch()}
          disabled={inboxQuery.isFetching}
          data-testid="inbox-refresh"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${
              inboxQuery.isFetching ? "animate-spin" : ""
            }`}
          />
          Refresh
        </Button>
      </div>

      {errorMessage && (
        <Card
          className="border-destructive/50 bg-destructive/10"
          data-testid="inbox-error"
        >
          <CardContent className="flex items-start gap-2 p-4 text-sm text-destructive">
            <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
            <div>
              <p className="font-medium">Failed to load inbox.</p>
              <p className="text-xs opacity-80">{errorMessage}</p>
            </div>
          </CardContent>
        </Card>
      )}

      {inboxQuery.isPending ? (
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
          data-testid="inbox-loading"
        >
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
        </div>
      ) : items.length === 0 && !errorMessage ? (
        <Card data-testid="inbox-empty">
          <CardContent className="space-y-2 p-8 text-center">
            <InboxIcon className="mx-auto h-10 w-10 text-muted-foreground" />
            <p className="text-sm">Nothing waiting on you.</p>
            <p className="text-xs text-muted-foreground">
              Agents will surface here when they ask for your input.
            </p>
          </CardContent>
        </Card>
      ) : items.length > 0 ? (
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
          data-testid="inbox-list"
        >
          {items.map((item) => (
            <InboxCard key={item.conversationId} item={item} />
          ))}
        </div>
      ) : null}
    </div>
  );
}
