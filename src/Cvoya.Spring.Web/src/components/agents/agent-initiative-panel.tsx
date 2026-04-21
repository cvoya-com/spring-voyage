"use client";

/**
 * Agent initiative editor (#934 / #815 §2 + §4).
 *
 * Lives inside the Agent Policies tab (the user explicitly chose the
 * "Policies" placement — symmetrical with the Unit surface). Reads
 * through `useAgentInitiativeLevel` + `useAgentInitiativePolicy` and
 * writes via `api.setAgentInitiativePolicy`. Mirrors the CLI
 * `spring agent initiative {level,policy} {get,set,clear}`.
 *
 * Clearing is modelled as "save an empty policy" — the server treats
 * an all-default `InitiativePolicy` as a logical clear and the next
 * GET returns `null`, which our hook surfaces as `null`. There is no
 * dedicated DELETE endpoint yet.
 */

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Zap } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import {
  useAgentInitiativeLevel,
  useAgentInitiativePolicy,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { InitiativeLevel, InitiativePolicy } from "@/lib/api/types";

const INITIATIVE_LEVELS: InitiativeLevel[] = [
  "Passive",
  "Attentive",
  "Proactive",
  "Autonomous",
];

/**
 * A policy is "effectively empty" when every slot is unset / default.
 * We treat saving one of these as a clear — nicer UX than asking the
 * operator to remember the distinction between "unset" and "all
 * defaults".
 */
function isEmptyPolicy(p: InitiativePolicy | null | undefined): boolean {
  if (!p) return true;
  const hasLevel = Boolean(p.maxLevel);
  const hasAllowed = Array.isArray(p.allowedActions) && p.allowedActions.length > 0;
  const hasBlocked = Array.isArray(p.blockedActions) && p.blockedActions.length > 0;
  return !hasLevel && !hasAllowed && !hasBlocked && !p.requireUnitApproval;
}

interface AgentInitiativePanelProps {
  agentId: string;
}

export function AgentInitiativePanel({ agentId }: AgentInitiativePanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const levelQuery = useAgentInitiativeLevel(agentId);
  const policyQuery = useAgentInitiativePolicy(agentId);

  const level = levelQuery.data;
  const policy = policyQuery.data;
  const [editing, setEditing] = useState(false);

  const save = useMutation({
    mutationFn: async (next: InitiativePolicy) => {
      await api.setAgentInitiativePolicy(agentId, next);
      return next;
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(
        queryKeys.agents.initiativePolicy(agentId),
        // Seed null when the save was a logical clear so the empty
        // state renders without a refetch round-trip.
        isEmptyPolicy(updated) ? null : updated,
      );
      queryClient.invalidateQueries({
        queryKey: queryKeys.agents.initiativeLevel(agentId),
      });
      toast({
        title: isEmptyPolicy(updated)
          ? "Initiative policy cleared"
          : "Initiative policy saved",
      });
      setEditing(false);
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const clear = () => {
    save.mutate({
      maxLevel: undefined,
      requireUnitApproval: false,
      allowedActions: null,
      blockedActions: null,
      tier1: policy?.tier1 ?? undefined,
      tier2: policy?.tier2 ?? undefined,
    });
  };

  if (policyQuery.isPending || levelQuery.isPending) {
    return <Skeleton className="h-32" data-testid="agent-initiative-loading" />;
  }

  const isSet = !isEmptyPolicy(policy);

  return (
    <Card data-testid="agent-initiative-panel">
      <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <Zap className="h-4 w-4" />
          <span>Initiative</span>
          {isSet ? null : (
            <Badge variant="outline" className="ml-2 text-xs font-normal">
              use defaults
            </Badge>
          )}
        </CardTitle>
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="outline"
            onClick={() => setEditing(true)}
            disabled={save.isPending}
          >
            Edit
          </Button>
          {isSet && (
            <Button
              size="sm"
              variant="ghost"
              onClick={clear}
              disabled={save.isPending}
              aria-label="Clear initiative policy"
            >
              Clear
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p className="text-xs text-muted-foreground">
          Per-agent autonomy policy. The unit Initiative overlay, when
          set, still applies on top — this surface controls just the
          agent&apos;s declared limits. Mirrors{" "}
          <code className="font-mono text-[11px]">
            spring agent initiative policy {`{get,set,clear}`}
          </code>
          .
        </p>
        <KvRow
          label="Current level (computed)"
          value={level?.level ?? null}
          muted={!level}
        />
        <KvRow label="Max level" value={policy?.maxLevel ?? null} />
        <KvRow
          label="Require unit approval"
          value={
            policy?.requireUnitApproval == null
              ? null
              : policy.requireUnitApproval
                ? "yes"
                : "no"
          }
        />
        <ListRow label="Allowed actions" items={policy?.allowedActions ?? null} />
        <ListRow label="Blocked actions" items={policy?.blockedActions ?? null} />
      </CardContent>

      {editing && (
        <AgentInitiativeDialog
          open
          initial={policy ?? null}
          onCancel={() => setEditing(false)}
          onSave={(next) => save.mutate(next)}
          saving={save.isPending}
        />
      )}
    </Card>
  );
}

function KvRow({
  label,
  value,
  muted,
}: {
  label: string;
  value: string | null | undefined;
  muted?: boolean;
}) {
  return (
    <div className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className={muted ? "text-xs text-muted-foreground" : "sm:text-right"}>
        {value ? (
          value
        ) : (
          <span className="text-xs text-muted-foreground">
            {muted ? "(unknown)" : "(unset)"}
          </span>
        )}
      </span>
    </div>
  );
}

function ListRow({
  label,
  items,
}: {
  label: string;
  items: readonly string[] | null;
}) {
  return (
    <div className="flex flex-col gap-1 sm:flex-row sm:items-start sm:justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className="flex flex-wrap justify-end gap-1">
        {items && items.length > 0 ? (
          items.map((s) => (
            <Badge key={s} variant="outline" className="font-mono text-xs">
              {s}
            </Badge>
          ))
        ) : (
          <span className="text-xs text-muted-foreground">(none)</span>
        )}
      </span>
    </div>
  );
}

function parseCsv(raw: string): string[] | null {
  const parts = raw
    .split(/[,\n]/)
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  return parts.length === 0 ? null : parts;
}

function formatCsv(values: readonly string[] | null | undefined): string {
  return values && values.length > 0 ? values.join(", ") : "";
}

function AgentInitiativeDialog({
  open,
  initial,
  onCancel,
  onSave,
  saving,
}: {
  open: boolean;
  initial: InitiativePolicy | null;
  onCancel: () => void;
  onSave: (slot: InitiativePolicy) => void;
  saving: boolean;
}) {
  const [maxLevel, setMaxLevel] = useState<InitiativeLevel | "">(
    initial?.maxLevel ?? "",
  );
  const [requireUnitApproval, setRequireUnitApproval] = useState<boolean>(
    initial?.requireUnitApproval ?? false,
  );
  const [allowedActions, setAllowedActions] = useState(
    formatCsv(initial?.allowedActions ?? null),
  );
  const [blockedActions, setBlockedActions] = useState(
    formatCsv(initial?.blockedActions ?? null),
  );

  const initialTiers = useMemo(
    () => ({ tier1: initial?.tier1 ?? undefined, tier2: initial?.tier2 ?? undefined }),
    [initial],
  );

  const handleSave = () => {
    onSave({
      maxLevel: maxLevel === "" ? undefined : (maxLevel as InitiativeLevel),
      requireUnitApproval,
      allowedActions: parseCsv(allowedActions),
      blockedActions: parseCsv(blockedActions),
      tier1: initialTiers.tier1,
      tier2: initialTiers.tier2,
    });
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title="Edit agent initiative policy"
      description="Autonomy ceiling and reflection action allow/block list for this agent. Leave everything blank to clear the policy."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      }
    >
      <div
        className="space-y-4 text-sm"
        data-testid="agent-initiative-dialog"
      >
        <label className="block space-y-1">
          <span className="text-muted-foreground">Max level</span>
          <select
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            value={maxLevel}
            onChange={(e) =>
              setMaxLevel(e.target.value as InitiativeLevel | "")
            }
            aria-label="Max level"
          >
            <option value="">(inherit)</option>
            {INITIATIVE_LEVELS.map((lvl) => (
              <option key={lvl} value={lvl}>
                {lvl}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={requireUnitApproval}
            onChange={(e) => setRequireUnitApproval(e.target.checked)}
          />
          <span>Require unit approval for initiative-triggered actions</span>
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Allowed actions (comma-separated)
          </span>
          <Input
            value={allowedActions}
            onChange={(e) => setAllowedActions(e.target.value)}
            placeholder="e.g. send-message, update-state"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Blocked actions (comma-separated)
          </span>
          <Input
            value={blockedActions}
            onChange={(e) => setBlockedActions(e.target.value)}
            placeholder="e.g. agent.spawn"
          />
        </label>
      </div>
    </Dialog>
  );
}
