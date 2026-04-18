"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Eraser, EyeOff, Filter, Plus, Shield, Sparkles, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useUnitBoundary } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  BoundaryOpacityRuleDto,
  BoundaryProjectionRuleDto,
  BoundarySynthesisRuleDto,
  UnitBoundaryResponse,
} from "@/lib/api/types";

interface BoundaryTabProps {
  unitId: string;
}

/**
 * Unit-boundary configuration tab (#495). Surfaces the three dimensions
 * landed by PR-PLAT-BOUND-2 (#413):
 *
 *   - **Opacities** — matcher-only rules that strip entries from the
 *     outside view.
 *   - **Projections** — matcher + rewrite (rename / retag / override
 *     level).
 *   - **Syntheses** — matcher + synthesised replacement (name + optional
 *     description / level).
 *
 * The tab reads `GET /api/v1/units/{id}/boundary`, edits rules locally,
 * and PUTs the full boundary back on Save. The **Clear** button issues
 * DELETE, which the server represents as an empty persisted boundary.
 *
 * The portal targets the same HTTP surface as `spring unit boundary
 * get|set|clear`, so every knob here maps 1:1 to a CLI flag. The only
 * CLI affordance the portal does not yet mirror is `set -f boundary.yaml`
 * bulk upload — tracked in #524.
 */
export function BoundaryTab({ unitId }: BoundaryTabProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const boundaryQuery = useUnitBoundary(unitId);

  const [opacities, setOpacities] = useState<BoundaryOpacityRuleDto[]>([]);
  const [projections, setProjections] = useState<BoundaryProjectionRuleDto[]>(
    [],
  );
  const [syntheses, setSyntheses] = useState<BoundarySynthesisRuleDto[]>([]);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [clearOpen, setClearOpen] = useState(false);
  const [clearing, setClearing] = useState(false);

  const data = boundaryQuery.data;

  // Seed local state from the query response. Only rewrite when not
  // dirty so a mid-edit cache refresh doesn't clobber the user's
  // in-flight changes.
  useEffect(() => {
    if (!data || dirty) return;
    setOpacities(data.opacities ? [...data.opacities] : []);
    setProjections(data.projections ? [...data.projections] : []);
    setSyntheses(data.syntheses ? [...data.syntheses] : []);
  }, [data, dirty]);

  const markDirty = useCallback(() => setDirty(true), []);

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      const body: UnitBoundaryResponse = {
        opacities: opacities.length === 0 ? null : opacities,
        projections: projections.length === 0 ? null : projections,
        syntheses: syntheses.length === 0 ? null : syntheses,
      };
      const stored = await api.setUnitBoundary(unitId, body);
      queryClient.setQueryData(queryKeys.units.boundary(unitId), stored);
      setOpacities(stored.opacities ? [...stored.opacities] : []);
      setProjections(stored.projections ? [...stored.projections] : []);
      setSyntheses(stored.syntheses ? [...stored.syntheses] : []);
      setDirty(false);
      toast({ title: "Boundary saved" });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSaveError(message);
      toast({
        title: "Save failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSaving(false);
    }
  };

  const handleClear = async () => {
    setClearing(true);
    try {
      await api.clearUnitBoundary(unitId);
      setOpacities([]);
      setProjections([]);
      setSyntheses([]);
      setDirty(false);
      setClearOpen(false);
      await queryClient.invalidateQueries({
        queryKey: queryKeys.units.boundary(unitId),
      });
      toast({ title: "Boundary cleared" });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Clear failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setClearing(false);
    }
  };

  const isEmpty = useMemo(
    () =>
      opacities.length === 0 &&
      projections.length === 0 &&
      syntheses.length === 0,
    [opacities.length, projections.length, syntheses.length],
  );

  if (boundaryQuery.isPending) {
    return (
      <Card>
        <CardContent className="space-y-3 p-6">
          <Skeleton className="h-4 w-48" />
          <Skeleton className="h-32" />
        </CardContent>
      </Card>
    );
  }

  if (boundaryQuery.error) {
    return (
      <Card>
        <CardContent className="p-6 text-sm text-destructive">
          {boundaryQuery.error instanceof Error
            ? boundaryQuery.error.message
            : String(boundaryQuery.error)}
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="space-y-4" data-testid="boundary-tab">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Shield className="h-5 w-5" />
            Boundary
            {isEmpty ? (
              <Badge variant="outline">Transparent</Badge>
            ) : (
              <Badge variant="default">Configured</Badge>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <p className="text-muted-foreground">
            Rules that control what outside callers see of this unit&apos;s
            aggregated expertise. A transparent boundary (no rules) makes
            every entry visible. Matches `spring unit boundary get|set|clear`.
          </p>
          <div className="flex flex-wrap items-center gap-2">
            <Button
              onClick={handleSave}
              disabled={saving || !dirty}
              size="sm"
            >
              {saving ? "Saving…" : "Save boundary"}
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setClearOpen(true)}
              disabled={clearing || (isEmpty && !dirty && !data?.opacities && !data?.projections && !data?.syntheses)}
            >
              <Eraser className="mr-1 h-4 w-4" /> Clear all rules
            </Button>
            {dirty && (
              <span className="text-xs text-muted-foreground">
                Unsaved changes
              </span>
            )}
          </div>
          {saveError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-destructive">
              {saveError}
            </p>
          )}
        </CardContent>
      </Card>

      <OpacitiesCard
        rules={opacities}
        onChange={(next) => {
          setOpacities(next);
          markDirty();
        }}
      />
      <ProjectionsCard
        rules={projections}
        onChange={(next) => {
          setProjections(next);
          markDirty();
        }}
      />
      <SynthesesCard
        rules={syntheses}
        onChange={(next) => {
          setSyntheses(next);
          markDirty();
        }}
      />

      <ConfirmDialog
        open={clearOpen}
        title="Clear boundary"
        description="Remove every opacity, projection, and synthesis rule on this unit. Outside callers will see the full raw aggregate until new rules are added."
        confirmLabel="Clear"
        cancelLabel="Cancel"
        onConfirm={handleClear}
        onCancel={() => {
          if (!clearing) setClearOpen(false);
        }}
        pending={clearing}
      />
    </div>
  );
}

// ---- Opacities card ------------------------------------------------------

function OpacitiesCard({
  rules,
  onChange,
}: {
  rules: BoundaryOpacityRuleDto[];
  onChange: (next: BoundaryOpacityRuleDto[]) => void;
}) {
  const [domain, setDomain] = useState("");
  const [origin, setOrigin] = useState("");
  const canAdd = domain.trim() !== "" || origin.trim() !== "";

  const handleAdd = () => {
    if (!canAdd) return;
    onChange([
      ...rules,
      {
        domainPattern: domain.trim() || null,
        originPattern: origin.trim() || null,
      },
    ]);
    setDomain("");
    setOrigin("");
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm">
          <EyeOff className="h-4 w-4" /> Opacities
          <span className="text-xs text-muted-foreground">
            hide matching entries
          </span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {rules.length === 0 ? (
          <p className="text-muted-foreground">No opacity rules.</p>
        ) : (
          <ul
            className="divide-y divide-border rounded-md border border-border"
            data-testid="opacity-rules"
          >
            {rules.map((rule, i) => (
              <li
                key={i}
                className="flex items-center gap-3 px-3 py-2"
              >
                <span className="flex-1 font-mono text-xs">
                  domain: {rule.domainPattern ?? "(any)"} · origin:{" "}
                  {rule.originPattern ?? "(any)"}
                </span>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() =>
                    onChange(rules.filter((_, j) => j !== i))
                  }
                  aria-label={`Remove opacity rule ${i + 1}`}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}

        <div className="rounded-md border border-border p-3 space-y-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Domain pattern
              </span>
              <Input
                value={domain}
                onChange={(e) => setDomain(e.target.value)}
                placeholder="secret-*"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Origin pattern
              </span>
              <Input
                value={origin}
                onChange={(e) => setOrigin(e.target.value)}
                placeholder="agent://internal-*"
              />
            </label>
          </div>
          <div className="flex justify-end">
            <Button size="sm" onClick={handleAdd} disabled={!canAdd}>
              <Plus className="mr-1 h-4 w-4" /> Add opacity
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

// ---- Projections card ----------------------------------------------------

const LEVEL_OPTIONS = [
  { value: "", label: "(no change)" },
  { value: "beginner", label: "beginner" },
  { value: "intermediate", label: "intermediate" },
  { value: "advanced", label: "advanced" },
  { value: "expert", label: "expert" },
] as const;

function ProjectionsCard({
  rules,
  onChange,
}: {
  rules: BoundaryProjectionRuleDto[];
  onChange: (next: BoundaryProjectionRuleDto[]) => void;
}) {
  const [domain, setDomain] = useState("");
  const [origin, setOrigin] = useState("");
  const [rename, setRename] = useState("");
  const [retag, setRetag] = useState("");
  const [level, setLevel] = useState("");

  const canAdd =
    domain.trim() !== "" ||
    origin.trim() !== "" ||
    rename.trim() !== "" ||
    retag.trim() !== "" ||
    level !== "";

  const handleAdd = () => {
    if (!canAdd) return;
    onChange([
      ...rules,
      {
        domainPattern: domain.trim() || null,
        originPattern: origin.trim() || null,
        renameTo: rename.trim() || null,
        retag: retag.trim() || null,
        overrideLevel: level || null,
      },
    ]);
    setDomain("");
    setOrigin("");
    setRename("");
    setRetag("");
    setLevel("");
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm">
          <Filter className="h-4 w-4" /> Projections
          <span className="text-xs text-muted-foreground">
            rewrite matching entries
          </span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {rules.length === 0 ? (
          <p className="text-muted-foreground">No projection rules.</p>
        ) : (
          <ul
            className="divide-y divide-border rounded-md border border-border"
            data-testid="projection-rules"
          >
            {rules.map((rule, i) => (
              <li
                key={i}
                className="flex items-center gap-3 px-3 py-2"
              >
                <span className="flex-1 font-mono text-xs">
                  domain: {rule.domainPattern ?? "(any)"} · origin:{" "}
                  {rule.originPattern ?? "(any)"} · rename:{" "}
                  {rule.renameTo ?? "(no change)"} · retag:{" "}
                  {rule.retag ?? "(no change)"} · level:{" "}
                  {rule.overrideLevel ?? "(no change)"}
                </span>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() =>
                    onChange(rules.filter((_, j) => j !== i))
                  }
                  aria-label={`Remove projection rule ${i + 1}`}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}

        <div className="rounded-md border border-border p-3 space-y-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Domain pattern
              </span>
              <Input
                value={domain}
                onChange={(e) => setDomain(e.target.value)}
                placeholder="react"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Origin pattern
              </span>
              <Input
                value={origin}
                onChange={(e) => setOrigin(e.target.value)}
                placeholder="agent://*"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Rename to</span>
              <Input
                value={rename}
                onChange={(e) => setRename(e.target.value)}
                placeholder="frontend"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Retag</span>
              <Input
                value={retag}
                onChange={(e) => setRetag(e.target.value)}
                placeholder="Frontend stack"
              />
            </label>
            <label className="block space-y-1 sm:col-span-2">
              <span className="text-xs text-muted-foreground">
                Override level
              </span>
              <select
                value={level}
                onChange={(e) => setLevel(e.target.value)}
                className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
                aria-label="Override level"
              >
                {LEVEL_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="flex justify-end">
            <Button size="sm" onClick={handleAdd} disabled={!canAdd}>
              <Plus className="mr-1 h-4 w-4" /> Add projection
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

// ---- Syntheses card ------------------------------------------------------

function SynthesesCard({
  rules,
  onChange,
}: {
  rules: BoundarySynthesisRuleDto[];
  onChange: (next: BoundarySynthesisRuleDto[]) => void;
}) {
  const [name, setName] = useState("");
  const [domain, setDomain] = useState("");
  const [origin, setOrigin] = useState("");
  const [description, setDescription] = useState("");
  const [level, setLevel] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  const handleAdd = () => {
    if (!name.trim()) {
      setFormError("Name is required.");
      return;
    }
    setFormError(null);
    onChange([
      ...rules,
      {
        name: name.trim(),
        domainPattern: domain.trim() || null,
        originPattern: origin.trim() || null,
        description: description.trim() || null,
        level: level || null,
      },
    ]);
    setName("");
    setDomain("");
    setOrigin("");
    setDescription("");
    setLevel("");
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm">
          <Sparkles className="h-4 w-4" /> Syntheses
          <span className="text-xs text-muted-foreground">
            collapse matches into a unit-level entry
          </span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {rules.length === 0 ? (
          <p className="text-muted-foreground">No synthesis rules.</p>
        ) : (
          <ul
            className="divide-y divide-border rounded-md border border-border"
            data-testid="synthesis-rules"
          >
            {rules.map((rule, i) => (
              <li
                key={i}
                className="flex items-center gap-3 px-3 py-2"
              >
                <span className="flex-1 font-mono text-xs">
                  name: {rule.name} · domain: {rule.domainPattern ?? "(any)"}{" "}
                  · origin: {rule.originPattern ?? "(any)"} · level:{" "}
                  {rule.level ?? "(strongest seen)"}
                </span>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() =>
                    onChange(rules.filter((_, j) => j !== i))
                  }
                  aria-label={`Remove synthesis rule ${i + 1}`}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}

        <div className="rounded-md border border-border p-3 space-y-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <label className="block space-y-1 sm:col-span-2">
              <span className="text-xs text-muted-foreground">
                Name <span className="text-destructive">*</span>
              </span>
              <Input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="team-frontend"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Domain pattern
              </span>
              <Input
                value={domain}
                onChange={(e) => setDomain(e.target.value)}
                placeholder="react"
              />
            </label>
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Origin pattern
              </span>
              <Input
                value={origin}
                onChange={(e) => setOrigin(e.target.value)}
                placeholder="agent://*"
              />
            </label>
            <label className="block space-y-1 sm:col-span-2">
              <span className="text-xs text-muted-foreground">Description</span>
              <Input
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Team-level frontend capability"
              />
            </label>
            <label className="block space-y-1 sm:col-span-2">
              <span className="text-xs text-muted-foreground">Level</span>
              <select
                value={level}
                onChange={(e) => setLevel(e.target.value)}
                className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
                aria-label="Synthesis level"
              >
                <option value="">(strongest seen)</option>
                <option value="beginner">beginner</option>
                <option value="intermediate">intermediate</option>
                <option value="advanced">advanced</option>
                <option value="expert">expert</option>
              </select>
            </label>
          </div>
          {formError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {formError}
            </p>
          )}
          <div className="flex justify-end">
            <Button size="sm" onClick={handleAdd}>
              <Plus className="mr-1 h-4 w-4" /> Add synthesis
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
