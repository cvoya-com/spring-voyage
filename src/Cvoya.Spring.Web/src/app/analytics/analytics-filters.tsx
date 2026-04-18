"use client";

// Shared filter bar for every Analytics tab (#448 / § 5.7 of
// `docs/design/portal-exploration.md`). Renders three controls:
//
//   1. Window picker (24h / 7d / 30d) — maps to `spring analytics
//      {costs,throughput,waits} --window`.
//   2. Scope kind (All / Unit / Agent) — maps to `--unit` / `--agent`.
//   3. Scope name input — the bare `<name>` after the scheme.
//
// The state lives in the URL (`?window=...&scope=...&name=...`) so deep
// links are honest and shareable; this mirrors the CLI's one-liner
// shape. Reading / writing the URL is behind `useAnalyticsFilters`
// below, so each page can keep its render code free of the router.

import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useMemo } from "react";

import { cn } from "@/lib/utils";
import {
  ANALYTICS_WINDOWS,
  type AnalyticsScope,
  type AnalyticsWindow,
} from "@/lib/api/types";

/**
 * Resolved filter state rendered as the `(from, to)` window + optional
 * source substring. Every Analytics page forwards this into its query
 * hook so the wire call shape matches the CLI verbatim.
 */
export interface AnalyticsFilters {
  window: AnalyticsWindow;
  scope: AnalyticsScope;
  /** Inclusive lower bound, ISO 8601. */
  from: string;
  /** Inclusive upper bound (usually "now"), ISO 8601. */
  to: string;
  /**
   * The `scheme://name` substring the CLI passes under `--unit` /
   * `--agent` (resolved to `unit://name` or `agent://name`), or
   * `undefined` when the scope is `all`.
   */
  sourceFilter?: string;
}

const WINDOW_LABELS: Record<AnalyticsWindow, string> = {
  "24h": "Last 24h",
  "7d": "Last 7d",
  "30d": "Last 30d",
};

/**
 * Default window (30d) matches the server's fallback so users landing
 * on `/analytics/*` without a query string see the same window the
 * `spring analytics` CLI defaults to.
 */
const DEFAULT_WINDOW: AnalyticsWindow = "30d";

function resolveWindow(window: AnalyticsWindow): { from: string; to: string } {
  const now = new Date();
  const to = now.toISOString();
  const from = (() => {
    const d = new Date(now);
    if (window === "24h") d.setUTCHours(d.getUTCHours() - 24);
    else if (window === "7d") d.setUTCDate(d.getUTCDate() - 7);
    else d.setUTCDate(d.getUTCDate() - 30);
    return d.toISOString();
  })();
  return { from, to };
}

function parseWindow(raw: string | null): AnalyticsWindow {
  if (raw && (ANALYTICS_WINDOWS as readonly string[]).includes(raw)) {
    return raw as AnalyticsWindow;
  }
  return DEFAULT_WINDOW;
}

function parseScope(
  kindRaw: string | null,
  nameRaw: string | null,
): AnalyticsScope {
  const name = (nameRaw ?? "").trim();
  if (kindRaw === "unit" && name) return { kind: "unit", name };
  if (kindRaw === "agent" && name) return { kind: "agent", name };
  return { kind: "all" };
}

function scopeToSourceFilter(scope: AnalyticsScope): string | undefined {
  if (scope.kind === "unit") return `unit://${scope.name}`;
  if (scope.kind === "agent") return `agent://${scope.name}`;
  return undefined;
}

/**
 * Hook consumed by every Analytics page. Reads the window + scope out
 * of the URL (with defaults matching the CLI) and exposes setters that
 * push a shallow router update so the cross-tab `<Link>`s in
 * `analytics/layout.tsx` preserve the same filters across tabs.
 */
export function useAnalyticsFilters(): AnalyticsFilters & {
  setWindow: (w: AnalyticsWindow) => void;
  setScope: (s: AnalyticsScope) => void;
} {
  const router = useRouter();
  const params = useSearchParams();

  const windowValue = parseWindow(params.get("window"));
  const scope = parseScope(params.get("scope"), params.get("name"));
  const { from, to } = useMemo(() => resolveWindow(windowValue), [windowValue]);
  const sourceFilter = scopeToSourceFilter(scope);

  const update = useCallback(
    (patch: { window?: AnalyticsWindow; scope?: AnalyticsScope }) => {
      const next = new URLSearchParams(params.toString());
      const nextWindow = patch.window ?? windowValue;
      const nextScope = patch.scope ?? scope;
      if (nextWindow === DEFAULT_WINDOW) {
        next.delete("window");
      } else {
        next.set("window", nextWindow);
      }
      if (nextScope.kind === "all") {
        next.delete("scope");
        next.delete("name");
      } else {
        next.set("scope", nextScope.kind);
        next.set("name", nextScope.name);
      }
      const qs = next.toString();
      router.replace(qs ? `?${qs}` : "?", { scroll: false });
    },
    [params, router, scope, windowValue],
  );

  return {
    window: windowValue,
    scope,
    from,
    to,
    sourceFilter,
    setWindow: (w) => update({ window: w }),
    setScope: (s) => update({ scope: s }),
  };
}

interface AnalyticsFiltersBarProps {
  windowValue: AnalyticsWindow;
  onWindowChange: (w: AnalyticsWindow) => void;
  scope: AnalyticsScope;
  onScopeChange: (s: AnalyticsScope) => void;
  /**
   * Optional trailing content (e.g. a "spring analytics …" CLI hint)
   * that the page wants to render inside the same bar.
   */
  hint?: React.ReactNode;
}

/**
 * Presentational filter bar. Every `/analytics/*` page renders exactly
 * one — the chrome is identical across tabs so switching between them
 * feels like the same surface.
 */
export function AnalyticsFiltersBar({
  windowValue,
  onWindowChange,
  scope,
  onScopeChange,
  hint,
}: AnalyticsFiltersBarProps) {
  return (
    <div
      role="group"
      aria-label="Analytics filters"
      className="flex flex-col gap-3 rounded-lg border border-border bg-card p-3 sm:flex-row sm:items-center sm:justify-between"
    >
      <div className="flex flex-wrap items-center gap-3">
        <div
          role="radiogroup"
          aria-label="Window"
          className="inline-flex items-center gap-1 rounded-md border border-border bg-muted p-0.5"
        >
          {ANALYTICS_WINDOWS.map((w) => {
            const active = w === windowValue;
            return (
              <button
                key={w}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => onWindowChange(w)}
                className={cn(
                  "rounded px-2.5 py-1 text-xs font-medium transition-colors",
                  active
                    ? "bg-background text-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {WINDOW_LABELS[w]}
              </button>
            );
          })}
        </div>

        <div className="flex items-center gap-2">
          <label htmlFor="analytics-scope-kind" className="text-xs text-muted-foreground">
            Scope
          </label>
          <select
            id="analytics-scope-kind"
            aria-label="Scope kind"
            value={scope.kind}
            onChange={(e) => {
              const kind = e.target.value as AnalyticsScope["kind"];
              if (kind === "all") onScopeChange({ kind: "all" });
              else onScopeChange({ kind, name: scope.kind === "all" ? "" : scope.name });
            }}
            className="h-8 rounded-md border border-input bg-background px-2 text-xs"
          >
            <option value="all">All sources</option>
            <option value="unit">Unit</option>
            <option value="agent">Agent</option>
          </select>

          {scope.kind !== "all" && (
            <input
              aria-label={`${scope.kind} name`}
              value={scope.name}
              onChange={(e) =>
                onScopeChange({ kind: scope.kind, name: e.target.value })
              }
              placeholder={scope.kind === "unit" ? "eng-team" : "ada"}
              className="h-8 w-40 rounded-md border border-input bg-background px-2 font-mono text-xs"
            />
          )}
        </div>
      </div>

      {hint && (
        <div className="text-xs text-muted-foreground sm:max-w-md sm:text-right">
          {hint}
        </div>
      )}
    </div>
  );
}

/**
 * The breadcrumb trail every Analytics page shares. Kept here so the
 * three pages cannot drift on labels / target URLs.
 */
export const ANALYTICS_BREADCRUMBS = {
  costs: [
    { label: "Dashboard", href: "/" },
    { label: "Analytics", href: "/analytics" },
    { label: "Costs" },
  ],
  throughput: [
    { label: "Dashboard", href: "/" },
    { label: "Analytics", href: "/analytics" },
    { label: "Throughput" },
  ],
  waits: [
    { label: "Dashboard", href: "/" },
    { label: "Analytics", href: "/analytics" },
    { label: "Wait times" },
  ],
} as const;
