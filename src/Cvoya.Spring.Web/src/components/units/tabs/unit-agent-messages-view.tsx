"use client";

// Shared timeline + inline-composer view for the Unit and Agent
// Messages tabs (#1459 / #1460). The two tabs both render the
// {current human, unit/agent} 1:1 engagement: a single timeline of all
// events between the pair (messages + tool calls + lifecycle events)
// plus a persistent inline entry box at the bottom.
//
// There is no master/detail list — the engagement is a single thread
// per pair. If multiple matching threads exist (legacy data), the
// most recently active one wins; older threads stay visible from the
// engagement portal's `/engagement/mine?unit=<id>` slice.
//
// Sending a message:
//   • If the 1:1 thread already exists, the composer appends to it via
//     `POST /api/v1/threads/{id}/messages`.
//   • If no thread exists yet, the composer posts to
//     `POST /api/v1/messages` with `threadId: null`; the server's
//     auto-gen assigns a fresh id and the next refetch picks it up.

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, Send } from "lucide-react";

import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { useCurrentUser, useThread, useThreads } from "@/lib/api/queries";
import type { ThreadSummary } from "@/lib/api/types";

import { ThreadEventRow } from "@/components/thread/thread-event-row";

interface UnitAgentMessagesViewProps {
  /** Hosting node kind — drives the threads filter and routing target. */
  targetScheme: "unit" | "agent";
  /** Hosting node id (slug). */
  targetPath: string;
  /** Display name for empty-state copy. */
  targetName: string;
  /** Test-id root for the empty state + container, e.g. `tab-unit-messages`. */
  rootTestId: string;
}

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

export function UnitAgentMessagesView({
  targetScheme,
  targetPath,
  targetName,
  rootTestId,
}: UnitAgentMessagesViewProps) {
  const userQuery = useCurrentUser();
  const humanAddress =
    (userQuery.data as unknown as { address?: string } | null)?.address ??
    undefined;

  // Filter threads by both the hosting node and the current human so
  // we land on the {human, unit/agent} 1:1 engagement (and not on
  // A2A-only threads involving the same node).
  const threadsQuery = useThreads(
    targetScheme === "unit"
      ? { unit: targetPath, participant: humanAddress }
      : { agent: targetPath, participant: humanAddress },
    { enabled: Boolean(humanAddress) },
  );

  const canonical = useMemo(
    () => pickCanonicalThread(threadsQuery.data ?? []),
    [threadsQuery.data],
  );

  const threadDetailQuery = useThread(canonical?.id ?? "", {
    enabled: Boolean(canonical?.id),
  });

  const isInitialLoading =
    userQuery.isPending ||
    threadsQuery.isLoading ||
    (Boolean(canonical?.id) && threadDetailQuery.isPending);

  const threadDetail = threadDetailQuery.data ?? null;
  const events = threadDetail?.events ?? [];

  if (isInitialLoading) {
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
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid={`${rootTestId}-error`}
      >
        Couldn&apos;t load conversation:{" "}
        {threadsQuery.error instanceof Error
          ? threadsQuery.error.message
          : String(threadsQuery.error)}
      </p>
    );
  }

  return (
    <div
      className="flex min-h-[28rem] flex-col gap-3"
      data-testid={rootTestId}
    >
      <div
        className="flex-1 space-y-3 overflow-auto rounded-md border border-border bg-background p-3"
        data-testid={`${rootTestId}-timeline`}
      >
        {events.length === 0 ? (
          <p
            className="text-sm text-muted-foreground"
            data-testid={`${rootTestId}-empty`}
          >
            No conversation with{" "}
            <span className="font-medium">{targetName}</span> yet. Send the
            first message below to start one.
          </p>
        ) : (
          events.map((event) => (
            <ThreadEventRow key={event.id} event={event} />
          ))
        )}
      </div>

      <InlineMessageComposer
        targetScheme={targetScheme}
        targetPath={targetPath}
        threadId={canonical?.id ?? null}
        rootTestId={rootTestId}
      />
    </div>
  );
}

interface InlineMessageComposerProps {
  targetScheme: "unit" | "agent";
  targetPath: string;
  /** Existing 1:1 thread id, or `null` to create a new one on send. */
  threadId: string | null;
  rootTestId: string;
}

/**
 * Persistent inline composer for the Messages tab (#1460). The
 * recipient is fixed (the hosting unit/agent), so the form is just
 * "type and send". Cmd/Ctrl+Enter submits.
 */
function InlineMessageComposer({
  targetScheme,
  targetPath,
  threadId,
  rootTestId,
}: InlineMessageComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const [text, setText] = useState("");

  const send = useMutation({
    mutationFn: async () => {
      const trimmed = text.trim();
      if (!trimmed) {
        throw new Error("Message text is required.");
      }
      if (threadId) {
        return api.sendThreadMessage(threadId, {
          to: { scheme: targetScheme, path: targetPath },
          text: trimmed,
        });
      }
      return api.sendMessage({
        to: { scheme: targetScheme, path: targetPath },
        type: "Domain",
        threadId: null,
        payload: trimmed,
      });
    },
    onSuccess: () => {
      setText("");
      // Refetch thread list so the freshly-created (or freshly-touched)
      // thread surfaces, and the detail timeline picks up the new event.
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
      if (threadId) {
        queryClient.invalidateQueries({
          queryKey: queryKeys.threads.detail(threadId),
        });
      }
    },
    onError: (err) => {
      toast({
        title: "Failed to send message",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const submit = () => {
    if (send.isPending || !text.trim()) return;
    send.mutate();
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    submit();
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
      e.preventDefault();
      submit();
    }
  };

  return (
    <form
      onSubmit={handleSubmit}
      className="space-y-2 rounded-md border border-border bg-muted/20 p-3"
      aria-label="Send a message"
      data-testid={`${rootTestId}-composer`}
    >
      <textarea
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={`Message ${targetScheme}://${targetPath}…  (⌘/Ctrl+Enter to send)`}
        rows={3}
        className="flex min-h-[64px] w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        aria-label="Message text"
        data-testid={`${rootTestId}-composer-input`}
        disabled={send.isPending}
      />
      <div className="flex items-center justify-end gap-2">
        <span className="text-xs text-muted-foreground">
          {send.isPending ? "Sending…" : "⌘/Ctrl+Enter to send"}
        </span>
        <Button
          type="submit"
          size="sm"
          disabled={send.isPending || !text.trim()}
          data-testid={`${rootTestId}-composer-send`}
        >
          {send.isPending ? (
            <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
          ) : (
            <Send className="mr-1 h-4 w-4" aria-hidden="true" />
          )}
          Send
        </Button>
      </div>
    </form>
  );
}
