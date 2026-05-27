import { afterEach, describe, expect, it, vi } from "vitest";

import {
  buildSlackOAuthClientState,
  pollForSlackBinding,
} from "@connector-slack/slack-oauth-browser";

describe("buildSlackOAuthClientState", () => {
  // The portal posts its own origin as the OAuth `clientState` so a
  // future HTML-handoff callback can scope `postMessage` correctly
  // (matches the GitHub helper's contract).
  it("returns a JSON envelope carrying the current window origin", () => {
    const state = buildSlackOAuthClientState();
    expect(state).not.toBeNull();
    const parsed = JSON.parse(state!) as { targetOrigin?: string };
    expect(parsed.targetOrigin).toBe(window.location.origin);
  });
});

describe("pollForSlackBinding", () => {
  it("returns 'connected' once isBound resolves true", async () => {
    let calls = 0;
    const outcome = await pollForSlackBinding({
      isBound: () => {
        calls++;
        return Promise.resolve(calls >= 2);
      },
      popup: null,
      timeoutMs: 1000,
      intervalMs: 1,
    });
    expect(outcome).toBe("connected");
    expect(calls).toBeGreaterThanOrEqual(2);
  });

  // When the popup window closes before isBound returns true, the
  // poller does one final check (catches the race where the binding
  // landed just as the popup closed itself) and then surfaces
  // "popup-closed" so the parent can render the cancellation notice.
  it("returns 'popup-closed' when the popup closes without the binding appearing", async () => {
    const fakePopup = { closed: false } as unknown as Window;
    // After the first probe, flip the popup to closed so the next
    // iteration sees a dead window.
    let probe = 0;
    const outcome = await pollForSlackBinding({
      isBound: () => {
        probe++;
        if (probe === 1) {
          // mark popup closed for the next loop iteration
          (fakePopup as unknown as { closed: boolean }).closed = true;
        }
        return Promise.resolve(false);
      },
      popup: fakePopup,
      timeoutMs: 1000,
      intervalMs: 1,
    });
    expect(outcome).toBe("popup-closed");
  });

  // Transient errors during the bound check (e.g. brief 5xx from the
  // dispatcher / network blip) must not derail the poll — they're
  // absorbed and the loop continues until the popup closes or the
  // deadline elapses. The bound check needs to throw enough times to
  // exhaust the deadline.
  it("returns 'timed-out' when isBound never resolves true within the deadline", async () => {
    const outcome = await pollForSlackBinding({
      isBound: () => Promise.resolve(false),
      popup: null,
      timeoutMs: 5,
      intervalMs: 1,
    });
    expect(outcome).toBe("timed-out");
  });

  it("returns 'aborted' when the abort signal fires", async () => {
    const controller = new AbortController();
    const promise = pollForSlackBinding({
      isBound: () => Promise.resolve(false),
      popup: null,
      signal: controller.signal,
      timeoutMs: 1000,
      intervalMs: 1,
    });
    controller.abort();
    const outcome = await promise;
    expect(outcome).toBe("aborted");
  });

  it("absorbs errors from isBound and keeps polling", async () => {
    let calls = 0;
    const outcome = await pollForSlackBinding({
      isBound: () => {
        calls++;
        if (calls === 1) throw new Error("transient");
        if (calls === 2) return Promise.resolve(false);
        return Promise.resolve(true);
      },
      popup: null,
      timeoutMs: 1000,
      intervalMs: 1,
    });
    expect(outcome).toBe("connected");
    expect(calls).toBeGreaterThanOrEqual(3);
  });
});

// `pollForSlackBinding` calls `setTimeout` between probes — the test
// suite's intervalMs of 1 makes that effectively a microtask under
// jsdom, so the loop completes synchronously without `vi.useFakeTimers`.
afterEach(() => {
  vi.clearAllMocks();
});
