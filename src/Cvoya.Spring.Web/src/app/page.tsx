"use client";

/**
 * Dashboard (DASH-rewrite, umbrella #815 §6).
 *
 * Shape:
 *  1. Header — title + sub-caption ("N units · M agents · K connectors
 *     healthy"), trailing "Copy address" and "New unit" buttons.
 *  2. 4-stat grid — Units / Agents / Running / Cost · 24h.
 *  3. Two-column split — top-level units widget (left) and Activity
 *     (right). Each top-level `<UnitCard>` wires `onOpenTab(id, tab)`
 *     to `router.push("/units?node=<id>&tab=<Tab>")`. Header button
 *     "Open explorer →" pushes `/units`.
 *  4. Budget (24h) — `<CostSummaryCard>` full-width at the bottom.
 *
 * Data comes from `useTenantTree()` (for the top-level unit list),
 * `useDashboardSummary()` (for the stat tiles + recent activity), and
 * `useTenantCost()` (for the rolling 24h tile). SSE refreshes via the
 * activity stream keep every slice fresh.
 */

import { useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  Activity,
  Bot,
  Check,
  ChevronRight,
  Copy,
  Network,
  Plus,
  Zap,
} from "lucide-react";

import { StatCard } from "@/components/stat-card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { CostSummaryCard } from "@/components/cards/cost-summary-card";
import { UnitCard } from "@/components/cards/unit-card";
import type { CardTabName } from "@/components/cards/card-tab-row";
import {
  useConnectorTypes,
  useDashboardSummary,
  useTenantCost,
  useTenantTree,
} from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { DashboardSummary } from "@/lib/api/types";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";
import { formatCost, humanEventType, timeAgo } from "@/lib/utils";

/* ------------------------------------------------------------------ */
/* Helpers                                                             */
/* ------------------------------------------------------------------ */

/**
 * Map the wire-level tenant-tree status strings (lowercase) into the
 * display-status vocabulary `<UnitCard>` speaks (title-case). Mirrors
 * `components/units/tabs/tenant-overview.tsx` so cards on both surfaces
 * paint identical status dots.
 */
function mapStatus(status: string): string {
  switch (status) {
    case "running":
      return "Running";
    case "starting":
      return "Starting";
    case "paused":
    case "stopped":
      return "Stopped";
    case "error":
      return "Error";
    default:
      return "Draft";
  }
}

/**
 * Resolve the rolling 24h `(from, to)` pair for the Cost · 24h tile.
 * Pinned via `useState(() => …)` at the caller so millisecond drift
 * doesn't bust the TanStack cache key between renders — same pattern
 * `CostSummaryCard` uses for the today/7d/30d tiles.
 */
function resolve24hWindow(now: Date = new Date()): {
  from: string;
  to: string;
} {
  const to = now.toISOString();
  const from = new Date(now.getTime() - 24 * 60 * 60 * 1000).toISOString();
  return { from, to };
}

/* ------------------------------------------------------------------ */
/* Header                                                              */
/* ------------------------------------------------------------------ */

interface DashboardHeaderProps {
  /** "N units" — from the dashboard summary. */
  unitCount: number;
  /** "M agents". */
  agentCount: number;
  /** Connectors with a healthy install row. */
  connectorsHealthy: number;
  /** Address to copy — the tenant tree root's `id` when available. */
  tenantAddress: string | null;
}

function DashboardHeader({
  unitCount,
  agentCount,
  connectorsHealthy,
  tenantAddress,
}: DashboardHeaderProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = async () => {
    if (!tenantAddress) return;
    try {
      await navigator.clipboard.writeText(tenantAddress);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can fail on insecure contexts or when the user denies
      // permission. Swallow the error — the button title already says
      // what it does, and the caller can retry. No toast system in this
      // surface to dispatch into yet.
    }
  };

  return (
    <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <h1 className="text-2xl font-bold">Dashboard</h1>
        <p
          className="mt-1 text-sm text-muted-foreground"
          data-testid="dashboard-subcaption"
        >
          {unitCount} {unitCount === 1 ? "unit" : "units"} &middot;{" "}
          {agentCount} {agentCount === 1 ? "agent" : "agents"} &middot;{" "}
          {connectorsHealthy}{" "}
          {connectorsHealthy === 1 ? "connector" : "connectors"} healthy
        </p>
      </div>
      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          onClick={handleCopy}
          disabled={!tenantAddress}
          aria-label={
            tenantAddress
              ? `Copy tenant address ${tenantAddress}`
              : "Copy tenant address"
          }
          data-testid="dashboard-copy-address"
        >
          {copied ? (
            <Check className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" />
          ) : (
            <Copy className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" />
          )}
          {copied ? "Copied" : "Copy address"}
        </Button>
        <Link
          href="/units/create"
          className="inline-flex h-8 items-center justify-center rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          data-testid="dashboard-new-unit"
        >
          <Plus className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" />
          New unit
        </Link>
      </div>
    </header>
  );
}

/* ------------------------------------------------------------------ */
/* Top-level units widget                                              */
/* ------------------------------------------------------------------ */

interface TopLevelUnit {
  id: string;
  displayName: string;
  status: string;
  cost?: number;
}

/**
 * Extract the top-level units from a validated tenant tree. Filters
 * the tenant root's children to `kind === "Unit"` — nested units stay
 * inside the Explorer — so the widget stays focused on the "what's at
 * the top level today" question the dashboard answers.
 *
 * Intentionally thin: no pagination or overflow handling. Tenants with
 * many top-level units render a long grid; lazy-expansion / pagination
 * is tracked as a follow-up in `V21-tenant-tree-lazy` (umbrella #815
 * §3).
 */
function selectTopLevelUnits(
  tree: ValidatedTenantTreeNode | undefined,
): TopLevelUnit[] {
  if (!tree) return [];
  const children = tree.children ?? [];
  return children
    .filter((c) => c.kind === "Unit")
    .map((c) => ({
      id: c.id,
      displayName: c.name,
      status: mapStatus(c.status),
      cost: typeof c.cost24h === "number" ? c.cost24h : undefined,
    }));
}

interface TopLevelUnitsWidgetProps {
  units: TopLevelUnit[];
  onOpenExplorer: () => void;
  onOpenTab: (id: string, tab: CardTabName) => void;
}

function TopLevelUnitsWidget({
  units,
  onOpenExplorer,
  onOpenTab,
}: TopLevelUnitsWidgetProps) {
  return (
    <section
      aria-labelledby="top-level-units-heading"
      data-testid="top-level-units"
      role="region"
    >
      <div className="mb-3 flex items-center justify-between">
        <h2
          id="top-level-units-heading"
          className="flex items-center gap-2 text-lg font-semibold"
        >
          <Network className="h-5 w-5" aria-hidden="true" />
          Top-level units
        </h2>
        <Button
          variant="ghost"
          size="sm"
          onClick={onOpenExplorer}
          data-testid="open-explorer-button"
        >
          Open explorer
          <ChevronRight
            className="ml-0.5 h-3.5 w-3.5"
            aria-hidden="true"
          />
        </Button>
      </div>
      {units.length === 0 ? (
        <Card className="flex flex-col items-center justify-center p-8 text-center">
          <Plus
            className="mb-3 h-10 w-10 text-muted-foreground"
            aria-hidden="true"
          />
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
      ) : (
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2"
          data-testid="top-level-units-grid"
        >
          {units.map((u) => (
            <UnitCard
              key={u.id}
              unit={{
                name: u.id,
                displayName: u.displayName,
                registeredAt: new Date().toISOString(),
                status: u.status,
                cost: u.cost ?? null,
              }}
              onOpenTab={onOpenTab}
            />
          ))}
        </div>
      )}
    </section>
  );
}

/* ------------------------------------------------------------------ */
/* Activity card                                                       */
/* ------------------------------------------------------------------ */

function ActivityCard({ summary }: { summary: DashboardSummary }) {
  const items = summary.recentActivity.slice(0, 10);
  return (
    <section
      aria-labelledby="dashboard-activity-heading"
      data-testid="dashboard-activity"
      role="region"
    >
      <div className="mb-3 flex items-center justify-between">
        <h2
          id="dashboard-activity-heading"
          className="flex items-center gap-2 text-lg font-semibold"
        >
          <Activity className="h-5 w-5" aria-hidden="true" />
          Activity
        </h2>
        {items.length > 0 && (
          <Link
            href="/activity"
            className="text-sm text-primary hover:underline"
          >
            View all
          </Link>
        )}
      </div>
      {items.length === 0 ? (
        <Card className="p-6 text-center">
          <Activity
            className="mx-auto mb-3 h-10 w-10 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="text-sm text-muted-foreground">
            Start a unit to see activity here.
          </p>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <ul className="divide-y">
              {items.map((item) => {
                const sourceName = item.source.replace(
                  /^(agent|unit):\/\//,
                  "",
                );
                const isAgent = item.source.startsWith("agent://");
                return (
                  <li
                    key={item.id}
                    className="flex items-start gap-3 px-4 py-3"
                    data-testid={`activity-item-${item.id}`}
                  >
                    <span className="mt-1 shrink-0">
                      {isAgent ? (
                        <Bot
                          className="h-4 w-4 text-muted-foreground"
                          aria-hidden="true"
                        />
                      ) : (
                        <Network
                          className="h-4 w-4 text-muted-foreground"
                          aria-hidden="true"
                        />
                      )}
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="text-sm">{item.summary}</p>
                      <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                        <Badge
                          variant={isAgent ? "secondary" : "default"}
                          className="text-[10px] px-1.5 py-0"
                        >
                          {sourceName}
                        </Badge>
                        <span>{humanEventType(item.eventType)}</span>
                        <span>{timeAgo(item.timestamp)}</span>
                      </div>
                    </div>
                  </li>
                );
              })}
            </ul>
          </CardContent>
        </Card>
      )}
    </section>
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
          <Skeleton className="h-8 w-40" />
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
      <Skeleton className="h-32" />
    </div>
  );
}

/* ------------------------------------------------------------------ */
/* Dashboard page                                                      */
/* ------------------------------------------------------------------ */

export default function DashboardPage() {
  const router = useRouter();

  // Pin the 24h window at mount so the tenant-cost cache key doesn't
  // drift on every render. The activity stream invalidates the
  // dashboard slice (see `queryKeysAffectedBySource`) so fresh costs
  // still land without re-resolving the window.
  const [costWindow] = useState(() => resolve24hWindow());

  const { data: summary, isPending: summaryPending } = useDashboardSummary();
  const { data: tree } = useTenantTree();
  const { data: cost24h } = useTenantCost(costWindow);
  const { data: connectors } = useConnectorTypes();

  // Subscribe to the activity stream — side-effect is cache
  // invalidation inside the hook (`queryKeysAffectedBySource`).
  useActivityStream();

  const topLevelUnits = useMemo(() => selectTopLevelUnits(tree), [tree]);

  if (summaryPending) {
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

  const running = summary.unitsByStatus["Running"] ?? 0;
  // Connectors with a tenant-install row are "healthy" for the header
  // caption — `/api/v1/connectors` only returns installed rows post-#714,
  // so a row's presence is itself the signal. A richer credential-health
  // check ships separately (`useConnectorCredentialHealth`) and is not
  // load-bearing for this caption.
  const connectorsHealthy = connectors?.length ?? 0;

  const handleOpenTab = (unitId: string, tab: CardTabName) => {
    router.push(
      `/units?node=${encodeURIComponent(unitId)}&tab=${encodeURIComponent(tab)}`,
    );
  };

  const handleOpenExplorer = () => {
    router.push("/units");
  };

  return (
    <div className="space-y-6">
      <DashboardHeader
        unitCount={summary.unitCount}
        agentCount={summary.agentCount}
        connectorsHealthy={connectorsHealthy}
        tenantAddress={tree?.id ?? null}
      />

      {/* 4-stat grid */}
      <div
        className="grid grid-cols-2 gap-4 sm:grid-cols-4"
        data-testid="stats-header"
      >
        <StatCard
          label="Units"
          value={summary.unitCount}
          icon={<Network className="h-5 w-5" aria-hidden="true" />}
        />
        <StatCard
          label="Agents"
          value={summary.agentCount}
          icon={<Bot className="h-5 w-5" aria-hidden="true" />}
        />
        <StatCard
          label="Running"
          value={running}
          icon={<Zap className="h-5 w-5" aria-hidden="true" />}
        />
        <StatCard
          label="Cost · 24h"
          value={formatCost(cost24h?.totalCost ?? 0)}
          icon={<Activity className="h-5 w-5" aria-hidden="true" />}
        />
      </div>

      {/* Two-column split: Top-level units (left, spans 2 on lg) +
          Activity (right). The top-level-units widget covers ~2/3 of
          the row so the grid reads at `sm:grid-cols-2` without the
          activity feed squeezing the unit cards. */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <TopLevelUnitsWidget
            units={topLevelUnits}
            onOpenExplorer={handleOpenExplorer}
            onOpenTab={handleOpenTab}
          />
        </div>
        <ActivityCard summary={summary} />
      </div>

      {/* Budget (24h) card — full-width at the bottom. The card reads
          from `useTenantCost(today/7d/30d)` under the hood and links
          to `/analytics/costs` for budget editing. */}
      <CostSummaryCard />
    </div>
  );
}
