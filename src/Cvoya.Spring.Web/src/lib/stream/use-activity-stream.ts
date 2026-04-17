"use client";

import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";

import { queryKeysAffectedBySource } from "@/lib/api/query-keys";
import type { ActivityEvent } from "@/lib/api/types";

/**
 * Max events retained in memory per hook instance. Keeps the feed
 * responsive while avoiding unbounded growth on long-lived sessions.
 */
const MAX_EVENTS = 200;

/**
 * Streams activity events from the same-origin Next.js route handler
 * (`/api/stream/activity`), which proxies the platform's SSE endpoint.
 *
 * On every event the hook:
 *  1. Prepends it to an in-memory list (`events`, newest first, capped
 *     at `MAX_EVENTS`) so existing UI that binds to `events` keeps
 *     working.
 *  2. Invalidates the TanStack Query cache slices that are likely to
 *     have gone stale — see `queryKeysAffectedBySource`. Consumers
 *     that previously relied on `setInterval` for refresh get live
 *     updates instead.
 *
 * Options:
 *  - `enabled` — when `false`, the hook doesn't open the stream. Use
 *    this for views where the stream shouldn't run (e.g. a modal
 *    that's been dismissed).
 *  - `filter` — client-side predicate. Events that fail the predicate
 *    still reach the cache-invalidation step ONLY if you want them
 *    to. By default, filtered-out events are dropped for both
 *    `events` AND cache invalidation so `useActivityStream({ filter:
 *    e => e.source.path === unitId })` gives you a unit-scoped view.
 *
 * @returns { events, connected } — `events` is newest-first, bounded;
 *   `connected` flips true on SSE open, false on error/close.
 */
export interface UseActivityStreamOptions {
  enabled?: boolean;
  /**
   * Optional client-side filter. Returning `false` drops the event
   * from BOTH the local `events` array AND cache invalidation. When
   * omitted, every event is kept.
   */
  filter?: (event: ActivityEvent) => boolean;
}

export function useActivityStream(
  options: UseActivityStreamOptions | boolean = {},
) {
  const resolved: UseActivityStreamOptions =
    typeof options === "boolean" ? { enabled: options } : options;
  const enabled = resolved.enabled ?? true;
  const filter = resolved.filter;

  const [events, setEvents] = useState<ActivityEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const eventsRef = useRef<ActivityEvent[]>([]);
  const queryClient = useQueryClient();
  // Keep the filter in a ref so changing it doesn't tear down the
  // EventSource. The predicate is called on every event delivered.
  // The ref is updated from an effect (not during render) per the
  // react-hooks/refs rule.
  const filterRef = useRef<
    ((event: ActivityEvent) => boolean) | undefined
  >(filter);
  useEffect(() => {
    filterRef.current = filter;
  }, [filter]);

  useEffect(() => {
    if (!enabled) return;

    // Use same-origin route handler. Relative URLs keep cookies on the
    // request without CORS preflights.
    const es = new EventSource(`/api/stream/activity`);

    es.onopen = () => setConnected(true);

    es.onmessage = (e) => {
      try {
        const event = JSON.parse(e.data) as ActivityEvent;
        if (filterRef.current && !filterRef.current(event)) return;

        eventsRef.current = [event, ...eventsRef.current].slice(0, MAX_EVENTS);
        setEvents([...eventsRef.current]);

        // Invalidate the TanStack Query slices affected by this event
        // so any mounted `useQuery` re-fetches the authoritative data.
        const affected = queryKeysAffectedBySource(event.source);
        for (const key of affected) {
          queryClient.invalidateQueries({ queryKey: key });
        }
      } catch {
        // Ignore malformed messages — the stream stays open.
      }
    };

    es.onerror = () => {
      // EventSource auto-reconnects; flipping `connected` to false lets
      // the UI show a "reconnecting..." indicator.
      setConnected(false);
    };

    return () => {
      es.close();
      setConnected(false);
    };
  }, [enabled, queryClient]);

  return { events, connected };
}
