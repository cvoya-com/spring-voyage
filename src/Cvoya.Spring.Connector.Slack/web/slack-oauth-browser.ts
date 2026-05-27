"use client";

// Browser-side helpers for the Slack OAuth popup. Issue #2837: the
// Slack OAuth callback now returns an HTML page that posts the
// outcome back to `window.opener` via `postMessage`, mirroring the
// GitHub connector. The portal listens for that message and reacts
// synchronously instead of polling the binding endpoint.
//
// The earlier poll-based helpers (`SLACK_OAUTH_POLL_*`,
// `pollForSlackBinding`) are gone — the postMessage handoff is the
// only path. The pop-up-closed safety net (the user clicked X on
// the popup before completing OAuth) lives in this file via a
// `setInterval(popup.closed)` watcher; no polling against the
// backend.

/** Type discriminator on the OAuth-callback handoff message. */
export const SLACK_OAUTH_CALLBACK_MESSAGE_TYPE = "sv:slack:oauth:done";

/**
 * Maximum number of milliseconds to wait for the popup's
 * postMessage before the portal surfaces a "took too long" notice.
 * Five minutes is plenty for the Slack consent screen plus any
 * "pick a workspace" prompt; less than that gets us into "user
 * opened another tab to check email" territory.
 */
export const SLACK_OAUTH_HANDOFF_TIMEOUT_MS = 5 * 60 * 1000;

/**
 * Builds the `clientState` payload posted to
 * `POST /api/v1/tenant/connectors/slack/oauth/authorize`. We carry
 * the portal origin so the backend callback's HTML can target
 * `postMessage` correctly — never `*`. Mirrors the GitHub helper's
 * shape so cross-window handoff validation can share code.
 */
export function buildSlackOAuthClientState(): string | null {
  if (typeof window === "undefined") return null;
  return JSON.stringify({ targetOrigin: window.location.origin });
}

/**
 * Parsed shape of the postMessage payload the Slack OAuth callback
 * page fires at the popup's opener. The `type` discriminator is
 * checked separately by {@link isSlackOAuthCallbackMessage} so the
 * panel can ignore everything else on the message bus.
 */
export type SlackOAuthCallbackMessage =
  | { type: typeof SLACK_OAUTH_CALLBACK_MESSAGE_TYPE; status: "success" }
  | {
      type: typeof SLACK_OAUTH_CALLBACK_MESSAGE_TYPE;
      status: "error";
      error: string;
      message: string;
    };

/**
 * Type-guard for the postMessage payload. Validates the discriminator
 * + the per-status fields. Returns `false` for anything else so the
 * panel's listener can drop foreign messages without recourse.
 */
export function isSlackOAuthCallbackMessage(
  value: unknown,
): value is SlackOAuthCallbackMessage {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  if (v.type !== SLACK_OAUTH_CALLBACK_MESSAGE_TYPE) return false;
  if (v.status === "success") return true;
  if (
    v.status === "error" &&
    typeof v.error === "string" &&
    typeof v.message === "string"
  ) {
    return true;
  }
  return false;
}

/**
 * Outcome of the postMessage handoff. `success` and `error` come
 * from the backend's HTML callback; `popup-closed`, `timed-out`,
 * and `aborted` are client-side observations the panel surfaces
 * as notices.
 */
export type SlackOAuthHandoffOutcome =
  | { kind: "success" }
  | { kind: "error"; error: string; message: string }
  | { kind: "popup-closed" }
  | { kind: "timed-out" }
  | { kind: "aborted" };

/**
 * Listens for the OAuth-callback postMessage from `popup`. Resolves
 * when one of:
 * <ul>
 *   <li>The callback HTML page posts a valid
 *       <code>sv:slack:oauth:done</code> message (success or error).</li>
 *   <li>The popup window closes without ever posting a message
 *       (user clicked X before completing OAuth, or the page never
 *       loaded).</li>
 *   <li>The deadline elapses (timeout fallback).</li>
 *   <li>The optional <code>signal</code> aborts (caller unmount).</li>
 * </ul>
 *
 * Origin validation is strict: only messages whose
 * <code>event.origin</code> matches <code>window.location.origin</code>
 * are honoured; everything else is silently dropped. The popup's
 * <code>event.source</code> must also match the supplied
 * <code>popup</code> handle so an unrelated tab cannot impersonate
 * the callback by posting to our top-level window.
 */
export async function awaitSlackOAuthHandoff(options: {
  popup: Window | null;
  signal?: AbortSignal;
  timeoutMs?: number;
  /** Test seam: clamp the popup-closed-watch interval for unit tests. */
  popupCheckIntervalMs?: number;
}): Promise<SlackOAuthHandoffOutcome> {
  const {
    popup,
    signal,
    timeoutMs = SLACK_OAUTH_HANDOFF_TIMEOUT_MS,
    popupCheckIntervalMs = 500,
  } = options;

  if (typeof window === "undefined") {
    // SSR path — should never run, but stay defensive: nothing to
    // listen to, surface as aborted so the caller doesn't hang.
    return { kind: "aborted" };
  }

  const expectedOrigin = window.location.origin;

  return new Promise<SlackOAuthHandoffOutcome>((resolve) => {
    let settled = false;
    const finish = (outcome: SlackOAuthHandoffOutcome) => {
      if (settled) return;
      settled = true;
      window.removeEventListener("message", onMessage);
      if (popupTimer !== null) {
        window.clearInterval(popupTimer);
      }
      window.clearTimeout(timeoutTimer);
      if (signal !== undefined) {
        signal.removeEventListener("abort", onAbort);
      }
      resolve(outcome);
    };

    const onMessage = (event: MessageEvent) => {
      // Strict origin check — drop messages from anywhere else on
      // the bus. This is the security boundary the brief calls out.
      if (event.origin !== expectedOrigin) return;
      // Validate the source is the popup we opened. `event.source`
      // is the WindowProxy the message came from; comparing it to
      // the popup handle blocks an unrelated tab impersonating the
      // callback. Skip the check when no popup handle was supplied
      // (jsdom tests / SSR fallback) — origin alone is the guard.
      if (popup !== null && event.source !== popup) return;
      if (!isSlackOAuthCallbackMessage(event.data)) return;

      const data = event.data;
      if (data.status === "success") {
        finish({ kind: "success" });
      } else {
        finish({ kind: "error", error: data.error, message: data.message });
      }
    };
    window.addEventListener("message", onMessage);

    // Popup-closed safety net. If the user clicks X on the OAuth
    // popup before the callback fires (network died, the user
    // cancelled by closing the tab, the embedded webview crashed),
    // we'd otherwise hang until the timeout. This watcher resolves
    // as soon as the popup is gone — the panel surfaces the
    // "install cancelled" notice. Skip the watcher when no popup
    // handle was supplied (jsdom tests / SSR fallback).
    let popupTimer: ReturnType<typeof setInterval> | null = null;
    if (popup !== null) {
      popupTimer = setInterval(() => {
        if (popup.closed) {
          finish({ kind: "popup-closed" });
        }
      }, popupCheckIntervalMs);
    }

    // Timeout fallback. If the popup is still alive at the deadline
    // (e.g. user is staring at the Slack consent screen for five
    // minutes), surface a timed-out outcome so the panel can offer
    // to retry.
    const timeoutTimer = setTimeout(() => {
      finish({ kind: "timed-out" });
    }, timeoutMs);

    // Abort signal — the caller (panel) fires this on unmount so
    // the listener doesn't outlive the React tree.
    const onAbort = () => {
      finish({ kind: "aborted" });
    };
    if (signal !== undefined) {
      if (signal.aborted) {
        finish({ kind: "aborted" });
        return;
      }
      signal.addEventListener("abort", onAbort);
    }
  });
}
