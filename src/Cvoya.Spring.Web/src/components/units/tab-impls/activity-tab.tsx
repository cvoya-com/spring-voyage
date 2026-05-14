"use client";

import { useState } from "react";
import {
  Activity,
  ChevronDown,
  ChevronRight,
  DollarSign,
  RefreshCw,
  TrendingDown,
} from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useActivityQuery,
  useAgentCostBreakdown,
  useAgentCostTimeseries,
} from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type {
  ActivityQueryResult,
  ActivitySeverity,
} from "@/lib/api/types";
import { formatCost, humanEventType, timeAgo } from "@/lib/utils";

/**
 * Subjects the unified activity tab can be driven by. The activity feed
 * is keyed by an `<scheme>:<id>` source filter — `unit:<id>` for units,
 * `agent:<id>` for agents — and the SSE stream filter mirrors the same
 * pair. Adding a new kind here is a matter of teaching the source-string
 * builder + filter predicate below; nothing else in the component is
 * subject-specific.
 */
export type ActivitySubjectKind = "Unit" | "Agent";

export interface ActivityTabProps {
  /** Subject kind — drives the activity-source filter and the stream predicate. */
  kind: ActivitySubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
}

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

// Maps the subject kind to the activity-source URI scheme used by the
// REST query + SSE stream. Kept inline (rather than promoted to a util)
// because it is the *only* place the kind ⇄ scheme correspondence is
// load-bearing — moving it would split the contract across files.
const SOURCE_SCHEME: Record<ActivitySubjectKind, "unit" | "agent"> = {
  Unit: "unit",
  Agent: "agent",
};

// Cost-card window options for the agent sparkline toggle. Only used
// when `kind === "Agent"` — the unit Overview tab already carries its
// own cost-over-time card (DESIGN.md § 9.1), so we deliberately do not
// render a second one inside the unit Activity tab.
const AGENT_WINDOW_OPTIONS = [
  { label: "7d", window: "7d", bucket: "1d" },
  { label: "24h", window: "24h", bucket: "1h" },
] as const;

type AgentWindowOption = (typeof AGENT_WINDOW_OPTIONS)[number];

/**
 * True when an activity row carries an expandable structured payload —
 * either the raw `details` JSON returned by the REST query (#1665) or
 * any other non-empty object value the SSE stream might attach later.
 */
function hasDetails(item: ActivityQueryResult["items"][number]): boolean {
  const details = (item as { details?: unknown }).details;
  if (details == null) return false;
  if (typeof details !== "object") return true;
  return Object.keys(details as Record<string, unknown>).length > 0;
}

/**
 * Minimal inline sparkline (SVG polyline). Matches the BudgetSparkline
 * and StatSparkline aesthetic — no new charting library.
 */
function CostSparkline({
  points,
  testId,
}: {
  points: number[];
  testId: string;
}) {
  const max = Math.max(1, ...points);
  const width = 120;
  const height = 24;
  const step = points.length > 1 ? width / (points.length - 1) : 0;
  const svgPoints = points
    .map(
      (v, i) =>
        `${(i * step).toFixed(1)},${(height - (v / max) * height).toFixed(1)}`,
    )
    .join(" ");
  return (
    <svg
      aria-hidden="true"
      role="img"
      data-testid={testId}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="text-primary/70"
    >
      <polyline
        points={svgPoints}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/**
 * Agent-only cost cards: sparkline (#1363) + per-model breakdown
 * (#1364). Pulled out as its own component so the unit path doesn't
 * mount the `useAgentCost*` hooks at all — they would 404 against a
 * unit id. A unit-side breakdown endpoint doesn't exist today; if one
 * lands we can parameterise this on `kind` and drop the conditional.
 */
function AgentCostCards({ agentId }: { agentId: string }) {
  const [windowOpt, setWindowOpt] = useState<AgentWindowOption>(
    AGENT_WINDOW_OPTIONS[0],
  );

  const timeseriesQuery = useAgentCostTimeseries(
    agentId,
    windowOpt.window,
    windowOpt.bucket,
  );
  const breakdownQuery = useAgentCostBreakdown(agentId);

  const sparklinePoints =
    timeseriesQuery.data?.points?.map((p) => p.costUsd) ?? [];
  const breakdownEntries = breakdownQuery.data?.entries ?? [];

  return (
    <>
      {/* Cost over time sparkline — #1363 */}
      <Card data-testid="agent-cost-timeseries-card">
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm">
            <TrendingDown className="h-4 w-4" aria-hidden="true" /> Cost over time
          </CardTitle>
          <div className="flex gap-1" role="group" aria-label="Time window">
            {AGENT_WINDOW_OPTIONS.map((opt) => (
              <button
                key={opt.label}
                onClick={() => setWindowOpt(opt)}
                className={`rounded-md px-2 py-0.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
                  windowOpt.label === opt.label
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-pressed={windowOpt.label === opt.label}
                data-testid={`agent-timeseries-window-${opt.label}`}
              >
                {opt.label}
              </button>
            ))}
          </div>
        </CardHeader>
        <CardContent>
          {timeseriesQuery.isLoading ? (
            <Skeleton
              className="h-8 w-full"
              data-testid="agent-cost-timeseries-loading"
            />
          ) : sparklinePoints.length === 0 ||
            sparklinePoints.every((v) => v === 0) ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="agent-cost-timeseries-empty"
            >
              No cost data for this window.
            </p>
          ) : (
            <div className="flex items-end gap-4">
              <CostSparkline
                points={sparklinePoints}
                testId="agent-cost-sparkline"
              />
              <span className="text-xs text-muted-foreground tabular-nums">
                {formatCost(sparklinePoints.reduce((sum, v) => sum + v, 0))}{" "}
                total
              </span>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Model cost breakdown — #1364 (agent-only; no unit endpoint today) */}
      {breakdownEntries.length > 0 ? (
        <Card data-testid="agent-cost-breakdown-card">
          <CardHeader className="pb-2">
            <CardTitle className="flex items-center gap-2 text-sm">
              <DollarSign className="h-4 w-4" aria-hidden="true" /> Model cost breakdown
            </CardTitle>
          </CardHeader>
          <CardContent>
            <table className="w-full text-xs">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="pb-1 text-left font-medium">Model</th>
                  <th className="pb-1 text-left font-medium">Kind</th>
                  <th className="pb-1 text-right font-medium">Cost</th>
                  <th className="pb-1 text-right font-medium">Requests</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {breakdownEntries.map((entry) => (
                  <tr key={entry.key}>
                    <td className="py-1 pr-3 font-mono">{entry.key}</td>
                    <td className="py-1 pr-3 capitalize text-muted-foreground">
                      {entry.kind}
                    </td>
                    <td className="py-1 text-right tabular-nums">
                      {formatCost(entry.totalCost)}
                    </td>
                    <td className="py-1 text-right tabular-nums text-muted-foreground">
                      {entry.recordCount.toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>
      ) : null}
    </>
  );
}

/**
 * Subject-agnostic activity tab. Renders the canonical event feed
 * (REST baseline + SSE stream-driven invalidation) with expandable
 * structured-payload rows for both units and agents. When `kind`
 * is `"Agent"`, the agent-only cost sparkline and per-model breakdown
 * cards render above the feed.
 */
export function ActivityTab({ kind, id }: ActivityTabProps) {
  const scheme = SOURCE_SCHEME[kind];

  // REST baseline — paginated query for this subject's events. The
  // stream layered on top keeps it fresh (#437).
  const queryParams = { source: `${scheme}:${id}`, pageSize: "20" };
  const {
    data: result,
    error,
    isLoading,
    isFetching,
    refetch,
  } = useActivityQuery(queryParams);

  // Subscribe to the subject-scoped live stream so the tab updates as
  // events arrive — no more manual refresh loop. The hook invalidates
  // the matching cache slice on every event, so the `useActivityQuery`
  // above re-fetches and the list stays in order.
  useActivityStream({
    filter: (event) =>
      event.source.scheme === scheme && event.source.path === id,
  });

  const activityError = error ?? null;

  // Expanded-row tracker: clicking a row toggles its `id` in the set so
  // the structured `details` payload is shown inline. Kept in component
  // state (no URL persistence) — expansion is ephemeral context, not a
  // navigation surface.
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const toggleExpanded = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const emptyMessage =
    kind === "Agent"
      ? "No activity for this agent yet."
      : "No activity events for this unit.";

  return (
    <div className="space-y-4" data-testid={`tab-${scheme}-activity`}>
      {kind === "Agent" ? <AgentCostCards agentId={id} /> : null}

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" />
            Recent Activity
            <Button
              variant="ghost"
              size="sm"
              className="ml-auto"
              onClick={() => refetch()}
              disabled={isFetching}
            >
              <RefreshCw
                className={`h-3.5 w-3.5 ${isFetching ? "animate-spin" : ""}`}
              />
            </Button>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {activityError && (
            <div className="mb-3">
              <ApiErrorMessage error={activityError} />
            </div>
          )}
          {isLoading && !result ? (
            <p className="text-sm text-muted-foreground">Loading activity...</p>
          ) : (result as ActivityQueryResult | undefined)?.items.length === 0 ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid={`tab-${scheme}-activity-empty`}
            >
              {emptyMessage}
            </p>
          ) : (
            // `aria-live="polite"` so screen readers announce new events as
            // they stream in (portal design doc §7 — accessibility).
            <div className="space-y-0" aria-live="polite">
              {result?.items.map((e) => {
                const expandable = hasDetails(e);
                const isOpen = expanded.has(e.id);
                const detailsId = `activity-details-${e.id}`;
                return (
                  <div
                    key={e.id}
                    className="border-b border-border py-2 last:border-0 text-sm"
                    data-testid="activity-row"
                    data-event-id={e.id}
                  >
                    <div className="flex items-start gap-2">
                      {expandable ? (
                        <button
                          type="button"
                          aria-expanded={isOpen}
                          aria-controls={detailsId}
                          aria-label={
                            isOpen
                              ? "Collapse event details"
                              : "Expand event details"
                          }
                          onClick={() => toggleExpanded(e.id)}
                          data-testid="activity-row-toggle"
                          className="mt-0.5 inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        >
                          {isOpen ? (
                            <ChevronDown className="h-3.5 w-3.5" aria-hidden />
                          ) : (
                            <ChevronRight className="h-3.5 w-3.5" aria-hidden />
                          )}
                        </button>
                      ) : (
                        // Reserve the gutter so summaries align across
                        // expandable + non-expandable rows.
                        <span
                          aria-hidden
                          className="mt-0.5 inline-block h-5 w-5 shrink-0"
                        />
                      )}
                      <Badge
                        variant={
                          severityVariant[e.severity as ActivitySeverity] ??
                          "default"
                        }
                        className="mt-0.5 shrink-0"
                      >
                        {e.severity}
                      </Badge>
                      <div className="min-w-0 flex-1">
                        <p className="text-sm">{e.summary}</p>
                        <div className="mt-0.5 flex flex-wrap gap-2 text-xs text-muted-foreground">
                          <span>{humanEventType(e.eventType)}</span>
                          {e.cost != null && <span>${e.cost.toFixed(4)}</span>}
                          <span>{timeAgo(e.timestamp)}</span>
                        </div>
                      </div>
                    </div>
                    {expandable && isOpen && (
                      <pre
                        id={detailsId}
                        data-testid="activity-row-details"
                        className="mt-2 ml-12 max-h-64 overflow-auto rounded-md border border-border bg-muted/30 px-3 py-2 text-[11px] leading-relaxed text-foreground whitespace-pre-wrap break-words"
                      >
                        {JSON.stringify(
                          (e as { details?: unknown }).details,
                          null,
                          2,
                        )}
                      </pre>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
