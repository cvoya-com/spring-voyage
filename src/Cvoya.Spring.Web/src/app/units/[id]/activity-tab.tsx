"use client";

import { Activity, RefreshCw } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useActivityQuery } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type {
  ActivityQueryResult,
  ActivitySeverity,
} from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

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

  const errorMessage =
    error instanceof Error ? error.message : error ? String(error) : null;

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
        {errorMessage && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive mb-3">
            {errorMessage}
          </p>
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
            {result?.items.map((e) => (
              <div
                key={e.id}
                className="flex items-start gap-2 border-b border-border py-2 last:border-0 text-sm"
              >
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
                    <span>{e.eventType}</span>
                    {e.cost != null && <span>${e.cost.toFixed(4)}</span>}
                    <span>{timeAgo(e.timestamp)}</span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
