"use client";

import { Suspense, useMemo } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Inbox, MessagesSquare, RefreshCw } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { ConversationCard } from "@/components/cards/conversation-card";
import { useConversations, useInbox } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { ConversationListFilters } from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";
import Link from "next/link";

const STATUSES: Array<{ value: "" | "active" | "completed"; label: string }> = [
  { value: "", label: "All" },
  { value: "active", label: "Active" },
  { value: "completed", label: "Completed" },
];

function setOrDelete(
  params: URLSearchParams,
  key: string,
  value: string | null,
) {
  if (value === null || value === "") {
    params.delete(key);
  } else {
    params.set(key, value);
  }
}

function ConversationsListContent() {
  const router = useRouter();
  const searchParams = useSearchParams();

  // The filter shape mirrors the CLI's `spring conversation list`
  // options (#452) so a URL is round-trippable with the CLI mental
  // model: ?unit=alpha&agent=ada&status=active&participant=human://savas.
  const filters = useMemo<ConversationListFilters>(() => {
    const status = searchParams.get("status");
    return {
      unit: searchParams.get("unit") ?? undefined,
      agent: searchParams.get("agent") ?? undefined,
      participant: searchParams.get("participant") ?? undefined,
      status:
        status === "active" || status === "completed" ? status : undefined,
    };
  }, [searchParams]);

  const conversationsQuery = useConversations(filters);
  const inboxQuery = useInbox();

  // Live updates: the activity stream invalidates the conversation
  // cache slices we care about (see `queryKeysAffectedBySource`), so
  // we just need the stream open. We pass no filter so unit-, agent-,
  // and human-scoped events all bubble through.
  useActivityStream();

  const conversations = conversationsQuery.data ?? [];
  const inbox = inboxQuery.data ?? [];
  const loading = conversationsQuery.isPending;
  const errorMessage =
    conversationsQuery.error instanceof Error
      ? conversationsQuery.error.message
      : null;

  const updateFilter = (key: keyof ConversationListFilters, value: string) => {
    const params = new URLSearchParams(searchParams.toString());
    setOrDelete(params, key, value || null);
    const qs = params.toString();
    router.replace(qs ? `/conversations?${qs}` : "/conversations");
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <MessagesSquare className="h-5 w-5" /> Conversations
          </h1>
          <p className="text-sm text-muted-foreground">
            Message threads between humans, agents, and units. Mirrors{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring conversation list
            </code>
            .
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => conversationsQuery.refetch()}
          disabled={conversationsQuery.isFetching}
          className="self-start sm:self-auto"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${
              conversationsQuery.isFetching ? "animate-spin" : ""
            }`}
          />
          Refresh
        </Button>
      </div>

      {/* Inbox — conversations awaiting the current human caller. */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Inbox className="h-4 w-4" /> Awaiting you
            {inbox.length > 0 && (
              <Badge variant="warning" className="ml-1">
                {inbox.length}
              </Badge>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent>
          {inboxQuery.isPending ? (
            <Skeleton className="h-12" />
          ) : inbox.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No conversations are waiting on you.
            </p>
          ) : (
            <ul
              className="divide-y divide-border text-sm"
              aria-label="Inbox"
            >
              {inbox.map((item) => (
                <li
                  key={item.conversationId}
                  className="flex items-center gap-3 py-2"
                >
                  <Badge variant="outline" className="shrink-0">
                    {item.from}
                  </Badge>
                  <Link
                    href={`/conversations/${encodeURIComponent(item.conversationId)}`}
                    className="min-w-0 flex-1 truncate hover:underline"
                  >
                    {item.summary || item.conversationId}
                  </Link>
                  <span className="shrink-0 text-xs text-muted-foreground">
                    {timeAgo(item.pendingSince)}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      {/* Filters */}
      <Card>
        <CardContent className="pt-4">
          <div className="flex flex-wrap gap-3">
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Unit</span>
              <Input
                placeholder="e.g. engineering-team"
                defaultValue={filters.unit ?? ""}
                onBlur={(e) => updateFilter("unit", e.target.value.trim())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateFilter("unit", e.currentTarget.value.trim());
                  }
                }}
                className="w-44"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Agent</span>
              <Input
                placeholder="e.g. ada"
                defaultValue={filters.agent ?? ""}
                onBlur={(e) => updateFilter("agent", e.target.value.trim())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateFilter("agent", e.currentTarget.value.trim());
                  }
                }}
                className="w-44"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Participant</span>
              <Input
                placeholder="scheme://path"
                defaultValue={filters.participant ?? ""}
                onBlur={(e) =>
                  updateFilter("participant", e.target.value.trim())
                }
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateFilter(
                      "participant",
                      e.currentTarget.value.trim(),
                    );
                  }
                }}
                className="w-56"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Status</span>
              <select
                value={filters.status ?? ""}
                onChange={(e) => updateFilter("status", e.target.value)}
                className="flex h-9 w-36 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              >
                {STATUSES.map((s) => (
                  <option key={s.value} value={s.value}>
                    {s.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </CardContent>
      </Card>

      {/* Conversation grid */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <MessagesSquare className="h-4 w-4" />
            All conversations
            {conversationsQuery.data && (
              <span className="ml-auto text-sm font-normal text-muted-foreground">
                {conversations.length} shown
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent>
          {errorMessage && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive mb-3">
              {errorMessage}
            </p>
          )}
          {loading ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              <Skeleton className="h-32" />
              <Skeleton className="h-32" />
              <Skeleton className="h-32" />
            </div>
          ) : conversations.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No conversations match these filters.
            </p>
          ) : (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {conversations.map((c) => (
                <ConversationCard
                  key={c.id}
                  conversation={{
                    id: c.id,
                    title: c.summary,
                    participants: c.participants,
                    lastActivityAt: c.lastActivity,
                    status: c.status,
                  }}
                />
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

export default function ConversationsPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <ConversationsListContent />
    </Suspense>
  );
}
