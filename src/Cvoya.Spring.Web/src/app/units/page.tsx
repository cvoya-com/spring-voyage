"use client";

import { Suspense, useCallback, useEffect, useState } from "react";
import { useSearchParams, useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  CostSummaryResponse,
  UnitDashboardSummary,
  UnitDetailResponse,
} from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";
import {
  ArrowLeft,
  DollarSign,
  Network,
  Plus,
  Trash2,
  Users,
  X,
} from "lucide-react";
import Link from "next/link";

function UnitListContent() {
  const [units, setUnits] = useState<UnitDashboardSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    api
      .getDashboardUnits()
      .then((u) => {
        if (!cancelled) {
          setUnits(u);
          setLoading(false);
        }
      })
      .catch(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <Network className="h-5 w-5" /> Units
          </h1>
          <p className="text-sm text-muted-foreground">
            Registered units in this environment.
          </p>
        </div>
        <Link href="/units/create">
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
            <ul className="divide-y divide-border">
              {units.map((u) => (
                <li key={u.name}>
                  <Link
                    href={`/units/${encodeURIComponent(u.name)}`}
                    className="flex items-center justify-between py-3 hover:bg-accent/50 -mx-2 px-2 rounded"
                  >
                    <div>
                      <div className="font-medium">{u.displayName}</div>
                      <div className="text-xs text-muted-foreground">
                        {u.name}
                      </div>
                    </div>
                    <div className="text-xs text-muted-foreground">
                      Registered {timeAgo(u.registeredAt)}
                    </div>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function UnitDetailContent() {
  const searchParams = useSearchParams();
  const router = useRouter();
  const { toast } = useToast();
  const id = searchParams.get("id") ?? "";
  const [data, setData] = useState<UnitDetailResponse | null>(null);
  const [cost, setCost] = useState<CostSummaryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [memberScheme, setMemberScheme] = useState("agent");
  const [memberPath, setMemberPath] = useState("");

  const load = useCallback(async () => {
    if (!id) return;
    try {
      const [unitData, costData] = await Promise.allSettled([
        api.getUnitDetail(id),
        api.getUnitCost(id),
      ]);
      if (unitData.status === "fulfilled") setData(unitData.value);
      if (costData.status === "fulfilled") setCost(costData.value);
    } catch {
      // silently handled
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    load();
  }, [load]);

  const handleDelete = async () => {
    try {
      await api.deleteUnit(id);
      toast({ title: "Unit deleted" });
      router.push("/");
    } catch (err) {
      toast({
        title: "Failed to delete unit",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  const handleAddMember = async () => {
    if (!memberPath.trim()) return;
    try {
      await api.addMember(id, memberScheme, memberPath.trim());
      toast({ title: "Member added" });
      setMemberPath("");
      load();
    } catch (err) {
      toast({
        title: "Failed to add member",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  const handleRemoveMember = async (memberId: string) => {
    try {
      await api.removeMember(id, memberId);
      toast({ title: "Member removed" });
      load();
    } catch (err) {
      toast({
        title: "Failed to remove member",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
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
        <Link href="/" className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
          <ArrowLeft className="h-4 w-4" /> Back to Dashboard
        </Link>
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
      <div className="flex items-center justify-between">
        <div>
          <Link href="/" className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground mb-2">
            <ArrowLeft className="h-4 w-4" /> Dashboard
          </Link>
          <h1 className="text-2xl font-bold">{unit.displayName}</h1>
          <p className="text-sm text-muted-foreground">{unit.name}</p>
        </div>
        <Button variant="destructive" size="sm" onClick={handleDelete}>
          <Trash2 className="h-4 w-4 mr-1" /> Delete
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
                  onClick={() => handleRemoveMember(m.id!)}
                  title="Remove member"
                >
                  <X className="h-3.5 w-3.5" />
                </Button>
              )}
            </div>
          ))}

          {/* Add member form */}
          <div className="flex gap-2 pt-2">
            <select
              value={memberScheme}
              onChange={(e) => setMemberScheme(e.target.value)}
              className="h-9 rounded-md border border-input bg-background px-3 text-sm"
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
            <Button size="sm" onClick={handleAddMember}>
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
