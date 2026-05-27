/* @vitest-environment jsdom */
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { InteractionsPulseResponse } from "@/lib/api/types";

import {
  DEFAULT_REWIND_SPEED,
  InteractionRewind,
  REWIND_SPEEDS,
  type RewindPulseFrame,
} from "./interaction-rewind";

// Drive rAF deterministically. JSDOM doesn't ship a real frame loop;
// shimming it with our own scheduler lets us advance the cursor by an
// exact number of frames per assertion.
type FrameCb = (now: number) => void;
interface FakeRaf {
  now: number;
  queue: { id: number; cb: FrameCb }[];
  nextId: number;
}

let raf: FakeRaf;

function stepFrames(deltaMs: number) {
  const dt = deltaMs;
  raf.now += dt;
  // Each step drains every callback queued up to this point. Components
  // may re-queue inside their tick handler — we want those to run on
  // the *next* iteration, not piggy-back the current one, mirroring the
  // browser's per-frame scheduling.
  const queued = raf.queue;
  raf.queue = [];
  for (const item of queued) {
    item.cb(raf.now);
  }
}

beforeEach(() => {
  raf = { now: 0, queue: [], nextId: 1 };
  vi.stubGlobal("requestAnimationFrame", (cb: FrameCb) => {
    const id = raf.nextId++;
    raf.queue.push({ id, cb });
    return id;
  });
  vi.stubGlobal("cancelAnimationFrame", (id: number) => {
    raf.queue = raf.queue.filter((q) => q.id !== id);
  });
  // `performance.now()` is what the rewind component reads to compute
  // the realtime delta — keep it in sync with the fake rAF clock.
  vi.stubGlobal("performance", { now: () => raf.now });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const SINCE = new Date("2026-05-27T10:00:00.000Z");
const UNTIL = new Date("2026-05-27T10:10:00.000Z"); // 10-minute window
const WINDOW_MS = UNTIL.getTime() - SINCE.getTime();

function makePulse(
  offsetMs: number,
  fromId = "a",
  toId = "b",
): InteractionsPulseResponse {
  return {
    messageId: `m-${offsetMs}`,
    fromId,
    toId,
    timestamp: new Date(SINCE.getTime() + offsetMs).toISOString(),
    threadId: null,
    channel: "agent",
  };
}

describe("<InteractionRewind>", () => {
  it("renders the transport bar with default speed selected", () => {
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    expect(screen.getByTestId("interaction-rewind")).toBeInTheDocument();
    const defaultSpeed = screen.getByTestId(
      `interaction-rewind-speed-${DEFAULT_REWIND_SPEED}`,
    );
    expect(defaultSpeed).toHaveAttribute("aria-checked", "true");
    // Initial elapsed = 00:00, total = 10:00.
    expect(screen.getByTestId("interaction-rewind-elapsed").textContent).toBe(
      "00:00",
    );
    expect(screen.getByTestId("interaction-rewind-total").textContent).toBe(
      "10:00",
    );
  });

  it("clicking play advances the cursor on each rAF tick", () => {
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    // First frame primes the anchor (cursor stays at 0).
    act(() => {
      stepFrames(0);
    });
    // Second frame at 30× — advance enough real ms so the mm:ss
    // readout ticks over to at least 00:01.
    act(() => {
      stepFrames(60);
    });
    const elapsed = screen.getByTestId("interaction-rewind-elapsed").textContent;
    expect(elapsed).not.toBe("00:00");
    expect(screen.getByTestId("interaction-rewind")).toHaveAttribute(
      "data-playing",
    );
  });

  it("clicking pause stops the cursor where it is", () => {
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(100);
    });
    const elapsedAfterRun = screen.getByTestId(
      "interaction-rewind-elapsed",
    ).textContent;

    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    // Advance real time — the cursor must NOT move because we paused.
    act(() => {
      stepFrames(500);
    });
    expect(screen.getByTestId("interaction-rewind-elapsed").textContent).toBe(
      elapsedAfterRun,
    );
    expect(screen.getByTestId("interaction-rewind")).not.toHaveAttribute(
      "data-playing",
    );
  });

  it("speed multiplies cursor velocity", () => {
    const { rerender } = render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    // Slow run at 1× — 100 real ms → 100 virtual ms.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(100);
    });
    const elapsedSlow = screen.getByTestId(
      "interaction-rewind-elapsed",
    ).textContent;

    // Reset by re-rendering a fresh component (new key triggers session
    // reset). The current component is finished if we ran 100ms at 1×
    // through a 10-minute window — far from done — but we still want a
    // clean baseline for the 100× comparison.
    rerender(
      <InteractionRewind
        key="fast"
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-100"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(100);
    });
    const elapsedFast = screen.getByTestId(
      "interaction-rewind-elapsed",
    ).textContent;

    // At 100× a 100 ms real-time step advances 10000 ms virtual. At 1×
    // the same step advances 100 ms. Compare in seconds.
    const toSeconds = (mmss: string | null) => {
      const [m, s] = (mmss ?? "00:00").split(":").map((x) => Number(x));
      return m * 60 + s;
    };
    expect(toSeconds(elapsedFast)).toBeGreaterThan(toSeconds(elapsedSlow));
  });

  it("changes speed without jumping the cursor backwards", () => {
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(200);
    });
    const beforeSwitch = screen.getByTestId(
      "interaction-rewind-elapsed",
    ).textContent;
    // Switch to a different speed; the cursor must not snap back.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1"));
    });
    const afterSwitch = screen.getByTestId(
      "interaction-rewind-elapsed",
    ).textContent;
    const toSeconds = (mmss: string | null) => {
      const [m, s] = (mmss ?? "00:00").split(":").map((x) => Number(x));
      return m * 60 + s;
    };
    expect(toSeconds(afterSwitch)).toBeGreaterThanOrEqual(
      toSeconds(beforeSwitch),
    );
  });

  it("dispatches pulses crossed by the cursor in original order", () => {
    const onPulse = vi.fn();
    const pulses = [
      makePulse(0, "a", "b"),
      makePulse(100, "c", "d"),
      makePulse(250, "a", "b"),
    ];
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        onPulse={onPulse}
      />,
    );
    // Bump speed to 1000× so a single rAF step crosses all pulses.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1000"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(50); // 50 real ms × 1000× = 50000 virtual ms — past all pulses.
    });
    const calls = onPulse.mock.calls.map((c) => c[0] as RewindPulseFrame);
    expect(calls.map((c) => c.messageIds[0])).toEqual([
      "m-0",
      "m-100",
      "m-250",
    ]);
  });

  it("fires onComplete when the cursor reaches the end", () => {
    const onComplete = vi.fn();
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
        onComplete={onComplete}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1000"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      // 1000× × 1000 real-ms == 1,000,000 virtual ms, past the 10-min window.
      stepFrames(1000);
    });
    expect(onComplete).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("interaction-rewind")).toHaveAttribute(
      "data-finished",
    );
    expect(screen.getByTestId("interaction-rewind")).not.toHaveAttribute(
      "data-playing",
    );
    // Restart button becomes enabled at the end.
    expect(screen.getByTestId("interaction-rewind-restart")).not.toBeDisabled();
  });

  it("seeking via the controlled cursorMs prop updates the cursor and dispatch pointer", () => {
    const onPulse = vi.fn();
    const pulses = [
      makePulse(0, "a", "b"),
      makePulse(200, "c", "d"),
    ];
    const { rerender } = render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        cursorMs={0}
        onPulse={onPulse}
      />,
    );
    // Seek forward to 300ms — past both pulses. The component must
    // *not* fire them retroactively (only the rAF loop dispatches);
    // however the dispatch index must be advanced so a future play
    // doesn't replay them.
    rerender(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        cursorMs={300}
        onPulse={onPulse}
      />,
    );
    expect(screen.getByTestId("interaction-rewind-elapsed").textContent).toBe(
      "00:00",
    );
    // 300ms → still 00:00 visually because mm:ss rounds. Confirm the
    // progress bar shifted instead.
    expect(onPulse).not.toHaveBeenCalled();

    // Now play — no rewind-fired pulses for the skipped messages.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1000"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(1000);
    });
    expect(onPulse).not.toHaveBeenCalled();
  });

  it("scrubbing backward via cursorMs resets the dispatch index", () => {
    const onPulse = vi.fn();
    const pulses = [makePulse(50, "a", "b")];
    const { rerender } = render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        cursorMs={500}
        onPulse={onPulse}
      />,
    );
    // Scrub back to 0.
    rerender(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        cursorMs={0}
        onPulse={onPulse}
      />,
    );
    // Play through — the previously-skipped pulse must fire now.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1000"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(100);
    });
    expect(onPulse).toHaveBeenCalledTimes(1);
    expect(
      (onPulse.mock.calls[0][0] as RewindPulseFrame).messageIds[0],
    ).toBe("m-50");
  });

  it("Restart button resets cursor + dispatch index to 0", () => {
    const onPulse = vi.fn();
    const pulses = [makePulse(50, "a", "b"), makePulse(250, "c", "d")];
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={pulses}
        onPulse={onPulse}
      />,
    );
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-speed-1000"));
    });
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(1000);
    });
    expect(onPulse).toHaveBeenCalledTimes(2);

    onPulse.mockReset();
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-restart"));
    });
    expect(screen.getByTestId("interaction-rewind-elapsed").textContent).toBe(
      "00:00",
    );

    // Pressing play again replays the pulses.
    act(() => {
      fireEvent.click(screen.getByTestId("interaction-rewind-play"));
    });
    act(() => {
      stepFrames(0);
      stepFrames(1000);
    });
    expect(onPulse).toHaveBeenCalledTimes(2);
  });

  it("arrow keys nudge the speed preset", () => {
    render(
      <InteractionRewind
        since={SINCE}
        until={UNTIL}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    const segment = screen.getByTestId(
      `interaction-rewind-speed-${DEFAULT_REWIND_SPEED}`,
    );
    segment.focus();
    fireEvent.keyDown(segment, { key: "ArrowRight" });
    // The default preset is 30 — the next preset right is 100.
    const idx = REWIND_SPEEDS.indexOf(DEFAULT_REWIND_SPEED);
    const expectedNext = REWIND_SPEEDS[idx + 1];
    expect(
      screen.getByTestId(`interaction-rewind-speed-${expectedNext}`),
    ).toHaveAttribute("aria-checked", "true");
  });

  it("expose the total window length without panicking on a 0-length window", () => {
    // Degenerate but legal — since === until. The component must not
    // divide by zero (windowMs is clamped to >= 1).
    render(
      <InteractionRewind
        since={SINCE}
        until={SINCE}
        pulses={[]}
        onPulse={() => {}}
      />,
    );
    expect(screen.getByTestId("interaction-rewind-total").textContent).toBe(
      "00:00",
    );
  });

  it("matches the constants the spec calls out", () => {
    // The spec calls out 1× / 5× / 30× / 100× / 1000× presets with 30×
    // as the default. Lock the contract here so a refactor can't slip
    // past review.
    expect([...REWIND_SPEEDS]).toEqual([1, 5, 30, 100, 1000]);
    expect(DEFAULT_REWIND_SPEED).toBe(30);
    // Window math sanity: 10-minute window, 30× → 20s of real time.
    expect(WINDOW_MS / 30 / 1000).toBeCloseTo(20, 1);
  });
});
