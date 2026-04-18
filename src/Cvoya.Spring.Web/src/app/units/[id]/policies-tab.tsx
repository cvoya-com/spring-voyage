"use client";

import { useCallback, useMemo, useState } from "react";
import Link from "next/link";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { DollarSign, Gauge, ListChecks, Shield, Zap } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useUnitPolicy } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  AgentExecutionMode,
  CostPolicy,
  ExecutionModePolicy,
  InitiativeLevel,
  InitiativePolicy,
  ModelPolicy,
  SkillPolicy,
  UnitPolicyResponse,
} from "@/lib/api/types";
import { formatCost } from "@/lib/utils";

/**
 * Policies tab for the unit configuration page (PR-R5 / #411).
 *
 * Renders one panel per UnitPolicy dimension — Skill, Model, Cost,
 * Execution mode, Initiative — plus an Effective-policy footer. Every
 * dimension has the same "allow list / block list / caps" shape by
 * design, so the repetition is intentional: once the user learns one
 * panel the others follow (see `docs/design/portal-exploration.md`
 * § 5.6 for the full direction).
 *
 * Every edit routes through `PUT /api/v1/units/{id}/policy`, which is
 * the same surface the CLI's `spring unit policy <dim> set|clear`
 * (#453) rides — the portal and CLI therefore round-trip byte-for-byte
 * identical payloads. Per-dimension edits merge into the current
 * policy rather than minting a fresh one, so editing (for example) the
 * Skill panel never wipes the Cost caps.
 *
 * Inheritance (`docs/architecture/units.md` § "First deny short-
 * circuits") is tracked under #414; for now the Effective-policy
 * footer shows a single hop ("this unit"), matching the CLI's one-hop
 * chain. When parent-unit overlay arrives the rendering slots in
 * without a tab reshape.
 */

const INITIATIVE_LEVELS: InitiativeLevel[] = [
  "Passive",
  "Attentive",
  "Proactive",
  "Autonomous",
];
const EXECUTION_MODES: AgentExecutionMode[] = ["Auto", "OnDemand"];

interface PoliciesTabProps {
  unitId: string;
}

export function PoliciesTab({ unitId }: PoliciesTabProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const policyQuery = useUnitPolicy(unitId);

  const policy: UnitPolicyResponse = policyQuery.data ?? {};

  const [editing, setEditing] = useState<
    "skill" | "model" | "cost" | "execution-mode" | "initiative" | null
  >(null);

  const closeEditor = useCallback(() => setEditing(null), []);

  const saveMutation = useMutation({
    mutationFn: async (next: UnitPolicyResponse) => {
      return await api.setUnitPolicy(unitId, next);
    },
    onSuccess: (updated) => {
      // Hand-seed the cache so the panels reflect the new slot
      // without waiting for a refetch. Mirrors the pattern the
      // General tab uses on save.
      queryClient.setQueryData(queryKeys.units.policy(unitId), updated);
      toast({ title: "Policy saved" });
      closeEditor();
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const clearMutation = useMutation({
    mutationFn: async (
      dim: "skill" | "model" | "cost" | "executionMode" | "initiative",
    ) => {
      // The dimension we're clearing; every other dimension is
      // carried through verbatim so the clear is truly scoped.
      const next: UnitPolicyResponse = { ...policy, [dim]: null };
      return await api.setUnitPolicy(unitId, next);
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.units.policy(unitId), updated);
      toast({ title: "Policy dimension cleared" });
    },
    onError: (err) => {
      toast({
        title: "Clear failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const handleSaveSkill = (slot: SkillPolicy | null) => {
    saveMutation.mutate({ ...policy, skill: slot });
  };
  const handleSaveModel = (slot: ModelPolicy | null) => {
    saveMutation.mutate({ ...policy, model: slot });
  };
  const handleSaveCost = (slot: CostPolicy | null) => {
    saveMutation.mutate({ ...policy, cost: slot });
  };
  const handleSaveExecutionMode = (slot: ExecutionModePolicy | null) => {
    saveMutation.mutate({ ...policy, executionMode: slot });
  };
  const handleSaveInitiative = (slot: InitiativePolicy | null) => {
    saveMutation.mutate({ ...policy, initiative: slot });
  };

  if (policyQuery.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-32" />
        <Skeleton className="h-32" />
        <Skeleton className="h-32" />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <SkillPolicyCard
        value={policy.skill ?? null}
        onEdit={() => setEditing("skill")}
        onClear={() => clearMutation.mutate("skill")}
        busy={saveMutation.isPending || clearMutation.isPending}
      />
      <ModelPolicyCard
        value={policy.model ?? null}
        onEdit={() => setEditing("model")}
        onClear={() => clearMutation.mutate("model")}
        busy={saveMutation.isPending || clearMutation.isPending}
      />
      <CostPolicyCard
        value={policy.cost ?? null}
        onEdit={() => setEditing("cost")}
        onClear={() => clearMutation.mutate("cost")}
        busy={saveMutation.isPending || clearMutation.isPending}
      />
      <ExecutionModePolicyCard
        value={policy.executionMode ?? null}
        onEdit={() => setEditing("execution-mode")}
        onClear={() => clearMutation.mutate("executionMode")}
        busy={saveMutation.isPending || clearMutation.isPending}
      />
      <InitiativePolicyCard
        value={policy.initiative ?? null}
        onEdit={() => setEditing("initiative")}
        onClear={() => clearMutation.mutate("initiative")}
        busy={saveMutation.isPending || clearMutation.isPending}
      />
      <EffectivePolicyCard unitId={unitId} policy={policy} />

      {editing === "skill" && (
        <SkillPolicyDialog
          open
          initial={policy.skill ?? null}
          onCancel={closeEditor}
          onSave={handleSaveSkill}
          saving={saveMutation.isPending}
        />
      )}
      {editing === "model" && (
        <ModelPolicyDialog
          open
          initial={policy.model ?? null}
          onCancel={closeEditor}
          onSave={handleSaveModel}
          saving={saveMutation.isPending}
        />
      )}
      {editing === "cost" && (
        <CostPolicyDialog
          open
          initial={policy.cost ?? null}
          onCancel={closeEditor}
          onSave={handleSaveCost}
          saving={saveMutation.isPending}
        />
      )}
      {editing === "execution-mode" && (
        <ExecutionModePolicyDialog
          open
          initial={policy.executionMode ?? null}
          onCancel={closeEditor}
          onSave={handleSaveExecutionMode}
          saving={saveMutation.isPending}
        />
      )}
      {editing === "initiative" && (
        <InitiativePolicyDialog
          open
          initial={policy.initiative ?? null}
          onCancel={closeEditor}
          onSave={handleSaveInitiative}
          saving={saveMutation.isPending}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Dimension panels
// ---------------------------------------------------------------------------

interface PanelChromeProps {
  title: string;
  icon: React.ReactNode;
  description: string;
  hasValue: boolean;
  children: React.ReactNode;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
  testId: string;
}

function PanelChrome({
  title,
  icon,
  description,
  hasValue,
  children,
  onEdit,
  onClear,
  busy,
  testId,
}: PanelChromeProps) {
  return (
    <Card data-testid={testId}>
      <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          {icon}
          <span>{title}</span>
          {hasValue ? null : (
            <Badge variant="outline" className="ml-2 text-xs font-normal">
              unset
            </Badge>
          )}
        </CardTitle>
        <div className="flex items-center gap-2">
          <Button size="sm" variant="outline" onClick={onEdit} disabled={busy}>
            Edit
          </Button>
          {hasValue && (
            <Button
              size="sm"
              variant="ghost"
              onClick={onClear}
              disabled={busy}
              aria-label={`Clear ${title.toLowerCase()} policy`}
            >
              Clear
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p className="text-xs text-muted-foreground">{description}</p>
        {children}
      </CardContent>
    </Card>
  );
}

function SkillPolicyCard({
  value,
  onEdit,
  onClear,
  busy,
}: {
  value: SkillPolicy | null;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
}) {
  return (
    <PanelChrome
      title="Skill"
      icon={<ListChecks className="h-4 w-4" />}
      description="Tool allow/block list. Empty allow list means allow every skill; blocked entries always deny."
      hasValue={value !== null}
      onEdit={onEdit}
      onClear={onClear}
      busy={busy}
      testId="policies-tab-skill"
    >
      <ListRow label="Allowed" items={value?.allowed ?? null} />
      <ListRow label="Blocked" items={value?.blocked ?? null} />
    </PanelChrome>
  );
}

function ModelPolicyCard({
  value,
  onEdit,
  onClear,
  busy,
}: {
  value: ModelPolicy | null;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
}) {
  return (
    <PanelChrome
      title="Model"
      icon={<Gauge className="h-4 w-4" />}
      description="LLM model allow/block list. Same shape as Skill."
      hasValue={value !== null}
      onEdit={onEdit}
      onClear={onClear}
      busy={busy}
      testId="policies-tab-model"
    >
      <ListRow label="Allowed" items={value?.allowed ?? null} />
      <ListRow label="Blocked" items={value?.blocked ?? null} />
    </PanelChrome>
  );
}

function CostPolicyCard({
  value,
  onEdit,
  onClear,
  busy,
}: {
  value: CostPolicy | null;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
}) {
  return (
    <PanelChrome
      title="Cost"
      icon={<DollarSign className="h-4 w-4" />}
      description="Per-invocation / per-hour / per-day USD caps applied to every member of this unit."
      hasValue={value !== null}
      onEdit={onEdit}
      onClear={onClear}
      busy={busy}
      testId="policies-tab-cost"
    >
      <KvRow
        label="Per invocation"
        value={
          value?.maxCostPerInvocation == null
            ? null
            : formatCost(value.maxCostPerInvocation)
        }
      />
      <KvRow
        label="Per hour"
        value={
          value?.maxCostPerHour == null
            ? null
            : formatCost(value.maxCostPerHour)
        }
      />
      <KvRow
        label="Per day"
        value={
          value?.maxCostPerDay == null ? null : formatCost(value.maxCostPerDay)
        }
      />
      <p className="pt-1 text-xs text-muted-foreground">
        See{" "}
        <Link href="/budgets" className="text-primary underline-offset-2 hover:underline">
          Budgets
        </Link>{" "}
        for current spend against these caps.
      </p>
    </PanelChrome>
  );
}

function ExecutionModePolicyCard({
  value,
  onEdit,
  onClear,
  busy,
}: {
  value: ExecutionModePolicy | null;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
}) {
  return (
    <PanelChrome
      title="Execution mode"
      icon={<Shield className="h-4 w-4" />}
      description="Pin every member to a specific mode (forced) or limit to a whitelist (allowed)."
      hasValue={value !== null}
      onEdit={onEdit}
      onClear={onClear}
      busy={busy}
      testId="policies-tab-execution-mode"
    >
      <KvRow label="Forced" value={value?.forced ?? null} />
      <ListRow label="Allowed" items={value?.allowed ?? null} />
    </PanelChrome>
  );
}

function InitiativePolicyCard({
  value,
  onEdit,
  onClear,
  busy,
}: {
  value: InitiativePolicy | null;
  onEdit: () => void;
  onClear: () => void;
  busy: boolean;
}) {
  return (
    <PanelChrome
      title="Initiative"
      icon={<Zap className="h-4 w-4" />}
      description="Max autonomy level and allow/block list for reflection actions. Applies as a unit-level overlay on per-agent policies."
      hasValue={value !== null}
      onEdit={onEdit}
      onClear={onClear}
      busy={busy}
      testId="policies-tab-initiative"
    >
      <KvRow label="Max level" value={value?.maxLevel ?? null} />
      <KvRow
        label="Require unit approval"
        value={
          value?.requireUnitApproval == null
            ? null
            : value.requireUnitApproval
              ? "yes"
              : "no"
        }
      />
      <ListRow label="Allowed actions" items={value?.allowedActions ?? null} />
      <ListRow label="Blocked actions" items={value?.blockedActions ?? null} />
    </PanelChrome>
  );
}

function EffectivePolicyCard({
  unitId,
  policy,
}: {
  unitId: string;
  policy: UnitPolicyResponse;
}) {
  const anySet = useMemo(
    () =>
      Boolean(
        policy.skill ??
          policy.model ??
          policy.cost ??
          policy.executionMode ??
          policy.initiative,
      ),
    [policy],
  );
  return (
    <Card data-testid="policies-tab-effective">
      <CardHeader>
        <CardTitle className="text-base">Effective policy</CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p className="text-xs text-muted-foreground">
          Inheritance chain starts at this unit. Parent-unit overlay is
          tracked under{" "}
          <a
            href="https://github.com/cvoya-com/spring-voyage/issues/414"
            className="text-primary underline-offset-2 hover:underline"
            target="_blank"
            rel="noreferrer"
          >
            #414
          </a>
          ; for now the chain has a single hop.
        </p>
        <ol className="list-decimal space-y-1 pl-5 text-sm">
          <li>
            <span className="font-mono text-xs">unit://{unitId}</span>
            {anySet ? (
              <span className="ml-2 text-muted-foreground">
                — applies the constraints above.
              </span>
            ) : (
              <span className="ml-2 text-muted-foreground">
                — no constraints set.
              </span>
            )}
          </li>
        </ol>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Small row primitives
// ---------------------------------------------------------------------------

function ListRow({
  label,
  items,
}: {
  label: string;
  items: readonly string[] | null;
}) {
  return (
    <div className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className="flex flex-wrap justify-end gap-1">
        {items && items.length > 0 ? (
          items.map((s) => (
            <Badge key={s} variant="outline" className="font-mono text-xs">
              {s}
            </Badge>
          ))
        ) : (
          <span className="text-xs text-muted-foreground">(none)</span>
        )}
      </span>
    </div>
  );
}

function KvRow({
  label,
  value,
}: {
  label: string;
  value: string | null | undefined;
}) {
  return (
    <div className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className="sm:text-right">
        {value ? (
          value
        ) : (
          <span className="text-xs text-muted-foreground">(unset)</span>
        )}
      </span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Edit dialogs — one per dimension. Each dialog edits a local draft
// and hands the final slot (or null to clear) back to the parent,
// which merges into the unified policy payload.
// ---------------------------------------------------------------------------

function parseCsv(raw: string): string[] | null {
  const parts = raw
    .split(/[,\n]/)
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  return parts.length === 0 ? null : parts;
}

function formatCsv(values: readonly string[] | null | undefined): string {
  return values && values.length > 0 ? values.join(", ") : "";
}

interface AllowBlockDialogProps<T extends { allowed?: string[] | null; blocked?: string[] | null }> {
  open: boolean;
  title: string;
  description: string;
  initial: T | null;
  onCancel: () => void;
  onSave: (slot: T | null) => void;
  saving: boolean;
  buildSlot: (allowed: string[] | null, blocked: string[] | null) => T;
  testId: string;
}

function AllowBlockDialog<T extends { allowed?: string[] | null; blocked?: string[] | null }>({
  open,
  title,
  description,
  initial,
  onCancel,
  onSave,
  saving,
  buildSlot,
  testId,
}: AllowBlockDialogProps<T>) {
  const [allowed, setAllowed] = useState(
    formatCsv(initial?.allowed ?? null),
  );
  const [blocked, setBlocked] = useState(
    formatCsv(initial?.blocked ?? null),
  );

  const handleSave = () => {
    const a = parseCsv(allowed);
    const b = parseCsv(blocked);
    if (a === null && b === null) {
      // Empty save is a clear — route through null so the server
      // removes the dimension slot entirely.
      onSave(null);
      return;
    }
    onSave(buildSlot(a, b));
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={title}
      description={description}
      footer={
        <div className="flex justify-end gap-2" data-testid={`${testId}-footer`}>
          <Button variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm" data-testid={testId}>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Allowed (comma-separated; empty means allow all)
          </span>
          <Input
            value={allowed}
            onChange={(e) => setAllowed(e.target.value)}
            placeholder="e.g. github, filesystem, http"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Blocked (comma-separated)
          </span>
          <Input
            value={blocked}
            onChange={(e) => setBlocked(e.target.value)}
            placeholder="e.g. shell"
          />
        </label>
      </div>
    </Dialog>
  );
}

function SkillPolicyDialog(props: {
  open: boolean;
  initial: SkillPolicy | null;
  onCancel: () => void;
  onSave: (slot: SkillPolicy | null) => void;
  saving: boolean;
}) {
  return (
    <AllowBlockDialog<SkillPolicy>
      {...props}
      title="Edit skill policy"
      description="Tool allow/block list enforced for every member of this unit."
      buildSlot={(allowed, blocked) => ({ allowed, blocked })}
      testId="skill-policy-dialog"
    />
  );
}

function ModelPolicyDialog(props: {
  open: boolean;
  initial: ModelPolicy | null;
  onCancel: () => void;
  onSave: (slot: ModelPolicy | null) => void;
  saving: boolean;
}) {
  return (
    <AllowBlockDialog<ModelPolicy>
      {...props}
      title="Edit model policy"
      description="LLM model allow/block list enforced for every member of this unit."
      buildSlot={(allowed, blocked) => ({ allowed, blocked })}
      testId="model-policy-dialog"
    />
  );
}

function CostPolicyDialog({
  open,
  initial,
  onCancel,
  onSave,
  saving,
}: {
  open: boolean;
  initial: CostPolicy | null;
  onCancel: () => void;
  onSave: (slot: CostPolicy | null) => void;
  saving: boolean;
}) {
  const [maxPerInvocation, setMaxPerInvocation] = useState(
    initial?.maxCostPerInvocation?.toString() ?? "",
  );
  const [maxPerHour, setMaxPerHour] = useState(
    initial?.maxCostPerHour?.toString() ?? "",
  );
  const [maxPerDay, setMaxPerDay] = useState(
    initial?.maxCostPerDay?.toString() ?? "",
  );

  const toNumber = (raw: string): number | null => {
    const trimmed = raw.trim();
    if (trimmed.length === 0) return null;
    const n = Number(trimmed);
    if (!Number.isFinite(n) || n < 0) return null;
    return n;
  };

  const handleSave = () => {
    const inv = toNumber(maxPerInvocation);
    const hour = toNumber(maxPerHour);
    const day = toNumber(maxPerDay);
    if (inv === null && hour === null && day === null) {
      onSave(null);
      return;
    }
    onSave({
      maxCostPerInvocation: inv ?? undefined,
      maxCostPerHour: hour ?? undefined,
      maxCostPerDay: day ?? undefined,
    });
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title="Edit cost policy"
      description="Per-invocation, per-hour, and per-day USD caps. Leave blank to skip that window."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm" data-testid="cost-policy-dialog">
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Max per invocation (USD)
          </span>
          <Input
            type="number"
            min="0"
            step="0.01"
            value={maxPerInvocation}
            onChange={(e) => setMaxPerInvocation(e.target.value)}
            placeholder="e.g. 0.50"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">Max per hour (USD)</span>
          <Input
            type="number"
            min="0"
            step="0.01"
            value={maxPerHour}
            onChange={(e) => setMaxPerHour(e.target.value)}
            placeholder="e.g. 5.00"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">Max per day (USD)</span>
          <Input
            type="number"
            min="0"
            step="0.01"
            value={maxPerDay}
            onChange={(e) => setMaxPerDay(e.target.value)}
            placeholder="e.g. 25.00"
          />
        </label>
      </div>
    </Dialog>
  );
}

function ExecutionModePolicyDialog({
  open,
  initial,
  onCancel,
  onSave,
  saving,
}: {
  open: boolean;
  initial: ExecutionModePolicy | null;
  onCancel: () => void;
  onSave: (slot: ExecutionModePolicy | null) => void;
  saving: boolean;
}) {
  const [forced, setForced] = useState<AgentExecutionMode | "">(
    (initial?.forced as AgentExecutionMode | null) ?? "",
  );
  const [allowedSet, setAllowedSet] = useState<Set<AgentExecutionMode>>(
    new Set(initial?.allowed ?? []),
  );

  const toggleAllowed = (mode: AgentExecutionMode) => {
    setAllowedSet((prev) => {
      const next = new Set(prev);
      if (next.has(mode)) {
        next.delete(mode);
      } else {
        next.add(mode);
      }
      return next;
    });
  };

  const handleSave = () => {
    const allowed =
      allowedSet.size === 0
        ? null
        : (Array.from(allowedSet) as AgentExecutionMode[]);
    const forcedValue = forced === "" ? null : forced;
    if (allowed === null && forcedValue === null) {
      onSave(null);
      return;
    }
    onSave({
      forced: forcedValue,
      allowed,
    });
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title="Edit execution mode policy"
      description="Pin every member to one mode (forced) or limit to a whitelist (allowed). Leave both unset to clear the dimension."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm" data-testid="execution-mode-policy-dialog">
        <label className="block space-y-1">
          <span className="text-muted-foreground">Forced mode</span>
          <select
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            value={forced}
            onChange={(e) =>
              setForced(e.target.value as AgentExecutionMode | "")
            }
          >
            <option value="">(none)</option>
            {EXECUTION_MODES.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        </label>
        <div className="space-y-1">
          <span className="text-muted-foreground">Allowed modes</span>
          <div className="flex flex-wrap gap-3">
            {EXECUTION_MODES.map((m) => (
              <label key={m} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={allowedSet.has(m)}
                  onChange={() => toggleAllowed(m)}
                />
                <span>{m}</span>
              </label>
            ))}
          </div>
        </div>
      </div>
    </Dialog>
  );
}

function InitiativePolicyDialog({
  open,
  initial,
  onCancel,
  onSave,
  saving,
}: {
  open: boolean;
  initial: InitiativePolicy | null;
  onCancel: () => void;
  onSave: (slot: InitiativePolicy | null) => void;
  saving: boolean;
}) {
  const [maxLevel, setMaxLevel] = useState<InitiativeLevel | "">(
    initial?.maxLevel ?? "",
  );
  const [requireUnitApproval, setRequireUnitApproval] = useState<boolean>(
    initial?.requireUnitApproval ?? false,
  );
  const [allowedActions, setAllowedActions] = useState(
    formatCsv(initial?.allowedActions ?? null),
  );
  const [blockedActions, setBlockedActions] = useState(
    formatCsv(initial?.blockedActions ?? null),
  );

  const handleSave = () => {
    const allowed = parseCsv(allowedActions);
    const blocked = parseCsv(blockedActions);
    if (
      maxLevel === "" &&
      allowed === null &&
      blocked === null &&
      requireUnitApproval === false
    ) {
      onSave(null);
      return;
    }
    const slot: InitiativePolicy = {
      maxLevel: maxLevel === "" ? undefined : (maxLevel as InitiativeLevel),
      requireUnitApproval,
      allowedActions: allowed,
      blockedActions: blocked,
      // Tier configs are carried through verbatim — they're edited on
      // the per-agent Initiative surface, not at the unit level.
      tier1: initial?.tier1,
      tier2: initial?.tier2,
    };
    onSave(slot);
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title="Edit initiative policy"
      description="Unit-level overlay on the per-agent initiative policy. Restricts max level and adds deny overrides on reflection actions."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm" data-testid="initiative-policy-dialog">
        <label className="block space-y-1">
          <span className="text-muted-foreground">Max level</span>
          <select
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            value={maxLevel}
            onChange={(e) =>
              setMaxLevel(e.target.value as InitiativeLevel | "")
            }
          >
            <option value="">(inherit)</option>
            {INITIATIVE_LEVELS.map((lvl) => (
              <option key={lvl} value={lvl}>
                {lvl}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={requireUnitApproval}
            onChange={(e) => setRequireUnitApproval(e.target.checked)}
          />
          <span>Require unit approval for initiative-triggered actions</span>
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Allowed actions (comma-separated)
          </span>
          <Input
            value={allowedActions}
            onChange={(e) => setAllowedActions(e.target.value)}
            placeholder="e.g. send-message, update-state"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-muted-foreground">
            Blocked actions (comma-separated)
          </span>
          <Input
            value={blockedActions}
            onChange={(e) => setBlockedActions(e.target.value)}
            placeholder="e.g. agent.spawn"
          />
        </label>
      </div>
    </Dialog>
  );
}
