"use client";

// Adjacency-matrix view of the interactions graph (#2867). Rows are
// senders, columns are receivers, each cell renders the aggregate
// message count between sender→receiver with intensity proportional to
// the count.
//
// Connector column is intentionally omitted per ADR-0048: connectors
// are source-only — they can send but never receive — so a connector
// column would always be empty.

import { useCallback, useMemo, useState } from "react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type {
  InteractionsEdgeResponse,
  InteractionsNodeResponse,
} from "@/lib/api/types";

import type { InteractionDetailSubject } from "./interaction-detail";

interface InteractionMatrixProps {
  nodes: readonly InteractionsNodeResponse[];
  edges: readonly InteractionsEdgeResponse[];
  /** Optional cell-click handler — opens the detail popover. */
  onSelectEdge?: (subject: InteractionDetailSubject) => void;
}

type SortKey = "volume" | "kind";

export function InteractionMatrix({
  nodes,
  edges,
  onSelectEdge,
}: InteractionMatrixProps) {
  const [sortBy, setSortBy] = useState<SortKey>("volume");

  // Senders = every node that has at least one outbound edge OR appears
  // as a node row. Receivers = every non-connector node (the column-
  // omission rule). The matrix renders every row even if it has zero
  // outbound edges in the window so the operator can see "this unit
  // hasn't talked to anyone".
  const { senders, receivers, cellLookup, maxCount } = useMemo(() => {
    const byId = new Map<string, InteractionsNodeResponse>();
    for (const node of nodes) byId.set(node.id, node);

    const cellMap = new Map<string, InteractionsEdgeResponse>();
    let max = 0;
    for (const edge of edges) {
      cellMap.set(`${edge.fromId}|${edge.toId}`, edge);
      if (edge.count > max) max = edge.count;
    }

    const sortNodes = (list: InteractionsNodeResponse[]) => {
      if (sortBy === "kind") {
        return [...list].sort((a, b) => {
          const kindCmp = a.kind.localeCompare(b.kind);
          return kindCmp !== 0
            ? kindCmp
            : (b.sent + b.received) - (a.sent + a.received);
        });
      }
      return [...list].sort(
        (a, b) => b.sent + b.received - (a.sent + a.received),
      );
    };

    const sendersList = sortNodes(nodes.slice());
    const receiversList = sortNodes(
      nodes.filter((n) => n.kind !== "connector"),
    );

    return {
      senders: sendersList,
      receivers: receiversList,
      cellLookup: cellMap,
      maxCount: max,
      nodeById: byId,
    };
  }, [nodes, edges, sortBy]);

  const nodeById = useMemo(() => {
    const map = new Map<string, InteractionsNodeResponse>();
    for (const node of nodes) map.set(node.id, node);
    return map;
  }, [nodes]);

  const handleCellClick = useCallback(
    (fromId: string, toId: string) => {
      const edge = cellLookup.get(`${fromId}|${toId}`);
      if (!edge || !onSelectEdge) return;
      const from = nodeById.get(fromId);
      const to = nodeById.get(toId);
      onSelectEdge({
        fromId,
        toId,
        fromName: from?.displayName ?? fromId,
        toName: to?.displayName ?? toId,
        fromKind: from?.kind ?? "unknown",
        toKind: to?.kind ?? "unknown",
        count: edge.count,
        channels: edge.channels ?? [],
        firstAt: edge.firstAt,
        lastAt: edge.lastAt,
      });
    },
    [cellLookup, nodeById, onSelectEdge],
  );

  if (nodes.length === 0) {
    return (
      <p
        className="text-sm text-muted-foreground"
        data-testid="interaction-matrix-empty"
      >
        No participants in this window.
      </p>
    );
  }

  return (
    <div
      className="space-y-2"
      data-testid="interaction-matrix"
      role="region"
      aria-label="Sender-by-receiver adjacency matrix"
    >
      <div className="flex items-center justify-between gap-2 text-xs">
        <span className="text-muted-foreground">
          {senders.length} senders × {receivers.length} receivers
        </span>
        <label className="flex items-center gap-1.5 text-xs">
          <span className="text-muted-foreground">Sort</span>
          <select
            aria-label="Sort matrix"
            value={sortBy}
            onChange={(e) => setSortBy(e.target.value as SortKey)}
            data-testid="interaction-matrix-sort"
            className="h-7 rounded-md border border-input bg-background px-2 text-xs"
          >
            <option value="volume">By total volume</option>
            <option value="kind">By kind</option>
          </select>
        </label>
      </div>

      <div className="overflow-auto rounded-md border border-border">
        <table
          className="min-w-full border-collapse text-xs"
          data-testid="interaction-matrix-table"
        >
          <thead>
            <tr className="bg-muted/40">
              <th className="sticky left-0 z-10 bg-muted/80 px-2 py-1 text-left font-medium text-muted-foreground">
                Sender ↓ / Receiver →
              </th>
              {receivers.map((r) => (
                <th
                  key={r.id}
                  scope="col"
                  data-testid={`interaction-matrix-col-${r.id}`}
                  className="px-1 py-1 font-mono text-[10px]"
                  title={`${r.displayName} (${r.kind})`}
                >
                  <span className="block max-w-20 truncate">
                    {r.displayName}
                  </span>
                  <Badge
                    variant="outline"
                    className="mt-0.5 px-1 text-[9px] font-normal"
                  >
                    {r.kind}
                  </Badge>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {senders.map((s) => (
              <tr key={s.id} data-testid={`interaction-matrix-row-${s.id}`}>
                <th
                  scope="row"
                  className="sticky left-0 z-10 bg-card px-2 py-1 text-left font-mono text-[10px]"
                  title={`${s.displayName} (${s.kind})`}
                >
                  <span className="block max-w-32 truncate">
                    {s.displayName}
                  </span>
                  <Badge
                    variant="outline"
                    className="mt-0.5 px-1 text-[9px] font-normal"
                  >
                    {s.kind}
                  </Badge>
                </th>
                {receivers.map((r) => {
                  const edge = cellLookup.get(`${s.id}|${r.id}`);
                  const count = edge?.count ?? 0;
                  const intensity =
                    maxCount > 0 ? Math.min(1, count / maxCount) : 0;
                  return (
                    <td
                      key={r.id}
                      data-testid={`interaction-matrix-cell-${s.id}-${r.id}`}
                      data-count={count}
                      className={cn(
                        "border-t border-l border-border px-1 py-1 text-center font-mono text-[10px] tabular-nums",
                        count > 0
                          ? "cursor-pointer hover:outline hover:outline-2 hover:outline-primary"
                          : "text-muted-foreground/50",
                      )}
                      style={
                        count > 0
                          ? {
                              backgroundColor: `color-mix(in srgb, var(--color-primary) ${Math.round(intensity * 60)}%, transparent)`,
                            }
                          : undefined
                      }
                      onClick={
                        count > 0
                          ? () => handleCellClick(s.id, r.id)
                          : undefined
                      }
                      role={count > 0 ? "button" : undefined}
                      tabIndex={count > 0 ? 0 : undefined}
                      onKeyDown={(e) => {
                        if (count > 0 && (e.key === "Enter" || e.key === " ")) {
                          e.preventDefault();
                          handleCellClick(s.id, r.id);
                        }
                      }}
                      aria-label={
                        count > 0
                          ? `${count} messages from ${s.displayName} to ${r.displayName}`
                          : undefined
                      }
                    >
                      {count > 0 ? count : ""}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
