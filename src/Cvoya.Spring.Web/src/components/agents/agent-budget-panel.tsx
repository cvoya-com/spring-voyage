"use client";

/**
 * Agent daily-budget editor (#933 / #815 §4).
 *
 * Reads `GET /api/v1/agents/{id}/budget` via `useAgentBudget` and
 * writes through `api.setAgentBudget`. Mirrors the CLI
 * `spring agent budget {get,set}` — the request/response shapes are
 * the same SetBudgetRequest / BudgetResponse records used by the
 * tenant-wide Settings → Budgets surface, so the behaviour stays
 * consistent across scopes.
 *
 * Surfaces:
 *   - Empty state when `useAgentBudget` resolves to `null`
 *     (no per-agent envelope has been set). The CLI reports the same
 *     "(no agent budget set)" note.
 *   - Current value read-out ("$5.00/day") when populated.
 *   - Single numeric input + Save button. Zero / non-finite / negative
 *     input is blocked client-side; the server also rejects it but we
 *     prefer to fail fast so the toast is meaningful.
 */

import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useAgentBudget } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatCost } from "@/lib/utils";

interface AgentBudgetPanelProps {
  agentId: string;
}

export function AgentBudgetPanel({ agentId }: AgentBudgetPanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const budgetQuery = useAgentBudget(agentId);

  const current = budgetQuery.data ?? null;
  const [input, setInput] = useState("");

  // Seed the input with the persisted value the first time the query
  // resolves. Subsequent refetches shouldn't clobber an in-flight edit
  // — operators expect the textbox to behave like a draft until Save.
  useEffect(() => {
    if (current && input === "") {
      setInput(current.dailyBudget.toString());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current]);

  const save = useMutation({
    mutationFn: (dailyBudget: number) =>
      api.setAgentBudget(agentId, { dailyBudget }),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.agents.budget(agentId), updated);
      toast({ title: "Agent budget saved" });
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
    save.mutate(value);
  };

  const saving = save.isPending;

  if (budgetQuery.isPending) {
    return (
      <p
        className="text-xs text-muted-foreground"
        data-testid="agent-budget-loading"
      >
        Loading budget…
      </p>
    );
  }

  return (
    <div className="space-y-3" data-testid="agent-budget-panel">
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
            data-testid="agent-budget-input"
          />
        </label>
        <Button
          onClick={handleSave}
          disabled={saving}
          className="sm:w-24"
          data-testid="agent-budget-save"
        >
          {saving ? "Saving…" : "Save"}
        </Button>
      </div>
      <p
        className="text-xs text-muted-foreground"
        data-testid="agent-budget-current"
      >
        {current
          ? `Current: ${formatCost(current.dailyBudget)}/day`
          : "No agent budget set yet — the agent inherits the tenant envelope."}
      </p>
      <p className="text-xs text-muted-foreground">
        Mirrors{" "}
        <code className="font-mono text-[11px]">
          spring agent budget set --agent &lt;id&gt; --amount &lt;n&gt;
        </code>
        .
      </p>
    </div>
  );
}
