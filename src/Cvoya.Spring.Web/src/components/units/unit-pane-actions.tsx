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
import {
  Play,
  Plus,
  RefreshCw,
  Square,
  Trash2,
  CheckCircle2,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { useToast } from "@/components/ui/toast";
import { api, ApiError } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { useUnit } from "@/lib/api/queries";
import type { UnitStatus } from "@/lib/api/types";

// #1137: shape of the API's 409 conflict body for DELETE /units/{id}.
// Only `forceHint` is consumed by the recovery flow — its presence is the
// signal that the operator can re-run the delete with `?force=true`.
function isForceableConflict(err: unknown): boolean {
  if (!(err instanceof ApiError) || err.status !== 409) return false;
  const body = err.body;
  if (!body || typeof body !== "object") return false;
  const forceHint = (body as Record<string, unknown>).forceHint;
  return typeof forceHint === "string" && forceHint.length > 0;
}

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
  // #1137: when the lifecycle-status gate refuses the delete with 409 +
  // forceHint, surface a recovery dialog rather than silently dropping the
  // operator into "open the API docs and figure out ?force=true". Set the
  // flag and re-open the confirm with a force-flavoured copy.
  const [forceConfirmOpen, setForceConfirmOpen] = useState(false);

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
      // #1137: the API's lifecycle-status gate returns 409 with
      // forceHint for stuck units. Don't blow up with a generic
      // "Delete failed" toast — pivot the dialog into a force-delete
      // recovery prompt so the operator's only out isn't dropping to
      // curl. Any other error still falls through to the regular
      // toast.
      if (isForceableConflict(err)) {
        setConfirmOpen(false);
        setForceConfirmOpen(true);
        return;
      }
      onError("Delete")(err);
      setConfirmOpen(false);
    },
  });

  const forceDeleteMutation = useMutation({
    mutationFn: () => api.deleteUnit(node.id, { force: true }),
    onSuccess: () => {
      invalidate();
      toast({
        title: "Unit force-deleted",
        description: node.name,
      });
      setForceConfirmOpen(false);
      router.replace("/units");
    },
    onError: (err) => {
      onError("Force delete")(err);
      setForceConfirmOpen(false);
    },
  });

  const pending =
    validateMutation.isPending ||
    revalidateMutation.isPending ||
    startMutation.isPending ||
    stopMutation.isPending ||
    deleteMutation.isPending ||
    forceDeleteMutation.isPending;

  // #1150: "Create sub-unit" launches the existing /units/create wizard
  // with this unit pre-selected as the parent. The wizard reads the
  // `parent` query param at mount and threads `parentUnitIds` /
  // `isTopLevel: false` through the create-unit API call (see
  // `src/app/units/create/page.tsx`). The button is unconditional —
  // every unit can be a parent, regardless of its lifecycle status —
  // so we do not gate it on `status` like the lifecycle verbs above.
  // The action sits ahead of the Day-2 verbs in the cluster because
  // it's a creation flow, not a verb on the current unit.
  const onCreateSubunit = () => {
    router.push(`/units/create?parent=${encodeURIComponent(node.id)}`);
  };

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      data-testid="unit-pane-actions"
    >
      <Button
        variant="outline"
        size="sm"
        onClick={onCreateSubunit}
        data-testid="unit-action-create-subunit"
      >
        <Plus className="mr-1 h-4 w-4" aria-hidden="true" />
        Create sub-unit
      </Button>
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
      {/*
        #1137 recovery dialog. Only ever opens after a regular delete
        comes back 409 with forceHint, so the operator has already
        confirmed intent once. Copy is deliberately heavier than the
        primary dialog — force-delete skips the lifecycle gate and may
        leave external resources (containers, sidecars) behind for the
        host's best-effort teardown to clean up.
      */}
      <ConfirmDialog
        open={forceConfirmOpen}
        title={`Force-delete unit "${node.name}"?`}
        description={`The API refused the normal delete because the unit is in a non-terminal state (e.g. Validating, Starting, Running, Stopping). Force-delete bypasses the lifecycle gate and removes the unit from the directory; the host runs a best-effort teardown of container, sidecar, and connector resources. Use this for units stuck in an intermediate state.`}
        confirmLabel="Force delete"
        confirmVariant="destructive"
        pending={forceDeleteMutation.isPending}
        onConfirm={() => forceDeleteMutation.mutate()}
        onCancel={() => setForceConfirmOpen(false)}
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
