import { afterEach, describe, expect, it, vi } from "vitest";

import {
  awaitSlackOAuthHandoff,
  buildSlackOAuthClientState,
  isSlackOAuthCallbackMessage,
  SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
} from "@connector-slack/slack-oauth-browser";

describe("buildSlackOAuthClientState", () => {
  // The portal posts its own origin as the OAuth `clientState` so the
  // backend's HTML callback page can target postMessage to a concrete
  // origin (never `*`). Mirrors the GitHub helper's contract.
  it("returns a JSON envelope carrying the current window origin", () => {
    const state = buildSlackOAuthClientState();
    expect(state).not.toBeNull();
    const parsed = JSON.parse(state!) as { targetOrigin?: string };
    expect(parsed.targetOrigin).toBe(window.location.origin);
  });
});

describe("isSlackOAuthCallbackMessage", () => {
  it("accepts a well-formed success payload", () => {
    expect(
      isSlackOAuthCallbackMessage({
        type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
        status: "success",
      }),
    ).toBe(true);
  });

  it("accepts a well-formed error payload", () => {
    expect(
      isSlackOAuthCallbackMessage({
        type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
        status: "error",
        error: "SlackEnterpriseGridUnsupported",
        message: "Grid is not supported in v0.1.",
      }),
    ).toBe(true);
  });

  // Defensive: any payload whose discriminator is wrong, or whose
  // status / error / message fields don't line up, must be dropped.
  // The listener relies on this to ignore foreign messages on the
  // shared `window.message` bus.
  it.each([
    null,
    undefined,
    "string",
    42,
    {},
    { type: "other:type", status: "success" },
    { type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE, status: "maybe" },
    { type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE, status: "error" }, // missing fields
    {
      type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
      status: "error",
      error: 7,
      message: "msg",
    },
    {
      type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
      status: "error",
      error: "code",
      message: null,
    },
  ])("rejects malformed payload %s", (payload) => {
    expect(isSlackOAuthCallbackMessage(payload)).toBe(false);
  });
});

describe("awaitSlackOAuthHandoff", () => {
  // The popup posts a success payload via postMessage; the listener
  // resolves with { kind: "success" } and tears itself down (a
  // subsequent message must NOT re-trigger).
  it("resolves to success when the popup posts a success message", async () => {
    // jsdom's MessageEvent doesn't carry `source` unless we set it,
    // so the listener's popup-source check is skipped via the
    // `popup === null` branch — we still validate the origin guard.
    const promise = awaitSlackOAuthHandoff({
      popup: null,
      timeoutMs: 1000,
      popupCheckIntervalMs: 10,
    });

    window.dispatchEvent(
      new MessageEvent("message", {
        origin: window.location.origin,
        data: { type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE, status: "success" },
      }),
    );

    const outcome = await promise;
    expect(outcome).toEqual({ kind: "success" });
  });

  it("resolves to error with the carried code+message for an error payload", async () => {
    const promise = awaitSlackOAuthHandoff({
      popup: null,
      timeoutMs: 1000,
      popupCheckIntervalMs: 10,
    });

    window.dispatchEvent(
      new MessageEvent("message", {
        origin: window.location.origin,
        data: {
          type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE,
          status: "error",
          error: "SlackEnterpriseGridUnsupported",
          message: "Grid is not supported in v0.1.",
        },
      }),
    );

    const outcome = await promise;
    expect(outcome).toEqual({
      kind: "error",
      error: "SlackEnterpriseGridUnsupported",
      message: "Grid is not supported in v0.1.",
    });
  });

  // Strict-origin guard. A message from a different origin must be
  // silently dropped — never resolve the promise. We confirm by
  // dispatching a wrong-origin message, then a correct-origin one;
  // the helper should only honour the second.
  it("ignores messages from the wrong origin", async () => {
    const promise = awaitSlackOAuthHandoff({
      popup: null,
      timeoutMs: 1000,
      popupCheckIntervalMs: 10,
    });

    window.dispatchEvent(
      new MessageEvent("message", {
        origin: "https://evil.example.test",
        data: { type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE, status: "success" },
      }),
    );

    // Still no resolution — give the listener a microtask to confirm.
    let settled = false;
    void promise.then(() => {
      settled = true;
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(settled).toBe(false);

    // Now the real one — same payload, correct origin — must resolve.
    window.dispatchEvent(
      new MessageEvent("message", {
        origin: window.location.origin,
        data: { type: SLACK_OAUTH_CALLBACK_MESSAGE_TYPE, status: "success" },
      }),
    );
    const outcome = await promise;
    expect(outcome).toEqual({ kind: "success" });
  });

  // Foreign-shape messages on the same origin must also be dropped
  // (e.g. an unrelated browser extension chatting on the bus).
  it("ignores messages of the wrong type", async () => {
    const promise = awaitSlackOAuthHandoff({
      popup: null,
      timeoutMs: 50,
      popupCheckIntervalMs: 10,
    });

    window.dispatchEvent(
      new MessageEvent("message", {
        origin: window.location.origin,
        data: { type: "some:other:event", status: "success" },
      }),
    );

    // No matching payload — the timeout safety net resolves the
    // promise instead.
    const outcome = await promise;
    expect(outcome).toEqual({ kind: "timed-out" });
  });

  // Popup-closed safety net: when the user clicks X before the
  // callback fires, the watcher trips and resolves with
  // `popup-closed`. We simulate the closed flag flipping after the
  // first tick.
  it("resolves to popup-closed when the popup window closes without a message", async () => {
    const popup = { closed: false } as unknown as Window;
    const promise = awaitSlackOAuthHandoff({
      popup,
      timeoutMs: 1000,
      popupCheckIntervalMs: 5,
    });

    // Flip the closed flag on the next tick — the watcher will pick
    // it up on its 5ms interval.
    setTimeout(() => {
      (popup as unknown as { closed: boolean }).closed = true;
    }, 10);

    const outcome = await promise;
    expect(outcome).toEqual({ kind: "popup-closed" });
  });

  it("resolves to timed-out when no message arrives within the deadline", async () => {
    const outcome = await awaitSlackOAuthHandoff({
      popup: null,
      timeoutMs: 10,
      popupCheckIntervalMs: 5,
    });
    expect(outcome).toEqual({ kind: "timed-out" });
  });

  it("resolves to aborted when the abort signal fires", async () => {
    const controller = new AbortController();
    const promise = awaitSlackOAuthHandoff({
      popup: null,
      signal: controller.signal,
      timeoutMs: 1000,
      popupCheckIntervalMs: 5,
    });
    controller.abort();
    const outcome = await promise;
    expect(outcome).toEqual({ kind: "aborted" });
  });

  it("resolves to aborted immediately when the abort signal is already fired", async () => {
    const controller = new AbortController();
    controller.abort();
    const outcome = await awaitSlackOAuthHandoff({
      popup: null,
      signal: controller.signal,
      timeoutMs: 1000,
      popupCheckIntervalMs: 5,
    });
    expect(outcome).toEqual({ kind: "aborted" });
  });
});

afterEach(() => {
  vi.clearAllMocks();
});
