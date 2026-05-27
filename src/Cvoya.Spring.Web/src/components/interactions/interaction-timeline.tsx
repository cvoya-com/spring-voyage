"use client";

// Interactions timeline (#2867). Stacked-area chart of message sends
// over time, with one series per kind (agent / unit / human /
// connector). A recharts `<Brush>` lets the operator pick a narrower
// window — the change rolls up into the URL state via `onBrush` so the
// graph + matrix refetch with the new `since` / `until`.
//
// Brush is disabled when live mode is on — the window pins to "now" so
// the operator can watch the stream tick by without the brush fighting
// the auto-scroll.

import { useMemo, useRef } from "react";
import {
  Area,
  AreaChart,
  Brush,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipProps,
} from "recharts";

import type { InteractionsTimelineBucketResponse } from "@/lib/api/types";

import { NODE_KIND_TINT } from "./interaction-detail";

export interface InteractionTimelineProps {
  buckets: readonly InteractionsTimelineBucketResponse[];
  /** Disable the brush — used when live mode is on. */
  brushDisabled?: boolean;
  /** Called with the new window when the brush moves. */
  onBrush?: (windowMs: { since: string; until: string }) => void;
  /** Chart height in px. */
  height?: number;
}

interface TimelineRow {
  bucket: string;
  agent: number;
  unit: number;
  human: number;
  connector: number;
}

const KIND_ORDER: readonly (keyof Omit<TimelineRow, "bucket">)[] = [
  "agent",
  "unit",
  "human",
  "connector",
];

function formatBucketLabel(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });
}

interface TimelineTooltipProps extends TooltipProps<number, string> {
  active?: boolean;
  payload?: Array<{ name: string; value: number; color: string }>;
  label?: string;
}

function TimelineTooltip({ active, payload, label }: TimelineTooltipProps) {
  if (!active || !payload?.length) return null;
  const total = payload.reduce((acc, p) => acc + (p.value ?? 0), 0);
  return (
    <div
      data-testid="interaction-timeline-tooltip"
      className="rounded-md border border-border bg-card p-2 text-xs shadow-sm"
    >
      <div className="text-muted-foreground">
        {label ? new Date(label).toLocaleString() : ""}
      </div>
      <div className="mt-1 font-medium">{total} messages</div>
      <ul className="mt-1 space-y-0.5">
        {payload.map((p) => (
          <li key={p.name} className="flex items-center gap-1.5">
            <span
              aria-hidden="true"
              className="inline-block h-2 w-2 rounded-full"
              style={{ background: p.color }}
            />
            <span className="text-muted-foreground">{p.name}</span>
            <span className="ml-auto font-mono tabular-nums">{p.value}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function InteractionTimeline({
  buckets,
  brushDisabled = false,
  onBrush,
  height = 200,
}: InteractionTimelineProps) {
  const data = useMemo<TimelineRow[]>(() => {
    return buckets.map((b) => ({
      bucket: b.bucket,
      agent: Number(b.byKind?.agent ?? 0),
      unit: Number(b.byKind?.unit ?? 0),
      human: Number(b.byKind?.human ?? 0),
      connector: Number(b.byKind?.connector ?? 0),
    }));
  }, [buckets]);

  // Track the previous brush window so we only fire `onBrush` when it
  // actually changes — recharts emits an event on every render that
  // includes the brush, even if neither bound moved.
  const lastBrush = useRef<{ start: number; end: number } | null>(null);

  if (buckets.length === 0) {
    return (
      <p
        className="rounded-md border border-dashed border-border p-6 text-center text-sm text-muted-foreground"
        data-testid="interaction-timeline-empty"
      >
        No timeline data in this window.
      </p>
    );
  }

  const handleBrush = (range: { startIndex?: number; endIndex?: number }) => {
    if (brushDisabled || !onBrush) return;
    if (range.startIndex === undefined || range.endIndex === undefined) return;
    if (
      lastBrush.current?.start === range.startIndex &&
      lastBrush.current?.end === range.endIndex
    ) {
      return;
    }
    lastBrush.current = { start: range.startIndex, end: range.endIndex };
    const startBucket = data[range.startIndex];
    const endBucket = data[range.endIndex];
    if (!startBucket || !endBucket) return;
    onBrush({ since: startBucket.bucket, until: endBucket.bucket });
  };

  return (
    <div
      data-testid="interaction-timeline"
      data-brush-enabled={!brushDisabled || undefined}
      style={{ height }}
      role="img"
      aria-label="Messages over time, stacked by participant kind"
    >
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={data} margin={{ top: 4, right: 8, left: 0, bottom: 0 }}>
          <CartesianGrid
            strokeDasharray="3 3"
            stroke="var(--color-border)"
            vertical={false}
          />
          <XAxis
            dataKey="bucket"
            tickFormatter={formatBucketLabel}
            tick={{ fontSize: 10, fill: "var(--color-muted-foreground)" }}
            axisLine={false}
            tickLine={false}
            interval="preserveStartEnd"
          />
          <YAxis
            tick={{ fontSize: 10, fill: "var(--color-muted-foreground)" }}
            axisLine={false}
            tickLine={false}
            width={32}
            allowDecimals={false}
          />
          <Tooltip
            content={<TimelineTooltip />}
            cursor={{ stroke: "var(--color-border)" }}
          />
          {KIND_ORDER.map((kind) => (
            <Area
              key={kind}
              type="monotone"
              dataKey={kind}
              stackId="kind"
              stroke={NODE_KIND_TINT[kind]}
              fill={NODE_KIND_TINT[kind]}
              fillOpacity={0.35}
              strokeWidth={1.5}
              dot={false}
              name={kind}
              isAnimationActive={false}
            />
          ))}
          {brushDisabled ? null : (
            <Brush
              dataKey="bucket"
              height={20}
              stroke="var(--color-primary)"
              travellerWidth={6}
              tickFormatter={formatBucketLabel}
              onChange={handleBrush}
              data-testid="interaction-timeline-brush"
            />
          )}
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}
