"use client";

// Tenant Budgets tab (EXP-tab-tenant, umbrella #815 §4, #902).
//
// Surfaces a richer breakdown than the original 24h tile:
//  - 24h total cost tile (unchanged).
//  - 7-day sparkline using the tenant cost time-series endpoint.
//  - Top-N units by 24h spend (from the dashboard costs breakdown).
//  - Cross-links to `/analytics/costs` and `/budgets`.

import { useMemo } from "react";
import Link from "next/link";
import { ArrowRight, DollarSign, TrendingUp, Wallet } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useDashboardCosts,
  useTenantCost,
  useTenantCostTimeseries,
} from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

// Maximum number of top units to surface in the tab.
const TOP_N = 5;

type SourceLookupNode = {
  id: string;
  name: string;
  kind: "Tenant" | "Unit" | "Agent";
  children?: SourceLookupNode[];
};

/** Index tree nodes by the no-dash GUID source emitted by costs. */
function buildSourceNodeById(
  tree: SourceLookupNode | null | undefined,
): Map<string, SourceLookupNode> {
  const byId = new Map<string, SourceLookupNode>();
  const walk = (node: SourceLookupNode) => {
    byId.set(node.id, node);
    for (const child of node.children ?? []) walk(child);
  };
  if (tree) walk(tree);
  return byId;
}

/** Project the time-series payload to the numeric array the sparkline needs. */
function seriesToPoints(
  payload: { series: { cost: number }[] } | null | undefined,
): number[] | undefined {
  if (!payload || payload.series.length === 0) return undefined;
  return payload.series.map((b) => b.cost);
}

/** Minimal inline SVG sparkline matching the `/budgets` page style. */
function CostSparkline({ series }: { series: number[] }) {
  const max = Math.max(1, ...series);
  const width = 100;
  const height = 20;
  const step = series.length > 1 ? width / (series.length - 1) : 0;
  const points = series
    .map(
      (v, i) =>
        `${(i * step).toFixed(1)},${(height - (v / max) * height).toFixed(1)}`,
    )
    .join(" ");
  return (
    <svg
      aria-hidden="true"
      role="img"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="shrink-0 text-primary/70"
      data-testid="tab-tenant-budgets-sparkline"
    >
      <polyline
        points={points}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function TenantBudgetsTab({ node }: TabContentProps) {
  // Last 24h window — matches the stat-tile window used elsewhere in the
  // Explorer so the number lines up with sibling surfaces. Hooks run
  // unconditionally — registry guarantees `kind === "Tenant"`.
  const range = useMemo(() => {
    const to = new Date();
    const from = new Date(to.getTime() - 24 * 60 * 60 * 1000);
    return { from: from.toISOString(), to: to.toISOString() };
  }, []);

  const { data: cost } = useTenantCost(range);
  // 7-day sparkline — shares the cache slot with `/budgets` when both
  // are mounted (queryKeys.tenant.costTimeseries("7d", "1d")).
  const timeseries = useTenantCostTimeseries("7d", "1d");
  const dashboardCosts = useDashboardCosts();
  const sourceNodeById = useMemo(() => buildSourceNodeById(node), [node]);

  const sevenDaySeries = useMemo(
    () => seriesToPoints(timeseries.data),
    [timeseries.data],
  );

  // Top-N units by 24h spend, sorted descending.
  const topUnits = useMemo(() => {
    const breakdown = dashboardCosts.data?.costsBySource ?? [];
    const byUnit = new Map<string, { name: string; spend: number }>();
    for (const row of breakdown) {
      const sourceNode = sourceNodeById.get(row.source);
      if (sourceNode && sourceNode.kind !== "Unit") continue;
      const current = byUnit.get(row.source);
      byUnit.set(row.source, {
        name: sourceNode?.name ?? row.source,
        spend: (current?.spend ?? 0) + row.totalCost,
      });
    }
    return Array.from(byUnit.entries())
      .map(([id, { name, spend }]) => ({ id, name, spend }))
      .sort((a, b) => b.spend - a.spend)
      .slice(0, TOP_N);
  }, [dashboardCosts.data, sourceNodeById]);

  if (node.kind !== "Tenant") return null;

  return (
    <Card data-testid="tab-tenant-budgets">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <DollarSign className="h-4 w-4" aria-hidden="true" /> Tenant budgets
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        {/* 24h total cost tile with optional 7-day sparkline */}
        {cost ? (
          <div className="flex items-center justify-between rounded-md border border-border p-3">
            <div>
              <p className="text-xs text-muted-foreground">Cost (last 24h)</p>
              <p className="text-2xl font-bold">
                {formatCost(cost.totalCost)}
              </p>
              <p className="mt-0.5 text-xs text-muted-foreground">
                {cost.recordCount} record{cost.recordCount === 1 ? "" : "s"}
              </p>
            </div>
            {sevenDaySeries && sevenDaySeries.length > 1 && (
              <div className="flex flex-col items-end gap-1">
                <span className="flex items-center gap-1 text-[10px] text-muted-foreground">
                  <TrendingUp className="h-3 w-3" aria-hidden="true" />
                  7 days
                </span>
                <CostSparkline series={sevenDaySeries} />
              </div>
            )}
          </div>
        ) : (
          <p
            className="text-muted-foreground"
            data-testid="tab-tenant-budgets-empty"
          >
            No tenant cost data yet.
          </p>
        )}

        {/* Top-N units by spend */}
        {dashboardCosts.isPending ? (
          <Skeleton className="h-20 w-full" data-testid="tab-tenant-budgets-units-loading" />
        ) : topUnits.length > 0 ? (
          <div data-testid="tab-tenant-budgets-top-units">
            <div className="mb-2 flex items-center gap-1 text-xs font-medium text-muted-foreground">
              <Wallet className="h-3 w-3" aria-hidden="true" />
              Top {topUnits.length === 1 ? "unit" : `${topUnits.length} units`} by 24h spend
            </div>
            <ul className="divide-y divide-border rounded-md border border-border">
              {topUnits.map(({ id, name, spend }) => (
                <li
                  key={id}
                  className="flex items-center justify-between px-3 py-1.5"
                  data-testid={`tab-tenant-budgets-unit-${id}`}
                >
                  <Badge variant="outline" className="text-[11px]" title={id}>
                    {name}
                  </Badge>
                  <span className="font-mono tabular-nums text-xs">
                    {formatCost(spend)}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {/* Cross-links */}
        <div className="flex flex-wrap gap-3 border-t border-border pt-2">
          <Link
            href="/analytics/costs"
            className="inline-flex items-center gap-1 text-primary hover:underline"
            data-testid="tab-tenant-budgets-costs-link"
          >
            Cost analytics
            <ArrowRight className="h-3 w-3" aria-hidden="true" />
          </Link>
          <Link
            href="/budgets"
            className="inline-flex items-center gap-1 text-primary hover:underline"
            data-testid="tab-tenant-budgets-link"
          >
            Budgets overview
            <ArrowRight className="h-3 w-3" aria-hidden="true" />
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}

registerTab("Tenant", "Budgets", TenantBudgetsTab);

export default TenantBudgetsTab;
