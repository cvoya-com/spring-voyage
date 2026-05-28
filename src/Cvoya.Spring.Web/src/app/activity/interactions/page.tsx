"use client";

// Tenant-wide interactions visualisation (#2867).
//
// One snapshot fetch from `/api/v1/tenant/observation/interactions`
// drives the graph, the matrix, and the timeline. The SSE stream
// (`/api/stream/interactions`, proxied to
// `/api/v1/tenant/observation/interactions/stream`) layers pulse / node /
// edge / throttled frames on top in live mode — node and edge additions
// splice into a local React mirror of the snapshot so the canvas grows
// without re-fetching.
//
// URL state mirrors every filter (see ./url-state) so back/forward
// reflows the whole canvas and a deep link reproduces the operator's
// view exactly.

import { Network } from "lucide-react";
import { useSearchParams } from "next/navigation";
import {
  Suspense,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useInteractionsHistory,
  useInteractionsSnapshot,
} from "@/lib/api/queries";
import type {
  InteractionsEdgeResponse,
  InteractionsGraphResponse,
  InteractionsNodeResponse,
  InteractionsTimelineBucketResponse,
} from "@/lib/api/types";

import { InteractionDetail, type InteractionDetailSubject } from "@/components/interactions/interaction-detail";
import { InteractionFilters } from "@/components/interactions/interaction-filters";
import { InteractionGraph, type LivePulse } from "@/components/interactions/interaction-graph";
import { InteractionLive } from "@/components/interactions/interaction-live";
import { InteractionMatrix } from "@/components/interactions/interaction-matrix";
import {
  InteractionRewind,
  type RewindPulseFrame,
} from "@/components/interactions/interaction-rewind";
import { InteractionTimeline } from "@/components/interactions/interaction-timeline";
import {
  BUCKET_SIZE_MS,
  readUrlState,
  resolveWindow,
  toSnapshotFilters,
  writeUrlState,
  type InteractionsUrlState,
} from "@/components/interactions/url-state";

const MAX_LIVE_PULSES = 200;

const TIMELINE_KINDS = ["agent", "unit", "human", "connector"] as const;

/**
 * Bucket the rewind path's pulse list into the timeline shape the
 * `<InteractionTimeline>` already consumes from the snapshot endpoint.
 * Sender kind is looked up on the per-pulse `fromId` against the
 * accompanying node list — the pulse wire shape itself doesn't carry
 * a sender scheme (only the recipient `channel`).
 *
 * Two breakdowns are emitted per bucket:
 *
 *   - `byKind` — per-sender-kind tally (one per pulse, by sender's
 *     kind). Same shape the snapshot endpoint produces.
 *   - `byActor` — per-actor "touch" tally. Each pulse contributes one
 *     tally to the sender's column and one to the recipient's, so a
 *     single message in the bucket adds 1 to two actors. Drives the
 *     portal's per-actor timeline lines.
 *
 * The bucket boundary aligns to multiples of {@link BUCKET_SIZE_MS} from
 * the UTC epoch so the client-side rollup matches the bucket grid the
 * backend uses.
 *
 * Empty input → empty list; the timeline component renders its own
 * "no data" placeholder for that case.
 */
function buildClientTimeline(
  nodes: ReadonlyArray<{ id: string; kind: string }>,
  pulses: ReadonlyArray<{ fromId: string; toId: string; timestamp: string }>,
  bucket: keyof typeof BUCKET_SIZE_MS,
): InteractionsTimelineBucketResponse[] {
  if (pulses.length === 0) return [];
  const size = BUCKET_SIZE_MS[bucket];
  const kindById = new Map<string, string>();
  for (const n of nodes) kindById.set(n.id, n.kind);

  type Entry = {
    sent: number;
    byKind: Record<string, number>;
    byActor: Record<string, number>;
  };
  const buckets = new Map<number, Entry>();
  for (const p of pulses) {
    const ts = new Date(p.timestamp).getTime();
    if (!Number.isFinite(ts)) continue;
    const start = Math.floor(ts / size) * size;
    let entry = buckets.get(start);
    if (!entry) {
      entry = {
        sent: 0,
        byKind: Object.fromEntries(TIMELINE_KINDS.map((k) => [k, 0])),
        byActor: {},
      };
      buckets.set(start, entry);
    }
    const senderKind = kindById.get(p.fromId) ?? "agent";
    entry.sent += 1;
    entry.byKind[senderKind] = (entry.byKind[senderKind] ?? 0) + 1;
    entry.byActor[p.fromId] = (entry.byActor[p.fromId] ?? 0) + 1;
    entry.byActor[p.toId] = (entry.byActor[p.toId] ?? 0) + 1;
  }

  // Zero-fill the gaps between the earliest and latest observed bucket
  // so the area chart paints contiguous bars (no visual gap looks like
  // missing data when it really is "no traffic in this minute").
  const sortedStarts = [...buckets.keys()].sort((a, b) => a - b);
  const first = sortedStarts[0];
  const last = sortedStarts[sortedStarts.length - 1];
  const out: InteractionsTimelineBucketResponse[] = [];
  for (let t = first; t <= last; t += size) {
    const e = buckets.get(t);
    out.push({
      bucket: new Date(t).toISOString(),
      sent: e?.sent ?? 0,
      byKind: e?.byKind ?? Object.fromEntries(TIMELINE_KINDS.map((k) => [k, 0])),
      byActor: e?.byActor ?? {},
    });
  }
  return out;
}

function InteractionsPageContent() {
  const searchParams = useSearchParams();
  const urlString = searchParams.toString();
  const urlState = useMemo<InteractionsUrlState>(
    () => readUrlState(new URLSearchParams(urlString)),
    [urlString],
  );
  const [state, setState] = useState<InteractionsUrlState>(urlState);

  // Sync state from URL when it changes externally (back/forward, deep link).
  useEffect(() => {
    setState(urlState);
  }, [urlState]);

  const applyState = useCallback(
    (next: InteractionsUrlState) => {
      const search = writeUrlState(next);
      const url = `/activity/interactions${search ? `?${search}` : ""}`;
      // Update local state and URL in lockstep. We use `history.replaceState`
      // rather than `router.replace` because the latter intermittently
      // dropped the URL update under Next.js 16 — local state would flip
      // (snapshot / SSE / pulse handlers all picked up the new mode) while
      // the URL bar stayed on the pre-click value, breaking deep links and
      // the activity-interactions e2e suite (test #3 in the rewind group).
      // The native History API is the officially-documented escape hatch
      // for in-page URL updates — Next.js's router observes pushState /
      // replaceState so `useSearchParams()` re-runs without a server
      // round-trip or RSC refetch.
      // https://nextjs.org/docs/app/getting-started/linking-and-navigating#native-history-api
      setState(next);
      if (typeof window !== "undefined") {
        window.history.replaceState(null, "", url);
      }
    },
    [],
  );

  const filters = useMemo(() => toSnapshotFilters(state), [state]);
  // Pause the snapshot query while rewind is active — rewind drives the
  // canvas from the history endpoint instead. The cache stays warm so
  // toggling rewind off resumes the live snapshot without a flash.
  const snapshotQuery = useInteractionsSnapshot(filters, {
    enabled: !state.rewind,
  });

  // History query — only runs when rewind is on. Mirrors the snapshot
  // filters so a deep link reproduces the operator's chosen window
  // exactly. `maxPulses` is fixed at 5000 here per the spec; raising it
  // would require a control surface we don't have in v0.1.
  const historyFilters = useMemo(
    () => ({
      since: filters.since,
      until: filters.until,
      unit: filters.unit,
      participant: filters.participant,
      neighbours: filters.neighbours,
      maxPulses: 5000,
    }),
    [filters],
  );
  const historyQuery = useInteractionsHistory(historyFilters, {
    enabled: state.rewind,
  });

  // Local mirror of the snapshot that SSE pulses splice into. We deep-
  // copy the immutable response into mutable arrays so node-added /
  // edge-added frames can grow the graph live without refetching.
  const [liveSnapshot, setLiveSnapshot] = useState<InteractionsGraphResponse | null>(
    null,
  );
  useEffect(() => {
    if (snapshotQuery.data) {
      setLiveSnapshot({
        ...snapshotQuery.data,
        nodes: snapshotQuery.data.nodes ?? [],
        edges: snapshotQuery.data.edges ?? [],
        timeline: snapshotQuery.data.timeline ?? [],
        truncated: snapshotQuery.data.truncated ?? null,
      });
    }
  }, [snapshotQuery.data]);

  // Rewind: materialise the history response's nodes / edges / pulses
  // into the same local snapshot mirror the graph + matrix consume.
  // The history endpoint does not run the timeline rollup itself — we
  // bucket the pulse list client-side so the timeline tracks the
  // operator's current `[since, until]` (the snapshot's stale timeline
  // would otherwise pin the X-axis to whatever window was last loaded
  // in snapshot mode).
  useEffect(() => {
    if (!state.rewind || !historyQuery.data) return;
    const timeline = buildClientTimeline(
      historyQuery.data.nodes ?? [],
      historyQuery.data.pulses ?? [],
      state.bucket,
    );
    setLiveSnapshot(() => ({
      nodes: historyQuery.data.nodes ?? [],
      edges: historyQuery.data.edges ?? [],
      timeline,
      truncated: historyQuery.data.truncated
        ? {
            total: historyQuery.data.truncated.total,
            kept: historyQuery.data.truncated.kept,
          }
        : null,
    }));
  }, [state.rewind, state.bucket, historyQuery.data]);

  const [livePulses, setLivePulses] = useState<LivePulse[]>([]);
  const [droppedCount, setDroppedCount] = useState(0);

  // Detail popover state. Stays open across re-renders so a snapshot
  // refetch doesn't close the operator's selected edge.
  const [detailSubject, setDetailSubject] = useState<
    InteractionDetailSubject | null
  >(null);

  const onSelectEdge = useCallback(
    (subject: InteractionDetailSubject) => setDetailSubject(subject),
    [],
  );
  const closeDetail = useCallback(() => setDetailSubject(null), []);

  // Live-mode pulse handler. The graph itself dedupes per-edge in-flight
  // pulses; we simply push every frame onto the pulse queue. The graph
  // bumps the badge instead of starting a second animation when it sees
  // a duplicate edge key with an active pulse already in flight.
  const handlePulse = useCallback(
    (frame: {
      messageIds: string[];
      fromId: string;
      toId: string;
      count: number;
    }) => {
      setLivePulses((prev) => {
        const next = [
          ...prev,
          {
            id: frame.messageIds[0] ?? `${frame.fromId}->${frame.toId}-${Date.now()}`,
            fromId: frame.fromId,
            toId: frame.toId,
            count: frame.count,
          },
        ];
        return next.length > MAX_LIVE_PULSES
          ? next.slice(next.length - MAX_LIVE_PULSES)
          : next;
      });
    },
    [],
  );

  const handleNodeAdded = useCallback(
    (frame: { id: string; kind: string; displayName: string }) => {
      setLiveSnapshot((prev) => {
        if (!prev) return prev;
        if (prev.nodes.some((n) => n.id === frame.id)) return prev;
        const newNode: InteractionsNodeResponse = {
          id: frame.id,
          kind: frame.kind,
          displayName: frame.displayName,
          sent: 0,
          received: 0,
        };
        return { ...prev, nodes: [...prev.nodes, newNode] };
      });
    },
    [],
  );

  const handleEdgeAdded = useCallback(
    (frame: { fromId: string; toId: string }) => {
      setLiveSnapshot((prev) => {
        if (!prev) return prev;
        if (
          prev.edges.some(
            (e) => e.fromId === frame.fromId && e.toId === frame.toId,
          )
        ) {
          return prev;
        }
        const now = new Date().toISOString();
        const newEdge: InteractionsEdgeResponse = {
          fromId: frame.fromId,
          toId: frame.toId,
          count: 0,
          firstAt: now,
          lastAt: now,
          channels: [],
        };
        return { ...prev, edges: [...prev.edges, newEdge] };
      });
    },
    [],
  );

  const handleThrottled = useCallback(
    (frame: { dropped: number }) => {
      setDroppedCount((d) => d + frame.dropped);
    },
    [],
  );

  // Live-mode auto-scroll: pin `until` to "now" so the timeline keeps
  // tracking the most recent activity. The interval is short enough
  // that the brushable timeline lingers behind the head by < 1 bucket.
  useEffect(() => {
    if (!state.live) return;
    const intervalId = setInterval(() => {
      const now = new Date().toISOString();
      applyState({ ...state, until: now, since: "" });
    }, 30_000);
    return () => clearInterval(intervalId);
  }, [state, applyState]);

  // Reset the dropped count whenever live mode flips off so a future
  // re-enable starts from zero.
  useEffect(() => {
    if (!state.live) setDroppedCount(0);
  }, [state.live]);

  // Resolve the window once per state change — both rewind-mode controls
  // and the timeline footer caption reference it. Named `viewWindow` (not
  // `window`) so we don't shadow the global inside this client component.
  const viewWindow = useMemo(() => resolveWindow(state), [state]);
  const windowSinceMs = useMemo(() => new Date(viewWindow.since).getTime(), [viewWindow]);
  const windowUntilMs = useMemo(() => new Date(viewWindow.until).getTime(), [viewWindow]);

  // Rewind cursor lives in page state (not URL — re-loading a deep-link
  // intentionally resets the cursor to 0 so the operator always starts
  // from the beginning of the window). The timeline brush and the
  // rewind transport bar both control this value.
  const [rewindCursorMs, setRewindCursorMs] = useState<number>(0);
  useEffect(() => {
    // Reset the cursor whenever the operator changes the window.
    setRewindCursorMs(0);
  }, [state.rewind, windowSinceMs, windowUntilMs]);

  // Rewind dispatch — feeds the same pulse queue the live SSE stream
  // does. The graph dedupes pulses by id (so a live reconnect doesn't
  // re-animate the same SSE frame twice), but rewind replays the same
  // messageId on every Restart. We mint a fresh per-dispatch id so the
  // animation actually fires on the second playthrough; the messageId
  // is preserved in the suffix so the detail card can still resolve it.
  const rewindPulseSeqRef = useRef(0);
  const handleRewindPulse = useCallback(
    (frame: RewindPulseFrame) => {
      const seq = ++rewindPulseSeqRef.current;
      const originalId = frame.messageIds[0] ?? `${frame.fromId}->${frame.toId}`;
      handlePulse({
        ...frame,
        messageIds: [`rewind:${seq}:${originalId}`],
      });
    },
    [handlePulse],
  );

  // Brush always selects [since, until] — in snapshot, live, and rewind
  // alike. Rewind replays through the brushed window; a separate cursor
  // (driven by the rewind transport + click-to-seek on the timeline)
  // tracks playback position independently.
  //
  // Defense in depth against degenerate windows: the timeline brush
  // already drops commits where the two handles overlap, but anything
  // else that hooks `handleBrush` (e.g. tests, future callers) could
  // still pass `since >= until`. Push `since` back to at least one
  // bucket before `until` so the brush always has room to drag and the
  // backend always sees a non-empty range.
  const handleBrush = useCallback(
    (windowMs: { since: string; until: string }) => {
      const sinceMs = new Date(windowMs.since).getTime();
      const untilMs = new Date(windowMs.until).getTime();
      if (!Number.isFinite(sinceMs) || !Number.isFinite(untilMs)) return;
      const minSpan = BUCKET_SIZE_MS[state.bucket] * 2;
      let since = windowMs.since;
      let until = windowMs.until;
      if (untilMs - sinceMs < minSpan) {
        since = new Date(untilMs - minSpan).toISOString();
      }
      applyState({ ...state, since, until });
    },
    [state, applyState],
  );

  // Click on a timeline bucket = narrow the window to exactly that
  // bucket's `[start, start + bucketSize)` slice. Both the graph and
  // matrix refetch against the new window so the operator can drill
  // into a single bucket's activity instantly. Works in all modes —
  // in rewind, the cursor naturally restarts at 0 because the window
  // changed (see the rewindCursorMs reset effect above).
  const handleBucketClick = useCallback(
    (bucketIso: string) => {
      const bucketStartMs = new Date(bucketIso).getTime();
      if (!Number.isFinite(bucketStartMs)) return;
      const bucketSize = BUCKET_SIZE_MS[state.bucket];
      const sinceIso = new Date(bucketStartMs).toISOString();
      const untilIso = new Date(bucketStartMs + bucketSize).toISOString();
      applyState({ ...state, since: sinceIso, until: untilIso });
    },
    [state, applyState],
  );

  const graph = liveSnapshot ?? null;
  const nodes = graph?.nodes ?? [];
  const edges = graph?.edges ?? [];
  const timeline = graph?.timeline ?? [];
  const truncated = graph?.truncated ?? null;

  const onSelectUnit = useCallback(
    (unitId: string) => {
      applyState({ ...state, unit: unitId });
    },
    [applyState, state],
  );

  const showGraph = state.view === "graph" || state.view === "both";
  const showMatrix = state.view === "matrix" || state.view === "both";

  return (
    <div className="space-y-4" data-testid="interactions-page">
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border pb-3">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <Network className="h-5 w-5" aria-hidden="true" />
            Interactions
          </h1>
          <p
            className="text-sm text-muted-foreground"
            data-testid="interactions-subtitle"
          >
            Graph and matrix of who is talking to whom — last 10 minutes
            unless narrowed below.
          </p>
        </div>
        {state.rewind ? (
          <InteractionRewind
            since={new Date(viewWindow.since)}
            until={new Date(viewWindow.until)}
            pulses={historyQuery.data?.pulses ?? []}
            cursorMs={rewindCursorMs}
            onCursorChange={setRewindCursorMs}
            onPulse={handleRewindPulse}
          />
        ) : (
          <InteractionLive
            enabled={state.live}
            unit={state.unit || undefined}
            neighbours={state.neighbours}
            onPulse={handlePulse}
            onNodeAdded={handleNodeAdded}
            onEdgeAdded={handleEdgeAdded}
            droppedCount={droppedCount}
            onThrottled={handleThrottled}
          />
        )}
      </header>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[18rem_minmax(0,1fr)]">
        <InteractionFilters state={state} onChange={applyState} />
        <div className="space-y-4">
          {state.rewind
            ? historyQuery.error
              ? (
                <ApiErrorMessage error={historyQuery.error} />
              )
              : null
            : snapshotQuery.error
              ? (
                <ApiErrorMessage error={snapshotQuery.error} />
              )
              : null}

          {(state.rewind
            ? historyQuery.isPending && !graph
            : snapshotQuery.isPending && !graph) ? (
            <Skeleton className="h-48" />
          ) : (
            <>
              {truncated ? (
                <Card
                  className="border-warning/40 bg-warning/5"
                  data-testid="interactions-truncation-banner"
                >
                  <CardContent className="flex flex-wrap items-center justify-between gap-2 p-3 text-xs">
                    <span>
                      Showing {truncated.kept} of {truncated.total} nodes by
                      message volume —{" "}
                      <button
                        type="button"
                        onClick={() => applyState({ ...state, view: "matrix" })}
                        className="text-primary underline hover:text-primary/80"
                        data-testid="interactions-truncation-switch-matrix"
                      >
                        switch to matrix to see all
                      </button>
                      .
                    </span>
                  </CardContent>
                </Card>
              ) : null}

              <Card>
                <CardContent className="p-3">
                  <InteractionTimeline
                    buckets={timeline}
                    nodes={nodes}
                    brushDisabled={state.live}
                    onBrush={handleBrush}
                    cursorIso={
                      state.rewind
                        ? new Date(windowSinceMs + rewindCursorMs).toISOString()
                        : undefined
                    }
                    onBucketClick={handleBucketClick}
                  />
                </CardContent>
              </Card>

              <div className="flex flex-col gap-4 lg:flex-row">
                <div className="min-w-0 flex-1 space-y-3">
                  {showGraph ? (
                    <Card>
                      <CardContent className="p-3">
                        <InteractionGraph
                          nodes={nodes}
                          edges={edges}
                          scopeId={state.unit || undefined}
                          onSelectUnit={onSelectUnit}
                          onSelectEdge={onSelectEdge}
                          pulses={livePulses}
                        />
                      </CardContent>
                    </Card>
                  ) : null}

                  {showMatrix ? (
                    <Card>
                      <CardContent className="p-3">
                        <InteractionMatrix
                          nodes={nodes}
                          edges={edges}
                          onSelectEdge={onSelectEdge}
                        />
                      </CardContent>
                    </Card>
                  ) : null}
                </div>

                {detailSubject ? (
                  <InteractionDetail
                    subject={detailSubject}
                    onClose={closeDetail}
                  />
                ) : null}
              </div>
            </>
          )}
        </div>
      </div>

      {/* Window summary tucked in the corner so deep-link consumers can
          see the materialised since/until they're looking at. */}
      <p className="text-[10px] text-muted-foreground">
        Window: {new Date(viewWindow.since).toLocaleString()} →{" "}
        {new Date(viewWindow.until).toLocaleString()}
      </p>

      {/* Hidden refresh button so tests + a11y users can refetch.
          Hidden visually — keep tests happy without adding chrome the
          design contract doesn't include. */}
      <Button
        variant="outline"
        size="sm"
        className="sr-only"
        onClick={() => snapshotQuery.refetch()}
        data-testid="interactions-refresh"
      >
        Refresh
      </Button>
    </div>
  );
}

// `useSearchParams()` must sit under Suspense so the route prerenders.
// Match the empty-state shape so hydration doesn't shift the layout.
export default function InteractionsPage() {
  return (
    <Suspense
      fallback={
        <div className="space-y-4" data-testid="interactions-page">
          <div className="space-y-2">
            <h1 className="flex items-center gap-2 text-2xl font-bold">
              <Network className="h-5 w-5" aria-hidden="true" />
              Interactions
            </h1>
          </div>
          <Skeleton className="h-48" />
        </div>
      }
    >
      <InteractionsPageContent />
    </Suspense>
  );
}
