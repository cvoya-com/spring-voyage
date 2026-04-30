"use client";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import type { ActivityEvent, ActivitySeverity } from "@/lib/api/types";
import { humanEventType, timeAgo } from "@/lib/utils";
import { Activity } from "lucide-react";

const severityColors: Record<ActivitySeverity, string> = {
  Debug: "bg-muted-foreground",
  Info: "bg-blue-500",
  Warning: "bg-warning",
  Error: "bg-destructive",
};

export function ActivityFeed({
  items,
  maxHeight = "400px",
}: {
  items: ActivityEvent[];
  maxHeight?: string;
}) {
  if (items.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-4 w-4" aria-hidden="true" /> Activity Feed
          </CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">No activity yet</p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Activity className="h-4 w-4" aria-hidden="true" /> Activity Feed
        </CardTitle>
      </CardHeader>
      {/* `aria-live="polite"` + `role="log"` announces new entries to
          assistive tech as the SSE stream appends them. `aria-atomic=
          "false"` prevents the entire feed being re-announced on every
          append (DESIGN.md § 7 and `docs/design/portal-exploration.md`
          § 7 — the live-regions requirement from #446). */}
      <CardContent
        role="log"
        aria-live="polite"
        aria-atomic="false"
        aria-relevant="additions"
        aria-label="Activity feed"
        className="space-y-2 overflow-y-auto"
        style={{ maxHeight }}
      >
        {items.map((item) => (
          <div key={item.id} className="flex items-start gap-2 text-sm">
            <span className="mt-1.5 shrink-0">
              <span
                aria-hidden="true"
                className={`inline-block h-2 w-2 rounded-full ${severityColors[item.severity] ?? "bg-muted-foreground"}`}
              />
              <span className="sr-only">{item.severity}: </span>
            </span>
            <div className="min-w-0 flex-1">
              <p>{item.summary}</p>
              <p className="text-xs text-muted-foreground">
                {item.source.scheme}://{item.source.path} &middot;{" "}
                {humanEventType(item.eventType)} &middot; {timeAgo(item.timestamp)}
              </p>
            </div>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}
