"use client";

// Analytics → Throughput — § 5.7 of `docs/design/portal-exploration.md`.
// Backed by `GET /api/v1/analytics/throughput`; CLI mirror is
// `spring analytics throughput --window <w> [--unit|--agent]` (PR #474).
// Every control on this page maps 1:1 to a CLI flag, per CONVENTIONS.md § 14.
//
// v2 reskin (SURF-reskin-analytics, #860): the KPI strip adopts
// `<StatCard>`; the per-source bar picks up a cycling brand-extension
// hue (voyage / blossom) so the visual weight of the list is legible
// at a glance; the table shell mirrors the Explorer `TabTraces` layout.
//
// #910: per-source breakdown now rendered as a stacked bar chart above
//   the detail list (recharts `<ThroughputBarChart>`).
// #911: the per-source detail list is now virtualised via `<DataTable>`.

import { Suspense, useMemo } from "react";
import Link from "next/link";
import { ArrowRight, BarChart3, Gauge } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { StatCard } from "@/components/stat-card";
import { ThroughputBarChart } from "@/components/analytics/throughput-bar-chart";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { DataTable, type DataTableColumn } from "@/components/ui/data-table";
import { Skeleton } from "@/components/ui/skeleton";
import { useAnalyticsThroughput } from "@/lib/api/queries";
import type { ThroughputEntryResponse } from "@/lib/api/types";

import {
  ANALYTICS_BREADCRUMBS,
  AnalyticsFiltersBar,
  useAnalyticsFilters,
} from "../analytics-filters";

/** `ThroughputEntryResponse` int64 fields arrive as `number | string`. */
function n(v: number | string | undefined | null): number {
  if (v === null || v === undefined) return 0;
  if (typeof v === "number") return v;
  const parsed = Number(v);
  return Number.isFinite(parsed) ? parsed : 0;
}

function entryTotal(e: ThroughputEntryResponse): number {
  return n(e.messagesReceived) + n(e.messagesSent) + n(e.turns) + n(e.toolCalls);
}

/**
 * Parses the wire-format source (`scheme://name`) into its scheme and
 * path parts. Returns `null` when the source carries no scheme, which
 * happens for free-form addresses; the row falls back to rendering the
 * raw source in that case.
 */
function parseSource(source: string): { scheme: string; name: string } | null {
  const idx = source.indexOf("://");
  if (idx < 0) return null;
  return { scheme: source.slice(0, idx), name: source.slice(idx + 3) };
}

// ---------------------------------------------------------------------------
// #911 — Throughput virtualised table columns
// ---------------------------------------------------------------------------

const THROUGHPUT_COLUMNS: DataTableColumn[] = [
  { key: "source", header: "Source", className: "flex-1 min-w-0" },
  { key: "received", header: "Received", className: "w-20 text-right" },
  { key: "sent", header: "Sent", className: "w-20 text-right" },
  { key: "turns", header: "Turns", className: "w-16 text-right" },
  { key: "toolCalls", header: "Tool calls", className: "w-20 text-right" },
  { key: "total", header: "Total", className: "w-16 text-right" },
];

function AnalyticsThroughputContent() {
  const filters = useAnalyticsFilters();
  const query = useAnalyticsThroughput({
    source: filters.sourceFilter,
    from: filters.from,
    to: filters.to,
  });

  const sortedEntries = useMemo(() => {
    const entries = query.data?.entries ?? [];
    return [...entries].sort((a, b) => entryTotal(b) - entryTotal(a));
  }, [query.data]);

  // KPI totals summed across every row in the visible window. Mirrors
  // the CLI's aggregate line `spring analytics throughput --summary`.
  const kpis = useMemo(
    () =>
      sortedEntries.reduce(
        (acc, e) => ({
          received: acc.received + n(e.messagesReceived),
          sent: acc.sent + n(e.messagesSent),
          turns: acc.turns + n(e.turns),
          toolCalls: acc.toolCalls + n(e.toolCalls),
        }),
        { received: 0, sent: 0, turns: 0, toolCalls: 0 },
      ),
    [sortedEntries],
  );

  // Shape for the stacked bar chart (#910).
  const chartEntries = useMemo(
    () =>
      sortedEntries.map((e) => ({
        source: e.source,
        received: n(e.messagesReceived),
        sent: n(e.messagesSent),
        turns: n(e.turns),
        toolCalls: n(e.toolCalls),
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
      <Breadcrumbs items={ANALYTICS_BREADCRUMBS.throughput as never} />
      <div>
        <h1 className="text-2xl font-bold">Throughput</h1>
        <p className="text-sm text-muted-foreground">
          Messages, turns, and tool calls per source over the selected window.
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
              spring analytics throughput --window {filters.window}{" "}
              {scopeHint}
            </code>
          </>
        }
      />

      {/* KPI strip — one StatCard per aggregated counter. */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard
          label="Messages received"
          value={kpis.received.toLocaleString()}
          icon={<BarChart3 className="h-4 w-4" />}
        />
        <StatCard
          label="Messages sent"
          value={kpis.sent.toLocaleString()}
          icon={<BarChart3 className="h-4 w-4" />}
        />
        <StatCard
          label="Turns"
          value={kpis.turns.toLocaleString()}
          icon={<Gauge className="h-4 w-4" />}
        />
        <StatCard
          label="Tool calls"
          value={kpis.toolCalls.toLocaleString()}
          icon={<Gauge className="h-4 w-4" />}
        />
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <BarChart3 className="h-4 w-4" /> Per-source counters
          </CardTitle>
        </CardHeader>
        <CardContent>
          {query.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-6 w-full" />
              <Skeleton className="h-6 w-full" />
              <Skeleton className="h-6 w-full" />
            </div>
          ) : query.isError ? (
            <p className="text-sm text-destructive">
              Failed to load throughput: {query.error.message}
            </p>
          ) : sortedEntries.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No throughput in this window.
            </p>
          ) : (
            <div className="space-y-4">
              {/* #910: stacked bar chart summarises the metric mix at a
                  glance before the operator scans the per-row detail table. */}
              <ThroughputBarChart entries={chartEntries} />

              {/* #911: virtualised detail table — keeps DOM lean at 500 rows. */}
              <DataTable<ThroughputEntryResponse>
                aria-label="Per-source throughput counters"
                columns={THROUGHPUT_COLUMNS}
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
                  if (col.key === "received") return <span className="tabular-nums">{n(entry.messagesReceived).toLocaleString()}</span>;
                  if (col.key === "sent") return <span className="tabular-nums">{n(entry.messagesSent).toLocaleString()}</span>;
                  if (col.key === "turns") return <span className="tabular-nums">{n(entry.turns).toLocaleString()}</span>;
                  if (col.key === "toolCalls") return <span className="tabular-nums">{n(entry.toolCalls).toLocaleString()}</span>;
                  if (col.key === "total") return <span className="font-medium tabular-nums">{entryTotal(entry).toLocaleString()}</span>;
                  return null;
                }}
              />
            </div>
          )}
        </CardContent>
      </Card>

      <div className="flex flex-wrap gap-3 text-xs text-muted-foreground">
        <Link
          href="/activity"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Raw activity stream <ArrowRight className="h-3 w-3" />
        </Link>
        <Link
          href="/analytics/waits"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          Wait times <ArrowRight className="h-3 w-3" />
        </Link>
      </div>
    </div>
  );
}

/** Wraps the content component in a Suspense boundary because the
 *  filter bar rides on `useSearchParams`; the App Router forbids
 *  bare-prerender of routes that read search params synchronously.
 */
export default function AnalyticsThroughputPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <AnalyticsThroughputContent />
    </Suspense>
  );
}
