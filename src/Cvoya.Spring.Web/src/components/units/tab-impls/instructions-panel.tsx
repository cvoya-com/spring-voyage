"use client";

// Instructions sub-tab (#2293) — surfaces the persisted `instructions`
// slot on Agent and Unit definitions. Two write paths:
//
//   Save           → PATCH /tenant/{agents|units}/{id} with the textarea
//                    contents as the new value. Whitespace-only input is
//                    treated as a clear (server-side null).
//   Clear (button) → PATCH with explicit `instructions: null` so the
//                    server removes the key from the Definition JSON.
//
// The Agent variant overlays the owning unit's persisted instructions
// (when known) as italic placeholder text under the editor — matching
// the inherited-defaults treatment in `<AgentExecutionPanel>`.

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ScrollText } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useAgent, useUnit } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";

export interface InstructionsPanelProps {
  /** Subject this panel is bound to. */
  kind: "Agent" | "Unit";
  /** Stable id of the subject (unit Guid or agent Guid). */
  id: string;
  /**
   * For Agent only: the id of the agent's owning unit, when known.
   * Used to overlay the inherited unit-level instructions as a greyed
   * placeholder when the agent has no own value. Tenant defaults are
   * not surfaced — that wiring is tracked separately (see #2293's
   * scope notes on Tenant defaults).
   */
  parentUnitId?: string | null;
}

export function InstructionsPanel({
  kind,
  id,
  parentUnitId = null,
}: InstructionsPanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  // The Agent and Unit detail queries already surface `instructions`
  // from #2293's server work — we read whichever one matches `kind`
  // and bind the textarea to the persisted value.
  const agentQuery = useAgent(kind === "Agent" ? id : "", {
    enabled: kind === "Agent",
  });
  const unitQuery = useUnit(kind === "Unit" ? id : "", {
    enabled: kind === "Unit",
  });

  // Parent-unit fetch for the inherited-defaults overlay. Skipped for
  // Unit kind (no overlay) and when no parent is known.
  const parentUnitQuery = useUnit(parentUnitId ?? "", {
    enabled: kind === "Agent" && Boolean(parentUnitId),
  });

  const persisted =
    kind === "Agent"
      ? (agentQuery.data?.agent?.instructions ?? null)
      : (unitQuery.data?.instructions ?? null);

  const inherited =
    kind === "Agent" && !persisted
      ? (parentUnitQuery.data?.instructions ?? null)
      : null;

  const [draft, setDraft] = useState<string>("");
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = useMemo(() => `${kind}:${id}:${persisted ?? ""}`, [
    kind,
    id,
    persisted,
  ]);
  if (fingerprint !== seededFor) {
    setDraft(persisted ?? "");
    setSeededFor(fingerprint);
  }

  const isPending =
    kind === "Agent" ? agentQuery.isPending : unitQuery.isPending;

  const dirty = (draft.trim().length === 0 ? null : draft) !== persisted;

  const saveMutation = useMutation({
    mutationFn: async (
      next: string | null,
    ): Promise<void> => {
      if (kind === "Agent") {
        await api.updateAgentMetadata(id, { instructions: next });
      } else {
        await api.updateUnit(id, { instructions: next });
      }
    },
    onSuccess: () => {
      const detailKey =
        kind === "Agent"
          ? queryKeys.agents.detail(id)
          : queryKeys.units.detail(id);
      // The detail query is the source of truth for the persisted
      // instructions value — refetching it makes the textarea seed
      // from the new value on the next render via the fingerprint
      // check above.
      queryClient.invalidateQueries({ queryKey: detailKey });
      toast({ title: "Instructions saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  if (isPending) {
    return <Skeleton className="h-64" data-testid="instructions-skeleton" />;
  }

  const handleSave = () => {
    const trimmed = draft.trim();
    saveMutation.mutate(trimmed.length === 0 ? null : draft);
  };

  const handleClear = () => {
    setDraft("");
    saveMutation.mutate(null);
  };

  const subjectLabel = kind === "Agent" ? "agent" : "unit";

  return (
    <Card data-testid={`${subjectLabel}-instructions-panel`}>
      <CardHeader className="flex flex-row items-center gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <ScrollText className="h-4 w-4" />
          <span>Instructions</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-xs text-muted-foreground">
          {kind === "Agent"
            ? "The agent's own instructions. Leave blank to inherit the owning unit's value."
            : "The unit's instructions. Members that do not override this value inherit it at dispatch."}
        </p>
        <label
          htmlFor={`${subjectLabel}-instructions-textarea`}
          className="sr-only"
        >
          Instructions
        </label>
        <textarea
          id={`${subjectLabel}-instructions-textarea`}
          data-testid={`${subjectLabel}-instructions-textarea`}
          className="min-h-[200px] w-full rounded-md border border-input bg-background p-2 font-mono text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder={inherited ?? "(no instructions set)"}
          aria-describedby={
            inherited ? `${subjectLabel}-instructions-inherited` : undefined
          }
        />
        {inherited && !draft && (
          <p
            id={`${subjectLabel}-instructions-inherited`}
            data-testid={`${subjectLabel}-instructions-inherited`}
            className="text-xs italic text-muted-foreground"
          >
            Inherited from parent unit: {inherited}
          </p>
        )}
        <div className="flex items-center gap-2">
          <Button
            onClick={handleSave}
            disabled={!dirty || saveMutation.isPending}
            data-testid={`${subjectLabel}-instructions-save`}
          >
            Save
          </Button>
          {persisted !== null && (
            <Button
              variant="outline"
              onClick={handleClear}
              disabled={saveMutation.isPending}
              data-testid={`${subjectLabel}-instructions-clear`}
            >
              Clear
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
