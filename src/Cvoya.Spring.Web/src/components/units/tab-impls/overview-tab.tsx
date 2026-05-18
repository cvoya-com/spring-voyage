"use client";

// Canonical Overview tab (#2258, design: docs/design/canonical-tabs.md
// § 5.1). Subject-agnostic component driven by a `{ kind, node }` pair —
// every Tenant, Unit, and Agent Overview tab renders through this body so
// the canonical structure (description / issues / stats / cost / link)
// lines up across subjects.
//
// Subject-specific affordances gate behind `node.kind`:
//   - Tenant: top-level units grid (`<UnitCard>` per child unit) or
//     empty state. No stat tiles or sparkline today — the canonical-tabs
//     design's tenant stat-tile row is a future scope item; tenant cost
//     today lives on Budgets (§ 4.1 variance).
//   - Unit:   ValidationPanel (Error only) + IssuesPanel + 5 stat tiles
//     + cost-over-time sparkline + expertise card + engagement link.
//   - Agent:  IssuesPanel + LifecyclePanel + cost summary card +
//     engagement link. Lifecycle on Unit is tracked under #2274 (the
//     panel is keyed strictly on `agentId`).

import { useState } from "react";
import Link from "next/link";
import {
  Activity,
  AtSign,
  Bot,
  CalendarClock,
  DollarSign,
  IdCard,
  Layers,
  MessagesSquare,
  ShieldCheck,
  TrendingDown,
  UserRound,
} from "lucide-react";

import { LifecyclePanel } from "@/components/agents/tab-impls/lifecycle-panel";
import { UnitCard } from "@/components/cards/unit-card";
import { StatCard } from "@/components/stat-card";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useAgentCost,
  useAgentIssues,
  useCurrentUser,
  useHuman,
  useUnit,
  useUnitCostTimeseries,
  useUnitExecution,
  useUnitIssues,
} from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

import {
  aggregate,
  type AgentNode,
  type HumanNode,
  type TenantNode,
  type TreeNode,
  type UnitNode,
} from "../aggregate";
import ValidationPanel from "../detail/validation-panel";
import { useExplorerSelection } from "../explorer-selection-context";
import { IssuesPanel } from "../issues-panel";
import { UnitOverviewExpertiseCard } from "../unit-overview-expertise-card";

/**
 * Subjects the unified Overview tab can be driven by. Mirrors the
 * `NodeKind` triplet — every subject in the Explorer tree has an
 * Overview slot (see `aggregate.ts` tab catalogs).
 */
export type OverviewSubjectKind = "Tenant" | "Unit" | "Agent" | "Human";

export interface OverviewTabProps {
  /** Subject kind — drives every affordance gate inside the body. */
  kind: OverviewSubjectKind;
  /**
   * The active tree node. Carried as the whole node (not just an id)
   * because two of the per-kind variants need the live tree data:
   * Tenant Overview renders a grid of children, and Unit Overview
   * computes its stat tiles by walking the subtree via `aggregate()`.
   */
  node: TreeNode;
}

// Window options for the unit Cost-over-time sparkline toggle. Tenant
// and Agent variants do not render this card (Agent has its own cost
// surface on the Activity tab; Tenant cost today lives on Budgets).
const UNIT_WINDOW_OPTIONS = [
  { label: "7d", window: "7d", bucket: "1d" },
  { label: "30d", window: "30d", bucket: "1d" },
] as const;

type UnitWindowOption = (typeof UNIT_WINDOW_OPTIONS)[number];

/**
 * Minimal inline sparkline (SVG polyline). Matches the activity-tab
 * `<CostSparkline>` and `<BudgetSparkline>` aesthetic — no new
 * charting library. Lifted here so the unit cost card stays
 * self-contained within the canonical Overview body.
 */
function UnitCostSparkline({
  points,
  testId,
}: {
  points: number[];
  testId: string;
}) {
  const max = Math.max(1, ...points);
  const width = 120;
  const height = 24;
  const step = points.length > 1 ? width / (points.length - 1) : 0;
  const svgPoints = points
    .map(
      (v, i) =>
        `${(i * step).toFixed(1)},${(height - (v / max) * height).toFixed(1)}`,
    )
    .join(" ");
  return (
    <svg
      aria-hidden="true"
      role="img"
      data-testid={testId}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="text-primary/70"
    >
      <polyline
        points={svgPoints}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/**
 * Tenant variant body — UnitCard grid of top-level units. Rendered as
 * a leaf component so the kind-guarded hooks in the Unit and Agent
 * branches stay outside the Tenant code path (and vice versa).
 */
function TenantOverviewBody({ tenant }: { tenant: TenantNode }) {
  const { dispatchSelect } = useExplorerSelection();
  const units: UnitNode[] = (tenant.children ?? []).filter(
    (c): c is UnitNode => c.kind === "Unit",
  );

  if (units.length === 0) {
    return (
      <div
        data-testid="tab-tenant-overview-empty"
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <Layers
          className="mx-auto h-6 w-6 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="mt-2 text-sm font-medium">No units yet</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Create a unit from the Dashboard or via{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring unit create
          </code>
          .
        </p>
      </div>
    );
  }

  return (
    <div
      className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
      data-testid="tab-tenant-overview"
    >
      {units.map((u) => {
        const roll = aggregate(u);
        return (
          <UnitCard
            key={u.id}
            unit={{
              name: u.id,
              displayName: u.name,
              registeredAt: new Date().toISOString(),
              status: mapTenantUnitStatus(u.status),
              cost: roll.cost,
            }}
            onSelect={(id) => dispatchSelect(id)}
            onOpenTab={(id) => dispatchSelect(id)}
          />
        );
      })}
    </div>
  );
}

// Tenant-side helper: the tree-level `NodeStatus` strings are
// lowercase ("running" / "paused" / …) but the shared `<UnitCard>`
// expects the API's `LifecycleStatus` casing. Kept inline because the
// tenant body is the only consumer; promoting this to a util would
// scatter the mapping across files.
function mapTenantUnitStatus(status: string): string {
  switch (status) {
    case "running":
      return "Running";
    case "starting":
      return "Starting";
    case "stopping":
      return "Stopping";
    case "validating":
      return "Validating";
    case "paused":
    case "stopped":
      return "Stopped";
    case "error":
      return "Error";
    case "draft":
    default:
      return "Draft";
  }
}

/**
 * Unit variant body — stat tiles + Cost-over-time card + expertise +
 * engagement link, plus the validation banner when the live unit is
 * in `Error`. Hooks run unconditionally inside this component because
 * it is only mounted when `kind === "Unit"`.
 */
function UnitOverviewBody({ unit }: { unit: UnitNode }) {
  const [windowOpt, setWindowOpt] = useState<UnitWindowOption>(
    UNIT_WINDOW_OPTIONS[0],
  );
  const timeseriesQuery = useUnitCostTimeseries(
    unit.id,
    windowOpt.window,
    windowOpt.bucket,
  );

  // #1665: pull the live unit envelope so we can surface the structured
  // `lastValidationError` when the unit has failed validation. The
  // tree's `node.status` is the aggregated worst-status, not the unit's
  // actual lifecycle state.
  //
  // #1787: poll while validating so the Status tile updates without a
  // manual refresh. The tenant-tree refresh used to be triggered here on
  // the Validating→terminal edge, but #2387 moved that invalidation into
  // the activity SSE handler so the tree updates regardless of which
  // surface drove the transition.
  const unitQuery = useUnit(unit.id, {
    refetchInterval: (query) =>
      query.state.data?.status === "Validating" ? 3000 : false,
  });
  const liveUnit = unitQuery.data ?? null;
  const executionQuery = useUnitExecution(unit.id);

  const roll = aggregate(unit);
  const sparklinePoints =
    timeseriesQuery.data?.points?.map((p) => p.costUsd) ?? [];

  return (
    <>
      {/*
       * #1665 / #2160: the unit's most recent validation error keeps
       * its dedicated panel when the unit is in `Error` (the panel
       * exposes the unique Edit-credential & retry affordance). The
       * broader operational-issues surface (#2160) lives below it and
       * renders whenever the unit OR its descendants have any open
       * issues.
       */}
      {liveUnit && liveUnit.status === "Error" && (
        <ValidationPanel
          unit={liveUnit}
          image={executionQuery.data?.image ?? null}
          runtime={executionQuery.data?.runtime ?? null}
          modelProvider={executionQuery.data?.model?.provider ?? null}
        />
      )}
      <UnitIssuesSection unitName={unit.name} />

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <StatCard
          label="Agents"
          value={roll.agents}
          icon={<Bot className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Sub-units"
          value={Math.max(0, roll.units - 1)}
          icon={<Layers className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Cost (24h)"
          value={formatCost(roll.cost)}
          icon={<DollarSign className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Messages (24h)"
          value={roll.msgs.toLocaleString()}
          icon={<MessagesSquare className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Status"
          value={liveUnit?.status?.toLowerCase() ?? roll.worst}
          icon={<Activity className="h-4 w-4" aria-hidden="true" />}
        />
      </div>
      <div className="text-xs text-muted-foreground">
        Stat tiles for agents, sub-units, cost, and messages are subtree
        roll-ups. Status reflects this unit&apos;s live lifecycle state.
        See the <Badge variant="outline">Agents</Badge> and{" "}
        <Badge variant="outline">Activity</Badge> tabs for drill-downs.
      </div>

      {/* Cost over time sparkline — #1363 */}
      <Card data-testid="unit-cost-timeseries-card">
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm">
            <TrendingDown className="h-4 w-4" aria-hidden="true" /> Cost over
            time
          </CardTitle>
          <div className="flex gap-1" role="group" aria-label="Time window">
            {UNIT_WINDOW_OPTIONS.map((opt) => (
              <button
                key={opt.label}
                onClick={() => setWindowOpt(opt)}
                className={`rounded-md px-2 py-0.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
                  windowOpt.label === opt.label
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-pressed={windowOpt.label === opt.label}
                data-testid={`unit-timeseries-window-${opt.label}`}
              >
                {opt.label}
              </button>
            ))}
          </div>
        </CardHeader>
        <CardContent>
          {timeseriesQuery.isLoading ? (
            <Skeleton
              className="h-8 w-full"
              data-testid="unit-cost-timeseries-loading"
            />
          ) : sparklinePoints.length === 0 ||
            sparklinePoints.every((v) => v === 0) ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="unit-cost-timeseries-empty"
            >
              No cost data for this window.
            </p>
          ) : (
            <div className="flex items-end gap-4">
              <UnitCostSparkline
                points={sparklinePoints}
                testId="unit-cost-sparkline"
              />
              <span className="text-xs text-muted-foreground tabular-nums">
                {formatCost(sparklinePoints.reduce((sum, v) => sum + v, 0))}{" "}
                total
              </span>
            </div>
          )}
        </CardContent>
      </Card>

      <UnitOverviewExpertiseCard unitId={unit.id} />

      {/* Cross-portal link to the engagement portal for this unit. Per
          ADR-0033 rule 6: cross-portal navigation is a standard anchor. */}
      <p
        className="text-xs text-muted-foreground"
        data-testid="unit-overview-engagement-link-row"
      >
        <Link
          href={`/engagement/mine?unit=${encodeURIComponent(unit.id)}`}
          className="text-primary hover:underline"
          data-testid="unit-overview-engagement-link"
        >
          View engagements for this unit
        </Link>{" "}
        in the Engagement portal.
      </p>
    </>
  );
}

/**
 * #2160: thin wrapper that fetches the unit's issues view and renders
 * the panel. Extracted so the Overview body stays declarative.
 */
function UnitIssuesSection({ unitName }: { unitName: string }) {
  const { data } = useUnitIssues(unitName, { includeDescendants: true });
  return <IssuesPanel view={data ?? null} subjectKind="unit" />;
}

/**
 * Agent variant body — issues panel + lifecycle embed + cost summary
 * card + engagement link. Hooks run unconditionally inside this
 * component because it is only mounted when `kind === "Agent"`.
 *
 * Lifecycle embed on Unit is deferred to #2274 — `<LifecyclePanel>`
 * is keyed strictly on `agentId` today.
 */
function AgentOverviewBody({ agent }: { agent: AgentNode }) {
  const costQuery = useAgentCost(agent.id);
  const issuesQuery = useAgentIssues(agent.id);
  const cost = costQuery.data ?? null;

  return (
    <>
      {/* #2160: open operational issues against this agent. */}
      <IssuesPanel view={issuesQuery.data ?? null} subjectKind="agent" />

      <LifecyclePanel agentId={agent.id} />

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <DollarSign className="h-4 w-4" aria-hidden="true" /> Cost summary
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          {cost === null ? (
            <p
              className="text-muted-foreground"
              data-testid="tab-agent-overview-cost-empty"
            >
              No cost data yet for this agent.
            </p>
          ) : (
            <>
              <Row label="Total" value={formatCost(cost.totalCost)} />
              <Row
                label="Input tokens"
                value={cost.totalInputTokens.toLocaleString()}
              />
              <Row
                label="Output tokens"
                value={cost.totalOutputTokens.toLocaleString()}
              />
              <Row label="Records" value={cost.recordCount.toString()} />
            </>
          )}
        </CardContent>
      </Card>

      {/* Cross-portal link to the engagement portal for this agent. Per
          ADR-0033 rule 6: cross-portal navigation is a standard anchor. */}
      <p
        className="text-xs text-muted-foreground"
        data-testid="agent-overview-engagement-link-row"
      >
        <Link
          href={`/engagement/mine?agent=${encodeURIComponent(agent.id)}`}
          className="text-primary hover:underline"
          data-testid="agent-overview-engagement-link"
        >
          View engagements for this agent
        </Link>{" "}
        in the Engagement portal.
      </p>
    </>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-medium">{value}</span>
    </div>
  );
}

/**
 * Human Overview body (#2267). Renders personal info — display name,
 * username, email, platform role, created-at — and a "You" hint when
 * the active human is the currently-authenticated caller (the OSS
 * single-operator case per `docs/concepts/humans.md`).
 *
 * No StatCard tiles, IssuesPanel, LifecyclePanel, cost summary, or
 * engagement link — humans don't have runtime, expertise, traces, or
 * cost (canonical-tabs.md § 4). The shape is intentionally minimal so
 * the slot keeps its "render something useful" promise without
 * overstating what a human has on the platform.
 *
 * Memberships are intentionally absent in this PR — neither
 * `GET /api/v1/tenant/humans/{id}/memberships` nor an aggregate over
 * the per-unit membership endpoints exists in v0.1. Follow-up issue
 * filed for v0.2 (Human memberships drill-down).
 */
function HumanOverviewBody({ human }: { human: HumanNode }) {
  const humanQuery = useHuman(human.id);
  const meQuery = useCurrentUser();

  if (humanQuery.isLoading) {
    return (
      <div className="space-y-3" data-testid="tab-human-overview-loading">
        <Skeleton className="h-6 w-48" />
        <Skeleton className="h-5 w-64" />
        <Skeleton className="h-5 w-40" />
      </div>
    );
  }

  // TanStack types `data` as `T | undefined` even when `queryFn`
  // resolves to `T | null`; collapse the two "no body" cases so the
  // body that follows can assume a concrete `HumanResponse`.
  const entity = humanQuery.data ?? null;
  if (entity === null) {
    return (
      <div
        data-testid="tab-human-overview-missing"
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <UserRound
          className="mx-auto h-6 w-6 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="mt-2 text-sm font-medium">Human not found</p>
        <p className="mt-1 text-xs text-muted-foreground">
          The human with id{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            {human.id}
          </code>{" "}
          could not be loaded. The record may have been deleted, or it
          may belong to a different tenant.
        </p>
      </div>
    );
  }

  // Match the currently-authenticated caller against this human's
  // UUID (not the display name / username — usernames can collide
  // across tenants, ids cannot). The "You" hint is cosmetic; in OSS
  // there's exactly one human and the hint is informative, in hosted
  // it disambiguates the operator's own row from teammates'.
  const isMe = meQuery.data?.id === entity.id;
  const createdAt = formatHumanCreatedAt(entity.createdAt);

  return (
    <div data-testid="tab-human-overview-body" className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <UserRound className="h-4 w-4" aria-hidden="true" /> Profile
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <div className="flex items-baseline justify-between gap-2">
            <div>
              <p className="text-xs text-muted-foreground">Display name</p>
              <p
                className="font-medium"
                data-testid="tab-human-overview-display-name"
              >
                {entity.displayName}
                {isMe ? (
                  <Badge
                    variant="outline"
                    className="ml-2 align-middle"
                    data-testid="tab-human-overview-you-hint"
                  >
                    You
                  </Badge>
                ) : null}
              </p>
            </div>
          </div>

          <Row label="Username" value={entity.username} />
          <Row
            label="Email"
            value={
              entity.email && entity.email.length > 0 ? entity.email : "—"
            }
          />
          <Row label="Platform role" value={entity.platformRole} />
          <Row label="Created" value={createdAt} />
        </CardContent>
      </Card>

      <div
        className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4"
        data-testid="tab-human-overview-facts"
      >
        <Fact
          icon={<IdCard className="h-4 w-4" aria-hidden="true" />}
          label="Username"
          value={entity.username}
        />
        <Fact
          icon={<AtSign className="h-4 w-4" aria-hidden="true" />}
          label="Email"
          value={
            entity.email && entity.email.length > 0 ? entity.email : "—"
          }
        />
        <Fact
          icon={<ShieldCheck className="h-4 w-4" aria-hidden="true" />}
          label="Platform role"
          value={entity.platformRole}
        />
        <Fact
          icon={<CalendarClock className="h-4 w-4" aria-hidden="true" />}
          label="Created"
          value={createdAt}
        />
      </div>

      <p className="text-xs text-muted-foreground">
        Humans participate in threads but do not have runtime, memory,
        skills, traces, or budgets. Per-unit team-role memberships and
        connector identities surface on dedicated v0.2 surfaces; see the
        Messages and Config tabs once they ship.
      </p>
    </div>
  );
}

/**
 * Compact icon + label + value pill used by the Human Overview body.
 * The StatCard component used by Tenant / Unit variants is keyed on
 * numeric values; the human surface is purely identity, so we keep a
 * smaller leaf-level helper local to the file.
 */
function Fact({
  icon,
  label,
  value,
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
}) {
  return (
    <div className="rounded-lg border border-border bg-card p-3">
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        {icon}
        {label}
      </p>
      <p className="mt-1 truncate text-sm font-medium" title={value}>
        {value}
      </p>
    </div>
  );
}

/**
 * Format the human's createdAt timestamp as `YYYY-MM-DD`. Locale-stable
 * (ISO date) so the rendered output is deterministic across test
 * environments and matches the API's wire-form precision (the row
 * carries an ISO-8601 string).
 */
function formatHumanCreatedAt(input: string): string {
  if (!input) return "—";
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(input);
  return match ? match[1] : input;
}

/**
 * Subject-agnostic Overview tab. Dispatches to the per-kind body so
 * the kind-specific hooks (`useUnit*`, `useAgent*`, the explorer
 * selection context) never mount for subjects they don't apply to.
 *
 * The description / desc-text is the only chrome the three subjects
 * share unconditionally; everything else lives inside the per-kind
 * body so the canonical layout reads consistently across subjects
 * without forcing every kind to mount every hook.
 */
export function OverviewTab({ kind, node }: OverviewTabProps) {
  // The container's vertical spacing scales with the variant: Tenant
  // is a single grid; Unit, Agent, and Human stack multiple cards.
  // Match the pre-unification spacing (`space-y-4` / `space-y-6`) so
  // the visual contract in DESIGN.md § 9.1 is unchanged.
  const spacing = kind === "Agent" ? "space-y-6" : "space-y-4";

  // Test-id matches the pre-unification shells for Unit and Agent so
  // existing E2E selectors keep working. Tenant has no shell-level
  // test-id — the body emits either `tab-tenant-overview` (grid) or
  // `tab-tenant-overview-empty` (no-units placeholder); a shell id
  // would shadow the empty-state assertion. Human gets its own shell
  // id (`tab-human-overview`) so the foundation PR's E2E + component
  // tests can target it without depending on the body's inner ids.
  const shellTestId =
    kind === "Unit"
      ? "tab-unit-overview"
      : kind === "Agent"
        ? "tab-agent-overview"
        : kind === "Human"
          ? "tab-human-overview"
          : undefined;

  return (
    <div className={spacing} data-testid={shellTestId}>
      {node.desc ? (
        <p className="text-sm text-muted-foreground">{node.desc}</p>
      ) : null}

      {kind === "Tenant" && node.kind === "Tenant" ? (
        <TenantOverviewBody tenant={node} />
      ) : null}

      {kind === "Unit" && node.kind === "Unit" ? (
        <UnitOverviewBody unit={node} />
      ) : null}

      {kind === "Agent" && node.kind === "Agent" ? (
        <AgentOverviewBody agent={node} />
      ) : null}

      {kind === "Human" && node.kind === "Human" ? (
        <HumanOverviewBody human={node} />
      ) : null}
    </div>
  );
}
