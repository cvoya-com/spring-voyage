"use client";

// Filter strip for /activity/interactions (#2867). Wraps the scope-unit
// picker (a left-rail `<UnitTree>`) plus the cross-canvas knobs:
//   - Depth dial: 0 / 1 / 2 hops around the scope unit (default 2).
//   - Since / Until: ISO instants, written to URL.
//   - Bucket: hour | day for the timeline aggregation.
//   - View mode: graph | matrix | both.
//   - Live mode: toggles the SSE stream.

import Link from "next/link";
import { History, Loader2, Radio, Square } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { UnitTree } from "@/components/units/unit-tree";
import { useTenantTree } from "@/lib/api/queries";
import { cn } from "@/lib/utils";

import {
  BUCKET_PRESETS,
  resolveWindow,
  toggleLive,
  toggleRewind,
  type InteractionsBucket,
  type InteractionsUrlState,
  type InteractionsViewMode,
} from "./url-state";

const BUCKET_LABELS: Record<InteractionsBucket, string> = {
  "15s": "15s",
  "30s": "30s",
  "1m": "1 min",
  "5m": "5 min",
  "10m": "10 min",
  "15m": "15 min",
  "30m": "30 min",
  "1h": "1 hour",
  "1d": "1 day",
};

interface InteractionFiltersProps {
  state: InteractionsUrlState;
  onChange: (next: InteractionsUrlState) => void;
}

/**
 * Scope-unit picker. Borrows the existing `<UnitTree>` so the left-rail
 * selection matches what `/explorer` paints; clicking a unit writes its
 * id into the `unit` URL slot.
 *
 * `<UnitTree>` doesn't know what to do with agent / human leaves for
 * this surface — we accept them too so the operator can scope to one
 * specific actor pair, but the URL only carries `unit` so when an agent
 * is picked we re-resolve to the parent unit. For v0.1 we accept any
 * selection — the backend's `unit` filter ignores the scheme.
 */
function ScopePicker({
  selectedUnit,
  onSelect,
}: {
  selectedUnit: string;
  onSelect: (id: string) => void;
}) {
  const treeQuery = useTenantTree();

  if (treeQuery.isPending) {
    return (
      <div
        className="flex items-center justify-center p-4 text-sm text-muted-foreground"
        role="status"
        aria-live="polite"
      >
        <Loader2 className="mr-2 h-3.5 w-3.5 animate-spin" aria-hidden="true" />
        Loading tenant…
      </div>
    );
  }
  if (treeQuery.error) {
    return (
      <div className="p-2">
        <ApiErrorMessage error={treeQuery.error} />
      </div>
    );
  }
  if (!treeQuery.data) {
    return (
      <p className="p-2 text-xs text-muted-foreground">
        No tenant tree available.
      </p>
    );
  }
  return (
    <UnitTree
      tree={treeQuery.data}
      selectedId={selectedUnit}
      onSelect={onSelect}
      hideCountBadge
    />
  );
}

export function InteractionFilters({
  state,
  onChange,
}: InteractionFiltersProps) {
  const setView = (view: InteractionsViewMode) =>
    onChange({ ...state, view });
  const setLive = (live: boolean) => onChange(toggleLive(state, live));
  const setRewind = (rewind: boolean) => onChange(toggleRewind(state, rewind));
  const setNeighbours = (n: 0 | 1 | 2) =>
    onChange({ ...state, neighbours: n });
  const setBucket = (bucket: InteractionsBucket) =>
    onChange({ ...state, bucket });

  return (
    <Card data-testid="interaction-filters">
      <CardContent className="space-y-3 p-3">
        {/* View / live toggle row */}
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <div
            className="inline-flex rounded-full border border-border bg-muted/60 p-0.5"
            role="tablist"
            aria-label="View mode"
          >
            {(["graph", "matrix", "both"] as const).map((view) => (
              <button
                key={view}
                type="button"
                role="tab"
                aria-selected={state.view === view}
                data-testid={`interaction-filters-view-${view}`}
                onClick={() => setView(view)}
                className={cn(
                  "rounded-full px-2.5 py-0.5 text-xs font-medium transition-colors",
                  state.view === view
                    ? "bg-primary/15 text-primary"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {view}
              </button>
            ))}
          </div>

          <Button
            size="sm"
            variant={state.live ? "default" : "outline"}
            onClick={() => setLive(!state.live)}
            data-testid="interaction-filters-live-toggle"
            aria-pressed={state.live}
            className="h-7"
          >
            {state.live ? (
              <>
                <Radio
                  className="mr-1 h-3 w-3 animate-pulse"
                  aria-hidden="true"
                />
                Live
              </>
            ) : (
              <>
                <Square className="mr-1 h-3 w-3" aria-hidden="true" />
                Snapshot
              </>
            )}
          </Button>

          <Button
            size="sm"
            variant={state.rewind ? "default" : "outline"}
            onClick={() => setRewind(!state.rewind)}
            data-testid="interaction-filters-rewind-toggle"
            aria-pressed={state.rewind}
            className="h-7"
          >
            <History className="mr-1 h-3 w-3" aria-hidden="true" />
            Rewind
          </Button>
        </div>

        {/* Depth + bucket row */}
        <div className="flex flex-wrap items-center gap-3 text-xs">
          <label className="inline-flex items-center gap-1.5">
            <span className="text-muted-foreground">Depth</span>
            <div
              className="inline-flex rounded-md border border-border bg-muted/40 p-0.5"
              role="radiogroup"
              aria-label="Hop depth"
            >
              {[0, 1, 2].map((n) => (
                <button
                  key={n}
                  type="button"
                  role="radio"
                  aria-checked={state.neighbours === n}
                  data-testid={`interaction-filters-neighbours-${n}`}
                  onClick={() => setNeighbours(n as 0 | 1 | 2)}
                  className={cn(
                    "rounded-sm px-2 py-0.5 font-mono text-[11px]",
                    state.neighbours === n
                      ? "bg-primary/15 text-primary"
                      : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  {n}
                </button>
              ))}
            </div>
          </label>

          <label className="inline-flex items-center gap-1.5">
            <span className="text-muted-foreground">Bucket</span>
            <select
              value={state.bucket}
              onChange={(e) =>
                setBucket(e.target.value as InteractionsBucket)
              }
              data-testid="interaction-filters-bucket"
              aria-label="Timeline bucket"
              className="h-7 rounded-md border border-input bg-background px-2 text-xs"
            >
              {BUCKET_PRESETS.map((b) => (
                <option key={b} value={b}>
                  {BUCKET_LABELS[b]}
                </option>
              ))}
            </select>
          </label>
        </div>

        {/* Since / Until stack. When the URL omits a bound we display the
            resolved default in the input so operators see the window the
            page is actually querying instead of an empty field. State
            stays empty until the operator types over it, which keeps the
            URL short and lets `until` track "now" on subsequent loads.
            The inputs are stacked vertically so the full
            `MM/DD/YYYY, HH:MM` value fits in the sidebar without
            truncation; the shared `<DateTimeField>` keeps the markup
            and styling in one place. */}
        {(() => {
          const resolved = resolveWindow(state);
          const isWindowCustomised = Boolean(state.since || state.until);
          return (
            <div className="space-y-2">
              <div className="space-y-2">
                <DateTimeField
                  label="Since"
                  value={state.since || resolved.since}
                  onCommit={(iso) => onChange({ ...state, since: iso })}
                  disabled={state.live}
                  testId="interaction-filters-since"
                  ariaLabel="Filter window start"
                />
                <DateTimeField
                  label="Until"
                  value={state.until || resolved.until}
                  onCommit={(iso) => onChange({ ...state, until: iso })}
                  disabled={state.live}
                  testId="interaction-filters-until"
                  ariaLabel="Filter window end"
                />
              </div>
              {/* Reset link clears since/until so resolveWindow falls back
                  to the default (last 10 min). Only shown when the
                  operator has actually narrowed the window — clicking
                  it returns the canvas to a clean state from an over-
                  drilled-in slice where the brush has no room to work. */}
              {isWindowCustomised && !state.live ? (
                <button
                  type="button"
                  onClick={() => onChange({ ...state, since: "", until: "" })}
                  className="text-[10px] text-primary hover:underline"
                  data-testid="interaction-filters-window-reset"
                >
                  Reset window
                </button>
              ) : null}
            </div>
          );
        })()}

        {/* Participant row */}
        <label className="space-y-1 text-xs">
          <span className="text-muted-foreground">Participant address</span>
          <Input
            type="text"
            value={state.participant}
            onChange={(e) =>
              onChange({ ...state, participant: e.target.value })
            }
            placeholder="scheme:hex or scheme://hex"
            className="h-7 font-mono text-[10px]"
            data-testid="interaction-filters-participant"
            aria-label="Filter by participant address"
          />
        </label>

        {/* Active scope chip. The full GUID is too wide for the 18-rem
            sidebar so we render a truncated form inside the badge and
            keep it as `title` for hover, then put `Open unit` on its own
            row to guarantee nothing escapes the card edge. */}
        {state.unit ? (
          <div className="space-y-1 text-xs">
            <div className="flex min-w-0 items-center gap-1.5">
              <span className="shrink-0 text-muted-foreground">Scope</span>
              <Badge
                variant="outline"
                className="min-w-0 max-w-full truncate font-mono text-[10px]"
                title={state.unit}
                data-testid="interaction-filters-scope-chip"
              >
                {truncateMiddle(state.unit, 14)}
              </Badge>
              <button
                type="button"
                onClick={() => onChange({ ...state, unit: "" })}
                className="shrink-0 text-muted-foreground hover:text-foreground"
                data-testid="interaction-filters-scope-clear"
              >
                clear
              </button>
            </div>
            <Link
              href={`/explorer/units/${encodeURIComponent(state.unit)}`}
              className="inline-block text-primary hover:underline"
            >
              Open unit
            </Link>
          </div>
        ) : null}

        {/* Scope picker — the left-rail tree */}
        <details className="rounded-md border border-border" open>
          <summary className="cursor-pointer px-2 py-1 text-xs font-medium text-muted-foreground">
            Scope picker
          </summary>
          <div
            className="max-h-72 overflow-y-auto border-t border-border p-1"
            data-testid="interaction-filters-scope-picker"
          >
            <ScopePicker
              selectedUnit={state.unit}
              onSelect={(id) => onChange({ ...state, unit: id })}
            />
          </div>
        </details>
      </CardContent>
    </Card>
  );
}

interface DateTimeFieldProps {
  label: string;
  /** ISO 8601 string for display + commit. May be empty for "use default". */
  value: string;
  /** Fires when the operator picks a new value. Receives an ISO string. */
  onCommit: (iso: string) => void;
  disabled?: boolean;
  testId: string;
  ariaLabel: string;
}

/**
 * Shared `datetime-local` field. Both the Since and Until inputs render
 * through this so the markup, sizing, and ISO-↔-local conversion live in
 * one place. The input is sized to fit `MM/DD/YYYY, HH:MM` plus the
 * native picker chrome without truncation.
 */
function DateTimeField({
  label,
  value,
  onCommit,
  disabled,
  testId,
  ariaLabel,
}: DateTimeFieldProps) {
  return (
    <label className="block space-y-1 text-xs">
      <span className="text-muted-foreground">{label}</span>
      <Input
        type="datetime-local"
        value={isoToLocal(value)}
        onChange={(e) => onCommit(localToIso(e.target.value))}
        className="h-8 w-full text-xs"
        data-testid={testId}
        aria-label={ariaLabel}
        disabled={disabled}
      />
    </label>
  );
}

/**
 * Convert an ISO 8601 instant to the format expected by
 * `<input type="datetime-local">` (YYYY-MM-DDTHH:mm). Returns empty
 * string for an empty / unparseable value so the input renders cleared.
 */
function isoToLocal(iso: string): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function localToIso(local: string): string {
  if (!local) return "";
  const d = new Date(local);
  return Number.isNaN(d.getTime()) ? "" : d.toISOString();
}

function truncateMiddle(value: string, maxLength: number): string {
  if (value.length <= maxLength) return value;
  const keep = Math.max(2, Math.floor((maxLength - 1) / 2));
  return `${value.slice(0, keep)}…${value.slice(-keep)}`;
}
