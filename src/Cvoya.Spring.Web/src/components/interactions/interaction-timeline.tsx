"use client";

// Interactions timeline (#2867 + #2870 + post-PR-2875 redesign).
//
// Per-actor line chart of message touches over time — one Line per
// in-scope node (agent / unit / human / connector). Each pulse
// contributes one tally to the sender's line and one to the receiver's,
// so an actor's line reads as "total touches in this bucket" rather
// than "messages sent." The aggregated per-kind view was confusing when
// kinds had vastly different volumes (one tall line, three flat lines
// at the top of the stack); per-actor lines surface the individual
// participants you usually want to compare.
//
// Colours: each line is HSL-derived from its actor's kind tint with a
// small per-id hue offset (deterministic). Lines in the same kind
// share a family colour so the chart still reads as "blue cluster =
// agents, pink cluster = humans" at a glance.
//
// Hover: hovering a legend chip OR a line dims every other line so a
// single actor's trajectory stands out across a crowded chart.
//
// Brush: always narrows `[since, until]` in URL state. In rewind mode
// the cursor reference line (driven by `cursorIso`) shows playback
// position; clicking a bucket seeks the cursor.

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Brush,
  CartesianGrid,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipProps,
} from "recharts";

import { cn } from "@/lib/utils";
import type {
  InteractionsNodeResponse,
  InteractionsTimelineBucketResponse,
} from "@/lib/api/types";

export interface InteractionTimelineProps {
  buckets: readonly InteractionsTimelineBucketResponse[];
  /**
   * The in-scope nodes — used to map per-actor counts in
   * `bucket.byActor` to display names + kind tints for the per-actor
   * line series and legend chips.
   */
  nodes: readonly InteractionsNodeResponse[];
  /** Disable the brush — used when live mode is on. */
  brushDisabled?: boolean;
  /** Called with the new window when the brush moves. */
  onBrush?: (windowMs: { since: string; until: string }) => void;
  /** Chart height in px (just the chart area; the legend renders below). */
  height?: number;
  /**
   * Current cursor position during rewind playback, ISO 8601. Renders
   * as a vertical reference line so the operator can see where the
   * playhead is sitting relative to the buckets.
   */
  cursorIso?: string;
  /**
   * Click handler for individual buckets. The page wires this in
   * rewind mode to seek the playback cursor to the clicked bucket.
   */
  onBucketClick?: (bucketIso: string) => void;
}

interface TimelineRow {
  /** Bucket start as ISO 8601 — kept for click callbacks + tooltips. */
  bucket: string;
  /** Bucket start as epoch ms for the numeric XAxis. */
  bucketMs: number;
  /** Sparse per-actor counts — actor id → touches in this bucket. */
  [actorId: string]: string | number;
}

interface VisibleActor {
  id: string;
  kind: string;
  displayName: string;
  total: number;
  color: string;
}

/**
 * Base HSL hue per actor kind. Each actor's stroke colour is derived
 * from this base by adding a small per-id hue offset (deterministic
 * hash) so the chart reads as "agents are the blue cluster, humans the
 * pink cluster" without making every agent literally indistinguishable.
 */
const KIND_BASE_HUE: Record<string, number> = {
  agent: 220, // blue
  unit: 265, // voyage purple
  human: 340, // blossom pink
  connector: 35, // amber
};

const FALLBACK_HUE = 200;
const HUE_OFFSET_RANGE = 50; // ±25° within the kind family
const ACTOR_SATURATION = 65;
const ACTOR_LIGHTNESS = 48;

function actorColor(kind: string, actorId: string): string {
  const base = KIND_BASE_HUE[kind] ?? FALLBACK_HUE;
  // 32-bit FNV-1a hash — deterministic, fast, well-distributed over
  // 32-hex actor ids.
  let hash = 0x811c9dc5;
  for (let i = 0; i < actorId.length; i++) {
    hash ^= actorId.charCodeAt(i);
    hash = (hash * 0x01000193) >>> 0;
  }
  const offset = (hash % HUE_OFFSET_RANGE) - HUE_OFFSET_RANGE / 2;
  const hue = (base + offset + 360) % 360;
  return `hsl(${hue.toFixed(0)}, ${ACTOR_SATURATION}%, ${ACTOR_LIGHTNESS}%)`;
}

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

const TOOLTIP_TOP_N = 10;

function TimelineTooltip({ active, payload, label }: TimelineTooltipProps) {
  if (!active || !payload?.length) return null;
  const nonZero = payload.filter((p) => (p.value ?? 0) > 0);
  const total = nonZero.reduce((acc, p) => acc + (p.value ?? 0), 0);
  // Each pulse contributes 2 touches (sender + receiver), so the
  // bucket's true message count is half the sum of per-actor touches.
  const messageCount = Math.round(total / 2);
  const sorted = [...nonZero].sort((a, b) => (b.value ?? 0) - (a.value ?? 0));
  const top = sorted.slice(0, TOOLTIP_TOP_N);
  const extra = sorted.length - top.length;
  return (
    <div
      data-testid="interaction-timeline-tooltip"
      className="rounded-md border border-border bg-card p-2 text-xs shadow-sm"
    >
      <div className="text-muted-foreground">
        {label ? new Date(label).toLocaleString() : ""}
      </div>
      <div className="mt-1 font-medium">
        {messageCount} {messageCount === 1 ? "message" : "messages"}
        <span className="ml-1 text-muted-foreground">
          · {nonZero.length}{" "}
          {nonZero.length === 1 ? "actor" : "actors"} active
        </span>
      </div>
      <ul className="mt-1 space-y-0.5">
        {top.map((p) => (
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
      {extra > 0 ? (
        <div className="mt-1 text-[10px] text-muted-foreground">
          +{extra} more not shown
        </div>
      ) : null}
    </div>
  );
}

export function InteractionTimeline({
  buckets,
  nodes,
  brushDisabled = false,
  onBrush,
  height = 200,
  cursorIso,
  onBucketClick,
}: InteractionTimelineProps) {
  // Collect every actor that has a non-zero tally in any bucket, then
  // join against the in-scope node list for display name + kind. The
  // result is sorted by total touches descending so the legend reads
  // busiest-first. Nodes without any touches in the window are omitted
  // — quiet participants don't earn a line.
  const visibleActors = useMemo<VisibleActor[]>(() => {
    const totals = new Map<string, number>();
    for (const b of buckets) {
      const byActor = b.byActor ?? {};
      for (const [actorId, count] of Object.entries(byActor)) {
        totals.set(actorId, (totals.get(actorId) ?? 0) + Number(count ?? 0));
      }
    }
    const nodeById = new Map<string, InteractionsNodeResponse>();
    for (const n of nodes) nodeById.set(n.id, n);
    const out: VisibleActor[] = [];
    for (const [id, total] of totals) {
      if (total <= 0) continue;
      const node = nodeById.get(id);
      out.push({
        id,
        kind: node?.kind ?? "agent",
        displayName: node?.displayName ?? id.slice(0, 8),
        total,
        color: actorColor(node?.kind ?? "agent", id),
      });
    }
    out.sort((a, b) => b.total - a.total);
    return out;
  }, [buckets, nodes]);

  // Per-bucket row materialised with each actor's count under its own
  // top-level key so recharts can use `dataKey={actor.id}` directly.
  const data = useMemo<TimelineRow[]>(() => {
    return buckets.map((b) => {
      const row: TimelineRow = {
        bucket: b.bucket,
        bucketMs: new Date(b.bucket).getTime(),
      };
      const byActor = b.byActor ?? {};
      for (const actor of visibleActors) {
        row[actor.id] = Number(byActor[actor.id] ?? 0);
      }
      return row;
    });
  }, [buckets, visibleActors]);

  // Hover state. `hoveredId === null` means no dimming; otherwise that
  // actor's line stays vivid, the rest fade. Hover is driven by the
  // legend chips below the chart — pointer-aware Lines inside recharts
  // are unreliable when 50+ lines overlap, so the legend owns the
  // interaction.
  const [hoveredId, setHoveredId] = useState<string | null>(null);

  // Brush commit model — release-driven, not debounce-driven.
  //
  // recharts' Brush emits `onChange` continuously while the user drags a
  // handle. Committing on every tick (or even on a 220 ms idle debounce)
  // triggered a refetch mid-drag, the data array got replaced under the
  // mouse, the brush re-mounted with new indices, and the gesture
  // collapsed. We now stash the latest range in a ref on every onChange
  // and commit exactly once when the operator releases the pointer —
  // the gesture is atomic from the data layer's perspective and slow
  // drags survive intact.
  //
  // `lastBrushCommit` dedupes against the most recently committed range
  // so the post-refetch re-mount echo (full range of new data) doesn't
  // re-fire and re-narrow the window.
  const pendingBrush = useRef<{ start: number; end: number } | null>(null);
  const lastBrushCommit = useRef<{ start: number; end: number } | null>(null);

  const cursorMs = useMemo(() => {
    if (!cursorIso) return undefined;
    const v = new Date(cursorIso).getTime();
    return Number.isFinite(v) ? v : undefined;
  }, [cursorIso]);

  // Commit any pending brush range when the operator releases the
  // pointer. The listener is global because recharts attaches its own
  // pointer-capture to the brush handle — releases routinely land off
  // the timeline DOM element. `pendingBrush.current` is null in the
  // common case (no brush interaction), so unrelated clicks anywhere
  // on the page are no-ops.
  useEffect(() => {
    if (brushDisabled || !onBrush) return;
    const commitBrush = () => {
      const range = pendingBrush.current;
      if (!range) return;
      pendingBrush.current = null;
      // Reject collapsed ranges. When the operator drags one handle
      // past the other, recharts clamps them and emits onChange with
      // start ≈ end. Committing that would write since == until (or
      // off by a single bucket), leaving the brush with no room to
      // drag any further — operators would then have to use the
      // explicit Reset window button to recover. Drop those commits
      // here so the previous (wider) window stays in URL state.
      if (range.end - range.start < 1) {
        return;
      }
      if (
        lastBrushCommit.current?.start === range.start &&
        lastBrushCommit.current?.end === range.end
      ) {
        return;
      }
      lastBrushCommit.current = { start: range.start, end: range.end };
      const startBucket = data[range.start];
      const endBucket = data[range.end];
      if (!startBucket || !endBucket) return;
      onBrush({
        since: String(startBucket.bucket),
        until: String(endBucket.bucket),
      });
    };
    window.addEventListener("pointerup", commitBrush);
    window.addEventListener("pointercancel", commitBrush);
    return () => {
      window.removeEventListener("pointerup", commitBrush);
      window.removeEventListener("pointercancel", commitBrush);
    };
  }, [brushDisabled, onBrush, data]);

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
    // Re-mount echo: after our own onBrush commit triggers a refetch
    // and the brush re-mounts on the new (narrower) data array, recharts
    // emits onChange(0, lastIndex) as its initial state. Skip that one
    // — operators reset to the default window via the explicit "Reset
    // window" button in the filter strip rather than dragging the
    // handles back to their bounds.
    if (range.startIndex === 0 && range.endIndex === data.length - 1) {
      pendingBrush.current = null;
      return;
    }
    // Stash the latest range; the pointerup listener commits it as
    // a single atomic update.
    pendingBrush.current = { start: range.startIndex, end: range.endIndex };
  };

  const handleChartClick = (
    payload: { activeLabel?: number | string } | undefined,
    e?: unknown,
  ) => {
    if (!onBucketClick || !payload) return;
    // Recharts renders <Brush> as a child of <AreaChart>/<LineChart>, so
    // mousedown / mouseup on the brush handles bubble up through the
    // chart's onClick. Without this filter, a brush drag would also
    // fire the bucket-click handler at the same time — collapsing the
    // window to a single bucket on every drag. We walk the original
    // event target up to the chart root and skip the click if it
    // started anywhere inside the brush DOM subtree.
    if (
      e &&
      typeof e === "object" &&
      "target" in e &&
      e.target instanceof Element &&
      e.target.closest(
        ".recharts-brush, .recharts-brush-slide, .recharts-brush-traveller, .recharts-brush-texts",
      )
    ) {
      return;
    }
    const labelMs =
      typeof payload.activeLabel === "number"
        ? payload.activeLabel
        : payload.activeLabel
          ? new Date(payload.activeLabel).getTime()
          : NaN;
    if (!Number.isFinite(labelMs)) return;
    const bucket = data.find((d) => d.bucketMs === labelMs);
    if (bucket) onBucketClick(String(bucket.bucket));
  };

  return (
    <div
      data-testid="interaction-timeline"
      data-brush-enabled={!brushDisabled || undefined}
      className="flex flex-col gap-2"
      role="region"
      aria-label="Message touches per actor over time"
    >
      <div style={{ height }}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart
            data={data}
            margin={{ top: 4, right: 8, left: 0, bottom: 0 }}
            onClick={handleChartClick}
          >
            <CartesianGrid
              strokeDasharray="3 3"
              stroke="var(--color-border)"
              vertical={false}
            />
            <XAxis
              dataKey="bucketMs"
              type="number"
              scale="time"
              domain={["dataMin", "dataMax"]}
              tickFormatter={(v: number) =>
                formatBucketLabel(new Date(v).toISOString())
              }
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
              labelFormatter={(value) =>
                typeof value === "number"
                  ? new Date(value).toISOString()
                  : String(value)
              }
            />
            {visibleActors.map((actor) => {
              const dimmed = hoveredId !== null && hoveredId !== actor.id;
              return (
                <Line
                  key={actor.id}
                  type="monotone"
                  dataKey={actor.id}
                  stroke={actor.color}
                  strokeOpacity={dimmed ? 0.1 : 0.9}
                  strokeWidth={hoveredId === actor.id ? 2.75 : 1.5}
                  dot={false}
                  name={actor.displayName}
                  isAnimationActive={false}
                />
              );
            })}
            {cursorMs !== undefined ? (
              <ReferenceLine
                x={cursorMs}
                stroke="var(--color-primary)"
                strokeWidth={2}
                strokeDasharray="3 3"
                ifOverflow="extendDomain"
                data-testid="interaction-timeline-cursor"
              />
            ) : null}
            {brushDisabled ? null : (
              <Brush
                dataKey="bucketMs"
                height={20}
                stroke="var(--color-primary)"
                travellerWidth={6}
                tickFormatter={(v: number) =>
                  formatBucketLabel(new Date(v).toISOString())
                }
                onChange={handleBrush}
                data-testid="interaction-timeline-brush"
              />
            )}
          </LineChart>
        </ResponsiveContainer>
      </div>

      {visibleActors.length > 0 ? (
        <ActorLegend
          actors={visibleActors}
          hoveredId={hoveredId}
          onHover={setHoveredId}
        />
      ) : null}
    </div>
  );
}

interface ActorLegendProps {
  actors: readonly VisibleActor[];
  hoveredId: string | null;
  onHover: (id: string | null) => void;
}

/**
 * Per-actor chips below the chart. Hovering a chip dims the other
 * lines in the chart so the operator can isolate a single actor's
 * trajectory across a crowded canvas. The list wraps and is capped at
 * an overflow chip rather than a hard scroll so the legend stays at a
 * single readable height for typical (~10 actor) scenes.
 */
function ActorLegend({ actors, hoveredId, onHover }: ActorLegendProps) {
  return (
    <ul
      className="flex flex-wrap items-center gap-1 text-[10px]"
      data-testid="interaction-timeline-legend"
      onMouseLeave={() => onHover(null)}
    >
      {actors.map((actor) => {
        const dimmed = hoveredId !== null && hoveredId !== actor.id;
        return (
          <li key={actor.id}>
            <button
              type="button"
              onMouseEnter={() => onHover(actor.id)}
              onFocus={() => onHover(actor.id)}
              onBlur={() => onHover(null)}
              className={cn(
                "inline-flex items-center gap-1 rounded-full border border-border bg-card px-1.5 py-0.5 transition-opacity",
                dimmed ? "opacity-40" : "opacity-100",
                hoveredId === actor.id ? "border-foreground" : "",
              )}
              title={`${actor.displayName} (${actor.kind}) — ${actor.total} ${
                actor.total === 1 ? "touch" : "touches"
              }`}
              data-testid={`interaction-timeline-legend-${actor.id}`}
            >
              <span
                aria-hidden="true"
                className="inline-block h-2 w-2 rounded-full"
                style={{ background: actor.color }}
              />
              <span className="truncate max-w-[10rem]">{actor.displayName}</span>
              <span className="font-mono tabular-nums text-muted-foreground">
                {actor.total}
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
