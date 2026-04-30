"use client";

// Shared timeline + inline-composer view for the Unit and Agent
// Messages tabs (#1459 / #1460, fixed #1472). The two tabs both render
// all threads involving the hosting unit/agent, with the most-recently-
// active one shown inline.
//
// #1472 fix: the `participant` filter was gating on `address` from the
// user-profile response, but `UserProfileResponse` does not include an
// `address` field on the wire (it carries only `userId` + `displayName`).
// As a result the query was never enabled and the tab was always empty.
// The fix drops the `participant` filter — the tab is already scoped to
// the agent/unit, so showing all threads involving that node is the
// correct semantic for v0.1. The `useCurrentUser` hook is retained for
// display purposes but no longer gates the thread query.
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
//
// #1473 fix: after a successful send the sent text is optimistically
// prepended to the timeline as a synthetic event so the user sees their
// message immediately rather than waiting for the next SSE/refetch cycle.

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, Send } from "lucide-react";

import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { useThread, useThreads } from "@/lib/api/queries";
import type { ThreadDetail, ThreadSummary } from "@/lib/api/types";

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
  // #1472 fix: filter only by the hosting node — drop the `participant`
  // filter because `UserProfileResponse.address` is not on the wire.
  // The tab is scoped to this agent/unit anyway, so all threads involving
  // it is the correct v0.1 semantic.
  const threadsQuery = useThreads(
    targetScheme === "unit"
      ? { unit: targetPath }
      : { agent: targetPath },
  );

  const canonical = useMemo(
    () => pickCanonicalThread(threadsQuery.data ?? []),
    [threadsQuery.data],
  );

  const threadDetailQuery = useThread(canonical?.id ?? "", {
    enabled: Boolean(canonical?.id),
  });

  const isInitialLoading =
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
 *
 * #1473 fix: on successful send, the sent text is optimistically
 * injected into the thread's cached event list so the user sees their
 * message immediately (before the server SSE or refetch cycle delivers
 * the canonical event). The optimistic event is replaced by the server's
 * authoritative version once `invalidateQueries` triggers a refetch.
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

  const send = useMutation<
    Awaited<ReturnType<typeof api.sendMessage>>,
    Error,
    { trimmed: string }
  >({
    mutationFn: async ({ trimmed }) => {
      if (threadId) {
        return api.sendThreadMessage(threadId, {
          to: { scheme: targetScheme, path: targetPath },
          text: trimmed,
        }) as Promise<Awaited<ReturnType<typeof api.sendMessage>>>;
      }
      return api.sendMessage({
        to: { scheme: targetScheme, path: targetPath },
        type: "Domain",
        threadId: null,
        payload: trimmed,
      });
    },
    onSuccess: (_data, { trimmed }) => {
      setText("");

      // #1473: optimistically inject the just-sent message into the
      // thread detail cache so the timeline renders immediately.
      // We inject into the canonical thread's detail cache when one
      // exists; for new threads (threadId null) the next refetch will
      // surface the created thread with the event already present.
      if (threadId) {
        const key = queryKeys.threads.detail(threadId);
        const prev = queryClient.getQueryData<ThreadDetail | null>(key);
        if (prev) {
          const syntheticEvent = {
            id: `optimistic-${Date.now()}`,
            eventType: "MessageReceived",
            source: "human://me",
            timestamp: new Date().toISOString(),
            severity: "Info",
            summary: trimmed,
            body: trimmed,
          };
          queryClient.setQueryData<ThreadDetail>(key, {
            ...prev,
            events: [...(prev.events ?? []), syntheticEvent],
          });
        }
      }

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
    const trimmed = text.trim();
    if (send.isPending || !trimmed) return;
    send.mutate({ trimmed });
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
