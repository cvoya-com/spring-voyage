"use client";

// Analytics → Wait times — § 5.7 of `docs/design/portal-exploration.md`.
// Backed by `GET /api/v1/analytics/waits`; CLI mirror is
// `spring analytics waits --window <w> [--unit|--agent]` (PR #474).
// Durations are computed from paired StateChanged lifecycle transitions
// (#476, Rx activity pipeline PR #484). Every control maps 1:1 to a CLI
// flag per CONVENTIONS.md § 14.
//
// v2 reskin (SURF-reskin-analytics, #860): KPI strip uses `<StatCard>`;
// the stacked idle/busy/waiting bar adopts semantic status tokens
// (success / warning / destructive) so the colour travels through
// theming instead of reaching for raw Tailwind hex utilities.
//
// #910: per-source breakdown now rendered as a stacked bar chart above
//   the detail list (recharts `<WaitsBarChart>`).
// #911: the per-source detail list is now virtualised via `<DataTable>`.

import { Suspense, useMemo } from "react";
import Link from "next/link";
import { ArrowRight, Clock, Pause, Play, UserCheck } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { StatCard } from "@/components/stat-card";
import { WaitsBarChart } from "@/components/analytics/waits-bar-chart";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { DataTable, type DataTableColumn } from "@/components/ui/data-table";
import { Skeleton } from "@/components/ui/skeleton";
import { useAnalyticsWaits } from "@/lib/api/queries";
import type { WaitTimeEntryResponse } from "@/lib/api/types";

import {
  ANALYTICS_BREADCRUMBS,
  AnalyticsFiltersBar,
  useAnalyticsFilters,
} from "../analytics-filters";

function n(v: number | string | undefined | null): number {
  if (v === null || v === undefined) return 0;
  if (typeof v === "number") return v;
  const parsed = Number(v);
  return Number.isFinite(parsed) ? parsed : 0;
}

function totalSeconds(e: WaitTimeEntryResponse): number {
  return (
    n(e.idleSeconds) + n(e.busySeconds) + n(e.waitingForHumanSeconds)
  );
}

/**
 * Renders a duration as `Xd Yh`, `Yh Zm`, or `Zm Ws` depending on
 * magnitude so the chip stays short but loses no information at the
 * ranges operators care about.
 */
function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0s";
  const s = Math.floor(seconds);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${sec}s`;
  return `${sec}s`;
}

function parseSource(source: string): { scheme: string; name: string } | null {
  const idx = source.indexOf("://");
  if (idx < 0) return null;
  return { scheme: source.slice(0, idx), name: source.slice(idx + 3) };
}

// ---------------------------------------------------------------------------
// #911 — Waits virtualised table columns
// ---------------------------------------------------------------------------

const WAITS_COLUMNS: DataTableColumn[] = [
  { key: "source", header: "Source", className: "flex-1 min-w-0" },
  { key: "idle", header: "Idle", className: "w-20 text-right" },
  { key: "busy", header: "Busy", className: "w-20 text-right" },
  { key: "waiting", header: "Waiting", className: "w-24 text-right" },
  { key: "transitions", header: "Transitions", className: "w-24 text-right" },
];

function AnalyticsWaitsContent() {
  const filters = useAnalyticsFilters();
  const query = useAnalyticsWaits({
    source: filters.sourceFilter,
    from: filters.from,
    to: filters.to,
  });

  const sortedEntries = useMemo(() => {
    const entries = query.data?.entries ?? [];
    return [...entries].sort((a, b) => totalSeconds(b) - totalSeconds(a));
  }, [query.data]);

  // KPI totals across every row in the window. Mirrors the CLI's
  // `spring analytics waits --summary` aggregate.
  const kpis = useMemo(
    () =>
      sortedEntries.reduce(
        (acc, e) => ({
          idle: acc.idle + n(e.idleSeconds),
          busy: acc.busy + n(e.busySeconds),
          waiting: acc.waiting + n(e.waitingForHumanSeconds),
          transitions: acc.transitions + n(e.stateTransitions),
        }),
        { idle: 0, busy: 0, waiting: 0, transitions: 0 },
      ),
    [sortedEntries],
  );

  // Shape for the stacked bar chart (#910).
  const chartEntries = useMemo(
    () =>
      sortedEntries.map((e) => ({
        source: e.source,
        idleSeconds: n(e.idleSeconds),
        busySeconds: n(e.busySeconds),
        waitingSeconds: n(e.waitingForHumanSeconds),
      })),
    [sortedEntries],
  );

  const scopeHint = (() => {
    if (filters.scope.kind === "unit") {
      return `--unit ${filters.scope.name || "<name>"} `;
    }
    if (filters.scope.kind === "agent") {
      return `--agent ${filters.scope.name || "<name>"} `;
    }
    return "";
  })();

  return (
    <div className="space-y-6">
      <Breadcrumbs items={ANALYTICS_BREADCRUMBS.waits as never} />
      <div>
        <h1 className="text-2xl font-bold">Wait times</h1>
        <p className="text-sm text-muted-foreground">
          Time-in-state rollups per source. Durations come from paired
          <span className="px-1 font-mono">StateChanged</span>
          lifecycle transitions; the raw transition count is also shown
          so you can tell quiet from never-transitioned.
        </p>
      </div>

      <AnalyticsFiltersBar
        windowValue={filters.window}
        onWindowChange={filters.setWindow}
        scope={filters.scope}
        onScopeChange={filters.setScope}
        hint={
          <>
            CLI:{" "}
            <code className="font-mono">
              spring analytics waits --window {filters.window} {scopeHint}
            </code>
          </>
        }
      />

      {/* KPI strip — one StatCard per aggregated duration / counter. */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard
          label="Idle"
          value={formatDuration(kpis.idle)}
          icon={<Pause className="h-4 w-4" />}
        />
        <StatCard
          label="Busy"
          value={formatDuration(kpis.busy)}
          icon={<Play className="h-4 w-4" />}
        />
        <StatCard
          label="Waiting for human"
          value={formatDuration(kpis.waiting)}
          icon={<UserCheck className="h-4 w-4" />}
        />
        <StatCard
          label="Transitions"
          value={kpis.transitions.toLocaleString()}
          icon={<Clock className="h-4 w-4" />}
        />
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Clock className="h-4 w-4" /> Per-source durations
          </CardTitle>
        </CardHeader>
        <CardContent>
          {query.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-full" />
            </div>
          ) : query.isError ? (
            <p className="text-sm text-destructive">
              Failed to load wait times: {query.error.message}
            </p>
          ) : sortedEntries.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No state transitions in this window.
            </p>
          ) : (
            <div className="space-y-4">
              {/* #910: stacked bar chart surfaces the idle/busy/waiting
                  split across sources so spikes are visible without
                  scanning the whole table. Colours match the legend strip
                  below. */}
              <WaitsBarChart entries={chartEntries} />

              {/* #911: virtualised detail table. */}
              <DataTable<WaitTimeEntryResponse>
                aria-label="Per-source wait-time durations"
                columns={WAITS_COLUMNS}
                rows={sortedEntries}
                estimateSize={() => 48}
                height={Math.min(360, sortedEntries.length * 48 + 40)}
                getRowKey={(row) => row.source}
                renderCell={(entry, col) => {
                  if (col.key === "source") {
                    const parsed = parseSource(entry.source);
                    const href = parsed
                      ? parsed.scheme === "unit"
                        ? `/units?node=${encodeURIComponent(parsed.name)}&tab=Overview`
                        : parsed.scheme === "agent"
                          ? `/agents/${encodeURIComponent(parsed.name)}`
                          : null
                      : null;
                    return (
                      <span className="truncate font-mono text-xs">
                        {href ? (
                          <Link href={href} className="text-primary hover:underline">
                            {entry.source}
                          </Link>
                        ) : (
                          entry.source
                        )}
                      </span>
                    );
                  }
                  if (col.key === "idle") return <span className="tabular-nums text-success">{formatDuration(n(entry.idleSeconds))}</span>;
                  if (col.key === "busy") return <span className="tabular-nums text-warning">{formatDuration(n(entry.busySeconds))}</span>;
                  if (col.key === "waiting") return <span className="tabular-nums text-destructive">{formatDuration(n(entry.waitingForHumanSeconds))}</span>;
                  if (col.key === "transitions") return <span className="tabular-nums text-muted-foreground">{n(entry.stateTransitions).toLocaleString()}</span>;
                  return null;
                }}
              />
            </div>
          )}
        </CardContent>
      </Card>

      <div className="flex flex-wrap items-center gap-4 text-xs text-muted-foreground">
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-success"
            aria-hidden="true"
          />
          Idle
        </div>
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-warning"
            aria-hidden="true"
          />
          Busy
        </div>
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-destructive"
            aria-hidden="true"
          />
          Waiting for human
        </div>
      </div>

      <div className="flex flex-wrap gap-3 text-xs text-muted-foreground">
        <Link
          href="/analytics/throughput"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Throughput <ArrowRight className="h-3 w-3" />
        </Link>
        <Link
          href="/activity"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Raw activity stream <ArrowRight className="h-3 w-3" />
        </Link>
        <Link
          href="/policies"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Policies (cost / execution-mode caps) <ArrowRight className="h-3 w-3" />
        </Link>
      </div>
    </div>
  );
}

/** Wraps the content component in a Suspense boundary because the
 *  filter bar rides on `useSearchParams`; the App Router forbids
 *  bare-prerender of routes that read search params synchronously.
 */
export default function AnalyticsWaitsPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <AnalyticsWaitsContent />
    </Suspense>
  );
}
