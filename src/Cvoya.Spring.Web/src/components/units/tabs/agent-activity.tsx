"use client";

// Agent Activity tab (EXP-tab-agent-activity, umbrella #815 §4).
//
// Mirrors the unit activity tab's filter/render pipeline but scopes
// queries to `agent:<id>` so the stream + REST baseline only surface
// events produced by this agent. Tiny reimplementation (rather than
// adapter) because the legacy unit ActivityTab only accepts `unitId`.
//
// #1363 / #569 UI follow-up: adds a "Cost over time" sparkline card
// using GET /api/v1/tenant/analytics/agents/{id}/cost-timeseries.
// #1364 / #570 UI follow-up: adds a "Model cost breakdown" table
// using GET /api/v1/tenant/cost/agents/{id}/breakdown.

import { useState } from "react";
import { Activity, DollarSign, RefreshCw, TrendingDown } from "lucide-react";

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
import type { ActivitySeverity } from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

// Window options for the agent sparkline toggle.
const AGENT_WINDOW_OPTIONS = [
  { label: "7d", window: "7d", bucket: "1d" },
  { label: "24h", window: "24h", bucket: "1h" },
] as const;

type AgentWindowOption = (typeof AGENT_WINDOW_OPTIONS)[number];

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

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

function AgentActivityTab({ node }: TabContentProps) {
  // Hooks run unconditionally — the registry guarantees `kind === "Agent"`.
  const agentId = node.id;

  const [windowOpt, setWindowOpt] = useState<AgentWindowOption>(
    AGENT_WINDOW_OPTIONS[0],
  );

  const queryParams = { source: `agent:${agentId}`, pageSize: "20" };
  const {
    data: result,
    error,
    isLoading,
    isFetching,
    refetch,
  } = useActivityQuery(queryParams);

  useActivityStream({
    filter: (event) =>
      event.source.scheme === "agent" && event.source.path === agentId,
  });

  const timeseriesQuery = useAgentCostTimeseries(
    agentId,
    windowOpt.window,
    windowOpt.bucket,
  );
  const breakdownQuery = useAgentCostBreakdown(agentId);

  if (node.kind !== "Agent") return null;

  const errorMessage =
    error instanceof Error ? error.message : error ? String(error) : null;
  const events = result?.items ?? [];

  const sparklinePoints =
    timeseriesQuery.data?.points?.map((p) => p.costUsd) ?? [];
  const breakdownEntries = breakdownQuery.data?.entries ?? [];

  return (
    <div className="space-y-4" data-testid="tab-agent-activity">
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
                {formatCost(
                  sparklinePoints.reduce((sum, v) => sum + v, 0),
                )}{" "}
                total
              </span>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Model cost breakdown — #1364 */}
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

      {/* Activity event feed */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between space-y-0">
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" aria-hidden="true" /> Activity
          </CardTitle>
          <Button
            variant="outline"
            size="sm"
            onClick={() => refetch()}
            disabled={isFetching}
          >
            <RefreshCw
              className={`h-4 w-4 mr-1 ${isFetching ? "animate-spin" : ""}`}
              aria-hidden="true"
            />
            Refresh
          </Button>
        </CardHeader>
        <CardContent>
          {errorMessage ? (
            <p
              role="alert"
              className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
            >
              {errorMessage}
            </p>
          ) : isLoading ? (
            <p
              role="status"
              aria-live="polite"
              className="text-sm text-muted-foreground"
            >
              Loading activity…
            </p>
          ) : events.length === 0 ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="tab-agent-activity-empty"
            >
              No activity for this agent yet.
            </p>
          ) : (
            <ul className="divide-y divide-border text-sm">
              {events.map((event) => (
                <li key={event.id} className="flex items-start gap-3 py-2">
                  <Badge
                    variant={
                      severityVariant[event.severity as ActivitySeverity] ??
                      "default"
                    }
                    className="shrink-0"
                  >
                    {event.severity}
                  </Badge>
                  <span className="min-w-0 flex-1 truncate">
                    {event.summary}
                  </span>
                  <span className="shrink-0 text-xs text-muted-foreground">
                    {timeAgo(event.timestamp)}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

registerTab("Agent", "Activity", AgentActivityTab);

export default AgentActivityTab;
