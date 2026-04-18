"use client";

// Dashboard cost summary card (PR-R4, #394 — the "total cost today /
// 7d / 30d" acceptance bullet). Reuses `useTenantCost` from the
// analytics query layer S2 shipped (#560) — three calls keyed by
// distinct `(from, to)` windows so the cache slices don't collide.
//
// The card is read-only on purpose: per the PR-R4 scope note, budget
// editing lives on `/analytics/costs`, and this card just links there.

import { useState } from "react";
import Link from "next/link";
import { ArrowRight, DollarSign } from "lucide-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useTenantCost } from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

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

interface CostTileProps {
  label: string;
  value: number | null;
  pending: boolean;
  testId: string;
}

function CostTile({ label, value, pending, testId }: CostTileProps) {
  return (
    <div
      className="flex flex-col gap-1 rounded-md border border-border bg-background/40 p-3"
      data-testid={testId}
    >
      <span className="text-xs text-muted-foreground">{label}</span>
      {pending ? (
        <Skeleton className="h-7 w-20" />
      ) : (
        <span className="text-xl font-bold tabular-nums">
          {value === null ? "—" : formatCost(value)}
        </span>
      )}
    </div>
  );
}

/**
 * Read-only spend summary for the main dashboard. Spends are
 * computed server-side via `GET /api/v1/costs/tenant?from&to`, so this
 * card never diverges from what `/analytics/costs` reports.
 */
export function CostSummaryCard() {
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
          Full-card overlay link (#593). The `Details` link expands via an
          `::after` pseudo-element so every surface area of the summary
          card navigates to `/analytics/costs` on click. There are no
          other interactive descendants to promote — the three cost tiles
          are pure display.
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
        <CostTile
          label="Today"
          value={today.data?.totalCost ?? null}
          pending={today.isPending}
          testId="cost-summary-today"
        />
        <CostTile
          label="Last 7d"
          value={sevenDay.data?.totalCost ?? null}
          pending={sevenDay.isPending}
          testId="cost-summary-7d"
        />
        <CostTile
          label="Last 30d"
          value={thirtyDay.data?.totalCost ?? null}
          pending={thirtyDay.isPending}
          testId="cost-summary-30d"
        />
      </CardContent>
    </Card>
  );
}
