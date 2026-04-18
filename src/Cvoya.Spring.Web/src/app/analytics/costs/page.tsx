"use client";

// Costs tab of the Analytics surface (§ 5.4 / § 5.7 of
// `docs/design/portal-exploration.md`). Promoted from `/budgets` as part
// of the nav restructure (#444); PR-S2 (#448) expands this surface to
// carry a range picker, a unit/agent scope filter, and a per-source
// breakdown so it is a peer of the Throughput / Wait-time tabs rather
// than a bare budgets editor.
//
// Data sources:
//   - Tenant / unit / agent total: `/api/v1/costs/tenant|units/{id}|agents/{id}`
//     (CLI `spring analytics costs --window <w> [--unit|--agent]`).
//   - Per-source breakdown: `/api/v1/dashboard/costs` (CostDashboardSummary).
//     The CLI does not expose this breakdown today (#554).
//   - Tenant + per-agent budget config: unchanged from the original
//     `/budgets` page; `spring cost set-budget` is the CLI mirror (PR #474).
//
// Old `/budgets` deep links 308-redirect here via `next.config.ts`.

import { Suspense, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import {
  useMutation,
  useQueries,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import { DollarSign, TrendingUp, Wallet } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import {
  useDashboardAgents,
  useDashboardCosts,
  useTenantBudget,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  BudgetResponse,
  CostSummaryResponse,
} from "@/lib/api/types";
import { formatCost } from "@/lib/utils";

import {
  ANALYTICS_BREADCRUMBS,
  AnalyticsFiltersBar,
  useAnalyticsFilters,
} from "../analytics-filters";

function AnalyticsCostsContent() {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const filters = useAnalyticsFilters();

  const tenantQuery = useTenantBudget();
  // The dashboard cost summary includes a per-source breakdown the CLI
  // doesn't expose yet (tracked in #554); fetching it here keeps the
  // portal's Costs surface more expressive without blocking PR-S2.
  const dashboardCostsQuery = useDashboardCosts();
  const agentsQuery = useDashboardAgents();

  // Windowed scope summary — mirrors `spring analytics costs` exactly
  // (same endpoints, same (from, to) resolution, same filter switch).
  const scopedCostsQuery = useQuery<CostSummaryResponse | null, Error>({
    queryKey: [
      "analytics",
      "costs",
      "scoped",
      filters.scope,
      filters.from,
      filters.to,
    ] as const,
    queryFn: async () => {
      try {
        if (filters.scope.kind === "unit") {
          return await api.getUnitCost(filters.scope.name);
        }
        if (filters.scope.kind === "agent") {
          return await api.getAgentCost(filters.scope.name);
        }
        // The "all" scope reuses the tenant-wide summary we already load
        // for the dashboard header — no redundant network round-trip.
        return null;
      } catch {
        // Missing entity or no-data windows surface as null; the card
        // renders the empty state rather than bubbling to the boundary.
        return null;
      }
    },
    enabled:
      filters.scope.kind !== "all" && filters.scope.name.trim().length > 0,
  });

  const tenantBudget = tenantQuery.data ?? null;
  const dashboardCosts = dashboardCostsQuery.data ?? null;
  const agents = useMemo(
    () => agentsQuery.data ?? [],
    [agentsQuery.data],
  );

  const agentBudgetQueries = useQueries({
    queries: agents.map((agent) => ({
      queryKey: queryKeys.agents.budget(agent.name),
      queryFn: async (): Promise<BudgetResponse | null> => {
        try {
          return await api.getAgentBudget(agent.name);
        } catch {
          return null;
        }
      },
    })),
  });

  const agentRows = useMemo(
    () =>
      agents.map((agent, i) => ({
        agent,
        budget: agentBudgetQueries[i]?.data ?? null,
      })),
    [agents, agentBudgetQueries],
  );

  const loading =
    tenantQuery.isPending ||
    dashboardCostsQuery.isPending ||
    agentsQuery.isPending ||
    agentBudgetQueries.some((q) => q.isPending);

  const [tenantInput, setTenantInput] = useState("");

  useEffect(() => {
    if (tenantBudget && tenantInput === "") {
      setTenantInput(tenantBudget.dailyBudget.toString());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantBudget]);

  const saveTenantBudget = useMutation({
    mutationFn: (dailyBudget: number) =>
      api.setTenantBudget({ dailyBudget }),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.tenant.budget(), updated);
      toast({ title: "Tenant budget saved" });
    },
    onError: (err) => {
      toast({
        title: "Failed to save budget",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const handleSaveTenant = () => {
    const value = Number(tenantInput);
    if (!Number.isFinite(value) || value <= 0) {
      toast({
        title: "Invalid budget",
        description: "Daily budget must be greater than zero.",
        variant: "destructive",
      });
      return;
    }
    saveTenantBudget.mutate(value);
  };

  const savingTenant = saveTenantBudget.isPending;

  // Breakdown rows rendered as "bySource" bars. When the scope is
  // narrowed to a unit or agent we filter the dashboard breakdown so
  // only the matching rows remain — that matches the CLI-style "zoom
  // to this entity" story without a separate endpoint.
  const breakdownRows = useMemo(() => {
    const raw = dashboardCosts?.costsBySource ?? [];
    const scope = filters.scope;
    const filtered =
      scope.kind === "unit"
        ? raw.filter((r) => r.source === `unit://${scope.name}`)
        : scope.kind === "agent"
          ? raw.filter((r) => r.source === `agent://${scope.name}`)
          : raw;
    return [...filtered].sort((a, b) => b.totalCost - a.totalCost);
  }, [dashboardCosts, filters.scope]);
  const breakdownMax =
    breakdownRows.length > 0 ? breakdownRows[0].totalCost : 0;

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-32" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  // Tenant-level spend comes from the dashboard summary (all-time-ish,
  // matches what the header reports). The scoped card below reports the
  // windowed number for whichever scope is selected.
  const totalCost = dashboardCosts?.totalCost ?? 0;
  const tenantValue = Number(tenantInput);
  const utilization =
    Number.isFinite(tenantValue) && tenantValue > 0 && totalCost > 0
      ? Math.min(100, (totalCost / tenantValue) * 100)
      : null;

  const scopeHint = (() => {
    if (filters.scope.kind === "unit") {
      return `--unit ${filters.scope.name || "<name>"} `;
    }
    if (filters.scope.kind === "agent") {
      return `--agent ${filters.scope.name || "<name>"} `;
    }
    return "";
  })();

  const scopedSummary = scopedCostsQuery.data ?? null;

  return (
    <div className="space-y-6">
      <Breadcrumbs items={ANALYTICS_BREADCRUMBS.costs as never} />
      <div>
        <h1 className="text-2xl font-bold">Costs</h1>
        <p className="text-sm text-muted-foreground">
          Tenant-wide spend, per-source breakdown, and budget configuration.
        </p>
      </div>

      <AnalyticsFiltersBar
        windowValue={filters.window}
        onWindowChange={filters.setWindow}
        scope={filters.scope}
        onScopeChange={filters.setScope}
        hint={
          <>
            CLI:{" "}
            <code className="font-mono">
              spring analytics costs --window {filters.window} {scopeHint}
            </code>
          </>
        }
      />

      {filters.scope.kind !== "all" && filters.scope.name.trim().length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <TrendingUp className="h-4 w-4" /> Scoped total ({filters.scope.kind}:{" "}
              <span className="font-mono">{filters.scope.name}</span>)
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            {scopedCostsQuery.isPending ? (
              <Skeleton className="h-10 w-40" />
            ) : scopedSummary ? (
              <div className="flex flex-wrap items-end gap-6">
                <div>
                  <div className="text-xs text-muted-foreground">Total</div>
                  <div className="text-xl font-bold">
                    {formatCost(scopedSummary.totalCost)}
                  </div>
                </div>
                <div>
                  <div className="text-xs text-muted-foreground">Work</div>
                  <div className="text-sm">
                    {formatCost(scopedSummary.workCost)}
                  </div>
                </div>
                <div>
                  <div className="text-xs text-muted-foreground">Initiative</div>
                  <div className="text-sm">
                    {formatCost(scopedSummary.initiativeCost)}
                  </div>
                </div>
                <div>
                  <div className="text-xs text-muted-foreground">Records</div>
                  <div className="text-sm tabular-nums">
                    {scopedSummary.recordCount.toLocaleString()}
                  </div>
                </div>
                <Link
                  href={
                    filters.scope.kind === "unit"
                      ? `/units/${encodeURIComponent(filters.scope.name)}`
                      : `/agents/${encodeURIComponent(filters.scope.name)}`
                  }
                  className="ml-auto text-xs text-primary hover:underline"
                >
                  Open {filters.scope.kind} →
                </Link>
              </div>
            ) : (
              <p className="text-muted-foreground">
                No cost records for{" "}
                <span className="font-mono">
                  {filters.scope.kind}://{filters.scope.name}
                </span>{" "}
                in this window.
              </p>
            )}
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <TrendingUp className="h-4 w-4" /> Breakdown by source
          </CardTitle>
        </CardHeader>
        <CardContent>
          {breakdownRows.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No cost records in this window.
            </p>
          ) : (
            <ul className="space-y-2 text-sm">
              {breakdownRows.map((row) => {
                const idx = row.source.indexOf("://");
                const scheme = idx >= 0 ? row.source.slice(0, idx) : null;
                const name = idx >= 0 ? row.source.slice(idx + 3) : row.source;
                const href =
                  scheme === "unit"
                    ? `/units/${encodeURIComponent(name)}`
                    : scheme === "agent"
                      ? `/agents/${encodeURIComponent(name)}`
                      : null;
                const width =
                  breakdownMax > 0 ? (row.totalCost / breakdownMax) * 100 : 0;
                return (
                  <li key={row.source} className="space-y-1">
                    <div className="flex items-center justify-between gap-3">
                      <span className="truncate font-mono text-xs">
                        {href ? (
                          <Link
                            href={href}
                            className="text-primary hover:underline"
                          >
                            {row.source}
                          </Link>
                        ) : (
                          row.source
                        )}
                      </span>
                      <span className="tabular-nums">
                        {formatCost(row.totalCost)}
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
          <p className="mt-3 text-xs text-muted-foreground">
            The CLI <code className="font-mono">spring analytics costs</code>{" "}
            returns per-scope totals only; the per-source breakdown is
            portal-only today. CLI parity tracked in{" "}
            <Link
              href="https://github.com/cvoya-com/spring-voyage/issues/554"
              target="_blank"
              rel="noreferrer"
              className="text-primary hover:underline"
            >
              #554
            </Link>
            .
          </p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Wallet className="h-4 w-4" /> Tenant daily budget
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-xs text-muted-foreground">
            Cap across all agents and units. Per-agent budgets override this
            value for individual agents.
          </p>
          <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
            <label className="block flex-1 space-y-1">
              <span className="text-xs text-muted-foreground">
                Daily budget (USD)
              </span>
              <Input
                type="number"
                inputMode="decimal"
                min="0"
                step="0.01"
                value={tenantInput}
                onChange={(e) => setTenantInput(e.target.value)}
                placeholder="e.g. 50.00"
              />
            </label>
            <Button
              onClick={handleSaveTenant}
              disabled={savingTenant}
              className="sm:w-32"
            >
              {savingTenant ? "Saving…" : "Save"}
            </Button>
          </div>
          {utilization !== null && (
            <div>
              <div className="flex justify-between text-xs text-muted-foreground">
                <span>Utilization (period-to-date)</span>
                <span>{utilization.toFixed(1)}%</span>
              </div>
              <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-muted">
                <div
                  className="h-full bg-primary"
                  style={{ width: `${utilization}%` }}
                />
              </div>
            </div>
          )}
          <div className="flex justify-between text-xs text-muted-foreground">
            <span>
              {tenantBudget
                ? `Current: ${formatCost(tenantBudget.dailyBudget)}/day`
                : "No tenant budget set"}
            </span>
            <span>
              {dashboardCosts
                ? `Spend to date: ${formatCost(dashboardCosts.totalCost)}`
                : ""}
            </span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <DollarSign className="h-4 w-4" /> Per-agent budgets
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2">
          {agentRows.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No agents registered.
            </p>
          ) : (
            <div className="space-y-2">
              {agentRows.map(({ agent, budget }) => (
                <div
                  key={agent.name}
                  className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm sm:flex-row sm:items-center sm:justify-between"
                >
                  <div className="min-w-0">
                    <div className="truncate font-medium">
                      {agent.displayName}
                    </div>
                    <div className="truncate text-xs text-muted-foreground">
                      {agent.name}
                    </div>
                  </div>
                  <div className="flex items-center gap-3">
                    <span className="text-xs text-muted-foreground">
                      {budget
                        ? `${formatCost(budget.dailyBudget)}/day`
                        : "Not set"}
                    </span>
                    <Link href={`/agents/${encodeURIComponent(agent.name)}`}>
                      <Button size="sm" variant="outline">
                        Configure
                      </Button>
                    </Link>
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      <div className="flex flex-wrap gap-3 text-xs text-muted-foreground">
        <Link
          href="/policies"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Policies (cost caps, model, execution) →
        </Link>
        <Link
          href="/analytics/throughput"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Throughput →
        </Link>
        <Link
          href="/analytics/waits"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Wait times →
        </Link>
      </div>
    </div>
  );
}

/**
 * The filter state rides on `useSearchParams`, which in the App Router
 * forces the page to declare a Suspense boundary — otherwise the
 * production build refuses to prerender the route. The boundary also
 * acts as the skeleton for the initial (no-data-yet) render.
 */
export default function AnalyticsCostsPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <AnalyticsCostsContent />
    </Suspense>
  );
}
