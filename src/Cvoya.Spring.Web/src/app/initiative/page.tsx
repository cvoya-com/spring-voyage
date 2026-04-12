"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Activity, Zap } from "lucide-react";

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
  AgentDashboardSummary,
  InitiativeLevel,
  InitiativePolicy,
} from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";

const INITIATIVE_LEVELS: InitiativeLevel[] = [
  "Passive",
  "Attentive",
  "Proactive",
  "Autonomous",
];

const DEFAULT_POLICY: InitiativePolicy = {
  MaxLevel: "Passive",
  RequireUnitApproval: false,
  Tier1: null,
  Tier2: { MaxCallsPerHour: 5, MaxCostPerDay: 3.0 },
  AllowedActions: null,
  BlockedActions: null,
};

interface AgentRow {
  agent: AgentDashboardSummary;
  level: InitiativeLevel | null;
  maxLevel: InitiativeLevel | null;
}

function levelBadgeVariant(
  level: InitiativeLevel | null,
): "default" | "success" | "warning" | "destructive" | "outline" {
  switch (level) {
    case "Passive":
      return "outline";
    case "Attentive":
      return "default";
    case "Proactive":
      return "warning";
    case "Autonomous":
      return "destructive";
    default:
      return "outline";
  }
}

function parseCsv(value: string): string[] | null {
  const parts = value
    .split(",")
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  return parts.length === 0 ? null : parts;
}

function formatCsv(value: string[] | null | undefined): string {
  return value && value.length > 0 ? value.join(", ") : "";
}

export default function InitiativePage() {
  const { toast } = useToast();
  const { events } = useActivityStream();

  const [rows, setRows] = useState<AgentRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [editorPolicy, setEditorPolicy] = useState<InitiativePolicy | null>(
    null,
  );
  const [editorAllowed, setEditorAllowed] = useState("");
  const [editorBlocked, setEditorBlocked] = useState("");
  const [editorLoading, setEditorLoading] = useState(false);
  const [saving, setSaving] = useState(false);

  const loadAgents = useCallback(async () => {
    const agents = await api.getDashboardAgents();
    const results = await Promise.all(
      agents.map(async (agent) => {
        const [levelRes, policyRes] = await Promise.allSettled([
          api.getAgentInitiativeLevel(agent.name),
          api.getAgentInitiativePolicy(agent.name),
        ]);
        const level =
          levelRes.status === "fulfilled" ? levelRes.value.level : null;
        const maxLevel =
          policyRes.status === "fulfilled" && policyRes.value
            ? policyRes.value.MaxLevel
            : null;
        return { agent, level, maxLevel } satisfies AgentRow;
      }),
    );
    return results;
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    loadAgents()
      .then((results) => {
        if (!cancelled) {
          setRows(results);
          setLoading(false);
        }
      })
      .catch(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [loadAgents]);

  const openEditor = useCallback(
    async (agentId: string) => {
      setSelectedAgentId(agentId);
      setEditorLoading(true);
      setEditorPolicy(null);
      try {
        const policy = await api.getAgentInitiativePolicy(agentId);
        const effective = policy ?? DEFAULT_POLICY;
        setEditorPolicy(effective);
        setEditorAllowed(formatCsv(effective.AllowedActions));
        setEditorBlocked(formatCsv(effective.BlockedActions));
      } catch (err) {
        toast({
          title: "Failed to load policy",
          description: err instanceof Error ? err.message : String(err),
          variant: "destructive",
        });
        setEditorPolicy({ ...DEFAULT_POLICY });
        setEditorAllowed("");
        setEditorBlocked("");
      } finally {
        setEditorLoading(false);
      }
    },
    [toast],
  );

  const closeEditor = useCallback(() => {
    setSelectedAgentId(null);
    setEditorPolicy(null);
    setEditorAllowed("");
    setEditorBlocked("");
  }, []);

  const handleSave = useCallback(async () => {
    if (!selectedAgentId || !editorPolicy) return;
    const payload: InitiativePolicy = {
      ...editorPolicy,
      AllowedActions: parseCsv(editorAllowed),
      BlockedActions: parseCsv(editorBlocked),
      Tier2: editorPolicy.Tier2 ?? {
        MaxCallsPerHour: 5,
        MaxCostPerDay: 3.0,
      },
    };
    setSaving(true);
    try {
      await api.setAgentInitiativePolicy(selectedAgentId, payload);
      toast({ title: "Policy saved" });
      const fresh = await loadAgents();
      setRows(fresh);
      closeEditor();
    } catch (err) {
      toast({
        title: "Failed to save policy",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setSaving(false);
    }
  }, [
    selectedAgentId,
    editorPolicy,
    editorAllowed,
    editorBlocked,
    loadAgents,
    toast,
    closeEditor,
  ]);

  const recentInitiativeEvents = useMemo<ActivityEvent[]>(
    () =>
      events
        .filter(
          (e) =>
            e.eventType === "InitiativeTriggered" ||
            e.eventType === "ReflectionCompleted",
        )
        .slice(0, 10),
    [events],
  );

  const agentNameLookup = useMemo<Record<string, string>>(() => {
    const map: Record<string, string> = {};
    for (const r of rows) {
      map[r.agent.name] = r.agent.displayName;
    }
    return map;
  }, [rows]);

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <Zap className="h-5 w-5" /> Initiative
          </h1>
          <p className="text-sm text-muted-foreground">
            Configure autonomy levels and review recent initiative activity.
          </p>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agents</CardTitle>
        </CardHeader>
        <CardContent>
          {loading ? (
            <div className="space-y-2">
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
            </div>
          ) : rows.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No agents registered.
            </p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Agent</TableHead>
                  <TableHead>Current Level</TableHead>
                  <TableHead>Max Level</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => {
                  const isSelected = selectedAgentId === row.agent.name;
                  return (
                    <TableRow
                      key={row.agent.name}
                      className={
                        isSelected
                          ? "cursor-pointer bg-muted/50"
                          : "cursor-pointer"
                      }
                      onClick={() => openEditor(row.agent.name)}
                    >
                      <TableCell>
                        <div className="font-medium">
                          {row.agent.displayName}
                        </div>
                        <div className="text-xs text-muted-foreground">
                          {row.agent.name}
                        </div>
                      </TableCell>
                      <TableCell>
                        <Badge variant={levelBadgeVariant(row.level)}>
                          {row.level ?? "—"}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <Badge variant={levelBadgeVariant(row.maxLevel)}>
                          {row.maxLevel ?? "—"}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={(e) => {
                            e.stopPropagation();
                            if (isSelected) {
                              closeEditor();
                            } else {
                              openEditor(row.agent.name);
                            }
                          }}
                        >
                          {isSelected ? "Close" : "Edit policy"}
                        </Button>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {selectedAgentId !== null && (
        <Card>
          <CardHeader>
            <CardTitle>
              Policy — {agentNameLookup[selectedAgentId] ?? selectedAgentId}
            </CardTitle>
          </CardHeader>
          <CardContent>
            {editorLoading || !editorPolicy ? (
              <Skeleton className="h-40" />
            ) : (
              <div className="space-y-4 text-sm">
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <label className="space-y-1">
                    <span className="text-muted-foreground">Max Level</span>
                    <select
                      className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                      value={editorPolicy.MaxLevel}
                      onChange={(e) =>
                        setEditorPolicy({
                          ...editorPolicy,
                          MaxLevel: e.target.value as InitiativeLevel,
                        })
                      }
                    >
                      {INITIATIVE_LEVELS.map((lvl) => (
                        <option key={lvl} value={lvl}>
                          {lvl}
                        </option>
                      ))}
                    </select>
                  </label>

                  <label className="flex items-center gap-2 pt-6">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-input"
                      checked={editorPolicy.RequireUnitApproval}
                      onChange={(e) =>
                        setEditorPolicy({
                          ...editorPolicy,
                          RequireUnitApproval: e.target.checked,
                        })
                      }
                    />
                    <span>Require unit approval</span>
                  </label>

                  <label className="space-y-1">
                    <span className="text-muted-foreground">
                      Tier 2 max calls / hour
                    </span>
                    <Input
                      type="number"
                      min={0}
                      value={editorPolicy.Tier2?.MaxCallsPerHour ?? 5}
                      onChange={(e) =>
                        setEditorPolicy({
                          ...editorPolicy,
                          Tier2: {
                            MaxCallsPerHour:
                              Number.parseInt(e.target.value, 10) || 0,
                            MaxCostPerDay:
                              editorPolicy.Tier2?.MaxCostPerDay ?? 3.0,
                          },
                        })
                      }
                    />
                  </label>

                  <label className="space-y-1">
                    <span className="text-muted-foreground">
                      Tier 2 max cost / day (USD)
                    </span>
                    <Input
                      type="number"
                      step="0.01"
                      min={0}
                      value={editorPolicy.Tier2?.MaxCostPerDay ?? 3.0}
                      onChange={(e) =>
                        setEditorPolicy({
                          ...editorPolicy,
                          Tier2: {
                            MaxCallsPerHour:
                              editorPolicy.Tier2?.MaxCallsPerHour ?? 5,
                            MaxCostPerDay:
                              Number.parseFloat(e.target.value) || 0,
                          },
                        })
                      }
                    />
                  </label>

                  <label className="space-y-1 sm:col-span-2">
                    <span className="text-muted-foreground">
                      Allowed actions (comma-separated)
                    </span>
                    <Input
                      type="text"
                      value={editorAllowed}
                      onChange={(e) => setEditorAllowed(e.target.value)}
                      placeholder="e.g. send-message, update-state"
                    />
                  </label>

                  <label className="space-y-1 sm:col-span-2">
                    <span className="text-muted-foreground">
                      Blocked actions (comma-separated)
                    </span>
                    <Input
                      type="text"
                      value={editorBlocked}
                      onChange={(e) => setEditorBlocked(e.target.value)}
                      placeholder="e.g. delete-agent"
                    />
                  </label>
                </div>

                <div className="flex justify-end gap-2">
                  <Button
                    variant="outline"
                    onClick={closeEditor}
                    disabled={saving}
                  >
                    Cancel
                  </Button>
                  <Button onClick={handleSave} disabled={saving}>
                    {saving ? "Saving…" : "Save"}
                  </Button>
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" /> Recent initiative activity
          </CardTitle>
        </CardHeader>
        <CardContent>
          {recentInitiativeEvents.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No initiative events yet.
            </p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Time</TableHead>
                  <TableHead>Agent</TableHead>
                  <TableHead>Type</TableHead>
                  <TableHead>Summary</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {recentInitiativeEvents.map((e) => {
                  const agentKey = e.source.path;
                  const display = agentNameLookup[agentKey] ?? agentKey;
                  return (
                    <TableRow key={e.id}>
                      <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                        {timeAgo(e.timestamp)}
                      </TableCell>
                      <TableCell>{display}</TableCell>
                      <TableCell>
                        <Badge variant="outline">{e.eventType}</Badge>
                      </TableCell>
                      <TableCell className="text-sm">{e.summary}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
