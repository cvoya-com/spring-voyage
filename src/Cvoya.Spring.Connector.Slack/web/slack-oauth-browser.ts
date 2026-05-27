"use client";

// Browser-side helpers for the Slack OAuth popup. The Slack callback
// endpoint (`GET /api/v1/tenant/connectors/slack/oauth/callback`)
// returns JSON in v0.1, so the popup does NOT post a structured message
// back to the opener the way the GitHub callback does. Instead the
// parent surface polls `GET /api/v1/tenant/connectors/slack/binding`
// until the binding shows up — a binding row exists iff the OAuth
// callback persisted it successfully. A follow-up issue tracks teaching
// the Slack callback to render an HTML postMessage handoff so the
// parent can react synchronously without polling.

/**
 * Maximum number of milliseconds to wait for the popup to complete the
 * OAuth round-trip before the parent gives up and surfaces "Install
 * cancelled or timed out". Five minutes is plenty for the typical Slack
 * consent screen plus any "pick a workspace" prompt; less than that
 * gets us into "user opened another tab to check email" territory.
 */
export const SLACK_OAUTH_POLL_TIMEOUT_MS = 5 * 60 * 1000;

/** Poll interval — keep tight so the bound state appears promptly. */
export const SLACK_OAUTH_POLL_INTERVAL_MS = 2000;

/**
 * Builds the `clientState` payload posted to
 * `POST /api/v1/tenant/connectors/slack/oauth/authorize`. Today we
 * carry the portal origin so a future HTML-handoff callback (see the
 * file header) can target `postMessage` correctly without trusting the
 * popup-side script. The connector's `SlackOAuthOptions.ClientState`
 * already allows arbitrary JSON; we mirror the GitHub helper's shape
 * so a future cross-window handoff can share validation code.
 */
export function buildSlackOAuthClientState(): string | null {
  if (typeof window === "undefined") return null;
  return JSON.stringify({ targetOrigin: window.location.origin });
}

/**
 * Polls the supplied checker until it returns `true`, the popup is
 * detected as closed, or the timeout elapses. Resolves with the reason
 * the loop stopped — call sites translate each outcome into the
 * appropriate UI state ("connected", "cancelled", or "timed out").
 *
 * `signal` lets callers tear the poller down on unmount.
 */
export type SlackOAuthPollOutcome =
  | "connected"
  | "popup-closed"
  | "timed-out"
  | "aborted";

export async function pollForSlackBinding(options: {
  /** Returns `true` once `GET /binding` resolves to a bound state. */
  isBound: () => Promise<boolean>;
  /** The popup window; closed by the caller when this resolves. */
  popup: Window | null;
  /** Optional teardown signal — flips to "aborted" when fired. */
  signal?: AbortSignal;
  /** Test seam: clamp the timeout / interval for unit tests. */
  timeoutMs?: number;
  intervalMs?: number;
}): Promise<SlackOAuthPollOutcome> {
  const {
    isBound,
    popup,
    signal,
    timeoutMs = SLACK_OAUTH_POLL_TIMEOUT_MS,
    intervalMs = SLACK_OAUTH_POLL_INTERVAL_MS,
  } = options;

  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (signal?.aborted) return "aborted";
    try {
      if (await isBound()) return "connected";
    } catch {
      // Transient network/auth errors are absorbed — keep polling. The
      // caller surfaces a fatal error via the regular `loadError` path.
    }
    if (popup !== null && popup.closed) {
      // One last check before declaring cancellation — the binding
      // might have landed in the window between the previous probe
      // and the popup closing itself after a fast OAuth round-trip.
      try {
        if (await isBound()) return "connected";
      } catch {
        // Same absorbing behaviour as above.
      }
      return "popup-closed";
    }
    await new Promise<void>((resolve) => setTimeout(resolve, intervalMs));
  }

  return "timed-out";
}
