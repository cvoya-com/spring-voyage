/**
 * Recently-used image reference history (#622 / #968).
 *
 * Persists up to `MAX_IMAGE_HISTORY` distinct image reference strings in
 * `localStorage` so the unit-creation wizard and agent-execution surfaces
 * can offer autocomplete suggestions without a backend round-trip.
 *
 * Design choices:
 *   * localStorage (not sessionStorage) — image references are useful across
 *     sessions. They contain no secrets, just public container image tags.
 *   * FIFO eviction with dedup on insert: a reference already in the list
 *     moves to the front rather than accumulating duplicates.
 *   * Quota / SecurityError failures are swallowed — loss of history is
 *     graceful degradation; the operator can still type freely.
 *   * SSR-safe: every call guards `typeof window`. The module is imported
 *     by `"use client"` components that may be server-rendered; the
 *     guards prevent `ReferenceError: localStorage is not defined`.
 */

import { useSyncExternalStore } from "react";

const STORAGE_KEY = "spring.image-history.v1";
export const MAX_IMAGE_HISTORY = 20;

/**
 * Built-in agent-image references that ship with the platform.
 *
 * The wizard surfaces these as suggestions even on first use — before the
 * user has ever submitted an image — so the picker is never empty out of
 * the box. The proper long-term solution is a Web API endpoint + CLI
 * command for listing available agent runtime images (#1433); this
 * hardcoded seed is the v0.1 expedient.
 */
export const BUILTIN_AGENT_IMAGES: readonly string[] = [
  "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
  "ghcr.io/cvoya-com/spring-voyage-agent:latest",
  "ghcr.io/cvoya-com/spring-voyage-agent-base:latest",
  "ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest",
  "ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest",
];

function readStorage(): string[] {
  if (typeof window === "undefined") return [];
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (v): v is string => typeof v === "string" && v.trim().length > 0,
    );
  } catch {
    return [];
  }
}

/**
 * Read the suggestion list the wizard renders into its `<datalist>`.
 *
 * Order: user-entered history first (most-recent first), then any built-in
 * agent image the user hasn't already explicitly recorded. This keeps the
 * list useful even on first run (built-ins are always there) without
 * pushing built-ins above an image the operator actually used.
 */
export function loadImageHistory(): string[] {
  const merged: string[] = [];
  const seen = new Set<string>();
  for (const ref of readStorage()) {
    if (!seen.has(ref)) {
      merged.push(ref);
      seen.add(ref);
    }
  }
  for (const ref of BUILTIN_AGENT_IMAGES) {
    if (!seen.has(ref)) {
      merged.push(ref);
      seen.add(ref);
    }
  }
  return merged;
}

/**
 * Add `reference` to the front of the persisted history list, deduplicating
 * and capping at `MAX_IMAGE_HISTORY`. Silently ignores blank strings, the
 * built-in seeds (no need to "remember" something that's always offered),
 * and storage errors.
 */
export function recordImageReference(reference: string): void {
  if (typeof window === "undefined") return;
  const trimmed = reference.trim();
  if (!trimmed) return;
  if (BUILTIN_AGENT_IMAGES.includes(trimmed)) return;
  try {
    const existing = readStorage();
    const deduped = existing.filter((r) => r !== trimmed);
    const next = [trimmed, ...deduped].slice(0, MAX_IMAGE_HISTORY);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    // `setItem` does not fire `storage` events in the originating tab, so
    // dispatch one manually to nudge any `useImageHistory()` subscriber
    // to re-read. Cross-tab updates already arrive via the native event.
    window.dispatchEvent(new StorageEvent("storage", { key: STORAGE_KEY }));
  } catch {
    // Quota exceeded or SecurityError — best-effort.
  }
}

// External-store adapter around `localStorage` for the image-history
// suggestion list. Reading `localStorage` inside a render-time
// `useState` initializer diverges between SSR (no storage → built-in
// seeds only) and the client's first/hydration render (stored history
// included), which trips React's hydration-mismatch error #418.
//
// `useSyncExternalStore` solves this the same way `lib/theme.tsx`
// does: the stable `serverSnapshot` is used for both the SSR render
// and the client's hydration render, then the live snapshot is
// adopted post-mount — so server and client agree on the first paint.
// The `subscribe` function listens for cross-tab `storage` events so a
// new reference recorded in another tab is picked up immediately.
function subscribeToStorage(onChange: () => void): () => void {
  if (typeof window === "undefined") return () => {};
  window.addEventListener("storage", onChange);
  return () => window.removeEventListener("storage", onChange);
}

// Stable server snapshot: the built-in seeds only, never the persisted
// history. Cached so `useSyncExternalStore` sees a referentially stable
// value and does not loop.
const SERVER_IMAGE_HISTORY: readonly string[] = [...BUILTIN_AGENT_IMAGES];

function getServerImageHistory(): string[] {
  return SERVER_IMAGE_HISTORY as string[];
}

// `useSyncExternalStore` compares snapshots with `Object.is` and re-renders
// in a loop if the client snapshot returns a fresh array each call. Cache
// the last result and only hand back a new array when the storage contents
// actually changed (the `subscribe` callback fires on `storage` events).
let cachedImageHistory: string[] = [];
let cachedImageHistoryKey: string | null = null;

function getClientImageHistory(): string[] {
  const next = loadImageHistory();
  const key = JSON.stringify(next);
  if (key !== cachedImageHistoryKey) {
    cachedImageHistory = next;
    cachedImageHistoryKey = key;
  }
  return cachedImageHistory;
}

/**
 * Hydration-safe hook for the image-reference suggestion list.
 *
 * Returns the built-in seeds during SSR and the client's hydration
 * render, then the full persisted-history-plus-seeds list once mounted.
 * Use this instead of `useState(() => loadImageHistory())` in any
 * component that may be server-rendered.
 */
export function useImageHistory(): string[] {
  return useSyncExternalStore(
    subscribeToStorage,
    getClientImageHistory,
    getServerImageHistory,
  );
}
