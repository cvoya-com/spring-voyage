"use client";

import { useMemo, useState } from "react";
import { FileUp, Upload } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  BoundaryYamlParseError,
  parseBoundaryYaml,
} from "@/lib/boundary/parse-yaml";
import {
  diffBoundaries,
  formatOpacity,
  formatProjection,
  formatSynthesis,
  type BoundaryDiff,
  type RuleDiffEntry,
} from "@/lib/boundary/diff";
import type {
  BoundaryOpacityRuleDto,
  BoundaryProjectionRuleDto,
  BoundarySynthesisRuleDto,
  UnitBoundaryResponse,
} from "@/lib/api/types";

interface BoundaryYamlUploadProps {
  /** The boundary currently persisted on the unit. Used as the diff baseline. */
  current: UnitBoundaryResponse | null | undefined;
  /**
   * Called when the operator confirms an upload. Should PUT the body through
   * the same {@link api.setUnitBoundary} path the per-rule form uses so the
   * two editors converge on one server mutation.
   */
  onApply: (body: UnitBoundaryResponse) => Promise<void>;
  /** True while {@link onApply} is in flight so the button disables. */
  applying?: boolean;
}

/**
 * YAML upload / paste mode for the boundary tab (#524). Parity target is
 * the CLI's `spring unit boundary set -f boundary.yaml`: the operator
 * picks a YAML file (or pastes its contents), we parse client-side, show a
 * diff against the live boundary, and PUT on confirm. Malformed YAML
 * surfaces inline with no server round-trip.
 *
 * Both the camelCase shape used by `spring unit boundary set -f` and the
 * snake_case shape shipped by PR #527 (`spring apply -f unit.yaml`
 * manifest `boundary:` block) are accepted — see `parseBoundaryYaml` for
 * the full tolerance table.
 */
export function BoundaryYamlUpload({
  current,
  onApply,
  applying = false,
}: BoundaryYamlUploadProps) {
  const [yamlText, setYamlText] = useState("");
  const [fileName, setFileName] = useState<string | null>(null);
  const [parseError, setParseError] = useState<string | null>(null);
  const [applyError, setApplyError] = useState<string | null>(null);

  // Re-derive the parsed body / diff on every render so the operator gets
  // immediate feedback as they edit the textarea. The cost is trivial —
  // boundary YAMLs are tiny by construction and the three parser stages
  // are all O(n) over a handful of rules.
  const parsed = useMemo<
    | { ok: true; body: UnitBoundaryResponse; diff: BoundaryDiff }
    | { ok: false; error: string }
    | null
  >(() => {
    const trimmed = yamlText.trim();
    if (trimmed === "") return null;
    try {
      const body = parseBoundaryYaml(yamlText);
      const diff = diffBoundaries(current, body);
      return { ok: true, body, diff };
    } catch (err) {
      if (err instanceof BoundaryYamlParseError) {
        return { ok: false, error: err.message };
      }
      return {
        ok: false,
        error: err instanceof Error ? err.message : String(err),
      };
    }
  }, [yamlText, current]);

  // Keep `parseError` as a separate state slot so the inline banner can
  // stay pinned while the user continues editing. The memo drives the
  // live preview; this slot drives the user-facing error channel.
  const liveError = parsed && !parsed.ok ? parsed.error : parseError;

  const handleFile = async (file: File) => {
    setApplyError(null);
    setParseError(null);
    try {
      const text = await file.text();
      setYamlText(text);
      setFileName(file.name);
    } catch (err) {
      setParseError(
        err instanceof Error ? err.message : "Could not read file.",
      );
    }
  };

  const handleApply = async () => {
    if (!parsed || !parsed.ok) return;
    setApplyError(null);
    try {
      await onApply(parsed.body);
      // Clear the form on a clean apply so the operator can tell the
      // upload succeeded and the per-rule cards below now reflect the
      // new state.
      setYamlText("");
      setFileName(null);
    } catch (err) {
      setApplyError(err instanceof Error ? err.message : String(err));
    }
  };

  const canApply = parsed?.ok === true && !applying;

  return (
    <Card data-testid="boundary-yaml-upload">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm">
          <FileUp className="h-4 w-4" /> Paste or upload YAML
          <span className="text-xs text-muted-foreground">
            parity with `spring unit boundary set -f`
          </span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-muted-foreground">
          Upload a boundary YAML file (or paste its contents) to replace the
          current boundary. We parse client-side and show a diff below before
          anything hits the server. Accepts both the CLI camelCase shape and
          the snake_case shape from the `spring apply` manifest.
        </p>

        <label className="block space-y-1 text-xs text-muted-foreground">
          <span>Upload a .yaml file</span>
          <input
            type="file"
            accept=".yaml,.yml,text/yaml"
            aria-label="Boundary YAML file"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) void handleFile(file);
            }}
            className="block text-sm"
          />
        </label>

        <label className="block space-y-1 text-xs text-muted-foreground">
          <span>
            YAML contents
            {fileName && (
              <span className="ml-2 text-[11px] text-muted-foreground">
                ({fileName})
              </span>
            )}
          </span>
          <textarea
            value={yamlText}
            onChange={(e) => {
              setYamlText(e.target.value);
              setParseError(null);
              setApplyError(null);
            }}
            placeholder={
              "opacities:\n  - domainPattern: secret-*\nprojections: []\nsyntheses: []"
            }
            rows={10}
            aria-label="Boundary YAML contents"
            className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
            spellCheck={false}
          />
        </label>

        {liveError && (
          <p
            role="alert"
            data-testid="boundary-yaml-error"
            className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
          >
            {liveError}
          </p>
        )}

        {parsed?.ok && (
          <DiffPreview diff={parsed.diff} />
        )}

        {applyError && (
          <p
            role="alert"
            className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
          >
            {applyError}
          </p>
        )}

        <div className="flex justify-end">
          <Button
            size="sm"
            onClick={handleApply}
            disabled={!canApply}
            data-testid="boundary-yaml-apply"
          >
            <Upload className="mr-1 h-4 w-4" />
            {applying ? "Applying…" : "Apply YAML"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

// ---- Diff preview -------------------------------------------------------

function DiffPreview({ diff }: { diff: BoundaryDiff }) {
  if (diff.isNoOp) {
    return (
      <p
        data-testid="boundary-yaml-diff-noop"
        className="rounded-md border border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground"
      >
        No changes — the uploaded YAML matches the current boundary.
      </p>
    );
  }
  return (
    <div
      data-testid="boundary-yaml-diff"
      className="space-y-2 rounded-md border border-border bg-muted/20 p-3"
    >
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="font-medium">Diff preview:</span>
        <Badge variant="default">+{diff.addedCount} added</Badge>
        <Badge variant="outline">−{diff.removedCount} removed</Badge>
      </div>
      <DiffSection
        title="Opacities"
        entries={diff.opacities}
        format={formatOpacity}
      />
      <DiffSection
        title="Projections"
        entries={diff.projections}
        format={formatProjection}
      />
      <DiffSection
        title="Syntheses"
        entries={diff.syntheses}
        format={formatSynthesis}
      />
    </div>
  );
}

function DiffSection<T>({
  title,
  entries,
  format,
}: {
  title: string;
  entries: RuleDiffEntry<T>[];
  format: (rule: T) => string;
}) {
  if (entries.length === 0) return null;
  return (
    <div className="space-y-1">
      <p className="text-xs font-medium text-muted-foreground">{title}</p>
      <ul className="space-y-1">
        {entries.map((entry, i) => (
          <li
            key={`${entry.status}-${i}`}
            className={
              entry.status === "added"
                ? "font-mono text-[11px] text-emerald-700 dark:text-emerald-400"
                : entry.status === "removed"
                  ? "font-mono text-[11px] text-destructive line-through"
                  : "font-mono text-[11px] text-muted-foreground"
            }
            data-status={entry.status}
          >
            <span className="mr-1">
              {entry.status === "added"
                ? "+"
                : entry.status === "removed"
                  ? "−"
                  : " "}
            </span>
            {format(entry.rule)}
          </li>
        ))}
      </ul>
    </div>
  );
}

// Re-export the per-dimension rule types so the test file can tighten
// its assertions without reaching into the API types module.
export type {
  BoundaryOpacityRuleDto,
  BoundaryProjectionRuleDto,
  BoundarySynthesisRuleDto,
};
