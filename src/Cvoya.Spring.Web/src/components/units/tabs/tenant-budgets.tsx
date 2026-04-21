"use client";

// Tenant Budgets tab (EXP-tab-tenant, umbrella #815 §4).
//
// Surfaces the tenant's 24h cost rollup plus a cross-link to the
// tenant-budget write surface (settings drawer today — the `/budgets`
// route is a V21 delivery).

import { useMemo } from "react";
import Link from "next/link";
import { ArrowRight, DollarSign } from "lucide-react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { useTenantCost } from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

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
  if (node.kind !== "Tenant") return null;

  return (
    <Card data-testid="tab-tenant-budgets">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <DollarSign className="h-4 w-4" aria-hidden="true" /> Tenant budgets
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {cost ? (
          <div className="flex items-center justify-between rounded-md border border-border p-3">
            <div>
              <p className="text-xs text-muted-foreground">Cost (last 24h)</p>
              <p className="text-2xl font-bold">
                {formatCost(cost.totalCost)}
              </p>
            </div>
            <div className="text-right text-xs text-muted-foreground">
              {cost.recordCount} record{cost.recordCount === 1 ? "" : "s"}
            </div>
          </div>
        ) : (
          <p
            className="text-muted-foreground"
            data-testid="tab-tenant-budgets-empty"
          >
            No tenant cost data yet.
          </p>
        )}
        <p className="text-xs text-muted-foreground">
          Set or update tenant budget ceilings from the settings drawer. The
          standalone `/budgets` surface lands in v2.1.
        </p>
        <Link
          href="/analytics/throughput"
          className="inline-flex items-center gap-1 text-primary hover:underline"
          data-testid="tab-tenant-budgets-link"
        >
          View cost trend
          <ArrowRight className="h-3 w-3" aria-hidden="true" />
        </Link>
      </CardContent>
    </Card>
  );
}

registerTab("Tenant", "Budgets", TenantBudgetsTab);

export default TenantBudgetsTab;
