// URL-state helpers for /activity/interactions.
//
// Mirrors the pattern used in /conversations: an 8-field state object is
// serialised into `URLSearchParams` so the back / forward button reflows
// every filter. The view-mode + live-mode booleans live alongside the
// data filters so a deep-link captures the operator's current canvas.

import type { InteractionsFilters } from "@/lib/api/types";

export type InteractionsViewMode = "graph" | "matrix" | "both";

/**
 * Default snapshot window — last 10 minutes. The backend would default to
 * the same window if `since` / `until` were omitted, but the portal
 * materialises both bounds so the timeline brush can move within a
 * known frame and so a deep link is reproducible.
 */
export const DEFAULT_WINDOW_MINUTES = 10;
export const DEFAULT_NEIGHBOURS = 2 as const;

/**
 * Timeline-bucket presets — ordered fine → coarse so a UI control can
 * iterate without re-sorting. The wire vocabulary mirrors the backend
 * {@link InteractionsBucket} enum: `15s` / `30s` / `1m` / `5m` / `10m`
 * / `15m` / `30m` / `1h` / `1d`. See `ParseBucket` in
 * `InteractionsEndpoints.cs` for the exact set.
 */
export const BUCKET_PRESETS = [
  "15s",
  "30s",
  "1m",
  "5m",
  "10m",
  "15m",
  "30m",
  "1h",
  "1d",
] as const;
export type InteractionsBucket = (typeof BUCKET_PRESETS)[number];
export const DEFAULT_BUCKET: InteractionsBucket = "15s";
export const DEFAULT_VIEW: InteractionsViewMode = "both";

/**
 * Size of each bucket preset in milliseconds. Used to align client-side
 * timestamps to bucket boundaries (e.g. the rewind path builds its own
 * timeline from history pulses since the snapshot's timeline doesn't
 * refetch while rewind is active).
 */
export const BUCKET_SIZE_MS: Record<InteractionsBucket, number> = {
  "15s": 15 * 1000,
  "30s": 30 * 1000,
  "1m": 60 * 1000,
  "5m": 5 * 60 * 1000,
  "10m": 10 * 60 * 1000,
  "15m": 15 * 60 * 1000,
  "30m": 30 * 60 * 1000,
  "1h": 60 * 60 * 1000,
  "1d": 24 * 60 * 60 * 1000,
};

export interface InteractionsUrlState {
  unit: string;
  participant: string;
  /** ISO 8601 instant. Empty string when defaulted. */
  since: string;
  /** ISO 8601 instant. Empty string when defaulted. */
  until: string;
  neighbours: 0 | 1 | 2;
  bucket: InteractionsBucket;
  view: InteractionsViewMode;
  live: boolean;
  /**
   * Rewind mode (#2872). Mutually exclusive with `live`. When `true`, the
   * page swaps the SSE subscription for a history fetch and shows a
   * transport bar that drives a virtual cursor through the window.
   *
   * Mutual exclusion is enforced by the toggle helpers in this module:
   * {@link toggleLive} forces `rewind` off; {@link toggleRewind} forces
   * `live` off. Hand-rolling a state object with both `live: true` and
   * `rewind: true` is a programming error — the writer normalises by
   * dropping `rewind` (live wins) but neither caller in the page does
   * this.
   */
  rewind: boolean;
}

export const EMPTY_URL_STATE: InteractionsUrlState = {
  unit: "",
  participant: "",
  since: "",
  until: "",
  neighbours: DEFAULT_NEIGHBOURS,
  bucket: DEFAULT_BUCKET,
  view: DEFAULT_VIEW,
  live: false,
  rewind: false,
};

export function parseNeighbours(raw: string | null): 0 | 1 | 2 {
  if (raw === "0") return 0;
  if (raw === "1") return 1;
  return DEFAULT_NEIGHBOURS;
}

export function parseBucket(raw: string | null): InteractionsBucket {
  if (raw && (BUCKET_PRESETS as readonly string[]).includes(raw)) {
    return raw as InteractionsBucket;
  }
  return DEFAULT_BUCKET;
}

export function parseView(raw: string | null): InteractionsViewMode {
  return raw === "graph" || raw === "matrix" ? raw : DEFAULT_VIEW;
}

export function readUrlState(params: URLSearchParams): InteractionsUrlState {
  const live = params.get("live") === "true";
  // Live wins on the rare case both arrive in the URL (hand-edited deep
  // link). The toggle helpers below enforce mutual exclusion on every
  // operator-driven transition so this branch is the only place we have
  // to defend against a malformed input.
  const rewind = !live && params.get("rewind") === "true";
  return {
    unit: params.get("unit") ?? "",
    participant: params.get("participant") ?? "",
    since: params.get("since") ?? "",
    until: params.get("until") ?? "",
    neighbours: parseNeighbours(params.get("neighbours")),
    bucket: parseBucket(params.get("bucket")),
    view: parseView(params.get("view")),
    live,
    rewind,
  };
}

export function writeUrlState(state: InteractionsUrlState): string {
  const out = new URLSearchParams();
  if (state.unit) out.set("unit", state.unit);
  if (state.participant) out.set("participant", state.participant);
  if (state.since) out.set("since", state.since);
  if (state.until) out.set("until", state.until);
  if (state.neighbours !== DEFAULT_NEIGHBOURS) {
    out.set("neighbours", String(state.neighbours));
  }
  if (state.bucket !== DEFAULT_BUCKET) out.set("bucket", state.bucket);
  if (state.view !== DEFAULT_VIEW) out.set("view", state.view);
  // Live wins if both are accidentally set; we never emit both flags.
  if (state.live) {
    out.set("live", "true");
  } else if (state.rewind) {
    out.set("rewind", "true");
  }
  return out.toString();
}

/**
 * Flip live mode on/off — turning live on forces rewind off (mutual
 * exclusion). Use this from the filters / page reducer so the two flags
 * can never both be true at once.
 */
export function toggleLive(
  state: InteractionsUrlState,
  next: boolean,
): InteractionsUrlState {
  return next
    ? { ...state, live: true, rewind: false }
    : { ...state, live: false };
}

/**
 * Flip rewind mode on/off — turning rewind on forces live off (mutual
 * exclusion). Same contract as {@link toggleLive}; pick whichever helper
 * matches the verb the operator just pressed.
 */
export function toggleRewind(
  state: InteractionsUrlState,
  next: boolean,
): InteractionsUrlState {
  return next
    ? { ...state, rewind: true, live: false }
    : { ...state, rewind: false };
}

/**
 * Resolve the snapshot window for the API call. When the URL omits
 * `since` / `until`, materialise the last-10-minutes default so the
 * timeline brush has a concrete frame to move within and so a refetch
 * doesn't drift between requests.
 */
export function resolveWindow(state: InteractionsUrlState): {
  since: string;
  until: string;
} {
  const now = new Date();
  const until = state.until || now.toISOString();
  const sinceDefault = new Date(
    now.getTime() - DEFAULT_WINDOW_MINUTES * 60 * 1000,
  ).toISOString();
  const since = state.since || sinceDefault;
  return { since, until };
}

/**
 * Translate URL state into the shape `useInteractionsSnapshot` consumes.
 */
export function toSnapshotFilters(
  state: InteractionsUrlState,
): InteractionsFilters {
  const { since, until } = resolveWindow(state);
  return {
    since,
    until,
    unit: state.unit || undefined,
    participant: state.participant || undefined,
    neighbours: state.neighbours,
    bucket: state.bucket,
  };
}
