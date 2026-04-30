"use client";

// Cost area chart (#910). Renders a single-series area chart of tenant cost
// over time using recharts. Consumes `CostTimeseriesResponse["series"]`
// (each point: `{ t: ISO string, cost: number }`).
//
// Visual contract (DESIGN.md § 11.5 / § 12):
//   - Fill: `--color-voyage` (cyan) at 30 % opacity; stroke: `--color-voyage`.
//   - Axis labels: `--color-muted-foreground` (#a1a1aa) at `text-xs`.
//   - Grid: `--color-border` (#27272a) horizontal lines only, dashed.
//   - Tooltip: `bg-card border border-border shadow-sm rounded-md` — matches
//     the existing card chrome so it reads as part of the design system.
//   - No legend (single series — the title is the legend).
//   - Responsive: fills container width; fixed height of 160 px.

import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  type TooltipProps,
} from "recharts";
import type { CSSProperties } from "react";

export interface CostAreaPoint {
  /** ISO 8601 datetime string. */
  t: string;
  /** Cost in USD. */
  cost: number;
}

/** Format a cost value as a short string (e.g. "$1.23" or "$0.001"). */
function formatCostShort(v: number): string {
  if (v >= 1) return `$${v.toFixed(2)}`;
  if (v >= 0.001) return `$${v.toFixed(3)}`;
  return `$${v.toExponential(1)}`;
}

/** Format a time bucket label. Strips the date part when all points share
 *  the same day; otherwise shows "MMM D". */
function formatTick(iso: string): string {
  const d = new Date(iso);
  // toLocaleDateString gives locale-correct short label.
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

interface CostTooltipProps extends TooltipProps<number, string> {
  active?: boolean;
  payload?: Array<{ value: number }>;
  label?: string;
}

function CostTooltip({ active, payload, label }: CostTooltipProps) {
  if (!active || !payload?.length) return null;
  const cost = payload[0]?.value ?? 0;
  return (
    <div
      style={
        {
          background: "var(--color-card)",
          border: "1px solid var(--color-border)",
          borderRadius: "var(--radius-md, 6px)",
          padding: "8px 12px",
          fontSize: "0.75rem",
          color: "var(--color-foreground)",
          boxShadow: "var(--shadow-sm, 0 1px 3px rgba(0,0,0,.4))",
        } as CSSProperties
      }
    >
      <div style={{ color: "var(--color-muted-foreground)", marginBottom: 2 }}>
        {label ? formatTick(label) : ""}
      </div>
      <div style={{ fontWeight: 600 }}>{formatCostShort(cost)}</div>
    </div>
  );
}

export interface CostAreaChartProps {
  points: CostAreaPoint[];
  /** Chart height in px. Defaults to 160. */
  height?: number;
  /** aria-label for the chart container. */
  ariaLabel?: string;
}

/**
 * Single-series area chart for tenant / agent / unit cost over time.
 * Uses design-system colour tokens via CSS custom properties so dark-mode
 * and theming work without any JS branching.
 */
export function CostAreaChart({
  points,
  height = 160,
  ariaLabel = "Cost over time",
}: CostAreaChartProps) {
  if (points.length === 0) {
    return (
      <p
        className="py-4 text-center text-sm text-muted-foreground"
        data-testid="cost-area-chart-empty"
      >
        No cost data for this window.
      </p>
    );
  }

  // recharts needs plain objects with stable keys.
  const data = points.map((p) => ({ t: p.t, cost: p.cost }));

  return (
    <div
      aria-label={ariaLabel}
      role="img"
      data-testid="cost-area-chart"
      style={{ height }}
    >
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart
          data={data}
          margin={{ top: 4, right: 8, left: 0, bottom: 0 }}
        >
          <defs>
            <linearGradient id="costFill" x1="0" y1="0" x2="0" y2="1">
              <stop
                offset="5%"
                stopColor="var(--color-voyage)"
                stopOpacity={0.35}
              />
              <stop
                offset="95%"
                stopColor="var(--color-voyage)"
                stopOpacity={0.02}
              />
            </linearGradient>
          </defs>
          <CartesianGrid
            strokeDasharray="3 3"
            stroke="var(--color-border)"
            vertical={false}
          />
          <XAxis
            dataKey="t"
            tickFormatter={formatTick}
            tick={{
              fontSize: 10,
              fill: "var(--color-muted-foreground)",
            }}
            axisLine={false}
            tickLine={false}
            interval="preserveStartEnd"
          />
          <YAxis
            tickFormatter={formatCostShort}
            tick={{
              fontSize: 10,
              fill: "var(--color-muted-foreground)",
            }}
            axisLine={false}
            tickLine={false}
            width={52}
          />
          <Tooltip content={<CostTooltip />} cursor={{ stroke: "var(--color-border)" }} />
          <Area
            type="monotone"
            dataKey="cost"
            stroke="var(--color-voyage)"
            strokeWidth={2}
            fill="url(#costFill)"
            dot={false}
            activeDot={{
              r: 4,
              fill: "var(--color-voyage)",
              stroke: "var(--color-card)",
              strokeWidth: 2,
            }}
            name="Cost (USD)"
          />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}
