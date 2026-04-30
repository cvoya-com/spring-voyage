"use client";

// Throughput stacked-bar chart (#910). Renders a horizontal stacked-bar chart
// showing messages-received / messages-sent / turns / tool-calls per source,
// sorted descending by total. Uses recharts.
//
// Visual contract (DESIGN.md § 11.5):
//   - Bars use the brand-extension palette (voyage / blossom / primary /
//     voyage-soft) — one hue per metric axis, not per source.
//   - Axis labels: `--color-muted-foreground`.
//   - Grid: `--color-border` horizontal dashes.
//   - Legend below the chart, matching the colour chips used in the bar.
//   - Responsive: fills container; fixed 200 px height.

import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
  type TooltipProps,
} from "recharts";
import type { CSSProperties } from "react";

export interface ThroughputBarEntry {
  source: string;
  received: number;
  sent: number;
  turns: number;
  toolCalls: number;
}

// One design-token hue per metric axis.
const METRIC_COLOURS = {
  received: "var(--color-voyage)",
  sent: "var(--color-blossom-deep)",
  turns: "var(--color-primary)",
  toolCalls: "var(--color-voyage-soft)",
} as const;

const METRIC_LABELS = {
  received: "Received",
  sent: "Sent",
  turns: "Turns",
  toolCalls: "Tool calls",
} as const;

interface ThroughputTooltipPayload {
  dataKey?: string;
  value?: number;
  color?: string;
}

function ThroughputTooltip({
  active,
  payload,
  label,
}: TooltipProps<number, string> & {
  active?: boolean;
  payload?: ThroughputTooltipPayload[];
  label?: string;
}) {
  if (!active || !payload?.length) return null;
  const tooltipStyle: CSSProperties = {
    background: "var(--color-card)",
    border: "1px solid var(--color-border)",
    borderRadius: "var(--radius-md, 6px)",
    padding: "8px 12px",
    fontSize: "0.75rem",
    color: "var(--color-foreground)",
    boxShadow: "var(--shadow-sm, 0 1px 3px rgba(0,0,0,.4))",
    maxWidth: 220,
  };
  return (
    <div style={tooltipStyle}>
      <div
        style={{
          color: "var(--color-muted-foreground)",
          marginBottom: 4,
          fontFamily: "monospace",
          fontSize: "0.7rem",
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
        }}
        title={label}
      >
        {label}
      </div>
      {payload.map((item, i) => (
        <div
          key={`${item.dataKey ?? ""}-${i}`}
          style={{ display: "flex", justifyContent: "space-between", gap: 12 }}
        >
          <span style={{ color: item.color }}>
            {METRIC_LABELS[(item.dataKey ?? "") as keyof typeof METRIC_LABELS] ??
              item.dataKey}
          </span>
          <span style={{ fontWeight: 600 }}>
            {Number(item.value ?? 0).toLocaleString()}
          </span>
        </div>
      ))}
    </div>
  );
}

/** Truncate long source identifiers so the Y-axis label doesn't overflow. */
function truncateSource(s: string, maxLen = 20): string {
  // Strip scheme prefix for the chart label — keep the name part.
  const idx = s.indexOf("://");
  const name = idx >= 0 ? s.slice(idx + 3) : s;
  return name.length > maxLen ? `…${name.slice(-maxLen + 1)}` : name;
}

export interface ThroughputBarChartProps {
  entries: ThroughputBarEntry[];
  /** Chart height in px. Defaults to 220. */
  height?: number;
}

/**
 * Horizontal stacked-bar chart for throughput metrics per source.
 * Sorted descending by total so the highest-traffic source is at the top.
 */
export function ThroughputBarChart({
  entries,
  height = 220,
}: ThroughputBarChartProps) {
  if (entries.length === 0) {
    return (
      <p
        className="py-4 text-center text-sm text-muted-foreground"
        data-testid="throughput-bar-chart-empty"
      >
        No throughput data for this window.
      </p>
    );
  }

  // Sort descending by total; limit to top 15 to keep the chart readable.
  const sorted = [...entries]
    .sort(
      (a, b) =>
        b.received + b.sent + b.turns + b.toolCalls -
        (a.received + a.sent + a.turns + a.toolCalls),
    )
    .slice(0, 15);

  const data = sorted.map((e) => ({
    source: e.source,
    label: truncateSource(e.source),
    received: e.received,
    sent: e.sent,
    turns: e.turns,
    toolCalls: e.toolCalls,
  }));

  const barHeight = Math.max(height, 40 + data.length * 32);

  return (
    <div
      aria-label="Throughput per source (stacked bar)"
      role="img"
      data-testid="throughput-bar-chart"
      style={{ height: barHeight }}
    >
      <ResponsiveContainer width="100%" height="100%">
        <BarChart
          data={data}
          layout="vertical"
          margin={{ top: 4, right: 16, left: 8, bottom: 4 }}
        >
          <CartesianGrid
            strokeDasharray="3 3"
            stroke="var(--color-border)"
            horizontal={false}
          />
          <XAxis
            type="number"
            tick={{ fontSize: 10, fill: "var(--color-muted-foreground)" }}
            axisLine={false}
            tickLine={false}
            tickFormatter={(v) => Number(v).toLocaleString()}
          />
          <YAxis
            type="category"
            dataKey="label"
            width={100}
            tick={{
              fontSize: 10,
              fill: "var(--color-muted-foreground)",
              fontFamily: "monospace",
            }}
            axisLine={false}
            tickLine={false}
          />
          <Tooltip content={<ThroughputTooltip />} cursor={{ fill: "var(--color-muted)", opacity: 0.3 }} />
          <Legend
            wrapperStyle={{ fontSize: "0.7rem", paddingTop: 4 }}
            iconSize={8}
            formatter={(value) =>
              METRIC_LABELS[value as keyof typeof METRIC_LABELS] ?? value
            }
          />
          <Bar
            dataKey="received"
            stackId="a"
            fill={METRIC_COLOURS.received}
            name="received"
            radius={[0, 0, 0, 0]}
            maxBarSize={18}
          />
          <Bar
            dataKey="sent"
            stackId="a"
            fill={METRIC_COLOURS.sent}
            name="sent"
            maxBarSize={18}
          />
          <Bar
            dataKey="turns"
            stackId="a"
            fill={METRIC_COLOURS.turns}
            name="turns"
            maxBarSize={18}
          />
          <Bar
            dataKey="toolCalls"
            stackId="a"
            fill={METRIC_COLOURS.toolCalls}
            name="toolCalls"
            maxBarSize={18}
            radius={[0, 3, 3, 0]}
          />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
