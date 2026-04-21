"use client";

// Explorer pane header actions (#980 item 3).
//
// Surfaces the Day-2 verbs the CLI already ships — `spring unit
// start|stop|revalidate|delete` and `spring agent delete` — as buttons
// in the `<DetailPane>` header. Each action is status-gated so the user
// only sees the verbs that apply to the unit's current lifecycle state;
// Delete is always available and goes through a confirmation dialog.
//
// Agent lifecycle: only `Delete` ships on this surface today. `start` /
// `stop` have no CLI equivalent for agents — follow-ups are filed as
// separate issues rather than silently expanding scope.
//
// The component reads live unit status via `useUnit(id)` because the
// tenant-tree endpoint pins every node to `"running"` (see
// `TenantTreeEndpoints.cs`). Hitting the real per-unit endpoint is the
// only way to know whether `Start` or `Revalidate` is the correct verb.

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Play, RefreshCw, Square, Trash2, CheckCircle2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { useUnit } from "@/lib/api/queries";
import type { UnitStatus } from "@/lib/api/types";

import type { TreeNode } from "./aggregate";

interface UnitPaneActionsProps {
  node: TreeNode;
}

/**
 * Renders the header action cluster for the Explorer pane. The caller
 * places it anywhere in the header layout; the component owns its own
 * mutation state, confirmation dialog, and cache-invalidation logic.
 */
export function UnitPaneActions({ node }: UnitPaneActionsProps) {
  if (node.kind === "Unit") {
    return <UnitActions node={node} />;
  }
  if (node.kind === "Agent") {
    return <AgentActions id={node.id} name={node.name} />;
  }
  return null;
}

function UnitActions({ node }: { node: TreeNode }) {
  const { toast } = useToast();
  const router = useRouter();
  const queryClient = useQueryClient();
  // The tenant-tree endpoint pins status to "running" for every unit
  // today — read the real status from the per-unit endpoint so the
  // gate matches what `spring unit start|stop|…` would accept.
  const unitQuery = useUnit(node.id);
  const status: UnitStatus | null = unitQuery.data?.status ?? null;

  const [confirmOpen, setConfirmOpen] = useState(false);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: queryKeys.units.detail(node.id) });
    queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
    queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
  };

  const onError = (verb: string) => (err: unknown) => {
    toast({
      title: `${verb} failed`,
      description: err instanceof Error ? err.message : String(err),
      variant: "destructive",
    });
  };

  const validateMutation = useMutation({
    mutationFn: () => api.revalidateUnit(node.id),
    onSuccess: invalidate,
    onError: onError("Validate"),
  });

  const revalidateMutation = useMutation({
    mutationFn: () => api.revalidateUnit(node.id),
    onSuccess: invalidate,
    onError: onError("Revalidate"),
  });

  const startMutation = useMutation({
    mutationFn: () => api.startUnit(node.id),
    onSuccess: invalidate,
    onError: onError("Start"),
  });

  const stopMutation = useMutation({
    mutationFn: () => api.stopUnit(node.id),
    onSuccess: invalidate,
    onError: onError("Stop"),
  });

  const deleteMutation = useMutation({
    mutationFn: () => api.deleteUnit(node.id),
    onSuccess: () => {
      invalidate();
      toast({
        title: "Unit deleted",
        description: node.name,
      });
      setConfirmOpen(false);
      // Drop the query-string selection so the pane doesn't keep
      // showing a node the tree just lost.
      router.replace("/units");
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
      data-testid="unit-pane-actions"
    >
      {status === "Draft" && (
        <Button
          variant="default"
          size="sm"
          disabled={pending}
          onClick={() => validateMutation.mutate()}
          data-testid="unit-action-validate"
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
          data-testid="unit-action-revalidate"
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
          data-testid="unit-action-start"
        >
          <Play className="mr-1 h-4 w-4" aria-hidden="true" />
          {startMutation.isPending ? "Starting…" : "Start"}
        </Button>
      )}
      {status === "Running" && (
        <Button
          variant="outline"
          size="sm"
          disabled={pending}
          onClick={() => stopMutation.mutate()}
          data-testid="unit-action-stop"
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
        data-testid="unit-action-delete"
      >
        <Trash2 className="mr-1 h-4 w-4" aria-hidden="true" />
        Delete
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Delete unit "${node.name}"?`}
        description="This removes the unit from the tenant. Running members are stopped; activity history is preserved. This cannot be undone."
        confirmLabel="Permanently delete"
        confirmVariant="destructive"
        pending={deleteMutation.isPending}
        onConfirm={() => deleteMutation.mutate()}
        onCancel={() => setConfirmOpen(false)}
      />
    </div>
  );
}

function AgentActions({ id, name }: { id: string; name: string }) {
  const { toast } = useToast();
  const router = useRouter();
  const queryClient = useQueryClient();

  const [confirmOpen, setConfirmOpen] = useState(false);

  const deleteMutation = useMutation({
    mutationFn: () => api.deleteAgent(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.agents.detail(id) });
      queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
      queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      toast({ title: "Agent deleted", description: name });
      setConfirmOpen(false);
      router.replace("/units");
    },
    onError: (err) => {
      toast({
        title: "Delete failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
      setConfirmOpen(false);
    },
  });

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      data-testid="agent-pane-actions"
    >
      <Button
        variant="destructive"
        size="sm"
        disabled={deleteMutation.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="agent-action-delete"
      >
        <Trash2 className="mr-1 h-4 w-4" aria-hidden="true" />
        Delete
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Delete agent "${name}"?`}
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
