"use client";

// Costs tab of the Analytics surface (§ 5.4 / § 5.7 of
// `docs/design/portal-exploration.md`). Promoted from `/budgets` as part
// of the nav restructure (#444): cost rollups and budget configuration
// live together, and the Analytics surface gains Throughput and
// Wait-time peers. Old `/budgets` deep links 308-redirect here via the
// framework-level rule in `next.config.ts`.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import {
  useMutation,
  useQueries,
  useQueryClient,
} from "@tanstack/react-query";
import { DollarSign, Wallet } from "lucide-react";

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
import type { BudgetResponse } from "@/lib/api/types";
import { formatCost } from "@/lib/utils";

export default function AnalyticsCostsPage() {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const tenantQuery = useTenantBudget();
  const costsQuery = useDashboardCosts();
  const agentsQuery = useDashboardAgents();

  const tenantBudget = tenantQuery.data ?? null;
  const costs = costsQuery.data ?? null;
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
    costsQuery.isPending ||
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

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-32" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const tenantValue = Number(tenantInput);
  const utilization =
    Number.isFinite(tenantValue) && tenantValue > 0 && costs
      ? Math.min(100, (costs.totalCost / tenantValue) * 100)
      : null;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">Costs</h1>
        <p className="text-sm text-muted-foreground">
          Tenant and per-agent cost ceilings. Mirrors{" "}
          <code className="font-mono text-xs">spring cost summary</code>.
        </p>
      </div>

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
              {costs ? `Spend to date: ${formatCost(costs.totalCost)}` : ""}
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
    </div>
  );
}
