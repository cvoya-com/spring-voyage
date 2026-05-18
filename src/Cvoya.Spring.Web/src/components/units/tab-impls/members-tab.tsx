"use client";

// Unit × Members tab body (#2270 / #2427). Three card kinds in one
// surface:
//
//   1. Agent member cards (per-agent membership row, with edit /
//      remove + the existing model / specialty / enabled overrides).
//   2. Sub-unit cards (clickable — drill into the sub-unit's own
//      Members tab via the explorer-selection bridge).
//   3. Human team-role member cards (new — display name + role chip
//      + expertise tags + notification tags; inline edit / remove
//      via the human-member dialog).
//
// Add affordances: `Add agent` (existing) + `Add human` (new). The
// human dialog auto-fills the operator's own Human id in OSS and
// renders a "You" badge — there is exactly one human in OSS, so the
// dialog never asks the operator to pick one. The role / expertise /
// notification fields are free-form.

import { useCallback, useEffect, useMemo, useState } from "react";
import { Pencil, Plus, Trash2, UserPlus } from "lucide-react";

import { AgentCreateDialog } from "@/components/agents/create-dialog";
import { AgentCard, type AgentCardAgent } from "@/components/cards/agent-card";
import { UnitCard } from "@/components/cards/unit-card";
import { ApiErrorMessage } from "@/components/ui/api-error-message";
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
import { formatTranslatedError } from "@/lib/api/translate-error";
import {
  useCurrentUser,
  useUnitHumanMembers,
} from "@/lib/api/queries";
import type {
  AgentResponse,
  UnitHumanMemberResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";
import {
  MembershipDialog,
  type MembershipFormValues,
} from "@/components/units/membership-dialog";
import {
  HumanMemberDialog,
  type HumanMemberFormValues,
} from "@/components/units/human-member-dialog";
import { HumanMemberCard } from "@/components/units/human-member-card";
import { useExplorerSelection } from "@/components/units/explorer-selection-context";
import type { TreeNode } from "@/components/units/aggregate";

interface MembersTabProps {
  unitId: string;
  unitDisplayName: string;
  /**
   * The sub-unit children of the active unit, sourced from the tree
   * snapshot the Explorer renders against. Sub-unit cards in this tab
   * surface the same rows the left-rail tree shows under this unit,
   * giving operators a single "what belongs to this unit?" view.
   *
   * Agent children that already render via the membership list are
   * filtered out here to avoid double-rendering the same agent.
   */
  childNodes: readonly TreeNode[];
}

type AgentDialogState =
  | { mode: "closed" }
  | { mode: "edit"; membership: UnitMembershipResponse };

type HumanDialogState =
  | { mode: "closed" }
  | { mode: "add" }
  | { mode: "edit"; row: UnitHumanMemberResponse };

/**
 * Members tab for the unit's Detail Pane. Lists agent memberships,
 * sub-units, and human team-role rows.
 *
 *  - Agent rows mutate through the existing
 *    `PUT/DELETE /api/v1/units/{unitId}/memberships/{agentAddress}`
 *    endpoints (unchanged).
 *  - Human rows mutate through the team-role endpoints
 *    (`POST/PATCH/DELETE /api/v1/tenant/units/{id}/members/humans[/...]`),
 *    landed under #2409.
 *  - Sub-unit cards are click-through into the sub-unit's own
 *    Members tab via the explorer-selection bridge.
 */
export function MembersTab({
  unitId,
  unitDisplayName,
  childNodes,
}: MembersTabProps) {
  const { toast } = useToast();
  const { dispatchSelect } = useExplorerSelection();

  // ---------- Agent memberships (unchanged from pre-rename) ----------
  const [memberships, setMemberships] = useState<UnitMembershipResponse[]>([]);
  const [allAgents, setAllAgents] = useState<AgentResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);

  const [addDialogOpen, setAddDialogOpen] = useState(false);
  const [agentDialog, setAgentDialog] = useState<AgentDialogState>({
    mode: "closed",
  });
  const [confirmRemove, setConfirmRemove] =
    useState<UnitMembershipResponse | null>(null);
  const [removing, setRemoving] = useState(false);

  // ---------- Human team-role members (new under #2270 / #2427) ----------
  const humanMembersQuery = useUnitHumanMembers(unitId);
  const meQuery = useCurrentUser();
  const operatorHumanId = meQuery.data?.id ?? null;

  const [humanDialog, setHumanDialog] = useState<HumanDialogState>({
    mode: "closed",
  });
  const [humanSubmitting, setHumanSubmitting] = useState(false);
  const [confirmHumanRemove, setConfirmHumanRemove] =
    useState<UnitHumanMemberResponse | null>(null);
  const [removingHuman, setRemovingHuman] = useState(false);

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
      setLoadError(err);
    } finally {
      setLoading(false);
    }
  }, [unitId]);

  useEffect(() => {
    load();
  }, [load]);

  // The agent directory carries the human-facing metadata (displayName);
  // memberships only carry the address. Index by name so each row can
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

  // Sub-units = every child node of kind `Unit`. We intentionally
  // exclude agents here: they already render via the memberships list
  // above. Filtering on `kind` keeps the view consistent with the
  // explorer-tree's notion of "what belongs to this unit".
  const subUnitChildren = useMemo(
    () => childNodes.filter((c) => c.kind === "Unit"),
    [childNodes],
  );

  const handleUpsert = async (values: MembershipFormValues) => {
    const saved = await api.upsertUnitMembership(unitId, values.agentAddress, {
      model: values.model,
      specialty: values.specialty,
      enabled: values.enabled,
      executionMode: values.executionMode,
    });
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
      title: "Membership updated",
      description: saved.agentAddress,
    });
    setAgentDialog({ mode: "closed" });
  };

  const handleAddDialogOpenChange = useCallback(
    (open: boolean) => {
      setAddDialogOpen(open);
      if (!open) {
        void load();
      }
    },
    [load],
  );

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
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setRemoving(false);
    }
  };

  // ---------- Human team-role member handlers ----------
  const handleHumanSubmit = async (values: HumanMemberFormValues) => {
    setHumanSubmitting(true);
    try {
      if (humanDialog.mode === "add") {
        await api.addUnitHumanMember(unitId, {
          humanId: values.humanId,
          roles: values.roles,
          expertise: values.expertise,
          notifications: values.notifications,
        });
        toast({
          title: "Member added",
          description: values.roles.join(", "),
        });
      } else if (humanDialog.mode === "edit") {
        await api.updateUnitHumanMember(
          unitId,
          humanDialog.row.humanId,
          {
            roles: values.roles,
            expertise: values.expertise,
            notifications: values.notifications,
          },
        );
        toast({
          title: "Member updated",
          description: values.roles.join(", "),
        });
      }
      await humanMembersQuery.refetch();
      setHumanDialog({ mode: "closed" });
    } catch (err) {
      toast({
        title:
          humanDialog.mode === "add" ? "Add failed" : "Update failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setHumanSubmitting(false);
    }
  };

  const handleHumanRemove = async () => {
    const target = confirmHumanRemove;
    if (!target) return;
    setRemovingHuman(true);
    try {
      await api.removeUnitHumanMember(unitId, target.humanId);
      toast({
        title: "Member removed",
        description: target.roles.join(", "),
      });
      await humanMembersQuery.refetch();
      setConfirmHumanRemove(null);
    } catch (err) {
      toast({
        title: "Remove failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setRemovingHuman(false);
    }
  };

  const humanRows = humanMembersQuery.data ?? [];
  const humansLoading = humanMembersQuery.isLoading;
  const humansError = humanMembersQuery.error;

  // Combined empty-state hits when no agents, no sub-units, AND no
  // human members live on this unit. Each section keeps its own
  // section-scoped empty copy when only one bucket is empty.
  const allEmpty =
    !loading &&
    !humansLoading &&
    memberships.length === 0 &&
    subUnitChildren.length === 0 &&
    humanRows.length === 0;

  return (
    <Card data-testid="unit-members-tab">
      <CardHeader className="flex flex-row items-center justify-between space-y-0">
        <CardTitle>Members</CardTitle>
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="outline"
            onClick={() => setHumanDialog({ mode: "add" })}
            disabled={!operatorHumanId}
            aria-label="Add human member"
            data-testid="unit-members-add-human"
          >
            <UserPlus className="mr-1 h-4 w-4" />
            Add human
          </Button>
          <Button
            size="sm"
            onClick={() => setAddDialogOpen(true)}
            // ADR-0039 J2: create-agent lives inside this dialog now, so
            // the button stays enabled even when no agents exist yet — the
            // operator can bootstrap the unit's first agent right here.
            disabled={loading}
            aria-label="Add agent"
            data-testid="unit-members-add-agent"
          >
            <Plus className="mr-1 h-4 w-4" />
            Add agent
          </Button>
        </div>
      </CardHeader>
      <CardContent className="space-y-6">
        {loadError !== null && <ApiErrorMessage error={loadError} />}
        {humansError && (
          <div data-testid="unit-members-humans-error">
            <ApiErrorMessage error={humansError} />
          </div>
        )}

        {allEmpty && (
          <p
            className="text-sm text-muted-foreground"
            data-testid="unit-members-empty"
          >
            No members yet —{" "}
            <button
              type="button"
              className="text-primary underline-offset-2 hover:underline"
              onClick={() => setHumanDialog({ mode: "add" })}
              data-testid="unit-members-empty-add"
            >
              add one
            </button>
            .
          </p>
        )}

        {/* --- Agent members --- */}
        {loading ? (
          <p className="text-sm text-muted-foreground">Loading agents…</p>
        ) : memberships.length > 0 ? (
          <section
            data-testid="unit-members-agents-section"
            className="space-y-2"
          >
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              Agents
            </h3>
            <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {memberships.map((m) => {
                const directoryAgent = agentByName[m.agentAddress];
                const displayName =
                  directoryAgent?.displayName ||
                  directoryAgent?.name ||
                  m.agentAddress;
                const cardAgent: AgentCardAgent = directoryAgent
                  ? {
                      id: directoryAgent.id,
                      name: directoryAgent.name,
                      displayName,
                      role: directoryAgent.role,
                      registeredAt: directoryAgent.registeredAt,
                      parentUnit: unitId,
                      executionMode: m.executionMode,
                    }
                  : {
                      name: m.agentAddress,
                      displayName,
                      role: null,
                      registeredAt: m.createdAt ?? new Date().toISOString(),
                      parentUnit: unitId,
                      executionMode: m.executionMode,
                    };

                return (
                  <li
                    key={m.agentAddress}
                    data-testid={`unit-membership-${m.agentAddress}`}
                    className="space-y-2"
                  >
                    <AgentCard
                      agent={cardAgent}
                      // #2464: dispatch through the Explorer selection
                      // bridge so the primary click doesn't trigger an
                      // App Router same-route RSC navigation — the
                      // pending transition was eating the first click
                      // and leaving the card "highlighted but not
                      // navigated". Mirrors the same pattern the
                      // sibling sub-unit cards already use.
                      onSelect={(name) => dispatchSelect(name)}
                      actions={
                        <>
                          <Button
                            variant="ghost"
                            size="icon"
                            onClick={() =>
                              setAgentDialog({
                                mode: "edit",
                                membership: m,
                              })
                            }
                            aria-label={`Edit ${displayName}`}
                            data-testid={`unit-membership-edit-${m.agentAddress}`}
                            className="h-7 w-7"
                          >
                            <Pencil className="h-3.5 w-3.5" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="icon"
                            onClick={() => setConfirmRemove(m)}
                            aria-label={`Remove ${displayName}`}
                            data-testid={`unit-membership-remove-${m.agentAddress}`}
                            className="h-7 w-7"
                          >
                            <Trash2 className="h-3.5 w-3.5 text-destructive" />
                          </Button>
                        </>
                      }
                    />
                    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">
                      {!m.enabled && <Badge variant="outline">Disabled</Badge>}
                      {m.specialty && (
                        <Badge variant="outline">{m.specialty}</Badge>
                      )}
                      <span>
                        <span className="text-muted-foreground/70">
                          Model:
                        </span>{" "}
                        {m.model ?? "(inherit)"}
                      </span>
                    </div>
                  </li>
                );
              })}
            </ul>
          </section>
        ) : null}

        {/* --- Sub-units --- */}
        {subUnitChildren.length > 0 && (
          <section
            data-testid="unit-members-sub-units-section"
            className="space-y-2"
          >
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              Sub-units
            </h3>
            <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {subUnitChildren.map((child) => (
                <li
                  key={child.id}
                  data-testid={`unit-members-sub-unit-${child.id}`}
                >
                  <UnitCard
                    unit={{
                      name: child.id,
                      displayName: child.name,
                      registeredAt: new Date().toISOString(),
                      status: mapTreeStatusToLifecycle(child.status),
                      id:
                        "definitionId" in child &&
                        typeof child.definitionId === "string"
                          ? child.definitionId
                          : undefined,
                    }}
                    onSelect={(id) => dispatchSelect(id)}
                    onOpenTab={(id) => dispatchSelect(id)}
                  />
                </li>
              ))}
            </ul>
          </section>
        )}

        {/* --- Human members --- */}
        {humansLoading ? (
          <p className="text-sm text-muted-foreground">Loading humans…</p>
        ) : humanRows.length > 0 ? (
          <section
            data-testid="unit-members-humans-section"
            className="space-y-2"
          >
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              Humans
            </h3>
            <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {humanRows.map((row) => (
                <li
                  key={row.membershipId}
                  data-testid={`unit-members-human-${row.membershipId}`}
                >
                  <HumanMemberCard
                    row={row}
                    operatorHumanId={operatorHumanId}
                    onEdit={() => setHumanDialog({ mode: "edit", row })}
                    onRemove={() => setConfirmHumanRemove(row)}
                  />
                </li>
              ))}
            </ul>
          </section>
        ) : null}
      </CardContent>

      <AgentCreateDialog
        unitId={unitId}
        unitDisplayName={unitDisplayName}
        open={addDialogOpen}
        onOpenChange={handleAddDialogOpenChange}
      />

      <MembershipDialog
        open={agentDialog.mode === "edit"}
        initial={agentDialog.mode === "edit" ? agentDialog.membership : null}
        agentDisplayNames={displayNameMap}
        onCancel={() => setAgentDialog({ mode: "closed" })}
        onSubmit={handleUpsert}
      />

      <HumanMemberDialog
        open={humanDialog.mode !== "closed"}
        mode={humanDialog.mode === "edit" ? "edit" : "add"}
        initial={humanDialog.mode === "edit" ? humanDialog.row : null}
        operatorHumanId={operatorHumanId}
        pending={humanSubmitting}
        onCancel={() => setHumanDialog({ mode: "closed" })}
        onSubmit={handleHumanSubmit}
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

      <ConfirmDialog
        open={confirmHumanRemove !== null}
        title="Remove member from unit"
        description={
          confirmHumanRemove
            ? `This removes this human from the unit (roles: ${confirmHumanRemove.roles.join(", ") || "(none)"}). The human's account is not deleted.`
            : undefined
        }
        confirmLabel="Remove"
        confirmVariant="destructive"
        pending={removingHuman}
        onConfirm={handleHumanRemove}
        onCancel={() => setConfirmHumanRemove(null)}
      />
    </Card>
  );
}

// Tree-level `NodeStatus` strings are lowercase (`"running"` /
// `"draft"` / …); `<UnitCard>` accepts both lifecycle casings, but
// converting to the canonical `LifecycleStatus` shape keeps the
// status badge consistent with the rest of the surface.
function mapTreeStatusToLifecycle(status: string): string {
  switch (status) {
    case "running":
      return "Running";
    case "starting":
      return "Starting";
    case "stopping":
      return "Stopping";
    case "validating":
      return "Validating";
    case "paused":
    case "stopped":
      return "Stopped";
    case "error":
      return "Error";
    case "draft":
    default:
      return "Draft";
  }
}

