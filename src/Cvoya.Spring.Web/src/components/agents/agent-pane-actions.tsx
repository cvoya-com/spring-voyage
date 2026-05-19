"use client";

// Explorer-pane header actions for agent nodes (#2372).
//
// Mirrors `src/Cvoya.Spring.Web/src/components/units/unit-pane-actions.tsx`
// for agents: surfaces the same Day-2 verbs the CLI ships under
// `spring agent {start,stop,revalidate,delete}` as buttons in the
// `<DetailPane>` header, status-gated against the live `LifecycleStatus`
// read from `useAgent(id)`. The backend exposes the matching verbs under
// `/api/v1/tenant/agents/{id}/{start,stop,revalidate}` (#2371). Verb
// labelling matches the agent CLI surface — `Start` renders as `Run` per
// the issue spec; the other labels (`Stop`, `Validate`, `Revalidate`,
// `Delete`) match the unit panel verbatim so the muscle-memory stays
// shared across kinds.
//
// The tenant-tree endpoint pins every node to `"running"` today (see
// `TenantTreeEndpoints.cs`), so this component reads live status from
// the per-agent endpoint — the same approach `UnitPaneActions` takes for
// units.

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  CheckCircle2,
  MessagesSquare,
  Play,
  RefreshCw,
  Square,
  Trash2,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import { queryKeys } from "@/lib/api/query-keys";
import { useAgent } from "@/lib/api/queries";
import type { LifecycleStatus } from "@/lib/api/types";

import type { AgentNode } from "@/components/units/aggregate";

interface AgentPaneActionsProps {
  node: AgentNode;
}

/**
 * Renders the header action cluster for an agent node in the Explorer
 * pane. The caller places it anywhere in the header layout; the
 * component owns its own mutation state, confirmation dialog, and
 * cache-invalidation logic. Behaviour mirrors `<UnitPaneActions>` for
 * units (#980, #2372).
 */
export function AgentPaneActions({ node }: AgentPaneActionsProps) {
  const { toast } = useToast();
  const router = useRouter();
  const queryClient = useQueryClient();
  // #2372: read the live LifecycleStatus from the per-agent endpoint —
  // the tenant-tree wire status for agents is the legacy "running"
  // pin until BuildAgentNode plumbs the real lifecycle through. Hitting
  // the detail endpoint matches what `spring agent start|stop|revalidate`
  // would accept.
  const agentQuery = useAgent(node.id);
  const status: LifecycleStatus | null =
    (agentQuery.data?.agent.lifecycleStatus as LifecycleStatus | null) ?? null;

  const [confirmOpen, setConfirmOpen] = useState(false);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: queryKeys.agents.detail(node.id) });
    // #2387: tenant.tree() is invalidated by the activity SSE handler
    // (`queryKeysAffectedBySource` for `agent://…`) — no manual call needed.
    queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
  };

  const onError = (verb: string) => (err: unknown) => {
    toast({
      title: `${verb} failed`,
      description: formatTranslatedError(err),
      variant: "destructive",
    });
  };

  const validateMutation = useMutation({
    mutationFn: () => api.revalidateAgent(node.id),
    onSuccess: invalidate,
    onError: onError("Validate"),
  });

  const revalidateMutation = useMutation({
    mutationFn: () => api.revalidateAgent(node.id),
    onSuccess: invalidate,
    onError: onError("Revalidate"),
  });

  const startMutation = useMutation({
    mutationFn: () => api.startAgent(node.id),
    onSuccess: invalidate,
    onError: onError("Start"),
  });

  const stopMutation = useMutation({
    mutationFn: () => api.stopAgent(node.id),
    onSuccess: invalidate,
    onError: onError("Stop"),
  });

  const deleteMutation = useMutation({
    mutationFn: () => api.deleteAgent(node.id),
    onSuccess: () => {
      invalidate();
      toast({ title: "Agent deleted", description: node.name });
      setConfirmOpen(false);
      router.replace("/explorer");
    },
    onError: (err) => {
      onError("Delete")(err);
      setConfirmOpen(false);
    },
  });

  const pending =
    validateMutation.isPending ||
    revalidateMutation.isPending ||
    startMutation.isPending ||
    stopMutation.isPending ||
    deleteMutation.isPending;

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      data-testid="agent-pane-actions"
    >
      {/* #1463 / #1464: open the engagement view for this agent — the
          {human, agent} 1:1 engagement, pre-selected on /engagement/mine
          via ?agent=<id>. */}
      <Button
        variant="outline"
        size="sm"
        onClick={() =>
          router.push("/engagement/mine?agent=" + encodeURIComponent(node.id))
        }
        data-testid="agent-action-engagement"
      >
        <MessagesSquare className="mr-1 h-4 w-4" aria-hidden="true" />
        Engagement
      </Button>
      {status === "Draft" && (
        <Button
          variant="default"
          size="sm"
          disabled={pending}
          onClick={() => validateMutation.mutate()}
          data-testid="agent-action-validate"
        >
          <CheckCircle2 className="mr-1 h-4 w-4" aria-hidden="true" />
          {validateMutation.isPending ? "Validating…" : "Validate"}
        </Button>
      )}
      {(status === "Error" || status === "Stopped") && (
        <Button
          variant="outline"
          size="sm"
          disabled={pending}
          onClick={() => revalidateMutation.mutate()}
          data-testid="agent-action-revalidate"
        >
          <RefreshCw className="mr-1 h-4 w-4" aria-hidden="true" />
          {revalidateMutation.isPending ? "Revalidating…" : "Revalidate"}
        </Button>
      )}
      {status === "Stopped" && (
        <Button
          variant="default"
          size="sm"
          disabled={pending}
          onClick={() => startMutation.mutate()}
          data-testid="agent-action-start"
        >
          <Play className="mr-1 h-4 w-4" aria-hidden="true" />
          {startMutation.isPending ? "Starting…" : "Run"}
        </Button>
      )}
      {status === "Running" && (
        <Button
          variant="outline"
          size="sm"
          disabled={pending}
          onClick={() => stopMutation.mutate()}
          data-testid="agent-action-stop"
        >
          <Square className="mr-1 h-4 w-4" aria-hidden="true" />
          {stopMutation.isPending ? "Stopping…" : "Stop"}
        </Button>
      )}
      <Button
        variant="destructive"
        size="sm"
        disabled={pending}
        onClick={() => setConfirmOpen(true)}
        data-testid="agent-action-delete"
      >
        <Trash2 className="mr-1 h-4 w-4" aria-hidden="true" />
        Delete
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Delete agent "${node.name}"?`}
        description="This removes the agent from the tenant and drops it from every unit membership. Activity history is preserved. This cannot be undone."
        confirmLabel="Permanently delete"
        confirmVariant="destructive"
        pending={deleteMutation.isPending}
        onConfirm={() => deleteMutation.mutate()}
        onCancel={() => setConfirmOpen(false)}
      />
    </div>
  );
}
