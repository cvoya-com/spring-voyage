"use client";

/**
 * Shared editor for the per-entity expertise list (#486). Works for both
 * agents and units: the caller supplies the current domain list, the
 * persisted domain list, and an async `onSave(domains)` that performs the
 * PUT (full replace — the server always returns the authoritative list).
 *
 * The editor is deliberately list-shaped rather than row-by-row commit:
 * the server only exposes the "replace the entire list" verb, so the UI
 * collects edits in local state and commits them together when the user
 * clicks Save. `spring {agent|unit} expertise set` mirrors the same
 * shape, keeping the CLI/UI parity contract (`AGENTS.md §
 * Concurrent Agents`, `docs/guide/portal.md § CLI/UI parity`).
 */

import { useEffect, useMemo, useState } from "react";
import { Plus, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import {
  EXPERTISE_LEVELS,
  type ExpertiseDomainDto,
  type ExpertiseLevel,
} from "@/lib/api/types";

interface ExpertiseEditorProps {
  /** The domain list currently persisted on the server. */
  domains: ExpertiseDomainDto[];
  /** Persists a replacement list. Throws on failure. */
  onSave: (next: ExpertiseDomainDto[]) => Promise<ExpertiseDomainDto[]>;
  /** Disables inputs (e.g. while a parent refetch is in flight). */
  disabled?: boolean;
  /**
   * When true, the editor renders row-by-row delete icons and the
   * "Add domain" row inline. Defaults to true.
   */
  editable?: boolean;
}

interface DraftRow {
  name: string;
  description: string;
  level: "" | ExpertiseLevel;
}

function toDraft(list: ExpertiseDomainDto[]): DraftRow[] {
  return list.map((d) => ({
    name: d.name ?? "",
    description: d.description ?? "",
    level: isLevel(d.level) ? d.level : "",
  }));
}

function isLevel(value: unknown): value is ExpertiseLevel {
  return (
    typeof value === "string" &&
    (EXPERTISE_LEVELS as readonly string[]).includes(value)
  );
}

function draftToWire(rows: DraftRow[]): ExpertiseDomainDto[] {
  return rows
    .map((r) => ({
      name: r.name.trim(),
      description: r.description.trim(),
      level: r.level === "" ? null : r.level,
    }))
    .filter((r) => r.name !== "");
}

function rowsEqual(a: DraftRow[], b: DraftRow[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].name !== b[i].name) return false;
    if (a[i].description !== b[i].description) return false;
    if (a[i].level !== b[i].level) return false;
  }
  return true;
}

export function ExpertiseEditor({
  domains,
  onSave,
  disabled,
  editable = true,
}: ExpertiseEditorProps) {
  const { toast } = useToast();

  const persisted = useMemo(() => toDraft(domains), [domains]);
  const [rows, setRows] = useState<DraftRow[]>(persisted);
  const [saving, setSaving] = useState(false);

  // Re-sync the editor whenever the persisted list changes (e.g. after a
  // successful save, or when the page refetches the entity's expertise).
  // Effectively discards unsaved local edits — the "Revert" button is the
  // explicit way to do that too, and unsaved edits are also considered
  // overwritten if the server value changes.
  useEffect(() => {
    setRows(persisted);
  }, [persisted]);

  const dirty = !rowsEqual(rows, persisted);

  const setRow = (index: number, patch: Partial<DraftRow>) => {
    setRows((prev) => {
      const next = [...prev];
      next[index] = { ...next[index], ...patch };
      return next;
    });
  };

  const addRow = () => {
    setRows((prev) => [...prev, { name: "", description: "", level: "" }]);
  };

  const removeRow = (index: number) => {
    setRows((prev) => prev.filter((_, i) => i !== index));
  };

  const handleRevert = () => {
    setRows(persisted);
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      const payload = draftToWire(rows);
      await onSave(payload);
      toast({ title: "Expertise saved" });
    } catch (err) {
      toast({
        title: "Failed to save expertise",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setSaving(false);
    }
  };

  if (!editable && rows.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">No expertise configured.</p>
    );
  }

  return (
    <div className="space-y-3">
      {rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No expertise configured. Add a domain below — name is required;
          description and level are optional.
        </p>
      ) : (
        <ul className="space-y-2" aria-label="Expertise domains">
          {rows.map((row, i) => (
            <li
              key={i}
              data-testid={`expertise-row-${i}`}
              className="rounded-md border border-border p-3"
            >
              {editable ? (
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_140px_2fr_auto]">
                  <Input
                    aria-label={`Domain name (row ${i + 1})`}
                    placeholder="e.g. python/fastapi"
                    value={row.name}
                    onChange={(e) => setRow(i, { name: e.target.value })}
                    disabled={disabled || saving}
                  />
                  <select
                    aria-label={`Level (row ${i + 1})`}
                    value={row.level}
                    onChange={(e) =>
                      setRow(i, {
                        level: (e.target.value === ""
                          ? ""
                          : (e.target.value as ExpertiseLevel)),
                      })
                    }
                    disabled={disabled || saving}
                    className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    <option value="">(unspecified)</option>
                    {EXPERTISE_LEVELS.map((lvl) => (
                      <option key={lvl} value={lvl}>
                        {lvl}
                      </option>
                    ))}
                  </select>
                  <Input
                    aria-label={`Description (row ${i + 1})`}
                    placeholder="Optional description"
                    value={row.description}
                    onChange={(e) =>
                      setRow(i, { description: e.target.value })
                    }
                    disabled={disabled || saving}
                  />
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    onClick={() => removeRow(i)}
                    aria-label={`Remove domain ${row.name || i + 1}`}
                    disabled={disabled || saving}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              ) : (
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-mono text-xs">{row.name}</span>
                  {row.level && (
                    <Badge variant="secondary">{row.level}</Badge>
                  )}
                  {row.description && (
                    <span className="text-muted-foreground">
                      — {row.description}
                    </span>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      {editable && (
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={addRow}
            disabled={disabled || saving}
          >
            <Plus className="mr-1 h-4 w-4" /> Add domain
          </Button>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={handleRevert}
              disabled={!dirty || disabled || saving}
            >
              Revert
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={handleSave}
              disabled={!dirty || disabled || saving}
            >
              {saving ? "Saving…" : "Save"}
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
