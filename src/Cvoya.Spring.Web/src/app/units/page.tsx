"use client";

import { Suspense, useState } from "react";
import { useSearchParams, useRouter } from "next/navigation";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Breadcrumbs } from "@/components/breadcrumbs";
import { UnitCard } from "@/components/cards/unit-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import {
  useDashboardUnits,
  useUnitCost,
  useUnitDetail,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { UnitDashboardSummary } from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";
import { DollarSign, Network, Plus, Users, X } from "lucide-react";
import Link from "next/link";

function UnitListContent() {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const unitsQuery = useDashboardUnits();
  const units = unitsQuery.data ?? [];
  const loading = unitsQuery.isPending;

  // Delete confirmation state.
  const [deleteTarget, setDeleteTarget] = useState<UnitDashboardSummary | null>(
    null,
  );

  const deleteUnit = useMutation({
    mutationFn: (name: string) => api.deleteUnit(name),
    onSuccess: (_data, name) => {
      const displayName =
        deleteTarget?.name === name
          ? deleteTarget?.displayName
          : undefined;
      toast({
        title: "Unit deleted",
        description: displayName,
      });
      // Drop the row from the dashboard cache so the list updates
      // immediately. We deliberately don't invalidate the same key
      // afterwards — the deleted unit would come back on the refetch
      // since the delete is tombstoned lazily on the server.
      queryClient.setQueryData<UnitDashboardSummary[] | undefined>(
        queryKeys.dashboard.units(),
        (prev) => prev?.filter((u) => u.name !== name),
      );
      // Other unit-derived slices can be re-fetched safely — they'll
      // 404 for the deleted unit and the UI handles that case.
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      setDeleteTarget(null);
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Delete failed",
        description: message,
        variant: "destructive",
      });
    },
  });

  const handleDeleteConfirm = () => {
    if (!deleteTarget) return;
    deleteUnit.mutate(deleteTarget.name);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <Network className="h-5 w-5" /> Units
          </h1>
          <p className="text-sm text-muted-foreground">
            Registered units in this environment.
          </p>
        </div>
        <Link href="/units/create" className="self-start sm:self-auto">
          <Button>
            <Plus className="h-4 w-4 mr-1" /> New unit
          </Button>
        </Link>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>All units</CardTitle>
        </CardHeader>
        <CardContent>
          {loading ? (
            <div className="space-y-2">
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
            </div>
          ) : units.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No units registered yet.
            </p>
          ) : (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {units.map((u) => (
                <UnitCard
                  key={u.name}
                  unit={u}
                  onDelete={(unit) =>
                    setDeleteTarget(
                      units.find((x) => x.name === unit.name) ?? null,
                    )
                  }
                />
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete unit"
        description={
          deleteTarget
            ? `Are you sure you want to delete ${deleteTarget.displayName}? This will remove the unit, its memberships, and its configuration. This action cannot be undone.`
            : ""
        }
        confirmLabel="Delete"
        cancelLabel="Cancel"
        onConfirm={handleDeleteConfirm}
        onCancel={() => {
          if (!deleteUnit.isPending) setDeleteTarget(null);
        }}
        pending={deleteUnit.isPending}
      />
    </div>
  );
}

function UnitDetailContent() {
  const searchParams = useSearchParams();
  const router = useRouter();
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const id = searchParams.get("id") ?? "";

  const detailQuery = useUnitDetail(id);
  const costQuery = useUnitCost(id);
  const data = detailQuery.data ?? null;
  const cost = costQuery.data ?? null;
  const loading = Boolean(id) && (detailQuery.isPending || costQuery.isPending);

  const [memberScheme, setMemberScheme] = useState("agent");
  const [memberPath, setMemberPath] = useState("");

  const invalidateUnit = () => {
    queryClient.invalidateQueries({
      queryKey: queryKeys.units.fullDetail(id),
    });
    queryClient.invalidateQueries({
      queryKey: queryKeys.units.detail(id),
    });
  };

  const deleteUnit = useMutation({
    mutationFn: () => api.deleteUnit(id),
    onSuccess: () => {
      toast({ title: "Unit deleted" });
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      router.push("/");
    },
    onError: (err) => {
      toast({
        title: "Failed to delete unit",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const addMember = useMutation({
    mutationFn: ({ scheme, path }: { scheme: string; path: string }) =>
      api.addMember(id, scheme, path),
    onSuccess: () => {
      toast({ title: "Member added" });
      setMemberPath("");
      invalidateUnit();
    },
    onError: (err) => {
      toast({
        title: "Failed to add member",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const removeMember = useMutation({
    mutationFn: (memberId: string) => api.removeMember(id, memberId),
    onSuccess: () => {
      toast({ title: "Member removed" });
      invalidateUnit();
    },
    onError: (err) => {
      toast({
        title: "Failed to remove member",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const handleAddMember = () => {
    if (!memberPath.trim()) return;
    addMember.mutate({ scheme: memberScheme, path: memberPath.trim() });
  };

  if (!id) {
    return <UnitListContent />;
  }

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  if (!data) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Units", href: "/units" },
            { label: "Unknown unit" },
          ]}
        />
        <p className="text-muted-foreground">Unit not found.</p>
      </div>
    );
  }

  const { unit } = data;

  // Parse members from the Details payload if available
  const members: Array<{ id?: string; scheme?: string; path?: string }> =
    Array.isArray((data.details as Record<string, unknown>)?.members)
      ? ((data.details as Record<string, unknown>).members as Array<{ id?: string; scheme?: string; path?: string }>)
      : [];

  return (
    <div className="space-y-6">
      <Breadcrumbs
        items={[
          { label: "Units", href: "/units" },
          { label: unit.displayName || unit.name },
        ]}
      />
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-2xl font-bold">{unit.displayName}</h1>
          <p className="truncate text-sm text-muted-foreground">{unit.name}</p>
        </div>
        <Button
          variant="destructive"
          size="sm"
          onClick={() => deleteUnit.mutate()}
          disabled={deleteUnit.isPending}
          className="self-start sm:self-auto"
        >
          <X className="h-4 w-4 mr-1" /> Delete
        </Button>
      </div>

      {/* Unit info */}
      <Card>
        <CardHeader>
          <CardTitle>Unit Info</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex justify-between">
            <span className="text-muted-foreground">Description</span>
            <span>{unit.description || "—"}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Registered</span>
            <span>{timeAgo(unit.registeredAt)}</span>
          </div>
        </CardContent>
      </Card>

      {/* Cost */}
      {cost !== null && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <DollarSign className="h-4 w-4" /> Cost Breakdown
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            <div className="flex justify-between">
              <span className="text-muted-foreground">Total Cost</span>
              <span className="font-medium">{formatCost(cost.totalCost)}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Input Tokens</span>
              <span>{cost.totalInputTokens.toLocaleString()}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Output Tokens</span>
              <span>{cost.totalOutputTokens.toLocaleString()}</span>
            </div>
          </CardContent>
        </Card>
      )}

      {/* Members */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Users className="h-4 w-4" /> Members ({members.length})
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          {members.length === 0 && (
            <p className="text-sm text-muted-foreground">No members</p>
          )}
          {members.map((m, i) => (
            <div key={m.id ?? i} className="flex items-center justify-between rounded border border-border p-2 text-sm">
              <span>
                {m.scheme}://{m.path}
              </span>
              {m.id && (
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => removeMember.mutate(m.id!)}
                  title="Remove member"
                >
                  <X className="h-3.5 w-3.5" />
                </Button>
              )}
            </div>
          ))}

          {/* Add member form — scheme + path wrap side-by-side, and the
              button drops onto its own row at narrow widths. */}
          <div className="flex flex-col gap-2 pt-2 sm:flex-row">
            <div className="flex gap-2">
              <select
                value={memberScheme}
                onChange={(e) => setMemberScheme(e.target.value)}
                className="h-9 rounded-md border border-input bg-background px-3 text-sm"
                aria-label="Member scheme"
              >
                <option value="agent">agent</option>
                <option value="unit">unit</option>
              </select>
              <Input
                placeholder="Member path (e.g., my-agent)"
                value={memberPath}
                onChange={(e) => setMemberPath(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleAddMember()}
              />
            </div>
            <Button size="sm" onClick={handleAddMember} className="self-start sm:self-auto">
              <Plus className="h-4 w-4 mr-1" /> Add
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Raw details */}
      {data.details != null && (
        <Card>
          <CardHeader>
            <CardTitle>Details</CardTitle>
          </CardHeader>
          <CardContent>
            <pre className="overflow-x-auto rounded bg-muted p-3 text-xs">
              {JSON.stringify(data.details, null, 2)}
            </pre>
          </CardContent>
        </Card>
      )}
    </div>
  );
}

export default function UnitDetailPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <UnitDetailContent />
    </Suspense>
  );
}
