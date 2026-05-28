/* @vitest-environment jsdom */
import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { InteractionGraph, type LivePulse } from "./interaction-graph";

// `@xyflow/react` measures DOM size and throws under JSDOM without a
// layout. Stub it with a render-friendly placeholder that emits the
// nodes / edges as DOM so the de-dupe assertions can target the SVG
// pulse elements via their `data-testid` hooks. The stub renders the
// edge layer through the edgeTypes registry so the real
// `InteractionEdge` component still drives the pulse-badge / animation
// state — that's the unit-under-test here.
vi.mock("@xyflow/react", async () => {
  const React = await import("react");
  type EdgeProps = {
    sourceX: number;
    sourceY: number;
    targetX: number;
    targetY: number;
    id: string;
    source: string;
    target: string;
    data: unknown;
  };
  // Shared in-test registry of node geometry. ReactFlow stub populates
  // it on render; `useInternalNode` reads from it so the
  // floating-edge perimeter math has measured dimensions to work with.
  const nodeRegistry = new Map<
    string,
    { positionAbsolute: { x: number; y: number }; measured: { width: number; height: number } }
  >();
  return {
    ReactFlow: ({
      nodes,
      edges,
      edgeTypes,
    }: {
      nodes: ReadonlyArray<{ id: string; data: unknown; position: { x: number; y: number } }>;
      edges: ReadonlyArray<{ id: string; source: string; target: string; data: unknown; type?: string }>;
      edgeTypes?: Record<string, React.ComponentType<EdgeProps>>;
    }) => {
      nodeRegistry.clear();
      for (const n of nodes) {
        nodeRegistry.set(n.id, {
          positionAbsolute: { x: n.position.x, y: n.position.y },
          measured: { width: 140, height: 36 },
        });
      }
      return (
        <svg data-testid="rf-mock" width={800} height={500}>
          {edges.map((e) => {
            const EdgeComp =
              edgeTypes?.[e.type ?? "interaction"] ??
              ((p: EdgeProps) => (
                <line
                  x1={p.sourceX}
                  x2={p.targetX}
                  y1={p.sourceY}
                  y2={p.targetY}
                />
              ));
            const src = nodeRegistry.get(e.source)?.positionAbsolute ?? { x: 0, y: 0 };
            const tgt = nodeRegistry.get(e.target)?.positionAbsolute ?? { x: 0, y: 0 };
            return (
              <EdgeComp
                key={e.id}
                id={e.id}
                source={e.source}
                target={e.target}
                sourceX={src.x}
                sourceY={src.y}
                targetX={tgt.x}
                targetY={tgt.y}
                data={e.data}
              />
            );
          })}
        </svg>
      );
    },
    Background: () => null,
    Controls: () => null,
    Handle: () => null,
    Position: { Top: "top", Right: "right", Bottom: "bottom", Left: "left" },
    useInternalNode: (id: string) => {
      const entry = nodeRegistry.get(id);
      if (!entry) return null;
      return {
        internals: { positionAbsolute: entry.positionAbsolute },
        measured: entry.measured,
      };
    },
  };
});

const NODES = [
  { id: "a", kind: "agent", displayName: "Agent", sent: 1, received: 1 },
  { id: "b", kind: "unit", displayName: "Unit", sent: 1, received: 1 },
];
const EDGES = [
  {
    fromId: "a",
    toId: "b",
    count: 5,
    firstAt: "2026-05-27T10:00:00Z",
    lastAt: "2026-05-27T10:01:00Z",
    channels: ["unit"],
  },
];

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

function pulse(id: string, count: number): LivePulse {
  return { id, fromId: "a", toId: "b", count };
}

describe("InteractionGraph live-pulse dedupe", () => {
  it("animates a single pulse on first frame", () => {
    const { rerender } = render(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[]} />,
    );
    rerender(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[pulse("p1", 1)]} />,
    );
    expect(
      screen.getByTestId("interaction-graph-pulse-a->b"),
    ).toBeInTheDocument();
    // Single pulse count of 1 — no badge.
    expect(
      screen.queryByTestId("interaction-graph-pulse-badge-a->b"),
    ).not.toBeInTheDocument();
  });

  it("does not start a second animation while one is in flight — bumps the badge instead", () => {
    const { rerender } = render(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[]} />,
    );
    rerender(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[pulse("p1", 1)]} />,
    );
    // Drive a second pulse on the same edge before the 1000ms expiry.
    act(() => {
      vi.advanceTimersByTime(100);
    });
    rerender(
      <InteractionGraph
        nodes={NODES}
        edges={EDGES}
        pulses={[pulse("p1", 1), pulse("p2", 3)]}
      />,
    );
    // Still exactly one pulse circle on this edge.
    expect(
      screen.getAllByTestId("interaction-graph-pulse-a->b"),
    ).toHaveLength(1);
    // Badge now visible with the cumulative count (1 + 3 = 4).
    expect(
      screen.getByTestId("interaction-graph-pulse-badge-a->b").textContent,
    ).toBe("×4");
  });

  it("clears the pulse after 1000ms so the next frame starts a fresh animation", () => {
    const { rerender } = render(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[]} />,
    );
    rerender(
      <InteractionGraph nodes={NODES} edges={EDGES} pulses={[pulse("p1", 1)]} />,
    );
    act(() => {
      vi.advanceTimersByTime(1001);
    });
    // After expiry the pulse circle is gone again.
    expect(
      screen.queryByTestId("interaction-graph-pulse-a->b"),
    ).not.toBeInTheDocument();
  });
});
