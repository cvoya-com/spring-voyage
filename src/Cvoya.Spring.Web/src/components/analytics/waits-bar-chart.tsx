"use client";

// Waits stacked-bar chart (#910). Renders a horizontal stacked-bar chart of
// idle / busy / waiting-for-human durations per source, sorted by total
// descending. Uses recharts.
//
// Visual contract (DESIGN.md § 11.5):
//   - Idle → `bg-success` (#22c55e) — "healthy / resting" state.
//   - Busy → `bg-warning` (#eab308) — "active" state.
//   - Waiting for human → `bg-destructive` (#ef4444) — blocked state.
//   These are the same semantic tokens used in the per-row stacked bars in
//   the existing list view, so the chart reads consistently.

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

export interface WaitsBarEntry {
  source: string;
  idleSeconds: number;
  busySeconds: number;
  waitingSeconds: number;
}

// Semantic colour tokens matching the list-view stacked bars.
const WAIT_COLOURS = {
  idle: "var(--color-success)",
  busy: "var(--color-warning)",
  waiting: "var(--color-destructive)",
} as const;

function formatDurationShort(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0s";
  const s = Math.floor(seconds);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d`;
  if (h > 0) return `${h}h`;
  if (m > 0) return `${m}m`;
  return `${s}s`;
}

interface WaitsTooltipPayload {
  dataKey?: string;
  value?: number;
  color?: string;
}

function WaitsTooltip({ active, payload, label }: TooltipProps<number, string> & {
  active?: boolean;
  payload?: WaitsTooltipPayload[];
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
  const labelMap: Record<string, string> = {
    idle: "Idle",
    busy: "Busy",
    waiting: "Waiting for human",
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
            {labelMap[item.dataKey ?? ""] ?? item.dataKey}
          </span>
          <span style={{ fontWeight: 600 }}>
            {formatDurationShort(Number(item.value ?? 0))}
          </span>
        </div>
      ))}
    </div>
  );
}

function truncateSource(s: string, maxLen = 20): string {
  const idx = s.indexOf("://");
  const name = idx >= 0 ? s.slice(idx + 3) : s;
  return name.length > maxLen ? `…${name.slice(-maxLen + 1)}` : name;
}

export interface WaitsBarChartProps {
  entries: WaitsBarEntry[];
  /** Chart height in px. Defaults to 220. */
  height?: number;
}

/**
 * Horizontal stacked-bar chart for wait-time dimensions per source.
 * Colours match the semantic tokens used in the companion list view
 * (success / warning / destructive) so both visualisations read the
 * same legend.
 */
export function WaitsBarChart({ entries, height = 220 }: WaitsBarChartProps) {
  if (entries.length === 0) {
    return (
      <p
        className="py-4 text-center text-sm text-muted-foreground"
        data-testid="waits-bar-chart-empty"
      >
        No wait-time data for this window.
      </p>
    );
  }

  const sorted = [...entries]
    .sort(
      (a, b) =>
        b.idleSeconds + b.busySeconds + b.waitingSeconds -
        (a.idleSeconds + a.busySeconds + a.waitingSeconds),
    )
    .slice(0, 15);

  const data = sorted.map((e) => ({
    source: e.source,
    label: truncateSource(e.source),
    idle: e.idleSeconds,
    busy: e.busySeconds,
    waiting: e.waitingSeconds,
  }));

  const barHeight = Math.max(height, 40 + data.length * 32);

  return (
    <div
      aria-label="Wait times per source (stacked bar)"
      role="img"
      data-testid="waits-bar-chart"
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
            tickFormatter={(v) => formatDurationShort(Number(v))}
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
          <Tooltip content={<WaitsTooltip />} cursor={{ fill: "var(--color-muted)", opacity: 0.3 }} />
          <Legend
            wrapperStyle={{ fontSize: "0.7rem", paddingTop: 4 }}
            iconSize={8}
            formatter={(value) => {
              const lm: Record<string, string> = {
                idle: "Idle",
                busy: "Busy",
                waiting: "Waiting for human",
              };
              return lm[value] ?? value;
            }}
          />
          <Bar
            dataKey="idle"
            stackId="w"
            fill={WAIT_COLOURS.idle}
            name="idle"
            maxBarSize={18}
          />
          <Bar
            dataKey="busy"
            stackId="w"
            fill={WAIT_COLOURS.busy}
            name="busy"
            maxBarSize={18}
          />
          <Bar
            dataKey="waiting"
            stackId="w"
            fill={WAIT_COLOURS.waiting}
            name="waiting"
            maxBarSize={18}
            radius={[0, 3, 3, 0]}
          />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
