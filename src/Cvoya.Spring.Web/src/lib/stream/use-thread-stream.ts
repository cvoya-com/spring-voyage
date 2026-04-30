"use client";

// Thread-scoped SSE stream hook (E2.5, #1417).
//
// Opens `GET /api/stream/activity?thread=<threadId>` which proxies the
// platform's `GET /api/v1/tenant/activity/stream?thread=<id>` — the
// server-side thread filter shipped in PR #1421. This keeps network
// traffic to events for the specified thread only, rather than filtering
// the full tenant activity stream on the client.
//
// On each event the hook:
//  1. Appends it to a local `events` array (newest-last, capped at 500).
//  2. Invalidates TanStack Query cache slices for this thread so the
//     `useThread` query refetches the authoritative state.
//
// Usage: replaces `useActivityStream` for the engagement detail view,
// where only one thread is in view at a time.

import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";

import { queryKeys } from "@/lib/api/query-keys";
import type { ActivityEvent } from "@/lib/api/types";

const MAX_EVENTS = 500;

interface UseThreadStreamResult {
  events: ActivityEvent[];
  connected: boolean;
}

/**
 * Opens a thread-scoped SSE stream. The server filters events to the
 * specified thread so the browser only receives relevant activity.
 *
 * When `threadId` is empty / falsy the stream stays closed.
 */
export function useThreadStream(threadId: string): UseThreadStreamResult {
  const [events, setEvents] = useState<ActivityEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const eventsRef = useRef<ActivityEvent[]>([]);
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!threadId) return;

    const url = `/api/stream/activity?thread=${encodeURIComponent(threadId)}`;
    const es = new EventSource(url);

    es.onopen = () => setConnected(true);

    es.onmessage = (e) => {
      try {
        const event = JSON.parse(e.data) as ActivityEvent;

        // Belt-and-braces: drop events that don't correlate to this thread.
        if (event.correlationId && event.correlationId !== threadId) return;

        eventsRef.current = [...eventsRef.current, event].slice(-MAX_EVENTS);
        setEvents([...eventsRef.current]);

        // Invalidate the thread detail so the ThreadEventRow list refreshes.
        queryClient.invalidateQueries({
          queryKey: queryKeys.threads.detail(threadId),
        });
        // Invalidate the thread list so the global badge and list update.
        queryClient.invalidateQueries({ queryKey: queryKeys.threads.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.threads.inbox() });
      } catch {
        // Ignore malformed SSE messages.
      }
    };

    es.onerror = () => setConnected(false);

    return () => {
      es.close();
      setConnected(false);
    };
  }, [threadId, queryClient]);

  return { events, connected };
}
