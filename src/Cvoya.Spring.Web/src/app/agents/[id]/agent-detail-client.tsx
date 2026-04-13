"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  ArrowLeft,
  Copy,
  DollarSign,
  Plus,
  Trash2,
  Wallet,
  Zap,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { useActivityStream } from "@/hooks/use-activity-stream";
import { api } from "@/lib/api/client";
import type {
  ActivityEvent,
  AgentDetailResponse,
  BudgetResponse,
  CloneAttachmentMode,
  CloneResponse,
  CloneType,
  CostSummaryResponse,
} from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";

type CostClass = "initiative_cost" | "work_cost";

interface ClassifiedCost {
  event: ActivityEvent;
  classification: CostClass;
}

// Classify CostIncurred events by reading the `costSource` tag written at
// emission time by AgentActor (see #101). Falls back to "work_cost" for
// legacy events that pre-date the tag so the table stays readable during
// rollout.
function classifyCostEvents(
  events: ActivityEvent[],
  agentId: string,
): ClassifiedCost[] {
  const result: ClassifiedCost[] = [];

  for (const e of events) {
    if (e.eventType !== "CostIncurred") continue;
    if (e.source.scheme !== "agent" || e.source.path !== agentId) continue;

    const raw = (e.details as { costSource?: unknown } | undefined)?.costSource;
    const classification: CostClass =
      typeof raw === "string" && raw.toLowerCase() === "initiative"
        ? "initiative_cost"
        : "work_cost";

    result.push({ event: e, classification });
  }

  return result;
}

interface ClientProps {
  id: string;
}

export default function AgentDetailClient({ id }: ClientProps) {
  const router = useRouter();
  const { toast } = useToast();
  const [data, setData] = useState<AgentDetailResponse | null>(null);
  const [cost, setCost] = useState<CostSummaryResponse | null>(null);
  const [clones, setClones] = useState<CloneResponse[]>([]);
  const [budget, setBudget] = useState<BudgetResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const { events } = useActivityStream();

  const [cloneType, setCloneType] = useState<CloneType>("ephemeral-no-memory");
  const [attachmentMode, setAttachmentMode] =
    useState<CloneAttachmentMode>("detached");
  const [creatingClone, setCreatingClone] = useState(false);

  const [budgetInput, setBudgetInput] = useState("");
  const [savingBudget, setSavingBudget] = useState(false);

  const loadClones = useCallback(async () => {
    try {
      const list = await api.getClones(id);
      setClones(list);
    } catch {
      // Swallowed: list endpoint 404s when parent is missing; handled by agent load.
    }
  }, [id]);

  const loadBudget = useCallback(async () => {
    try {
      const b = await api.getAgentBudget(id);
      setBudget(b);
      setBudgetInput(b.dailyBudget.toString());
    } catch {
      // No budget set yet — that's the normal initial state.
      setBudget(null);
    }
  }, [id]);

  useEffect(() => {
    if (!id) return;
    let cancelled = false;

    async function load() {
      const [agentData, costData, clonesData, budgetData] =
        await Promise.allSettled([
          api.getAgent(id),
          api.getAgentCost(id),
          api.getClones(id),
          api.getAgentBudget(id),
        ]);
      if (cancelled) return;
      if (agentData.status === "fulfilled") setData(agentData.value);
      if (costData.status === "fulfilled") setCost(costData.value);
      if (clonesData.status === "fulfilled") setClones(clonesData.value);
      if (budgetData.status === "fulfilled") {
        setBudget(budgetData.value);
        setBudgetInput(budgetData.value.dailyBudget.toString());
      }
      setLoading(false);
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [id]);

  const handleDeleteAgent = async () => {
    try {
      await api.deleteAgent(id);
      toast({ title: "Agent deleted" });
      router.push("/");
    } catch (err) {
      toast({
        title: "Failed to delete agent",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  const handleCreateClone = async () => {
    setCreatingClone(true);
    try {
      await api.createClone(id, { cloneType, attachmentMode });
      toast({ title: "Clone creation requested" });
      // Provisioning is async; the new clone shows up on the next poll.
      await loadClones();
    } catch (err) {
      toast({
        title: "Failed to create clone",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setCreatingClone(false);
    }
  };

  const handleDeleteClone = async (cloneId: string) => {
    try {
      await api.deleteClone(id, cloneId);
      toast({ title: "Clone deletion requested" });
      await loadClones();
    } catch (err) {
      toast({
        title: "Failed to delete clone",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  const handleSaveBudget = async () => {
    const value = Number(budgetInput);
    if (!Number.isFinite(value) || value <= 0) {
      toast({
        title: "Invalid budget",
        description: "Daily budget must be greater than zero.",
        variant: "destructive",
      });
      return;
    }
    setSavingBudget(true);
    try {
      const updated = await api.setAgentBudget(id, { dailyBudget: value });
      setBudget(updated);
      toast({ title: "Budget saved" });
    } catch (err) {
      toast({
        title: "Failed to save budget",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setSavingBudget(false);
    }
  };

  if (!id || id === "__placeholder__") {
    return <p className="text-muted-foreground">No agent ID specified.</p>;
  }

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-40" />
        <Skeleton className="h-32" />
      </div>
    );
  }

  if (!data) {
    return (
      <div className="space-y-4">
        <Link
          href="/"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-4 w-4" /> Dashboard
        </Link>
        <p className="text-muted-foreground">Agent not found.</p>
      </div>
    );
  }

  const { agent } = data;
  const classified = classifyCostEvents(events, agent.name);
  // Totals come from the cost API (#101 moved classification server-side).
  // The `classified` list above is only used to label individual rows in the
  // recent-events table; summing it here would drift from the authoritative
  // server split whenever an event arrives mid-stream but isn't yet persisted.
  const initiativeCostTotal = Number(cost?.initiativeCost ?? 0); // decimal -> number (#181)
  const workCostTotal = Number(cost?.workCost ?? 0);

  const now = Date.now();
  const tier2Last24h = events.filter(
    (e) =>
      e.source.scheme === "agent" &&
      e.source.path === agent.name &&
      e.eventType === "ReflectionCompleted" &&
      now - new Date(e.timestamp).getTime() <= 24 * 60 * 60 * 1000,
  ).length;

  const recentClassified = classified.slice(0, 20);

  const budgetValue = Number(budgetInput);
  const budgetUtilization =
    Number.isFinite(budgetValue) && budgetValue > 0 && cost
      ? Math.min(100, (Number(cost.totalCost) / budgetValue) * 100) // decimal -> number (#181)
      : null;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <Link
            href="/"
            className="mb-2 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" /> Dashboard
          </Link>
          <h1 className="text-2xl font-bold">{agent.displayName}</h1>
          <p className="text-sm text-muted-foreground">{agent.name}</p>
        </div>
        <Button
          variant="destructive"
          size="sm"
          onClick={handleDeleteAgent}
          className="self-start sm:self-auto"
        >
          <Trash2 className="mr-1 h-4 w-4" /> Delete
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agent Info</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex flex-col gap-1 sm:flex-row sm:justify-between">
            <span className="text-muted-foreground">Description</span>
            <span className="sm:text-right">{agent.description || "—"}</span>
          </div>
          <div className="flex flex-col gap-1 sm:flex-row sm:justify-between">
            <span className="text-muted-foreground">Role</span>
            <span className="sm:text-right">
              {agent.role ? (
                <Badge variant="outline">{String(agent.role)}</Badge>
              ) : (
                "—"
              )}
            </span>
          </div>
          <div className="flex flex-col gap-1 sm:flex-row sm:justify-between">
            <span className="text-muted-foreground">Registered</span>
            <span className="sm:text-right">{timeAgo(agent.registeredAt)}</span>
          </div>
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        {cost !== null && (
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <DollarSign className="h-4 w-4" /> Cost Summary
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <div className="flex justify-between">
                <span className="text-muted-foreground">Total Cost</span>
                <span className="font-medium">
                  {formatCost(Number(cost.totalCost))}
                </span>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">Input Tokens</span>
                <span>{cost.totalInputTokens.toLocaleString()}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">Output Tokens</span>
                <span>{cost.totalOutputTokens.toLocaleString()}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">Records</span>
                <span>{cost.recordCount}</span>
              </div>
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Wallet className="h-4 w-4" /> Daily Budget
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p className="text-xs text-muted-foreground">
              Sets a per-day cost ceiling (USD) for this agent. Used by
              initiative and cost-guard policies.
            </p>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Daily budget (USD)
              </span>
              <Input
                type="number"
                inputMode="decimal"
                min="0"
                step="0.01"
                value={budgetInput}
                onChange={(e) => setBudgetInput(e.target.value)}
                placeholder="e.g. 5.00"
              />
            </label>
            {budgetUtilization !== null && (
              <div>
                <div className="flex justify-between text-xs text-muted-foreground">
                  <span>Utilization (period-to-date)</span>
                  <span>{budgetUtilization.toFixed(1)}%</span>
                </div>
                <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-muted">
                  <div
                    className="h-full bg-primary"
                    style={{ width: `${budgetUtilization}%` }}
                  />
                </div>
              </div>
            )}
            <div className="flex items-center justify-between">
              <span className="text-xs text-muted-foreground">
                {budget
                  ? `Current: ${formatCost(Number(budget.dailyBudget))}/day`
                  : "No budget set"}
              </span>
              <Button size="sm" onClick={handleSaveBudget} disabled={savingBudget}>
                {savingBudget ? "Saving…" : "Save"}
              </Button>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Cost breakdown by activity (client-side heuristic; see #75) */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Zap className="h-4 w-4" /> Cost breakdown by activity
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <div className="rounded-md border border-border p-3">
              <div className="text-xs text-muted-foreground">
                Initiative cost
              </div>
              <div className="text-lg font-semibold">
                {formatCost(initiativeCostTotal)}
              </div>
            </div>
            <div className="rounded-md border border-border p-3">
              <div className="text-xs text-muted-foreground">Work cost</div>
              <div className="text-lg font-semibold">
                {formatCost(workCostTotal)}
              </div>
            </div>
            <div className="rounded-md border border-border p-3">
              <div className="text-xs text-muted-foreground">
                Tier 2 invocations (last 24h)
              </div>
              <div className="text-lg font-semibold">{tier2Last24h}</div>
            </div>
          </div>

          {recentClassified.length === 0 ? (
            <p className="text-xs text-muted-foreground">
              No recent CostIncurred events.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Time</TableHead>
                    <TableHead>Class</TableHead>
                    <TableHead>Summary</TableHead>
                    <TableHead className="text-right">Cost</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {recentClassified.map(({ event, classification }) => (
                    <TableRow key={event.id}>
                      <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                        {timeAgo(event.timestamp)}
                      </TableCell>
                      <TableCell>
                        <Badge
                          variant={
                            classification === "initiative_cost"
                              ? "warning"
                              : "outline"
                          }
                        >
                          {classification}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-sm">{event.summary}</TableCell>
                      <TableCell className="text-right font-mono text-xs">
                        {event.cost != null
                          ? `$${event.cost.toFixed(4)}`
                          : "—"}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Copy className="h-4 w-4" /> Clones ({clones.length})
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_1fr_auto]">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Clone type</span>
              <select
                value={cloneType}
                onChange={(e) => setCloneType(e.target.value as CloneType)}
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              >
                <option value="ephemeral-no-memory">
                  Ephemeral (no memory)
                </option>
                <option value="ephemeral-with-memory">
                  Ephemeral (with memory)
                </option>
              </select>
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Attachment mode
              </span>
              <select
                value={attachmentMode}
                onChange={(e) =>
                  setAttachmentMode(e.target.value as CloneAttachmentMode)
                }
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              >
                <option value="detached">Detached</option>
                <option value="attached">Attached</option>
              </select>
            </label>
            <div className="flex items-end">
              <Button
                onClick={handleCreateClone}
                disabled={creatingClone}
                className="w-full sm:w-auto"
              >
                <Plus className="mr-1 h-4 w-4" />
                {creatingClone ? "Creating…" : "Create clone"}
              </Button>
            </div>
          </div>

          {clones.length === 0 ? (
            <p className="text-sm text-muted-foreground">No clones yet.</p>
          ) : (
            <div className="space-y-2">
              {clones.map((c) => (
                <div
                  key={c.cloneId}
                  className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm sm:flex-row sm:items-center sm:justify-between"
                >
                  <div className="min-w-0 space-y-1">
                    <div className="truncate font-mono text-xs">
                      {c.cloneId}
                    </div>
                    <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                      <Badge variant="outline">{c.cloneType}</Badge>
                      <Badge variant="outline">{c.attachmentMode}</Badge>
                      <Badge
                        variant={
                          c.status === "active" ? "success" : "default"
                        }
                      >
                        {c.status}
                      </Badge>
                      <span>{timeAgo(c.createdAt)}</span>
                    </div>
                  </div>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleDeleteClone(c.cloneId)}
                    aria-label={`Delete clone ${c.cloneId}`}
                  >
                    <Trash2 className="mr-1 h-4 w-4" /> Delete
                  </Button>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {data.status != null && (
        <Card>
          <CardHeader>
            <CardTitle>Status</CardTitle>
          </CardHeader>
          <CardContent>
            <pre className="overflow-x-auto rounded bg-muted p-3 text-xs">
              {JSON.stringify(data.status, null, 2)}
            </pre>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
