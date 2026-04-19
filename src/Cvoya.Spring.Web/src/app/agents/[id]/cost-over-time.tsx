"use client";

// Agent-detail cost section (PR-R4, #394). The #394 acceptance list
// asks for a "cost-over-time chart + tool/model breakdown when
// present". The cost API does not expose either dimension today:
//
//   - Time-series per agent (tracked in #569). We render the 24h / 7d
//     / 30d totals as proportional bars — the CLI returns the same
//     windows so the portal stays 1:1 with `spring analytics costs
//     --agent <name>`.
//   - Tool / model breakdown (tracked in #570). The card renders a
//     short note pointing there until the endpoint lands.
//
// Reuses `useAgentCostWindowed` from the analytics query layer S2
// shipped (#560); no new client hooks, no new endpoints.

import { useState } from "react";
import Link from "next/link";
import { ArrowRight, DollarSign } from "lucide-react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { resolveCostWindows } from "@/components/cards/cost-summary-card";
import { useAgentCostWindowed } from "@/lib/api/queries";
import type { CostSummaryResponse } from "@/lib/api/types";
import { formatCost } from "@/lib/utils";

interface CostOverTimeCardProps {
  agentId: string;
}

export function CostOverTimeCard({ agentId }: CostOverTimeCardProps) {
  // Pin the windows once per mount — see the note in
  // `CostSummaryCard` for why this matters for TanStack cache keys.
  const [windows] = useState(() => resolveCostWindows());

  const day = useAgentCostWindowed(agentId, windows.today);
  const week = useAgentCostWindowed(agentId, windows.sevenDay);
  const month = useAgentCostWindowed(agentId, windows.thirtyDay);

  const windowMax = Math.max(
    day.data?.totalCost ?? 0,
    week.data?.totalCost ?? 0,
    month.data?.totalCost ?? 0,
  );

  return (
    <Card data-testid="agent-cost-over-time">
      <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2">
          <DollarSign className="h-4 w-4" /> Cost over time
        </CardTitle>
        <Link
          href={`/analytics/costs?scope=agent&name=${encodeURIComponent(agentId)}`}
          className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
          data-testid="agent-cost-analytics-link"
        >
          Open in Analytics <ArrowRight className="h-3 w-3" />
        </Link>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-muted-foreground">
          Rolling totals for this agent. Per-bucket sparklines and a
          tool / model breakdown arrive in a later release.
        </p>
        <WindowBar
          label="Last 24h"
          query={day}
          windowMax={windowMax}
          testId="agent-cost-24h"
        />
        <WindowBar
          label="Last 7d"
          query={week}
          windowMax={windowMax}
          testId="agent-cost-7d"
        />
        <WindowBar
          label="Last 30d"
          query={month}
          windowMax={windowMax}
          testId="agent-cost-30d"
        />
      </CardContent>
    </Card>
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
