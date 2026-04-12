"use client";

import { Suspense, useEffect, useState } from "react";
import { useSearchParams, useRouter } from "next/navigation";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
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
  CloneResponse,
  CostSummaryResponse,
} from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";
import { ArrowLeft, Copy, DollarSign, Trash2, Zap } from "lucide-react";
import Link from "next/link";

type CostClass = "initiative_cost" | "work_cost";

interface ClassifiedCost {
  event: ActivityEvent;
  classification: CostClass;
}

// Server-side split is a follow-up; see #75.
function classifyCostEvents(
  events: ActivityEvent[],
  agentId: string,
): ClassifiedCost[] {
  // Events arrive newest-first from the stream; walk oldest-first so the
  // "initiative mode" flag flips in chronological order.
  const scoped = events
    .filter((e) => e.source.scheme === "agent" && e.source.path === agentId)
    .slice()
    .reverse();

  const QUIET_MS = 30 * 60 * 1000;
  let initiativeMode = false;
  let lastInitiativeTs = 0;
  const classified: ClassifiedCost[] = [];

  for (const e of scoped) {
    const ts = new Date(e.timestamp).getTime();
    if (
      e.eventType === "InitiativeTriggered" ||
      e.eventType === "ReflectionCompleted"
    ) {
      initiativeMode = true;
      lastInitiativeTs = ts;
      continue;
    }

    if (initiativeMode && ts - lastInitiativeTs > QUIET_MS) {
      initiativeMode = false;
    }

    if (
      initiativeMode &&
      (e.eventType === "MessageReceived" || e.eventType === "MessageSent")
    ) {
      initiativeMode = false;
    }

    if (e.eventType === "CostIncurred") {
      classified.push({
        event: e,
        classification: initiativeMode ? "initiative_cost" : "work_cost",
      });
    }
  }

  // Return newest-first for display.
  return classified.reverse();
}

function AgentDetailContent() {
  const searchParams = useSearchParams();
  const router = useRouter();
  const { toast } = useToast();
  const id = searchParams.get("id") ?? "";
  const [data, setData] = useState<AgentDetailResponse | null>(null);
  const [cost, setCost] = useState<CostSummaryResponse | null>(null);
  const [clones, setClones] = useState<CloneResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const { events } = useActivityStream();

  useEffect(() => {
    if (!id) return;
    let cancelled = false;

    async function load() {
      try {
        const [agentData, costData, clonesData] = await Promise.allSettled([
          api.getAgent(id),
          api.getAgentCost(id),
          api.getClones(id),
        ]);
        if (!cancelled) {
          if (agentData.status === "fulfilled") setData(agentData.value);
          if (costData.status === "fulfilled") setCost(costData.value);
          if (clonesData.status === "fulfilled") setClones(clonesData.value);
          setLoading(false);
        }
      } catch {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => { cancelled = true; };
  }, [id]);

  const handleDelete = async () => {
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

  if (!id) {
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
        <Link href="/" className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
          <ArrowLeft className="h-4 w-4" /> Back to Dashboard
        </Link>
        <p className="text-muted-foreground">Agent not found.</p>
      </div>
    );
  }

  const { agent } = data;

  const classified = classifyCostEvents(events, agent.name);
  const initiativeCostTotal = classified
    .filter((c) => c.classification === "initiative_cost")
    .reduce((sum, c) => sum + (c.event.cost ?? 0), 0);
  const workCostTotal = classified
    .filter((c) => c.classification === "work_cost")
    .reduce((sum, c) => sum + (c.event.cost ?? 0), 0);

  const now = Date.now();
  const tier2Last24h = events.filter(
    (e) =>
      e.source.scheme === "agent" &&
      e.source.path === agent.name &&
      e.eventType === "ReflectionCompleted" &&
      now - new Date(e.timestamp).getTime() <= 24 * 60 * 60 * 1000,
  ).length;

  const recentClassified = classified.slice(0, 20);

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <Link href="/" className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground mb-2">
            <ArrowLeft className="h-4 w-4" /> Dashboard
          </Link>
          <h1 className="text-2xl font-bold">{agent.displayName}</h1>
          <p className="text-sm text-muted-foreground">{agent.name}</p>
        </div>
        <Button variant="destructive" size="sm" onClick={handleDelete}>
          <Trash2 className="h-4 w-4 mr-1" /> Delete
        </Button>
      </div>

      {/* Agent info */}
      <Card>
        <CardHeader>
          <CardTitle>Agent Info</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex justify-between">
            <span className="text-muted-foreground">Description</span>
            <span>{agent.description || "—"}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Role</span>
            <span>{agent.role ? <Badge variant="outline">{String(agent.role)}</Badge> : "—"}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Registered</span>
            <span>{timeAgo(agent.registeredAt)}</span>
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
            <div className="flex justify-between">
              <span className="text-muted-foreground">Records</span>
              <span>{cost.recordCount}</span>
            </div>
          </CardContent>
        </Card>
      )}

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
          )}
        </CardContent>
      </Card>

      {/* Status payload */}
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

      {/* Clones */}
      {clones.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Copy className="h-4 w-4" /> Clones ({clones.length})
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            {clones.map((c) => (
              <div key={c.id} className="flex items-center justify-between rounded border border-border p-2 text-sm">
                <span className="font-mono text-xs">{c.id}</span>
                <div className="flex items-center gap-2">
                  <Badge variant="outline">{c.state}</Badge>
                  <span className="text-xs text-muted-foreground">{timeAgo(c.createdAt)}</span>
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      )}
    </div>
  );
}

export default function AgentDetailPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <AgentDetailContent />
    </Suspense>
  );
}
