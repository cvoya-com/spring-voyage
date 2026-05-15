"use client";

import { useEffect, useState } from "react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { useSetUnitBudget, useUnitBudget } from "@/lib/api/queries";
import { formatTranslatedError } from "@/lib/api/translate-error";
import { formatCost } from "@/lib/utils";

interface UnitBudgetPanelProps {
  unitId: string;
}

export function UnitBudgetPanel({ unitId }: UnitBudgetPanelProps) {
  const { toast } = useToast();
  const budgetQuery = useUnitBudget(unitId);
  const save = useSetUnitBudget(unitId);

  const current = budgetQuery.data ?? null;
  const [input, setInput] = useState("");

  useEffect(() => {
    if (current && input === "") {
      setInput(current.dailyBudget.toString());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current]);

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
    save.mutate(value, {
      onSuccess: () => {
        toast({ title: "Unit budget saved" });
      },
      onError: (err) => {
        toast({
          title: "Failed to save budget",
          description: formatTranslatedError(err),
          variant: "destructive",
        });
      },
    });
  };

  const saving = save.isPending;

  if (budgetQuery.isPending) {
    return (
      <p
        className="text-xs text-muted-foreground"
        data-testid="unit-budget-loading"
      >
        Loading budget…
      </p>
    );
  }

  return (
    <div className="space-y-3" data-testid="unit-budget-panel">
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
            placeholder="e.g. 5.00"
            data-testid="unit-budget-input"
          />
        </label>
        <Button
          onClick={handleSave}
          disabled={saving}
          className="sm:w-24"
          data-testid="unit-budget-save"
        >
          {saving ? "Saving…" : "Save"}
        </Button>
      </div>
      <p
        className="text-xs text-muted-foreground"
        data-testid="unit-budget-current"
      >
        {current
          ? `Current: ${formatCost(current.dailyBudget)}/day`
          : "No unit budget set yet — member agents inherit the tenant envelope."}
      </p>
      <p className="text-xs text-muted-foreground">
        Mirrors{" "}
        <code className="font-mono text-[11px]">
          spring cost set-budget --scope unit --unit &lt;id&gt; --amount &lt;n&gt;
        </code>
        .
      </p>
    </div>
  );
}
