"use client";

import { useCallback, useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  AgentExecutionMode,
  AgentResponse,
} from "@/lib/api/types";

const EXECUTION_MODES: AgentExecutionMode[] = ["Auto", "OnDemand"];

interface AgentsTabProps {
  unitId: string;
}

/**
 * Agents tab for the unit configuration page. Lists the agents that belong
 * to this unit (members with `scheme=agent`, enriched with each agent's
 * own metadata) and lets an operator assign / unassign / edit.
 *
 * Per the v2 model, a unit *is* an agent, and an agent owns its own
 * metadata. This tab is a UI convenience — the edits fire at the
 * agent-scoped endpoint `PATCH /api/v1/agents/{id}`. Assign / unassign
 * go through the unit-scoped endpoints which maintain the
 * `agent.parentUnit ↔ unit.members` invariant in one place.
 */
export function AgentsTab({ unitId }: AgentsTabProps) {
  const { toast } = useToast();
  const [agents, setAgents] = useState<AgentResponse[]>([]);
  const [availableAgents, setAvailableAgents] = useState<AgentResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [addAgentId, setAddAgentId] = useState("");
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [inUnit, all] = await Promise.all([
        api.listUnitAgents(unitId),
        api.listAgents(),
      ]);
      setAgents(inUnit);
      setAvailableAgents(all);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [unitId]);

  useEffect(() => {
    load();
  }, [load]);

  // An agent is eligible for assignment here if it has no parent unit yet.
  // Agents belonging to *this* unit are already in `agents`; agents with a
  // different parentUnit should be unassigned from that unit first.
  const assignable = availableAgents.filter((a) => !a.parentUnit);

  const handleAdd = async () => {
    if (!addAgentId) return;
    setAdding(true);
    setAddError(null);
    try {
      const created = await api.assignUnitAgent(unitId, addAgentId);
      setAgents((prev) => [...prev, created]);
      setAddAgentId("");
      toast({ title: "Agent assigned", description: addAgentId });
    } catch (err) {
      setAddError(err instanceof Error ? err.message : String(err));
    } finally {
      setAdding(false);
    }
  };

  const patchAgent = async (
    agentId: string,
    patch: Partial<AgentResponse>,
  ) => {
    try {
      const updated = await api.updateAgentMetadata(agentId, {
        model: patch.model,
        specialty: patch.specialty,
        enabled: patch.enabled,
        executionMode: patch.executionMode,
      });
      setAgents((prev) =>
        prev.map((a) => (a.name === agentId ? updated : a)),
      );
    } catch (err) {
      toast({
        title: "Update failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  const handleRemove = async (agentId: string) => {
    try {
      await api.unassignUnitAgent(unitId, agentId);
      setAgents((prev) => prev.filter((a) => a.name !== agentId));
      toast({ title: "Agent unassigned", description: agentId });
    } catch (err) {
      toast({
        title: "Unassign failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Agents</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}

        {loading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : agents.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No agents assigned to this unit yet.
          </p>
        ) : (
          <ul className="space-y-2">
            {agents.map((agent) => (
              <li
                key={agent.name}
                className="rounded-md border bg-card p-3 space-y-3"
              >
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">
                      {agent.displayName || agent.name}
                    </span>
                    {!agent.enabled && (
                      <Badge variant="outline">Disabled</Badge>
                    )}
                  </div>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => handleRemove(agent.name)}
                    aria-label={`Unassign ${agent.name}`}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>

                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                  <label className="block space-y-1">
                    <span className="text-xs text-muted-foreground">Model</span>
                    <Input
                      value={agent.model ?? ""}
                      placeholder="(inherit)"
                      onChange={(e) =>
                        setAgents((prev) =>
                          prev.map((a) =>
                            a.name === agent.name
                              ? { ...a, model: e.target.value || null }
                              : a,
                          ),
                        )
                      }
                      onBlur={(e) =>
                        patchAgent(agent.name, {
                          model: e.target.value || null,
                        })
                      }
                    />
                  </label>

                  <label className="block space-y-1">
                    <span className="text-xs text-muted-foreground">
                      Specialty
                    </span>
                    <Input
                      value={agent.specialty ?? ""}
                      placeholder="e.g. reviewer"
                      onChange={(e) =>
                        setAgents((prev) =>
                          prev.map((a) =>
                            a.name === agent.name
                              ? { ...a, specialty: e.target.value || null }
                              : a,
                          ),
                        )
                      }
                      onBlur={(e) =>
                        patchAgent(agent.name, {
                          specialty: e.target.value || null,
                        })
                      }
                    />
                  </label>

                  <label className="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      checked={agent.enabled}
                      onChange={(e) =>
                        patchAgent(agent.name, { enabled: e.target.checked })
                      }
                    />
                    Enabled
                  </label>

                  <label className="block space-y-1">
                    <span className="text-xs text-muted-foreground">
                      Execution mode
                    </span>
                    <select
                      value={agent.executionMode}
                      onChange={(e) =>
                        patchAgent(agent.name, {
                          executionMode: e.target.value as AgentExecutionMode,
                        })
                      }
                      className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    >
                      {EXECUTION_MODES.map((m) => (
                        <option key={m} value={m}>
                          {m}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>
              </li>
            ))}
          </ul>
        )}

        <div className="border-t pt-4 space-y-2">
          <p className="text-sm font-medium">Assign an agent</p>
          {assignable.length === 0 ? (
            <p className="text-xs text-muted-foreground">
              All registered agents already belong to a unit.
            </p>
          ) : (
            <div className="flex items-end gap-2">
              <label className="flex-1 space-y-1">
                <span className="text-xs text-muted-foreground">Agent</span>
                <select
                  value={addAgentId}
                  onChange={(e) => setAddAgentId(e.target.value)}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                >
                  <option value="">Pick an agent…</option>
                  {assignable.map((a) => (
                    <option key={a.name} value={a.name}>
                      {a.displayName || a.name}
                    </option>
                  ))}
                </select>
              </label>
              <Button
                onClick={handleAdd}
                disabled={!addAgentId || adding}
                size="sm"
              >
                <Plus className="mr-1 h-4 w-4" />
                {adding ? "Assigning…" : "Assign"}
              </Button>
            </div>
          )}
          {addError && (
            <p className="text-xs text-destructive">{addError}</p>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
