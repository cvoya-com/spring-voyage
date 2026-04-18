"use client";

// Analytics → Wait times — § 5.7 of `docs/design/portal-exploration.md`.
// Backed by `GET /api/v1/analytics/waits`; CLI mirror is
// `spring analytics waits --window <w> [--unit|--agent]` (PR #474).
// Durations are computed from paired StateChanged lifecycle transitions
// (#476, Rx activity pipeline PR #484). Every control maps 1:1 to a CLI
// flag per CONVENTIONS.md § 14.

import { Suspense } from "react";
import Link from "next/link";
import { ArrowRight, Clock } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
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

function AnalyticsWaitsContent() {
  const filters = useAnalyticsFilters();
  const query = useAnalyticsWaits({
    source: filters.sourceFilter,
    from: filters.from,
    to: filters.to,
  });

  const entries = query.data?.entries ?? [];
  const sortedEntries = [...entries].sort(
    (a, b) => totalSeconds(b) - totalSeconds(a),
  );
  const maxTotal =
    sortedEntries.length > 0 ? totalSeconds(sortedEntries[0]) : 0;

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
            <ul className="space-y-3">
              <li className="grid grid-cols-[1fr_repeat(4,auto)] items-center gap-3 text-xs font-medium text-muted-foreground">
                <span>Source</span>
                <span className="w-16 text-right">Idle</span>
                <span className="w-16 text-right">Busy</span>
                <span className="w-20 text-right">Waiting</span>
                <span className="w-20 text-right">Transitions</span>
              </li>
              {sortedEntries.map((entry) => {
                const idle = n(entry.idleSeconds);
                const busy = n(entry.busySeconds);
                const waiting = n(entry.waitingForHumanSeconds);
                const total = idle + busy + waiting;
                const parsed = parseSource(entry.source);
                const href = parsed
                  ? parsed.scheme === "unit"
                    ? `/units/${encodeURIComponent(parsed.name)}`
                    : parsed.scheme === "agent"
                      ? `/agents/${encodeURIComponent(parsed.name)}`
                      : null
                  : null;
                const rowScale = maxTotal > 0 ? total / maxTotal : 0;
                // Keep the bar legible even for the shortest row.
                const barPct = Math.max(rowScale * 100, 4);
                const idlePct = total > 0 ? (idle / total) * 100 : 0;
                const busyPct = total > 0 ? (busy / total) * 100 : 0;
                const waitPct = total > 0 ? (waiting / total) * 100 : 0;
                return (
                  <li
                    key={entry.source}
                    className="grid grid-cols-[1fr_repeat(4,auto)] items-center gap-3 text-sm"
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
                        className="mt-1 h-2 overflow-hidden rounded-full bg-muted"
                        aria-hidden="true"
                      >
                        <div
                          className="flex h-full"
                          style={{ width: `${barPct}%` }}
                        >
                          <div
                            className="h-full bg-emerald-500/70"
                            title={`Idle ${formatDuration(idle)}`}
                            style={{ width: `${idlePct}%` }}
                          />
                          <div
                            className="h-full bg-amber-500/70"
                            title={`Busy ${formatDuration(busy)}`}
                            style={{ width: `${busyPct}%` }}
                          />
                          <div
                            className="h-full bg-rose-500/70"
                            title={`Waiting for human ${formatDuration(waiting)}`}
                            style={{ width: `${waitPct}%` }}
                          />
                        </div>
                      </div>
                    </div>
                    <span className="w-16 text-right tabular-nums">
                      {formatDuration(idle)}
                    </span>
                    <span className="w-16 text-right tabular-nums">
                      {formatDuration(busy)}
                    </span>
                    <span className="w-20 text-right tabular-nums">
                      {formatDuration(waiting)}
                    </span>
                    <span className="w-20 text-right tabular-nums text-muted-foreground">
                      {n(entry.stateTransitions).toLocaleString()}
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </CardContent>
      </Card>

      <div className="flex flex-wrap items-center gap-4 text-xs text-muted-foreground">
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-emerald-500/70"
            aria-hidden="true"
          />
          Idle
        </div>
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-amber-500/70"
            aria-hidden="true"
          />
          Busy
        </div>
        <div className="flex items-center gap-1">
          <span
            className="inline-block h-2 w-2 rounded-full bg-rose-500/70"
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
