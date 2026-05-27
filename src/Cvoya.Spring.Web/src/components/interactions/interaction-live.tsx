"use client";

// Live-mode side-car (#2867). Wires `useInteractionsStream` to the
// page's snapshot / pulse state and renders the throttle indicator
// when the server drops events. Kept as a sibling component so the
// page can wire pulses → graph and node/edge inserts → snapshot
// mirror without owning the EventSource lifecycle directly.

import { AlertTriangle } from "lucide-react";
import { useCallback } from "react";

import { useInteractionsStream } from "@/lib/stream/use-interactions-stream";
import type {
  InteractionsEdgeAddedFrame,
  InteractionsNodeAddedFrame,
  InteractionsPulseFrame,
  InteractionsThrottledFrame,
} from "@/lib/api/types";

interface InteractionLiveProps {
  enabled: boolean;
  unit?: string;
  neighbours?: 0 | 1 | 2;
  onPulse: (frame: InteractionsPulseFrame) => void;
  onNodeAdded: (frame: InteractionsNodeAddedFrame) => void;
  onEdgeAdded: (frame: InteractionsEdgeAddedFrame) => void;
  /** Rolling count of dropped events shown next to the live badge. */
  droppedCount: number;
  onThrottled: (frame: InteractionsThrottledFrame) => void;
}

export function InteractionLive({
  enabled,
  unit,
  neighbours,
  onPulse,
  onNodeAdded,
  onEdgeAdded,
  droppedCount,
  onThrottled,
}: InteractionLiveProps) {
  const handlePulse = useCallback(
    (frame: InteractionsPulseFrame) => onPulse(frame),
    [onPulse],
  );
  const handleNodeAdded = useCallback(
    (frame: InteractionsNodeAddedFrame) => onNodeAdded(frame),
    [onNodeAdded],
  );
  const handleEdgeAdded = useCallback(
    (frame: InteractionsEdgeAddedFrame) => onEdgeAdded(frame),
    [onEdgeAdded],
  );
  const handleThrottled = useCallback(
    (frame: InteractionsThrottledFrame) => onThrottled(frame),
    [onThrottled],
  );

  const { connected } = useInteractionsStream({
    enabled,
    unit,
    neighbours,
    onPulse: handlePulse,
    onNodeAdded: handleNodeAdded,
    onEdgeAdded: handleEdgeAdded,
    onThrottled: handleThrottled,
  });

  if (!enabled) return null;

  return (
    <div
      data-testid="interaction-live-status"
      data-connected={connected || undefined}
      className="flex items-center gap-2 text-xs"
    >
      <span
        aria-hidden="true"
        className={
          connected
            ? "inline-block h-2 w-2 rounded-full bg-success"
            : "inline-block h-2 w-2 rounded-full bg-muted-foreground"
        }
      />
      <span className="text-muted-foreground">
        {connected ? "Live" : "Connecting…"}
      </span>
      {droppedCount > 0 ? (
        <span
          className="inline-flex items-center gap-1 text-warning"
          data-testid="interaction-live-throttle-indicator"
        >
          <AlertTriangle className="h-3 w-3" aria-hidden="true" />
          +{droppedCount} more dropped
        </span>
      ) : null}
    </div>
  );
}
