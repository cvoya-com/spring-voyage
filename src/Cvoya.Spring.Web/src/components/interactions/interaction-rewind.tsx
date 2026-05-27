"use client";

// Rewind-mode transport bar (#2872).
//
// Walks a virtual cursor through the [since, until] window at a chosen
// playback speed and dispatches every pulse the cursor steps over to the
// parent. The parent forwards the dispatched pulses through the exact
// same path the live SSE stream uses (`<InteractionGraph pulses=…>` +
// `<InteractionMatrix>` cell flash) so there's only one rendering
// engine — replay just feeds it from a static list instead of a stream.
//
// Cursor math
// -----------
// We pick a "rate" — virtual-window milliseconds per real-time
// millisecond — and advance the cursor on every `requestAnimationFrame`
// tick by `(realDeltaMs * rate)`. Changing the speed mid-playback rebases
// the realtime anchor so the cursor itself is preserved at the moment of
// change (no jump). Pausing snapshots the cursor; resuming rebases the
// realtime anchor against the snapshot.
//
// Dispatch
// --------
// On each frame, every pulse with `timestamp ∈ (prevCursor, currentCursor]`
// fires through `onPulse` in original (timestamp-ordered) order. We
// pre-sort the pulse list on prop change so we can walk it linearly with
// a single index pointer; we never re-scan from index 0 mid-replay. A
// scrub backwards through the controlled `cursorMs` prop resets the
// index to the last pulse whose timestamp is <= the new cursor.
//
// Why no setState-in-useEffect?
// -----------------------------
// React 19 / `react-hooks/set-state-in-effect` forbids that pattern. We
// instead drive the cursor + finished flags via mutable refs and a
// single `useState` "tick" counter that we bump from the rAF loop and
// the scrub handler to provoke a re-render. The render reads from the
// refs, so it always paints the freshest cursor without an intermediate
// state-update cycle.

import { Pause, Play, RotateCcw } from "lucide-react";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { InteractionsPulseResponse } from "@/lib/api/types";

/**
 * Playback-speed presets (virtual ms per real ms). 30× is the default —
 * a 10-minute window finishes in ~20s, brisk enough to feel like
 * replay but slow enough that an operator can recognise individual
 * pulses. The presets are ordered ascending so arrow-key nudging on the
 * segmented control feels natural.
 */
export const REWIND_SPEEDS = [1, 5, 30, 100, 1000] as const;
export type RewindSpeed = (typeof REWIND_SPEEDS)[number];
export const DEFAULT_REWIND_SPEED: RewindSpeed = 30;

/**
 * Dispatched-pulse frame shape — matches the SSE `InteractionsPulseFrame`
 * the page's `handlePulse` already consumes, with `count: 1` (each
 * history row is a single delivery) and a single-element `messageIds`.
 */
export interface RewindPulseFrame {
  messageIds: string[];
  fromId: string;
  toId: string;
  count: number;
}

interface InteractionRewindProps {
  since: Date;
  until: Date;
  pulses: readonly InteractionsPulseResponse[];
  /**
   * Controlled cursor — when provided, the component renders the cursor
   * at this position instead of relying on its internal state. Used by
   * the timeline brush to scrub. When the operator resumes play the
   * cursor advances from the controlled value.
   */
  cursorMs?: number;
  /**
   * Fires when the cursor moves (rAF tick or scrub). The page mirrors
   * this into URL-less local state so the timeline brush can highlight
   * the current position.
   */
  onCursorChange?: (cursorMs: number) => void;
  onPulse: (frame: RewindPulseFrame) => void;
  /** Fires when the cursor first reaches `until`. */
  onComplete?: () => void;
}

function clamp(value: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, value));
}

function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

export function InteractionRewind({
  since,
  until,
  pulses,
  cursorMs: cursorMsProp,
  onCursorChange,
  onPulse,
  onComplete,
}: InteractionRewindProps) {
  const sinceMs = since.getTime();
  const untilMs = until.getTime();
  const windowMs = Math.max(1, untilMs - sinceMs);

  // Pre-sort the pulse list so the dispatch walk is linear. The
  // dispatch-index ref steps through this exact array.
  const sortedPulses = useMemo(() => {
    const arr = [...pulses];
    arr.sort(
      (a, b) =>
        new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime(),
    );
    return arr;
  }, [pulses]);

  // Cursor + flags live in refs so the rAF loop and the scrub-prop sync
  // can write them without provoking the React 19 setState-in-effect
  // lint rule. A `tick` counter forces a re-render whenever the cursor
  // advances; the render body reads `cursorRef.current` etc. so the
  // paint reflects the latest values without an indirection.
  const cursorRef = useRef(0);
  const finishedRef = useRef(false);
  const playingRef = useRef(false);
  const speedRef = useRef<RewindSpeed>(DEFAULT_REWIND_SPEED);
  const dispatchIndexRef = useRef(0);
  // Realtime anchor — paired (real-clock instant, cursor position).
  // Updated on play, pause, resume, scrub, or speed-change so the
  // cursor never jumps mid-burst.
  const anchorRef = useRef<{ realMs: number; cursorMs: number } | null>(null);
  const [tick, setTick] = useState(0);
  const bump = useCallback(() => setTick((n) => n + 1), []);

  // Reset on window change (operator picked a new `since`/`until`, or
  // the pulse list itself changed). We use a "session key" we compare
  // each render, and when it differs we mutate the refs *during render*
  // — React allows that for refs (writing during render to set up state
  // tied to props). We don't bump `tick` here because React is already
  // running this render cycle for the new props.
  const sessionKey = useMemo(
    () => `${sinceMs}|${untilMs}|${sortedPulses.length}`,
    [sinceMs, untilMs, sortedPulses],
  );
  const lastSessionRef = useRef<string | null>(null);
  if (lastSessionRef.current !== sessionKey) {
    lastSessionRef.current = sessionKey;
    cursorRef.current = 0;
    finishedRef.current = false;
    dispatchIndexRef.current = 0;
    anchorRef.current = null;
    playingRef.current = false;
  }

  // Controlled-cursor sync — when the parent supplies `cursorMs`, treat
  // it as the source of truth. We *write to refs during render* (legal
  // for refs) instead of in a `useEffect` so the next paint already
  // reflects the scrubbed position. Reset the dispatch index so a
  // backwards scrub replays from the new point and a forwards scrub
  // doesn't re-fire previously-played pulses.
  const lastCursorPropRef = useRef<number | undefined>(undefined);
  if (cursorMsProp !== undefined && cursorMsProp !== lastCursorPropRef.current) {
    lastCursorPropRef.current = cursorMsProp;
    const clamped = clamp(cursorMsProp, 0, windowMs);
    cursorRef.current = clamped;
    finishedRef.current = clamped >= windowMs;
    // Re-anchor an active play burst so it picks up from the scrubbed
    // cursor without lurching.
    if (anchorRef.current) {
      anchorRef.current = {
        realMs: performance.now(),
        cursorMs: clamped,
      };
    }
    // Walk the sorted-pulse list to find the next dispatchable index.
    const cursorAbs = sinceMs + clamped;
    let idx = 0;
    while (
      idx < sortedPulses.length &&
      new Date(sortedPulses[idx].timestamp).getTime() <= cursorAbs
    ) {
      idx++;
    }
    dispatchIndexRef.current = idx;
  }

  // Keep callbacks fresh in refs so the rAF loop doesn't capture stale
  // closures from the first render. (Render-phase ref-writes are fine
  // for assignments that depend only on props/state from the same
  // render.)
  const onPulseRef = useRef(onPulse);
  const onCompleteRef = useRef(onComplete);
  const onCursorChangeRef = useRef(onCursorChange);
  onPulseRef.current = onPulse;
  onCompleteRef.current = onComplete;
  onCursorChangeRef.current = onCursorChange;

  // rAF loop. The hook is keyed only on `tick` for play state — we use
  // a `playingRef` snapshot inside the loop, so toggling play/pause
  // doesn't tear down and restart the loop.
  useEffect(() => {
    if (!playingRef.current) return;

    let rafId: number | null = null;
    const tickLoop = (now: number) => {
      if (!playingRef.current) {
        anchorRef.current = null;
        return;
      }
      if (!anchorRef.current) {
        anchorRef.current = { realMs: now, cursorMs: cursorRef.current };
      }
      const elapsedReal = now - anchorRef.current.realMs;
      const prevCursor = cursorRef.current;
      const nextCursor = clamp(
        anchorRef.current.cursorMs + elapsedReal * speedRef.current,
        0,
        windowMs,
      );

      // Dispatch every pulse the cursor stepped over on this frame.
      // Closed lower bound on the very first tick (prevCursor === 0) so
      // a pulse landing exactly at `since` still fires; otherwise an
      // open lower bound prevents the same pulse from re-firing on the
      // subsequent frame.
      if (nextCursor > prevCursor) {
        const prevAbs = sinceMs + prevCursor;
        const nextAbs = sinceMs + nextCursor;
        const firstTick = prevCursor === 0;
        let idx = dispatchIndexRef.current;
        while (idx < sortedPulses.length) {
          const ts = new Date(sortedPulses[idx].timestamp).getTime();
          if (ts > nextAbs) break;
          const inWindow = firstTick ? ts >= prevAbs : ts > prevAbs;
          if (inWindow) {
            const p = sortedPulses[idx];
            onPulseRef.current({
              messageIds: [p.messageId],
              fromId: p.fromId,
              toId: p.toId,
              count: 1,
            });
          }
          idx++;
        }
        dispatchIndexRef.current = idx;
      }

      cursorRef.current = nextCursor;
      const finished = nextCursor >= windowMs;
      onCursorChangeRef.current?.(nextCursor);
      if (finished) {
        finishedRef.current = true;
        playingRef.current = false;
        anchorRef.current = null;
        onCompleteRef.current?.();
        bump();
        return;
      }
      bump();
      rafId = requestAnimationFrame(tickLoop);
    };
    rafId = requestAnimationFrame(tickLoop);

    return () => {
      if (rafId !== null) cancelAnimationFrame(rafId);
    };
    // `tick` is the only "are we playing now?" signal — when the user
    // pauses we set playingRef and bump; the next render re-runs this
    // hook with playingRef === false and we early-return.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick, sinceMs, sortedPulses, windowMs]);

  const setPlaying = useCallback(
    (next: boolean) => {
      if (playingRef.current === next) return;
      if (next && finishedRef.current) {
        // Resume after the cursor reached the end — restart playback
        // implicitly so pressing Play again on a finished window
        // doesn't no-op.
        cursorRef.current = 0;
        finishedRef.current = false;
        dispatchIndexRef.current = 0;
        anchorRef.current = null;
      } else {
        anchorRef.current = null;
      }
      playingRef.current = next;
      bump();
    },
    [bump],
  );

  const setSpeed = useCallback(
    (next: RewindSpeed) => {
      if (speedRef.current === next) return;
      // Rebase the realtime anchor so the cursor is preserved at the
      // moment of change — no jump backwards or forwards on
      // speed-change.
      if (anchorRef.current) {
        anchorRef.current = {
          realMs: performance.now(),
          cursorMs: cursorRef.current,
        };
      }
      speedRef.current = next;
      bump();
    },
    [bump],
  );

  const restart = useCallback(() => {
    cursorRef.current = 0;
    finishedRef.current = false;
    dispatchIndexRef.current = 0;
    anchorRef.current = null;
    playingRef.current = false;
    onCursorChangeRef.current?.(0);
    bump();
  }, [bump]);

  // Arrow-key speed nudging when the segmented control is focused.
  const onSpeedKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLButtonElement>, current: RewindSpeed) => {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      const idx = REWIND_SPEEDS.indexOf(current);
      if (idx < 0) return;
      const nextIdx = clamp(
        idx + (e.key === "ArrowLeft" ? -1 : 1),
        0,
        REWIND_SPEEDS.length - 1,
      );
      if (nextIdx !== idx) {
        e.preventDefault();
        setSpeed(REWIND_SPEEDS[nextIdx]);
      }
    },
    [setSpeed],
  );

  // Reference `tick` so React knows the render depends on it — without
  // this the linter would prune the state. The ref-driven cursor is the
  // real source of paint truth, but `tick` is what schedules the paint.
  void tick;

  const cursorMs = cursorRef.current;
  const playing = playingRef.current;
  const speed = speedRef.current;
  const finished = finishedRef.current;
  const elapsedLabel = formatElapsed(cursorMs);
  const totalLabel = formatElapsed(windowMs);
  const progress = clamp(cursorMs / windowMs, 0, 1);

  return (
    <div
      data-testid="interaction-rewind"
      data-playing={playing || undefined}
      data-finished={finished || undefined}
      className="flex flex-wrap items-center gap-2 text-xs"
      role="group"
      aria-label="Rewind transport"
    >
      <Button
        type="button"
        size="sm"
        variant={playing ? "outline" : "default"}
        onClick={() => setPlaying(!playing)}
        aria-label={playing ? "Pause" : "Play"}
        aria-pressed={playing}
        data-testid="interaction-rewind-play"
        className="h-7 w-7 p-0"
      >
        {playing ? (
          <Pause className="h-3.5 w-3.5" aria-hidden="true" />
        ) : (
          <Play className="h-3.5 w-3.5" aria-hidden="true" />
        )}
      </Button>

      <div
        className="inline-flex rounded-full border border-border bg-muted/40 p-0.5"
        role="radiogroup"
        aria-label="Playback speed"
        data-testid="interaction-rewind-speed"
      >
        {REWIND_SPEEDS.map((s) => (
          <button
            key={s}
            type="button"
            role="radio"
            aria-checked={speed === s}
            onClick={() => setSpeed(s)}
            onKeyDown={(e) => onSpeedKeyDown(e, speed)}
            className={cn(
              "rounded-full px-2 py-0.5 font-mono text-[10px]",
              speed === s
                ? "bg-primary/15 text-primary"
                : "text-muted-foreground hover:text-foreground",
            )}
            data-testid={`interaction-rewind-speed-${s}`}
          >
            {s}x
          </button>
        ))}
      </div>

      <div
        className="flex min-w-32 items-center gap-1.5"
        data-testid="interaction-rewind-progress"
      >
        <span
          className="font-mono tabular-nums text-muted-foreground"
          data-testid="interaction-rewind-elapsed"
        >
          {elapsedLabel}
        </span>
        <span className="text-muted-foreground">/</span>
        <span
          className="font-mono tabular-nums text-muted-foreground"
          data-testid="interaction-rewind-total"
        >
          {totalLabel}
        </span>
        <div
          className="ml-2 h-1 w-24 overflow-hidden rounded-full bg-muted"
          aria-hidden="true"
        >
          <div
            className="h-full bg-primary transition-[width] duration-100"
            style={{ width: `${progress * 100}%` }}
          />
        </div>
      </div>

      <Button
        type="button"
        size="sm"
        variant="ghost"
        onClick={restart}
        disabled={!finished}
        aria-label="Restart"
        data-testid="interaction-rewind-restart"
        className="h-7 px-2"
      >
        <RotateCcw className="mr-1 h-3 w-3" aria-hidden="true" />
        Restart
      </Button>
    </div>
  );
}
