"use client";

import { useState } from "react";
import { Activity, ChevronDown, ChevronRight, RefreshCw } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useActivityQuery } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type {
  ActivityQueryResult,
  ActivitySeverity,
} from "@/lib/api/types";
import { humanEventType, timeAgo } from "@/lib/utils";

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

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

export function ActivityTab({ unitId }: { unitId: string }) {
  // REST baseline — paginated query for this unit's events. The
  // stream layered on top keeps it fresh (#437).
  const queryParams = { source: `unit:${unitId}`, pageSize: "20" };
  const {
    data: result,
    error,
    isLoading,
    isFetching,
    refetch,
  } = useActivityQuery(queryParams);

  // Subscribe to the unit-scoped live stream so the tab updates as
  // events arrive — no more manual refresh loop. The hook invalidates
  // the matching cache slice on every event, so the `useActivityQuery`
  // above re-fetches and the list stays in order.
  useActivityStream({
    filter: (event) =>
      event.source.scheme === "unit" && event.source.path === unitId,
  });

  const activityError = error ?? null;

  // Expanded-row tracker: clicking a row toggles its `id` in the set so
  // the structured `details` payload is shown inline. Kept in component
  // state (no URL persistence) — expansion is ephemeral context, not a
  // navigation surface.
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const toggleExpanded = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
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
        ) : (result as ActivityQueryResult | undefined)?.items.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No activity events for this unit.
          </p>
        ) : (
          // `aria-live="polite"` so screen readers announce new events as
          // they stream in (portal design doc §7 — accessibility).
          <div className="space-y-0" aria-live="polite">
            {result?.items.map((e) => {
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
                          isOpen ? "Collapse event details" : "Expand event details"
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
                      <span aria-hidden className="mt-0.5 inline-block h-5 w-5 shrink-0" />
                    )}
                    <Badge
                      variant={
                        severityVariant[e.severity as ActivitySeverity] ?? "default"
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
  );
}
