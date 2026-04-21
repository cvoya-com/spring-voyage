"use client";

import { useCallback, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Plus, Route, Trash2, Workflow } from "lucide-react";

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
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useUnitOrchestration, useUnitPolicy } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  LabelRoutingPolicy,
  OrchestrationStrategyKey,
  UnitOrchestrationResponse,
  UnitPolicyResponse,
} from "@/lib/api/types";
import { ORCHESTRATION_STRATEGIES } from "@/lib/api/types";

/**
 * Orchestration tab for the unit detail page (#602, #606).
 *
 * Surfaces two slices the rest of the platform already understands:
 *
 * - **Strategy selector** — the three platform-offered
 *   `IOrchestrationStrategy` keys (`ai`, `workflow`, `label-routed`).
 *   Fully editable through the dedicated
 *   `GET/PUT/DELETE /api/v1/units/{id}/orchestration` endpoint (#606)
 *   so the dropdown writes directly rather than linking out to
 *   `spring apply`.
 * - **Label routing rules** — the sixth `UnitPolicy` dimension that the
 *   `label-routed` strategy consumes (#389). Editable through the
 *   existing `/api/v1/units/{id}/policy` endpoint so the portal and CLI
 *   round-trip the same wire shape.
 *
 * A read-only **Effective strategy** line shows the resolver's current
 * selection per ADR-0010: manifest key → `LabelRouting` inference →
 * unkeyed default. All three hops are observable from the portal now
 * that the dedicated orchestration endpoint (#606) surfaces the
 * manifest-declared key to the browser.
 */

interface OrchestrationTabProps {
  unitId: string;
}

interface EffectiveStrategy {
  key: OrchestrationStrategyKey;
  source: "manifest" | "policy-inference" | "default";
  reason: string;
}

export function OrchestrationTab({ unitId }: OrchestrationTabProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const policyQuery = useUnitPolicy(unitId);
  const orchestrationQuery = useUnitOrchestration(unitId);

  const policy: UnitPolicyResponse = policyQuery.data ?? {};
  const labelRouting = policy.labelRouting ?? null;
  const manifestStrategy = (orchestrationQuery.data?.strategy ?? null) as
    | OrchestrationStrategyKey
    | null;

  // Effective strategy derivation — all three hops of the resolver
  // ladder (ADR-0010) are now observable through the portal: the
  // dedicated `/orchestration` endpoint (#606) surfaces the manifest-
  // declared key, the existing `/policy` endpoint covers the
  // LabelRouting inference, and the default falls through last.
  const effective = useMemo<EffectiveStrategy>(() => {
    if (manifestStrategy) {
      return {
        key: manifestStrategy,
        source: "manifest",
        reason:
          "orchestration.strategy is set on the unit; resolver dispatches through the matching IOrchestrationStrategy registration.",
      };
    }
    if (labelRouting) {
      return {
        key: "label-routed",
        source: "policy-inference",
        reason:
          "No manifest strategy and UnitPolicy.LabelRouting is set; resolver infers label-routed per ADR-0007.",
      };
    }
    return {
      key: "ai",
      source: "default",
      reason:
        "No manifest strategy and no LabelRouting policy; resolver falls through to the platform default.",
    };
  }, [labelRouting, manifestStrategy]);

  const setLabelRoutingMutation = useMutation({
    mutationFn: async (next: LabelRoutingPolicy | null) => {
      const body: UnitPolicyResponse = { ...policy, labelRouting: next };
      return await api.setUnitPolicy(unitId, body);
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.units.policy(unitId), updated);
      toast({ title: "Label routing saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  // Strategy writer (#606). `null` means "clear" — the dedicated
  // DELETE verb strips the slot so the resolver falls through to
  // policy inference / the unkeyed default.
  const setStrategyMutation = useMutation({
    mutationFn: async (
      next: OrchestrationStrategyKey | null,
    ): Promise<UnitOrchestrationResponse> => {
      if (next === null) {
        await api.clearUnitOrchestration(unitId);
        return { strategy: null };
      }
      return await api.setUnitOrchestration(unitId, { strategy: next });
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(
        queryKeys.units.orchestration(unitId),
        updated,
      );
      toast({
        title: updated.strategy
          ? `Strategy set to ${updated.strategy}`
          : "Strategy cleared (falling back to inferred / default)",
      });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  // Key the label-routing card by a stable fingerprint of the server
  // value so the child remounts (and re-seeds its local edit state
  // from scratch) whenever the server payload changes identity. This
  // is the idiomatic alternative to calling `setState` inside an
  // effect and avoids the cascading-render pattern flagged by
  // `react-hooks/set-state-in-effect`.
  const labelRoutingKey = useMemo(
    () => JSON.stringify(labelRouting ?? null),
    [labelRouting],
  );

  if (policyQuery.isPending || orchestrationQuery.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-28" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  return (
    <div className="space-y-4" data-testid="orchestration-tab">
      <StrategyCard
        manifestStrategy={manifestStrategy}
        effective={effective}
        onChange={(next) => setStrategyMutation.mutate(next)}
        busy={setStrategyMutation.isPending}
      />
      <EffectiveStrategyLine effective={effective} />
      <LabelRoutingCard
        key={labelRoutingKey}
        value={labelRouting}
        onSave={(next) => setLabelRoutingMutation.mutate(next)}
        onClear={() => setLabelRoutingMutation.mutate(null)}
        busy={setLabelRoutingMutation.isPending}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Strategy selector — fully editable (#606). Writes through the dedicated
// `/api/v1/units/{id}/orchestration` endpoint so a change here persists on
// the same JSON slot the manifest apply path writes. An empty selection
// (`— inferred / default —`) issues a DELETE so the resolver falls back to
// policy inference / the unkeyed default per ADR-0010.
// ---------------------------------------------------------------------------

const MANIFEST_UNSET_VALUE = "__unset__";

interface StrategyCardProps {
  manifestStrategy: OrchestrationStrategyKey | null;
  effective: EffectiveStrategy;
  onChange: (next: OrchestrationStrategyKey | null) => void;
  busy: boolean;
}

function StrategyCard({
  manifestStrategy,
  effective,
  onChange,
  busy,
}: StrategyCardProps) {
  const selectedValue = manifestStrategy ?? MANIFEST_UNSET_VALUE;

  return (
    <Card data-testid="orchestration-strategy-card">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Workflow className="h-4 w-4" />
          Strategy
          <Badge variant="outline" className="ml-2 text-xs font-normal">
            {manifestStrategy
              ? "manifest"
              : effective.source === "policy-inference"
                ? "inferred"
                : "default"}
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-muted-foreground">
          Picks the <code>IOrchestrationStrategy</code> the unit dispatches
          through on every domain message. Platform-offered strategies:{" "}
          <code>ai</code>, <code>workflow</code>, <code>label-routed</code>.
          Clearing the selection lets the resolver fall back through the
          precedence ladder (manifest → LabelRouting inference → default,
          ADR-0010).
        </p>
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Manifest strategy
          </span>
          <select
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm disabled:cursor-not-allowed disabled:opacity-70"
            value={selectedValue}
            disabled={busy}
            onChange={(e) => {
              const next = e.target.value;
              if (next === MANIFEST_UNSET_VALUE) {
                onChange(null);
              } else {
                onChange(next as OrchestrationStrategyKey);
              }
            }}
            aria-label="Orchestration strategy"
            data-testid="orchestration-strategy-select"
          >
            <option value={MANIFEST_UNSET_VALUE}>
              — inferred / default —
            </option>
            {ORCHESTRATION_STRATEGIES.map((key) => (
              <option key={key} value={key}>
                {key}
              </option>
            ))}
          </select>
        </label>
        <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
          Edits write through{" "}
          <code>PUT /api/v1/units/{"{id}"}/orchestration</code> — the
          same persistence slot as{" "}
          <code>orchestration.strategy</code> in a{" "}
          <code>spring apply -f unit.yaml</code> manifest. Clearing the slot
          issues a <code>DELETE</code>, after which the resolver falls back
          to <code>UnitPolicy.LabelRouting</code>-inferred{" "}
          <code>label-routed</code> (when set) or the unkeyed platform
          default.
        </p>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Effective-strategy status line — the resolver's per-message answer,
// computed from what the portal can see.
// ---------------------------------------------------------------------------

function EffectiveStrategyLine({
  effective,
}: {
  effective: EffectiveStrategy;
}) {
  return (
    <Card data-testid="orchestration-effective">
      <CardHeader>
        <CardTitle className="text-base">Effective strategy</CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p>
          <span className="font-mono text-xs">{effective.key}</span>
          <span className="ml-2 text-muted-foreground">
            — resolved via{" "}
            {effective.source === "manifest"
              ? "manifest key"
              : effective.source === "policy-inference"
                ? "policy inference"
                : "platform default"}
            .
          </span>
        </p>
        <p className="text-xs text-muted-foreground">{effective.reason}</p>
        <p className="text-xs text-muted-foreground">
          Resolver precedence (per ADR-0010): manifest{" "}
          <code>orchestration.strategy</code> → policy inference (
          <code>UnitPolicy.LabelRouting</code> non-null ⇒{" "}
          <code>label-routed</code>) → unkeyed platform default.
        </p>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Label routing editor — the editable half of the tab. Rides the existing
// `/api/v1/units/{id}/policy` endpoint so CLI parity is already in place
// via `spring unit policy label-routing set|clear`.
// ---------------------------------------------------------------------------

interface LabelRoutingCardProps {
  value: LabelRoutingPolicy | null;
  onSave: (next: LabelRoutingPolicy | null) => void;
  onClear: () => void;
  busy: boolean;
}

function LabelRoutingCard({
  value,
  onSave,
  onClear,
  busy,
}: LabelRoutingCardProps) {
  // The parent keys this component by the server payload, so a
  // cache-refresh that carries a new value simply remounts this card
  // and re-runs the initializers. That keeps dirty local edits
  // isolated from server-state drift without calling `setState` from
  // an effect.
  const [entries, setEntries] = useState<Array<[string, string]>>(() => {
    const triggers = value?.triggerLabels ?? null;
    return triggers
      ? Object.entries(triggers).map(([label, target]) => [label, target])
      : [];
  });
  const [addOnAssign, setAddOnAssign] = useState(() =>
    (value?.addOnAssign ?? []).join(", "),
  );
  const [removeOnAssign, setRemoveOnAssign] = useState(() =>
    (value?.removeOnAssign ?? []).join(", "),
  );
  const [dirty, setDirty] = useState(false);

  const markDirty = useCallback(() => setDirty(true), []);

  const [newLabel, setNewLabel] = useState("");
  const [newTarget, setNewTarget] = useState("");

  const handleAdd = () => {
    const label = newLabel.trim();
    const target = newTarget.trim();
    if (!label || !target) return;
    setEntries([...entries, [label, target]]);
    setNewLabel("");
    setNewTarget("");
    markDirty();
  };

  const handleRemove = (index: number) => {
    setEntries(entries.filter((_, i) => i !== index));
    markDirty();
  };

  const handleUpdate = (index: number, label: string, target: string) => {
    const next = entries.slice();
    next[index] = [label, target];
    setEntries(next);
    markDirty();
  };

  const handleSave = () => {
    const triggerEntries = entries.filter(
      ([label, target]) => label.trim() !== "" && target.trim() !== "",
    );
    const triggerLabels =
      triggerEntries.length === 0
        ? null
        : Object.fromEntries(
            triggerEntries.map(([label, target]) => [
              label.trim(),
              target.trim(),
            ]),
          );
    const add = parseCsv(addOnAssign);
    const remove = parseCsv(removeOnAssign);
    if (triggerLabels === null && add === null && remove === null) {
      // All three slots empty → clear the dimension.
      onClear();
      setDirty(false);
      return;
    }
    onSave({
      triggerLabels,
      addOnAssign: add,
      removeOnAssign: remove,
    });
    setDirty(false);
  };

  const hasAny = value !== null;

  return (
    <Card data-testid="orchestration-label-routing-card">
      <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <Route className="h-4 w-4" />
          <span>Label routing</span>
          {hasAny ? (
            <Badge variant="default" className="ml-2 text-xs font-normal">
              Configured
            </Badge>
          ) : (
            <Badge variant="outline" className="ml-2 text-xs font-normal">
              Unset
            </Badge>
          )}
        </CardTitle>
        <div className="flex items-center gap-2">
          {hasAny && (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                onClear();
                setDirty(false);
              }}
              disabled={busy}
              aria-label="Clear label routing policy"
            >
              Clear
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-muted-foreground">
          Maps inbound-message labels onto unit members so the{" "}
          <code>label-routed</code> strategy can dispatch by human-applied
          tag. Matching is case-insensitive set intersection — the first
          payload label that hits the map wins and its target member
          receives the message. Drops the message when no configured label
          matches. Matches <code>spring unit policy label-routing</code>.
        </p>

        <div className="space-y-2">
          <div className="text-xs font-medium text-muted-foreground">
            Trigger labels → target member
          </div>
          {entries.length === 0 ? (
            <p className="text-muted-foreground">No trigger labels.</p>
          ) : (
            <ul
              className="divide-y divide-border rounded-md border border-border"
              data-testid="label-routing-rules"
            >
              {entries.map(([label, target], i) => (
                <li
                  key={i}
                  className="flex items-center gap-2 px-3 py-2"
                >
                  <Input
                    className="h-8 max-w-[200px] font-mono text-xs"
                    value={label}
                    onChange={(e) => handleUpdate(i, e.target.value, target)}
                    aria-label={`Trigger label ${i + 1}`}
                    placeholder="frontend"
                  />
                  <span className="text-muted-foreground">→</span>
                  <Input
                    className="h-8 flex-1 font-mono text-xs"
                    value={target}
                    onChange={(e) => handleUpdate(i, label, e.target.value)}
                    aria-label={`Target for label ${label || `rule ${i + 1}`}`}
                    placeholder="frontend-engineer"
                  />
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleRemove(i)}
                    aria-label={`Remove trigger label ${label || `rule ${i + 1}`}`}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </li>
              ))}
            </ul>
          )}

          <div className="grid grid-cols-1 gap-2 rounded-md border border-border p-3 sm:grid-cols-[1fr,auto,1fr,auto]">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Label</span>
              <Input
                value={newLabel}
                onChange={(e) => setNewLabel(e.target.value)}
                placeholder="frontend"
                data-testid="label-routing-new-label"
              />
            </label>
            <span className="hidden self-end pb-2 text-muted-foreground sm:block">
              →
            </span>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Target member (agent or sub-unit path)
              </span>
              <Input
                value={newTarget}
                onChange={(e) => setNewTarget(e.target.value)}
                placeholder="frontend-engineer"
                data-testid="label-routing-new-target"
              />
            </label>
            <div className="flex items-end justify-end">
              <Button
                size="sm"
                onClick={handleAdd}
                disabled={!newLabel.trim() || !newTarget.trim()}
              >
                <Plus className="mr-1 h-4 w-4" /> Add
              </Button>
            </div>
          </div>
        </div>

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">
              Add labels on assign (comma-separated)
            </span>
            <Input
              value={addOnAssign}
              onChange={(e) => {
                setAddOnAssign(e.target.value);
                markDirty();
              }}
              placeholder="in-progress"
              data-testid="label-routing-add-on-assign"
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">
              Remove labels on assign (comma-separated)
            </span>
            <Input
              value={removeOnAssign}
              onChange={(e) => {
                setRemoveOnAssign(e.target.value);
                markDirty();
              }}
              placeholder="needs-triage"
              data-testid="label-routing-remove-on-assign"
            />
          </label>
        </div>

        <div className="flex items-center justify-end gap-2">
          {dirty && (
            <span className="text-xs text-muted-foreground">
              Unsaved changes
            </span>
          )}
          <Button size="sm" onClick={handleSave} disabled={busy || !dirty}>
            {busy ? "Saving…" : "Save label routing"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

function parseCsv(raw: string): string[] | null {
  const parts = raw
    .split(/[,\n]/)
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  return parts.length === 0 ? null : parts;
}
