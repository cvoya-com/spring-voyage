"use client";

// Costs tab for `/units/[id]` (PR-R4, #394). Renders two pieces:
//
//   1. A window breakdown bar showing total cost over the last 24h /
//      7d / 30d. This stands in for the sparkline #394 asks for until
//      the API exposes per-bucket time-series (#569).
//   2. A per-agent breakdown within the unit. Each row shows the
//      agent's total cost + a proportional bar relative to the top
//      spender. Data comes from fanning out `useAgentCostWindowed`
//      across the unit's memberships — no new endpoint, no duplicate
//      hook.
//
// The heading + cross-link to `/analytics/costs` preserves the
// cross-link contract from DESIGN.md § "Cross-links are required,
// not optional".

import { useMemo, useState } from "react";
import Link from "next/link";
import { useQueries, useQuery } from "@tanstack/react-query";
import { ArrowRight, DollarSign } from "lucide-react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import { useUnitCostWindowed } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  CostSummaryResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";
import { formatCost } from "@/lib/utils";

import { resolveCostWindows } from "@/components/cards/cost-summary-card";

interface CostsTabProps {
  unitId: string;
}

/**
 * The Costs tab. Mounts three windowed unit-cost queries (24h / 7d /
 * 30d) in parallel; the per-agent breakdown uses a 7d window by
 * default — it matches the CLI's default on `spring analytics costs`.
 */
export function CostsTab({ unitId }: CostsTabProps) {
  // Pin the windows once per mount so the TanStack cache keys are
  // stable — see the note in `CostSummaryCard` for why this matters.
  const [windows] = useState(() => resolveCostWindows());

  const day = useUnitCostWindowed(unitId, windows.today);
  const week = useUnitCostWindowed(unitId, windows.sevenDay);
  const month = useUnitCostWindowed(unitId, windows.thirtyDay);

  // List the unit's memberships so the per-agent breakdown rows are
  // a known, finite set. Failure (e.g., fresh unit with no
  // memberships) surfaces as an empty list, not an error.
  const membershipsQuery = useQuery({
    queryKey: queryKeys.units.memberships(unitId),
    queryFn: async (): Promise<UnitMembershipResponse[]> => {
      try {
        return await api.listUnitMemberships(unitId);
      } catch {
        return [];
      }
    },
    enabled: Boolean(unitId),
  });

  const memberships = useMemo(
    () => membershipsQuery.data ?? [],
    [membershipsQuery.data],
  );

  // Fan out one windowed agent-cost query per member. Keyed on the
  // agent address + the 7d window so each row caches independently.
  const agentCostQueries = useQueries({
    queries: memberships.map((m) => ({
      queryKey: [
        ...queryKeys.agents.cost(m.agentAddress),
        windows.sevenDay.from,
        windows.sevenDay.to,
      ] as const,
      queryFn: async (): Promise<CostSummaryResponse | null> => {
        try {
          return await api.getAgentCost(m.agentAddress, windows.sevenDay);
        } catch {
          return null;
        }
      },
      enabled: Boolean(m.agentAddress),
    })),
  });

  const agentRows = useMemo(() => {
    const rows = memberships.map((m, i) => ({
      address: m.agentAddress,
      cost: agentCostQueries[i]?.data ?? null,
      pending: agentCostQueries[i]?.isPending ?? true,
    }));
    return rows.sort((a, b) => {
      const av = a.cost?.totalCost ?? 0;
      const bv = b.cost?.totalCost ?? 0;
      return bv - av;
    });
  }, [memberships, agentCostQueries]);

  const agentMax = agentRows.reduce(
    (max, r) => Math.max(max, r.cost?.totalCost ?? 0),
    0,
  );

  const windowMax = Math.max(
    day.data?.totalCost ?? 0,
    week.data?.totalCost ?? 0,
    month.data?.totalCost ?? 0,
  );

  return (
    <div className="space-y-4" data-testid="unit-costs-tab">
      <Card>
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2">
            <DollarSign className="h-4 w-4" /> Spend by window
          </CardTitle>
          <Link
            href={`/analytics/costs?scope=unit&name=${encodeURIComponent(unitId)}`}
            className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
            data-testid="unit-costs-analytics-link"
          >
            Open in Analytics <ArrowRight className="h-3 w-3" />
          </Link>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-xs text-muted-foreground">
            Rolling totals for this unit. The per-bucket sparkline #394
            asks for lands once the cost API exposes a time-series
            (tracked in{" "}
            <Link
              href="https://github.com/cvoya-com/spring-voyage/issues/569"
              target="_blank"
              rel="noreferrer"
              className="text-primary hover:underline"
            >
              #569
            </Link>
            ); today the portal renders the windows the CLI already
            returns.
          </p>
          <WindowBar
            label="24h"
            query={day}
            windowMax={windowMax}
            testId="unit-costs-24h"
          />
          <WindowBar
            label="7d"
            query={week}
            windowMax={windowMax}
            testId="unit-costs-7d"
          />
          <WindowBar
            label="30d"
            query={month}
            windowMax={windowMax}
            testId="unit-costs-30d"
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <DollarSign className="h-4 w-4" /> Breakdown by agent (7d)
          </CardTitle>
        </CardHeader>
        <CardContent>
          {membershipsQuery.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-6 w-full" />
              <Skeleton className="h-6 w-full" />
            </div>
          ) : agentRows.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No agents belong to this unit yet.
            </p>
          ) : (
            <ul
              className="space-y-2 text-sm"
              data-testid="unit-costs-agent-breakdown"
            >
              {agentRows.map(({ address, cost, pending }) => {
                const value = cost?.totalCost ?? 0;
                const width = agentMax > 0 ? (value / agentMax) * 100 : 0;
                return (
                  <li key={address} className="space-y-1">
                    <div className="flex items-center justify-between gap-3">
                      <Link
                        href={`/agents/${encodeURIComponent(address)}`}
                        className="truncate font-mono text-xs text-primary hover:underline"
                      >
                        {address}
                      </Link>
                      <span className="tabular-nums">
                        {pending ? (
                          <Skeleton className="h-4 w-16" />
                        ) : cost ? (
                          formatCost(value)
                        ) : (
                          "—"
                        )}
                      </span>
                    </div>
                    <div
                      className="h-1.5 overflow-hidden rounded-full bg-muted"
                      aria-hidden="true"
                    >
                      <div
                        className="h-full bg-primary/70"
                        style={{ width: `${width}%` }}
                      />
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

interface WindowBarProps {
  label: string;
  query: {
    data?: CostSummaryResponse | null | undefined;
    isPending: boolean;
  };
  windowMax: number;
  testId: string;
}

function WindowBar({ label, query, windowMax, testId }: WindowBarProps) {
  const value = query.data?.totalCost ?? 0;
  const width = windowMax > 0 ? (value / windowMax) * 100 : 0;
  return (
    <div className="space-y-1" data-testid={testId}>
      <div className="flex items-center justify-between gap-3 text-xs">
        <span className="text-muted-foreground">{label}</span>
        <span className="tabular-nums font-medium">
          {query.isPending ? (
            <Skeleton className="inline-block h-4 w-16" />
          ) : query.data ? (
            formatCost(value)
          ) : (
            "—"
          )}
        </span>
      </div>
      <div
        className="h-2 overflow-hidden rounded-full bg-muted"
        aria-hidden="true"
      >
        <div
          className="h-full bg-primary/70"
          style={{ width: `${width}%` }}
        />
      </div>
    </div>
  );
}
