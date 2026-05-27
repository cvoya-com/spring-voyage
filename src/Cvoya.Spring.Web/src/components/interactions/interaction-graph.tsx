"use client";

// Force-directed graph view of the interactions snapshot (#2867).
//
// Built on @xyflow/react. We don't ship a custom simulation: layouts
// are deterministic radial concentric rings around the scope unit
// (when set) or the highest-volume node (otherwise). xyflow handles
// pan / zoom / selection; we layer a thin façade that:
//
//   - colours each node by kind (agent / unit / human / connector),
//   - scales each node by `sent + received`,
//   - draws each edge as a thin line with weight proportional to count,
//   - dispatches click-on-node → `onSelectUnit`,
//   - dispatches click-on-edge → `onSelectEdge` (opens the detail card),
//   - animates short-lived "pulses" along an edge when the parent
//     pushes a frame via `pulseSeed`.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Background,
  Controls,
  ReactFlow,
  type Edge as RfEdge,
  type EdgeProps,
  type Node as RfNode,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import { cn } from "@/lib/utils";
import type {
  InteractionsEdgeResponse,
  InteractionsNodeResponse,
} from "@/lib/api/types";

import {
  type InteractionDetailSubject,
  NODE_KIND_TINT,
} from "./interaction-detail";

interface InteractionGraphProps {
  nodes: readonly InteractionsNodeResponse[];
  edges: readonly InteractionsEdgeResponse[];
  /** Currently-scoped unit id, if any. Used to anchor the radial layout. */
  scopeId?: string;
  onSelectUnit?: (unitId: string, kind: string) => void;
  onSelectEdge?: (subject: InteractionDetailSubject) => void;
  /**
   * Monotonically-increasing pulse stream from the parent. Each entry is
   * one pulse frame; the graph animates a dot along the matching edge
   * for ~600ms. The parent dedupes per-edge in-flight pulses (per the
   * brief) so a burst on a single edge bumps the count badge instead of
   * spawning a second animation.
   */
  pulses?: readonly LivePulse[];
}

export interface LivePulse {
  /** Unique frame id (monotonic per stream). */
  id: string;
  fromId: string;
  toId: string;
  /** Count badge to surface on the receiving end. */
  count: number;
}

interface InteractionNodeData {
  kind: string;
  displayName: string;
  sent: number;
  received: number;
  scale: number;
  isScope: boolean;
  [key: string]: unknown;
}

interface InteractionEdgeData {
  count: number;
  channels: readonly string[];
  firstAt: string;
  lastAt: string;
  weight: number;
  hasPulse: boolean;
  pulseCount: number;
  [key: string]: unknown;
}

/**
 * Concentric radial layout — scope at the centre, neighbours on rings
 * by hop-derived position (we don't have the hop on the wire; instead
 * we sort by `sent + received` and place high-volume nodes inside the
 * first ring, low-volume on the outer ring). Deterministic for a given
 * node set so the graph doesn't reshuffle between snapshot refetches.
 */
function layoutNodes(
  nodes: readonly InteractionsNodeResponse[],
  scopeId: string | undefined,
): RfNode<InteractionNodeData>[] {
  if (nodes.length === 0) return [];

  // Pick the anchor: the scope id if it's in the snapshot, otherwise
  // the node with the highest combined volume so the canvas reads
  // centred even without a unit filter.
  const sorted = [...nodes].sort(
    (a, b) => b.sent + b.received - (a.sent + a.received),
  );
  const anchorId = scopeId && nodes.some((n) => n.id === scopeId)
    ? scopeId
    : sorted[0].id;

  const maxVolume = Math.max(
    1,
    ...nodes.map((n) => n.sent + n.received),
  );

  const others = sorted.filter((n) => n.id !== anchorId);
  const rings = 2;
  const perRing = Math.max(1, Math.ceil(others.length / rings));

  const result: RfNode<InteractionNodeData>[] = [];
  const center = { x: 400, y: 280 };

  const anchor = nodes.find((n) => n.id === anchorId)!;
  result.push({
    id: anchor.id,
    position: { x: center.x, y: center.y },
    type: "interaction",
    data: {
      kind: anchor.kind,
      displayName: anchor.displayName,
      sent: anchor.sent,
      received: anchor.received,
      scale: scaleFor(anchor.sent + anchor.received, maxVolume),
      isScope: anchor.id === scopeId,
    },
    draggable: true,
  });

  for (let i = 0; i < others.length; i++) {
    const ring = Math.min(rings - 1, Math.floor(i / perRing));
    const indexInRing = i - ring * perRing;
    const ringSize = Math.min(perRing, others.length - ring * perRing);
    const radius = 140 + ring * 130;
    const angle = (indexInRing / Math.max(1, ringSize)) * Math.PI * 2;
    const node = others[i];
    result.push({
      id: node.id,
      position: {
        x: center.x + radius * Math.cos(angle),
        y: center.y + radius * Math.sin(angle),
      },
      type: "interaction",
      data: {
        kind: node.kind,
        displayName: node.displayName,
        sent: node.sent,
        received: node.received,
        scale: scaleFor(node.sent + node.received, maxVolume),
        isScope: node.id === scopeId,
      },
      draggable: true,
    });
  }
  return result;
}

function scaleFor(volume: number, maxVolume: number): number {
  return 0.6 + 0.6 * Math.sqrt(volume / maxVolume);
}

function InteractionNode({ data }: NodeProps<RfNode<InteractionNodeData>>) {
  const tint =
    NODE_KIND_TINT[data.kind as keyof typeof NODE_KIND_TINT] ??
    NODE_KIND_TINT.default;
  const size = Math.round(32 * data.scale);
  return (
    <div
      className={cn(
        "rounded-full border-2 text-center flex items-center justify-center font-mono text-[10px]",
        data.isScope ? "ring-2 ring-primary" : "",
      )}
      style={{
        width: size,
        height: size,
        background: `color-mix(in srgb, ${tint} 25%, var(--color-card))`,
        borderColor: tint,
        color: "var(--color-foreground)",
      }}
      title={`${data.displayName} (${data.kind}) — sent ${data.sent}, received ${data.received}`}
      data-testid={`interaction-graph-node-${data.kind}`}
    >
      <span className="block max-w-full truncate px-1">
        {data.displayName.slice(0, 6)}
      </span>
    </div>
  );
}

function InteractionEdge({
  sourceX,
  sourceY,
  targetX,
  targetY,
  data,
  id,
}: EdgeProps<RfEdge<InteractionEdgeData>>) {
  const weight = data?.weight ?? 1;
  const strokeWidth = 0.6 + weight * 3;
  const pulseCount = data?.pulseCount ?? 0;
  return (
    <g data-testid={`interaction-graph-edge-${id}`}>
      <path
        d={`M ${sourceX},${sourceY} L ${targetX},${targetY}`}
        stroke="var(--color-muted-foreground)"
        strokeOpacity={0.5 + weight * 0.4}
        strokeWidth={strokeWidth}
        fill="none"
      />
      {data?.hasPulse ? (
        <>
          <circle
            data-testid={`interaction-graph-pulse-${id}`}
            r={4}
            fill="var(--color-primary)"
            stroke="var(--color-card)"
            strokeWidth={1}
          >
            <animateMotion
              dur="0.6s"
              repeatCount="1"
              path={`M ${sourceX},${sourceY} L ${targetX},${targetY}`}
              fill="freeze"
            />
          </circle>
          {pulseCount > 1 ? (
            <text
              x={(sourceX + targetX) / 2}
              y={(sourceY + targetY) / 2 - 6}
              fontSize="9"
              fill="var(--color-primary)"
              textAnchor="middle"
              fontFamily="var(--font-mono)"
              data-testid={`interaction-graph-pulse-badge-${id}`}
            >
              ×{pulseCount}
            </text>
          ) : null}
        </>
      ) : null}
    </g>
  );
}

const nodeTypes = { interaction: InteractionNode };
const edgeTypes = { interaction: InteractionEdge };

interface ActivePulse {
  edgeKey: string;
  count: number;
  /** monotonic React key — bumped every time the count changes so xyflow re-renders the SVG. */
  refreshKey: number;
}

export function InteractionGraph({
  nodes,
  edges,
  scopeId,
  onSelectUnit,
  onSelectEdge,
  pulses,
}: InteractionGraphProps) {
  // Track in-flight pulses per edge keyed by "fromId|toId". A pulse
  // expires ~600 ms after it starts; while it is active, further pulse
  // frames on the same edge bump the count instead of starting a new
  // animation (per the brief's "don't start another" rule).
  const [activePulses, setActivePulses] = useState<Map<string, ActivePulse>>(
    () => new Map(),
  );
  const seenPulseIds = useRef<Set<string>>(new Set());

  useEffect(() => {
    if (!pulses || pulses.length === 0) return;
    let mutated = false;
    const next = new Map(activePulses);
    for (const p of pulses) {
      if (seenPulseIds.current.has(p.id)) continue;
      seenPulseIds.current.add(p.id);
      const key = `${p.fromId}|${p.toId}`;
      const existing = next.get(key);
      if (existing) {
        next.set(key, {
          edgeKey: key,
          count: existing.count + p.count,
          refreshKey: existing.refreshKey + 1,
        });
      } else {
        next.set(key, { edgeKey: key, count: p.count, refreshKey: 1 });
        // Schedule expiry — pulses last 600ms.
        setTimeout(() => {
          setActivePulses((current) => {
            const updated = new Map(current);
            updated.delete(key);
            return updated;
          });
        }, 600);
      }
      mutated = true;
    }
    if (mutated) setActivePulses(next);
    // We deliberately depend only on `pulses` — the active-pulses map is
    // managed via the seen-id set so we don't loop on our own setState.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pulses]);

  const rfNodes = useMemo(() => layoutNodes(nodes, scopeId), [nodes, scopeId]);
  const nodeById = useMemo(() => {
    const m = new Map<string, InteractionsNodeResponse>();
    for (const n of nodes) m.set(n.id, n);
    return m;
  }, [nodes]);

  const rfEdges = useMemo<RfEdge<InteractionEdgeData>[]>(() => {
    if (edges.length === 0) return [];
    const maxCount = Math.max(1, ...edges.map((e) => e.count));
    return edges.map((edge) => {
      const key = `${edge.fromId}|${edge.toId}`;
      const pulse = activePulses.get(key);
      return {
        id: `${edge.fromId}->${edge.toId}`,
        source: edge.fromId,
        target: edge.toId,
        type: "interaction",
        data: {
          count: edge.count,
          channels: edge.channels ?? [],
          firstAt: edge.firstAt,
          lastAt: edge.lastAt,
          weight: edge.count / maxCount,
          hasPulse: !!pulse,
          pulseCount: pulse?.count ?? 0,
        },
      };
    });
  }, [edges, activePulses]);

  const handleNodeClick = useCallback(
    (_e: React.MouseEvent, node: RfNode<InteractionNodeData>) => {
      onSelectUnit?.(node.id, node.data.kind);
    },
    [onSelectUnit],
  );

  const handleEdgeClick = useCallback(
    (_e: React.MouseEvent, edge: RfEdge<InteractionEdgeData>) => {
      const data = edge.data;
      if (!data) return;
      const from = nodeById.get(edge.source);
      const to = nodeById.get(edge.target);
      onSelectEdge?.({
        fromId: edge.source,
        toId: edge.target,
        fromName: from?.displayName ?? edge.source,
        toName: to?.displayName ?? edge.target,
        fromKind: from?.kind ?? "unknown",
        toKind: to?.kind ?? "unknown",
        count: data.count,
        channels: data.channels,
        firstAt: data.firstAt,
        lastAt: data.lastAt,
      });
    },
    [nodeById, onSelectEdge],
  );

  if (nodes.length === 0) {
    return (
      <p
        className="rounded-md border border-dashed border-border p-6 text-center text-sm text-muted-foreground"
        data-testid="interaction-graph-empty"
      >
        No interactions in this window.
      </p>
    );
  }

  return (
    <div
      data-testid="interaction-graph"
      className="rounded-md border border-border bg-card"
      style={{ height: 480 }}
    >
      <ReactFlow
        nodes={rfNodes}
        edges={rfEdges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodeClick={handleNodeClick}
        onEdgeClick={handleEdgeClick}
        fitView
        proOptions={{ hideAttribution: true }}
        // Read-only canvas: no connections, no drag-to-create, no
        // delete-on-backspace. Only pan / zoom / select.
        nodesConnectable={false}
        edgesFocusable
        edgesReconnectable={false}
        deleteKeyCode={null}
      >
        <Background gap={32} color="var(--color-border)" />
        <Controls
          showInteractive={false}
          className="bg-card border border-border rounded-md"
        />
      </ReactFlow>
    </div>
  );
}
