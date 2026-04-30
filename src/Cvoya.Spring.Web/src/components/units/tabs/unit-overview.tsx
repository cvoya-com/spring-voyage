"use client";

// Unit Overview tab (EXP-tab-unit-overview, umbrella #815 §4).
//
// Shows stat tiles rolled up from the subtree via `aggregate(node)`:
// agents count, sub-unit count, 24h cost, 24h msgs, and the worst
// status in the subtree. The tiles deliberately stay lightweight — the
// richer drill-downs belong on the dedicated Agents / Activity / Policies
// tabs, which each ship their own registered content.
//
// #1363 / #569 UI follow-up: adds a "Cost over time" sparkline card
// using GET /api/v1/tenant/analytics/units/{id}/cost-timeseries
// (default window 7d / bucket 1d, toggle to 30d / 1d).

import { useState } from "react";
import Link from "next/link";
import { Activity, Bot, DollarSign, Layers, MessagesSquare, TrendingDown } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { StatCard } from "@/components/stat-card";
import { useUnitCostTimeseries } from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

import { aggregate, type UnitNode } from "../aggregate";
import { UnitOverviewExpertiseCard } from "../unit-overview-expertise-card";

import { registerTab, type TabContentProps } from "./index";

// Window options for the unit sparkline toggle.
const UNIT_WINDOW_OPTIONS = [
  { label: "7d", window: "7d", bucket: "1d" },
  { label: "30d", window: "30d", bucket: "1d" },
] as const;

type UnitWindowOption = (typeof UNIT_WINDOW_OPTIONS)[number];

/**
 * Minimal inline sparkline (SVG polyline). Matches the agent Activity tab
 * and the BudgetSparkline aesthetic — no new charting library.
 */
function UnitCostSparkline({
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

function UnitOverviewTab({ node }: TabContentProps) {
  // Hooks must run unconditionally — the kind guard is below.
  const [windowOpt, setWindowOpt] = useState<UnitWindowOption>(
    UNIT_WINDOW_OPTIONS[0],
  );
  // Use node.id for the timeseries query; disabled when kind !== "Unit" so
  // the hook never fires for non-unit nodes.
  const timeseriesQuery = useUnitCostTimeseries(
    node.id,
    windowOpt.window,
    windowOpt.bucket,
    { enabled: node.kind === "Unit" },
  );

  if (node.kind !== "Unit") return null;
  const unit = node as UnitNode;
  const roll = aggregate(unit);

  const sparklinePoints =
    timeseriesQuery.data?.points?.map((p) => p.costUsd) ?? [];

  return (
    <div className="space-y-4" data-testid="tab-unit-overview">
      {unit.desc ? (
        <p className="text-sm text-muted-foreground">{unit.desc}</p>
      ) : null}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <StatCard
          label="Agents"
          value={roll.agents}
          icon={<Bot className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Sub-units"
          value={Math.max(0, roll.units - 1)}
          icon={<Layers className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Cost (24h)"
          value={formatCost(roll.cost)}
          icon={<DollarSign className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Messages (24h)"
          value={roll.msgs.toLocaleString()}
          icon={<MessagesSquare className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Worst status"
          value={roll.worst}
          icon={<Activity className="h-4 w-4" aria-hidden="true" />}
        />
      </div>
      <div className="text-xs text-muted-foreground">
        Subtree roll-ups include this unit and every descendant. See the{" "}
        <Badge variant="outline">Agents</Badge> and{" "}
        <Badge variant="outline">Activity</Badge> tabs for drill-downs.
      </div>

      {/* Cost over time sparkline — #1363 */}
      <Card data-testid="unit-cost-timeseries-card">
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm">
            <TrendingDown className="h-4 w-4" aria-hidden="true" /> Cost over time
          </CardTitle>
          <div className="flex gap-1" role="group" aria-label="Time window">
            {UNIT_WINDOW_OPTIONS.map((opt) => (
              <button
                key={opt.label}
                onClick={() => setWindowOpt(opt)}
                className={`rounded-md px-2 py-0.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
                  windowOpt.label === opt.label
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-pressed={windowOpt.label === opt.label}
                data-testid={`unit-timeseries-window-${opt.label}`}
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
              data-testid="unit-cost-timeseries-loading"
            />
          ) : sparklinePoints.length === 0 ||
            sparklinePoints.every((v) => v === 0) ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="unit-cost-timeseries-empty"
            >
              No cost data for this window.
            </p>
          ) : (
            <div className="flex items-end gap-4">
              <UnitCostSparkline
                points={sparklinePoints}
                testId="unit-cost-sparkline"
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

      <UnitOverviewExpertiseCard unitId={unit.id} />

      {/* Cross-portal link to the engagement portal for this unit.
          Per ADR-0033 rule 6: cross-portal navigation is a standard anchor. */}
      <p className="text-xs text-muted-foreground" data-testid="unit-overview-engagement-link-row">
        <Link
          href={`/engagement/mine?unit=${encodeURIComponent(unit.id)}`}
          className="text-primary hover:underline"
          data-testid="unit-overview-engagement-link"
        >
          View engagements for this unit
        </Link>{" "}
        in the Engagement portal.
      </p>
    </div>
  );
}

registerTab("Unit", "Overview", UnitOverviewTab);

export default UnitOverviewTab;
