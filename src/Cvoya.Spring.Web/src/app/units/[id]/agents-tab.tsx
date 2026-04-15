"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  AgentResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";
import {
  MembershipDialog,
  type MembershipFormValues,
} from "./membership-dialog";

interface AgentsTabProps {
  unitId: string;
}

type DialogState =
  | { mode: "closed" }
  | { mode: "add" }
  | { mode: "edit"; membership: UnitMembershipResponse };

/**
 * Agents tab for the unit configuration page. Lists the memberships that
 * belong to this unit (one row per `UnitMembershipResponse`, enriched with
 * each agent's display-name from `/api/v1/agents`) and offers:
 *
 *  - An "Add agent" button at the top that opens a dialog with an agent
 *    picker + per-membership config form.
 *  - An edit icon per row that opens the same dialog pre-populated.
 *  - A remove icon per row that confirms, then deletes the membership.
 *
 * All mutating calls go through the membership endpoints introduced in
 * C2b-1 (#245):
 *
 *   PUT    /api/v1/units/{unitId}/memberships/{agentAddress}
 *   DELETE /api/v1/units/{unitId}/memberships/{agentAddress}
 *
 * These endpoints already exist and are covered by the generated Kiota
 * client in `lib/api/client.ts` (`upsertUnitMembership`,
 * `deleteUnitMembership`), so this tab does not need to drop down to the
 * raw fetch wrapper.
 */
export function AgentsTab({ unitId }: AgentsTabProps) {
  const { toast } = useToast();
  const [memberships, setMemberships] = useState<UnitMembershipResponse[]>([]);
  const [allAgents, setAllAgents] = useState<AgentResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [dialog, setDialog] = useState<DialogState>({ mode: "closed" });
  const [confirmRemove, setConfirmRemove] =
    useState<UnitMembershipResponse | null>(null);
  const [removing, setRemoving] = useState(false);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [members, agents] = await Promise.all([
        api.listUnitMemberships(unitId),
        api.listAgents(),
      ]);
      setMemberships(members);
      setAllAgents(agents);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [unitId]);

  useEffect(() => {
    load();
  }, [load]);

  // The agent directory carries the human-facing metadata (displayName);
  // memberships only carry the address. We index by name so each row can
  // render "Ada" rather than the raw address.
  const agentByName = useMemo(() => {
    const m: Record<string, AgentResponse> = {};
    for (const a of allAgents) m[a.name] = a;
    return m;
  }, [allAgents]);

  const displayNameMap = useMemo(() => {
    const m: Record<string, string> = {};
    for (const a of allAgents) m[a.name] = a.displayName || a.name;
    return m;
  }, [allAgents]);

  // Post-C2b-1 an agent may belong to multiple units (M:N). "Assignable"
  // is just "not already a member of THIS unit" — membership of another
  // unit does not disqualify the agent.
  const assignableAgents = useMemo(() => {
    const inThisUnit = new Set(memberships.map((m) => m.agentAddress));
    return allAgents.filter((a) => !inThisUnit.has(a.name));
  }, [memberships, allAgents]);

  const handleUpsert = async (values: MembershipFormValues) => {
    const saved = await api.upsertUnitMembership(
      unitId,
      values.agentAddress,
      {
        model: values.model,
        specialty: values.specialty,
        enabled: values.enabled,
        executionMode: values.executionMode,
      },
    );
    setMemberships((prev) => {
      const existing = prev.findIndex(
        (m) => m.agentAddress === saved.agentAddress,
      );
      if (existing >= 0) {
        const next = [...prev];
        next[existing] = saved;
        return next;
      }
      return [...prev, saved];
    });
    toast({
      title: dialog.mode === "edit" ? "Membership updated" : "Agent added",
      description: saved.agentAddress,
    });
    setDialog({ mode: "closed" });
  };

  const handleRemove = async () => {
    const target = confirmRemove;
    if (!target) return;
    setRemoving(true);
    try {
      await api.deleteUnitMembership(unitId, target.agentAddress);
      setMemberships((prev) =>
        prev.filter((m) => m.agentAddress !== target.agentAddress),
      );
      toast({ title: "Agent removed", description: target.agentAddress });
      setConfirmRemove(null);
    } catch (err) {
      toast({
        title: "Remove failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setRemoving(false);
    }
  };

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between space-y-0">
        <CardTitle>Agents</CardTitle>
        <Button
          size="sm"
          onClick={() => setDialog({ mode: "add" })}
          disabled={loading || allAgents.length === 0}
          aria-label="Add agent"
        >
          <Plus className="mr-1 h-4 w-4" />
          Add agent
        </Button>
      </CardHeader>
      <CardContent className="space-y-4">
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}

        {loading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : memberships.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No agents assigned to this unit yet. Click{" "}
            <span className="font-medium">Add agent</span> to assign one.
          </p>
        ) : (
          <ul className="divide-y divide-border rounded-md border border-border">
            {memberships.map((m) => {
              const agent = agentByName[m.agentAddress];
              const displayName =
                agent?.displayName || agent?.name || m.agentAddress;
              return (
                <li
                  key={m.agentAddress}
                  className="flex flex-col gap-2 px-3 py-3 sm:flex-row sm:items-center sm:gap-4"
                >
                  <div className="flex min-w-0 flex-1 flex-col gap-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-medium">{displayName}</span>
                      {!m.enabled && (
                        <Badge variant="outline">Disabled</Badge>
                      )}
                      {m.specialty && (
                        <Badge variant="outline">{m.specialty}</Badge>
                      )}
                    </div>
                    <div className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
                      <span>
                        <span className="text-muted-foreground/70">Model:</span>{" "}
                        {m.model ?? "(inherit)"}
                      </span>
                      <span>
                        <span className="text-muted-foreground/70">Mode:</span>{" "}
                        {m.executionMode ?? "(inherit)"}
                      </span>
                      <span className="font-mono">{m.agentAddress}</span>
                    </div>
                  </div>
                  <div className="flex gap-1 self-end sm:self-center">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setDialog({ mode: "edit", membership: m })}
                      aria-label={`Edit ${displayName}`}
                    >
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setConfirmRemove(m)}
                      aria-label={`Remove ${displayName}`}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>

      <MembershipDialog
        open={dialog.mode !== "closed"}
        unitLabel={unitId}
        mode={dialog.mode === "edit" ? "edit" : "add"}
        assignableAgents={assignableAgents}
        initial={dialog.mode === "edit" ? dialog.membership : null}
        agentDisplayNames={displayNameMap}
        onCancel={() => setDialog({ mode: "closed" })}
        onSubmit={handleUpsert}
      />

      <ConfirmDialog
        open={confirmRemove !== null}
        title="Remove agent from unit"
        description={
          confirmRemove
            ? `This removes the membership for ${
                displayNameMap[confirmRemove.agentAddress] ??
                confirmRemove.agentAddress
              }. The agent itself is not deleted.`
            : undefined
        }
        confirmLabel="Remove"
        confirmVariant="destructive"
        pending={removing}
        onConfirm={handleRemove}
        onCancel={() => setConfirmRemove(null)}
      />
    </Card>
  );
}
