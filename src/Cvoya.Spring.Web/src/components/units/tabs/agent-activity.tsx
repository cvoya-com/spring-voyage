"use client";

// Agent Activity tab (EXP-tab-agent-activity, umbrella #815 §4).
//
// Mirrors the unit activity tab's filter/render pipeline but scopes
// queries to `agent:<id>` so the stream + REST baseline only surface
// events produced by this agent. Tiny reimplementation (rather than
// adapter) because the legacy unit ActivityTab only accepts `unitId`.

import { Activity, RefreshCw } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { useActivityQuery } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { ActivitySeverity } from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

const severityVariant: Record<
  ActivitySeverity,
  "default" | "success" | "warning" | "destructive"
> = {
  Debug: "default",
  Info: "success",
  Warning: "warning",
  Error: "destructive",
};

function AgentActivityTab({ node }: TabContentProps) {
  // Hooks run unconditionally — the registry guarantees `kind === "Agent"`.
  const agentId = node.id;
  const queryParams = { source: `agent:${agentId}`, pageSize: "20" };
  const {
    data: result,
    error,
    isLoading,
    isFetching,
    refetch,
  } = useActivityQuery(queryParams);

  useActivityStream({
    filter: (event) =>
      event.source.scheme === "agent" && event.source.path === agentId,
  });

  if (node.kind !== "Agent") return null;

  const errorMessage =
    error instanceof Error ? error.message : error ? String(error) : null;
  const events = result?.items ?? [];

  return (
    <Card data-testid="tab-agent-activity">
      <CardHeader className="flex flex-row items-center justify-between space-y-0">
        <CardTitle className="flex items-center gap-2">
          <Activity className="h-4 w-4" aria-hidden="true" /> Activity
        </CardTitle>
        <Button
          variant="outline"
          size="sm"
          onClick={() => refetch()}
          disabled={isFetching}
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${isFetching ? "animate-spin" : ""}`}
            aria-hidden="true"
          />
          Refresh
        </Button>
      </CardHeader>
      <CardContent>
        {errorMessage ? (
          <p
            role="alert"
            className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          >
            {errorMessage}
          </p>
        ) : isLoading ? (
          <p
            role="status"
            aria-live="polite"
            className="text-sm text-muted-foreground"
          >
            Loading activity…
          </p>
        ) : events.length === 0 ? (
          <p
            className="text-sm text-muted-foreground"
            data-testid="tab-agent-activity-empty"
          >
            No activity for this agent yet.
          </p>
        ) : (
          <ul className="divide-y divide-border text-sm">
            {events.map((event) => (
              <li
                key={event.id}
                className="flex items-start gap-3 py-2"
              >
                <Badge
                  variant={
                    severityVariant[event.severity as ActivitySeverity] ??
                    "default"
                  }
                  className="shrink-0"
                >
                  {event.severity}
                </Badge>
                <span className="min-w-0 flex-1 truncate">
                  {event.summary}
                </span>
                <span className="shrink-0 text-xs text-muted-foreground">
                  {timeAgo(event.timestamp)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

registerTab("Agent", "Activity", AgentActivityTab);

export default AgentActivityTab;
