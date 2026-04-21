"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import {
  Activity,
  ChevronDown,
  ChevronRight,
  ChevronLeft,
  ExternalLink,
  MessagesSquare,
  RefreshCw,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useActivityQuery } from "@/lib/api/queries";
import type {
  ActivityEventType,
  ActivityQueryResult,
  ActivitySeverity,
} from "@/lib/api/types";
import { cn, timeAgo } from "@/lib/utils";

// Map a `scheme://path` source string onto the matching detail route.
// Returns null when the scheme doesn't have a portal page yet. Mirrors
// `docs/design/portal-exploration.md` § 3.3 cross-link rules.
function sourceHref(source: string): string | null {
  const m = source.match(/^([a-z]+):\/\/(.+)$/i);
  if (!m) return null;
  const [, scheme, path] = m;
  switch (scheme.toLowerCase()) {
    case "agent":
    case "unit":
      // The legacy `/agents/[id]` and `/units/[id]` routes were retired
      // by the v2 Explorer migration (#815); the canonical surface is
      // the unified `/units?node=<id>` Explorer.
      return `/units?node=${encodeURIComponent(path)}`;
    default:
      return null;
  }
}

/**
 * Build a deep-link into the Messages tab of the event's source node
 * with the correlation-id selected. Falls back to `null` when the
 * source is not a unit/agent (e.g. `human://`, `system://`) — in that
 * case the UI still surfaces the correlation id, just without an
 * "open thread" affordance.
 */
function conversationHref(source: string, correlationId: string): string | null {
  const m = source.match(/^([a-z]+):\/\/(.+)$/i);
  if (!m) return null;
  const [, scheme, path] = m;
  const s = scheme.toLowerCase();
  if (s !== "agent" && s !== "unit") return null;
  return `/units?node=${encodeURIComponent(path)}&tab=Messages&conversation=${encodeURIComponent(correlationId)}`;
}

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive" | "outline"
> = {
  Debug: "outline",
  Info: "default",
  Warning: "warning",
  Error: "destructive",
};

// Severity -> status-dot colour. Mirrors the `<DetailPane>` pattern in
// `components/units/unit-detail-pane.tsx` so every surface in v2 grades
// severity with the same swatch.
const severityDot: Record<ActivitySeverity, string> = {
  Debug: "bg-debug",
  Info: "bg-info",
  Warning: "bg-warning",
  Error: "bg-destructive",
};

const eventTypes: ActivityEventType[] = [
  "MessageReceived",
  "MessageSent",
  "ConversationStarted",
  "ConversationCompleted",
  "DecisionMade",
  "ErrorOccurred",
  "StateChanged",
  "InitiativeTriggered",
  "ReflectionCompleted",
  "WorkflowStepCompleted",
  "CostIncurred",
  "TokenDelta",
];

const severities: ActivitySeverity[] = ["Debug", "Info", "Warning", "Error"];

interface Filters {
  source: string;
  eventType: string;
  severity: string;
}

const PAGE_SIZE = 20;

/**
 * Filter chip — the v2 filter-bar primitive. Each chip collapses the
 * raw `<select>` / `<input>` into a pill-styled control that matches
 * the brand-extension utilities (mono identifier, blossom accents on
 * active filters). Kept local because the analytics surface ships its
 * own chip variant — the two diverge on behaviour, not on styling.
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

function EventRow({
  event,
  expanded,
  onToggle,
}: {
  event: ActivityQueryResult["items"][number];
  expanded: boolean;
  onToggle: () => void;
}) {
  const severity = event.severity as ActivitySeverity;
  return (
    <div className="border-b border-border last:border-0">
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-start gap-3 rounded-md px-2 py-3 text-left transition-colors hover:bg-accent/50"
      >
        {/* Status dot — severity-coded. Mirrors the Explorer DetailPane
            pattern so every surface reads the same. */}
        <span
          aria-hidden="true"
          data-testid={`activity-severity-dot-${event.id}`}
          data-severity={severity}
          className={cn(
            "mt-1.5 h-2 w-2 shrink-0 rounded-full",
            severityDot[severity] ?? "bg-muted-foreground",
          )}
        />
        {expanded ? (
          <ChevronDown className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" />
        ) : (
          <ChevronRight className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" />
        )}
        {/* Row reflow at narrow widths: summary on top, metadata
            (time, source, event type, severity) wraps beneath. sm+
            restores the single-row inline layout. */}
        <div className="min-w-0 flex-1 space-y-1">
          <p className="truncate text-sm">{event.summary}</p>
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <span className="tabular-nums">
              {timeAgo(event.timestamp)}
            </span>
            {/* Mono-badge: scheme://name identifiers read as code. */}
            <Badge
              variant="outline"
              className="max-w-full truncate font-mono text-xs"
            >
              {event.source}
            </Badge>
            {/* Brand pill: the event-type label sits in the brand/action
                hue so operators can scan a long feed for a specific
                event kind without reading the text. */}
            <Badge variant="secondary" className="text-[11px]">
              {event.eventType}
            </Badge>
            <Badge variant={severityVariant[severity] ?? "default"}>
              {event.severity}
            </Badge>
          </div>
        </div>
      </button>
      {expanded && (
        <div className="mb-3 ml-10 space-y-1 rounded-md border border-border bg-muted/30 p-3 text-sm">
          <div className="flex gap-2">
            <span className="text-muted-foreground">ID:</span>
            <span className="font-mono text-xs">{event.id}</span>
          </div>
          {event.correlationId && (
            <div className="flex items-center gap-2">
              <span className="text-muted-foreground">Correlation ID:</span>
              <span className="font-mono text-xs">{event.correlationId}</span>
              {(() => {
                const threadHref = conversationHref(
                  event.source,
                  event.correlationId,
                );
                return threadHref ? (
                  <Link
                    href={threadHref}
                    className="inline-flex items-center gap-1 rounded border border-input bg-background px-2 py-0.5 text-xs text-primary hover:bg-accent"
                    aria-label="Open conversation thread"
                  >
                    <MessagesSquare className="h-3 w-3" />
                    Open thread
                  </Link>
                ) : null;
              })()}
            </div>
          )}
          {(() => {
            const href = sourceHref(event.source);
            return href ? (
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-muted-foreground">Source:</span>
                <Link
                  href={href}
                  className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
                  data-testid={`activity-event-source-link-${event.id}`}
                >
                  Open {event.source}
                  <ExternalLink className="h-3 w-3" />
                </Link>
              </div>
            ) : null;
          })()}
          {event.cost != null && (
            <div className="flex gap-2">
              <span className="text-muted-foreground">Cost:</span>
              <span>${event.cost.toFixed(4)}</span>
            </div>
          )}
          <div className="flex gap-2">
            <span className="text-muted-foreground">Timestamp:</span>
            <span>{new Date(event.timestamp).toLocaleString()}</span>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Compact SVG sparkline rendering the count of events per severity across
 * the visible window. Decorative — the numbers are also rendered as chips
 * beside it so screen readers get the same signal.
 *
 * Matches the `UnitSparkline` helper in `components/cards/unit-card.tsx`
 * (§12 of the umbrella plan), keeping a single shape across every surface
 * that grades severity over time.
 */
function SeveritySparkline({ items }: { items: ActivityQueryResult["items"] }) {
  const series = useMemo(() => {
    if (items.length === 0) return [] as number[];
    // Bucket into 12 equal-count slots so a small page (20 events) still
    // renders a coherent line. For larger windows the buckets grow, for
    // smaller ones they shrink — either way 12 is the resolution.
    const buckets = 12;
    const size = Math.max(1, Math.ceil(items.length / buckets));
    const slots: number[] = [];
    for (let i = 0; i < items.length; i += size) {
      const slice = items.slice(i, i + size);
      // Severity score: Error = 3, Warning = 2, Info = 1, Debug = 0.5.
      // Aggregating into a weighted count produces a stable line instead
      // of a binary error/not-error flash.
      const weight = slice.reduce((acc, e) => {
        const s = e.severity as ActivitySeverity;
        return (
          acc +
          (s === "Error" ? 3 : s === "Warning" ? 2 : s === "Info" ? 1 : 0.5)
        );
      }, 0);
      slots.push(weight);
    }
    return slots;
  }, [items]);

  if (series.length === 0) {
    return (
      <span
        aria-hidden="true"
        data-testid="activity-sparkline-placeholder"
        className="inline-block h-4 w-20 rounded-sm bg-muted"
      />
    );
  }

  const max = Math.max(1, ...series);
  const width = 80;
  const height = 16;
  const step = series.length > 1 ? width / (series.length - 1) : 0;
  const points = series
    .map(
      (v, i) =>
        `${(i * step).toFixed(1)},${(
          height -
          (v / max) * height
        ).toFixed(1)}`,
    )
    .join(" ");

  return (
    <svg
      role="img"
      aria-label="Recent event severity trend"
      data-testid="activity-sparkline"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="text-primary/80"
    >
      <polyline
        points={points}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

export default function ActivityPage() {
  const [page, setPage] = useState(1);
  const [filters, setFilters] = useState<Filters>({
    source: "",
    eventType: "",
    severity: "",
  });
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());

  // Build the query params object once per (page, filters) change so the
  // memoised reference keeps the TanStack cache key stable across renders.
  const params = useMemo<Record<string, string>>(() => {
    const p: Record<string, string> = {
      page: String(page),
      pageSize: String(PAGE_SIZE),
    };
    if (filters.source) p.source = filters.source;
    if (filters.eventType) p.eventType = filters.eventType;
    if (filters.severity) p.severity = filters.severity;
    return p;
  }, [page, filters]);

  const {
    data: result,
    isLoading,
    isFetching,
    isError,
    error,
    refetch,
  } = useActivityQuery(params);

  const totalPages = result
    ? Math.max(1, Math.ceil(Number(result.totalCount) / Number(result.pageSize)))
    : 1;

  const toggleExpanded = (id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const handleFilterChange = (key: keyof Filters, value: string) => {
    setFilters((prev) => ({ ...prev, [key]: value }));
    setPage(1);
  };

  // The refresh button should reflect an in-flight fetch whether it's the
  // initial load or a manual refresh — `isFetching` captures both.
  const loading = isFetching;

  const anyFilter = Boolean(
    filters.source || filters.eventType || filters.severity,
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold">Activity</h1>
          <p className="text-xs text-muted-foreground">
            Tenant-wide event stream. Filter by source, type, or severity.
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => refetch()}
          disabled={loading}
          className="self-start sm:self-auto"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${loading ? "animate-spin" : ""}`}
          />
          Refresh
        </Button>
      </div>

      {/* Filters — filter-chip pattern per the v2 kit. Each chip is a
          self-contained label + control pair; the active chips pick up
          the brand tint so the active filter set is legible at a glance. */}
      <Card>
        <CardContent className="pt-4">
          <div className="flex flex-wrap items-center gap-2">
            <FilterChip label="Source" active={filters.source.length > 0}>
              <Input
                placeholder="e.g. unit:my-unit"
                value={filters.source}
                onChange={(e) => handleFilterChange("source", e.target.value)}
                className="h-7 w-40 border-0 bg-transparent px-0 font-mono shadow-none focus-visible:ring-0"
              />
            </FilterChip>
            <FilterChip label="Type" active={filters.eventType.length > 0}>
              <select
                aria-label="Event Type"
                value={filters.eventType}
                onChange={(e) =>
                  handleFilterChange("eventType", e.target.value)
                }
                className="h-7 w-36 rounded-full border-0 bg-transparent text-xs focus-visible:outline-none"
              >
                <option value="">All types</option>
                {eventTypes.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </select>
            </FilterChip>
            <FilterChip label="Severity" active={filters.severity.length > 0}>
              <select
                aria-label="Severity"
                value={filters.severity}
                onChange={(e) =>
                  handleFilterChange("severity", e.target.value)
                }
                className="h-7 w-24 rounded-full border-0 bg-transparent text-xs focus-visible:outline-none"
              >
                <option value="">All</option>
                {severities.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </select>
            </FilterChip>
            {anyFilter && (
              <button
                type="button"
                onClick={() =>
                  setFilters({ source: "", eventType: "", severity: "" })
                }
                className="ml-auto text-xs text-muted-foreground hover:text-foreground"
              >
                Clear filters
              </button>
            )}
          </div>
        </CardContent>
      </Card>

      {/* Event list */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" />
            Events
            {result && result.items.length > 0 && (
              <SeveritySparkline items={result.items} />
            )}
            {result && (
              <span className="ml-auto text-sm font-normal text-muted-foreground">
                {result.totalCount} total
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent>
          {isError && (
            <p className="mb-3 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {error instanceof Error ? error.message : String(error)}
            </p>
          )}
          {isLoading && !result ? (
            <p className="text-sm text-muted-foreground">Loading activity...</p>
          ) : result?.items.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No activity events found.
            </p>
          ) : (
            <div className="max-h-[calc(100vh-400px)] overflow-y-auto">
              {result?.items.map((e) => (
                <EventRow
                  key={e.id}
                  event={e}
                  expanded={expandedIds.has(e.id)}
                  onToggle={() => toggleExpanded(e.id)}
                />
              ))}
            </div>
          )}

          {/* Pagination */}
          {result && totalPages > 1 && (
            <div className="mt-4 flex items-center justify-between border-t border-border pt-3">
              <Button
                variant="outline"
                size="sm"
                disabled={page <= 1}
                onClick={() => setPage((p) => p - 1)}
              >
                <ChevronLeft className="h-4 w-4 mr-1" /> Previous
              </Button>
              <span className="text-sm text-muted-foreground">
                Page {page} of {totalPages}
              </span>
              <Button
                variant="outline"
                size="sm"
                disabled={page >= totalPages}
                onClick={() => setPage((p) => p + 1)}
              >
                Next <ChevronRight className="h-4 w-4 ml-1" />
              </Button>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
