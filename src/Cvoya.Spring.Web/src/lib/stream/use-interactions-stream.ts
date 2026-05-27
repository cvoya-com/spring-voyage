"use client";

// Interactions SSE stream hook (#2867).
//
// Opens `GET /api/stream/interactions?unit=<id>&neighbours=<n>` which
// proxies the platform's
// `GET /api/v1/tenant/observation/interactions/stream` endpoint. The
// hook surfaces four kinds of events for the page:
//
//   - `pulse`       — animate a dot along an edge; coalesced upstream
//                     so one frame per edge per ~250ms window.
//   - `node-added`  — new participant entered the live window; the
//                     page splices it into the local graph mirror.
//   - `edge-added`  — new sender→receiver pair entered the live window.
//   - `throttled`   — server-side rate-cap fired; page renders a small
//                     "events dropped" indicator in the header.
//
// EventSource honours `Last-Event-ID` automatically on reconnect, and
// the backend's resume contract is "we ship monotonic ids; on reconnect
// we won't re-emit anything you already saw". The hook holds onto the
// latest frames in refs so a re-render during the page's
// `requestAnimationFrame` pulses doesn't churn React state on every
// event.

import { useCallback, useEffect, useRef, useState } from "react";

import type {
  InteractionsEdgeAddedFrame,
  InteractionsNodeAddedFrame,
  InteractionsPulseFrame,
  InteractionsThrottledFrame,
} from "@/lib/api/types";

export interface UseInteractionsStreamOptions {
  /** Scope unit id (no scheme prefix). When unset the server streams the whole tenant. */
  unit?: string;
  /** Hop depth around the unit scope (0 / 1 / 2). Ignored when `unit` is unset. */
  neighbours?: 0 | 1 | 2;
  /** Per-edge coalesce window, ms. Defaults to the server's 250ms. */
  coalesceMs?: number;
  /** Per-stream rate ceiling, events / second. Defaults to the server's 50. */
  maxRate?: number;
  /** When false, the EventSource stays closed. Default: true. */
  enabled?: boolean;
  /** Pulse-frame callback. Re-fires on every coalesced edge pulse. */
  onPulse?: (frame: InteractionsPulseFrame) => void;
  /** Fires once per previously-unseen node (before the first pulse that references it). */
  onNodeAdded?: (frame: InteractionsNodeAddedFrame) => void;
  /** Fires once per previously-unseen edge (before the first pulse that references it). */
  onEdgeAdded?: (frame: InteractionsEdgeAddedFrame) => void;
  /** Fires when the per-subscription rate cap drops events. */
  onThrottled?: (frame: InteractionsThrottledFrame) => void;
}

export interface UseInteractionsStreamResult {
  /** True once the EventSource has reported `open`. Flips to false on error. */
  connected: boolean;
}

/**
 * Subscribes to the interactions SSE stream and dispatches typed
 * callbacks per frame kind. Returns a `connected` flag for the page's
 * live-mode indicator. The callbacks are captured by ref so the
 * EventSource doesn't tear down on every render.
 */
export function useInteractionsStream(
  opts: UseInteractionsStreamOptions,
): UseInteractionsStreamResult {
  const {
    unit,
    neighbours,
    coalesceMs,
    maxRate,
    enabled = true,
    onPulse,
    onNodeAdded,
    onEdgeAdded,
    onThrottled,
  } = opts;

  const [connected, setConnected] = useState(false);

  // Hold the latest callbacks in refs so the EventSource doesn't reopen
  // every time the parent re-renders with a new closure. The refs are
  // updated inside a useEffect (writing during render would trip the
  // react-hooks/refs lint rule).
  const onPulseRef = useRef(onPulse);
  const onNodeAddedRef = useRef(onNodeAdded);
  const onEdgeAddedRef = useRef(onEdgeAdded);
  const onThrottledRef = useRef(onThrottled);
  useEffect(() => {
    onPulseRef.current = onPulse;
    onNodeAddedRef.current = onNodeAdded;
    onEdgeAddedRef.current = onEdgeAdded;
    onThrottledRef.current = onThrottled;
  }, [onPulse, onNodeAdded, onEdgeAdded, onThrottled]);

  const parse = useCallback(<T,>(raw: string): T | null => {
    try {
      return JSON.parse(raw) as T;
    } catch {
      // Malformed SSE payload — drop. The server only emits well-formed
      // JSON; this branch protects against a future contract drift.
      return null;
    }
  }, []);

  useEffect(() => {
    if (!enabled) return;

    const search = new URLSearchParams();
    if (unit) search.set("unit", unit);
    if (neighbours !== undefined) search.set("neighbours", String(neighbours));
    if (coalesceMs !== undefined) search.set("coalesceMs", String(coalesceMs));
    if (maxRate !== undefined) search.set("maxRate", String(maxRate));
    const qs = search.toString();
    const url = `/api/stream/interactions${qs ? `?${qs}` : ""}`;

    const es = new EventSource(url);
    es.onopen = () => setConnected(true);
    es.onerror = () => setConnected(false);

    es.addEventListener("pulse", (evt) => {
      const frame = parse<InteractionsPulseFrame>(
        (evt as MessageEvent).data,
      );
      if (frame) onPulseRef.current?.(frame);
    });
    es.addEventListener("node-added", (evt) => {
      const frame = parse<InteractionsNodeAddedFrame>(
        (evt as MessageEvent).data,
      );
      if (frame) onNodeAddedRef.current?.(frame);
    });
    es.addEventListener("edge-added", (evt) => {
      const frame = parse<InteractionsEdgeAddedFrame>(
        (evt as MessageEvent).data,
      );
      if (frame) onEdgeAddedRef.current?.(frame);
    });
    es.addEventListener("throttled", (evt) => {
      const frame = parse<InteractionsThrottledFrame>(
        (evt as MessageEvent).data,
      );
      if (frame) onThrottledRef.current?.(frame);
    });

    return () => {
      es.close();
      setConnected(false);
    };
  }, [enabled, unit, neighbours, coalesceMs, maxRate, parse]);

  return { connected };
}
