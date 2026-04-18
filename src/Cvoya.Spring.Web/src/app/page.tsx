"use client";

import Link from "next/link";
import {
  Activity,
  AlertCircle,
  Bot,
  CheckCircle2,
  DollarSign,
  Network,
  Plus,
} from "lucide-react";
import { useDashboardSummary } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { DashboardSummary } from "@/lib/api/types";
import { formatCost, timeAgo } from "@/lib/utils";
import { AgentCard } from "@/components/cards/agent-card";
import { CostSummaryCard } from "@/components/cards/cost-summary-card";
import { UnitCard } from "@/components/cards/unit-card";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/* ------------------------------------------------------------------ */
/* Status helpers                                                      */
/* ------------------------------------------------------------------ */

const severityColor: Record<string, string> = {
  Debug: "text-muted-foreground",
  Info: "text-blue-500",
  Warning: "text-amber-500",
  Error: "text-red-500",
};

const severityDot: Record<string, string> = {
  Debug: "bg-muted-foreground",
  Info: "bg-blue-500",
  Warning: "bg-amber-500",
  Error: "bg-red-500",
};

/* ------------------------------------------------------------------ */
/* Health indicator                                                    */
/* ------------------------------------------------------------------ */

function computeHealth(
  unitsByStatus: Record<string, number>,
): "healthy" | "degraded" | "none" {
  const total = Object.values(unitsByStatus).reduce((s, n) => s + n, 0);
  if (total === 0) return "none";
  if ((unitsByStatus["Error"] ?? 0) > 0) return "degraded";
  return "healthy";
}

/* ------------------------------------------------------------------ */
/* Stats header                                                        */
/* ------------------------------------------------------------------ */

function StatsHeader({ summary }: { summary: DashboardSummary }) {
  const health = computeHealth(summary.unitsByStatus);
  const running = summary.unitsByStatus["Running"] ?? 0;
  const stopped =
    (summary.unitsByStatus["Stopped"] ?? 0) +
    (summary.unitsByStatus["Draft"] ?? 0);
  const errored = summary.unitsByStatus["Error"] ?? 0;

  return (
    <div
      className="grid grid-cols-2 gap-4 sm:grid-cols-4"
      data-testid="stats-header"
    >
      {/* Units stat */}
      <Card>
        <CardContent className="p-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs text-muted-foreground">Units</p>
              <p className="text-2xl font-bold">{summary.unitCount}</p>
              <div className="mt-1 flex flex-wrap gap-1">
                {running > 0 && (
                  <Badge variant="success" data-testid="units-running-badge">
                    {running} running
                  </Badge>
                )}
                {stopped > 0 && (
                  <Badge variant="secondary" data-testid="units-stopped-badge">
                    {stopped} stopped
                  </Badge>
                )}
                {errored > 0 && (
                  <Badge
                    variant="destructive"
                    data-testid="units-error-badge"
                  >
                    {errored} error
                  </Badge>
                )}
              </div>
            </div>
            <Network className="h-5 w-5 text-muted-foreground" />
          </div>
        </CardContent>
      </Card>

      {/* Agents stat */}
      <Card>
        <CardContent className="p-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs text-muted-foreground">Agents</p>
              <p className="text-2xl font-bold">{summary.agentCount}</p>
            </div>
            <Bot className="h-5 w-5 text-muted-foreground" />
          </div>
        </CardContent>
      </Card>

      {/* Cost stat */}
      <Card>
        <CardContent className="p-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs text-muted-foreground">Total Cost</p>
              <p className="text-2xl font-bold">
                {formatCost(summary.totalCost)}
              </p>
            </div>
            <DollarSign className="h-5 w-5 text-muted-foreground" />
          </div>
        </CardContent>
      </Card>

      {/* Health stat */}
      <Card>
        <CardContent className="p-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs text-muted-foreground">System Health</p>
              <div className="mt-1 flex items-center gap-2">
                {health === "healthy" && (
                  <>
                    <CheckCircle2
                      className="h-5 w-5 text-green-500"
                      data-testid="health-icon"
                    />
                    <span
                      className="text-sm font-medium text-green-600"
                      data-testid="health-label"
                    >
                      Healthy
                    </span>
                  </>
                )}
                {health === "degraded" && (
                  <>
                    <AlertCircle
                      className="h-5 w-5 text-amber-500"
                      data-testid="health-icon"
                    />
                    <span
                      className="text-sm font-medium text-amber-600"
                      data-testid="health-label"
                    >
                      Degraded
                    </span>
                  </>
                )}
                {health === "none" && (
                  <span
                    className="text-sm text-muted-foreground"
                    data-testid="health-label"
                  >
                    No units
                  </span>
                )}
              </div>
            </div>
            <Activity className="h-5 w-5 text-muted-foreground" />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/* Unit cards                                                          */
/* ------------------------------------------------------------------ */

function UnitCards({ summary }: { summary: DashboardSummary }) {
  if (summary.units.length === 0) {
    return (
      <Card className="flex flex-col items-center justify-center p-8 text-center">
        <Plus className="mb-3 h-10 w-10 text-muted-foreground" />
        <p className="mb-2 font-medium">Create your first unit</p>
        <p className="mb-4 text-sm text-muted-foreground">
          Units organize agents and define how they collaborate.
        </p>
        <Link
          href="/units/create"
          className="inline-flex items-center gap-1 rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90"
          data-testid="create-unit-cta"
        >
          Create your first unit
        </Link>
      </Card>
    );
  }

  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
      {summary.units.map((unit) => (
        <UnitCard key={unit.name} unit={unit} />
      ))}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/* Agent cards                                                         */
/* ------------------------------------------------------------------ */

function AgentCards({ summary }: { summary: DashboardSummary }) {
  if (summary.agents.length === 0) {
    return (
      <Card className="p-6 text-center">
        <Bot className="mx-auto mb-3 h-10 w-10 text-muted-foreground" />
        <p className="text-sm text-muted-foreground">
          Agents appear when you create a unit from a template.
        </p>
      </Card>
    );
  }

  // Build a quick lookup: agent name -> latest activity summary.
  const agentActivity: Record<string, string> = {};
  for (const item of summary.recentActivity) {
    // source is like "agent://agent-1" or "unit://unit-alpha"
    if (item.source.startsWith("agent://")) {
      const agentPath = item.source.replace("agent://", "");
      if (!agentActivity[agentPath]) {
        agentActivity[agentPath] = item.summary;
      }
    }
  }

  // Agent -> parent unit mapping based on the "unitName/agentName" naming
  // convention used for nested agents.
  const agentUnit: Record<string, string> = {};
  const unitNames = new Set(summary.units.map((u) => u.name));
  for (const agent of summary.agents) {
    const slashIdx = agent.name.lastIndexOf("/");
    if (slashIdx > 0) {
      const parentPath = agent.name.substring(0, slashIdx);
      if (unitNames.has(parentPath)) {
        agentUnit[agent.name] = parentPath;
      }
    }
  }

  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
      {summary.agents.map((agent) => (
        <AgentCard
          key={agent.name}
          agent={agent}
          parentUnit={agentUnit[agent.name] ?? null}
          lastActivity={agentActivity[agent.name] ?? null}
        />
      ))}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/* Activity feed                                                       */
/* ------------------------------------------------------------------ */

// Map a `scheme://path` source string onto the matching detail route.
// Returns null when the scheme doesn't have a portal page yet (so the
// caller can render the source as plain text rather than an unreachable
// link). Mirrors the cross-link rules in
// `docs/design/portal-exploration.md` § 3.3.
function sourceHref(source: string): string | null {
  const m = source.match(/^([a-z]+):\/\/(.+)$/i);
  if (!m) return null;
  const [, scheme, path] = m;
  switch (scheme.toLowerCase()) {
    case "agent":
      return `/agents/${encodeURIComponent(path)}`;
    case "unit":
      return `/units/${encodeURIComponent(path)}`;
    default:
      return null;
  }
}

function ActivityTimeline({
  summary,
}: {
  summary: DashboardSummary;
}) {
  const items = summary.recentActivity.slice(0, 10);

  if (items.length === 0) {
    return (
      <Card className="p-6 text-center">
        <Activity className="mx-auto mb-3 h-10 w-10 text-muted-foreground" />
        <p className="text-sm text-muted-foreground">
          Start a unit to see activity here.
        </p>
      </Card>
    );
  }

  return (
    <Card>
      <CardContent className="p-0">
        <div className="divide-y">
          {items.map((item) => {
            const isAgent = item.source.startsWith("agent://");
            const sourceName = item.source.replace(
              /^(agent|unit):\/\//,
              "",
            );
            const sourceLink = sourceHref(item.source);
            const conversationHref = item.correlationId
              ? `/conversations/${encodeURIComponent(item.correlationId)}`
              : null;
            // Each row deep-links to the most specific destination available:
            //   1. The conversation thread when we have a correlationId
            //      (every Message* / Conversation* event).
            //   2. The source agent or unit otherwise.
            //   3. Plain text when neither is reachable.
            // Per portal-exploration.md § 3.3 — no orphan rows.
            const rowHref = conversationHref ?? sourceLink;
            const rowContent = (
              <>
                <span className="mt-1.5 shrink-0">
                  <span
                    className={`inline-block h-2 w-2 rounded-full ${severityDot[item.severity] ?? "bg-muted-foreground"}`}
                  />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="text-sm">{item.summary}</p>
                  <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <Badge
                      variant={isAgent ? "secondary" : "default"}
                      className="text-[10px] px-1.5 py-0"
                    >
                      {isAgent ? (
                        <Bot className="mr-0.5 inline h-2.5 w-2.5" />
                      ) : (
                        <Network className="mr-0.5 inline h-2.5 w-2.5" />
                      )}
                      {sourceName}
                    </Badge>
                    <span
                      className={severityColor[item.severity] ?? "text-muted-foreground"}
                    >
                      {item.eventType}
                    </span>
                    <span>{timeAgo(item.timestamp)}</span>
                    {conversationHref && (
                      <Badge
                        variant="outline"
                        className="text-[10px] px-1.5 py-0"
                      >
                        conversation
                      </Badge>
                    )}
                  </div>
                </div>
              </>
            );

            return rowHref ? (
              <Link
                key={item.id}
                href={rowHref}
                className="flex items-start gap-3 px-4 py-3 hover:bg-accent/50 transition-colors"
                data-testid={`activity-item-${item.id}`}
              >
                {rowContent}
              </Link>
            ) : (
              <div
                key={item.id}
                className="flex items-start gap-3 px-4 py-3"
                data-testid={`activity-item-${item.id}`}
              >
                {rowContent}
              </div>
            );
          })}
        </div>
      </CardContent>
    </Card>
  );
}

/* ------------------------------------------------------------------ */
/* Loading skeleton                                                    */
/* ------------------------------------------------------------------ */

function DashboardSkeleton() {
  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Dashboard</h1>
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Skeleton className="h-24" />
        <Skeleton className="h-24" />
        <Skeleton className="h-24" />
        <Skeleton className="h-24" />
      </div>
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <div className="lg:col-span-2 space-y-4">
          <Skeleton className="h-8 w-32" />
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
          </div>
        </div>
        <div className="space-y-4">
          <Skeleton className="h-8 w-32" />
          <Skeleton className="h-64" />
        </div>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/* Dashboard page                                                      */
/* ------------------------------------------------------------------ */

export default function DashboardPage() {
  // Replaces the legacy `useEffect` + `setInterval(10s)` loop (#438).
  // The dashboard now stays fresh via two paths:
  //   1. SSE activity stream — every new event invalidates the
  //      dashboard cache slice (see queryKeysAffectedBySource).
  //   2. TanStack Query defaults — a 30s staleTime plus the cache
  //      invalidation above means data is never older than one event
  //      or one minute, whichever comes first.
  const { data: summary, isPending } = useDashboardSummary();
  // Subscribe to the platform's activity stream; side-effect is cache
  // invalidation inside the hook. We don't need the local `events`
  // array here — the stream's job is to trigger refetches.
  useActivityStream();

  if (isPending) {
    return <DashboardSkeleton />;
  }

  if (!summary) {
    return (
      <div className="space-y-6">
        <h1 className="text-2xl font-bold">Dashboard</h1>
        <Card>
          <CardContent className="p-6 text-center text-muted-foreground">
            Failed to load dashboard data. Please try refreshing.
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Dashboard</h1>

      {/* Stats header */}
      <StatsHeader summary={summary} />

      {/* Spend summary (PR-R4 / #394). Sits near the top so tenants
          see today / 7d / 30d totals without drilling into
          /analytics/costs. The card is read-only — budget editing
          lives on /analytics/costs per the PR-R4 scope note. */}
      <CostSummaryCard />

      {/* Main content: 3-column on large screens */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        {/* Units section — spans 1 col on lg */}
        <section>
          <div className="mb-3 flex items-center justify-between">
            <h2 className="flex items-center gap-2 text-lg font-semibold">
              <Network className="h-5 w-5" />
              Units
            </h2>
            {summary.unitCount > 0 && (
              <Link
                href="/units"
                className="text-sm text-primary hover:underline"
              >
                View all
              </Link>
            )}
          </div>
          <UnitCards summary={summary} />
        </section>

        {/* Agents section — spans 1 col on lg */}
        <section>
          <div className="mb-3 flex items-center justify-between">
            <h2 className="flex items-center gap-2 text-lg font-semibold">
              <Bot className="h-5 w-5" />
              Agents
            </h2>
          </div>
          <AgentCards summary={summary} />
        </section>

        {/* Activity section — spans 1 col on lg */}
        <section>
          <div className="mb-3 flex items-center justify-between">
            <h2 className="flex items-center gap-2 text-lg font-semibold">
              <Activity className="h-5 w-5" />
              Recent Activity
            </h2>
            {summary.recentActivity.length > 0 && (
              <Link
                href="/activity"
                className="text-sm text-primary hover:underline"
              >
                View all
              </Link>
            )}
          </div>
          <ActivityTimeline summary={summary} />
        </section>
      </div>
    </div>
  );
}
