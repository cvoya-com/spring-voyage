"use client";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import type { UnitDashboardSummary, UnitStatus } from "@/lib/api/types";
// Some dashboard payloads (see DashboardUnit) ship status as a raw
// string until the OpenAPI regeneration lands; accept either form so
// callers don't have to narrow.
type UnitStatusInput = UnitStatus | string | null | undefined;
import { cn, formatCost, timeAgo } from "@/lib/utils";
import {
  Activity,
  Clock,
  DollarSign,
  ExternalLink,
  ShieldCheck,
  Trash2,
} from "lucide-react";
import Link from "next/link";
import type { MouseEvent } from "react";

/**
 * Minimal shape the UnitCard needs. `UnitDashboardSummary` satisfies this
 * today; the interface lets callers pass richer unit records once the API
 * exposes them (activity, cost), without forcing a schema change here.
 */
export interface UnitCardUnit {
  name: string;
  displayName: string;
  registeredAt: string;
  status?: UnitStatusInput;
  /**
   * Optional cost-to-date for this unit in USD. Rendered as a small badge
   * beside the status when present.
   */
  cost?: number | null;
  /**
   * Optional last-N-buckets activity series (e.g. message counts per
   * 5-minute window). Rendered as a minimal sparkline when provided.
   */
  activitySeries?: number[];
}

interface UnitCardInput {
  name: string;
  displayName: string;
  registeredAt: string;
  status?: UnitStatusInput;
  cost?: number | null;
  activitySeries?: number[];
}

interface UnitCardProps {
  unit: UnitCardInput | UnitCardUnit | UnitDashboardSummary;
  onDelete?: (unit: UnitCardUnit) => void;
  className?: string;
}

const statusVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  Draft: "outline",
  Stopped: "secondary",
  Starting: "default",
  Running: "success",
  Stopping: "warning",
  Error: "destructive",
};

const statusDot: Record<string, string> = {
  Draft: "bg-muted-foreground",
  Stopped: "bg-muted-foreground",
  Starting: "bg-yellow-500",
  Running: "bg-green-500",
  Stopping: "bg-yellow-500",
  Error: "bg-red-500",
};

/**
 * Reusable unit card primitive. See `docs/design/portal-exploration.md`
 * § 3.3: every unit card links to its activity, costs, and policies, and
 * exposes a primary "open" affordance.
 */
export function UnitCard({ unit, onDelete, className }: UnitCardProps) {
  const status = unit.status ?? "Draft";
  const href = `/units/${encodeURIComponent(unit.name)}`;
  const activityHref = `${href}?tab=activity`;
  const costsHref = `${href}?tab=costs`;
  const policiesHref = `${href}?tab=policies`;
  const cost =
    "cost" in unit && typeof unit.cost === "number" ? unit.cost : null;
  const activitySeries =
    "activitySeries" in unit && Array.isArray(unit.activitySeries)
      ? unit.activitySeries
      : undefined;

  const handleDelete = (e: MouseEvent<HTMLButtonElement>) => {
    e.preventDefault();
    e.stopPropagation();
    onDelete?.({
      name: unit.name,
      displayName: unit.displayName,
      registeredAt: unit.registeredAt,
      status: unit.status as UnitStatus | null | undefined,
      cost,
      activitySeries,
    });
  };

  return (
    <Card
      data-testid={`unit-card-${unit.name}`}
      className={cn(
        "relative h-full transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2",
        className,
      )}
    >
      <CardContent className="p-4">
        {/*
          Full-card overlay link (#593). The primary card link expands to
          cover the whole card via the `::after` pseudo-element so every
          unused surface area navigates to the unit detail page on click.
          Interactive descendants (cross-link buttons, the delete button,
          the footer "Open" link) are promoted to `relative z-[1]` so they
          sit above the overlay and keep their own click targets. Tab
          focus lands on this link first; Enter activates it.
        */}
        <Link
          href={href}
          aria-label={`Open unit ${unit.displayName}`}
          data-testid={`unit-card-link-${unit.name}`}
          className="flex items-start justify-between gap-2 rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span
                aria-hidden="true"
                className={cn(
                  "inline-block h-2.5 w-2.5 shrink-0 rounded-full",
                  statusDot[status] ?? "bg-muted-foreground",
                )}
                data-testid={`unit-status-dot-${unit.name}`}
              />
              <h3 className="truncate font-semibold">{unit.displayName}</h3>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">{unit.name}</p>
          </div>
          <div className="flex shrink-0 items-center gap-1">
            <Badge variant={statusVariant[status] ?? "outline"}>{status}</Badge>
            {cost !== null && (
              <Badge
                variant="outline"
                className="gap-1"
                data-testid="unit-cost-badge"
                title="Cost to date"
              >
                <DollarSign className="h-3 w-3" aria-hidden="true" />
                {formatCost(cost)}
              </Badge>
            )}
          </div>
        </Link>

        <div className="mt-3 flex items-center gap-3 text-xs text-muted-foreground">
          <span className="flex items-center gap-1">
            <Clock className="h-3 w-3" aria-hidden="true" />
            {timeAgo(unit.registeredAt)}
          </span>
          <UnitSparkline series={activitySeries} />
        </div>

        <div className="relative z-[1] mt-3 flex items-center justify-between">
          {/* Cross-links: activity, costs, policies (see § 3.3) */}
          <div className="flex items-center gap-1">
            <CrossLinkButton
              href={activityHref}
              label={`View activity for ${unit.displayName}`}
              icon={<Activity className="h-3.5 w-3.5" />}
              testId={`unit-link-activity-${unit.name}`}
            />
            <CrossLinkButton
              href={costsHref}
              label={`View costs for ${unit.displayName}`}
              icon={<DollarSign className="h-3.5 w-3.5" />}
              testId={`unit-link-costs-${unit.name}`}
            />
            <CrossLinkButton
              href={policiesHref}
              label={`View policies for ${unit.displayName}`}
              icon={<ShieldCheck className="h-3.5 w-3.5" />}
              testId={`unit-link-policies-${unit.name}`}
            />
          </div>
          <div className="flex items-center gap-1">
            <Link
              href={href}
              className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
              data-testid={`unit-open-${unit.name}`}
            >
              Open
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </Link>
            {onDelete && (
              <Button
                variant="ghost"
                size="icon"
                onClick={handleDelete}
                aria-label={`Delete ${unit.displayName}`}
                data-testid={`unit-delete-${unit.name}`}
                className="h-7 w-7"
              >
                <Trash2 className="h-3.5 w-3.5 text-destructive" aria-hidden="true" />
              </Button>
            )}
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

function CrossLinkButton({
  href,
  label,
  icon,
  testId,
}: {
  href: string;
  label: string;
  icon: React.ReactNode;
  testId: string;
}) {
  return (
    <Link
      href={href}
      aria-label={label}
      title={label}
      data-testid={testId}
      className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
    >
      {icon}
    </Link>
  );
}

/**
 * Minimal inline sparkline (SVG polyline) for a small activity preview.
 * When no series is provided renders a muted placeholder so the layout
 * does not shift once data is wired. The rendered SVG is purely
 * decorative — unit-level screen readers already have the activity
 * cross-link above.
 */
function UnitSparkline({ series }: { series?: number[] }) {
  if (!series || series.length === 0) {
    return (
      <span
        aria-hidden="true"
        data-testid="unit-sparkline-placeholder"
        className="inline-block h-3 w-16 rounded-sm bg-muted"
      />
    );
  }
  const max = Math.max(1, ...series);
  const width = 64;
  const height = 12;
  const step = series.length > 1 ? width / (series.length - 1) : 0;
  const points = series
    .map((v, i) => `${(i * step).toFixed(1)},${(height - (v / max) * height).toFixed(1)}`)
    .join(" ");
  return (
    <svg
      aria-hidden="true"
      role="img"
      data-testid="unit-sparkline"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="text-primary/70"
    >
      <polyline
        points={points}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
