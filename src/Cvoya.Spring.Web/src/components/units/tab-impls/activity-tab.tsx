"use client";

import { useMemo, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import {
  Activity,
  ChevronDown,
  ChevronRight,
  DollarSign,
  Info,
  RefreshCw,
  TrendingDown,
  X,
} from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useActivityQuery,
  useAgent,
  useAgentCostBreakdown,
  useAgentCostTimeseries,
  useAgentExecution,
  useUnitExecution,
} from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type {
  ActivityQueryResult,
  ActivitySeverity,
} from "@/lib/api/types";
import { cn, formatCost, humanEventType, timeAgo } from "@/lib/utils";

/**
 * Subjects the unified activity tab can be driven by. The activity feed
 * is keyed by an `<scheme>:<id>` source filter — `unit:<id>` for units,
 * `agent:<id>` for agents — and the SSE stream filter mirrors the same
 * pair. Adding a new kind here is a matter of teaching the source-string
 * builder + filter predicate below; nothing else in the component is
 * subject-specific.
 */
export type ActivitySubjectKind = "Unit" | "Agent";

export interface ActivityTabProps {
  /** Subject kind — drives the activity-source filter and the stream predicate. */
  kind: ActivitySubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
}

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

// Maps the subject kind to the activity-source URI scheme used by the
// REST query + SSE stream. Kept inline (rather than promoted to a util)
// because it is the *only* place the kind ⇄ scheme correspondence is
// load-bearing — moving it would split the contract across files.
const SOURCE_SCHEME: Record<ActivitySubjectKind, "unit" | "agent"> = {
  Unit: "unit",
  Agent: "agent",
};

// Cost-card window options for the agent sparkline toggle. Only used
// when `kind === "Agent"` — the unit Overview tab already carries its
// own cost-over-time card (DESIGN.md § 9.1), so we deliberately do not
// render a second one inside the unit Activity tab.
const AGENT_WINDOW_OPTIONS = [
  { label: "7d", window: "7d", bucket: "1d" },
  { label: "24h", window: "24h", bucket: "1h" },
] as const;

type AgentWindowOption = (typeof AGENT_WINDOW_OPTIONS)[number];

// #2502: filter-chip event-kind whitelist. The list pulls the runtime
// event kinds plus the most common platform kinds operators tag a
// session by; the dropdown still surfaces every kind present in the
// current window so users discover new ones organically.
const KNOWN_EVENT_KINDS = [
  "RuntimeSpan",
  "RuntimeLog",
  "RuntimeProgress",
  "LlmTurn",
  "ToolCall",
  "ToolResult",
  "MessageArrived",
  "MessageSent",
  "DecisionMade",
  "ErrorOccurred",
  "WorkflowStepCompleted",
  "CostIncurred",
] as const;

// #2564: the three runtime event kinds that arrive *only* via the OTLP
// ingest path (`/otlp/v1/*`, #2492). They are emitted exclusively by
// runtimes whose launcher calls `LauncherOtelEnvironment.Add()`.
const OTLP_ONLY_EVENT_KINDS = ["RuntimeLog", "LlmTurn", "RuntimeSpan"] as const;

// #2564: OTLP-emitting runtime allowlist. In v0.1 only the
// `spring-voyage` launcher (`SpringVoyageAgentLauncher`) injects the
// `OTEL_EXPORTER_OTLP_*` env vars; `claude-code` / `codex` / `gemini`
// ship no OTLP telemetry, so `OTLP_ONLY_EVENT_KINDS` stay permanently
// empty for those subjects. A runtime not on this list — including an
// unrecognised or absent one — gets the empty-chip hint.
const OTLP_EMITTING_RUNTIMES = new Set<string>(["spring-voyage"]);

// #2502: quick-preset durations for the time-range chip.
const TIME_RANGE_PRESETS = [
  { label: "Last 5m", minutes: 5 },
  { label: "Last 15m", minutes: 15 },
  { label: "Last 1h", minutes: 60 },
  { label: "Last 24h", minutes: 24 * 60 },
] as const;

const URL_PARAM_KINDS = "kinds";
const URL_PARAM_THREAD = "thread";
const URL_PARAM_MESSAGE = "message";
const URL_PARAM_FROM = "from";
const URL_PARAM_TO = "to";

/**
 * True when an activity row carries an expandable structured payload —
 * either the raw `details` JSON returned by the REST query (#1665) or
 * any other non-empty object value the SSE stream might attach later.
 */
function hasDetails(item: ActivityQueryResult["items"][number]): boolean {
  const details = (item as { details?: unknown }).details;
  if (details == null) return false;
  if (typeof details !== "object") return true;
  return Object.keys(details as Record<string, unknown>).length > 0;
}

/**
 * Minimal inline sparkline (SVG polyline). Matches the BudgetSparkline
 * and StatSparkline aesthetic — no new charting library.
 */
function CostSparkline({
  points,
  testId,
}: {
  points: number[];
  testId: string;
}) {
  const max = Math.max(1, ...points);
  const width = 120;
  const height = 24;
  const step = points.length > 1 ? width / (points.length - 1) : 0;
  const svgPoints = points
    .map(
      (v, i) =>
        `${(i * step).toFixed(1)},${(height - (v / max) * height).toFixed(1)}`,
    )
    .join(" ");
  return (
    <svg
      aria-hidden="true"
      role="img"
      data-testid={testId}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="text-primary/70"
    >
      <polyline
        points={svgPoints}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/**
 * Agent-only cost cards: sparkline (#1363) + per-model breakdown
 * (#1364). Pulled out as its own component so the unit path doesn't
 * mount the `useAgentCost*` hooks at all — they would 404 against a
 * unit id. A unit-side breakdown endpoint doesn't exist today; if one
 * lands we can parameterise this on `kind` and drop the conditional.
 */
function AgentCostCards({ agentId }: { agentId: string }) {
  const [windowOpt, setWindowOpt] = useState<AgentWindowOption>(
    AGENT_WINDOW_OPTIONS[0],
  );

  const timeseriesQuery = useAgentCostTimeseries(
    agentId,
    windowOpt.window,
    windowOpt.bucket,
  );
  const breakdownQuery = useAgentCostBreakdown(agentId);

  const sparklinePoints =
    timeseriesQuery.data?.points?.map((p) => p.costUsd) ?? [];
  const breakdownEntries = breakdownQuery.data?.entries ?? [];

  return (
    <>
      {/* Cost over time sparkline — #1363 */}
      <Card data-testid="agent-cost-timeseries-card">
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm">
            <TrendingDown className="h-4 w-4" aria-hidden="true" /> Cost over time
          </CardTitle>
          <div className="flex gap-1" role="group" aria-label="Time window">
            {AGENT_WINDOW_OPTIONS.map((opt) => (
              <button
                key={opt.label}
                onClick={() => setWindowOpt(opt)}
                className={`rounded-md px-2 py-0.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
                  windowOpt.label === opt.label
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-pressed={windowOpt.label === opt.label}
                data-testid={`agent-timeseries-window-${opt.label}`}
              >
                {opt.label}
              </button>
            ))}
          </div>
        </CardHeader>
        <CardContent>
          {timeseriesQuery.isLoading ? (
            <Skeleton
              className="h-8 w-full"
              data-testid="agent-cost-timeseries-loading"
            />
          ) : sparklinePoints.length === 0 ||
            sparklinePoints.every((v) => v === 0) ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="agent-cost-timeseries-empty"
            >
              No cost data for this window.
            </p>
          ) : (
            <div className="flex items-end gap-4">
              <CostSparkline
                points={sparklinePoints}
                testId="agent-cost-sparkline"
              />
              <span className="text-xs text-muted-foreground tabular-nums">
                {formatCost(sparklinePoints.reduce((sum, v) => sum + v, 0))}{" "}
                total
              </span>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Model cost breakdown — #1364 (agent-only; no unit endpoint today) */}
      {breakdownEntries.length > 0 ? (
        <Card data-testid="agent-cost-breakdown-card">
          <CardHeader className="pb-2">
            <CardTitle className="flex items-center gap-2 text-sm">
              <DollarSign className="h-4 w-4" aria-hidden="true" /> Model cost breakdown
            </CardTitle>
          </CardHeader>
          <CardContent>
            <table className="w-full text-xs">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="pb-1 text-left font-medium">Model</th>
                  <th className="pb-1 text-left font-medium">Kind</th>
                  <th className="pb-1 text-right font-medium">Cost</th>
                  <th className="pb-1 text-right font-medium">Requests</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {breakdownEntries.map((entry) => (
                  <tr key={entry.key}>
                    <td className="py-1 pr-3 font-mono">{entry.key}</td>
                    <td className="py-1 pr-3 capitalize text-muted-foreground">
                      {entry.kind}
                    </td>
                    <td className="py-1 text-right tabular-nums">
                      {formatCost(entry.totalCost)}
                    </td>
                    <td className="py-1 text-right tabular-nums text-muted-foreground">
                      {entry.recordCount.toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>
      ) : null}
    </>
  );
}

/**
 * #2502: filter-chip primitive shared across the Activity-tab filter
 * row. Mirrors the tenant-wide `/activity` page's chip styling so
 * operators see the same active/inactive treatment on both surfaces.
 * Kept local: this is the only file under the unit/agent tab tree
 * that needs the chip primitive, and the tenant-wide page already owns
 * its own copy with different content — keeping them in sync via shared
 * design tokens (Tailwind classes), not a shared component.
 */
function FilterChip({
  label,
  active,
  children,
}: {
  label: string;
  active: boolean;
  children: React.ReactNode;
}) {
  return (
    <label
      className={cn(
        "inline-flex min-w-0 items-center gap-2 rounded-full border px-3 py-1 text-xs transition-colors",
        active
          ? "border-primary/40 bg-primary/10 text-foreground"
          : "border-border bg-muted/40 text-muted-foreground hover:text-foreground",
      )}
    >
      <span className="shrink-0 font-medium uppercase tracking-wide text-[10px] text-muted-foreground">
        {label}
      </span>
      {children}
    </label>
  );
}

/**
 * Activity-tab filters state. Each field maps to a URL search-param so
 * a refresh / share / deep-link preserves the filter set. Filter
 * composition is AND: a row is rendered iff every active filter passes.
 */
interface ActivityFilters {
  kinds: Set<string>;
  thread: string;
  message: string;
  from: string; // ISO-8601 lower bound (inclusive); empty = unbounded.
  to: string; // ISO-8601 upper bound (inclusive); empty = unbounded.
}

function readFiltersFromUrl(params: URLSearchParams): ActivityFilters {
  const kinds = new Set<string>();
  for (const v of params.getAll(URL_PARAM_KINDS)) {
    if (v) {
      kinds.add(v);
    }
  }
  return {
    kinds,
    thread: params.get(URL_PARAM_THREAD) ?? "",
    message: params.get(URL_PARAM_MESSAGE) ?? "",
    from: params.get(URL_PARAM_FROM) ?? "",
    to: params.get(URL_PARAM_TO) ?? "",
  };
}

function writeFiltersToUrl(
  current: URLSearchParams,
  filters: ActivityFilters,
): string {
  const next = new URLSearchParams(current);
  next.delete(URL_PARAM_KINDS);
  for (const k of filters.kinds) {
    next.append(URL_PARAM_KINDS, k);
  }
  if (filters.thread) next.set(URL_PARAM_THREAD, filters.thread);
  else next.delete(URL_PARAM_THREAD);
  if (filters.message) next.set(URL_PARAM_MESSAGE, filters.message);
  else next.delete(URL_PARAM_MESSAGE);
  if (filters.from) next.set(URL_PARAM_FROM, filters.from);
  else next.delete(URL_PARAM_FROM);
  if (filters.to) next.set(URL_PARAM_TO, filters.to);
  else next.delete(URL_PARAM_TO);
  return next.toString();
}

/**
 * #2564: resolve the *effective* runtime of the Activity-tab subject so
 * the tab can warn when the OTLP-only event kinds will stay empty.
 *
 * - For a unit the runtime is read straight from `GET /units/{id}/execution`.
 * - For an agent the agent's own declared runtime wins; when it is left
 *   blank (inherited) the owning unit's declared runtime is used —
 *   mirroring the dispatch-time merge the Execution panel renders.
 *
 * Returns `undefined` while the queries are still resolving so the
 * caller can stay silent rather than flash a hint that might not apply.
 */
function useSubjectRuntime(
  kind: ActivitySubjectKind,
  id: string,
): string | null | undefined {
  const unitExecution = useUnitExecution(id, { enabled: kind === "Unit" });
  const agentExecution = useAgentExecution(id, { enabled: kind === "Agent" });
  const agent = useAgent(id, { enabled: kind === "Agent" });

  const parentUnitId = agent.data?.agent.parentUnitId ?? null;
  const parentUnitExecution = useUnitExecution(parentUnitId ?? "", {
    enabled: kind === "Agent" && Boolean(parentUnitId),
  });

  if (kind === "Unit") {
    if (unitExecution.isLoading) return undefined;
    return unitExecution.data?.runtime ?? null;
  }

  // Agent path: own runtime first, then the owning unit's default.
  if (agentExecution.isLoading || agent.isLoading) return undefined;
  const ownRuntime = agentExecution.data?.runtime ?? null;
  if (ownRuntime) return ownRuntime;
  if (parentUnitId && parentUnitExecution.isLoading) return undefined;
  return parentUnitExecution.data?.runtime ?? null;
}

/**
 * #2564: inline advisory rendered above the filter row when the
 * subject's runtime emits no OTLP telemetry. Without it, an operator
 * adds the `RuntimeLog` / `LlmTurn` / `RuntimeSpan` chips, sees
 * "No events match the current filters", and wrongly concludes the
 * subject is never invoked. Uses the Info/context banner palette from
 * DESIGN.md § 12.4 — no new styling primitive.
 */
function NoOtlpHint() {
  return (
    <div
      role="status"
      data-testid="activity-no-otlp-hint"
      className="flex items-start gap-2 rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm text-foreground"
    >
      <Info className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden />
      <span>
        This runtime does not emit OTLP telemetry —{" "}
        {OTLP_ONLY_EVENT_KINDS.join(" / ")} will stay empty. Filter by{" "}
        MessageArrived and RuntimeProgress instead.
      </span>
    </div>
  );
}

/**
 * Subject-agnostic activity tab. Renders the canonical event feed
 * (REST baseline + SSE stream-driven invalidation) with expandable
 * structured-payload rows for both units and agents. When `kind`
 * is `"Agent"`, the agent-only cost sparkline and per-model breakdown
 * cards render above the feed.
 *
 * #2502: composable filter chips above the feed — event-kind
 * multiselect, thread dropdown, message dropdown, time-range picker.
 * Filter state lives in URL search params so navigation preserves
 * the view; chips reuse the tokens established by the tenant-wide
 * `/activity` page (rounded full pill, primary/10 tint on active).
 */
export function ActivityTab({ kind, id }: ActivityTabProps) {
  const scheme = SOURCE_SCHEME[kind];
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const filters = useMemo(
    () => readFiltersFromUrl(new URLSearchParams(searchParams?.toString() ?? "")),
    [searchParams],
  );

  const setFilters = (next: ActivityFilters) => {
    const current = new URLSearchParams(searchParams?.toString() ?? "");
    const search = writeFiltersToUrl(current, next);
    router.replace(`${pathname}${search ? `?${search}` : ""}`, { scroll: false });
  };

  // REST baseline — paginated query for this subject's events. The
  // stream layered on top keeps it fresh (#437). Time-range params are
  // forwarded to the API so filters compose with server-side pagination.
  const queryParams: Record<string, string> = useMemo(() => {
    const p: Record<string, string> = {
      source: `${scheme}:${id}`,
      pageSize: "20",
    };
    if (filters.from) p.from = filters.from;
    if (filters.to) p.to = filters.to;
    return p;
  }, [scheme, id, filters.from, filters.to]);

  const {
    data: result,
    error,
    isLoading,
    isFetching,
    refetch,
  } = useActivityQuery(queryParams);

  // Subscribe to the subject-scoped live stream so the tab updates as
  // events arrive — no more manual refresh loop. The hook invalidates
  // the matching cache slice on every event, so the `useActivityQuery`
  // above re-fetches and the list stays in order.
  useActivityStream({
    filter: (event) =>
      event.source.scheme === scheme && event.source.path === id,
  });

  const activityError = error ?? null;

  // Expanded-row tracker: clicking a row toggles its `id` in the set so
  // the structured `details` payload is shown inline. Kept in component
  // state (no URL persistence) — expansion is ephemeral context, not a
  // navigation surface.
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const toggleExpanded = (rowId: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(rowId)) next.delete(rowId);
      else next.add(rowId);
      return next;
    });

  // Visible items after client-side composition of the kind / thread /
  // message filters. The server already applied the time-range; the
  // remaining filters narrow the in-memory set without an extra round
  // trip. The thread / message dropdowns are sourced from `result.items`
  // so they always offer real navigation targets.
  const items = useMemo(() => result?.items ?? [], [result]);

  const filteredItems = useMemo(() => {
    return items.filter((e) => {
      if (filters.kinds.size > 0 && !filters.kinds.has(e.eventType)) {
        return false;
      }
      if (filters.thread && e.correlationId !== filters.thread) {
        return false;
      }
      if (
        filters.message
        && (e as { messageId?: string }).messageId !== filters.message
      ) {
        return false;
      }
      return true;
    });
  }, [items, filters.kinds, filters.thread, filters.message]);

  // Distinct values for the dropdowns. Threads / messages come from the
  // currently-loaded events; that keeps the chips honest (they only
  // expose values an operator can actually drill into without a fresh
  // server query).
  const distinctThreads = useMemo(() => {
    const set = new Set<string>();
    for (const e of items) {
      if (e.correlationId) set.add(e.correlationId);
    }
    return Array.from(set).sort();
  }, [items]);

  const distinctMessages = useMemo(() => {
    const set = new Set<string>();
    for (const e of items) {
      const messageId = (e as { messageId?: string }).messageId;
      if (!messageId) continue;
      if (filters.thread && e.correlationId !== filters.thread) continue;
      set.add(messageId);
    }
    return Array.from(set).sort();
  }, [items, filters.thread]);

  // Event kinds: union of the well-known list and any kinds the current
  // window surfaces. Operators can't accidentally hide events whose
  // kind we don't yet have a label for — the dropdown still shows the
  // raw value.
  const eventKindChoices = useMemo(() => {
    const set = new Set<string>(KNOWN_EVENT_KINDS);
    for (const e of items) set.add(e.eventType);
    return Array.from(set).sort();
  }, [items]);

  const anyFilter =
    filters.kinds.size > 0
    || filters.thread !== ""
    || filters.message !== ""
    || filters.from !== ""
    || filters.to !== "";

  const toggleKind = (kindValue: string) => {
    const next = new Set(filters.kinds);
    if (next.has(kindValue)) next.delete(kindValue);
    else next.add(kindValue);
    setFilters({ ...filters, kinds: next });
  };

  const applyTimePreset = (minutes: number) => {
    const to = new Date();
    const from = new Date(to.getTime() - minutes * 60 * 1000);
    setFilters({
      ...filters,
      from: from.toISOString(),
      to: to.toISOString(),
    });
  };

  const clearFilters = () =>
    setFilters({
      kinds: new Set(),
      thread: "",
      message: "",
      from: "",
      to: "",
    });

  // #2564: warn when the subject's runtime emits no OTLP telemetry so
  // the operator does not add the OTLP-only chips and misread the
  // resulting empty feed. Only fires for a *concrete* runtime not on the
  // allowlist — `undefined` (still resolving) and `null` (runtime not
  // declared; the effective value is decided at dispatch) stay silent
  // rather than flash a hint that might not apply.
  const subjectRuntime = useSubjectRuntime(kind, id);
  const showNoOtlpHint =
    typeof subjectRuntime === "string"
    && !OTLP_EMITTING_RUNTIMES.has(subjectRuntime);

  const emptyMessage =
    kind === "Agent"
      ? "No activity for this agent yet."
      : "No activity events for this unit.";

  const emptyFilteredMessage =
    "No events match the current filters.";

  return (
    <div className="space-y-4" data-testid={`tab-${scheme}-activity`}>
      {kind === "Agent" ? <AgentCostCards agentId={id} /> : null}

      {/* #2564: runtime-aware advisory — the subject's runtime emits no
          OTLP telemetry, so the OTLP-only filter chips would stay
          empty. Rendered above the filter row so the operator reads it
          before reaching for those chips. */}
      {showNoOtlpHint ? <NoOtlpHint /> : null}

      {/* #2502: filter chip row. Visual density matches the tenant-wide
          /activity page — same rounded pill, same primary/10 tint on
          active chips. */}
      <Card>
        <CardContent className="pt-4">
          <div
            className="flex flex-wrap items-center gap-2"
            data-testid={`tab-${scheme}-activity-filters`}
          >
            <FilterChip label="Kinds" active={filters.kinds.size > 0}>
              <select
                aria-label="Event Kind"
                value=""
                onChange={(e) => {
                  if (e.target.value) {
                    toggleKind(e.target.value);
                    e.target.value = "";
                  }
                }}
                data-testid="activity-filter-kind-select"
                className="h-7 w-40 rounded-full border-0 bg-transparent text-xs focus-visible:outline-none"
              >
                <option value="">Add kind…</option>
                {eventKindChoices.map((k) => (
                  <option key={k} value={k} disabled={filters.kinds.has(k)}>
                    {humanEventType(k)}
                  </option>
                ))}
              </select>
            </FilterChip>
            {Array.from(filters.kinds).map((k) => (
              <Badge
                key={k}
                variant="secondary"
                className="cursor-pointer text-[11px]"
                onClick={() => toggleKind(k)}
                data-testid={`activity-filter-kind-chip-${k}`}
              >
                {humanEventType(k)}
                <X className="ml-1 inline h-3 w-3" aria-hidden />
              </Badge>
            ))}
            <FilterChip label="Thread" active={filters.thread !== ""}>
              <select
                aria-label="Thread"
                value={filters.thread}
                onChange={(e) =>
                  setFilters({ ...filters, thread: e.target.value, message: "" })
                }
                data-testid="activity-filter-thread-select"
                className="h-7 w-40 rounded-full border-0 bg-transparent text-xs font-mono focus-visible:outline-none"
              >
                <option value="">All threads</option>
                {distinctThreads.map((t) => (
                  <option key={t} value={t}>
                    {t.slice(0, 12)}
                  </option>
                ))}
              </select>
            </FilterChip>
            <FilterChip label="Message" active={filters.message !== ""}>
              <select
                aria-label="Message"
                value={filters.message}
                onChange={(e) =>
                  setFilters({ ...filters, message: e.target.value })
                }
                data-testid="activity-filter-message-select"
                className="h-7 w-40 rounded-full border-0 bg-transparent text-xs font-mono focus-visible:outline-none"
              >
                <option value="">All messages</option>
                {distinctMessages.map((m) => (
                  <option key={m} value={m}>
                    {m.slice(0, 12)}
                  </option>
                ))}
              </select>
            </FilterChip>
            <FilterChip
              label="Time"
              active={filters.from !== "" || filters.to !== ""}
            >
              <div className="flex items-center gap-1">
                {TIME_RANGE_PRESETS.map((preset) => (
                  <button
                    key={preset.label}
                    type="button"
                    onClick={() => applyTimePreset(preset.minutes)}
                    className="rounded-full bg-transparent px-1.5 py-0.5 text-[11px] hover:bg-accent/50"
                    data-testid={`activity-filter-time-preset-${preset.minutes}m`}
                  >
                    {preset.label}
                  </button>
                ))}
                <input
                  type="datetime-local"
                  aria-label="From"
                  value={filters.from ? filters.from.slice(0, 16) : ""}
                  onChange={(e) =>
                    setFilters({
                      ...filters,
                      from: e.target.value
                        ? new Date(e.target.value).toISOString()
                        : "",
                    })
                  }
                  data-testid="activity-filter-from-input"
                  className="h-6 rounded-full border-0 bg-transparent px-1 text-[11px] focus-visible:outline-none"
                />
                <span className="text-[11px] text-muted-foreground">→</span>
                <input
                  type="datetime-local"
                  aria-label="To"
                  value={filters.to ? filters.to.slice(0, 16) : ""}
                  onChange={(e) =>
                    setFilters({
                      ...filters,
                      to: e.target.value
                        ? new Date(e.target.value).toISOString()
                        : "",
                    })
                  }
                  data-testid="activity-filter-to-input"
                  className="h-6 rounded-full border-0 bg-transparent px-1 text-[11px] focus-visible:outline-none"
                />
              </div>
            </FilterChip>
            {anyFilter && (
              <button
                type="button"
                onClick={clearFilters}
                data-testid="activity-filter-clear"
                className="ml-auto text-xs text-muted-foreground hover:text-foreground"
              >
                Clear filters
              </button>
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" />
            Recent Activity
            <Button
              variant="ghost"
              size="sm"
              className="ml-auto"
              onClick={() => refetch()}
              disabled={isFetching}
            >
              <RefreshCw
                className={`h-3.5 w-3.5 ${isFetching ? "animate-spin" : ""}`}
              />
            </Button>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {activityError && (
            <div className="mb-3">
              <ApiErrorMessage error={activityError} />
            </div>
          )}
          {isLoading && !result ? (
            <p className="text-sm text-muted-foreground">Loading activity...</p>
          ) : items.length === 0 ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid={`tab-${scheme}-activity-empty`}
            >
              {emptyMessage}
            </p>
          ) : filteredItems.length === 0 ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid={`tab-${scheme}-activity-filtered-empty`}
            >
              {emptyFilteredMessage}
            </p>
          ) : (
            // `aria-live="polite"` so screen readers announce new events as
            // they stream in (portal design doc §7 — accessibility).
            <div className="space-y-0" aria-live="polite">
              {filteredItems.map((e) => {
                const expandable = hasDetails(e);
                const isOpen = expanded.has(e.id);
                const detailsId = `activity-details-${e.id}`;
                return (
                  <div
                    key={e.id}
                    className="border-b border-border py-2 last:border-0 text-sm"
                    data-testid="activity-row"
                    data-event-id={e.id}
                  >
                    <div className="flex items-start gap-2">
                      {expandable ? (
                        <button
                          type="button"
                          aria-expanded={isOpen}
                          aria-controls={detailsId}
                          aria-label={
                            isOpen
                              ? "Collapse event details"
                              : "Expand event details"
                          }
                          onClick={() => toggleExpanded(e.id)}
                          data-testid="activity-row-toggle"
                          className="mt-0.5 inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        >
                          {isOpen ? (
                            <ChevronDown className="h-3.5 w-3.5" aria-hidden />
                          ) : (
                            <ChevronRight className="h-3.5 w-3.5" aria-hidden />
                          )}
                        </button>
                      ) : (
                        // Reserve the gutter so summaries align across
                        // expandable + non-expandable rows.
                        <span
                          aria-hidden
                          className="mt-0.5 inline-block h-5 w-5 shrink-0"
                        />
                      )}
                      <Badge
                        variant={
                          severityVariant[e.severity as ActivitySeverity] ??
                          "default"
                        }
                        className="mt-0.5 shrink-0"
                      >
                        {e.severity}
                      </Badge>
                      <div className="min-w-0 flex-1">
                        <p className="text-sm">{e.summary}</p>
                        <div className="mt-0.5 flex flex-wrap gap-2 text-xs text-muted-foreground">
                          <span>{humanEventType(e.eventType)}</span>
                          {e.cost != null && <span>${e.cost.toFixed(4)}</span>}
                          <span>{timeAgo(e.timestamp)}</span>
                        </div>
                      </div>
                    </div>
                    {expandable && isOpen && (
                      <pre
                        id={detailsId}
                        data-testid="activity-row-details"
                        className="mt-2 ml-12 max-h-64 overflow-auto rounded-md border border-border bg-muted/30 px-3 py-2 text-[11px] leading-relaxed text-foreground whitespace-pre-wrap break-words"
                      >
                        {JSON.stringify(
                          (e as { details?: unknown }).details,
                          null,
                          2,
                        )}
                      </pre>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
