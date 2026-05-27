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
import { useRouter, useSearchParams } from "next/navigation";
import {
  Suspense,
  useCallback,
  useEffect,
  useMemo,
  useState,
} from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useInteractionsSnapshot } from "@/lib/api/queries";
import type {
  InteractionsEdgeResponse,
  InteractionsGraphResponse,
  InteractionsNodeResponse,
} from "@/lib/api/types";

import { InteractionDetail, type InteractionDetailSubject } from "@/components/interactions/interaction-detail";
import { InteractionFilters } from "@/components/interactions/interaction-filters";
import { InteractionGraph, type LivePulse } from "@/components/interactions/interaction-graph";
import { InteractionLive } from "@/components/interactions/interaction-live";
import { InteractionMatrix } from "@/components/interactions/interaction-matrix";
import { InteractionTimeline } from "@/components/interactions/interaction-timeline";
import {
  readUrlState,
  resolveWindow,
  toSnapshotFilters,
  writeUrlState,
  type InteractionsUrlState,
} from "@/components/interactions/url-state";

const MAX_LIVE_PULSES = 200;

function InteractionsPageContent() {
  const router = useRouter();
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
      setState(next);
      const search = writeUrlState(next);
      router.replace(`/activity/interactions${search ? `?${search}` : ""}`);
    },
    [router],
  );

  const filters = useMemo(() => toSnapshotFilters(state), [state]);
  const snapshotQuery = useInteractionsSnapshot(filters);

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

  const handleBrush = useCallback(
    (windowMs: { since: string; until: string }) => {
      applyState({ ...state, since: windowMs.since, until: windowMs.until });
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
      </header>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[18rem_minmax(0,1fr)]">
        <InteractionFilters state={state} onChange={applyState} />
        <div className="space-y-4">
          {snapshotQuery.error ? (
            <ApiErrorMessage error={snapshotQuery.error} />
          ) : null}

          {snapshotQuery.isPending && !graph ? (
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
                    brushDisabled={state.live}
                    onBrush={handleBrush}
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
        Window: {new Date(resolveWindow(state).since).toLocaleString()} →{" "}
        {new Date(resolveWindow(state).until).toLocaleString()}
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
