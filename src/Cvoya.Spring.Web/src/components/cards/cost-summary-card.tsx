"use client";

// Dashboard cost summary card (PR-R4, #394 — the "total cost today /
// 7d / 30d" acceptance bullet). Reuses `useTenantCost` from the
// analytics query layer S2 shipped (#560) — three calls keyed by
// distinct `(from, to)` windows so the cache slices don't collide.
//
// The card is read-only on purpose: per the PR-R4 scope note, budget
// editing lives on `/analytics/costs`, and this card just links there.
//
// Reskinned for the v2 design system (plan §7 / CARD-cost-summary-
// refresh, #852): each tile is a `StatCard` — label on top, mono
// tabular-nums value below, optional sparkline footer. The 30d tile
// carries the sparkline by default because it's the most informative
// trend window.

import { useState } from "react";
import Link from "next/link";
import { ArrowRight, DollarSign } from "lucide-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useTenantCost } from "@/lib/api/queries";
import { cn, formatCost } from "@/lib/utils";

/**
 * Computes the canonical "today / 7d / 30d" window boundaries the
 * summary card and the `/analytics/costs` page both read. Each tuple
 * is an inclusive `(from, to)` pair the server resolves verbatim.
 * "Today" runs from UTC midnight → now; "7d" and "30d" are rolling
 * windows ending at now, matching the CLI defaults.
 */
export function resolveCostWindows(now: Date = new Date()): {
  today: { from: string; to: string };
  sevenDay: { from: string; to: string };
  thirtyDay: { from: string; to: string };
} {
  const to = now.toISOString();
  const midnight = new Date(
    Date.UTC(
      now.getUTCFullYear(),
      now.getUTCMonth(),
      now.getUTCDate(),
      0,
      0,
      0,
      0,
    ),
  );
  const sevenDay = new Date(now);
  sevenDay.setUTCDate(sevenDay.getUTCDate() - 7);
  const thirtyDay = new Date(now);
  thirtyDay.setUTCDate(thirtyDay.getUTCDate() - 30);
  return {
    today: { from: midnight.toISOString(), to },
    sevenDay: { from: sevenDay.toISOString(), to },
    thirtyDay: { from: thirtyDay.toISOString(), to },
  };
}

export interface SpendStatTileProps {
  label: string;
  value: number | null;
  pending: boolean;
  series?: number[];
  testId: string;
  className?: string;
}

/**
 * Cost-specific stat tile used inside `CostSummaryCard`. Label on
 * top, mono tabular-nums USD value underneath, optional sparkline
 * footer — matches the StatCard aesthetic from the design kit
 * without colliding with the generic `<StatCard>` primitive at
 * `src/components/stat-card.tsx`. Exported so the Tenant Budgets
 * tab (per `EXP-tab-tenant`) and `/budgets` can reuse the shape.
 */
export function SpendStatTile({
  label,
  value,
  pending,
  series,
  testId,
  className,
}: SpendStatTileProps) {
  return (
    <div
      className={cn(
        "flex flex-col gap-1 rounded-md border border-border bg-background/40 p-3",
        className,
      )}
      data-testid={testId}
    >
      <span className="text-xs text-muted-foreground">{label}</span>
      {pending ? (
        <Skeleton className="h-7 w-20" />
      ) : (
        <span
          className="font-mono text-xl font-semibold tabular-nums"
          data-testid={`${testId}-value`}
        >
          {value === null ? "—" : formatCost(value)}
        </span>
      )}
      {series && series.length > 0 && (
        <StatSparkline series={series} testId={`${testId}-sparkline`} />
      )}
    </div>
  );
}

/**
 * Minimal inline sparkline (SVG polyline) for the StatCard footer.
 * Purely decorative — aria-hidden — because the numeric value above
 * carries the accessible summary. Matches the `UnitCard` sparkline
 * aesthetic so all v2 cards share one visual language.
 */
function StatSparkline({
  series,
  testId,
}: {
  series: number[];
  testId: string;
}) {
  const max = Math.max(1, ...series);
  const width = 80;
  const height = 16;
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
      data-testid={testId}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="mt-1 text-primary/70"
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

export interface CostSummaryCardProps {
  /**
   * Optional spend history for the 30d sparkline. Each element is a
   * per-bucket USD total ordered oldest → newest. The Dashboard wires
   * the series once the tenant cost timeseries endpoint lands; until
   * then, the card renders the StatCard without a sparkline and the
   * existing trio of numbers stays the primary signal.
   */
  thirtyDaySeries?: number[];
}

/**
 * Read-only spend summary for the main dashboard. Spends are computed
 * server-side via `GET /api/v1/costs/tenant?from&to`, so this card
 * never diverges from what `/analytics/costs` reports.
 */
export function CostSummaryCard({ thirtyDaySeries }: CostSummaryCardProps = {}) {
  // Pin the windows at mount. Re-computing on every render would drift
  // the `to = now` ISO string by milliseconds and bust the TanStack
  // cache keys. The activity stream invalidates `queryKeys.dashboard.all`
  // on every cost event (see `queryKeysAffectedBySource`), which
  // includes these tenant slices, so we don't need to re-resolve the
  // window to pick up new data — TanStack's own invalidation does it.
  const [windows] = useState(() => resolveCostWindows());

  const today = useTenantCost(windows.today);
  const sevenDay = useTenantCost(windows.sevenDay);
  const thirtyDay = useTenantCost(windows.thirtyDay);

  return (
    <Card
      data-testid="cost-summary-card"
      className="relative transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2"
    >
      <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <DollarSign className="h-4 w-4" aria-hidden="true" /> Spend
        </CardTitle>
        {/*
          Full-card overlay link (#593). The `Details` link expands via
          an `::after` pseudo-element so every surface area of the
          summary card navigates to `/analytics/costs` on click. There
          are no other interactive descendants to promote — the three
          stat tiles are pure display.
        */}
        <Link
          href="/analytics/costs"
          aria-label="Open spend details"
          className="inline-flex items-center gap-1 text-xs text-primary focus-visible:outline-none hover:underline after:absolute after:inset-0 after:content-['']"
          data-testid="cost-summary-link"
        >
          Details <ArrowRight className="h-3 w-3" aria-hidden="true" />
        </Link>
      </CardHeader>
      <CardContent className="grid grid-cols-3 gap-3">
        <SpendStatTile
          label="Today"
          value={today.data?.totalCost ?? null}
          pending={today.isPending}
          testId="cost-summary-today"
        />
        <SpendStatTile
          label="Last 7d"
          value={sevenDay.data?.totalCost ?? null}
          pending={sevenDay.isPending}
          testId="cost-summary-7d"
        />
        <SpendStatTile
          label="Last 30d"
          value={thirtyDay.data?.totalCost ?? null}
          pending={thirtyDay.isPending}
          series={thirtyDaySeries}
          testId="cost-summary-30d"
        />
      </CardContent>
    </Card>
  );
}
