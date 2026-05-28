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
  Handle,
  Position,
  ReactFlow,
  useInternalNode,
  type Edge as RfEdge,
  type EdgeProps,
  type InternalNode,
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

  // Use one ring for small populations (≤ 12 others) so a 3-node graph
  // doesn't end up with each node alone on its own ring at angle 0
  // (which collapsed the whole graph onto the X axis under the old
  // formula). For larger populations split into two concentric rings —
  // high-volume first, low-volume second — so the busiest nodes sit
  // closer to the anchor.
  const ringCount = others.length > 12 ? 2 : 1;
  const inner = ringCount === 1 ? others : others.slice(0, Math.ceil(others.length / 2));
  const outer = ringCount === 1 ? [] : others.slice(inner.length);

  // Radius scales with the per-ring node count so wide rectangles never
  // overlap — required circumference = N × (node width + padding).
  const minSpacing = NODE_BASE_WIDTH + 32;
  const minRadius = 200;
  const innerRadius = Math.max(
    minRadius,
    (inner.length * minSpacing) / (2 * Math.PI),
  );
  const outerRadius = Math.max(
    innerRadius + 180,
    ((inner.length + outer.length) * minSpacing) / (2 * Math.PI),
  );

  const result: RfNode<InteractionNodeData>[] = [];
  const center = { x: 480, y: 320 };

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

  // Place a single ring of nodes around the anchor, starting at the
  // top (-π/2) and walking clockwise so the visual reading order
  // matches the volume sort.
  const placeRing = (
    ringNodes: readonly InteractionsNodeResponse[],
    radius: number,
  ) => {
    const n = ringNodes.length;
    if (n === 0) return;
    for (let i = 0; i < n; i++) {
      const angle = -Math.PI / 2 + (i / n) * Math.PI * 2;
      const node = ringNodes[i];
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
  };
  placeRing(inner, innerRadius);
  placeRing(outer, outerRadius);

  return result;
}

function scaleFor(volume: number, maxVolume: number): number {
  return 0.6 + 0.6 * Math.sqrt(volume / maxVolume);
}

// xyflow requires explicit Handle components on custom nodes — without
// them, edges referencing the node have no connection point and silently
// fail to render (sourceX/Y/targetX/Y collapse to 0,0). For our radial
// layout we expose one hidden source + one hidden target handle at the
// node centre so an edge can attach regardless of direction; the
// `InteractionEdge` component does its own arrow-end pullback so the
// arrowhead sits at the node perimeter, not the centre.
const HIDDEN_HANDLE_STYLE: React.CSSProperties = {
  opacity: 0,
  width: 1,
  height: 1,
  minWidth: 1,
  minHeight: 1,
  border: 0,
  background: "transparent",
  pointerEvents: "none",
};

// Rounded-rectangle node dimensions. Width scales with volume so the
// busiest node still visually anchors the canvas; height is fixed so the
// font + padding don't change per node (variable height made the
// scaled-down nodes' text uncomfortably tight in the previous circle
// layout). The label cap moves to 24 chars — wide rectangles fit
// "managing-editor" and similar slugs without an ellipsis.
const NODE_HEIGHT = 36;
const NODE_BASE_WIDTH = 140;
const NODE_LABEL_CAP = 24;

function InteractionNode({ data }: NodeProps<RfNode<InteractionNodeData>>) {
  const tint =
    NODE_KIND_TINT[data.kind as keyof typeof NODE_KIND_TINT] ??
    NODE_KIND_TINT.default;
  const width = Math.round(NODE_BASE_WIDTH * data.scale);
  return (
    <div
      className={cn(
        "relative rounded-md border-2 text-center flex items-center justify-center font-mono text-[11px]",
        data.isScope ? "ring-2 ring-primary" : "",
      )}
      style={{
        width,
        height: NODE_HEIGHT,
        background: `color-mix(in srgb, ${tint} 25%, var(--color-card))`,
        borderColor: tint,
        color: "var(--color-foreground)",
      }}
      title={`${data.displayName} (${data.kind}) — sent ${data.sent}, received ${data.received}`}
      data-testid={`interaction-graph-node-${data.kind}`}
    >
      <Handle
        type="target"
        position={Position.Top}
        isConnectable={false}
        style={HIDDEN_HANDLE_STYLE}
      />
      <span className="block max-w-full truncate px-2 leading-tight">
        {data.displayName.length > NODE_LABEL_CAP
          ? `${data.displayName.slice(0, NODE_LABEL_CAP - 1)}…`
          : data.displayName}
      </span>
      <Handle
        type="source"
        position={Position.Bottom}
        isConnectable={false}
        style={HIDDEN_HANDLE_STYLE}
      />
    </div>
  );
}

/**
 * Floating-edge perimeter intersection (adapted from xyflow's official
 * Floating Edges example: https://reactflow.dev/examples/edges/floating-edges).
 *
 * Treats the node as an axis-aligned rectangle and returns the point
 * where the line from <c>intersectionNode</c>'s centre toward
 * <c>otherNode</c>'s centre crosses <c>intersectionNode</c>'s perimeter.
 * Using xyflow's own measured geometry (`internals.positionAbsolute` +
 * `measured.width/height`) means the result tracks node size + position
 * automatically — when a node grows because of volume scaling or the
 * operator drags it, the arrow follows the rectangle's actual edge.
 */
function getNodeIntersection(
  intersectionNode: InternalNode,
  otherNode: InternalNode,
): { x: number; y: number } {
  const w = (intersectionNode.measured?.width ?? 0) / 2;
  const h = (intersectionNode.measured?.height ?? 0) / 2;
  if (w === 0 || h === 0) {
    const pos = intersectionNode.internals.positionAbsolute;
    return { x: pos.x, y: pos.y };
  }
  const x2 = intersectionNode.internals.positionAbsolute.x + w;
  const y2 = intersectionNode.internals.positionAbsolute.y + h;
  const x1 =
    otherNode.internals.positionAbsolute.x + (otherNode.measured?.width ?? 0) / 2;
  const y1 =
    otherNode.internals.positionAbsolute.y + (otherNode.measured?.height ?? 0) / 2;
  const xx1 = (x1 - x2) / (2 * w) - (y1 - y2) / (2 * h);
  const yy1 = (x1 - x2) / (2 * w) + (y1 - y2) / (2 * h);
  const a = 1 / (Math.abs(xx1) + Math.abs(yy1));
  return {
    x: w * (a * xx1 + a * yy1) + x2,
    y: h * (a * yy1 - a * xx1) + y2,
  };
}

function InteractionEdge({
  source,
  target,
  data,
  id,
}: EdgeProps<RfEdge<InteractionEdgeData>>) {
  // Read source/target geometry from xyflow's own node store — this
  // is the floating-edges pattern: the edge does not depend on Handle
  // positions, so endpoints always land on the rectangle's nearest
  // perimeter regardless of the node's size or where it's positioned.
  const sourceNode = useInternalNode(source);
  const targetNode = useInternalNode(target);

  const weight = data?.weight ?? 1;
  const pulseCount = data?.pulseCount ?? 0;
  const isActive = !!data?.hasPulse;
  // Per-edge arrow-marker id so multiple edges don't share one marker
  // definition (xyflow re-mounts edge SVGs as it pans / zooms; a
  // single shared id leaks between renders and visually drops arrows).
  const markerId = `interaction-arrow-${id.replace(/[^a-zA-Z0-9-]/g, "_")}`;

  if (!sourceNode || !targetNode) return null;

  // Endpoints sit on the perimeter of the source and target rectangles
  // respectively — not at the centre, not at the Handle position. The
  // arrowhead therefore lands flush with the rectangle's edge instead
  // of disappearing behind the node body.
  const sourceIntersection = getNodeIntersection(sourceNode, targetNode);
  const targetIntersection = getNodeIntersection(targetNode, sourceNode);
  const sourceX = sourceIntersection.x;
  const sourceY = sourceIntersection.y;
  const targetX = targetIntersection.x;
  const targetY = targetIntersection.y;

  // Quadratic bezier curve with the control point offset perpendicular
  // to the chord. A→B and B→A travel between the same node pair but
  // the perpendicular vector flips with the source/target swap, so
  // reciprocal traffic naturally splits into two parallel-looking arcs
  // instead of stacking on the same straight line.
  const dx = targetX - sourceX;
  const dy = targetY - sourceY;
  const len = Math.max(1, Math.hypot(dx, dy));
  const perpX = -dy / len;
  const perpY = dx / len;
  const curveOffset = Math.min(len * 0.15, 55);
  const midX = (sourceX + targetX) / 2 + perpX * curveOffset;
  const midY = (sourceY + targetY) / 2 + perpY * curveOffset;
  const pathD = `M ${sourceX},${sourceY} Q ${midX},${midY} ${targetX},${targetY}`;

  // Idle vs active: idle edges stay faint so the canvas doesn't read
  // as a tangle of lines at rest. Active edges (a pulse is in flight)
  // brighten and thicken so the eye is pulled to where activity is
  // happening — together with the rewind cursor this makes "what's
  // moving right now" pop out across the graph.
  const strokeOpacity = isActive ? 0.95 : 0.22;
  const strokeWidth = isActive ? 1.6 + weight * 3 : 1.0 + weight * 1.2;

  return (
    <g data-testid={`interaction-graph-edge-${id}`}>
      <defs>
        <marker
          id={markerId}
          viewBox="0 0 10 10"
          refX="9"
          refY="5"
          markerWidth="7"
          markerHeight="7"
          orient="auto-start-reverse"
        >
          <path
            d="M 0 0 L 10 5 L 0 10 z"
            fill="var(--color-primary)"
            opacity={isActive ? 0.95 : 0.5}
          />
        </marker>
      </defs>
      <path
        d={pathD}
        stroke="var(--color-primary)"
        strokeOpacity={strokeOpacity}
        strokeWidth={strokeWidth}
        fill="none"
        markerEnd={`url(#${markerId})`}
        style={{ transition: "stroke-opacity 200ms, stroke-width 200ms" }}
      />
      {isActive ? (
        <>
          <circle
            data-testid={`interaction-graph-pulse-${id}`}
            r={5}
            fill="var(--color-primary)"
            stroke="var(--color-card)"
            strokeWidth={1.5}
          >
            <animateMotion
              dur="0.8s"
              repeatCount="1"
              path={pathD}
              fill="freeze"
            />
          </circle>
          {pulseCount > 1 ? (
            <g
              transform={`translate(${midX}, ${midY - 10})`}
              data-testid={`interaction-graph-pulse-badge-${id}`}
            >
              <rect
                x={-14}
                y={-9}
                width={28}
                height={16}
                rx={8}
                fill="var(--color-primary)"
                opacity={0.95}
              />
              <text
                x={0}
                y={3}
                fontSize="11"
                fontWeight="600"
                fill="var(--color-primary-foreground, white)"
                textAnchor="middle"
                fontFamily="var(--font-mono)"
              >
                ×{pulseCount}
              </text>
            </g>
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
        // Schedule expiry — match the SVG animateMotion duration plus a
        // small buffer so the bright "active" stroke stays up until the
        // pulse dot reaches the target. 800ms motion + 200ms badge
        // dwell gives the operator time to read the count badge.
        setTimeout(() => {
          setActivePulses((current) => {
            const updated = new Map(current);
            updated.delete(key);
            return updated;
          });
        }, 1000);
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
