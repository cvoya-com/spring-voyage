"use client";

// /budgets — tenant-wide budget rollup (SURF-reskin-budgets, #856). A
// visibility-anchor surface that presents the same numbers the
// dashboard's spend tile reports plus a per-unit drill-down so operators
// can see which units are closest to their cap. Edits themselves still
// land on `/analytics/costs` (the canonical budget editor) and on each
// unit's Policies → Cost tab — this page deliberately cross-links to
// them rather than duplicating the form.
//
// Design contract: plan §12 `SURF-reskin-budgets` — budget bar +
// sparkline matching the `Pages.jsx` budget card; per-unit drill-down
// via cross-link. Reuses the v2 `<CostSummaryCard>` for the today / 7d
// / 30d trio (which already adopted the `StatCard` aesthetic). The 30d
// sparkline rides the real tenant cost time-series endpoint
// (V21-tenant-cost-timeseries, #916) — the previous build synthesised
// it from the per-source breakdown because that endpoint didn't exist
// yet.

import Link from "next/link";

import { toExplorerPathSegment } from "@/lib/explorer-url";
import { ArrowRight, DollarSign, ExternalLink, Wallet } from "lucide-react";
import { useMemo } from "react";

import { CostSummaryCard } from "@/components/cards/cost-summary-card";
import { StatCard } from "@/components/stat-card";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  type ValidatedTenantTreeNode,
  useDashboardCosts,
  useDashboardUnits,
  useTenantBudget,
  useTenantCostTimeseries,
  useTenantTree,
} from "@/lib/api/queries";
import type { UnitDashboardSummary } from "@/lib/api/types";
import { cn, formatCost } from "@/lib/utils";

/** Index unit display names by the no-dash GUID source emitted by costs. */
function buildUnitNameById(
  tree: ValidatedTenantTreeNode | null | undefined,
): Map<string, string> {
  const byId = new Map<string, string>();
  const walk = (node: ValidatedTenantTreeNode) => {
    if (node.kind === "Unit") byId.set(node.id, node.name);
    for (const child of node.children ?? []) walk(child);
  };
  if (tree) walk(tree);
  return byId;
}

/**
 * Projects the tenant cost time-series endpoint (V21-tenant-cost-timeseries,
 * #916) down to the numeric array that `<CostSummaryCard>` and the
 * inline `<BudgetSparkline>` both consume. The endpoint always emits a
 * zero-filled, ordered series, so the projection is a trivial `.map` —
 * no bucketing or cumulative running total is needed (the server is
 * canonical for both). Returns `undefined` when the series is empty or
 * the endpoint errored, which the card renders as "no sparkline" rather
 * than a flat zero line.
 */
function seriesToCostPoints(
  payload:
    | { series: { cost: number }[] }
    | null
    | undefined,
): number[] | undefined {
  if (!payload || payload.series.length === 0) return undefined;
  return payload.series.map((b) => b.cost);
}

/**
 * Pill colour for a per-unit spend row. Green < 60% · yellow 60–90% ·
 * red > 90%. Mirrors the severity colouring on the `Pages.jsx` budget
 * card so operators can scan the list for the units closest to their
 * cap without reading every number.
 */
function utilizationVariant(
  pct: number | null,
): "success" | "warning" | "destructive" | "outline" {
  if (pct === null) return "outline";
  if (pct > 90) return "destructive";
  if (pct > 60) return "warning";
  return "success";
}

export default function BudgetsIndexPage() {
  const tenantBudget = useTenantBudget();
  const dashboardCosts = useDashboardCosts();
  const dashboardUnits = useDashboardUnits();
  const tenantTree = useTenantTree();
  // Real 30d tenant cost sparkline — feeds both the `<CostSummaryCard>`
  // footer and the budget-card's inline sparkline. Shares the cache slot
  // with downstream consumers (#910 analytics chart, #902 tenant-budgets
  // tile) via `queryKeys.tenant.costTimeseries(window, bucket)`.
  const tenantTimeseries = useTenantCostTimeseries("30d", "1d");

  const tenantCap = tenantBudget.data?.dailyBudget ?? null;
  const totalCost = dashboardCosts.data?.totalCost ?? null;
  const pct =
    tenantCap != null && tenantCap > 0 && totalCost != null
      ? Math.min(100, (totalCost / tenantCap) * 100)
      : null;

  const thirtyDaySeries = useMemo(
    () => seriesToCostPoints(tenantTimeseries.data),
    [tenantTimeseries.data],
  );
  const unitNameById = useMemo(
    () => buildUnitNameById(tenantTree.data),
    [tenantTree.data],
  );

  const unitRows = useMemo(() => {
    const units = dashboardUnits.data ?? [];
    const breakdown = dashboardCosts.data?.costsBySource ?? [];
    const byUnit = new Map<string, number>();
    for (const row of breakdown) {
      byUnit.set(row.source, (byUnit.get(row.source) ?? 0) + row.totalCost);
    }
    return units
      .map((u) => ({
        unit: u,
        displayName: unitNameById.get(u.name) ?? u.name,
        spend: byUnit.get(u.name) ?? 0,
      }))
      .sort((a, b) => b.spend - a.spend);
  }, [dashboardUnits.data, dashboardCosts.data, unitNameById]);

  const unitsLoading =
    dashboardUnits.isPending || dashboardCosts.isPending || tenantTree.isPending;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-2">
          <Wallet className="h-5 w-5 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-bold">Budgets</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          Tenant-wide and per-unit spend caps. Edit caps on{" "}
          <Link
            href="/analytics/costs"
            className="text-primary hover:underline"
          >
            Analytics · Costs
          </Link>
          , or drill into a unit to edit its Policies → Cost tab.
        </p>
      </div>

      {/* Tenant KPIs — same today / 7d / 30d trio the dashboard reports.
          The card already carries the optional `thirtyDaySeries` prop
          (#852), so the v2 sparkline lands the moment the card mounts. */}
      <CostSummaryCard thirtyDaySeries={thirtyDaySeries} />

      {/* Budget (24h) card — matches the `Pages.jsx` Dashboard budget
          card: big-number + fraction on the left, sparkline on the
          right, progress bar underneath. Visibility-only; the edit
          button cross-links to the canonical editor. */}
      <Card data-testid="budgets-tenant-card">
        <CardHeader className="flex flex-row items-start justify-between space-y-0 gap-3">
          <div>
            <CardTitle className="flex items-center gap-2 text-base">
              <DollarSign className="h-4 w-4" aria-hidden="true" />
              Budget (24h)
            </CardTitle>
            <p className="mt-1 text-xs text-muted-foreground">
              Daily cost ceiling across every agent and unit in this tenant.
            </p>
          </div>
          <Link
            href="/analytics/costs"
            className="inline-flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
            data-testid="budgets-edit-link"
          >
            Edit cap
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </Link>
        </CardHeader>
        <CardContent className="space-y-3">
          {tenantBudget.isPending || dashboardCosts.isPending ? (
            <Skeleton className="h-16 w-full" />
          ) : (
            <>
              <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
                <div>
                  <div className="text-3xl font-semibold tracking-tight tabular-nums">
                    {totalCost !== null ? formatCost(totalCost) : "—"}
                    {tenantCap !== null && (
                      <span className="ml-2 text-sm font-medium text-muted-foreground">
                        / {formatCost(tenantCap)}
                      </span>
                    )}
                  </div>
                  <div
                    className="mt-1 text-xs text-muted-foreground"
                    data-testid="budgets-tenant-pct"
                  >
                    {pct !== null
                      ? `${pct.toFixed(0)}% of daily cap`
                      : tenantCap === null
                        ? "No tenant cap set"
                        : "—"}
                  </div>
                </div>
                {thirtyDaySeries && thirtyDaySeries.length > 0 && (
                  <BudgetSparkline series={thirtyDaySeries} />
                )}
              </div>
              <BudgetBar pct={pct} />
            </>
          )}
        </CardContent>
      </Card>

      {/* Per-unit drill-down — lists every unit with today's spend so
          operators can see which are nearest to their cap. The row
          click-target navigates into the Explorer so the Cost policy
          edit lives on the unit's Policies → Cost tab. */}
      <Card data-testid="budgets-per-unit-card">
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Wallet className="h-4 w-4" aria-hidden="true" />
            Per-unit spend (24h)
            <span className="ml-auto text-xs font-normal text-muted-foreground">
              {unitRows.length} {unitRows.length === 1 ? "unit" : "units"}
            </span>
          </CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          {unitsLoading ? (
            <div className="p-4">
              <Skeleton className="h-24 w-full" />
            </div>
          ) : unitRows.length === 0 ? (
            <div className="p-8 text-center text-sm text-muted-foreground">
              No units yet. Create one to start tracking per-unit spend.
              <div className="mt-3">
                <Link
                  href="/units/create"
                  className="inline-flex items-center gap-1 rounded-md border border-input bg-background px-3 py-1.5 text-xs font-medium text-foreground hover:bg-accent hover:text-accent-foreground"
                >
                  Create a unit
                  <ArrowRight className="h-3 w-3" aria-hidden="true" />
                </Link>
              </div>
            </div>
          ) : (
            <ul
              className="divide-y divide-border"
              aria-label="Per-unit budgets"
            >
              {unitRows.map(({ unit, displayName, spend }) => (
                <UnitSpendRow
                  key={unit.name}
                  unit={unit}
                  displayName={displayName}
                  spend={spend}
                  tenantCap={tenantCap}
                />
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      {/* Secondary KPIs strip — fits the v2 `<StatCard>` aesthetic and
          gives a quick "is this tenant active?" read even before the
          per-unit list loads. */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <StatCard
          label="Units"
          value={unitRows.length}
          icon={<Wallet className="h-5 w-5" aria-hidden="true" />}
        />
        <StatCard
          label="Tenant cap"
          value={tenantCap !== null ? formatCost(tenantCap) : "—"}
          icon={<DollarSign className="h-5 w-5" aria-hidden="true" />}
        />
        <StatCard
          label="24h spend"
          value={totalCost !== null ? formatCost(totalCost) : "—"}
          icon={<DollarSign className="h-5 w-5" aria-hidden="true" />}
        />
      </div>
    </div>
  );
}

/**
 * Per-unit row — mono unit address on the left, pill-coloured
 * utilisation on the right, drill-down arrow into the Explorer. The
 * row itself is a Link so click-through is obvious.
 */
function UnitSpendRow({
  unit,
  displayName,
  spend,
  tenantCap,
}: {
  unit: UnitDashboardSummary;
  displayName: string;
  spend: number;
  tenantCap: number | null;
}) {
  const pct =
    tenantCap != null && tenantCap > 0
      ? Math.min(100, (spend / tenantCap) * 100)
      : null;
  const variant = utilizationVariant(pct);
  // #2473: canonical path-based URL; tab is still a query param.
  const href = `/explorer/units/${encodeURIComponent(toExplorerPathSegment(unit.name))}?tab=Policies`;

  return (
    <li>
      <Link
        href={href}
        className={cn(
          "flex items-center gap-3 px-4 py-3 text-sm transition-colors hover:bg-accent/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        )}
        data-testid={`budgets-unit-row-${unit.name}`}
      >
        <div className="min-w-0 flex-1 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium" title={unit.name}>
              {displayName}
            </span>
            <Badge variant={variant === "outline" ? "outline" : variant}>
              {pct !== null ? `${pct.toFixed(0)}% of cap` : formatCost(spend)}
            </Badge>
          </div>
          <BudgetBar pct={pct} compact />
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1 text-xs text-muted-foreground">
          <span className="font-mono tabular-nums">{formatCost(spend)}</span>
          <ArrowRight className="h-3 w-3" aria-hidden="true" />
        </div>
      </Link>
    </li>
  );
}

/** Progress bar ── 6px / 4px track + primary-tinted fill. */
function BudgetBar({
  pct,
  compact = false,
}: {
  pct: number | null;
  compact?: boolean;
}) {
  if (pct === null) return null;
  return (
    <div
      className={cn(
        "overflow-hidden rounded-full bg-muted",
        compact ? "h-1" : "h-1.5",
      )}
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      <div
        className={cn(
          "h-full transition-[width] duration-500",
          pct > 90
            ? "bg-destructive"
            : pct > 60
              ? "bg-warning"
              : "bg-primary",
        )}
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}

/** Inline SVG sparkline — matches the `CostSummaryCard` footer. */
function BudgetSparkline({ series }: { series: number[] }) {
  const max = Math.max(1, ...series);
  const width = 120;
  const height = 24;
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
      className="text-primary/70"
      data-testid="budgets-sparkline"
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
