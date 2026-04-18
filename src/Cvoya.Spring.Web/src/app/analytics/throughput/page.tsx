"use client";

// Analytics → Throughput — § 5.7 of `docs/design/portal-exploration.md`.
// Backed by `GET /api/v1/analytics/throughput`; CLI mirror is
// `spring analytics throughput --window <w> [--unit|--agent]` (PR #474).
// Every control on this page maps 1:1 to a CLI flag, per CONVENTIONS.md § 14.

import { Suspense } from "react";
import Link from "next/link";
import { BarChart3, ArrowRight } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
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

function AnalyticsThroughputContent() {
  const filters = useAnalyticsFilters();
  const query = useAnalyticsThroughput({
    source: filters.sourceFilter,
    from: filters.from,
    to: filters.to,
  });

  const entries = query.data?.entries ?? [];
  const sortedEntries = [...entries].sort(
    (a, b) => entryTotal(b) - entryTotal(a),
  );
  const maxTotal = sortedEntries.length > 0 ? entryTotal(sortedEntries[0]) : 0;

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
            // Narrow viewports collapse the 6-column grid into a
            // stacked row: each row renders the source + bar on top and
            // a 2×2 (plus total) metrics grid below. On sm+ we restore
            // the original grid so the wide layout reads as a compact
            // table.
            <ul className="divide-y divide-border" data-testid="throughput-list">
              <li
                className="hidden items-center gap-3 pb-2 text-xs font-medium text-muted-foreground sm:grid sm:grid-cols-[1fr_repeat(5,auto)]"
                aria-hidden="true"
              >
                <span>Source</span>
                <span className="w-16 text-right">Received</span>
                <span className="w-16 text-right">Sent</span>
                <span className="w-16 text-right">Turns</span>
                <span className="w-20 text-right">Tool calls</span>
                <span className="w-14 text-right">Total</span>
              </li>
              {sortedEntries.map((entry) => {
                const total = entryTotal(entry);
                const width = maxTotal > 0 ? (total / maxTotal) * 100 : 0;
                const parsed = parseSource(entry.source);
                const href = parsed
                  ? parsed.scheme === "unit"
                    ? `/units/${encodeURIComponent(parsed.name)}`
                    : parsed.scheme === "agent"
                      ? `/agents/${encodeURIComponent(parsed.name)}`
                      : null
                  : null;
                return (
                  <li
                    key={entry.source}
                    className="flex flex-col gap-2 py-2 text-sm sm:grid sm:grid-cols-[1fr_repeat(5,auto)] sm:items-center sm:gap-3"
                  >
                    <div className="min-w-0">
                      <div className="truncate font-mono text-xs">
                        {href ? (
                          <Link
                            href={href}
                            className="text-primary hover:underline"
                          >
                            {entry.source}
                          </Link>
                        ) : (
                          entry.source
                        )}
                      </div>
                      <div
                        className="mt-1 h-1.5 overflow-hidden rounded-full bg-muted"
                        aria-hidden="true"
                      >
                        <div
                          className="h-full bg-primary/70"
                          style={{ width: `${width}%` }}
                        />
                      </div>
                    </div>
                    {/* Mobile: 2×2 stat grid under the bar.
                        sm+: restore the legacy per-column cells. */}
                    <div className="grid grid-cols-2 gap-x-3 gap-y-1 text-xs sm:hidden">
                      <div className="flex justify-between">
                        <span className="text-muted-foreground">Received</span>
                        <span className="tabular-nums">
                          {n(entry.messagesReceived).toLocaleString()}
                        </span>
                      </div>
                      <div className="flex justify-between">
                        <span className="text-muted-foreground">Sent</span>
                        <span className="tabular-nums">
                          {n(entry.messagesSent).toLocaleString()}
                        </span>
                      </div>
                      <div className="flex justify-between">
                        <span className="text-muted-foreground">Turns</span>
                        <span className="tabular-nums">
                          {n(entry.turns).toLocaleString()}
                        </span>
                      </div>
                      <div className="flex justify-between">
                        <span className="text-muted-foreground">Tool calls</span>
                        <span className="tabular-nums">
                          {n(entry.toolCalls).toLocaleString()}
                        </span>
                      </div>
                      <div className="col-span-2 flex justify-between border-t border-border pt-1">
                        <span className="text-muted-foreground">Total</span>
                        <span className="font-medium tabular-nums">
                          {total.toLocaleString()}
                        </span>
                      </div>
                    </div>
                    <span className="hidden w-16 text-right tabular-nums sm:inline">
                      {n(entry.messagesReceived).toLocaleString()}
                    </span>
                    <span className="hidden w-16 text-right tabular-nums sm:inline">
                      {n(entry.messagesSent).toLocaleString()}
                    </span>
                    <span className="hidden w-16 text-right tabular-nums sm:inline">
                      {n(entry.turns).toLocaleString()}
                    </span>
                    <span className="hidden w-20 text-right tabular-nums sm:inline">
                      {n(entry.toolCalls).toLocaleString()}
                    </span>
                    <span className="hidden w-14 text-right font-medium tabular-nums sm:inline">
                      {total.toLocaleString()}
                    </span>
                  </li>
                );
              })}
            </ul>
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
