"use client";

// Budget panel (Settings drawer / #451). CLI parity target:
// `spring cost set-budget --scope tenant --amount <n> --period daily`
// (PR #474). The form is a narrowed variant of the cost surface's
// tenant-budget card in /analytics/costs (PR-R5 / PR-S2): reads
// `GET /api/v1/tenant/budget`, writes `PUT /api/v1/tenant/budget`.

import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useTenantBudget } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatCost } from "@/lib/utils";

export function BudgetPanel() {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const tenantQuery = useTenantBudget();

  const tenantBudget = tenantQuery.data ?? null;
  const [input, setInput] = useState("");

  useEffect(() => {
    if (tenantBudget && input === "") {
      setInput(tenantBudget.dailyBudget.toString());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantBudget]);

  const saveTenantBudget = useMutation({
    mutationFn: (dailyBudget: number) => api.setTenantBudget({ dailyBudget }),
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

  const handleSave = () => {
    const value = Number(input);
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

  const saving = saveTenantBudget.isPending;

  if (tenantQuery.isPending) {
    return (
      <p className="text-xs text-muted-foreground">Loading tenant budget…</p>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
        <label className="block flex-1 space-y-1">
          <span className="text-xs text-muted-foreground">
            Daily budget (USD)
          </span>
          <Input
            type="number"
            inputMode="decimal"
            min="0"
            step="0.01"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="e.g. 50.00"
            data-testid="settings-budget-input"
          />
        </label>
        <Button
          onClick={handleSave}
          disabled={saving}
          className="sm:w-24"
          data-testid="settings-budget-save"
        >
          {saving ? "Saving…" : "Save"}
        </Button>
      </div>
      <p className="text-xs text-muted-foreground">
        {tenantBudget
          ? `Current: ${formatCost(tenantBudget.dailyBudget)}/day`
          : "No tenant budget set yet."}
      </p>
      <p className="text-xs text-muted-foreground">
        Mirrors{" "}
        <code className="font-mono text-[11px]">
          spring cost set-budget --scope tenant --amount &lt;n&gt; --period daily
        </code>
        .
      </p>
    </div>
  );
}
